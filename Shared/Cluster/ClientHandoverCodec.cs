using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClusterPlugin;

public static class ClientHandoverCodec
{
    private const uint Magic = 0x31564843; // CHV1
    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private const int MaxReplicables = 100000;
    private const int MaxStrings = 100000;
    private const int MaxStringBytes = 4096;
    private const int MaxInventoryGroups = 64;
    private const int MaxVoxelGroups = 8;
    private const int MaxHeldItems = 100000;
    private const int MaxUnsentDeltas = 1024;

    /// <summary>SE-1554: written in place of the mirror section's count when the source did not say what the client
    /// holds of the player mirror (MirrorHeld null) and a later section follows.</summary>
    private const int NoMirror = -1;

    public static string Write(ClientHandoverState state)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(state.StateSyncPacketId);
        writer.Write(state.StreamingPacketId);
        writer.Write(state.LastReceivedAckId);
        writer.Write(state.LastClientStreamingAck);
        writer.Write(state.LastProcessedClientPacketId);
        writer.Write(state.ClientTimestampMilliseconds);
        writer.Write(state.PriorityMultiplier);
        writer.Write(state.PCULimit.HasValue);
        if (state.PCULimit.HasValue)
            writer.Write(state.PCULimit.Value);
        writer.Write(state.WantsBatchCompleteConfirmation);
        writer.Write(state.ForcePlayoutDelayBuffer);
        writer.Write(state.UsePlayoutDelayBufferForCharacter);
        writer.Write(state.UsePlayoutDelayBufferForJetpack);
        writer.Write(state.UsePlayoutDelayBufferForGrids);
        writer.Write(state.ControlledReplicableId);
        writer.Write(state.CharacterReplicableId);
        writer.Write(state.ControlledEntityId);
        writer.Write(state.CharacterEntityId);
        WriteCount(writer, state.Replicables.Count, MaxReplicables);
        foreach (ClientReplicableState replicable in state.Replicables)
        {
            writer.Write(replicable.NetworkId);
            writer.Write(replicable.IsPending);
            writer.Write(replicable.IsStreaming);
            writer.Write(replicable.Layer);
            writer.Write(replicable.PermanentLayer.HasValue);
            if (replicable.PermanentLayer.HasValue)
                writer.Write(replicable.PermanentLayer.Value);
        }
        WriteCount(writer, state.CachedVoxelNames.Count, MaxStrings);
        foreach (string name in state.CachedVoxelNames)
            WriteString(writer, name);
        WriteCount(writer, state.KnownSectors.Count, MaxStrings);
        foreach ((long planet, HashSet<long> sectors) in state.KnownSectors)
        {
            writer.Write(planet);
            WriteCount(writer, sectors.Count, MaxStrings);
            foreach (long sector in sectors)
                writer.Write(sector);
        }
        // T-0265: inventory message counters, an optional trailing section (a state without it reads as none).
        List<ClientReplicableState> withInventory = state.Replicables.FindAll(x =>
            x.InventoryMessageIds.Count != 0
        );
        // T-0368: carried asteroids, a second optional trailing section; the inventory section precedes it even when empty.
        List<ClientReplicableState> withVoxel = state.Replicables.FindAll(x =>
            x.VoxelEntityId != 0
        );
        // SE-1192: the player mirror's delivered set, a third optional trailing section after the other two.
        bool mirror = state.MirrorHeld != null;
        // SE-1554: what a client holds of inventories whose changes had not all reached it, a fourth optional trailing section.
        List<ClientReplicableState> withHeld = state.Replicables.FindAll(x =>
            x.InventoryHeld.Count != 0
        );
        bool held = withHeld.Count != 0;
        if (withInventory.Count != 0 || withVoxel.Count != 0 || mirror || held)
        {
            WriteCount(writer, withInventory.Count, MaxReplicables);
            foreach (ClientReplicableState replicable in withInventory)
            {
                writer.Write(replicable.NetworkId);
                WriteCount(writer, replicable.InventoryMessageIds.Count, MaxInventoryGroups);
                foreach (uint id in replicable.InventoryMessageIds)
                    writer.Write(id);
            }
        }
        if (withVoxel.Count != 0 || mirror || held)
        {
            WriteCount(writer, withVoxel.Count, MaxReplicables);
            foreach (ClientReplicableState replicable in withVoxel)
            {
                writer.Write(replicable.NetworkId);
                writer.Write(replicable.VoxelEntityId);
                writer.Write(replicable.VoxelApplied);
                WriteCount(writer, replicable.VoxelGroupIds.Count, MaxVoxelGroups);
                foreach (uint id in replicable.VoxelGroupIds)
                    writer.Write(id);
            }
        }
        if (mirror)
        {
            WriteCount(writer, state.MirrorHeld.Count, MaxStrings);
            foreach (MirrorHolding holding in state.MirrorHeld)
            {
                writer.Write(holding.SteamId);
                writer.Write(holding.SerialId);
                writer.Write((byte)((holding.Member ? 1 : 0) | (holding.Player ? 2 : 0)));
            }
        }
        else if (held)
            writer.Write(NoMirror);
        if (held)
        {
            List<string> definitions = new List<string>();
            Dictionary<string, int> index = new Dictionary<string, int>();
            foreach (ClientReplicableState replicable in withHeld)
            foreach (InventoryHeld inventory in replicable.InventoryHeld)
            foreach (InventoryHeldItem item in inventory.Items)
                if (
                    index.TryAdd(
                        item.Definition
                            ?? throw new InvalidDataException("Held item without a definition"),
                        definitions.Count
                    )
                )
                    definitions.Add(item.Definition);
            WriteCount(writer, definitions.Count, MaxStrings);
            foreach (string definition in definitions)
                WriteString(writer, definition);
            WriteCount(writer, withHeld.Count, MaxReplicables);
            foreach (ClientReplicableState replicable in withHeld)
            {
                writer.Write(replicable.NetworkId);
                WriteCount(writer, replicable.InventoryHeld.Count, MaxInventoryGroups);
                foreach (InventoryHeld inventory in replicable.InventoryHeld)
                {
                    writer.Write(inventory.Group);
                    writer.Write(inventory.ItemsSaid);
                    WriteCount(writer, inventory.Unsent.Count, MaxUnsentDeltas);
                    foreach (byte[] delta in inventory.Unsent)
                    {
                        WriteCount(writer, delta.Length, MaxPayloadBytes);
                        writer.Write(delta);
                    }
                    WriteCount(writer, inventory.Items.Count, MaxHeldItems);
                    foreach (InventoryHeldItem item in inventory.Items)
                    {
                        writer.Write(item.ItemId);
                        writer.Write(item.AmountRaw);
                        writer.Write(item.Gas);
                        writer.Write(index[item.Definition]);
                    }
                }
            }
        }
        if (stream.Length > MaxPayloadBytes)
            throw new InvalidDataException("Client handover state exceeds limit");
        return Convert.ToBase64String(stream.ToArray());
    }

    public static ClientHandoverState Read(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > ((MaxPayloadBytes + 2) / 3) * 4)
            throw new InvalidDataException("Client handover state is absent or exceeds limit");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Malformed client handover Base64", e);
        }
        if (bytes.Length > MaxPayloadBytes)
            throw new InvalidDataException("Client handover state exceeds limit");
        using MemoryStream stream = new MemoryStream(bytes, writable: false);
        using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("Unsupported client handover version");
            ClientHandoverState state = new ClientHandoverState
            {
                StateSyncPacketId = reader.ReadByte(),
                StreamingPacketId = reader.ReadByte(),
                LastReceivedAckId = reader.ReadByte(),
                LastClientStreamingAck = reader.ReadByte(),
                LastProcessedClientPacketId = reader.ReadByte(),
                ClientTimestampMilliseconds = reader.ReadDouble(),
                PriorityMultiplier = reader.ReadSingle(),
            };
            if (reader.ReadBoolean())
                state.PCULimit = reader.ReadInt32();
            state.WantsBatchCompleteConfirmation = reader.ReadBoolean();
            state.ForcePlayoutDelayBuffer = reader.ReadBoolean();
            state.UsePlayoutDelayBufferForCharacter = reader.ReadBoolean();
            state.UsePlayoutDelayBufferForJetpack = reader.ReadBoolean();
            state.UsePlayoutDelayBufferForGrids = reader.ReadBoolean();
            state.ControlledReplicableId = reader.ReadUInt32();
            state.CharacterReplicableId = reader.ReadUInt32();
            state.ControlledEntityId = reader.ReadInt64();
            state.CharacterEntityId = reader.ReadInt64();
            HashSet<uint> ids = new HashSet<uint>();
            for (int i = ReadCount(reader, MaxReplicables); i > 0; i--)
            {
                ClientReplicableState item = new ClientReplicableState
                {
                    NetworkId = reader.ReadUInt32(),
                    IsPending = reader.ReadBoolean(),
                    IsStreaming = reader.ReadBoolean(),
                    Layer = reader.ReadInt32(),
                };
                if (item.NetworkId == 0 || !ids.Add(item.NetworkId))
                    throw new InvalidDataException(
                        "Duplicate or invalid client replicable NetworkId"
                    );
                if (reader.ReadBoolean())
                    item.PermanentLayer = reader.ReadByte();
                state.Replicables.Add(item);
            }
            for (int i = ReadCount(reader, MaxStrings); i > 0; i--)
                state.CachedVoxelNames.Add(ReadString(reader));
            for (int i = ReadCount(reader, MaxStrings); i > 0; i--)
            {
                long planet = reader.ReadInt64();
                if (!state.KnownSectors.TryAdd(planet, new HashSet<long>()))
                    throw new InvalidDataException("Duplicate known-sector planet");
                for (int j = ReadCount(reader, MaxStrings); j > 0; j--)
                    if (!state.KnownSectors[planet].Add(reader.ReadInt64()))
                        throw new InvalidDataException("Duplicate known sector");
            }
            if (stream.Position != stream.Length)
            {
                Dictionary<uint, ClientReplicableState> byId =
                    new Dictionary<uint, ClientReplicableState>();
                foreach (ClientReplicableState item in state.Replicables)
                    byId[item.NetworkId] = item;
                for (int i = ReadCount(reader, MaxReplicables); i > 0; i--)
                {
                    uint networkId = reader.ReadUInt32();
                    if (
                        !byId.TryGetValue(networkId, out ClientReplicableState owner)
                        || owner.InventoryMessageIds.Count != 0
                    )
                        throw new InvalidDataException(
                            "Inventory counters for an unknown or repeated replicable " + networkId
                        );
                    for (int j = ReadCount(reader, MaxInventoryGroups); j > 0; j--)
                        owner.InventoryMessageIds.Add(reader.ReadUInt32());
                }
                if (stream.Position != stream.Length)
                    for (int i = ReadCount(reader, MaxReplicables); i > 0; i--)
                    {
                        uint networkId = reader.ReadUInt32();
                        if (
                            !byId.TryGetValue(networkId, out ClientReplicableState owner)
                            || owner.VoxelEntityId != 0
                        )
                            throw new InvalidDataException(
                                "Carried voxel for an unknown or repeated replicable " + networkId
                            );
                        owner.VoxelEntityId = reader.ReadInt64();
                        owner.VoxelApplied = reader.ReadInt64();
                        if (owner.VoxelEntityId == 0)
                            throw new InvalidDataException(
                                "Carried voxel without an entity id " + networkId
                            );
                        for (int j = ReadCount(reader, MaxVoxelGroups); j > 0; j--)
                            owner.VoxelGroupIds.Add(reader.ReadUInt32());
                    }
                if (stream.Position != stream.Length)
                {
                    int mirrored = reader.ReadInt32();
                    if (mirrored != NoMirror)
                    {
                        if ((uint)mirrored > MaxStrings)
                            throw new InvalidDataException(
                                "Invalid client handover collection count"
                            );
                        state.MirrorHeld = new List<MirrorHolding>();
                        for (int i = mirrored; i > 0; i--)
                        {
                            ulong steamId = reader.ReadUInt64();
                            int serialId = reader.ReadInt32();
                            byte held = reader.ReadByte();
                            state.MirrorHeld.Add(
                                new MirrorHolding(
                                    steamId,
                                    serialId,
                                    (held & 1) != 0,
                                    (held & 2) != 0
                                )
                            );
                        }
                    }
                    if (stream.Position != stream.Length)
                    {
                        string[] definitions = new string[ReadCount(reader, MaxStrings)];
                        for (int i = 0; i < definitions.Length; i++)
                            definitions[i] = ReadString(reader);
                        for (int i = ReadCount(reader, MaxReplicables); i > 0; i--)
                        {
                            uint networkId = reader.ReadUInt32();
                            if (
                                !byId.TryGetValue(networkId, out ClientReplicableState owner)
                                || owner.InventoryHeld.Count != 0
                            )
                                throw new InvalidDataException(
                                    "Held inventory items for an unknown or repeated replicable "
                                        + networkId
                                );
                            for (int j = ReadCount(reader, MaxInventoryGroups); j > 0; j--)
                            {
                                InventoryHeld inventory = new InventoryHeld
                                {
                                    Group = reader.ReadInt32(),
                                };
                                if ((uint)inventory.Group >= MaxInventoryGroups)
                                    throw new InvalidDataException(
                                        "Invalid held inventory group " + inventory.Group
                                    );
                                inventory.ItemsSaid = reader.ReadBoolean();
                                for (int k = ReadCount(reader, MaxUnsentDeltas); k > 0; k--)
                                {
                                    int length = ReadCount(reader, MaxPayloadBytes);
                                    byte[] delta = reader.ReadBytes(length);
                                    if (delta.Length != length)
                                        throw new EndOfStreamException();
                                    inventory.Unsent.Add(delta);
                                }
                                for (int k = ReadCount(reader, MaxHeldItems); k > 0; k--)
                                {
                                    uint itemId = reader.ReadUInt32();
                                    long amount = reader.ReadInt64();
                                    float gas = reader.ReadSingle();
                                    int definition = reader.ReadInt32();
                                    if ((uint)definition >= definitions.Length)
                                        throw new InvalidDataException(
                                            "Invalid held item definition index"
                                        );
                                    inventory.Items.Add(
                                        new InventoryHeldItem(
                                            itemId,
                                            amount,
                                            gas,
                                            definitions[definition]
                                        )
                                    );
                                }
                                owner.InventoryHeld.Add(inventory);
                            }
                        }
                    }
                }
            }
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing bytes in client handover state");
            return state;
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated client handover state", e);
        }
    }

    private static void WriteCount(BinaryWriter writer, int count, int max)
    {
        if ((uint)count > max)
            throw new InvalidDataException("Client handover collection exceeds limit");
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader, int max)
    {
        int count = reader.ReadInt32();
        if ((uint)count > max)
            throw new InvalidDataException("Invalid client handover collection count");
        return count;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            value ?? throw new InvalidDataException("Null cached voxel name")
        );
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Client handover string exceeds limit");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = ReadCount(reader, MaxStringBytes);
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
}
