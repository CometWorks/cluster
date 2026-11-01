using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClusterPlugin;

internal static class GlobalCommandLedger
{
	private const int MaxEntries = 4096;
	private static readonly object Sync = new();
	private static readonly Dictionary<Key, Stored> Results = new();
	private static readonly Dictionary<Key, Inflight> Pending = new();
	private static long m_order;
	private static long m_durableOrder;
	private static bool m_capturing;

	public static GlobalCommandResult Execute(string subsystem, Guid operationId, string payload, Func<GlobalCommandResult> handler)
		=> Execute(subsystem, operationId, payload, handler, out _);

	/// <summary>
	/// <paramref name="order"/> is the result's place in the ledger (0 for a refusal the ledger does not keep): the
	/// result is in a recoverable global save once <see cref="IsDurable"/> says so (#1270).
	/// </summary>
	public static GlobalCommandResult Execute(string subsystem, Guid operationId, string payload, Func<GlobalCommandResult> handler,
		out long order)
	{
		order = 0;
		if (string.IsNullOrWhiteSpace(subsystem) || operationId == Guid.Empty || payload == null || handler == null)
			throw new ArgumentException("A subsystem, operation ID, payload, and handler are required");
		var key = new Key(subsystem, operationId);
		string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
		TaskCompletionSource<GlobalCommandResult> pending;
		bool owner;
		lock (Sync)
		{
			while (m_capturing) System.Threading.Monitor.Wait(Sync);
			if (Results.TryGetValue(key, out Stored stored))
			{
				if (stored.Fingerprint != fingerprint) return Conflict();
				order = stored.Order;
				return stored.Result;
			}
			if (!Pending.TryGetValue(key, out Inflight inflight))
			{
				pending = new TaskCompletionSource<GlobalCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
				Pending.Add(key, new Inflight(fingerprint, pending));
				owner = true;
			}
			else
			{
				if (inflight.Fingerprint != fingerprint) return Conflict();
				pending = inflight.Completion;
				owner = false;
			}
		}
		if (!owner)
		{
			GlobalCommandResult shared = pending.Task.GetAwaiter().GetResult();
			lock (Sync) order = Results.TryGetValue(key, out Stored done) ? done.Order : 0;
			return shared;
		}

		GlobalCommandResult result;
		try { result = GlobalCommandCodec.Read(GlobalCommandCodec.Write(handler())); }
		catch (Exception exception) { result = new GlobalCommandResult(false, string.Empty, SecretRedactor.Redact(exception.Message)); }
		lock (Sync)
		{
			Results[key] = new Stored(order = ++m_order, fingerprint, result);
			Pending.Remove(key);
			// ponytail: retain recent outcomes; move this table to durable transaction storage if 4096 live operations is insufficient.
			while (Results.Count > MaxEntries) Results.Remove(Results.MinBy(item => item.Value.Order).Key);
			System.Threading.Monitor.PulseAll(Sync);
		}
		pending.SetResult(result);
		return result;
	}

	private static GlobalCommandResult Conflict()
		=> new(false, string.Empty, "Operation ID was reused with a different payload");

	/// <summary>
	/// <paramref name="order"/>: every result up to it is in <paramref name="ledger"/>, and no handler ran while
	/// <paramref name="capture"/> did, so the state those results describe is in <paramref name="value"/> too.
	/// </summary>
	public static bool TryCapture<T>(Func<T> capture, out T value, out string ledger, out long order)
	{
		if (capture == null) throw new ArgumentNullException(nameof(capture));
		lock (Sync)
		{
			if (Pending.Count != 0)
			{
				value = default;
				ledger = null;
				order = 0;
				return false;
			}
			m_capturing = true;
		}
		try
		{
			value = capture();
			lock (Sync)
			{
				ledger = WriteSnapshot();
				order = m_order;
			}
			return true;
		}
		finally
		{
			lock (Sync)
			{
				m_capturing = false;
				System.Threading.Monitor.PulseAll(Sync);
			}
		}
	}

