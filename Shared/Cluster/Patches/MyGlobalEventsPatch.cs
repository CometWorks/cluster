using Sandbox.Game.World;

namespace ClusterPlugin;

public static class MyGlobalEventsPatch
{
	public static void Install()
		=> MyGlobalEventsSeams.ClusterUpdatePrefix = UpdateBeforeSimulationPrefix;

	public static bool UpdateBeforeSimulationPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
