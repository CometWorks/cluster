using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Game.Entity;
using VRage.Library.Collections;

namespace Shared.Patches;

/// <summary>
/// Maintains the two MyClientState diagnostic fields (<see cref="MyClientStateSeams"/>): a prefix on the
/// private <c>Read</c> stamps the frame and presets "unresolved"; a transpiler swaps the single
/// <c>GetControllingPlayer</c> call of Read for a helper that records which branch of the silent input
/// gate was taken. Vanilla control flow is unchanged; the stamps only happen while clustered.
/// </summary>
[HarmonyPatch(typeof(MyClientState), "Read", new[] { typeof(BitStream), typeof(bool) })]
public static class MyClientStatePatch
{
	[HarmonyPrefix]
	private static void ReadPrefix(MyClientState __instance)
	{
		MyClientStateSeams.BeginRead(__instance);
	}

	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> ReadTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		const string method = "MyClientState.Read";
		List<CodeInstruction> code = instructions.ToList();
		MethodInfo getControllingPlayer = AccessTools.Method(typeof(MyPlayerCollection), nameof(MyPlayerCollection.GetControllingPlayer), new[] { typeof(MyEntity) });
		MethodInfo gate = AccessTools.Method(typeof(MyClientStateSeams), nameof(MyClientStateSeams.GateControllingPlayer));
		int call = PlayersTranspilerHelpers.IndexOfSingle(code, c => PlayersTranspilerHelpers.CallsMethod(c, getControllingPlayer),
			"GetControllingPlayer call", method);
		// stack: [players, entity] -> [players, entity, this] -> gate(players, entity, this) -> [player]
		CodeInstruction original = code[call];
		CodeInstruction loadThis = new CodeInstruction(OpCodes.Ldarg_0);
		loadThis.labels.AddRange(original.labels);
		original.labels.Clear();
		original.opcode = OpCodes.Call;
		original.operand = gate;
		code.Insert(call, loadThis);
		return code;
	}
}
