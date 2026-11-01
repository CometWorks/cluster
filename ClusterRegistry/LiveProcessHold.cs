namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    /// <summary>
    /// #1218: how long a node may stay silent (since its last lease renewal) while its host's executor still reports its
    /// process alive. CLUSTER_LIVE_PROCESS_GRACE_SECONDS, default 60 s (the simulation-stall kill), 0 turns the hold off.
    /// </summary>
    internal static readonly TimeSpan DefaultLiveProcessGrace =
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CLUSTER_LIVE_PROCESS_GRACE_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0 ? seconds : 60);

    internal TimeSpan LiveProcessGrace { get; set; } = DefaultLiveProcessGrace;

    /// <summary>
    /// An executor observation older than this proves nothing about the process. Host agents poll every 1-15 s
    /// (Quasar.Host PollIntervalSeconds); a host that stops reporting must not keep a node alive.
    /// </summary>
    private static readonly TimeSpan LiveProcessObservationFreshness = TimeSpan.FromSeconds(20);

    /// <summary>The lapse each node incarnation was last logged as held at: one line per freeze, not per pass.</summary>
    private readonly Dictionary<(string Node, long Epoch), DateTimeOffset> _liveProcessHoldLogged = [];

    /// <summary>
    /// #1218: a lapsed lease is held while the node's own host says its process is still running. The #1191 octahedron
    /// test (2026-10-20 18:47Z, polaris saturated by another user's ~970 % CPU job) froze whole node processes - every
    /// thread silent, one core busy, the heap shrinking afterwards: a blocking full GC of a 6-9 GB heap - for 35 s
    /// (local-node-3), 20 s (local-node-1) and 51 s (local-node-0) against a 15 s lease. Each was declared Dead and
    /// fenced the moment it woke, its players dropped and its partitions moved to a spare on the same saturated host.
    /// The executor's Ready report comes from another process (the host agent checks the recorded pid and start
    /// identity every poll), so it tells a frozen node from a dead one: a crash turns the report into Gone or Failed at
    /// the next poll, and the hold ends there. The hold is bounded by <see cref="LiveProcessGrace"/> since the last
    /// renewal, and it never covers a live lease, so the simulation-stall kill still fires on the first heartbeat of a
    /// node whose game thread stopped. #1282: a Closed node is admitted on purpose, but only on a Ready report: the
    /// executor API records a Ready report of a node that is no longer Active as Spawning (running, never admission
    /// proof), so in practice a Closed node is held only while the last Ready taken before its close is still fresh
    /// (<see cref="LiveProcessObservationFreshness"/>). Holding a draining node on a Spawning report as well would need
    /// the observed epoch, which the slot does not keep. Caller holds _lock.
    /// </summary>
    private bool LiveProcessHeld(NodeRegistration node, DateTimeOffset now)
    {
        if (LiveProcessGrace <= TimeSpan.Zero || node.LeaseExpires > now || node.State is not (NodeState.Active or NodeState.Closed)
            || now >= node.LeaseExpires - _leaseTtl + LiveProcessGrace)
            return false;
        bool alive = _state.NodePlan.Values.Any(slot => slot.ObservedNode == node.Node && slot.Observed == SpawnState.Ready
            && slot.ObservedAt is { } observedAt && now - observedAt <= LiveProcessObservationFreshness);
        if (alive && _liveProcessHoldLogged.GetValueOrDefault((node.Node, node.Epoch)) != node.LeaseExpires)
        {
            _liveProcessHoldLogged[(node.Node, node.Epoch)] = node.LeaseExpires;
            Console.WriteLine($"lease of {node.Node} epoch={node.Epoch} lapsed at {node.LeaseExpires:O} but its host reports the process "
                + $"running: held up to {(node.LeaseExpires - _leaseTtl + LiveProcessGrace):O} (#1218)");
        }
        return alive;
    }

    /// <summary>When a frozen silence of each node incarnation was last forgiven (<see cref="ForgiveFrozenSilence"/>).</summary>
    private readonly Dictionary<(string Node, long Epoch), DateTimeOffset> _frozenSilenceForgiven = [];

    /// <summary>
    /// #1218: the first frame report after a whole-process freeze can carry the frame of the last one before it (no frame
    /// finished in the ~0.5 s between that report and the freeze). Read as it was, the freeze counted as a game-thread
    /// stall: a 51 s freeze closed the node and set its forced drain for 9 s after it woke, which is a fence by another
    /// name. A node that reports while its lease has lapsed (it got here held, <see cref="LiveProcessHeld"/>) was silent
    /// on every heartbeat thread since its last renewal, and that silence says nothing about the game thread, so the
    /// stall clock skips it - once per <see cref="SimulationStallKill"/>, so a process that keeps freezing still accrues
    /// stall time and is drained and killed as before. Off with the hold (grace 0). Caller holds _lock, before the frame
    /// comparison and before the lease is renewed.
    /// </summary>
    private void ForgiveFrozenSilence(NodeRegistration node, DateTimeOffset now)
    {
        if (LiveProcessGrace <= TimeSpan.Zero || node.LeaseExpires > now || node.SimulationProgress == default
            || now - _frozenSilenceForgiven.GetValueOrDefault((node.Node, node.Epoch), DateTimeOffset.MinValue) < SimulationStallKill)
            return;
        TimeSpan silence = now - (node.LeaseExpires - _leaseTtl);
        node.SimulationProgress = node.SimulationProgress + silence < now ? node.SimulationProgress + silence : now;
        _frozenSilenceForgiven[(node.Node, node.Epoch)] = now;
        Console.WriteLine($"{node.Node} epoch={node.Epoch} reported again after {silence.TotalSeconds:F1} s of silence past its lease: "
            + "the silence is not counted as a simulation stall (#1218)");
    }

    /// <summary>#1282: drops the hold's per-incarnation entries of every incarnation that is no longer Active or Closed
    /// (Dead, replaced by a later epoch, or gone from the table). Called by ExpireLeases; caller holds _lock.</summary>
    private void PruneLiveProcessHold()
    {
        if (_liveProcessHoldLogged.Count == 0 && _frozenSilenceForgiven.Count == 0) return;
        bool Gone((string Node, long Epoch) key) => !_state.Nodes.TryGetValue(key.Node, out NodeRegistration? row)
            || row.Epoch != key.Epoch || row.State is not (NodeState.Active or NodeState.Closed);
        foreach ((string, long) key in _liveProcessHoldLogged.Keys.Where(Gone).ToArray()) _liveProcessHoldLogged.Remove(key);
        foreach ((string, long) key in _frozenSilenceForgiven.Keys.Where(Gone).ToArray()) _frozenSilenceForgiven.Remove(key);
    }
}
