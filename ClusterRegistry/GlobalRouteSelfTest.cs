namespace ClusterRegistry;

/// <summary>SE-1091: a World Authority answer still reaches a node that drains (Closed); broadcasts stay with the Active
/// nodes. Earned red: with the old Active-only filter the draining node's answer was dropped and its command timed out.</summary>
internal static partial class SelfTest
{
    private static void TestGlobalRoute()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("global-route: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "global-route-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => DateTimeOffset.UtcNow);
            var wa = registry.RegisterNode(Join("wa-1", "127.0.0.1:28440", Guid.NewGuid()) with { Role = NodeRole.WA });
            var active = Register(registry, "node-1", "127.0.0.1:28441", Guid.NewGuid());
            var draining = Register(registry, "node-2", "127.0.0.1:28442", Guid.NewGuid());
            registry.CloseNode(draining.Node, draining.Epoch);
            NodeRegistration Row(string node) => registry.Nodes().Single(x => x.Node == node);
            Check(Row("node-2").State == NodeState.Closed, "the fixture's node drains");
            Check(DirectGateway.ReceivesGlobal(Row("node-2"), "node-2"), "an answer reaches its draining node");
            Check(DirectGateway.ReceivesGlobal(Row("node-1"), "node-1"), "an answer reaches its active node");
            Check(!DirectGateway.ReceivesGlobal(Row("node-1"), "node-2"), "an answer goes only to its target");
            Check(DirectGateway.ReceivesGlobal(Row("node-1"), null) && !DirectGateway.ReceivesGlobal(Row("node-2"), null),
                "a broadcast goes to the active nodes only");
            Check(!DirectGateway.ReceivesGlobal(Row(wa.Node), wa.Node) && !DirectGateway.ReceivesGlobal(Row(wa.Node), null),
                "nothing is routed back to the World Authority");
            Check(!DirectGateway.ReceivesGlobal(Row(active.Node) with { State = NodeState.Dead }, active.Node)
                && !DirectGateway.ReceivesGlobal(Row(active.Node) with { State = NodeState.Empty }, active.Node), "a gone node gets nothing");
        }
        finally { Directory.Delete(directory, true); }
    }
}
