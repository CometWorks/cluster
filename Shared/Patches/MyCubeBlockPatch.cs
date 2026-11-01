using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Shared.Tools;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// Unknown-identity preservation, Owner half. MyCubeBlock.Init(builder, grid)
/// calls the private <c>InitOwnership(builder)</c>, which zeroes <c>builder.Owner</c> when
/// <c>!Sync.Players.HasIdentity(builder.Owner)</c> (the patch targets InitOwnership, not Init, whose stock IL
/// holds no HasIdentity call at all). On a cluster node an
/// unknown identity is the NORMAL case for an imported block, not a deleted player: identities live on
/// the world authority and reach a freshly started node by replication, which can trail the first
/// handover onto it by seconds (w22 2026-08-21 17:41:33, a 58 s old node-5 zeroed the Owner of a
/// projector whose owner it learnt of 7 s later - a 32-residual abort). The ledger's "prefix replacing
/// the builder.Owner check" cannot be a Harmony prefix (the check is mid-method), so the transpiler
/// feeds the HasIdentity result through PreserveOwner, which ORs in the flag. Off = vanilla.
/// Stock 1.210.014 IL of InitOwnership at the anchor:
/// <c>call Sync::get_Players; ldarg.1; ldfld Owner; callvirt MyPlayerCollection::HasIdentity(int64); brtrue.s</c>
/// - the only HasIdentity call in the method; PreserveOwner is inserted right after it.
/// </summary>
[HarmonyPatch(typeof(MyCubeBlock), "InitOwnership", new[] { typeof(MyObjectBuilder_CubeBlock) })]
public static class MyCubeBlockPatch
{
    /// <summary>Transpiler call site: the value the ownership restore treats as "identity known".</summary>
    public static bool PreserveOwner(bool hasIdentity)
    {
        return hasIdentity || MyCubeBlockSeams.ClusterPreserveUnknownOwner;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> InitOwnershipTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var il = instructions.ToList();
        var hasIdentity = AccessTools.Method(typeof(MyPlayerCollection), nameof(MyPlayerCollection.HasIdentity), new[] { typeof(long) })
                          ?? throw new CodeInstructionNotFound("MyPlayerCollection.HasIdentity(long) not found");
        var preserve = AccessTools.Method(typeof(MyCubeBlockPatch), nameof(PreserveOwner));

        var calls = il.FindAllIndex(ci => ci.Calls(hasIdentity));
        if (calls.Count != 1)
            throw new CodeInstructionNotFound($"{original.Name}: expected exactly one Sync.Players.HasIdentity call, found {calls.Count}");

        il.Insert(calls[0] + 1, new CodeInstruction(OpCodes.Call, preserve));
        return il;
    }
}
