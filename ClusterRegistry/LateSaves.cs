namespace ClusterRegistry;

/// <summary>One Final save of a node's local stop (T-0347), as the next start reports it from the writer's journal.</summary>
internal sealed record LateSaveReport(ulong Partition, long Generation, long Sequence, long SizeBytes, string Checksum, long ClusterTime,
    string Location, string Writer, long WriterEpoch, int? Entities = null, string? Homes = null);
internal sealed record LateSaveRequest(LateSaveReport[]? Saves);
internal sealed record LateSaveVerdict(ulong Partition, long Generation, long Sequence, bool Accepted, string Reason);

/// <summary>
/// T-0347: Final saves the registry never saw registered. A cluster stop with the registry down asks every node for a
/// local stop; the node writes a Final save of each partition it owns and journals it (UnregisteredFinals), because
/// nothing can catalogue it. The next start is a Recovery start, and recovery reads the catalogue - whose newest save
/// of those partitions is the last Periodic one. Two things keep that start from recovering the older save:
///
/// The report (<see cref="RegisterLateSaves"/>). The first node of each host reports its host's journals before its
/// first recovery claim. A save is catalogued only when it is provably still the partition's newest state: nothing
/// has owned the partition since its writer (the row is still the writer's at the save's generation, or unowned and
/// not recovering at exactly the next one - every way a row loses its owner bumps the generation once, and any later
/// owner would have left it owned, recovering, or another bump on), no catalogued save is newer, and the generation's
/// catalogued saves are the writer's own. Everything else is refused BY NAME: the save file stays on disk, the
/// partition recovers from the catalogue as before, and the registry log says which save was passed over and why.
///
/// The hold (<see cref="HeldForLateSave"/>). A recovery claim that lands before the report would hand out the older
/// save, and the report would then find the partition owned and be refused. So a partition that still had an owner
/// when the registry loaded its state is not handed out until a node of that owner's host has reported (an empty
/// report counts: every node sends one at registration), or <see cref="LateSaveHold"/> has passed since the load.
/// An owner that heartbeats again (a registry-only restart) ends the hold for its partitions at once. Memory only: a
/// registry restart inside the hold arms it again from the rows it loads.
/// </summary>
internal sealed partial class GatewayRegistry
{
    /// <summary>How long recovery of a partition owned at load waits for its host's report. CLUSTER_LATE_SAVE_HOLD_SECONDS; 0 = no hold.</summary>
    internal static TimeSpan LateSaveHold = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_LATE_SAVE_HOLD_SECONDS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 ? seconds : 300);

    /// <summary>Partition -> the row of the node that owned it when the registry loaded its state (its host is asked when
    /// it is needed: the gateway host's name is configured after the constructor).</summary>
    private readonly Dictionary<ulong, NodeRegistration> _lateSaveHold = [];
    private DateTimeOffset _lateSaveHoldUntil;

    /// <summary>Constructor only, after the state is loaded and before any lease can expire.</summary>
    private void ArmLateSaveHold()
    {
        if (LateSaveHold <= TimeSpan.Zero) return;
        foreach (PartitionOwnership row in _state.Partitions.Values)
            if (row.Node != null && _state.Nodes.TryGetValue(row.Node, out NodeRegistration? owner) && owner.Role == NodeRole.Regular)
                _lateSaveHold[row.Partition] = owner;
        if (_lateSaveHold.Count == 0) return;
        _lateSaveHoldUntil = _clock() + LateSaveHold;
        Console.WriteLine($"late-save hold: {_lateSaveHold.Count} partition(s) had an owner when the registry state was loaded; their recovery waits "
            + $"for the unregistered-final report of the owner's host, {LateSaveHold.TotalSeconds:F0} s at most (T-0347)");
    }

    /// <summary>Whether recovery of the partition still waits for its former owner's host to report. Caller holds _lock.</summary>
    private bool HeldForLateSave(ulong partition, DateTimeOffset now)
    {
        if (_lateSaveHold.Count == 0) return false;
        if (now >= _lateSaveHoldUntil)
        {
            Console.WriteLine($"late-save hold: ended at its deadline with {_lateSaveHold.Count} partition(s) of host(s) "
                + $"{string.Join(", ", _lateSaveHold.Values.Select(HostKey).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))} never reported; "
                + "they recover from the catalogue, and a Final reported later is catalogued only while nothing has recovered its partition");
            _lateSaveHold.Clear();
            return false;
        }
        return _lateSaveHold.ContainsKey(partition);
    }

    private void ReleaseLateSaveHold(string host, string reporter)
    {
        ulong[] released = [.. _lateSaveHold.Where(x => string.Equals(HostKey(x.Value), host, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key)];
        if (released.Length == 0) return;
        foreach (ulong partition in released) _lateSaveHold.Remove(partition);
        Console.WriteLine($"late-save hold: {reporter} reported for host {host}; {released.Length} partition(s) released for recovery, {_lateSaveHold.Count} still held");
    }

    /// <summary>
    /// The owner at load is still running (a registry-only restart: its own incarnation heartbeats again): it made no
    /// local stop, there is no journal to wait for, and what it releases from now on - a drain, a rotation - is
    /// recoverable at once. Caller holds _lock.
    /// </summary>
    private void OwnerAliveForLateSaveHold(NodeRegistration owner)
    {
        if (_lateSaveHold.Count == 0) return;
        ulong[] released = [.. _lateSaveHold.Where(x => ReferenceEquals(x.Value, owner)).Select(x => x.Key)];
        if (released.Length == 0) return;
        foreach (ulong partition in released) _lateSaveHold.Remove(partition);
        Console.WriteLine($"late-save hold: {owner.Node} epoch {owner.Epoch} is alive; its {released.Length} partition(s) are not held, {_lateSaveHold.Count} still held");
    }

    internal int LateSaveHeldCount { get { lock (_lock) return _lateSaveHold.Count; } }

    public LateSaveVerdict[] RegisterLateSaves(string node, long epoch, LateSaveReport[] reports)
    {
        lock (_lock)
        {
            NodeRegistration reporter = RequireNode(node, epoch);
            if (reporter.State is NodeState.Dead or NodeState.Empty)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} cannot report saves while {reporter.State}");
            var verdicts = new List<LateSaveVerdict>();
            bool catalogued = false;
            foreach (LateSaveReport report in reports)
            {
                var id = new SaveId(report.Partition, report.Generation, report.Sequence);
                string? refusal = RegisterLateSave(reporter, id, report, out bool added);
                catalogued |= added;
                verdicts.Add(new LateSaveVerdict(report.Partition, report.Generation, report.Sequence, refusal == null, refusal ?? (added ? "catalogued" : "already catalogued")));
                if (refusal != null)
                    Console.WriteLine($"late save {id.Partition}:{id.Generation}:{id.Sequence} of {report.Writer} epoch {report.WriterEpoch} REFUSED (reported by {node}): {refusal}. "
                        + $"The file stays at {report.Location}; the partition recovers from its catalogued saves (T-0347)");
            }
            // After the saves, so no claim can fall between the release and the registration.
            ReleaseLateSaveHold(HostKey(reporter), node);
            if (catalogued)
            {
                UpdateCollapsed();
                Persist();
            }
            return [.. verdicts];
        }
    }

    /// <summary>Null when the save is (or already was) catalogued, else why it is not. Caller holds _lock and persists.</summary>
    private string? RegisterLateSave(NodeRegistration reporter, SaveId id, LateSaveReport report, out bool added)
    {
        added = false;
        if (id.Sequence < 1 || id.Generation < 1 || report.SizeBytes < 0 || string.IsNullOrWhiteSpace(report.Checksum)
            || string.IsNullOrWhiteSpace(report.Location) || string.IsNullOrWhiteSpace(report.Writer))
            return "the report is incomplete";
        Dictionary<long, long>? homes;
        try { homes = DirectGateway.ParseHomeStamps(report.Homes); }
        catch (ArgumentException error) { return error.Message; }
        if (TryGetSave(Key(id), out SaveRegistration? old))
            return old.Kind == SaveKind.Final && old.SizeBytes == report.SizeBytes && old.Checksum == report.Checksum && old.ClusterTime == report.ClusterTime
                ? null : "a different save is catalogued under this id";
        if (!_state.Nodes.TryGetValue(report.Writer, out NodeRegistration? writer) || writer.Epoch < report.WriterEpoch)
            return "the registry has no node row for its writer";
        if (!_state.Partitions.TryGetValue(id.Partition, out PartitionOwnership? row))
            return "the partition has no row (reaped, or never registered)";
        if (IsRetired(id.Partition)) return "the partition is retired (merged or split since)";
        if (_state.OfflinePartitions.ContainsKey(id.Partition)) return "the partition is offline; its offline Final is its state";
        bool stillWriters = row.Node == report.Writer && row.Epoch == report.WriterEpoch && row.Generation == id.Generation;
        bool unownedSince = row.Node == null && !row.Recovering && row.Generation == id.Generation + 1;
        if (!stillWriters && !unownedSince)
            return $"the partition moved on: generation {row.Generation}, " + (row.Node == null ? "unowned" : $"owner {row.Node} epoch {row.Epoch}")
                + (row.Recovering ? ", recovering" : "") + " - a stale save must not replace what was recovered since";
        IReadOnlyList<SaveRegistration> saves = SavesOf(id.Partition);
        if (saves.FirstOrDefault(x => x.SaveId.CompareTo(id) > 0) is { } newer)
            return $"a newer save is catalogued ({newer.Id})";
        if (saves.FirstOrDefault(x => x.SaveId.Generation == id.Generation && (x.Node != report.Writer || x.Epoch != report.WriterEpoch)) is { } foreign)
            return $"generation {id.Generation} was written by {foreign.Node} epoch {foreign.Epoch} ({foreign.Id}), not by the reported writer";

        SaveRegistration? newest = NewestCatalogued(id.Partition);
        var save = new SaveRegistration(id, report.Writer, report.WriterEpoch, SaveKind.Final, report.SizeBytes, report.Checksum, report.ClusterTime, [report.Location], _clock())
            { HomeStamps = homes, Entities = report.Entities };
        save.LocationHolders[report.Location] = report.Writer;
        // The writer's id registered again (no node control): it serves its own file. A writer that is gone for good is
        // served by a proxy on its host, like any other save a Dead node registered (T-0348).
        if (report.Writer == reporter.Node && !string.IsNullOrWhiteSpace(reporter.ControlEndpoint))
            save.HolderEndpoints[report.Writer] = reporter.ControlEndpoint;
        PutSave(Key(id), save);
        added = true;
        Console.WriteLine($"late save {id.Partition}:{id.Generation}:{id.Sequence} of {report.Writer} epoch {report.WriterEpoch} catalogued (reported by {reporter.Node}): a local-stop Final, "
            + $"newer than the catalogue's {(newest == null ? "nothing" : $"{newest.Id} ({newest.Kind})")} (T-0347)");
        return null;
    }
}
