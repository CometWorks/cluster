using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// WA-2: durable node-local reconciliation for store payments. The reservation is a real World
/// Authority debit; a node crash between the reservation and its commit/refund would otherwise leak
/// the debit forever (money destroyed with no record anywhere). Every scope journals its operation
/// ids BEFORE reserving and closes the entry after the commit or refund; a restarting node replays
/// BOTH legs of every open entry with the SAME ids - the banking command ledger makes each leg
/// exactly-once - so the pair nets zero no matter where the crash landed:
/// crash before the reserve reached the WA -> the replayed reserve refuses or applies fresh, and the
/// refund only runs when the reserve leg reports success; crash after the reserve -> the reserve
/// replay dedupes to the recorded result and the refund returns the money; crash after the refund
/// but before the journal close -> both legs dedupe.
/// </summary>
internal static class StorePaymentJournal
{
	private static readonly object Sync = new object();
	private static string m_path;
	private static bool m_ready;
	private static bool m_reconciled;

	private sealed record Entry(string Op, Guid ReserveId, Guid RefundId, long Payer, long Amount);

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
	/// Replays both legs of every open reservation, once per process. Blocks on World Authority round
	/// trips, so callers run it off the game thread.
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
		foreach (Entry entry in entries.Where(entry => entry.Op == "reserve" && !closed.Contains(entry.ReserveId)))
		{
			try
			{
				bool reserved = change(entry.Payer, -entry.Amount, entry.ReserveId);
				bool refunded = reserved && change(entry.Payer, entry.Amount, entry.RefundId);
				Console.WriteLine("[ClusterPlugin] store payment reconciled reserve=" + entry.ReserveId
					+ " payer=" + entry.Payer + " amount=" + entry.Amount
					+ " reserved=" + reserved + " refunded=" + refunded);
				Close(entry.ReserveId);
			}
			catch (Exception exception)
			{
				// Left open: the next restart retries with the same ids, which stays exactly-once.
				Console.WriteLine("[ClusterPlugin] store payment reconcile failed reserve=" + entry.ReserveId
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		}
	}
}
