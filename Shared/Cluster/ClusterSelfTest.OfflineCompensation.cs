using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	private static void OfflineSaveSections()
	{
		void Check(bool value, string why) { if (!value) throw new InvalidOperationException("offline-save-sections: " + why); }
		byte[] body = [11, 22, 33], profiles = [91, 93];
		byte[] legacy = PartitionSaveFile.Write(1, 2, 3, 2, new BoundingSphereD(default, 5), false, 7, body, 100);
		var old = PartitionSaveFile.Read(legacy);
		Check(BinaryPrimitives.ReadUInt16LittleEndian(legacy.AsSpan(4)) == 1 && old.Body.SequenceEqual(body) && old.Profiles == null, "v1 read/write compatibility");
		byte[] modern = PartitionSaveFile.Write(1, 2, 3, 2, new BoundingSphereD(default, 5), false, 7, body, 100, profiles);
		var read = PartitionSaveFile.Read(modern);
		Check(BinaryPrimitives.ReadUInt16LittleEndian(modern.AsSpan(4)) == 2 && read.Body.SequenceEqual(body) && read.Profiles.SequenceEqual(profiles), "independent entity and profile sections");
		Check(read.EntityCount == 7 && read.PartitionId == 1 && read.Generation == 2 && read.Sequence == 3 && read.Kind == 2, "envelope unchanged");
		var header = PartitionSaveFile.ReadHeader(modern);
		Check(header.Body == null && header.Profiles == null && header.BodyChecksum == read.BodyChecksum, "header-only read verifies the same catalog checksum");
		bool Reject(byte[] file) { try { PartitionSaveFile.Read(file); return false; } catch (InvalidDataException) { return true; } }
		var corrupt = (byte[])modern.Clone(); corrupt[100] ^= 1;
		Check(Reject(corrupt), "profile-bearing body checksum enforced");
		Check(Reject(modern[..^1]), "truncated profile container refused");
		Check(Reject(modern.Concat(new byte[] { 0 }).ToArray()), "unframed trailing bytes refused");
		// This tiny payload uses no compression. Re-checksum deliberately malformed section lengths.
		Check(modern[25] == 0, "small payload is uncompressed for structural corruption proof");
		corrupt = (byte[])modern.Clone(); BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(100), int.MaxValue);
		BinaryPrimitives.WriteUInt64LittleEndian(corrupt.AsSpan(corrupt.Length - 8), PartitionSaveFile.BodyChecksum(corrupt.AsSpan(100, corrupt.Length - 116).ToArray()));
		Check(Reject(corrupt), "entity section cannot overlap/truncate profiles even with a valid checksum");
		bool oversized = false;
		try { PartitionSaveFile.Write(1, 2, 3, 2, default, false, 0, [], 100, new byte[PartitionSaveFile.MaximumProfileBytes + 1]); }
		catch (InvalidDataException) { oversized = true; }
		Check(oversized, "bounded profile allocation");
	}
}
