using System.Linq;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Admin relocation of an unpiloted grid on this node (control route <c>/cluster/grid-move</c>).
///
/// The regression harness never deletes a wreck (Viktor, 2026-08-14: a wreck may be MOVED but never
/// DELETED), so a ship that dies on its berth stays there and the berth is lost for the rest of the
/// run. w29 measured 51.7 % of replacement pastes ending on the last-resort spot because every berth
/// held a wreck (T-0037). The client plugin cannot move a grid it does not control - the only vanilla
/// teleport a client can raise is for the entity it is controlling - so the move is done here, on the
/// node that owns the grid, on the game thread, with the vanilla <see cref="MyCubeGrid.Teleport"/>.
///
/// Refusals are deliberate and narrow: a station, a grid someone is sitting in, a grid that is
/// mechanically attached to others (Teleport moves the whole physical group and would drag the
/// platform along with a connector-locked wreck), and a grid frozen for a handover.
/// </summary>
public static class GridMoveService
{
	public static object Move(long entityId, double x, double y, double z)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { moved = false, entityId, reason = "not a grid on this node" };
		if (grid.Closed || grid.MarkedForClose)
			return new { moved = false, entityId, reason = "grid is closing" };
		if (grid.IsStatic)
			return new { moved = false, entityId, reason = "static grid; stations are never moved" };
		if (grid.GetTopMostParent() != grid)
			return new { moved = false, entityId, reason = "not a top-level grid" };
		if (ClusterRuntime.IsFrozen(grid.EntityId))
			return new { moved = false, entityId, reason = "frozen for a handover in progress" };
		long[] pilots = grid.GetFatBlocks<MyCockpit>()
			.Where(cockpit => cockpit.Pilot != null)
			.Select(cockpit => cockpit.Pilot.EntityId).ToArray();
		if (pilots.Length > 0)
			return new { moved = false, entityId, reason = "a cockpit holds a pilot", pilots };
		var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
		if (group != null && group.Nodes.Count > 1)
			return new { moved = false, entityId, reason = "mechanically attached to other grids",
				attached = group.Nodes.Select(node => node.NodeData.EntityId).Where(id => id != entityId).ToArray() };
		Vector3D from = grid.PositionComp.GetPosition();
		MatrixD matrix = grid.WorldMatrix;
		matrix.Translation = new Vector3D(x, y, z);
		grid.Physics?.ClearSpeed();
		grid.Teleport(matrix);
		Vector3D to = grid.PositionComp.GetPosition();
		return new
		{
			moved = true,
			entityId,
			blocks = grid.BlocksCount,
			from = new[] { from.X, from.Y, from.Z },
			to = new[] { to.X, to.Y, to.Z },
		};
	}
}
