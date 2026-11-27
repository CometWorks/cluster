using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>SE-1998 (P2-AST-TURN): the pose of an asteroid and of an entity on this node, and the turns it holds.</summary>
internal static class AsteroidTurnRoutes
{
    public static void Register()
    {
        // GET /asteroid-turn-test-pose?storage=NAME
        TestRoutes.Add(
            "GET",
            "/asteroid-turn-test-pose",
            context =>
            {
                string storage =
                    context.Request.QueryString["storage"] ?? throw new ArgumentException("Missing storage");
                object result = TestRoutes.OnGameThread<object>(() =>
                {
                    MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage);
                    return new
                    {
                        found = voxel != null,
                        matrix = voxel == null ? null : Pose(voxel.WorldMatrix),
                        turns = AccessTools
                            .Method(AccessTools.TypeByName("ClusterPlugin.AsteroidTurnService"), "Describe")
                            .Invoke(null, null),
                    };
                });
                TestRoutes.Json(context, 200, result);
            }
        );
        // GET /asteroid-turn-test-entity?entity=ID: the entity's world matrix on this node, if it is here
        TestRoutes.Add(
            "GET",
            "/asteroid-turn-test-entity",
            context =>
            {
                long id = (long)TestRoutes.Double(context, "entity");
                object result = TestRoutes.OnGameThread<object>(() =>
                    MyEntities.TryGetEntityById(id, out MyEntity entity) && !entity.MarkedForClose
                        ? new { found = true, matrix = Pose(entity.WorldMatrix) }
                        : new { found = false, matrix = (double[])null }
                );
                TestRoutes.Json(context, 200, result);
            }
        );
    }

    /// <summary>Translation, Forward and Up: 9 numbers.</summary>
    private static double[] Pose(MatrixD m) =>
        [
            m.Translation.X,
            m.Translation.Y,
            m.Translation.Z,
            m.Forward.X,
            m.Forward.Y,
            m.Forward.Z,
            m.Up.X,
            m.Up.Y,
            m.Up.Z,
        ];
}
