using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;

namespace Shared.Patches;

/// <summary>
/// MyShipMergeBlockFrozenPhantomGuard. GetOtherMergeBlock runs from
/// phantom_Leave, a Havok phantom callback, and reads THIS block's own rigid body two lines in. A
/// partition freeze disables member physics, which closes that body and makes every body in the merge
/// field leave the phantom at once - so the callback fires precisely when the thing it is about to
/// dereference has gone. Nothing catches it there: the callback runs off the game thread, so the null
/// reference is an unhandled exception and the node dies outright (SIGABRT). The vanilla
/// <c>other == null</c> test already concedes this is called with stale arguments; its own physics
/// deserves the same courtesy. Third of a family with the MyPhysicsBody and MyAirtightDoorGeneric
/// guards: Freeze closes rigid bodies and vanilla physics paths assume their own body still exists.
/// </summary>
[HarmonyPatch(typeof(MyShipMergeBlock), "GetOtherMergeBlock", new[] { typeof(MyCubeGrid) })]
public static class MyShipMergeBlockPatch
{
    [HarmonyPrefix]
    private static bool GetOtherMergeBlockPrefix(MyShipMergeBlock __instance, ref MyShipMergeBlock __result)
    {
        if (!ClusterRuntime.IsClustered || __instance.Physics?.RigidBody != null)
            return true;
        __result = null;
        return false;
    }
}
