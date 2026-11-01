using System;
using System.Collections.Generic;
using System.Linq;

namespace Reference;

// Frozen at "Fixes (#2)"; re-frozen for T-0319 (deliberate behaviour change: merges judged on the cores,
// split reason names the smallest cross-group gap). The T-0319 parts are written independently of the
// live file - a sorted candidate list instead of the live scan with refusals - so the comparison still
// checks the live implementation rather than a copy of it.
// Re-frozen for T-0339 (deliberate behaviour change: the children of a distance split carry the parent's CreatedAt).
// Re-frozen for T-0339 B (deliberate behaviour change: the largest output of a distance split keeps the input's id),
// written independently of the live file (a stable sort and a queue instead of the live index arithmetic).
internal sealed class PartitionMembership
{
	internal readonly record struct Member(long Id, double X, double Y, double Z, double Radius, double Speed, long AtomicGroup = 0);
	/// <summary>
	/// <see cref="Pending"/> pins members adopted from a save whose entities have not materialized
	/// in the world census yet. A large grid loaded from a partition save spends over a minute in
	/// the parallel entity-init pipeline (11k blocks took 64 s on 2026-08-16), during which the
	/// census does not report it; without the pin its layout dissolved, and when the entity finally
	/// surfaced the unassigned-member pass minted a brand-new REGISTERED partition around it -
	/// laundering a stale seed-save copy into legitimate content and wedging the cluster on a
	/// duplicate incarnation (partitions 330/333, "Pirate Base Large mk.2"). A pinned member counts
	/// as assigned, and the layout holding it takes no part in splits, merges or nearest-adoption
	/// until the pin resolves. <see cref="PinnedUntil"/> bounds the wait: an entity that never
	/// materializes (init crashed, content superseded) unpins there and leaves normally.
	/// </summary>
	internal sealed record Layout(ulong Id, long[] Members, double CreatedAt)
	{
		public long[] Pending { get; init; } = Array.Empty<long>();
		public double PinnedUntil { get; init; }
	}
	/// <summary><paramref name="Why"/>: the trigger and the values compared, for the commit log (T-0073).</summary>
	internal sealed record Change(bool Split, ulong[] Inputs, Layout[] Outputs, string Why = "");

	/// <summary>How long an adopted-but-absent member stays pinned before it is given up on.</summary>
	internal const double PinSeconds = 180.0;

	private double m_distance;
	private readonly double m_margin;
	private readonly Func<int, ulong[]> m_allocate;
	// Partitioning.md 3.4: Frozen / HandoverInProgress / MergePending partitions are skipped by the
	// membership pass and revisited next cycle. Splitting or merging a partition whose entities are
	// mid-transfer rewrites the very set the transaction agreed on.
	private readonly Func<ulong, bool> m_locked;
	private List<Layout> m_layouts = new List<Layout>();

	public PartitionMembership(double distance, double margin, Func<int, ulong[]> allocate, Func<ulong, bool> locked = null)
	{
		m_distance = distance;
		m_margin = margin;
		m_allocate = allocate;
		m_locked = locked ?? (_ => false);
	}

	public IReadOnlyList<Layout> Layouts => m_layouts;
	public void SetDistance(double distance) => m_distance = distance;
	public bool CoreMerge { get; set; } = true;
	public int RefusedMerges { get; private set; }

	/// <summary>T-0319: both cores (members outside the contact D-component) still reach the other layout within the split distance, or are empty.</summary>
	private bool CoresHold(Layout a, Layout b, Dictionary<long, Member> byId) => Reaches(a, b, byId) && Reaches(b, a, byId);

	private bool Reaches(Layout side, Layout other, Dictionary<long, Member> byId)
	{
		long[] own = side.Members.Where(byId.ContainsKey).ToArray();
		long[] far = other.Members.Where(byId.ContainsKey).ToArray();
		long[] core = Components(own, byId, m_distance)
			.Where(part => !part.Any(x => far.Any(y => Gap(byId[x], byId[y]) < m_distance)))
			.SelectMany(part => part).ToArray();
		return core.Length == 0 || core.Any(x => far.Any(y => Gap(byId[x], byId[y]) <= m_distance + 2 * m_margin));
	}
	public void Adopt(ulong id, IEnumerable<long> members, double now, bool mature,
		IEnumerable<long> pending = null)
	{
		long[] pinned = (pending ?? Array.Empty<long>()).Distinct().Order().ToArray();
		long[] adopted = members.Concat(pinned).Distinct().Order().ToArray();
		m_layouts = m_layouts.Select(x => x with { Members = x.Members.Except(adopted).ToArray(),
			Pending = x.Pending.Except(adopted).ToArray() }).Where(x => x.Members.Length != 0).ToList();
		if (mature) m_layouts = m_layouts.Select(x => x with { CreatedAt = double.NegativeInfinity }).ToList();
		m_layouts.RemoveAll(x => x.Id == id);
		m_layouts.Add(new Layout(id, adopted, mature ? double.NegativeInfinity : now)
			{ Pending = pinned, PinnedUntil = pinned.Length == 0 ? 0 : now + PinSeconds });
	}
	public void Remove(ulong id) => m_layouts.RemoveAll(x => x.Id == id);

