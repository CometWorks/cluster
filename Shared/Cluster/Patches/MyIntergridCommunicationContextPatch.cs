using Sandbox.Game.GameSystems.IntergridCommunication;

namespace ClusterPlugin;

/// <summary>
/// WA-4: routes IGC sends that leave this node. Vanilla resolves an addressee only among locally loaded blocks
/// and delivers a broadcast only to local listeners, both of which stop at a partition boundary in a cluster.
/// </summary>
public static class MyIntergridCommunicationContextPatch
{
	public static void Install()
	{
		MyIntergridCommunicationContextSeams.ClusterSendRemoteUnicast = IgcDeliveryService.SendRemoteUnicast;
		MyIntergridCommunicationContextSeams.ClusterSendRemoteBroadcast = IgcDeliveryService.SendRemoteBroadcast;
	}
}
