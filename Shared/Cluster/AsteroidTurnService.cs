using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1998: asteroids turned to the identity orientation one at a time while the cluster runs (Docs/AsteroidTurns.md).
/// <list type="bullet">
/// <item>The World Authority proposes the next rotated asteroid every <c>asteroidReset.intervalSeconds</c> when no online
/// player is within <c>asteroidReset.playerMeters</c>; the Gateway commits the turn only while no online partition is
/// near the rock and pins the saves of the offline ones that are (ClusterRegistry/AsteroidTurns.cs).</item>
/// <item>Every node, the WA included, turns its own copy of each committed rock about its center
/// (<c>PositionComp.SetWorldMatrix</c>: physics, pruning and render follow) and marks the clients' replicas stale.</item>
/// <item>A partition loaded from a pinned save gets its members near the rock turned with it before any entity exists
/// (<see cref="TurnBuilders"/>, from PartitionPersistence.LoadRecovery).</item>
/// </list>
/// Nothing rewrites a save; the premade world keeps the old poses and every node start turns them again.
/// </summary>
internal static class AsteroidTurnService
{
    private const string Subsystem = "asteroid-turns";
    private static readonly HttpClient Http = ClusterHttp.Client(TimeSpan.FromSeconds(10));
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, AsteroidTurnRow> m_rows = new(
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> m_applied = new(StringComparer.Ordinal);

    /// <summary>Entities a load already turned, per rock: the late-member pass leaves them alone.</summary>
    private static readonly Dictionary<string, HashSet<long>> m_turnedAtLoad = new(
        StringComparer.Ordinal
    );
    private static bool m_loaded;
    private static bool m_fetching;
    private static bool m_authorized;
    private static double m_nextPoll;

    // World Authority
    private static double m_nextPropose;
    private static double m_nextStatus;
    private static bool m_proposing;
    private static string m_cursor = string.Empty;
    private static readonly Dictionary<string, string> m_waiting = new(StringComparer.Ordinal);
    private static string m_lastRefusal;
    private static int m_lateMembers;
    private static int m_frozenMembers;

    private static bool Enabled =>
        ClusterRuntime.IsWorldAuthority && ClusterConfig.GetBool("asteroidReset.enabled");

    private static string Gateway =>
        Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');

    public static void Install()
    {
        GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceivePublished);
    }

