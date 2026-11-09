using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-1316: removes the ships bots left behind, right after each cluster backup.
///
/// The sebot traders and taxi drivers give a ship up when it gets stuck, runs dry or does not stream in, and order a
/// new one; the old one stays in the world (about 20 an hour on octahedron-test, 367 removed by hand on 2026-11-09).
///
/// A BOT SHIP is a top-level grid whose block owners (big and small) are all identities of <c>cleanup.botSteamIds</c>.
/// It is IN USE while a pilot sits in any grid of its physical group, a player controls it, or a player or bot stands
/// within <c>cleanup.botShipUseRadius</c> metres of it. Every <see cref="TrackMilliseconds"/> the node stamps a bot
/// ship in use with the time (unix seconds). The stamp rides the grid's object builder (<see cref="BlockBuilderEntry"/>,
/// <see cref="Key"/>), so a handover, a partition save, an offline partition and a node restart all keep it. A ship
/// first seen without one counts as used then: switching the cleanup on starts every ship's clock.
///
/// A bot ship is ABANDONED when the newest finished registry backup (<see cref="NpcCensusService.Snapshot.BackupAt"/>,
/// the Gateway's census) was taken <c>cleanup.botShipIdleMinutes</c> or more after its last use. That backup holds the
/// ship unused, so it can be restored from there. It is removed:
/// - right after each backup: the first pass that sees a new backup time takes at most <c>cleanup.botShipsMaxPerRun</c>
///   on this node, the longest unused first;
/// - right before its partition goes offline (the offline sweep), so an offline partition keeps none, at most the same
///   cap per partition.
///
/// NEVER removed: a ship in use right now, a ship attached (docked, landed, on rotors) to any grid that is not a bot
/// ship or its own ownerless subgrid, a grid mid-handover or whose links are still settling after an arrival
/// (<c>gridDelete.linkSettleSeconds</c>), a group spanning partitions or in a
/// partition this node does not own. <c>cleanup.botShips</c> = dryrun logs each ship it would remove and keeps it.
/// Every removal is logged with the grid's id, name, owners, position, partition and the reason.
/// </summary>
internal static class BotShipCleaner
{
	internal static readonly Guid Key = new Guid("6f1e9c55-2a0b-4d9e-9a51-5e1316b07500");
	private const double TrackMilliseconds = 10000;
	/// <summary>How long a stamp outlives its grid's absence from the entity list (an import in progress, a closed grid).</summary>
	private const long MissingSeconds = 600;

	/// <summary>Last use (unix seconds) by grid id. Written by Init on any thread, so concurrent.</summary>
	private static readonly ConcurrentDictionary<long, long> m_lastUse = new ConcurrentDictionary<long, long>();
	private static double m_lastTrack = double.NaN;
	private static long m_lastBackupRun;
	private static string m_botsText;
	private static SteamIdRanges m_bots;
	private static int m_removed, m_removedBeforeOffline, m_wouldRemove;
	private static readonly HashSet<long> m_dryRunLogged = new HashSet<long>();
	private static readonly Dictionary<long, long> m_missingSince = new Dictionary<long, long>();

	internal enum Mode { Off, DryRun, On }

	internal static Mode Current => ClusterConfig.Get("cleanup.botShips") switch
	{
		"on" => Mode.On,
		"dryrun" => Mode.DryRun,
		_ => Mode.Off,
	};

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && Sync.IsServer && Current != Mode.Off;

	private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	/// <summary>The rule: the backup was taken at least <paramref name="idleSeconds"/> after the ship's last use.</summary>
	internal static bool Abandoned(long lastUse, long backupAt, long idleSeconds)
		=> lastUse > 0 && backupAt > 0 && idleSeconds > 0 && backupAt - lastUse >= idleSeconds;

	/// <summary>MyCubeGrid.Init postfix, any thread: take the carried stamp, out of the grid's runtime storage.</summary>
	internal static void Initialized(MyCubeGrid grid, MyObjectBuilder_EntityBase builder)
	{
		if (grid == null || builder == null || !ClusterRuntime.IsClustered) return;
		string carried = BlockBuilderEntry.Read(builder, Key);
		if (carried == null) return;
		grid.Storage?.RemoveValue(Key); // unregistered: vanilla would log it on every save and drop it
		if (Active && long.TryParse(carried, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lastUse))
			m_lastUse[builder.EntityId] = lastUse;
	}

