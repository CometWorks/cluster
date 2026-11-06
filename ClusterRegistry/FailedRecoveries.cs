using System.Globalization;

namespace ClusterRegistry;

/// <summary>
/// SE-1161: a recovery whose load fails the same way every time. The node releases the claim after the failure, the row is
/// unowned again, and the next claim loop (any node's, every second) took it right back: octahedron-test node-1 claimed
/// partition 3432 71 times in 3 minutes (generation 2 -> 72, 2026-11-06), and only a node stop ended it. Two things now
/// bound that loop:
///
/// The backoff. A release the node marks as a failed recovery (<c>failed=true</c>) holds the partition back from every
/// claim for recovery.failedBackoffSeconds, doubled per failure in a row up to recovery.failedBackoffMaxSeconds. The
/// first save a node registers for the partition (its load worked) ends the run. Memory only: a registry restart starts
/// the count again, which costs a few claims at most.
///
/// The quarantine. A node quarantines a partition after recovery.quarantineAfter failed loads (cold or warm recovery
/// alike) and reports it on its heartbeat; while a live node reports it, nobody is handed the partition. It shows on
/// /registry/quarantine and in the partition list, and the node's quarantine repair brings it back.
/// </summary>
internal sealed partial class GatewayRegistry
{
    internal const string FailedBackoffKey = "recovery.failedBackoffSeconds";
    internal const string FailedBackoffMaxKey = "recovery.failedBackoffMaxSeconds";

    /// <summary>Partition -> failed recovery loads in a row, and when it may be handed out again. Caller holds _lock.</summary>
    private readonly Dictionary<ulong, (int Failures, DateTimeOffset RetryAt)> _failedRecoveries =
    [];

    private int ConfigSeconds(string key, int fallback) =>
        int.TryParse(
            _state.HandoverConfig.GetValueOrDefault(key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int seconds
        )
        && seconds >= 0
            ? seconds
            : fallback;

    /// <summary>A released claim whose load failed on its node. Caller holds _lock.</summary>
    private void NoteFailedRecovery(ulong partition, string node)
    {
        int failures = _failedRecoveries.GetValueOrDefault(partition).Failures + 1;
        int baseSeconds = ConfigSeconds(FailedBackoffKey, 5),
            maxSeconds = ConfigSeconds(FailedBackoffMaxKey, 300);
        double backoff = Math.Min(
            maxSeconds,
            baseSeconds * Math.Pow(2, Math.Min(failures - 1, 30))
        );
        _failedRecoveries[partition] = (failures, _clock() + TimeSpan.FromSeconds(backoff));
        Console.WriteLine(
            $"recovery of partition {partition} failed on {node} ({failures} in a row); not handed out again for {backoff:F0} s (SE-1161)"
        );
    }

    /// <summary>Whether no node may be handed the partition for recovery now: it is in its failure backoff, or a live node
    /// has quarantined it. Caller holds _lock.</summary>
    private bool RecoveryHeldBack(ulong partition, DateTimeOffset now) =>
        _failedRecoveries.TryGetValue(partition, out var failed) && now < failed.RetryAt
        || _state.Nodes.Values.Any(node =>
            node.State is NodeState.Active or NodeState.Closed
            && node.Quarantined.Contains(partition)
        );

    /// <summary>Test seam: failed recovery loads in a row of the partition (0 = none).</summary>
    internal int FailedRecoveriesForTest(ulong partition)
    {
        lock (_lock)
            return _failedRecoveries.GetValueOrDefault(partition).Failures;
    }
}
