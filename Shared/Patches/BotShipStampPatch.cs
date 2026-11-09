using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace Shared.Patches;

/// <summary>
/// SE-1316: a bot ship's last use travels in the grid's object builder, so handovers, saves and offline partitions keep
/// it. The logic is <see cref="BotShipCleaner"/>; the hooks are inert outside a cluster and while the cleanup is off.
/// </summary>
public static class BotShipStampPatch
{
	[HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.Init), typeof(MyObjectBuilder_EntityBase))]
	private static class Init
	{
		[HarmonyPostfix]
		private static void Postfix(MyCubeGrid __instance, MyObjectBuilder_EntityBase objectBuilder)
			=> BotShipCleaner.Initialized(__instance, objectBuilder);
	}

	[HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.GetObjectBuilder), typeof(bool))]
	private static class GetObjectBuilder
	{
		[HarmonyPostfix]
		private static void Postfix(MyCubeGrid __instance, MyObjectBuilder_EntityBase __result)
			=> BotShipCleaner.Saving(__instance, __result);
	}
}