	public Change[] Evaluate(IEnumerable<Member> input, double now)
	{
		Member[] members = input.OrderBy(x => x.Id).ToArray();
		Dictionary<long, Member> byId = members.ToDictionary(x => x.Id);
		if (m_layouts.Count == 0)
		{
			// Plan/EntityGapPlacement.md (Viktor 2026-08-24): grouping happens at the SYNC DISTANCE,
			// not sync + margin. The margin's anticipation job moved to the gateway's velocity-aware
			// placement; keeping it in membership grouping only inflated partitions (and their
			// bounding spheres) with content that never actually met the sync invariant.
			m_layouts = Components(members.Select(x => x.Id), byId, m_distance)
				.Select(ids => new Layout(m_allocate(1).Single(), ids, now)).OrderBy(x => x.Id).ToList();
			return Array.Empty<Change>();
		}

		List<Change> changes = new List<Change>();
		m_layouts = m_layouts.Select(x =>
		{
			// A pinned member stays in its layout while absent from the census (entity still in the
			// parallel init pipeline); it unpins the moment it is seen, or at the pin deadline.
			long[] pending = now < x.PinnedUntil
				? x.Pending.Where(id => !byId.ContainsKey(id)).ToArray() : Array.Empty<long>();
			return x with {
				Members = x.Members.Where(id => byId.ContainsKey(id) || pending.Contains(id))
					.OrderBy(y => y).ToArray(),
				Pending = pending };
		}).Where(x => x.Members.Length != 0).OrderBy(x => x.Id).ToList();
		HashSet<long> assigned = m_layouts.SelectMany(x => x.Members).ToHashSet();
		// Adopting the unassigned members used to measure the new member against EVERY member of EVERY
		// layout - O(unassigned x members), which degenerates to O(N^2) right after a world load or a mass
		// import, exactly when the node is busiest. The enclosing sphere of a layout bounds the gap to all of
		// its members from below, so a layout that cannot possibly be within the adoption distance is skipped
		// without touching its members. Layouts that survive the bound are measured exactly as before, so the
		// chosen layout and the tie-break (smallest gap, then smallest id) are unchanged.
		// Adoption is a one-member merge, so it uses the merge distance: the sync distance itself
		// (Plan/EntityGapPlacement.md). An entity between D and the old D+m now becomes its own
		// (co-located) partition instead of inflating a neighbour's membership and sphere.
		double adoptDistance = m_distance;
		foreach (long id in members.Select(x => x.Id).Where(x => !assigned.Contains(x)))
		{
			Member candidate = byId[id];
			Layout nearest = null;
			double nearestGap = 0;
			foreach (Layout layout in m_layouts)
			{
				if (m_locked(layout.Id) || layout.Pending.Length != 0) continue;
				(double X, double Y, double Z, double R) sphere = Enclosing(layout, byId);
				double lower = Math.Sqrt((sphere.X - candidate.X) * (sphere.X - candidate.X)
					+ (sphere.Y - candidate.Y) * (sphere.Y - candidate.Y)
					+ (sphere.Z - candidate.Z) * (sphere.Z - candidate.Z)) - sphere.R - candidate.Radius;
				if (lower >= adoptDistance) continue;
				double gap = layout.Members.Min(y => Gap(candidate, byId[y]));
				if (!(gap < adoptDistance)) continue;
				// m_layouts is kept ordered by id, so a strict comparison keeps the smallest id on a tie -
				// the same choice "OrderBy(Gap).ThenBy(Layout.Id).First()" made.
				if (nearest == null || gap < nearestGap) { nearest = layout; nearestGap = gap; }
			}
			if (nearest == null) m_layouts.Add(new Layout(m_allocate(1).Single(), new[] { id }, now));
			else m_layouts[m_layouts.IndexOf(nearest)] = nearest with { Members = nearest.Members.Append(id).Order().ToArray() };
		}

		foreach (Layout layout in m_layouts.OrderBy(x => x.Id).ToArray())
		{
			if (m_locked(layout.Id) || layout.Pending.Length != 0) continue;
			long[][] parts = Components(layout.Members, byId, m_distance + 2 * m_margin).ToArray();
			if (parts.Length < 2) continue;
			long[] largest = parts.OrderByDescending(x => x.Length).First();   // a stable sort: a tie goes to the earlier group
			Queue<ulong> fresh = new Queue<ulong>(m_allocate(parts.Length - 1));
			Layout[] children = parts.Select(x => new Layout(ReferenceEquals(x, largest) ? layout.Id : fresh.Dequeue(), x, layout.CreatedAt)).ToArray();
			m_layouts.Remove(layout);
			m_layouts.AddRange(children);
			changes.Add(new Change(true, new[] { layout.Id }, children,
				"members fell into " + parts.Length + " groups of [" + string.Join(",", parts.Select(x => x.Length))
				+ "] with every cross-group gap >= " + (m_distance + 2 * m_margin).ToString("F0") + " m (split distance); smallest "
				+ parts.SelectMany((x, i) => parts.Skip(i + 1).SelectMany(y => x.SelectMany(a => y.Select(b => Gap(byId[a], byId[b])))))
					.DefaultIfEmpty(double.PositiveInfinity).Min().ToString("F1") + " m"));
		}

		// The merge search picks the same pair the LINQ cross-join used to (smallest gap, ties by a.Id then
		// b.Id) but does not pay for every pair: the old query evaluated AtomicLink AND the full
		// member-by-member nearest-pair distance for EVERY ordered pair of layouts, then sorted them all to
		// take the first - O(N^2) member-distance computations per pass in the node's total member count, and
		// the pass repeats after every merge. It was 61% of PartitionMembership.Evaluate on the w22 regression
		// cluster (node-3, 372 entities, 2026-08-22). Two exact-preserving changes: atomic linkage is a set
		// overlap instead of |a|x|b| comparisons, and a pair whose enclosing spheres are farther apart than the
		// merge distance cannot contain a close member pair, so its inner scan is skipped. Neither can change
		// which pair wins: the sphere bound is a true lower bound on every member gap in the pair (a member
		// sphere lies inside its layout's enclosing sphere), so a pruned pair would have failed the
		// gap < distance test anyway. Atomic pairs bypass the bound because they merge at any distance.
		// Viktor 2026-08-24: "Don't merge until the partitions actually reach the sync distance."
		// Preemptive same-node placement is the gateway's job (time-to-contact co-location); the
		// merge itself waits for the invariant to actually bind. Split stays at D + 2·margin, so
		// the merge/split hysteresis band widens to [D, D+2m] and churn can only go down.
		double mergeDistance = m_distance;
		RefusedMerges = 0;
		while (true)
		{
			Layout[] ordered = m_layouts.OrderBy(x => x.Id).ToArray();
			var enclosing = new (double X, double Y, double Z, double R)[ordered.Length];
			var groups = new HashSet<long>[ordered.Length];
			for (int i = 0; i < ordered.Length; i++)
			{
				enclosing[i] = Enclosing(ordered[i], byId);
				groups[i] = AtomicGroups(ordered[i], byId);
			}
			Layout bestA = null, bestB = null;
			double bestGap = 0;
			bool bestAtomic = false;
			for (int i = 0; i < ordered.Length; i++)
			{
				Layout a = ordered[i];
				if (m_locked(a.Id) || a.Pending.Length != 0) continue;
				for (int j = i + 1; j < ordered.Length; j++)
				{
					Layout b = ordered[j];
					if (m_locked(b.Id) || b.Pending.Length != 0) continue;
					bool atomic = Overlaps(groups[i], groups[j]);
					if (!atomic && !(now - a.CreatedAt >= 10 && now - b.CreatedAt >= 10)) continue;
					if (!atomic && SphereGap(enclosing[i], enclosing[j]) >= mergeDistance) continue;
					double gap = a.Members.Min(x => b.Members.Min(y => Gap(byId[x], byId[y])));
					if (!atomic && !(gap < mergeDistance)) continue;
					// Ascending (a.Id, b.Id) iteration plus a strict comparison keeps the first pair of the
					// smallest gap, which is what "orderby gap, a.Id, b.Id" selected.
					if (bestA == null || gap < bestGap) { bestA = a; bestB = b; bestGap = gap; bestAtomic = atomic; }
				}
			}
			if (bestA == null) break;
			// T-0319, written independently of the live scan: the merge taken is the first pair in
			// (gap, a.Id, b.Id) order that is atomic, touching, or passes the core test.
			if (CoreMerge && !bestAtomic && bestGap > 0 && !CoresHold(bestA, bestB, byId))
			{
				var candidates = (from i in Enumerable.Range(0, ordered.Length)
					from j in Enumerable.Range(i + 1, ordered.Length - i - 1)
					let a = ordered[i]
					let b = ordered[j]
					where !m_locked(a.Id) && a.Pending.Length == 0 && !m_locked(b.Id) && b.Pending.Length == 0
					let atomic = Overlaps(groups[i], groups[j])
					where atomic || now - a.CreatedAt >= 10 && now - b.CreatedAt >= 10
					let gap = a.Members.Min(x => b.Members.Min(y => Gap(byId[x], byId[y])))
					where atomic || gap < mergeDistance
					orderby gap, a.Id, b.Id
					select (a, b, gap, atomic)).ToArray();
				var taken = candidates.FirstOrDefault(c => c.atomic || c.gap <= 0 || CoresHold(c.a, c.b, byId));
				RefusedMerges += candidates.TakeWhile(c => !(c.atomic || c.gap <= 0 || CoresHold(c.a, c.b, byId))).Count();
				if (taken.a == null) break;
				(bestA, bestB, bestGap, bestAtomic) = taken;
			}
			Layout keep = bestA.Members.Length > bestB.Members.Length || bestA.Members.Length == bestB.Members.Length && bestA.Id < bestB.Id ? bestA : bestB;
			Layout merged = keep with { Members = bestA.Members.Concat(bestB.Members).Order().ToArray() };
			m_layouts.Remove(bestA); m_layouts.Remove(bestB); m_layouts.Add(merged);
			changes.Add(new Change(false, new[] { bestA.Id, bestB.Id }, new[] { merged },
				(bestAtomic ? "atomic link (attached grids) between the two, nearest members " + bestGap.ToString("F1") + " m apart"
					: "nearest member pair " + bestGap.ToString("F1") + " m apart, under the merge distance " + mergeDistance.ToString("F0") + " m (D)")
				+ "; kept " + keep.Id + " (" + keep.Members.Length + " of " + merged.Members.Length + " members)"));
		}
		m_layouts.Sort((a, b) => a.Id.CompareTo(b.Id));
		return changes.ToArray();
	}

