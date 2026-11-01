using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.Library.Utils;

namespace Shared.Patches;

/// <summary>
/// Slew regular-node replication time to the
/// generation-fenced World Authority game clock. Without the seam the vanilla SimulationTimeWithSpeed result
/// is returned unchanged.
/// </summary>
[HarmonyPatch(typeof(MyMultiplayerServerBase), nameof(MyMultiplayerServerBase.GetUpdateTime))]
public static class MyMultiplayerServerBasePatch
{
	// ClusterPlugin patch point: ReplicationClockPatch.Postfix
	[HarmonyPostfix]
	private static void Postfix(ref MyTimeSpan __result)
	{
		var seam = MyMultiplayerServerBaseSeams.ClusterAdjustUpdateTime;
		if (seam != null) __result = seam(__result);
	}
}
