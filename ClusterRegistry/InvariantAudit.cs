using System.Globalization;

namespace ClusterRegistry;

/// <summary>One live violation of a runtime invariant (Docs/ConcurrencyReview.md section 8).</summary>
internal sealed record InvariantViolation(string Kind, ulong Partition, string Detail, DateTimeOffset Since, DateTimeOffset ReportedAt);

/// <summary>The audit's view for /diag: violations still standing, and how many episodes were ever reported.</summary>
internal sealed record InvariantAuditView(long Total, InvariantViolation[] Active);

/// <summary>
/// T-0296 runtime invariant audit, the safety net behind the code review (Docs/ConcurrencyReview.md section 8). Once a
/// second, inside ExpireLeases and under the registry lock, it checks two invariants and says so the moment one has held
/// broken for longer than any legitimate transition takes:
///
/// * duplicate: two live node incarnations both report the SAME partition with members in their latest heartbeats. A
///   handover overlaps source and target for about a second (commit, then the source's release), and F1/T-0297's stale
///   recovery copy for at most one heartbeat; past <see cref="DuplicateGrace"/> it is a copy no protocol step will end -
///   the content is simulated twice and the two copies diverge (T-0249/T-0291/T-0307 class). The report is
///   generation-blind on purpose: a node holding a copy under an OLD generation is exactly the stale copy to catch.
/// * abandoned: an unowned row (not recovering, not retired, not offline) whose newest saves are reachable stays unclaimed
///   for <see cref="AbandonedGrace"/> while at least one serving regular node is up: every claimant skipped it (the T-0295
///   "someone else has it" shape), so its content is out of the world with nobody bound to bring it back. The stranded
///   case (no reachable holder) is the stranded sweep's (ReapUnrecoverable) and is not repeated here.
///
/// Each episode is reported ONCE (event invariant.violation, a console line starting "INVARIANT VIOLATION"), stays listed
/// in /diag registry.invariants.active while it holds, and ends when the condition clears. Memory only: a registry
/// restart re-learns the reports from the next heartbeats and starts every grace afresh.
/// </summary>
internal sealed partial class GatewayRegistry
{
    internal static TimeSpan DuplicateGrace = Seconds("CLUSTER_AUDIT_DUPLICATE_SECONDS", 30);
    /// <summary>Self-test switch for the earned red of invariant-audit: off, a liveness heartbeat's empty list is an ownership report again.</summary>
    internal static bool LivenessIsNoReport = true;
    internal static TimeSpan AbandonedGrace = Seconds("CLUSTER_AUDIT_ABANDONED_SECONDS", 600);

