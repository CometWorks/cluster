using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// WA-2: durable node-local reconciliation for store payments. The reservation is a real World
/// Authority debit; a node crash between the reservation and its commit/refund would otherwise leak
/// the debit forever (money destroyed with no record anywhere). Every scope journals its operation
/// ids BEFORE reserving and closes the entry once the payment is settled; a restarting node replays
/// the open legs of every entry with the SAME ids - the banking command ledger makes each leg
/// exactly-once - so the payment ends exactly once no matter where the crash landed:
/// crash before the reserve reached the WA -> the replayed reserve refuses or applies fresh, and the
/// refund only runs when the reserve leg reports success; crash after the reserve -> the reserve
/// replay dedupes to the recorded result and the refund returns the money; crash after the refund
/// but before the journal close -> both legs dedupe; crash after the commit was journaled -> the
/// credit replay dedupes or applies, and only a refused credit is refunded.
/// <para>
/// SE-1226: a leg the World Authority did not answer in time has an unknown outcome, not a failed one.
/// It may still apply (the live WA applied credits 10 s late, and the refund that followed created the
/// money twice). The same settle path therefore runs in the background for live payments too: it repeats
/// a leg with its own id until the WA answers yes or no, and refunds only on a definite no. The credit and
/// refund legs no longer wait on the game thread.
/// </para>
/// <para>
/// SE-1525: a live store trade no longer waits for its reserve either. The node approves it against its replica
/// (<see cref="StorePaymentHolds"/>), journals one "async" entry with all three ids when the trade commits, and the
/// worker sends the reserve, then the credit. A reserve the WA refuses (the payer spent the money on another node in
/// between) is clawed back: the WA takes what the payer has and books the rest as store debt, and the payee is paid.
/// Nothing reaches the WA before the commit, so a trade that never commits needs no settling.
/// </para>
/// </summary>
internal static class StorePaymentJournal
{
    private static readonly object Sync = new object();
    private static readonly BlockingCollection<Work> Queue = new BlockingCollection<Work>();
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static string m_path;
    private static bool m_ready;
    private static bool m_reconciled;
    private static Thread m_worker;

    private sealed record Entry(
        string Op,
        Guid ReserveId,
        Guid RefundId,
        long Payer,
        long Amount,
        Guid CreditId = default,
        long Payee = 0,
        long Credit = 0,
        bool Async = false
    );

    /// <summary><paramref name="Reserved"/>: the reserve leg is known to have applied, so only the refund is left.</summary>
    private sealed record Work(
        Func<long, long, Guid, bool> Change,
        Entry Reserve,
        Entry Commit,
        bool Reserved,
        string Verb,
        Func<BankingAuthorityRequest, Guid, BankingAuthorityResult> Command = null
    );

    /// <summary>SE-1525 test knobs, zero in production: the worker waits before a payment until this
    /// <see cref="Environment.TickCount64"/>, and between a reserve and its credit this long.</summary>
    internal static long TestHoldUntil;
    internal static int TestHoldAfterReserveMs;

