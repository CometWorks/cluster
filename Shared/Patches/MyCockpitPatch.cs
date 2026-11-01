using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using VRage.Game.Entity;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// MyCockpit seams:
///
/// 1. Closed-pilot guard in GetObjectBuilderCubeBlock. A departed player's character can be closed while
///    the cockpit still holds the reference; serializing it NREs on torn-down internals (and would
///    resurrect a phantom pilot on load). Both <c>pilot.Save</c> tests need
///    <c>!Closed &amp;&amp; !MarkedForClose</c>; the transpiler routes the two MyEntity.Save reads of that
///    method through <see cref="PilotSaveable"/>, which applies the same test in cluster mode and is the
///    plain Save read otherwise.
///
/// 3. Torn-down-pilot guard in RemovePilot (T-0042): a closed pilot, or one whose suit battery or radio
///    components are gone, is dropped from the seat the way the vanilla physics-null path drops it, instead
///    of the body dereferencing those components and taking the node down.
///
/// 2. RemovePilot traces. Which RemovePilot path runs (dead / physics-null / transfer / normal) decides
///    whether the control switch back to the character can happen at all, and the seat-exit request is
///    silent end to end. cluster-regression findings/node-3-silent-abort-on-73-and-a-client-triggerable-nre.md
///    diagnosed a client-triggerable NRE inside RemovePilot from the <c>cockpit_remove_pilot_enter</c> line
///    alone ("keep that instrumentation"). The entry line is a prefix; the exit line, which belongs
///    after <c>m_pilot = null</c> in the normal path, is a postfix using the controller state captured
///    at entry - nothing between entry and that point touches ControllerInfo.Controller.
/// </summary>
[HarmonyPatch(typeof(MyCockpit))]
public static class MyCockpitPatch
{
    // ---- 1. closed-pilot guard --------------------------------------------------------------------

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyCockpit.GetObjectBuilderCubeBlock))]
    private static IEnumerable<CodeInstruction> GetObjectBuilderCubeBlockTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getSave = AccessTools.PropertyGetter(typeof(MyEntity), nameof(MyEntity.Save));
        MethodInfo helper = AccessTools.Method(typeof(MyCockpitPatch), nameof(PilotSaveable));
        List<CodeInstruction> code = instructions.ToList();
        int matches = 0;
        foreach (CodeInstruction instruction in code)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                && instruction.operand is MethodInfo method && method == getSave)
            {
                // Same stack shape: the pilot reference in, a bool out. Labels and blocks stay on the instruction.
                instruction.opcode = OpCodes.Call;
                instruction.operand = helper;
                matches++;
            }
        }
        // One read for m_pilot, one for m_savedPilot - the pairing is the anchor.
        if (matches != 2)
            throw new InvalidOperationException(
                $"MyCockpitPatch: expected exactly 2 MyEntity.Save reads in MyCockpit.GetObjectBuilderCubeBlock, found {matches}");
        return code;
    }

    /// <summary>Replaces <c>pilot.Save</c>; vanilla read outside cluster mode.</summary>
    public static bool PilotSaveable(MyEntity pilot)
    {
        if (!ClusterRuntime.IsClustered)
            return pilot.Save;
        return pilot.Save && !pilot.Closed && !pilot.MarkedForClose;
    }

    // ---- 2. RemovePilot traces ----------------------------------------------------------------------

    // Name as a string: with the publicizer the event and its backing field share the name and
    // nameof() is ambiguous.
    private static readonly FieldInfo IsOccupiedChangedField = AccessTools.Field(typeof(MyCockpit), "IsOccupiedChanged");

    public struct RemovePilotState
    {
        public MyCharacter Pilot;
        public bool HadPhysics;
        public bool WasDead;
        public bool ControllerWired;
        public ulong ControllerSteamId;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyCockpit.RemovePilot))]
    private static bool RemovePilotPrefix(MyCockpit __instance, out RemovePilotState __state, ref bool __result)
    {
        __state = default;
        if (!ClusterRuntime.IsClustered || !Sync.IsServer)
            return true;
        MyCharacter pilot = __instance.m_pilot;
        if (pilot == null)
            return true;   // the method's own first early return; the entry line is logged after it
        // ---- 3. torn-down-pilot guard (T-0042) -----------------------------------------------------
        // The vanilla body takes the cheap path only when pilot.Physics == null. A pilot that is
        // closed, or whose suit/radio components are already gone, still has a physics body for a
        // frame or two and then NREs on m_pilot.SuitBattery / RadioBroadcaster / RadioReceiver, which
        // the body dereferences unconditionally. w29 node-4 died that way at 2026-09-05 00:07:11:
        // stock trash removed a grid whose cockpit still held a warm-resume ghost pilot
        // (findings/warm-resume-ghost-pilot-crashes-node-via-stock-trash.md). Same shape the save
        // path already guards against above; here it takes the physics-null path the vanilla code
        // itself uses for a pilot with nothing behind it.
        if (pilot.Closed || pilot.MarkedForClose || pilot.SuitBattery == null
            || pilot.RadioBroadcaster == null || pilot.RadioReceiver == null)
        {
            try
            {
                MyLog.Default?.WriteLine($"cockpit_remove_pilot_torn_down entity={__instance.EntityId} pilot={pilot.EntityId} closed={pilot.Closed} markedForClose={pilot.MarkedForClose} suitBattery={pilot.SuitBattery != null} radio={pilot.RadioBroadcaster != null}");
            }
            catch (Exception)
            {
                // Diagnostics must never be able to take down the game thread.
            }
            __instance.m_pilot = null;
            // The event can only be raised from inside MyCockpit; read the backing delegate.
            (IsOccupiedChangedField?.GetValue(__instance) as Action<Sandbox.ModAPI.IMyCockpit>)?.Invoke(__instance);
            __result = true;
            return false;
        }
        try
        {
            var controller = __instance.ControllerInfo?.Controller;
            __state = new RemovePilotState
            {
                Pilot = pilot,
                HadPhysics = pilot.Physics != null,
                WasDead = pilot.IsDead,
                ControllerWired = controller != null,
                ControllerSteamId = controller?.Player?.Id.SteamId ?? 0,
            };
            MyLog.Default?.WriteLine($"cockpit_remove_pilot_enter entity={__instance.EntityId} pilot={pilot.EntityId} pilotPhysics={__state.HadPhysics} pilotDead={__state.WasDead} transferInProgress={__instance.CubeGrid.IsBlockTrasferInProgress}");
        }
        catch (Exception)
        {
            // Diagnostics must never be able to take down the game thread.
        }
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyCockpit.RemovePilot))]
    private static void RemovePilotPostfix(MyCockpit __instance, RemovePilotState __state)
    {
        // The exit line covers the normal path only: pilot present, with physics, not dead, and
        // actually removed. The dead and physics-null paths null m_pilot too but were not traced there.
        if (__state.Pilot == null || !__state.HadPhysics || __state.WasDead || __instance.m_pilot != null)
            return;
        MyLog.Default?.WriteLine($"cockpit_remove_pilot entity={__instance.EntityId} pilot={__state.Pilot.EntityId} controllerWired={__state.ControllerWired} controller={__state.ControllerSteamId}");
    }
}
