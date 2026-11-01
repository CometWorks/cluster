using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using VRage.Game;
using VRage.Library.Collections;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;
using VRageMath;
using ZstdSharp;

namespace ClusterPlugin;

internal static class PartitionSaveFile
{
	private const ushort Version = 1;
	private const int HeaderLength = 100;
	private const int TrailerLength = 16;
	private const byte None = 0;
	private const byte Lz4 = 1;
	private const byte Zstd = 2;

	public static byte[] SerializeEntities(IReadOnlyList<MyObjectBuilder_EntityBase> entities)
	{
		using BitStream stream = new BitStream();
		stream.ResetWrite();
		for (int i = 0; i < entities.Count; i++)
		{
			MyObjectBuilder_EntityBase entity = entities[i] ?? throw new InvalidDataException("Partition contains a null entity");
			MySerializer.Write(stream, ref entity, MyObjectBuilderSerializerKeen.Dynamic);
		}

		byte[] body = new byte[stream.BytePosition];
		Marshal.Copy(stream.DataPointer, body, 0, body.Length);
		return body;
	}

	public static MyObjectBuilder_EntityBase[] DeserializeEntities(byte[] body, int entityCount)
	{
		if (body == null || entityCount < 0)
		{
			throw new InvalidDataException("Invalid partition entity body");
		}

		using BitStream stream = new BitStream();
		stream.ResetRead(body, 0, (long)body.Length * 8);
		MyObjectBuilder_EntityBase[] entities = new MyObjectBuilder_EntityBase[entityCount];
		for (int i = 0; i < entities.Length; i++)
		{
			entities[i] = MySerializer.CreateAndRead<MyObjectBuilder_EntityBase>(stream, MyObjectBuilderSerializerKeen.Dynamic);
		}
		return entities;
	}

