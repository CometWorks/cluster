using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using VRage.Game;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.SessionComponents.MySectorWeatherComponent</c>. The role
/// split inside <c>UpdatePlanetDataServer</c> and the player-position source inside
/// <c>CreateRandomWeather(MyPlanet, Action&lt;string&gt;, int?)</c> are installed by
/// <c>Shared.Patches.MySectorWeatherComponentPatch</c> (transpilers); the lightning counter lives in a ConditionalWeakTable maintained by that patch's CreateLightning postfix.
/// </summary>
public static class MySectorWeatherComponentSeams
{
	// ClusterPlugin patch point: WeatherAuthorityService
	// Harmony migration: prefix guards on UpdatePlanetDataServer plus publicized m_weatherPlanetData.
	// The cluster splits vanilla's server weather work: the World Authority runs the per-planet
	// weather DECISION schedule (planets are loaded on every role; player positions come from the
	// cluster seam below because the WA hosts no players) and publishes the resulting state, while
	// regular nodes apply that state read-only and keep only the LIGHTNING ticks - vanilla lightning
	// targets local players and grids, which only the hosting node has, so strikes execute exactly
	// once without extra arbitration.
	public static Func<int> ClusterWeatherRole; // 0 vanilla, 1 decisions only (WA), 2 lightning only (node)

	public static Func<List<Vector3D>> ClusterWeatherPlayerPositions;

	private sealed class LightningCounter
	{
		public int Spawned;
	}

	private static readonly ConditionalWeakTable<MySectorWeatherComponent, LightningCounter> Counters = new();

	extension(MySectorWeatherComponent self)
	{
		public int ClusterLightningsSpawned => Counters.TryGetValue(self, out var counter) ? counter.Spawned : 0;

		public List<MyObjectBuilder_WeatherPlanetData> ClusterWeatherData => self.m_weatherPlanetData;

		public void ClusterApplyWeather(List<MyObjectBuilder_WeatherPlanetData> data)
		{
			self.m_weatherPlanetData = data ?? new List<MyObjectBuilder_WeatherPlanetData>();
			self.m_weatherDirty = true;
		}
	}

	/// <summary>The role the transpiled UpdatePlanetDataServer reads.</summary>
	public static int WeatherRole() => ClusterWeatherRole?.Invoke() ?? 0;

	/// <summary>
	/// The player positions the transpiled CreateRandomWeather iterates: the cluster's when the seam supplies
	/// them, otherwise the positions of this server's own online players - exactly vanilla's loop input.
	/// </summary>
	public static List<Vector3D> WeatherPlayerPositionsOrLocal()
		=> ClusterWeatherPlayerPositions?.Invoke()
		   ?? Sync.Players.GetOnlinePlayers().Where(player => player != null).Select(player => player.GetPosition()).ToList();

	/// <summary>Maintained by the CreateLightning postfix: one per lightning vanilla really added.</summary>
	public static void CountLightning(MySectorWeatherComponent self) => Counters.GetOrCreateValue(self).Spawned++;
}
