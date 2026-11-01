using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using SpaceEngineers.Game.SessionComponents;

namespace Shared.Patches;

/// <summary>
/// A
/// mid-method gate, not a whole-method prefix. The upkeep at the top of UpdateBeforeSimulation (queued spawn
/// execution, removal timers, GPS countdowns, expired-encounter removal) must run on the regular nodes that
/// hold the encounters; only the decision to spawn something new is the World Authority's, which drives
/// owning-node execution through <see cref="MyGlobalEncountersGeneratorSeams.ClusterSpawnGlobalEncounter"/>.
/// The transpiler inserts <c>if (!UpdateGate()) return;</c> immediately before the encounter-cap / spawn-timer
/// check. The gate reads true while the seam is null, so vanilla is unchanged.
/// </summary>
[HarmonyPatch(typeof(MyGlobalEncountersGenerator), nameof(MyGlobalEncountersGenerator.UpdateBeforeSimulation))]
public static class MyGlobalEncountersGeneratorPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> UpdateBeforeSimulationTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();

        // Anchor: the only read of m_activeEncounters in this method is the cap check
        //   if (MySession.Static.Settings.GlobalEncounterCap <= m_activeEncounters || ...) return;
        int active = il.IndexOfSingle(original, "ldfld m_activeEncounters", ci => ci.LoadsFieldNamed("m_activeEncounters"));
        int start = active - 4;
        GateTranspilerHelpers.Require(start >= 0
            && il[active - 1].IsLdarg(0)
            && il[active - 2].LoadsFieldNamed("GlobalEncounterCap")
            && il[active - 3].LoadsFieldNamed("Settings")
            && il[start].CallsNamed("get_Static"),
            original, "the cap check to read MySession.Static.Settings.GlobalEncounterCap <= this.m_activeEncounters");

        var ret = il.LabelOnLastRet(generator, original);
        var gate = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MyGlobalEncountersGeneratorSeams), nameof(MyGlobalEncountersGeneratorSeams.UpdateGate)))
            .MoveLabelsFrom(il[start]);
        il.InsertRange(start, new[]
        {
            gate,
            new CodeInstruction(OpCodes.Brfalse, ret),
        });

        return il;
    }
}
