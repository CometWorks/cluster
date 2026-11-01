using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// Drone spawning is
/// node-local (antennas are blocks, each loaded on exactly one node); the seam skips it on the World
/// Authority, which holds no blocks. The target is a private instance method, hence the string name.
/// Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MyPirateAntennas), "UpdateDroneSpawning")]
public static class MyPirateAntennasPatch
{
    [HarmonyPrefix]
    private static bool UpdateDroneSpawningPrefix()
    {
        var prefix = MyPirateAntennasSeams.ClusterDroneSpawningPrefix;
        return prefix == null || prefix();
    }
}
