using System.Net;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    internal static Admin.OfflineGroupSummary[] OfflineGroups(JsonElement? summary) =>
        summary is { ValueKind: JsonValueKind.Array } value
            ? (value.Deserialize<Admin.OfflineGroupSummary[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [])
                .Select(g => g with { Members = g.Members ?? [], Unknowns = g.Unknowns ?? [], Observations = g.Observations ?? [] }).ToArray()
            : [];

    internal Admin.OfflinePartitionCatalog AdminOfflineCatalog()
    {
        lock (_lock) return new(_state.OfflinePartitions.Values.OrderBy(p => p.Partition).Select(p =>
            new Admin.OfflinePartitionSummary(p.Partition, $"{p.Save.Partition}:{p.Save.Generation}:{p.Save.Sequence}",
                p.OfflineBy, p.OfflineAt, p.OfflineGameFrame, p.EstimatedSnapshotBytes, p.LoadRequestedNode,
                p.LoadReason, OfflineHolders(p), OfflineGroups(p.ProfileSummary))).ToArray());
    }

    private string[] OfflineHolders(OfflinePartition row) => _state.Saves.TryGetValue(Key(row.Save), out var save)
        ? SaveHolders(save).Select(id => ProxyOf(id)?.Node ?? id).Distinct(StringComparer.Ordinal)
            .Where(id => _state.Nodes.TryGetValue(id, out var node) && node.State == NodeState.Active
                && node.LeaseExpires > _clock() && node.ControlEndpoint != null).ToArray() : [];

    internal (NodeRegistration Node, string Identity, bool Offline, string? SaveId, long Generation) AdminOfflineTarget(string nodeId, ulong partition, bool offlineOnly = false, bool onlineOnly = false)
    {
        lock (_lock)
        {
            if (!_state.Partitions.TryGetValue(partition, out var owner)) throw new OfflineAdminException(404, "partition_not_found");
            if (!_state.Nodes.TryGetValue(nodeId, out var node) || node.State != NodeState.Active || node.LeaseExpires <= _clock() || node.ControlEndpoint == null)
                throw new OfflineAdminException(503, "node_unavailable");
            if (_state.OfflinePartitions.TryGetValue(partition, out var row))
            {
                if (onlineOnly || owner.Node != null || !OfflineHolders(row).Contains(nodeId)) throw new OfflineAdminException(409, "offline_target_mismatch");
                string save = $"{row.Save.Partition}:{row.Save.Generation}:{row.Save.Sequence}";
                return (node, $"{node.Node}:{node.Epoch}:offline:{save}", true, save, row.Save.Generation);
            }
            if (offlineOnly) throw new OfflineAdminException(409, "partition_not_offline");
            if (owner.Node != nodeId || owner.Epoch != node.Epoch || owner.Recovering || owner.LeaseExpires <= _clock())
                throw new OfflineAdminException(409, "offline_target_mismatch");
            return (node, $"{node.Node}:{node.Epoch}:online:{owner.Generation}", false, null, owner.Generation);
        }
    }
}

internal sealed class OfflineAdminException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

