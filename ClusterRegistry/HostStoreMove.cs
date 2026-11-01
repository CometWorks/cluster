using System.Text.Json;

namespace ClusterRegistry;

/// <summary>One legacy save file a host agent found in a stopped node's data dir (T-0340 step 7): whose dir, which save,
/// and what the file says it is (its size, and the body checksum in its trailer; null when the file is unreadable).</summary>
internal sealed record HostStoreAdoptFile(string Node, ulong Partition, long Generation, long Sequence, bool Global, long SizeBytes, string? Checksum);

/// <summary><see cref="GatewayRegistry.HostStoreAdopt"/>: <c>Commit</c> false only answers what the registry would do.</summary>
internal sealed record HostStoreAdoptRequest(string Host, bool Commit, HostStoreAdoptFile[] Files);

/// <summary>Per file: <c>Status</c> (see <see cref="GatewayRegistry.HostStoreAdopt"/>) and the catalogue's size and checksum.
/// <c>ArchiveFilesDue</c> (T-0400): the save is archived and its partition is past the archive retention age
/// (retention.archiveFileDays) - its files are due for deletion, so a sweep may delete the file instead of adopting it
/// (the registry still names that location until a commit path for it exists).</summary>
internal sealed record HostStoreAdoptResult(string Status, long? SizeBytes = null, string? Checksum = null, bool Archived = false, bool ArchiveFilesDue = false);

/// <summary>
/// T-0340 step 7: the host agent's file move, registry side. A world from before store mode keeps its saves in node data
/// dirs, node-held; on r113 every stopped row was pinned by such locations (265k archived saves, ~10 GB, many only in
/// stopped nodes' dirs). The load-time re-key (<see cref="RekeyCatalogueToHostStore"/>) needs the registry to SEE the
/// store file, which it can only on its own host, and it never rewrites archived rows. This route is the per-file
/// evidence the host agent brings instead, for any host, in memory and in the archive:
/// <list type="number">
/// <item>the agent asks (Commit false) what each legacy file of a stopped dir is to the catalogue;</item>
/// <item>it links the files the catalogue names into its host's store (link(2): same file system, no copy, never
/// replacing a file) and verifies them;</item>
/// <item>it commits: the registry re-keys exactly that node's location of that save to the host holder - in memory,
/// or by rewriting the archived row in place (a partition is NOT read back: that would put 265k archived saves in
/// front of the replica queue) - and writes it durably before it answers;</item>
/// <item>only a file answered <c>rekeyed</c> or <c>hostHeld</c> may then be deleted from the node dir.</item>
/// </list>
/// Statuses: <c>adopt</c> (would re-key), <c>rekeyed</c>, <c>hostHeld</c> (the node's copy is no longer catalogued and
/// this host's store holds the save), <c>unnamed</c> (the save is catalogued but names neither the node's copy nor this
/// host), <c>uncatalogued</c> (no such save, in memory or archived), <c>live</c> (the node is not gone), <c>mismatch</c>
/// (size or checksum differ from the catalogue), <c>wrongHost</c> (the node ran on another host), <c>corrupt</c> (the
/// node's or the host's copy is marked corrupt), <c>pendingDelete</c>, <c>busy</c> (#1276: a recovery claim or an
/// unexpired grant still names the node's copy; a later sweep moves it), <c>invalid</c>. A file answered anything but the
/// first four is left where it is.
/// </summary>
internal sealed partial class GatewayRegistry
{
    /// <summary>The most files one adopt call may name: the commit writes durably under the lock.</summary>
    internal const int HostStoreAdoptBatch = 2000;

    internal object HostStoreAdopt(HostStoreAdoptRequest request)
    {
        if (request.Files is not { } files || files.Length > HostStoreAdoptBatch)
            throw new ArgumentException($"host-store adopt takes 1..{HostStoreAdoptBatch} files");
        lock (_lock)
        {
            if (!NodeControlActive) throw new RegistryConflictException("host-store adopt needs node control");
            string host = HostsFileSpelling(request.Host ?? "");
            if (!Control.Hosts.ContainsKey(host)) throw new ArgumentException($"host '{request.Host}' is not in the hosts file");
            HostStoreRoot(RunRoot, host);
            if (request.Commit && !_hostStore) throw new RegistryConflictException("the catalogue is not in host-store mode; nothing is re-keyed");
            var results = new HostStoreAdoptResult[files.Length];
            int rekeyed = 0;
            HashSet<ulong> claimed = AdoptClaimedPartitions();
            HashSet<string> granted = AdoptGrantedLocations();
            for (int i = 0; i < files.Length; i++)
            {
                results[i] = AdoptOne(files[i], host, request.Commit, claimed, granted);
                if (results[i].Status == "rekeyed") rekeyed++;
            }
            if (rekeyed != 0)
            {
                // Durable before the answer: the agent deletes the node-dir file on it.
                Persist();
                FlushState();
                HostStoreAdopted += rekeyed;
                Console.WriteLine($"registry: host store: re-keyed {rekeyed} legacy location(s) to host:{host} on the host agent's evidence");
            }
            return new { storeMode = _hostStore, host, files = results };
        }
    }

