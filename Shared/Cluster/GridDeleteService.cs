using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Admin removal of an unpiloted grid on the node that owns it (control route <c>/cluster/grid-delete</c>, #1334).
///
/// The octahedron load fleet leaves the ships of recycled and killed pilot clients where they stopped, and
/// every pilot flies to the same lane approach points: 191 'Ion Pilot-N' ships against 43-86 active pilots,
/// and 4 of 13 lane legs aborted on a leftover 1-7 m from the start. The Remote plugin's close_grid only
/// hides a grid from the calling client (T-0109). The owning node removes it the way the vanilla trash
/// collector does (<c>MySessionComponentTrash.RemoveGrid</c>: Close on the game thread), which every regular
/// node already does for trash: partition membership, the next partition save and the registry follow the
/// close as they do for a trashed grid.
///
/// Refusals mirror <see cref="GridMoveService"/> - a grid someone is sitting in, a grid a player controls,
/// a grid mechanically attached to others, a grid frozen for a handover - plus two of its own:
/// <paramref name="name"/> must equal the grid's display name, so an id read from an older partitions
/// report never removes something else, and a grid inside its import grace (its links are not re-formed
/// yet, so "attached" cannot be read) is left alone, as the trash leaves it. That is the first
/// <c>gridDelete.linkSettleSeconds</c> of an arrival only, not the rest of a recovery load's longer trash grace (SE-1317).
///
/// A grid alone in its partition (the other members already closed by earlier deletes count as gone) empties it, and an emptied partition is never saved again: its newest save
/// would still hold the grid and the next recovery of the row would bring it back. Such a delete also queues
/// the partition's empty Final and releases it (<see cref="PartitionPersistence.ReleaseEmptied"/>), so the
/// registry reaps the row; it is refused while that cannot be done (a registry request or a save in flight).
/// </summary>
internal static class GridDeleteService
{
	public static object Delete(long entityId, string name, PartitionManager partitions, PartitionPersistence persistence)
	{
		if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCubeGrid grid)
			return new { deleted = false, entityId, reason = "not a grid on this node" };
		if (grid.Closed || grid.MarkedForClose)
			return new { deleted = false, entityId, reason = "grid is closing" };
		if (grid.DisplayName != name)
			return new { deleted = false, entityId, reason = "name does not match", gridName = grid.DisplayName };
		if (grid.GetTopMostParent() != grid)
			return new { deleted = false, entityId, reason = "not a top-level grid" };
		if (ClusterRuntime.IsFrozen(grid.EntityId))
			return new { deleted = false, entityId, reason = "frozen for a handover in progress" };
		if (MySessionComponentTrashPatch.LinksSettling(grid.EntityId))
			return new { deleted = false, entityId, reason = "arrived recently (import grace)" };
		long[] pilots = grid.GetFatBlocks<MyCockpit>()
			.Where(cockpit => cockpit.Pilot != null)
			.Select(cockpit => cockpit.Pilot.EntityId).ToArray();
		if (pilots.Length > 0)
			return new { deleted = false, entityId, reason = "a cockpit holds a pilot", pilots };
		long[] controllers = MySession.Static.Players.GetOnlinePlayers()
			.Where(player => player.Controller?.ControlledEntity?.Entity?.GetTopMostParent() == grid)
			.Select(player => player.Identity.IdentityId).ToArray();
		if (controllers.Length > 0)
			return new { deleted = false, entityId, reason = "a player controls it", controllers };
		var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
		if (group != null && group.Nodes.Count > 1)
			return new { deleted = false, entityId, reason = "mechanically attached to other grids",
				attached = group.Nodes.Select(node => node.NodeData.EntityId).Where(id => id != entityId).ToArray() };
		ulong partition = partitions.Holder(entityId);
		bool alone = partition != 0 && AloneIn(partitions.Peek(partition), entityId);
		if (alone && (persistence == null ? "the node has no partition persistence yet" : persistence.EmptyReleaseBlocker(partition)) is { } blocker)
			return new { deleted = false, entityId, reason = "alone in partition " + partition + " and " + blocker };
		var position = grid.PositionComp.GetPosition();
		int blocks = grid.BlocksCount;
		MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] grid-delete removed grid '{name}' with entity id '{entityId}'"
			+ $" ({blocks} blocks at {position.X:F0},{position.Y:F0},{position.Z:F0})");
		grid.Close();
		string release = alone ? persistence.ReleaseEmptied(partition) ?? "queued" : "not emptied";
		return new { deleted = true, entityId, name, blocks, position = new[] { position.X, position.Y, position.Z }, partition, emptyRelease = release };
	}

	/// <summary>
	/// SE-1317: no other member of <paramref name="partition"/> is still live. The member set is the standing layout,
	/// which drops a closed grid only at the next topology sweep, so a grid an earlier delete closed a moment ago still
	/// counts there. Deleting a partition's 3 ships in a row answered "not emptied" for each: none queued the empty
	/// Final, the partition went offline with its older save and the next wake loaded all three back (4426 and 4976 on
	/// octahedron-test, 2026-11-09). A member the census cannot see yet (a load in progress) counts as live.
	/// </summary>
	internal static bool AloneIn(PartitionManager.Partition partition, long entityId)
	{
		if (partition.HasPending) return partition.Members.Count == 1 && partition.Members.Contains(entityId);
		foreach (long member in partition.Members)
			if (member != entityId && MyEntities.TryGetEntityById(member, out MyEntity other) && !other.Closed && !other.MarkedForClose)
				return false;
		return true;
	}
}