    private static TimeSpan Seconds(string variable, double fallback) =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double seconds) && seconds > 0 ? seconds : fallback);

    /// <summary>Latest heartbeat per node: the incarnation and the partitions it reported WITH members.</summary>
    private readonly Dictionary<string, (long Epoch, ulong[] Partitions)> _reportedWithMembers = [];
    private readonly Dictionary<ulong, DateTimeOffset> _duplicateSince = [];
    private readonly Dictionary<ulong, DateTimeOffset> _abandonedSince = [];
    private readonly Dictionary<(string Kind, ulong Partition), InvariantViolation> _violations = [];
    private long _violationsTotal;

    /// <summary>Heartbeat, under _lock: remembers what this incarnation simulates (members &gt; 0).</summary>
    private void RecordReportedOwnerships(string node, long epoch, PartitionVersion[] ownerships)
    {
        ulong[] partitions = [.. ownerships.Where(x => x.Members > 0).Select(x => x.Partition).Distinct()];
        if (partitions.Length == 0) _reportedWithMembers.Remove(node);
        else _reportedWithMembers[node] = (epoch, partitions);
    }

    /// <summary>The live incarnations whose latest report has the partition with members (self-test and diag).</summary>
    internal string[] ReportedHolders(ulong partition)
    {
        lock (_lock) return ReportedHoldersLocked(partition, _clock());
    }

    private string[] ReportedHoldersLocked(ulong partition, DateTimeOffset now) => [.. _reportedWithMembers
        .Where(x => x.Value.Partitions.Contains(partition) && LiveIncarnation(x.Key, x.Value.Epoch, now))
        .Select(x => x.Key).Order(StringComparer.Ordinal)];

    private bool LiveIncarnation(string node, long epoch, DateTimeOffset now) =>
        _state.Nodes.TryGetValue(node, out NodeRegistration? row) && row.Epoch == epoch
            && row.State is NodeState.Active or NodeState.Closed && row.LeaseExpires > now;

    public InvariantAuditView InvariantAudit()
    {
        lock (_lock) return new InvariantAuditView(_violationsTotal, [.. _violations.Values.OrderBy(x => x.Since)]);
    }

    /// <summary>ExpireLeases, under _lock, after the expiry and the sweeps.</summary>
    private void AuditInvariants(DateTimeOffset now)
    {
        // Reports of incarnations that are gone say nothing any more.
        foreach (string node in _reportedWithMembers.Where(x => !LiveIncarnation(x.Key, x.Value.Epoch, now)).Select(x => x.Key).ToArray())
            _reportedWithMembers.Remove(node);

        HashSet<ulong> duplicated = [.. _reportedWithMembers.Values.SelectMany(x => x.Partitions)
            .GroupBy(x => x).Where(group => group.Count() > 1).Select(group => group.Key)];
        Track("duplicate", duplicated, _duplicateSince, DuplicateGrace, now, partition =>
        {
            PartitionOwnership? owner = _state.Partitions.GetValueOrDefault(partition);
            return $"reported with members by {string.Join(", ", ReportedHoldersLocked(partition, now))}; registry owner "
                + (owner?.Node == null ? "none" : $"{owner.Node} epoch={owner.Epoch} generation={owner.Generation}");
        });

        HashSet<ulong> abandoned = [];
        bool serving = _state.Phase is not (ClusterPhase.Draining or ClusterPhase.Down) && _state.Nodes.Values.Any(x =>
            x.Role == NodeRole.Regular && x.State == NodeState.Active && x.Serving && !x.Spare && x.LeaseExpires > now);
        if (serving)
            foreach (PartitionOwnership row in _state.Partitions.Values)
                if (row.Node == null && !row.Recovering && !IsRetired(row.Partition) && !_state.OfflinePartitions.ContainsKey(row.Partition)
                    && SavesOf(row.Partition).Any(Reachable) && !NewerSaveHeld(row.Partition, now)
                    && !HeldForLateSave(row.Partition, now))
                    abandoned.Add(row.Partition);
        Track("abandoned", abandoned, _abandonedSince, AbandonedGrace, now, partition =>
            $"unowned, not recovering and not offline, with {SavesOf(partition).Count(Reachable)} reachable save(s), while serving nodes are up");
    }

    private void Track(string kind, HashSet<ulong> current, Dictionary<ulong, DateTimeOffset> since, TimeSpan grace,
        DateTimeOffset now, Func<ulong, string> detail)
    {
        foreach (ulong partition in since.Keys.Where(x => !current.Contains(x)).ToArray())
        {
            since.Remove(partition);
            if (_violations.Remove((kind, partition)))
                Console.WriteLine($"invariant restored: {kind} partition {partition}");
        }
        foreach (ulong partition in current)
        {
            if (!since.TryGetValue(partition, out DateTimeOffset first)) { since[partition] = now; continue; }
            if (now - first < grace || _violations.ContainsKey((kind, partition))) continue;
            string text = detail(partition);
            _violations[(kind, partition)] = new InvariantViolation(kind, partition, text, first, now);
            _violationsTotal++;
            Emit("invariant.violation", partition.ToString(CultureInfo.InvariantCulture), new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["seconds"] = ((long)(now - first).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                ["detail"] = text,
            });
            Console.WriteLine($"INVARIANT VIOLATION {kind}: partition {partition} for {(now - first).TotalSeconds:F0}s: {text}");
        }
    }
}
