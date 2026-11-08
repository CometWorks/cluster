using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1272 bench route: whether Havok has the given grids asleep on this node, read on the game thread, so a scenario
/// can sample a pile's sleep state on both sides of a handover.
/// </summary>
internal static class AsleepTestRoutes
{
    public static void Register()
    {
        // GET /asleep-test-state?ids=a,b,c - per grid on this node: its body enabled and Havok-active. A grid not on
        // this node (or closing) is left out; "active" counts the listed grids whose enabled body is awake.
        TestRoutes.Add("GET", "/asleep-test-state", context =>
        {
            long[] ids = TestRoutes.Query(context, "ids").Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray();
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var grids = ids.Select(id => MyEntities.TryGetEntityById(id, out MyEntity entity) ? entity as MyCubeGrid : null)
                    .Where(grid => grid != null && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    .Select(grid => new { id = grid.EntityId, enabled = grid.Physics.Enabled, active = grid.Physics.RigidBody.IsActive })
                    .ToArray();
                return (object)new
                {
                    present = grids.Length,
                    enabled = grids.Count(grid => grid.enabled),
                    active = grids.Count(grid => grid.enabled && grid.active),
                    grids,
                };
            }));
        });
    }
}
