using Sandbox.Game.SessionComponents;

namespace ClusterPlugin;

public static class MySessionComponentEconomyPatch
{
	public static void Install()
	{
		MySessionComponentEconomySeams.ClusterBeforeStartPrefix = BeforeStartPrefix;
		MySessionComponentEconomySeams.ClusterUpdatePrefix = UpdateAfterSimulationPrefix;
	}

	public static bool BeforeStartPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);

	public static bool UpdateAfterSimulationPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
