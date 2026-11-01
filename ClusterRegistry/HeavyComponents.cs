namespace ClusterRegistry;

/// <summary>
/// #1454 (Viktor 2026-10-23): heavy components, Docs/Gateway/GlobalLoadBalancing.md "Heavy components". A balance unit carrying at least
/// <see cref="CostModel.HeavyClients"/> clients is heavy: moving it costs every one of its pilots a migration stall and
/// relieves nothing, because its cost goes with it (#1191 run 2: the Alpha hub, 58-74 clients, was moved 17 times and
/// its node ran at sim 0.90 wherever it went). The balancer pins it on its node, sends nothing else there, and moves the
/// node's other components away instead. Only drains, failures and scale-in move it, once, to the emptiest node.
/// </summary>
internal sealed partial class DirectGateway
{
    // The member ids of the heavy components on the last pass; read by the pair loop, the planner, the drains and
    // (through _heavyNodes) the registry's free offline target. Replaced whole, never mutated.
    private volatile IReadOnlySet<ulong> _heavy = new HashSet<ulong>();
    private volatile string[] _heavyNodes = [];
    // When each partition last moved as (or in) a heavy component, and the clients it carried then: the cool-down.
    private readonly Dictionary<ulong, (DateTime At, int Clients)> _heavyMovedAt = new();
    // The heavy components last logged, keyed by their smallest member id: one line per pin and per release.
    private volatile Dictionary<ulong, (string Node, int Clients)> _heavyLogged = new();

