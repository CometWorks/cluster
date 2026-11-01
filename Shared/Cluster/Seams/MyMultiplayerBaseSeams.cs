using System;
using Sandbox.Engine.Multiplayer;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyMultiplayerBase"/>. Harmony side: Shared/Patches/MyMultiplayerBasePatch.cs
/// (ChatAuthorityPatch.RequestPrefix on the private static OnChatMessageReceived_Server).
/// </summary>
public static class MyMultiplayerBaseSeams
{
	public static Func<ChatMsg, bool> ClusterChatRequestPrefix;

	/// <summary>
	/// ClusterPlugin seam adapter: ChatAuthorityPatch.RelayAtReplica. Raises the private registered
	/// [Client] event OnChatMessageReceived_SingleTarget(ChatMsg) at one attached client. The method is
	/// reached through the publicized assembly - no reflection.
	/// </summary>
	public static void ClusterRelayChat(ChatMsg msg, ulong recipient)
	{
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner s) => MyMultiplayerBase.OnChatMessageReceived_SingleTarget, msg, new EndpointId(recipient));
	}
}
