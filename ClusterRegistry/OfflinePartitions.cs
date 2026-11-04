using System.Net;
using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// T-0193 offline partitions (Plan/Partitioning.md §3.6): a partition with no online player in or near
/// it, whose content is not moving, is taken OFFLINE - its owner writes a Final save, the registry row
/// becomes unowned and an <see cref="OfflinePartition"/> proxy row records where it is and what it
/// broadcast. No node hosts it. It comes back through the ordinary recovery claim, but only on demand:
/// a player or another partition approaching it (the gateway's wake pass and the World Authority's
/// player pass), a respawn at one of its medical rooms, or a join whose character home is inside it.
///
/// THE CONTENT IS NEVER AT RISK FROM THIS STATE. An offline partition is exactly an unowned partition
/// whose newest catalogued save is a Final: retention keeps a partition's newest saves and everything
/// its recovery cover names, replication keeps two holders of every save, and a clean shutdown already
/// treats an unowned partition's newest save as its state. What the offline row changes is only WHO may
/// claim it: the generic recovery auto-pick never does (that would reload the whole sleeping world on
/// every rotation), a directed load does. If the row is ever lost - a registry rebuilt from disk scans -
/// the partition is simply an unowned partition again and generic recovery loads it: no content is lost
/// either way.
/// </summary>
internal sealed class OfflinePartition
{
    public ulong Partition { get; set; }
    /// <summary>The Final save the partition went offline with - its whole state.</summary>
    public SaveId Save { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Radius { get; set; }
    /// <summary>Per-member bounding spheres, [x,y,z,r] rows in <see cref="Members"/> order: static by construction.</summary>
    public double[][] MemberSpheres { get; set; } = [];
    public long[] Members { get; set; } = [];
    public long EstimatedSnapshotBytes { get; set; }
    /// <summary>AntennaGraphEntry[] JSON as the owner captured it at the freeze - requirement 6.</summary>
    public string? Antennas { get; set; }
    /// <summary>ClusterRespawnPoint[] JSON as the owner captured it at the freeze - the spawn trigger.</summary>
    public string? RespawnPoints { get; set; }
    public string OfflineBy { get; set; } = string.Empty;
    public DateTimeOffset OfflineAt { get; set; }
    public long? OfflineGameFrame { get; set; }
    public System.Text.Json.JsonElement? ProfileSummary { get; set; }
    public string? LoadRequestedNode { get; set; }
    public DateTimeOffset? LoadRequestedAt { get; set; }
    public string? LoadReason { get; set; }
    /// <summary>How many times a directed load has claimed the row since it went offline.</summary>
    public int LoadClaims { get; set; }
    /// <summary>#1354: the NPC grids among the members at the freeze, for the NPC census (NpcCensus.cs).</summary>
    public NpcGrid[] Npc { get; set; } = [];
    /// <summary>SE-810: every grid among the members at the freeze (OfflineGrids.cs). Null while not known: the row came from a
    /// node that predates the field, and a node is to read it from the Final. Never in the table the nodes poll.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OfflineGrid[]? Grids { get; set; }
}

internal sealed record OfflineRequest(ulong Partition, string Node, long Epoch, long Generation, long Sequence,
    double X, double Y, double Z, double Radius, double[][]? MemberSpheres, long[]? Members, long EstimatedSnapshotBytes,
    string? Antennas, string? RespawnPoints)
{
    /// <summary>T-0237: the offer comes from a CLOSED node's drain (a cluster stop or a node rotation) in place
    /// of a release - the Final is already written and the entities already closed, so a refusal means the
    /// node releases the partition unowned as before.</summary>
    public bool Drain { get; init; }
    public long? OfflineGameFrame { get; init; }
    public System.Text.Json.JsonElement? ProfileSummary { get; init; }

    /// <summary>#1354: the NPC grids among the members. Null from a node that predates the field.</summary>
    public NpcGrid[]? Npc { get; init; }

    /// <summary>SE-810: the grids among the members. Null from a node that predates the field.</summary>
    public OfflineGrid[]? Grids { get; init; }
}

internal sealed record OfflineTable(long Revision, bool Unchanged, OfflinePartition[] Partitions)
{
    /// <summary>SE1-0084: the clients whose partition is fast now, filled in by the HTTP route on every answer,
    /// changed or not. The World Authority's wake check skips their players.</summary>
    public ulong[] FastClients { get; init; } = [];
}

internal sealed partial class GatewayRegistry
{
    /// <summary>A directed load is claimable by its target for this long; demand re-requests it
    /// long before, so a target that died or stopped claiming simply lets the next request re-aim.</summary>
    internal static readonly TimeSpan OfflineLoadRequestTtl = TimeSpan.FromSeconds(60);

    /// <summary>How long a claimed offline row may stay owned without its owner reporting members
    /// before the row is dropped: the partition is then an ordinary one, which is always safe.</summary>
    private static readonly TimeSpan OfflineClaimSettle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Partitions that place <paramref name="partition"/> while no report shows it: those of the lineage
    /// records that produced it (inputs and co-outputs: a split's parent and siblings), and the partition the
    /// client's character was last seen in.
    /// </summary>
    public ulong[] PlacementRelatives(ulong partition, ulong client)
    {
        lock (_lock)
        {
            var relatives = new HashSet<ulong>();
            if (_lineageByOutputPartition.TryGetValue(partition, out List<int>? producers))
                foreach (int index in producers.TakeLast(2))
                    if (index >= 0 && index < _state.Lineage.Count)
                    {
                        LineageRecord record = _state.Lineage[index];
                        foreach (PartitionVersion version in record.Inputs.Concat(record.Outputs))
                            relatives.Add(version.Partition);
                    }
            if (_state.CharacterHomes.TryGetValue(client, out CharacterHome? home))
                relatives.Add(home.Partition);
            relatives.Remove(partition);
            return relatives.ToArray();
        }
    }

    public bool IsOffline(ulong partition)
    {
        lock (_lock) return _state.OfflinePartitions.ContainsKey(partition);
    }

