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
            if (step != expected)
                throw new InvalidOperationException(
                    $"heap-watch: {heapMb} MB, dump {dumpMb}, limit {limitMb}, dumped {dumped}: {step}"
                );
        }
        Expect(HeapWatch.Step.None, 1500, 6144, 16384, false);
        Expect(HeapWatch.Step.Dump, 6144, 6144, 16384, false);
        Expect(HeapWatch.Step.None, 9000, 6144, 16384, true);
        Expect(HeapWatch.Step.Exit, 16384, 6144, 16384, true);
        Expect(HeapWatch.Step.Exit, 20000, 6144, 16384, false);
        Expect(HeapWatch.Step.None, 20000, 0, 0, false);
        Expect(HeapWatch.Step.Dump, 20000, 6144, 0, false);
        // SE-1561: the facts line names the collector's numbers and the node link's queues, with and without a collection.
        foreach (bool collect in new[] { false, true })
        {
            string facts = HeapWatch.Facts(collect);
            foreach (
                string part in new[] { "heap=", "gen2=", "loh=", "collections=", "linkQueue=" }
            )
                if (!facts.Contains(part))
                    throw new InvalidOperationException(
                        "heap-watch: facts lack " + part + ": " + facts
                    );
            if (facts.Contains(" live=") != collect)
                throw new InvalidOperationException(
                    "heap-watch: live only after a collection: " + facts
                );
        }
    }
}