	/// <summary>
	/// The connected components of the members under "atomically linked, or within <paramref name="distance"/>
	/// of each other". Components come out ordered by their smallest member, each one sorted - the order the
	/// previous breadth-first search produced, because it seeded from the smallest remaining id.
	/// </summary>
	/// <remarks>
	/// The BFS re-scanned every still-unassigned member (and allocated an array) for every member it dequeued,
	/// which is O(N^2) distance tests in the member count. That is the whole world census on the cold-start
	/// path at line 71 and the whole partition on every split pass, several times a second on the game thread.
	/// This does the same work by union-find over two edge sources: atomic groups, which are equality classes
	/// and need no pairwise test at all, and proximity, where members are bucketed into a uniform grid whose
	/// cell is wide enough (distance + 2 x the largest member radius) that any linked pair must share a cell
	/// or touch one of its 26 neighbours. Distant members are never compared. A dense clump still costs its
	/// own pairs - members genuinely within reach of each other have to be tested - so the worst case is no
	/// worse than before, while the common case of many separated clumps stops being quadratic across clumps.
	/// </remarks>
	private static IEnumerable<long[]> Components(IEnumerable<long> ids, Dictionary<long, Member> members, double distance)
	{
		long[] all = ids.ToArray();
		if (all.Length == 0) yield break;
		Dictionary<long, long> parent = new Dictionary<long, long>(all.Length);
		foreach (long id in all) parent[id] = id;
		long Find(long id)
		{
			while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; }
			return id;
		}
		void Union(long a, long b)
		{
			a = Find(a); b = Find(b);
			if (a == b) return;
			if (a < b) parent[b] = a; else parent[a] = b;
		}

