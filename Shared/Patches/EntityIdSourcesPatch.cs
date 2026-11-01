using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// EntityIdAllocator, the sources AllocateId and the remap helper do not cover.
/// MyEntityIdentifierPatch routes every AllocateId and MyEntityIdRemapHelperPatch every remap through
/// the node's registry-issued range. A decompile of the game (every AllocateId, RemapObjectBuilder,
/// Remap(helper), NextLong and every [Server] event that takes a location or an object builder) leaves
/// these entity ids that do NOT come from the node's range:
///
///  1. Block placement. MyCubeBuilder allocates MyBlockLocation.EntityId on the CLIENT (vanilla random),
///     and the [Server] BuildBlocksRequest builds and broadcasts the blocks with those ids.
///  2. Loading a blueprint into a projector or console block (MySpaceProjector). The client remaps the
///     grids with its own allocator and sends them; the server's OnNewBlueprintSuccess keeps them as the
///     reserved ids welding uses, so every welded block carries a client id.
///  3. Multi-grid (mechanical) blueprints in a projector. MyProjectorBase.Remap, OnSpawnProjection,
///     UpdateOnceBeforeFrame and GetObjectBuilderCubeBlock(copy) call MyEntities.RemapObjectBuilder once
///     PER GRID, and it clears its helper on every call: a rotor's TopBlockId in one grid and the top
///     block's EntityId in the other get two different new ids, and the link is lost. Here one helper
///     covers all grids of the call, as paste (RemapObjectBuilderCollection) already does.
///  4. Creative floating-object spawn: the client's builder, id included, is added as is.
///  5. Planets spawned at run time (admin spawn menu, mod API SpawnPlanet, world-generator op) take a
///     MyRandom.NextLong id.
///
/// Already in range and unchanged: paste (TryPasteGrid, PasteBlocksToGridServer), prefabs, grid split
/// (CreateGridForSplit -> AllocateId), merge (MoveBlocksByObjectBuilders), projector welding (reserved
/// ids from the server-side remap, AllocateId when no builder matches), SpawnDynamicGrid (T-0245 pair).
/// Deliberately unchanged: deterministic ids (MyVoxelPhysics ConstructId, planet environment sectors
/// ConstructIdFromString) must be equal on every node. Everything is a no-op while the allocator is not
/// enabled (standalone server, codec tests).
/// </summary>
[HarmonyPatch(typeof(MyCubeGrid))]
public static class EntityIdBlockPlacementPatch
{
    /// <summary>Source 1: every client-chosen block id is replaced by one from the node's range before
    /// the server builds, so the BuildBlocksClient broadcast carries the server's ids to every client.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("BuildBlocksRequest")]
    private static void BuildBlocksRequestPrefix(ref HashSet<MyCubeGrid.MyBlockLocation> locations)
    {
        if (!EntityIdAllocator.Enabled || !Sync.IsServer || MyEventContext.Current.IsLocallyInvoked || locations == null)
            return;
        var fresh = new HashSet<MyCubeGrid.MyBlockLocation>();
        foreach (MyCubeGrid.MyBlockLocation location in locations)
        {
            MyCubeGrid.MyBlockLocation copy = location;
            if (copy.EntityId != 0)
                copy.EntityId = MyEntityIdentifier.AllocateId();
            fresh.Add(copy);
        }
        locations = fresh;
    }
}

[HarmonyPatch(typeof(MyProjectorBase))]
public static class EntityIdProjectorPatch
{
    /// <summary>All grids through one helper: old ids that appear in several grids (TopBlockId,
    /// RotorEntityId, SubGridId) map to the same new id.</summary>
    internal static MyEntityIdRemapHelper RemapConsistently(IEnumerable<MyObjectBuilder_CubeGrid> grids)
    {
        var helper = new MyEntityIdRemapHelper();
        foreach (MyObjectBuilder_CubeGrid grid in grids)
            grid?.Remap(helper);
        return helper;
    }

    /// <summary>Source 2: a blueprint a client loaded is remapped on the server before it becomes the
    /// projection, so the reserved ids welding uses come from the node's range.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnNewBlueprintSuccess")]
    private static void OnNewBlueprintSuccessPrefix(List<MyObjectBuilder_CubeGrid> projectedGrids)
    {
        if (!EntityIdAllocator.Enabled || !Sync.IsServer || MyEventContext.Current.IsLocallyInvoked || projectedGrids == null)
            return;
        RemapConsistently(projectedGrids);
    }

