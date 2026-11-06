using System.Globalization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

/// <summary>
/// T-0320 node control model v2 (spec ~/ws/se/notes/NodeControlDesign.md, plan tools/t0320/PLAN.md): per-host serving and
/// spare targets, global node ids that are never reused, one record per live node (host, pid, start, intent, stop
/// request), the operator states Starting/Serving/Spare/Draining/Stopping/Failed, and a bounded history of stopped
/// nodes. The registry makes every policy decision here; the decisions reach the launcher as NodePlan slots
/// (SlotKey = node id, Wanted, then Draining, then Kill after MarkEmpty), and T-0312's scale-in stays the drain.
///
/// Active once any host has a target (the hosts file applied at start, or a runtime set). While active, only planned
/// ids may register. Managed (Quasar) deployments are read-only here: their inventory is the hash-approved slot list,
/// and their hot spares are designated slots of it (ManagedDeployment.md "Hot spares").
/// </summary>
internal sealed class NodeControlState
{
    /// <summary>The number the next allocated id gets (node-N); 0 = never used, seeded from every id the registry knows.</summary>
    public long NextNodeNumber { get; set; }
    public long Revision { get; set; }
    /// <summary>The last history sequence handed out (history cursors).</summary>
    public long HistorySeq { get; set; }
    /// <summary>Runtime targets: the hosts file at start, then whatever the operator set.</summary>
    public Dictionary<string, HostTarget> Hosts { get; set; } = [];
    /// <summary>The targets of the last hosts-file apply, for "runtime differs from hosts file".</summary>
    public Dictionary<string, HostTarget> FileHosts { get; set; } = [];
    public string? HostsFileSha256 { get; set; }
    public DateTimeOffset? HostsFileAppliedAt { get; set; }
    /// <summary>Every node from planning until it is gone (then it moves to RegistryState.NodeHistory).</summary>
    public Dictionary<string, NodeRecord> Live { get; set; } = [];
    /// <summary>A cluster stop began: nothing is planned or replaced until the next start applies the hosts file (or an
    /// operator sets a target). The stopped nodes are Empty or Dead then, and the start gives every node a fresh id anyway;
    /// planning replacements in between burns ids and, with an always-on executor, launches them (bench run 2).
    /// Persisted, so a crash of the reopened registry before the apply keeps it.</summary>
    public bool AwaitingStart { get; set; }
}

internal sealed record HostTarget(int Serving, int Spares, long? RamFloorMb = null, long? DiskFloorMb = null);

internal enum NodeIntent { Serving, Spare }

internal sealed record NodeRecord(string Node, string Host)
{
    public NodeIntent Intent { get; set; }
    public int? Pid { get; set; }
    public DateTimeOffset PlannedAt { get; set; }
    /// <summary>Registration of the running process (age counts from here).</summary>
    public DateTimeOffset? StartedAt { get; set; }
    public string? Replaces { get; set; }
    public string? ReplacedBy { get; set; }
    public NodeStop? Stop { get; set; }
    /// <summary>Taken over from a node set started before node control was active (no NodePlan slot).</summary>
    public bool Adopted { get; set; }
}

/// <summary>Why and how a node is being stopped. Kinds: remove, recycle, decrease, failed.</summary>
internal sealed record NodeStop(string Kind, string Actor, string? RequestedBy, string Why, DateTimeOffset RequestedAt,
    DateTimeOffset Deadline, bool Now)
{
    public DateTimeOffset Deadline { get; set; } = Deadline;
    public bool Now { get; set; } = Now;
    public bool Forced { get; set; }
    public DateTimeOffset? ForcedAt { get; set; }
    /// <summary>The deadline clock is stopped while partitions recover (the drain cannot progress either).</summary>
    public DateTimeOffset? PausedSince { get; set; }
    public int PartitionsAtStop { get; set; }
}

internal sealed record NodeHistoryRecord(long Seq, string Node, string Host, int? Pid, DateTimeOffset StartedAt,
    DateTimeOffset EndedAt, double AgeSeconds, string Outcome, string Actor, string? RequestedBy, string Why,
    bool Forced, string? ReplacedBy, int PartitionsAtStop);

