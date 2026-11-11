using System.Globalization;

namespace ClusterRegistry;

/// <summary>A live node on a gone holder's host that serves that holder's catalogued save files (T-0323).</summary>
internal sealed record HolderProxy(string Node, string Endpoint);

/// <summary>
/// T-0323: save holders that stopped for good, and the registry rows they leave behind.
///
/// Under node control (T-0320) every start, recycle and remove gives a node a fresh id that never comes back, so a
/// stopped node's <see cref="NodeRegistration"/> row stays Empty (or Dead) forever while its data dir still holds
/// catalogued saves. Such a node is a GONE holder: a regular row, Empty or Dead, not in the node-control plan while
/// node control is active (only planned ids may register then, so it can never register again).
///
/// Reachability. <see cref="HolderUsable"/> keeps treating an Empty holder as usable, and that is only true while
/// something can serve its files: its own process is gone, its control endpoint is dead or (slots reuse ports) belongs
/// to another node that rejects the grant. A gone holder's saves are therefore served by a PROXY, a live regular node
/// on the gone holder's host (<see cref="HostKey"/>; a pre-T-0320 row without a host is on the gateway host), which
/// reads the exact catalogued Location that a proxied grant binds (<see cref="IssueGrant"/>). Recovery claims carry
/// the proxy per holder (<see cref="SaveRegistration.HolderProxies"/>), replica directives name it as the source.
///
/// Dead holders (T-0348). A crashed or killed node's REGISTERED saves are served the same way: a save is registered
/// after its file is written and renamed, the file survives the process, and every fetch is checksum-verified by the
/// requester (a torn file is reported corrupt and the next holder tried). Until then a save whose only holders were
/// Dead had no copy source at all: after the hard stop on r116, 34k saves sat in the replica queue for good. Unlike
/// an Empty holder, a Dead one is usable only WHILE a proxy exists (<see cref="DeadHolderServed"/>): Dead is also
/// what a host cut leaves, and calling those rows usable would make recovery wait on a newest save nothing can read
/// and rank a save with one live copy as a mere evacuation. A Dead row that node control still lists (Failed, until
/// its replacement serves) is not gone; a lapsed lease alone (Active/Closed) is not gone either.
///
/// Release. Nothing ever removed these rows. <see cref="ReleaseGoneRows"/> (after each retention pass, off the lock) drops a gone
/// holder's Location from a save being deleted (the node can never acknowledge its delete; the file goes with the
/// data dir at <c>cluster nodes prune</c>) and from a save that live holders already cover, then removes the row of
/// a gone node that nothing names any more: no in-memory save, global save, voxel base or journal, owned partition,
/// open handover or ARCHIVED save. The node history (the audit record) is not touched. The replica queue moves saves
/// off gone holders, at the lowest rank and rate-limited (<see cref="EvacuateBytesPerSecond"/>).
///
/// T-0340 step 6: in host-store mode the release does not consult the archive, and a holder whose row was released is
/// still served through its host (<see cref="ReleasedHolderHost"/>, HostStore.cs). The archive scan and its cache
/// stay for the catalogue that is not in store mode (the switch is off by default). #1210: a managed deployment releases
/// the same way (<see cref="ReleaseByHost"/>); a slot a new generation dropped is gone, and its own data dir under
/// runtime/nodes is read by a live node of its host (ClusterProtocol.ProxiedSavePath).
/// </summary>
internal sealed partial class GatewayRegistry
{
    /// <summary>Gone node ids an archived save still names (cached: a scan of the archive per id is ~0.3 s on w33).
    /// Cleared when a partition comes back from the archive, the only way an archived reference can go away.</summary>
    private readonly HashSet<string> _archiveReferenced = new(StringComparer.Ordinal);
    /// <summary>Self-test switch that restores the df fc5d693 cache handling, to earn the red of gone-holder-archive-cache:
    /// any rehydrate clears the whole cache, and a pass deferred by a rehydrate still caches what its scan found.</summary>
    internal static bool RehydrateClearsArchiveCache;

    /// <summary>A partition came back from the archive (caller holds _lock): the ids its rows named (<paramref name="named"/>,
    /// <see cref="NodeIdsOf"/>) may no longer be named by the archive, so they leave the cache. Every other cached id is
    /// still named by a row that stays archived: archived rows go away only by a rehydrate of their own partition.</summary>
    private void UncacheArchiveReferences(HashSet<string> named)
    {
        if (RehydrateClearsArchiveCache) _archiveReferenced.Clear();
        else _archiveReferenced.ExceptWith(named);
    }

