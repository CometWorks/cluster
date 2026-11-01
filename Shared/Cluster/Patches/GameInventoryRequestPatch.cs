using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.SessionComponents;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// A Steam client's skin inventory check is never answered: a node is not a Steam game server, so it
/// cannot verify one, and answering is what made the check loop.
/// </summary>
/// <remarks>
/// Vanilla: the client sends its Steam inventory check data (RequestUpdateClientGameInventory). The
/// server runs <c>MyGameService.CheckItemData</c> for up to 5 ticks and answers
/// UpdateClientGameInventoryResult(false) when it never verifies. The client answers a false by
/// sending the check again, at once. On a cluster node CheckItemData never succeeds (Steam lives in
/// gateway-rs, not in the node), so the CH's Steam client logged "Server failed to update game
/// inventory items." about 8 times a second from world-ready on (2026-09-27 22:34), each round costing
/// the node 5 CheckItemData calls. Without an answer the client sends one check per inventory refresh.
/// Skins behave as before: the node never had the client's items, so owned skins were already
/// rejected. Direct Transport pilots have no Steam inventory and never send the check.
/// </remarks>
[HarmonyPatch(typeof(MySessionComponentGameInventory), nameof(MySessionComponentGameInventory.RequestUpdateClientGameInventory))]
public static class GameInventoryRequestPatch
{
	private static readonly HashSet<ulong> Logged = new();

	[HarmonyPrefix]
	private static bool Prefix()
	{
		try
		{
			ulong sender = MyEventContext.Current.Sender.Value;
			if (Logged.Add(sender))
				Console.WriteLine($"[ClusterPlugin] game_inventory_check_ignored client={sender} (a node cannot verify Steam inventory; logged once per client)");
		}
		catch (Exception)
		{
		}
		return false;
	}
}
