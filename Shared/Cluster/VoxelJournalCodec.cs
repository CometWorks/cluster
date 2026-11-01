using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using VRage.Voxels;
using VRageMath;

namespace ClusterPlugin;

internal static class VoxelJournalCodec
{
	private const uint RangeMagic = 0x31524A56;
	private const uint CutoutSphereMagic = 0x32524A56;
	private const int MaxPayload = 64 * 1024 * 1024;

	internal sealed record Entry(Guid OperationId, long AuthorityGeneration, long Sequence, string StorageName,
		Vector3I Min, Vector3I Max, MyStorageDataTypeFlags Flags, byte[] Content, byte[] Material,
		EntryKind Kind = EntryKind.Range, string OriginNode = null, Vector3D Center = default, float Radius = 0,
		bool CreateDebris = false, bool Damage = false);

	internal enum EntryKind : byte
	{
		Range,
		CutoutSphere
	}

	public static long NextSequence(long retainedSequence, long appliedSequence)
		=> checked(Math.Max(retainedSequence, appliedSequence) + 1);

	public static byte[] Write(Entry entry)
	{
		if (entry.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(entry.StorageName) || entry.Sequence < 0)
			throw new ArgumentException("Voxel journal identity, storage, and sequence are required");
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(entry.Kind == EntryKind.Range ? RangeMagic : CutoutSphereMagic);
			writer.Write(entry.OperationId.ToByteArray());
			writer.Write(entry.AuthorityGeneration);
			writer.Write(entry.Sequence);
			writer.Write(entry.StorageName);
			if (entry.Kind == EntryKind.Range)
			{
				int voxels = checked((entry.Max - entry.Min + 1).Size);
				if (voxels <= 0 || entry.Content?.Length != voxels || entry.Material?.Length != voxels)
					throw new ArgumentException("Voxel journal range and payload lengths do not match");
				writer.Write(entry.Min.X); writer.Write(entry.Min.Y); writer.Write(entry.Min.Z);
				writer.Write(entry.Max.X); writer.Write(entry.Max.Y); writer.Write(entry.Max.Z);
				writer.Write((byte)entry.Flags);
				writer.Write(voxels);
				writer.Write(entry.Content);
				writer.Write(entry.Material);
			}
			else if (entry.Kind == EntryKind.CutoutSphere)
			{
				if (string.IsNullOrWhiteSpace(entry.OriginNode) || !double.IsFinite(entry.Center.X)
					|| !double.IsFinite(entry.Center.Y) || !double.IsFinite(entry.Center.Z)
					|| !float.IsFinite(entry.Radius) || entry.Radius <= 0)
					throw new ArgumentException("Voxel cutout sphere origin and geometry are required");
				writer.Write(entry.OriginNode);
				writer.Write(entry.Center.X); writer.Write(entry.Center.Y); writer.Write(entry.Center.Z);
				writer.Write(entry.Radius);
				writer.Write(entry.CreateDebris);
				writer.Write(entry.Damage);
			}
			else throw new ArgumentException("Unknown voxel journal entry kind");
		}
		if (stream.Length > MaxPayload) throw new InvalidDataException("Voxel journal entry exceeds 64 MiB");
		byte[] body = stream.ToArray();
		using MemoryStream result = new();
		result.Write(body);
		result.Write(BitConverter.GetBytes(XxHash3.HashToUInt64(body)));
		return result.ToArray();
	}

	public static Entry Read(byte[] bytes)
	{
		if (bytes == null || bytes.Length < 8 || bytes.Length > MaxPayload + 8)
			throw new InvalidDataException("Invalid voxel journal length");
		int bodyLength = bytes.Length - 8;
		if (BitConverter.ToUInt64(bytes, bodyLength) != XxHash3.HashToUInt64(bytes.AsSpan(0, bodyLength)))
			throw new InvalidDataException("Voxel journal checksum mismatch");
		using MemoryStream stream = new(bytes, 0, bodyLength, writable: false);
		using BinaryReader reader = new(stream, Encoding.UTF8);
		uint magic = reader.ReadUInt32();
		Guid operationId = new(reader.ReadBytes(16));
		long generation = reader.ReadInt64(), sequence = reader.ReadInt64();
		string storage = reader.ReadString();
		if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(storage) || sequence < 0)
			throw new InvalidDataException("Invalid voxel journal identity");
		if (magic == CutoutSphereMagic)
		{
			string origin = reader.ReadString();
			Vector3D center = new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
			float radius = reader.ReadSingle();
			bool createDebris = reader.ReadBoolean(), damage = reader.ReadBoolean();
			if (string.IsNullOrWhiteSpace(origin) || !double.IsFinite(center.X) || !double.IsFinite(center.Y)
				|| !double.IsFinite(center.Z) || !float.IsFinite(radius) || radius <= 0 || stream.Position != stream.Length)
				throw new InvalidDataException("Invalid voxel cutout sphere envelope");
			return new Entry(operationId, generation, sequence, storage, default, default, default,
				Array.Empty<byte>(), Array.Empty<byte>(), EntryKind.CutoutSphere, origin, center, radius, createDebris, damage);
		}
		if (magic != RangeMagic) throw new InvalidDataException("Invalid voxel journal magic");
		Vector3I min = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
		Vector3I max = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
		MyStorageDataTypeFlags flags = (MyStorageDataTypeFlags)reader.ReadByte();
		int voxels = reader.ReadInt32();
		int expected = checked((max - min + 1).Size);
		if (voxels <= 0 || voxels != expected || stream.Length - stream.Position != voxels * 2L)
			throw new InvalidDataException("Invalid voxel journal envelope");
		return new Entry(operationId, generation, sequence, storage, min, max, flags, reader.ReadBytes(voxels), reader.ReadBytes(voxels));
	}
}
