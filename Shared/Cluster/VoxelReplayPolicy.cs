namespace ClusterPlugin;

/// <summary>T-0364: when a node has to replay a storage's voxel journal from the World Authority.</summary>
internal static class VoxelReplayPolicy
{
	/// <summary>
	/// A replay is due when this node skipped entries for the storage while it did not hold it, or when the
	/// watermark was applied to another voxel instance (<paramref name="sameInstance"/> false: the storage was
	/// regenerated, so its content is the pristine shape again). Null: no watermark was applied to any instance.
	/// </summary>
	public static bool NeedsReplay(bool skipped, bool? sameInstance) => skipped || sameInstance == false;

	/// <summary>A catalogued base already contains every entry up to its tail; without one the replay starts at the first entry.</summary>
	public static long ReplayFrom(long? baseTail) => baseTail ?? 0;
}
