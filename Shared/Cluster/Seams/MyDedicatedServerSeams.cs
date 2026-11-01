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
}
