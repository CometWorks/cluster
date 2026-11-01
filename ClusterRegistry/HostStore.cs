namespace ClusterRegistry;

/// <summary>
/// T-0340 step 1: the per-host save store in the catalogue. A save file belongs to the HOST it lies on, not to the
/// node that wrote it: its holder is <c>host:&lt;name&gt;</c> and its location is a pure function of (host, save id)
/// under <c>&lt;runRoot&gt;/saves/&lt;host&gt;/</c>, with the relative names the nodes already use
/// (PartitionPersistence.SavePath, WorldAuthorityPersistence). A stopped node then leaves nothing catalogued behind.
///
/// This step only re-keys the catalogue, at load, behind <see cref="HostStoreEnabled"/> (off by default: nothing here
/// runs and nothing is written differently, except the empty <see cref="RegistryState.HostStoreAliases"/> state row).
/// The nodes still write into their own data dirs and the files are moved by the host agent (steps 4 and 7), so the
/// switch stays off on a live cluster until those are in.
///
/// Rollback form. The store has no schema version, so a re-keyed database must stay readable by the package before
/// this ticket. Each host holder therefore has a synthetic node row: Regular, Empty, with its Host, never in the
/// node-control plan. That package reads it as a gone holder (GoneHolders.cs): usable, served through a proxy on that
/// host at the exact catalogued location, a <c>.mps</c> under the run root. Proven by the self-test section
/// <c>host-store-previous-package</c>, and only while node control is active - without it nothing is a gone holder
/// and no node on another host could fetch the file - so the re-key does not run otherwise. One limit: that package
/// proxies partition saves only, so a host-held global save has no replica source there (the World Authority still
/// reads it from its host's disk, and its next global save replicates as before). The World Authority always runs on
/// the gateway host, where its global saves' store is; a rollback with it anywhere else is out of scope.
///
/// Global saves have no retention: the registry never marks one PendingDelete (PlanRetention covers partition saves
/// only) and no delete directive names one, for node holders and host holders alike. A host's GLOBAL directory
/// therefore grows as a node's did; this ticket does not add a retention rule for them.
/// </summary>
/// <summary>
/// T-0340 step 2: what a host's save store can do for the catalogue.
/// <list type="bullet">
/// <item>Serving: a regular node of the host is Active or Closed with a live lease (a spare counts). Its copies count
/// and are offered as a source.</item>
/// <item>Held: every such node's lease has lapsed, and at least one of them is held: inside a network window, or by its
/// host reporting its process running (#1218, #1282). The copies count, are not offered, and a recovery that needs one
/// waits the hold out.</item>
/// <item>Dark: no live node, but the host is still in the node plan. The copies count; nothing is re-replicated.</item>
/// <item>Lost: the host left the plan. Its copies stop counting.</item>
/// </list>
/// </summary>
internal enum HostStoreState { Serving, Held, Dark, Lost }

internal sealed partial class GatewayRegistry
{
    /// <summary>CLUSTER_HOST_STORE=1: re-key the catalogue to host holders at load.</summary>
    internal static bool HostStoreEnabled = Environment.GetEnvironmentVariable("CLUSTER_HOST_STORE") == "1";

    private const string HostHolderPrefix = "host:";

    internal static string HostHolder(string host) => ClusterProtocol.HostSaveStore.Holder(host);

    /// <summary>The host of a host holder key, null for a node id.</summary>
    internal static string? HostOfHolder(string holder) =>
        holder.StartsWith(HostHolderPrefix, StringComparison.Ordinal) ? holder[HostHolderPrefix.Length..] : null;

    private static bool IsHostHolder(string holder) => holder.StartsWith(HostHolderPrefix, StringComparison.Ordinal);

    /// <summary>Where <paramref name="host"/>'s copy of a partition save lies: the node's own function
    /// (<see cref="ClusterProtocol.HostSaveStore"/>). The registry never takes this path from a request.</summary>
    internal static string HostStorePath(string runRoot, string host, SaveId id) =>
        ClusterProtocol.HostSaveStore.PartitionSave(HostStoreRoot(runRoot, host), id.Partition, id.Generation, id.Sequence);