    /// <summary>Source 3, the welding projector's remap: vanilla Remap() with one helper for all grids.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("Remap")]
    private static bool RemapPrefix(MyProjectorBase __instance)
    {
        if (!EntityIdAllocator.Enabled)
            return true;
        List<MyObjectBuilder_CubeGrid> grids = __instance.m_originalGridBuilders;
        if (grids == null || grids.Count <= 0 || !Sync.IsServer)
            return false;
        MyBlueprintIdTracker.OnRemap(__instance, RemapConsistently(grids));
        __instance.SetNewBlueprint(grids);
        return false;
    }
}

/// <summary>Source 3, the per-grid loops: in each target MyEntities.RemapObjectBuilder becomes RemapInScope,
/// which shares one helper across all grids of the call (opened by the prefix, closed by the finalizer).</summary>
[HarmonyPatch]
public static class EntityIdProjectorRemapScopePatch
{
    /// <summary>A stack so a nested call cannot share a map; the helper is created on first use.</summary>
    [ThreadStatic] private static List<MyEntityIdRemapHelper> t_scopes;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MyProjectorBase), "OnSpawnProjection");
        yield return AccessTools.Method(typeof(MyProjectorBase), nameof(MyProjectorBase.UpdateOnceBeforeFrame));
        yield return AccessTools.Method(typeof(MyProjectorBase), nameof(MyProjectorBase.GetObjectBuilderCubeBlock));
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo vanilla = AccessTools.Method(typeof(MyEntities), nameof(MyEntities.RemapObjectBuilder));
        MethodInfo scoped = AccessTools.Method(typeof(EntityIdProjectorRemapScopePatch), nameof(RemapInScope));
        List<CodeInstruction> code = instructions.ToList();
        int replaced = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.Calls(vanilla))
            {
                instruction.operand = scoped;
                replaced++;
            }
        }
        if (replaced != 1)
            throw new InvalidOperationException($"MyProjectorBase.{original.Name}: expected 1 MyEntities.RemapObjectBuilder call, found {replaced}");
        return code;
    }

    [HarmonyPrefix]
    private static void Prefix()
    {
        (t_scopes ??= new List<MyEntityIdRemapHelper>()).Add(null);
    }

    [HarmonyFinalizer]
    private static void Finalizer()
    {
        if (t_scopes is { Count: > 0 } scopes)
            scopes.RemoveAt(scopes.Count - 1);
    }

    private static IMyRemapHelper RemapInScope(MyObjectBuilder_EntityBase objectBuilder)
    {
        if (!EntityIdAllocator.Enabled || t_scopes is not { Count: > 0 } scopes)
            return MyEntities.RemapObjectBuilder(objectBuilder);
        int top = scopes.Count - 1;
        MyEntityIdRemapHelper helper = scopes[top] ??= new MyEntityIdRemapHelper();
        objectBuilder.Remap(helper);
        return helper;
    }
}

[HarmonyPatch(typeof(MyFloatingObjects))]
public static class EntityIdFloatingObjectPatch
{
    /// <summary>Source 4: a creative spawn gets a node id instead of the one the client put in.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("RequestSpawnCreative_Implementation")]
    private static void RequestSpawnCreativePrefix(MyObjectBuilder_FloatingObject obj)
    {
        if (!EntityIdAllocator.Enabled || MyEventContext.Current.IsLocallyInvoked || obj == null || obj.EntityId == 0)
            return;
        obj.EntityId = MyEntityIdentifier.AllocateId();
    }
}

[HarmonyPatch]
public static class EntityIdPlanetPatch
{
    /// <summary>Source 5: the three server-side planet spawns that draw a random id.</summary>
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MyGuiScreenDebugSpawnMenu), "SpawnPlanet_Server");
        InterfaceMapping map = typeof(MyVoxelMaps).GetInterfaceMap(typeof(IMyVoxelMaps));
        int index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IMyVoxelMaps.SpawnPlanet));
        if (index < 0)
            throw new InvalidOperationException("MyVoxelMaps: IMyVoxelMaps.SpawnPlanet not found");
        yield return map.TargetMethods[index];
        yield return AccessTools.Method(typeof(MyWorldGenerator.OperationCreatePlanet), nameof(MyWorldGenerator.OperationCreatePlanet.Apply));
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo random = AccessTools.Method(typeof(MyRandom), nameof(MyRandom.NextLong));
        MethodInfo node = AccessTools.Method(typeof(EntityIdPlanetPatch), nameof(PlanetId));
        List<CodeInstruction> code = instructions.ToList();
        int replaced = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.Calls(random))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = node;
                replaced++;
            }
        }
        if (replaced != 1)
            throw new InvalidOperationException($"{original.DeclaringType?.Name}.{original.Name}: expected 1 MyRandom.NextLong call, found {replaced}");
        return code;
    }

    private static long PlanetId(MyRandom random)
        => EntityIdAllocator.Enabled ? MyEntityIdentifier.AllocateId() : random.NextLong();
}
