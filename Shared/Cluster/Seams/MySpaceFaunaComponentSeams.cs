using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>SpaceEngineers.Game.AI.MySpaceFaunaComponent</c>; installed by
/// <c>Shared.Patches.MySpaceFaunaComponentPatch</c>.
/// </summary>
public static class MySpaceFaunaComponentSeams
{
	// ClusterPlugin seam adapter for MySpaceFaunaComponentPatch.UpdateAfterSimulationPrefix
	public static Func<bool> ClusterUpdatePrefix;
}
