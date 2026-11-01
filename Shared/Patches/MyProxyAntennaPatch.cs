using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;

namespace Shared.Patches;

/// <summary>
/// Laser-proxy cleanup in <c>MyProxyAntenna.Closing</c>: Active=true put a
/// laser proxy into MyAntennaSystem.m_laserAntennas (AddLaser register:false), and only the
/// Active=false path ever took it out - a proxy closed via Entity.Close() leaked the entry, and the
/// real laser antenna arriving with a recovered partition then crashed the game loop on the
/// duplicate key. A prefix rather than a hook after UnregisterAntenna: that call
/// never reads m_laserAntennas, so the order is immaterial, and the broadcaster component is still
/// guaranteed present before base.Closing() runs. Guarded by <see cref="ClusterRuntime.IsClustered"/>.
/// </summary>
[HarmonyPatch(typeof(MyProxyAntenna), "Closing")]
public static class MyProxyAntennaPatch
{
	[HarmonyPrefix]
	private static bool ClosingPrefix(MyProxyAntenna __instance)
	{
		if (!ClusterRuntime.IsClustered)
		{
			return true;
		}
		MyAntennaSystem antennas = MyAntennaSystem.Static;
		// Remove the entry only if it is still ours: the real antenna may have replaced it.
		if (antennas != null && __instance.Broadcaster is MyLaserBroadcaster
			&& antennas.LaserAntennas.TryGetValue(__instance.AntennaEntityId, out MyLaserBroadcaster registered)
			&& ReferenceEquals(registered, __instance.Broadcaster))
		{
			antennas.RemoveLaser(__instance.AntennaEntityId, register: false);
		}
		return true;
	}
}
