using System.Globalization;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ShieldDamageFixture
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private IMySlimBlock control,
            shielded;
        private int elapsed;
        private float controlHit = -1,
            shieldHit = -1,
            controlStart,
            shieldStart;

        private void AfterDamage(object target, MyDamageInformation info)
        {
            if (info.Type != MyStringHash.GetOrCompute("Bullet"))
                return;
            if (target == control)
                controlHit = info.Amount;
            if (target == shielded)
                shieldHit = info.Amount;
        }

        private static IMySlimBlock CreateTarget(bool addShield)
        {
            var grid = new MyObjectBuilder_CubeGrid
            {
                DisplayName = addShield ? "Shielded control target" : "Unshielded control target",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(
                    new Vector3D(addShield ? 135 : 110, 250, 120),
                    Vector3.Forward,
                    Vector3.Up
                ),
            };
            grid.CubeBlocks.Add(
                new MyObjectBuilder_CubeBlock
                {
                    SubtypeName = "LargeBlockArmorBlock",
                    Min = Vector3I.Zero,
                }
            );
            grid.CubeBlocks.Add(
                new MyObjectBuilder_BatteryBlock
                {
                    SubtypeName = "LargeBlockBatteryBlock",
                    Min = new Vector3I(1, 0, 0),
                    CurrentStoredPower = 3f,
                }
            );
            if (addShield)
                grid.CubeBlocks.Add(
                    new MyObjectBuilder_Refinery
                    {
                        SubtypeName = "LargeShipSmallShieldGeneratorBase",
                        Min = new Vector3I(-1, 0, 0),
                    }
                );
            var live = (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
            return live.GetCubeBlock(Vector3I.Zero);
        }

        public override void UpdateAfterSimulation()
        {
            if (++elapsed % 30 != 0)
                return;
            if (control == null)
            {
                if (
                    !MyAPIGateway.Utilities.FileExistsInModLocation("start.txt", ModContext.ModItem)
                )
                    return;
                control = CreateTarget(false);
                shielded = CreateTarget(true);
                MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(1000, AfterDamage);
                elapsed = 0;
                return;
            }
            if (elapsed == 300)
            {
                var generator =
                    shielded.CubeGrid.GetCubeBlock(new Vector3I(-1, 0, 0))?.FatBlock
                    as IMyTerminalBlock;
                if (generator != null)
                    generator.RefreshCustomInfo();
                var battery =
                    shielded.CubeGrid.GetCubeBlock(new Vector3I(1, 0, 0))?.FatBlock
                    as IMyBatteryBlock;
                MyLog.Default.WriteLineAndConsole(
                    "shield_fixture generator="
                        + (generator?.CustomInfo ?? "missing").Replace("\r", "").Replace("\n", " ")
                        + " functional="
                        + generator?.IsFunctional
                        + " working="
                        + generator?.IsWorking
                        + " battery="
                        + battery?.CurrentStoredPower
                        + " output="
                        + battery?.CurrentOutput
                );
                var damage = MyStringHash.GetOrCompute("Bullet");
                controlStart = control.Integrity;
                shieldStart = shielded.Integrity;
                control.DoDamage(20, damage, true);
                shielded.DoDamage(20, damage, true);
            }
            if (elapsed == 360)
            {
                MyLog.Default.WriteLineAndConsole(
                    "shield_damage_probe control="
                        + controlHit.ToString(CultureInfo.InvariantCulture)
                        + " shielded="
                        + shieldHit.ToString(CultureInfo.InvariantCulture)
                        + " controlLoss="
                        + (controlStart - control.Integrity + control.AccumulatedDamage).ToString(
                            CultureInfo.InvariantCulture
                        )
                        + " shieldLoss="
                        + (shieldStart - shielded.Integrity + shielded.AccumulatedDamage).ToString(
                            CultureInfo.InvariantCulture
                        )
                );
            }
        }
    }
}
