using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Shared.Tools;

namespace Shared.Patches;

// Distinguish the engine's ordinary fuel settlement from opaque mod inventory edits.
[HarmonyPatch(typeof(MyReactor), "ConsumeFuel", typeof(float))]
internal static class OfflineReactorObservationPatch
{
	[HarmonyPrefix]
	[EnsureCode("49c86cae")]
	private static void Prefix(MyReactor __instance, float timeDeltaMilliseconds, out Action __state)
		=> __state = OfflineCompensation.ObserveReactorFuel(__instance, timeDeltaMilliseconds);
	[HarmonyFinalizer]
	private static void Finalizer(Action __state, Exception __exception)
	{
		if (__exception == null) __state?.Invoke();
	}
}
