using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Collections;
using VRage.Game.Entity;
using VRageMath;
using VRageMath.Spatial;

namespace ClusterPlugin;

/// <summary>
/// T-0404 (import.thawEnsureSpace): one Havok cluster placement for a commit's thaw instead of one per body.
/// </summary>
/// <remarks>
/// RestoreFrozen enables the frozen bodies one at a time. MyClusterTree.AddObject reorders the clusters whenever a
/// dynamic body's box (inflated by IdealClusterSize / 100) is not inside exactly one cluster, and every reorder
/// re-partitions all objects of the clusters it touches. Bodies spread over a partition each trigger their own
/// reorder of the same neighbourhood: r121i's commits over 100 ms were 98 % reorder, p50 8 reorders per commit
/// moving 2682 objects. MyPhysics.EnsurePhysicsSpace(union) is vanilla's call for "make this box one cluster"
/// (MyPhysics uses it for moving bodies); after it, each enable finds its box inside a cluster and adds without
/// reordering.
///
/// Only bodies whose box is NOT inside exactly one existing cluster enter the union, and only when at least two are:
/// a body already inside one cluster adds without a reorder, and a lone outside body costs one reorder (or create)
/// either way. Testing each body against today's clusters alone is not enough (bench 2026-10-08, chain fixture: 0
/// predicted, 7 reorders): the first outside body CREATES a cluster and the next ones straddle it - the cascade.
///
/// t0404b (import.thawEnsureGroups): the single union box is skipped once it reaches MaximumForSplit, and those
/// commits carried the remaining reorders (r139 ON: 54 of 133 eligible, 4.89 reorders per commit). Grouped, the
/// bodies are cut into spatial groups whose boxes stay below MaximumForSplit, and each group gets its own ensure.
/// EnsureClusterSpace places a dummy object, reorders, removes the dummy and leaves the cluster behind; a later
/// reorder that touches that cluster rebuilds it from the objects in it, and a frozen body is not one of them. So the
/// group's bodies are enabled right after its ensure, before the next group's ensure can take the space away.
/// Every dynamic body to enable is grouped, not only the outside ones: a body inside a cluster at the start can be
/// left outside by another group's reorder. A group already inside one cluster makes its ensure a no-op.
/// RestoreFrozen then sets the same Enabled value again, which is a no-op (MyPhysicsComponentBase.Enabled), and
/// restores the rest as before.
/// </remarks>
internal sealed partial class ClusterControlServer
{
	private readonly record struct ThawBody(MyEntity Entity, FrozenState State, BoundingBoxD Box, bool Outside);

	/// <summary>Game thread, inside the thaw's timed slice (its reorders count in the thaw's addReorder* fields).</summary>
	/// <returns>The handover_thaw fields. In BOTH arms (with import.thawEnsureSpace or diag.importAddTimers), observe-only,
	/// computed before any ensure - the size class of the commit: thawDyn (top-level dynamic bodies to enable), thawOut
	/// (dynamic bodies not inside exactly one existing cluster), thawOutM (the largest side of their union box) and
	/// thawGroupsOut (the groups below MaximumForSplit that would hold an outside body). With the knob: ensureBodies,
	/// ensureOut, ensureApplied (0/1), ensureBoxM (the union's largest side, 0 when not applied); grouped also
	/// ensureGroups, ensureGroupsOut, ensureGroupBodies, ensureGroupOut, ensureGroupBoxM and ensureGroupMs (per group, in
	/// ensure order).</returns>
	private static string ThawEnsureSpace(Transfer transfer)
	{
		bool knob = ClusterConfig.GetBool("import.thawEnsureSpace");
		if ((!knob && !ClusterConfig.GetBool("diag.importAddTimers")) || MyPhysics.Clusters == null) return "";
		ListReader<MyClusterTree.MyCluster> clusters = MyPhysics.Clusters.GetClusters();
		Vector3 inflate = MyClusterTree.IdealClusterSize / 100f;
		double limit = MyClusterTree.MaximumForSplit.AbsMax();
		List<ThawBody> all = new(), topLevel = new();
		int outside = 0;
		BoundingBoxD union = BoundingBoxD.CreateInvalid();
		foreach ((long id, FrozenState state) in transfer.Frozen)
		{
			if (!state.HadPhysics || !state.PhysicsEnabled) continue;
			if (!MyEntities.TryGetEntityById(id, out MyEntity entity) || entity.MarkedForClose) continue;
			if (entity.Physics is not MyPhysicsBody body || body.IsStaticForCluster) continue;
			BoundingBoxD box = entity.PositionComp.WorldAABB.GetInflated(inflate);
			ThawBody thawBody = new(entity, state, box, !InsideOneCluster(clusters, box));
			all.Add(thawBody);
			// A hierarchy child (a seated pilot) is restored after its parent by RestoreFrozen and lies inside the
			// parent's box; only top-level bodies are grouped and enabled early.
			if (entity.Parent == null) topLevel.Add(thawBody);
			if (!thawBody.Outside) continue;
			outside++;
			union.Include(box);
		}
		(List<List<ThawBody>> groups, List<BoundingBoxD> boxes) = Group(topLevel, limit);
		string fields = " thawDyn=" + topLevel.Count + " thawOut=" + outside
			+ " thawOutM=" + (outside > 0 ? union.Size.AbsMax() : 0).ToString("F0", CultureInfo.InvariantCulture)
			+ " thawGroupsOut=" + groups.Count(g => g.Any(x => x.Outside));
		if (!knob) return fields;
		if (ClusterConfig.GetBool("import.thawEnsureGroups"))
			return fields + " ensureBodies=" + topLevel.Count + " ensureOut=" + outside + EnsureGroups(groups, boxes, outside, limit);
		bool applied = outside >= 2 && union.Size.AbsMax() < limit;
		if (applied) MyPhysics.EnsurePhysicsSpace(union);
		return fields + " ensureBodies=" + all.Count + " ensureOut=" + outside + " ensureApplied=" + (applied ? 1 : 0)
			+ " ensureBoxM=" + (applied ? union.Size.AbsMax() : 0).ToString("F0", CultureInfo.InvariantCulture);
	}

