using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Shared.Patches;

/// <summary>
/// Anchor-finding and injection helpers shared by the B-economy transpilers (MyStoreBlock, MyContract,
/// MyBankingSystem). Every lookup throws a descriptive exception unless the anchor matches exactly as the
/// ledger prescribes, so a game update that moves an anchor fails the patch loudly at PatchAll instead of
/// silently running vanilla settlement.
/// </summary>
internal static class EconomyIl
{
	public static Exception Mismatch(MethodBase original, string what)
		=> new InvalidOperationException("ClusterPlugin transpiler anchor mismatch in "
			+ original.DeclaringType?.Name + "." + original.Name + ": " + what);

	public static T Required<T>(T member, string name) where T : MemberInfo
		=> member ?? throw new MissingMemberException("ClusterPlugin transpiler target missing: " + name);

	/// <summary>Index of the single instruction satisfying the predicate (index-based, so lookahead works).</summary>
	public static int FindUnique(List<CodeInstruction> code, MethodBase original, string what, Func<int, bool> predicate)
	{
		int found = -1;
		for (int i = 0; i < code.Count; i++)
		{
			if (!predicate(i)) continue;
			if (found >= 0) throw Mismatch(original, what + " matched more than once");
			found = i;
		}
		if (found < 0) throw Mismatch(original, what + " not found");
		return found;
	}

	public static List<int> FindAll(List<CodeInstruction> code, Func<CodeInstruction, bool> predicate)
	{
		var found = new List<int>();
		for (int i = 0; i < code.Count; i++)
			if (predicate(code[i])) found.Add(i);
		return found;
	}

	public static List<int> FindExactly(List<CodeInstruction> code, MethodBase original, string what, int count, Func<CodeInstruction, bool> predicate)
	{
		List<int> found = FindAll(code, predicate);
		if (found.Count != count)
			throw Mismatch(original, what + " expected " + count + " matches, found " + found.Count);
		return found;
	}

	/// <summary>First index in (start, start + window] satisfying the predicate.</summary>
	public static int FindNext(List<CodeInstruction> code, MethodBase original, string what, int start, int window, Func<CodeInstruction, bool> predicate)
	{
		for (int i = start + 1; i <= start + window && i < code.Count; i++)
			if (predicate(code[i])) return i;
		throw Mismatch(original, what + " not found within " + window + " instructions after index " + start);
	}

	public static void Require(bool condition, MethodBase original, string what)
	{
		if (!condition) throw Mismatch(original, what);
	}

