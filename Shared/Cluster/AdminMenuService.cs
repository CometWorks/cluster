using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1596: the Alt-F10 admin menu's Entity List and PCU tabs for the whole cluster.
///
/// Vanilla answers both from the node the admin is attached to, so a grid hosted by another node or sleeping in an
/// offline partition is missing, and a row action on an id the node doesn't have does nothing. Here the admin's node
/// asks every other node over the plugin bus (the same fan-out as <c>!grids list</c>, SE-810), each runs vanilla's
/// own list builder over the entities it owns, and the offline partitions' grids come from the Gateway. The answer goes
/// back to the admin's client with the vanilla client event once the fan-out finishes or times out; the game thread
/// never waits for it. Rows from elsewhere carry " [node-2]" or " [offline]" after their name.
///
/// Row actions on an id this node doesn't own are broadcast; the node that owns the entity runs vanilla's
/// <c>ProceedEntityAction</c>, the others ignore it. The admin is told the outcome in chat, as for F3 kicks
/// (GatewayModerationPatch). Offline grids are skipped with a note.
///
/// A node's reply on the bus holds at most 64 KiB, so a node's rows go gzipped in pages, and the asker fetches the
/// further pages with more broadcasts, at most <see cref="MaxPages"/> per node.
/// </summary>
internal static partial class AdminMenuService
{
    private const string Plugin = "cluster-node";
    private const string ListTopic = "admin-entity-list";
    private const string ActionTopic = "admin-entity-action";
    private const string GridsTopic = "admin-player-grids";
    private const string PcuTopic = "admin-pcu-info";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Under the bus's 64 KiB payload limit.</summary>
    internal const int MaxPagePayload = 60_000;
    private const int RawPageBudget = 256 * 1024;
    internal const int MaxPages = 32;
    private const int ParallelPages = 4;
    private const int MaxKnown = 200_000;

    internal const string OfflineTag = "offline";

    /// <summary>In a Missing list: offline partitions whose grids the Gateway doesn't know yet (SE-1687).</summary>
    internal const string UnindexedTag = "unindexed";
    internal const string AllNodes = "all";

    private static readonly string Node =
        Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

    private static bool m_registered;

    /// <summary>Rows built for an asker's key, so its later pages come from the same build.</summary>
    private static readonly ConcurrentDictionary<Guid, (long Built, Task<byte[][]> Pages)> m_pages =
        new();

    /// <summary>Where the last lists put the entities this node doesn't have: the camera jump of the PCU tab and the
    /// "offline, skipped" answer of a row action read it. Node = <see cref="OfflineTag"/> for an offline grid.</summary>
    private static readonly ConcurrentDictionary<long, (Vector3D Position, string Node)> m_known =
        new();

    internal static bool Active =>
        m_registered && Shared.Patches.MyEntityCyclingPatch.Enabled && ClusterRuntime.IsClustered;

    internal static void Update()
    {
        if (
            m_registered
            || !ClusterRuntime.IsClustered
            || ClusterRuntime.IsWorldAuthority
            || !Shared.Patches.MyEntityCyclingPatch.Enabled
            || MySession.Static?.Ready != true
            || PluginServices.Instance == null
        )
            return;
        m_registered = true;
        PluginServices.Instance.RegisterHandler(Plugin, ListTopic, OnList);
        PluginServices.Instance.RegisterHandler(Plugin, ActionTopic, OnAction);
        PluginServices.Instance.RegisterHandler(Plugin, GridsTopic, OnGrids);
        PluginServices.Instance.RegisterHandler(Plugin, PcuTopic, OnPcu);
        PluginServices.Instance.RegisterHandler(Plugin, CycleTopic, OnCycle);
        PluginServices.Instance.RegisterHandler(Plugin, SweepTopic, OnSweep);
    }

    // ---- rows on the wire ------------------------------------------------------------------------------------------

    /// <summary>One <see cref="MyEntityList.MyEntityListInfoItem"/>, with short names, rounded where a meter or a
    /// kilogram is plenty.</summary>
    internal sealed class Row
    {
        [JsonPropertyName("i")]
        public long Id { get; set; }

        [JsonPropertyName("n")]
        public string Name { get; set; }

