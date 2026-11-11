namespace ClusterRegistry;

/// <summary>SE-1347 copy trimming (CopyTrim.cs).</summary>
internal static partial class SelfTest
{
    /// <summary>
    /// Saves with a copy on every node that held them end on exactly two holders, the owner's copy first, and recovery
    /// still finds every partition's newest save. A trimmed copy leaves Locations at once and goes to its own holder only;
    /// the ack clears it, and a restart keeps what is still to delete. Not trimmed: a save with fewer than two keepable
    /// holders, a partition in an incomplete handover, anything with the knob at 0. While a trim waits for its ack, the
    /// replica queue does not hand that holder a fresh copy. Multi-host: the second kept copy is on the other host. Earned
    /// reds: keeping by name instead of owner first keeps node-1/node-2 for partition 2; without the replica guard node-3
    /// is handed partition 1's save while its trim is pending.
    /// </summary>
    private static void TestCopyTrim()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "cluster-gateway-copy-trim-self-test-" + Guid.NewGuid()
        );
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        int batch = GatewayRegistry.TrimBatch;
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("copy-trim: " + what);
        }
        try
        {
            NodeRegistration[] nodes;
            SaveId p1Old,
                p1New,
                p2Save,
                p3Save;
            using (
                var registry = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromDays(1)
                )
            )
            {
                nodes =
                [
                    .. Enumerable
                        .Range(1, 4)
                        .Select(i =>
                            registry.RegisterNode(
                                Join("node-" + i, "node-" + i, Guid.NewGuid()) with
                                {
                                    Host = "h1",
                                    ControlEndpoint = "http://127.0.0.1:2960" + i,
                                }
                            )
                        ),
                ];
                NodeRegistration N(int i) => nodes[i - 1];
                SaveId Save(int owner, ulong partition, long generation, int sequence)
                {
                    now += TimeSpan.FromSeconds(1);
                    var id = new SaveId(partition, generation, sequence);
                    registry.RegisterSave(
                        id,
                        N(owner).Node,
                        N(owner).Epoch,
                        SaveKind.Periodic,
                        100,
                        $"c{partition}.{sequence}",
                        sequence,
                        $"node-{owner}/P{partition}/{sequence}",
                        N(owner).ControlEndpoint!
                    );
                    return id;
                }
                void Copy(SaveId id, params int[] holders)
                {
                    foreach (int i in holders)
                        registry.RegisterHolder(
                            id,
                            N(i).Node,
                            N(i).Epoch,
                            100,
                            $"c{id.Partition}.{id.Sequence}",
                            $"node-{i}/P{id.Partition}/{id.Sequence}",
                            N(i).ControlEndpoint!
                        );
                }
                SaveRegistration Row(SaveId id) => registry.Saves().Single(x => x.SaveId == id);
                string[] Holders(SaveId id) =>
                    [.. Row(id).LocationHolders.Values.Distinct().Order(StringComparer.Ordinal)];

                // Partition 1 (owner node-1): an older save on all four nodes and the newest on two. Partition 2 (owner
                // node-4) on all four. Partition 3 (owner node-1) on three, two of them corrupt.
                PartitionOwnership one = registry.ClaimPartition(1, N(1).Node, N(1).Epoch, 0);
                p1Old = Save(1, 1, one.Generation, 1);
                Copy(p1Old, 2, 3, 4);
                p1New = Save(1, 1, one.Generation, 2);
                Copy(p1New, 2);
                PartitionOwnership two = registry.ClaimPartition(2, N(4).Node, N(4).Epoch, 0);
                p2Save = Save(4, 2, two.Generation, 1);
                Copy(p2Save, 1, 2, 3);
                PartitionOwnership three = registry.ClaimPartition(3, N(1).Node, N(1).Epoch, 0);
                p3Save = Save(1, 3, three.Generation, 1);
                Copy(p3Save, 2, 3);
                registry.ReportCorruption(p3Save, N(2).Node, N(1).Node, N(1).Epoch);
                registry.ReportCorruption(p3Save, N(3).Node, N(1).Node, N(1).Epoch);
                // Partition 4 (owner node-2) on three, in an incomplete handover.
                PartitionOwnership four = registry.ClaimPartition(4, N(2).Node, N(2).Epoch, 0);
                SaveId p4Save = Save(2, 4, four.Generation, 1);
                Copy(p4Save, 3, 4);
                registry.Begin(
                    "h-4",
                    4,
                    N(2).Node,
                    N(2).Epoch,
                    four.Generation,
                    N(3).Node,
                    N(3).Epoch
                );

                GatewayRegistry.TrimBatch = 0;
                Check(registry.PlanRetention().Length == 0, "knob 0: nothing is trimmed");
                GatewayRegistry.TrimBatch = 1;
                Check(registry.PlanRetention().Length == 1, "batch 1: one copy a pass");
                GatewayRegistry.TrimBatch = 1000;
                DeleteDirective[] planned = registry.PlanRetention();
                Check(
                    planned.Length == 3,
                    $"the other three copies above the target go ({planned.Length})"
                );

                Check(
                    Holders(p1Old).SequenceEqual(["node-1", "node-2"]),
                    "partition 1's older save keeps its owner node-1 and node-2: "
                        + string.Join(",", Holders(p1Old))
                );
                Check(
                    Holders(p1New).SequenceEqual(["node-1", "node-2"]),
                    "a save at the target is untouched"
                );
                Check(
                    Holders(p2Save).SequenceEqual(["node-1", "node-4"]),
                    "partition 2 keeps its owner node-4 first: " + string.Join(",", Holders(p2Save))
                );
                Check(
                    Holders(p3Save).Length == 3,
                    "fewer than two intact holders: nothing is trimmed"
                );
                Check(Holders(p4Save).Length == 3, "an incomplete handover: nothing is trimmed");
                Check(
                    registry.NewestSave(1)?.SaveId == p1New
                        && registry.NewestSave(2)?.SaveId == p2Save,
                    "recovery still finds every newest save"
                );
                Check(
                    registry
                        .ResolveCover(2)
                        .All(x =>
                            x.Save.Locations.All(l =>
                                !l.StartsWith("node-2/") && !l.StartsWith("node-3/")
                            )
                        ),
                    "a recovery cover never offers a trimmed copy"
                );

                string[] Deleting(int i) =>
                    [
                        .. registry
                            .PendingDeletes(N(i).Node, N(i).Epoch)
                            .Select(x => x.Location)
                            .Order(StringComparer.Ordinal),
                    ];
                Check(Deleting(1).Length == 0, "node-1 deletes nothing");
                Check(
                    Deleting(2).SequenceEqual(["node-2/P2/1"]),
                    "node-2 deletes its partition 2 copy: " + string.Join(",", Deleting(2))
                );
                Check(
                    Deleting(3).SequenceEqual(["node-3/P1/1", "node-3/P2/1"]),
                    "node-3 deletes its two copies: " + string.Join(",", Deleting(3))
                );
                Check(
                    Deleting(4).SequenceEqual(["node-4/P1/1"]),
                    "node-4 deletes its partition 1 copy"
                );

                // The kept node-2 copy of partition 1's older save turns out corrupt: the save needs a copy, but node-3 and
                // node-4 still have to delete theirs, so neither is handed one until its ack.
                registry.ReportCorruption(p1Old, N(2).Node, N(1).Node, N(1).Epoch);
                Check(
                    !registry.ReplicaWork(N(3).Node, N(3).Epoch).Any(x => x.SaveId == p1Old),
                    "no fresh copy to node-3 while its trim is pending"
                );
                Check(
                    !registry.ReplicaWork(N(4).Node, N(4).Epoch).Any(x => x.SaveId == p1Old),
                    "no fresh copy to node-4 while its trim is pending"
                );
                Check(registry.AcknowledgeDelete(p1Old, "node-3/P1/1"), "node-3 acks its trim");
                Check(
                    !registry.AcknowledgeDelete(p1Old, "node-3/P1/1"),
                    "a second ack answers false"
                );
                Check(
                    registry.ReplicaWork(N(3).Node, N(3).Epoch).Any(x => x.SaveId == p1Old),
                    "after its ack node-3 may copy the save again"
                );
                registry.RegisterHolder(
                    p1Old,
                    N(3).Node,
                    N(3).Epoch,
                    100,
                    "c1.1",
                    "node-3/P1/1",
                    N(3).ControlEndpoint!
                );
                Check(
                    Holders(p1Old).Contains("node-3") && Deleting(3).SequenceEqual(["node-3/P2/1"]),
                    "the fresh copy is catalogued, not deleted"
                );
                // A holder that registers its trimmed location again (a copy that landed anyway) cancels the trim.
                registry.RegisterHolder(
                    p1Old,
                    N(4).Node,
                    N(4).Epoch,
                    100,
                    "c1.1",
                    "node-4/P1/1",
                    N(4).ControlEndpoint!
                );
                Check(
                    Holders(p1Old).Contains("node-4") && Deleting(4).Length == 0,
                    "a registered location is no longer deleted: " + string.Join(",", Deleting(4))
                );
            }

            // A restart keeps the trims still to acknowledge.
            using (
                var restarted = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromDays(1)
                )
            )
            {
                string[] Deleting(int i) =>
                    [
                        .. restarted
                            .PendingDeletes(nodes[i - 1].Node, nodes[i - 1].Epoch)
                            .Select(x => x.Location)
                            .Order(StringComparer.Ordinal),
                    ];
                Check(
                    Deleting(2).SequenceEqual(["node-2/P2/1"])
                        && Deleting(3).SequenceEqual(["node-3/P2/1"]),
                    "the pending trims survive a restart"
                );
                foreach (int i in new[] { 2, 3 })
                foreach (
                    DeleteDirective deletion in restarted.PendingDeletes(
                        nodes[i - 1].Node,
                        nodes[i - 1].Epoch
                    )
                )
                    Check(
                        restarted.AcknowledgeDelete(deletion.SaveId, deletion.Location),
                        "every trim is acknowledged"
                    );
                Check(
                    restarted.Saves().All(x => x.TrimmedLocations == null),
                    "no trim is left after the acks"
                );
                Check(
                    restarted.Saves().Single(x => x.SaveId == p2Save).Locations.Count == 2,
                    "partition 2's save stays on exactly two holders"
                );
            }
            Directory.Delete(directory, true);

            // Two hosts: the second kept copy must be on the other host, even when a same-host holder sorts first.
            using (
                var registry = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromDays(1)
                )
            )
            {
                NodeRegistration Node(string name, string host, int port) =>
                    registry.RegisterNode(
                        Join(name, name, Guid.NewGuid()) with
                        {
                            Host = host,
                            ControlEndpoint = "http://127.0.0.1:" + port,
                        }
                    );
                NodeRegistration a1 = Node("a1", "A", 29611),
                    a2 = Node("a2", "A", 29612),
                    b1 = Node("b1", "B", 29613);
                PartitionOwnership owner = registry.ClaimPartition(7, a1.Node, a1.Epoch, 0);
                var id = new SaveId(7, owner.Generation, 1);
                registry.RegisterSave(
                    id,
                    a1.Node,
                    a1.Epoch,
                    SaveKind.Periodic,
                    100,
                    "c7",
                    1,
                    "a1/P7",
                    a1.ControlEndpoint!
                );
                registry.RegisterHolder(
                    id,
                    a2.Node,
                    a2.Epoch,
                    100,
                    "c7",
                    "a2/P7",
                    a2.ControlEndpoint!
                );
                registry.RegisterHolder(
                    id,
                    b1.Node,
                    b1.Epoch,
                    100,
                    "c7",
                    "b1/P7",
                    b1.ControlEndpoint!
                );
                DeleteDirective[] planned = registry.PlanRetention();
                Check(
                    planned.Length == 1 && planned[0].Location == "a2/P7",
                    "two hosts: a2 goes, b1 stays for the host spread"
                );
            }
            Directory.Delete(directory, true);

            // A holder whose lease lapsed keeps its copy: it could delete nothing now (left to the gone-holder release).
            using (
                var registry = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromSeconds(10)
                )
            )
            {
                NodeRegistration[] xs =
                [
                    .. Enumerable
                        .Range(1, 4)
                        .Select(i =>
                            registry.RegisterNode(
                                Join("x" + i, "x" + i, Guid.NewGuid()) with
                                {
                                    ControlEndpoint = "http://127.0.0.1:2962" + i,
                                }
                            )
                        ),
                ];
                PartitionOwnership owner = registry.ClaimPartition(9, xs[0].Node, xs[0].Epoch, 0);
                var id = new SaveId(9, owner.Generation, 1);
                registry.RegisterSave(
                    id,
                    xs[0].Node,
                    xs[0].Epoch,
                    SaveKind.Periodic,
                    100,
                    "c9",
                    1,
                    "x1/P9",
                    xs[0].ControlEndpoint!
                );
                foreach (NodeRegistration x in xs.Skip(1))
                    registry.RegisterHolder(
                        id,
                        x.Node,
                        x.Epoch,
                        100,
                        "c9",
                        x.Node + "/P9",
                        x.ControlEndpoint!
                    );
                for (int step = 0; step < 2; step++)
                {
                    now += TimeSpan.FromSeconds(8);
                    registry.Heartbeat(
                        xs[0].Node,
                        xs[0].Epoch,
                        [new PartitionVersion(9, owner.Generation)]
                    );
                    registry.Heartbeat(xs[1].Node, xs[1].Epoch, []);
                    registry.Heartbeat(xs[2].Node, xs[2].Epoch, []);
                }
                DeleteDirective[] planned = registry.PlanRetention();
                Check(
                    planned.Select(x => x.Location).SequenceEqual(["x3/P9"]),
                    "x3's copy goes, the lapsed x4 keeps its own: "
                        + string.Join(",", planned.Select(x => x.Location))
                );
            }
        }
        finally
        {
            GatewayRegistry.TrimBatch = batch;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
