using System.Net;

namespace ClusterRegistry;

/// <summary>
/// T-0312 online node scaling, registry half: the desired number of partition-hosting (regular, non-spare)
/// nodes, persisted so a registry restart keeps it, and the set of nodes being retired by a scale-in.
///
/// The registry does not start or stop processes. A launcher (Cli/cluster.py <c>Cluster.scale_step</c>, run by
/// <c>cluster scale</c> or the harness rotator) starts nodes while fewer regulars serve than the target and
/// stops the nodes this file reports retired. Scale-in is the registry's job: <see cref="DirectGateway"/> picks
/// the nodes to retire, drains them through the pinned-migration path, closes each once it owns nothing, and
/// records it retired once it is Empty (or Dead). See Docs/Gateway/NodeScaling.md.
/// </summary>
internal sealed class ScaleState
{
    /// <summary>Desired regular node count; null = never set (the node set is whatever the launcher started).</summary>
    public int? Target { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? RequestedAt { get; set; }
    /// <summary>Scale-in past the capacity check was requested explicitly.</summary>
    public bool Force { get; set; }
    /// <summary>Nodes draining for a scale-in: no new content, their partitions leave, then close.</summary>
    public List<string> Retiring { get; set; } = [];
    /// <summary>Nodes a scale-in finished with (Empty or Dead): the launcher stops and forgets them.</summary>
    public List<string> Retired { get; set; } = [];
    /// <summary>T-0312 only (unused since T-0320: node control names the nodes to stop).</summary>
    public List<string> PreferRetire { get; set; } = [];
    /// <summary>When the node count last reached the target, for the "settling" phase.</summary>
    public DateTimeOffset? ReachedAt { get; set; }
}

internal sealed record ScaleStatus(int? Target, long Revision, DateTimeOffset? RequestedAt, bool Force, string Phase,
    string[] Serving, string[] Starting, string[] Retiring, string[] Retired, string[] Spares, string? Blocked,
    DateTimeOffset? ReachedAt);

internal sealed partial class GatewayRegistry
{
    /// <summary>Most nodes a target may ask for; ports and slots are per node, so this is a sanity bound.</summary>
    internal const int MaxScaleTarget = 64;

    /// <summary>
    /// Sets the desired regular node count. A raised target first takes back retiring nodes that have not
    /// closed yet (newest request wins, no drain is finished for nothing); a lowered one is carried out by
    /// the automatic loop. Returns the new revision.
    /// </summary>
    public long SetScaleTarget(int target, bool force, IReadOnlyCollection<string>? retire = null)
    {
        if (target < 1 || target > MaxScaleTarget)
            throw new ArgumentException($"Scale target must be 1..{MaxScaleTarget}, got {target}");
        lock (_lock)
        {
            ScaleState scale = _state.Scale;
            int serving = ServingRegulars().Count(node => !scale.Retiring.Contains(node));
            // Supersede: keep as many retiring nodes as the new target still needs removed, cancel the rest,
            // newest first (the least-drained). A Closed node has handed its content away already; keep it.
            int excess = serving + scale.Retiring.Count - target;
            foreach (string node in scale.Retiring.ToArray().Reverse())
            {
                if (excess >= scale.Retiring.Count) break;
                if (_state.Nodes.TryGetValue(node, out NodeRegistration? row) && row.State != NodeState.Active) continue;
                scale.Retiring.Remove(node);
                Console.WriteLine($"Scale: node {node} is no longer retiring (target raised to {target})");
            }
            scale.Target = target;
            scale.Force = force;
            scale.PreferRetire = [.. retire ?? []];
            scale.Revision++;
            scale.RequestedAt = _clock();
            scale.ReachedAt = null;
            Emit("scale.target", null, new Dictionary<string, string> { ["target"] = target.ToString(), ["force"] = force.ToString() });
            Console.WriteLine($"Scale: target {target} regular node(s) (revision {scale.Revision}{(force ? ", forced" : "")}); serving {serving}, retiring {scale.Retiring.Count}");
            Persist();
            return scale.Revision;
        }
    }

    /// <summary>Regular, Active, serving, non-spare nodes. Caller holds _lock.</summary>
    private IEnumerable<string> ServingRegulars() => _state.Nodes.Values
        .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && x.Serving && !x.Spare && !_state.SpareNodes.Contains(x.Node))
        .Select(x => x.Node);

    public bool IsRetiring(string node)
    {
        lock (_lock) return _state.Scale.Retiring.Contains(node);
    }

