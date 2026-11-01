using System;

namespace ClusterPlugin;

internal static class ClusterNodeRole
{
	public static bool RunsAuthoritativeSystems(bool isClustered, bool isWorldAuthority)
		=> !isClustered || isWorldAuthority;

	/// <summary>
	/// True where systems that act on this node's own players, entities, and blocks should run: standalone and
	/// clustered regular nodes. The World Authority holds no players or partition entities, so these systems
	/// would only no-op there - or worse, act on proxy entities it hosts for replication.
	/// </summary>
	public static bool RunsNodeLocalSystems(bool isClustered, bool isWorldAuthority)
		=> !isClustered || !isWorldAuthority;

	public static bool IsWorldAuthority(string role)
	{
		if (string.IsNullOrWhiteSpace(role) || role.Equals("Regular", StringComparison.OrdinalIgnoreCase)) return false;
		if (role.Equals("WA", StringComparison.OrdinalIgnoreCase)) return true;
		throw new ArgumentException("CLUSTER_NODE_ROLE must be Regular or WA");
	}
}
