using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// #1390: removes small wreckage made of cheap blocks, periodically and right before its partition is saved for good.
///
/// WRECKAGE = a physical group (every grid joined by rotors, pistons, hinges, connectors or landing gear) that is dynamic
/// throughout, holds at most <c>cleanup.wreckMaxBlocks</c> blocks, and of those at most <c>cleanup.wreckMaxOtherBlocks</c>
/// are not cheap. A CHEAP block is one whose every component is in <c>cleanup.wreckCheapComponents</c> (steel and interior
/// plates, construction components, girders, metal grids, tubes and bulletproof glass: armor, windows, catwalks, ladders,
/// interior walls). Owner, name, beacon and subgrid names are not looked at.
///
/// NEVER removed, whatever the regime: a group with a grid that has a player among its big owners and holds a working
/// terminal block while it has power (stored energy on a battery, or any other functional power producer); a group
/// with a grid of a kept owner (<c>npc.sweepKeepFactions</c>, trade stations, #1379); a seated pilot or a player
/// controlling it; a grid mid-handover, inside its import grace or in a partition this node does not own wholly.
///
/// THE REGIME, per node, from the node's own simulation ratio (the 1 s sample PartitionPersistence takes, so the save-test
/// seam drives it too) averaged over the last <see cref="WindowSamples"/> seconds: below
/// <c>cleanup.wreckAggressiveSimSpeed</c> (0.95) the node is AGGRESSIVE until the mean is back at the threshold plus
/// <see cref="Hysteresis"/>. Normal: a wreck goes once no player or viewed camera has been within the sync distance of it
/// for <c>cleanup.wreckSeconds</c>. Aggressive: once none has been within <c>cleanup.wreckTouchMeters</c> (50 m: standing on
/// it, building on it) for as long, so wreckage inside a player's sync range goes too.
///
/// BEFORE OFFLINE (<c>cleanup.wreckBeforeOffline</c>): the offline sweep and the drain call <see cref="BeforeOffline"/> on the
/// partition they are about to save, which removes its wreckage at once, without the wait, so the save never holds it.
/// </summary>
internal static class WreckageCleaner
{
	private const double TrackMilliseconds = 10000;
	private const double SampleMilliseconds = 1000;
	internal const int WindowSamples = 30;
	internal const double Hysteresis = 0.02;
	/// <summary>At most this many groups per periodic pass, the longest clear first, so one frame never closes hundreds.</summary>
	private const int MaxRemovalsPerPass = 25;

	/// <summary>Seconds with nobody within the sync distance (Far) and within the touch radius (Near), per wreck root.</summary>
	private static readonly Dictionary<long, (double Far, double Near)> m_clear = new Dictionary<long, (double, double)>();
	private static readonly Queue<double> m_samples = new Queue<double>();
	private static double m_sampleSum, m_lastSample = double.NaN, m_lastTrack = double.NaN;
	private static bool m_aggressive;
	private static int m_removed, m_removedNear, m_removedBeforeOffline, m_regimeChanges;
	private static string m_lastSkip;
	private static string m_cheapList;
	private static readonly Dictionary<MyCubeBlockDefinition, bool> m_cheap = new Dictionary<MyCubeBlockDefinition, bool>();

	internal static bool Aggressive => m_aggressive;

	/// <summary>The regime after one more window mean: in below the threshold, out at threshold + hysteresis; 0 = never in.</summary>
	internal static bool NextAggressive(bool aggressive, double mean, double threshold)
		=> threshold > 0 && (aggressive ? mean < threshold + Hysteresis : mean < threshold);

	/// <summary>True when a group of this size is wreckage: few blocks, and few that are not cheap.</summary>
	internal static bool IsWreckShape(int blocks, int otherBlocks, int maxBlocks, int maxOtherBlocks)
		=> blocks > 0 && blocks <= maxBlocks && otherBlocks <= maxOtherBlocks;

