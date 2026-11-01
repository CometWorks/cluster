using Sandbox.Game.World;

namespace ClusterPlugin;

public static class MyPlayerPatch
{
	public static void Install() => MyPlayerSeams.ClusterIdentityChanging = IdentityPrefix;

	public static void IdentityPrefix(MyPlayer __instance, MyIdentity value)
	{
		if (__instance?.Identity == null || value == null || __instance.Identity.IdentityId == value.IdentityId) return;
		MyPlayerCollectionPatch.Rebind(__instance, __instance.Identity.IdentityId, value);
	}
}
