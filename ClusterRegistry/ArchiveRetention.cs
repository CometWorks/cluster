using System.Globalization;
using System.Text.Json;

namespace ClusterRegistry;

/// <summary>
/// T-0400 archive retention (Viktor 2026-10-05): the FILES of the saves of a partition archived more than
/// <c>retention.archiveFileDays</c> days ago are deleted; the registry ROWS stay (lineage and history stay queryable), each
/// deleted location recorded in <see cref="SaveRegistration.DeletedLocations"/>. Archived saves were never deleted before,
/// and w33 retires partitions fast (T-0339: 42% of live ones under 60 s), so their files only piled up.
///
/// <list type="bullet">
/// <item>Age is time since the partition was ARCHIVED: the stamp ArchiveRetired writes with the archive move
/// (RegistryStore.StampTable). Rehydrate removes it; a re-archive stamps afresh. Partitions archived before this build are
/// stamped the first time this build loads them, so their files go a full period after it ships, never earlier.</item>
/// <item>Eligibility is decided in PlanRetention, under the lock, AFTER its <c>needed</c> walk (which brings back any
/// archived partition a live cover reaches): a partition still archived then is reached by no live cover. It must also be
/// retired, with its retiring lineage record collapsed (its content is durable downstream), unowned, not recovering, not
/// offline, not named dirty by a snapshot, and none of its saves may be in <c>needed</c>.</item>
/// <item>Files are deleted through the existing directive/ack path, and each ack rewrites the archived row in place: a
/// location held by <c>host:H</c> at exactly H's store path goes to the node that has H's delete turn (PendingDeletes);
/// a location a node id holds goes to that node itself while it is Active (T-0412: a Quasar-managed cluster has no host
/// store, so every copy is node-held in a node dir that lives as long as the node). A registered holder that is not
/// Active now (Closed, Dead, Empty) leaves its partition unswept, read again on a later round: Quasar restarts the same
/// node id into the same dir. A copy of a node whose row is gone (released: a stopped node's dir in store mode) is never
/// handed out - no node would act on it - and is counted (legacy): T-0340's host sweep (<c>cluster hostd sweep</c>)
/// adopts it into the host store, which re-opens the partition's sweep. A node-held directive not acknowledged within <see cref="ArchiveHandOutGrace"/> is recorded as
/// deleted: its holder deleted it and lost the ack, or refused it (T-0163, outside its save root), and either way only
/// that node could act on it, so it must not hold the batch for ever.</item>
/// <item>Paced: a pass reads at most <see cref="ArchiveFileBatch"/> archived rows, straight from the store (the
/// partition is not brought back), and keeps at most that many locations outstanding.</item>
/// </list>
///
/// The outstanding directives live in memory only. They are reconstructible: the archived rows are the truth (an acked
/// location is gone from its row), a partition is marked swept only when its last directive is acked, and the cursor
/// over unswept due partitions starts afresh after a restart, re-queueing whatever is still catalogued. A node that
/// deleted a file whose ack was lost gets the directive again; deleting a missing file is a no-op and it acks.
/// </summary>
internal sealed partial class GatewayRegistry
{
    internal const string ArchiveFileDaysKey = "retention.archiveFileDays";
    internal const int ArchiveFileDaysDefault = 7;
    /// <summary>CLUSTER_ARCHIVE_FILE_BATCH: archived rows (and due stamps) one pass may read, and store files that may be
    /// outstanding. 250: on a copy of the live w33 registry (304k archived partitions, ~1 KB rows) a full 250-row pass held
    /// the lock p50 2.7 / p90 5.6 ms over 60 passes, max 22 ms in a pass that ran into a GC (5 of 60); at 500 the typical
    /// pass was ~5 ms with ~50 ms GC passes. 250 rows a minute (~100 store files on w33) is still far above its archive
    /// rate (~30k saves a day); the first-sight backlog takes ~20 h to read through once.</summary>
    internal static int ArchiveFileBatch = EnvInt("CLUSTER_ARCHIVE_FILE_BATCH", 250);
    /// <summary>Due stamps read per store query; a pass reads several slices until its row budget is spent.</summary>
    private const int ArchiveStampSlice = 256;
    /// <summary>T-0412: how long a node-held directive may stay handed out without an ack. An Active node polls its
    /// deletions every replication loop (seconds), deletes and acks in the same call.</summary>
    internal static readonly TimeSpan ArchiveHandOutGrace = TimeSpan.FromMinutes(10);

