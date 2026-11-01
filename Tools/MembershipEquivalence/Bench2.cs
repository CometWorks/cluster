using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

// The shape T-0101 measured on the live node: partition 30262, the whole Earth ground site - 406 members, every one
// inside the sync distance of the next - plus scattered traffic, on a node of `total` entities.
internal static class Bench2
{
	public static void Run()
	{
		const double D = 3000, M = 300;
		// The shape the live node actually has, from bin/t0097-sample: ~1290 entities in 39-47
		// PARTITIONS, not one partition per entity - the traffic is Visitor ships that arrive in
		// convoys, so a node holds a few dozen layouts of wildly different sizes, one of which is the
		// whole Earth ground site (partition 30262, 406 members, every one inside the sync distance
		// of the next). An earlier version of this benchmark gave every entity its own partition,
		// which made the merge pass look like the dominant cost; at 40 layouts it is 780 pairs.
		foreach ((int total, int layouts, int clump, int arriving) in
			new[] { (1290, 40, 406, 0), (1290, 40, 406, 25), (965, 40, 300, 0), (388, 40, 120, 0) })
		{
			Random rng = new Random(11);
			var bodies = new List<(long Id, double X, double Y, double Z, double R)>();
			for (int i = 0; i < clump; i++)                       // the ground site
				bodies.Add((100 + i, rng.NextDouble() * 900, rng.NextDouble() * 900, rng.NextDouble() * 900, rng.NextDouble() * 30));
			int rest = total - clump, groups = Math.Max(1, layouts - 1), placed = clump;
			for (int g = 0; g < groups; g++)                      // the other layouts, each a tight convoy
			{
				int size = g == groups - 1 ? total - placed : Math.Max(1, rest / groups);
				double cx = rng.NextDouble() * 800000 - 400000, cy = rng.NextDouble() * 800000 - 400000, cz = rng.NextDouble() * 800000 - 400000;
				for (int k = 0; k < size && placed < total; k++, placed++)
					bodies.Add((100 + placed, cx + rng.NextDouble() * 1500, cy + rng.NextDouble() * 1500, cz + rng.NextDouble() * 1500,
						rng.NextDouble() * 30));
			}

			ulong nb = 1, no = 1;
			var b = new Reference.PartitionMembership(D, M, c => Enumerable.Range(0, c).Select(_ => nb++).ToArray());
			var o = new ClusterPlugin.PartitionMembership(D, M, c => Enumerable.Range(0, c).Select(_ => no++).ToArray());
			var inB = bodies.Select(x => new Reference.PartitionMembership.Member(x.Id, x.X, x.Y, x.Z, x.R, 0, 0)).ToArray();
			var inO = bodies.Select(x => new ClusterPlugin.PartitionMembership.Member(x.Id, x.X, x.Y, x.Z, x.R, 0, 0)).ToArray();
			var seedB = inB.Take(total - arriving).ToArray();
			var seedO = inO.Take(total - arriving).ToArray();
			b.Evaluate(seedB, 100); o.Evaluate(seedO, 100);
			// Warm up past tiered JIT before timing anything. Two calls is not enough: the first timed
			// case used to fall monotonically 3.9 -> 0.85 ms across its rounds while the later cases were
			// flat, which reads as bimodality if you only look at the median.
			for (int i = 0; i < 120; i++) { b.Evaluate(inB, 110 + i); o.Evaluate(inO, 110 + i); }

			// Interleaved A/B rounds with a settled heap, reported as medians. A straight
			// "reference N times then live N times" is not safe at these magnitudes: the reference
			// allocates much more, so a gen0 collection lands in a different phase for each side and
			// sub-millisecond phase readings swing by 2-3x on GC placement alone.
			const int reps = 20, rounds = 9;
			double[] refRound = new double[rounds], liveRound = new double[rounds];
			for (int round = 0; round < rounds; round++)
			{
				GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
				Stopwatch sw = Stopwatch.StartNew();
				for (int i = 0; i < reps; i++) b.Evaluate(inB, 200 + i);
				refRound[round] = sw.Elapsed.TotalMilliseconds / reps;
				GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
				sw.Restart();
				for (int i = 0; i < reps; i++) o.Evaluate(inO, 200 + i);
				liveRound[round] = sw.Elapsed.TotalMilliseconds / reps;
			}
			// Allocation is the GC-independent half of the story, and the medians above deliberately
			// exclude collection cost by settling the heap between rounds - so they UNDERSTATE the win.
			long refBytes = -GC.GetAllocatedBytesForCurrentThread();
			b.Evaluate(inB, 300);
			refBytes += GC.GetAllocatedBytesForCurrentThread();
			long liveBytes = -GC.GetAllocatedBytesForCurrentThread();
			o.Evaluate(inO, 300);
			liveBytes += GC.GetAllocatedBytesForCurrentThread();
			if (Environment.GetEnvironmentVariable("BENCH_ROUNDS") != null)
				Console.WriteLine("   rounds live: " + string.Join(" ", liveRound.Select(x => x.ToString("F3")))
					+ "   ref: " + string.Join(" ", refRound.Select(x => x.ToString("F3"))));
			Array.Sort(refRound); Array.Sort(liveRound);
			double bms = refRound[rounds / 2], oms = liveRound[rounds / 2];
			Console.WriteLine($"entities={total,5} layouts={o.Layouts.Count,4} clump={clump,4} arriving={arriving,3}   "
				+ $"reference {bms,7:F3} ms   live {oms,7:F3} ms   {bms / oms,5:F2}x"
				+ $"   [at 4 passes/s: {bms * 4 / 10,5:F2}% -> {oms * 4 / 10,5:F2}% of wall]"
				+ $"   alloc {refBytes / 1024.0,7:F0} KB -> {liveBytes / 1024.0,6:F0} KB per pass");
		}
	}
}
