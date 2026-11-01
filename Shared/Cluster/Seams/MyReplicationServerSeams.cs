using VRage.Network;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the MyReplicationServer methods <c>RemotePullService</c> and <c>RespawnRegistryService</c> use.
/// The absent-replicable demand call-out is a Harmony prefix in <c>Shared.Patches.MyReplicationServerPatch</c> feeding
/// <see cref="ClusterReplicationDemand.AbsentReplicable"/>. Everything else in the
/// MyReplicationServer patching lives with the replication patches.
/// </summary>
public static class MyReplicationServerSeams
{
	/// <summary>
	/// ClusterPlugin seam: a pulled partition's grid must reach the client whose demand
	/// triggered the pull - its original request was consumed while the entity was still
	/// absent, and the vanilla medical screen never re-requests.
	/// </summary>
	public static void ForceReplicableForClient(this MyReplicationServer self, long entityId, Endpoint clientEndpoint)
	{
		self.ForceReplicable(self.m_callback.GetReplicableByEntityId(entityId), clientEndpoint);
	}

	/// <summary>
	/// ClusterPlugin seam (SE1-0122): replays the respawn screen's request for a spawn point's grid on a node that did
	/// not receive it, because the grid or the client got here after the request. Same rights check and permanent
	/// replicable entry as vanilla's <c>ReplicableRequest</c>, so the preview stays streamed however far away the
	/// client is, and the grid is sent now instead of at the next layer update. False when the grid or the client
	/// is not on this node or the rights check refuses.
	/// </summary>
	public static bool RequestSpawnPreviewForClient(this MyReplicationServer self, long gridEntityId, long medicalRoomId,
		Endpoint clientEndpoint)
	{
		IMyReplicable replicable = self.m_callback.GetReplicableByEntityId(gridEntityId);
		if (replicable == null || !self.m_clientStates.TryGetValue(clientEndpoint, out MyClient client)
			|| !replicable.HasRightsForReplication(clientEndpoint, 0, medicalRoomId)) return false;
		client.PermanentReplicables[replicable] = 0;
		self.ForceReplicable(replicable, clientEndpoint);
		return true;
	}
}
