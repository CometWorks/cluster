using System;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="Sandbox.Game.Multiplayer.MyGpsCollection"/>. The seam adapter passes the
/// operation name and boxed arguments to GpsAuthorityPatch.SeamPrefix; the thirteen typed Harmony
/// prefixes/postfixes in Shared/Patches/MyGpsCollectionPatch.cs call these two members.
/// </summary>
public static class MyGpsCollectionSeams
{
	// ClusterPlugin seam adapter: GpsAuthorityPatch.SeamPrefix/AcceptedPostfix
	public static Func<string, object, object, object, object, bool> ClusterGpsRequestPrefix;

	public static Action ClusterGpsAcceptedPostfix;
}
