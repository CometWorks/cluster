using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// SE-1595: everything the World Authority's voxel reset must keep away from, as [x,y,z,r] member spheres: the
/// members of every partition in the last complete report pass of the serving nodes, plus the members of every
/// offline partition.
/// <para>
/// SE-1706: a live partition the pass does not list is judged by the member spheres it was last seen with
/// (<see cref="Memory"/>), since its saved content may come back there. One its owner left out of a complete report
/// for <see cref="MemberlessAfter"/> with no known position is memberless: its content left the node (a grid closed
/// by trash, MES or combat) and the node keeps the lease until its drain releases it (NodeScaling reads such a row the
/// same way). Live on Octahedron those rows were always there, and the reset waited for them forever. Complete is
/// false only while a partition has no known position at all: never seen and recovering, unowned, owned by a node
/// that did not report, or new since the pass.
/// </para>
/// </summary>
internal sealed record VoxelResetOccupancy(
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("ageSeconds")] double AgeSeconds,
    [property: JsonPropertyName("spheres")] double[][] Spheres,
    [property: JsonPropertyName("remembered")] int Remembered = 0,
    [property: JsonPropertyName("memberless")] int Memberless = 0
)
{
    internal static readonly TimeSpan MemberlessAfter = TimeSpan.FromSeconds(60);

    internal readonly record struct LivePartition(ulong Id, string? Node, bool Recovering);

    /// <summary>What the Gateway has seen of each partition, across passes. Lives as long as the Gateway process.</summary>
    internal sealed class Memory
    {
        internal readonly Dictionary<ulong, double[][]> LastSpheres = [];
        internal readonly Dictionary<ulong, DateTime> UnlistedSince = [];

        /// <summary>Every complete pass, so a partition listed only briefly (a recovery whose grid trash then closes)
        /// is remembered too, not just the ones that happen to be listed when the World Authority asks.</summary>
        internal void Observe((string Node, PartitionReport Report)[] reports)
        {
            lock (this)
                foreach (PartitionInfo partition in reports.SelectMany(x => x.Report.Partitions))
                {
                    LastSpheres[partition.Id] = Rows(partition);
                    UnlistedSince.Remove(partition.Id);
                }
        }
    }

    private static double[][] Rows(PartitionInfo partition)
    {
        PartitionSphere s = partition.Sphere;
        // A node predating memberSpheres: the bounding sphere covers every member.
        return partition.MemberSpheres is { Length: > 0 } member
            ? [.. member.Where(x => x.Length >= 4)]
            :
            [
                [s.X, s.Y, s.Z, s.Radius],
            ];
    }

    internal static VoxelResetOccupancy Build(
        DateTime now,
        (DateTime At, (string Node, PartitionReport Report)[] Reports)? pass,
        IEnumerable<OfflinePartition> offline,
        IEnumerable<LivePartition> livePartitions,
        Memory memory
    )
    {
        lock (memory)
            return BuildLocked(now, pass, offline, livePartitions, memory);
    }

    private static VoxelResetOccupancy BuildLocked(
        DateTime now,
        (DateTime At, (string Node, PartitionReport Report)[] Reports)? pass,
        IEnumerable<OfflinePartition> offline,
        IEnumerable<LivePartition> livePartitions,
        Memory memory
    )
    {
        if (pass is not { } reports)
            return new(false, "no complete partition report pass yet", 0, []);
        var spheres = new List<double[]>();
        var known = new HashSet<ulong>();
        foreach (PartitionInfo partition in reports.Reports.SelectMany(x => x.Report.Partitions))
        {
            known.Add(partition.Id);
            double[][] rows = Rows(partition);
            spheres.AddRange(rows);
            memory.LastSpheres[partition.Id] = rows;
            memory.UnlistedSince.Remove(partition.Id);
        }
        foreach (OfflinePartition row in offline)
        {
            known.Add(row.Partition);
            double[][] rows =
                row.MemberSpheres.Length > 0
                    ? [.. row.MemberSpheres.Where(x => x.Length >= 4)]
                    :
                    [
                        [row.X, row.Y, row.Z, row.Radius],
                    ];
            spheres.AddRange(rows);
            memory.LastSpheres[row.Partition] = rows;
            memory.UnlistedSince.Remove(row.Partition);
        }
        HashSet<string> reporters = [.. reports.Reports.Select(x => x.Node)];
        HashSet<ulong> alive = [.. known];
        var missing = new List<ulong>();
        int remembered = 0,
            memberless = 0;
        foreach (LivePartition live in livePartitions)
        {
            alive.Add(live.Id);
            if (known.Contains(live.Id))
                continue;
            if (memory.LastSpheres.TryGetValue(live.Id, out double[][]? last))
            {
                spheres.AddRange(last);
                remembered++;
                continue;
            }
            if (live.Recovering || live.Node == null || !reporters.Contains(live.Node))
            {
                missing.Add(live.Id);
                continue;
            }
            // The owner reported everything it simulates, and this one was not in it.
            if (!memory.UnlistedSince.TryGetValue(live.Id, out DateTime since))
                memory.UnlistedSince[live.Id] = since = reports.At;
            if (reports.At - since >= MemberlessAfter)
                memberless++;
            else
                missing.Add(live.Id);
        }
        // Rows that are gone (retired, reaped) are forgotten.
        foreach (ulong gone in memory.LastSpheres.Keys.Where(x => !alive.Contains(x)).ToArray())
            memory.LastSpheres.Remove(gone);
        foreach (ulong gone in memory.UnlistedSince.Keys.Where(x => !alive.Contains(x)).ToArray())
            memory.UnlistedSince.Remove(gone);
        double age = Math.Round(Math.Max(0, (now - reports.At).TotalSeconds), 1);
        return missing.Count == 0
            ? new(true, null, age, spheres.ToArray(), remembered, memberless)
            : new(
                false,
                $"partitions without a known position: {string.Join(",", missing.Take(4))}"
                    + (missing.Count > 4 ? $" (+{missing.Count - 4})" : ""),
                age,
                spheres.ToArray(),
                remembered,
                memberless
            );
    }
}

internal sealed partial class GatewayRegistry
{
    /// <summary>SE-1595: partitions that hold content and are not offline (not retired by a merge), with their owner.</summary>
    public VoxelResetOccupancy.LivePartition[] LivePartitions()
    {
        lock (_lock)
            return _state
                .Partitions.Where(x =>
                    !IsRetired(x.Key) && !_state.OfflinePartitions.ContainsKey(x.Key)
                )
                .Select(x => new VoxelResetOccupancy.LivePartition(
                    x.Key,
                    x.Value.Node,
                    x.Value.Recovering
                ))
                .ToArray();
    }
}
