namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0339: a split whose largest output keeps the input's partition id. The id is then input and output of
    /// one split, and the saves catalogued under it BEFORE the split hold more than the partition holds after it.
    /// Everything that reads "the newest save of this id" as the partition's state must cut such a save down to
    /// the members the split left under the id, and the members that left must come back from that same save
    /// through the other output and from nowhere else.
    /// <para>
    /// The fixture knows what each save file holds, so it can replay what a recovering node loads from a claim
    /// (PartitionPersistence.LoadRecovery: every cover file through its entity filter, and when a file cannot be
    /// read the newest readable fallback of the same partition through the SAME filter) and count every entity.
    /// </para>
    /// </summary>
    private static void TestSplitKeepsInputId()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("split-keeps-input-id: " + what); }
        static string Show(IEnumerable<SaveCover> cover) => string.Join(" + ", cover.Select(x =>
            $"{x.Save.SaveId.Partition}/{x.Save.SaveId.Generation}/{x.Save.SaveId.Sequence}:{(x.EntityIds == null ? "*" : string.Join(",", x.EntityIds))}"));
        const ulong Kept = 700;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var content = new Dictionary<SaveId, long[]>();   // what each save file holds

        // What a node creates from one claim: primary = every cover file readable; otherwise the cover file of
        // `unreadable` is replaced by `fallback`, read through the cover file's filter.
        long[] Loads(RecoveryClaim claim, SaveId? unreadable = null, SaveCover? fallback = null) => claim.ResolveCover
            .SelectMany(item => content[item.Save.SaveId == unreadable ? fallback!.Save.SaveId : item.Save.SaveId]
                .Where(entity => item.EntityIds == null || item.EntityIds.Contains(entity)))
            .Distinct().Order().ToArray();

        // Holder death: n1 dies, n2 recovers the named partitions. Every entity of `expected` must be created
        // exactly once by the primary covers, and no fallback may create an entity outside its own partition.
        void RecoverAfterHolderDeath(GatewayRegistry registry, NodeRegistration n2, string what, Dictionary<ulong, long[]> expected,
            string? keptFallbacks = null)
        {
            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(n2.Node, n2.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            registry.ExpireLeases();
            Check(registry.Nodes().Single(x => x.Node == "n1").State == NodeState.Dead, what + ": the holder must be declared dead");
            var created = new List<long>();
            foreach ((ulong partition, long[] members) in expected.OrderBy(x => x.Key))
            {
                Check(registry.GetPartitionOwner(partition).Node == null, $"{what}: partition {partition} must be unowned after the holder died");
                RecoveryClaim claim = registry.ClaimRecoveries(n2.Node, n2.Epoch, [partition]).Single();
                long[] loaded = Loads(claim);
                Console.WriteLine($"split-keeps-input-id: {what}: partition {partition} cover [{Show(claim.ResolveCover)}] fallbacks [{Show(claim.Fallbacks)}] loads [{string.Join(",", loaded)}]");
                Check(loaded.SequenceEqual(members), $"{what}: partition {partition} recovers [{string.Join(",", loaded)}], its members are [{string.Join(",", members)}]");
                created.AddRange(loaded);
                if (partition == Kept && keptFallbacks != null)
                    Check(Show(claim.Fallbacks) == keptFallbacks, $"{what}: the kept id's fallbacks are [{Show(claim.Fallbacks)}], expected [{keptFallbacks}]");
                foreach (SaveCover item in claim.ResolveCover)
                    foreach (SaveCover fallback in claim.Fallbacks.Where(x => x.Save.SaveId.Partition == item.Save.SaveId.Partition))
                    {
                        long[] stale = Loads(claim, item.Save.SaveId, fallback);
                        Check(stale.All(members.Contains), $"{what}: partition {partition} falling back from {item.Save.SaveId} to {fallback.Save.SaveId} "
                            + $"creates [{string.Join(",", stale)}], beyond its members [{string.Join(",", members)}]");
                    }
            }
            long[] all = expected.Values.SelectMany(x => x).Order().ToArray();
            Check(created.Order().SequenceEqual(all), $"{what}: recovery creates [{string.Join(",", created.Order())}], every entity exactly once is [{string.Join(",", all)}]");
        }

        // One world per arm: 700 holds 1, 2, 3 and is saved (S0); 3 leaves by a split that keeps 700 for [1, 2].
        void Arm(string what, Action<GatewayRegistry, NodeRegistration, NodeRegistration, long, ulong> body)
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-split-keeps-").FullName;
            now = DateTimeOffset.UnixEpoch;
            content.Clear();
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                NodeRegistration n2 = Register(registry, "n2", "two", Guid.NewGuid());
                long generation = registry.ClaimPartition(Kept, n1.Node, n1.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(Kept, generation, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                content[new SaveId(Kept, generation, 1)] = [1, 2, 3];
                ulong piece = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
                LineageRecord split = registry.ApplyLineage(what + "-split", LineageKind.Split, n1.Node, n1.Epoch, [new(Kept, generation)],
                    [Kept, piece], new() { [Kept] = [1, 2], [piece] = [3] }, 200);
                Check(split.Outputs.Select(x => (x.Partition, x.Generation)).SequenceEqual([(Kept, generation + 1), (piece, 1L)]),
                    what + ": the kept id must advance one generation, the piece starts at 1");
                body(registry, n1, n2, generation, piece);
            }
            finally { Directory.Delete(directory, true); }
        }

        // 1. The registry's own bookkeeping of an id that is input and output of one split.
        Arm("bookkeeping", (registry, n1, n2, generation, piece) =>
        {
            PartitionOwnership owner = registry.GetPartitionOwner(Kept);
            Check(owner.Node == n1.Node && owner.Generation == generation + 1, "the kept id stays owned, one generation on");
            Check(registry.Partitions().Single(x => x.Partition == Kept).Node == n1.Node && registry.ResolveCover(Kept).Length != 0, "the kept id is live, not retired");
            // The old generation is fenced: a capture taken before the split (it holds 3 as well) cannot be
            // catalogued under the kept id afterwards, and nothing of the old generation can release or hand it over.
            AssertConflict(() => registry.RegisterSave(new SaveId(Kept, generation, 2), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "late", 150, "n1/late"));
            AssertConflict(() => registry.ReleasePartition(Kept, n1.Node, n1.Epoch, generation));
            AssertConflict(() => registry.Begin("stale", Kept, n1.Node, n1.Epoch, generation, n2.Node, n2.Epoch));
            // The pre-split save is the state of [1, 2] under the kept id and of [3] under the piece - never of all three.
            Check(Show(registry.ResolveCover(Kept)) == $"{Kept}/{generation}/1:1,2", "the kept id's cover is the pre-split save cut to its members: " + Show(registry.ResolveCover(Kept)));
            Check(Show(registry.ResolveCover(piece)) == $"{Kept}/{generation}/1:3", "the piece's cover is the pre-split save cut to its members: " + Show(registry.ResolveCover(piece)));
            // Retention keeps the pre-split save for as long as the piece has no save of its own.
            registry.RegisterSave(new SaveId(Kept, generation + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1");
            Check(Show(registry.ResolveCover(Kept)) == $"{Kept}/{generation + 1}/1:*", "a save of the new generation is the kept id's whole state");
            Check(registry.PlanRetention(1, 0).All(x => x.SaveId != new SaveId(Kept, generation, 1)), "the pre-split save was planned for deletion while the piece still recovers from it");
            Check(!registry.Lineage().Single().Collapsed, "the split collapsed while one output has no save");
            registry.RegisterSave(new SaveId(piece, 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "p1", 310, "n1/p1");
            Check(registry.Lineage().Single().Collapsed, "the split must collapse once both outputs are saved");
            Check(registry.PlanRetention(1, 0).Any(x => x.SaveId == new SaveId(Kept, generation, 1)), "the pre-split save must be released once nothing recovers from it");
        });

        // 2. Client placement and character homes follow the body; the kept id is never followed as retired.
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-split-keeps-").FullName;
            now = DateTimeOffset.UnixEpoch;
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                long generation = registry.ClaimPartition(Kept, n1.Node, n1.Epoch, 0).Generation;
                ReportCharacterHome(registry, 42, 7, 3, Kept, "n1");   // the body that leaves
                ReportCharacterHome(registry, 43, 8, 1, Kept, "n1");   // the body that stays
                registry.RouteClient(42, Kept, 3);
                registry.RouteClient(43, Kept, 1);
                ulong piece = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
                registry.ApplyLineage("routing", LineageKind.Split, n1.Node, n1.Epoch, [new(Kept, generation)],
                    [Kept, piece], new() { [Kept] = [1, 2], [piece] = [3] }, 200);
                Check(registry.GetCharacterHome(42) is { Partition: var left } && left == piece, "the home of the body that left must follow it to the piece");
                Check(registry.GetCharacterHome(43) is { Partition: Kept }, "the home of the body that stayed must stay on the kept id");
                Check(registry.GetClientPartition(42)?.Partition == piece && registry.GetClientPartition(43)?.Partition == Kept, "placements must follow the bodies");
                Check(registry.ResolveClientRoute(42) is { Node: "n1" } leaver && leaver.Partition == piece, "the leaver routes to the piece");
                Check(registry.ResolveClientRoute(43) is { Partition: Kept, Node: "n1" }, "the stayer routes to the kept id");
            }
            finally { Directory.Delete(directory, true); }
        }

        // 3. The holder dies before anything was saved after the split.
        Arm("death before a post-split save", (registry, n1, n2, generation, piece) =>
            RecoverAfterHolderDeath(registry, n2, "death before a post-split save", new() { [Kept] = [1, 2], [piece] = [3] }));

        // 4. The kept id was saved after the split; that save is readable. The older, pre-split save must not be
        //    offered as a fallback that restores 3 a second time.
        Arm("death after a post-split save", (registry, n1, n2, generation, piece) =>
        {
            registry.RegisterSave(new SaveId(Kept, generation + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1");
            content[new SaveId(Kept, generation + 1, 1)] = [1, 2];
            RecoverAfterHolderDeath(registry, n2, "death after a post-split save", new() { [Kept] = [1, 2], [piece] = [3] }, keptFallbacks: "");
        });

        //    Control: an older save from AFTER the split is still offered as the fallback it always was.
        Arm("two post-split saves", (registry, n1, n2, generation, piece) =>
        {
            for (int sequence = 1; sequence <= 2; sequence++)
            {
                registry.RegisterSave(new SaveId(Kept, generation + 1, sequence), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s" + sequence, 300 + sequence, "n1/s" + sequence);
                content[new SaveId(Kept, generation + 1, sequence)] = [1, 2];
            }
            RecoverAfterHolderDeath(registry, n2, "two post-split saves", new() { [Kept] = [1, 2], [piece] = [3] },
                keptFallbacks: $"{Kept}/{generation + 1}/1:*");
        });

        // 5. The post-split save died with its only holder (it names the dead node's endpoint); the pre-split save
        //    is the newest usable one of the kept id and must still be cut to [1, 2].
        Arm("post-split save lost with the holder", (registry, n1, n2, generation, piece) =>
        {
            registry.RegisterSave(new SaveId(Kept, generation + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1", "127.0.0.1:1");
            content[new SaveId(Kept, generation + 1, 1)] = [1, 2];
            RecoverAfterHolderDeath(registry, n2, "post-split save lost with the holder", new() { [Kept] = [1, 2], [piece] = [3] });
        });

        // 6. The post-split save is reported corrupt on its holder: the same.
        Arm("post-split save corrupt", (registry, n1, n2, generation, piece) =>
        {
            registry.RegisterSave(new SaveId(Kept, generation + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1");
            content[new SaveId(Kept, generation + 1, 1)] = [1, 2];
            registry.ReportCorruption(new SaveId(Kept, generation + 1, 1), n1.Node, n2.Node, n2.Epoch);
            RecoverAfterHolderDeath(registry, n2, "post-split save corrupt", new() { [Kept] = [1, 2], [piece] = [3] });
        });

        //    The piece merges into a saved neighbour in the pass that split it off (1 ms later, nothing saved in between).
        Arm("piece merged in the same pass", (registry, n1, n2, generation, piece) =>
        {
            long neighbour = registry.ClaimPartition(702, n1.Node, n1.Epoch, 0).Generation;
            registry.RegisterSave(new SaveId(702, neighbour, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "sz", 120, "n1/sz");
            content[new SaveId(702, neighbour, 1)] = [50];
            registry.ApplyLineage("same-pass-merge", LineageKind.Merge, n1.Node, n1.Epoch, [new(702, neighbour), new(piece, 1)], [702], null, 201);
            RecoverAfterHolderDeath(registry, n2, "piece merged in the same pass", new() { [Kept] = [1, 2], [702] = [3, 50] });
        });

        // 7. A chain with no save in between: split again keeping the id, merge a saved neighbour in, split a
        //    third time. Then the same after a restart of the registry (the lineage indexes are rebuilt).
        Arm("chain", (registry, n1, n2, generation, piece) =>
        {
            ulong second = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
            registry.ApplyLineage("chain-2", LineageKind.Split, n1.Node, n1.Epoch, [new(Kept, generation + 1)],
                [Kept, second], new() { [Kept] = [1], [second] = [2] }, 400);
            Check(Show(registry.ResolveCover(Kept)) == $"{Kept}/{generation}/1:1" && Show(registry.ResolveCover(second)) == $"{Kept}/{generation}/1:2"
                && Show(registry.ResolveCover(piece)) == $"{Kept}/{generation}/1:3", "two splits keeping the id: each output is the pre-split save cut to its members");
            long neighbour = registry.ClaimPartition(701, n1.Node, n1.Epoch, 0).Generation;
            registry.RegisterSave(new SaveId(701, neighbour, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "sy", 120, "n1/sy");
            content[new SaveId(701, neighbour, 1)] = [9, 10];
            LineageRecord merge = registry.ApplyLineage("chain-merge", LineageKind.Merge, n1.Node, n1.Epoch,
                [new(Kept, generation + 2), new(701, neighbour)], [Kept], null, 500);
            ulong third = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
            LineageRecord last = registry.ApplyLineage("chain-3", LineageKind.Split, n1.Node, n1.Epoch, [merge.Outputs[0]],
                [Kept, third], new() { [Kept] = [1, 9], [third] = [10] }, 600);
            Check(last.Outputs[0] == new PartitionVersion(Kept, generation + 4), "every lineage operation advances the kept id's generation");
            RecoverAfterHolderDeath(registry, n2, "chain", new() { [Kept] = [1, 9], [piece] = [3], [second] = [2], [third] = [10] });
        });
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-split-keeps-").FullName;
            now = DateTimeOffset.UnixEpoch;
            try
            {
                long generation;
                ulong piece;
                using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
                {
                    NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                    generation = registry.ClaimPartition(Kept, n1.Node, n1.Epoch, 0).Generation;
                    registry.RegisterSave(new SaveId(Kept, generation, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                    piece = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
                    registry.ApplyLineage("restart", LineageKind.Split, n1.Node, n1.Epoch, [new(Kept, generation)],
                        [Kept, piece], new() { [Kept] = [1, 2], [piece] = [3] }, 200);
                    registry.Checkpoint();
                }
                using var reloaded = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                Check(Show(reloaded.ResolveCover(Kept)) == $"{Kept}/{generation}/1:1,2" && Show(reloaded.ResolveCover(piece)) == $"{Kept}/{generation}/1:3",
                    "after a registry restart the covers must be the same: " + Show(reloaded.ResolveCover(Kept)) + " | " + Show(reloaded.ResolveCover(piece)));
                Check(reloaded.GetPartitionOwner(Kept).Generation == generation + 1, "the kept id's generation must survive the restart");
            }
            finally { Directory.Delete(directory, true); }
        }

        // 8. Control: the split as it was (two new ids, the input retired) recovers the same entities. If this arm
        //    failed, the fixture's replay of a recovery would be what is wrong, not the kept id.
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-split-keeps-").FullName;
            now = DateTimeOffset.UnixEpoch;
            content.Clear();
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                NodeRegistration n2 = Register(registry, "n2", "two", Guid.NewGuid());
                long generation = registry.ClaimPartition(Kept, n1.Node, n1.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(Kept, generation, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                content[new SaveId(Kept, generation, 1)] = [1, 2, 3];
                ulong[] fresh = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 2);
                registry.ApplyLineage("control", LineageKind.Split, n1.Node, n1.Epoch, [new(Kept, generation)],
                    fresh, new() { [fresh[0]] = [1, 2], [fresh[1]] = [3] }, 200);
                RecoverAfterHolderDeath(registry, n2, "control (two new ids)", new() { [fresh[0]] = [1, 2], [fresh[1]] = [3] });
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
