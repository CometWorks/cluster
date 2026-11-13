namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// SE-1443: a fresh registry hands out partition ids from 1 and learns a seed partition's id only from its node's
    /// startup claim, so an id can sit in n1's reserve when n2 claims its seed partition under it (claim-created-row).
    /// The seed claim stands (its content is real), and n1's later split into that id is refused as a
    /// PartitionIdConflictException - the type the node reads as "this id, not this node" - with nothing changed: the
    /// input stays n1's at its generation, and the same split goes through with a fresh id. Cluster 0.3.11 answered a
    /// plain RegistryConflictException, which the node treats as terminal.
    /// </summary>
    private static void TestPartitionIdClaimedElsewhere()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("partition-id-claimed-elsewhere: " + what); }
        string directory = Directory.CreateTempSubdirectory("gw-selftest-id-claimed-").FullName;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration n1 = Register(registry, "n1", "one", Guid.NewGuid());
            NodeRegistration n2 = Register(registry, "n2", "two", Guid.NewGuid());
            ulong[] reserve = registry.AllocatePartitionIds(n1.Node, n1.Epoch, 3);
            Check(reserve.SequenceEqual([1UL, 2UL, 3UL]), "a fresh registry starts its ids at 1: " + string.Join(",", reserve));
            ulong source = reserve[0], seed = reserve[1];
            long generation = registry.ClaimPartition(source, n1.Node, n1.Epoch, 0).Generation;
            // n2 boots with seed partition 2 (generation 1) and claims it; the registry has no row for it yet.
            Check(registry.ClaimPartition(seed, n2.Node, n2.Epoch, 0).Node == n2.Node, "the seed claim must stand");
            Exception? refused = null;
            try
            {
                registry.ApplyLineage("split-into-seed", LineageKind.Split, n1.Node, n1.Epoch, [new(source, generation)], [source, seed],
                    new() { [source] = [1], [seed] = [2] }, 100);
            }
            catch (Exception e) { refused = e; }
            Check(refused is PartitionIdConflictException, "a split into another node's partition id must be refused as an id conflict, was: "
                + (refused == null ? "applied" : refused.GetType().Name + ": " + refused.Message));
            PartitionOwnership kept = registry.GetPartitionOwner(source);
            Check(kept.Node == n1.Node && kept.Generation == generation && registry.GetPartitionOwner(seed).Node == n2.Node
                && registry.Lineage().Length == 0, "a refused split must change nothing");
            // An id never allocated is the same refusal.
            Check(Throws(() => registry.ApplyLineage("never-allocated", LineageKind.Split, n1.Node, n1.Epoch, [new(source, generation)], [source, 999],
                new() { [source] = [1], [999] = [2] }, 100)) is PartitionIdConflictException, "an unallocated output id must be an id conflict");
            // The node makes the split again with its next id: applied.
            LineageRecord again = registry.ApplyLineage("split-fresh-id", LineageKind.Split, n1.Node, n1.Epoch, [new(source, generation)], [source, reserve[2]],
                new() { [source] = [1], [reserve[2]] = [2] }, 100);
            Check(again.Outputs.Select(x => x.Partition).SequenceEqual([source, reserve[2]]) && registry.GetPartitionOwner(reserve[2]).Node == n1.Node,
                "the split with a fresh id must apply");
            // A plain conflict of a topology apply (a stale input generation) keeps its type: that one stays terminal.
            Check(Throws(() => registry.ApplyLineage("stale", LineageKind.Split, n1.Node, n1.Epoch, [new(source, generation)], [source],
                new() { [source] = [1] }, 200)) is RegistryConflictException and not PartitionIdConflictException, "a fenced input must stay a plain conflict");
            Console.WriteLine("partition-id-claimed-elsewhere: OK");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static Exception? Throws(Action action)
        {
            try { action(); return null; }
            catch (Exception e) { return e; }
        }
    }
}