    /// <summary>
    /// The member ids of every heavy balance unit in <paramref name="nodes"/>: the planner's units (Components with
    /// units: true), heavy by <see cref="CostModel.IsHeavy"/> with the hysteresis keyed on <paramref name="latched"/> (a
    /// unit holding a member that was heavy last pass stays heavy down to HeavyReleaseClients). Empty while HeavyClients
    /// is 0. Splits keep an input id and merges keep one of theirs, so the latch follows the unit through both.
    /// </summary>
    internal static HashSet<ulong> HeavyMembers((PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong> routed,
        IReadOnlySet<ulong> latched, Action<string, PartitionInfo[], int>? found = null)
    {
        HashSet<ulong> heavy = [];
        if (model.HeavyClients <= 0) return heavy;
        foreach ((PartitionReport report, string node) in nodes)
        {
            (PartitionInfo Partition, bool OnSource)[] all = [.. report.Partitions.Select(p => (p, true))];
            int[] labels = Components(all, report.SyncDistance + report.Margin, model, units: true);
            foreach (IGrouping<int, int> group in Enumerable.Range(0, all.Length).GroupBy(i => labels[i]))
            {
                PartitionInfo[] members = [.. group.Select(i => all[i].Partition)];
                int clients = members.Sum(m => ClientCount(m, routed));
                if (!model.IsHeavy(clients, members.Any(m => latched.Contains(m.Id)))) continue;
                heavy.UnionWith(members.Select(m => m.Id));
                found?.Invoke(node, members, clients);
            }
        }
        return heavy;
    }

    /// <summary>
    /// Once per automatic pass: the heavy set for this pass, and a log line whenever a heavy component appears, changes
    /// node or is released. A pass missing a node's report keeps that node's heavy members (absence is not release).
    /// </summary>
    private IReadOnlySet<ulong> UpdateHeavy((ServedNode Node, PartitionReport Report)[] reports, HashSet<ulong> routed, CostModel model,
        bool partial)
    {
        IReadOnlySet<ulong> previous = _heavy;
        Dictionary<ulong, (string Node, int Clients)> logged = [];
        HashSet<ulong> heavy = HeavyMembers([.. reports.Select(x => (x.Report, x.Node.Node))], model, routed, previous,
            (node, members, clients) =>
            {
                ulong key = members.Min(m => m.Id);
                logged[key] = (node, clients);
                if (_heavyLogged.TryGetValue(key, out var was) && was.Node == node) return;
                Console.WriteLine($"Heavy component pinned on {node}: {clients} client(s), {members.Length} partition(s)"
                    + $" [{string.Join(" ", members.Take(8).Select(m => m.Id))}{(members.Length > 8 ? " ..." : "")}]"
                    + (was.Node != null ? $", was on {was.Node}" : "") + $"; heavy at {model.HeavyClients} clients, released below"
                    + $" {(model.HeavyReleaseClients > 0 ? Math.Min(model.HeavyReleaseClients, model.HeavyClients) : model.HeavyClients)}");
            });
        if (partial)
        {
            HashSet<ulong> reported = [.. reports.SelectMany(x => x.Report.Partitions).Select(p => p.Id)];
            heavy.UnionWith(previous.Where(id => !reported.Contains(id)));
            foreach (var entry in _heavyLogged) if (!reported.Contains(entry.Key)) logged.TryAdd(entry.Key, entry.Value);
        }
        foreach (var (key, was) in _heavyLogged)
            if (!logged.ContainsKey(key) && !heavy.Contains(key))
                Console.WriteLine($"Heavy component released on {was.Node}: partition {key} is below the release threshold;"
                    + " normal balancing resumes for it");
        _heavyLogged = logged;
        _heavy = heavy;
        _heavyNodes = [.. logged.Values.Select(x => x.Node).Distinct()];
        lock (_heavyMovedAt)
            foreach (ulong id in _heavyMovedAt.Where(x => (DateTime.UtcNow - x.Value.At).TotalSeconds
                         > x.Value.Clients * model.HeavyCooldownSecondsPerClient).Select(x => x.Key).ToArray())
                _heavyMovedAt.Remove(id);
        return heavy;
    }

    /// <summary>A heavy component moved (a heavy plan or a drain): its members cool down for clients x HeavyCooldownSecondsPerClient.</summary>
    private void HeavyMoved(IEnumerable<ulong> members, int clients)
    {
        lock (_heavyMovedAt)
            foreach (ulong id in members) _heavyMovedAt[id] = (DateTime.UtcNow, clients);
    }

    private bool HeavyCooling(ulong partition)
    {
        lock (_heavyMovedAt)
            return _heavyMovedAt.TryGetValue(partition, out var moved)
                && (DateTime.UtcNow - moved.At).TotalSeconds < moved.Clients * _costModel.HeavyCooldownSecondsPerClient;
    }

    /// <summary>
    /// The heavy moves of one paced pass, planned before every gate of <see cref="ChooseRebalancePlans"/> (a heavy node's
    /// neighbours leave before it saturates, case 4). Per node holding a heavy component, at most one move, node-disjoint
    /// with <paramref name="busyNodes"/> and with each other like every paced plan:
    /// <list type="number">
    /// <item>two or more heavy components (case 2): the one with the fewest clients that is not cooling down moves to
    /// an empty node, else to the least loaded node without a heavy component;</item>
    /// <item>else, when the node's other components carry more clients than its heavy component and an empty node is
    /// free (case 8), the heavy component moves there instead: fewer clients pay a migration;</item>
    /// <item>else (cases 1 and 4) its biggest other component moves to the least loaded node without a heavy component.</item>
    /// </list>
    /// A node without a free target keeps everything. <paramref name="used"/> returns the nodes the moves engage.
    /// </summary>
    internal static List<RebalancePlan> PlanHeavyMoves(IReadOnlyList<PlacementComponent> components,
        (PartitionReport Report, string Node)[] nodes, long[] load, IReadOnlySet<string>? busyNodes, Func<ulong, bool>? cooling,
        string loadUnit, double excess, string explain, out HashSet<string> used)
    {
        used = busyNodes != null ? [.. busyNodes] : [];
        List<RebalancePlan> plans = [];
        bool[] heavyNode = new bool[nodes.Length];
        int[] contentCount = new int[nodes.Length];
        foreach (PlacementComponent c in components)
        {
            if (c.Heavy) heavyNode[c.Node] = true;
            if (c.Members.Length != 0 && (c.Content || c.Heavy)) contentCount[c.Node]++;
        }
        if (!heavyNode.Any(x => x)) return plans;
        long[] projected = (long[])load.Clone();
        double meanLoad = load.Sum() / (double)nodes.Length;
        HashSet<string> engaged = used;
        int? Target(bool emptyFirst)
        {
            int? best = null;
            for (int n = 0; n < nodes.Length; n++)
            {
                if (heavyNode[n] || engaged.Contains(nodes[n].Node)) continue;
                if (best is not int b) { best = n; continue; }
                (bool, long, string) mine = (emptyFirst && contentCount[n] != 0, projected[n], nodes[n].Node);
                (bool, long, string) theirs = (emptyFirst && contentCount[b] != 0, projected[b], nodes[b].Node);
                if (mine.CompareTo(theirs) < 0) best = n;
            }
            return best;
        }
        void Add(PlacementComponent c, int target, RebalanceKind kind)
        {
            long before = projected.Max();
            projected[c.Node] -= c.Load;
            projected[target] += c.Load;
            plans.Add(new RebalancePlan(c.Members, nodes[c.Node].Node, nodes[target].Node, c.Load, before, projected.Max(),
                c.CostMs, excess, loadUnit, explain, kind, 0, meanLoad));
            engaged.Add(nodes[c.Node].Node);
            engaged.Add(nodes[target].Node);
            if (c.Heavy) heavyNode[target] = true;
            contentCount[target]++;
        }
        bool Cooling(PlacementComponent c) => cooling != null && c.Members.Any(m => cooling(m.Id));
        foreach (int n in Enumerable.Range(0, nodes.Length).Where(n => heavyNode[n])
                     .OrderByDescending(n => components.Where(c => c.Node == n && c.Heavy).Sum(c => c.Clients)).ToArray())
        {
            if (engaged.Contains(nodes[n].Node)) continue;
            PlacementComponent[] heavies = [.. components.Where(c => c.Node == n && c.Heavy).OrderByDescending(c => c.Clients)];
            if (heavies.Length >= 2)
            {
                PlacementComponent[] movers = [.. heavies.Where(c => c.Free && !Cooling(c)).OrderBy(c => c.Clients).ThenBy(c => c.CostMs)];
                if (movers.Length != 0 && Target(emptyFirst: true) is int split)
                {
                    Add(movers[0], split, RebalanceKind.HeavySplit);
                    continue;
                }
            }
            PlacementComponent[] others = [.. components.Where(c => c.Node == n && !c.Heavy && c.Members.Length != 0 && c.Content)];
            if (others.Length == 0) continue;
            if (heavies.Length == 1 && others.Sum(c => c.Clients) > heavies[0].Clients && heavies[0].Free && !Cooling(heavies[0])
                && Target(emptyFirst: true) is int empty && contentCount[empty] == 0)
            {
                Add(heavies[0], empty, RebalanceKind.HeavyDedicate);
                continue;
            }
            PlacementComponent[] evacuable = [.. others.Where(c => c.Free).OrderByDescending(c => c.Load).ThenBy(c => c.Clients).ThenBy(c => c.CostMs)];
            if (evacuable.Length != 0 && Target(emptyFirst: false) is int away)
                Add(evacuable[0], away, RebalanceKind.Evacuate);
        }
        return plans;
    }

    /// <summary>
    /// The co-location home when a heavy component is involved (case 5), or null for the §7.5 price to decide: a heavy
    /// side stays and the other side comes to it; when the merged component would be heavy (both sides, or neither side
    /// alone), the side with more clients stays, and on equal clients the lighter node. Fewer clients pay the switch;
    /// the light components on the home node are then evacuated (PlanHeavyMoves), which is the cheap side of the
    /// cost model. A teleport keeps its own rule.
    /// </summary>
    internal static bool? HeavyHome((PartitionInfo Partition, bool OnSource)[] members, HashSet<ulong> routed, CostModel model,
        IReadOnlySet<ulong>? heavy, HashSet<ulong>? travelling, double loadSource, double loadTarget, out string why)
    {
        why = "";
        if (model.HeavyClients <= 0) return null;
        if (travelling is { Count: > 0 } && members.Any(m => m.Partition.Clients.Any(travelling.Contains))) return null;
        bool heavySource = heavy != null && members.Any(m => m.OnSource && heavy.Contains(m.Partition.Id));
        bool heavyTarget = heavy != null && members.Any(m => !m.OnSource && heavy.Contains(m.Partition.Id));
        int clientsSource = members.Where(m => m.OnSource).Sum(m => ClientCount(m.Partition, routed));
        int clientsTarget = members.Where(m => !m.OnSource).Sum(m => ClientCount(m.Partition, routed));
        if (heavySource != heavyTarget)
        {
            why = $"heavy component on the {(heavyTarget ? "target" : "source")} ({(heavyTarget ? clientsTarget : clientsSource)} clients) stays";
            return heavyTarget;
        }
        if (!model.IsHeavy(clientsSource + clientsTarget, heavySource)) return null;
        bool home = clientsSource != clientsTarget ? clientsTarget > clientsSource : loadTarget < loadSource;
        why = $"merged component heavy ({clientsSource}+{clientsTarget} clients): the side with "
            + (clientsSource != clientsTarget ? "more clients" : "the lighter node") + " stays";
        return home;
    }

    /// <summary>
    /// The drain target of a heavy component or partition (case 6): once, to the emptiest node. The preferred receiver
    /// (a recycled node's replacement, which starts empty) when it holds no heavy component, else the least loaded
    /// candidate without one; null when every candidate holds one (the caller's usual rule then applies).
    /// </summary>
    internal static string? HeavyDrainTarget(IEnumerable<(string Node, double Load)> candidates, IReadOnlySet<string> heavyNodes,
        string? preferred = null)
    {
        (string Node, double Load)[] open = [.. candidates.Where(x => !heavyNodes.Contains(x.Node))];
        if (preferred != null && open.Any(x => x.Node == preferred)) return preferred;
        return open.Length == 0 ? null : open.OrderBy(x => x.Load).ThenBy(x => x.Node, StringComparer.Ordinal).First().Node;
    }
}
