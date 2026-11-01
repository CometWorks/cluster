using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

// Differential test: the baseline PartitionMembership (git HEAD) and the optimized one must be
// observably identical - same layouts, same ids, same Change list including the Why strings -
// over randomized worlds, pass after pass, with the same allocator sequence.
internal static class Program
{
	private const double Distance = 3000, Margin = 300;

	private sealed class World
	{
		public List<(long Id, double X, double Y, double Z, double R, double Speed, long Group)> Bodies = new();
	}

	private static string Dump(IReadOnlyList<Reference.PartitionMembership.Layout> layouts,
		Reference.PartitionMembership.Change[] changes)
	{
		StringBuilder sb = new StringBuilder();
		foreach (var l in layouts)
			sb.Append(l.Id).Append(':').Append(string.Join(",", l.Members)).Append('@')
			  .Append(l.CreatedAt.ToString("R", CultureInfo.InvariantCulture)).Append("/p[")
			  .Append(string.Join(",", l.Pending)).Append("]u")
			  .Append(l.PinnedUntil.ToString("R", CultureInfo.InvariantCulture)).Append(" | ");
		sb.Append(" ## ");
		foreach (var c in changes)
			sb.Append(c.Split ? 'S' : 'M').Append('[').Append(string.Join(",", c.Inputs)).Append("]->[")
			  .Append(string.Join(";", c.Outputs.Select(o => o.Id + ":" + string.Join(",", o.Members))))
			  .Append("] \"").Append(c.Why).Append("\" | ");
		return sb.ToString();
	}

	private static string Dump(IReadOnlyList<ClusterPlugin.PartitionMembership.Layout> layouts,
		ClusterPlugin.PartitionMembership.Change[] changes)
	{
		StringBuilder sb = new StringBuilder();
		foreach (var l in layouts)
			sb.Append(l.Id).Append(':').Append(string.Join(",", l.Members)).Append('@')
			  .Append(l.CreatedAt.ToString("R", CultureInfo.InvariantCulture)).Append("/p[")
			  .Append(string.Join(",", l.Pending)).Append("]u")
			  .Append(l.PinnedUntil.ToString("R", CultureInfo.InvariantCulture)).Append(" | ");
		sb.Append(" ## ");
		foreach (var c in changes)
			sb.Append(c.Split ? 'S' : 'M').Append('[').Append(string.Join(",", c.Inputs)).Append("]->[")
			  .Append(string.Join(";", c.Outputs.Select(o => o.Id + ":" + string.Join(",", o.Members))))
			  .Append("] \"").Append(c.Why).Append("\" | ");
		return sb.ToString();
	}

	private static World MakeWorld(Random rng, int shape, int n)
	{
		World w = new World();
		int clumps = shape switch { 0 => 1, 1 => Math.Max(1, n / 8), 2 => 3, 3 => n, 6 => n, 7 => n, _ => 2 + rng.Next(5) };
		var centres = Enumerable.Range(0, clumps)
			.Select(_ => (rng.NextDouble() * 60000 - 30000, rng.NextDouble() * 60000 - 30000, rng.NextDouble() * 60000 - 30000))
			.ToArray();
		double spread = shape switch { 0 => 900, 1 => 1200, 2 => 4000, 3 => 40000, 6 => 6000, 7 => 8000, _ => 2500 };
		for (int i = 0; i < n; i++)
		{
			var c = centres[rng.Next(clumps)];
			double r = shape == 5 ? rng.NextDouble() * 2500 : (rng.Next(10) == 0 ? rng.NextDouble() * 400 : rng.NextDouble() * 30);
			long group = rng.Next(6) == 0 ? 1 + rng.Next(4) : 0;
			w.Bodies.Add((100 + i,
				c.Item1 + (rng.NextDouble() - 0.5) * spread,
				c.Item2 + (rng.NextDouble() - 0.5) * spread,
				c.Item3 + (rng.NextDouble() - 0.5) * spread,
				r, rng.NextDouble() * 100, group));
		}
		if (shape == 7)
			for (int i = 0; i < w.Bodies.Count; i++)
			{
				var t = w.Bodies[i];
				w.Bodies[i] = (t.Id, t.X, t.Y, t.Z, i == 0 ? 5000 : t.R, t.Speed, 1 + (i % Math.Max(1, w.Bodies.Count / 3)));
			}
		// deliberate knife-edge pairs: exactly at, just under and just over the distance
		if (n >= 4 && shape != 3)
		{
			var b = w.Bodies[0];
			for (int k = 1; k <= 3 && k < w.Bodies.Count; k++)
			{
				double d = Distance + (k - 2) * 1e-9 + b.R + w.Bodies[k].R;
				var t = w.Bodies[k];
				w.Bodies[k] = (t.Id, b.X + d, b.Y, b.Z, t.R, t.Speed, t.Group);
			}
		}
		return w;
	}

