using System.Globalization;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace NaniteRepairFixture
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private class Rig
        {
            public IMyFunctionalBlock Factory;
            public IMySlimBlock Target;
        }

        private Rig enabled,
            disabled;
        private int frames;
        private bool done;

        private static Rig Create(bool treatment)
        {
            var grid = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                DisplayName = treatment ? "Enabled nanite repair" : "Disabled nanite control",
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(
                    new Vector3D(treatment ? 110 : 2110, 140, 100),
                    Vector3.Forward,
                    Vector3.Up
                ),
            };
            grid.CubeBlocks.Add(
                new MyObjectBuilder_ShipWelder
                {
                    SubtypeName = "LargeNaniteControlFacility",
                    Min = Vector3I.Zero,
                    Enabled = false,
                }
            );
            // A continuous armor beam puts the target far beyond the factory block's 4 m vanilla welder.
            for (int x = 3; x <= 16; x++)
                grid.CubeBlocks.Add(
                    new MyObjectBuilder_CubeBlock
                    {
                        SubtypeName = "LargeBlockArmorBlock",
                        Min = new Vector3I(x, 0, 0),
                    }
                );
            for (int x = 3; x < 15; x++)
                grid.CubeBlocks.Add(
                    new MyObjectBuilder_BatteryBlock
                    {
                        SubtypeName = "LargeBlockBatteryBlock",
                        Min = new Vector3I(x, 1, 0),
                        CurrentStoredPower = 3f,
                    }
                );
            // The mod obtains a grid's resource distributor from a ship controller.
            grid.CubeBlocks.Add(
                new MyObjectBuilder_RemoteControl
                {
                    SubtypeName = "LargeBlockRemoteControl",
                    Min = new Vector3I(15, 1, 0),
                }
            );
            var live = (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
            var factory = (IMyFunctionalBlock)live.GetCubeBlock(Vector3I.Zero).FatBlock;
            factory
                .GetInventory()
                .AddItems(100, new MyObjectBuilder_Component { SubtypeName = "SteelPlate" });
            return new Rig
            {
                Factory = factory,
                Target = live.GetCubeBlock(new Vector3I(16, 0, 0)),
            };
        }

        private void Observe(string phase)
        {
            MyLog.Default.WriteLineAndConsole(
                "nanite_repair "
                    + phase
                    + " frame="
                    + frames
                    + " enabled="
                    + enabled.Factory.Enabled
                    + " working="
                    + enabled.Factory.IsWorking
                    + " disabled="
                    + disabled.Factory.Enabled
                    + " creative="
                    + MyAPIGateway.Session.CreativeMode
                    + " max="
                    + enabled.Target.MaxIntegrity.ToString("R", CultureInfo.InvariantCulture)
                    + " test="
                    + enabled.Target.Integrity.ToString("R", CultureInfo.InvariantCulture)
                    + " control="
                    + disabled.Target.Integrity.ToString("R", CultureInfo.InvariantCulture)
            );
        }

        public override void UpdateAfterSimulation()
        {
            if (done || ++frames % 30 != 0)
                return;
            if (enabled == null)
            {
                if (
                    !MyAPIGateway.Utilities.FileExistsInModLocation("start.txt", ModContext.ModItem)
                )
                    return;
                enabled = Create(true);
                disabled = Create(false);
                frames = 0;
                return;
            }
            if (frames == 120)
            {
                Observe("pristine");
                var type = MyStringHash.GetOrCompute("Bullet");
                enabled.Target.DoDamage(300f, type, true);
                disabled.Target.DoDamage(300f, type, true);
            }
            if (frames == 240)
            {
                Observe("damaged");
                enabled.Factory.Enabled = true;
            }
            if (frames > 240 && (enabled.Target.IsFullIntegrity || frames >= 4800))
            {
                Observe("settled");
                enabled.Factory.RefreshCustomInfo();
                MyLog.Default.WriteLineAndConsole(
                    "nanite_factory "
                        + enabled.Factory.CustomInfo.Replace("\r", "").Replace("\n", " | ")
                );
                done = true;
            }
        }
    }
}
