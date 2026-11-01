using System;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator.
/// Installed into DispatchOnceBeforeFrame by Shared.Patches.MyParallelEntityUpdateOrchestratorPatch.
/// </summary>
public static class MyParallelEntityUpdateOrchestratorSeams
{
	/// <summary>
	/// Cluster seam: true when an entity belongs to a partition frozen for a transfer, so its
	/// once-before-frame work must wait for the thaw instead of running against a grid whose physics is
	/// disabled. Deferred, not dropped - see the re-registration at the end of DispatchOnceBeforeFrame.
	/// </summary>
	public static Func<MyEntity, bool> ClusterDeferOnceBeforeFrame;
}
