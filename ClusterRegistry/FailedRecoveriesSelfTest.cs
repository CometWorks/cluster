namespace ClusterRegistry;

/// <summary>SE-1161 (FailedRecoveries.cs): a recovery whose load always fails is not re-claimed every loop. A simulated node
/// claims every half second for a minute of registry clock and fails every load of partition 5; the backoff and the node's
/// quarantine are set to seconds through the handover config, so the whole minute runs in milliseconds.</summary>
internal static partial class SelfTest
{
    private static void TestFailedRecoveryBackoff()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("failed-recovery-backoff: " + what);
        }

        // The red: no backoff and no quarantine (both 0) is the old loop, a claim and a new generation on every tick.
        (List<double> old, _) = RunFailingRecovery(backoff: 0, backoffMax: 0, quarantineAfter: 0);
        Check(
            old.Count == 120,
            $"without backoff the failing partition is claimed on every tick ({old.Count} of 120)"
        );

        // Backoff 1 s doubling to 4 s: claims at 0, 1, 3, 7, then every 4 s - 17 in the minute, not 120.
        (List<double> backedOff, FailingRecoveryProbe probe) = RunFailingRecovery(
            backoff: 1,
            backoffMax: 4,
            quarantineAfter: 0
        );
        Check(
            backedOff.Take(4).SequenceEqual([0, 1, 3, 7]),
            "the gaps double from the base: " + string.Join(", ", backedOff.Take(5))
        );
        Check(
            backedOff.Zip(backedOff.Skip(1), (a, b) => b - a).Skip(3).All(gap => gap == 4),
            "the gaps stop at the cap: " + string.Join(", ", backedOff)
        );
        Check(backedOff.Count == 17, $"a minute of failures is 17 claims ({backedOff.Count})");
        Check(
            probe.Failures == 17 && probe.Generation <= 2 + 17,
            $"17 failures in a row counted, generation {probe.Generation}"
        );
        Check(
            probe.OtherRecovered,
            "a healthy partition is recovered while the failing one backs off"
        );
        Check(probe.ResetBySave, "the first save of the partition ends the failure run");

        // And the node's quarantine after 3: the third failure is the last claim, and the registry lists it.
        (List<double> quarantined, probe) = RunFailingRecovery(
            backoff: 1,
            backoffMax: 4,
            quarantineAfter: 3
        );
        Check(
            quarantined.SequenceEqual([0, 1, 3]),
            "a quarantined partition is handed to nobody: " + string.Join(", ", quarantined)
        );
        Check(probe.Listed, "the quarantine is on /registry/quarantine");
    }

    private sealed record FailingRecoveryProbe(
        int Failures,
        long Generation,
        bool OtherRecovered,
        bool Listed,
        bool ResetBySave
    );

    /// <summary>One node, partitions 5 (every load fails) and 6 (loads). Returns the registry clock second of each claim of 5.</summary>
    private static (List<double> Claims, FailingRecoveryProbe Probe) RunFailingRecovery(
        int backoff,
        int backoffMax,
        int quarantineAfter
    )
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            DateTimeOffset start = DateTimeOffset.UnixEpoch,
                now = start;
            using var registry = new GatewayRegistry(
                directory,
                Admission,
                () => now,
                TimeSpan.FromSeconds(10)
            );
            registry.SetHandoverConfig(
                new Dictionary<string, string?>
                {
                    [GatewayRegistry.FailedBackoffKey] = backoff.ToString(),
                    [GatewayRegistry.FailedBackoffMaxKey] = backoffMax.ToString(),
                }
            );
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            foreach (ulong partition in new ulong[] { 5, 6 })
            {
                PartitionOwnership owner = registry.ClaimPartition(
                    partition,
                    one.Node,
                    one.Epoch,
                    0
                );
                registry.RegisterSave(
                    new SaveId(partition, owner.Generation, 1),
                    one.Node,
                    one.Epoch,
                    SaveKind.Periodic,
                    1,
                    "hash",
                    2,
                    "one/save-" + partition,
                    "http://one/control"
                );
                registry.ReleasePartition(partition, one.Node, one.Epoch, owner.Generation);
            }

            List<double> claims = [];
            int failures = 0;
            bool otherRecovered = false;
            ulong[] quarantined = [];
            for (int tick = 0; tick < 120; tick++, now += TimeSpan.FromSeconds(0.5))
            {
                PartitionVersion[] owned = otherRecovered
                    ? [new(6, registry.GetPartitionOwner(6).Generation)]
                    : [];
                registry.Heartbeat(one.Node, one.Epoch, owned, quarantined: quarantined);
                foreach (RecoveryClaim claim in registry.ClaimRecoveries(one.Node, one.Epoch, []))
                {
                    PartitionOwnership granted = claim.Ownership;
                    if (granted.Partition == 6)
                    {
                        otherRecovered = true;
                        continue;
                    }
                    claims.Add((now - start).TotalSeconds);
                    // The node's load throws, PartitionPersistence counts it, quarantines at the threshold, and releases.
                    if (quarantineAfter > 0 && ++failures >= quarantineAfter)
                        quarantined = [5];
                    registry.ReleasePartition(
                        5,
                        one.Node,
                        one.Epoch,
                        granted.Generation,
                        failedRecovery: true
                    );
                }
            }

            var probe = new FailingRecoveryProbe(
                registry.FailedRecoveriesForTest(5),
                registry.GetPartitionOwner(5).Generation,
                otherRecovered,
                registry.QuarantinedPartitions().Length == 1,
                false
            );
            if (quarantineAfter == 0)
            {
                // Now the load works: the next claim after the backoff is kept and saved.
                now += TimeSpan.FromSeconds(backoffMax);
                RecoveryClaim fixedLoad = registry
                    .ClaimRecoveries(one.Node, one.Epoch, [])
                    .Single();
                registry.Heartbeat(
                    one.Node,
                    one.Epoch,
                    [
                        new(5, fixedLoad.Ownership.Generation),
                        new(6, registry.GetPartitionOwner(6).Generation),
                    ]
                );
                registry.RegisterSave(
                    new SaveId(5, fixedLoad.Ownership.Generation, 1),
                    one.Node,
                    one.Epoch,
                    SaveKind.Periodic,
                    1,
                    "hash",
                    2,
                    "one/save-5b",
                    "http://one/control"
                );
                probe = probe with { ResetBySave = registry.FailedRecoveriesForTest(5) == 0 };
            }
            return (claims, probe);
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }
}
