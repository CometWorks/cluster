using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// Publish only chat requests that reached
/// vanilla's accepted-message sink. The prefix captures the sender exactly as OnChatMessage resolves it
/// (event sender, or the server id for a non-local dedicated call) so the postfix hands the seam that
/// sender. On the World Authority, a line a regular node forwarded skips OnChatMessage: the node ran it
/// already, so plugins and mods see the line once, where the player is (SE-1261).
/// </summary>
[HarmonyPatch(typeof(MyDedicatedServer), nameof(MyDedicatedServer.OnChatMessage))]
public static class MyDedicatedServerPatch
{
	[HarmonyPrefix]
	private static bool Prefix(out ulong __state)
	{
		ulong sender = MyEventContext.Current.Sender.Value;
		if (sender == 0L && Sync.IsDedicated && !MyEventContext.Current.IsLocallyInvoked)
			sender = Sync.MyId;
		__state = sender;
		return MyDedicatedServerSeams.ClusterChatForwarded?.Invoke() != true;
	}

	// ClusterPlugin patch point: ChatAuthorityPatch.AcceptedPostfix
	[HarmonyPostfix]
	private static void Postfix(ref ChatMsg msg, ulong __state)
		=> MyDedicatedServerSeams.ClusterChatAccepted?.Invoke(msg, __state);
}
