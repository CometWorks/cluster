using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// #1365: reports the grids on this node that a big owner owns whom this node has no identity for. That is what a
/// node-created NPC faction leaves behind once the World Authority's snapshots drop its founder: MES on the node had
/// owned 31 of 31 pocket grids by such founders (no identity, no faction, not NPC), so the #1354 census and sweep
/// never saw them. A handover import can name an identity for a few seconds before its replication arrives
/// (MyCubeBlockSeams.ClusterPreserveUnknownOwner), so only a grid unknown on two walks in a row counts. One walk
/// every WalkSeconds; a line when the count changes, else at most every RepeatSeconds while it stays above zero.
/// </summary>
internal static class UnknownOwnerGuard
{
	private const double WalkSeconds = 60;
	private const double RepeatSeconds = 600;
	private const int Examples = 5;
	private static Dictionary<long, long> m_previous = new Dictionary<long, long>();
	private static double m_nextWalk, m_nextRepeat;
	private static int m_reported;

	internal static void Update()
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextWalk) return;
		m_nextWalk = now + WalkSeconds * 1000;
		var current = new Dictionary<long, long>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || grid.MarkedForClose) continue;
			foreach (long owner in grid.BigOwners)
				if (owner != 0 && MySession.Static.Players.TryGetIdentity(owner) == null)
				{
					current[grid.EntityId] = owner;
					break;
				}
		}
		var persistent = current.Where(entry => m_previous.TryGetValue(entry.Key, out long owner) && owner == entry.Value).ToList();
		m_previous = current;
		if (persistent.Count == m_reported && (persistent.Count == 0 || now < m_nextRepeat)) return;
		m_reported = persistent.Count;
		m_nextRepeat = now + RepeatSeconds * 1000;
		Console.WriteLine("[ClusterPlugin] " + (persistent.Count == 0 ? "" : "WARNING: ") + "grids_unknown_owner count=" + persistent.Count
			+ " owners=" + persistent.Select(entry => entry.Value).Distinct().Count()
			+ " examples=" + string.Join(",", persistent.Take(Examples).Select(entry => entry.Key + ":" + entry.Value))
			+ (persistent.Count == 0 ? "" : " (grids owned by an identity this node does not know, #1365)"));
	}
}
