using System;
using System.Runtime.CompilerServices;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.GameSystems;
using VRage.Game;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// CLUSTER-AIRTIGHTNESS-FIX / -TRACE on <see cref="MyGridGasSystem"/> (findings/import-depressurises-the-grid-until-the-first-gas-pass.md,
/// findings/oxygen-rooms-two-loss-modes-in-the-capture-path.md). There is no seam delegate here, so every hook is gated on <see cref="ClusterRuntime.IsClustered"/> and a
/// standalone server keeps vanilla behaviour.
/// <list type="bullet">
/// <item>Init: a grid handed its saved rooms asks for the pass that applies them (the cluster's import path
/// does not reliably add blocks afterwards, the load path does).</item>
/// <item>Update: the pressurization request is kept (bounded, MaxPendingRetries) while the grid has no
/// physics body yet - an imported, not yet activated grid - instead of being consumed by a refused
/// ShouldPressurize(). Prefix/postfix pair: vanilla still clears the flag; the postfix restores it.</item>
/// <item>GetOxygenAmount: unapplied saved rooms are the freshest pressure and are returned as-is; a capture
/// landing inside a room rebuild waits the pool task out (the engine's own OnGridClosing idiom).</item>
/// <item>Trace: one <c>[Airtight]</c> line per imported grid when its first pass completes (consumed by
/// ~/ws/se/se1/cluster-regression/bin/airtight-*; keep the format).</item>
/// </list>
/// The added per-system state lives in a <see cref="ConditionalWeakTable{TKey,TValue}"/> cell.
/// </summary>
[HarmonyPatch(typeof(MyGridGasSystem))]
public static class MyGridGasSystemPatch
{
    /// <summary>
    /// Updates the pressurization request may wait for a physics body before it is dropped. Import to
    /// activation is a handful of frames; this is generous without being unbounded.
    /// </summary>
    private const int MaxPendingRetries = 600;

    private sealed class State
    {
        public int PendingRetries;

        // Trace: set when the saved rooms are STORED (GenerateAirtightData nulls m_savedRooms before the
        // trace is emitted, so the count is captured here), -1 once traced.
        public int SavedRoomsAtMs = -1;
        public int SavedRoomsCount;
        // How many saved rooms landed on a live room; applied < saved is pressure dropped with no copy.
        public int AppliedRoomsCount = -1;
        public int FirstUpdateAtMs = -1;
        public int PassStartedAtMs = -1;
        public bool Traced;
    }

    private static readonly ConditionalWeakTable<MyGridGasSystem, State> States = new();

    private static State Of(MyGridGasSystem system) => States.GetOrCreateValue(system);

    private static bool TryGet(MyGridGasSystem system, out State state) => States.TryGetValue(system, out state);

