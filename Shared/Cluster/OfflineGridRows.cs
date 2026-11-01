using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// SE-810: the grids of a partition going offline, for the registry's offline row (ClusterRegistry/OfflineGrids.cs), so plugins
/// can tell a player's grids in offline partitions without loading any. The row's fields: id, n(ame), l(arge), s(tatic),
/// b(locks), x, y, z, o(wners: the big owners).
/// </summary>
internal static class OfflineGridRows
{
	/// <summary>Game thread: the live grids among the members of an offline offer.</summary>
	public static object[] Rows(IEnumerable<MyEntity> members)
	{
		var stations = new HashSet<long>(MySession.Static.Factions.SelectMany(faction => faction.Value.Stations).Select(station => station.StationEntityId));
		return members.OfType<MyCubeGrid>().Select(grid =>
		{
			VRageMath.Vector3D position = grid.PositionComp.GetPosition();
			MyTerminalBlock[] terminals = grid.GetFatBlocks().OfType<MyTerminalBlock>().ToArray();
			return (object)new { id = grid.EntityId, n = grid.DisplayName, l = grid.GridSizeEnum == MyCubeSize.Large, s = grid.IsStatic,
				b = grid.BlocksCount, x = position.X, y = position.Y, z = position.Z, o = grid.BigOwners.ToArray(),
				// The blocks' mass, the same for a static grid, whose physics has none, and a movable one: no cargo or tank fill
				m = grid.GetBlocks().Sum(block => (double)block.GetMass()),
				g = MyCubeGridGroups.Static?.Physical.GetGroup(grid)?.Nodes.Min(node => node.NodeData.EntityId) ?? grid.EntityId,
				f = terminals.Any(block => block.IsFunctional), p = terminals.Any(Producing), t = stations.Contains(grid.EntityId) };
		}).ToArray();
	}

	private static bool Producing(MyTerminalBlock block) => block.IsFunctional && block switch
	{
		MyBatteryBlock battery => battery.CurrentStoredPower > 0,
		MyFueledPowerProducer fueled => fueled.SourceComp.HasCapacityRemainingByType(MyResourceDistributorComponent.ElectricityId),
		_ => block is Sandbox.ModAPI.Ingame.IMyPowerProducer,
	};

	/// <summary>Any thread: what a save can tell, for a row that went offline before the offer carried them. The rest stays unknown.</summary>
	public static object[] Rows(IEnumerable<MyObjectBuilder_EntityBase> entities) => entities.OfType<MyObjectBuilder_CubeGrid>().Select(grid =>
	{
		VRageMath.Vector3D position = grid.PositionAndOrientation?.Position ?? VRageMath.Vector3D.Zero;
		return (object)new { id = grid.EntityId, n = grid.DisplayName, l = grid.GridSizeEnum == MyCubeSize.Large, s = grid.IsStatic,
			b = grid.CubeBlocks.Count, x = position.X, y = position.Y, z = position.Z, o = BigOwners(grid) };
	}).ToArray();

	/// <summary>As the game counts them: the owners with the most blocks.</summary>
	internal static long[] BigOwners(MyObjectBuilder_CubeGrid grid)
	{
		var counts = new Dictionary<long, int>();
		foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			if (block.Owner != 0)
				counts[block.Owner] = counts.GetValueOrDefault(block.Owner) + 1;
		if (counts.Count == 0) return Array.Empty<long>();
		int most = counts.Values.Max();
		return counts.Where(x => x.Value == most).Select(x => x.Key).ToArray();
	}
}
