using System.Runtime.CompilerServices;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage;

namespace Shared.Patches;

/// <summary>
/// EntityIdAllocator, remap half: MyEntityIdRemapHelper draws ids
/// 100 at a time through MyEntityIdBatch instead of one allocator reservation per remapped entity
/// (blueprint/projector/paste). The batch lives in a ConditionalWeakTable entry per
/// helper. Vanilla path while the allocator is not enabled.
/// </summary>
[HarmonyPatch(typeof(MyEntityIdRemapHelper))]
public static class MyEntityIdRemapHelperPatch
{
    private sealed class Batch
    {
        public MyEntityIdBatch Ids = new MyEntityIdBatch();
    }

    private static readonly ConditionalWeakTable<MyEntityIdRemapHelper, Batch> Batches = new();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyEntityIdRemapHelper.RemapEntityId))]
    private static bool RemapEntityIdPrefix(MyEntityIdRemapHelper __instance, long oldEntityId, ref long __result)
    {
        if (!EntityIdAllocator.Enabled)
            return true;
        // MyEntities keeps ONE static helper, and PasteGridData.TryPasteGrid remaps on a parallel worker while
        // the game thread can remap too (projector, merge, prefab). The batch hands out ids with a plain
        // m_next++, so two threads in it at once could take the same id: serialise on the helper's batch.
        var batch = Batches.GetOrCreateValue(__instance);
        lock (batch)
        {
            var map = __instance.m_oldToNewMap;
            if (!map.TryGetValue(oldEntityId, out var value))
            {
                value = batch.Ids.Next();
                map.Add(oldEntityId, value);
            }
            __result = value;
        }
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyEntityIdRemapHelper.Clear))]
    private static void ClearPostfix(MyEntityIdRemapHelper __instance)
    {
        if (Batches.TryGetValue(__instance, out var batch))
            lock (batch) batch.Ids.Clear();
    }
}