    /// <summary>Whether a client's character home lies in an offline partition (or one still loading out of it).</summary>
    public bool HomeOffline(ulong client)
    {
        lock (_lock)
            return _state.CharacterHomes.TryGetValue(client, out CharacterHome? home)
                && _state.OfflinePartitions.ContainsKey(LiveHomePartition(home));
    }

    public bool HasOffline
    {
        get { lock (_lock) return _state.OfflinePartitions.Count != 0; }
    }

    /// <summary>
    /// The owner's offer to take a partition offline. Refused (409) unless the caller owns the row at
    /// the stated generation, no handover is in flight for it, and the Final it names is catalogued and
    /// reachable from a live holder. Idempotent: the same offer after a lost response answers the row it
    /// created. Flushed to disk before it returns, not on the coalesced schedule - a gateway crash inside
    /// the window must not resurrect "owned by N" for content N has already closed.
    /// </summary>
    public OfflinePartition TakeOffline(OfflineRequest request)
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            if (_state.OfflinePartitions.TryGetValue(request.Partition, out OfflinePartition? existing))
            {
                if (existing.Save == new SaveId(request.Partition, request.Generation, request.Sequence)
                    && existing.OfflineBy == request.Node)
                    return existing;
                throw new RegistryConflictException($"Partition {request.Partition} is already offline with save {Key(existing.Save)}");
            }
            if (request.Drain)
            {
                NodeRegistration drainer = RequireNode(request.Node, request.Epoch);
                if (drainer.Role != NodeRole.Regular || drainer.State is not (NodeState.Closed or NodeState.Active))
                    throw new RegistryConflictException($"Node '{request.Node}' epoch {request.Epoch} is {drainer.State}; refusing a drain offline");
            }
            else RequireRegularActiveNode(request.Node, request.Epoch);
            PartitionOwnership owner = RequireOwner(request.Partition, request.Node, request.Epoch, request.Generation);
            if (IsRetired(request.Partition))
                throw new RegistryConflictException($"Partition {request.Partition} is retired");
            if (_state.Handovers.Values.Any(x => x.Partition == request.Partition && x.State is HandoverState.Begun or HandoverState.Active))
                throw new RegistryConflictException($"Partition {request.Partition} has a handover in flight; refusing offline");
            if (!(request.Drain && ShuttingDown) && HasRemoteUse(request.Partition))
                throw new RegistryConflictException($"Partition {request.Partition} has remote-use; refusing offline");
            var saveId = new SaveId(request.Partition, request.Generation, request.Sequence);
            if (!_state.Saves.TryGetValue(Key(saveId), out SaveRegistration? save) || save.PendingDelete || !Reachable(save))
                throw new RegistryConflictException($"Partition {request.Partition} save {Key(saveId)} is not catalogued with a usable holder; refusing offline");
            if (NewestCatalogued(request.Partition)?.SaveId != saveId)
                throw new RegistryConflictException($"Partition {request.Partition} save {Key(saveId)} is not its newest catalogued save; refusing offline");
            if (request.Members is not { Length: > 0 } || !double.IsFinite(request.Radius) || request.Radius < 0)
                throw new ArgumentException("An offline partition needs its members and a finite sphere");
            DateTimeOffset now = _clock();
            owner.Node = null;
            owner.Epoch = 0;
            owner.Generation++;
            owner.LeaseExpires = DateTimeOffset.MinValue;
            owner.Recovering = false;
            Touch(owner);
            var row = new OfflinePartition
            {
                Partition = request.Partition,
                Save = saveId,
                X = request.X, Y = request.Y, Z = request.Z, Radius = request.Radius,
                MemberSpheres = (request.MemberSpheres ?? []).Where(x => x is { Length: >= 4 }).Select(x => x[..4]).ToArray(),
                Members = request.Members,
                EstimatedSnapshotBytes = Math.Max(0, request.EstimatedSnapshotBytes),
                Antennas = request.Antennas,
                RespawnPoints = request.RespawnPoints,
                Npc = request.Npc ?? [],
                Grids = request.Grids,
                OfflineBy = request.Node,
                OfflineAt = now,
                OfflineGameFrame = request.OfflineGameFrame,
                ProfileSummary = request.ProfileSummary,
            };
            _state.OfflinePartitions[request.Partition] = row;
            _state.OfflineRevision++;
            _state.PartitionWeights[request.Partition] = request.Members.Length;
            EmitPartition("offline", request.Partition, null, 0, owner.Generation, "offline");
            Console.WriteLine($"partition {request.Partition} went OFFLINE from {request.Node}: save {Key(saveId)}, "
                + $"{request.Members.Length} members, radius {request.Radius:F0}m, row generation {owner.Generation}");
            // Durable before the owner closes the entities - but through the group commit
            // (FlushOfflineCommit), not a full-registry write per transition: the caller flushes.
            Persist();
            return row;
        }
    }

    private readonly object _offlineCommitLock = new();
    private DateTimeOffset _lastOfflineCommit;

    /// <summary>
    /// Makes every mutation recorded so far durable before an offline commit is answered: the owner closes
    /// the entities on that answer, so the row must be on disk first. A registry write serialises the whole
    /// state under the global lock - 2.4-3.0 s on w32 (37 MB), stalling every join and route for that long -
    /// and one write per transition blocked the gateway for most of the first offline wave (2026-09-23
    /// 21:44-21:48, 72 commits, a nas join burst failing with it). So commits share writes: a caller whose
    /// mutation a completed write (this one or the periodic flush) already covers returns at once, and
    /// writes made for offline commits are spaced at least <paramref name="minGap"/> apart. The owner keeps
    /// the members frozen meanwhile; its offer waits up to a minute and is idempotent.
    /// </summary>
    public void FlushOfflineCommit(TimeSpan minGap)
    {
        long wanted;
        lock (_lock) wanted = _state.StateSequence;
        lock (_offlineCommitLock)
        {
            lock (_lock) if (!_dirty || _durableSequence >= wanted) return;
            TimeSpan wait = _lastOfflineCommit + minGap - _clock();
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            lock (_lock) if (!_dirty || _durableSequence >= wanted) return;
            Flush();
            _lastOfflineCommit = _clock();
        }
    }

    /// <summary>
    /// Demand for an offline partition: aim a directed load at <paramref name="node"/> (the node whose
    /// player or partition is approaching), or, when that one cannot take it or
    /// none is named, at the serving regular node with the fewest partitions (<see cref="FreeOfflineTarget"/>).
    /// Radio/laser call-ups require their named caller node instead of falling back.
    /// Answers what happened; never throws for "not offline" - demand races the load.
    /// </summary>
    public string RequestOfflineLoad(ulong partition, string? node, string reason)
    {
        lock (_lock)
        {
            if (!_state.OfflinePartitions.TryGetValue(partition, out OfflinePartition? row)) return "not-offline";
            if (_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner) && owner.Node != null)
                return "loading:" + owner.Node;
            DateTimeOffset now = _clock();
            // w32 2026-09-25 21:21:38: a join's home partition 253350 was loaded onto node-2 mid-drain; the gateway
            // then switched the client off the closing node without it, and the route pinned the drain until a
            // forced reconnect (node-2 parked, then relaunched).
            IReadOnlyCollection<string> draining = DrainingNodes?.Invoke() ?? [];
            bool callUp = reason is "remote" or "laser";
            string? target = node != null && CanTakeOfflineLoad(node, now, draining) ? node
                : callUp ? null : FreeOfflineTarget(row, node, now, draining);
            if (target == null) return "no-target";
            // Deliberate call-ups race for one unsplittable partition. Keep the first live target
            // until its demand expires; a second requester must use ordinary conflict-checked pull.
            bool reserved = row.LoadReason is "remote" or "laser" && row.LoadRequestedNode is { } reservedNode && row.LoadRequestedAt is { } reservedAt
                && now - reservedAt < OfflineLoadRequestTtl && CanTakeOfflineLoad(reservedNode, now, draining);
            if (reserved && row.LoadRequestedNode != target) return "requested:" + row.LoadRequestedNode;
            bool changed = row.LoadRequestedNode != target || row.LoadRequestedAt is not { } at || now - at >= TimeSpan.FromSeconds(10);
            if (!changed) return "requested:" + target;
            bool first = row.LoadRequestedNode == null || row.LoadRequestedAt is not { } last || now - last >= OfflineLoadRequestTtl;
            row.LoadRequestedNode = target;
            row.LoadRequestedAt = now;
            // An approach on the same node can refresh the request, but cannot demote its call-up reservation.
            if (!reserved) row.LoadReason = reason;
            if (first)
                Console.WriteLine($"offline partition {partition} load requested onto {target}, reason {reason}");
            // Demand is volatile by nature and re-asserted by whoever still has it: the coalesced
            // schedule is fine here, unlike the offline transition itself.
            Persist();
            return "requested:" + target;
        }
    }

    private bool CanTakeOfflineLoad(string node, DateTimeOffset now, IReadOnlyCollection<string> draining) =>
        !draining.Contains(node) && !_state.Scale.Retiring.Contains(node)
        && _state.Nodes.TryGetValue(node, out NodeRegistration? registration)
        && registration.Role == NodeRole.Regular && registration.State == NodeState.Active && registration.Serving
        && !registration.Spare && !_state.SpareNodes.Contains(node) && registration.LeaseExpires > now;

    /// <summary>
    /// T-0337: the pick among nodes when nothing dictates one - fewest partitions, then lowest load, then node id.
    /// A node promoted by a scale-out stayed Serving with 0 partitions for 20+ min: the balancer only moves load
    /// off an overloaded node, and arrivals went to co-located owners or to the lowest projected load.
    /// </summary>
    internal static string? FewestPartitionsNode(IEnumerable<(string Node, int Partitions, long Load)> eligible) => eligible
        .OrderBy(x => x.Partitions).ThenBy(x => x.Load).ThenBy(x => x.Node, StringComparer.Ordinal)
        .Select(x => x.Node).FirstOrDefault();

    /// <summary>
    /// T-0337: where an offline load goes when no node dictates it (a join whose home is offline, a request
    /// naming no node, a named node that cannot take it). A request that is still fresh keeps its target, so
    /// re-asserted demand does not re-aim a load its target is about to claim. Otherwise the serving node with
    /// the fewest partitions (directed loads on their way counted) on the host of the node that could not take
    /// it, else on a host holding the save (a same-host read, T-0323), else anywhere. Caller holds _lock.
    /// </summary>
    private string? FreeOfflineTarget(OfflinePartition row, string? unable, DateTimeOffset now, IReadOnlyCollection<string> draining)
    {
        if (row.LoadRequestedNode is { } asked && row.LoadRequestedAt is { } at && now - at < OfflineLoadRequestTtl
            && CanTakeOfflineLoad(asked, now, draining))
            return asked;
        NodeRegistration[] eligible = _state.Nodes.Values.Where(x => CanTakeOfflineLoad(x.Node, now, draining)).ToArray();
        // #1454: not onto a heavy component's node while another node can take it.
        if (HeavyNodes?.Invoke() is { Count: > 0 } heavy && eligible.Where(x => !heavy.Contains(x.Node)).ToArray() is { Length: > 0 } light)
            eligible = light;
        if (eligible.Length < 2) return eligible.FirstOrDefault()?.Node;
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
        if (unable != null) { if (HostOfNode(unable) is { } host) hosts.Add(host); }
        else if (TryGetSave(Key(row.Save), out SaveRegistration? save))
            foreach (string holder in save.LocationHolders.Values.Distinct(StringComparer.Ordinal))
                if (HostOfNode(holder) is { } host) hosts.Add(host);
        NodeRegistration[] local = eligible.Where(x => HostOfNode(x.Node) is { } host && hosts.Contains(host)).ToArray();
        if (local.Length != 0) eligible = local;
        Dictionary<string, int> partitions = eligible.ToDictionary(x => x.Node, _ => 0, StringComparer.Ordinal);
        foreach (PartitionOwnership owner in _state.Partitions.Values)
            if (owner.Node != null && partitions.ContainsKey(owner.Node)) partitions[owner.Node]++;
        foreach (OfflinePartition other in _state.OfflinePartitions.Values)
            if (other.LoadRequestedNode != null && partitions.ContainsKey(other.LoadRequestedNode)
                && other.LoadRequestedAt is { } requested && now - requested < OfflineLoadRequestTtl
                && _state.Partitions.TryGetValue(other.Partition, out PartitionOwnership? owner) && owner.Node == null)
                partitions[other.LoadRequestedNode]++;
        return FewestPartitionsNode(eligible.Select(x => (x.Node, partitions[x.Node], x.ProjectedLoad)));
    }

    /// <summary>How long a recovery claim waits for an emptier node of the same host to ask for it (T-0337).</summary>
    internal static readonly TimeSpan RecoveryClaimPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// T-0337: nodes pull recoveries, so the registry only decides which asker gets one. The emptier node the
    /// asker has to wait for, or null when the asker may claim: a peer with fewer partitions that can take the
    /// claim and has not been waited for longer than <paramref name="patience"/> without being granted one.
    /// </summary>
    internal static string? RecoveryClaimWaitsFor(int askerPartitions,
        IEnumerable<(string Node, int Partitions, long Load, DateTimeOffset? WaitedSince)> peers, DateTimeOffset now, TimeSpan patience) =>
        FewestPartitionsNode(peers.Where(x => x.Partitions < askerPartitions && (x.WaitedSince is not { } since || now - since < patience))
            .Select(x => (x.Node, x.Partitions, x.Load)));

    /// <summary>T-0337: since when askers have been waiting for a node to claim a recovery; cleared by a grant to
    /// it. An entry older than the patience stops the waiting until then. Memory only.</summary>
    private readonly Dictionary<string, (DateTimeOffset Since, bool GivenUp)> _recoveryWaitedFor = new(StringComparer.Ordinal);

    /// <summary>
    /// T-0337: the generic recovery pick of <paramref name="claimant"/> waits when a serving node of its host
    /// (of any host when its host is unknown) holds fewer partitions and can take <paramref name="weight"/>:
    /// that node asks within a second. Caller holds _lock.
    /// </summary>
    private string? RecoveryWaitsForEmptier(NodeRegistration claimant, Dictionary<string, int> owned, long weight, DateTimeOffset now)
    {
        int mine = owned.GetValueOrDefault(claimant.Node);
        if (mine == 0) return null;
        IReadOnlyCollection<string>? draining = null;
        string? host = HostOfNode(claimant.Node);
        List<(string Node, int Partitions, long Load, DateTimeOffset? WaitedSince)> peers = [];
        foreach (NodeRegistration peer in _state.Nodes.Values)
        {
            int theirs = owned.GetValueOrDefault(peer.Node);
            if (peer.Node == claimant.Node || theirs >= mine) continue;
            if (!CanTakeOfflineLoad(peer.Node, now, draining ??= DrainingNodes?.Invoke() ?? [])) continue;
            if (host != null && !string.Equals(HostOfNode(peer.Node), host, StringComparison.OrdinalIgnoreCase)) continue;
            if (peer.RecoveryCapacity > 0 && theirs >= peer.RecoveryCapacity) continue;
            if (peer.LoadCapacity > 0 && weight > peer.LoadCapacity - peer.ProjectedLoad) continue;
            peers.Add((peer.Node, theirs, peer.ProjectedLoad, _recoveryWaitedFor.TryGetValue(peer.Node, out var waited) ? waited.Since : null));
        }
        string? emptier = RecoveryClaimWaitsFor(mine, peers, now, RecoveryClaimPatience);
        if (emptier != null && _recoveryWaitedFor.TryAdd(emptier, (now, false)))
            Console.WriteLine($"recovery: {claimant.Node} ({mine} partitions) waits for {emptier} ({owned.GetValueOrDefault(emptier)}) to claim first");
        foreach ((string peer, _, _, DateTimeOffset? since) in peers)
            if (since is { } at && now - at >= RecoveryClaimPatience && !_recoveryWaitedFor[peer].GivenUp)
            {
                _recoveryWaitedFor[peer] = (at, true);
                Console.WriteLine($"recovery: {peer} did not claim within {RecoveryClaimPatience.TotalSeconds:F0}s; nobody waits for it until it does");
            }
        return emptier;
    }

    /// <summary>The directed loads a claiming node should take first, before any generic pick. Caller holds _lock.</summary>
    private ulong[] DirectedOfflineLoads(string node, DateTimeOffset now) => _state.OfflinePartitions.Values
        .Where(row => row.LoadRequestedNode == node && row.LoadRequestedAt is { } at && now - at < OfflineLoadRequestTtl
            && _state.Partitions.TryGetValue(row.Partition, out PartitionOwnership? owner) && owner.Node == null
            && !IsRetired(row.Partition))
        .OrderBy(row => row.LoadRequestedAt).Select(row => row.Partition).Take(4).ToArray();

    /// <summary>Caller holds _lock. The row stops being offline: loaded (members reported), merged away
    /// (lineage input) or settled as an ordinary partition.</summary>
    private bool DropOfflineRow(ulong partition, string why)
    {
        if (!_state.OfflinePartitions.Remove(partition, out OfflinePartition? row)) return false;
        _state.OfflineRevision++;
        if (why is not ("retired" or "row-removed")) NoteArrival(partition);
        EmitPartition("online", partition, null, 0, 0, why);
        Console.WriteLine($"offline partition {partition} is back ONLINE ({why}); offline since {row.OfflineAt:O}, "
            + $"save {Key(row.Save)}, {row.LoadClaims} load claim(s), last load reason {row.LoadReason ?? "none"}");
        return true;
    }

    /// <summary>Caller holds _lock: rows whose partition has been owned for a long time without its
    /// owner ever reporting members are ordinary partitions now.</summary>
    private void SettleOfflineRows(DateTimeOffset now)
    {
        foreach (OfflinePartition row in _state.OfflinePartitions.Values.ToArray())
        {
            if (IsRetired(row.Partition))
            {
                DropOfflineRow(row.Partition, "retired");
                continue;
            }
            if (!_state.Partitions.TryGetValue(row.Partition, out PartitionOwnership? owner))
            {
                DropOfflineRow(row.Partition, "row-removed");
                continue;
            }
            if (owner.Node != null && row.LoadRequestedAt is { } at && now - at >= OfflineClaimSettle)
                DropOfflineRow(row.Partition, "claimed-settled");
        }
    }

    public OfflineTable OfflinePartitions(long since)
    {
        lock (_lock)
            return since == _state.OfflineRevision && since != 0
                ? new OfflineTable(_state.OfflineRevision, true, [])
                : new OfflineTable(_state.OfflineRevision, false,
                    _state.OfflinePartitions.Values.OrderBy(x => x.Partition).Select(Copy).ToArray());
    }

    public OfflinePartition? OfflineRow(ulong partition)
    {
        lock (_lock) return _state.OfflinePartitions.TryGetValue(partition, out OfflinePartition? row) ? Copy(row) : null;
    }

    /// <summary>Snapshot for the gateway's wake pass: geometry only, no antenna payloads.</summary>
    public OfflinePartition[] OfflineGeometry()
    {
        lock (_lock)
            return _state.OfflinePartitions.Values
                .Where(row => !_state.Partitions.TryGetValue(row.Partition, out PartitionOwnership? owner) || owner.Node == null)
                .Select(row => new OfflinePartition
                {
                    Partition = row.Partition, Save = row.Save, X = row.X, Y = row.Y, Z = row.Z, Radius = row.Radius,
                    MemberSpheres = row.MemberSpheres, Members = row.Members, EstimatedSnapshotBytes = row.EstimatedSnapshotBytes,
                    LoadRequestedNode = row.LoadRequestedNode, LoadRequestedAt = row.LoadRequestedAt, LoadReason = row.LoadReason,
                }).ToArray();
    }

    private static OfflinePartition Copy(OfflinePartition row) => new()
    {
        Partition = row.Partition, Save = row.Save, X = row.X, Y = row.Y, Z = row.Z, Radius = row.Radius,
        MemberSpheres = row.MemberSpheres, Members = row.Members, EstimatedSnapshotBytes = row.EstimatedSnapshotBytes,
        Antennas = row.Antennas, RespawnPoints = row.RespawnPoints, OfflineBy = row.OfflineBy, OfflineAt = row.OfflineAt,
        OfflineGameFrame = row.OfflineGameFrame, ProfileSummary = row.ProfileSummary,
        LoadRequestedNode = row.LoadRequestedNode, LoadRequestedAt = row.LoadRequestedAt, LoadReason = row.LoadReason,
        LoadClaims = row.LoadClaims, Npc = row.Npc,
    };
}

