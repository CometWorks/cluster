using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// SE-1595: everything the World Authority's voxel reset must keep away from, as [x,y,z,r] member spheres: the
/// members of every partition in the last complete report pass of the serving nodes, plus the members of every
/// offline partition. Complete is false while some live partition is in neither (unowned, recovering, or created
/// after the pass): its members have no known position, so the reset waits.
/// </summary>
internal sealed record VoxelResetOccupancy(
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("ageSeconds")] double AgeSeconds,
    [property: JsonPropertyName("spheres")] double[][] Spheres
)
{
    internal static VoxelResetOccupancy Build(
        DateTime now,
        (DateTime At, (string Node, PartitionReport Report)[] Reports)? pass,
        IEnumerable<OfflinePartition> offline,
        IEnumerable<ulong> livePartitions
    )
    {
        if (pass is not { } reports)
            return new(false, "no complete partition report pass yet", 0, []);
        var spheres = new List<double[]>();
        var known = new HashSet<ulong>();
        foreach (PartitionInfo partition in reports.Reports.SelectMany(x => x.Report.Partitions))
        {
            known.Add(partition.Id);
            PartitionSphere s = partition.Sphere;
            // A node predating memberSpheres: the bounding sphere covers every member.
            spheres.AddRange(
                partition.MemberSpheres is { Length: > 0 } rows
                    ? rows.Where(x => x.Length >= 4)
                    :
                    [
                        [s.X, s.Y, s.Z, s.Radius],
                    ]
            );
        }
        foreach (OfflinePartition row in offline)
        {
            known.Add(row.Partition);
            spheres.AddRange(
                row.MemberSpheres.Length > 0
                    ? row.MemberSpheres.Where(x => x.Length >= 4)
                    :
                    [
                        [row.X, row.Y, row.Z, row.Radius],
                    ]
            );
        }
        ulong[] missing = livePartitions.Where(x => !known.Contains(x)).Take(4).ToArray();
        double age = Math.Round(Math.Max(0, (now - reports.At).TotalSeconds), 1);
        return missing.Length == 0
            ? new(true, null, age, spheres.ToArray())
            : new(
                false,
                "partitions without a position: " + string.Join(",", missing),
                age,
                spheres.ToArray()
            );
    }
}

internal sealed partial class GatewayRegistry
{
    /// <summary>SE-1595: partitions that hold content and are not offline (not retired by a merge).</summary>
    public ulong[] LivePartitionIds()
    {
        lock (_lock)
            return _state
                .Partitions.Keys.Where(x =>
                    !IsRetired(x) && !_state.OfflinePartitions.ContainsKey(x)
                )
                .ToArray();
    }
}
