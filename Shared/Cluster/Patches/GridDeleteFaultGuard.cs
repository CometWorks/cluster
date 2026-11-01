using System;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// #1295: a grid whose vanilla <c>MyCubeGrid.BeforeDelete</c> throws must not end the node in the middle of a
/// cluster close.
///
/// octahedron-1191, 2026-10-21: four graceful cluster stops in a row (04:34, 04:43, 05:13, 05:26Z) lost a regular
/// node half way through its drain. Each time the drain had just registered partition 651's Final and closed its
/// members ('Wrecks-88', 6090 blocks; 'Nova Voyager-Station-Updated', 4017 blocks), on three different nodes:
///
///     NullReferenceException at MyCubeGrid.BeforeDelete_Patch1 &lt;- MyEntity.Delete
///         &lt;- MyEntities.DeleteRememberedEntities &lt;- PartitionPersistence.CloseWithHierarchy &lt;- UpdateDrain
///
/// The exception unwound the game loop, the DS ran its clean shutdown without a word in the game log, the node
/// never reported Empty, its lease expired, and the registry recorded it Dead: "node:local-node-N" in the
/// CleanShutdown snapshot's Dirty list, which Quasar takes as "no clean shutdown proof" and refuses every update.
///
/// The null itself is not known yet: the top frame is BeforeDelete's Harmony replacement, so a null receiver in
/// it or in any callee the JIT inlined into it reads the same. This guard turns the next occurrence into a
/// diagnosis (<see cref="Probe"/> names every candidate) and lets the close finish. Catching higher up, around
/// DeleteRememberedEntities, is not enough: a Delete that throws leaves the grid in the delete set and the next
/// frame's DeleteRememberedEntities calls the same throwing Delete again.
///
/// What a swallowed fault skips is the rest of BeforeDelete (unregistering the blocks from the grid systems, the
/// block maps' clears). MyEntity.Delete itself goes on: the grid leaves the scene, its physics and its updates.
/// The content is not lost: a cluster close only runs on content that lives on in a save or on another node.
/// Only closes the cluster makes (<see cref="ClusterCloseScope"/>) are guarded; a vanilla close fails as before.
/// </summary>
[HarmonyPatch(typeof(MyCubeGrid), "BeforeDelete")]
internal static class GridDeleteFaultGuard
{
	private static long s_faults;

	public static long Faults => System.Threading.Interlocked.Read(ref s_faults);

	/// <summary>CLUSTER_GRID_DELETE_GUARD=0 leaves the fault to end the node, as vanilla (the red arm).</summary>
	[HarmonyPrepare]
	private static bool Prepare() => Environment.GetEnvironmentVariable("CLUSTER_GRID_DELETE_GUARD") != "0";

	[HarmonyFinalizer]
	[HarmonyPriority(Priority.Last)]
	private static Exception Finalizer(MyCubeGrid __instance, Exception __exception)
	{
		if (__exception == null || !ClusterCloseScope.Active) return __exception;
		long faults = System.Threading.Interlocked.Increment(ref s_faults);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] grid delete fault: BeforeDelete of grid " + __instance?.EntityId
			+ " '" + __instance?.DisplayName + "' threw during a cluster close; the close goes on without the rest of the"
			+ " vanilla cleanup (#1295, fault " + faults + "): " + Probe(__instance) + "; " + __exception);
		return null;
	}

	/// <summary>Every receiver BeforeDelete and its small callees dereference without a frame of their own.</summary>
	internal static string Probe(MyCubeGrid grid)
	{
		if (grid == null) return "grid=null";
		try
		{
			MySession session = MySession.Static;
			MySlimBlock[] blocks = grid.m_cubeBlocks?.ToArray() ?? Array.Empty<MySlimBlock>();
			int unknownBuilder = 0, nullLimits = 0;
			foreach (MySlimBlock block in blocks)
			{
				if (block == null || block.BuiltBy == 0 || session?.Players == null) continue;
				MyIdentity identity = session.Players.TryGetIdentity(block.BuiltBy);
				if (identity == null) unknownBuilder++;
				else if (identity.BlockLimits == null) nullLimits++;
			}
			return "blocks=" + blocks.Length
				+ " nullBlocks=" + blocks.Count(x => x == null)
				+ " nullDefinition=" + blocks.Count(x => x != null && x.BlockDefinition == null)
				+ " nullComponentStack=" + blocks.Count(x => x != null && x.ComponentStack == null)
				+ " foreignCubeGrid=" + blocks.Count(x => x != null && x.CubeGrid != grid)
				+ " unknownBuilder=" + unknownBuilder + " nullIdentityLimits=" + nullLimits
				+ " gridSystems=" + (grid.GridSystems != null)
				+ " fatBlocks=" + (grid.m_fatBlocks != null) + " cubes=" + (grid.m_cubes != null)
				+ " targetingList=" + (grid.m_targetingList != null) + " occupied=" + (grid.m_occupiedBlocks != null)
				+ " session=" + (session != null) + " settings=" + (session?.Settings != null)
				+ " sessionLimits=" + (session?.SessionBlockLimits != null) + " npcLimits=" + (session?.NPCBlockLimits != null)
				+ " marked=" + grid.MarkedForClose + " closed=" + grid.Closed;
		}
		catch (Exception probe)
		{
			return "probe failed: " + probe.GetType().Name + ": " + probe.Message;
		}
	}
}
