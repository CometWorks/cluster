using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

public sealed record ClusterRespawnPoint(
	string Origin,
	long RespawnEntityId,
	long GridEntityId,
	ulong PartitionId,
	uint Generation,
	string Name,
	long OwnerIdentityId,
	int ShareMode,
	bool Working,
	bool SpawnWithoutOxygen,
	bool SetFactionToSpawnee,
	float OxygenLevel,
	double X,
	double Y,
	double Z);

public sealed record ClusterRespawnSnapshot(ClusterRespawnPoint[] Points);

public sealed record ClusterRespawnRequest(ulong UserId, int SerialId, long IdentityId, long RespawnEntityId);

public sealed record ClusterRespawnDecision(
	long RespawnEntityId,
	long GridEntityId,
	string Origin,
	ulong PartitionId,
	uint Generation);

public sealed record ClusterRespawnExecution(
	Guid OperationId,
	ulong UserId,
	int SerialId,
	long IdentityId,
	long RespawnEntityId,
	long GridEntityId,
	string Origin,
	ulong PartitionId,
	uint Generation,
	bool JoinGame,
	string ModelName,
	uint PackedColor,
	string ClientState,
	// WA-6 pod placement: non-empty routes the execution through the vanilla respawn-SHIP path -
	// no medbay anchors (RespawnEntityId/GridEntityId/PartitionId/Generation may be zero), the pod
	// creates its own fresh partition on the executing node. NewIdentity carries vanilla's
	// FirstSpawnDone semantics (the executing node recreates the identity through the same seams
	// the local pod spawn uses). Both defaulted for wire compatibility.
	string RespawnShipId = null,
	bool NewIdentity = false,
	// SE1-0122 spawn point preview: move the dead client to the node of the medbay it previews, adopt its view
	// there and stream it the grid, but respawn nothing. JoinGame, ModelName and PackedColor are unused.
	bool Preview = false);

public sealed record ClusterRespawnExecutionResult(bool Ready, bool Success, long ControlledEntityId);

/// <summary>SE-1150: one respawn point the codec repaired or dropped. <see cref="Check"/> names the failed check
/// without the value, so a value that keeps changing is still the same problem; <see cref="Value"/> is for the log.</summary>
public readonly record struct RespawnPointProblem(long RespawnEntityId, string Check, string Value, bool Dropped);

public static class RespawnRegistryCodec
{
	public const int MaxNameLength = 1024;

	public static string Write(IEnumerable<ClusterRespawnPoint> points)
		=> JsonSerializer.Serialize(new ClusterRespawnSnapshot(points.OrderBy(x => x.RespawnEntityId).ToArray()));

