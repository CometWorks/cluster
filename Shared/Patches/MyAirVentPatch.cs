using System;
using System.Runtime.CompilerServices;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.GameSystems;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// CLUSTER-AIRTIGHTNESS-FIX / -TRACE on <see cref="MyAirVent"/> (findings/the-depressurisation-was-the-client-not-the-world.md). A client renders its air-vent readout
/// from <c>m_blockRoomInfo</c> alone, the server writes that value only in UpdateAfterSimulation100, and
/// Sync suppresses a write that does not change the value. When the cluster hands a partition to another
/// node the target rebuilds the grid's rooms in ~167 ms while the freshly replicated block still holds the
/// default - not airtight, oxygen 0 - and every client renders "Not pressurized" on a full box until the
/// next tick. The grid never lost its air; only the readout did.
/// <list type="bullet">
/// <item>OnAddedToScene: subscribe the vent's publish to the gas system's OnProcessingDataComplete so the
/// readout is republished the moment the rooms exist, and publish at once if the room is already known
/// (an unconditional publish there would assert "not airtight" as fact on a grid re-added to a scene
/// whose block still holds a correct reading).</item>
/// <item>Closing: unsubscribe.</item>
/// <item>UpdateAfterSimulation100: the periodic publish is vanilla's own write; the prefix/postfix pair only
/// adds the <c>[AirVentPublish]</c> trace around it.</item>
/// </list>
/// There is no seam delegate; every hook is gated on <see cref="ClusterRuntime.IsClustered"/>.
/// The per-vent handler and trace counter live in a <see cref="ConditionalWeakTable{TKey,TValue}"/> cell.
/// </summary>
[HarmonyPatch(typeof(MyAirVent))]
public static class MyAirVentPatch
{
    private sealed class State
    {
        public Action Handler;
        // How many publishes this vent has made since it was added to the scene, so the first few after
        // an import can be logged without spamming a long-lived block.
        public int PublishTraceCount;
        // The value before vanilla's UpdateAfterSimulation100 write, for the postfix trace.
        public MyAirVentBlockRoomInfo Previous;
    }

    private static readonly ConditionalWeakTable<MyAirVent, State> States = new();

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyAirVent.OnAddedToScene))]
    private static void OnAddedToScenePostfix(MyAirVent __instance)
    {
        if (!ClusterRuntime.IsClustered || !Sync.IsServer)
            return;
        var gasSystem = __instance.CubeGrid?.GridSystems?.GasSystem;
        if (gasSystem == null)
            return;
        var state = States.GetOrCreateValue(__instance);
        state.PublishTraceCount = 0;
        state.Handler ??= () => PublishRoomInfo(__instance);
        // Every vent on the grid shares this hook (a large base has five), hence += on the delegate
        // property. Remove first: a vent re-added without a Closing in between must not publish twice.
        gasSystem.OnProcessingDataComplete -= state.Handler;
        gasSystem.OnProcessingDataComplete += state.Handler;
        PublishRoomInfoIfRoomKnown(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch("Closing")]
    private static void ClosingPrefix(MyAirVent __instance)
    {
        if (!ClusterRuntime.IsClustered || !Sync.IsServer)
            return;
        if (!States.TryGetValue(__instance, out var state))
            return;
        state.PublishTraceCount = 0;
        var gasSystem = __instance.CubeGrid?.GridSystems?.GasSystem;
        if (gasSystem != null && state.Handler != null)
            gasSystem.OnProcessingDataComplete -= state.Handler;
    }

    // Vanilla writes m_blockRoomInfo.Value inside `if (Sync.IsServer)`; the trace goes around that
    // same write.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyAirVent.UpdateAfterSimulation100))]
    private static void UpdateAfterSimulation100Prefix(MyAirVent __instance, out State __state)
    {
        __state = null;
        if (!ClusterRuntime.IsClustered || !Sync.IsServer)
            return;
        if (!States.TryGetValue(__instance, out var state) || state.PublishTraceCount >= 6)
            return;
        __state = state;
        state.Previous = __instance.m_blockRoomInfo.Value;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyAirVent.UpdateAfterSimulation100))]
    private static void UpdateAfterSimulation100Postfix(MyAirVent __instance, State __state)
    {
        if (__state == null)
            return;
        Trace(__instance, __state, __state.Previous);
    }

    /// <summary>
    /// Publish only if this vent actually knows its room yet. OnAddedToScene runs before MyGridGasSystem
    /// has rebuilt the grid's rooms, so an unconditional publish there asserts "not airtight, oxygen 0"
    /// while the truth is merely not computed yet. The periodic publish stays unconditional: a room that
    /// genuinely lost its seal must be able to report that.
    /// </summary>
    private static void PublishRoomInfoIfRoomKnown(MyAirVent vent)
    {
        MyOxygenBlock oxygenBlock = vent.GetOxygenBlock();
        if (oxygenBlock?.Room == null)
            return;
        PublishRoomInfo(vent);
    }

    private static void PublishRoomInfo(MyAirVent vent)
    {
        if (!Sync.IsServer)
            return;
        MyOxygenBlock oxygenBlock = vent.GetOxygenBlock();
        bool isRoomAirtight = oxygenBlock != null && oxygenBlock.Room != null && oxygenBlock.Room.IsAirtight;
        float roomEnvironmentOxygen = (oxygenBlock != null && oxygenBlock.Room != null) ? oxygenBlock.Room.EnvironmentOxygen : 0f;
        float oxygenLevel = oxygenBlock?.OxygenLevel(vent.CubeGrid.GridSize) ?? 0f;
        MyAirVentBlockRoomInfo previous = vent.m_blockRoomInfo.Value;
        vent.m_blockRoomInfo.Value = new MyAirVentBlockRoomInfo(isRoomAirtight, oxygenLevel, roomEnvironmentOxygen);
        if (States.TryGetValue(vent, out var state) && state.PublishTraceCount < 6)
            Trace(vent, state, previous);
    }

    // Sync.SetValue returns WITHOUT raising a change when the new value equals the old one, so a publish
    // that looks like it fired can send nothing at all. `sent` is whether a delta actually went out;
    // `now*` is what clients would now be told. Format consumed by ~/ws/se/se1/cluster-regression/bin/readout-lag.
    private static void Trace(MyAirVent vent, State state, MyAirVentBlockRoomInfo previous)
    {
        state.PublishTraceCount++;
        MyAirVentBlockRoomInfo current = vent.m_blockRoomInfo.Value;
        bool sent = previous.IsRoomAirtight != current.IsRoomAirtight
            || previous.OxygenLevel != current.OxygenLevel
            || previous.RoomEnvironmentOxygen != current.RoomEnvironmentOxygen;
        MyLog.Default.WriteLineAndConsole(
            "[AirVentPublish] grid=" + (vent.CubeGrid != null ? vent.CubeGrid.EntityId : 0L)
            + " block=" + vent.EntityId
            + " n=" + state.PublishTraceCount
            + " sent=" + sent
            + " wasAirtight=" + previous.IsRoomAirtight + " wasOxygen=" + previous.OxygenLevel
            + " nowAirtight=" + current.IsRoomAirtight + " nowOxygen=" + current.OxygenLevel);
    }
}
