using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Common.ObjectBuilders;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// SE1-0252: admin safe zones are World Authority state. The logic is <see cref="AdminSafeZones"/>; every hook is
/// inert outside a cluster, on the World Authority, on clients and while a node applies the World Authority's set.
/// </summary>
public static class MySessionComponentSafeZonesPatch
{
	[HarmonyPatch(typeof(MySessionComponentSafeZones), nameof(MySessionComponentSafeZones.CrateSafeZone))]
	private static class Create
	{
		[HarmonyPostfix]
		private static void Postfix(MyEntity __result) => AdminSafeZones.Created(__result);
	}

	[HarmonyPatch(typeof(MySessionComponentSafeZones), nameof(MySessionComponentSafeZones.UpdateSafeZone))]
	private static class Update
	{
		[HarmonyPostfix]
		private static void Postfix(MyObjectBuilder_SafeZone ob) => AdminSafeZones.Updated(ob.EntityId);
	}

	[HarmonyPatch(typeof(MySessionComponentSafeZones), nameof(MySessionComponentSafeZones.DeleteSafeZone_Implementation))]
	private static class Delete
	{
		[HarmonyPrefix]
		private static void Prefix(long entityId) => AdminSafeZones.Deleting(entityId);
	}
}
