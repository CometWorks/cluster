using System;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// A spawn decided at the World Authority and executed on the node that owns the target player's partition.
/// <paramref name="OperationId"/> is the exactly-once key: the executing node replays the recorded result
/// instead of spawning twice, which is what keeps an encounter from appearing once per node.
/// </summary>
public sealed record ClusterSpawnRequest(
	Guid OperationId,
	string Origin,
	string PrefabName,
	ulong TargetClient,
	double X,
	double Y,
	double Z,
	long OwnerId,
	string BeaconName);

/// <summary>
/// A dispatched spawn's outcome. <paramref name="Pending"/> marks the window between dispatch and the prefab
/// manager's asynchronous completion callback, during which neither success nor failure is known yet.
/// </summary>
public sealed record ClusterSpawnResult(bool Success, long[] GridEntityIds, string Error, bool Pending = false);

public static class SpawnDirectorCodec
{
	public const int MaxPrefabNameLength = 256;

	public static string WriteRequest(ClusterSpawnRequest request)
	{
		Validate(request);
		return JsonSerializer.Serialize(request);
	}

	public static ClusterSpawnRequest ReadRequest(string payload)
	{
		if (payload == null || payload.Length > 64 * 1024)
			throw new InvalidOperationException("Invalid spawn request payload");
		ClusterSpawnRequest request = JsonSerializer.Deserialize<ClusterSpawnRequest>(payload)
			?? throw new InvalidOperationException("Invalid spawn request payload");
		Validate(request);
		return request;
	}

	/// <summary>
	/// Validates everything the World Authority decides. <see cref="ClusterSpawnRequest.Origin"/> is deliberately
	/// not required here: the owning node is resolved during dispatch, so a decision is well-formed without it.
	/// </summary>
	public static void ValidateDecision(ClusterSpawnRequest request)
	{
		if (request == null || request.OperationId == Guid.Empty
			|| request.Origin?.Length > 128
			|| string.IsNullOrEmpty(request.PrefabName) || request.PrefabName.Length > MaxPrefabNameLength
			|| request.BeaconName?.Length > MaxPrefabNameLength
			|| !double.IsFinite(request.X) || !double.IsFinite(request.Y) || !double.IsFinite(request.Z))
			throw new InvalidOperationException("Invalid spawn request payload");
	}

	/// <summary>Validates a dispatched request, which must name the node that is to execute it.</summary>
	public static void Validate(ClusterSpawnRequest request)
	{
		ValidateDecision(request);
		if (string.IsNullOrEmpty(request.Origin))
			throw new InvalidOperationException("Invalid spawn request payload");
	}
}
