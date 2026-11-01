using System;
using System.Threading;

namespace ClusterPlugin;

/// <summary>
/// Registry-issued sequential EntityId ranges (Docs/Gateway/EntityIdRanges.md).
/// The 56-bit unique number is rangeIndex(32) &lt;&lt; 24 | offset(24); the registry issues every
/// index exactly once and never issues 0, this class hands out every offset exactly once, so
/// ids are unique across the whole cluster by construction with no collision check anywhere.
///
/// Fast path is lock-free: one Interlocked.Add per reservation. Crossing the end of the range
/// takes a lock, draws the next range from the registry (at most once per 2^24 ids) and
/// abandons the offsets reserved past the end - gaps are free. A range is prefetched at 90 %
/// so the swap normally never waits on the registry from the game thread.
/// </summary>
public static class EntityIdAllocator
{
	public const int RangeBits = 24;
	public const long UniqueMask = 0xFFFFFFFFFFFFFFL;

	private sealed class Range
	{
		public readonly long Start;
		public readonly long End;
		public long Next;
		public Range(long start, long end) { Start = start; End = end; Next = start; }
		public uint Index => (uint)(Start >> RangeBits);
	}

	private static readonly object m_lock = new object();
	private static Range m_current;
	private static Range m_prefetched;
	private static Func<(long Start, long End)> m_fetch;
	private static Thread m_prefetching;

	/// <summary>True once a range has been installed; until then the vanilla allocator runs.</summary>
	public static bool Enabled => m_current != null;

	/// <summary>Index of the range ids are currently drawn from (0 when not enabled).</summary>
	public static uint CurrentRangeIndex => m_current?.Index ?? 0;

	/// <summary>
	/// Diagnostic view of the allocator for the node's /diag section (cluster-status shows it):
	/// the range ids are drawn from, how far it is consumed, and whether the next one is already
	/// in hand. Null while not enabled. Reads are racy by design - it is a snapshot, not a reservation.
	/// </summary>
	public static object Snapshot()
	{
		Range current = Volatile.Read(ref m_current);
		if (current == null) return null;
		Range prefetched;
		bool prefetching;
		lock (m_lock)
		{
			prefetched = m_prefetched;
			prefetching = m_prefetching != null;
		}
		long next = Math.Min(Volatile.Read(ref current.Next), current.End + 1);
		return new
		{
			index = current.Index,
			start = current.Start,
			end = current.End,
			next,
			used = next - current.Start,
			remaining = current.End + 1 - next,
			prefetchedIndex = prefetched?.Index,
			prefetching
		};
	}

	/// <summary>Installs the first range and the fetcher for the following ones.</summary>
	public static void Configure(long start, long end, Func<(long Start, long End)> fetch)
	{
		lock (m_lock)
		{
			m_fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
			m_prefetched = null;
			m_current = Validate(start, end);
		}
	}

	/// <summary>Test/standalone reset: back to vanilla allocation.</summary>
	public static void Reset()
	{
		lock (m_lock)
		{
			m_current = null;
			m_prefetched = null;
			m_fetch = null;
		}
	}

	/// <summary>One new 56-bit unique number.</summary>
	public static long NextId() => NextIds(1);

	/// <summary>
	/// Reserves <paramref name="count"/> consecutive unique numbers and returns the first; the caller
	/// owns first .. first+count-1. Tight loops (blueprint remapping) take 100 at a time and drop the
	/// unused tail, which removes ~99 % of the traffic through this allocator.
	/// </summary>
	public static long NextIds(int count)
	{
		if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
		while (true)
		{
			Range range = Volatile.Read(ref m_current) ?? throw new InvalidOperationException("EntityIdAllocator is not configured");
			long first = Interlocked.Add(ref range.Next, count) - count;
			long last = first + count - 1;
			if (last <= range.End)
			{
				if (last - range.Start >= (range.End - range.Start) / 10 * 9) Prefetch(range);
				return first;
			}
			Advance(range);
		}
	}

	private static void Prefetch(Range range)
	{
		lock (m_lock)
		{
			if (m_current != range || m_prefetched != null || m_prefetching != null) return;
			Func<(long, long)> fetch = m_fetch;
			m_prefetching = new Thread(() =>
			{
				try
				{
					(long start, long end) = fetch();
					Range next = Validate(start, end);
					lock (m_lock) m_prefetched = next;
				}
				catch (Exception exception)
				{
					// Not fatal: Advance fetches synchronously when the prefetch is missing.
					Console.WriteLine("[ClusterPlugin] entity_id_range prefetch failed: " + exception.Message);
				}
				finally
				{
					lock (m_lock) m_prefetching = null;
				}
			}) { IsBackground = true, Name = "ClusterEntityIdRangePrefetch" };
			m_prefetching.Start();
		}
	}

	private static void Advance(Range exhausted)
	{
		lock (m_lock)
		{
			if (m_current != exhausted) return;
			Range next = m_prefetched;
			m_prefetched = null;
			if (next == null)
			{
				// T-0350: this is the game thread with no prefetched range. The fetch would throw after its 10 s
				// timeout; while the registry is known to be unreachable it throws now instead.
				if (ClusterRuntime.RegistryUnreachable) throw new InvalidOperationException("Registry unreachable: no entity id range");
				(long start, long end) = m_fetch();
				next = Validate(start, end);
			}
			if (next.Start <= exhausted.End)
				throw new InvalidOperationException("EntityId range went backwards: " + next.Start + " after " + exhausted.End);
			Volatile.Write(ref m_current, next);
		}
	}

	private static Range Validate(long start, long end)
	{
		if (start <= 0 || end < start || (end & ~UniqueMask) != 0 || (start & ((1L << RangeBits) - 1)) != 0)
			throw new InvalidOperationException("Invalid EntityId range " + start + ".." + end);
		return new Range(start, end);
	}
}
