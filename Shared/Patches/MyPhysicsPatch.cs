using System.Diagnostics;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Physics;

namespace Shared.Patches;

/// <summary>
/// CLUSTER-SIM-COST (MyPhysics): times the StepWorlds() call inside MyPhysics.SimulateInternal
/// and accumulates the ticks into MyPhysics.StepWallTicks. StepWorlds is a private method called exactly once per physics step from
/// SimulateInternal, so a prefix/postfix pair on it is the same boundary. Per-cluster timing is impossible
/// in the parallel path (all worlds share one Havok job queue); PartitionManager distributes deltas of
/// this total over partition members by active-rigid-body weight.
///
/// Not gated: two Stopwatch reads per frame, no behavioural effect. A postfix does not run when StepWorlds throws, which only loses the ticks of a frame that has already
/// killed the physics step.
/// </summary>
[HarmonyPatch(typeof(MyPhysics), "StepWorlds")]
public static class MyPhysicsPatch
{
    [HarmonyPrefix]
    private static void StepWorldsPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    [HarmonyPostfix]
    private static void StepWorldsPostfix(long __state)
    {
        MyPhysicsSeams.StepWallTicks += Stopwatch.GetTimestamp() - __state;
    }
}
