using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed record LobbyIntent(string Operation, Admin.LobbyUpdate Update, long Grid, string Node, long Epoch, DateTimeOffset Started, bool Dispatched = false);
internal sealed record LobbyNodeReply(string Status, long GridEntityId, ulong Partition, long Generation,
    string? Name, double X, double Y, double Z, string? Reason);

internal sealed partial class GatewayRegistry
{
    internal LobbyIntent? PendingLobby { get { lock (_lock) return _state.LobbyIntent; } }

    internal Admin.LobbyStatus LobbyStatus()
    {
        lock (_lock)
        {
            var anchor = _state.LobbyAnchor;
            if (anchor == null) return new(_state.LobbyRevision, null, null, "unconfigured", _state.LobbyIntent?.Operation);
            ulong partition = LivePartition(anchor.Partition, anchor.GridId);
            var owner = _state.Partitions.GetValueOrDefault(partition);
            string? node = owner != null && !owner.Recovering && owner.LeaseExpires > _clock()
                ? owner.Node : null;
            string state = node != null ? "assigned" : _state.OfflinePartitions.ContainsKey(partition) ? "offline" : "unavailable";
            return new(_state.LobbyRevision, anchor with { Partition = partition }, node, state, _state.LobbyIntent?.Operation);
        }
    }

    internal void ReserveLobby(AdminOperationRecord operation, Admin.LobbyUpdate update)
    {
        lock (_lock)
        {
            if (operation.State != AdminOperationPhase.Running || _state.LobbyIntent?.Operation == operation.OperationId) return;
            if (ShuttingDown)
                FailAdminOperation(operation.OperationId, "cluster_stopping", "Wait until the cluster is serving again.");
            else if (_state.LobbyRevision != update.ExpectedRevision)
                FailAdminOperation(operation.OperationId, "revision_mismatch", "The lobby changed. Refresh before submitting.");
            else if (_state.LobbyIntent != null)
                FailAdminOperation(operation.OperationId, "lobby_busy", "Another lobby change is in progress.");
            else
            {
                var nodes = _state.Nodes.Values.Where(n => n.State == NodeState.Active && n.Serving
                    && !n.Spare && n.Role == NodeRole.Regular && n.LeaseExpires > _clock() && n.ControlEndpoint != null);
                string? owner = update.Partition is ulong partition ? _state.Partitions.GetValueOrDefault(partition)?.Node : null;
                var node = update.Partition.HasValue ? nodes.FirstOrDefault(n => n.Node == owner)
                    : nodes.OrderBy(n => n.ProjectedLoad).ThenBy(n => n.Node, StringComparer.Ordinal).FirstOrDefault();
                if (node == null)
                    FailAdminOperation(operation.OperationId, "node_unavailable", "No serving node is available for this station.");
                else
                {
                    // Reserve a unique typed entity ID before dispatch. Nodes never consume this dedicated range.
                    long grid = update.BlueprintXml == null ? update.GridId ?? 0 : (1L << 56) | AllocateEntityIdRange(node.Node, node.Epoch).Start;
                    _state.LobbyIntent = new(operation.OperationId, update, grid, node.Node, node.Epoch, _clock());
                }
            }
            Persist();
            Flush();
        }
    }

    internal LobbyIntent MarkLobbyDispatched(LobbyIntent intent)
    {
        lock (_lock)
        {
            if (_state.LobbyIntent != intent || intent.Dispatched)
                throw new RegistryConflictException("Lobby placement was already dispatched.");
            _state.LobbyIntent = intent with { Dispatched = true };
            Persist();
            Flush();
            return _state.LobbyIntent;
        }
    }

