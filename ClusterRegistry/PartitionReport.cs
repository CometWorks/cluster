using System.Text.Json.Serialization;

namespace ClusterRegistry;

internal sealed record PartitionReport(
    [property: JsonPropertyName("syncDistance")] double SyncDistance,
    [property: JsonPropertyName("margin")] double Margin,
    [property: JsonPropertyName("partitions")] PartitionInfo[] Partitions,
    [property: JsonPropertyName("evaluatedAt")] double EvaluatedAt,
    // Partitioning.md 7.2's load metric. NOT a percentage, despite the name and despite what this
    // comment used to say. The engine computes it as the execution time SUMMED OVER EVERY WORKER
    // THREAD divided by ONE frame budget (Sandbox/Engine/Platform/Game.cs:181, over
    // PrioritizedScheduler.ReadAndClearExecutionTime which adds up the whole worker pool), so the
    // scale runs 0 .. workers*100 and w22 has observed 487 and 700 on healthy nodes. 487 means
    // roughly 4.9 worker-threads' worth of parallel work per frame budget.
    //
    // Therefore headroom is NOT 100 - ThreadLoad; on this scale that is negative most of the time.
    // Real headroom would need the worker count, which is not reported here. Do not compute headroom
    // from this field. Compare it only against another node's value, which is what ChooseBalanceMove
    // does and all it needs. (CPULoad on the adjacent engine line IS a 0-100 percentage - wall time
    // of one frame - so the two look alike and only one of them is what its name suggests.)
    //
    // Zero from a node that predates the field, which the balancer treats as "do not know" and stays
    // idle rather than moving on bad numbers.
    [property: JsonPropertyName("threadLoad")] double ThreadLoad = 0,
    [property: JsonPropertyName("simulationRatio")] double SimulationRatio = 1,
    // CLUSTER-SIM-COST: true when this node measures per-partition simulation cost and fills
    // PartitionInfo.SimLoadUs. The balancer switches its load dimension from PCU to the measured
    // cost only when EVERY node in a pass reports true - the two units are not commensurable, so a
    // mixed fleet keeps comparing in PCU.
    [property: JsonPropertyName("simLoadSupported")] bool SimLoadSupported = false);

internal sealed record PartitionInfo(
    [property: JsonPropertyName("id")] ulong Id,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("anchored")] bool Anchored,
    [property: JsonPropertyName("sphere")] PartitionSphere Sphere,
    [property: JsonPropertyName("maxMemberSpeed")] double MaxMemberSpeed,
    [property: JsonPropertyName("clients")] ulong[] Clients,
    [property: JsonPropertyName("pcu")] int Pcu = 0,
    // The §7.5 size input, estimated by the owning node (linear in PCU and member count, so a
    // character-only partition weighs something). Zero from a node that predates the field, which
    // the cost model floors so the side still prices above nothing.
    [property: JsonPropertyName("estimatedSnapshotBytes")] long EstimatedSnapshotBytes = 0,
    // CLUSTER-SIM-COST: measured simulation cost of this partition - microseconds of game-thread
    // update time plus attributed physics-step time per wall-clock second, EMA-smoothed (~10 s) by
    // the owning node. Zero from a node that predates the field (see the report-level
    // SimLoadSupported flag) and legitimately near-zero for parked content.
    [property: JsonPropertyName("simLoadUs")] long SimLoadUs = 0,
    // Plan/EntityGapPlacement.md: per-member bounding spheres as [x,y,z,radius] rows, so placement
    // can use the true minimum entity-pair gap instead of the partition bounding sphere (which is
    // only a lower bound and reads 14 km of false adjacency on a spread-out partition). Null from a
    // node that predates the field - every consumer falls back to the bounding sphere. A partition
    // with members still pending (mid-init, no entity yet) includes its own bounding sphere as an
    // extra row, so the unknown positions stay conservatively covered.
    [property: JsonPropertyName("memberSpheres")] double[][]? MemberSpheres = null,
    // T-0292: a member moves faster than the partition machinery (the node measures position over
    // time, FastMotionTracker; > fast.enterMps, default 1000 m/s). While true the partition is alone
    // with that group and the registry keeps it out of co-location, balance migration, remote pull
    // and offline wakes: it neither merges nor triggers loads until it slows down. False from a node
    // that predates the field.
    [property: JsonPropertyName("fast")] bool Fast = false,
    [property: JsonPropertyName("fastSpeed")] double FastSpeed = 0,
    // #1354: the partition's NPC grids, for the cluster-wide NPC census (NpcCensus.cs). Null from a node
    // that predates the field.
    [property: JsonPropertyName("npc")] NpcGrid[]? Npc = null);

internal sealed record PartitionSphere(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("radius")] double Radius);
