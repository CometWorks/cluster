using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of the internal <c>SpaceEngineers.Game.SessionComponents.MyPlanetaryEncountersGenerator</c>.
/// The plugin publicizes the assembly, so the holder is a plain static and <c>MyPlanetaryEncountersGeneratorPatch.Install</c> assigns it
/// directly. The per-player gate is installed by <c>Shared.Patches.MyPlanetaryEncountersGeneratorPatch</c>
/// (transpiler).
/// </summary>
public static class MyPlanetaryEncountersGeneratorSeams
{
	// ClusterPlugin seam adapter for MyPlanetaryEncountersGeneratorPatch.UpdateBeforeSimulationPrefix
	public static Func<bool> ClusterUpdatePrefix;

	/// <summary>The gate the transpiler calls: vanilla (true) unless the seam says otherwise.</summary>
	public static bool UpdateGate()
	{
		var prefix = ClusterUpdatePrefix;
		return prefix == null || prefix();
	}
}
