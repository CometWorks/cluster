using System;
using Sandbox.Game.Entities.Blocks;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the cluster delegates of <c>MyIntergridCommunicationContext</c>
/// (MyIntergridCommunicationContextPatch: SendRemoteUnicast and
/// SendRemoteBroadcast). Vanilla IGC stops at the node boundary: GetContextForPB resolves only
/// blocks loaded on the calling node and a broadcast reaches only local listeners. The Harmony side
/// is <c>Shared.Patches.MyIntergridCommunicationContextPatch</c> (unicast) and
/// <c>Shared.Patches.MyIGCSystemSessionComponentPatch</c> (broadcast); both are inert while these
/// delegates are null.
/// </summary>
public static class MyIntergridCommunicationContextSeams
{
	// ClusterPlugin seam adapters for MyIntergridCommunicationContextPatch
	public static Func<MyProgrammableBlock, long, string, object, bool> ClusterSendRemoteUnicast;

	public static Action<MyProgrammableBlock, string, object, int> ClusterSendRemoteBroadcast;
}
