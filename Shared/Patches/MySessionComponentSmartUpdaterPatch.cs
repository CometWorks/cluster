using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;

namespace Shared.Patches;

/// <summary>
/// Dormant-entity guard, smart updater half. A grid reaches
/// MySessionComponentSmartUpdater's list from MyTieredUpdateSystem.Register during block Init, i.e.
/// BEFORE MyEntities.Add. A staged handover import keeps such a grid dormant for a few frames (built
/// on a worker, activated in one frame); judging its presence there - it is not in the updater's tree
/// yet, so "nobody present" - put its thrusters' update timers to ChangeTimerTick(0)+Pause and failed
/// the handover round-trip gate (w22 2026-08-21 17:50:02, partition 5248). The natural place to skip the
/// grid is the updater's Iterate lambda, but that lambda is a compiler-generated closure, and its only
/// effect on the grid is the OnGridPresenceUpdate call (MyCubeGrid's sole caller), so the guard sits on
/// that stable boundary instead. Not in the scene = not yet anybody's business; the first pass after
/// Add judges it normally. Vanilla never has such entities for long (paste), so behaviour is unchanged
/// there; the guard is also skipped entirely on an unclustered process.
/// </summary>
[HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.OnGridPresenceUpdate))]
public static class MySessionComponentSmartUpdaterPatch
{
    [HarmonyPrefix]
    private static bool OnGridPresenceUpdatePrefix(MyCubeGrid __instance)
    {
        // T-0392: the same reason for a grid whose sliced import activation is still open - it is in the
        // scene but not yet validated, and judging its presence pauses its timers (TimerEnabled residual).
        return !ClusterRuntime.IsClustered || (__instance.InScene && !ImportActivationGuard.IsOpenFor(GuardUser.SmartUpdater, __instance.EntityId));
    }
}
