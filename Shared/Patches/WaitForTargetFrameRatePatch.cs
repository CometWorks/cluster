using System;
using ClusterPlugin;
using HarmonyLib;
using VRage.Library.Utils;

namespace Shared.Patches;

/// <summary>
/// A registered node that has never owned
/// content runs the WHOLE main loop at dormant.hz (default 4) instead of 60 Hz. Throttle, never
/// pause - a stopped loop deadlocks the invoke queue and trips the gateway's stall watchdog.
/// ClusterRuntime.UpdateDormancy sets/clears WaitForTargetFrameRateSeams.ClusterTargetFrequency;
/// 0 (standalone servers, the World Authority) keeps the constructor's frequency.
/// </summary>
[HarmonyPatch(typeof(WaitForTargetFrameRate), nameof(WaitForTargetFrameRate.TickPerFrame), MethodType.Getter)]
public static class WaitForTargetFrameRatePatch
{
    [HarmonyPrefix]
    private static bool TickPerFramePrefix(ref long __result)
    {
        var frequency = WaitForTargetFrameRateSeams.ClusterTargetFrequency;
        if (frequency <= 0f || System.Threading.Volatile.Read(ref WaitForTargetFrameRateSeams.PendingControlCalls) > 0)
            return true;
        __result = (int)Math.Round((float)MyGameTimer.Frequency / frequency);
        return false;
    }
}
