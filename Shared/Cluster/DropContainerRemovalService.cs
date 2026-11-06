using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Clustering;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;

namespace ClusterPlugin;

/// <summary>
/// #1391: vanilla's drop-container removal timer, kept with the grid. Vanilla puts every drop on the spawning
/// server's <c>m_delayedEntitiesForRemoval</c> (15 min) and, when the time runs out, closes the grid only if
/// that server still has it - the entry goes either way. A drop whose partition moved to another node never
/// despawned: the source dropped the entry with nothing to close, and the target had none. #1378 saw drops pile
/// up at ~+240/h with ~160 pilots. The handover hook carries the absolute deadline to the node that now owns the
/// grid, where vanilla's own removal runs; the sweep gives a drop that arrived with no deadline at all (a
/// recovery load, a node restart, a drop from before this fix) a fresh vanilla timeout.
/// </summary>
internal static class DropContainerRemovalService
{
	public const string HandoverKey = "cluster.drop-removal";
	private const double SweepIntervalMilliseconds = 60 * 1000;
	/// <summary>New deadlines per sweep: a world full of old drops expires over minutes, not in one tick.</summary>
	private const int MaxRegistrationsPerSweep = 32;
	internal static readonly IPluginGridHandover Handover = new RemovalHandover();
	private static IDisposable s_registration;
	private static double s_nextSweep;

	public static void Install()
	{
		s_registration?.Dispose();
		s_registration = Environment.GetEnvironmentVariable("CLUSTER_DROP_REMOVAL_HANDOVER") == "0"
			? null : PluginHandoverService.Register(HandoverKey, Handover);
		s_nextSweep = 0;
	}

	private static MySessionComponentContainerDropSystem System => MySession.Static?.GetComponent<MySessionComponentContainerDropSystem>();

	private sealed class RemovalHandover : IPluginGridHandover
	{
		public byte[] Leaving(long gridId)
		{
			MyEntityForRemoval entry = System?.m_delayedEntitiesForRemoval?.FirstOrDefault(item => item.EntityId == gridId);
			return entry == null ? null : BitConverter.GetBytes(DateTime.UtcNow.AddSeconds(entry.TimeLeft).Ticks);
		}

		/// <summary>The grid's timer now runs on the target; the GPS countdown that shares its entry goes too.</summary>
		public void Left(long gridId)
		{
			MySessionComponentContainerDropSystem system = System;
			if (system?.m_delayedEntitiesForRemoval == null) return;
			system.m_delayedEntitiesForRemoval.RemoveAll(item => item.EntityId == gridId);
			foreach (var gps in system.m_gpsList.Where(pair => pair.Value.EntityId == gridId).Select(pair => pair.Key).ToArray())
				system.m_gpsList.Remove(gps);
		}

		public void Stayed(long gridId) { }

