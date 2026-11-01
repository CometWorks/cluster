using System.Globalization;
using System.Text.Json;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    /// <summary>
    /// #1304: <c>--balance-replay [--cost-model FILE] [--held ID,ID...] NODE=REPORT.json ...</c> runs the paced planner on
    /// saved <c>/cluster/partitions</c> reports, offline: each node's balance units (what the planner can move), the links
    /// that hold the heaviest one together (raw member gap against the envelope gap, so a lookahead-only link shows) and
    /// the plan it chooses, with the given partitions residence held. Reads files, touches no cluster.
    /// </summary>
    internal static void BalanceReplay(string[] args)
    {
        if (args.Length != 0 && args[0] == "--jsonl")
        {
            ReplayHeavy(args);
            return;
        }
        CostModel model = CostModel.Deployed();
        HashSet<ulong> held = [];
        int passes = 0;
        List<(PartitionReport Report, string Node)> nodes = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cost-model") model = JsonSerializer.Deserialize<CostModel>(File.ReadAllText(args[++i]))!;
            else if (args[i] == "--held") held = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse)];
            else if (args[i] == "--passes") passes = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else
            {
                string[] pair = args[i].Split('=', 2);
                PartitionReport report = JsonSerializer.Deserialize<PartitionReport>(File.ReadAllText(pair[1]))!;
                if (report.Partitions.Length != 0) nodes.Add((report, pair[0]));
            }
        }
        string F(double x) => x.ToString("F0", CultureInfo.InvariantCulture);
        foreach ((PartitionReport report, string node) in nodes)
        {
            (PartitionInfo Partition, bool OnSource)[] all = [.. report.Partitions.Select(p => (p, true))];
            double envelope = report.SyncDistance + report.Margin;
            double engage = envelope * model.ColocationEngageFraction;
            int envelopeComponents = Components(all, envelope, model).Distinct().Count();
            int[] labels = Components(all, envelope, model, units: true);
            var groups = Enumerable.Range(0, all.Length).GroupBy(i => labels[i])
                .Select(g => g.Select(i => all[i].Partition).ToArray())
                .OrderByDescending(g => g.Sum(p => p.SimLoadUs)).ToArray();
            Console.WriteLine($"{node}: sim {report.SimulationRatio:F2}, {all.Length} partitions, load {report.Partitions.Sum(p => p.SimLoadUs)},"
                + $" clients {report.Partitions.Sum(p => p.Clients.Length)}, envelope components {envelopeComponents}, balance units at {F(engage)} m {groups.Length}, sizes [{string.Join(" ", groups.Take(8).Select(g => g.Length))}]");
            foreach (PartitionInfo[] g in groups.Take(3))
                Console.WriteLine($"  unit of {g.Length}: members {g.Sum(p => p.MemberCount)}, load {g.Sum(p => p.SimLoadUs)},"
                    + $" clients {g.Sum(p => p.Clients.Length)}, cost {F(g.Sum(p => model.FreezeMs(p.EstimatedSnapshotBytes, p.MemberCount)))} ms;"
                    + $" heaviest [{string.Join(" ", g.OrderByDescending(p => p.SimLoadUs).Take(6).Select(p => $"{p.Id}(m{p.MemberCount},c{p.Clients.Length},{p.SimLoadUs})"))}]");
            PartitionInfo[] top = groups[0];
            int lookaheadOnly = 0;
            List<double> raws = [];
            for (int i = 0; i < top.Length; i++)
                for (int j = i + 1; j < top.Length; j++)
                {
                    if (EnvelopeGap(top[i], top[j], engage, model) >= engage) continue;
                    double raw = MinGap(top[i], top[j]);
                    raws.Add(raw);
                    if (raw >= engage) lookaheadOnly++;
                }
            raws.Sort();
            Console.WriteLine($"  heaviest unit links: {raws.Count}, raw gap {(raws.Count == 0 ? "-" : F(raws[0]) + ".." + F(raws[^1]))} m,"
                + $" lookahead-only (raw >= {F(engage)} m) {lookaheadOnly}");
        }
        HashSet<ulong> ids = [.. nodes.SelectMany(n => n.Report.Partitions.Select(p => p.Id))];
        // #1454: the heavy units of the snapshot (no latch: a single report has no history).
        IReadOnlySet<ulong> heavy = HeavyMembers([.. nodes], model, [], new HashSet<ulong>());
        RebalancePlan[] plans = ChooseRebalancePlans([.. nodes], model, [], id => held.Contains(id), null, null, false, null, out var diag,
            null, null, heavy, _ => false);
        Console.WriteLine($"plan (held {(held.Count == 0 ? "none" : string.Join(",", held.Where(ids.Contains)))}): {diag.Explain}");
        Console.WriteLine($"  worst deviation {diag.WorstDeviation:F2}, client deviation {diag.WorstClientDeviation:F2},"
            + $" floor-held peak {diag.FloorHeldPeak}, min-gain skipped {diag.MinGainSkipped}, peak-drop skipped {diag.PeakDropSkipped}");
        foreach (RebalancePlan p in plans)
            Console.WriteLine($"  move {p.Kind.ToString().ToLowerInvariant()}: component of {p.Members.Length} {p.Source}->{p.Target}, load {p.ComponentLoad},"
                + $" peak {p.OldPeak}->{p.NewPeak}, estimated lag {F(p.CostMs)} ms, members [{string.Join(" ", p.Members.Take(8).Select(m => m.Id))}]");
        if (plans.Length == 0) Console.WriteLine("  move: nothing");
        if (passes > 0) SimulatePasses([.. nodes], model, held, passes);
    }

    /// <summary>
    /// <c>--passes N</c>: N paced passes one balance cooldown (60 s) apart. Each applies its plans at once (no migration
    /// time), holds the moved partitions for the residence window (10 passes) and releases the given held set after it,
    /// and keeps each node's unattributed load (effective minus measured) fixed by re-deriving its simulation ratio. A
    /// rough trajectory, not a forecast: no new arrivals, no merges, no flying.
    /// </summary>
    private static void SimulatePasses((PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong> held, int passes)
    {
        bool simLoad = nodes.All(n => n.Report.SimLoadSupported);
        long[] overhead = [.. nodes.Select(n => EffectiveNodeLoad(n.Report, simLoad) - n.Report.Partitions.Sum(p => PartitionLoad(p, simLoad)))];
        Dictionary<ulong, int> heldUntil = held.ToDictionary(id => id, _ => 10);
        string Loads() => string.Join(" ", nodes.Select((n, i) =>
            $"{n.Node}={EffectiveNodeLoad(n.Report, simLoad) / 1000}k/c{n.Report.Partitions.Sum(p => p.Clients.Length)}"));
        Console.WriteLine($"pass 0: {Loads()}");
        IReadOnlySet<ulong> latched = new HashSet<ulong>();
        for (int pass = 1; pass <= passes; pass++)
        {
            latched = HeavyMembers(nodes, model, [], latched);
            RebalancePlan[] plans = ChooseRebalancePlans(nodes, model, [], id => heldUntil.TryGetValue(id, out int until) && until >= pass,
                null, null, false, null, out _, null, null, latched, _ => false);
            foreach (RebalancePlan plan in plans)
            {
                HashSet<ulong> ids = [.. plan.Members.Select(m => m.Id)];
                int s = Array.FindIndex(nodes, n => n.Node == plan.Source), t = Array.FindIndex(nodes, n => n.Node == plan.Target);
                PartitionInfo[] moving = [.. nodes[s].Report.Partitions.Where(p => ids.Contains(p.Id))];
                nodes[s].Report = nodes[s].Report with { Partitions = [.. nodes[s].Report.Partitions.Where(p => !ids.Contains(p.Id))] };
                nodes[t].Report = nodes[t].Report with { Partitions = [.. nodes[t].Report.Partitions, .. moving] };
                foreach (ulong id in ids) heldUntil[id] = pass + 10;
                Console.WriteLine($"pass {pass}: move {plan.Kind.ToString().ToLowerInvariant()} {plan.Members.Length} partition(s)"
                    + $" {plan.Source}->{plan.Target}, load {plan.ComponentLoad}, clients {moving.Sum(p => p.Clients.Length)}, lag {plan.CostMs:F0} ms");
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                long measured = nodes[i].Report.Partitions.Sum(p => PartitionLoad(p, simLoad));
                nodes[i].Report = nodes[i].Report with { SimulationRatio = overhead[i] > 0 ? 1_000_000.0 / (measured + overhead[i]) : 1 };
            }
            if (plans.Length != 0 || pass == passes) Console.WriteLine($"pass {pass}: {Loads()}");
        }
    }

    /// <summary>
    /// #1454: <c>--balance-replay --jsonl FILE [--from UTC] [--to UTC] [--cost-model FILE]</c> replays a run's
    /// partitions.jsonl (one row of node reports per sample, the cluster-regression recorder's format) through the paced
    /// planner twice per sample: the model as given (deployed: heavy components on) and the same model with HeavyClients 0
    /// (the control), the heavy latch carried from sample to sample. Per sample: the heavy units and both plans; at the end
    /// how often each planner moved a heavy unit and how many clients its moves carried. Residence and the balance
    /// cooldown are not modelled: it shows each sample's decision, not the run's sequence. Reads files, touches no cluster.
    /// </summary>
    private static void ReplayHeavy(string[] args)
    {
        string file = args[1];
        CostModel model = CostModel.Deployed();
        DateTime from = DateTime.MinValue, to = DateTime.MaxValue;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--from") from = DateTime.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            else if (args[i] == "--to") to = DateTime.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            else if (args[i] == "--cost-model") model = JsonSerializer.Deserialize<CostModel>(File.ReadAllText(args[++i]))!;
        }
        CostModel control = model with { HeavyClients = 0 };
        IReadOnlySet<ulong> latched = new HashSet<ulong>();
        int samples = 0, controlHeavyMoves = 0, heavyHeavyMoves = 0, controlClients = 0, heavyClients = 0, evacuations = 0;
        string Moves(RebalancePlan[] plans) => plans.Length == 0 ? "nothing" : string.Join("; ", plans.Select(p =>
            $"{p.Kind.ToString().ToLowerInvariant()} [{string.Join(" ", p.Members.Take(4).Select(m => m.Id))}{(p.Members.Length > 4 ? " ..." : "")}]"
            + $" c{p.Members.Sum(m => m.Clients.Length)} {p.Source}->{p.Target}"));
        foreach (string line in File.ReadLines(file))
        {
            using JsonDocument row = JsonDocument.Parse(line);
            DateTime utc = row.RootElement.GetProperty("utc").GetDateTime().ToUniversalTime();
            if (utc < from || utc > to) continue;
            (PartitionReport Report, string Node)[] nodes = [.. row.RootElement.GetProperty("nodes").EnumerateArray()
                .Where(n => n.TryGetProperty("partitions", out JsonElement parts) && parts.GetArrayLength() != 0)
                .Select(n => (n.Deserialize<PartitionReport>()!, n.GetProperty("node").GetString()!))];
            if (nodes.Length < 2) continue;
            samples++;
            List<(string Node, PartitionInfo[] Members, int Clients)> units = [];
            latched = HeavyMembers(nodes, model, [], latched, (node, members, clients) => units.Add((node, members, clients)));
            RebalancePlan[] before = ChooseRebalancePlans(nodes, control, [], _ => false, null, null, false, null, out _);
            RebalancePlan[] after = ChooseRebalancePlans(nodes, model, [], _ => false, null, null, false, null, out _, null, null, latched, _ => false);
            bool Heavy(RebalancePlan p) => p.Members.Any(m => latched.Contains(m.Id));
            controlHeavyMoves += before.Count(Heavy);
            heavyHeavyMoves += after.Count(Heavy);
            controlClients += before.Sum(p => p.Members.Sum(m => m.Clients.Length));
            heavyClients += after.Sum(p => p.Members.Sum(m => m.Clients.Length));
            evacuations += after.Count(p => p.Kind == RebalanceKind.Evacuate);
            Console.WriteLine($"{utc:HH:mm:ss}Z heavy {(units.Count == 0 ? "none" : string.Join(", ", units.Select(u =>
                $"[{string.Join(" ", u.Members.Take(3).Select(m => m.Id))}] c{u.Clients} on {u.Node} with {nodes.First(n => n.Node == u.Node).Report.Partitions.Length - u.Members.Length} other(s)")))}"
                + $" | control: {Moves(before)} | heavy: {Moves(after)}");
        }
        Console.WriteLine($"samples {samples}: heavy-unit moves control {controlHeavyMoves}, heavy {heavyHeavyMoves}; clients carried by"
            + $" planned moves control {controlClients}, heavy {heavyClients}; evacuations {evacuations}");
    }
}
