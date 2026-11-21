using System;
using System.Collections.Concurrent;
using System.Threading;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Multiplayer;

namespace Shared.Patches;

/// <summary>
/// SE-1583: a character whose owner identity this node does not hold must not reach the weapon position update.
///
/// On a dedicated server <c>MyCharacterWeaponPositionComponent.Update</c> runs every frame for a character that holds
/// a tool or weapon, from the parallel <c>UpdateAfterSimulationParallel</c>, and its first statement dereferences
/// <c>Character.GetIdentity().IdentityId</c>. A character with no controlling player falls back to
/// <c>Players.TryGetIdentity(OwningPlayerIdentityId)</c>, and that was null on handover targets on 2026-11-15: the
/// imported body (a player's, whose pilot attaches only at commit, and an offline one) threw in the first frame after
/// activation, the parallel task failed, the game loop stopped the server, and the Gateway offered the partition to
/// the next node, which died the same way. Seven nodes in five minutes.
///
/// Skipping the update is what vanilla does for an offline player's body anyway (the same statement returns for it),
/// and a body nobody on this node controls has no crosshair to keep. The update resumes on its own once the identity
/// arrives or a player takes control. Each character is logged once with the owner id this node failed to resolve.
/// </summary>
[HarmonyPatch(
    typeof(MyCharacterWeaponPositionComponent),
    nameof(MyCharacterWeaponPositionComponent.Update)
)]
internal static class MyCharacterWeaponPositionComponentPatch
{
    private static readonly ConcurrentDictionary<long, byte> s_logged =
        new ConcurrentDictionary<long, byte>();
    private static long s_skips;

    /// <summary>Updates skipped for a character without an identity since start (self-test and diagnostics).</summary>
    internal static long Skips => Interlocked.Read(ref s_skips);

    [HarmonyPrefix]
    private static bool Prefix(MyCharacterWeaponPositionComponent __instance)
    {
        if (!ClusterRuntime.IsClustered || !Sync.IsDedicated)
            return true;
        MyCharacter character = __instance.Character;
        if (character == null || character.Definition == null || character.GetIdentity() != null)
            return true;
        Interlocked.Increment(ref s_skips);
        if (s_logged.TryAdd(character.EntityId, 0))
            Console.WriteLine(
                "[ClusterPlugin] character_without_identity entity="
                    + character.EntityId
                    + " name="
                    + character.DisplayName
                    + " owner="
                    + character.GetPlayerIdentityId()
                    + ": weapon position update skipped until the identity is known (SE-1583)"
            );
        return false;
    }
}