internal sealed partial class DirectGateway
{
    /// <summary>The last complete set of served-node partition reports the automatic loop gathered,
    /// for the offline admission check on the HTTP threads.</summary>
    private (DateTime At, (string Node, PartitionReport Report)[] Reports)? _lastPartitionReports
    {
        get { lock (_offlineWakeAsked) return _lastPartitionReportsValue; }
        set { lock (_offlineWakeAsked) _lastPartitionReportsValue = value; }
    }
    private (DateTime At, (string Node, PartitionReport Report)[] Reports)? _lastPartitionReportsValue;

    /// <summary>Per offline partition: when the wake pass last asked for it, so one approach is one
    /// request every few seconds rather than one per pass.</summary>
    private readonly Dictionary<ulong, DateTime> _offlineWakeAsked = [];

    // Gateway-side knobs, read from the same handover-config document the nodes read (Plan/Partitioning.md §3.6).
    private double OfflineConfig(string key, double fallback) =>
        _registry != null && _registry.HandoverConfig().Values.TryGetValue(key, out string? value)
            && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed : fallback;

    private double OfflineWakeMeters => OfflineConfig("offline.wakeMeters", 5000);
    private double OfflineLeadSeconds => OfflineConfig("offline.leadSeconds", 15);

    /// <summary><see cref="CostModel.MaxGridSpeed"/> from the handover config: the highest member speed the velocity
    /// lookahead counts, the world's top speed (100 m/s vanilla).</summary>
    private double LookaheadMaxSpeed => OfflineConfig("lookahead.maxSpeed", 100);

