using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.SessionComponents;

namespace Shared.Patches;

/// <summary>
/// Initial NPC faction/station generation
/// (BeforeStart) and the economy tick - reputation decay, station restocking, contract generation,
/// faction-balance recovery, currency analysis (UpdateAfterSimulation) - run once, where the seams allow
/// (the World Authority in a cluster). Both prefixes are inert while their seam is null.
/// </summary>
[HarmonyPatch(typeof(MySessionComponentEconomy))]
public static class MySessionComponentEconomyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MySessionComponentEconomy.BeforeStart))]
    private static bool BeforeStartPrefix()
    {
        var prefix = MySessionComponentEconomySeams.ClusterBeforeStartPrefix;
        return prefix == null || prefix();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MySessionComponentEconomy.UpdateAfterSimulation))]
    private static bool UpdateAfterSimulationPrefix()
    {
        var prefix = MySessionComponentEconomySeams.ClusterUpdatePrefix;
        return prefix == null || prefix();
    }
}
