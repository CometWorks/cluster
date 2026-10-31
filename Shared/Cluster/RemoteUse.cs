using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>Game-thread snapshot of vanilla's remote terminal/camera pins and remote controllers.
/// No lease: closing the UI, releasing control or disconnecting removes presence on the next pass.</summary>
internal static class RemoteUse
{
	internal static bool BlocksProfiling(IEnumerable<long> grids)
	{
		var ids = grids.ToHashSet();
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server) return false;
		return server.m_clientStates.Values.Any(client => client.PermanentReplicables.Keys.OfType<MyCubeGridReplicable>()
			.Any(pin => pin.Instance != null && ids.Contains(pin.Instance.EntityId)))
			|| RemotePullService.WaitingUsers().Any(p => ids.Contains(p.Entity));
	}

	internal static Dictionary<ulong, ulong[]> Capture(IEnumerable<PartitionManager.Partition> partitions)
	{
		var result = new Dictionary<ulong, HashSet<ulong>>();
		var memberPartition = partitions.SelectMany(p => p.Members.Select(id => (id, p.Id)))
			.ToDictionary(x => x.id, x => x.Id);
		if (MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server) return new();
		foreach (var pair in server.m_clientStates)
		{
			ulong user = pair.Key.Id.Value;
			MySession.Static.Players.TryGetPlayerBySteamId(user, out MyPlayer player);
			long body = player?.Character?.GetTopMostParent()?.EntityId ?? 0;
			void Add(long member)
			{
				if (!memberPartition.TryGetValue(member, out ulong partition)
					|| memberPartition.TryGetValue(body, out ulong home) && home == partition) return;
				if (!result.TryGetValue(partition, out var users)) result[partition] = users = new();
				users.Add(user);
			}
			foreach (var pin in pair.Value.PermanentReplicables.Keys)
				if (pin is MyCubeGridReplicable grid && grid.Instance != null) Add(grid.Instance.EntityId);
			Add(player?.Controller?.ControlledEntity?.Entity?.GetTopMostParent()?.EntityId ?? 0);
		}
		// Admission already checked this demand; protect the short load-to-replicable gap too.
		foreach (var waiting in RemotePullService.WaitingUsers())
			if (memberPartition.TryGetValue(waiting.Entity, out ulong partition))
			{
				if (!result.TryGetValue(partition, out var users)) result[partition] = users = new();
				users.Add(waiting.User);
			}
		return result.ToDictionary(x => x.Key, x => x.Value.OrderBy(id => id).ToArray());
	}
}
