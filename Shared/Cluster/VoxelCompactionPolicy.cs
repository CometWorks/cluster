using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClusterPlugin;

/// <summary>
/// SE-1893: the rules of the World Authority's voxel journal compaction, kept free of game types so the Gateway
/// self-test runs them without a game: when the nightly run starts, how far a storage's journal may be cut, and
/// which entries a voxel reset has overwritten.
/// </summary>
internal static class VoxelCompactionPolicy
{
    /// <summary>"HH:mm" as a time of day, null for an empty value (no daily run).</summary>
    public static TimeSpan? ParseTimeOfDay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (
            !TimeSpan.TryParseExact(
                value.Trim(),
                @"hh\:mm",
                CultureInfo.InvariantCulture,
                out TimeSpan time
            )
            || time >= TimeSpan.FromDays(1)
        )
            throw new FormatException("Not a time of day (HH:mm): " + value);
        return time;
    }

    /// <summary>
    /// An IANA zone id such as Europe/Stockholm, or UTC. .NET Framework on Windows knows only Windows ids, so the
    /// European zones a cluster is likely to name map to theirs there; an unknown id throws.
    /// </summary>
    public static TimeZoneInfo Zone(string id)
    {
        if (
            string.IsNullOrWhiteSpace(id)
            || id.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase)
        )
            return TimeZoneInfo.Utc;
        id = id.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception) when (WindowsIds.TryGetValue(id, out string windows))
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windows);
        }
    }

    private static readonly Dictionary<string, string> WindowsIds = new(StringComparer.Ordinal)
    {
        ["Europe/Stockholm"] = "W. Europe Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Amsterdam"] = "W. Europe Standard Time",
        ["Europe/Budapest"] = "Central Europe Standard Time",
        ["Europe/London"] = "GMT Standard Time",
    };

    /// <summary>
    /// The first instant after <paramref name="nowUtc"/> at which the wall clock of <paramref name="zone"/> shows
    /// <paramref name="timeOfDay"/>. Europe/Stockholm has 04:00 on both DST change days (its clocks change at 02:00
    /// and 03:00), so the run starts at 03:00 UTC in winter and 02:00 UTC in summer. A time the clocks skip runs at
    /// the first minute after the jump; a time shown twice runs at its first showing.
    /// </summary>
    public static DateTimeOffset NextDaily(
        DateTimeOffset nowUtc,
        TimeSpan timeOfDay,
        TimeZoneInfo zone
    )
    {
        DateTime localDate = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        for (int day = -1; day <= 2; day++)
        {
            DateTimeOffset utc = ToUtc(
                DateTime.SpecifyKind(localDate.AddDays(day) + timeOfDay, DateTimeKind.Unspecified),
                zone
            );
            if (utc > nowUtc)
                return utc;
        }
        throw new InvalidOperationException("No next compaction time found");
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        // The larger offset of a repeated hour is its first showing (summer time).
        TimeSpan offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>A node as the Gateway lists it: regular or World Authority, and whether it is Active.</summary>
    public sealed record Consumer(string Node, long Epoch, bool Regular, bool Active);

    /// <summary>
    /// How far a storage's journal may be cut once a base holds everything up to <paramref name="tail"/>: the tail,
    /// or the lowest applied watermark of an active regular node if that is lower. A node that has not reported the
    /// storage in its current epoch counts as zero. It may be replaying the journal or catching up from an old base,
    /// and it reports a storage only once it has applied it in order, never while it skips or replays, so it may still
    /// need every entry. A node that is not Active does not count: the Gateway takes a stopped or frozen node out of
    /// Active when its lease runs out or its simulation stalls, and a retired one when it drains. Returns the cut and
    /// the nodes that hold it below the tail, as "node:applied".
    /// </summary>
    public static (long Cut, string[] Blockers) Cut(
        long tail,
        IEnumerable<Consumer> nodes,
        IReadOnlyDictionary<string, (long Epoch, long Applied)> reported
    )
    {
        long cut = tail;
        List<(string Node, long Applied)> below = new();
        foreach (Consumer node in nodes)
        {
            if (!node.Regular || !node.Active)
                continue;
            long applied =
                reported.TryGetValue(node.Node, out (long Epoch, long Applied) row)
                && row.Epoch == node.Epoch
                    ? row.Applied
                    : 0;
            if (applied < tail)
                below.Add((node.Node, applied));
            cut = Math.Min(cut, applied);
        }
        return (
            Math.Max(0, cut),
            below
                .OrderBy(x => x.Applied)
                .ThenBy(x => x.Node, StringComparer.Ordinal)
                .Select(x => x.Node + ":" + x.Applied.ToString(CultureInfo.InvariantCulture))
                .ToArray()
        );
    }

    /// <summary>
    /// True when every 64-voxel cube a range entry's box touches was reset by a later entry. A reset writes its whole
    /// cube with absolute content, so the entry is overwritten for every consumer whatever its watermark.
    /// <paramref name="resetAt"/> gives the sequence of a cube's newest reset entry, or null. An entry that crosses
    /// into a cube not reset since keeps its content.
    /// </summary>
    public static bool Superseded(
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        long sequence,
        Func<long, long?> resetAt
    )
    {
        foreach (long cube in VoxelResetPolicy.CubesInBox(minX, minY, minZ, maxX, maxY, maxZ))
            if (!(resetAt(cube) is long reset && reset > sequence))
                return false;
        return true;
    }
}
