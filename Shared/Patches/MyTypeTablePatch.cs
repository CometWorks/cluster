using System;
using ClusterPlugin;
using HarmonyLib;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// Register concrete event-proxy types
/// first encountered after a client handover instead of throwing KeyNotFoundException from the
/// vanilla dictionary lookup. The body is <see cref="ReplicationTypeTablePatch.Prefix"/>,
/// which continues the original lookup while <see cref="ReplicationTypeTablePatch.Enabled"/> is false.
/// </summary>
[HarmonyPatch(typeof(MyTypeTable), nameof(MyTypeTable.Get), typeof(Type))]
public static class MyTypeTablePatch
{
	[HarmonyPrefix]
	private static bool GetPrefix(MyTypeTable __instance, Type type, ref MySynchronizedTypeInfo __result)
	{
		return ReplicationTypeTablePatch.Prefix(__instance, type, ref __result);
	}
}
