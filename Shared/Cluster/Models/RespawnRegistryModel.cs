using System;
using System.Collections.Generic;
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

public static class RespawnRegistryCodec
{
	public static string Write(IEnumerable<ClusterRespawnPoint> points)
		=> JsonSerializer.Serialize(new ClusterRespawnSnapshot(points.OrderBy(x => x.RespawnEntityId).ToArray()));

	public static ClusterRespawnSnapshot Read(string payload)
	{
		if (payload == null || payload.Length > 4 * 1024 * 1024)
			throw new InvalidOperationException("Invalid respawn registry payload");
		ClusterRespawnSnapshot snapshot = JsonSerializer.Deserialize<ClusterRespawnSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid respawn registry payload");
		if (snapshot.Points == null || snapshot.Points.Length > 4096
			|| snapshot.Points.Any(x => x == null || string.IsNullOrEmpty(x.Origin) || x.Origin.Length > 128
				|| x.RespawnEntityId <= 0 || x.GridEntityId <= 0 || x.PartitionId == 0 || x.Generation == 0
				|| x.Name == null || x.Name.Length > 1024 || x.OwnerIdentityId < 0 || x.ShareMode < 0 || x.ShareMode > 2
				|| !float.IsFinite(x.OxygenLevel) || x.OxygenLevel < 0f || x.OxygenLevel > 1f
				|| !double.IsFinite(x.X) || !double.IsFinite(x.Y) || !double.IsFinite(x.Z))
			|| snapshot.Points.Select(x => x.RespawnEntityId).Distinct().Count() != snapshot.Points.Length)
			throw new InvalidOperationException("Invalid respawn registry payload");
		return snapshot;
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
