using System.Globalization;

namespace ClusterRegistry;

/// <summary>
/// T-0323: the registry paces save copies, the nodes do not. A node takes directives for as long as
/// <see cref="GatewayRegistry.ReplicaWork"/> hands one out (PartitionPersistence.ReplicationLoop); what bounds the
/// cluster is one byte budget here, and what tells an operator that the bound (or the nodes) cannot keep up is the
/// alarm below.
///
/// Budgets. Every partition save copy spends the global budget (<see cref="ReplicaBytesPerSecond"/>); a copy that only
/// moves a save off gone holders spends the evacuation budget (<see cref="EvacuateBytesPerSecond"/>) as well, so
/// evacuation never takes more than its own share of the global one. Nothing is reserved per class: the queue's order
/// (<see cref="Better"/>) decides who gets the next byte, so an at-risk or newest save waits for one refill at most
/// (a 21 KB save refills in 5 ms) and evacuation stays last. Global saves spend their own budget
/// (<see cref="GlobalBytesPerSecond"/>, T-0397).
///
/// Alarm. Every recycled node returns the saves it held to the queue (~11k per stop at 34k catalogued saves on r85).
/// If the copies do not clear that before the next stop the queue only grows and no stopped node's row is ever
/// released, which is how T-0323 was found: by reading the queue depth by hand after 2 h 20 min.
/// </summary>
internal sealed partial class GatewayRegistry
{
    /// <summary>A token bucket in bytes: full at first use, bursts up to <see cref="BurstSeconds"/> of its rate. The
    /// rate is passed in because the self-tests change it. A take may overdraw by one save, so a save larger than the
    /// burst still goes out (and the next waits for the debt).</summary>
    private struct ByteBudget
    {
        private const double BurstSeconds = 10;
        private double _tokens;
        private DateTimeOffset _refilled;
        private bool _started;

        public bool Available(int bytesPerSecond, DateTimeOffset now)
        {
            double capacity = bytesPerSecond * BurstSeconds;
            if (!_started) { _tokens = capacity; _started = true; }
            else if (now > _refilled) _tokens = Math.Min(capacity, _tokens + (now - _refilled).TotalSeconds * bytesPerSecond);
            _refilled = now;
            return _tokens > 0;
        }

        public void Spend(long bytes) => _tokens -= Math.Max(1, bytes);
    }

    /// <summary>Byte budget per second for ALL partition save copies in the cluster (CLUSTER_REPLICA_BYTES_PER_SECOND).
    /// Default 1 MiB/s = ~8.4 Mb/s = ~50 average 21 KB saves a second: the ~11k saves (~230 MB) one stopped node
    /// returns to the queue clear in about 4 min of a 25-30 min recycle interval, and the whole 34k catalogue in
    /// twelve. Bursts up to 10 s of budget (10 MiB). Not higher by default: every copy is a disk write on its target,
    /// and an fsynced small file costs ~3.8x its size on btrfs (T-0345).</summary>
    internal static int ReplicaBytesPerSecond = EnvInt("CLUSTER_REPLICA_BYTES_PER_SECOND", 1 << 20);
    /// <summary>Of that, the byte budget per second for copies that only move a save off gone holders
    /// (CLUSTER_REPLICA_EVACUATE_BYTES_PER_SECOND, default 512 KiB/s = ~4.2 Mb/s, about 25 average 21 KB saves a second:
    /// the same ~11k saves in under 8 min; bursts up to 10 s of budget).</summary>
    internal static int EvacuateBytesPerSecond = EnvInt("CLUSTER_REPLICA_EVACUATE_BYTES_PER_SECOND", 512 << 10);
    /// <summary>T-0397: byte budget per second for ALL global save copies in the cluster
    /// (CLUSTER_REPLICA_GLOBAL_BYTES_PER_SECOND, default 1 MiB/s = about four 250 KB global saves a second, bursts up to
    /// 10 s). Separate from the partition budget so a retired node's ~1,000 under-held globals (~290 MB, ~5 min) do not
    /// delay the partition queue, and paced at all because unpaced they were copied as fast as the nodes could fsync.</summary>
    internal static int GlobalBytesPerSecond = EnvInt("CLUSTER_REPLICA_GLOBAL_BYTES_PER_SECOND", 1 << 20);
    /// <summary>T-0397 (c), the fallback to store mode: how many of the NEWEST global saves must keep two usable node
    /// holders (CLUSTER_REPLICA_GLOBAL_NEWEST; 0 = all of them, today). Global saves are never retired and a node holder
    /// counts only while its node lives, so with "all" the whole global history (~2,500 on w33) goes round the node
    /// rotation: every recycle re-copies what the retiring node held (~1,000-2,800 copies). With N > 0 only the newest N,
    /// the ones a snapshot names and the last clean shutdown's must; the others keep whatever holders they have and their
    /// files stay where they are (nothing is deleted).</summary>
    internal static int GlobalReplicaNewest = EnvInt("CLUSTER_REPLICA_GLOBAL_NEWEST", 0);
    private ByteBudget _replicaBudget, _evacuateBudget, _globalBudget;

