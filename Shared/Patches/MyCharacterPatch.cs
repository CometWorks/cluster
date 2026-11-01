using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Replication.ClientStates;
using VRage.Game;
using VRage.Library.Collections;
using VRage.ObjectBuilders;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// The cluster hunks of MyCharacter: MyCharacterPatch.CharacterLoaded as a
/// postfix on Init, and the four ClusterLast* diagnostic stamps at the sites that
/// change them. The stamps are gated on <see cref="ClusterRuntime.IsClustered"/>; the seam postfix is inert while
/// the delegate is null.
/// </summary>
[HarmonyPatch(typeof(MyCharacter))]
public static class MyCharacterPatch
{
	// CharacterLoaded: restore the identity -> body link a cluster save loses.
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyCharacter.Init), new[] { typeof(MyObjectBuilder_EntityBase) })]
	private static void InitPostfix(MyCharacter __instance, MyObjectBuilder_EntityBase objectBuilder)
	{
		Action<MyCharacter, long> seam = MyCharacterSeams.ClusterCharacterLoaded;
		if (seam == null) return;
		seam(__instance, (objectBuilder as MyObjectBuilder_Character)?.OwningPlayerIdentityId ?? 0L);
	}

	// ClusterLastControlsFrame / ClusterLastControlsMove: stamp the arrival of a controls payload right after
	// vanilla constructed the client state from the stream (before the out-of-order check).
	[HarmonyTranspiler]
	[HarmonyPatch(nameof(MyCharacter.DeserializeControls), new[] { typeof(BitStream), typeof(bool) })]
	private static IEnumerable<CodeInstruction> DeserializeControlsTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		const string method = "MyCharacter.DeserializeControls";
		List<CodeInstruction> code = instructions.ToList();
		ConstructorInfo ctor = AccessTools.Constructor(typeof(MyCharacterClientState), new[] { typeof(BitStream) });
		MethodInfo record = AccessTools.Method(typeof(MyCharacterSeams), nameof(MyCharacterSeams.RecordControls));
		// MyCharacterClientState is a struct: `ldloca.s state; ldarg.1; call .ctor(BitStream)`.
		int construct = PlayersTranspilerHelpers.IndexOfSingle(code, c => c.opcode == OpCodes.Call && Equals(c.operand, ctor),
			"MyCharacterClientState(BitStream) construction", method);
		if (construct < 2 || code[construct - 1].opcode != OpCodes.Ldarg_1)
			throw new InvalidOperationException($"{method}: unexpected shape around the client-state construction");
		CodeInstruction loadState = PlayersTranspilerHelpers.LoadLocalAddressedBy(code[construct - 2], method);
		code.InsertRange(construct + 1, new[]
		{
			new CodeInstruction(OpCodes.Ldarg_0),
			loadState,
			new CodeInstruction(OpCodes.Call, record),
		});
		return code;
	}

	// ClusterLastInputFrame: nonzero movement input reaching MoveAndRotate.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyCharacter.MoveAndRotate), new[] { typeof(Vector3), typeof(Vector2), typeof(float) })]
	private static void MoveAndRotatePrefix(MyCharacter __instance, Vector3 moveIndicator)
	{
		if (moveIndicator != Vector3.Zero && ClusterRuntime.IsClustered)
			__instance.ClusterLastInputFrame = MyCharacterSeams.CurrentFrame;
	}

	// ClusterLastUpdateFrame: the frame this character's UpdateBeforeSimulation actually ran.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyCharacter.UpdateBeforeSimulation), new Type[0])]
	private static void UpdateBeforeSimulationPrefix(MyCharacter __instance)
	{
		if (ClusterRuntime.IsClustered)
			__instance.ClusterLastUpdateFrame = MyCharacterSeams.CurrentFrame;
	}
}
