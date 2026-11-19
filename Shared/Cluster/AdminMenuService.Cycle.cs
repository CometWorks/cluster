using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1687: the Alt-F10 Cycle Objects tab (First / Next / Previous under an order such as "Biggest grids") for the
/// whole cluster, and the players' distance the Entity List and the cycler measure.
///
/// Vanilla's <c>MyEntityCycling.FindNext</c> walks the admin's node's <c>MyEntities</c>: the biggest grid on another
/// node or in an offline partition never comes up. Here each node finds its own next entity and its own wrap-around
/// entity under the same order and metric, the Gateway's offline grids add theirs where the order can be measured
/// without loading them (block count, static, distance from players, owner login time), and the admin's node picks the global one with
/// vanilla's own <c>Metric</c> comparison, so the walk is the one a single server with all of them would make.
///
/// The client gets vanilla's <c>Cycle_Implementation</c> event with the pick's position, so its camera goes there even
/// when the entity is not replicated to it. Its name label can't follow such an entity (a vanilla client limit), so a
/// pick from elsewhere also comes as a chat line. Row actions on it are forwarded like the Entity List's (SE-1596).
/// </summary>
internal static partial class AdminMenuService
{
    private const string CycleTopic = "admin-cycle";

    /// <summary>One entity the cycler can land on, as the node that owns it sees it.</summary>
    internal sealed record Pick(
        [property: JsonPropertyName("v")] float Value,
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("s")] bool Station,
        [property: JsonPropertyName("n")] string Name
    )
    {
        [JsonIgnore]
        public string Node { get; init; }

        internal MyEntityCycling.Metric Metric => new(Value, Id);
    }

    /// <summary>Next = the nearest entity past the current one in the walk's direction; Wrap = the first one of the
    /// walk, where it goes on when nothing is past the current one. Either is null when there is none.</summary>
    internal sealed record Candidates(
        [property: JsonPropertyName("next")] Pick Next,
        [property: JsonPropertyName("wrap")] Pick Wrap
    );

    internal sealed record CycleStep(
        [property: JsonPropertyName("o")] int Order,
        [property: JsonPropertyName("l")] bool FindLarger,
        [property: JsonPropertyName("v")] float Metric,
        [property: JsonPropertyName("e")] long EntityId,
        [property: JsonPropertyName("on")] bool Filtered,
        [property: JsonPropertyName("sg")] bool OnlySmall,
        [property: JsonPropertyName("lg")] bool OnlyLarge
    )
    {
        internal MyEntityCyclingOrder CyclingOrder => (MyEntityCyclingOrder)Order;

        internal static CycleStep Of(
            MyEntityCyclingOrder order,
            bool findLarger,
            float metric,
            long entityId,
            CyclingOptions options
        ) =>
            new(
                (int)order,
                findLarger,
                metric,
                entityId,
                options.Enabled,
                options.OnlySmallGrids,
                options.OnlyLargeGrids
            );

        /// <summary>Vanilla's Small/Large grid checkboxes.</summary>
        internal bool Skips(bool isGrid, bool large) =>
            Filtered && (OnlyLarge && (!isGrid || !large) || OnlySmall && (!isGrid || large));
    }

    internal sealed record CycleResult(Pick Pick, string[] Missing);

    /// <summary>a comes before b in the walk's direction: vanilla's FindNext ordering.</summary>
    private static bool Ahead(
        MyEntityCycling.Metric a,
        MyEntityCycling.Metric b,
        bool findLarger
    ) => findLarger ? a > b : a < b;

    /// <summary>The next and wrap-around pick among <paramref name="picks"/> from the current position: what
    /// vanilla's FindNext answers for these entities. Merging the nodes' answers is the same call over their
    /// candidates, since each node's Next and Wrap are its best ones.</summary>
    internal static Candidates Best(IEnumerable<Pick> picks, CycleStep step)
    {
        var current = new MyEntityCycling.Metric(step.Metric, step.EntityId);
        Pick next = null,
            wrap = null;
        foreach (Pick pick in picks)
        {
            if (pick == null || pick.Value == 0f)
                continue;
            MyEntityCycling.Metric metric = pick.Metric;
            if (
                Ahead(metric, current, step.FindLarger)
                && (next == null || Ahead(metric, next.Metric, !step.FindLarger))
            )
                next = pick;
            if (wrap == null || Ahead(metric, wrap.Metric, !step.FindLarger))
                wrap = pick;
        }
        return new Candidates(next, wrap);
    }

    /// <summary>Where the walk lands: the nearest next one, or the first one when nothing is past the current one.</summary>
    internal static Pick Choose(IEnumerable<Candidates> candidates, CycleStep step)
    {
        Candidates best = Best(candidates.SelectMany(c => new[] { c?.Next, c?.Wrap }), step);
        return best.Next ?? best.Wrap;
    }

    /// <summary>Game thread: this node's candidates among the entities <paramref name="include"/> lets through.</summary>
    internal static Candidates CycleCandidates(CycleStep step, Func<MyEntity, bool> include)
    {
        var current = new MyEntityCycling.Metric(step.Metric, step.EntityId);
        MyEntityCyclingOrder order = step.CyclingOrder;
        MyEntity next = null,
            wrap = null;
        MyEntityCycling.Metric nextMetric = default,
            wrapMetric = default;
        foreach (MyEntity entity in MyEntities.GetEntities())
        {
            var grid = entity as MyCubeGrid;
            if (step.Skips(grid != null, grid?.GridSizeEnum == MyCubeSize.Large))
                continue;
            var metric = new MyEntityCycling.Metric(
                MyEntityCycling.GetMetric(order, entity),
                entity.EntityId
            );
            if (metric.Value == 0f)
                continue;
            bool isNext =
                Ahead(metric, current, step.FindLarger)
                && (next == null || Ahead(metric, nextMetric, !step.FindLarger));
            bool isWrap = wrap == null || Ahead(metric, wrapMetric, !step.FindLarger);
            if ((!isNext && !isWrap) || !include(entity))
                continue;
            if (isNext)
                (next, nextMetric) = (entity, metric);
            if (isWrap)
                (wrap, wrapMetric) = (entity, metric);
        }
        return new Candidates(
            next == null ? null : PickOf(next, nextMetric.Value),
            wrap == null ? null : PickOf(wrap, wrapMetric.Value)
        );
    }

    private static Pick PickOf(MyEntity entity, float value)
    {
        Vector3D position = entity.PositionComp.GetPosition();
        return new Pick(
            value,
            entity.EntityId,
            position.X,
            position.Y,
            position.Z,
            MySession
                .Static?.GetComponent<MySessionComponentEconomy>()
                ?.IsGridStation(entity.EntityId) == true,
            entity is MyVoxelBase voxel ? voxel.StorageName : entity.DisplayName
        )
        {
            Node = Node,
        };
    }

    /// <summary>Game thread, on the admin's node: an entity here that is not a copy of another node's.</summary>
    internal static bool CyclesHere(MyEntity entity)
    {
        PluginEntityPlacement placement = PluginServices.Instance.LocateEntity(entity.EntityId);
        return placement.Residence != PluginEntityResidence.Unowned
            || placement.Partition == 0
            || ClusterRuntime.PartitionClaimPending(placement.Partition);
    }

    // Game thread, on every other node: this node's candidates among the entities it owns
    private static Task<byte[]> OnCycle(PluginMessage message)
    {
        if (message.Source == Node)
            return Task.FromResult(Array.Empty<byte>());
        CycleStep step = JsonSerializer.Deserialize<CycleStep>(message.Payload);
        Candidates candidates = CycleCandidates(step, entity => OwnedHere(entity.EntityId));
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(candidates));
    }

    /// <summary>The metric of an offline grid under the order, or 0 when it can't be told without loading the grid.
    /// An offline grid doesn't move, so it is static.</summary>
    internal static float OfflineMetric(MyEntityCyclingOrder order, OfflineGrid grid) =>
        order switch
        {
            MyEntityCyclingOrder.BiggestGrids => grid.Blocks,
            MyEntityCyclingOrder.StaticObjects => 1f,
            MyEntityCyclingOrder.OwnerLoginTime => OwnerLoginDays(grid.Owners),
            MyEntityCyclingOrder.BiggestDistanceFromPlayers => NearestPlayer(
                new Vector3D(grid.X, grid.Y, grid.Z),
                (float)Math.Sqrt(float.MaxValue)
            ),
            _ => 0f,
        };

    /// <summary>Vanilla's GetOwnerLoginTime: days since the first big owner's last login, 0 when unknown.</summary>
    private static float OwnerLoginDays(long[] owners)
    {
        if (owners is not { Length: > 0 })
            return 0f;
        MyIdentity identity = MySession.Static?.Players.TryGetIdentity(owners[0]);
        return identity == null
            ? 0f
            : (float)Math.Round((DateTime.Now - identity.LastLoginTime).TotalDays, 2);
    }

    private static bool OfflineOrder(MyEntityCyclingOrder order) =>
        order
            is MyEntityCyclingOrder.BiggestGrids
                or MyEntityCyclingOrder.StaticObjects
                or MyEntityCyclingOrder.OwnerLoginTime
                or MyEntityCyclingOrder.BiggestDistanceFromPlayers;

    /// <summary>The offline grids' candidates.</summary>
    internal static Candidates OfflineCandidates(CycleStep step, IEnumerable<OfflineGrid> grids) =>
        Best(
            grids
                .Where(grid => !step.Skips(true, grid.Large))
                .Select(grid => new Pick(
                    OfflineMetric(step.CyclingOrder, grid),
                    grid.Id,
                    grid.X,
                    grid.Y,
                    grid.Z,
                    false,
                    grid.Name
                )
                {
                    Node = OfflineTag,
                }),
            step
        );

    /// <summary>Any thread: the cluster-wide pick from this node's candidates, the other nodes' and the offline
    /// grids'. A pick from elsewhere is remembered, so a row action on it is forwarded or skipped with a note.</summary>
    internal static async Task<CycleResult> CycleAsync(CycleStep step, Candidates local)
    {
        Task<OfflineGrids> offline = OfflineOrder(step.CyclingOrder)
            ? OfflineGridsAsync(null)
            : null;
        PluginBroadcastResult result = await BroadcastAsync(
                CycleTopic,
                JsonSerializer.SerializeToUtf8Bytes(step)
            )
            .ConfigureAwait(false);
        var all = new List<Candidates> { local };
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
            Candidates got = JsonSerializer.Deserialize<Candidates>(answer.Value.Payload);
            all.Add(
                new Candidates(
                    got?.Next is { } next ? next with { Node = answer.Key } : null,
                    got?.Wrap is { } wrap ? wrap with { Node = answer.Key } : null
                )
            );
        }
        if (offline != null)
        {
            try
            {
                OfflineGrids answer = await offline.ConfigureAwait(false);
                if (answer.Unknown > 0)
                    missing.Add(UnindexedTag);
                all.Add(OfflineCandidates(step, answer.Grids));
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
        Pick pick = Choose(all, step);
        if (pick != null && pick.Node != Node)
            Remember(pick.Id, new Vector3D(pick.X, pick.Y, pick.Z), pick.Node);
        return new CycleResult(pick, missing.ToArray());
    }

    /// <summary>The chat line for a pick the admin's client may not have: its name label stays on the last one.</summary>
    internal static string CycleLine(Pick pick) =>
        pick == null || pick.Node == Node
            ? null
            : FormattableString.Invariant(
                $"Cycle Objects: {pick.Name} [{pick.Node}], value {pick.Value:0.##}, at {pick.X:0} {pick.Y:0} {pick.Z:0}"
            );

    // ---- players' distance -------------------------------------------------------------------------------------------

    /// <summary>The distance from <paramref name="position"/> to the nearest player anywhere in the cluster, or
    /// <paramref name="vanilla"/> (the nearest player on this node) when that is nearer. Vanilla only sees this node's
    /// players: the others are phantoms without a character (SE-1687).</summary>
    internal static float NearestPlayer(Vector3D position, float vanilla)
    {
        double best = (double)vanilla * vanilla;
        foreach (ClusterPlayerPosition player in PositionRegistryService.Players)
        {
            if (player.ClientId == 0)
                continue;
            double dx = player.X - position.X,
                dy = player.Y - position.Y,
                dz = player.Z - position.Z;
            double squared = dx * dx + dy * dy + dz * dz;
            if (squared < best)
                best = squared;
        }
        return (float)Math.Min(vanilla, Math.Sqrt(best));
    }
}