    /// <summary>Locations the host agents' evidence re-keyed since this registry started (status hostStore.adopted).</summary>
    internal long HostStoreAdopted;

    /// <summary>Self-test: state or archive changes not yet written.</summary>
    internal bool StatePendingWrite { get { lock (_lock) return _dirty || _store.PendingRows != 0; } }

    /// <summary>The archived partitions, for the host agent's archive-retention knob (default off). Caller: the route.</summary>
    internal ulong[] HostStoreArchivedPartitions() { lock (_lock) return [.. _archivedPartitions.Keys.Order()]; }

    /// <summary>#1276 (T-0296 F33): a recovery claim hands the node its cover at the legacy paths, and the node fetches them
    /// later through the gone-holder proxy under a grant bound to that path. A re-key commits the agent to unlinking the
    /// path at once, so that fetch fails and the node silently loads an older fallback. Off only for the self-test's red.</summary>
    internal static bool AdoptHoldsClaimedSaves = true;

    /// <summary>The partitions whose saves a recovery claim may still fetch: every Recovering row and every partition its
    /// cover reaches (a split or merge cover names its ancestors' saves). The fallbacks are saves of the same partitions.</summary>
    private HashSet<ulong> AdoptClaimedPartitions()
    {
        HashSet<ulong> claimed = [];
        if (!AdoptHoldsClaimedSaves) return claimed;
        foreach (PartitionOwnership owner in _state.Partitions.Values.Where(x => x.Recovering))
        {
            claimed.Add(owner.Partition);
            foreach (SaveCover item in ResolveCover(owner.Partition, long.MaxValue, null, []))
                claimed.Add(item.Save.SaveId.Partition);
        }
        return claimed;
    }

