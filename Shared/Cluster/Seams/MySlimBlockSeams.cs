namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Game.Entities.Cube.MySlimBlock.
/// Read by the AddAuthorship prefix in Shared.Patches.MySlimBlockPatch.
/// </summary>
public static class MySlimBlockSeams
{
	// ClusterPlugin: keep authorship on blocks whose builder's identity is not present on this node.
	// Identities live on the node their player connected to, so a partition import routinely carries
	// blocks built by an identity this node has never seen - vanilla AddAuthorship would zero their
	// BuiltBy, which both loses the authorship for good and makes the handover round-trip validation
	// abort ("14 != 13 child elements": the re-export omits the now-default BuiltBy element).
	public static bool ClusterPreserveUnknownAuthorship;
}
