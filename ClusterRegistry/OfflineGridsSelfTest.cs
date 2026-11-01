using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// SE-810: an offline partition's grids. The node's offer binds, the rows answer by owner and survive a registry reopen, stay
    /// out of the table the nodes poll, and a row without them is listed as missing until a node fills it in from the Final,
    /// which only counts for the save the row is at.
    /// </summary>
    private static void TestOfflineGrids()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("offline-grids: " + what); }
        var nodeJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // The node's offline offer (OfflineGridRows.Rows: anonymous objects)
        const string offer = "{\"Partition\":7,\"X\":0,\"Y\":0,\"Z\":0,\"Radius\":10,\"Members\":[144115188075855873,5],\"Grids\":["
            + "{\"id\":144115188075855873,\"n\":\"Base\",\"l\":true,\"s\":true,\"b\":12,\"x\":1.5,\"y\":2,\"z\":3,\"o\":[1000,2000],\"m\":2500.5,\"g\":5,\"f\":true,\"p\":false,\"t\":true},"
            + "{\"id\":5,\"n\":\"Ship\",\"l\":false,\"s\":false,\"b\":3,\"x\":4,\"y\":5,\"z\":6,\"o\":[]}]}";
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(offer, nodeJson)!;
        Check(request.Grids is [{ Id: 144115188075855873, Name: "Base", Large: true, Static: true, Blocks: 12, X: 1.5, Owners: [1000, 2000], MassKg: 2500.5, Group: 5, Functional: true, Powered: false, Station: true },
                { Id: 5, Large: false, Owners: [], MassKg: null, Powered: null }],
            "the offer's rows bind, the 64-bit entity id exactly");

        string directory = Path.Combine(Path.GetTempPath(), "offline-grids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long generation;
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
                registry.Heartbeat(one.Node, one.Epoch, []);
                generation = registry.ClaimPartition(7, one.Node, one.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(7, generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "b", 2, "one/7-1", "http://one/control");
                registry.TakeOffline(request with { Node = one.Node, Epoch = one.Epoch, Generation = generation, Sequence = 1 });
                long old = registry.ClaimPartition(8, one.Node, one.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(8, old, 1), one.Node, one.Epoch, SaveKind.Final, 1, "b", 2, "one/8-1", "http://one/control");
                registry.TakeOffline(request with { Partition = 8, Node = one.Node, Epoch = one.Epoch, Generation = old, Sequence = 1, Grids = null });

                OfflineGridsAnswer mine = registry.OfflineGrids(2000);
                Check(mine is { Unknown: 1, Grids: [{ Partition: 7, Grid.Name: "Base" }] }, "one owner's grid, and the row not known yet counted");
                Check(registry.OfflineGrids(null).Grids.Length == 2 && registry.OfflineGrids(3000).Grids.Length == 0, "all grids, and an owner of none");
                Check(!JsonSerializer.Serialize(registry.OfflinePartitions(0)).Contains("Grids"), "the table the nodes poll has no grids");

                Check(registry.OfflineGridsMissing(0, 5) is [{ Partition: 8 }], "the row without grids is missing");
                Check(registry.OfflineGridsMissing(8, 5) is [{ Partition: 8 }], "the walk comes round");
                OfflineGrid read = new(9, "Old", true, false, 1, 0, 0, 0, [2000]);
                Check(!registry.SetOfflineGrids(new OfflineGridsRequest(8, old, 2, [read])), "another save of the row doesn't count");
                Check(registry.SetOfflineGrids(new OfflineGridsRequest(8, old, 1, [read])), "the row's Final fills it in");
                Check(!registry.SetOfflineGrids(new OfflineGridsRequest(8, old, 1, [])), "the first answer stays");
                Check(registry.OfflineGridsMissing(0, 5).Length == 0 && registry.OfflineGrids(2000) is { Unknown: 0, Grids.Length: 2 }, "nothing missing now");
            }
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
                Check(registry.OfflineGrids(2000) is { Unknown: 0, Grids.Length: 2 }, "the rows survive a registry reopen");
        }
        finally { Directory.Delete(directory, true); }

        Check(!JsonSerializer.Serialize(new OfflineGrid(1, "x", true, false, 1, 0, 0, 0, [])).Contains("\"p\""), "an unknown fact stays out of the row");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new OfflineGrid(144115188075855873, "Large Grid 1234", true, false, 500, 1234567.5, -2345678.5, 3456789.5, [144115188075855873], 25000, 144115188075855873, true, true, false));
        Console.WriteLine($"  offline-grids: {bytes.Length} bytes per grid row in the registry state");
    }
}
