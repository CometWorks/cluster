using System;
using Sandbox.Game.Entities;
using VRage.Game.Entity.UseObject;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Denies remote control use requests on grids that are frozen for a partition transfer, through the vanilla
/// failure path (the requester gets the standard access-denied HUD response) instead of the silent RPC drop
/// every other event gets on a frozen root. Cameras need no equivalent gate: MyCameraBlock.RequestSetView is
/// entirely client-side (no server RPC), so a camera view of a departing grid simply ends when the replicable
/// leaves the client, and taking a camera view grants the cluster nothing to arbitrate.
/// </summary>
public static class RemoteControlGatePatch
{
	public static Func<long, bool> IsFrozen { get; set; }

	public static void Install()
	{
		MyRemoteControlSeams.ClusterUseGate = Gate;
	}

	private static UseActionResult? Gate(MyRemoteControl block, UseActionEnum useAction, long usedById)
	{
		long root = block?.CubeGrid?.EntityId ?? 0;
		if (root == 0 || IsFrozen?.Invoke(root) != true) return null;
		MyLog.Default?.WriteLine(
			$"[ClusterPlugin] remote control denied: block={block.EntityId} root={root} usedBy={usedById} action={useAction} reason=frozen partition transfer in progress");
		return UseActionResult.AccessDenied;
	}
}
