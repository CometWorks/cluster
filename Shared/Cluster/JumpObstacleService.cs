using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1871: a jump drive does not land a ship inside a grid that another node hosts or that sleeps offline.
///
/// When a jump's charge runs out, the server's <c>MyGridJumpDriveSystem.FindSuitableJumpLocation</c> moves the
/// arrival off every entity in a box around the target (the ship's group bounds plus 1 km, times eleven), each
/// entity's bounds inflated by that group box's half size. It reads them from the node's own pruning structure, so
/// on a cluster it saw only what the jumping node hosts: a jump onto a station on another node, or in an offline
/// partition, ended inside it.
///
/// The charge takes 10 s. When it starts (<c>Jump</c>, on the server), this node asks every node over the plugin
/// bus for the bounds of the entities it owns in that box, and the World Authority for the offline members in it
/// (only the authority keeps a copy of the offline table). When the charge ends, those bounds go into vanilla's
/// obstacle list, inflated the vanilla way, and vanilla picks the arrival as on a single server. A role that does
/// not answer in time is logged and left out.
/// A charge restored from a save or a handover (no <c>Jump</c> call on this node) asks nothing: vanilla alone.
/// </summary>
internal static class JumpObstacleService
{
    private const string Plugin = "cluster-node";
    private const string Topic = "jump-obstacles";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static bool m_registered;
    private static IDisposable m_handler;

    private sealed class Query
    {
        public BoundingBoxD Box;
        public volatile List<BoundingBoxD> Remote;
        public volatile string Missing;
    }

    private static readonly ConcurrentDictionary<long, Query> Queries = new();

    private static readonly AccessTools.FieldRef<MyGridJumpDriveSystem, Vector3D> SelectedDestination =
        AccessTools.FieldRefAccess<MyGridJumpDriveSystem, Vector3D>("m_selectedDestination");

    private static readonly AccessTools.FieldRef<
        MyGridJumpDriveSystem,
        List<BoundingBoxD>
    > ObstaclesInRange = AccessTools.FieldRefAccess<MyGridJumpDriveSystem, List<BoundingBoxD>>(
        "m_obstaclesInRange"
    );

    private static readonly Func<MyGridJumpDriveSystem, MyCubeGrid> GridOf = AccessTools.MethodDelegate<
        Func<MyGridJumpDriveSystem, MyCubeGrid>
    >(AccessTools.PropertyGetter(typeof(MyGridJumpDriveSystem), "Grid"));

    private static bool Active =>
        ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && Sync.IsServer;

    public static void Update()
    {
        if (
            m_registered
            || !ClusterRuntime.IsClustered
            || Sandbox.Game.World.MySession.Static?.Ready != true
            || PluginServices.Instance == null
        )
            return;
        m_registered = true;
        m_handler = PluginServices.Instance.RegisterHandler(Plugin, Topic, OnQuery);
    }

    /// <summary>Vanilla's search box around the target (FindSuitableJumpLocation's first lines).</summary>
    internal static BoundingBoxD SearchBox(BoundingBoxD group, Vector3D destination)
    {
        group.Inflate(1000.0);
        BoundingBoxD box = group.GetInflated(group.HalfExtents * 10.0);
        box.Translate(destination - box.Center);
        return box;
    }

    /// <summary>Vanilla's inflation of one obstacle: by the half size of the ship's group box plus 1 km.</summary>
    internal static BoundingBoxD Inflated(BoundingBoxD obstacle, BoundingBoxD group)
    {
        group.Inflate(1000.0);
        return obstacle.GetInflated(group.HalfExtents);
    }

