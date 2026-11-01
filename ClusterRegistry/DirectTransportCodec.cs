using System.Buffers.Binary;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ClusterRegistry;

internal static class DirectTransportCodec
{
    public const ulong NodeLinkId = ulong.MaxValue;
    private const string NodeLinkKey = "SE-ClusterNodeLink-v2";

    public static bool TryDecodeHandshake(NetDataReader reader, out ulong clientId)
        => TryDecodeHandshake(reader, out clientId, out _);

    /// <summary>
    /// The optional trailing name is what the client wants to be called. Security.md §3.6: this debug
    /// frontend already takes the client's word for who it is, so an asserted name is no weaker than the
    /// asserted id it would otherwise be named after - and unlike the id, it is readable. A node link puts
    /// its own key in that position, so only a client id is offered one.
    /// </summary>
    public static bool TryDecodeHandshake(NetDataReader reader, out ulong clientId, out string? name)
    {
        clientId = 0;
        name = null;
        if (reader.AvailableBytes < 1)
            return false;

        string key = reader.GetString();
        if (key != DirectTransportConstants.ProtocolKey || reader.AvailableBytes < sizeof(ulong))
            return false;

        clientId = reader.GetULong();
        if (clientId != NodeLinkId && reader.AvailableBytes > 0 && reader.TryGetString(out string asserted))
            name = SanitizeName(asserted);
        return true;
    }

