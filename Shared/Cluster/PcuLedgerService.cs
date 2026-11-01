using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// WA-2: the global PCU / block-limit ledger. Each node's <c>MyBlockLimits</c> instances only count the grids
/// that node has loaded, so without this every world-wide cap could be exceeded once per node. Regular nodes
/// report their per-pool usage once a second, the World Authority merges and replicates the reports, and the
/// <c>MySession.CheckLimits</c> seams join the <em>other</em> nodes' usage into every local limit verdict.
/// <para>
/// Pool keys follow the block-limit mode, mirroring how <c>MyIdentity.BlockLimits</c> picks the instance:
/// GLOBALLY uses the single shared pool (key 0), PER_PLAYER keys by identity, PER_FACTION by faction id. NPC
/// pools stay node-local - their spawn budgets are already governed per node by the encounter generators.
/// </para>
/// Enforcement stays local and advisory-fresh: a verdict can be up to a report cycle stale, so two nodes racing
/// the last sliver of a cap can still both pass - the same window vanilla accepts between check and build. The
/// ledger bounds the error to one cycle of building instead of letting the caps diverge without bound.
/// </summary>
public static class PcuLedgerService
{
	private const string Subsystem = "pcu";
	private const int MaxPoolsPerReport = 4096;
	private const int MaxTypesPerPool = 256;

	/// <summary>
	/// How long a node's usage report stays in the merge without a fresh one. Nodes report once a second, so
	/// this only fires for dead nodes - whose grids are being recovered by OTHER nodes and would otherwise be
	/// counted twice (once by the recovering node's fresh report, once by the dead node's last one) forever.
	/// </summary>
	private const double ReportExpiryMilliseconds = 30000;

	private static readonly object Sync = new object();
	private static readonly Dictionary<string, StampedReport> Reports = new(StringComparer.Ordinal);
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	private sealed record StampedReport(double Received, PcuUsageEntry[] Pools);
	private static Dictionary<string, PcuUsageEntry[]> m_merged = new(StringComparer.Ordinal);
	private static string m_snapshot = JsonSerializer.Serialize(new PcuLedgerSnapshot(Array.Empty<PcuNodeUsage>()));
	private static double m_nextReport;

	public sealed record PcuUsageEntry(long Key, int PcuBuilt, int BlocksBuilt, Dictionary<string, int> TypeBuilt);
	public sealed record PcuNodeUsage(string Origin, PcuUsageEntry[] Pools);
	public sealed record PcuLedgerSnapshot(PcuNodeUsage[] Nodes);

