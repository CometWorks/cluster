using System;

namespace Sandbox.Game.World;

// ClusterPlugin seam holder for cross-node grid ownership transfer. Deliberately NOT static fields on
// MyBlockLimits: its static constructor dereferences MySession.Static, so installing a seam there at process
// start crashes the type initializer (the same trap MyLaserAntenna has).
public static class ClusterGridTransfer
{
	public static Func<long, string, long, long, int, int, ulong, bool> Offer;
	public static Func<long, long, long, bool> ForwardAccept;
}
