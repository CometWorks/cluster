using System;
using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;

namespace ClusterPlugin;

/// <summary>
/// Graceful cluster shutdown, step 3 (Viktor 2026-09-26): send every client attached to this node back
/// to the main menu before the drain, instead of leaving it to a dropped connection.
///
/// It sends vanilla's "server left" control message to that one client:
/// <c>MyControlDisconnectedMsg { Client = ServerId }</c> (what <c>MyDedicatedServerBase.Dispose</c> sends
/// every member). On the client <c>OnDisconnectedClient</c> raises <c>ClientLeft(ServerId)</c>, so
/// <c>MyMultiplayerClient_HostLeft</c> closes the session, runs <c>MySessionLoader.UnloadAndExitToMenu</c>
/// and opens vanilla's <c>MyGuiScreenServerReconnector</c>: "Reconnecting in N s" with a Cancel button,
/// pinging the server and rejoining by itself once the cluster is back (Viktor 2026-09-27: this, not the
/// kick, whose client side always adds the "You have been kicked" error box). Unlike vanilla
/// <c>DisconnectClient</c> it does not raise ClientLeft on the node: the player leaves when the client
/// closes its link, the same way as a normal logout. The gateway relays channel 0 unchanged.
///
/// The registry calls this repeatedly until its leave deadline, so a client that attaches here after
/// the first pass (mid-handover, still joining) is sent too. Each client is sent once per process: a
/// second message reaching a client that is already unloading would start a second unload.
/// Game thread only.
/// </summary>
internal static class ShutdownReturnToMenu
{
	private static readonly HashSet<ulong> Returned = new();

	/// <summary>True once the registry has run the return-to-menu pass on this node: the node is going
	/// down and every login from now on is a reconnector retry (see <see cref="ShutdownLoginPatch"/>).</summary>
	public static volatile bool Active;

	/// <param name="frontServerId">The gateway's Steam GameServer id, 0 if it has none. A vanilla client acts
	/// on the message only when Client is the id IT knows the server by (MyMultiplayerClient_ClientLeft:
	/// <c>user == ServerId</c>, set from <c>server.SteamID</c> at join). A Direct Transport client knows the
	/// node's own ServerId; a Steam client through gateway-rs knows the GameServer id. With any other id the
	/// client logs "Disconnected" and treats it as some member leaving: that is what the CH's Steam client did
	/// at the 22:16 stop (2026-09-27), staying in-world until the Steam session closed 30 s later. So a
	/// client gets one message per id; the one naming its host sends it to the menu, the other is a no-op.</param>
	/// <returns>The ids sent back to the menu by THIS call.</returns>
	public static List<ulong> ReturnAttachedClients(ulong frontServerId = 0)
	{
		var sent = new List<ulong>();
		Active = true;
		MyClientCollection clients = Sync.Clients;
		if (clients == null || MyMultiplayer.Static == null) return sent;
		foreach (var client in clients.GetClients())
		{
			ulong userId = client.SteamUserId;
			if (userId == 0 || userId == Sync.ServerId || !Returned.Add(userId)) continue;
			var message = new MyControlDisconnectedMsg { Client = Sync.ServerId };
			MyMultiplayer.Static.SendControlMessage(userId, ref message);
			if (frontServerId != 0 && frontServerId != Sync.ServerId)
			{
				var front = new MyControlDisconnectedMsg { Client = frontServerId };
				MyMultiplayer.Static.SendControlMessage(userId, ref front);
			}
			sent.Add(userId);
		}
		if (sent.Count > 0)
			Console.WriteLine($"[ClusterPlugin] shutdown return_to_menu clients={sent.Count} total={Returned.Count} frontServerId={frontServerId} ids={string.Join(",", sent)}");
		return sent;
	}
}

/// <summary>
/// T-0248: after the return-to-menu pass, a login is left unanswered instead of getting JoinResult
/// AlreadyJoined.
/// </summary>
/// <remarks>
/// Vanilla's reconnector retries 10 s after the host-left, often on the same Steam P2P session, whose
/// node still lists the player (it leaves only when its link closes). OnConnectedClient then answered
/// AlreadyJoined, an error box that ends the reconnector (2026-09-28 03:41:52, semods client: node-2
/// "OnConnectedClient semods attempt" / "Already joined"). The gateway's own guard
/// (DirectGateway.OnClientMessage, login-while-admission-closed) cannot stop it on gateway-rs: the relay
/// forwards CLIENT_CONNECTED to the node and reports it to C# side by side (relay/src/lib.rs
/// on_client_packet), so the login reaches the node anyway. Unanswered, the client gets only the
/// gateway's host-left from that guard and goes back to the reconnector.
/// </remarks>
[HarmonyLib.HarmonyPatch(typeof(MyDedicatedServerBase), "OnConnectedClient")]
public static class ShutdownLoginPatch
{
	[HarmonyLib.HarmonyPrefix]
	private static bool Prefix(ulong steamId)
	{
		if (!ShutdownReturnToMenu.Active) return true;
		Console.WriteLine($"[ClusterPlugin] shutdown login_ignored client={steamId} (node returned its clients to the menu; no AlreadyJoined)");
		return false;
	}
}
