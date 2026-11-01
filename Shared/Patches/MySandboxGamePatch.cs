using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// ClusterKeepPremadeSessionSettings: in the premade-checkpoint branch of <c>MySandboxGame.InitQuickLaunch</c>
/// vanilla overwrites the world's session settings with the dedicated config's. The transpiler replaces the
/// single <c>checkpoint.Settings = sessionSettings</c> store with the seam helper, which performs the same
/// store unless the seam says the premade settings must survive. Inert while the seam is null.
/// ClusterSaveFolder: vanilla derives the session path from the checkpoint's SessionName, which it sets to the
/// dedicated config's world name. The second store goes through a helper that puts the save folder there, so a
/// world's save and its mods' world storage don't move with the name players see.
/// </summary>
[HarmonyPatch(typeof(MySandboxGame), "InitQuickLaunch")]
public static class MySandboxGamePatch
{
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		const string method = "MySandboxGame.InitQuickLaunch";
		List<CodeInstruction> code = instructions.ToList();
		FieldInfo settings = AccessTools.Field(typeof(MyObjectBuilder_Checkpoint), nameof(MyObjectBuilder_Checkpoint.Settings));
		FieldInfo sessionName = AccessTools.Field(typeof(MyObjectBuilder_Checkpoint), nameof(MyObjectBuilder_Checkpoint.SessionName));
		MethodInfo apply = AccessTools.Method(typeof(MySandboxGameSeams), nameof(MySandboxGameSeams.ApplyPremadeSessionSettings));
		MethodInfo applyName = AccessTools.Method(typeof(MySandboxGameSeams), nameof(MySandboxGameSeams.ApplyPremadeSessionName));
		int store = PlayersTranspilerHelpers.IndexOfSingle(code, c => c.opcode == OpCodes.Stfld && Equals(c.operand, settings),
			"MyObjectBuilder_Checkpoint.Settings store", method);
		// stack: [checkpoint, sessionSettings] -> apply(checkpoint, sessionSettings); labels/blocks stay on the instruction.
		code[store].opcode = OpCodes.Call;
		code[store].operand = apply;
		int nameStore = PlayersTranspilerHelpers.IndexOfSingle(code, c => c.opcode == OpCodes.Stfld && Equals(c.operand, sessionName),
			"MyObjectBuilder_Checkpoint.SessionName store", method);
		code[nameStore].opcode = OpCodes.Call;
		code[nameStore].operand = applyName;
		return code;
	}
}
