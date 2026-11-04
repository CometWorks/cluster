using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// Invokes <c>ClusterRegisterPostfix</c> / <c>ClusterUnregisterPostfix</c> after the complete bodies of
/// RegisterAntenna / UnregisterAntenna so <c>AntennaGraphService</c> can track real local radio and
/// laser broadcasters for the cluster antenna graph. Plain postfixes; the original always runs
/// unchanged and the postfix is a no-op while the seam is null.
///
/// AddLaserPrefix is a stale-entry guard in <c>AddLaser</c>: a leaked proxy
/// broadcaster or a duplicate incarnation under the same id used to throw out of the game loop and
/// take the node down. Guarded by <see cref="ClusterRuntime.IsClustered"/> so a standalone server
/// keeps vanilla's Dictionary.Add.
/// </summary>
[HarmonyPatch(typeof(MyAntennaSystem))]
public static class MyAntennaSystemPatch
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyAntennaSystem.RegisterAntenna), typeof(MyDataBroadcaster))]
	private static void RegisterAntennaPostfix(MyDataBroadcaster broadcaster)
	{
		MyAntennaSystemSeams.ClusterRegisterPostfix?.Invoke(broadcaster);
	}

	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyAntennaSystem.UnregisterAntenna), typeof(MyDataBroadcaster))]
	private static void UnregisterAntennaPostfix(MyDataBroadcaster broadcaster)
	{
		MyAntennaSystemSeams.ClusterUnregisterPostfix?.Invoke(broadcaster);
	}

	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyAntennaSystem.AddLaser))]
	private static bool AddLaserPrefix(MyAntennaSystem __instance, long id, MyLaserBroadcaster laser, bool register)
	{
		if (!ClusterRuntime.IsClustered || !__instance.m_laserAntennas.TryGetValue(id, out MyLaserBroadcaster existing))
		{
			return true;
		}
		if (register)
		{
			__instance.RegisterAntenna(laser);
		}
		// A stale entry under this id (a leaked proxy broadcaster, or a duplicate incarnation) must
		// not throw out of the game loop and take the node down; the newest broadcaster wins.
		if (!ReferenceEquals(existing, laser))
		{
			MyLog.Default?.WriteLineAndConsole("Laser antenna " + id + " re-registered; replacing the stale broadcaster");
			__instance.m_laserAntennas[id] = laser;
		}
		return false;
	}

	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyAntennaSystem.AddLaser))]
	private static void AddLaserPostfix(MyLaserBroadcaster laser)
	{
		if (ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && laser.RealAntenna != null)
			LaserCallUpService.TargetAvailable(laser.RealAntenna);
	}
}
