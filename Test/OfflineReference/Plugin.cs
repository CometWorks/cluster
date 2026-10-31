using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClusterPlugin;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Plugins;
using VRageMath;
using SpaceEngineers.Game.Entities.Blocks;

namespace OfflineReference;

/// <summary>Optional standalone observer. ClusterNode is only a dormant capture/model library:
/// no cluster plugin is registered or initialized, and predictions never touch live state.</summary>
public sealed class Plugin : IPlugin
{
    private sealed class Case
    {
        public MyCubeGrid Grid;
        public object Census;
        public OfflineProfile Initial;
    }
    private readonly List<Case> cases = new();
    private string root;
    private long spawned = -1, start, elapsedStart, gameplayStart, nextSample;
    private int target;
    private bool fast, finished;
    private readonly Stopwatch wall = new();
    private static readonly Type CensusType = typeof(OfflineCompensation).GetNestedType("Census", BindingFlags.NonPublic);
    private static readonly MethodInfo Capture = typeof(OfflineCompensation).GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo SampleSun = typeof(OfflineCompensation).GetMethod("SampleSun", BindingFlags.NonPublic | BindingFlags.Static);

    public void Init(object gameInstance)
    {
        root = Environment.GetEnvironmentVariable("OFFLINE_REFERENCE_ROOT") ?? throw new InvalidOperationException("Missing reference root");
        Directory.CreateDirectory(root);
        target = int.Parse(Environment.GetEnvironmentVariable("OFFLINE_REFERENCE_FRAMES") ?? "1296000");
        fast = Environment.GetEnvironmentVariable("OFFLINE_REFERENCE_FAST") == "1";
        if (target < 3600 || target > 2000000) throw new ArgumentOutOfRangeException(nameof(target));
    }

