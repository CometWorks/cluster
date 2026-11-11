namespace ClusterRegistry;

/// <summary>SE-1346 global save retention (GlobalRetention.cs).</summary>
internal static partial class SelfTest
{
    /// <summary>
    /// A World Authority writes 30 global saves, each copied to node-1. Retention keeps the newest 10 on both holders
    /// (each holder is handed exactly its own copies, the ack removes the row) and recovery still finds the newest. A
    /// snapshot's global and a registry backup's catalogue are kept; nothing at or above the newest fully held save goes,
    /// and with no fully held save nothing goes at all. The removals survive a restart, and a replica pump keeps handing
    /// out the newest global after rows were removed. Earned reds: without the floor the unreplicated 26..30 tail loses
    /// its older fully held saves; without the snapshot pin the rollback target is deleted.
    /// </summary>
    private static void TestGlobalRetention()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "cluster-gateway-global-retention-self-test-" + Guid.NewGuid()
        );
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        int keep = GatewayRegistry.GlobalRetentionKeep;
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("global-retention: " + what);
        }
        try
        {
            GatewayRegistry.GlobalRetentionKeep = 10;
            NodeRegistration authority,
                node1,
                node2;
            long generation;
            using (
                var registry = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromDays(1)
                )
            )
            {
                authority = registry.RegisterNode(
                    Join("wa-1", "wa-one", Guid.NewGuid()) with
                    {
                        Role = NodeRole.WA,
                        ControlEndpoint = "http://127.0.0.1:29519",
                    }
                );
                node1 = registry.RegisterNode(
                    Join("node-1", "node-1", Guid.NewGuid()) with
                    {
                        ControlEndpoint = "http://127.0.0.1:29521",
                    }
                );
                node2 = registry.RegisterNode(
                    Join("node-2", "node-2", Guid.NewGuid()) with
                    {
                        ControlEndpoint = "http://127.0.0.1:29522",
                    }
                );
                generation = authority.AuthorityGeneration;
                GlobalSaveRegistration Global(int sequence, bool replicate)
                {
                    now += TimeSpan.FromSeconds(1);
                    GlobalSaveRegistration save = registry.RegisterGlobalSave(
                        new(generation, sequence),
                        authority.Node,
                        authority.Epoch,
                        SaveKind.Periodic,
                        40 + sequence,
                        "c" + sequence,
                        100 + sequence,
                        "wa/GLOBAL/" + sequence,
                        authority.ControlEndpoint!
                    );
                    if (replicate)
                        registry.RegisterGlobalHolder(
                            save.SaveId,
                            node1.Node,
                            node1.Epoch,
                            save.SizeBytes,
                            save.Checksum,
                            "node-1/GLOBAL/" + sequence,
                            node1.ControlEndpoint!
                        );
                    return save;
                }
                string[] Held(string node) =>
                    registry
                        .GlobalSaves()
                        .SelectMany(save => save.LocationHolders.Where(x => x.Value == node))
                        .Select(x => x.Key)
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                string[] Expect(string prefix, params int[] sequences) =>
                    sequences.Select(x => prefix + x).Order(StringComparer.Ordinal).ToArray();
                void AckAll(params NodeRegistration[] holders)
                {
                    foreach (NodeRegistration holder in holders)
                    foreach (
                        DeleteDirective deletion in registry.PendingDeletes(
                            holder.Node,
                            holder.Epoch
                        )
                    )
                        Check(
                            deletion.Global
                                && deletion.SaveId.Partition == 0
                                && registry.AcknowledgeDelete(deletion.SaveId, deletion.Location),
                            "a global directive carries partition 0 and its ack is taken"
                        );
                }

                // No fully held save (only the World Authority holds them): nothing goes.
                for (int sequence = 1; sequence <= 12; sequence++)
                    Global(sequence, replicate: false);
                registry.PlanRetention();
                Check(
                    registry.GlobalSaves().Length == 12
                        && !registry.GlobalSaves().Any(x => x.PendingDelete),
                    "with no global save on two holders nothing is released"
                );

                // Replicated: 13..30 on two holders. 1..20 go, 21..30 stay on both.
                for (int sequence = 13; sequence <= 30; sequence++)
                    Global(sequence, replicate: true);
                DeleteDirective[] planned = registry.PlanRetention();
                Check(
                    planned.Count(x => x.Global) == 20 + 8,
                    "20 World Authority copies and 8 node-1 copies (13..20) are released"
                );
                DeleteDirective[] waDeletes = registry.PendingDeletes(
                    authority.Node,
                    authority.Epoch
                );
                Check(
                    waDeletes.Length == 20
                        && waDeletes.All(x =>
                            x.Location.StartsWith("wa/", StringComparison.Ordinal)
                        ),
                    "the World Authority is handed exactly its own 20 copies"
                );
                Check(
                    registry
                        .PendingDeletes(node1.Node, node1.Epoch)
                        .All(x => x.Location.StartsWith("node-1/", StringComparison.Ordinal)),
                    "node-1 is handed only its own copies"
                );
                Check(
                    registry.PendingDeletes(node2.Node, node2.Epoch).Length == 0,
                    "node-2 holds nothing"
                );
                Check(
                    registry
                        .ReplicaWork(node2.Node, node2.Epoch)
                        .All(x => !x.Global || x.SaveId.Sequence > 20),
                    "a released global is never copied again"
                );
                AckAll(authority, node1);
                Check(
                    registry.GlobalSaves().Length == 10,
                    "the acks remove the released rows: 10 global saves left"
                );
                Check(
                    Held(authority.Node)
                        .SequenceEqual(
                            Expect("wa/GLOBAL/", 21, 22, 23, 24, 25, 26, 27, 28, 29, 30)
                        ),
                    "the World Authority keeps 21..30"
                );
                Check(
                    Held(node1.Node)
                        .SequenceEqual(
                            Expect("node-1/GLOBAL/", 21, 22, 23, 24, 25, 26, 27, 28, 29, 30)
                        ),
                    "node-1 keeps 21..30"
                );
                Check(
                    registry.NewestGlobalSaveForRecovery() is { SaveId.Sequence: 30 },
                    "recovery finds the newest global save"
                );
                Check(
                    !registry.AcknowledgeDelete(new SaveId(0, generation, 5), "wa/GLOBAL/5"),
                    "a second ack of a removed row answers false"
                );

                // A rollback target: a snapshot names 22. A registry backup catalogues 21..30 while it runs and after.
                SnapshotRegistration snapshot = registry.CreateSnapshot(
                    "rollback-22",
                    SnapshotKind.Online,
                    122,
                    [],
                    [],
                    new GlobalSaveId(generation, 22)
                );
                registry.CopyStore(
                    Path.Combine(directory, "backup.db"),
                    "backup-1",
                    now + TimeSpan.FromHours(1)
                );
                // A replication backlog: 31..45 only on the World Authority. The floor is 30, so 31..35 stay although
                // they are past the newest 10, and so does everything the backup and the snapshot name.
                for (int sequence = 31; sequence <= 45; sequence++)
                    Global(sequence, replicate: false);
                registry.PlanRetention();
                Check(
                    !registry.GlobalSaves().Any(x => x.PendingDelete),
                    "the backup hold keeps 21..30, the floor keeps 31..35"
                );
                Check(registry.ReleaseBackupHold("backup-1"), "the hold is released");
                registry.PlanRetention();
                long[] pending = registry
                    .GlobalSaves()
                    .Where(x => x.PendingDelete)
                    .Select(x => x.SaveId.Sequence)
                    .Order()
                    .ToArray();
                Check(
                    pending.SequenceEqual([21L, 23, 24, 25, 26, 27, 28, 29]),
                    "released: 21..29 go but the snapshot's 22; 30 is the floor ("
                        + string.Join(",", pending)
                        + ")"
                );
                AckAll(authority, node1);
                Check(
                    registry
                        .GlobalSaves()
                        .Select(x => x.SaveId.Sequence)
                        .Order()
                        .SequenceEqual([
                            22L,
                            30,
                            31,
                            32,
                            33,
                            34,
                            35,
                            36,
                            37,
                            38,
                            39,
                            40,
                            41,
                            42,
                            43,
                            44,
                            45,
                        ]),
                    "left: the snapshot's, the floor and everything above it"
                );
                Check(
                    registry
                        .GlobalSaves()
                        .Any(x =>
                            x.SaveId == snapshot.GlobalRef
                            && !x.PendingDelete
                            && x.Locations.Count == 2
                        ),
                    "the rollback target's global save keeps both copies"
                );

                // The replica pump: after removals the newest global (45, one holder) is still handed out.
                Check(
                    registry
                        .ReplicaWork(node2.Node, node2.Epoch)
                        .Any(x => x.Global && x.SaveId.Sequence == 45),
                    "the newest global is copied after rows were removed"
                );
            }

            // A restart reads the same catalogue: the removed rows stay removed.
            using (
                var restarted = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromDays(1)
                )
            )
                Check(
                    restarted.GlobalSaves().Length == 17
                        && restarted.GlobalSaves().Any(x => x.SaveId.Sequence == 22),
                    "the removals and the snapshot's save survive a restart"
                );

            // Off: CLUSTER_RETENTION_GLOBAL_KEEP=0 keeps every global save.
            GatewayRegistry.GlobalRetentionKeep = 0;
            using (
                var off = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromDays(1))
            )
            {
                Check(
                    off.PlanRetention().All(x => !x.Global)
                        && !off.GlobalSaves().Any(x => x.PendingDelete),
                    "keep=0 releases no global save"
                );
            }
        }
        finally
        {
            GatewayRegistry.GlobalRetentionKeep = keep;
            Directory.Delete(directory, true);
        }
    }
}
