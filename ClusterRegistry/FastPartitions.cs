using System.Threading;

namespace ClusterRegistry;

/// <summary>
/// T-0292: what the registry did NOT do because a partition was fast (PartitionInfo.Fast: a member
/// faster than fast.enterMps, measured by the owning node from positions over time). A fast partition
/// neither merges nor triggers loads until it slows down; these counters show each suppression was
/// real work avoided, and appear in /diag?registry=summary.
/// </summary>
internal static class FastPartitionCounters
{
    private static long s_colocationLinks, s_migrations, s_wakes, s_offlineRefusals, s_pulls, s_passRequests;

    public static void ColocationLink() => Interlocked.Increment(ref s_colocationLinks);
    public static void Migration() => Interlocked.Increment(ref s_migrations);
    public static void Wake() => Interlocked.Increment(ref s_wakes);
    public static void OfflineRefusal() => Interlocked.Increment(ref s_offlineRefusals);
    public static void Pull() => Interlocked.Increment(ref s_pulls);
    public static void PassRequest() => Interlocked.Increment(ref s_passRequests);

    public static object Snapshot() => new
    {
        // Counted once per pair per co-location pass (1 Hz per node pair): link-seconds, not events.
        colocationLinkPassesSuppressed = Interlocked.Read(ref s_colocationLinks),
        // Counted once per fast component per balance pass: how often migration had to skip it.
        balancePassesExcludingFast = Interlocked.Read(ref s_migrations),
        // Per wake pass per offline row the fast partition was close enough to have woken.
        wakePassesSuppressed = Interlocked.Read(ref s_wakes),
        // Offline admissions a passing fast partition would otherwise have refused.
        offlineRefusalsIgnored = Interlocked.Read(ref s_offlineRefusals),
        refusedPulls = Interlocked.Read(ref s_pulls),
        // SE1-0084: POST /registry/report-now, sent by a node after a fast transition, at most one per 250 ms per node.
        passRequests = Interlocked.Read(ref s_passRequests),
    };
}