    internal static string HostStorePath(string runRoot, string host, GlobalSaveId id) =>
        ClusterProtocol.HostSaveStore.GlobalSave(HostStoreRoot(runRoot, host), id.Generation, id.Sequence);

    // The host name becomes a directory name and comes from a node's own registration: it must be one path component.
    private static string HostStoreRoot(string runRoot, string host) => ClusterProtocol.HostSaveStore.Root(runRoot, host);

    /// <summary>The run root every host shares (Cli/cluster.py refuses hosts whose runRoot differs): the registry directory's parent.</summary>
    internal string RunRoot { get; }

    /// <summary>The load re-keyed the catalogue (or found it re-keyed): saves coming back from the archive are re-keyed too.</summary>
    private bool _hostStore;

    /// <summary>Self-test: the frozen legacy node id -> host map.</summary>
    internal Dictionary<string, string> HostStoreAliases() { lock (_lock) return new(_state.HostStoreAliases, StringComparer.Ordinal); }

    /// <summary>Self-test: the archived save rows as stored.</summary>
    internal Dictionary<string, byte[]> ArchivedRowBytes() { lock (_lock) return _store.ArchivedRowsFresh(); }

    /// <summary>
    /// The host a legacy (node id) holder's files lie on: the host node control launched it on (its live record, else
    /// the node history), else the Host its row reported (a node from before T-0320, the World Authority), else the
    /// gateway host. The plan's host goes first: it is where the launcher put the data dir, while the row's is what the
    /// process said about itself. Frozen at first use: the row is released and the history is bounded, but an archived
    /// save can name the id for as long as the world lives, and it must map to the same store path every time.
    /// </summary>
    private string HostOfLegacyHolder(string node)
    {
        if (_state.HostStoreAliases.TryGetValue(node, out string? known)) return known;
        string host = Control.Live.GetValueOrDefault(node)?.Host
            ?? _state.NodeHistory.LastOrDefault(x => x.Node == node)?.Host
            ?? _state.Nodes.GetValueOrDefault(node)?.Host
            ?? GatewayHost ?? MachineHost;
        host = HostsFileSpelling(host);
        HostStoreRoot(RunRoot, host);
        _state.HostStoreAliases[node] = host;
        return host;
    }

