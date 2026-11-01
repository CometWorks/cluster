using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game.World;
using SpaceEngineers.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MySpaceRespawnComponent"/>. The static seam is raised by
/// Shared/Patches/MySpaceRespawnComponentPatch.cs from a postfix on <c>SpawnAtShip</c>; the cooldown
/// visit/apply helpers are extension methods over the private map.
/// </summary>
public static class MySpaceRespawnComponentSeams
{
	/// <summary>
	/// Cluster seam: raised on the node that executed a respawn-ship spawn, right after the vanilla
	/// cooldown reset. Vanilla serves OnSyncCooldownRequest from the local map only, so a client whose
	/// next join lands on another node would be handed an empty cooldown list without replication.
	/// </summary>
	public static Action<MyPlayer.PlayerId> ClusterCooldownChanged;

	public static void ClusterVisitCooldowns(this MySpaceRespawnComponent self, ulong steamId, Action<int, string, int> visitor)
	{
		int totalTimeInMilliseconds = MySandboxGame.TotalTimeInMilliseconds;
		foreach (KeyValuePair<MySpaceRespawnComponent.RespawnKey, int> globalRespawnTimesM in self.m_globalRespawnTimesMs)
		{
			if (globalRespawnTimesM.Key.ControllerId.SteamId == steamId)
			{
				visitor(globalRespawnTimesM.Key.ControllerId.SerialId, globalRespawnTimesM.Key.RespawnShipId,
					globalRespawnTimesM.Value - totalTimeInMilliseconds);
			}
		}
	}

	public static void ClusterApplyCooldown(this MySpaceRespawnComponent self, ulong steamId, int serialId, string respawnShipId, int remainingMs)
	{
		MySpaceRespawnComponent.RespawnKey key = new MySpaceRespawnComponent.RespawnKey
		{
			ControllerId = new MyPlayer.PlayerId(steamId, serialId),
			RespawnShipId = respawnShipId
		};
		if (remainingMs > 0)
		{
			self.m_globalRespawnTimesMs.Add(key, MySandboxGame.TotalTimeInMilliseconds + remainingMs, immediate: true);
			return;
		}
		self.m_globalRespawnTimesMs.Remove(key);
		self.m_globalRespawnTimesMs.ApplyRemovals();
	}
}
