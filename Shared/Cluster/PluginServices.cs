using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk;
using PluginSdk.Clustering;

namespace ClusterPlugin;

internal sealed class PluginServices : IPluginClusterProvider, IPluginClusterBroadcastProvider, IPluginClusterViewProvider, IPluginClusterHandoverProvider, IPluginClusterOfflineProvider, IPluginClusterClockProvider, IPluginClusterEventProvider, IPluginGlobalCommandLedger, IClusterLifecycleProvider, IClusterNodeLink, IDisposable
{
    internal static PluginServices Instance;
    private readonly HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(5));
    private readonly PluginServiceDiagnostics diagnostics = new PluginServiceDiagnostics();
    private readonly PluginHandlers handlers = new PluginHandlers();
    private readonly ConcurrentDictionary<Guid, (PluginMessage Message, TaskCompletionSource<PluginResult> Completion)> pending = new();
    private bool disposed;
    private NodeContext previousContext;
    private int requests;
    private int receiving;
    private readonly string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
    private readonly string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
    public NodeContext Context => new NodeContext { ClusterId = Environment.GetEnvironmentVariable("CLUSTER_ID"), Node = node,
        WorldAuthorityGeneration = ClusterRuntime.AuthorityGeneration, OwnedPartitions = ClusterRuntime.PluginOwnershipSnapshot(),
        Incarnation = ClusterRuntime.RegistryNodeEpoch, Role = ClusterRuntime.IsWorldAuthority ? "WorldAuthority" : "Regular",
        Available = !disposed && ClusterRuntime.IsServing && !ClusterRuntime.IsAuthorityFenced
            && (string.IsNullOrEmpty(NodeProcessIdentity.Revision) || NodeProcessIdentity.Verified) };
    public event Action ContextChanged;
    public event Action<byte[]> MessageReceived;
    public event Action<bool> ConnectionChanged;
    public event Action<ulong> ClientDetached;
    public event Action<ClusterLifecycleAcknowledgement> LifecycleAcknowledged;
    public Func<ulong, byte[], bool> AttachValidator { get; set; }
    private Func<ulong, byte[], bool> originalValidator;
    public bool IsConnected => DirectTransportNodeLinkPatch.IsConnected;
    public bool Send(byte[] payload) => DirectTransportNodeLinkPatch.SendGlobal(payload);
    private void GlobalReceived(byte[] bytes) => MessageReceived?.Invoke(bytes);
    private void Detached(ulong id) => ClientDetached?.Invoke(id);
    private bool ValidateAttach(ulong id, byte[] bytes) => (originalValidator?.Invoke(id, bytes) ?? false) && (AttachValidator?.Invoke(id, bytes) ?? true);
    private void LinkChanged(bool connected)
    {
        if (!connected) FailPending();
        ConnectionChanged?.Invoke(connected);
    }
    internal static void Start()
    {
        var service = new PluginServices();
        ClusterRuntime.AuthorizeRegistryClient(service.client);
        if (!PluginCluster.Register(service) || !ClusterLifecycle.Register(service) || !ClusterNodeLink.Register(service))
        { service.Dispose(); throw new InvalidOperationException("Plugin cluster providers already registered."); }
        Instance = service;
        DirectTransportNodeLinkPatch.PluginMessageReceived += service.Receive;
        DirectTransportNodeLinkPatch.GlobalMessageReceived += service.GlobalReceived;
        DirectTransportNodeLinkPatch.GlobalLinkChanged += service.LinkChanged;
        DirectTransportNodeLinkPatch.AttachBindingRemoved += service.Detached;
        service.originalValidator = DirectTransportNodeLinkPatch.AttachBindingAccepted;
        DirectTransportNodeLinkPatch.AttachBindingAccepted = service.ValidateAttach;
    }
    public async Task<PluginResult> ExecuteAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        if (!Context.Available && (disposed || ClusterRuntime.RegistryNodeEpoch <= 0 || request.Action is not ("stop" or "restart"))) return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Unavailable));
        if (Interlocked.Increment(ref requests) > 128) { Interlocked.Decrement(ref requests); return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Capacity)); }
        try
        {
            request.Node = node; request.Incarnation = ClusterRuntime.RegistryNodeEpoch;
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(gateway + "/registry/plugins/v1", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Unavailable));
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 128 * 1024) return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Invalid));
            return diagnostics.Observe(JsonSerializer.Deserialize<PluginResult>(bytes) ?? PluginResult.Failure(PluginResultCode.Unavailable));
        }
        catch (OperationCanceledException) { return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Timeout)); }
        catch { return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Unavailable)); }
        finally { Interlocked.Decrement(ref requests); }
    }
    public IDisposable RegisterHandler(string plugin, string topic, Func<PluginMessage, Task<byte[]>> handler) => handlers.Register(plugin, topic, handler);
    public async Task<PluginResult> RequestAsync(string plugin, PluginTarget target, string topic, byte[] payload,
        Guid operationId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!PluginRecordStore.ValidName(plugin) || !PluginRecordStore.ValidName(topic) || operationId == Guid.Empty
            || (payload?.Length ?? 0) > PluginRecordStore.MaxPayload || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Invalid));
        var owner = await ExecuteAsync(new PluginRequest { Action = "owner", Plugin = plugin, Target = target }, cancellationToken).ConfigureAwait(false);
        if (owner.Code != PluginResultCode.Success) return owner;
        return await SendAsync(plugin, owner.Owner, topic, payload, operationId, timeout, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// T-0256 broadcast: the registry's roster of ready nodes ("nodes"), then one fenced unicast per node through the
    /// same node link, validation and handler path as <see cref="RequestAsync(string, PluginTarget, string, byte[], Guid, TimeSpan, CancellationToken)"/>.
    /// Every copy carries the caller's operation id.
    /// </summary>
    public async Task<PluginBroadcastResult> BroadcastAsync(string plugin, string topic, byte[] payload, Guid operationId,
        bool includeWorldAuthority, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!PluginRecordStore.ValidName(plugin) || !PluginRecordStore.ValidName(topic) || operationId == Guid.Empty
            || (payload?.Length ?? 0) > PluginRecordStore.MaxPayload || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            return PluginBroadcastResult.Failure(diagnostics.Observe(PluginResult.Failure(PluginResultCode.Invalid)).Code);
        var (code, nodes) = await RosterAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (code != PluginResultCode.Success) return PluginBroadcastResult.Failure(code);
        var targets = nodes.Where(n => includeWorldAuthority || n.Role != "WorldAuthority").ToArray();
        var sends = targets.Select(n => SendAsync(plugin, n.Fence, topic, payload, operationId, timeout, cancellationToken)).ToArray();
        var results = await Task.WhenAll(sends).ConfigureAwait(false);
        var byNode = new System.Collections.Generic.Dictionary<string, PluginResult>();
        for (int i = 0; i < targets.Length; i++) byNode[targets[i].Node] = results[i];
        var failed = results.FirstOrDefault(r => r.Code != PluginResultCode.Success);
        return new PluginBroadcastResult { Code = failed?.Code ?? PluginResultCode.Success, Nodes = byNode };
    }
    // T-0256 views: the services' published snapshots (any thread), and this node's partition layout (game thread).
    public IReadOnlyList<PluginPlayerInfo> OnlinePlayers() => OnlinePlayersService.Cluster
        .Select(p => new PluginPlayerInfo { Node = p.Node, SteamId = p.SteamId, SerialId = p.SerialId, IdentityId = p.IdentityId,
            Name = p.Name, IsAdmin = p.IsAdmin }).ToArray();
    // ClientId 0 entries are viewed cameras (wake demand), not players.
    public IReadOnlyList<PluginPlayerPosition> PlayerPositions() => PositionRegistryService.Players.Where(p => p.ClientId != 0)
        .Select(p => new PluginPlayerPosition { IdentityId = p.IdentityId, SteamId = p.ClientId, EntityId = p.EntityId,
            Position = new VRageMath.Vector3D(p.X, p.Y, p.Z) }).ToArray();
    public PluginEntityPlacement LocateEntity(long entityId)
    {
        if (!Sandbox.Game.Entities.MyEntities.TryGetEntityById(entityId, out VRage.Game.Entity.MyEntity entity) || entity.MarkedForClose)
            return new PluginEntityPlacement { EntityId = entityId, Residence = PluginEntityResidence.Absent };
        long top = entity.GetTopMostParent()?.EntityId ?? entity.EntityId;
        var (partition, owned) = ClusterRuntime.PluginEntityPartition(top);
        // SE-1155: an import whose sliced activation is still open counts as frozen too. A plugin that writes an
        // entity it holds as Local between the slices changes what the round-trip gate collects (T-0392).
        var residence = ClusterRuntime.IsFrozen(top) || ImportActivationGuard.IsOpen(top) ? PluginEntityResidence.Frozen
            : owned ? PluginEntityResidence.Local : PluginEntityResidence.Unowned;
        return new PluginEntityPlacement { EntityId = top, Residence = residence, Partition = partition, Node = node };
    }
    // T-0256 clock: a node's game time already follows the WA (SessionClockPatch); the never-stepping clock is
    // the replication clock (game time minus the admin time-of-day offset on the WA, its slewed estimate on a node).
    public PluginClusterTime Time()
    {
        bool authority = ClusterRuntime.IsWorldAuthority;
        return new PluginClusterTime {
            GameTime = Sandbox.Game.World.MySession.Static?.ElapsedGameTime ?? TimeSpan.Zero,
            ClockMilliseconds = authority ? ClusterRuntime.AuthorityClockMilliseconds : ClusterRuntime.ClusterMilliseconds(),
            Synchronized = authority || ClusterRuntime.HasAuthorityClock,
            Authoritative = authority };
    }
    // T-0256 global commands: the handler runs on the WA (the request targets it) inside the WA's GlobalCommandLedger,
    // which the WA checkpoint persists. The subsystem is a fixed-length hash: the ledger's restore refuses names over 128.
    public PluginGlobalCommandResult Execute(string plugin, string command, Guid operationId, byte[] payload, Func<byte[], byte[]> handler)
    {
        var result = GlobalCommandLedger.Execute(Subsystem(plugin, command), operationId, Convert.ToBase64String(payload ?? Array.Empty<byte>()),
            () => new GlobalCommandResult(true, Convert.ToBase64String(handler(payload) ?? Array.Empty<byte>()), string.Empty));
        if (result.Success) return new PluginGlobalCommandResult { Code = PluginResultCode.Success, Payload = Convert.FromBase64String(result.Payload) };
        bool conflict = result.Error == "Operation ID was reused with a different payload";
        return new PluginGlobalCommandResult { Code = conflict ? PluginResultCode.Conflict : PluginResultCode.Invalid, Error = result.Error };
    }
    private static string Subsystem(string plugin, string command) => "plugin/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(plugin + "\n" + command)), 0, 16);
    // SE-865: any plugin's ExecuteGlobalAsync of this command answers once a recoverable global save holds all the World
    // Authority did before the command got here. An older cluster answers Unsupported, as for a command nobody registered.
    private const string GlobalSaveCommand = "cluster.global-save";
    private const string GlobalSaveTopic = "global/" + GlobalSaveCommand;
    private async Task<PluginResult> GlobalSave(PluginMessage message)
    {
        // A place in the ledger of its own: a save with this order was captured after this. A retry with the same
        // operation id keeps the place, so saves slower than the caller's timeout still end the wait.
        GlobalCommandLedger.Execute(Subsystem(message.Plugin, GlobalSaveCommand), message.OperationId, string.Empty,
            () => new GlobalCommandResult(true, string.Empty, string.Empty), out long order);
        await GlobalStateChannel.UntilDurable(order).ConfigureAwait(false);
        return new PluginResult { Code = PluginResultCode.Success, Payload = new byte[1] }; // What PluginGlobalCommands.Decode reads as Success, no result.
    }
    public IDisposable RegisterGridHandover(string plugin, IPluginGridHandover handler)
    {
        if (!PluginRecordStore.ValidName(plugin)) throw new ArgumentException("Invalid plugin id.", nameof(plugin));
        return PluginHandoverService.Register(plugin, handler ?? throw new ArgumentNullException(nameof(handler)));
    }
    public IDisposable RegisterOfflineBlocker(string plugin, Func<long, string> blocker)
    {
        if (!PluginRecordStore.ValidName(plugin)) throw new ArgumentException("Invalid plugin id.", nameof(plugin));
        return PluginOfflineService.Register(plugin, blocker ?? throw new ArgumentNullException(nameof(blocker)));
    }
    /// <summary>The registry's roster of ready nodes and the WA ("nodes"), validated.</summary>
    private async Task<(PluginResultCode Code, PluginNodeInfo[] Nodes)> RosterAsync(string plugin, CancellationToken cancellationToken)
    {
        var roster = await ExecuteAsync(new PluginRequest { Action = "nodes", Plugin = plugin }, cancellationToken).ConfigureAwait(false);
        if (roster.Code != PluginResultCode.Success) return (roster.Code, null);
        PluginNodeInfo[] nodes;
        try { nodes = JsonSerializer.Deserialize<PluginNodeInfo[]>(roster.Payload ?? Array.Empty<byte>()); }
        catch { return (PluginResultCode.Unavailable, null); }
        if (nodes == null || nodes.Any(n => n?.Fence?.Target == null || n.Fence.Node != n.Node || n.Fence.Target.Kind != PluginTargetKind.Node))
            return (PluginResultCode.Unavailable, null);
        return (PluginResultCode.Success, nodes);
    }
    // T-0256 events. The roster has no push source, so it is polled - only while a plugin subscribes.
    private const double RosterPollSeconds = 5;
    private const string EventsPlugin = "cluster-events";
    public event Action<PluginClusterEvent> ClusterEvent;
    private Dictionary<string, PluginNodeInfo> knownNodes;
    private Task<(PluginResultCode Code, PluginNodeInfo[] Nodes)> rosterPoll;
    private DateTime nextRosterPoll;
    public async Task<IReadOnlyList<PluginNodeInfo>> NodesAsync(CancellationToken cancellationToken)
    {
        var (code, nodes) = await RosterAsync(EventsPlugin, cancellationToken).ConfigureAwait(false);
        return code == PluginResultCode.Success ? nodes : null;
    }
    private void Raise(PluginClusterEvent change)
    {
        Console.WriteLine("[ClusterPlugin] plugin-event " + change);
        try { ClusterEvent?.Invoke(change); } catch { /* PluginCluster.Relay isolates each plugin's observer. */ }
    }
    /// <summary>Game thread, from Update: the WA generation and this node's partitions, diffed against the last context.</summary>
    private void RaiseOwnershipEvents(NodeContext before, NodeContext after)
    {
        if (ClusterEvent == null || before == null) return;
        if (after.WorldAuthorityGeneration != before.WorldAuthorityGeneration)
            Raise(new PluginClusterEvent { Kind = PluginClusterEventKind.WorldAuthorityChanged, Generation = after.WorldAuthorityGeneration });
        foreach (var owned in after.OwnedPartitions.OrderBy(p => p.Key))
            if (!before.OwnedPartitions.TryGetValue(owned.Key, out long generation) || generation != owned.Value)
                Raise(new PluginClusterEvent { Kind = PluginClusterEventKind.PartitionAcquired, Node = node,
                    Incarnation = after.Incarnation, Partition = owned.Key, Generation = owned.Value });
        foreach (var lost in before.OwnedPartitions.OrderBy(p => p.Key))
            if (!after.OwnedPartitions.ContainsKey(lost.Key))
                Raise(new PluginClusterEvent { Kind = PluginClusterEventKind.PartitionLost, Node = node,
                    Incarnation = before.Incarnation, Partition = lost.Key, Generation = lost.Value });
    }
    /// <summary>Game thread, from Update: poll the roster while subscribed; NodeUp/NodeDown on a change of (node, incarnation).
    /// The first roster after subscribing seeds silently.</summary>
    private void PollRoster()
    {
        if (ClusterEvent == null) { knownNodes = null; return; }
        if (rosterPoll == null)
        {
            if (DateTime.UtcNow < nextRosterPoll || !Context.Available) return;
            nextRosterPoll = DateTime.UtcNow.AddSeconds(RosterPollSeconds);
            rosterPoll = RosterAsync(EventsPlugin, CancellationToken.None);
            return;
        }
        if (!rosterPoll.IsCompleted) return;
        var (code, nodes) = rosterPoll.Status == TaskStatus.RanToCompletion ? rosterPoll.Result : (PluginResultCode.Unavailable, null);
        rosterPoll = null;
        if (code != PluginResultCode.Success) return;
        var current = nodes.ToDictionary(n => n.Node + "#" + n.Incarnation);
        if (knownNodes != null)
        {
            foreach (var gone in knownNodes.Where(n => !current.ContainsKey(n.Key)).OrderBy(n => n.Key))
                Raise(new PluginClusterEvent { Kind = PluginClusterEventKind.NodeDown, Node = gone.Value.Node,
                    Incarnation = gone.Value.Incarnation, Role = gone.Value.Role });
            foreach (var up in current.Where(n => !knownNodes.ContainsKey(n.Key)).OrderBy(n => n.Key))
                Raise(new PluginClusterEvent { Kind = PluginClusterEventKind.NodeUp, Node = up.Value.Node,
                    Incarnation = up.Value.Incarnation, Role = up.Value.Role });
        }
        knownNodes = current;
    }
    private async Task<PluginResult> SendAsync(string plugin, OwnerFence destination, string topic, byte[] payload,
        Guid operationId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var message = new PluginMessage { Id = Guid.NewGuid(), OperationId = operationId, Plugin = plugin, Topic = topic,
            Source = node, Incarnation = ClusterRuntime.RegistryNodeEpoch, Destination = destination, Payload = payload };
        var completion = new TaskCompletionSource<PluginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending)
        {
            if (pending.Count >= 128) return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Capacity));
            pending[message.Id] = (message, completion);
        }
        try
        {
            if (!DirectTransportNodeLinkPatch.SendPlugin(JsonSerializer.SerializeToUtf8Bytes(message))) return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Unavailable));
            if (await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) != completion.Task)
                return diagnostics.Observe(PluginResult.Failure(PluginResultCode.Timeout));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(message.Id, out _); }
    }
    private void Receive(byte[] bytes)
    {
        if (bytes.Length > 128 * 1024) return;
        PluginMessage message;
        try { message = JsonSerializer.Deserialize<PluginMessage>(bytes); } catch { return; }
        if (message == null || message.Protocol != 1 || !PluginRecordStore.ValidName(message.Plugin)
            || !PluginRecordStore.ValidName(message.Topic) || (message.Payload?.Length ?? 0) > PluginRecordStore.MaxPayload) return;
        if (message.Reply)
        {
            if (pending.TryGetValue(message.Id, out var request) && message.OperationId == request.Message.OperationId
                && message.Source == request.Message.Destination.Node && message.Incarnation == request.Message.Destination.Incarnation
                && message.Plugin == request.Message.Plugin && message.Topic == request.Message.Topic)
                request.Completion.TrySetResult(new PluginResult { Code = message.Code, Payload = message.Payload });
            return;
        }
        if (message.Id == Guid.Empty || message.OperationId == Guid.Empty || message.Destination?.Node != node) return;
        if (Interlocked.Increment(ref receiving) > 128)
        { Interlocked.Decrement(ref receiving); Reply(message, PluginResult.Failure(PluginResultCode.Capacity)); return; }
        _ = Dispatch(message);
    }
    private const int MaxValidations = 3;
    private async Task Dispatch(PluginMessage message)
    {
        try
        {
            var fence = message.Destination;
            if (fence.Incarnation != ClusterRuntime.RegistryNodeEpoch)
            { Reply(message, PluginResult.Failure(PluginResultCode.Fenced)); return; }
            PluginResult result;
            bool expired;
            int attempts = 0;
            do
            {
                expired = false;
                var freshness = Stopwatch.StartNew();
                var validated = await ExecuteAsync(new PluginRequest { Action = "validate", Plugin = message.Plugin, Fence = fence }, CancellationToken.None).ConfigureAwait(false);
                if (validated.Code != PluginResultCode.Success) { result = validated; break; }
                if (message.Topic == GlobalSaveTopic && ClusterRuntime.IsWorldAuthority) { result = await GlobalSave(message).ConfigureAwait(false); break; }
                var handled = handlers.Enqueue(message, () =>
                {
                    expired = freshness.Elapsed.TotalMilliseconds >= validated.ValidForMilliseconds;
                    return !expired && Context.Available && validated.Owner?.StoreId == fence.StoreId && ClusterRuntime.PluginFenceCurrent(fence);
                });
                // The 250 ms update is as long as the validation holds: waiting for it would expire the validation.
                Sandbox.MySandboxGame.Static?.Invoke(handlers.Update, "ClusterPlugin plugin handlers");
                result = await handled.ConfigureAwait(false);
            }
            // A long frame outlasted the validation, nothing is known against the fence and the handler has not run: ask the Registry again.
            while (expired && ++attempts < MaxValidations);
            Reply(message, result);
        }
        catch { Reply(message, PluginResult.Failure(PluginResultCode.Unavailable)); }
        finally { Interlocked.Decrement(ref receiving); }
    }
    private void Reply(PluginMessage message, PluginResult result)
    {
        message.Reply = true; message.Source = node; message.Incarnation = ClusterRuntime.RegistryNodeEpoch;
        message.Code = result.Code; message.Payload = result.Payload;
        DirectTransportNodeLinkPatch.SendPlugin(JsonSerializer.SerializeToUtf8Bytes(message));
    }
    public async Task<ClusterLifecycleAcknowledgement> RequestAsync(ClusterLifecycleRequest request, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(new PluginRequest { Action = request.Kind == ServerTerminationKind.Restart ? "restart" : "stop",
            Plugin = "cluster-lifecycle", OperationId = request.RequestId, SaveFirst = request.SaveFirst }, cancellationToken).ConfigureAwait(false);
        var ack = new ClusterLifecycleAcknowledgement(request.RequestId, result.Code == PluginResultCode.Success
            ? ClusterLifecycleDisposition.Accepted : ClusterLifecycleDisposition.Rejected, result.Code == PluginResultCode.Success ? request.RequestId : Guid.Empty,
            result.Code.ToString(), "Registry lifecycle decision", ClusterRuntime.NodeState);
        LifecycleAcknowledged?.Invoke(ack); return ack;
    }
    public bool SendLifecycleRequest(ClusterLifecycleRequest request)
    { if (!Context.Available) return false; _ = RequestAsync(request, CancellationToken.None); return true; }
    internal void Update()
    {
        var context = Context;
        bool ownershipChanged = previousContext != null && (context.WorldAuthorityGeneration != previousContext.WorldAuthorityGeneration
            || context.OwnedPartitions.Count != previousContext.OwnedPartitions.Count
            || context.OwnedPartitions.Any(p => !previousContext.OwnedPartitions.TryGetValue(p.Key, out long generation) || generation != p.Value));
        if (previousContext == null || context.Available != previousContext.Available || ownershipChanged)
        {
            if (ownershipChanged) RaiseOwnershipEvents(previousContext, context);
            previousContext = context;
            if (!context.Available) FailPending();
            if (ownershipChanged) diagnostics.OwnershipChanged();
            ContextChanged?.Invoke();
        }
        diagnostics.Publish(context, requests + pending.Count + receiving);
        PollRoster();
        handlers.Update();
    }
    private void FailPending()
    { foreach (var entry in pending.Values) entry.Completion.TrySetResult(PluginResult.Failure(PluginResultCode.Unavailable)); }
    public void Dispose()
    {
        disposed = true; FailPending(); handlers.Dispose(); client.Dispose();
        DirectTransportNodeLinkPatch.PluginMessageReceived -= Receive;
        DirectTransportNodeLinkPatch.GlobalMessageReceived -= GlobalReceived;
        DirectTransportNodeLinkPatch.GlobalLinkChanged -= LinkChanged;
        DirectTransportNodeLinkPatch.AttachBindingRemoved -= Detached;
        if (DirectTransportNodeLinkPatch.AttachBindingAccepted == ValidateAttach) DirectTransportNodeLinkPatch.AttachBindingAccepted = originalValidator;
        PluginCluster.Unregister(this); ClusterLifecycle.Unregister(this); ClusterNodeLink.Unregister(this);
        if (Instance == this) Instance = null;
    }
}
