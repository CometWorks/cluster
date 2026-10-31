using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>Real engine fixtures and a bounded hold at the shared detached-builder apply boundary.
/// All mutation/injection stays in the optional test plugin; production has no test branch.</summary>
internal static class OfflineTestRoutes
{
    private static long target, until;
    private static object applied;
    private static bool patched;
    private static OfflineProfile reference;

    public static void Register()
    {
        TestRoutes.Add("POST", "/offline-test-reference", context =>
        {
            ulong partition = ulong.Parse(TestRoutes.Query(context, "partition"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                reference = OfflineCompensation.CaptureFor(ClusterRuntime.m_partitions.Get(partition)).Single();
                return JsonSerializer.SerializeToElement(reference, OfflineProfile.Json);
            }));
        });
        TestRoutes.Add("GET", "/offline-test-reference", context =>
        {
            ulong partition = ulong.Parse(TestRoutes.Query(context, "partition"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var actual = OfflineCompensation.CaptureFor(ClusterRuntime.m_partitions.Get(partition)).Single();
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
                    .Select(b => new { block = b.EntityId, grid = b.CubeGrid.EntityId, working = b.IsWorking,
                        power = b.ResourceSink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) }).ToArray(),
            })));
        TestRoutes.Add("POST", "/offline-test-rate-fixtures", context =>
        {
            long owner = TestRoutes.Long(context, "owner");
            int count = int.TryParse(TestRoutes.Query(context, "count"), out int parsed) ? parsed : 20;
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => RcTestHarness.RateFixtures(owner, count)));
        });
        TestRoutes.Add("POST", "/offline-test-cache", context =>
        {
            string key = TestRoutes.Query(context, "key");
            int? epoch = int.TryParse(TestRoutes.Query(context, "epoch"), out int supplied) ? supplied : null;
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
            antenna.Enabled = true; antenna.EnableBroadcasting = true; antenna.BroadcastRadius = 1000;
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

    private static object Snapshot(IEnumerable<MyObjectBuilder_CubeGrid> grids) => grids.Select(g => new
    {
        grid = g.EntityId, isStatic = g.IsStatic,
        position = g.PositionAndOrientation is { } pose ? new[] { pose.Position.X, pose.Position.Y, pose.Position.Z } : null,
        blocks = g.CubeBlocks.Select(b => new
        {
            id = b.EntityId, type = b.GetId().ToString(),
            energy = b is MyObjectBuilder_BatteryBlock battery ? (double?)battery.CurrentStoredPower : null,
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
