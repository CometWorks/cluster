using HarmonyLib;
using Sandbox.Game.Entities.Cube;

namespace Shared.Patches;

/// <summary>
/// MyShipConnector teardown guard (for <c>CubeGrid_OnBodyPhysicsChanged</c>): a grid being deleted has no physics body to reconstrain, and the welded branch
/// otherwise NREs inside <c>RecreateConstraintInternal</c> at <c>HkConstraint</c> creation when a docked
/// pair is bulk-closed - the handover release of a platform with docked ships. On w25 that NRE escaped
/// <c>MyEntities.DeleteRememberedEntities</c> on the game thread and exited the node (node-1 05:56:20,
/// node-2 05:59:21). Guarding the callee rather than the call site covers that case and
/// the welding path, where a missing body would have thrown the same way.
/// </summary>
[HarmonyPatch(typeof(MyShipConnector), nameof(MyShipConnector.RecreateConstraintInternal))]
public static class MyShipConnectorPatch
{
    [HarmonyPrefix]
    private static bool RecreateConstraintInternalPrefix(MyShipConnector __instance)
    {
        var other = __instance.m_other;
        return __instance.CubeGrid?.Physics != null && other?.CubeGrid?.Physics != null;
    }
}