    internal bool AcceptLobby(LobbyIntent intent, NodeRegistration node, LobbyNodeReply reply)
    {
        lock (_lock)
        {
            if (_state.LobbyIntent != intent || reply.GridEntityId <= 0 || (intent.Grid != 0 && reply.GridEntityId != intent.Grid) || reply.Partition == 0
                || !double.IsFinite(reply.X) || !double.IsFinite(reply.Y) || !double.IsFinite(reply.Z)
                || !_state.Partitions.TryGetValue(reply.Partition, out var owner) || owner.Recovering
                || IsRetired(reply.Partition) || node.Quarantined.Contains(reply.Partition)
                || (intent.Update.Partition is ulong initial && LivePartition(initial, reply.GridEntityId) != reply.Partition)
                || owner.Node != node.Node || owner.Epoch != node.Epoch || owner.Generation != reply.Generation
                || owner.LeaseExpires <= _clock() || _state.OfflinePartitions.ContainsKey(reply.Partition)) return false;
            _state.LobbyAnchor = new(reply.GridEntityId, reply.Partition, reply.Name ?? "", new(reply.X, reply.Y, reply.Z));
            _state.LobbyRevision++;
            _state.LobbyIntent = null;
            CompleteAdminOperation(intent.Operation, new Dictionary<string,string>
            {
                ["gridId"] = reply.GridEntityId.ToString(), ["partition"] = reply.Partition.ToString(),
                ["revision"] = _state.LobbyRevision.ToString()
            });
            Persist();
            Flush();
            return true;
        }
    }

    internal void RejectLobby(LobbyIntent intent, string code, string reason)
    {
        lock (_lock)
        {
            if (_state.LobbyIntent != intent) return;
            _state.LobbyIntent = null;
            FailAdminOperation(intent.Operation, code, reason);
            Persist();
            Flush();
        }
    }
}

