using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ClusterPlugin;

/// <summary>
/// WA-1: the cluster-wide online-player list. Vanilla's <c>MyPlayerCollection.GetOnlinePlayers</c> only
/// knows the players attached to THIS server, so no node (or the WA) can answer "who is online in the
/// world". Each node reports its locally attached players once a second, the
/// World Authority merges per origin with the same dead-node expiry the PCU ledger uses, and replicates
/// the flat cluster list to every node. Login and logout surface as report deltas within a report cycle.
/// </summary>
public static class OnlinePlayersService
{
	private const string Subsystem = "online-players";
	private const int MaxPlayersPerReport = 4096;

	/// <summary>Dead-node expiry, mirroring the PCU ledger: live nodes refresh every second.</summary>
	private const double ReportExpiryMilliseconds = 30000;

	/// <summary>
	/// How long after its FIRST report the World Authority keeps calling deltas seeding (T-0204).
	/// The WA merges one origin at a time, so its first merge holds one node's players and every
	/// other node's first report arrives as a batch of keys it has never seen - i.e. as logins.
	/// The window is the dead-node expiry on purpose: a node that has not reported within it is
	/// not part of the cluster by this service's own definition, so anything arriving later really
	/// is new. The cost is that a genuine login in the first 30 s after a WA restart gets no chat
	/// line; the mirror still installs it, because seeding only silences the announcer.
	/// </summary>
	private const double SeedingWindowMilliseconds = ReportExpiryMilliseconds;

	private static readonly object SyncRoot = new object();
	private static readonly Dictionary<string, StampedReport> Reports = new(StringComparer.Ordinal);
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	private sealed record StampedReport(double Received, OnlinePlayerEntry[] Players);
	private static OnlinePlayerEntry[] m_cluster = Array.Empty<OnlinePlayerEntry>();
	/// <summary>Last set the delta was computed against, keyed the way a player is identified.</summary>
	private static Dictionary<(ulong SteamId, int SerialId), OnlinePlayerEntry> m_previous = new();
	private static bool m_seeded;
	/// <summary>End of the WA's seeding window; see <see cref="SeedingWindowMilliseconds"/>.</summary>
	private static double m_seedingUntil;
	/// <summary>
	/// Origins dropped by the expiry sweep rather than by anything they said. Their next report is
	/// a RETURN, not a wave of logins; see the announcement rule in <see cref="ReceiveAtAuthority"/>.
	/// </summary>
	private static readonly HashSet<string> Returning = new(StringComparer.Ordinal);
	private static string m_snapshot = JsonSerializer.Serialize(new OnlineSnapshot(Array.Empty<OnlinePlayerEntry>()));
	private static double m_nextReport;

	/// <summary>
	/// One attached player, as the node that hosts it sees it.
	///
	/// T-0178: <paramref name="ServiceName"/> and <paramref name="IsAdmin"/> exist for the F3
	/// player list, which a remote node cannot fill from anything else it holds - the platform
	/// icon comes from the member entry vanilla never created for a player attached elsewhere.
	/// Both default, so a report from a node that predates them still deserializes; the cluster
	/// runs one version set (cluster-consistency-rules), but a report is not the place to find
	/// out that it does not.
	///
	/// <paramref name="IsAdmin"/> is a SNAPSHOT of the reporting node's view. PromotionService
	/// replicates promote levels cluster-wide and is the live source, so a consumer that has it
	/// should prefer it; this field is for the case where the entry travels on its own.
	/// </summary>
	public sealed record OnlinePlayerEntry(string Node, ulong SteamId, int SerialId, long IdentityId, string Name,
		string ServiceName = "", bool IsAdmin = false);
	public sealed record OnlineReport(string Origin, OnlinePlayerEntry[] Players);
	public sealed record OnlineSnapshot(OnlinePlayerEntry[] Players);

	/// <summary>
	/// A player entering or leaving the CLUSTER-wide list (T-0178, T-0179). Raised on every node,
	/// off the merged snapshot, so it fires once per real login/logout and NOT on a node traversal -
	/// a traversal only changes which node reports the player, and the merged list is keyed by
	/// (SteamId, SerialId), not by node.
	///
	/// <c>seeding</c> is true for the first snapshot a process sees, where every player present is
	/// reported as arriving. T-0178's mirror wants those (it has nothing installed yet); T-0179's
	/// join announcement must skip them, or a World Authority restart would announce everyone.
	///
	/// Raised OUTSIDE the lock and on whatever thread the snapshot arrived on. A handler that
	/// touches the game must marshal to the update thread itself.
	/// </summary>
	public static event Action<OnlinePlayerEntry, bool> PlayerEnteredCluster;