    /// <summary>The alarm samples the queue once a minute (on a node's replica poll).</summary>
    private static readonly TimeSpan ReplicaSampleInterval = TimeSpan.FromMinutes(1);
    /// <summary>"The queue has not shrunk" looks this far back; also the recycle interval assumed while the node
    /// history has fewer than two recycles.</summary>
    private static readonly TimeSpan ReplicaAlarmWindow = TimeSpan.FromMinutes(30);
    /// <summary>The copy rate is measured over this many samples, and a verdict must hold for as many in a row before the
    /// alarm changes: a stop puts its saves in the queue at once, so the first samples after it still carry the idle
    /// rate from before. By the fifth the rate is all backlog, and a backlog that cleared meanwhile never alarmed.</summary>
    private const int ReplicaAlarmSamples = 5;
    /// <summary>The recycle interval is the mean gap between this many of the newest recycles in the node history. Enough to
    /// span several rotation waves: recycles cluster (five in 11 min on r85, then none for 1 h 45 min), and over the newest
    /// six the interval read 4 min and raised the alarm on a queue that was draining (r113).</summary>
    private const int ReplicaAlarmRecycles = 24;

    private readonly record struct ReplicaSample(DateTimeOffset At, int Depth, long Copies, bool Backlogged);
    internal sealed record ReplicaAlarmView(bool Active, int Depth, double CopiesPerMinute, double? EtaMinutes, double IntervalMinutes,
        string? Reason, DateTimeOffset? Since);

    private readonly List<ReplicaSample> _replicaSamples = [];
    /// <summary>Partition save copies registered (<see cref="RegisterHolder"/>) since the registry started.</summary>
    private long _replicaCopies;
    private int _replicaAlarmStreak;
    private ReplicaAlarmView _replicaAlarm = new(false, 0, 0, null, ReplicaAlarmWindow.TotalMinutes, null, null);
    /// <summary>Self-test: how often the alarm was raised and cleared.</summary>
    internal int ReplicaAlarmsRaised { get; private set; }
    internal int ReplicaAlarmsCleared { get; private set; }

    /// <summary>The alarm as of its last sample (diag, self-test).</summary>
    internal ReplicaAlarmView ReplicaAlarm() { lock (_lock) return _replicaAlarm; }

    /// <summary>The mean gap between the newest recycles in the node history: how long the copies have to clear what one
    /// stop returned to the queue. Other outcomes do not count (a restart stops every node in the same second).</summary>
    private TimeSpan RecycleInterval()
    {
        DateTimeOffset? newest = null, oldest = null;
        int stops = 0;
        for (int index = _state.NodeHistory.Count - 1; index >= 0 && stops < ReplicaAlarmRecycles; index--)
        {
            if (_state.NodeHistory[index].Outcome != "recycle") continue;
            oldest = _state.NodeHistory[index].EndedAt;
            newest ??= oldest;
            stops++;
        }
        return stops < 2 || newest <= oldest ? ReplicaAlarmWindow : (newest!.Value - oldest!.Value) / (stops - 1);
    }

