using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.SessionComponents.MySessionComponentEconomy</c>;
/// installed by <c>Shared.Patches.MySessionComponentEconomyPatch</c>.
/// </summary>
public static class MySessionComponentEconomySeams
{
	// ClusterPlugin seam adapters for MySessionComponentEconomyPatch
	public static Func<bool> ClusterBeforeStartPrefix;

	public static Func<bool> ClusterUpdatePrefix;
}