    /// <summary>The cost model with this pass's <see cref="LookaheadMaxSpeed"/>, for everything that measures envelope gaps.</summary>
    private CostModel GapModel(CostModel model) => model with { MaxGridSpeed = LookaheadMaxSpeed };

    /// <summary>
    /// How close a reported partition may come to an offline one before it is demand for it. A client-bearing
    /// partition: <c>offline.wakeMeters</c> (at least the envelope), so the content is there before the player
    /// can see it. A MOVING partition: the co-location envelope D + m, so the pair is co-located from the
    /// moment the offline one exists again and the ordinary same-node merge takes over (requirement 5, merge).
    /// A STATIC partition is no demand at all short of the merge distance D itself: nothing is coming, and
    /// counting a parked neighbour would keep every co-located static group online forever - each member
    /// would refuse to go offline because of the other.
    /// </summary>
    private double OfflineDemandThreshold(PartitionInfo live, bool player, PartitionReport report) =>
        player ? Math.Max(OfflineWakeMeters, report.SyncDistance + report.Margin)
        : live.MaxMemberSpeed > OfflineMovingSpeed ? report.SyncDistance + report.Margin
        : report.SyncDistance;

    private const double OfflineMovingSpeed = 0.5;

    /// <summary>T-0269: a wake whose demander's node carries more than this multiple of the mean node load is
    /// placed on the least-loaded node instead (runtime knob offline.wakeOverloadRatio, 0 = always the demander).</summary>
    private double OfflineWakeOverloadRatio => OfflineConfig("offline.wakeOverloadRatio", 1.5);

