using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// T-0401: NetworkIdRangeGenerator.NextId takes ids lock-free below the 75 % prefetch point (or the block end once
	/// the next block is held) and the lock at those boundaries only. (1) Single-threaded it issues exactly what the
	/// locked r119 NextId (<see cref="NetworkIdRangeGeneratorLocked"/>, kept verbatim below) issues - same ids, same
	/// allocator calls, same exceptions - over scripted runs with prefetch failures, an unreachable registry, the 5 s
	/// backoff clock and PrefetchNow calls. (2) 8 threads x 200k NextId over 1000-id blocks: every id unique, every
	/// block but the last fully used, the last used contiguously from its start, nothing drawn from a prefetched block
	/// before its predecessor ran out, and the allocator called once per block boundary plus the final prefetch.
	/// </summary>
	private static void NetworkIdLockFree()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("network-id-lockfree: " + message); }

		// (1) equivalence with the locked generator, single-threaded.
		foreach (int seed in Enumerable.Range(1, 12))
		{
			string a = ScriptedRun(seed, locked: true), b = ScriptedRun(seed, locked: false);
			Check(a == b, $"seed {seed}: lock-free run differs from the locked one\n locked: {a[..Math.Min(400, a.Length)]}\n free:   {b[..Math.Min(400, b.Length)]}");
		}

		// (2) multi-threaded stress.
		const int Threads = 8, PerThread = 200_000, Size = 1000;
		List<NetworkIdBlock> allocated = new List<NetworkIdBlock>();
		object allocLock = new object();
		NetworkIdBlock Block(int k) => new NetworkIdBlock((uint)(k * 1500 + 1), (uint)(k * 1500 + Size));
		NetworkIdRangeGenerator generator = new NetworkIdRangeGenerator(Block(0), () =>
		{
			lock (allocLock)
			{
				NetworkIdBlock block = Block(allocated.Count + 1);
				allocated.Add(block);
				return block;
			}
		});
		uint[][] drawn = new uint[Threads][];
		using (Barrier start = new Barrier(Threads))
		{
			Thread[] threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
			{
				uint[] mine = new uint[PerThread];
				start.SignalAndWait();
				for (int i = 0; i < PerThread; i++) mine[i] = generator.NextId();
				drawn[t] = mine;
			}) { IsBackground = true }).ToArray();
			foreach (Thread thread in threads) thread.Start();
			foreach (Thread thread in threads) thread.Join();
		}
		foreach (uint[] mine in drawn)
			for (int i = 1; i < mine.Length; i++)
				Check(mine[i] > mine[i - 1], "one thread's ids are increasing (blocks are issued in order)");
		uint[] all = drawn.SelectMany(x => x).OrderBy(x => x).ToArray();
		int duplicates = 0;
		for (int i = 1; i < all.Length; i++) if (all[i] == all[i - 1]) duplicates++;
		Check(duplicates == 0, duplicates + " duplicate ids");
		List<NetworkIdBlock> blocks = new List<NetworkIdBlock> { Block(0) };
		blocks.AddRange(allocated);
		int used = (Threads * PerThread + Size - 1) / Size;
		Dictionary<int, int> perBlock = all.GroupBy(id => (int)((id - 1) / 1500)).ToDictionary(g => g.Key, g => g.Count());
		for (int k = 0; k < used - 1; k++)
			Check(perBlock.GetValueOrDefault(k) == Size, $"block {k} fully used ({perBlock.GetValueOrDefault(k)} of {Size})");
		int lastCount = Threads * PerThread - (used - 1) * Size;
		uint lastStart = Block(used - 1).Start;
		Check(all.Where(id => id >= lastStart).SequenceEqual(Enumerable.Range(0, lastCount).Select(i => (uint)(lastStart + i))),
			"the last block is used contiguously from its start");
		Check(perBlock.Keys.All(k => k < used), "no id from a block beyond the last one needed");
		int expectedAllocations = (used - 1) + (lastCount * 4 >= Size * 3 ? 1 : 0);
		Check(allocated.Count == expectedAllocations, $"allocator calls {allocated.Count}, expected {expectedAllocations} (one per boundary + the prefetch)");
	}

	/// <summary>A deterministic run of NextId / PrefetchNow over a scripted allocator, registry and clock; the transcript.</summary>
	private static string ScriptedRun(int seed, bool locked)
	{
		Random random = new Random(seed);
		long clock = 0;
		bool unreachable = false;
		int calls = 0;
		uint nextStart = 1;
		double failRate = random.NextDouble() * 0.4;
		int size = random.Next(1, 40);
		Func<NetworkIdBlock> allocate = () =>
		{
			calls++;
			if (random.NextDouble() < failRate) throw new InvalidOperationException("registry unavailable");
			nextStart += (uint)random.Next(size + 1, 3 * size + 2);
			return new NetworkIdBlock(nextStart, nextStart + (uint)size - 1);
		};
		NetworkIdBlock first = new NetworkIdBlock(1, (uint)size);
		nextStart = (uint)size;
		NetworkIdRangeGenerator free = locked ? null : new NetworkIdRangeGenerator(first, allocate, () => unreachable, () => clock);
		NetworkIdRangeGeneratorLocked reference = locked ? new NetworkIdRangeGeneratorLocked(first, allocate, () => unreachable, () => clock) : null;
		System.Text.StringBuilder log = new System.Text.StringBuilder();
		{
			for (int step = 0; step < 600; step++)
			{
				double roll = random.NextDouble();
				if (roll < 0.03) unreachable = !unreachable;
				else if (roll < 0.08) clock += random.Next(0, 7000);
				try
				{
					if (roll > 0.98)
					{
						NetworkIdBlock? got = locked ? reference.PrefetchNow() : free.PrefetchNow();
						log.Append('P').Append(got?.Start.ToString() ?? "-").Append(' ');
					}
					else log.Append(locked ? reference.NextId() : free.NextId()).Append(' ');
				}
				catch (Exception error) { log.Append('!').Append(error.GetType().Name).Append(' '); }
			}
		}
		return log.Append("calls=").Append(calls).ToString();
	}

	/// <summary>
	/// Offline micro-benchmark (not a section; tools call it by reflection): ns per NextId of the r119 locked generator
	/// and the lock-free one, `threads` threads x `perThread` calls over 1M-id blocks (the live block is 64k-1M).
	/// </summary>
	internal static (double LockedNs, double FreeNs) NetworkIdBenchmark(int threads, int perThread)
	{
		uint start = 1;
		Func<NetworkIdBlock> allocate = () => { start += 2_000_000; return new NetworkIdBlock(start, start + 999_999); };
		double Run(Func<uint> next)
		{
			using Barrier barrier = new Barrier(threads + 1);
			Thread[] workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
			{
				barrier.SignalAndWait();
				uint sink = 0;
				for (int i = 0; i < perThread; i++) sink ^= next();
				GC.KeepAlive(sink);
				barrier.SignalAndWait();
			}) { IsBackground = true }).ToArray();
			foreach (Thread worker in workers) worker.Start();
			barrier.SignalAndWait();
			Stopwatch watch = Stopwatch.StartNew();
			barrier.SignalAndWait();
			return watch.Elapsed.TotalMilliseconds * 1e6 / ((double)threads * perThread);
		}
		NetworkIdRangeGeneratorLocked locked = new NetworkIdRangeGeneratorLocked(new NetworkIdBlock(1, 1_000_000), allocate);
		NetworkIdRangeGenerator free = new NetworkIdRangeGenerator(new NetworkIdBlock(1, 1_000_000), allocate);
		Run(locked.NextId); Run(free.NextId); // warm-up (JIT)
		return (Run(locked.NextId), Run(free.NextId));
	}
}