/// <summary>An operator error with its admin status code and stable error code.</summary>
internal sealed class NodeControlException(int status, string code, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>A node id that was stopped (history) or is being stopped registers again: refused, ids are never reused.</summary>
internal sealed class NodeIdRetiredException(string message) : RegistryConflictException("node_id_retired: " + message);

/// <summary>What a node-control mutation did, for the operation result.</summary>
internal sealed record NodeControlChange(long Revision, string? Node, string? Replacement, string[] Actions, string[] Warnings);

internal sealed partial class GatewayRegistry
{
    internal const int MaxNodeHistory = 2000;
    /// <summary>The history bound in force (self-test lowers it to prove the pruning without 2000 stops).</summary>
    internal static int NodeHistoryLimit = MaxNodeHistory;
    internal const int MaxHostNodes = 16;

    /// <summary>Default graceful-drain budget before a stop is forced (CLUSTER_DRAIN_TIMEOUT_SECONDS, default 300).</summary>
    internal static TimeSpan DefaultDrainTimeout =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_DRAIN_TIMEOUT_SECONDS"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 ? seconds : 300);

    private NodeControlState Control => _state.NodeControl;
    private bool NodeControlActive => _state.ManagedDeployment is null && _state.NodeControl.Hosts.Count != 0;
    private HashSet<string>? _historyIds;

    private HashSet<string> HistoryIds => _historyIds ??= [.. _state.NodeHistory.Select(x => x.Node)];

    private static long NodeNumber(string? id) =>
        id != null && id.StartsWith("node-", StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0 ? number : 0;

    /// <summary>
    /// The only place a node id is created. Never reused: the counter is persisted before the id is returned and is
    /// floored by every id the registry still knows (rows, slots, records, history, spare and scale sets), so the ids
    /// of a T-0312 run (node-5..7) and of nodes pruned from the bounded history are skipped too. Caller holds _lock.
    /// </summary>
    private string AllocateNodeId()
    {
        long floor = new[]
        {
            _state.Nodes.Keys, _state.NodePlan.Keys, _state.NodePlan.Values.Select(x => x.ObservedNode ?? ""),
            _state.NodePlan.Values.Select(x => x.IncumbentNode ?? ""), Control.Live.Keys, _state.NodeHistory.Select(x => x.Node),
            _state.SpareNodes, _state.Scale.Retired, _state.Scale.Retiring,
        }.SelectMany(ids => ids).Select(NodeNumber).DefaultIfEmpty(0).Max() + 1;
        long number = Math.Max(Control.NextNodeNumber, floor);
        Control.NextNodeNumber = number + 1;
        Persist();
        Flush();
        return "node-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Registration gate and record update for a regular node (RegisterNode, under _lock).</summary>
    private NodeRecord? NodeControlOnRegister(NodeJoinRequest request, NodeRegistration? existing)
    {
        if (_state.ManagedDeployment is not null || request.Role != NodeRole.Regular) return null;
        string node = request.Node;
        if (HistoryIds.Contains(node))
            throw new NodeIdRetiredException($"{node} was stopped and is in the node history; node ids are never reused");
        if (!Control.Live.TryGetValue(node, out NodeRecord? record))
        {
            if (NodeControlActive)
                throw new RegistryConflictException($"node_not_planned: {node} is not in the node-control plan; only planned node ids may register");
            return null;
        }
        if (record.Stop != null && existing != null)
            throw new NodeIdRetiredException($"{node} is being stopped ({record.Stop.Kind}); it does not come back");
        if (!record.Adopted && existing?.State == NodeState.Dead)
            throw new NodeIdRetiredException($"{node} died; its replacement takes a new id");
        if (!string.IsNullOrWhiteSpace(request.Host) && !string.Equals(request.Host, record.Host, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Node control: {node} registered from host {request.Host}, planned on {record.Host}; the plan's host counts");
        return record;
    }

    // ---------------------------------------------------------------- operator state

    private NodeSlotPlan? SlotOf(string node) => _state.NodePlan.GetValueOrDefault(node);

    private Admin.OperatorNodeState OperatorState(NodeRecord record, NodeRegistration? row, NodeSlotPlan? slot)
    {
        if (record.Stop is { } stop)
        {
            if (stop.Kind == "failed") return Admin.OperatorNodeState.Failed;
            if (row == null || stop.Forced || row.State is NodeState.Empty or NodeState.Dead
                || slot?.IncumbentAction == NodeIncumbentAction.Kill || slot?.Desired == NodeDesiredState.Kill)
                return Admin.OperatorNodeState.Stopping;
            return Admin.OperatorNodeState.Draining;
        }
        if (row == null) return Admin.OperatorNodeState.Starting;
        return RowState(row);
    }

    /// <summary>The operator state of a registration with no stop request (records, managed slots, unplanned rows).</summary>
    private Admin.OperatorNodeState RowState(NodeRegistration row) => row.State switch
    {
        NodeState.Dead => Admin.OperatorNodeState.Failed,
        NodeState.Empty => Admin.OperatorNodeState.Stopping,
        NodeState.Closed => Admin.OperatorNodeState.Draining,
        _ when _state.Scale.Retiring.Contains(row.Node) => Admin.OperatorNodeState.Draining,
        _ when !row.Serving => Admin.OperatorNodeState.Starting,
        _ when row.Spare || _state.SpareNodes.Contains(row.Node) => Admin.OperatorNodeState.Spare,
        _ => Admin.OperatorNodeState.Serving,
    };

    /// <summary>Counts toward its host's target: not stopping and not failing (a registry-closed node is on its way out).</summary>
    private bool Counted(NodeRecord record)
    {
        if (record.Stop != null) return false;
        NodeRegistration? row = _state.Nodes.GetValueOrDefault(record.Node);
        return row == null || row.State == NodeState.Active;
    }

    private static IOrderedEnumerable<NodeRecord> Oldest(IEnumerable<NodeRecord> records) => records
        .OrderBy(x => x.StartedAt ?? DateTimeOffset.MaxValue).ThenBy(x => x.PlannedAt).ThenBy(x => NodeNumber(x.Node)).ThenBy(x => x.Node, StringComparer.Ordinal);

    private int OwnedPartitions(string node)
    {
        if (!_state.Nodes.TryGetValue(node, out NodeRegistration? row)) return 0;
        return _state.Partitions.Values.Count(x => x.Node == node && x.Epoch == row.Epoch);
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>Once a second from the lease thread, and after every executor report: lifecycle, deadlines, planner.</summary>
    public void NodeControlPass()
    {
        lock (_lock)
        {
            if (_state.ManagedDeployment is not null)
            {
                if (ManagedRotationStep(_clock())) Persist();
                return;
            }
            if (NodeControlStep(_clock(), "registry", null, null, [], [])) Persist();
        }
    }

    /// <summary>Caller holds _lock. Returns whether anything changed (the caller persists).</summary>
    private bool NodeControlStep(DateTimeOffset now, string actor, string? requestedBy, Func<int, string?>? capacity,
        List<string> actions, List<string> warnings, string? reason = null)
    {
        if (_state.Phase is ClusterPhase.Draining or ClusterPhase.Down || Control.AwaitingStart
            || Control.Live.Count == 0 && Control.Hosts.Count == 0)
            return false;
        bool changed = false;
        bool? recovering = null;
        foreach (NodeRecord record in Control.Live.Values.ToArray())
        {
            NodeRegistration? row = _state.Nodes.GetValueOrDefault(record.Node);
            NodeSlotPlan? slot = SlotOf(record.Node);
            if (record.Stop == null)
            {
                if (row?.State == NodeState.Dead) { MarkFailed(record, 0, now); changed = true; }
                // Closed by the registry itself (save failure, stall, a direct close) and now gone: it left unasked.
                else if (row?.State is NodeState.Empty or NodeState.Closed && slot?.Observed == SpawnState.Gone)
                {
                    ToHistory(record, "closed", now);
                    changed = true;
                }
                continue;
            }
            NodeStop stop = record.Stop;
            if (stop.Kind == "failed")
            {
                KillSlot(slot, now);
                bool replaced = record.ReplacedBy != null && (!Control.Live.TryGetValue(record.ReplacedBy, out NodeRecord? successor)
                    || OperatorState(successor, _state.Nodes.GetValueOrDefault(successor.Node), SlotOf(successor.Node)) == Admin.OperatorNodeState.Serving);
                if (replaced || !Control.Hosts.ContainsKey(record.Host)) { ToHistory(record, "failed", now); changed = true; }
                continue;
            }
            if (slot?.Observed == SpawnState.Gone && (row == null || slot.IncumbentNode == null))
            {
                ToHistory(record, stop.Kind, now);
                changed = true;
                continue;
            }
            if (row == null)
            {
                // Never registered: cancel the launch. A process being spawned is killed first.
                if (slot == null || slot.Observed is not (SpawnState.Spawning or SpawnState.Ready)) { ToHistory(record, stop.Kind, now); changed = true; }
                else changed |= KillSlot(slot, now);
                continue;
            }
            if (row.State == NodeState.Dead)
            {
                if (slot == null || slot.Observed is not (SpawnState.Spawning or SpawnState.Ready)) { ToHistory(record, stop.Kind, now); changed = true; }
                else changed |= KillSlot(slot, now);
                continue;
            }
            changed |= InitiateStop(record, row, slot, now);
            if (!stop.Forced && row.State is NodeState.Active or NodeState.Closed)
            {
                bool paused = !stop.Now && (recovering ??= RecoveryPending());
                if (paused && stop.PausedSince == null) { stop.PausedSince = now; changed = true; }
                else if (!paused && stop.PausedSince is { } since)
                {
                    stop.Deadline += now - since;
                    stop.PausedSince = null;
                    changed = true;
                }
                if (!paused && now >= stop.Deadline) { ForceStop(record, row, now); changed = true; }
            }
        }
        changed |= CleanHistorySlots(now);
        foreach ((string host, HostTarget target) in Control.Hosts.OrderBy(x => x.Key, StringComparer.Ordinal))
            changed |= Reconcile(host, target, now, actor, requestedBy, capacity, actions, warnings, servingFirst: false, reason);
        // A failed node nothing will replace (its host's count is met without it) leaves the live list.
        foreach (NodeRecord failed in Control.Live.Values.Where(x => x.Stop?.Kind == "failed" && x.ReplacedBy == null).ToArray())
        {
            if (!Control.Hosts.TryGetValue(failed.Host, out HostTarget? target)) continue;
            int serving = Control.Live.Values.Count(x => x.Host == failed.Host && x.Intent == NodeIntent.Serving && Counted(x));
            int spares = Control.Live.Values.Count(x => x.Host == failed.Host && x.Intent == NodeIntent.Spare && Counted(x));
            if (serving >= target.Serving && spares >= target.Spares
                && SlotOf(failed.Node)?.Observed is not (SpawnState.Spawning or SpawnState.Ready))
            {
                ToHistory(failed, "failed", now);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>A planned node died without a stop request: Failed until its replacement (a new id) serves. Caller holds _lock.</summary>
    private void MarkFailed(NodeRecord record, int lost, DateTimeOffset now)
    {
        record.Stop = new NodeStop("failed", "registry", null, $"the node died holding {lost} partition(s)", now, now, true)
            { PartitionsAtStop = lost };
        KillSlot(SlotOf(record.Node), now);
        Emit("node.failed", record.Node, new Dictionary<string, string> { ["host"] = record.Host, ["partitions"] = lost.ToString(CultureInfo.InvariantCulture) });
        Console.WriteLine($"Node control: {record.Node} @{record.Host} failed holding {lost} partition(s); a new id replaces it, it is never restarted");
    }

    /// <summary>ExpireLeases, per regular node declared Dead, before the spare promotion. Caller holds _lock.</summary>
    private void NodeControlOnDeath(string node, int lost, DateTimeOffset now)
    {
        if (_state.ManagedDeployment is null && _state.Phase is not (ClusterPhase.Draining or ClusterPhase.Down) && !Control.AwaitingStart
            && Control.Live.TryGetValue(node, out NodeRecord? record) && record.Stop == null)
            MarkFailed(record, lost, now);
    }

    /// <summary>The host a node runs on: its record, its own registration, else its slot.</summary>
    private string? HostOfNode(string node) =>
        Control.Live.GetValueOrDefault(node)?.Host ?? _state.Nodes.GetValueOrDefault(node)?.Host
            ?? (_state.NodePlan.GetValueOrDefault(node) ?? _state.NodePlan.Values.FirstOrDefault(x => x.ObservedNode == node))?.Host;

    /// <summary>Tells the launcher to stop the slot's process (never to restart it). Caller holds _lock.</summary>
    private bool KillSlot(NodeSlotPlan? slot, DateTimeOffset now)
    {
        if (slot == null || slot.Desired == NodeDesiredState.Kill) return false;
        slot.Desired = NodeDesiredState.Kill;
        slot.RetryAt = null;
        _state.NodePlanUpdated = now;
        return true;
    }

    /// <summary>Starts the drain of a stopped node once it is registered: a serving node retires through T-0312's pinned
    /// drain; a spare or a node still starting owns nothing it could hand over and closes directly.</summary>
    private bool InitiateStop(NodeRecord record, NodeRegistration row, NodeSlotPlan? slot, DateTimeOffset now)
    {
        bool changed = false;
        if (row.State == NodeState.Active)
        {
            bool servingRegular = row.Role == NodeRole.Regular && row.Serving && !row.Spare && !_state.SpareNodes.Contains(row.Node);
            if (servingRegular)
            {
                if (!_state.Scale.Retiring.Contains(row.Node))
                {
                    _state.Scale.Retiring.Add(row.Node);
                    _state.Scale.Retired.Remove(row.Node);
                    Emit("scale.retiring", row.Node, new Dictionary<string, string> { ["why"] = record.Stop!.Kind });
                    Console.WriteLine($"Node control: {row.Node} @{record.Host} drains ({record.Stop.Kind} by {record.Stop.RequestedBy ?? record.Stop.Actor}: {record.Stop.Why})");
                    changed = true;
                }
            }
            else
            {
                row.State = NodeState.Closed;
                row.ForceDrainAt ??= now + TimeSpan.FromSeconds(60);
                Emit("node.closing", row.Node, new Dictionary<string, string> { ["epoch"] = row.Epoch.ToString(CultureInfo.InvariantCulture) });
                Console.WriteLine($"Node control: {row.Node} @{record.Host} ({(row.Spare ? "spare" : "not serving yet")}) closed for {record.Stop!.Kind}");
                changed = true;
            }
        }
        if (slot != null)
        {
            NodeIncumbentAction action = row.State is NodeState.Empty or NodeState.Dead ? NodeIncumbentAction.Kill : NodeIncumbentAction.Draining;
            if (slot.Desired == NodeDesiredState.Wanted) { slot.Desired = NodeDesiredState.Draining; changed = true; }
            if (slot.IncumbentNode != row.Node || slot.IncumbentEpoch != row.Epoch
                || slot.IncumbentAction == NodeIncumbentAction.None || action == NodeIncumbentAction.Kill && slot.IncumbentAction != action)
            {
                slot.IncumbentNode = row.Node;
                slot.IncumbentEpoch = row.Epoch;
                slot.IncumbentAction = slot.IncumbentAction == NodeIncumbentAction.Kill ? NodeIncumbentAction.Kill : action;
                slot.RetryAt = null;
                _state.NodePlanUpdated = now;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// The drain deadline passed (or --now): the node's OWN close drain is forced - Closed with ForceDrainAt now, so it
    /// writes its Finals and offers each partition offline (T-0237), releasing memberless rows as it goes. Never a
    /// kill: a node that still owns rows afterwards stays Stopping until its drain or its lease ends.
    /// </summary>
    private void ForceStop(NodeRecord record, NodeRegistration row, DateTimeOffset now)
    {
        NodeStop stop = record.Stop!;
        stop.Forced = true;
        stop.ForcedAt = now;
        stop.PausedSince = null;
        if (row.State == NodeState.Active) row.State = NodeState.Closed;
        if (row.ForceDrainAt == null || row.ForceDrainAt > now) row.ForceDrainAt = now;
        if (SlotOf(record.Node) is { } slot && slot.IncumbentAction == NodeIncumbentAction.None) SetIncumbent(slot, row, now);
        Emit("node.forced", record.Node, new Dictionary<string, string>
        {
            ["kind"] = stop.Kind,
            ["partitions"] = OwnedPartitions(record.Node).ToString(CultureInfo.InvariantCulture),
            ["now"] = stop.Now ? "true" : "false",
        });
        Console.WriteLine($"Node control: {record.Node} @{record.Host} forced ({(stop.Now ? "--now" : "drain deadline passed")}): "
            + $"{OwnedPartitions(record.Node)} partition(s) go offline through its own drain");
    }

    private bool SetIncumbent(NodeSlotPlan slot, NodeRegistration row, DateTimeOffset now)
    {
        slot.IncumbentNode = row.Node;
        slot.IncumbentEpoch = row.Epoch;
        slot.IncumbentAction = row.State == NodeState.Empty ? NodeIncumbentAction.Kill : NodeIncumbentAction.Draining;
        if (slot.Desired == NodeDesiredState.Wanted) slot.Desired = NodeDesiredState.Draining;
        _state.NodePlanUpdated = now;
        return true;
    }

    /// <summary>Moves a live record to the bounded history (the registration row stays: it may hold saves). Caller holds _lock.</summary>
    private void ToHistory(NodeRecord record, string outcome, DateTimeOffset now)
    {
        NodeRegistration? row = _state.Nodes.GetValueOrDefault(record.Node);
        NodeStop? stop = record.Stop;
        DateTimeOffset started = record.StartedAt ?? record.PlannedAt;
        var entry = new NodeHistoryRecord(++Control.HistorySeq, record.Node, record.Host, record.Pid ?? row?.Pid, started, now,
            Math.Max(0, (now - started).TotalSeconds), outcome, stop?.Actor ?? "registry", stop?.RequestedBy,
            stop?.Why ?? (record.StartedAt == null ? "never started" : "stopped"), stop?.Forced ?? false, record.ReplacedBy,
            stop?.PartitionsAtStop ?? 0);
        _state.NodeHistory.Add(entry);
        HistoryIds.Add(record.Node);
        if (_state.NodeHistory.Count > NodeHistoryLimit)
        {
            // Pruned in batches (a twentieth of the bound) so the tracked table is rewritten once per batch, not per stop.
            int batch = Math.Max(1, NodeHistoryLimit / 20);
            _state.NodeHistory.RemoveRange(0, Math.Min(_state.NodeHistory.Count, _state.NodeHistory.Count - NodeHistoryLimit + batch));
            _historyIds = null;
        }
        Control.Live.Remove(record.Node);
        _state.SpareNodes.Remove(record.Node);
        _state.Scale.Retiring.Remove(record.Node);
        _state.Scale.Retired.Remove(record.Node);
        if (SlotOf(record.Node) is { } slot)
        {
            if (slot.Observed is SpawnState.Spawning or SpawnState.Ready) KillSlot(slot, now);
            else _state.NodePlan.Remove(record.Node);
            _state.NodePlanUpdated = now;
        }
        Emit("node.gone", record.Node, new Dictionary<string, string> { ["host"] = record.Host, ["outcome"] = outcome });
        Console.WriteLine($"Node control: {record.Node} @{record.Host} gone ({outcome}{(entry.Forced ? ", forced" : "")}, age {entry.AgeSeconds:F0}s); in the history");
    }

    /// <summary>Slots of history ids are dropped once no process runs in them; until then they say Kill.</summary>
    private bool CleanHistorySlots(DateTimeOffset now)
    {
        bool changed = false;
        foreach (NodeSlotPlan slot in _state.NodePlan.Values.Where(x => HistoryIds.Contains(x.SlotKey) && !Control.Live.ContainsKey(x.SlotKey)).ToArray())
        {
            if (slot.Observed is SpawnState.Spawning or SpawnState.Ready) changed |= KillSlot(slot, now);
            else
            {
                _state.NodePlan.Remove(slot.SlotKey);
                _state.NodePlanUpdated = now;
                changed = true;
            }
        }
        return changed;
    }

    // ---------------------------------------------------------------- planner

    /// <summary>
    /// One host toward its targets (spec "Operations"): an increase activates a warm spare on the host first, then the
    /// spares are refilled with NEW ids, and fresh serving nodes cover only what spares could not; a decrease stops the
    /// oldest spare / the OLDEST serving node (earliest process start). Caller holds _lock.
    /// </summary>
    private bool Reconcile(string host, HostTarget target, DateTimeOffset now, string actor, string? requestedBy,
        Func<int, string?>? capacity, List<string> actions, List<string> warnings, bool servingFirst, string? reason = null)
    {
        // The operator's reason leads a decrease stop's why (the history's "why"); the generated part says what changed.
        string Why(string what) => reason is null ? what : $"{reason} ({what})";
        bool changed = false;
        List<NodeRecord> serving = [.. Control.Live.Values.Where(x => x.Host == host && x.Intent == NodeIntent.Serving && Counted(x))];
        List<NodeRecord> spares = [.. Control.Live.Values.Where(x => x.Host == host && x.Intent == NodeIntent.Spare && Counted(x))];
        while (serving.Count < target.Serving)
        {
            NodeRecord? warm = spares
                .Select(x => (Record: x, Row: _state.Nodes.GetValueOrDefault(x.Node)))
                .Where(x => x.Row is { State: NodeState.Active, Serving: true } && x.Row.LeaseExpires > now)
                .OrderBy(x => x.Row!.Registered).ThenBy(x => NodeNumber(x.Record.Node)).Select(x => x.Record).FirstOrDefault();
            if (warm == null) break;
            NodeRegistration row = _state.Nodes[warm.Node];
            _state.SpareNodes.Remove(warm.Node);
            row.Spare = false;
            row.Activated = now;
            warm.Intent = NodeIntent.Serving;
            spares.Remove(warm);
            serving.Add(warm);
            AssignReplacement(host, warm.Node);
            Emit("node.spare", warm.Node, new Dictionary<string, string> { ["spare"] = false.ToString() });
            Emit("node.activated", warm.Node, new Dictionary<string, string> { ["host"] = host });
            actions.Add($"activated spare {warm.Node} @{host}");
            Console.WriteLine($"Node control: spare {warm.Node} @{host} activated (serving {serving.Count}/{target.Serving})");
            changed = true;
        }
        void FillServing()
        {
            while (serving.Count < target.Serving) { serving.Add(PlanNode(host, NodeIntent.Serving, now, actions)); changed = true; }
        }
        void FillSpares()
        {
            while (spares.Count < target.Spares) { spares.Add(PlanNode(host, NodeIntent.Spare, now, actions)); changed = true; }
        }
        if (servingFirst) { FillServing(); FillSpares(); }
        else { FillSpares(); FillServing(); }
        foreach (NodeRecord victim in Oldest(spares).Take(Math.Max(0, spares.Count - target.Spares)).ToArray())
        {
            RequestStop(victim, "decrease", actor, requestedBy, Why($"spares on {host} lowered to {target.Spares}"), now, DefaultDrainTimeout, immediate: false);
            actions.Add($"stopping spare {victim.Node} @{host} (oldest spare)");
            changed = true;
        }
        int excess = serving.Count - target.Serving;
        if (excess > 0)
        {
            foreach (NodeRecord victim in Oldest(serving).Take(excess).ToArray())
            {
                RequestStop(victim, "decrease", actor, requestedBy, Why($"serving on {host} lowered to {target.Serving}"), now, DefaultDrainTimeout, immediate: false);
                actions.Add($"draining {victim.Node} @{host} (oldest serving)");
            }
            // Obeyed with a warning, never refused (spec "Capacity").
            if (capacity?.Invoke(ServingTargetTotalLocked()) is { } warning) warnings.Add(warning);
            changed = true;
        }
        return changed;
    }

    private NodeRecord PlanNode(string host, NodeIntent intent, DateTimeOffset now, List<string> actions)
    {
        string id = AllocateNodeId();
        var record = new NodeRecord(id, host) { Intent = intent, PlannedAt = now };
        Control.Live[id] = record;
        if (intent == NodeIntent.Spare) _state.SpareNodes.Add(id);
        var slot = new NodeSlotPlan(id, host, NodeRole.Regular, NodeDesiredState.Wanted);
        // A new id is a new slot with no spawn failures of its own, so a node that crash-loops would relaunch at once
        // forever under ever new ids. Deaths on the host in the spawn-failure window back the next launch off instead.
        int failures = RecentFailures(host, now);
        if (failures > 0)
        {
            slot.RetryAt = now + SpawnBackoff(failures);
            actions.Add($"{id} launch delayed {SpawnBackoff(failures).TotalSeconds:F0}s: {failures} node(s) on {host} failed in the last {SpawnFailureWindow.TotalMinutes:F0} min");
        }
        _state.NodePlan[id] = slot;
        _state.NodePlanUpdated = now;
        if (intent == NodeIntent.Serving) AssignReplacement(host, id);
        Emit("node.planned", id, new Dictionary<string, string> { ["host"] = host, ["intent"] = intent.ToString() });
        actions.Add($"planned {(intent == NodeIntent.Spare ? "spare" : "serving")} {id} @{host}");
        Console.WriteLine($"Node control: planned {intent.ToString().ToLowerInvariant()} node {id} @{host}");
        return record;
    }

    /// <summary>Nodes of the host that died (not stopped on request) within the spawn-failure window.</summary>
    private int RecentFailures(string host, DateTimeOffset now) =>
        Control.Live.Values.Count(x => x.Host == host && x.Stop is { Kind: "failed" } stop && now - stop.RequestedAt <= SpawnFailureWindow)
        + _state.NodeHistory.Reverse<NodeHistoryRecord>().TakeWhile(x => now - x.EndedAt <= SpawnFailureWindow + TimeSpan.FromMinutes(1))
            .Count(x => x.Host == host && x.Outcome == "failed" && now - x.EndedAt <= SpawnFailureWindow);

    /// <summary>A new serving node replaces the oldest recycled or failed node on its host that has no replacement yet.</summary>
    private void AssignReplacement(string host, string replacement)
    {
        NodeRecord? replaced = Control.Live.Values
            .Where(x => x.Host == host && x.ReplacedBy == null && x.Node != replacement && x.Stop?.Kind is "recycle" or "failed")
            .OrderBy(x => x.Stop!.RequestedAt).ThenBy(x => NodeNumber(x.Node)).FirstOrDefault();
        if (replaced == null) return;
        replaced.ReplacedBy = replacement;
        if (Control.Live.TryGetValue(replacement, out NodeRecord? successor)) successor.Replaces = replaced.Node;
    }

    private void RequestStop(NodeRecord record, string kind, string actor, string? requestedBy, string why, DateTimeOffset now,
        TimeSpan timeout, bool immediate)
    {
        record.Stop = new NodeStop(kind, actor, requestedBy, why, now, immediate ? now : now + timeout, immediate)
            { PartitionsAtStop = OwnedPartitions(record.Node) };
        Emit("node.stop-requested", record.Node, new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["actor"] = actor,
            ["requestedBy"] = requestedBy ?? "",
            ["why"] = why,
            ["now"] = immediate ? "true" : "false",
        });
        Console.WriteLine($"Node control: stop {record.Node} @{record.Host} ({kind} by {requestedBy ?? actor}: {why}; "
            + (immediate ? "--now" : $"force after {timeout.TotalSeconds:F0}s") + ")");
        if (_state.Nodes.TryGetValue(record.Node, out NodeRegistration? row) && row.State != NodeState.Dead)
        {
            InitiateStop(record, row, SlotOf(record.Node), now);
            if (immediate && row.State is NodeState.Active or NodeState.Closed) ForceStop(record, row, now);
        }
    }

    private int ServingTargetTotalLocked() => Control.Hosts.Values.Sum(x => x.Serving);

    /// <summary>Sum of the per-host serving targets (the capacity warning's denominator).</summary>
    public int ServingTargetTotal()
    {
        lock (_lock) return ServingTargetTotalLocked();
    }

    /// <summary>For ScaleInPass: a recycled node's activated replacement receives its whole drain.</summary>
    public string? PreferredDrainReceiver(string node)
    {
        lock (_lock)
        {
            if (_state.ManagedRotation is { } recycle && recycle.Node == node) return recycle.Spare;
            if (!Control.Live.TryGetValue(node, out NodeRecord? record) || record.ReplacedBy is not { } receiver) return null;
            return _state.Nodes.TryGetValue(receiver, out NodeRegistration? row) && row.State == NodeState.Active && row.Serving
                && !row.Spare && !_state.SpareNodes.Contains(receiver) ? receiver : null;
        }
    }

    // ---------------------------------------------------------------- mutations

    private void RequireMutable()
    {
        if (_state.ManagedDeployment is not null)
            throw new NodeControlException(409, "managed_inventory_fixed",
                "A managed deployment's node inventory is fixed by its approved deployment spec; its hot spares are designated slots of that spec.");
        EnsurePersistenceHealthy();
    }

    private static void ValidateCount(int value, string name)
    {
        if (value < 0 || value > MaxHostNodes)
            throw new NodeControlException(400, "invalid_arguments", $"{name} must be 0..{MaxHostNodes}, got {value}");
    }

    /// <summary>Runtime targets for one host (lasts until the next start applies the hosts file).</summary>
    public NodeControlChange SetHostTargets(string host, int? serving, int? spares, string actor, string? requestedBy, string? reason,
        Func<int, string?>? capacity = null)
    {
        lock (_lock)
        {
            (HostTarget current, HostTarget next) = CheckHostTargetsLocked(host, serving, spares);
            DateTimeOffset now = _clock();
            List<string> actions = [], warnings = [];
            if (!NodeControlActive) AdoptRunningNodes(host, now, actions);
            Control.Hosts[host] = next;
            Control.AwaitingStart = false;   // an explicit target is an operator decision: plan for it now
            Control.Revision++;
            SyncScaleTarget(now);
            Emit("node-control.targets", host, new Dictionary<string, string>
            {
                ["serving"] = next.Serving.ToString(CultureInfo.InvariantCulture),
                ["spares"] = next.Spares.ToString(CultureInfo.InvariantCulture),
                ["actor"] = actor,
                ["requestedBy"] = requestedBy ?? "",
            });
            Console.WriteLine($"Node control: {host} serving {current.Serving}->{next.Serving}, spares {current.Spares}->{next.Spares}"
                + $" (by {requestedBy ?? actor}{(reason != null ? ": " + reason : "")})");
            NodeControlStep(now, actor, requestedBy, capacity, actions, warnings, reason);
            Persist();
            return new NodeControlChange(Control.Revision, null, null, [.. actions], [.. warnings]);
        }
    }

    /// <summary>The checks of <see cref="SetHostTargets"/> without its effect (the admin route answers them as 4xx).</summary>
    public void CheckHostTargets(string host, int? serving, int? spares)
    {
        lock (_lock) CheckHostTargetsLocked(host, serving, spares);
    }

    private (HostTarget Current, HostTarget Next) CheckHostTargetsLocked(string host, int? serving, int? spares)
    {
        RequireMutable();
        if (string.IsNullOrWhiteSpace(host)) throw new NodeControlException(400, "invalid_arguments", "A host name is required");
        if (serving == null && spares == null) throw new NodeControlException(400, "invalid_arguments", "Set serving, spares or both");
        if (serving is int s) ValidateCount(s, "serving");
        if (spares is int p) ValidateCount(p, "spares");
        if (NodeControlActive && !Control.Hosts.ContainsKey(host))
            throw new NodeControlException(404, "host_not_found",
                $"Host '{host}' is not in the hosts file ({string.Join(", ", Control.Hosts.Keys.Order())}); hosts change only with a restart");
        HostTarget current = Control.Hosts.GetValueOrDefault(host) ?? new HostTarget(0, 0);
        HostTarget next = current with { Serving = serving ?? current.Serving, Spares = spares ?? current.Spares };
        if (Control.Hosts.Where(x => x.Key != host).Sum(x => x.Value.Serving) + next.Serving < 1)
            throw new NodeControlException(409, "last_serving_node", "The cluster needs at least one serving node");
        return (current, next);
    }

    /// <summary>The checks of remove/recycle without the effect: managed, selector, a repeated stop, the last serving node.</summary>
    public string CheckStop(string selector, string kind, bool immediate)
    {
        lock (_lock)
        {
            RequireMutable();
            NodeRecord record = ResolveSelectorLocked(selector);
            CheckStopLocked(record, kind, immediate);
            return record.Node;
        }
    }

    private void CheckStopLocked(NodeRecord record, string kind, bool immediate)
    {
        if (record.Stop != null)
        {
            if (!immediate || record.Stop.Kind == "failed" || record.Stop.Forced)
                throw new NodeControlException(409, "already_stopping", $"{record.Node} is already {record.Stop.Kind}"
                    + (record.Stop.Forced ? " (forced)" : "") + $" since {record.Stop.RequestedAt:O}");
            return;
        }
        if (kind == "remove" && record.Intent == NodeIntent.Serving
            && Control.Hosts.Sum(x => x.Value.Serving) - 1 < 1)
            throw new NodeControlException(409, "last_serving_node", $"{record.Node} is the cluster's last serving node");
    }

    /// <summary>First runtime target on a cluster node control did not start: the nodes already running are taken over
    /// (no slot: whatever started them stops them), so the planner counts them instead of launching a second set.</summary>
    private void AdoptRunningNodes(string host, DateTimeOffset now, List<string> actions)
    {
        foreach (NodeRegistration row in _state.Nodes.Values.Where(x => x.Role == NodeRole.Regular && x.State is NodeState.Active or NodeState.Closed
            && x.LeaseExpires > now && !Control.Live.ContainsKey(x.Node) && !HistoryIds.Contains(x.Node)).OrderBy(x => x.Registered))
        {
            Control.Live[row.Node] = new NodeRecord(row.Node, string.IsNullOrWhiteSpace(row.Host) ? host : row.Host)
            {
                Intent = row.Spare || _state.SpareNodes.Contains(row.Node) ? NodeIntent.Spare : NodeIntent.Serving,
                Pid = row.Pid,
                PlannedAt = row.Registered,
                StartedAt = row.Registered,
                Adopted = true,
            };
            actions.Add($"adopted running {row.Node}");
        }
    }

    /// <summary>T-0312 readers (ScaleStatus, cluster scale) keep a total: the sum of the serving targets.</summary>
    private void SyncScaleTarget(DateTimeOffset now)
    {
        int total = ServingTargetTotalLocked();
        if (_state.Scale.Target == total) return;
        _state.Scale.Target = total;
        _state.Scale.Revision++;
        _state.Scale.RequestedAt = now;
        _state.Scale.ReachedAt = null;
    }

    public NodeControlChange RemoveNode(string selector, bool now, TimeSpan? timeout, string actor, string? requestedBy, string? reason)
        => StopNode(selector, "remove", now, timeout, actor, requestedBy, reason);

    public NodeControlChange RecycleNode(string selector, bool now, TimeSpan? timeout, string actor, string? requestedBy, string? reason)
        => StopNode(selector, "recycle", now, timeout, actor, requestedBy, reason);

    private NodeControlChange StopNode(string selector, string kind, bool immediate, TimeSpan? timeout, string actor, string? requestedBy, string? reason)
    {
        lock (_lock)
        {
            RequireMutable();
            NodeRecord record = ResolveSelectorLocked(selector);
            CheckStopLocked(record, kind, immediate);
            DateTimeOffset now = _clock();
            List<string> actions = [], warnings = [];
            if (record.Stop != null)
            {
                // --now on a node already draining escalates it to the forced stop.
                record.Stop.Now = true;
                record.Stop.Deadline = now;
                if (_state.Nodes.TryGetValue(record.Node, out NodeRegistration? draining) && draining.State is NodeState.Active or NodeState.Closed)
                    ForceStop(record, draining, now);
                actions.Add($"forcing {record.Node} now");
                NodeControlStep(now, actor, requestedBy, null, actions, warnings);
                Persist();
                return new NodeControlChange(Control.Revision, record.Node, record.ReplacedBy, [.. actions], [.. warnings]);
            }
            if (kind == "remove")
            {
                HostTarget target = Control.Hosts.GetValueOrDefault(record.Host) ?? new HostTarget(0, 0);
                HostTarget next = record.Intent == NodeIntent.Spare
                    ? target with { Spares = Math.Max(0, target.Spares - 1) }
                    : target with { Serving = Math.Max(0, target.Serving - 1) };
                Control.Hosts[record.Host] = next;
                SyncScaleTarget(now);
                actions.Add($"{record.Host} {(record.Intent == NodeIntent.Spare ? "spares" : "serving")} target now "
                    + (record.Intent == NodeIntent.Spare ? next.Spares : next.Serving));
            }
            Control.Revision++;
            string why = reason ?? (kind == "remove" ? "removed" : "recycled");
            RequestStop(record, kind, actor, requestedBy, why, now, timeout ?? DefaultDrainTimeout, immediate);
            actions.Add($"{(immediate ? "forcing" : "draining")} {record.Node} @{record.Host} ({kind})");
            NodeControlStep(now, actor, requestedBy, null, actions, warnings);
            Persist();
            return new NodeControlChange(Control.Revision, record.Node, record.ReplacedBy, [.. actions], [.. warnings]);
        }
    }

    /// <summary>
    /// The start's hosts file (Viktor 2026-10-01: FRESH ids on every start). Every record still live goes to the history
    /// (outcome restart), its slot is dropped, and every node of the file is planned under a new id. Only the launcher's
    /// start/fresh posts it; a gateway restart keeps the runtime targets.
    /// </summary>
    public NodeControlChange ApplyHostsFile(IReadOnlyDictionary<string, HostTarget>? hosts, string? sha256, string actor)
    {
        lock (_lock)
        {
            CheckHostsFileLocked(hosts);
            DateTimeOffset now = _clock();
            List<string> actions = [], warnings = [];
            foreach (NodeRecord record in Control.Live.Values.ToArray())
            {
                record.Stop = new NodeStop("restart", "start", actor, "the start applied the hosts file (fresh ids every start)", now, now, true)
                    { PartitionsAtStop = OwnedPartitions(record.Node) };
                ToHistory(record, "restart", now);
                if (SlotOf(record.Node) is { } stale) { _state.NodePlan.Remove(stale.SlotKey); _state.NodePlanUpdated = now; }
            }
            CleanHistorySlots(now);
            Control.Hosts = hosts!.ToDictionary(x => x.Key, x => x.Value);
            Control.FileHosts = hosts!.ToDictionary(x => x.Key, x => x.Value);
            Control.HostsFileSha256 = sha256;
            Control.HostsFileAppliedAt = now;
            Control.AwaitingStart = false;
            Control.Revision++;
            SyncScaleTarget(now);
            Emit("node-control.hosts-file", null, new Dictionary<string, string> { ["sha256"] = sha256 ?? "", ["hosts"] = string.Join(",", hosts!.Keys.Order()) });
            foreach ((string host, HostTarget target) in Control.Hosts.OrderBy(x => x.Key, StringComparer.Ordinal))
                Reconcile(host, target, now, actor, null, null, actions, warnings, servingFirst: true);
            Persist();
            Flush();
            // T-0340: the first start of a run root makes node control active here, after the constructor's re-key pass
            // gave up on it; store mode must be in force before the nodes this start launches register.
            RekeyCatalogueToHostStore();
            return new NodeControlChange(Control.Revision, null, null, [.. actions], [.. warnings]);
        }
    }

    /// <summary>The checks of <see cref="ApplyHostsFile"/> without the effect.</summary>
    public void CheckHostsFile(IReadOnlyDictionary<string, HostTarget>? hosts)
    {
        lock (_lock) CheckHostsFileLocked(hosts);
    }

    private void CheckHostsFileLocked(IReadOnlyDictionary<string, HostTarget>? hosts)
    {
        RequireMutable();
        if (hosts == null || hosts.Count == 0) throw new NodeControlException(400, "invalid_arguments", "The hosts file names no host");
        foreach ((string host, HostTarget target) in hosts)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new NodeControlException(400, "invalid_arguments", "Empty host name");
            ValidateCount(target.Serving, $"hosts.{host}.serving");
            ValidateCount(target.Spares, $"hosts.{host}.spares");
        }
        if (hosts.Values.Sum(x => x.Serving) < 1) throw new NodeControlException(400, "invalid_arguments", "The hosts file must ask for at least one serving node");
    }

    /// <summary>T-0312's POST /registry/scale?nodes=N, kept one release: the sole host's serving target.</summary>
    public NodeControlChange ScaleShim(int nodes, string actor, Func<int, string?>? capacity = null)
    {
        string host;
        lock (_lock)
        {
            string[] hosts = NodeControlActive
                ? [.. Control.Hosts.Keys]
                : [.. _state.Nodes.Values.Where(x => x.Role == NodeRole.Regular && x.State is NodeState.Active or NodeState.Closed)
                    .Select(x => string.IsNullOrWhiteSpace(x.Host) ? Environment.MachineName.Split('.')[0] : x.Host!).Distinct()];
            if (hosts.Length == 0) hosts = [Environment.MachineName.Split('.')[0]];
            if (hosts.Length != 1)
                throw new NodeControlException(409, "multiple_hosts", $"/registry/scale sets one host's count; this cluster has {string.Join(", ", hosts)}: use cluster nodes set HOST");
            host = hosts[0];
        }
        return SetHostTargets(host, nodes, null, actor, "registry/scale", $"scale to {nodes}", capacity);
    }

    // ---------------------------------------------------------------- selectors

    /// <summary>Resolves node-N, PID@host or host#position (k-th live node on the host by start, oldest #1).</summary>
    public string ResolveSelector(string selector)
    {
        lock (_lock)
        {
            RequireMutable();
            return ResolveSelectorLocked(selector).Node;
        }
    }

    private NodeRecord ResolveSelectorLocked(string selector)
    {
        if (!NodeControlActive)
            throw new NodeControlException(409, "node_control_inactive",
                "Node control is not active: the start applies the hosts file, or set a host's targets first");
        selector = (selector ?? "").Trim();
        if (selector.Length == 0) throw new NodeControlException(400, "invalid_arguments", "A node selector is required (node-N, PID@host or host#position)");
        int hash = selector.LastIndexOf('#');
        int at = selector.IndexOf('@');
        NodeRecord[] matches;
        if (hash > 0)
        {
            string host = selector[..hash];
            if (!int.TryParse(selector.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int position) || position < 1)
                throw new NodeControlException(400, "invalid_arguments", $"'{selector}': the position after # is 1-based");
            NodeRecord[] onHost = [.. Oldest(Control.Live.Values.Where(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)))];
            matches = position <= onHost.Length ? [onHost[position - 1]] : [];
        }
        else if (at > 0 && int.TryParse(selector.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            string host = selector[(at + 1)..];
            matches = [.. Control.Live.Values.Where(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)
                && (x.Pid ?? _state.Nodes.GetValueOrDefault(x.Node)?.Pid) == pid)];
        }
        else
            matches = Control.Live.TryGetValue(selector, out NodeRecord? byId) ? [byId] : [];
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            throw new NodeControlException(409, "selector_ambiguous", $"'{selector}' matches {string.Join(", ", matches.Select(x => x.Node))}");
        throw new NodeControlException(404, "selector_not_found", HistoryIds.Contains(selector)
            ? $"{selector} is stopped (see node history)" : $"No live node matches '{selector}'");
    }

    // ---------------------------------------------------------------- views

    public Admin.NodeControlView NodeControlView(IReadOnlyDictionary<string, int>? clientsByNode, string? capacityWarning,
        string gatewayHost, int gatewayPid)
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            List<(NodeRecord Record, NodeRegistration? Row, Admin.OperatorNodeState State)> entries = [];
            if (_state.ManagedDeployment is { } deployment)
            {
                foreach (ManagedSlot slot in deployment.Slots.Where(x => x.Role == NodeRole.Regular))
                {
                    NodeRegistration? row = _state.Nodes.GetValueOrDefault(slot.Node);
                    var record = new NodeRecord(slot.Node, slot.Host) { StartedAt = row?.Registered, PlannedAt = row?.Registered ?? default, Pid = row?.Pid };
                    entries.Add((record, row, row == null ? Admin.OperatorNodeState.Starting : RowState(row)));
                }
            }
            else if (!NodeControlActive)
            {
                foreach (NodeRegistration row in _state.Nodes.Values.Where(x => x.Role == NodeRole.Regular
                    && (x.State is NodeState.Active or NodeState.Closed || x.State == NodeState.Empty && x.LeaseExpires > now)))
                {
                    string host = row.Host ?? _state.NodePlan.Values.FirstOrDefault(x => x.ObservedNode == row.Node)?.Host ?? "?";
                    var record = new NodeRecord(row.Node, host) { StartedAt = row.Registered, PlannedAt = row.Registered, Pid = row.Pid };
                    entries.Add((record, row, RowState(row)));
                }
            }
            else
            {
                foreach (NodeRecord record in Control.Live.Values)
                {
                    NodeRegistration? row = _state.Nodes.GetValueOrDefault(record.Node);
                    entries.Add((record, row, OperatorState(record, row, SlotOf(record.Node))));
                }
            }
            List<Admin.OperatorNode> nodes = [];
            foreach (IGrouping<string, (NodeRecord Record, NodeRegistration? Row, Admin.OperatorNodeState State)> host in entries
                .GroupBy(x => x.Record.Host).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                int position = 0;
                Dictionary<string, (NodeRegistration? Row, Admin.OperatorNodeState State)> byNode = host.ToDictionary(x => x.Record.Node, x => (x.Row, x.State));
                foreach (NodeRecord record in Oldest(host.Select(x => x.Record)))
                {
                    (NodeRegistration? row, Admin.OperatorNodeState state) = byNode[record.Node];
                    position++;
                    DateTimeOffset? started = record.StartedAt;
                    nodes.Add(new Admin.OperatorNode(record.Node, record.Host, position, record.Pid ?? row?.Pid, started,
                        state == Admin.OperatorNodeState.Serving || state == Admin.OperatorNodeState.Draining ? row?.Activated : null,
                        started is { } s ? Math.Max(0, (now - s).TotalSeconds) : null, state, OwnedPartitions(record.Node),
                        clientsByNode?.GetValueOrDefault(record.Node) ?? 0, StopView(record, row, now), record.Replaces, record.ReplacedBy));
                }
            }
            List<Admin.HostView> hosts = [];
            IEnumerable<string> hostNames = Control.Hosts.Keys.Concat(Control.FileHosts.Keys).Concat(entries.Select(x => x.Record.Host))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (string host in hostNames)
            {
                HostTarget? target = Control.Hosts.GetValueOrDefault(host);
                HostTarget? file = Control.FileHosts.GetValueOrDefault(host);
                Admin.OperatorNode[] onHost = [.. nodes.Where(x => x.Host == host)];
                if (_state.ManagedDeployment is { } managed)
                {
                    // The fixed slot pool, split by the current designation: a spare that replaced a dead node on another
                    // host moved the spare there (its slot came back as the spare).
                    ManagedSlot[] regular = [.. managed.Slots.Where(x => x.Role == NodeRole.Regular && x.Host == host)];
                    int spares = regular.Count(x => _state.SpareNodes.Contains(x.Node));
                    target = new HostTarget(regular.Length - spares, spares);
                }
                List<string> hostWarnings = [];
                if (capacityWarning != null && onHost.Any(x => x.State == Admin.OperatorNodeState.Draining)) hostWarnings.Add(capacityWarning);
                foreach (Admin.OperatorNode node in onHost)
                    if (SlotOf(node.Node) is { LastFailure: { } failure } slot && slot.Desired == NodeDesiredState.Wanted)
                        hostWarnings.Add($"{node.Node}: launch failed ({slot.FailureCount}x): {failure}"
                            + (slot.RetryAt is { } retry && retry > now ? $"; retry in {(retry - now).TotalSeconds:F0}s" : ""));
                if (RecentFailures(host, now) is int failed and > 0)
                    hostWarnings.Add($"{failed} node(s) failed in the last {SpawnFailureWindow.TotalMinutes:F0} min; replacement launches back off");
                int starting = onHost.Count(x => x.State == Admin.OperatorNodeState.Starting);
                if (target != null && onHost.Count(x => x.State == Admin.OperatorNodeState.Serving) < target.Serving && starting > 0)
                    hostWarnings.Add($"{starting} node(s) still starting");
                hosts.Add(new Admin.HostView(host,
                    new Admin.TargetCount(target?.Serving ?? 0, file?.Serving, onHost.Count(x => x.State == Admin.OperatorNodeState.Serving)),
                    new Admin.TargetCount(target?.Spares ?? 0, file?.Spares, onHost.Count(x => x.State == Admin.OperatorNodeState.Spare)),
                    target?.RamFloorMb ?? file?.RamFloorMb, target?.DiskFloorMb ?? file?.DiskFloorMb, [.. hostWarnings]));
            }
            List<Admin.NotCountedNode> notCounted = [];
            foreach (NodeRegistration wa in _state.Nodes.Values.Where(x => x.Role == NodeRole.WA && x.State is NodeState.Active or NodeState.Closed))
                notCounted.Add(new Admin.NotCountedNode(wa.Node, "WorldAuthority", wa.Host ?? _state.NodePlan.Values.FirstOrDefault(x => x.ObservedNode == wa.Node)?.Host,
                    wa.Pid, wa.Registered, Math.Max(0, (now - wa.Registered).TotalSeconds)));
            notCounted.Add(new Admin.NotCountedNode("gateway", "Gateway", gatewayHost, gatewayPid, null, null));
            return new Admin.NodeControlView(Control.Revision, DiffersFromFile(), new Admin.HostsFileInfo(Control.HostsFileSha256, Control.HostsFileAppliedAt),
                [.. hosts], [.. nodes], [.. notCounted], NodeControlActive, _state.ManagedDeployment is not null);
        }
    }

    private bool DiffersFromFile()
    {
        if (Control.FileHosts.Count == 0) return false;
        return Control.Hosts.Count != Control.FileHosts.Count || Control.Hosts.Any(x =>
            !Control.FileHosts.TryGetValue(x.Key, out HostTarget? file) || file.Serving != x.Value.Serving || file.Spares != x.Value.Spares);
    }

    private Admin.OperatorNodeStop? StopView(NodeRecord record, NodeRegistration? row, DateTimeOffset now)
    {
        if (record.Stop is not { } stop) return null;
        DateTimeOffset deadline = stop.PausedSince is { } paused ? stop.Deadline + (now - paused) : stop.Deadline;
        int owned = OwnedPartitions(record.Node);
        string? detail = stop.Kind == "failed" ? (record.ReplacedBy != null ? $"replaced by {record.ReplacedBy}" : "waiting for a replacement")
            : stop.Forced ? (owned != 0 && row?.State != NodeState.Dead
                ? $"forced; still owns {owned} partition(s), its own drain takes them offline (never killed while it owns rows)"
                : "forced; waiting for the launcher to stop the process")
            : stop.PausedSince != null ? "drain deadline paused: partitions are still recovering"
            : row?.State == NodeState.Empty ? "drained; waiting for the launcher to stop the process"
            : null;
        return new Admin.OperatorNodeStop(stop.Kind, stop.Actor, stop.Why, deadline, stop.Forced, stop.RequestedBy, stop.RequestedAt,
            stop.ForcedAt, stop.Now, detail);
    }

    public Admin.NodeHistoryPage NodeHistoryPage(int limit, long? cursor, string? host)
    {
        lock (_lock)
        {
            limit = Math.Clamp(limit, 1, MaxNodeHistory);
            List<Admin.NodeHistoryEntry> page = [];
            bool more = false;
            for (int index = _state.NodeHistory.Count - 1; index >= 0; index--)
            {
                NodeHistoryRecord entry = _state.NodeHistory[index];
                if (cursor is long before && entry.Seq >= before) continue;
                if (host != null && !string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                if (page.Count == limit) { more = true; break; }
                page.Add(new Admin.NodeHistoryEntry(entry.Seq, entry.Node, entry.Host, entry.Pid, entry.StartedAt, entry.EndedAt,
                    entry.AgeSeconds, entry.Outcome, entry.Actor, entry.RequestedBy, entry.Why, entry.Forced, entry.ReplacedBy,
                    entry.PartitionsAtStop));
            }
            return new Admin.NodeHistoryPage([.. page], more ? page[^1].Seq : null);
        }
    }

    /// <summary>Whether an idempotency key already names an operation (the retry replays, nothing is re-validated).</summary>
    public bool AdminOperationKeyExists(string idempotencyKey)
    {
        lock (_lock) return _state.AdminOperations.Values.Any(x => x.IdempotencyKey == idempotencyKey);
    }

    /// <summary>A launcher observation carries the pid a node without the Pid field did not report.</summary>
    private bool NodeControlObservedPid(SpawnObservation report)
    {
        if (report.Pid is not int pid || report.Node is not string node || !Control.Live.TryGetValue(node, out NodeRecord? record) || record.Pid != null)
            return false;
        record.Pid = pid;
        return true;
    }
}
