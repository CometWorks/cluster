using System.Diagnostics;
using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// #1354: the NPC census. The node's report rows and offline offer bind (the exact JSON the node writes), the merge
    /// counts each grid once (online over offline, the smaller unvisited count of a grid two nodes report), an offline
    /// row keeps its NPC rows across a registry reopen, and the overhead of a census at the octahedron's scale
    /// (~900 NPC grids, #1306) is printed: report bytes per grid, census bytes, merge + serialise time and allocation.
    /// </summary>
    private static void TestNpcCensus()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("npc-census: " + what); }
        var nodeJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // The node's ReportJson row format (PartitionManager + NpcCensusService.RowsJson).
        const string report = "{\"syncDistance\":3000,\"margin\":1000,\"evaluatedAt\":1,\"partitions\":["
            + "{\"id\":7,\"memberCount\":2,\"anchored\":false,\"sphere\":{\"x\":0,\"y\":0,\"z\":0,\"radius\":10},\"maxMemberSpeed\":0,\"clients\":[],"
            + "\"npc\":[{\"id\":144115188075855873,\"x\":100,\"y\":-200,\"z\":300,\"u\":40},{\"id\":5,\"x\":1,\"y\":2,\"z\":3,\"u\":4,\"k\":\"unowned\"}]},"
            + "{\"id\":8,\"memberCount\":1,\"anchored\":false,\"sphere\":{\"x\":0,\"y\":0,\"z\":0,\"radius\":10},\"maxMemberSpeed\":0,\"clients\":[],\"npc\":[]},"
            + "{\"id\":9,\"memberCount\":1,\"anchored\":false,\"sphere\":{\"x\":0,\"y\":0,\"z\":0,\"radius\":10},\"maxMemberSpeed\":0,\"clients\":[]}]}";
        PartitionReport parsed = JsonSerializer.Deserialize<PartitionReport>(report)!;
        Check(parsed.Partitions[0].Npc is [{ Id: 144115188075855873, X: 100, Y: -200, Z: 300, UnvisitedSeconds: 40, Kind: null },
                { Id: 5, Kind: "unowned", Offline: false }],
            "a report's npc row binds, the 64-bit entity id exactly, and #1379's kind");
        Check(parsed.Partitions[1].Npc is [] && parsed.Partitions[2].Npc == null, "an empty npc list and a node that predates the field");

        static PartitionReport Report(params (ulong Partition, NpcGrid[] Npc)[] partitions) => new(3000, 1000,
            [.. partitions.Select(x => new PartitionInfo(x.Partition, x.Npc.Length, false, new PartitionSphere(0, 0, 0, 10), 0, [], Npc: x.Npc))], 0);
        NpcGrid a = new(1, 0, 0, 0, 50), b = new(2, 10, 0, 0, 0), handedOver = new(1, 0, 0, 0, 20), parked = new(3, 0, 5000, 0, 900);
        NpcGrid drop = new(4, 0, 0, 9000, 30, "unowned"), parkedDrop = new(5, 0, 0, -9000, 60, "unowned");
        NpcGrid[] merged = DirectGateway.MergeNpcCensus([Report((7, [a, b, drop])), Report((8, [handedOver]))],
            [new OfflinePartition { Partition = 9, Npc = [parked, new NpcGrid(2, 99, 99, 99, 999), parkedDrop] }], out int online, out int offlineCount);
        Check(online == 2 && offlineCount == 1 && merged.Length == 5,
            $"five rows, two NPC grids online and one offline ({online} online, {offlineCount} offline, {merged.Length} rows)");
        Check(merged.Single(x => x.Id == 1).UnvisitedSeconds == 20, "a grid two nodes report keeps the smaller unvisited count");
        Check(merged.Single(x => x.Id == 2) == b, "a grid online and in an offline row counts once, as the online row");
        Check(merged.Single(x => x.Id == 3) == parked with { Offline = true }, "an offline partition's NPC grid is counted, marked offline");
        Check(merged.Single(x => x.Id == 4) == drop && merged.Single(x => x.Id == 5) == parkedDrop with { Offline = true },
            "#1379: the unowned rows are kept, online and offline, but not counted as NPC grids");
        string wire = JsonSerializer.Serialize(new NpcCensus(0, online, offlineCount, merged));
        Check(wire.Contains("{\"id\":1,\"x\":0,\"y\":0,\"z\":0,\"u\":20}") && wire.Contains("\"k\":\"unowned\",\"o\":true"),
            "a row writes k only when known and o only when offline: " + wire);

        // #1443: a drop row's d and e bind from a report and an offline offer, survive the merge (offline marked) and are written back.
        PartitionReport dropReport = JsonSerializer.Deserialize<PartitionReport>(report.Replace("\"k\":\"unowned\"}", "\"k\":\"unowned\",\"d\":true,\"e\":1761200000}"))!;
        Check(dropReport.Partitions[0].Npc![1] is { Id: 5, Drop: true, Expires: 1761200000 } && dropReport.Partitions[0].Npc![0] is { Drop: false, Expires: 0 },
            "#1443: a report's drop row binds d and e, a plain row has neither");
        NpcGrid liveDrop = new(10, 0, 0, 0, 0, "unowned", Drop: true, Expires: 1761200000), frozenDrop = new(11, 0, 0, 0, 0, "unowned", Drop: true);
        NpcGrid[] drops = DirectGateway.MergeNpcCensus([Report((7, [liveDrop]))], [new OfflinePartition { Partition = 9, Npc = [frozenDrop] }], out _, out _);
        Check(drops.Single(x => x.Id == 10) == liveDrop && drops.Single(x => x.Id == 11) == frozenDrop with { Offline = true },
            "#1443: drop rows keep d and e through the merge, the offline one marked offline");
        string dropWire = JsonSerializer.Serialize(new NpcCensus(0, 0, 0, drops));
        Check(dropWire.Contains("\"k\":\"unowned\",\"d\":true,\"e\":1761200000}") && dropWire.Contains("\"o\":true,\"d\":true}"),
            "#1443: a drop row writes d, and e only when known: " + dropWire);

        // The node's offline offer (PartitionPersistence.Offline: anonymous objects, default System.Text.Json naming).
        string offer = JsonSerializer.Serialize(new
        {
            Partition = 7UL, Node = "one", Epoch = 1L, Generation = 1L, Sequence = 2L, X = 0.0, Y = 0.0, Z = 0.0, Radius = 50.0,
            MemberSpheres = new[] { new[] { 0.0, 0, 0, 50 } }, Members = new[] { 144115188075855873L }, EstimatedSnapshotBytes = 1000L,
            Antennas = "[]", RespawnPoints = "[]",
            Npc = new object[] { new { id = 144115188075855873L, x = 1.0, y = 2.0, z = 3.0, u = 600.0, k = "npc" } },
        });
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(offer, nodeJson)!;
        Check(request.Npc is [{ Id: 144115188075855873, X: 1, Y: 2, Z: 3, UnvisitedSeconds: 600, Kind: "npc" }], "the offline offer's npc rows bind");
        OfflineRequest dropOffer = JsonSerializer.Deserialize<OfflineRequest>(offer.Replace("\"k\":\"npc\"}", "\"k\":\"unowned\",\"d\":true,\"e\":1761200000}"), nodeJson)!;
        Check(dropOffer.Npc is [{ Kind: "unowned", Drop: true, Expires: 1761200000 }], "#1443: the offline offer's drop row binds d and e");

        // An offline row keeps its NPC rows, also across a registry reopen (the census counts them while offline).
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
                registry.Heartbeat(one.Node, one.Epoch, []);
                long generation = registry.ClaimPartition(7, one.Node, one.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(7, generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "b", 2, "one/7-1", "http://one/control");
                registry.TakeOffline(request with { Node = one.Node, Epoch = one.Epoch, Generation = generation, Sequence = 1 });
                Check(registry.OfflinePartitions(0).Partitions.Single().Npc is [{ Id: 144115188075855873 }], "the offline row holds the NPC rows");
            }
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
                Check(registry.OfflinePartitions(0).Partitions.Single().Npc is [{ Id: 144115188075855873, UnvisitedSeconds: 600 }],
                    "the NPC rows survive a registry reopen");
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }

        // Overhead at the octahedron's scale: 900 NPC grids online over 6 nodes x 60 partitions, 300 more offline.
        var random = new Random(1354);
        NpcGrid Grid(long id) => new(0x0100_0000_0000_0000 + id, Math.Round(random.NextDouble() * 4e6 - 2e6),
            Math.Round(random.NextDouble() * 4e6 - 2e6), Math.Round(random.NextDouble() * 4e6 - 2e6), Math.Round(random.NextDouble() * 3600));
        long next = 0;
        PartitionReport[] reports = [.. Enumerable.Range(0, 6).Select(_ => Report([.. Enumerable.Range(0, 60).Select(p =>
            ((ulong)p, Enumerable.Range(0, 900 / 360 + (p % 2)).Select(__ => Grid(next++)).ToArray()))]))];
        OfflinePartition[] offline = [.. Enumerable.Range(0, 100).Select(p => new OfflinePartition
            { Partition = 1000 + (ulong)p, Npc = [Grid(next++), Grid(next++), Grid(next++)] })];
        int reported = reports.Sum(x => x.Partitions.Sum(p => p.Npc!.Length));
        // One row as the node writes it (RowsJson: whole metres and seconds).
        int rowBytes = "{\"id\":72057594037928000,\"x\":-1234567,\"y\":1234567,\"z\":-1234567,\"u\":1800},".Length;
        string census = "";
        var times = new List<double>();
        long allocated = 0;
        for (int i = 0; i < 21; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            NpcGrid[] grids = DirectGateway.MergeNpcCensus(reports, offline, out int live, out int parkedNpc);
            census = JsonSerializer.Serialize(new NpcCensus(1.5, live, parkedNpc, grids));
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        times.Sort();
        Check(census.Contains("\"online\":" + reported) && census.Contains("\"offline\":300"), "the overhead census counts every grid");
        Console.WriteLine($"npc-census overhead: {reported} online + 300 offline NPC grids; report +{rowBytes} B per NPC grid "
            + $"(+{rowBytes * reported / 6 / 1024.0:F1} KiB per node report); census answer {census.Length / 1024.0:F1} KiB; "
            + $"merge+serialise median {times[times.Count / 2]:F2} ms (max {times[^1]:F2} ms), {allocated / 1024.0:F0} KiB allocated per rebuild; "
            + $"offline rows +{3 * rowBytes} B each in the registry store");
    }
}