	public static bool Calls(CodeInstruction code, MethodInfo method)
		=> (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo target && target == method;

	public static bool LoadsInt(CodeInstruction code, int value)
	{
		OpCode op = code.opcode;
		if (op == OpCodes.Ldc_I4_M1) return value == -1;
		if (op == OpCodes.Ldc_I4_0) return value == 0;
		if (op == OpCodes.Ldc_I4_1) return value == 1;
		if (op == OpCodes.Ldc_I4_2) return value == 2;
		if (op == OpCodes.Ldc_I4_3) return value == 3;
		if (op == OpCodes.Ldc_I4_4) return value == 4;
		if (op == OpCodes.Ldc_I4_5) return value == 5;
		if (op == OpCodes.Ldc_I4_6) return value == 6;
		if (op == OpCodes.Ldc_I4_7) return value == 7;
		if (op == OpCodes.Ldc_I4_8) return value == 8;
		if (op == OpCodes.Ldc_I4_S) return Convert.ToInt32(code.operand) == value;
		if (op == OpCodes.Ldc_I4) return Convert.ToInt32(code.operand) == value;
		return false;
	}

	public static bool IsLdloc(CodeInstruction code)
		=> code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2
			|| code.opcode == OpCodes.Ldloc_3 || code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc;

	public static bool IsStloc(CodeInstruction code)
		=> code.opcode == OpCodes.Stloc_0 || code.opcode == OpCodes.Stloc_1 || code.opcode == OpCodes.Stloc_2
			|| code.opcode == OpCodes.Stloc_3 || code.opcode == OpCodes.Stloc_S || code.opcode == OpCodes.Stloc;

	public static bool IsLdloca(CodeInstruction code)
		=> code.opcode == OpCodes.Ldloca_S || code.opcode == OpCodes.Ldloca;

	public static bool IsBranchOrReturn(CodeInstruction code)
		=> code.opcode == OpCodes.Ret || code.opcode == OpCodes.Br || code.opcode == OpCodes.Br_S;

	/// <summary>A fresh (label-free) copy of a local load instruction.</summary>
	public static CodeInstruction CloneLoad(CodeInstruction ldloc, MethodBase original, string what)
	{
		Require(IsLdloc(ldloc), original, what + " is not a local load (" + ldloc.opcode + ")");
		return new CodeInstruction(ldloc.opcode, ldloc.operand);
	}

	/// <summary>The load matching a local store (stloc.N -> ldloc.N, stloc.s X -> ldloc.s X).</summary>
	public static CodeInstruction LoadOfStore(CodeInstruction stloc, MethodBase original, string what)
	{
		Require(IsStloc(stloc), original, what + " is not a local store (" + stloc.opcode + ")");
		if (stloc.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
		if (stloc.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
		if (stloc.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
		if (stloc.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
		if (stloc.opcode == OpCodes.Stloc_S) return new CodeInstruction(OpCodes.Ldloc_S, stloc.operand);
		return new CodeInstruction(OpCodes.Ldloc, stloc.operand);
	}

	/// <summary>The load matching a local address load (ldloca.s X -> ldloc.s X).</summary>
	public static CodeInstruction LoadOfAddress(CodeInstruction ldloca, MethodBase original, string what)
	{
		Require(IsLdloca(ldloca), original, what + " is not a local address load (" + ldloca.opcode + ")");
		return new CodeInstruction(ldloca.opcode == OpCodes.Ldloca_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, ldloca.operand);
	}

	/// <summary>
	/// Inserts <paramref name="injected"/> before <c>code[index]</c>. Labels and block starts of the displaced
	/// instruction move to the first injected instruction so branches into the anchor keep flowing through the
	/// injected code.
	/// </summary>
	public static void InsertBefore(List<CodeInstruction> code, int index, IList<CodeInstruction> injected)
	{
		if (injected.Count == 0) return;
		if (index < code.Count) MoveLabelsAndBlockStarts(code[index], injected[0]);
		code.InsertRange(index, injected);
	}

	/// <summary>Inserts after <c>code[index]</c>; see <see cref="InsertBefore"/> for label handling.</summary>
	public static void InsertAfter(List<CodeInstruction> code, int index, IList<CodeInstruction> injected)
		=> InsertBefore(code, index + 1, injected);

	/// <summary>
	/// Injects "if (!helper(args)) { rejection }" before <c>code[index]</c>; execution resumes at the displaced
	/// instruction when the helper returns true.
	/// </summary>
	public static void InjectGateBefore(List<CodeInstruction> code, int index, ILGenerator generator, MethodInfo helper,
		IList<CodeInstruction> arguments, IList<CodeInstruction> rejection)
	{
		Label resume = generator.DefineLabel();
		var injected = new List<CodeInstruction>(arguments)
		{
			new CodeInstruction(OpCodes.Call, helper),
			new CodeInstruction(OpCodes.Brtrue, resume)
		};
		injected.AddRange(rejection);
		InsertBefore(code, index, injected);
		code[index + injected.Count].labels.Add(resume);
	}

	public static void InjectGateAfter(List<CodeInstruction> code, int index, ILGenerator generator, MethodInfo helper,
		IList<CodeInstruction> arguments, IList<CodeInstruction> rejection)
		=> InjectGateBefore(code, index + 1, generator, helper, arguments, rejection);

	private static void MoveLabelsAndBlockStarts(CodeInstruction from, CodeInstruction to)
	{
		to.labels.AddRange(from.labels);
		from.labels.Clear();
		// Block starts (try/handler begins) move with the labels so the injected code stays inside the same
		// protected region as the anchor; an end marker stays on the original instruction.
		for (int i = from.blocks.Count - 1; i >= 0; i--)
		{
			if (from.blocks[i].blockType == ExceptionBlockType.EndExceptionBlock) continue;
			to.blocks.Add(from.blocks[i]);
			from.blocks.RemoveAt(i);
		}
	}
}
