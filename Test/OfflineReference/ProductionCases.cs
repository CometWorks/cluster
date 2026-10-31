using System;
using System.Linq;
using ClusterPlugin;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace OfflineReference;

public sealed partial class Plugin
{
    private Case SpawnProduction(bool full)
    {
        var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
        b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(30000, full ? 30000 : 20000, 0), Vector3D.Forward, Vector3D.Up);
        b.DisplayName = full ? "production-output-full" : "production-three-ores";
        for (int i = 0; i < 2; i++)
        {
            var r = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Refinery>("LargeRefinery");
            r.Min = new SerializableVector3I(i * 10, 0, 0); r.UseConveyorSystem = true; b.CubeBlocks.Add(r);
        }
        var a = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Assembler>("LargeAssembler");
        a.Min = new SerializableVector3I(20, 0, 0); a.UseConveyorSystem = true; b.CubeBlocks.Add(a);
        var cargo = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer");
        cargo.Min = new SerializableVector3I(26, 0, 0); b.CubeBlocks.Add(cargo);
        var output = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer");
        output.Min = new SerializableVector3I(28, 0, 0); b.CubeBlocks.Add(output);
        var reactor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
        reactor.Min = new SerializableVector3I(-5, 0, 0); reactor.UseConveyorSystem = false; b.CubeBlocks.Add(reactor);
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
        grid.GetFatBlocks<MyReactor>().Single().GetInventory().AddItems(10, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
        foreach (var refinery in grid.GetFatBlocks<MyRefinery>().ToArray()) AttachProductivity(grid, refinery);
        ConnectPorts(grid);
        var input = grid.GetFatBlocks<MyCargoContainer>().OrderBy(c => c.Position.X).First().GetInventory();
        foreach (string ore in new[] { "Iron", "Nickel", "Cobalt" })
            input.AddItems(ore == "Iron" ? 600 : 100, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>(ore));
        var assembler = grid.GetFatBlocks<MyAssembler>().Single();
        assembler.InsertQueueItem(-1, MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate")), 1000);
        object census = Activator.CreateInstance(CensusType, true);
        CensusType.GetField("Group").SetValue(census, grid.EntityId);
        CensusType.GetField("Members").SetValue(census, new[] { grid.EntityId });
        return new Case { Grid = grid, Census = census, Start = () =>
        {
            if (grid.GetFatBlocks<MyRefinery>().Any(r => r.UpgradeValues["Productivity"] <= 0)) throw new InvalidOperationException("Native productivity modules not connected");
            if (full)
            {
                foreach (var r in grid.GetFatBlocks<MyRefinery>())
                {
                    var id = MyDefinitionId.Parse("MyObjectBuilder_Ingot/Stone");
                    r.OutputInventory.AddItems(r.OutputInventory.ComputeAmountThatFits(id), MyObjectBuilderSerializerKeen.CreateNewObject(id));
                }
                var component = MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate");
                assembler.OutputInventory.AddItems(assembler.OutputInventory.ComputeAmountThatFits(component), MyObjectBuilderSerializerKeen.CreateNewObject(component));
                var ballast = MyDefinitionId.Parse("MyObjectBuilder_Ingot/Stone");
                assembler.InputInventory.AddItems(assembler.InputInventory.ComputeAmountThatFits(ballast), MyObjectBuilderSerializerKeen.CreateNewObject(ballast));
                foreach (var container in grid.GetFatBlocks<MyCargoContainer>())
                    container.GetInventory().AddItems(container.GetInventory().ComputeAmountThatFits(ballast), MyObjectBuilderSerializerKeen.CreateNewObject(ballast));
            }
        } };
    }

    private static void AttachProductivity(MyCubeGrid grid, MyRefinery refinery)
    {
        // Use native upgrade-port geometry to place a real vanilla module. No direct
        // UpgradeValues mutation: the engine must apply its ordinary connected module.
        foreach (var port in refinery.GetComponent().ConnectionPositions)
        foreach (var forward in Base6Directions.EnumDirections)
        foreach (var up in Base6Directions.EnumDirections)
        {
            if (Base6Directions.GetAxis(forward) == Base6Directions.GetAxis(up)) continue;
            var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_UpgradeModule>("LargeProductivityModule");
            b.Min = new SerializableVector3I(45, 0, 0); b.BlockOrientation = new SerializableBlockOrientation(forward, up);
            var slim = grid.AddBlock(b, false);
            if (slim == null) continue;
            var module = (MyUpgradeModule)slim.FatBlock;
            var connection = module.m_connectionPositions.FirstOrDefault(p => p.Direction == Base6Directions.GetFlippedDirection(port.Direction));
            Vector3I shift = port.LocalGridPosition - connection.GetConnectingPosition().LocalGridPosition;
            bool matching = module.m_connectionPositions.Any(p => p.Direction == Base6Directions.GetFlippedDirection(port.Direction));
            grid.RemoveBlock(slim, updatePhysics: false);
            if (!matching) continue;
            b.Min = (Vector3I)b.Min + shift;
            var placed = grid.AddBlock(b, false);
            if (placed == null) continue;
            ((MyUpgradeModule)placed.FatBlock).RefreshConnections();
            return;
        }
        throw new InvalidOperationException("No native module port placement");
    }
}
