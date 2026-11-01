using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage.Game.Entity;
using VRage.Network;
using VRage.Voxels;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// T-0368 bench routes: a modified asteroid in a client's view across a handover. The existing
/// /voxel-test-cutout refuses a node with players and /voxel-test-spawn-asteroid makes an asteroid whose
/// entity id differs per node, so the bench works on a PROCEDURAL asteroid (same id on every node, as live).
/// </summary>
internal static class VoxelCarryRoutes
{
    /// <summary>Keeps the tracking stand-ins alive: the generator holds them, nothing else does.</summary>
    private static readonly List<MyEntity> Trackers = new List<MyEntity>();

    public static void Register()
    {
        // GET /voxel-test-asteroids?x=&y=&z=&radius= - the asteroids (not planets) loaded on this node within
        // radius of the point, nearest first, each with the replication facts the bench asserts on: the network id
        // this node addresses the replicable by, and which clients hold it (pending / streaming).
        TestRoutes.Add("GET", "/voxel-test-asteroids", context =>
        {
            Vector3D point = Point(context);
            double radius = double.Parse(TestRoutes.Query(context, "radius", "6000"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                MyReplicationServer server = MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer;
                return (object)new
                {
                    asteroids = MyEntities.GetEntities().OfType<MyVoxelMap>()
                        .Where(map => !map.Closed && map.Storage != null)
                        .Select(map => new { map, center = map.PositionComp.WorldAABB.Center })
                        .Select(row => new { row.map, row.center, distance = Vector3D.Distance(row.center, point) })
                        .Where(row => row.distance <= radius)
                        .OrderBy(row => row.distance)
                        .Select(row => Describe(server, row.map, row.center, row.distance)).ToArray(),
                };
            }));
        });
        // POST /voxel-test-track-point?x=&y=&z=&radius= - have the vanilla procedural generator keep the cells
        // around a point generated, without an entity in the world (nothing joins a partition, nothing replicates).
        // The target of a handover has nobody near the client's asteroid, so it would not hold it at adopt time.
        TestRoutes.Add("POST", "/voxel-test-track-point", context =>
        {
            Vector3D point = Point(context);
            double radius = double.Parse(TestRoutes.Query(context, "radius", "1000"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                MyProceduralWorldGenerator generator = MyProceduralWorldGenerator.Static
                    ?? throw new InvalidOperationException("No procedural generator (ProceduralDensity 0?)");
                MyEntity standIn = new MyEntity();
                standIn.PositionComp.LocalAABB = new BoundingBox(-Vector3.One, Vector3.One);
                standIn.PositionComp.SetPosition(point);
                generator.TrackEntity(standIn, radius);
                Trackers.Add(standIn);
                Vector3D center = standIn.PositionComp.WorldAABB.Center;
                return (object)new { tracked = Trackers.Count, radius, center = new[] { center.X, center.Y, center.Z } };
            }));
        });
        // POST /voxel-test-untrack-points - T-0367: release every stand-in of /voxel-test-track-point. The vanilla
        // generator marks their cells dirty and, on its next update, closes the asteroids of every cell no other
        // tracker reaches - but only those with Save=false (MyProceduralAsteroidCellGenerator.CloseObjectSeed).
        TestRoutes.Add("POST", "/voxel-test-untrack-points", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                MyProceduralWorldGenerator generator = MyProceduralWorldGenerator.Static
                    ?? throw new InvalidOperationException("No procedural generator (ProceduralDensity 0?)");
                int released = Trackers.Count;
                foreach (MyEntity standIn in Trackers) generator.RemoveTrackedEntity(standIn);
                Trackers.Clear();
                return (object)new { released, tracked = Trackers.Count };
            }));
        });
        // POST /voxel-test-revert-settings?entity= - T-0367: what the vanilla voxel trash revert leaves on a
        // procedural asteroid (MySessionComponentTrash -> RevertProceduralAsteroidVoxelSettings): Save=false again,
        // so the generator unloads it with its cell and generates it anew - a pristine instance under the same id.
        // The storage content is NOT reverted here: the instance is about to be closed.
        TestRoutes.Add("POST", "/voxel-test-revert-settings", context =>
        {
            long entityId = TestRoutes.Long(context, "entity");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyVoxelMap voxel || voxel.Storage == null)
                    throw new ArgumentException("Unknown asteroid " + entityId);
                bool before = voxel.Save;
                voxel.RevertProceduralAsteroidVoxelSettings();
                return (object)new { entity = entityId, storage = voxel.StorageName, saveBefore = before, save = voxel.Save };
            }));
        });
        // POST /voxel-test-cutout-live?entity=&x=&y=&z=&radius= - an explosion's cut (RequestVoxelCutoutSphere:
        // executed on this node, broadcast to its clients, journalled by the cluster's postfix and by the storage's
        // range-changed notification) at the first solid voxel on the line from the point to the asteroid's centre.
        // Unlike /voxel-test-cutout it runs with players online: the client holding the replica is the test.
        // dry=1 only reports where the cut would land, so the bench can hash the pristine range first.
        TestRoutes.Add("POST", "/voxel-test-cutout-live", context =>
        {
            long entityId = TestRoutes.Long(context, "entity");
            Vector3D from = Point(context);
            float radius = float.Parse(TestRoutes.Query(context, "radius", "3"), CultureInfo.InvariantCulture);
            if (!float.IsFinite(radius) || radius <= 0 || radius > 32) throw new ArgumentOutOfRangeException(nameof(radius));
            bool dry = TestRoutes.Query(context, "dry", "0") == "1";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyVoxelMap voxel || voxel.Storage == null)
                    throw new ArgumentException("Unknown asteroid " + entityId);
                Vector3D target = voxel.PositionComp.WorldAABB.Center;
                Vector3D direction = target - from;
                double length = direction.Length();
                if (length < 1) throw new ArgumentException("The point is the asteroid's centre");
                direction /= length;
                MyStorageData data = new MyStorageData(MyStorageDataTypeFlags.Content);
                for (double step = 0; step <= length; step += 1)
                {
                    Vector3D world = from + direction * step;
                    Vector3I coord = Vector3I.Floor(world - voxel.PositionLeftBottomCorner);
                    if (coord.X < 0 || coord.Y < 0 || coord.Z < 0
                        || coord.X >= voxel.Storage.Size.X || coord.Y >= voxel.Storage.Size.Y || coord.Z >= voxel.Storage.Size.Z) continue;
                    data.Resize(coord, coord);
                    voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.Content, 0, coord, coord);
                    byte content = data[MyStorageDataTypeEnum.Content][0];
                    if (content < 128) continue;
                    if (!dry)
                    {
                        // As MyExplosion.ApplyExplosionOnVoxel: the server cuts its own storage, the request only broadcasts.
                        Sandbox.Game.MyExplosion.CutOutVoxelMap(radius, world, voxel.RootVoxel, createDebris: false);
                        voxel.RootVoxel.RequestVoxelCutoutSphere(world, radius, createDebris: false, damage: false);
                    }
                    return (object)new
                    {
                        entity = entityId, storage = voxel.StorageName, requested = !dry, radius, content,
                        center = new[] { world.X, world.Y, world.Z }, voxel = new[] { coord.X, coord.Y, coord.Z },
                        players = MySession.Static.Players.GetOnlinePlayerCount(),
                    };
                }
                throw new InvalidOperationException("No solid voxel between the point and the centre of " + voxel.StorageName);
            }));
        });
    }

    private static Vector3D Point(System.Net.HttpListenerContext context) =>
        new Vector3D(TestRoutes.Double(context, "x"), TestRoutes.Double(context, "y"), TestRoutes.Double(context, "z"));

    private static object Describe(MyReplicationServer server, MyVoxelMap map, Vector3D center, double distance)
    {
        IMyReplicable replicable = MyExternalReplicable.FindByObject(map);
        uint networkId = 0;
        var clients = new List<object>();
        if (server != null && replicable != null)
        {
            // An unregistered replicable has no id; the lookup throws for it, and that is "0" here.
            try { networkId = server.GetNetworkIdByObject(replicable).Value; }
            catch (Exception) { networkId = 0; }
            foreach (var client in server.m_clientStates.Values)
                if (client.Replicables.TryGetValue(replicable, out var held))
                    clients.Add(new { client = client.State.EndpointId.Id.Value, pending = held.IsPending, streaming = held.IsStreaming });
        }
        return new
        {
            entityId = map.EntityId, storage = map.StorageName, distance,
            center = new[] { center.X, center.Y, center.Z },
            halfExtent = map.PositionComp.WorldAABB.HalfExtents.Length(),
            save = map.Save, contentChanged = map.ContentChanged, beforeContentChanged = map.BeforeContentChanged,
            replicable = replicable != null, networkId, clients,
        };
    }
}
