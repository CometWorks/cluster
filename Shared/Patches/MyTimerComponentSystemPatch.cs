using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.EntityComponents.Systems;
using Shared.Tools;

namespace Shared.Patches;

/// <summary>
/// Dormant-entity guard, timer half. Timer registration happens in
/// OnAddedToContainer during entity Init, before the entity is Added; a dormant (staged-import)
/// entity's session-updated timer must not tick or fire. The two private update loops test
/// <c>item.IsSessionUpdateEnabled</c> before <c>item.Update()</c>; the transpiler routes that test
/// through ShouldUpdate, which adds the InScene condition. Exactly one test per loop, or the patch
/// fails. Unchanged on an unclustered process.
/// </summary>
[HarmonyPatch]
public static class MyTimerComponentSystemPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(MyTimerComponentSystem), "UpdateTimerComponents10")
                     ?? throw new MissingMethodException(nameof(MyTimerComponentSystem), "UpdateTimerComponents10");
        yield return AccessTools.DeclaredMethod(typeof(MyTimerComponentSystem), "UpdateTimerComponents100")
                     ?? throw new MissingMethodException(nameof(MyTimerComponentSystem), "UpdateTimerComponents100");
    }

    /// <summary>Transpiler call site replacing the IsSessionUpdateEnabled read.</summary>
    public static bool ShouldUpdate(MyTimerComponent timer)
    {
        if (!timer.IsSessionUpdateEnabled)
            return false;
        if (!ClusterRuntime.IsClustered)
            return true;
        var entity = timer.Entity;
        // T-0392: not while the entity's sliced import activation is open - FramesFromLastTrigger is a
        // round-trip field and the source froze it; advancing it between slices is a residual.
        return entity == null || (entity.InScene && !ImportActivationGuard.IsOpenFor(GuardUser.Timer, entity as VRage.Game.Entity.MyEntity));
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var isSessionUpdateEnabled = AccessTools.PropertyGetter(typeof(MyTimerComponent), nameof(MyTimerComponent.IsSessionUpdateEnabled));
        var shouldUpdate = AccessTools.Method(typeof(MyTimerComponentSystemPatch), nameof(ShouldUpdate));

        var reads = il.FindAllIndex(ci => ci.Calls(isSessionUpdateEnabled));
        if (reads.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one IsSessionUpdateEnabled read, found {reads.Count}");

        var read = il[reads[0]];
        read.opcode = OpCodes.Call;
        read.operand = shouldUpdate;
        return il;
    }
}