    public string[] RetiringNodes()
    {
        lock (_lock) return [.. _state.Scale.Retiring];
    }

    public int? ScaleTarget
    {
        get { lock (_lock) return _state.Scale.Target; }
    }

    public bool ScaleForced
    {
        get { lock (_lock) return _state.Scale.Force; }
    }

    public string[] PreferRetire()
    {
        lock (_lock) return [.. _state.Scale.PreferRetire];
    }

    /// <summary>Marks nodes retiring (scale-in). Only serving regulars qualify.</summary>
    public void BeginRetiring(IEnumerable<string> nodes, string why)
    {
        lock (_lock)
        {
            HashSet<string> serving = [.. ServingRegulars()];
            bool changed = false;
            foreach (string node in nodes)
            {
                if (!serving.Contains(node) || _state.Scale.Retiring.Contains(node)) continue;
                _state.Scale.Retiring.Add(node);
                _state.Scale.Retired.Remove(node);
                changed = true;
                Emit("scale.retiring", node, new Dictionary<string, string> { ["why"] = why });
                Console.WriteLine($"Scale: retiring node {node}: {why}");
            }
            if (changed) Persist();
        }
    }

    /// <summary>
    /// Moves retiring nodes that finished (Empty, Dead, or gone from the registry) to Retired, and returns
    /// the retiring nodes that own nothing any more but are still Active, for the caller to close.
    /// </summary>
    public string[] SettleRetiring(IReadOnlySet<string> owningSomething)
    {
        lock (_lock)
        {
            List<string> toClose = [];
            bool changed = false;
            foreach (string node in _state.Scale.Retiring.ToArray())
            {
                NodeRegistration? row = _state.Nodes.GetValueOrDefault(node);
                if (row == null || row.State is NodeState.Empty or NodeState.Dead)
                {
                    _state.Scale.Retiring.Remove(node);
                    if (!_state.Scale.Retired.Contains(node)) _state.Scale.Retired.Add(node);
                    changed = true;
                    Emit("scale.retired", node, new Dictionary<string, string> { ["state"] = row?.State.ToString() ?? "gone" });
                    Console.WriteLine($"Scale: node {node} retired ({row?.State.ToString() ?? "gone"}); the launcher stops it");
                }
                // The node's REPORT decides. A registry row the report does not list is memberless (its content
                // left, r34 2026-09-30 node-4 / 286401) and only the close releases it - through the node's own
                // drain, which releases memberless partitions. Waiting for the row blocked the scale-in forever.
                // A Recovering row is a load in flight whose entities are not reported yet: that one waits.
                else if (row.State == NodeState.Active && !owningSomething.Contains(node)
                    && !_state.Partitions.Values.Any(p => p.Node == node && p.Recovering))
                    toClose.Add(node);
            }
            if (changed) Persist();
            return [.. toClose];
        }
    }

    /// <summary>A retired node that registers again is a legitimate launch (for example a cold start with an
    /// older node set): it is no longer retired. Caller holds _lock.</summary>
    private void ForgetRetiredOnRegistration(string node)
    {
        if (_state.Scale.Retired.Remove(node))
            Console.WriteLine($"Scale: retired node {node} registered again; it counts as a regular node");
        _state.Scale.Retiring.Remove(node);
    }

    public ScaleStatus Scale(bool balancerIdle)
    {
        lock (_lock)
        {
            ScaleState scale = _state.Scale;
            DateTimeOffset now = _clock();
            string[] serving = [.. ServingRegulars().Where(node => !scale.Retiring.Contains(node)).Order(StringComparer.Ordinal)];
            string[] starting = [.. _state.Nodes.Values
                .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && !x.Serving && !x.Spare
                    && !_state.SpareNodes.Contains(x.Node) && !scale.Retiring.Contains(x.Node) && x.LeaseExpires > now)
                .Select(x => x.Node).Order(StringComparer.Ordinal)];
            string[] spares = [.. _state.SpareNodes.Order(StringComparer.Ordinal)];
            string? blocked = RecoveryPending() ? "partitions are still unowned or recovering" : null;
            string phase;
            if (scale.Target is not int target) phase = "unset";
            else if (serving.Length < target) phase = "scaling-out";
            else if (serving.Length > target || scale.Retiring.Count != 0) phase = "scaling-in";
            else
            {
                if (scale.ReachedAt == null) { scale.ReachedAt = now; Persist(); }
                phase = balancerIdle && now - scale.ReachedAt >= TimeSpan.FromSeconds(60) ? "settled" : "settling";
            }
            return new ScaleStatus(scale.Target, scale.Revision, scale.RequestedAt, scale.Force, phase, serving, starting,
                [.. scale.Retiring], [.. scale.Retired], spares, blocked, scale.ReachedAt);
        }
    }
}

