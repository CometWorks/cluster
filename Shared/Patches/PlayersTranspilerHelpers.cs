using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Shared.Patches;

/// <summary>
/// Small IL helpers shared by the A-players transpilers. Every anchor lookup throws a descriptive
/// exception unless it matches exactly once, so a game update that moves an injection point fails the
/// Harmony install loudly instead of silently dropping a seam.
/// </summary>
internal static class PlayersTranspilerHelpers
{
	/// <summary>Index of the single instruction satisfying <paramref name="match"/>; throws otherwise.</summary>
	public static int IndexOfSingle(List<CodeInstruction> code, Func<CodeInstruction, bool> match, string what, string method)
	{
		int found = -1, count = 0;
		for (int i = 0; i < code.Count; i++)
		{
			if (!match(code[i])) continue;
			count++;
			found = i;
		}
		if (count != 1)
			throw new InvalidOperationException($"{method}: expected exactly one {what}, found {count}; the game code changed");
		return found;
	}

	/// <summary>The ldloc counterpart of a stloc instruction (same local).</summary>
	public static CodeInstruction LoadLocalStoredBy(CodeInstruction store, string method)
	{
		if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
		if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
		if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
		if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
		if (store.opcode == OpCodes.Stloc_S) return new CodeInstruction(OpCodes.Ldloc_S, store.operand);
		if (store.opcode == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, store.operand);
		throw new InvalidOperationException($"{method}: expected a stloc after the anchor, found {store}");
	}

	/// <summary>The ldloc counterpart of a ldloca instruction (same local).</summary>
	public static CodeInstruction LoadLocalAddressedBy(CodeInstruction address, string method)
	{
		if (address.opcode == OpCodes.Ldloca_S) return new CodeInstruction(OpCodes.Ldloc_S, address.operand);
		if (address.opcode == OpCodes.Ldloca) return new CodeInstruction(OpCodes.Ldloc, address.operand);
		throw new InvalidOperationException($"{method}: expected a ldloca before the anchor, found {address}");
	}

	public static bool CallsMethod(CodeInstruction instruction, MethodBase method)
		=> (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, method);

	public static bool IsCall(CodeInstruction instruction)
		=> instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
}
