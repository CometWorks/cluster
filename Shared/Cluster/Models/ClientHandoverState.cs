using System.Collections.Generic;

namespace ClusterPlugin;

public sealed class ClientHandoverState
{
	public byte StateSyncPacketId;
	public byte StreamingPacketId;
	public byte LastReceivedAckId;
	public byte LastClientStreamingAck;
	public byte LastProcessedClientPacketId;
	public double ClientTimestampMilliseconds;
	public float PriorityMultiplier;
	public int? PCULimit;
	public bool WantsBatchCompleteConfirmation;
	public bool ForcePlayoutDelayBuffer;
	public bool UsePlayoutDelayBufferForCharacter;
	public bool UsePlayoutDelayBufferForJetpack;
	public bool UsePlayoutDelayBufferForGrids;
	public uint ControlledReplicableId;
	public uint CharacterReplicableId;
	public long ControlledEntityId;
	public long CharacterEntityId;
	public readonly List<ClientReplicableState> Replicables = new List<ClientReplicableState>();
	public readonly List<string> CachedVoxelNames = new List<string>();
	public readonly Dictionary<long, HashSet<long>> KnownSectors = new Dictionary<long, HashSet<long>>();
	/// <summary>SE-1192: the cluster players the source node knows this client already has a member entry or a
	/// MyPlayer for, so the target's player mirror sends it exactly the rest. Null when the source did not say;
	/// the target then treats the client as holding nothing it can vouch for.</summary>
	public List<MirrorHolding> MirrorHeld;
}

/// <summary>SE-1192: one cluster player as a client holds it: its member entry, its MyPlayer, or both.</summary>
public readonly record struct MirrorHolding(ulong SteamId, int SerialId, bool Member, bool Player);

public sealed class ClientReplicableState
{
	public uint NetworkId;
	public bool IsPending;
	public bool IsStreaming;
	public int Layer = -1;
	public byte? PermanentLayer;
	/// <summary>T-0265: this client's CurrentMessageId in each MyEntityInventoryStateGroup of the replicable, in
	/// the server's group order; empty for a replicable without inventory groups.</summary>
	public readonly List<uint> InventoryMessageIds = new List<uint>();
	/// <summary>SE-1554: per inventory group of the replicable whose changes had not all reached this client: the
	/// items the source knows the client has, and the deltas it sent without an acknowledgement. Without them
	/// the target takes its own items for the client's and never sends the difference.</summary>
	public readonly List<InventoryHeld> InventoryHeld = new List<InventoryHeld>();
	/// <summary>T-0368: non-zero for an asteroid replicable carried with the client (VoxelCarryPolicy): the voxel's
	/// entity id, the source's journal watermark of its storage, and the network ids of its state groups in the
	/// server's group order, which the target's replicable takes over.</summary>
	public long VoxelEntityId;
	public long VoxelApplied;
	public readonly List<uint> VoxelGroupIds = new List<uint>();
}

/// <summary>SE-1554: what a client holds of one inventory, by the index of its MyEntityInventoryStateGroup among
/// the replicable's inventory groups (the order of InventoryMessageIds).</summary>
public sealed class InventoryHeld
{
	public int Group;
	/// <summary>True when Items says what the client has (the source had a change waiting); false when the
	/// client is up to date but for the Unsent deltas, so the target's own items are the client's.</summary>
	public bool ItemsSaid;
	/// <summary>The client's items, in the client's order, as the source's deltas left them (Unsent included).</summary>
	public readonly List<InventoryHeldItem> Items = new List<InventoryHeldItem>();
	/// <summary>The deltas the source sent or was about to send again and has no acknowledgement for
	/// (InventoryDeltaWire), by ascending message id. A client drops a delta whose id it has already taken, so
	/// sending them again is safe; one that never arrives stops every later delta of that inventory.</summary>
	public readonly List<byte[]> Unsent = new List<byte[]>();
}

/// <summary>SE-1554: one item as a client holds it. Definition is "TypeId/Subtype".</summary>
public readonly record struct InventoryHeldItem(uint ItemId, long AmountRaw, float Gas, string Definition);
