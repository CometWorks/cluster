using System;
using System.Collections.Generic;
using System.Linq;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Network;
using VRage.Utils;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// MyCubeGrid diagnostics: the build and raze requests a client raises
/// on a grid are silent when they go missing on a cluster node - the caller just leaves the block out of the
/// result set and the client only learns "placement unable". This logs every request at entry and every
/// refusal with its reason. Entry lines are prefixes; the refusal reasons inside
/// BuildBlocksRequest are the same three gates evaluated read-only in the prefix (the game evaluates them
/// again right after, unchanged), and CanPlaceWithConnectivity's two reasons are split in a postfix that
/// re-runs CanPlaceBlock on the refused path only. Every line is gated on cluster mode; a standalone server
/// logs nothing.
/// </summary>
[HarmonyPatch(typeof(MyCubeGrid))]
public static class MyCubeGridPatch
{
    [HarmonyPrefix]
    [HarmonyPatch("BuildBlocksRequest")]
    private static void BuildBlocksRequestPrefix(MyCubeGrid __instance, HashSet<MyCubeGrid.MyBlockLocation> locations, long builderEntityId, long ownerId)
    {
        if (!ClusterRuntime.IsClustered)
            return;
        try
        {
            ulong source = MyEventContext.Current.Sender.Value;
            bool local = MyEventContext.Current.IsLocallyInvoked;
            // ClusterPlugin diagnostic: pairs with the build_refused records below, so a request that never
            // arrives can be told apart from one the server received and then dropped.
            MyLog.Default?.WriteLine($"build_request grid={__instance.EntityId} locations={locations.Count} "
                + $"min={locations.FirstOrDefault().Min} builder={builderEntityId} owner={ownerId} "
                + $"source={source} local={local}");
            if (!__instance.Editable)
            {
                MyLog.Default?.WriteLine($"build_refused reason=not-editable grid={__instance.EntityId} source={source}");
                return;
            }
            MyEntities.TryGetEntityById(builderEntityId, out MyEntity entity);
            MyCubeBuilder.BuildComponent.GetBlocksPlacementMaterials(locations, __instance);
            bool creativeTools = MySession.Static.CreativeToolsEnabled(source) || MySession.Static.CreativeMode;
            bool creativeRights = local || MySession.Static.HasPlayerCreativeRights(source);
            if ((entity == null && !creativeRights && !MySession.Static.CreativeMode)
                || (!MyCubeBuilder.BuildComponent.HasBuildingMaterials(entity) && !creativeRights))
            {
                MyLog.Default?.WriteLine($"build_refused reason=no-materials grid={__instance.EntityId} builder={builderEntityId} "
                    + $"resolved={entity != null} creativeRights={creativeRights} source={source}");
                return;
            }
            MyCubeBlockDefinition definition = MyDefinitionManager.Static.GetCubeBlockDefinition(locations.First().BlockDefinition);
            string limitType = definition.GetLimitType();
            if (!__instance.IsWithinWorldLimits(ownerId, locations.Count, creativeTools ? locations.Count * definition.PCU : locations.Count, limitType))
            {
                MyLog.Default?.WriteLine($"build_refused reason=world-limits grid={__instance.EntityId} owner={ownerId} "
                    + $"type={limitType} source={source}");
            }
        }
        catch (Exception e)
        {
            // Diagnostics must never be able to take down the game thread.
            MyLog.Default?.WriteLine($"build_request trace failed grid={__instance.EntityId}: {e.GetType().Name}: {e.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch("CanPlaceWithConnectivity")]
    private static void CanPlaceWithConnectivityPostfix(MyCubeGrid __instance, bool __result, MyCubeGrid.MyBlockLocation location, ref Vector3I center, MyCubeBlockDefinition blockDefinition, ulong placingPlayer)
    {
        if (__result || !ClusterRuntime.IsClustered)
            return;
        try
        {
            // ClusterPlugin diagnostic: this is the last silent gate of a build request. Re-running the
            // placement test on the refused path only tells the two reasons apart.
            if (!__instance.CanPlaceBlock(location.Min, location.Max, location.Orientation, blockDefinition, placingPlayer))
            {
                MyLog.Default?.WriteLine($"build_refused reason=placement grid={__instance.EntityId} min={location.Min} "
                    + $"def={blockDefinition.Id} addCubes={__instance.CanAddCubes(location.Min, location.Max)} player={placingPlayer}");
                return;
            }
            MyLog.Default?.WriteLine($"build_refused reason=connectivity grid={__instance.EntityId} min={location.Min} "
                + $"def={blockDefinition.Id} center={center}");
        }
        catch (Exception)
        {
            // Diagnostics must never be able to take down the game thread.
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyCubeGrid.RazeBlocksRequest))]
    private static void RazeBlocksRequestPrefix(MyCubeGrid __instance, List<Vector3I> locations, long builderEntityId)
    {
        if (!ClusterRuntime.IsClustered)
            return;
        // ClusterPlugin diagnostic: same family as build_request - grinding a block off a grid is the
        // other instance event a client raises on a grid, and it is just as silent when it goes missing.
        MyLog.Default?.WriteLine($"raze_request grid={__instance.EntityId} locations={locations.Count} "
            + $"builder={builderEntityId} source={MyEventContext.Current.Sender.Value} "
            + $"local={MyEventContext.Current.IsLocallyInvoked}");
    }
}
