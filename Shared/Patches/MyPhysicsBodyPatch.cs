using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Physics;

namespace Shared.Patches;

/// <summary>
/// MyPhysicsBodyClosedRigidBodyGuard. MyPhysics.IterateBodies caches
/// the active rigid body list across frames and resolves each entry back to its MyPhysicsBody, so a
/// body whose rigid body was closed after the cache was built is still dispatched here.
/// ClusterControlServer.Freeze sets <c>Physics.Enabled = false</c> on every partition member inside a
/// game-thread Invoke - i.e. between the cache being built and consumed - and the null dereference
/// took the node's game thread down with it ("Shutting down server...", every control request then
/// timing out). OnMotionDynamic's neighbouring <c>entity == null</c> test already concedes that
/// entries go stale; OnMotionKinematic dereferenced with no check at all. A closed rigid body simply
/// has no motion to report.
/// </summary>
[HarmonyPatch(typeof(MyPhysicsBody))]
public static class MyPhysicsBodyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyPhysicsBody.OnMotionDynamic))]
    private static bool OnMotionDynamicPrefix(MyPhysicsBody __instance)
    {
        return !ClusterRuntime.IsClustered || __instance.RigidBody != null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyPhysicsBody.OnMotionKinematic))]
    private static bool OnMotionKinematicPrefix(MyPhysicsBody __instance)
    {
        return !ClusterRuntime.IsClustered || __instance.RigidBody != null;
    }
}
