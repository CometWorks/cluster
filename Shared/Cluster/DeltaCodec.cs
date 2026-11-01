using System;
using System.Collections.Generic;
using System.IO;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>One entity of a delta capture (HandoverDelta.md §2): unchanged, or its new builder plus the changed paths.</summary>
internal sealed class DeltaEntry
{
	public long Id;
	public bool Changed;
	public List<string> Paths;
	public byte[] Bytes;
	public MyObjectBuilder_EntityBase Builder;
	public DeltaClassifier.Tier Tier;
}

/// <summary>
/// The delta capture's body, carried inside the MPS1 envelope with Kind = 3 (Zstd like every
/// non-periodic kind). Magic, the baseline (presync) id, then per entity in the source's order:
/// id, changed flag, and for changed entities the diff paths and the entity's own serialisation
/// (PartitionSaveFile.SerializeEntities of that one builder).
/// </summary>
internal static class DeltaCodec
{
	public const byte Kind = 3;
	private static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'S', (byte)'D' };

	public static byte[] Write(Guid presyncId, IReadOnlyList<DeltaEntry> entries)
	{
		using MemoryStream body = new MemoryStream();
		using (BinaryWriter writer = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(Magic);
			writer.Write(presyncId.ToByteArray());
			writer.Write(entries.Count);
			foreach (DeltaEntry entry in entries)
			{
				writer.Write(entry.Id);
				writer.Write(entry.Changed);
				if (!entry.Changed) continue;
				writer.Write(entry.Paths.Count);
				foreach (string path in entry.Paths) writer.Write(path);
				writer.Write(entry.Bytes.Length);
				writer.Write(entry.Bytes);
			}
		}
		return body.ToArray();
	}

	public static (Guid PresyncId, DeltaEntry[] Entries) Parse(byte[] body)
	{
		try { return ParseInternal(body); }
		catch (EndOfStreamException e) { throw new InvalidDataException("Delta body truncated", e); }
	}

	private static (Guid PresyncId, DeltaEntry[] Entries) ParseInternal(byte[] body)
	{
		using MemoryStream stream = new MemoryStream(body);
		using BinaryReader reader = new BinaryReader(stream, System.Text.Encoding.UTF8);
		if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Delta body magic mismatch");
		Guid presyncId = new Guid(reader.ReadBytes(16));
		int count = reader.ReadInt32();
		if (count < 0) throw new InvalidDataException("Delta entity count is negative");
		DeltaEntry[] entries = new DeltaEntry[count];
		for (int i = 0; i < count; i++)
		{
			DeltaEntry entry = new DeltaEntry { Id = reader.ReadInt64(), Changed = reader.ReadBoolean() };
			if (entry.Changed)
			{
				int paths = reader.ReadInt32();
				entry.Paths = new List<string>(paths);
				for (int p = 0; p < paths; p++) entry.Paths.Add(reader.ReadString());
				int length = reader.ReadInt32();
				entry.Bytes = reader.ReadBytes(length);
				if (entry.Bytes.Length != length) throw new InvalidDataException("Delta body truncated at entity " + entry.Id);
			}
			entries[i] = entry;
		}
		if (stream.Position != stream.Length) throw new InvalidDataException("Delta body has trailing bytes");
		return (presyncId, entries);
	}
}
