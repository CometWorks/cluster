using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// WA-5: cluster weather authority. Vanilla runs an independent weather simulation on every server,
/// so nodes would show different storms at the same planet and the WA (which hosts no players) would
/// drive none at all. The split: the World Authority runs vanilla's per-planet weather DECISION
/// schedule - fed cluster-wide player positions from the position registry - and publishes the
/// resulting weather state; regular nodes apply that state read-only (vanilla then replicates it to
/// their own clients) and keep only the LIGHTNING ticks, whose vanilla target selection reads local
/// players and grids - which only the hosting node has - so strikes execute exactly once per target
/// without extra arbitration.
/// </summary>
public static class WeatherAuthorityService
{
	private const string Subsystem = "weather";
	private const double PublishIntervalMilliseconds = 2000;

	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
	private static readonly object Sync = new object();
	private static string m_pending;
	private static string m_lastPublished;
	private static double m_nextPublish;

	private sealed record WeatherEffectState(string Weather, double PX, double PY, double PZ,
		double VX, double VY, double VZ, float Radius, int Life, int MaxLife, float Intensity,
		double SX, double SY, double SZ);
	private sealed record PlanetWeatherState(long PlanetId, int NextWeather, List<WeatherEffectState> Weathers);

	public static void Install()
	{
		MySectorWeatherComponentSeams.ClusterWeatherRole = ()
			=> !ClusterRuntime.IsClustered ? 0 : ClusterRuntime.IsWorldAuthority ? 1 : 2;
		MySectorWeatherComponentSeams.ClusterWeatherPlayerPositions = ()
			=> !ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority ? null
				: PositionRegistryService.Players.Where(player => player.ClientId != 0 && player.EntityId != 0)
					.Select(player => new Vector3D(player.X, player.Y, player.Z)).ToList();
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, payload =>
		{
			lock (Sync) m_pending = payload;
		});
	}

	/// <summary>Runs on the game thread: the WA publishes its weather state, nodes apply the pushed one.</summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered) return;
		MySectorWeatherComponent component = MySession.Static?.GetComponent<MySectorWeatherComponent>();
		if (component == null) return;
		if (ClusterRuntime.IsWorldAuthority)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			if (now < m_nextPublish) return;
			m_nextPublish = now + PublishIntervalMilliseconds;
			string state = Serialize(component.ClusterWeatherData);
			if (state == m_lastPublished) return;
			m_lastPublished = state;
			GlobalStateChannel.Publish(Subsystem, state);
			return;
		}
		string pending;
		lock (Sync)
		{
			pending = m_pending;
			m_pending = null;
		}
		if (pending == null) return;
		try { component.ClusterApplyWeather(Deserialize(pending)); }
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] weather apply failed error=" + SecretRedactor.Redact(exception.Message));
		}
	}

	private static string Serialize(List<MyObjectBuilder_WeatherPlanetData> data)
		=> JsonSerializer.Serialize((data ?? new List<MyObjectBuilder_WeatherPlanetData>())
			.Where(planet => planet != null)
			.Select(planet => new PlanetWeatherState(planet.PlanetId, planet.NextWeather,
				(planet.Weathers ?? new List<MyObjectBuilder_WeatherEffect>()).Select(effect => new WeatherEffectState(
					effect.Weather, effect.Position.X, effect.Position.Y, effect.Position.Z,
					effect.Velocity.X, effect.Velocity.Y, effect.Velocity.Z,
					effect.Radius, effect.Life, effect.MaxLife, effect.Intensity,
					effect.StartPoint.X, effect.StartPoint.Y, effect.StartPoint.Z)).ToList()))
			.OrderBy(planet => planet.PlanetId).ToList());

	private static List<MyObjectBuilder_WeatherPlanetData> Deserialize(string payload)
		=> (JsonSerializer.Deserialize<List<PlanetWeatherState>>(payload) ?? new List<PlanetWeatherState>())
			.Select(planet => new MyObjectBuilder_WeatherPlanetData
			{
				PlanetId = planet.PlanetId,
				NextWeather = planet.NextWeather,
				Weathers = planet.Weathers.Select(effect => new MyObjectBuilder_WeatherEffect
				{
					Weather = effect.Weather,
					Position = new Vector3D(effect.PX, effect.PY, effect.PZ),
					Velocity = new Vector3D(effect.VX, effect.VY, effect.VZ),
					Radius = effect.Radius,
					Life = effect.Life,
					MaxLife = effect.MaxLife,
					Intensity = effect.Intensity,
					StartPoint = new Vector3D(effect.SX, effect.SY, effect.SZ)
				}).ToList()
			}).ToList();

	public static object Status()
	{
		MySectorWeatherComponent component = MySession.Static?.GetComponent<MySectorWeatherComponent>();
		return new
		{
			node = Node,
			authority = ClusterRuntime.IsWorldAuthority,
			lightnings = component?.ClusterLightningsSpawned ?? 0,
			planets = component == null
				? Array.Empty<object>()
				: component.ClusterWeatherData.Where(planet => planet != null)
					.Select(planet => (object)new
					{
						planetId = planet.PlanetId,
						weathers = (planet.Weathers ?? new List<MyObjectBuilder_WeatherEffect>())
							.Select(effect => new { weather = effect.Weather, radius = effect.Radius, life = effect.Life, maxLife = effect.MaxLife })
							.ToArray()
					}).ToArray(),
			definitions = Sandbox.Definitions.MyDefinitionManager.Static?.GetWeatherDefinitions()
				.Where(definition => definition.Public).Select(definition => definition.Id.SubtypeName)
				.OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>()
		};
	}

	/// <summary>Sets a weather at the WA over the planet nearest the given (or first) planet center.</summary>
	public static object TestSet(string weather, int length)
	{
		if (!ClusterRuntime.IsWorldAuthority)
			throw new InvalidOperationException("Weather decisions belong to the World Authority");
		MySectorWeatherComponent component = MySession.Static?.GetComponent<MySectorWeatherComponent>()
			?? throw new InvalidOperationException("Weather component is not loaded");
		MyPlanet planet = MySession.Static.VoxelMaps.Instances.OfType<MyPlanet>().OrderBy(candidate => candidate.EntityId).FirstOrDefault()
			?? throw new InvalidOperationException("No planet is loaded");
		// Vanilla SetWeather stores the effect only into an EXISTING planet entry (they are
		// normally created by a MainCamera-gated update tick) - ensure it, or the set no-ops.
		if (!component.ClusterWeatherData.Any(entry => entry != null && entry.PlanetId == planet.EntityId))
			component.ClusterWeatherData.Add(new MyObjectBuilder_WeatherPlanetData { PlanetId = planet.EntityId });
		bool applied = component.SetWeather(weather, planet.AtmosphereRadius,
			PlanetCenter(planet), null, Vector3.Zero, length);
		return new { node = Node, planetId = planet.EntityId, weather, applied };
	}

	// MatrixD is not a readonly struct, so WorldMatrixRef.Translation would still defensively
	// copy the whole matrix - read the translation fields through the reference instead.
	private static Vector3D PlanetCenter(MyPlanet planet)
	{
		ref readonly MatrixD world = ref planet.PositionComp.WorldMatrixRef;
		return new Vector3D(world.M41, world.M42, world.M43);
	}

	/// <summary>
	/// Spawns a damage-free lightning bolt on THIS node (lane evidence). Without coordinates it
	/// strikes just above the first planet's surface at the world-up pole - always above ground.
	/// </summary>
	public static object TestLightning(double? x, double? y, double? z)
	{
		MySectorWeatherComponent component = MySession.Static?.GetComponent<MySectorWeatherComponent>()
			?? throw new InvalidOperationException("Weather component is not loaded");
		Vector3D position;
		if (x.HasValue && y.HasValue && z.HasValue) position = new Vector3D(x.Value, y.Value, z.Value);
		else
		{
			MyPlanet planet = MySession.Static.VoxelMaps.Instances.OfType<MyPlanet>().OrderBy(candidate => candidate.EntityId).FirstOrDefault()
				?? throw new InvalidOperationException("No planet is loaded");
			Vector3D probe = PlanetCenter(planet) + Vector3D.Up * planet.AtmosphereRadius;
			position = planet.GetClosestSurfacePointGlobal(ref probe) + Vector3D.Up * 30;
		}
		component.CreateLightning(position, null, doDamage: false);
		return new { node = Node, lightnings = component.ClusterLightningsSpawned };
	}
}
