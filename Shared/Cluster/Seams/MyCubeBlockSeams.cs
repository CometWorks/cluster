namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Game.Entities.MyCubeBlock.
/// Read by the InitOwnership transpiler in Shared.Patches.MyCubeBlockPatch.
/// </summary>
public static class MyCubeBlockSeams
{
	/// <summary>
	/// Cluster nodes keep a block's Owner even when the identity is not (yet) known locally; see the
	/// comment at the ownership restore in MyCubeBlockPatch. Off = vanilla (unknown owner is zeroed).
	/// </summary>
	public static bool ClusterPreserveUnknownOwner;
}
