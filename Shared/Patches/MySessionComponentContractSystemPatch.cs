using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.SessionComponents;

namespace Shared.Patches;

/// <summary>
/// MySessionComponentContractSystemPatch.UpdateAfterSimulationPrefix:
/// contract timers, lifecycle state changes and payouts run once, where the seam allows (the World
/// Authority in a cluster). BeforeStart stays vanilla on every role. Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MySessionComponentContractSystem), nameof(MySessionComponentContractSystem.UpdateAfterSimulation))]
public static class MySessionComponentContractSystemPatch
{
    [HarmonyPrefix]
    private static bool UpdateAfterSimulationPrefix()
    {
        var prefix = MySessionComponentContractSystemSeams.ClusterUpdatePrefix;
        return prefix == null || prefix();
    }
}
