using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// SE1-0231: a safe zone block's zone travels in its object builder. The logic is
/// <see cref="BlockSafeZones"/>; every hook is inert outside a cluster, on the World Authority and on clients.
/// </summary>
public static class MySafeZoneBlockPatch
{
	[HarmonyPatch(typeof(MySafeZoneBlock), nameof(MySafeZoneBlock.Init), typeof(MyObjectBuilder_CubeBlock), typeof(MyCubeGrid))]
	private static class Init
	{
		[HarmonyPostfix]
		private static void Postfix(MySafeZoneBlock __instance, MyObjectBuilder_CubeBlock objectBuilder)
			=> BlockSafeZones.Initialized(__instance, objectBuilder);
	}

	[HarmonyPatch(typeof(MySafeZoneBlock), nameof(MySafeZoneBlock.GetObjectBuilderCubeBlock))]
	private static class GetObjectBuilder
	{
		[HarmonyPostfix]
		private static void Postfix(MySafeZoneBlock __instance, MyObjectBuilder_CubeBlock __result, bool copy)
			=> BlockSafeZones.Saving(__instance, __result, copy);
	}

	[HarmonyPatch(typeof(MySafeZoneBlock), nameof(MySafeZoneBlock.OnAddedToScene))]
	private static class AddedToScene
	{
		[HarmonyPostfix]
		private static void Postfix(MySafeZoneBlock __instance) => BlockSafeZones.AddedToScene(__instance);
	}
}