internal sealed partial class DirectGateway
{
    private JsonElement OfflineNodeRequest(NodeRegistration node, string path, HttpMethod? method = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, ClusterUrl(node.ControlEndpoint!.Replace("http://", string.Empty), path));
            using var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound) throw new OfflineAdminException(501, "offline_unsupported");
            if (!response.IsSuccessStatusCode) throw new OfflineAdminException(502, "node_request_failed");
            using var document = JsonDocument.Parse(response.Content.ReadAsStream(timeout.Token));
            return document.RootElement.Clone();
        }
        catch (OfflineAdminException) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        { throw new OfflineAdminException(503, "node_unavailable"); }
    }

    private JsonElement OfflineNodeView(NodeRegistration node, ulong? partition = null, bool offline = false)
    {
        var value = OfflineNodeRequest(node, "offline/admin" + (partition.HasValue ? $"?partition={partition}&offline={offline.ToString().ToLowerInvariant()}" : ""));
        if (!value.TryGetProperty("capability", out var capability) || capability.GetString() != "v1")
            throw new OfflineAdminException(501, "offline_unsupported");
        return value;
    }

    private NodeRegistration OfflineAuthority()
    {
        var wa = _registry!.WorldAuthority();
        return _registry.Nodes().FirstOrDefault(n => n.Node == wa.Node && n.Epoch == wa.Epoch
            && n.State == NodeState.Active && n.LeaseExpires > DateTimeOffset.UtcNow && n.ControlEndpoint != null)
            ?? throw new OfflineAdminException(503, "node_unavailable");
    }

    private static (string Node, ulong Partition) OfflineTargetQuery(HttpListenerContext context)
    {
        string? node = context.Request.QueryString["node"];
        if (string.IsNullOrWhiteSpace(node) || !ulong.TryParse(context.Request.QueryString["partition"], out ulong partition) || partition == 0)
            throw new ArgumentException("An exact node id and positive partition are required");
        return (node, partition);
    }

    private bool HandleAdminOfflineRead(HttpListenerContext context, string path)
    {
        string prefix = Admin.AdminProtocol.RoutePrefix + "/offline/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            switch (path[prefix.Length..].ToLowerInvariant())
            {
                case "partitions": WriteAdminEnvelope(context, _registry!.AdminOfflineCatalog()); break;
                case "cache":
                    var authority = OfflineAuthority();
                    var cache = OfflineNodeView(authority).GetProperty("cache").GetProperty("entries").Deserialize<Admin.OfflineRateEntry[]>(AdminJson) ?? [];
                    if (OfflineAuthority().Epoch != authority.Epoch || OfflineAuthority().Node != authority.Node) throw new OfflineAdminException(409, "offline_target_mismatch");
                    WriteAdminEnvelope(context, new Admin.OfflineCacheView(authority.Node, authority.Epoch, cache));
                    break;
                case "details":
                case "prediction":
                    bool prediction = path.EndsWith("/prediction", StringComparison.OrdinalIgnoreCase);
                    var query = OfflineTargetQuery(context);
                    double hours = 0;
                    if (prediction && (!double.TryParse(context.Request.QueryString["hours"], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out hours) || !double.IsFinite(hours) || hours is < 0 or > 168))
                        throw new ArgumentException("hours must be finite and between 0 and 168");
                    var target = _registry!.AdminOfflineTarget(query.Node, query.Partition, offlineOnly: prediction);
                    var payload = OfflineNodeView(target.Node, query.Partition, target.Offline);
                    object result;
                    if (prediction)
                    {
                        var values = OfflineNodeRequest(target.Node, $"trigger/compensate-dry-run?partition={query.Partition}&hours={hours.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}&save={Uri.EscapeDataString(target.SaveId!)}", HttpMethod.Post)
                            .Deserialize<Admin.OfflineCompensationResult[]>(AdminJson) ?? [];
                        result = new Admin.OfflinePrediction(target.Node.Node, target.Node.Epoch, query.Partition, target.SaveId!, hours, values);
                    }
                    else result = new Admin.OfflinePartitionDetails(target.Node.Node, target.Node.Epoch, query.Partition, target.Offline, target.SaveId,
                        payload.GetProperty("groups").Deserialize<Admin.OfflineGroupSummary[]>(AdminJson) ?? [],
                        payload.GetProperty("results").Deserialize<Admin.OfflineCompensationResult[]>(AdminJson) ?? []);
                    if (_registry.AdminOfflineTarget(query.Node, query.Partition, offlineOnly: prediction).Identity != target.Identity)
                        throw new OfflineAdminException(409, "offline_target_mismatch");
                    WriteAdminEnvelope(context, result);
                    break;
                default: WriteAdminError(context, 404, "route_not_found", "Unknown offline admin route"); break;
            }
        }
        catch (OfflineAdminException e) { WriteAdminError(context, e.Status, e.Code, "Offline operation could not use the selected target."); }
        return true;
    }

    private void AdminOfflineProfile(HttpListenerContext context, AdminActor actor)
    {
        if (!RequireManage(context, actor, "profile")) return;
        var query = OfflineTargetQuery(context);
        AdminOperationRecord? record = BeginMutation(context, actor, "trigger", $"profile|{query.Node}|{query.Partition}", $"{query.Node}:{query.Partition}", out bool replayed);
        if (record == null) return;
        if (!replayed)
        {
            try
            {
                var target = _registry!.AdminOfflineTarget(query.Node, query.Partition, onlineOnly: true);
                OfflineNodeView(target.Node); // Older nodes must not accidentally accept a different trigger.
                var result = OfflineNodeRequest(target.Node, $"trigger/profile?partition={query.Partition}&generation={target.Generation}", HttpMethod.Post);
                _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string> { ["task"] = "profile",
                    ["node"] = query.Node, ["partition"] = query.Partition.ToString(), ["result"] = result.GetRawText() });
            }
            catch (OfflineAdminException e) { _registry!.FailAdminOperation(record.OperationId, e.Code, "Offline profile target unavailable or changed."); }
        }
        WriteOperation(context, record);
    }

    private void AdminOfflineCacheClear(HttpListenerContext context, AdminActor actor)
    {
        if (!RequireManage(context, actor, "offline-cache-clear")) return;
        var body = ReadAdminBody<Admin.OfflineCacheClearRequest>(context);
        if (body == null || string.IsNullOrEmpty(body.Key) || body.Key.Length > 4096 || body.ExpectedEpoch < 0)
            throw new ArgumentException("A cache key and non-negative expectedEpoch are required");
        AdminOperationRecord? record = BeginMutation(context, actor, "offline-cache-clear", Fingerprint(body), body.Key, out bool replayed);
        if (record == null) return;
        if (!replayed)
        {
            try
            {
                var authority = OfflineAuthority();
                OfflineNodeView(authority);
                var result = OfflineNodeRequest(authority, $"profiles/cache/clear?key={Uri.EscapeDataString(body.Key)}&expectedEpoch={body.ExpectedEpoch}&operation={Uri.EscapeDataString(record.OperationId)}", HttpMethod.Post);
                if (!result.GetProperty("cleared").GetBoolean()) _registry!.FailAdminOperation(record.OperationId, "epoch_mismatch", "Cache epoch changed; refresh before clearing.");
                else _registry!.CompleteAdminOperation(record.OperationId, new Dictionary<string, string> { ["key"] = body.Key, ["epoch"] = result.GetProperty("epoch").GetInt32().ToString() });
            }
            catch (OfflineAdminException e) { _registry!.FailAdminOperation(record.OperationId, e.Code, "Offline cache authority unavailable."); }
        }
        WriteOperation(context, record);
    }
}
