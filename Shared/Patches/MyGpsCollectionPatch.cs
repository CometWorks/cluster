using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// The thirteen typed prefixes on GPS mutation requests plus
/// the accepted postfix on each, mapped onto the boxed seam members with one operation string per request. The
/// postfix (a WA-side "publish if changed") runs on every return of the target; only the completing returns
/// change anything, so the extra calls publish nothing new.
/// </summary>
public static class MyGpsCollectionPatch
{
	private static bool Request(string operation, object first, object second, object third, object fourth)
	{
		var seam = MyGpsCollectionSeams.ClusterGpsRequestPrefix;
		return seam == null || seam(operation, first, second, third, fourth);
	}

	private static void Accepted() => MyGpsCollectionSeams.ClusterGpsAcceptedPostfix?.Invoke();

	// ClusterPlugin patch point: GpsAuthorityPatch.AddPrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendAddGpsRequest))]
	private static class SendAddGpsRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, ref MyGps gps, long entityId, bool playSoundOnCreation)
			=> Request("Add", identityId, gps, entityId, playSoundOnCreation);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "AddGpsOnServer")]
	private static class AddGpsOnServer
	{
		[HarmonyPrefix] private static bool Prefix(MyGpsCollection.AddMsg msg) => Request("AddServer", msg, null, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.DeletePrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendDeleteGpsRequest))]
	private static class SendDeleteGpsRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, int gpsHash) => Request("Delete", identityId, gpsHash, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "DeleteGpsOnServer")]
	private static class DeleteGpsOnServer
	{
		[HarmonyPrefix] private static bool Prefix(int gpsHash) => Request("DeleteServer", gpsHash, null, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.DeleteContainerPrefix (SE-1146)
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendDeleteContainerGpsRequest))]
	private static class SendDeleteContainerGpsRequest
	{
		[HarmonyPrefix] private static bool Prefix(long entityId, long identityId) => Request("DeleteContainer", entityId, identityId, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.ModifyPrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendModifyGpsRequest))]
	private static class SendModifyGpsRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, MyGps gps) => Request("Modify", identityId, gps, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "ModifyGpsOnServer")]
	private static class ModifyGpsOnServer
	{
		[HarmonyPrefix] private static bool Prefix(MyGpsCollection.ModifyMsg msg) => Request("ModifyServer", msg, null, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.ShowOnHudPrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendChangeShowOnHudRequest))]
	private static class SendChangeShowOnHudRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, int gpsHash, bool show) => Request("ShowOnHud", identityId, gpsHash, show, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "ChangeShowOnHudOnServer")]
	private static class ChangeShowOnHudOnServer
	{
		[HarmonyPrefix] private static bool Prefix(int gpsHash, bool show) => Request("ShowOnHudServer", gpsHash, show, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.AlwaysVisiblePrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendChangeAlwaysVisibleRequest))]
	private static class SendChangeAlwaysVisibleRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, int gpsHash, bool alwaysVisible) => Request("AlwaysVisible", identityId, gpsHash, alwaysVisible, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "ChangeAlwaysVisibleOnServer")]
	private static class ChangeAlwaysVisibleOnServer
	{
		[HarmonyPrefix] private static bool Prefix(int gpsHash, bool alwaysVisible) => Request("AlwaysVisibleServer", gpsHash, alwaysVisible, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	// ClusterPlugin patch point: GpsAuthorityPatch.ColorPrefix
	[HarmonyPatch(typeof(MyGpsCollection), nameof(MyGpsCollection.SendChangeColorRequest))]
	private static class SendChangeColorRequest
	{
		[HarmonyPrefix] private static bool Prefix(long identityId, int gpsHash, Color color) => Request("Color", identityId, gpsHash, color, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}

	[HarmonyPatch(typeof(MyGpsCollection), "ChangeColorOnServer")]
	private static class ChangeColorOnServer
	{
		[HarmonyPrefix] private static bool Prefix(int gpsHash, Color color) => Request("ColorServer", gpsHash, color, null, null);
		[HarmonyPostfix] private static void Postfix() => Accepted();
	}
}
