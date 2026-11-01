using System;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using VRage.Network;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <see cref="MyVoxelBase"/>. The Harmony
/// side lives in Shared/Patches/MyVoxelBasePatch.cs.
/// </summary>
public static class MyVoxelBaseSeams
{
	/// <summary>
	/// Runs after <c>MyVoxelBase.RequestVoxelCutoutSphere</c>, i.e. after the vanilla reliable
	/// broadcast, so the drill/explosion cutout can be journalled for remote-node clients.
	/// </summary>
	public static Action<MyVoxelBase, Vector3D, float, bool, bool> ClusterVoxelCutoutSphere;

	/// <summary>
	/// Broadcasts the cutout to this node's clients without executing it on this server.
	/// <para>
	/// On a dedicated server the replication layer's local endpoint is the default value, so a broadcast raised
	/// with the default target is also invoked locally (<see cref="MyReplicationLayerBase.ShouldServerInvokeLocally"/>).
	/// A replica applying a relayed cutout must not take that local invocation: its content comes from the ordered
	/// journal range entries, while the local re-executed carve lands asynchronously, outside journal order. Any
	/// non-default target suppresses the local invocation and is otherwise ignored for a [Broadcast] event.
	/// </para>
	/// Written against the publicized assembly: <c>VoxelCutoutSphere_Implementation</c> is the private
	/// <c>[Event] [Reliable] [Broadcast]</c> method the vanilla request raises, so the call site the
	/// replication layer resolves is exactly the one the clients already know.
	/// </summary>
	public static void ClusterBroadcastVoxelCutoutSphere(this MyVoxelBase self, Vector3D center, float radius, bool createDebris, bool damage)
	{
		MyMultiplayer.RaiseEvent(self.RootVoxel, (MyVoxelBase x) => x.VoxelCutoutSphere_Implementation, center, radius, createDebris, damage, new EndpointId(ulong.MaxValue));
	}
}
