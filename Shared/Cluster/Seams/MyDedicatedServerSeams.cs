using System;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="Sandbox.Engine.Multiplayer.MyDedicatedServer"/>. Harmony side:
/// Shared/Patches/MyDedicatedServerPatch.cs (ChatAuthorityPatch.AcceptedPostfix).
/// </summary>
public static class MyDedicatedServerSeams
{
	// (accepted message, sender steam id as vanilla resolved it) after OnChatMessage ran unchanged.
	public static Action<ChatMsg, ulong> ClusterChatAccepted;

	// (sender) true: skip OnChatMessage, the accepted postfix still runs. The World Authority leaves a player's
	// line to the player's node (ChatAuthorityPatch.SkipAccepted).
	public static Func<ulong, bool> ClusterChatSkipAccepted;
}
