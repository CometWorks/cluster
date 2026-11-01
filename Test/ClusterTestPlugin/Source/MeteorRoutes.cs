using System.Globalization;
using ClusterPlugin;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>#1381 bench routes: a meteor probe and its lifetime state (P2-METEOR-LIFETIME).</summary>
internal static class MeteorRoutes
{
    public static void Register()
    {
        // POST /meteor-test-spawn?x=&y=&z=[&speed=m/s along +x] - a meteor from a builder, as vanilla's PrepareBuilder makes it
        TestRoutes.Add("POST", "/meteor-test-spawn", context =>
        {
            Vector3D position = new Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));
            float speed = float.Parse(TestRoutes.Query(context, "speed", "0"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Spawn(position, speed)));
        });
        // GET /meteor-test-state?entityId= - exists, the vanilla age (game ms since m_timeCreated) and the carried birth
        TestRoutes.Add("GET", "/meteor-test-state", context =>
        {
            long entityId = TestRoutes.Long(context, "entityId");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => State(entityId)));
        });
    }

    private static object Spawn(Vector3D position, float speed)
    {
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Meteor>();
        builder.Item = new MyPhysicalInventoryItem((MyFixedPoint)500, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Stone")).GetObjectBuilder();
        builder.Integrity = 1;
        builder.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
        builder.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3.Forward, Vector3.Up);
        builder.LinearVelocity = new Vector3(speed, 0, 0);
        MyEntity meteor = MyEntities.CreateFromObjectBuilderAndAdd(builder, false);
        return new { entityId = meteor?.EntityId ?? 0, meteor = meteor is MyMeteor };
    }

    private static object State(long entityId)
    {
        if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyMeteor meteor || meteor.MarkedForClose)
            return new { exists = false };
        var logic = (MyMeteor.MyMeteorGameLogic)meteor.GameLogic;
        return new
        {
            exists = true,
            ageMs = MySandboxGame.TotalGamePlayTimeInMilliseconds - logic.m_timeCreated,
            lifetimeMs = MeteorLifetime.LifetimeMilliseconds(meteor.Physics?.LinearVelocity.Length() ?? 0),
            birth = MeteorLifetime.Birth(meteor)
        };
    }
}
