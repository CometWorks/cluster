using VRage.Game;

namespace ClusterPlugin;

/// <summary>
/// Cluster seam of <see cref="Sandbox.Engine.Networking.MyLocalCache"/>. The Harmony transpiler that
/// calls it lives in WorldAuthorityPlugin/Patches/MyLocalCachePatch.cs: only the World Authority
/// ever restores a catalogued global checkpoint, so regular nodes do not carry the patch at all.
/// </summary>
public static class MyLocalCacheSeams
{
	/// <summary>
	/// Invoked right after <c>LoadCheckpoint</c> deserialized <c>Sandbox.sbc</c> and before vanilla
	/// applies the world configuration, platform patches and experimental-mode normalisation, so a
	/// replacement World Authority can swap in the newest catalogued global checkpoint and its size.
	/// </summary>
	public delegate void ClusterRestoreCheckpoint(ref MyObjectBuilder_Checkpoint checkpoint, ref ulong checkpointSizeInBytes);

	public static ClusterRestoreCheckpoint ClusterRestoreWorldAuthorityCheckpoint;
}
