using System;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <see cref="MyPlanet"/>. The Harmony side lives in Shared/Patches/MyPlanetPatch.cs;
/// VoxelJournalService installs both delegates for clustered startup only.
/// </summary>
public static class MyPlanetSeams
{
	/// <summary>
	/// Runs before <c>MyPlanet.Init(MyObjectBuilder_EntityBase)</c>. Returning false means the seam
	/// initialised the planet itself (from the newest verified catalogued native base) and the
	/// vanilla one-line body must be skipped.
	/// </summary>
	public static Func<MyPlanet, MyObjectBuilder_EntityBase, bool> ClusterStorageInit;

	/// <summary>
	/// Runs after <c>MyPlanet.storage_RangeChangedPlanet</c>: captures the exact committed content and
	/// material bytes of every planet-storage range mutation for the voxel journal.
	/// </summary>
	public static Action<MyPlanet, Vector3I, Vector3I, MyStorageDataTypeFlags> ClusterRangeChanged;
}
