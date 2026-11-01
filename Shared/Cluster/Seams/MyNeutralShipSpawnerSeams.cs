using System;
using Sandbox.Definitions;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.World.MyNeutralShipSpawner</c>; the two injection points in
/// the private static <c>Spawn</c> are installed by <c>Shared.Patches.MyNeutralShipSpawnerPatch</c> (transpiler).
/// </summary>
public static class MyNeutralShipSpawnerSeams
{
	// ClusterPlugin seam: on the World Authority the online-player list is empty, so vanilla could never pick a
	// target and no cargo ship ever spawned in a cluster. The handoff lets the WA dispatch the decided spawn group
	// to the node owning a cluster player instead; returning true means the spawn was handed off (or rescheduled).
	public static Func<MyGlobalEventBase, MySpawnGroupDefinition, bool> ClusterSpawnHandoff;

	// ClusterPlugin seam: the tracked-ship count is the observable spawn signal (the prefab spawn completes in an
	// asynchronous callback, so the count moves after the dispatching call returns).
	public static int ClusterShipsInProgress() => MyNeutralShipSpawner.m_shipsInProgress.Count;

	// ClusterPlugin seam: the executing node pins the WA-chosen target so the dispatch is deterministic instead of
	// re-rolling a random local player. Zero means vanilla's own random pick. Game-thread only.
	public static ulong ClusterPinnedTarget;

	/// <summary>Transpiler call-in after vanilla's PCU check: true when the cluster took the spawn over.</summary>
	public static bool TryHandoff(object senderEvent, MySpawnGroupDefinition spawnGroup)
	{
		var handoff = ClusterSpawnHandoff;
		return handoff != null && handoff(senderEvent as MyGlobalEventBase, spawnGroup);
	}

	/// <summary>Transpiler call-in after vanilla dropped the character-less players from its buffer.</summary>
	public static void ApplyPinnedTarget()
	{
		if (ClusterPinnedTarget != 0)
		{
			MyNeutralShipSpawner.m_playersBuffer.RemoveAll((MyPlayer x) => x.Id.SteamId != ClusterPinnedTarget);
		}
	}
}
