using System.Text.Json;
using PluginSdk.Clustering;

namespace ClusterRegistry;

internal sealed record PluginLifecycleOutcome(string Node, long Epoch, string Action, bool SaveFirst, DateTimeOffset CreatedAt);

internal sealed partial class GatewayRegistry
{
    internal PluginResult PluginOperation(PluginRequest request)
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            if (request is null || request.Protocol != 1 || !PluginRecordStore.ValidName(request.Plugin))
                return PluginResult.Failure(PluginResultCode.Invalid);
            if (request.Action is "stop" or "restart" && _state.PluginLifecycles.TryGetValue(request.OperationId, out var outcome))
                return PluginResult.Failure(outcome.Node == request.Node && outcome.Epoch == request.Incarnation
                    && outcome.Action == request.Action && outcome.SaveFirst == request.SaveFirst
                        ? PluginResultCode.Success : PluginResultCode.Conflict);
            if (!PluginNodeReady(request.Node, request.Incarnation)) return PluginResult.Failure(PluginResultCode.Fenced);
            if (request.Action == "owner") return ResolvePluginOwner(request.Target);
            if (request.Action == "validate") return ValidatePluginDispatch(request);
            if (request.Action == "nodes") return PluginNodes();
            var replay = _state.PluginStore.Replay(request);
            if (replay is not null) return replay;
            if (request.Fence is not null && (!ValidPluginFence(request.Fence) || request.Fence.Node != request.Node
                || request.Fence.Incarnation != request.Incarnation)) return PluginResult.Failure(PluginResultCode.Fenced);
            if (request.Action is "stop" or "restart")
            {
                if (!request.SaveFirst) return PluginResult.Failure(PluginResultCode.Unsupported);
                if (request.OperationId == Guid.Empty) return PluginResult.Failure(PluginResultCode.Invalid);
                // WA replacement requires a cluster lifecycle operation; never turn a local request into a global shutdown.
                if (_state.Nodes[request.Node].Role == NodeRole.WA) return PluginResult.Failure(PluginResultCode.Unsupported);
                var slot = _state.NodePlan.Values.SingleOrDefault(s => s.ObservedNode == request.Node);
                if (slot is null) return PluginResult.Failure(PluginResultCode.Unavailable);
                slot.IncumbentNode = request.Node; slot.IncumbentEpoch = request.Incarnation; slot.IncumbentAction = NodeIncumbentAction.Draining;
                slot.Desired = request.Action == "restart" ? NodeDesiredState.Wanted : NodeDesiredState.Draining;
                if (_state.PluginLifecycles.Count >= 128)
                    _state.PluginLifecycles.Remove(_state.PluginLifecycles.OrderBy(p => p.Value.CreatedAt).First().Key);
                _state.PluginLifecycles[request.OperationId] = new(request.Node, request.Incarnation, request.Action, request.SaveFirst, _clock());
                // CloseNode persists this entire mutation, including its replay outcome, in one WAL entry.
                CloseNode(request.Node, request.Incarnation); Flush();
                return PluginResult.Failure(PluginResultCode.Success);
            }
            if (request.Action == "cas" && !AcceptPluginWrite(request.Plugin))
                return PluginResult.Failure(PluginResultCode.Capacity);
            if (request.Action == "cas") EnsurePluginBaseline();
            string[] replays = request.Action == "cas" ? _state.PluginStore.Replays.Keys.ToArray() : [];
            var result = _state.PluginStore.Execute(request);
            if (request.Action == "cas" && result.Code == PluginResultCode.Success) PersistPluginWrite(request, replays);
            return result;
        }
    }

    private bool PluginNodeReady(string? node, long epoch) => node is not null && _state.Nodes.TryGetValue(node, out var item)
        && item.Epoch == epoch && item.State == NodeState.Active && item.LeaseExpires > _clock() && item.Serving && ManagedNodeReady(item);

    private PluginResult ResolvePluginOwner(PluginTarget? target)
    {
        if (target is null || !Enum.IsDefined(target.Kind)) return PluginResult.Failure(PluginResultCode.Invalid);
        string? node; long epoch, generation;
        switch (target.Kind)
        {
            case PluginTargetKind.Node: node = target.Node; epoch = target.Incarnation; generation = epoch; break;
            case PluginTargetKind.WorldAuthority:
                var wa = _state.WorldAuthority;
                if (wa.LeaseExpires <= _clock()) return PluginResult.Failure(PluginResultCode.Unavailable);
                node = wa.Node; epoch = wa.Epoch; generation = wa.Generation; break;
            default:
                if (!_state.Partitions.TryGetValue(target.Partition, out var partition) || partition.Recovering
                    || partition.LeaseExpires <= _clock()) return PluginResult.Failure(PluginResultCode.Unavailable);
                node = partition.Node; epoch = partition.Epoch; generation = partition.Generation; break;
        }
        if (!PluginNodeReady(node, epoch)) return PluginResult.Failure(PluginResultCode.Unavailable);
        return new PluginResult { Code = PluginResultCode.Success, Owner = new OwnerFence { StoreId = _state.PluginStore.StoreId,
            Target = target, Node = node, Incarnation = epoch, Generation = generation } };
    }

    /// <summary>
    /// T-0256 broadcast roster: every node a plugin message can be delivered to now, each with the Node-target fence
    /// its copy is addressed to. The payload is JSON [{Node, Incarnation, Role, Fence}] (PluginSdk PluginNodeInfo);
    /// the protocol records are unchanged, so a broadcast stays N fenced unicast messages.
    /// </summary>
    private PluginResult PluginNodes()
    {
        var nodes = new List<object>();
        foreach (var (name, node) in _state.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            var owner = ResolvePluginOwner(new PluginTarget { Kind = PluginTargetKind.Node, Node = name, Incarnation = node.Epoch });
            if (owner.Code != PluginResultCode.Success) continue;
            nodes.Add(new { Node = name, Incarnation = node.Epoch, Role = node.Role == NodeRole.WA ? "WorldAuthority" : "Regular",
                Fence = owner.Owner });
        }
        return new PluginResult { Code = PluginResultCode.Success, Payload = JsonSerializer.SerializeToUtf8Bytes(nodes) };
    }

    private PluginResult ValidatePluginDispatch(PluginRequest request)
    {
        var fence = request.Fence;
        if (!ValidPluginFence(fence) || fence!.Node != request.Node || fence.Incarnation != request.Incarnation)
            return PluginResult.Failure(PluginResultCode.Fenced);
        var expires = _state.Nodes[fence.Node].LeaseExpires;
        if (fence.Target.Kind == PluginTargetKind.WorldAuthority && _state.WorldAuthority.LeaseExpires < expires)
            expires = _state.WorldAuthority.LeaseExpires;
        if (fence.Target.Kind == PluginTargetKind.Partition && _state.Partitions[fence.Target.Partition].LeaseExpires < expires)
            expires = _state.Partitions[fence.Target.Partition].LeaseExpires;
        return new PluginResult { Code = PluginResultCode.Success, Owner = fence,
            ValidForMilliseconds = Math.Max(0, Math.Min(250, (expires - _clock()).TotalMilliseconds)) };
    }

    private bool ValidPluginFence(OwnerFence? fence)
    {
        if (fence is null || fence.StoreId != _state.PluginStore.StoreId) return false;
        var result = ResolvePluginOwner(fence.Target);
        return result.Code == PluginResultCode.Success && result.Owner.Node == fence.Node
            && result.Owner.Incarnation == fence.Incarnation && result.Owner.Generation == fence.Generation;
    }

    internal NodeRegistration? PluginMessageTarget(PluginMessage message, string sourceEndpoint, out PluginResultCode? rejection)
    {
        lock (_lock)
        {
            rejection = null; // Malformed or spoofed messages get no reply.
            if (message.Protocol != 1 || message.Reply || message.Id == Guid.Empty || message.OperationId == Guid.Empty
                || !PluginRecordStore.ValidName(message.Plugin) || !PluginRecordStore.ValidName(message.Topic)
                || (message.Payload?.Length ?? 0) > PluginRecordStore.MaxPayload || message.Destination?.Node is null
                || message.Source is null || !_state.Nodes.TryGetValue(message.Source, out var source)
                || source.Epoch != message.Incarnation || source.Endpoint != sourceEndpoint) return null;
            if (!PluginNodeReady(message.Source, message.Incarnation) || !ValidPluginFence(message.Destination))
            { rejection = PluginResultCode.Fenced; return null; }
            return _state.Nodes[message.Destination.Node];
        }
    }

    internal bool PluginReplyValid(PluginMessage reply, string sourceEndpoint, PluginMessage request, out PluginResultCode? rejection)
    {
        lock (_lock)
        {
            rejection = null;
            if (reply.Protocol != 1 || !reply.Reply || reply.Plugin != request.Plugin || reply.Topic != request.Topic
                || reply.OperationId != request.OperationId || reply.Id != request.Id || (reply.Payload?.Length ?? 0) > PluginRecordStore.MaxPayload
                || !Enum.IsDefined(reply.Code) || reply.Source != request.Destination.Node || reply.Incarnation != request.Destination.Incarnation
                || !_state.Nodes.TryGetValue(request.Destination.Node, out var source)
                || source.Endpoint != sourceEndpoint) return false;
            if (source.Epoch != reply.Incarnation || !ValidPluginFence(request.Destination) || !PluginNodeReady(request.Source, request.Incarnation))
            { rejection = PluginResultCode.Fenced; return false; }
            return true;
        }
    }

}

