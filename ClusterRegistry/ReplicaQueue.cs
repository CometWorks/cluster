namespace ClusterRegistry;

/// <summary>
/// T-0227: the replica work queue - every catalogued save with fewer than two usable holders, kept in
/// <see cref="NextReplica"/>'s order as the catalogue changes, instead of ranked from scratch.
/// </summary>
/// <remarks>
/// Ranking the whole catalogue visits every save (195k on w32, 43k of them single-holder): about 100 ms
/// under the registry lock however it is ordered, because it is bound by walking the save objects. Every
/// node polls about once a second, so ranking per poll held the lock for a third of the time or more
/// (live allocation trace, 2026-09-23 23:34).
///
/// A save's rank depends only on its own holders, on the other saves of its partition (newest, covered),
/// on whether its partition is retired, and on which nodes are usable or draining. So:
/// <list type="bullet">
/// <item>any change to a save marks its partition dirty (<see cref="PutSave"/>, <see cref="RemoveSave"/>,
/// <see cref="Touch(SaveRegistration)"/>), and so does a lineage record that changes a partition's
/// retirement; a poll re-ranks the dirty partitions' saves only. Marking rather than re-ranking in place
/// keeps the order of a <c>Touch</c> relative to its mutation irrelevant;</item>
/// <item>a change in any node's usability or draining state rebuilds the queue (rotation: a handful an
/// hour);</item>
/// <item>the self-test compares the queue with a full ranking after every registry write and every
/// answer with the brute-force choice; production does the full comparison every
/// <c>CLUSTER_REPLICA_AUDIT_SECONDS</c> (300), logs any drift and adopts the fresh ranking.</item>
/// </list>
/// </remarks>
internal sealed partial class GatewayRegistry
{
    /// <summary><see cref="Evacuate"/> (T-0323): the save lacks live copies only because its holders are gone - counting them it would
    /// be satisfied. Ranks after everything else and spends the evacuation budget.</summary>
    private readonly record struct ReplicaRank(bool AtRisk, bool Newest, bool Live, bool Exposed, DateTimeOffset Created, bool Evacuate = false);
    private readonly record struct ReplicaEntry(ReplicaRank Key, SaveRegistration Save);
    /// <summary>T-0321: Host and Takes (an Active regular holder that takes replica work) decide multi-host placement;
    /// a change of either rebuilds the queue like any usability change.</summary>
    /// <summary>T-0323: Gone - a stopped node's files (usable through a proxy) that no longer count toward the two copies.</summary>
    /// <summary>T-0340: Store - a host holder's row: the state of that host's save store (null for a node).</summary>
    private readonly record struct HolderState(string Node, bool Usable, bool Closed, string? Host = null, bool Takes = false, bool Gone = false,
        HostStoreState? Store = null);

    /// <summary>Best first; equal ranks by SaveId, so the order is total and a rebuild reproduces it exactly.</summary>
    private sealed class ReplicaOrder : IComparer<ReplicaEntry>
    {
        public static readonly ReplicaOrder Instance = new();
        public int Compare(ReplicaEntry a, ReplicaEntry b)
        {
            if (Better(a.Key, b.Key)) return -1;
            if (Better(b.Key, a.Key)) return 1;
            SaveId x = a.Save.SaveId, y = b.Save.SaveId;
            int c = x.Partition.CompareTo(y.Partition);
            if (c == 0) c = x.Generation.CompareTo(y.Generation);
            if (c == 0) c = x.Sequence.CompareTo(y.Sequence);
            return c;
        }
    }