	/// <summary>MyCubeGrid.GetObjectBuilder postfix: write the stamp of a tracked bot ship into the builder.</summary>
	internal static void Saving(MyCubeGrid grid, MyObjectBuilder_EntityBase builder)
	{
		if (grid == null || builder == null || !m_lastUse.TryGetValue(grid.EntityId, out long lastUse) || !Active) return;
		BlockBuilderEntry.Write(builder, Key, lastUse.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>The grid's last use (test probe), null when it is not tracked.</summary>
	internal static long? LastUse(long entityId) => m_lastUse.TryGetValue(entityId, out long lastUse) ? lastUse : null;

	/// <summary>Test seam: set a grid's last use, as if it had been carried in.</summary>
	internal static void SetLastUse(long entityId, long lastUse) => m_lastUse[entityId] = lastUse;

	private static SteamIdRanges Bots()
	{
		string text = ClusterConfig.Get("cleanup.botSteamIds");
		if (text != m_botsText)
		{
			m_botsText = text;
			m_bots = SteamIdRanges.Parse(text);
		}
		return m_bots;
	}

	/// <summary>Every block owner (big and small) is a bot of <paramref name="bots"/>, and there is one.</summary>
	internal static bool IsBotShip(MyCubeGrid grid, SteamIdRanges bots)
	{
		if (bots == null || bots.IsEmpty || grid.Closed || grid.MarkedForClose) return false;
		bool any = false;
		foreach (long owner in grid.BigOwners.Concat(grid.SmallOwners))
		{
			if (!bots.Contains(MySession.Static.Players.TryGetSteamId(owner))) return false;
			any = true;
		}
		return any;
	}

	private static bool IsRoot(MyCubeGrid grid)
		=> grid.Save && !grid.IsPreview && !grid.Closed && !grid.MarkedForClose
			&& (MyGridPhysicalHierarchy.Static == null || MyGridPhysicalHierarchy.Static.GetRoot(grid) == grid);

	private static List<MyCubeGrid> Group(MyCubeGrid root)
		=> MyCubeGridGroups.Static?.Physical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToList() ?? new List<MyCubeGrid> { root };

	/// <summary>Why the group is in use right now, or null.</summary>
	private static string InUse(List<MyCubeGrid> group, double radius, List<Vector3D> people)
	{
		foreach (MyCubeGrid grid in group)
		{
			foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
				if (cockpit.Pilot != null) return "seated";
			if (UnvisitedNpcSweeper.Visited(grid, radius, people)) return "someone near";
		}
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
			if (player.Controller?.ControlledEntity?.Entity?.GetTopMostParent() is MyCubeGrid controlled && group.Contains(controlled))
				return "player controls it";
		return null;
	}

	/// <summary>Game thread, from ClusterRuntime: track use, and run once for each new backup.</summary>
	internal static void Update(PartitionManager partitions, PartitionPersistence persistence)
	{
		if (MySession.Static?.Ready != true || partitions == null || persistence == null) return;
		if (!Active)
		{
			if (m_lastUse.Count > 0) m_lastUse.Clear();
			m_missingSince.Clear();
			m_lastBackupRun = 0;
			return;
		}
		double now = ClusterRuntime.LocalMilliseconds;
		if (!double.IsNaN(m_lastTrack) && now - m_lastTrack < TrackMilliseconds) return;
		m_lastTrack = now;
		List<MyCubeGrid> ships = Track();
		long backupAt = NpcCensusService.Current?.BackupAt ?? 0;
		if (backupAt == 0 || backupAt == m_lastBackupRun) return;
		m_lastBackupRun = backupAt;
		m_dryRunLogged.Clear();
		Run(ships, backupAt, partitions, persistence);
	}

	/// <summary>Stamps the bot ships in use (or first seen) and forgets every other grid. Returns this node's bot ship roots.</summary>
	private static List<MyCubeGrid> Track()
	{
		SteamIdRanges bots = Bots();
		double radius = ClusterConfig.GetInt("cleanup.botShipUseRadius");
		List<Vector3D> people = UnvisitedNpcSweeper.PlayerPositions();
		long now = Now;
		var ships = new List<MyCubeGrid>();
		var seen = new HashSet<long>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || !IsRoot(grid) || !IsBotShip(grid, bots)) continue;
			ships.Add(grid);
			seen.Add(grid.EntityId);
			// Not while it moves between nodes: the handover gate compares the builder before and after the import.
			bool settling = ClusterRuntime.IsFrozen(grid.EntityId) || MySessionComponentTrashPatch.InImportGrace(grid.EntityId);
			if (!m_lastUse.ContainsKey(grid.EntityId))
			{
				if (!settling) m_lastUse[grid.EntityId] = now;
			}
			else if (!settling && InUse(Group(grid), radius, people) != null)
				m_lastUse[grid.EntityId] = now;
		}
		// A grid that is no bot ship root (any more) is forgotten. One not in the entity list may be an import still being
		// built (its builder came in with a stamp the handover gate expects back), so it is forgotten only after a while.
		foreach (long id in m_lastUse.Keys.ToArray())
		{
			if (seen.Contains(id)) { m_missingSince.Remove(id); continue; }
			bool present = MyEntities.TryGetEntityById(id, out MyEntity entity) && entity is MyCubeGrid grid && !grid.Closed;
			if (!present && !m_missingSince.ContainsKey(id)) m_missingSince[id] = now;
			if (present || now - m_missingSince[id] > MissingSeconds)
			{
				m_lastUse.TryRemove(id, out _);
				m_missingSince.Remove(id);
			}
		}
		return ships;
	}

