using System.Text;

namespace ClusterRegistry;

/// <summary>
/// Hand-written codec for the two vanilla join messages the Gateway is not allowed to treat as
/// opaque (Gateway.md §3.5): CLIENT_CONNECTED carries the platform auth ticket the Gateway must
/// validate, and JOIN_RESULT must be synthesized when the Gateway rejects a client itself.
/// VRage.Network serializes both over a BitStream whose bits run LSB-first inside little-endian
/// 64-bit words, which is the same order as LSB-first over the byte array.
/// </summary>
internal static class VanillaJoinCodec
{
    public const byte ClientConnectedMessageId = 14;
    public const byte JoinResultMessageId = 12;
    public const byte ClientReadyMessageId = 8;
    private const int MessageHeaderBytes = 10; // 0xCE frame prefix (8) + [MyMessageId][receiverIndex]
    private const int MaximumTokenBytes = 1024; // MyDedicatedServerBase: Token is a 1024-byte buffer

    // Member order is NOT declaration order: MySerializerObject takes it from
    // TypeExtensions.GetDataMembers, which sorts by "DeclaringTypeName + MemberName" - so both
    // messages serialize their fields alphabetically. Golden fixtures in SelfTest pin this.
    public static ClientConnectedData DecodeClientConnected(ReadOnlySpan<byte> framePayload)
    {
        var reader = new BitReader(framePayload, MessageHeaderBytes);
        ulong clientId = reader.Read(64);
        bool experimentalMode = reader.ReadBool();
        bool isAdmin = reader.ReadBool();
        bool isProfiling = reader.ReadBool();
        bool join = reader.ReadBool();
        string? name = reader.ReadBool() ? reader.ReadString() : null;
        string? serviceName = reader.ReadBool() ? reader.ReadString() : null;
        byte[]? token = reader.ReadBool() ? reader.ReadByteArray(MaximumTokenBytes) : null;
        return new ClientConnectedData(clientId, serviceName, name, isAdmin, join, token, experimentalMode, isProfiling);
    }

    /// <summary>Frames a JOIN_RESULT exactly as MyNetworkWriter does for a reliable message.</summary>
    public static byte[] EncodeJoinResultFrame(JoinResult result, bool serverExperimental = false, ulong admin = 0)
    {
        var writer = new BitWriter(MessageHeaderBytes);
        writer.Write(admin, 64);
        writer.Write((ulong)result, JoinResultBits);
        writer.Write(serverExperimental ? 1u : 0u, 1);
        byte[] frame = writer.ToArray();
        frame[0] = 0xCE;
        frame[1] = 0; // reliable: no CRC
        frame[2] = frame[3] = frame[4] = frame[5] = 0xFF; // untouched CRC placeholder
        frame[6] = 0; // part index
        frame[7] = 1; // part count
        frame[8] = JoinResultMessageId;
        frame[9] = 0; // receiver index
        return frame;
    }

    // MySerializerEnum: (int)Math.Log(maxValue, 2) + 1 bits, JoinResult.IncorrectTime == 22.
    private const int JoinResultBits = 5;

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data, int byteOffset)
        {
            _data = data;
            _bit = byteOffset * 8;
            if (data.Length < byteOffset)
                throw new InvalidDataException("Vanilla message is shorter than its header");
        }

        public ulong Read(int bits)
        {
            if (_bit + bits > _data.Length * 8)
                throw new InvalidDataException("Vanilla message is truncated");
            ulong value = 0;
            for (int i = 0; i < bits; i++, _bit++)
                value |= (ulong)((_data[_bit >> 3] >> (_bit & 7)) & 1) << i;
            return value;
        }

        public bool ReadBool() => Read(1) != 0;

        public uint ReadVariant()
        {
            uint value = 0;
            for (int shift = 0; shift <= 28; shift += 7)
            {
                uint part = (uint)Read(8);
                value |= (part & 0x7F) << shift;
                if ((part & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("Vanilla message has an overlong variant");
        }

        public string ReadString()
        {
            int bytes = (int)ReadVariant();
            if (bytes == 0)
                return string.Empty;
            if (bytes > 4096)
                throw new InvalidDataException("Vanilla message string is too long");
            byte[] utf8 = new byte[bytes];
            for (int i = 0; i < bytes; i++)
                utf8[i] = (byte)Read(8);
            return Encoding.UTF8.GetString(utf8);
        }

        public byte[] ReadByteArray(int maximum)
        {
            int count = (int)ReadVariant();
            if (count > maximum)
                throw new InvalidDataException("Vanilla message byte array is too long");
            byte[] value = new byte[count];
            for (int i = 0; i < count; i++)
                value[i] = (byte)Read(8);
            return value;
        }
    }

    private struct BitWriter
    {
        private byte[] _data;
        private int _bit;

        public BitWriter(int byteOffset)
        {
            _data = new byte[byteOffset + 16];
            _bit = byteOffset * 8;
        }

        public void Write(ulong value, int bits)
        {
            int required = (_bit + bits + 7) / 8;
            if (required > _data.Length)
                Array.Resize(ref _data, Math.Max(required, _data.Length * 2));
            for (int i = 0; i < bits; i++, _bit++)
                if ((value >> i & 1) != 0)
                    _data[_bit >> 3] |= (byte)(1 << (_bit & 7));
        }

        public byte[] ToArray() => _data[..((_bit + 7) / 8)]; // BitStream.BytePosition rounds up
    }
}

internal sealed record ClientConnectedData(ulong ClientId, string? ServiceName, string? Name, bool IsAdmin, bool Join,
    byte[]? Token, bool ExperimentalMode, bool IsProfiling);

/// <summary>VRage.Network.JoinResult, pinned by ordinal - the enum is serialized as its value.</summary>
internal enum JoinResult
{
    OK,
    AlreadyJoined,
    TicketInvalid,
    SteamServersOffline,
    NotInGroup,
    GroupIdInvalid,
    ServerFull,
    BannedByAdmins,
    KickedRecently,
    TicketCanceled,
    TicketAlreadyUsed,
    LoggedInElseWhere,
    NoLicenseOrExpired,
    UserNotConnected,
    VACBanned,
    VACCheckTimedOut,
    PasswordRequired,
    WrongPassword,
    ExperimentalMode,
    ProfilingNotAllowed,
    FamilySharing,
    NotFound,
    IncorrectTime,
}
