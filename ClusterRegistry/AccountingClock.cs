using System.Text.Json.Serialization;

namespace ClusterRegistry;

internal sealed record AccountingClockSample([property: JsonRequired] string World, [property: JsonRequired] string Epoch,
    [property: JsonRequired] long Frame, [property: JsonRequired] long Generation, [property: JsonRequired] long SunTicks,
    [property: JsonRequired] long HistoryFrom, [property: JsonRequired] Dictionary<long, long> SunRuns, [property: JsonRequired] int Version = 1);
internal sealed record AccountingSunRun([property: JsonRequired] long Frame, [property: JsonRequired] long Ticks);

internal sealed partial class GatewayRegistry
{
    // Commit before publishing a resource-work address. The existing SQLite store
    // writes changed rows only; no native tick or wall-clock estimate is persisted here.
    internal AccountingClockSample? AccountingClock(string node, long epoch, AccountingClockSample? sample)
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            if (sample != null)
            {
                if (_state.WorldAuthority.Node != node || _state.WorldAuthority.Epoch != epoch
                    || sample.Generation != _state.WorldAuthority.Generation || _state.WorldAuthority.LeaseExpires <= _clock())
                    throw new InvalidOperationException("Only the current World Authority may report simulation work");
                if (sample.Version != 1 || !Guid.TryParseExact(sample.Epoch, "D", out _) || sample.Frame < 0 || sample.SunTicks < 0 || string.IsNullOrEmpty(sample.World)
                    || sample.SunRuns == null || sample.HistoryFrom < 0 || sample.HistoryFrom > sample.Frame)
                    throw new ArgumentException("Invalid accounting clock sample");
                var previous = _state.AccountingClock;
                if (previous != null && (sample.World != previous.World || sample.Epoch != previous.Epoch || sample.Frame < previous.Frame))
                    throw new InvalidOperationException("Accounting clock epoch mismatch or backwards progress");
                if (previous == null || sample.Frame != previous.Frame || sample.SunTicks != previous.SunTicks
                    || sample.Generation != previous.Generation || sample.SunRuns.Count != 0)
                {
                    long priorFrame = previous?.Frame ?? 0;
                    if (sample.HistoryFrom > priorFrame || sample.SunRuns.Any(p => p.Key < sample.HistoryFrom || p.Key >= sample.Frame || p.Value < 0))
                        throw new InvalidOperationException("Incomplete sun/work interval");
                    foreach (var run in sample.SunRuns.OrderBy(p => p.Key))
                    {
                        int i = SunRunAt(run.Key);
                        if (run.Key < priorFrame && (i < 0 || _state.AccountingSun[i].Frame != run.Key || _state.AccountingSun[i].Ticks != run.Value))
                            throw new InvalidOperationException("Sun/work history is immutable");
                    }
                    if (sample.Frame > 0 && _state.AccountingSun.Count == 0 && !sample.SunRuns.ContainsKey(0))
                        throw new InvalidOperationException("Sun/work history has no origin");
                    foreach (var run in sample.SunRuns.OrderBy(p => p.Key).Where(p => p.Key >= priorFrame))
                        _state.AccountingSun.Add(new(run.Key, run.Value));
                    _state.AccountingClock = sample with { HistoryFrom = sample.Frame, SunRuns = [] };
                    Persist();
                    FlushState();
                }
            }
            return _state.AccountingClock;
        }
    }

    internal object AccountingSun(string epoch, long from, long through)
    {
        lock (_lock)
        {
            EnsurePersistenceHealthy();
            var clock = _state.AccountingClock;
            if (clock == null || epoch != clock.Epoch || from < 0 || through < from || through > clock.Frame)
                throw new InvalidOperationException("Sun/work interval is unavailable in this accounting epoch");
            int start = SunRunAt(from);
            if (through > from && start < 0) throw new InvalidOperationException("Sun/work history has no interval origin");
            var runs = new List<long[]>();
            if (through > from)
                for (int i = start; i < _state.AccountingSun.Count && _state.AccountingSun[i].Frame < through; i++)
                    runs.Add([_state.AccountingSun[i].Frame, _state.AccountingSun[i].Ticks]);
            return new { Version = 1, Epoch = epoch, From = from, Through = through, Runs = runs };
        }
    }

    private int SunRunAt(long frame)
    {
        int low = -1, high = _state.AccountingSun.Count;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (_state.AccountingSun[middle].Frame <= frame) low = middle; else high = middle;
        }
        return low;
    }
}
