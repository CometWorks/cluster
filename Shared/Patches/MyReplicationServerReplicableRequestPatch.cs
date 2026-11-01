using System;
using HarmonyLib;
using VRage;
using VRage.Library.Collections;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// Invokes <see cref="ClusterReplicationDemand.AbsentReplicable"/> inside
/// <c>MyReplicationServer.ReplicableRequest</c> when an add-request's GetReplicableByEntityId
/// returned null - vanilla's silent no-op, but in a cluster the demand signal for pulling the
/// entity's partition from its owning node. The ledger's migration option "a prefix re-doing the
/// GetReplicableByEntityId lookup" is what this is: the prefix peeks the entity id, the add flag and
/// the medical room id (set only by the respawn screen's spawn point preview) from the packet's bit
/// stream, rewinds it, and repeats the lookup. Batch D owns only this
/// call-out; the rest of the MyReplicationServer diff is batch F.
/// </summary>
[HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.ReplicableRequest))]
public static class MyReplicationServerReplicableRequestPatch
{
	[HarmonyPrefix]
	private static void ReplicableRequestPrefix(MyReplicationServer __instance, MyPacket packet)
	{
		Action<Endpoint, long, long> hook = ClusterReplicationDemand.AbsentReplicable;
		if (hook == null)
		{
			return;
		}
		BitStream stream = packet.BitStream;
		long position = stream.BitPosition;
		long entityId = stream.ReadInt64();
		bool add = stream.ReadBool();
		bool hasMedicalRoom = stream.ReadBool();
		stream.ReadBool();
		if (add) stream.ReadByte();
		long medicalRoom = hasMedicalRoom ? stream.ReadInt64() : 0;
		stream.SetBitPositionRead(position);
		// ClusterPlugin patch point: ClusterReplicationDemand.AbsentReplicable
		// An add-request for an entity this node does not hold is vanilla's silent no-op; in a cluster it is
		// the demand signal for pulling the entity's partition from its owning node.
		if (add && __instance.m_callback.GetReplicableByEntityId(entityId) == null)
		{
			hook(packet.Sender, entityId, medicalRoom);
		}
	}
}
