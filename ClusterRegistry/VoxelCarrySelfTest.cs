using ClusterPlugin;

namespace ClusterRegistry;

/// <summary>T-0368 (Shared/Cluster/VoxelCarryPolicy.cs): the node's rule for carrying a modified asteroid across a
/// handover, run here because the plugin's own self-test sections need a live node. Earned red: the rule before the
/// fix is "never" - every asteroid replica was destroyed on the source and streamed again by the target.</summary>
internal static partial class SelfTest
{
    private static void TestVoxelCarryPolicy()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("voxel-carry-policy: " + what); }
        static VoxelCarry Target(bool present = true, bool groupsMatch = true, bool unsettled = false, bool ownEdits = false, long source = 7,
            long target = 7, bool sameIds = false, bool idsFree = true, bool viewed = false) =>
            VoxelCarryPolicy.Target(present, groupsMatch, unsettled, ownEdits, source, target, sameIds, idsFree, viewed);

        Check(VoxelCarryPolicy.Source(planet: false, delivered: true, stale: false, unsettled: false) == VoxelCarry.Carry, "a delivered, current asteroid replica is exported");
        Check(VoxelCarryPolicy.Source(true, true, false, false) == VoxelCarry.Planet, "planets stay as they were");
        Check(VoxelCarryPolicy.Source(false, false, false, false) == VoxelCarry.NotDelivered, "a replica still streaming is not carried");
        Check(VoxelCarryPolicy.Source(false, true, true, false) == VoxelCarry.Stale, "a replica a journal write passed by is not carried");
        Check(VoxelCarryPolicy.Source(false, true, false, true) == VoxelCarry.Unsettled, "a source whose watermark overstates its storage does not carry");

        Check(Target() == VoxelCarry.Remap, "an unviewed target replicable takes the source's ids");
        Check(Target(sameIds: true, viewed: true, idsFree: false) == VoxelCarry.Carry, "ids that already agree carry, whoever else views it");
        Check(Target(target: 6) == VoxelCarry.Remap, "a target behind the source catches up from the journal");
        Check(Target(present: false) == VoxelCarry.Missing, "a voxel the target does not replicate falls back");
        Check(Target(groupsMatch: false) == VoxelCarry.Groups, "state groups that do not line up fall back");
        Check(Target(unsettled: true) == VoxelCarry.Unsettled, "a target replaying the storage falls back");
        Check(Target(ownEdits: true, sameIds: true) == VoxelCarry.OwnEdits, "a target edit the journal has not sequenced falls back");
        Check(Target(target: 8, sameIds: true) == VoxelCarry.Newer, "a target ahead of the client's copy falls back");
        Check(Target(viewed: true) == VoxelCarry.Viewed, "ids another client of the target knows are not changed");
        Check(Target(idsFree: false) == VoxelCarry.IdTaken, "an id held by another object is not taken");
        Check(Enum.GetValues<VoxelCarry>().Count(VoxelCarryPolicy.Carries) == 2, "every verdict but Carry and Remap is a fallback");
        // r42 live 19:35:28: the target had not generated the asteroid yet (reason=Missing) and destroyed the client's copy
        // ~1 s before it would have carried it. Earned red: the rule before was "never wait".
        Check(VoxelCarryPolicy.Waits(VoxelCarry.Missing, 15, 0, expired: false), "a voxel the target has not generated yet is waited for");
        Check(VoxelCarryPolicy.Waits(VoxelCarry.Unsettled, 15, 3, expired: false), "a target replaying the storage is waited for");
        Check(VoxelCarryPolicy.Waits(VoxelCarry.Remap, 15, 3, expired: false), "a target still behind the client's copy is waited for");
        Check(!VoxelCarryPolicy.Waits(VoxelCarry.Remap, 15, 15, expired: false), "a caught-up target carries at once");
        Check(!VoxelCarryPolicy.Waits(VoxelCarry.Carry, 15, 15, expired: false), "ids that agree on a caught-up target carry at once");
        Check(!VoxelCarryPolicy.Waits(VoxelCarry.Missing, 15, 0, expired: true), "the wait ends at the deadline");
        Check(!VoxelCarryPolicy.Waits(VoxelCarry.Remap, 15, 3, expired: true), "a target behind at the deadline carries as before");
        foreach (VoxelCarry verdict in new[] { VoxelCarry.Groups, VoxelCarry.OwnEdits, VoxelCarry.Newer, VoxelCarry.IdTaken, VoxelCarry.Viewed })
            Check(!VoxelCarryPolicy.Waits(verdict, 15, 15, expired: false), verdict + " falls back without waiting");
        Console.WriteLine("voxel-carry-policy: an asteroid replica is carried only when the client's copy is current and the target can address it; every other case falls back to destroy and re-add; a target still generating or catching up is waited for");
    }
}
