using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ClusterPlugin;

/// <summary>
/// WA-1: promote-level replication. Vanilla's OnPromoteLevelSet is [ServerInvoked][Broadcast]: it updates
/// PromotedUsers on the server that raised it and broadcasts to the clients attached THERE. In the cluster
/// that leaves every other node serving the old level - server-side admin checks
/// (MySession.GetUserPromoteLevel and friends) silently fail for a promoted player routed elsewhere. The
/// node that executed a change reports it, the World Authority merges the authoritative map and
/// replicates, and every node applies differences by re-raising the VANILLA event locally (which also
/// re-broadcasts to its own attached clients). A reentrancy guard keeps the seam from reporting the
/// cluster's own applications back.
/// </summary>
public static class PromotionService
{
	private const string Subsystem = "promotions";
	private const int MaxEntries = 4096;
	private const double SeedGraceMilliseconds = 15_000;

	private static readonly object SyncRoot = new object();
	private static readonly Dictionary<ulong, int> Levels = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	private static string m_snapshot = JsonSerializer.Serialize(new PromotionSnapshot(Array.Empty<PromotionEntry>()));
	private static string m_pending;
	private static string m_applied;
	private static bool m_applying;
	private static bool m_seeded;
	private static double m_readySince;

	public sealed record PromotionEntry(ulong SteamId, int Level);
	public sealed record PromotionReport(string Origin, PromotionEntry[] Entries);
	public sealed record PromotionSnapshot(PromotionEntry[] Entries);

