namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0339 (design C): a TRANSFER moves members from one partition to another in one lineage operation - a split
    /// with two inputs [source, target] whose outputs are the same two ids, each one generation on, with both member
    /// lists. No id is born and none retired. The invariants: both outputs stay owned and live; an exact replay of the
    /// op id answers the recorded outputs and anything else under that id is refused; a stale generation of either
    /// input is fenced; a recovery of either id after the transfer restores every entity exactly once (the moved ones
    /// through the target, never again through a pre-transfer save of the source); clients and homes on a moved body
    /// follow it to the target and nobody else moves.
    /// <para>
    /// A df registry refuses the request with "Split requires one input" (an ArgumentException, HTTP 400): that is the
    /// answer the node falls back on (PartitionPersistence.ApplyTopology), and why this section is red on df.
    /// </para>
    /// </summary>
    private static void TestTopologyTransfer()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("topology-transfer: " + what); }
        static string Show(IEnumerable<SaveCover> cover) => string.Join(" + ", cover.Select(x =>
            $"{x.Save.SaveId.Partition}/{x.Save.SaveId.Generation}/{x.Save.SaveId.Sequence}:{(x.EntityIds == null ? "*" : string.Join(",", x.EntityIds))}"));
        const ulong Source = 700, Target = 702;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var content = new Dictionary<SaveId, long[]>();   // what each save file holds

        long[] Loads(RecoveryClaim claim, SaveId? unreadable = null, SaveCover? fallback = null) => claim.ResolveCover
            .SelectMany(item => content[item.Save.SaveId == unreadable ? fallback!.Save.SaveId : item.Save.SaveId]
                .Where(entity => item.EntityIds == null || item.EntityIds.Contains(entity)))
            .Distinct().Order().ToArray();

        // Holder death: n1 dies, n2 recovers the named partitions; every entity exactly once, no fallback beyond its partition.
        void RecoverAfterHolderDeath(GatewayRegistry registry, NodeRegistration n2, string what, Dictionary<ulong, long[]> expected)
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
                Console.WriteLine($"topology-transfer: {what}: partition {partition} cover [{Show(claim.ResolveCover)}] fallbacks [{Show(claim.Fallbacks)}] loads [{string.Join(",", loaded)}]");
                Check(loaded.SequenceEqual(members), $"{what}: partition {partition} recovers [{string.Join(",", loaded)}], its members are [{string.Join(",", members)}]");
                created.AddRange(loaded);
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

        LineageRecord Transfer(GatewayRegistry registry, NodeRegistration node, string op, PartitionVersion source, PartitionVersion target,
            long[] sourceMembers, long[] targetMembers, long clusterTime, Dictionary<ulong, long[]>? seated = null) =>
            registry.ApplyLineage(op, LineageKind.Split, node.Node, node.Epoch, [source, target], [source.Partition, target.Partition],
                new() { [source.Partition] = sourceMembers, [target.Partition] = targetMembers }, clusterTime, seated);

        // One world per arm: 700 holds 1, 2, 3 (save S0) and 702 holds 50, 51 (save T0); 3 moves 700 -> 702 at 200.
        void Arm(string what, Action<GatewayRegistry, NodeRegistration, NodeRegistration, long, long, LineageRecord> body)
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-transfer-").FullName;
            now = DateTimeOffset.UnixEpoch;
            content.Clear();
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                NodeRegistration n2 = Register(registry, "n2", "two", Guid.NewGuid());
                long source = registry.ClaimPartition(Source, n1.Node, n1.Epoch, 0).Generation;
                long target = registry.ClaimPartition(Target, n1.Node, n1.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(Source, source, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                content[new SaveId(Source, source, 1)] = [1, 2, 3];
                registry.RegisterSave(new SaveId(Target, target, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t0", 110, "n1/t0");
                content[new SaveId(Target, target, 1)] = [50, 51];
                LineageRecord transfer = Transfer(registry, n1, what + "-transfer", new(Source, source), new(Target, target), [1, 2], [3, 50, 51], 200);
                body(registry, n1, n2, source, target, transfer);
            }
            finally { Directory.Delete(directory, true); }
        }

        // 1. Bookkeeping: both ids live and owned, one generation on, no id allocated or retired; replay and refusals.
        Arm("bookkeeping", (registry, n1, n2, source, target, transfer) =>
        {
            Check(transfer.Outputs.SequenceEqual([new PartitionVersion(Source, source + 1), new PartitionVersion(Target, target + 1)]),
                "both ids must come out one generation on, in input order: " + string.Join(",", transfer.Outputs));
            foreach ((ulong id, long generation) in new[] { (Source, source + 1), (Target, target + 1) })
            {
                PartitionOwnership owner = registry.GetPartitionOwner(id);
                Check(owner.Node == n1.Node && owner.Generation == generation, $"{id} stays owned by n1 at generation {generation}");
                Check(registry.Partitions().Single(x => x.Partition == id).Node == n1.Node, $"{id} is live, not retired");
            }
            // T-0308 replay: the same op id, inputs, outputs, members and time answer the recorded record - nothing moves again.
            LineageRecord replay = Transfer(registry, n1, "bookkeeping-transfer", new(Source, source), new(Target, target), [1, 2], [3, 50, 51], 200);
            Check(replay.Outputs.SequenceEqual(transfer.Outputs) && registry.GetPartitionOwner(Target).Generation == target + 1
                && registry.Lineage().Length == 1, "an exact replay must answer the recorded outputs and change nothing");
            AssertConflict(() => Transfer(registry, n1, "bookkeeping-transfer", new(Source, source), new(Target, target), [1], [2, 3, 50, 51], 200));
            // Stale generations of either input are fenced, as for every lineage operation.
            AssertConflict(() => Transfer(registry, n1, "stale-source", new(Source, source), new(Target, target + 1), [1], [2, 3, 50, 51], 300));
            AssertConflict(() => Transfer(registry, n1, "stale-target", new(Source, source + 1), new(Target, target), [1], [2, 3, 50, 51], 300));
            AssertConflict(() => registry.RegisterSave(new SaveId(Source, source, 2), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "late", 150, "n1/late"));
            AssertConflict(() => registry.RegisterSave(new SaveId(Target, target, 2), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "late-t", 150, "n1/late-t"));
            // Malformed transfers: an output that is not an input, overlapping or empty member lists, a missing list, three inputs.
            ulong fresh = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
            PartitionVersion s1 = new(Source, source + 1), t1 = new(Target, target + 1);
            AssertArgument(() => registry.ApplyLineage("bad-output", LineageKind.Split, n1.Node, n1.Epoch, [s1, t1], [Source, fresh],
                new() { [Source] = [1], [fresh] = [2] }, 300));
            AssertArgument(() => registry.ApplyLineage("bad-order", LineageKind.Split, n1.Node, n1.Epoch, [s1, t1], [Target, Source],
                new() { [Source] = [1], [Target] = [2, 3, 50, 51] }, 300));
            AssertArgument(() => Transfer(registry, n1, "overlap", s1, t1, [1, 2], [2, 3, 50, 51], 300));
            AssertArgument(() => Transfer(registry, n1, "empty-source", s1, t1, [], [1, 2, 3, 50, 51], 300));
            AssertArgument(() => registry.ApplyLineage("missing-list", LineageKind.Split, n1.Node, n1.Epoch, [s1, t1], [Source, Target],
                new() { [Source] = [1] }, 300));
            long third = registry.ClaimPartition(704, n1.Node, n1.Epoch, 0).Generation;
            AssertArgument(() => registry.ApplyLineage("three", LineageKind.Split, n1.Node, n1.Epoch, [s1, t1, new(704, third)], [Source, Target, 704],
                new() { [Source] = [1], [Target] = [2, 3, 50, 51], [704] = [9] }, 300));
            Check(registry.GetPartitionOwner(Source).Generation == source + 1 && registry.GetPartitionOwner(Target).Generation == target + 1,
                "a refused transfer must not advance any generation");
            // Covers: the source is its pre-transfer save cut to what stayed; the target its own save plus the moved member.
            Check(Show(registry.ResolveCover(Source)) == $"{Source}/{source}/1:1,2", "the source's cover is its pre-transfer save cut to [1,2]: " + Show(registry.ResolveCover(Source)));
            Check(Show(registry.ResolveCover(Target)) == $"{Source}/{source}/1:3,50,51 + {Target}/{target}/1:3,50,51",
                "the target's cover is the source's pre-transfer save for 3 and its own for 50, 51: " + Show(registry.ResolveCover(Target)));
            // Retention keeps the source's pre-transfer save while the target recovers 3 from it; the record collapses once both are saved.
            registry.RegisterSave(new SaveId(Source, source + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1");
            Check(Show(registry.ResolveCover(Source)) == $"{Source}/{source + 1}/1:*", "a post-transfer save is the source's whole state");
            Check(registry.PlanRetention(1, 0).All(x => x.SaveId != new SaveId(Source, source, 1)), "the source's pre-transfer save was planned for deletion while the target still recovers 3 from it");
            Check(!registry.Lineage().Single(x => x.OpId == "bookkeeping-transfer").Collapsed, "the transfer collapsed while the target has no post-transfer save");
            registry.RegisterSave(new SaveId(Target, target + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t1", 310, "n1/t1");
            Check(Show(registry.ResolveCover(Target)) == $"{Target}/{target + 1}/1:*", "a post-transfer save is the target's whole state");
            Check(registry.Lineage().Single(x => x.OpId == "bookkeeping-transfer").Collapsed, "the transfer must collapse once both outputs are saved");
            Check(registry.PlanRetention(1, 0).Any(x => x.SaveId == new SaveId(Source, source, 1)), "the source's pre-transfer save must be released once nothing recovers from it");
        });

        // 2. Client placement and homes: the moved body (standing or seated in the moved ship) goes to the target; a
        //    client whose body is in neither member list stays on its own partition (a split sends it to the most populated output).
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-transfer-").FullName;
            now = DateTimeOffset.UnixEpoch;
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                long source = registry.ClaimPartition(Source, n1.Node, n1.Epoch, 0).Generation;
                long target = registry.ClaimPartition(Target, n1.Node, n1.Epoch, 0).Generation;
                ReportCharacterHome(registry, 42, 7, 3, Source, "n1");     // standing body that moves
                ReportCharacterHome(registry, 43, 8, 1, Source, "n1");     // body that stays
                ReportCharacterHome(registry, 44, 9, 88, Source, "n1");    // pilot seated in the moved ship 3
                ReportCharacterHome(registry, 45, 10, 99, Source, "n1");   // a body in no member list (moved in the same tick)
                ReportCharacterHome(registry, 46, 11, 77, Target, "n1");   // seated in the target's grid 50, no seat hint
                foreach ((ulong client, long entity, ulong partition) in new[] { (42UL, 3L, Source), (43UL, 1L, Source), (44UL, 88L, Source), (45UL, 99L, Source), (46UL, 77L, Target) })
                    registry.RouteClient(client, partition, entity);
                Transfer(registry, n1, "routing", new(Source, source), new(Target, target), [1, 2], [3, 50, 51], 200, new() { [Target] = [88] });
                foreach ((ulong client, ulong expected, string what) in new[] { (42UL, Target, "the moved body"), (43UL, Source, "the body that stayed"),
                    (44UL, Target, "the pilot seated in the moved ship"), (45UL, Source, "a body in no list (stays, not the most populated output)"),
                    (46UL, Target, "a target client in no list") })
                {
                    Check(registry.GetCharacterHome(client)?.Partition == expected, $"{what}: home {registry.GetCharacterHome(client)?.Partition}, expected {expected}");
                    Check(registry.GetClientPartition(client)?.Partition == expected, $"{what}: placement {registry.GetClientPartition(client)?.Partition}, expected {expected}");
                    Check(registry.ResolveClientRoute(client) is { Node: "n1" } route && route.Partition == expected, $"{what}: routes to {expected}");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        // 3. The holder dies before anything was saved after the transfer.
        Arm("death before a post-transfer save", (registry, n1, n2, source, target, transfer) =>
            RecoverAfterHolderDeath(registry, n2, "death before a post-transfer save", new() { [Source] = [1, 2], [Target] = [3, 50, 51] }));

        // 4. Only the source was saved after the transfer: the target still takes 3 from the source's pre-transfer save.
        Arm("death after a post-transfer source save", (registry, n1, n2, source, target, transfer) =>
        {
            registry.RegisterSave(new SaveId(Source, source + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s1", 300, "n1/s1");
            content[new SaveId(Source, source + 1, 1)] = [1, 2];
            RecoverAfterHolderDeath(registry, n2, "death after a post-transfer source save", new() { [Source] = [1, 2], [Target] = [3, 50, 51] });
        });

        // 5. Only the target was saved after the transfer: the source's pre-transfer save is cut to [1, 2], never 3 again.
        Arm("death after a post-transfer target save", (registry, n1, n2, source, target, transfer) =>
        {
            registry.RegisterSave(new SaveId(Target, target + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t1", 300, "n1/t1");
            content[new SaveId(Target, target + 1, 1)] = [3, 50, 51];
            RecoverAfterHolderDeath(registry, n2, "death after a post-transfer target save", new() { [Source] = [1, 2], [Target] = [3, 50, 51] });
        });

        // 6. The target's post-transfer save is corrupt: its pre-transfer save is not a fallback that loses 3; the walk
        //    through the transfer restores 3 from the source exactly once.
        Arm("post-transfer target save corrupt", (registry, n1, n2, source, target, transfer) =>
        {
            registry.RegisterSave(new SaveId(Target, target + 1, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t1", 300, "n1/t1");
            content[new SaveId(Target, target + 1, 1)] = [3, 50, 51];
            registry.ReportCorruption(new SaveId(Target, target + 1, 1), n1.Node, n2.Node, n2.Epoch);
            RecoverAfterHolderDeath(registry, n2, "post-transfer target save corrupt", new() { [Source] = [1, 2], [Target] = [3, 50, 51] });
        });

        // 7. Two transfers in a row (2 follows 3, nothing saved in between), and the ship going back (3 returns 702 -> 700).
        Arm("two transfers", (registry, n1, n2, source, target, transfer) =>
        {
            Transfer(registry, n1, "second", transfer.Outputs[0], transfer.Outputs[1], [1], [2, 3, 50, 51], 201);
            RecoverAfterHolderDeath(registry, n2, "two transfers", new() { [Source] = [1], [Target] = [2, 3, 50, 51] });
        });
        Arm("transfer and back", (registry, n1, n2, source, target, transfer) =>
        {
            Transfer(registry, n1, "back", transfer.Outputs[1], transfer.Outputs[0], [50, 51], [1, 2, 3], 201);
            RecoverAfterHolderDeath(registry, n2, "transfer and back", new() { [Source] = [1, 2, 3], [Target] = [50, 51] });
        });

        // 8. After a registry restart (the lineage indexes are rebuilt) the covers are the same.
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-transfer-").FullName;
            now = DateTimeOffset.UnixEpoch;
            try
            {
                long source, target;
                using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
                {
                    NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                    source = registry.ClaimPartition(Source, n1.Node, n1.Epoch, 0).Generation;
                    target = registry.ClaimPartition(Target, n1.Node, n1.Epoch, 0).Generation;
                    registry.RegisterSave(new SaveId(Source, source, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                    registry.RegisterSave(new SaveId(Target, target, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t0", 110, "n1/t0");
                    Transfer(registry, n1, "restart", new(Source, source), new(Target, target), [1, 2], [3, 50, 51], 200);
                    registry.Checkpoint();
                }
                using var reloaded = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                Check(Show(reloaded.ResolveCover(Source)) == $"{Source}/{source}/1:1,2"
                    && Show(reloaded.ResolveCover(Target)) == $"{Source}/{source}/1:3,50,51 + {Target}/{target}/1:3,50,51",
                    "after a registry restart the covers must be the same: " + Show(reloaded.ResolveCover(Source)) + " | " + Show(reloaded.ResolveCover(Target)));
                Check(reloaded.GetPartitionOwner(Source).Generation == source + 1 && reloaded.GetPartitionOwner(Target).Generation == target + 1,
                    "both generations must survive the restart");
            }
            finally { Directory.Delete(directory, true); }
        }

        // 9. Control: the same move as df made it (split with a new id, merge in the next millisecond) recovers the same
        //    entities - if this arm failed, the fixture's replay of a recovery would be what is wrong.
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-transfer-").FullName;
            now = DateTimeOffset.UnixEpoch;
            content.Clear();
            try
            {
                using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
                NodeRegistration n2 = Register(registry, "n2", "two", Guid.NewGuid());
                long source = registry.ClaimPartition(Source, n1.Node, n1.Epoch, 0).Generation;
                long target = registry.ClaimPartition(Target, n1.Node, n1.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(Source, source, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "s0", 100, "n1/s0");
                content[new SaveId(Source, source, 1)] = [1, 2, 3];
                registry.RegisterSave(new SaveId(Target, target, 1), n1.Node, n1.Epoch, SaveKind.Periodic, 1, "t0", 110, "n1/t0");
                content[new SaveId(Target, target, 1)] = [50, 51];
                ulong piece = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 1).Single();
                LineageRecord split = registry.ApplyLineage("control-split", LineageKind.Split, n1.Node, n1.Epoch, [new(Source, source)],
                    [Source, piece], new() { [Source] = [1, 2], [piece] = [3] }, 200);
                registry.ApplyLineage("control-merge", LineageKind.Merge, n1.Node, n1.Epoch, [new(Target, target), split.Outputs[1]], [Target], null, 201);
                RecoverAfterHolderDeath(registry, n2, "control (split and merge)", new() { [Source] = [1, 2], [Target] = [3, 50, 51] });
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
