using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClusterPlugin;

public sealed record ClusterPlayerPosition(long IdentityId, ulong ClientId, long EntityId, double X, double Y, double Z)
{
	public bool IsWithinDistance(double x, double y, double z, double distanceSquared)
		=> (X - x) * (X - x) + (Y - y) * (Y - y) + (Z - z) * (Z - z) < distanceSquared;
}
public sealed record ClusterPositionSnapshot(ClusterPlayerPosition[] Players);

public static class PositionRegistryCodec
{
	public static string Write(IEnumerable<ClusterPlayerPosition> players)
		=> JsonSerializer.Serialize(new ClusterPositionSnapshot(players.OrderBy(x => x.IdentityId).ToArray()));

	public static ClusterPositionSnapshot Read(string payload)
	{
		ClusterPositionSnapshot snapshot = JsonSerializer.Deserialize<ClusterPositionSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid position snapshot");
		if (snapshot.Players == null || snapshot.Players.Length > 4096 || snapshot.Players.Any(x => x == null || x.IdentityId <= 0
			|| !double.IsFinite(x.X) || !double.IsFinite(x.Y) || !double.IsFinite(x.Z)))
			throw new InvalidOperationException("Invalid player positions");
		return snapshot;
	}
}
