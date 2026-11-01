using System.Buffers.Binary;
using PluginSdk.Clustering;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// A committed plugin write is durable before it is acknowledged (T-0227: one registry store
    /// transaction, which also carries any coalesced mutation pending at that moment - strictly more
    /// durable than the old per-write WAL delta). Idempotent replays commit nothing, and the per-second
    /// write budget still applies.
    /// </summary>
    private static void TestPluginPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugin-store-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        static string CopyStore(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, RegistryStore.FileName + "*"))
                if (!file.EndsWith("-shm", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            return to;
        }
        try
        {
            string source = Path.Combine(root, "source");
            using (var registry = new GatewayRegistry(source, Admission, () => now, leaseTtl: TimeSpan.FromHours(1)))
            {
                var node = Register(registry, "node", "127.0.0.1:28417", Guid.NewGuid());
                registry.Heartbeat(node.Node, node.Epoch, [], serving: true);
                PluginRequest Request(string plugin, string key) => new() { Node = node.Node, Incarnation = node.Epoch,
                    Plugin = plugin, Key = key, Action = "read" };
                for (int i = 0; i < 40; i++)
                {
                    now = now.AddSeconds(1);
                    var request = Request(i % 2 == 0 ? "one" : "two", "key" + i);
                    request.Expected = registry.PluginOperation(request).Record.Version;
                    request.Action = "cas"; request.OperationId = Guid.NewGuid(); request.SchemaVersion = 1;
                    request.Payload = new byte[32 * 1024]; request.Payload[0] = (byte)i;
                    long commits = registry.StoreCommits;
                    Assert(registry.PluginOperation(request).Code == PluginResultCode.Success);
                    Assert(registry.StoreCommits > commits);          // durable before acknowledged (the first also commits the baseline)
                }
                // A coalesced non-plugin mutation pending at a plugin write rides its commit.
                registry.ClaimPartition(42, node.Node, node.Epoch, 0);
                var last = Request("one", "last");
                last.Expected = registry.PluginOperation(last).Record.Version;
                last.Action = "cas"; last.OperationId = Guid.NewGuid(); last.SchemaVersion = 1; last.Payload = [42];
                Assert(registry.PluginOperation(last).Code == PluginResultCode.Success);
                long settled = registry.StoreCommits;
                for (int i = 0; i < 30; i++) // Idempotent results neither consume write budget nor commit.
                    Assert(registry.PluginOperation(last).Code == PluginResultCode.Success);
                Assert(registry.StoreCommits == settled);
                string crash = CopyStore(source, Path.Combine(root, "crash"));
                using (var recovered = new GatewayRegistry(crash, Admission, () => now))
                {
                    var next = Register(recovered, "next", "127.0.0.1:28418", Guid.NewGuid());
                    recovered.Heartbeat(next.Node, next.Epoch, [], serving: true);
                    var read = Request("one", "last"); read.Node = next.Node; read.Incarnation = next.Epoch;
                    Assert(recovered.PluginOperation(read).Record.Payload.SequenceEqual(new byte[] { 42 }));
                    Assert(recovered.Partitions().Any(p => p.Partition == 42));
                }
                registry.Checkpoint();
                string checkpoint = CopyStore(source, Path.Combine(root, "checkpoint"));
                using (var recovered = new GatewayRegistry(checkpoint, Admission, () => now))
                    Assert(recovered.Partitions().Any(p => p.Partition == 42));
                now = now.AddSeconds(1);
                for (int i = 0; i < 11; i++)
                {
                    var request = Request("limited", "key" + i);
                    request.Expected = registry.PluginOperation(request).Record.Version;
                    request.Action = "cas"; request.OperationId = Guid.NewGuid(); request.SchemaVersion = 1;
                    Assert(registry.PluginOperation(request).Code == (i < 10 ? PluginResultCode.Success : PluginResultCode.Capacity));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
