using System.Net;
using ClusterPlugin;

namespace ClusterRegistry;

/// <summary>The Gateway's answer to a proposed asteroid turn (SE-1998).</summary>
internal sealed record AsteroidTurnAnswer(bool Committed, string? Refused, AsteroidTurnRow? Row);

/// <summary>
/// SE-1998: the World Authority turns the asteroids to the identity orientation one at a time, while the cluster runs.
/// The Gateway is where a turn is decided and kept: it commits one only while no online partition is near the rock,
/// and pins the saves of the offline partitions that are near it. Those partitions' members are turned as they load
/// (the recovery claim carries the turns that cover its saves), so nothing ever rewrites a save. Rows are never
/// removed; nodes fetch them all (GET /registry/asteroid-turns) and turn their own copy of each rock.
/// </summary>
internal sealed partial class GatewayRegistry
{
    public AsteroidTurnRow[] AsteroidTurns()
    {
        lock (_lock)
            return
            [
                .. _state
                    .AsteroidTurns.Values.OrderBy(x => x.At)
                    .ThenBy(x => x.Storage, StringComparer.Ordinal),
            ];
    }

    /// <summary>
    /// Decides a turn the World Authority proposes. <paramref name="live"/> are the member spheres of the online
    /// partitions (from the last complete report pass); the offline rows are read here, under the lock, so a load that
    /// claims one of them can't slip between the decision and the pins. A row a load has claimed counts as online.
    /// </summary>
    public AsteroidTurnAnswer ProposeAsteroidTurn(
        string node,
        long epoch,
        long generation,
        AsteroidTurnRow proposal,
        IEnumerable<AsteroidTurnMember> live,
        double gridMeters,
        double memberMeters,
        double staticMeters,
        bool dryRun
    )
    {
        if (
            string.IsNullOrWhiteSpace(proposal.Storage)
            || proposal.Pivot is not { Length: 3 }
            || proposal.Right is not { Length: 3 }
            || proposal.Up is not { Length: 3 }
            || proposal.Backward is not { Length: 3 }
            || proposal.Half is not { Length: 3 }
            || proposal.Half.Any(x => !(x > 0))
        )
            throw new ArgumentException(
                "An asteroid turn needs a storage, a pivot, three axes and a positive half size"
            );
        lock (_lock)
        {
            NodeRegistration current = RequireActiveNode(node, epoch);
            RequireWorldAuthority(node, epoch, generation);
            if (current.LeaseExpires <= _clock() || !WorldAuthorityLive())
                throw new RegistryConflictException(
                    $"World Authority generation {generation} is fenced"
                );
            if (_state.AsteroidTurns.TryGetValue(proposal.Storage, out AsteroidTurnRow? done))
                return new(false, "turned", done);
            var members = new List<AsteroidTurnMember>(live);
            foreach (OfflinePartition row in _state.OfflinePartitions.Values)
            {
                bool claimed =
                    _state.Partitions.TryGetValue(row.Partition, out PartitionOwnership? owner)
                    && (owner.Node != null || owner.Recovering);
                HashSet<long> statics =
                [
                    .. (row.Grids ?? []).Where(g => g.Static).Select(g => g.Id),
                ];
                double[][] spheres =
                    row.MemberSpheres.Length == row.Members.Length && row.MemberSpheres.Length > 0
                        ? row.MemberSpheres
                        :
                        [
                            [row.X, row.Y, row.Z, row.Radius],
                        ];
                for (int i = 0; i < spheres.Length; i++)
                {
                    double[] s = spheres[i];
                    if (s.Length < 4)
                        continue;
                    bool isStatic =
                        spheres.Length == row.Members.Length && statics.Contains(row.Members[i]);
                    members.Add(new(row.Partition, !claimed, isStatic, s[0], s[1], s[2], s[3]));
                }
            }
            string? refused = AsteroidTurnPolicy.Decide(
                proposal,
                members,
                gridMeters,
                memberMeters,
                staticMeters,
                out ulong[] near
            );
            if (refused != null)
                return new(false, refused, null);
            // Every save a load of a near partition could still read, its lineage's included, holds the old poses:
            // the pin is the newest of them per partition, so a fallback to an older save is covered too.
            var pins = new Dictionary<ulong, AsteroidTurnPin>();
            foreach (ulong partition in near)
            foreach (SaveCover item in ResolveCover(partition, long.MaxValue, null, []))
            {
                SaveId id = item.Save.SaveId;
                if (
                    !pins.TryGetValue(id.Partition, out AsteroidTurnPin? old)
                    || id.Generation > old.Generation
                    || id.Generation == old.Generation && id.Sequence > old.Sequence
                )
                    pins[id.Partition] = new(id.Partition, id.Generation, id.Sequence);
            }
            if (near.Any(p => !pins.ContainsKey(p)))
                return new(false, "uncovered:" + near.First(p => !pins.ContainsKey(p)), null);
            AsteroidTurnRow committed = proposal with
            {
                Pins = [.. pins.Values.OrderBy(x => x.Partition)],
                At = _clock(),
            };
            if (dryRun)
                return new(false, "dry-run", committed);
            _state.AsteroidTurns[committed.Storage] = committed;
            Persist();
            Console.WriteLine(
                $"asteroid_turn committed storage={committed.Storage} pins={string.Join(",", committed.Pins.Select(p => $"{p.Partition}/{p.Generation}/{p.Sequence}"))}"
            );
            return new(true, null, committed);
        }
    }

