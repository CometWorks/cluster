using System;
using System.Collections.Generic;
using System.Linq;
using ClusterPlugin;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.Conveyors;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace OfflineReference;

public sealed partial class Plugin
{
    private Case SpawnGas(int variant)
    {
        var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
        b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(20000 + variant * 1000, 20000, 0), Vector3D.Forward, Vector3D.Up);
        b.DisplayName = new[] { "gas-seeded-exhaustion", "gas-cold-negative", "gas-seeded-ice", "gas-battery-bootstrap", "gas-isolated-conveyor" }[variant];
        var engine = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_HydrogenEngine>("LargeHydrogenEngine");
        engine.Min = new SerializableVector3I(0, 0, 0); engine.Enabled = variant != 1; engine.Capacity = variant is 1 or 3 ? 0 : 10000; b.CubeBlocks.Add(engine);
        var tank = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenTank>("LargeHydrogenTank");
        tank.Min = new SerializableVector3I(5, 0, 0); tank.Enabled = true; tank.FilledRatio = variant is 2 or 3 ? 0 : 200000f / 15000000; b.CubeBlocks.Add(tank);
        var generator = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>();
        generator.Min = new SerializableVector3I(12, 0, 0); generator.Enabled = variant != 3; generator.UseConveyorSystem = false; b.CubeBlocks.Add(generator);
        var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
        battery.Min = new SerializableVector3I(16, 0, 0); battery.Enabled = true; battery.ChargeMode = variant == 3 ? 2 : 1; battery.CurrentStoredPower = variant == 3 ? .002f : 0; b.CubeBlocks.Add(battery);
        var light = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
        light.Min = new SerializableVector3I(25, 0, 0); light.BroadcastRadius = 50000; light.EnableBroadcasting = true; b.CubeBlocks.Add(light);
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
        if (variant is 0 or 1 or 4 && grid.GetFatBlocks<MyGasTank>().Single().FilledRatio * grid.GetFatBlocks<MyGasTank>().Single().Capacity < 190000)
            throw new InvalidOperationException("Native tank did not retain the fixture's initial gas stock");
        if (variant != 4) ConnectPorts(grid);
        object census = Activator.CreateInstance(CensusType, true);
        CensusType.GetField("Group").SetValue(census, grid.EntityId);
        CensusType.GetField("Members").SetValue(census, new[] { grid.EntityId });
        var gasGenerator = grid.GetFatBlocks<MyGasGenerator>().Single();
        gasGenerator.GetInventory().AddItems(500, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
        return new Case { Grid = grid, Census = census, Start = () =>
        {
            // All engine integrators have already advanced during the warm-up. No
            // stock reset crosses a native Update100 timer boundary at the reference.
            var e = grid.GetFatBlocks<MyHydrogenEngine>().Single();
            if (variant == 1) e.Enabled = true;
            if (variant == 3) { gasGenerator.Enabled = true; grid.GetFatBlocks<MyBatteryBlock>().Single().Enabled = true; }
            if (variant is not (1 or 4))
                foreach (var endpoint in grid.GetFatBlocks().OfType<IMyConveyorEndpointBlock>())
                    if (!MyGridConveyorSystem.Reachable(e.ConveyorEndpoint, endpoint.ConveyorEndpoint)) throw new InvalidOperationException("Gas reference conveyor circuit disconnected, variant=" + variant);
        } };
    }
    private static void ConnectPorts(MyCubeGrid grid)
    {
        // Ordinary conveyor junctions connect actual model ports, rather than making
        // an artificial distributor circuit. A plane below the blocks joins the stems.
        int minX = grid.Min.X - 6, maxX = grid.Max.X + 6;
        int minY = grid.Min.Y - 6, maxY = grid.Max.Y + 6, minZ = grid.Min.Z - 6, maxZ = grid.Max.Z + 6;
        var ports = grid.GetFatBlocks().OfType<IMyConveyorEndpointBlock>().Select(endpoint => endpoint.ConveyorEndpoint.GetPosition(0).NeighbourGridPosition).ToArray();
        void Junction(Vector3I cell)
        {
            if (grid.GetCubeBlock(cell) != null) return;
            var junction = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Conveyor>("LargeBlockConveyor");
            junction.Min = cell; grid.AddBlock(junction, false);
        }
        foreach (var port in ports)
        {
            var hub = new Vector3I(0, -4, 5);
            var queue = new Queue<Vector3I>(); queue.Enqueue(port);
            var previous = new Dictionary<Vector3I, Vector3I> { [port] = port };
            while (queue.Count > 0 && !previous.ContainsKey(hub))
            {
                var cell = queue.Dequeue();
                foreach (var direction in Base6Directions.IntDirections)
                {
                    var next = cell + direction;
                    if (next.X < minX || next.X > maxX || next.Y < minY || next.Y > maxY || next.Z < minZ || next.Z > maxZ || previous.ContainsKey(next)) continue;
                    var block = grid.GetCubeBlock(next);
                    if (block != null && block.BlockDefinition.Id.SubtypeName != "LargeBlockConveyor") continue;
                    previous[next] = cell; queue.Enqueue(next);
                }
            }
            if (!previous.ContainsKey(hub)) throw new InvalidOperationException("No ordinary conveyor path");
            for (var cell = hub; cell != port; cell = previous[cell]) Junction(cell);
            Junction(port);
        }
    }

}
