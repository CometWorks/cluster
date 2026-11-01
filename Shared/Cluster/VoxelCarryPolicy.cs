namespace ClusterPlugin;

/// <summary>T-0368: the verdict on one asteroid replicable in a client's view at a handover; only the first two carry it.</summary>
internal enum VoxelCarry
{
	/// <summary>The target's replicable already has the source's network ids.</summary>
	Carry,
	/// <summary>The target's replicable takes the source's network ids, then carries.</summary>
	Remap,
	Planet,
	NotDelivered,
	Stale,
	Unsettled,
	Missing,
	Groups,
	OwnEdits,
	Newer,
	IdTaken,
	Viewed
}

/// <summary>
/// T-0368: when a modified asteroid the client already holds travels with the client across a handover instead
/// of being destroyed on the source and streamed again by the target. Two things have to hold. The client's copy
/// must not be OLDER than the target's storage: nothing tells a client about a journal write, so a client that
/// keeps its copy would never see what it lacks. And the target must be able to address the replica by the
/// network ids the client knows: ids are minted per node, so the target's replicable has to take the source's.
/// </summary>
internal static class VoxelCarryPolicy
{
	/// <summary>
	/// Source side. <paramref name="stale"/>: a journal entry was written into the storage while the client held
	/// the replicable (the client was not told). <paramref name="unsettled"/>: the source's own journal watermark
	/// overstates its storage (entries skipped or being replayed, T-0364).
	/// </summary>
	public static VoxelCarry Source(bool planet, bool delivered, bool stale, bool unsettled) =>
		planet ? VoxelCarry.Planet
		: !delivered ? VoxelCarry.NotDelivered
		: stale ? VoxelCarry.Stale
		: unsettled ? VoxelCarry.Unsettled
		: VoxelCarry.Carry;

	/// <summary>
	/// Target side. The client holds the source's storage as of <paramref name="sourceApplied"/>; the target
	/// holds no more than that when it has applied no later journal entry and has no edit of its own that is
	/// not in the journal yet. <paramref name="viewed"/>: another client of the target holds the replicable
	/// under the target's ids, which therefore cannot change.
	/// </summary>
	public static VoxelCarry Target(bool present, bool groupsMatch, bool unsettled, bool ownEdits, long sourceApplied, long targetApplied,
		bool sameIds, bool idsFree, bool viewed) =>
		!present ? VoxelCarry.Missing
		: !groupsMatch ? VoxelCarry.Groups
		: unsettled ? VoxelCarry.Unsettled
		: ownEdits ? VoxelCarry.OwnEdits
		: targetApplied > sourceApplied ? VoxelCarry.Newer
		: sameIds ? VoxelCarry.Carry
		: !idsFree ? VoxelCarry.IdTaken
		: viewed ? VoxelCarry.Viewed
		: VoxelCarry.Remap;

	public static bool Carries(VoxelCarry verdict) => verdict is VoxelCarry.Carry or VoxelCarry.Remap;

	/// <summary>
	/// Target side, T-0368 (r42 live, Viktor 2026-10-02 19:35:28): what to do with a carried asteroid the target
	/// cannot adopt yet. A stock procedural asteroid exists on a node only once something there has generated
	/// it, and is replicated only once the journal has written its edits into it - on a target that had nothing
	/// near, both happen AFTER the import, and the immediate fallback destroyed the client's copy ~1 s before
	/// the target could have carried it. Missing and Unsettled are what the journal resolves by itself, and so
	/// is a target still behind the client's copy (adopted now, the entries still to come would mark the
	/// client's stream stale and the next handover would prune it). Those wait while the client keeps the
	/// source's copy; everything else, and anything still waiting at <paramref name="expired"/>, falls back.
	/// </summary>
	public static bool Waits(VoxelCarry verdict, long sourceApplied, long targetApplied, bool expired) =>
		!expired && (verdict is VoxelCarry.Missing or VoxelCarry.Unsettled || (Carries(verdict) && targetApplied < sourceApplied));
}
