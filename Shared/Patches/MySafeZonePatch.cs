using ClusterPlugin;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.ObjectBuilders;

namespace Shared.Patches;

/// <summary>
/// SE-1997: safe zone faction lists that outlive a rebuilt faction collection (<see cref="SafeZoneFactions"/>), and
/// the settings the players set on a block's zone (<see cref="BlockSafeZones"/>, its Settings part).
/// </summary>
public static class MySafeZonePatch
{
    [HarmonyPatch(typeof(MySafeZone), nameof(MySafeZone.InitInternal))]
    private static class InitInternal
    {
        [HarmonyPostfix]
        private static void Postfix(MySafeZone __instance, MyObjectBuilder_SafeZone ob) =>
            SafeZoneFactions.Initialized(__instance, ob);
    }

    [HarmonyPatch(typeof(MySafeZone), nameof(MySafeZone.GetObjectBuilder))]
    private static class GetObjectBuilder
    {
        [HarmonyPostfix]
        private static void Postfix(MySafeZone __instance, MyObjectBuilder_EntityBase __result) =>
            SafeZoneFactions.Saved(__instance, __result as MyObjectBuilder_SafeZone);
    }

    [HarmonyPatch(typeof(MyFactionCollection), "Add", typeof(MyFaction))]
    private static class FactionAdded
    {
        [HarmonyPostfix]
        private static void Postfix() => SafeZoneFactions.FactionAdded();
    }

    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.UpdateSafeZone_Implementation)
    )]
    private static class AdminUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(MyObjectBuilder_SafeZone ob, out string __state) =>
            __state = ob == null ? null : BlockSafeZones.Editing(ob.EntityId);

        [HarmonyPostfix]
        private static void Postfix(MyObjectBuilder_SafeZone ob, string __state) =>
            BlockSafeZones.Edited(ob?.EntityId ?? 0, __state);
    }

    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.UpdateSafeZonePlayer_Implementation)
    )]
    private static class FilterUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(MyObjectBuilder_SafeZone ob, out string __state) =>
            __state = ob == null ? null : BlockSafeZones.Editing(ob.EntityId);

        [HarmonyPostfix]
        private static void Postfix(MyObjectBuilder_SafeZone ob, string __state) =>
            BlockSafeZones.Edited(ob?.EntityId ?? 0, __state);
    }

    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.UpdateSafeZone_ImplementationPlayer)
    )]
    private static class PlayerUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(MyObjectBuilder_SafeZone ob, out string __state) =>
            __state = ob == null ? null : BlockSafeZones.Editing(ob.EntityId);

        [HarmonyPostfix]
        private static void Postfix(MyObjectBuilder_SafeZone ob, string __state) =>
            BlockSafeZones.Edited(ob?.EntityId ?? 0, __state);
    }

    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.UpdateSafeZoneRadius_ImplementationPlayer)
    )]
    private static class RadiusUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(long safezoneId, out string __state) =>
            __state = BlockSafeZones.Editing(safezoneId);

        [HarmonyPostfix]
        private static void Postfix(long safezoneId, string __state) =>
            BlockSafeZones.Edited(safezoneId, __state);
    }

    [HarmonyPatch(typeof(MySafeZoneComponent), nameof(MySafeZoneComponent.SafeZoneCreate_Server))]
    private static class Create
    {
        [HarmonyPostfix]
        private static void Postfix(MySafeZoneComponent __instance) =>
            BlockSafeZones.Created(__instance);
    }

    [HarmonyPatch(typeof(MySafeZoneComponent), nameof(MySafeZoneComponent.OnSafezoneCreateRemove))]
    private static class SwitchedByPlayer
    {
        [HarmonyPostfix]
        private static void Postfix(MySafeZoneComponent __instance) =>
            BlockSafeZones.Created(__instance);
    }
}
