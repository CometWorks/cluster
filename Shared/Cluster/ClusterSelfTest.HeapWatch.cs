using System;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// SE-1382: one dump when the heap first passes the dump mark, exit past the limit, 0 turns either off.
	private static void HeapWatchDecide()
	{
		void Expect(HeapWatch.Step expected, long heapMb, int dumpMb, int limitMb, bool dumped)
		{
			HeapWatch.Step step = HeapWatch.Decide(heapMb, dumpMb, limitMb, dumped);
			if (step != expected) throw new InvalidOperationException($"heap-watch: {heapMb} MB, dump {dumpMb}, limit {limitMb}, dumped {dumped}: {step}");
		}
		Expect(HeapWatch.Step.None, 1500, 6144, 24576, false);
		Expect(HeapWatch.Step.Dump, 6144, 6144, 24576, false);
		Expect(HeapWatch.Step.None, 9000, 6144, 24576, true);
		Expect(HeapWatch.Step.Exit, 24576, 6144, 24576, true);
		Expect(HeapWatch.Step.Exit, 30000, 6144, 24576, false);
		Expect(HeapWatch.Step.None, 30000, 0, 0, false);
		Expect(HeapWatch.Step.Dump, 30000, 6144, 0, false);
	}
}
