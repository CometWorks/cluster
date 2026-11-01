using SpaceEngineers.Game.SessionComponents;

namespace ClusterPlugin;

public static class MyGlobalEncountersGeneratorPatch
{
	public static void Install()
		=> MyGlobalEncountersGeneratorSeams.ClusterUpdatePrefix = UpdateBeforeSimulationPrefix;

	public static bool UpdateBeforeSimulationPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
