using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using ClusterProtocol;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// T-0399 item 3: the no-scrub gap. A global save replica is verified once, when it is installed; the registry learns
/// of holders only through POST /registry/global-saves/holders and never forgets one. A copy that tears (an unfsynced
/// write cut by a power loss, replica.globalFsync=false) or rots afterwards therefore kept counting toward the two
/// holders, and kept being offered as a source, while nobody could read it. This scrub re-reads one held global copy
/// per <c>replica.scrubSeconds</c> on the replication worker's idle path, and a copy that fails is set aside as
/// <c>&lt;name&gt;.corrupt</c> (kept as evidence, never deleted) and withdrawn
/// (POST /registry/global-saves/holders?withdraw=1), so the registry hands out a fresh copy from another holder.
/// </summary>
internal sealed partial class PartitionPersistence
{
	internal readonly record struct ScrubTarget(uint Generation, uint Sequence, long Size, string Checksum, string Path);

	// The pass: the held copies listed at its start, walked round-robin. A copy registered later waits for the next pass
	// (it was verified at install). Replication worker thread only.
	private ScrubTarget[] m_scrubTargets = Array.Empty<ScrubTarget>();
	private int m_scrubCursor;
	private DateTime m_scrubNextUtc, m_scrubListAfterUtc;
	private bool m_scrubber;
	// One buffer for every file, grown to the largest: a ~290 KB global read with File.ReadAllBytes is a large-object-heap
	// array per check.
	private byte[] m_scrubBuffer;
	private DateTime m_scrubSummaryUtc = DateTime.UtcNow;
	private int m_scrubFiles, m_scrubOk, m_scrubCorrupt;
	private double m_scrubMs;

	// A pass's list is taken at most this often: a node with nothing to scrub (a store-mode node that is not its host's
	// scrubber, an empty catalogue) would otherwise fetch the whole global catalogue every few seconds.
	private static readonly TimeSpan ScrubListInterval = TimeSpan.FromMinutes(10);

