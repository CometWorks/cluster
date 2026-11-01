using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClusterRegistry;

/// <summary>T-0344 (GatewayRegistry.ReclaimNetworkIdBlocksAtColdStart): NetworkId blocks are freed only by a registry
/// start that finds no node Active or Closed. Earned red: before the fix the block after a clean stop and start began
/// above every block ever issued, and the space ended after 65,535 of them.</summary>
internal static partial class SelfTest
{
    private static void TestNetworkIdColdStartReclaim()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("network-id-cold-start-reclaim: " + what); }
        static long Figure(GatewayRegistry registry, string name) =>
            JsonDocument.Parse(JsonSerializer.Serialize(registry.Status())).RootElement.GetProperty(name).GetInt64();
        static void Empty(GatewayRegistry registry, NodeRegistration node)
        {
            registry.CloseNode(node.Node, node.Epoch);
            registry.MarkEmpty(node.Node, node.Epoch);
        }
        DateTimeOffset now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);
        TimeSpan lease = TimeSpan.FromSeconds(10);
        GatewayRegistry Open(string directory) => new(directory, Admission, () => now, lease);
        string Fresh() => Directory.CreateTempSubdirectory("network-id-reclaim-").FullName;

        // 1. A clean stop (every node Empty) and a start: the blocks are free and the space starts over.
        string directory = Fresh();
        try
        {
            NodeRegistration one, two;
            using (GatewayRegistry registry = Open(directory))
            {
                one = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
                two = Register(registry, "n2", "127.0.0.1:27018", Guid.NewGuid());
                Check(registry.AllocateNetworkIdBlock(one.Node, one.Epoch).Start == 1, "the first block starts at 1");
                Check(registry.AllocateNetworkIdBlock(two.Node, two.Epoch).Start == 65_537, "the second follows it");
                Check(Figure(registry, "networkIdBlocksFree") == 65_533 && Figure(registry, "networkIdBlocksReclaimed") == 0, "status counts what is left");
                Empty(registry, one);
                // 5. All but one Empty and no restart: nothing is freed while this process runs, whatever registers.
                NodeRegistration again = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
                Check(registry.AllocateNetworkIdBlock(again.Node, again.Epoch).Start == 131_073, "a re-registration in a running cluster continues above every block");
                Empty(registry, again);
                Empty(registry, two);
                NodeRegistration late = Register(registry, "n3", "127.0.0.1:27020", Guid.NewGuid());
                Check(registry.AllocateNetworkIdBlock(late.Node, late.Epoch).Start == 196_609 && registry.NetworkIdBlocks().Length == 4,
                    "every node Empty in a RUNNING registry frees nothing: the captures this process carries are not provably gone");
                // #1302: a Closed (draining) node still draws ids - its game loop builds antenna proxies and finishes
                // recoveries until it is Empty. Earned red: before the fix both threw "epoch N is Closed".
                registry.CloseNode(late.Node, late.Epoch);
                Check(registry.AllocateNetworkIdBlock(late.Node, late.Epoch).Start == 262_145 && registry.NetworkIdBlocks().Length == 5,
                    "a Closed node draws a network-id block");
                Check(registry.AllocateEntityIdRange(late.Node, late.Epoch).Node == late.Node, "a Closed node draws an entity-id range");
                registry.MarkEmpty(late.Node, late.Epoch);
                AssertConflict(() => registry.AllocateEntityIdRange(late.Node, late.Epoch));
                // The premises: an Empty incarnation draws no block, owns nothing and sources no handover.
                AssertConflict(() => registry.AllocateNetworkIdBlock(late.Node, late.Epoch));
                AssertConflict(() => registry.ClaimPartition(5, late.Node, late.Epoch, 0));
                AssertConflict(() => registry.AllocateNetworkIdBlock(one.Node, one.Epoch));
            }
            using (GatewayRegistry restarted = Open(directory))
            {
                Check(restarted.NetworkIdBlocks().Length == 0, "a start with every node Empty frees every block");
                Check(Figure(restarted, "networkIdBlocksReclaimed") == 5 && Figure(restarted, "networkIdBlocksFree") == 65_535, "status counts the reclaim");
                NodeRegistration next = Register(restarted, "n1", "127.0.0.1:27016", Guid.NewGuid());
                Check(next.Epoch == 3, "the node's epochs go on");
                NetworkIdBlock block = restarted.AllocateNetworkIdBlock(next.Node, next.Epoch);
                Check(block.Start == 1 && block.End == 65_536, "the first block after a cold start is the first of the space again");
            }
            // 2. Falsifier, Active: the registry restarts under a running node. Its blocks, and everything issued
            // before it that it may have received by handover, stay exactly as they were.
            using (GatewayRegistry underLiveNode = Open(directory))
            {
                NetworkIdBlock[] kept = underLiveNode.NetworkIdBlocks();
                Check(kept.Length == 1 && kept[0].Start == 1 && kept[0].Node == "n1" && kept[0].Epoch == 3, "an Active node keeps the list");
                Check(Figure(underLiveNode, "networkIdBlocksReclaimed") == 5, "and nothing more is counted");
                NodeRegistration other = Register(underLiveNode, "n2", "127.0.0.1:27018", Guid.NewGuid());
                Check(underLiveNode.AllocateNetworkIdBlock(other.Node, other.Epoch).Start == 65_537, "the next block is above it");
                // 3. Falsifier, Closed: n2 Empty, n1 draining (Closed) - a draining node still hands its ids over.
                Empty(underLiveNode, other);
                underLiveNode.CloseNode("n1", 3);
            }
            using (GatewayRegistry underClosedNode = Open(directory))
            {
                Check(underClosedNode.NetworkIdBlocks().Select(x => x.Start).SequenceEqual([1u, 65_537u]),
                    "one Closed node keeps every block, the Empty node's too (its ids may have moved to the Closed one)");
                // 4. Dead is final as Empty is: the lease runs out, the incarnation is fenced, the next start frees.
                                underClosedNode.Heartbeat("n1", 3, []);
                now += lease + lease + TimeSpan.FromSeconds(1);
                underClosedNode.ExpireLeases();
                AssertConflict(() => underClosedNode.Heartbeat("n1", 3, []));
                AssertConflict(() => underClosedNode.AllocateNetworkIdBlock("n1", 3));
                NodeRegistration target = Register(underClosedNode, "n4", "127.0.0.1:27022", Guid.NewGuid());
                AssertConflict(() => underClosedNode.Begin("dead-source", 9, "n1", 3, 0, target.Node, target.Epoch));
                Check(underClosedNode.NetworkIdBlocks().Length == 2, "a death in a running registry frees nothing");
                Empty(underClosedNode, target);
            }
            // An older database: the counter's row does not exist. It loads as zero and the blocks are untouched
            // until the reclaim itself.
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, RegistryStore.FileName), Pooling = false }.ToString()))
            {
                db.Open();
                using SqliteCommand command = db.CreateCommand();
                command.CommandText = "DELETE FROM \"state\" WHERE k = 'NetworkIdBlocksReclaimed'";
                Check(command.ExecuteNonQuery() == 1, "the fixture removes the counter row");
            }
            using (GatewayRegistry afterDeath = Open(directory))
            {
                Check(afterDeath.NetworkIdBlocks().Length == 0 && Figure(afterDeath, "networkIdBlocksReclaimed") == 2,
                    "Dead and Empty nodes only: freed, and a state without the counter starts it at zero");
            }
        }
        finally { Directory.Delete(directory, true); }

        // 6. A crash right after a reclaim and a reissue: the reclaim and the new block are both on disk, the
        // restart sees the Active node, frees nothing and issues above the block (T-0306 fixture).
        directory = Fresh();
        try
        {
            using (var seeded = new GatewayRegistry(directory, Admission))
            {
                NodeRegistration old = Register(seeded, "old", "127.0.0.1:27016", Guid.NewGuid());
                for (int i = 0; i < 3; i++) seeded.AllocateNetworkIdBlock(old.Node, old.Epoch);
                Empty(seeded, old);
            }
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(SelfTest).Assembly.Location);
            start.ArgumentList.Add("--self-test-abandon-allocation");
            start.ArgumentList.Add(directory);
            start.Environment["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "60";
            using var child = System.Diagnostics.Process.Start(start)!;
            if (!child.WaitForExit(10000))
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("network-id-cold-start-reclaim: crash fixture child");
            }
            Check(child.ExitCode == 0, "the crash fixture child ran: " + child.StandardError.ReadToEnd());
            using JsonDocument issued = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "issued.json")));
            Check(issued.RootElement.GetProperty("Block").GetProperty("Start").GetUInt32() == 1, "the crashed process reclaimed and reissued block 1");
            using var recovered = new GatewayRegistry(directory, Admission);
            Check(recovered.NetworkIdBlocks().Select(x => x.Start).SequenceEqual([1u]), "after the crash: exactly the reissued block, not the three old ones");
            NodeRegistration next = Register(recovered, "after-crash", "127.0.0.1:27018", Guid.NewGuid());
            Check(recovered.AllocateNetworkIdBlock(next.Node, next.Epoch).Start == 65_537, "and the next block does not overlap it");
        }
        finally { Directory.Delete(directory, true); }

        // The warning: under a tenth of the space left. A world that ran on 2^20 blocks counts from its highest End.
        NetworkIdBlock Block(uint start, uint end) => new(start, end, "n", 1, DateTimeOffset.UnixEpoch);
        Check(GatewayRegistry.NetworkIdBlocksFree([]) == 65_535, "an empty space holds 65,535 blocks (id 0 is invalid, so the last 64k is one short)");
        Check(GatewayRegistry.NetworkIdBlocksFree([Block(1, 1_048_576), Block(1_048_577, 1_114_112)]) == 65_535 - 17, "a legacy 2^20 block costs sixteen");
        Check(GatewayRegistry.NetworkIdBlocksFree([Block(4_294_901_761, uint.MaxValue)]) == 0, "nothing is left above the last id");
        Check(GatewayRegistry.NetworkIdBlocksLow == 6_553, "the warning threshold is a tenth of the space");
        Console.WriteLine("network-id-cold-start-reclaim: a registry start with no node Active or Closed frees every NetworkId block;"
            + " one Active or Closed node, or a running registry, frees none; a crash after the reissue leaves no overlap");
    }
}
