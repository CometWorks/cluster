using static ClusterRegistry.DirectGateway;

namespace ClusterRegistry;

/// <summary>
/// #1454: the heavy-component scenarios, one section per corner case of the ticket. The paced planner runs pass after pass
/// on a small simulated cluster (HeavyRun): plans land at once, the heavy latch carries over, a pass is 60 s of cool-down
/// clock. Every behaviour is checked against its control (the same model with HeavyClients 0), which must show the old
/// behaviour first, or the section is VOID.
/// </summary>
internal static partial class SelfTest
{
    private static PartitionInfo HeavyAt(ulong id, double x, long simLoad, int clients = 0, int members = 1, int pcu = 1000,
        double speed = 0) =>
        new(id, members, true, new PartitionSphere(x, 0, 0, 100), speed,
            [.. Enumerable.Range(0, clients).Select(i => id * 1000 + (ulong)i)], pcu, 400L * pcu + 8_000L * members + 2_000, simLoad);

    private static (PartitionReport Report, string Node) HeavyNode(string name, params PartitionInfo[] parts) =>
        (new PartitionReport(3000, 1000, parts, 0, 50, SimLoadSupported: true), name);

    /// <summary>The deployed model, and the same model without heavy components (the control).</summary>
    private static (CostModel Heavy, CostModel Control) HeavyModels() =>
        (CostModel.Deployed(), CostModel.Deployed() with { HeavyClients = 0 });

    private sealed record HeavyRunResult(List<RebalancePlan> Plans, (PartitionReport Report, string Node)[] Final, int HeavyMoves,
        int ClientsMoved, IReadOnlySet<ulong> Heavy)
    {
        public int Moves => Plans.Count;
        public string Log => Plans.Count == 0 ? "nothing" : string.Join("; ", Plans.Select(p =>
            $"[{string.Join(",", p.Members.Select(m => m.Id))}] {p.Source}->{p.Target} {p.Kind}"));
        public string NodeOf(ulong id) => Final.First(n => n.Report.Partitions.Any(p => p.Id == id)).Node;
    }

