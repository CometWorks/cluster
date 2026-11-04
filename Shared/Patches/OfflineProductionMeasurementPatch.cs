using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Shared.Tools;

namespace Shared.Patches;

// Observe an ordinary engine timer tick; never manufacture simulation frames for measurement.
[HarmonyPatch(typeof(MyRefinery), "UpdateProduction", typeof(uint))]
internal static class OfflineRefineryMeasurementPatch
{
	[HarmonyPrefix]
	[EnsureCode("d24b196b")]
	private static void Prefix(MyRefinery __instance, uint framesFromLastTrigger, out object __state)
		=> __state = OfflineCompensation.BeginMeasurement(__instance, framesFromLastTrigger);
	[HarmonyFinalizer]
	private static void Finalizer(object __state, Exception __exception)
		=> OfflineCompensation.FinishMeasurement(__state, __exception == null);
}

[HarmonyPatch(typeof(MyAssembler), "UpdateProduction", typeof(uint), typeof(bool))]
internal static class OfflineAssemblerMeasurementPatch
{
	[HarmonyPrefix]
	[EnsureCode("a90270cc")]
	private static void Prefix(MyAssembler __instance, uint framesFromLastTrigger, bool forceUpdate, out object __state)
		=> __state = forceUpdate ? null : OfflineCompensation.BeginMeasurement(__instance, framesFromLastTrigger);
	[HarmonyFinalizer]
	private static void Finalizer(object __state, Exception __exception)
		=> OfflineCompensation.FinishMeasurement(__state, __exception == null);
}
