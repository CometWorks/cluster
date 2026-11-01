using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClusterPlugin;

/// <summary>
/// T-0319: scripted motion against <see cref="PartitionMembership"/> - pure geometry, no game types, so
/// the same scenarios run in the cluster self-test (section "membership-flapping") and offline in
/// Tools/MembershipEquivalence ("dotnet run -c Release -- flap").
///
/// What r34 showed (2026-09-30): a member travelling from one group to another merges the two groups
/// when it comes within D of the destination while still within the split distance of the group it
/// left, and the split follows seconds later when it passes the split distance from its origin. The
/// [D, split] band protects one PAIR; the merge and the split are crossed by two different pairs, so
/// the band never applies. A shuttle between two groups paid two merges and two splits per round trip.
/// Each scenario below says how many commits the motion may cost at most.
/// </summary>
internal static class MembershipFlapping
{
	internal const double Distance = 3000;

	/// <summary>The split distance a node runs with by default at sync distance <paramref name="distance"/>.</summary>
	internal static double ProductionSplitDistance(double distance) => PartitionMembership.AutoSplitDistance(distance);   // partition.splitDistance "auto"

	/// <summary><paramref name="Transfers"/>: T-0339 (design C) transfers, each also counted as one merge and one split - the pair it replaces.</summary>
	internal sealed record Outcome(string Name, int Merges, int Splits, bool Passed, string Expectation, string Trace, int Transfers = 0);

	private sealed class Body
	{
		public long Id;
		public double R = 5;
		public Func<double, long> Group = _ => 0;
		public Func<double, (double X, double Y, double Z)> At;
	}

	/// <summary>Every scenario, run at the production split distance or at the one it names.</summary>
	internal static Outcome[] Run()
	{
		double production = ProductionSplitDistance(Distance);
		return new[]
		{
			FlyBy(production),
			Shuttle(production),
			BridgeRefused(Distance + 2 * 1000),
			KnifeEdge(production),
			TouchingBridge(production),
			AtomicBridge(production),
			Arrival(production),
			Relay(production),
			SplitEdgeHover(production),
			SplitAge(production),
			FreshLayoutWaits(production),
			ChangeTimes(),
			SplitKeepsId(production),
			// #1300: an oversized layout splits at the first shorter cut that fits, never below the floor.
			OversizeSplit(production),
			// T-0339 (design C): the motion scenarios again with transfers on (a registry that applies them). A transfer
			// counts as the split and the merge it replaces, so every bound above must hold unchanged.
			Transferred(FlyBy(production, true)),
			Transferred(Shuttle(production, true)),
			Transferred(BridgeRefused(Distance + 2 * 1000, true)),
			Transferred(KnifeEdge(production, true)),
			Transferred(TouchingBridge(production, true)),
			Transferred(AtomicBridge(production, true)),
			Transferred(Arrival(production, true)),
			Transferred(Relay(production, true)),
			Transferred(SplitEdgeHover(production, true)),
			RelayTransfers(production),
			DepartureSplits(production),
			TransferRevert(production),
			// T-0339 (design C2): the motion scenarios again with the transfer band on; every bound above must hold.
			Banded(FlyBy(production, true, true)),
			Banded(Shuttle(production, true, true)),
			Banded(BridgeRefused(Distance + 2 * 1000, true, true)),
			Banded(KnifeEdge(production, true, true)),
			Banded(TouchingBridge(production, true, true)),
			Banded(AtomicBridge(production, true, true)),
			Banded(Arrival(production, true, true)),
			Banded(Relay(production, true, true)),
			Banded(SplitEdgeHover(production, true, true)),
			BandApproach(production),
			BandRecedes(production),
			BandNearest(production),
			BandEdgeNoPingPong(production),
			BandCores(production),
			BandStable(production),
			// T-0339 (merge hold-off): the hold-off scenario, with transfers off and with transfer + band on (the live default).
			MergeHoldoff(production, false),
			MergeHoldoff(production, true),
			// T-0339 (merge hold-off, r118 gate): the hold-off never suppresses a split or a transfer. The relay bench's
			// fixture and legs, transfers off, on, and on with the band (the live default); siblings that split again or
			// move a piece out (into a third layout and into the other sibling); a held pair whose sibling then splits.
			HoldoffRelay(production, false, false),
			HoldoffRelay(production, true, false),
			HoldoffRelay(production, true, true),
			HoldoffSiblingSplits(production, false),
			HoldoffSiblingSplits(production, true),
			HoldoffHeldThenSplits(production, false),
			HoldoffHeldThenSplits(production, true),
			// T-0339 (split deferral): a due split the pass leaves undone is listed with its reason, and nothing else changes.
			SplitDeferral(production),
			MintedPieceDiag(production),
			// T-0339 (split hysteresis): partition.splitHysteresisSeconds = 60 against knob 0, design C and with the band.
			HysteresisClosing(production, false),
			HysteresisClosing(production, true),
			HysteresisNotClosing(production, false),
			HysteresisNotClosing(production, true),
			HysteresisTooSlow(production, false),
			HysteresisTooSlow(production, true),
			HysteresisBraking(production),
			HysteresisTwoPieces(production, false),
			HysteresisTwoPieces(production, true),
			// T-0339i: the node evaluates several times inside one game frame; the closing rate reads game time.
			HysteresisSameFrame(production, false),
			HysteresisSameFrame(production, true),
			HysteresisStops(production),
			HysteresisJump(production, false),
			HysteresisJump(production, true),
			// T-0339j: a held piece inside D whose transfer the target's core test refuses keeps its hold.
			HysteresisInsideD(production, false),
			HysteresisInsideD(production, true),
			HysteresisInsideDNeverTaken(production, false),
			HysteresisInsideDNeverTaken(production, true),
		};
	}

	private static Outcome Transferred(Outcome outcome) => outcome with { Name = outcome.Name + " +transfer" };
	private static Outcome Banded(Outcome outcome) => outcome with { Name = outcome.Name + " +band" };

	internal static string[] Failures() => Run().Where(x => !x.Passed)
		.Select(x => $"{x.Name}: {x.Merges} merge(s), {x.Splits} split(s); expected {x.Expectation}. {x.Trace}").ToArray();

	private static Func<double, (double, double, double)> Fixed(double x, double y = 0, double z = 0) => _ => (x, y, z);

	/// <summary>Straight legs at constant speed through the waypoints, then parked at the last one.</summary>
	private static Func<double, (double, double, double)> Route(double speed, params (double X, double Y)[] points) => t =>
	{
		double left = t * speed;
		for (int i = 1; i < points.Length; i++)
		{
			double dx = points[i].X - points[i - 1].X, dy = points[i].Y - points[i - 1].Y;
			double length = Math.Sqrt(dx * dx + dy * dy);
			if (left <= length) return (points[i - 1].X + dx * left / length, points[i - 1].Y + dy * left / length, 0);
			left -= length;
		}
		return (points[^1].X, points[^1].Y, 0);
	};