    /// <summary>
    /// T-0269: where a woken offline partition loads. The demander's node by default: the player (or the
    /// partition merging with it) is about to be co-located with it, and loading it there saves that handover.
    /// But every approach wake used to land there, and a node whose ~60 pilots flew past offline partitions
    /// took 184 loads in 30 min (18 -> 224 partitions, sim 0.9 -> 0.28) until Havok ran out of memory
    /// (node-1 w33 2026-09-27 23:45:23), while the paced balancer moved one component a minute off it. So
    /// when the demander's node is above <paramref name="ratio"/> x the mean load, the wake goes to the
    /// least-loaded node; co-location still joins the two if the player really arrives. An overlap stays with
    /// the demander: the two already touch, and anywhere else is an immediate handover.
    /// </summary>
    internal static string OfflineWakeTarget(string demander, string reason, IReadOnlyDictionary<string, double> nodeLoad, double ratio)
    {
        if (reason == "overlap" || ratio <= 0 || nodeLoad.Count < 2 || !nodeLoad.TryGetValue(demander, out double load)) return demander;
        double mean = nodeLoad.Values.Average();
        if (load <= ratio * mean) return demander;
        return nodeLoad.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
    }

    /// <summary>Seconds a load of this row is expected to take on top of the lead: a station-sized
    /// partition sits in the parallel init pipeline for a minute or more (~5 MB/s of save is generous).</summary>
    private static double OfflineLoadSeconds(OfflinePartition row) => Math.Min(120, row.EstimatedSnapshotBytes / 5_000_000.0);