    /// <summary>
    /// Samples the queue and the copy counter when a minute has passed and settles the alarm. Caller holds _lock.
    /// Raised when, for <see cref="ReplicaAlarmSamples"/> samples in a row, the queue would take longer than a recycle
    /// interval to clear at the measured rate, or it held a backlog (more than a minute of copies) at every sample of the
    /// last 30 min and is no smaller than it was then. Cleared the same way. A cluster with fewer than two nodes that
    /// take copies has nowhere to put one: every save queues, and that is not this alarm.
    /// </summary>
    private void SampleReplicaAlarm(DateTimeOffset now)
    {
        if (_replicaSamples.Count != 0)
        {
            if (now < _replicaSamples[^1].At) _replicaSamples.Clear();
            else if (now - _replicaSamples[^1].At < ReplicaSampleInterval) return;
        }
        RefreshReplicaQueue(now);
        int depth = _replicaQueue.Count;
        // The rate needs its full span: no verdict until there is a sample ReplicaAlarmSamples minutes back.
        ReplicaSample? from = _replicaSamples.Count >= ReplicaAlarmSamples ? _replicaSamples[^ReplicaAlarmSamples] : null;
        double rate = from is { } start ? (_replicaCopies - start.Copies) / (now - start.At).TotalMinutes : 0;
        double eta = depth == 0 ? 0 : rate > 0 ? depth / rate : double.PositiveInfinity;
        _replicaSamples.Add(new ReplicaSample(now, depth, _replicaCopies, eta > ReplicaSampleInterval.TotalMinutes));
        while (_replicaSamples.Count > 1 && now - _replicaSamples[1].At >= ReplicaAlarmWindow) _replicaSamples.RemoveAt(0);
        TimeSpan interval = RecycleInterval();
        string? reason = null;
        if (from != null && _replicaHolders.Count(x => x.Takes) >= 2)
        {
            if (eta > interval.TotalMinutes) reason = "the queue outlasts a recycle interval";
            else if (now - _replicaSamples[0].At >= ReplicaAlarmWindow && depth >= _replicaSamples[0].Depth && _replicaSamples.All(x => x.Backlogged))
                reason = $"the queue has not shrunk in {ReplicaAlarmWindow.TotalMinutes:F0} min";
        }
        bool active = _replicaAlarm.Active;
        if (reason != null == active) _replicaAlarmStreak = 0;
        else if (++_replicaAlarmStreak >= ReplicaAlarmSamples)
        {
            _replicaAlarmStreak = 0;
            active = !active;
            string numbers = string.Create(CultureInfo.InvariantCulture, $"{depth} save(s) queued, {rate:F0} copies/min, ")
                + (double.IsPositiveInfinity(eta) ? "no end in sight" : string.Create(CultureInfo.InvariantCulture, $"{eta:F0} min to clear"))
                + string.Create(CultureInfo.InvariantCulture, $", recycle interval {interval.TotalMinutes:F0} min");
            if (active)
            {
                ReplicaAlarmsRaised++;
                Console.WriteLine($"WARNING: replica capacity below invalidation rate: {reason}: {numbers}. A stopped node's row is not released "
                    + "while its saves wait; raise CLUSTER_REPLICA_BYTES_PER_SECOND or recycle less often (T-0323).");
            }
            else
            {
                ReplicaAlarmsCleared++;
                Console.WriteLine($"registry: replica capacity alarm cleared after {(now - _replicaAlarm.Since!.Value).TotalMinutes:F0} min: {numbers}");
            }
        }
        _replicaAlarm = new ReplicaAlarmView(active, depth, rate, double.IsPositiveInfinity(eta) ? null : eta, interval.TotalMinutes,
            !active ? null : reason ?? _replicaAlarm.Reason, !active ? null : _replicaAlarm.Since ?? now);
    }
}
