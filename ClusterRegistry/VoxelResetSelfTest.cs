using ClusterPlugin;

namespace ClusterRegistry;

/// <summary>SE-1595 (Shared/Cluster/VoxelResetPolicy.cs, VoxelResetOccupancy.cs): which cubes an edit marks, when a unit is
/// due, what blocks it, how the ledger survives a restart, and what the Gateway counts as occupied.</summary>
internal static partial class SelfTest
{
    private static void TestVoxelReset()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("voxel-reset: " + what);
        }

        // Cubes: an edit's padded box marks every 64-voxel cube it touches, and a key round-trips.
        long[] cubes = VoxelResetPolicy.CubesInBox(-1, 63, 0, 64, 64, 10).ToArray();
        Check(
            cubes.Length == 4,
            "a box across one x and one y boundary marks 4 cubes, got " + cubes.Length
        );
        Check(
            VoxelResetPolicy.CubeOf(VoxelResetPolicy.CubeKey(2047, 3, 1500)) == (2047, 3, 1500),
            "cube key round trip"
        );

        // Due: 48 h after the newest edit, not a second before.
        Check(!VoxelResetPolicy.Due(1000, 1000 + 172799, 172800), "one second early is not due");
        Check(VoxelResetPolicy.Due(1000, 1000 + 172800, 172800), "48 h later is due");

        // Blocked: member spheres by their surface, players by their position.
        double[] min = [0, 0, 0],
            max = [64, 64, 64];
        (double, double, double)[] none = [];
        Check(
            VoxelResetPolicy.Blocked(
                min,
                max,
                [
                    [64 + 350, 32, 32, 60],
                ],
                none,
                300,
                1000
            ) != null,
            "a grid whose AABB sphere reaches within 290 m blocks"
        );
        Check(
            VoxelResetPolicy.Blocked(
                min,
                max,
                [
                    [64 + 400, 32, 32, 60],
                ],
                none,
                300,
                1000
            ) == null,
            "a grid 340 m away does not"
        );
        Check(
            VoxelResetPolicy.Blocked(min, max, [], [(64 + 999, 32, 32)], 300, 1000) != null,
            "a player 999 m away blocks"
        );
        Check(
            VoxelResetPolicy.Blocked(min, max, [], [(64 + 1001, 32, 32)], 300, 1000) == null,
            "a player 1001 m away does not"
        );

        // Ledger: a live edit after a reset counts, a retained entry from before the reset (its own included) does not.
        var ledger = new VoxelEditLedger();
        ledger.Touch("Moon", 0, 0, 0, 10, 10, 10, 100);
        long cube = VoxelResetPolicy.CubeKey(0, 0, 0);
        Check(ledger.LastEdit("Moon", cube) == 100, "touch records the edit");
        ledger.Touch("Moon", 0, 0, 0, 10, 10, 10, 90);
        Check(ledger.LastEdit("Moon", cube) == 100, "an older edit does not move it back");
        ledger.MarkReset("Moon", cube, 200);
        Check(ledger.LastEdit("Moon", cube) == -1, "a reset clears the cube");
        var restarted = VoxelEditLedger.Deserialize(ledger.Serialize());
        restarted.Seed("Moon", 0, 0, 0, 10, 10, 10, 100);
        restarted.Seed("Moon", 0, 0, 0, 10, 10, 10, 200);
        Check(
            restarted.LastEdit("Moon", cube) == -1,
            "seeding the old edit and the reset's own entry adds nothing"
        );
        restarted.Seed("Moon", 0, 0, 0, 10, 10, 10, 201);
        Check(restarted.LastEdit("Moon", cube) == 201, "an edit after the reset is seeded");
        restarted.Touch("Rock", 70, 0, 0, 70, 0, 0, 50);
        Check(
            restarted.NewestEdit("Rock") == 50 && restarted.EditedCubes() == 2,
            "per-storage newest edit and the count"
        );

        // Occupancy: online member spheres (or the bounding sphere of a node predating them) plus offline ones.
        static PartitionReport Report(params PartitionInfo[] partitions) =>
            new(3000, 1000, partitions, 0);
        PartitionInfo withSpheres = new(
            1,
            2,
            false,
            new PartitionSphere(0, 0, 0, 500),
            0,
            [],
            MemberSpheres:
            [
                [1, 2, 3, 4],
                [5, 6, 7, 8],
            ]
        );
        PartitionInfo old = new(2, 1, false, new PartitionSphere(10, 20, 30, 40), 0, []);
        var offline = new OfflinePartition
        {
            Partition = 3,
            X = 9,
            Y = 9,
            Z = 9,
            Radius = 9,
            MemberSpheres =
            [
                [100, 0, 0, 5],
            ],
        };
        DateTime at = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var pass = (at, new[] { ("n1", Report(withSpheres, old)) });

        VoxelResetOccupancy none2 = VoxelResetOccupancy.Build(at, null, [offline], [1]);
        Check(!none2.Complete, "no report pass yet is incomplete");

        VoxelResetOccupancy full = VoxelResetOccupancy.Build(
            at.AddSeconds(5),
            pass,
            [offline],
            [1, 2]
        );
        Check(full.Complete && full.Reason == null, "every live partition reported: complete");
        Check(
            full.Spheres.Length == 4,
            "2 member spheres + 1 bounding sphere + 1 offline member, got " + full.Spheres.Length
        );
        Check(
            full.Spheres.Any(x => x.SequenceEqual(new double[] { 100, 0, 0, 5 })),
            "the offline partition's member is occupied"
        );
        Check(
            full.Spheres.Any(x => x.SequenceEqual(new double[] { 10, 20, 30, 40 })),
            "a node without member spheres falls back to the partition sphere"
        );
        Check(full.AgeSeconds == 5, "age is since the pass");

        VoxelResetOccupancy partial = VoxelResetOccupancy.Build(at, pass, [offline], [1, 2, 77]);
        Check(
            !partial.Complete && partial.Reason!.Contains("77"),
            "a live partition nobody reported makes it incomplete"
        );
    }
}