    /// <summary>
    /// Smallest velocity-adjusted gap between a reported partition and an offline one. The offline side is
    /// static (4-wide rows); the reported side contributes its closing speed through the same row formats
    /// <see cref="EnvelopeGap"/> reads. Sphere bound first, member scan only when the bound cannot settle it.
    /// Closing speed is capped at 2 x <paramref name="maxSpeed"/>, the same pair cap EnvelopeGap uses.
    /// </summary>
    internal static double OfflineGap(PartitionInfo live, OfflinePartition row, double lead, double threshold, double maxSpeed)
    {
        double cap = 2 * maxSpeed;
        double dx = live.Sphere.X - row.X, dy = live.Sphere.Y - row.Y, dz = live.Sphere.Z - row.Z;
        double bound = Math.Sqrt(dx * dx + dy * dy + dz * dz) - live.Sphere.Radius - row.Radius
            - Math.Min(live.MaxMemberSpeed, cap) * lead;
        if (bound >= threshold) return bound;
        if (live.MemberSpheres is not { Length: > 0 } ma || row.MemberSpheres is not { Length: > 0 } mb) return bound;
        double min = double.MaxValue;
        foreach (double[] x in ma)
            foreach (double[] y in mb)
            {
                double ex = x[0] - y[0], ey = x[1] - y[1], ez = x[2] - y[2];
                double distance = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                double closing;
                if (x.Length >= 8)
                    closing = distance > 0 ? Math.Max(0, -(x[5] * ex + x[6] * ey + x[7] * ez) / distance) : Math.Sqrt(x[5] * x[5] + x[6] * x[6] + x[7] * x[7]);
                else closing = x.Length > 4 ? x[4] : 0;
                double gap = distance - x[3] - y[3] - Math.Min(closing, cap) * lead;
                if (gap < min) min = gap;
            }
        return min;
    }

    /// <summary>
    /// The wake pass (requirement 5, "enters" and "merge"): a reported partition closing on an offline one
    /// pulls it onto its own node, at the distance <see cref="OfflineDemandThreshold"/> gives for it, with a
    /// closing-speed lookahead of the lead plus the expected load time. The node of a client-bearing
    /// partition is preferred, then the nearest.
    /// </summary>
    private void WakeOfflinePartitions((ServedNode Node, PartitionReport Report)[] reports, HashSet<ulong> routed)
    {
        if (_registry == null || reports.Length == 0) return;
        OfflinePartition[] offline = _registry.OfflineGeometry();
        DateTime now = DateTime.UtcNow;
        lock (_offlineWakeAsked)
            foreach (ulong stale in _offlineWakeAsked.Keys.Where(id => offline.All(row => row.Partition != id)).ToArray())
                _offlineWakeAsked.Remove(stale);
        if (offline.Length == 0) return;
        double leadBase = OfflineLeadSeconds;
        double maxSpeed = LookaheadMaxSpeed;
        // A report taken before the owner's close still lists the partition that just went offline (and a
        // loading node lists it before its heartbeat drops the row): that is the row's own content, never a
        // demand on it or on another offline row.
        var offlineIds = new HashSet<ulong>(offline.Select(row => row.Partition));
        // T-0237: only content the registry says the reporting node OWNS is demand. A booting node loads its
        // assigned seed saves fenced until its startup claim settles - stale copies by construction - and one
        // of them woke the Earth base on a start that was to load nothing (w32 2026-09-25 00:07:43: node-2's
        // fence-tainted seed partition 5, generation 1, "overlapped" offline 199400, whose own members it held).
        var ownedBy = reports.Select(pair => pair.Node).DistinctBy(node => node.Node)
            .ToDictionary(node => node.Node, node => _registry.PartitionsOwnedBy(node.Node, node.Epoch).Select(x => x.Partition).ToHashSet());
        // T-0269: per-node load from the same reports, for the placement below.
        bool simLoad = reports.All(pair => pair.Report.SimLoadSupported);
        Dictionary<string, double> nodeLoad = [];
        Dictionary<string, int> nodePartitions = [];
        foreach ((ServedNode node, PartitionReport report) in reports)
        {
            nodeLoad[node.Node] = nodeLoad.GetValueOrDefault(node.Node) + report.Partitions.Sum(p => (double)PartitionLoad(p, simLoad));
            nodePartitions[node.Node] = nodePartitions.GetValueOrDefault(node.Node) + report.Partitions.Length;
        }
        foreach (OfflinePartition row in offline)
        {
            double lead = leadBase + OfflineLoadSeconds(row);
            (string Node, ulong Partition, double Gap, bool Player)? best = null;
            foreach ((ServedNode node, PartitionReport report) in reports)
            {
                foreach (PartitionInfo live in report.Partitions)
                {
                    if (offlineIds.Contains(live.Id)) continue;
                    if (!ownedBy[node.Node].Contains(live.Id)) continue;
                    bool player = live.Clients.Length != 0 || routed.Contains(live.Id);
                    double threshold = OfflineDemandThreshold(live, player, report);
                    double gap = OfflineGap(live, row, lead, threshold, maxSpeed);
                    if (gap >= threshold) continue;
                    // T-0292: a fast partition flies past offline content without loading it.
                    if (live.Fast) { FastPartitionCounters.Wake(); continue; }
                    if (best is not { } current || player && !current.Player || player == current.Player && gap < current.Gap)
                        best = (node.Node, live.Id, gap, player);
                }
            }
            if (best is not { } demand) continue;
            lock (_offlineWakeAsked)
            {
                if (_offlineWakeAsked.TryGetValue(row.Partition, out DateTime asked) && now - asked < TimeSpan.FromSeconds(5)) continue;
                _offlineWakeAsked[row.Partition] = now;
            }
            string reason = demand.Player ? "approach" : demand.Gap < 0 ? "overlap" : "merge";
            string target = OfflineWakeTarget(demand.Node, reason, nodeLoad, OfflineWakeOverloadRatio);
            string outcome = _registry.RequestOfflineLoad(row.Partition, target, reason);
            if (target != demand.Node)
            {
                // Count the diverted load where it went, so one pass does not send every wake to the same node.
                double each = nodePartitions.GetValueOrDefault(demand.Node) is > 0 and var n ? nodeLoad[demand.Node] / n : 0;
                nodeLoad[target] += each;
            }
            if (row.LoadRequestedNode == null)
                Console.WriteLine($"Offline wake: partition {row.Partition} {reason} by partition {demand.Partition} on {demand.Node}, "
                    + $"gap {demand.Gap:F0}m (lead {lead:F0}s): {outcome}"
                    + (target != demand.Node ? $" (placed off {demand.Node}: load {nodeLoad[demand.Node]:F0} > {OfflineWakeOverloadRatio:0.0}x mean {nodeLoad.Values.Average():F0})" : ""));
        }
    }

