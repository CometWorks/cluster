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
        if (args.Length != 0 && args[0] == "--decisions")
        {
            ReplayDecisions(args);
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
                nodes.Add((report, pair[0]));
            }
        }
        string F(double x) => x.ToString("F0", CultureInfo.InvariantCulture);
        foreach ((PartitionReport report, string node) in nodes)
        {
            // SE-1415: an empty node is a receiver like any other; the live planner sees it too.
            if (report.Partitions.Length == 0)
            {
                Console.WriteLine($"{node}: sim {report.SimulationRatio:F2}, empty");
                continue;
            }
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

    /// <summary>
    /// SE-1134: <c>--balance-replay --decisions FILE [--from HH:MM:SS] [--to HH:MM:SS] [--cost-model FILE] [--set FIELD=VALUE ...] [--assume-sustained]</c>
    /// re-decides the pins of a gateway log (the "Automatic balance migration pinned", "Balancing declined" and "Balancing
    /// held by the load floor" lines, in the log's own clock) under the model's SimRatioSustainSeconds,
    /// BalanceMinPeakLoad and ClientMinGainPerLagSecond. A node load that is exactly EffectiveNodeLoad's 1e6 / ratio for a
    /// two-decimal ratio under 0.98 is a sim-ratio reading; any other is the measured sum. Each logged line is one pass
    /// through <see cref="SustainRatios"/>, so a dip counts only if the LOGGED passes show it lasting the window: the log
    /// holds a fraction of the passes, and --assume-sustained instead counts every dip as lasting (fix 2 alone). A dip
    /// that does not count leaves the node at its last logged measured load; a paced pin whose peak then falls under the
    /// floor is held, a client pin whose gain does not pay for its lag is refused; an idle-cluster or heavy pin passes no
    /// gate and is kept. The trajectory is not re-simulated: a pin
    /// that would not have happened still shapes the lines after it. Reads a file, touches no cluster.
    /// </summary>
    private static void ReplayDecisions(string[] args)
    {
        string file = args[1];
        CostModel model = CostModel.Deployed();
        TimeSpan from = TimeSpan.Zero, to = TimeSpan.MaxValue;
        bool assumeSustained = false;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--from") from = TimeSpan.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--to") to = TimeSpan.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--cost-model") model = JsonSerializer.Deserialize<CostModel>(File.ReadAllText(args[++i]))!;
            else if (args[i] == "--assume-sustained") assumeSustained = true;
            else if (args[i] == "--set" && args[++i].Split('=', 2) is [var field, var value])
                model = CostModel.WithOverrides(model, [KeyValuePair.Create(field, value)]);
        }
        // Every load a two-decimal ratio under 0.98 turns into, by EffectiveNodeLoad's own arithmetic.
        Dictionary<long, double> reciprocal = [];
        for (int hundredths = 1; hundredths < 98; hundredths++)
        {
            double ratio = hundredths / 100.0;
            reciprocal.TryAdd(EffectiveNodeLoad(new PartitionReport(0, 0, [new(0, 1, true, new(0, 0, 0, 1), 0, [], SimLoadUs: 1)], 0, 0,
                SimulationRatio: ratio, SimLoadSupported: true), true), ratio);
        }
        var line = new System.Text.RegularExpressions.Regex(@"^\[gateway [^\]]*\] (\d\d:\d\d:\d\d) (Automatic balance migration pinned|Balancing declined|Balancing held by the load floor)");
        var loadsField = new System.Text.RegularExpressions.Regex(@"loads \[([^\]]*)\]");
        var pinFields = new System.Text.RegularExpressions.Regex(@"kind ([a-z-]+), load \d+ \S+, (\S+)->(\S+), peak load (\d+)->\d+.*?overload reduction (-?\d+).*?estimated migration lag (\d+) ms.*?members \[([^\]]*)\].*gain=([0-9.]+|n/a)");
        Dictionary<string, List<(DateTime At, double Ratio)>> runs = [];
        Dictionary<string, long> lastMeasured = [];
        DateTime clock = DateTime.UnixEpoch;
        TimeSpan? previous = null;
        int pins = 0, kept = 0, floorHeld = 0, gainRefused = 0;
        Dictionary<string, int> keptByKind = [];
        foreach (string text in File.ReadLines(file))
        {
            var m = line.Match(text);
            if (!m.Success) continue;
            TimeSpan time = TimeSpan.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            // The log's clock has no date: a step back of more than 12 h is midnight.
            clock = clock.Date + time + (previous is { } p && (p - time).TotalHours > 12 ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            previous = time;
            if (loadsField.Match(text) is not { Success: true } loadsMatch) continue;
            (string Node, long Load)[] logged = [.. loadsMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=')).Select(x => (x[0], long.Parse(x[1], CultureInfo.InvariantCulture)))];
            // This line as a pass: each node's ratio reading through the gateway's own sustain rule.
            (string Node, PartitionReport Report)[] pass = [.. logged.Select(x => (x.Node, new PartitionReport(0, 0, [], 0, 0,
                SimulationRatio: reciprocal.TryGetValue(x.Load, out double r) ? r : 1, SimLoadSupported: true)))];
            (string Node, PartitionReport Report)[] read = assumeSustained ? pass
                : SustainRatios(pass, n => n, runs, model.SimRatioSustainSeconds, clock);
            Dictionary<string, long> effective = [];
            foreach (((string node, long load), int i) in logged.Select((x, i) => (x, i)))
            {
                if (!reciprocal.ContainsKey(load)) lastMeasured[node] = load;
                effective[node] = read[i].Report.SimulationRatio < 0.98 || !reciprocal.ContainsKey(load) ? load
                    : lastMeasured.GetValueOrDefault(node, load);
            }
            if (m.Groups[2].Value != "Automatic balance migration pinned" || time < from || time > to) continue;
            var pin = pinFields.Match(text);
            if (!pin.Success) continue;
            pins++;
            string kind = pin.Groups[1].Value;
            long peak = effective.Values.Max();
            double meanLoad = effective.Values.Average();
            long reduction = long.Parse(pin.Groups[5].Value, CultureInfo.InvariantCulture);
            double lagMs = double.Parse(pin.Groups[6].Value, CultureInfo.InvariantCulture);
            string verdict;
            // The idle-cluster plan and the heavy moves pass neither gate (ChooseRebalancePlans): kept as logged.
            if (kind is not ("load" or "client")) verdict = "pinned (passes no gate)";
            else if (model.BalanceMinPeakLoad > 0 && peak < model.BalanceMinPeakLoad) { floorHeld++; verdict = $"held by the floor at peak {peak}"; }
            else if (kind == "client" && model.ClientMinGainPerLagSecond > 0
                && reduction / Math.Max(meanLoad, 1.0) < model.ClientMinGainPerLagSecond * lagMs / 1000)
            {
                gainRefused++;
                verdict = $"refused: gain {reduction / Math.Max(meanLoad, 1.0):F3} < {model.ClientMinGainPerLagSecond * lagMs / 1000:F3} for {lagMs / 1000:F1} s";
            }
            else verdict = "pinned";
            if (verdict.StartsWith("pinned"))
            {
                kept++;
                keptByKind[kind] = keptByKind.GetValueOrDefault(kind) + 1;
            }
            Console.WriteLine($"{m.Groups[1].Value} {kind} [{pin.Groups[7].Value}] {pin.Groups[2].Value}->{pin.Groups[3].Value}"
                + $" logged peak {pin.Groups[4].Value} lag {lagMs / 1000:F1} s gain {pin.Groups[8].Value}: {verdict}");
        }
        Console.WriteLine($"pins {pins}: still pinned {kept} ({string.Join(", ", keptByKind.Select(x => $"{x.Key} {x.Value}"))}),"
            + $" held by the floor {floorHeld}, refused for gain per lag second {gainRefused}"
            + $" (SimRatioSustainSeconds {(assumeSustained ? "assumed met" : model.SimRatioSustainSeconds.ToString(CultureInfo.InvariantCulture))},"
            + $" BalanceMinPeakLoad {model.BalanceMinPeakLoad}, ClientMinGainPerLagSecond {model.ClientMinGainPerLagSecond})");
    }
}
