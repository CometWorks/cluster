using System;
using System.Linq;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// SE-1192: F3 kick and ban on the row of a player hosted by ANOTHER node are refused on this node.
///
/// The F3 list shows every cluster player (<see cref="RemotePlayerMirrorService"/>), and its Kick and Ban buttons
/// raise vanilla's KickUser/BanUser on the node the admin is attached to. That node does not host the player, so
/// vanilla misfires: the player is not kicked, the id goes onto THIS node's kick list or ban config only (the
/// player is refused later when they cross into this node), and the OnUserKicked/OnUserBanned broadcast takes the
/// row off this node's clients for good, while the mirror still books it as delivered. Kick and ban of a remote
/// player belong to the Gateway's cluster-wide moderation (Quasar's player admin), so the node refuses them and
/// tells the admin where to go. Unkick and unban, and players attached here or online nowhere, keep vanilla's path.
/// </summary>
public static class RemoteRowModerationPatch
{
	private const string Advice = "is on another node of the cluster. Kick or ban them from the cluster admin (Quasar) instead.";

	private static long m_refused;

	/// <summary>Kicks and bans refused since the start, for /cluster/player-mirror.</summary>
	public static long Refused => m_refused;

	[HarmonyPatch(typeof(MyMultiplayerServerBase), nameof(MyMultiplayerServerBase.KickClient))]
	public static class KickClient
	{
		[HarmonyPrefix]
		private static bool Prefix(ulong userId, bool kicked) => !kicked || !Refuse(userId, "kick");
	}

	[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.BanClient))]
	public static class BanClient
	{
		[HarmonyPrefix]
		private static bool Prefix(ulong userId, bool banned) => !banned || !Refuse(userId, "ban");
	}

	/// <summary>True when <paramref name="userId"/> is hosted by another node and not attached here.</summary>
	private static bool Refuse(ulong userId, string action)
	{
		if (!ClusterRuntime.IsClustered || Sync.Clients?.HasClient(userId) == true) return false;
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		OnlinePlayersService.OnlinePlayerEntry remote = OnlinePlayersService.Cluster
			.FirstOrDefault(entry => entry.SteamId == userId && !string.Equals(entry.Node, node, StringComparison.Ordinal));
		if (remote == null) return false;

		m_refused++;
		MyEventContext context = MyEventContext.Current;
		ulong admin = context.IsValid && !context.IsLocallyInvoked ? context.Sender.Value : 0;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + action + " refused for player=" + userId + " on node="
			+ remote.Node + " by=" + admin + " (SE-1192)");
		if (admin != 0)
			(MyMultiplayer.Static as MyDedicatedServerBase)?.SendChatMessageToPlayer(remote.Name + " " + Advice, admin);
		return true;
	}
}
