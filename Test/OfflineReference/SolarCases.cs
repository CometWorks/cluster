using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClusterPlugin;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using Sandbox.Game.SessionComponents;
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
    private readonly List<object> solarSamples = new();
    private int lastSunBin = -1;
    private void SpawnPlanetSolar()
    {
        var planet = MyPlanets.GetPlanets().FirstOrDefault() ?? throw new InvalidOperationException("Planet reference has no planet");
        Vector3D center = planet.PositionComp.GetPosition();
        Vector3D axis = MySector.SunProperties.SunRotationAxis;
        Vector3D direction = MySector.SunProperties.BaseSunDirectionNormalized;
        direction = Vector3D.Normalize(direction - axis * Vector3D.Dot(direction, axis));
        Vector3D chosen = direction, surface = Vector3D.Zero;
        double ridge = double.MinValue;
        for (int i = 0; i < 72; i++)
        {
            var normal = Vector3D.TransformNormal(direction, MatrixD.CreateFromAxisAngle(axis, i * Math.PI / 36));
            var ground = planet.GetClosestSurfacePointGlobal(center + normal * planet.MaximumRadius);
            var tangent = Vector3D.Normalize(Vector3D.Cross(axis, normal));
            foreach (int side in new[] { -1, 1 })
            {
                var neighbour = planet.GetClosestSurfacePointGlobal(ground + tangent * side * 750);
                double height = Vector3D.Dot(neighbour - ground, normal);
                if (height <= ridge) continue;
                ridge = height; chosen = normal; surface = ground;
            }
        }
        Write("solar-geometry.json", new { center, surface, normal = chosen, ridge750m = ridge, periodMinutes = MySession.Static.Settings.SunRotationIntervalMinutes });
        if (ridge < 30) throw new InvalidOperationException("No real terrain ridge above the reference precondition");
        var lateral = Vector3D.Normalize(Vector3D.Cross(chosen, axis));
        for (int variant = 0; variant < 4; variant++)
        {
            var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
            b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
            b.PositionAndOrientation = new MyPositionAndOrientation(surface + chosen * (variant == 1 ? 8 : 300) + lateral * (variant * 250), Vector3D.Forward, Vector3D.Up);
            b.DisplayName = new[] { "planet-open", "planet-ridge", "planet-own-obstruction", "planet-nearby-obstruction" }[variant];
            var panel = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
            b.CubeBlocks.Add(panel);
            var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(-8, 0, 0); battery.CurrentStoredPower = 3; battery.ChargeMode = 2; b.CubeBlocks.Add(battery);
            var farm = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenFarm>("LargeBlockOxygenFarm");
            farm.Min = new SerializableVector3I(10, 0, 0); b.CubeBlocks.Add(farm);
            var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
            var native = grid.GetFatBlocks<MySolarPanel>().Single();
            Vector3D normal = native.SolarPanelDefinition.PanelOrientation;
            var local = MatrixD.CreateWorld(Vector3D.Zero, normal, Vector3D.CalculatePerpendicularVector(normal));
            var world = MatrixD.CreateWorld(Vector3D.Zero, chosen, Vector3D.CalculatePerpendicularVector(chosen));
            var matrix = MatrixD.Invert(local) * world; matrix.Translation = grid.PositionComp.GetPosition(); grid.WorldMatrix = matrix;
            if (variant >= 2)
            {
                var wallBuilder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
                wallBuilder.GridSizeEnum = MyCubeSize.Large; wallBuilder.IsStatic = true; wallBuilder.CreatePhysics = true;
                wallBuilder.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
                wallBuilder.PositionAndOrientation = new MyPositionAndOrientation(matrix.Translation, matrix.Forward, matrix.Up);
                var forward = Base6Directions.GetIntVector(Base6Directions.GetClosestDirection(normal));
                var side = Base6Directions.GetIntVector(Base6Directions.GetPerpendicular(Base6Directions.GetClosestDirection(normal)));
                Vector3I.Cross(ref forward, ref side, out var up);
                for (int x = -8; x <= 16; x++) for (int y = -8; y <= 8; y++)
                {
                    var armour = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
                    armour.Min = forward * 5 + side * x + up * y;
                    if (variant == 2) grid.AddBlock(armour, false);
                    else wallBuilder.CubeBlocks.Add(armour);
                }
                if (variant == 3) MyEntities.CreateFromObjectBuilderAndAdd(wallBuilder, false);
            }
            object census = Activator.CreateInstance(CensusType, true);
            CensusType.GetField("Group").SetValue(census, grid.EntityId);
            CensusType.GetField("Members").SetValue(census, new[] { grid.EntityId });
            cases.Add(new Case { Grid = grid, Census = census, Start = () =>
            {
                var curves = (Dictionary<long, Task<double[]>>)CensusType.GetField("Curves").GetValue(census);
                foreach (var block in grid.GetFatBlocks().Where(b => b is MySolarPanel or MyOxygenFarm))
                    curves[block.EntityId] = (Task<double[]>)SampleSun.Invoke(null, new object[] { block });
            } });
        }
    }

    private void SamplePlanetSolar(long count)
    {
        double phase = MySession.Static.ElapsedGameTime.TotalMinutes / MySession.Static.Settings.SunRotationIntervalMinutes * 360;
        int bin = (int)Math.Floor(phase / 5);
        if (bin == lastSunBin) return;
        lastSunBin = bin;
        solarSamples.Add(new { nativeFrames = count, phase, sun = MySector.DirectionToSunNormalized,
            grids = cases.Select(c => new { name = c.Grid.DisplayName,
                dark = MySectorWeatherComponent.IsOnDarkSide(c.Grid.PositionComp.GetPosition()),
                panels = c.Grid.GetFatBlocks<MySolarPanel>().Select(p => new { block = p.EntityId, ratio = p.SolarComponent.MaxOutput }).ToArray(),
                farms = c.Grid.GetFatBlocks<MyOxygenFarm>().Select(f => new { block = f.EntityId, ratio = f.SolarComponent.MaxOutput, powered = f.IsWorking }).ToArray() }).ToArray() });
        Write("solar-samples.json", solarSamples);
    }
}