    private SortedSet<ReplicaEntry> _replicaQueue = new(ReplicaOrder.Instance);
    private Dictionary<SaveRegistration, ReplicaEntry> _replicaEntries = new(ReferenceEqualityComparer.Instance);
    private Dictionary<ulong, List<SaveRegistration>> _replicaQueuedByPartition = [];
    private readonly HashSet<ulong> _replicaDirty = [];
    private HolderState[] _replicaHolders = [];
    private Dictionary<string, HolderState> _replicaHolderMap = new(StringComparer.Ordinal);
    private bool _replicaQueueReady;
    /// <summary>T-0321: more than one host has an Active regular node. Then a save needs holders on two hosts, not just two
    /// holders (a save whose holders all share a host is lost with that host). One host: exactly the old rule.</summary>
    private bool _replicaMultiHost;
    private DateTimeOffset _replicaAuditedAt;
    private static readonly TimeSpan ReplicaAuditInterval = TimeSpan.FromSeconds(EnvInt("CLUSTER_REPLICA_AUDIT_SECONDS", 300));

    /// <summary>Full rankings built (start, node-state change, audit). The self-test reads it.</summary>
    internal int ReplicaRebuilds { get; private set; }
    /// <summary>Audits that found the incremental queue different from a full ranking. Any is a missed mark.</summary>
    internal int ReplicaAuditRepairs { get; private set; }
    /// <summary>Self-test: first divergence of the queue from a full ranking or of an answer from the brute-force choice (sticky).</summary>
    internal static Exception? ReplicaDivergence;

    private void MarkReplicaDirty(ulong partition)
    {
        if (_replicaQueueReady) _replicaDirty.Add(partition);
    }

    private void InvalidateReplicaQueue() => _replicaQueueReady = false;

    private SaveRegistration? NextReplica(string node)
    {
        DateTimeOffset now = _clock();
        RefreshReplicaQueue(now);
        SaveRegistration? next = null;
        string? host = _state.Nodes.GetValueOrDefault(node)?.Host;
        foreach (ReplicaEntry entry in _replicaQueue)
        {
            if (EligibleReplica(entry.Save, node, host, now))
            {
                next = entry.Save;
                break;
            }
        }
        if (VerifyStoreAfterWrite)
        {
            SaveRegistration? expected = BruteForceNextReplica(node, now);
            if (!ReferenceEquals(expected, next))
                ReplicaDivergence ??= new InvalidOperationException(
                    $"replica queue answered {Describe(next)} for {node}, a full scan picks {Describe(expected)}");
        }
        return next;

        static string Describe(SaveRegistration? save) => save == null ? "nothing" : Key(save.SaveId);
    }

    /// <summary>Brings the queue up to date: rebuild on a node-state change or when due for audit, else re-rank the dirty partitions.</summary>
    private void RefreshReplicaQueue(DateTimeOffset now)
    {
        HolderState[] holders = CurrentHolderStates();
        if (!_replicaQueueReady || !holders.AsSpan().SequenceEqual(_replicaHolders) || _hostStore && HostCopyTarget() != _replicaCopyTarget)
        {
            RebuildReplicaQueue(holders, now);
            return;
        }
        foreach (ulong partition in _replicaDirty)
            RerankPartition(partition);
        _replicaDirty.Clear();
        if (now - _replicaAuditedAt >= ReplicaAuditInterval || now < _replicaAuditedAt)
            AuditReplicaQueue(holders, now);
    }

    private HolderState[] CurrentHolderStates()
    {
        var states = new HolderState[_state.Nodes.Count];
        int i = 0;
        foreach ((string name, NodeRegistration registration) in _state.Nodes)
        {
            bool usable = HolderUsable(name);
            states[i++] = new HolderState(name, usable, registration.State == NodeState.Closed, registration.Host,
                usable && registration.State == NodeState.Active && registration.Role == NodeRole.Regular,
                // T-0348: Empty, or Dead with a proxy (usable says so) - one definition, GoneHolder's.
                usable && GoneHolder(name) != null,
                _hostStore && IsHostHolder(name) ? StoreState(HostOfHolder(name)!) : null);
        }
        Array.Sort(states, (a, b) => string.CompareOrdinal(a.Node, b.Node));
        return states;
    }

    private void RebuildReplicaQueue(HolderState[] holders, DateTimeOffset now)
    {
        _replicaHolders = holders;
        _replicaHolderMap = holders.ToDictionary(x => x.Node, StringComparer.Ordinal);
        _replicaMultiHost = MultiHost(holders);
        _replicaCopyTarget = HostCopyTarget();
        (_replicaQueue, _replicaEntries, _replicaQueuedByPartition) = RankAll();
        _replicaDirty.Clear();
        _replicaQueueReady = true;
        _replicaAuditedAt = now;
        ReplicaRebuilds++;
    }

