using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace Shared.Patches;

/// <summary>
/// #1381: a meteor's birth travels in its object builder, so a load does not restart its lifetime. The logic is
/// <see cref="MeteorLifetime"/>; every hook is inert outside a cluster, on the World Authority and on clients.
/// </summary>
public static class MyMeteorPatch
{
	[HarmonyPatch(typeof(MyMeteor.MyMeteorGameLogic), nameof(MyMeteor.MyMeteorGameLogic.Init), typeof(MyObjectBuilder_EntityBase))]
	private static class Init
	{
		[HarmonyPostfix]
		private static void Postfix(MyMeteor.MyMeteorGameLogic __instance, MyObjectBuilder_EntityBase objectBuilder)
			=> MeteorLifetime.Initialized(__instance, objectBuilder);
	}

	[HarmonyPatch(typeof(MyMeteor.MyMeteorGameLogic), nameof(MyMeteor.MyMeteorGameLogic.GetObjectBuilder))]
	private static class GetObjectBuilder
	{
		[HarmonyPostfix]
		private static void Postfix(MyMeteor.MyMeteorGameLogic __instance, MyObjectBuilder_EntityBase __result)
			=> MeteorLifetime.Saving(__instance, __result);
	}
}