    /// <summary>One spelling per host, the hosts file's: the name is a path component and a holder key.</summary>
    private string HostsFileSpelling(string host) =>
        Control.Hosts.Keys.FirstOrDefault(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase)) ?? host;

    /// <summary>The synthetic row of a host holder (see the class summary). Its lease is set, and lapsed, so no load re-arms it.</summary>
    private void EnsureHostRow(string host)
    {
        string key = HostHolder(host);
        if (_state.Nodes.ContainsKey(key)) return;
        DateTimeOffset now = _clock();
        _state.Nodes[key] = new NodeRegistration(key, 0, string.Empty, now, NodeState.Empty, now) { Host = host };
    }

    /// <summary>A host holder whose row is missing gets it back (the previous package releases a host row nothing names;
    /// a save it did not see must not be left without one).</summary>
    private void EnsureHostRows(Dictionary<string, string> holders)
    {
        foreach (string holder in holders.Values)
            if (HostOfHolder(holder) is { } host) EnsureHostRow(host);
    }

    /// <summary>
    /// The only evidence that a save's file was moved into a host's store: the registry sees it there, with the
    /// catalogued size. That is the gateway host's store; another host's is on a disk the registry does not see (unless
    /// the run root is shared), so its locations stay legacy until the host agent's move brings the same per-file
    /// evidence (step 7). A marker per host would be weaker: it says nothing about a file a legacy node wrote later.
    /// </summary>
    private static bool StoreFileThere(string path, long size)
    {
        try { return new FileInfo(path) is { Exists: true } file && file.Length == size; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Re-keys one save's holders: a location a node holds becomes its host's store path (<paramref name="storePath"/>),
    /// held by the host - when the file is there (<see cref="StoreFileThere"/>). The re-key must never turn a readable
    /// catalogue into an unreadable one: a location whose store file is missing stays as it is, node-held at its old
    /// path with its endpoint, served the way it was (<paramref name="unmoved"/> collects those). Copies on one host
    /// merge into one location, which is corrupt only if every copy merged into it was (the file move keeps the copy
    /// whose checksum matches the catalogue). Idempotent; true when anything changed.
    /// </summary>
    private bool RekeyToHostStore(HashSet<string> locations, Dictionary<string, string> holders, Dictionary<string, string> endpoints,
        HashSet<string>? corrupt, string writer, long size, Func<string, string> storePath, List<string>? unmoved = null)
    {
        Dictionary<string, bool>? intact = null;
        HashSet<string>? rekeyed = null;
        foreach (string location in locations.ToArray())
        {
            string holder = holders.GetValueOrDefault(location, writer);
            if (HostOfHolder(holder) != null) continue;
            string host = HostOfLegacyHolder(holder);
            if (!StoreFileThere(storePath(host), size))
            {
                unmoved?.Add(location);
                continue;
            }
            intact ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            intact[host] = intact.GetValueOrDefault(host) || corrupt?.Contains(holder) != true;
            (rekeyed ??= new HashSet<string>(StringComparer.Ordinal)).Add(holder);
            locations.Remove(location);
            holders.Remove(location);
        }
        if (intact == null) return false;
        // A host holder has no endpoint of its own: whichever live node of the host serves its files. A node that
        // still holds an unmoved location keeps its endpoint and its corrupt mark.
        foreach (string node in rekeyed!.Where(node => !holders.ContainsValue(node)))
        {
            endpoints.Remove(node);
            corrupt?.Remove(node);
        }
        foreach ((string host, bool good) in intact)
        {
            string key = HostHolder(host);
            if (good || corrupt == null || holders.ContainsValue(key) && !corrupt.Contains(key)) corrupt?.Remove(key);
            else corrupt.Add(key);
            string path = storePath(host);
            locations.Add(path);
            holders[path] = key;
            EnsureHostRow(host);
        }
        return true;
    }

    private bool RekeyToHostStore(SaveRegistration save, List<string>? unmoved = null) => RekeyToHostStore(save.Locations, save.LocationHolders,
        save.HolderEndpoints, save.CorruptHolders, save.Node, save.SizeBytes, host => HostStorePath(RunRoot, host, save.SaveId), unmoved);

    /// <summary>
    /// The re-key pass: at load (constructor, after the store is attached) and again when a start applies the hosts
    /// file - at the first start of a run root node control only becomes active then, and store mode must be in force
    /// before any node registers. Every in-memory partition save and every global save, written in ONE transaction. Archived saves are not rewritten (260k rows on w33); they are re-keyed
    /// when their partition is rehydrated, through the alias map, which is why every id the registry still knows is
    /// frozen into it here. Voxel bases stay with their node (a base has no holder list yet).
    /// </summary>
    private void RekeyCatalogueToHostStore()
    {
        if (!HostStoreEnabled) return;
        if (!NodeControlActive)
        {
            Console.WriteLine("WARNING: host store: CLUSTER_HOST_STORE=1 but node control is not active yet; store mode is decided again when "
                + "a start applies the hosts file (without node control the previous package could not serve a host holder's files to another host)");
            return;
        }
        // The store directory of the gateway host is the operator's go-ahead for store mode; which locations are re-keyed
        // is decided file by file (RekeyToHostStore).
        string store = HostStoreRoot(RunRoot, HostsFileSpelling(GatewayHost ?? MachineHost));
        if (!Directory.Exists(store))
        {
            Console.WriteLine($"WARNING: host store: CLUSTER_HOST_STORE=1 but {store} does not exist; the catalogue is not re-keyed "
                + "(the save files have not been moved into the per-host store)");
            return;
        }
        if (!_hostStore) _replicaQueueReady = false;   // host holders are ranked by the host rules from here on
        _hostStore = true;
        var unmoved = new List<string>();
        int aliases = _state.HostStoreAliases.Count, rows = _state.Nodes.Count, saves = 0, globals = 0;
        foreach (string node in _state.Nodes.Keys.Concat(_state.NodeHistory.Select(x => x.Node)).Where(x => HostOfHolder(x) == null).ToArray())
            HostOfLegacyHolder(node);
        foreach (SaveRegistration save in _state.Saves.Values)
        {
            if (RekeyToHostStore(save, unmoved))
            {
                Touch(save);
                saves++;
            }
            EnsureHostRows(save.LocationHolders);
        }
        foreach (GlobalSaveRegistration global in _state.GlobalSaves.Values)
        {
            if (RekeyToHostStore(global.Locations, global.LocationHolders, global.HolderEndpoints, null, global.Node, global.SizeBytes,
                    host => HostStorePath(RunRoot, host, global.SaveId), unmoved))
            {
                Touch(global);
                globals++;
            }
            EnsureHostRows(global.LocationHolders);
        }
        if (unmoved.Count != 0)
            Console.WriteLine($"WARNING: host store: {unmoved.Count} location(s) are not in the store (no file of the catalogued size at the store path the "
                + $"registry sees); they stay node-held at their old paths: {string.Join(", ", unmoved.Take(5))}{(unmoved.Count > 5 ? ", ..." : "")}");
        if (saves == 0 && globals == 0 && aliases == _state.HostStoreAliases.Count && rows == _state.Nodes.Count) return;
        Persist();
        FlushState();
        Console.WriteLine($"registry: host store: re-keyed {saves} save(s) and {globals} global save(s) to host holders under "
            + $"{Path.Combine(RunRoot, "saves")}; {_state.Nodes.Count - rows} host row(s) added, "
            + $"{_state.HostStoreAliases.Count} legacy node id(s) mapped to their host ({_archivedPartitions.Count} archived partition(s) re-key when read)");
    }

    // ---- Step 2: host holders in the replica queue and in reachability. All of it behind _hostStore: with the switch
    // off (and in the package before this ticket) a host row is a gone holder and takes the T-0323 paths instead.

    /// <summary>The host node control runs <paramref name="node"/> on; null for a node it does not run (stopped, the World Authority).</summary>
    private string? PlanHostOf(string node) => Control.Live.GetValueOrDefault(node)?.Host;

    private HostStoreState StoreState(string host)
    {
        DateTimeOffset now = _clock();
        bool held = false;
        foreach (NodeRecord record in Control.Live.Values)
        {
            if (!string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase) || !_state.Nodes.TryGetValue(record.Node, out NodeRegistration? row)
                || row.Role != NodeRole.Regular || row.State is not (NodeState.Active or NodeState.Closed)) continue;
            if (row.LeaseExpires > now) return HostStoreState.Serving;
            held |= LeaseHold?.Holds(row, now) == true || LiveProcessHeld(row, now);   // #1282: the live-process hold too
        }
        return held ? HostStoreState.Held : Control.Hosts.Keys.Contains(host, StringComparer.OrdinalIgnoreCase) ? HostStoreState.Dark : HostStoreState.Lost;
    }

    /// <summary>Self-test and diag: a host store's state.</summary>
    internal HostStoreState HostStoreStateOf(string host) { lock (_lock) return StoreState(host); }

    /// <summary>How many hosts must hold a save: two while two hosts are planned to serve, else one. From the PLAN, not
    /// from the hosts that happen to have an Active node now: a stopped node or a dark host changes nothing.</summary>
    private int HostCopyTarget() => Math.Min(2, Control.Hosts.Values.Count(x => x.Serving > 0));

    /// <summary>The copy target the replica queue was ranked with (a change rebuilds it).</summary>
    private int _replicaCopyTarget;

    /// <summary>A save with a host holder is judged by hosts alone; a node-held location it still has (registered by a
    /// node that does not write into the store yet) is re-keyed by the next load.</summary>
    private static bool HeldByHost(SaveRegistration save) => HeldByHost(save.LocationHolders);

    private static bool HeldByHost(Dictionary<string, string> holders)
    {
        foreach (string holder in holders.Values)
            if (IsHostHolder(holder)) return true;
        return false;
    }

    /// <summary>Enough hosts hold an intact copy: every store but a Lost one counts. Reads the queue's holder snapshot, like
    /// <see cref="Satisfied"/>. A node stopping or dying on a host changes none of this.</summary>
    private bool HostSatisfied(SaveRegistration save)
    {
        int copies = 0;
        foreach (string holder in save.LocationHolders.Values)
            if (IsHostHolder(holder) && !save.CorruptHolders.Contains(holder)
                && _replicaHolderMap.TryGetValue(holder, out HolderState state) && state.Store is not (null or HostStoreState.Lost)) copies++;
        return copies >= _replicaCopyTarget;
    }

    /// <summary>
    /// Whether <paramref name="node"/> should copy a host-held save now: its host holds no intact copy, fewer hosts than
    /// the target do, nobody else has it reserved, its host has room, and some Serving store can serve it.
    /// </summary>
    private bool HostEligibleReplica(SaveRegistration save, string node, DateTimeOffset now)
    {
        if (PlanHostOf(node) is not { } host || !HostAboveDiskFloor(host)) return false;
        int copies = 0;
        foreach (string holder in save.LocationHolders.Values)
        {
            if (!IsHostHolder(holder) || save.CorruptHolders.Contains(holder)) continue;
            string holding = HostOfHolder(holder)!;
            if (string.Equals(holding, host, StringComparison.OrdinalIgnoreCase)) return false;
            if (StoreState(holding) != HostStoreState.Lost) copies++;
        }
        if (copies >= HostCopyTarget()) return false;
        if (_replicaReservations.TryGetValue(save.SaveId, out (string Node, DateTimeOffset Until) reservation) && reservation.Node != node && reservation.Until > now)
            return false;
        return ReplicaSources(save).Length != 0;
    }

    /// <summary>
    /// HOOK, not implemented: replica work must not go to a host below its disk floor (HostTarget.DiskFloorMb). The
    /// registry cannot see a host's free disk yet - the host agent does not report it (step 7) - so every host passes.
    /// </summary>
    private static bool HostAboveDiskFloor(string host) => true;

    /// <summary>The holder a registering node's location belongs to: its host, when the node wrote into the host store
    /// (the location IS the store path - the registry computes it, <paramref name="storePath"/>, it does not take it from
    /// the request); else the node, as before (a location outside the store; the next load re-keys it once the file is moved).</summary>
    private string RegisteringHolder(string node, string location, Func<string, string> storePath)
    {
        if (!_hostStore || StoreHostOf(node) is not { } host || location != storePath(host)) return node;
        EnsureHostRow(host);
        return HostHolder(host);
    }

    /// <summary>The host whose store a registered node writes into: the plan's host, and for the World Authority - which
    /// node control does not run - the gateway host, where it always runs.</summary>
    private string? StoreHostOf(string node) => PlanHostOf(node)
        ?? (_state.Nodes.TryGetValue(node, out NodeRegistration? row) && row.Role == NodeRole.WA ? HostsFileSpelling(GatewayHost ?? MachineHost) : null);

    /// <summary>
    /// The registry alone decides whether a node keeps its saves in its host's store, and says so in the registration
    /// answer (<see cref="NodeRegistration.HostStore"/>); the node's own environment does not. Yes only when the
    /// catalogue is in host-store mode, the node offered it, and it reports the very host the plan runs it on (that name
    /// is the store directory both sides compute). Otherwise the node runs with saves in its data dir, as before, and
    /// in host-store mode that is logged: its saves are node-held and stop being served when it stops. Caller holds
    /// _lock, after the node's row is in.
    /// </summary>
    private bool NodeUsesHostStore(NodeJoinRequest request)
    {
        if (!_hostStore) return false;
        string? host = StoreHostOf(request.Node);
        string? refusal = !request.HostStore ? "it did not offer host-store mode (a plugin before T-0340, or it has no data root)"
            : host == null ? "node control does not run it on any host"
            : !string.Equals(request.Host, host, StringComparison.Ordinal) ? $"it reports host '{request.Host}', the plan has it on '{host}'" : null;
        if (refusal == null) return true;
        Console.WriteLine($"WARNING: host store: {request.Node} keeps its saves in its own data dir, not in the host store: {refusal}");
        return false;
    }

    private (DateTimeOffset At, Dictionary<string, int> Saves, int Unmoved)? _hostStoreCounts;

    /// <summary>
    /// Registry status (<c>hostStore</c>): whether the catalogue is in host-store mode, each host store's state and how
    /// many catalogued partition saves it holds, how many locations (partition and global saves) are still node-held
    /// at a legacy path (<c>unmoved</c>), and the live nodes that were NOT told to use the store (their saves are
    /// node-held), and the size of the alias map (<c>aliases</c>). A bench and cluster-status assert the mode on it. The save counts walk the in-memory catalogue
    /// at most once a minute - status is polled - and leave archived partitions' saves out. Caller holds _lock.
    /// </summary>
    private object HostStoreStatus()
    {
        var hosts = new SortedDictionary<string, object>(StringComparer.Ordinal);
        // aliases: step 6 - not zero means rows may have been released without the archive, in this run or an earlier one
        // (`cluster nodes prune` then keeps every node dir that still has save files, whatever the mode is now).
        int aliases = _state.HostStoreAliases.Count;
        if (!_hostStore) return new { enabled = false, hosts, unmoved = 0, legacyNodes = Array.Empty<string>(), aliases, adopted = HostStoreAdopted };
        DateTimeOffset now = _clock();
        if (_hostStoreCounts is not { } counts || now < counts.At || now - counts.At >= TimeSpan.FromMinutes(1))
        {
            counts = (now, new Dictionary<string, int>(StringComparer.Ordinal), 0);
            foreach (SaveRegistration save in _state.Saves.Values)
                foreach (string holder in save.LocationHolders.Values)
                    if (HostOfHolder(holder) is { } host) counts.Saves[host] = counts.Saves.GetValueOrDefault(host) + 1;
                    else counts.Unmoved++;
            foreach (GlobalSaveRegistration global in _state.GlobalSaves.Values)
                counts.Unmoved += global.LocationHolders.Values.Count(holder => !IsHostHolder(holder));
            _hostStoreCounts = counts;
        }
        foreach (string host in Control.Hosts.Keys.Concat(counts.Saves.Keys).Distinct(StringComparer.Ordinal))
            hosts[host] = new { state = StoreState(host).ToString(), saves = counts.Saves.GetValueOrDefault(host) };
        return new
        {
            enabled = true, hosts, unmoved = counts.Unmoved, aliases, adopted = HostStoreAdopted,
            legacyNodes = _state.Nodes.Values.Where(row => !IsHostHolder(row.Node) && !row.HostStore && row.LeaseExpires > now
                && row.State is NodeState.Active or NodeState.Closed).Select(row => row.Node).OrderBy(node => node, StringComparer.Ordinal).ToArray(),
        };
    }

    // ---- Step 4: host-held global saves and deletes.

    /// <summary>Whether <paramref name="node"/> should copy a host-held global save: its host has none and fewer hosts
    /// than the target do (a Lost store's copy does not count).</summary>
    private bool HostNeedsGlobal(GlobalSaveRegistration global, string node)
    {
        if (PlanHostOf(node) is not { } host || !HostAboveDiskFloor(host)) return false;
        int copies = 0;
        foreach (string holder in global.LocationHolders.Values)
        {
            if (!IsHostHolder(holder)) continue;
            string holding = HostOfHolder(holder)!;
            if (string.Equals(holding, host, StringComparison.OrdinalIgnoreCase)) return false;
            if (StoreState(holding) != HostStoreState.Lost) copies++;
        }
        return copies < HostCopyTarget();
    }

    /// <summary>A live server of every Serving host that holds the global save (the node fetches /global-save-file there).</summary>
    private HolderSource[] HostGlobalSources(GlobalSaveRegistration global) =>
        [.. global.LocationHolders.Values.Where(IsHostHolder).Distinct(StringComparer.Ordinal)
            .Select(holder => (Holder: holder, Server: HostServers(HostOfHolder(holder)!).FirstOrDefault()))
            .Where(x => x.Server != null).Select(x => new HolderSource(x.Holder, x.Server!.Endpoint))];

    /// <summary>The global save as recovery is told about it: a host holder has no endpoint of its own, so the answer
    /// names a live server of each Serving host that holds it (the World Authority reads its own host's store from disk
    /// and fetches from one of these only when that is gone).</summary>
    private GlobalSaveRegistration ServedGlobal(GlobalSaveRegistration global)
    {
        GlobalSaveRegistration copy = Snapshot(global);
        if (_hostStore)
            foreach (HolderSource source in HostGlobalSources(global)) copy.HolderEndpoints[source.Holder] = source.Endpoint;
        return copy;
    }

    /// <summary>Per host: the node that is handed the host's delete batch, and until when.</summary>
    private readonly Dictionary<string, (string Node, DateTimeOffset Until)> _hostDeleteTurn = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HostDeleteTurnLength = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The host holder whose pending deletes <paramref name="node"/> is handed now, or null. Any node can unlink a file
    /// in its host's store and a missing file is a no-op, so one node at a time gets the host's whole batch: the one
    /// whose turn it is, for a minute from its last poll; the next asker when that node is no longer a live server of
    /// the host or stopped polling. Caller holds _lock.
    /// </summary>
    private string? HostDeleteTurn(string node)
    {
        if (!_hostStore || PlanHostOf(node) is not { } host) return null;
        DateTimeOffset now = _clock();
        HolderProxy[] servers = HostServers(host);
        if (!servers.Any(x => x.Node == node)) return null;
        if (_hostDeleteTurn.TryGetValue(host, out (string Node, DateTimeOffset Until) turn) && turn.Node != node && turn.Until > now
            && servers.Any(x => x.Node == turn.Node))
            return null;
        _hostDeleteTurn[host] = (node, now + HostDeleteTurnLength);
        return HostHolder(host);
    }

    /// <summary>Per host: how many grants its store has served, which rotates its Active servers.</summary>
    private readonly Dictionary<string, int> _hostServerCursor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The live regular nodes of <paramref name="host"/> that can serve its store, in the order to try them: spares
    /// (their control listener is idle), then the Active nodes rotated by the host's cursor, then the Closed ones
    /// (draining). One fixed first choice would put every cross-host read on a single node's single-threaded listener.
    /// </summary>
    private HolderProxy[] HostServers(string host)
    {
        DateTimeOffset now = _clock();
        // #1210: managed mode has no node control; there a row's own host is its slot's.
        IEnumerable<NodeRegistration?> rows = NodeControlActive
            ? Control.Live.Values.Where(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)).Select(x => _state.Nodes.GetValueOrDefault(x.Node))
            : _state.Nodes.Values.Where(row => string.Equals(HostKey(row), host, StringComparison.OrdinalIgnoreCase));
        NodeRegistration[] live = [.. rows.Select(row => row!)
            .Where(row => row is { Role: NodeRole.Regular, State: NodeState.Active or NodeState.Closed } && row.LeaseExpires > now && !string.IsNullOrWhiteSpace(row.ControlEndpoint))
            .OrderBy(row => NodeNumber(row.Node)).ThenBy(row => row.Node, StringComparer.Ordinal)];
        NodeRegistration[] active = [.. live.Where(row => row.State == NodeState.Active && !row.Spare)];
        int turn = active.Length == 0 ? 0 : _hostServerCursor.GetValueOrDefault(host) % active.Length;
        return [.. live.Where(row => row.State == NodeState.Active && row.Spare).Concat(active.Skip(turn)).Concat(active.Take(turn))
            .Concat(live.Where(row => row.State == NodeState.Closed)).Select(row => new HolderProxy(row.Node, row.ControlEndpoint!))];
    }

    // ---- Step 6: stopped rows release without the archive; a released holder's catalogued files stay served.

    /// <summary>Self-test switch that restores the release pass before step 6 - in store mode too it keeps every row an
    /// archived save names - to earn the red of host-store-release.</summary>
    internal static bool HostStoreReleaseChecksArchive;
    /// <summary>Self-test switch: without it a released holder's files are not served (what a bare release would do), to
    /// show that host-store-release can tell.</summary>
    internal static bool ServeReleasedHolders = true;

    /// <summary>
    /// The host of a legacy (node id) holder whose row was released, else null. In store mode the release pass no longer
    /// asks the archive who names a stopped node (<see cref="ReleaseGoneRows"/>), so an archived save - or one a rollback
    /// left node-held - can name an id that has no row. What made such a holder servable was never the row but its
    /// host: the file is where the catalogue says, on that host's disk, and any live node there reads it as a proxy
    /// under a grant bound to that one location (T-0323). The frozen alias map (<see cref="HostOfLegacyHolder"/>)
    /// remembers the host; every released row leaves its entry there. Not tied to the switch: a registry started with
    /// the switch off again must still serve what an earlier run released. #1210: in managed mode too, where a removed
    /// slot's row is released the same way.
    /// </summary>
    private string? ReleasedHolderHost(string holder) =>
        ServeReleasedHolders && (NodeControlActive || _state.ManagedDeployment is not null) && !IsHostHolder(holder) && !_state.Nodes.ContainsKey(holder)
        && _state.HostStoreAliases.TryGetValue(holder, out string? host) ? host : null;

    /// <summary>Freezes the host of a row about to be released (store mode); false - and the row stays - when no store
    /// directory can be named for it (a host name that is not one path component).</summary>
    private bool FreezeReleasedHost(string node)
    {
        try
        {
            HostOfLegacyHolder(node);
            return true;
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"WARNING: host store: the row of {node} is kept: {error.Message}");
            return false;
        }
    }

    /// <summary>A released holder is usable exactly while a live node of its host can serve its files (as a Dead gone
    /// holder is, T-0348): no node there - a host cut - and it is not offered.</summary>
    private bool ReleasedHolderServed(string holder) => ReleasedHolderHost(holder) is { } host && HostServers(host).Length != 0;

    // ---- Step 3: save grants bound to (save id, host).

    /// <summary>
    /// The host a save grant request is for, when the save is held there by the host: the request names the host holder
    /// as its receiver, or (the current node plugin, which was told a "proxy") a node and the catalogued location that
    /// the host holds. Null: not a host-held location, the receiver-bound grant applies. Caller holds _lock.
    /// </summary>
    private string? HostOfGrant(GrantRequest request)
    {
        if (!_hostStore || !TryGetSave(Key(new SaveId(request.Partition, request.Generation, request.Sequence)), out SaveRegistration? save)) return null;
        string? holder = request.Location != null ? save.LocationHolders.GetValueOrDefault(request.Location)
            : IsHostHolder(request.Receiver) ? request.Receiver : null;
        if (holder == null || !IsHostHolder(holder)) return null;
        string host = HostOfHolder(holder)!;
        if (!save.LocationHolders.ContainsValue(holder) || save.CorruptHolders.Contains(holder))
            throw new RegistryConflictException($"Save grant: host '{host}' holds no intact copy of save {save.SaveId}");
        if (StoreState(host) != HostStoreState.Serving)
            throw new RegistryConflictException($"Save grant: no node of host '{host}' can serve save {save.SaveId} now");
        return host;
    }

    /// <summary>The servers of a host grant, and the host's turn moves on. Caller holds _lock.</summary>
    private HolderProxy[] NextHostServers(string host)
    {
        HolderProxy[] servers = HostServers(host);
        _hostServerCursor[host] = _hostServerCursor.GetValueOrDefault(host) + 1;
        return servers;
    }

    /// <summary>
    /// Who may consume a host grant: a registered incarnation (the caller checked node and epoch) that is not Dead and
    /// that node control runs on the grant's host - the plan's host, not the one the process reports. A stopped node is
    /// in no plan, so neither its stale process nor anything else answering on its old port can; the node that took
    /// over the port is a server of the host in its own right.
    /// </summary>
    private bool MayServeHostGrant(NodeRegistration receiver, string host) =>
        receiver.State != NodeState.Dead && string.Equals(PlanHostOf(receiver.Node), host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Self-test: how many saves wait for a copy.</summary>
    internal int ReplicaQueueDepth()
    {
        lock (_lock)
        {
            RefreshReplicaQueue(_clock());
            return _replicaQueue.Count;
        }
    }
}
