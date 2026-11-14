using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Sandbox.Game.Replication.StateGroups;
using VRage;
using VRage.Game.Entity;
using VRage.Library.Collections;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;

namespace ClusterPlugin;

/// <summary>
/// SE-1554: one inventory delta as bytes, so a delta the source sent but the client never acknowledged can travel
/// with the client's view and be sent again by the target under its own message id. The fields are written in the
/// order vanilla's MyEntityInventoryStateGroup.WriteInventory puts them on the wire, items included.
/// </summary>
internal static class InventoryDeltaWire
{
	private const int MaxEntries = 100000;

	public static byte[] Write(MyEntityInventoryStateGroup.InventoryDeltaInformation delta)
	{
		using BitStream stream = new BitStream();
		stream.ResetWrite();
		stream.WriteUInt32(delta.MessageId);
		stream.WriteBool(delta.HasChanges);
		stream.WriteBool(delta.ChangedItems != null);
		if (delta.ChangedItems != null)
		{
			stream.WriteInt32(delta.ChangedItems.Count);
			foreach (KeyValuePair<uint, KeyValuePair<MyFixedPoint, float>> changed in delta.ChangedItems)
			{
				stream.WriteUInt32(changed.Key);
				stream.WriteInt64(changed.Value.Key.RawValue);
				stream.WriteFloat(changed.Value.Value);
			}
		}
		stream.WriteBool(delta.RemovedItems != null);
		if (delta.RemovedItems != null)
		{
			stream.WriteInt32(delta.RemovedItems.Count);
			foreach (uint removed in delta.RemovedItems) stream.WriteUInt32(removed);
		}
		stream.WriteBool(delta.NewItems != null);
		if (delta.NewItems != null)
		{
			stream.WriteInt32(delta.NewItems.Count);
			foreach (KeyValuePair<int, MyPhysicalInventoryItem> added in delta.NewItems)
			{
				stream.WriteInt32(added.Key);
				MyPhysicalInventoryItem item = added.Value;
				MySerializer.Write(stream, ref item, MyObjectBuilderSerializerKeen.Dynamic);
			}
		}
		stream.WriteBool(delta.SwappedItems != null);
		if (delta.SwappedItems != null)
		{
			stream.WriteInt32(delta.SwappedItems.Count);
			foreach (KeyValuePair<uint, int> swapped in delta.SwappedItems)
			{
				stream.WriteUInt32(swapped.Key);
				stream.WriteInt32(swapped.Value);
			}
		}
		byte[] body = new byte[stream.BytePosition];
		Marshal.Copy(stream.DataPointer, body, 0, body.Length);
		return body;
	}

	public static MyEntityInventoryStateGroup.InventoryDeltaInformation Read(byte[] body)
	{
		using BitStream stream = new BitStream();
		stream.ResetRead(body, 0, (long)body.Length * 8);
		MyEntityInventoryStateGroup.InventoryDeltaInformation delta = new MyEntityInventoryStateGroup.InventoryDeltaInformation
		{
			MessageId = stream.ReadUInt32(),
			HasChanges = stream.ReadBool()
		};
		if (stream.ReadBool())
		{
			delta.ChangedItems = new Dictionary<uint, KeyValuePair<MyFixedPoint, float>>();
			for (int i = Count(stream); i > 0; i--)
			{
				uint id = stream.ReadUInt32();
				MyFixedPoint amount = default;
				amount.RawValue = stream.ReadInt64();
				delta.ChangedItems[id] = new KeyValuePair<MyFixedPoint, float>(amount, stream.ReadFloat());
			}
		}
		if (stream.ReadBool())
		{
			delta.RemovedItems = new List<uint>();
			for (int i = Count(stream); i > 0; i--) delta.RemovedItems.Add(stream.ReadUInt32());
		}
		if (stream.ReadBool())
		{
			delta.NewItems = new SortedDictionary<int, MyPhysicalInventoryItem>();
			for (int i = Count(stream); i > 0; i--)
			{
				int position = stream.ReadInt32();
				MySerializer.CreateAndRead(stream, out MyPhysicalInventoryItem item, MyObjectBuilderSerializerKeen.Dynamic);
				delta.NewItems[position] = item;
			}
		}
		if (stream.ReadBool())
		{
			delta.SwappedItems = new Dictionary<uint, int>();
			for (int i = Count(stream); i > 0; i--)
			{
				uint id = stream.ReadUInt32();
				delta.SwappedItems[id] = stream.ReadInt32();
			}
		}
		return delta;
	}

	private static int Count(BitStream stream)
	{
		int count = stream.ReadInt32();
		if ((uint)count > MaxEntries) throw new InvalidDataException("Invalid inventory delta entry count " + count);
		return count;
	}
}
