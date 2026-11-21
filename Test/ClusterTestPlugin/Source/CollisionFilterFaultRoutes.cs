using System;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1491 bench route: leave a stale entry in <c>MyPhysics</c>'s collision filter refresh queue when one grid is
/// deleted, the way local-node-2's drain did on octahedron-test 2026-11-14 01:59:14Z. A Harmony postfix on
/// <c>MyCubeGrid.BeforeDelete</c>, installed on first arm, on this test plugin only; inert while nothing is armed.
/// It queues a null entry, which the next physics step's vanilla ProcessCollisionFilterRefreshes throws a
/// NullReferenceException on, the stack the node died with live. The product's CollisionFilterRefreshGuard drops it.
/// </summary>
internal static class CollisionFilterFaultRoutes
{
    private static long m_grid;
    private static long m_queued;
    private static bool m_patched;

    public static void Register()
    {
        // POST /test-collision-filter-fault?entity= - arm: the delete of grid `entity` queues a stale refresh.
        TestRoutes.Add(
            "POST",
            "/test-collision-filter-fault",
            context =>
            {
                long grid = TestRoutes.Long(context, "entity");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (!(MyEntities.GetEntityById(grid) is MyCubeGrid))
                            throw new InvalidOperationException($"No grid {grid} on this node");
                        EnsurePatched();
                        Interlocked.Exchange(ref m_grid, grid);
                        Plugin.Log($"collision-filter-fault armed grid={grid} (SE-1491)");
                        return (object)new { armed = true, grid };
                    })
                );
            }
        );
        // GET /test-collision-filter-fault - how many stale entries were queued.
        TestRoutes.Add(
            "GET",
            "/test-collision-filter-fault",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        grid = Interlocked.Read(ref m_grid),
                        queued = Interlocked.Read(ref m_queued),
                    }
                )
        );
    }

    private static void EnsurePatched()
    {
        if (m_patched)
            return;
        var original =
            AccessTools.Method(typeof(MyCubeGrid), "BeforeDelete")
            ?? throw new MissingMethodException("MyCubeGrid.BeforeDelete");
        new Harmony("ClusterTestPlugin.CollisionFilterFault").Patch(
            original,
            postfix: new HarmonyMethod(
                AccessTools.Method(typeof(CollisionFilterFaultRoutes), nameof(BeforeDeletePostfix))
            )
        );
        m_patched = true;
    }

    private static void BeforeDeletePostfix(MyCubeGrid __instance)
    {
        long grid = Interlocked.Read(ref m_grid);
        if (
            grid == 0
            || __instance.EntityId != grid
            || Interlocked.CompareExchange(ref m_grid, 0, grid) != grid
        )
            return;
        MyPhysics.RefreshCollisionFilter(null);
        Interlocked.Increment(ref m_queued);
        Plugin.Log(
            $"collision-filter-fault queued a stale refresh in the delete of grid {grid} (SE-1491)"
        );
    }
}
