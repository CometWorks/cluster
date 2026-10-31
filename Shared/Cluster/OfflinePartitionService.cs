using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// T-0193 offline partitions, the cluster-wide half (Plan/Partitioning.md §3.6).
///
/// On the WORLD AUTHORITY it holds the registry's offline table (polled once a second, "changed since"
/// by revision) and does three things with it:
///   * requirement 4 and 5 ("enters"): the partition PROXIES - location, radius, member spheres - meet
///     the cluster position registry here, which knows every player and actively viewed camera EXACTLY
///     and which node each is on. A player or camera closer than <c>offline.wakeMeters</c> to an offline
///     partition's members asks the gateway to load it onto that player's node. The gateway's own wake
///     pass (any partition closing on it, with speed lookahead) is the independent second trigger;
///   * requirement 6: the antennas captured at the freeze are merged into the published antenna graph
///     under origin <c>offline:&lt;partition&gt;</c>, ordered before every live report so a real claim of
///     the same antenna always wins, and kept for a short grace after the row ends so the handover to
///     the loading node's live report never blinks;
///   * the spawn trigger: the respawn points captured at the freeze are merged the same way, so an
///     offline medical room stays on every respawn screen; choosing it loads the partition (see
///     RespawnRegistryService.RouteExactRespawn).
///
/// On every node it is the client for "please load this offline partition" (spawn, view).
/// </summary>
public static class OfflinePartitionService
{
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(15));
	private static readonly string GatewayRoot = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
	private static readonly object Sync = new object();
	private static Row[] m_rows = Array.Empty<Row>();
	private static long m_revision;
	private static AntennaGraphEntry[] m_antennas = Array.Empty<AntennaGraphEntry>();
	private static ClusterRespawnPoint[] m_respawns = Array.Empty<ClusterRespawnPoint>();
	/// <summary>Antennas of rows that just ended, published until the loading node's own report has had
	/// time to replace them (local milliseconds deadline).</summary>
	private static readonly Dictionary<long, (AntennaGraphEntry Entry, double Until)> m_endedAntennas = new Dictionary<long, (AntennaGraphEntry, double)>();
	private static int m_pollInFlight;
	private static double m_nextPoll, m_nextWake, m_nextPollError;
	private static readonly Dictionary<ulong, double> m_wakeAsked = new Dictionary<ulong, double>();
	// SE1-0084: the clients in a fast partition, from the Gateway (the offline table's FastClients, every poll).
	private static HashSet<ulong> m_fastClients = new HashSet<ulong>();
	private static long m_loadRequests, m_pollFailures;
	private const double EndedAntennaGraceMilliseconds = 30000;

	private sealed record Row(ulong Partition, double X, double Y, double Z, double Radius, double[][] MemberSpheres, int Members,
		string OfflineBy, DateTimeOffset OfflineAt, string LoadRequestedNode);

	public static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Gateway);
	}

	/// <summary>Antenna entries of offline partitions, for the authority's merge (never mutated in place).</summary>
	public static AntennaGraphEntry[] AntennaEntries() => AntennaEntries(out _);

	internal static AntennaGraphEntry[] AntennaEntries(out Dictionary<long, string> loadTargets)
	{
		lock (Sync)
		{
			var targets = m_rows.ToDictionary(r => r.Partition, r => r.LoadRequestedNode);
			loadTargets = m_antennas.ToDictionary(a => a.AntennaEntityId,
				a => targets.GetValueOrDefault(PartitionOfOrigin(a.Origin)));
			if (m_endedAntennas.Count == 0) return m_antennas;
			double now = ClusterRuntime.LocalMilliseconds;
			foreach (long expired in m_endedAntennas.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) m_endedAntennas.Remove(expired);
			HashSet<long> current = m_antennas.Select(x => x.AntennaEntityId).ToHashSet();
			return m_antennas.Concat(m_endedAntennas.Values.Where(x => !current.Contains(x.Entry.AntennaEntityId)).Select(x => x.Entry)).ToArray();
		}
	}

	/// <summary>Respawn points of offline partitions, for the authority's merge.</summary>
	public static ClusterRespawnPoint[] RespawnPoints() { lock (Sync) return m_respawns; }

	public static bool IsOfflineOrigin(string origin) => origin != null && origin.StartsWith("offline:", StringComparison.Ordinal);

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority || GatewayRoot == null || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now >= m_nextPoll && Interlocked.CompareExchange(ref m_pollInFlight, 1, 0) == 0)
		{
			m_nextPoll = now + 1000;
			Task.Run(Poll);
		}
		if (now >= m_nextWake)
		{
			m_nextWake = now + 1000;
			try { WakeForPlayers(now); }
			catch (Exception exception) { Console.WriteLine("[ClusterPlugin] offline wake pass failed: " + SecretRedactor.Redact(exception.Message)); }
		}
	}

	private static void Poll()
	{
		try
		{
			long since;
			lock (Sync) since = m_revision;
			string payload = Gateway.GetStringAsync(GatewayRoot + "/registry/partitions/offline?since=" + since.ToString(CultureInfo.InvariantCulture))
				.GetAwaiter().GetResult();
			using JsonDocument document = JsonDocument.Parse(payload);
			JsonElement root = document.RootElement;
			long revision = root.GetProperty("Revision").GetInt64();
			HashSet<ulong> fastClients = root.TryGetProperty("FastClients", out JsonElement fast) && fast.ValueKind == JsonValueKind.Array
				? fast.EnumerateArray().Select(x => x.GetUInt64()).ToHashSet() : new HashSet<ulong>();
			lock (Sync) m_fastClients = fastClients;
			if (root.GetProperty("Unchanged").GetBoolean()) return;
			var rows = new List<Row>();
			var antennas = new List<AntennaGraphEntry>();
			var respawns = new List<ClusterRespawnPoint>();
			foreach (JsonElement row in root.GetProperty("Partitions").EnumerateArray())
			{
				ulong partition = row.GetProperty("Partition").GetUInt64();
				string origin = "offline:" + partition.ToString(CultureInfo.InvariantCulture);
				double[][] spheres = row.TryGetProperty("MemberSpheres", out JsonElement memberSpheres) && memberSpheres.ValueKind == JsonValueKind.Array
					? memberSpheres.EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.GetDouble()).ToArray()).Where(x => x.Length >= 4).ToArray()
					: Array.Empty<double[]>();
				int members = row.TryGetProperty("Members", out JsonElement memberIds) && memberIds.ValueKind == JsonValueKind.Array ? memberIds.GetArrayLength() : 0;
				rows.Add(new Row(partition, row.GetProperty("X").GetDouble(), row.GetProperty("Y").GetDouble(), row.GetProperty("Z").GetDouble(),
					row.GetProperty("Radius").GetDouble(), spheres, members,
					row.TryGetProperty("OfflineBy", out JsonElement by) ? by.GetString() : null,
					row.TryGetProperty("OfflineAt", out JsonElement at) ? at.GetDateTimeOffset() : default,
					row.TryGetProperty("LoadRequestedNode", out JsonElement target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null));
				// A row whose captured payload cannot be read keeps its location (the proxy) and loses
				// only the extras; one bad row must not cost every other offline partition its antennas.
				try
				{
					if (row.TryGetProperty("Antennas", out JsonElement antennaJson) && antennaJson.ValueKind == JsonValueKind.String)
						foreach (AntennaGraphEntry entry in JsonSerializer.Deserialize<AntennaGraphEntry[]>(antennaJson.GetString()) ?? Array.Empty<AntennaGraphEntry>())
						{
							AntennaGraphCodec.ReadBuilderShared(entry);   // validates; a bad builder throws here, not in the merge
							antennas.Add(entry with { Origin = origin });
						}
				}
				catch (Exception exception)
				{
					Console.WriteLine("[ClusterPlugin] offline partition " + partition + " antennas unreadable: " + SecretRedactor.Redact(exception.Message));
				}
				try
				{
					if (row.TryGetProperty("RespawnPoints", out JsonElement respawnJson) && respawnJson.ValueKind == JsonValueKind.String)
						foreach (ClusterRespawnPoint point in JsonSerializer.Deserialize<ClusterRespawnPoint[]>(respawnJson.GetString()) ?? Array.Empty<ClusterRespawnPoint>())
							respawns.Add(point with { Origin = origin });
				}
				catch (Exception exception)
				{
					Console.WriteLine("[ClusterPlugin] offline partition " + partition + " respawn points unreadable: " + SecretRedactor.Redact(exception.Message));
				}
			}
			AntennaGraphEntry[] nextAntennas = antennas.GroupBy(x => x.AntennaEntityId).Select(x => x.First()).OrderBy(x => x.AntennaEntityId).ToArray();
			lock (Sync)
			{
				double now = ClusterRuntime.LocalMilliseconds;
				HashSet<long> kept = nextAntennas.Select(x => x.AntennaEntityId).ToHashSet();
				foreach (AntennaGraphEntry ended in m_antennas.Where(x => !kept.Contains(x.AntennaEntityId)))
					m_endedAntennas[ended.AntennaEntityId] = (ended, now + EndedAntennaGraceMilliseconds);
				foreach (long back in kept) m_endedAntennas.Remove(back);
				if (rows.Count != m_rows.Length)
					Console.WriteLine("[ClusterPlugin] offline table revision " + revision + ": " + rows.Count + " partition(s) offline, "
						+ nextAntennas.Length + " retained antenna(s), " + respawns.Count + " retained respawn point(s)");
				m_rows = rows.ToArray();
				m_antennas = nextAntennas;
				m_respawns = respawns.GroupBy(x => x.RespawnEntityId).Select(x => x.First()).OrderBy(x => x.RespawnEntityId).ToArray();
				m_revision = revision;
			}
			AntennaGraphService.RequestPublish();
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref m_pollFailures);
			if (ClusterRuntime.LocalMilliseconds >= m_nextPollError)
			{
				m_nextPollError = ClusterRuntime.LocalMilliseconds + 60000;
				Console.WriteLine("[ClusterPlugin] offline table poll failed: " + SecretRedactor.Redact(exception.Message));
			}
		}
		finally { Interlocked.Exchange(ref m_pollInFlight, 0); }
	}

	/// <summary>Requirement 5 "enters", from exact positions: every player and viewed camera the position
	/// registry knows, against every offline partition's member spheres.</summary>
	private static void WakeForPlayers(double now)
	{
		Row[] rows;
		HashSet<ulong> fastClients;
		lock (Sync) (rows, fastClients) = (m_rows, m_fastClients);
		(string Origin, ClusterPlayerPosition Player)[] players = PositionRegistryService.PlayersWithOrigin();
		// T-0292: a player or camera moving faster than fast.enterMps wakes nothing it flies past; it loads content
		// again once it slows down. SE1-0084: a player is fast when the Gateway says its partition is (the node's tick
		// detector, within a pass of the change); re-deriving it here from the ~2 s position refreshes came seconds
		// later. A camera has no partition to ask about, so cameras keep the position-over-time tracker, sampled even
		// with no offline rows so the state is current when rows appear.
		m_fastCameras.Sample(players.Where(x => x.Player.ClientId == 0).Select(x => new KeyValuePair<long, VRageMath.Vector3D>(
			x.Player.EntityId, new VRageMath.Vector3D(x.Player.X, x.Player.Y, x.Player.Z))), now / 1000.0, holdUnchangedSeconds: 5);
		if (rows.Length == 0) return;
		if (players.Length == 0) return;
		double wake = ClusterConfig.GetInt("offline.wakeMeters");
		foreach (Row row in rows)
		{
			if (m_wakeAsked.TryGetValue(row.Partition, out double asked) && now - asked < 5000) continue;
			foreach ((string origin, ClusterPlayerPosition player) in players)
			{
				double gap = Gap(row, player.X, player.Y, player.Z);
				if (gap >= wake) continue;
				if (player.ClientId != 0 ? fastClients.Contains(player.ClientId) : m_fastCameras.IsFast(player.EntityId))
				{
					Interlocked.Increment(ref m_fastWakesSuppressed);
					continue;
				}
				m_wakeAsked[row.Partition] = now;
				string reason = player.ClientId == 0 ? "view" : "approach";
				Console.WriteLine("[ClusterPlugin] offline wake: partition " + row.Partition + " " + reason + " by "
					+ (player.ClientId == 0 ? "camera " + player.EntityId : "player " + player.ClientId) + " on " + origin
					+ " at " + gap.ToString("F0", CultureInfo.InvariantCulture) + "m");
				RequestLoad(row.Partition, origin, reason);
				break;
			}
		}
		foreach (ulong gone in m_wakeAsked.Where(x => now - x.Value > 60000).Select(x => x.Key).ToArray()) m_wakeAsked.Remove(gone);
	}

	private static readonly FastMotionTracker m_fastCameras = new FastMotionTracker();
	private static long m_fastWakesSuppressed;
	/// <summary>T-0292: wake passes a fast player or camera would have triggered.</summary>
	public static long FastWakesSuppressed => Interlocked.Read(ref m_fastWakesSuppressed);

	private static double Gap(Row row, double x, double y, double z)
	{
		double dx = x - row.X, dy = y - row.Y, dz = z - row.Z;
		double bound = Math.Sqrt(dx * dx + dy * dy + dz * dz) - row.Radius;
		if (row.MemberSpheres.Length == 0) return bound;
		double min = double.MaxValue;
		foreach (double[] sphere in row.MemberSpheres)
		{
			double ex = x - sphere[0], ey = y - sphere[1], ez = z - sphere[2];
			double gap = Math.Sqrt(ex * ex + ey * ey + ez * ez) - sphere[3];
			if (gap < min) min = gap;
		}
		return Math.Max(bound, Math.Min(min, double.MaxValue));
	}

	/// <summary>Ask the gateway to load an offline partition onto <paramref name="node"/> (or wherever it can go).
	/// Fire-and-forget: demand is re-asserted by whoever still has it, so a lost request costs one retry interval.</summary>
	public static void RequestLoad(ulong partition, string node, string reason)
	{
		if (GatewayRoot == null) return;
		Interlocked.Increment(ref m_loadRequests);
		Task.Run(() =>
		{
			try
			{
				using HttpResponseMessage response = Gateway.PostAsync(GatewayRoot + "/registry/partitions/offline/load?partition="
					+ partition.ToString(CultureInfo.InvariantCulture) + "&node=" + Uri.EscapeDataString(node ?? string.Empty)
					+ "&reason=" + Uri.EscapeDataString(reason), null).GetAwaiter().GetResult();
				if (!response.IsSuccessStatusCode)
					Console.WriteLine("[ClusterPlugin] offline load request for partition " + partition + " answered " + (int)response.StatusCode);
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] offline load request for partition " + partition + " failed: " + SecretRedactor.Redact(exception.Message));
			}
		});
	}

	/// <summary>The partition id of an <c>offline:&lt;id&gt;</c> origin, or 0.</summary>
	public static ulong PartitionOfOrigin(string origin) =>
		IsOfflineOrigin(origin) && ulong.TryParse(origin.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;

	public static object Status()
	{
		lock (Sync)
			return new
			{
				authority = ClusterRuntime.IsWorldAuthority,
				revision = m_revision,
				offline = m_rows.Length,
				retainedAntennas = m_antennas.Length,
				endedAntennasInGrace = m_endedAntennas.Count,
				retainedRespawnPoints = m_respawns.Length,
				loadRequests = Interlocked.Read(ref m_loadRequests),
				pollFailures = Interlocked.Read(ref m_pollFailures),
				fastClients = m_fastClients.OrderBy(x => x).ToArray(),
				partitions = m_rows.Select(x => new { x.Partition, x.Members, x.Radius, center = new[] { x.X, x.Y, x.Z }, x.OfflineBy, x.OfflineAt, x.LoadRequestedNode }).ToArray(),
			};
	}
}
