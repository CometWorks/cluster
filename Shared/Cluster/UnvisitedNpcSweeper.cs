using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// #1354: removes NPC grids nobody visits, periodically.
///
/// MES spawns its encounters along the players' flight paths, and the ones nobody comes back to chain pockets of
/// NPCs into partitions the balancer cannot split or take offline (#1321, Viktor 2026-10-21: "need to remove
/// unvisited NPCs blocking partitioning"). Every <see cref="TrackMilliseconds"/> this node counts, per census grid it
/// holds (<see cref="NpcCensusService.TopLevelKind"/>: every top-level grid but a player's), the seconds it has been
/// ONLINE with no player or viewed camera within <c>npc.visitRadius</c> of it: a visit or a fresh spawn counts from zero. The count rides the partition
/// report and the offline offer into the registry's NPC census, so the node that gets the grid next - by a handover
/// or by loading its offline partition - seeds the count from there instead of starting over. Offline time does not
/// count: an offline pocket costs nothing.
///
/// Every <c>npc.sweepSeconds</c> a grid unvisited for <c>npc.unvisitedMinutes</c> is closed - after
/// <c>npc.unvisitedMinutes / npc.gapRemovalFactor</c> when the mod-mes plugin says it lies in a gap band of its
/// spawn pattern (the <see cref="InGapSlot"/> delegate, read on every pass because the plugin load order is not
/// fixed; without it there is no gap boost). MES tracks its grids through OnClose, so it drops them from its counts.
///
/// #1379, one cleanup policy for the same sweep (<see cref="NpcCensusService.KindOf"/>): a grid with a player or pilot
/// among its big owners, a grid of a faction in <c>npc.sweepKeepFactions</c> (OCT) and a faction trade station are never
/// taken. Every other NPC grid goes by the rule above, whether MES spawned it or not (a piece split off an MES grid has no
/// MES storage key, but keeps its NPC owner). A top-level grid with no big owner at all - a container drop, debris, a
/// cut-off piece - goes after <c>cleanup.unownedMinutes</c>, and for it only a player or camera within
/// <c>cleanup.unownedVisitRadius</c> (200 m) is a visit: drops lie along the pilots' routes, so a fly-by at the sync
/// distance reset most of them every few minutes (#1379, 57 % of the Octahedron's drops within 5 min). A static one
/// only with <c>cleanup.unownedStatic</c>.
///
/// NEVER taken: a grid with a pilot seated or a player controlling it, a grid with a player or camera within the
/// visit radius right now, a grid attached to a player or kept grid (a docked or landed player grid keeps the whole
/// group) or to a kept static ownerless one, an ownerless grid attached to an NPC grid (that one has its own, slower
/// rule), a grid mid-handover or inside its import grace, a grid of a partition this node does not own. A sweep that
/// would empty a partition releases it the way an admin grid delete does (#1334), or waits a pass.
/// </summary>
internal static class UnvisitedNpcSweeper
{
	public const string InGapSlot = "CometWorks.ModMes.InGap.v1";
	private const double TrackMilliseconds = 10000;

	/// <summary>Seconds unvisited per census grid (every kind but Player) on this node. Game thread only.</summary>
	private static readonly Dictionary<long, double> m_unvisited = new Dictionary<long, double>();
	private static double m_lastTrack = double.NaN, m_nextSweep;
	private static int m_removedTotal, m_removedInGap, m_removedUnowned;
	private static string m_lastSkip;

	/// <summary>The grid's unvisited seconds: this node's count, else the census's (a grid not tracked here yet), else 0.</summary>
	internal static double UnvisitedSeconds(long entityId)
		=> m_unvisited.TryGetValue(entityId, out double seconds) ? seconds : NpcCensusService.SnapshotUnvisited(entityId) ?? 0;

	internal static double VisitRadius()
	{
		string configured = ClusterConfig.Get("npc.visitRadius");
		return string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
			? MyLayers.GetSyncDistance()
			: double.Parse(configured, CultureInfo.InvariantCulture);
	}

	/// <summary>The visit radius of a kind: an ownerless grid needs a player close to it, not a fly-by (#1379).</summary>
	private static double VisitRadius(NpcCensusService.GridKind kind, double radius)
		=> kind == NpcCensusService.GridKind.Unowned ? double.Parse(ClusterConfig.Get("cleanup.unownedVisitRadius"), CultureInfo.InvariantCulture) : radius;

