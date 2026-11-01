using System;
using VRage;
using VRage.Library.Collections;
using VRage.Network;
using VRage.Replication;
using VRage.Utils;

namespace ClusterPlugin;

public static class FrozenRootIncomingRpcPatch
{
	public static Func<long, bool> IsFrozen { get; set; }

	public static bool Prefix(MyPacketDataBitStreamBase data, CallSite site, IMyNetObject sendAs, EndpointId source)
	{
		IMyReplicable current = sendAs as IMyReplicable ?? (sendAs as IMyStateGroup)?.Owner;
		if (current == null) return true;
		while (current.GetParent() != null) current = current.GetParent();
		long root = (current as IMyEntityReplicable)?.EntityId ?? 0;
		if (root == 0 || IsFrozen?.Invoke(root) != true) return true;
		// A remote control use request has a vanilla denial path; let it reach the game layer so
		// RemoteControlGatePatch refuses it with a clear client-visible failure instead of a silent drop.
		// Matched by name because this file is compiled into VRage, which cannot reference Sandbox.Game.
		if (site.MethodInfo?.Name == "RequestUseMessage" && site.MethodInfo.DeclaringType?.Name == "MyRemoteControl")
		{
			MyLog.Default?.WriteLine($"rpc_allow reason=frozen-control-denial root={root} source={source.Value} event={site}");
			return true;
		}
		data.Return();
		MyLog.Default?.WriteLine($"rpc_drop reason=frozen-root root={root} source={source.Value} event={site}");
		return false;
	}
}
