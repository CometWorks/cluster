using ClusterPlugin;

namespace ClusterRegistry;

/// <summary>SE-1893 (Shared/Cluster/VoxelCompactionPolicy.cs): when the nightly compaction starts, how far a storage's
/// journal is cut and who holds it back, and which entries a voxel reset overwrote.</summary>
internal static partial class SelfTest
{
    private static void TestVoxelCompaction()
    {
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("voxel-compaction: " + what);
        }

        // Schedule: 04:00 Stockholm is 03:00 UTC in winter (CET) and 02:00 UTC in summer (CEST).
        TimeZoneInfo stockholm = VoxelCompactionPolicy.Zone("Europe/Stockholm");
        TimeSpan four = VoxelCompactionPolicy.ParseTimeOfDay("04:00")!.Value;
        DateTimeOffset Next(string nowUtc) =>
            VoxelCompactionPolicy.NextDaily(DateTimeOffset.Parse(nowUtc), four, stockholm);
        Check(
            Next("2026-11-26T02:00:00Z") == DateTimeOffset.Parse("2026-11-26T03:00:00Z"),
            "winter: today 03:00 UTC"
        );
        Check(
            Next("2026-11-26T03:00:00Z") == DateTimeOffset.Parse("2026-11-27T03:00:00Z"),
            "at the time: the next day"
        );
        Check(
            Next("2026-11-26T03:00:01Z") == DateTimeOffset.Parse("2026-11-27T03:00:00Z"),
            "after it: the next day"
        );
        Check(
            Next("2026-07-01T01:59:00Z") == DateTimeOffset.Parse("2026-07-01T02:00:00Z"),
            "summer: 02:00 UTC"
        );
        Check(
            Next("2026-11-26T23:30:00Z") == DateTimeOffset.Parse("2026-11-27T03:00:00Z"),
            "local midnight passed in UTC"
        );
        // DST change days: spring forward 2026-03-29 02:00 -> 03:00, fall back 2026-10-25 03:00 -> 02:00 local.
        Check(
            Next("2026-03-29T00:00:00Z") == DateTimeOffset.Parse("2026-03-29T02:00:00Z"),
            "spring forward day"
        );
        Check(
            Next("2026-03-28T03:00:00Z") == DateTimeOffset.Parse("2026-03-29T02:00:00Z"),
            "the night before spring forward"
        );
        Check(
            Next("2026-10-25T00:00:00Z") == DateTimeOffset.Parse("2026-10-25T03:00:00Z"),
            "fall back day"
        );
        Check(
            Next("2026-10-24T02:00:00Z") == DateTimeOffset.Parse("2026-10-25T03:00:00Z"),
            "the night before fall back"
        );
        // A time the clocks skip (02:30 on the spring day) runs at 03:00 local; a repeated one at its first showing.
        TimeSpan skipped = VoxelCompactionPolicy.ParseTimeOfDay("02:30")!.Value;
        Check(
            VoxelCompactionPolicy.NextDaily(
                DateTimeOffset.Parse("2026-03-29T00:00:00Z"),
                skipped,
                stockholm
            ) == DateTimeOffset.Parse("2026-03-29T01:00:00Z"),
            "a skipped time runs right after the jump"
        );
        Check(
            VoxelCompactionPolicy.NextDaily(
                DateTimeOffset.Parse("2026-10-24T23:00:00Z"),
                skipped,
                stockholm
            ) == DateTimeOffset.Parse("2026-10-25T00:30:00Z"),
            "a repeated time runs at its first showing"
        );
        Check(
            VoxelCompactionPolicy.NextDaily(
                DateTimeOffset.Parse("2026-11-26T05:00:00Z"),
                four,
                VoxelCompactionPolicy.Zone("UTC")
            ) == DateTimeOffset.Parse("2026-11-27T04:00:00Z"),
            "UTC zone"
        );
        Check(VoxelCompactionPolicy.ParseTimeOfDay("") == null, "an empty time is no daily run");
        bool rejected = false;
        try
        {
            VoxelCompactionPolicy.ParseTimeOfDay("24:00");
        }
        catch (FormatException)
        {
            rejected = true;
        }
        Check(rejected, "24:00 is rejected");

        // Cut: the lowest current-epoch watermark of an active regular node, an unreported one counting as zero.
        VoxelCompactionPolicy.Consumer[] nodes =
        [
            new("node-0", 5, true, true),
            new("node-1", 7, true, true),
            new("node-2", 3, true, false), // dead, closed or empty: does not count
            new("world-authority", 2, false, true),
        ];
        Dictionary<string, (long Epoch, long Applied)> rows = new()
        {
            ["node-0"] = (5, 900),
            ["node-1"] = (7, 1000),
            ["node-2"] = (3, 10),
        };
        (long cut, string[] blockers) = VoxelCompactionPolicy.Cut(1000, nodes, rows);
        Check(
            cut == 900 && blockers.SequenceEqual(["node-0:900"]),
            $"lowest live watermark: {cut} {string.Join(",", blockers)}"
        );
        rows["node-0"] = (5, 1200);
        (cut, blockers) = VoxelCompactionPolicy.Cut(1000, nodes, rows);
        Check(cut == 1000 && blockers.Length == 0, "never past the base tail, and nobody blocks");
        rows["node-1"] = (6, 1000); // reported by its previous process
        (cut, blockers) = VoxelCompactionPolicy.Cut(1000, nodes, rows);
        Check(
            cut == 0 && blockers.SequenceEqual(["node-1:0"]),
            "a restarted node that has not reported again blocks"
        );
        rows.Remove("node-1");
        (cut, blockers) = VoxelCompactionPolicy.Cut(1000, nodes, rows);
        Check(cut == 0 && blockers.SequenceEqual(["node-1:0"]), "an unreported live node blocks");
        nodes[1] = nodes[1] with { Active = false };
        (cut, blockers) = VoxelCompactionPolicy.Cut(1000, nodes, rows);
        Check(
            cut == 1000 && blockers.Length == 0,
            "once the Gateway takes it out of Active it no longer blocks"
        );

        // Superseded: every cube an entry touches was reset after it.
        Dictionary<long, long> resets = new() { [VoxelResetPolicy.CubeKey(1, 0, 0)] = 50 };
        long? At(long cube) => resets.TryGetValue(cube, out long at) ? at : null;
        Check(
            VoxelCompactionPolicy.Superseded(64, 0, 0, 127, 63, 63, 49, At),
            "an entry inside a later reset cube"
        );
        Check(
            !VoxelCompactionPolicy.Superseded(64, 0, 0, 127, 63, 63, 50, At),
            "the reset entry itself is not"
        );
        Check(
            !VoxelCompactionPolicy.Superseded(64, 0, 0, 127, 63, 63, 60, At),
            "an entry after the reset is not"
        );
        Check(
            !VoxelCompactionPolicy.Superseded(60, 0, 0, 70, 10, 10, 49, At),
            "an entry crossing into a cube not reset stays"
        );
        resets[VoxelResetPolicy.CubeKey(0, 0, 0)] = 51;
        Check(
            VoxelCompactionPolicy.Superseded(60, 0, 0, 70, 10, 10, 49, At),
            "once both its cubes were reset after it, it goes"
        );
        resets[VoxelResetPolicy.CubeKey(0, 0, 0)] = 40;
        Check(
            !VoxelCompactionPolicy.Superseded(60, 0, 0, 70, 10, 10, 49, At),
            "a reset before the entry does not count"
        );
    }
}
