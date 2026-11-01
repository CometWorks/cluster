using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.SessionComponents.MySessionComponentContractSystem</c>;
/// installed by <c>Shared.Patches.MySessionComponentContractSystemPatch</c>.
/// </summary>
public static class MySessionComponentContractSystemSeams
{
	// ClusterPlugin seam adapter for MySessionComponentContractSystemPatch.UpdateAfterSimulationPrefix
	public static Func<bool> ClusterUpdatePrefix;
}