internal sealed partial class DirectGateway
{
    /// <summary>Balance cooldown while a scale step has not settled (instead of <see cref="BalanceCooldownSeconds"/>).</summary>
    private const double ScaleBalanceCooldownSeconds = 15;
    /// <summary>A decrease warns (T-0312 refused; T-0320 obeys) when the remaining nodes would each carry more than this
    /// share of one game thread (sim-us/s over 1e6), measured from the nodes' partition loads.</summary>
    private static readonly double ScaleInMaxThreadShare = EnvDouble("CLUSTER_SCALE_IN_MAX_THREAD_SHARE", 0.7);
    private string? _scaleInWarning;

    /// <summary>T-0320: the projected partition work per remaining serving node when it exceeds the thread-share limit,
    /// else null. Only ever a warning: a decrease is obeyed regardless.</summary>
    internal static string? CapacityWarning(double work, int remaining, bool measured, double limit)
    {
        if (remaining <= 0) return "no serving node would remain to carry the partitions";
        if (!measured) return null;
        double perNode = work / remaining;
        return perNode > limit * 1_000_000
            ? $"projected load {perNode / 1e6:F2} game threads of partition work on each of the {remaining} remaining serving node(s) (limit {limit:F2}); obeyed anyway"
            : null;
    }

    /// <summary>The capacity warning for a serving total, from the last partition reports (null before the first pass).</summary>
    private string? CapacityWarningFor(int servingTotal)
    {
        if (_lastPartitionReports is not { } last || last.Reports.Length == 0) return null;
        return CapacityWarning(last.Reports.Sum(x => (double)x.Report.Partitions.Sum(p => p.SimLoadUs)), servingTotal,
            last.Reports.All(x => x.Report.SimLoadSupported), ScaleInMaxThreadShare);
    }

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private bool ScaleUnsettled()
    {
        if (_registry?.ScaleTarget == null) return false;
        return _registry.Scale(BalancerIdle()).Phase != "settled";
    }

