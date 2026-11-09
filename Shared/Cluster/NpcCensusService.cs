using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// #1354: the cluster-wide NPC census, node side.
///
/// Every node reports the NPC grids of each partition with its partition report (<see cref="RowsJson"/>) and with an
/// offline offer (<see cref="Rows"/>); the registry merges the last complete report pass with the rows of the offline
/// partitions (ClusterRegistry/NpcCensus.cs) and every node polls the result every <see cref="PollMilliseconds"/> into
/// a local snapshot. NPC grid = a top-level grid (the root of its mechanical hierarchy) whose big owners are non-empty
/// and all NPC identities. #1379: the census also holds the top-level grids with no big owner (kind "unowned"), so the
/// cleanup rule's unvisited count survives a handover and an offline cycle too; mod-mes's Count never counts them.
///
/// The snapshot is published for the mod-mes MES plugin (#1321) in-process, with no assembly reference either way,
/// as BCL delegates in AppDomain data slots (interface v1, #1354):
///   * <see cref="CountSlot"/>: <c>Func&lt;double,double,double,double,int&gt;</c> Count(x, y, z, radius) = the NPC
///     grids, online on any node or in an offline partition, whose position lies within radius metres of (x,y,z);
///     -1 while unknown (no snapshot yet, or one older than <see cref="MaxAgeSeconds"/>): mod-mes then refuses the spawn;
///   * <see cref="AgeSlot"/>: <c>Func&lt;double&gt;</c> the snapshot age in seconds, -1 without one;
///   * <see cref="OnlineCountSlot"/> (DF-712): the same signature and -1 rule as Count, but only the NPC grids online on a
///     node. Offline partitions' NPC grids are never cleaned (no node runs MES on them), so a cluster-wide cap that counts
///     them fills up as the players move on and stops every spawn; the Octahedron plugin's cap uses this count.
/// Both run on the game thread inside an MES spawn decision: a read of the immutable snapshot, no I/O.
/// </summary>
public static class NpcCensusService
{
	public const string CountSlot = "CometWorks.Cluster.NpcCensus.v1";
	public const string AgeSlot = "CometWorks.Cluster.NpcCensus.Age.v1";
	public const string OnlineCountSlot = "CometWorks.Cluster.NpcCensus.Online.v1";
	private const double PollMilliseconds = 5000;
	internal const double MaxAgeSeconds = 60;

	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(15));
	private static readonly string GatewayRoot = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
	private static Snapshot m_snapshot;
	private static int m_pollInFlight;
	private static double m_nextPoll, m_nextPollError;
	private static long m_polls, m_pollFailures, m_lastBytes;

	/// <summary>One census answer. Never mutated after construction, so the game thread reads it without a lock.</summary>
	internal sealed class Snapshot
	{
		/// <summary>The NPC grids' positions (the kinds keep and npc), for Count, and whether each is in an offline partition.</summary>
		public double[] X, Y, Z;
		public bool[] InOffline;
		/// <summary>Every row's unvisited seconds, unowned ones included.</summary>
		public Dictionary<long, double> Unvisited;
		public int Online, Offline;
		/// <summary>#1443: the unopened container drops, online and offline.</summary>
		public DropRow[] Drops;
		/// <summary>The registry's age of the census when it answered, and the local time of the answer.</summary>
		public double AgeAtAnswer, AnsweredAt;
		/// <summary>SE-1316: the unix second the newest registry backup finished on the Gateway, 0 = none since it started.</summary>
		public long BackupAt;

		public double Age(double now) => AgeAtAnswer + Math.Max(0, now - AnsweredAt) / 1000.0;

		public int Count(double x, double y, double z, double radius, bool onlineOnly = false)
		{
			double limit = radius * radius;
			int count = 0;
			for (int i = 0; i < X.Length; i++)
			{
				if (onlineOnly && InOffline[i]) continue;
				double dx = X[i] - x, dy = Y[i] - y, dz = Z[i] - z;
				if (dx * dx + dy * dy + dz * dz <= limit) count++;
			}
			return count;
		}
	}

	public static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Gateway);
		AppDomain.CurrentDomain.SetData(CountSlot, (Func<double, double, double, double, int>)Count);
		AppDomain.CurrentDomain.SetData(AgeSlot, (Func<double>)AgeSeconds);
		AppDomain.CurrentDomain.SetData(OnlineCountSlot, (Func<double, double, double, double, int>)CountOnline);
	}

	public static int Count(double x, double y, double z, double radius) => Count(Volatile.Read(ref m_snapshot), ClusterRuntime.LocalMilliseconds, x, y, z, radius);

	/// <summary>DF-712: Count over the NPC grids online on a node only.</summary>
	public static int CountOnline(double x, double y, double z, double radius) => Count(Volatile.Read(ref m_snapshot), ClusterRuntime.LocalMilliseconds, x, y, z, radius, onlineOnly: true);

	internal static int Count(Snapshot snapshot, double now, double x, double y, double z, double radius, bool onlineOnly = false)
	{
		if (snapshot == null || !(snapshot.Age(now) <= MaxAgeSeconds)) return -1;
		return snapshot.Count(x, y, z, radius, onlineOnly);
	}

	/// <summary>The last census answer, null before the first. Immutable: read it without a lock.</summary>
	internal static Snapshot Current => Volatile.Read(ref m_snapshot);

	/// <summary>#1443: one unopened container drop of the census; Expires = the unix second its vanilla removal was due on the
	/// node that reported it (an offline row: at the freeze), 0 = none there.</summary>
	internal readonly record struct DropRow(long Id, bool Offline, long Expires);

	/// <summary>#1443: the removal deadline the census holds for a drop (unix seconds), 0 = none, to restore it on a reload.</summary>
	internal static long SnapshotDropExpires(long entityId)
	{
		if (Volatile.Read(ref m_snapshot)?.Drops is not { } drops) return 0;
		foreach (DropRow drop in drops)
			if (drop.Id == entityId) return drop.Expires;
		return 0;
	}

	public static double AgeSeconds() => Volatile.Read(ref m_snapshot) is { } snapshot ? snapshot.Age(ClusterRuntime.LocalMilliseconds) : -1;

	/// <summary>The unvisited seconds the census holds for a grid, to seed a node that has just got it.</summary>
	internal static double? SnapshotUnvisited(long entityId)
		=> Volatile.Read(ref m_snapshot) is { } snapshot && snapshot.Unvisited.TryGetValue(entityId, out double seconds) ? seconds : null;

	public static string Describe()
	{
		Snapshot snapshot = Volatile.Read(ref m_snapshot);
		return snapshot == null
			? "npcCensus=none polls=" + m_polls + " failures=" + m_pollFailures
			: string.Format(CultureInfo.InvariantCulture, "npcCensus online={0} offline={1} ageSeconds={2:F1} bytes={3} polls={4} failures={5}",
				snapshot.Online, snapshot.Offline, snapshot.Age(ClusterRuntime.LocalMilliseconds), Interlocked.Read(ref m_lastBytes), m_polls, m_pollFailures);
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || GatewayRoot == null || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextPoll || Interlocked.CompareExchange(ref m_pollInFlight, 1, 0) != 0) return;
		m_nextPoll = now + PollMilliseconds;
		Task.Run(Poll);
	}

	private static void Poll()
	{
		try
		{
			using HttpResponseMessage response = Gateway.GetAsync(GatewayRoot + "/registry/npc-census").GetAwaiter().GetResult();
			byte[] payload = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
			// 503 before the registry's first complete report pass: no census yet, the old snapshot ages out.
			response.EnsureSuccessStatusCode();
			Snapshot snapshot = Parse(payload, ClusterRuntime.LocalMilliseconds);
			Interlocked.Increment(ref m_polls);
			Interlocked.Exchange(ref m_lastBytes, payload.Length);
			Snapshot previous = Interlocked.Exchange(ref m_snapshot, snapshot);
			if (previous == null || Interlocked.Read(ref m_polls) % 60 == 0)   // the first one, then every 5 min
				Console.WriteLine("[ClusterPlugin] " + Describe());
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref m_pollFailures);
			if (ClusterRuntime.LocalMilliseconds >= m_nextPollError)
			{
				m_nextPollError = ClusterRuntime.LocalMilliseconds + 60000;
				Console.WriteLine("[ClusterPlugin] NPC census poll failed: " + SecretRedactor.Redact(exception.Message));
			}
		}
		finally { Interlocked.Exchange(ref m_pollInFlight, 0); }
	}

	internal static Snapshot Parse(byte[] payload, double answeredAt)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		JsonElement root = document.RootElement;
		JsonElement grids = root.GetProperty("grids");
		int length = 0;
		foreach (JsonElement grid in grids.EnumerateArray())
			if (!IsUnownedRow(grid)) length++;
		var snapshot = new Snapshot
		{
			X = new double[length], Y = new double[length], Z = new double[length], InOffline = new bool[length],
			Unvisited = new Dictionary<long, double>(grids.GetArrayLength()),
			Online = root.GetProperty("online").GetInt32(),
			Offline = root.GetProperty("offline").GetInt32(),
			AgeAtAnswer = root.GetProperty("ageSeconds").GetDouble(),
			AnsweredAt = answeredAt,
			BackupAt = root.TryGetProperty("backupAt", out JsonElement backupAt) && backupAt.ValueKind == JsonValueKind.Number ? backupAt.GetInt64() : 0,
		};
		int i = 0;
		var drops = new List<DropRow>();
		foreach (JsonElement grid in grids.EnumerateArray())
		{
			snapshot.Unvisited[grid.GetProperty("id").GetInt64()] = grid.GetProperty("u").GetDouble();
			if (grid.TryGetProperty("d", out JsonElement drop) && drop.ValueKind == JsonValueKind.True)
				drops.Add(new DropRow(grid.GetProperty("id").GetInt64(),
					grid.TryGetProperty("o", out JsonElement offline) && offline.ValueKind == JsonValueKind.True,
					grid.TryGetProperty("e", out JsonElement expires) ? expires.GetInt64() : 0));
			if (IsUnownedRow(grid)) continue;
			snapshot.X[i] = grid.GetProperty("x").GetDouble();
			snapshot.Y[i] = grid.GetProperty("y").GetDouble();
			snapshot.Z[i] = grid.GetProperty("z").GetDouble();
			snapshot.InOffline[i] = grid.TryGetProperty("o", out JsonElement inOffline) && inOffline.ValueKind == JsonValueKind.True;
			i++;
		}
		snapshot.Drops = drops.ToArray();
		return snapshot;
	}

	/// <summary>A row without a kind comes from a node that predates #1379, which reported NPC grids only.</summary>
	private static bool IsUnownedRow(JsonElement grid) => grid.TryGetProperty("k", out JsonElement kind) && kind.GetString() == "unowned";

	/// <summary>#1379: what the cleanup policy makes of a grid, by its big owners. The census leaves Player grids out.</summary>
	internal enum GridKind { Player, Keep, Npc, Unowned }

	private static readonly string[] KindNames = { "player", "keep", "npc", "unowned" };
	internal static string Name(GridKind kind) => KindNames[(int)kind];

	private static (HashSet<string> Tags, HashSet<long> Stations) m_keep;
	private static double m_keepAt = double.NaN;

	/// <summary>npc.sweepKeepFactions and, with cleanup.keepTradeStations, the faction trade stations' grid ids; rebuilt
	/// at most once a second (a report pass asks once per partition). Game thread.</summary>
	private static (HashSet<string> Tags, HashSet<long> Stations) Keep()
	{
		double now = ClusterRuntime.LocalMilliseconds;
		if (!double.IsNaN(m_keepAt) && now >= m_keepAt && now - m_keepAt < 1000) return m_keep;
		var tags = new HashSet<string>(ClusterConfig.Get("npc.sweepKeepFactions").Split(',', StringSplitOptions.RemoveEmptyEntries),
			StringComparer.OrdinalIgnoreCase);
		var stations = new HashSet<long>();
		if (ClusterConfig.GetBool("cleanup.keepTradeStations"))
			foreach (KeyValuePair<long, MyFaction> faction in MySession.Static.Factions)
				foreach (IMyFactionStation station in faction.Value.Stations)
					stations.Add(station.StationEntityId);
		m_keepAt = now;
		return m_keep = (tags, stations);
	}

	/// <summary>
	/// Player: a big owner that is not an NPC identity (a pilot, a player). Keep: a faction trade station, or a big owner
	/// in a faction of npc.sweepKeepFactions. Npc: every other grid whose big owners are all NPC identities, MES spawned
	/// or not. Unowned: no big owner at all (container drops, debris, cut-off pieces).
	/// </summary>
	internal static GridKind KindOf(MyCubeGrid grid)
	{
		(HashSet<string> tags, HashSet<long> stations) = Keep();
		if (stations.Contains(grid.EntityId)) return GridKind.Keep;
		List<long> owners = grid.BigOwners;
		if (owners == null || owners.Count == 0) return GridKind.Unowned;
		foreach (long owner in owners)
			if (!MySession.Static.Players.IdentityIsNpc(owner))
				return GridKind.Player;
		if (tags.Count != 0)
			foreach (long owner in owners)
				if (MySession.Static.Factions.GetPlayerFaction(owner)?.Tag is { } tag && tags.Contains(tag))
					return GridKind.Keep;
		return GridKind.Npc;
	}

	/// <summary>A top-level grid's kind; Player (left out of the census) for a subgrid or a closing grid.</summary>
	internal static GridKind TopLevelKind(MyCubeGrid grid)
	{
		if (grid.Closed || grid.MarkedForClose || MySession.Static == null) return GridKind.Player;
		if (MyGridPhysicalHierarchy.Static != null && MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid) return GridKind.Player;
		return KindOf(grid);
	}

	/// <summary>A top-level grid (the root of its mechanical hierarchy) whose big owners are non-empty and all NPCs.</summary>
	public static bool IsNpcGrid(MyCubeGrid grid) => TopLevelKind(grid) is GridKind.Keep or GridKind.Npc;

	/// <summary>The census grids (every kind but Player) among a partition's members as report JSON rows (PartitionReport's
	/// "npc"). Game thread.</summary>
	internal static string RowsJson(IEnumerable<long> members)
	{
		StringBuilder rows = null;
		foreach (long member in members)
		{
			if (!MyEntities.TryGetEntityById(member, out MyEntity entity) || entity is not MyCubeGrid grid) continue;
			GridKind kind = TopLevelKind(grid);
			if (kind == GridKind.Player) continue;
			Vector3D position = grid.PositionComp.WorldAABB.Center;
			(rows ??= new StringBuilder()).Append(rows.Length == 0 ? "" : ",").AppendFormat(CultureInfo.InvariantCulture,
				"{{\"id\":{0},\"x\":{1:F0},\"y\":{2:F0},\"z\":{3:F0},\"u\":{4:F0},\"k\":\"{5}\"",
				grid.EntityId, position.X, position.Y, position.Z, UnvisitedNpcSweeper.UnvisitedSeconds(grid.EntityId), Name(kind));
			if (IsDrop(grid, kind, out long expires))
				rows.Append(",\"d\":true").Append(expires > 0 ? ",\"e\":" + expires.ToString(CultureInfo.InvariantCulture) : "");
			rows.Append('}');
		}
		return rows?.ToString() ?? string.Empty;
	}

	/// <summary>
	/// #1443: an unopened container drop - vanilla spawns drops ownerless and NPC-flagged and takes the component away when a
	/// player opens the container - and the unix second its vanilla removal is due: this node's, else the census's (a drop
	/// just loaded from an offline partition, before the removal sweep gives it that deadline), 0 = none. Game thread.
	/// </summary>
	internal static bool IsDrop(MyCubeGrid grid, GridKind kind, out long expires)
	{
		expires = 0;
		if (kind != GridKind.Unowned || !grid.IsNpcSpawnedGrid || DropContainerRemovalService.DropComponent(grid) == null) return false;
		expires = DropContainerRemovalService.Deadline(grid.EntityId);
		if (expires == 0) expires = SnapshotDropExpires(grid.EntityId);
		return true;
	}

	/// <summary>The census grids among the members of an offline offer (OfflineRequest.Npc). Game thread.</summary>
	internal static object[] Rows(IEnumerable<MyEntity> members)
	{
		var rows = new List<object>();
		foreach (MyEntity entity in members)
		{
			if (entity is not MyCubeGrid grid) continue;
			GridKind kind = TopLevelKind(grid);
			if (kind == GridKind.Player) continue;
			Vector3D position = grid.PositionComp.WorldAABB.Center;
			bool drop = IsDrop(grid, kind, out long expires);
			rows.Add(new { id = grid.EntityId, x = Math.Round(position.X), y = Math.Round(position.Y), z = Math.Round(position.Z),
				u = Math.Round(UnvisitedNpcSweeper.UnvisitedSeconds(grid.EntityId)), k = Name(kind), d = drop, e = expires });
		}
		return rows.ToArray();
	}
}
