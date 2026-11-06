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
	/// </summary>
	[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.Peer2Peer_SessionRequest))]
	public static class SessionTakeover
	{
		[HarmonyPrefix]
		private static void Prefix(MyDedicatedServerBase __instance, ulong remoteUserId)
		{
			if (!ClusterRuntime.IsClustered || remoteUserId == __instance.ServerId || !__instance.m_members.Contains(remoteUserId))
				return;
			int copies = __instance.m_members.Count(member => member == remoteUserId);
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] stale_member_cleared client={remoteUserId} copies={copies} (SE-1156)");
			DirectTransportNodeLinkPatch.KeepSession = remoteUserId;
			try
			{
				__instance.RaiseClientLeft(remoteUserId, MyChatMemberStateChangeEnum.Disconnected);
			}
			finally
			{
				DirectTransportNodeLinkPatch.KeepSession = 0;
			}
			__instance.m_members.RemoveAll(member => member == remoteUserId);
		}
	}

	/// <summary>
	/// A CLIENT_CONNECTED that reaches the game thread after its session was closed belongs to nobody.
	/// Vanilla would still make the sender a member, with no session left to ever remove it.
	/// </summary>
	[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.ClientConnected))]
	public static class ClientConnected
	{
		[HarmonyPrefix]
		private static bool Prefix(MyPacket packet)
		{
			ulong client = packet.Sender.Id.Value;
			if (!ClusterRuntime.IsClustered || DirectTransportNodeLinkPatch.HasSession(client) != false)
				return true;
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] client_connected_dropped client={client}: its session is closed (SE-1156)");
			packet.Return();
			return false;
		}
	}
}