    /// <summary>Every node id a save row carries, i.e. every string field the archive scan's quoted-id match can hit: the
    /// writer, the holders, the endpoint and corruption keys (the other strings are paths, a checksum, a legacy id).</summary>
    private static void NodeIdsOf(SaveRegistration save, HashSet<string> ids)
    {
        ids.Add(save.Node);
        ids.UnionWith(save.LocationHolders.Values);
        ids.UnionWith(save.HolderEndpoints.Keys);
        ids.UnionWith(save.CorruptHolders);
    }

    internal int ReleasedRows { get; private set; }
    internal int DroppedGoneLocations { get; private set; }
    /// <summary>Settable for the self-test only: its fixtures name their hosts, and must not depend on the machine they run on.</summary>
    internal static string MachineHost = Environment.MachineName.Split('.')[0];
    /// <summary>Self-test switches that restore the pre-T-0323 behaviour, to earn each red: no proxy for a gone holder,
    /// no release pass, a release that does not look at the archive.</summary>
    internal static bool ProxyGoneHolders = true, ReleaseGoneRowsEnabled = true, ReleaseChecksArchive = true;
    /// <summary>Self-test switch that restores the pre-T-0348 rule (only an Empty gone holder's files are servable), to earn the red.</summary>
    internal static bool ServeDeadGoneHolders = true;
    /// <summary>Self-test switch that restores the pre-#1210 rule (no row is gone in managed mode), to earn the red.</summary>
    internal static bool ManagedRemovedSlotsGone = true;
    /// <summary>Self-test switch: the managed release pass asks the archive again (the first #1210 change), to earn the red.</summary>
    internal static bool ManagedReleaseChecksArchive;

    /// <summary>A regular node that stopped for good: Empty or Dead, and not in the plan while node control is active.
    /// #1210: in managed mode, not in the current inventory (a later generation dropped its slot, e.g. a hot spare
    /// count lowered): the Host never starts that node id again, so its row would otherwise stay Empty forever.</summary>
    private bool IsGone(string node, NodeRegistration row) =>
        row.Role == NodeRole.Regular && row.State is NodeState.Empty or NodeState.Dead
        && (NodeControlActive ? !Control.Live.ContainsKey(node)
            : ManagedRemovedSlotsGone && _state.ManagedDeployment is { } managed && !managed.Slots.Any(slot => slot.Node == node))
        // T-0340: with the host store in force a host's row is a store, judged by its state, never a stopped node.
        && !(_hostStore && IsHostHolder(node));

    /// <summary>The row of a gone holder whose catalogued files a proxy may serve, else null. Empty: it drained and exited
    /// cleanly. Dead (T-0348): what it registered is on disk all the same, and the requester's checksum catches a torn file.</summary>
    private NodeRegistration? GoneHolder(string holder) =>
        _state.Nodes.TryGetValue(holder, out NodeRegistration? row) && (row.State == NodeState.Empty || ServeDeadGoneHolders) && IsGone(holder, row) ? row : null;

    /// <summary>T-0348: a Dead holder counts as usable (<see cref="HolderUsable"/>) exactly while it is gone and a live node on
    /// its host can serve its files. No proxy (a host cut, or no node on that host yet): unusable, as every Dead holder was.</summary>
    private bool DeadHolderServed(NodeRegistration row) =>
        ServeDeadGoneHolders && ProxyGoneHolders && row.State == NodeState.Dead && IsGone(row.Node, row) && ProxyFor(row) != null;

    /// <summary>The host a node's files live on. A row from before T-0320 has none: every such save was written on the gateway host.</summary>
    private string HostKey(NodeRegistration row) => row.Host ?? GatewayHost ?? MachineHost;

