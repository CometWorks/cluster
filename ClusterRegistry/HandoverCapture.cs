using System.Text;

namespace ClusterRegistry;

/// <summary>
/// The source node's capture payload: one frozen partition sector, the network-id state that goes
/// with it, and the replication view of every client riding the partition across the handover.
/// </summary>
/// <remarks>
/// Format 2 carries a client list rather than a single client. The payload opens with a -1 marker
/// where format 1 wrote the client-state length, so a format-1 reader rejects it as an invalid
/// length instead of silently mis-slicing the payload if gateway and plugin ever ship out of step.
/// </remarks>
internal sealed record HandoverCapture(IReadOnlyDictionary<ulong, string> ClientStates, string NetworkState, byte[] Sector)
{
    // Sanity bounds on lengths read from the payload, so a corrupt length cannot make
    // ReadBytes allocate wildly.  They are NOT a statement about how large a legitimate
    // state may be: ReadExactly still fails if the payload is shorter than claimed.
    //
    // MaxStateBytes was 1 << 20 until 2026-08-20 and that was far too low.  Partition 5375
    // captured 1,512,553 bytes of network state - 44% over - and every handover of it aborted
    // with "Invalid network state length".  Because the partition could not be captured it
    // could not move; node-3 was Closed holding it and could never drain; and since the
    // rotator refuses to rotate while any node is Closed, the ENTIRE FLEET stopped rotating
    // for over four hours and four pilots were locked out of the world.  A 1 MiB state bound
    // is also indefensible next to the 256 MiB allowed for the sector it accompanies.
    //
    // 64 MiB matches the bounds used elsewhere for comparable payloads (VoxelJournalCodec,
    // GatewayRegistry frame length) and leaves ~43x headroom over the size that broke w20 -
    // and the test grids are deliberately LOW block count, so real ones are much larger.
    private const int MaxStateBytes = 64 << 20;
    private const int MaxSectorBytes = 256 << 20;
    private const int MaxClients = 256;
    private const int Format = 2;

    public static HandoverCapture Parse(byte[] payload)
    {
        using BinaryReader reader = new(new MemoryStream(payload, writable: false));
        int marker = reader.ReadInt32();
        if (marker != -1)
            throw new InvalidDataException($"Unrecognised handover capture: expected the format marker, read {marker}");
        int format = reader.ReadInt32();
        if (format != Format)
            throw new InvalidDataException($"Unsupported handover capture format {format}; this gateway reads {Format}");
        int clientCount = reader.ReadInt32();
        if (clientCount < 0 || clientCount > MaxClients)
            throw new InvalidDataException($"Invalid client count: {clientCount}");
        ulong[] clients = new ulong[clientCount];
        int[] stateLengths = new int[clientCount];
        for (int i = 0; i < clientCount; i++)
        {
            clients[i] = reader.ReadUInt64();
            stateLengths[i] = ReadLength(reader, MaxStateBytes, "client state");
        }
        int networkLength = ReadLength(reader, MaxStateBytes, "network state");
        int sectorLength = ReadLength(reader, MaxSectorBytes, "sector");
        Dictionary<ulong, string> states = [];
        for (int i = 0; i < clientCount; i++)
            if (!states.TryAdd(clients[i], Encoding.UTF8.GetString(ReadExactly(reader, stateLengths[i]))))
                throw new InvalidDataException($"Duplicate client {clients[i]} in handover capture");
        string network = Encoding.UTF8.GetString(ReadExactly(reader, networkLength));
        byte[] sector = ReadExactly(reader, sectorLength);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Trailing bytes in handover capture");
        return new HandoverCapture(states, network, sector);
    }

    /// <summary>Total client-state bytes, for telemetry that used to report the single client's size.</summary>
    public int ClientStateBytes => ClientStates.Values.Sum(state => Encoding.UTF8.GetByteCount(state));

    public string StateFor(ulong client) => ClientStates.TryGetValue(client, out string? state) ? state
        : throw new InvalidDataException($"Handover capture carries no state for client {client}");

    private static int ReadLength(BinaryReader reader, int maximum, string name)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximum)
            throw new InvalidDataException($"Invalid {name} length: {length}");
        return length;
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] value = reader.ReadBytes(length);
        if (value.Length != length)
            throw new EndOfStreamException("Incomplete handover capture");
        return value;
    }
}
