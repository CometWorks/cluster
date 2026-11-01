using System.Net;

namespace ClusterRegistry;

/// <summary>
/// T-0268 test trigger: a balance migration of a NAMED partition, now, instead of waiting for the balancer
/// to choose one (T-0249 closes only on a balance migration of a client's partition near the Earth base).
///
///   POST /balance/migrate?partition=ID[&amp;target=node-N][&amp;dryRun=true]     (admin token: not a /registry/ route)
///
/// It plans exactly the unit the balancer moves - the partition's envelope-connected component on its node - and
/// pins it with the balancer's own PinMigration, logged by LogBalancePin with the explain text naming the trigger.
/// From the next pass the co-location loop carries it member by member as reason "balance-migration", the same
/// handover lines a natural migration logs. Without a target it goes to the least-loaded other node. It refuses,
/// with the reason, whatever the balancer itself would wait for: a pause (recovery, rotation), automatic handovers
/// suspended (nothing would carry the pin), or a member in backoff, validation-blocked or already pinned.
/// </summary>
internal sealed partial class DirectGateway
{
    internal readonly record struct TestMigration(RebalancePlan? Plan, string? Refusal, int Clients);

    internal static TestMigration PlanTestMigration((PartitionReport Report, string Node)[] nodes, ulong partition,
        string? target, CostModel model, HashSet<ulong> routed, Func<ulong, bool> held)
    {
        int source = Array.FindIndex(nodes, n => n.Report.Partitions.Any(p => p.Id == partition));
        if (source < 0) return new TestMigration(null, $"partition {partition} is on no reporting node", 0);
        bool simLoad = nodes.All(n => n.Report.SimLoadSupported);
        int to = target == null
            ? Enumerable.Range(0, nodes.Length).Where(i => i != source)
                .OrderBy(i => EffectiveNodeLoad(nodes[i].Report, simLoad))
                .DefaultIfEmpty(-1).First()
            : Array.FindIndex(nodes, n => n.Node == target);
        if (to < 0) return new TestMigration(null, target == null ? "no other node reports partitions" : $"target {target} is not a reporting node", 0);
        if (to == source) return new TestMigration(null, $"partition {partition} is already on {target}", 0);

        PartitionReport report = nodes[source].Report;
        (PartitionInfo Partition, bool OnSource)[] all = report.Partitions.Select(p => (p, true)).ToArray();
        int[] labels = Components(all, report.SyncDistance + report.Margin, model);
        int label = labels[Array.FindIndex(all, x => x.Partition.Id == partition)];
        PartitionInfo[] members = Enumerable.Range(0, all.Length).Where(i => labels[i] == label).Select(i => all[i].Partition).ToArray();
        int clients = members.Sum(m => ClientCount(m, routed));
        if (members.FirstOrDefault(m => held(m.Id)) is { } stuck)
            return new TestMigration(null, $"member {stuck.Id} is in handover backoff, validation-blocked or already pinned", clients);

        long[] load = nodes.Select(n => EffectiveNodeLoad(n.Report, simLoad)).ToArray();
        long componentLoad = members.Sum(m => PartitionLoad(m, simLoad));
        long oldPeak = load.Max();
        load[source] -= componentLoad;
        load[to] += componentLoad;
        double cost = members.Sum(m => MoveCost(model, m, routed));
        return new TestMigration(new RebalancePlan(members, nodes[source].Node, nodes[to].Node, componentLoad, oldPeak, load.Max(), cost, 0,
            simLoad ? "sim-us/s" : "PCU", "operator test trigger (T-0268 POST /balance/migrate)",
            clients > 0 ? RebalanceKind.Client : RebalanceKind.Load), null, clients);
    }

    private void HandleTestMigration(HttpListenerContext context)
    {
        string? raw = context.Request.QueryString["partition"];
        if (!ulong.TryParse(raw, out ulong partition)) throw new ArgumentException("partition=<id> is required");
        string? target = context.Request.QueryString["target"];
        bool dryRun = string.Equals(context.Request.QueryString["dryRun"], "true", StringComparison.OrdinalIgnoreCase);
        if (_lastPartitionReports is not { } snapshot || DateTime.UtcNow - snapshot.At > TimeSpan.FromSeconds(15))
            throw new RegistryBusyException("no partition report from the last 15 s");
        string? pause = _automaticSuspended ? "automatic handovers are suspended (nothing would carry the pin)"
            : _registry?.RecoveryPending() == true ? "partitions are still unowned or recovering"
            : _registry?.RotationInFlight(TimeSpan.FromSeconds(RotationSettlingSeconds)) == true || DrainingNodes().Length != 0 ? "node rotation in flight"
            : null;
        HashSet<ulong> routed = [.. _clients.Keys.Select(client => _registry?.GetClientPartition(client)?.Partition ?? 0).Where(x => x != 0)];
        CostModel model = _costModel;
        TestMigration planned = PlanTestMigration(snapshot.Reports.Select(x => (x.Report, x.Node)).ToArray(), partition, target,
            GapModel(model), routed, id => InHandoverBackoff(id) || ValidationBlocked(id) || MigrationPinned(id));
        string? refusal = planned.Refusal ?? pause;
        bool pinned = false;
        if (refusal == null && !dryRun && planned.Plan is { } plan)
        {
            PinMigration(plan.Members, plan.Target);
            _lastBalanceMove = DateTime.UtcNow;
            LogBalancePin(plan, model);
            pinned = true;
        }
        Console.WriteLine($"Balance migration test trigger: partition {partition}, target {target ?? "(least loaded)"}, dryRun {dryRun},"
            + $" {(pinned ? "pinned" : refusal != null ? "refused: " + refusal : "not pinned (dry run)")}");
        WriteJson(context, refusal == null ? 200 : 409, new
        {
            partition,
            dryRun,
            pinned,
            refusal,
            source = planned.Plan?.Source,
            target = planned.Plan?.Target,
            members = planned.Plan?.Members.Select(m => m.Id).ToArray(),
            clients = planned.Clients,
            kind = planned.Plan?.Kind.ToString().ToLowerInvariant(),
            componentLoad = planned.Plan?.ComponentLoad,
            loadUnit = planned.Plan?.LoadUnit,
            peak = planned.Plan is { } p ? $"{p.OldPeak}->{p.NewPeak}" : null,
            estimatedLagMs = planned.Plan?.CostMs,
        });
    }
}
