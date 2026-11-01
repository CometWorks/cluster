namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// T-0339: a split child that merges into a neighbour in the pass that made it (the children of a split
    /// carry the parent's age). Neither the child nor the neighbour is saved in between, so the member that
    /// moved is covered only through the lineage: the neighbour's cover reaches the child as a merge input, cut
    /// at the merge's time, and the child's cover is its parent's save through the split. That walk needs the
    /// split to be strictly EARLIER than the merge; the node stamps its changes that way
    /// (PartitionMembership.NextChangeTime). The control stamps both with the same millisecond and must lose
    /// the member - if it did not, this section would not be testing the ordering.
    /// </summary>
    private static void TestLineageSamePass()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("lineage-same-pass: " + what); }
        static string Show(IEnumerable<SaveCover> cover) => string.Join(" + ", cover.Select(x =>
            $"{x.Save.SaveId.Partition}:{(x.EntityIds == null ? "*" : string.Join(",", x.EntityIds))}"));
        string Run(long mergeTime)
        {
            string directory = Directory.CreateTempSubdirectory("gw-selftest-same-pass-").FullName;
            try
            {
                using var registry = new GatewayRegistry(directory, Admission);
                NodeRegistration node = Register(registry, "n1", "one", Guid.NewGuid());
                PartitionOwnership parent = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
                PartitionOwnership neighbour = registry.ClaimPartition(2, node.Node, node.Epoch, 0);
                registry.RegisterSave(new SaveId(1, parent.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "parent", 100, "n1/parent");
                registry.RegisterSave(new SaveId(2, neighbour.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "neighbour", 110, "n1/neighbour");
                // The ship 901 leaves [900, 901] and is within D of the neighbour [950] at once.
                ulong[] children = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
                LineageRecord split = registry.ApplyLineage("split", LineageKind.Split, node.Node, node.Epoch, [new(1, parent.Generation)],
                    children, new() { [children[0]] = [900], [children[1]] = [901] }, 200);
                registry.ApplyLineage("merge", LineageKind.Merge, node.Node, node.Epoch,
                    [new(2, neighbour.Generation), split.Outputs[1]], [2], null, mergeTime);
                return Show(registry.ResolveCover(2)) + " | " + Show(registry.ResolveCover(children[0]));
            }
            finally { Directory.Delete(directory, true); }
        }
        string later = Run(201), tied = Run(200);
        Console.WriteLine($"lineage-same-pass: merge 1 ms after the split: {later}; at the same millisecond (control): {tied}");
        Check(later == "2:* + 1:901 | 1:900", "a merge one millisecond after the split must cover the member that moved from the parent's save: " + later);
        Check(tied == "2:* | 1:900", "control: at an equal time the walk does not see the split (the defect the node's strictly increasing change times avoid): " + tied);
    }
}
