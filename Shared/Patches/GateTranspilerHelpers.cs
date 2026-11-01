using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Shared.Patches;

/// <summary>
/// Small IL helpers for the mid-method cluster gates (batch C-worldsystems). Every lookup throws
/// with the patched method's name when an anchor is missing or ambiguous, so a game update that
/// moves the code fails the Harmony pass loudly (the plugin refuses to load) instead of silently
/// running vanilla with a half-applied seam.
/// </summary>
internal static class GateTranspilerHelpers
{
    public static int IndexOfSingle(this List<CodeInstruction> il, MethodBase original, string what, Func<CodeInstruction, bool> predicate)
    {
        int index = -1;
        for (int i = 0; i < il.Count; i++)
        {
            if (!predicate(il[i]))
                continue;
            if (index >= 0)
                throw new InvalidOperationException($"{Describe(original)}: anchor '{what}' matched more than once (at {index} and {i})");
            index = i;
        }

        if (index < 0)
            throw new InvalidOperationException($"{Describe(original)}: anchor '{what}' not found");
        return index;
    }

    public static void Require(bool condition, MethodBase original, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"{Describe(original)}: expected {what}");
    }

    public static bool CallsNamed(this CodeInstruction ci, string name, Type declaringType = null)
        => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
           && ci.operand is MethodInfo method
           && method.Name == name
           && (declaringType == null || method.DeclaringType == declaringType);

    public static bool LoadsFieldNamed(this CodeInstruction ci, string name)
        => (ci.opcode == OpCodes.Ldfld || ci.opcode == OpCodes.Ldsfld)
           && ci.operand is FieldInfo field
           && field.Name == name;

    public static bool LoadsString(this CodeInstruction ci, string value)
        => ci.opcode == OpCodes.Ldstr && ci.operand is string s && s == value;

    /// <summary>The method's final instruction, which must be a ret; returns a label placed on it.</summary>
    public static Label LabelOnLastRet(this List<CodeInstruction> il, ILGenerator generator, MethodBase original)
    {
        Require(il.Count > 0 && il[il.Count - 1].opcode == OpCodes.Ret, original, "the method to end with ret");
        return il[il.Count - 1].AddLabel(generator);
    }

    public static Label AddLabel(this CodeInstruction ci, ILGenerator generator)
    {
        var label = generator.DefineLabel();
        ci.labels.Add(label);
        return label;
    }

    /// <summary>Index of the local a ldloc/stloc (any encoding) refers to; false for anything else.</summary>
    public static bool TryGetLocalIndex(this CodeInstruction ci, out int index, out bool isStore)
    {
        isStore = false;
        index = -1;
        var op = ci.opcode;
        if (op == OpCodes.Ldloc_0) index = 0;
        else if (op == OpCodes.Ldloc_1) index = 1;
        else if (op == OpCodes.Ldloc_2) index = 2;
        else if (op == OpCodes.Ldloc_3) index = 3;
        else if (op == OpCodes.Stloc_0) { index = 0; isStore = true; }
        else if (op == OpCodes.Stloc_1) { index = 1; isStore = true; }
        else if (op == OpCodes.Stloc_2) { index = 2; isStore = true; }
        else if (op == OpCodes.Stloc_3) { index = 3; isStore = true; }
        else if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Stloc || op == OpCodes.Stloc_S)
        {
            isStore = op == OpCodes.Stloc || op == OpCodes.Stloc_S;
            switch (ci.operand)
            {
                case LocalVariableInfo local:
                    index = local.LocalIndex;
                    break;
                case int i:
                    index = i;
                    break;
                case byte b:
                    index = b;
                    break;
                default:
                    return false;
            }
        }

        return index >= 0;
    }

    /// <summary>Turns an instruction into a nop in place, keeping its labels and exception blocks.</summary>
    public static void MakeNop(this CodeInstruction ci)
    {
        ci.opcode = OpCodes.Nop;
        ci.operand = null;
    }

    public static string Describe(MethodBase method)
        => $"{method?.DeclaringType?.Name}.{method?.Name}";
}
