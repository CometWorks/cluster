using System;
using System.Linq;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.SessionComponents.MySessionComponentTrash</c>. The
/// close-to-player prefix and the two sweep gates are installed by <c>Shared.Patches.MySessionComponentTrashPatch</c>;
/// the identity helper is written against the publicized private members.
/// </summary>
public static class MySessionComponentTrashSeams
{
	// ClusterPlugin seam adapter for MySessionComponentTrashPatch.IsCloseToPlayerOrCameraPrefix:
	// true = close, false = not close, null = let vanilla decide.
	public static Func<MyEntity, float, bool?> ClusterIsCloseToPlayer;

	// ClusterPlugin patch points: role gates for the trash sweeps (see UpdateAfterSimulation).
	// Grid trash acts on partition-owned entities, which only regular nodes load - the World Authority
	// hosts no partition grids, and what it does host (proxy antennas, previews) it does not own, so its
	// sweep must not close anything. Identities are the opposite: WA-owned global state in a cluster; a
	// regular node sees only its own players and would remove identities that are merely on another node.
	public static Func<bool> ClusterRunsGridTrash;
	public static Func<bool> ClusterRunsIdentityTrash;

	/// <summary>The grid/voxel/forageable sweep gate the transpiler calls: vanilla (true) when unset.</summary>
	public static bool RunsGridTrash()
	{
		var gate = ClusterRunsGridTrash;
		return gate == null || gate();
	}

	/// <summary>The identity sweep gate the transpiler calls: vanilla (true) when unset.</summary>
	public static bool RunsIdentityTrash()
	{
		var gate = ClusterRunsIdentityTrash;
		return gate == null || gate();
	}

	/// <summary>
	/// Cluster test entry: runs the vanilla abandoned-identity removal for one identity NOW, skipping
	/// only the logout-age timer. Identity cleanup is gated to the World Authority in the cluster and
	/// the lane worlds keep RemoveOldIdentitiesH at 0, so the production path would otherwise stay
	/// inert. Same guards as the timer path: never an online player, never one with built blocks.
	/// </summary>
	public static bool ClusterRemoveAbandonedIdentity(this MySessionComponentTrash self, long identityId)
	{
		MyIdentity identity = self.m_allIdentities.FirstOrDefault(candidate => candidate.IdentityId == identityId);
		if (identity == null) return false;
		MySession.Static.Players.TryGetPlayerId(identityId, out MyPlayer.PlayerId playerId);
		if (MySession.Static.Players.TryGetPlayerById(playerId, out var _)) return false;
		return self.TryRemoveAbandonedIdentity(identity, playerId);
	}
}