    /// <summary>
    /// The gateway's half of the offline admission: the owner has checked its own content (sleeping, no
    /// characters, no player near by the cluster position registry); here the question is whether any
    /// OTHER partition, on any node, is close enough that the wake pass would pull it straight back.
    /// Answered from the automatic loop's last reports; refuses when those are not fresh.
    /// </summary>
    // T-0237: zero. The 15 s spacing dates from full-state registry writes (~3 s under the global lock); since
    // the SQLite registry (T-0227) a write is one small transaction, and the spacing only made every offline
    // wait out the 15 s tick (w32 2026-09-24: median 12.7 s per offline, of which 0.5-2 s was work).
    // Concurrent offers still share one write (FlushOfflineCommit returns at once for a covered mutation).
    // 1 s (Viktor 2026-09-25): zero let a node's offlines finish back to back, each closing a partition's grids
    // on its game thread in one frame (300-700 ms on w32); a second between commits leaves frames to breathe.
    // The node posts its offer off the game thread and the registry serves each request on the thread pool, so
    // the wait delays only the offering node's next offline, never a game frame or another request.
    private static readonly TimeSpan OfflineCommitGap = TimeSpan.FromSeconds(1);

    /// <summary>How long a connected client's unreported, unplaceable route keeps refusing every offer.</summary>
    private static readonly TimeSpan UnreportedRouteGrace = TimeSpan.FromSeconds(120);
    private readonly HashSet<(ulong Client, ulong Partition)> _staleRoutesLogged = [];

    private string? OfflineGeometryRefusal(OfflineRequest request, Dictionary<ulong, ulong> routedPartitions)
    {
        if (_lastPartitionReports is not { } snapshot || DateTime.UtcNow - snapshot.At > TimeSpan.FromSeconds(15))
            return "no fresh partition reports";
        var candidate = new OfflinePartition
        {
            Partition = request.Partition, X = request.X, Y = request.Y, Z = request.Z, Radius = request.Radius,
            MemberSpheres = (request.MemberSpheres ?? []).Where(x => x is { Length: >= 4 }).ToArray(),
            EstimatedSnapshotBytes = request.EstimatedSnapshotBytes,
        };
        double lead = OfflineLeadSeconds + OfflineLoadSeconds(candidate);
        double maxSpeed = LookaheadMaxSpeed;
        // Hysteresis: refuse well outside where the wake pass would load it back.
        const double Hysteresis = 2000;
        var reportedById = new Dictionary<ulong, (string Node, PartitionInfo Info, PartitionReport Report)>();
        foreach ((string node, PartitionReport report) in snapshot.Reports)
            foreach (PartitionInfo info in report.Partitions)
                reportedById[info.Id] = (node, info, report);
        // A connected client's partition is client-bearing from the moment its route exists: the node report
        // re-associates a joining client only after the player binding settles, and the cluster position
        // registry has no position for it yet either (w32 2026-09-23 21:44:39: pilot 7's parked ship went
        // offline 8 s after the pilot was world-ready, its character standing 2 m away in its own partition).
        // A routed partition the reports do not show yet cannot be measured, so it refuses.
        //
        // Scoped by distance (T-0193, CM 2026-09-23 23:03: 14 refusals in 55 min, each blocking every offer
        // cluster-wide for a report interval). The unreported partition is placed by a reported relative: a
        // partition of the lineage record that produced it (a split's parent or sibling), or the partition
        // the client's character was last seen in. That relative is measured as client-bearing. With no
        // relative the route refuses while its client joined less than UnreportedRouteGrace ago, and after
        // that stops blocking (logged once): a route that stays unreported is stale, and a stale route must
        // not hold every offer in the cluster.
        foreach ((ulong routedPartition, ulong client) in routedPartitions)
        {
            if (reportedById.ContainsKey(routedPartition) || _registry!.IsOffline(routedPartition)) continue;
            ulong[] relatives = _registry.PlacementRelatives(routedPartition, client).Where(reportedById.ContainsKey).ToArray();
            if (relatives.Length == 0)
            {
                if (_clients.TryGetValue(client, out ClientSession? session) && DateTimeOffset.UtcNow - session.ConnectedAt < UnreportedRouteGrace)
                    return $"connected client {client} is routed to partition {routedPartition}, which no report shows yet";
                if (_staleRoutesLogged.Count > 4096) _staleRoutesLogged.Clear();
                if (_staleRoutesLogged.Add((client, routedPartition)))
                    Console.WriteLine($"Offline admission: client {client}'s route to partition {routedPartition} has no report and no "
                        + $"reported relative {UnreportedRouteGrace.TotalSeconds:F0}s after join; no longer refusing offers for it");
                continue;
            }
            foreach (ulong relative in relatives)
            {
                if (relative == request.Partition)
                    return $"connected client {client} is routed to partition {routedPartition}, which no report shows yet, "
                        + "and was produced from or last seen in this one";
                (string node, PartitionInfo info, PartitionReport report) = reportedById[relative];
                double threshold = OfflineDemandThreshold(info, true, report) + Hysteresis;
                double gap = OfflineGap(info, candidate, lead, threshold, maxSpeed);
                if (gap < threshold)
                    return $"connected client {client} is routed to partition {routedPartition}, which no report shows yet; "
                        + $"its relative {relative} on {node} is {gap:F0}m away (limit {threshold:F0}m)";
            }
        }
        foreach ((string node, PartitionReport report) in snapshot.Reports)
        {
            foreach (PartitionInfo live in report.Partitions)
            {
                if (live.Id == request.Partition) continue;
                bool player = live.Clients.Length != 0 || routedPartitions.ContainsKey(live.Id);
                bool moving = live.MaxMemberSpeed > OfflineMovingSpeed;
                double threshold = OfflineDemandThreshold(live, player, report) + (player || moving ? Hysteresis : 0);
                double gap = OfflineGap(live, candidate, lead, threshold, maxSpeed);
                // T-0292: a fast partition passing by does not hold content online (it will not wake it either).
                if (gap < threshold && live.Fast) { FastPartitionCounters.OfflineRefusal(); continue; }
                if (gap < threshold)
                    return $"partition {live.Id} on {node} is {gap:F0}m away (limit {threshold:F0}m{(player ? ", client-bearing" : moving ? ", moving" : ", static")})";
            }
        }
        return null;
    }