	/// <inheritdoc cref="PlayerEnteredCluster"/>
	public static event Action<OnlinePlayerEntry, bool> PlayerLeftCluster;

	/// <summary>The cluster-wide list as this node last saw it.</summary>
	public static OnlinePlayerEntry[] Cluster { get { lock (SyncRoot) return m_cluster; } }

	/// <summary>
	/// Replace the merged list and report what changed. Called under <see cref="SyncRoot"/>; the
	/// deltas it returns are raised by the caller after the lock is dropped, because a handler
	/// installs members and identities on the game thread and must never run with this held.
	/// </summary>
	private static List<(OnlinePlayerEntry Entry, bool Entered, bool Seeding)> SetCluster(OnlinePlayerEntry[] next, bool seeding)
	{
		var current = new Dictionary<(ulong, int), OnlinePlayerEntry>();
		foreach (OnlinePlayerEntry entry in next)
			current[(entry.SteamId, entry.SerialId)] = entry;   // last writer wins; a duplicated id is one player
		var deltas = new List<(OnlinePlayerEntry, bool, bool)>();
		foreach (var pair in current)
			if (!m_previous.ContainsKey(pair.Key)) deltas.Add((pair.Value, true, seeding));
		foreach (var pair in m_previous)
			if (!current.ContainsKey(pair.Key)) deltas.Add((pair.Value, false, seeding));
		m_previous = current;
		m_seeded = true;
		m_cluster = next;
		return deltas;
	}