    private static void ReceivePublished(string payload)
    {
        try
        {
            if (JsonSerializer.Deserialize<AsteroidTurnRow>(payload, Json) is { } row)
                Merge([row]);
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                "[ClusterPlugin] asteroid_turn publish_unreadable=true error=" + exception.Message
            );
        }
    }

    private static void Merge(IEnumerable<AsteroidTurnRow> rows)
    {
        lock (Sync)
            foreach (AsteroidTurnRow row in rows)
                m_rows.TryAdd(row.Storage, row);
    }

    /// <summary>Game thread, every frame.</summary>
    public static void Update()
    {
        if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true)
            return;
        double now = ClusterRuntime.LocalMilliseconds;
        if (now >= m_nextPoll)
        {
            m_nextPoll =
                now + 1000.0 * Math.Max(1, ClusterConfig.GetInt("asteroidReset.pollSeconds"));
            Fetch(blocking: false);
        }
        ApplyPending();
        if (!Enabled)
            return;
        if (now >= m_nextPropose)
        {
            m_nextPropose =
                now + 1000.0 * Math.Max(1, ClusterConfig.GetInt("asteroidReset.intervalSeconds"));
            Propose();
        }
        if (now >= m_nextStatus)
        {
            m_nextStatus =
                now + 1000.0 * Math.Max(1, ClusterConfig.GetInt("asteroidReset.statusSeconds"));
            Status();
        }
    }

    /// <summary>
    /// Game thread, before a recovery creates any entity: the node holds every committed turn and has turned its rocks.
    /// A node never simulates a turned member next to a rock that isn't turned yet.
    /// </summary>
    public static void EnsureReady(IEnumerable<AsteroidTurnRow> claimed)
    {
        if (claimed != null)
            Merge(claimed);
        if (!m_loaded)
            Fetch(blocking: true);
        ApplyPending();
    }

    private static void Fetch(bool blocking)
    {
        string gateway = Gateway;
        if (string.IsNullOrWhiteSpace(gateway))
            return;
        lock (Sync)
        {
            if (m_fetching && !blocking)
                return;
            m_fetching = true;
        }
        void Run()
        {
            try
            {
                if (!m_authorized)
                {
                    ClusterRuntime.AuthorizeRegistryClient(Http);
                    m_authorized = true;
                }
                int known;
                lock (Sync)
                    known = m_rows.Count;
                using HttpResponseMessage response = Http.GetAsync(
                        gateway
                            + "/registry/asteroid-turns?known="
                            + known.ToString(CultureInfo.InvariantCulture)
                    )
                    .GetAwaiter()
                    .GetResult();
                // A Gateway from before SE-1998: no turns.
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                    AsteroidTurnList list = JsonSerializer.Deserialize<AsteroidTurnList>(
                        response.Content.ReadAsStream(),
                        Json
                    );
                    Merge(list?.Rows ?? []);
                }
                m_loaded = true;
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[ClusterPlugin] asteroid_turn fetch_failed=true error=" + exception.Message
                );
                if (blocking)
                    throw;
            }
            finally
            {
                lock (Sync)
                    m_fetching = false;
            }
        }
        if (blocking)
            Run();
        else
            ThreadPool.QueueUserWorkItem(_ => Run());
    }

    internal sealed record AsteroidTurnList(int Count, AsteroidTurnRow[] Rows);

    private static MatrixD Turn(AsteroidTurnRow row)
    {
        var pivot = new Vector3D(row.Pivot[0], row.Pivot[1], row.Pivot[2]);
        // Row vectors: v' = v * M, so the old axes are M's columns and a point's rock-local coordinates become world ones.
        var rotation = new MatrixD(
            row.Right[0],
            row.Up[0],
            row.Backward[0],
            0,
            row.Right[1],
            row.Up[1],
            row.Backward[1],
            0,
            row.Right[2],
            row.Up[2],
            row.Backward[2],
            0,
            0,
            0,
            0,
            1
        );
        return MatrixD.CreateTranslation(-pivot) * rotation * MatrixD.CreateTranslation(pivot);
    }

    private static void ApplyPending()
    {
        AsteroidTurnRow[] pending;
        lock (Sync)
            pending = m_rows.Values.Where(r => !m_applied.Contains(r.Storage)).ToArray();
        foreach (AsteroidTurnRow row in pending)
            Apply(row);
    }

    private static void Apply(AsteroidTurnRow row)
    {
        MyVoxelBase voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(row.Storage);
        if (voxel == null || voxel.Closed)
            return; // not in this world (yet): tried again next frame
        MatrixD current = voxel.WorldMatrix;
        var pivot = new Vector3D(row.Pivot[0], row.Pivot[1], row.Pivot[2]);
        if (
            AsteroidTurnPolicy.Aligned(
                [current.Right.X, current.Right.Y, current.Right.Z],
                [current.Up.X, current.Up.Y, current.Up.Z],
                [current.Backward.X, current.Backward.Y, current.Backward.Z]
            )
        )
        {
            lock (Sync)
                m_applied.Add(row.Storage);
            return;
        }
        if (
            Vector3D.Distance(current.Translation, pivot) > 0.01
            || Vector3D.Distance(
                current.Right,
                new Vector3D(row.Right[0], row.Right[1], row.Right[2])
            ) > 1e-4
            || Vector3D.Distance(current.Up, new Vector3D(row.Up[0], row.Up[1], row.Up[2])) > 1e-4
        )
        {
            lock (Sync)
                m_applied.Add(row.Storage);
            Console.WriteLine(
                "[ClusterPlugin] asteroid_turn error=pose_mismatch storage="
                    + row.Storage
                    + " pose="
                    + current.Translation
                    + " expected="
                    + pivot
            );
            return;
        }
        MatrixD turn = Turn(row);
        int late = TurnLateMembers(row, turn);
        MatrixD turned = MatrixD.CreateWorld(pivot, Vector3D.Forward, Vector3D.Up);
        voxel.PositionComp.SetWorldMatrix(ref turned, null, false, true, true, true);
        ReplicationClientViewPatch.MarkVoxelStale(voxel);
        lock (Sync)
            m_applied.Add(row.Storage);
        VRage.Utils.MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] asteroid_turn applied storage="
                + row.Storage
                + " late_members="
                + late.ToString(CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// Members already here near the rock that no load turned. By the Gateway's rule there are none: this covers a
    /// partition this node loaded while the turn was still on its way. Each is turned with its physical group.
    /// </summary>
    private static int TurnLateMembers(AsteroidTurnRow row, MatrixD turn)
    {
        double reach =
            Math.Sqrt(row.Half.Sum(x => x * x))
            + ClusterConfig.GetInt("asteroidReset.memberMeters");
        var sphere = new BoundingSphereD(
            new Vector3D(row.Pivot[0], row.Pivot[1], row.Pivot[2]),
            reach
        );
        HashSet<long> skip;
        lock (Sync)
            skip = m_turnedAtLoad.TryGetValue(row.Storage, out HashSet<long> ids)
                ? ids
                : new HashSet<long>();
        var moved = new HashSet<long>();
        int count = 0;
        foreach (MyEntity entity in MyEntities.GetTopMostEntitiesInSphere(ref sphere))
        {
            if (
                !entity.Save
                || entity.MarkedForClose
                || entity
                    is not (
                        MyCubeGrid
                        or MyCharacter
                        or MyFloatingObject
                        or MyInventoryBagEntity
                        or MyMeteor
                    )
                || skip.Contains(entity.EntityId)
                || moved.Contains(entity.EntityId)
            )
                continue;
            BoundingSphereD volume = entity.PositionComp.WorldVolume;
            if (
                AsteroidTurnPolicy.Gap(
                    row,
                    volume.Center.X,
                    volume.Center.Y,
                    volume.Center.Z,
                    volume.Radius
                ) > ClusterConfig.GetInt("asteroidReset.memberMeters")
            )
                continue;
            List<MyEntity> group = entity is MyCubeGrid grid
                ? MyCubeGridGroups
                    .Static.Physical.GetGroup(grid)
                    ?.Nodes.Select(n => (MyEntity)n.NodeData)
                    .ToList()
                    ?? [entity]
                : [entity];
            if (group.Any(e => ClusterRuntime.IsFrozen(e.EntityId)))
            {
                Interlocked.Increment(ref m_frozenMembers);
                Console.WriteLine(
                    "[ClusterPlugin] asteroid_turn error=frozen_member storage="
                        + row.Storage
                        + " entity="
                        + entity.EntityId
                );
                continue;
            }
            foreach (MyEntity member in group)
            {
                if (!moved.Add(member.EntityId))
                    continue;
                TurnEntity(member, turn);
                count++;
                Interlocked.Increment(ref m_lateMembers);
                Console.WriteLine(
                    "[ClusterPlugin] asteroid_turn late_member storage="
                        + row.Storage
                        + " entity="
                        + member.EntityId
                );
            }
        }
        return count;
    }

    private static void TurnEntity(MyEntity entity, MatrixD turn)
    {
        Vector3 linear = entity.Physics?.LinearVelocity ?? Vector3.Zero;
        Vector3 angular = entity.Physics?.AngularVelocity ?? Vector3.Zero;
        bool enabled = entity.Physics?.Enabled == true;
        if (enabled)
            entity.Physics.Enabled = false;
        MatrixD world = entity.WorldMatrix * turn;
        entity.PositionComp.SetWorldMatrix(ref world, null, false, true, true, true);
        if (enabled)
        {
            entity.Physics.Enabled = true;
            entity.Physics.LinearVelocity = Vector3.TransformNormal(linear, turn);
            entity.Physics.AngularVelocity = Vector3.TransformNormal(angular, turn);
            entity.Physics.RigidBody?.Activate();
        }
    }

    /// <summary>
    /// PartitionPersistence.LoadRecovery, before any entity is created: the builders from a save a turn pins, near
    /// its rock, are turned with it, each grid with every grid it is linked to (rotors, pistons, hinges, connectors,
    /// landing gear). <paramref name="sourceOf"/> names the save each builder was read from. Returns how many turned.
    /// </summary>
    public static int TurnBuilders(
        ulong partition,
        Dictionary<long, MyObjectBuilder_EntityBase> entities,
        Func<long, (ulong Partition, long Generation, long Sequence)?> sourceOf,
        IEnumerable<AsteroidTurnRow> turns
    )
    {
        int total = 0;
        double margin = ClusterConfig.GetInt("asteroidReset.memberMeters");
        foreach (AsteroidTurnRow row in turns ?? [])
        {
            var near = new HashSet<long>();
            foreach (MyObjectBuilder_EntityBase e in entities.Values)
            {
                if (
                    e.PositionAndOrientation == null
                    || sourceOf(e.EntityId) is not { } source
                    || !AsteroidTurnPolicy.Pinned(
                        row,
                        source.Partition,
                        source.Generation,
                        source.Sequence
                    )
                )
                    continue;
                if (Points(e).Any(p => AsteroidTurnPolicy.Gap(row, p.X, p.Y, p.Z, 2) <= margin))
                    near.Add(e.EntityId);
            }
            if (near.Count == 0)
                continue;
            // Physical groups: a grid linked to a near grid turns with it, wherever it is.
            Dictionary<long, long> gridOf = BlockOwners(entities);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (
                    MyObjectBuilder_CubeGrid grid in entities.Values.OfType<MyObjectBuilder_CubeGrid>()
                )
                foreach (long linked in Links(grid))
                {
                    long other;
                    if (gridOf.TryGetValue(linked, out long owner))
                        other = owner;
                    else if (entities.ContainsKey(linked))
                        other = linked;
                    else
                        continue; // a voxel a gear is locked to, or an entity outside this load
                    if (near.Contains(grid.EntityId) != near.Contains(other))
                    {
                        near.Add(grid.EntityId);
                        near.Add(other);
                        grew = true;
                    }
                }
            }
            MatrixD turn = Turn(row);
            foreach (long id in near)
                TurnBuilder(entities[id], turn);
            lock (Sync)
            {
                if (!m_turnedAtLoad.TryGetValue(row.Storage, out HashSet<long> ids))
                    m_turnedAtLoad[row.Storage] = ids = new HashSet<long>();
                ids.UnionWith(near);
            }
            total += near.Count;
            VRage.Utils.MyLog.Default?.WriteLineAndConsole(
                "[ClusterPlugin] asteroid_turn load partition="
                    + partition.ToString(CultureInfo.InvariantCulture)
                    + " storage="
                    + row.Storage
                    + " turned="
                    + near.Count.ToString(CultureInfo.InvariantCulture)
            );
        }
        return total;
    }

    /// <summary>World points of an entity builder to measure: a grid's block cells, else its position.</summary>
    private static IEnumerable<Vector3D> Points(MyObjectBuilder_EntityBase e)
    {
        MatrixD m = e.PositionAndOrientation.Value.GetMatrix();
        if (e is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks.Count == 0)
        {
            yield return m.Translation;
            yield break;
        }
        double cell = grid.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;
        foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
        {
            Vector3I min = block.Min;
            yield return Vector3D.Transform(new Vector3D(min.X, min.Y, min.Z) * cell, m);
        }
    }

    private static Dictionary<long, long> BlockOwners(
        Dictionary<long, MyObjectBuilder_EntityBase> entities
    )
    {
        var owners = new Dictionary<long, long>();
        foreach (
            MyObjectBuilder_CubeGrid grid in entities.Values.OfType<MyObjectBuilder_CubeGrid>()
        )
        foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
            if (block.EntityId != 0)
                owners[block.EntityId] = grid.EntityId;
        return owners;
    }

    /// <summary>The entities a grid's blocks are attached to: tops, bases, connected connectors, locked landing gear.</summary>
    private static IEnumerable<long> Links(MyObjectBuilder_CubeGrid grid)
    {
        foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
        {
            long? link = block switch
            {
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_MechanicalConnectionBlock b =>
                    b.TopBlockId,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_AttachableTopBlockBase b =>
                    b.ParentEntityId,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_ShipConnector b =>
                    b.ConnectedEntityId,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear b => b.AttachedEntityId,
                _ => null,
            };
            if (link is long id && id != 0)
                yield return id;
        }
    }

    private static readonly Dictionary<Type, FieldInfo[]> VelocityFields = new();

    private static void TurnBuilder(MyObjectBuilder_EntityBase e, MatrixD turn)
    {
        MatrixD world = e.PositionAndOrientation.Value.GetMatrix() * turn;
        e.PositionAndOrientation = new MyPositionAndOrientation(ref world);
        FieldInfo[] fields;
        lock (VelocityFields)
            if (!VelocityFields.TryGetValue(e.GetType(), out fields))
                VelocityFields[e.GetType()] = fields = e.GetType()
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f =>
                        f.FieldType == typeof(SerializableVector3)
                        && f.Name is "LinearVelocity" or "AngularVelocity"
                    )
                    .ToArray();
        foreach (FieldInfo field in fields)
        {
            Vector3 v = (SerializableVector3)field.GetValue(e);
            field.SetValue(e, (SerializableVector3)Vector3.TransformNormal(v, turn));
        }
    }

    // --- World Authority

    private static void Propose()
    {
        if (m_proposing)
            return;
        double players = ClusterConfig.GetInt("asteroidReset.playerMeters");
        HashSet<string> exclude = new(
            (ClusterConfig.Get("asteroidReset.exclude") ?? string.Empty).Split(
                [',', ' '],
                StringSplitOptions.RemoveEmptyEntries
            ),
            StringComparer.Ordinal
        );
        (double X, double Y, double Z)[] online = PositionRegistryService
            .Players.Select(p => (p.X, p.Y, p.Z))
            .ToArray();
        HashSet<string> done;
        lock (Sync)
            done = new HashSet<string>(m_rows.Keys, StringComparer.Ordinal);
        // Round robin by storage name from the last one proposed, so a rock that waits doesn't hold up the others.
        List<AsteroidTurnRow> candidates = Candidates(exclude, done).ToList();
        AsteroidTurnRow next = null;
        foreach (
            AsteroidTurnRow row in candidates
                .Where(r => string.CompareOrdinal(r.Storage, m_cursor) > 0)
                .Concat(candidates.Where(r => string.CompareOrdinal(r.Storage, m_cursor) <= 0))
        )
        {
            if (online.Any(p => AsteroidTurnPolicy.Gap(row, p.X, p.Y, p.Z, 0) <= players))
            {
                m_waiting[row.Storage] = "player";
                continue;
            }
            next = row;
            break;
        }
        if (next == null)
            return;
        m_cursor = next.Storage;
        m_proposing = true;
        string url =
            Gateway
            + "/registry/asteroid-turns?node="
            + Uri.EscapeDataString(
                Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty
            )
            + "&epoch="
            + ClusterRuntime.RegistryNodeEpoch.ToString(CultureInfo.InvariantCulture)
            + "&generation="
            + ClusterRuntime.AuthorityGeneration.ToString(CultureInfo.InvariantCulture)
            + "&gridMeters="
            + ClusterConfig
                .GetInt("asteroidReset.gridMeters")
                .ToString(CultureInfo.InvariantCulture)
            + "&memberMeters="
            + ClusterConfig
                .GetInt("asteroidReset.memberMeters")
                .ToString(CultureInfo.InvariantCulture)
            + "&staticMeters="
            + ClusterConfig
                .GetInt("asteroidReset.staticMeters")
                .ToString(CultureInfo.InvariantCulture)
            + "&dryRun="
            + (ClusterConfig.GetBool("asteroidReset.dryRun") ? "true" : "false");
        string body = JsonSerializer.Serialize(next, Json);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!m_authorized)
                {
                    ClusterRuntime.AuthorizeRegistryClient(Http);
                    m_authorized = true;
                }
                using HttpResponseMessage response = Http.PostAsync(
                        url,
                        new StringContent(body, Encoding.UTF8, "application/json")
                    )
                    .GetAwaiter()
                    .GetResult();
                using JsonDocument answer = ClusterRuntime.ReadRegistryResponse(response);
                JsonElement root = answer.RootElement;
                if (root.GetProperty("Committed").GetBoolean())
                {
                    AsteroidTurnRow row = JsonSerializer.Deserialize<AsteroidTurnRow>(
                        root.GetProperty("Row").GetRawText(),
                        Json
                    );
                    Merge([row]);
                    lock (Sync)
                        m_waiting.Remove(row.Storage);
                    GlobalStateChannel.Publish(Subsystem, JsonSerializer.Serialize(row, Json));
                    Console.WriteLine(
                        "[ClusterPlugin] asteroid_turn committed storage="
                            + row.Storage
                            + " pins="
                            + row.Pins.Length.ToString(CultureInfo.InvariantCulture)
                    );
                }
                else
                {
                    string why = root.TryGetProperty("Refused", out JsonElement refused)
                        ? refused.GetString()
                        : "?";
                    lock (Sync)
                    {
                        m_waiting[next.Storage] = why;
                        m_lastRefusal = next.Storage + " " + why;
                    }
                    Console.WriteLine(
                        "[ClusterPlugin] asteroid_turn refused storage="
                            + next.Storage
                            + " reason="
                            + why
                    );
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[ClusterPlugin] asteroid_turn propose_failed=true error=" + exception.Message
                );
            }
            finally
            {
                m_proposing = false;
            }
        });
    }

    /// <summary>The rotated asteroids not turned yet: free voxel maps (not planets, not user-created) off identity.</summary>
    private static IEnumerable<AsteroidTurnRow> Candidates(
        HashSet<string> exclude,
        HashSet<string> done
    )
    {
        foreach (
            MyVoxelBase voxel in MySession.Static.VoxelMaps.Instances.OrderBy(
                v => v.StorageName,
                StringComparer.Ordinal
            )
        )
        {
            if (
                voxel is not MyVoxelMap
                || voxel != voxel.RootVoxel
                || voxel.Closed
                || voxel.CreatedByUser
                || voxel.Storage == null
                || voxel.StorageName == null
                || exclude.Contains(voxel.StorageName)
                || done.Contains(voxel.StorageName)
            )
                continue;
            MatrixD m = voxel.WorldMatrix;
            double[] right = [m.Right.X, m.Right.Y, m.Right.Z],
                up = [m.Up.X, m.Up.Y, m.Up.Z],
                backward = [m.Backward.X, m.Backward.Y, m.Backward.Z];
            if (AsteroidTurnPolicy.Aligned(right, up, backward))
                continue;
            Vector3I size = voxel.Storage.Size;
            yield return new AsteroidTurnRow(
                voxel.StorageName,
                voxel.EntityId,
                [m.Translation.X, m.Translation.Y, m.Translation.Z],
                right,
                up,
                backward,
                [size.X * 0.5, size.Y * 0.5, size.Z * 0.5],
                [],
                DateTimeOffset.UtcNow
            );
        }
    }

    private static void Status()
    {
        HashSet<string> done;
        lock (Sync)
            done = new HashSet<string>(m_rows.Keys, StringComparer.Ordinal);
        int left = Candidates(new HashSet<string>(StringComparer.Ordinal), done).Count();
        string[] waiting;
        string last;
        lock (Sync)
        {
            waiting = m_waiting
                .Where(x => !done.Contains(x.Key))
                .GroupBy(x => x.Value.Split(':')[0])
                .Select(g => g.Key + "=" + g.Count().ToString(CultureInfo.InvariantCulture))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            last = m_lastRefusal;
        }
        Console.WriteLine(
            "[ClusterPlugin] asteroid_turn status turned="
                + done.Count.ToString(CultureInfo.InvariantCulture)
                + " left="
                + left.ToString(CultureInfo.InvariantCulture)
                + " waiting="
                + (waiting.Length == 0 ? "none" : string.Join(",", waiting))
                + " late_members="
                + m_lateMembers.ToString(CultureInfo.InvariantCulture)
                + " frozen_members="
                + m_frozenMembers.ToString(CultureInfo.InvariantCulture)
                + " dry_run="
                + (ClusterConfig.GetBool("asteroidReset.dryRun") ? "true" : "false")
                + " last_refusal="
                + (last ?? "none")
        );
    }

    /// <summary>For tests: the turns this node holds and which it has applied.</summary>
    internal static object Describe()
    {
        lock (Sync)
            return new
            {
                loaded = m_loaded,
                rows = m_rows.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                applied = m_applied.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                lateMembers = m_lateMembers,
                frozenMembers = m_frozenMembers,
            };
    }
}
