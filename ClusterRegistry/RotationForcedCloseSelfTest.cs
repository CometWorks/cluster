namespace ClusterRegistry;

/// <summary>
/// SE-1553 (octahedron 2026-11-14, cluster 0.3.16): two rotation recycles were force-closed after 10 minutes with 27
/// and 32 partitions left, and the Steam clients on them were force-reconnected every 10 s, one for 27 minutes.
/// Each check here fails on 0.3.16.
/// </summary>
internal static partial class SelfTest
{
    private static void TestRotationForcedClose()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("rotation-forced-close: " + what);
        }

        // The drain. 1 is the busy client-bearing partition on the retiring node (source) and cannot be handed over
        // (HandoverBlocked); 2 is its neighbour the drain already carried to the node that replaces it (target).
        static PartitionInfo At(ulong id, double x, int pcu, params ulong[] clients) =>
            new(
                id,
                1,
                true,
                new PartitionSphere(x, 0, 0, 100),
                0,
                clients,
                pcu,
                400L * pcu + 10_000
            );
        static PartitionReport Report(params PartitionInfo[] partitions) =>
            new(3000, 1000, partitions, 0, 50);
        PartitionReport retiringNode = Report(At(1, 0, 2000, 76561198000000001)),
            replacement = Report(At(2, 3500, 50));
        static bool Blocked(ulong id) => id == 1;
        DirectGateway.Move? back = DirectGateway.ChooseMove(
            retiringNode,
            replacement,
            4000,
            [],
            _ => false,
            validationBlocked: Blocked
        );
        Check(
            back is { Moving.Id: 2, ToTarget: false },
            "control: co-location hands the drained neighbour back to the node holding the blocked partition: "
                + back
        );
        Check(
            DirectGateway.ChooseMove(
                retiringNode,
                replacement,
                4000,
                [],
                _ => false,
                validationBlocked: Blocked,
                sourceRetiring: true
            ) == null,
            "nothing is handed onto a retiring node; the blocked partition stays behind alone"
        );
        Check(
            DirectGateway.ChooseMove(
                replacement,
                retiringNode,
                4000,
                [],
                _ => false,
                validationBlocked: Blocked,
                targetRetiring: true
            ) == null,
            "the same with the retiring node as the pair's target"
        );
        // 3 is a second neighbour still on the retiring node and free to move: it leaves, although cost and the
        // block both price the retiring node as the home.
        DirectGateway.Move? leaves = DirectGateway.ChooseMove(
            Report(At(1, 0, 2000, 76561198000000001), At(3, -3500, 50)),
            replacement,
            4000,
            [],
            _ => false,
            validationBlocked: Blocked,
            sourceRetiring: true
        );
        Check(
            leaves is { Moving.Id: 3, ToTarget: true },
            "a free neighbour on the retiring node still leaves it: " + leaves
        );
        DirectGateway.Move? both = DirectGateway.ChooseMove(
            retiringNode,
            replacement,
            4000,
            [],
            _ => false,
            validationBlocked: Blocked,
            sourceRetiring: true,
            targetRetiring: true
        );
        Check(
            both is { Moving.Id: 2, ToTarget: false },
            "two retiring nodes co-locate as before: " + both
        );

        // The forced close. The closed node has saved and unloaded the partition; the client it is routed to is
        // connected (rejoining) and cannot be served there.
        Check(
            DirectGateway.ReleaseRoutePinApplies(pinStale: false, NodeState.Active),
            "a serving node's release is still refused while a client is routed there"
        );
        Check(
            !DirectGateway.ReleaseRoutePinApplies(pinStale: false, NodeState.Closed),
            "a Closed node's release is accepted"
        );
        Check(
            !DirectGateway.ReleaseRoutePinApplies(pinStale: true, NodeState.Active),
            "a stale pin refuses nothing"
        );
        Check(
            DirectGateway.ReleaseRoutePinApplies(pinStale: false, null),
            "an unknown node is refused as before"
        );

        // The reconnect loop. A vanilla client leaves only when the Disconnected message names its server.
        const ulong gameServer = 90297465325989918;
        Check(
            DirectGateway.ReconnectServerId("steam", gameServer) == gameServer,
            "a forced reconnect of a Steam client names the Steam GameServer, whichever frontend holds the session"
        );
        Check(
            DirectGateway.ReconnectServerId("steam", 0) == DirectTransportConstants.ServerId
                && DirectGateway.ReconnectServerId("dt", gameServer)
                    == DirectTransportConstants.ServerId,
            "the Direct Transport id before the GameServer is known and for Direct Transport clients"
        );
        TimeSpan grace = DirectGateway.SteamLoginGrace;
        Check(
            DirectGateway.SteamSessionWithoutLogin(
                "steam",
                loginPresented: false,
                admitted: false,
                ending: false,
                grace
            ),
            "a Steam session reopened by the traffic of an ended one is ended after the login grace"
        );
        Check(
            !DirectGateway.SteamSessionWithoutLogin(
                "steam",
                false,
                false,
                false,
                grace - TimeSpan.FromMilliseconds(1)
            )
                && !DirectGateway.SteamSessionWithoutLogin("steam", true, false, false, grace)
                && !DirectGateway.SteamSessionWithoutLogin("steam", false, true, false, grace)
                && !DirectGateway.SteamSessionWithoutLogin("steam", false, false, true, grace)
                && !DirectGateway.SteamSessionWithoutLogin("dt", false, false, false, grace),
            "not inside the grace, with a login, once admitted, while ending, or on Direct Transport"
        );
    }
}