	private static void Jitter(World w, Random rng, double scale)
	{
		for (int i = 0; i < w.Bodies.Count; i++)
		{
			var b = w.Bodies[i];
			w.Bodies[i] = (b.Id, b.X + (rng.NextDouble() - 0.5) * scale, b.Y + (rng.NextDouble() - 0.5) * scale,
				b.Z + (rng.NextDouble() - 0.5) * scale, b.R, b.Speed, b.Group);
		}
	}

	/// Components is private and cannot be reached through the public surface: Evaluate builds its
	/// member dictionary with ToDictionary, which throws on a repeated id, and Adopt calls Distinct.
	/// The duplicate handling inside it is defensive, so it is exercised by reflection or not at all.
	private static string Components(Type owner, long[] ids, System.Collections.IDictionary members)
	{
		MethodInfo method = owner.GetMethod("Components", BindingFlags.NonPublic | BindingFlags.Static);
		Type member = owner.GetNestedType("Member", BindingFlags.NonPublic | BindingFlags.Public);
		object typed = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(long), member));
		foreach (System.Collections.DictionaryEntry entry in members)
			typed.GetType().GetMethod("Add").Invoke(typed, new[] { entry.Key, entry.Value });
		object result = method.Invoke(null, new object[] { ids, typed, 3000.0 });
		return string.Join(" | ", ((System.Collections.IEnumerable)result).Cast<long[]>().Select(c => string.Join(",", c)));
	}

	private static int Main(string[] args)
	{
		if (args.Length > 0 && args[0] == "bench") return Bench();
		if (args.Length > 0 && args[0] == "bench2") { Bench2.Run(); return 0; }
		if (args.Length > 0 && args[0] == "flap") return Flap();
		if (args.Length > 0 && args[0] == "apply")
		{
			// T-0354: Shared/Cluster/RegistryApplyScenarios.cs; throws on the first failed check.
			Console.WriteLine("apply: all scenarios pass: " + string.Join(", ", ClusterPlugin.RegistryApplyScenarios.Run()));
			return 0;
		}
		int seeds = args.Length > 0 ? int.Parse(args[0]) : 400;
		int mismatches = 0, passes = 0;
		int splits = 0, merges = 0, atomicMerges = 0, adoptions = 0, maxLayouts = 0, pinnedPasses = 0, refusals = 0;
		// T-0339 (design C): a third membership with transfers on, driven through the same worlds. Until its first transfer
		// it must be the baseline exactly (a transfer is the ONLY intended difference); from then on it runs on and must keep
		// every census member in exactly one layout, and every transfer must have the shape the registry applies.
		int transfers = 0, transferSeeds = 0;
		// T-0339 (design C2): a fourth membership with the transfer band on as well. Until its first band transfer it must be
		// the transfer arm exactly (the band adds candidates and nothing else); from then on the same invariants hold.
		int bandTransfers = 0, bandSeeds = 0;
		long allocatedBand = 0;
		// T-0339 (merge hold-off): a fifth membership, the band arm with partition.mergeHoldoffSeconds = 30. Until its first
		// held merge it must be the band arm exactly (the hold-off only skips merges); from then on the same invariants hold.
		int heldPasses = 0, holdoffSeeds = 0;
		long heldPairs = 0;
		// T-0339 (merge hold-off, r118 gate): the hold-off never suppresses a split or a transfer. Before every pass a knob-0
		// shadow of the band arm's flags is stood on the hold-off arm's layouts (same locks, same allocator position): the
		// split and transfer changes of the two must be identical on EVERY pass, after a held merge too, and a pass that holds
		// nothing must be identical altogether (MembershipFlapping.HoldoffFault). The sequence of split and transfer changes
		// over a whole seed equals the band arm's only while no merge has been held - a held merge leaves the returning piece
		// as a layout of its own, and its next departure is then a split of that piece (or no split at all) where the band
		// arm splits the merged layout: the same members leave under other input ids. So: equal sequences for every seed
		// that never held a merge (asserted), and the shadow invariant on every pass of every seed (asserted); the seeds
		// whose later sequence a hold changed are counted.
		int shadowPasses = 0, seedsSequenceChangedByAHold = 0, seedsSequenceEqualWithoutAHold = 0;
		// T-0339 (split hysteresis): a sixth membership, the band arm with partition.splitHysteresisSeconds = 60. Until its first
		// hold it must be the band arm exactly; on every pass a knob-0 shadow stood on its layouts must make the same pass when
		// nothing is held and the same transfers when something is (MembershipFlapping.HysteresisFault); every pass keeps every
		// census member in exactly one layout.
		int hysteresisPasses = 0, hysteresisSeeds = 0;
		long hysteresisRuns = 0, hysteresisTransferred = 0, hysteresisExpired = 0, hysteresisReleased = 0;
		long allocatedBaseline = 0, allocatedTransfer = 0;
		for (int seed = 0; seed < seeds; seed++)
		{
			Random rng = new Random(seed);
			int shape = seed % 8;
			int n = 1 + rng.Next(seed % 20 == 0 ? 260 : 45);
			World world = MakeWorld(rng, shape, n);

			ulong nb = 1, no = 1;
			HashSet<ulong> lockedB = new(), lockedO = new();
			var baseline = new Reference.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => nb++).ToArray(), id => lockedB.Contains(id));
			var opt = new ClusterPlugin.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => no++).ToArray(), id => lockedO.Contains(id));
			ulong nt = 1;
			HashSet<ulong> lockedT = new();
			var tr = new ClusterPlugin.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => nt++).ToArray(), id => lockedT.Contains(id)) { TransferEnabled = true };
			bool diverged = false;
			ulong nbd = 1;
			HashSet<ulong> lockedD = new();
			var bd = new ClusterPlugin.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => nbd++).ToArray(), id => lockedD.Contains(id)) { TransferEnabled = true, TransferBand = true };
			bool bandDiverged = false;
			ulong nho = 1;
			HashSet<ulong> lockedH = new();
			var ho = new ClusterPlugin.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => nho++).ToArray(), id => lockedH.Contains(id))
				{ TransferEnabled = true, TransferBand = true, MergeHoldoffSeconds = 30 };
			bool holdoffDiverged = false;
			ulong nhy = 1;
			var hy = new ClusterPlugin.PartitionMembership(Distance, Margin,
				c => Enumerable.Range(0, c).Select(_ => nhy++).ToArray(), id => lockedD.Contains(id))
				{ TransferEnabled = true, TransferBand = true, SplitHysteresisSeconds = 60 };
			bool hysteresisDiverged = false;
			List<string> splitsH = new(), splitsD = new();

			for (int pass = 0; pass < 14; pass++)
			{
				double now = 100 + pass * 7;
				// occasionally lock a layout, adopt a saved partition (with pending pins), or drop bodies
				if (pass == 3 && baseline.Layouts.Count > 1)
				{
					ulong lockId = baseline.Layouts[rng.Next(baseline.Layouts.Count)].Id;
					lockedB.Add(lockId); lockedO.Add(lockId); lockedT.Add(lockId); lockedD.Add(lockId); lockedH.Add(lockId);
				}
				if (pass == 5)
				{
					long[] some = world.Bodies.Take(Math.Max(1, world.Bodies.Count / 5)).Select(b => b.Id).ToArray();
					long[] ghosts = new long[] { 999001, 999002 };
					bool mature = rng.Next(2) == 0;   // ONE draw: both sides must be told the same thing
					baseline.Adopt(9000, some, now, mature, ghosts);
					opt.Adopt(9000, some, now, mature, ghosts);
					tr.Adopt(9000, some, now, mature, ghosts);
					bd.Adopt(9000, some, now, mature, ghosts);
					ho.Adopt(9000, some, now, mature, ghosts);
					hy.Adopt(9000, some, now, mature, ghosts);
				}
				if (pass == 8 && world.Bodies.Count > 3) world.Bodies.RemoveRange(0, 2);
				if (pass == 9) { lockedB.Clear(); lockedO.Clear(); lockedT.Clear(); lockedD.Clear(); lockedH.Clear(); }
				if (pass == 11 && world.Bodies.Count > 0)
				{
					baseline.Remove(baseline.Layouts[0].Id);
					opt.Remove(opt.Layouts[0].Id);
					if (tr.Layouts.Count > 0) tr.Remove(tr.Layouts[0].Id);
					if (bd.Layouts.Count > 0) bd.Remove(bd.Layouts[0].Id);
					if (ho.Layouts.Count > 0) ho.Remove(ho.Layouts[0].Id);
					if (hy.Layouts.Count > 0) hy.Remove(hy.Layouts[0].Id);
				}
				Jitter(world, rng, pass % 3 == 0 ? 2200 : 120);

				var inB = world.Bodies.Select(b => new Reference.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, b.Speed, b.Group)).ToArray();
				var inO = world.Bodies.Select(b => new ClusterPlugin.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, b.Speed, b.Group)).ToArray();
				// shuffle the input order identically for both: order must not matter
				int[] order = Enumerable.Range(0, inB.Length).OrderBy(_ => rng.Next()).ToArray();
				var cb = baseline.Evaluate(order.Select(i => inB[i]).ToArray(), now);
				var co = opt.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				var ct = tr.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				var cd = bd.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				ulong nhs = nho;
				var hs = new ClusterPlugin.PartitionMembership(Distance, Margin,
					c => Enumerable.Range(0, c).Select(_ => nhs++).ToArray(), id => lockedH.Contains(id)) { TransferEnabled = true, TransferBand = true };
				hs.Restore(ho.Layouts);
				var chs = hs.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				var ch = ho.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				ulong nhys = nhy;
				var hys = new ClusterPlugin.PartitionMembership(Distance, Margin,
					c => Enumerable.Range(0, c).Select(_ => nhys++).ToArray(), id => lockedD.Contains(id)) { TransferEnabled = true, TransferBand = true };
				hys.Restore(hy.Layouts);
				var chys = hys.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				var chy = hy.Evaluate(order.Select(i => inO[i]).ToArray(), now);
				string hysteresisFault = ClusterPlugin.MembershipFlapping.HysteresisFault(hy, chy, hys, chys) ?? TransferFault(hy, chy, inO.Select(x => x.Id));
				if (hysteresisFault != null)
				{
					mismatches++;
					Console.WriteLine($"HYSTERESIS FAULT seed={seed} shape={shape} n={n} pass={pass}: {hysteresisFault}");
				}
				if (hy.HysteresisHeld > 0) hysteresisPasses++;
				if (!hysteresisDiverged && Dump(hy.Layouts, chy) != Dump(bd.Layouts, cd))
				{
					hysteresisDiverged = true;
					if (hy.HysteresisHeld > 0) hysteresisSeeds++;
					else
					{
						mismatches++;
						Console.WriteLine($"HYSTERESIS ARM MISMATCH WITHOUT A HOLD seed={seed} shape={shape} n={n} pass={pass}");
						Console.WriteLine("  band      : " + Dump(bd.Layouts, cd));
						Console.WriteLine("  hysteresis: " + Dump(hy.Layouts, chy));
					}
				}
				passes++;
				shadowPasses++;
				string shadowFault = ClusterPlugin.MembershipFlapping.HoldoffFault(ho, ch, hs, chs);
				if (shadowFault != null)
				{
					mismatches++;
					Console.WriteLine($"HOLDOFF SHADOW FAULT seed={seed} shape={shape} n={n} pass={pass}: {shadowFault}");
				}
				splitsH.AddRange(ch.Where(c => c.Split).Select(ClusterPlugin.MembershipFlapping.ChangeKey));
				splitsD.AddRange(cd.Where(c => c.Split).Select(ClusterPlugin.MembershipFlapping.ChangeKey));
				string holdoffFault = TransferFault(ho, ch, inO.Select(x => x.Id));
				if (holdoffFault != null)
				{
					mismatches++;
					Console.WriteLine($"HOLDOFF FAULT seed={seed} shape={shape} n={n} pass={pass}: {holdoffFault}");
				}
				if (ho.HeldMerges > 0) heldPasses++;
				if (!holdoffDiverged && Dump(ho.Layouts, ch) != Dump(bd.Layouts, cd))
				{
					holdoffDiverged = true;
					if (ho.HeldMerges > 0) holdoffSeeds++;
					else
					{
						mismatches++;
						Console.WriteLine($"HOLDOFF ARM MISMATCH WITHOUT A HELD MERGE seed={seed} shape={shape} n={n} pass={pass}");
						Console.WriteLine("  band   : " + Dump(bd.Layouts, cd));
						Console.WriteLine("  holdoff: " + Dump(ho.Layouts, ch));
					}
				}
				string bandFault = TransferFault(bd, cd, inO.Select(x => x.Id));
				if (bandFault != null)
				{
					mismatches++;
					Console.WriteLine($"BAND FAULT seed={seed} shape={shape} n={n} pass={pass}: {bandFault}");
				}
				bandTransfers += cd.Count(c => c.Transfer && c.Why.Contains("transfer band"));
				if (!bandDiverged && Dump(bd.Layouts, cd) != Dump(tr.Layouts, ct))
				{
					bandDiverged = true;
					if (cd.Any(c => c.Transfer && c.Why.Contains("transfer band"))) bandSeeds++;
					else
					{
						mismatches++;
						Console.WriteLine($"BAND ARM MISMATCH WITHOUT A BAND TRANSFER seed={seed} shape={shape} n={n} pass={pass}");
						Console.WriteLine("  tr  : " + Dump(tr.Layouts, ct));
						Console.WriteLine("  band: " + Dump(bd.Layouts, cd));
					}
				}
				string transferFault = TransferFault(tr, ct, inO.Select(x => x.Id));
				if (transferFault != null)
				{
					mismatches++;
					Console.WriteLine($"TRANSFER FAULT seed={seed} shape={shape} n={n} pass={pass}: {transferFault}");
				}
				transfers += ct.Count(c => c.Transfer);
				if (!diverged && Dump(tr.Layouts, ct) != Dump(baseline.Layouts, cb))
				{
					if (ct.Any(c => c.Transfer)) { diverged = true; transferSeeds++; }
					else
					{
						mismatches++;
						Console.WriteLine($"TRANSFER ARM MISMATCH WITHOUT A TRANSFER seed={seed} shape={shape} n={n} pass={pass}");
						Console.WriteLine("  base: " + Dump(baseline.Layouts, cb));
						Console.WriteLine("  tr  : " + Dump(tr.Layouts, ct));
						diverged = true;
					}
				}
				foreach (var c in co)
				{
					if (c.Split) splits++;
					else { merges++; if (c.Why.Contains("atomic link")) atomicMerges++; }
				}
				maxLayouts = Math.Max(maxLayouts, opt.Layouts.Count);
				refusals += opt.RefusedMerges;
				if (opt.Layouts.Any(l => l.Pending.Length != 0)) pinnedPasses++;
				adoptions += opt.Layouts.Count;
				string db = Dump(baseline.Layouts, cb), doo = Dump(opt.Layouts, co);
				if (db != doo)
				{
					mismatches++;
					if (mismatches <= 3)
					{
						Console.WriteLine($"MISMATCH seed={seed} shape={shape} n={n} pass={pass}");
						Console.WriteLine("  base: " + db);
						Console.WriteLine("  opt : " + doo);
					}
					break;
				}
				if (nb != no) { Console.WriteLine($"ALLOCATOR DIVERGED seed={seed} pass={pass}: {nb} vs {no}"); mismatches++; break; }
			}
			allocatedBaseline += (long)nb - 1;
			allocatedTransfer += (long)nt - 1;
			allocatedBand += (long)nbd - 1;
			heldPairs += ho.HeldMergePairs;
			hysteresisRuns += hy.HysteresisHeldTotal;
			hysteresisTransferred += hy.HysteresisTransferred;
			hysteresisExpired += hy.HysteresisExpired;
			hysteresisReleased += hy.HysteresisReleased;
			if (ho.HeldMergePairs == 0)
			{
				if (splitsH.SequenceEqual(splitsD)) seedsSequenceEqualWithoutAHold++;
				else
				{
					mismatches++;
					Console.WriteLine($"HOLDOFF SPLIT SEQUENCE DIFFERS WITHOUT A HELD MERGE seed={seed} shape={shape} n={n}");
				}
			}
			else if (!splitsH.SequenceEqual(splitsD)) seedsSequenceChangedByAHold++;
		}
		Console.WriteLine($"transfer arm: transfers={transfers} seedsWithATransfer={transferSeeds} idsAllocated baseline={allocatedBaseline} transfer={allocatedTransfer}");
		Console.WriteLine($"band arm: bandTransfers={bandTransfers} seedsWithABandTransfer={bandSeeds} idsAllocated band={allocatedBand}");
		Console.WriteLine($"holdoff arm (30 s): passesWithAHeldMerge={heldPasses} heldPairs={heldPairs} seedsDivergedByAHold={holdoffSeeds}");
		Console.WriteLine($"holdoff shadow (knob 0 on the hold-off arm's layouts): passesChecked={shadowPasses} "
			+ $"seedsWithoutAHoldSplitSequenceEqual={seedsSequenceEqualWithoutAHold} seedsWhoseLaterSplitsAHoldChanged={seedsSequenceChangedByAHold}");
		Console.WriteLine($"hysteresis arm (60 s): passesWithAHold={hysteresisPasses} runs={hysteresisRuns} transferred={hysteresisTransferred} "
			+ $"expired={hysteresisExpired} released={hysteresisReleased} seedsDivergedByAHold={hysteresisSeeds}");
		Console.WriteLine($"coverage: splits={splits} merges={merges} atomicMerges={atomicMerges} coreRefusals={refusals} maxLayoutsInOnePass={maxLayouts} passesWithPins={pinnedPasses}");
		mismatches += LargeScale();
		mismatches += Degenerate();
		mismatches += Duplicates();
		Console.WriteLine($"seeds={seeds} passes={passes} mismatches={mismatches}");
		return mismatches == 0 ? 0 : 1;
	}

	/// T-0339 (design C): what must hold after every pass with transfers on, or null. Every census member is in exactly one
	/// layout, ids are unique, and a transfer is [source, target] -> [source without Moved, target with it], ids kept.
	private static string TransferFault(ClusterPlugin.PartitionMembership membership, ClusterPlugin.PartitionMembership.Change[] changes,
		IEnumerable<long> census)
	{
		var seen = new HashSet<long>();
		foreach (var layout in membership.Layouts)
			foreach (long member in layout.Members)
				if (!seen.Add(member)) return "member " + member + " in two layouts";
		if (membership.Layouts.Select(x => x.Id).Distinct().Count() != membership.Layouts.Count) return "two layouts share an id";
		foreach (long member in census)
			if (!seen.Contains(member)) return "census member " + member + " in no layout";
		foreach (var change in changes.Where(c => c.Transfer))
		{
			if (!change.Split || change.Inputs.Length != 2 || change.Outputs.Length != 2 || change.InputLayouts.Length != 2
				|| change.Outputs[0].Id != change.Inputs[0] || change.Outputs[1].Id != change.Inputs[1]
				|| change.InputLayouts[0].Id != change.Inputs[0] || change.InputLayouts[1].Id != change.Inputs[1])
				return "transfer of the wrong shape: " + string.Join(",", change.Inputs) + " -> " + string.Join(",", change.Outputs.Select(x => x.Id));
			if (!change.Moved.All(change.InputLayouts[0].Members.Contains)
				|| !change.Outputs[0].Members.SequenceEqual(change.InputLayouts[0].Members.Except(change.Moved).Order())
				|| !change.Outputs[1].Members.SequenceEqual(change.InputLayouts[1].Members.Concat(change.Moved).Order())
				|| change.Outputs[0].Members.Length == 0)
				return "transfer members do not add up: " + change.Why;
		}
		return null;
	}

	/// Hundreds of layouts, which is what the merge grid exists for: the exhaustive scan and the grid
	/// must pick the same pair, pass after pass, while entities drift in and out of the merge distance.
	private static int LargeScale()
	{
		int bad = 0;
		foreach ((int total, int clump, bool atomics, bool giant) in
			new[] { (700, 300, false, false), (700, 300, true, false), (900, 406, true, true), (1000, 0, true, false) })
		{
			Random rng = new Random(4242);
			var bodies = new List<(long Id, double X, double Y, double Z, double R, long G)>();
			for (int i = 0; i < clump; i++)
				bodies.Add((100 + i, rng.NextDouble() * 900, rng.NextDouble() * 900, rng.NextDouble() * 900, rng.NextDouble() * 30, 0));
			for (int i = clump; i < total; i++)
				bodies.Add((100 + i, rng.NextDouble() * 120000, rng.NextDouble() * 120000, rng.NextDouble() * 120000,
					giant && i == clump ? 4000 : rng.NextDouble() * 30,
					atomics && rng.Next(8) == 0 ? 1 + rng.Next(20) : 0));

			ulong nb = 1, no = 1;
			HashSet<ulong> lockedB = new(), lockedO = new();
			var b = new Reference.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => nb++).ToArray(), id => lockedB.Contains(id));
			var o = new ClusterPlugin.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => no++).ToArray(), id => lockedO.Contains(id));
			for (int pass = 0; pass < 8; pass++)
			{
				double now = 100 + pass * 7;
				if (pass == 2 && b.Layouts.Count > 5)
					for (int k = 0; k < 5; k++) { lockedB.Add(b.Layouts[k * 3].Id); lockedO.Add(o.Layouts[k * 3].Id); }
				if (pass == 5) { lockedB.Clear(); lockedO.Clear(); }
				for (int i = 0; i < bodies.Count; i++)      // drift: pulls scattered bodies through the merge distance
				{
					var t = bodies[i];
					bodies[i] = (t.Id, t.X + (rng.NextDouble() - 0.5) * 5000, t.Y + (rng.NextDouble() - 0.5) * 5000,
						t.Z + (rng.NextDouble() - 0.5) * 5000, t.R, t.G);
				}
				var cb = b.Evaluate(bodies.Select(x => new Reference.PartitionMembership.Member(x.Id, x.X, x.Y, x.Z, x.R, 0, x.G)).ToArray(), now);
				var co = o.Evaluate(bodies.Select(x => new ClusterPlugin.PartitionMembership.Member(x.Id, x.X, x.Y, x.Z, x.R, 0, x.G)).ToArray(), now);
				string db = Dump(b.Layouts, cb), doo = Dump(o.Layouts, co);
				if (db != doo)
				{
					Console.WriteLine($"LARGE MISMATCH total={total} clump={clump} atomics={atomics} giant={giant} pass={pass}");
					Console.WriteLine("  base: " + db.Substring(0, Math.Min(900, db.Length)));
					Console.WriteLine("  opt : " + doo.Substring(0, Math.Min(900, doo.Length)));
					bad++; break;
				}
				if (pass == 7)
					Console.WriteLine($"large-scale ok: total={total} clump={clump} atomics={atomics} giant={giant} "
						+ $"layouts={o.Layouts.Count} changesInLastPass={co.Length}");
			}
		}
		return bad;
	}

	/// The exhaustive fallback: zero distance and zero radii make the grid cell non-positive. Also feeds
	/// a non-finite coordinate, which is the other way in.
	private static int Degenerate()
	{
		int bad = 0;
		foreach (double dist in new[] { 0.0, 3000.0 })
		foreach (bool poison in new[] { false, true })
		{
			Random rng = new Random(99);
			ulong nb = 1, no = 1;
			var baseline = new Reference.PartitionMembership(dist, 0, c => Enumerable.Range(0, c).Select(_ => nb++).ToArray());
			var opt = new ClusterPlugin.PartitionMembership(dist, 0, c => Enumerable.Range(0, c).Select(_ => no++).ToArray());
			var bodies = Enumerable.Range(0, 30).Select(i => (Id: 100L + i,
				X: i < 10 ? 0.0 : rng.Next(3) * 1.0, Y: 0.0, Z: 0.0, R: 0.0, G: (long)(i % 4 == 0 ? 2 : 0))).ToList();
			if (poison) bodies[5] = (bodies[5].Id, double.PositiveInfinity, 0, 0, 0, 0);
			for (int pass = 0; pass < 4; pass++)
			{
				double now = 100 + pass * 7;
				var cb = baseline.Evaluate(bodies.Select(b => new Reference.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, 0, b.G)).ToArray(), now);
				var co = opt.Evaluate(bodies.Select(b => new ClusterPlugin.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, 0, b.G)).ToArray(), now);
				if (Dump(baseline.Layouts, cb) != Dump(opt.Layouts, co))
				{
					Console.WriteLine($"DEGENERATE MISMATCH dist={dist} poison={poison} pass={pass}");
					Console.WriteLine("  base: " + Dump(baseline.Layouts, cb));
					Console.WriteLine("  opt : " + Dump(opt.Layouts, co));
					bad++; break;
				}
			}
		}
		return bad;
	}

	/// A repeated id reached Components as one node in the id-keyed union-find; it must still be one.
	private static int Duplicates()
	{
		var raw = new[] { 100L, 101, 101, 102, 103, 103, 103, 104 };
		var members = new Dictionary<long, (double X, long G)> {
			{ 100, (0, 0) }, { 101, (100, 7) }, { 102, (9000, 0) }, { 103, (9100, 7) }, { 104, (40000, 0) } };
		string b = Components(typeof(Reference.PartitionMembership), raw,
			members.ToDictionary(kv => kv.Key, kv => (object)new Reference.PartitionMembership.Member(kv.Key, kv.Value.X, 0, 0, 0, 0, kv.Value.G)));
		string o = Components(typeof(ClusterPlugin.PartitionMembership), raw,
			members.ToDictionary(kv => kv.Key, kv => (object)new ClusterPlugin.PartitionMembership.Member(kv.Key, kv.Value.X, 0, 0, 0, 0, kv.Value.G)));
		if (b == o) { Console.WriteLine("duplicate ids: " + b); return 0; }
		Console.WriteLine($"DUPLICATE MISMATCH\n  base: {b}\n  opt : {o}");
		return 1;
	}

	/// T-0319: the scripted flapping scenarios (Shared/Cluster/MembershipFlapping.cs) against the live file.
	private static int Flap()
	{
		int failed = 0;
		foreach (var outcome in ClusterPlugin.MembershipFlapping.Run())
		{
			Console.WriteLine($"{(outcome.Passed ? "PASS" : "FAIL")} {outcome.Name}: {outcome.Merges} merge(s), {outcome.Splits} split(s), {outcome.Transfers} transfer(s); expected {outcome.Expectation}");
			Console.WriteLine("     " + outcome.Trace);
			if (!outcome.Passed) failed++;
		}
		Console.WriteLine(failed == 0 ? "flap: all scenarios pass" : $"flap: {failed} scenario(s) FAIL");
		return failed == 0 ? 0 : 1;
	}

	private static int Bench()
	{
		// The shape T-0101 measured: one dense ground clump every member of which is inside the sync
		// distance of the next, plus scattered traffic, with new entities arriving unassigned.
		foreach (int total in new[] { 400, 900, 1323 })
		{
			Random rng = new Random(7);
			World w = new World();
			for (int i = 0; i < total * 3 / 4; i++)
				w.Bodies.Add((100 + i, rng.NextDouble() * 1200 - 600, rng.NextDouble() * 1200 - 600,
					rng.NextDouble() * 1200 - 600, rng.NextDouble() * 25, 0, 0));
			for (int i = total * 3 / 4; i < total; i++)
				w.Bodies.Add((100 + i, rng.NextDouble() * 200000, rng.NextDouble() * 200000, rng.NextDouble() * 200000,
					rng.NextDouble() * 25, 0, 0));

			ulong nb = 1, no = 1;
			var baseline = new Reference.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => nb++).ToArray());
			var opt = new ClusterPlugin.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => no++).ToArray());
			var inB = w.Bodies.Select(b => new Reference.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, b.Speed, b.Group)).ToArray();
			var inO = w.Bodies.Select(b => new ClusterPlugin.PartitionMembership.Member(b.Id, b.X, b.Y, b.Z, b.R, b.Speed, b.Group)).ToArray();
			baseline.Evaluate(inB, 100); opt.Evaluate(inO, 100);           // seed the layouts
			baseline.Evaluate(inB, 110); opt.Evaluate(inO, 110);           // warm

			const int reps = 40;
			Stopwatch sw = Stopwatch.StartNew();
			for (int i = 0; i < reps; i++) baseline.Evaluate(inB, 200 + i);
			double bms = sw.Elapsed.TotalMilliseconds / reps;
			sw.Restart();
			for (int i = 0; i < reps; i++) opt.Evaluate(inO, 200 + i);
			double oms = sw.Elapsed.TotalMilliseconds / reps;
			Console.WriteLine($"steady   n={total,5}  baseline {bms,8:F3} ms   optimized {oms,8:F3} ms   {bms / oms,5:F2}x");

			// with an import: 12% of the bodies arrive unassigned every pass
			int cut = total * 88 / 100;
			var partB = inB.Take(cut).ToArray(); var partO = inO.Take(cut).ToArray();
			ulong nb2 = 1, no2 = 1;
			var b2 = new Reference.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => nb2++).ToArray());
			var o2 = new ClusterPlugin.PartitionMembership(Distance, Margin, c => Enumerable.Range(0, c).Select(_ => no2++).ToArray());
			b2.Evaluate(partB, 100); o2.Evaluate(partO, 100);
			sw.Restart();
			for (int i = 0; i < reps; i++) { b2.Evaluate(inB, 200 + i); b2.Remove(b2.Layouts[^1].Id); }
			double bi = sw.Elapsed.TotalMilliseconds / reps;
			sw.Restart();
			for (int i = 0; i < reps; i++) { o2.Evaluate(inO, 200 + i); o2.Remove(o2.Layouts[^1].Id); }
			double oi = sw.Elapsed.TotalMilliseconds / reps;
			Console.WriteLine($"adopting n={total,5}  baseline {bi,8:F3} ms   optimized {oi,8:F3} ms   {bi / oi,5:F2}x");
		}
		return 0;
	}
}