	/// <summary>
	/// The unvisited seconds after which a grid goes: the plain limit, or the gap limit inside a gap band. A factor
	/// below 1 would make the gap SLOWER than the rest, which is never what the knob is for, so it reads as 1.
	/// </summary>
	internal static double RemovalLimitSeconds(double unvisitedMinutes, double gapRemovalFactor, bool inGap)
		=> unvisitedMinutes * 60.0 / (inGap ? Math.Max(1.0, gapRemovalFactor) : 1.0);

	internal static void Update(PartitionManager partitions, PartitionPersistence persistence)
	{
		if (MySession.Static?.Ready != true || partitions == null || persistence == null) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (!double.IsNaN(m_lastTrack) && now - m_lastTrack < TrackMilliseconds) return;
		// A stall (a long save, a debugger) does not count as more than two passes of unvisited time.
		double elapsed = double.IsNaN(m_lastTrack) ? 0 : Math.Min(now - m_lastTrack, 2 * TrackMilliseconds) / 1000.0;
		m_lastTrack = now;
		double radius = VisitRadius();
		List<Vector3D> players = PlayerPositions();
		List<(MyCubeGrid Grid, NpcCensusService.GridKind Kind)> grids = Track(elapsed, radius, players);
		int interval = ClusterConfig.GetInt("npc.sweepSeconds");
		if (interval <= 0 || now < m_nextSweep) return;
		m_nextSweep = now + interval * 1000.0;
		Sweep(grids, radius, players, partitions, persistence);
	}

	/// <summary>Every player and viewed camera the cluster position registry knows, plus this node's own players
	/// at their exact positions (the registry's are up to a report old).</summary>
	internal static List<Vector3D> PlayerPositions()
	{
		var positions = new List<Vector3D>();
		foreach (ClusterPlayerPosition player in PositionRegistryService.Players)
			positions.Add(new Vector3D(player.X, player.Y, player.Z));
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
			if (player.Controller?.ControlledEntity?.Entity is MyEntity controlled && controlled.PositionComp != null)
				positions.Add(controlled.PositionComp.GetPosition());
			else if (player.Character?.PositionComp != null)
				positions.Add(player.Character.PositionComp.GetPosition());
		return positions;
	}

	/// <summary>True when a player is within <paramref name="radius"/> of the grid's bounding sphere.</summary>
	internal static bool Visited(MyCubeGrid grid, double radius, List<Vector3D> players)
	{
		BoundingSphereD volume = grid.PositionComp.WorldVolume;
		double reach = radius + volume.Radius;
		double limit = reach * reach;
		foreach (Vector3D player in players)
			if (Vector3D.DistanceSquared(player, volume.Center) <= limit)
				return true;
		return false;
	}

