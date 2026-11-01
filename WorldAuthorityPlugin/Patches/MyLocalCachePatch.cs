using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Networking;
using VRage.Game;
using VRage.Library.Utils;
using VRage.ObjectBuilders.Private;

namespace WorldAuthorityPlugin.Patches;

/// <summary>
/// A replacement World Authority
/// restores the newest catalogued global checkpoint through the complete vanilla world-load path.
/// The seam has to run right after <c>LoadCheckpoint</c> deserialized
/// <c>Sandbox.sbc</c>; a prefix would run before the local checkpoint exists and a postfix after
/// vanilla already applied the world configuration and platform patches to the wrong one, so this
/// is a transpiler inserting the call at that exact spot. WA-only: only this plugin applies it.
/// </summary>
[HarmonyPatch(typeof(MyLocalCache), nameof(MyLocalCache.LoadCheckpoint),
	new[] { typeof(string), typeof(ulong), typeof(MyGameModeEnum?), typeof(MyOnlineModeEnum?) },
	new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal })]
public static class MyLocalCachePatch
{
	/// <summary>Called from the patched method; inert while the seam is not installed.</summary>
	public static void Restore(ref MyObjectBuilder_Checkpoint checkpoint, ref ulong sizeInBytes)
	{
		MyLocalCacheSeams.ClusterRestoreWorldAuthorityCheckpoint?.Invoke(ref checkpoint, ref sizeInBytes);
	}

	/// <summary>
	/// Anchor: the single closed-generic
	/// <c>MyObjectBuilderSerializerKeen.DeserializeXML&lt;MyObjectBuilder_Checkpoint&gt;(path, out objectBuilder, out sizeInBytes)</c>
	/// call whose size destination is the method's <c>out sizeInBytes</c> argument (ldarg.1), whose
	/// checkpoint destination is a local of type MyObjectBuilder_Checkpoint (ldloca), and whose result
	/// is discarded and followed by a null test of that same local (ldloc + brfalse). The method must
	/// also build its path from "Sandbox.sbc". After the anchor (and its pop) the transpiler inserts
	/// <c>ldloca objectBuilder; ldarg.1; call Restore</c>; labels stay on the following ldloc.
	/// </summary>
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
	{
		var code = new List<CodeInstruction>(instructions);
		bool sandboxPath = false;
		foreach (var instruction in code)
			if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string s && s == "Sandbox.sbc")
				sandboxPath = true;
		if (!sandboxPath)
			throw new InvalidOperationException(original + ": no \"Sandbox.sbc\" path constant; the checkpoint load sequence changed");

		var matches = new List<int>();
		for (int i = 2; i + 2 < code.Count; i++)
		{
			if (!IsDeserializeCheckpointXml(code[i])) continue;
			if (!code[i - 1].IsLdarg(1)) continue;
			if (!TryLocalIndex(code[i - 2], OpCodes.Ldloca, OpCodes.Ldloca_S, out int local)) continue;
			if (code[i - 2].operand is LocalBuilder builder && builder.LocalType != typeof(MyObjectBuilder_Checkpoint)) continue;
			int next = i + 1;
			if (code[next].opcode == OpCodes.Pop) next++;
			if (next + 1 >= code.Count) continue;
			if (!TryLocalIndex(code[next], OpCodes.Ldloc, OpCodes.Ldloc_S, out int tested) || tested != local) continue;
			if (code[next + 1].opcode != OpCodes.Brfalse && code[next + 1].opcode != OpCodes.Brfalse_S) continue;
			matches.Add(i);
		}
		if (matches.Count != 1)
			throw new InvalidOperationException(original + ": expected exactly one DeserializeXML<MyObjectBuilder_Checkpoint>"
				+ "(path, out local, out sizeInBytes) anchor followed by a null test, found " + matches.Count);

		int at = matches[0];
		int insertAt = at + 1;
		if (code[insertAt].opcode == OpCodes.Pop) insertAt++;
		CodeInstruction ldloca = code[at - 2];
		code.InsertRange(insertAt, new[]
		{
			new CodeInstruction(ldloca.opcode, ldloca.operand),
			new CodeInstruction(OpCodes.Ldarg_1),
			CodeInstruction.Call(typeof(MyLocalCachePatch), nameof(Restore)),
		});
		return code;
	}

	private static bool IsDeserializeCheckpointXml(CodeInstruction instruction)
	{
		if (instruction.opcode != OpCodes.Call || instruction.operand is not MethodInfo method) return false;
		if (method.DeclaringType != typeof(MyObjectBuilderSerializerKeen) || method.Name != "DeserializeXML") return false;
		if (!method.IsGenericMethod || method.GetGenericArguments()[0] != typeof(MyObjectBuilder_Checkpoint)) return false;
		ParameterInfo[] parameters = method.GetParameters();
		return parameters.Length == 3
			&& parameters[0].ParameterType == typeof(string)
			&& parameters[1].ParameterType == typeof(MyObjectBuilder_Checkpoint).MakeByRefType()
			&& parameters[2].ParameterType == typeof(ulong).MakeByRefType();
	}

	/// <summary>Local index of a ldloc/ldloca family instruction given its long and short opcodes.</summary>
	private static bool TryLocalIndex(CodeInstruction instruction, OpCode longForm, OpCode shortForm, out int index)
	{
		index = -1;
		if (instruction.opcode == longForm || instruction.opcode == shortForm)
		{
			index = instruction.operand switch
			{
				LocalBuilder builder => builder.LocalIndex,
				int i => i,
				short s => s,
				byte b => b,
				sbyte sb => sb,
				_ => -1,
			};
			return index >= 0;
		}
		// ldloc.0-3 are only used for the numbered short forms; ldloca has no such forms.
		if (longForm == OpCodes.Ldloc)
		{
			if (instruction.opcode == OpCodes.Ldloc_0) index = 0;
			else if (instruction.opcode == OpCodes.Ldloc_1) index = 1;
			else if (instruction.opcode == OpCodes.Ldloc_2) index = 2;
			else if (instruction.opcode == OpCodes.Ldloc_3) index = 3;
		}
		return index >= 0;
	}
}
