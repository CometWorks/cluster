namespace ClusterPlugin;

public static class MyPlanetaryEncountersGeneratorPatch
{
	// The seam holder is a plain static (the game type is publicized), so the delegate is assigned directly.
	public static void Install()
		=> MyPlanetaryEncountersGeneratorSeams.ClusterUpdatePrefix = UpdateBeforeSimulationPrefix;

	public static bool UpdateBeforeSimulationPrefix()
		=> ClusterNodeRole.RunsAuthoritativeSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
