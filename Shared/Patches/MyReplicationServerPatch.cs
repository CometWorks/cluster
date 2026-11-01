using ClusterPlugin;
using HarmonyLib;
using VRage;
using VRage.Network;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Silently drop client RPCs aimed at a
/// transaction-frozen partition root instead of invoking them or reporting the client as a cheater.
/// The decision, logging and the remote-control allowlist stay in
/// <see cref="FrozenRootIncomingRpcPatch"/>; this is the Harmony prefix on the exact protected
/// overload. Inert until ClusterRuntime installs <see cref="FrozenRootIncomingRpcPatch.IsFrozen"/>.
/// <para>
/// The check could sit after the client-state and server-flag checks; a prefix runs before
/// them, so on a frozen root a packet from an unknown client or without the server flag is dropped
/// here instead of being returned/kicked there. Either way it never reaches validation or invoke.
/// </para>
/// </summary>
[HarmonyPatch(typeof(MyReplicationServer), "OnEvent",
	typeof(MyPacketDataBitStreamBase), typeof(CallSite), typeof(object), typeof(IMyNetObject), typeof(Vector3D?), typeof(EndpointId))]
public static class MyReplicationServerPatch
{
	[HarmonyPrefix]
	private static bool OnEventPrefix(MyPacketDataBitStreamBase data, CallSite site, IMyNetObject sendAs, EndpointId source)
	{
		if (FrozenRootIncomingRpcPatch.IsFrozen == null)
			return true;
		return FrozenRootIncomingRpcPatch.Prefix(data, site, sendAs, source);
	}
}