		// Atomic linkage is an equality on the group id, so one pass over the members joins each group.
		Dictionary<long, long> firstOfGroup = new Dictionary<long, long>();
		foreach (long id in all)
		{
			long group = members[id].AtomicGroup;
			if (group == 0) continue;
			if (firstOfGroup.TryGetValue(group, out long seen)) Union(id, seen);
			else firstOfGroup[group] = id;
		}

		// Proximity linkage. Cell width covers the largest gap a linked pair can span, so candidates only ever
		// live in the 3x3x3 block around a member's own cell.
		double maxRadius = 0;
		foreach (long id in all) maxRadius = Math.Max(maxRadius, members[id].Radius);
		double cell = distance + 2 * maxRadius;
		if (!(cell > 0) || double.IsInfinity(cell) || double.IsNaN(cell))
		{
			// Degenerate geometry (zero distance and zero radii, or a non-finite coordinate): fall back to the
			// exhaustive comparison rather than risk a grid that cannot represent it.
			for (int i = 0; i < all.Length; i++)
				for (int j = i + 1; j < all.Length; j++)
					if (Gap(members[all[i]], members[all[j]]) <= distance) Union(all[i], all[j]);
		}
		else
		{
			Dictionary<(long X, long Y, long Z), List<long>> grid = new Dictionary<(long, long, long), List<long>>();
			(long X, long Y, long Z) CellOf(Member member) => ((long)Math.Floor(member.X / cell),
				(long)Math.Floor(member.Y / cell), (long)Math.Floor(member.Z / cell));
			foreach (long id in all)
			{
				(long X, long Y, long Z) key = CellOf(members[id]);
				if (!grid.TryGetValue(key, out List<long> bucket)) grid[key] = bucket = new List<long>();
				bucket.Add(id);
			}
			foreach (long id in all)
			{
				Member member = members[id];
				(long X, long Y, long Z) key = CellOf(member);
				for (long dx = -1; dx <= 1; dx++)
					for (long dy = -1; dy <= 1; dy++)
						for (long dz = -1; dz <= 1; dz++)
						{
							if (!grid.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out List<long> bucket)) continue;
							foreach (long other in bucket)
							{
								if (other <= id) continue;
								if (Gap(member, members[other]) <= distance) Union(id, other);
							}
						}
			}
		}

		SortedDictionary<long, SortedSet<long>> components = new SortedDictionary<long, SortedSet<long>>();
		foreach (long id in all)
		{
			long root = Find(id);
			if (!components.TryGetValue(root, out SortedSet<long> component)) components[root] = component = new SortedSet<long>();
			component.Add(id);
		}
		// Union by smaller root makes every root the minimum of its component, so iterating the roots in
		// ascending order yields the components smallest-member-first.
		foreach (SortedSet<long> component in components.Values) yield return component.ToArray();
	}

	private static bool AtomicLink(Layout a, Layout b, Dictionary<long, Member> members) => a.Members.Any(x => b.Members.Any(y => Atomic(members[x], members[y])));

	/// <summary>The non-zero atomic groups a layout's members belong to.</summary>
	private static HashSet<long> AtomicGroups(Layout layout, Dictionary<long, Member> members)
	{
		HashSet<long> groups = new HashSet<long>();
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member) && member.AtomicGroup != 0) groups.Add(member.AtomicGroup);
		return groups;
	}

	/// <summary>Same answer as <see cref="AtomicLink"/>, in |a|+|b| instead of |a|x|b|.</summary>
	private static bool Overlaps(HashSet<long> a, HashSet<long> b)
	{
		if (a.Count == 0 || b.Count == 0) return false;
		HashSet<long> small = a.Count <= b.Count ? a : b, large = ReferenceEquals(small, a) ? b : a;
		foreach (long group in small) if (large.Contains(group)) return true;
		return false;
	}

	/// <summary>
	/// A sphere containing every member sphere of the layout: centred on the mean member centre, with the
	/// radius stretched to cover the farthest member's own radius.
	/// </summary>
	private static (double X, double Y, double Z, double R) Enclosing(Layout layout, Dictionary<long, Member> members)
	{
		double x = 0, y = 0, z = 0;
		int count = 0;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member)) { x += member.X; y += member.Y; z += member.Z; count++; }
		if (count == 0) return (0, 0, 0, double.PositiveInfinity);
		x /= count; y /= count; z /= count;
		double radius = 0;
		foreach (long id in layout.Members)
			if (members.TryGetValue(id, out Member member))
			{
				double dx = member.X - x, dy = member.Y - y, dz = member.Z - z;
				radius = Math.Max(radius, Math.Sqrt(dx * dx + dy * dy + dz * dz) + member.Radius);
			}
		return (x, y, z, radius);
	}

	/// <summary>A lower bound on the gap between any member of one layout and any member of the other.</summary>
	private static double SphereGap((double X, double Y, double Z, double R) a, (double X, double Y, double Z, double R) b)
	{
		double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
		return Math.Sqrt(x * x + y * y + z * z) - a.R - b.R;
	}
	private static bool Atomic(Member a, Member b) => a.AtomicGroup != 0 && a.AtomicGroup == b.AtomicGroup;
	private static double Gap(Member a, Member b)
	{
		double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
		return Math.Sqrt(x * x + y * y + z * z) - a.Radius - b.Radius;
	}
}
