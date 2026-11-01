using Sandbox.Game.SessionComponents;

namespace ClusterPlugin;

public static class MySessionComponentContainerDropSystemPatch
{
	public static void Install()
	{
		MySessionComponentContainerDropSystemSeams.ClusterSpawnerPrefix = UpdateContainerSpawnerPrefix;
		DropContainerRemovalService.Install();   // #1391: the removal deadline travels with the grid
	}

	public static bool UpdateContainerSpawnerPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