	public static byte[] Write(ulong partitionId, uint generation, uint sequence, byte kind, BoundingSphereD sphere, bool anchored, int entityCount, byte[] body, long clusterTime)
	{
		if (body == null || entityCount < 0)
		{
			throw new ArgumentException("Invalid partition body or entity count");
		}

		// T-0147: a partition body on the lattice world is ~15 MB, and every array this size is a
		// large-object-heap allocation the GC never compacts. The compressed form goes into a POOLED
		// scratch buffer and the file is written once into its exact final array - two large
		// allocations per save (the caller's body and the file) instead of five (compressed, its
		// trimmed copy, the stream buffer and ToArray on top of the body).
		byte compression = kind == 1 ? Lz4 : Zstd;
		int bound = compression == Lz4 ? LZ4Codec.MaximumOutputSize(body.Length) : Compressor.GetCompressBound(body.Length);
		byte[] scratch = ArrayPool<byte>.Shared.Rent(bound);
		try
		{
			int storedLength = compression == Lz4
				? LZ4Codec.Encode(body, 0, body.Length, scratch, 0, bound, LZ4Level.L00_FAST)
				: CompressZstd(body, scratch.AsSpan(0, bound));
			ReadOnlySpan<byte> storedBody;
			if (storedLength <= 0 || storedLength >= body.Length)
			{
				compression = None;
				storedBody = body;
			}
			else
			{
				storedBody = scratch.AsSpan(0, storedLength);
			}
			byte[] file = new byte[HeaderLength + storedBody.Length + TrailerLength];
			WriteHeader(file, partitionId, generation, sequence, kind, compression, sphere, anchored, entityCount, storedBody.Length, body.Length, clusterTime);
			storedBody.CopyTo(file.AsSpan(HeaderLength));
			BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HeaderLength + storedBody.Length), Checksum(file.AsSpan(0, HeaderLength)));
			BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HeaderLength + storedBody.Length + 8), Checksum(storedBody));
			return file;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(scratch);
		}
	}

	private static void WriteHeader(byte[] file, ulong partitionId, uint generation, uint sequence, byte kind, byte compression, BoundingSphereD sphere,
		bool anchored, int entityCount, long storedLength, long bodyLength, long clusterTime)
	{
		using MemoryStream stream = new MemoryStream(file, 0, HeaderLength, writable: true);
		using BinaryWriter writer = new BinaryWriter(stream);
		writer.Write(new byte[] { (byte)'M', (byte)'P', (byte)'S', (byte)'1' });
		writer.Write(Version);
		writer.Write((ushort)HeaderLength);
		writer.Write(partitionId);
		writer.Write(generation);
		writer.Write(sequence);
		writer.Write(kind);
		writer.Write(compression);
		writer.Write(anchored);
		writer.Write((byte)0);
		writer.Write(sphere.Center.X);
		writer.Write(sphere.Center.Y);
		writer.Write(sphere.Center.Z);
		writer.Write(sphere.Radius);
		writer.Write(0d);
		writer.Write(clusterTime);
		writer.Write(storedLength);
		writer.Write(bodyLength);
		writer.Write(entityCount);
		writer.Write(0u);
		writer.Flush();
		if (stream.Position != HeaderLength)
		{
			throw new InvalidOperationException("Invalid partition save header length " + stream.Position);
		}
	}

	/// <summary>
	/// T-0345: CLUSTER_SAVE_FSYNC=all restores an fsync for every save file. By default only saves that must
	/// survive a host power loss on their own are fsynced (Final, HandoverTee, the World Authority's global saves):
	/// on btrfs an fsynced ~35 kB file costs ~3.8x its size in device writes, and a Periodic save or a fetched copy
	/// that a power loss tears is caught by its checksum and recovery falls back to another holder or the previous save.
	/// </summary>
	public static readonly bool FsyncEverySave = Environment.GetEnvironmentVariable("CLUSTER_SAVE_FSYNC") == "all";

	/// <param name="storeOwner">T-0340: set (the writing node's id) when <paramref name="path"/> is in the per-host save
	/// store, which other nodes of the host write too: the temporary name carries the id and the file is installed
	/// without replacing one that is already there (ClusterProtocol.HostSaveStore.Install) - the same save is success,
	/// a readable save with other content under this id is refused. Null: the node's own directory, as always.</param>
	public static void WriteFile(string path, byte[] file, bool durable = true, string storeOwner = null)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Partition save path is required", nameof(path));
		}
		ReadHeader(file);

		string fullPath = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Partition save path has no directory", nameof(path));
		Directory.CreateDirectory(directory);
		string temporaryPath = storeOwner != null ? ClusterProtocol.HostSaveStore.Temporary(fullPath, storeOwner)
			: Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				stream.Write(file);
				stream.Flush(flushToDisk: durable || FsyncEverySave);
			}
			Parsed written = ReadHeader(File.ReadAllBytes(temporaryPath));
			if (storeOwner != null) ClusterProtocol.HostSaveStore.Install(temporaryPath, fullPath, existing => StoredIs(existing, written.BodyChecksum), replaceOther: false);
			else File.Move(temporaryPath, fullPath, overwrite: true);
		}
		finally
		{
			File.Delete(temporaryPath);
		}
	}

	/// <summary>T-0340: what a file already in the host store is, for an install: true - the save with this body
	/// checksum; false - another readable save; null - damaged (a torn write).</summary>
	public static bool? StoredIs(string path, ulong bodyChecksum)
	{
		try { return ReadHeader(File.ReadAllBytes(path)).BodyChecksum == bodyChecksum; }
		catch (InvalidDataException) { return null; }
	}

	public static Parsed ReadFile(string path) => Read(File.ReadAllBytes(path));

	public static Parsed Read(byte[] file) => Parse(file, withBody: true);

	/// <summary>
	/// T-0147: the envelope, both checksums and the length rules verified exactly as Read does, but the
	/// body stays compressed in the file - Body is null. For the callers that only route on Kind, compare
	/// a checksum or verify a file they just wrote; each of those was decompressing a ~15 MB lattice body
	/// onto the large-object heap and dropping it.
	/// </summary>
	public static Parsed ReadHeader(byte[] file) => Parse(file, withBody: false);

	/// <summary>T-0399: <see cref="ReadHeader(byte[])"/> over the first <paramref name="length"/> bytes of a reused buffer
	/// (the global scrub reads every file into one array instead of a large-object-heap array per file).</summary>
	public static Parsed ReadHeader(byte[] buffer, int length) => Parse(buffer, withBody: false, length);

	private static Parsed Parse(byte[] file, bool withBody, int length = -1)
	{
		if (file != null && length < 0) length = file.Length;
		if (file == null || length < HeaderLength + TrailerLength || length > file.Length)
		{
			throw new InvalidDataException("Partition save is truncated");
		}
		using MemoryStream stream = new MemoryStream(file, 0, length, writable: false);
		using BinaryReader reader = new BinaryReader(stream);
		if (reader.ReadByte() != 'M' || reader.ReadByte() != 'P' || reader.ReadByte() != 'S' || reader.ReadByte() != '1')
		{
			throw new InvalidDataException("Partition save magic is not MPS1");
		}
		if (reader.ReadUInt16() != Version || reader.ReadUInt16() != HeaderLength)
		{
			throw new InvalidDataException("Unsupported partition save version");
		}
		ulong id = reader.ReadUInt64();
		uint generation = reader.ReadUInt32();
		uint sequence = reader.ReadUInt32();
		byte kind = reader.ReadByte();
		byte compression = reader.ReadByte();
		bool anchored = reader.ReadBoolean();
		reader.ReadByte();
		BoundingSphereD sphere = new BoundingSphereD(new Vector3D(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()), reader.ReadDouble());
		reader.ReadDouble();
		long clusterTime = reader.ReadInt64();
		long compressedLength = reader.ReadInt64();
		long uncompressedLength = reader.ReadInt64();
		int entityCount = reader.ReadInt32();
		reader.ReadUInt32();
		if ((compression != None && compression != Lz4 && compression != Zstd) || compressedLength < 0 || compressedLength > int.MaxValue || uncompressedLength < 0 || uncompressedLength > int.MaxValue || entityCount < 0 || HeaderLength + compressedLength + TrailerLength != length)
		{
			throw new InvalidDataException("Invalid partition save body lengths or compression");
		}

		// T-0147: the stored body is read in place (no ReadBytes copy) and decompressed straight into
		// the one array the caller gets.
		ReadOnlySpan<byte> storedBody = file.AsSpan(HeaderLength, (int)compressedLength);
		ulong headerChecksum = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(HeaderLength + (int)compressedLength));
		ulong bodyChecksum = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(HeaderLength + (int)compressedLength + 8));
		if (headerChecksum != Checksum(file.AsSpan(0, HeaderLength)) || bodyChecksum != Checksum(storedBody))
		{
			throw new InvalidDataException("Partition save checksum mismatch");
		}
		if (!withBody)
		{
			if (compression == None && compressedLength != uncompressedLength)
			{
				throw new InvalidDataException("Partition save uncompressed length mismatch");
			}
			return new Parsed(id, generation, sequence, kind, sphere, anchored, entityCount, clusterTime, bodyChecksum, null);
		}
		byte[] body = compression switch
		{
			Lz4 => DecompressLz4(file, HeaderLength, (int)compressedLength, (int)uncompressedLength),
			Zstd => DecompressZstd(storedBody, (int)uncompressedLength),
			_ => storedBody.ToArray()
		};
		if (body.Length != uncompressedLength)
		{
			throw new InvalidDataException("Partition save uncompressed length mismatch");
		}
		return new Parsed(id, generation, sequence, kind, sphere, anchored, entityCount, clusterTime, bodyChecksum, body);
	}

	public static void ValidateEnvelope(Parsed save, ulong partitionId, uint generation, uint? sequence = null)
	{
		if (save.PartitionId != partitionId)
		{
			throw new InvalidDataException("Partition save ID does not match transfer partition");
		}
		if (save.Generation != generation)
		{
			throw new InvalidDataException("Partition save generation does not match transfer generation");
		}
		if (sequence.HasValue && save.Sequence != sequence.Value)
		{
			throw new InvalidDataException("Partition save sequence does not match catalog sequence");
		}
	}

	internal static void MergeEntities(IDictionary<long, MyObjectBuilder_EntityBase> target, IEnumerable<MyObjectBuilder_EntityBase> entities, ISet<long> filter)
	{
		foreach (MyObjectBuilder_EntityBase entity in entities)
			if ((filter == null || filter.Contains(entity.EntityId)) && !target.ContainsKey(entity.EntityId)) target.Add(entity.EntityId, entity);
	}

	private static byte[] DecompressLz4(byte[] file, int offset, int length, int expectedLength)
	{
		byte[] decompressed = new byte[expectedLength];
		if (LZ4Codec.Decode(file, offset, length, decompressed, 0, decompressed.Length) != expectedLength)
			throw new InvalidDataException("Invalid LZ4 partition body");
		return decompressed;
	}

	/// <summary>Compresses into the caller's buffer; returns the stored length.</summary>
	private static int CompressZstd(byte[] body, Span<byte> destination)
	{
		using Compressor compressor = new Compressor(3);
		return compressor.Wrap(body, destination);
	}

	private static byte[] DecompressZstd(ReadOnlySpan<byte> stored, int expectedLength)
	{
		byte[] decompressed = new byte[expectedLength];
		using Decompressor decompressor = new Decompressor();
		if (decompressor.Unwrap(stored, decompressed) != expectedLength)
			throw new InvalidDataException("Invalid zstd partition body");
		return decompressed;
	}

	private static ulong Checksum(ReadOnlySpan<byte> value) => XxHash3.HashToUInt64(value);

	/// <summary>The checksum Read reports for an uncompressed (None) body of these bytes.</summary>
	public static ulong BodyChecksum(byte[] body) => Checksum(body);

	/// <summary>Body is null when the record came from ReadHeader.</summary>
	internal sealed record Parsed(ulong PartitionId, uint Generation, uint Sequence, byte Kind, BoundingSphereD Sphere, bool Anchored, int EntityCount, long ClusterTime, ulong BodyChecksum, byte[] Body);
}
