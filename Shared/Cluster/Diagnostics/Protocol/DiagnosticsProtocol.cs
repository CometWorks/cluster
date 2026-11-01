using System;
using System.IO;
using System.Text;

namespace CometWorks.ClusterGateway.AdminContract.V1;

public static class DiagnosticsProtocol
{
    public const ushort Channel = 48739;
    public const int MaxPacketBytes = 48 * 1024;
    public const int MaxNodes = 24;
    private const byte Version = 4;
    private const int MaxStringBytes = 512;

    public static byte[] Request(Guid session)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write(session.ToByteArray());
        return stream.ToArray();
    }

    public static bool TryRequest(byte[] data, out Guid session)
    {
        session = Guid.Empty;
        if (data == null || data.Length != 17 || data[0] != Version) return false;
        var bytes = new byte[16];
        Array.Copy(data, 1, bytes, 0, bytes.Length);
        session = new Guid(bytes);
        return session != Guid.Empty;
    }

    // Permission is applied while serializing: basic packets never contain advanced fields.
    public static byte[] Encode(DiagnosticsSnapshot snapshot, Guid session, bool admin)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Version);
        writer.Write(session.ToByteArray());
        writer.Write(admin);
        writer.Write(snapshot.ServerAt);
        WriteNode(writer, snapshot.Current);
        if (admin)
        {
            writer.Write(snapshot.RegistryAvailable);
            writer.Write(snapshot.CapturedAt);
            WriteString(writer, snapshot.Registry);
            writer.Write(snapshot.TotalNodes);
            int count = Math.Min(snapshot.Nodes.Length, MaxNodes);
            writer.Write(count);
            for (int i = 0; i < count; i++) WriteNode(writer, snapshot.Nodes[i]);
        }
        if (stream.Length > MaxPacketBytes) throw new InvalidDataException("Diagnostics packet too large");
        return stream.ToArray();
    }

    public static DiagnosticsSnapshot Decode(byte[] data, Guid session)
    {
        if (data == null || data.Length > MaxPacketBytes) throw new InvalidDataException("Invalid packet size");
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadByte() != Version || new Guid(ReadBytes(reader, 16)) != session)
            throw new InvalidDataException("Wrong protocol or session");
        var result = new DiagnosticsSnapshot { Admin = reader.ReadBoolean(), ServerAt = reader.ReadInt64(), Current = ReadNode(reader) };
        if (result.Admin)
        {
            result.RegistryAvailable = reader.ReadBoolean();
            result.CapturedAt = reader.ReadInt64();
            result.Registry = ReadString(reader);
            result.TotalNodes = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxNodes || result.TotalNodes < count) throw new InvalidDataException("Invalid node count");
            result.Nodes = new NodeDiagnostics[count];
            for (int i = 0; i < count; i++) result.Nodes[i] = ReadNode(reader);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing diagnostics data");
        return result;
    }

    private static void WriteNode(BinaryWriter writer, NodeDiagnostics node)
    {
        WriteString(writer, node.Name);
        WriteString(writer, node.Endpoint);
        writer.Write(node.Epoch);
        writer.Write(node.AgeSeconds);
        writer.Write(node.ProcessUptimeSeconds);
        writer.Write(node.ServingSeconds);
        writer.Write(node.DrainAt);
        writer.Write(node.RotationDueAt);
        writer.Write(node.RotationExpiresAt);
        writer.Write((byte)node.Rotation);
        WriteString(writer, node.State);
        writer.Write(node.Serving);
        writer.Write(node.Spare);
        writer.Write(node.Authority);
        writer.Write(node.AuthorityGeneration);
        writer.Write(node.LeaseAt);
    }

    private static NodeDiagnostics ReadNode(BinaryReader reader) => new NodeDiagnostics
    {
        Name = ReadString(reader), Endpoint = ReadString(reader), Epoch = reader.ReadInt64(),
        AgeSeconds = reader.ReadInt64(), ProcessUptimeSeconds = reader.ReadInt64(), ServingSeconds = reader.ReadInt64(), DrainAt = reader.ReadInt64(),
        RotationDueAt = reader.ReadInt64(), RotationExpiresAt = reader.ReadInt64(), Rotation = ReadRotation(reader),
        State = ReadString(reader), Serving = reader.ReadBoolean(), Spare = reader.ReadBoolean(),
        Authority = reader.ReadBoolean(), AuthorityGeneration = reader.ReadInt64(), LeaseAt = reader.ReadInt64()
    };

    private static RotationState ReadRotation(BinaryReader reader)
    {
        var state = (RotationState)reader.ReadByte();
        if (!Enum.IsDefined(typeof(RotationState), state)) throw new InvalidDataException("Invalid rotation state");
        return state;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length > MaxStringBytes) throw new InvalidDataException("Diagnostics string too long");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if (length > MaxStringBytes) throw new InvalidDataException("Diagnostics string too long");
        return Encoding.UTF8.GetString(ReadBytes(reader, length));
    }

    private static byte[] ReadBytes(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
