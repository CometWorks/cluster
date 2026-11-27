using System.Collections.Generic;
using System.Globalization;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace RelativeSpeedProbe
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private const string Prefix = "RTS governor probe ";
        private int frames,
            untilSample;
        private string phase;
        private bool spawned,
            moved;
        private IMyCubeGrid[] rigs;

        private static IMyCubeGrid Create(int index)
        {
            var builder = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                DampenersEnabled = false,
                DisplayName = Prefix + index,
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(
                    new Vector3D(110, 140 + 20 * index, 110),
                    Vector3.Forward,
                    Vector3.Up
                ),
            };
            builder.CubeBlocks.Add(
                new MyObjectBuilder_CubeBlock
                {
                    SubtypeName = "LargeBlockArmorBlock",
                    Min = Vector3I.Zero,
                }
            );
            if (index != 1)
                builder.CubeBlocks.Add(
                    new MyObjectBuilder_Cockpit
                    {
                        SubtypeName = "LargeBlockCockpit",
                        Min = new Vector3I(1, 0, 0),
                    }
                );
            return (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
        }

        public override void UpdateAfterSimulation()
        {
            ++frames;
            if (untilSample > 0)
            {
                if (--untilSample == 0)
                    Observe("settled");
                return;
            }
            if (frames % 15 != 0)
                return;
            if (!spawned && Command("spawn"))
            {
                spawned = true;
                rigs = new[] { Create(0), Create(1), Create(2) };
                phase = "initial";
                Observe("stationary");
                Kick();
            }
            else if (!moved && Command("moved"))
            {
                moved = true;
                long id;
                using (
                    var reader = MyAPIGateway.Utilities.ReadFileInModLocation(
                        "moved.txt",
                        ModContext.ModItem
                    )
                )
                    id = long.Parse(reader.ReadToEnd().Trim());
                IMyEntity entity;
                if (
                    !MyAPIGateway.Entities.TryGetEntityById(id, out entity)
                    || !(entity is IMyCubeGrid)
                )
                {
                    MyLog.Default.WriteLineAndConsole("rts_probe missing id=" + id);
                    return;
                }
                rigs = new[] { (IMyCubeGrid)entity };
                phase = "moved";
                Observe("imported");
                Kick();
            }
        }

        private bool Command(string name)
        {
            return MyAPIGateway.Utilities.FileExistsInModLocation(
                name + ".txt",
                ModContext.ModItem
            );
        }

        private void Kick()
        {
            for (int i = 0; i < rigs.Length; i++)
                rigs[i].Physics.LinearVelocity = i < 2 ? new Vector3(60, 0, 0) : Vector3.Zero;
            Observe("kick");
            untilSample = 240;
        }

        private void Observe(string step)
        {
            var values = new List<string>();
            foreach (var rig in rigs)
                values.Add(
                    rig.EntityId
                        + "="
                        + rig.Physics.LinearVelocity.Length()
                            .ToString("R", CultureInfo.InvariantCulture)
                );
            MyLog.Default.WriteLineAndConsole(
                "rts_probe "
                    + phase
                    + " "
                    + step
                    + " frame="
                    + frames
                    + " grids="
                    + string.Join(",", values)
            );
        }
    }
}