    private (SortedSet<ReplicaEntry>, Dictionary<SaveRegistration, ReplicaEntry>, Dictionary<ulong, List<SaveRegistration>>) RankAll()
    {
        var entries = new List<ReplicaEntry>();
        foreach (SaveRegistration save in _state.Saves.Values)
            if (RankOf(save) is ReplicaRank key)
                entries.Add(new ReplicaEntry(key, save));
        var queue = new SortedSet<ReplicaEntry>(entries, ReplicaOrder.Instance);
        var byEntry = new Dictionary<SaveRegistration, ReplicaEntry>(entries.Count, ReferenceEqualityComparer.Instance);
        var byPartition = new Dictionary<ulong, List<SaveRegistration>>();
        foreach (ReplicaEntry entry in entries)
        {
            byEntry[entry.Save] = entry;
            if (!byPartition.TryGetValue(entry.Save.SaveId.Partition, out List<SaveRegistration>? list))
                byPartition[entry.Save.SaveId.Partition] = list = [];
            list.Add(entry.Save);
        }
        return (queue, byEntry, byPartition);
    }

    private void RerankPartition(ulong partition)
    {
        if (_replicaQueuedByPartition.Remove(partition, out List<SaveRegistration>? queued))
            foreach (SaveRegistration save in queued)
                if (_replicaEntries.Remove(save, out ReplicaEntry entry))
                    _replicaQueue.Remove(entry);
        List<SaveRegistration>? added = null;
        foreach (SaveRegistration save in InMemorySavesOf(partition))
        {
            if (RankOf(save) is not ReplicaRank key) continue;
            var entry = new ReplicaEntry(key, save);
            _replicaQueue.Add(entry);
            _replicaEntries[save] = entry;
            (added ??= []).Add(save);
        }
        if (added != null) _replicaQueuedByPartition[partition] = added;
    }

    /// <summary>The save's rank, or null when it needs no copy (deleted, or two usable holders already).</summary>
    private ReplicaRank? RankOf(SaveRegistration save)
    {
        if (save.PendingDelete || Satisfied(save, countGone: false))
            return null;
        bool atRisk = false;
        int index = 0;
        foreach (string holder in save.LocationHolders.Values)
            if (!RepeatedHolder(save.LocationHolders, holder, index++) && !save.CorruptHolders.Contains(holder)
                && _replicaHolderMap.TryGetValue(holder, out HolderState state) && state.Usable && state.Closed)
                atRisk = true;
        bool newest = true, covered = false;
        foreach (SaveRegistration other in InMemorySavesOf(save.SaveId.Partition))
        {
            if (other.PendingDelete || ReferenceEquals(other, save)) continue;
            if (other.SaveId.CompareTo(save.SaveId) > 0) newest = false;
            else if (!covered && Satisfied(other, countGone: false)) covered = true;
        }
        return new ReplicaRank(atRisk, newest, !IsRetired(save.SaveId.Partition), !covered, save.Created, Satisfied(save, countGone: true));
    }

    /// <summary>T-0323: the save would have its copies if gone holders still counted - a copy only moves it off them.</summary>
    private bool IsEvacuation(SaveRegistration save) => !Satisfied(save, countGone: false) && Satisfied(save, countGone: true);