	/// <summary>Why <paramref name="root"/> must stay now, or null. <paramref name="group"/> = its physical group.</summary>
	private static string Blocker(MyCubeGrid root, List<MyCubeGrid> group, SteamIdRanges bots, double radius, List<Vector3D> people,
		Dictionary<long, ulong> holders, PartitionPersistence persistence, out ulong partition)
	{
		partition = holders.TryGetValue(root.EntityId, out ulong holder) ? holder : 0;
		HashSet<MyCubeGrid> mechanical = MyCubeGridGroups.Static?.Mechanical.GetGroup(root)?.Nodes.Select(node => node.NodeData).ToHashSet()
			?? new HashSet<MyCubeGrid> { root };
		foreach (MyCubeGrid grid in group)
		{
			if (grid.Closed || grid.MarkedForClose) return "closing";
			if (ClusterRuntime.IsFrozen(grid.EntityId) || ImportActivationGuard.IsOpenFor(GuardUser.BotShipCleanup, grid)) return "mid-handover";
			// The link-settle part of the import grace only (SE-1317), as grid-delete: an offline wake is a recovery load whose
			// trash grace outlasts the woken partition's time online, and its ships go right before it goes offline again.
			if (MySessionComponentTrashPatch.LinksSettling(grid.EntityId)) return "import grace";
			bool ownSubgrid = mechanical.Contains(grid) && grid.BigOwners.Count == 0 && grid.SmallOwners.Count == 0;
			if (grid != root && !ownSubgrid && !IsBotShip(grid, bots)) return "attached to another grid";
			if (!holders.TryGetValue(grid.EntityId, out ulong other) || other != partition) return "group spans partitions";
		}
		if (InUse(group, radius, people) is { } use) return use;
		if (partition == 0 || !persistence.OwnsPartition(partition)) return "partition not owned here";
		return null;
	}

