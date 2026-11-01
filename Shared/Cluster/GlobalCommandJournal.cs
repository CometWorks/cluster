using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// #1318: the World Authority's write-ahead journal of journaled commands (banking, factions, identities, player bindings,
/// time of day) in the Gateway registry, between its global saves. These commands are answered at once and the next
/// global save that holds their effect can be a minute away, so a WA killed in that window came back with the older
/// balances while the asking node kept the purchase, refund or payout. Waiting for a save like the durable commands
/// (#1270) costs 0.6-8 s per answer on a node's game thread; an append costs one round trip.
///
/// The answer is held until the registry acknowledged the append (GlobalStateChannel). A registry that does not answer
/// costs at most <see cref="FailOpen"/>: the answer is then sent anyway (counted, logged), because the node gives up
/// after 5 s and an effect the WA applied but the node never heard of is worse than the rare WA death the journal
/// covers. A refused append (409: this WA is fenced) is never answered: its replacement does not know it.
///
/// A replacement restores the newest recoverable global save, then <see cref="LoadForReplay"/> reads the entries after
/// that save's ledger order, and <see cref="ReplayRestored"/> runs them on its game thread before it serves any command.
/// CLUSTER_GLOBAL_JOURNAL=0 turns both off (the answers are sent at once again, as before #1318).
/// </summary>
internal static class GlobalCommandJournal
{
	internal static readonly TimeSpan FailOpen = TimeSpan.FromMilliseconds(1500);
	private const int BatchEntries = 256;
	private const int KeptEntries = 4096;
	private static readonly bool Enabled = Environment.GetEnvironmentVariable("CLUSTER_GLOBAL_JOURNAL") != "0";
	private static readonly object Sync = new object();
	private static readonly SortedDictionary<long, Entry> Entries = new SortedDictionary<long, Entry>();
	private static Thread m_writer;
	private static HttpClient m_client;
	/// <summary>Every journaled command up to this ledger order is in the restored save, in the registry or replayed.</summary>
	private static long m_coveredThrough;
	private static List<JournalEntry> m_replay;
	private static bool m_sawReady;
	private static long m_appended, m_appendFailures, m_refused, m_failedOpen, m_replayed, m_replayDiverged, m_replayFailed;
	/// <summary>The Gateway has no journal (a build before #1318): answer at once, as before.</summary>
	private static volatile bool m_unsupported;

	/// <summary>This World Authority journals: it persists global saves through the Gateway (the same registry).</summary>
	public static bool Active => Enabled && !m_unsupported && ClusterRuntime.GlobalSavesPersist;

	/// <summary>Restored entries wait for <see cref="ReplayRestored"/>: no command may run before them.</summary>
	public static bool ReplayPending { get { lock (Sync) return m_replay != null; } }

	/// <summary>Queues a command result for the registry, or the one queued for this order already (a replay of the same operation id).</summary>
	public static Entry Append(long order, string subsystem, Guid operationId, string origin, string payload, string result)
	{
		lock (Sync)
		{
			if (Entries.TryGetValue(order, out Entry known)) return known;
			var entry = new Entry(order, subsystem, operationId, origin, payload, result);
			Entries.Add(order, entry);
			m_writer ??= StartWriter();
			Monitor.PulseAll(Sync);
			return entry;
		}
	}

	/// <summary>A journaled result at <paramref name="order"/> that is no longer tracked was appended, restored or replayed.</summary>
	public static bool Covers(long order)
	{
		lock (Sync) return order <= m_coveredThrough || Entries.TryGetValue(order, out Entry entry) && entry.State == EntryState.Appended;
	}

	public static void CountFailOpen() => Interlocked.Increment(ref m_failedOpen);

	public static object Status()
	{
		lock (Sync)
			return new
			{
				active = Active,
				unsupported = m_unsupported,
				queued = Entries.Values.Count(entry => entry.State == EntryState.Queued),
				coveredThrough = m_coveredThrough,
				appended = m_appended,
				appendFailures = m_appendFailures,
				refused = m_refused,
				failedOpen = m_failedOpen,
				replayed = m_replayed,
				replayDiverged = m_replayDiverged,
				replayFailed = m_replayFailed,
				replayPending = m_replay != null,
			};
	}

