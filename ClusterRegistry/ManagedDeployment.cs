using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>One approved slot. <paramref name="Spare"/> designates a regular slot a hot spare at the start of a generation
/// (ManagedDeployment.md "Hot spares"); which slots are spares changes at runtime as spares replace dead nodes.</summary>
internal sealed record ManagedSlot(string SlotKey, string Host, string Node, string Endpoint, NodeRole Role, bool Spare = false);
/// <summary><paramref name="RecoveryUpdate"/>: a recovery start that also takes a new revision or slot inventory, so a
/// cluster whose every stop ends dirty can still receive the build that fixes it (#1296).</summary>
internal sealed record ManagedDeployment(string Revision, Guid StartGeneration, ManagedSlot[] Slots, bool Recover = false,
    bool RecoveryUpdate = false);
internal sealed record ManagedRestore(Guid RestoreId, string Revision);
internal sealed record ManagedProof(string Node, long Epoch, string Revision);

internal sealed partial class GatewayRegistry
{
    internal static ManagedDeployment? ReadManagedDeployment()
    {
        string? path = Environment.GetEnvironmentVariable("CLUSTER_MANAGED_DEPLOYMENT");
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Managed deployment exceeds 1 MiB.");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new JsonStringEnumConverter() } };
        var deployment = JsonSerializer.Deserialize<ManagedDeployment>(File.ReadAllBytes(path), json)
            ?? throw new InvalidDataException("Managed deployment is empty.");
        if (!Guid.TryParse(Environment.GetEnvironmentVariable("CLUSTER_START_GENERATION"), out var generation)
            || generation == Guid.Empty) throw new InvalidDataException("A managed start generation is required.");
        string? recovery = Environment.GetEnvironmentVariable("CLUSTER_START_RECOVERY");
        bool recover = false, update = recovery == "update";
        if (recovery is not null && !update && !bool.TryParse(recovery, out recover))
            throw new InvalidDataException("Invalid managed recovery flag.");
        return deployment with { StartGeneration = generation, Recover = recover || update, RecoveryUpdate = update };
    }

