using System;
using Sandbox.Game.Entities;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the cluster members of <c>MyAntennaSystem</c>
/// (MyAntennaSystemPatch). The Harmony side is
/// <c>Shared.Patches.MyAntennaSystemPatch</c>: postfixes on RegisterAntenna / UnregisterAntenna
/// that invoke these after the complete original method.
/// </summary>
public static class MyAntennaSystemSeams
{
	// ClusterPlugin seam adapters for MyAntennaSystemPatch postfixes
	public static Action<MyDataBroadcaster> ClusterRegisterPostfix;

	public static Action<MyDataBroadcaster> ClusterUnregisterPostfix;
}
