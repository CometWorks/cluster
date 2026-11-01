using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// MyShipController trace: the seat-exit request is silent end to end;
/// this and the RemovePilot gate line (MyCockpitPatch) are the only way to see whether the control switch
/// actually ran on the server. The line belongs at the top of sync_ControlledEntity_Used, which is exactly
/// where a prefix runs.
/// </summary>
[HarmonyPatch(typeof(MyShipController), "sync_ControlledEntity_Used")]
public static class MyShipControllerPatch
{
    [HarmonyPrefix]
    private static void Prefix(MyShipController __instance)
    {
        if (!ClusterRuntime.IsClustered || !Sync.IsServer)
            return;
        MyLog.Default?.WriteLine($"cockpit_use_received entity={__instance.EntityId} pilot={(__instance as MyCockpit)?.Pilot?.EntityId ?? 0} controller={(__instance.ControllerInfo.Controller?.Player?.Id.SteamId ?? 0)}");
    }
}