    private void HandleOfflineRoutes(HttpListenerContext context, string path)
    {
        if (path.StartsWith("/registry/partitions/offline/grids", StringComparison.OrdinalIgnoreCase))
        {
            HandleOfflineGridRoutes(context, path);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals("/registry/partitions/offline", StringComparison.OrdinalIgnoreCase))
        {
            OfflineRequest request = ReadJson<OfflineRequest>(context);
            if (_registry!.OfflineRow(request.Partition) is { } done && done.Save == new SaveId(request.Partition, request.Generation, request.Sequence))
            {
                // A retry of a commit whose answer was lost: still durable before the owner closes.
                _registry.FlushOfflineCommit(OfflineCommitGap);
                WriteJson(context, 200, done);
                return;
            }
            // T-0237: a cluster stop takes every partition offline a node offers (it keeps back what a plugin
            // keeps online, SE-904), so the next start loads only those and each other partition comes back
            // only on demand (a join, an approach, a spawn). Clients are already
            // disconnected and every node is Closed, so neither a route nor a live neighbour can wake it.
            if (request.Drain && _registry.ShuttingDown)
            {
                OfflinePartition stopped = _registry.TakeOffline(request);
                _registry.FlushOfflineCommit(TimeSpan.Zero);
                WriteJson(context, 200, stopped);
                return;
            }
            var routedPartitions = new Dictionary<ulong, ulong>();
            foreach (ulong client in _clients.Keys)
                if (_registry.RoutedPartition(client) is ulong routedPartition)
                {
                    if (routedPartition == request.Partition)
                        throw new RegistryConflictException($"Partition {request.Partition} is routed to connected client {client}; refusing offline");
                    routedPartitions[routedPartition] = client;
                }
            NodeRegistration? offering = _registry.Nodes().FirstOrDefault(n => n.Node == request.Node);
            if (offering?.ControlEndpoint == null || GetPartitions(StripScheme(offering.ControlEndpoint)).Partitions
                .Any(p => p.Id == request.Partition && p.RemoteUsers is { Length: > 0 }) || _registry.HasRemoteUse(request.Partition))
                throw new RegistryConflictException($"Partition {request.Partition} has remote-use; refusing offline");
            if (MigrationTarget(request.Partition) is { } pinned)
                throw new RegistryConflictException($"Partition {request.Partition} is pinned for migration to {pinned}; refusing offline");
            if (OfflineGeometryRefusal(request, routedPartitions) is { } refusal)
                throw new RegistryConflictException($"Partition {request.Partition} refused offline: {refusal}");
            OfflinePartition committed = _registry.TakeOffline(request);
            // A draining node's offers (rotation) take no gap: that node is leaving, its frames carry no player.
            _registry.FlushOfflineCommit(request.Drain ? TimeSpan.Zero : OfflineCommitGap);
            WriteJson(context, 200, committed);
            return;
        }
        if (context.Request.HttpMethod == "GET" && path.Equals("/registry/partitions/offline", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.QueryString["partition"] is { } single)
            {
                OfflinePartition? row = _registry!.OfflineRow(ulong.Parse(single));
                if (row == null) WriteJson(context, 404, new { error = "not offline" });
                else WriteJson(context, 200, row);
                return;
            }
            long since = long.TryParse(context.Request.QueryString["since"], out long parsed) ? parsed : 0;
            WriteJson(context, 200, _registry!.OfflinePartitions(since) with { FastClients = FastClientsNow() });
            return;
        }
        if (context.Request.HttpMethod == "POST" && path.Equals("/registry/partitions/offline/load", StringComparison.OrdinalIgnoreCase))
        {
            ulong partition = ulong.Parse(Required(context, "partition"));
            string? node = context.Request.QueryString["node"];
            string reason = context.Request.QueryString["reason"] ?? "request";
            WriteJson(context, 200, new { partition, outcome = _registry!.RequestOfflineLoad(partition, string.IsNullOrWhiteSpace(node) ? null : node, reason) });
            return;
        }
        throw new ArgumentException("Unknown offline partition route");
    }
}
