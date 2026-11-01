using System;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyFaction"/>. Harmony side: Shared/Patches/MyFactionPatch.cs (prefix on the
/// explicit IMyFaction.AcceptHumans setter).
/// </summary>
public static class MyFactionSeams
{
	public static Func<MyFaction, bool, bool> ClusterAcceptHumansPrefix;
}
