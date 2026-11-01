using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using SpaceEngineers.Game.GUI;
using SpaceEngineers.Game.World;

namespace Shared.Patches;

/// <summary>
/// Append WA-registry
/// respawn rows to the vanilla medical screen. Transpiles <c>RefreshRespawnPointsRequest</c>: right after
/// <c>GetAvailableRespawnPoints(...)</c> is stored to its local, inject (identity, token) and call the seam
/// helper. Autorespawn and the local spawn-selection callers of GetAvailableRespawnPoints are untouched.
/// </summary>
[HarmonyPatch(typeof(MyGuiScreenMedicals), "RefreshRespawnPointsRequest")]
public static class MyGuiScreenMedicalsPatch
{
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		const string method = "MyGuiScreenMedicals.RefreshRespawnPointsRequest";
		List<CodeInstruction> code = instructions.ToList();
		MethodInfo getPoints = AccessTools.Method(typeof(MySpaceRespawnComponent), nameof(MySpaceRespawnComponent.GetAvailableRespawnPoints),
			new[] { typeof(long?), typeof(bool), typeof(bool) });
		MethodInfo getIdentity = AccessTools.Method(typeof(MyPlayerCollection), nameof(MyPlayerCollection.TryGetIdentityId),
			new[] { typeof(ulong), typeof(int) });
		MethodInfo helper = AccessTools.Method(typeof(MyGuiScreenMedicalsSeams), nameof(MyGuiScreenMedicalsSeams.AppendRespawnPoints));

		int identityCall = PlayersTranspilerHelpers.IndexOfSingle(code, c => PlayersTranspilerHelpers.CallsMethod(c, getIdentity),
			"TryGetIdentityId call", method);
		int pointsCall = PlayersTranspilerHelpers.IndexOfSingle(code, c => PlayersTranspilerHelpers.CallsMethod(c, getPoints),
			"GetAvailableRespawnPoints call", method);
		CodeInstruction loadIdentity = PlayersTranspilerHelpers.LoadLocalStoredBy(code[identityCall + 1], method);
		CodeInstruction loadToken = PlayersTranspilerHelpers.LoadLocalStoredBy(code[pointsCall + 1], method);
		// Inserted before vanilla's try block (its first instruction keeps the exception-block marker).
		code.InsertRange(pointsCall + 2, new[]
		{
			loadIdentity,
			loadToken,
			new CodeInstruction(System.Reflection.Emit.OpCodes.Call, helper),
		});
		return code;
	}
}
