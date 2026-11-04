using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace Shared.Patches;

// Host-owned preservation only. Optional hints are not an entitlement channel:
// dormant standalone claims/legacy receipts travel with the actual grid stocks.
// The core refuses conflicting positive claims instead of activating Concealment.
[HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.GetObjectBuilder), typeof(bool))]
internal static class ConcealmentMetadataPatch
{
    [HarmonyPostfix]
    private static void Postfix(MyCubeGrid __instance, MyObjectBuilder_EntityBase __result)
    {
        if (ClusterRuntime.IsClustered) OfflineCompensation.Call("PreserveMetadata", __instance, __result);
    }
}
