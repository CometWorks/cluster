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
}

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
	/// <summary>T-0368: non-zero for an asteroid replicable carried with the client (VoxelCarryPolicy): the voxel's
	/// entity id, the source's journal watermark of its storage, and the network ids of its state groups in the
	/// server's group order, which the target's replicable takes over.</summary>
	public long VoxelEntityId;
	public long VoxelApplied;
	public readonly List<uint> VoxelGroupIds = new List<uint>();
}
