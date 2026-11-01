using ClusterPlugin;
using HarmonyLib;
using VRage.Library.Algorithms;

namespace Shared.Patches;

/// <summary>
/// NetworkIdRangeGenerator needs to override SequenceIdGenerator.NextId/Return, but the
/// stock assembly has them non-virtual, so these prefixes dispatch to the range generator whenever
/// the replication layer's generator instance is one. Every other generator runs vanilla.
/// </summary>
[HarmonyPatch(typeof(SequenceIdGenerator))]
public static class SequenceIdGeneratorPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SequenceIdGenerator.NextId))]
    private static bool NextIdPrefix(SequenceIdGenerator __instance, ref uint __result)
    {
        if (__instance is not NetworkIdRangeGenerator ranged)
            return true;
        __result = ranged.NextId();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SequenceIdGenerator.Return))]
    private static bool ReturnPrefix(SequenceIdGenerator __instance, uint id)
    {
        if (__instance is not NetworkIdRangeGenerator ranged)
            return true;
        ranged.Return(id);
        return false;
    }
}
