using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.GameSystems.CoordinateSystem;
using VRage.Game;

namespace Shared.Patches;

/// <summary>
/// CoordinateSystemIdPatch.
///
/// Every node loads the same base world, so every node's local coordinate-system counter starts at
/// the same value and issues the same first id. A client that hands over between two such nodes
/// receives a duplicate id and CRASHES - MyReceiveQueue.Process rethrows on clients. See
/// CoordinateSystemIdPatch for the full mechanism and the 2026-09-04 20:44:14 evidence (T-0046).
/// </summary>
[HarmonyPatch(typeof(MyCoordinateSystem))]
public static class MyCoordinateSystemPatch
{
    /// <summary>
    /// Reseed the id counter to this node's registry-issued range immediately after Init has loaded
    /// it from the world. A postfix rather than a prefix because Init assigns the counter from the
    /// object builder, so a prefix's value would be overwritten.
    ///
    /// Inert outside a cluster: CoordinateSystemIdPatch.IsClustered is null until ClusterRuntime
    /// installs it, and the vanilla per-process counter is correct for a standalone server.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyCoordinateSystem.Init), new[] { typeof(MyObjectBuilder_SessionComponent) })]
    private static void InitPostfix(MyCoordinateSystem __instance)
    {
        CoordinateSystemIdPatch.Reseed(__instance);
    }
}
