using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// Authorize post-creation identity
/// replacements at the WA before vanilla changes the player and its maps. Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MyPlayer), nameof(MyPlayer.Identity), MethodType.Setter)]
public static class MyPlayerPatch
{
	[HarmonyPrefix]
	private static void IdentityPrefix(MyPlayer __instance, MyIdentity value)
	{
		MyPlayerSeams.ClusterIdentityChanging?.Invoke(__instance, value);
	}
}
