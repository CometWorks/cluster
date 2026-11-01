using System.Threading;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// Unknown-identity preservation, BuiltBy half. On a cluster node an
/// identity the node has not seen is the normal case for an imported block (identities live on the
/// world authority and arrive by replication), not a deleted player. Vanilla AddAuthorship zeroes
/// BuiltBy, which loses the data and fails the handover round-trip gate ("N != N-1 child elements";
/// 2026-08-14 partition 406). With the flag set and the identity absent the prefix keeps the vanilla
/// session PCU accounting and skips only the zeroing; block-limit accounting is already skipped either
/// way when the identity is absent. Off = vanilla.
/// </summary>
[HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.AddAuthorship))]
public static class MySlimBlockPatch
{
    [HarmonyPrefix]
    private static bool AddAuthorshipPrefix(MySlimBlock __instance)
    {
        if (!MySlimBlockSeams.ClusterPreserveUnknownAuthorship)
            return true;
        var builtBy = __instance.m_builtByID;
        if (builtBy == 0L || MySession.Static.Players.TryGetIdentity(builtBy) != null)
            return true;
        Interlocked.Add(ref MySession.Static.TotalSessionPCU, __instance.BlockDefinition.PCU);
        return false;
    }
}
