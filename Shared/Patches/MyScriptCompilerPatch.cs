using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using ClusterPlugin;
using HarmonyLib;
using Shared.Tools;
using VRage.Scripting;

namespace Shared.Patches;

/// <summary>
/// Reuse completed identical PB compilations
/// within a session while loading a separate assembly image for every PB (static-state isolation).
/// ClusterPlugin.ScriptCompilerPatch owns hashing, the generation-fenced PE-image cache and the
/// isolated Assembly.Load; this class only reproduces the two game-side injection points. Both are
/// inert while the MyScriptCompilerSeams delegates are null.
/// </summary>
[HarmonyPatch(typeof(MyScriptCompiler))]
public static class MyScriptCompilerPatch
{
    /// <summary>
    /// Cache lookup ahead of vanilla compilation. The scripts enumerable is materialised to an array
    /// (by ref, so the state machine sees the same array) before computing the key, which leaves a
    /// replayable argument for the miss path. On a hit the completed task is returned directly.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyScriptCompiler.Compile))]
    private static bool CompilePrefix(MyScriptCompiler __instance, MyApiTarget target, ref IEnumerable<Script> scripts,
        List<Message> messages, bool enableDebugInformation, bool trackMemoryUsage, ref Task<Assembly> __result)
    {
        var getCompiled = MyScriptCompilerSeams.ClusterGetCompiled;
        if (getCompiled == null)
            return true;
        var scriptArray = scripts?.ToArray() ?? Array.Empty<Script>();
        scripts = scriptArray;
        var cached = getCompiled(__instance, target, scriptArray, messages, enableDebugInformation, trackMemoryUsage);
        if (cached == null)
            return true;
        __result = Task.FromResult(cached);
        return false;
    }

    /// <summary>Transpiler call site inside the async state machine.</summary>
    public static void StoreImage(MyScriptCompiler compiler, MyApiTarget target, IEnumerable<Script> scripts,
        List<Message> messages, bool enableDebugInformation, bool trackMemoryUsage, byte[] image)
    {
        MyScriptCompilerSeams.ClusterStoreCompiled?.Invoke(compiler, target, scripts, messages, enableDebugInformation, trackMemoryUsage, image);
    }

    /// <summary>
    /// Image store: in the state machine's MoveNext, the single successful
    /// <c>Assembly.Load(assemblyStream.ToArray())</c> gets its byte array handed to StoreImage first.
    /// The hoisted parameters of the async method are read from the state machine's fields (the one
    /// place reflection is the accepted tool: compiler-generated state machines are not publicized).
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(MyScriptCompiler.Compile), MethodType.Async)]
    private static IEnumerable<CodeInstruction> CompileMoveNextTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var il = instructions.ToList();
        var toArray = AccessTools.Method(typeof(MemoryStream), nameof(MemoryStream.ToArray));
        var load = AccessTools.Method(typeof(Assembly), nameof(Assembly.Load), new[] { typeof(byte[]) });
        var store = AccessTools.Method(typeof(MyScriptCompilerPatch), nameof(StoreImage));

        var loads = il.FindAllIndex(ci => ci.Calls(load));
        var anchors = loads.Where(i => i > 0 && il[i - 1].Calls(toArray)).ToList();
        if (anchors.Count != 1)
            throw new CodeInstructionNotFound($"{original.DeclaringType?.Name}.{original.Name}: expected exactly one Assembly.Load(assemblyStream.ToArray()), found {anchors.Count}");
        var at = anchors[0];

        var stateMachine = original.DeclaringType ?? throw new CodeInstructionNotFound("Compile state machine type not found");
        FieldInfo Hoisted(string name) => AccessTools.Field(stateMachine, name)
                                          ?? throw new CodeInstructionNotFound($"{stateMachine.Name} has no hoisted field '{name}'");
        var image = generator.DeclareLocal(typeof(byte[]));
        il.InsertRange(at, new[]
        {
            new CodeInstruction(OpCodes.Stloc, image),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("<>4__this")),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("target")),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("scripts")),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("messages")),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("enableDebugInformation")),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, Hoisted("trackMemoryUsage")),
            new CodeInstruction(OpCodes.Ldloc, image),
            new CodeInstruction(OpCodes.Call, store),
            new CodeInstruction(OpCodes.Ldloc, image),
        });
        return il;
    }
}
