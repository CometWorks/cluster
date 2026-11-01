using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// The Partitioning.md §7.5 handover cost model:
/// <c>cost = α·snapshot_bytes + β·frozen_ms + γ·client_switches</c>, with the frozen window
/// itself estimated linearly from the snapshot size. Every direction decision (component home,
/// balance mover, drain order) prices both sides through this one function, so "the big cluster
/// stays put", "prefer clientless movers" and "characters travel to content" are all the same
/// rule rather than three special cases.
/// </summary>
/// <remarks>
/// Coefficients are the P3-CST-01 calibration (run g-cst01-2, 2026-07-31; blessed baseline in
/// Test/cost-model-baseline.json), in disruption-millisecond units: the entire
/// per-byte cost is the measured frozen window over ESTIMATOR bytes
/// (PartitionManager.EstimatedSnapshotBytes), and gamma is the measured client-switch chain.
/// Recalibration is a reviewed commit copying a new P3-CST-01 <c>cost-model.json</c> artifact,
/// or an ops override via <c>--cost-model</c>. Under these values a lone character with its
/// client (~10 KB + one switch, ~350 ms) prices below any station past ~770 KB estimator bytes
/// (~1900 PCU) while a clientless crumb prices below both - §6.4's direction rule and §7.3's
/// clientless preference falling out of the arithmetic.
/// </remarks>
internal sealed record CostModel(
    double Alpha = 0.0,               // per snapshot byte, beyond the frozen window
    double BetaPerFrozenMs = 1.0,     // per estimated frozen millisecond
    double FrozenMsBase = 93.0,       // freeze floor a transfer pays regardless of size
    double FrozenMsPerByte = 0.00033, // frozen-window growth per estimator byte
    double GammaPerSwitch = 254.0,    // per client switch
    // The freeze estimate's entity term (w22/w23 measurements, 2026-08-22/24): the import commit
    // stall is driven by ENTITY COUNT, not bytes - essentially zero below ~10 entities, 8-12
    // ms/entity above ~100, flat across a 20x byte range. Prices the LAG side of a rebalance
    // decision (Docs/GlobalLoadBalancing.md); nothing is refused by it, only ordered.
    double FreezeMsPerEntity = 10.0,      // commit stall per imported entity once stalling
    int StallFloorEntities = 10,          // below this a commit essentially never stalls
    // Viktor 2026-08-24: THE single balancing tunable. The most loaded node may exceed the mean
    // PCU of the other serving nodes by this relative fraction before moving partitions is
    // warranted; below it the assignment is "good enough" and nothing moves, because the lag the
    // moves would cause does not warrant the possible simspeed gain on the most loaded node.
    double BalanceRiskTolerance = 0.25,
    // T-0141 second attempt. How hard an already-loaded node resists becoming the co-location
    // HOME, as a fraction of the pair's total move cost per unit of relative load imbalance.
    //
    // WHY IT IS A COST TERM AND NOT A VETO. The first attempt (da56622, reverted 2692d65) refused
    // co-locations into an overloaded node. Refusing leaves the two partitions closer than
    // SyncDistance on DIFFERENT nodes, which is the 3000m sphere-gap invariant, and the gateway's
    // response to that violation is to ACCELERATE co-location against it - so the guard
    // manufactured the condition that made the system fight it. Measured 0.86 -> 2.64 invariant
    // alarms/min, 3.1x. Priced INSIDE the §7.5 comparison the co-location always happens; only
    // the DIRECTION changes, so no invariant is ever left violated.
    //
    // THE SCALE. imbalance is |loadA - loadB| / max(loadA, loadB), so 0 when even and ->1 when one
    // node carries everything. The penalty is Weight * imbalance * (onSource + onTarget), i.e. a
    // fraction of what the move is already worth, which keeps it commensurable with the ms the
    // rest of the rule is denominated in without inventing a us-per-ms conversion.
    //
    // 0 DISABLES IT EXACTLY - the term is computed as zero and every decision is bit-identical to
    // §7.5 as documented. That is the default deliberately: this ships inert and is switched on
    // through the --cost-model FILE, so backing it out is an edit and a restart rather than a
    // rebuild. 3a79fe3's knobs could not back its change out and that cost us a roll.
    double LoadHomeWeight = 0.0,
    // T-0141 THIRD attempt: hysteresis on the co-location envelope.
    //
    // WHY THE FIRST TWO ATTEMPTS MOVED THE WRONG LEVER. Both changed WHICH SIDE is the home - a
    // veto (reverted) and then a load term inside the §7.5 price (refuted, 59% aborting minutes).
    // Neither touched how often co-location engages, or the fact that it never disengages.
    // Measured 2026-09-13 on stock 74b95d1: the blob node held 33 partitions that form THIRTY-TWO
    // independent components at the gateway's own 4000 m envelope, median pairwise distance 19 km,
    // max 43.9 km. That is not a co-located workload. It is the residue of co-location decisions
    // whose justification has expired and which nothing revisits.
    //
    // HOW THE RESIDUE ACCUMULATES. EnvelopeGap subtracts (speed_a + speed_b) * RelocationLeadSeconds,
    // so a ship passing at 100 m/s co-locates partitions kilometres apart on the grounds they MIGHT
    // close. Components() then uses ONE threshold for entering and for staying, so "might close"
    // becomes permanent placement whether or not it closed. Of 282 co-location moves in one hour,
    // 54.6% fired within 200 m of the 4000 m threshold and the median RAW centre gap was 4426 m -
    // already wider than the envelope. Meanwhile the balancer pulled ~70/hour off against
    // co-location's ~100/hour on, and 85% of all cluster handover traffic was that one fight.
    //
    // WHAT THIS DOES. A pair ALREADY on the same node stays linked out to the full envelope, as
    // today. A pair on DIFFERENT nodes must close to Fraction * envelope before co-location drags
    // them together. Placement is the memory, so this needs no new state and no history - the same
    // merge/split asymmetry the partitioner already uses (merge under 3000 m, split at 5000 m) and
    // that co-location conspicuously lacks.
    //
    // THE INVARIANT IS SAFE BY CONSTRUCTION as long as Fraction * envelope > SyncDistance. Two
    // partitions closer than SyncDistance on different nodes is the 3000 m sphere-gap violation
    // that killed attempt #1, and at the deployed 3000/1000 geometry a fraction of 0.875 engages
    // at 3500 m - still 500 m of margin above the invariant. DO NOT set it below
    // SyncDistance / (SyncDistance + Margin) = 0.75; at or under that line this reproduces attempt
    // #1's failure exactly, by refusing co-locations the gateway then accelerates against.
    //
    // 1.0 DISABLES IT EXACTLY - cross-node pairs get the same envelope as same-node pairs and every
    // decision is bit-identical to today. That is the default deliberately: this ships inert and is
    // switched on through the --cost-model FILE, so backing it out is an edit and a restart rather
    // than a rebuild. #1304 (2026-10-21): Deployed() runs 0.875, because the balancer now plans units at the engage
    // distance (DirectGateway.Components, units: true) and moves part of a component only through this gap.
    double ColocationEngageFraction = 1.0,
    // T-0141 (2026-09-15, the 8 km lattice world w31). The in-flight exemption (T-0074) keeps the
    // balancer from chasing a flying ship's own partition: its load leaves with the ship. On the
    // lattice a partition is a whole cluster - platform + 30 parked ships, 31 members, ~15 MB - and
    // one transiting ship makes the WHOLE cluster "in flight", so the balancer may not touch it
    // while co-location keeps dragging such clusters onto the node with the pilots. 01:00-01:10:
    // 13 of the 43 partitions on the two pile nodes were exempt this way, excess 1.02 against a
    // tolerance of 0.25, achievable 0.19, and the balancer planned 2 moves against 89 co-location
    // moves. The exemption is meant for the ship, not for the base it happens to be leaving: a
    // component with more members than this stays a placement unit even while one of them moves
    // (its 30 parked members' load does NOT leave with the ship). 0 restores the T-0074 rule
    // exactly (any moving member exempts the component).
    int InFlightMaxMembers = 4,
    // T-0385 (2026-10-02). The T-0074 client rule has the same blind spot the in-flight rule had:
    // "a component carrying a client is the pilot's ship, its load leaves with it" is true of the
    // ship and false of the base the pilots are standing on. At 152 pilots node-261 ran saturated
    // (1005018 sim-us/s) on two platform components of 121 and 107 members with 11 and 7 clients
    // (515854 + 256277 sim-us/s) next to nodes at ~0.4, holding exactly the even client split, so
    // nothing on it was movable: 44 of 79 components exempt, excess 0.74 against 0.25. A
    // client-bearing component with more members than this (a lattice cluster is a platform plus
    // 30 parked ships) is a load unit like any other; its clients' route switches stay priced in
    // its cost (GammaPerSwitch). 0 restores the T-0074 rule exactly.
    // T-0391 (2026-10-03): default 0. A live ABBA at 108 pilots on r89 (POST /balance/cost-model, 30-min arms)
    // read 5.95 % / 4.39 % of Serving 1-s samples under 0.9 at 30 against 2.72 % / 3.01 % at 0: under 30 the
    // big client-bearing platform components move as load units and their imports are the long hitches
    // (polaris targets: imports >= 1 s 12-16 vs 7-9 per half hour). 30 stays available as a live override.
    int ClientBoundMaxMembers = 0,
    // T-0141 (2026-09-15). The §7.5 home is the side that costs more to move. Two lattice
    // clusters price within a few percent of each other (5233 vs 5651 ms), so the home was in
    // practice decided by noise, and the pile node won often enough to become the pile. Within
    // this fraction of the higher price the two sides are a TIE and the LIGHTER node (by the
    // smoothed sim load the pass already reads) is the home. Unlike LoadHomeWeight it never
    // outweighs a real cost difference, so a lone ship still travels to its base. 0 disables it.
    double HomeTieFraction = 0.10,
    // T-0393 (2026-10-03): rebalancing pace control, ~/notes/T-0393-RebalancePace.md. Every handover costs its
    // TARGET ~1 bad sim-second (P(sim<0.9) 14-24 % in the 6 s after it against 1.4-1.7 % quiet), nearly
    // independent of the move's size, and the paced planner pins ~300 balance migrations an hour on top of
    // co-location. The five knobs below price, filter and pace those moves. Each default reproduces the
    // planner as it was before them exactly; all are live-tunable through POST /balance/cost-model.
    //
    // 1. A fixed per-move hitch price in estimated-lag ms, added to every paced candidate's CostMs in both score
    //    tiers: score = reduction / (1 + (CostMs + HitchMsPerMove) * (1 + clients on target)). CostMs prices the
    //    frozen window and client switches, not the target's game-thread import; ~1000 makes a pebble's tiny
    //    gain lose the ranking to a bigger one. It only reorders: it never makes a candidate unqualified.
    double HitchMsPerMove = 0,
    // 2. Minimum gain. A paced load candidate (tier 1) qualifies only if its overload reduction is at least
    //    this fraction of the MEAN NODE LOAD (sum of every node's load, unattributed overhead included, over the
    //    node count, in the pass's unit); a client candidate (tier 2) only if its client-overload reduction is at
    //    least this fraction of the MEAN CLIENTS PER NODE (routed clients over the node count). Kills moves whose
    //    gain is inside the sim-us/s measurement noise. A load candidate refused here may still qualify as a
    //    client move, exactly like one that reduces no load at all. 0 = any reduction > 0 qualifies (as before).
    double MinGainFraction = 0,
    // 3. Low water: hysteresis on the tolerance gate. The planner starts when a deviation (or the excess)
    //    exceeds BalanceRiskTolerance and, once started, keeps the gate at this low water until every node is
    //    within it; then it idles until the high water is crossed again (DirectGateway.BalancePace). A negative
    //    value means "the tolerance" (one threshold, as before); a value above the tolerance is clamped to it.
    double BalanceLowWater = -1,
    // 4. The T-0251 balance residence holds a partition that arrived less than CLUSTER_BALANCE_RESIDENCE_SECONDS
    //    (600) ago only if its estimated freeze (FreezeMs) is at least this - the former const
    //    BalanceResidenceMinFreezeMs. 0 holds every arrival (re-moves of small partitions; co-location still
    //    moves them, it is a different mechanism).
    double ResidenceMinFreezeMs = 2000,
    // 5. Move budget under a threshold: while the pass's worst deviation (max of load and client) is below
    //    BudgetUnderDeviation, a node may be the TARGET of at most BudgetMovesPerNodeHour paced balance pins in
    //    a sliding 3600 s window; a candidate whose target is over budget is skipped. At or above the threshold
    //    (a real overload) the budget does not apply. Both must be set: 0 moves = off, and with the threshold 0
    //    no deviation is below it (a worst deviation is never negative). The idle-cluster plan is never budgeted.
    int BudgetMovesPerNodeHour = 0,
    double BudgetUnderDeviation = 0,
    // T-0409 (2026-10-09): the balance load floor, in sim-us/s. The gates above are all RELATIVE (a node's load against
    // the others'), so the paced planner moves 40-120-body partitions between nodes that all have sim headroom. r142
    // (nas 32, 04:31-07:00): balance migrations carried 654 of the 947 handover-adjacent slow pilot-seconds, 102 of
    // 122 pins fired with the peak below 700k sim-us/s and 122 of 122 with a healthy source node, while the pilot lag
    // away from handovers stayed flat (0.000-0.025 %) at every node load up to >= 900k. While the pass's peak node
    // load is below this floor the PACED planner (both tiers, load and client) plans nothing; co-location, drains,
    // offline wake, the idle-cluster full plan and PCU passes are untouched, and at or above the floor every decision
    // is the planner's as before. The floor is a frame-budget share: 1e6 sim-us/s is one game-thread second per
    // second. 0 = off: every decision is bit-identical. Live-tunable through POST /balance/cost-model.
    double BalanceMinPeakLoad = 0,
    // T-0409 follow-up (2026-10-09, CH): the peak-drop guard, a fraction of the pass's peak node load. No paced plan
    // tier is required to lower the peak (see RebalanceKind), and the r143 ABBA showed what that costs: of 115 pins in
    // the OFF arms only 41 lowered the peak, and once the floor held the sub-floor moves the rest rebounded above it as
    // pins at an unchanged peak (58 of 70 ON pins). With this > 0 a PACED sim-load candidate, of either tier, qualifies
    // only if the cluster peak after it is <= (1 - BalanceMinPeakDrop) x the peak before it. The idle-cluster full
    // plan and PCU passes are untouched. 0 = off: every decision is bit-identical. Live-tunable through
    // POST /balance/cost-model.
    double BalanceMinPeakDrop = 0,
    // #1454 (2026-10-23, Viktor): heavy components. #1191 run 2: the Alpha hub, one anchored partition with 58-74
    // clients, was carried node-4 -> node-1 -> node-5 -> ... every 5-10 min (17 moves), a 36-38 s estimated migration
    // lag for every pilot each time, and the hub node's sim stayed at 0.90 wherever it went: its own slowness reads as
    // fixed node overhead (EffectiveNodeLoad), so the planner saw a slow node and moved the hub off it, and the next
    // node became the slow one. An envelope component with at least HeavyClients clients (connected, routed) is HEAVY:
    // the balancer never moves it, no balance plan targets its node, and the other components on its node are
    // evacuated (Docs/Gateway/GlobalLoadBalancing.md, "Heavy components"). It stays heavy until its clients drop below HeavyReleaseClients (hysteresis; a
    // value above HeavyClients is clamped to it, 0 = no hysteresis). Drains, failures and scale-in still move it, once, to the emptiest
    // node. 0 = off: every decision is bit-identical. Live-tunable through POST /balance/cost-model.
    int HeavyClients = 0,
    int HeavyReleaseClients = 0,
    // #1454 corner cases 2 and 8: the balancer moves a heavy component only to split two heavy components sharing a
    // node, or onto an empty node when evacuating its neighbours would move more clients. After any such move (or a
    // drain) the component is not moved again by the balancer for this many seconds per client it carries.
    double HeavyCooldownSecondsPerClient = 60)
{
    /// <summary>#1454: whether a component carrying <paramref name="clients"/> is heavy, given whether it was heavy on the
    /// previous pass (<paramref name="latched"/>): the hysteresis between <see cref="HeavyClients"/> and
    /// <see cref="HeavyReleaseClients"/>. Always false while HeavyClients is 0.</summary>
    public bool IsHeavy(int clients, bool latched) =>
        HeavyClients > 0 && clients >= (latched && HeavyReleaseClients > 0 ? Math.Min(HeavyReleaseClients, HeavyClients) : HeavyClients);

    /// <summary>T-0393: the gate threshold while balancing is active (see <see cref="BalanceLowWater"/>). A method, not a
    /// property: a property would serialize into GET /balance/cost-model as a field an override silently cannot set.</summary>
    public double EffectiveLowWater() => BalanceLowWater < 0 ? BalanceRiskTolerance : Math.Min(BalanceLowWater, BalanceRiskTolerance);

    /// <summary>The highest member speed, in m/s, the co-location and offline-wake velocity lookahead counts; a pair's
    /// closing speed is capped at twice this. Not a cost-model field: the gateway sets it from the handover config's
    /// <c>lookahead.maxSpeed</c> (DirectGateway.GapModel), so it stays out of the JSON, the file and the live overrides.
    /// 100 is the vanilla top speed; a world with a speed mod sets its own top speed.</summary>
    [JsonIgnore] public double MaxGridSpeed { get; init; } = 100;

    /// <summary>A never-reported size still prices above nothing - an empty partition's floor.</summary>
    private const long MinimumBytes = 2_000;

    public double Cost(long snapshotBytes, int clientSwitches)
    {
        long bytes = Math.Max(snapshotBytes, MinimumBytes);
        return Alpha * bytes + BetaPerFrozenMs * (FrozenMsBase + FrozenMsPerByte * bytes)
            + GammaPerSwitch * clientSwitches;
    }

    /// <summary>
    /// The estimated game-thread freeze of importing one partition: the calibrated byte term plus
    /// the measured per-entity commit stall once the partition is big enough to stall at all.
    /// Prices the lag side of a global rebalance decision. It deliberately does NOT feed
    /// <see cref="Cost"/>, whose P3-CST-01 calibration predates it and prices co-location move
    /// ORDERING.
    /// </summary>
    public double FreezeMs(long snapshotBytes, int entities)
    {
        long bytes = Math.Max(snapshotBytes, MinimumBytes);
        double freeze = FrozenMsBase + FrozenMsPerByte * bytes;
        if (entities >= StallFloorEntities) freeze += FreezeMsPerEntity * entities;
        return freeze;
    }

    /// <summary>
    /// T-0391: the cost model with some fields replaced by name (a live A/B of a balancer knob, e.g.
    /// ClientBoundMaxMembers, without a cluster restart - the model is otherwise read once from --cost-model).
    /// Names match the record's properties ignoring case; values parse as the property's JSON type. An unknown
    /// name or an unparsable value throws, and nothing is applied.
    /// </summary>
    public static CostModel WithOverrides(CostModel model, IEnumerable<KeyValuePair<string, string>> overrides)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(model))!.AsObject();
        foreach ((string name, string value) in overrides)
        {
            string? key = node.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key == null) throw new ArgumentException("Unknown cost model field " + name);
            System.Text.Json.Nodes.JsonNode? current = node[key];
            node[key] = current switch
            {
                System.Text.Json.Nodes.JsonValue v when v.TryGetValue(out bool _) => bool.Parse(value),
                System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.Number => System.Text.Json.Nodes.JsonNode.Parse(
                    double.Parse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
                        .ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                _ => throw new ArgumentException("Cost model field " + key + " is not a number or a flag"),
            };
        }
        return node.Deserialize<CostModel>() ?? throw new InvalidDataException("Empty cost model");
    }

    /// <summary>T-0393 (2026-10-04): the model a gateway runs when it is given no cost-model file - the pace-control
    /// knobs the live A/B on r91 chose (nas 32, 30/60-min arms, client sim&lt;0.9 and the target hitch share). The record's
    /// own defaults stay the pre-T-0393 planner, so <c>new CostModel()</c> remains the self-tests' reference plan.
    /// T-0409 (2026-10-09, Viktor): the load floor 800000 and the peak-drop guard 0.01 ship on, after the T-0409b ABBA on r144
    /// (notes/LagHunt-T0409-2026-10-09.md).</summary>
    public static CostModel Deployed() => new CostModel() with
    {
        HitchMsPerMove = 1000, MinGainFraction = 0.02, ResidenceMinFreezeMs = 0, BalanceLowWater = 0.15,
        BalanceMinPeakLoad = 800_000, BalanceMinPeakDrop = 0.01,
        // #1304 (2026-10-21): the balancer plans units at the engage distance, so the hysteresis is what lets it move part
        // of a lane chain; at 1.0 octahedron-1191's node-1 was 21-24-partition units of 230-290 s estimated lag. 0.875 =
        // engage at 3500 m at 3000/1000, the value this record's bound comment names; 1 reverts it live (POST /balance/cost-model).
        ColocationEngageFraction = 0.875,
        // #1454 (Viktor 2026-10-23): "a partition above a client threshold (configurable, e.g. 30) is heavy".
        HeavyClients = 30, HeavyReleaseClients = 20,
    };

    public static CostModel Load(string? path)
    {
        if (path == null || !File.Exists(path)) return Deployed();
        CostModel model = JsonSerializer.Deserialize<CostModel>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty cost model at " + path);
        Console.WriteLine($"Cost model loaded from {path}: {JsonSerializer.Serialize(model)}");
        return model;
    }
}
