using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Replication;

namespace Shared.Patches;

/// <summary>
/// Makes a character replicable's far-broadcaster dependencies follow its radio receiver's found/lost
/// events (see <see cref="MyCharacterReplicableSeams.HookRadioReceiver"/>). A postfix on <c>OnHook</c>,
/// applied only while clustered.
/// </summary>
[HarmonyPatch(typeof(MyCharacterReplicable), "OnHook")]
public static class MyCharacterReplicablePatch
{
	[HarmonyPostfix]
	private static void OnHookPostfix(MyCharacterReplicable __instance)
	{
		MyCharacterReplicableSeams.HookRadioReceiver(__instance);
	}
}
