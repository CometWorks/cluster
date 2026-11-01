using ClusterPlugin;
using HarmonyLib;
using VRage;

namespace Shared.Patches;

/// <summary>
/// The 56-bit unique number becomes a
/// registry-issued sequential range for random and serial IDs alike, so ids are unique cluster-wide
/// by construction. An unconfigured allocator (standalone server, codec tests) keeps the vanilla
/// random/serial IDs; MarkIdUsed stays vanilla and unpatched.
///
/// Every id is handed out as the first of a reserved PAIR, so id+1 is never issued to anyone else.
/// Vanilla assumes it may take id+1 for free: MyCubeBuilder.SpawnDynamicGrid (a block placed in empty
/// space, server branch) does <c>grid.EntityId = AllocateId(); block.EntityId = grid.EntityId + 1;</c>
/// without allocating the second id. With random ids that is harmless; with sequential ranges the next
/// AllocateId returned exactly that block's id, and the first grid split after it (a warhead placed in the
/// air and detonated, w33 2026-09-25 23:25:28) threw DuplicateIdException in CreateGridForSplit and killed
/// node-4 (T-0245). A decompile of Sandbox.Game shows no other id+N assumption; the pair costs half of
/// each 2^24-id range, and ranges are cheap.
/// </summary>
[HarmonyPatch(typeof(MyEntityIdentifier), nameof(MyEntityIdentifier.AllocateId),
    new[] { typeof(MyEntityIdentifier.ID_OBJECT_TYPE), typeof(MyEntityIdentifier.ID_ALLOCATION_METHOD) })]
public static class MyEntityIdentifierPatch
{
    [HarmonyPrefix]
    private static bool AllocateIdPrefix(MyEntityIdentifier.ID_OBJECT_TYPE objectType, ref long __result)
    {
        if (!EntityIdAllocator.Enabled)
            return true;
        __result = MyEntityIdentifier.ConstructId(objectType, EntityIdAllocator.NextIds(2));
        return false;
    }
}
