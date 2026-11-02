using System;
using System.Collections.Generic;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Library.Utils;

namespace Shared.Patches;

/// <summary>
/// Game-side injection points of the MySession seams: session clock, strict promotion, PlayerProfileService,
/// promotion replication and remote block usage. Every patch is inert while its seam is null.
/// </summary>
[HarmonyPatch(typeof(MySession))]
public static class MySessionPatch
{
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MySession.Update), new[] { typeof(MyTimeSpan) })]
	private static void BeforeUpdate(MySession __instance, out (int Frame, long Sun) __state)
		=> __state = (__instance.GameplayFrameCounter, AccountingClock.SunTicks(__instance));

	// SessionClockPatch.Postfix: slew the node's session clock to the WA clock plus the admin time-of-day offset.
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MySession.Update), new[] { typeof(MyTimeSpan) })]
	private static void UpdatePostfix(MySession __instance, (int Frame, long Sun) __state)
	{
		AccountingClock.CompletedNativeUpdate(__instance, __state.Frame, __state.Sun);
		MySessionSeams.ClusterAdjustElapsedGameTime?.Invoke(__instance);
	}

	// ClusterSaveFolder: the session loaded under its save folder's name; players see the world's.
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MySession.Load), new[] { typeof(string), typeof(MyObjectBuilder_Checkpoint), typeof(ulong), typeof(bool), typeof(bool) })]
	private static void LoadPostfix()
	{
		MySandboxGameSeams.RestorePremadeWorldName();
	}

	// ClusterStrictPromotion: the raw PromotedUsers entry decides for every id on the test bench.
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MySession.GetUserPromoteLevel), new[] { typeof(ulong) })]
	private static bool GetUserPromoteLevelPrefix(ulong steamId, ref MyPromoteLevel __result)
	{
		Func<bool> strict = MySessionSeams.ClusterStrictPromotion;
		if (strict == null || !strict()) return true;
		MySession.Static.PromotedUsers.TryGetValue(steamId, out MyPromoteLevel level);
		__result = level;
		return false;
	}

	// ClusterPromotionChanged: raised after a promote-level change executed on this server.
	[HarmonyPostfix]
	[HarmonyPatch("OnPromoteLevelSet", new[] { typeof(ulong), typeof(MyPromoteLevel) })]
	private static void OnPromoteLevelSetPostfix(ulong steamId, MyPromoteLevel level)
	{
		MySessionSeams.ClusterPromotionChanged?.Invoke(steamId, level);
	}

	// ClusterAfterSavePlayers: after SavePlayers, SaveToolbars and SaveCameraCollection have all run - a
	// postfix on the whole checkpoint build satisfies the ordering requirement of the ledger.
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MySession.GetCheckpoint), new[] { typeof(string), typeof(bool) })]
	private static void GetCheckpointPostfix(MyObjectBuilder_Checkpoint __result)
	{
		if (__result != null) MySessionSeams.ClusterAfterSavePlayers?.Invoke(__result);
	}

	// ClusterRemoteBlockUsage / ClusterRemoteTypeBuilt: block and PCU usage committed on other nodes joins every
	// limit verdict. Three expressions inside CheckLimits need them, so the patch substitutes
	// MySessionSeams.CheckLimitsWithRemoteUsage while either seam is installed.
	[HarmonyPrefix]
	[HarmonyPatch("CheckLimits", new[]
	{
		typeof(Dictionary<string, int>), typeof(long), typeof(string), typeof(int), typeof(int), typeof(int), typeof(Dictionary<string, int>)
	})]
	private static bool CheckLimitsPrefix(MySession __instance, Dictionary<string, int> failedBlockTypesResult, long ownerID,
		string blockName, int pcuToBuild, int blocksToBuild, int blocksCount, Dictionary<string, int> blocksPerType,
		ref MySession.LimitResult __result)
	{
		if (MySessionSeams.ClusterRemoteBlockUsage == null && MySessionSeams.ClusterRemoteTypeBuilt == null) return true;
		__result = MySessionSeams.CheckLimitsWithRemoteUsage(__instance, failedBlockTypesResult, ownerID, blockName,
			pcuToBuild, blocksToBuild, blocksCount, blocksPerType);
		return false;
	}
}
