using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Shared.Tools;

namespace Shared.Patches;

/// <summary>
/// ProgrammableBlockFreezePatch + ProgrammableBlockMigrationPatch.
/// </summary>
[HarmonyPatch(typeof(MyProgrammableBlock))]
public static class MyProgrammableBlockPatch
{
    /// <summary>
    /// Prevent programmable-block scripts from mutating a partition while a source capture or target
    /// import is transaction-frozen. Bound to the exact private overload because the public execution
    /// methods delegate to it. Inert until ClusterRuntime installs the frozen resolver.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("ExecuteCode", new[] { typeof(string), typeof(UpdateType), typeof(string) }, new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    private static bool ExecuteCodePrefix(MyProgrammableBlock __instance, ref MyProgrammableBlock.ScriptTerminationReason __result, ref string response)
    {
        if (ProgrammableBlockFreezePatch.IsFrozen == null)
            return true;
        return ProgrammableBlockFreezePatch.Prefix(__instance, ref __result, ref response);
    }

    /// <summary>Transpiler call site: eagerly prepare the transferred PB's IGC context.</summary>
    public static void PrepareProgram(MyProgrammableBlock block, IMyGridProgram program)
    {
        MyProgrammableBlockSeams.ClusterPrepareProgram?.Invoke(block, program);
    }

    /// <summary>
    /// Eagerly create the transferred PB's IGC context before its constructor so IGC.Me and
    /// constructor-driven Update10 setup use the preserved EntityId. Inserted right after the single
    /// <c>m_instance.IGC_ContextGetter = ...</c> assignment and before the sandboxed constructor call.
    /// A null callback preserves the original lazy IGC context exactly.
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch("CreateInstance", new[] { typeof(Assembly), typeof(IEnumerable<string>), typeof(string) })]
    private static IEnumerable<CodeInstruction> CreateInstanceTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var instance = AccessTools.Field(typeof(MyProgrammableBlock), "m_instance")
                       ?? throw new CodeInstructionNotFound("MyProgrammableBlock.m_instance not found");
        var prepare = AccessTools.Method(typeof(MyProgrammableBlockPatch), nameof(PrepareProgram));

        var setters = il.FindAllIndex(ci => (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                                            && ci.operand is MethodInfo mi && mi.Name == "set_IGC_ContextGetter");
        if (setters.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one IGC_ContextGetter assignment, found {setters.Count}");

        il.InsertRange(setters[0] + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, instance),
            new CodeInstruction(OpCodes.Call, prepare),
        });
        return il;
    }
}