internal sealed partial class DirectGateway
{
    private void AdminSetLobby(HttpListenerContext context, AdminActor actor)
    {
        if (!RequireManage(context, actor, "lobby-set")) return;
        // JSON escaping can consume six bytes per input character. Bound chunked requests as well.
        const int max = Admin.LobbyUpdate.MaxBlueprintBytes * 6 + 4096;
        using var body = new MemoryStream();
        var chunk = new byte[16384];
        int count;
        while ((count = context.Request.InputStream.Read(chunk)) > 0)
        {
            if (body.Length + count > max)
            {
                WriteAdminError(context, 413, "lobby_request_too_large", "Lobby blueprint exceeds the upload limit.");
                return;
            }
            body.Write(chunk, 0, count);
        }
        Admin.LobbyUpdate? update;
        try { update = JsonSerializer.Deserialize<Admin.LobbyUpdate>(body.GetBuffer().AsSpan(0, (int)body.Length), AdminJson); }
        catch (JsonException) { update = null; }
        if (update == null || update.ValidationError() != null)
        {
            WriteAdminError(context, 400, "invalid_lobby", update?.ValidationError() ?? "Invalid lobby request.");
            return;
        }
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(update, AdminJson)));
        var record = BeginMutation(context, actor, "lobby-set", hash, null, out _);
        if (record == null) return;
        _registry!.ReserveLobby(record, update);
        WriteOperation(context, record);
    }

    private async Task<LobbyNodeReply> LobbyNodeRequest(NodeRegistration node, LobbyIntent intent, bool create, ulong partition)
    {
        var pos = intent.Update.Position;
        var request = new { mode = create ? "create" : "inspect", node = node.Node, epoch = node.Epoch,
            gridEntityId = intent.Grid, partition, x = pos?.X ?? 0, y = pos?.Y ?? 0, z = pos?.Z ?? 0,
            blueprint = create ? Convert.ToBase64String(Encoding.UTF8.GetBytes(intent.Update.BlueprintXml!)) : null };
        string url = ClusterUrl(node.ControlEndpoint!.Replace("http://", string.Empty), "lobby-anchor");
        // HttpListener requires a known length for this endpoint.
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, AdminJson));
        content.Headers.ContentType = new("application/json");
        using var response = await _http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LobbyNodeReply>()
            ?? throw new InvalidDataException("Empty lobby response");
    }

    private bool ClearLobbySite(Admin.LobbyPosition position)
    {
        // Protect a remote station site, including stations asleep on another host.
        // A lobby station fits within 1 km of its anchor; reserve another 100 km around it.
        var site = new PartitionSphere(position.X, position.Y, position.Z, 101_000);
        var nodes = _registry!.Nodes().Where(n => n.Role == NodeRole.Regular && n.Serving
            && n.State is NodeState.Active or NodeState.Closed && n.LeaseExpires > DateTimeOffset.UtcNow).ToArray();
        var seen = new HashSet<ulong>();
        foreach (var node in nodes)
        {
            if (node.ControlEndpoint == null) return false;
            foreach (var part in GetPartitions(StripScheme(node.ControlEndpoint)).Partitions)
            {
                if (!(SphereGap(site, part.Sphere) > 0)) return false;
                seen.Add(part.Id);
            }
        }
        foreach (var row in _registry.OfflineGeometry())
        {
            if (!(SphereGap(site, new(row.X, row.Y, row.Z, row.Radius)) > 0)) return false;
            seen.Add(row.Partition);
        }
        return _registry.AdminPartitions().All(p => p.Retired || seen.Contains(p.Partition));
    }

    private async Task RunLobbyWorker()
    {
        while (_running)
        {
            var intent = _registry?.PendingLobby;
            try
            {
                if (intent != null) await ReconcileLobby(intent);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Lobby reconciliation: " + SecretRedactor.Redact(error.Message));
            }
            await Task.Delay(TimeSpan.FromSeconds(intent == null ? 3 : 2));
        }
    }

    private async Task ReconcileLobby(LobbyIntent intent)
    {
        var registry = _registry!;
        var nodes = registry.Nodes().Where(n => n.Role == NodeRole.Regular && n.Serving && n.ControlEndpoint != null
            && n.State == NodeState.Active && n.LeaseExpires > DateTimeOffset.UtcNow).ToArray();
        if (DateTimeOffset.UtcNow - intent.Started > TimeSpan.FromMinutes(5))
        {
            string reason = intent.Dispatched
                ? $"Could not confirm placement of grid {intent.Grid}. Inspect it before starting a new creation."
                : "Could not reach a serving owner before the operation timed out.";
            registry.RejectLobby(intent, intent.Dispatched ? "placement_unconfirmed" : "owner_unavailable", reason);
            return;
        }
        bool creating = intent.Update.BlueprintXml != null;
        ulong partition = intent.Update.Partition is ulong seed ? registry.LivePartitionOf(seed, intent.Grid) : 0;
        if (!creating)
        {
            var owner = registry.GetPartitionOwner(partition);
            var node = nodes.FirstOrDefault(n => n.Node == owner.Node && n.Epoch == owner.Epoch);
            if (node == null) return;
            var view = await LobbyNodeRequest(node, intent, false, partition);
            if (view.Status == "ready") registry.AcceptLobby(intent, node, view);
            else if (view.Status is "missing" or "invalid" or "conflict")
                registry.RejectLobby(intent, "invalid_station", view.Reason ?? "The selected station is unavailable.");
            return;
        }
        // A lost reply may mean the station is already saved or migrated. Only the originally fenced
        // node may materialize the reserved ID. Every other node may only report an existing station.
        var original = nodes.FirstOrDefault(n => n.Node == intent.Node && n.Epoch == intent.Epoch);
        if (intent.Dispatched)
        {
            foreach (var node in nodes)
            {
                try
                {
                    var seen = await LobbyNodeRequest(node, intent, false, 0);
                    if (seen.Status == "ready" && registry.AcceptLobby(intent, node, seen)) return;
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                    or JsonException or InvalidDataException)
                {
                    // An unavailable peer must not prevent inspection of the node holding the station.
                    Console.Error.WriteLine($"Lobby inspection on {node.Node}: " + SecretRedactor.Redact(error.Message));
                }
            }
            return;
        }
        if (original == null || registry.ShuttingDown) return;
        if (!ClearLobbySite(intent.Update.Position!))
        {
            registry.RejectLobby(intent, "lobby_site_unavailable",
                "Choose an empty remote position at least 101 km from other partition bounds, with all partitions accounted for.");
            return;
        }
        intent = registry.MarkLobbyDispatched(intent);
        var result = await LobbyNodeRequest(original, intent, true, 0);
        if (result.Status == "ready") registry.AcceptLobby(intent, original, result);
        else if (result.Status is "invalid" or "conflict")
            registry.RejectLobby(intent, "lobby_creation_refused", result.Reason ?? "Station placement was refused.");
    }
}