/// <summary>The r119 (912c613) NetworkIdRangeGenerator.NextId/PrefetchNow verbatim, the reference for T-0401's lock-free one.</summary>
internal sealed class NetworkIdRangeGeneratorLocked
{
	private readonly object m_lock = new object();
	private readonly Func<NetworkIdBlock> m_allocate;
	private readonly List<NetworkIdBlock> m_blocks = new List<NetworkIdBlock>();
	private uint m_next;
	private bool m_prefetched;
	private const long PrefetchBackoffMilliseconds = 5000;
	private readonly Func<bool> m_registryUnreachable;
	private readonly Func<long> m_clock;
	private long m_prefetchRetryAfter = long.MinValue;

	public NetworkIdRangeGeneratorLocked(NetworkIdBlock initial, Func<NetworkIdBlock> allocate, Func<bool> registryUnreachable = null, Func<long> clock = null)
	{
		m_allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));
		m_registryUnreachable = registryUnreachable ?? (() => false);
		m_clock = clock ?? (() => Environment.TickCount64);
		Add(initial);
		m_next = initial.Start;
	}

	public uint NextId()
	{
		lock (m_lock)
		{
			NetworkIdBlock current = m_blocks[0];
			if (!m_prefetched && (ulong)(m_next - current.Start) * 4 >= (ulong)(current.End - current.Start + 1) * 3
				&& !m_registryUnreachable() && m_clock() >= m_prefetchRetryAfter)
			{
				try
				{
					Add(m_allocate());
					m_prefetched = true;
				}
				catch (Exception)
				{
					m_prefetchRetryAfter = m_clock() + PrefetchBackoffMilliseconds;   // (the r119 log line is left out of the reference)
				}
			}
			if (m_next > current.End)
			{
				if (m_blocks.Count == 1)
				{
					if (m_registryUnreachable()) throw new System.Net.Http.HttpRequestException("Registry unreachable: no network id block");
					Add(m_allocate());
				}
				m_blocks.RemoveAt(0);
				m_next = m_blocks[0].Start;
				m_prefetched = m_blocks.Count > 1;
				m_prefetchRetryAfter = long.MinValue;
			}
			return m_next++;
		}
	}

	public NetworkIdBlock? PrefetchNow()
	{
		lock (m_lock)
		{
			if (m_blocks.Count > 1)
				return null;
			NetworkIdBlock block = m_allocate();
			Add(block);
			m_prefetched = true;
			return block;
		}
	}

	private void Add(NetworkIdBlock block)
	{
		if (block.Start == 0 || block.End < block.Start || m_blocks.Exists(x => block.Start <= x.End && x.Start <= block.End))
			throw new InvalidOperationException("Invalid or overlapping NetworkId block");
		m_blocks.Add(block);
	}
}
