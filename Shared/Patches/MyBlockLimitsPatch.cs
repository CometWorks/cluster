using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// Cross-node grid ownership transfer (the MyBlockLimits diff; its comments are the spec). The
/// delegates live on <see cref="ClusterGridTransfer"/>, installed by <c>GridTransferService</c>;
/// both prefixes are inert while they are null.
///
/// SendTransferRequestMessage: the offer seam belongs between the transfer transaction and the
/// directed client event. Nothing non-generic separates those two steps, so the
/// prefix re-runs the vanilla body (1.210.014, all members publicized) with the seam in place and
/// skips the original. TransferBlocksBuiltByID: the forward-accept seam sits after vanilla's two
/// validations and before the grid lookup; the prefix repeats those checks (returning to vanilla on
/// any failure so vanilla's ValidationFailed / warnings still run) and only skips the original when
/// the acceptance was forwarded.
/// </summary>
[HarmonyPatch(typeof(MyBlockLimits))]
public static class MyBlockLimitsPatch
{
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyBlockLimits.SendTransferRequestMessage))]
	private static bool SendTransferRequestMessagePrefix(MyBlockLimits.MyGridLimitData gridData, long newOwner)
	{
		if (ClusterGridTransfer.Offer == null)
		{
			return true;
		}
		// --- vanilla body ---
		ulong num = MySession.Static.Players.TryGetSteamId(newOwner);
		if (num == 0L)
		{
			return false;
		}
		ulong value = MyEventContext.Current.Sender.Value;
		long num2 = MySession.Static.Players.TryGetIdentityId(value);
		if (num2 == newOwner)
		{
			MyLog.Default.Warning($"User {value} is trying to transfer blocks to himself");
			return false;
		}
		if (!MyBlockLimits.IsTransferBlocksBuiltByIDPossible(gridData.EntityId, num2, newOwner, forceCheckNewIdentityLimits: false,
			out int blocksCount, out int pcu, out _))
		{
			MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyBlockLimits.ReceiveTransferNotPossibleMessage, newOwner, MyEventContext.Current.Sender);
			return false;
		}
		MyIdentity myIdentity = MySession.Static.Players.TryGetIdentity(num2);
		MyBlockLimits.MyGridLimitData myGridLimitData = myIdentity.BlockLimits.BlocksBuiltByGrid[gridData.EntityId];
		myIdentity.BlockLimits.AddTransferTransaction(gridData, newOwner);
		if (MyEventContext.Current.IsLocallyInvoked && newOwner == MySession.Static.LocalPlayerId)
		{
			MyBlockLimits.ReceiveTransferRequestMessage(new MyBlockLimits.TransferMessageData
			{
				EntityId = myGridLimitData.EntityId,
				GridName = myGridLimitData.GridName,
				BlocksBuilt = myGridLimitData.BlocksBuilt,
				PCUBuilt = myGridLimitData.PCUBuilt
			}, num2, newOwner, blocksCount, pcu);
			return false;
		}
		// ClusterPlugin seam: the new owner's client may be attached to another node - a directed client event
		// raised here would be dropped silently. The handoff forwards the offer to the node that really hosts
		// that client, which raises this same vanilla dialog event locally. The transfer transaction recorded
		// above stays on THIS node, where the acceptance is routed back and validated.
		if (ClusterGridTransfer.Offer(myGridLimitData.EntityId, myGridLimitData.GridName,
			num2, newOwner, blocksCount, pcu, num))
		{
			return false;
		}
		MyMultiplayer.RaiseStaticEvent((IMyEventOwner x) => MyBlockLimits.ReceiveTransferRequestMessage, new MyBlockLimits.TransferMessageData
		{
			EntityId = myGridLimitData.EntityId,
			GridName = myGridLimitData.GridName,
			BlocksBuilt = myGridLimitData.BlocksBuilt,
			PCUBuilt = myGridLimitData.PCUBuilt
		}, num2, newOwner, blocksCount, pcu, new EndpointId(num));
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyBlockLimits.TransferBlocksBuiltByID))]
	private static bool TransferBlocksBuiltByIDPrefix(long gridEntityId, long oldOwner, long newOwner)
	{
		if (ClusterGridTransfer.ForwardAccept == null)
		{
			return true;
		}
		// Vanilla's own two validations: on failure let the original run and report it its own way.
		ulong value = MyEventContext.Current.Sender.Value;
		if (!MyEventContext.Current.IsLocallyInvoked && MySession.Static.Players.TryGetSteamId(newOwner) != value)
		{
			return true;
		}
		if (oldOwner == newOwner)
		{
			return true;
		}
		// ClusterPlugin seam: the accepting client is attached to the node hosting IT, not necessarily the node
		// hosting the grid. When the grid is absent here, forward the acceptance to the grid's node, where the
		// transfer transaction lives and this same event body runs with its vanilla validation.
		if (MyBlockLimits.GetGridFromId(gridEntityId) != null)
		{
			return true;
		}
		return !ClusterGridTransfer.ForwardAccept(gridEntityId, oldOwner, newOwner);
	}
}
