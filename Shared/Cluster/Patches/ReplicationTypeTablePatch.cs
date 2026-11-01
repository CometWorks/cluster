using System;
using System.Collections.Generic;
using VRage.Network;

namespace ClusterPlugin;

public static class ReplicationTypeTablePatch
{
	public static bool Enabled;

	public static bool Prefix(MyTypeTable __instance, Type type, ref MySynchronizedTypeInfo __result)
	{
		if (!Enabled) return true;
		__result = __instance.Register(type) ?? throw new KeyNotFoundException("No replication type for " + type.FullName);
		return false;
	}
}
