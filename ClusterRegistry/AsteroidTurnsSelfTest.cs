using ClusterPlugin;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// SE-1998 (AsteroidTurnPolicy.cs, AsteroidTurns.cs): the turn's geometry, what the Gateway commits and refuses, the
    /// pins it takes, that a recovery claim of a pinned save carries the turn, and that the rows survive a reopen.
    /// </summary>
    private static void TestAsteroidTurns()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("asteroid-turns: " + what);
        }
        static bool Close(double a, double b) => Math.Abs(a - b) < 1e-9;

        // A rock turned 90 degrees about Y, then 30 about its own X: an orthonormal frame, rows of its world matrix.
        double c = Math.Cos(Math.PI / 6),
            s = Math.Sin(Math.PI / 6);
        double[] right = [0, 0, -1],
            up = [s, c, 0],
            backward = [c, -s, 0];
        static AsteroidTurnRow Rock(string name, double x, double[] r, double[] u, double[] b) =>
            new(name, 1, [x, 100, 200], r, u, b, [64, 64, 64], [], DateTimeOffset.UnixEpoch);
        AsteroidTurnRow a = Rock("A", 1000, right, up, backward);

        // A point at rock-local (10, 20, 30) lands at the pivot + (10, 20, 30) after the turn; directions likewise.
        double wx = 1000 + 10 * right[0] + 20 * up[0] + 30 * backward[0],
            wy = 100 + 10 * right[1] + 20 * up[1] + 30 * backward[1],
            wz = 200 + 10 * right[2] + 20 * up[2] + 30 * backward[2];
        (double tx, double ty, double tz) = AsteroidTurnPolicy.TurnPoint(a, wx, wy, wz);
        Check(
            Close(tx, 1010) && Close(ty, 120) && Close(tz, 230),
            "a point keeps its place relative to the rock"
        );
        (double rx, double ry, double rz) = AsteroidTurnPolicy.TurnVector(
            a,
            right[0],
            right[1],
            right[2]
        );
        Check(
            Close(rx, 1) && Close(ry, 0) && Close(rz, 0),
            "the rock's right axis becomes the world's"
        );
        Check(!AsteroidTurnPolicy.Aligned(right, up, backward), "a rotated rock is not aligned");
        Check(
            AsteroidTurnPolicy.Aligned([1, 0, 0], [0, 1, 0], [0, 0, 1]),
            "the identity is aligned"
        );

        // Gap: the nearer of the two poses. 100 m out along the rock's right axis is 36 m off the old box, and
        // 100 * cos 30 - 64 m off the new one along the world's z... the nearer counts.
        Check(AsteroidTurnPolicy.Gap(a, 1000, 100, 200, 0) == 0, "the pivot is inside");
        Check(
            Close(
                AsteroidTurnPolicy.Gap(
                    a,
                    1000 + 100 * right[0],
                    100 + 100 * right[1],
                    200 + 100 * right[2],
                    0
                ),
                36
            ),
            "100 m out along the rock's right axis: 36 m off the old box, 36 m off the new one (z = -100)"
        );
        // 100 m along the world's x: 36 m off the new box; in the old one it is (0, 50, 86.6) rock-local, 22.6 m off.
        Check(
            Close(AsteroidTurnPolicy.Gap(a, 1100, 100, 200, 0), 100 * c - 64),
            "the nearer pose counts"
        );
        Check(
            Close(AsteroidTurnPolicy.Gap(a, 1100, 100, 200, 20), 100 * c - 84),
            "a sphere by its surface"
        );
        Check(AsteroidTurnPolicy.Gap(a, 1100, 100, 200, 30) == 0, "never below 0");

        // Pins: a save up to the pinned one predates the turn; a later one, or another partition's, doesn't.
        AsteroidTurnRow pinned = a with
        {
            Pins = [new AsteroidTurnPin(7, 3, 5)],
        };
        Check(AsteroidTurnPolicy.Pinned(pinned, 7, 3, 5), "the pinned save");
        Check(AsteroidTurnPolicy.Pinned(pinned, 7, 2, 99), "an older generation");
        Check(!AsteroidTurnPolicy.Pinned(pinned, 7, 3, 6), "a later save");
        Check(!AsteroidTurnPolicy.Pinned(pinned, 8, 1, 1), "another partition");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "asteroid-turns-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NodeRegistration wa;
            using (
                var registry = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromSeconds(10)
                )
            )
            {
                NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
                registry.Heartbeat(one.Node, one.Epoch, []);
                wa = registry.RegisterNode(
                    Join("wa-1", "wa-one", Guid.NewGuid()) with
                    {
                        Role = NodeRole.WA,
                        ControlEndpoint = "http://127.0.0.1:29419",
                    }
                );
                registry.Heartbeat(wa.Node, wa.Epoch, []);

                // Offline partitions: 7, a dynamic ship 20 m off A's box; 8, a static base 30 m off B's box (x 3000);
                // 9, a ship 500 m from C (x 5000), which a later claim makes count as online.
                void Offline(ulong partition, double x, bool isStatic)
                {
                    long generation = registry
                        .ClaimPartition(partition, one.Node, one.Epoch, 0)
                        .Generation;
                    for (int sequence = 1; sequence <= 2; sequence++)
                        registry.RegisterSave(
                            new SaveId(partition, generation, sequence),
                            one.Node,
                            one.Epoch,
                            SaveKind.Final,
                            1,
                            "b" + sequence,
                            2,
                            $"one/{partition}-{sequence}",
                            "http://one/control"
                        );
                    long member = 1000 + (long)partition;
                    registry.TakeOffline(
                        new OfflineRequest(
                            partition,
                            one.Node,
                            one.Epoch,
                            generation,
                            2,
                            x,
                            100,
                            200,
                            5,
                            [
                                [x, 100, 200, 5],
                            ],
                            [member],
                            0,
                            null,
                            null
                        )
                        {
                            Grids =
                            [
                                new OfflineGrid(member, "g", true, isStatic, 10, x, 100, 200, [1]),
                            ],
                        }
                    );
                }
                Offline(7, 1000 + 64 + 20 + 5, false);
                Offline(8, 3000 + 64 + 30 + 5, true);
                Offline(9, 5000 + 64 + 500 + 5, false);

                AsteroidTurnAnswer Propose(
                    AsteroidTurnRow row,
                    AsteroidTurnMember[] live,
                    bool dry = false
                ) =>
                    registry.ProposeAsteroidTurn(
                        wa.Node,
                        wa.Epoch,
                        wa.AuthorityGeneration,
                        row,
                        live,
                        1000,
                        50,
                        50,
                        dry
                    );

                AsteroidTurnAnswer dryRun = Propose(a, [], dry: true);
                Check(
                    dryRun
                        is {
                            Committed: false,
                            Refused: "dry-run",
                            Row.Pins: [{ Partition: 7, Sequence: 2 }]
                        }
                        && registry.AsteroidTurns().Length == 0,
                    "a dry run names the pins and commits nothing"
                );
                AsteroidTurnAnswer turned = Propose(a, []);
                Check(
                    turned is { Committed: true, Row.Pins: [{ Partition: 7, Sequence: 2 }] },
                    "A turns, and the offline ship 20 m off it is pinned at its newest save: "
                        + System.Text.Json.JsonSerializer.Serialize(turned)
                );
                Check(
                    Propose(a, []) is { Committed: false, Refused: "turned" },
                    "a rock turns once"
                );
                Check(
                    Propose(Rock("B", 3000, right, up, backward), [])
                        is { Committed: false, Refused: "static:8" },
                    "a static grid 30 m off B keeps it"
                );
                AsteroidTurnMember ship = new(42, false, false, 5000 + 64 + 900, 100, 200, 10);
                AsteroidTurnRow rockC = Rock("C", 5000, right, up, backward);
                Check(
                    Propose(rockC, [ship]) is { Refused: "online:42" },
                    "an online ship 890 m off C makes it wait"
                );
                Check(
                    Propose(rockC, [ship with { X = 5000 + 64 + 1200 }])
                        is { Committed: true, Row.Pins: [] },
                    "1190 m off it doesn't, and the offline ship 500 m off C is past memberMeters: no pin"
                );

                // A recovery claim of 7's newest save carries A's turn; a claim of 9 carries none.
                string[] requests =
                [
                    registry.RequestOfflineLoad(7, one.Node, "join"),
                    registry.RequestOfflineLoad(9, one.Node, "join"),
                ];
                RecoveryClaim[] claims = registry.ClaimRecoveries(one.Node, one.Epoch, [7, 9]);
                Check(
                    claims.Length == 2
                        && claims.Single(x => x.Ownership.Partition == 7).AsteroidTurns
                            is [{ Storage: "A" }]
                        && claims.Single(x => x.Ownership.Partition == 9).AsteroidTurns is [],
                    "the claim of a pinned save carries the turn: "
                        + string.Join(",", requests)
                        + " "
                        + string.Join(
                            " ",
                            claims.Select(x =>
                                x.Ownership.Partition + ":" + x.AsteroidTurns?.Length
                            )
                        )
                );
                // 9 is claimed now: it counts as online for the next rock near it.
                Check(
                    Propose(Rock("D", 5000 + 64 + 500 + 5 + 64 + 100, right, up, backward), [])
                        is { Refused: "online:9" },
                    "a partition a load has claimed counts as online"
                );
            }
            using (
                var reopened = new GatewayRegistry(
                    directory,
                    Admission,
                    () => now,
                    TimeSpan.FromSeconds(10)
                )
            )
                Check(
                    reopened.AsteroidTurns().Select(x => x.Storage).SequenceEqual(["A", "C"])
                        && reopened.AsteroidTurns()[0].Pins
                            is [{ Partition: 7, Generation: > 0, Sequence: 2 }],
                    "the turns and their pins survive a reopen"
                );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
