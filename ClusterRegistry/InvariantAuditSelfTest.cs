namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0296 runtime invariant audit (InvariantAudit.cs). Each alarm is EARNED: the test first asserts that the forced
    /// condition exists in the registry (two live holders reported; an unowned row with a reachable save), then that the
    /// audit stays silent inside the grace, alarms once past it, and clears when the condition ends. A short overlap (a
    /// handover's second) and a recovering row are the controls that must never alarm.
    /// </summary>
    private static void TestInvariantAudit()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        var log = new StringWriter();
        TimeSpan duplicateGrace = GatewayRegistry.DuplicateGrace, abandonedGrace = GatewayRegistry.AbandonedGrace;
        static void Assert(bool value, string what = "assertion failed") { if (!value) throw new InvalidOperationException("invariant-audit: " + what); }
        try
        {
            Console.SetOut(log);
            GatewayRegistry.DuplicateGrace = TimeSpan.FromSeconds(30);
            GatewayRegistry.AbandonedGrace = TimeSpan.FromSeconds(60);
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            PartitionOwnership seven = registry.ClaimPartition(7, one.Node, one.Epoch, 0);
            PartitionOwnership eight = registry.ClaimPartition(8, one.Node, one.Epoch, 0);
            PartitionVersion[] oneReport = [new(7, seven.Generation, 3), new(8, eight.Generation, 2)];

            // Control: a handover-length overlap on 8 (two reports it for 5 s, then stops) never alarms.
            registry.Heartbeat(one.Node, one.Epoch, oneReport);
            registry.Heartbeat(two.Node, two.Epoch, [new(8, eight.Generation, 2)]);
            Assert(registry.ReportedHolders(8).SequenceEqual(["one", "two"]));
            registry.ExpireLeases();
            now += TimeSpan.FromSeconds(5);
            registry.Heartbeat(one.Node, one.Epoch, oneReport);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            for (int i = 0; i < 8; i++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.Heartbeat(one.Node, one.Epoch, oneReport);
                registry.Heartbeat(two.Node, two.Epoch, []);
                registry.ExpireLeases();
            }
            Assert(registry.InvariantAudit() is { Total: 0, Active.Length: 0 });

            // Forced violation: "two" keeps a stale copy of 7 (an older generation, as a copy the registry took back would be).
            registry.Heartbeat(one.Node, one.Epoch, oneReport);
            registry.Heartbeat(two.Node, two.Epoch, [new(7, seven.Generation - 1, 3)]);
                // The liveness heartbeat every node also sends each second: an empty list that is NOT a report.
                registry.Heartbeat(two.Node, two.Epoch, [], liveness: true);
                registry.Heartbeat(one.Node, one.Epoch, [], liveness: true);
            Assert(registry.ReportedHolders(7).SequenceEqual(["one", "two"]), "the falsifier must create two live holders of 7");
            registry.ExpireLeases();
            for (int step = 0; step < 5; step++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.Heartbeat(one.Node, one.Epoch, oneReport);
                registry.Heartbeat(two.Node, two.Epoch, [new(7, seven.Generation - 1, 3)]);
                // The liveness heartbeat every node also sends each second: an empty list that is NOT a report.
                registry.Heartbeat(two.Node, two.Epoch, [], liveness: true);
                registry.Heartbeat(one.Node, one.Epoch, [], liveness: true);
                registry.ExpireLeases();
            }
            Assert(registry.InvariantAudit().Total == 0, "inside the duplicate grace the audit stays silent");
            for (int step = 0; step < 4; step++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.Heartbeat(one.Node, one.Epoch, oneReport);
                registry.Heartbeat(two.Node, two.Epoch, [new(7, seven.Generation - 1, 3)]);
                // The liveness heartbeat every node also sends each second: an empty list that is NOT a report.
                registry.Heartbeat(two.Node, two.Epoch, [], liveness: true);
                registry.Heartbeat(one.Node, one.Epoch, [], liveness: true);
                registry.ExpireLeases();
            }
            InvariantAuditView duplicate = registry.InvariantAudit();
            Assert(duplicate.Total == 1 && duplicate.Active is [{ Kind: "duplicate", Partition: 7 }], "past the grace the duplicate alarms once");
            Assert(duplicate.Active[0].Detail.Contains("one, two") && duplicate.Active[0].Detail.Contains("registry owner one"));
            Assert(log.ToString().Split("INVARIANT VIOLATION duplicate: partition 7").Length == 2);
            // The copy is discarded: the violation ends, the episode count stays.
            now += TimeSpan.FromSeconds(1);
            registry.Heartbeat(one.Node, one.Epoch, oneReport);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            Assert(registry.InvariantAudit() is { Total: 1, Active.Length: 0 });
            Assert(log.ToString().Contains("invariant restored: duplicate partition 7"));

            // Abandoned: 9 has a reachable save (its holder "one" is alive) and nobody claims it.
            PartitionOwnership nine = registry.ClaimPartition(9, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(9, nine.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "hash", 2,
                "one/save", "http://one/control");
            registry.ReleasePartition(9, one.Node, one.Epoch, nine.Generation);
            Assert(registry.GetPartitionOwner(9) is { Node: null, Recovering: false }, "the falsifier must leave 9 unowned");
            for (int step = 0; step < 11; step++)
            {
                registry.Heartbeat(one.Node, one.Epoch, oneReport);
                registry.Heartbeat(two.Node, two.Epoch, []);
                registry.ExpireLeases();
                now += TimeSpan.FromSeconds(5);
            }
            Assert(registry.InvariantAudit() is { Total: 1, Active.Length: 0 }, "inside the abandoned grace the audit stays silent");
            for (int step = 0; step < 3; step++)
            {
                registry.Heartbeat(one.Node, one.Epoch, oneReport);
                registry.Heartbeat(two.Node, two.Epoch, []);
                registry.ExpireLeases();
                now += TimeSpan.FromSeconds(5);
            }
            Assert(registry.InvariantAudit() is { Total: 2, Active: [{ Kind: "abandoned", Partition: 9 }] }, "past the grace 9 alarms");
            // A recovery claim ends it (Recovering is not abandoned).
            RecoveryClaim[] claimed = registry.ClaimRecoveries(two.Node, two.Epoch, []);
            Assert(claimed.Length == 1 && claimed[0].Ownership.Partition == 9);
            registry.Heartbeat(one.Node, one.Epoch, oneReport);
            registry.ExpireLeases();
            Assert(registry.InvariantAudit() is { Total: 2, Active.Length: 0 });
        }
        finally
        {
            GatewayRegistry.DuplicateGrace = duplicateGrace;
            GatewayRegistry.AbandonedGrace = abandonedGrace;
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }
}
