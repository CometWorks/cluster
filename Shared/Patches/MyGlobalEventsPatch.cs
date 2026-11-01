using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// The global event
/// scheduler runs only where the seam allows (the World Authority in a cluster), so regular nodes never
/// make duplicate cargo-ship / meteor event decisions. Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MyGlobalEvents), nameof(MyGlobalEvents.UpdateBeforeSimulation))]
public static class MyGlobalEventsPatch
{
    [HarmonyPrefix]
    private static bool UpdateBeforeSimulationPrefix()
    {
        var prefix = MyGlobalEventsSeams.ClusterUpdatePrefix;
        return prefix == null || prefix();
    }
}
