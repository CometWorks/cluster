using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;

namespace Shared.Patches;

/// <summary>
/// SE-1203: a cluster node never refuses a client as ServerFull. The player cap is the Gateway's admission
/// memberLimit, counted over the whole cluster, and the Gateway has already let the client in when the node sees
/// the join.
///
/// Vanilla SetUserConnectionResult refuses a join once the node's members (its own id included) reach the member
/// limit, and that limit is the node world's MaxPlayers. Nothing ties it to the Gateway's memberLimit, and a node
/// can hold more players than the world allows: logins routed to it, spawn preview switches (SE-1124), respawns
/// and handovers all bring players in, and each arrival is a vanilla join. The refused client got no clear error,
/// and a Direct Transport client rejoined every 5 s, forever (the bench world's MaxPlayers is 2).
///
/// A postfix on SetMemberLimit, which every source of the limit goes through (the dedicated config at start, the
/// world's MaxPlayers at load, an admin's change), so worlds and keep-world deployments need no change.
/// </summary>
[HarmonyPatch(typeof(MyDedicatedServerBase), nameof(MyDedicatedServerBase.SetMemberLimit))]
public static class NodeMemberLimitPatch
{
    /// <summary>The largest MaxPlayers a world can hold (it is a short), far above any cluster's memberLimit.</summary>
    public const int NodeMemberLimit = short.MaxValue;

    [HarmonyPostfix]
    private static void Postfix(MyDedicatedServerBase __instance)
    {
        if (!ClusterRuntime.IsClustered || __instance.m_membersLimit >= NodeMemberLimit)
            return;
        __instance.m_membersLimit = NodeMemberLimit;
        __instance.m_gameServerDataDirty = true;
    }
}
