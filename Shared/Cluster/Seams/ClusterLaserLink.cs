using System;
using VRageMath;

namespace Sandbox.Game.Entities.Cube;

// ClusterPlugin seam: synced peer state for a laser antenna hosted on another node, keyed by antenna
// entity id. Lives outside MyLaserAntenna on purpose - assigning a static on MyLaserAntenna would run
// its static constructor, which dereferences MySession.Static long before a session exists at cluster
// install time. Null (no delegate, or no record) means "no such remote peer".
public static class ClusterLaserLink
{
	public struct PeerInfo
	{
		public long TargetId;
		public byte State;
		public bool Powered;
		public bool RotationFinished;
		public Vector3D HeadPos;
	}

	public static Func<long, PeerInfo?> Peer;
}
