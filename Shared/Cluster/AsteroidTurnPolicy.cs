#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

/// <summary>SE-1998: a partition's saves up to this one hold poses from before the turn.</summary>
internal sealed record AsteroidTurnPin(ulong Partition, long Generation, long Sequence);

/// <summary>
/// SE-1998: one asteroid turned to the identity orientation about its center (the voxel map's matrix translation).
/// Right, Up and Backward are the rock's axes before the turn (the rows of its old world matrix); after it they are
/// the world axes. Half is the storage box's half size per rock axis, in meters. Pins: the saves the Gateway found
/// near the rock when it committed the turn, whose members are turned as their partition loads.
/// </summary>
internal sealed record AsteroidTurnRow(
    string Storage,
    long Voxel,
    double[] Pivot,
    double[] Right,
    double[] Up,
    double[] Backward,
    double[] Half,
    AsteroidTurnPin[] Pins,
    DateTimeOffset At
);

/// <summary>A member sphere [x, y, z, r] the Gateway judges a turn by.</summary>
internal readonly record struct AsteroidTurnMember(
    ulong Partition,
    bool Offline,
    bool Static,
    double X,
    double Y,
    double Z,
    double Radius
);

/// <summary>SE-1998: the geometry and the decision of an asteroid turn, with no game types, so the Gateway shares it.</summary>
internal static class AsteroidTurnPolicy
{
    /// <summary>The rock is aligned when its axes are the world's within this (per component).</summary>
    public const double AlignedEpsilon = 1e-6;

    public static bool Aligned(double[] right, double[] up, double[] backward) =>
        Near(right, 1, 0, 0) && Near(up, 0, 1, 0) && Near(backward, 0, 0, 1);

    private static bool Near(double[] v, double x, double y, double z) =>
        Math.Abs(v[0] - x) <= AlignedEpsilon
        && Math.Abs(v[1] - y) <= AlignedEpsilon
        && Math.Abs(v[2] - z) <= AlignedEpsilon;

    private static double Dot(double[] a, double x, double y, double z) =>
        a[0] * x + a[1] * y + a[2] * z;

    /// <summary>A world direction turned with the rock: its rock-local coordinates become world ones.</summary>
    public static (double X, double Y, double Z) TurnVector(
        AsteroidTurnRow row,
        double x,
        double y,
        double z
    ) => (Dot(row.Right, x, y, z), Dot(row.Up, x, y, z), Dot(row.Backward, x, y, z));

    /// <summary>A world point turned with the rock about its pivot.</summary>
    public static (double X, double Y, double Z) TurnPoint(
        AsteroidTurnRow row,
        double x,
        double y,
        double z
    )
    {
        (double lx, double ly, double lz) = TurnVector(
            row,
            x - row.Pivot[0],
            y - row.Pivot[1],
            z - row.Pivot[2]
        );
        return (row.Pivot[0] + lx, row.Pivot[1] + ly, row.Pivot[2] + lz);
    }

    /// <summary>
    /// Meters from a sphere's surface to the rock's storage box, the nearer of the box before the turn and after it
    /// (0 when they touch or overlap). A member near either pose counts as near the rock.
    /// </summary>
    public static double Gap(AsteroidTurnRow row, double x, double y, double z, double radius)
    {
        double dx = x - row.Pivot[0],
            dy = y - row.Pivot[1],
            dz = z - row.Pivot[2];
        double before = BoxDistance(
            Dot(row.Right, dx, dy, dz),
            Dot(row.Up, dx, dy, dz),
            Dot(row.Backward, dx, dy, dz),
            row.Half
        );
        double after = BoxDistance(dx, dy, dz, row.Half);
        return Math.Max(0, Math.Min(before, after) - radius);
    }

    private static double BoxDistance(double x, double y, double z, double[] half)
    {
        double ox = Math.Max(Math.Abs(x) - half[0], 0),
            oy = Math.Max(Math.Abs(y) - half[1], 0),
            oz = Math.Max(Math.Abs(z) - half[2], 0);
        return Math.Sqrt(ox * ox + oy * oy + oz * oz);
    }

    /// <summary>
    /// The Gateway's verdict on a proposed turn: a refusal reason, or null with the offline partitions to pin.
    /// A static grid within <paramref name="staticMeters"/> keeps the rock as it is for good (a base built on it);
    /// any online member within <paramref name="gridMeters"/> makes it wait; offline members within
    /// <paramref name="memberMeters"/> are turned when they load.
    /// </summary>
    public static string? Decide(
        AsteroidTurnRow row,
        IEnumerable<AsteroidTurnMember> members,
        double gridMeters,
        double memberMeters,
        double staticMeters,
        out ulong[] pinned
    )
    {
        pinned = [];
        var pins = new SortedSet<ulong>();
        string? online = null;
        foreach (AsteroidTurnMember m in members)
        {
            double gap = Gap(row, m.X, m.Y, m.Z, m.Radius);
            if (m.Static && gap <= staticMeters)
                return "static:" + m.Partition;
            if (!m.Offline && gap <= gridMeters)
                online ??= "online:" + m.Partition;
            else if (m.Offline && gap <= memberMeters)
                pins.Add(m.Partition);
        }
        if (online != null)
            return online;
        pinned = [.. pins];
        return null;
    }

    /// <summary>Whether the save (partition, generation, sequence) holds poses from before the turn.</summary>
    public static bool Pinned(
        AsteroidTurnRow row,
        ulong partition,
        long generation,
        long sequence
    ) =>
        row.Pins.Any(p =>
            p.Partition == partition
            && (generation < p.Generation || generation == p.Generation && sequence <= p.Sequence)
        );
}