    internal static void Install()
    {
        string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(data))
            return;
        m_path = Path.Combine(Path.GetFullPath(data), "cluster", "store-payments.jsonl");
        m_ready = true;
    }

    internal static void Reserve(Guid reserveId, Guid refundId, long payer, long amount) =>
        Append(new Entry("reserve", reserveId, refundId, payer, amount));

    internal static void Close(Guid reserveId) =>
        Append(new Entry("closed", reserveId, Guid.Empty, 0, 0));

    /// <summary>
    /// The payment did not commit: the reserve was refused, unanswered or applied without a settlement. Settles in the
    /// background; <paramref name="reserved"/> skips asking again for a reserve the World Authority already approved.
    /// </summary>
    internal static void Abandon(
        Func<long, long, Guid, bool> change,
        Guid reserveId,
        Guid refundId,
        long payer,
        long amount,
        bool reserved
    ) =>
        Enqueue(
            new Work(
                change,
                new Entry("reserve", reserveId, refundId, payer, amount),
                null,
                reserved,
                "settled"
            )
        );

    /// <summary>Journals the credit leg before it is sent, then settles it in the background.</summary>
    internal static void Commit(
        Func<long, long, Guid, bool> change,
        Guid reserveId,
        Guid refundId,
        long payer,
        long amount,
        Guid creditId,
        long payee,
        long credit
    )
    {
        var commit = new Entry(
            "commit",
            reserveId,
            refundId,
            payer,
            amount,
            creditId,
            payee,
            credit
        );
        Append(commit);
        Enqueue(
            new Work(
                change,
                new Entry("reserve", reserveId, refundId, payer, amount),
                commit,
                true,
                "settled"
            )
        );
    }

    /// <summary>
    /// SE-1525: journals a store payment the node already finished, then settles it in the background: the reserve, then
    /// the credit, a clawback if the reserve is refused. <paramref name="payee"/> 0 has no credit leg.
    /// </summary>
    internal static void CommitAsync(
        Func<BankingAuthorityRequest, Guid, BankingAuthorityResult> command,
        Guid reserveId,
        Guid refundId,
        long payer,
        long amount,
        Guid creditId,
        long payee,
        long credit
    )
    {
        var entry = new Entry(
            "async",
            reserveId,
            refundId,
            payer,
            amount,
            creditId,
            payee,
            credit,
            Async: true
        );
        Append(entry);
        Enqueue(new Work(null, entry, entry, false, "settled", command));
    }

    /// <summary>Self-test: settles one SE-1525 payment on the calling thread, as the background worker would.</summary>
    internal static long SettleAsyncNow(
        Func<BankingAuthorityRequest, Guid, BankingAuthorityResult> command,
        Guid reserveId,
        Guid refundId,
        long payer,
        long amount,
        Guid creditId,
        long payee,
        long credit
    )
    {
        var entry = new Entry(
            "async",
            reserveId,
            refundId,
            payer,
            amount,
            creditId,
            payee,
            credit,
            Async: true
        );
        return SettleAsync(new Work(null, entry, entry, false, "self-test", command));
    }

    /// <summary>Self-test: settles one payment on the calling thread, as the background worker would.</summary>
    internal static void SettleNow(
        Func<long, long, Guid, bool> change,
        Guid reserveId,
        Guid refundId,
        long payer,
        long amount,
        bool reserved,
        Guid creditId = default,
        long payee = 0,
        long credit = 0
    ) =>
        Settle(
            new Work(
                change,
                new Entry("reserve", reserveId, refundId, payer, amount),
                creditId == Guid.Empty
                    ? null
                    : new Entry(
                        "commit",
                        reserveId,
                        refundId,
                        payer,
                        amount,
                        creditId,
                        payee,
                        credit
                    ),
                reserved,
                "self-test"
            )
        );

    /// <summary>SE-1883: journal appends so far and their total and longest time, for the store payment bench.</summary>
    internal static long AppendCount,
        AppendTicks,
        AppendMaxTicks;

    private static void Append(Entry entry)
    {
        if (!m_ready)
            return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            AppendLocked(entry);
        }
        finally
        {
            // SE-1883: a store trade appends on the game thread, so a slow disk shows in the hitch line as store_journal.
            HitchRecorder.Add(HitchRecorder.Bucket.StoreJournal, started);
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref AppendCount);
            Interlocked.Add(ref AppendTicks, ticks);
            long max;
            while ((max = Interlocked.Read(ref AppendMaxTicks)) < ticks)
                if (Interlocked.CompareExchange(ref AppendMaxTicks, ticks, max) == max)
                    break;
        }
    }

    private static void AppendLocked(Entry entry)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_path));
            using FileStream stream = new FileStream(
                m_path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read
            );
            using StreamWriter writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(entry));
            writer.Flush();
            stream.Flush(true);
        }
    }

    /// <summary>
    /// Queues the open legs of every journaled payment for settling, once per process. Reads the journal file, so
    /// callers run it off the game thread.
    /// </summary>
    internal static void Reconcile(
        Func<long, long, Guid, bool> change,
        Func<BankingAuthorityRequest, Guid, BankingAuthorityResult> command
    )
    {
        if (!m_ready || m_reconciled)
            return;
        m_reconciled = true;
        if (!File.Exists(m_path))
            return;
        List<Entry> entries;
        lock (Sync)
            entries = File.ReadAllLines(m_path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<Entry>(line);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(entry => entry != null)
                .ToList();
        HashSet<Guid> closed = entries
            .Where(entry => entry.Op == "closed")
            .Select(entry => entry.ReserveId)
            .ToHashSet();
        Dictionary<Guid, Entry> commits = entries
            .Where(entry => entry.Op == "commit")
            .GroupBy(entry => entry.ReserveId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (
            Entry entry in entries.Where(entry =>
                entry.Op == "reserve" && !closed.Contains(entry.ReserveId)
            )
        )
        {
            commits.TryGetValue(entry.ReserveId, out Entry commit);
            Enqueue(new Work(change, entry, commit, commit != null, "reconciled"));
        }
        // SE-1525: a finished trade the node died before settling; its ids make every leg exactly-once.
        foreach (
            Entry entry in entries.Where(entry =>
                entry.Op == "async" && !closed.Contains(entry.ReserveId)
            )
        )
            Enqueue(new Work(null, entry, entry, false, "reconciled", command));
    }

    private static void Enqueue(Work work)
    {
        lock (Sync)
        {
            if (m_worker == null)
            {
                m_worker = new Thread(SettleLoop)
                {
                    IsBackground = true,
                    Name = "ClusterStorePaymentSettle",
                };
                m_worker.Start();
            }
        }
        Queue.Add(work);
    }

    private static void SettleLoop()
    {
        foreach (Work work in Queue.GetConsumingEnumerable())
        {
            try
            {
                long holdUntil = Interlocked.Read(ref TestHoldUntil);
                while (Environment.TickCount64 < holdUntil)
                {
                    Thread.Sleep(50);
                    holdUntil = Interlocked.Read(ref TestHoldUntil);
                }
                if (work.Reserve.Async)
                    SettleAsync(work);
                else
                    Settle(work);
            }
            catch (Exception exception)
            {
                // Left open: the next restart retries with the same ids, which stays exactly-once.
                Console.WriteLine(
                    "[ClusterPlugin] store payment "
                        + work.Verb
                        + " failed reserve="
                        + work.Reserve.ReserveId
                        + " error="
                        + SecretRedactor.Redact(exception.Message)
                );
            }
        }
    }

    private static void Settle(Work work)
    {
        Entry reserve = work.Reserve;
        Entry commit = work.Commit;
        bool reserved = work.Reserved;
        bool credited = false,
            refunded = false;
        if (commit != null)
            credited = Answer(
                work,
                "credit",
                () => work.Change(commit.Payee, commit.Credit, commit.CreditId)
            );
        else if (!reserved)
            reserved = Answer(
                work,
                "reserve",
                () => work.Change(reserve.Payer, -reserve.Amount, reserve.ReserveId)
            );
        // Only a definite no to the credit, or a reserve that applied without a settlement, gives the payer back the money.
        if (reserved && !credited)
            refunded = Answer(
                work,
                "refund",
                () => work.Change(reserve.Payer, reserve.Amount, reserve.RefundId)
            );
        Console.WriteLine(
            "[ClusterPlugin] store payment "
                + work.Verb
                + " reserve="
                + reserve.ReserveId
                + " payer="
                + reserve.Payer
                + " amount="
                + reserve.Amount
                + " reserved="
                + reserved
                + " refunded="
                + refunded
                + " credited="
                + credited
                + (
                    commit == null
                        ? string.Empty
                        : " payee=" + commit.Payee + " credit=" + commit.Credit
                )
        );
        if (reserved && !credited && !refunded)
            Console.WriteLine(
                "[ClusterPlugin] ERROR: store payment refund refused reserve="
                    + reserve.ReserveId
                    + " payer="
                    + reserve.Payer
                    + " amount="
                    + reserve.Amount
            );
        Close(reserve.ReserveId);
    }

    /// <summary>
    /// SE-1525: settles a store payment the node finished on its own: the reserve, then the credit. A refused reserve is
    /// clawed back (the WA collects what the payer has and books the rest as debt) and the payee is still paid, so the
    /// trade the node finished stays paid in full. Returns the banking version that carries the last leg.
    /// </summary>
    private static long SettleAsync(Work work)
    {
        Entry entry = work.Reserve;
        long version = 0;
        BankingAuthorityResult Leg(string leg, BankingAuthorityRequest request, Guid operation)
        {
            BankingAuthorityResult result = Answer(
                work,
                leg,
                () => work.Command(request, operation)
            );
            version = Math.Max(version, result.Version);
            return result;
        }
        bool reserved = Leg(
            "reserve",
            new BankingAuthorityRequest("Change", entry.Payer, -entry.Amount),
            entry.ReserveId
        ).Success;
        long collected = entry.Amount,
            debt = 0;
        bool clawed = false;
        if (!reserved)
        {
            // The refund id is free here: a refused reserve has nothing to refund.
            BankingAuthorityResult clawback = Leg(
                "clawback",
                new BankingAuthorityRequest("Collect", entry.Payer, entry.Amount),
                entry.RefundId
            );
            clawed = clawback.Success;
            collected = clawback.Collected;
            debt = clawback.Debt;
        }
        int pause = TestHoldAfterReserveMs;
        if (pause > 0)
            Thread.Sleep(pause);
        bool credited = false,
            refunded = false;
        if (entry.Payee != 0 && entry.Credit > 0)
        {
            credited = Leg(
                "credit",
                new BankingAuthorityRequest("Change", entry.Payee, entry.Credit),
                entry.CreditId
            ).Success;
            // A refused credit (no such account any more) gives the payer back what was taken from it.
            if (!credited && reserved)
                refunded = Leg(
                    "refund",
                    new BankingAuthorityRequest("Change", entry.Payer, entry.Amount),
                    entry.RefundId
                ).Success;
        }
        bool paid = credited || entry.Payee == 0 || entry.Credit <= 0;
        string line =
            "store payment "
            + work.Verb
            + " reserve="
            + entry.ReserveId
            + " payer="
            + entry.Payer
            + " amount="
            + entry.Amount
            + " reserved="
            + reserved
            + " credited="
            + credited
            + " refunded="
            + refunded
            + (
                entry.Payee == 0
                    ? string.Empty
                    : " payee=" + entry.Payee + " credit=" + entry.Credit
            )
            + (
                reserved
                    ? string.Empty
                    : " clawback=" + clawed + " collected=" + collected + " debt=" + debt
            )
            + " version="
            + version;
        // A clawback or a credit that did not land moves money outside the trade's own legs: the game log gets it.
        if (reserved && paid)
            Console.WriteLine("[ClusterPlugin] " + line);
        else if (!reserved && clawed && paid)
            MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + line);
        else
            MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ERROR: " + line);
        if (work.Verb != "self-test")
            StorePaymentHolds.End(entry.Payer, entry.Amount, version);
        Close(entry.ReserveId);
        return version;
    }

    /// <summary>
    /// The World Authority's yes or no to one leg. Anything else (a timeout, a lost link, a generation change) says
    /// nothing about whether the leg applied, so the same operation id is sent again until the WA answers.
    /// </summary>
    private static T Answer<T>(Work work, string leg, Func<T> send)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return send();
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[ClusterPlugin] store payment "
                        + leg
                        + " unanswered reserve="
                        + work.Reserve.ReserveId
                        + " attempt="
                        + attempt
                        + " retry_ms="
                        + (long)backoff.TotalMilliseconds
                        + " error="
                        + SecretRedactor.Redact(exception.Message)
                );
            }
            Thread.Sleep(backoff);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}