	internal static void Install()
	{
		MySessionSeams.ClusterPromotionChanged = OnPromotionChanged;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveSnapshot);
	}

	private static void OnPromotionChanged(ulong steamId, MyPromoteLevel level)
	{
		if (!ClusterRuntime.IsClustered || m_applying) return;
		string payload = JsonSerializer.Serialize(new PromotionReport(Node,
			new[] { new PromotionEntry(steamId, (int)level) }));
		Console.WriteLine($"[ClusterPlugin] promotion report client={steamId} level={(int)level}");
		if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
		else GlobalStateChannel.SendRequest(Subsystem, payload);
	}

	/// <summary>
	/// Reports this node's whole local promotion map once, as soon as the session is ready. A level that
	/// arrives in the world checkpoint (or from ConfigDedicated.Administrators) is not a change, so nothing
	/// would ever report it: the authoritative map would start empty and the demotion pass below would then
	/// wipe the seeded admins on every node, clients included.
	/// </summary>
	private static void SeedFromSession(MySession session)
	{
		if (m_seeded || !ClusterRuntime.IsClustered) return;
		if (m_readySince == 0) m_readySince = ClusterRuntime.LocalMilliseconds;
		PromotionEntry[] entries = session.PromotedUsers
			.Where(entry => (int)entry.Value > 0)
			.Select(entry => new PromotionEntry(entry.Key, Math.Min((int)entry.Value, (int)MyPromoteLevel.Admin)))
			.ToArray();
		if (entries.Length == 0)
		{
			// The checkpoint's levels do not all land with MySession.Ready: player data carries its own
			// PromoteLevel (MyPlayerCollection.LoadPlayers) and arrives later, so an empty map right after
			// load is no proof there is nothing to seed - a live flight lost its admin exactly there.
			// ponytail: fixed grace, swap for a session-loaded signal if one ever gets exposed.
			if (ClusterRuntime.LocalMilliseconds - m_readySince < SeedGraceMilliseconds) return;
			m_seeded = true;
			return;
		}
		string payload = JsonSerializer.Serialize(new PromotionReport(Node, entries));
		Console.WriteLine($"[ClusterPlugin] promotion seed entries={entries.Length}");
		if (ClusterRuntime.IsWorldAuthority)
		{
			ReceiveReport(Node, payload);
			m_seeded = true;
		}
		// Retried on the next frame while the World Authority is still unreachable: losing the seed would
		// cost the promotions the demotion pass is about to compare against.
		else if (GlobalStateChannel.SendRequest(Subsystem, payload)) m_seeded = true;
	}

	private static void ReceiveReport(string origin, string payload)
	{
		PromotionReport report = Read(payload);
		lock (SyncRoot)
			foreach (PromotionEntry entry in report.Entries)
			{
				if (entry.Level > 0) Levels[entry.SteamId] = entry.Level;
				else Levels.Remove(entry.SteamId);
			}
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string payload;
		lock (SyncRoot)
			payload = m_snapshot = JsonSerializer.Serialize(new PromotionSnapshot(Levels
				.OrderBy(entry => entry.Key)
				.Select(entry => new PromotionEntry(entry.Key, entry.Value))
				.ToArray()));
		GlobalStateChannel.Publish(Subsystem, payload);
		// The WA never receives its own replica broadcasts - park the merged map so its Update()
		// applies it into the local session the same way every replica does.
		lock (SyncRoot) m_pending = payload;
	}

	private static void ReceiveSnapshot(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid promotion snapshot");
		lock (SyncRoot) m_pending = payload;
	}

	/// <summary>
	/// Applies the latest replicated snapshot on the game thread by re-raising the vanilla event for
	/// every difference - PromotedUsers and the client broadcast are game-thread machinery.
	/// </summary>
	public static void Update()
	{
		MySession session = MySession.Static;
		if (session?.Ready != true) return;
		SeedFromSession(session);
		// Until the seed is in, an authoritative map that has never heard of this node's promotions would
		// demote them.
		if (!m_seeded) return;
		string payload;
		lock (SyncRoot)
		{
			payload = m_pending;
			if (payload == null) return;
			if (string.Equals(payload, m_applied, StringComparison.Ordinal))
			{
				m_pending = null;
				return;
			}
		}
		PromotionSnapshot snapshot = null;
		try { snapshot = JsonSerializer.Deserialize<PromotionSnapshot>(payload); }
		catch (Exception e) { Console.WriteLine("[ClusterPlugin] promotion snapshot rejected: " + e.Message); }
		if (snapshot?.Entries == null || snapshot.Entries.Length > MaxEntries) snapshot = null;
		if (snapshot != null)
		{
			var replicated = snapshot.Entries.ToDictionary(entry => entry.SteamId, entry => entry.Level);
			// Demotions first: local entries the authoritative map no longer contains.
			ulong[] demoted = session.PromotedUsers.Keys.Where(steamId => !replicated.ContainsKey(steamId)).ToArray();
			m_applying = true;
			try
			{
				foreach (ulong steamId in demoted)
					MySessionSeams.ClusterRaisePromoteLevel(steamId, MyPromoteLevel.None);
				foreach ((ulong steamId, int level) in replicated)
				{
					if (level < 0 || level > (int)MyPromoteLevel.Admin) continue;
					session.PromotedUsers.TryGetValue(steamId, out MyPromoteLevel current);
					if ((int)current != level) MySessionSeams.ClusterRaisePromoteLevel(steamId, (MyPromoteLevel)level);
				}
			}
			finally { m_applying = false; }
		}
		lock (SyncRoot)
		{
			m_applied = payload;
			if (string.Equals(m_pending, payload, StringComparison.Ordinal)) m_pending = null;
		}
	}

	/// <summary>
	/// Reports the effective level AND the raw PromotedUsers entry. The lane asserts the raw entry:
	/// with OnlineMode=OFFLINE (the headless test cluster) vanilla short-circuits every steamId to
	/// Owner before ever consulting the map the cluster replicates.
	/// </summary>
	public static object Status(ulong userId)
	{
		MySession session = MySession.Static;
		int promoted = 0;
		if (session != null && session.PromotedUsers.TryGetValue(userId, out MyPromoteLevel entry))
			promoted = (int)entry;
		return new
		{
			node = Node,
			userId,
			level = session == null ? 0 : (int)session.GetUserPromoteLevel(userId),
			promoted
		};
	}

	private static PromotionReport Read(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid promotion report");
		PromotionReport report = JsonSerializer.Deserialize<PromotionReport>(payload)
			?? throw new InvalidOperationException("Invalid promotion report");
		if (report.Entries == null || report.Entries.Length > MaxEntries
			|| report.Entries.Any(entry => entry == null || entry.Level < 0 || entry.Level > (int)MyPromoteLevel.Admin))
			throw new InvalidOperationException("Invalid promotion report");
		return report;
	}

	private static string Snapshot() { lock (SyncRoot) return m_snapshot; }
}
