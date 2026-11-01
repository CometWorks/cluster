using System.Diagnostics;

namespace ClusterRegistry;

/// <summary>
/// Per-transfer timing and size records, the calibration source for the Partitioning §7.5
/// handover cost model (TestPlan P3-CST-01) and the arrival-matrix evidence of P3-CLU-01/02.
/// Ring of the most recent transfers, exposed by GET /registry/handovers/history.
/// </summary>
public sealed class HandoverTelemetry
{
    // ponytail: in-memory ring, a gateway restart loses it; persist per run if calibration
    // ever needs transfers that predate the current gateway process.
    private const int Capacity = 512;

    private readonly object _lock = new();
    private readonly Queue<Transfer> _ring = new();
    private readonly Dictionary<string, Transfer> _byId = new();

    public sealed record StateStamp(string State, long AtMs);

    public sealed class Transfer
    {
        public required string TransferId { get; init; }
        public required ulong Partition { get; init; }
        public required string SourceNode { get; init; }
        public required string TargetNode { get; init; }
        /// <summary>Client switches this transfer pays — the γ input.</summary>
        public required int Clients { get; init; }
        /// <summary>Source-report size proxies at decision time; null when the report lagged.</summary>
        public long? Pcu { get; init; }
        public int? MemberCount { get; init; }
        /// <summary>Measured snapshot sizes — the α ground truth; 0 until the capture lands.</summary>
        public long SectorBytes { get; set; }
        public long NetworkStateBytes { get; set; }
        public long ClientStateBytes { get; set; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>Every protocol state with its elapsed-since-begun ms; windows (freeze,
        /// client switch) are derived from these, e.g. captured→committed and
        /// imported→target-traffic-ready.</summary>
        public List<StateStamp> States { get; } = [];
        /// <summary>Node-reported game-thread hitches attributed to this transfer (HandoverImprovements.md §3).</summary>
        public List<HitchStamp> Hitches { get; } = [];
        internal long StartTimestamp { get; } = Stopwatch.GetTimestamp();
    }

    /// <summary>PhaseMs = how much of that frame handover phases held; Ms is the whole frame.</summary>
    public sealed record HitchStamp(string Node, string Phase, double Ms, DateTime Utc, double PhaseMs);

    /// <summary>Files a node's hitch on the transfer it names; hitches outside any transfer are the node's own business (/cluster/hitches).</summary>
    public void Hitch(string? transferId, string node, string phase, double ms, DateTime utc, double phaseMs = 0)
    {
        if (transferId == null) return;
        lock (_lock)
            if (_byId.TryGetValue(transferId, out Transfer? transfer))
                transfer.Hitches.Add(new HitchStamp(node, phase, ms, utc, phaseMs));
    }

    public Transfer Begin(string transferId, ulong partition, string sourceNode, string targetNode,
        int clients, long? pcu, int? memberCount)
    {
        Transfer transfer = new()
        {
            TransferId = transferId, Partition = partition, SourceNode = sourceNode,
            TargetNode = targetNode, Clients = clients, Pcu = pcu, MemberCount = memberCount,
        };
        lock (_lock)
        {
            _byId[transferId] = transfer;
            _ring.Enqueue(transfer);
            while (_ring.Count > Capacity)
                _byId.Remove(_ring.Dequeue().TransferId);
        }
        return transfer;
    }

    /// <summary>Stamps a protocol state on the transfer; returns the elapsed ms since begun,
    /// or null for a transfer this telemetry never saw begin.</summary>
    public long? Stamp(string transferId, string state)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(transferId, out Transfer? transfer))
                return null;
            long atMs = (long)Stopwatch.GetElapsedTime(transfer.StartTimestamp).TotalMilliseconds;
            transfer.States.Add(new StateStamp(state, atMs));
            return atMs;
        }
    }

    /// <summary>Copies under the lock so serialization never races an in-flight Stamp.</summary>
    public object[] Snapshot()
    {
        lock (_lock)
            return [.. _ring.Select(t => new
            {
                t.TransferId, t.Partition, t.SourceNode, t.TargetNode, t.Clients, t.Pcu,
                t.MemberCount, t.SectorBytes, t.NetworkStateBytes, t.ClientStateBytes,
                t.StartedUtc, States = t.States.ToArray(), Hitches = t.Hitches.ToArray(),
            })];
    }
}