    // Called once during construction, before exposing any API. The immutable manifest is
    // authorized by Host; restarting a process is never authorization to undo a shutdown.
    private void InitializeManagedDeployment(ManagedDeployment deployment, string directory)
    {
        if (string.IsNullOrWhiteSpace(deployment.Revision) || deployment.Revision.Length > 128
            || deployment.StartGeneration == Guid.Empty || deployment.Slots is null
            || deployment.Slots.Length is < 2 or > 256
            || deployment.Slots.Count(slot => slot.Role == NodeRole.WA) != 1
            || !deployment.Slots.Any(slot => slot.Role == NodeRole.Regular)
            || deployment.Slots.Any(slot => string.IsNullOrWhiteSpace(slot.Host)
                || string.IsNullOrWhiteSpace(slot.SlotKey) || string.IsNullOrWhiteSpace(slot.Node)
                || string.IsNullOrWhiteSpace(slot.Endpoint) || !Enum.IsDefined(slot.Role))
            || deployment.Slots.Select(slot => slot.SlotKey).Distinct().Count() != deployment.Slots.Length
            || deployment.Slots.Select(slot => slot.Node).Distinct().Count() != deployment.Slots.Length
            || deployment.Slots.Any(slot => slot.Spare && slot.Role != NodeRole.Regular)
            || !deployment.Slots.Any(slot => slot.Role == NodeRole.Regular && !slot.Spare))
            throw new InvalidDataException("Invalid managed deployment inventory.");
        string restorePath = Path.Combine(directory, "managed-restore.json");
        if (File.Exists(restorePath))
        {
            var restore = JsonSerializer.Deserialize<ManagedRestore>(File.ReadAllBytes(restorePath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing restore authorization.");
            if (restore.RestoreId == Guid.Empty) throw new InvalidDataException("Invalid restore authorization.");
            if (_state.LastManagedRestore != restore.RestoreId)
            {
                if (restore.Revision != deployment.Revision || _state.ManagedDeployment?.Revision == deployment.Revision)
                    throw new RegistryConflictException("Restore authorization must match a newly prepared deployment revision with rotated credentials.");
                FenceManagedIncarnations();
                _state.Executors.Clear();
                _state.NodePlan.Clear();
                foreach (var slot in deployment.Slots)
                    _state.NodePlan.Add(slot.SlotKey, new(slot.SlotKey, slot.Host, slot.Role, NodeDesiredState.Wanted));
                _state.ManagedDeployment = deployment;
                SeedManagedSpares(deployment);
                _state.ManagedProofs.Clear();
                _state.PluginStore.Restored();
                _state.PluginLifecycles.Clear();
                _state.LastManagedRestore = restore.RestoreId;
                _state.CleanShutdown = null; _state.LastShutdown = null; _state.ShutdownStarted = null;
                _state.ShutdownNodes.Clear();
                _state.Phase = ClusterPhase.Bootstrapping; _state.Startup = StartupKind.Recovery;
                Persist(); Flush();
            }
        }
        var previous = _state.ManagedDeployment;
        if (previous is not null && previous.StartGeneration == deployment.StartGeneration)
        {
            if (previous.Revision != deployment.Revision || !previous.Slots.SequenceEqual(deployment.Slots))
                throw new RegistryConflictException("A start generation cannot change deployment inputs.");
            return; // Preserve drains, failures, pending kills and operator changes.
        }
        bool recoveryStart = deployment.Recover && previous is not null;
        if (deployment.Recover && (previous is null || !deployment.RecoveryUpdate && (previous.Revision != deployment.Revision
            || !previous.Slots.SequenceEqual(deployment.Slots))))
            throw new RegistryConflictException("Recovery requires the unchanged managed deployment inventory.");
        if (!recoveryStart && (previous is null ? _state.Nodes.Count != 0 || _state.NodePlan.Count != 0
            : _state.Phase != ClusterPhase.Down || !IsWarmShutdown(_state.CleanShutdown)))
            throw new RegistryConflictException("A new managed generation requires clean Down or explicit stopped-fleet recovery.");
        if (recoveryStart)
        {
            // Host must prove the entire fleet stopped before authorizing this generation.
            // Keep saves and plugin records; invalidate only obsolete runtime authority and operations.
            FenceManagedIncarnations();
            _state.Executors.Clear();
        }
        _state.Startup = previous is null || recoveryStart ? StartupKind.Recovery : StartupKind.Warm;
        _state.LastShutdown = _state.CleanShutdown;
        if (_state.Startup == StartupKind.Warm) _startedFromGlobal = _state.LastShutdown?.GlobalRef;
        _state.CleanShutdown = null;
        _state.ShutdownStarted = null;
        _state.ShutdownNodes.Clear();
        _state.NodePlan.Clear();
        foreach (var slot in deployment.Slots)
            _state.NodePlan.Add(slot.SlotKey, new(slot.SlotKey, slot.Host, slot.Role, NodeDesiredState.Wanted));
        _state.ManagedDeployment = deployment;
        SeedManagedSpares(deployment);
        _state.ManagedProofs.Clear();
        _state.Phase = ClusterPhase.Bootstrapping;
        Persist();
        Flush();
    }

    /// <summary>
    /// Every generation starts with the approved spare slots, before any node registers, so no spare is ever
    /// placement-eligible. At runtime the set moves: a promoted spare serves and the dead node's slot comes back as
    /// the spare (PromoteSparesForDeadNodes). Replaying a generation keeps the moved set.
    /// </summary>
    private void SeedManagedSpares(ManagedDeployment deployment)
    {
        _state.SpareNodes.Clear();
        foreach (var slot in deployment.Slots.Where(slot => slot.Spare)) _state.SpareNodes.Add(slot.Node);
        foreach (var node in _state.Nodes.Values) node.Spare = _state.SpareNodes.Contains(node.Node);
    }

    private void FenceManagedIncarnations()
    {
        foreach (var node in _state.Nodes.Values) { node.State = NodeState.Dead; node.Serving = false; }
        foreach (var owner in _state.Partitions.Values)
        {
            owner.Node = null; owner.Epoch = 0; owner.Generation++;
            owner.LeaseExpires = DateTimeOffset.MinValue; owner.Recovering = false;
            Touch(owner);
        }
        _state.WorldAuthority.Node = null; _state.WorldAuthority.Epoch = 0;
        _state.WorldAuthority.Generation++; _state.WorldAuthority.LeaseExpires = DateTimeOffset.MinValue;
        _state.WorldAuthority.ControlEndpoint = null;
        foreach (var handover in _state.Handovers.Values.Where(h => h.State is HandoverState.Begun or HandoverState.Active))
            { handover.State = HandoverState.Aborted; Touch(handover); }
        _state.AdminOperations.Clear();
    }

    private void ValidateManagedReport(Admin.ExecutorPollRequest request, Admin.ExecutorNodeReport report)
    {
        if (_state.ManagedDeployment is not { } deployment) return;
        var slot = deployment.Slots.SingleOrDefault(slot => slot.SlotKey == report.SlotKey);
        if (slot is null || slot.Host != request.Host || request.DeploymentRevision != deployment.Revision
            || report.Node is not null && report.Node != slot.Node)
            throw new RegistryConflictException("Executor observation does not match the active deployment.");
    }

    private void RecordManagedReports(Admin.ExecutorPollRequest request)
    {
        // Executor readiness observes process startup. Only the runtime may prove live config.
        foreach (var report in request.Reports)
            if (report.Epoch > 0 && _state.Nodes.TryGetValue(report.Node ?? "", out var node)
                && node.Epoch == report.Epoch && report.State == Admin.NodeObservation.Failed
                && report.Failure?.StartsWith("configuration_drift:", StringComparison.Ordinal) == true)
                RecordManagedHeartbeat(node, request.DeploymentRevision, false, report.Failure);
    }

    private void RecordManagedHeartbeat(NodeRegistration node, string? revision, bool verified, string? failure)
    {
        if (_state.ManagedDeployment is not { } deployment) return;
        var slot = deployment.Slots.Single(s => s.Node == node.Node);
        if (verified && revision == deployment.Revision && failure is null && node.State == NodeState.Active)
            _state.ManagedProofs[slot.SlotKey] = new(node.Node, node.Epoch, revision);
        else
        {
            _state.ManagedProofs.Remove(slot.SlotKey);
            if (node.State == NodeState.Active && (failure is not null || revision != deployment.Revision))
            {
                CloseNode(node.Node, node.Epoch);
                var plan = _state.NodePlan[slot.SlotKey];
                plan.IncumbentNode = node.Node;
                plan.IncumbentEpoch = node.Epoch;
                plan.IncumbentAction = NodeIncumbentAction.Draining;
                // A replacement with identical bad configuration would repeat the same failure.
                // Hold this slot until a corrected deployment or explicit operator action.
                plan.Desired = NodeDesiredState.Draining;
                plan.LastFailure = failure ?? "deployment_revision_mismatch";
                Persist();
            }
        }
    }

    private void AdvanceManagedReadiness()
    {
        if (_state.ManagedDeployment is { } deployment && _state.Phase == ClusterPhase.Bootstrapping
            && deployment.Slots.All(slot => _state.Nodes.TryGetValue(slot.Node, out var node)
                && node.Serving && ManagedNodeReady(node)))
        {
            _state.Phase = ClusterPhase.Serving;
            Emit("cluster.phase", data: new Dictionary<string, string> { ["phase"] = nameof(ClusterPhase.Serving) });
            Persist();
        }
    }

    private bool ManagedNodeReady(NodeRegistration node) => _state.ManagedDeployment is not { } deployment
        || node.State == NodeState.Active && node.LeaseExpires > _clock()
            && _state.ManagedProofs.Values.Any(proof => proof.Node == node.Node
                && proof.Epoch == node.Epoch && proof.Revision == deployment.Revision);
}
