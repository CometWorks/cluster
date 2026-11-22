using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1871: start a grid's jump the way the server takes a client's request, without a client. The grid needs a
/// charged jump drive nobody owns (block owner 0, so any user may jump it).
/// </summary>
internal static class JumpTestRoutes
{
    private static readonly MethodInfo RequestFromClient = typeof(MyGridJumpDriveSystem).GetMethod(
        "OnRequestJumpFromClient",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    public static void Register()
    {
        // POST /jump-test?grid=&x=&y=&z= - the server-side jump request to that target: after the vanilla 10 s
        // charge the grid arrives where FindSuitableJumpLocation puts it. Answers the grid's position at the request.
        TestRoutes.Add(
            "POST",
            "/jump-test",
            context =>
            {
                long gridId = long.Parse(
                    TestRoutes.Query(context, "grid"),
                    CultureInfo.InvariantCulture
                );
                Vector3D target = new(
                    TestRoutes.Double(context, "x"),
                    TestRoutes.Double(context, "y"),
                    TestRoutes.Double(context, "z")
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (!MyEntities.TryGetEntityById(gridId, out MyCubeGrid grid))
                            throw new ArgumentException("Unknown grid " + gridId);
                        MyJumpDrive drive =
                            grid.GetFatBlocks<MyJumpDrive>().FirstOrDefault()
                            ?? throw new InvalidOperationException("No jump drive on " + gridId);
                        // As MyCubeGrid.OnJumpRequested: a remote client's delay is always 10 s
                        RequestFromClient.Invoke(
                            grid.GridSystems.JumpSystem,
                            new object[] { target, 0L, 10f, (long?)drive.EntityId }
                        );
                        Vector3D at = grid.PositionComp.GetPosition();
                        return (object)
                            new
                            {
                                grid = gridId,
                                jumping = grid.GridSystems.JumpSystem.IsJumping,
                                position = new[] { at.X, at.Y, at.Z },
                            };
                    })
                );
            }
        );
    }
}