    /// <summary>The knob's value as the registry acts on it: absent = the default; a non-negative integer = that many days
    /// (0 = off). Anything else is refused at the PUT (<see cref="NormalizeArchiveFileDays"/>); should one ever be stored,
    /// it reads as OFF - this knob deletes files, so an unreadable value must not.</summary>
    internal static int ArchiveFileDaysOf(string? value) => value == null ? ArchiveFileDaysDefault
        : int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int days) ? days : 0;

    /// <summary>The PUT's validation, as the nodes' ClusterConfig.Normalize does it for their integer knobs.</summary>
    internal static string NormalizeArchiveFileDays(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int days)
            ? days.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Config key {ArchiveFileDaysKey} expects a non-negative integer (days, 0 = off), got {value}");

    /// <summary>Caller holds _lock.</summary>
    private int ArchiveFileDays() => ArchiveFileDaysOf(_state.HandoverConfig.GetValueOrDefault(ArchiveFileDaysKey));

    private sealed class PendingArchiveFile(SaveId id, string holder)
    {
        public readonly SaveId Id = id;
        public readonly string Holder = holder;
        /// <summary>When a node was first handed it: from then on the file may be gone even without an ack.</summary>
        public DateTimeOffset? HandedOutAt;
        public bool HandedOut => HandedOutAt != null;
    }

    private sealed class ArchiveSweep(long archivedMs, int legacy, bool complete)
    {
        public readonly long ArchivedMs = archivedMs;
        public readonly int Legacy = legacy;
        /// <summary>Every store file of the partition is queued: the last ack marks it swept. False once a file left the
        /// queue unacknowledged (it is still in the store).</summary>
        public bool Complete = complete;
        public readonly Dictionary<(string Key, string Location), PendingArchiveFile> Files = [];
    }

    /// <summary>Per archived partition being swept: its store files not yet acknowledged deleted.</summary>
    private readonly Dictionary<ulong, ArchiveSweep> _archiveSweeps = [];
    private int _archiveFilesOutstanding;
    /// <summary>Where the pass continues in (archived, partition) order; back to the start once it reaches the end.</summary>
    private (long ArchivedMs, ulong Partition) _archiveSweepCursor = (long.MinValue, 0);
    /// <summary>Status: the last pass's figures, and the swept partitions' legacy locations (store total at load, kept in step).</summary>
    private long _archiveRetentionDue, _archiveRetentionLegacy, _archiveRetentionSkipped, _archiveRetentionLegacyStamped, _archiveRetentionUnacked;
    private int _archiveRetentionLastRows;
    private double _archiveRetentionLastMs;

    /// <summary>
    /// Load (constructor, after the archive summary and the lineage index): stamps the archived partitions that have no
    /// stamp at first sight, and logs ONE line with how many, and - read-only, for the CH - how many would already be
    /// past the retention age by the proxy max(newest save Created, that save's Created advanced to the retiring lineage
    /// record's cluster time). The proxy is never used to delete.
    /// </summary>
    private void StampArchiveAtLoad()
    {
        DateTimeOffset now = _clock();
        (int stamped, int dropped, List<(ulong Partition, DateTimeOffset? Created, long? ClusterTime)> legacy) = _store.StampUnstamped(now.ToUnixTimeMilliseconds());
        _archiveRetentionLegacyStamped = stamped;
        _archiveRetentionLegacy = _store.SweptLegacyLocations();
        if (stamped == 0 && dropped == 0) return;
        int days = ArchiveFileDays();
        int past = 0, unknown = 0;
        foreach ((ulong partition, DateTimeOffset? created, long? clusterTime) in legacy)
        {
            if (created is not { } at) { unknown++; continue; }
            // Cluster time is milliseconds but does not advance while the cluster is stopped, so only the span between
            // the newest save and the retiring record is carried over to wall time, and only when it is forward.
            if (RetiringRecord(partition) is { } retiring && clusterTime is { } saved && retiring.ClusterTime > saved
                && retiring.ClusterTime - saved < TimeSpan.FromDays(3650).TotalMilliseconds)
                at += TimeSpan.FromMilliseconds(retiring.ClusterTime - saved);
            if (days > 0 && now - at > TimeSpan.FromDays(days)) past++;
        }
        Console.WriteLine($"registry: archive retention: {stamped} archived partition(s) had no archive stamp and were stamped at first sight "
            + $"{now:yyyy-MM-ddTHH:mm:ssZ} (their files go {days} day(s) after that at the earliest, {ArchiveFileDaysKey}={days}); "
            + $"read-only proxy max(newest save Created, retiring lineage op time): {past} would already be past {days} day(s), "
            + $"{unknown} without a proxy; not used to delete" + (dropped != 0 ? $"; {dropped} stale stamp(s) of partitions no longer archived dropped" : ""));
    }

    /// <summary>The partitions an archive retention pass may not touch, whatever their age: still archived but owned,
    /// recovering, offline, not retired, retired by a record that has not collapsed, or named dirty by a snapshot.</summary>
    private string? ArchiveSweepRefusal(ulong partition, HashSet<ulong> neededPartitions, HashSet<ulong> dirtyPartitions)
    {
        if (_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner) && (owner.Node != null || owner.Recovering)) return "owned";
        if (_state.OfflinePartitions.ContainsKey(partition)) return "offline";
        if (!IsRetired(partition) || RetiringRecord(partition) is not { Collapsed: true }) return "lineage";
        if (dirtyPartitions.Contains(partition)) return "dirty";
        if (neededPartitions.Contains(partition)) return "needed";
        return null;
    }

    /// <summary>T-0412: whether a node holder can be handed its archived copy now - Active with a live lease, the state in
    /// which a node polls its deletions - (true), may become so again (null: a registered row in any other state; Quasar
    /// restarts a Dead or Empty node under the same id, into the same dir), or never will (false: the row is released - a
    /// stopped node's dir, the host sweep's).</summary>
    private bool? NodeHolderDeletes(string holder) =>
        !_state.Nodes.TryGetValue(holder, out NodeRegistration? row) ? false
            : row.State == NodeState.Active && row.LeaseExpires > _clock() ? true : null;

    /// <summary>
    /// One archive retention pass: PlanRetention, under _lock, after ArchiveRetired and against the same
    /// <paramref name="needed"/>. Returns whether it queued a store change (the caller persists). The due count is read
    /// before the lock (<paramref name="due"/>).
    /// </summary>
    private bool SweepArchivedFiles(HashSet<SaveId> needed, long due)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int days = ArchiveFileDays();
        _archiveRetentionDue = due;
        bool off = days <= 0;
        long cutoff = (_clock() - TimeSpan.FromDays(Math.Max(days, 0))).ToUnixTimeMilliseconds();
        HashSet<ulong> neededPartitions = [.. needed.Select(id => id.Partition)];
        HashSet<ulong> dirtyPartitions = [.. _state.Snapshots.SelectMany(x => x.Dirty)
            .Where(x => x.StartsWith("partition:", StringComparison.Ordinal))
            .Select(x => ulong.TryParse(x.AsSpan("partition:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong p) ? p : 0)];
        // One store state per host per pass (StoreState walks the live node records).
        var serving = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Serving(string host) => serving.TryGetValue(host, out bool known) ? known : serving[host] = StoreState(host) == HostStoreState.Serving;
        // Whoever acts on a queued file can do so now: its host's store serves, or its node holder is Active.
        bool Deletes(string holder) => HostOfHolder(holder) is { } host ? Serving(host) : NodeHolderDeletes(holder) == true;
        DateTimeOffset now = _clock();
        bool changed = false;
        int unacked = 0;
        // The queue is re-checked every pass. A file not handed out yet leaves it when retention is off, when no node can
        // act on it now (a dark host, a node holder no longer Active: neither may hold the whole batch), or when its
        // partition may no longer be touched; the partition stays unswept and is read again on a later round. A file
        // already handed out stays: the node may have deleted it, and its ack must still land (a rehydrate records it as
        // deleted). Only a node-held one past ArchiveHandOutGrace goes, recorded as deleted (see the class summary).
        foreach ((ulong partition, ArchiveSweep sweep) in _archiveSweeps.ToArray())
        {
            bool refusedNow = off || ArchiveSweepRefusal(partition, neededPartitions, dirtyPartitions) != null;
            foreach (KeyValuePair<(string Key, string Location), PendingArchiveFile> file in sweep.Files.ToArray())
                if (!file.Value.HandedOut && (refusedNow || !Deletes(file.Value.Holder)))
                {
                    sweep.Files.Remove(file.Key);
                    sweep.Complete = false;
                    _archiveFilesOutstanding--;
                }
                else if (file.Value.HandedOutAt is { } handed && now - handed > ArchiveHandOutGrace && HostOfHolder(file.Value.Holder) == null)
                {
                    sweep.Files.Remove(file.Key);
                    _archiveFilesOutstanding--;
                    RecordArchivedDeletion(file.Value.Id, file.Key.Key, file.Key.Location, counted: false);
                    FinishArchiveSweep(partition, sweep);
                    unacked++;
                    changed = true;
                }
            if (sweep.Files.Count == 0) _archiveSweeps.Remove(partition);
        }
        if (unacked != 0)
        {
            _archiveRetentionUnacked += unacked;
            Console.WriteLine($"WARNING: registry: archive retention: {unacked} node-held archived file(s) handed out more than "
                + $"{ArchiveHandOutGrace.TotalMinutes:F0} min ago and not acknowledged by their holder; recorded as deleted");
        }
        int rows = 0, examined = 0, queued = 0, swept = 0, skipped = 0;
        if (!off && _archiveFilesOutstanding < ArchiveFileBatch)
        {
            Dictionary<ulong, RegistryStore.ArchiveStamp?> pending = _store.QueuedStamps();
            // Reused across rows: the scan reads four fields per row, not a whole SaveRegistration.
            List<string> scanLocations = [];
            List<(string Location, string Holder)> scanHolders = [];
            // Bounded three ways: archived rows read, stamps examined (a refused partition costs one), files outstanding.
            bool Spent() => rows >= ArchiveFileBatch || examined >= ArchiveFileBatch || _archiveFilesOutstanding >= ArchiveFileBatch;
            while (!Spent())
            {
                List<(ulong Partition, long ArchivedMs)> slice = _store.DueStamps(cutoff, _archiveSweepCursor, ArchiveStampSlice);
                int consumed = 0;
                foreach ((ulong partition, long archivedMs) in slice)
                {
                    if (Spent()) break;
                    consumed++;
                    _archiveSweepCursor = (archivedMs, partition);
                    examined++;
                    // The committed stamp, unless a write still queued changed it (rehydrated, re-archived, swept).
                    if (pending.TryGetValue(partition, out RegistryStore.ArchiveStamp? queuedStamp)
                        && (queuedStamp is null || queuedStamp.Swept || queuedStamp.ArchivedMs > cutoff)) continue;
                    if (!_archivedPartitions.ContainsKey(partition) || _archiveSweeps.ContainsKey(partition)) continue;
                    if (ArchiveSweepRefusal(partition, neededPartitions, dirtyPartitions) != null) { skipped++; continue; }
                    var files = new Dictionary<(string Key, string Location), PendingArchiveFile>();
                    int legacy = 0;
                    bool waiting = false, refused = false;
                    foreach ((string key, RegistryStore.ArchivedRow row) in _store.ReadArchivedPartition(partition))
                    {
                        rows++;
                        SaveId id = ScanArchivedRow(row.Value, key, scanLocations, scanHolders, out string writer);
                        if (needed.Contains(id)) { refused = true; break; }
                        foreach (string location in scanLocations)
                        {
                            // As Rehydrate reads it: a location without a holder is the writer's.
                            string holder = writer;
                            foreach ((string held, string by) in scanHolders)
                                if (held == location) { holder = by; break; }
                            if (StoreFileHost(id, location, holder) is { } host)
                            {
                                if (!Serving(host)) waiting = true;
                                else files[(key, location)] = new PendingArchiveFile(id, holder);
                            }
                            else if (HostOfHolder(holder) != null) legacy++;   // a host holder's file outside its store path
                            // T-0412: a node's own copy goes to that node while it is Active; a released node's is legacy.
                            else switch (NodeHolderDeletes(holder))
                            {
                                case true: files[(key, location)] = new PendingArchiveFile(id, holder); break;
                                case null: waiting = true; break;
                                default: legacy++; break;
                            }
                        }
                    }
                    if (refused) { skipped++; continue; }
                    if (files.Count != 0)
                    {
                        // A file no node can act on now (a host no node serves, a node holder not Active) is not queued;
                        // then the partition stays unswept after the last ack and is read again on a later round.
                        var sweep = new ArchiveSweep(archivedMs, legacy, complete: !waiting);
                        foreach (KeyValuePair<(string, string), PendingArchiveFile> file in files) sweep.Files.Add(file.Key, file.Value);
                        _archiveSweeps[partition] = sweep;
                        _archiveFilesOutstanding += files.Count;
                        queued += files.Count;
                    }
                    else if (!waiting)
                    {
                        // No file left that a live node can delete: swept. Its legacy locations stay for T-0340's host sweep.
                        _store.MarkSwept(partition, archivedMs, legacy);
                        _archiveRetentionLegacy += legacy;
                        swept++;
                        changed = true;
                    }
                }
                if (consumed == slice.Count && slice.Count < ArchiveStampSlice)
                {
                    // The end of the due partitions: the next round starts over (what is left unswept is read again then).
                    _archiveSweepCursor = (long.MinValue, 0);
                    break;
                }
            }
        }
        _archiveRetentionSkipped = skipped;
        _archiveRetentionLastRows = rows;
        _archiveRetentionLastMs = clock.Elapsed.TotalMilliseconds;
        if (rows != 0 || swept != 0)
            Console.WriteLine($"registry: archive retention: {rows} archived row(s) read, {queued} file(s) queued for deletion, "
                + $"{swept} partition(s) swept, {skipped} skipped (owned/recovering/offline/lineage/dirty/needed); {_archiveFilesOutstanding} outstanding, "
                + $"{_archiveRetentionDue} due at the last count; {_archiveRetentionLastMs:F1} ms under the registry lock ({ArchiveFileDaysKey}={days})");
        return changed;
    }

    /// <summary>
    /// The sweep's slim read of one archived row (a SaveRegistration's JSON): SaveId, Node, Locations and LocationHolders
    /// into the caller's reused lists, every other property skipped. On w33 a full deserialization per row (1 KB, home
    /// stamps, endpoints) allocated enough that 500-row passes ran into ~50 ms GCs under the lock every few passes. The ack
    /// rewrite and Rehydrate still deserialize the whole row - they write it back or keep it.
    /// </summary>
    private static SaveId ScanArchivedRow(byte[] json, string key, List<string> locations, List<(string, string)> holders, out string writer)
    {
        locations.Clear();
        holders.Clear();
        writer = "";
        ulong partition = 0;
        long generation = 0, sequence = 0;
        bool hasId = false;
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException($"Archived save {key} is not an object");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("SaveId"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException($"Archived save {key}: SaveId is not an object");
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("Partition"u8)) { reader.Read(); partition = reader.GetUInt64(); }
                    else if (reader.ValueTextEquals("Generation"u8)) { reader.Read(); generation = reader.GetInt64(); }
                    else if (reader.ValueTextEquals("Sequence"u8)) { reader.Read(); sequence = reader.GetInt64(); }
                    else { reader.Read(); reader.Skip(); }
                }
                hasId = true;
            }
            else if (reader.ValueTextEquals("Node"u8)) { reader.Read(); writer = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Locations"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    while (reader.Read() && reader.TokenType == JsonTokenType.String) locations.Add(reader.GetString()!);
            }
            else if (reader.ValueTextEquals("LocationHolders"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string location = reader.GetString()!;
                        reader.Read();
                        holders.Add((location, reader.GetString() ?? ""));
                    }
            }
            else { reader.Read(); reader.Skip(); }
        }
        if (!hasId) throw new InvalidDataException($"Archived save {key} has no SaveId");
        return new SaveId(partition, generation, sequence);
    }

    /// <summary>The host whose store holds this location of the save: a host holder at exactly that host's store path for
    /// the save. Null for anything else - a node id's location (a node data dir), or a path outside the store.</summary>
    private string? StoreFileHost(SaveId id, string location, string holder)
    {
        if (HostOfHolder(holder) is not { } host) return null;
        try { return location == HostStorePath(RunRoot, host, id) ? host : null; }
        catch (InvalidDataException) { return null; }   // not one path component: not a store this registry names
    }

    /// <summary>PendingDeletes, under _lock: the archived files <paramref name="node"/> holds itself (T-0412), and the
    /// store files of <paramref name="hostHolder"/> (the host whose delete turn it has), handed out like a live save's
    /// pending deletes.</summary>
    private IEnumerable<DeleteDirective> ArchivedDeletesFor(string node, string? hostHolder)
    {
        if (_archiveFilesOutstanding == 0 || ArchiveFileDays() <= 0) yield break;
        DateTimeOffset now = _clock();
        foreach (ArchiveSweep sweep in _archiveSweeps.Values)
            foreach (KeyValuePair<(string Key, string Location), PendingArchiveFile> file in sweep.Files)
                if (file.Value.Holder == node || hostHolder != null && file.Value.Holder == hostHolder)
                {
                    file.Value.HandedOutAt ??= now;
                    yield return new DeleteDirective(file.Value.Id, node, file.Key.Location);
                }
    }

    /// <summary>
    /// AcknowledgeDelete of a save that is not in memory, under _lock: an archived store file retention handed out. The
    /// archived row is rewritten in place - the location leaves Locations and its holder, and is recorded in
    /// DeletedLocations with the time - and KEPT; the partition is not brought back. False when no such directive is
    /// outstanding (e.g. after a restart: the location is queued again and the next ack lands).
    /// </summary>
    private bool AcknowledgeArchivedDelete(SaveId id, string location)
    {
        string key = Key(id);
        if (!_archiveSweeps.TryGetValue(id.Partition, out ArchiveSweep? sweep) || !sweep.Files.Remove((key, location))) return false;
        _archiveFilesOutstanding--;
        RecordArchivedDeletion(id, key, location, counted: true);
        if (sweep.Files.Count == 0) _archiveSweeps.Remove(id.Partition);
        FinishArchiveSweep(id.Partition, sweep);
        Persist();
        return true;
    }

    /// <summary>Rewrites the archived row in place: the location leaves Locations and its holder, and is recorded in
    /// DeletedLocations with the time. <paramref name="counted"/>: an acknowledged deletion (status deletedLocations).</summary>
    private void RecordArchivedDeletion(SaveId id, string key, string location, bool counted)
    {
        if (_store.ReadArchived(id.Partition, key) is not { } stored) return;
        SaveRegistration save = JsonSerializer.Deserialize<SaveRegistration>(stored.Value) ?? throw new InvalidDataException($"Archived save {key} is empty");
        foreach (string held in save.Locations) save.LocationHolders.TryAdd(held, save.Node);
        if (!save.Locations.Remove(location)) return;
        save.LocationHolders.Remove(location);
        (save.DeletedLocations ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal))[location] = _clock();
        // Located stays as archived: the summary is lineage durability (collapse), which the content downstream keeps;
        // the deletion is in the row.
        _store.Rearchive(id.Partition, key, stored.Generation, stored.Located, save, typeof(SaveRegistration));
        if (counted) _state.ArchiveFilesDeleted++;
    }

    /// <summary>A sweep whose last file left the queue marks its partition swept, unless a file was left out (Complete).</summary>
    private void FinishArchiveSweep(ulong partition, ArchiveSweep sweep)
    {
        if (sweep.Files.Count != 0 || !sweep.Complete) return;
        _store.MarkSwept(partition, sweep.ArchivedMs, sweep.Legacy);
        _archiveRetentionLegacy += sweep.Legacy;
    }

    /// <summary>
    /// Rehydrate, under _lock, before the rows come back: the partition leaves the sweep (its stamp leaves with the archive).
    /// Returns the locations a node was handed but has not acknowledged - their files may already be gone, so the caller
    /// records them as deleted rather than offer a file that may not exist.
    /// </summary>
    private HashSet<(string Key, string Location)>? ForgetArchiveSweep(ulong partition)
    {
        if (_store.ReadStamp(partition) is { Swept: true } stamp) _archiveRetentionLegacy -= stamp.Legacy;
        if (!_archiveSweeps.Remove(partition, out ArchiveSweep? sweep)) return null;
        _archiveFilesOutstanding -= sweep.Files.Count;
        HashSet<(string, string)> handed = [.. sweep.Files.Where(x => x.Value.HandedOut).Select(x => x.Key)];
        return handed.Count == 0 ? null : handed;
    }

    /// <summary>
    /// Rehydrate, per row coming back: a location handed out for deletion becomes a deleted one, and a row with deleted
    /// files says so in ONE named line - the recovery that needed it must not fall back to something older silently.
    /// </summary>
    private void NoteArchiveRetention(string key, SaveRegistration save, HashSet<(string Key, string Location)>? handed)
    {
        if (handed != null)
            foreach (string location in save.Locations.Where(location => handed.Contains((key, location))).ToArray())
            {
                save.Locations.Remove(location);
                save.LocationHolders.Remove(location);
                (save.DeletedLocations ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal))[location] = _clock();
                Console.WriteLine($"WARNING: registry: archived save {key}: {location} was handed out for deletion (archive retention) and not "
                    + "acknowledged when its partition came back; recorded as deleted");
            }
        if (save.DeletedLocations is not { Count: > 0 } deleted) return;
        string at = deleted.Values.Max().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Console.WriteLine(save.Locations.Count == 0
            ? $"registry: archived save {key} files deleted at {at} (archive retention) - not recoverable"
            : $"registry: archived save {key} files deleted at {at} (archive retention) from {deleted.Count} location(s) - "
                + $"recoverable only from its {save.Locations.Count} other location(s)");
    }

    /// <summary>Registry status fields (next to archivedPartitions). Caller holds _lock.</summary>
    private object ArchiveRetentionStatus() => new
    {
        days = ArchiveFileDays(),
        eligiblePartitions = _archiveRetentionDue,
        outstandingDirectives = _archiveFilesOutstanding,
        sweepingPartitions = _archiveSweeps.Count,
        deletedLocations = _state.ArchiveFilesDeleted,
        legacyHolderLocations = _archiveRetentionLegacy,
        unackedRecordedDeleted = _archiveRetentionUnacked,
        skippedPartitions = _archiveRetentionSkipped,
        lastPassRows = _archiveRetentionLastRows,
        lastPassMs = Math.Round(_archiveRetentionLastMs, 2),
        firstSightStamped = _archiveRetentionLegacyStamped,
    };

    /// <summary>The due count for status, read BEFORE PlanRetention takes the lock, on a read-only connection.</summary>
    private long ArchiveRetentionDueCount()
    {
        int days;
        lock (_lock) days = ArchiveFileDays();
        if (days <= 0) return 0;
        try { return _store.CountDueReadOnly((_clock() - TimeSpan.FromDays(days)).ToUnixTimeMilliseconds()); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return -1; }
    }

    // ---- Self-test access.
    internal object ArchiveRetentionStatusForTest() { lock (_lock) return ArchiveRetentionStatus(); }
    internal Dictionary<ulong, RegistryStore.ArchiveStamp> ArchiveStampsForTest() { lock (_lock) return _store.StampsFresh(); }
    internal RegistryStore.ArchiveStamp? ArchiveStampForTest(ulong partition) { lock (_lock) return _store.ReadStamp(partition); }
    internal int ArchiveFilesOutstanding { get { lock (_lock) return _archiveFilesOutstanding; } }
    internal double ArchiveRetentionLastMs { get { lock (_lock) return _archiveRetentionLastMs; } }
    internal int ArchiveRetentionLastRows { get { lock (_lock) return _archiveRetentionLastRows; } }

    /// <summary>Self-test: the sweep's slim read of a row.</summary>
    internal static (SaveId Id, string Writer, List<string> Locations, List<(string, string)> Holders) ScanArchivedRowForTest(byte[] json)
    {
        List<string> locations = [];
        List<(string, string)> holders = [];
        SaveId id = ScanArchivedRow(json, "test", locations, holders, out string writer);
        return (id, writer, locations, holders);
    }

    /// <summary>Self-test: one archived save row as stored (null when not archived).</summary>
    internal SaveRegistration? ArchivedSaveForTest(SaveId id)
    {
        lock (_lock)
            return _store.ReadArchived(id.Partition, Key(id)) is { } row ? JsonSerializer.Deserialize<SaveRegistration>(row.Value) : null;
    }

    /// <summary>Self-test: puts an archived partition's ownership row in a state no API reaches without bringing it back.</summary>
    internal void TestSetOwnerRow(ulong partition, string? node, bool recovering)
    {
        lock (_lock)
        {
            if (!_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner))
                throw new InvalidOperationException($"partition {partition} has no row");
            owner.Node = node;
            owner.Recovering = recovering;
            Touch(owner);
            Persist();
        }
    }
}