	/// <summary>t0404b: cut the bodies into groups below <paramref name="limit"/> along the longest axis of their union.</summary>
	private static (List<List<ThawBody>> Groups, List<BoundingBoxD> Boxes) Group(List<ThawBody> bodies, double limit)
	{
		List<List<ThawBody>> groups = new();
		List<BoundingBoxD> boxes = new();
		if (bodies.Count == 0) return (groups, boxes);
		BoundingBoxD all = BoundingBoxD.CreateInvalid();
		foreach (ThawBody body in bodies) all.Include(body.Box);
		int axis = all.Size.AbsMaxComponent();
		foreach (ThawBody body in bodies.OrderBy(x => x.Box.Min.GetDim(axis)))
		{
			if (groups.Count > 0)
			{
				BoundingBoxD grown = boxes[^1];
				grown.Include(body.Box);
				if (grown.Size.AbsMax() < limit)
				{
					groups[^1].Add(body);
					boxes[^1] = grown;
					continue;
				}
			}
			groups.Add(new List<ThawBody> { body });
			boxes.Add(body.Box);
		}
		return (groups, boxes);
	}

	/// <summary>t0404b: per group, ensure its box, then enable its bodies (with their frozen velocities, as RestoreFrozen
	/// does).</summary>
	private static string EnsureGroups(List<List<ThawBody>> groups, List<BoundingBoxD> boxes, int outside, double limit)
	{
		if (outside < 2) return " ensureApplied=0 ensureBoxM=0 ensureGroups=0 ensureGroupsOut=0";
		List<string> ms = new(groups.Count);
		double largest = 0;
		for (int index = 0; index < groups.Count; index++)
		{
			long t0 = Stopwatch.GetTimestamp();
			// A group whose box is itself at the limit (one body that large) gets no ensure: its bodies add as before.
			if (boxes[index].Size.AbsMax() < limit) MyPhysics.EnsurePhysicsSpace(boxes[index]);
			ms.Add((Stopwatch.GetElapsedTime(t0).TotalMilliseconds).ToString("F2", CultureInfo.InvariantCulture));
			largest = System.Math.Max(largest, boxes[index].Size.AbsMax());
			foreach (ThawBody body in groups[index])
			{
				if (body.Entity.MarkedForClose || body.Entity.Physics == null) continue;
				body.Entity.Physics.Enabled = body.State.PhysicsEnabled;
				body.Entity.Physics.LinearVelocity = body.State.LinearVelocity;
				body.Entity.Physics.AngularVelocity = body.State.AngularVelocity;
			}
		}
		return " ensureApplied=1 ensureBoxM=" + largest.ToString("F0", CultureInfo.InvariantCulture)
			+ " ensureGroups=" + groups.Count + " ensureGroupsOut=" + groups.Count(g => g.Any(x => x.Outside))
			+ " ensureGroupBodies=" + string.Join(",", groups.Select(g => g.Count))
			+ " ensureGroupOut=" + string.Join(",", groups.Select(g => g.Count(x => x.Outside)))
			+ " ensureGroupBoxM=" + string.Join(",", boxes.Select(b => b.Size.AbsMax().ToString("F0", CultureInfo.InvariantCulture)))
			+ " ensureGroupMs=" + string.Join(",", ms);
	}

	/// <summary>AddObject's no-reorder case for a dynamic body: its (inflated) box overlaps exactly one cluster and lies
	/// inside it.</summary>
	private static bool InsideOneCluster(ListReader<MyClusterTree.MyCluster> clusters, BoundingBoxD box)
	{
		MyClusterTree.MyCluster only = null;
		foreach (MyClusterTree.MyCluster cluster in clusters)
		{
			if (cluster.AABB.Contains(box) == ContainmentType.Disjoint) continue;
			if (only != null) return false;
			only = cluster;
		}
		return only != null && only.AABB.Contains(box) == ContainmentType.Contains;
	}
}
