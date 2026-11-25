using HarmonyLib;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using SpaceEngineers.Game.SessionComponents;

namespace ClusterPlugin;

/// <summary>
/// SE-1921: an encounter that registers on a node by a handover or an offline wake keeps the removal deadline it had
/// (<see cref="EncounterDirectorService.KeepDeadline"/>) instead of the fresh random timer vanilla gives it.
/// </summary>
[HarmonyPatch(typeof(MyGlobalEncountersGenerator), "RegisterEncounter")]
internal static class EncounterDeadlinePatch
{
    [HarmonyPostfix]
    private static void Postfix(
        MyGlobalEncountersGenerator __instance,
        MyGlobalEncounterComponent globalEncounterComponent
    ) => EncounterDirectorService.KeepDeadline(__instance, globalEncounterComponent.EncounterId);
}
