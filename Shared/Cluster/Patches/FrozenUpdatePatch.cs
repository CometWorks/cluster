using System;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Partitioning 6.4 step 1: a frozen partition's entities must not run game logic, and "children follow
/// parents". Freeze reaches only top-level entities - it disables the grid's physics and clears its
/// NeedsUpdate - but a grid's blocks are entities in their own right with their own update registration,
/// and nothing was holding them. They kept running their once-before-frame initialisation against a grid
/// whose physics was disabled and not in the Havok world, which is one cause wearing three faces:
///
///   - an airtight door built subpart constraints the grid could not accept, and the native call on the
///     runtime that only being added creates dereferenced null (the node died of SIGSEGV);
///   - a rotor top's Init marked its stator for reattach, the reattach could not complete, m_parentBlock
///     stayed null, and the block re-serialized with ParentEntityId 0 - which the handover's round-trip
///     check correctly refused, forever;
///   - the gas system never rebuilt its rooms, so GetOxygenAmount returned null.
///
/// Skipping the update is not enough, it has to be deferred. DispatchOnceBeforeFrame clears its work list
/// at the end of the pass, so an entity merely skipped loses the pending work instead of postponing it -
/// the caller re-registers whatever this defers, and the entity's NeedsUpdateBeforeNextFrame flag is left
/// standing so the work runs on a later frame, once the partition is thawed and the grid is live.
/// </summary>
public static class FrozenUpdatePatch
{
	public static Func<long, bool> IsFrozen { get; set; }

	public static void Install()
	{
		MyParallelEntityUpdateOrchestratorSeams.ClusterDeferOnceBeforeFrame = Defer;
	}

	/// <summary>
	/// True when this entity belongs to a frozen partition and its once-before-frame work must wait. Blocks
	/// resolve to their grid: the freeze is recorded against the top-level entity, which is the partition
	/// member, and a block is only ever frozen by way of its parent.
	/// </summary>
	public static bool Defer(MyEntity entity)
	{
		if (IsFrozen == null || entity == null || entity.MarkedForClose) return false;
		MyEntity root = entity.GetTopMostParent() ?? entity;
		return IsFrozen(root.EntityId);
	}
}
