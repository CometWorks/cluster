using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1892: recovery.mode=staged. LoadRecovery reads, screens and compensates in its frame as before, then hands the
/// entities to the staged import's builders instead of creating them all in that frame (300-2200 ms on the live
/// cluster for a station partition). Each later update either waits for the builders (no game-thread work) or Adds
/// one slice of whole physical groups, adopting what is placed so far. The partition becomes this node's, and its
/// Final is captured, in the frame of the last slice, exactly as the inline load ends.
/// </summary>
internal sealed partial class PartitionPersistence
{
    /// <summary>LoadRecovery: the entities are building off the game thread; <see cref="StepStagedRecovery"/> goes on.</summary>
    private const int StagedLoad = -2;

    /// <summary>Staged builds that take longer than this fail the load (the claim is released for a retry).</summary>
    private static readonly TimeSpan StagedBuildTimeout = TimeSpan.FromSeconds(120);

    private sealed class StagedRecovery
    {
        public RecoveryClaim Claim;
        public Dictionary<long, MyObjectBuilder_EntityBase> Entities;
        public int Saved;
        public ClusterControlServer.RecoveryStage Stage;
        public long PrepareMs;
        public string Times;
        public double ActivateMs;
        public double LongestSliceMs;
        public int Slices;
    }

    private StagedRecovery m_stagedRecovery;

    /// <summary>Game thread, once per update while a staged recovery is in flight.</summary>
    private void StepStagedRecovery()
    {
        StagedRecovery staged = m_stagedRecovery;
        RecoveryClaim recovery = staged.Claim;
        try
        {
            // T-0297, as for a queued claim: the heartbeat dropped it while it built.
            if (
                DropLostPending
                && (
                    !m_claimedPending.TryGetValue(recovery.Partition, out uint pendingGeneration)
                    || pendingGeneration != recovery.Generation
                )
            )
            {
                UndoStagedRecovery(staged);
                MyLog.Default?.WriteLineAndConsole(
                    "[ClusterPlugin] recovery skipped: claim "
                        + recovery.Partition
                        + " generation "
                        + recovery.Generation
                        + " was lost while its entities were built (T-0297)"
                );
                return;
            }
            if (!staged.Stage.Built)
            {
                if (staged.Stage.Wall.Elapsed > StagedBuildTimeout)
                    throw new TimeoutException(
                        "Recovery builders did not finish within "
                            + StagedBuildTimeout.TotalSeconds
                            + " s"
                    );
                return;
            }
            Stopwatch slice = Stopwatch.StartNew();
            ClusterControlServer.ActivateRecoverySlice(
                staged.Stage,
                ClusterConfig.GetInt("recovery.activatePerFrame")
            );
            bool last = staged.Stage.Finished;
            if (!last)
                // Pinned between slices like the sliced handover import: the topology sweep must not mint a partition
                // around a grid that is placed but not yet a member.
                m_partitions.Adopt(recovery.Partition, staged.Stage.Placed);
            else
                AdoptRecovered(recovery, staged.Entities.Keys);
            double sliceMs = slice.Elapsed.TotalMilliseconds;
            staged.ActivateMs += sliceMs;
            staged.LongestSliceMs = Math.Max(staged.LongestSliceMs, sliceMs);
            staged.Slices++;
            if (!last)
                return;
            m_stagedRecovery = null;
            m_lastLoadTimes =
                "game thread "
                + (staged.PrepareMs + (long)staged.ActivateMs)
                + " ms: "
                + staged.Times
                + ", create 0; staged: build "
                + (long)staged.Stage.BuildWallMs
                + " ms off the game thread, activate "
                + (long)staged.ActivateMs
                + " ms in "
                + staged.Slices
                + " frames, longest frame "
                + (long)Math.Max(staged.PrepareMs, staged.LongestSliceMs)
                + " ms";
        }
        catch (Exception exception)
        {
            UndoStagedRecovery(staged);
            FailRecovery(recovery, exception);
            return;
        }
        // As the inline load: a failure after the adopt releases the claim but keeps the entities.
        try
        {
            CompleteRecovery(recovery, staged.Saved);
        }
        catch (Exception exception)
        {
            FailRecovery(recovery, exception);
        }
    }

