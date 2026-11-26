using System;
using System.Threading;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders.Private;

namespace ClusterTestPlugin;

/// <summary>
/// #1295 bench route: make one grid's vanilla <c>MyCubeGrid.BeforeDelete</c> throw, the way partition 651's grids
/// did on octahedron-1191 when a cluster stop's drain closed them. A Harmony prefix installed on first arm, on this
/// test plugin only; inert while nothing is armed. It throws the same NullReferenceException type before the
/// vanilla body runs, so the product's GridDeleteFaultGuard (a finalizer) sees what it saw live.
///
/// SE-1964: at=remove throws from a MyEntities.OnEntityRemove handler instead, inside BeforeDelete's
/// MyEntities.Remove, where the live faults threw: the grid systems' unregister after it is skipped. gyro=true
/// adds a gyro first, whose grid system then threw in MyEntity.Delete's RaisePhysicsChanged and ended the node.
/// </summary>
internal static class GridDeleteFaultRoutes
{
    private static long m_grid;
    private static long m_thrown;
    private static bool m_atRemove;
    private static bool m_patched;

    public static void Register()
    {
        // POST /test-grid-delete-fault?entity=[&at=remove][&gyro=true] - arm: the next BeforeDelete of grid `entity`
        // throws, at its start or (at=remove) in its MyEntities.Remove; gyro=true adds a gyro to the grid first.
        TestRoutes.Add(
            "POST",
            "/test-grid-delete-fault",
            context =>
            {
                long grid = TestRoutes.Long(context, "entity");
                bool atRemove = TestRoutes.Query(context, "at", "start") == "remove";
                bool gyro = TestRoutes.Query(context, "gyro", "false") == "true";
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                    {
                        if (!(MyEntities.GetEntityById(grid) is MyCubeGrid target))
                            throw new InvalidOperationException($"No grid {grid} on this node");
                        if (gyro)
                            AddGyro(target);
                        EnsurePatched();
                        m_atRemove = atRemove;
                        Interlocked.Exchange(ref m_grid, grid);
                        Plugin.Log(
                            $"grid-delete-fault armed grid={grid} at={(atRemove ? "remove" : "start")} gyros={target.GridSystems.GyroSystem.GyroCount} (#1295)"
                        );
                        return (object)
                            new
                            {
                                armed = true,
                                grid,
                                gyros = target.GridSystems.GyroSystem.GyroCount,
                            };
                    })
                );
            }
        );
        // GET /test-grid-delete-fault - how many faults were thrown.
        TestRoutes.Add(
            "GET",
            "/test-grid-delete-fault",
            context =>
                TestRoutes.Json(
                    context,
                    200,
                    new
                    {
                        grid = Interlocked.Read(ref m_grid),
                        thrown = Interlocked.Read(ref m_thrown),
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
        new Harmony("ClusterTestPlugin.GridDeleteFault").Patch(
            original,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(GridDeleteFaultRoutes), nameof(BeforeDeletePrefix))
            )
        );
        MyEntities.OnEntityRemove += OnEntityRemove;
        m_patched = true;
    }

    private static void AddGyro(MyCubeGrid grid)
    {
        var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>(
            grid.GridSizeEnum == MyCubeSize.Large ? "LargeBlockGyro" : "SmallBlockGyro"
        );
        gyro.Min = new SerializableVector3I(grid.Min.X, grid.Min.Y, grid.Max.Z + 2);
        if (grid.AddBlock(gyro, false) == null)
            throw new InvalidOperationException($"Could not add a gyro to grid {grid.EntityId}");
    }

    private static void BeforeDeletePrefix(MyCubeGrid __instance)
    {
        if (!m_atRemove)
            Throw(__instance, "MyCubeGrid.BeforeDelete");
    }

    private static void OnEntityRemove(MyEntity entity)
    {
        if (m_atRemove)
            Throw(entity, "MyEntities.Remove");
    }

    private static void Throw(MyEntity entity, string where)
    {
        long grid = Interlocked.Read(ref m_grid);
        if (
            grid == 0
            || entity.EntityId != grid
            || Interlocked.CompareExchange(ref m_grid, 0, grid) != grid
        )
            return;
        Interlocked.Increment(ref m_thrown);
        throw new NullReferenceException($"test fault in {where} of grid {grid} (#1295)");
    }
}
