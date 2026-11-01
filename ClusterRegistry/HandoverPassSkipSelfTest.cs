namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0373 (2), r116b 2026-10-01 18:22:11-18:24:06 (85 passes) and r124 (4): a Closed node reports a client-bearing
    /// partition that has no registry row. The drain catch-up of the automatic pass hands it over, FencedHandover
    /// claims the row for the source first, and ClaimPartition refuses a Closed node: "Node 'node-79' epoch 1 is
    /// Closed" escaped DrainClosedNodes, so no partition after it in the report (and no other closed node) was
    /// drained in that pass, nor in any later one until the node went Empty. Here the closed source reports such a
    /// partition FIRST and its owned, client-bearing partition second: the pass must skip the first and still move
    /// the second to the target.
    /// </summary>
    private static void TestHandoverPassSkipsClosedNodeItem()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-pass-skips-closed-node-item: " + what); }
        const ulong Unclaimed = 999001;
        using var scripted = new ScriptedHandover((_, _) => 200);
        static string Info(ulong id) => $$"""{"id":{{id}},"memberCount":1,"anchored":false,"sphere":{"x":0,"y":0,"z":0,"radius":10},"maxMemberSpeed":0,"clients":[{{ScriptedHandover.Client}}]}""";
        scripted.SourcePartitions = $$"""{"syncDistance":3000,"margin":100,"partitions":[{{Info(Unclaimed)}},{{Info(ScriptedHandover.Partition)}}],"evaluatedAt":0}""";
        Exception? escaped = scripted.DrainClosedSource();
        Console.WriteLine($"handover-pass-skips-closed-node-item: escaped={escaped?.GetType().Name ?? "none"}"
            + (escaped == null ? "" : $" ({escaped.Message})") + $" owner={scripted.Owner} calls=[{string.Join(", ", scripted.Calls)}]");
        Check(escaped == null, "one partition the pass could not drain ended the drain catch-up: " + escaped?.Message);
        Check(scripted.Owner == "target", "the healthy partition behind the skipped one was not drained in the same pass (owner " + scripted.Owner + ")");
        Check(scripted.Count("target commit") == 1 && scripted.Count("source release") == 1, "the healthy partition's handover did not commit and release");
        Check(!scripted.ClientDisconnected, "the client was disconnected");
    }

    /// <summary>
    /// #1277 (T-0296 F34, T-0327 class): a network window is active and the only serving node, the target, is remote
    /// with a lapsed lease the window holds; it still reports (lightest). The Closed source reports a client-bearing
    /// partition. The drain catch-up handed the crew into the held target, where the window outcome rolls it back.
    /// Fixed: no handover, the crew waits (for the force drain or the target's return); once the target heartbeats
    /// again, the next pass drains it there.
    /// </summary>
    private static void TestDrainSkipsLeaseHeldTarget()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("drain-skips-lease-held-target: " + what); }
        using var scripted = new ScriptedHandover((_, _) => 200);
        scripted.SourcePartitions = $$"""{"syncDistance":3000,"margin":100,"partitions":[{"id":{{ScriptedHandover.Partition}},"memberCount":1,"anchored":false,"sphere":{"x":0,"y":0,"z":0,"radius":10},"maxMemberSpeed":0,"clients":[{{ScriptedHandover.Client}}]}],"evaluatedAt":0}""";
        GatewayRegistry registry = scripted.Registry;
        // A window all day long (start = end), the gateway on "polaris", the target on "ai" with its lease lapsed.
        registry.LeaseHold = new NetworkWindows([(TimeSpan.Zero, TimeSpan.Zero)], TimeSpan.Zero, "polaris", TimeZoneInfo.Utc);
        NodeRegistration target = registry.Nodes().Single(x => x.Node == "target");
        target.Host = "ai";
        target.LeaseExpires = DateTimeOffset.UtcNow.AddSeconds(-1);
        string held = string.Join(",", registry.LapsedHeldNodes());
        Exception? escaped = scripted.DrainClosedSource();
        string heldOwner = scripted.Owner, heldCalls = string.Join(", ", scripted.Calls);
        bool targetCalled = scripted.Calls.Any(x => x.StartsWith("target ", StringComparison.Ordinal));
        // The target is back: its lease is fresh, the next pass drains the crew.
        target.LeaseExpires = DateTimeOffset.UtcNow.AddMinutes(5);
        Exception? next = scripted.DrainClosedSource();
        Console.WriteLine($"drain-skips-lease-held-target: held=[{held}] escaped={escaped?.Message ?? "none"} owner={heldOwner} calls=[{heldCalls}]"
            + $" | back: escaped={next?.Message ?? "none"} owner={scripted.Owner}");
        Check(held == "target", "setup: the target is not lapsed and held: [" + held + "]");
        Check(escaped == null, "the drain catch-up failed: " + escaped?.Message);
        Check(heldOwner == "source" && !targetCalled, "the crew was handed into the lease-held target (owner " + heldOwner + ", calls " + heldCalls + ")");
        Check(next == null && scripted.Owner == "target", "the crew was not drained once the target was back (owner " + scripted.Owner + ")");
        Check(!scripted.ClientDisconnected, "the client was disconnected");
    }

    /// <summary>
    /// T-0374, the root of T-0373 (2): the row-less partition was a placeholder layout the Closed node made around a
    /// stranded client's body (PartitionPersistence.AllocatePartitionIds, ids of ClusterProtocol.PlaceholderPartitionIds).
    /// The drain catch-up must claim it for the Closed source and hand it, with the client, to the target - not skip it
    /// until the force drain. The claim takes nothing else: no id below the range, no row that exists, no Active node.
    /// </summary>
    private static void TestClosedNodePlaceholderDrains()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("closed-node-placeholder-drains: " + what); }
        static bool Refused(Action claim)
        {
            try { claim(); return false; }
            catch (RegistryConflictException) { return true; }
        }
        ulong placeholder = ClusterProtocol.PlaceholderPartitionIds.Floor + 4242;
        using var scripted = new ScriptedHandover((_, _) => 200);
        scripted.SourcePartitions = $$"""{"syncDistance":3000,"margin":100,"partitions":[{"id":{{placeholder}},"memberCount":1,"anchored":false,"sphere":{"x":0,"y":0,"z":0,"radius":10},"maxMemberSpeed":0,"clients":[{{ScriptedHandover.Client}}]}],"evaluatedAt":0}""";
        Exception? escaped = scripted.DrainClosedSource();
        string owner = scripted.Registry.Partitions().FirstOrDefault(x => x.Partition == placeholder)?.Node ?? "no row";
        Console.WriteLine($"closed-node-placeholder-drains: escaped={escaped?.GetType().Name ?? "none"} owner={owner} calls=[{string.Join(", ", scripted.Calls)}]");
        Check(escaped == null, "the drain catch-up failed: " + escaped?.Message);
        Check(owner == "target", "the Closed node's placeholder was not handed to the target (owner " + owner + ")");
        Check(scripted.Count("target commit") == 1 && scripted.Count("source release") == 1, "the placeholder's handover did not commit and release");
        Check(!scripted.ClientDisconnected, "the client was disconnected");

        GatewayRegistry registry = scripted.Registry;
        NodeRegistration source = registry.Nodes().Single(x => x.Node == "source"), target = registry.Nodes().Single(x => x.Node == "target");
        Check(Refused(() => registry.ClaimClosedNodePlaceholder(placeholder + 1, target.Node, target.Epoch)), "an Active node claimed a placeholder as Closed");
        Check(Refused(() => registry.ClaimClosedNodePlaceholder(ClusterProtocol.PlaceholderPartitionIds.Floor - 1, source.Node, source.Epoch)),
            "a Closed node claimed an id below the placeholder range");
        Check(Refused(() => registry.ClaimClosedNodePlaceholder(placeholder, source.Node, source.Epoch)), "a Closed node claimed a placeholder that has a row");
        Check(Refused(() => registry.ClaimPartition(placeholder + 2, source.Node, source.Epoch, 0)), "ClaimPartition took a claim from a Closed node");
        Check(registry.AllocatePartitionIds(target.Node, target.Epoch, 1).Single() < ClusterProtocol.PlaceholderPartitionIds.Floor,
            "the registry issued an id in the placeholder range");
        long start = ClusterProtocol.PlaceholderPartitionIds.RandomStart();
        Check((ulong)start >= ClusterProtocol.PlaceholderPartitionIds.Floor && (ulong)start < ClusterProtocol.PlaceholderPartitionIds.Floor + ClusterProtocol.PlaceholderPartitionIds.StartSpan
            && start < long.MaxValue - (1L << 60), "a placeholder start fell outside its range: " + start);
        Check(Enumerable.Range(0, 64).Select(_ => ClusterProtocol.PlaceholderPartitionIds.RandomStart()).Distinct().Count() > 60,
            "node processes still start their placeholders at the same id");
    }

    /// <summary>
    /// #1283 (T-0296 F38): the placeholder row the drain claims for a Closed source is not in the node's m_owned, so no
    /// heartbeat renews it. (1) Claimed before the handover's partition report and presync (10 s client timeout each),
    /// its lease lapsed under them: unowned at generation 2, Begin refused generation 1 as fenced, and the
    /// unrecoverable row paused balancing (RecoveryPending) for the 5-min unowned reap. Here the lapse is forced at
    /// every partition report of the source: no claimed placeholder may exist then, and the drain commits. (2) An
    /// aborted handover (the capture fails) left the claimed row to the same lapse: it must be released and reaped,
    /// so a forced lapse finds nothing, RecoveryPending stays false and the next pass drains the placeholder.
    /// </summary>
    private static void TestClosedNodePlaceholderLeaseLapse()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("closed-node-placeholder-lease-lapse: " + what); }
        ulong placeholder = ClusterProtocol.PlaceholderPartitionIds.Floor + 4343;
        string partitions = $$"""{"syncDistance":3000,"margin":100,"partitions":[{"id":{{placeholder}},"memberCount":1,"anchored":false,"sphere":{"x":0,"y":0,"z":0,"radius":10},"maxMemberSpeed":0,"clients":[{{ScriptedHandover.Client}}]}],"evaluatedAt":0}""";
        // The lease of the row the Closed source holds for the placeholder lapses now, as it does ttl after the claim.
        static bool Lapse(GatewayRegistry registry, ulong partition)
        {
            if (registry.Partitions().FirstOrDefault(x => x.Partition == partition) is not { Node: not null } row) return false;
            row.LeaseExpires = DateTimeOffset.MinValue;
            registry.ExpireLeases();
            return true;
        }
        static string Row(GatewayRegistry registry, ulong partition) =>
            registry.Partitions().FirstOrDefault(x => x.Partition == partition) is { } row ? $"{row.Node ?? "unowned"}@{row.Generation}" : "no row";

        ScriptedHandover? scripted = null;
        bool lapsed = false;
        using (scripted = new ScriptedHandover((call, _) =>
        {
            if (call == "source partitions" && Lapse(scripted!.Registry, placeholder)) lapsed = true;
            return 200;
        }))
        {
            scripted.SourcePartitions = partitions;
            Exception? escaped = scripted.DrainClosedSource();
            string row = Row(scripted.Registry, placeholder);
            Console.WriteLine($"closed-node-placeholder-lease-lapse (1): lapsed={lapsed} row={row} recoveryPending={scripted.Registry.RecoveryPending()}"
                + $" calls=[{string.Join(", ", scripted.Calls)}]");
            Check(escaped == null, "the drain catch-up failed: " + escaped?.Message);
            Check(!lapsed, "the placeholder was claimed before the handover's partition report, where its unrenewed lease can lapse");
            Check(row.StartsWith("target@", StringComparison.Ordinal), "the placeholder was not handed to the target (row " + row + ")");
            Check(!scripted.Registry.RecoveryPending(), "recovery is pending: the balancer pauses");
        }

        using (scripted = new ScriptedHandover((call, nth) => call == "source capture" && nth == 1 ? 500 : 200))
        {
            scripted.SourcePartitions = partitions;
            Exception? escaped = scripted.DrainClosedSource();
            string afterAbort = Row(scripted.Registry, placeholder);
            lapsed = Lapse(scripted.Registry, placeholder);
            bool pending = scripted.Registry.RecoveryPending();
            Console.WriteLine($"closed-node-placeholder-lease-lapse (2): afterAbort={afterAbort} lapsed={lapsed} row={Row(scripted.Registry, placeholder)}"
                + $" recoveryPending={pending} calls=[{string.Join(", ", scripted.Calls)}]");
            Check(escaped == null, "the drain catch-up failed: " + escaped?.Message);
            Check(scripted.Count("source thaw") == 1 && scripted.Count("target commit") == 0, "the first handover was not aborted at the capture");
            Check(afterAbort == "no row", "the aborted handover left its claimed placeholder row (" + afterAbort + ")");
            Check(!pending, "recovery is pending after the aborted placeholder's lease lapsed: the balancer pauses");
            escaped = scripted.DrainClosedSource();
            string row = Row(scripted.Registry, placeholder);
            Console.WriteLine($"closed-node-placeholder-lease-lapse (2) next pass: row={row} recoveryPending={scripted.Registry.RecoveryPending()}");
            Check(escaped == null, "the next drain catch-up failed: " + escaped?.Message);
            Check(row.StartsWith("target@", StringComparison.Ordinal), "the next pass did not hand the placeholder to the target (row " + row + ")");
            Check(scripted.Count("target commit") == 1 && !scripted.ClientDisconnected, "the next pass did not commit, or the client was disconnected");
        }
    }

    /// <summary>
    /// T-0373 (1), live 2026-10-02 07:04:35: the balance migration of 307625 off node-187 read the partition's
    /// registry row, and node-187 split the partition (topology-input, 07:04:35.264) before the handover reached
    /// Begin. The registry releases a row IN PLACE (Node = null) and hands out the live object, so Begin was called
    /// with a null source node and its node lookup threw ArgumentNullException ("Value cannot be null. (Parameter
    /// 'key')"). Here the split lands while the handover reads the source's partition report: the handover must be
    /// refused as a registry conflict (a stale generation), which the pass counts as that partition's failure.
    /// </summary>
    private static void TestHandoverOwnerRowReleasedInFlight()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("handover-owner-row-released-in-flight: " + what); }
        ScriptedHandover? scripted = null;
        bool split = false;
        using (scripted = new ScriptedHandover((call, nth) =>
        {
            if (call != "source partitions" || nth != 1) return 200;
            GatewayRegistry registry = scripted!.Registry;
            NodeRegistration source = registry.Nodes().Single(x => x.Node == "source");
            ulong[] outputs = registry.AllocatePartitionIds(source.Node, source.Epoch, 2);
            registry.ApplyLineage("t0373-split", LineageKind.Split, source.Node, source.Epoch,
                [new(ScriptedHandover.Partition, registry.GetPartitionOwner(ScriptedHandover.Partition).Generation)], outputs,
                new() { [outputs[0]] = [1], [outputs[1]] = [2] }, 1);
            split = true;
            return 200;
        }))
        {
            Exception? failure = scripted.Handover();
            Console.WriteLine($"handover-owner-row-released-in-flight: split={split} failure={(failure == null ? "none" : DirectGateway.DescribeFailure(failure))}"
                + $" owner={scripted.Owner} calls=[{string.Join(", ", scripted.Calls)}]");
            Check(split, "the split did not land inside the handover (no pre-freeze partition report was read)");
            Check(failure is not ArgumentNullException, "the released row reached the registry as a null node: " + failure?.Message);
            Check(failure is RegistryConflictException, "the handover of a partition that was split away was not refused as a conflict: " + failure?.Message);
            Check(scripted.Count("source capture") == 0 && scripted.Registry.Incomplete().Length == 0, "the handover froze the source or left a record");
        }

        // The log lines of the fix: once a minute per distinct key, and the type with the first frame of ours.
        Dictionary<string, DateTime> logged = [];
        DateTime now = DateTime.UtcNow;
        Check(DirectGateway.PassLogDue(logged, "partition 1|a", now) && !DirectGateway.PassLogDue(logged, "partition 1|a", now.AddSeconds(59)),
            "the same skip was logged twice within a minute");
        Check(DirectGateway.PassLogDue(logged, "partition 2|a", now) && DirectGateway.PassLogDue(logged, "partition 1|b", now),
            "a different item or message was not logged");
        Check(DirectGateway.PassLogDue(logged, "partition 1|a", now.AddSeconds(61)), "a skip that persists was not logged again after a minute");
        for (int i = 0; i < 1000; i++) DirectGateway.PassLogDue(logged, "flood " + i, now.AddSeconds(200 + i));
        Check(logged.Count <= 257, "the log-due map grew without bound (" + logged.Count + ")");
        try { new Dictionary<string, int>().TryGetValue(NullKey()!, out _); Check(false, "no exception"); }
        catch (ArgumentNullException e)
        {
            string described = DirectGateway.DescribeFailure(e);
            Check(described.StartsWith("System.ArgumentNullException: ", StringComparison.Ordinal) && described.Contains("first frame: at ClusterRegistry."),
                "the failure detail names no type or no frame of ours: " + described);
        }
        static string? NullKey() => null;
    }
}