    /// <summary>The locations an unexpired proxied grant binds: its fetch reads exactly that path.</summary>
    private HashSet<string> AdoptGrantedLocations()
    {
        DateTimeOffset now = _clock();
        return AdoptHoldsClaimedSaves
            ? _grants.Values.Where(x => x.Location != null && x.Expires > now).Select(x => x.Location!).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    private HostStoreAdoptResult AdoptOne(HostStoreAdoptFile file, string host, bool commit, HashSet<ulong> claimed, HashSet<string> granted)
    {
        string node = file.Node ?? "";
        if (node.Length == 0 || IsHostHolder(node)) return new("invalid");
        if (Control.Live.ContainsKey(node) || _state.Nodes.TryGetValue(node, out NodeRegistration? row) && row.State is NodeState.Active or NodeState.Closed)
            return new("live");
        if (file.Global)
        {
            var id = new GlobalSaveId(file.Generation, file.Sequence);
            if (!_state.GlobalSaves.TryGetValue(GlobalKey(id), out GlobalSaveRegistration? global)) return new("uncatalogued");
            return AdoptLocation(global.Locations, global.LocationHolders, global.HolderEndpoints, null, global.Node, global.SizeBytes, global.Checksum,
                global.PendingDelete, HostStorePath(RunRoot, host, id), file, host, commit, () => Touch(global), archived: false, busy: false, granted);
        }
        var saveId = new SaveId(file.Partition, file.Generation, file.Sequence);
        string key = Key(saveId);
        if (_state.Saves.TryGetValue(key, out SaveRegistration? save))
            return AdoptLocation(save.Locations, save.LocationHolders, save.HolderEndpoints, save.CorruptHolders, save.Node, save.SizeBytes, save.Checksum,
                save.PendingDelete, HostStorePath(RunRoot, host, saveId), file, host, commit, () => Touch(save), archived: false,
                claimed.Contains(file.Partition), granted);
        if (!_archivedPartitions.ContainsKey(file.Partition) || _store.ReadArchived(file.Partition, key) is not { } stored) return new("uncatalogued");
        SaveRegistration archived = JsonSerializer.Deserialize<SaveRegistration>(stored.Value) ?? throw new InvalidDataException($"Archived save {key} is empty");
        // As Rehydrate reads it: a location without a holder is the writer's.
        foreach (string location in archived.Locations) archived.LocationHolders.TryAdd(location, archived.Node);
        // T-0400: the answer says whether the partition's files are due under archive retention; a copy adopted into a
        // store of a partition already swept re-opens its sweep, or that store file would never be deleted.
        RegistryStore.ArchiveStamp? stamp = _store.ReadStamp(file.Partition);
        int days = ArchiveFileDays();
        bool due = days > 0 && stamp != null && stamp.ArchivedMs <= (_clock() - TimeSpan.FromDays(days)).ToUnixTimeMilliseconds();
        HostStoreAdoptResult result = AdoptLocation(archived.Locations, archived.LocationHolders, archived.HolderEndpoints, archived.CorruptHolders,
            archived.Node, archived.SizeBytes, archived.Checksum, archived.PendingDelete, HostStorePath(RunRoot, host, saveId), file, host, commit,
            () => _store.Rearchive(file.Partition, key, stored.Generation, stored.Located, archived, typeof(SaveRegistration)), archived: true,
            claimed.Contains(file.Partition), granted);
        if (result.Status == "rekeyed" && stamp is { Swept: true })
        {
            _store.Stamp(file.Partition, stamp.ArchivedMs);
            _archiveRetentionLegacy -= stamp.Legacy;
        }
        return result with { ArchiveFilesDue = due };
    }

    /// <summary>The re-key of one node's copy of one save to <paramref name="host"/>'s store, on the host agent's evidence
    /// (the file is in that store, with the catalogue's size and checksum). Only that node's location changes; a copy the
    /// host already holds merges with it, exactly as the load-time re-key merges copies on one host. <paramref name="busy"/>:
    /// a recovery claim may still fetch the save; <paramref name="granted"/>: the locations unexpired grants bind.</summary>
    private HostStoreAdoptResult AdoptLocation(HashSet<string> locations, Dictionary<string, string> holders, Dictionary<string, string> endpoints,
        HashSet<string>? corrupt, string writer, long size, string checksum, bool pendingDelete, string store, HostStoreAdoptFile file, string host,
        bool commit, Action touch, bool archived, bool busy, HashSet<string> granted)
    {
        string hostKey = HostHolder(host);
        string[] mine = locations.Where(location => holders.GetValueOrDefault(location, writer) == file.Node).ToArray();
        if (mine.Length == 0)
            return new(locations.Contains(store) && holders.GetValueOrDefault(store) == hostKey ? "hostHeld" : "unnamed", size, checksum, archived);
        if (pendingDelete) return new("pendingDelete", size, checksum, archived);
        string? known = KnownHostOfLegacyHolder(file.Node);
        if (known != null && !string.Equals(known, host, StringComparison.OrdinalIgnoreCase)) return new("wrongHost", size, checksum, archived);
        if (file.SizeBytes != size || !string.Equals(file.Checksum, checksum, StringComparison.OrdinalIgnoreCase)) return new("mismatch", size, checksum, archived);
        if (corrupt != null && (corrupt.Contains(file.Node) || corrupt.Contains(hostKey))) return new("corrupt", size, checksum, archived);
        if (busy || mine.Any(granted.Contains)) return new("busy", size, checksum, archived);
        if (!commit) return new("adopt", size, checksum, archived);
        // The file is on this host's disk: that is where the node's files are, whatever a guess would have said. A known
        // host is frozen as it is (it equals this one); an unknown one is frozen to this host.
        if (known == null) _state.HostStoreAliases[file.Node] = host;
        else HostOfLegacyHolder(file.Node);
        foreach (string location in mine)
        {
            locations.Remove(location);
            holders.Remove(location);
        }
        if (!holders.ContainsValue(file.Node)) endpoints.Remove(file.Node);
        locations.Add(store);
        holders[store] = hostKey;
        EnsureHostRow(host);
        touch();
        return new("rekeyed", size, checksum, archived);
    }

    /// <summary><see cref="HostOfLegacyHolder"/> without its last resort (the gateway host) and without freezing anything:
    /// null when nothing the registry keeps says where the node ran.</summary>
    private string? KnownHostOfLegacyHolder(string node)
    {
        if (_state.HostStoreAliases.TryGetValue(node, out string? known)) return known;
        string? host = Control.Live.GetValueOrDefault(node)?.Host
            ?? _state.NodeHistory.LastOrDefault(x => x.Node == node)?.Host
            ?? _state.Nodes.GetValueOrDefault(node)?.Host;
        return host == null ? null : HostsFileSpelling(host);
    }
}
