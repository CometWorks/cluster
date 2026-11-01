using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.World;
using SpaceEngineers.Game.World;

namespace ClusterPlugin;

/// <summary>
/// WA-1: respawn-ship cooldown replication. Vanilla keeps cooldowns in the executing node's
/// <c>MySpaceRespawnComponent</c> map and answers <c>OnSyncCooldownRequest</c> only from there, so a client
/// whose next join is routed through another node is handed an empty cooldown list and the pod cooldown
/// silently vanishes. The executing node reports the player's entries as REMAINING milliseconds right after
/// the vanilla reset, the World Authority merges them with wall-clock expiry and replicates, and every node
/// applies them into its local map - any node can then serve the vanilla cooldown sync truthfully. Cooldown
/// ENFORCEMENT stays exactly where vanilla put it: in the client's medical screen.
/// </summary>
public static class RespawnCooldownService
{
	private const string Subsystem = "respawn-cooldowns";
	private const int MaxEntries = 4096;
	private const int MaxShipIdLength = 256;
	private const double MaxRemainingMilliseconds = 7 * 24 * 3600 * 1000.0;

	private static readonly object SyncRoot = new object();
	private static readonly Dictionary<CooldownKey, double> Expiry = new();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	private static string m_snapshot = JsonSerializer.Serialize(new CooldownSnapshot(Array.Empty<CooldownEntry>()));
	private static string m_pending;
	private static string m_applied;

	private readonly record struct CooldownKey(ulong SteamId, int SerialId, string ShipId);
	public sealed record CooldownEntry(ulong SteamId, int SerialId, string ShipId, int RemainingMs);
	public sealed record CooldownReport(string Origin, CooldownEntry[] Entries);
	public sealed record CooldownSnapshot(CooldownEntry[] Entries);

	internal static void Install()
	{
		MySpaceRespawnComponentSeams.ClusterCooldownChanged = OnCooldownChanged;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveSnapshot);
	}

	private static void OnCooldownChanged(MyPlayer.PlayerId controllerId)
	{
		if (!ClusterRuntime.IsClustered) return;
		MySpaceRespawnComponent respawn = MySpaceRespawnComponent.Static;
		if (respawn == null) return;
		var entries = new List<CooldownEntry>();
		respawn.ClusterVisitCooldowns(controllerId.SteamId, (serialId, shipId, remainingMs) =>
			entries.Add(new CooldownEntry(controllerId.SteamId, serialId, shipId, remainingMs)));
		string payload = JsonSerializer.Serialize(new CooldownReport(Node, entries.Take(MaxEntries).ToArray()));
		Console.WriteLine($"[ClusterPlugin] respawn cooldown report client={controllerId.SteamId} serial={controllerId.SerialId} entries={entries.Count}");
		if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
		else GlobalStateChannel.SendRequest(Subsystem, payload);
	}

	private static void ReceiveReport(string origin, string payload)
	{
		CooldownReport report = Read(payload);
		lock (SyncRoot)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			foreach (CooldownEntry entry in report.Entries)
			{
				var key = new CooldownKey(entry.SteamId, entry.SerialId, entry.ShipId);
				if (entry.RemainingMs > 0) Expiry[key] = now + Math.Min(entry.RemainingMs, MaxRemainingMilliseconds);
				else Expiry.Remove(key);
			}
		}
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string payload;
		lock (SyncRoot)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			foreach (CooldownKey stale in Expiry.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
				Expiry.Remove(stale);
			payload = m_snapshot = JsonSerializer.Serialize(new CooldownSnapshot(Expiry
				.OrderBy(entry => entry.Key.SteamId).ThenBy(entry => entry.Key.SerialId)
				.ThenBy(entry => entry.Key.ShipId, StringComparer.Ordinal)
				.Select(entry => new CooldownEntry(entry.Key.SteamId, entry.Key.SerialId, entry.Key.ShipId,
					(int)(entry.Value - now)))
				.ToArray()));
		}
		GlobalStateChannel.Publish(Subsystem, payload);
	}

	private static void ReceiveSnapshot(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid respawn cooldown snapshot");
		lock (SyncRoot) m_pending = payload;
	}

	/// <summary>
	/// Applies the latest replicated snapshot on the game thread - <c>m_globalRespawnTimesMs</c> is a
	/// game-thread structure, so the replica handler only parks the payload.
	/// </summary>
	public static void Update()
	{
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
		if (MySession.Static?.Ready != true) return;
		MySpaceRespawnComponent respawn = MySpaceRespawnComponent.Static;
		if (respawn == null) return;
		CooldownSnapshot snapshot = null;
		try { snapshot = JsonSerializer.Deserialize<CooldownSnapshot>(payload); }
		catch (Exception e) { Console.WriteLine("[ClusterPlugin] respawn cooldown snapshot rejected: " + e.Message); }
		if (snapshot?.Entries == null || snapshot.Entries.Length > MaxEntries) snapshot = null;
		if (snapshot != null)
		{
			var resync = new HashSet<ulong>();
			foreach (CooldownEntry entry in snapshot.Entries)
			{
				if (string.IsNullOrEmpty(entry.ShipId) || entry.ShipId.Length > MaxShipIdLength) continue;
				respawn.ClusterApplyCooldown(entry.SteamId, entry.SerialId, entry.ShipId, entry.RemainingMs);
				resync.Add(entry.SteamId);
			}
			// A client already attached HERE re-learns the cooldown a foreign node just set for it.
			foreach (ulong steamId in resync)
				if (steamId != Sandbox.Game.Multiplayer.Sync.MyId
					&& Sandbox.Game.Multiplayer.Sync.Clients?.HasClient(steamId) == true)
					respawn.SyncCooldownToPlayer(steamId, isLocal: false);
			Console.WriteLine($"[ClusterPlugin] respawn cooldowns applied entries={snapshot.Entries.Length}");
		}
		lock (SyncRoot)
		{
			m_applied = payload;
			if (string.Equals(m_pending, payload, StringComparison.Ordinal)) m_pending = null;
		}
	}

	public static object Status(ulong userId, int serialId, string shipId)
	{
		MySpaceRespawnComponent respawn = MySpaceRespawnComponent.Static;
		if (respawn == null)
			return new { node = Node, userId, serialId, seconds = 0, entries = Array.Empty<object>() };
		if (!string.IsNullOrEmpty(shipId))
			return new
			{
				node = Node, userId, serialId, ship = shipId,
				seconds = respawn.GetRespawnCooldownSeconds(new MyPlayer.PlayerId(userId, serialId), shipId)
			};
		var entries = new List<object>();
		int seconds = 0;
		respawn.ClusterVisitCooldowns(userId, (serial, ship, remainingMs) =>
		{
			if (serial != serialId) return;
			int shipSeconds = Math.Max(remainingMs / 1000, 0);
			seconds = Math.Max(seconds, shipSeconds);
			entries.Add(new { ship, seconds = shipSeconds });
		});
		return new { node = Node, userId, serialId, seconds, entries };
	}

	private static CooldownReport Read(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid respawn cooldown report");
		CooldownReport report = JsonSerializer.Deserialize<CooldownReport>(payload)
			?? throw new InvalidOperationException("Invalid respawn cooldown report");
		if (report.Entries == null || report.Entries.Length > MaxEntries
			|| report.Entries.Any(entry => entry == null || string.IsNullOrEmpty(entry.ShipId)
				|| entry.ShipId.Length > MaxShipIdLength))
			throw new InvalidOperationException("Invalid respawn cooldown report");
		return report;
	}

	private static string Snapshot() { lock (SyncRoot) return m_snapshot; }
}
