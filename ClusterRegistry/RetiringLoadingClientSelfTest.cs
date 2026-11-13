using System.Net;

namespace ClusterRegistry;

/// <summary>
/// SE-1448 (octahedron-test 2026-11-13, cluster 0.3.11): AetherAdept's first join was routed to local-node-3 a minute
/// before rotation recycled it. A first join is in no partition until it spawns, so the scale-in drain read node-3 as
/// owning nothing, closed it and the slot restarted under the load: node-link-lost.
/// </summary>
internal static partial class SelfTest
{
    private static void TestRetiringLoadingClient()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("retiring-loading-client: " + what);
        }
        IPEndPoint one = new(IPAddress.Loopback, 27101),
            three = new(IPAddress.Loopback, 27103);
        (string, IPEndPoint)[] retiring = [("node-3", three)];
        const ulong aether = 76561197997618232,
            settled = 76561202000000002,
            elsewhere = 76561202000000004;

        Dictionary<string, ulong[]> loading = DirectGateway.LoadingClientsByNode(
            [(aether, false, three), (settled, true, three), (elsewhere, false, one)],
            retiring
        );
        Check(
            loading.Count == 1 && loading["node-3"].SequenceEqual([aether]),
            "a client still loading on the retiring node holds it; a ready one and one loading elsewhere do not"
        );
        Check(
            DirectGateway.LoadingClientsByNode([(aether, true, three)], retiring).Count == 0,
            "once world-ready it holds nothing"
        );

        string directory = Directory.CreateTempSubdirectory("retiring-loading-selftest-").FullName;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);
            foreach (string node in new[] { "node-1", "node-2", "node-3" })
            {
                NodeRegistration row = Register(
                    registry,
                    node,
                    "127.0.0.1:2710" + node[^1],
                    Guid.NewGuid()
                );
                registry.Heartbeat(row.Node, row.Epoch, []);
            }
            registry.BeginRetiring(["node-3"], "rotation");
            // ScaleInPass: nodes whose report lists partitions, plus those a client is still loading on.
            HashSet<string> owning = [];
            owning.UnionWith(
                DirectGateway.LoadingClientsByNode([(aether, false, three)], retiring).Keys
            );
            Check(
                registry.SettleRetiring(owning).Length == 0,
                "a retiring node a client is loading on is not closed although its report owns nothing"
            );
            owning =
            [
                .. DirectGateway.LoadingClientsByNode([(aether, true, three)], retiring).Keys,
            ];
            Check(
                registry.SettleRetiring(owning).SequenceEqual(["node-3"]),
                "it closes once the client is world-ready"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
