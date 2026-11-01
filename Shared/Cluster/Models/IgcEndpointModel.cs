using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

/// <summary>
/// One programmable-block IGC endpoint and the node currently hosting it. Cross-node unicast needs this because
/// <c>MyIGCSystemSessionComponent.GetContextForPB</c> only resolves blocks loaded on the local node, so an
/// addressee living on another node is indistinguishable from one that does not exist.
/// </summary>
public sealed record ClusterIgcEndpoint(string Origin, long EndpointId, long GridEntityId, long OwnerIdentityId);

public sealed record ClusterIgcSnapshot(ClusterIgcEndpoint[] Endpoints);

public static class IgcEndpointCodec
{
	public const int MaxEndpoints = 65536;

	public static string Write(IEnumerable<ClusterIgcEndpoint> endpoints)
		=> JsonSerializer.Serialize(new ClusterIgcSnapshot(endpoints.OrderBy(x => x.EndpointId).ToArray()));

	public static ClusterIgcSnapshot Read(string payload)
	{
		if (payload == null || payload.Length > 8 * 1024 * 1024)
			throw new InvalidOperationException("Invalid IGC endpoint payload");
		ClusterIgcSnapshot snapshot = JsonSerializer.Deserialize<ClusterIgcSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid IGC endpoint payload");
		if (snapshot.Endpoints == null || snapshot.Endpoints.Length > MaxEndpoints
			|| snapshot.Endpoints.Any(x => x == null || string.IsNullOrEmpty(x.Origin) || x.Origin.Length > 128
				|| x.EndpointId == 0 || x.GridEntityId == 0)
			|| snapshot.Endpoints.Select(x => x.EndpointId).Distinct().Count() != snapshot.Endpoints.Length)
			throw new InvalidOperationException("Invalid IGC endpoint payload");
		return snapshot;
	}
}