		/// <summary>A deadline already past still gets one second: vanilla would have removed the drop by now.</summary>
		public void Arrived(long gridId, byte[] state)
		{
			if (state?.Length != sizeof(long)) return;
			long ticks = BitConverter.ToInt64(state, 0);
			if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return;
			MySessionComponentContainerDropSystem system = System;
			if (system?.m_delayedEntitiesForRemoval == null) return;
			int seconds = (int)Math.Max(1, Math.Ceiling((new DateTime(ticks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds));
			Register(system, gridId, seconds);
		}
	}

	/// <summary>Sets the grid's one removal entry (replacing any): a grid back on a node it left must not hold two.</summary>
	private static void Register(MySessionComponentContainerDropSystem system, long gridId, int seconds)
	{
		system.m_delayedEntitiesForRemoval.RemoveAll(item => item.EntityId == gridId);
		system.RegisterDelayedContainerRemovalInternal(gridId, seconds);
		if (MyEntities.TryGetEntityById(gridId, out MyEntity entity) && entity is MyCubeGrid grid)
			TrackGps(system, grid, system.m_delayedEntitiesForRemoval[^1]);
	}

	/// <summary>
	/// SE-1146: vanilla counts a drop's marker down, and at the deadline deletes it, from m_gpsList, which only the spawning
	/// node fills. On a node that got the drop by a handover or a reload the marker kept its last countdown ("T - 0sec") and
	/// the deadline deleted nothing. The marker's entry shares the drop's removal entry, as vanilla's does.
	/// </summary>
	private static void TrackGps(MySessionComponentContainerDropSystem system, MyCubeGrid grid, MyEntityForRemoval removal)
	{
		MyContainerDropComponent drop = DropComponent(grid);
		if (drop?.Entity == null || string.IsNullOrEmpty(drop.GPSName)) return;
		foreach (MyGps stale in system.m_gpsList.Keys.Where(gps => gps.EntityId == drop.Entity.EntityId || system.m_gpsList[gps].EntityId == grid.EntityId).ToArray())
			system.m_gpsList.Remove(stale);
		var marker = new MyGps { Name = drop.GPSName, EntityId = drop.Entity.EntityId, IsContainerGPS = true };
		marker.UpdateHash();
		system.m_gpsList.Add(marker, removal);
	}

	/// <summary>
	/// Regular nodes, game thread, once a minute while drops are on (vanilla's removal does not run otherwise):
	/// every grid holding an unopened drop container, in a partition this node owns, with no removal entry gets the
	/// vanilla timeout. A grid mid-handover or in its import grace is skipped: the handover commit carries the real
	/// deadline. A copy of another node's grid is never this node's to remove.
	/// </summary>
	internal static void Update(PartitionManager partitions, PartitionPersistence persistence)
	{
		if (partitions == null || persistence == null) return;
		if (MySession.Static?.Ready != true || !MySession.Static.EnableContainerDrops) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < s_nextSweep) return;
		s_nextSweep = now + SweepIntervalMilliseconds;
		MySessionComponentContainerDropSystem system = System;
		if (system?.m_delayedEntitiesForRemoval == null || system.m_definition == null) return;
		var scheduled = new Dictionary<long, MyEntityForRemoval>();
		foreach (MyEntityForRemoval item in system.m_delayedEntitiesForRemoval) scheduled.TryAdd(item.EntityId, item);
		var tracked = new HashSet<long>(system.m_gpsList.Values.Select(item => item.EntityId));
		Dictionary<long, ulong> holders = null;
		int added = 0;
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || !grid.IsNpcSpawnedGrid) continue;
			if (scheduled.TryGetValue(grid.EntityId, out MyEntityForRemoval removal))
			{
				// SE-1146: a drop restored with its removal entry (a node restart) has no marker countdown yet
				if (!tracked.Contains(grid.EntityId)) TrackGps(system, grid, removal);
				continue;
			}
			MyContainerDropComponent drop = DropComponent(grid);
			if (drop == null || ClusterRuntime.IsFrozen(grid.EntityId)) continue;
			// #1443: a drop back from an offline partition keeps the deadline the census holds for it, so it leaves the world
			// when vanilla would have removed it (the World Authority's drop cap stops counting it at that deadline). An
			// offline load is a recovery load, so its grids sit in the 300 s import grace; the census deadline is the one a
			// handover would carry, so it need not wait for the grace (a later Arrived replaces it with the same value).
			long expires = NpcCensusService.SnapshotDropExpires(grid.EntityId);
			if (expires == 0 && MySessionComponentTrashPatch.InImportGrace(grid.EntityId)) continue;
			holders ??= partitions.Holders();
			if (!holders.TryGetValue(grid.EntityId, out ulong partition) || !persistence.OwnsPartition(partition)) continue;
			int seconds = drop.Competetive ? system.m_definition.CompetetiveContainerGridTimeOut : system.m_definition.PersonalContainerGridTimeOut;
			string reason = "no-deadline";
			if (expires > 0)
			{
				seconds = (int)Math.Clamp(expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, seconds);
				reason = "census-deadline";
			}
			Register(system, grid.EntityId, seconds);
			Console.WriteLine("[ClusterPlugin] drop_container removal registered grid=" + grid.EntityId
				+ " competitive=" + drop.Competetive + " seconds=" + seconds + " reason=" + reason);
			if (++added >= MaxRegistrationsPerSweep) break;
		}
	}

	internal static MyContainerDropComponent DropComponent(MyCubeGrid grid)
	{
		// Untyped GetFatBlocks() walks the maintained fat-block list (see DropContainerDirectorService.Grids).
		foreach (var block in grid.GetFatBlocks())
			if (block is MyTerminalBlock terminal && terminal.Components.TryGet(out MyContainerDropComponent drop))
				return drop;
		return null;
	}

	/// <summary>#1443: the unix second this node's vanilla removal of the grid is due, 0 = no entry here. Game thread.</summary>
	internal static long Deadline(long gridId)
	{
		MyEntityForRemoval entry = System?.m_delayedEntitiesForRemoval?.FirstOrDefault(item => item.EntityId == gridId);
		return entry == null ? 0 : DateTimeOffset.UtcNow.AddSeconds(entry.TimeLeft).ToUnixTimeSeconds();
	}

	/// <summary>This node's removal entries, seconds left per grid. Game thread.</summary>
	public static object Removals()
	{
		MySessionComponentContainerDropSystem system = System;
		var entries = system?.m_delayedEntitiesForRemoval ?? new List<MyEntityForRemoval>();
		return new
		{
			node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty,
			count = entries.Count,
			removals = entries.Select(item => new
			{
				grid = item.EntityId,
				seconds = item.TimeLeft,
				// Vanilla closes only an NPC-spawned grid at the deadline; any other only loses its drop component.
				present = MyEntities.TryGetEntityById(item.EntityId, out MyEntity entity),
				npc = (entity as MyCubeGrid)?.IsNpcSpawnedGrid
			}).ToArray()
		};
	}

	/// <summary>Test knob: moves one grid's removal deadline on this node, so a lane can watch it run out.</summary>
	public static object SetTestDeadline(long gridId, int seconds)
	{
		if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
		MyEntityForRemoval entry = System?.m_delayedEntitiesForRemoval?.FirstOrDefault(item => item.EntityId == gridId)
			?? throw new KeyNotFoundException("No drop container removal entry for grid " + gridId);
		entry.TimeLeft = seconds;
		return Removals();
	}
}
