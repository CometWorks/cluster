using System.Linq;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage;
using VRage.GameServices;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// SE-1156: a player the node still lists as a member after their session ended can never join it
/// again. Vanilla OnConnectedClient answers every later login with JOIN_RESULT AlreadyJoined ("User
/// already connected to server"), and nothing else removes the entry.
///
/// The live case (octahedron-test, node-0, 2026-11-06 04:11:33Z): a client's Disconnected control
/// message from its previous connection was relayed at the start of its next one, ahead of its
/// CLIENT_CONNECTED. The node ran the leave, whose CloseSession detached the new session, and then
/// processed the CLIENT_CONNECTED already in its queue: the player became a member with no session
/// behind it. Every login after that got AlreadyJoined, and the auth answer vanilla requests before
/// that check added the member again, so the leave that followed each refusal never emptied the list.
/// </summary>
public static class StaleMemberPatch
{
	/// <summary>
	/// A new session for a client the node still lists is a reconnect: the old one is gone, because the
	/// Gateway lets an identity have one session at a time. Run the leave the old session never got,
	/// then let the new one join. The leave's CloseSession must not close the new session.
	///
	/// SE-1444: not when the new session's own CLIENT_CONNECTED got to the game thread first (a hitch
	/// while many clients join). Then the listed member is this login's, and clearing it left the
	/// client with no player and its OnNewPlayerRequest unanswered.
	/// </summary>
	[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.Peer2Peer_SessionRequest))]
	public static class SessionTakeover
	{
		[HarmonyPrefix]
		private static void Prefix(MyDedicatedServerBase __instance, ulong remoteUserId)
		{
			if (DirectTransportNodeLinkPatch.SessionRequestQueued.TryRemove(remoteUserId, out bool loginFirst) && loginFirst)
			{
				MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] session_request_after_login client={remoteUserId}: the member is this login's (SE-1444)");
				return;
			}
			if (!ClusterRuntime.IsClustered || remoteUserId == __instance.ServerId || !__instance.m_members.Contains(remoteUserId))
				return;
			Clear(__instance, remoteUserId);
		}
	}

	/// <summary>
	/// A CLIENT_CONNECTED that reaches the game thread after its session was closed belongs to nobody.
	/// Vanilla would still make the sender a member, with no session left to ever remove it.
	///
	/// SE-1444: one that arrives before its session's SessionRequest does the takeover itself, so a stale
	/// entry is gone before vanilla checks the member list, and marks the session so that the late
	/// SessionRequest leaves the member it adds alone.
	/// </summary>
	[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.ClientConnected))]
	public static class ClientConnected
	{
		[HarmonyPrefix]
		private static bool Prefix(MyDedicatedServerBase __instance, MyPacket packet)
		{
			ulong client = packet.Sender.Id.Value;
			if (!ClusterRuntime.IsClustered)
				return true;
			if (DirectTransportNodeLinkPatch.HasSession(client) == false)
			{
				MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] client_connected_dropped client={client}: its session is closed (SE-1156)");
				packet.Return();
				return false;
			}
			if (DirectTransportNodeLinkPatch.SessionRequestQueued.TryUpdate(client, true, false))
			{
				MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] client_connected_before_session_request client={client} (SE-1444)");
				if (__instance.m_members.Contains(client))
					Clear(__instance, client);
			}
			return true;
		}
	}

	private static void Clear(MyDedicatedServerBase server, ulong client)
	{
		int copies = server.m_members.Count(member => member == client);
		MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] stale_member_cleared client={client} copies={copies} (SE-1156)");
		DirectTransportNodeLinkPatch.KeepSession = client;
		try
		{
			server.RaiseClientLeft(client, MyChatMemberStateChangeEnum.Disconnected);
		}
		finally
		{
			DirectTransportNodeLinkPatch.KeepSession = 0;
		}
		server.m_members.RemoveAll(member => member == client);
	}
}