    // Two usable holders - on two hosts when more than one host serves (T-0321). A gone holder counts only with countGone
    // (T-0323): its files are served by a proxy, but its process is gone and the copy on its disk is what we move off.
    private bool Satisfied(SaveRegistration candidate, bool countGone)
        {
            if (_hostStore && HeldByHost(candidate)) return HostSatisfied(candidate);
            int count = 0, position = 0;
            string? firstHost = null;
            bool spread = !_replicaMultiHost;
            foreach (string holder in candidate.LocationHolders.Values)
                if (!RepeatedHolder(candidate.LocationHolders, holder, position++) && !candidate.CorruptHolders.Contains(holder)
                    && _replicaHolderMap.TryGetValue(holder, out HolderState state) && state.Usable && (countGone || !state.Gone))
                {
                    string key = state.Host ?? "#" + holder;
                    if (++count == 1) firstHost = key;
                    else if (!string.Equals(key, firstHost, StringComparison.OrdinalIgnoreCase)) spread = true;
                    if (count >= 2 && spread) return true;
                }
            return false;
        }

    private static bool MultiHost(HolderState[] holders)
    {
        string? first = null;
        foreach (HolderState holder in holders)
        {
            if (!holder.Takes || holder.Host == null) continue;
            if (first == null) first = holder.Host;
            else if (!string.Equals(first, holder.Host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="node"/> (on <paramref name="host"/>) should copy <paramref name="save"/> now: it does not
    /// hold it, and the save still lacks two usable holders (counting a copy another node has reserved) - or, while more
    /// than one host serves, lacks a holder on a second host. A node on the only holders' host is not handed such a save:
    /// its copy would add a holder but no host spread, so the copy is left to a node on another host (T-0321). The queue
    /// and the brute-force reference both decide through here.
    /// </summary>
    private bool EligibleReplica(SaveRegistration save, string node, string? host, DateTimeOffset now)
    {
        // SE-1347: not to a holder whose trimmed copy of this save is still to be deleted (CopyTrim.cs).
        if (TrimPendingFor(save, node)) return false;
        if (_hostStore && HeldByHost(save)) return HostEligibleReplica(save, node, now);
        if (save.LocationHolders.ContainsValue(node)) return false;
        // T-0323: only a save something can serve - a live holder, or a gone one with a proxy on its host. Without this
        // a save whose gone holders have no live node on their host would stay first in line and starve the queue.
        if (ReplicaSources(save).Length == 0) return false;
        int usable = 0, index = 0;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string holder in save.LocationHolders.Values)
            if (!RepeatedHolder(save.LocationHolders, holder, index++) && !save.CorruptHolders.Contains(holder) && HolderUsable(holder)
                && GoneHolder(holder) == null && ReleasedHolderHost(holder) == null)
            {
                usable++;
                hosts.Add(_state.Nodes.GetValueOrDefault(holder)?.Host ?? "#" + holder);
            }
        bool reserved = _replicaReservations.TryGetValue(save.SaveId, out (string Node, DateTimeOffset Until) reservation)
            && reservation.Node != node && reservation.Until > now;
        if (!_replicaMultiHost) return usable + (reserved ? 1 : 0) < 2;
        // Multi-host: a reservation was handed to a node that adds host spread (below), so it counts as both.
        if (reserved) return false;
        if (usable >= 2 && hosts.Count >= 2) return false;
        return usable == 0 || host == null || !hosts.Contains(host);
    }

    /// <summary>
    /// T-0321 diag: while more than one host serves, the live partitions whose newest save has usable holders on one host
    /// only - content that dies with that host. The acceptance gate for the host-cut test is 0.
    /// </summary>
    internal object ReplicaHostSpread()
    {
        lock (_lock)
        {
            // Polled at 1 Hz by cluster-status through /diag; the walk visits every live partition, so it is cached briefly.
            DateTimeOffset at = _clock();
            if (_replicaHostSpread is { } cached && at - cached.At < TimeSpan.FromSeconds(10) && at >= cached.At) return cached.Value;
            HolderState[] holders = CurrentHolderStates();
            bool multiHost = MultiHost(holders);
            Dictionary<string, HolderState> map = holders.ToDictionary(x => x.Node, StringComparer.Ordinal);
            int live = 0, singleHost = 0, singleCopy = 0;
            var examples = new List<object>();
            foreach (PartitionOwnership row in _state.Partitions.Values)
            {
                if (IsRetired(row.Partition)) continue;
                SaveRegistration? newest = null;
                foreach (SaveRegistration save in InMemorySavesOf(row.Partition))
                    if (!save.PendingDelete && (newest == null || save.SaveId.CompareTo(newest.SaveId) > 0)) newest = save;
                if (newest == null) continue;
                live++;
                string[] usable = newest.LocationHolders.Values.Distinct(StringComparer.Ordinal)
                    .Where(holder => !newest.CorruptHolders.Contains(holder) && map.TryGetValue(holder, out HolderState state) && state.Usable).ToArray();
                if (usable.Length < 2) singleCopy++;
                string[] hosts = usable.Select(holder => map[holder].Host ?? "#" + holder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (multiHost && usable.Length != 0 && hosts.Length == 1)
                {
                    singleHost++;
                    if (examples.Count < 10) examples.Add(new { partition = row.Partition, save = Key(newest.SaveId), host = hosts[0], holders = usable });
                }
            }
            object value = new
            {
                multiHost,
                hosts = holders.Where(x => x.Takes && x.Host != null).Select(x => x.Host!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
                livePartitionsWithSaves = live,
                singleHostNewestSaves = singleHost,
                singleCopyNewestSaves = singleCopy,
                singleHostExamples = examples,
            };
            _replicaHostSpread = (at, value);
            return value;
        }
    }

    private (DateTimeOffset At, object Value)? _replicaHostSpread;

    /// <summary>Compares the incremental queue with a full ranking; drift is logged and the full ranking adopted.</summary>
    private void AuditReplicaQueue(HolderState[] holders, DateTimeOffset now)
    {
        (SortedSet<ReplicaEntry> fresh, _, _) = RankAll();
        _replicaAuditedAt = now;
        if (SameQueue(fresh, _replicaQueue)) return;
        ReplicaAuditRepairs++;
        Console.WriteLine($"WARNING: replica queue audit: incremental queue ({_replicaQueue.Count} saves) differs from a full ranking "
            + $"({fresh.Count}); a save change did not mark its partition. Rebuilding.");
        RebuildReplicaQueue(holders, now);
    }

    private static bool SameQueue(SortedSet<ReplicaEntry> a, SortedSet<ReplicaEntry> b)
    {
        if (a.Count != b.Count) return false;
        using SortedSet<ReplicaEntry>.Enumerator x = a.GetEnumerator(), y = b.GetEnumerator();
        while (x.MoveNext() && y.MoveNext())
            if (!ReferenceEquals(x.Current.Save, y.Current.Save) || x.Current.Key != y.Current.Key) return false;
        return true;
    }

    /// <summary>Self-test: after a registry write, the queue (with its dirty partitions applied) must equal a full ranking.</summary>
    private void VerifyReplicaQueue()
    {
        if (!_replicaQueueReady) return;
        HolderState[] holders = CurrentHolderStates();
        if (!holders.AsSpan().SequenceEqual(_replicaHolders) || _hostStore && HostCopyTarget() != _replicaCopyTarget) return;   // the next poll rebuilds anyway
        foreach (ulong partition in _replicaDirty)
            RerankPartition(partition);
        _replicaDirty.Clear();
        (SortedSet<ReplicaEntry> fresh, _, _) = RankAll();
        if (!SameQueue(fresh, _replicaQueue))
            ReplicaDivergence ??= new InvalidOperationException(
                $"replica queue ({_replicaQueue.Count} saves) differs from a full ranking ({fresh.Count}) after a registry write");
    }

    /// <summary>The original single pass over the whole catalogue: the reference the self-test checks answers against.</summary>
    private SaveRegistration? BruteForceNextReplica(string node, DateTimeOffset now)
    {
        ReplicaEntry? best = null;
        foreach (SaveRegistration save in _state.Saves.Values)
        {
            if (RankOf(save) is not ReplicaRank key || !EligibleReplica(save, node, _state.Nodes.GetValueOrDefault(node)?.Host, now))
                continue;
            var entry = new ReplicaEntry(key, save);
            if (best == null || ReplicaOrder.Instance.Compare(entry, best.Value) < 0)
                best = entry;
        }
        return best?.Save;
    }
}