    /// <summary>Game thread: drops a staged recovery that will not finish - its dormant copies, what it Added and
    /// the partition it adopted so far - as the inline load's failure path does.</summary>
    private void UndoStagedRecovery(StagedRecovery staged)
    {
        m_stagedRecovery = null;
        ClusterControlServer.AbandonRecoveryStage(staged.Stage);
        if (staged.Slices > 0 || staged.Stage.Created.Count > 0)
            m_partitions.Remove(staged.Claim.Partition);
        m_state.TryRemove(staged.Claim.Partition, out _);
        // T-0213: hierarchy, not membership - see FinishReconceal.
        CloseWithHierarchy(
            MyEntities.GetEntities().Where(entity => staged.Stage.Created.Contains(entity.EntityId))
        );
    }

    /// <summary>Game thread: the recovered entities are in the world; the partition adopts them and its Final is queued.</summary>
    private void AdoptRecovered(RecoveryClaim recovery, ICollection<long> ids)
    {
        m_partitions.Adopt(recovery.Partition, ids);
        // Recovered content is confirmed ownership: lift any fence taint an assigned save left.
        UntaintEntities(ids);
        m_state[recovery.Partition] = new State { Generation = recovery.Generation };
        // Peek, never Get: Get runs the topology sweep, which is free to split the partition
        // adopted two lines up - mid-load - and did (see PartitionManager.Peek).
        PartitionManager.Partition recovered = m_partitions.Peek(recovery.Partition);
        if (recovered.HasPending)
            // Big grids from the save are still in the parallel init pipeline; a Final taken
            // now would silently omit them. The recovery cover stays claimable until the first
            // periodic capture lands after the pin drains, so nothing is stranded by waiting.
            MyLog.Default?.WriteLineAndConsole(
                "[ClusterPlugin] recovery Final deferred for partition "
                    + recovery.Partition
                    + ": members still initializing"
            );
        else
        {
            // T-0316: the entities are created and adopted; a Final that cannot be captured in the load frame
            // (a vanilla component that serializes only after its first update) must not fail the recovery.
            // Failing it releases the claim, and with a client routed to the partition the registry refuses
            // that release and fences the node (r32 20:27-20:30, three node deaths). The recovery cover stays
            // claimable until the first periodic capture lands, as for the deferred case above.
            try
            {
                QueueCapture(recovered, "Final");
            }
            catch (Exception exception)
            {
                MyLog.Default?.WriteLineAndConsole(
                    "[ClusterPlugin] recovery Final deferred for partition "
                        + recovery.Partition
                        + ": capture failed in the load frame (T-0316): "
                        + SecretRedactor.Redact(exception.ToString())
                );
            }
        }
    }

