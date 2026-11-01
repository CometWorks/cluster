using Sandbox.Game.World;

namespace ClusterPlugin;

public static class MyPirateAntennasPatch
{
	public static void Install()
		=> MyPirateAntennasSeams.ClusterDroneSpawningPrefix = UpdateDroneSpawningPrefix;

	// Pirate antennas are blocks, each loaded on exactly one node, and their drones spawn near the antenna, so
	// drone spawning is node-local: every regular node serves its own antennas and nothing double-spawns. Proxy
	// antennas cannot leak in - they are MyProxyAntenna entities, not MyRadioAntenna blocks, and never register
	// through UpdatePirateAntenna. The WA holds no blocks, so the vanilla body would only no-op there. The
	// vanilla MaxDrones world cap becomes a per-node cap under this split; the global-limit ledger is WA-2 work.
	public static bool UpdateDroneSpawningPrefix()
		=> ClusterNodeRole.RunsNodeLocalSystems(ClusterRuntime.IsClustered, ClusterRuntime.IsWorldAuthority);
}
