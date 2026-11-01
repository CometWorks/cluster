using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Shared.Tools;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// Partitioning 6.4 step 1 - a frozen
/// partition's entities must not run game logic, and "children follow parents". Freeze reaches only
/// top-level entities (it disables the grid's physics and clears its NeedsUpdate) but a grid's blocks
/// are entities in their own right with their own update registration, and nothing was holding them.
/// They kept running their once-before-frame initialisation against a grid whose physics was disabled
/// and not in the Havok world: an airtight door built subpart constraints the grid could not accept
/// (native null dereference, SIGSEGV); a rotor top's Init marked its stator for reattach, the reattach
/// could not complete and the block re-serialized with ParentEntityId 0; the gas system never rebuilt
/// its rooms so GetOxygenAmount returned null.
///
/// Defer, do not drop. DispatchOnceBeforeFrame clears its work list at the end of every pass, so an
/// entity merely skipped loses its pending work instead of postponing it. The skip is placed after
/// <c>m_lastUpdateRecord[entity] &amp;= ~BEFORE_NEXT_FRAME</c> and before the entity's
/// NeedsUpdateBeforeNextFrame flag is cleared, so the flag stays standing; the tail after
/// <c>m_entitiesForUpdateOnce.Clear()</c> re-adds the entity and restores the record bit, so a later
/// MarkForUpdate cannot add a second copy.
///
/// Scope: only the once-before-frame dispatch. Recurring UpdateBeforeSimulation/10/100 on a frozen
/// member's blocks is deliberately left alone - those lists persist across passes so they need no
/// deferral machinery, and no defect has been observed through them.
/// </summary>
[HarmonyPatch(typeof(MyParallelEntityUpdateOrchestrator))]
public static class MyParallelEntityUpdateOrchestratorPatch
{
    // Game thread only: DispatchOnceBeforeFrame runs on the update thread and Requeue empties the
    // list at the end of the same pass, so no synchronisation is needed.
    private static readonly List<MyEntity> Deferred = new();

    /// <summary>Loop body seam: true when the entity's once-before-frame work must wait.</summary>
    public static bool Defer(MyParallelEntityUpdateOrchestrator orchestrator, MyEntity entity)
    {
        var defer = MyParallelEntityUpdateOrchestratorSeams.ClusterDeferOnceBeforeFrame;
        if (defer == null || !defer(entity))
            return false;
        Deferred.Add(entity);
        return true;
    }

    /// <summary>
    /// Deferral tail: the list is cleared every pass, so a frozen entity that was merely skipped would
    /// lose its once-before-frame work rather than postpone it. Put it back, and restore the record bit
    /// with it so a later MarkForUpdate does not add a second copy.
    /// </summary>
    public static void Requeue(MyParallelEntityUpdateOrchestrator orchestrator)
    {
        if (Deferred.Count == 0)
            return;
        foreach (var entity in Deferred)
        {
            if (entity.MarkedForClose)
                continue;
            orchestrator.m_lastUpdateRecord[entity] |= MyParallelUpdateFlags.BEFORE_NEXT_FRAME;
            orchestrator.m_entitiesForUpdateOnce.Add(entity);
        }
        Deferred.Clear();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyParallelEntityUpdateOrchestrator.DispatchOnceBeforeFrame))]
    private static IEnumerable<CodeInstruction> DispatchOnceBeforeFrameTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var setRecord = AccessTools.Method(typeof(Dictionary<MyEntity, MyParallelUpdateFlags>), "set_Item");
        var getFlags = AccessTools.PropertyGetter(typeof(MyEntity), nameof(MyEntity.Flags));
        var clear = AccessTools.Method(typeof(List<MyEntity>), nameof(List<MyEntity>.Clear));
        var defer = AccessTools.Method(typeof(MyParallelEntityUpdateOrchestratorPatch), nameof(Defer));
        var requeue = AccessTools.Method(typeof(MyParallelEntityUpdateOrchestratorPatch), nameof(Requeue));

        // Anchor 1: the single m_lastUpdateRecord[myEntity] &= ~BEFORE_NEXT_FRAME store, followed by
        // the entity's Flags read (from which the entity local's load instruction is copied).
        var records = il.FindAllIndex(ci => ci.Calls(setRecord));
        if (records.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one m_lastUpdateRecord store, found {records.Count}");
        var record = records[0];
        if (record + 2 >= il.Count || !il[record + 1].IsLdloc() || !il[record + 2].Calls(getFlags))
            throw new CodeInstructionNotFound($"{original.Name}: the m_lastUpdateRecord store is not followed by the entity's Flags read");
        var loadEntity = il[record + 1];

        // The `continue` target: the first conditional branch after the Flags read is the
        // `if ((flags & NeedsUpdateBeforeNextFrame) != 0)` test, whose false edge is the loop
        // increment (the if is the last statement of the loop body). Verify it lands on `j++`.
        var branch = il.FindIndex(record + 2, ci => ci.opcode == OpCodes.Brfalse || ci.opcode == OpCodes.Brfalse_S);
        if (branch < 0 || il[branch].operand is not Label next)
            throw new CodeInstructionNotFound($"{original.Name}: no conditional branch after the entity's Flags read");
        var target = il.FindIndex(ci => ci.labels.Contains(next));
        if (target < 0 || target + 2 >= il.Count || !il[target].IsLdloc() || il[target + 1].opcode != OpCodes.Ldc_I4_1 || il[target + 2].opcode != OpCodes.Add)
            throw new CodeInstructionNotFound($"{original.Name}: the NeedsUpdateBeforeNextFrame branch does not target the loop increment");

        // Anchor 2: the single m_entitiesForUpdateOnce.Clear() call.
        var clears = il.FindAllIndex(ci => ci.Calls(clear));
        if (clears.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one List<MyEntity>.Clear call, found {clears.Count}");
        var clearAt = clears[0];

        // Tail first so the earlier index stays valid.
        il.InsertRange(clearAt + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, requeue),
        });
        il.InsertRange(record + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(loadEntity.opcode, loadEntity.operand),
            new CodeInstruction(OpCodes.Call, defer),
            new CodeInstruction(OpCodes.Brtrue, next),
        });
        return il;
    }
}
