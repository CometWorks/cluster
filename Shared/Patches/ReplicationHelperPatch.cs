using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// Diagnostic on <see cref="ReplicationHelper.CheckMedicalRoom"/>: one
/// <c>medical_room_rights_drop reason=...</c> line for whichever of the method's four
/// <c>return false</c> sites refused. Postfix: when the vanilla method said no, the same checks are re-derived once
/// in the same order to name the culprit (the cheap ones inside CanPlayerSpawn are restated so
/// access+relation fine with the player present means FindFreePlace failed). Only the failure path pays
/// for the second evaluation. No seam delegate; gated on <see cref="ClusterRuntime.IsClustered"/>.
/// </summary>
[HarmonyPatch(typeof(ReplicationHelper), nameof(ReplicationHelper.CheckMedicalRoom))]
public static class ReplicationHelperPatch
{
    [HarmonyPostfix]
    private static void CheckMedicalRoomPostfix(long medicalRoom, MyPlayer player, MyCubeGridReplicable grid, bool __result)
    {
        if (__result || !ClusterRuntime.IsClustered)
            return;
        var log = MyLog.Default;
        if (log == null)
            return;
        if (!(MyEntities.GetEntityById(medicalRoom) is IMySpawnBlock spawnBlock))
        {
            log.WriteLine($"medical_room_rights_drop reason=no-spawn-block block={medicalRoom} grid={grid.EntityId}");
            return;
        }
        if (!spawnBlock.Components.TryGet<IMyRespawnComponent>(out var component))
        {
            log.WriteLine($"medical_room_rights_drop reason=no-respawn-component block={medicalRoom} grid={grid.EntityId} identity={player.Identity.IdentityId}");
            return;
        }
        if (!component.CanPlayerSpawn(player.Identity.IdentityId, acceptPublicRespawn: true))
        {
            MyTerminalBlock spawnTerminal = spawnBlock as MyTerminalBlock;
            bool access = spawnTerminal?.HasPlayerAccess(player.Identity.IdentityId) ?? false;
            MyRelationsBetweenPlayerAndBlock relation = spawnTerminal == null
                ? MyRelationsBetweenPlayerAndBlock.NoOwnership
                : MyIDModule.GetRelationPlayerBlock(spawnTerminal.IDModule.Owner, player.Identity.IdentityId, spawnTerminal.IDModule.ShareMode);
            bool playerFound = MySession.Static.Players.TryGetPlayer(player.Identity.IdentityId) != null;
            log.WriteLine($"medical_room_rights_drop reason=cannot-spawn block={medicalRoom} grid={grid.EntityId} identity={player.Identity.IdentityId} access={access} relation={relation} playerFound={playerFound}");
            return;
        }
        if (spawnBlock.CubeGrid.EntityId != grid.EntityId)
        {
            bool found = false;
            (spawnBlock.CubeGrid as MyCubeGrid)?.GetConnectedGrids(GridLinkTypeEnum.Logical, x => { found |= x.EntityId == grid.EntityId; });
            if (!found)
                log.WriteLine($"medical_room_rights_drop reason=grid-mismatch block={medicalRoom} blockGrid={spawnBlock.CubeGrid.EntityId} grid={grid.EntityId}");
        }
    }
}
