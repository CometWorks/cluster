using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// Regular nodes forward player chat to the
/// World Authority instead of validating and routing it independently. Inert while the seam is null.
/// Low priority: Magnetar's chat command prefix (and any other plugin's) runs first, on the node the
/// player is connected to, where the player's character, grids and chat replies are. Harmony skips
/// the prefixes after one that returns false, so a forwarding prefix that ran first would send every
/// command to the World Authority, which has no player to act on or answer (DF-698).
/// </summary>
[HarmonyPatch(typeof(MyMultiplayerBase), "OnChatMessageReceived_Server", new[] { typeof(ChatMsg) })]
public static class MyMultiplayerBasePatch
{
	// ClusterPlugin patch point: ChatAuthorityPatch.RequestPrefix
	[HarmonyPrefix]
	[HarmonyPriority(Priority.Low)]
	private static bool RequestPrefix(ChatMsg msg)
	{
		var seam = MyMultiplayerBaseSeams.ClusterChatRequestPrefix;
		return seam == null || seam(msg);
	}
}