    /// <summary>
    /// No balance pin live, the balancer not paused for a rotation or scale-in, and 60 s without a move since it last moved or resumed.
    /// A paused balancer is not idle: r34 2026-09-30 4->7 read "settled" at 21:45:31 while balancing was paused
    /// for the new nodes' 90 s rotation window, a second before it resumed and started filling them.
    /// </summary>
    private bool BalancerIdle()
    {
        lock (_balanceMigrations)
            if (_balanceMigrations.Values.Any(pin => pin.Expires >= DateTime.UtcNow)) return false;
        // A recovery pause ("unowned or recovering") flickers with every offline wake and is not counted;
        // counting it would hold "settled", and the rotator with it, for as long as players roam.
        if (_balancePauseReason is "node rotation in flight" or "scale-in drain in flight") return false;
        DateTime quietSince = _lastBalanceMove > _balanceResumedAt ? _lastBalanceMove : _balanceResumedAt;
        return DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// T-0312 scale-in, once per automatic pass, before the co-location loop. Picks nodes to retire while more
    /// regulars serve than the target, pins every envelope component still on a retiring node to the
    /// non-retiring node that already holds its neighbours (else the least loaded), so the pinned-migration
    /// path carries it over member by member with the usual one-transfer-per-pass budget, clients last and
    /// every join/settle deferral; then closes a retiring node that owns nothing. Returns the retiring set.
    /// </summary>
    private HashSet<string> ScaleInPass((ServedNode Node, PartitionReport Report)[] reports, IReadOnlyDictionary<string, double> passLoad,
        HashSet<ulong> routed, CostModel gapModel)
    {
        GatewayRegistry registry = _registry!;
        if (registry.RecoveryPending()) return [.. registry.RetiringNodes()];
        HashSet<string> retiring = [.. registry.RetiringNodes()];
        HashSet<string> owning = [.. reports.Where(x => x.Report.Partitions.Length != 0).Select(x => x.Node.Node)];
        foreach (string node in registry.SettleRetiring(owning))
        {
            try
            {
                NodeRegistration row = registry.Nodes().First(x => x.Node == node);
                registry.CloseNode(node, row.Epoch, TimeSpan.FromSeconds(60));
                Console.WriteLine($"Scale: retiring node {node} owns nothing; closed");
            }
            catch (Exception exception) { Console.Error.WriteLine($"Scale: closing retiring node {node} failed: {exception.Message}"); }
        }
        retiring = [.. registry.RetiringNodes()];
        // T-0320: WHICH nodes retire is node control's decision (NodeControl.cs: the oldest serving node of the host
        // over its target, or the node named by remove/recycle), made when the target changes. This pass only drains.
        // Capacity is a WARNING, never a refusal (spec: "obeyed with a warning showing the projected load"): the
        // MEASURED partition work (sim-us/s of game thread) spread over the nodes that stay. Not passLoad: the
        // effective load adds the time a slow node is missing (EffectiveNodeLoad), which reads as several threads on
        // a node still loading.
        string? warning = retiring.Count == 0 ? null : CapacityWarning(reports.Sum(x => (double)x.Report.Partitions.Sum(p => p.SimLoadUs)),
            reports.Count(x => !retiring.Contains(x.Node.Node)), reports.All(x => x.Report.SimLoadSupported), ScaleInMaxThreadShare);
        if (warning != _scaleInWarning && warning != null) Console.WriteLine("Scale: " + warning);
        _scaleInWarning = warning;
        if (retiring.Count == 0) return retiring;
        (ServedNode Node, PartitionReport Report)[] receivers = [.. reports.Where(x => !retiring.Contains(x.Node.Node))];
        if (receivers.Length == 0) return retiring;
        Dictionary<string, double> projected = receivers.ToDictionary(x => x.Node.Node, x => passLoad.GetValueOrDefault(x.Node.Node));
        // #1454 case 6: a heavy component drains once, to the emptiest node without one, and the light ones keep off it.
        IReadOnlySet<ulong> heavy = _heavy;
        HashSet<string> heavyNodes = [.. _heavyNodes];
        // SE-1415: the clients each receiver serves, plus the components already given to it this pass, and the node the
        // rotation recycles next (a crew sent there would move again within one rotation spacing).
        Dictionary<string, int> projectedClients = receivers.ToDictionary(x => x.Node.Node, x => x.Report.Partitions.Sum(p => ClientCount(p, routed)));
        string? nextToRotate = registry.NextToRotate();
        foreach ((ServedNode node, PartitionReport report) in reports.Where(x => retiring.Contains(x.Node.Node)))
        {
            if (report.Partitions.Length == 0) continue;
            // T-0320: a recycled node drains whole into the spare activated in its place, like the rotator's only=spare.
            string? preferred = registry.PreferredDrainReceiver(node.Node);
            double envelope = report.SyncDistance + report.Margin;
            (PartitionInfo Partition, bool OnSource)[] all = [.. report.Partitions.Select(p => (p, true))];
            int[] labels = Components(all, envelope, gapModel);
            // Heavy components first, so the light ones know which receivers took one; then the biggest crews, so the
            // client spread below places them first.
            foreach (IGrouping<int, int> group in Enumerable.Range(0, all.Length).GroupBy(i => labels[i])
                         .OrderByDescending(g => g.Any(i => heavy.Contains(all[i].Partition.Id)))
                         .ThenByDescending(g => g.Sum(i => ClientCount(all[i].Partition, routed))))
            {
                PartitionInfo[] members = [.. group.Select(i => all[i].Partition)];
                bool isHeavy = members.Any(m => heavy.Contains(m.Id));
                int clients = members.Sum(m => ClientCount(m, routed));
                string? pinned = members.Select(m => MigrationTarget(m.Id)).FirstOrDefault(t => t != null && projected.ContainsKey(t));
                string target = pinned
                    ?? (isHeavy ? HeavyDrainTarget(projected.Select(x => (x.Key, x.Value)), heavyNodes, preferred) : null)
                    ?? DrainTarget(members, [.. receivers.Select(x => (x.Node.Node, x.Report))], envelope, projected, preferred, heavyNodes,
                        clients, projectedClients, nextToRotate);
                if (pinned == null || members.Any(m => MigrationTarget(m.Id) != target))
                    PinMigration(members, target);
                if (isHeavy) heavyNodes.Add(target);
                projectedClients[target] += clients;
                if (pinned == null)
                {
                    long load = members.Sum(m => PartitionLoad(m, true));
                    projected[target] += load;
                    if (isHeavy) HeavyMoved(members.Select(m => m.Id), members.Sum(m => ClientCount(m, routed)));
                    Console.WriteLine($"Scale: draining component [{string.Join(" ", members.Take(8).Select(m => m.Id))}{(members.Length > 8 ? " ..." : "")}]"
                        + $" ({members.Length} partition(s), {load} sim-us/s, clients {members.Sum(m => ClientCount(m, routed))}{(isHeavy ? ", heavy" : "")}) {node.Node} -> {target}");
                }
            }
        }
        return retiring;
    }

    /// <summary>The preferred receiver when it is one (a recycled node's replacement); else the receiver already holding an
    /// envelope neighbour of the component, else the least projected load. #1454: a receiver in <paramref name="heavyNodes"/>
    /// (it holds or takes a heavy component) is the target only for an envelope neighbour or when nothing else receives.
    /// SE-1415: a component carrying <paramref name="clients"/> out of a recycled node (one with a preferred receiver) goes to
    /// an envelope neighbour if there is one, else to the open receiver with the fewest <paramref name="projectedClients"/>
    /// (ties: the preferred one first, then the least load), never to <paramref name="avoid"/> while another is open. Sending
    /// every component to the replacement moved each node's crowd whole onto the spare, so the players stayed on the same
    /// few nodes for good: the paced balancer holds below its load floor and never spreads them.</summary>
    internal static string DrainTarget(PartitionInfo[] members, (string Node, PartitionReport Report)[] receivers, double envelope,
        IReadOnlyDictionary<string, double> projected, string? preferred = null, IReadOnlySet<string>? heavyNodes = null,
        int clients = 0, IReadOnlyDictionary<string, int>? projectedClients = null, string? avoid = null)
    {
        bool Open(string node) => heavyNodes?.Contains(node) != true;
        bool spread = preferred != null && clients > 0 && projectedClients != null;
        if (!spread && preferred != null && receivers.Any(x => x.Node == preferred) && Open(preferred)) return preferred;
        foreach ((string node, PartitionReport report) in receivers.OrderBy(x => projected[x.Node]).ThenBy(x => x.Node, StringComparer.Ordinal))
            if (report.Partitions.Any(p => members.Any(m => SphereGap(m.Sphere, p.Sphere) < envelope)))
                return node;
        if (spread)
        {
            string[] open = [.. receivers.Select(x => x.Node).Where(Open)];
            string[] candidates = open.Length > 1 ? [.. open.Where(x => x != avoid)] : open;
            if (candidates.Length != 0)
                return candidates.OrderBy(x => projectedClients!.GetValueOrDefault(x)).ThenBy(x => x == preferred ? 0 : 1)
                    .ThenBy(x => projected[x]).ThenBy(x => x, StringComparer.Ordinal).First();
        }
        return receivers.OrderBy(x => Open(x.Node) ? 0 : 1).ThenBy(x => projected[x.Node]).ThenBy(x => x.Node, StringComparer.Ordinal).First().Node;
    }
}

internal sealed partial class DirectGateway
{
    /// <summary>GET/POST /registry/scale body: the registry's scale status plus the balancer's view.</summary>
    private object ScaleView()
    {
        ScaleStatus status = _registry!.Scale(BalancerIdle());
        BalanceDiagnostics? balance = _balanceDiagnostics;
        return new
        {
            target = status.Target,
            revision = status.Revision,
            requestedAt = status.RequestedAt,
            force = status.Force,
            phase = status.Phase,
            serving = status.Serving,
            starting = status.Starting,
            retiring = status.Retiring,
            retired = status.Retired,
            spares = status.Spares,
            blocked = status.Blocked,
            warning = _scaleInWarning,
            hosts = _registry.NodeControlView(null, null, "", 0).Hosts.Select(h => new
            {
                host = h.Host,
                serving = h.Serving.Target,
                spares = h.Spares.Target,
                servingActual = h.Serving.Actual,
                sparesActual = h.Spares.Actual,
            }).ToArray(),
            reachedAt = status.ReachedAt,
            balance = balance == null ? null : new
            {
                evaluatedUtc = balance.EvaluatedUtc,
                excess = balance.Excess,
                achievableExcess = balance.AchievableExcess,
                worstDeviation = balance.WorstDeviation,
                pauseReason = balance.PauseReason,
            },
        };
    }
}