    /// <summary>
    /// <paramref name="passes"/> paced planner passes: the heavy latch (HeavyMembers) and the cool-down carry over, every plan
    /// lands before the next pass, and <paramref name="physics"/> rewrites the reports before each pass (a node's sim speed
    /// following the hub). <paramref name="movedAt"/> seeds the cool-down clock (seconds; the run starts at 0).
    /// </summary>
    private static HeavyRunResult HeavyRun((PartitionReport Report, string Node)[] start, CostModel model, int passes,
        Func<(PartitionReport Report, string Node)[], (PartitionReport Report, string Node)[]>? physics = null,
        Dictionary<ulong, (double At, int Clients)>? movedAt = null)
    {
        var nodes = start.Select(n => (n.Report, n.Node)).ToArray();
        IReadOnlySet<ulong> latched = new HashSet<ulong>();
        Dictionary<ulong, (double At, int Clients)> moved = movedAt != null ? new(movedAt) : [];
        List<RebalancePlan> log = [];
        int heavyMoves = 0, clientsMoved = 0;
        double now = 0;
        for (int pass = 0; pass < passes; pass++, now += 60)
        {
            if (physics != null) nodes = physics(nodes);
            latched = HeavyMembers(nodes, model, [], latched);
            IReadOnlySet<ulong> heavy = latched;
            bool Cooling(ulong id) => moved.TryGetValue(id, out var m) && now - m.At < m.Clients * model.HeavyCooldownSecondsPerClient;
            RebalancePlan[] plans = ChooseRebalancePlans(nodes, model, [], _ => false, null, null, false, null, out _, null, null,
                heavy, Cooling);
            foreach (RebalancePlan plan in plans)
            {
                HashSet<ulong> ids = [.. plan.Members.Select(m => m.Id)];
                int clients = plan.Members.Sum(m => m.Clients.Length);
                PartitionInfo[] carried = [.. nodes.First(n => n.Node == plan.Source).Report.Partitions.Where(p => ids.Contains(p.Id))];
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (nodes[n].Node == plan.Source)
                        nodes[n].Report = nodes[n].Report with { Partitions = [.. nodes[n].Report.Partitions.Where(p => !ids.Contains(p.Id))] };
                    else if (nodes[n].Node == plan.Target)
                        nodes[n].Report = nodes[n].Report with { Partitions = [.. nodes[n].Report.Partitions, .. carried] };
                }
                log.Add(plan);
                clientsMoved += clients;
                if (ids.Any(heavy.Contains))
                {
                    heavyMoves++;
                    foreach (ulong id in ids) moved[id] = (now, clients);
                }
            }
        }
        return new HeavyRunResult(log, nodes, heavyMoves, clientsMoved, latched);
    }

    /// <summary>
    /// The #1191 run 2 hub (gateway log 2026-10-22 22:24:30, octahedron-1191e): partition 2666, 60 clients, 93 grids, ~400k
    /// sim-us/s on local-node-4 next to a few light partitions, the other nodes at 40-180k. The hub node runs at sim 0.97,
    /// so it reads ~1.03M sim-us/s (EffectiveNodeLoad) - and the slowness goes wherever the hub goes (FollowHub).
    /// </summary>
    private static (PartitionReport Report, string Node)[] HeavyHubShape() =>
    [
        HeavyNode("local-node-0", HeavyAt(101, 100_000, 20_000), HeavyAt(102, 110_000, 22_000, 1)),
        HeavyNode("local-node-1", HeavyAt(111, 200_000, 30_000), HeavyAt(112, 210_000, 26_000)),
        HeavyNode("local-node-2", HeavyAt(121, 300_000, 90_000, 0, 40, 30_000), HeavyAt(122, 310_000, 94_000, 0, 40, 30_000)),
        HeavyNode("local-node-3", HeavyAt(131, 400_000, 30_000), HeavyAt(132, 410_000, 27_000, 1)),
        HeavyNode("local-node-4", HeavyAt(2666, 0, 400_000, 60, 93, 120_000), HeavyAt(141, 20_000, 30_000, 0, 10, 8_000),
            HeavyAt(142, 30_000, 25_000, 0, 10, 6_000), HeavyAt(143, 40_000, 12_000, 1)),
        HeavyNode("local-node-5", HeavyAt(151, 500_000, 30_000), HeavyAt(152, 510_000, 27_000)),
    ];

    private static (PartitionReport Report, string Node)[] FollowHub((PartitionReport Report, string Node)[] nodes) =>
        [.. nodes.Select(n => (n.Report with { SimulationRatio = n.Report.Partitions.Any(p => p.Id == 2666) ? 0.97 : 1.0 }, n.Node))];

    /// <summary>Control: with HeavyClients 0 (and on every pace fixture, where nothing carries 30 clients) the planner given
    /// a heavy set computed by HeavyMembers plans byte-identically to the planner given none.</summary>
    private static void TestBalanceHeavyControl()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-control: " + what); }
        Check(new CostModel().HeavyClients == 0 && CostModel.Deployed().HeavyClients == 30 && CostModel.Deployed().HeavyReleaseClients == 20,
            "heavy components are off in the record and 30/20 deployed");
        foreach (CostModel model in new[] { new CostModel(), PaceOverride("HeavyClients", "30") })
            foreach (var f in PaceFixtures())
            {
                IReadOnlySet<ulong> heavy = HeavyMembers(f.Nodes, model, f.Routed, new HashSet<ulong>());
                Check(heavy.Count == 0, $"{f.Name}: no pace fixture carries a heavy component");
                RebalancePlan[] reference = ChooseRebalancePlans(f.Nodes, new CostModel(), f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d0);
                RebalancePlan[] knob = ChooseRebalancePlans(f.Nodes, model, f.Routed, _ => false, null, null, f.FullPlan, f.Busy, out var d1,
                    null, null, heavy, _ => true);
                Check(PlanSignature(knob, d1) == PlanSignature(reference, d0), $"{f.Name}: plans differ with an empty heavy set");
            }
        // The hub shape with the control model: no heavy set at all, whatever the hub carries.
        Check(HeavyMembers(HeavyHubShape(), HeavyModels().Control, [], new HashSet<ulong>()).Count == 0, "HeavyClients 0 latches nothing");
        Console.WriteLine("balance-heavy-control: HeavyClients 0, and 30 on every pace fixture, plan byte-identically to the planner without heavy components");
    }

    /// <summary>Cases 1 and 7: the #1191 hub is pinned, its node is evacuated, nothing ping-pongs.</summary>
    private static void TestBalanceHeavyPin()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-pin: " + what); }
        (CostModel heavyModel, CostModel control) = HeavyModels();
        const int Passes = 60;   // an hour of 60 s paced passes
        HeavyRunResult before = HeavyRun(HeavyHubShape(), control, Passes, FollowHub);
        int hubMoves = before.Plans.Count(p => p.Members.Any(m => m.Id == 2666));
        if (hubMoves < 3)
            throw new InvalidOperationException($"balance-heavy-pin: VOID - the control does not carry the hub around ({hubMoves} moves): {before.Log}");
        HeavyRunResult after = HeavyRun(HeavyHubShape(), heavyModel, Passes, FollowHub);
        Check(after.HeavyMoves == 0 && after.NodeOf(2666) == "local-node-4", $"the hub never moves: {after.Log}");
        Check(after.Final.First(n => n.Node == "local-node-4").Report.Partitions.Select(p => p.Id).SequenceEqual([2666UL]),
            $"its node is evacuated to the hub alone: {after.Log}");
        Check(after.Plans.Where(p => p.Source == "local-node-4").All(p => p.Kind == RebalanceKind.Evacuate)
            && after.Plans.Count(p => p.Kind == RebalanceKind.Evacuate) == 3, $"three evacuations, nothing else off the hub node: {after.Log}");
        Check(after.Plans.All(p => p.Target != "local-node-4"), $"nothing targets the hub node: {after.Log}");
        // No ping-pong: no partition moves twice, so no partition returns to a node it left.
        Check(after.Plans.GroupBy(p => p.Members[0].Id).All(g => g.Count() == 1), $"no partition moves twice: {after.Log}");
        Check(after.ClientsMoved < before.ClientsMoved, $"fewer clients pay a migration: {after.ClientsMoved} vs {before.ClientsMoved}");
        Console.WriteLine($"balance-heavy-pin: #1191 hub over {Passes} passes: control moved it {hubMoves}x ({before.ClientsMoved} client"
            + $" migrations), heavy pins it on local-node-4 and evacuates its 3 neighbours ({after.ClientsMoved} client migrations): {after.Log}");
    }

    /// <summary>Case 2: two heavy components on one node split once, the one with fewer clients moving, then stay.</summary>
    private static void TestBalanceHeavySplit()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-split: " + what); }
        (CostModel heavyModel, CostModel control) = HeavyModels();
        (PartitionReport Report, string Node)[] Shape() =>
        [
            HeavyNode("n0", HeavyAt(1, 0, 300_000, 50, 60, 80_000), HeavyAt(2, 50_000, 250_000, 35, 50, 60_000), HeavyAt(3, 100_000, 20_000)),
            HeavyNode("n1", HeavyAt(11, 200_000, 120_000), HeavyAt(12, 210_000, 100_000)),
            HeavyNode("n2", HeavyAt(21, 300_000, 60_000)),
            HeavyNode("n3"),
        ];
        HeavyRunResult after = HeavyRun(Shape(), heavyModel, 30);
        RebalancePlan[] heavyPlans = [.. after.Plans.Where(p => p.Members.Any(m => m.Id is 1 or 2))];
        Check(heavyPlans.Length == 1 && heavyPlans[0].Kind == RebalanceKind.HeavySplit && heavyPlans[0].Members.Single().Id == 2
            && heavyPlans[0].Target == "n3", $"one split: the 35-client component to the empty node: {after.Log}");
        Check(after.NodeOf(1) == "n0" && after.NodeOf(2) == "n3", $"both pinned where the split left them: {after.Log}");
        Check(after.NodeOf(3) != "n0" && after.Plans.All(p => p.Target is not ("n0" or "n3") || p.Kind == RebalanceKind.HeavySplit),
            $"the light component leaves n0 and nothing joins a heavy node: {after.Log}");
        // A heavy component cooling down from its own move stays; the other one moves instead (corner case 7: no chain).
        Dictionary<ulong, (double At, int Clients)> justArrived = new() { [2] = (0, 35) };
        HeavyRunResult cooling = HeavyRun(Shape(), heavyModel, 1, null, justArrived);
        Check(cooling.Plans.Any(p => p.Kind == RebalanceKind.HeavySplit && p.Members.Single().Id == 1) && cooling.Plans.All(p => p.Members.All(m => m.Id != 2)),
            $"the cooling component stays, the other one splits off: {cooling.Log}");
        // The control has no split: the optimizer may move either heavy component again on any pass.
        HeavyRunResult before = HeavyRun(Shape(), control, 30);
        Console.WriteLine($"balance-heavy-split: {after.Log}; a cooling component stays ({cooling.Log}); control: {before.HeavyMoves} heavy moves ({before.Log})");
    }

    /// <summary>Case 3: the latch holds a heavy component down to HeavyReleaseClients, then normal balancing resumes.</summary>
    private static void TestBalanceHeavyRelease()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-release: " + what); }
        (CostModel heavyModel, CostModel control) = HeavyModels();
        (PartitionReport Report, string Node)[] Shape(int clients) =>
        [
            HeavyNode("n0", HeavyAt(1, 0, 700_000, clients, 60, 80_000), HeavyAt(2, 50_000, 200_000)),
            HeavyNode("n1", HeavyAt(11, 200_000, 50_000)),
            HeavyNode("n2", HeavyAt(21, 300_000, 40_000)),
        ];
        HashSet<ulong> none = [], latched = [1];
        Check(HeavyMembers(Shape(30), heavyModel, [], none).Contains(1) && !HeavyMembers(Shape(29), heavyModel, [], none).Contains(1),
            "heavy at 30 clients");
        Check(HeavyMembers(Shape(20), heavyModel, [], latched).Contains(1) && !HeavyMembers(Shape(19), heavyModel, [], latched).Contains(1),
            "latched, it stays heavy down to 20 and is released at 19");
        Check(!HeavyMembers(Shape(25), heavyModel, [], none).Contains(1), "not latched, 25 clients is not heavy");
        Check(HeavyMembers(Shape(25), heavyModel with { HeavyReleaseClients = 0 }, [], latched).Count == 0, "release 0 = no hysteresis");
        // Shrinking 40 -> 25 -> 15 over passes: pinned while latched, then the planner is the control's again.
        var shrink = new Queue<int>([40, 40, 25, 25, 15]);
        (PartitionReport Report, string Node)[] Shrink((PartitionReport Report, string Node)[] nodes)
        {
            int clients = shrink.Dequeue();
            return [.. nodes.Select(n => (n.Report with { Partitions = [.. n.Report.Partitions.Select(p => p.Id == 1
                ? p with { Clients = [.. Enumerable.Range(0, clients).Select(i => 1000UL + (ulong)i)] } : p)] }, n.Node))];
        }
        HeavyRunResult run = HeavyRun(Shape(40), heavyModel, 4, Shrink);
        Check(run.Heavy.Contains(1) && run.Plans.All(p => p.Members.All(m => m.Id != 1)), $"pinned at 40 and still at 25: {run.Log}");
        // Its neighbour blocked, so the component itself is the only mover.
        static bool Blocked(ulong id) => id == 2;
        RebalancePlan[] released = ChooseRebalancePlans(Shape(15), heavyModel, [], Blocked, null, null, false, null, out var dReleased,
            null, null, HeavyMembers(Shape(15), heavyModel, [], latched), _ => false);
        RebalancePlan[] reference = ChooseRebalancePlans(Shape(15), control, [], Blocked, null, null, false, null, out var dReference);
        RebalancePlan[] held = ChooseRebalancePlans(Shape(25), heavyModel, [], Blocked, null, null, false, null, out _,
            null, null, HeavyMembers(Shape(25), heavyModel, [], latched), _ => false);
        Check(held.All(p => p.Members.All(m => m.Id != 1)), "latched at 25 it is not moved: " + PaceMoves(held));
        if (!reference.Any(p => p.Members.Any(m => m.Id == 1)))
            throw new InvalidOperationException("balance-heavy-release: VOID - the control does not move the shrunk component: " + PaceMoves(reference));
        Check(PlanSignature(released, dReleased) == PlanSignature(reference, dReference), $"released, it balances like the control: {PaceMoves(released)}");
        Console.WriteLine($"balance-heavy-release: heavy at 30, held to 20, released at 19; released, it moves like any other: {PaceMoves(released)}");
    }

    /// <summary>Case 4: a heavy component on a node with headroom: its neighbours leave before the node saturates, where
    /// the control idles (the load floor and the tolerance both say nothing is wrong).</summary>
    private static void TestBalanceHeavyEarlyEvacuation()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-early: " + what); }
        (CostModel heavyModel, CostModel control) = HeavyModels();
        (PartitionReport Report, string Node)[] shape =
        [
            HeavyNode("n0", HeavyAt(1, 0, 200_000, 32, 40, 50_000), HeavyAt(2, 50_000, 60_000), HeavyAt(3, 60_000, 50_000, 2)),
            HeavyNode("n1", HeavyAt(11, 200_000, 150_000), HeavyAt(12, 210_000, 140_000)),
            HeavyNode("n2", HeavyAt(21, 300_000, 160_000), HeavyAt(22, 310_000, 120_000)),
        ];
        RebalancePlan[] idle = ChooseRebalancePlans(shape, control, [], _ => false, null, null, false, null, out var dIdle);
        if (idle.Length != 0)
            throw new InvalidOperationException("balance-heavy-early: VOID - the control already moves something: " + PaceMoves(idle));
        HeavyRunResult run = HeavyRun(shape, heavyModel, 5);
        Check(run.Plans.Count(p => p.Kind == RebalanceKind.Evacuate) == 2 && run.HeavyMoves == 0
            && run.Final.First(n => n.Node == "n0").Report.Partitions.Single().Id == 1, $"both neighbours leave, the heavy component stays: {run.Log}");
        Check(run.Plans.First().Members.Single().Id == 2, $"the bigger neighbour leaves first: {run.Log}");
        Console.WriteLine($"balance-heavy-early: control idle (peak {shape.Max(n => n.Report.Partitions.Sum(p => p.SimLoadUs))} under the floor,"
            + $" excess {dIdle.Excess:F2}); heavy evacuates: {run.Log}");
    }

    /// <summary>Case 5: co-location with a heavy component: the heavy side is home; a merge that makes one keeps the bigger crew.</summary>
    private static void TestBalanceHeavyMerge()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-merge: " + what); }
        (CostModel heavyModel, CostModel control) = HeavyModels();
        // A 30-client crowd standing in space (little content) next to a big station: §7.5 prices the station higher, so the
        // control moves the crowd - every one of its 30 clients - to the station's node.
        var crowd = new PartitionReport(3000, 1000, [HeavyAt(1, 0, 150_000, 30, 30, 100)], 0, 50, SimLoadSupported: true);
        var station = new PartitionReport(3000, 1000, [HeavyAt(2, 2_000, 80_000, 0, 400, 200_000)], 0, 50, SimLoadSupported: true);
        Move? plain = ChooseMove(crowd, station, 4000, [], _ => false, control, loadSource: 600_000, loadTarget: 300_000);
        if (plain is not { Moving.Id: 1 })
            throw new InvalidOperationException($"balance-heavy-merge: VOID - the control does not move the crowd: {plain}");
        Move? pinned = ChooseMove(crowd, station, 4000, [], _ => false, heavyModel, loadSource: 600_000, loadTarget: 300_000,
            heavy: new HashSet<ulong> { 1 });
        Check(pinned is { Moving.Id: 2, ToTarget: false, Reason: "co-location" } && pinned.Value.Why.Contains("heavy"),
            $"the station comes to the heavy crowd: {pinned}");
        // Two 20-client groups merging into a 40-client (heavy) component, prices within the tie band: the control makes the
        // lighter node home and moves the group on the busy one; heavy keeps the bigger crew in place.
        var busy = new PartitionReport(3000, 1000, [HeavyAt(3, 0, 100_000, 21, 20, 2_000)], 0, 50, SimLoadSupported: true);
        var quiet = new PartitionReport(3000, 1000, [HeavyAt(4, 2_000, 100_000, 20, 20, 2_000)], 0, 50, SimLoadSupported: true);
        Move? tie = ChooseMove(busy, quiet, 4000, [], _ => false, control, loadSource: 900_000, loadTarget: 300_000);
        if (tie is not { Moving.Id: 3 })
            throw new InvalidOperationException($"balance-heavy-merge: VOID - the control does not move the 21-client group to the quiet node: {tie}");
        Move? merged = ChooseMove(busy, quiet, 4000, [], _ => false, heavyModel, loadSource: 900_000, loadTarget: 300_000, heavy: new HashSet<ulong>());
        Check(merged is { Moving.Id: 4 }, $"the 20-client group joins the 21: {merged}");
        // Equal crews: the lighter node keeps them (then its neighbours are evacuated - the cheap side of the cost model).
        var even = new PartitionReport(3000, 1000, [HeavyAt(3, 0, 100_000, 20, 20, 2_000)], 0, 50, SimLoadSupported: true);
        Move? equal = ChooseMove(even, quiet, 4000, [], _ => false, heavyModel, loadSource: 900_000, loadTarget: 300_000, heavy: new HashSet<ulong>());
        Check(equal is { Moving.Id: 3 }, $"equal crews: the lighter node is home: {equal}");
        // Below the threshold together, the price decides exactly as before.
        var small = new PartitionReport(3000, 1000, [HeavyAt(3, 0, 100_000, 5, 20, 2_000)], 0, 50, SimLoadSupported: true);
        Check(ChooseMove(small, quiet, 4000, [], _ => false, heavyModel, loadSource: 900_000, loadTarget: 300_000, heavy: new HashSet<ulong>())
            == ChooseMove(small, quiet, 4000, [], _ => false, heavyModel with { HeavyClients = 0 }, loadSource: 900_000, loadTarget: 300_000),
            "a merge below the threshold is priced as before");
        Console.WriteLine($"balance-heavy-merge: control moves the crowd ({plain.Value.Moving.Id}); heavy moves the station"
            + $" ({pinned!.Value.Moving.Id}, {pinned.Value.Why}); a heavy merge keeps the bigger crew ({merged!.Value.Moving.Id} moves)");
    }

    /// <summary>SE-1415: a recycled node's crews spread over the receivers by client count instead of moving whole onto the
    /// spare (octahedron-test 2026-11-13: 73 players in 7 station partitions on 3 of 6 nodes, carried node to spare every
    /// 20 min while the load floor held the balancer). Red before SE-1415: every component goes to the spare.</summary>
    private static void TestRotationClientSpread()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("rotation-client-spread: " + what); }
        // The 05:26:15 CET recycle: node-0 drains stations with 9, 9 and 3 clients; node-5 and node-6 serve 28 and 24,
        // node-2..4 none, spare node-1 none. Stations are 50 km apart: no envelope neighbours.
        Dictionary<string, double> projected = new() { ["n1"] = 0, ["n2"] = 20_000, ["n3"] = 30_000, ["n4"] = 10_000, ["n5"] = 300_000, ["n6"] = 250_000 };
        Dictionary<string, int> crew = new() { ["n1"] = 0, ["n2"] = 0, ["n3"] = 0, ["n4"] = 0, ["n5"] = 28, ["n6"] = 24 };
        (string, PartitionReport)[] receivers = [.. projected.Keys.Select((k, i) => (k, new PartitionReport(3000, 1000,
            k == "n1" ? [] : [HeavyAt(200 + (ulong)i, 1_000_000 + i * 50_000, 10_000, crew[k])], 0, 50, SimLoadSupported: true)))];
        PartitionInfo[] stations = [HeavyAt(301, -1_000_000, 90_000, 9), HeavyAt(302, -2_000_000, 80_000, 9), HeavyAt(303, -3_000_000, 30_000, 3)];
        string[] Drain(Dictionary<string, int>? clients, string? avoid)
        {
            Dictionary<string, int>? projectedClients = clients == null ? null : new(clients);
            List<string> targets = [];
            foreach (PartitionInfo station in stations)
            {
                string target = DrainTarget([station], receivers, 4000, projected, "n1", null, station.Clients.Length, projectedClients, avoid);
                if (projectedClients != null) projectedClients[target] += station.Clients.Length;
                targets.Add(target);
            }
            return [.. targets];
        }
        string[] before = Drain(null, null);
        Check(before.All(x => x == "n1"), $"control: without client counts the spare takes all ({string.Join(",", before)})");
        string[] after = Drain(crew, null);
        Check(after.SequenceEqual(["n1", "n4", "n2"]), $"the crews go to three crew-less nodes, the spare first ({string.Join(",", after)})");
        string[] skipNext = Drain(crew, "n4");
        Check(skipNext.SequenceEqual(["n1", "n2", "n3"]), $"never the node that rotates next ({string.Join(",", skipNext)})");
        // An empty component (no clients) still follows the spare, and a neighbour still wins over the spread.
        Check(DrainTarget([HeavyAt(304, -4_000_000, 1_000)], receivers, 4000, projected, "n1", null, 0, crew) == "n1", "no clients: the spare");
        Check(DrainTarget([HeavyAt(305, 1_000_000 + 4 * 50_000 + 150, 1_000, 5)], receivers, 4000, projected, "n1", null, 5, crew) == "n5",
            "a crew next to a partition of n5 joins it");
        Check(DrainTarget([HeavyAt(306, -5_000_000, 1_000, 5)], receivers, 4000, projected, "n1", new HashSet<string> { "n1", "n2", "n3", "n4" }, 5, crew) == "n6",
            "heavy-holding receivers stay closed");
        Console.WriteLine($"rotation-client-spread: before {string.Join(",", before)}; after {string.Join(",", after)}; skipping the next {string.Join(",", skipNext)}");
    }

    /// <summary>Case 6: drains move a heavy component once, to the emptiest node without one; light components keep off it;
    /// once landed, the balancer does not move it on (no chain).</summary>
    private static void TestBalanceHeavyDrain()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-drain: " + what); }
        Dictionary<string, double> projected = new() { ["r1"] = 400_000, ["r2"] = 100_000, ["r3"] = 50_000, ["r4"] = 0 };
        HashSet<string> heavyOnR1 = ["r1"];
        Check(HeavyDrainTarget(projected.Select(x => (x.Key, x.Value)), heavyOnR1, "r4") == "r4", "a recycled node's replacement takes the heavy component");
        Check(HeavyDrainTarget(projected.Select(x => (x.Key, x.Value)), heavyOnR1) == "r4", "else the emptiest node");
        Check(HeavyDrainTarget(projected.Where(x => x.Key != "r4").Select(x => (x.Key, x.Value)), heavyOnR1) == "r3",
            "the emptiest node without a heavy component, never the lightest heavy one");
        Check(HeavyDrainTarget([("r1", 0.0)], heavyOnR1) == null, "every receiver heavy: the usual rule applies");
        // Light components keep off the nodes holding or taking a heavy component, unless a neighbour is there.
        (string, PartitionReport)[] receivers = [.. projected.Keys.Select(k => (k, new PartitionReport(3000, 1000,
            k == "r2" ? [HeavyAt(50, 100_000, 1_000)] : [], 0, 50, SimLoadSupported: true)))];
        HashSet<string> taken = ["r1", "r4"];
        Check(DrainTarget([HeavyAt(60, 500_000, 1_000)], receivers, 4000, projected, "r4", taken) == "r3",
            "a light component skips the replacement that took the heavy one and the heavy node");
        Check(DrainTarget([HeavyAt(61, 101_000, 1_000)], receivers, 4000, projected, "r4", taken) == "r2", "a neighbour still wins");
        Check(DrainTarget([HeavyAt(62, 500_000, 1_000)], receivers, 4000, projected, "r4") == "r4", "no heavy component: unchanged");
        // After the drain the hub sits on its new node: the paced balancer leaves it there for an hour (it does not chain).
        (PartitionReport Report, string Node)[] landed = [.. HeavyHubShape().Select(n => n.Node switch
        {
            "local-node-4" => (n.Report with { Partitions = [.. n.Report.Partitions.Where(p => p.Id != 2666)] }, n.Node),
            "local-node-1" => (n.Report with { Partitions = [.. n.Report.Partitions, .. HeavyHubShape()[4].Report.Partitions.Where(p => p.Id == 2666)] }, n.Node),
            _ => n,
        })];
        HeavyRunResult run = HeavyRun(landed, HeavyModels().Heavy, 60, FollowHub);
        Check(run.HeavyMoves == 0 && run.NodeOf(2666) == "local-node-1", $"the drained hub stays: {run.Log}");
        Console.WriteLine($"balance-heavy-drain: heavy drains to the replacement / emptiest node without one; light ones keep off it; after landing: {run.Log}");
    }

    /// <summary>Case 8: an empty node is the heavy component's dedicated node when evacuating its neighbours would move more
    /// clients, and the evacuation target otherwise.</summary>
    private static void TestBalanceHeavySpare()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-heavy-spare: " + what); }
        CostModel heavyModel = HeavyModels().Heavy;
        (PartitionReport Report, string Node)[] Shape(int pilotsPerShip, bool empty) =>
        [
            HeavyNode("n0", [HeavyAt(1, 0, 300_000, 30, 60, 80_000), .. Enumerable.Range(0, 8).Select(i =>
                HeavyAt(10 + (ulong)i, 100_000 + 20_000 * i, 15_000, pilotsPerShip, 3, 2_000))]),
            HeavyNode("n1", HeavyAt(21, 400_000, 200_000)),
            empty ? HeavyNode("n2") : HeavyNode("n2", HeavyAt(31, 500_000, 180_000)),
        ];
        // 8 ships x 5 pilots = 40 clients against the hub's 30: the hub moves, once.
        HeavyRunResult dedicate = HeavyRun(Shape(5, empty: true), heavyModel, 30);
        Check(dedicate.Plans.Count(p => p.Kind == RebalanceKind.HeavyDedicate) == 1 && dedicate.NodeOf(1) == "n2" && dedicate.HeavyMoves == 1
            && dedicate.Plans.All(p => p.Kind == RebalanceKind.HeavyDedicate), $"the hub takes the empty node, the ships stay: {dedicate.Log}");
        // 8 ships x 1 pilot = 8 clients: they leave, to the empty node first.
        HeavyRunResult evacuate = HeavyRun(Shape(1, empty: true), heavyModel, 30);
        Check(evacuate.HeavyMoves == 0 && evacuate.Plans.All(p => p.Kind == RebalanceKind.Evacuate) && evacuate.Plans.Count == 8
            && evacuate.Plans[0].Target == "n2" && evacuate.ClientsMoved == 8, $"the ships leave, the hub stays: {evacuate.Log}");
        // No empty node: the ships leave whatever they carry.
        HeavyRunResult busy = HeavyRun(Shape(5, empty: false), heavyModel, 30);
        Check(busy.HeavyMoves == 0 && busy.Plans.Count(p => p.Kind == RebalanceKind.Evacuate) == 8, $"without an empty node the hub stays: {busy.Log}");
        Console.WriteLine($"balance-heavy-spare: 40 ship clients vs 30 hub clients -> {dedicate.Log}; 8 vs 30 -> {evacuate.Plans.Count} evacuations; no empty node -> {busy.Plans.Count} evacuations");
    }
}
