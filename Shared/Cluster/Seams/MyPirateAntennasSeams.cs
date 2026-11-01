using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.World.MyPirateAntennas</c>; installed by
/// <c>Shared.Patches.MyPirateAntennasPatch</c>.
/// </summary>
public static class MyPirateAntennasSeams
{
	// ClusterPlugin seam adapter for MyPirateAntennasPatch.UpdateDroneSpawningPrefix
	public static Func<bool> ClusterDroneSpawningPrefix;
}
