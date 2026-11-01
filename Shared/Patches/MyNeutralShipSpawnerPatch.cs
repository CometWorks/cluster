using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;
using Shared.Tools;

namespace Shared.Patches;

/// <summary>
/// MyNeutralShipSpawner seams (the diff's "ClusterPlugin seam" comments are the spec; WA-3 cargo ships).
/// Two call-ins inside the private static <c>Spawn(object senderEvent, MySpawnGroupDefinition spawnGroup)</c>:
/// <list type="bullet">
/// <item>after vanilla's PCU check: <c>if (TryHandoff(senderEvent, spawnGroup)) return;</c> - on the World
/// Authority the online-player list is empty, so vanilla could never pick a target; the handoff dispatches the
/// decided spawn group to the node owning a cluster player (or reschedules).</item>
/// <item>after vanilla dropped the character-less players from its buffer: <c>ApplyPinnedTarget()</c> - the
/// executing node keeps only the WA-chosen target so the dispatch is deterministic.</item>
/// </list>
/// Both call-ins are no-ops while the seams are unset (null delegate / zero pin), so vanilla is unchanged.
/// </summary>
[HarmonyPatch(typeof(MyNeutralShipSpawner), "Spawn")]
public static class MyNeutralShipSpawnerPatch
{
    private const string PcuExhaustedMessage = "NPC PCUs exhausted. Cargo ship will not spawn.";

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> SpawnTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();

        // Second call-in first, so the first one's insertion does not shift its index.
        // Anchor: the first of vanilla's two m_playersBuffer.RemoveAll calls (x.Character == null), whose int
        // result is popped; the pin filter goes right after the pop.
        var removeAlls = il.FindAllIndex(ci => ci.CallsNamed("RemoveAll", typeof(List<MyPlayer>)));
        GateTranspilerHelpers.Require(removeAlls.Count == 2, original, "exactly two List<MyPlayer>.RemoveAll calls");
        int pop = removeAlls[0] + 1;
        GateTranspilerHelpers.Require(il[pop].opcode == OpCodes.Pop, original, "pop after the first RemoveAll");
        var pin = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MyNeutralShipSpawnerSeams), nameof(MyNeutralShipSpawnerSeams.ApplyPinnedTarget)))
            .MoveLabelsFrom(il[pop + 1]);
        il.Insert(pop + 1, pin);

        // Anchor: the PCU-exhausted log line, followed by vanilla's `return`; the handoff goes right after it,
        // taking over the label the PCU check jumps to when PCUs remain.
        int message = il.IndexOfSingle(original, "ldstr PCU exhausted", ci => ci.LoadsString(PcuExhaustedMessage));
        int ret = il.FindIndex(message, ci => ci.opcode == OpCodes.Ret);
        GateTranspilerHelpers.Require(ret > message && ret - message <= 6, original, "ret shortly after the PCU-exhausted log");
        var next = il[ret + 1];
        GateTranspilerHelpers.Require(next.labels.Count > 0, original, "a label on the instruction after the PCU-exhausted return");
        var handoff = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(next);
        var resume = next.AddLabel(generator);
        il.InsertRange(ret + 1, new[]
        {
            handoff,
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MyNeutralShipSpawnerSeams), nameof(MyNeutralShipSpawnerSeams.TryHandoff))),
            new CodeInstruction(OpCodes.Brfalse, resume),
            new CodeInstruction(OpCodes.Ret),
        });

        return il;
    }
}
