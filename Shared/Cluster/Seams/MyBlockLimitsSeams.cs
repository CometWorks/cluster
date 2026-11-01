using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the cluster static helper of <c>MyBlockLimits</c>. The
/// delegates of the cross-node grid ownership transfer stay on <see cref="ClusterGridTransfer"/>
/// (its comment explains why: MyBlockLimits' static constructor dereferences MySession.Static).
/// The Harmony side is <c>Shared.Patches.MyBlockLimitsPatch</c>.
/// </summary>
public static class MyBlockLimitsSeams
{
	// ClusterPlugin seam: raises the vanilla confirm dialog to a locally attached client on behalf of the node
	// that holds the grid and the transfer transaction.
	public static void ClusterRaiseTransferOffer(long gridEntityId, string gridName, long oldOwner, long newOwner,
		int blocksCount, int pcu, ulong targetSteamId)
	{
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyBlockLimits.ReceiveTransferRequestMessage, new MyBlockLimits.TransferMessageData
		{
			EntityId = gridEntityId,
			GridName = gridName,
			BlocksBuilt = blocksCount,
			PCUBuilt = pcu
		}, oldOwner, newOwner, blocksCount, pcu, new EndpointId(targetSteamId));
	}
}
