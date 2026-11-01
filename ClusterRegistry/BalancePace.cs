namespace ClusterRegistry;

/// <summary>
/// T-0393: the registry-side state of rebalancing pace control (~/notes/T-0393-RebalancePace.md) - the
/// hysteresis flag behind <see cref="CostModel.BalanceLowWater"/>, the per-target sliding window behind
/// <see cref="CostModel.BudgetMovesPerNodeHour"/>, and the last-hour counters /balance/status publishes.
/// <c>DirectGateway.ChooseRebalancePlans</c> stays a pure function: the balance loop asks this for the pass's gate and over-budget targets and reports
/// the pass back. Every method takes the clock, so a sequence of passes is testable without sleeping.
/// </summary>
internal sealed class BalancePace
{
    private const double WindowSeconds = 3600;
    private readonly object _lock = new();
    private bool _active;
    // Paced pins per TARGET node, oldest first. Recorded whether or not a budget is configured, so a budget
    // switched on live starts from the real last hour.
    private readonly Dictionary<string, Queue<DateTime>> _targetPins = new();
    private readonly Queue<(DateTime At, int Pins, int MinGain, int Budget, int Deadband, int Residence)> _events = new();

    /// <summary>Whether balancing is active: the high water was crossed and not every node is back within the low water.</summary>
    public bool Active { get { lock (_lock) return _active; } }

    /// <summary>The threshold both tolerance gates use this pass: the tolerance while idle, the low water while active.
    /// With the default low water (the tolerance) this is always the tolerance.</summary>
    public double Gate(CostModel model)
    {
        lock (_lock) return _active ? model.EffectiveLowWater() : model.BalanceRiskTolerance;
    }

    /// <summary>The nodes that were the target of at least BudgetMovesPerNodeHour paced pins in the last hour;
    /// null while the budget is off. The planner applies it only below BudgetUnderDeviation.</summary>
    public IReadOnlySet<string>? OverBudget(CostModel model, DateTime now)
    {
        if (model.BudgetMovesPerNodeHour <= 0) return null;
        lock (_lock)
        {
            Prune(now);
            return _targetPins.Where(pair => pair.Value.Count >= model.BudgetMovesPerNodeHour).Select(pair => pair.Key).ToHashSet();
        }
    }

    /// <summary>A paced balance pin to <paramref name="target"/> (the idle-cluster plan is not charged: nobody plays).</summary>
    public void Pinned(string target, DateTime now)
    {
        lock (_lock)
        {
            if (!_targetPins.TryGetValue(target, out Queue<DateTime>? pins)) _targetPins[target] = pins = new();
            pins.Enqueue(now);
        }
    }

    /// <summary>
    /// One planner pass ran with threshold <paramref name="gate"/>: advance the hysteresis and count it. The flag
    /// becomes worst &gt; gate - so an idle pass (gate = tolerance) starts balancing above the high water, and an
    /// active pass (gate = low water) keeps it until every node is within the low water. A pass the excess gate
    /// stopped reports deviation 0, which ends an active phase (that gate also uses the low water while active).
    /// A deadband hold is an idle pass whose worst deviation lies in (low water, tolerance]: the low water would
    /// have moved, the high water does not.
    /// </summary>
    public void Passed(CostModel model, double gate, double worstDeviation, double worstClientDeviation,
        int pins, int minGainSkipped, int budgetSkipped, int residenceHeld, DateTime now)
    {
        double worst = Math.Max(worstDeviation, worstClientDeviation);
        lock (_lock)
        {
            bool wasActive = _active;
            _active = worst > gate;
            int deadband = !wasActive && worst > model.EffectiveLowWater() && worst <= model.BalanceRiskTolerance ? 1 : 0;
            if (pins != 0 || minGainSkipped != 0 || budgetSkipped != 0 || deadband != 0 || residenceHeld != 0)
                _events.Enqueue((now, pins, minGainSkipped, budgetSkipped, deadband, residenceHeld));
            Prune(now);
        }
    }

    /// <summary>Last-hour counters: pins (paced and idle-plan components), candidates skipped by the minimum gain and by
    /// the budget (one per candidate per pass), idle passes held by the deadband, partitions held by residence (per pass).</summary>
    public (int Pins, int MinGainSkips, int BudgetSkips, int DeadbandHolds, int ResidenceHolds) LastHour(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            return (_events.Sum(e => e.Pins), _events.Sum(e => e.MinGain), _events.Sum(e => e.Budget),
                _events.Sum(e => e.Deadband), _events.Sum(e => e.Residence));
        }
    }

    /// <summary>Paced pins per target node in the last hour (the budget's window).</summary>
    public Dictionary<string, int> TargetPinsLastHour(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            return _targetPins.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        }
    }

    private void Prune(DateTime now)
    {
        DateTime cutoff = now.AddSeconds(-WindowSeconds);
        foreach ((string node, Queue<DateTime> pins) in _targetPins.ToArray())
        {
            while (pins.Count != 0 && pins.Peek() <= cutoff) pins.Dequeue();
            if (pins.Count == 0) _targetPins.Remove(node);
        }
        while (_events.Count != 0 && _events.Peek().At <= cutoff) _events.Dequeue();
    }
}
