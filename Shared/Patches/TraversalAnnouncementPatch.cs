using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.GameServices;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// T-0179: stop a node traversal from looking like a logout followed by a login.
///
/// When a client crosses from one node to another the target announces "X connected" and the
/// source "X disconnected". Neither is true - X never left the world - and nobody outside those
/// two nodes sees anything at all. Both messages are server-triggered HUD notifications; the
/// client never derives them from a member-list diff, so suppressing them is entirely a
/// server-side job and needs no client plugin and no protocol change.
///
/// The node already knows a traversal is under way before either vanilla event fires: the
/// Gateway records the transfer at /capture on the source and /prepare-import on the target,
/// both strictly before the detach and the replayed CLIENT_CONNECTED. So the marker is simply
/// read back here.
///
/// BOTH PATCHES FAIL SAFE. If the marker is absent the vanilla behaviour stands. Announcing a
/// traversal is cosmetic; silently swallowing a real login or logout misrepresents who is in the
/// world, and those two errors do not deserve equal treatment.
///
/// PAIRED WITH T-0178 AND NOT DEPLOYABLE ALONE. Suppressing the disconnect leaves X in the source
/// node's clients' m_members and Sync.Clients, and MyClientCollection.RemoveClient on an id that
/// is not there only logs - so without the remote-player mirror's cluster-wide removal those
/// entries are never cleaned up. The mirror is what makes this correct rather than merely quiet.
/// </summary>
public static class TraversalAnnouncementPatch
{
	/// <summary>
	/// Join half. MyDedicatedServerBase.SetUserAccepted sends ConnectedClientDataMsg to every
	/// member; the client stores the member and raises ClientJoined regardless of Join, and shows
	/// the toast only when Join is true. Clearing the flag therefore keeps every member list
	/// correct and removes only the notification.
	/// </summary>
	// The argument types are spelled out because the parameter is BY REF: an attribute naming only
	// the method leaves Harmony to resolve an overload set at startup, and PatchHelpers.HarmonyPatchAll
	// fails loudly there rather than silently, so an ambiguity would take the node down at launch
	// instead of at build. ArgumentType.Ref is how a `ref` parameter is expressed in an attribute.
	[HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.SendClientConnected),
		new[] { typeof(ConnectedClientDataMsg), typeof(ulong) },
		new[] { ArgumentType.Ref, ArgumentType.Normal })]
	public static class SendClientConnected
	{
		[HarmonyPrefix]
		private static void Prefix(ref ConnectedClientDataMsg msg, ulong sendTo)
		{
			if (!ClusterRuntime.IsClustered) return;
			RemotePlayerMirrorService.NoteMemberSent(sendTo, msg.ClientId.Value);   // SE-1192
			if (!msg.Join) return;
			ulong client = msg.ClientId.Value;
			if (!ClusterControlServer.IsArrivingByHandover(client)) return;
			msg.Join = false;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] traversal join toast suppressed client="
				+ client + " (T-0179)");
		}
	}

	/// <summary>
	/// Leave half. The goal is to skip ONLY the MyControlDisconnectedMsg broadcast inside
	/// MyDedicatedServerBase.MyDedicatedServer_ClientLeft, while CloseSession, the member and
	/// pending-member removals, SendUserDisconnect and MemberDataRemove all still run - otherwise
	/// the source node stops tearing the session down properly.
	///
	/// No transpiler is needed, because vanilla already has a switch for exactly this. The body
	/// (decompiled, Sandbox.Game.dll) wraps the broadcast in
	///
	///     if (arg2 != MyChatMemberStateChangeEnum.Kicked &amp;&amp; arg2 != MyChatMemberStateChangeEnum.Banned)
	///
	/// and arg2 appears NOWHERE ELSE in the method - it is read by that one condition and nothing
	/// more. So presenting the departure as Kicked suppresses the broadcast and leaves every other
	/// statement untouched. That is the whole patch: one assignment, no IL rewriting, and nothing
	/// that a game update can silently shift out from under us the way an IL anchor can.
	///
	/// The reason vanilla skips the broadcast when kicked is the same reason we want it skipped
	/// here: the clients are about to be told what happened by a more specific message. In our
	/// case that message is T-0178's mirror removal, which is why this must not ship without it.
	/// </summary>
	[HarmonyPatch(typeof(MyDedicatedServerBase), "MyDedicatedServer_ClientLeft")]
	public static class ClientLeft
	{
		[HarmonyPrefix]
		private static void Prefix(ulong user, ref MyChatMemberStateChangeEnum arg2, out System.Action __state)
		{
			__state = null;
			if (!ClusterRuntime.IsClustered) return;
			// Already suppressed by vanilla; leave the reason vanilla was given intact. A kick or ban removes the
			// member on every client here by its own broadcast, like a disconnect does (SE-1192).
			if (arg2 == MyChatMemberStateChangeEnum.Kicked || arg2 == MyChatMemberStateChangeEnum.Banned
				|| !ClusterControlServer.IsLeavingByHandover(user))
			{
				RemotePlayerMirrorService.NoteMemberGone(user);
				if (arg2 != MyChatMemberStateChangeEnum.Kicked && arg2 != MyChatMemberStateChangeEnum.Banned)
					__state = RemotePlayerMirrorService.BeforeMemberGone(user);   // SE-1476
				return;
			}
			arg2 = MyChatMemberStateChangeEnum.Kicked;
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] traversal leave toast suppressed client="
				+ user + " (T-0179)");
		}

		/// <summary>
		/// SE-1476: vanilla's disconnect broadcast also reached the clients riding a handover out of this node, which
		/// the next node trusts to hold this member (they were exported holding it). Give it back to them.
		/// </summary>
		[HarmonyPostfix]
		private static void Postfix(System.Action __state) => __state?.Invoke();
	}
}