    /// <summary>The committed turns whose pins cover any of these saves: a load of them must turn its members.</summary>
    private AsteroidTurnRow[] AsteroidTurnsCovering(IEnumerable<SaveCover> saves)
    {
        if (_state.AsteroidTurns.Count == 0)
            return [];
        SaveId[] ids = [.. saves.Select(x => x.Save.SaveId)];
        return
        [
            .. _state.AsteroidTurns.Values.Where(row =>
                ids.Any(id =>
                    AsteroidTurnPolicy.Pinned(row, id.Partition, id.Generation, id.Sequence)
                )
            ),
        ];
    }
}

internal sealed partial class DirectGateway
{
    /// <summary>
    /// SE-1998: GET lists every committed turn; POST (the World Authority, with node, epoch and generation) proposes
    /// one, a turn row in the body with the WA's gridMeters, memberMeters, staticMeters and dryRun as query values.
    /// The online members are judged from the last complete partition report pass, under the voxel reset's rule that
    /// a live partition with no known position makes the answer "incomplete".
    /// </summary>
    private bool HandleAsteroidTurns(HttpListenerContext context, string path)
    {
        if (
            _registry == null
            || !path.Equals("/registry/asteroid-turns", StringComparison.OrdinalIgnoreCase)
        )
            return false;
        if (context.Request.HttpMethod == "GET")
        {
            // ?known=N: a node that already holds N rows gets none back while that is still all of them.
            AsteroidTurnRow[] rows = _registry.AsteroidTurns();
            bool same =
                int.TryParse(context.Request.QueryString["known"], out int known)
                && known == rows.Length;
            WriteJson(
                context,
                200,
                new { Count = rows.Length, Rows = same ? [] : rows },
                redact: false
            );
            return true;
        }
        if (context.Request.HttpMethod != "POST")
            return false;
        AsteroidTurnRow proposal = ReadJson<AsteroidTurnRow>(context);
        VoxelResetOccupancy occupancy = VoxelResetOccupancy.Build(
            DateTime.UtcNow,
            _lastPartitionReports,
            _registry.OfflinePartitions(0).Partitions,
            _registry.LivePartitions(),
            _voxelResetMemory
        );
        if (!occupancy.Complete)
        {
            WriteJson(
                context,
                200,
                new AsteroidTurnAnswer(false, "incomplete: " + occupancy.Reason, null),
                redact: false
            );
            return true;
        }
        var live = new List<AsteroidTurnMember>();
        var listed = new HashSet<ulong>();
        if (_lastPartitionReports is { } pass)
            foreach (PartitionInfo partition in pass.Reports.SelectMany(x => x.Report.Partitions))
            {
                listed.Add(partition.Id);
                foreach (double[] s in VoxelResetOccupancy.RowsOf(partition))
                    live.Add(new(partition.Id, false, false, s[0], s[1], s[2], s[3]));
            }
        // Live partitions the pass didn't list are judged by the spheres they were last reported with.
        lock (_voxelResetMemory)
            foreach (VoxelResetOccupancy.LivePartition partition in _registry.LivePartitions())
                if (
                    !listed.Contains(partition.Id)
                    && _voxelResetMemory.LastSpheres.TryGetValue(partition.Id, out double[][]? last)
                )
                    foreach (double[] s in last)
                        live.Add(new(partition.Id, false, false, s[0], s[1], s[2], s[3]));
        static double Number(HttpListenerContext context, string key, double fallback) =>
            double.TryParse(
                context.Request.QueryString[key],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value
            )
                ? value
                : fallback;
        WriteJson(
            context,
            200,
            _registry.ProposeAsteroidTurn(
                Required(context, "node"),
                long.Parse(Required(context, "epoch")),
                long.Parse(Required(context, "generation")),
                proposal,
                live,
                Number(context, "gridMeters", 1000),
                Number(context, "memberMeters", 50),
                Number(context, "staticMeters", 50),
                context.Request.QueryString["dryRun"] == "true"
            ),
            redact: false
        );
        return true;
    }
}
