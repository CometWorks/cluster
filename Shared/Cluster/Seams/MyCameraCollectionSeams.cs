using System.Collections.Generic;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyCameraCollection"/>.
/// No Harmony patch: the accessor reads the private map through the publicizer.
/// </summary>
public static class MyCameraCollectionSeams
{
	/// <summary>
	/// A player's camera settings have to travel between nodes with the player, and vanilla only ever reads
	/// them one entity at a time. Returns the live per-entity map (read-only view) or null.
	/// </summary>
	public static IReadOnlyDictionary<long, MyEntityCameraSettings> ClusterCameraSettings(this MyCameraCollection self, MyPlayer.PlayerId pid)
	{
		return self.m_entityCameraSettings.TryGetValue(pid, out Dictionary<long, MyEntityCameraSettings> settings) ? settings : null;
	}
}