	/// <summary>True when a wreck is due: the clear seconds of the regime's radius have reached the limit.</summary>
	internal static bool IsDue(bool aggressive, double farSeconds, double nearSeconds, double limitSeconds)
		=> limitSeconds > 0 && (aggressive ? nearSeconds : farSeconds) >= limitSeconds;

	internal static void Update(PartitionManager partitions, PartitionPersistence persistence)
	{
		if (MySession.Static?.Ready != true || partitions == null || persistence == null) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (double.IsNaN(m_lastSample) || now - m_lastSample >= SampleMilliseconds)
		{
			m_lastSample = now;
			Sample(persistence.SimulationRatio);
		}
		if (!double.IsNaN(m_lastTrack) && now - m_lastTrack < TrackMilliseconds) return;
		double elapsed = double.IsNaN(m_lastTrack) ? 0 : Math.Min(now - m_lastTrack, 2 * TrackMilliseconds) / 1000.0;
		m_lastTrack = now;
		int limit = ClusterConfig.GetInt("cleanup.wreckSeconds");
		if (limit <= 0)
		{
			m_clear.Clear();
			return;
		}
		Settings settings = Settings.Read();
		List<Vector3D> players = UnvisitedNpcSweeper.PlayerPositions();
		var seen = new HashSet<long>();
		var grouped = new HashSet<long>();
		var due = new List<(MyCubeGrid Root, List<MyCubeGrid> Group, int Other, double Clear)>();
		var skips = new Dictionary<string, int>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || !IsRoot(grid)) continue;
			List<MyCubeGrid> group = Group(grid);
			if (!FirstVisit(group, grouped) || Wreck(group, settings, out int other) != null) continue;
			seen.Add(grid.EntityId);
			(double far, double near) = m_clear.TryGetValue(grid.EntityId, out var clear) ? clear : (0, 0);
			far = Near(group, settings.SyncMeters, players) ? 0 : far + elapsed;
			near = Near(group, settings.TouchMeters, players) ? 0 : near + elapsed;
			m_clear[grid.EntityId] = (far, near);
			if (!IsDue(m_aggressive, far, near, limit)) continue;
			string blocker = Blocker(grid, group);
			if (blocker != null)
			{
				skips[blocker] = skips.GetValueOrDefault(blocker) + 1;
				continue;
			}
			due.Add((grid, group, other, m_aggressive ? near : far));
		}
		foreach (long gone in m_clear.Keys.Where(id => !seen.Contains(id)).ToArray()) m_clear.Remove(gone);
		if (due.Count > MaxRemovalsPerPass)
		{
			skips["over " + MaxRemovalsPerPass + " per pass, next pass"] = due.Count - MaxRemovalsPerPass;
			due = due.OrderByDescending(x => x.Clear).Take(MaxRemovalsPerPass).ToList();
		}
		Dictionary<long, ulong> holders = partitions.Holders();
		foreach (var byPartition in due.GroupBy(x => holders.TryGetValue(x.Root.EntityId, out ulong holder) ? holder : 0))
		{
			ulong partition = byPartition.Key;
			var list = byPartition.ToList();
			string blocker = PartitionBlocker(partition, list.Select(x => x.Group), holders, persistence);
			if (blocker != null)
			{
				skips[blocker] = skips.GetValueOrDefault(blocker) + list.Count;
				continue;
			}
			bool emptied = Empties(partitions, partition, list.SelectMany(x => x.Group));
			if (emptied && persistence.EmptyReleaseBlocker(partition) is { } release)
			{
				skips["empties partition and " + release] = list.Count;
				continue;
			}
			foreach (var wreck in list)
			{
				bool nearPlayer = m_aggressive && Near(wreck.Group, settings.SyncMeters, players);
				if (nearPlayer) m_removedNear++;
				Remove(wreck.Root, wreck.Group, wreck.Other, partition, nearPlayer ? "near a player" : "away from players", wreck.Clear);
			}
			if (emptied && persistence.ReleaseEmptied(partition) is { } failed)
				Console.WriteLine("[ClusterPlugin] wreck cleanup emptied partition " + partition + " but could not release it: " + failed);
		}
		string skipped = string.Join(", ", skips.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value));
		if (skipped.Length != 0 && skipped != m_lastSkip)
			Console.WriteLine("[ClusterPlugin] wreck cleanup kept due wreck(s): " + skipped);
		m_lastSkip = skipped;
	}

	/// <summary>
	/// Removes the wreckage of a partition that is about to be saved and unloaded (offline sweep, drain), at once: no
	/// wait, but every safety rule and the regime's player radius. Not when it would empty the partition and
	/// <paramref name="mayEmpty"/> is false (a drain releases a memberless partition with its older save as the newest,
	/// #1334), nor when the empty release is not possible now. Game thread. The number of groups removed.
	/// </summary>
	internal static int BeforeOffline(PartitionManager.Partition partition, PartitionPersistence persistence, string cause, bool mayEmpty)
	{
		if (!ClusterConfig.GetBool("cleanup.wreckBeforeOffline") || MySession.Static?.Ready != true) return 0;
		Settings settings = Settings.Read();
		List<Vector3D> players = UnvisitedNpcSweeper.PlayerPositions();
		double radius = m_aggressive ? settings.TouchMeters : settings.SyncMeters;
		var wrecks = new List<(MyCubeGrid Root, List<MyCubeGrid> Group, int Other)>();
		var grouped = new HashSet<long>();
		foreach (long member in partition.Members)
		{
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity) || entity is not MyCubeGrid grid || !IsRoot(grid)) continue;
			List<MyCubeGrid> group = Group(grid);
			if (!FirstVisit(group, grouped) || Wreck(group, settings, out int other) != null || group.Any(x => !partition.Members.Contains(x.EntityId))) continue;
			if (Near(group, radius, players) || Blocker(grid, group) != null) continue;
			wrecks.Add((grid, group, other));
		}
		if (wrecks.Count == 0) return 0;
		bool emptied = partition.Members.All(wrecks.SelectMany(x => x.Group).Select(x => x.EntityId).ToHashSet().Contains);
		if (emptied && (!mayEmpty || persistence.EmptyReleaseBlocker(partition.Id) != null)) return 0;
		foreach (var wreck in wrecks)
		{
			m_removedBeforeOffline++;
			Remove(wreck.Root, wreck.Group, wreck.Other, partition.Id, cause, m_clear.TryGetValue(wreck.Root.EntityId, out var clear) ? clear.Far : 0);
		}
		if (emptied && persistence.ReleaseEmptied(partition.Id) is { } failed)
			Console.WriteLine("[ClusterPlugin] wreck cleanup emptied partition " + partition.Id + " but could not release it: " + failed);
		return wrecks.Count;
	}

	private static void Sample(double ratio)
	{
		m_samples.Enqueue(ratio);
		m_sampleSum += ratio;
		if (m_samples.Count > WindowSamples) m_sampleSum -= m_samples.Dequeue();
		// A full window only: a node's first seconds (loading, the startup claim) run slow, and on 10 samples that alone
		// turned a freshly started bench node aggressive (20261021-231936Z).
		if (m_samples.Count < WindowSamples) return;
		double mean = m_sampleSum / m_samples.Count;
		double threshold = double.Parse(ClusterConfig.Get("cleanup.wreckAggressiveSimSpeed"), CultureInfo.InvariantCulture);
		bool next = NextAggressive(m_aggressive, mean, threshold);
		if (next == m_aggressive) return;
		m_aggressive = next;
		m_regimeChanges++;
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] wreck cleanup regime {0}: sim speed {1:F3} over the last {2} s, threshold {3} (leave at +{4})",
			next ? "aggressive" : "normal", mean, m_samples.Count, threshold, Hysteresis));
	}

	private sealed class Settings
	{
		public int MaxBlocks, MaxOther;
		public double SyncMeters, TouchMeters;

		public static Settings Read()
		{
			string list = ClusterConfig.Get("cleanup.wreckCheapComponents");
			if (list != m_cheapList)
			{
				m_cheap.Clear();
				m_cheapList = list;
			}
			return new Settings
			{
				MaxBlocks = ClusterConfig.GetInt("cleanup.wreckMaxBlocks"),
				MaxOther = ClusterConfig.GetInt("cleanup.wreckMaxOtherBlocks"),
				SyncMeters = MyLayers.GetSyncDistance(),
				TouchMeters = double.Parse(ClusterConfig.Get("cleanup.wreckTouchMeters"), CultureInfo.InvariantCulture),
			};
		}
	}

	private static bool IsRoot(MyCubeGrid grid)
		=> grid.Save && !grid.IsPreview && !grid.Closed && !grid.MarkedForClose
			&& (MyGridPhysicalHierarchy.Static == null || MyGridPhysicalHierarchy.Static.GetRoot(grid) == grid);

	/// <summary>False when a grid of the group was seen this pass already: a connector-docked group has a root per ship.</summary>
	private static bool FirstVisit(List<MyCubeGrid> group, HashSet<long> grouped)
	{
		if (group.Any(grid => grouped.Contains(grid.EntityId))) return false;
		foreach (MyCubeGrid grid in group) grouped.Add(grid.EntityId);
		return true;
	}

	private static List<MyCubeGrid> Group(MyCubeGrid root)
		=> MyCubeGridGroups.Static?.Physical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToList() ?? new List<MyCubeGrid> { root };

	/// <summary>Why the group is not wreckage (static, too big, too many blocks that are not cheap, protected), or null.</summary>
	private static string Wreck(List<MyCubeGrid> group, Settings settings, out int other)
	{
		other = 0;
		int blocks = 0;
		foreach (MyCubeGrid grid in group)
		{
			if (grid.IsStatic) return "static";
			blocks += grid.BlocksCount;
		}
		if (blocks > settings.MaxBlocks) return "big";
		foreach (MyCubeGrid grid in group)
			foreach (MySlimBlock block in grid.GetBlocks())
				if (!IsCheap(block.BlockDefinition) && ++other > settings.MaxOther) return "costly";
		if (!IsWreckShape(blocks, other, settings.MaxBlocks, settings.MaxOther)) return "shape";
		foreach (MyCubeGrid grid in group)
		{
			NpcCensusService.GridKind kind = NpcCensusService.KindOf(grid);
			if (kind == NpcCensusService.GridKind.Keep) return "kept owner";
			if (kind == NpcCensusService.GridKind.Player && IsWorking(grid)) return "player grid, functional and powered";
		}
		return null;
	}

	/// <summary>Every component of the block is in cleanup.wreckCheapComponents. Cached per definition.</summary>
	private static bool IsCheap(MyCubeBlockDefinition definition)
	{
		if (definition == null) return false;
		if (m_cheap.TryGetValue(definition, out bool cheap)) return cheap;
		HashSet<string> names = new HashSet<string>(m_cheapList.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
		cheap = definition.Components != null && definition.Components.All(x => x.Definition != null && names.Contains(x.Definition.Id.SubtypeName));
		m_cheap[definition] = cheap;
		return cheap;
	}

	/// <summary>
	/// The protection test: a functional terminal block on a grid with power. Power = a battery holding stored energy (a
	/// switched-off ship keeps it), or any other functional power producer (reactor, engine, solar panel, turbine) whether
	/// it produces right now or not. Not the electrical distributor: it counts an empty battery's rated output as available.
	/// </summary>
	internal static bool IsWorking(MyCubeGrid grid)
	{
		bool functional = false, powered = false;
		foreach (MyTerminalBlock block in grid.GetFatBlocks<MyTerminalBlock>())
		{
			if (!block.IsFunctional) continue;
			functional = true;
			if (block is MyBatteryBlock battery ? battery.CurrentStoredPower > 0 : block is Sandbox.ModAPI.Ingame.IMyPowerProducer) powered = true;
		}
		return functional && powered;
	}

	/// <summary>True when a player or viewed camera is within <paramref name="radius"/> of a grid of the group.</summary>
	private static bool Near(List<MyCubeGrid> group, double radius, List<Vector3D> players)
		=> group.Any(grid => UnvisitedNpcSweeper.Visited(grid, radius, players));

	/// <summary>Why a wreck must stay this pass whatever the regime, or null.</summary>
	private static string Blocker(MyCubeGrid root, List<MyCubeGrid> group)
	{
		foreach (MyCubeGrid grid in group)
		{
			if (grid.Closed || grid.MarkedForClose) return "closing";
			if (ClusterRuntime.IsFrozen(grid.EntityId) || ImportActivationGuard.IsOpenFor(GuardUser.WreckCleanup, grid)) return "mid-handover";
			if (MySessionComponentTrashPatch.InImportGrace(grid.EntityId)) return "import grace";
			foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
				if (cockpit.Pilot != null) return "seated";
		}
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
			if (player.Controller?.ControlledEntity?.Entity?.GetTopMostParent() is MyCubeGrid controlled && group.Contains(controlled))
				return "player controls it";
		return null;
	}

	private static string PartitionBlocker(ulong partition, IEnumerable<List<MyCubeGrid>> groups, Dictionary<long, ulong> holders,
		PartitionPersistence persistence)
	{
		if (partition == 0 || !persistence.OwnsPartition(partition)) return "partition not owned here";
		foreach (List<MyCubeGrid> group in groups)
			if (group.Any(member => !holders.TryGetValue(member.EntityId, out ulong other) || other != partition))
				return "group spans partitions";
		return null;
	}

	private static bool Empties(PartitionManager partitions, ulong partition, IEnumerable<MyCubeGrid> closing)
	{
		HashSet<long> ids = closing.Select(x => x.EntityId).ToHashSet();
		return partitions.Peek(partition).Members.All(ids.Contains);
	}

	private static void Remove(MyCubeGrid root, List<MyCubeGrid> group, int other, ulong partition, string cause, double clearSeconds)
	{
		string name = root.DisplayName;
		int blocks = group.Sum(x => x.BlocksCount);
		Vector3D position = root.PositionComp.GetPosition();
		NpcCensusService.GridKind kind = NpcCensusService.KindOf(root);
		foreach (MyCubeGrid member in group) member.Close();
		m_clear.Remove(root.EntityId);
		m_removed++;
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] wreck cleanup removed grid {0} '{1}' ({2} grid(s), {3} blocks, {4} not cheap, kind {5}) at {6:F0},{7:F0},{8:F0} "
			+ "in partition {9}: {10}, regime {11}, clear {12:F0}s; {13} removed since start, {14} near players, {15} before offline",
			root.EntityId, name, group.Count, blocks, other, NpcCensusService.Name(kind), position.X, position.Y, position.Z, partition,
			cause, m_aggressive ? "aggressive" : "normal", clearSeconds, m_removed, m_removedNear, m_removedBeforeOffline));
	}

	public static string Describe()
		=> string.Format(CultureInfo.InvariantCulture, "wreckCleanup regime={0} simMean={1:F3} tracked={2} removed={3} removedNear={4} removedBeforeOffline={5} regimeChanges={6}",
			m_aggressive ? "aggressive" : "normal", m_samples.Count == 0 ? 1 : m_sampleSum / m_samples.Count, m_clear.Count, m_removed, m_removedNear,
			m_removedBeforeOffline, m_regimeChanges);
}
