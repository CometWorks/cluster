using System;
using System.Collections.Generic;
using System.Threading;
using VRage.Library.Algorithms;

namespace ClusterPlugin;

/// <summary>
/// Registry-issued NetworkId allocator installed into MyReplicationLayer in place of the vanilla
/// SequenceIdGenerator. The vanilla NextId/Return are not virtual, so the replication layer reaches
/// this class through the Harmony prefixes in Shared/Patches/SequenceIdGeneratorPatch.cs, which
/// dispatch here whenever the generator instance is one of ours.
/// </summary>
public sealed class NetworkIdRangeGenerator : SequenceIdGenerator
{
	private new readonly object m_lock = new object();
	private readonly Func<NetworkIdBlock> m_allocate;
	private readonly List<NetworkIdBlock> m_blocks = new List<NetworkIdBlock>();
	private bool m_prefetched;
	// T-0350: NextId runs on the game thread and the allocation is a synchronous registry request.
	private const long PrefetchBackoffMilliseconds = 5000;
	private readonly Func<bool> m_registryUnreachable;
	private readonly Func<long> m_clock;
	private long m_prefetchRetryAfter = long.MinValue;

	/// <summary>
	/// T-0401: the block ids are being drawn from (m_blocks[0]). A traced MyEntities.Add spent 9.5 % spinning in
	/// Monitor.Enter_Slowpath here (one NextId per state group, SetReadyForReplication, from several threads), so the
	/// ids below <see cref="Limit"/> are taken lock-free with a compare-and-swap on <see cref="Next"/>; the lock is
	/// taken only at or past the limit: the 75 % prefetch point while no next block is held (where the old NextId
	/// tried the prefetch on every call), and the end of the block. Next and Limit only grow; Limit is raised under
	/// the lock only. long, so End = uint.MaxValue cannot wrap.
	/// </summary>
	private sealed class Cursor
	{
		public readonly long Start;
		public readonly long End;           // inclusive, as NetworkIdBlock
		public readonly long PrefetchAt;    // first id whose draw triggers the prefetch: (id - Start) * 4 >= size * 3
		public long Next;
		public long Limit;                  // ids < Limit go lock-free

		public Cursor(NetworkIdBlock block, bool prefetched)
		{
			Start = block.Start;
			End = block.End;
			long size = End - Start + 1;
			PrefetchAt = Start + (size * 3 + 3) / 4;
			Next = Start;
			Limit = prefetched ? End + 1 : PrefetchAt;
		}
	}

	private Cursor m_cursor;

