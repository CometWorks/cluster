using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// SE1-0232: a contract block's offers travel in its object builder. The logic is
/// <see cref="ContractOffers"/>; every hook is inert outside a cluster and on the World Authority.
/// </summary>
public static class MyContractBlockPatch
{
	[HarmonyPatch(typeof(MyContractBlock), nameof(MyContractBlock.Init), typeof(MyObjectBuilder_CubeBlock), typeof(MyCubeGrid))]
	private static class Init
	{
		[HarmonyPostfix]
		private static void Postfix(MyContractBlock __instance, MyObjectBuilder_CubeBlock objectBuilder)
			=> ContractOffers.Initialized(__instance, objectBuilder);
	}

	[HarmonyPatch(typeof(MyContractBlock), nameof(MyContractBlock.GetObjectBuilderCubeBlock))]
	private static class GetObjectBuilder
	{
		[HarmonyPostfix]
		private static void Postfix(MyContractBlock __instance, MyObjectBuilder_CubeBlock __result)
			=> ContractOffers.Saving(__instance, __result);
	}

	// MyContractBlock does not override OnAddedToScene; the nearest override is its base's.
	[HarmonyPatch(typeof(MyFunctionalBlock), nameof(MyFunctionalBlock.OnAddedToScene))]
	private static class AddedToScene
	{
		[HarmonyPostfix]
		private static void Postfix(MyFunctionalBlock __instance)
		{
			if (__instance is MyContractBlock block) ContractOffers.AddedToScene(block);
		}
	}

	// The block's OnClose handler: vanilla drops its offers (refunding a player-made one when it still finds the
	// block). A close the cluster makes keeps them; a real destruction also tells the contract ledger (SE1-0247).
	[HarmonyPatch(typeof(MySessionComponentContractSystem), nameof(MySessionComponentContractSystem.ContractBlockDestroyed))]
	private static class BlockClosed
	{
		[HarmonyPrefix]
		private static bool Prefix(long entityId)
		{
			if (ContractOffers.KeepOnClose(entityId)) return false;
			ContractLedger.BlockGone(entityId);
			return true;
		}
	}
}
