namespace ClusterRegistry;

/// <summary>One World Authority command result, journaled before its answer is sent (#1318). Generation is set by the registry.</summary>
internal sealed record GlobalJournalEntry(long Order, string Subsystem, Guid OperationId, string Origin, string Payload, string Result,
    long Generation = 0);
internal sealed record GlobalJournalAppend(GlobalJournalEntry[]? Entries);
/// <param name="DroppedThrough">The highest order the capacity has dropped: a replay after an older save would miss entries.</param>
internal sealed record GlobalJournalPage(GlobalJournalEntry[] Entries, long DroppedThrough);

/// <summary>
/// #1318: a write-ahead journal of the World Authority's command ledger between its global saves. Banking, faction,
/// identity and time-of-day commands are answered while their effect is only in the WA's memory, and the next global
/// save that holds it can be up to a minute away; a WA killed in that window came back with the older balances while
/// the asking node had already handed out the purchase or payout. Holding each answer for a save costs 0.6-8 s on a
/// node's game thread (#1270), so the WA appends the command here instead and answers once the append is
/// acknowledged: one round trip. A replacement restores the newest recoverable global save and replays the entries
/// newer than its ledger order (<see cref="GlobalJournal"/>).
///
/// Fenced like a global save: only the current World Authority, at its own epoch and generation, may append. An
/// older generation's entries at or above a newer generation's first order are dropped: that newer WA numbered its
/// own commands from the save it restored, so it never replayed them (its journal read failed) and they describe a
/// history that is gone. Memory only: it outlives a WA, which is what it is for, and not the Gateway, whose own
/// unclean stop already loses up to a flush interval of registrations (T-0194). Capacity-bounded, oldest first.
/// </summary>
internal sealed partial class GatewayRegistry
{
    internal const int GlobalJournalCapacity = 16384;
    private const int GlobalJournalEntryChars = 256 * 1024;
    private readonly SortedDictionary<long, GlobalJournalEntry> _globalJournal = [];
    private long _globalJournalDroppedThrough;

    public int AppendGlobalJournal(string node, long epoch, long generation, GlobalJournalEntry[] entries)
    {
        if (entries.Length == 0 || entries.Any(entry => entry.Order <= 0 || string.IsNullOrWhiteSpace(entry.Subsystem)
                || entry.Subsystem.Length > 128 || entry.OperationId == Guid.Empty || entry.Origin == null || entry.Payload == null
                || entry.Result == null || entry.Payload.Length + entry.Result.Length > GlobalJournalEntryChars)
            || entries.Select(entry => entry.Order).Distinct().Count() != entries.Length)
            throw new ArgumentException("A global journal append requires entries with distinct positive orders, a subsystem, an operation ID, a payload and a result");
        lock (_lock)
        {
            // Not refused while Empty: a WA past its Final still serves the regular nodes it waits for (T-0048), and a
            // refused append is never answered, which would stall their game threads for the command timeout. Its
            // commands then outlive it here, for a replacement started while this Gateway runs.
            RequireNode(node, epoch);
            WorldAuthority authority = RequireWorldAuthority(node, epoch, generation);
            if (authority.LeaseExpires <= _clock())
                throw new RegistryConflictException($"World Authority generation {generation} is fenced");
            long first = entries.Min(entry => entry.Order);
            foreach (long stale in _globalJournal.Where(item => item.Value.Generation < generation && item.Key >= first).Select(item => item.Key).ToArray())
                _globalJournal.Remove(stale);
            foreach (GlobalJournalEntry entry in entries)
                _globalJournal[entry.Order] = entry with { Generation = generation };
            while (_globalJournal.Count > GlobalJournalCapacity)
            {
                long oldest = _globalJournal.Keys.First();
                _globalJournal.Remove(oldest);
                _globalJournalDroppedThrough = Math.Max(_globalJournalDroppedThrough, oldest);
            }
            return entries.Length;
        }
    }

    /// <summary>The journaled commands after ledger order <paramref name="after"/>, in order.</summary>
    public GlobalJournalPage GlobalJournal(long after)
    {
        lock (_lock)
            return new GlobalJournalPage(_globalJournal.Where(item => item.Key > after).Select(item => item.Value).ToArray(),
                _globalJournalDroppedThrough);
    }
}
