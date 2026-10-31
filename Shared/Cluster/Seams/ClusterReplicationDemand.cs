using System;

namespace VRage.Network;

// ClusterPlugin seam: a client asked the replication server for an entity this node does not hold.
// Vanilla silently ignores the request - correct standalone, but in a cluster the entity may live on
// another node, and this demand is the trigger for pulling its partition here (WA-4 remote terminal /
// WA-6 remote control across nodes, the respawn screen's spawn point preview). The request retains add/remove, medical room, interacted entity and pin flags for cancellation and rights-checked replay. Lives in its own static class so assigning the delegate never
// runs another type's static constructor at install time.
public static class ClusterReplicationDemand
{
	public static Action<Endpoint, long, bool, long, long, byte> Request;
}
