using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Utils;
using VRage.Game.Definitions;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// SE-1158: the game's DLC and skin ownership checks answer from what the Gateway's Steam frontend found
/// (<see cref="PlayerEntitlements"/>) instead of the node's Direct Transport game server, which has no Steam.
/// Postfixes of other plugins (Octahedron's shipyard overrides) still run after these prefixes.
/// </summary>
[HarmonyPatch]
public static class PlayerEntitlementsPatch
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(MySessionComponentDLC), nameof(MySessionComponentDLC.HasDLC), typeof(string), typeof(ulong))]
	private static bool HasDlcPrefix(string DLCName, ulong steamId, ref bool __result)
	{
		if (MyFakes.OWN_ALL_DLCS || steamId == Sync.MyId || !MyDLCs.TryGetDLC(DLCName, out MyDlcDefinition dlc)) return true;
		bool? owned = PlayerEntitlements.HasDlc(dlc.AppId, steamId);
		if (owned == null) return true;
		__result = owned.Value;
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(MySessionComponentGameInventory), nameof(MySessionComponentGameInventory.HasItem))]
	private static bool HasItemPrefix(MyStringHash itemSubtypeId, ulong steamId, ref bool __result)
	{
		if (steamId == Sync.MyId) return true;
		bool? owned = PlayerEntitlements.HasSkin(itemSubtypeId, steamId);
		if (owned == null) return true;
		__result = owned.Value;
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(MySessionComponentGameInventory), "UpdateClientGameInventory")]
	private static bool UpdateClientGameInventoryPrefix(ulong steamId, byte[] checkData)
		=> !PlayerEntitlements.CheckSkins(steamId, checkData);
}
