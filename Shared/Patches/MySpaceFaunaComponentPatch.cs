using ClusterPlugin;
using HarmonyLib;
using SpaceEngineers.Game.AI;

namespace Shared.Patches;

/// <summary>
/// Fauna is
/// node-local (it spawns near this node's own players and its bots tick here); the seam skips it on the
/// World Authority, which hosts no players. Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MySpaceFaunaComponent), nameof(MySpaceFaunaComponent.UpdateAfterSimulation))]
public static class MySpaceFaunaComponentPatch
{
    [HarmonyPrefix]
    private static bool UpdateAfterSimulationPrefix()
    {
        var prefix = MySpaceFaunaComponentSeams.ClusterUpdatePrefix;
        return prefix == null || prefix();
    }
}