	/// <summary>A global save that holds every result up to <paramref name="order"/> is registered and recoverable.</summary>
	public static void MarkDurable(long order)
	{
		lock (Sync) m_durableOrder = Math.Max(m_durableOrder, order);
	}

	public static bool IsDurable(long order)
	{
		lock (Sync) return order <= m_durableOrder;
	}

	public static long DurableOrder { get { lock (Sync) return m_durableOrder; } }

	/// <summary>The order of the newest result (after <see cref="Restore"/>: of the restored save's last one).</summary>
	public static long Order { get { lock (Sync) return m_order; } }

	/// <summary>
	/// #1318: a journaled command a replacement World Authority replayed, recorded under its operation id and its order
	/// with the result its node was given, so a retry gets that answer and new commands are numbered after it.
	/// </summary>
	public static void Replay(string subsystem, Guid operationId, string payload, long order, GlobalCommandResult result)
	{
		if (string.IsNullOrWhiteSpace(subsystem) || operationId == Guid.Empty || payload == null || order <= 0 || result == null)
			throw new ArgumentException("A subsystem, operation ID, payload, order, and result are required");
		string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
		lock (Sync)
		{
			while (m_capturing) System.Threading.Monitor.Wait(Sync);
			Results[new Key(subsystem, operationId)] = new Stored(order, fingerprint, result);
			m_order = Math.Max(m_order, order);
			while (Results.Count > MaxEntries) Results.Remove(Results.MinBy(item => item.Value.Order).Key);
		}
	}

	public static string Snapshot()
	{
		lock (Sync) return WriteSnapshot();
	}

	public static void Restore(string payload)
	{
		SnapshotModel snapshot = JsonSerializer.Deserialize<SnapshotModel>(string.IsNullOrWhiteSpace(payload) ? "{\"Entries\":[]}" : payload)
			?? throw new InvalidOperationException("Invalid global command ledger");
		if (snapshot.Entries == null || snapshot.Entries.Length > MaxEntries
			|| snapshot.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Subsystem) || entry.Subsystem.Length > 128
				|| entry.OperationId == Guid.Empty || entry.Order <= 0 || entry.Fingerprint?.Length != 64
				|| entry.Fingerprint.Any(character => !Uri.IsHexDigit(character)) || entry.Result == null)
			|| snapshot.Entries.Select(entry => new Key(entry.Subsystem, entry.OperationId)).Distinct().Count() != snapshot.Entries.Length
			|| snapshot.Entries.Select(entry => entry.Order).Distinct().Count() != snapshot.Entries.Length)
			throw new InvalidOperationException("Invalid global command ledger entries");
		lock (Sync)
		{
			if (Pending.Count != 0) throw new InvalidOperationException("Cannot restore the command ledger while operations are pending");
			Results.Clear();
			foreach (Entry entry in snapshot.Entries)
				Results.Add(new Key(entry.Subsystem, entry.OperationId), new Stored(entry.Order, entry.Fingerprint,
					GlobalCommandCodec.Read(GlobalCommandCodec.Write(entry.Result))));
			m_order = snapshot.Entries.Select(entry => entry.Order).DefaultIfEmpty().Max();
			// Restored from a catalogued global save: durable already.
			m_durableOrder = m_order;
		}
	}

	private static string WriteSnapshot() => JsonSerializer.Serialize(new SnapshotModel(Results
		.OrderBy(item => item.Value.Order)
		.Select(item => new Entry(item.Key.Subsystem, item.Key.OperationId, item.Value.Order, item.Value.Fingerprint, item.Value.Result)).ToArray()));

	private readonly record struct Key(string Subsystem, Guid OperationId);
	private sealed record Stored(long Order, string Fingerprint, GlobalCommandResult Result);
	private sealed record Inflight(string Fingerprint, TaskCompletionSource<GlobalCommandResult> Completion);
	private sealed record Entry(string Subsystem, Guid OperationId, long Order, string Fingerprint, GlobalCommandResult Result);
	private sealed record SnapshotModel(Entry[] Entries);
}