	private static void Raise(List<(OnlinePlayerEntry Entry, bool Entered, bool Seeding)> deltas)
	{
		foreach (var delta in deltas)
		{
			try
			{
				// One bad handler must not stop the others, and must never stop the snapshot
				// from being applied - this runs on the channel's thread.
				if (delta.Entered) PlayerEnteredCluster?.Invoke(delta.Entry, delta.Seeding);
				else PlayerLeftCluster?.Invoke(delta.Entry, delta.Seeding);
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] online players delta handler failed steam="
					+ delta.Entry.SteamId + " entered=" + delta.Entered + ": " + exception);
			}
		}
	}

	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true
			|| ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		string payload = JsonSerializer.Serialize(new OnlineReport(Node, CaptureLocal()));
		if (ClusterRuntime.IsWorldAuthority)
		{
			// The WA hosts no players of its own, but its (empty) report keeps its origin fresh and
			// drives the same merge cadence the nodes get.
			ReceiveReport(Node, payload);
			return;
		}
		// Every second even when unchanged: a fresh report is what keeps this origin alive in the
		// WA's merge - the expiry exists to drop DEAD nodes, not stable ones.
		GlobalStateChannel.SendRequest(Subsystem, payload);
	}

	private static OnlinePlayerEntry[] CaptureLocal()
	{
		MySession session = MySession.Static;
		if (session == null) return Array.Empty<OnlinePlayerEntry>();
		return session.Players.GetOnlinePlayers()
			.Select(player => new OnlinePlayerEntry(Node, player.Id.SteamId, player.Id.SerialId,
				player.Identity?.IdentityId ?? 0, player.DisplayName ?? string.Empty,
				ServiceNameOf(player.Id.SteamId), IsAdminOf(player.Id.SteamId)))
			.OrderBy(entry => entry.SteamId).ThenBy(entry => entry.SerialId)
			.Take(MaxPlayersPerReport)
			.ToArray();
	}

	/// <summary>
	/// The platform a locally attached player came in on ("Steam", "EOS", "DirectTransport"), for
	/// the F3 row's icon (T-0178). Only the hosting node can answer this: the member entry it reads
	/// exists nowhere else in the cluster. Empty when the member is gone - a player captured in the
	/// same millisecond it detaches is not worth an exception.
	/// </summary>
	private static string ServiceNameOf(ulong steamId)
	{
		try { return MyMultiplayer.Static?.GetMemberServiceName(steamId) ?? string.Empty; }
		catch (Exception) { return string.Empty; }
	}

	/// <summary>
	/// Whether the hosting node considers this player an admin, read straight out of PromotedUsers
	/// for the same reason PlayerProfileService:436 does - GetUserPromoteLevel answers Owner for
	/// ids that are not in the table at all, which would mark every player an admin.
	/// </summary>
	private static bool IsAdminOf(ulong steamId)
		=> PromotionService.IsModerator(steamId);

	private static void ReceiveReport(string origin, string payload)
	{
		OnlineReport report = Read(payload);
		string snapshot;
		List<(OnlinePlayerEntry Entry, bool Entered, bool Seeding)> deltas;
		lock (SyncRoot)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			Reports[origin] = new StampedReport(now, report.Players);
			bool returned = Returning.Remove(origin);
			foreach (string stale in Reports.Where(entry => now - entry.Value.Received >= ReportExpiryMilliseconds)
				.Select(entry => entry.Key).ToArray())
			{
				Reports.Remove(stale);
				Returning.Add(stale);
				Console.WriteLine("[ClusterPlugin] online report expired origin=" + stale);
			}
			// SEEDING IS NOT "THE FIRST MERGE" HERE. Reports arrive one origin at a time, so the
			// first merge is one node's players and the second node's first report would otherwise
			// read as that whole node logging in - the storm the seeding flag exists to prevent
			// (T-0204). Stay in seeding for the window after the first report instead.
			if (!m_seeded) m_seedingUntil = now + SeedingWindowMilliseconds;
			bool seeding = !m_seeded || now < m_seedingUntil;
			deltas = SetCluster(Reports.OrderBy(entry => entry.Key, StringComparer.Ordinal)
				.SelectMany(entry => entry.Value.Players).ToArray(), seeding);
			// AN EXPIRED ORIGIN IS NOT A ROOM FULL OF LOGOUTS (T-0204). Reports are produced from
			// ClusterRuntime.Update, i.e. on the game thread, so a node that stalls stops reporting
			// while every one of its players is still in the world - and the save-catalogue hitch
			// on record is 60 s against a 30 s expiry. Announcing that as N departures, then as N
			// arrivals when the node resumes, is a lie in both directions and the loudest possible
			// one. The merged list still drops them, because nothing can route to a node that is
			// not answering; only the CHAT LINE is suppressed, and the same for the return.
			//
			// The cost, stated because it is a real one: when a node dies for good, its players
			// leave the list with no announcement. That is the trade against announcing everyone
			// twice on a hitch, and a dead node's own clients are disconnected regardless.
			for (int i = 0; i < deltas.Count; i++)
			{
				var delta = deltas[i];
				if (delta.Seeding) continue;
				bool quiet = delta.Entered
					? returned && string.Equals(delta.Entry.Node, origin, StringComparison.Ordinal)
					: !Reports.ContainsKey(delta.Entry.Node);
				if (quiet) deltas[i] = (delta.Entry, delta.Entered, true);
			}
			snapshot = m_snapshot = JsonSerializer.Serialize(new OnlineSnapshot(m_cluster));
		}
		Raise(deltas);
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	private static void ApplySnapshot(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid online players snapshot");
		OnlineSnapshot snapshot = JsonSerializer.Deserialize<OnlineSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid online players snapshot");
		if (snapshot.Players == null) throw new InvalidOperationException("Invalid online players snapshot");
		List<(OnlinePlayerEntry Entry, bool Entered, bool Seeding)> deltas;
		lock (SyncRoot)
		{
			// A node gets the WHOLE merged list in one snapshot, so its first one really is the
			// complete picture and no window is needed on this side.
			deltas = SetCluster(snapshot.Players, !m_seeded);
			m_snapshot = payload;
		}
		Raise(deltas);
	}

	public static object Status()
	{
		OnlinePlayerEntry[] cluster;
		lock (SyncRoot) cluster = m_cluster;
		return new
		{
			node = Node,
			local = CaptureLocal().Select(entry => new { steamId = entry.SteamId, serialId = entry.SerialId,
				identityId = entry.IdentityId, name = entry.Name, serviceName = entry.ServiceName,
				isAdmin = entry.IsAdmin }),
			cluster = cluster.Select(entry => new { node = entry.Node, steamId = entry.SteamId,
				serialId = entry.SerialId, identityId = entry.IdentityId, name = entry.Name,
				serviceName = entry.ServiceName, isAdmin = entry.IsAdmin })
		};
	}

	private static OnlineReport Read(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid online players report");
		OnlineReport report = JsonSerializer.Deserialize<OnlineReport>(payload)
			?? throw new InvalidOperationException("Invalid online players report");
		if (string.IsNullOrEmpty(report.Origin) || report.Origin.Length > 128 || report.Players == null
			|| report.Players.Length > MaxPlayersPerReport
			|| report.Players.Any(entry => entry == null || entry.Name is { Length: > 256 }))
			throw new InvalidOperationException("Invalid online players report");
		return report;
	}

	private static string Snapshot() { lock (SyncRoot) return m_snapshot; }
}