	/// <summary>
	/// SE-1150: a bad point used to throw out the whole payload, so the World Authority kept a node's stale list for as long
	/// as one medbay misreported. The envelope still has to be sound; each point is checked on its own: a point with a
	/// fixable field (oxygen outside 0..1, a long name) is repaired, one that can't be used (no ids, generation 0, a
	/// non-finite position, a duplicate id) is dropped, and the rest are kept. <paramref name="problems"/> gets one entry
	/// per repaired or dropped point.
	/// </summary>
	public static ClusterRespawnSnapshot Read(string payload, List<RespawnPointProblem> problems = null)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid respawn registry payload");
		ClusterRespawnSnapshot snapshot = JsonSerializer.Deserialize<ClusterRespawnSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid respawn registry payload");
		if (snapshot.Points == null || snapshot.Points.Length > 4096)
			throw new InvalidOperationException("Invalid respawn registry payload");
		var kept = new List<ClusterRespawnPoint>(snapshot.Points.Length);
		var ids = new HashSet<long>();
		bool changed = false;
		foreach (ClusterRespawnPoint point in snapshot.Points)
		{
			ClusterRespawnPoint sane = Sanitize(point, out RespawnPointProblem? problem);
			if (sane != null && !ids.Add(sane.RespawnEntityId))
			{
				problem = new RespawnPointProblem(sane.RespawnEntityId, "duplicate RespawnEntityId", string.Empty, true);
				sane = null;
			}
			if (problem.HasValue) problems?.Add(problem.Value);
			if (sane != null) kept.Add(sane);
			changed |= !ReferenceEquals(sane, point);
		}
		return changed ? new ClusterRespawnSnapshot(kept.ToArray()) : snapshot;
	}

	/// <summary>
	/// SE-1150: <paramref name="point"/> itself when it passes every check, a repaired copy when only fixable fields are
	/// off, or null when it can't be used. The game's per-cell oxygen level is not clamped (a saved room amount applied
	/// to a smaller live room reads above 1), so a medbay can report more than 1 for as long as the room stays sealed.
	/// </summary>
	public static ClusterRespawnPoint Sanitize(ClusterRespawnPoint point, out RespawnPointProblem? problem)
	{
		problem = null;
		if (point == null)
		{
			problem = new RespawnPointProblem(0, "null point", string.Empty, true);
			return null;
		}
		string dropped = string.IsNullOrEmpty(point.Origin) || point.Origin.Length > 128 ? "Origin"
			: point.RespawnEntityId <= 0 ? "RespawnEntityId"
			: point.GridEntityId <= 0 ? "GridEntityId"
			: point.PartitionId == 0 ? "PartitionId"
			: point.Generation == 0 ? "Generation"
			: point.OwnerIdentityId < 0 ? "OwnerIdentityId"
			: point.ShareMode < 0 || point.ShareMode > 2 ? "ShareMode"
			: !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) ? "position"
			: null;
		if (dropped != null)
		{
			problem = new RespawnPointProblem(point.RespawnEntityId, dropped + " invalid", string.Empty, true);
			return null;
		}
		ClusterRespawnPoint sane = point;
		if (!float.IsFinite(point.OxygenLevel) || point.OxygenLevel < 0f || point.OxygenLevel > 1f)
		{
			problem = new RespawnPointProblem(point.RespawnEntityId, "OxygenLevel outside 0..1",
				point.OxygenLevel.ToString("R", CultureInfo.InvariantCulture), false);
			sane = sane with { OxygenLevel = float.IsFinite(point.OxygenLevel) ? Math.Clamp(point.OxygenLevel, 0f, 1f) : 0f };
		}
		if (point.Name == null || point.Name.Length > MaxNameLength)
		{
			problem = new RespawnPointProblem(point.RespawnEntityId, "Name length",
				point.Name == null ? "null" : point.Name.Length.ToString(CultureInfo.InvariantCulture), false);
			sane = sane with { Name = point.Name == null ? string.Empty : point.Name.Substring(0, MaxNameLength) };
		}
		return sane;
	}

	public static string WriteRequest(ClusterRespawnRequest request)
	{
		Validate(request);
		return JsonSerializer.Serialize(request);
	}

	public static ClusterRespawnRequest ReadRequest(string payload)
	{
		if (payload == null || payload.Length > 4096)
			throw new InvalidOperationException("Invalid respawn request payload");
		ClusterRespawnRequest request = JsonSerializer.Deserialize<ClusterRespawnRequest>(payload)
			?? throw new InvalidOperationException("Invalid respawn request payload");
		Validate(request);
		return request;
	}

	public static string WriteDecision(ClusterRespawnDecision decision)
	{
		Validate(decision);
		return JsonSerializer.Serialize(decision);
	}

	public static ClusterRespawnDecision ReadDecision(string payload)
	{
		if (payload == null || payload.Length > 4096)
			throw new InvalidOperationException("Invalid respawn decision payload");
		ClusterRespawnDecision decision = JsonSerializer.Deserialize<ClusterRespawnDecision>(payload)
			?? throw new InvalidOperationException("Invalid respawn decision payload");
		Validate(decision);
		return decision;
	}

	private static void Validate(ClusterRespawnRequest request)
	{
		if (request.UserId == 0 || request.SerialId < 0 || request.IdentityId <= 0 || request.RespawnEntityId <= 0)
			throw new InvalidOperationException("Invalid respawn request payload");
	}

	private static void Validate(ClusterRespawnDecision decision)
	{
		if (decision.RespawnEntityId <= 0 || decision.GridEntityId <= 0 || string.IsNullOrEmpty(decision.Origin)
			|| decision.Origin.Length > 128 || decision.PartitionId == 0 || decision.Generation == 0)
			throw new InvalidOperationException("Invalid respawn decision payload");
	}
}

/// <summary>
/// SE-1150: which repaired or dropped points are news. A node reports its list every second, so the same bad medbay
/// would be logged every second; this keeps the (point, check) pairs each scope (an origin, or the local capture) had
/// last time and passes on only the new ones. A pair that went away is forgotten, so it is logged again if it comes back.
/// The caller serializes.
/// </summary>
public sealed class RespawnProblemLog
{
	private readonly Dictionary<string, HashSet<(long, string)>> m_seen = new(StringComparer.Ordinal);

