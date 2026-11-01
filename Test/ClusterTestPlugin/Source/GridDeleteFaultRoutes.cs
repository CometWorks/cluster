using System;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities;

namespace ClusterTestPlugin;

/// <summary>
/// #1295 bench route: make one grid's vanilla <c>MyCubeGrid.BeforeDelete</c> throw, the way partition 651's grids
/// did on octahedron-1191 when a cluster stop's drain closed them. A Harmony prefix installed on first arm, on this
/// test plugin only; inert while nothing is armed. It throws the same NullReferenceException type before the
/// vanilla body runs, so the product's GridDeleteFaultGuard (a finalizer) sees what it saw live.
/// </summary>
internal static class GridDeleteFaultRoutes
{
    private static long m_grid;
    private static long m_thrown;
    private static bool m_patched;

    public static void Register()
    {
        // POST /test-grid-delete-fault?entity= - arm: the next BeforeDelete of grid `entity` throws.
        TestRoutes.Add("POST", "/test-grid-delete-fault", context =>
        {
            long grid = TestRoutes.Long(context, "entity");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!(MyEntities.GetEntityById(grid) is MyCubeGrid)) throw new InvalidOperationException($"No grid {grid} on this node");
                EnsurePatched();
                Interlocked.Exchange(ref m_grid, grid);
                Plugin.Log($"grid-delete-fault armed grid={grid} (#1295)");
                return (object)new { armed = true, grid };
            }));
        });
        // GET /test-grid-delete-fault - how many faults were thrown.
        TestRoutes.Add("GET", "/test-grid-delete-fault", context =>
            TestRoutes.Json(context, 200, new { grid = Interlocked.Read(ref m_grid), thrown = Interlocked.Read(ref m_thrown) }));
    }

    private static void EnsurePatched()
    {
        if (m_patched) return;
        var original = AccessTools.Method(typeof(MyCubeGrid), "BeforeDelete")
            ?? throw new MissingMethodException("MyCubeGrid.BeforeDelete");
        new Harmony("ClusterTestPlugin.GridDeleteFault").Patch(original,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GridDeleteFaultRoutes), nameof(BeforeDeletePrefix))));
        m_patched = true;
    }

    private static void BeforeDeletePrefix(MyCubeGrid __instance)
    {
        long grid = Interlocked.Read(ref m_grid);
        if (grid == 0 || __instance.EntityId != grid || Interlocked.CompareExchange(ref m_grid, 0, grid) != grid) return;
        Interlocked.Increment(ref m_thrown);
        throw new NullReferenceException($"test fault in MyCubeGrid.BeforeDelete of grid {grid} (#1295)");
    }
}
