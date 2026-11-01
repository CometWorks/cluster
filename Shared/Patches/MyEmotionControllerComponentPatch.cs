using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// T-0316: vanilla MyEmotionControllerComponent.Serialize iterates m_selectedLCDsIds.Value, which the server sets
/// only in Ent_OnUpdateOnceBeforeNextFrame (from the deserialize builder). A grid serialized in the same frame it
/// was created, before that update ran, therefore throws a NullReferenceException. Vanilla never saves in that
/// frame; the cluster does: a recovery load queues its Final capture right after CreateFromObjectBuilderAndAdd.
/// r32 2026-09-30 20:27-20:30: partition 279043 ('Heavy Railgun Cruiser Stella Cepheus', an emotion controller
/// block) failed every recovery with this NRE, and with its owner's client routed to it the refused release fenced
/// the node, three node deaths in three minutes.
/// While Value is still null, serialize what the next-frame update WILL set: the deserialize builder's values, or
/// vanilla's defaults without one (the owning entity's id, no emotion).
/// </summary>
[HarmonyPatch(typeof(MyEmotionControllerComponent), nameof(MyEmotionControllerComponent.Serialize))]
public static class MyEmotionControllerComponentPatch
{
	private static long s_guarded;

	[HarmonyPrefix]
	private static bool Prefix(MyEmotionControllerComponent __instance, ref MyObjectBuilder_ComponentBase __result)
	{
		if (__instance.m_selectedLCDsIds?.Value != null) return true;
		if (MyComponentFactory.Static.CreateObjectBuilder(__instance) is not MyObjectBuilder_EmotionControllerComponent builder) return true;
		builder.RemoveExistingComponentOnNewInsert = __instance.RemoveExistingComponentOnNewInsert;
		MyObjectBuilder_EmotionControllerComponent loaded = __instance.m_deserializeBuilder;
		builder.SelectedLCDsIds = loaded?.SelectedLCDsIds != null
			? new List<long>(loaded.SelectedLCDsIds)
			: new List<long> { __instance.Entity?.EntityId ?? 0 };
		builder.SelectedEmotionSubtype = loaded != null ? loaded.SelectedEmotionSubtype : __instance.m_selectedEmotionSubtype?.Value ?? "";
		__result = builder;
		if (++s_guarded <= 5 || s_guarded % 100 == 0)
			MyLog.Default?.WriteLine($"emotion_controller_serialize_guard entity={__instance.Entity?.EntityId} fromBuilder={loaded != null} count={s_guarded} (T-0316)");
		return false;
	}
}
