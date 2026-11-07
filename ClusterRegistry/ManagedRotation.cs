using System.Globalization;

namespace ClusterRegistry;

/// <summary>SE-1168: one lifetime recycle of a managed node: <paramref name="Node"/> incarnation <paramref name="Epoch"/>
/// drains into the activated <paramref name="Spare"/>, and its slot restarts as the new spare.</summary>
internal sealed record ManagedRecycle(string Node, long Epoch, string Spare, DateTimeOffset StartedAt)
{
    /// <summary>The drain timeout passed and the node was closed: its own drain takes the rest offline.</summary>
    public bool Forced { get; set; }
}

/// <summary>
/// SE-1168: node rotation of a managed (Quasar) deployment. Before it, only the regression harness's rotator recycled
/// nodes, through node control, which a managed deployment's fixed inventory refuses (409 managed_inventory_fixed); no
/// Quasar cluster ever recycled a node, and on octahedron-test the regular nodes ran 11-13 h and grew to 18-23 GB.
///
/// Every <c>rotation.lifetimeSeconds</c> / n (n = serving regular nodes) the oldest serving regular node, if it has
/// served that long, is recycled onto the hot spare, so in steady state each node serves about one lifetime. One
/// recycle at a time: the spare is activated, the node retires through the scale-in drain (its components are pinned
/// to the spare, clients last), and once it owns nothing it is closed; its Empty marks the slot's incumbent for the
/// kill, Host kills that incarnation, and the still-wanted slot restarts as the new spare (the slot is designated a
/// spare once the old incarnation is Empty or Dead, so the new one registers holding nothing). Waits while the cluster
/// is not Serving, a recovery is pending, any node is retiring or closed, any slot has an incumbent action, or no live,
/// proven spare exists. After <c>rotation.drainTimeoutSeconds</c> the node is closed anyway: its own close drain hands
/// out or offlines what is left. Counts never change. Caller of every method holds _lock.
/// </summary>
internal sealed partial class GatewayRegistry
{
    internal const string RotationLifetimeKey = "rotation.lifetimeSeconds";
    internal const string RotationDrainTimeoutKey = "rotation.drainTimeoutSeconds";
    private string? _rotationWaitLogged;

