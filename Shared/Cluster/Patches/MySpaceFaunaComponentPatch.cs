using SpaceEngineers.Game.AI;

namespace ClusterPlugin;

public static class MySpaceFaunaComponentPatch
{
	public static void Install()
		=> MySpaceFaunaComponentSeams.ClusterUpdatePrefix = UpdateAfterSimulationPrefix;

	// Fauna spawns near this node's own online players and its bots live on this node, so it is a node-local
	// system: every regular node runs it for its disjoint player set and nothing can double-spawn. On the WA the
	// player list is empty, so the vanilla body would only no-op there. The vanilla per-world bot caps become
	// per-node caps under this split; the global-limit ledger is tracked WA-2 work.
	public static bool UpdateAfterSimulationPrefix()
		=> ClusterNodeRole.RunsNodeLocalSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
