using System;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// Cluster seam of <see cref="MyVoxelMap"/>. The Harmony side lives in Shared/Patches/MyVoxelMapPatch.cs;
/// VoxelJournalService installs it for clustered startup only.
/// </summary>
public static class MyVoxelMapSeams
{
	/// <summary>
	/// Runs before <c>MyVoxelMap.Init(MyObjectBuilder_EntityBase)</c>, the overload a world load uses for a
	/// saved asteroid. Returning false means the seam initialised the voxel map itself (from the newest
	/// verified catalogued native base) and the vanilla body, which loads the world's own file, must be skipped.
	/// </summary>
	public static Func<MyVoxelMap, MyObjectBuilder_EntityBase, bool> ClusterStorageInit;
}
