using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// T-0392, <c>import.activatePerFrame</c>: which staged entities the target activates together in
/// one game-thread frame. Pure function of the builders, so the self-test (section
/// <c>activate-slices</c>) checks it without a world.
///
/// Rule: entities that the builders say are physically joined form one group, and a group is never
/// split across slices - a slice must not Add a rotor base whose top arrives a frame later, or a
/// connector/landing gear locked to a grid that is not there yet. A plain import (no transfer) does
/// not freeze what it Adds, so between two slices such a half structure would update and give up its
/// attachment. Links read from the builders (block ids resolve to the grid that carries the block):
///
///  * mechanical connection base (rotor, advanced rotor, hinge, piston, suspension): TopBlockId
///  * attachable top (rotor/hinge head, piston top, wheel): ParentEntityId
///  * connector: ConnectedEntityId (another connector block)
///  * landing gear / magnetic plate: AttachedEntityId (a grid, or a block's id)
///
/// A merge block keeps nothing in its builder: merged grids ARE one grid. A seated pilot is inside
/// its cockpit's builder, so it moves with its grid. Ids that resolve to nothing in this import (the
/// other side is on this node already, a voxel, or not in the partition) link nothing.
///
/// Groups are taken in the order of their first member; slices are filled greedily with whole
/// groups up to <c>perFrame</c> entities; a group larger than <c>perFrame</c> is a slice of its own.
/// Inside a slice the members keep their original relative order.
///
/// <c>import.activateBudgetMs</c> (T-0392, cost budget): the same greedy fill, but a slice also stops
/// before the group that would take its estimated Add time past the budget (entities x estimated ms
/// per entity). It can only make a slice smaller than the count cap, never larger, and a slice
/// always takes at least one group: a single group that alone exceeds the budget is still one slice
/// (flagged overBudget, a group must activate in one frame). Budget 0 or no estimate = the count cap
/// alone, i.e. exactly the plan without a budget. The live import takes one slice at a time
/// (<see cref="Take"/>) because the estimate is re-measured after every slice.
/// </summary>
internal static class ImportActivateSlices
{
	/// <summary>Slices as ascending index lists into <paramref name="builders"/>; empty input = no slices.</summary>
	internal static List<List<int>> Plan(IReadOnlyList<MyObjectBuilder_EntityBase> builders, int perFrame)
		=> Plan(builders, perFrame, 0, 0, out _);

	/// <summary>
	/// The plan at a constant cost estimate (the self-test's view of the budget; the live import
	/// re-estimates between slices). <paramref name="overBudget"/>: slices that are a single group
	/// whose estimate alone exceeds the budget.
	/// </summary>
	internal static List<List<int>> Plan(IReadOnlyList<MyObjectBuilder_EntityBase> builders, int perFrame, double budgetMs, double msPerEntity, out int overBudget)
	{
		if (perFrame <= 0) throw new ArgumentOutOfRangeException(nameof(perFrame), "perFrame must be positive");
		List<List<int>> groups = Groups(builders);
		int[] sizes = groups.Select(x => x.Count).ToArray();
		List<List<int>> slices = new List<List<int>>();
		overBudget = 0;
		for (int next = 0; next < groups.Count;)
		{
			int take = Take(sizes, next, perFrame, budgetMs, msPerEntity, out bool over);
			if (over) overBudget++;
			slices.Add(Slice(groups, next, take));
			next += take;
		}
		return slices;
	}

	/// <summary>The members of groups [start, start + take) as one ascending index list.</summary>
	internal static List<int> Slice(List<List<int>> groups, int start, int take)
	{
		List<int> slice = new List<int>();
		for (int g = start; g < start + take; g++) slice.AddRange(groups[g]);
		slice.Sort();
		return slice;
	}

	/// <summary>
	/// How many whole groups, from <paramref name="start"/> on, the next slice takes: at least one
	/// (0 only when none is left), then each next group while the slice stays within
	/// <paramref name="perFrame"/> entities and, when <paramref name="budgetMs"/> and
	/// <paramref name="msPerEntity"/> are both positive, within budgetMs at msPerEntity per entity.
	/// <paramref name="overBudget"/>: the budget applies and the first group alone exceeds it.
	/// </summary>
	internal static int Take(IReadOnlyList<int> groupSizes, int start, int perFrame, double budgetMs, double msPerEntity, out bool overBudget)
	{
		if (perFrame <= 0) throw new ArgumentOutOfRangeException(nameof(perFrame), "perFrame must be positive");
		overBudget = false;
		if (start >= groupSizes.Count) return 0;
		bool budgeted = budgetMs > 0 && msPerEntity > 0 && !double.IsInfinity(msPerEntity);
		int entities = groupSizes[start];
		overBudget = budgeted && entities * msPerEntity > budgetMs;
		int take = 1;
		while (start + take < groupSizes.Count)
		{
			int grown = entities + groupSizes[start + take];
			if (grown > perFrame || (budgeted && grown * msPerEntity > budgetMs)) break;
			entities = grown;
			take++;
		}
		return take;
	}

	/// <summary>
	/// The physical groups as ascending index lists into <paramref name="builders"/>, in the order of
	/// their first member; every index exactly once.
	/// </summary>
	internal static List<List<int>> Groups(IReadOnlyList<MyObjectBuilder_EntityBase> builders)
	{
		int count = builders.Count;
		int[] parent = new int[count];
		for (int i = 0; i < count; i++) parent[i] = i;
		int Find(int x)
		{
			while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
			return x;
		}
		void Union(int a, int b)
		{
			if (a < 0 || b < 0) return;
			a = Find(a); b = Find(b);
			if (a == b) return;
			// The smaller index is the root, so a group's root is its first member.
			if (a < b) parent[b] = a; else parent[a] = b;
		}

		Dictionary<long, int> owner = new Dictionary<long, int>();
		for (int i = 0; i < count; i++)
		{
			MyObjectBuilder_EntityBase builder = builders[i];
			if (builder == null) continue;
			if (builder.EntityId != 0) owner.TryAdd(builder.EntityId, i);
			if (builder is MyObjectBuilder_CubeGrid grid && grid.CubeBlocks != null)
				foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
					if (block != null && block.EntityId != 0) owner.TryAdd(block.EntityId, i);
		}
		int Resolve(long id) => id != 0 && owner.TryGetValue(id, out int index) ? index : -1;

		for (int i = 0; i < count; i++)
		{
			if (builders[i] is not MyObjectBuilder_CubeGrid grid || grid.CubeBlocks == null) continue;
			foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
			{
				switch (block)
				{
					case MyObjectBuilder_MechanicalConnectionBlock mechanical when mechanical.TopBlockId is long top:
						Union(i, Resolve(top));
						break;
					case MyObjectBuilder_AttachableTopBlockBase head:
						Union(i, Resolve(head.ParentEntityId));
						break;
					case MyObjectBuilder_ShipConnector connector:
						Union(i, Resolve(connector.ConnectedEntityId));
						break;
					case MyObjectBuilder_LandingGear gear when gear.AttachedEntityId is long attached:
						Union(i, Resolve(attached));
						break;
				}
			}
		}

		Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
		List<int> roots = new List<int>();
		for (int i = 0; i < count; i++)
		{
			int root = Find(i);
			if (!groups.TryGetValue(root, out List<int> members)) { groups[root] = members = new List<int>(); roots.Add(root); }
			members.Add(i);
		}

		return roots.Select(root => groups[root]).ToList();
	}
}
