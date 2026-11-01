using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.World.MyGlobalEvents</c>; installed by
/// <c>Shared.Patches.MyGlobalEventsPatch</c>.
/// </summary>
public static class MyGlobalEventsSeams
{
	// ClusterPlugin seam adapter for MyGlobalEventsPatch.UpdateBeforeSimulationPrefix
	public static Func<bool> ClusterUpdatePrefix;
}
