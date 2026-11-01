using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage.Game;
using MyGridStorageRequestResult = Sandbox.ModAPI.MyGridStorageRequestResult;

namespace Shared.Patches;

/// <summary>
/// SE1-0233: ship storage is World Authority state. The logic is <see cref="ShipStorageService"/>; every hook is
/// inert outside a cluster, and the regular-node hooks are inert on the World Authority.
/// </summary>
public static class MyGridsStorageSessionComponentPatch
{
	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.Init))]
	private static class Init
	{
		[HarmonyPrefix]
		private static void Prefix(MyObjectBuilder_SessionComponent sessionComponentBuilder)
			=> ShipStorageService.Initializing(sessionComponentBuilder as MyObjectBuilder_GridsStorageSessionComponent);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent.StoreRequest), nameof(MyGridsStorageSessionComponent.StoreRequest.Dispatch))]
	private static class Store
	{
		[HarmonyPostfix]
		private static void Postfix(MyGridsStorageSessionComponent.StoreRequest __instance, MyGridsStorageSessionComponent context)
			=> ShipStorageService.Stored(context, __instance);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent.RetrieveRequest), nameof(MyGridsStorageSessionComponent.RetrieveRequest.Dispatch))]
	private static class Retrieve
	{
		[HarmonyPrefix]
		private static bool Prefix(MyGridsStorageSessionComponent.RetrieveRequest __instance)
			=> ShipStorageService.Retrieving(__instance);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent.RetrieveRequest), nameof(MyGridsStorageSessionComponent.RetrieveRequest.Notify))]
	private static class Retrieved
	{
		[HarmonyPrefix]
		private static void Prefix(MyGridsStorageSessionComponent.RetrieveRequest __instance, MyGridStorageRequestResult res)
			=> ShipStorageService.Retrieved(__instance, res);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.DeleteGrid))]
	private static class Delete
	{
		[HarmonyPrefix]
		private static bool Prefix(MyGridsStorageSessionComponent __instance, Guid id, ref bool __result)
			=> ShipStorageService.Delete(__instance, id, ref __result);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.OnIdentityCleanedUp))]
	private static class IdentityCleanedUp
	{
		[HarmonyPostfix]
		private static void Postfix(long identityId) => ShipStorageService.IdentityRemoved(identityId);
	}

	[HarmonyPatch(typeof(MyGridsStorageEntityComponent), nameof(MyGridsStorageEntityComponent.SetOwnershipKindEndpoint))]
	private static class Share
	{
		[HarmonyPostfix]
		private static void Postfix(Guid id, Sandbox.ModAPI.MyStoredGridOwnershipKind kind)
			=> ShipStorageService.Shared(id, (MyStoredGridOwnershipKind)kind);
	}

	// SE1-0248: retrieval orders and auto-deploy switches.

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.EnqueueOrder))]
	private static class Enqueue
	{
		[HarmonyPrefix]
		private static void Prefix(MyGridsStorageSessionComponent __instance, long clientId, out int __state)
			=> __state = __instance.GetCurrentClientQueueLength(clientId);

		[HarmonyPostfix]
		private static void Postfix(MyGridsStorageSessionComponent __instance, long clientId, Guid id, int __state)
			=> ShipStorageService.Enqueued(__instance, clientId, id, __state);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.RemoveOrder))]
	private static class Dequeue
	{
		[HarmonyPrefix]
		private static void Prefix(MyGridsStorageSessionComponent __instance, long clientId)
			=> ShipStorageService.Dequeuing(__instance, clientId);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.SetAutoDeploySwitch))]
	private static class AutoDeploy
	{
		[HarmonyPostfix]
		private static void Postfix(long clientId, bool value) => ShipStorageService.AutoDeploySet(clientId, value);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.ProcessOrder))]
	private static class Process
	{
		[HarmonyPrefix]
		private static bool Prefix(Sandbox.ModAPI.MyGridRetrievalOrder currentOrder) => ShipStorageService.Processing(currentOrder);
	}

	[HarmonyPatch(typeof(MyGridsStorageSessionComponent), nameof(MyGridsStorageSessionComponent.RemoveOrdersForTerminal))]
	private static class TerminalOrders
	{
		[HarmonyPrefix]
		private static bool Prefix() => ShipStorageService.RemovingTerminalOrders();
	}

	[HarmonyPatch(typeof(MyGridsStorageEntityComponent), nameof(MyGridsStorageEntityComponent.OnBlockEntityClosing))]
	private static class TerminalClosing
	{
		[HarmonyPrefix]
		private static void Prefix(MyGridsStorageEntityComponent __instance) => ShipStorageService.TerminalClosing(__instance.Entity);
	}

	[HarmonyPatch(typeof(MyGridsStorageEntityComponent), nameof(MyGridsStorageEntityComponent.ExpediteCurrentClientOrderEndpoint))]
	private static class Expedite
	{
		[HarmonyPostfix]
		private static void Postfix(MyGridsStorageEntityComponent __instance) => ShipStorageService.Expedited(__instance.m_sessionComponent);
	}
}
