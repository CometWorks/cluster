using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using HarmonyLib;
using Sandbox.Game.Replication.StateGroups;
using VRage.Collections;
using VRage.Library.Collections;
using VRage.Network;
using VRage.Serialization;
using VRage.Sync;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// #1376: a client-view adopt makes an arriving partition's replicables known to its clients BEFORE the commit
/// thaws them, so their property groups (all properties dirty for a new client) are serialized while the grids
/// are still frozen. Freeze sets NeedsUpdate = NONE right after MyEntities.Add, which holds back every
/// BEFORE_NEXT_FRAME init until the thaw; a Sync of a reference type that vanilla sets only there is still null
/// (MyEmotionControllerComponent.m_selectedLCDsIds / m_selectedEmotionSubtype, see T-0316). Writing a null
/// into a non-nullable Sync throws MySerializeException(NullNotAllowed) out of Sync`2.Serialize, and that
/// stopped octahedron-1191c local-node-3 at 18:43:23Z (partition 925: 11 client adopts, no commit yet).
/// Vanilla never serializes in that window: a new replicable's state goes out only after the client acks it.
/// The prefix withholds a dirty property whose value cannot be written (a null in a non-nullable Sync, or a
/// derived instance in a non-dynamic one): it is left out of this packet and stays dirty, so it goes out once
/// the thaw's update sets it. The finalizer is the net for any other MySerializeException: it rewinds the group
/// to an empty one (the length SendStateSync writes stays right) and drops this group's update for this client,
/// with the full stack in the log, instead of ending the server.
/// </summary>
[HarmonyPatch(typeof(MyPropertySyncStateGroup), nameof(MyPropertySyncStateGroup.Serialize))]
public static class MyPropertySyncStateGroupPatch
{
	public struct SerializeState
	{
		public bool Writing;
		public long Start;
		public ulong Withheld;
		public MyPropertySyncStateGroup.ServerData.DataPerClient Data;
	}

	// Per closed Sync<T, D>: reads Value as object (only for reference-typed, non-nullable Syncs, so no boxing).
	private static readonly ConcurrentDictionary<Type, Func<SyncBase, object>> s_getters = new();

	private static long s_withheld;
	private static long s_dropped;

	public static long WithheldCount => s_withheld;
	public static long DroppedCount => s_dropped;

	[HarmonyPrefix]
	private static void Prefix(MyPropertySyncStateGroup __instance, BitStream stream, MyClientInfo forClient, out SerializeState __state)
	{
		__state = default;
		if (stream == null || !stream.Writing) return;
		__state.Writing = true;
		__state.Start = stream.BitPosition;
		if (__instance.m_serverData == null
			|| !__instance.m_serverData.ServerClientData.TryGetValue(forClient.EndpointId, out MyPropertySyncStateGroup.ServerData.DataPerClient data)
			|| data == null)
			return;
		__state.Data = data;
		ulong dirty = data.DirtyProperties.Bits;
		if (dirty == 0) return;
		ListReader<SyncBase> properties = __instance.m_properties;
		ulong withheld = 0;
		for (int i = 0; i < properties.Count && i < SmallBitField.BitCount; i++)
		{
			if ((dirty & (1UL << i)) == 0) continue;
			SyncBase property = properties[i];
			if (property == null || property.ValueType.IsValueType || property.SerializeInfo.IsNullable) continue;
			object value = s_getters.GetOrAdd(property.GetType(), CreateGetter)(property);
			if (value != null && (property.SerializeInfo.IsDynamic || value.GetType() == property.ValueType)) continue;
			withheld |= 1UL << i;
			long count = ++s_withheld;
			if (count <= 20 || count % 1000 == 0)
				MyLog.Default?.WriteLine("property_sync_withheld client=" + forClient.EndpointId.Id.Value
					+ " owner=" + Describe(__instance) + " property=" + (property.DebugName ?? "#" + i)
					+ " type=" + property.ValueType.Name + " value=" + (value == null ? "null" : value.GetType().Name)
					+ " count=" + count + " (#1376)");
		}
		if (withheld == 0) return;
		data.DirtyProperties.Bits = dirty & ~withheld;
		__state.Withheld = withheld;
	}

	[HarmonyFinalizer]
	private static Exception Finalizer(Exception __exception, MyPropertySyncStateGroup __instance, BitStream stream, MyClientInfo forClient, byte packetId, SerializeState __state)
	{
		// Serialize cleared the dirty bits it sent; the withheld ones were not sent and stay dirty.
		if (__state.Withheld != 0) __state.Data.DirtyProperties.Bits |= __state.Withheld;
		if (__exception is not MySerializeException || !__state.Writing) return __exception;
		stream.SetBitPositionWrite(__state.Start);
		stream.WriteUInt64(0, __instance.m_properties.Count);
		if (__state.Data != null)
		{
			__state.Data.SentProperties[packetId].Bits = 0;
			__state.Data.DirtyProperties.Bits = 0;
		}
		long count = ++s_dropped;
		if (count <= 5 || count % 100 == 0)
		{
			MyLog.Default?.WriteLine("property_sync_dropped client=" + forClient.EndpointId.Id.Value + " owner=" + Describe(__instance)
				+ " error=" + ((MySerializeException)__exception).Error + " count=" + count
				+ " (#1376: the group's update for this client is dropped instead of stopping the server) " + __exception);
			MyLog.Default?.Flush();
		}
		return null;
	}

	private static Func<SyncBase, object> CreateGetter(Type syncType)
	{
		ParameterExpression sync = Expression.Parameter(typeof(SyncBase), "sync");
		return Expression.Lambda<Func<SyncBase, object>>(
			Expression.Convert(Expression.Property(Expression.Convert(sync, syncType), "Value"), typeof(object)), sync).Compile();
	}

	private static string Describe(MyPropertySyncStateGroup group)
	{
		IMyReplicable owner = group.Owner;
		if (owner == null) return "null";
		return owner.GetType().Name + (owner is IMyEntityReplicable entity ? "/" + entity.EntityId : "");
	}
}
