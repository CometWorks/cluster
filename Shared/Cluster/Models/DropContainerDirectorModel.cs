using System;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// A drop-container spawn decided at the World Authority and executed on the node that owns the target player.
/// <paramref name="OperationId"/> is the exactly-once key: a dispatch retry replays the recorded outcome rather
/// than dropping a second container on the same player.
/// </summary>
public sealed record ClusterDropContainerRequest(
	Guid OperationId,
	string Origin,
	long IdentityId,
	ulong TargetClient,
	bool Competitive);

/// <summary>A dispatched drop's outcome. <paramref name="Spawned"/> is vanilla's own success flag.</summary>
public sealed record ClusterDropContainerResult(bool Spawned, string Error);

public static class DropContainerDirectorCodec
{
	public static string WriteRequest(ClusterDropContainerRequest request)
	{
		Validate(request);
		return JsonSerializer.Serialize(request);
	}

	public static ClusterDropContainerRequest ReadRequest(string payload)
	{
		if (payload == null || payload.Length > 16 * 1024)
			throw new InvalidOperationException("Invalid drop container request payload");
		ClusterDropContainerRequest request = JsonSerializer.Deserialize<ClusterDropContainerRequest>(payload)
			?? throw new InvalidOperationException("Invalid drop container request payload");
		Validate(request);
		return request;
	}

	/// <summary>
	/// Validates what the World Authority decides. <see cref="ClusterDropContainerRequest.Origin"/> is not
	/// required: the owning node is resolved during dispatch, so a decision is well-formed without it.
	/// </summary>
	public static void ValidateDecision(ClusterDropContainerRequest request)
	{
		if (request == null || request.OperationId == Guid.Empty || request.IdentityId == 0
			|| request.TargetClient == 0 || request.Origin?.Length > 128)
			throw new InvalidOperationException("Invalid drop container request payload");
	}

	/// <summary>Validates a dispatched request, which must name the node that is to execute it.</summary>
	public static void Validate(ClusterDropContainerRequest request)
	{
		ValidateDecision(request);
		if (string.IsNullOrEmpty(request.Origin))
			throw new InvalidOperationException("Invalid drop container request payload");
	}
}
