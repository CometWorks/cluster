using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

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

	private sealed record Entry(string Op, Guid ReserveId, Guid RefundId, long Payer, long Amount,
		Guid CreditId = default, long Payee = 0, long Credit = 0);

	/// <summary><paramref name="Reserved"/>: the reserve leg is known to have applied, so only the refund is left.</summary>
	private sealed record Work(Func<long, long, Guid, bool> Change, Entry Reserve, Entry Commit, bool Reserved, string Verb);

	internal static void Install()
	{
		string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
		if (string.IsNullOrWhiteSpace(data)) return;
		m_path = Path.Combine(Path.GetFullPath(data), "cluster", "store-payments.jsonl");
		m_ready = true;
	}

	internal static void Reserve(Guid reserveId, Guid refundId, long payer, long amount)
		=> Append(new Entry("reserve", reserveId, refundId, payer, amount));

	internal static void Close(Guid reserveId)
		=> Append(new Entry("closed", reserveId, Guid.Empty, 0, 0));

	/// <summary>
	/// The payment did not commit: the reserve was refused, unanswered or applied without a settlement. Settles in the
	/// background; <paramref name="reserved"/> skips asking again for a reserve the World Authority already approved.
	/// </summary>
	internal static void Abandon(Func<long, long, Guid, bool> change, Guid reserveId, Guid refundId, long payer, long amount, bool reserved)
		=> Enqueue(new Work(change, new Entry("reserve", reserveId, refundId, payer, amount), null, reserved, "settled"));

	/// <summary>Journals the credit leg before it is sent, then settles it in the background.</summary>
	internal static void Commit(Func<long, long, Guid, bool> change, Guid reserveId, Guid refundId, long payer, long amount,
		Guid creditId, long payee, long credit)
	{
		var commit = new Entry("commit", reserveId, refundId, payer, amount, creditId, payee, credit);
		Append(commit);
		Enqueue(new Work(change, new Entry("reserve", reserveId, refundId, payer, amount), commit, true, "settled"));
	}

	/// <summary>Self-test: settles one payment on the calling thread, as the background worker would.</summary>
	internal static void SettleNow(Func<long, long, Guid, bool> change, Guid reserveId, Guid refundId, long payer, long amount, bool reserved,
		Guid creditId = default, long payee = 0, long credit = 0)
		=> Settle(new Work(change, new Entry("reserve", reserveId, refundId, payer, amount),
			creditId == Guid.Empty ? null : new Entry("commit", reserveId, refundId, payer, amount, creditId, payee, credit), reserved, "self-test"));

	private static void Append(Entry entry)
	{
		if (!m_ready) return;
		lock (Sync)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(m_path));
			using FileStream stream = new FileStream(m_path, FileMode.Append, FileAccess.Write, FileShare.Read);
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
	internal static void Reconcile(Func<long, long, Guid, bool> change)
	{
		if (!m_ready || m_reconciled) return;
		m_reconciled = true;
		if (!File.Exists(m_path)) return;
		List<Entry> entries;
		lock (Sync)
			entries = File.ReadAllLines(m_path).Where(line => !string.IsNullOrWhiteSpace(line))
				.Select(line =>
				{
					try { return JsonSerializer.Deserialize<Entry>(line); }
					catch { return null; }
				})
				.Where(entry => entry != null).ToList();
		HashSet<Guid> closed = entries.Where(entry => entry.Op == "closed").Select(entry => entry.ReserveId).ToHashSet();
		Dictionary<Guid, Entry> commits = entries.Where(entry => entry.Op == "commit").GroupBy(entry => entry.ReserveId)
			.ToDictionary(group => group.Key, group => group.Last());
		foreach (Entry entry in entries.Where(entry => entry.Op == "reserve" && !closed.Contains(entry.ReserveId)))
		{
			commits.TryGetValue(entry.ReserveId, out Entry commit);
			Enqueue(new Work(change, entry, commit, commit != null, "reconciled"));
		}
	}

	private static void Enqueue(Work work)
	{
		lock (Sync)
		{
			if (m_worker == null)
			{
				m_worker = new Thread(SettleLoop) { IsBackground = true, Name = "ClusterStorePaymentSettle" };
				m_worker.Start();
			}
		}
		Queue.Add(work);
	}

	private static void SettleLoop()
	{
		foreach (Work work in Queue.GetConsumingEnumerable())
		{
			try { Settle(work); }
			catch (Exception exception)
			{
				// Left open: the next restart retries with the same ids, which stays exactly-once.
				Console.WriteLine("[ClusterPlugin] store payment " + work.Verb + " failed reserve=" + work.Reserve.ReserveId
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		}
	}

	private static void Settle(Work work)
	{
		Entry reserve = work.Reserve;
		Entry commit = work.Commit;
		bool reserved = work.Reserved;
		bool credited = false, refunded = false;
		if (commit != null)
			credited = Answer(work, "credit", () => work.Change(commit.Payee, commit.Credit, commit.CreditId));
		else if (!reserved)
			reserved = Answer(work, "reserve", () => work.Change(reserve.Payer, -reserve.Amount, reserve.ReserveId));
		// Only a definite no to the credit, or a reserve that applied without a settlement, gives the payer back the money.
		if (reserved && !credited)
			refunded = Answer(work, "refund", () => work.Change(reserve.Payer, reserve.Amount, reserve.RefundId));
		Console.WriteLine("[ClusterPlugin] store payment " + work.Verb + " reserve=" + reserve.ReserveId
			+ " payer=" + reserve.Payer + " amount=" + reserve.Amount
			+ " reserved=" + reserved + " refunded=" + refunded + " credited=" + credited
			+ (commit == null ? string.Empty : " payee=" + commit.Payee + " credit=" + commit.Credit));
		if (reserved && !credited && !refunded)
			Console.WriteLine("[ClusterPlugin] ERROR: store payment refund refused reserve=" + reserve.ReserveId
				+ " payer=" + reserve.Payer + " amount=" + reserve.Amount);
		Close(reserve.ReserveId);
	}

	/// <summary>
	/// The World Authority's yes or no to one leg. Anything else (a timeout, a lost link, a generation change) says
	/// nothing about whether the leg applied, so the same operation id is sent again until the WA answers.
	/// </summary>
	private static bool Answer(Work work, string leg, Func<bool> send)
	{
		TimeSpan backoff = TimeSpan.FromSeconds(1);
		for (int attempt = 1; ; attempt++)
		{
			try { return send(); }
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] store payment " + leg + " unanswered reserve=" + work.Reserve.ReserveId
					+ " attempt=" + attempt + " retry_ms=" + (long)backoff.TotalMilliseconds
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
			Thread.Sleep(backoff);
			backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
		}
	}
}
