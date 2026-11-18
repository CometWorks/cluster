using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
/// <para>
/// SE-1596: an admin's weather change on a regular node (the Alt-F10 Weather tab, <c>/weather</c>, <c>/rweather</c>,
/// which all run on the admin's client and send the client's weather list up, or a plugin or mod calling the weather API
/// on the node) used to be overwritten by the World Authority's next state. The node now sends the planets whose
/// weathers changed to the World Authority, which takes them over and publishes at once, so every node shows the change
/// within a few seconds. Meanwhile the node holds the World Authority's older states back, so the change doesn't flicker.
/// </para>
/// </summary>
public static class WeatherAuthorityService
{
    private const string Subsystem = "weather";
    private const double PublishIntervalMilliseconds = 2000;

    private static readonly string Node =
        Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
    private static readonly object Sync = new object();
    private static string m_pending;
    private static string m_lastPublished;
    private static double m_nextPublish;

    // Regular node: the World Authority's state last applied here, the base a local change is told apart from.
    private static List<PlanetWeatherState> m_applied;
    private static double m_holdUntil;

    // World Authority: planets a node changed, applied on the game thread.
    private static readonly ConcurrentQueue<List<PlanetWeatherState>> m_changes = new();
    private const int ForwardTimeoutMilliseconds = 5000;

    internal sealed record WeatherEffectState(
        string Weather,
        double PX,
        double PY,
        double PZ,
        double VX,
        double VY,
        double VZ,
        float Radius,
        int Life,
        int MaxLife,
        float Intensity,
        double SX,
        double SY,
        double SZ
    );

    internal sealed record PlanetWeatherState(
        long PlanetId,
        int NextWeather,
        List<WeatherEffectState> Weathers
    );

    public static void Install()
    {
        MySectorWeatherComponentSeams.ClusterWeatherRole = () =>
            !ClusterRuntime.IsClustered ? 0
            : ClusterRuntime.IsWorldAuthority ? 1
            : 2;
        MySectorWeatherComponentSeams.ClusterWeatherPlayerPositions = () =>
            !ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority
                ? null
                : PositionRegistryService
                    .Players.Where(player => player.ClientId != 0 && player.EntityId != 0)
                    .Select(player => new Vector3D(player.X, player.Y, player.Z))
                    .ToList();
        GlobalStateChannel.RegisterReplicaHandler(
            Subsystem,
            payload =>
            {
                lock (Sync)
                    m_pending = payload;
            }
        );
        GlobalStateChannel.RegisterCommandHandler(Subsystem, HandleChange);
        MySectorWeatherComponentSeams.ClusterWeatherMaybeChanged = MaybeChanged;
    }