        [JsonPropertyName("b")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Blocks { get; set; }

        [JsonPropertyName("p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Pcu { get; set; }

        [JsonPropertyName("m")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Mass { get; set; }

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("z")]
        public double Z { get; set; }

        [JsonPropertyName("on")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string OwnerName { get; set; }

        [JsonPropertyName("o")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long Owner { get; set; }

        [JsonPropertyName("s")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Speed { get; set; }

        [JsonPropertyName("d")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Distance { get; set; }

        [JsonPropertyName("li")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Login { get; set; }

        [JsonPropertyName("lo")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Logout { get; set; }

        [JsonPropertyName("pt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int PlayerTier { get; set; }

        [JsonPropertyName("gt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int GridTier { get; set; }

        [JsonPropertyName("r")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Replicated { get; set; }

        [JsonPropertyName("g")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Grid { get; set; }

        internal static Row From(MyEntityList.MyEntityListInfoItem item) =>
            new()
            {
                Id = item.EntityId,
                Name = item.DisplayName,
                Blocks = item.BlockCount,
                Pcu = item.PCU,
                Mass = (float)Math.Round(item.Mass),
                X = Math.Round(item.Position.X),
                Y = Math.Round(item.Position.Y),
                Z = Math.Round(item.Position.Z),
                OwnerName = string.IsNullOrEmpty(item.OwnerName) ? null : item.OwnerName,
                Owner = item.Owner,
                Speed = (float)Math.Round(item.Speed, 1),
                Distance = (float)Math.Round(item.DistanceFromPlayers),
                Login = (float)Math.Round(item.OwnerLoginTime),
                Logout = item.OwnerLogoutTime is { } logout ? (float)Math.Round(logout) : null,
                PlayerTier = (int)item.PlayerPresenceTier,
                GridTier = (int)item.GridPresenceTier,
                Replicated = item.IsReplicated,
                Grid = item.IsGrid,
            };

        internal MyEntityList.MyEntityListInfoItem ToItem(string tag) =>
            new()
            {
                EntityId = Id,
                DisplayName = (Name ?? string.Empty) + " [" + tag + "]",
                BlockCount = Blocks,
                PCU = Pcu,
                Mass = Mass,
                Position = new Vector3D(X, Y, Z),
                OwnerName = OwnerName ?? string.Empty,
                Owner = Owner,
                Speed = Speed,
                DistanceFromPlayers = Distance,
                OwnerLoginTime = Login,
                OwnerLogoutTime = Logout,
                PlayerPresenceTier = (MyUpdateTiersPlayerPresence)PlayerTier,
                GridPresenceTier = (MyUpdateTiersGridPresence)GridTier,
                IsReplicated = Replicated,
                IsGrid = Grid,
            };
    }

    /// <summary>Packs rows into pages of at most <see cref="MaxPagePayload"/> bytes each: two bytes of page count, then
    /// the gzipped JSON array.</summary>
    internal static byte[][] Pages(IReadOnlyList<Row> rows, int rawBudget = RawPageBudget)
    {
        byte[][] encoded = rows.Select(row => JsonSerializer.SerializeToUtf8Bytes(row)).ToArray();
        var bodies = new List<byte[]>();
        int start = 0;
        while (start < encoded.Length)
        {
            int count = 0;
            long raw = 0;
            while (
                start + count < encoded.Length
                && (count == 0 || raw + encoded[start + count].Length + 1 <= rawBudget)
            )
                raw += encoded[start + count++].Length + 1;
            byte[] body;
            while (
                (body = GzipRows(encoded, start, count)).Length > MaxPagePayload - 2 && count > 1
            )
                count /= 2;
            bodies.Add(body);
            start += count;
        }
        if (bodies.Count == 0)
            bodies.Add(GzipRows(encoded, 0, 0));
        int pages = bodies.Count;
        return bodies
            .Select(body =>
            {
                var page = new byte[body.Length + 2];
                page[0] = (byte)(pages & 0xFF);
                page[1] = (byte)(pages >> 8);
                Buffer.BlockCopy(body, 0, page, 2, body.Length);
                return page;
            })
            .ToArray();
    }

    private static byte[] GzipRows(byte[][] rows, int start, int count)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            zip.WriteByte((byte)'[');
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    zip.WriteByte((byte)',');
                zip.Write(rows[start + i], 0, rows[start + i].Length);
            }
            zip.WriteByte((byte)']');
        }
        return output.ToArray();
    }

    /// <summary>A page back to its page count and rows. A bare count (two bytes) is a page past the node's last.</summary>
    internal static (int Pages, Row[] Rows) ReadPage(byte[] page)
    {
        if (page == null || page.Length < 2)
            return (0, Array.Empty<Row>());
        int pages = page[0] | (page[1] << 8);
        if (page.Length == 2)
            return (pages, Array.Empty<Row>());
        using var input = new GZipStream(
            new MemoryStream(page, 2, page.Length - 2),
            CompressionMode.Decompress
        );
        return (pages, JsonSerializer.Deserialize<Row[]>(input) ?? Array.Empty<Row>());
    }

    // ---- what this node holds ----------------------------------------------------------------------------------------

    /// <summary>Game thread: this node hosts the entity as its own, not as a copy of another node's, and it is a
    /// partition member (planets, asteroids and safe zones are on every node, so only the asking node lists them).</summary>
    internal static bool OwnedHere(long entityId)
    {
        if (entityId <= 0)
            return false;
        PluginEntityPlacement placement = PluginServices.Instance.LocateEntity(entityId);
        return placement.Residence switch
        {
            PluginEntityResidence.Local or PluginEntityResidence.Frozen => true,
            PluginEntityResidence.Unowned => placement.Partition != 0
                && ClusterRuntime.PartitionClaimPending(placement.Partition),
            _ => false,
        };
    }

    /// <summary>Game thread: a row action on this id belongs to this node. Not when the entity is missing here, or is
    /// a copy of something in a partition this node doesn't own. Synthetic GPS rows (negative ids) stay vanilla's,
    /// which ignores them.</summary>
    internal static bool ActsHere(long entityId)
    {
        if (entityId <= 0)
            return true;
        if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity.MarkedForClose)
            return false;
        PluginEntityPlacement placement = PluginServices.Instance.LocateEntity(entityId);
        return placement.Residence != PluginEntityResidence.Unowned
            || placement.Partition == 0
            || ClusterRuntime.PartitionClaimPending(placement.Partition);
    }

    // ---- Entity List -------------------------------------------------------------------------------------------------

    /// <summary>What a list cost: rows by source, node replies read, their bytes, wall time, and who is missing.</summary>
    internal sealed record ListStats(
        int Local,
        int Remote,
        int Offline,
        int Merged,
        int Replies,
        long Bytes,
        long Milliseconds,
        string[] Missing,
        string[] Truncated
    );

    internal sealed record ListResult(
        List<MyEntityList.MyEntityListInfoItem> Rows,
        ListStats Stats
    );

    private sealed record ListRequest(
        [property: JsonPropertyName("t")] int Type,
        [property: JsonPropertyName("p")] int Page,
        [property: JsonPropertyName("k")] Guid Key
    );

    // Game thread, on every other node: page p of this node's own rows of the type. Only the list is built here, the
    // gzip (about 15 ms per 1000 rows) runs off the game thread.
    private static async Task<byte[]> OnList(PluginMessage message)
    {
        if (message.Source == Node)
            return new byte[] { 0, 0 };
        ListRequest request = JsonSerializer.Deserialize<ListRequest>(message.Payload);
        long now = Environment.TickCount64;
        foreach (var stale in m_pages.Where(entry => now - entry.Value.Built > 60_000).ToArray())
            m_pages.TryRemove(stale.Key, out _);
        if (!m_pages.TryGetValue(request.Key, out var built))
        {
            var watch = Stopwatch.StartNew();
            var type = (MyEntityList.MyEntityTypeEnum)request.Type;
            List<Row> rows = MyEntityList
                .GetEntityList(type)
                .Where(row => OwnedHere(row.EntityId))
                .Select(Row.From)
                .ToList();
            long listed = watch.ElapsedMilliseconds;
            string source = message.Source;
            built = (
                now,
                Task.Run(() =>
                {
                    var packing = Stopwatch.StartNew();
                    byte[][] pages = Pages(rows);
                    Console.WriteLine(
                        $"[ClusterPlugin] admin entity list for {source}: type={type} rows={rows.Count}"
                            + $" pages={pages.Length} bytes={pages.Sum(page => (long)page.Length)}"
                            + $" list={listed}ms (game thread) pack={packing.ElapsedMilliseconds}ms (SE-1596)"
                    );
                    return pages;
                })
            );
            m_pages[request.Key] = built;
        }
        byte[][] pages = await built.Pages.ConfigureAwait(false);
        return request.Page < pages.Length
            ? pages[request.Page]
            : new byte[] { (byte)(pages.Length & 0xFF), (byte)(pages.Length >> 8) };
    }

    /// <summary>Any thread: the other nodes' rows and the offline grids, merged into <paramref name="local"/>.</summary>
    internal static async Task<ListResult> CollectListAsync(
        MyEntityList.MyEntityTypeEnum type,
        List<MyEntityList.MyEntityListInfoItem> local
    )
    {
        var watch = Stopwatch.StartNew();
        Guid key = Guid.NewGuid();
        Task<OfflineGrids> offline = GridType(type) ? OfflineGridsAsync(null) : null;
        var rows = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var pageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        long bytes = 0;
        int replies = 0;
        object sync = new();

        void Take(PluginBroadcastResult result, int page)
        {
            lock (sync)
            {
                if (result.Nodes == null)
                    missing.Add(AllNodes);
                foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
                {
                    if (answer.Key == Node)
                        continue;
                    if (answer.Value.Code != PluginResultCode.Success)
                    {
                        missing.Add(answer.Key);
                        continue;
                    }
                    (int pages, Row[] got) = ReadPage(answer.Value.Payload);
                    bytes += answer.Value.Payload?.Length ?? 0;
                    replies++;
                    if (page == 0)
                        pageCounts[answer.Key] = pages;
                    if (!rows.TryGetValue(answer.Key, out var list))
                        rows[answer.Key] = list = new List<Row>();
                    list.AddRange(got);
                }
            }
        }

        Take(await BroadcastAsync(ListTopic, ListPayload(type, 0, key)).ConfigureAwait(false), 0);
        int most = Math.Min(pageCounts.Count == 0 ? 0 : pageCounts.Values.Max(), MaxPages);
        using (var gate = new SemaphoreSlim(ParallelPages))
            await Task.WhenAll(
                    Enumerable
                        .Range(1, Math.Max(0, most - 1))
                        .Select(async page =>
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                Take(
                                    await BroadcastAsync(ListTopic, ListPayload(type, page, key))
                                        .ConfigureAwait(false),
                                    page
                                );
                            }
                            finally
                            {
                                gate.Release();
                            }
                        })
                )
                .ConfigureAwait(false);

        OfflineGrid[] sleeping = Array.Empty<OfflineGrid>();
        if (offline != null)
        {
            try
            {
                OfflineGrids answer = await offline.ConfigureAwait(false);
                if (answer.Unknown > 0)
                    missing.Add(UnindexedTag);
                sleeping = answer
                    .Grids.Where(grid =>
                        type == MyEntityList.MyEntityTypeEnum.Grids
                        || grid.Large == (type == MyEntityList.MyEntityTypeEnum.LargeGrids)
                    )
                    .ToArray();
            }
            catch (Exception exception)
                when (exception
                        is HttpRequestException
                            or TaskCanceledException
                            or JsonException
                            or InvalidOperationException
                            or KeyNotFoundException
                )
            {
                missing.Add(OfflineTag);
            }
        }

        List<MyEntityList.MyEntityListInfoItem> merged = Merge(
            local,
            rows.Select(entry => (entry.Key, (IReadOnlyList<Row>)entry.Value)),
            sleeping.Select(OfflineRow)
        );
        foreach (var entry in rows)
        foreach (Row row in entry.Value)
            Remember(row.Id, new Vector3D(row.X, row.Y, row.Z), entry.Key);
        foreach (OfflineGrid grid in sleeping)
            Remember(grid.Id, new Vector3D(grid.X, grid.Y, grid.Z), OfflineTag);
        return new ListResult(
            merged,
            new ListStats(
                local.Count,
                rows.Values.Sum(list => list.Count),
                sleeping.Length,
                merged.Count,
                replies,
                bytes,
                watch.ElapsedMilliseconds,
                missing.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                pageCounts
                    .Where(entry => entry.Value > MaxPages)
                    .Select(entry => entry.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()
            )
        );
    }

    private static byte[] ListPayload(MyEntityList.MyEntityTypeEnum type, int page, Guid key) =>
        JsonSerializer.SerializeToUtf8Bytes(new ListRequest((int)type, page, key));

    private static bool GridType(MyEntityList.MyEntityTypeEnum type) =>
        type
            is MyEntityList.MyEntityTypeEnum.Grids
                or MyEntityList.MyEntityTypeEnum.SmallGrids
                or MyEntityList.MyEntityTypeEnum.LargeGrids;

    /// <summary>
    /// The local list plus the other nodes' rows and the offline grids, each id once. A node's row of an entity it owns
    /// replaces a local row of the same id, which can only be a copy here; an offline row only fills a gap, since a
    /// partition that just woke can still have its offline row.
    /// </summary>
    internal static List<MyEntityList.MyEntityListInfoItem> Merge(
        List<MyEntityList.MyEntityListInfoItem> local,
        IEnumerable<(string Node, IReadOnlyList<Row> Rows)> remote,
        IEnumerable<Row> offline
    )
    {
        var merged = new List<MyEntityList.MyEntityListInfoItem>(local);
        var index = new Dictionary<long, int>();
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].EntityId > 0)
                index[merged[i].EntityId] = i;
        var owned = new HashSet<long>();
        foreach (
            (string node, IReadOnlyList<Row> rows) in remote.OrderBy(
                entry => entry.Node,
                StringComparer.Ordinal
            )
        )
        foreach (Row row in rows)
        {
            if (row.Id <= 0 || !owned.Add(row.Id))
                continue;
            MyEntityList.MyEntityListInfoItem item = row.ToItem(node);
            if (index.TryGetValue(row.Id, out int at))
                merged[at] = item;
            else
            {
                index[row.Id] = merged.Count;
                merged.Add(item);
            }
        }
        foreach (Row row in offline)
            if (row.Id > 0 && !index.ContainsKey(row.Id))
            {
                index[row.Id] = merged.Count;
                merged.Add(row.ToItem(OfflineTag));
            }
        return merged;
    }

    private static void Remember(long id, Vector3D position, string node)
    {
        if (m_known.Count >= MaxKnown)
            m_known.Clear();
        m_known[id] = (position, node);
    }

    internal static bool TryKnown(long id, out Vector3D position, out string node)
    {
        bool found = m_known.TryGetValue(id, out var known);
        (position, node) = known;
        return found;
    }

    // ---- offline grids from the Gateway ------------------------------------------------------------------------------

    internal sealed record OfflineGrid(
        long Id,
        string Name,
        bool Large,
        int Blocks,
        double X,
        double Y,
        double Z,
        long[] Owners,
        double? Mass
    );

    /// <summary>Unknown = offline partitions whose grids the Gateway hasn't read yet: they are not in Grids.</summary>
    internal sealed record OfflineGrids(OfflineGrid[] Grids, int Unknown);

    private static async Task<OfflineGrids> OfflineGridsAsync(long? owner)
    {
        using HttpClient client = ClusterHttp.Client(Timeout);
        ClusterRuntime.AuthorizeRegistryClient(client);
        string root = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        string text = await client
            .GetStringAsync(
                root
                    + "/registry/partitions/offline/grids"
                    + (
                        owner is { } id
                            ? "?owner=" + id.ToString(CultureInfo.InvariantCulture)
                            : string.Empty
                    )
            )
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(text);
        var grids = new List<OfflineGrid>();
        foreach (JsonElement row in document.RootElement.GetProperty("grids").EnumerateArray())
        {
            JsonElement grid = row.GetProperty("grid");
            grids.Add(
                new OfflineGrid(
                    grid.GetProperty("id").GetInt64(),
                    grid.GetProperty("n").GetString(),
                    grid.GetProperty("l").GetBoolean(),
                    grid.GetProperty("b").GetInt32(),
                    grid.GetProperty("x").GetDouble(),
                    grid.GetProperty("y").GetDouble(),
                    grid.GetProperty("z").GetDouble(),
                    grid.TryGetProperty("o", out JsonElement owners)
                    && owners.ValueKind == JsonValueKind.Array
                        ? owners.EnumerateArray().Select(o => o.GetInt64()).ToArray()
                        : Array.Empty<long>(),
                    grid.TryGetProperty("m", out JsonElement mass)
                    && mass.ValueKind == JsonValueKind.Number
                        ? mass.GetDouble()
                        : null
                )
            );
        }
        return new OfflineGrids(
            grids.ToArray(),
            document.RootElement.TryGetProperty("unknown", out JsonElement unknown)
            && unknown.ValueKind == JsonValueKind.Number
                ? unknown.GetInt32()
                : 0
        );
    }

    internal static Row OfflineRow(OfflineGrid grid)
    {
        long owner = grid.Owners?.FirstOrDefault() ?? 0;
        return new Row
        {
            Id = grid.Id,
            Name = grid.Name,
            Blocks = grid.Blocks,
            Mass = (float)Math.Round(grid.Mass ?? 0),
            X = Math.Round(grid.X),
            Y = Math.Round(grid.Y),
            Z = Math.Round(grid.Z),
            Distance = NearestPlayer(
                new Vector3D(grid.X, grid.Y, grid.Z),
                (float)Math.Sqrt(float.MaxValue)
            ),
            Owner = owner,
            OwnerName =
                owner == 0 ? null : MySession.Static?.Players.TryGetIdentity(owner)?.DisplayName,
            Replicated = false,
            Grid = true,
        };
    }

    // ---- row actions -------------------------------------------------------------------------------------------------

    private sealed record ActionRequest(
        [property: JsonPropertyName("ids")] long[] Ids,
        [property: JsonPropertyName("a")] int Action
    );

    private sealed record ActionReply(
        [property: JsonPropertyName("done")] long[] Done,
        [property: JsonPropertyName("busy")] long[] Busy
    );

    /// <summary>Game thread: takes the ids that belong to other nodes out of <paramref name="ids"/> and returns them.</summary>
    internal static List<long> TakeRemote(List<long> ids)
    {
        var remote = ids.Where(id => !ActsHere(id)).Distinct().ToList();
        if (remote.Count > 0)
            ids.RemoveAll(remote.Contains);
        return remote;
    }

    // Game thread, on every node: the action on the ids this node owns
    private static Task<byte[]> OnAction(PluginMessage message)
    {
        ActionRequest request = JsonSerializer.Deserialize<ActionRequest>(message.Payload);
        var action = (MyEntityList.EntityListAction)request.Action;
        var done = new List<long>();
        var busy = new List<long>();
        if (message.Source != Node)
            foreach (long id in request.Ids ?? Array.Empty<long>())
            {
                if (
                    !OwnedHere(id)
                    || !MyEntities.TryGetEntityById(id, out MyEntity entity)
                    || entity.MarkedForClose
                )
                    continue;
                if (
                    PluginServices.Instance.LocateEntity(id).Residence
                    is PluginEntityResidence.Frozen
                )
                {
                    busy.Add(id);
                    continue;
                }
                MyEntityList.ProceedEntityAction(entity, action);
                done.Add(id);
            }
        if (done.Count > 0)
            Console.WriteLine(
                $"[ClusterPlugin] admin {action} asked by {message.Source} done on"
                    + $" {string.Join(",", done)} (SE-1596)"
            );
        return Task.FromResult(
            JsonSerializer.SerializeToUtf8Bytes(new ActionReply(done.ToArray(), busy.ToArray()))
        );
    }

    /// <summary>Any thread: the action on ids other nodes own, and the chat line for the admin.</summary>
    internal static async Task<string> ForwardAsync(
        IReadOnlyList<long> ids,
        MyEntityList.EntityListAction action
    )
    {
        var offline = ids.Where(id => TryKnown(id, out _, out string node) && node == OfflineTag)
            .ToList();
        long[] asked = ids.Except(offline).ToArray();
        var done = new Dictionary<string, int>(StringComparer.Ordinal);
        int busy = 0;
        var silent = new List<string>();
        var answered = new HashSet<long>();
        if (asked.Length > 0)
        {
            PluginBroadcastResult result = await BroadcastAsync(
                    ActionTopic,
                    JsonSerializer.SerializeToUtf8Bytes(new ActionRequest(asked, (int)action))
                )
                .ConfigureAwait(false);
            if (result.Nodes == null)
                silent.Add(AllNodes);
            foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
            {
                if (answer.Value.Code != PluginResultCode.Success)
                {
                    if (answer.Key != Node)
                        silent.Add(answer.Key);
                    continue;
                }
                ActionReply reply = JsonSerializer.Deserialize<ActionReply>(answer.Value.Payload);
                if (reply.Done?.Length > 0)
                    done[answer.Key] = reply.Done.Length;
                busy += reply.Busy?.Length ?? 0;
                answered.UnionWith(reply.Done ?? Array.Empty<long>());
                answered.UnionWith(reply.Busy ?? Array.Empty<long>());
            }
        }
        return Outcome(
            action,
            done,
            busy,
            offline.Count,
            asked.Count(id => !answered.Contains(id)),
            silent
        );
    }

    /// <summary>The chat line for a forwarded action.</summary>
    internal static string Outcome(
        MyEntityList.EntityListAction action,
        IReadOnlyDictionary<string, int> done,
        int busy,
        int offline,
        int notFound,
        IReadOnlyList<string> silent
    )
    {
        var text = new StringBuilder("Admin " + action + ":");
        if (done.Count > 0)
            text.Append(
                " done, "
                    + string.Join(
                        ", ",
                        done.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .Select(entry => entry.Value + " on " + entry.Key)
                    )
                    + "."
            );
        if (busy > 0)
            text.Append(" " + busy + " moving between servers, try again.");
        if (offline > 0)
            text.Append(" " + offline + " in an offline area, skipped.");
        if (notFound > 0)
            text.Append(
                " "
                    + notFound
                    + " not found on any server"
                    + (silent.Count > 0 ? " that answered" : "")
                    + "."
            );
        if (silent.Contains(AllNodes))
            text.Append(" The other servers did not answer.");
        else if (silent.Count > 0)
            text.Append(" No answer from " + string.Join(", ", silent) + ".");
        return text.ToString();
    }

    // ---- PCU tab -------------------------------------------------------------------------------------------------------

    internal sealed record GridPcu(
        [property: JsonPropertyName("p")] int Pcu,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("i")] long Id,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z
    );

    /// <summary>
    /// Game thread: vanilla's <c>RequestPlayerGridsUpdate</c> list, the identity's PCU on each grid it built blocks on,
    /// for the grids this node owns. <paramref name="ownedOnly"/> false keeps every grid loaded here, as vanilla does.
    /// </summary>
    internal static List<GridPcu> PlayerGrids(long identityId, bool ownedOnly)
    {
        var grids = new List<GridPcu>();
        MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId);
        if (identity?.BlockLimits == null)
            return grids;
        var builders = new HashSet<long>();
        if (MySession.Static.Players.IdentityIsNpc(identityId))
        {
            foreach (long npc in Sync.Players.GetNPCIdentities() ?? new HashSet<long>())
                if (
                    Sync.Players.TryGetIdentity(npc) is { } other
                    && other.IsGlobalEncounterSpawner == identity.IsGlobalEncounterSpawner
                )
                    builders.Add(npc);
        }
        else
            builders.Add(identityId);
        foreach (var built in identity.BlockLimits.BlocksBuiltByGrid.ToArray())
        {
            if (
                !MyEntities.TryGetEntityById(built.Value.EntityId, out MyEntity entity)
                || entity is not MyCubeGrid grid
                || ownedOnly && !OwnedHere(grid.EntityId)
            )
                continue;
            int pcu = 0;
            foreach (MySlimBlock block in grid.CubeBlocks)
                if (builders.Contains(block.BuiltBy))
                    pcu += MyBlockLimits.GetBlockCurrentPCU(block);
            if (pcu <= 0)
                continue;
            Vector3D position = grid.PositionComp.GetPosition();
            grids.Add(
                new GridPcu(
                    pcu,
                    built.Value.GridName,
                    grid.EntityId,
                    Math.Round(position.X),
                    Math.Round(position.Y),
                    Math.Round(position.Z)
                )
            );
        }
        return grids;
    }

    // Game thread, on every other node: the identity's grids this node owns
    private static Task<byte[]> OnGrids(PluginMessage message)
    {
        if (message.Source == Node)
            return Task.FromResult(Gzip(new List<GridPcu>()));
        long identity = long.Parse(
            Encoding.UTF8.GetString(message.Payload),
            CultureInfo.InvariantCulture
        );
        List<GridPcu> grids = PlayerGrids(identity, ownedOnly: true)
            .OrderByDescending(grid => grid.Pcu)
            .ToList();
        byte[] payload;
        // An NPC identity can hold hundreds: keep the biggest that fit
        while ((payload = Gzip(grids)).Length > MaxPagePayload && grids.Count > 1)
            grids.RemoveRange(grids.Count / 2, grids.Count - grids.Count / 2);
        return Task.FromResult(payload);
    }

    private static byte[] Gzip(List<GridPcu> items)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true))
            JsonSerializer.Serialize(zip, items);
        return output.ToArray();
    }

    private static GridPcu[] Gunzip(byte[] payload)
    {
        using var input = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<GridPcu[]>(input) ?? Array.Empty<GridPcu>();
    }

    internal sealed record GridsResult(
        List<string> Names,
        List<long> Ids,
        int TotalPcu,
        string[] Missing,
        long Milliseconds
    );

    /// <summary>Any thread: the identity's grids on every node and in offline partitions, in vanilla's order and
    /// format.</summary>
    internal static async Task<GridsResult> CollectPlayerGridsAsync(
        long identityId,
        List<GridPcu> local
    )
    {
        var watch = Stopwatch.StartNew();
        Task<OfflineGrids> offline = OfflineGridsAsync(identityId);
        PluginBroadcastResult result = await BroadcastAsync(
                GridsTopic,
                Encoding.UTF8.GetBytes(identityId.ToString(CultureInfo.InvariantCulture))
            )
            .ConfigureAwait(false);
        var remote = new List<(string Node, GridPcu Grid)>();
        var missing = new List<string>();
        if (result.Nodes == null)
            missing.Add(AllNodes);
        foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
        {
            if (answer.Key == Node)
                continue;
            if (answer.Value.Code != PluginResultCode.Success)
                missing.Add(answer.Key);
            else
                remote.AddRange(Gunzip(answer.Value.Payload).Select(grid => (answer.Key, grid)));
        }
        OfflineGrid[] sleeping = Array.Empty<OfflineGrid>();
        try
        {
            OfflineGrids answer = await offline.ConfigureAwait(false);
            if (answer.Unknown > 0)
                missing.Add(UnindexedTag);
            sleeping = answer.Grids;
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or TaskCanceledException
                        or JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
            )
        {
            missing.Add(OfflineTag);
        }
        foreach (var (node, grid) in remote)
            Remember(grid.Id, new Vector3D(grid.X, grid.Y, grid.Z), node);
        foreach (OfflineGrid grid in sleeping)
            Remember(grid.Id, new Vector3D(grid.X, grid.Y, grid.Z), OfflineTag);
        (List<string> names, List<long> ids, int total) = MergeGrids(local, remote, sleeping);
        return new GridsResult(names, ids, total, missing.ToArray(), watch.ElapsedMilliseconds);
    }

    /// <summary>Vanilla's "(PCU) name", most PCU first, other nodes' grids tagged with their node. Offline grids have
    /// no PCU known: "(offline) name" at the end, not in the total.</summary>
    internal static (List<string> Names, List<long> Ids, int Total) MergeGrids(
        IReadOnlyList<GridPcu> local,
        IReadOnlyList<(string Node, GridPcu Grid)> remote,
        IReadOnlyList<OfflineGrid> offline
    )
    {
        var seen = new HashSet<long>(local.Select(grid => grid.Id));
        var rows = local
            .Select(grid => (grid.Pcu, Name: "(" + grid.Pcu + ") " + grid.Name, grid.Id))
            .Concat(
                remote
                    .Where(entry => seen.Add(entry.Grid.Id))
                    .Select(entry =>
                        (
                            entry.Grid.Pcu,
                            Name: "("
                                + entry.Grid.Pcu
                                + ") "
                                + entry.Grid.Name
                                + " ["
                                + entry.Node
                                + "]",
                            entry.Grid.Id
                        )
                    )
            )
            .OrderByDescending(row => row.Pcu)
            .ToList();
        int total = rows.Sum(row => row.Pcu);
        rows.AddRange(
            offline
                .Where(grid => seen.Add(grid.Id))
                .OrderBy(grid => grid.Name, StringComparer.Ordinal)
                .Select(grid => (0, "(" + OfflineTag + ") " + grid.Name, grid.Id))
        );
        return (rows.Select(row => row.Name).ToList(), rows.Select(row => row.Id).ToList(), total);
    }

    /// <summary>Vanilla's PCU numbers of this node: used, pirate used and available, global encounters used and
    /// available.</summary>
    internal static int[] PcuHere() =>
        new[]
        {
            MySession.Static?.SessionBlockLimits?.PCUBuilt ?? 0,
            MySession.Static?.NPCBlockLimits?.Pirate?.PCUBuilt ?? 0,
            MySession.Static?.NPCBlockLimits?.Pirate?.PCU ?? 0,
            MySession.Static?.NPCBlockLimits?.GlobalEncounter?.PCUBuilt ?? 0,
            MySession.Static?.NPCBlockLimits?.GlobalEncounter?.PCU ?? 0,
        };

    private static Task<byte[]> OnPcu(PluginMessage message) =>
        Task.FromResult(
            JsonSerializer.SerializeToUtf8Bytes(message.Source == Node ? new int[5] : PcuHere())
        );

    /// <summary>Any thread: every node's PCU numbers summed with <paramref name="local"/>. Each node has its own NPC
    /// pools, so their budgets add up too.</summary>
    internal static async Task<(int[] Sums, string[] Missing)> CollectPcuAsync(int[] local)
    {
        PluginBroadcastResult result = await BroadcastAsync(PcuTopic, Array.Empty<byte>())
            .ConfigureAwait(false);
        int[] sums = (int[])local.Clone();
        var missing = new List<string>();
        if (result.Nodes == null)
            missing.Add(AllNodes);
        foreach (var answer in result.Nodes ?? new Dictionary<string, PluginResult>())
        {
            if (answer.Key == Node)
                continue;
            if (answer.Value.Code != PluginResultCode.Success)
            {
                missing.Add(answer.Key);
                continue;
            }
            int[] values = JsonSerializer.Deserialize<int[]>(answer.Value.Payload);
            for (int i = 0; i < sums.Length && i < values.Length; i++)
                sums[i] += values[i];
        }
        return (sums, missing.ToArray());
    }

    // ---- bus -----------------------------------------------------------------------------------------------------------

    // A Fenced answer can be a fence that moved after the roster was read: a new broadcast reads the roster again
    private static async Task<PluginBroadcastResult> BroadcastAsync(string topic, byte[] payload)
    {
        for (int attempt = 1; ; attempt++)
        {
            PluginBroadcastResult result = await PluginServices
                .Instance.BroadcastAsync(
                    Plugin,
                    topic,
                    payload,
                    Guid.NewGuid(),
                    false,
                    Timeout,
                    default
                )
                .ConfigureAwait(false);
            if (attempt == 3 || result.Code != PluginResultCode.Fenced)
                return result;
        }
    }

    /// <summary>The chat line for a list that is not complete, or null.</summary>
    internal static string Incomplete(
        IReadOnlyCollection<string> missing,
        IReadOnlyCollection<string> truncated
    )
    {
        var parts = new List<string>();
        string[] nodes = missing
            .Where(name => name != OfflineTag && name != AllNodes && name != UnindexedTag)
            .ToArray();
        if (missing.Contains(AllNodes))
            parts.Add("The other servers of the cluster did not answer, so they are not listed.");
        else if (nodes.Length > 0)
            parts.Add("No answer from " + string.Join(", ", nodes) + ", so they are not listed.");
        if (missing.Contains(OfflineTag))
            parts.Add("Grids in offline areas could not be listed.");
        else if (missing.Contains(UnindexedTag))
            parts.Add("Some offline areas are not indexed yet, so their grids are not listed.");
        if (truncated.Count > 0)
            parts.Add(
                "Too many rows on "
                    + string.Join(", ", truncated)
                    + ": only the first "
                    + MaxPages
                    + " pages are listed."
            );
        return parts.Count == 0 ? null : "Admin list: " + string.Join(" ", parts);
    }
}