    // Game thread, the jumping node: the charge starts
    private static void Started(MyGridJumpDriveSystem system)
    {
        MyCubeGrid grid = GridOf(system);
        if (grid == null || grid.MarkedForClose)
            return;
        var query = new Query
        {
            Box = SearchBox(grid.GetPhysicalGroupAABB(), SelectedDestination(system)),
        };
        Queries[grid.EntityId] = query;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new[]
            {
                query.Box.Min.X,
                query.Box.Min.Y,
                query.Box.Min.Z,
                query.Box.Max.X,
                query.Box.Max.Y,
                query.Box.Max.Z,
            }
        );
        Task.Run(async () =>
        {
            try
            {
                PluginBroadcastResult result = await PluginServices
                    .Instance.BroadcastAsync(
                        Plugin,
                        Topic,
                        payload,
                        Guid.NewGuid(),
                        true,
                        Timeout,
                        default
                    )
                    .ConfigureAwait(false);
                var boxes = new List<BoundingBoxD>();
                var missing = new List<string>();
                foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
                {
                    if (answer.Value.Code != PluginResultCode.Success)
                    {
                        missing.Add(answer.Key);
                        continue;
                    }
                    double[][] rows = JsonSerializer.Deserialize<double[][]>(
                        answer.Value.Payload ?? Encoding.UTF8.GetBytes("[]")
                    );
                    foreach (double[] row in rows)
                        boxes.Add(
                            new BoundingBoxD(
                                new Vector3D(row[0], row[1], row[2]),
                                new Vector3D(row[3], row[4], row[5])
                            )
                        );
                }
                if (result.Nodes == null)
                    missing.Add(result.Code.ToString());
                query.Missing = missing.Count == 0 ? null : string.Join(",", missing);
                query.Remote = boxes;
            }
            catch (Exception exception)
            {
                query.Missing = exception.GetType().Name;
                query.Remote = new List<BoundingBoxD>();
            }
        });
    }

    // Game thread, the jumping node: vanilla is about to pick the arrival
    private static void Arriving(MyGridJumpDriveSystem system, Vector3D desired)
    {
        // The request also asks for a location, before the charge starts: only the charge's end takes the answers
        MyCubeGrid grid = GridOf(system);
        if (!system.IsJumping || grid == null || !Queries.TryRemove(grid.EntityId, out Query query))
            return;
        BoundingBoxD group = grid.GetPhysicalGroupAABB();
        List<BoundingBoxD> obstacles = ObstaclesInRange(system);
        List<BoundingBoxD> remote = query.Remote;
        if (remote != null)
            foreach (BoundingBoxD box in remote)
                obstacles.Add(Inflated(box, group));
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "[ClusterPlugin] jump obstacles grid={0} target=({1:F0},{2:F0},{3:F0}) answered={4}{5}",
                grid.EntityId,
                desired.X,
                desired.Y,
                desired.Z,
                remote == null ? "late" : remote.Count.ToString(CultureInfo.InvariantCulture),
                query.Missing == null ? "" : " unanswered=" + query.Missing
            )
        );
    }

    private static double[] Row(BoundingBoxD bounds) =>
        new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z };

    // Game thread, every node: the bounds of what this node owns in the box; the World Authority: the offline members
    private static Task<byte[]> OnQuery(PluginMessage message)
    {
        var rows = new List<double[]>();
        double[] box = JsonSerializer.Deserialize<double[]>(message.Payload ?? Encoding.UTF8.GetBytes("[]"));
        if (box is { Length: 6 })
        {
            var search = new BoundingBoxD(
                new Vector3D(box[0], box[1], box[2]),
                new Vector3D(box[3], box[4], box[5])
            );
            if (ClusterRuntime.IsWorldAuthority)
            {
                foreach (BoundingBoxD member in OfflinePartitionService.MemberBoxes(search))
                    rows.Add(Row(member));
                return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(rows));
            }
            var found = new List<MyEntity>();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref search, found);
            foreach (MyEntity entity in found)
            {
                // Voxels and safe zones are on every node, so the jumping node has them already
                if (
                    entity is MyVoxelBase
                    || entity.MarkedForClose
                    || !AdminMenuService.OwnedHere(entity.EntityId)
                )
                    continue;
                rows.Add(Row(entity.PositionComp.WorldAABB));
            }
        }
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(rows));
    }

    [HarmonyPatch(typeof(MyGridJumpDriveSystem), "Jump")]
    private static class JumpPatch
    {
        private static void Postfix(MyGridJumpDriveSystem __instance)
        {
            if (!Active || !m_registered)
                return;
            try
            {
                Started(__instance);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[ClusterPlugin] jump obstacle query failed: " + exception);
            }
        }
    }

    [HarmonyPatch(typeof(MyGridJumpDriveSystem), "FindSuitableJumpLocation")]
    private static class FindSuitableJumpLocationPatch
    {
        private static void Prefix(MyGridJumpDriveSystem __instance, Vector3D desiredLocation)
        {
            if (!Active)
                return;
            try
            {
                Arriving(__instance, desiredLocation);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[ClusterPlugin] jump obstacles not added: " + exception);
            }
        }
    }
}
