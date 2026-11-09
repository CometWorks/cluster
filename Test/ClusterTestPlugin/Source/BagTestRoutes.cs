using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>SE-1313 bench routes: inventory bags, the entities whose parallel staged builds corrupted
/// the game's static entity-event registry.</summary>
internal static class BagTestRoutes
{
    public static void Register()
    {
        // POST /bag-test-spawn?x=&y=&z=&count=[&spacing=m] - astronaut backpacks in a row along +x, the
        // way vanilla spawns them (MyEntityInventorySpawnComponent), one iron ingot each, at rest
        TestRoutes.Add(
            "POST",
            "/bag-test-spawn",
            context =>
            {
                Vector3D origin = new Vector3D(
                    TestRoutes.Double(context, "x"),
                    TestRoutes.Double(context, "y"),
                    TestRoutes.Double(context, "z")
                );
                int count = int.Parse(
                    TestRoutes.Query(context, "count"),
                    CultureInfo.InvariantCulture
                );
                double spacing = double.Parse(
                    TestRoutes.Query(context, "spacing", "3"),
                    CultureInfo.InvariantCulture
                );
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() => Spawn(origin, count, spacing))
                );
            }
        );
        // GET /bag-test-state?ids=a,b,... - the given bags that are live on this node
        TestRoutes.Add(
            "GET",
            "/bag-test-state",
            context =>
            {
                long[] ids = TestRoutes
                    .Query(context, "ids")
                    .Split(',')
                    .Select(x => long.Parse(x, CultureInfo.InvariantCulture))
                    .ToArray();
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread(() =>
                        new
                        {
                            present = ids.Where(id =>
                                    MyEntities.TryGetEntityById(id, out MyEntity bag)
                                    && bag is MyInventoryBagEntity
                                    && !bag.MarkedForClose
                                )
                                .ToArray(),
                        }
                    )
                );
            }
        );
    }

    private static object Spawn(Vector3D origin, int count, double spacing)
    {
        var definition = new MyDefinitionId(
            typeof(MyObjectBuilder_InventoryBagEntity),
            "AstronautBackpack"
        );
        var ids = new List<long>();
        for (int i = 0; i < count; i++)
        {
            MyEntity bag = MyEntities.CreateFromComponentContainerDefinitionAndAdd(
                definition,
                fadeIn: false
            );
            if (bag == null)
                continue;
            MatrixD matrix = MatrixD.CreateTranslation(origin + new Vector3D(i * spacing, 0, 0));
            bag.PositionComp.SetWorldMatrix(ref matrix);
            bag.GetInventory()
                ?.AddItems(
                    (MyFixedPoint)1,
                    MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Iron")
                );
            if (bag.Physics != null)
            {
                bag.Physics.LinearVelocity = Vector3.Zero;
                bag.Physics.AngularVelocity = Vector3.Zero;
            }
            ids.Add(bag.EntityId);
        }
        return new { bags = ids };
    }
}
