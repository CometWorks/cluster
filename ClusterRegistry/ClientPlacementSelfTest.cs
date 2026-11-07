namespace ClusterRegistry;

/// <summary>SE-1241: the operator view names the node a client's partition is on now. The Gateway cached the
/// route a join or respawn resolved and a handover never rewrote it, so after a rotation drained a node into
/// the others its players still counted on it, and the idle hot spare showed 8 routed players.</summary>
internal static partial class SelfTest
{
    private static void TestClientPlacement()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("client-placement: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "client-placement-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => DateTimeOffset.UtcNow);
            NodeRegistration spare = Register(registry, "node-3", "127.0.0.1:28443", Guid.NewGuid());
            NodeRegistration active = Register(registry, "node-2", "127.0.0.1:28442", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, spare.Node, spare.Epoch, 0);
            ClientRoute cached = registry.RouteClient(7, 1);
            Check(cached.Node == spare.Node, "the client starts on the node that later becomes the spare");
            Check(registry.CurrentPlacement(7, cached.Partition) == (1UL, spare.Node), "the placement names the owner");

            registry.Begin("drain", 1, spare.Node, spare.Epoch, owner.Generation, active.Node, active.Epoch);
            registry.Activate("drain");
            registry.Commit("drain");
            Check(cached.Node == spare.Node, "the cached route still names the old node, which is what the Gateway showed");
            Check(registry.CurrentPlacement(7, cached.Partition) == (1UL, active.Node), "after the handover the placement follows the partition");
            Check(registry.CurrentPlacement(8, 1) == (1UL, active.Node), "a client no node reported yet follows its routed partition");
            Check(registry.CurrentPlacement(9, null) == null, "a client with no route has no placement");
        }
        finally { Directory.Delete(directory, true); }
    }
}