    public void Update()
    {
        if (finished || MySession.Static?.Ready != true) return;
        try
        {
            if (ClusterRuntime.IsClustered || ClusterNodePlugin.Plugin.Instance != null)
                throw new InvalidOperationException("Reference cannot run with an initialized cluster plugin");
            if (MySession.Static.CreativeMode || MySession.Static.SimplifiedSimulation)
                throw new InvalidOperationException("Reference requires actual Survival with ordinary resource simulation");
            long frame = MySession.Static.GameplayFrameCounter;
            if (spawned < 0)
            {
                MySession.Static.Settings.EnableSunRotation = false;
                MySession.Static.Settings.AutoSaveInMinutes = 0;
                foreach (int mode in new[] { 0, 1, 2 }) cases.Add(Spawn(mode, false));
                cases.Add(Spawn(2, true));
                spawned = frame;
            }
            if (start == 0 && frame - spawned >= 600 && OfflineCompensation.ContentReady)
            {
                foreach (var c in cases) c.Initial = Read(c);
                start = frame; elapsedStart = MySession.Static.ElapsedGameTime.Ticks;
                gameplayStart = MySandboxGame.TotalGamePlayTimeInMilliseconds;
                Write("initial.json", new { frame, dormantClusterLibrary = ClusterNodePlugin.Plugin.Instance == null,
                    fast, target, profiles = cases.Select(c => c.Initial).ToArray() });
                MySandboxGame.Static.EnableMaxSpeed = fast;
                wall.Start(); nextSample = frame;
            }
            if (start == 0) return;
            long count = frame - start;
            if (frame >= nextSample)
            {
                nextSample = frame + 3600;
                Write("progress.json", new { nativeFrames = count, target, wallSeconds = wall.Elapsed.TotalSeconds,
                    elapsedTicks = MySession.Static.ElapsedGameTime.Ticks - elapsedStart,
                    gameplayMilliseconds = MySandboxGame.TotalGamePlayTimeInMilliseconds - gameplayStart,
                    cases = cases.Select(c => State(c.Grid)).ToArray() });
            }
            if (count >= target)
            {
                var results = cases.Select(c =>
                {
                    var actual = Read(c);
                    var modeled = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(c.Initial, OfflineProfile.Json), OfflineProfile.Json);
                    // Native resource integrators use frames/60; clustered authority stamps use
                    // milliseconds/16. Compare the same honest number of engine updates.
                    long clockUnits = checked((long)Math.Round(count / (modeled.ClockFrameSeconds * 60)));
                    var result = OfflineModel.Simulate(modeled, modeled.SimulatedThrough + clockUnits, long.MaxValue);
                    return new { initial = c.Initial, actual, modeled, result, native = State(c.Grid) };
                }).ToArray();
                Write("result.json", new { nativeFrames = count, target, fast, wallSeconds = wall.Elapsed.TotalSeconds,
                    elapsedTicks = MySession.Static.ElapsedGameTime.Ticks - elapsedStart,
                    gameplayMilliseconds = MySandboxGame.TotalGamePlayTimeInMilliseconds - gameplayStart,
                    dormantClusterLibrary = ClusterNodePlugin.Plugin.Instance == null, results });
                MySandboxGame.Static.EnableMaxSpeed = false;
                finished = true;
            }
            // Native max-speed changes pacing only. Bound CPU/noise to at most ~500 updates/s.
            if (fast && !finished) Thread.Sleep(2);
        }
        catch (Exception e) { finished = true; MySandboxGame.Static.EnableMaxSpeed = false; Write("error.json", new { error = e.ToString() }); }
    }

    private static object State(MyCubeGrid grid) => new
    {
        name = grid.DisplayName,
        batteries = grid.GetFatBlocks<MyBatteryBlock>().Select(b => new { energy = b.CurrentStoredPower, mode = (int)b.ChargeMode,
            input = b.ResourceSink.CurrentInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId), output = b.SourceComp.CurrentOutput }).ToArray(),
        reactors = grid.GetFatBlocks<MyReactor>().Select(r => new { fuel = (decimal)r.GetInventory().GetItems().Sum(i => (decimal)i.Amount),
            output = r.SourceComp.CurrentOutput, timer = r.m_timer.TimerTickInFrames, sinceTrigger = r.m_timer.FramesFromLastTrigger }).ToArray(),
    };

    private static OfflineProfile Read(Case c) => (OfflineProfile)Capture.Invoke(null, new object[] { new[] { c.Grid }, c.Census, true });
    private void Write(string file, object value)
    {
        string path = Path.Combine(root, file);
        File.WriteAllText(path + ".next", JsonSerializer.Serialize(value, OfflineProfile.Json));
        File.Move(path + ".next", path, true);
    }

    private Case Spawn(int mode, bool solar)
    {
        var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
        b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(10000 + cases.Count * 1000, 10000, 0), Vector3D.Forward, Vector3D.Up);
        b.DisplayName = solar ? "solar-priority" : "battery-mode-" + mode;
        for (int i = 0; i < 2; i++)
        {
            var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(i * 5, 0, 0); battery.CurrentStoredPower = mode == 0 ? 3 : 1.5f;
            battery.ChargeMode = mode; b.CubeBlocks.Add(battery);
        }
        var reactor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
        reactor.Min = new SerializableVector3I(10, 0, 0); reactor.UseConveyorSystem = false; b.CubeBlocks.Add(reactor);
        var antenna = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
        antenna.Min = new SerializableVector3I(12, 0, 0); antenna.BroadcastRadius = 50000; antenna.EnableBroadcasting = true; b.CubeBlocks.Add(antenna);
        for (int i = 0; i < 4; i++)
        {
            var light = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_InteriorLight>("LargeBlockLight_1corner");
            light.Min = new SerializableVector3I(15+i,0,0); b.CubeBlocks.Add(light);
        }
        if (solar)
        {
            var panel = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
            panel.Min = new SerializableVector3I(20, 5, 0); b.CubeBlocks.Add(panel);
        }
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
        grid.GetFatBlocks<MyReactor>().Single().GetInventory().AddItems(10,
            MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
        object census = Activator.CreateInstance(CensusType, true);
        CensusType.GetField("Group").SetValue(census, grid.EntityId);
        CensusType.GetField("Members").SetValue(census, new[] { grid.EntityId });
        if (solar)
        {
            var panel = grid.GetFatBlocks<MySolarPanel>().Single();
            var component = (SpaceEngineers.Game.EntityComponents.GameLogic.MyNewSolarGameLogicComponent)panel.SolarComponent;
            Vector3D normal = component.PanelOrientation, sun = MySector.DirectionToSunNormalized;
            var local = MatrixD.CreateWorld(Vector3D.Zero, normal, Vector3D.CalculatePerpendicularVector(normal));
            var world = MatrixD.CreateWorld(Vector3D.Zero, sun, Vector3D.CalculatePerpendicularVector(sun));
            var matrix = MatrixD.Invert(local) * world; matrix.Translation = grid.PositionComp.GetPosition(); grid.WorldMatrix = matrix;
            var curves = (Dictionary<long, Task<double[]>>)CensusType.GetField("Curves").GetValue(census);
            curves[panel.EntityId] = (Task<double[]>)SampleSun.Invoke(null, new object[] { panel });
        }
        return new Case { Grid = grid, Census = census };
    }

    public void Dispose() { }
}