	private static List<(MyCubeGrid, NpcCensusService.GridKind)> Track(double elapsed, double radius, List<Vector3D> players)
	{
		var grids = new List<(MyCubeGrid, NpcCensusService.GridKind)>();
		var seen = new HashSet<long>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || !grid.Save) continue;
			NpcCensusService.GridKind kind = NpcCensusService.TopLevelKind(grid);
			if (kind == NpcCensusService.GridKind.Player) continue;
			grids.Add((grid, kind));
			seen.Add(grid.EntityId);
			// A grid new to this node starts from the census's count: a handover or an offline load carries it.
			double seconds = m_unvisited.TryGetValue(grid.EntityId, out double tracked)
				? tracked + elapsed
				: NpcCensusService.SnapshotUnvisited(grid.EntityId) ?? 0;
			m_unvisited[grid.EntityId] = Visited(grid, VisitRadius(kind, radius), players) ? 0 : seconds;
		}
		foreach (long gone in m_unvisited.Keys.Where(id => !seen.Contains(id)).ToArray()) m_unvisited.Remove(gone);
		return grids;
	}

	/// <summary>The unvisited minutes after which a grid of this kind goes; 0 = never (a kept grid, or a rule switched off).</summary>
	internal static int RemovalMinutes(NpcCensusService.GridKind kind, bool isStatic, int npcMinutes, int unownedMinutes, bool unownedStatic)
		=> kind switch
		{
			NpcCensusService.GridKind.Npc => npcMinutes,
			NpcCensusService.GridKind.Unowned when !isStatic || unownedStatic => unownedMinutes,
			_ => 0,
		};

	private static void Sweep(List<(MyCubeGrid Grid, NpcCensusService.GridKind Kind)> grids, double radius, List<Vector3D> players,
		PartitionManager partitions, PartitionPersistence persistence)
	{
		int npcMinutes = ClusterConfig.GetInt("npc.unvisitedMinutes"), unownedMinutes = ClusterConfig.GetInt("cleanup.unownedMinutes");
		bool unownedStatic = ClusterConfig.GetBool("cleanup.unownedStatic");
		double factor = double.Parse(ClusterConfig.Get("npc.gapRemovalFactor"), CultureInfo.InvariantCulture);
		Func<double, double, double, bool> inGap = AppDomain.CurrentDomain.GetData(InGapSlot) as Func<double, double, double, bool>;
		Dictionary<long, ulong> holders = null;
		var due = new List<(ulong Partition, double Overdue, (MyCubeGrid Root, NpcCensusService.GridKind Kind, List<MyCubeGrid> Group, bool Gap, double Seconds) Grid)>();
		var skips = new Dictionary<string, int>();
		foreach ((MyCubeGrid grid, NpcCensusService.GridKind kind) in grids)
		{
			int minutes = RemovalMinutes(kind, grid.IsStatic, npcMinutes, unownedMinutes, unownedStatic);
			if (minutes <= 0) continue;
			double seconds = m_unvisited.TryGetValue(grid.EntityId, out double tracked) ? tracked : 0;
			if (seconds < RemovalLimitSeconds(minutes, factor, inGap: true)) continue;
			Vector3D position = grid.PositionComp.WorldAABB.Center;
			bool gap = inGap != null && InGap(inGap, position);
			double limit = RemovalLimitSeconds(minutes, factor, gap);
			if (seconds < limit) continue;
			string blocker = RemovalBlocker(grid, kind, VisitRadius(kind, radius), players, unownedStatic, out List<MyCubeGrid> group);
			holders ??= partitions.Holders();
			ulong partition = holders.TryGetValue(grid.EntityId, out ulong holder) ? holder : 0;
			if (blocker == null && (partition == 0 || !persistence.OwnsPartition(partition))) blocker = "partition not owned here";
			if (blocker == null && group.Any(member => !holders.TryGetValue(member.EntityId, out ulong other) || other != partition))
				blocker = "group spans partitions";
			if (blocker != null)
			{
				skips[blocker] = skips.TryGetValue(blocker, out int count) ? count + 1 : 1;
				continue;
			}
			due.Add((partition, seconds / limit, (grid, kind, group, gap, seconds)));
		}
		// #1379: a backlog (the Octahedron's ~400 drops at the first pass) goes over several passes, most overdue first,
		// so one frame never closes hundreds of grids.
		int max = ClusterConfig.GetInt("npc.sweepMaxRemovals");
		if (max > 0 && due.Count > max)
		{
			skips["over npc.sweepMaxRemovals, next pass"] = due.Count - max;
			due = due.OrderByDescending(x => x.Overdue).Take(max).ToList();
		}
		var doomed = due.GroupBy(x => x.Partition).ToDictionary(x => x.Key, x => x.Select(y => y.Grid).ToList());
		foreach ((ulong partition, var list) in doomed)
		{
			HashSet<long> closing = list.SelectMany(x => x.Group).Select(x => x.EntityId).ToHashSet();
			bool emptied = partitions.Peek(partition).Members.All(closing.Contains);
			// An emptied partition is never saved again, so its newest save would still hold the grids: it has to be
			// released with an empty Final, as GridDeleteService does. Not possible right now: the next pass.
			if (emptied && persistence.EmptyReleaseBlocker(partition) is { } release)
			{
				skips["empties partition and " + release] = list.Count;
				continue;
			}
			foreach ((MyCubeGrid root, NpcCensusService.GridKind kind, List<MyCubeGrid> group, bool gap, double seconds) in list)
			{
				string name = root.DisplayName;
				int blocks = group.Sum(x => x.BlocksCount);
				Vector3D position = root.PositionComp.GetPosition();
				foreach (MyCubeGrid member in group) member.Close();
				m_removedTotal++;
				if (gap) m_removedInGap++;
				if (kind == NpcCensusService.GridKind.Unowned) m_removedUnowned++;
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[ClusterPlugin] npc sweep removed grid {0} '{1}' ({2} grid(s), {3} blocks) at {4:F0},{5:F0},{6:F0} in partition {7}: "
					+ "unvisited {8:F0}s, gap {9}, kind {12}; {10} removed since start, {11} in gaps, {13} unowned",
					root.EntityId, name, group.Count, blocks, position.X, position.Y, position.Z, partition, seconds,
					gap.ToString().ToLowerInvariant(), m_removedTotal, m_removedInGap, NpcCensusService.Name(kind), m_removedUnowned));
			}
			if (emptied && persistence.ReleaseEmptied(partition) is { } failed)
				Console.WriteLine("[ClusterPlugin] npc sweep emptied partition " + partition + " but could not release it: " + failed);
		}
		string skipped = string.Join(", ", skips.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value));
		if (skipped.Length != 0 && skipped != m_lastSkip)
			Console.WriteLine("[ClusterPlugin] npc sweep kept due grid(s): " + skipped);
		m_lastSkip = skipped;
	}

	/// <summary>The mod-mes gap test, guarded: a throwing plugin means no gap boost, never a failed sweep.</summary>
	private static bool InGap(Func<double, double, double, bool> inGap, Vector3D position)
	{
		try { return inGap(position.X, position.Y, position.Z); }
		catch (Exception exception)
		{
			Console.WriteLine("[ClusterPlugin] npc sweep: " + InGapSlot + " threw, no gap boost: " + exception.Message);
			return false;
		}
	}

	/// <summary>
	/// Why the grid must stay, or null. <paramref name="group"/> = every grid physically attached to it (rotors, pistons,
	/// connectors, landing gear), all of which go with it. None may be a player's or a kept grid; a kept static ownerless
	/// grid only as a mechanical subgrid of the root; an ownerless root takes no NPC grid but its own mechanical subgrids.
	/// </summary>
	internal static string RemovalBlocker(MyCubeGrid root, NpcCensusService.GridKind rootKind, double radius, List<Vector3D> players,
		bool unownedStatic, out List<MyCubeGrid> group)
	{
		group = MyCubeGridGroups.Static?.Physical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToList() ?? new List<MyCubeGrid> { root };
		HashSet<MyCubeGrid> mechanical = MyCubeGridGroups.Static?.Mechanical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToHashSet()
			?? new HashSet<MyCubeGrid> { root };
		foreach (MyCubeGrid grid in group)
		{
			if (grid.Closed || grid.MarkedForClose) return "closing";
			if (ClusterRuntime.IsFrozen(grid.EntityId) || ImportActivationGuard.IsOpenFor(GuardUser.NpcSweep, grid)) return "mid-handover";
			if (MySessionComponentTrashPatch.InImportGrace(grid.EntityId)) return "import grace";
			NpcCensusService.GridKind kind = grid == root ? rootKind : NpcCensusService.KindOf(grid);
			if (kind is NpcCensusService.GridKind.Player or NpcCensusService.GridKind.Keep) return "attached to a protected grid";
			if (!mechanical.Contains(grid))
			{
				if (kind == NpcCensusService.GridKind.Unowned && grid.IsStatic && !unownedStatic) return "attached to a kept static grid";
				if (kind == NpcCensusService.GridKind.Npc && rootKind == NpcCensusService.GridKind.Unowned) return "attached to an NPC grid";
			}
			foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
				if (cockpit.Pilot != null) return "seated";
			if (Visited(grid, radius, players)) return "player near";
		}
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
			if (player.Controller?.ControlledEntity?.Entity?.GetTopMostParent() is MyCubeGrid controlled && group.Contains(controlled))
				return "player controls it";
		return null;
	}

	public static string Describe()
		=> "npcSweep tracked=" + m_unvisited.Count + " removed=" + m_removedTotal + " removedInGap=" + m_removedInGap + " removedUnowned=" + m_removedUnowned;
}