	private static void Run(List<MyCubeGrid> ships, long backupAt, PartitionManager partitions, PartitionPersistence persistence)
	{
		Mode mode = Current;
		long idle = ClusterConfig.GetInt("cleanup.botShipIdleMinutes") * 60L;
		int max = ClusterConfig.GetInt("cleanup.botShipsMaxPerRun");
		SteamIdRanges bots = Bots();
		double radius = ClusterConfig.GetInt("cleanup.botShipUseRadius");
		List<Vector3D> people = UnvisitedNpcSweeper.PlayerPositions();
		Dictionary<long, ulong> holders = partitions.Holders();
		var due = new List<(MyCubeGrid Root, List<MyCubeGrid> Group, ulong Partition, long LastUse)>();
		var kept = new Dictionary<string, int>();
		foreach (MyCubeGrid root in ships)
		{
			if (!m_lastUse.TryGetValue(root.EntityId, out long lastUse) || !Abandoned(lastUse, backupAt, idle)) continue;
			List<MyCubeGrid> group = Group(root);
			if (Blocker(root, group, bots, radius, people, holders, persistence, out ulong partition) is { } blocker)
			{
				kept[blocker] = kept.GetValueOrDefault(blocker) + 1;
				continue;
			}
			due.Add((root, group, partition, lastUse));
		}
		int found = due.Count;
		if (max > 0 && due.Count > max)
		{
			kept["over cleanup.botShipsMaxPerRun, next backup"] = due.Count - max;
			due = due.OrderBy(x => x.LastUse).Take(max).ToList();
		}
		int removed = 0;
		foreach (var byPartition in due.GroupBy(x => x.Partition))
			removed += Remove(byPartition.ToList(), partitions.Peek(byPartition.Key), persistence, mode, backupAt, "after backup", kept);
		Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] bot ship cleanup ({0}) after the backup of {1:u}: {2} bot ship(s) here, {3} abandoned and free, {4} {5}{6}",
			mode == Mode.On ? "on" : "dryrun", DateTimeOffset.FromUnixTimeSeconds(backupAt), ships.Count, found, removed,
			mode == Mode.On ? "removed" : "would go",
			kept.Count == 0 ? "" : "; kept: " + string.Join(", ", kept.OrderBy(x => x.Key).Select(x => x.Key + " " + x.Value))));
	}

	/// <summary>
	/// The offline sweep, right before <paramref name="partition"/> is saved for good: its abandoned bot ships go first, so
	/// the offline save never holds them. Returns how many went (the sweep then waits a pass, as for wreckage).
	/// </summary>
	internal static int BeforeOffline(PartitionManager.Partition partition, PartitionPersistence persistence, PartitionManager partitions)
	{
		if (!Active || MySession.Static?.Ready != true) return 0;
		long backupAt = NpcCensusService.Current?.BackupAt ?? 0;
		long idle = ClusterConfig.GetInt("cleanup.botShipIdleMinutes") * 60L;
		if (backupAt == 0) return 0;
		SteamIdRanges bots = Bots();
		double radius = ClusterConfig.GetInt("cleanup.botShipUseRadius");
		List<Vector3D> people = UnvisitedNpcSweeper.PlayerPositions();
		Dictionary<long, ulong> holders = partitions.Holders();
		var due = new List<(MyCubeGrid Root, List<MyCubeGrid> Group, ulong Partition, long LastUse)>();
		foreach (long member in partition.Members)
		{
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity) || entity is not MyCubeGrid root || !IsRoot(root)
				|| !m_lastUse.TryGetValue(root.EntityId, out long lastUse) || !Abandoned(lastUse, backupAt, idle) || !IsBotShip(root, bots))
				continue;
			List<MyCubeGrid> group = Group(root);
			if (Blocker(root, group, bots, radius, people, holders, persistence, out ulong holder) != null || holder != partition.Id) continue;
			due.Add((root, group, holder, lastUse));
		}
		int max = ClusterConfig.GetInt("cleanup.botShipsMaxPerRun");
		if (max > 0) due = due.OrderBy(x => x.LastUse).Take(max).ToList();
		if (due.Count == 0) return 0;
		int removed = Remove(due, partition, persistence, Current, backupAt, "before offline", new Dictionary<string, int>());
		m_removedBeforeOffline += Current == Mode.On ? removed : 0;
		return Current == Mode.On ? removed : 0; // a dry run never holds the partition online
	}

	/// <summary>Removes (or, in a dry run, logs) the ships of one partition; an emptied partition is released as a grid delete does.</summary>
	private static int Remove(List<(MyCubeGrid Root, List<MyCubeGrid> Group, ulong Partition, long LastUse)> ships,
		PartitionManager.Partition partition, PartitionPersistence persistence, Mode mode, long backupAt, string cause, Dictionary<string, int> kept)
	{
		HashSet<long> closing = ships.SelectMany(x => x.Group).Select(x => x.EntityId).ToHashSet();
		bool emptied = partition.Members.All(closing.Contains);
		// An emptied partition is never saved again, so its newest save would still hold the ships: it is released with an
		// empty Final (GridDeleteService, #1334). Not possible right now: they wait for the next run.
		if (mode == Mode.On && emptied && persistence.EmptyReleaseBlocker(partition.Id) is { } release)
		{
			kept["empties partition and " + release] = kept.GetValueOrDefault("empties partition and " + release) + ships.Count;
			return 0;
		}
		long now = Now;
		int count = 0;
		foreach ((MyCubeGrid root, List<MyCubeGrid> group, ulong id, long lastUse) in ships)
		{
			if (mode == Mode.DryRun && !m_dryRunLogged.Add(root.EntityId)) continue;
			Vector3D position = root.PositionComp.GetPosition();
			string line = string.Format(CultureInfo.InvariantCulture,
				"[ClusterPlugin] bot ship cleanup {0} grid {1} '{2}' ({3} grid(s), {4} blocks) owned by {5} at {6:F0},{7:F0},{8:F0} "
				+ "in partition {9}: unused since {10:u} ({11:F0} min), still so at the backup of {12:u}, {13}",
				mode == Mode.On ? "removed" : "would remove", root.EntityId, root.DisplayName, group.Count, group.Sum(x => x.BlocksCount),
				Owners(root), position.X, position.Y, position.Z, id, DateTimeOffset.FromUnixTimeSeconds(lastUse), (now - lastUse) / 60.0,
				DateTimeOffset.FromUnixTimeSeconds(backupAt), cause);
			if (mode == Mode.On)
			{
				foreach (MyCubeGrid member in group) member.Close();
				m_lastUse.TryRemove(root.EntityId, out _);
				m_removed++;
			}
			else m_wouldRemove++;
			count++;
			Console.WriteLine(line);
		}
		if (mode == Mode.On && emptied && count > 0 && persistence.ReleaseEmptied(partition.Id) is { } failed)
			Console.WriteLine("[ClusterPlugin] bot ship cleanup emptied partition " + partition.Id + " but could not release it: " + failed);
		return count;
	}

	/// <summary>"Name (identity, steam id)" of each block owner.</summary>
	private static string Owners(MyCubeGrid grid)
		=> string.Join(", ", grid.BigOwners.Concat(grid.SmallOwners).Distinct().Select(owner =>
			(MySession.Static.Players.TryGetIdentity(owner)?.DisplayName ?? "?") + " (" + owner.ToString(CultureInfo.InvariantCulture) + ", "
			+ MySession.Static.Players.TryGetSteamId(owner).ToString(CultureInfo.InvariantCulture) + ")"));

	/// <summary>GET /cluster/bot-ships: the mode, the backup time, and every bot ship this node holds. Game thread.</summary>
	internal static object Report()
	{
		long backupAt = NpcCensusService.Current?.BackupAt ?? 0;
		long idle = ClusterConfig.GetInt("cleanup.botShipIdleMinutes") * 60L;
		SteamIdRanges bots = Bots();
		var ships = new List<object>();
		foreach (MyEntity entity in MyEntities.GetEntities())
		{
			if (entity is not MyCubeGrid grid || !IsRoot(grid) || !IsBotShip(grid, bots)) continue;
			long? lastUse = LastUse(grid.EntityId);
			Vector3D position = grid.PositionComp.GetPosition();
			ships.Add(new
			{
				id = grid.EntityId, name = grid.DisplayName, owners = Owners(grid), lastUse,
				abandoned = lastUse is { } use && Abandoned(use, backupAt, idle),
				position = new[] { Math.Round(position.X), Math.Round(position.Y), Math.Round(position.Z) },
			});
		}
		return new { mode = ClusterConfig.Get("cleanup.botShips"), backupAt, lastRun = m_lastBackupRun, idleSeconds = idle,
			removed = m_removed, removedBeforeOffline = m_removedBeforeOffline, wouldRemove = m_wouldRemove, ships };
	}

	public static string Describe()
		=> string.Format(CultureInfo.InvariantCulture, "botShipCleanup mode={0} tracked={1} removed={2} removedBeforeOffline={3} wouldRemove={4} lastBackup={5}",
			ClusterConfig.Get("cleanup.botShips"), m_lastUse.Count, m_removed, m_removedBeforeOffline, m_wouldRemove, m_lastBackupRun);
}
