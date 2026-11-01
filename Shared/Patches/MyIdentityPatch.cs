using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// Raises <see cref="MyIdentitySeams.ClusterStateChanged"/> from three places: a character
/// change syncing IsDead, a character death, and SetDead. Postfixes; inert while null.
/// </summary>
[HarmonyPatch(typeof(MyIdentity))]
public static class MyIdentityPatch
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyIdentity.ChangeCharacter), new[] { typeof(MyCharacter) })]
	private static void ChangeCharacterPostfix(MyIdentity __instance, MyCharacter character)
	{
		// Only inside the `character != null` block, after IsDead = character.IsDead.
		if (character != null) MyIdentitySeams.ClusterStateChanged?.Invoke(__instance);
	}

	[HarmonyPostfix]
	[HarmonyPatch("OnCharacterDied", new[] { typeof(MyCharacter) })]
	private static void OnCharacterDiedPostfix(MyIdentity __instance)
	{
		MyIdentitySeams.ClusterStateChanged?.Invoke(__instance);
	}

	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyIdentity.SetDead), new[] { typeof(bool) })]
	private static void SetDeadPostfix(MyIdentity __instance)
	{
		MyIdentitySeams.ClusterStateChanged?.Invoke(__instance);
	}
}
