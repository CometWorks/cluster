using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace WeldPadProbe
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private const string Marker = "Cluster weld probe ";
        private IMyCubeGrid first;
        private IMyCubeGrid second;
        private readonly HashSet<string> sampled = new HashSet<string>();
        private int ticks;
        private int lastAction;

        private static IMyCubeGrid Spawn(string name, Vector3D position, bool anchor)
        {
            var grid = new MyObjectBuilder_CubeGrid
            {
                DisplayName = Marker + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = anchor,
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(
                    position,
                    Vector3.Forward,
                    anchor ? Vector3.Up : Vector3.Down
                ),
            };
            grid.CubeBlocks.Add(
                new MyObjectBuilder_BatteryBlock
                {
                    SubtypeName = "LargeBlockBatteryBlock",
                    Min = Vector3I.Zero,
                    CustomName = Marker + name,
                    CurrentStoredPower = 3,
                }
            );
            grid.CubeBlocks.Add(
                new MyObjectBuilder_TerminalBlock
                {
                    SubtypeName = "LargeWeldPad",
                    Min = Vector3I.Up,
                }
            );
            return (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        }

        private bool Command(string phase)
        {
            return !sampled.Contains(phase)
                && MyAPIGateway.Utilities.FileExistsInModLocation(
                    phase + ".txt",
                    ModContext.ModItem
                )
                && sampled.Add(phase);
        }

        public override void UpdateAfterSimulation()
        {
            if (++ticks % 10 != 0)
                return;
            if (Command("spawn"))
            {
                var position = new Vector3D(120, 250, 115);
                first = Spawn("A", position, true);
                second = Spawn("B", position + Vector3D.Up * 15, false);
                lastAction = ticks;
            }
            if (Command("align") && first != null && second != null)
            {
                // The pad faces are half a block from their respective marker blocks.
                second.SetPosition(first.GetPosition() + first.WorldMatrix.Up * 2.7);
                lastAction = ticks;
            }
            foreach (var phase in new[] { "initial", "apart", "aligned" })
            {
                if (first != null && ticks - lastAction < 120)
                    continue;
                if (Command(phase))
                {
                    Snapshot(phase);
                    lastAction = ticks;
                }
            }
        }

        private static void Snapshot(string phase)
        {
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid && !e.MarkedForClose);
            var markers = new List<string>();
            var pads = new List<string>();
            foreach (var entity in entities)
            {
                var grid = (IMyCubeGrid)entity;
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);
                foreach (var slim in blocks)
                {
                    var block = slim.FatBlock as IMyTerminalBlock;
                    if (block == null)
                        continue;
                    if (block.CustomName == Marker + "A" || block.CustomName == Marker + "B")
                        markers.Add(
                            block.CustomName.Substring(Marker.Length)
                                + ":"
                                + block.EntityId
                                + ":"
                                + grid.EntityId
                        );
                    if (block.BlockDefinition.SubtypeName == "LargeWeldPad")
                        pads.Add(block.EntityId + ":" + grid.EntityId + ":" + block.IsWorking);
                }
            }
            markers.Sort();
            pads.Sort();
            MyLog.Default.WriteLineAndConsole(
                "weld_pad_probe "
                    + phase
                    + " markers="
                    + (markers.Count == 0 ? "-" : string.Join(",", markers))
                    + " pads="
                    + (pads.Count == 0 ? "-" : string.Join(",", pads))
            );
        }
    }
}