	/// <summary>
	/// Runs the bodies through membership passes every <paramref name="step"/> seconds for
	/// <paramref name="seconds"/>, and reports commits and whether any layout ever held a member of
	/// <paramref name="apart"/>.Item1 together with one of its Item2. T-0339: Lone is how long the body
	/// <paramref name="lone"/> ended a pass alone in a layout while within D of a member of another layout.
	/// T-0339 (design C): <paramref name="transfer"/> turns transfers on; Born is how many partition ids the passes after
	/// the first one allocated. T-0339 (design C2): <paramref name="band"/> turns the transfer band on; each member reports
	/// its displacement over the last step as its speed; Commits lists every change with its time.
	/// </summary>
	private static (int Merges, int Splits, bool Together, string Trace, PartitionMembership Membership, double Lone, int Transfers, int Born,
		List<(double T, PartitionMembership.Change Change)> Commits) Drive(
		IReadOnlyList<Body> bodies, double split, double seconds, (long[] A, long[] B)? apart = null, double step = 0.5, long? lone = null,
		bool transfer = false, bool band = false)
	{
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = transfer, TransferBand = band };
		int merges = 0, splits = 0, transfers = 0;
		ulong first = 0;
		bool together = false;
		double alone = 0;
		List<string> trace = new List<string>();
		List<(double T, PartitionMembership.Change Change)> commits = new List<(double, PartitionMembership.Change)>();
		for (double t = 0; t <= seconds; t += step)
		{
			PartitionMembership.Member[] members = bodies.Select(b =>
			{
				(double x, double y, double z) = b.At(t);
				// T-0339 (design C2): the band reads the speed. Unread without it, so every scenario above is unchanged.
				(double px, double py, double pz) = b.At(Math.Max(0, t - step));
				double speed = t == 0 ? 0 : Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz)) / step;
				return new PartitionMembership.Member(b.Id, x, y, z, b.R, speed, b.Group(t));
			}).ToArray();
			PartitionMembership.Change[] changes = membership.Evaluate(members, 1000 + t);
			if (t == 0) first = next;
			foreach (PartitionMembership.Change change in changes)
			{
				if (change.Transfer) { transfers++; splits++; merges++; }
				else if (change.Split) splits++; else merges++;
				trace.Add($"t={t:F1} {(change.Transfer ? (change.Why.Contains("transfer band") ? "band transfer" : "transfer") : change.Split ? "split" : "merge")} [{string.Join("|", (change.Split ? change.Outputs : change.InputLayouts).Select(l => l.Id + ":" + string.Join(",", l.Members)))}]");
				commits.Add((t, change));
			}
			if (apart is { } pair && membership.Layouts.Any(l => l.Members.Any(pair.A.Contains) && l.Members.Any(pair.B.Contains)))
				together = true;
			if (lone is { } id && membership.Layouts.Any(l => l.Members.Length == 1 && l.Members[0] == id))
			{
				PartitionMembership.Member self = members.Single(m => m.Id == id);
				if (members.Any(m => m.Id != id && Math.Sqrt((m.X - self.X) * (m.X - self.X) + (m.Y - self.Y) * (m.Y - self.Y)
						+ (m.Z - self.Z) * (m.Z - self.Z)) - m.Radius - self.Radius < Distance))
					alone += step;
			}
		}
		return (merges, splits, together, trace.Count == 0 ? "no commits" : string.Join("; ", trace), membership, alone, transfers, (int)(next - first), commits);
	}

	private static bool SameLayout(PartitionMembership membership, long a, long b) =>
		membership.Layouts.Any(l => l.Members.Contains(a) && l.Members.Contains(b));

	/// <summary>
	/// A pilot leaves a parked group A' and flies past a parked pair B (6.2 km from A') at 2.5 km and on into
	/// empty space. Passing B once may cost one merge and one split. df: it split off A', merged B and split
	/// off B again - three commits for one pass.
	/// </summary>
	private static Outcome FlyBy(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(300) }, new Body { Id = 3, At = Fixed(0, 300) },
			new Body { Id = 4, At = Fixed(6500) }, new Body { Id = 5, At = Fixed(6700) },
			new Body { Id = 6, At = Route(100, (500, 2500), (40000, 2500)) },
		};
		var run = Drive(bodies, split, 400, transfer: transfer, band: band);
		return new Outcome($"fly-by (split {split:F0} m)", run.Merges, run.Splits, run.Merges <= 1 && run.Splits <= 1,
			"<= 1 merge and <= 1 split", run.Trace);
	}

	/// <summary>
	/// A pilot shuttles three round trips between two parked groups 7 km apart, turning 1 km short of
	/// each, so it crosses D of one group while still inside the split distance of the other - the merges
	/// land just under D (2990-3000 m), as in r34. The two groups and the shuttle may cost one merge and one
	/// split in total. df: two merges and two splits per round trip.
	/// </summary>
	private static Outcome Shuttle(double split, bool transfer = false, bool band = false)
	{
		(double, double)[] legs = Enumerable.Range(0, 3).SelectMany(_ => new[] { (6000.0, 500.0), (1000.0, 500.0) })
			.Prepend((1000.0, 500.0)).ToArray();
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(7000) }, new Body { Id = 4, At = Fixed(7200) },
			new Body { Id = 5, At = Route(100, legs) },
		};
		var run = Drive(bodies, split, 330, transfer: transfer, band: band);
		return new Outcome($"shuttle (split {split:F0} m)", run.Merges, run.Splits, run.Merges <= 1 && run.Splits <= 1,
			"<= 1 merge and <= 1 split over three round trips", run.Trace);
	}

	/// <summary>
	/// A pilot leaves A' and parks next to a single grid B that is 7 km from A' - beyond the split distance
	/// (5000 m here, for df and the fix alike). Under df the pilot merged A' and B for the seconds between
	/// coming within D of B and passing the split distance from A' (286411 on node-1, 21:40:42, 1.4 s). The
	/// merge must be judged on the cores: A' and B never share a partition, and the trip costs at most one
	/// split (leave A') and one merge (join B).
	/// </summary>
	private static Outcome BridgeRefused(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(0, 400) },
			new Body { Id = 3, At = Fixed(7000) },
			new Body { Id = 4, At = Route(100, (800, 0), (6500, 0)) },
		};
		var run = Drive(bodies, split, 150, (new long[] { 1, 2 }, new long[] { 3 }), transfer: transfer, band: band);
		bool joined = SameLayout(run.Membership, 4, 3);
		return new Outcome($"bridge refused (split {split:F0} m)", run.Merges, run.Splits,
			!run.Together && joined && run.Merges <= 1 && run.Splits <= 1,
			"A' and B never in one layout, the pilot ends with B, <= 1 merge and <= 1 split",
			(run.Together ? "A' and B shared a layout; " : "") + (joined ? "" : "pilot not with B; ") + run.Trace);
	}

	/// <summary>Control: a pilot hovering at D +- 15 m from a lone grid merges once and never splits (the band works for one pair).</summary>
	private static Outcome KnifeEdge(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) },
			new Body { Id = 2, At = t => t < 170 ? Route(100, (20000, 0), (3010, 0))(t) : (3010 + (((int)(t * 2)) % 2 == 0 ? 15 : -15), 0, 0) },
		};
		var run = Drive(bodies, split, 300, transfer: transfer, band: band);
		return new Outcome($"knife-edge hover (split {split:F0} m)", run.Merges, run.Splits, run.Merges <= 1 && run.Splits == 0,
			"<= 1 merge and no split", run.Trace);
	}

	/// <summary>
	/// Exception: a 600 m ship leaves a grid A' for a grid B 13 km away - beyond the split distance of A',
	/// so the core test refuses the merge while the ship is merely within D of B (from x = 9395 m) - and
	/// then touches B (overlapping bounding spheres from x = 12395 m) while still attached to A'.
	/// Physically interacting grids merge whatever the cores say.
	/// </summary>
	private static Outcome TouchingBridge(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) },
			new Body { Id = 2, At = Fixed(13000) },
			new Body { Id = 3, R = 600, At = Route(50, (2000, 0), (12450, 0)) },
		};
		var run = Drive(bodies, split, 230, transfer: transfer, band: band);
		bool merged = SameLayout(run.Membership, 3, 2);
		return new Outcome($"touching bridge (split {split:F0} m)", run.Merges, run.Splits, merged,
			"the touching ship and B in one layout", run.Trace);
	}

	/// <summary>
	/// Exception: the same ship stops 395 m short of B and docks with it (one atomic group from t = 200 s)
	/// while still attached to A' - it merges at any distance.
	/// </summary>
	private static Outcome AtomicBridge(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) },
			new Body { Id = 2, Group = _ => 77, At = Fixed(13000) },
			new Body { Id = 3, R = 600, Group = t => t >= 200 ? 77 : 0, At = Route(50, (2000, 0), (12000, 0)) },
		};
		var run = Drive(bodies, split, 230, transfer: transfer, band: band);
		bool merged = SameLayout(run.Membership, 3, 2);
		return new Outcome($"atomic bridge (split {split:F0} m)", run.Merges, run.Splits, merged,
			"the docked ship and B in one layout", run.Trace);
	}

	/// <summary>Control: a lone ship flies in from 20 km and parks inside a compact hub - one merge, nothing else.</summary>
	private static Outcome Arrival(double split, bool transfer = false, bool band = false)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(500) }, new Body { Id = 3, At = Fixed(0, 800) },
			new Body { Id = 4, At = Route(200, (20000, 0), (1500, 0)) },
		};
		var run = Drive(bodies, split, 150, transfer: transfer, band: band);
		bool merged = SameLayout(run.Membership, 4, 1);
		return new Outcome($"arrival (split {split:F0} m)", run.Merges, run.Splits, merged && run.Merges == 1 && run.Splits == 0,
			"the ship joins the hub with exactly one merge", run.Trace);
	}

	/// <summary>
	/// T-0339: a pilot relays three round trips between two parked groups 14 km apart - beyond the split
	/// distance, so the groups never share a partition (the core test refuses the bridge) and every leg costs
	/// one split (leave) and one merge (join). The ship passes the split distance from the group it left while
	/// already within D of the group it flies to: r42 2026-10-02, 597 partition ids lived 10 s between that
	/// split and that merge, each one registered, saved and retired. The piece inherits its parent's age, so
	/// the merge follows the split in the same pass.
	/// </summary>
	private static Outcome Relay(double split, bool transfer = false, bool band = false)
	{
		(double, double)[] legs = Enumerable.Range(0, 3).SelectMany(_ => new[] { (13000.0, 0.0), (1000.0, 0.0) })
			.Prepend((1000.0, 0.0)).ToArray();
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(14000) }, new Body { Id = 4, At = Fixed(14200) },
			new Body { Id = 5, At = Route(100, legs) },
		};
		var run = Drive(bodies, split, 740, (new long[] { 1, 2 }, new long[] { 3, 4 }), lone: 5, transfer: transfer, band: band);
		return new Outcome($"relay (split {split:F0} m)", run.Merges, run.Splits,
			!run.Together && run.Merges == 6 && run.Splits == 6 && run.Lone == 0,
			"the groups never in one layout, 6 merges and 6 splits over six legs, the ship never alone in a layout within D of a group",
			(run.Together ? "the groups shared a layout; " : "") + $"ship alone within D for {run.Lone:F1} s; " + run.Trace);
	}

	/// <summary>
	/// T-0339 control: the same ship stops where it passes the split distance from the group it left and
	/// hovers +- 15 m around that point for 100 s, within D of the other group. One split and one merge: the
	/// inherited age must not let the piece bounce between the two groups.
	/// </summary>
	private static Outcome SplitEdgeHover(double split, bool transfer = false, bool band = false)
	{
		double edge = 200 + 5 + split + 5;   // member 2 at 200 m, both radii 5 m
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(14000) }, new Body { Id = 4, At = Fixed(14200) },
			new Body { Id = 5, At = t => t < (edge - 1000) / 100 ? Route(100, (1000, 0), (edge, 0))(t) : (edge + (((int)(t * 2)) % 2 == 0 ? 15 : -15), 0, 0) },
		};
		var run = Drive(bodies, split, (edge - 1000) / 100 + 100, (new long[] { 1, 2 }, new long[] { 3, 4 }), transfer: transfer, band: band);
		return new Outcome($"split-edge hover (split {split:F0} m)", run.Merges, run.Splits,
			!run.Together && run.Merges == 1 && run.Splits == 1 && SameLayout(run.Membership, 5, 3),
			"the groups never in one layout, exactly one split and one merge, the ship ends with the far group",
			(run.Together ? "the groups shared a layout; " : "") + run.Trace);
	}

	/// <summary>
	/// T-0339: the children of a split carry the age of the layout they were split from - a mature parent
	/// gives mature children, and the children of a parent that arrived 3 s ago wait out the rest of ITS 10 s
	/// (an arrival's maturity delay is not shortened by splitting it).
	/// </summary>
	private static Outcome SplitAge(double split)
	{
		PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 5, 0);
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray());
		List<string> trace = new List<string>();
		membership.Evaluate(new[] { At(1, 0), At(2, 100), At(12, 120000) }, 1000);
		PartitionMembership.Change[] parted = membership.Evaluate(new[] { At(1, 0), At(2, 40000), At(12, 120000) }, 1100);
		bool inherited = parted.Length == 1 && parted[0].Split && parted[0].Outputs.All(x => x.CreatedAt == 1000);
		trace.Add("children of a layout made at 1000 and split at 1100 are aged " + string.Join(",", membership.Layouts.Select(l => l.CreatedAt)));
		// An immature arrival [10, 11] (adopted at 2000) next to a standing grid 12: 11 leaves at 2003 and is within D of 12.
		membership.Adopt(900, new long[] { 10, 11 }, 2000, mature: false);
		PartitionMembership.Member[] before = { At(1, 0), At(2, 40000), At(10, 100000), At(11, 100100), At(12, 120000) };
		PartitionMembership.Member[] after = { At(1, 0), At(2, 40000), At(10, 100000), At(11, 119000), At(12, 120000) };
		membership.Evaluate(before, 2001);
		int merges = 0, splits = 0;
		bool early = false, joined = false;
		foreach (double now in new[] { 2003.0, 2005.0, 2009.5, 2010.5 })
		{
			foreach (PartitionMembership.Change change in membership.Evaluate(after, now))
			{
				if (change.Split) splits++; else { merges++; early |= now < 2010; }
				trace.Add($"now={now:F1} {(change.Split ? "split" : "merge")}");
			}
			joined = SameLayout(membership, 11, 12);
		}
		return new Outcome($"split age (split {split:F0} m)", merges, splits, inherited && splits == 1 && merges == 1 && !early && joined,
			"split children aged as their parent; the piece of an arrival adopted at 2000 merges at 2010, not before and not later",
			string.Join("; ", trace));
	}

	/// <summary>
	/// T-0339 control: the maturity delay itself stands. A layout made for a new member (not a split child)
	/// that comes within D of a neighbour 4 s later merges only once it is 10 s old.
	/// </summary>
	private static Outcome FreshLayoutWaits(double split)
	{
		PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 5, 0);
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray());
		membership.Evaluate(new[] { At(1, 0) }, 1000);
		membership.Evaluate(new[] { At(1, 0), At(2, 50000) }, 1100);
		int early = membership.Evaluate(new[] { At(1, 0), At(2, 2000) }, 1104).Length
			+ membership.Evaluate(new[] { At(1, 0), At(2, 2000) }, 1109.5).Length;
		PartitionMembership.Change[] late = membership.Evaluate(new[] { At(1, 0), At(2, 2000) }, 1110.5);
		return new Outcome($"fresh layout waits (split {split:F0} m)", late.Count(x => !x.Split), 0,
			early == 0 && late.Length == 1 && !late[0].Split,
			"no merge while the new layout is under 10 s old, one merge after",
			$"{early} change(s) before 10 s, {late.Length} after");
	}

	/// <summary>
	/// T-0339: the catalogue times of one node's topology changes are strictly increasing, whatever the clock
	/// does between two commits of one pass (the same millisecond, or a step back).
	/// </summary>
	private static Outcome ChangeTimes()
	{
		long previous = long.MinValue;
		long[] stamped = new long[] { 0, 0, 5000, 5000, 5000, 5007, 5003, 9000 }
			.Select(clock => PartitionMembership.NextChangeTime(clock, ref previous)).ToArray();
		return new Outcome("change times", 0, 0, stamped.SequenceEqual(new long[] { 0, 1, 5000, 5001, 5002, 5007, 5008, 9000 }),
			"the clock where it is ahead, else one past the previous change", "stamped " + string.Join(",", stamped));
	}

	/// <summary>
	/// T-0339: the largest group of a distance split keeps the input's partition id and only the others get new
	/// ones - a parked group does not change its id because a ship left it (r42 2026-10-02: 2286 of 5017
	/// partition births in 340 min were such remainders). A tie goes to the group holding the smallest member
	/// id, whatever order the census came in. One id fewer is asked of the allocator per split.
	/// </summary>
	private static Outcome SplitKeepsId(double split)
	{
		PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 5, 0);
		List<string> trace = new List<string>();
		bool ok = true;
		int splits = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		string Shape(PartitionMembership.Change change) => string.Join("|", change.Outputs.Select(x => x.Id + ":" + string.Join(",", x.Members)));

		foreach (bool reversed in new[] { false, true })
		{
			ulong next = 1;
			List<int> asked = new List<int>();
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => { asked.Add(count); return Enumerable.Range(0, count).Select(_ => next++).ToArray(); });
			PartitionMembership.Change[] Pass(double now, params PartitionMembership.Member[] census) =>
				membership.Evaluate(reversed ? census.Reverse().ToArray() : census, now);
			// One layout of seven; 4 leaves (the larger rest keeps the id), then the rest falls into [1,2,3] and [5,6,7] (a tie).
			Pass(1000, At(1, 0), At(2, 100), At(3, 200), At(4, 300), At(5, 400), At(6, 500), At(7, 600));
			ulong id = membership.Layouts.Single().Id;
			asked.Clear();
			PartitionMembership.Change[] one = Pass(1100, At(1, 0), At(2, 100), At(3, 200), At(4, 50000), At(5, 400), At(6, 500), At(7, 600));
			splits += one.Count(x => x.Split);
			Expect(one.Length == 1 && one[0].Split && one[0].Inputs.SequenceEqual(new[] { id }), "one split of the layout");
			if (one.Length == 1) trace.Add((reversed ? "reversed " : "") + "split " + id + " -> " + Shape(one[0]));
			Expect(one.Length == 1 && one[0].Outputs.Single(x => x.Members.Length == 6).Id == id, "the group of six keeps id " + id);
			Expect(one.Length == 1 && one[0].Outputs.Single(x => x.Members.Length == 1).Id != id, "the group of one gets a new id");
			Expect(asked.SequenceEqual(new[] { 1 }), "one new id asked for a split in two, asked [" + string.Join(",", asked) + "]");
			asked.Clear();
			PartitionMembership.Change[] two = Pass(1200, At(1, 0), At(2, 100), At(3, 200), At(4, 50000), At(5, 100400), At(6, 100500), At(7, 100600));
			splits += two.Count(x => x.Split);
			if (two.Length == 1) trace.Add((reversed ? "reversed " : "") + "split " + id + " -> " + Shape(two[0]));
			Expect(two.Length == 1 && two[0].Split && two[0].Outputs.Single(x => x.Members.Contains(1L)).Id == id, "on a tie the group with the smallest member keeps id " + id);
			Expect(two.Length == 1 && two[0].Outputs.Single(x => x.Members.Contains(5L)).Id != id, "the other group of the tie gets a new id");
			Expect(membership.Layouts.Select(x => x.Id).Distinct().Count() == membership.Layouts.Count, "no two layouts share an id");
			// The undo of a refused split puts the whole input back under its id.
			if (two.Length == 1)
			{
				membership.Revert(two[0]);
				Expect(membership.Layouts.SingleOrDefault(x => x.Id == id)?.Members.SequenceEqual(new long[] { 1, 2, 3, 5, 6, 7 }) == true, "a reverted split restores the input layout");
				Expect(membership.Layouts.Count == 2, "a reverted split leaves no output behind");
			}
		}

		// The relay of the scenario above: the two parked groups keep their ids through all six legs.
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(14000) }, new Body { Id = 4, At = Fixed(14200) },
			new Body { Id = 5, At = Route(100, Enumerable.Range(0, 3).SelectMany(_ => new[] { (13000.0, 0.0), (1000.0, 0.0) }).Prepend((1000.0, 0.0)).ToArray()) },
		};
		var run = Drive(bodies, split, 740);
		ulong near = run.Membership.Layouts.Single(x => x.Members.Contains(1L)).Id, far = run.Membership.Layouts.Single(x => x.Members.Contains(3L)).Id;
		trace.Add($"relay: after {run.Splits} splits the groups are partitions {near} and {far}");
		Expect(near == 1 && far == 2, "the parked groups of the relay still have the ids they started with (1 and 2)");
		return new Outcome($"split keeps id (split {split:F0} m)", 0, splits, ok,
			"the largest output of a split keeps the input id, ties to the smallest member, one id fewer allocated", string.Join("; ", trace));
	}

	/// <summary>
	/// #1300: four parked groups of four on a line, 9.5, 6 and 4.5 km apart - one partition at the 12 km split distance, as
	/// the 578-member lane partition of octahedron-1191 was. Above partition.oversizeMembers the split comes down 1000 m at
	/// a time: a cap of 12 stops at 9000 m and a cap of 8 at 5000 m (the first cut that fits); a cap of 4 reaches the floor.
	/// At the node's floor D + margin = 4000 m that is four pieces; at the #1300 floor D + 2 x margin = 5000 m the last two
	/// groups, 4.5 km apart, stayed one piece of 8 (#1305: lane partition 3187, widest gap 4.9 km). Off, under a cap the
	/// layout fits, or with the floor at D nothing splits; a split layout and a compact clump over the cap stay as they are
	/// on the next passes.
	/// </summary>
	private static Outcome OversizeSplit(double split)
	{
		List<string> trace = new List<string>();
		bool ok = true;
		int splits = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		// Spread: groups start at 0, 9800, 16100 and 20900 (member gaps 9490, 5990 and 4490 m); together: one row 100 m apart.
		PartitionMembership.Member[] Line(bool spread) => Enumerable.Range(0, 16).Select(i => new PartitionMembership.Member(i + 1,
			spread ? new[] { 0, 9800, 16100, 20900 }[i / 4] + i % 4 * 100 : i * 100, 0, 0, 5, 0)).ToArray();
		PartitionMembership.Member[] Clump() => Enumerable.Range(0, 20).Select(i => new PartitionMembership.Member(100 + i, 50000 + i * 100, 0, 0, 5, 0)).ToArray();

		int[] Pieces(int cap, double floor, out PartitionMembership.Change[] later, out string why)
		{
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray())
				{ OversizeMembers = cap, OversizeSplitFloor = floor };
			membership.Evaluate(Line(false).Concat(Clump()), 1000);   // one layout of 16 (100 m apart) and one of 20
			PartitionMembership.Change[] changes = membership.Evaluate(Line(true).Concat(Clump()), 1100);
			splits += changes.Count(x => x.Split);
			why = string.Join(" / ", changes.Select(x => x.Why));
			later = membership.Evaluate(Line(true).Concat(Clump()), 1200).Concat(membership.Evaluate(Line(true).Concat(Clump()), 1300)).ToArray();
			return membership.Layouts.Where(x => x.Members.Min() < 100).Select(x => x.Members.Length).OrderBy(x => x).ToArray();
		}
		// The node's floor (PartitionManager.Evaluate) at partition.margin 1000, and the #1300 one.
		double floor = Distance + 1000, old = Distance + 2 * 1000;
		foreach ((int cap, double at, string expect) in new[] { (0, floor, "16"), (16, floor, "16"), (12, floor, "4,12"), (8, floor, "4,4,8"),
			(4, floor, "4,4,4,4"), (4, old, "4,4,8"), (4, Distance, "16") })
		{
			int[] pieces = Pieces(cap, at, out PartitionMembership.Change[] later, out string why);
			trace.Add($"cap {cap} floor {at:F0}: [{string.Join(",", pieces)}] {why}");
			Expect(string.Join(",", pieces) == expect, $"cap {cap}, floor {at:F0} m leaves pieces of [{expect}]");
			Expect(later.Length == 0, $"cap {cap}: nothing changes on the next two passes (got {later.Length})");
			Expect(expect == "16" ? why.Length == 0 : why.Contains("oversize split"), $"cap {cap}: the split says why");
		}
		Expect(trace.Any(x => x.StartsWith("cap 12 ") && x.Contains(">= 9000 m")), "cap 12 cuts at 9000 m");
		return new Outcome($"oversize split (split {split:F0} m)", 0, splits, ok,
			"an oversized layout splits at the first shorter cut that fits, down to the floor, and then holds", string.Join("; ", trace));
	}

	/// <summary>
	/// T-0339 (design C): the relay with transfers on. Every leg used to cost a split that gave the ship a new
	/// partition id and a merge that retired it in the same pass (r77 2026-10-03: 62% of the new ids died within
	/// 60 s, the pattern "split:piece -> merge" with a median life of 0 s). Now each leg is one transfer from the
	/// group the ship left to the group it reaches: no split, no merge, no id allocated after the first pass, and
	/// both groups keep their ids. The control arm is the same motion with transfers off - the df behaviour, six
	/// new ids - so this scenario is red there.
	/// </summary>
	private static Outcome RelayTransfers(double split)
	{
		Body[] Bodies() => new[]
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(14000) }, new Body { Id = 4, At = Fixed(14200) },
			new Body { Id = 5, At = Route(100, Enumerable.Range(0, 3).SelectMany(_ => new[] { (13000.0, 0.0), (1000.0, 0.0) }).Prepend((1000.0, 0.0)).ToArray()) },
		};
		var on = Drive(Bodies(), split, 740, (new long[] { 1, 2 }, new long[] { 3, 4 }), lone: 5, transfer: true);
		var off = Drive(Bodies(), split, 740, (new long[] { 1, 2 }, new long[] { 3, 4 }), lone: 5, transfer: false);
		ulong near = on.Membership.Layouts.Single(x => x.Members.Contains(1L)).Id, far = on.Membership.Layouts.Single(x => x.Members.Contains(3L)).Id;
		bool passed = !on.Together && on.Transfers == 6 && on.Splits == 6 && on.Merges == 6 && on.Born == 0 && on.Lone == 0
			&& near == 1 && far == 2 && SameLayout(on.Membership, 5, 1);
		return new Outcome($"relay transfers (split {split:F0} m)", on.Merges, on.Splits, passed,
			"6 transfers and nothing else over six legs, no partition id born, the groups keep ids 1 and 2 and never share a layout",
			$"transfers on: {on.Transfers} transfer(s), {on.Born} id(s) born; control, transfers off (df): {off.Splits} split(s), {off.Merges} merge(s), "
			+ $"{off.Born} id(s) born; " + on.Trace, on.Transfers);
	}

	/// <summary>
	/// T-0339 (design C) control: a ship that leaves its group into empty space (nothing within D) is split off with
	/// a new id exactly as before - a transfer needs a layout to take the piece.
	/// </summary>
	private static Outcome DepartureSplits(double split)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(60000) },
			new Body { Id = 5, At = Route(100, (1000, 0), (30000, 0)) },
		};
		var run = Drive(bodies, split, 300, transfer: true);
		return new Outcome($"departure splits (split {split:F0} m)", run.Merges, run.Splits,
			run.Transfers == 0 && run.Splits == 1 && run.Merges == 0 && run.Born == 1,
			"one split, one id born, no transfer", run.Trace, run.Transfers);
	}

	/// <summary>
	/// T-0339 (design C): the node's fallback when a registry refuses a transfer (an older registry answers 400:
	/// "Split requires one input"). The transfer is reverted - source and target exactly as they stood - and the next
	/// pass, with transfers off, makes the split and the merge df made, ending in the same groups.
	/// </summary>
	private static Outcome TransferRevert(double split)
	{
		PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 5, 0);
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = true };
		List<string> trace = new List<string>();
		bool ok = true;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		string Sets() => string.Join(" ", membership.Layouts.Select(x => x.Id + ":" + string.Join(",", x.Members)));
		// [1, 2, 5] and [3, 4]; then 5 is past the split distance from 2 and within D of 3.
		membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 1000), At(3, 14000), At(4, 14200) }, 1000);
		string before = Sets();
		PartitionMembership.Member[] moved = { At(1, 0), At(2, 200), At(5, 12500), At(3, 14000), At(4, 14200) };
		PartitionMembership.Change[] pass = membership.Evaluate(moved, 1100);
		trace.Add("before " + before + "; transfer pass " + string.Join(", ", pass.Select(x => x.Why)) + " -> " + Sets());
		Expect(pass.Length == 1 && pass[0].Transfer && pass[0].Inputs.SequenceEqual(new ulong[] { 1, 2 })
			&& pass[0].Outputs.Select(x => x.Id).SequenceEqual(new ulong[] { 1, 2 }) && pass[0].Moved.SequenceEqual(new long[] { 5 }),
			"one transfer of [5] from 1 to 2, both ids kept");
		Expect(next == 3, "no id allocated by the transfer");
		string transferred = Sets();
		if (pass.Length == 1)
		{
			membership.Revert(pass[0]);
			Expect(Sets() == before, "the reverted transfer restores both layouts: " + Sets());
		}
		membership.TransferEnabled = false;
		PartitionMembership.Change[] fallback = membership.Evaluate(moved, 1101);
		trace.Add("fallback pass " + string.Join(", ", fallback.Select(x => (x.Split ? "split " : "merge ") + string.Join(",", x.Inputs))) + " -> " + Sets());
		Expect(fallback.Length == 2 && fallback[0].Split && !fallback[0].Transfer && !fallback[1].Split, "transfers off: a split, then a merge");
		Expect(next == 4, "the fallback split allocates one id, as df did");
		Expect(Sets() == transferred, "the fallback ends in the groups the transfer made: " + Sets() + " vs " + transferred);
		return new Outcome($"transfer revert and fallback (split {split:F0} m)", fallback.Count(x => !x.Split), fallback.Count(x => x.Split), ok,
			"a reverted transfer restores both inputs; with transfers off the same census is a split and a merge to the same groups",
			string.Join("; ", trace), pass.Count(x => x.Transfer));
	}

	private static bool IsBand(PartitionMembership.Change change) => change.Transfer && change.Why.Contains("transfer band");

	/// <summary>
	/// T-0339 (design C2): a ship leaves P at 60 m/s towards a standing Q. When it passes the split distance from P it is
	/// 5 km from Q - in the band (D + 60 m/s x 60 s = 6600 m) - and reaches D of Q 33 s later. Band on: one band transfer
	/// P -> Q and NOTHING else over the whole approach and the parking (no re-split, no flap, no id born). Band off (design
	/// C, transfers on): the df split with a new id, which the merge retires when the ship crosses D - the short-lived id
	/// of r101's dominant pattern.
	/// </summary>
	private static Outcome BandApproach(double split)
	{
		Body[] Bodies() => new[]
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(17220) }, new Body { Id = 4, At = Fixed(17420) },
			new Body { Id = 5, At = Route(60, (1000, 0), (16000, 0)) },
		};
		var on = Drive(Bodies(), split, 300, (new long[] { 1, 2 }, new long[] { 3, 4 }), transfer: true, band: true);
		var off = Drive(Bodies(), split, 300, (new long[] { 1, 2 }, new long[] { 3, 4 }), transfer: true, band: false);
		bool passed = !on.Together && on.Commits.Count == 1 && IsBand(on.Commits[0].Change) && on.Born == 0
			&& on.Commits[0].Change.Moved.SequenceEqual(new long[] { 5 }) && SameLayout(on.Membership, 5, 3)
			&& off.Born == 1 && off.Transfers == 0 && off.Commits.Count == 2;
		return new Outcome($"band approach (split {split:F0} m)", on.Merges, on.Splits, passed,
			"band on: one band transfer of the ship into Q and no other commit over 300 s, no id born; band off: a split (one id born) and a merge, no transfer",
			$"band on: {on.Trace}; band off: {off.Born} id(s) born, {off.Trace}", on.Transfers);
	}

	/// <summary>
	/// T-0339 (design C2): the speed has no direction, so a ship RECEDING from Q is taken too. It leaves P at 100 m/s along
	/// x; Q sits 5 km off its course, ~8 km from it when it passes the split distance from P (band 9000 m). It moves into Q,
	/// stays there while Q's split rule holds it, and splits off Q - with a new id, the birth it had anyway - only once it
	/// is past the split distance from Q. Nothing else: no move back to P, no second transfer.
	/// </summary>
	private static Outcome BandRecedes(double split)
	{
		Body[] bodies =
		{
			new Body { Id = 1, At = Fixed(0) }, new Body { Id = 2, At = Fixed(200) },
			new Body { Id = 3, At = Fixed(6000, 5000) },
			new Body { Id = 5, At = Route(100, (1000, 0), (60000, 0)) },
		};
		var run = Drive(bodies, split, 400, transfer: true, band: true);
		bool ok = run.Commits.Count == 2 && IsBand(run.Commits[0].Change) && run.Commits[0].Change.Moved.SequenceEqual(new long[] { 5 });
		double leaveGap = double.NaN;
		if (ok)
		{
			PartitionMembership.Change leave = run.Commits[1].Change;
			double t = run.Commits[1].T;
			(double x, double y, double z) = bodies[3].At(t);
			leaveGap = Math.Sqrt((x - 6000) * (x - 6000) + (y - 5000) * (y - 5000) + z * z) - 10;
			(double px, double py, double pz) = bodies[3].At(t - 0.5);
			double before = Math.Sqrt((px - 6000) * (px - 6000) + (py - 5000) * (py - 5000) + pz * pz) - 10;
			ok = leave.Split && !leave.Transfer && leave.Inputs.SequenceEqual(new[] { run.Commits[0].Change.Inputs[1] })
				&& leave.Outputs.Any(o => o.Members.SequenceEqual(new long[] { 5 })) && leaveGap > split && before <= split;
		}
		return new Outcome($"band recedes (split {split:F0} m)", run.Merges, run.Splits, ok && run.Born == 1,
			"one band transfer into Q, then one plain split off Q exactly when the ship passes the split distance from Q, one id born, nothing else",
			$"split off Q at a gap of {leaveGap:F1} m; " + run.Trace, run.Transfers);
	}

	/// <summary>
	/// T-0339 (design C2): a piece in the band of TWO standing layouts goes to the nearer one (both orders of the layout
	/// ids, so the id tie-break cannot pass it), and then stays: 20 more passes hovering +-15 m change nothing - the
	/// merge pass leaves it (no gap under D) and the band moves it only when a split cuts it off. Band off: a plain
	/// split, no transfer.
	/// </summary>
	private static Outcome BandNearest(double split)
	{
		static PartitionMembership.Member At(long id, double x, double y = 0, double speed = 0) => new PartitionMembership.Member(id, x, y, 0, 5, speed);
		List<string> trace = new List<string>();
		bool ok = true;
		int transfers = 0, changes = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		foreach ((double near, double far, bool band) in new[] { (6010.0, -8010.0, true), (-8010.0, 6010.0, true), (6010.0, -8010.0, false) })
		{
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = true, TransferBand = band };
			membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 1000), At(3, 12500, near), At(4, 12500, far) }, 1000);
			ulong nearer = membership.Layouts.Single(x => x.Members.Contains(near > 0 ? 3L : 4L)).Id;
			PartitionMembership.Change[] pass = membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 12500, 0, 100), At(3, 12500, near), At(4, 12500, far) }, 1100);
			trace.Add($"band {(band ? "on" : "off")}, Q at y={near:F0} and y={far:F0}: " + string.Join(", ", pass.Select(x => x.Why)));
			transfers += pass.Count(x => x.Transfer);
			if (band)
				Expect(pass.Length == 1 && IsBand(pass[0]) && pass[0].Inputs[1] == nearer && SameLayout(membership, 5, near > 0 ? 3 : 4),
					"one band transfer into the NEARER layout " + nearer);
			else
				Expect(pass.Length == 1 && pass[0].Split && !pass[0].Transfer, "band off: one plain split");
			for (int i = 0; i < 20; i++)
			{
				PartitionMembership.Change[] hover = membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 12500 + (i % 2 == 0 ? 15 : -15), 0, 100),
					At(3, 12500, near), At(4, 12500, far) }, 1101 + i);
				changes += hover.Length;
				Expect(hover.Length == 0, $"hover pass {i}: no change, got " + string.Join(", ", hover.Select(x => x.Why)));
			}
		}
		return new Outcome($"band nearest of two (split {split:F0} m)", 0, 0, ok,
			"the band transfer goes to the nearer of two layouts in the band, whatever their ids; no change over 20 hover passes; band off a plain split",
			$"{changes} change(s) while hovering; " + string.Join("; ", trace), transfers);
	}

	/// <summary>
	/// T-0339 (design C2): no ping-pong between two layouts whose split edges meet. P and Q are 24 km apart and a fast
	/// ship (200 m/s scalar: band capped at the split distance) hovers +-15 m around the point 12 km from both, so each
	/// pass it is past the split distance of the layout holding it and just inside the other's. Without the return margin
	/// it was moved P -> Q -> P every pass; with it the band needs Q nearer than P by D/3, so the ship is split off once
	/// (one id born) and stays alone.
	/// </summary>
	private static Outcome BandEdgeNoPingPong(double split)
	{
		static PartitionMembership.Member At(long id, double x, double speed = 0) => new PartitionMembership.Member(id, x, 0, 0, 5, speed);
		double edge = 200 + 5 + split + 5;
		ulong next = 1;
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = true, TransferBand = true };
		membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 1000), At(3, edge + split + 10), At(4, edge + split + 210) }, 1000);
		int transfers = 0, splits = 0;
		List<string> trace = new List<string>();
		for (int i = 0; i < 40; i++)
			foreach (PartitionMembership.Change change in membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, edge + (i % 2 == 0 ? 15 : -15), 200),
				At(3, edge + split + 10), At(4, edge + split + 210) }, 1100 + i))
			{
				if (change.Transfer) transfers++; else if (change.Split) splits++;
				trace.Add($"pass {i}: " + (change.Transfer ? "transfer " : change.Split ? "split " : "merge ") + string.Join(",", change.Inputs));
			}
		return new Outcome($"band edge no ping-pong (split {split:F0} m)", 0, splits, transfers == 0 && splits == 1 && next == 4,
			"over 40 passes at the meeting split edges: one plain split (one id born), no transfer",
			trace.Count == 0 ? "no commits" : string.Join("; ", trace.Take(8)) + (trace.Count > 8 ? $"; ... {trace.Count} commits" : ""), transfers);
	}

	/// <summary>
	/// T-0339 (design C2): the core test of a band candidate takes its contact at the band limit. Q is a core grid 14 km
	/// from the ship and a grid 5 km from it, the two 9 km apart (one layout through the band). The ship, split off P,
	/// is in the band of Q's near grid only; once that grid leaves, Q's core is past the split distance from the ship.
	/// Contact at D would see no contact and pass anything with a pair under the split distance; at the band limit the
	/// near grid is the contact, Q's core does not reach, so no transfer: a plain split, as the merge pass would refuse
	/// the merge with Q at D. Control: the same geometry with Q's core near the ship moves it.
	/// </summary>
	private static Outcome BandCores(double split)
	{
		static PartitionMembership.Member At(long id, double x, double y = 0, double speed = 0) => new PartitionMembership.Member(id, x, y, 0, 5, speed);
		List<string> trace = new List<string>();
		bool ok = true;
		int transfers = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		foreach ((double core, bool moves) in new[] { (14020.0, false), (-4010.0, true) })
		{
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = true, TransferBand = true };
			membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 1000), At(3, 12500, core), At(4, 12500, 5010) }, 1000);
			membership.Adopt(900, new long[] { 3, 4 }, 1000, mature: true);
			PartitionMembership.Change[] pass = membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 12500, 0, 100), At(3, 12500, core), At(4, 12500, 5010) }, 1100);
			transfers += pass.Count(x => x.Transfer);
			trace.Add($"Q core at y={core:F0}: " + string.Join(", ", pass.Select(x => x.Why)));
			if (moves) Expect(pass.Length == 1 && IsBand(pass[0]) && pass[0].Inputs[1] == 900, "control: Q's core reaches the ship, one band transfer into 900");
			else Expect(pass.Length == 1 && pass[0].Split && !pass[0].Transfer && !SameLayout(membership, 5, 4), "Q's core beyond the split distance: a plain split, the ship not in Q");
		}
		return new Outcome($"band cores (split {split:F0} m)", 0, 0, ok,
			"no band transfer when the target's only member within the band is band-attached to a core beyond the split distance; the control moves",
			string.Join("; ", trace), transfers);
	}

	/// <summary>
	/// T-0339 (design C2): a band transfer is stable - the band never reaches past the split distance, so the target does
	/// not split the piece off on the next pass. A fast ship (200 m/s: D + 200 x 60 = 15 km) jumps far from P (its source
	/// gap 29.8 km, so the return margin does not bind); Q at 11.5 km takes it and the next 10 passes change nothing; Q at
	/// 12.5 km (past the split distance, inside D + speed x T) does not take it - a plain split.
	/// </summary>
	private static Outcome BandStable(double split)
	{
		static PartitionMembership.Member At(long id, double x, double speed = 0) => new PartitionMembership.Member(id, x, 0, 0, 5, speed);
		List<string> trace = new List<string>();
		bool ok = true;
		int transfers = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		foreach (double gapQ in new[] { split - 500, split + 500 })
		{
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray()) { TransferEnabled = true, TransferBand = true };
			double q = 30000 + 10 + gapQ;
			membership.Evaluate(new[] { At(1, 0), At(2, 200), At(5, 1000), At(3, q), At(4, q + 200) }, 1000);
			PartitionMembership.Member[] census = { At(1, 0), At(2, 200), At(5, 30000, 200), At(3, q), At(4, q + 200) };
			PartitionMembership.Change[] pass = membership.Evaluate(census, 1100);
			transfers += pass.Count(x => x.Transfer);
			int after = 0;
			for (int i = 1; i <= 10; i++) after += membership.Evaluate(census, 1100 + i).Length;
			trace.Add($"Q at {gapQ:F0} m: " + string.Join(", ", pass.Select(x => x.Why)) + $"; {after} change(s) in the next 10 passes");
			if (gapQ < split) Expect(pass.Length == 1 && IsBand(pass[0]) && SameLayout(membership, 5, 3) && after == 0, "a band transfer into Q and no change after");
			else Expect(pass.Length == 1 && pass[0].Split && !pass[0].Transfer && after == 0, "past the split distance: a plain split, no transfer, no change after");
		}
		return new Outcome($"band stable (split {split:F0} m)", 0, 0, ok,
			"a band transfer is never undone by the next pass: the band stops at the split distance",
			string.Join("; ", trace), transfers);
	}
	/// <summary>
	/// T-0339 (merge hold-off, partition.mergeHoldoffSeconds): P = {1, 2} and the ship 5 start as one layout, Q = {3, 4}
	/// stands 20 km away. The ship flies past the split distance from P (one plain split, a new id N for the ship), then
	/// comes back. Each case below starts from that split and runs one or two more passes:
	/// knob 0: back within D (gap 2490 m) 10 s later merges at once, as before the knob;
	/// knob 60: the same pass holds the merge off (P and N stay two layouts, one held pair counted, once);
	/// knob 60: under D/2 (gap 1390 m) inside the window merges;
	/// knob 60: still at 2490 m 61 s after the split (window over) merges;
	/// knob 60: attached (same atomic group as P's member) inside the window merges;
	/// knob 60: within D of Q (a third layout, not a sibling) inside the window merges with Q;
	/// knob 60 lowered to 0 inside the window: merges on that pass (the knob is read per pass).
	/// The split pass itself is identical with the knob on and off (same changes, same Why text).
	/// </summary>
	private static Outcome MergeHoldoff(double split, bool transfer)
	{
		static PartitionMembership.Member At(long id, double x, double y = 0, long group = 0) => new PartitionMembership.Member(id, x, y, 0, 5, 0, group);
		List<string> trace = new List<string>();
		bool ok = true;
		int merges = 0, splits = 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		PartitionMembership.Member[] Census(double shipX, double shipY = 0, long shipGroup = 0) =>
			new[] { At(1, 0, 0, shipGroup), At(2, 200), At(3, 12500, 20000), At(4, 12700, 20000), At(5, shipX, shipY, shipGroup) };
		string splitWhy0 = null;
		foreach ((string name, double knob, double t, PartitionMembership.Member[] census, double knobThen, bool mergesWithP, bool mergesWithQ) in new[]
		{
			("knob 0, back within D", 0.0, 1110.0, Census(2700), 0.0, true, false),
			("knob 60, back within D", 60.0, 1110.0, Census(2700), 60.0, false, false),
			("knob 60, under D/2", 60.0, 1110.0, Census(1600), 60.0, true, false),
			("knob 60, window over", 60.0, 1161.0, Census(2700), 60.0, true, false),
			("knob 60, attached", 60.0, 1110.0, Census(2700, 0, 77), 60.0, true, false),
			("knob 60, third layout", 60.0, 1110.0, Census(12500, 17500), 60.0, false, true),
			("knob 60 lowered to 0", 60.0, 1110.0, Census(2700), 0.0, true, false),
		})
		{
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray())
				{ TransferEnabled = transfer, TransferBand = transfer, MergeHoldoffSeconds = knob };
			membership.Evaluate(Census(1000), 1000);
			ulong p = membership.Layouts.Single(x => x.Members.Contains(1)).Id;
			PartitionMembership.Change[] cut = membership.Evaluate(Census(12500), 1100);
			splits += cut.Count(x => x.Split);
			string why = string.Join(" | ", cut.Select(x => (x.Split ? "S" : "M") + "[" + string.Join(",", x.Inputs) + "]->["
				+ string.Join(";", x.Outputs.Select(o => o.Id + ":" + string.Join(",", o.Members))) + "] " + x.Why));
			splitWhy0 ??= why;
			Expect(why == splitWhy0, name + ": the split pass is the knob-0 split pass (" + why + ")");
			Expect(cut.Length == 1 && cut[0].Split && !cut[0].Transfer && !SameLayout(membership, 1, 5), name + ": one plain split of the ship off P");
			ulong n = membership.Layouts.Single(x => x.Members.Contains(5)).Id;
			membership.MergeHoldoffSeconds = knobThen;
			PartitionMembership.Change[] back = membership.Evaluate(census, t);
			merges += back.Count(x => !x.Split);
			trace.Add($"{name}: P={p} N={n}; t={t - 1100:F0} s after the split: "
				+ (back.Length == 0 ? "no change" : string.Join(", ", back.Select(x => (x.Split ? "split " : "merge ") + string.Join(",", x.Inputs))))
				+ $"; held {membership.HeldMerges} pair(s) this pass, {membership.HeldMergePairs} ever");
			if (mergesWithP)
				Expect(back.Length == 1 && !back[0].Split && back[0].Inputs.OrderBy(x => x).SequenceEqual(new[] { p, n }.OrderBy(x => x))
					&& SameLayout(membership, 1, 5) && membership.HeldMerges == 0 && membership.HeldMergePairs == 0, name + ": P and N merge on this pass, nothing held");
			else if (mergesWithQ)
				Expect(back.Length == 1 && !back[0].Split && SameLayout(membership, 3, 5) && !SameLayout(membership, 1, 5)
					&& membership.HeldMerges == 0, name + ": N merges with Q, nothing held");
			else
			{
				Expect(back.Length == 0 && !SameLayout(membership, 1, 5) && membership.HeldMerges == 1 && membership.HeldMergePairs == 1
					&& membership.HoldoffsStarted.Count == 1 && membership.HoldoffsStarted[0].A == Math.Min(p, n) && membership.HoldoffsStarted[0].B == Math.Max(p, n),
					name + ": the merge is held off (no change, one held pair, one first-hold record)");
				// Held again on the next pass, counted once; inside D/2 it merges at once.
				PartitionMembership.Change[] again = membership.Evaluate(census, t + 1);
				Expect(again.Length == 0 && membership.HeldMerges == 1 && membership.HeldMergePairs == 1 && membership.HoldoffsStarted.Count == 0,
					name + ": held again on the next pass, the pair counted once");
				PartitionMembership.Change[] close = membership.Evaluate(Census(1600), t + 2);
				merges += close.Count(x => !x.Split);
				Expect(close.Length == 1 && !close[0].Split && SameLayout(membership, 1, 5) && membership.HeldMerges == 0, name + ": under D/2 the held pair merges");
			}
		}
		return new Outcome($"merge hold-off{(transfer ? " +transfer +band" : "")} (split {split:F0} m)", merges, splits, ok,
			"knob 0 merges the returning piece at once; knob 60 holds it off inside the window above D/2 only, never against an attached grid or a third layout, and the split pass is the same",
			string.Join("; ", trace));
	}

	/// <summary>
	/// T-0339 (merge hold-off): one change as text - kind (S split, T transfer, M merge), inputs, outputs with their
	/// members and age, the members a transfer moved, and the reason text. Two passes made the same decision iff equal.
	/// </summary>
	internal static string ChangeKey(PartitionMembership.Change change) =>
		(change.Transfer ? "T" : change.Split ? "S" : "M") + "[" + string.Join(",", change.Inputs) + "]->["
		+ string.Join(";", change.Outputs.Select(o => o.Id + ":" + string.Join(",", o.Members) + "@" + o.CreatedAt.ToString("R", CultureInfo.InvariantCulture)))
		+ "] moved[" + string.Join(",", change.Moved) + "] " + change.Why;

	/// <summary>T-0339 (merge hold-off): the layouts of a membership as text (id, members, age, pins).</summary>
	internal static string LayoutsKey(PartitionMembership membership) => string.Join(" | ", membership.Layouts.OrderBy(x => x.Id)
		.Select(l => l.Id + ":" + string.Join(",", l.Members) + "@" + l.CreatedAt.ToString("R", CultureInfo.InvariantCulture)
			+ "/p[" + string.Join(",", l.Pending) + "]u" + l.PinnedUntil.ToString("R", CultureInfo.InvariantCulture)));

	/// <summary>
	/// T-0339 (merge hold-off): the invariant the hold-off keeps on one pass, or null when it holds.
	/// </summary>
	/// <remarks>
	/// "The same splits and transfers as knob 0, pass for pass" is NOT true once a merge has been held: the held arm then
	/// has two layouts (P and the returning piece N) where knob 0 has one, and the next departure from that content is a
	/// split of N in one arm and of the merged P in the other - the same members leave, under other input ids, and a piece
	/// that leaves N alone needs no split at all. What is true by construction is narrower and exact: the knob is read by
	/// the merge step only, after every adoption, split and transfer of the pass, so a held merge changes later decisions
	/// only through the layouts it left unmerged. <paramref name="shadow"/> is a knob-0 membership of the same flags, locks
	/// and allocator position, stood on the held arm's layouts before the pass (<see cref="PartitionMembership.Restore"/>):
	/// its split and transfer changes must equal the held arm's, and when the held arm held nothing this pass, the whole
	/// pass (every change and the layouts it leaves) must equal too.
	/// </remarks>
	internal static string HoldoffFault(PartitionMembership held, PartitionMembership.Change[] heldChanges,
		PartitionMembership shadow, PartitionMembership.Change[] shadowChanges)
	{
		string[] mine = heldChanges.Where(x => x.Split).Select(ChangeKey).ToArray();
		string[] knob0 = shadowChanges.Where(x => x.Split).Select(ChangeKey).ToArray();
		if (!mine.SequenceEqual(knob0))
			return "split/transfer decisions differ from knob 0 on the same layouts: [" + string.Join(" | ", mine) + "] vs ["
				+ string.Join(" | ", knob0) + "]";
		if (held.HeldMerges == 0 && (!heldChanges.Select(ChangeKey).SequenceEqual(shadowChanges.Select(ChangeKey))
				|| LayoutsKey(held) != LayoutsKey(shadow)))
			return "no merge held, yet the pass differs from knob 0 on the same layouts: [" + string.Join(" | ", heldChanges.Select(ChangeKey))
				+ "] vs [" + string.Join(" | ", shadowChanges.Select(ChangeKey)) + "]";
		return null;
	}

	/// <summary>One pass of <see cref="HoldoffArms"/>: the changes and layouts of the knob-0 arm and of the hold-off arm.</summary>
	private sealed record HoldoffPass(double T, PartitionMembership.Change[] Zero, PartitionMembership.Change[] Held,
		PartitionMembership.Layout[] ZeroLayouts, PartitionMembership.Layout[] HeldLayouts, int HeldMerges);

	/// <summary>
	/// T-0339 (merge hold-off): <paramref name="passes"/> through two memberships of the same flags, knob 0 and knob
	/// <paramref name="knob"/>, and before every pass a knob-0 shadow stood on the hold-off arm's layouts
	/// (<see cref="HoldoffFault"/>, faults returned).
	/// </summary>
	private static (List<HoldoffPass> Passes, List<string> Faults, PartitionMembership Held) HoldoffArms(
		IEnumerable<(double T, PartitionMembership.Member[] Census)> passes, double split, bool transfer, bool band, double knob = 30)
	{
		ulong nextZero = 1, nextHeld = 1;
		PartitionMembership Make(Func<int, ulong[]> allocate, double holdoff) => new PartitionMembership(Distance, (split - Distance) / 2, allocate)
			{ TransferEnabled = transfer, TransferBand = band, MergeHoldoffSeconds = holdoff };
		PartitionMembership zero = Make(count => Enumerable.Range(0, count).Select(_ => nextZero++).ToArray(), 0);
		PartitionMembership held = Make(count => Enumerable.Range(0, count).Select(_ => nextHeld++).ToArray(), knob);
		List<HoldoffPass> record = new List<HoldoffPass>();
		List<string> faults = new List<string>();
		foreach ((double t, PartitionMembership.Member[] census) in passes)
		{
			ulong nextShadow = nextHeld;
			PartitionMembership shadow = Make(count => Enumerable.Range(0, count).Select(_ => nextShadow++).ToArray(), 0);
			shadow.Restore(held.Layouts);
			PartitionMembership.Change[] knob0 = shadow.Evaluate(census, t);
			PartitionMembership.Change[] zeroChanges = zero.Evaluate(census, t);
			PartitionMembership.Change[] heldChanges = held.Evaluate(census, t);
			string fault = HoldoffFault(held, heldChanges, shadow, knob0);
			if (fault != null) faults.Add($"t={t - 1000:F1}: {fault}");
			record.Add(new HoldoffPass(t, zeroChanges, heldChanges, zero.Layouts.ToArray(), held.Layouts.ToArray(), held.HeldMerges));
		}
		return (record, faults, held);
	}

	private static PartitionMembership.Member Grid(long id, double x, double y = 0, double radius = 5) =>
		new PartitionMembership.Member(id, x, y, 0, radius, 0);

	private static bool Together(IEnumerable<PartitionMembership.Layout> layouts, long a, long b) =>
		layouts.Any(l => l.Members.Contains(a) && l.Members.Contains(b));

	/// <summary>Member sets of the layouts, ids ignored: two arms that hold the same content the same way are equal here.</summary>
	private static string Shape(IEnumerable<PartitionMembership.Layout> layouts) =>
		string.Join(" | ", layouts.Select(l => string.Join(",", l.Members)).Order(StringComparer.Ordinal));

	private static string Changes(PartitionMembership.Change[] changes) => string.Join(", ", changes.Select(x =>
		(x.Transfer ? "transfer " : x.Split ? "split " : "merge ") + string.Join(",", x.Inputs) + "->" + string.Join(",", x.Outputs.Select(o => o.Id))));

	/// <summary>
	/// T-0339 (merge hold-off): the r118 relay bench (T0339C-relay-transfer), whose hold-off-30 transfer arm once timed
	/// out in its setup. Pair A (two static grids 40 m apart), pair B the split distance + 2000 m away, and a 27-block ship
	/// 300 m off a pair; the ship starts parked at B, so in B's layout, and is teleported between the pairs (the bench's
	/// legs) - first 3 s in (before the 10 s age gate: no transfer target yet, a split and a later merge), then 12, 18,
	/// 3 (a return inside the window), 18 and 30 s apart. Knob 0 and knob 30 must make the same change on every pass,
	/// the shadow invariant must hold, no merge is ever held (no transfer and no split here makes two siblings that meet
	/// again), and after every leg the ship is with the pair it is parked at and the pairs are apart.
	/// </summary>
	private static Outcome HoldoffRelay(double split, bool transfer, bool band)
	{
		double separation = split + 2000;
		PartitionMembership.Member[] Census(bool atA) => new[]
			{ Grid(1, 0), Grid(2, 40), Grid(3, separation), Grid(4, separation + 40), Grid(5, atA ? 0 : separation, 300, 10) };
		double[] legs = { 1003, 1015, 1033, 1036, 1054, 1084 };
		List<(double, PartitionMembership.Member[])> passes = new List<(double, PartitionMembership.Member[])>();
		for (double t = 1000; t <= 1110; t += 1) passes.Add((t, Census(legs.Count(x => x <= t) % 2 == 1)));
		var run = HoldoffArms(passes, split, transfer, band);
		List<string> trace = new List<string>(run.Faults);
		foreach (HoldoffPass pass in run.Passes)
		{
			if (!pass.Zero.Select(ChangeKey).SequenceEqual(pass.Held.Select(ChangeKey)))
				trace.Add($"t={pass.T - 1000:F0}: knob 0 [{Changes(pass.Zero)}] but knob 30 [{Changes(pass.Held)}]");
			if (pass.Held.Length != 0) trace.Add($"t={pass.T - 1000:F0} {Changes(pass.Held)}");
		}
		bool settled = true;
		foreach (double end in legs.Skip(1).Select(x => x - 1).Append(1110))
		{
			HoldoffPass pass = run.Passes.Single(x => x.T == end);
			long pair = legs.Count(x => x <= end) % 2 == 1 ? 1 : 3;
			foreach (PartitionMembership.Layout[] layouts in new[] { pass.ZeroLayouts, pass.HeldLayouts })
				if (!Together(layouts, 5, pair) || !Together(layouts, 1, 2) || !Together(layouts, 3, 4) || Together(layouts, 1, 3))
				{
					settled = false;
					trace.Add($"NOT settled at t={end - 1000:F0}: {Shape(layouts)}");
				}
		}
		int merges = run.Passes.Sum(x => x.Held.Count(c => !c.Split || c.Transfer));
		int splits = run.Passes.Sum(x => x.Held.Count(c => c.Split));
		int transfers = run.Passes.Sum(x => x.Held.Count(c => c.Transfer));
		bool same = run.Passes.All(x => x.Zero.Select(ChangeKey).SequenceEqual(x.Held.Select(ChangeKey)));
		// Transfers on: the first leg (before the age gate) is a split and a merge, every later one a single transfer.
		bool shape = transfer ? transfers == legs.Length - 1 && splits == legs.Length && merges == legs.Length
			: transfers == 0 && splits == legs.Length && merges == legs.Length;
		return new Outcome($"hold-off relay{(transfer ? " +transfer" : "")}{(band ? " +band" : "")} (split {split:F0} m)", merges, splits,
			run.Faults.Count == 0 && same && settled && shape && run.Held.HeldMergePairs == 0,
			"knob 0 and knob 30 make the same change on every pass, nothing held, the ship with its pair after every leg, "
			+ (transfer ? "one split + merge, then one transfer per leg" : "one split + one merge per leg"),
			string.Join("; ", trace), transfers);
	}

	/// <summary>
	/// T-0339 (merge hold-off): siblings ARE recorded, and then split again or move a piece out inside the window, with
	/// knob 30. P = two grids + a convoy of three ships, Q = two grids 40 km away. The convoy leaves to 20 km (P splits:
	/// siblings P and N); a ship of N flies to Q (N must split, its piece goes to Q); a ship of N flies back within D (but
	/// beyond D/2) of P (N must split again, and its piece goes INTO its sibling P - a transfer is never held; transfers
	/// off it is a split and a merge with a layout that is not its sibling); a grid of P leaves (the other sibling splits).
	/// Every change must be the knob-0 change, and the shadow invariant must hold.
	/// </summary>
	private static Outcome HoldoffSiblingSplits(double split, bool transfer)
	{
		PartitionMembership.Member[] Census(double t)
		{
			(double X, double Y) s3 = t < 1012 ? (0, 300) : (20000, 0);
			(double X, double Y) s4 = t < 1012 ? (0, 500) : t < 1014 ? (20100, 0) : (40000, 2000);
			(double X, double Y) s7 = t < 1012 ? (0, 700) : t < 1016 ? (20200, 0) : (2500, 0);
			(double X, double Y) g2 = t < 1018 ? (200, 0) : (0, 20000);
			return new[] { Grid(1, 0), Grid(2, g2.X, g2.Y), Grid(3, s3.X, s3.Y), Grid(4, s4.X, s4.Y), Grid(5, 40000), Grid(6, 40200),
				Grid(7, s7.X, s7.Y) };
		}
		var run = HoldoffArms(Enumerable.Range(0, 31).Select(i => (1000.0 + i, Census(1000.0 + i))), split, transfer, transfer);
		List<string> trace = new List<string>(run.Faults);
		bool same = true;
		foreach (HoldoffPass pass in run.Passes)
		{
			if (!pass.Zero.Select(ChangeKey).SequenceEqual(pass.Held.Select(ChangeKey)))
			{
				same = false;
				trace.Add($"t={pass.T - 1000:F0}: knob 0 [{Changes(pass.Zero)}] but knob 30 [{Changes(pass.Held)}]");
			}
			if (pass.Held.Length != 0) trace.Add($"t={pass.T - 1000:F0} {Changes(pass.Held)}");
		}
		HoldoffPass At(double t) => run.Passes.Single(x => x.T == t);
		ulong n = At(1012).HeldLayouts.Single(l => l.Members.Contains(3)).Id;
		bool ok = run.Faults.Count == 0 && same && run.Held.HeldMergePairs == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		Expect(At(1012).Held.Count(x => x.Split) == 1 && !Together(At(1012).HeldLayouts, 1, 3), "t=12: P splits, the convoy is N");
		Expect(At(1014).Held.Any(x => x.Split && x.Inputs[0] == n) && Together(At(1014).HeldLayouts, 4, 5) && !Together(At(1014).HeldLayouts, 3, 4),
			"t=14: sibling N splits, its piece joins Q in the same pass");
		Expect(At(1016).Held.Any(x => x.Split && x.Inputs[0] == n && (!transfer || x.Transfer)) && Together(At(1016).HeldLayouts, 1, 7)
			&& !Together(At(1016).HeldLayouts, 3, 7), "t=16: sibling N splits again, its piece joins sibling P in the same pass");
		Expect(At(1018).Held.Any(x => x.Split && !x.Transfer) && !Together(At(1018).HeldLayouts, 1, 2), "t=18: sibling P splits, the grid leaves");
		Expect(Shape(At(1030).HeldLayouts) == "1,7 | 2 | 3 | 4,5,6", "the end: {1,7} {2} {3} {4,5,6}, got " + Shape(At(1030).HeldLayouts));
		return new Outcome($"hold-off siblings split again{(transfer ? " +transfer +band" : "")} (split {split:F0} m)",
			run.Passes.Sum(x => x.Held.Count(c => !c.Split || c.Transfer)), run.Passes.Sum(x => x.Held.Count(c => c.Split)), ok,
			"every split and transfer of a sibling is made on the pass knob 0 makes it, identically; nothing held",
			string.Join("; ", trace), run.Passes.Sum(x => x.Held.Count(c => c.Transfer)));
	}

	/// <summary>
	/// T-0339 (merge hold-off): a merge IS held, and the held sibling then has to split. P = two grids + two ships, Q =
	/// two grids 40 km away. The ships leave to 20 km (siblings P, N), come back to 2.7 km (knob 30 holds N off P; knob 0
	/// merges), then one ship flies to Q while the other stays: knob 30 splits N (its piece to Q), knob 0 splits P - the
	/// same ship leaves in the same pass under other input ids, which is why the arms are compared by the shadow invariant
	/// and by content, not change by change. Then the other ship leaves into empty space: knob 0 splits P again, knob 30
	/// has nothing to split (N is that ship alone). Both arms must end with the same content in the same groups.
	/// </summary>
	private static Outcome HoldoffHeldThenSplits(double split, bool transfer)
	{
		PartitionMembership.Member[] Census(double t)
		{
			(double X, double Y) s3 = t < 1012 ? (0, 300) : t < 1014 ? (20000, 0) : t < 1018 ? (2700, 0) : (-20000, 0);
			(double X, double Y) s4 = t < 1012 ? (0, 500) : t < 1014 ? (20100, 0) : t < 1016 ? (2700, 150) : (40000, 2000);
			return new[] { Grid(1, 0), Grid(2, 200), Grid(3, s3.X, s3.Y), Grid(4, s4.X, s4.Y), Grid(5, 40000), Grid(6, 40200) };
		}
		var run = HoldoffArms(Enumerable.Range(0, 25).Select(i => (1000.0 + i, Census(1000.0 + i))), split, transfer, transfer);
		List<string> trace = new List<string>(run.Faults);
		foreach (HoldoffPass pass in run.Passes.Where(x => x.Zero.Length != 0 || x.Held.Length != 0))
			trace.Add($"t={pass.T - 1000:F0} knob 0 [{Changes(pass.Zero)}] knob 30 [{Changes(pass.Held)}] held {pass.HeldMerges}");
		HoldoffPass At(double t) => run.Passes.Single(x => x.T == t);
		ulong n = At(1012).HeldLayouts.Single(l => l.Members.Contains(3)).Id;
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		Expect(At(1014).HeldMerges == 1 && !Together(At(1014).HeldLayouts, 1, 3) && Together(At(1014).ZeroLayouts, 1, 3),
			"t=14: knob 30 holds N off P, knob 0 merges them");
		Expect(At(1016).Held.Any(x => x.Split && x.Inputs[0] == n && (!transfer || x.Transfer)) && At(1016).Zero.Any(x => x.Split),
			"t=16: the held sibling N splits (knob 0 splits P)");
		foreach ((string arm, PartitionMembership.Layout[] layouts) in new[] { ("knob 0", At(1016).ZeroLayouts), ("knob 30", At(1016).HeldLayouts) })
			Expect(Together(layouts, 4, 5) && !Together(layouts, 3, 4), arm + ", t=16: the ship that flew to Q is with Q in the same pass");
		Expect(At(1018).Zero.Any(x => x.Split) && At(1018).Held.All(x => !x.Split), "t=18: knob 0 splits P, knob 30 has nothing to split");
		Expect(Shape(At(1024).HeldLayouts) == Shape(At(1024).ZeroLayouts) && Shape(At(1024).HeldLayouts) == "1,2 | 3 | 4,5,6",
			"the end: both arms {1,2} {3} {4,5,6}, got " + Shape(At(1024).ZeroLayouts) + " / " + Shape(At(1024).HeldLayouts));
		return new Outcome($"hold-off held, then the sibling splits{(transfer ? " +transfer +band" : "")} (split {split:F0} m)",
			run.Passes.Sum(x => x.Held.Count(c => !c.Split || c.Transfer)), run.Passes.Sum(x => x.Held.Count(c => c.Split)), ok,
			"the held sibling splits on the pass the ship leaves, the shadow invariant holds, both arms end with the same groups",
			string.Join("; ", trace), run.Passes.Sum(x => x.Held.Count(c => c.Transfer)));
	}

	/// <summary>
	/// T-0339 (split deferral): a layout due to split (members in two groups at the split distance) that the pass leaves
	/// whole is listed in <see cref="PartitionMembership.DeferredSplits"/> with its reason - locked, pending (a pinned member)
	/// or no-ids (the allocator had none; the planned transfers wait with the split) - and the seconds the run of deferrals
	/// has lasted; a layout that is not due is never listed, a split made lists nothing, and the log throttle names a layout
	/// once per reason and again every 60 s.
	/// </summary>
	/// <summary>
	/// T-0339 (class B diag, CH 2026-10-08 10:21): <see cref="PartitionMembership.MintedPieces"/> names, for every id a split
	/// mints, the nearest other layout and the first transfer gate it failed. Known positives: a piece within D of a LOCKED
	/// standing layout is minted with gate=locked; a piece far from every layout is minted with gate=reach. Control: the same
	/// piece with the layout unlocked is transferred, and nothing is listed (a transfer mints no id).
	/// </summary>
	private static Outcome MintedPieceDiag(double split)
	{
		List<string> trace = new List<string>();
		bool ok = true;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		string Listed(PartitionMembership m) => string.Join(",", m.MintedPieces.Select(x => x.Source + ">" + x.Nearest + ":" + x.Gate));
		(PartitionMembership M, ulong P, ulong Q, PartitionMembership.Change[] Changes) Run(double x3, bool lockQ)
		{
			ulong next = 1;
			HashSet<ulong> locked = new HashSet<ulong>();
			PartitionMembership m = new PartitionMembership(Distance, (split - Distance) / 2,
				count => Enumerable.Range(0, count).Select(_ => next++).ToArray(), id => locked.Contains(id)) { TransferEnabled = true };
			PartitionMembership.Member[] Census(double x) => new[] { Grid(1, 0), Grid(2, 200), Grid(3, x, 300), Grid(5, 40000), Grid(6, 40200) };
			m.Evaluate(Census(0), 1000);
			ulong p = m.Layouts.Single(l => l.Members.Contains(1)).Id, q = m.Layouts.Single(l => l.Members.Contains(5)).Id;
			if (lockQ) locked.Add(q);
			PartitionMembership.Change[] changes = m.Evaluate(Census(x3), 1012);
			return (m, p, q, changes);
		}
		// Member 3 leaves P (>= split distance) to 1 km from Q: a transfer into Q unless Q is locked.
		var open = Run(39000, false);
		Expect(open.Changes.Length == 1 && open.Changes[0].Transfer && open.M.MintedPieces.Count == 0,
			"control: Q unlocked -> transferred, nothing minted (" + Changes(open.Changes) + " | " + Listed(open.M) + ")");
		var shut = Run(39000, true);
		Expect(shut.Changes.Length == 1 && shut.Changes[0].Split && !shut.Changes[0].Transfer && Listed(shut.M) == $"{shut.P}>{shut.Q}:locked"
			&& shut.M.MintedPieces[0].Members == 1 && shut.M.MintedPieces[0].Gap > 1000 && shut.M.MintedPieces[0].Gap < 1050,
			"KNOWN POSITIVE: Q locked -> minted, nearest Q, gate=locked, gap ~1034 m (" + Changes(shut.Changes) + " | " + Listed(shut.M)
			+ " gap " + (shut.M.MintedPieces.Count == 0 ? "-" : shut.M.MintedPieces[0].Gap.ToString("F0", CultureInfo.InvariantCulture)) + ")");
		// Member 3 leaves P to 20 km from both: minted, nearest Q (20 km < 20 km+), gate=reach.
		var far = Run(20000, false);
		Expect(far.Changes.Length == 1 && far.Changes[0].Split && far.M.MintedPieces.Count == 1 && far.M.MintedPieces[0].Gate == "reach",
			"far from every layout: minted with gate=reach (" + Changes(far.Changes) + " | " + Listed(far.M) + ")");
		// The next pass lists nothing (the list is per pass).
		far.M.Evaluate(new[] { Grid(1, 0), Grid(2, 200), Grid(3, 20000, 300), Grid(5, 40000), Grid(6, 40200) }, 1013);
		Expect(far.M.MintedPieces.Count == 0, "a pass that mints nothing lists nothing (" + Listed(far.M) + ")");
		return new Outcome($"minted piece diag (split {split:F0} m)", 0, 0, ok,
			"every minted id names its nearest layout and the transfer gate it failed; a transfer lists nothing",
			trace.Count == 0 ? "as expected" : string.Join("; ", trace));
	}

	private static Outcome SplitDeferral(double split)
	{
		List<string> trace = new List<string>();
		bool ok = true;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		string Listed(PartitionMembership m) => string.Join(",", m.DeferredSplits.Select(x =>
			x.Layout + ":" + x.Reason + ":" + x.Groups + ":" + x.Since.ToString("F1", CultureInfo.InvariantCulture)));
		ulong next = 1;
		bool dry = false;
		HashSet<ulong> locked = new HashSet<ulong>();
		PartitionMembership membership = new PartitionMembership(Distance, (split - Distance) / 2,
			count => dry ? null : Enumerable.Range(0, count).Select(_ => next++).ToArray(), id => locked.Contains(id)) { TransferEnabled = true };
		PartitionMembership.Member[] Census(double x3, double x4) => new[] { Grid(1, 0), Grid(2, 200), Grid(3, x3, 300), Grid(4, x4, 600),
			Grid(5, 40000), Grid(6, 40200) };
		membership.Evaluate(Census(0, 0), 1000);
		ulong p = membership.Layouts.Single(x => x.Members.Contains(1)).Id;
		locked.Add(p);
		PartitionMembership.Change[] changes = membership.Evaluate(Census(0, 0), 1011);
		Expect(changes.Length == 0 && membership.DeferredSplits.Count == 0, "a locked layout that is not due is not listed: " + Listed(membership));
		changes = membership.Evaluate(Census(20000, 20000), 1012);
		Expect(changes.Length == 0 && Listed(membership) == $"{p}:locked:2:0.0", "locked and due: listed, since 0 (" + Listed(membership) + ")");
		changes = membership.Evaluate(Census(20000, 20000), 1013.5);
		Expect(changes.Length == 0 && Listed(membership) == $"{p}:locked:2:1.5", "still locked: since 1.5 (" + Listed(membership) + ")");
		locked.Clear();
		dry = true;
		changes = membership.Evaluate(Census(20000, 20000), 1014);
		Expect(changes.Length == 0 && Listed(membership) == $"{p}:no-ids:2:2.0", "no fresh id: listed, the run goes on (" + Listed(membership) + ")");
		dry = false;
		changes = membership.Evaluate(Census(20000, 20000), 1015);
		Expect(changes.Length == 1 && changes[0].Split && !changes[0].Transfer && membership.DeferredSplits.Count == 0,
			"ids back: the split is made, nothing listed (" + Changes(changes) + ")");
		// Three groups: one keeps the id, one is planned as a transfer into Q, one needs a fresh id. Without the id the
		// planned transfer waits with the split, and nothing of the layout changes.
		ulong next2 = 100;
		PartitionMembership second = new PartitionMembership(Distance, (split - Distance) / 2,
			count => dry ? null : Enumerable.Range(0, count).Select(_ => next2++).ToArray()) { TransferEnabled = true };
		PartitionMembership.Member[] Leaving(bool gone, params PartitionMembership.Member[] more) => new[] { Grid(1, 0), Grid(2, 200),
			gone ? Grid(3, 40000, 2000) : Grid(3, 0, 300), gone ? Grid(4, -20000) : Grid(4, 0, 600), Grid(5, 40000), Grid(6, 40200) }
			.Concat(more).ToArray();
		second.Evaluate(Leaving(false), 2000);
		ulong p2 = second.Layouts.Single(x => x.Members.Contains(1)).Id, q2 = second.Layouts.Single(x => x.Members.Contains(5)).Id;
		dry = true;
		changes = second.Evaluate(Leaving(true), 2012);
		Expect(changes.Length == 0 && Listed(second) == $"{p2}:no-ids:3:0.0", "no fresh id: the planned transfer waits with the split (" + Listed(second) + ")");
		dry = false;
		changes = second.Evaluate(Leaving(true), 2013);
		Expect(changes.Length == 2 && changes[0].Transfer && changes[0].Inputs[1] == q2 && changes[1].Split && !changes[1].Transfer
			&& second.DeferredSplits.Count == 0, "ids back: the transfer and the split are made (" + Changes(changes) + ")");
		// Pinned: a layout adopted with a member that has not materialized is not split, and is listed as pending.
		second.Adopt(9000, new long[] { 10, 11 }, 2013.5, true, new long[] { 999 });
		changes = second.Evaluate(Leaving(true, Grid(10, 90000), Grid(11, 110000)), 2014);
		Expect(changes.Length == 0 && Listed(second) == "9000:pending:2:0.0", "pinned and due: listed as pending (" + Listed(second) + ")");
		// The log throttle.
		Dictionary<ulong, (string, double)> logged = new Dictionary<ulong, (string, double)>();
		int Logs(string reason, double now) => PartitionMembership.DeferralsToLog(reason == null ? Array.Empty<(ulong, string, int, double)>()
			: new[] { (7UL, reason, 2, 0.0) }, logged, now).Count;
		int[] lines = { Logs("locked", 100), Logs("locked", 130), Logs("no-ids", 131), Logs("no-ids", 190), Logs("no-ids", 191),
			Logs(null, 192), Logs("no-ids", 193) };
		Expect(lines.SequenceEqual(new[] { 1, 0, 1, 0, 1, 0, 1 }), "log: first, reason change, every 60 s, again after a gap: " + string.Join(",", lines));
		return new Outcome($"split deferral listed (split {split:F0} m)", 0, 0, ok,
			"locked / no-ids / pending deferrals listed with since, nothing listed when not due or when made, the log throttled",
			trace.Count == 0 ? "as expected" : string.Join("; ", trace));
	}

	/// <summary>
	/// T-0339 (split hysteresis): the invariant the hysteresis keeps on one pass, or null when it holds. <paramref name="shadow"/>
	/// is a knob-0 membership of the same flags and allocator position stood on the hysteresis arm's layouts before the pass.
	/// A pass that holds nothing (no layout kept whole or in part, HysteresisHeld 0 - a run that ends this pass included)
	/// must be the knob-0 pass exactly: every change and the layouts it leaves. A pass that holds must make the same
	/// transfers: the hold only keeps fresh pieces with their kept group, never moves or blocks a transfer.
	/// </summary>
	internal static string HysteresisFault(PartitionMembership held, PartitionMembership.Change[] heldChanges,
		PartitionMembership shadow, PartitionMembership.Change[] shadowChanges)
	{
		if (held.HysteresisHeld == 0)
			return heldChanges.Select(ChangeKey).SequenceEqual(shadowChanges.Select(ChangeKey)) && LayoutsKey(held) == LayoutsKey(shadow) ? null
				: "nothing held, yet the pass differs from knob 0 on the same layouts: [" + string.Join(" | ", heldChanges.Select(ChangeKey))
					+ "] vs [" + string.Join(" | ", shadowChanges.Select(ChangeKey)) + "]";
		string[] mine = heldChanges.Where(x => x.Transfer).Select(ChangeKey).ToArray(), knob0 = shadowChanges.Where(x => x.Transfer).Select(ChangeKey).ToArray();
		return mine.SequenceEqual(knob0) ? null
			: "a held pass made other transfers than knob 0: [" + string.Join(" | ", mine) + "] vs [" + string.Join(" | ", knob0) + "]";
	}

	/// <summary>One pass of <see cref="HysteresisArms"/>: both arms' changes and layouts, the hysteresis arm's held count and log events.</summary>
	private sealed record HysteresisPass(double T, PartitionMembership.Change[] Zero, PartitionMembership.Change[] Held,
		PartitionMembership.Layout[] ZeroLayouts, PartitionMembership.Layout[] HeldLayouts, int HeldCount,
		(ulong Layout, int Piece, ulong Target, double Gap, double Rate, string Outcome, double HeldS)[] Events, int Call = 0);

	/// <summary>
	/// T-0339 (split hysteresis): one pass a second for <paramref name="seconds"/> through a knob-0 membership and one with
	/// partition.splitHysteresisSeconds = <paramref name="knob"/>, same flags (transfers on, the band as given), each member's
	/// speed its displacement over the last second. Before every pass a knob-0 shadow is stood on the hysteresis arm's
	/// layouts (<see cref="HysteresisFault"/>). Independently of the membership's own bookkeeping, every layout of the
	/// hysteresis arm whose members lie in two or more groups at the split distance after a pass (kept whole although due)
	/// must have had a "held" event less than <paramref name="knob"/> seconds before: a hold never outlives its window.
	/// Born: ids allocated after the first pass.
	/// T-0339i: one game frame every <paramref name="framesPerPass"/> frames (60 a second), evaluated <paramref name="callsPerFrame"/>
	/// times with the same positions and the same game time (the frame / 60), the wall clock 3 ms further on each call - as
	/// the node runs the pass from several triggers inside one frame.
	/// </summary>
	private static (List<HysteresisPass> Passes, List<string> Faults, PartitionMembership Held, int BornZero, int BornHeld) HysteresisArms(
		Func<double, (long Id, double X, double Y)[]> bodies, double seconds, double split, bool band, double knob = 60,
		int framesPerPass = 60, int callsPerFrame = 1)
	{
		ulong nextZero = 1, nextHeld = 1, firstZero = 0, firstHeld = 0;
		PartitionMembership Make(Func<int, ulong[]> allocate, double hysteresis) => new PartitionMembership(Distance, (split - Distance) / 2, allocate)
			{ TransferEnabled = true, TransferBand = band, SplitHysteresisSeconds = hysteresis };
		PartitionMembership zero = Make(count => Enumerable.Range(0, count).Select(_ => nextZero++).ToArray(), 0);
		PartitionMembership held = Make(count => Enumerable.Range(0, count).Select(_ => nextHeld++).ToArray(), knob);
		List<HysteresisPass> record = new List<HysteresisPass>();
		List<string> faults = new List<string>();
		Dictionary<ulong, double> heldSince = new Dictionary<ulong, double>();
		for (int frame = 0; frame <= seconds * 60; frame += framesPerPass)
		for (int call = 0; call < callsPerFrame; call++)
		{
			double t = frame / 60.0;
			Dictionary<long, (double X, double Y)> before = bodies(Math.Max(0, t - 1)).ToDictionary(x => x.Id, x => (x.X, x.Y));
			PartitionMembership.Member[] census = bodies(t).Select(b => new PartitionMembership.Member(b.Id, b.X, b.Y, 0, 5,
				Math.Sqrt((b.X - before[b.Id].X) * (b.X - before[b.Id].X) + (b.Y - before[b.Id].Y) * (b.Y - before[b.Id].Y)))).ToArray();
			double now = 1000 + t + 0.003 * call, game = 500 + t;
			ulong nextShadow = nextHeld;
			PartitionMembership shadow = Make(count => Enumerable.Range(0, count).Select(_ => nextShadow++).ToArray(), 0);
			shadow.Restore(held.Layouts);
			PartitionMembership.Change[] knob0 = shadow.Evaluate(census, now, game);
			PartitionMembership.Change[] zeroChanges = zero.Evaluate(census, now, game);
			PartitionMembership.Change[] heldChanges = held.Evaluate(census, now, game);
			if (frame == 0 && call == 0) { firstZero = nextZero; firstHeld = nextHeld; }
			string fault = HysteresisFault(held, heldChanges, shadow, knob0);
			if (fault != null) faults.Add($"t={t:F0}: {fault}");
			foreach (var e in held.HysteresisEvents.Where(x => x.Outcome == "held")) heldSince[e.Layout] = now;
			foreach (PartitionMembership.Layout layout in held.Layouts)
			{
				if (SplitGroups(layout.Members.Select(id => census.Single(m => m.Id == id)).ToArray(), split) < 2) continue;
				if (!heldSince.TryGetValue(layout.Id, out double since) || !(now - since < knob))
					faults.Add($"t={t:F0}: layout {layout.Id} [{string.Join(",", layout.Members)}] is due and whole without a hold started under {knob:F0} s before");
			}
			record.Add(new HysteresisPass(t, zeroChanges, heldChanges, zero.Layouts.ToArray(), held.Layouts.ToArray(), held.HysteresisHeld,
				held.HysteresisEvents.ToArray(), call));
		}
		if (held.HysteresisHeldTotal != record.Sum(x => x.Events.Count(e => e.Outcome == "held")))
			faults.Add("HysteresisHeldTotal " + held.HysteresisHeldTotal + " is not the number of held events");
		return (record, faults, held, (int)(nextZero - firstZero), (int)(nextHeld - firstHeld));
	}

	/// <summary>T-0339 (split hysteresis): the groups of <paramref name="members"/> at the split distance, by plain pairwise union.</summary>
	private static int SplitGroups(PartitionMembership.Member[] members, double split)
	{
		int[] parent = Enumerable.Range(0, members.Length).ToArray();
		int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
		for (int i = 0; i < members.Length; i++)
			for (int j = i + 1; j < members.Length; j++)
			{
				double dx = members[i].X - members[j].X, dy = members[i].Y - members[j].Y, dz = members[i].Z - members[j].Z;
				if (Math.Sqrt(dx * dx + dy * dy + dz * dz) - members[i].Radius - members[j].Radius <= split) parent[Find(i)] = Find(j);
			}
		return Enumerable.Range(0, members.Length).Select(Find).Distinct().Count();
	}

	private static string Events(IEnumerable<HysteresisPass> passes) => string.Join("; ", passes.SelectMany(p => p.Events.Select(e =>
		$"t={p.T:0.##}{(p.Call == 0 ? "" : "#" + p.Call)} {e.Outcome} partition={e.Layout} piece={e.Piece} target={e.Target} gapM={e.Gap:F0} closingMps={e.Rate:F1} heldS={e.HeldS:F1}")));

	private static string Trace(List<HysteresisPass> passes) => string.Join("; ", passes.Where(x => x.Zero.Length != 0 || x.Held.Length != 0)
		.Select(x => $"t={x.T:F0} knob 0 [{Changes(x.Zero)}] knob 60 [{Changes(x.Held)}]")) + "; events: " + Events(passes);

	/// <summary>
	/// T-0339 (split hysteresis): P = two grids at x = 0 and -200, Q = two grids at x = 30220 and 30420; the ship 5 starts
	/// 1 km off P and flies at Q at 300 m/s, parking at x = 28000 (2 km off Q). It passes the split distance from P at t = 37
	/// (x = 12100) with Q 18 km ahead: beyond the transfer reach (the band's too: its source gap caps it at 11 km), so
	/// knob 0 splits it off under a NEW id and merges that id into Q when it comes within D. Knob 60 holds the split (the
	/// piece reaches D in 50 s) and transfers the ship into Q when it is within reach (with the band at 12 km, t = 58;
	/// design C at D, t = 88): no id is allocated after the first pass, one hold, one transfer.
	/// </summary>
	private static Outcome HysteresisClosing(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(5L, Math.Min(1000 + 300 * t, 28000), 0.0) };
		var run = HysteresisArms(Bodies, 100, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		HysteresisPass At(double t) => run.Passes.Single(x => x.T == t);
		double arrive = band ? 58 : 88;
		Expect(At(37).Zero.Length == 1 && At(37).Zero[0].Split && !At(37).Zero[0].Transfer && run.BornZero == 1, "knob 0: a plain split at t=37, one new id");
		Expect(At(37).Held.Length == 0 && At(37).HeldCount == 1 && At(37).Events.Length == 1 && At(37).Events[0].Outcome == "held"
			&& At(37).Events[0].Piece == 1 && At(37).Events[0].Target == At(37).HeldLayouts.Single(l => l.Members.Contains(3)).Id, "knob 60: held at t=37, nothing changes, target Q");
		Expect(run.Passes.Where(x => x.T > 37 && x.T < arrive).All(x => x.Held.Length == 0 && x.HeldCount == 1 && x.Events.Length == 0),
			"knob 60: held without a change until t=" + arrive);
		Expect(At(arrive).Held.Length == 1 && At(arrive).Held[0].Transfer && At(arrive).Events.Single().Outcome == "transferred",
			"knob 60: transferred into Q at t=" + arrive);
		Expect(run.BornHeld == 0 && run.Held.HysteresisHeldTotal == 1 && run.Held.HysteresisTransferred == 1 && run.Held.HysteresisExpired == 0
			&& run.Held.HysteresisReleased == 0, $"knob 60: no new id, counters held 1 transferred 1 (born {run.BornHeld}, held {run.Held.HysteresisHeldTotal}, "
			+ $"transferred {run.Held.HysteresisTransferred}, expired {run.Held.HysteresisExpired}, released {run.Held.HysteresisReleased})");
		Expect(Together(At(100).HeldLayouts, 5, 3) && Together(At(100).ZeroLayouts, 5, 3) && !Together(At(100).HeldLayouts, 5, 1), "the end: the ship with Q in both arms");
		return new Outcome($"split hysteresis closing{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"knob 0: split + new id + merge; knob 60: held, then transferred, no new id", trace.Count == run.Faults.Count && ok ? Events(run.Passes) : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339j (split hysteresis inside D; r120 2026-10-07 16:03-16:04): P = two grids at x = 0 and -200; Q = two grids at
	/// x = 30220 and 30420 plus a traveller 6 that starts at x = 29000 (inside D of Q's core, so it is Q's) and flies at P,
	/// parking at x = 20000 by t = 45 - still within the split distance of Q's core, a D-component of its own. The ship 5
	/// leaves P at 300 m/s (split due at t = 37, held: closing on Q through 6) and crosses D of 6 at x ~ 17000 (t ~ 54) while
	/// Q's core is still ~13 km off: the transfer's core test refuses Q (no core of Q reaches the ship within the split
	/// distance) until x ~ 18200 (t ~ 58). t0339i ended the run "not-closing" on the crossing pass (gap &lt; D is not
	/// "closing"), split the ship off under a NEW id and merged that id into Q seconds later. t0339j keeps holding inside D:
	/// one hold, then "transferred", no "not-closing", no id born.
	/// </summary>
	private static Outcome HysteresisInsideD(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(6L, Math.Max(29000 - 200 * t, 20000), 0.0), (5L, Math.Min(1000 + 300 * t, 28000), 0.0) };
		var run = HysteresisArms(Bodies, 100, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		var events = run.Passes.SelectMany(p => p.Events.Select(e => (p.T, e.Outcome, e.Gap))).ToList();
		Expect(run.BornZero >= 1, $"knob 0: the ship split off under a new id (born {run.BornZero})");
		Expect(events.Count(e => e.Outcome == "held") == 1 && events.First().Outcome == "held", "knob 60: one hold");
		Expect(events.All(e => e.Outcome != "not-closing"), "knob 60: the hold does not end 'not-closing' inside D");
		Expect(events.Count(e => e.Outcome == "transferred") == 1, "knob 60: the hold ends 'transferred'");
		Expect(run.Passes.Any(p => p.HeldCount == 1 && p.HeldLayouts.Any(l => l.Members.Contains(5) && l.Members.Contains(1))
			&& Bodies(p.T).Single(b => b.Item1 == 5).Item2 >= 17100), "knob 60: the ship is still held with P on a pass inside D of the traveller");
		Expect(run.BornHeld == 0 && run.Held.HysteresisHeldTotal == 1 && run.Held.HysteresisTransferred == 1 && run.Held.HysteresisReleased == 0
			&& run.Held.HysteresisExpired == 0, $"knob 60: no new id, counters held 1 transferred 1 released 0 (born {run.BornHeld}, held "
			+ $"{run.Held.HysteresisHeldTotal}, transferred {run.Held.HysteresisTransferred}, released {run.Held.HysteresisReleased}, expired {run.Held.HysteresisExpired})");
		HysteresisPass end = run.Passes.Last();
		Expect(Together(end.HeldLayouts, 5, 3) && Together(end.ZeroLayouts, 5, 3), "the end: the ship with Q in both arms");
		return new Outcome($"split hysteresis inside D{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"held, kept inside D while Q's core test refuses, then transferred; no new id", trace.Count == run.Faults.Count && ok ? Events(run.Passes) : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339j: as <see cref="HysteresisInsideD"/>, but the ship stops at x = 17500 - inside D of the traveller, never within
	/// the split distance of Q's core: the transfer is refused on every pass. The hold must end "expired" at N (not
	/// "not-closing" on the crossing pass), and the layout then splits as knob 0 did.
	/// </summary>
	private static Outcome HysteresisInsideDNeverTaken(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(6L, Math.Max(29000 - 200 * t, 20000), 0.0), (5L, Math.Min(1000 + 300 * t, 17500), 0.0) };
		var run = HysteresisArms(Bodies, 110, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		var events = run.Passes.SelectMany(p => p.Events.Select(e => (p.T, e.Outcome, e.HeldS))).ToList();
		Expect(events.Count(e => e.Outcome == "held") == 1, "knob 60: one hold");
		Expect(events.All(e => e.Outcome != "not-closing" && e.Outcome != "transferred"), "knob 60: neither 'not-closing' nor 'transferred'");
		Expect(events.Count(e => e.Outcome == "expired") == 1 && events.Single(e => e.Outcome == "expired").HeldS >= 60
			&& events.Single(e => e.Outcome == "expired").HeldS < 61, "knob 60: 'expired' at N = 60 s");
		Expect(run.BornHeld == 1 && run.Held.HysteresisExpired == 1, $"knob 60: the split after expiry mints the one id (born {run.BornHeld})");
		return new Outcome($"split hysteresis inside D never taken{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"held inside D until N, then expired and split as knob 0", trace.Count == run.Faults.Count && ok ? Events(run.Passes) : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339 (split hysteresis): the same P and Q, the ship leaves P at right angles to Q (y up at 300 m/s): its gap to Q
	/// grows. Knob 60 must make every decision of knob 0 on the same pass - the split at t = 37 included - and hold nothing.
	/// </summary>
	private static Outcome HysteresisNotClosing(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(5L, 0.0, 1000 + 300 * t) };
		var run = HysteresisArms(Bodies, 60, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool same = run.Passes.All(x => x.Zero.Select(ChangeKey).SequenceEqual(x.Held.Select(ChangeKey)));
		bool split37 = run.Passes.Single(x => x.T == 37).Held.Count(x => x.Split) == 1;
		bool ok = run.Faults.Count == 0 && same && split37 && run.Held.HysteresisHeldTotal == 0 && run.Passes.All(x => x.Events.Length == 0);
		return new Outcome($"split hysteresis not closing{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"knob 60 identical to knob 0 on every pass (the split at t=37), nothing held", ok ? "as expected" : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339 (split hysteresis): the ship flies at Q at 100 m/s: at the split (t = 111, Q 18 km ahead) it would need 151 s
	/// to reach D, more than the 60 s hold. Not closing by the rule: split on the same pass as knob 0, nothing held.
	/// </summary>
	private static Outcome HysteresisTooSlow(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(5L, 1000 + 100 * t, 0.0) };
		var run = HysteresisArms(Bodies, 130, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool same = run.Passes.All(x => x.Zero.Select(ChangeKey).SequenceEqual(x.Held.Select(ChangeKey)));
		bool split111 = run.Passes.Single(x => x.T == 111).Held.Count(x => x.Split && !x.Transfer) == 1;
		bool ok = run.Faults.Count == 0 && same && split111 && run.Held.HysteresisHeldTotal == 0;
		return new Outcome($"split hysteresis too slow{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"a piece that cannot reach D within the hold splits on the knob-0 pass (t=111)", ok ? "as expected" : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339 (split hysteresis), expiry (design C, no band): the ship passes the split distance from P at t = 37 at 300 m/s
	/// and brakes at a constant rate to a stop exactly at D from Q at t = 97.5 - closing on every pass, and at every pass
	/// the rate says it gets there within the hold, but it is never within reach. Knob 60 holds from t = 37 and splits on the
	/// first pass at 60 s (t = 97, outcome expired); knob 0 splits at t = 37. Both arms allocate one id.
	/// </summary>
	private static Outcome HysteresisBraking(double split)
	{
		const double t0 = 37, stopIn = 60.5, cruise = 300, x0 = 12100, qx = x0 + cruise / 2 * stopIn + 10 + Distance;
		static double X(double t)
		{
			if (t <= t0) return 1000 + cruise * t;
			double left = Math.Max(0, stopIn - (t - t0));
			return x0 + cruise / 2 * stopIn - cruise / stopIn / 2 * left * left;
		}
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, qx, 0.0), (4L, qx + 200, 0.0), (5L, X(t), 0.0) };
		var run = HysteresisArms(Bodies, 110, split, false);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		HysteresisPass At(double t) => run.Passes.Single(x => x.T == t);
		Expect(At(37).Zero.Count(x => x.Split) == 1 && At(37).Held.Length == 0 && At(37).Events.Single().Outcome == "held", "t=37: knob 0 splits, knob 60 holds");
		Expect(run.Passes.Where(x => x.T > 37 && x.T < 97).All(x => x.Held.Length == 0 && x.HeldCount == 1), "held through t=96");
		Expect(At(97).Held.Count(x => x.Split && !x.Transfer) == 1 && At(97).HeldCount == 0 && At(97).Events.Single().Outcome == "expired"
			&& At(97).Events.Single().HeldS == 60, "t=97 (60 s): expired, the split made as knob 0 makes it");
		Expect(run.BornHeld == 1 && run.BornZero == 1 && run.Held.HysteresisExpired == 1 && run.Held.HysteresisTransferred == 0 && run.Held.HysteresisHeldTotal == 1,
			$"one new id each, counters expired 1 (expired {run.Held.HysteresisExpired}, transferred {run.Held.HysteresisTransferred})");
		Expect(run.Passes.Where(x => x.T > 97).All(x => x.HeldCount == 0 && x.Events.Length == 0), "never held again");
		return new Outcome($"split hysteresis expires (split {split:F0} m)", 0, 0, ok,
			"held from t=37, split at t=97 (60 s, expired), never held past the window", ok ? Events(run.Passes) : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}

	/// <summary>
	/// T-0339 (split hysteresis): two ships leave P at t = 37 on the same pass, 5 at Q (closing, as in the closing scenario)
	/// and 6 the other way (not closing). Knob 60 splits 6 off under a new id on that pass and holds only 5 with P (one split,
	/// two outputs: P with 5, and 6), then transfers 5 into Q; knob 0 splits three ways and later merges 5's new id into Q.
	/// </summary>
	private static Outcome HysteresisTwoPieces(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(5L, Math.Min(1000 + 300 * t, 28000), 0.0), (6L, -1200 - 300 * t, 0.0) };
		var run = HysteresisArms(Bodies, 100, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		HysteresisPass At(double t) => run.Passes.Single(x => x.T == t);
		Expect(At(37).Zero.Length == 1 && At(37).Zero[0].Outputs.Length == 3, "knob 0: P splits three ways at t=37");
		Expect(At(37).Held.Length == 1 && At(37).Held[0].Split && !At(37).Held[0].Transfer && At(37).Held[0].Outputs.Length == 2
			&& At(37).Held[0].Outputs.Any(o => o.Members.SequenceEqual(new long[] { 1, 2, 5 })) && At(37).Held[0].Outputs.Any(o => o.Members.SequenceEqual(new long[] { 6 }))
			&& At(37).HeldCount == 1 && At(37).Events.Single().Outcome == "held", "knob 60: 6 split off, 5 held with P");
		Expect(run.Held.HysteresisTransferred == 1 && run.BornHeld == 1 && run.BornZero == 2, $"knob 60: 5 transferred, one new id (knob 0 two): born {run.BornHeld} / {run.BornZero}");
		Expect(Together(At(100).HeldLayouts, 5, 3) && !Together(At(100).HeldLayouts, 6, 1) && !Together(At(100).HeldLayouts, 5, 1), "the end: 5 with Q, 6 alone");
		return new Outcome($"split hysteresis two pieces{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"only the closing piece is held; the other splits off on the knob-0 pass", ok ? Events(run.Passes) : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}
	/// <summary>
	/// T-0339i (r119 live 2026-10-07 13:11-13:45: 10 holds, all "not-closing" after 0-1 s, closingMps=0.0 on the line after
	/// the start; two started at 3084.5 and 7743.1 m/s): the pass runs three times inside every game frame (same positions,
	/// the wall clock 3 ms on), a frame every 1/6 s. Without the band: P at x = 0/-200, Q at 20000/20200, the ship leaves P
	/// at 100 m/s and parks 2 km off Q; it passes the split distance at t ~ 111 with 4.9 km to D (49 s), is held, and is
	/// transferred into Q at D (t ~ 160). With the band: the closing scenario's geometry at 300 m/s (band transfer at
	/// t ~ 58). Either way: held once, transferred, never "not-closing", every logged rate within 5% of the ship's speed.
	/// </summary>
	private static Outcome HysteresisSameFrame(double split, bool band)
	{
		double speed = band ? 300 : 100, qx = band ? 30220 : 20000;
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, qx, 0.0), (4L, qx + 200, 0.0),
			(5L, Math.Min(1000 + speed * t, qx - 2000), 0.0) };
		var run = HysteresisArms(Bodies, band ? 100 : 200, split, band, 60, 10, 3);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		var events = run.Passes.SelectMany(p => p.Events).ToArray();
		Expect(events.Select(e => e.Outcome).SequenceEqual(new[] { "held", "transferred" }), "events: held, then transferred ("
			+ string.Join(",", events.Select(e => e.Outcome)) + ")");
		Expect(events.All(e => Math.Abs(e.Rate - speed) <= 0.05 * speed), $"every logged closingMps within 5% of {speed:F0}");
		Expect(run.BornHeld == 0 && run.BornZero >= 1 && run.Held.HysteresisTransferred == 1 && run.Held.HysteresisReleased == 0,
			$"knob 60: no new id (born {run.BornHeld}, knob 0 {run.BornZero}), transferred {run.Held.HysteresisTransferred}, released {run.Held.HysteresisReleased}");
		Expect(Together(run.Passes[^1].HeldLayouts, 5, 3) && !Together(run.Passes[^1].HeldLayouts, 5, 1), "the end: the ship with Q");
		return new Outcome($"split hysteresis 3 passes a frame{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"held once, then transferred, rate ~ the ship's speed, never not-closing", ok ? Events(run.Passes) : string.Join("; ", trace) + "; events: " + Events(run.Passes));
	}

	/// <summary>
	/// T-0339i: the 100 m/s ship of <see cref="HysteresisSameFrame"/> (three passes a frame, a frame every 1/6 s) stops dead
	/// at t = 125, 6.5 km off Q. It is held from t ~ 110 and the run ends "not-closing" after the stop (once the rate read
	/// over the 1-2 s reference span no longer reaches D within the hold), within 2.5 s of it and never before, and the
	/// split is made on that pass.
	/// </summary>
	private static Outcome HysteresisStops(double split)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 20000.0, 0.0), (4L, 20200.0, 0.0),
			(5L, 1000 + 100 * Math.Min(t, 125), 0.0) };
		var run = HysteresisArms(Bodies, 140, split, false, 60, 10, 3);
		List<string> trace = new List<string>(run.Faults);
		bool ok = run.Faults.Count == 0;
		void Expect(bool condition, string what) { if (!condition) { ok = false; trace.Add("NOT " + what); } }
		var events = run.Passes.SelectMany(p => p.Events.Select(e => (p.T, e.Outcome, e.Rate, Split: p.Held.Any(c => c.Split && !c.Transfer)))).ToArray();
		Expect(events.Select(e => e.Outcome).SequenceEqual(new[] { "held", "not-closing" }), "events: held, then not-closing ("
			+ string.Join(",", events.Select(e => e.Outcome)) + ")");
		if (events.Length == 2)
		{
			Expect(Math.Abs(events[0].Rate - 100) <= 5, "held at ~100 m/s");
			Expect(events[1].T > 125 && events[1].T <= 127.5, $"not-closing after the stop, within 2.5 s of it (t={events[1].T:0.##})");
			Expect(events[1].Split, "the split made on the not-closing pass");
		}
		Expect(run.BornHeld == 1 && run.Held.HysteresisReleased == 1, $"one new id (born {run.BornHeld}), released {run.Held.HysteresisReleased}");
		return new Outcome($"split hysteresis piece stops (split {split:F0} m)", 0, 0, ok,
			"held, then not-closing only after the ship stopped, split on that pass", ok ? Events(run.Passes) : string.Join("; ", trace) + "; events: " + Events(run.Passes));
	}

	/// <summary>
	/// T-0339i: the ship sits 1 km off P and at t = 10 jumps 15 km towards Q (P at 0/-200, Q at 30220/30420) - a split falls
	/// due on that pass with Q 14.2 km ahead. r119 read 15000 m/s from the jump and held it; the jumped member is left out of
	/// the rate now (it moved faster than MaxMemberSpeedMps), so knob 60 is knob 0 on every pass, nothing held.
	/// </summary>
	private static Outcome HysteresisJump(double split, bool band)
	{
		(long, double, double)[] Bodies(double t) => new[] { (1L, 0.0, 0.0), (2L, -200.0, 0.0), (3L, 30220.0, 0.0), (4L, 30420.0, 0.0),
			(5L, t < 10 ? 1000.0 : 16000.0, 0.0) };
		var run = HysteresisArms(Bodies, 30, split, band);
		List<string> trace = new List<string>(run.Faults);
		bool same = run.Passes.All(x => x.Zero.Select(ChangeKey).SequenceEqual(x.Held.Select(ChangeKey)));
		bool split10 = run.Passes.Single(x => x.T == 10).Held.Count(x => x.Split && !x.Transfer) == 1;
		bool ok = run.Faults.Count == 0 && same && split10 && run.Held.HysteresisHeldTotal == 0 && run.Passes.All(x => x.Events.Length == 0);
		return new Outcome($"split hysteresis jump{(band ? " +band" : "")} (split {split:F0} m)", 0, 0, ok,
			"a jump is no closing rate: knob 60 identical to knob 0 (the split at t=10), nothing held",
			ok ? "as expected" : string.Join("; ", trace) + "; " + Trace(run.Passes));
	}
}
