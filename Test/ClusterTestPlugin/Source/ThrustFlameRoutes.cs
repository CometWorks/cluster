using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1694 bench routes: what the node holds of one ship's thrust, to compare with the flames a client shows.
/// GET /thrust-test-state?grid= reads it; POST /thrust-test-dampeners?grid=&amp;on=false switches the dampeners
/// through the synced grid property, so a released throttle leaves every thruster at zero.
/// </summary>
internal static class ThrustFlameRoutes
{
    public static void Register()
    {
        TestRoutes.Add(
            "GET",
            "/thrust-test-state",
            context =>
            {
                long grid = TestRoutes.Long(context, "grid");
                TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => State(grid)));
            }
        );
        TestRoutes.Add(
            "POST",
            "/thrust-test-dampeners",
            context =>
            {
                long grid = TestRoutes.Long(context, "grid");
                bool on = TestRoutes.Query(context, "on", "false") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (MyEntities.GetEntityById(grid) is MyCubeGrid cubeGrid)
                            cubeGrid.EnableDampingInternal(on, true);
                        return State(grid);
                    })
                );
            }
        );
    }

    private static object State(long gridId)
    {
        if (!(MyEntities.GetEntityById(gridId) is MyCubeGrid grid))
            return new { found = false, grid = gridId };
        MyEntityThrustComponent thrust = grid.EntityThrustComponent;
        var controller = grid.GridSystems.ControlSystem?.GetShipController() as MyShipController;
        return new
        {
            found = true,
            grid = gridId,
            dampeners = grid.DampenersEnabled,
            physicsActive = grid.Physics?.IsActive ?? false,
            speed = grid.Physics?.LinearVelocity.Length() ?? 0f,
            position = new[]
            {
                grid.PositionComp.GetPosition().X,
                grid.PositionComp.GetPosition().Y,
                grid.PositionComp.GetPosition().Z,
            },
            controlThrust = Vec(thrust?.ControlThrust ?? Vector3.Zero),
            finalThrust = Vec(thrust?.FinalThrust ?? Vector3.Zero),
            controller = controller?.EntityId ?? 0,
            move = Vec(controller?.MoveIndicator ?? Vector3.Zero),
            thrusters = grid.GetFatBlocks<MyThrust>()
                .Select(t => new
                {
                    id = t.EntityId,
                    min = new[] { t.Min.X, t.Min.Y, t.Min.Z },
                    strength = t.CurrentStrength,
                    thrustOverride = t.ThrustOverride,
                })
                .ToArray(),
        };
    }

    private static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
}