    /// <summary>A live regular node on <paramref name="gone"/>'s host that can serve its files: Active before Closed, then by id.</summary>
    private NodeRegistration? ProxyFor(NodeRegistration gone)
    {
        string host = HostKey(gone);
        DateTimeOffset now = _clock();
        NodeRegistration? best = null;
        foreach (NodeRegistration row in _state.Nodes.Values)
        {
            if (row.Role != NodeRole.Regular || row.State is not (NodeState.Active or NodeState.Closed) || row.LeaseExpires <= now
                || string.IsNullOrWhiteSpace(row.ControlEndpoint) || !string.Equals(HostKey(row), host, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best == null) best = row;
            else if ((row.State == NodeState.Active) != (best.State == NodeState.Active)) { if (row.State == NodeState.Active) best = row; }
            else if (string.CompareOrdinal(row.Node, best.Node) < 0) best = row;
        }
        return best;
    }

    // T-0340: a host holder's files are served by a node of that host, named to the current node plugin as a proxy.
    private HolderProxy? ProxyOf(string holder) =>
        _hostStore && IsHostHolder(holder) ? HostServers(HostOfHolder(holder)!).FirstOrDefault() :
        ReleasedHolderHost(holder) is { } released ? HostServers(released).FirstOrDefault() :
        ProxyGoneHolders && GoneHolder(holder) is { } gone && ProxyFor(gone) is { } proxy ? new HolderProxy(proxy.Node, proxy.ControlEndpoint!) : null;

    /// <summary>A snapshot for a node that will fetch the save: <see cref="Snapshot(SaveRegistration)"/> plus the proxy of every gone holder.</summary>
    private SaveRegistration Served(SaveRegistration save)
    {
        SaveRegistration copy = Snapshot(save);
        foreach (string holder in save.LocationHolders.Values.Distinct(StringComparer.Ordinal))
            if (ProxyOf(holder) is { } proxy)
                (copy.HolderProxies ??= new Dictionary<string, HolderProxy>(StringComparer.Ordinal))[holder] = proxy;
        return copy;
    }

    /// <summary>Where <paramref name="node"/> can copy <paramref name="save"/> from: live holders by their own endpoint, gone holders through a proxy.</summary>
    private HolderSource[] ReplicaSources(SaveRegistration save)
    {
        var live = new List<HolderSource>();
        var proxied = new List<HolderSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string location, string holder) in save.LocationHolders)
        {
            if (!seen.Add(holder) || save.CorruptHolders.Contains(holder) || !HolderUsable(holder)) continue;
            if (ProxyOf(holder) is { } proxy)
                proxied.Add(new HolderSource(holder, proxy.Endpoint, proxy.Node, location));
            else if (GoneHolder(holder) == null && save.HolderEndpoints.TryGetValue(holder, out string? endpoint))
                live.Add(new HolderSource(holder, endpoint));
        }
        return [.. live, .. proxied];
    }

    /// <summary>Live copies of the save: distinct usable non-gone holders, on two hosts while more than one host serves.</summary>
    private bool CoveredByLiveHolders(SaveRegistration save, bool multiHost)
    {
        int count = 0;
        string? firstHost = null;
        bool spread = !multiHost;
        foreach (string holder in save.LocationHolders.Values.Distinct(StringComparer.Ordinal))
        {
            // T-0340 step 6: a released holder's copy is a stopped node's too, row or no row - never a live copy.
            if (save.CorruptHolders.Contains(holder) || !HolderUsable(holder) || GoneHolder(holder) != null || ReleasedHolderHost(holder) != null) continue;
            string host = _state.Nodes.TryGetValue(holder, out NodeRegistration? row) ? HostKey(row) : "#" + holder;
            if (++count == 1) firstHost = host;
            else if (!string.Equals(host, firstHost, StringComparison.OrdinalIgnoreCase)) spread = true;
            if (count >= 2 && spread) return true;
        }
        return false;
    }

    /// <summary>Drops every Location of <paramref name="save"/> held by a node in <paramref name="gone"/>. Caller holds _lock.</summary>
    private int DropGoneLocations(SaveRegistration save, HashSet<string> gone)
    {
        string[] locations = save.Locations.Where(location => gone.Contains(save.LocationHolders.GetValueOrDefault(location, save.Node))).ToArray();
        foreach (string location in locations)
        {
            string holder = save.LocationHolders.GetValueOrDefault(location, save.Node);
            save.Locations.Remove(location);
            save.LocationHolders.Remove(location);
            if (!save.LocationHolders.ContainsValue(holder))
            {
                save.HolderEndpoints.Remove(holder);
                save.CorruptHolders.Remove(holder);
            }
        }
        if (locations.Length != 0) Touch(save);
        return locations.Length;
    }

    /// <summary>Self-test seams: a pause (and a callback) inside the archive scan, and the pre-fix shape that scans under the lock.</summary>
    internal static TimeSpan ArchiveScanTestDelay = TimeSpan.Zero;
    internal static Action? ArchiveScanTestHook;
    internal static bool ReleaseScanUnderLock;
    /// <summary>The last release pass: its longest single registry-lock hold, and how long its archive scan took.</summary>
    internal double LastReleaseLockMs { get; private set; }
    internal double LastReleaseScanMs { get; private set; }
    /// <summary>How many ids the last release pass scanned the archive for (each up to ~0.3 s on w33).</summary>
    internal int LastReleaseScannedIds { get; private set; }
    /// <summary>Self-test: the gone ids cached as named by the archive.</summary>
    internal string[] ArchiveReferencedIds() { lock (_lock) return [.. _archiveReferenced.Order(StringComparer.Ordinal)]; }

    /// <summary>
    /// Drops what no longer needs a gone holder and removes the rows of gone nodes that nothing names (see the class
    /// summary). Runs after each retention pass, called WITHOUT the registry lock: nodes report character homes
    /// synchronously from the game thread, so the archive scan (~0.3 s per id on w33's 260k archived saves) must not
    /// hold the lock. Three steps: under the lock, drop locations and pick the candidates (one catalogue walk, one hold);
    /// without it, scan the archive through a read-only connection; under the lock again, release each candidate that
    /// is still gone and still unnamed, unless a partition came back from the archive meanwhile (then next pass).
    ///
    /// T-0340 step 6: in store mode the archive is not asked (<see cref="ReleaseAsksArchive"/>). On r113 every one of the
    /// 52 stopped rows was kept for an archived save, and nothing could ever un-name them: archived saves are not
    /// replicated, not deleted and not rewritten. A released row leaves its host in the alias map, and a save that
    /// still names the id - an archived one coming back, whose file was not moved into the store - is served through
    /// that host (<see cref="ReleasedHolderHost"/>); one whose file is in the store is re-keyed to the host when it comes
    /// back (<see cref="Rehydrate"/>). What is in memory pins a row as before (<see cref="NamedInMemory"/>): a location a
    /// stopped node still holds, a voxel base or journal, an owned partition, an open handover. A finished handover
    /// record and a save's writer field name a node without needing its row, and never pinned one.
    /// </summary>
    internal string[] ReleaseGoneRows()
    {
        if (!ReleaseGoneRowsEnabled) return [];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double longest = 0;
        string[] candidates;
        HashSet<string> archived;
        long rehydrations;
        int dropped, goneCount;
        bool stale, archive;
        lock (_lock)
        {
            clock.Restart();
            archive = ReleaseAsksArchive;
            (candidates, archived, rehydrations, dropped, goneCount) = PrepareGoneRowRelease();
            // T-0296: rows a rehydrate took out of the archive stay committed until the next write; a scan that finds an id
            // only there must not cache it (its rows will not come back to uncache it).
            stale = _store.ArchiveRemovalsQueued;
            longest = clock.Elapsed.TotalMilliseconds;
        }
        string[] scan = candidates.Where(node => !archived.Contains(node)).ToArray();
        LastReleaseScannedIds = archive ? scan.Length : 0;
        if (scan.Length != 0 && archive)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (ReleaseScanUnderLock)
                lock (_lock)
                {
                    archived.UnionWith(ScanArchive(scan));
                    longest = Math.Max(longest, timer.Elapsed.TotalMilliseconds);
                }
            else archived.UnionWith(ScanArchive(scan));
            LastReleaseScanMs = timer.Elapsed.TotalMilliseconds;
        }
        string[] released;
        lock (_lock)
        {
            clock.Restart();
            released = FinishGoneRowRelease(candidates, archived, rehydrations, dropped, goneCount, cacheable: !stale);
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
        }
        LastReleaseLockMs = longest;
        return released;
    }

    /// <summary>Whether the release pass asks the archive who names a gone node: not in store mode (step 6), where a
    /// released holder's files stay served through its host. Caller holds _lock.</summary>
    private bool ReleaseAsksArchive => ReleaseChecksArchive && !ReleaseByHost;

    /// <summary>Step 6's release: no archive scan, the released row's host frozen in the alias map, its files served
    /// through that host (<see cref="ReleasedHolderHost"/>). In store mode, and in managed mode (#1210): there no
    /// location a node id holds is ever re-keyed or deleted, so an archived save would keep a removed slot's row forever
    /// (the live 2 -> 0 spare check: two archived copies on the removed slot kept it).</summary>
    private bool ReleaseByHost => _hostStore ? !HostStoreReleaseChecksArchive
        : _state.ManagedDeployment is not null && !ManagedReleaseChecksArchive;

    private HashSet<string> ScanArchive(string[] ids)
    {
        HashSet<string> found = _store.ArchiveMentionsReadOnly(ids);
        if (ArchiveScanTestDelay > TimeSpan.Zero) Thread.Sleep(ArchiveScanTestDelay);
        ArchiveScanTestHook?.Invoke();
        return found;
    }

    /// <summary>The ids that a gone holder's in-memory references could be (see <see cref="ReleaseGoneRows"/>). Caller holds _lock.</summary>
    private HashSet<string> NamedInMemory()
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (SaveRegistration save in _state.Saves.Values)
            foreach (string location in save.Locations) named.Add(save.LocationHolders.GetValueOrDefault(location, save.Node));
        foreach (GlobalSaveRegistration global in _state.GlobalSaves.Values)
            foreach (string location in global.Locations) named.Add(global.LocationHolders.GetValueOrDefault(location, global.Node));
        foreach (VoxelBaseRegistration voxel in _state.VoxelBases.Values) named.Add(voxel.Node);
        foreach (VoxelJournalRegistration journal in _state.VoxelJournals.Values) named.Add(journal.Node);
        foreach (PartitionOwnership owner in _state.Partitions.Values) if (owner.Node != null) named.Add(owner.Node);
        foreach (HandoverRecord handover in _state.Handovers.Values)
            if (handover.State is HandoverState.Begun or HandoverState.Active) { named.Add(handover.SourceNode); named.Add(handover.TargetNode); }
        foreach (OfflinePartition row in _state.OfflinePartitions.Values) if (row.LoadRequestedNode != null) named.Add(row.LoadRequestedNode);
        return named;
    }

    /// <summary>Step 1 (caller holds _lock): drop the locations nothing needs; the gone ids no in-memory state names, and
    /// those of them that an archive write still queued names (the scan reads only what is committed).</summary>
    private (string[] Candidates, HashSet<string> Archived, long Rehydrations, int Dropped, int Gone) PrepareGoneRowRelease()
    {
        var gone = new HashSet<string>(_state.Nodes.Where(x => IsGone(x.Key, x.Value)).Select(x => x.Key), StringComparer.Ordinal);
        if (gone.Count == 0) return ([], [], RehydratedPartitions, 0, 0);
        bool multiHost = MultiHost(CurrentHolderStates());
        int dropped = 0;
        foreach ((string key, SaveRegistration save) in _state.Saves.ToArray())
        {
            if (!save.LocationHolders.Values.Any(gone.Contains)) continue;
            // A save being deleted is never offered to recovery; a covered save keeps its live copies.
            if (!save.PendingDelete && !CoveredByLiveHolders(save, multiHost)) continue;
            dropped += DropGoneLocations(save, gone);
            if (save.PendingDelete && save.Locations.Count == 0) RemoveSave(key);
        }
        foreach (GlobalSaveRegistration global in _state.GlobalSaves.Values.ToArray())
        {
            if (!global.LocationHolders.Values.Any(gone.Contains)) continue;
            // (A released holder serves no global save - there is no proxied global fetch - so it covers none.)
            if (!global.PendingDelete && !global.LocationHolders.Values.Any(holder => !gone.Contains(holder) && ReleasedHolderHost(holder) == null && HolderUsable(holder))) continue;
            string[] locations = global.Locations.Where(location => gone.Contains(global.LocationHolders.GetValueOrDefault(location, global.Node))).ToArray();
            foreach (string location in locations)
            {
                string holder = global.LocationHolders.GetValueOrDefault(location, global.Node);
                global.Locations.Remove(location);
                global.LocationHolders.Remove(location);
                if (!global.LocationHolders.ContainsValue(holder)) global.HolderEndpoints.Remove(holder);
            }
            if (locations.Length != 0) { Touch(global); dropped += locations.Length; }
            // SE-1346: a global retention released whose last copy was on a gone holder is done.
            if (global.PendingDelete && global.Locations.Count == 0) RemoveGlobal(global.SaveId);
        }
        DroppedGoneLocations += dropped;
        if (dropped != 0) Persist();
        HashSet<string> named = NamedInMemory();
        // Never below the id counter: a released id must stay unallocatable (AllocateNodeId floors on the rows too).
        // Managed ids are the inventory's, never allocated: a later inventory that names one again starts a fresh row.
        bool archive = ReleaseAsksArchive, managed = _state.ManagedDeployment is not null;
        string[] candidates = gone.Where(node => !named.Contains(node) && !(archive && _archiveReferenced.Contains(node))
            && (managed || NodeNumber(node) == 0 || NodeNumber(node) < Control.NextNodeNumber)).Order(StringComparer.Ordinal).ToArray();
        HashSet<string> queued = candidates.Length == 0 || !archive ? [] : _store.QueuedArchiveMentions(candidates);
        return (candidates, queued, RehydratedPartitions, dropped, gone.Count);
    }

    /// <summary>Step 3 (caller holds _lock): release the candidates the archive does not name and nothing named since.
    /// Anything archived after step 1 came from memory, where no candidate was named; only a partition brought BACK
    /// from the archive in between can carry a reference the scan did not see, so then everything waits a pass.</summary>
    private string[] FinishGoneRowRelease(string[] candidates, HashSet<string> archived, long rehydrations, int dropped, int goneCount, bool cacheable)
    {
        string[] released = [];
        // Store mode (step 6): no scan, so nothing a rehydrate could have hidden from it - what came back is in memory
        // and NamedInMemory below sees it.
        bool storeRelease = ReleaseByHost;
        bool deferred = !storeRelease && candidates.Length != 0 && RehydratedPartitions != rehydrations;
        // T-0296: a pass that a rehydrate overlapped may have found a row that is in memory now; caching that id would keep
        // its row forever (nothing uncaches an id whose rows have left the archive). The next pass scans it again.
        if (!deferred && cacheable || RehydrateClearsArchiveCache) _archiveReferenced.UnionWith(archived);
        if (candidates.Length != 0 && !deferred)
        {
            HashSet<string> named = NamedInMemory();
            released = candidates.Where(node => !archived.Contains(node) && !named.Contains(node)
                && _state.Nodes.TryGetValue(node, out NodeRegistration? row) && IsGone(node, row)
                // Step 6: the row goes only once its host is in the alias map - what an archived save's location is served by.
                && (!storeRelease || FreezeReleasedHost(node))).ToArray();
        }
        foreach (string node in released)
        {
            NodeRegistration row = _state.Nodes[node];
            _state.Nodes.Remove(node);
            foreach (string key in _state.VoxelReplicas.Where(x => x.Value.Node == node).Select(x => x.Key).ToArray())
                _state.VoxelReplicas.Remove(key);
            ReleasedRows++;
            Emit("node.row-released", node, new Dictionary<string, string>
            {
                ["state"] = row.State.ToString(), ["host"] = HostKey(row), ["epoch"] = row.Epoch.ToString(CultureInfo.InvariantCulture),
            });
        }
        if (dropped != 0 || released.Length != 0 || deferred)
        {
            Console.WriteLine($"registry: gone holders: dropped {dropped} location(s) no recovery needs, released {released.Length} row(s)"
                + (released.Length == 0 ? "" : $" ({string.Join(",", released)})") + $"; {goneCount - released.Length} gone row(s) kept"
                + (archived.Count == 0 ? "" : $", {archived.Count} named by the archive")
                + (deferred ? "; a partition came back from the archive during the scan, release deferred to the next pass" : ""));
        }
        if (released.Length != 0) Persist();
        return released;
    }

    /// <summary>Self-test and diag: the gone rows the registry still keeps, and why (the first thing that names each).</summary>
    internal string[] GoneRowIds()
    {
        lock (_lock) return [.. _state.Nodes.Where(x => IsGone(x.Key, x.Value)).Select(x => x.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary>Self-test: the naive fix - delete a gone node's row whatever still names it.</summary>
    internal void RemoveRowNaively(string node)
    {
        lock (_lock) { _state.Nodes.Remove(node); Persist(); }
    }

    /// <summary>Whether the registry can offer this save to recovery (Newest()'s holder test).</summary>
    internal bool SaveReachable(SaveId id)
    {
        lock (_lock) return TryGetSave(Key(id), out SaveRegistration? save) && Reachable(save);
    }

    /// <summary>Self-test: the save as a recovery claim hands it to a node (proxies included).</summary>
    internal SaveRegistration ServedForTest(SaveId id)
    {
        lock (_lock) return TryGetSave(Key(id), out SaveRegistration? save) ? Served(save) : throw new ArgumentException($"no save {id}");
    }

    /// <summary>The release pass on demand (self-test; production runs it after each retention pass).</summary>
    internal string[] ReleaseGoneRowsNow() => ReleaseGoneRows();
}
