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
/// mid-method gate inside the per-player loop of the internal generator's UpdateBeforeSimulation. The tracker,
/// presence and installation-registry upkeep must run on the regular nodes that hold the installations;
/// spawning a new one is the World Authority's decision, executed on the owning node through the public
/// SpawnAroundPosition. The transpiler inserts <c>if (!UpdateGate()) continue;</c> right after
/// <c>m_playerTracker.AdvanceTimer(key)</c>, so the remaining players' upkeep still runs. The gate reads true
/// while the seam is null, so vanilla is unchanged. The type is internal; the publicized reference resolves it
/// without reflection.
/// </summary>
[HarmonyPatch(typeof(MyPlanetaryEncountersGenerator), nameof(MyPlanetaryEncountersGenerator.UpdateBeforeSimulation))]
public static class MyPlanetaryEncountersGeneratorPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> UpdateBeforeSimulationTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();

        // Anchor: the only AdvanceTimer call in the method; the gate goes right after it.
        int advance = il.IndexOfSingle(original, "call PlayerTracker.AdvanceTimer", ci => ci.CallsNamed("AdvanceTimer"));

        // The loop's continue target: the enumerator condition (the instruction before the single MoveNext call
        // carries the label every `continue` in the loop branches to).
        int moveNext = il.IndexOfSingle(original, "call MoveNext", ci => ci.CallsNamed("MoveNext"));
        GateTranspilerHelpers.Require(moveNext > advance, original, "the loop condition to follow the AdvanceTimer call");
        var condition = il[moveNext - 1];
        GateTranspilerHelpers.Require(condition.labels.Count > 0, original, "a label on the loop condition before MoveNext");
        var continueLabel = condition.labels[0];

        il.InsertRange(advance + 1, new[]
        {
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MyPlanetaryEncountersGeneratorSeams), nameof(MyPlanetaryEncountersGeneratorSeams.UpdateGate))),
            new CodeInstruction(OpCodes.Brfalse, continueLabel),
        });

        return il;
    }
}
