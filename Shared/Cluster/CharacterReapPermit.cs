#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace ClusterProtocol;

/// <summary>A Registry-serialized authorization, valid until this exact executor resolves it.</summary>
public sealed record CharacterReapPermit(Guid Operation, long Entity, long Identity, string Node,
	long Epoch, ulong Partition, long Generation, long Revision);

/// <summary>Single lookup producer, game-thread consumer; resolved permissions cannot be replayed.</summary>
public sealed class CharacterReapQueue
{
	public sealed class Work
	{
		public readonly ulong ClientId;
		public readonly CharacterReapPermit Permit;
		private int m_consumed;
		private int m_ready;
		public bool ReadyForResolve => Volatile.Read(ref m_ready) != 0;
		public void FinishExecution() => Volatile.Write(ref m_ready, 1);
		public bool Consumed => Volatile.Read(ref m_consumed) != 0;
		internal void Consume() => Volatile.Write(ref m_consumed, 1);
		internal Work(ulong client, CharacterReapPermit permit) { ClientId = client; Permit = permit; }
	}

	private readonly object m_sync = new();
	private readonly Dictionary<ulong, Work> m_active = new();
	private readonly Dictionary<ulong, long> m_consumed = new();
	private readonly Queue<Work> m_queue = new();

	public bool Contains(ulong client) { lock (m_sync) return m_active.ContainsKey(client); }
	public Work[] Unresolved() { lock (m_sync) return m_active.Values.Where(work => work.ReadyForResolve).ToArray(); }

	public bool Enqueue(ulong client, CharacterReapPermit permit)
	{
		if (permit == null || permit.Operation == Guid.Empty || permit.Revision <= 0) return false;
		lock (m_sync)
		{
			if (m_active.ContainsKey(client) || m_consumed.TryGetValue(client, out long revision) && permit.Revision <= revision)
				return false;
			var work = new Work(client, permit);
			m_active.Add(client, work);
			m_queue.Enqueue(work);
			return true;
		}
	}

	public bool TryTake([NotNullWhen(true)] out Work? work)
	{
		lock (m_sync)
		{
			if (m_queue.Count == 0) { work = null; return false; }
			work = m_queue.Dequeue();
			work.Consume(); // before either close or cancellation can be acknowledged
			m_consumed[work.ClientId] = work.Permit.Revision;
			return true;
		}
	}

	public void Resolved(Work work)
	{
		if (!work.ReadyForResolve) throw new InvalidOperationException("Character reap execution has not finished");
		lock (m_sync)
			if (m_active.TryGetValue(work.ClientId, out Work? current) && ReferenceEquals(current, work))
				m_active.Remove(work.ClientId);
	}

	/// <summary>Caller guarantees the lookup producer finished and every queued response was drained.
	/// Resolve workers never enqueue. At this quiescent point no old response can reappear, so only
	/// unresolved clients need a watermark; no permanent per-player history accumulates.</summary>
	public bool FinishLookup()
	{
		lock (m_sync)
		{
			if (m_queue.Count != 0) return false;
			foreach (ulong client in m_consumed.Keys.Where(client => !m_active.ContainsKey(client)).ToArray())
				m_consumed.Remove(client);
			return true;
		}
	}
}