    private double RotationSeconds(string key, double fallback) =>
        _state.HandoverConfig.TryGetValue(key, out string? value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0 ? parsed : fallback;

    /// <summary>From <see cref="NodeControlPass"/>, once a second. Returns whether anything changed (the caller persists).</summary>
    private bool ManagedRotationStep(DateTimeOffset now)
    {
        if (_state.ManagedRotation is { } recycle) return AdvanceManagedRecycle(recycle, now);
        double lifetime = RotationSeconds(RotationLifetimeKey, 7200);
        if (lifetime <= 0 || _state.Phase != ClusterPhase.Serving) return false;
        NodeRegistration[] regulars = [.. _state.Nodes.Values.Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active)];
        NodeRegistration[] serving = RotationOrder(regulars, now);
        if (serving.Length == 0) return false;
        TimeSpan spacing = TimeSpan.FromSeconds(lifetime / serving.Length);
        NodeRegistration oldest = serving[0];
        TimeSpan age = now - (oldest.Activated ?? oldest.Registered);
        if (age < spacing || _state.LastManagedRotationAt is { } last && now - last < spacing) return false;
        string? wait = _state.Scale.Retiring.Count != 0 ? "a node is retiring"
            : _state.Nodes.Values.Any(x => x.Role == NodeRole.Regular && x.State == NodeState.Closed) ? "a node is closing"
            : _state.NodePlan.Values.Any(x => x.IncumbentAction != NodeIncumbentAction.None) ? "a node slot has a kill or drain planned"
            : RecoveryPending() ? "a recovery is pending" : null;
        string? host = HostOfNode(oldest.Node);
        NodeRegistration? spare = regulars
            .Where(x => _state.SpareNodes.Contains(x.Node) && x.Serving && NodeLeaseLive(x, now) && ManagedNodeReady(x))
            .OrderBy(x => string.Equals(HostOfNode(x.Node), host, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Registered).ThenBy(x => x.Node, StringComparer.Ordinal).FirstOrDefault();
        wait ??= spare == null ? "no live hot spare" : null;
        if (wait != null)
        {
            if (_rotationWaitLogged != wait)
                Console.WriteLine($"Rotation: {oldest.Node} has served {Duration(age)} (one rotation per {Duration(spacing)}, lifetime "
                    + $"{Duration(TimeSpan.FromSeconds(lifetime))}); waiting: {wait}");
            _rotationWaitLogged = wait;
            return false;
        }
        _rotationWaitLogged = null;
        _state.SpareNodes.Remove(spare!.Node);
        spare.Spare = false;
        spare.Activated = now;
        Emit("node.spare", spare.Node, new Dictionary<string, string> { ["spare"] = false.ToString() });
        Emit("node.activated", spare.Node, new Dictionary<string, string> { ["host"] = host ?? "", ["replaces"] = oldest.Node });
        _state.Scale.Retiring.Add(oldest.Node);
        _state.Scale.Retired.Remove(oldest.Node);
        Emit("scale.retiring", oldest.Node, new Dictionary<string, string> { ["why"] = "rotation" });
        // Desired stays Wanted: the incumbent's Empty turns Draining into Kill (MarkEmpty), and after Gone the slot restarts.
        NodeSlotPlan slot = _state.NodePlan.Values.Single(x => x.Role == NodeRole.Regular
            && _state.ManagedDeployment!.Slots.Any(s => s.SlotKey == x.SlotKey && s.Node == oldest.Node));
        slot.IncumbentNode = oldest.Node;
        slot.IncumbentEpoch = oldest.Epoch;
        slot.IncumbentAction = NodeIncumbentAction.Draining;
        slot.RetryAt = null;
        _state.NodePlanUpdated = now;
        _state.ManagedRotation = new(oldest.Node, oldest.Epoch, spare.Node, now);
        _state.LastManagedRotationAt = now;
        Console.WriteLine($"Rotation: recycling {oldest.Node} epoch={oldest.Epoch} after {Duration(age)} of serving "
            + $"({OwnedPartitions(oldest.Node)} partition(s)); hot spare {spare.Node} takes its place, its slot restarts as the spare");
        return true;
    }

    /// <summary>The serving regular nodes rotation may recycle, oldest serving first: the order it recycles them in.</summary>
    private NodeRegistration[] RotationOrder(IEnumerable<NodeRegistration> regulars, DateTimeOffset now) =>
        [.. regulars.Where(x => x.Serving && !x.Spare && !_state.SpareNodes.Contains(x.Node) && !_state.Scale.Retiring.Contains(x.Node)
            && NodeLeaseLive(x, now)).OrderBy(x => x.Activated ?? x.Registered).ThenBy(x => x.Node, StringComparer.Ordinal)];

    /// <summary>
    /// SE-1241: when each serving regular node is expected to be recycled, for the operator view. The same rule as
    /// <see cref="ManagedRotationStep"/>: oldest first, each once it has served one spacing (lifetime / n) and one
    /// spacing after the rotation before it. An estimate: a wait (a recovery, no live spare) pushes them all back.
    /// Empty when the deployment is not managed or rotation is off.
    /// </summary>
    private Dictionary<string, DateTimeOffset> RotationSchedule(DateTimeOffset now)
    {
        Dictionary<string, DateTimeOffset> due = new(StringComparer.Ordinal);
        double lifetime = RotationSeconds(RotationLifetimeKey, 7200);
        if (_state.ManagedDeployment is null || lifetime <= 0) return due;
        NodeRegistration[] serving = RotationOrder(_state.Nodes.Values.Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active), now);
        if (serving.Length == 0) return due;
        TimeSpan spacing = TimeSpan.FromSeconds(lifetime / serving.Length);
        DateTimeOffset next = _state.LastManagedRotationAt is { } last ? last + spacing : now;
        foreach (NodeRegistration node in serving)
        {
            DateTimeOffset at = new[] { next, (node.Activated ?? node.Registered) + spacing, now }.Max();
            due[node.Node] = at;
            next = at + spacing;
        }
        return due;
    }

    private bool AdvanceManagedRecycle(ManagedRecycle recycle, DateTimeOffset now)
    {
        NodeRegistration? row = _state.Nodes.GetValueOrDefault(recycle.Node);
        if (row == null || row.Epoch > recycle.Epoch)
        {
            _state.ManagedRotation = null;
            Console.WriteLine($"Rotation: {recycle.Node} recycled in {Duration(now - recycle.StartedAt)}"
                + (row == null ? "" : $"; epoch {row.Epoch} registered as {(row.Spare ? "the hot spare" : "a serving node")}"));
            return true;
        }
        bool changed = false;
        if (row.State is NodeState.Empty or NodeState.Dead && _state.SpareNodes.Add(recycle.Node))
        {
            row.Spare = true;
            row.Activated = null;
            Emit("node.spare", recycle.Node, new Dictionary<string, string> { ["spare"] = true.ToString() });
            Console.WriteLine($"Rotation: {recycle.Node} epoch={recycle.Epoch} is {row.State}; its slot restarts as the hot spare");
            changed = true;
        }
        if (!recycle.Forced && row.State == NodeState.Active
            && now - recycle.StartedAt >= TimeSpan.FromSeconds(RotationSeconds(RotationDrainTimeoutKey, 600)))
        {
            recycle.Forced = true;
            row.State = NodeState.Closed;
            row.ForceDrainAt ??= now;
            Emit("node.closing", row.Node, new Dictionary<string, string> { ["epoch"] = row.Epoch.ToString(CultureInfo.InvariantCulture) });
            Console.WriteLine($"Rotation: {recycle.Node} still owns {OwnedPartitions(recycle.Node)} partition(s) after "
                + $"{Duration(now - recycle.StartedAt)}; closed, its own drain hands out the rest");
            changed = true;
        }
        return changed;
    }

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h{span.Minutes:00}m" : $"{(int)span.TotalMinutes}m{span.Seconds:00}s";
}
