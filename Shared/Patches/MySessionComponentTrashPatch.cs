using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.SessionComponents;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// MySessionComponentTrashPatch.
/// <list type="bullet">
/// <item>IsCloseToPlayerOrCameraPrefix: a grid near a player hosted by ANOTHER node counts as close, so no
/// node trash-collects it; on a node without a controlling player of its own, a cluster player elsewhere
/// makes a far grid "not close" (#1307), as one server with that player online would answer. Otherwise
/// the original runs unchanged.</item>
/// <item>UpdateAfterSimulation gates (transpiler): grid trash, voxel revert and forageable revert act on
/// partition-owned state and run only where <see cref="MySessionComponentTrashSeams.ClusterRunsGridTrash"/>
/// allows (regular nodes); identity cleanup is WA-owned global state and runs only where
/// <see cref="MySessionComponentTrashSeams.ClusterRunsIdentityTrash"/> allows. Both gates read true while
/// their seam is null, so vanilla is unchanged.</item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(MySessionComponentTrash))]
public static class MySessionComponentTrashPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MySessionComponentTrash.IsCloseToPlayerOrCamera), new[] { typeof(MyEntity), typeof(float) })]
    private static bool IsCloseToPlayerOrCameraPrefix(MyEntity entity, float distanceThreshold, ref bool __result)
    {
        bool? close = MySessionComponentTrashSeams.ClusterIsCloseToPlayer?.Invoke(entity, distanceThreshold);
        if (close == null)
            return true;
        __result = close.Value;
        return false;
    }

    // Vanilla:
    //   m_gridsToCheck.List.ApplyChanges();
    //   if (TrashRemovalEnabled && (IsDedicated || !IsCameraUserAnySpectator()))
    //   {
    //       [grid sweep: closure alloc, trashFound, m_gridsToCheck.Update/Iterate, hysteresis]
    //       CheckIdentitiesTrash();
    //   }
    //   VoxelRevertor_Update();
    //   RevertForageableItems();
    // Intended shape:
    //   if (RunsGridTrash()) { [grid sweep] }        -> gate 1 at the if-body start, skipping to gate 2
    //   if (RunsIdentityTrash()) CheckIdentitiesTrash(); -> gate 2 before the call, skipping to gate 3
    //   if (RunsGridTrash()) { VoxelRevertor_Update(); RevertForageableItems(); } -> gate 3, skipping to ret
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MySessionComponentTrash.UpdateAfterSimulation))]
    private static IEnumerable<CodeInstruction> UpdateAfterSimulationTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();
        var gridGate = AccessTools.Method(typeof(MySessionComponentTrashSeams), nameof(MySessionComponentTrashSeams.RunsGridTrash));
        var identityGate = AccessTools.Method(typeof(MySessionComponentTrashSeams), nameof(MySessionComponentTrashSeams.RunsIdentityTrash));

        int checkIdentities = il.IndexOfSingle(original, "call CheckIdentitiesTrash", ci => ci.CallsNamed("CheckIdentitiesTrash"));
        int voxelRevert = il.IndexOfSingle(original, "call VoxelRevertor_Update", ci => ci.CallsNamed("VoxelRevertor_Update"));
        GateTranspilerHelpers.Require(checkIdentities > 1 && il[checkIdentities - 1].IsLdarg(0), original, "ldarg.0 before CheckIdentitiesTrash");
        GateTranspilerHelpers.Require(voxelRevert == checkIdentities + 2 && il[voxelRevert - 1].IsLdarg(0), original, "VoxelRevertor_Update to directly follow CheckIdentitiesTrash");

        // The if's false branches all land on the ldarg.0 of VoxelRevertor_Update(); the if-body starts right
        // after the last of those branches.
        var afterIf = il[voxelRevert - 1];
        GateTranspilerHelpers.Require(afterIf.labels.Count > 0, original, "the skip label of the trash-removal if on VoxelRevertor_Update");
        var skipLabels = afterIf.labels.ToList();
        int bodyStart = -1;
        for (int i = 0; i < checkIdentities; i++)
        {
            if (il[i].operand is Label target && skipLabels.Contains(target))
                bodyStart = i + 1;
        }
        GateTranspilerHelpers.Require(bodyStart > 0 && bodyStart < checkIdentities - 1, original, "a branch to the skip label before the trash sweep");
        GateTranspilerHelpers.Require(il[bodyStart].opcode == OpCodes.Newobj, original, "the trash sweep to start with its closure allocation (newobj)");

        // Gate 3 (before VoxelRevertor_Update): !RunsGridTrash -> ret. Takes over the if's skip labels.
        var ret = il.LabelOnLastRet(generator, original);
        var gate3 = new CodeInstruction(OpCodes.Call, gridGate).MoveLabelsFrom(afterIf);
        il.InsertRange(voxelRevert - 1, new[] { gate3, new CodeInstruction(OpCodes.Brfalse, ret) });

        // Gate 2 (before CheckIdentitiesTrash): !RunsIdentityTrash -> gate 3. The ldarg.0 it precedes is the
        // join point of the OptimalGridCount if/else chain (every false branch lands on it), so the gate must
        // take over those labels or the branches bypass it - which is exactly what removed the template's
        // character on a regular node (trash collector, 8 ms after character_seen).
        var gate2 = new CodeInstruction(OpCodes.Call, identityGate).MoveLabelsFrom(il[checkIdentities - 1]);
        var gate2Label = gate2.AddLabel(generator);
        il.InsertRange(checkIdentities - 1, new[] { gate2, new CodeInstruction(OpCodes.Brfalse, skipLabels[0]) });

        // Gate 1 (if-body start): !RunsGridTrash -> gate 2. Takes over the if's body labels.
        var gate1 = new CodeInstruction(OpCodes.Call, gridGate).MoveLabelsFrom(il[bodyStart]);
        il.InsertRange(bodyStart, new[] { gate1, new CodeInstruction(OpCodes.Brfalse, gate2Label) });

        return il;
    }
}
