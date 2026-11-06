using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.ObjectBuilders.ComponentSystem;

namespace Shared.Patches;

/// <summary>
/// SE-1133: a maintenance panel (LargeCrate and the other blocks with MyMaintenancePanelComponent) applies its
/// saved Open state one frame late. Deserialize only keeps the builder in m_builderToInitFrom, OnAddedToContainer
/// sets the status to Closed, and only OnUpdateOnceBeforeNextFrame turns the builder's Open into Opening. So a block
/// serialized before that update reports the panel closed. Vanilla never saves in that frame; the cluster does: the
/// handover gate serializes the imported grid while the partition is still frozen, and every handover of a grid with
/// an open panel failed on "Open: true != false" (octahedron-test 2026-11-06, Alpha Station's LargeCrate, partition
/// 4728 HandoverBlocked, each attempt froze it for ~2 s). A capture of a freshly imported or recovered grid would
/// have closed the panel for good.
/// While the builder is still pending, serialize the state the next-frame update WILL set, as the emotion
/// controller patch does (T-0316). Once the update has run the field is null and vanilla's value stands.
/// </summary>
[HarmonyPatch(typeof(MyMaintenancePanelComponent), nameof(MyMaintenancePanelComponent.Serialize))]
public static class MyMaintenancePanelComponentPatch
{
	[HarmonyPostfix]
	private static void Postfix(MyMaintenancePanelComponent __instance, MyObjectBuilder_ComponentBase __result)
	{
		if (__instance.m_builderToInitFrom is { } pending && __result is MyObjectBuilder_MaintenancePanelComponent builder)
			builder.Open = pending.Open;
	}
}