    /// <summary>Runs on the game thread: the WA publishes its weather state, nodes apply the pushed one.</summary>
    public static void Update()
    {
        if (!ClusterRuntime.IsClustered)
            return;
        MySectorWeatherComponent component =
            MySession.Static?.GetComponent<MySectorWeatherComponent>();
        if (component == null)
            return;
        if (ClusterRuntime.IsWorldAuthority)
        {
            double now = ClusterRuntime.LocalMilliseconds;
            bool changed = false;
            while (m_changes.TryDequeue(out List<PlanetWeatherState> change))
                changed |= TakeOver(component.ClusterWeatherData, change);
            if (changed)
                m_nextPublish = 0;
            if (now < m_nextPublish)
                return;
            m_nextPublish = now + PublishIntervalMilliseconds;
            string state = Serialize(component.ClusterWeatherData);
            if (state == m_lastPublished)
                return;
            m_lastPublished = state;
            GlobalStateChannel.Publish(Subsystem, state);
            return;
        }
        string pending;
        lock (Sync)
        {
            // A change of this node's own is on its way to the World Authority: an older state would undo it here.
            if (ClusterRuntime.LocalMilliseconds < m_holdUntil)
                return;
            pending = m_pending;
            m_pending = null;
        }
        if (pending == null)
            return;
        try
        {
            m_applied = Parse(pending);
            component.ClusterApplyWeather(Deserialize(pending));
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                "[ClusterPlugin] weather apply failed error="
                    + SecretRedactor.Redact(exception.Message)
            );
        }
    }

    // -- SE-1596: a node's own weather change ------------------------------------------------------------------------

    /// <summary>
    /// Game thread, regular node: the weather list may have changed here (SyncIfDirty saw it dirty, or an admin's client
    /// sent its list up). The planets whose weathers differ from the World Authority's last state go to it.
    /// </summary>
    private static void MaybeChanged(MySectorWeatherComponent component)
    {
        if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || m_applied == null)
            return;
        List<PlanetWeatherState> changed = ChangedPlanets(
            m_applied,
            States(component.ClusterWeatherData)
        );
        if (changed.Count == 0)
            return;
        // The change is the new base: it is not sent again, and the World Authority's answer replaces it anyway.
        m_applied = m_applied
            .Where(planet => changed.All(change => change.PlanetId != planet.PlanetId))
            .Concat(changed)
            .ToList();
        string payload = JsonSerializer.Serialize(changed);
        lock (Sync)
            m_holdUntil = ClusterRuntime.LocalMilliseconds + ForwardTimeoutMilliseconds + 1000;
        Console.WriteLine(
            "[ClusterPlugin] weather change sent to the World Authority planets="
                + string.Join(",", changed.Select(planet => planet.PlanetId))
        );
        Task.Run(() =>
        {
            bool sent = false;
            try
            {
                GlobalStateChannel.SendCommand(Subsystem, payload, ForwardTimeoutMilliseconds);
                sent = true;
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "[ClusterPlugin] weather change not taken by the World Authority: "
                        + SecretRedactor.Redact(error.Message)
                );
            }
            // Taken: the World Authority publishes it at its next frame. Not taken: its state is the truth again.
            lock (Sync)
                m_holdUntil = sent ? ClusterRuntime.LocalMilliseconds + 1000 : 0;
        });
    }

    /// <summary>World Authority, channel thread: a node's changed planets, taken over on the game thread.</summary>
    private static string HandleChange(string origin, string payload)
    {
        List<PlanetWeatherState> change =
            JsonSerializer.Deserialize<List<PlanetWeatherState>>(payload)
            ?? throw new InvalidOperationException("Invalid weather change");
        m_changes.Enqueue(change);
        Console.WriteLine(
            "[ClusterPlugin] weather change from "
                + origin
                + " planets="
                + string.Join(",", change.Select(planet => planet.PlanetId))
        );
        return "{}";
    }

    /// <summary>
    /// The planets of <paramref name="local"/> whose set of weathers differs from <paramref name="baseline"/>'s. A weather
    /// is told by what an admin sets (name, start point, radius, length), not by what runs on by itself (life, position,
    /// intensity). Pure, for the weather self-test section.
    /// </summary>
    internal static List<PlanetWeatherState> ChangedPlanets(
        List<PlanetWeatherState> baseline,
        List<PlanetWeatherState> local
    ) =>
        local
            .Where(planet =>
            {
                PlanetWeatherState before = baseline.FirstOrDefault(entry =>
                    entry.PlanetId == planet.PlanetId
                );
                return !Keys(before?.Weathers).SetEquals(Keys(planet.Weathers));
            })
            .ToList();

    /// <summary>
    /// World Authority: each changed planet's weathers become the node's, keeping this side's running copy of a weather
    /// both have (it is fresher). The planet's schedule stays this side's. True when anything changed.
    /// </summary>
    private static bool TakeOver(
        List<MyObjectBuilder_WeatherPlanetData> data,
        List<PlanetWeatherState> change
    )
    {
        bool changed = false;
        foreach (PlanetWeatherState planet in change)
        {
            MyObjectBuilder_WeatherPlanetData target = data.FirstOrDefault(entry =>
                entry != null && entry.PlanetId == planet.PlanetId
            );
            if (target == null)
                continue;
            List<MyObjectBuilder_WeatherEffect> current =
                target.Weathers ?? new List<MyObjectBuilder_WeatherEffect>();
            target.Weathers = TakeOver(current, ToEffects(planet.Weathers));
            changed = true;
        }
        return changed;
    }

    /// <summary>The node's weathers, each replaced by this side's running copy when there is one. Pure, for the self-test.</summary>
    internal static List<MyObjectBuilder_WeatherEffect> TakeOver(
        List<MyObjectBuilder_WeatherEffect> current,
        List<MyObjectBuilder_WeatherEffect> wanted
    ) =>
        wanted
            .Select(effect =>
                current.FirstOrDefault(running => Key(running) == Key(effect)) ?? effect
            )
            .ToList();

    private static HashSet<(string, double, double, double, float, int)> Keys(
        IEnumerable<WeatherEffectState> weathers
    ) =>
        (weathers ?? Enumerable.Empty<WeatherEffectState>())
            .Select(effect =>
                (effect.Weather, effect.SX, effect.SY, effect.SZ, effect.Radius, effect.MaxLife)
            )
            .ToHashSet();

    private static (string, double, double, double, float, int) Key(
        MyObjectBuilder_WeatherEffect effect
    ) =>
        (
            effect.Weather,
            effect.StartPoint.X,
            effect.StartPoint.Y,
            effect.StartPoint.Z,
            effect.Radius,
            effect.MaxLife
        );

    private static List<PlanetWeatherState> Parse(string payload) =>
        JsonSerializer.Deserialize<List<PlanetWeatherState>>(payload)
        ?? new List<PlanetWeatherState>();

    private static string Serialize(List<MyObjectBuilder_WeatherPlanetData> data) =>
        JsonSerializer.Serialize(States(data));

    internal static List<PlanetWeatherState> States(List<MyObjectBuilder_WeatherPlanetData> data) =>
        (data ?? new List<MyObjectBuilder_WeatherPlanetData>())
            .Where(planet => planet != null)
            .Select(planet => new PlanetWeatherState(
                planet.PlanetId,
                planet.NextWeather,
                (planet.Weathers ?? new List<MyObjectBuilder_WeatherEffect>())
                    .Select(effect => new WeatherEffectState(
                        effect.Weather,
                        effect.Position.X,
                        effect.Position.Y,
                        effect.Position.Z,
                        effect.Velocity.X,
                        effect.Velocity.Y,
                        effect.Velocity.Z,
                        effect.Radius,
                        effect.Life,
                        effect.MaxLife,
                        effect.Intensity,
                        effect.StartPoint.X,
                        effect.StartPoint.Y,
                        effect.StartPoint.Z
                    ))
                    .ToList()
            ))
            .OrderBy(planet => planet.PlanetId)
            .ToList();

    private static List<MyObjectBuilder_WeatherPlanetData> Deserialize(string payload) =>
        Parse(payload)
            .Select(planet => new MyObjectBuilder_WeatherPlanetData
            {
                PlanetId = planet.PlanetId,
                NextWeather = planet.NextWeather,
                Weathers = ToEffects(planet.Weathers),
            })
            .ToList();

    private static List<MyObjectBuilder_WeatherEffect> ToEffects(
        IEnumerable<WeatherEffectState> weathers
    ) =>
        (weathers ?? Enumerable.Empty<WeatherEffectState>())
            .Select(effect => new MyObjectBuilder_WeatherEffect
            {
                Weather = effect.Weather,
                Position = new Vector3D(effect.PX, effect.PY, effect.PZ),
                Velocity = new Vector3D(effect.VX, effect.VY, effect.VZ),
                Radius = effect.Radius,
                Life = effect.Life,
                MaxLife = effect.MaxLife,
                Intensity = effect.Intensity,
                StartPoint = new Vector3D(effect.SX, effect.SY, effect.SZ),
            })
            .ToList();

    public static object Status()
    {
        MySectorWeatherComponent component =
            MySession.Static?.GetComponent<MySectorWeatherComponent>();
        return new
        {
            node = Node,
            authority = ClusterRuntime.IsWorldAuthority,
            lightnings = component?.ClusterLightningsSpawned ?? 0,
            planets = component == null
                ? Array.Empty<object>()
                : component
                    .ClusterWeatherData.Where(planet => planet != null)
                    .Select(planet =>
                        (object)
                            new
                            {
                                planetId = planet.PlanetId,
                                weathers = (
                                    planet.Weathers ?? new List<MyObjectBuilder_WeatherEffect>()
                                )
                                    .Select(effect => new
                                    {
                                        weather = effect.Weather,
                                        radius = effect.Radius,
                                        life = effect.Life,
                                        maxLife = effect.MaxLife,
                                    })
                                    .ToArray(),
                            }
                    )
                    .ToArray(),
            definitions = Sandbox
                .Definitions.MyDefinitionManager.Static?.GetWeatherDefinitions()
                .Where(definition => definition.Public)
                .Select(definition => definition.Id.SubtypeName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
                ?? Array.Empty<string>(),
        };
    }

    /// <summary>Sets a weather at the WA over the planet nearest the given (or first) planet center.</summary>
    public static object TestSet(string weather, int length)
    {
        if (!ClusterRuntime.IsWorldAuthority)
            throw new InvalidOperationException("Weather decisions belong to the World Authority");
        MySectorWeatherComponent component =
            MySession.Static?.GetComponent<MySectorWeatherComponent>()
            ?? throw new InvalidOperationException("Weather component is not loaded");
        MyPlanet planet =
            MySession
                .Static.VoxelMaps.Instances.OfType<MyPlanet>()
                .OrderBy(candidate => candidate.EntityId)
                .FirstOrDefault()
            ?? throw new InvalidOperationException("No planet is loaded");
        // Vanilla SetWeather stores the effect only into an EXISTING planet entry (they are
        // normally created by a MainCamera-gated update tick) - ensure it, or the set no-ops.
        if (
            !component.ClusterWeatherData.Any(entry =>
                entry != null && entry.PlanetId == planet.EntityId
            )
        )
            component.ClusterWeatherData.Add(
                new MyObjectBuilder_WeatherPlanetData { PlanetId = planet.EntityId }
            );
        bool applied = component.SetWeather(
            weather,
            planet.AtmosphereRadius,
            PlanetCenter(planet),
            null,
            Vector3.Zero,
            length
        );
        return new
        {
            node = Node,
            planetId = planet.EntityId,
            weather,
            applied,
        };
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
        MySectorWeatherComponent component =
            MySession.Static?.GetComponent<MySectorWeatherComponent>()
            ?? throw new InvalidOperationException("Weather component is not loaded");
        Vector3D position;
        if (x.HasValue && y.HasValue && z.HasValue)
            position = new Vector3D(x.Value, y.Value, z.Value);
        else
        {
            MyPlanet planet =
                MySession
                    .Static.VoxelMaps.Instances.OfType<MyPlanet>()
                    .OrderBy(candidate => candidate.EntityId)
                    .FirstOrDefault()
                ?? throw new InvalidOperationException("No planet is loaded");
            Vector3D probe = PlanetCenter(planet) + Vector3D.Up * planet.AtmosphereRadius;
            position = planet.GetClosestSurfacePointGlobal(ref probe) + Vector3D.Up * 30;
        }
        component.CreateLightning(position, null, doDamage: false);
        return new { node = Node, lightnings = component.ClusterLightningsSpawned };
    }
}