    /// <summary>Game thread: a recovery's entities are in the world and adopted; the partition is this node's now.</summary>
    private void CompleteRecovery(RecoveryClaim recovery, int saved)
    {
        lock (m_owned)
            m_owned[recovery.Partition] = recovery.Generation;
        m_claimedPending.TryRemove(recovery.Partition, out _);
        NoteRecoveredForRewake(recovery);
        lock (m_recoveryFailureCounts)
            m_recoveryFailureCounts.Remove(recovery.Partition);
        // Peek, twice over: Get() runs the topology sweep (the P1-SAV-07 hazard, see
        // PartitionManager.Peek), and its post-sweep count includes co-located neighbors the
        // sweep just merged in - this line claims what recovery LOADED, so it must report the
        // adopted member set, not the census a merge already rewrote (P1-REC-05b read a 1-entity
        // save and logged "2 members" off the beacon next door).
        PartitionManager.Partition loadedPartition = m_partitions.Peek(recovery.Partition);
        MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] recovery loaded partition "
                + recovery.Partition
                + " generation "
                + recovery.Generation
                + " with "
                + loadedPartition.Members.Count
                + " members ["
                + string.Join(",", loadedPartition.Members)
                + "] ("
                + m_lastLoadTimes
                + ")"
        );
        // T-0388: an empty layout is dropped by the next sweep, so this partition is never reported again; the
        // registry kept its row on this node until the drain's memberless release (321894 on node-282, 01:11 to
        // 03:16 on 2026-10-03). Release it as soon as its empty Final is catalogued instead.
        if (
            MarkEmptyRecovery(
                ClusterConfig.GetBool("recovery.releaseEmpty"),
                saved,
                loadedPartition.Members.Count
            )
        )
        {
            m_emptyRecoveries[recovery.Partition] = (recovery.Generation, DateTime.UtcNow);
            MyLog.Default?.WriteLineAndConsole(
                "[ClusterPlugin] recovery of partition "
                    + recovery.Partition
                    + " generation "
                    + recovery.Generation
                    + " is empty: all "
                    + saved
                    + " saved entities are live elsewhere or stale; it is released"
                    + " unowned once its empty Final is catalogued (T-0388)"
            );
        }
        // T-0254: a recovery load recreates the entities like a handover import, and breaks them like one
        // too (P517 on node-5 05:39:27 2026-09-26: 60 DoDisconnects in the next 180 s).
        ClientLayerRefresh.NoteImport(loadedPartition.Members); // T-0300: offer the new grids to clients now, not at the next 16 s layer refresh
        MySessionComponentTrashPatch.ProtectArrivals(
            loadedPartition.Members,
            MySessionComponentTrashPatch.RecoveryGraceSeconds
        ); // T-0254: the displaced players' way back
    }

    /// <summary>Game thread: a recovery load failed; the claim is released for a retry, or quarantined.</summary>
    private void FailRecovery(RecoveryClaim recovery, Exception exception)
    {
        int failures;
        lock (m_recoveryFailureCounts)
            failures = m_recoveryFailureCounts[recovery.Partition] =
                m_recoveryFailureCounts.GetValueOrDefault(recovery.Partition) + 1;
        int quarantineAfter = ClusterConfig.GetInt("recovery.quarantineAfter");
        if (quarantineAfter > 0 && failures >= quarantineAfter)
        {
            // P2-REC-03: content that keeps failing to load is poison, not a transient - a warm
            // candidate re-added forever would reclaim and fail in a loop with the partition never
            // becoming repairable. SE-1161: the cold path looped the same way (the registry offered
            // the released row straight back: node-1 claimed 3432 71 times in 3 minutes, 2026-11-06).
            // Quarantine it: visible on /cluster/quarantine, repairable in place, and the registry
            // hands it to nobody while this node reports it. The claim is still released.
            string staged = m_assignedRecoveries.TryGetValue(
                recovery.Partition,
                out var assigned
            )
                ? assigned.Path
                : "<recovery claim " + recovery.Partition + ">";
            Quarantine(staged, exception, recovery.Partition);
            lock (m_recoveryFailureCounts)
                m_recoveryFailureCounts.Remove(recovery.Partition);
            m_failedRecoveries.Enqueue(recovery);
            return;
        }
        if (m_warmRecovery)
            m_recoveryCandidates.TryAdd(recovery.Partition, 0);
        m_failedRecoveries.Enqueue(recovery);
        MyLog.Default?.WriteLineAndConsole(
            "[ClusterPlugin] ERROR: recovery load failed for partition "
                + recovery.Partition
                + " generation "
                + recovery.Generation
                + "; releasing claim for retry: "
                + SecretRedactor.Redact(exception.ToString())
        );
    }
}