	public List<RespawnPointProblem> Fresh(string scope, List<RespawnPointProblem> problems)
	{
		var current = new HashSet<(long, string)>(problems.Select(x => (x.RespawnEntityId, x.Check)));
		m_seen.TryGetValue(scope, out HashSet<(long, string)> previous);
		if (current.Count == 0) m_seen.Remove(scope);
		else m_seen[scope] = current;
		return problems.Where(x => previous == null || !previous.Contains((x.RespawnEntityId, x.Check))).ToList();
	}

	public static string Line(string where, string origin, RespawnPointProblem problem)
		=> "[ClusterPlugin] respawn_point " + (problem.Dropped ? "dropped" : "repaired") + " where=" + where + " origin=" + origin
			+ " entity=" + problem.RespawnEntityId.ToString(CultureInfo.InvariantCulture) + " check='" + problem.Check + "'"
			+ (problem.Value.Length > 0 ? " value=" + problem.Value : string.Empty);
}

public static class RespawnArbiter
{
	public static ClusterRespawnDecision Select(IEnumerable<ClusterRespawnPoint> points, ClusterRespawnRequest request,
		Func<ClusterRespawnPoint, bool> canAccess)
	{
		if (points == null) throw new ArgumentNullException(nameof(points));
		if (canAccess == null) throw new ArgumentNullException(nameof(canAccess));
		ClusterRespawnPoint point = points.SingleOrDefault(x => x.RespawnEntityId == request.RespawnEntityId)
			?? throw new InvalidOperationException("Respawn point is unavailable");
		if (!point.Working) throw new InvalidOperationException("Respawn point is not working");
		if (!point.SpawnWithoutOxygen && point.OxygenLevel <= 0f)
			throw new InvalidOperationException("Respawn point has no oxygen");
		if (!canAccess(point)) throw new InvalidOperationException("Respawn point access denied");
		return new ClusterRespawnDecision(point.RespawnEntityId, point.GridEntityId, point.Origin, point.PartitionId, point.Generation);
	}
}

/// <summary>
/// WA-6 arbitration cooldown/in-flight guard. Arbitration is a pure function of the registry, so
/// re-arbitrating the SAME logical respawn is naturally idempotent and stays allowed at any rate -
/// the routing node's pending-respawn retry and the chaos lane's fresh-operation-id retries depend
/// on that. What must not happen is a decision for a DIFFERENT respawn point for the same identity
/// while a recently decided respawn may still be executing: that is two transactions racing toward
/// two medbays. Admit checks, Record commits - a failed arbitration records nothing, so a player
/// refused at a broken medbay can immediately pick another. The caller serializes (WA arbitrations
/// run on the game thread).
/// </summary>
public sealed class RespawnArbitrationWindow
{
	private readonly double m_windowMilliseconds;
	private readonly Dictionary<long, Entry> m_entries = new Dictionary<long, Entry>();

	public RespawnArbitrationWindow(double windowMilliseconds) => m_windowMilliseconds = windowMilliseconds;

	/// <summary>Throws when a different point was decided for this identity inside the window.</summary>
	public void Admit(long identityId, long respawnEntityId, double nowMilliseconds)
	{
		Prune(nowMilliseconds);
		if (m_entries.TryGetValue(identityId, out Entry entry) && entry.RespawnEntityId != respawnEntityId)
			throw new InvalidOperationException("A respawn to a different point was just arbitrated for this identity");
	}

	/// <summary>Commits a successful decision, opening (or refreshing) the identity's window.</summary>
	public void Record(long identityId, long respawnEntityId, double nowMilliseconds)
		=> m_entries[identityId] = new Entry(respawnEntityId, nowMilliseconds);

	private void Prune(double nowMilliseconds)
	{
		List<long> expired = null;
		foreach (KeyValuePair<long, Entry> pair in m_entries)
			if (nowMilliseconds - pair.Value.DecidedAt >= m_windowMilliseconds)
				(expired ??= new List<long>()).Add(pair.Key);
		if (expired != null)
			foreach (long key in expired)
				m_entries.Remove(key);
	}

	private readonly struct Entry
	{
		public readonly long RespawnEntityId;
		public readonly double DecidedAt;
		public Entry(long respawnEntityId, double decidedAt) { RespawnEntityId = respawnEntityId; DecidedAt = decidedAt; }
	}
}
