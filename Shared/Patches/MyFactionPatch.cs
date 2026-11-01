using System.Reflection;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.World;

namespace Shared.Patches;

/// <summary>
/// Routes the mod API setter
/// <c>IMyFaction.AcceptHumans</c> before it mutates regular-node state. The target is the explicit interface
/// implementation, which only exists under its metadata name.
/// </summary>
[HarmonyPatch]
public static class MyFactionPatch
{
	private const string SetterName = "VRage.Game.ModAPI.IMyFaction.set_AcceptHumans";

	private static MethodBase TargetMethod()
		=> EconomyIl.Required(AccessTools.Method(typeof(MyFaction), SetterName), "MyFaction." + SetterName);

	// ClusterPlugin patch point: MyFactionCollectionPatch.AcceptHumansPrefix
	[HarmonyPrefix]
	private static bool Prefix(MyFaction __instance, bool value)
	{
		var seam = MyFactionSeams.ClusterAcceptHumansPrefix;
		return seam == null || seam(__instance, value);
	}
}