    // ---- Init: request the pass that applies the saved rooms ----

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGridGasSystem.Init), new[] { typeof(OxygenRoom[]) })]
    private static void InitPostfix(MyGridGasSystem __instance, OxygenRoom[] oxygenAmount)
    {
        if (!ClusterRuntime.IsClustered || oxygenAmount == null || oxygenAmount.Length == 0)
            return;
        // m_savedRooms is only ever consumed at the end of GenerateAirtightData, so a grid handed its rooms
        // here and never scheduled holds its pressure somewhere nothing reads.
        __instance.m_generatedDataPending = true;
        var state = Of(__instance);
        state.SavedRoomsCount = oxygenAmount.Length;
        state.AppliedRoomsCount = -1;
        state.SavedRoomsAtMs = __instance.GetTotalGamePlayTimeInMilliseconds();
        state.FirstUpdateAtMs = -1;
        state.PassStartedAtMs = -1;
        state.Traced = false;
        __instance.ScheduleUpdate();
    }

    // ---- Update: keep the request while the physics body is missing ----

    private sealed class UpdateSnapshot
    {
        public bool PhysicsNull;
    }

    // Vanilla:
    //   if (!BACKGROUND_OXYGEN || m_isProcessingData) return;
    //   if (m_generatedDataPending) { if (BACKGROUND_OXYGEN && ShouldPressurize()) StartGenerateAirtightData(); flag = true; m_generatedDataPending = false; }
    // ShouldPressurize() refuses first of all on CubeGrid.Physics == null, so "pending and no physics" is exactly
    // the case where vanilla consumes the request without starting a pass. The prefix records that case (null
    // __state otherwise: the early return, no request, or a physics body); the postfix restores the flag, bounded.
    // Vanilla's flag=true already re-schedules the update. Every other reason ShouldPressurize() says no is a
    // settled property of the grid and consumes the flag as before.
    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    private static void UpdatePrefix(MyGridGasSystem __instance, out UpdateSnapshot __state)
    {
        __state = null;
        if (!ClusterRuntime.IsClustered || !MyFakes.BACKGROUND_OXYGEN || __instance.m_isProcessingData || !__instance.m_generatedDataPending)
            return;
        if (TryGet(__instance, out var state) && state.SavedRoomsAtMs >= 0 && state.FirstUpdateAtMs < 0)
            state.FirstUpdateAtMs = __instance.GetTotalGamePlayTimeInMilliseconds();
        __state = new UpdateSnapshot { PhysicsNull = __instance.CubeGrid.Physics == null };
    }

    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void UpdatePostfix(MyGridGasSystem __instance, UpdateSnapshot __state)
    {
        if (__state == null)
            return;
        var state = Of(__instance);
        if (!__state.PhysicsNull)
        {
            state.PendingRetries = 0;
            return;
        }
        // Bounded on purpose. A dormant staged grid may never get a physics body, and an unbounded retry
        // would reschedule this system every tick for every such grid - latency is a standing goal here,
        // and a fix for a 6-second hitch must not buy it with a permanent update.
        if (++state.PendingRetries > MaxPendingRetries)
        {
            state.PendingRetries = 0;
            return;
        }
        __instance.m_generatedDataPending = true;
    }

    // ---- GetOxygenAmount: never drop the pressure from a capture ----

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridGasSystem.GetOxygenAmount))]
    private static bool GetOxygenAmountPrefix(MyGridGasSystem __instance, ref OxygenRoom[] __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        // Between an import and the first gas pass the pressure lives only in m_savedRooms and m_rooms is
        // still null; m_savedRooms is nulled the instant it is applied, so non-null means "not yet applied" -
        // the freshest pressure that exists.
        var saved = __instance.m_savedRooms;
        if (saved != null)
        {
            __result = saved;
            return false;
        }
        // GenerateAirtightData clears and refills m_rooms on a pool thread; a capture landing inside a
        // rebuild would read a half-built list. Wait it out; a capture is already a frozen moment.
        if (__instance.m_isProcessingData && !__instance.m_backgroundTask.IsComplete)
        {
            try
            {
                __instance.m_backgroundTask.WaitOrExecute();
            }
            catch (Exception ex)
            {
                MySandboxGame.Log.WriteLineAndConsole("MyGridGasSystem.GetOxygenAmount: " + ex.Message + ", " + ex.StackTrace);
            }
        }
        return true;
    }

    // ---- Trace ----

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridGasSystem.StartGenerateAirtightData))]
    private static void StartGenerateAirtightDataPrefix(MyGridGasSystem __instance)
    {
        if (!ClusterRuntime.IsClustered || !TryGet(__instance, out var state))
            return;
        if (state.SavedRoomsAtMs >= 0 && state.PassStartedAtMs < 0)
            state.PassStartedAtMs = __instance.GetTotalGamePlayTimeInMilliseconds();
    }

    // The matches could be counted inside the apply loop, but the loop's condition (position inside the
    // stored bounds, a cube-room link with a live room) reads only m_storedGridMin/Max and m_cubeRoom, which
    // the method does not touch after the loop, so the postfix recounts the same predicate on the array the
    // prefix captured before the method nulled it. Pool thread, like the original.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridGasSystem.GenerateAirtightData))]
    private static void GenerateAirtightDataPrefix(MyGridGasSystem __instance, out OxygenRoom[] __state)
    {
        __state = ClusterRuntime.IsClustered ? __instance.m_savedRooms : null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGridGasSystem.GenerateAirtightData))]
    private static void GenerateAirtightDataPostfix(MyGridGasSystem __instance, OxygenRoom[] __state)
    {
        if (__state == null || !TryGet(__instance, out var state))
            return;
        int applied = 0;
        var cube = __instance.m_cubeRoom;
        Vector3I min = __instance.m_storedGridMin;
        Vector3I max = __instance.m_storedGridMax;
        foreach (var room in __state)
        {
            if (Vector3I.Min(room.StartingPosition, min) != min || Vector3I.Max(room.StartingPosition, max) != max)
                continue;
            var link = cube?[room.StartingPosition.X, room.StartingPosition.Y, room.StartingPosition.Z];
            if (link != null && link.Room != null)
                applied++;
        }
        state.AppliedRoomsCount = applied;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGridGasSystem.OnBackgroundTaskFinished))]
    private static void OnBackgroundTaskFinishedPrefix(MyGridGasSystem __instance)
    {
        if (!ClusterRuntime.IsClustered || !TryGet(__instance, out var state))
            return;
        if (state.SavedRoomsAtMs < 0 || state.Traced)
            return;
        state.Traced = true;
        int now = __instance.GetTotalGamePlayTimeInMilliseconds();
        var grid = __instance.CubeGrid;
        var rooms = __instance.m_rooms;
        MySandboxGame.Log.WriteLineAndConsole(
            "[Airtight] grid=" + (grid != null ? grid.EntityId : 0L)
            + " name='" + (grid != null ? grid.DisplayName : "?") + "'"
            + " savedRooms=" + state.SavedRoomsCount
            + " appliedRooms=" + state.AppliedRoomsCount
            + " firstUpdateMs=" + (state.FirstUpdateAtMs < 0 ? -1 : state.FirstUpdateAtMs - state.SavedRoomsAtMs)
            + " passStartMs=" + (state.PassStartedAtMs < 0 ? -1 : state.PassStartedAtMs - state.SavedRoomsAtMs)
            + " passDoneMs=" + (now - state.SavedRoomsAtMs)
            + " rooms=" + (rooms != null ? rooms.Count : -1)
            + " physics=" + (grid != null && grid.Physics != null));
        state.SavedRoomsAtMs = -1;
    }
}
