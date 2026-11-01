using System;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyPlayer"/>.
/// Invoked by Shared/Patches/MyPlayerPatch.cs from a prefix on the <c>MyPlayer.Identity</c> setter.
/// </summary>
public static class MyPlayerSeams
{
	// ClusterPlugin seam adapter for MyPlayerPatch.IdentityPrefix.
	public static Action<MyPlayer, MyIdentity> ClusterIdentityChanging;
}
