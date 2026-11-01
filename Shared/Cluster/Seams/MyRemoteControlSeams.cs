using System;
using Sandbox.Game.Entities;
using VRage.Game.Entity.UseObject;

namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Game.Entities.MyRemoteControl.
/// Consulted from RequestUseMessage by Shared.Patches.MyRemoteControlPatch.
/// </summary>
public static class MyRemoteControlSeams
{
	// ClusterPlugin seam adapter for RemoteControlGatePatch.
	// Consulted before a server-side use request is honored; a non-null result denies the request through the
	// vanilla failure path (client-visible HUD notification) instead of granting control.
	public static Func<MyRemoteControl, UseActionEnum, long, UseActionResult?> ClusterUseGate;
}
