using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static ClusterRegistry.DirectGateway;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    // Fixture partitions estimate snapshot bytes exactly like partition-placement's (the node's estimator).
    private static PartitionInfo PaceAt(ulong id, double x, int members, int pcu, ulong[]? clients = null) =>
        new(id, members, true, new PartitionSphere(x, 0, 0, 100), 0, clients ?? [], pcu, 400L * pcu + 8_000L * members + 2_000);

    private static (PartitionReport Report, string Node) PaceNode(string name, params PartitionInfo[] parts) =>
        (new PartitionReport(3000, 1000, parts, 0, 50), name);

    private static (PartitionReport Report, string Node) PaceSimNode(string name, params PartitionInfo[] parts) =>
        (new PartitionReport(3000, 1000, parts, 0, 50, SimLoadSupported: true), name);

    /// <summary>
    /// T-0393: the planner fixtures the default-equivalence controls run on - the partition-placement shapes
    /// (w23 packed cluster, secondary imbalance, audience-weighted targets, client pile, the per-node-disjoint
    /// pair, in-flight and client-bound siblings, the T-0385 crowd, measured sim load, the startup pile) plus
    /// the effect shapes of the pace sections, in paced and full-plan mode.
    /// </summary>
    private static (string Name, (PartitionReport Report, string Node)[] Nodes, HashSet<ulong> Routed, bool FullPlan,
        IReadOnlySet<string>? Busy)[] PaceFixtures()
    {
        static PartitionInfo SimAt(ulong id, double x, int pcu, long simLoad) =>
            new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, [], pcu, 400L * pcu + 10_000, simLoad);
        var shapes = new List<(string, (PartitionReport, string)[], HashSet<ulong>)>
        {
            ("w23-packed", [PaceNode("h", PaceAt(1, 0, 324, 466000), PaceAt(2, 100000, 20, 33498), PaceAt(3, 101000, 16, 45752),
                    PaceAt(4, 200000, 1, 1204)),
                PaceNode("b", PaceAt(5, 300000, 1, 93000)), PaceNode("c", PaceAt(6, 400000, 1, 8000)), PaceNode("d", PaceAt(7, 500000, 1, 6000))], []),
            ("secondary", [PaceNode("h", PaceAt(1, 0, 300, 435000)), PaceNode("b", PaceAt(2, 100000, 5, 95000), PaceAt(3, 200000, 5, 82000)),
                PaceNode("c", PaceAt(4, 300000, 1, 2400)), PaceNode("d", PaceAt(5, 400000, 1, 102000))], []),
            ("quiet-target", [PaceNode("h", PaceAt(1, 0, 1, 15000), PaceAt(2, 10000, 1, 5000), PaceAt(6, 20000, 1, 5000)),
                PaceNode("b", PaceAt(3, 100000, 1, 3000, [77])), PaceNode("c", PaceAt(4, 200000, 1, 3000))], [77]),
            ("busy-peak", [PaceNode("h", PaceAt(21, 0, 1, 15000, [77]), PaceAt(22, 10000, 1, 5000)),
                PaceNode("b", PaceAt(23, 100000, 1, 100)), PaceNode("c", PaceAt(24, 200000, 1, 100))], [77]),
            ("client-pile", [PaceNode("h", PaceAt(31, 0, 1, 20000), PaceAt(32, 10000, 1, 1000, [80]), PaceAt(33, 20000, 1, 1000, [81])),
                PaceNode("b", PaceAt(34, 100000, 1, 20000), PaceAt(35, 110000, 1, 1000), PaceAt(36, 120000, 1, 1000))], [80, 81]),
            ("pair", [PaceNode("h1", PaceAt(41, 0, 1, 20000), PaceAt(42, 10000, 1, 5000)),
                PaceNode("h2", PaceAt(43, 100000, 1, 20000), PaceAt(44, 110000, 1, 5000)),
                PaceNode("e1", PaceAt(45, 200000, 1, 100)), PaceNode("e2", PaceAt(46, 300000, 1, 100))], []),
            ("in-flight-sibling", [PaceNode("h", PaceAt(1, 0, 1, 20000), PaceAt(4, 50000, 1, 9000) with { MaxMemberSpeed = 100 },
                    PaceAt(5, 60000, 1, 9000)),
                PaceNode("b", PaceAt(2, 100000, 1, 15000)), PaceNode("c", PaceAt(3, 200000, 1, 15000))], []),
            ("client-bound-release", [PaceNode("h", PaceAt(1, 0, 1, 20000), PaceAt(4, 50000, 1, 9000, [77]), PaceAt(5, 60000, 1, 9000, [78]),
                    PaceAt(6, 70000, 1, 9000, [79])),
                PaceNode("b", PaceAt(2, 100000, 1, 15000)), PaceNode("c", PaceAt(3, 200000, 1, 15000))], [77, 78, 79]),
            ("crowd", [PaceNode("h", PaceAt(1, 0, 1, 20000), PaceAt(4, 50000, 31, 9000, [77])),
                PaceNode("b", PaceAt(2, 100000, 1, 15000)), PaceNode("c", PaceAt(3, 200000, 1, 15000))], [77]),
            ("measured", [PaceSimNode("busy", SimAt(50, 0, 10000, 90_000), SimAt(51, 10000, 10000, 60_000)),
                PaceSimNode("parked", SimAt(52, 100000, 20000, 500)), PaceSimNode("idle", SimAt(53, 200000, 20000, 400))], []),
            ("startup-pile", [PaceNode("h", PaceAt(1, 0, 1, 7000), PaceAt(2, 10000, 1, 6000), PaceAt(3, 20000, 1, 5000),
                    PaceAt(4, 30000, 1, 4000), PaceAt(5, 40000, 1, 3000), PaceAt(6, 50000, 1, 3000)),
                PaceNode("b"), PaceNode("c")], []),
            ("converged", [PaceNode("a", PaceAt(1, 0, 1, 10000)), PaceNode("b", PaceAt(2, 100000, 1, 9000)),
                PaceNode("c", PaceAt(3, 200000, 1, 9500))], []),
            ("hitch", PaceHitchShape(), []),
            ("pebbles", PacePebbleShape(), []),
            ("client-crumbs", PaceClientCrumbShape(out HashSet<ulong> crumbRouted), crumbRouted),
            ("budget", PaceBudgetShape(5000), []),
            ("budget-crisis", PaceBudgetShape(12000), []),
            ("deadband", PaceBudgetShape(2000), []),
            ("settled", PaceBudgetShape(1000), []),
        };
        var fixtures = new List<(string, (PartitionReport, string)[], HashSet<ulong>, bool, IReadOnlySet<string>?)>();
        foreach ((string name, (PartitionReport, string)[] nodes, HashSet<ulong> routed) in shapes)
        {
            fixtures.Add((name + "/paced", nodes, routed, false, null));
            fixtures.Add((name + "/full", nodes, routed, true, null));
        }
        fixtures.Add(("pair/paced-busy-h2", shapes.First(s => s.Item1 == "pair").Item2, [], false, new HashSet<string> { "h2" }));
        return [.. fixtures];
    }

    // A source with a cheap pebble (6000, one member) and a costly 30000 (100 members, an entity stall) above a
    // 50000 floor: the default ranking prefers the cheap pebble, a fixed hitch price the big gain. The control
    // fixture has 20000 on b and c (the optimizer also shuffles b's 20000, so the 30000 reduces only 16000);
    // the effect case empty b and c (the 30000 reduces all of its 30000).
    private static (PartitionReport Report, string Node)[] PaceHitchShape(int others = 20000) =>
    [
        PaceNode("h", PaceAt(1, 0, 1, 50000), PaceAt(2, 100000, 100, 30000), PaceAt(3, 200000, 1, 6000)),
        others == 0 ? PaceNode("b") : PaceNode("b", PaceAt(4, 300000, 1, others)),
        others == 0 ? PaceNode("c") : PaceNode("c", PaceAt(5, 400000, 1, others)),
    ];

    // Twenty 500-PCU pebbles on one node next to two 2000 nodes: deviation far above tolerance, every single
    // move a gain of 500 / mean 4667 = 0.107.
    private static (PartitionReport Report, string Node)[] PacePebbleShape() =>
    [
        PaceNode("h", [.. Enumerable.Range(0, 20).Select(i => PaceAt((ulong)(100 + i), i * 20000.0, 1, 500))]),
        PaceNode("b", PaceAt(200, 1_000_000, 1, 2000)), PaceNode("c", PaceAt(201, 1_100_000, 1, 2000)),
    ];

    // Load-balanced (32000 each) with twelve 1-client crumbs on one node: only the client tier moves, each move
    // a gain of one client against a mean of 6 per node.
    private static (PartitionReport Report, string Node)[] PaceClientCrumbShape(out HashSet<ulong> routed)
    {
        routed = [.. Enumerable.Range(0, 12).Select(i => (ulong)(900 + i))];
        return
        [
            PaceNode("h", [PaceAt(300, 0, 1, 20000), .. Enumerable.Range(0, 12).Select(i => PaceAt((ulong)(301 + i), 10000 + i * 20000.0, 1, 1000, [(ulong)(900 + i)]))]),
            PaceNode("b", PaceAt(400, 1_000_000, 1, 32000)),
        ];
    }

    // Two equal movers above a 10000 floor next to b (10000) and the lighter c (9000): the paced pass sends one
    // mover, to the emptier c by the tie-break. The mover size sets the worst deviation: 1000 reads 0.15 or less, 2000 inside
    // (0.15, 0.25], 5000 above the tolerance but inside a 0.6 budget band, 12000 a crisis above it.
    private static (PartitionReport Report, string Node)[] PaceBudgetShape(int moverPcu) =>
    [
        PaceNode("h", PaceAt(1, 0, 1, 10000), PaceAt(2, 100000, 1, moverPcu), PaceAt(3, 200000, 1, moverPcu)),
        PaceNode("b", PaceAt(4, 300000, 1, 10000)), PaceNode("c", PaceAt(5, 400000, 1, 9000)),
    ];

    /// <summary>Every field of a plan set and of the pass diagnostics, round-trip formatted, so "identical" is byte-identical.</summary>
    private static string PlanSignature(RebalancePlan[] plans,
        (double Excess, double AchievableExcess, double WorstDeviation, double ClientExcess, double WorstClientDeviation, string Explain,
            int MinGainSkipped, int BudgetSkipped, long FloorHeldPeak, int PeakDropSkipped) diag)
    {
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append($"diag {R(diag.Excess)} {R(diag.AchievableExcess)} {R(diag.WorstDeviation)} {R(diag.ClientExcess)}"
            + $" {R(diag.WorstClientDeviation)} {diag.Explain}\n");
        foreach (RebalancePlan p in plans)
            text.Append($"plan [{string.Join(",", p.Members.Select(m => m.Id))}] {p.Source}->{p.Target} {p.ComponentLoad} {p.OldPeak}->{p.NewPeak}"
                + $" {R(p.CostMs)} {R(p.Excess)} {p.LoadUnit} {p.Kind} {p.OverloadReduction} {p.Explain}\n");
        return text.ToString();
    }

    private static string ShortHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>
    /// The PlanSignature hashes of the df 9a65347 planner - before T-0393 touched it - on <see cref="PaceFixtures"/>
    /// with <c>new CostModel()</c> and nothing blocked (~/cluster-runs/regression/tools/slot0/t0393/baseline-signatures-df-9a65347.log).
    /// A mismatch means the default planner is no longer byte-identical to the one the live cluster ran.
    /// </summary>
    private static readonly Dictionary<string, string> PaceBaseline = new()
    {
        ["w23-packed/paced"] = "B956EFCD12474FD9",
        ["w23-packed/full"] = "F51442DDE62D307A",
        ["secondary/paced"] = "1B95CE7EA878925B",
        ["secondary/full"] = "77073688F53999D2",
        ["quiet-target/paced"] = "4EC6EEEBF4C60534",
        ["quiet-target/full"] = "88C44237FEAB5A95",
        ["busy-peak/paced"] = "89D83444C46FA535",
        ["busy-peak/full"] = "49772B8E9C1BC04E",
        ["client-pile/paced"] = "A54A453913F0DB5F",
        ["client-pile/full"] = "0D2D5BCD5731E10A",
        ["pair/paced"] = "C9F2C6ABC370C3CF",
        ["pair/full"] = "A11324D6E2EC671C",
        ["in-flight-sibling/paced"] = "94C16E66A87FC7DB",
        ["in-flight-sibling/full"] = "B1509D632A471E61",
        ["client-bound-release/paced"] = "7FC579E3A39C0C95",
        ["client-bound-release/full"] = "4633A2E00BF7DE3B",
        ["crowd/paced"] = "06CC63CD1C796072",
        ["crowd/full"] = "19D76688555F6410",
        ["measured/paced"] = "8CA91F9314DED12F",
        ["measured/full"] = "1FC2C95E5DBE0715",
        ["startup-pile/paced"] = "AD2EB9B30F1CF0D3",
        ["startup-pile/full"] = "98EF6BE97B26FEF8",
        ["converged/paced"] = "F530ED51E09B7D24",
        ["converged/full"] = "F530ED51E09B7D24",
        ["hitch/paced"] = "243AE184F5DD1F3C",
        ["hitch/full"] = "18B0860C02116A7F",
        ["pebbles/paced"] = "235B1CBEB469DBFF",
        ["pebbles/full"] = "9DA6E0889ABAAF59",
        ["client-crumbs/paced"] = "4D48CDE239A6C1BA",
        ["client-crumbs/full"] = "D7C19201AE33F296",
        ["budget/paced"] = "2D2AB2CE8312D6DE",
        ["budget/full"] = "84F2937E14B6887A",
        ["budget-crisis/paced"] = "A344B519E4695368",
        ["budget-crisis/full"] = "519E2C3126FD47B6",
        ["deadband/paced"] = "6DFF6293CD696B48",
        ["deadband/full"] = "6DFF6293CD696B48",
        ["settled/paced"] = "389252F00065BC53",
        ["settled/full"] = "389252F00065BC53",
        ["pair/paced-busy-h2"] = "3775E9E886D2B19E",
    };

    private static RebalancePlan[] PacePlan((PartitionReport Report, string Node)[] nodes, CostModel model, HashSet<ulong>? routed = null,
        bool fullPlan = false, double? gate = null, IReadOnlySet<string>? overBudget = null, Func<ulong, bool>? blocked = null) =>
        ChooseRebalancePlans(nodes, model, routed ?? [], blocked ?? (_ => false), null, null, fullPlan, null, out _, gate, overBudget);

    private static string PaceMoves(RebalancePlan[] plans) =>
        plans.Length == 0 ? "nothing" : string.Join("; ", plans.Select(p => $"[{string.Join(",", p.Members.Select(m => m.Id))}] {p.Source}->{p.Target} {p.Kind}"));

    /// <summary>
    /// Control (a) of every pace section: on every fixture, paced and full plan, the planner with <paramref name="model"/>
    /// (a knob set to its default through the live override path), <paramref name="gate"/> and <paramref name="overBudget"/>
    /// produces the byte-identical plans and diagnostics of <c>new CostModel()</c> - and those still hash to the df planner's.
    /// </summary>
    private static void PaceDefaultControl(string section, CostModel model, double? gate = null, IReadOnlySet<string>? overBudget = null)
    {
        foreach (var f in PaceFixtures())
        {
            RebalancePlan[] reference = ChooseRebalancePlans(f.Nodes, new CostModel(), f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d0);
            RebalancePlan[] knob = ChooseRebalancePlans(f.Nodes, model, f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d1, gate, overBudget);
            string expected = PlanSignature(reference, d0), actual = PlanSignature(knob, d1);
            if (actual != expected)
                throw new InvalidOperationException($"{section}: default control {f.Name}: plans differ from new CostModel():\n{expected}--- vs ---\n{actual}");
            if (d1.MinGainSkipped != 0 || d1.BudgetSkipped != 0)
                throw new InvalidOperationException($"{section}: default control {f.Name}: skipped {d1.MinGainSkipped} by min-gain, {d1.BudgetSkipped} by budget");
            if (ShortHash(expected) != PaceBaseline[f.Name])
                throw new InvalidOperationException($"{section}: default control {f.Name}: hash {ShortHash(expected)} is not the df planner's"
                    + $" {PaceBaseline[f.Name]} ({PaceMoves(reference)})\n{expected}");
        }
    }

    private static CostModel PaceOverride(string field, string value) => CostModel.WithOverrides(new CostModel(), [KeyValuePair.Create(field, value)]);

    /// <summary>T-0393 knob 1: a fixed per-move hitch price reorders the paced ranking toward the bigger gain.</summary>
    private static void TestBalanceHitchPrice()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-hitch-price: " + what); }
        Check(new CostModel().HitchMsPerMove == 0, "default HitchMsPerMove is 0");
        PaceDefaultControl("balance-hitch-price", PaceOverride("HitchMsPerMove", "0"));
        // h sheds either the cheap 6000 pebble (1 member, ~888 ms) or the 30000 with an entity stall (100 members, ~5318 ms);
        // both share the source, so one pass pins one. Per ms of estimated lag the pebble wins (6.75 vs 5.64); with
        // 1000 ms of hitch on each the bigger gain wins (4.75 vs 3.18).
        RebalancePlan[] plain = PacePlan(PaceHitchShape(0), new CostModel());
        Check(plain.Length == 1 && plain[0].Members.Single().Id == 3, "without a hitch price the cheap pebble ranks first: " + PaceMoves(plain));
        RebalancePlan[] priced = PacePlan(PaceHitchShape(0), PaceOverride("HitchMsPerMove", "1000"));
        Check(priced.Length == 1 && priced[0].Members.Single().Id == 2 && priced[0].Kind == RebalanceKind.Load,
            "with HitchMsPerMove 1000 the 30000 gain ranks first: " + PaceMoves(priced));
        // The price is not part of the plan's estimated lag (CostMs), and it only reorders: the pebble is still a candidate.
        Check(priced[0].CostMs == PacePlan(PaceHitchShape(0), new CostModel(), fullPlan: true).Single(p => p.Members.Single().Id == 2).CostMs,
            "CostMs stays the estimated lag");
        Check(PacePlan([.. PaceHitchShape(0).Select(n => n.Node == "h"
            ? (new PartitionReport(3000, 1000, [.. n.Report.Partitions.Where(p => p.Id != 2)], 0, 50), n.Node) : n)],
            PaceOverride("HitchMsPerMove", "100000")).Single().Members.Single().Id == 3, "a hitch price never disqualifies the only candidate");
        Console.WriteLine("balance-hitch-price: default identical to the df planner on every fixture; 1000 ms per move ranks the 30000 gain over the cheap pebble");
    }

    /// <summary>T-0393 knob 2: candidates whose gain is below a fraction of the mean node load (clients: of the mean clients per node) do not qualify.</summary>
    private static void TestBalanceMinGain()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-min-gain: " + what); }
        Check(new CostModel().MinGainFraction == 0, "default MinGainFraction is 0");
        PaceDefaultControl("balance-min-gain", PaceOverride("MinGainFraction", "0"));
        // Load tier: twenty 500 pebbles next to two 2000 nodes, mean node load 14000 / 3; every move's gain is 0.107.
        RebalancePlan[] pebble = ChooseRebalancePlans(PacePebbleShape(), new CostModel(), [], _ => false, null, null, false, null, out var d0);
        Check(pebble.Length == 1 && pebble[0].OverloadReduction == 500 && Math.Abs(pebble[0].MeanLoad - 14000 / 3.0) < 1e-9
            && d0.MinGainSkipped == 0, "default: a pebble moves, gain 500 over mean 4667: " + PaceMoves(pebble));
        Check(Math.Abs(pebble[0].OverloadReduction / pebble[0].MeanLoad - 0.107) < 0.001, "the pin's gain is 0.107");
        Check(PacePlan(PacePebbleShape(), PaceOverride("MinGainFraction", "0.1")).Length == 1, "0.107 >= 0.1 qualifies");
        RebalancePlan[] refused = ChooseRebalancePlans(PacePebbleShape(), PaceOverride("MinGainFraction", "0.2"), [], _ => false, null, null, false, null, out var d1);
        Check(refused.Length == 0 && d1.MinGainSkipped > 0, $"0.107 < 0.2 refuses every pebble: {PaceMoves(refused)}, skipped {d1.MinGainSkipped}");
        // A big gain next to the pebbles still qualifies under the same fraction (the w23 cluster: 79250 of mean ~172000).
        Check(PacePlan(PaceFixtures().First(f => f.Name == "w23-packed/paced").Nodes, PaceOverride("MinGainFraction", "0.2")).Length == 1,
            "a large gain is not refused");
        // Client tier: twelve 1-client crumbs on h, loads even; mean clients per node 6, every move a gain of 1 client.
        RebalancePlan[] crumbs = PaceClientPlan(new CostModel(), out int skipped0);
        Check(crumbs.Length == 1 && crumbs[0].Kind == RebalanceKind.Client && skipped0 == 0, "default: a client crumb moves: " + PaceMoves(crumbs));
        Check(PaceClientPlan(PaceOverride("MinGainFraction", "0.15"), out _).Length == 1, "1 >= 0.15 * 6 qualifies");
        RebalancePlan[] crumbsRefused = PaceClientPlan(PaceOverride("MinGainFraction", "0.2"), out int skipped1);
        Check(crumbsRefused.Length == 0 && skipped1 > 0, $"1 < 0.2 * 6 refuses every crumb: {PaceMoves(crumbsRefused)}, skipped {skipped1}");
        Console.WriteLine("balance-min-gain: default identical to the df planner on every fixture; 0.2 refuses pebbles of gain 0.107 and client crumbs of 1/6, keeps the large gain");
    }

    private static RebalancePlan[] PaceClientPlan(CostModel model, out int minGainSkipped)
    {
        var nodes = PaceClientCrumbShape(out HashSet<ulong> routed);
        RebalancePlan[] plans = ChooseRebalancePlans(nodes, model, routed, _ => false, null, null, false, null, out var diag);
        minGainSkipped = diag.MinGainSkipped;
        return plans;
    }

    /// <summary>
    /// T-0393 knob 3: the low-water hysteresis over a sequence of passes. Shapes: "deadband" deviation 0.19 (inside
    /// (0.15, 0.25]), "budget" 0.42 (above the high water), "settled" 0.10 (inside the low water).
    /// </summary>
    private static void TestBalanceLowWater()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-low-water: " + what); }
        var model = new CostModel();
        Check(model.BalanceLowWater < 0 && model.EffectiveLowWater() == model.BalanceRiskTolerance, "default low water is the tolerance");
        Check(PaceOverride("BalanceLowWater", "0.4").EffectiveLowWater() == model.BalanceRiskTolerance, "a low water above the tolerance is clamped to it");
        PaceDefaultControl("balance-low-water", PaceOverride("BalanceLowWater", "-1"), gate: model.BalanceRiskTolerance);
        PaceDefaultControl("balance-low-water", PaceOverride("BalanceLowWater", "0.25"), gate: PaceOverride("BalanceLowWater", "0.25").EffectiveLowWater());
        var deadband = PaceBudgetShape(2000);
        var high = PaceBudgetShape(5000);
        var settled = PaceBudgetShape(1000);
        var sequence = new[] { ("deadband", deadband), ("high", high), ("deadband", deadband), ("deadband", deadband), ("settled", settled),
            ("deadband", deadband), ("high", high) };
        DateTime t0 = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);

        // The default runs the whole sequence exactly like the stateless planner: the gate is the tolerance in every state.
        var plain = new BalancePace();
        for (int pass = 0; pass < sequence.Length; pass++)
        {
            double gate = plain.Gate(model);
            RebalancePlan[] plans = ChooseRebalancePlans(sequence[pass].Item2, model, [], _ => false, null, null, false, null, out var d, gate);
            Check(gate == model.BalanceRiskTolerance && PlanSignature(plans, d) == PlanSignature(PacePlan(sequence[pass].Item2, model), d),
                $"default pass {pass} ({sequence[pass].Item1}) is the stateless plan");
            plain.Passed(model, gate, d.WorstDeviation, d.WorstClientDeviation, plans.Length, 0, 0, 0, t0.AddMinutes(pass));
        }
        Check(plain.LastHour(t0.AddMinutes(sequence.Length)).DeadbandHolds == 0, "no deadband without a low water");

        // Low water 0.15: idle until the high water is crossed, then balance down to 0.15, then idle again.
        CostModel hysteresis = PaceOverride("BalanceLowWater", "0.15");
        var pace = new BalancePace();
        bool[] expectMoves = [false, true, true, true, false, false, true];
        bool[] expectActive = [false, true, true, true, false, false, true];
        for (int pass = 0; pass < sequence.Length; pass++)
        {
            double gate = pace.Gate(hysteresis);
            RebalancePlan[] plans = ChooseRebalancePlans(sequence[pass].Item2, hysteresis, [], _ => false, null, null, false, null, out var d, gate);
            pace.Passed(hysteresis, gate, d.WorstDeviation, d.WorstClientDeviation, plans.Length, 0, 0, 0, t0.AddMinutes(pass));
            Check((plans.Length != 0) == expectMoves[pass] && pace.Active == expectActive[pass],
                $"pass {pass} ({sequence[pass].Item1}, deviation {d.WorstDeviation:F3}, gate {gate:F2}): moves {PaceMoves(plans)}, active {pace.Active}");
        }
        var hour = pace.LastHour(t0.AddMinutes(sequence.Length));
        Check(hour.DeadbandHolds == 2 && hour.Pins == 4, $"two deadband holds (passes 0 and 5) and four pins, got {hour.DeadbandHolds} and {hour.Pins}");
        // The counters slide: an hour after pass 5 only pass 6's pin is left.
        var later = pace.LastHour(t0.AddMinutes(5).AddSeconds(3600));
        Check(later.DeadbandHolds == 0 && later.Pins == 1, $"an hour later: holds {later.DeadbandHolds}, pins {later.Pins}");
        Console.WriteLine("balance-low-water: default identical to the df planner; with 0.15 the balancer starts above 0.25, continues through 0.19, stops at 0.10 and holds at 0.19 again");
    }

    /// <summary>T-0393 knob 4: the residence size threshold, formerly the const 2000 ms.</summary>
    private static void TestBalanceResidenceSize()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-residence-size: " + what); }
        var model = new CostModel();
        Check(model.ResidenceMinFreezeMs == 2000, "default ResidenceMinFreezeMs is 2000 (the former const)");
        // Default control 1: the hold decision equals the former const rule over a grid of sizes, both sides of 2000 ms.
        CostModel reset = PaceOverride("ResidenceMinFreezeMs", "2000");
        int held = 0, free = 0;
        foreach (int members in new[] { 1, 5, 9, 10, 50, 100, 150, 190, 200, 300 })
            foreach (int pcu in new[] { 0, 100, 1000, 5000, 10000, 13000, 20000, 50000 })
            {
                PartitionInfo p = PaceAt(1, 0, members, pcu);
                bool before = model.FreezeMs(p.EstimatedSnapshotBytes, p.MemberCount) >= 2000;
                Check(ResidenceSizeHeld(model, p) == before && ResidenceSizeHeld(reset, p) == before, $"members {members} pcu {pcu}: the const rule");
                if (before) held++; else free++;
            }
        Check(held > 0 && free > 0, "the grid spans both sides of 2000 ms");
        // Default control 2: the planner fed the residence predicate (every partition just arrived) plans as with the const rule.
        foreach (var f in PaceFixtures())
        {
            Dictionary<ulong, PartitionInfo> reported = f.Nodes.SelectMany(n => n.Report.Partitions).ToDictionary(p => p.Id);
            string withField = PlanSignature(ChooseRebalancePlans(f.Nodes, reset, f.Routed, id => ResidenceSizeHeld(reset, reported[id]),
                null, null, f.FullPlan, f.Busy, out var d1), d1);
            string withConst = PlanSignature(ChooseRebalancePlans(f.Nodes, model, f.Routed,
                id => model.FreezeMs(reported[id].EstimatedSnapshotBytes, reported[id].MemberCount) >= 2000,
                null, null, f.FullPlan, f.Busy, out var d0), d0);
            Check(withField == withConst, $"{f.Name}: residence plans as the const rule");
        }
        // Effect: the budget shape's movers are small (5000 PCU, ~760 ms) - free to re-move under the default, held at 0.
        var shape = PaceBudgetShape(5000);
        Dictionary<ulong, PartitionInfo> parts = shape.SelectMany(n => n.Report.Partitions).ToDictionary(p => p.Id);
        CostModel all = PaceOverride("ResidenceMinFreezeMs", "0");
        Check(!ResidenceSizeHeld(model, parts[2]) && ResidenceSizeHeld(all, parts[2]), "a 5000-PCU arrival is held only at 0");
        RebalancePlan[] free2000 = PacePlan(shape, model, blocked: id => ResidenceSizeHeld(model, parts[id]));
        RebalancePlan[] held0 = PacePlan(shape, all, blocked: id => ResidenceSizeHeld(all, parts[id]));
        Check(free2000.Length == 1 && held0.Length == 0, $"just-arrived movers: default moves {PaceMoves(free2000)}, 0 moves {PaceMoves(held0)}");
        Console.WriteLine($"balance-residence-size: default = the const 2000 rule on {held + free} sizes and every fixture; 0 holds a small arrival");
    }

    /// <summary>T-0393 knob 5: the per-target move budget under a deviation threshold, over a sliding hour.</summary>
    private static void TestBalanceTargetBudget()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-target-budget: " + what); }
        var model = new CostModel();
        Check(model.BudgetMovesPerNodeHour == 0 && model.BudgetUnderDeviation == 0, "default budget is off");
        DateTime t0 = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        // Default control: pins recorded under the default never make an over-budget set, and an over-budget set is inert
        // while BudgetUnderDeviation is 0 (no deviation is below it) - even with a move cap configured.
        var idle = new BalancePace();
        for (int i = 0; i < 50; i++) idle.Pinned("c", t0.AddSeconds(i));
        Check(idle.OverBudget(model, t0.AddMinutes(1)) == null, "the default has no over-budget set");
        HashSet<string> everyone = ["a", "b", "c", "d", "h", "h1", "h2", "e1", "e2", "busy", "parked", "idle"];
        PaceDefaultControl("balance-target-budget", PaceOverride("BudgetMovesPerNodeHour", "0"), overBudget: everyone);
        PaceDefaultControl("balance-target-budget", PaceOverride("BudgetMovesPerNodeHour", "2"), overBudget: everyone);

        CostModel budget = CostModel.WithOverrides(new CostModel(),
            [KeyValuePair.Create("BudgetMovesPerNodeHour", "2"), KeyValuePair.Create("BudgetUnderDeviation", "0.6")]);
        var shape = PaceBudgetShape(5000);         // deviation 0.42: inside the budget band
        var crisis = PaceBudgetShape(12000);       // deviation 1.38: a real overload
        var pace = new BalancePace();
        RebalancePlan[] Pass(BalancePace p, (PartitionReport Report, string Node)[] nodes, DateTime now, out int skipped)
        {
            IReadOnlySet<string>? over = p.OverBudget(budget, now);
            RebalancePlan[] plans = ChooseRebalancePlans(nodes, budget, [], _ => false, null, null, false, null, out var d, p.Gate(budget), over);
            foreach (RebalancePlan plan in plans) p.Pinned(plan.Target, now);
            p.Passed(budget, p.Gate(budget), d.WorstDeviation, d.WorstClientDeviation, plans.Length, d.MinGainSkipped, d.BudgetSkipped, 0, now);
            skipped = d.BudgetSkipped;
            return plans;
        }
        // Passes at 0 and 10 min pin the mover to c, the emptier target, and use up c's budget of 2.
        Check(Pass(pace, shape, t0, out _).Single().Target == "c" && Pass(pace, shape, t0.AddMinutes(10), out _).Single().Target == "c",
            "the first two passes pin to c");
        Check(pace.OverBudget(budget, t0.AddMinutes(20)) is { } over && over.SetEquals(["c"]), "c is over budget after two pins");
        // At 20 min the c candidate - the optimizer's only planned move here - is skipped, and the pass pins nothing.
        RebalancePlan[] third = Pass(pace, shape, t0.AddMinutes(20), out int skipped3);
        Check(third.Length == 0 && skipped3 == 1, $"over budget, c is skipped: {PaceMoves(third)}, skipped {skipped3}");
        // A crisis is served at once, budget or not: the crisis shape still pins to c.
        RebalancePlan[] crisisPlan = Pass(pace, crisis, t0.AddMinutes(25), out int skippedCrisis);
        Check(crisisPlan.Single().Target == "c" && skippedCrisis == 0, $"above the budget threshold the budget is off: {PaceMoves(crisisPlan)}");
        Check(PlanSignature(crisisPlan, default) == PlanSignature(PacePlan(crisis, new CostModel()), default), "the crisis plan is the default plan");
        // The window slides: at 60 min + 1 s the 0-min pin has left, c has 2 again (10 min and the crisis pin at 25 min) ...
        Check(pace.TargetPinsLastHour(t0.AddMinutes(60).AddSeconds(1)).GetValueOrDefault("c") == 2, "c: 10 and 25 min left in the window");
        // ... and once the 10-min pin has left too, c is under budget and is the target again.
        RebalancePlan[] freed = Pass(pace, shape, t0.AddMinutes(70).AddSeconds(1), out int skippedFreed);
        Check(freed.Single().Target == "c" && skippedFreed == 0, $"after the window slid, c is the target again: {PaceMoves(freed)}");
        var hour = pace.LastHour(t0.AddMinutes(70).AddSeconds(1));
        Check(hour.Pins == 2 && hour.BudgetSkips == 1,
            $"last hour from 10:01 on: the pins at 25 and 70 min and the 20-min skip ({hour.Pins}, {hour.BudgetSkips})");
        Check(pace.LastHour(t0.AddMinutes(80).AddSeconds(1)).BudgetSkips == 0, "the skip slides out of the hour");
        // The budget is per target: on the per-node-disjoint pair (deviation 0.50) an exhausted e1 drops only h1->e1.
        var pairPace = new BalancePace();
        pairPace.Pinned("e1", t0);
        pairPace.Pinned("e1", t0.AddMinutes(1));
        var pairNodes = PaceFixtures().First(f => f.Name == "pair/paced").Nodes;
        RebalancePlan[] pairPlans = Pass(pairPace, pairNodes, t0.AddMinutes(2), out int pairSkipped);
        Check(PacePlan(pairNodes, new CostModel()).Length == 2 && pairPlans.Length == 1 && pairPlans[0].Source == "h2"
            && pairPlans[0].Target == "e2" && pairSkipped == 1, $"only the exhausted target is skipped: {PaceMoves(pairPlans)}");
        Console.WriteLine("balance-target-budget: default and threshold 0 identical to the df planner; 2/h below 0.6 skips the third move to c, keeps the other target's,"
            + " a crisis still to c, and the sliding hour frees c again");
    }

    // T-0409: r142's own balance input - the first 'loads [...]' line of the run (gateway.log 2026-10-09 04:3x: node-819=554384
    // node-820=415238 node-822=518812 node-823=351340 node-824=203248 node-825=454841 sim-us/s; the logged optimum moves ~140k off
    // node-819 to node-824). node-819 carries its base and a heavy 110-member component of that size; every other node one partition.
    // Measured sim load throughout, so the pass runs in sim-us/s. scale multiplies every load (a busier cluster, same shape).
    private static (PartitionReport Report, string Node)[] PaceFloorShape(double scale = 1.0)
    {
        PartitionInfo At(ulong id, double x, int members, long simLoad) =>
            new(id, members, true, new PartitionSphere(x, 0, 0, 100), 0, [], 1000, 400L * 1000 + 8_000L * members + 2_000, (long)(simLoad * scale));
        return
        [
            PaceSimNode("node-819", At(1, 0, 1, 413_717), At(2, 100_000, 110, 140_667)),
            PaceSimNode("node-820", At(3, 200_000, 1, 415_238)),
            PaceSimNode("node-822", At(4, 300_000, 1, 518_812)),
            PaceSimNode("node-823", At(5, 400_000, 1, 351_340)),
            PaceSimNode("node-824", At(6, 500_000, 1, 203_248)),
            PaceSimNode("node-825", At(7, 600_000, 1, 454_841)),
        ];
    }

    /// <summary>T-0409: the balance load floor (CostModel.BalanceMinPeakLoad).</summary>
    private static void TestBalanceLoadFloor()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-load-floor: " + what); }
        Check(new CostModel().BalanceMinPeakLoad == 0 && CostModel.Deployed().BalanceMinPeakLoad == 800_000, "the floor is off by default and 800000 deployed");
        // (c) Default control: 0 through the live override path is byte-identical to the df planner on every fixture.
        PaceDefaultControl("balance-load-floor", PaceOverride("BalanceMinPeakLoad", "0"));
        // PCU passes and full plans never see the floor, even one far above every fixture's load.
        CostModel huge = PaceOverride("BalanceMinPeakLoad", "1e12");
        foreach (var f in PaceFixtures().Where(f => f.FullPlan || !f.Nodes.All(n => n.Report.SimLoadSupported)))
        {
            RebalancePlan[] reference = ChooseRebalancePlans(f.Nodes, new CostModel(), f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d0);
            RebalancePlan[] floored = ChooseRebalancePlans(f.Nodes, huge, f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d1);
            Check(PlanSignature(floored, d1) == PlanSignature(reference, d0) && d1.FloorHeldPeak == 0, $"{f.Name}: a PCU pass or a full plan ignores the floor");
        }

        // (a) Known positive on r142's input with the deployed model: at floor 0 the paced pass pins (VOID otherwise); at 800k it
        // holds, reports the peak it held at, and keeps the diagnostics the pace hysteresis and /diag read.
        CostModel live = CostModel.Deployed() with { BalanceMinPeakLoad = 0, BalanceMinPeakDrop = 0 };
        CostModel floored800 = live with { BalanceMinPeakLoad = 800_000 };
        var r142 = PaceFloorShape();
        RebalancePlan[] open = ChooseRebalancePlans(r142, live, [], _ => false, null, null, false, null, out var dOpen);
        if (open.Length == 0) throw new InvalidOperationException("balance-load-floor: VOID - r142's input pins nothing at floor 0 (no known positive)");
        RebalancePlan[] held = ChooseRebalancePlans(r142, floored800, [], _ => false, null, null, false, null, out var dHeld);
        Check(held.Length == 0 && dHeld.FloorHeldPeak == 554_384, $"r142 at 800k: {PaceMoves(held)}, held at {dHeld.FloorHeldPeak}");
        Check(dHeld.WorstDeviation == dOpen.WorstDeviation && dHeld.WorstClientDeviation == dOpen.WorstClientDeviation && dHeld.Explain == dOpen.Explain,
            "a held pass keeps its diagnostics");
        // The idle-cluster full plan is untouched by the floor.
        Check(PlanSignature(ChooseRebalancePlans(r142, floored800, [], _ => false, null, null, true, null, out var dFull1), dFull1)
            == PlanSignature(ChooseRebalancePlans(r142, live, [], _ => false, null, null, true, null, out var dFull0), dFull0), "the full plan ignores the floor");
        // (b) Above the floor: the same shape at 1.6x (peak 887k) plans exactly as at floor 0.
        var busy = PaceFloorShape(1.6);
        RebalancePlan[] above = ChooseRebalancePlans(busy, floored800, [], _ => false, null, null, false, null, out var dAbove);
        RebalancePlan[] aboveOpen = ChooseRebalancePlans(busy, live, [], _ => false, null, null, false, null, out var dAboveOpen);
        Check(above.Length != 0 && PlanSignature(above, dAbove) == PlanSignature(aboveOpen, dAboveOpen) && dAbove.FloorHeldPeak == 0,
            $"above the floor the plan is the floor-0 plan: {PaceMoves(above)} vs {PaceMoves(aboveOpen)}");

        // Concentration: the load rises pass by pass past the floor; balancing resumes at the first pass above it (one pace
        // interval), through the deployed hysteresis, and that pass plans exactly what floor 0 plans.
        double[] scales = [1.0, 1.3, 1.44, 1.5, 1.6];
        bool[] expectHeld = [true, true, true, false, false];
        var pace = new BalancePace();
        DateTime t0 = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        for (int pass = 0; pass < scales.Length; pass++)
        {
            var nodes = PaceFloorShape(scales[pass]);
            double gate = pace.Gate(floored800);
            RebalancePlan[] plans = ChooseRebalancePlans(nodes, floored800, [], _ => false, null, null, false, null, out var d, gate);
            RebalancePlan[] reference = ChooseRebalancePlans(nodes, live, [], _ => false, null, null, false, null, out var dRef, gate);
            Check((d.FloorHeldPeak > 0) == expectHeld[pass] && (expectHeld[pass] ? plans.Length == 0 : PlanSignature(plans, d) == PlanSignature(reference, dRef)),
                $"pass {pass} (scale {scales[pass]}, peak {(long)(554_384 * scales[pass])}): held at {d.FloorHeldPeak}, {PaceMoves(plans)} vs floor 0 {PaceMoves(reference)}");
            pace.Passed(floored800, gate, d.WorstDeviation, d.WorstClientDeviation, plans.Length, d.MinGainSkipped, d.BudgetSkipped, 0, t0.AddMinutes(pass));
        }

        // Co-location never sees the floor (the mutation guard): a pair inside the 4000 m envelope on two nodes, both far below
        // the floor, moves the same way with the floor at 800k as without it.
        static PartitionInfo Near(ulong id, double x, int members, long simLoad) =>
            new(id, members, true, new PartitionSphere(x, 0, 0, 100), 0, [], 1000, 400L * 1000 + 8_000L * members + 2_000, simLoad);
        var source = new PartitionReport(3000, 1000, [Near(31, 0, 1, 20_000)], 0, 50, SimLoadSupported: true);
        var target = new PartitionReport(3000, 1000, [Near(32, 1500, 40, 90_000)], 0, 50, SimLoadSupported: true);
        Move? plain = ChooseMove(source, target, 4000, [], _ => false, live, loadSource: 300_000, loadTarget: 200_000);
        Move? withFloor = ChooseMove(source, target, 4000, [], _ => false, floored800, loadSource: 300_000, loadTarget: 200_000);
        Check(plain is { Reason: "co-location" } && withFloor == plain, $"co-location ignores the floor: {plain} vs {withFloor}");
        Console.WriteLine($"balance-load-floor: default and PCU/full-plan identical to the df planner; r142's input pins {PaceMoves(open)} at 0 and holds"
            + $" at 800k (peak 554384); at 1.6x it plans as floor 0; rising load resumes at the first pass above the floor; co-location unchanged");
    }

    // T-0409 follow-up: r143's own unchanged-peak pin (A1 OFF, gateway.log 2026-10-09 13:51:26: node-854->node-851, 14996 sim-us/s,
    // peak 596114->596114; loads node-851=347448 node-852=309886 node-854=552030 node-855=475722 node-856=596114 node-858=410231,
    // the optimum takes ~111k off node-856, which was busy). node-856 carries its base and the 110976 component the optimum moves;
    // node-854 its base and the 14996 mover. scale multiplies every load.
    private static (PartitionReport Report, string Node)[] PacePeakDropShape(double scale = 1.0, ulong[]? clients = null)
    {
        PartitionInfo At(ulong id, double x, long simLoad, ulong[]? c = null) =>
            new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, c ?? [], 1000, 400L * 1000 + 8_000L + 2_000, (long)(simLoad * scale));
        return
        [
            PaceSimNode("node-851", At(11, 0, 347_448)),
            PaceSimNode("node-852", At(12, 100_000, 309_886)),
            PaceSimNode("node-854", At(13, 200_000, 537_034), At(14, 300_000, 14_996, clients)),
            PaceSimNode("node-855", At(15, 400_000, 475_722)),
            PaceSimNode("node-856", At(16, 500_000, 485_138), At(17, 600_000, 110_976)),
            PaceSimNode("node-858", At(18, 700_000, 410_231)),
        ];
    }

    /// <summary>T-0409 follow-up: the peak-drop guard (CostModel.BalanceMinPeakDrop).</summary>
    private static void TestBalancePeakDrop()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-peak-drop: " + what); }
        Check(new CostModel().BalanceMinPeakDrop == 0 && CostModel.Deployed().BalanceMinPeakDrop == 0.01, "the guard is off by default and 0.01 deployed");
        // Default control: 0 through the live override path is byte-identical to the df planner on every fixture.
        PaceDefaultControl("balance-peak-drop", PaceOverride("BalanceMinPeakDrop", "0"));
        // PCU passes and full plans never see the guard, even at a margin no move can meet.
        CostModel all = PaceOverride("BalanceMinPeakDrop", "0.99");
        foreach (var f in PaceFixtures().Where(f => f.FullPlan || !f.Nodes.All(n => n.Report.SimLoadSupported)))
        {
            RebalancePlan[] reference = ChooseRebalancePlans(f.Nodes, new CostModel(), f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d0);
            RebalancePlan[] guarded = ChooseRebalancePlans(f.Nodes, all, f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d1);
            Check(PlanSignature(guarded, d1) == PlanSignature(reference, d0) && d1.PeakDropSkipped == 0, $"{f.Name}: a PCU pass or a full plan ignores the guard");
        }

        CostModel live = CostModel.Deployed() with { BalanceMinPeakLoad = 0, BalanceMinPeakDrop = 0 };
        CostModel guard = live with { BalanceMinPeakDrop = 0.01 };
        IReadOnlySet<string> busy856 = new HashSet<string> { "node-856" };
        // (a) Known positive: r143's unchanged-peak pin. Off, the paced pass pins node-854->node-856's neighbour at an unchanged
        // peak (VOID otherwise); with the guard it is refused, counted, and the pass keeps its diagnostics.
        var r143 = PacePeakDropShape();
        RebalancePlan[] open = ChooseRebalancePlans(r143, live, [], _ => false, null, null, false, busy856, out var dOpen);
        if (!open.Any(p => p.Source == "node-854" && p.OldPeak == p.NewPeak))
            throw new InvalidOperationException($"balance-peak-drop: VOID - r143's input does not pin node-854 at an unchanged peak: {PaceMoves(open)}");
        RebalancePlan[] refused = ChooseRebalancePlans(r143, guard, [], _ => false, null, null, false, busy856, out var dRefused);
        Check(refused.All(p => p.NewPeak <= p.OldPeak * 0.99) && !refused.Any(p => p.Source == "node-854") && dRefused.PeakDropSkipped > 0,
            $"r143's unchanged-peak pin is refused: {PaceMoves(refused)}, skipped {dRefused.PeakDropSkipped}");
        Check(dRefused.WorstDeviation == dOpen.WorstDeviation && dRefused.Explain == dOpen.Explain, "a guarded pass keeps its diagnostics");
        // (b) A move that lowers the peak passes unchanged: with node-856 free, its 110976 component leaves the peak node, and
        // the guarded pass pins exactly that plan; every guarded plan lowers the peak by the margin.
        RebalancePlan[] free0 = ChooseRebalancePlans(r143, live, [], _ => false, null, null, false, null, out _);
        RebalancePlan[] free1 = ChooseRebalancePlans(r143, guard, [], _ => false, null, null, false, null, out var dFree1);
        RebalancePlan off856 = free0.SingleOrDefault(p => p.Source == "node-856");
        Check(off856.Members != null && off856.NewPeak < off856.OldPeak * 0.99, $"r143 with node-856 free moves its component and lowers the peak: {PaceMoves(free0)}");
        Check(free1.Length != 0 && free1.All(p => p.NewPeak <= p.OldPeak * 0.99)
            && free1.Any(p => p.Source == "node-856" && p.Target == off856.Target && p.Members.SequenceEqual(off856.Members)),
            $"a peak-lowering move passes the guard: {PaceMoves(free1)} vs {PaceMoves(free0)}");
        // The margin is a margin: node-856's move lowers the peak 596114 -> 552030 (7.4 %), so at 0.2 it is refused too.
        RebalancePlan[] wide = ChooseRebalancePlans(r143, live with { BalanceMinPeakDrop = 0.2 }, [], _ => false, null, null, false, null, out var dWide);
        Check(!wide.Any(p => p.Source == "node-856") && wide.All(p => p.NewPeak <= p.OldPeak * 0.8) && dWide.PeakDropSkipped > 0,
            $"a drop inside the margin is refused: {PaceMoves(wide)}");
        // (c) The client tier too: the same mover carrying three clients (node-854 then holds all of them, above the even split,
        // so the client dimension wants it moved) is a client plan at an unchanged peak with the guard off, and refused with it.
        HashSet<ulong> routed = [901, 902, 903];
        var clientShape = PacePeakDropShape(1.0, [901, 902, 903]);
        RebalancePlan[] clientOpen = ChooseRebalancePlans(clientShape, live, routed, _ => false, null, null, false, busy856, out _);
        if (!clientOpen.Any(p => p.Kind == RebalanceKind.Client && p.OldPeak == p.NewPeak))
            throw new InvalidOperationException($"balance-peak-drop: VOID - no client plan at an unchanged peak: {PaceMoves(clientOpen)}");
        RebalancePlan[] clientGuarded = ChooseRebalancePlans(clientShape, guard, routed, _ => false, null, null, false, busy856, out var dClient);
        Check(!clientGuarded.Any(p => p.Kind == RebalanceKind.Client) && dClient.PeakDropSkipped > 0,
            $"a client move at an unchanged peak is refused: {PaceMoves(clientGuarded)}");
        // (d) With the floor: below it the floor holds first (the guard is never reached); above it the guard decides.
        CostModel both = guard with { BalanceMinPeakLoad = 800_000 };
        ChooseRebalancePlans(r143, both, [], _ => false, null, null, false, busy856, out var dBelow);
        Check(dBelow.FloorHeldPeak == 596_114 && dBelow.PeakDropSkipped == 0, $"below the floor the floor holds: {dBelow.FloorHeldPeak}, {dBelow.PeakDropSkipped}");
        var r143Busy = PacePeakDropShape(1.5);
        RebalancePlan[] aboveOpen = ChooseRebalancePlans(r143Busy, live with { BalanceMinPeakLoad = 800_000 }, [], _ => false, null, null, false, busy856, out _);
        RebalancePlan[] aboveBoth = ChooseRebalancePlans(r143Busy, both, [], _ => false, null, null, false, busy856, out var dAbove);
        Check(aboveOpen.Any(p => p.OldPeak == p.NewPeak) && !aboveBoth.Any(p => p.NewPeak > p.OldPeak * 0.99) && dAbove.PeakDropSkipped > 0
            && dAbove.FloorHeldPeak == 0, $"above the floor the guard refuses the unchanged-peak pin: {PaceMoves(aboveBoth)} vs floor only {PaceMoves(aboveOpen)}");
        // Co-location never sees the guard.
        static PartitionInfo Near(ulong id, double x, int members, long simLoad) =>
            new(id, members, true, new PartitionSphere(x, 0, 0, 100), 0, [], 1000, 400L * 1000 + 8_000L * members + 2_000, simLoad);
        var source = new PartitionReport(3000, 1000, [Near(31, 0, 1, 20_000)], 0, 50, SimLoadSupported: true);
        var target = new PartitionReport(3000, 1000, [Near(32, 1500, 40, 90_000)], 0, 50, SimLoadSupported: true);
        Move? plain = ChooseMove(source, target, 4000, [], _ => false, live, loadSource: 300_000, loadTarget: 200_000);
        Move? guarded2 = ChooseMove(source, target, 4000, [], _ => false, both, loadSource: 300_000, loadTarget: 200_000);
        Check(plain is { Reason: "co-location" } && guarded2 == plain, $"co-location ignores the guard: {plain} vs {guarded2}");
        Console.WriteLine($"balance-peak-drop: default and PCU/full-plan identical to the df planner; r143's unchanged-peak pin {PaceMoves(open)}"
            + $" refused at 0.01 ({dRefused.PeakDropSkipped} skipped); the peak-lowering {PaceMoves(free1)} passes; the client tier is guarded;"
            + " the floor holds first below it; co-location unchanged");
    }
}