	/// <param name="registryUnreachable">T-0350: true while the registry is known not to answer (ClusterRuntime.RegistryUnreachable).</param>
	/// <param name="clock">Milliseconds, monotonic; Environment.TickCount64 unless a test supplies its own.</param>
	public NetworkIdRangeGenerator(NetworkIdBlock initial, Func<NetworkIdBlock> allocate, Func<bool> registryUnreachable = null, Func<long> clock = null)
	{
		m_allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));
		m_registryUnreachable = registryUnreachable ?? (() => false);
		m_clock = clock ?? (() => Environment.TickCount64);
		Add(initial);
		m_cursor = new Cursor(initial, prefetched: false);
	}

	public new uint NextId()
	{
		// Fast path: an id below the limit of the current block, no lock.
		Cursor cursor = Volatile.Read(ref m_cursor);
		while (true)
		{
			long next = Volatile.Read(ref cursor.Next);
			if (next >= Volatile.Read(ref cursor.Limit)) break;
			if (Interlocked.CompareExchange(ref cursor.Next, next + 1, next) == next) return (uint)next;
		}
		return NextIdLocked();
	}

	/// <summary>The old NextId, step for step, for an id at or past the fast path's limit (or a stale cursor).</summary>
	private uint NextIdLocked()
	{
		lock (m_lock)
		{
			while (true)
			{
				Cursor current = m_cursor;
				long next = Volatile.Read(ref current.Next);
				if (!m_prefetched && next >= current.PrefetchAt && !m_registryUnreachable() && m_clock() >= m_prefetchRetryAfter)
				{
					// T-0264: with 64k registry blocks this runs several times per node epoch instead of never, so a
					// registry hiccup here must not break replication - the quarter of the block still left is the
					// margin. Only running out (below) is fatal.
					// T-0350: not attempted while the registry is unreachable, and a failed attempt is retried after 5 s
					// rather than after another 1024 ids - a busy node draws those in well under a second, and each
					// retry held the game thread for the request timeout.
					try
					{
						Add(m_allocate());
						m_prefetched = true;
						Volatile.Write(ref current.Limit, current.End + 1);
					}
					catch (Exception error)
					{
						m_prefetchRetryAfter = m_clock() + PrefetchBackoffMilliseconds;
						Console.WriteLine("[ClusterPlugin] network_id_block prefetch failed, retrying after 5 s: " + error.GetType().Name + ": " + error.Message);
					}
				}
				if (next > current.End)
				{
					if (m_blocks.Count == 1)
					{
						// T-0350: out of ids with the registry unreachable - the request would throw this after its timeout.
						if (m_registryUnreachable()) throw new System.Net.Http.HttpRequestException("Registry unreachable: no network id block");
						Add(m_allocate());
					}
					m_blocks.RemoveAt(0);
					m_prefetched = m_blocks.Count > 1;
					m_prefetchRetryAfter = long.MinValue;
					Volatile.Write(ref m_cursor, new Cursor(m_blocks[0], m_prefetched));
					continue;   // the new block's first id (its prefetch point is never its first id)
				}
				// Fast-path threads may draw from this cursor concurrently (below the limit): take the id the same way.
				if (Interlocked.CompareExchange(ref current.Next, next + 1, next) == next) return (uint)next;
			}
		}
	}

	/// <summary>
	/// T-0268: draws the next block now, as NextId does at 75% of the current one, so the prefetch path and its
	/// "network_id_block allocated ... initial=false" line can be exercised on demand (POST /network-id-prefetch).
	/// Returns null when a next block is already held: the ids already drawn stay the only ones issued.
	/// </summary>
	public NetworkIdBlock? PrefetchNow()
	{
		lock (m_lock)
		{
			if (m_blocks.Count > 1)
				return null;
			NetworkIdBlock block = m_allocate();
			Add(block);
			m_prefetched = true;
			Volatile.Write(ref m_cursor.Limit, m_cursor.End + 1);
			return block;
		}
	}

	/// <summary>
	/// Deliberately a no-op: an id is issued once per registry block and never again. T-0143.
	/// </summary>
	/// <remarks>
	/// Vanilla returns an id when its replicable is removed and reissues it 30 minutes later, which is
	/// safe only while ids never leave the process. Here they do: a handover carries every replicable's
	/// id to the target, so content that LEFT this node still owns the id this node was about to reissue.
	/// When that content came back the id was taken, and two things broke (w31, 2026-09-13): the import
	/// failed "NetworkId belongs to a different replicated object" (86 failures, every one on an id the
	/// importer itself had minted, none earlier than 30.67 min into its epoch), and vanilla
	/// AddNetworkObject refused the colliding NEW object with only a "Replicated object already exists"
	/// log line - while Replicate still put it in the replicable tree. An object with no id is skipped by
	/// MyReplicationServer.Destroy forever, so it stayed in the tree for the life of the node (T-0142).
	/// Cost of never reusing: ~250k ids/h per node at t0141 churn against a 1,048,576-id block.
	/// </remarks>
	public new void Return(uint id)
	{
	}

	private void Add(NetworkIdBlock block)
	{
		if (block.Start == 0 || block.End < block.Start || m_blocks.Exists(x => block.Start <= x.End && x.Start <= block.End))
			throw new InvalidOperationException("Invalid or overlapping NetworkId block");
		m_blocks.Add(block);
	}
}

public readonly struct NetworkIdBlock
{
	public readonly uint Start;
	public readonly uint End;
	public NetworkIdBlock(uint start, uint end) { Start = start; End = end; }
}