internal sealed partial class DirectGateway
{
    private readonly object _pluginSync = new();
    private readonly Dictionary<Guid, (PluginMessage Request, NodeLink Source, DateTimeOffset Expires)> _pluginRequests = [];

    private void HandlePluginOperation(System.Net.HttpListenerContext context, string path)
    {
        if (context.Request.HttpMethod != "POST" || !NodeAuthorized(context, path))
        { WriteJson(context, 403, new { error = "node credential required" }); return; }
        // Reject chunked/unbounded input before deserialization; clients supply a fixed content length.
        if (context.Request.ContentLength64 is <= 0 or > 128 * 1024)
        { WriteJson(context, 413, new { error = "plugin request exceeds limit" }); return; }
        var result = _registry?.PluginOperation(ReadJson<PluginRequest>(context)) ?? PluginResult.Failure(PluginResultCode.Unavailable);
        WriteJson(context, 200, result, redact: false);
    }

    private void RoutePluginMessage(NodeLink source, byte[] payload)
    {
        if (_registry is null || payload.Length > 128 * 1024) return;
        PluginMessage? message;
        try { message = JsonSerializer.Deserialize<PluginMessage>(payload); }
        catch (JsonException) { return; }
        if (message is null) return;
        lock (_pluginSync)
        {
            foreach (var key in _pluginRequests.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
                _pluginRequests.Remove(key);
            if (message.Reply)
            {
                if (!_pluginRequests.TryGetValue(message.Id, out var pending)) return;
                if (!_registry.PluginReplyValid(message, source.Endpoint.ToString(), pending.Request, out var rejection))
                {
                    if (rejection is not null)
                    { _pluginRequests.Remove(message.Id); RejectPlugin(pending.Source, pending.Request, rejection.Value); }
                    return;
                }
                _pluginRequests.Remove(message.Id);
                SendPlugin(pending.Source, payload);
                return;
            }
            var destination = _registry.PluginMessageTarget(message, source.Endpoint.ToString(), out var failure);
            if (destination is null)
            { if (failure is not null) RejectPlugin(source, message, failure.Value); return; }
            if (_pluginRequests.ContainsKey(message.Id)) return; // Never cancel another request's correlation.
            if (_pluginRequests.Count >= 128) { RejectPlugin(source, message, PluginResultCode.Capacity); return; }
            _pluginRequests.Add(message.Id, (message, source, DateTimeOffset.UtcNow.AddSeconds(30)));
            if (!SendPlugin(GetNodeLink(EndpointParser.Parse(destination.Endpoint, DirectTransportConstants.DefaultPort)), payload))
            { _pluginRequests.Remove(message.Id); RejectPlugin(source, message, PluginResultCode.Unavailable); }
        }
    }

    private static void RejectPlugin(NodeLink source, PluginMessage request, PluginResultCode code)
    {
        var reply = new PluginMessage { Id = request.Id, OperationId = request.OperationId, Plugin = request.Plugin,
            Topic = request.Topic, Reply = true, Source = request.Destination.Node, Incarnation = request.Destination.Incarnation,
            Destination = request.Destination, Code = code };
        SendPlugin(source, JsonSerializer.SerializeToUtf8Bytes(reply));
    }

    private static bool SendPlugin(NodeLink link, byte[] payload)
    {
        if (link.External)
        {
            // T-0229: link.Peer is the in-process relay's; gateway-rs holds this link.
            if (!link.ExternalConnected) return false;
            SendExternalNodeLink(link, new NodeLinkPacket(NodeLinkMessage.Plugin, 0, 0, payload, LiteNetLib.DeliveryMethod.ReliableUnordered));
            return true;
        }
        lock (link.Sync)
        {
            if (link.Peer?.ConnectionState != LiteNetLib.ConnectionState.Connected) return false;
            link.Peer.Send(DirectTransportCodec.EncodeNodeLink(new NodeLinkPacket(NodeLinkMessage.Plugin, 0, 0, payload, LiteNetLib.DeliveryMethod.ReliableUnordered)), 0, LiteNetLib.DeliveryMethod.ReliableUnordered);
            return true;
        }
    }
}
