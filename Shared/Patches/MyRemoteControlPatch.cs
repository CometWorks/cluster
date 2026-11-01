using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using VRage.Game.Entity.UseObject;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// Refuse remote control take-over of a grid
/// frozen for a partition transfer with the vanilla failure path (UseFailureCallback with
/// AccessDenied, locally for local invocations and raised to the sender for client RPCs) instead of
/// granting control of a captured copy or silently dropping the request. The decision and logging
/// live in ClusterPlugin.RemoteControlGatePatch behind the MyRemoteControlSeams.ClusterUseGate delegate.
/// Cameras need no equivalent gate: MyCameraBlock.RequestSetView is entirely client-side.
/// </summary>
[HarmonyPatch(typeof(MyRemoteControl), "RequestUseMessage")]
public static class MyRemoteControlPatch
{
    [HarmonyPrefix]
    private static bool RequestUseMessagePrefix(MyRemoteControl __instance, UseActionEnum useAction, long usedById)
    {
        var gate = MyRemoteControlSeams.ClusterUseGate;
        if (gate == null)
            return true;
        var denial = gate(__instance, useAction, usedById);
        if (!denial.HasValue)
            return true;
        if (MyEventContext.Current.IsLocallyInvoked)
            __instance.UseFailureCallback(useAction, usedById, denial.Value);
        else
            MyMultiplayer.RaiseEvent(__instance, (MyRemoteControl x) => x.UseFailureCallback, useAction, usedById, denial.Value, MyEventContext.Current.Sender);
        return false;
    }
}
