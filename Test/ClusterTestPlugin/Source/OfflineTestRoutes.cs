using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>Real engine fixtures and a bounded hold at the shared detached-builder apply boundary.
/// All mutation/injection stays in the optional test plugin; production has no test branch.</summary>
internal static class OfflineTestRoutes
{
    private static long cycleGrid, cycleStart;
    private static OfflineProfile cycleProfile;
    private static readonly List<object> CycleTrace = new();
    internal static void ObserveGasCycle()
    {
        if (cycleGrid == 0 || MySession.Static == null || MySession.Static.GameplayFrameCounter - cycleStart > 2400) return;
        if (MyEntities.GetEntityById(cycleGrid) is not MyCubeGrid grid) return;
        CycleTrace.Add(GasCycleState(grid));
    }
    private static object GasCycleState(MyCubeGrid grid)
    {
        var gas = grid.GetFatBlocks<MyGasGenerator>().Single();
        return new { frame = MySession.Static.GameplayFrameCounter - cycleStart,
            working = gas.IsWorking, enabled = gas.SourceComp.Enabled,
            required = gas.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId),
            input = gas.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId),
            output = gas.SourceComp.CurrentOutputByType(MyResourceDistributorComponent.HydrogenId),
            pending = gas.m_gasOutputAggregate.Values.Sum(),
            ice = (decimal)gas.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ore/Ice")),
            battery = grid.GetFatBlocks<MyBatteryBlock>().Single().CurrentStoredPower,
            tank = grid.GetFatBlocks<MyGasTank>().Single().FilledRatio * grid.GetFatBlocks<MyGasTank>().Single().Capacity };
    }
    private static long target, until;
    private static object applied;
    private static bool patched;
    private static OfflineProfile reference;
    private static long capturePartition;
    private static int captureEntered;
    private static readonly ManualResetEventSlim CaptureRelease = new(true);
    private static long adminPreflightPartition, adminReleasePartition;
    private static int adminPreflightEntered, adminReleaseEntered;
    private static object adminReleaseProof;
    private static readonly ManualResetEventSlim AdminPreflightRelease = new(true), AdminSourceRelease = new(true);
    private static long tracedGrid;
    private static readonly ConcurrentQueue<object> TransferTrace = new();
    private static long watchedZoneGrid;
    private static readonly ConcurrentQueue<object> ZoneRestoreTrace = new();
    private static long cancelRestoredZoneGrid;
    private static int restoredZoneCancelled;
    private static object pendingZoneSaveProof;

    public static void Register()
    {
        TestRoutes.Add("POST", "/offline-test-zone-watch", context =>
        {
            Interlocked.Exchange(ref watchedZoneGrid, TestRoutes.Long(context, "grid")); ZoneRestoreTrace.Clear();
            Volatile.Write(ref pendingZoneSaveProof, null);
            TestRoutes.Json(context, 200, new { watching = Interlocked.Read(ref watchedZoneGrid) });
        });
        TestRoutes.Add("GET", "/offline-test-zone-watch", context => TestRoutes.Json(context, 200, ZoneRestoreTrace.ToArray()));
        TestRoutes.Add("GET", "/offline-test-zone-pending-save", context => TestRoutes.Json(context, 200, Volatile.Read(ref pendingZoneSaveProof)));
        TestRoutes.Add("POST", "/offline-test-zone-cancel", context =>
        {
            Interlocked.Exchange(ref cancelRestoredZoneGrid, TestRoutes.Long(context, "grid"));
            Volatile.Write(ref restoredZoneCancelled, 0);
            TestRoutes.Json(context, 200, new { armed = true });
        });
        TestRoutes.Add("GET", "/offline-test-zone-cancel", context => TestRoutes.Json(context, 200, new { cancelled = Volatile.Read(ref restoredZoneCancelled) != 0 }));
        var traceHarmony = new Harmony("ClusterTestPlugin.offline-transfer-observer");
        traceHarmony.Patch(AccessTools.Method(typeof(BlockSafeZones), "Restore"), prefix: new HarmonyMethod(typeof(OfflineTestRoutes), nameof(CancelRestoredZone)));
        traceHarmony.Patch(AccessTools.Method(typeof(ClusterRuntime), "MarkFrozen"), postfix: new HarmonyMethod(typeof(OfflineTestRoutes), nameof(TraceFrozen)));
        traceHarmony.Patch(AccessTools.Method(typeof(ClusterRuntime), "ClearFrozen", new[] { typeof(long), typeof(Guid) }), postfix: new HarmonyMethod(typeof(OfflineTestRoutes), nameof(TraceCleared)));
        TestRoutes.Add("POST", "/offline-test-transfer-trace", context =>
        {
            Interlocked.Exchange(ref tracedGrid, TestRoutes.Long(context, "grid")); TransferTrace.Clear();
            TestRoutes.Json(context, 200, new { frequency = Stopwatch.Frequency });
        });
        TestRoutes.Add("GET", "/offline-test-transfer-trace", context => TestRoutes.Json(context, 200,
            new { frequency = Stopwatch.Frequency, entries = TransferTrace.ToArray() }));
        TestRoutes.Add("POST", "/offline-test-large-station", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => LargeStation(owner)));
        });
        TestRoutes.Add("POST", "/offline-test-radio-at", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            var position = new Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
            string name = TestRoutes.Query(context, "name", "offline-laser-radio");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var grid = RcTestHarness.SpawnStation(name, position, owner, false);
                foreach (var antenna in grid.GetFatBlocks<MyRadioAntenna>()) antenna.RadioBroadcaster.BroadcastRadius = 4000;
                return new { grid = grid.EntityId };
            }));
        });
        TestRoutes.Add("GET", "/offline-test-radio-chain", context =>
        {
            long source = TestRoutes.Long(context, "source"), destination = TestRoutes.Long(context, "destination"), owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(source, out MyEntity entity) || entity is not MyCubeGrid grid) throw new InvalidOperationException("Missing source grid");
                var relays = MyAntennaSystem.Static.GetAllRelayedBroadcasters(grid, owner, mutual: true);
                return new { reachable = AntennaGraphService.CanReachGrid(grid.GetFatBlocks<MyLaserAntenna>().Single(), destination),
                    broadcasters = relays.Select(b => b.Entity.EntityId).ToArray() };
            }));
        });
        // Hold the HTTP request after the Gateway's source report/presync, before
        // game-thread Capture. A real client can acquire a pin during this gap.
        // The production dispatcher and capture then run unchanged.
        ClusterControlServer.AddRouteExtension((context, path) =>
        {
            bool preflight = path.EndsWith("/offline/admin", StringComparison.OrdinalIgnoreCase);
            bool release = path.EndsWith("/release", StringComparison.OrdinalIgnoreCase);
            long held = preflight ? Interlocked.Read(ref adminPreflightPartition) : release ? Interlocked.Read(ref adminReleasePartition) : 0;
            if (held > 0 && (preflight || context.Request.QueryString["partition"] == held.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                if (preflight) { Interlocked.Exchange(ref adminPreflightPartition, 0); Volatile.Write(ref adminPreflightEntered, 1); }
                else
                {
                    adminReleaseProof = TestRoutes.OnGameThread(() =>
                    {
                        var persistence = (PartitionPersistence)AccessTools.Field(typeof(ClusterRuntime), "m_persistence").GetValue(null);
                        var partitions = (PartitionManager)AccessTools.Field(typeof(ClusterRuntime), "m_partitions").GetValue(null);
                        var members = partitions.Peek((ulong)held).Members;
                        bool frozen = members.Any(ClusterRuntime.IsFrozen);
                        uint generation = persistence.OwnedGeneration((ulong)held);
                        bool refused = false;
                        try { MaintenanceTriggers.Run("profile", partitions, persistence, (ulong)held); }
                        catch (InvalidOperationException error) { refused = error.Message.Contains("non-frozen ownership"); }
                        return new { frozen, generation, refused };
                    });
                    Volatile.Write(ref adminReleaseEntered, 1);
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    (preflight ? AdminPreflightRelease : AdminSourceRelease).Wait(TimeSpan.FromSeconds(25));
                    if (!preflight) Interlocked.Exchange(ref adminReleasePartition, 0);
                    ClusterControlServer.Handle(context);
                });
                return true;
            }
            long expected = Interlocked.Read(ref capturePartition);
            if (expected > 0 && context.Request.HttpMethod == "GET" && path.EndsWith("/capture", StringComparison.OrdinalIgnoreCase)
                && context.Request.QueryString["partition"] == expected.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && Interlocked.CompareExchange(ref capturePartition, 0, expected) == expected)
            {
                Volatile.Write(ref captureEntered, 1);
                // The control listener is serial. Hold this one request on a worker so
                // status/release requests and the Gateway's heartbeats keep flowing.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    CaptureRelease.Wait(TimeSpan.FromSeconds(20));
                    ClusterControlServer.Handle(context);
                });
                return true;
            }
            return false;
        });
        TestRoutes.Add("POST", "/offline-test-capture-hold", context =>
        {
            long partition = long.Parse(TestRoutes.Query(context, "partition", "0"), System.Globalization.CultureInfo.InvariantCulture);
            if (partition < 0) throw new ArgumentOutOfRangeException(nameof(partition));
            if (partition == 0) CaptureRelease.Set();
            else { CaptureRelease.Reset(); Volatile.Write(ref captureEntered, 0); }
            Interlocked.Exchange(ref capturePartition, partition);
            TestRoutes.Json(context, 200, new { partition });
        });
        TestRoutes.Add("GET", "/offline-test-capture-hold", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, new { partition = Interlocked.Read(ref capturePartition), entered = Volatile.Read(ref captureEntered) != 0,
                frozen = ClusterRuntime.IsFrozen(grid) });
        });
        TestRoutes.Add("POST", "/offline-test-admin-hold", context =>
        {
            long partition = long.Parse(TestRoutes.Query(context, "partition", "0"));
            if (partition < 0) throw new ArgumentOutOfRangeException(nameof(partition));
            bool preflight = TestRoutes.Query(context, "phase", "preflight") == "preflight";
            var signal = preflight ? AdminPreflightRelease : AdminSourceRelease;
            if (partition == 0) signal.Set(); else signal.Reset();
            if (preflight) { Volatile.Write(ref adminPreflightEntered, 0); Interlocked.Exchange(ref adminPreflightPartition, partition); }
            else { Volatile.Write(ref adminReleaseEntered, 0); Interlocked.Exchange(ref adminReleasePartition, partition); }
            TestRoutes.Json(context, 200, new { partition, preflight });
        });
        TestRoutes.Add("GET", "/offline-test-admin-hold", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            ulong partition = ulong.Parse(TestRoutes.Query(context, "partition", "0"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new
            {
                preflightEntered = Volatile.Read(ref adminPreflightEntered) != 0,
                releaseEntered = Volatile.Read(ref adminReleaseEntered) != 0, proof = Volatile.Read(ref adminReleaseProof),
                frozen = ClusterRuntime.IsFrozen(grid), generation = ((PartitionPersistence)AccessTools.Field(typeof(ClusterRuntime), "m_persistence").GetValue(null)).OwnedGeneration(partition)
            }));
        });
        TestRoutes.Add("POST", "/offline-test-station", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new
            {
                grid = RcTestHarness.SpawnStation("offline-native-station", new Vector3D(20000, 20000, 0), owner, withRemoteControl: true).EntityId,
            }));
        });
        TestRoutes.Add("POST", "/offline-test-colocation", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            var position = new Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var grid = RcTestHarness.SpawnStation("offline-spatial-heavy-home", position, owner, true);
                for (int x = 3; x < 103; x++)
                {
                    var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
                    gyro.Min = new SerializableVector3I(x, 0, 0); gyro.Owner = owner;
                    grid.AddBlock(gyro, false);
                }
                return new { grid = grid.EntityId };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-observed", context =>
        {
            int variant = int.Parse(TestRoutes.Query(context, "variant", "0"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                MySession.Static.Settings.EnableSunRotation = true;
                MySession.Static.Settings.SunRotationIntervalMinutes = 1;
                var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
                b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
                b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
                b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(45000 + variant * 6000, 45000, 0), Vector3D.Forward, Vector3D.Up);
                b.DisplayName = "scheduler-observed-" + variant;
                var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
                battery.Min = new SerializableVector3I(0, 0, 0); battery.CurrentStoredPower = 1; battery.ChargeMode = 1; b.CubeBlocks.Add(battery);
                var reactor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
                reactor.Min = new SerializableVector3I(5, 0, 0); reactor.UseConveyorSystem = false; b.CubeBlocks.Add(reactor);
                var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
                gyro.Min = new SerializableVector3I(7, 0, 0); b.CubeBlocks.Add(gyro);
                var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
                var native = grid.GetFatBlocks<MyReactor>().Single();
                native.GetInventory().AddItems(10, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
                OfflineObservedLogic.Attach(native, sun: true);
                if (variant == 2) OfflineObservedLogic.Attach(native, inventory: true);
                return new { grid = grid.EntityId, block = native.EntityId };
            }));
        });
        TestRoutes.Add("GET", "/offline-test-observed", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new
            {
                nativeFrame = MySession.Static.GameplayFrameCounter,
                phase = MySector.DirectionToSunNormalized,
                status = OfflineCompensation.Status(),
            })));
        TestRoutes.Add("POST", "/offline-test-observed-control", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var grid = (MyCubeGrid)MyEntities.GetEntityById(TestRoutes.Long(context, "grid"));
                var gyro = grid.GetFatBlocks<MyGyro>().Single();
                gyro.GyroOverride = TestRoutes.Query(context, "moving", "true") == "true";
                return new { gyro.GyroOverride };
            })));
        TestRoutes.Add("POST", "/offline-test-gas-cycle", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                cycleGrid = TestRoutes.Long(context, "grid"); cycleStart = MySession.Static.GameplayFrameCounter;
                CycleTrace.Clear();
                var grid = (MyCubeGrid)MyEntities.GetEntityById(cycleGrid);
                cycleProfile = OfflineCompensation.CaptureFor(ClusterRuntime.m_partitions.Get(ClusterRuntime.PluginEntityPartition(cycleGrid).Partition), quick: true).Single();
                CycleTrace.Add(GasCycleState(grid));
                return new { cycleGrid, cycleStart };
            })));
        TestRoutes.Add("GET", "/offline-test-gas-cycle", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new { initial = JsonSerializer.SerializeToElement(cycleProfile, OfflineProfile.Json), trace = CycleTrace.ToArray() })));
        TestRoutes.Add("POST", "/offline-test-mod-gas", context =>
        {
            bool factory = TestRoutes.Query(context, "factory", "false") == "true";
            bool cycling = TestRoutes.Query(context, "cycling", "false") == "true";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
                builder.GridSizeEnum = MyCubeSize.Large; builder.IsStatic = true; builder.CreatePhysics = true;
                builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
                builder.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(25000, cycling ? 30000 : factory ? 25000 : 20000, 0), Vector3D.Forward, Vector3D.Up);
                builder.DisplayName = factory ? "offline-factory-negative" : "offline-adaptible-generator";
                var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("OfflinePartialBattery");
                battery.Min = new SerializableVector3I(-3, 0, 0); battery.CurrentStoredPower = 3;
                battery.ChargeMode = (int)Sandbox.ModAPI.Ingame.ChargeMode.Discharge;
                builder.CubeBlocks.Add(battery);
                var tank = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_GasTank>("LargeHydrogenTank");
                tank.Min = new SerializableVector3I(0, 0, 0); tank.FilledRatio = 0; tank.IsStockpiling = true;
                builder.CubeBlocks.Add(tank);
                var generator = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>(cycling ? "OfflineCyclingGenerator" : factory ? "OfflineFactoryGenerator" : "OfflinePartialGenerator");
                generator.Min = new SerializableVector3I(1, 1, -1); generator.UseConveyorSystem = false;
                builder.CubeBlocks.Add(generator);
                var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(builder, false);
                var gas = grid.GetFatBlocks<MyGasGenerator>().Single();
                gas.GetInventory().AddItems(1000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
                return new { grid = grid.EntityId, generator = gas.EntityId };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-zone", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            int chips = int.Parse(TestRoutes.Query(context, "chips", "3"));
            double seconds = double.Parse(TestRoutes.Query(context, "seconds", "3600"), System.Globalization.CultureInfo.InvariantCulture);
            float radius = float.Parse(TestRoutes.Query(context, "radius", "10"), System.Globalization.CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => AddZone(grid, chips, seconds, radius)));
        });
        TestRoutes.Add("GET", "/offline-test-zone-state", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => ZoneState(grid)));
        });
        TestRoutes.Add("POST", "/offline-test-limit-fuel", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            decimal fuel = decimal.Parse(TestRoutes.Query(context, "fuel"), System.Globalization.CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var reactor = ((MyCubeGrid)MyEntities.GetEntityById(grid)).GetFatBlocks<MyReactor>().Single();
                reactor.GetInventory().Clear();
                if (fuel > 0) reactor.GetInventory().AddItems((MyFixedPoint)fuel, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
                return new { grid, fuel };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-reference", context =>
        {
            ulong partition = ulong.Parse(TestRoutes.Query(context, "partition"));
            bool prepare = TestRoutes.Query(context, "prepare", "false") == "true";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var candidate = ClusterRuntime.m_partitions.Get(partition);
                if (prepare && !OfflineCompensation.Prepare(candidate)) return JsonSerializer.SerializeToElement(new { complete = false });
                reference = OfflineCompensation.CaptureFor(candidate).Single();
                if (prepare) return JsonSerializer.SerializeToElement(new { complete = true, profile = reference }, OfflineProfile.Json);
                return JsonSerializer.SerializeToElement(reference, OfflineProfile.Json);
            }));
        });
        TestRoutes.Add("GET", "/offline-test-reference", context =>
        {
            ulong partition = ulong.Parse(TestRoutes.Query(context, "partition"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                // The live endpoint may have emptied a queue since the starting census. Read
                // stocks/working state without requiring the old profiling signature to survive.
                var actual = OfflineCompensation.CaptureFor(ClusterRuntime.m_partitions.Get(partition), quick: true).Single();
                var predicted = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(reference, OfflineProfile.Json), OfflineProfile.Json);
                var result = OfflineModel.Simulate(predicted, actual.FrozenFrame, OfflineCompensation.MaxFrames);
                return new { actual = JsonSerializer.SerializeToElement(actual, OfflineProfile.Json),
                    predicted = JsonSerializer.SerializeToElement(predicted, OfflineProfile.Json),
                    result = JsonSerializer.SerializeToElement(result, OfflineProfile.Json) };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-sibling", context =>
        {
            long grid = TestRoutes.Long(context, "grid"), owner = TestRoutes.Long(context, "owner");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => RcTestHarness.Sibling(grid, owner)));
        });
        TestRoutes.Add("GET", "/offline-test-measurements", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => new
            {
                attempts = OfflineCompensation.Attempted.Count,
                perBlock = OfflineCompensation.PerBlockRates.Count,
                cache = OfflineRateCache.Status(),
                production = MyEntities.GetEntities().OfType<MyCubeGrid>().SelectMany(g => g.GetFatBlocks<MyProductionBlock>())
                    .Select(b => new { block = b.EntityId, grid = b.CubeGrid.EntityId, working = b.IsWorking, producing = b.IsProducing,
                        output = b.OutputInventory.GetItems().Sum(i => (decimal)i.Amount),
                        power = b.ResourceSink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) }).ToArray(),
            })));
        TestRoutes.Add("POST", "/offline-test-rate-fixtures", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            int count = int.TryParse(TestRoutes.Query(context, "count", "20"), out int parsed) ? parsed : 20;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => RcTestHarness.RateFixtures(owner, count)));
        });
        TestRoutes.Add("POST", "/offline-test-cache", context =>
        {
            string key = TestRoutes.Query(context, "key");
            int? epoch = int.TryParse(TestRoutes.Query(context, "epoch", ""), out int supplied) ? supplied : null;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                OfflineRateCache.Measured(key, 1, 1, epoch);
                return new { key, epoch = OfflineRateCache.Epoch(key) };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-options", context =>
        {
            long id = TestRoutes.Long(context, "grid");
            string mode = TestRoutes.Query(context, "mode");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Options(id, mode)));
        });
        TestRoutes.Add("POST", "/offline-test-production", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => AddProduction(grid)));
        });
        TestRoutes.Add("GET", "/offline-test-state", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Snapshot(
                new[] { (MyObjectBuilder_CubeGrid)((MyCubeGrid)MyEntities.GetEntityById(grid)).GetObjectBuilder() })));
        });
        TestRoutes.Add("GET", "/offline-test-solar-state", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => ((MyCubeGrid)MyEntities.GetEntityById(grid))
                .GetFatBlocks<MySolarPanel>().Select(p => new { block = p.EntityId,
                    maximum = p.SourceComp.MaxOutput, output = p.SourceComp.CurrentOutput }).ToArray()));
        });
        TestRoutes.Add("GET", "/offline-test-gas-state", context =>
        {
            long grid = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => ((MyCubeGrid)MyEntities.GetEntityById(grid))
                .GetFatBlocks<MyGasGenerator>().Select(g => new { block = g.EntityId, working = g.IsWorking,
                    required = g.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId),
                    input = g.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId),
                    outputs = g.SourceComp.ResourceTypes.Select(t => g.SourceComp.CurrentOutputByType(t)).ToArray(),
                    ice = (decimal)g.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ore/Ice")) }).ToArray()));
        });
        TestRoutes.Add("GET", "/offline-test-grid-sinks", context =>
        {
            long id = TestRoutes.Long(context, "grid");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var grid = (MyCubeGrid)MyEntities.GetEntityById(id);
                var distributor = grid.GridSystems.ResourceDistributor;
                var electricity = MyResourceDistributorComponent.ElectricityId;
                distributor.ResourceStateByType(electricity, true, grid);
                var sinks = distributor.m_dataPerType.Where(t => t.TypeId == electricity)
                    .SelectMany(t => t.SinksByPriority.SelectMany(s => s).Concat(t.InputOutputList.Select(p => p.Item1))
                        .Concat(t.ElectricalDistributionGroups.SelectMany(g => g.SinksByPriority.SelectMany(s => s).Concat(g.InputOutputList.Select(p => p.Item1)))))
                    .Where(s => s.Grid == grid).Distinct().ToArray();
                return new { grid = id, totalInput = distributor.TotalRequiredInputByType(electricity, grid),
                    gyro = grid.GridSystems.GyroSystem.ResourceSink.RequiredInputByType(electricity),
                    sinks = sinks.Select(s => new { group = s.Group.String, block = s.Entity?.EntityId,
                        battery = s.Entity is MyBatteryBlock, required = s.RequiredInputByType(electricity), input = s.CurrentInputByType(electricity) }).ToArray() };
            }));
        });
        TestRoutes.Add("POST", "/offline-test-recovery", context =>
        {
            long grid = TestRoutes.Long(context, "grid"), ms = long.Parse(TestRoutes.Query(context, "ms"), System.Globalization.CultureInfo.InvariantCulture);
            if (grid == 0 || ms is < 0 or > 45000) throw new ArgumentOutOfRangeException("grid/ms");
            // Patch on the game thread so it cannot race a running compensation invocation.
            TestRoutes.OnGameThread(() =>
            {
                if (!patched)
                {
                    new Harmony("ClusterTestPlugin.offline-recovery").Patch(
                        AccessTools.Method(typeof(OfflineCompensation), "Apply"),
                        postfix: new HarmonyMethod(typeof(OfflineTestRoutes), nameof(AfterApply)));
                    patched = true;
                }
                return true;
            });
            Volatile.Write(ref applied, null);
            Interlocked.Exchange(ref target, grid);
            Interlocked.Exchange(ref until, DateTime.UtcNow.AddMilliseconds(ms).Ticks);
            TestRoutes.Json(context, 200, new { grid, holdMs = ms });
        });
        // Deliberately no game-thread dispatch: a held game thread cannot answer a dispatched probe.
        TestRoutes.Add("GET", "/offline-test-recovery", context =>
            TestRoutes.Json(context, 200, new { target = Interlocked.Read(ref target), applied = Volatile.Read(ref applied) }));
    }

    private static void CancelRestoredZone(MySafeZoneBlock __0)
    {
        if (__0.CubeGrid.EntityId != Interlocked.Read(ref cancelRestoredZoneGrid) || Volatile.Read(ref restoredZoneCancelled) != 0) return;
        if (ClusterRuntime.IsFrozen(__0.CubeGrid.EntityId)) return; // an operator cannot edit an uncommitted import
        if (MyEntities.TryGetEntityById(__0.m_safeZoneManager.SafeZoneEntityId, out MySafeZone zone) && !zone.Enabled)
        {
            __0.Enabled = false; // actual terminal On/Off, not deletion while still enabled/unpowered
            __0.m_safeZoneManager.SafeZoneRemove_Server();
            Volatile.Write(ref restoredZoneCancelled, 1);
        }
    }

    internal static void ObserveZoneRestore()
    {
        long grid = Interlocked.Read(ref watchedZoneGrid);
        if (grid == 0 || ZoneRestoreTrace.Count >= 2048 || !MyEntities.TryGetEntityById(grid, out MyEntity entity) || entity is not MyCubeGrid native) return;
        var block = native.GetFatBlocks<MySafeZoneBlock>().FirstOrDefault();
        if (block == null) return;
        var manager = block.Components.Get<MySafeZoneComponent>();
        MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone zone);
        if (zone != null && !zone.Enabled && Volatile.Read(ref pendingZoneSaveProof) == null)
        {
            var builder = block.GetObjectBuilderCubeBlock(); // real native Saving postfix
            var carried = BlockBuilderEntry.Read(builder, BlockSafeZones.Key);
            var saved = builder.ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single();
            var censusType = typeof(OfflineCompensation).GetNestedType("Census", System.Reflection.BindingFlags.NonPublic);
            var census = Activator.CreateInstance(censusType, true);
            censusType.GetField("Group").SetValue(census, grid);
            censusType.GetField("Members").SetValue(census, new[] { grid });
            var profile = (OfflineProfile)AccessTools.Method(typeof(OfflineCompensation), "Capture").Invoke(null, new object[] { new[] { native }, census, true });
            Volatile.Write(ref pendingZoneSaveProof, new { entryEnabled = carried != null && BlockSafeZones.Parse(carried).Enabled,
                creditMs = saved.UpkeepTime, activating = saved.Activating, blockEnabled = ((MyObjectBuilder_FunctionalBlock)builder).Enabled,
                upkeepCaptured = profile.Upkeep.Count == 1 && profile.Upkeep[0].Exists && profile.Upkeep[0].OperationalPower > 1 });
        }
        ZoneRestoreTrace.Enqueue(new { frame = MySession.Static.GameplayFrameCounter, active = zone?.Enabled == true,
            required = block.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId),
            input = block.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId), working = block.IsWorking });
    }

    private static void TraceFrozen(long __0, Guid __1) => TraceTransfer(__0, __1, "frozen");
    private static void TraceCleared(long __0, Guid __1) => TraceTransfer(__0, __1, "cleared");
    private static void TraceTransfer(long grid, Guid transfer, string phase)
    {
        if (grid != Interlocked.Read(ref tracedGrid)) return;
        bool live = MyEntities.TryGetEntityById(grid, out MyEntity entity) && !entity.MarkedForClose;
        TransferTrace.Enqueue(new { phase, transfer, ticks = Stopwatch.GetTimestamp(), live, physicsEnabled = live && entity.Physics?.Enabled == true });
        while (TransferTrace.Count > 128) TransferTrace.TryDequeue(out _);
    }

    private static object LargeStation(long owner)
    {
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        builder.GridSizeEnum = MyCubeSize.Large; builder.IsStatic = true; builder.CreatePhysics = true;
        builder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        builder.DisplayName = "controlled-large-online-pull";
        builder.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(20000, 20000, 0), Vector3D.Forward, Vector3D.Up);
        for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++)
        {
            var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
            armor.Min = new SerializableVector3I(x, y, -1); armor.Owner = owner; builder.CubeBlocks.Add(armor);
        }
        for (int i = 0; i < 200; i++)
        {
            var light = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_InteriorLight>("LargeBlockLight_1corner");
            light.Min = new SerializableVector3I(20 + i % 100, i / 100, 0); light.Owner = owner; builder.CubeBlocks.Add(light);
        }
        var antenna = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
        antenna.Owner = owner; antenna.BroadcastRadius = 50000; antenna.EnableBroadcasting = true; builder.CubeBlocks.Add(antenna);
        var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
        battery.Min = new SerializableVector3I(5, 0, 0); battery.Owner = owner; battery.CurrentStoredPower = 3; builder.CubeBlocks.Add(battery);
        var remote = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RemoteControl>("LargeBlockRemoteControl");
        remote.Min = new SerializableVector3I(10, 0, 0); remote.Owner = owner; builder.CubeBlocks.Add(remote);
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(builder, false);
        return new { grid = grid.EntityId, blocks = grid.BlocksCount, functional = grid.GetFatBlocks<MyFunctionalBlock>().Count(),
            fixture = "10000 armor,200 lights,antenna,battery,remote; controlled synthetic payload, shared host" };
    }

    private static object Options(long id, string mode)
    {
        var grid = (MyCubeGrid)MyEntities.GetEntityById(id);
        if (mode == "no-relay")
            foreach (var antenna in grid.GetFatBlocks<MyRadioAntenna>()) antenna.Enabled = false;
        else if (mode == "stay-online")
        {
            var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
            gyro.Min = new SerializableVector3I(2, 0, 0); gyro.GyroOverride = true;
            grid.AddBlock(gyro, false);
        }
        else if (mode == "rotor")
        {
            var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MotorStator>("LargeStator");
            builder.Min = new SerializableVector3I(3, 0, 0); builder.Owner = grid.BigOwners.First();
            var rotor = (MyMotorStator)(grid.AddBlock(builder, false) ?? throw new InvalidOperationException("rotor fixture did not fit")).FatBlock;
            rotor.RecreateTop(builder.Owner, instantBuild: true);
            rotor.TargetVelocityRPM = 1;
        }
        else if (mode == "full-auto-reactor")
        {
            var battery = grid.GetFatBlocks<MyBatteryBlock>().Single();
            battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
            battery.CurrentStoredPower = battery.MaxStoredPower;
            var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
            builder.Min = new SerializableVector3I(9, -1, 0); builder.Owner = grid.BigOwners.First();
            var reactor = (MyReactor)grid.AddBlock(builder, false).FatBlock;
            reactor.GetInventory().AddItems(10, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
        }
        else if (mode is "grid-gyros" or "grid-gyros-off")
        {
            if (!grid.GetFatBlocks<MyGyro>().Any())
                for (int i = 0; i < 4; i++)
                {
                    var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
                    builder.Min = new SerializableVector3I(12 + i, 0, 0);
                    grid.AddBlock(builder, false);
                }
            foreach (var gyro in grid.GetFatBlocks<MyGyro>())
            {
                gyro.Enabled = mode == "grid-gyros";
                gyro.GyroOverride = false;
                gyro.GyroPower = .75f;
                ((Sandbox.ModAPI.IMyGyro)gyro).PowerConsumptionMultiplier = 100;
            }
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>()) battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Discharge;
        }
        else if (mode == "gas-idle")
        {
            for (int x = 2; x <= 13; x++)
            {
                var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(x, -1, 0); grid.AddBlock(armor, false);
            }
            var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>();
            builder.Min = new SerializableVector3I(12, 0, 0); builder.Owner = grid.BigOwners.First(); builder.UseConveyorSystem = false;
            var generator = (MyGasGenerator)(grid.AddBlock(builder, false) ?? throw new InvalidOperationException("idle generator did not fit")).FatBlock;
            generator.GetInventory().AddItems(1000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
        }
        else if (mode == "battery-off")
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>()) battery.Enabled = false;
        else if (mode is "zone-power-off" or "zone-power-on")
        {
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>()) battery.Enabled = mode == "zone-power-on";
            foreach (var reactor in grid.GetFatBlocks<MyReactor>()) reactor.Enabled = mode == "zone-power-on";
        }
        else if (mode == "zone-manual-off")
            grid.GetFatBlocks<MySafeZoneBlock>().Single().Components.Get<MySafeZoneComponent>().SafeZoneRemove_Server();
        else if (mode == "empty-production")
            foreach (var production in grid.GetFatBlocks<MyProductionBlock>()) production.InputInventory.Clear();
        else if (mode == "restore-production")
            foreach (var production in grid.GetFatBlocks<MyProductionBlock>())
            {
                production.InputInventory.Clear(); production.OutputInventory.Clear();
                var item = MyDefinitionId.Parse(production is MyRefinery ? "MyObjectBuilder_Ore/Iron" : "MyObjectBuilder_Ingot/Iron");
                var amount = MyFixedPoint.Min(5000, production.InputInventory.ComputeAmountThatFits(item));
                production.InputInventory.AddItems(amount, MyObjectBuilderSerializerKeen.CreateNewObject(item));
            }
        else if (mode == "full-production")
            foreach (var production in grid.GetFatBlocks<MyProductionBlock>())
            {
                var item = MyDefinitionId.Parse(production is MyRefinery ? "MyObjectBuilder_Ingot/Iron" : "MyObjectBuilder_Component/SteelPlate");
                production.OutputInventory.AddItems(production.OutputInventory.ComputeAmountThatFits(item), MyObjectBuilderSerializerKeen.CreateNewObject(item));
            }
        else if (mode is "radius20" or "box-zone" or "sphere10")
        {
            var block = grid.GetFatBlocks<MySafeZoneBlock>().Single();
            var manager = block.Components.Get<MySafeZoneComponent>();
            var zone = (MySafeZone)MyEntities.GetEntityById(manager.SafeZoneEntityId);
            var settings = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
            settings.Radius = mode == "sphere10" ? 10 : 20;
            settings.Shape = mode == "box-zone" ? MySafeZoneShape.Box : MySafeZoneShape.Sphere;
            if (mode == "box-zone") settings.Size = new Vector3(40, 50, 60);
            MySessionComponentSafeZones.UpdateSafeZone(settings, sync: true);
        }
        else if (mode == "zone-environment")
        {
            for (int i = 0; i < 100; i++)
            {
                var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
                builder.Min = new SerializableVector3I(30 + i % 10 * 6, i / 10 * 6, 0);
                if (grid.AddBlock(builder, false) == null) throw new InvalidOperationException("environment solar did not fit");
            }
            var panel = grid.GetFatBlocks<MySolarPanel>().First();
            Vector3D normal = panel.SolarPanelDefinition.PanelOrientation;
            var local = MatrixD.CreateWorld(Vector3D.Zero, normal, Vector3D.CalculatePerpendicularVector(normal));
            var dark = Vector3D.Normalize(Vector3D.Cross(MySector.DirectionToSunNormalized, MySector.SunProperties.SunRotationAxis));
            var facing = MatrixD.CreateWorld(Vector3D.Zero, dark, Vector3D.CalculatePerpendicularVector(dark));
            var aligned = MatrixD.Invert(local) * facing; aligned.Translation = grid.PositionComp.GetPosition(); grid.WorldMatrix = aligned;
            foreach (var production in grid.GetFatBlocks<MyProductionBlock>()) production.Enabled = false;
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>()) battery.Enabled = false;
            foreach (var reactor in grid.GetFatBlocks<MyReactor>()) reactor.Enabled = false;
        }
        else if (mode == "solar-backup")
        {
            for (int y = 0; y < 5; y++)
            {
                var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(3, y, 0); grid.AddBlock(armor, false);
            }
            var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
            builder.Min = new SerializableVector3I(3, 5, 0);
            var panel = (MySolarPanel)(grid.AddBlock(builder, false) ?? throw new InvalidOperationException("backup solar did not fit")).FatBlock;
            Vector3D normal = ((SpaceEngineers.Game.EntityComponents.GameLogic.MyNewSolarGameLogicComponent)panel.SolarComponent).PanelOrientation;
            var local = MatrixD.CreateWorld(Vector3D.Zero, normal, Vector3D.CalculatePerpendicularVector(normal));
            Vector3D sun = MySector.DirectionToSunNormalized;
            var world = MatrixD.CreateWorld(Vector3D.Zero, sun, Vector3D.CalculatePerpendicularVector(sun));
            var aligned = MatrixD.Invert(local) * world;
            aligned.Translation = grid.PositionComp.GetPosition(); grid.WorldMatrix = aligned;
            // Recreate the powered bubble only after moving its block into the final pose.
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>()) { battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto; battery.Enabled = true; }
        }
        else if (mode == "stop-rotor")
            foreach (var rotor in grid.GetFatBlocks<MyMotorStator>())
            {
                rotor.TargetVelocityRPM = 0;
                ((Sandbox.ModAPI.Ingame.IMyMotorStator)rotor).RotorLock = true;
            }
        else if (mode == "short-radio")
        {
            var antenna = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
            antenna.Min = new SerializableVector3I(3, 0, 0); antenna.Owner = grid.BigOwners.FirstOrDefault();
            antenna.Enabled = true; antenna.EnableBroadcasting = true; antenna.BroadcastRadius = 4000;
            grid.AddBlock(antenna, false);
        }
        else if (mode == "observed")
        {
            for (int x = 2; x <= 13; x++)
            {
                var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(x, -1, 0);
                grid.AddBlock(armor, false);
            }
            var solar = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
            solar.Min = new SerializableVector3I(3, 0, 0);
            OfflineObservedLogic.Attach((grid.AddBlock(solar, false) ?? throw new InvalidOperationException("observed solar fixture did not fit")).FatBlock);
            var generator = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>();
            generator.Min = new SerializableVector3I(12, 0, 0); generator.UseConveyorSystem = false;
            var gas = (grid.AddBlock(generator, false) ?? throw new InvalidOperationException("observed generator fixture did not fit")).FatBlock;
            gas.GetInventory().AddItems(1000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
            OfflineObservedLogic.Attach(gas);
            // A scripted reservoir is deliberately opaque, and a scripted cargo invents items.
            // Both must freeze while the independent source remains observable.
            OfflineObservedLogic.Attach(grid.GetFatBlocks<MyBatteryBlock>().Single());
            var cargo = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer");
            cargo.Min = new SerializableVector3I(2, 1, 0);
            OfflineObservedLogic.Attach(grid.AddBlock(cargo, false).FatBlock, inventory: true);
        }
        else if (mode == "hostile")
        {
            long hostile = MySession.Static.Players.CreateNewIdentity("Offline hostile fixture", initialPlayer: true).IdentityId;
            foreach (var block in grid.GetFatBlocks()) block.ChangeOwner(hostile, MyOwnershipShareModeEnum.None);
        }
        else if (mode == "share-all")
            foreach (var block in grid.GetFatBlocks()) block.ChangeOwner(grid.BigOwners.First(), MyOwnershipShareModeEnum.All);
        else if (mode == "unknown")
        {
            var pb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MyProgrammableBlock>("LargeProgrammableBlock");
            pb.Min = new SerializableVector3I(3, 0, 0); pb.Storage = "offline-storage-sentinel";
            pb.Program = "public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update100; } public void Main(string s) {}";
            grid.AddBlock(pb, false);
            var timer = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_TimerBlock>("TimerBlockLarge");
            timer.Min = new SerializableVector3I(4, 0, 0); timer.IsCountingDown = true; timer.CurrentTime = 600000;
            grid.AddBlock(timer, false);
            var connector = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipConnector>("Connector");
            connector.Min = new SerializableVector3I(5, 0, 0); connector.ThrowOut = true;
            grid.AddBlock(connector, false);
        }
        else throw new ArgumentException("Unknown fixture mode " + mode);
        return new { grid = id, mode, state = Snapshot(new[] { (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder() }) };
    }

    private static void AfterApply(IDictionary<long, MyObjectBuilder_EntityBase> __0)
    {
        long grid = Interlocked.Read(ref target);
        if (grid == 0 || !__0.ContainsKey(grid)) return;
        Volatile.Write(ref applied, new
        {
            grid, frame = OfflineCompensation.Frame,
            builders = Snapshot(__0.Values.OfType<MyObjectBuilder_CubeGrid>()),
            compensation = JsonSerializer.SerializeToElement(OfflineCompensation.Status(), OfflineProfile.Json),
        });
        Plugin.Log("offline recovery applied before instantiation grid=" + grid);
        // The bench SIGKILLs only this root's node inside this window. An abandoned probe releases.
        while (DateTime.UtcNow.Ticks < Interlocked.Read(ref until)) Thread.Sleep(20);
        Interlocked.Exchange(ref target, 0);
    }

    private static object AddProduction(long id)
    {
        var grid = (MyCubeGrid)MyEntities.GetEntityById(id);
        if (grid.GetFatBlocks().OfType<MyRefinery>().Any()) throw new InvalidOperationException("fixture already has a refinery");
        var refinery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Refinery>("Blast Furnace");
        refinery.Min = new SerializableVector3I(6, 0, 0);
        refinery.Owner = grid.BigOwners.FirstOrDefault();
        refinery.Enabled = true;
        refinery.UseConveyorSystem = false;
        // A connected armor spine prevents the fixture being split into disconnected grids.
        for (int x = 2; x < 9; x++)
        {
            var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
            armor.Min = new SerializableVector3I(x, -1, 0);
            grid.AddBlock(armor, false);
        }
        var slim = grid.AddBlock(refinery, false) ?? throw new InvalidOperationException("refinery fixture did not fit");
        var block = (MyRefinery)slim.FatBlock;
        block.InputInventory.AddItems((MyFixedPoint)10000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron"));
        return new { grid = id, refinery = block.EntityId, state = Snapshot(new[] { (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder() }) };
    }

    private static object AddZone(long id, int chips, double seconds, float radius)
    {
        if (chips is < 0 or > 100 || !double.IsFinite(seconds) || seconds <= 0 || seconds > 14400 || !float.IsFinite(radius)) throw new ArgumentOutOfRangeException();
        var grid = (MyCubeGrid)MyEntities.GetEntityById(id);
        if (!grid.GetFatBlocks<MyRefinery>().Any()) AddProduction(id);
        if (!grid.GetFatBlocks<MyReactor>().Any()) Options(id, "full-auto-reactor");
        for (int x = 9; x <= 18; x++)
        {
            if (grid.GetCubeBlock(new Vector3I(x, -1, 0)) != null) continue;
            var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
            armor.Min = new SerializableVector3I(x, -1, 0); grid.AddBlock(armor, false);
        }
        var assemblerBuilder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Assembler>("LargeAssembler");
        assemblerBuilder.Min = new SerializableVector3I(14, 0, 0); assemblerBuilder.Owner = grid.BigOwners.First(); assemblerBuilder.UseConveyorSystem = false;
        var assembler = (MyAssembler)(grid.AddBlock(assemblerBuilder, false) ?? throw new InvalidOperationException("zone assembler did not fit")).FatBlock;
        assembler.InputInventory.AddItems(1000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Iron"));
        assembler.InsertQueueItem(-1, MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate")), 1000);
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock>("SafeZoneBlock");
        builder.Min = new SerializableVector3I(10, 0, 0); builder.Owner = grid.BigOwners.First(); builder.Enabled = true; builder.UseConveyorSystem = false;
        var block = (MySafeZoneBlock)(grid.AddBlock(builder, false) ?? throw new InvalidOperationException("safe zone did not fit")).FatBlock;
        var manager = block.Components.Get<MySafeZoneComponent>();
        if (chips > 0) block.GetInventory().AddItems(chips, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Component>("ZoneChip"));
        manager.m_upkeepTime = TimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds + seconds * 1000);
        manager.m_timeLeft = TimeSpan.FromSeconds(seconds);
        manager.SafeZoneCreate_Server(skipActivationTimer: true);
        if (!MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone zone)) throw new InvalidOperationException("native safe zone was not created");
        var settings = (MyObjectBuilder_SafeZone)zone.GetObjectBuilder();
        settings.Radius = radius;
        settings.AllowedActions ^= MySafeZoneAction.Welding;
        MySessionComponentSafeZones.UpdateSafeZone(settings, sync: true);
        return ZoneState(id);
    }

    private static object ZoneState(long id)
    {
        var grid = (MyCubeGrid)MyEntities.GetEntityById(id);
        var block = grid.GetFatBlocks<MySafeZoneBlock>().Single();
        var manager = block.Components.Get<MySafeZoneComponent>();
        MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone zone);
        var component = (MyObjectBuilder_SafeZoneComponent)manager.Serialize();
        var saved = component.SafeZoneOb as MyObjectBuilder_SafeZone;
        return new { grid = id, frame = OfflineCompensation.Frame, gameplayFrame = MySession.Static.GameplayFrameCounter,
            block = block.EntityId, blockEnabled = block.Enabled, working = block.IsWorking,
            powerRequired = block.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId),
            supplied = block.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId),
            zone = manager.SafeZoneEntityId, active = zone?.Enabled == true, activating = component.Activating,
            remainingMs = zone != null && !component.Activating ? component.UpkeepTime : manager.m_timeLeft.TotalMilliseconds, activationMs = component.ActivationTime,
            radius = zone?.Radius ?? saved?.Radius, shape = (zone?.Shape ?? saved?.Shape)?.ToString(),
            allowedActions = (zone?.AllowedActions ?? saved?.AllowedActions)?.ToString(),
            producers = grid.GetFatBlocks<MyReactor>().Select(r => new { block = r.EntityId, output = r.SourceComp.CurrentOutput, maximum = r.MaxOutput,
                timerFrames = r.m_timer?.TimerTickInFrames ?? 0, fuel = (decimal)r.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ingot/Uranium")) }).ToArray(),
            consumers = grid.GetFatBlocks().Where(b => b.ResourceSink != null).Select(b => new { block = b.EntityId,
                input = b.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) }).ToArray(),
            state = Snapshot(new[] { (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder() }) };
    }

    private static object Snapshot(IEnumerable<MyObjectBuilder_CubeGrid> grids) => grids.Select(g => new
    {
        grid = g.EntityId, isStatic = g.IsStatic,
        position = g.PositionAndOrientation is { } pose ? new[] { pose.Position.X, pose.Position.Y, pose.Position.Z } : null,
        blocks = g.CubeBlocks.Select(b => new
        {
            id = b.EntityId, type = b.GetId().ToString(),
            energy = b is MyObjectBuilder_BatteryBlock battery ? (double?)battery.CurrentStoredPower : null,
            enabled = b is MyObjectBuilder_FunctionalBlock functional ? (bool?)functional.Enabled : null,
            zone = b is ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock zoneBlock ? (long?)zoneBlock.SafeZoneId : null,
            upkeep = b.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>()
                .Select(c => new { remainingMs = c.UpkeepTime, activating = c.Activating, activationMs = c.ActivationTime }).FirstOrDefault(),
            storage = b is MyObjectBuilder_MyProgrammableBlock pb ? pb.Storage : null,
            countdown = b is MyObjectBuilder_TimerBlock timer ? (int?)timer.CurrentTime : null,
            inventories = OfflineCompensation.Inventories(b).Select(i => new
            {
                id = i.InventoryId,
                items = i.Items.Select(item => new { type = item.PhysicalContent.GetId().ToString(), amount = (decimal)item.Amount, id = item.ItemId }).ToArray(),
            }).ToArray(),
        }).ToArray(),
    }).ToArray();
}