	/// <summary>
	/// Restore, after the command ledger: the journaled commands after <paramref name="ledgerOrder"/> (the restored save's
	/// last one). An unreadable journal is logged and skipped: the WA then comes back as it did before #1318, rewound to
	/// its save, which is better than not coming back.
	/// </summary>
	public static void LoadForReplay(string gateway, long ledgerOrder)
	{
		lock (Sync)
		{
			m_coveredThrough = ledgerOrder;
			m_replay = null;
		}
		if (!Enabled) return;
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
		ClusterRuntime.AuthorizeRegistryClient(client);
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				using JsonDocument page = ClusterRuntime.Get(client, gateway + "/registry/global-journal?after="
					+ ledgerOrder.ToString(CultureInfo.InvariantCulture));
				JournalPage read = page.RootElement.Deserialize<JournalPage>() ?? throw new InvalidOperationException("Empty global journal page");
				List<JournalEntry> entries = (read.Entries ?? Array.Empty<JournalEntry>()).Where(entry => entry.Order > ledgerOrder)
					.OrderBy(entry => entry.Order).ToList();
				if (read.DroppedThrough > ledgerOrder)
					Console.WriteLine("[ClusterPlugin] WARNING: global_journal dropped_through=" + read.DroppedThrough + " after=" + ledgerOrder
						+ ": the registry's journal no longer holds every command since the restored save; the oldest ones are not replayed");
				Console.WriteLine("[ClusterPlugin] global_journal restore after=" + ledgerOrder + " entries=" + entries.Count
					+ " through=" + (entries.Count == 0 ? ledgerOrder : entries[^1].Order));
				if (entries.Count != 0) lock (Sync) m_replay = entries;
				return;
			}
			catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
			{
				m_unsupported = true;
				Console.WriteLine("[ClusterPlugin] global_journal unsupported by the Gateway (no /registry/global-journal); nothing to replay,"
					+ " and commands are answered without it");
				return;
			}
			catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
				or System.Threading.Tasks.TaskCanceledException)
			{
				if (attempt >= 5)
				{
					Console.WriteLine("[ClusterPlugin] ERROR: global_journal unreadable after " + attempt + " attempts; the World Authority comes back"
						+ " without the commands answered since its restored save (after=" + ledgerOrder + "): " + SecretRedactor.Redact(exception.Message));
					return;
				}
				Thread.Sleep(1000);
			}
		}
	}

	/// <summary>
	/// World Authority game thread, before any command is served: runs the restored entries in their ledger order and
	/// records each under its operation id with the result the node was given, so a retry gets that answer again. A
	/// replay whose result differs (the handler saw another state, e.g. a WA-local change the save did not hold) is
	/// logged and counted; the node's answer stays the recorded one.
	/// </summary>
	public static void ReplayRestored()
	{
		List<JournalEntry> entries;
		lock (Sync)
		{
			if (m_replay == null) return;
			entries = m_replay;
		}
		if (MySession.Static?.Ready != true) return;
		// The handlers take the ready session from their own Update, which runs after this one (the factions refuse
		// until then), so the replay starts on the second ready frame.
		if (!m_sawReady)
		{
			m_sawReady = true;
			return;
		}
		long through = 0;
		foreach (JournalEntry entry in entries)
		{
			GlobalCommandResult recorded;
			try { recorded = GlobalCommandCodec.Read(entry.Result); }
			catch (InvalidOperationException)
			{
				Interlocked.Increment(ref m_replayFailed);
				Console.WriteLine("[ClusterPlugin] global_journal replay_invalid order=" + entry.Order + " subsystem=" + entry.Subsystem);
				continue;
			}
			GlobalCommandResult replayed = GlobalStateChannel.ReplayCommand(entry.Subsystem, entry.Origin, entry.Payload, recorded.Payload);
			GlobalCommandLedger.Replay(entry.Subsystem, entry.OperationId, entry.Payload, entry.Order, recorded);
			through = Math.Max(through, entry.Order);
			Interlocked.Increment(ref m_replayed);
			if (!replayed.Success) Interlocked.Increment(ref m_replayFailed);
			if (replayed.Success != recorded.Success || replayed.Payload != recorded.Payload)
			{
				Interlocked.Increment(ref m_replayDiverged);
				Console.WriteLine("[ClusterPlugin] WARNING: global_journal replay_diverged order=" + entry.Order + " subsystem=" + entry.Subsystem
					+ " operation=" + entry.OperationId + " recorded=" + Shorten(GlobalCommandCodec.Write(recorded))
					+ " replayed=" + Shorten(GlobalCommandCodec.Write(replayed)));
			}
		}
		lock (Sync)
		{
			m_coveredThrough = Math.Max(m_coveredThrough, through);
			m_replay = null;
		}
		Console.WriteLine("[ClusterPlugin] global_journal replayed=" + entries.Count + " diverged=" + Interlocked.Read(ref m_replayDiverged)
			+ " failed=" + Interlocked.Read(ref m_replayFailed) + " through=" + through);
		// The entries are in this WA's state now; a save takes them out of the registry's hands.
		ClusterRuntime.RequestGlobalSave();
	}

	private static string Shorten(string value) => value.Length <= 200 ? value : value.Substring(0, 200) + "...";

	private static Thread StartWriter()
	{
		m_client = ClusterHttp.Client(TimeSpan.FromSeconds(5));
		ClusterRuntime.AuthorizeRegistryClient(m_client);
		var thread = new Thread(WriteLoop) { IsBackground = true, Name = "ClusterGlobalJournal" };
		thread.Start();
		return thread;
	}

	/// <summary>Group commit: every queued entry goes in one append; the answers waiting for them are released at once.</summary>
	private static void WriteLoop()
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
		DateTime nextFailureLog = DateTime.MinValue;
		while (true)
		{
			Entry[] batch;
			lock (Sync)
			{
				while (!Entries.Values.Any(entry => entry.State == EntryState.Queued)) Monitor.Wait(Sync);
				batch = Entries.Values.Where(entry => entry.State == EntryState.Queued).Take(BatchEntries).ToArray();
			}
			try
			{
				string url = gateway + "/registry/global-journal?node=" + Uri.EscapeDataString(node ?? string.Empty)
					+ "&epoch=" + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
					+ "&generation=" + ClusterRuntime.AuthorityGeneration.ToString(CultureInfo.InvariantCulture);
				string body = JsonSerializer.Serialize(new { Entries = batch.Select(entry => new JournalEntry(entry.Order, entry.Subsystem,
					entry.OperationId, entry.Origin, entry.Payload, entry.Result)).ToArray() });
				using var content = new StringContent(body, Encoding.UTF8, "application/json");
				using HttpResponseMessage response = m_client.PostAsync(url, content).GetAwaiter().GetResult();
				if (response.StatusCode == HttpStatusCode.Conflict)
				{
					// Fenced: a replacement owns the ledger now. These answers are never sent.
					lock (Sync)
						foreach (Entry entry in batch) entry.State = EntryState.Refused;
					Interlocked.Add(ref m_refused, batch.Length);
					Console.WriteLine("[ClusterPlugin] global_journal append refused (fenced) entries=" + batch.Length
						+ " orders=" + batch[0].Order + ".." + batch[^1].Order + "; their answers stay unsent");
					continue;
				}
				if (response.StatusCode == HttpStatusCode.NotFound)
				{
					m_unsupported = true;
					lock (Sync)
						foreach (Entry entry in Entries.Values.Where(entry => entry.State == EntryState.Queued)) entry.State = EntryState.Unjournaled;
					Console.WriteLine("[ClusterPlugin] global_journal unsupported by the Gateway (no /registry/global-journal);"
						+ " commands are answered without it");
					GlobalStateChannel.ReleaseHeldResponses();
					continue;
				}
				response.EnsureSuccessStatusCode();
				lock (Sync)
				{
					foreach (Entry entry in batch) entry.State = EntryState.Appended;
					// Keep the recent ones for replays of their operation ids; the older ones are covered by the watermark.
					while (Entries.Count > KeptEntries && Entries.First() is { Value.State: EntryState.Appended } oldest)
					{
						m_coveredThrough = Math.Max(m_coveredThrough, oldest.Key);
						Entries.Remove(oldest.Key);
					}
				}
				Interlocked.Add(ref m_appended, batch.Length);
				GlobalStateChannel.ReleaseHeldResponses();
			}
			catch (Exception exception)
			{
				Interlocked.Increment(ref m_appendFailures);
				if (DateTime.UtcNow >= nextFailureLog)
				{
					nextFailureLog = DateTime.UtcNow.AddSeconds(60);
					Console.WriteLine("[ClusterPlugin] global_journal append failed (logged once a minute; answers fail open after "
						+ FailOpen.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms): " + SecretRedactor.Redact(exception.Message));
				}
				Thread.Sleep(250);
			}
		}
	}

	/// <summary>Unjournaled: the Gateway has no journal; the answer goes out at once.</summary>
	internal enum EntryState { Queued, Appended, Refused, Unjournaled }

	internal sealed class Entry
	{
		public Entry(long order, string subsystem, Guid operationId, string origin, string payload, string result)
		{
			Order = order;
			Subsystem = subsystem;
			OperationId = operationId;
			Origin = origin;
			Payload = payload;
			Result = result;
		}

		public long Order { get; }
		public string Subsystem { get; }
		public Guid OperationId { get; }
		public string Origin { get; }
		public string Payload { get; }
		public string Result { get; }
		/// <summary>Written under the journal's lock; read without it by the release check, which only needs a recent value.</summary>
		public volatile EntryState State;
	}

	private sealed record JournalEntry(long Order, string Subsystem, Guid OperationId, string Origin, string Payload, string Result);
	private sealed record JournalPage(JournalEntry[] Entries, long DroppedThrough);
}
