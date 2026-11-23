using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// SE-1892: a recovery load built the staged import's way. The inline recovery created every entity of the partition
/// with CreateFromObjectBuilderAndAdd on the game thread in one frame: 300-2200 ms for a station partition on the live
/// cluster (2026-11-23), felt by every client on the node. Here the Init runs on the staged import's builders
/// (BuildGate, orchestrator in serial mode, SE-1313 entity events recorded), and the game thread only Adds the dormant
/// entities, whole physical groups of at most recovery.activatePerFrame entities per frame.
/// </summary>
internal sealed partial class ClusterControlServer
{
    internal sealed class RecoveryStage
    {
        internal StagePlan Plan;
        internal Stopwatch Wall;

        /// <summary>Ids the partition adopts so far: present before the load (kept) and Added by a slice.</summary>
        internal readonly List<long> Placed = new List<long>();

        /// <summary>Ids this load Added to the world: what a failure closes.</summary>
        internal readonly HashSet<long> Created = new HashSet<long>();

        internal bool Built =>
            Plan.Workers <= 0 || Volatile.Read(ref Plan.Pending) == 0 || Plan.Done.IsSet;
        internal bool Finished => Plan.Groups != null && Plan.NextGroup >= Plan.Groups.Count;

        /// <summary>Wall time from the start of the builds to the first slice frame.</summary>
        internal double BuildWallMs => Plan.StageWallMs;
    }

    /// <summary>Game thread: classifies the builders (a present entity is kept, as the inline load keeps it) and
    /// starts the off-thread builds of the rest. import.stageWorkers=0 builds each slice on the game thread.</summary>
    internal static RecoveryStage BeginRecoveryStage(IEnumerable<MyObjectBuilder_EntityBase> builders)
    {
        RecoveryStage stage = new RecoveryStage
        {
            Plan = new StagePlan { Workers = ClusterConfig.GetInt("import.stageWorkers") },
            Wall = Stopwatch.StartNew(),
        };
        StageBegin(builders, stage.Plan);
        stage.Placed.AddRange(stage.Plan.Kept.Select(x => x.EntityId));
        return stage;
    }

    /// <summary>
    /// Game thread, once <see cref="RecoveryStage.Built"/>: Adds the next whole physical groups, at most
    /// <paramref name="perFrame"/> entities unless one group alone is bigger. A builder error throws; the caller then
    /// runs <see cref="AbandonRecoveryStage"/> and closes <see cref="RecoveryStage.Created"/>. Returns the count Added.
    /// </summary>
    internal static int ActivateRecoverySlice(RecoveryStage stage, int perFrame)
    {
        StagePlan plan = stage.Plan;
        if (plan.Groups == null)
        {
            ReleaseSerialUpdate(plan);
            plan.StageWallMs = stage.Wall.Elapsed.TotalMilliseconds;
            plan.Groups = ImportActivateSlices.Groups(plan.Staged.Select(x => x.Builder).ToList());
            plan.GroupSizes = plan.Groups.Select(x => x.Count).ToArray();
        }
        int take = ImportActivateSlices.Take(
            plan.GroupSizes,
            plan.NextGroup,
            Math.Max(1, perFrame),
            0,
            0,
            out _
        );
        StagedEntity[] slice = ImportActivateSlices
            .Slice(plan.Groups, plan.NextGroup, take)
            .Select(index => plan.Staged[index])
            .ToArray();
        plan.NextGroup += take;
        bool suspended = VRage.MyEntityIdentifier.AllocationSuspended;
        VRage.MyEntityIdentifier.AllocationSuspended = true;
        int added = 0;
        try
        {
            foreach (StagedEntity slot in slice)
            {
                if (slot.Entity == null && slot.Error == null)
                    StageOne(slot); // import.stageWorkers=0: built here, ids registered directly
                if (slot.Error != null)
                    throw new InvalidDataException(
                        "Recovered entity "
                            + slot.Builder.EntityId
                            + " could not be built: "
                            + slot.Error.Message,
                        slot.Error
                    );
                if (
                    MyEntities.TryGetEntityById(slot.Builder.EntityId, out MyEntity appeared)
                    && appeared != slot.Entity
                )
                {
                    // Appeared while it was built (an import landed it): the live one stays, as the inline load keeps
                    // an entity that is already present.
                    DiscardDormant(slot);
                    stage.Placed.Add(slot.Builder.EntityId);
                    continue;
                }
                slot.Init?.OnEntityInitialized();
                ReplayEntityEvents(slot);
                MyEntity entity = slot.Entity;
                if (entity.EntityId == 0)
                    throw new InvalidDataException(
                        "Recovered entity lost its id: " + slot.Builder.EntityId
                    );
                bool insertIntoScene =
                    (slot.Builder.PersistentFlags & MyPersistentEntityFlags2.InScene) != 0;
                MyEntities.Add(entity, insertIntoScene);
                slot.Entity = null; // owned by the world from here; a failure closes it through Created
                stage.Created.Add(entity.EntityId);
                stage.Placed.Add(entity.EntityId);
                added++;
            }
        }
        finally
        {
            VRage.MyEntityIdentifier.AllocationSuspended = suspended;
        }
        return added;
    }

    /// <summary>Game thread, failure path: serial mode off, and the dormant copies not yet Added deleted (with a
    /// builder still running, left to the GC unregistered, as StageAbandon does).</summary>
    internal static void AbandonRecoveryStage(RecoveryStage stage) => StageAbandonPlan(stage.Plan);
}
