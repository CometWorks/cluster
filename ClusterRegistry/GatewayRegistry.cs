using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class GatewayRegistry : IDisposable
{
    internal static readonly TimeSpan HandoverFinalizationLease = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SimulationStallDrain = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SimulationStallKill = TimeSpan.FromSeconds(60);
    // The stall budget while a node reports recovery loads in flight - loading a partition freezes
    // its game thread legitimately. Drain must stay well above the largest single-partition load;
    // kill is the hard cap that still reaps a genuinely hung loader.
    private static readonly TimeSpan SimulationStallRecoveryDrain = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SimulationStallRecoveryKill = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExecutorSilence = TimeSpan.FromSeconds(60);
    // Five of the World Authority's one-minute periodic saves.
    private static readonly TimeSpan GlobalSaveStale = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SpawnFailureWindow = TimeSpan.FromMinutes(10);
    private const uint Magic = 0x31524743; // CGR1 - raw JSON payload (pre-2026-08-19)
    /// <summary>
    /// CGR2: the payload is DEFLATE-compressed JSON. The registry is highly repetitive JSON -
    /// w18's 16.24 MB final state compresses to 1.22 MB, 13.4x - and it was being written in
    /// full on every mutation, so compressing it is the single largest reduction available on
    /// the write path (Docs/StateDurability.md). Both magics are accepted on read, so a
    /// registry written by an older build still loads.
    /// </summary>
    private const uint MagicCompressed = 0x32524743; // CGR2
    private const int HeaderSize = 12;

    /// <summary>
    /// Serialises the state STRAIGHT INTO the compressor and returns only the compressed bytes.
    ///
    /// Streaming rather than serialise-then-compress is deliberate (Viktor, 2026-08-19): the old
    /// order built the entire 16 MB JSON document as one byte[] on the large object heap, handed
    /// it to the compressor, and produced a second buffer - two LOH allocations per flush and a
    /// 16 MB working set that no CPU cache can hold. Utf8JsonWriter instead fills a small
    /// internal buffer (16 KB, the framework default - the same order as the ~4 KB Viktor asked
    /// for, and comfortably inside L1) and flushes it into the DeflateStream as it goes, so the
    /// hot loop stays in cache and only the ~1.2 MB compressed result is ever materialised.
    ///
    /// It cannot stream all the way to the file: the record header carries the length and CRC of
    /// the stored bytes, both of which are only known once compression has finished. Buffering
    /// 1.2 MB to learn them is the cheap half of the old cost.
    ///
    /// Optimal is .NET's nearest equivalent to `gzip -6`, which is what Viktor asked for
    /// ("relatively quick but still reasonably effective, similar to gzip -5"). Measured on
    /// w18's 16.24 MB final registry: Fastest gives 1.68 MB in 38 ms, Optimal 1.22 MB in 107 ms,
    /// SmallestSize spends seconds for a few more percent. At one flush a minute the extra 70 ms
    /// is free, and the smaller file is written, fsynced and read back on every boot.
    /// </summary>
    private byte[] SerializeCompressed()
    {
        using var buffer = new MemoryStream(1 << 20);
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new Utf8JsonWriter(deflate, new JsonWriterOptions { SkipValidation = true }))
            JsonSerializer.Serialize(writer, _state);
        return buffer.ToArray();
    }

    private static byte[] Decompress(byte[] stored)
    {
        using var input = new MemoryStream(stored);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(stored.Length * 8);
        deflate.CopyTo(output);
        return output.ToArray();
    }
    // Every WAL record is a FULL state snapshot, so everything before the last record is dead
    // weight; once the log outgrows this cap the snapshot supersedes it and the WAL restarts
    // empty. Overridable from the constructor so the self-test can trigger the roll-over with
    // a tiny cap.
    private readonly long _checkpointWalBytes;
    private readonly object _lock = new();
    /// <summary>T-0251: when each partition last came online on a node (an offline load); the balancer
    /// leaves a fresh arrival where it landed for CLUSTER_BALANCE_RESIDENCE_SECONDS. Not persisted: a
    /// restart starts everyone fresh.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _arrivals = new();

    /// <summary>T-0251: record that <paramref name="partition"/> just arrived on a node.</summary>
    public void NoteArrival(ulong partition)
    {
        DateTime now = DateTime.UtcNow;
        _arrivals[partition] = now;
        if (_arrivals.Count > 8192)
            foreach (var old in _arrivals.Where(x => now - x.Value > TimeSpan.FromHours(1)).ToArray())
                _arrivals.TryRemove(old.Key, out _);
    }

    /// <summary>T-0251: when the partition last arrived on a node, if recorded.</summary>
    public DateTime? ArrivedAt(ulong partition) => _arrivals.TryGetValue(partition, out DateTime at) ? at : null;

    /// <summary>T-0251: a split hands its input's arrival to every output (#1304: merges and transfers no longer call it). A split gives
    /// every child a fresh id, so without this a pilot spawning at the Earth base and flying off (a
    /// [216,1] split, 31 on node-1 in 10 min on 2026-09-26) re-ID'd the base and released its residence:
    /// the balancer could move it right after it landed.</summary>
    public void CarryArrival(IEnumerable<ulong> inputs, IEnumerable<ulong> outputs)
    {
        DateTime? latest = inputs.Select(ArrivedAt).Where(x => x.HasValue).DefaultIfEmpty(null).Max();
        if (latest is not DateTime at) return;
        foreach (ulong output in outputs)
            _arrivals.AddOrUpdate(output, at, (_, old) => old > at ? old : at);
    }

    // Lock-occupancy sampler (w19, 2026-08-20). Handover p50 tripled while the
    // workload did not: partition size flat, node PCU flat, handover volume
    // flat, handover concurrency 0.0-0.3, node RPCs flat at 24 ms, hosts idle.
    // What DID change is that no registry call - not even a 4 KB one - could be
    // served in under ~250 ms, over 24 probes. That is a FLOOR, not a median, so
    // the lock is occupied near-continuously; but an external probe cannot say
    // by whom or for how long at a stretch.
    //
    // This measures it from inside, without touching a single lock site: a
    // background thread polls Monitor.TryEnter(0) and reports the fraction of
    // samples that found the lock already held, plus the longest unbroken run of
    // held samples (a lower bound on one hold). Costs one uncontended TryEnter
    // per interval and never blocks - TryEnter(0) returns immediately - so it
    // cannot itself become the contention it is measuring.
    //
    // Off unless CLUSTER_LOCK_SAMPLE_MS is set, so it ships dark and is armed
    // deliberately.
    private void StartLockSampler()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_LOCK_SAMPLE_MS"),
                out int periodMs) || periodMs <= 0)
            return;
        Thread sampler = new(() =>
        {
            int held = 0, total = 0, run = 0, longestRun = 0;
            DateTimeOffset nextReport = _clock().AddMinutes(1);
            while (true)
            {
                bool taken = false;
                try
                {
                    Monitor.TryEnter(_lock, 0, ref taken);
                }
                finally
                {
                    if (taken) Monitor.Exit(_lock);
                }
                total++;
                if (taken) { run = 0; }
                else { held++; run++; if (run > longestRun) longestRun = run; }
                if (_clock() >= nextReport)
                {
                    Console.WriteLine($"registry lock: held on {held}/{total} samples "
                        + $"({(total == 0 ? 0 : 100.0 * held / total):F1}%), longest unbroken "
                        + $"run {longestRun} samples (>= {longestRun * periodMs} ms), "
                        + $"sample period {periodMs} ms");
                    held = total = run = longestRun = 0;
                    nextReport = _clock().AddMinutes(1);
                }
                Thread.Sleep(periodMs);
            }
        })
        { IsBackground = true, Name = "registry-lock-sampler" };
        sampler.Start();
    }

    private readonly string _snapshotPath;
    private readonly string _walPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _leaseTtl;
    private readonly NodeAdmission _admission;
    // T-0227: the durable store (registry.db). registry.json / registry.wal are read only to migrate.
    private readonly RegistryStore _store;
    // Lease renewals no longer spend a WAL record each, so recovered lease timestamps are stale
    // by design. Consumers that read leases as a SAFETY signal (voxel truncation watermarks,
    // client routing) correctly see them as expired after a restart; only the FENCING sweep must
    // wait one TTL from boot so every live node gets to renew before it can be declared dead.
    private readonly DateTimeOffset _expiryHoldUntil;
    // SE-892: the global save of the clean stop this process consumed at a warm start. Not persisted on purpose.
    private GlobalSaveId? _startedFromGlobal;
    private readonly Dictionary<string, TransferGrant> _grants = [];
    private RegistryState _state;
    private long _records;
    private bool _dirty;
    private Exception? _persistenceFailure;

    internal bool PersistenceFailed => Volatile.Read(ref _persistenceFailure) is not null;

    internal void EnsurePersistenceHealthy()
    {
        if (_persistenceFailure is not null)
            throw new IOException("Registry persistence failed; restart and recover before serving requests.", _persistenceFailure);
    }
    private DateTimeOffset _flushDue = DateTimeOffset.MaxValue;

    /// <summary>
    /// How long a mutation may sit in memory before it reaches the disk
    /// (Docs/StateDurability.md, Viktor 2026-08-19). Every mutation used to serialise the
    /// WHOLE registry and fsync it, inside the global lock: 99 MB/s and ~8.5 TB/day on w18,
    /// climbing with state size, and blocking every handover behind each fsync. Losing up to
    /// this much state on an unclean stop is an accepted trade; the explicit commit points
    /// (clean shutdown, operator checkpoint) still flush immediately.
    /// </summary>
    public static readonly TimeSpan PersistInterval =
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_PERSIST_INTERVAL_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0 ? seconds : 60);

    /// <summary>
    /// Saves grouped by partition. Newest(), NewestForRecovery(), NewestCatalogued() and the
    /// save-sequence fence all used to scan EVERY catalogued save on every call, and all four sit
    /// on the handover, recovery and periodic-save paths under the global registry lock. Three
    /// hours of w18 catalogued 8361 saves for 1451 partitions, so each of those lookups walked
    /// 8361 registrations to find the ~6 that belonged to the partition asked about - the cost of
    /// a handover grew with the age of the run, which is the O(N^2) the latency hunt was after.
    /// Derived state: deliberately NOT part of RegistryState (serialising it would double the
    /// snapshot), rebuilt from _state.Saves in NormalizeState(), which every load path - fresh
    /// snapshot and WAL replay alike - passes through before the registry serves anything.
    /// </summary>
    private readonly Dictionary<ulong, List<SaveRegistration>> _savesByPartition = [];

    private IReadOnlyList<SaveRegistration> SavesOf(ulong partition)
    {
        if (_savesByPartition.TryGetValue(partition, out List<SaveRegistration>? saves)) return saves;
        if (_archivedPartitions.ContainsKey(partition) && Rehydrate(partition) && _savesByPartition.TryGetValue(partition, out saves))
            return saves;
        return [];
    }

    /// <summary>The saves held in memory, never reading the archive: for the replica queue (archived
    /// saves are not replicated) and for walks that must not pull a whole partition back in.</summary>
    private IReadOnlyList<SaveRegistration> InMemorySavesOf(ulong partition) =>
        _savesByPartition.TryGetValue(partition, out List<SaveRegistration>? saves) ? saves : [];

    /// <summary>Whether the partition catalogues any save, in memory or archived, without loading the archive.</summary>
    private bool HasCatalogued(ulong partition) => _savesByPartition.ContainsKey(partition) || _archivedPartitions.ContainsKey(partition);

    /// <summary>
    /// Heap shrink (2026-09-24). A retired partition's saves stay catalogued as a safety margin
    /// (PlanRetention's keepRetired), and on w32 those were 194,152 of 194,814 saves and 509 MB of
    /// an 816 MB registry heap, loaded and walked for nothing. Once no live partition's cover and no
    /// snapshot references any of them, PlanRetention moves the whole partition's saves to the
    /// ArchivedSaves table (the same transaction that removes them from Saves) and forgets them in
    /// memory. What stays in memory is this summary: partition -> newest generation with a location
    /// (-1 when none), which is all OutputIsDurable and "does it have saves" need.
    ///
    /// Invariant: a partition's saves are either all in memory or all archived. Any lookup that
    /// needs an archived save (SavesOf, TryGetSave, PutSave) brings the WHOLE partition back first
    /// (<see cref="Rehydrate"/>). Archived saves are never replicated; their rows are never deleted, and their FILES only
    /// by archive retention (T-0400, ArchiveRetention.cs) once the partition has been archived long enough.
    /// </summary>
    private readonly Dictionary<ulong, long> _archivedPartitions = [];
    internal long ArchivedPartitionsTotal, RehydratedPartitions, ArchiveMilliseconds;
    private static readonly int ArchiveBatch = EnvInt("CLUSTER_ARCHIVE_BATCH", 5000);
    /// <summary>Set by the self-test and by PlanRetention's retired-partition walk: SavesOf answers from memory only.</summary>
    private bool _archiveReadOnly;

    /// <summary>Brings every archived save of <paramref name="partition"/> back into memory. Caller holds _lock.</summary>
    private bool Rehydrate(ulong partition)
    {
        if (_archiveReadOnly || !_archivedPartitions.Remove(partition)) return false;
        // T-0400: out of the archive retention sweep; a file handed out for deletion is no longer offered.
        HashSet<(string Key, string Location)>? handedOut = ForgetArchiveSweep(partition);
        List<(string Key, byte[] Value)> rows = _store.Unarchive(partition);
        int restored = 0;
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, byte[] value) in rows)
        {
            SaveRegistration save = JsonSerializer.Deserialize<SaveRegistration>(value)
                ?? throw new InvalidDataException($"Archived save {key} is empty");
            NoteArchiveRetention(key, save, handedOut);
            NodeIdsOf(save, named);
            foreach (string location in save.Locations)
                save.LocationHolders.TryAdd(location, save.Node);
            // T-0340: the load does not rewrite archived rows; a legacy holder is re-keyed here, through the alias map.
            if (_hostStore) RekeyToHostStore(save);
            // Endpoints recorded while the save was archived were never written to it.
            foreach (string holder in save.LocationHolders.Values.Distinct(StringComparer.Ordinal).ToArray())
                if (_state.Nodes.TryGetValue(holder, out NodeRegistration? node) && !string.IsNullOrWhiteSpace(node.ControlEndpoint))
                    save.HolderEndpoints[holder] = node.ControlEndpoint;
            if (_state.Saves.ContainsKey(key)) continue;
            PutSave(key, save);
            restored++;
        }
        RehydratedPartitions++;
        // T-0323: an archived reference to a gone node may have come back into memory; let the release pass re-check the
        // ids these rows name. T-0296: ONLY those - the rest are still named by rows that stay archived, and clearing the
        // whole cache made the next pass rescan every archive-named gone id (~0.3 s each on w33).
        UncacheArchiveReferences(named);
        Console.WriteLine($"registry: partition {partition}: {restored} archived save(s) brought back into memory");
        _dirty = true;
        return true;
    }

    /// <summary>A save by key, bringing its partition back from the archive when it is there.</summary>
    private bool TryGetSave(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out SaveRegistration save)
    {
        if (_state.Saves.TryGetValue(key, out save)) return true;
        int colon = key.IndexOf(':');
        if (colon > 0 && ulong.TryParse(key.AsSpan(0, colon), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong partition)
            && _archivedPartitions.ContainsKey(partition) && Rehydrate(partition))
            return _state.Saves.TryGetValue(key, out save);
        return false;
    }

    /// <summary>
    /// Moves up to <see cref="ArchiveBatch"/> retired partitions' saves to the archive: partitions
    /// none of whose saves is in <paramref name="needed"/> (a live partition's cover or a snapshot
    /// reference), with no delete still pending and no owner. Caller holds _lock and persists.
    /// </summary>
    private int ArchiveRetired(HashSet<SaveId> needed)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<ulong> eligible = [];
        foreach ((ulong partition, List<SaveRegistration> saves) in _savesByPartition)
        {
            if (eligible.Count >= ArchiveBatch) break;
            if (!IsRetired(partition)) continue;
            if (_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner) && (owner.Node != null || owner.Recovering)) continue;
            if (saves.Any(save => save.PendingDelete || needed.Contains(save.SaveId)
                    || _replicaReservations.ContainsKey(save.SaveId))) continue;
            eligible.Add(partition);
        }
        int archived = 0;
        foreach (ulong partition in eligible)
        {
            long located = -1;
            foreach (SaveRegistration save in _savesByPartition[partition].ToArray())
            {
                string key = Key(save.SaveId);
                bool hasLocation = save.Locations.Count != 0;
                if (hasLocation) located = Math.Max(located, save.SaveId.Generation);
                _store.Archive(partition, key, save.SaveId.Generation, hasLocation, save, typeof(SaveRegistration));
                RemoveSave(key);
                archived++;
            }
            _archivedPartitions[partition] = located;
            // T-0400: archive retention counts its age from here; same write as the move.
            _store.Stamp(partition, _clock().ToUnixTimeMilliseconds());
            ArchivedPartitionsTotal++;
        }
        ArchiveMilliseconds += clock.ElapsedMilliseconds;
        if (eligible.Count != 0)
            Console.WriteLine($"registry: archived {archived} save(s) of {eligible.Count} retired partition(s) in {clock.ElapsedMilliseconds} ms"
                + $" ({_archivedPartitions.Count} partitions archived, {_state.Saves.Count} saves in memory)");
        return archived;
    }

    /// <summary>
    /// Handovers by partition, and by the node incarnation they target. Both keys are fixed when the
    /// record is created and the record is mutated in place afterwards, so these indexes only ever
    /// need maintaining at the single write site.
    /// </summary>
    /// <remarks>
    /// <c>_state.Handovers</c> is never pruned - aborted, committed and reconciled records accumulate
    /// for the life of the registry - so every linear scan of it cost more the longer the cluster had
    /// been up. Two of those scans were on the hot path: Begin() asked "does this partition already
    /// have an incomplete handover", which is the FIRST registry mutation of every transfer and so was
    /// O(handovers-so-far) per handover, i.e. quadratic over a run; and the 1 Hz heartbeat asked
    /// whether this node incarnation had a committed handover still awaiting finalization, per node,
    /// per second, under the registry lock. Same shape the per-partition save index was introduced to
    /// kill (see <see cref="_savesByPartition"/>).
    /// </remarks>
    private readonly Dictionary<ulong, List<HandoverRecord>> _handoversByPartition = [];
    private readonly Dictionary<(string Node, long Epoch), List<HandoverRecord>> _handoversByTarget = [];

    private void IndexHandover(HandoverRecord handover)
    {
        if (!_handoversByPartition.TryGetValue(handover.Partition, out List<HandoverRecord>? byPartition))
            _handoversByPartition[handover.Partition] = byPartition = [];
        byPartition.Add(handover);
        var target = (handover.TargetNode, handover.TargetEpoch);
        if (!_handoversByTarget.TryGetValue(target, out List<HandoverRecord>? byTarget))
            _handoversByTarget[target] = byTarget = [];
        byTarget.Add(handover);
    }

    private IReadOnlyList<HandoverRecord> HandoversOf(ulong partition) =>
        _handoversByPartition.TryGetValue(partition, out List<HandoverRecord>? handovers) ? handovers : [];

    private IReadOnlyList<HandoverRecord> HandoversTargeting(string node, long epoch) =>
        _handoversByTarget.TryGetValue((node, epoch), out List<HandoverRecord>? handovers) ? handovers : [];

    /// <summary>
    /// Whether one recovery candidate beats another: a connected client in it wins, then a character
    /// that calls it home, then the fresher catalogue entry, then the lower partition id.
    /// </summary>
    /// <remarks>
    /// This is the ordering the OrderByDescending(client).ThenByDescending(home)
    /// .ThenByDescending(catalogued).ThenBy(id) chain expressed, written out so it can be applied in
    /// a single pass rather than by sorting the whole candidate set to take one element. Written as
    /// statements on purpose: the first attempt folded it into a conditional expression and C#'s
    /// precedence bound the leading `||` into the condition of the first `?:`, which silently skipped
    /// the first candidate whenever it had no client.
    /// </remarks>
    internal static bool OutranksForRecovery((bool Client, bool Home, long Catalogued) candidate, ulong candidateId,
        (bool Client, bool Home, long Catalogued) best, ulong bestId)
    {
        if (candidate.Client != best.Client) return candidate.Client;
        if (candidate.Home != best.Home) return candidate.Home;
        if (candidate.Catalogued != best.Catalogued) return candidate.Catalogued > best.Catalogued;
        return candidateId < bestId;
    }

    /// <summary>Lineage records by operation id, for the idempotency check both lineage paths do.</summary>
    /// <remarks>
    /// The check was a linear scan of the lineage list on every split and merge - and the list only
    /// grows (w19: 2253 records in four hours) - so retrying an op cost more the longer the run had
    /// been going. Under the registry lock, like everything else here.
    /// </remarks>
    private readonly Dictionary<string, LineageRecord> _lineageByOpId = [];

    /// <summary>
    /// Whether two member maps are the same, compared structurally.
    /// </summary>
    /// <remarks>
    /// This used to be <c>JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b)</c>: it built two
    /// JSON strings of every member entity id in the operation purely to answer a yes/no, on a retry
    /// path, with the registry lock held. It also made the answer depend on key ORDER, which a
    /// dictionary does not promise - two equal maps that enumerated differently would have been
    /// reported as a conflicting re-record.
    /// </remarks>
    private static bool SameMembers(Dictionary<ulong, long[]> left, Dictionary<ulong, long[]> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        foreach (KeyValuePair<ulong, long[]> entry in left)
            if (!right.TryGetValue(entry.Key, out long[]? other) || !entry.Value.SequenceEqual(other)) return false;
        return true;
    }

    private void IndexSave(SaveRegistration save)
    {
        if (!_savesByPartition.TryGetValue(save.SaveId.Partition, out List<SaveRegistration>? saves))
            _savesByPartition[save.SaveId.Partition] = saves = [];
        saves.Add(save);
    }

    /// <summary>Catalogues a save and keeps <see cref="_savesByPartition"/> in step. Every write
    /// to _state.Saves goes through here so the index cannot drift from the state it indexes.</summary>
    private void PutSave(string key, SaveRegistration save)
    {
        if (_archivedPartitions.ContainsKey(save.SaveId.Partition))
            Rehydrate(save.SaveId.Partition);
        if (_state.Saves.TryGetValue(key, out SaveRegistration? replaced))
            DropFromIndex(replaced.SaveId);
        _state.Saves[key] = save;
        IndexSave(save);
        MarkReplicaDirty(save.SaveId.Partition);
        if (replaced != null) MarkReplicaDirty(replaced.SaveId.Partition);
    }

    private void RemoveSave(string key)
    {
        if (!_state.Saves.TryGetValue(key, out SaveRegistration? save)) return;
        _state.Saves.Remove(key);
        DropFromIndex(save.SaveId);
        MarkReplicaDirty(save.SaveId.Partition);
    }

    private void DropFromIndex(SaveId id)
    {
        if (!_savesByPartition.TryGetValue(id.Partition, out List<SaveRegistration>? saves)) return;
        saves.RemoveAll(x => x.SaveId == id);
        if (saves.Count == 0) _savesByPartition.Remove(id.Partition);
    }

    /// <summary>
    /// Optional ordered event sink (Orchestration.md §12): phase changes, node lifecycle
    /// transitions, and alarms as they commit. Set after construction by the Gateway; invoked
    /// inside the registry lock, so the sink must only append, never call back in.
    /// </summary>
    public Action<string, string?, IReadOnlyDictionary<string, string>?>? EventSink;

    /// <summary>Nodes an operator drain is emptying right now (the gateway's T-0235 drain sources). They stay
    /// Active until /close-node, so without this a directed offline load picked them: a drained node is by
    /// construction the least loaded one. Called under _lock; must not take registry locks.</summary>
    public Func<IReadOnlyCollection<string>>? DrainingNodes;

    /// <summary>#1454: nodes holding a heavy component (DirectGateway.HeavyMembers). A free offline load (no node named)
    /// goes elsewhere while another node can take it: a hub node holds few partitions and so read as the emptiest.
    /// Called under _lock; must not take registry locks.</summary>
    public Func<IReadOnlyCollection<string>>? HeavyNodes;

    private void Emit(string type, string? target = null, IReadOnlyDictionary<string, string>? data = null) =>
        EventSink?.Invoke(type, target, data);

    /// <summary>Every write to a partition row, in the event log.
    ///
    /// T-0207 spent four occurrences and two nights asking "what advanced partition N to generation 1
    /// before the only node that could own it had ever claimed it", and could not answer it: the event
    /// log records node lifecycle only, so a partition row could be created, re-owned and advanced
    /// without leaving one line anywhere. ClaimPartition is not even the only writer - a lease expiry,
    /// a release, a handover commit or abort, a topology merge and a registry rebuild all move a
    /// generation, and three of them leave the row UNOWNED at the new generation, which is exactly the
    /// state the failures are found in. A snapshot answers "what is the state"; only this answers
    /// "how did it get there", and a rare race never needs the first one.</summary>
    private void EmitPartition(string what, ulong partition, string? node, long epoch, long generation, string reason) =>
        Emit("partition." + what, partition.ToString(), new Dictionary<string, string>
        {
            ["node"] = node ?? "",
            ["epoch"] = epoch.ToString(),
            ["generation"] = generation.ToString(),
            ["reason"] = reason,
        });

    public GatewayRegistry(string directory, NodeAdmission admission, Func<DateTimeOffset>? clock = null, TimeSpan? leaseTtl = null,
        long checkpointWalBytes = 16 << 20, ManagedDeployment? managedDeployment = null, string? gatewayHost = null)
    {
        _admission = admission;
        // T-0340: known before the load, whose re-key maps a holder without a host to the gateway host.
        GatewayHost = gatewayHost;
        RunRoot = Path.GetDirectoryName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)) ?? Path.GetFullPath(directory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _checkpointWalBytes = checkpointWalBytes;
        _leaseTtl = leaseTtl ?? TimeSpan.FromSeconds(10);
        if (_leaseTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTtl));
        Directory.CreateDirectory(directory);
        _snapshotPath = Path.Combine(directory, "registry.json");
        _walPath = Path.Combine(directory, "registry.wal");
        CheckProvenance(directory, admission);
        _store = new RegistryStore(directory, typeof(RegistryState));
        bool migrate = false;
        try
        {
            if (_store.HasState())
                _state = _store.Load<RegistryState>();
            else
            {
                // First start on this build over an old-format registry (or a fresh one): the legacy
                // snapshot + WAL are replayed exactly as before, then written to the store in full.
                _state = File.Exists(_snapshotPath) ? ReadSnapshot(_snapshotPath) : new RegistryState();
                RecoverWal();
                migrate = File.Exists(_snapshotPath) || File.Exists(_walPath);
            }
            // Before NormalizeState: the lineage index asks OutputIsDurable, which reads the archive summary.
            foreach ((ulong partition, long generation) in _store.ArchivedPartitions())
                _archivedPartitions[partition] = generation;
            NormalizeState();
            _store.Attach(_state);
            if (migrate) MigrateLegacyFiles();
            // The invariant (a partition all in memory or all archived) repaired, should a crash or an
            // older build ever have left saves on both sides: the archived ones join the live ones.
            foreach (ulong partition in _archivedPartitions.Keys.Where(_savesByPartition.ContainsKey).ToArray())
                Rehydrate(partition);
            // T-0400: every archived partition has an archive stamp from here on.
            StampArchiveAtLoad();
            RekeyCatalogueToHostStore();
        }
        catch { _store.Dispose(); throw; }
        if (managedDeployment is not null)
        {
            try { InitializeManagedDeployment(managedDeployment, directory); }
            catch { _store.Dispose(); throw; }
        }
        else if (_state.ManagedDeployment is not null)
        {
            _store.Dispose();
            throw new InvalidDataException("A managed Registry requires its deployment manifest and start generation.");
        }
        else
        {
            bool consumedCleanShutdown = _state.CleanShutdown != null;
            _state.Startup = IsWarmShutdown(_state.CleanShutdown) ? StartupKind.Warm : StartupKind.Recovery;
            _state.LastShutdown = _state.CleanShutdown;
            if (_state.Startup == StartupKind.Warm) _startedFromGlobal = _state.LastShutdown?.GlobalRef;
            _state.CleanShutdown = null;
            _state.ShutdownStarted = null;
            _state.ShutdownNodes.Clear();
            _state.Phase = ClusterPhase.Bootstrapping;
            if (consumedCleanShutdown) { Persist(); FlushState(); }
        }
        try { ReclaimNetworkIdBlocksAtColdStart(); }
        catch { _store.Dispose(); throw; }
        // Viktor 2026-09-23: at cluster start the replayed state is folded into the database file
        // and the log truncated, like a database checkpoint.
        _store.Checkpoint();
        // Only a boot that recovered registrations has stale timestamps to shield; a fresh
        // registry fences on the normal schedule from the first registration on.
        _expiryHoldUntil = _state.Nodes.Count != 0 ? _clock() + _leaseTtl : DateTimeOffset.MinValue;
        ArmLateSaveHold();
        StartLockSampler();
    }

    public bool AdmissionOpen
    {
        get { lock (_lock) return _persistenceFailure is null
            && (_state.ManagedDeployment is null
                ? _state.Phase is not (ClusterPhase.Draining or ClusterPhase.Down)
                : (_state.Phase is ClusterPhase.Serving or ClusterPhase.Degraded)
                    && _state.Nodes.Values.Any(n => n.Role == NodeRole.WA && n.Serving && ManagedNodeReady(n))
                    && _state.Nodes.Values.Any(n => n.Role == NodeRole.Regular && n.Serving && ManagedNodeReady(n))); }
    }

    internal sealed record ProvenanceStamp(string ClusterId, string WorldId, Guid Instance, Guid? PreviousInstance,
        bool PreviousShutdownClean, DateTimeOffset Created);

    /// <summary>
    /// Orchestration §5.1: the durable directory carries a provenance stamp - world identity, cluster
    /// id, and boot lineage. A start whose configured identity does not match the stamp is refused
    /// BEFORE anything else opens: fresh nodes against a foreign registry get fenced exactly as the
    /// epoch protocol intends, which is correct behavior for the wrong operation, and the observable
    /// result is a wedged half-dead cluster (the 2026-07-31 field incident). A refusal at launch costs
    /// seconds and names both sides. Same-identity reuse stays legal - that is a warm/recovery start.
    /// </summary>
    private void CheckProvenance(string directory, NodeAdmission admission)
    {
        string path = Path.Combine(directory, "provenance.json");
        ProvenanceStamp? previous = null;
        if (File.Exists(path))
        {
            try { previous = JsonSerializer.Deserialize<ProvenanceStamp>(File.ReadAllBytes(path)); }
            catch (JsonException) { /* an unreadable stamp cannot veto; the fresh stamp below replaces it */ }
            if (previous != null && (previous.ClusterId != admission.ClusterId || previous.WorldId != admission.WorldId))
                throw new InvalidOperationException(
                    $"Registry run root '{directory}' belongs to cluster '{previous.ClusterId}' world '{previous.WorldId}'"
                    + $" (stamped {previous.Created:u}), but this start is configured for cluster '{admission.ClusterId}'"
                    + $" world '{admission.WorldId}'. Refusing the launch before any node spawns. Either wipe the run root"
                    + " (fresh start) or start the recorded instance with its own world configuration (warm/recovery start).");
        }
        bool cleanShutdown = false;
        if (RegistryStore.Exists(directory))
        {
            // Boot lineage only - the authoritative warm/recovery decision still comes from the
            // recovered state's clean-shutdown marker after the load.
            try
            {
                byte[]? marker = RegistryStore.PeekState(directory, nameof(RegistryState.CleanShutdown));
                cleanShutdown = marker != null && JsonSerializer.Deserialize<ShutdownMarker?>(marker) != null;
            }
            catch (Exception) { }
        }
        else if (File.Exists(_snapshotPath) || File.Exists(_walPath))
        {
            try
            {
                RegistryState peek = File.Exists(_snapshotPath) ? ReadSnapshot(_snapshotPath) : new RegistryState();
                cleanShutdown = peek.CleanShutdown != null;
            }
            catch (Exception) { }
        }
        var stamp = new ProvenanceStamp(admission.ClusterId, admission.WorldId, Guid.NewGuid(), previous?.Instance,
            cleanShutdown, DateTimeOffset.UtcNow);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(stamp, new JsonSerializerOptions { WriteIndented = true }));
    }

    // TestPlan §1.4 "Registration rejection" injector: the registry ANSWERS this node's requests with
    // definitive 409-class refusals, deterministically, so P3-REC-01 needs no stale state to reproduce
    // the fenced/rejected condition. Armed only through the --direct-gated /debug/reject-node route.
    private readonly HashSet<string> _testRejectedNodes = new();

    public void SetTestRejection(string node, bool rejected)
    {
        lock (_lock)
        {
            if (rejected) _testRejectedNodes.Add(node);
            else _testRejectedNodes.Remove(node);
        }
        Console.WriteLine($"debug reject-node {(rejected ? "armed" : "cleared")} node={node}");
    }

    private void RequireNotTestRejected(string node)
    {
        if (_testRejectedNodes.Contains(node))
            throw new RegistryConflictException($"Node '{node}' registration is rejected (debug reject-node hook)");
    }

    public NodeRegistration RegisterNode(NodeJoinRequest request)
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            RequireNotTestRejected(request.Node);
            if (_state.ManagedDeployment is { } deployment
                && !deployment.Slots.Any(slot => slot.Node == request.Node
                    && slot.Endpoint == request.Endpoint && slot.Role == request.Role))
                throw new RegistryConflictException("Node is outside the managed deployment inventory.");
            if (_state.Phase is ClusterPhase.Draining or ClusterPhase.Down)
                throw new RegistryConflictException("Cluster admission is closed");
            _admission.Validate(request);
            RequireReachableFromGateway(request);
            string node = request.Node;
            string endpoint = request.Endpoint;
            Guid instance = request.Instance;
            if (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("Node registration requires node and endpoint");
            if (instance == Guid.Empty)
                throw new ArgumentException("Node registration requires a process instance GUID");
            if (_state.Nodes.TryGetValue(node, out NodeRegistration? existing) && existing.Instance == instance)
            {
                if (existing.Endpoint != endpoint)
                    throw new RegistryConflictException($"Node '{node}' process instance is already registered at '{existing.Endpoint}'");
                if (existing.Role != request.Role)
                    throw new RegistryConflictException($"Node '{node}' process instance is already registered as {existing.Role}");
                return existing;
            }
            if (request.Role == NodeRole.WA && WorldAuthorityLive())
                throw new RegistryBusyException($"World Authority is already owned by '{_state.WorldAuthority.Node}' until its lease lapses");
            NodeRecord? controlled = NodeControlOnRegister(request, existing);
            if (_state.ManagedDeployment is not null && existing is { Role: NodeRole.Regular, State: NodeState.Active or NodeState.Closed })
                ReplaceRestartedIncarnation(existing, _clock());
            long epoch = _state.Nodes.TryGetValue(node, out NodeRegistration? old) ? old.Epoch + 1 : 1;
            DateTimeOffset now = _clock();
            long authorityGeneration = 0;
            if (request.Role == NodeRole.WA)
            {
                authorityGeneration = ++_state.WorldAuthority.Generation;
                _state.WorldAuthority.Node = node;
                _state.WorldAuthority.Epoch = epoch;
                _state.WorldAuthority.LeaseExpires = now + _leaseTtl;
				_state.WorldAuthority.ControlEndpoint = request.ControlEndpoint;
            }
            var registration = new NodeRegistration(node, epoch, endpoint, now, NodeState.Active, now + _leaseTtl, instance,
                request.Role, authorityGeneration, request.ControlEndpoint)
            { Spare = _state.SpareNodes.Contains(node), Host = string.IsNullOrWhiteSpace(request.Host) ? controlled?.Host : request.Host, Pid = request.Pid };
            registration.Activated = registration.Spare ? null : now;
            if (controlled != null)
            {
                controlled.StartedAt = now;
                controlled.Pid = request.Pid ?? controlled.Pid;
            }
            _state.Nodes[node] = registration;
            registration.HostStore = NodeUsesHostStore(request);
            ForgetRetiredOnRegistration(node);
            if (!string.IsNullOrWhiteSpace(request.ControlEndpoint))
            {
                // A rebuilt catalog knows which node holds each scanned save but not how to reach it:
                // the scan reads headers off disk roots, where no control endpoint exists. The holder's
                // own registration is the first moment the registry can complete those entries; without
                // it a rebuilt save can never be offered as a cross-host replica source.
                foreach (SaveRegistration save in _state.Saves.Values)
                    if (save.LocationHolders.ContainsValue(node))
                        { save.HolderEndpoints[node] = request.ControlEndpoint; Touch(save); }
                foreach (GlobalSaveRegistration save in _state.GlobalSaves.Values)
                    if (save.LocationHolders.ContainsValue(node))
                        { save.HolderEndpoints[node] = request.ControlEndpoint; Touch(save); }
            }
            if (_state.Phase == ClusterPhase.Bootstrapping && _state.ManagedDeployment is null)
            {
                _state.Phase = ClusterPhase.Serving;
                Emit("cluster.phase", data: new Dictionary<string, string> { ["phase"] = nameof(ClusterPhase.Serving) });
            }
            Emit("node.registered", node, new Dictionary<string, string>
            {
                ["epoch"] = registration.Epoch.ToString(),
                ["role"] = registration.Role.ToString(),
            });
            Persist();
            return registration;
        }
    }

    public PartitionOwnership ClaimPartition(ulong partition, string node, long epoch, long? expectedGeneration)
    {
        lock (_lock)
        {
            RequireNotTestRejected(node);
            RequireRegularActiveNode(node, epoch);
            _state.Partitions.TryGetValue(partition, out PartitionOwnership? old);
            // T-0193: an offline partition comes back only through a directed recovery claim. A startup
            // claim naming one is a node's stale seed or template copy - the offline Final is the truth.
            if (_state.OfflinePartitions.ContainsKey(partition))
                throw new RegistryConflictException($"Partition {partition} is offline; it loads only through a directed recovery claim");
            // SE-835: no row with saves is a partition reaped as empty (ReapIfEmpty). A claim would start it again at
            // generation 1, below those saves: a node's stale world seed, and its saves would be the "superseded" ones.
            if (old == null && HasCatalogued(partition))
                throw new RegistryConflictException($"Partition {partition} was reaped as empty; its row does not come back from a seed");
            long generation = old?.Generation ?? 0;
            if (expectedGeneration.HasValue && expectedGeneration.Value != generation)
                throw new RegistryConflictException($"Partition {partition} is generation {generation}, not {expectedGeneration.Value}");
            if (old?.Node == node && old.Epoch == epoch)
                return old;
            // T-0296 r2: a partition whose remote owner is held by a network window (T-0321 P4) is still owned.
            if (old?.Node != null && _state.Nodes.TryGetValue(old.Node, out NodeRegistration? ownerNode) && ownerNode.Epoch == old.Epoch
                && ownerNode.State is NodeState.Active or NodeState.Closed && PartitionLeaseLive(old, ownerNode, _clock()))
                throw new RegistryConflictException($"Partition {partition} is owned by live node '{old.Node}'");
            var ownership = new PartitionOwnership(partition, node, epoch, generation + 1, _clock() + _leaseTtl);
            _state.Partitions[partition] = ownership;
            EmitPartition("claimed", partition, node, epoch, ownership.Generation,
                old is null ? "claim-created-row" : "claim");
            Persist();
            return ownership;
        }
    }

    /// <summary>
    /// T-0374: a Closed node makes local placeholder layouts around strays (ClusterProtocol.PlaceholderPartitionIds) with
    /// no registry row, and ClaimPartition refuses a Closed node, so the drain handover of a placeholder bearing a client
    /// was skipped until the force drain (r116b node-79, r124 node-98). The handover claims it for the node here: only a
    /// regular Closed node, only a row-less id of the placeholder range, which this registry never issues.
    /// </summary>
    public PartitionOwnership ClaimClosedNodePlaceholder(ulong partition, string node, long epoch)
    {
        lock (_lock)
        {
            RequireNotTestRejected(node);
            NodeRegistration current = RequireNode(node, epoch);
            if (current.Role != NodeRole.Regular || current.State != NodeState.Closed)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} is {current.State}: only a Closed regular node claims a placeholder");
            if (!ClusterProtocol.PlaceholderPartitionIds.Contains(partition))
                throw new RegistryConflictException($"Partition {partition} is not a placeholder id: a Closed node claims no other");
            if (_state.Partitions.ContainsKey(partition) || _state.OfflinePartitions.ContainsKey(partition))
                throw new RegistryConflictException($"Partition {partition} already has a registry row");
            var ownership = new PartitionOwnership(partition, node, epoch, 1, _clock() + _leaseTtl);
            _state.Partitions[partition] = ownership;
            EmitPartition("claimed", partition, node, epoch, ownership.Generation, "closed-node-placeholder");
            Persist();
            return ownership;
        }
    }

    /// <summary>Quarantine state as reported by live nodes' heartbeats - a poisoned partition must be
    /// findable at the registry, not only on the node that holds it.</summary>
    public object[] QuarantinedPartitions()
    {
        lock (_lock)
            return _state.Nodes.Values
                .Where(node => node.State is NodeState.Active or NodeState.Closed && node.Quarantined.Length > 0)
                .SelectMany(node => node.Quarantined.Select(partition => (object)new { node.Node, Partition = partition }))
                .ToArray();
    }

    public NodeRegistration Heartbeat(string node, long epoch, PartitionVersion[] ownerships, ClientPartitionReport[]? clientPartitions = null,
        long? simulationFrame = null, SaveFailureReport? saveFailure = null, long? recoveryCapacity = null, ulong[]? quarantined = null,
        long? projectedLoad = null, long? loadCapacity = null, long? recoveryLoadsPending = null, bool? serving = null,
        string? deploymentRevision = null, bool configurationVerified = false, string? configurationFailure = null,
        bool liveness = false)
    {
        lock (_lock)
        {
            RequireNotTestRejected(node);
            NodeRegistration current = RequireNode(node, epoch);
            if (current.State == NodeState.Dead)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} is dead");
            RecordManagedHeartbeat(current, deploymentRevision, configurationVerified, configurationFailure);
            OwnerAliveForLateSaveHold(current);
            // The registration heartbeat carries no ownership report (it posts an empty list every second next to the
            // persistence heartbeat): read as one, it wiped what the node simulates and the audit went blind.
            if (!LivenessIsNoReport || !liveness) RecordReportedOwnerships(node, epoch, ownerships);
            bool nowServing = (serving ?? true) && ManagedNodeReady(current);
            if (current.Serving != nowServing)
            {
                current.Serving = nowServing;
                Emit("node.serving", node, new Dictionary<string, string> { ["epoch"] = epoch.ToString(), ["serving"] = nowServing ? "true" : "false" });
            }
            // Most heartbeats renew leases and echo telemetry without changing anything durable.
            // Those must not spend a WAL record each (a full state snapshot per second per node is
            // how an idle cluster wrote a 32 GB WAL); only material state changes persist, and the
            // volatile fields (leases, simulation progress, executor freshness) are re-established
            // by NormalizeState and the 1 Hz heartbeats after a restart.
            bool dirty = false;
            if (quarantined != null && !quarantined.SequenceEqual(current.Quarantined))
            {
                current.Quarantined = quarantined;
                dirty = true;
            }
            DateTimeOffset now = _clock();
            if (recoveryCapacity.HasValue && current.RecoveryCapacity != recoveryCapacity.Value)
            {
                current.RecoveryCapacity = recoveryCapacity.Value;
                dirty = true;
            }
            if (projectedLoad.HasValue && current.ProjectedLoad != projectedLoad.Value)
            {
                current.ProjectedLoad = projectedLoad.Value;
                dirty = true;
            }
            if (loadCapacity.HasValue && current.LoadCapacity != loadCapacity.Value)
            {
                current.LoadCapacity = loadCapacity.Value;
                dirty = true;
            }
            if (saveFailure != null)
            {
                current.LastSaveFailure = saveFailure;
                current.LastSaveFailureAt = now;
                dirty = true;
                if (current.State == NodeState.Active)
                {
                    current.State = NodeState.Closed;
                    current.ForceDrainAt = now + TimeSpan.FromMinutes(15);
                }
            }
            if (recoveryLoadsPending.HasValue) current.RecoveryLoadsPending = recoveryLoadsPending.Value;
            if (simulationFrame >= 0)
            {
                ForgiveFrozenSilence(current, now);
                if (current.SimulationFrame != simulationFrame.Value)
                {
                    current.SimulationFrame = simulationFrame.Value;
                    current.SimulationProgress = now;
                }
                // A node loading recovery claims holds its game thread inside the import for however
                // long the content takes - frames legitimately freeze for minutes on a station-sized
                // partition. Fencing it as stalled re-offers everything it just claimed and killed
                // node-3 on every boot (2026-08-11). While the node reports recovery loads in flight
                // it gets the long budget; the hard cap still catches a loader that is truly hung.
                else if (current.State == NodeState.Active && now - current.SimulationProgress >=
                    (current.RecoveryLoadsPending > 0 ? SimulationStallRecoveryDrain : SimulationStallDrain))
                {
                    current.State = NodeState.Closed;
                    current.ForceDrainAt = current.SimulationProgress + SimulationStallKill;
                    dirty = true;
                }
            }
            DateTimeOffset expires = now + _leaseTtl;
            if (current.Role == NodeRole.WA)
            {
                WorldAuthority authority = RequireWorldAuthority(node, epoch, current.AuthorityGeneration);
                authority.LeaseExpires = expires;
            }
            bool finalizing = HandoversTargeting(node, epoch).Any(handover => handover.State == HandoverState.Committed
                && _state.Partitions.TryGetValue(handover.Partition, out PartitionOwnership? owner)
                && owner.Generation == handover.CommittedGeneration && owner.LeaseExpires > expires
                && !ownerships.Any(x => x.Partition == owner.Partition && x.Generation == owner.Generation));
            if (!finalizing || current.LeaseExpires < expires)
                current.LeaseExpires = expires;
            foreach (PartitionVersion version in ownerships)
                if (_state.Partitions.TryGetValue(version.Partition, out PartitionOwnership? owner) && owner.Node == node && owner.Epoch == epoch && owner.Generation == version.Generation)
                {
                    owner.LeaseExpires = expires;
                    Touch(owner);
                    if (owner.Recovering)
                    {
                        owner.Recovering = false;
                        dirty = true;
                    }
                }
            foreach (ClientPartitionReport route in clientPartitions ?? [])
                if (route.Partition != 0 && _state.Partitions.TryGetValue(route.Partition, out PartitionOwnership? owner)
                    && owner.Node == node && owner.Epoch == epoch
                    && owner.Generation == route.Generation
                    && ownerships.Any(x => x.Partition == route.Partition && x.Generation == owner.Generation))
                {
                    var location = new ClientPartition(route.Partition, route.Entity);
                    if (_state.ClientPartitions.GetValueOrDefault(route.Client) != location)
                    {
                        _state.ClientPartitions[route.Client] = location;
                        dirty = true;
                    }
                }
            // Retain the owner's reported member counts: once the owner dies these are the only
            // accurate projected weights for its partitions (lineage may be empty or stale).
            // T-0193: an offline partition is back once its new owner reports it WITH members - a pending
            // claim is reported too, at zero members, and must not end the offline state before the load.
            foreach (PartitionVersion version in ownerships)
                if (version.Members > 0 && _state.OfflinePartitions.ContainsKey(version.Partition)
                    && _state.Partitions.TryGetValue(version.Partition, out PartitionOwnership? loaded)
                    && loaded.Node == node && loaded.Epoch == epoch && loaded.Generation == version.Generation
                    && DropOfflineRow(version.Partition, "loaded on " + node))
                    dirty = true;
            foreach (PartitionVersion version in ownerships)
                if (version.Members > 0 && _state.PartitionWeights.GetValueOrDefault(version.Partition) != version.Members)
                {
                    _state.PartitionWeights[version.Partition] = version.Members;
                    dirty = true;
                }
            AdvanceManagedReadiness();
            if (dirty) Persist();
            return current;
        }
    }

    public NodeRegistration CloseNode(string node, long epoch, TimeSpan? forceDrainAfter = null)
    {
        lock (_lock)
        {
            NodeRegistration current = RequireNode(node, epoch);
            if (current.State == NodeState.Dead)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} is dead");
            if (current.State == NodeState.Empty)
                return current;
            current.State = NodeState.Closed;
            current.ForceDrainAt ??= _clock() + (forceDrainAfter ?? TimeSpan.FromMinutes(15));
            Emit("node.closing", node, new Dictionary<string, string> { ["epoch"] = epoch.ToString() });
            Persist();
            return current;
        }
    }

    /// <summary>
    /// Designates (or activates, spare=false) a hot spare by node id (HotSpareNodes.md). Callable
    /// before the node's process ever registers, so a slot can be marked spare with no window in
    /// which its empty node is placement-eligible; the flag rides node id across incarnations.
    /// </summary>
    public NodeRegistration? SetNodeSpare(string node, bool spare)
    {
        lock (_lock)
        {
            bool changed = spare ? _state.SpareNodes.Add(node) : _state.SpareNodes.Remove(node);
            _state.Nodes.TryGetValue(node, out NodeRegistration? current);
            if (current != null && current.Spare != spare)
            {
                current.Spare = spare;
                current.Activated = spare ? null : _clock();
                changed = true;
            }
            if (changed)
            {
                Emit("node.spare", node, new Dictionary<string, string> { ["spare"] = spare.ToString() });
                Persist();
            }
            return current;
        }
    }

    public NodeRegistration MarkEmpty(string node, long epoch)
    {
        lock (_lock)
        {
            NodeRegistration current = RequireNode(node, epoch);
            if (current.State == NodeState.Empty)
                return current;
            if (current.State != NodeState.Closed)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} is not closed");
            if (_state.Partitions.Values.Any(x => x.Node == node && x.Epoch == epoch))
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} still owns partitions");
            current.State = NodeState.Empty;
            Emit("node.empty", node, new Dictionary<string, string> { ["epoch"] = epoch.ToString() });
            foreach (NodeSlotPlan slot in _state.NodePlan.Values.Where(x =>
                x.IncumbentNode == node && x.IncumbentEpoch == epoch
                    && x.IncumbentAction == NodeIncumbentAction.Draining))
            {
                slot.IncumbentAction = NodeIncumbentAction.Kill;
                _state.NodePlanUpdated = _clock();
            }
            Persist();
            return current;
        }
    }

    /// <param name="memberless">The owner holds no member of the partition (a drain's member-less release, T-0048):
    /// see <see cref="ReapIfMemberlessUnsaved"/>.</param>
    public PartitionOwnership ReleasePartition(ulong partition, string node, long epoch, long generation, bool memberless = false)
    {
        lock (_lock)
        {
            if (RequireNode(node, epoch).State == NodeState.Dead)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} is dead");
            if (_state.Partitions.TryGetValue(partition, out PartitionOwnership? released)
                && released.Generation == generation + 1 && (released.Node != node || released.Epoch != epoch))
                return released;
            PartitionOwnership owner = RequireOwner(partition, node, epoch, generation);
            owner.Node = null;
            owner.Epoch = 0;
            owner.Generation++;
            owner.LeaseExpires = DateTimeOffset.MinValue;
            Touch(owner);
            EmitPartition("released", partition, null, 0, owner.Generation, "release");
            if (!ReapIfStillborn(owner) && !(memberless && ReapIfMemberlessUnsaved(owner))) ReapIfEmpty(owner);
            Persist();
            return owner;
        }
    }

    /// <summary>
    /// #1292: remove a row its owner released with NO members before any save of its own was catalogued. Its
    /// only content is its lineage cover - the parent's save from before the split, cut to the members it had
    /// at the mint - and the owner says those members are gone. Kept, the row is "unsaved state" at every
    /// stop (ShutdownDirtyState): Closed nodes cannot claim it, so the stop records it dirty, and a stop
    /// before the next start's recovery backlog reaches it records it dirty again (octahedron-1191,
    /// 2026-10-21: split outputs 836 (an NPC grid MES removed) and 922 (a meteor) released at 04:33Z, still
    /// dirty after a 66 s start, and Quasar refused update, restore and delete without a clean shutdown).
    /// Recovering it only brings back what was removed. Not while a client is routed to it or a character
    /// calls it, or anything in its cover, home: a body the stop's disconnect removed is content, and its
    /// cover is the only copy (T-0048). Caller holds _lock and persists.
    /// </summary>
    private bool ReapIfMemberlessUnsaved(PartitionOwnership owner)
    {
        ulong partition = owner.Partition;
        if (owner.Node != null || HasCatalogued(partition) || IsRetired(partition) || _state.OfflinePartitions.ContainsKey(partition))
            return false;
        if (_state.ClientPartitions.Values.Any(client => client.Partition == partition)) return false;
        List<SaveCover> cover;
        try { cover = ResolveCover(partition, long.MaxValue, null, []); }
        catch (InvalidDataException) { return false; }   // cyclic lineage: not this release's call
        if (cover.Any(item => item.EntityIds == null)) return false;
        HashSet<long> entities = cover.SelectMany(item => item.EntityIds!).ToHashSet();
        if (_state.CharacterHomes.Values.Any(home => home.Partition == partition || entities.Contains(home.Entity))) return false;
        _state.Partitions.Remove(partition);
        _state.PartitionWeights.Remove(partition);
        _unownedSince.Remove(partition);
        EmitPartition("reaped", partition, null, 0, owner.Generation, "memberless-unsaved");
        Console.WriteLine($"reaped member-less partition {partition}: released with no members before its first save;"
            + $" its cover of {entities.Count} entit{(entities.Count == 1 ? "y" : "ies")} is not recovered");
        return true;
    }

    /// <summary>
    /// Remove a just-released ownership row that recovery could never act on: no catalogued save at
    /// any generation and no lineage record means the partition was minted but never materialized -
    /// its members either never existed durably or moved on under another id before the first
    /// capture. Left in place such a row sits "unowned and not recovering" forever and reads as an
    /// outage (partition 498, minted on a draining node and killed with it, 2026-08-15). A fresh
    /// split's seed partitions are protected by their Split lineage, and anything that ever
    /// catalogued a save is protected by the save index. Caller holds _lock and persists.
    /// </summary>
    private bool ReapIfStillborn(PartitionOwnership owner)
    {
        if (owner.Node != null) return false;
        ulong partition = owner.Partition;
        if (HasCatalogued(partition)) return false;
        // _retirement carries an entry for every partition any lineage record mentions as an input or
        // an output, which is exactly the question the two nested Any() scans over the whole lineage
        // list were asking.
        if (_retirement.ContainsKey(partition)) return false;
        _state.Partitions.Remove(partition);
        _state.PartitionWeights.Remove(partition);
        EmitPartition("reaped", partition, null, 0, owner.Generation, "stillborn");
        Console.WriteLine($"reaped stillborn partition {partition}: released with no catalogued saves and no lineage");
        return true;
    }

    /// <summary>
    /// Remove an unowned row whose newest catalogued save - taken at the generation just released, with no
    /// lineage record after it - holds NO entities: there is nothing to recover, and recovering it anyway only
    /// hands an empty partition to the next node, which holds it unreported until its drain releases it again.
    /// T-0237 w32 2026-09-25: partition 227687's only member was a stale standing body; node-4's reaper removed
    /// it, its drain released the memberless row unowned (05:57), node-1 recovered generation 8 with 0 members
    /// (06:03) and held it unreported for hours - a cycle, one lap per drain. Not while a client is routed to
    /// it, a character calls it home, or it is an offline row. Caller holds _lock and persists.
    /// </summary>
    private bool ReapIfEmpty(PartitionOwnership owner)
    {
        if (!EmptyAndUnclaimed(owner, out SaveRegistration? newest)) return false;
        ulong partition = owner.Partition;
        _state.Partitions.Remove(partition);
        _state.PartitionWeights.Remove(partition);
        _unownedSince.Remove(partition);
        EmitPartition("reaped", partition, null, 0, owner.Generation, "empty");
        Console.WriteLine($"reaped empty partition {partition}: unowned at generation {owner.Generation} and its newest save "
            + $"{newest!.Id} ({newest.Kind}) holds no entities - nothing to recover");
        return true;
    }

    /// <summary>The <see cref="ReapIfEmpty"/> test without the removal, so a caller enumerating the rows can
    /// collect and reap afterwards. Caller holds _lock.</summary>
    private bool EmptyAndUnclaimed(PartitionOwnership owner, out SaveRegistration? newest)
    {
        newest = null;
        if (owner.Node != null) return false;
        ulong partition = owner.Partition;
        if (IsRetired(partition) || _state.OfflinePartitions.ContainsKey(partition)) return false;
        if (NewestCatalogued(partition) is not { Entities: 0 } save || save.SaveId.Generation != owner.Generation - 1) return false;
        if (_retirement.TryGetValue(partition, out (long ClusterTime, bool Retired, int Index) entry) && entry.ClusterTime > save.ClusterTime) return false;
        if (_state.ClientPartitions.Values.Any(client => client.Partition == partition)) return false;
        if (_state.CharacterHomes.Values.Any(home => home.Partition == partition)) return false;
        newest = save;
        return true;
    }

    /// <summary>How long a row must sit unowned before the sweep treats it as dead rather than
    /// as one recovery has simply not reached yet. Far beyond a lease TTL and beyond any recovery
    /// cycle - ClaimRecoveries hands out one partition per request, so a backlog drains slowly and
    /// must not be mistaken for abandonment.</summary>
    private static readonly TimeSpan UnownedReapGrace = TimeSpan.FromMinutes(5);

    /// <summary>First time each currently-unowned row was SEEN unowned by the sweep. In memory
    /// only and deliberately so: after a restart every surviving orphan starts its grace afresh at
    /// load, which is the correct reading - a row that came back unowned from disk has already had
    /// a whole run to be recovered.</summary>
    private readonly Dictionary<ulong, DateTimeOffset> _unownedSince = [];

    /// <summary>Partitions already reported stranded, so the 1 Hz sweep states it once rather than
    /// every second. Cleared the moment the row is owned, recovering, retired or reachable again,
    /// so a partition that strands twice is reported twice.</summary>
    private readonly HashSet<ulong> _strandedReported = [];

    /// <summary>
    /// Sweep unowned rows that no recovery can ever claim, and that no path will ever retire.
    ///
    /// ClaimRecoveries only offers a partition whose ResolveCover is non-empty, and nothing
    /// retires a row that never got that far, so a partition fenced before its first save is
    /// unowned, unclaimable and permanent. It reports forever as "N partition(s) unowned and not
    /// recovering" - the health gate the endurance harness refuses to start against.
    ///
    /// <see cref="ReapIfStillborn"/> already removes exactly this shape, but only from
    /// <see cref="Release"/>. A lease EXPIRY produces the same orphan by a different path and
    /// never reaps, which is how these accumulate.
    ///
    /// The condition is "nothing to lose", tested twice over: no catalogued save for the partition
    /// at any generation, AND an empty recovery cover, so no save is reachable through lineage
    /// either (a split's seed partitions have no save of their own but DO resolve a cover through
    /// the parent, and are spared by that second test).
    ///
    /// The grace is what separates this from ReapIfStillborn's caller-intends-disposal contract:
    /// a freshly expired row looks identical to a dead one and is routinely re-claimed, so reaping
    /// on sight broke restart-reconciliation and handover-finalization-lease in the self-test.
    ///
    /// 2026-08-20: partitions 8294, 8295, 8304, 8305, 8610, 8611 - each minted, fenced by the
    /// topology-WAL rejection before its first save, then lease-expired 15s later. Zero .mps files
    /// on disk (control: live partitions carry 52-59), cover resolves [], no lineage record names
    /// them, and six hours plus two restarts later they still blocked the run.
    ///
    /// Caller holds _lock. Returns the number reaped so the caller can persist.
    /// </summary>
    private int ReapUnrecoverable(DateTimeOffset now)
    {
        int reaped = 0;
        // Only the rows this sweep can act on are copied out (the loop below removes rows). A ToArray of the whole
        // table - which keeps every RETIRED row, 242k on w32 - put ~2 MB on the LOH every second on this 1 Hz sweep
        // (222 MB in a 2-minute allocation trace, 2026-09-25). _unownedSince forgets rows that left the candidate set.
        List<PartitionOwnership> candidates = [];
        foreach (PartitionOwnership row in _state.Partitions.Values)
            if (row.Node == null && !row.Recovering && !IsRetired(row.Partition)) candidates.Add(row);
        if (_unownedSince.Count != 0 || _strandedReported.Count != 0)
        {
            HashSet<ulong> candidateIds = candidates.Select(row => row.Partition).ToHashSet();
            foreach (ulong gone in _unownedSince.Keys.Where(id => !candidateIds.Contains(id)).ToArray()) _unownedSince.Remove(gone);
            foreach (ulong gone in _strandedReported.Where(id => !candidateIds.Contains(id)).ToArray()) _strandedReported.Remove(gone);
        }
        foreach (PartitionOwnership owner in candidates)
        {
            ulong partition = owner.Partition;
            if (owner.Node != null || owner.Recovering || IsRetired(partition))
            {
                _unownedSince.Remove(partition);
                _strandedReported.Remove(partition);
                continue;
            }
            if (!_unownedSince.TryGetValue(partition, out DateTimeOffset since))
            {
                _unownedSince[partition] = now;
                continue;
            }
            if (now - since < UnownedReapGrace) continue;
            // SavesOf, not a scan of every catalogued save: this runs once per unowned partition on
            // the 1 Hz lease sweep, under the registry lock, and _savesByPartition exists precisely
            // so this question costs a dictionary lookup.
            IReadOnlyList<SaveRegistration> saves = SavesOf(partition);
            if (saves.Count != 0)
            {
                // T-0198: a catalogued save is worth something only while some node can still serve
                // the file. When the sole holder dies with the partition on it, the rows stay, so
                // this early-out fires - but ResolveCover, which DOES ask about holders, answers
                // empty, so ClaimRecoveries skips the partition too (line ~996, silently). The row
                // is then neither claimable nor reapable, RecoveryPending() latches, the balancer
                // stays paused and nothing ever says which partition or why: sirius bench
                // P3-PLR-01 run .3, 2026-09-20, partition 3 unowned from 00:15:25 to the end of
                // the run. Reachability is asked here with a single pass over this partition's own
                // saves - the same holder-usability rule Newest() applies - and never with a
                // lineage walk, which is what this early-out exists to avoid.
                if (saves.Any(Reachable))
                {
                    _strandedReported.Remove(partition);
                    continue;
                }
                if (_strandedReported.Add(partition))
                {
                    string[] holders = saves.SelectMany(SaveHolders).Distinct(StringComparer.Ordinal)
                        .OrderBy(holder => holder, StringComparer.Ordinal).ToArray();
                    Console.WriteLine($"partition {partition} is stranded: unowned for {(now - since).TotalSeconds:F0}s"
                        + $" with {saves.Count} catalogued save(s) and no usable holder for any of them"
                        + $" (last held by {(holders.Length == 0 ? "no node on record" : string.Join(", ", holders))});"
                        + " no node can claim it, the sweep will not reap it, and recovery and"
                        + " balancing stay pending until an operator decides");
                }
                continue;
            }
            List<SaveCover> cover;
            try { cover = ResolveCover(partition, long.MaxValue, null, []); }
            catch (InvalidDataException) { continue; }   // cyclic lineage: not this sweep's call
            if (cover.Count != 0) continue;
            _state.Partitions.Remove(partition);
            _state.PartitionWeights.Remove(partition);
            _unownedSince.Remove(partition);
            EmitPartition("reaped", partition, null, 0, 0, "unrecoverable");
            Console.WriteLine($"reaped unrecoverable partition {partition}: unowned for "
                + $"{(now - since).TotalSeconds:F0}s with no catalogued save at any generation "
                + "and an empty recovery cover");
            reaped++;
        }
        return reaped;
    }

    /// <summary>
    /// T-0246: partitions of a dead node reserved for the hot spare promoted in its place, with the
    /// spare's incarnation and the moment any node may claim them again. Memory only, like
    /// _replicaReservations: a lost reservation just makes the partition claimable by anyone, which
    /// is the pre-T-0246 behaviour.
    /// </summary>
    private readonly Dictionary<ulong, (string Node, long Epoch, DateTimeOffset Until)> _spareRecoveries = [];

    /// <summary>
    /// How long a promoted spare has the dead node's partitions to itself. A live spare claims within
    /// a second or two: the node's claim poll runs at 1 Hz (PartitionPersistence.ReplicationLoop) and
    /// wakes a dormant spare from inside the claim response, and on w33 (2026-09-25 23:25:43) node-2
    /// and node-1 claimed node-4's partitions 2 s after its lease expired. That poll shares its thread
    /// with ApplyDeletes and one whole-save replica copy, so a busy pass delays it by seconds, not by
    /// tens of seconds. A spare that DIES drops its reservations the moment it is declared Dead, which
    /// is at most one lease TTL away - so the window is never shorter than two TTLs, and the fallback to
    /// the loaded survivors never races the spare's own Dead verdict. At the live 15 s TTL that is 30 s:
    /// a wedged spare costs the dead node's content at most ~30 s of extra downtime over today.
    /// </summary>
    private TimeSpan SpareRecoveryWindow => TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, 2 * _leaseTtl.Ticks));

    /// <summary>
    /// T-0246 (Viktor 2026-09-25: "If the node is not functional, then it should be replaced with the
    /// hot spare with the online partitions reloaded"). For each regular that died holding partitions,
    /// promote a live hot spare and reserve every one of those partitions for it. (Until T-0320 the dead
    /// slot was designated the spare in its place; ids are never reused now, and the node-control planner
    /// refills the spare with a new id. A managed deployment still does it the old way: its slots are fixed.) Without this, the survivor that polled first took
    /// each partition: w33 23:25:45, node-2 already at 25 partitions and 46 clients claimed node-4's
    /// 396-member Earth-base partition 517 and fell to 0.55x, while the rotator only activated the
    /// spare 9 s later, with nothing left for it to recover.
    ///
    /// The oldest-registered spare on the dead node's host goes first (the warmest, as the rotator's activate_spare picks), and
    /// the dead node with the most partitions gets it when two die at once and spares run short; the
    /// rest recover the old way. Nothing is promoted during a cluster shutdown. Caller holds _lock and
    /// persists.
    /// </summary>
    private void PromoteSparesForDeadNodes(HashSet<(string Node, long Epoch)> dead, Dictionary<string, ulong[]> lostByDeadNode,
        DateTimeOffset now)
    {
        if (lostByDeadNode.Count == 0 || _state.Phase is ClusterPhase.Draining or ClusterPhase.Down) return;
        List<NodeRegistration> spares = [.. _state.Nodes.Values
            .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && _state.SpareNodes.Contains(x.Node)
                && x.LeaseExpires > now && !dead.Contains((x.Node, x.Epoch))
                && _state.NodeControl.Live.GetValueOrDefault(x.Node)?.Stop == null)
            .OrderBy(x => x.Registered).ThenBy(x => x.Node, StringComparer.Ordinal)];
        foreach ((string deadNode, ulong[] lost) in lostByDeadNode
            .OrderByDescending(entry => entry.Value.Length).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (spares.Count == 0) break;
            // T-0312: a retiring node that dies is not replaced by a spare - the scale-in wanted it gone.
            // T-0320: nor is any node being stopped (remove/recycle/decrease).
            NodeRecord? deadRecord = _state.NodeControl.Live.GetValueOrDefault(deadNode);
            if (!_state.Nodes.TryGetValue(deadNode, out NodeRegistration? deadRow) || deadRow.Role != NodeRole.Regular
                || _state.SpareNodes.Contains(deadNode) || _state.Scale.Retiring.Contains(deadNode)
                || deadRecord?.Stop is { Kind: not "failed" })
                continue;
            // T-0320: a spare on the dead node's host first. With per-host targets a spare from another host would
            // leave that host one serving node over its target (and the planner would drain its oldest to match).
            string? deadHost = HostOfNode(deadNode);
            NodeRegistration? spare = spares.FirstOrDefault(x => deadHost != null && string.Equals(HostOfNode(x.Node), deadHost, StringComparison.OrdinalIgnoreCase))
                ?? (NodeControlActive ? null : spares[0]);
            if (spare == null) continue;
            spares.Remove(spare);
            _state.SpareNodes.Remove(spare.Node);
            spare.Spare = false;
            spare.Activated = now;
            Emit("node.spare", spare.Node, new Dictionary<string, string> { ["spare"] = false.ToString() });
            // A managed deployment's slot pool is fixed (#1195): the dead node's slot comes back as the spare, so its
            // next incarnation registers holding nothing and the spare count is refilled.
            if (_state.ManagedDeployment is not null)
            {
                _state.SpareNodes.Add(deadNode);
                deadRow.Spare = true;
                deadRow.Activated = null;
                Emit("node.spare", deadNode, new Dictionary<string, string> { ["spare"] = true.ToString() });
            }
            // T-0320: the dead id is never re-designated as the spare (ids are never reused); its record says who
            // replaced it, and the planner refills the host's spare with a new id.
            if (_state.NodeControl.Live.TryGetValue(spare.Node, out NodeRecord? spareRecord))
            {
                spareRecord.Intent = NodeIntent.Serving;
                if (deadRecord != null)
                {
                    deadRecord.ReplacedBy = spare.Node;
                    spareRecord.Replaces = deadNode;
                }
            }
            Emit("node.activated", spare.Node, new Dictionary<string, string> { ["host"] = deadHost ?? "", ["replaces"] = deadNode });
            DateTimeOffset until = now + SpareRecoveryWindow;
            foreach (ulong partition in lost)
                _spareRecoveries[partition] = (spare.Node, spare.Epoch, until);
            Console.WriteLine($"hot spare {spare.Node} epoch={spare.Epoch} replaces dead {deadNode}: its {lost.Length} partition(s) "
                + $"[{string.Join(",", lost.OrderBy(x => x))}] are reserved for recovery on {spare.Node} for "
                + $"{SpareRecoveryWindow.TotalSeconds:F0}s");
        }
    }

    /// <summary>
    /// T-0246: drops every spare reservation that has done its job (the partition is owned again, or
    /// retired) or can no longer do it (the spare's incarnation is not Active, or the window ran out),
    /// saying which when the partition is released to any node. Caller holds _lock.
    /// </summary>
    /// <summary>
    /// #1195: Host relaunches a crashed managed slot at once, and its new process can register before the old incarnation's
    /// lease runs out (live: 13 s against a 15 s lease). That incarnation is dead: its partitions are released and a spare
    /// replaces it as ExpireLeases would have, and this slot registers as the new spare. Without it the row is overwritten,
    /// no death is seen, and the loaded survivors recover the partitions.
    /// #1206: released here, not by the next expiry pass. A reservation on a row that is still owned reads as one that has
    /// done its job, so the first claim poll of any node before that pass dropped it in silence and the partition went to
    /// whoever asked next (sirius 2026-10-20 16:10: a survivor got one of the spare's two partitions).
    /// Caller holds _lock, before the new registration replaces <paramref name="old"/>.
    /// </summary>
    private void ReplaceRestartedIncarnation(NodeRegistration old, DateTimeOffset now)
    {
        PartitionOwnership[] owned = [.. _state.Partitions.Values.Where(x => x.Node == old.Node && x.Epoch == old.Epoch)];
        Emit("node.dead", old.Node, new Dictionary<string, string>
            { ["epoch"] = old.Epoch.ToString(), ["endpoint"] = old.Endpoint, ["reason"] = "restarted" });
        if (owned.Length == 0) return;
        foreach (PartitionOwnership owner in owned)
        {
            Console.WriteLine($"restarted incarnation released partition={owner.Partition} node={owner.Node} epoch={owner.Epoch} "
                + $"generation={owner.Generation} leaseExpires={owner.LeaseExpires:O} now={now:O}");
            ReleaseOwnership(owner, "restarted");
        }
        PromoteSparesForDeadNodes([(old.Node, old.Epoch)], new() { [old.Node] = [.. owned.Select(x => x.Partition)] }, now);
    }

    private void PruneSpareRecoveries(DateTimeOffset now)
    {
        if (_spareRecoveries.Count == 0) return;
        foreach ((ulong partition, (string Node, long Epoch, DateTimeOffset Until) reservation) in _spareRecoveries.ToArray())
        {
            bool open = _state.Partitions.TryGetValue(partition, out PartitionOwnership? row) && row.Node == null && !IsRetired(partition);
            bool spareLive = _state.Nodes.TryGetValue(reservation.Node, out NodeRegistration? spare) && spare.Epoch == reservation.Epoch
                && spare.State == NodeState.Active;
            if (open && spareLive && now < reservation.Until) continue;
            _spareRecoveries.Remove(partition);
            if (open)
                Console.WriteLine($"partition {partition} is no longer reserved for hot spare {reservation.Node} epoch={reservation.Epoch}: "
                    + (spareLive ? $"not claimed within {SpareRecoveryWindow.TotalSeconds:F0}s" : "that incarnation is no longer Active")
                    + "; any node may recover it");
        }
    }

    private void ReleaseOwnership(PartitionOwnership owner, string reason)
    {
        owner.Node = null;
        owner.Epoch = 0;
        owner.Generation++;
        owner.LeaseExpires = DateTimeOffset.MinValue;
        Touch(owner);
        EmitPartition("released", owner.Partition, null, 0, owner.Generation, reason);
    }

    /// <summary>
    /// SE-214: a managed node declared Dead while its host still reports its process running has hung: nothing else stops
    /// that process (NodeControlOnDeath is unmanaged only, and the fenced-exit watchdog needs a running heartbeat thread),
    /// so the slot would stay on the zombie forever. The executor kills exactly this incarnation and reports Gone. No grace
    /// first: the stall or the live-process hold has already waited, and the node has lost its partitions. The kill counts
    /// as a spawn failure so a slot that hangs on every start backs off. The node id keeps each slot's failure distinct:
    /// nodes that all hang together (a saturated host) must not read as a global spawn halt, which a Ready report ends and
    /// a halted slot can never send. Caller holds _lock and persists.
    /// </summary>
    private void PlanKillOfHungNode(NodeSlotPlan slot, NodeRegistration node, DateTimeOffset now)
    {
        slot.IncumbentNode = node.Node;
        slot.IncumbentEpoch = node.Epoch;
        slot.IncumbentAction = NodeIncumbentAction.Kill;
        slot.LastFailure = "node_dead:" + node.Node;
        slot.Failures.Add(now);
        slot.RetryAt = now + SpawnBackoff(slot.Failures.Count);
        _state.NodePlanUpdated = now;
        Emit("node.kill-requested", node.Node, new Dictionary<string, string>
        {
            ["epoch"] = node.Epoch.ToString(),
            ["slot"] = slot.SlotKey,
            ["reason"] = "dead",
        });
    }

    public int ExpireLeases()
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            if (now < _expiryHoldUntil) return 0;
            // T-0321 P4: a remote node's lease is held inside a configured network window (LeaseHeld): not Dead, and its
            // partitions below keep their owner, so a LAN blip recovers nothing and fences nobody. #1218: so is the lease
            // of a node whose host still reports its process running, for up to LiveProcessGrace (a frozen process).
            HashSet<(string Node, long Epoch)> dead = _state.Nodes.Values
                .Where(x => x.State is NodeState.Active or NodeState.Closed
                    && (x.LeaseExpires <= now || x.SimulationProgress != default && now - x.SimulationProgress >=
                        (x.RecoveryLoadsPending > 0 ? SimulationStallRecoveryKill : SimulationStallKill))
                    && !LeaseHeld(x, now))
                .Select(x => (x.Node, x.Epoch)).ToHashSet();
            foreach (NodeRegistration node in _state.Nodes.Values.Where(x => dead.Contains((x.Node, x.Epoch))))
            {
                node.State = NodeState.Dead;
                Emit("node.dead", node.Node, new Dictionary<string, string> { ["epoch"] = node.Epoch.ToString(), ["endpoint"] = node.Endpoint });
                foreach (NodeSlotPlan slot in _state.NodePlan.Values.Where(x => x.ObservedNode == node.Node))
                {
                    bool processRunning = slot.Observed is SpawnState.Ready or SpawnState.Spawning && slot.ObservedEpoch == node.Epoch;
                    slot.Observed = SpawnState.Missing;
                    slot.ObservedAt = now;
                    if (slot.IncumbentNode == node.Node && slot.IncumbentEpoch == node.Epoch
                        && slot.IncumbentAction == NodeIncumbentAction.Draining)
                    {
                        slot.IncumbentAction = NodeIncumbentAction.Kill;
                        _state.NodePlanUpdated = now;
                    }
                    else if (_state.ManagedDeployment is not null && processRunning && slot.IncumbentAction != NodeIncumbentAction.Kill)
                        PlanKillOfHungNode(slot, node, now);
                }
            }
            PruneLiveProcessHold();
            if (_state.WorldAuthority.Node != null && dead.Contains((_state.WorldAuthority.Node, _state.WorldAuthority.Epoch)))
            {
                _state.WorldAuthority.Node = null;
                _state.WorldAuthority.Epoch = 0;
                _state.WorldAuthority.LeaseExpires = DateTimeOffset.MinValue;
				_state.WorldAuthority.ControlEndpoint = null;
                _state.WorldAuthority.Generation++;
            }
            // A partition mid-handover is deliberately frozen on its source, so its owner answers
            // for it through the transfer protocol, not through lease renewal. Time-expiring it
            // here is what let a recovery sweep claim a partition out from under its own in-flight
            // handover and fence the live transfer. Owner death still expires it, and a wedged
            // handover is bounded by its own finalization lease and reconciliation.
            // ...but only while the source is still ALIVE to answer for it. A handover whose
            // source node/epoch no longer exists has no transfer in flight to fence against: it
            // can never commit (the source is gone), and it cannot be aborted either, because
            // Abort -> RequireOwner fences on the same stale epoch. Left exempt, its partition is
            // pinned to a dead epoch of a live node forever - it never expires, so recovery never
            // sees it, so the node that "owns" it can never be retired.
            //
            // 2026-08-20 23:52, w20: node-5 drained to 0 partitions and could not be killed -
            // "could not dispose of empty partition 5619: 409 Partition 5619 ownership is fenced",
            // then "NOT killing it (parked for 5 min)". 5619's row sat at node-5 epoch 43 with a
            // lease three hours stale while node-5 ran epoch 46, held open by a handover begun
            // before the restart (source node-3 epoch 43; node-3 was on epoch 48). That is the
            // same head-of-line rotation stall partition 8563 caused for three hours earlier the
            // same day. Its 59 catalogued saves were recoverable the whole time.
            HashSet<ulong> transferring = _state.Handovers.Values
                .Where(x => x.State is HandoverState.Begun or HandoverState.Active
                    && _state.Nodes.TryGetValue(x.SourceNode, out NodeRegistration? source)
                    && source.Epoch == x.SourceEpoch
                    && source.State is NodeState.Active or NodeState.Closed)
                .Select(x => x.Partition).ToHashSet();
            PartitionOwnership[] expired = _state.Partitions.Values
                .Where(x => x.Node != null && (dead.Contains((x.Node, x.Epoch))
                    || x.LeaseExpires <= now && !transferring.Contains(x.Partition)
                        && !(_state.Nodes.TryGetValue(x.Node, out NodeRegistration? holder)
                            && holder.Epoch == x.Epoch && LeaseHeld(holder, now)))).ToArray();
            // Taken before the loop below forgets the owners.
            Dictionary<string, ulong[]> lostByDeadNode = expired.Where(x => dead.Contains((x.Node!, x.Epoch)))
                .GroupBy(x => x.Node!).ToDictionary(group => group.Key, group => group.Select(x => x.Partition).ToArray());
            foreach (PartitionOwnership owner in expired)
            {
                Console.WriteLine($"lease expired partition={owner.Partition} node={owner.Node} epoch={owner.Epoch} "
                    + $"generation={owner.Generation} leaseExpires={owner.LeaseExpires:O} now={now:O}");
                ReleaseOwnership(owner, "lease-expired");
            }
            foreach ((string deadNode, long _) in dead)
                if (_state.Nodes.TryGetValue(deadNode, out NodeRegistration? deadRow) && deadRow.Role == NodeRole.Regular)
                    NodeControlOnDeath(deadNode, lostByDeadNode.GetValueOrDefault(deadNode)?.Length ?? 0, now);
            PromoteSparesForDeadNodes(dead, lostByDeadNode, now);
            PruneSpareRecoveries(now);
            int reaped = ReapUnrecoverable(now);
            long offlineRevision = _state.OfflineRevision;
            SettleOfflineRows(now);
            AuditInvariants(now);
            if (dead.Count != 0 || expired.Length != 0 || reaped != 0 || offlineRevision != _state.OfflineRevision)
                Persist();
            return expired.Length;
        }
    }

    /// <summary>
    /// Whether any partition is still mid-recovery: claimed but not yet imported, or unowned and not
    /// retired (waiting for a claimant). The balancer waits this out - node loads are not the
    /// cluster's real shape until the content is back, and a plan made against half-loaded nodes
    /// moves the wrong things (2026-08-24 16:51: a cold-start pass read excess 20947.00 because only
    /// one node had registered content). Unowned-unrecoverable rows are reaped by the sweep, so this
    /// cannot latch permanently.
    /// </summary>
    /// <remarks>
    /// T-0198: a STRANDED row - unowned past the grace with catalogued saves no live node can serve -
    /// is excluded too, for the same reason a retired one is. Nothing will ever claim it, so counting
    /// it pauses the balancer for the life of the process over content that is not coming back on its
    /// own. The row itself is kept and the sweep has said so by name; the exclusion is lifted the
    /// moment a holder is usable again, so a node that returns with its data restores the pause.
    ///
    /// Retired rows are excluded from BOTH terms: the live w23 registry carries retired tombstones
    /// whose Recovering flag was never cleared (retired mid-claim; partition 2 among 8 such rows,
    /// 2026-08-24 17:36), and counting them latched the balance pause for the life of the process
    /// on the first -glb2 boot. A retired partition can never be recovered, so it is never pending.
    /// </remarks>
    public bool RecoveryPending()
    {
        lock (_lock)
            return _state.Partitions.Values.Any(x => !IsRetired(x.Partition)
                && !_strandedReported.Contains(x.Partition)
                && (x.Recovering || x.Node == null && !_state.OfflinePartitions.ContainsKey(x.Partition)));
    }

    /// <summary>
    /// Whether a node rotation is (or was moments ago) rewriting the cluster's load distribution: a
    /// node is Closed, or a regular was activated out of spare within the settling window. A
    /// rotation drains a whole node into a freshly activated spare, so for its duration the load
    /// snapshot is transient in exactly the way that fools a balancer: the drained node reads
    /// empty right before it is killed, and the fresh spare reads empty right before it receives a
    /// whole node's content. w23 2026-08-24 18:53-18:58 (-glb4): three balance pins each fired 1-4 s
    /// after a rotation began, one sending 80,379 PCU INTO the node the rotator had started
    /// draining 4 s earlier - it bounced back 110 s later, ~28 s of migration lag for a net zero.
    /// Balance initiation waits the rotation out; co-location is unaffected.
    /// </summary>
    public bool RotationInFlight(TimeSpan settling)
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            return _state.Nodes.Values.Any(x => x.State == NodeState.Closed
                || (x.State == NodeState.Active && !x.Spare
                    && x.Activated is { } activated && now - activated < settling));
        }
    }

    public RecoveryClaim[] ClaimRecoveries(string node, long epoch, ulong[] partitions)
    {
        lock (_lock)
        {
            RequireRegularActiveNode(node, epoch);
            // A hot spare must not accumulate content, and must NOT be answered with an error:
            // a 409 on the node's registry loop fences and terminates the process.
            if (_state.SpareNodes.Contains(node))
                return [];
            // T-0312: a node retiring for a scale-in takes no new content either (same no-error answer).
            if (_state.Scale.Retiring.Contains(node))
                return [];
            DateTimeOffset expires = _clock() + _leaseTtl;
            List<RecoveryClaim> claims = [];
            // Host-loss triage order (Orchestration §9.3): player-attached partitions
            // recover first, then the most recently captured, so a capacity-limited
            // survivor spends its slots on the parts of the world people are in.
            // Projected-load gate: a partition whose member weight would push the claimant past
            // its load ceiling is skipped in the auto-pick. Explicitly requested partitions (warm
            // recovery of a node's own world) are never load-gated.
            NodeRegistration claimant = _state.Nodes[node];
            // ProjectedLoad lags a fresh claim (census refresh and ownership report race by a
            // frame), so charge the registry's own view of what the node holds: in-flight
            // recovering claims plus owned-partition weights, whichever bound is higher.
            long headroom;
            if (claimant.LoadCapacity > 0)
            {
                long ownedWeight = _state.Partitions.Values.Where(x => x.Node == node && !x.Recovering).Sum(x => PartitionWeight(x.Partition));
                long recoveringWeight = _state.Partitions.Values.Where(x => x.Node == node && x.Recovering).Sum(x => PartitionWeight(x.Partition));
                headroom = claimant.LoadCapacity - Math.Max(claimant.ProjectedLoad, ownedWeight) - recoveringWeight;
            }
            else
                headroom = long.MaxValue;
            // Same choice as before - the partition that a connected client is in wins, then one a
            // character calls home, then the freshest catalogue entry, then the lowest id - but taken
            // in one pass with the two "is anybody there" questions answered once for the whole
            // candidate set. It used to ask them per candidate (each a scan of every route and every
            // character home, and CharacterHomes is durable, so it grows with every player who has
            // ever joined), resolve the full save cover per candidate against a fresh memo, and then
            // SORT the whole set to take a single element. This path is hottest during host-loss
            // recovery, when the candidate set is at its largest and the lock is most contended.
            // An empty row is reaped rather than handed out: recovery of it only moves an empty partition to this
            // node (see ReapIfEmpty). Rows reach the unowned state through release, lease expiry and handover abort
            // alike, and every one of them is handed out through this method, so only the rows it would hand out
            // are checked - never a sweep of _state.Partitions: that table keeps every RETIRED row too (242k of
            // 242k on w32), and a per-claim ToArray of it put ~2 MB on the LOH per call (3a1ecb5, 2026-09-25:
            // allocation 204 -> 533 MB/10 s, LOH fragmentation 17 -> 65 MB).
            HashSet<ulong> reapedEmpty = [];
            ulong[] requested;
            // T-0193: directed offline loads first, whatever else was asked and whatever the headroom -
            // a player is on the way, and capacity is not negotiable. Offline rows are never handed out
            // any other way: not by the auto-pick (that reloads the whole sleeping world on every
            // rotation) and not by an explicit request (a warm-recovery candidate list is a node's own
            // stale view of what it used to hold).
            ulong[] directed = DirectedOfflineLoads(node, _clock());
            // T-0246: a dead node's partitions reserved for the spare promoted in its place go to that spare,
            // all in one claim and never load-gated (it is empty, and they are the node it replaces), and to
            // nobody else until the reservation ends - see PromoteSparesForDeadNodes. Rows no save can
            // rebuild yet are left out exactly as the auto-pick leaves them out.
            PruneSpareRecoveries(_clock());
            HashSet<ulong> reservedElsewhere = [];
            List<ulong> reservedHere = [];
            foreach ((ulong partition, (string Node, long Epoch, DateTimeOffset Until) reservation) in _spareRecoveries)
                if (reservation.Node != node || reservation.Epoch != epoch)
                    reservedElsewhere.Add(partition);
                else if (ResolveCover(partition, long.MaxValue, null, []).Count != 0 && !NewerSaveHeld(partition, _clock())
                    && !HeldForLateSave(partition, _clock()))
                    reservedHere.Add(partition);
            if (directed.Length != 0 || reservedHere.Count != 0)
                requested = directed.Concat(reservedHere).Distinct().ToArray();
            else if (partitions.Length != 0)
                requested = partitions.Distinct().Where(x => !_state.OfflinePartitions.ContainsKey(x) && !reservedElsewhere.Contains(x)
                    && !NewerSaveHeld(x, _clock()) && !HeldForLateSave(x, _clock())).ToArray();
            else
            {
                HashSet<ulong> clientBearing = _state.ClientPartitions.Values.Select(client => client.Partition).ToHashSet();
                HashSet<ulong> homeBearing = _state.CharacterHomes.Values.Select(home => home.Partition).ToHashSet();
                Dictionary<(ulong, long, string, bool), List<SaveCover>> claimMemo = [];
                PartitionOwnership? best = null;
                (bool Client, bool Home, long Catalogued) bestRank = default;
                Dictionary<string, int> owned = new(StringComparer.Ordinal);   // T-0337: counted in the same pass
                foreach (PartitionOwnership candidate in _state.Partitions.Values)
                {
                    if (candidate.Node != null) { owned[candidate.Node] = owned.GetValueOrDefault(candidate.Node) + 1; continue; }
                    if (IsRetired(candidate.Partition)) continue;
                    if (_state.OfflinePartitions.ContainsKey(candidate.Partition)) continue;
                    if (reservedElsewhere.Contains(candidate.Partition)) continue;
                    // T-0347: before the empty-row reap below - the unreported Final may be the save that holds the content.
                    if (HeldForLateSave(candidate.Partition, _clock())) continue;
                    if (PartitionWeight(candidate.Partition) > headroom) continue;
                    if (EmptyAndUnclaimed(candidate, out _)) { reapedEmpty.Add(candidate.Partition); continue; }
                    if (ResolveCover(candidate.Partition, long.MaxValue, null, [], memo: claimMemo).Count == 0) continue;
                    if (NewerSaveHeld(candidate.Partition, _clock())) continue;   // T-0296 r2, #1282: wait out the hold
                    (bool Client, bool Home, long Catalogued) rank = (clientBearing.Contains(candidate.Partition),
                        homeBearing.Contains(candidate.Partition), NewestCatalogued(candidate.Partition)?.ClusterTime ?? 0);
                    if (best == null || OutranksForRecovery(rank, candidate.Partition, bestRank, best.Partition))
                    {
                        best = candidate;
                        bestRank = rank;
                    }
                }
                // T-0337: a free recovery goes to the emptiest serving node of the host. Directed loads, spare
                // reservations and a warm node's own list (above) are not free and are never held back.
                if (best != null && RecoveryWaitsForEmptier(claimant, owned, PartitionWeight(best.Partition), _clock()) != null)
                    best = null;
                requested = best == null ? [] : [best.Partition];
            }
            foreach (ulong partition in requested)
                if (_state.Partitions.TryGetValue(partition, out PartitionOwnership? row) && EmptyAndUnclaimed(row, out _))
                    reapedEmpty.Add(partition);
            foreach (ulong partition in reapedEmpty)
                ReapIfEmpty(_state.Partitions[partition]);
            if (reapedEmpty.Count != 0) Persist();
            // A warm node's candidate list may name a row reaped just above: nothing to recover, not a conflict.
            requested = requested.Where(partition => !reapedEmpty.Contains(partition)).ToArray();
            foreach (ulong partition in requested)
            {
                if (!_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner) || owner.Node != null || IsRetired(partition))
                    throw new RegistryConflictException($"Partition {partition} is not available for recovery");
            }
            foreach (ulong partition in requested)
            {
                PartitionOwnership owner = _state.Partitions[partition];
                owner.Node = node;
                owner.Epoch = epoch;
                owner.LeaseExpires = expires;
                owner.Recovering = true;
                Touch(owner);
                if (_state.OfflinePartitions.TryGetValue(partition, out OfflinePartition? offlineRow))
                {
                    offlineRow.LoadClaims++;
                    Console.WriteLine($"offline partition {partition} claimed for load by {node} (reason {offlineRow.LoadReason ?? "unknown"})");
                }
                // KI-15 again: Snapshot() every registration that leaves this lock. The response is
                // serialised by WriteJson OUTSIDE the lock, and SaveRegistration carries four live
                // mutable collections (Locations, LocationHolders, HolderEndpoints, CorruptHolders).
                // A replica registration landing mid-serialisation threw
                // "Collection was modified" out of HashSet<string>.Enumerator.MoveNext inside
                // System.Text.Json and failed a node's /registry/recoveries/claim with a 500
                // (node-1, 2026-08-22 06:19:12). Locations is mutated on every replicated save, so
                // this path is hit constantly and only needs the write to land inside the window.
                // T-0323: Served() also names a proxy for every gone holder (its own process can no longer serve its files).
                SaveCover[] cover = ResolveCover(partition, long.MaxValue, null, [])
                    .Select(item => item with { Save = Served(item.Save) }).ToArray();
                // SavesOf, not every catalogued save in the registry, per cover item.
                SaveCover[] fallbacks = cover.SelectMany(item => SavesOf(item.Save.SaveId.Partition)
                    .Where(save => save.SaveId.CompareTo(item.Save.SaveId) < 0
                        && !save.PendingDelete && save.Locations.Count != 0
                        // T-0339: not a save from before a split that kept this id (the node reads a fallback
                        // through the cover item's filter). ResolveCover reaches that save, cut to the kept
                        // members, once the newer one is reported corrupt or its holders are gone.
                        && !SplitBetween(save, item.Save))
                    .OrderByDescending(save => save.SaveId).Select(save => new SaveCover(Served(save), item.EntityIds))).ToArray();
                // owner is a live PartitionOwnership this method just mutated; copy it too so the
                // response cannot observe a later lease change mid-write.
                // T-0237: player bodies whose durable home moved to ANOTHER partition after this content was saved.
                // A copy of such a body in this save is stale - w32 2026-09-25 00:23:57: pilot-10's old standing
                // partition 227687 (saved 00:02) was released memberless at a stop after the pilot had boarded
                // its ship (home 227713 since ~00:10), a start recovered the standing copy, and the ship's load
                // then emptied the pilot's cockpit as a duplicate. Standing and seated copies alike: the node
                // drops a standing one and empties the seat of a seated one. See StaleHomedCopies.
                long[] homedElsewhere = StaleHomedCopies(partition, cover);
                SaveRegistration[] coverNewestFirst = cover.Select(item => item.Save).OrderByDescending(save => save.SaveId).ToArray();
                Dictionary<long, long> stamps = [];
                foreach (SaveRegistration save in coverNewestFirst)
                    foreach ((long entity, long version) in save.HomeStamps ?? [])
                        stamps.TryAdd(entity, version);
                claims.Add(new RecoveryClaim(owner with { }, cover, fallbacks, homedElsewhere,
                    HomesElsewhereOf(partition), stamps,
                    coverNewestFirst.Any(save => save.HomeStamps != null),
                    coverNewestFirst.Length == 0 ? null : cover.Max(item => item.Save.Created),
                    coverNewestFirst.Length == 0 ? 0 : cover.Max(item => item.Save.ClusterTime)));
            }
            if (claims.Count != 0)
            {
                _recoveryWaitedFor.Remove(node);
                Persist();
            }
            return claims.ToArray();
        }
    }

    /// <summary>
    /// The projected member weight of a partition: the last owner's heartbeat-reported member
    /// count if one was ever retained, else the newest lineage record that names it.
    /// Unknown weight counts as one member so it never blocks an unlimited node.
    /// </summary>
    private long PartitionWeight(ulong partition)
    {
        if (_state.PartitionWeights.TryGetValue(partition, out long reported) && reported > 0)
            return reported;
        for (int i = _state.Lineage.Count - 1; i >= 0; i--)
            if (_state.Lineage[i].Members != null && _state.Lineage[i].Members.TryGetValue(partition, out long[]? members))
                return members.Length;
        return 1;
    }

    public RebuildSummary RebuildCatalog(RebuildRequest request)
    {
        lock (_lock)
        {
            // P2-REC-04: reconstruct a lost registry from save headers scanned off the surviving node
            // roots. Only an empty registry may be rebuilt — a populated one is authoritative and a
            // rebuild over it would fork the lineage.
            if (_state.Nodes.Count != 0 || _state.Partitions.Count != 0 || _state.Saves.Count != 0 || _archivedPartitions.Count != 0 || _state.GlobalSaves.Count != 0
                || _state.VoxelBases.Count != 0 || _state.VoxelJournals.Count != 0)
                throw new RegistryConflictException("Catalog rebuild requires an empty registry");
            foreach (RebuildSaveReport report in request.Saves ?? [])
            {
                var id = new SaveId(report.Partition, report.Generation, report.Sequence);
                if (report.Partition == 0 || report.Generation < 1 || report.Sequence < 1 || report.SizeBytes < 0
                    || string.IsNullOrWhiteSpace(report.Checksum) || string.IsNullOrWhiteSpace(report.Location) || string.IsNullOrWhiteSpace(report.Node))
                    throw new ArgumentException($"Rebuild save report {id} is incomplete");
                if (_state.Saves.TryGetValue(Key(id), out SaveRegistration? existing))
                {
                    if (existing.Kind != report.Kind || existing.SizeBytes != report.SizeBytes || existing.ClusterTime != report.ClusterTime
                        || !existing.Checksum.Equals(report.Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new RegistryConflictException($"Rebuild save {id} conflicts across scanned roots");
                    existing.Locations.Add(report.Location);
                    existing.LocationHolders[report.Location] = report.Node;
                    Touch(existing);
                    continue;
                }
                var save = new SaveRegistration(id, report.Node, 0, report.Kind, report.SizeBytes, report.Checksum, report.ClusterTime, [report.Location], _clock());
                save.LocationHolders[report.Location] = report.Node;
                PutSave(Key(id), save);
            }
            foreach (RebuildGlobalSaveReport report in request.GlobalSaves ?? [])
            {
                var id = new GlobalSaveId(report.Generation, report.Sequence);
                if (report.Generation < 1 || report.Sequence < 1 || report.SizeBytes < 0
                    || string.IsNullOrWhiteSpace(report.Checksum) || string.IsNullOrWhiteSpace(report.Location) || string.IsNullOrWhiteSpace(report.Node))
                    throw new ArgumentException($"Rebuild global save report {id} is incomplete");
                if (_state.GlobalSaves.TryGetValue(GlobalKey(id), out GlobalSaveRegistration? existing))
                {
                    if (existing.Kind != report.Kind || existing.SizeBytes != report.SizeBytes || existing.ClusterTime != report.ClusterTime
                        || !existing.Checksum.Equals(report.Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new RegistryConflictException($"Rebuild global save {id} conflicts across scanned roots");
                    existing.Locations.Add(report.Location);
                    existing.LocationHolders[report.Location] = report.Node;
                    Touch(existing);
                    continue;
                }
                var save = new GlobalSaveRegistration(id, report.Node, 0, report.Kind, report.SizeBytes, report.Checksum, report.ClusterTime, [report.Location], _clock());
                save.LocationHolders[report.Location] = report.Node;
                _state.GlobalSaves[GlobalKey(id)] = save;
            }
            // Voxel bases and journal tails scanned off the node roots; without them every node would fall
            // back to the pristine world-template storage on its next load and the journal lineage would
            // silently restart. Epoch and generation are 0 — the rebuilt records predate every live claim,
            // and the monotonic checks on later registrations only require never going backwards.
            foreach (RebuildVoxelBaseReport report in request.VoxelBases ?? [])
            {
                if (string.IsNullOrWhiteSpace(report.StorageName) || report.BaseVersion < 0 || report.TailSequence < report.BaseVersion
                    || report.SizeBytes < 0 || string.IsNullOrWhiteSpace(report.Checksum) || string.IsNullOrWhiteSpace(report.Location)
                    || string.IsNullOrWhiteSpace(report.Node))
                    throw new ArgumentException($"Rebuild voxel base report {report.StorageName}/{report.BaseVersion} is incomplete");
                string key = VoxelBaseKey(report.StorageName, report.BaseVersion);
                if (_state.VoxelBases.TryGetValue(key, out VoxelBaseRegistration? existing))
                {
                    if (existing.SizeBytes != report.SizeBytes || !existing.Checksum.Equals(report.Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new RegistryConflictException($"Rebuild voxel base {report.StorageName}/{report.BaseVersion} conflicts across scanned roots");
                    continue;
                }
                _state.VoxelBases[key] = new VoxelBaseRegistration(report.StorageName, report.BaseVersion, report.TailSequence,
                    report.Node, 0, 0, report.SizeBytes, report.Checksum, report.Location, _clock());
            }
            foreach (RebuildVoxelJournalReport report in request.VoxelJournals ?? [])
            {
                if (string.IsNullOrWhiteSpace(report.StorageName) || string.IsNullOrWhiteSpace(report.Node))
                    throw new ArgumentException($"Rebuild voxel journal report {report.StorageName} is incomplete");
                if (!_state.VoxelBases.TryGetValue(VoxelBaseKey(report.StorageName, report.BaseVersion), out VoxelBaseRegistration? basis)
                    || report.TailSeq < basis.TailSequence)
                    throw new ArgumentException($"Rebuild voxel journal {report.StorageName} does not sit on a scanned base");
                if (_state.VoxelJournals.TryGetValue(report.StorageName, out VoxelJournalRegistration? old)
                    && (old.BaseVersion != report.BaseVersion || old.TailSeq != report.TailSeq))
                    throw new RegistryConflictException($"Rebuild voxel journal {report.StorageName} conflicts across scanned roots");
                _state.VoxelJournals[report.StorageName] = new VoxelJournalRegistration(report.StorageName, report.BaseVersion,
                    report.TailSeq, report.Node, 0, 0, _clock());
            }
            // Every rebuilt partition comes back unowned one generation past its newest catalogued save —
            // the same state ExpireLeases leaves after an owner crash — so ordinary recovery claims resume
            // the lineage and fresh captures register at sequence 1 of the claimed generation.
            foreach (IGrouping<ulong, SaveRegistration> group in _state.Saves.Values.GroupBy(x => x.SaveId.Partition))
            {
                _state.Partitions[group.Key] = new PartitionOwnership(group.Key, null, 0, group.Max(x => x.SaveId.Generation) + 1, DateTimeOffset.MinValue);
                EmitPartition("created", group.Key, null, 0, _state.Partitions[group.Key].Generation, "registry-rebuild");
            }
            _state.WorldAuthority.Generation = _state.GlobalSaves.Values.Select(x => x.SaveId.Generation).DefaultIfEmpty(0L).Max();
            // T-0374: a claimed placeholder id would move the counter into the placeholder range.
            ulong maxPartition = _state.Partitions.Keys.Where(x => !ClusterProtocol.PlaceholderPartitionIds.Contains(x)).DefaultIfEmpty(0UL).Max();
            if (_state.NextPartitionId <= maxPartition)
                _state.NextPartitionId = maxPartition + 1;
            // Ranges already issued before the wipe are unknown to a rebuilt registry; the operator
            // floor keeps the counter above every range that may still be alive in a save.
            if (request.NextEntityIdRange > _state.NextEntityIdRange)
                _state.NextEntityIdRange = request.NextEntityIdRange.Value;
            Persist();
            return new RebuildSummary(_state.Partitions.Count, _state.Saves.Count, _state.GlobalSaves.Count,
                _state.WorldAuthority.Generation, _state.NextPartitionId, _state.NextEntityIdRange,
                _state.VoxelBases.Count, _state.VoxelJournals.Count);
        }
    }

    public PartitionOwnership GetPartitionOwner(ulong partition)
    {
        lock (_lock)
            return _state.Partitions.GetValueOrDefault(partition)
                ?? throw new ArgumentException($"Partition {partition} has no registered owner");
    }

    /// <summary>T-0354b: an id this registry allocated to a node that has no row yet - a layout whose claim is under way.</summary>
    public bool IsAllocatedUnclaimed(ulong partition)
    {
        lock (_lock) return _state.AllocatedPartitionIds.Contains(partition) && !_state.Partitions.ContainsKey(partition);
    }

    public NodeRegistration GetNodeByEndpoint(string endpoint)
    {
        lock (_lock)
        {
            NodeRegistration[] matches = _state.Nodes.Values
                .Where(x => string.Equals(x.Endpoint.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)).ToArray();
            // A fresh-id node reuses the port slot of a stopped node whose row is still kept for its
            // saves (T-0323). One process binds an endpoint at a time, so it belongs to the live
            // registration: Active, else the one still draining (Closed), else the newest.
            return LiveFirst(matches)
                ?? throw new ArgumentException($"No node is registered at endpoint '{endpoint}'");
        }
    }

    /// <summary>
    /// Of the rows that share an endpoint or control endpoint, the one whose process is there: Active, else the
    /// one still draining (Closed), else the newest. Rows of stopped nodes are kept for their saves and their port
    /// slots are reused, so a first match names a stopped node (T-0335, T-0341).
    /// </summary>
    internal static NodeRegistration? LiveFirst(IEnumerable<NodeRegistration> sameEndpoint) => sameEndpoint
        .OrderBy(x => x.State switch { NodeState.Active => 0, NodeState.Closed => 1, _ => 2 })
        .ThenByDescending(x => x.Registered).FirstOrDefault();

    /// <summary>
    /// True when the client's last-known body sits in a partition that is momentarily unowned (or
    /// still loading) but recoverable - the drain/recovery window. Admitting the client now would
    /// land it on the default backend and spawn a fresh body seconds before the real one is
    /// imported, forking the player's progress into a duplicate; a transient join refusal makes
    /// the client retry after the auto-claim lands.
    /// </summary>
    public bool PendingHomeRecovery(ulong client)
    {
        lock (_lock)
        {
            if (_state.ClientPartitions.ContainsKey(client)) return false;
            if (!_state.CharacterHomes.TryGetValue(client, out CharacterHome? home)) return false;
            ulong partition = LiveHomePartition(home);
            if (IsRetired(partition)) return false;
            if (!_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner)) return false;
            bool unready = owner.Node == null || owner.Recovering || owner.LeaseExpires <= _clock();
            // T-0237: a home that is MOVING is the same window from the other side. The route would name the
            // source, and the body leaves with the partition: w33 2026-09-27 20:12:11 Pilot-038's home 111231 was
            // co-located node-2 -> node-1 while its join was being answered, the client was routed to node-2, got
            // a fresh body there, and node-1 despawned the seated one as a duplicate. Hold until it has landed.
            if (owner.Node != null && PartitionMoving(partition)) return true;
            // T-0193: a home inside an offline partition (a body left in a cryo chamber) is demand - the
            // join is held while a directed load brings the partition up on the emptiest serving node (T-0337).
            if (owner.Node == null && _state.OfflinePartitions.ContainsKey(partition))
            {
                RequestOfflineLoad(partition, null, "join");
                return true;
            }
            return unready && ResolveCover(partition, long.MaxValue, null, []).Count != 0;
        }
    }

    public ClientRoute? ResolveClientRoute(ulong client)
    {
        lock (_lock)
        {
            if (_state.ClientPartitions.TryGetValue(client, out ClientPartition? location))
            {
                // A live placement that will not resolve is deliberately fatal (§6.3, and the
                // self-test asserts it): the partition is mid-rotation or recovering, it *will* come
                // back, and answering the default backend meanwhile forks a duplicate body.
                // A retired partition never comes back, so the same throw strands the client for the
                // rest of the run - it retries into an identical refusal forever, and the 1 Hz
                // heartbeat that would repair the binding only runs for a client that has already
                // attached. Drop the dead placement instead and fall through to the home path, which
                // fails soft into a fresh join. The body went with the partition, so there is no
                // surviving character for that join to duplicate.
                if (!IsRetired(location.Partition))
                    return ResolveClientRoute(client, location);
                _state.ClientPartitions.Remove(client);
                Persist();
            }
            // No live session for this client, so derive the route from where its character was last
            // seen (Clustering §6.4 Join). The partition id outlives handovers - ownership moves, the id
            // does not - so this stays correct however far the body has travelled since, and ownership
            // is durable, so it is still answerable after a Gateway restart.
            if (!_state.CharacterHomes.TryGetValue(client, out CharacterHome? home)) return null;
            ulong partition = LiveHomePartition(home);
            try
            {
                ClientRoute route = ResolveClientRoute(client, new ClientPartition(partition, home.Entity));
                _joining[client] = (route.Partition, _clock() + JoiningPlacementTtl);
                return route;
            }
            catch (Exception failure) when (failure is RegistryConflictException or RegistryBusyException)
            {
                // The partition is unloaded, recovering, or owned by a node that is not currently
                // active. Answering null sends the client to the default backend, which is the old
                // behaviour and no worse; refusing the join outright would be. Say so: this fallback
                // is where a second body gets spawned, and it used to be silent (w29 pilot-10).
                //
                // T-0197 SPLIT THE REASON IN TWO AND THIS PATH DELIBERATELY KEEPS TREATING THEM THE
                // SAME. Only a LIVE PLACEMENT is worth holding a join for: there the client has a
                // node it was just on and a body to go back to. Here there is no session, only a
                // recorded home, and the case where holding would be right - unowned but
                // recoverable - is already held one level up by PendingHomeRecovery, before the
                // route is ever asked for. Holding here as well would turn every join that arrives
                // while any home partition is briefly unroutable into a 20 s wait for a client
                // that has nothing to wait for.
                Console.WriteLine($"Client {client} character home partition {home.Partition}"
                    + (partition != home.Partition ? $" (now {partition})" : "")
                    + $" entity {home.Entity} did not resolve: {failure.Message}; the join falls back to the default backend");
                return null;
            }
        }
    }

    /// <summary>Records a client's character home and answers its version (see <see cref="CharacterHome.Version"/>).</summary>
    public long SetCharacterHome(ulong client, long identity, long entity, ulong partition, string node,
        long epoch, long generation, long expectedVersion, long expectedWriteRevision)
    {
        if (client == 0 || entity == 0 || partition == 0) throw new ArgumentException("Client, entity and partition are required");
        lock (_lock)
        {
            // T-0304: validate even unchanged reports. A delayed writer must not spend a new
            // home version after its node/claim was fenced, retired or stopped renewing.
            RequireCharacterHomeWriter(partition, node, epoch, generation);
            CharacterHome? previous = _state.CharacterHomes.GetValueOrDefault(client);
            // T-0305: validate before the unchanged shortcut too. A permit's candidate cannot
            // become a current home while its authorized close can still execute.
            if ((previous?.WriteRevision ?? 0) != expectedWriteRevision || previous?.PendingReap?.Entity == entity)
                throw new RegistryConflictException($"Client {client} character home write revision changed or reap is pending");
            var home = new CharacterHome(identity, entity, partition, node, _clock());
            if (_state.CharacterHomes.TryGetValue(client, out CharacterHome? existing)
                && existing == home with { Updated = existing.Updated, Version = existing.Version,
                    WriteRevision = existing.WriteRevision, PendingReap = existing.PendingReap,
                    PendingReapPartition = existing.PendingReapPartition })
            {
                if (existing.Version != 0) return existing.Version;   // unchanged - do not spend a WAL write per report
                // A home recorded before versions existed gets one on its first report, without moving Updated.
                _state.CharacterHomes[client] = existing with { Version = ++_state.HomeVersion,
                    WriteRevision = existing.WriteRevision + 1 };
                Persist();
                return _state.CharacterHomes[client].Version;
            }
            // Ownership alone cannot order two still-owned partitions when a character moves
            // between them. Compare the observed home version at this same linearization point.
            if ((_state.CharacterHomes.GetValueOrDefault(client)?.Version ?? 0) != expectedVersion)
                throw new RegistryConflictException($"Client {client} character home version changed");
            _state.CharacterHomes[client] = home with { Version = ++_state.HomeVersion,
                WriteRevision = (previous?.WriteRevision ?? 0) + 1, PendingReap = previous?.PendingReap,
                PendingReapPartition = previous?.PendingReapPartition ?? 0 };
            Persist();
            return _state.CharacterHomes[client].Version;
        }
    }

    public CharacterHome? GetCharacterHome(ulong client)
    {
        lock (_lock) return _state.CharacterHomes.GetValueOrDefault(client);
    }

    public ClientRoute RouteClient(ulong client, ulong partition, long entity = 0)
    {
        if (client == 0 || partition == 0) throw new ArgumentException("Client and partition are required");
        lock (_lock)
        {
            var location = new ClientPartition(partition, entity);
            ClientRoute route = ResolveClientRoute(client, location);
            _state.ClientPartitions[client] = location;
            Persist();
            return route;
        }
    }

    public ClientPartition? GetClientPartition(ulong client)
    {
        lock (_lock) return _state.ClientPartitions.GetValueOrDefault(client);
    }

    /// <summary>
    /// T-0234: where a client that joined by its character home is headed, until a node reports it. The node
    /// reports a client's partition only once the client controls something, so for the whole join window the
    /// gateway's "routed to connected client" offline and release guards had nothing to match: w32 2026-09-24
    /// 06:57:32 pilot-58 resolved 204479 by its home and node-2 committed 204479 offline 8 s later, 3 s before
    /// the client was world-ready. Transient on purpose: writing ClientPartitions instead would turn the home
    /// path's soft fallback (null, join held by PendingHomeRecovery) into the live placement's hard refusal on
    /// the client's next join.
    /// </summary>
    private readonly Dictionary<ulong, (ulong Partition, DateTimeOffset Until)> _joining = [];
    private static readonly TimeSpan JoiningPlacementTtl = TimeSpan.FromSeconds(120);

    /// <summary>The partition a client is routed to: its reported placement, else a fresh home-path resolve (T-0234).</summary>
    public ulong? RoutedPartition(ulong client)
    {
        lock (_lock)
        {
            if (_state.ClientPartitions.TryGetValue(client, out ClientPartition? placement)) return placement.Partition;
            if (_joining.TryGetValue(client, out var joining))
            {
                if (joining.Until > _clock()) return joining.Partition;
                _joining.Remove(client);
            }
            return null;
        }
    }

    public ulong[] ClientPartitionIds()
    {
        lock (_lock) return _state.ClientPartitions.Values.Select(x => x.Partition).ToArray();
    }

    public ClientRoute? RestoreClientRoute(ulong client, ClientPartition? location)
    {
        lock (_lock)
        {
            if (location == null)
                _state.ClientPartitions.Remove(client);
            else
                _state.ClientPartitions[client] = location;
            Persist();
            return location == null ? null : ResolveClientRoute(client, location);
        }
    }

    public NodeRegistration[] Nodes()
    {
        lock (_lock)
            return _state.Nodes.Values.OrderBy(x => x.Node).ToArray();
    }

    public WorldAuthority WorldAuthority()
    {
        lock (_lock)
            return _state.WorldAuthority with { };
    }

    /// <summary>
    /// The World Authority generation, read WITHOUT the registry lock. The relay asks it on every
    /// client-to-node packet (DirectGateway.AttachedBindingCurrent); through WorldAuthority() that
    /// was one or two registry-lock acquisitions per packet, so any long lock hold stalled every
    /// client's upstream traffic. Published under the lock by every Persist() and at load; the
    /// generation only changes inside a mutation that persists.
    /// </summary>
    public long WorldAuthorityGeneration => Volatile.Read(ref _authorityGeneration);
    private long _authorityGeneration;

    public ClusterLifecycle Lifecycle()
    {
        lock (_lock)
        {
            string[] reasons = DegradedReasons();
            ClusterPhase phase = _state.Phase == ClusterPhase.Serving && reasons.Length != 0 ? ClusterPhase.Degraded : _state.Phase;
            return new ClusterLifecycle(phase, _state.Startup, _state.ShutdownStarted, _state.LastShutdown,
                ExecutorSilent(), GlobalSpawnHalted(), reasons,
                NewestGlobalSaveTime() is { } newest ? (long)(_clock() - newest).TotalSeconds : null);
        }
    }

    /// <summary>T-0237: a cluster stop is draining its nodes (cheap: read on every node heartbeat).</summary>
    public bool ShuttingDown
    {
        get { lock (_lock) return _state.Phase == ClusterPhase.Draining; }
    }

    public ClusterLifecycle BeginShutdown(bool emergency, TimeSpan forceDrainAfter)
    {
        lock (_lock)
        {
            if (_state.Phase == ClusterPhase.Down)
                return Lifecycle();
            _state.Phase = ClusterPhase.Draining;
            if (NodeControlActive) _state.NodeControl.AwaitingStart = true;   // T-0320: no planning until the next start's apply
            Emit("cluster.phase", data: new Dictionary<string, string>
            {
                ["phase"] = nameof(ClusterPhase.Draining),
                ["emergency"] = emergency ? "true" : "false",
            });
            _state.ShutdownStarted ??= _clock();
            foreach (NodeRegistration node in _state.Nodes.Values.Where(x => x.State is NodeState.Active or NodeState.Closed))
            {
                _state.ShutdownNodes.Add(NodeKey(node));
                node.State = NodeState.Closed;
                DateTimeOffset deadline = _clock() + (emergency ? TimeSpan.Zero : forceDrainAfter);
                if (node.ForceDrainAt == null || node.ForceDrainAt > deadline) node.ForceDrainAt = deadline;
            }
            foreach (NodeSlotPlan slot in _state.NodePlan.Values) slot.Desired = emergency ? NodeDesiredState.Kill : NodeDesiredState.Draining;
            Persist();
            return Lifecycle();
        }
    }

    public bool ShutdownReady()
    {
        lock (_lock)
            return _state.ShutdownNodes.All(key => _state.Nodes.Values.Any(node => NodeKey(node) == key && node.State is NodeState.Empty or NodeState.Dead))
                && (ShutdownDirtyState(waitable: true).Length == 0 || !AnyLiveNode());
    }

    /// <summary>
    /// #1301: a node that is Active or Closed can still write a final save. Once none is left, nothing the
    /// shutdown waits for can arrive any more (a WA that died before its final global save leaves "global"
    /// waitable forever), so the shutdown completes and the marker names what is missing. Caller holds _lock.
    /// </summary>
    private bool AnyLiveNode() => _state.Nodes.Values.Any(x => x.State is NodeState.Active or NodeState.Closed);

    /// <summary>
    /// #1301: completes a graceful shutdown that no caller will complete. The drain's deadline can pass while a node
    /// is still Closed, the node dies a moment later (octahedron-1191 2026-10-21 08:35: local-node-1 crashed 2 s after
    /// the 90 s deadline, its lease lapsed 14 s later), and the failed request leaves the cluster Draining for good:
    /// Quasar does not retry once no node is active, and an update despite a dirty shutdown (#1296) needs phase Down.
    /// Called every lease pass; once no node is live it completes the shutdown, dirty, exactly as a retried request
    /// would have. Returns whether it did.
    /// </summary>
    public bool CompleteStrandedShutdown()
    {
        lock (_lock)
        {
            if (_state.Phase != ClusterPhase.Draining || AnyLiveNode() || !ShutdownReady()) return false;
            ClusterLifecycle down = CompleteShutdown(allowDirty: false);
            Console.WriteLine("graceful shutdown completed after its drain: no node is Active or Closed any more; dirty: "
                + (down.LastShutdown?.Dirty is { Length: > 0 } dirty ? string.Join(",", dirty.Take(12))
                    + (dirty.Length > 12 ? ",... (" + dirty.Length + ")" : "") : "none") + " (#1301)");
            return true;
        }
    }

    public ClusterLifecycle CompleteShutdown(bool allowDirty)
    {
        lock (_lock)
        {
            if (_state.Phase == ClusterPhase.Down) return Lifecycle();
            string[] dirty = ShutdownDirtyState();
            string[] waitable = ShutdownDirtyState(waitable: true);
            NodeRegistration[] shutdownNodes = _state.Nodes.Values.Where(node => _state.ShutdownNodes.Contains(NodeKey(node))).ToArray();
            // A Dead node never becomes Empty (ShutdownReady already counts it as done), so it cannot be
            // waited for: it is recorded as dirty ("node:<id>" below) and the shutdown completes. Refusing
            // here left the gateway running until the caller's grace ran out and SIGKILLed the cluster
            // (2026-09-24 03:17: a stop 12 s after boot fenced node-1 in its startup claim).
            string[] notEmpty = shutdownNodes.Where(x => x.State is not (NodeState.Empty or NodeState.Dead))
                .Select(x => x.Node + ":" + x.State).ToArray();
            if (!allowDirty && (notEmpty.Length != 0 || waitable.Length != 0 && AnyLiveNode()))
                // Name what is being waited for. On 2026-09-07 21:19 this fired after 600 s with all
                // six nodes Empty and the bare message left the blocker to be reconstructed from
                // four other logs; the entries are the diagnosis.
                throw new RegistryConflictException("Graceful shutdown is waiting for Empty nodes and final saves"
                    + (notEmpty.Length != 0 ? "; nodes not Empty: " + string.Join(",", notEmpty) : "")
                    + (waitable.Length != 0 ? "; dirty: " + string.Join(",", waitable.Take(12)) + (waitable.Length > 12 ? ",... (" + waitable.Length + ")" : "") : ""));
            dirty = dirty.Concat(shutdownNodes.Where(x => x.State != NodeState.Empty).Select(x => "node:" + x.Node)).Distinct().Order().ToArray();
            SaveId[] manifest = _state.Partitions.Keys.Where(x => !IsRetired(x)).Select(ShutdownSave)
                .Where(x => x != null).Select(x => x!.SaveId).ToArray();
            GlobalSaveId? globalRef = ShutdownGlobalSave()?.SaveId;
            VoxelRef[] voxelRefs = VoxelCut();
            var marker = new ShutdownMarker(_clock(), manifest, dirty, globalRef, voxelRefs);
            _state.Snapshots.Add(new SnapshotRegistration("shutdown-" + marker.Timestamp.UtcTicks, SnapshotKind.CleanShutdown,
                marker.Timestamp.UtcTicks, manifest, dirty, globalRef, voxelRefs));
            _state.CleanShutdown = marker;
            _state.LastShutdown = marker;
            _state.Phase = ClusterPhase.Down;
            Emit("cluster.phase", data: new Dictionary<string, string>
            {
                ["phase"] = nameof(ClusterPhase.Down),
                ["dirty"] = dirty.Length.ToString(),
            });
            Persist();
            Checkpoint();
            return Lifecycle();
        }
    }

    public NodeSlotPlan PutNodeSlot(string slotKey, string host, NodeRole role, NodeDesiredState desired)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(slotKey) || string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Node slot key and host are required");
            if (_state.NodePlan.TryGetValue(slotKey, out NodeSlotPlan? slot))
            {
                if (slot.Host == host && slot.Role == role && slot.Desired == desired) return slot;
                slot.Host = host;
                slot.Role = role;
                slot.Desired = desired;
                if (desired != NodeDesiredState.Wanted) slot.RetryAt = null;
            }
            else
                _state.NodePlan[slotKey] = slot = new NodeSlotPlan(slotKey, host, role, desired);
            _state.NodePlanUpdated = _clock();
            Persist();
            return slot;
        }
    }

    public NodeSlotPlan[] NodePlan()
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock();
            bool halted = GlobalSpawnHalted();
            foreach (NodeSlotPlan slot in _state.NodePlan.Values)
                slot.Failures.RemoveAll(x => now - x > SpawnFailureWindow);
            return _state.NodePlan.Values.OrderBy(x => x.SlotKey).Select(slot => slot with
            {
                SpawnAllowed = slot.Desired == NodeDesiredState.Wanted
                    && slot.IncumbentAction == NodeIncumbentAction.None
                    && !halted && (slot.RetryAt == null || slot.RetryAt <= now)
            }).ToArray();
        }
    }

    internal void EnsureLegacyExecutorAllowed()
    {
        lock (_lock)
            if (_state.ManagedDeployment is not null)
                throw new RegistryConflictException("Managed deployments require the fenced executor contract.");
    }

    public ExecutorRegistration ExecutorHeartbeat(string executor, SpawnObservation[] observations)
    {
        EnsureLegacyExecutorAllowed();
        return ApplyExecutorObservations(executor, observations);
    }

    private ExecutorRegistration ApplyExecutorObservations(string executor, SpawnObservation[] observations)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(executor)) throw new ArgumentException("Executor id is required");
            DateTimeOffset now = _clock();
            // Same WAL discipline as Heartbeat: a steady-state "everything still Ready" report each
            // second is volatile freshness (LastHeartbeat/ObservedAt), not durable state - only an
            // actual observation change earns a WAL record.
            bool dirty = false;
            foreach (SpawnObservation report in observations)
            {
                if (!_state.NodePlan.TryGetValue(report.SlotKey, out NodeSlotPlan? slot))
                {
                    // T-0320: a stopped node's slot is dropped once it is gone; a late report of it is not an error.
                    if (HistoryIds.Contains(report.SlotKey)) continue;
                    throw new RegistryConflictException($"Node slot '{report.SlotKey}' is not planned");
                }
                dirty |= NodeControlObservedPid(report);
                if (slot.Observed != report.State || slot.ObservedNode != report.Node || slot.ObservedEpoch != report.Epoch)
                    dirty = true;
                slot.Observed = report.State;
                slot.ObservedNode = report.Node;
                slot.ObservedEpoch = report.Epoch;
                slot.ObservedAt = now;
                if (report.State == SpawnState.Gone && slot.IncumbentAction == NodeIncumbentAction.Kill)
                {
                    dirty = true;
                    slot.IncumbentAction = NodeIncumbentAction.None;
                    slot.IncumbentNode = null;
                    slot.IncumbentEpoch = 0;
                }
                if (report.State == SpawnState.Failed && slot.LastAttemptKey != report.AttemptKey)
                {
                    if (string.IsNullOrWhiteSpace(report.AttemptKey)) throw new ArgumentException("Failed spawn requires an attempt key");
                    slot.LastAttemptKey = report.AttemptKey;
                    slot.LastFailure = report.Failure;
                    slot.Failures.Add(now);
                    slot.Failures.RemoveAll(x => now - x > SpawnFailureWindow);
                    slot.RetryAt = now + SpawnBackoff(slot.Failures.Count);
                    dirty = true;
                }
                else if (report.State == SpawnState.Ready && (slot.RetryAt != null || slot.LastFailure != null))
                {
                    slot.RetryAt = null;
                    slot.LastFailure = null;
                    dirty = true;
                }
            }
            var registration = new ExecutorRegistration(executor, now);
            _state.Executors[executor] = registration;
            if (_state.ManagedDeployment is null) dirty |= NodeControlStep(now, "registry", null, null, [], []);
            if (dirty) Persist();
            return registration;
        }
    }

    internal static TimeSpan SpawnBackoff(int failures) => TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>
    /// T-0264. Ids are never reclaimed while the cluster runs (T-0143; T-0344 frees them all at a cold start), so the
    /// 32-bit space is spent once per run, and a block is spent whole when its node epoch ends. With 2^20 blocks every epoch took exactly one: w33 used
    /// 232 of 4096 in 1.6 days (95/day, exhausted around day 43), while the highest id logged in a block
    /// had a median of 37k and at most 197k. At 2^16 an epoch takes what it mints, rounded up to 64k; the
    /// node generator already prefetches the next block at 75%.
    /// </summary>
    internal const ulong NetworkIdBlockSize = 1UL << 16;

    /// <summary>After the highest block issued, not Count x size: a world started under the old 2^20 size
    /// keeps its blocks, and a count-based start would overlap them.</summary>
    internal static ulong NextNetworkIdBlockStart(IEnumerable<NetworkIdBlock> issued) =>
        issued.Select(x => (ulong)x.End).DefaultIfEmpty(0UL).Max() + 1;

    /// <summary>
    /// T-0344: the only reclaim, and it takes every block at once. Network ids live in process memory only: a node's
    /// id map, pending imports and generator, the handover capture this process carries from a source to a target
    /// (DirectGateway), and the replication layer of a client. Saves hold none (a handover tee is the sector alone),
    /// so a partition loaded from a save mints fresh ids. They DO survive a node's life: a live handover gives the
    /// source's ids to the target (ReplicationNetworkIdPatch) and to the client's view, and a client keeps ids no
    /// node holds any more (client_view_adopt_dropped) - so the blocks of one ended epoch are never provably free
    /// while anything else runs, and nothing is freed then.
    /// At a start of this process with no node row Active or Closed, nothing that runs from here on can receive an
    /// old id: Empty and Dead are final for an incarnation (it is fenced; a new process takes the next epoch), only
    /// an Active or Closed node owns a partition and so can be a handover source, the captures the previous process
    /// carried went with it, and a client reaches a node that registers from now on only by a join (a new
    /// replication layer) or by a handover from such a node. A start that finds a node Active or Closed (a registry
    /// restart under a running cluster, or a crash before the leases ran out) frees nothing; the next clean stop does.
    /// </summary>
    private void ReclaimNetworkIdBlocksAtColdStart()
    {
        if (_state.NetworkIdBlocks.Count == 0 || _state.Nodes.Values.Any(x => x.State is NodeState.Active or NodeState.Closed))
            return;
        int blocks = _state.NetworkIdBlocks.Count;
        ulong next = NextNetworkIdBlockStart(_state.NetworkIdBlocks);
        _state.NetworkIdBlocks.Clear();
        _state.NetworkIdBlocksReclaimed += blocks;
        Persist();
        FlushState(); // before the first registration can draw a block: a crash here leaves the old list, never a half.
        Console.WriteLine($"registry: network_id_blocks reclaimed={blocks} nextStartWas={next} nodes={_state.Nodes.Count}"
            + " (cold start: no node Active or Closed, T-0344)");
    }

    /// <summary>T-0344: 64k blocks still unissued above the highest issued one - what is left until the next cold start.</summary>
    internal static long NetworkIdBlocksFree(IEnumerable<NetworkIdBlock> issued) =>
        (long)((uint.MaxValue + 1UL - Math.Min(NextNetworkIdBlockStart(issued), uint.MaxValue + 1UL)) / NetworkIdBlockSize);

    /// <summary>T-0344: under a tenth of the space left (~6,500 blocks, weeks at 10 blocks/h): plan a clean stop and start.</summary>
    internal const long NetworkIdBlocksLow = (long)((1UL << 32) / NetworkIdBlockSize / 10);

    /// <summary>Active OR Closed (#1302): a draining node still creates entities - antenna proxies for the
    /// partitions that just left it, a recovery it claimed before the stop - and each needs a network id.
    /// Refusing a Closed node threw the 409 out of its game loop and stopped it mid-drain. The block is
    /// recorded like any other and only a cold start with no node Active or Closed frees it (T-0344).</summary>
    public NetworkIdBlock AllocateNetworkIdBlock(string node, long epoch)
    {
        lock (_lock)
        {
            RequireNotTestRejected(node);
            RequireLiveNode(node, epoch);
            ulong start = NextNetworkIdBlockStart(_state.NetworkIdBlocks);
            ulong end = start + NetworkIdBlockSize - 1;
            if (end > uint.MaxValue)
                throw new RegistryConflictException("NetworkId space is exhausted");
            var block = new NetworkIdBlock((uint)start, (uint)end, node, epoch, _clock());
            _state.NetworkIdBlocks.Add(block);
            Persist();
            Flush(); // T-0306: an issued range must never be allocated again after a Registry crash.
            return block;
        }
    }

    /// <summary>
    /// EntityId ranges (Docs/EntityIdRanges.md): the 56-bit unique number is rangeIndex(32) << 24 |
    /// offset(24). Each index is issued exactly once, never reclaimed, never zero; a node draws its
    /// ids sequentially from the range and asks for another when it runs out.
    /// </summary>
    public EntityIdRange AllocateEntityIdRange(string node, long epoch)
    {
        lock (_lock)
        {
            RequireNotTestRejected(node);
            RequireLiveNode(node, epoch); // Active OR Closed, as for network-id blocks (#1302)
            uint index = _state.NextEntityIdRange;
            if (index == 0 || index > EntityIdRange.MaxIndex)
                throw new RegistryConflictException("EntityId range space is exhausted");
            var range = new EntityIdRange(index, node, epoch, _clock());
            _state.NextEntityIdRange = index + 1;
            _state.EntityIdRanges.Add(range);
            Persist();
            Flush(); // T-0306: uniqueness counters are not soft state eligible for coalesced loss.
            return range;
        }
    }

    /// <summary>The index the next POST /registry/entity-id-ranges will issue (Docs/EntityIdRanges.md).</summary>
    public uint NextEntityIdRange()
    {
        lock (_lock)
            return _state.NextEntityIdRange;
    }

    public EntityIdRange[] EntityIdRanges()
    {
        lock (_lock)
            return _state.EntityIdRanges.OrderBy(x => x.Index).ToArray();
    }

    /// <summary>
    /// The EntityId issue log in summary form: how many ranges were issued and the index span they cover,
    /// how many each node took, and only the ranges held by the node incarnations registered now (the
    /// ones still being drawn from). The full log (591 rows on w32) is <see cref="EntityIdRanges"/>.
    /// </summary>
    public object EntityIdRangeSummary()
    {
        lock (_lock)
        {
            var current = _state.Nodes.Values.Select(node => (node.Node, node.Epoch)).ToHashSet();
            return new
            {
                issued = _state.EntityIdRanges.Count,
                firstIndex = _state.EntityIdRanges.Count == 0 ? (uint?)null : _state.EntityIdRanges.Min(x => x.Index),
                lastIndex = _state.EntityIdRanges.Count == 0 ? (uint?)null : _state.EntityIdRanges.Max(x => x.Index),
                byNode = _state.EntityIdRanges.GroupBy(x => x.Node).OrderBy(x => x.Key, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Count()),
                current = _state.EntityIdRanges.Where(x => current.Contains((x.Node, x.Epoch))).OrderBy(x => x.Index).ToArray(),
            };
        }
    }

    public NetworkIdBlock[] NetworkIdBlocks()
    {
        lock (_lock)
            return _state.NetworkIdBlocks.OrderBy(x => x.Start).ToArray();
    }

    public PartitionOwnership[] Partitions()
    {
        lock (_lock)
            return _state.Partitions.Values.OrderBy(x => x.Partition).ToArray();
    }

    /// <summary>The partitions one node incarnation owns.</summary>
    /// <remarks>
    /// The heartbeat used to reach this through <see cref="Partitions"/>, which sorts and copies the
    /// WHOLE partition table before the caller throws away every row that belongs to another node -
    /// per node, per second, under the registry lock (w18 ran 1451 partitions and 5 nodes, so ~7000
    /// sorted rows a second of pure garbage, in front of every Begin, grant and commit that needed
    /// the same lock). Filtering first keeps the same rows in the same order.
    /// </remarks>
    public PartitionOwnership[] PartitionsOwnedBy(string node, long epoch)
    {
        lock (_lock)
            return _state.Partitions.Values.Where(x => x.Node == node && x.Epoch == epoch)
                .OrderBy(x => x.Partition).ToArray();
    }

    /// <summary>The HTTP report: ownership plus whether the partition is a retired lineage husk,
    /// so lanes can tell "unowned because merged away" from "unowned and waiting for recovery".</summary>
    public object[] PartitionsReport(bool summary = false, ulong? only = null)
    {
        lock (_lock)
        {
            IEnumerable<PartitionOwnership> rows = only is not { } one ? _state.Partitions.Values
                : _state.Partitions.TryGetValue(one, out PartitionOwnership? row) ? [row] : Array.Empty<PartitionOwnership>();
            return rows
                // summary: only rows that are not settled history. A retired row matters only while it still
                // says Recovering or blocked; the rest (195k of 196k on w32) are counted by PartitionCounts.
                .Where(x => !summary || !IsRetired(x.Partition) || x.Recovering || _state.HandoverBlocks.ContainsKey(x.Partition))
                .OrderBy(x => x.Partition)
                .Select(x => (object)new
                {
                    x.Partition, x.Node, x.Epoch, x.Generation, x.LeaseExpires, x.Recovering, Retired = IsRetired(x.Partition),
                    Offline = _state.OfflinePartitions.ContainsKey(x.Partition),
                    HandoverBlocked = _state.HandoverBlocks.TryGetValue(x.Partition, out HandoverBlockRecord? block)
                        && block.Attempts >= HandoverValidationAborts
                        ? new { Since = block.FirstAbort, block.Attempts, block.Reason, block.Signature, block.BackoffUntil }
                        : null,
                })
                .ToArray();
        }
    }

    /// <summary>Row counts behind a summary <see cref="PartitionsReport"/>, so a reader of the trimmed list
    /// still knows how many retired rows it left out.</summary>
    public object PartitionCounts()
    {
        lock (_lock)
        {
            int retired = 0, owned = 0, offline = 0, unowned = 0;
            foreach (PartitionOwnership row in _state.Partitions.Values)
            {
                if (IsRetired(row.Partition)) retired++;
                else if (row.Node != null) owned++;
                else if (_state.OfflinePartitions.ContainsKey(row.Partition)) offline++;
                else unowned++;
            }
            return new { total = _state.Partitions.Count, retired, owned, offline, unowned };
        }
    }

    public LineageRecord[] Lineage()
    {
        lock (_lock)
            return _state.Lineage.ToArray();
    }

    public ulong[] AllocatePartitionIds(string node, long epoch, int count)
    {
        lock (_lock)
        {
            RequireRegularActiveNode(node, epoch);
            if (count < 1 || count > 1024)
                throw new ArgumentOutOfRangeException(nameof(count));
            List<ulong> ids = [];
            while (ids.Count < count)
            {
                ulong id = _state.NextPartitionId++;
                if (id == 0)
                    throw new RegistryConflictException("Partition ID space is exhausted");
                if (!_state.Partitions.ContainsKey(id) && !_state.AllocatedPartitionIds.Contains(id))
                    ids.Add(id);
            }
            foreach (ulong id in ids)
                _state.AllocatedPartitionIds.Add(id);
            Persist();
            return ids.ToArray();
        }
    }

    public SaveRegistration RegisterSave(SaveId id, string node, long epoch, SaveKind kind, long sizeBytes, string checksum, long clusterTime, string location, string control = "",
        Dictionary<long, long>? homeStamps = null, int? entities = null)
    {
        lock (_lock)
        {
            NodeRegistration current = RequireNode(node, epoch);
            if (current.State is NodeState.Dead or NodeState.Empty)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} cannot register a {kind} save while {current.State}");
            if (!_state.Partitions.TryGetValue(id.Partition, out PartitionOwnership? owner) || owner.Node != node || owner.Epoch != epoch || owner.Generation != id.Generation)
                throw new RegistryConflictException($"Save {id} is fenced by partition ownership");
            if (id.Sequence < 1 || sizeBytes < 0 || string.IsNullOrWhiteSpace(checksum) || string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("A save requires a positive sequence, non-negative size, checksum, and location");

            string key = Key(id);
            if (TryGetSave(key, out SaveRegistration? old))
            {
                if (old.Kind != kind || old.SizeBytes != sizeBytes || old.Checksum != checksum || old.ClusterTime != clusterTime)
                    throw new RegistryConflictException($"Save {id} is already registered differently");
                if (old.Locations.Contains(location))
                    return Snapshot(old);   // KI-15: never hand out the live registration
                old.Locations.Add(location);
				string again = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
				old.LocationHolders[location] = again;
				if (again == node && !string.IsNullOrWhiteSpace(control)) old.HolderEndpoints[node] = control;
				Touch(old);
                Persist();
                return Snapshot(old);   // KI-15: never hand out the live registration
            }

            RequireNewerSequence($"Save {id}", id.Sequence, SavesOf(id.Partition).Where(x => x.SaveId.Generation == id.Generation)
                .Select(x => x.SaveId.Sequence).DefaultIfEmpty(0).Max());

            var save = new SaveRegistration(id, node, epoch, kind, sizeBytes, checksum, clusterTime, [location], _clock()) { HomeStamps = homeStamps, Entities = entities };
			string holder = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
			save.LocationHolders[location] = holder;
			if (holder == node && !string.IsNullOrWhiteSpace(control)) save.HolderEndpoints[node] = control;
            PutSave(key, save);
            UpdateCollapsed();
            Persist();
            return Snapshot(save);   // KI-15: never hand out the live registration
        }
    }

    /// <summary>
    /// Corrects a catalogued save whose sole physical copy the owning node legitimately rewrote.
    /// Node-3 2026-08-11: a boot-time double claim left the node re-capturing into an
    /// already-catalogued (generation, sequence) slot; the catalog checksum then pointed at bytes
    /// that no longer existed anywhere, poisoning recovery of the partition. Amending is safe
    /// exactly when the caller is the partition's current owner at that generation AND holds the
    /// only copy - no other node's replica is invalidated because there is none.
    /// </summary>
    public SaveRegistration AmendSave(SaveId id, string node, long epoch, long sizeBytes, string checksum, long clusterTime, string location)
    {
        lock (_lock)
        {
            RequireNode(node, epoch);
            // Generation <= owner's: a recovery claim bumps the generation first and then amends the
            // anchor save it is recovering FROM, which sits one generation below the fresh claim.
            if (!_state.Partitions.TryGetValue(id.Partition, out PartitionOwnership? owner) || owner.Node != node || owner.Epoch != epoch || owner.Generation < id.Generation)
                throw new RegistryConflictException($"Save {id} amendment is fenced by partition ownership");
            if (!TryGetSave(Key(id), out SaveRegistration? save))
                throw new RegistryConflictException($"Save {id} is not catalogued; nothing to amend");
            if (save.LocationHolders.Values.Any(holder => holder != node))
                throw new RegistryConflictException($"Save {id} has replicas on other holders and cannot be amended");
            if (sizeBytes < 0 || string.IsNullOrWhiteSpace(checksum) || string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("An amendment requires a non-negative size, checksum, and location");
            var amended = new SaveRegistration(id, node, epoch, save.Kind, sizeBytes, checksum, clusterTime, [location], _clock()) { HomeStamps = save.HomeStamps };
            amended.LocationHolders[location] = node;
            if (save.HolderEndpoints.TryGetValue(node, out string? control)) amended.HolderEndpoints[node] = control;
            PutSave(Key(id), amended);
            UpdateCollapsed();
            Persist();
            return amended;
        }
    }

    /// <summary>
    /// SE-819: a writer that passed its ownership or authority fence may be ahead of the catalogue, never behind it. An
    /// unclean Gateway stop loses up to a flush interval of registrations (Docs/Gateway/StateDurability.md) while the
    /// writer keeps its sequence in memory, so the slots it skips are rows this registry lost. Refusing the jump refused
    /// every later save of that writer as well, down to the Final a graceful shutdown waits for. Caller holds _lock.
    /// </summary>
    private static void RequireNewerSequence(string save, long sequence, long newest)
    {
        if (sequence <= newest)
            throw new RegistryConflictException($"{save} sequence is fenced; expected {newest + 1} or later");
        if (sequence > newest + 1)
            Console.WriteLine($"registry: {save} skips the sequence after {newest}: this registry lost those rows in an unclean stop (SE-819)");
    }

    public GlobalSaveRegistration RegisterGlobalSave(GlobalSaveId id, string node, long epoch, SaveKind kind, long sizeBytes, string checksum, long clusterTime, string location, string control = "")
    {
        lock (_lock)
        {
            NodeRegistration current = RequireNode(node, epoch);
            if (current.State is NodeState.Dead or NodeState.Empty)
                throw new RegistryConflictException($"Node '{node}' epoch {epoch} cannot register a {kind} global save while {current.State}");
            RequireWorldAuthority(node, epoch, id.Generation);
            if (id.Sequence < 1 || sizeBytes < 0 || string.IsNullOrWhiteSpace(checksum) || string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("A global save requires a positive sequence, non-negative size, checksum, and location");

            string key = GlobalKey(id);
            if (_state.GlobalSaves.TryGetValue(key, out GlobalSaveRegistration? old))
            {
                if (old.Kind != kind || old.SizeBytes != sizeBytes || old.Checksum != checksum || old.ClusterTime != clusterTime)
                    throw new RegistryConflictException($"Global save {id} is already registered differently");
                if (old.Locations.Contains(location))
                    return Snapshot(old);   // KI-15: never hand out the live registration
                old.Locations.Add(location);
                string again = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
                old.LocationHolders[location] = again;
                if (again == node && !string.IsNullOrWhiteSpace(control)) old.HolderEndpoints[node] = control;
                Touch(old);
                Persist();
                return Snapshot(old);   // KI-15: never hand out the live registration
            }

            RequireNewerSequence($"Global save {id}", id.Sequence, _state.GlobalSaves.Values.Where(x => x.SaveId.Generation == id.Generation)
                .Select(x => x.SaveId.Sequence).DefaultIfEmpty(0).Max());

            var save = new GlobalSaveRegistration(id, node, epoch, kind, sizeBytes, checksum, clusterTime, [location], _clock());
            string holder = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
            save.LocationHolders[location] = holder;
            if (holder == node && !string.IsNullOrWhiteSpace(control)) save.HolderEndpoints[node] = control;
            _state.GlobalSaves[key] = save;
            Persist();
            return Snapshot(save);   // KI-15: never hand out the live registration
        }
    }

    public VoxelBaseRegistration RegisterVoxelBase(string storageName, long baseVersion, long tailSequence, string node, long epoch,
        long authorityGeneration, long sizeBytes, string checksum, string location)
    {
        lock (_lock)
        {
            NodeRegistration current = RequireActiveNode(node, epoch);
            RequireWorldAuthority(node, epoch, authorityGeneration);
            if (current.LeaseExpires <= _clock() || !WorldAuthorityLive())
                throw new RegistryConflictException($"World Authority generation {authorityGeneration} is fenced");
            if (string.IsNullOrWhiteSpace(storageName) || baseVersion < 0 || tailSequence < 0 || sizeBytes < 0
                || string.IsNullOrWhiteSpace(checksum) || string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("A voxel base requires storage, non-negative versions and size, checksum, and location");
            string key = VoxelBaseKey(storageName, baseVersion);
            if (_state.VoxelBases.TryGetValue(key, out VoxelBaseRegistration? old))
            {
                if (old.TailSequence != tailSequence || old.SizeBytes != sizeBytes
                    || !old.Checksum.Equals(checksum, StringComparison.OrdinalIgnoreCase) || old.Location != location)
                    throw new RegistryConflictException($"Voxel base {storageName}/{baseVersion} is already registered differently");
                return old;
            }
            long latest = _state.VoxelBases.Values.Where(x => x.StorageName == storageName).Select(x => x.BaseVersion).DefaultIfEmpty(-1).Max();
            if (baseVersion <= latest) throw new RegistryConflictException($"Voxel base {storageName}/{baseVersion} is older than {latest}");
            if (_state.VoxelJournals.TryGetValue(storageName, out VoxelJournalRegistration? journal) && tailSequence < journal.TailSeq)
                throw new RegistryConflictException($"Voxel base {storageName}/{baseVersion} cannot precede journal tail {journal.TailSeq}");
            var registration = new VoxelBaseRegistration(storageName, baseVersion, tailSequence, node, epoch, authorityGeneration,
                sizeBytes, checksum, location, _clock());
            _state.VoxelBases[key] = registration;
            Persist();
            return registration;
        }
    }

    public VoxelJournalRegistration ReportVoxelJournal(VoxelRef watermark, string node, long epoch, long authorityGeneration)
    {
        lock (_lock)
        {
            NodeRegistration current = RequireLiveNode(node, epoch);
            RequireWorldAuthority(node, epoch, authorityGeneration);
            if (current.LeaseExpires <= _clock() || !WorldAuthorityLive())
                throw new RegistryConflictException($"World Authority generation {authorityGeneration} is fenced");
            if (string.IsNullOrWhiteSpace(watermark.StorageName) || watermark.BaseVersion < 0 || watermark.TailSeq < 0
                || !_state.VoxelBases.TryGetValue(VoxelBaseKey(watermark.StorageName, watermark.BaseVersion), out VoxelBaseRegistration? basis)
                || watermark.TailSeq < basis.TailSequence)
                throw new ArgumentException("Voxel journal watermark requires a catalogued base and a valid tail");
            if (_state.VoxelJournals.TryGetValue(watermark.StorageName, out VoxelJournalRegistration? old)
                && (watermark.BaseVersion < old.BaseVersion || watermark.TailSeq < old.TailSeq))
                throw new RegistryConflictException($"Voxel journal watermark for {watermark.StorageName} cannot move backward");
            if (old is { AuthorityGeneration: var generation } && generation == authorityGeneration
                && old.BaseVersion == watermark.BaseVersion && old.TailSeq == watermark.TailSeq) return old;
            var registration = new VoxelJournalRegistration(watermark.StorageName, watermark.BaseVersion, watermark.TailSeq,
                node, epoch, authorityGeneration, _clock());
            _state.VoxelJournals[watermark.StorageName] = registration;
            Persist();
            return registration;
        }
    }

    public VoxelBaseRegistration[] VoxelBases()
    {
        lock (_lock) return _state.VoxelBases.Values.OrderBy(x => x.StorageName).ThenBy(x => x.BaseVersion).ToArray();
    }

    /// <summary>
    /// The voxel base catalog as served over HTTP: each registration is augmented with the holder
    /// node's current control endpoint so a node without the base on its own root can fetch it
    /// directly instead of trusting a foreign absolute path.
    /// </summary>
    public object[] VoxelBaseCatalog()
    {
        lock (_lock) return _state.VoxelBases.Values.OrderBy(x => x.StorageName).ThenBy(x => x.BaseVersion)
            .Select(x => (object)new { x.StorageName, x.BaseVersion, x.TailSequence, x.Node, x.Epoch, x.AuthorityGeneration,
                x.SizeBytes, x.Checksum, x.Location, x.Created,
                HolderEndpoint = _state.Nodes.TryGetValue(x.Node, out NodeRegistration? holder) && holder.State == NodeState.Active
                    ? holder.ControlEndpoint : null }).ToArray();
    }

    public VoxelJournalRegistration[] VoxelJournals()
    {
        lock (_lock) return _state.VoxelJournals.Values.OrderBy(x => x.StorageName).ToArray();
    }

    /// <summary>
    /// Records how far a regular node has applied a storage's voxel journal. The World Authority reads these to
    /// pick a truncation cut that no live replica has yet to fetch, so the report is a safety input, not bookkeeping:
    /// within one epoch it must never move backward, while a new epoch may restart lower because the node rebuilt
    /// from a catalogued base whose tail is itself a safe floor.
    /// </summary>
    public VoxelReplicaRegistration ReportVoxelReplica(string storageName, string node, long epoch, long applied)
    {
        lock (_lock)
        {
            RequireRegularActiveNode(node, epoch);
            if (string.IsNullOrWhiteSpace(storageName) || applied < 0)
                throw new ArgumentException("Voxel replica watermark requires a storage and a non-negative applied sequence");
            string key = storageName + "|" + node;
            if (_state.VoxelReplicas.TryGetValue(key, out VoxelReplicaRegistration? old) && old.Epoch == epoch && applied < old.Applied)
                throw new RegistryConflictException($"Voxel replica watermark for {storageName} on {node} cannot move backward within epoch {epoch}");
            var registration = new VoxelReplicaRegistration(storageName, node, epoch, applied, _clock());
            _state.VoxelReplicas[key] = registration;
            Persist();
            return registration;
        }
    }

    /// <summary>
    /// Watermarks of currently admitted, lease-live regular nodes at their current epoch. Rows from dead nodes or
    /// stale epochs are omitted rather than returned: a returning node reloads from the newest catalogued base, so
    /// its old watermark says nothing about what it still needs from the journal.
    /// </summary>
    public VoxelReplicaRegistration[] VoxelReplicas()
    {
        lock (_lock) return _state.VoxelReplicas.Values
            .Where(x => _state.Nodes.TryGetValue(x.Node, out NodeRegistration? node) && node.State == NodeState.Active
                && node.Role == NodeRole.Regular && node.Epoch == x.Epoch && node.LeaseExpires > _clock())
            .OrderBy(x => x.StorageName).ThenBy(x => x.Node).ToArray();
    }

	public SaveRegistration RegisterHolder(SaveId id, string node, long epoch, long sizeBytes, string checksum, string location, string control)
	{
		lock (_lock)
		{
			RequireLiveNode(node, epoch);
			if (!TryGetSave(Key(id), out SaveRegistration? save) || save.SizeBytes != sizeBytes || save.Checksum != checksum)
				throw new RegistryConflictException($"Replica holder metadata does not match save {id}");
			if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(control)) throw new ArgumentException("Replica location and control endpoint are required");
			save.Locations.Add(location);
			string holder = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
			save.LocationHolders[location] = holder;
			if (holder == node) save.HolderEndpoints[node] = control;
			save.CorruptHolders.Remove(holder);
			Touch(save);
			_replicaReservations.Remove(id);
			_replicaCopies++;
			Persist();
			// The caller serialises this AFTER the lock is gone, so it must not
			// be the live registration (KI-15).
			return Snapshot(save);
		}
	}

	public GlobalSaveRegistration RegisterGlobalHolder(GlobalSaveId id, string node, long epoch, long sizeBytes, string checksum, string location, string control)
	{
		lock (_lock)
		{
			RequireLiveNode(node, epoch);
			if (!_state.GlobalSaves.TryGetValue(GlobalKey(id), out GlobalSaveRegistration? save) || save.SizeBytes != sizeBytes || save.Checksum != checksum)
				throw new RegistryConflictException($"Replica holder metadata does not match global save {id}");
			if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(control)) throw new ArgumentException("Replica location and control endpoint are required");
			save.Locations.Add(location);
			string holder = RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id));
			save.LocationHolders[location] = holder;
			if (holder == node) save.HolderEndpoints[node] = control;
			Touch(save);
			_replicaReservations.Remove(GlobalReservationKey(id));
			Persist();
			return Snapshot(save);
		}
	}

	/// <summary>
	/// T-0399: a holder takes back its copy of a global save. Holders were only ever added (RegisterGlobalHolder), so a
	/// copy that tore or rotted after its verify kept counting toward the two holders and was offered as a source; the
	/// node's scrub (PartitionPersistence.ScrubGlobals) finds it and calls this. Drops exactly the location the node
	/// names, and only while it is held by that node - or by its host, when the location is its host's store path (the
	/// same mapping as the register call) - so ReplicaWork sees one usable holder fewer and hands out a fresh copy.
	/// Idempotent: an unknown global, location or holder changes nothing (false).
	/// </summary>
	public bool WithdrawGlobalHolder(GlobalSaveId id, string node, long epoch, string location)
	{
		lock (_lock)
		{
			RequireLiveNode(node, epoch);
			if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("A withdrawn replica names its location");
			if (!_state.GlobalSaves.TryGetValue(GlobalKey(id), out GlobalSaveRegistration? save)
				|| !save.LocationHolders.TryGetValue(location, out string? held)
				|| held != RegisteringHolder(node, location, host => HostStorePath(RunRoot, host, id))) return false;
			save.Locations.Remove(location);
			save.LocationHolders.Remove(location);
			if (!save.LocationHolders.ContainsValue(held)) save.HolderEndpoints.Remove(held);
			Touch(save);
			Persist();
			Console.WriteLine($"registry: global save {id.Generation}/{id.Sequence}: holder {held} withdrew {location} (reported by {node}, T-0399)");
			return true;
		}
	}

    /// <summary>A save registration whose mutable collections are copies.
    ///
    /// KI-15: the query methods below materialise the OUTER sequence under the
    /// lock and then hand the registrations themselves to the HTTP layer, which
    /// serialises them AFTER the lock is gone. Their Locations / LocationHolders
    /// / HolderEndpoints / CorruptHolders are live objects, so a concurrent
    /// RegisterHolder or ReportCorruption throws "Collection was modified"
    /// inside System.Text.Json and the caller gets a 500 - which put every
    /// node's replica pump into a permanent 1/s retry loop and quietly left
    /// every save with a single holder. ToArray() on the outside is not a
    /// snapshot of the inside.</summary>
    private static SaveRegistration Snapshot(SaveRegistration save) => save with
    {
        Locations = [.. save.Locations],
        LocationHolders = new Dictionary<string, string>(save.LocationHolders),
        HolderEndpoints = new Dictionary<string, string>(save.HolderEndpoints),
        CorruptHolders = [.. save.CorruptHolders],
        DeletedLocations = save.DeletedLocations == null ? null : new Dictionary<string, DateTimeOffset>(save.DeletedLocations),
    };

    private static GlobalSaveRegistration Snapshot(GlobalSaveRegistration save) => save with
    {
        Locations = [.. save.Locations],
        LocationHolders = new Dictionary<string, string>(save.LocationHolders),
        HolderEndpoints = new Dictionary<string, string>(save.HolderEndpoints),
    };

    public ReplicaDirective[] ReplicaWork(string node, long epoch)
	{
		lock (_lock)
		{
			NodeRegistration requester = RequireLiveNode(node, epoch);
			// A draining (Closed) node takes no NEW partition replica work: it is leaving (T-0217).
			// Globals need no grant, so it still takes those.
			// T-0323: every copy spends the cluster's byte budget (ReplicaPacing.cs); with it spent nobody is handed a
			// partition save, and the queue is not even walked. The best-ranked save goes out first once it refills.
			DateTimeOffset now = _clock();
			SampleReplicaAlarm(now);
			SaveRegistration? save = requester.State != NodeState.Active || !_replicaBudget.Available(ReplicaBytesPerSecond, now) ? null : NextReplica(node);
			// A copy that only moves the save off gone holders waits for the evacuation budget too (it ranks last,
			// so nothing more urgent is waiting behind it).
			bool evacuation = save != null && IsEvacuation(save);
			if (evacuation && !_evacuateBudget.Available(EvacuateBytesPerSecond, now)) save = null;
			if (save != null)
			{
				// Live holders by their own endpoint; a gone holder (its process is gone) through a proxy on its host (T-0323).
				HolderSource[] sources = ReplicaSources(save);
				if (sources.Length != 0)
				{
					_replicaBudget.Spend(save.SizeBytes);
					if (evacuation) _evacuateBudget.Spend(save.SizeBytes);
					if (_replicaReservations.Count > 4096)
						foreach (SaveId stale in _replicaReservations.Where(x => x.Value.Until <= _clock()).Select(x => x.Key).ToArray())
							_replicaReservations.Remove(stale);
					_replicaReservations[save.SaveId] = (node, _clock() + ReplicaReservation);
					return [new ReplicaDirective(save.SaveId, save.SizeBytes, save.Checksum, sources)];
				}
			}
			// Global saves replicate the same way: without a second holder the World Authority's
			// checkpoint dies with its host, and a replacement WA on a fresh root has no source.
			// T-0340: a host-held global save is judged by hosts and served by a live node of a holding host (HostStore.cs).
			// T-0397: one node copies a global at a time (a reservation, as for partition saves) and the copies spend their
			// own byte budget. Without both, every live node took the same newest under-held global at once: after one node
			// retired at 10:46:47 (2026-10-04, r91) the nodes made ~10,000 copies of ~1,000 under-held globals (~2.9 GB) in
			// 6 min, and every serving node on both hosts dropped single sim seconds every ~8 s for those 6 min.
			HashSet<GlobalSaveId>? required = GlobalReplicasRequired();
			GlobalSaveRegistration? global = _state.GlobalSaves.Values.Where(item => !item.PendingDelete
				&& (required == null || required.Contains(item.SaveId))
				&& !GlobalReservedByOther(item.SaveId, node, now)
				&& (_hostStore && HeldByHost(item.LocationHolders) ? HostNeedsGlobal(item, node)
					: !item.LocationHolders.Values.Contains(node)
					&& item.LocationHolders.Values.Where(GlobalHolderUsable).Distinct().Count() < 2))
				.OrderByDescending(item => item.SaveId.Generation).ThenByDescending(item => item.SaveId.Sequence).FirstOrDefault();
			if (global == null || !_globalBudget.Available(GlobalBytesPerSecond, now)) return [];
			HolderSource[] globalSources = _hostStore && HeldByHost(global.LocationHolders) ? HostGlobalSources(global)
				: global.LocationHolders.Values.Distinct().Where(holder => GlobalHolderUsable(holder)
				&& global.HolderEndpoints.ContainsKey(holder)).Select(holder => new HolderSource(holder, global.HolderEndpoints[holder])).ToArray();
			if (globalSources.Length == 0) return [];
			_globalBudget.Spend(global.SizeBytes);
			_replicaReservations[GlobalReservationKey(global.SaveId)] = (node, now + ReplicaReservation);
			return [new ReplicaDirective(new SaveId(0, global.SaveId.Generation, global.SaveId.Sequence), global.SizeBytes, global.Checksum, globalSources, Global: true)];
		}
	}

	private (int Globals, int Snapshots, GlobalSaveId? Shutdown, int Newest, HashSet<GlobalSaveId>? Set) _globalRequired;

	/// <summary>
	/// T-0397 (c): the global saves that must keep two usable node holders, or null = all of them
	/// (<see cref="GlobalReplicaNewest"/> = 0, today). Otherwise the newest N, every snapshot's global reference and the
	/// last clean shutdown's. Under the registry lock; rebuilt only when the catalogue or the snapshot list changed (both
	/// only grow, so their counts are the key), not on every node's poll.
	/// </summary>
	private HashSet<GlobalSaveId>? GlobalReplicasRequired()
	{
		int newest = GlobalReplicaNewest;
		if (newest <= 0) return null;
		var key = (_state.GlobalSaves.Count, _state.Snapshots.Count, _state.LastShutdown?.GlobalRef, newest);
		if (_globalRequired.Set != null && (_globalRequired.Globals, _globalRequired.Snapshots, _globalRequired.Shutdown, _globalRequired.Newest) == key)
			return _globalRequired.Set;
		HashSet<GlobalSaveId> set = _state.GlobalSaves.Values.Where(item => !item.PendingDelete).Select(item => item.SaveId)
			.OrderByDescending(id => id).Take(newest).ToHashSet();
		foreach (SnapshotRegistration snapshot in _state.Snapshots)
			if (snapshot.GlobalRef is GlobalSaveId reference) set.Add(reference);
		if (_state.LastShutdown?.GlobalRef is GlobalSaveId shutdown) set.Add(shutdown);
		_globalRequired = (key.Item1, key.Item2, key.Item3, key.Item4, set);
		return set;
	}

	/// <summary>T-0397: global copies share the reservation map; partition 0 is never a partition save's id.</summary>
	private static SaveId GlobalReservationKey(GlobalSaveId id) => new(0, id.Generation, id.Sequence);

	private bool GlobalReservedByOther(GlobalSaveId id, string node, DateTimeOffset now) =>
		_replicaReservations.TryGetValue(GlobalReservationKey(id), out (string Node, DateTimeOffset Until) reservation)
		&& reservation.Node != node && reservation.Until > now;

	/// <summary>
	/// The save <paramref name="node"/> should copy next, among those with fewer than two usable holders.
	/// Ordered by exposure, not by SaveId: SaveId.CompareTo ranks generation then sequence and ignores the
	/// partition, so "newest first" always served busy partitions' high generations and a quiet
	/// partition's first save (generation 1, sequence 1) ranked last of all. Replication runs at 85-91 %
	/// of the save rate, so those were never reached: 48 % of partitions whose latest save was 1-3 h old
	/// had no second copy, and a drain that released two of them left recovery claiming saves nobody
	/// could serve, 45 times in 36 s (T-0222, 2026-09-23 06:44). Now: a save whose holder is draining
	/// first, then a partition's newest save ahead of the older ones it supersedes, then a LIVE
	/// partition's ahead of a retired one's (the registry keeps ~181k retired partitions, whose saves
	/// made up most of the 38k single-holder newest saves at 11:44), then a partition
	/// with NO replicated save at all ahead of one whose older save already has two holders, then the
	/// most recent first. Oldest-first starved every save made after the 07:03 deploy behind the
	/// backlog; plain newest-first spent the copies (~163/min) on busy partitions' every new save
	/// (~170/min), all of which already had a replicated predecessor, and left quiet live partitions
	/// with a single copy: 8 of 16 whose latest save was 5+ min old (10:24). Unprotected partitions
	/// arrive at the rate of new and split partitions, well below the copy rate. The order is kept
	/// incrementally in ReplicaQueue.cs (T-0227).
	/// </summary>
	/// <summary>
	/// Saves handed to a replicator and not yet registered back, so the next poller is not handed the
	/// same one. Without it every polling node got the same top-ranked save: at 11:44 on e391945 node-1
	/// and node-4 were both directed to 181545:1:1, and the registry held 34,047 saves with FIVE holders
	/// and 8,681 with four while 38,656 had one (T-0222). Memory only; an expired or lost reservation
	/// just makes the save eligible again. NOTE: this makes GET /registry/replicas state-changing - a
	/// manual probe of it reserves the answer for the node it named.
	/// </summary>
	private readonly Dictionary<SaveId, (string Node, DateTimeOffset Until)> _replicaReservations = [];
	private static readonly TimeSpan ReplicaReservation = TimeSpan.FromSeconds(60);

	private static bool Better(ReplicaRank a, ReplicaRank b) =>
		a.Evacuate != b.Evacuate ? !a.Evacuate
		: a.AtRisk != b.AtRisk ? a.AtRisk : a.Newest != b.Newest ? a.Newest : a.Live != b.Live ? a.Live
		: a.Exposed != b.Exposed ? a.Exposed
		// Exposed saves are few (new and split partitions), so the freshest first. A covered one waits
		// oldest-first: busy partitions add a covered save every minute, and newest-first left a quiet
		// partition's newest save single-copied for 28 min behind them (2026-09-23 12:57, 2550e87).
		: a.Exposed ? a.Created > b.Created : a.Created < b.Created;

	private int UsableHolders(SaveRegistration candidate)
	{
		int count = 0, index = 0;
		foreach (string holder in candidate.LocationHolders.Values)
			if (!RepeatedHolder(candidate.LocationHolders, holder, index++) && !candidate.CorruptHolders.Contains(holder) && HolderUsable(holder)) count++;
		return count;
	}

	// A holder with two locations counts once. Values.Distinct() allocated a HashSet per save, and the
	// ranking runs over every catalogued save (195k on w32): 43.5 MB per ranking under the registry lock
	// (T-0227, 2026-09-23). A save has a handful of holders, so an earlier-duplicate scan over the struct
	// enumerator is cheaper and allocates nothing.
	private static bool RepeatedHolder(Dictionary<string, string> holders, string holder, int index)
	{
		int position = 0;
		foreach (string earlier in holders.Values)
		{
			if (position++ == index) return false;
			if (string.Equals(earlier, holder, StringComparison.Ordinal)) return true;
		}
		return false;
	}

	public void ReportCorruption(SaveId id, string holder, string reporter, long reporterEpoch)
	{
		lock (_lock)
		{
			RequireLiveNode(reporter, reporterEpoch);
			SaveRegistration save = (TryGetSave(Key(id), out SaveRegistration? found) ? found : null) ?? throw new ArgumentException($"Save {id} does not exist");
			if (save.CorruptHolders.Add(holder)) Persist();
			Touch(save);
		}
	}

    public HandoverRecord Begin(string id, ulong partition, string sourceNode, long sourceEpoch, long sourceGeneration, string targetNode, long targetEpoch)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(id) || sourceNode == targetNode)
                throw new ArgumentException("Handover requires an id and different source and target nodes");
            NodeRegistration source = RequireNode(sourceNode, sourceEpoch);
            if (source.State is not (NodeState.Active or NodeState.Closed))
                throw new RegistryConflictException($"Node '{sourceNode}' epoch {sourceEpoch} cannot source a handover while {source.State}");
            NodeRegistration target = RequireRegularActiveNode(targetNode, targetEpoch);
            if (_state.SpareNodes.Contains(targetNode))
                throw new RegistryConflictException($"Node '{targetNode}' is a hot spare; activate it before targeting it");
            // #1277 (T-0296 F34, T-0327 class): the one entry of every handover - automatic pass, drain catch-up, pull
            // demand, operator /handover - refuses a node whose lease is lapsed and held by a network window. Such a node
            // is unreachable; a partition committed into it rolls back when the window outcome closes it Dead.
            DateTimeOffset now = _clock();
            foreach (NodeRegistration end in new[] { source, target })
                if (LapsedHeld(end, now))
                    throw new RegistryConflictException($"Node '{end.Node}' epoch {end.Epoch} has a lapsed lease held by a network window; "
                        + "no handover into or out of it until it heartbeats again");
            if (_state.Handovers.TryGetValue(id, out HandoverRecord? old))
            {
                if (old.Partition == partition && old.SourceNode == sourceNode && old.SourceEpoch == sourceEpoch && old.SourceGeneration == sourceGeneration && old.TargetNode == targetNode && old.TargetEpoch == targetEpoch)
                    return old;
                throw new RegistryConflictException($"Handover '{id}' is already recorded differently");
            }
            RequireOwner(partition, sourceNode, sourceEpoch, sourceGeneration);
            if (HandoversOf(partition).Any(x => x.State is HandoverState.Begun or HandoverState.Active))
                throw new RegistryConflictException($"Partition {partition} already has an incomplete handover");
            var handover = new HandoverRecord(id, partition, sourceNode, sourceEpoch, sourceGeneration, targetNode, targetEpoch, HandoverState.Begun, null);
            _state.Handovers[id] = handover;
            IndexHandover(handover);
            Persist();
            return handover;
        }
    }

    public TransferGrant IssueGrant(GrantRequest request)
    {
        lock (_lock)
        {
            string? grantHost = null;
            if (request.Operation == "handover")
            {
                HandoverRecord handover = GetRequiredHandover(request.TransferId);
                NodeRegistration requester = RequireNode(request.Requester, request.RequesterEpoch);
                if (requester.State is NodeState.Dead or NodeState.Empty)
                    throw new RegistryConflictException("Handover grant requester is not live");
                RequireActiveNode(request.Receiver, handover.TargetEpoch);
                if (handover.Partition != request.Partition || handover.SourceGeneration != request.Generation
                    || handover.SourceNode != request.Requester || handover.SourceEpoch != request.RequesterEpoch || handover.TargetNode != request.Receiver
                    || handover.State is not (HandoverState.Begun or HandoverState.Active))
                    throw new RegistryConflictException("Handover grant does not match the registry transaction");
            }
            else if (request.Operation == "save")
            {
                // Active OR Closed, like RegisterHolder that completes the same fetch: ReplicaWork gives
                // new work to Active nodes only, but a directive fetched a moment before the close still
                // needs its grant - a whole-cluster stop closed every node inside that gap (T-0217,
                // 2026-09-23 02:36:41, 4 of 5 nodes).
                RequireLiveNode(request.Requester, request.RequesterEpoch);
                // T-0340: a host-held save is granted to the host; no single receiver is checked (HostStore.cs).
                grantHost = HostOfGrant(request);
            }
            if (request.Operation == "save" && grantHost == null)
            {
                // The holder only serves bytes: a Closed (draining) or Empty (drained, lingering to
                // serve its finals) holder is a valid source. Refusing non-Active holders orphaned
                // every drain Final and recovery silently restored a stale Periodic (P1-DRAIN-01,
                // lost character) - the HolderUsable cover fix was defeated here at grant time.
                NodeRegistration holder = RequireNode(request.Receiver, _state.Nodes.GetValueOrDefault(request.Receiver)?.Epoch ?? 0);
                if (holder.State == NodeState.Dead)
                    throw new RegistryConflictException($"Save grant holder '{request.Receiver}' is Dead");
                SaveId id = new(request.Partition, request.Generation, request.Sequence);
                SaveRegistration save = (TryGetSave(Key(id), out SaveRegistration? found) ? found : null)
                    ?? throw new RegistryConflictException($"Save {id} is not catalogued");
                if (request.Location != null)
                {
                    // T-0323: a proxied fetch. The receiver is a live node that serves a GONE holder's file on its own
                    // host; the grant binds the one catalogued location, which is all the proxy will read.
                    if (holder.State is not (NodeState.Active or NodeState.Closed) || holder.Role != NodeRole.Regular
                        || !save.Locations.Contains(request.Location)
                        || !save.LocationHolders.TryGetValue(request.Location, out string? goneHolder) || save.CorruptHolders.Contains(goneHolder)
                        // T-0340 step 6: or a holder whose row was released - its host is the alias map's.
                        || (GoneHolder(goneHolder) is { } gone ? HostKey(gone) : ReleasedHolderHost(goneHolder)) is not { } fileHost
                        || !string.Equals(fileHost, HostKey(holder), StringComparison.OrdinalIgnoreCase))
                        throw new RegistryConflictException($"Save grant receiver '{request.Receiver}' is not a proxy for that location of save {id}");
                }
                else if (!save.LocationHolders.Values.Contains(request.Receiver) || save.CorruptHolders.Contains(request.Receiver))
                    throw new RegistryConflictException("Save grant receiver is not a usable holder");
            }
            else if (request.Operation is not ("save" or "handover")) throw new ArgumentException("Unknown transfer grant operation");
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var grant = new TransferGrant(token, request.Operation, request.TransferId, request.Partition, request.Generation,
                request.Sequence, request.Receiver, _clock() + TimeSpan.FromMinutes(1), request.Operation == "save" ? request.Location : null,
                grantHost, grantHost == null ? null : NextHostServers(grantHost));
            _grants[token] = grant;
            return grant;
        }
    }

    public void ValidateGrant(GrantValidation request)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(request.Token, out TransferGrant? grant) || grant.Expires <= _clock()
                || grant.Operation != request.Operation || grant.TransferId != request.TransferId || grant.Partition != request.Partition
                || grant.Generation != request.Generation || grant.Sequence != request.Sequence
                // T-0340: a host grant is not bound to one receiver, and its server may name no location (it computes
                // the store path itself); a location it does name must be the granted one.
                || (grant.Host != null ? request.Location != null && !string.Equals(grant.Location, request.Location, StringComparison.Ordinal)
                    : grant.Receiver != request.Receiver
                // T-0323: a proxied grant serves only the location it binds; a plain grant serves no location at all.
                || !string.Equals(grant.Location, request.Location, StringComparison.Ordinal)))
                throw new RegistryConflictException("Transfer grant is absent, expired, consumed, or scoped differently");
            // A save grant's receiver is the serving holder, which may be Closed or Empty (see
            // IssueGrant); every other operation's receiver must still be Active. State-check
            // before consuming so a refused validation does not burn the one-time token.
            NodeRegistration receiver = RequireNode(request.Receiver, request.ReceiverEpoch);
            if (grant.Host != null && !MayServeHostGrant(receiver, grant.Host))
                throw new RegistryConflictException($"Grant receiver '{request.Receiver}' is not a live node of host '{grant.Host}'");
            if (request.Operation == "save" ? receiver.State == NodeState.Dead : receiver.State != NodeState.Active)
                throw new RegistryConflictException($"Grant receiver '{request.Receiver}' is {receiver.State}");
            _grants.Remove(request.Token);
        }
    }

    public HandoverRecord Activate(string id)
    {
        lock (_lock)
        {
            HandoverRecord handover = GetRequiredHandover(id);
            if (handover.State == HandoverState.Active)
                return handover;
            if (handover.State != HandoverState.Begun)
                throw new RegistryConflictException($"Handover '{id}' is {handover.State}");
            RequireActiveNode(handover.TargetNode, handover.TargetEpoch);
            RequireOwner(handover.Partition, handover.SourceNode, handover.SourceEpoch, handover.SourceGeneration);
            handover.State = HandoverState.Active;
            Touch(handover);
            Persist();
            return handover;
        }
    }

    public HandoverRecord Commit(string id)
    {
        lock (_lock)
        {
            HandoverRecord handover = GetRequiredHandover(id);
            if (handover.State == HandoverState.Committed)
                return handover;
            if (handover.State != HandoverState.Active)
                throw new RegistryConflictException($"Handover '{id}' is {handover.State}");
            RequireActiveNode(handover.TargetNode, handover.TargetEpoch);
            RequireOwner(handover.Partition, handover.SourceNode, handover.SourceEpoch, handover.SourceGeneration);
            long generation = handover.SourceGeneration + 1;
            DateTimeOffset expires = _clock() + (_leaseTtl > HandoverFinalizationLease ? _leaseTtl : HandoverFinalizationLease);
            _state.Nodes[handover.TargetNode].LeaseExpires = expires;
            _state.Partitions[handover.Partition] = new PartitionOwnership(handover.Partition, handover.TargetNode, handover.TargetEpoch, generation, expires);
            EmitPartition("claimed", handover.Partition, handover.TargetNode, handover.TargetEpoch, generation, "handover-commit");
            handover.CommittedGeneration = generation;
            handover.State = HandoverState.Committed;
            Touch(handover);
            ClearHandoverBlockLocked(handover.Partition, "committed-handover");
            Persist();
            return handover;
        }
    }

    public HandoverRecord Abort(string id)
    {
        lock (_lock)
        {
            HandoverRecord handover = GetRequiredHandover(id);
            if (handover.State == HandoverState.Aborted)
                return handover;
            if (handover.State == HandoverState.Committed)
                throw new RegistryConflictException($"Handover '{id}' is committed");
            RequireOwner(handover.Partition, handover.SourceNode, handover.SourceEpoch, handover.SourceGeneration);
            handover.State = HandoverState.Aborted;
            Touch(handover);
            Persist();
            return handover;
        }
    }

    /// <summary>Orchestration §3.3 default: consecutive same-signature validation aborts before a partition blocks.</summary>
    internal static readonly int HandoverValidationAborts =
        int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_HANDOVER_VALIDATION_ABORTS"), out int aborts) && aborts > 0 ? aborts : 2;

    /// <summary>
    /// Records one Partitioning §6.10 validation abort. Alarm-level once the same-signature count reaches
    /// the threshold: the partition is HandoverBlocked, scheduling backs off 1 min doubling to 1 h, and the
    /// policy layer moves the counterpart of any pending co-location instead.
    /// </summary>
    public void RecordValidationAbort(ulong partition, string signature, string reason, long[]? entities = null)
    {
        lock (_lock)
        {
            if (!_state.HandoverBlocks.TryGetValue(partition, out HandoverBlockRecord? block))
            {
                block = new HandoverBlockRecord(partition, signature, reason) { FirstAbort = _clock() };
                _state.HandoverBlocks[partition] = block;
            }
            else if (block.Signature != signature)
            {
                // A drifting signature is the same failure episode, not a fresh finding: a pose-shaped
                // residual changes signature every capture, and restarting the count on each one kept a
                // doomed handover churning forever (report 2026-08-09 §4). The attempt count carries on;
                // only a committed handover or the admin trigger starts the partition's slate clean.
                Console.WriteLine($"handover abort signature drifted partition={partition} {block.Signature}->{signature}; attempts continue");
                block.Signature = signature;
            }
            block.Attempts++;
            block.Reason = reason;
            block.LastAbort = _clock();
            // SE-1134: from a node that names them; an older node leaves the last known set.
            if (entities is { Length: > 0 }) block.Entities = entities;
            Console.WriteLine($"handover validation abort partition={partition} signature={signature} attempts={block.Attempts} reason={reason}");
            if (block.Attempts >= HandoverValidationAborts)
            {
                double backoffSeconds = Math.Min(60 * Math.Pow(2, block.Attempts - HandoverValidationAborts), 3600);
                block.BackoffUntil = _clock() + TimeSpan.FromSeconds(backoffSeconds);
                Console.WriteLine($"ALARM: partition {partition} is HandoverBlocked, signature {signature}, "
                    + $"attempts {block.Attempts}, backoff {backoffSeconds:F0}s, reason {reason}");
                Emit("alarm.handover-blocked", partition.ToString(), new Dictionary<string, string>
                {
                    ["signature"] = signature,
                    ["attempts"] = block.Attempts.ToString(),
                    ["reason"] = reason,
                });
            }
            Persist();
        }
    }

    /// <summary>Whether the §7 policy layer must not pick this partition as a mover right now.</summary>
    public bool HandoverScheduleBlocked(ulong partition)
    {
        lock (_lock)
            return _state.HandoverBlocks.TryGetValue(partition, out HandoverBlockRecord? block)
                && block.Attempts >= HandoverValidationAborts && block.BackoffUntil > _clock();
    }

    public HandoverBlockRecord[] HandoverBlocks()
    {
        lock (_lock)
            return _state.HandoverBlocks.Values.OrderBy(x => x.Partition).ToArray();
    }

    /// <summary>The admin trigger of Orchestration §3.3: clears a block without waiting for a handover.</summary>
    public bool ClearHandoverBlock(ulong partition)
    {
        lock (_lock)
        {
            if (!_state.HandoverBlocks.Remove(partition))
                return false;
            Console.WriteLine($"handover block cleared partition={partition} by=admin");
            Persist();
            return true;
        }
    }

    private void ClearHandoverBlockLocked(ulong partition, string cause)
    {
        if (_state.HandoverBlocks.Remove(partition))
            Console.WriteLine($"handover block cleared partition={partition} by={cause}");
    }

    /// <summary>One prompt retry for a partition whose content changed: the backoff gate opens, the
    /// attempt count stays, so a repeat abort re-blocks immediately at the escalated backoff.</summary>
    public void ReleaseHandoverBackoff(ulong partition, string cause)
    {
        lock (_lock)
        {
            ReleaseHandoverBackoffLocked(partition, cause);
            Persist();
        }
    }

    /// <summary>
    /// SE-1134: a residual entity of the partition's handover block that a lineage op leaves in it, or null when the op
    /// may have taken the residual out (or the block names no entities). A merge survivor keeps all it had; a split or
    /// transfer output keeps the members its list names. A retired input keeps nothing.
    /// </summary>
    private long? ResidualStays(ulong partition, LineageKind kind, ulong[] outputIds, Dictionary<ulong, long[]>? members)
    {
        if (!_state.HandoverBlocks.TryGetValue(partition, out HandoverBlockRecord? block) || block.Entities is not { Length: > 0 } entities
            || !outputIds.Contains(partition))
            return null;
        if (kind == LineageKind.Merge) return entities[0];
        return members != null && members.TryGetValue(partition, out long[]? kept) && kept.Intersect(entities).FirstOrDefault() is var e and not 0
            ? e : null;
    }

    private void ReleaseHandoverBackoffLocked(ulong partition, string cause)
    {
        if (_state.HandoverBlocks.TryGetValue(partition, out HandoverBlockRecord? block) && block.BackoffUntil != null)
        {
            block.BackoffUntil = null;
            Console.WriteLine($"handover backoff released partition={partition} by={cause}; attempts={block.Attempts} retained");
        }
    }

    public HandoverRecord? Get(string id)
    {
        lock (_lock)
            return _state.Handovers.GetValueOrDefault(id);
    }

    public HandoverRecord[] Incomplete()
    {
        lock (_lock)
            return _state.Handovers.Values.Where(x => x.State is HandoverState.Begun or HandoverState.Active).ToArray();
    }

    public HandoverRecord[] PendingReconciliation()
    {
        lock (_lock)
            return _state.Handovers.Values.Where(x => !x.Reconciled && x.State != HandoverState.Aborted).ToArray();
    }

    public bool HasLiveOwnership(ulong partition, string node, long epoch, long generation)
    {
        lock (_lock)
            return _state.Partitions.TryGetValue(partition, out PartitionOwnership? owner)
                && owner.Node == node && owner.Epoch == epoch && owner.Generation == generation
                && _state.Nodes.TryGetValue(node, out NodeRegistration? registration) && registration.Epoch == epoch
                && registration.State is NodeState.Active or NodeState.Closed && NodeLeaseLive(registration, _clock())
                && PartitionLeaseLive(owner, registration, _clock());
    }

    /// <summary>T-0296 round 2: whether the partition's row is still at this node/epoch/generation, whatever its lease.</summary>
    public bool OwnedAt(ulong partition, string node, long epoch, long generation)
    {
        lock (_lock)
            return _state.Partitions.TryGetValue(partition, out PartitionOwnership? owner)
                && owner.Node == node && owner.Epoch == epoch && owner.Generation == generation;
    }

    public bool HasLiveNode(string node, long epoch)
    {
        lock (_lock)
            return _state.Nodes.TryGetValue(node, out NodeRegistration? registration) && registration.Epoch == epoch
                && registration.State is NodeState.Active or NodeState.Closed && NodeLeaseLive(registration, _clock());
    }

    /// <summary>T-0296 r2: a lapsed lease that a network window holds (T-0321 P4, <see cref="LeaseHeld"/>) is live for every
    /// reader that would otherwise act on the node or its partition as gone - the hold must not mean "alive" to ExpireLeases
    /// and "dead" to the handover reconciler or a claim. Caller holds _lock.</summary>
    private bool NodeLeaseLive(NodeRegistration node, DateTimeOffset now) => node.LeaseExpires > now || LeaseHeld(node, now);

    private bool PartitionLeaseLive(PartitionOwnership owner, NodeRegistration holder, DateTimeOffset now) =>
        owner.LeaseExpires > now || holder.Node == owner.Node && holder.Epoch == owner.Epoch && LeaseHeld(holder, now);

    public HandoverRecord FinalizeReconciliation(string id)
    {
        lock (_lock)
        {
            HandoverRecord handover = GetRequiredHandover(id);
            if (handover.State != HandoverState.Committed)
                throw new RegistryConflictException($"Handover '{id}' is {handover.State}");
            if (!handover.Reconciled)
            {
                handover.Reconciled = true;
                Touch(handover);
                Persist();
            }
            return handover;
        }
    }

    public HandoverRecord AbandonReconciliation(string id)
    {
        lock (_lock)
        {
            HandoverRecord handover = GetRequiredHandover(id);
            if (handover.Reconciled || handover.State == HandoverState.Aborted)
                return handover;
            string expectedNode = handover.State == HandoverState.Committed ? handover.TargetNode : handover.SourceNode;
            long expectedEpoch = handover.State == HandoverState.Committed ? handover.TargetEpoch : handover.SourceEpoch;
            long expectedGeneration = handover.State == HandoverState.Committed ? handover.SourceGeneration + 1 : handover.SourceGeneration;
            if (_state.Partitions.TryGetValue(handover.Partition, out PartitionOwnership? owner)
                && owner.Node == expectedNode && owner.Epoch == expectedEpoch && owner.Generation == expectedGeneration)
            {
                owner.Node = null;
                owner.Epoch = 0;
                owner.Generation++;
                owner.LeaseExpires = DateTimeOffset.MinValue;
                Touch(owner);
                EmitPartition("released", handover.Partition, null, 0, owner.Generation, "handover-reconciled");
            }
            if (handover.State != HandoverState.Committed)
                handover.State = HandoverState.Aborted;
            handover.Reconciled = true;
            Touch(handover);
            Persist();
            return handover;
        }
    }

    // Compatibility for the original one-save-id API. New callers should provide a SaveId and metadata.
    public SaveRegistration RegisterSave(string id, string node, long epoch, long generation, ulong[] partitions, string location, DateTimeOffset created)
    {
        ulong[] cover = partitions.Distinct().Order().ToArray();
        if (cover.Length != 1)
            throw new ArgumentException("Persistence saves identify exactly one partition");
        lock (_lock)
        {
            SaveRegistration? existing = _state.Saves.Values.FirstOrDefault(x => x.LegacyId == id);
            if (existing != null)
            {
                if (existing.SaveId.Partition == cover[0] && existing.SaveId.Generation == generation && existing.Locations.SequenceEqual([location]))
                    return Snapshot(existing);   // KI-15: never hand out the live registration
                throw new RegistryConflictException($"Save '{id}' is already registered differently");
            }
            long sequence = _state.Saves.Values.Where(x => x.SaveId.Partition == cover[0] && x.SaveId.Generation == generation)
                .Select(x => x.SaveId.Sequence).DefaultIfEmpty(0).Max() + 1;
            var saveId = new SaveId(cover[0], generation, sequence);
            RegisterSave(saveId, node, epoch, SaveKind.Periodic, 0, id, created.UtcTicks, location);
            // Stamp the legacy id on the LIVE row, not on what the write path returned. Since
            // KI-15 (dd71d1e) that return is a snapshot, so `save.LegacyId = id` was writing to a
            // copy and the catalogued row kept LegacyId null. Everything that identifies a save by
            // its legacy id then missed it: the idempotency lookup above found nothing and
            // registered a SECOND row for every re-registration of the same id, and
            // SaveRegistration.Id fell back to the numeric "partition:generation:sequence" key.
            SaveRegistration live = _state.Saves[Key(saveId)];
            live.LegacyId = id;
            Touch(live);
            Persist();
            return Snapshot(live);   // KI-15: never hand out the live registration
        }
    }

    public LineageRecord RecordLineage(string opId, LineageKind kind, PartitionVersion[] inputs, PartitionVersion[] outputs,
        Dictionary<ulong, long[]>? members, long clusterTime, Dictionary<ulong, long[]>? seated = null)
    {
        lock (_lock)
        {
            if (inputs.Length == 0 || outputs.Length == 0 || string.IsNullOrWhiteSpace(opId))
                throw new ArgumentException("Lineage requires an id, inputs, and outputs");
            if (kind == LineageKind.Split && (members == null || outputs.Any(x => !members.ContainsKey(x.Partition))))
                throw new ArgumentException("Split lineage requires every output's member list");
            if (kind == LineageKind.Merge && members is { Count: > 0 })
                throw new ArgumentException("Merge lineage does not carry member lists");
            foreach (PartitionVersion input in inputs)
                DropOfflineRow(input.Partition, "topology-input");
            var record = new LineageRecord(opId, kind, inputs, outputs, members ?? [], clusterTime, false);
            LineageRecord? old = _lineageByOpId.GetValueOrDefault(opId);
            if (old != null)
            {
                if (old.Kind == kind && old.Inputs.SequenceEqual(inputs) && old.Outputs.SequenceEqual(outputs)
                    && SameMembers(old.Members, members ?? []) && old.ClusterTime == clusterTime)
                    return old;
                throw new RegistryConflictException($"Lineage operation '{opId}' is already recorded differently");
            }
            _state.Lineage.Add(record);
            IndexLineage(record);
            // A recorded lineage retires its inputs just as surely as ApplyLineage does, so it
            // must repair client state the same way - this path (the rotator's retire-empty
            // merge) used to leave any placement naming an input pointing at a partition that
            // no longer exists, which strands the client on an eternal "not currently available"
            // conflict (world 10, six pilots; world 12, fleet-wide).
            RepointClientState(kind, inputs, outputs.Select(x => x.Partition).ToArray(), members ?? [], seated);
            UpdateCollapsed();
            Persist();
            return record;
        }
    }

    public LineageRecord ApplyLineage(string opId, LineageKind kind, string node, long epoch, PartitionVersion[] inputs,
        ulong[] outputIds, Dictionary<ulong, long[]>? members, long clusterTime, Dictionary<ulong, long[]>? seated = null)
    {
        lock (_lock)
        {
            if (inputs.Length == 0 || outputIds.Length == 0 || outputIds.Distinct().Count() != outputIds.Length || string.IsNullOrWhiteSpace(opId))
                throw new ArgumentException("Lineage requires an id and distinct inputs and outputs");
            LineageRecord? old = _lineageByOpId.GetValueOrDefault(opId);
            if (old != null)
            {
                // T-0308: the node retries with the same op id when the reply is lost, and a graceful
                // stop may close it in between. Answer an exact replay from the same incarnation even
                // when Closed: refusing it made the node revert layouts the registry had committed.
                RequireLiveNode(node, epoch);
                if (old.Kind == kind && old.Inputs.SequenceEqual(inputs) && old.Outputs.Select(x => x.Partition).SequenceEqual(outputIds)
                    && SameMembers(old.Members, members ?? []) && old.ClusterTime == clusterTime)
                    return old;
                throw new RegistryConflictException($"Lineage operation '{opId}' is already recorded differently");
            }
            RequireActiveNode(node, epoch);
            foreach (PartitionVersion input in inputs)
                RequireOwner(input.Partition, node, epoch, input.Generation);
            if (kind == LineageKind.Merge && (outputIds.Length != 1 || !inputs.Any(x => x.Partition == outputIds[0]) || members is { Count: > 0 }))
                throw new ArgumentException("Merge output must be one surviving input and carries no member lists");
            // T-0339 (design C): a transfer is the one split with two inputs - see IsTransfer. A registry without it answers
            // this message (HTTP 400), which is what the node falls back to a split and a merge on.
            bool transfer = kind == LineageKind.Split && inputs.Length == 2;
            if (kind == LineageKind.Split && (inputs.Length != 1 && !transfer || members == null || outputIds.Any(x => !members.ContainsKey(x))))
                throw new ArgumentException("Split requires one input and every output's member list");
            if (transfer && (inputs[0].Partition == inputs[1].Partition || outputIds.Length != 2
                    || outputIds[0] != inputs[0].Partition || outputIds[1] != inputs[1].Partition
                    || members!.Values.Any(x => x.Length == 0) || members.Values.SelectMany(x => x).Distinct().Count() != members.Values.Sum(x => x.Length)))
                throw new ArgumentException("A transfer's outputs are its two inputs in input order (source, target), each with a non-empty member list, the lists disjoint");
            foreach (ulong id in outputIds.Where(x => inputs.All(y => y.Partition != x)))
                if (!_state.AllocatedPartitionIds.Contains(id) || _state.Partitions.TryGetValue(id, out PartitionOwnership? allocatedOwner)
                    && (allocatedOwner.Node != node || allocatedOwner.Epoch != epoch || allocatedOwner.Generation != 1))
                    throw new RegistryConflictException($"Partition {id} was not allocated or is already in use");

            DateTimeOffset expires = _clock() + _leaseTtl;
            foreach (PartitionVersion input in inputs)
            {
                PartitionOwnership owner = _state.Partitions[input.Partition];
                owner.Node = null;
                owner.Epoch = 0;
                owner.Generation++;
                owner.LeaseExpires = DateTimeOffset.MinValue;
                Touch(owner);
                EmitPartition("released", input.Partition, null, 0, owner.Generation, "topology-input");
            }
            PartitionVersion[] outputs = outputIds.Select(id =>
            {
                long generation = (inputs.FirstOrDefault(x => x.Partition == id)?.Generation ?? 0) + 1;
                _state.Partitions[id] = new PartitionOwnership(id, node, epoch, generation, expires);
                EmitPartition("claimed", id, node, epoch, generation, "topology-output");
                _state.AllocatedPartitionIds.Remove(id);
                return new PartitionVersion(id, generation);
            }).ToArray();
            foreach (PartitionVersion input in inputs)
                DropOfflineRow(input.Partition, "topology-input");
            var record = new LineageRecord(opId, kind, inputs, outputs, members ?? [], clusterTime, false);
            _state.Lineage.Add(record);
            IndexLineage(record);
            // Orchestration §3.3 amended (report 2026-08-09 §4): a membership change no longer clears a
            // HandoverBlocked state outright - player connects mint a spawn-partition merge, so any
            // busy partition had its block wiped every few minutes and the doomed retry churn never
            // ended. The changed member set does deserve exactly one prompt retry: release the backoff,
            // keep the attempt count, and a repeat abort re-blocks immediately at the escalated backoff.
            // SE-1134: only if the residual's entity has left the partition. Alpha Station (partition 4728, an open
            // maintenance panel) had its backoff released by a trader shuttle's split, and the next pin froze Alpha again.
            foreach (ulong changed in inputs.Select(x => x.Partition).Concat(outputIds).Distinct())
                if (ResidualStays(changed, kind, outputIds, members) is { } entity)
                    Console.WriteLine($"handover backoff kept partition={changed} on membership-change: residual entity {entity} is still in it");
                else
                    ReleaseHandoverBackoffLocked(changed, "membership-change");
            RepointClientState(kind, inputs, outputIds, members ?? [], seated);
            // #1304: only a split re-IDs content that landed. A merge survivor and both sides of a transfer keep their own
            // arrival: octahedron-1191's lane partitions absorbed a just-woken parked grid every 1-2 min, took its arrival
            // each time and stayed residence held for hours (2930 merged 2956 at 11:44:59, 41 s after it came ONLINE).
            if (kind == LineageKind.Split && !transfer) CarryArrival(inputs.Select(x => x.Partition), outputIds);
            UpdateCollapsed();
            Persist();
            return record;
        }
    }

    /// <summary>
    /// A lineage retires its inputs, so every client-side pointer naming an input has to move to
    /// an output or it dangles - the live placement (ClientPartitions) and the character home
    /// (CharacterHomes) alike. Only the placement used to move (w29 2026-09-07, pilot-10): a
    /// seated body reports no home, so the home stayed at the pre-boarding partition, the boarding
    /// merge retired that id, and the rejoin after the next restart resolved the retired id, fell
    /// back to the default backend and spawned a second body while the first sat in the ship's
    /// save - the half-seated pilot that never re-seats (T-0065).
    /// <para>
    /// Merge: everything lands on the surviving output. Split: the output holding the entity, else
    /// the one holding the client's home entity, else the most populated - a miss (entity 0 from
    /// RouteClient's default, a seated character whose own id is not a top-most member, or a body
    /// that moved in the same tick) must not delete a binding, and all outputs live on the same
    /// node, so any output keeps the client routable and the next report refines it (KI-2).
    /// </para>
    /// </summary>
    private void RepointClientState(LineageKind kind, PartitionVersion[] inputs, ulong[] outputIds, Dictionary<ulong, long[]> members,
        Dictionary<ulong, long[]>? seated = null)
    {
        // T-0252: a seated body is not a top-most member - its ship is - so a split used to send a DISCONNECTED
        // pilot's placement and home to the most populated output, and the next report that "refines it" never
        // comes while the pilot is away. w33 2026-09-26 08:51:34: 10343 split into 10579 (2 members) and 10580
        // (Pilot-111's ship with the pilot's seated body); the rejoin at 08:56:47 resolved 10670, 10580 went offline
        // unrequested, the client respawned a second body and lost it to the duplicate election. The node now
        // sends each output's seated pilots beside its members; they are a placement hint only (never a member,
        // so partition weights and save covers are unchanged).
        bool bySeat = false;
        ulong Holding(long entity)
        {
            if (entity == 0) return 0;
            ulong member = outputIds.FirstOrDefault(id => members.TryGetValue(id, out long[]? held) && held.Contains(entity));
            if (member != 0 || seated == null) return member;
            ulong seat = outputIds.FirstOrDefault(id => seated.TryGetValue(id, out long[]? pilots) && pilots.Contains(entity));
            bySeat |= seat != 0;
            return seat;
        }
        // T-0339 (design C): both outputs of a transfer are its inputs, and only the moved members change partition. A
        // client none of whose entities is in either list stays where it is - the most populated output (a split's
        // guess) would move a client of the source to the target for nothing.
        bool transfer = IsTransfer(kind, inputs, outputIds);
        ulong Output(long entity, long homeEntity, ulong current)
        {
            if (kind == LineageKind.Merge) return outputIds[0];
            ulong chosen = Holding(entity);
            if (chosen == 0 && homeEntity != entity) chosen = Holding(homeEntity);
            return chosen != 0 ? chosen : transfer ? current : outputIds.MaxBy(id => members.TryGetValue(id, out long[]? held) ? held.Length : 0);
        }
        foreach ((ulong client, ClientPartition location) in _state.ClientPartitions.ToArray())
        {
            if (!inputs.Any(x => x.Partition == location.Partition)) continue;
            long homeEntity = _state.CharacterHomes.TryGetValue(client, out CharacterHome? home) ? home.Entity : 0;
            bySeat = false;
            _state.ClientPartitions[client] = location with { Partition = Output(location.Entity, homeEntity, location.Partition) };
            if (bySeat) Console.WriteLine($"Lineage: client {client} placement followed its seated body to partition {_state.ClientPartitions[client].Partition} (T-0252)");
        }
        foreach ((ulong client, CharacterHome home) in _state.CharacterHomes.ToArray())
        {
            CharacterHome updated = home;
            if (inputs.Any(x => x.Partition == home.Partition))
            {
                bySeat = false;
                updated = updated with { Partition = Output(home.Entity, home.Entity, home.Partition) };
                if (bySeat) Console.WriteLine($"Lineage: client {client} home body {home.Entity} followed its seat to partition {updated.Partition} (T-0252)");
            }
            // The authorization's executor token stays fixed. Its durable locator follows the
            // candidate (including a seated pilot), so a new owner can take over after a split.
            if (home.PendingReap is { } permit && inputs.Any(x => x.Partition ==
                (home.PendingReapPartition != 0 ? home.PendingReapPartition : permit.Partition)))
                updated = updated with { PendingReapPartition = Output(permit.Entity, permit.Entity,
                    home.PendingReapPartition != 0 ? home.PendingReapPartition : permit.Partition) };
            if (updated != home) _state.CharacterHomes[client] = updated;
        }
    }

    /// <summary>
    /// The partition a character home stands for today: the recorded one while it lives, else the
    /// lineage followed forward through every retirement. A home written before
    /// <see cref="RepointClientState"/> covered homes still names a retired id in the persisted
    /// state, and the join must land on the body, not on the default backend.
    /// </summary>
    private ulong LiveHomePartition(CharacterHome home)
    {
        ulong partition = home.Partition;
        // Every hop is one index lookup, and there is no small hop budget: a pilot away for three
        // days came back to a home 146-351 merges and splits behind the live partition (w29
        // 2026-09-08 06:16, Pilot-011/012/014/016/018/019/022), and a 64-hop cap left every one of
        // them on a retired id - "is not currently available", default backend, a second body, and
        // the registry then despawned the body they had come back for. The visited set is the
        // cycle guard; the count is a sanity bound, not a budget.
        HashSet<ulong> visited = [];
        for (int hop = 0; hop < 1_000_000 && IsRetired(partition) && visited.Add(partition); hop++)
        {
            LineageRecord? retiring = RetiringRecord(partition);
            if (retiring == null) break;
            ulong[] outputIds = retiring.Outputs.Select(x => x.Partition).ToArray();
            ulong next = retiring.Kind == LineageKind.Merge ? outputIds[0]
                : outputIds.FirstOrDefault(id => retiring.Members.TryGetValue(id, out long[]? held) && held.Contains(home.Entity));
            if (next == 0) next = outputIds.MaxBy(id => retiring.Members.TryGetValue(id, out long[]? held) ? held.Length : 0);
            if (next == 0 || next == partition) break;
            partition = next;
        }
        return partition;
    }

    public SaveRegistration? NewestSave(ulong partition)
    {
        lock (_lock)
            return Newest(partition, long.MaxValue);
    }

    /// <summary>One partition's catalogued saves (snapshots), as <see cref="Saves"/> filtered.</summary>
    public SaveRegistration[] SavesFor(ulong partition)
    {
        lock (_lock) return SavesOf(partition).Select(Snapshot).ToArray();
    }

    public SaveRegistration[] Saves()
    {
        lock (_lock)
            return _state.Saves.Values.OrderBy(save => save.SaveId).Select(Snapshot).ToArray();
    }

    public GlobalSaveRegistration? NewestGlobalSave()
    {
        lock (_lock)
        {
            GlobalSaveRegistration? save = NewestGlobal();
            return save == null ? null : Snapshot(save);
        }
    }

    /// <summary>
    /// The global save a (replacement) World Authority restores. T-0230: the newest one whose every
    /// holder is KNOWN DEAD is skipped - a WA that died within a second of a periodic save left one
    /// only it held, and the replacement then retried the dead holder and, on another host, could not
    /// boot at all. That costs at most the saves since the last replicated one. A holder that has not
    /// registered yet (cold start, registry rebuild) is not known dead, so a cold start still restores
    /// the newest save. With no such save, the newest one with any location, as before: a readable
    /// foreign path may still serve it.
    /// </summary>
    /// <param name="without">#1270: the save a replacement would restore if this node died now - the World Authority
    /// asks it before it answers a command its newest save holds, since a save only it holds is not recovered.</param>
    public GlobalSaveRegistration? NewestGlobalSaveForRecovery(string? without = null)
    {
        lock (_lock)
        {
            GlobalSaveRegistration[] candidates = _state.GlobalSaves.Values
                .Where(x => !x.PendingDelete && x.Locations.Count != 0)
                .OrderByDescending(x => x.SaveId).ToArray();
            GlobalSaveRegistration? save = candidates.FirstOrDefault(x => x.Locations.Any(location =>
                !x.LocationHolders.TryGetValue(location, out string? holder) || holder != without && !HolderKnownDead(holder)))
                ?? candidates.FirstOrDefault();
            return save == null ? null : ServedGlobal(save);
        }
    }

    public GlobalSaveRegistration[] GlobalSaves()
    {
        lock (_lock)
            return _state.GlobalSaves.Values.OrderBy(save => save.SaveId).Select(Snapshot).ToArray();
    }

    public SaveCover[] ResolveCover(ulong partition)
    {
        lock (_lock)
            // Snapshot: /registry/saves/resolve serialises this outside the lock (KI-15).
            return ResolveCover(partition, long.MaxValue, null, []).GroupBy(x => (x.Save.SaveId, Filter: x.EntityIds == null ? "*" : string.Join(',', x.EntityIds)))
                .Select(x => x.First() with { Save = Snapshot(x.First().Save) }).ToArray();
    }

    public SaveRegistration[] Cover(IEnumerable<ulong> partitions)
    {
        lock (_lock)
        {
            // Snapshot: the only callers serialise this outside the lock (KI-15).
            SaveRegistration[] saves = partitions.Distinct().SelectMany(x => ResolveCover(x, long.MaxValue, null, []))
                .Select(x => x.Save).DistinctBy(x => x.SaveId).Select(Snapshot).ToArray();
            return saves.Length == 0 && partitions.Any() ? [] : saves;
        }
    }

    public SnapshotRegistration CreateSnapshot(string snapshotId, SnapshotKind kind, long clusterTime, SaveId[] partitionRefs, string[] dirty,
        GlobalSaveId? globalRef = null, VoxelRef[]? voxelRefs = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(snapshotId) || partitionRefs.Any(x => !TryGetSave(Key(x), out SaveRegistration? save) || save.PendingDelete))
                throw new ArgumentException("Snapshot requires an id and catalogued partition references");
            if (globalRef.HasValue && (!_state.GlobalSaves.TryGetValue(GlobalKey(globalRef.Value), out GlobalSaveRegistration? global) || global.PendingDelete))
                throw new ArgumentException("Snapshot global reference is not catalogued");
            voxelRefs = (voxelRefs ?? []).Distinct().OrderBy(reference => reference.StorageName, StringComparer.Ordinal).ToArray();
            if (voxelRefs.Any(reference => !_state.VoxelBases.TryGetValue(VoxelBaseKey(reference.StorageName, reference.BaseVersion), out VoxelBaseRegistration? basis)
                || reference.TailSeq < basis.TailSequence || !_state.VoxelJournals.TryGetValue(reference.StorageName, out VoxelJournalRegistration? journal)
                || reference.TailSeq > journal.TailSeq))
                throw new ArgumentException("Snapshot voxel reference is not catalogued");
            string[] distinctDirty = dirty.Distinct().ToArray();
            var snapshot = new SnapshotRegistration(snapshotId, kind, clusterTime, partitionRefs.Distinct().ToArray(),
                distinctDirty, globalRef, voxelRefs);
            SnapshotRegistration? old = _state.Snapshots.FirstOrDefault(x => x.SnapshotId == snapshotId);
            if (old != null)
            {
                if (JsonSerializer.Serialize(old with { VoxelRefs = old.VoxelRefs ?? [] }) == JsonSerializer.Serialize(snapshot))
                    return old;
                throw new RegistryConflictException($"Snapshot '{snapshotId}' is already recorded differently");
            }
            if (kind is SnapshotKind.Online or SnapshotKind.CleanShutdown)
            {
                List<string> voxelDirty = [];
                VoxelRef[] current = VoxelCut(voxelDirty);
                if (!voxelRefs.SequenceEqual(current) || voxelDirty.Any(item => !distinctDirty.Contains(item)))
                    throw new ArgumentException("Snapshot requires the exact current voxel cut or matching dirty entries");
            }
            _state.Snapshots.Add(snapshot);
            Persist();
            return snapshot;
        }
    }

    public SnapshotRegistration CreateOnlineSnapshot(string snapshotId, long clusterTime)
    {
        lock (_lock)
        {
            List<SaveId> references = [];
            List<string> dirty = [];
            foreach (ulong partition in _state.Partitions.Keys.Where(x => !IsRetired(x)).Order())
            {
                SaveCover[] cover = ResolveCover(partition, long.MaxValue, null, []).ToArray();
                if (cover.Length == 0 || _state.Partitions[partition].Recovering)
                    dirty.Add("partition:" + partition);
                else
                    references.AddRange(cover.Select(x => x.Save.SaveId));
            }
            GlobalSaveId? globalRef = NewestGlobal()?.SaveId;
            if (RequiresGlobalSave() && !globalRef.HasValue) dirty.Add("global");
            VoxelRef[] voxelRefs = VoxelCut(dirty);
            return CreateSnapshot(snapshotId, SnapshotKind.Online, clusterTime, references.Distinct().ToArray(), dirty.ToArray(), globalRef, voxelRefs);
        }
    }

    /// <param name="keepRetired">
    /// How many saves a partition that lineage has already RETIRED keeps. Retired partitions
    /// dominate the catalogue - 1426 of the 1448 that held saves on w18, carrying 87% of the
    /// Saves section - and their content lives on in whatever merge or split consumed them, so
    /// keeping three generations of each is what made the registry grow without bound. One is
    /// kept rather than none as a margin: it costs little and leaves something to recover from
    /// if the lineage walk is ever wrong about a partition being finished.
    /// </param>
    public DeleteDirective[] PlanRetention(int keepN = 3, int snapshotPinKeep = 2, int keepRetired = 1)
    {
        // T-0400: the status count of due archived partitions, read before the lock (a read-only connection).
        long archiveDue = ArchiveRetentionDueCount();
        lock (_lock)
        {
            if (keepN < 1 || snapshotPinKeep < 0 || keepRetired < 1)
                throw new ArgumentOutOfRangeException(nameof(keepN));
            // PORTED-PERF-FIX / SAFETY: never plan retention while no holder is usable.
            //
            // Newest() only accepts a save whose holder is a registered node with a live
            // lease (HolderUsable). At cold start no node has registered yet and every
            // lease has expired, so Newest() returns null for EVERY partition. Two things
            // then go wrong at once:
            //
            //  * ResolveCover falls through to the split/merge lineage walk for every
            //    partition instead of terminating at its anchor, and that walk is
            //    exponential on a real lineage DAG. It runs under _lock, so RegisterNode
            //    blocks behind it - and node registration is precisely what would make a
            //    holder usable. Cold start deadlocks against itself (2026-08-20: five
            //    nodes timed out at 300s, gateway spinning at 101% CPU for 13 minutes,
            //    ResolveCover recursion depth 7 -> 99 between two stack samples).
            //  * The cover contributes nothing to `retained` in that state, so every save
            //    beyond the keepN newest would be marked PendingDelete on the strength of
            //    a walk that could not see a single usable copy.
            //
            // With nodes live this is a no-op: HolderUsable is true and the pass runs as
            // before, which is why w20 ran 5h20m without hitting it.
            // T-0340: with host holders the same guard reads "no host store is serving" - a stopped node's Empty row
            // says nothing about a store.
            if (_hostStore ? !Control.Hosts.Keys.Any(host => StoreState(host) == HostStoreState.Serving) : !_state.Nodes.Keys.Any(HolderUsable))
                return [];
            HashSet<SaveId> retained = _state.Saves.Values.GroupBy(x => x.SaveId.Partition)
                .SelectMany(x => x.OrderByDescending(y => y.SaveId)
                    .Take(IsRetired(x.Key) ? Math.Min(keepRetired, keepN) : keepN))
                .Select(x => x.SaveId).ToHashSet();
            foreach (SaveId id in _state.Snapshots.OrderByDescending(x => x.ClusterTime).Take(snapshotPinKeep).SelectMany(x => x.PartitionRefs))
                retained.Add(id);
            // ONE memo for the whole pass, not one per partition. ResolveCover already memoises its
            // lineage walk, but the memo was allocated fresh per top-level call, so nothing was shared
            // across the partitions here even though they walk the same DAG - and this loop is every
            // partition in the registry. It runs once a minute holding both the rotation gate and the
            // registry lock, so its whole duration is added to any handover that overlaps it.
            Dictionary<(ulong, long, string, bool), List<SaveCover>> retentionMemo = [];
            // What a live partition, or any snapshot, may still recover from. Walked with the archive
            // readable: a live cover that reaches an archived save brings it back (and keeps it).
            HashSet<SaveId> needed = _state.Snapshots.SelectMany(x => x.PartitionRefs).ToHashSet();
            foreach (ulong partition in _state.Partitions.Keys.Where(partition => !IsRetired(partition)).ToArray())
                foreach (SaveCover cover in ResolveCover(partition, long.MaxValue, null, [], memo: retentionMemo))
                    needed.Add(cover.Save.SaveId);
            retained.UnionWith(needed);
            // Retired partitions still in memory keep their covers retained as before (a margin); an
            // archived one is walked from memory only - this loop never deletes it (archive retention
            // decides its files separately, below), so reading it back would change nothing but the heap.
            // Once archived, a partition drops out of this loop, which is what took the pass from
            // ~850 ms under the lock (every one of 195k partitions) to the live set.
            _archiveReadOnly = true;
            try
            {
                foreach (ulong partition in _state.Partitions.Keys.Where(partition => IsRetired(partition) && _savesByPartition.ContainsKey(partition)).ToArray())
                    foreach (SaveCover cover in ResolveCover(partition, long.MaxValue, null, [], memo: retentionMemo))
                        retained.Add(cover.Save.SaveId);
            }
            finally { _archiveReadOnly = false; }
            // #1275: and every save an outstanding registry backup catalogues (RegistryBackups.cs).
            RetainBackupHolds(retained);

            List<DeleteDirective> result = [];
            // T-0400: a row whose files archive retention deleted is history with nothing left to delete; marking it
            // pending would only hold its partition out of the archive forever (no ack can ever remove it).
            foreach (SaveRegistration save in _state.Saves.Values.Where(x => !retained.Contains(x.SaveId) && !x.PendingDelete
                && !(x.Locations.Count == 0 && x.DeletedLocations is { Count: > 0 })))
            {
                save.PendingDelete = true;
                Touch(save);
				result.AddRange(save.Locations.Select(location => new DeleteDirective(save.SaveId, save.LocationHolders.GetValueOrDefault(location, save.Node), location)));
            }
            bool changed = ArchiveRetired(needed) != 0 || result.Count != 0;
            // T-0400: the archive retention pass, in the same pass and lock, against the same `needed`: a partition
            // still archived here is reached by no live cover and no snapshot.
            if (SweepArchivedFiles(needed, archiveDue)) changed = true;
            if (changed)
                Persist();
            return result.ToArray();
        }
    }

    public bool AcknowledgeDelete(SaveId id, string location)
    {
        lock (_lock)
        {
            // T-0400: not in memory - an archived store file retention handed out.
            if (!_state.Saves.TryGetValue(Key(id), out SaveRegistration? save))
                return AcknowledgeArchivedDelete(id, location);
            if (!save.PendingDelete)
                return false;
            if (!save.Locations.Remove(location))
                return false;
			save.LocationHolders.Remove(location);
			Touch(save);
            if (save.Locations.Count == 0)
                RemoveSave(Key(id));
            Persist();
            return true;
        }
    }

    public DeleteDirective[] PendingDeletes(string node, long epoch)
    {
        lock (_lock)
        {
            RequireNode(node, epoch);
			// T-0340: the node's own copies, and its host's while this node has the host's turn (HostStore.cs).
			string? host = HostDeleteTurn(node);
			return _state.Saves.Values.Where(save => save.PendingDelete)
				.SelectMany(save => save.Locations.Where(location => save.LocationHolders.GetValueOrDefault(location, save.Node) is { } holder
						&& (holder == node || host != null && holder == host))
					.Select(location => new DeleteDirective(save.SaveId, node, location)))
				// T-0400: and the host's archived store files archive retention queued.
				.Concat(ArchivedDeletesFor(node, host)).ToArray();
        }
    }

    public object Status()
    {
        lock (_lock)
            return new { lifecycle = Lifecycle(), nodes = _state.Nodes.Values.OrderBy(x => x.Node), nodePlan = NodePlan(), executors = _state.Executors.Values,
                networkIdBlocks = _state.NetworkIdBlocks.Count, networkIdBlocksFree = NetworkIdBlocksFree(_state.NetworkIdBlocks), networkIdBlocksReclaimed = _state.NetworkIdBlocksReclaimed, entityIdRanges = _state.EntityIdRanges.Count, nextEntityIdRange = _state.NextEntityIdRange, partitions = _state.Partitions.Count, saves = _state.Saves.Count, handovers = _state.Handovers.Count,
                incompleteHandovers = _state.Handovers.Values.Count(x => x.State is HandoverState.Begun or HandoverState.Active), lineage = _state.Lineage.Count,
                snapshots = _state.Snapshots.Count, voxelBases = _state.VoxelBases.Count, voxelJournals = _state.VoxelJournals.Count,
                pendingDeletes = _state.Saves.Values.Count(x => x.PendingDelete), offlinePartitions = _state.OfflinePartitions.Count,
                walRecords = _store.Commits, walBytes = _store.WalBytes, storeBytes = _store.DatabaseBytes, storeAudited = _store.AuditedRows, storeAuditRepairs = _store.AuditRepairs,
                replicaQueue = _replicaQueue.Count, hostStore = HostStoreStatus(),
                // T-0323: replica capacity below invalidation rate (ReplicaPacing.cs), as of the last one-minute sample.
                replicaAlarm = new { active = _replicaAlarm.Active, depth = _replicaAlarm.Depth, copiesPerMinute = _replicaAlarm.CopiesPerMinute,
                    etaMinutes = _replicaAlarm.EtaMinutes, intervalMinutes = _replicaAlarm.IntervalMinutes, reason = _replicaAlarm.Reason, since = _replicaAlarm.Since },
                replicaRebuilds = ReplicaRebuilds, replicaAuditRepairs = ReplicaAuditRepairs,
                archivedPartitions = _archivedPartitions.Count, archivedSaves = _store.ArchivedRows, archiveRehydrations = RehydratedPartitions,
                archiveMilliseconds = ArchiveMilliseconds, archiveRetention = ArchiveRetentionStatus() };
    }

    public Admin.ClusterStatus AdminStatus(int connectedClients, bool paused = false)
    {
        lock (_lock)
        {
            ClusterLifecycle lifecycle = Lifecycle();
            WorldAuthority authority = _state.WorldAuthority;
            string[] reasonCodes = AdminReasonCodes(lifecycle, paused);
            return new Admin.ClusterStatus(
                _admission.ClusterId,
                _admission.WorldId,
                Map(lifecycle.Phase),
                Map(lifecycle.Startup),
                lifecycle.ShutdownStarted,
                lifecycle.LastShutdown?.Timestamp,
                lifecycle.ExecutorSilent,
                lifecycle.GlobalSpawnHalted,
                lifecycle.DegradedReasons,
                new Admin.ClusterCounts(
                    connectedClients,
                    _state.Nodes.Count,
                    _state.Partitions.Count,
                    _state.Saves.Count,
                    _state.Handovers.Count,
                    _state.Handovers.Values.Count(x => x.State is HandoverState.Begun or HandoverState.Active),
                    _state.Snapshots.Count,
                    _state.VoxelBases.Count,
                    _state.VoxelJournals.Count,
                    _state.Saves.Values.Count(x => x.PendingDelete),
                    _store.Commits,
                    _store.WalBytes),
                new Admin.WorldAuthorityStatus(authority.Node, authority.Epoch, authority.Generation, authority.LeaseExpires),
                MapNodes(),
                _state.Executors.Values.OrderBy(x => x.Executor)
                    .Select(x => new Admin.ExecutorStatus(x.Executor, x.LastHeartbeat)).ToArray(),
                !paused && AdmissionOpen && lifecycle.Phase is ClusterPhase.Serving or ClusterPhase.Degraded,
                AdminHealth(lifecycle, reasonCodes),
                reasonCodes,
                _clock(), _state.ManagedDeployment?.Revision,
                _state.ManagedDeployment is { } managed && managed.Slots.All(slot =>
                    _state.Nodes.TryGetValue(slot.Node, out var ready) && ready.Serving && ManagedNodeReady(ready)));
        }
    }

    /// <summary>
    /// Stable machine-readable reason codes behind the health severity, one vocabulary for
    /// Quasar's outer state machine: the registry's own degraded reasons plus
    /// lifecycle-level codes. Quasar must never decode internal states or parse messages.
    /// </summary>
    private string[] AdminReasonCodes(ClusterLifecycle lifecycle, bool paused)
    {
        List<string> codes = [.. lifecycle.DegradedReasons];
        if (lifecycle.Phase == ClusterPhase.Bootstrapping) codes.Add("bootstrapping");
        if (lifecycle.Phase == ClusterPhase.Draining) codes.Add("draining");
        if (lifecycle.Phase == ClusterPhase.Down) codes.Add("clusterDown");
        if (paused) codes.Add("paused");
        if (_state.Nodes.Values.Any(x => x.State == NodeState.Dead)) codes.Add("nodeDead");
        if (NetworkIdBlocksFree(_state.NetworkIdBlocks) < NetworkIdBlocksLow) codes.Add("networkIdSpaceLow");
        return codes.Distinct().ToArray();
    }

    private static Admin.AdminHealth AdminHealth(ClusterLifecycle lifecycle, string[] reasonCodes)
    {
        // Severe conditions: the cluster is up but cannot make progress or is losing data
        // durability. Transitional phases are warnings, not failures - an intentionally
        // stopped or starting cluster is not "unhealthy", and Quasar maps its own goal state.
        string[] severe = ["saveFailure", "globalSaveStale", "globalSpawnFailure", "capacityShortfall"];
        if (lifecycle.Phase is ClusterPhase.Serving or ClusterPhase.Degraded
            && reasonCodes.Any(severe.Contains))
            return Admin.AdminHealth.Unhealthy;
        return reasonCodes.Length == 0 ? Admin.AdminHealth.Healthy : Admin.AdminHealth.Warning;
    }

    private Admin.NodeStatus[] MapNodes()
    {
        DateTimeOffset now = _clock();
        return _state.Nodes.Values.OrderBy(x => x.Node).Select(node =>
        {
            NodeSlotPlan? slot = _state.NodePlan.Values.FirstOrDefault(x => x.ObservedNode == node.Node)
                ?? (_state.NodeControl.Live.ContainsKey(node.Node) ? _state.NodePlan.GetValueOrDefault(node.Node) : null);
            return new Admin.NodeStatus(
                node.Node,
                slot?.SlotKey,
                node.Epoch,
                Map(node.Role),
                Map(node.State),
                node.Endpoint,
                node.ControlEndpoint,
                node.Registered,
                node.LeaseExpires,
                node.SimulationFrame,
                node.Quarantined.Length,
                slot?.Host ?? node.Host,
                node.LeaseExpires > now,
                node.Spare || _state.SpareNodes.Contains(node.Node));
        }).ToArray();
    }

    /// <summary>Fleet view: every registered node incarnation with slot identity and lease freshness.</summary>
    public Admin.NodeStatus[] AdminNodes()
    {
        lock (_lock)
            return MapNodes();
    }

    public Admin.WorldAuthorityStatus AdminWorldAuthority()
    {
        lock (_lock)
        {
            WorldAuthority authority = _state.WorldAuthority;
            return new Admin.WorldAuthorityStatus(authority.Node, authority.Epoch, authority.Generation,
                authority.LeaseExpires);
        }
    }

    /// <summary>Read-only partition map for the UI; partition mutations stay internal.</summary>
    public Admin.PartitionSummary[] AdminPartitions()
    {
        lock (_lock)
        {
            HashSet<ulong> quarantined = _state.Nodes.Values.SelectMany(x => x.Quarantined).ToHashSet();
            return _state.Partitions.Values.OrderBy(x => x.Partition).Select(partition => new Admin.PartitionSummary(
                partition.Partition,
                partition.Node,
                partition.Epoch,
                partition.Generation,
                partition.LeaseExpires,
                partition.Recovering,
                quarantined.Contains(partition.Partition),
                IsRetired(partition.Partition),
                _state.OfflinePartitions.ContainsKey(partition.Partition),
                _state.OfflinePartitions.GetValueOrDefault(partition.Partition)?.OfflineGameFrame,
                OfflineGroups(_state.OfflinePartitions.GetValueOrDefault(partition.Partition)?.ProfileSummary),
                HasRemoteUse(partition.Partition) ? _remoteUse[partition.Partition].Users.ToArray() : [])).ToArray();
        }
    }

    public long HandoverConfigRevision { get { lock (_lock) return _state.HandoverConfigRevision; } }

    /// <summary>The document a node applies: {revision, values}.</summary>
    public HandoverConfigDocument HandoverConfig()
    {
        lock (_lock)
            return new HandoverConfigDocument(_state.HandoverConfigRevision, new SortedDictionary<string, string>(_state.HandoverConfig));
    }

    /// <summary>Merges the given keys (a null value removes the key so the node's default returns). Unchanged = no revision bump.</summary>
    public HandoverConfigDocument SetHandoverConfig(IReadOnlyDictionary<string, string?> update)
    {
        lock (_lock)
        {
            List<string> changed = [];
            // T-0400: the registry itself reads retention.archiveFileDays (it deletes files): refused here, before anything
            // changes, rather than only by the nodes' fan-out.
            if (update.TryGetValue(ArchiveFileDaysKey, out string? days) && days != null)
                update = new Dictionary<string, string?>(update) { [ArchiveFileDaysKey] = NormalizeArchiveFileDays(days) };
            foreach ((string key, string? value) in update)
            {
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Handover config key must not be empty");
                if (value == null ? _state.HandoverConfig.Remove(key) : !_state.HandoverConfig.TryGetValue(key, out string? current) || current != value)
                {
                    if (value != null) _state.HandoverConfig[key] = value;
                    changed.Add(key + "=" + (value ?? "<default>"));
                }
            }
            if (changed.Count != 0)
            {
                _state.HandoverConfigRevision++;
                Emit("handover-config.updated", data: new Dictionary<string, string>
                {
                    ["revision"] = _state.HandoverConfigRevision.ToString(),
                    ["changed"] = string.Join(',', changed),
                });
                Console.WriteLine($"handover-config revision={_state.HandoverConfigRevision} changed {string.Join(' ', changed)}");
                Persist();
            }
            return new HandoverConfigDocument(_state.HandoverConfigRevision, new SortedDictionary<string, string>(_state.HandoverConfig));
        }
    }

    public Admin.AdminConfig AdminConfig(TimeSpan leaseTtl)
    {
        lock (_lock)
            return new Admin.AdminConfig(
                _admission.ClusterId,
                _admission.WorldId,
                _admission.ClusterConfigHash,
                _admission.BinaryVersion,
                _admission.ReplicationTypeTableHash,
                _admission.WorldSettingsHash,
                _admission.SyncDistance,
                leaseTtl.TotalSeconds,
                _state.PolicyRevision,
                _state.NodePlan.Values.OrderBy(x => x.SlotKey).Select(slot =>
                    new Admin.AdminSlotSpec(slot.SlotKey, slot.Host, Map(slot.Role), Map(slot.Desired))).ToArray());
    }

    /// <summary>
    /// Compare-and-set update of the registry-owned policy: upserts the given slots and
    /// bumps the revision. A stale expected revision is refused so two admins cannot
    /// silently interleave partial policies.
    /// </summary>
    public long AdminSetSlots(long expectedRevision, Admin.AdminSlotSpec[] slots)
    {
        lock (_lock)
        {
            if (expectedRevision != _state.PolicyRevision)
                throw new RegistryConflictException(
                    $"Policy revision {expectedRevision} is stale; current revision is {_state.PolicyRevision}");
            foreach (Admin.AdminSlotSpec slot in slots)
                PutNodeSlot(slot.SlotKey, slot.Host, MapRole(slot.Role), MapGoal(slot.Goal));
            _state.PolicyRevision++;
            Emit("config.updated", data: new Dictionary<string, string>
            {
                ["revision"] = _state.PolicyRevision.ToString(),
                ["slots"] = string.Join(',', slots.Select(x => x.SlotKey)),
            });
            Persist();
            return _state.PolicyRevision;
        }
    }

    /// <summary>
    /// Provenance-bound force removal (Orchestration §2.2): the caller must name the exact
    /// incarnation the slot currently observes; the executor kills only that process and the
    /// still-wanted slot respawns after it reports the incumbent gone.
    /// </summary>
    public NodeSlotPlan AdminKillNode(string slotKey, string node, long epoch)
    {
        lock (_lock)
        {
            if (!_state.NodePlan.TryGetValue(slotKey, out NodeSlotPlan? slot))
                throw new RegistryConflictException($"Unknown node slot '{slotKey}'");
            if (!string.Equals(slot.ObservedNode, node, StringComparison.Ordinal))
                throw new RegistryConflictException(
                    $"Slot '{slotKey}' observes '{slot.ObservedNode ?? "nothing"}', not '{node}'");
            NodeRegistration current = RequireNode(node, epoch);
            slot.IncumbentNode = node;
            slot.IncumbentEpoch = epoch;
            slot.IncumbentAction = NodeIncumbentAction.Kill;
            _state.NodePlanUpdated = _clock();
            Emit("node.kill-requested", node, new Dictionary<string, string>
            {
                ["epoch"] = epoch.ToString(),
                ["slot"] = slotKey,
                ["state"] = current.State.ToString(),
            });
            Persist();
            return slot;
        }
    }

    private static NodeRole MapRole(Admin.NodeRole value) => value == Admin.NodeRole.WorldAuthority
        ? NodeRole.WA : NodeRole.Regular;

    private static NodeDesiredState MapGoal(Admin.NodeGoal value) => value switch
    {
        Admin.NodeGoal.Draining => NodeDesiredState.Draining,
        Admin.NodeGoal.Kill => NodeDesiredState.Kill,
        _ => NodeDesiredState.Wanted,
    };

    public Admin.SnapshotSummary[] AdminSnapshots()
    {
        lock (_lock)
            return _state.Snapshots.OrderByDescending(x => x.ClusterTime).Select(snapshot => new Admin.SnapshotSummary(
                snapshot.SnapshotId,
                snapshot.Kind switch
                {
                    SnapshotKind.CleanShutdown => Admin.AdminSnapshotKind.CleanShutdown,
                    SnapshotKind.Conversion => Admin.AdminSnapshotKind.Conversion,
                    _ => Admin.AdminSnapshotKind.Online,
                },
                snapshot.ClusterTime,
                snapshot.PartitionRefs.Length,
                snapshot.GlobalRef != null,
                snapshot.VoxelRefs?.Length ?? 0,
                snapshot.Dirty)).ToArray();
    }

    public Admin.NodePlan[] AdminNodePlan()
    {
        lock (_lock)
            return NodePlan().Select(slot => new Admin.NodePlan(
                slot.SlotKey,
                slot.Host,
                Map(slot.Role),
                Map(slot.Desired),
                Map(slot.Observed),
                slot.ObservedNode,
                Map(slot.IncumbentAction),
                slot.IncumbentNode,
                slot.IncumbentEpoch,
                slot.ObservedAt,
                slot.LastAttemptKey,
                slot.LastFailure,
                slot.RetryAt,
                slot.FailureCount,
                slot.FailureAlert,
                slot.SpawnAllowed)).ToArray();
    }

    /// <summary>
    /// Opens a durable admin operation, or replays the one already bound to the idempotency
    /// key. A key reused for a different kind or request fingerprint is a conflict - the
    /// caller must not silently attach a new effect to an old key. Records are WAL-backed
    /// so a retry after a Gateway restart still replays rather than repeats.
    /// </summary>
    public AdminOperationRecord BeginAdminOperation(string kind, string fingerprint, string actor,
        string idempotencyKey, string? target, out bool created)
    {
        lock (_lock)
        {
            created = false;
            AdminOperationRecord? existing = _state.AdminOperations.Values
                .FirstOrDefault(x => x.IdempotencyKey == idempotencyKey);
            if (existing != null)
            {
                if (existing.Kind != kind || existing.Fingerprint != fingerprint)
                    throw new RegistryConflictException(
                        $"Idempotency key is already bound to a different {existing.Kind} request");
                return existing;
            }
            created = true;
            if (_state.AdminOperations.Count >= 1024)
            {
                AdminOperationRecord evicted = _state.AdminOperations.Values
                    .Where(x => x.State != AdminOperationPhase.Running)
                    .OrderBy(x => x.CreatedAt).FirstOrDefault()
                    ?? _state.AdminOperations.Values.OrderBy(x => x.CreatedAt).First();
                _state.AdminOperations.Remove(evicted.OperationId);
            }
            DateTimeOffset now = _clock();
            var record = new AdminOperationRecord
            {
                OperationId = Guid.NewGuid().ToString("n"),
                Kind = kind,
                Fingerprint = fingerprint,
                Actor = actor,
                IdempotencyKey = idempotencyKey,
                Target = target,
                State = AdminOperationPhase.Running,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _state.AdminOperations[record.OperationId] = record;
            Emit("operation.begun", record.OperationId, new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["actor"] = actor,
            });
            Persist();
            return record;
        }
    }

    public AdminOperationRecord CompleteAdminOperation(string operationId,
        IReadOnlyDictionary<string, string>? result = null) =>
        FinishAdminOperation(operationId, AdminOperationPhase.Succeeded, result, null, null);

    public AdminOperationRecord FailAdminOperation(string operationId, string errorCode, string errorMessage) =>
        FinishAdminOperation(operationId, AdminOperationPhase.Failed, null, errorCode, errorMessage);

    /// <summary>Reconcile an unconfirmed promotion under its original WA ledger GUID.</summary>
    internal bool ResumePlayerPromotion(string operationId)
    {
        lock (_lock)
        {
            if (!_state.AdminOperations.TryGetValue(operationId, out var record) || record.Kind != "player-promotion"
                || record.State != AdminOperationPhase.Failed || record.ErrorCode != "promotion_uncertain") return false;
            record.State = AdminOperationPhase.Running;
            record.ErrorCode = record.ErrorMessage = null;
            record.UpdatedAt = _clock();
            Emit("operation.resumed", operationId, new Dictionary<string, string> { ["kind"] = record.Kind, ["actor"] = record.Actor });
            Persist();
            return true;
        }
    }

    /// <summary>A lost/malformed clear reply is uncertain, not proof of no effect. Retrying
    /// its original key reuses the WA ledger GUID and CAS, including after Gateway restart.</summary>
    internal bool ResumeOfflineCacheClear(string operationId)
    {
        lock (_lock)
        {
            if (!_state.AdminOperations.TryGetValue(operationId, out var record) || record.Kind != "offline-cache-clear"
                || record.State != AdminOperationPhase.Failed || record.ErrorCode != "offline_clear_uncertain") return false;
            record.State = AdminOperationPhase.Running;
            record.ErrorCode = record.ErrorMessage = null;
            record.UpdatedAt = _clock();
            Emit("operation.resumed", operationId, new Dictionary<string, string> { ["kind"] = record.Kind, ["actor"] = record.Actor });
            Persist();
            return true;
        }
    }

    private AdminOperationRecord FinishAdminOperation(string operationId, AdminOperationPhase state,
        IReadOnlyDictionary<string, string>? result, string? errorCode, string? errorMessage)
    {
        lock (_lock)
        {
            if (!_state.AdminOperations.TryGetValue(operationId, out AdminOperationRecord? record))
                throw new RegistryConflictException($"Unknown admin operation '{operationId}'");
            if (record.State != AdminOperationPhase.Running)
                return record;
            record.State = state;
            record.Result = result == null ? null : new Dictionary<string, string>(result);
            record.ErrorCode = errorCode;
            record.ErrorMessage = errorMessage;
            record.UpdatedAt = _clock();
            Emit("operation.completed", record.OperationId, new Dictionary<string, string>
            {
                ["kind"] = record.Kind,
                ["state"] = state.ToString(),
                ["actor"] = record.Actor,
            });
            Persist();
            return record;
        }
    }

    public Admin.AdminOperation? AdminOperationView(string operationId)
    {
        lock (_lock)
            return _state.AdminOperations.TryGetValue(operationId, out AdminOperationRecord? record)
                ? Map(record) : null;
    }

    public Admin.AdminOperation[] AdminOperationList(int limit)
    {
        lock (_lock)
            return _state.AdminOperations.Values.OrderByDescending(x => x.CreatedAt)
                .Take(Math.Clamp(limit, 1, 1024)).Select(Map).ToArray();
    }

    private static Admin.AdminOperation Map(AdminOperationRecord record) => new(
        record.OperationId,
        record.Kind,
        record.State switch
        {
            AdminOperationPhase.Succeeded => Admin.AdminOperationState.Succeeded,
            AdminOperationPhase.Failed => Admin.AdminOperationState.Failed,
            _ => Admin.AdminOperationState.Running,
        },
        record.Actor,
        record.IdempotencyKey,
        record.CreatedAt,
        record.UpdatedAt,
        record.Target,
        record.Result,
        record.ErrorCode == null ? null : new Admin.AdminError(record.ErrorCode, record.ErrorMessage ?? string.Empty));

    public Admin.RecoveryReadiness AdminRecoveryReadiness()
    {
        lock (_lock)
        {
            ulong[] requiredPartitions = _state.Partitions.Keys.Where(partition => !IsRetired(partition))
                .Order().ToArray();
            var missing = new List<Admin.MissingArtifact>();
            var partitionArtifacts = new List<SaveRegistration>();
            var partitionLatestSaveTimes = new List<DateTimeOffset>();
            int coveredPartitions = 0;
            foreach (ulong partition in requiredPartitions)
            {
                SaveRegistration[] cover = ResolveCover(partition, long.MaxValue, null, [], catalogOnly: true)
                    .Select(item => item.Save).DistinctBy(save => save.SaveId).ToArray();
                if (cover.Length == 0)
                    missing.Add(new Admin.MissingArtifact("partition", partition.ToString()));
                else
                {
                    coveredPartitions++;
                    partitionArtifacts.AddRange(cover);
                    partitionLatestSaveTimes.Add(cover.Max(save => save.Created));
                }
            }
            partitionArtifacts = partitionArtifacts.DistinctBy(save => save.SaveId).ToList();

            bool globalRequired = RequiresGlobalSave();
            GlobalSaveRegistration? global = globalRequired
                ? _state.GlobalSaves.Values.Where(save => !save.PendingDelete && SaveHolders(save).Length != 0)
                    .OrderByDescending(save => save.SaveId).FirstOrDefault()
                : null;
            if (globalRequired && global == null)
                missing.Add(new Admin.MissingArtifact("global", "world-authority"));

            List<string> voxelDirty = [];
            VoxelRef[] voxelCut = VoxelCut(voxelDirty);
            string[] requiredVoxels = _state.VoxelBases.Values.Select(item => item.StorageName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (string storage in requiredVoxels.Except(voxelCut.Select(item => item.StorageName), StringComparer.Ordinal))
                missing.Add(new Admin.MissingArtifact("voxel", storage));

            SnapshotRegistration? snapshot = _state.Snapshots
                .Where(item => item.Dirty.Length == 0 && SnapshotAvailable(item))
                .OrderByDescending(item => item.ClusterTime).FirstOrDefault();
            Admin.RecoveryPoint? latestPoint = snapshot == null ? null : new Admin.RecoveryPoint(
                snapshot.SnapshotId,
                snapshot.Kind == SnapshotKind.Online
                    ? Admin.RecoveryConsistency.CutConsistent
                    : Admin.RecoveryConsistency.Quiescent,
                snapshot.ClusterTime);

            Admin.ArtifactCoverage partitions = Coverage(requiredPartitions.Length, coveredPartitions,
                partitionArtifacts.Select(SaveHolders).ToArray());
            Admin.ArtifactCoverage globalState = Coverage(globalRequired ? 1 : 0, global == null ? 0 : 1,
                global == null ? [] : [SaveHolders(global)]);
            Admin.ArtifactCoverage voxels = Coverage(requiredVoxels.Length, voxelCut.Length,
                voxelCut.Select(reference => new[] { LatestVoxelBase(reference.StorageName).Node }).ToArray());

            var risks = new List<string>();
            if (latestPoint == null) risks.Add("noReconstructibleSnapshot");
            AddReplicaRisk(risks, "partitionSingleCopy", partitions);
            AddReplicaRisk(risks, "globalSingleCopy", globalState);
            AddReplicaRisk(risks, "voxelSingleCopy", voxels);

            Admin.RecoveryReadinessState state = missing.Count != 0
                ? Admin.RecoveryReadinessState.NotReconstructible
                : risks.Count != 0 ? Admin.RecoveryReadinessState.AtRisk : Admin.RecoveryReadinessState.Ready;
            string storePath = Path.Combine(Path.GetDirectoryName(_snapshotPath)!, RegistryStore.FileName);
            DateTimeOffset? checkpoint = File.Exists(storePath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(storePath), TimeSpan.Zero)
                : null;
            return new Admin.RecoveryReadiness(
                state,
                latestPoint,
                MaxAgeSeconds(partitionLatestSaveTimes),
                global == null ? null : MaxAgeSeconds([global.Created]),
                MaxAgeSeconds(voxelCut.Select(reference => _state.VoxelJournals[reference.StorageName].Reported)),
                partitions,
                globalState,
                voxels,
                new Admin.RegistryDurability(checkpoint != null, checkpoint, _store.Commits, _store.WalBytes),
                missing.ToArray(),
                risks.ToArray());
        }
    }

    private bool SnapshotAvailable(SnapshotRegistration snapshot) =>
        snapshot.PartitionRefs.All(id => TryGetSave(Key(id), out SaveRegistration? save)
            && !save.PendingDelete && SaveHolders(save).Length != 0)
        && (!RequiresGlobalSave() || snapshot.GlobalRef.HasValue)
        && (!snapshot.GlobalRef.HasValue
            || _state.GlobalSaves.TryGetValue(GlobalKey(snapshot.GlobalRef.Value), out GlobalSaveRegistration? global)
            && !global.PendingDelete && SaveHolders(global).Length != 0)
        && _state.VoxelBases.Values.Select(item => item.StorageName).Distinct(StringComparer.Ordinal)
            .All(storage => (snapshot.VoxelRefs ?? []).Any(reference => reference.StorageName == storage))
        && (snapshot.VoxelRefs ?? []).All(reference =>
            _state.VoxelBases.ContainsKey(VoxelBaseKey(reference.StorageName, reference.BaseVersion))
            && _state.VoxelJournals.TryGetValue(reference.StorageName, out VoxelJournalRegistration? journal)
            && journal.BaseVersion == reference.BaseVersion && journal.TailSeq >= reference.TailSeq);

    private Admin.ArtifactCoverage Coverage(int required, int covered, string[][] holders)
    {
        if (required == 0)
            return new Admin.ArtifactCoverage(0, 0, 0, 0);
        int minimumCopies = holders.Length == 0 ? 0 : holders.Min(items => items.Length);
        int minimumHosts = holders.Length == 0 ? 0 : holders.Min(items => items.Select(HostForHolder)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return new Admin.ArtifactCoverage(required, covered, minimumCopies, minimumHosts);
    }

    private string HostForHolder(string holder) => _state.NodePlan.Values
        .FirstOrDefault(slot => string.Equals(slot.ObservedNode, holder, StringComparison.Ordinal))?.Host
        ?? "unknown";

    private static void AddReplicaRisk(List<string> risks, string code, Admin.ArtifactCoverage coverage)
    {
        if (coverage.Required != 0 && coverage.Covered == coverage.Required
            && (coverage.MinimumCopies < 2 || coverage.MinimumDistinctHosts < 2))
            risks.Add(code);
    }

    private long? MaxAgeSeconds(IEnumerable<DateTimeOffset> timestamps)
    {
        DateTimeOffset[] values = timestamps.ToArray();
        return values.Length == 0 ? null : (long)Math.Max(0, values.Max(timestamp => (_clock() - timestamp).TotalSeconds));
    }

    private string[] SaveHolders(SaveRegistration save) => save.LocationHolders.Values
        .Where(holder => !save.CorruptHolders.Contains(holder)).Distinct(StringComparer.Ordinal).ToArray();

    private static string[] SaveHolders(GlobalSaveRegistration save) => save.LocationHolders.Values
        .Distinct(StringComparer.Ordinal).ToArray();

    private VoxelBaseRegistration LatestVoxelBase(string storageName) => _state.VoxelBases.Values
        .Where(item => item.StorageName == storageName).OrderByDescending(item => item.BaseVersion).First();

    private static Admin.ClusterPhase Map(ClusterPhase value) => value switch
    {
        ClusterPhase.Bootstrapping => Admin.ClusterPhase.Bootstrapping,
        ClusterPhase.Serving => Admin.ClusterPhase.Serving,
        ClusterPhase.Degraded => Admin.ClusterPhase.Degraded,
        ClusterPhase.Draining => Admin.ClusterPhase.Draining,
        ClusterPhase.Down => Admin.ClusterPhase.Down,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.StartupKind Map(StartupKind value) => value switch
    {
        StartupKind.Warm => Admin.StartupKind.Warm,
        StartupKind.Recovery => Admin.StartupKind.Recovery,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.NodeRole Map(NodeRole value) => value switch
    {
        NodeRole.Regular => Admin.NodeRole.Regular,
        NodeRole.WA => Admin.NodeRole.WorldAuthority,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.NodeState Map(NodeState value) => value switch
    {
        NodeState.Active => Admin.NodeState.Active,
        NodeState.Closed => Admin.NodeState.Closed,
        NodeState.Dead => Admin.NodeState.Dead,
        NodeState.Empty => Admin.NodeState.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.NodeGoal Map(NodeDesiredState value) => value switch
    {
        NodeDesiredState.Wanted => Admin.NodeGoal.Wanted,
        NodeDesiredState.Draining => Admin.NodeGoal.Draining,
        NodeDesiredState.Kill => Admin.NodeGoal.Kill,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.NodeObservation Map(SpawnState value) => value switch
    {
        SpawnState.Missing => Admin.NodeObservation.Missing,
        SpawnState.Spawning => Admin.NodeObservation.Spawning,
        SpawnState.Ready => Admin.NodeObservation.Ready,
        SpawnState.Failed => Admin.NodeObservation.Failed,
        SpawnState.Gone => Admin.NodeObservation.Gone,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Admin.IncumbentAction Map(NodeIncumbentAction value) => value switch
    {
        NodeIncumbentAction.None => Admin.IncumbentAction.None,
        NodeIncumbentAction.Draining => Admin.IncumbentAction.Draining,
        NodeIncumbentAction.Kill => Admin.IncumbentAction.Kill,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>
    /// An operator/shutdown commit point: everything pending is written, then the database's WAL is
    /// folded into the main file and truncated (T-0227; the legacy full-snapshot rewrite is gone).
    /// </summary>
    public void Checkpoint()
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            try
            {
                FlushState();
                _store.Checkpoint();
            }
            catch (Exception error) { _persistenceFailure = error; throw; }
        }
    }

    /// <summary>
    /// T-0227 migration: the whole replayed legacy state goes into the store in one transaction, the
    /// file is compacted, and the legacy files are renamed aside (kept, never deleted) so this build
    /// never reads them again and an operator can still inspect them.
    /// </summary>
    private void MigrateLegacyFiles()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _store.ResetAll();
        _store.Write(_state);
        _store.Checkpoint();
        _store.Compact();
        string suffix = ".migrated-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string legacy in new[] { _snapshotPath, _walPath })
            if (File.Exists(legacy)) File.Move(legacy, legacy + suffix);
        Console.WriteLine($"Registry migrated to {RegistryStore.FileName} in {clock.ElapsedMilliseconds} ms: "
            + $"{_state.Partitions.Count} partitions, {_state.Saves.Count} saves, {_state.Lineage.Count} lineage records; "
            + $"legacy files renamed *{suffix}");
    }

    public void Dispose()
    {
        // Docs/StateDurability.md: the interval is a ceiling on BACKGROUND flushing. A
        // deliberate close is a commit point, so the pending mutation goes down here rather
        // than being the one minute the rule says we may lose.
        try { lock (_lock) if (_dirty) FlushState(); }
        catch (Exception error) { Console.WriteLine("WARNING: final registry flush failed: " + error.Message); }
        _store.Dispose();
    }

    /// <summary>The records that produced <paramref name="partition"/> at any generation, in lineage order.</summary>
    private IEnumerable<LineageRecord> LineageProducing(ulong partition) =>
        _lineageByOutputPartition.TryGetValue(partition, out List<int>? producers)
            ? producers.Select(index => _state.Lineage[index]) : [];

    private List<SaveCover> ResolveCover(ulong partition, long until, HashSet<long>? filter,
        HashSet<(ulong, long)> visiting, bool catalogOnly = false,
        Dictionary<(ulong, long, string, bool), List<SaveCover>>? memo = null)
    {
        // An empty filter means no entity of the target partition descends through this branch;
        // recursing anyway emitted covers with EntityIds:[] that made the partition claimable
        // while contributing nothing - the node then adopted an empty set (see P1-SAV-07).
        if (filter is { Count: 0 })
            return [];
        // PORTED-PERF-FIX: memoise the walk within one top-level resolution.
        //
        // `visiting` is an on-stack set - it is removed again in the finally - so it
        // detects CYCLES but does nothing about re-exploration: a lineage DAG whose
        // merges reconverge is walked once per distinct path, which is exponential in
        // the depth. Each node also runs two linear scans of _state.Lineage. The result
        // is a pure function of (_state, partition, until, filter, catalogOnly), so a
        // per-resolution cache keyed on exactly those is sound.
        //
        // The only behavioural difference is that a cached subtree is not re-entered, so
        // a cycle that closes only through an already-completed subtree no longer raises
        // "Cyclic partition lineage". That subtree demonstrably terminated when it was
        // computed, so termination of the whole walk is unaffected.
        memo ??= [];
        (ulong, long, string, bool) key = (partition, until,
            filter == null ? "*" : string.Join(',', filter.Order()), catalogOnly);
        if (memo.TryGetValue(key, out List<SaveCover>? cached))
            return cached;
        if (!visiting.Add((partition, until)))
            throw new InvalidDataException("Cyclic partition lineage");
        try
        {
            List<SaveCover> result = [];
            SaveRegistration? anchor = catalogOnly ? NewestForRecovery(partition, until) : Newest(partition, until);
            LineageRecord? split = LineageProducing(partition).Where(x => x.Kind == LineageKind.Split && x.ClusterTime < until)
                .OrderByDescending(x => x.ClusterTime).FirstOrDefault();
            // T-0339: a split may keep its input's id for one output. A save of that id from before the split holds
            // the members that left as well, so it is not the partition's state: it is the PARENT's, and is read
            // through the split like any other parent's (cut to the members the split left under the id). Judged on
            // the generation, which every lineage operation advances - never on the times, which can tie.
            if (anchor != null && split != null && anchor.SaveId.Generation < SplitOutputGeneration(split, partition))
                anchor = null;
            long after = 0;
            if (anchor != null)
            {
                result.Add(new SaveCover(anchor, filter?.Order().ToArray()));
                after = anchor.ClusterTime;
            }
            else
            {
                if (split != null)
                {
                    HashSet<long> child = split.Members[partition].ToHashSet();
                    if (filter != null)
                        child.IntersectWith(filter);
                    // T-0339 (design C): members only leave the source of a transfer, so its output reads the source alone. The
                    // target reads both: its own members from itself, the moved ones from the source. (Reading the target
                    // for the source as well, as a registry without transfers does after a rollback, is still exact: every
                    // hop is cut to the members recorded for it - it only fetches a file that contributes nothing.)
                    IEnumerable<PartitionVersion> parents = IsTransfer(split) && split.Inputs[0].Partition == partition
                        ? split.Inputs.Take(1) : split.Inputs;
                    foreach (PartitionVersion parent in parents)
                        result.AddRange(ResolveCover(parent.Partition, split.ClusterTime, child, visiting, catalogOnly, memo));
                    after = split.ClusterTime;
                }
            }
            foreach (LineageRecord merge in LineageProducing(partition).Where(x => x.Kind == LineageKind.Merge && x.ClusterTime > after && x.ClusterTime < until).OrderBy(x => x.ClusterTime))
                foreach (PartitionVersion input in merge.Inputs.Where(x => x.Partition != partition))
                    result.AddRange(ResolveCover(input.Partition, merge.ClusterTime, filter, visiting, catalogOnly, memo));
            memo[key] = result;
            return result;
        }
        finally { visiting.Remove((partition, until)); }
    }

    /// <summary>
    /// T-0339 (design C): a TRANSFER moves members from one partition to another in one operation, so a piece that leaves
    /// one layout already within merge distance of another never gets an id of its own (the split that made one and the
    /// merge that retired it in the same pass were most of the partitions that lived under a minute). It is recorded as
    /// a split with two inputs whose outputs are those two ids in input order: [source, target] -> [source, target],
    /// each one generation on, with both member lists. Its invariants:
    /// <list type="bullet">
    /// <item>No id is born or retired; both stay owned by the node that applied it (ApplyLineage refuses any other shape).</item>
    /// <item>A save of either id from before the transfer is the parents' state, read through the transfer cut to the
    /// members recorded for each output (the kept-id rule of ResolveCover, judged on generations), and is never a fallback
    /// past it (SplitBetween). So a recovery of the source after it can never bring the moved members back to it.</item>
    /// <item>The source output reads only the source; the target reads the source (for the moved members) and itself.</item>
    /// <item>Clients and homes follow a moved body to the target; anything in neither list stays on its partition.</item>
    /// </list>
    /// Kept a Split on the wire and in the store ON PURPOSE: a registry without transfers deserialises and walks such a
    /// record correctly after a rollback (multi-parent splits were always walked parent by parent), and refuses a new one
    /// with HTTP 400 instead of failing the request body.
    /// </summary>
    internal static bool IsTransfer(LineageRecord record) =>
        IsTransfer(record.Kind, record.Inputs, record.Outputs.Select(x => x.Partition).ToArray());

    private static bool IsTransfer(LineageKind kind, PartitionVersion[] inputs, ulong[] outputIds) =>
        kind == LineageKind.Split && inputs.Length == 2 && outputIds.Length == 2
        && outputIds[0] == inputs[0].Partition && outputIds[1] == inputs[1].Partition;

    /// <summary>The generation <paramref name="partition"/> has as an output of <paramref name="split"/>.</summary>
    private static long SplitOutputGeneration(LineageRecord split, ulong partition) =>
        split.Outputs.First(x => x.Partition == partition).Generation;

    /// <summary>
    /// T-0339: whether a split that kept the id of <paramref name="older"/>'s partition lies between that save and
    /// <paramref name="newer"/> (a later save of the same id). The older save then holds members the id no longer
    /// has, and it is no substitute for the newer one under the newer one's entity filter.
    /// </summary>
    private bool SplitBetween(SaveRegistration older, SaveRegistration newer) =>
        LineageProducing(older.SaveId.Partition).Any(x => x.Kind == LineageKind.Split
            && SplitOutputGeneration(x, older.SaveId.Partition) is long generation
            && generation > older.SaveId.Generation && generation <= newer.SaveId.Generation);

    /// <summary>Whether this save can still be fetched from somewhere: the holder test Newest()
    /// applies, asked of one save. A save whose every holder is gone is a catalogue row and
    /// nothing more.</summary>
    private bool Reachable(SaveRegistration save) => !save.PendingDelete && save.Locations.Any(location =>
        save.LocationHolders.TryGetValue(location, out string? holder) && !save.CorruptHolders.Contains(holder)
        && LocationUsable(save.HolderEndpoints, holder));

    private SaveRegistration? Newest(ulong partition, long until) => SavesOf(partition)
		.Where(x => x.ClusterTime < until && !x.PendingDelete && x.Locations.Any(location =>
			x.LocationHolders.TryGetValue(location, out string? holder) && !x.CorruptHolders.Contains(holder)
			&& LocationUsable(x.HolderEndpoints, holder)))
        .OrderByDescending(x => x.SaveId).FirstOrDefault();

    private SaveRegistration? NewestForRecovery(ulong partition, long until) => SavesOf(partition)
        .Where(save => save.ClusterTime < until && !save.PendingDelete
            && SaveHolders(save).Length != 0)
        .OrderByDescending(save => save.SaveId).FirstOrDefault();

    // An Empty node exited cleanly after registering its finals: its files are complete and
    // checksummed, so its locations stay usable (its lease has necessarily lapsed - it stopped
    // heartbeating). Excluding them made a drain Final unreachable milliseconds after release,
    // and recovery silently restored a stale Periodic instead (P1-DRAIN-01, lost character).
    // T-0348: a Dead node's registered files are complete too (registered after the rename), but nothing says its host
    // is still there, so it is usable only while it is a gone holder with a live proxy on its host (GoneHolders.cs).
    // T-0340: a host holder is usable while a node of its host can serve its files (HostStore.cs); so is a legacy
    // holder whose row was released (step 6), through a proxy on the host its files lie on.
    private bool HolderUsable(string holder) => _hostStore && IsHostHolder(holder) ? StoreState(HostOfHolder(holder)!) == HostStoreState.Serving
        : _state.Nodes.TryGetValue(holder, out NodeRegistration? node) ? GlobalHolderUsable(node) || DeadHolderServed(node) : ReleasedHolderServed(holder);

    /// <summary>The holder test for GLOBAL saves, and <see cref="HolderUsable"/> without T-0348's Dead holders: a global
    /// save has no proxied fetch (its replica directive names the holder's own endpoint), so a Dead holder serves none.
    /// T-0340: a host holder is judged by its store, like a partition save's.</summary>
    private bool GlobalHolderUsable(string holder) => _hostStore && IsHostHolder(holder) ? HolderUsable(holder)
        : _state.Nodes.TryGetValue(holder, out NodeRegistration? node) && GlobalHolderUsable(node);

    private bool GlobalHolderUsable(NodeRegistration node) =>
        node.State == NodeState.Empty || (node.State is NodeState.Active or NodeState.Closed && node.LeaseExpires > _clock());

    /// <summary>Whether a location's holder can be offered: a holder that never gave an endpoint is taken on trust (a
    /// readable path), any other must be usable. T-0340: a host holder has no endpoint and is judged by its store.</summary>
    private bool LocationUsable(Dictionary<string, string> endpoints, string holder) =>
        _hostStore && IsHostHolder(holder) ? HolderUsable(holder) : !endpoints.ContainsKey(holder) || HolderUsable(holder);

    /// <summary>Registered and gone: Dead, or Active/Closed with a lapsed lease. Unlike
    /// <see cref="HolderUsable"/>, an unregistered holder is unknown rather than unusable.</summary>
    private bool HolderKnownDead(string holder) => _state.Nodes.TryGetValue(holder, out NodeRegistration? node)
        && (node.State == NodeState.Dead || (node.State is NodeState.Active or NodeState.Closed && !NodeLeaseLive(node, _clock())));

    /// <summary>
    /// T-0237: the characters whose copy in a recovered partition's content is older than their home, where
    /// the home is another partition that really holds the body.
    /// </summary>
    /// <remarks>
    /// Ordered by <see cref="CharacterHome.Version"/>: the newest covering save that carries a stamp for the
    /// character decides, and the copy is stale when its stamp is below the home's version. A save with stamps
    /// but none for this character (not in it, or not yet reported here) never overrides the home. Only a save
    /// from an older build (no stamps at all) falls back to the registration times, which can misorder by the
    /// capture-to-registration delay. Never drops the only copy: the home partition must be live-owned, or
    /// hold the body by its offline row's members or its newest save's stamps.
    /// </remarks>
    private long[] StaleHomedCopies(ulong partition, SaveCover[] cover)
    {
        if (cover.Length == 0) return [];
        DateTimeOffset newestCovered = cover.Max(item => item.Save.Created);
        SaveRegistration[] newestFirst = cover.Select(item => item.Save).OrderByDescending(save => save.SaveId).ToArray();
        bool stampedBuild = newestFirst.Any(save => save.HomeStamps != null);
        var stale = new List<long>();
        var reasons = new List<string>();
        foreach (CharacterHome home in _state.CharacterHomes.Values)
        {
            ulong homePartition = LiveHomePartition(home);
            if (homePartition == partition) continue;
            SaveRegistration? stamped = newestFirst.FirstOrDefault(save => save.HomeStamps?.ContainsKey(home.Entity) == true);
            string why;
            if (stamped != null)
            {
                long copy = stamped.HomeStamps![home.Entity];
                if (home.Version == 0 || copy >= home.Version) continue;
                why = $"copy version {copy} < home version {home.Version}";
            }
            else if (stampedBuild || home.Updated <= newestCovered) continue;
            else why = $"unstamped save, home updated {home.Updated:O} > save {newestCovered:O}";
            if (!HomeHoldsBody(home, homePartition)) continue;
            stale.Add(home.Entity);
            reasons.Add($"{home.Entity}->{homePartition} ({why}, home updated {home.Updated:O})");
        }
        // One line per claim. These are CANDIDATES the node drops only if the save really carries them: a stamped
        // candidate is known to be in the save, an unstamped one (older build) may not be - the node logs each real drop.
        if (stale.Count != 0)
            Console.WriteLine($"recovery of partition {partition}: {stale.Count} character(s) homed elsewhere are candidates for a stale copy "
                + $"(newest covered save {newestFirst[0].SaveId} created {newestCovered:O}): {string.Join("; ", reasons)}");
        return stale.ToArray();
    }

    /// <summary>
    /// T-0237: every character home in another partition that holds its body. The node matches these by IDENTITY
    /// against the characters of the recovered content: a stale copy can be an older, DIFFERENT body of the same
    /// player (w32 2026-09-25 04:16:08: partition 227687's only member was Pilot-010's standing body ...870 from a
    /// 22:28Z save while the home named ...929; the entity-keyed candidate list could not match it and the World
    /// Authority's duplicate election closed it a second later).
    /// </summary>
    private HomeElsewhere[] HomesElsewhereOf(ulong partition) => _state.CharacterHomes.Values
        .Select(home => (Home: home, Partition: LiveHomePartition(home)))
        .Where(x => x.Home.Identity != 0 && x.Partition != partition && HomeHoldsBody(x.Home, x.Partition))
        .Select(x => new HomeElsewhere(x.Home.Identity, x.Home.Entity, x.Partition, x.Home.Version, x.Home.Updated,
            HomeCopyCaptured(x.Home, x.Partition)))
        .ToArray();

    /// <summary>
    /// T-0237: the cluster-clock capture time of the newest catalogued save of the home partition that stamps the
    /// home's body, or 0. A stamp is only written for a body the save really carries, so the body was in the home
    /// partition at that time - any copy captured before it elsewhere is older (see <see cref="HomeElsewhere.Captured"/>).
    /// </summary>
    private long HomeCopyCaptured(CharacterHome home, ulong homePartition) => SavesOf(homePartition)
        .Where(x => !x.PendingDelete && x.Locations.Count != 0 && x.HomeStamps?.ContainsKey(home.Entity) == true)
        .Select(x => x.ClusterTime).DefaultIfEmpty(0).Max();

    private bool HomeHoldsBody(CharacterHome home, ulong homePartition)
    {
        if (_state.Partitions.TryGetValue(homePartition, out PartitionOwnership? owner) && owner.Node != null && !owner.Recovering)
            return true;
        if (_state.OfflinePartitions.TryGetValue(homePartition, out OfflinePartition? row))
        {
            if (row.Members.Contains(home.Entity)) return true;
            if (TryGetSave(Key(row.Save), out SaveRegistration? offlineSave) && offlineSave.HomeStamps?.ContainsKey(home.Entity) == true) return true;
            return false;
        }
        return NewestCatalogued(homePartition)?.HomeStamps?.ContainsKey(home.Entity) == true;
    }

    private SaveRegistration? NewestCatalogued(ulong partition) => SavesOf(partition)
        .Where(x => !x.PendingDelete && x.Locations.Count != 0)
        .OrderByDescending(x => x.SaveId).FirstOrDefault();

    private GlobalSaveRegistration? NewestGlobal() => _state.GlobalSaves.Values
        .Where(x => x.SaveId.Generation == _state.WorldAuthority.Generation && !x.PendingDelete
            && x.Locations.Any(location => x.LocationHolders.TryGetValue(location, out string? holder)
            && (_hostStore && IsHostHolder(holder) ? HolderUsable(holder) : !x.HolderEndpoints.ContainsKey(holder) || GlobalHolderUsable(holder))))
        .OrderByDescending(x => x.SaveId).FirstOrDefault();

    private GlobalSaveRegistration? ShutdownGlobalSave() => _state.GlobalSaves.Values
        .Where(x => x.SaveId.Generation == _state.WorldAuthority.Generation && x.Kind == SaveKind.Final && !x.PendingDelete
            && x.Locations.Count != 0 && (!_state.ShutdownStarted.HasValue || x.Created >= _state.ShutdownStarted.Value))
        .OrderByDescending(x => x.SaveId).FirstOrDefault() ?? StandingGlobalSave();

    /// <summary>
    /// SE-892: the Final save of the clean stop this process started from, while it still is the global state. Each World
    /// Authority gets a new generation when it registers, and another when it dies, so an unchanged generation says none has
    /// run since that stop and nothing global can be unsaved. Without this, an update whose nodes all died at startup stopped
    /// dirty ("global") although nothing had run. A Recovery start never qualifies, and neither does a respawned Gateway:
    /// it can have lost the row of an authority that ran, as a coalesced flush covers up to a minute.
    /// </summary>
    private GlobalSaveRegistration? StandingGlobalSave()
    {
        if (_startedFromGlobal is not GlobalSaveId id || id.Generation != _state.WorldAuthority.Generation
            || !_state.GlobalSaves.TryGetValue(GlobalKey(id), out GlobalSaveRegistration? save)
            || save.Kind != SaveKind.Final || save.PendingDelete || save.Locations.Count == 0
            || _state.GlobalSaves.Values.Any(x => x.SaveId.CompareTo(id) > 0))
            return null;
        return save;
    }

    private bool RequiresGlobalSave() => _state.WorldAuthority.Generation > 0 || _state.GlobalSaves.Count != 0;

    private bool IsWarmShutdown(ShutdownMarker? marker)
    {
        if (marker is not { Dirty.Length: 0 }) return false;
		List<string> voxelDirty = [];
		VoxelRef[] voxelRefs = VoxelCut(voxelDirty);
		if (voxelDirty.Count != 0 || marker.VoxelRefs != null && !marker.VoxelRefs.OrderBy(x => x.StorageName, StringComparer.Ordinal).SequenceEqual(voxelRefs))
			return false;
		SaveId[] live = _state.Partitions.Keys.Where(partition => !IsRetired(partition)).Select(partition => marker.Manifest
			.FirstOrDefault(save => save.Partition == partition)).ToArray();
		if (live.Length != marker.Manifest.Length || live.Any(id => id == default
			|| !TryGetSave(Key(id), out SaveRegistration? save) || save.PendingDelete || save.Locations.Count == 0
			// A manifest entry that is not a Final save is an unowned partition's newest save (see
			// ShutdownSave); it still stands for the partition as long as nothing newer was catalogued.
			|| save.Kind != SaveKind.Final && NewestCatalogued(id.Partition)?.SaveId != id))
			return false;
        if (!RequiresGlobalSave()) return marker.GlobalRef == null;
        if (!marker.GlobalRef.HasValue || !_state.GlobalSaves.TryGetValue(GlobalKey(marker.GlobalRef.Value), out GlobalSaveRegistration? save)) return false;
        return save.SaveId.Generation == _state.WorldAuthority.Generation && save.Kind == SaveKind.Final && !save.PendingDelete && save.Locations.Count != 0;
    }

    /// <summary>
    /// The save that stands for a partition in a clean shutdown. A Final save is one by definition.
    /// A partition nobody owns will never get one: once the shutdown has begun every node is Closed
    /// and may not claim recoveries, and none needs to - nothing has simulated the partition since
    /// its newest save was written, so that save IS its state. Such partitions exist at almost every
    /// stop: the rotator releases stranded partitions "for recovery" and drains release member-less
    /// ones unowned, and whichever were still unclaimed when the stop closed the nodes stay unowned.
    /// Without this the 2026-09-07 21:19 stop waited the full 600 s with all six nodes Empty
    /// (five recovery claims refused at 21:19:27 because the nodes were Closed) and was force-killed.
    /// </summary>
    private SaveRegistration? ShutdownSave(ulong partition)
    {
        SaveRegistration? newest = NewestCatalogued(partition);
        if (newest == null || newest.Kind == SaveKind.Final) return newest;
        return _state.Partitions.TryGetValue(partition, out PartitionOwnership? ownership) && ownership.Node == null ? newest : null;
    }

    /// <param name="waitable">Only what the shutdown can still wait for. An UNOWNED partition with no
    /// catalogued save at all can never get one - every node is Closed and nothing owns it - so waiting
    /// for it only ran the stop to its timeout and SIGKILLed the gateway (T-0223, 2026-09-23 06:57: split
    /// output 175587 released memberless by a drain before its first save). It stays in the marker's
    /// dirty list, so the next start is a Recovery start, which recovers it through its lineage cover
    /// (175587 loaded with 0 members at 07:04:52). Since #1292 a drain's member-less release reaps such a row
    /// (<see cref="ReapIfMemberlessUnsaved"/>) unless a client or character home still needs its cover.</param>
    private string[] ShutdownDirtyState(bool waitable = false)
    {
        List<string> dirty = _state.Partitions.Keys.Where(partition => !IsRetired(partition) && ShutdownSave(partition) == null
                && !(waitable && _state.Partitions[partition].Node == null && NewestCatalogued(partition) == null)
                // Nothing will ever write a Final save for a partition its Dead owner still holds.
                && !(waitable && OwnedByDeadNode(_state.Partitions[partition])))
            .Select(partition => "partition:" + partition).ToList();
        if (RequiresGlobalSave() && ShutdownGlobalSave() == null) dirty.Add("global");
        VoxelCut(dirty);
        return dirty.ToArray();
    }

    private bool OwnedByDeadNode(PartitionOwnership owner) =>
        owner.Node != null && _state.Nodes.TryGetValue(owner.Node, out NodeRegistration? node)
        && node.Epoch == owner.Epoch && node.State == NodeState.Dead;

    private VoxelRef[] VoxelCut(List<string>? dirty = null)
    {
        List<VoxelRef> result = [];
        foreach (VoxelBaseRegistration basis in _state.VoxelBases.Values.GroupBy(x => x.StorageName)
            .Select(group => group.OrderByDescending(x => x.BaseVersion).First()).OrderBy(x => x.StorageName, StringComparer.Ordinal))
        {
            if (!_state.VoxelJournals.TryGetValue(basis.StorageName, out VoxelJournalRegistration? journal)
                || journal.BaseVersion != basis.BaseVersion || journal.TailSeq < basis.TailSequence
                || journal.AuthorityGeneration != _state.WorldAuthority.Generation)
                dirty?.Add("voxel:" + basis.StorageName);
            else
                result.Add(new VoxelRef(basis.StorageName, basis.BaseVersion, journal.TailSeq));
        }
        return result.ToArray();
    }

    private static string VoxelBaseKey(string storageName, long baseVersion) => storageName + "\0" + baseVersion;

    private static string NodeKey(NodeRegistration node) => node.Node + ":" + node.Epoch;

    private bool ExecutorSilent()
    {
        if (!_state.NodePlan.Values.Any(x => x.Desired == NodeDesiredState.Wanted && x.Observed != SpawnState.Ready)) return false;
        DateTimeOffset last = _state.Executors.Values.Select(x => x.LastHeartbeat).DefaultIfEmpty(_state.NodePlanUpdated).Max();
        return _clock() - last >= ExecutorSilence;
    }

    private bool GlobalSpawnHalted()
    {
        NodeSlotPlan[] wanted = _state.NodePlan.Values.Where(x => x.Desired == NodeDesiredState.Wanted).ToArray();
        if (wanted.Length < 2) return false;
        string? cause = wanted[0].LastFailure;
        return !string.IsNullOrWhiteSpace(cause) && wanted.All(x => x.LastFailure == cause && x.Failures.Any(f => _clock() - f <= SpawnFailureWindow));
    }

    private DateTimeOffset? NewestGlobalSaveTime() => _state.GlobalSaves.Values.Select(x => (DateTimeOffset?)x.Created).Max();

    /// <summary>
    /// SE-843: the catalogue stopped advancing on a cluster that otherwise looks healthy, because the World Authority
    /// cannot write or register its global saves. It retries the same save for as long as it lives and captures no
    /// other, so without this nothing showed until a stop waited for a final save that never came (SE-819). The clock
    /// starts no earlier than the authority's registration: a new one has had no chance to save yet.
    /// </summary>
    private bool GlobalSaveIsStale()
    {
        if (_state.Phase != ClusterPhase.Serving || _state.WorldAuthority.Node is not { } authority
            || !_state.Nodes.TryGetValue(authority, out NodeRegistration? node)) return false;
        DateTimeOffset newest = NewestGlobalSaveTime() ?? node.Registered;
        return _clock() - (newest > node.Registered ? newest : node.Registered) >= GlobalSaveStale;
    }

    private string[] DegradedReasons()
    {
        List<string> reasons = [];
        if (_state.Nodes.Values.Any(x => x.LastSaveFailure != null && x.State == NodeState.Closed)) reasons.Add("saveFailure");
        if (GlobalSaveIsStale()) reasons.Add("globalSaveStale");
        if (ExecutorSilent()) reasons.Add("executorSilence");
        if (GlobalSpawnHalted()) reasons.Add("globalSpawnFailure");
        if (_state.NodePlan.Values.Any(x => x.Failures.Count(f => _clock() - f <= SpawnFailureWindow) >= 3)) reasons.Add("spawnFailureAlert");
        // Capacity shortfall (Orchestration §9.3): recoverable partitions are waiting
        // but every live regular node reports a recovery capacity it has already
        // reached, so the rest of the world stays Unowned until capacity returns.
        NodeRegistration[] liveRegulars = _state.Nodes.Values
            .Where(x => x.Role == NodeRole.Regular && x.State == NodeState.Active && !x.Spare && x.LeaseExpires > _clock()).ToArray();
        PartitionOwnership[] recoverable = _state.Partitions.Values.Where(x => x.Node == null && !IsRetired(x.Partition)
            && !_state.OfflinePartitions.ContainsKey(x.Partition)
            && ResolveCover(x.Partition, long.MaxValue, null, []).Count != 0).ToArray();
        if (liveRegulars.Length != 0 && recoverable.Length != 0
            && !liveRegulars.Any(node =>
            {
                bool countOk = node.RecoveryCapacity <= 0
                    || _state.Partitions.Values.Count(p => p.Node == node.Node && p.Epoch == node.Epoch) < node.RecoveryCapacity;
                long free = node.LoadCapacity > 0 ? node.LoadCapacity - node.ProjectedLoad : long.MaxValue;
                return countOk && recoverable.Any(x => PartitionWeight(x.Partition) <= free);
            }))
            reasons.Add("capacityShortfall");
        return reasons.ToArray();
    }

    /// <summary>
    /// Marks every lineage record whose outputs are all durable - each output either has a save that
    /// covers it, or is consumed by a record that has itself already collapsed.
    /// </summary>
    /// <remarks>
    /// Same closure as before, computed once instead of by re-scanning to a fixpoint. The previous
    /// shape was a do/while over the whole lineage list where the inner test scanned the whole list
    /// AGAIN for a collapsed consumer - O(lineage^2) per pass and O(lineage^3) to converge, in a list
    /// that is only ever appended to (w19: 2253 records after four hours) and with the global registry
    /// lock held throughout. It runs on every save registration, which includes the handover tee save,
    /// so every concurrent heartbeat, route resolve and Begin queued behind it - the best candidate
    /// for the registry-latency floor this hunt was after.
    ///
    /// The rewrite keeps two facts instead of rediscovering them: the set of partition versions
    /// consumed by an already-collapsed record, and which records name a given version as an OUTPUT.
    /// Collapsing a record can only ever enable records whose output it consumes, so those are the
    /// only ones worth revisiting. Each record collapses at most once and each version enters the
    /// consumed set at most once, so the whole thing is linear in the lineage plus its edges.
    /// Collapsing order does not matter: the condition is monotone, so the fixpoint is the same set.
    /// </remarks>
    private void UpdateCollapsed()
    {
        Queue<int> pending = new Queue<int>();
        for (int i = 0; i < _state.Lineage.Count; i++)
            if (!_state.Lineage[i].Collapsed) pending.Enqueue(i);
        while (pending.Count != 0)
        {
            int index = pending.Dequeue();
            LineageRecord record = _state.Lineage[index];
            if (record.Collapsed) continue;
            if (!record.Outputs.All(OutputIsDurable)) continue;
            // Collapsed does not enter the retirement decision (which reads only
            // ClusterTime, Inputs and Outputs), so this replacement needs no reindex.
            LineageRecord collapsed = record with { Collapsed = true };
            _state.Lineage[index] = collapsed;
            // The op-id index holds record REFERENCES, and this is the one place a record is replaced
            // rather than mutated, so it has to be re-pointed or a retried lineage op would be handed
            // back the pre-collapse object. _lineageByOutput stores positions, which do not move.
            _lineageByOpId[collapsed.OpId] = collapsed;
            foreach (PartitionVersion input in record.Inputs)
                if (_collapsedInputs.Add(input) && _lineageByOutput.TryGetValue(input, out List<int>? enabled))
                    foreach (int next in enabled) pending.Enqueue(next);
        }
    }

    private bool OutputIsDurable(PartitionVersion output) =>
        (_archivedPartitions.TryGetValue(output.Partition, out long archivedGeneration) && archivedGeneration >= output.Generation)
        || InMemorySavesOf(output.Partition).Any(save => save.SaveId.Partition == output.Partition
            && save.SaveId.Generation >= output.Generation && !save.PendingDelete && save.Locations.Count != 0)
        || _collapsedInputs.Contains(output);

    /// <summary>Partition versions consumed by a record that has already collapsed. Derived state,
    /// rebuilt in <see cref="NormalizeState"/> alongside the other lineage indexes.</summary>
    private readonly HashSet<PartitionVersion> _collapsedInputs = [];

    /// <summary>Lineage record positions by the version each one PRODUCES, so collapsing a record can
    /// find exactly the records its inputs might now enable. Positions are stable: the list is only
    /// appended to, and records are replaced in place.</summary>
    private readonly Dictionary<PartitionVersion, List<int>> _lineageByOutput = [];
    /// <summary>Lineage records producing a partition at ANY generation, in list order - the walk in
    /// ResolveCover asks by partition, and scanning the whole list per recursion level (twice) made a
    /// cold start with every lease expired spin for minutes under the lock (2026-09-08 11:54).</summary>
    private readonly Dictionary<ulong, List<int>> _lineageByOutputPartition = [];

    private NodeRegistration RequireNode(string node, long epoch)
    {
        EnsurePersistenceHealthy();
        if (!_state.Nodes.TryGetValue(node, out NodeRegistration? current) || current.Epoch != epoch)
            throw new RegistryConflictException($"Node '{node}' epoch {epoch} is fenced");
        return current;
    }

    /// <summary>Active OR Closed: a node the registry is draining still replicates saves and reports
    /// voxel watermarks - refusing those with "is Closed" left every drain final unreplicated in a
    /// full graceful stop and fenced nodes mid-drain (T-0048, 2026-09-06 08:13).</summary>
    private NodeRegistration RequireLiveNode(string node, long epoch)
    {
        NodeRegistration current = RequireNode(node, epoch);
        if (current.State is not (NodeState.Active or NodeState.Closed))
            throw new RegistryConflictException($"Node '{node}' epoch {epoch} is {current.State}");
        return current;
    }

    private NodeRegistration RequireActiveNode(string node, long epoch)
    {
        NodeRegistration current = RequireNode(node, epoch);
        if (current.State != NodeState.Active)
            throw new RegistryConflictException($"Node '{node}' epoch {epoch} is {current.State}");
        return current;
    }

    private NodeRegistration RequireRegularActiveNode(string node, long epoch)
    {
        NodeRegistration current = RequireActiveNode(node, epoch);
        if (current.Role != NodeRole.Regular)
            throw new RegistryConflictException($"World Authority node '{node}' cannot own partitions");
        return current;
    }

    private bool WorldAuthorityLive()
    {
        WorldAuthority authority = _state.WorldAuthority;
        return authority.Node != null && authority.LeaseExpires > _clock()
            && _state.Nodes.TryGetValue(authority.Node, out NodeRegistration? node)
            && node.Epoch == authority.Epoch && node.State == NodeState.Active;
    }

    private WorldAuthority RequireWorldAuthority(string node, long epoch, long generation)
    {
        WorldAuthority authority = _state.WorldAuthority;
        if (authority.Node != node || authority.Epoch != epoch || authority.Generation != generation)
            throw new RegistryConflictException($"World Authority generation {generation} is fenced");
        return authority;
    }

    private PartitionOwnership RequireOwner(ulong partition, string node, long epoch, long generation)
    {
        if (!_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner) || owner.Node != node || owner.Epoch != epoch || owner.Generation != generation)
            throw new RegistryConflictException($"Partition {partition} ownership is fenced");
        return owner;
    }

    /// <summary>
    /// The route a client should take, or a refusal that says WHETHER IT IS WORTH COMING BACK.
    ///
    /// T-0197: this used to answer one exception for eight different disjuncts, and
    /// DirectGateway.OnConnectionRequest turned all eight into Reject() - a definitive verb for
    /// conditions that are transient by construction. A partition in mid-recovery becomes available
    /// in seconds; a client that asks during that window was told "no" and, in 6 of 9 measured runs,
    /// did not come back. The classification is what the caller needs, so it is made here, where the
    /// reason is known, rather than guessed at the catch site.
    ///
    /// TRANSIENT (RegistryBusyException, "come back shortly"): the partition is recovering, its
    /// lease has lapsed, its owner is a node the registry no longer has, the owner's epoch is stale,
    /// or the node is not serving yet. Every one of those is a state the cluster is actively leaving.
    /// DEFINITIVE (RegistryConflictException): the partition is unknown, or it has no owner AND no
    /// recovery can be expected for it - nothing is going to change by asking again.
    /// </summary>
    private ClientRoute ResolveClientRoute(ulong client, ClientPartition location)
    {
        ulong partition = location.Partition;
        DateTimeOffset now = _clock();
        if (!_state.Partitions.TryGetValue(partition, out PartitionOwnership? owner))
            throw new RegistryConflictException($"Client {client} partition {partition} is not known to the registry");
        // RETIRED IS THE MOST DEFINITIVE ANSWER THERE IS, and it has to be read before the unowned
        // test: a retired row keeps its entry with no owner, so without this it reads as "being
        // re-homed" and the caller would hold a join for a partition that is never coming back.
        // The public overload already drops a retired placement and falls through to the home path;
        // this is the same fact stated where the classification is made.
        if (IsRetired(partition))
            throw new RegistryConflictException($"Client {client} partition {partition} has been retired");
        if (owner.Node == null && _state.OfflinePartitions.ContainsKey(partition))
        {
            // T-0193: the client's placement (or home) is in an offline partition - that IS demand. Load it and
            // answer "come back shortly"; the retry lands once the new owner reports it.
            string outcome = RequestOfflineLoad(partition, null, "join");
            throw new RegistryBusyException($"Client {client} partition {partition} is offline and loading ({outcome})");
        }
        if (owner.Node == null)
        {
            // An unowned partition is normally mid-re-home and worth waiting for. One that has been
            // REPORTED STRANDED is not: no holder can recover it, and T-0198 exists because that state
            // was held in silence. That report is the discriminator between the two.
            if (_strandedReported.Contains(partition))
                throw new RegistryConflictException($"Client {client} partition {partition} is stranded: no node can recover it");
            throw new RegistryBusyException($"Client {client} partition {partition} is unowned and being re-homed");
        }
        if (owner.Recovering)
            throw new RegistryBusyException($"Client {client} partition {partition} is recovering");
        // T-0237: mid-handover the owner is still the source, and routing a joining client there sends it to a
        // node its partition (and body) is leaving. A connected client's route check treats busy as unconfirmed.
        if (PartitionMoving(partition))
            throw new RegistryBusyException($"Client {client} partition {partition} is moving between nodes");
        if (owner.LeaseExpires <= now)
            throw new RegistryBusyException($"Client {client} partition {partition} lease has lapsed and is being re-homed");
        if (!_state.Nodes.TryGetValue(owner.Node, out NodeRegistration? node))
            throw new RegistryBusyException($"Client {client} partition {partition} is owned by {owner.Node}, which is not registered");
        if (node.Epoch != owner.Epoch)
            throw new RegistryBusyException($"Client {client} partition {partition} owner epoch {owner.Epoch} is behind node {node.Node} epoch {node.Epoch}");
        if (node.State != NodeState.Active || node.LeaseExpires <= now || !ManagedNodeReady(node))
            throw new RegistryBusyException($"Client {client} partition {partition} is on {node.Node}, which is not serving yet ({node.State})");
        return new ClientRoute(client, partition, node.Node, node.Epoch, owner.Generation, node.Endpoint);
    }

    /// <summary>T-0237: the partition has a handover that has begun and not yet committed or aborted. Under _lock.</summary>
    private bool PartitionMoving(ulong partition) => HandoversOf(partition).Any(x => x.State is HandoverState.Begun or HandoverState.Active);

    /// <summary>
    /// One-time layout migration (Docs/EntityIdRanges.md, Migration): states written before
    /// 2026-08-21 issued ranges as rangeIndex(24) &lt;&lt; 32; the layout is now rangeIndex(32) &lt;&lt; 24.
    /// Old range k covered exactly the new indices k*256 .. k*256+255, so each legacy record is
    /// renumbered to k*256 and the counter is moved past the last legacy range; the other 255
    /// sub-ranges of each legacy range are simply never issued. Idempotent: runs only while the
    /// persisted layout still says 32 offset bits. A fresh state (nothing issued) just adopts
    /// the current layout.
    /// </summary>
    private void MigrateEntityIdRangeLayout()
    {
        if (_state.EntityIdRangeOffsetBits == EntityIdRange.RangeBits) return;
        if (_state.EntityIdRangeOffsetBits != 32)
            throw new InvalidDataException($"Registry EntityId range layout has {_state.EntityIdRangeOffsetBits} offset bits; this build understands 32 (legacy) and {EntityIdRange.RangeBits}");
        if (_state.EntityIdRanges.Count > 0 || _state.NextEntityIdRange > 1)
        {
            const uint factor = 1u << (32 - EntityIdRange.RangeBits);
            uint maxLegacy = Math.Max(_state.NextEntityIdRange - 1, _state.EntityIdRanges.Select(x => x.Index).DefaultIfEmpty(0u).Max());
            if (maxLegacy >= uint.MaxValue / factor)
                throw new InvalidDataException("Legacy EntityId range index too large to renumber");
            List<EntityIdRange> renumbered = _state.EntityIdRanges
                .Select(x => x with { Index = checked(x.Index * factor) }).ToList();
            _state.EntityIdRanges = renumbered;
            _state.NextEntityIdRange = checked((maxLegacy + 1) * factor);
            Console.WriteLine($"entity-id ranges migrated from 32 to {EntityIdRange.RangeBits} offset bits: {renumbered.Count} legacy ranges renumbered (index*{factor}), next index {_state.NextEntityIdRange}");
        }
        _state.EntityIdRangeOffsetBits = EntityIdRange.RangeBits;
    }

    private void NormalizeState()
    {
        _state.PluginStore.Validate();
        _state.NodeControl ??= new();
        _state.NodeHistory ??= [];
        _historyIds = null;
        // Readiness is a live runtime observation, never a persisted startup authorization.
        _state.ManagedProofs.Clear();
        foreach (var node in _state.Nodes.Values) node.Serving = false;
        if (_state.NextPartitionId == 0)
            _state.NextPartitionId = _state.Partitions.Keys.Where(x => !ClusterProtocol.PlaceholderPartitionIds.Contains(x)).DefaultIfEmpty(0UL).Max() + 1;
        MigrateEntityIdRangeLayout();
        // Range 0 is never issued (EntityId unique bits must never be all zero) and a range is
        // never re-issued: the counter is floored by the highest persisted range.
        _state.NextEntityIdRange = Math.Max(Math.Max(1u, _state.NextEntityIdRange),
            _state.EntityIdRanges.Select(x => x.Index + 1).DefaultIfEmpty(1u).Max());
        DateTimeOffset expires = _clock() + _leaseTtl;
        foreach (NodeRegistration node in _state.Nodes.Values)
        {
            if (node.LeaseExpires == default)
                node.LeaseExpires = expires;
            // Simulation progress is volatile (heartbeats no longer spend a WAL record on it), so
            // the recovered timestamp is stale by design; re-arm the stall clocks from boot rather
            // than let hours-old progress drain a node on its first same-frame heartbeat.
            if (node.SimulationProgress != default)
                node.SimulationProgress = _clock();
            if (node.State == NodeState.Closed && node.ForceDrainAt == null)
                node.ForceDrainAt = _clock() + TimeSpan.FromMinutes(15);
        }
        if (_state.WorldAuthority.Node != null && (!_state.Nodes.TryGetValue(_state.WorldAuthority.Node, out NodeRegistration? authorityNode)
            || authorityNode.Role != NodeRole.WA || authorityNode.Epoch != _state.WorldAuthority.Epoch
            || authorityNode.AuthorityGeneration != _state.WorldAuthority.Generation))
            throw new InvalidDataException("Registry World Authority designation does not match its node registration");
        foreach (PartitionOwnership owner in _state.Partitions.Values)
        {
            if (string.IsNullOrWhiteSpace(owner.Node))
            {
                owner.Node = null;
                owner.Epoch = 0;
            }
            else if (owner.LeaseExpires == default)
                owner.LeaseExpires = expires;
        }
		foreach (SaveRegistration save in _state.Saves.Values)
			foreach (string location in save.Locations)
				save.LocationHolders.TryAdd(location, save.Node);
		foreach (GlobalSaveRegistration save in _state.GlobalSaves.Values)
			foreach (string location in save.Locations)
				save.LocationHolders.TryAdd(location, save.Node);
		_state.VoxelBases ??= [];
		_state.VoxelJournals ??= [];
		_state.PartitionWeights ??= [];
		_state.OfflinePartitions ??= [];
        // RecoverWal() replaces _state wholesale as it replays, so the index is built here, after
        // the last assignment and before the registry answers anything.
        InvalidateReplicaQueue();
        _savesByPartition.Clear();
        foreach (SaveRegistration save in _state.Saves.Values)
            IndexSave(save);
        // In list order, so ties resolve the same way they do incrementally.
        _handoversByPartition.Clear();
        _handoversByTarget.Clear();
        foreach (HandoverRecord handover in _state.Handovers.Values)
            IndexHandover(handover);
        _retirement.Clear();
        _lineageByOutput.Clear();
        _lineageByOutputPartition.Clear();
        _collapsedInputs.Clear();
        _lineageByOpId.Clear();
        for (int index = 0; index < _state.Lineage.Count; index++)
            IndexLineage(index, _state.Lineage[index]);
        Volatile.Write(ref _authorityGeneration, _state.WorldAuthority.Generation);
    }

    private HandoverRecord GetRequiredHandover(string id) => _state.Handovers.GetValueOrDefault(id)
        ?? throw new ArgumentException($"Handover '{id}' does not exist");

    // Liveness is decided by the LATEST lineage record touching the partition id, not by any record
    // ever: ids are reused across merges and splits, so a partition consumed by one operation is
    // routinely re-created as a later operation's output. Judging by Any() retired 16 live partitions
    // (the mountain base among them) the moment they needed generic recovery - the only path that
    // consults this - and they stayed unowned across restarts (2026-08-11).
    // ... and decided by a LOOKUP, not by scanning every lineage record. The scan version was
    // O(lineage) and IsRetired is called once per partition from six sites, including the
    // /registry/partitions listing the rotator and monitor poll continuously - so those sites were
    // O(partitions x lineage) with both factors growing monotonically and neither ever pruned
    // (w19 2026-08-19: 2253 lineage records and 2280 partition rows, 99% of them retired, after
    // four hours). Derived state: rebuilt in NormalizeState(), never serialised.
    private readonly Dictionary<ulong, (long ClusterTime, bool Retired, int Index)> _retirement = [];

    private void IndexLineage(LineageRecord record) => IndexLineage(_state.Lineage.Count - 1, record);

    private void IndexLineage(int index, LineageRecord record)
    {
        _lineageByOpId[record.OpId] = record;
        foreach (PartitionVersion output in record.Outputs)
        {
            if (!_lineageByOutput.TryGetValue(output, out List<int>? producers))
                _lineageByOutput[output] = producers = [];
            producers.Add(index);
        }
        foreach (ulong outputPartition in record.Outputs.Select(x => x.Partition).Distinct())
        {
            if (!_lineageByOutputPartition.TryGetValue(outputPartition, out List<int>? byPartition))
                _lineageByOutputPartition[outputPartition] = byPartition = [];
            byPartition.Add(index);
        }
        if (record.Collapsed)
            foreach (PartitionVersion input in record.Inputs) _collapsedInputs.Add(input);
        foreach (ulong partition in record.Inputs.Select(x => x.Partition)
                     .Concat(record.Outputs.Select(x => x.Partition)).Distinct())
        {
            // A tie on ClusterTime goes to the record indexed FIRST, which is exactly what the
            // stable OrderByDescending(ClusterTime).FirstOrDefault() above it picked.
            if (_retirement.TryGetValue(partition, out (long ClusterTime, bool Retired, int Index) current)
                && current.ClusterTime >= record.ClusterTime)
                continue;
            _retirement[partition] = (record.ClusterTime,
                record.Inputs.Any(y => y.Partition == partition)
                && record.Outputs.All(y => y.Partition != partition), index);
            MarkReplicaDirty(partition);
        }
    }

    private bool IsRetired(ulong partition) =>
        _retirement.TryGetValue(partition, out (long ClusterTime, bool Retired, int Index) entry) && entry.Retired;

    /// <summary>The lineage record that retired this partition (its latest record, in which it is an input and no output), or null while it lives.</summary>
    private LineageRecord? RetiringRecord(ulong partition) =>
        _retirement.TryGetValue(partition, out (long ClusterTime, bool Retired, int Index) entry) && entry.Retired
            && entry.Index >= 0 && entry.Index < _state.Lineage.Count
            ? _state.Lineage[entry.Index] : null;

    /// <summary>
    /// Records a mutation. The bytes reach the disk on the next <see cref="FlushDue"/> tick or
    /// at the next explicit commit point, not here - see Docs/StateDurability.md.
    /// </summary>
    private void Persist()
    {
        Volatile.Write(ref _authorityGeneration, _state.WorldAuthority.Generation);
        _state.StateSequence++;
        if (!_dirty)
        {
            _dirty = true;
            _flushDue = _clock() + PersistInterval;
        }
        if (PersistInterval > TimeSpan.Zero) return;
        FlushState();
    }

    /// <summary>True when a mutation is waiting and its interval has elapsed.</summary>
    public bool FlushDue
    {
        get { lock (_lock) return _dirty && _clock() >= _flushDue; }
    }

    /// <summary>
    /// A consistent copy of the registry database for an online cluster backup: pending mutations are written
    /// first, then the copy reads the committed state while the registry keeps working.
    /// </summary>
    public void CopyStore(string destination)
    {
        Flush();
        _store.CopyTo(destination);
    }

    /// <summary>Writes any pending mutation out now. Safe and cheap when nothing is pending.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            if (_dirty) FlushState();
        }
    }

    /// <summary>
    /// SE-842: the last commit before the process exits on SIGTERM. The lock stays taken for the rest of
    /// the process, so nothing can be acknowledged after this commit: a caller blocked on it sees its
    /// connection drop and retries against the respawned Gateway. Its own thread, because the commit
    /// must not keep the process alive when a lock holder or the disk is stuck: false after <paramref name="timeout"/>.
    /// </summary>
    public bool FlushAndHold(TimeSpan timeout)
    {
        bool committed = false;
        ManualResetEventSlim finished = new();
        new Thread(() =>
        {
            Monitor.Enter(_lock);
            try
            {
                if (_dirty) FlushState();
                committed = true;
            }
            catch (Exception error) { Console.Error.WriteLine("WARNING: final registry flush failed: " + error.Message); }
            finished.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true, Name = "Registry-Final-Flush" }.Start();
        return finished.Wait(timeout) && committed;
    }

    private void FlushState()
    {
        EnsurePersistenceHealthy();
        long sequence = _state.StateSequence;
        try { WritePendingState(); }
        catch (Exception error) { _persistenceFailure = error; throw; }
        _dirty = false;
        _flushDue = DateTimeOffset.MaxValue;
        _durableSequence = sequence;
    }

    /// <summary>The <see cref="RegistryState.StateSequence"/> the last completed write covered.</summary>
    private long _durableSequence;

    /// <summary>
    /// T-0227: one SQLite transaction with the rows changed since the last write - milliseconds, where
    /// the old full-state serialise + deflate + fsync held the registry lock for ~3 s on w32.
    /// </summary>
    internal object LockForSelfTest => _lock;
    internal long StoreCommits { get { lock (_lock) return _store.Commits; } }
    internal int ArchivedPartitionCount { get { lock (_lock) return _archivedPartitions.Count; } }
    internal long ArchiveRehydrations { get { lock (_lock) return RehydratedPartitions; } }
    internal long StoreRowsWritten { get { lock (_lock) return _store.RowsWritten; } }
    internal static uint LegacyCrc32(byte[] bytes) => Crc32(bytes);
    internal long StoreAuditRepairs { get { lock (_lock) return _store.AuditRepairs; } }

    /// <summary>Self-test hook: a mutation that bypasses change tracking, as a missing Touch would.</summary>
    internal void TestUntrackedChange(ulong partition, long generationDelta)
    {
        lock (_lock)
        {
            ((Dictionary<ulong, PartitionOwnership>)_state.Partitions)[partition].Generation += generationDelta;
            Persist();
        }
    }

    internal void TestPersist() { lock (_lock) Persist(); }

    // T-0227: a row changed IN PLACE is not seen by its tracked collection; these mark it for the next write.
    private void Touch(PartitionOwnership owner) => _state.Partitions.Touch(owner.Partition);
    private void Touch(SaveRegistration save)
    {
        _state.Saves.Touch(Key(save.SaveId));
        MarkReplicaDirty(save.SaveId.Partition);
    }
    private void Touch(GlobalSaveRegistration save) => _state.GlobalSaves.Touch(GlobalKey(save.SaveId));
    private void Touch(HandoverRecord handover) => _state.Handovers.Touch(handover.Id);

    private void WritePendingState()
    {
        _store.Write(_state);
        if (VerifyStoreAfterWrite)
        {
            VerifyStore();
            VerifyReplicaQueue();
        }
        // The live safety net: every live partition row and a rotating slice of every table are
        // compared with what the store holds; a row changed without a Touch is repaired by the next
        // write (and logged, so the missing Touch can be fixed). About 10 ms under the lock.
        if (++_writesSinceAudit >= StoreAuditEvery)
        {
            _writesSinceAudit = 0;
            _store.Audit(_state, StoreAuditBudget, _state.Partitions.Values
                .Where(owner => owner.Node != null || owner.Recovering)
                .Select(owner => ("Partitions", (object)owner.Partition)));
        }
    }

    private int _writesSinceAudit;
    private static readonly int StoreAuditEvery = EnvInt("CLUSTER_STORE_AUDIT_EVERY", 1);
    private static readonly int StoreAuditBudget = EnvInt("CLUSTER_STORE_AUDIT_BUDGET", 50);
    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    /// <summary>Self-test switch: after every write, reload the store and compare it with the live state.</summary>
    internal static bool VerifyStoreAfterWrite;

    /// <summary>The first divergence <see cref="VerifyStore"/> found. Sticky, because some flushes (Dispose)
    /// swallow their exceptions; the self-test runner checks it after every section.</summary>
    internal static Exception? StoreDivergence;

    /// <summary>
    /// The completeness guard for change tracking: a mutation that reached the live state without a
    /// Changed/Touch is missing from the store, and shows here as a difference. Serialises both whole
    /// states, so it is for the self-test only.
    /// </summary>
    private void VerifyStore()
    {
        RegistryState stored = _store.LoadFresh<RegistryState>();
        // The archive: disjoint from the live catalogue, and summarised exactly by _archivedPartitions.
        Dictionary<string, byte[]> archived = _store.ArchivedRowsFresh();
        string? both = archived.Keys.FirstOrDefault(key => _state.Saves.ContainsKey(key));
        HashSet<ulong> archivedPartitions = archived.Keys.Select(key => ulong.Parse(key.AsSpan(0, key.IndexOf(':')),
            provider: System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        if (both != null || !archivedPartitions.SetEquals(_archivedPartitions.Keys) || archived.Count != _store.ArchivedRows)
            throw StoreDivergence = new InvalidOperationException($"Registry archive diverged: {archived.Count} rows stored ({_store.ArchivedRows} counted), "
                + $"{archivedPartitions.Count} partitions stored vs {_archivedPartitions.Count} in memory; key in both: {both ?? "none"}");
        // T-0400: exactly one archive stamp per archived partition.
        Dictionary<ulong, RegistryStore.ArchiveStamp> stamps = _store.StampsFresh();
        if (!stamps.Keys.ToHashSet().SetEquals(_archivedPartitions.Keys))
            throw StoreDivergence = new InvalidOperationException($"Registry archive stamps diverged: {stamps.Count} stamps stored for "
                + $"{_archivedPartitions.Count} archived partitions; first unmatched: "
                + $"{stamps.Keys.Except(_archivedPartitions.Keys).Concat(_archivedPartitions.Keys.Except(stamps.Keys)).FirstOrDefault()}");
        foreach (System.Reflection.PropertyInfo property in typeof(RegistryState).GetProperties())
        {
            if (!RegistryStore.IsPersisted(property)) continue;
            string live = Canonical(property.GetValue(_state), property.PropertyType);
            string disk = Canonical(property.GetValue(stored), property.PropertyType);
            if (live != disk)
                throw StoreDivergence = new InvalidOperationException($"Registry store diverged from the live state in {property.Name}: "
                    + $"live {live.Length} bytes, stored {disk.Length} bytes; first difference at "
                    + $"{Enumerable.Range(0, Math.Min(live.Length, disk.Length)).FirstOrDefault(i => live[i] != disk[i], Math.Min(live.Length, disk.Length))}: "
                    + $"live ...{Excerpt(live, disk)}");
        }
    }

    private static string Excerpt(string live, string disk)
    {
        int at = Enumerable.Range(0, Math.Min(live.Length, disk.Length)).FirstOrDefault(i => live[i] != disk[i], Math.Min(live.Length, disk.Length));
        int from = Math.Max(0, at - 80);
        return live.Substring(from, Math.Min(200, live.Length - from)) + " | stored ..." + disk.Substring(from, Math.Min(200, disk.Length - from));
    }

    /// <summary>Order-insensitive JSON of a dictionary (rows sorted by key); other values as serialised.</summary>
    private static string Canonical(object? value, Type type)
    {
        if (value is System.Collections.IDictionary dictionary)
            return string.Join("\n", dictionary.Keys.Cast<object>()
                .Select(key => RegistryStore.FormatKey(key) + "=" + JsonSerializer.Serialize(dictionary[key]))
                .OrderBy(x => x, StringComparer.Ordinal));
        if (value is System.Collections.IEnumerable sequence and not string && type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(TrackedSet<>))
            return string.Join(",", sequence.Cast<object>().Select(RegistryStore.FormatKey).OrderBy(x => x, StringComparer.Ordinal));
        return JsonSerializer.Serialize(value, type);
    }

    private void RecoverWal()
    {
        if (!File.Exists(_walPath)) return;
        using FileStream file = new(_walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long valid = 0;
        long snapshotSequence = _state.StateSequence;
        Span<byte> header = stackalloc byte[HeaderSize];
        while (file.Position < file.Length)
        {
            long recordStart = file.Position;
            if (file.Length - file.Position < HeaderSize || file.Read(header) != HeaderSize) break;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            // CGR1 records were written raw by builds before
            // 2026-08-19, CGR2 records are compressed. A WAL may legitimately contain a run of
            // each across an upgrade, plus CGR3 plugin deltas. Decide per record, not file.
            if ((magic != Magic && magic != MagicCompressed && magic != MagicPluginMutation) || length < 0 || length > 64 * 1024 * 1024)
                throw new InvalidDataException($"Invalid registry WAL record at {recordStart}");
            if (file.Length - file.Position < length) break;
            byte[] payload = new byte[length];
            file.ReadExactly(payload);
            // The checksum covers the STORED bytes, so corruption is caught before inflating -
            // handing a truncated deflate stream to the decompressor would throw something far
            // less specific than "corrupt record at offset N".
            if (Crc32(payload) != checksum)
                throw new InvalidDataException($"Corrupt registry WAL record at {recordStart}");
            if (magic == MagicPluginMutation) RecoverPluginWrite(payload);
            else
            {
                var recovered = ReadState(magic == MagicCompressed ? Decompress(payload) : payload);
                // A snapshot can be published just before a crash leaves the old WAL intact.
                // Never replay covered records over that newer snapshot.
                if (recovered.StateSequence > snapshotSequence || recovered.StateSequence == 0 && snapshotSequence == 0)
                    _state = recovered;
            }
            valid = file.Position;
            _records++;
        }
        if (valid != file.Length)
        {
            file.SetLength(valid);
            file.Flush(true);
        }
    }

    /// <summary>
    /// Loads a snapshot file, accepting both the headered/compressed form and the bare JSON that
    /// builds before 2026-08-19 wrote. An upgrade therefore needs no migration step: the first
    /// flush rewrites the file in the new form.
    /// </summary>
    private static RegistryState ReadSnapshot(string path)
    {
        byte[] stored = File.ReadAllBytes(path);
        if (stored.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(stored) != MagicCompressed)
            return ReadState(stored);          // legacy: the whole file is raw JSON
        int length = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(4, 4));
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(8, 4));
        if (length < 0 || stored.Length - HeaderSize < length)
            throw new InvalidDataException($"Registry snapshot {path} is truncated");
        byte[] payload = stored[HeaderSize..(HeaderSize + length)];
        if (Crc32(payload) != checksum)
            throw new InvalidDataException($"Registry snapshot {path} failed its checksum");
        return ReadState(Decompress(payload));
    }

    private static string Key(SaveId id) => $"{id.Partition}:{id.Generation}:{id.Sequence}";
    private static string GlobalKey(GlobalSaveId id) => $"{id.Generation}:{id.Sequence}";
    private static RegistryState ReadState(byte[] json)
    {
        RegistryState state = JsonSerializer.Deserialize<RegistryState>(json) ?? throw new InvalidDataException("Empty registry state");
        if (state.Saves.Values.All(x => x.Locations != null))
        {
            foreach (SaveRegistration save in state.Saves.Values)
            {
                save.LocationHolders ??= [];
                save.HolderEndpoints ??= [];
                save.CorruptHolders ??= [];
                foreach (string location in save.Locations) save.LocationHolders.TryAdd(location, save.Node);
            }
            return state;
        }

        // Migrate the original timestamp/id catalog persisted by earlier gateway builds.
        state.Saves.Clear();
        foreach (JsonProperty property in JsonDocument.Parse(json).RootElement.GetProperty("Saves").EnumerateObject())
        {
            JsonElement old = property.Value;
            string node = old.GetProperty("Node").GetString()!;
            long epoch = old.GetProperty("Epoch").GetInt64();
            long generation = old.GetProperty("Generation").GetInt64();
            string location = old.GetProperty("Location").GetString()!;
            DateTimeOffset created = old.GetProperty("Created").GetDateTimeOffset();
            foreach (ulong partition in old.GetProperty("Partitions").EnumerateArray().Select(x => x.GetUInt64()))
            {
                long sequence = state.Saves.Values.Where(x => x.SaveId.Partition == partition && x.SaveId.Generation == generation)
                    .Select(x => x.SaveId.Sequence).DefaultIfEmpty(0).Max() + 1;
                var id = new SaveId(partition, generation, sequence);
                SaveRegistration save = new SaveRegistration(id, node, epoch, SaveKind.Periodic, 0, property.Name, created.UtcTicks, [location], created) { LegacyId = property.Name };
                save.LocationHolders[location] = node;
                state.Saves[Key(id)] = save;
            }
        }
        return state;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private sealed class RegistryState
    {
        /// <summary>
        /// Monotonic mutation counter, written with the state (Docs/StateDurability.md).
        /// A recovered snapshot identifies itself by this, not by its file mtime: wall-clock
        /// has finite precision, is not monotonic across a restart, and cannot order two
        /// records written inside the same tick. Peers reconcile against this number.
        /// </summary>
        public ManagedDeployment? ManagedDeployment { get; set; }
        public Dictionary<Guid, PluginLifecycleOutcome> PluginLifecycles { get; set; } = [];
        public PluginSdk.Clustering.PluginRecordStore PluginStore { get; set; } = new();
        public Guid? LastManagedRestore { get; set; }
        public Dictionary<string, ManagedProof> ManagedProofs { get; set; } = [];
        public long StateSequence { get; set; }
        public AccountingClockSample? AccountingClock { get; set; }
        public TrackedList<AccountingSunRun> AccountingSun { get; set; } = [];
        public Dictionary<string, NodeRegistration> Nodes { get; set; } = [];
        public WorldAuthority WorldAuthority { get; set; } = new(null, 0, 0);
        public TrackedDictionary<ulong, PartitionOwnership> Partitions { get; set; } = [];
        public TrackedDictionary<string, SaveRegistration> Saves { get; set; } = [];
        public TrackedDictionary<string, GlobalSaveRegistration> GlobalSaves { get; set; } = [];
        public Dictionary<string, VoxelBaseRegistration> VoxelBases { get; set; } = [];
        public TrackedDictionary<ulong, long> PartitionWeights { get; set; } = [];
        public Dictionary<string, VoxelJournalRegistration> VoxelJournals { get; set; } = [];
        public Dictionary<string, VoxelReplicaRegistration> VoxelReplicas { get; set; } = [];
        public TrackedDictionary<string, HandoverRecord> Handovers { get; set; } = [];
        public Dictionary<ulong, HandoverBlockRecord> HandoverBlocks { get; set; } = [];
        public TrackedList<LineageRecord> Lineage { get; set; } = [];
        public List<SnapshotRegistration> Snapshots { get; set; } = [];
        public List<NetworkIdBlock> NetworkIdBlocks { get; set; } = [];
        /// <summary>T-0344: blocks freed by cold starts so far (ReclaimNetworkIdBlocksAtColdStart); absent in older states.</summary>
        public long NetworkIdBlocksReclaimed { get; set; }
        public List<EntityIdRange> EntityIdRanges { get; set; } = [];
        /// <summary>Offset bits of the persisted range layout; absent in states written before
        /// 2026-08-21, which used 32 (see MigrateEntityIdRangeLayout).</summary>
        public int EntityIdRangeOffsetBits { get; set; } = 32;
        public ulong NextPartitionId { get; set; } = 1;
        public uint NextEntityIdRange { get; set; } = 1;
        public TrackedSet<ulong> AllocatedPartitionIds { get; set; } = [];
        public ClusterPhase Phase { get; set; } = ClusterPhase.Bootstrapping;
        public StartupKind Startup { get; set; } = StartupKind.Recovery;
        public DateTimeOffset? ShutdownStarted { get; set; }
        public ShutdownMarker? CleanShutdown { get; set; }
        public ShutdownMarker? LastShutdown { get; set; }
        public Dictionary<string, NodeSlotPlan> NodePlan { get; set; } = [];
        public Dictionary<string, AdminOperationRecord> AdminOperations { get; set; } = [];
        /// <summary>Bumped on every admin policy update; the CAS token for PUT /admin/v1/config.</summary>
        public long PolicyRevision { get; set; }
        public Admin.RotationStatus Rotation { get; set; } = new(0, null, null, false, []);
        /// <summary>HandoverImprovements.md §2: the runtime handover knobs, fanned out to every node and
        /// handed to each at registration; keys are the nodes' ClusterConfig keys. Durable so a gateway
        /// restart keeps the experiment's setting.</summary>
        public Dictionary<string, string> HandoverConfig { get; set; } = [];
        public long HandoverConfigRevision { get; set; }
        public Dictionary<string, ExecutorRegistration> Executors { get; set; } = [];
        public DateTimeOffset NodePlanUpdated { get; set; }
        public HashSet<string> ShutdownNodes { get; set; } = [];
        /// <summary>Node ids currently designated hot spares (HotSpareNodes.md). Keyed by node id,
        /// not incarnation, so a spare that crashes and re-registers is still a spare; durable so a
        /// gateway restart keeps the designation.</summary>
        public HashSet<string> SpareNodes { get; set; } = [];
        [JsonIgnore]
        public Dictionary<ulong, ClientPartition> ClientPartitions { get; set; } = [];
        // Durable, unlike the routes above. Clustering §6.4 has the Gateway answer a join with "the node
        // owning the partition containing the client's character", and §6.3 keeps routes soft because
        // they are "re-derived per client at rejoin" - but nothing durable could say where a character
        // was, so the derivation had nothing to read and every join fell back to the default backend.
        // A player whose body was on another node then spawned a second one beside it.
        public Dictionary<ulong, CharacterHome> CharacterHomes { get; set; } = [];
        /// <summary>The last <see cref="CharacterHome.Version"/> handed out; never reused.</summary>
        public long HomeVersion { get; set; }
        /// <summary>T-0193: partitions taken offline (unowned by design, loaded only on demand); see OfflinePartitions.cs.</summary>
        public Dictionary<ulong, OfflinePartition> OfflinePartitions { get; set; } = [];
        /// <summary>Bumped on every offline row insert/delete, so pollers can ask "changed since".</summary>
        public long OfflineRevision { get; set; }
        /// <summary>T-0312: desired regular node count and the scale-in retiring/retired sets (NodeScaling.cs).</summary>
        public ScaleState Scale { get; set; } = new();
        /// <summary>T-0320: per-host targets, the id counter and the live node records (NodeControl.cs).</summary>
        public NodeControlState NodeControl { get; set; } = new();
        /// <summary>T-0320: stopped nodes, newest last, bounded (NodeControl.cs). Tracked: appended row by row.</summary>
        public TrackedList<NodeHistoryRecord> NodeHistory { get; set; } = [];
        /// <summary>T-0340: legacy node id -> the host its save files lie on, frozen when first resolved (HostStore.cs).</summary>
        public Dictionary<string, string> HostStoreAliases { get; set; } = [];
        /// <summary>T-0400: archived save locations whose file archive retention deleted (acknowledged), ever.</summary>
        public long ArchiveFilesDeleted { get; set; }
    }
}

internal readonly record struct SaveId(ulong Partition, long Generation, long Sequence) : IComparable<SaveId>
{
    public int CompareTo(SaveId other)
    {
        int generation = Generation.CompareTo(other.Generation);
        return generation != 0 ? generation : Sequence.CompareTo(other.Sequence);
    }
}
internal readonly record struct GlobalSaveId(long Generation, long Sequence) : IComparable<GlobalSaveId>
{
    public int CompareTo(GlobalSaveId other)
    {
        int generation = Generation.CompareTo(other.Generation);
        return generation != 0 ? generation : Sequence.CompareTo(other.Sequence);
    }
}
internal readonly record struct VoxelRef(string StorageName, long BaseVersion, long TailSeq);
internal sealed record HandoverConfigDocument(long Revision, IDictionary<string, string> Values);
internal enum SaveKind { Periodic, HandoverTee, Final, Sweep, Conversion, Repair }
internal enum HandoverState { Begun, Active, Committed, Aborted }
internal enum LineageKind { Split, Merge }
internal enum SnapshotKind { CleanShutdown, Online, Conversion }
internal enum NodeState { Active, Closed, Dead, Empty }
internal enum ClusterPhase { Bootstrapping, Serving, Degraded, Draining, Down }
internal enum StartupKind { Warm, Recovery }
internal enum NodeRole { Regular, WA }
internal enum NodeDesiredState { Wanted, Draining, Kill }
internal enum NodeIncumbentAction { None, Draining, Kill }
internal enum SpawnState { Missing, Spawning, Ready, Failed, Gone }
internal sealed record NodeRegistration(string Node, long Epoch, string Endpoint, DateTimeOffset Registered, NodeState State = NodeState.Active,
    DateTimeOffset LeaseExpires = default, Guid Instance = default, NodeRole Role = NodeRole.Regular,
	long AuthorityGeneration = 0, string? ControlEndpoint = null)
{
    public NodeState State { get; set; } = State;
    public DateTimeOffset LeaseExpires { get; set; } = LeaseExpires;
    public DateTimeOffset? ForceDrainAt { get; set; }
    public long SimulationFrame { get; set; } = -1;
    public DateTimeOffset SimulationProgress { get; set; }
    public SaveFailureReport? LastSaveFailure { get; set; }
    public DateTimeOffset? LastSaveFailureAt { get; set; }
    public long RecoveryCapacity { get; set; }
    public long ProjectedLoad { get; set; }
    public long LoadCapacity { get; set; }
    public long RecoveryLoadsPending { get; set; }
    public ulong[] Quarantined { get; set; } = Array.Empty<ulong>();
    /// <summary>Hot spare (HotSpareNodes.md): registered and warm but excluded from every
    /// placement decision until activated. Mirrors RegistryState.SpareNodes for status readers.</summary>
    public bool Spare { get; set; }
    /// <summary>The node's own lifecycle has reached Serving (session loaded, voxel replica caught
    /// up, claims registered). State=Active is set at REGISTRATION, minutes before that, and every
    /// placement decision used to filter on it alone - so the balancer, pod placement and the
    /// unrouted-client pick could all hand work to a node that had not loaded its world yet
    /// (T-0053/T-0059). Reported by each heartbeat; a heartbeat that omits it counts as serving.</summary>
    public bool Serving { get; set; }
    /// <summary>When this incarnation started SERVING - registration for a normal node, the
    /// SetNodeSpare(false) activation for a spare, null while still a spare. The rotator counts
    /// lifetime from this, not from Registered: a spare idles first, and counting the idle time
    /// rotated w23's node-5 four minutes after activation, double-moving a whole drain.</summary>
    public DateTimeOffset? Activated { get; set; }
    /// <summary>T-0320: the host the node process runs on and its pid, as the node reported them at registration.</summary>
    public string? Host { get; set; }
    public int? Pid { get; set; }
    /// <summary>T-0340: the registry told this incarnation, in its registration answer, to keep its saves in its host's
    /// store (GatewayRegistry.NodeUsesHostStore). False: the node keeps them in its own data dir.</summary>
    public bool HostStore { get; set; }
}
internal sealed record WorldAuthority(string? Node, long Epoch, long Generation, DateTimeOffset LeaseExpires = default, string? ControlEndpoint = null)
{
    public string? Node { get; set; } = Node;
    public long Epoch { get; set; } = Epoch;
    public long Generation { get; set; } = Generation;
    public DateTimeOffset LeaseExpires { get; set; } = LeaseExpires;
	public string? ControlEndpoint { get; set; } = ControlEndpoint;
}
internal sealed record NetworkIdBlock(uint Start, uint End, string Node, long Epoch, DateTimeOffset Allocated);
internal sealed record EntityIdRange(uint Index, string Node, long Epoch, DateTimeOffset Allocated)
{
    public const int RangeBits = 24;
    public const uint MaxIndex = uint.MaxValue;
    public const long Size = 1L << RangeBits;
    public long Start => (long)Index << RangeBits;
    public long End => Start + Size - 1;
}
internal sealed record PartitionOwnership(ulong Partition, string? Node, long Epoch, long Generation, DateTimeOffset LeaseExpires = default)
{
    public string? Node { get; set; } = Node;
    public long Epoch { get; set; } = Epoch;
    public long Generation { get; set; } = Generation;
    public DateTimeOffset LeaseExpires { get; set; } = LeaseExpires;
    public bool Recovering { get; set; }
}
internal sealed record HandoverRecord(string Id, ulong Partition, string SourceNode, long SourceEpoch, long SourceGeneration, string TargetNode, long TargetEpoch, HandoverState State, long? CommittedGeneration)
{
    public HandoverState State { get; set; } = State;
    public long? CommittedGeneration { get; set; } = CommittedGeneration;
    public bool Reconciled { get; set; }
}

/// <summary>
/// Orchestration.md §3.3 / Partitioning.md §6.10: consecutive import round-trip validation aborts of one
/// partition, keyed by their residual signature. Attempts at or above the configured threshold put the
/// partition in the HandoverBlocked state - owned and healthy where it is, excluded from handover
/// scheduling until the exponential backoff expires. Cleared by a committed handover or the admin
/// trigger. A drifting signature and a membership change each used to reset the slate, which kept a
/// doomed handover churning forever (report 2026-08-09 §4): now a new signature continues the same
/// episode's count, and a membership change only releases the backoff for one prompt retry.
/// </summary>
internal sealed record HandoverBlockRecord(ulong Partition, string Signature, string Reason)
{
    public string Signature { get; set; } = Signature;
    public string Reason { get; set; } = Reason;
    public int Attempts { get; set; }
    public DateTimeOffset FirstAbort { get; set; }
    public DateTimeOffset LastAbort { get; set; }
    public DateTimeOffset? BackoffUntil { get; set; }
    /// <summary>SE-1134: the top-level entities the residuals are in, as the importing node named them. Null from an
    /// older node or a row persisted before it: a membership change then releases the backoff as before.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long[]? Entities { get; set; }
}
internal sealed record PartitionVersion(ulong Partition, long Generation, long Members = 0);
internal sealed record SaveRegistration(SaveId SaveId, string Node, long Epoch, SaveKind Kind, long SizeBytes, string Checksum, long ClusterTime, HashSet<string> Locations, DateTimeOffset Created)
{
    public bool PendingDelete { get; set; }
    public string? LegacyId { get; set; }
    /// <summary>T-0237: character entity -> the home version the saving node held for that character IN THIS
    /// partition when it captured the save (standing and seated alike). Null for a save from an older build.</summary>
    public Dictionary<long, long>? HomeStamps { get; init; }
    /// <summary>How many entities the save file holds (its header's EntityCount). Null for a save from an older
    /// build or one amended in place. Zero proves the partition's content was empty at that capture.</summary>
    public int? Entities { get; init; }
    public string Id => LegacyId ?? $"{SaveId.Partition}:{SaveId.Generation}:{SaveId.Sequence}";
    public ulong[] Partitions => [SaveId.Partition];
    public long Generation => SaveId.Generation;
    public string Location => Locations.FirstOrDefault() ?? string.Empty;
	public Dictionary<string, string> LocationHolders { get; set; } = [];
	public Dictionary<string, string> HolderEndpoints { get; set; } = [];
	public HashSet<string> CorruptHolders { get; set; } = [];
	/// <summary>T-0323: gone holder -> the live node on its host that serves its files. Only on the snapshots handed to a node
	/// that will fetch the save (recovery claims), never on the stored registration.</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, HolderProxy>? HolderProxies { get; set; }
	/// <summary>T-0400: locations whose file archive retention deleted (or handed out for deletion when the partition was
	/// brought back before the ack), and when. The row is kept for its history; these are no longer in <see cref="Locations"/>.
	/// Null (not written) on every save retention never touched, so no stored row changes bytes.</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, DateTimeOffset>? DeletedLocations { get; set; }
}
internal sealed record GlobalSaveRegistration(GlobalSaveId SaveId, string Node, long Epoch, SaveKind Kind, long SizeBytes, string Checksum, long ClusterTime, HashSet<string> Locations, DateTimeOffset Created)
{
    public bool PendingDelete { get; set; }
    public string Id => $"GLOBAL:{SaveId.Generation}:{SaveId.Sequence}";
    public long Generation => SaveId.Generation;
    public long Sequence => SaveId.Sequence;
    public string Location => Locations.FirstOrDefault() ?? string.Empty;
    public Dictionary<string, string> LocationHolders { get; set; } = [];
    public Dictionary<string, string> HolderEndpoints { get; set; } = [];
}
internal sealed record VoxelBaseRegistration(string StorageName, long BaseVersion, long TailSequence, string Node, long Epoch,
    long AuthorityGeneration, long SizeBytes, string Checksum, string Location, DateTimeOffset Created);
internal sealed record VoxelJournalRegistration(string StorageName, long BaseVersion, long TailSeq, string Node, long Epoch,
    long AuthorityGeneration, DateTimeOffset Reported);
internal sealed record VoxelReplicaRegistration(string StorageName, string Node, long Epoch, long Applied, DateTimeOffset Reported);
internal sealed record LineageRecord(string OpId, LineageKind Kind, PartitionVersion[] Inputs, PartitionVersion[] Outputs, Dictionary<ulong, long[]> Members, long ClusterTime, bool Collapsed);
internal sealed record SaveCover(SaveRegistration Save, long[]? EntityIds);
internal sealed record RecoveryClaim(PartitionOwnership Ownership, SaveCover[] ResolveCover, SaveCover[] Fallbacks,
    long[]? CharactersHomedElsewhere = null, HomeElsewhere[]? HomesElsewhere = null, Dictionary<long, long>? SaveHomeStamps = null,
    bool SaveStamped = false, DateTimeOffset? SaveCreated = null, long SaveCaptured = 0);
/// <summary>T-0237: a player's home in ANOTHER partition that really holds the body - so the node can drop ANY
/// older body of that identity from recovered content, not only a copy with the home's own entity id.
/// <see cref="Captured"/> is the cluster-clock capture time of the home partition's newest save that stamps the
/// body (0 = none): a stamped recovered save WITHOUT a stamp for the home's own body is stale when every covered
/// save was captured before it (<see cref="RecoveryClaim.SaveCaptured"/>).</summary>
internal sealed record HomeElsewhere(long Identity, long Entity, ulong Partition, long Version, DateTimeOffset Updated, long Captured = 0);
internal sealed record SnapshotRegistration(string SnapshotId, SnapshotKind Kind, long ClusterTime, SaveId[] PartitionRefs, string[] Dirty,
    GlobalSaveId? GlobalRef = null, VoxelRef[]? VoxelRefs = null);
internal sealed record DeleteDirective(SaveId SaveId, string Node, string Location);
/// <summary>A replica source. <see cref="Proxy"/> set: the holder is gone and <see cref="Endpoint"/> is the proxy's, which serves
/// exactly <see cref="Location"/> under a grant naming the proxy and that location (T-0323).</summary>
internal sealed record HolderSource(string Holder, string Endpoint, string? Proxy = null, string? Location = null);
internal sealed record ReplicaDirective(SaveId SaveId, long SizeBytes, string Checksum, HolderSource[] Sources, bool Global = false);
internal sealed record GrantRequest(string Operation, string TransferId, ulong Partition, long Generation, long Sequence,
    string Requester, long RequesterEpoch, string Receiver, string? Location = null);
/// <summary><see cref="Location"/>: a proxied save grant (T-0323) - the proxy serves exactly this catalogued path.</summary>
internal sealed record GrantValidation(string Token, string Operation, string TransferId, ulong Partition, long Generation,
    long Sequence, string Receiver, long ReceiverEpoch, string? Location = null);
/// <summary>T-0340: <see cref="Host"/> set - a save grant bound to (save id, host): any live node of that host may serve it,
/// <see cref="Servers"/> in the order to try them. Absent from the wire for every other grant.</summary>
internal sealed record TransferGrant(string Token, string Operation, string TransferId, ulong Partition, long Generation,
    long Sequence, string Receiver, DateTimeOffset Expires, string? Location = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HolderProxy[]? Servers = null);
internal sealed record ClientPartition(ulong Partition, long Entity);
/// <summary>Where a client's character was last seen, durable across restarts and log-offs.</summary>
/// <summary>A client's durable character home. <see cref="Version"/> orders home changes (T-0237): it comes from one
/// registry-wide counter, bumps on every change and never on a repeated report, and a node writes the version it was
/// answered beside each character of a save (<see cref="SaveRegistration.HomeStamps"/>), so "is this copy older than
/// the home" is a comparison of two versions, not of two wall-clock stamps taken at different moments.
/// <see cref="Updated"/> is kept for diagnostics (correlating logs) and orders nothing for a stamped save.</summary>
internal sealed record CharacterHome(long Identity, long Entity, ulong Partition, string Node, DateTimeOffset Updated,
    long Version = 0, long WriteRevision = 0, ClusterProtocol.CharacterReapPermit? PendingReap = null,
    ulong PendingReapPartition = 0);
internal sealed record ClientPartitionReport(ulong Client, ulong Partition, long Entity, long Generation);
internal sealed record ClientRoute(ulong Client, ulong Partition, string Node, long Epoch, long Generation, string Endpoint);
internal sealed record SaveFailureReport(ulong Partition, string Error, int Attempts);
internal sealed record ShutdownMarker(DateTimeOffset Timestamp, SaveId[] Manifest, string[] Dirty, GlobalSaveId? GlobalRef = null, VoxelRef[]? VoxelRefs = null);
internal sealed record RebuildSaveReport(ulong Partition, long Generation, long Sequence, SaveKind Kind, long SizeBytes, string Checksum, long ClusterTime, string Location, string Node);
internal sealed record RebuildGlobalSaveReport(long Generation, long Sequence, SaveKind Kind, long SizeBytes, string Checksum, long ClusterTime, string Location, string Node);
internal sealed record RebuildVoxelBaseReport(string StorageName, long BaseVersion, long TailSequence, long SizeBytes, string Checksum, string Location, string Node);
internal sealed record RebuildVoxelJournalReport(string StorageName, long BaseVersion, long TailSeq, string Node);
internal sealed record RebuildRequest(RebuildSaveReport[]? Saves, RebuildGlobalSaveReport[]? GlobalSaves, uint? NextEntityIdRange = null,
    RebuildVoxelBaseReport[]? VoxelBases = null, RebuildVoxelJournalReport[]? VoxelJournals = null);
internal sealed record RebuildSummary(int Partitions, int Saves, int GlobalSaves, long WorldAuthorityGeneration, ulong NextPartitionId, uint NextEntityIdRange,
    int VoxelBases = 0, int VoxelJournals = 0);
internal sealed record ClusterLifecycle(ClusterPhase Phase, StartupKind Startup, DateTimeOffset? ShutdownStarted,
    ShutdownMarker? LastShutdown, bool ExecutorSilent, bool GlobalSpawnHalted, string[] DegradedReasons, long? GlobalSaveAgeSeconds = null);
internal sealed record ExecutorRegistration(string Executor, DateTimeOffset LastHeartbeat);
internal sealed record SpawnObservation(string SlotKey, string AttemptKey, SpawnState State, string? Node, string? Failure, int? Pid = null, long Epoch = 0);
internal sealed record NodeSlotPlan(string SlotKey, string Host, NodeRole Role, NodeDesiredState Desired)
{
    public string Host { get; set; } = Host;
    public NodeRole Role { get; set; } = Role;
    public NodeDesiredState Desired { get; set; } = Desired;
    public SpawnState Observed { get; set; }
    public string? ObservedNode { get; set; }
    /// <summary>The incarnation the executor last named: the one it knows it runs, and so the one it can be told to kill.</summary>
    public long ObservedEpoch { get; set; }
    public NodeIncumbentAction IncumbentAction { get; set; }
    public string? IncumbentNode { get; set; }
    public long IncumbentEpoch { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public string? LastAttemptKey { get; set; }
    public string? LastFailure { get; set; }
    public List<DateTimeOffset> Failures { get; set; } = [];
    public DateTimeOffset? RetryAt { get; set; }
    public int FailureCount => Failures.Count;
    public bool FailureAlert => Failures.Count >= 3;
    // Serialized for the executor's plan reads; NodePlan() recomputes it on every request, so a
    // stale persisted value is never observable.
    public bool SpawnAllowed { get; init; }
}
internal enum AdminOperationPhase { Running, Succeeded, Failed }

/// <summary>Durable record behind one admin-contract mutation; see BeginAdminOperation.</summary>
internal sealed class AdminOperationRecord
{
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    /// <summary>Hash of the request body/parameters, binding the idempotency key to one exact request.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? Target { get; set; }
    public AdminOperationPhase State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Dictionary<string, string>? Result { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
internal class RegistryConflictException(string message) : InvalidOperationException(message);

/// <summary>T-0354b: a handover refused because a registry request about the partition is unanswered on its source (a new
/// layout's id claim, a topology apply). Nothing was frozen; DirectGateway.WhenRegistryApplied retries a requested move.</summary>
internal sealed class RegistryApplyPendingRefusal(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>A conflict that resolves by itself with time (a live lease that will lapse) — answered
/// as 503 so the registrant's retry loop rides it out, where a 409 is a definitive rejection the
/// node treats as terminal (Orchestration §4.2). Born from P3-SUN-01's warm restart: the new wa-1
/// registered before the killed wa-1's lease expired and fail-fasted on the 409.</summary>
internal sealed class RegistryBusyException(string message) : InvalidOperationException(message);

internal sealed record NodeJoinRequest(string Node, string Endpoint, Guid Instance, string Token, string ClusterId, string WorldId,
    string ClusterConfigHash, string BinaryVersion, string ReplicationTypeTableHash, string WorldSettingsHash, double SyncDistance,
    NodeRole Role = NodeRole.Regular, string? ControlEndpoint = null, string? Host = null, int? Pid = null, bool HostStore = false);

internal sealed class NodeAdmission
{
    private readonly byte[] _currentTokenHash;
    private readonly byte[] _previousTokenHash;
    private readonly bool _hasPrevious;
    private readonly string _clusterId;
    private readonly string _worldId;
    private readonly string _clusterConfigHash;
    private readonly string _binaryVersion;
    private readonly string _replicationTypeTableHash;
    private readonly string _worldSettingsHash;
    private readonly double _syncDistance;

    public NodeAdmission(string currentToken, string? previousToken, string clusterId, string worldId, string clusterConfigHash,
        string binaryVersion, string replicationTypeTableHash, string worldSettingsHash, double syncDistance)
    {
        ValidateToken(currentToken, "CLUSTER_JOIN_TOKEN_CURRENT");
        if (previousToken != null) ValidateToken(previousToken, "CLUSTER_JOIN_TOKEN_PREVIOUS");
        _currentTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(currentToken));
        _previousTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(previousToken ?? string.Empty));
        _hasPrevious = previousToken != null;
        _clusterId = clusterId;
        _worldId = worldId;
        _clusterConfigHash = clusterConfigHash;
        ClusterId = clusterId;
        WorldId = worldId;
        _binaryVersion = binaryVersion;
        _replicationTypeTableHash = replicationTypeTableHash;
        _worldSettingsHash = worldSettingsHash;
        _syncDistance = double.IsFinite(syncDistance) && syncDistance > 0 ? syncDistance : throw new ArgumentException("CLUSTER_SYNC_DISTANCE must be positive");
    }

    /// <summary>The identities the run root's provenance stamp is checked against (Orchestration §5.1).</summary>
    public string ClusterId { get; }
    public string WorldId { get; }

    /// <summary>Deployment identity for the admin config view; hashes, not secrets.</summary>
    public string ClusterConfigHash => _clusterConfigHash;
    public string BinaryVersion => _binaryVersion;
    public string ReplicationTypeTableHash => _replicationTypeTableHash;
    public string WorldSettingsHash => _worldSettingsHash;
    public double SyncDistance => _syncDistance;

    public static NodeAdmission FromEnvironment()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException($"{name} is required with --registry");
        string? previous = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_PREVIOUS");
        if (string.IsNullOrEmpty(previous)) previous = null;
        if (!double.TryParse(Required("CLUSTER_SYNC_DISTANCE"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double syncDistance))
            throw new ArgumentException("CLUSTER_SYNC_DISTANCE must be a number");
        return new NodeAdmission(Required("CLUSTER_JOIN_TOKEN_CURRENT"), previous, Required("CLUSTER_ID"), Required("CLUSTER_WORLD_ID"),
            Required("CLUSTER_CONFIG_HASH"), Required("CLUSTER_BINARY_VERSION"), Required("CLUSTER_REPLICATION_TABLE_HASH"),
            // SE-796: the world as activation left it, the same file the nodes on this host read.
            ClusterProtocol.WorldSettingsHash.Of(Path.Combine(Required("CLUSTER_WORLD"), "Sandbox.sbc")), syncDistance);
    }

    public void Validate(NodeJoinRequest request)
    {
        if (!AcceptsToken(request.Token ?? string.Empty)) Reject("join token");
        if (!string.Equals(request.ClusterId, _clusterId, StringComparison.Ordinal)) Reject("cluster identity");
        if (!string.Equals(request.WorldId, _worldId, StringComparison.Ordinal)) Reject("world identity");
        if (!string.Equals(request.ClusterConfigHash, _clusterConfigHash, StringComparison.Ordinal)) Reject("cluster config hash");
        if (!string.Equals(request.BinaryVersion, _binaryVersion, StringComparison.Ordinal)) Reject("binary version");
        if (!string.Equals(request.ReplicationTypeTableHash, _replicationTypeTableHash, StringComparison.Ordinal)) Reject("replication type table hash");
        if (!string.Equals(request.WorldSettingsHash, _worldSettingsHash, StringComparison.Ordinal)) Reject("world settings hash");
        if (!double.IsFinite(request.SyncDistance) || request.SyncDistance != _syncDistance) Reject("sync distance");
    }

    public bool AcceptsToken(string token)
    {
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        bool extra = false;
        foreach (byte[] hash in _extraTokenHashes) extra |= CryptographicOperations.FixedTimeEquals(supplied, hash);
        return CryptographicOperations.FixedTimeEquals(supplied, _currentTokenHash)
            | (_hasPrevious & CryptographicOperations.FixedTimeEquals(supplied, _previousTokenHash)) | extra;
    }

    private byte[][] _extraTokenHashes = [];

    /// <summary>T-0321: a copy that also accepts <paramref name="tokens"/> - the persisted join tokens of the running
    /// start after a gateway-only restart (StartCredentials). Same identity checks, same current token.</summary>
    public NodeAdmission Accepting(params string?[] tokens)
    {
        var copy = (NodeAdmission)MemberwiseClone();
        copy._extraTokenHashes = _extraTokenHashes.Concat(tokens.Where(token => !string.IsNullOrEmpty(token))
            .Select(token => SHA256.HashData(Encoding.UTF8.GetBytes(token!)))).ToArray();
        return copy;
    }

    private static void ValidateToken(string token, string name)
    {
        try
        {
            if (Convert.FromBase64String(token).Length >= 32) return;
        }
        catch (FormatException) { }
        throw new ArgumentException($"{name} must be base64 encoding of at least 32 bytes");
    }

    private static void Reject(string reason) => throw new RegistryConflictException("Node admission rejected: " + reason);
}
