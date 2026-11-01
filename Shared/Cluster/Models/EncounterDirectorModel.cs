using System;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// A global encounter decided at the World Authority and executed on the node owning the target player.
/// <paramref name="OperationId"/> is the exactly-once key: it is what keeps an encounter from appearing once per
/// node when a dispatch is retried.
/// </summary>
public sealed record ClusterEncounterRequest(
	Guid OperationId,
	string Origin,
	string SpawnGroup,
	ulong TargetClient,
	string Kind = ClusterEncounterKinds.Global);

/// <summary>
/// Which generator a dispatch drives. All are decided at the World Authority and executed on the node owning the
/// target player, but they differ in what the WA gets to decide: a global encounter's or cargo ship's spawn group
/// is a definition lookup the WA can do, while a planetary installation or meteor wave is placed entirely from the
/// target's own position.
/// </summary>
public static class ClusterEncounterKinds
{
	public const string Global = "global";
	public const string Planetary = "planetary";
	public const string Cargo = "cargo";
	public const string Meteor = "meteor";
}

public sealed record ClusterEncounterResult(
	bool Spawned,
	string SpawnGroup,
	int EncountersBefore,
	int EncountersAfter,
	string Error);

public static class EncounterDirectorCodec
{
	public const int MaxSpawnGroupLength = 256;

	public static string WriteRequest(ClusterEncounterRequest request)
	{
		Validate(request);
		return JsonSerializer.Serialize(request);
	}

	public static ClusterEncounterRequest ReadRequest(string payload)
	{
		if (payload == null || payload.Length > 16 * 1024)
			throw new InvalidOperationException("Invalid encounter request payload");
		ClusterEncounterRequest request = JsonSerializer.Deserialize<ClusterEncounterRequest>(payload)
			?? throw new InvalidOperationException("Invalid encounter request payload");
		Validate(request);
		return request;
	}

	/// <summary>
	/// Validates what the World Authority decides. <see cref="ClusterEncounterRequest.Origin"/> is not required:
	/// the owning node is resolved during dispatch, so a decision is well-formed without it.
	/// </summary>
	public static void ValidateDecision(ClusterEncounterRequest request)
	{
		if (request == null || request.OperationId == Guid.Empty || request.TargetClient == 0
			|| request.SpawnGroup?.Length > MaxSpawnGroupLength
			|| request.Origin?.Length > 128
			|| (request.Kind != ClusterEncounterKinds.Global && request.Kind != ClusterEncounterKinds.Planetary
				&& request.Kind != ClusterEncounterKinds.Cargo && request.Kind != ClusterEncounterKinds.Meteor)
			// Global encounters and cargo ships name their spawn group; planetary installations and meteor
			// waves are placed from the target's position alone.
			|| ((request.Kind == ClusterEncounterKinds.Global || request.Kind == ClusterEncounterKinds.Cargo)
				&& string.IsNullOrEmpty(request.SpawnGroup)))
			throw new InvalidOperationException("Invalid encounter request payload");
	}

	/// <summary>Validates a dispatched request, which must name the node that is to execute it.</summary>
	public static void Validate(ClusterEncounterRequest request)
	{
		ValidateDecision(request);
		if (string.IsNullOrEmpty(request.Origin))
			throw new InvalidOperationException("Invalid encounter request payload");
	}
}
