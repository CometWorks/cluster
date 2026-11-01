using Sandbox.Game.SessionComponents;

namespace ClusterPlugin;

public static class MySessionComponentContractSystemPatch
{
	public static void Install()
		=> MySessionComponentContractSystemSeams.ClusterUpdatePrefix = UpdateAfterSimulationPrefix;

	public static bool UpdateAfterSimulationPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
