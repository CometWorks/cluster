namespace ClusterRegistry;

/// <summary>SE-819: a Gateway restarted from a store older than its writers. Earned red: before the change every save
/// is refused with "sequence is fenced; expected 2", and the shutdown never gets its Final.</summary>
internal static partial class SelfTest
{
    private static void TestSaveSequenceAfterRollback()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "cluster-gateway-save-sequence-self-test-" + Guid.NewGuid()
        );
        string crashed = Path.Combine(directory, "crashed");
        try
        {
            using var live = new GatewayRegistry(Path.Combine(directory, "live"), Admission);
            NodeRegistration authority = live.RegisterNode(
                Join("wa-1", "wa-one", Guid.NewGuid()) with
                {
                    Role = NodeRole.WA,
                }
            );
            NodeRegistration node = Register(live, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            PartitionOwnership owner = live.ClaimPartition(1, node.Node, node.Epoch, 0);
            long generation = authority.AuthorityGeneration;
            live.RegisterGlobalSave(
                new(generation, 1),
                authority.Node,
                authority.Epoch,
                SaveKind.Periodic,
                1,
                "g1",
                1,
                "wa/g1"
            );
            live.RegisterSave(
                new SaveId(1, owner.Generation, 1),
                node.Node,
                node.Epoch,
                SaveKind.Periodic,
                1,
                "s1",
                1,
                "n1/s1"
            );
            // The store as a crash leaves it: this is on disk, and the registrations of sequences 2 to 4 that the writers
            // got an answer for after it are not.
            Directory.CreateDirectory(crashed);
            live.CopyStore(Path.Combine(crashed, RegistryStore.FileName));

            using var restarted = new GatewayRegistry(crashed, Admission);
            restarted.Heartbeat(authority.Node, authority.Epoch, []);
            restarted.Heartbeat(node.Node, node.Epoch, [new(1, owner.Generation)]);

            // The writers go on from where they are.
            GlobalSaveRegistration global = restarted.RegisterGlobalSave(
                new(generation, 5),
                authority.Node,
                authority.Epoch,
                SaveKind.Periodic,
                5,
                "g5",
                5,
                "wa/g5"
            );
            SaveRegistration save = restarted.RegisterSave(
                new SaveId(1, owner.Generation, 5),
                node.Node,
                node.Epoch,
                SaveKind.Periodic,
                5,
                "s5",
                5,
                "n1/s5"
            );
            Assert(restarted.NewestGlobalSave()?.SaveId == global.SaveId);
            Assert(restarted.ResolveCover(1).Single().Save.SaveId == save.SaveId);

            // A retry of a lost answer stays idempotent, and the slot behind the newest save stays closed.
            Assert(
                restarted
                    .RegisterGlobalSave(
                        global.SaveId,
                        authority.Node,
                        authority.Epoch,
                        SaveKind.Periodic,
                        5,
                        "g5",
                        5,
                        "wa/g5"
                    )
                    .SaveId == global.SaveId
            );
            AssertConflict(() =>
                restarted.RegisterGlobalSave(
                    new(generation, 3),
                    authority.Node,
                    authority.Epoch,
                    SaveKind.Periodic,
                    3,
                    "g3",
                    3,
                    "wa/g3"
                )
            );
            AssertConflict(() =>
                restarted.RegisterSave(
                    new SaveId(1, owner.Generation, 3),
                    node.Node,
                    node.Epoch,
                    SaveKind.Periodic,
                    3,
                    "s3",
                    3,
                    "n1/s3"
                )
            );

            // A jump does not get past the fences in front of it.
            AssertConflict(() =>
                restarted.RegisterGlobalSave(
                    new(generation + 1, 9),
                    authority.Node,
                    authority.Epoch,
                    SaveKind.Periodic,
                    9,
                    "stale",
                    9,
                    "wa/stale"
                )
            );
            AssertConflict(() =>
                restarted.RegisterSave(
                    new SaveId(1, owner.Generation + 1, 9),
                    node.Node,
                    node.Epoch,
                    SaveKind.Periodic,
                    9,
                    "stale",
                    9,
                    "n1/stale"
                )
            );

            // What the ticket lost: the graceful shutdown gets its Finals and ends clean.
            restarted.BeginShutdown(false, TimeSpan.FromMinutes(1));
            restarted.RegisterSave(
                new SaveId(1, owner.Generation, 6),
                node.Node,
                node.Epoch,
                SaveKind.Final,
                6,
                "s6",
                6,
                "n1/s6"
            );
            restarted.ReleasePartition(1, node.Node, node.Epoch, owner.Generation);
            restarted.MarkEmpty(node.Node, node.Epoch);
            restarted.RegisterGlobalSave(
                new(generation, 6),
                authority.Node,
                authority.Epoch,
                SaveKind.Final,
                6,
                "g6",
                6,
                "wa/g6"
            );
            restarted.MarkEmpty(authority.Node, authority.Epoch);
            Assert(restarted.ShutdownReady());
            Assert(
                restarted.CompleteShutdown(false).LastShutdown
                    is { Dirty.Length: 0, GlobalRef.Sequence: 6 }
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