    /// <summary>A name reaches chat lines, the player list and an identity, so it has to be one: printable and bounded.</summary>
    private static string? SanitizeName(string asserted)
    {
        string clean = new string(asserted.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (clean.Length > 32) clean = clean[..32];
        return clean.Length == 0 ? null : clean;
    }

    public static NetDataWriter EncodeHandshake(ulong clientId, string? name = null)
    {
        var writer = new NetDataWriter();
        writer.Put(DirectTransportConstants.ProtocolKey);
        writer.Put(clientId);
        if (name != null)
            writer.Put(name);
        return writer;
    }

    /// <summary>
    /// Security.md §4.1/§4.3: the node link authenticates cluster membership with the
    /// registry-issued join token, not with a well-known protocol string.
    /// </summary>
    public static NetDataWriter EncodeNodeLinkHandshake(string joinToken)
    {
        NetDataWriter writer = EncodeHandshake(NodeLinkId);
        writer.Put(NodeLinkKey);
        writer.Put(joinToken);
        return writer;
    }

    public static NetDataWriter EncodeNodeLink(NodeLinkPacket packet)
    {
        if (packet.Kind == NodeLinkMessage.Attach && packet.Payload.Length > 4096)
            throw new InvalidDataException("Node-link attach payload is too large");
        int size = 1 + sizeof(ulong) + (packet.Kind == NodeLinkMessage.Relay ? packet.Payload.Length + 1
            : packet.Kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach ? packet.Payload.Length
            : packet.Kind == NodeLinkMessage.Credit ? sizeof(int) : 0);
        var writer = new NetDataWriter(true, size);
        writer.Put((byte)packet.Kind);
        writer.Put(packet.ClientId);
        if (packet.Kind == NodeLinkMessage.Relay)
        {
            writer.Put(packet.Channel);
            writer.Put(packet.Payload);
        }
        else if (packet.Kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin or NodeLinkMessage.Attach)
            writer.Put(packet.Payload);
        else if (packet.Kind == NodeLinkMessage.Credit)
            writer.Put(packet.Credit);
        return writer;
    }

    public static NodeLinkPacket DecodeNodeLink(byte[] payload, DeliveryMethod delivery)
    {
        if (payload.Length < 1 + sizeof(ulong))
            throw new InvalidDataException("Node-link packet is truncated");

        var reader = new NetDataReader(payload);
        NodeLinkMessage kind = (NodeLinkMessage)reader.GetByte();
        ulong clientId = reader.GetULong();
        if (kind == NodeLinkMessage.Attach)
        {
            if (reader.AvailableBytes > 4096) throw new InvalidDataException("Node-link attach payload is too large");
            return new NodeLinkPacket(kind, clientId, 0, reader.GetRemainingBytes(), delivery);
        }
        if (kind is NodeLinkMessage.Detach or NodeLinkMessage.AttachAck or NodeLinkMessage.DetachAck)
        {
            if (reader.AvailableBytes != 0)
                throw new InvalidDataException("Node-link control packet has trailing data");
            return new NodeLinkPacket(kind, clientId, 0, [], delivery);
        }
        if (kind == NodeLinkMessage.Credit)
        {
            if (reader.AvailableBytes != sizeof(int)) throw new InvalidDataException("Invalid node-link credit packet");
            int credit = reader.GetInt();
            if (credit <= 0) throw new InvalidDataException("Node-link credit must be positive");
            return new NodeLinkPacket(kind, clientId, 0, [], delivery, credit);
        }
        if (kind is NodeLinkMessage.Global or NodeLinkMessage.Plugin)
        {
            // No size cap here: the node side (NodeLinkCodec.Write) never bounds a Global payload, and
            // a full global-state sync legitimately exceeds 4 KB. A 4096 cap briefly added here made
            // the gateway drop the node link on the first big frame after the 2026-08-10 restart -
            // every link disconnect-looped and no character home could recover.
            if (clientId != 0 || reader.AvailableBytes == 0 || kind == NodeLinkMessage.Plugin && reader.AvailableBytes > 128 * 1024) throw new InvalidDataException("Invalid node-link global packet");
            return new NodeLinkPacket(kind, 0, 0, reader.GetRemainingBytes(), delivery);
        }
        if (kind != NodeLinkMessage.Relay || reader.AvailableBytes < 1)
            throw new InvalidDataException("Unknown or truncated node-link packet");
        byte channel = reader.GetByte();
        return new NodeLinkPacket(kind, clientId, channel, reader.GetRemainingBytes(), delivery);
    }

    public static bool TryDecodeNodeLinkHandshake(NetDataReader reader, out string joinToken)
    {
        joinToken = string.Empty;
        if (reader.AvailableBytes == 0 || reader.GetString() != NodeLinkKey || reader.AvailableBytes == 0)
            return false;
        joinToken = reader.GetString();
        return reader.AvailableBytes == 0;
    }

    public static GatewayPacket DecodePacket(ulong clientId, byte[] wirePayload, DeliveryMethod delivery)
    {
        if (wirePayload.Length == 0)
            throw new InvalidDataException("Direct Transport packet is missing the channel byte");

        byte[] payload = new byte[wirePayload.Length - 1];
        Buffer.BlockCopy(wirePayload, 1, payload, 0, payload.Length);
        if (wirePayload[0] == 2)
            ParseGameFrame(payload, delivery);
        return new GatewayPacket(clientId, wirePayload[0], payload, delivery);
    }

    public static NetDataWriter EncodePacket(GatewayPacket packet)
    {
        if ((uint)packet.Channel > byte.MaxValue)
            throw new InvalidDataException("Direct Transport channel is outside the byte framing range");
        var writer = new NetDataWriter(true, packet.Payload.Length + 1);
        writer.Put((byte)packet.Channel);
        writer.Put(packet.Payload, 0, packet.Payload.Length);
        return writer;
    }

    public static bool TryGetGameMessageId(GatewayPacket packet, out byte messageId)
    {
        messageId = 0;
        if (packet.Channel != 2)
            return false;
        GameFrame frame = ParseGameFrame(packet.Payload, packet.Delivery);
        messageId = frame.MessageId;
        return frame.PartIndex == 0;
    }

    public static GameFrame ParseGameFrame(ReadOnlySpan<byte> payload, DeliveryMethod delivery)
    {
        if (payload.Length < 8 || payload[0] != 0xCE)
            throw new InvalidDataException("Invalid vanilla game frame header");
        byte crcFlag = payload[1];
        if (crcFlag > 1)
            throw new InvalidDataException("Invalid vanilla game frame CRC flag");
        byte partIndex = payload[6], partCount = payload[7];
        if (partCount == 0 || partIndex >= partCount || partIndex == 0 && payload.Length < 10)
            throw new InvalidDataException("Invalid vanilla game frame multipart header");
        if (crcFlag == 1)
        {
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..6]);
            uint actual = Crc32(payload[6..]);
            if (expected != actual)
                throw new InvalidDataException("Vanilla game frame CRC mismatch");
        }
        return new GameFrame(crcFlag != 0, partIndex, partCount, partIndex == 0 ? payload[8] : (byte)0,
            partIndex == 0 ? payload[9] : (byte)0);
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

internal readonly record struct GameFrame(bool HasCrc, byte PartIndex, byte PartCount, byte MessageId, byte ReceiverIndex);

internal enum NodeLinkMessage : byte
{
    Relay = 1,
    Attach = 2,
    Detach = 3,
    AttachAck = 4,
    DetachAck = 5,
    Credit = 6,
    Global = 7,
    Plugin = 8,
}

internal sealed record NodeLinkPacket(NodeLinkMessage Kind, ulong ClientId, byte Channel, byte[] Payload, DeliveryMethod Delivery, int Credit = 0);
