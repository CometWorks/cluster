using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Platform;
using Sandbox.Game.Debugging;

namespace Shared.Patches;

/// <summary>
/// HitchRecorder (HandoverImprovements.md §3): every game-thread
/// frame over hitch.thresholdMs is recorded with the handover phase/transfer active at the time.
/// Postfix on RunSingleFrame reading the FrameTimeTicks it just computed. ThresholdMs 0 disables
/// recording; the whole hook is skipped on an unclustered process.
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.RunSingleFrame))]
public static class GamePatch
{
    [HarmonyPostfix]
    private static void RunSingleFramePostfix(Game __instance)
    {
        if (!ClusterRuntime.IsClustered)
            return;
        HitchRecorder.Frame(MyPerformanceCounter.TicksToMs(__instance.FrameTimeTicks));
    }
}