	/// <summary>Called from the replication loop when no replica work was handed out. Never throws.</summary>
	private void ScrubGlobals(HttpClient client)
	{
		int every = ClusterConfig.GetInt("replica.scrubSeconds");
		if (every <= 0 || m_saveRoot == null) return;
		DateTime now = DateTime.UtcNow;
		if (now - m_scrubSummaryUtc >= TimeSpan.FromHours(1))
		{
			MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] global_scrub pass files={0} ok={1} corrupt={2} msPerFile={3:F2} targets={4} scrubber={5}",
				m_scrubFiles, m_scrubOk, m_scrubCorrupt, m_scrubFiles == 0 ? 0 : m_scrubMs / m_scrubFiles, m_scrubTargets.Length,
				m_scrubber ? "true" : "false"));
			m_scrubSummaryUtc = now;
			m_scrubFiles = m_scrubOk = m_scrubCorrupt = 0;
			m_scrubMs = 0;
		}
		if (now < m_scrubNextUtc) return;
		m_scrubNextUtc = now.AddSeconds(every);
		try
		{
			if (m_scrubCursor >= m_scrubTargets.Length)
			{
				if (now < m_scrubListAfterUtc) return;
				m_scrubListAfterUtc = now + ScrubListInterval;
				m_scrubTargets = ListScrubTargets(client);
				m_scrubCursor = 0;
				if (m_scrubTargets.Length == 0) return;
			}
			ScrubTarget target = m_scrubTargets[m_scrubCursor++];
			Stopwatch watch = Stopwatch.StartNew();
			bool? intact = ScrubOne(target, m_node, m_nodeEpoch, ref m_scrubBuffer, withdrawPath =>
			{
				using JsonDocument answer = Post(client, withdrawPath);
				return answer.RootElement.TryGetProperty("withdrawn", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
			}, out string line);
			m_scrubMs += watch.Elapsed.TotalMilliseconds;
			m_scrubFiles++;
			if (intact == true) m_scrubOk++;
			else if (intact == false) m_scrubCorrupt++;
			if (line != null) MyLog.Default?.WriteLineAndConsole(line);
		}
		catch (Exception exception)
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] global_scrub unavailable: " + SecretRedactor.Redact(exception.Message));
		}
	}

	/// <summary>
	/// T-0399: one scrub check, with the registry call behind <paramref name="withdraw"/> (the request path in, the
	/// registry's "withdrawn" out; it may throw) so the self-test runs the whole chain without a network. true = intact
	/// (no line); false = set aside and withdrawn; null = unreadable for now, logged and left as it is.
	/// </summary>
	internal static bool? ScrubOne(ScrubTarget target, string node, long epoch, ref byte[] buffer, Func<string, bool> withdraw, out string line)
	{
		line = null;
		bool? intact = CheckGlobalCopy(target.Path, target.Size, target.Checksum, ref buffer, out string reason);
		if (intact == true) return true;
		if (intact == null)
		{
			// Not a verdict on the copy (the read itself failed): no rename, no withdraw; the next pass reads it again.
			line = "[ClusterPlugin] global_scrub unreadable generation=" + target.Generation + " sequence=" + target.Sequence
				+ " path=" + target.Path + " reason=" + reason;
			return null;
		}
		string kept = QuarantineGlobalCopy(target.Path);
		bool withdrawn = false;
		try { withdrawn = withdraw(GlobalWithdrawPath(target.Generation, target.Sequence, node, epoch, target.Path)); }
		catch (Exception exception)
		{
			// The file is set aside already: the next pass finds it missing and withdraws it again.
			reason += "; withdraw failed: " + SecretRedactor.Redact(exception.Message);
		}
		line = "[ClusterPlugin] global_scrub corrupt generation=" + target.Generation + " sequence=" + target.Sequence
			+ " path=" + target.Path + " reason=" + reason + " kept=" + (kept ?? "none") + " withdrawn=" + (withdrawn ? "true" : "false");
		return false;
	}

	/// <summary>
	/// The global copies this node answers for: those the catalogue holds by this node at its own GLOBAL path, or - in
	/// host-store mode - by its host at the host store's path, and then only on the host's scrubber
	/// (<see cref="IsHostScrubber"/>). Only a location equal to the path this node computes itself is ever read or renamed.
	/// </summary>
	private ScrubTarget[] ListScrubTargets(HttpClient client)
	{
		string holder = m_node;
		if (m_hostStore)
		{
			using JsonDocument nodes = Get(client, "/registry/nodes");
			m_scrubber = IsHostScrubber(nodes.RootElement, m_node, ClusterHttp.NodeHost);
			if (!m_scrubber) return Array.Empty<ScrubTarget>();
			holder = HostSaveStore.Holder(ClusterHttp.NodeHost);
		}
		else m_scrubber = true;
		using JsonDocument catalogue = Get(client, "/registry/global-saves");
		return ScrubTargetsOf(catalogue.RootElement, holder, (generation, sequence) => HostSaveStore.GlobalSave(m_saveRoot, generation, sequence));
	}

	/// <summary>
	/// T-0399: one node per host scrubs the host store - the lowest id (ordinal) among the host's Active regular nodes in
	/// store mode, so every node of the host picks the same one from the same node list. While the list changes (a node
	/// starts or stops) two nodes may both scrub for a pass: the rename tolerates a file another node set aside and the
	/// withdraw is idempotent at the registry.
	/// </summary>
	internal static bool IsHostScrubber(JsonElement nodes, string node, string host)
	{
		string lowest = null;
		foreach (JsonElement row in nodes.EnumerateArray())
		{
			// NodeState.Active = 0, NodeRole.Regular = 0 (the registry serialises its enums as numbers).
			if (!row.TryGetProperty("Node", out JsonElement id) || id.ValueKind != JsonValueKind.String
				|| !row.TryGetProperty("State", out JsonElement state) || state.ValueKind != JsonValueKind.Number || state.GetInt32() != 0
				|| row.TryGetProperty("Role", out JsonElement role) && role.ValueKind == JsonValueKind.Number && role.GetInt32() != 0
				|| !row.TryGetProperty("HostStore", out JsonElement store) || store.ValueKind != JsonValueKind.True
				|| !row.TryGetProperty("Host", out JsonElement rowHost) || rowHost.ValueKind != JsonValueKind.String
				|| !string.Equals(rowHost.GetString(), host, StringComparison.Ordinal)) continue;
			string name = id.GetString();
			if (lowest == null || string.CompareOrdinal(name, lowest) < 0) lowest = name;
		}
		return lowest == node;
	}

	/// <summary>The catalogue's (GET /registry/global-saves) copies held by <paramref name="holder"/> at exactly
	/// <paramref name="pathOf"/>(generation, sequence), oldest first. A global pending deletion is left alone.</summary>
	internal static ScrubTarget[] ScrubTargetsOf(JsonElement catalogue, string holder, Func<long, long, string> pathOf)
	{
		List<ScrubTarget> targets = new List<ScrubTarget>();
		foreach (JsonElement global in catalogue.EnumerateArray())
		{
			if (global.TryGetProperty("PendingDelete", out JsonElement pending) && pending.ValueKind == JsonValueKind.True) continue;
			if (!global.TryGetProperty("LocationHolders", out JsonElement holders) || holders.ValueKind != JsonValueKind.Object) continue;
			JsonElement id = global.GetProperty("SaveId");
			long generation = id.GetProperty("Generation").GetInt64(), sequence = id.GetProperty("Sequence").GetInt64();
			if (generation < 0 || generation > uint.MaxValue || sequence < 0 || sequence > uint.MaxValue) continue;
			string path = pathOf(generation, sequence);
			foreach (JsonProperty location in holders.EnumerateObject())
			{
				if (location.Name != path || location.Value.ValueKind != JsonValueKind.String || location.Value.GetString() != holder) continue;
				targets.Add(new ScrubTarget((uint)generation, (uint)sequence, global.GetProperty("SizeBytes").GetInt64(),
					global.GetProperty("Checksum").GetString(), path));
				break;
			}
		}
		return targets.OrderBy(target => target.Generation).ThenBy(target => target.Sequence).ToArray();
	}

	/// <summary>
	/// T-0399: one held global copy against its catalogue row, by the rule every reader applies (DownloadGlobal,
	/// WriteVerifiedGlobalSave, WorldAuthorityCheckpointPatch.VerifiedGlobalCopy): the catalogued size, the container's
	/// header and body checksums, and the body checksum equal to the catalogued one. true = intact; false = the copy is
	/// bad (missing, torn, rotted, another save) with <paramref name="reason"/>; null = the read failed for another
	/// reason (an I/O error), which says nothing certain about the copy. Reads into <paramref name="buffer"/>, grown as needed.
	/// </summary>
	internal static bool? CheckGlobalCopy(string path, long size, string checksum, ref byte[] buffer, out string reason)
	{
		reason = null;
		if (size < 0 || size > int.MaxValue) { reason = "catalogued size " + size + " is out of range"; return null; }
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
			long length = stream.Length;
			if (length != size) { reason = "size " + length + " != catalogued " + size; return false; }
			if (buffer == null || buffer.Length < length) buffer = GC.AllocateUninitializedArray<byte>((int)length);
			stream.ReadExactly(buffer, 0, (int)length);
			string actual = PartitionSaveFile.ReadHeader(buffer, (int)length).BodyChecksum.ToString("X16", CultureInfo.InvariantCulture);
			if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase)) { reason = "checksum " + actual + " != catalogued " + checksum; return false; }
			return true;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { reason = "missing"; return false; }
		catch (InvalidDataException exception) { reason = exception.Message; return false; }
		catch (EndOfStreamException) { reason = "shorter than its length while read"; return false; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { reason = exception.Message; return null; }
	}

	/// <summary>T-0399: sets a bad copy aside as <c>&lt;path&gt;.corrupt</c> (<c>.corrupt.1</c>, <c>.2</c>, ... when taken), never
	/// deleting it. Returns the new name, or null when the file is not there (missing, or set aside by another scrubber).</summary>
	internal static string QuarantineGlobalCopy(string path)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			string target = path + ".corrupt" + (attempt == 0 ? string.Empty : "." + attempt.ToString(CultureInfo.InvariantCulture));
			if (File.Exists(target)) continue;
			try
			{
				File.Move(path, target, overwrite: false);
				return target;
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
			catch (IOException) when (File.Exists(target)) { }   // the name was taken meanwhile: the next one
		}
		return null;
	}

	/// <summary>T-0399: the withdraw request - the register call's parameters (generation, sequence, node, epoch,
	/// location); the registry maps node + location to the holder exactly as it did when the copy was registered.</summary>
	internal static string GlobalWithdrawPath(uint generation, uint sequence, string node, long epoch, string location) =>
		"/registry/global-saves/holders?withdraw=1&generation=" + generation.ToString(CultureInfo.InvariantCulture)
		+ "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture)
		+ "&node=" + Uri.EscapeDataString(node) + "&epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
		+ "&location=" + Uri.EscapeDataString(location);
}
