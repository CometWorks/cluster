using System.Net;
using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// SE-810: one grid of an offline partition as it was at the freeze. Nothing can change it while the partition is offline, so
/// plugins can answer "whose grids are where" without loading anything: Essentials' grid list, Octahedron's salvage contracts.
/// Owners = the grid's big owners. A null fact is unknown: a row read back from a Final has no live blocks to ask.
/// Group = the smallest entity id of the grid's physical group (connectors and landing gear too). Powered = a functional
/// working power source (a charged battery, a fueled producer, any other). Functional = a functional terminal block.
/// Station = a registered faction station.
/// </summary>
internal sealed record OfflineGrid(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("l")] bool Large,
    [property: JsonPropertyName("s")] bool Static,
    [property: JsonPropertyName("b")] int Blocks,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("o")] long[] Owners,
    [property: JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MassKg = null,
    [property: JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Group = null,
    [property: JsonPropertyName("f"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Functional = null,
    [property: JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Powered = null,
    [property: JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Station = null);

/// <summary>A grid with the offline row it is in, as GET /registry/partitions/offline/grids answers.</summary>
internal sealed record OfflineGridRow(
    [property: JsonPropertyName("partition")] ulong Partition,
    [property: JsonPropertyName("offlineAt")] DateTimeOffset OfflineAt,
    [property: JsonPropertyName("grid")] OfflineGrid Grid);

/// <summary>Unknown = offline rows whose grids aren't known yet, so an answer can say it is incomplete.</summary>
internal sealed record OfflineGridsAnswer(
    [property: JsonPropertyName("unknown")] int Unknown,
    [property: JsonPropertyName("grids")] OfflineGridRow[] Grids);

internal sealed record OfflineGridsRequest(ulong Partition, long Generation, long Sequence, OfflineGrid[] Grids);

internal sealed partial class GatewayRegistry
{
    /// <summary>The grids in offline partitions, of one big owner or all. A row a load has claimed is still listed until it
    /// drops: the caller prefers what the nodes report for the same id.</summary>
    public OfflineGridsAnswer OfflineGrids(long? owner)
    {
        lock (_lock)
            return new OfflineGridsAnswer(
                _state.OfflinePartitions.Values.Count(row => row.Grids == null),
                [.. _state.OfflinePartitions.Values.Where(row => row.Grids != null).SelectMany(row => row.Grids!
                    .Where(grid => owner == null || grid.Owners.Contains(owner.Value))
                    .Select(grid => new OfflineGridRow(row.Partition, row.OfflineAt, grid)))]);
    }

    /// <summary>The saves of the next rows whose grids aren't known, after a partition id: the caller walks them round.</summary>
    public SaveId[] OfflineGridsMissing(ulong after, int limit)
    {
        lock (_lock)
        {
            SaveId[] missing = [.. _state.OfflinePartitions.Values.Where(row => row.Grids == null).Select(row => row.Save).OrderBy(x => x.Partition)];
            return [.. missing.Where(x => x.Partition > after).Concat(missing.Where(x => x.Partition <= after)).Take(limit)];
        }
    }

    /// <summary>A node read the grids of a row from its Final. A row that has since woken, or gone offline again, stays as it is.</summary>
    public bool SetOfflineGrids(OfflineGridsRequest request)
    {
        lock (_lock)
        {
            if (!_state.OfflinePartitions.TryGetValue(request.Partition, out OfflinePartition? row)
                || row.Grids != null || row.Save != new SaveId(request.Partition, request.Generation, request.Sequence))
                return false;
            row.Grids = request.Grids;
            Persist();
            return true;
        }
    }
}

internal sealed partial class DirectGateway
{
    private void HandleOfflineGridRoutes(HttpListenerContext context, string path)
    {
        if (context.Request.HttpMethod == "GET" && path.Equals("/registry/partitions/offline/grids", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(context, 200, _registry!.OfflineGrids(context.Request.QueryString["owner"] is { } owner ? long.Parse(owner) : null));
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals("/registry/partitions/offline/grids", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(context, 200, new { set = _registry!.SetOfflineGrids(ReadJson<OfflineGridsRequest>(context)) });
            return;
        }
        if (context.Request.HttpMethod == "GET" && path.Equals("/registry/partitions/offline/grids/missing", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(context, 200, _registry!.OfflineGridsMissing(
                ulong.Parse(context.Request.QueryString["after"] ?? "0"), int.Parse(context.Request.QueryString["limit"] ?? "1")));
            return;
        }
        WriteJson(context, 404, new { error = "no such route" });
    }
}
