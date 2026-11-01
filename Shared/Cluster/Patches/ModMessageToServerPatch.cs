using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.ModAPI;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// A node takes every mod message a client sends to the server (<c>SendMessageToServer</c>) as its own,
/// whatever server id the client addressed it to.
/// </summary>
/// <remarks>
/// Vanilla: <c>SendMessageToServer</c> raises ModMessageServerReliable/Unreliable with
/// <c>recipient = Sync.ServerId</c> as the client knows it, and the server hands the message to the mod
/// only when <c>recipient == Sync.MyId</c> (HandleMessageClient). A node's own id is the Direct Transport
/// server id, the id a Direct Transport client knows its server by; a Steam client through gateway-rs
/// knows the gateway's GameServer id instead (MyMultiplayerClient: ServerId = server.SteamID). So every
/// mod request of a Steam client was dropped without a trace: on octahedron-1191e the Trade Lanes
/// lane requests of Steam clients never got an answer; Viktor's Steam client drew only the lanes a node happened
/// to broadcast (15 of 20 after two hours) and gate rings were missing at the stations (DF-671). Both
/// events are server-only, so the server is the only recipient they can have. Self-test section
/// <c>mod-message-to-server</c>.
/// </remarks>
[HarmonyPatch]
public static class ModMessageToServerPatch
{
	private static readonly HashSet<ulong> Logged = new();

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.DeclaredMethod(typeof(MyModAPIHelper.MyMultiplayer), nameof(MyModAPIHelper.MyMultiplayer.ModMessageServerReliable));
		yield return AccessTools.DeclaredMethod(typeof(MyModAPIHelper.MyMultiplayer), nameof(MyModAPIHelper.MyMultiplayer.ModMessageServerUnreliable));
	}

	[HarmonyPrefix]
	private static void Prefix(ref ulong recipient)
	{
		if (!ClusterRuntime.IsClustered || !Sync.IsServer || recipient == Sync.MyId)
			return;
		try
		{
			ulong sender = MyEventContext.Current.Sender.Value;
			if (Logged.Add(sender))
				Console.WriteLine($"[ClusterPlugin] mod_message_to_server client={sender} addressed={recipient} (taken as this node's; logged once per client)");
		}
		catch (Exception)
		{
		}
		recipient = Sync.MyId;
	}
}
