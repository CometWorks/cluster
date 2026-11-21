using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestLobbyAnchor()
    {
        string dir = Directory.CreateTempSubdirectory("lobby-anchor-").FullName;
        static AdminOperationRecord Begin(GatewayRegistry r, string key) =>
            r.BeginAdminOperation("lobby-set", key, "admin", key, null, out _);
        try
        {
            string operation;
            long grid;
            using (var r = new GatewayRegistry(dir, Admission))
            {
                var node = r.RegisterNode(
                    Join("regular", "ep", Guid.NewGuid()) with
                    {
                        ControlEndpoint = "http://localhost:1",
                    }
                );
                r.Heartbeat(node.Node, node.Epoch, []);
                var desired = new Admin.LobbyUpdate(
                    0,
                    BlueprintXml: "<Definitions/>",
                    Position: new(1e7, 0, 0)
                );
                Assert(desired.ValidationError() == null);
                Assert((desired with { GridId = 1 }).ValidationError() != null);
                Assert(
                    (desired with { Position = new(double.NaN, 0, 0) }).ValidationError() != null
                );
                Assert(
                    (
                        desired with
                        {
                            BlueprintXml = new string('x', Admin.LobbyUpdate.MaxBlueprintBytes + 1),
                        }
                    ).ValidationError() != null
                );
                Assert(r.LobbyStatus().State == "unconfigured");
                var create = Begin(r, "create");
                r.ReserveLobby(create, desired);
                var first = r.PendingLobby!;
                grid = first.Grid;
                operation = first.Operation;
                Assert(grid > 0 && operation == create.OperationId);
                var concurrent = Begin(r, "concurrent");
                r.ReserveLobby(concurrent, desired);
                Assert(concurrent.ErrorCode == "lobby_busy");
                r.MarkLobbyDispatched(first);
                r.ReserveLobby(Begin(r, "create"), desired);
                Assert(r.PendingLobby is { Dispatched: true } && r.PendingLobby.Grid == grid);
            }
            using (var r = new GatewayRegistry(dir, Admission))
            {
                var intent = r.PendingLobby!;
                Assert(intent.Operation == operation && intent.Grid == grid && intent.Dispatched);
                var node = r.Nodes().Single();
                r.Heartbeat(node.Node, node.Epoch, []);
                var owner = r.ClaimPartition(21, node.Node, node.Epoch, 0);
                var result = new LobbyNodeReply(
                    "ready",
                    grid,
                    21,
                    owner.Generation,
                    "Faction Hall",
                    1e7,
                    0,
                    0,
                    null
                );
                Assert(
                    !r.AcceptLobby(intent, node, result with { Generation = owner.Generation + 1 })
                );
                Assert(!r.AcceptLobby(intent, node, result with { GridEntityId = grid + 1 }));
                Assert(r.AcceptLobby(intent, node, result));
                Assert(
                    r.AdminOperationView(operation)!.State == Admin.AdminOperationState.Succeeded
                );
                var stale = Begin(r, "stale");
                r.ReserveLobby(stale, new(0, grid, 21));
                Assert(stale.ErrorCode == "revision_mismatch" && r.PendingLobby == null);
                ulong piece = r.AllocatePartitionIds(node.Node, node.Epoch, 1).Single();
                r.ApplyLineage(
                    "split-with-lobby",
                    LineageKind.Split,
                    node.Node,
                    node.Epoch,
                    [new(21, owner.Generation)],
                    [21, piece],
                    new() { [21] = [42], [piece] = [grid] },
                    100
                );
                Assert(r.LobbyStatus().Anchor!.Partition == piece);
                var replace = Begin(r, "replace");
                r.ReserveLobby(replace, new(1, null, 21));
                r.RejectLobby(r.PendingLobby!, "invalid_station", "Ambiguous station");
                Assert(r.LobbyStatus().Anchor?.GridId == grid && r.LobbyStatus().Revision == 1);
                var holder = r.GetPartitionOwner(piece);
                r.RegisterSave(
                    new SaveId(piece, holder.Generation, 1),
                    node.Node,
                    node.Epoch,
                    SaveKind.Final,
                    1,
                    "a",
                    1,
                    "lobby/1",
                    "http://localhost:1"
                );
                r.TakeOffline(
                    new OfflineRequest(
                        piece,
                        node.Node,
                        node.Epoch,
                        holder.Generation,
                        1,
                        1e7,
                        0,
                        0,
                        10,
                        [
                            [1e7, 0, 0, 10],
                        ],
                        [grid],
                        100,
                        "[]",
                        "[]"
                    )
                );
                Assert(
                    r.IsOffline(piece)
                        && r.LobbyStatus().Anchor?.GridId == grid
                        && r.LobbyStatus() is { Owner: null, State: "offline" }
                );
                Assert(r.OfflineRow(piece)?.LoadRequestedNode == null);
            }
            using var reopened = new GatewayRegistry(dir, Admission);
            Assert(
                reopened.LobbyStatus().Anchor?.Name == "Faction Hall"
                    && reopened.PendingLobby == null
            );
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
