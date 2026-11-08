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

	// True while the World Authority accepts a line a regular node forwarded: OnChatMessage already ran on
	// the sender's node, so the WA skips it and only the accepted postfix runs.
	public static Func<bool> ClusterChatForwarded;
}