	internal static void Install()
	{
		MySessionSeams.ClusterRemoteBlockUsage = RemoteUsage;
		MySessionSeams.ClusterRemoteTypeBuilt = RemoteTypeBuilt;
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ApplySnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ApplySnapshot);
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true
			|| ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		if (ClusterRuntime.IsWorldAuthority)
		{
			// The World Authority loads no grids, so it contributes no usage of its own.
			lock (Sync) Reports[Node] = new StampedReport(ClusterRuntime.LocalMilliseconds, Array.Empty<PcuUsageEntry>());
			PublishMerged();
		}
		else GlobalStateChannel.SendRequest(Subsystem,
			JsonSerializer.Serialize(new PcuNodeUsage(Node, CaptureLocal())));
	}

	/// <summary>PCU and block count built for this owner's pool on every node except this one.</summary>
	public static (int PcuBuilt, int BlocksBuilt) RemoteUsage(long ownerId)
	{
		if (!TryResolveKey(ownerId, out long key)) return default;
		int pcu = 0, blocks = 0;
		lock (Sync)
			foreach ((string origin, PcuUsageEntry[] pools) in m_merged)
			{
				if (string.Equals(origin, Node, StringComparison.Ordinal)) continue;
				foreach (PcuUsageEntry pool in pools)
					if (pool.Key == key)
					{
						pcu += pool.PcuBuilt;
						blocks += pool.BlocksBuilt;
					}
			}
		return (pcu, blocks);
	}

	public static int RemoteTypeBuilt(long ownerId, string blockPairName)
	{
		if (!TryResolveKey(ownerId, out long key)) return 0;
		int built = 0;
		lock (Sync)
			foreach ((string origin, PcuUsageEntry[] pools) in m_merged)
			{
				if (string.Equals(origin, Node, StringComparison.Ordinal)) continue;
				foreach (PcuUsageEntry pool in pools)
					if (pool.Key == key && pool.TypeBuilt != null
						&& pool.TypeBuilt.TryGetValue(blockPairName, out int count))
						built += count;
			}
		return built;
	}

	public static object Status(long ownerId)
	{
		TryResolveKey(ownerId, out long key);
		(int remotePcu, int remoteBlocks) = RemoteUsage(ownerId);
		lock (Sync)
			return new
			{
				node = Node,
				owner = ownerId,
				key,
				remotePcuBuilt = remotePcu,
				remoteBlocksBuilt = remoteBlocks,
				nodes = m_merged.Select(entry => new
				{
					origin = entry.Key,
					pools = entry.Value.Select(pool => new { pool.Key, pool.PcuBuilt, pool.BlocksBuilt })
				}).ToArray()
			};
	}

	/// <summary>
	/// Maps an owner to its usage-pool key the same way <c>MyIdentity.BlockLimits</c> picks the limits instance.
	/// NPC identities return false: their pools are node-local by design.
	/// </summary>
	private static bool TryResolveKey(long ownerId, out long key)
	{
		key = 0;
		MySession session = MySession.Static;
		if (session == null) return false;
		if (session.Players.IdentityIsNpc(ownerId)) return false;
		switch (session.BlockLimitsEnabled)
		{
			case VRage.Game.MyBlockLimitsEnabledEnum.GLOBALLY:
				key = 0;
				return true;
			case VRage.Game.MyBlockLimitsEnabledEnum.PER_PLAYER:
				key = ownerId;
				return true;
			case VRage.Game.MyBlockLimitsEnabledEnum.PER_FACTION:
				MyFaction faction = session.Factions.TryGetPlayerFaction(ownerId) as MyFaction;
				if (faction == null) return false;
				key = faction.FactionId;
				return true;
			default:
				return false;
		}
	}

	private static PcuUsageEntry[] CaptureLocal()
	{
		MySession session = MySession.Static;
		var pools = new List<PcuUsageEntry>();
		switch (session.BlockLimitsEnabled)
		{
			case VRage.Game.MyBlockLimitsEnabledEnum.GLOBALLY:
				if (session.GlobalBlockLimits != null) pools.Add(Capture(0, session.GlobalBlockLimits));
				break;
			case VRage.Game.MyBlockLimitsEnabledEnum.PER_PLAYER:
				foreach (MyIdentity identity in session.Players.GetAllIdentities().ToArray())
					if (!session.Players.IdentityIsNpc(identity.IdentityId) && identity.BlockLimits != null
						&& identity.BlockLimits != MyBlockLimits.Empty)
						pools.Add(Capture(identity.IdentityId, identity.BlockLimits));
				break;
			case VRage.Game.MyBlockLimitsEnabledEnum.PER_FACTION:
				foreach ((long factionId, MyFaction faction) in session.Factions.ToArray())
					if (faction.BlockLimits != null && faction.BlockLimits != MyBlockLimits.Empty)
						pools.Add(Capture(factionId, faction.BlockLimits));
				break;
		}
		// Empty pools still report: a node whose usage dropped to zero must overwrite its previous report.
		return pools.Take(MaxPoolsPerReport).ToArray();
	}

	private static PcuUsageEntry Capture(long key, MyBlockLimits limits)
		=> new PcuUsageEntry(key, limits.PCUBuilt, limits.BlocksBuilt,
			limits.BlockTypeBuilt.Take(MaxTypesPerPool)
				.ToDictionary(entry => entry.Key, entry => entry.Value.BlocksBuilt, StringComparer.Ordinal));

	private static void ReceiveReport(string origin, string payload)
	{
		PcuNodeUsage usage = Read(payload);
		lock (Sync) Reports[origin] = new StampedReport(ClusterRuntime.LocalMilliseconds, usage.Pools);
		PublishMerged();
	}

	private static void PublishMerged()
	{
		string payload;
		lock (Sync)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			foreach (string stale in Reports.Where(entry => now - entry.Value.Received >= ReportExpiryMilliseconds)
				.Select(entry => entry.Key).ToArray())
			{
				Reports.Remove(stale);
				Console.WriteLine("[ClusterPlugin] pcu report expired origin=" + stale);
			}
			m_merged = Reports.ToDictionary(entry => entry.Key, entry => entry.Value.Pools, StringComparer.Ordinal);
			payload = JsonSerializer.Serialize(new PcuLedgerSnapshot(m_merged
				.OrderBy(entry => entry.Key, StringComparer.Ordinal)
				.Select(entry => new PcuNodeUsage(entry.Key, entry.Value)).ToArray()));
			// Every other registry skips an unchanged republish (PositionRegistryService, RespawnRegistryService,
			// IgcEndpointRegistryService, AntennaGraphService); this one did not. PublishMerged runs once per
			// node report, so with K nodes the authority emitted K broadcasts a second and each of them made all
			// K nodes apply an identical snapshot - O(K^2) messages a second carrying no news.
			if (payload == m_snapshot) return;
			m_snapshot = payload;
		}
		GlobalStateChannel.Publish(Subsystem, payload);
	}

	private static void ApplySnapshot(string payload)
	{
		PcuLedgerSnapshot snapshot = JsonSerializer.Deserialize<PcuLedgerSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid PCU ledger snapshot");
		lock (Sync)
		{
			m_merged = snapshot.Nodes.ToDictionary(node => node.Origin, node => node.Pools, StringComparer.Ordinal);
			m_snapshot = payload;
		}
	}

	private static PcuNodeUsage Read(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid PCU usage report");
		PcuNodeUsage usage = JsonSerializer.Deserialize<PcuNodeUsage>(payload)
			?? throw new InvalidOperationException("Invalid PCU usage report");
		if (string.IsNullOrEmpty(usage.Origin) || usage.Origin.Length > 128 || usage.Pools == null
			|| usage.Pools.Length > MaxPoolsPerReport
			|| usage.Pools.Any(pool => pool == null || pool.TypeBuilt is { Count: > MaxTypesPerPool }))
			throw new InvalidOperationException("Invalid PCU usage report");
		return usage;
	}

	private static string Snapshot() { lock (Sync) return m_snapshot; }
}
