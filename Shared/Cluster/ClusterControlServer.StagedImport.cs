using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// Staged import (HandoverImprovements.md §5, the part of "pre-sync" that moves game-thread cost;
/// `import.mode=staged`). The handover protocol, payload, tee and round-trip gate are unchanged;
/// only WHERE the target spends the import changes:
///
///   handler thread   parse + deserialise the payload                (was: import_deserialize on the game thread)
///   game thread      import_begin: idempotency, classify, kick workers                 (~0 ms)
///   worker threads   CreateFromObjectBuilderNoinit + Init per entity, dormant          (was: import_create)
///   game thread      import_activate: register ids, Add, freeze, reseat, adopt, collect builders
///                    (import.activatePerFrame=N>0, T-0392: import_activate_slice frames of at most N
///                    entities in whole physical groups, then import_activate_final; see ActivateSlice)
///   handler thread   serialise + compare the round trip, dump on residual               (was: most of validate)
///   game thread      import_finish: network ids, membership, transfer record
///
/// Why this shape and not "stage a copy on the target before the freeze" (§5.3 presync.stage): the
/// per-entity drift between two saves 60 s apart on w22 is BLOCK COMPONENT STATE - MyTimerComponent
/// FramesFromLastTrigger/TimerTickInFrames, tank FilledRatio, presence tiers - so in the partitions
/// that cost anything (5-46 entities) only 1 of 16, 1 of 6, 2 of 7 builders were identical apart
/// from pose and velocity (findings/presync-staged-copies-cannot-be-reused.md). A staged copy that
/// differs in a timer phase cannot be patched and must be rebuilt, which is exactly what this does
/// off the game thread AT the freeze.
///
/// Worker-side Init follows MyEntities.Load / CreateFromObjectBuilderParallel exactly (per-thread
/// id storage, InEntityCreationBlock, ids merged on the game thread by InitEntityData.OnEntityInitialized)
/// with one addition: AllocationSuspended on the worker, as the legacy import sets it on the game
/// thread. The update orchestrator spin-waits for in-flight init workers before EVERY parallel
/// update (SE-31669, MyEntities.DrainOutstandingEntityInitWork), which would turn the off-thread
/// build straight back into a game-thread stall; for the staging window the orchestrator is put in
/// serial mode (MyParallelEntityUpdateOrchestrator.ForceSerialUpdate), which is the regime vanilla's
/// own paste/missile/floating-object spawns run in (worker init concurrent with the serial frame),
/// and restored in the activate frame - or in the failure path, always.
///
/// A dormant entity (initialised, never Added) is not in MyEntities, not replicated, not updated,
/// not saved, not a partition member and not in the census; its ids are registered only when it is
/// activated. Discard = Delete() on the game thread.
/// </summary>
internal sealed partial class ClusterControlServer
{
	private sealed class StagedEntity
	{
		public MyObjectBuilder_EntityBase Builder;
		public MyEntity Entity;
		public MyEntities.InitEntityData Init;
		public Exception Error;
		public double InitMs;
	}

	private sealed class StagePlan
	{
		public List<StagedEntity> Staged = new List<StagedEntity>();
		public List<MyObjectBuilder_EntityBase> Kept = new List<MyObjectBuilder_EntityBase>();
		public int Workers;
		public int Pending;
		public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
		public bool SerialForced;
		public bool Activated;
		public double StageWallMs;
		/// <summary>import.activatePerFrame &gt; 0: the physical groups (index lists into Staged), grouped in the
		/// first slice frame; each slice frame takes the next whole groups (ImportActivateSlices.Take).</summary>
		public List<List<int>> Groups;
		public int[] GroupSizes;
		public int NextGroup;
		/// <summary>import.activateBudgetMs: the previous slice's measured Add ms per entity (0 = none yet).</summary>
		public double SliceMsPerEntity;
		/// <summary>Slices that were one group whose estimate alone exceeded import.activateBudgetMs.</summary>
		public int OverBudgetSlices;
		/// <summary>Kept + activated ids so far: what a slice adopts into the partition between frames.</summary>
		public List<long> Placed;
		/// <summary>T-0401: this import's 1-based index among the staged imports this process began activating (0 = not yet).</summary>
		public int ImportIndex;
	}

	/// <summary>
	/// Staged imports in flight that hold the orchestrator in serial mode. Two targets staging at
	/// once must not let the first to finish switch the parallel update back on under the second's
	/// workers - the SE-31669 race those workers are not counted against.
	/// </summary>
	private static int m_serialForcers;

	private static void ForceSerialUpdate(StagePlan plan)
	{
		if (plan.SerialForced) return;
		plan.SerialForced = true;
		if (++m_serialForcers == 1) MyParallelEntityUpdateOrchestrator.ForceSerialUpdate = true;
	}

	private static void ReleaseSerialUpdate(StagePlan plan)
	{
		if (!plan.SerialForced) return;
		plan.SerialForced = false;
		if (--m_serialForcers == 0) MyParallelEntityUpdateOrchestrator.ForceSerialUpdate = false;
	}

	private sealed class ImportComparison
	{
		/// <summary>The source entries the gate compares, for their ids and order: the entities created from
		/// them may have changed their contents (#1259), so the compared builders come from the body.</summary>
		public MyObjectBuilder_EntityBase[] GatedSource;
		public MyObjectBuilder_EntityBase[] Imported;
		public byte[] SourceBody;
	}

	/// <summary>Handler-thread driver of the staged import; see the class comment.</summary>
	private static int ImportStaged(byte[] body, ulong partitionId, Guid? transferId, uint? generation)
	{
		Stopwatch wall = Stopwatch.StartNew();
		ImportContext context = ImportParse(body, partitionId, generation);
		double deserializeMs = wall.Elapsed.TotalMilliseconds;
		// The source half of the round-trip document, overlapped with the staging: the byte fast path
		// rarely holds (a moving entity's pose differs inside tolerance, and the target's own
		// derived fields differ), so the XML comparison runs on most imports.
		// #1259: from its own copy of the builders, deserialised from the transfer body. The staging
		// creates the entities from context.Entities, and an entity may keep and mutate its builder
		// (MyProjectorBase remaps its blueprint in place), so those are not the frozen source any more,
		// and serialising them here would race the game thread.
		System.Threading.Tasks.Task<ImportRoundTrip.PreparedSource> preparedSource = transferId.HasValue
			? System.Threading.Tasks.Task.Run(() => ImportRoundTrip.PrepareSource(FrozenSourceCopy(context.Save.Body, context.Save.EntityCount)))
			: null;
		int workers = ClusterConfig.GetInt("import.stageWorkers");
		StagePlan plan = new StagePlan { Workers = workers };
		bool begun = InvokePhase("import_begin", transferId, partitionId, () =>
		{
			if (!ImportBegin(context, transferId)) return false;
			ResolveGhostSeats(context);
			try { StageBegin(context, plan); }
			catch { ImportFail(context); throw; }
			return true;
		});
		if (!begun) return context.Transfer.ImportCount;
		Stopwatch stage = Stopwatch.StartNew();
		ImportComparison comparison = null;
		double compareMs = 0;
		// T-0392: read once per import, so a PUT mid-import cannot change the frame plan under it.
		int activatePerFrame = ClusterConfig.GetInt("import.activatePerFrame");
		// T-0392 cost budget: read at the same point, for the same reason; 0 = the count cap alone.
		int activateBudgetMs = Math.Max(0, ClusterConfig.GetInt("import.activateBudgetMs"));
		// T-0392 per-second pacing: read at the same point too; 0 = no frame ever waits (ImportActivatePacer).
		// Every sliced import holds a ticket, whatever its N: registration order is the FIFO order, and the
		// ledger records its frames so the window is true when a PUT turns pacing on.
		int activateMsPerSecond = Math.Max(0, ClusterConfig.GetInt("import.activateMsPerSecond"));
		ImportActivatePacer.Ticket pace = activatePerFrame > 0 ? ImportActivatePacer.Node.Register() : null;
		int activateSlices = 1;
		try
		{
			if (plan.Workers > 0 && plan.Pending > 0 && !plan.Done.Wait(TimeSpan.FromSeconds(120)))
				throw new TimeoutException("Staged import workers did not finish within 120 s (" + plan.Pending + " of " + plan.Staged.Count + " pending)");
			plan.StageWallMs = stage.Elapsed.TotalMilliseconds;
			if (activatePerFrame <= 0)
			{
				comparison = InvokePhase("import_activate", transferId, partitionId, () =>
				{
					try { return ImportActivate(context, plan); }
					catch { ImportFail(context); throw; }
				});
			}
			else
			{
				// T-0392: N entities per frame (whole physical groups), then one final frame; see
				// ActivateSlice. Every frame that throws runs ImportFail itself; the catch below
				// still runs StageAbandon (serial mode, dormant copies), exactly as for one frame.
				// ImportActivationGuard: from before the first slice to the end of the final frame the
				// world-writing periodic services skip this import's entities, so nothing changes
				// what the final frame collects for the round trip (antennas switched off between
				// slices: 00:22:56 2026-10-05, residuals=7). Released on every way out.
				// T-0392 import.activateMsPerSecond: each frame first asks the pacer; a frame it holds back
				// is a bare game-thread call (no phase line), retried the next frame (MySandboxGame swaps its
				// invoke queue per frame, so a re-Invoke lands one frame later).
				comparison = ImportActivationGuard.RunSliced(context.Entities.Select(x => x.EntityId),
					() =>
					{
						while (!InvokePacedPhase("import_activate_slice", transferId, partitionId, context, pace, activateMsPerSecond, activateBudgetMs, decision =>
						{
							try { return ActivateSlice(context, plan, activatePerFrame, decision); }
							catch { ImportFail(context); throw; }
						}, out _)) { }
						return plan.NextGroup < plan.Groups.Count;
					},
					() =>
					{
						ImportComparison final;
						while (!InvokePacedPhase("import_activate_final", transferId, partitionId, context, pace, activateMsPerSecond, activateBudgetMs, decision =>
						{
							try { return ActivateSlicedFinal(context, plan, decision); }
							catch { ImportFail(context); throw; }
						}, out final)) { }
						return final;
					},
					out activateSlices);
			}
			Stopwatch compare = Stopwatch.StartNew();
			if (comparison != null)
			{
				ImportRoundTrip.PreparedSource prepared = null;
				if (preparedSource != null)
				{
					try { prepared = preparedSource.Result; }
					catch (Exception e) { Log("prepared source document failed, comparing the slow way: " + e.Message); }
				}
				try { CompareImportedState(context.Transfer, comparison.GatedSource, context.Entities.Length, comparison.Imported, comparison.SourceBody, prepared); }
				catch { Invoke(() => { ImportFail(context); return 0; }); throw; }
			}
			compareMs = compare.Elapsed.TotalMilliseconds;
			int count = InvokePhase("import_finish", transferId, partitionId, () =>
			{
				try { return ImportFinish(context); }
				catch { ImportFail(context); throw; }
			});
			Log("handover_import transferId=" + (transferId?.ToString("D") ?? "-") + " partition=" + partitionId
				+ " mode=staged workers=" + plan.Workers + " staged=" + plan.Staged.Count + " kept=" + plan.Kept.Count
				+ " activatePerFrame=" + Math.Max(0, activatePerFrame) + " activateSlices=" + activateSlices
				+ (activatePerFrame > 0 ? " activateBudgetMs=" + activateBudgetMs + " activateOverBudgetSlices=" + plan.OverBudgetSlices : "")
				+ (pace != null ? " activateMsPerSecond=" + activateMsPerSecond + " pacedFramesTotal=" + pace.PacedFramesTotal + " forcedFrames=" + pace.ForcedFrames
					+ " activateSpanMs=" + pace.ActivateSpanMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "")
				+ " deserializeMs=" + deserializeMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " stageWallMs=" + plan.StageWallMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " stageInitMsMax=" + (plan.Staged.Count == 0 ? 0 : plan.Staged.Max(x => x.InitMs)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " stageInitMsSum=" + plan.Staged.Sum(x => x.InitMs).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " compareMs=" + compareMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
				+ " totalWallMs=" + wall.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
			return count;
		}
		catch
		{
			// Whatever failed, the orchestrator must come back out of serial mode and dormant copies
			// must not leak; a phase that threw has already run ImportFail on the game thread, a
			// phase that could not be invoked (game-thread timeout) has not.
			try { Invoke(() => { StageAbandon(context, plan); return 0; }); }
			catch (Exception e) { Log("staged import cleanup failed transferId=" + transferId + " partition=" + partitionId + ": " + e.Message); }
			throw;
		}
		finally
		{
			ImportActivatePacer.Node.Unregister(pace, ImportActivatePacer.NowMs);
		}
	}

	/// <summary>
	/// T-0392: one game-thread call that asks <see cref="ImportActivatePacer.Node"/> first. Held back:
	/// returns false having done nothing (no handover_phase line, the frame is counted on the ticket).
	/// Admitted: runs <paramref name="action"/> under a phase timer as InvokePhase does and enters the
	/// frame's measured game-thread ms into the ledger, also when the frame throws (the cost was paid).
	/// No ticket (unsliced) = always admitted.
	/// </summary>
	private static bool InvokePacedPhase<T>(string phase, Guid? transferId, ulong partition, ImportContext context, ImportActivatePacer.Ticket ticket,
		int perSecondMs, int frameBudgetMs, Func<ImportActivatePacer.Decision, T> action, out T result)
	{
		(bool ran, T value) = Invoke(() =>
		{
			bool urgent = context.Failed || context.Transfer?.Discarded == true;
			ImportActivatePacer.Decision decision = ImportActivatePacer.Node.Decide(ticket, perSecondMs, frameBudgetMs, urgent, ImportActivatePacer.NowMs);
			if (!decision.Go) return (false, default(T));
			using HandoverPhaseTimer timer = HandoverPhaseTimer.Phase(phase, transferId, partition);
			try { return (true, action(decision)); }
			catch (Exception e) { timer.Error = (e is InvalidOperationException && e.InnerException != null ? e.InnerException : e).GetType().Name; throw; }
			finally { ImportActivatePacer.Node.Spend(ticket, timer.ElapsedMs, ImportActivatePacer.NowMs); }
		});
		result = value;
		return ran;
	}

	/// <summary>T-0392: the pacing fields of an activation frame's handover_phase line.</summary>
	private static string PaceFields(ImportActivatePacer.Decision decision) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
		" paceMsPerSecond={0} spentLast1sMs={1:F1} pacedFrames={2} forced={3}",
		decision.PerSecondMs, decision.SpentMs, decision.PacedFrames, decision.Forced ? 1 : 0);

	/// <summary>
	/// Game thread. Splits the payload into entities that already exist on this node (handled in the
	/// activate frame exactly as the legacy import does) and entities to build, and starts the
	/// workers. Classification and creation are separated by frames, so the activate frame re-checks
	/// for an entity that appeared in between and discards its dormant copy.
	/// </summary>
	private static void StageBegin(ImportContext context, StagePlan plan) => StageBegin(context.Entities, plan);

	/// <summary>
	/// Builds one dormant entity on the calling thread. On a worker it mirrors MyEntities.LoadEntity:
	/// per-thread id storage, so the ids are merged into the main table by OnEntityInitialized on the
	/// game thread. On the game thread it is the legacy CreateFromObjectBuilder (ids registered
	/// directly) - InitEntityData.CallInitEntity must NEVER run on the main thread: its finally
	/// copies and CLEARS the calling thread's id table, which on the main thread is the world's.
	/// AllocationSuspended as the legacy import sets it.
	/// </summary>
	private static void StageOne(StagedEntity slot)
	{
		Stopwatch watch = Stopwatch.StartNew();
		bool mainThread = VRage.Utils.MyUtils.MainThread == Thread.CurrentThread;
		if (!mainThread) VRage.MyEntityIdentifier.LazyInitPerThreadStorage(2048);
		bool suspended = VRage.MyEntityIdentifier.AllocationSuspended;
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			if (mainThread)
			{
				MyEntity entity = MyEntities.CreateFromObjectBuilder(slot.Builder, fadeIn: false);
				if (entity == null || entity.EntityId == 0)
					throw new InvalidDataException("Entity init failed for " + slot.Builder.GetType().Name + " entity " + slot.Builder.EntityId + " (see ERROR Entity init in the game log)");
				slot.Entity = entity;
				slot.Init = null;
				return;
			}
			MyEntity created = MyEntities.CreateFromObjectBuilderNoinit(slot.Builder);
			if (created == null) throw new InvalidDataException("No entity type for object builder " + slot.Builder.GetType().Name + " entity " + slot.Builder.EntityId);
			MyEntities.InitEntityData init = new MyEntities.InitEntityData(slot.Builder, addToScene: false, null, created, fadeIn: false);
			(bool success, MyEntity initialised) = init.CallInitEntity();
			if (!success || initialised == null || initialised.EntityId == 0)
				throw new InvalidDataException("Entity init failed for " + slot.Builder.GetType().Name + " entity " + slot.Builder.EntityId + " (see ERROR Entity init in the game log)");
			slot.Entity = initialised;
			slot.Init = init;
		}
		catch (Exception e)
		{
			slot.Error = e;
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = suspended;
			slot.InitMs = watch.Elapsed.TotalMilliseconds;
		}
	}

	/// <summary>
	/// Game thread: the legacy import_create frame minus the entity construction. Registers the
	/// workers' ids, Adds the dormant entities, runs the kept-entity rules, freezes and reseats,
	/// adopts the ids into the partition (the topology sweep must not mint a partition around them
	/// before import_finish) and collects the round-trip builders. Null = nothing to compare
	/// (no transfer, i.e. a plain import). import.activatePerFrame=0: the whole of it in one frame;
	/// N&gt;0 runs the same pieces over ActivateSlice frames and ActivateSlicedFinal.
	/// </summary>
	private static ImportComparison ImportActivate(ImportContext context, StagePlan plan)
	{
		HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
		HashSet<long> liveControlled = LiveControlledEntityIds();
		bool allocationSuspended = VRage.MyEntityIdentifier.AllocationSuspended;
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			if (plan.Workers <= 0)
			{
				timer?.Step("import_create");
				foreach (StagedEntity slot in plan.Staged) StageOne(slot);
				plan.StageWallMs = plan.Staged.Sum(x => x.InitMs);
			}
			plan.ImportIndex = NodeAgeCounter.Imports.Next();
			ActivateKept(context, plan, liveControlled, buildLate: true);
			timer?.Step("import_activate");
			List<MyEntity> activated = new List<MyEntity>(plan.Staged.Count);
			ImportAddSlice addSlice = ActivateEntities(context, plan.Staged, liveControlled, activated);
			if (timer != null && addSlice != null) timer.Fields += addSlice.Fields() + NodeAgeCounter.Imports.Fields(plan.ImportIndex);
			return ActivateFinish(context, plan, activated);
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = allocationSuspended;
			ReleaseSerialUpdate(plan);
		}
	}

	/// <summary>
	/// Game thread: the kept-entity rules for the builders classified as present in import_begin.
	/// One that is gone now, or a fenced duplicate ImportKeepExisting purged, needs the arriving copy
	/// built after all: on the game thread at once (buildLate, the one-frame activate), or left
	/// unbuilt for its slice frame to build (sliced: a game-thread-built entity has its ids in the
	/// world's table, so it must not sit dormant across a frame).
	/// </summary>
	private static void ActivateKept(ImportContext context, StagePlan plan, HashSet<long> liveControlled, bool buildLate)
	{
		HandoverPhaseTimer.Current?.Step("import_activate_kept");
		foreach (MyObjectBuilder_EntityBase builder in plan.Kept)
		{
			// Classified as present in import_begin but gone now, or a fenced duplicate that
			// ImportKeepExisting purged: build the arriving copy here, on the game thread, as the
			// legacy path would.
			if (!MyEntities.TryGetEntityById(builder.EntityId, out MyEntity existing)
				|| !ImportKeepExisting(context, builder, existing, liveControlled))
			{
				StagedEntity late = new StagedEntity { Builder = builder };
				if (buildLate) StageOne(late);
				plan.Staged.Add(late);
			}
		}
	}

	/// <summary>
	/// Game thread: registers ids, Adds, freezes and reseats the given staged entities in order.
	/// <paramref name="activated"/> (optional) receives each entity Added. Returns the T-0401 MyEntities.Add
	/// breakdown of these entities (diag.importAddTimers), null when that knob is off.
	/// </summary>
	private static ImportAddSlice ActivateEntities(ImportContext context, IEnumerable<StagedEntity> slots, HashSet<long> liveControlled, List<MyEntity> activated)
	{
		ulong partitionId = context.PartitionId;
		Transfer transfer = context.Transfer;
		HashSet<long> created = context.Created;
		// T-0392: the caller's import_activate step split per sub-step (Step accumulates by name), so the
		// handover_phase line says which part of the per-entity activation dominates; the remainder
		// (the appeared-before-activation check) stays under import_activate.
		HandoverPhaseTimer steps = HandoverPhaseTimer.Current;
		ImportAddSlice addSlice = ClusterConfig.GetBool("diag.importAddTimers") ? new ImportAddSlice() : null;
		int count = 0;
		foreach (StagedEntity slot in slots)
		{
			steps?.Step("import_activate");
			if (slot.Error != null) throw new InvalidDataException("Staged entity " + slot.Builder.EntityId + " could not be built: " + slot.Error.Message, slot.Error);
			if (MyEntities.TryGetEntityById(slot.Builder.EntityId, out MyEntity appeared) && appeared != slot.Entity)
			{
				// Appeared between classification and activation (a recovery or another
				// import landed it): the dormant copy loses, the kept-entity rules apply.
				Log("staged entity " + slot.Builder.EntityId + " appeared before activation partition=" + partitionId + "; discarding the dormant copy");
				DiscardDormant(slot);
				if (ImportKeepExisting(context, slot.Builder, appeared, liveControlled)) continue;
				StageOne(slot); // purged a fenced duplicate: build the arriving copy on the game thread
				if (slot.Error != null) throw new InvalidDataException("Staged entity " + slot.Builder.EntityId + " could not be rebuilt: " + slot.Error.Message, slot.Error);
			}
			// Registers the ids the worker collected (the entity and its blocks) in the main
			// table; addToScene is false so this neither Adds nor calls back.
			steps?.Step("activate_init");
			slot.Init?.OnEntityInitialized();
			MyEntity entity = slot.Entity;
			if (entity.EntityId == 0) throw new InvalidDataException("Staged entity lost its id: " + slot.Builder.EntityId);
			bool insertIntoScene = (slot.Builder.PersistentFlags & VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene) != 0;
			steps?.Step("activate_add");
			// T-0401 fix (import.deferPhysics): the freeze below disables the entity's physics right after the Add, so
			// a grid without a seated pilot is Added with physics (its children's bodies too) already off - the Add's activation then touches no
			// cluster (no HkWorld created and disposed, no neighbours' clusters reordered for nothing) and the thaw's
			// Enabled = recorded state is the one placement. Only with a freeze to follow (a handover import).
			Dictionary<long, DeferredBody> deferred = transfer != null && ClusterConfig.GetBool("import.deferPhysics") ? DeferPhysics(entity, slot.Builder, transfer) : null;
			// T-0401: marks the Add as an import activation (GridAddTrace skips it unless diag.gridAddTraceImports)
			// and times its parts into addSlice; the mark is cleared even when Add throws.
			ImportAddDiag.Run(entity, addSlice, () => MyEntities.Add(entity, insertIntoScene));
			ImportAddDiag.CountEntity(addSlice, entity);
			slot.Entity = null; // owned by the world from here; ImportFail closes it through Created
			created.Add(entity.EntityId);
			MySessionComponentTrashPatch.ProtectArrival(entity.EntityId); // T-0254: no trash sweep mid-import
			steps?.Step("activate_freeze");
			if (transfer != null) Freeze(transfer, new[] { entity }, deferred);
			steps?.Step("activate_reseat");
			ReseatImported(entity, slot.Builder);
			JetpackResyncService.Remember(entity, slot.Builder);
			activated?.Add(entity);
			count++;
		}
		steps?.Step("import_activate");
		context.Count += count;
		HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
		if (timer != null) timer.Entities += count;
		return addSlice;
	}

	/// <summary>Game thread: the once-per-import tail of the activation (whole partition).</summary>
	/// <param name="activated">The entities this frame Added (activatePerFrame=0), whose transferred ids are
	/// applied by the per-import walk first; null when the slices already did that.</param>
	private static ImportComparison ActivateFinish(ImportContext context, StagePlan plan, List<MyEntity> activated)
	{
		HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
		Transfer transfer = context.Transfer;
		plan.Activated = true;
		context.Next = context.Entities.Length;
		// Entities in the world across frames must be in the partition too (see ImportStep), and
		// they must carry their transferred network ids before the replication layer can offer
		// them to a client already on this node: the legacy import finalised in the creation
		// frame, so a client never saw a temporary id; here import_finish is frames away, so
		// finalise now. ImportFinish's call then finds nothing left to do.
		m_partitions.Adopt(context.PartitionId, context.Entities.Select(x => x.EntityId));
		timer?.Step("finalize_network_ids");
		// T-0392: the full Finalize computes an identity for every replicable on the node (~33 ms per
		// import on r116 whatever its size). The per-import walk resolves the imported entities' ids
		// first; the full Finalize then returns at once unless something is left over (kept entities,
		// a replicable destroyed between export and import), which it resolves or drops as before.
		if (activated != null) ReplicationServerPatch.FinalizeNetworkIds(EntityIdsWithChildren(activated));
		ReplicationServerPatch.FinalizeNetworkIds();
		timer?.Step("validate_builders");
		return transfer == null ? null : CollectImportedState(transfer, context.Entities, context.Save.Body);
	}

	/// <summary>The entities' ids and their hierarchy children's (blocks), the scope of a per-import finalize.</summary>
	private static HashSet<long> EntityIdsWithChildren(List<MyEntity> entities)
	{
		HashSet<long> ids = new HashSet<long>();
		foreach (MyEntity entity in entities)
		{
			ids.Add(entity.EntityId);
			if (entity.Hierarchy == null) continue;
			HashSet<VRage.ModAPI.IMyEntity> children = new HashSet<VRage.ModAPI.IMyEntity>();
			entity.Hierarchy.GetChildrenRecursive(children);
			foreach (VRage.ModAPI.IMyEntity child in children) ids.Add(child.EntityId);
		}
		return ids;
	}

	/// <summary>
	/// Game thread, import.activatePerFrame=N&gt;0 (T-0392): one activation slice. The one-frame
	/// activate held the game thread ~4-5 ms per entity (register ids, Add, freeze, reseat, network
	/// ids), 300-650 ms for a 100-entity partition; this spreads the per-entity work over frames.
	///
	/// First slice frame only: the kept-entity rules (as the one-frame activate, before any staged
	/// entity is Added), a build error from the workers fails the import before anything is Added,
	/// and the staged entities are grouped (ImportActivateSlices.Groups: whole physical groups). Every
	/// slice frame takes the next whole groups, at most N entities unless one group is larger and, with
	/// import.activateBudgetMs=B&gt;0, no more than fit into B ms at the estimated Add ms per entity
	/// (ImportActivateSlices.Take; at least one group). Every slice frame: build on the game
	/// thread what has no dormant copy (import.stageWorkers=0, or a late kept one), then per entity
	/// exactly what the one-frame activate does; then the slice's own network ids (FinalizeEntities:
	/// no client may be offered an entity under a temporary id between frames) and the partition
	/// adopts everything placed so far (as ImportStep's slices do: no topology sweep may mint a
	/// partition around a slice). The live-controlled set is recomputed per frame, as ImportStep
	/// does, because a client can take control of an Added entity between two slices.
	///
	/// Serial update mode is released at the end of the FIRST slice frame: it only shields worker
	/// Init in flight from the parallel orchestrator's spin-wait (SE-31669), the workers are done
	/// before activation starts, and holding the whole node serial for N more frames is a cost with
	/// no reason. ReleaseSerialUpdate is idempotent per plan, so the later frames, the final frame
	/// and StageAbandon calling it again is the same single release.
	/// </summary>
	private const double ActivateEstimateWeight = 0.2;

	/// <summary>
	/// T-0392 import.activateBudgetMs: this node's running average (EWMA, weight 0.2 per slice) of the
	/// MyEntities.Add ms per entity over sliced imports, the first slice's estimate. 0 until the first
	/// slice was measured, so the node's first-ever slice is the count cap alone. Updated whatever the
	/// budget, so turning the budget on starts from a measured value. Game thread only.
	/// </summary>
	private static double s_activateMsPerEntity;

	private static int ActivateSlice(ImportContext context, StagePlan plan, int perFrame, ImportActivatePacer.Decision pace)
	{
		// T-0392: the per-frame budget, or with import.activateMsPerSecond the smaller of it and the
		// window's remaining allowance (ImportActivatePacer.Decide); N=0 hands import.activateBudgetMs through.
		double budgetMs = pace.BudgetMs;
		HandoverPhaseTimer timer = HandoverPhaseTimer.Current;
		bool allocationSuspended = VRage.MyEntityIdentifier.AllocationSuspended;
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			ThrowIfAbandoned(context);
			HashSet<long> liveControlled = LiveControlledEntityIds();
			if (plan.Groups == null)
			{
				plan.ImportIndex = NodeAgeCounter.Imports.Next();
				ActivateKept(context, plan, liveControlled, buildLate: false);
				foreach (StagedEntity slot in plan.Staged)
					if (slot.Error != null) throw new InvalidDataException("Staged entity " + slot.Builder.EntityId + " could not be built: " + slot.Error.Message, slot.Error);
				plan.Groups = ImportActivateSlices.Groups(plan.Staged.Select(x => x.Builder).ToList());
				plan.GroupSizes = plan.Groups.Select(x => x.Count).ToArray();
				plan.Placed = plan.Kept.Select(x => x.EntityId).ToList();
			}
			if (plan.NextGroup >= plan.Groups.Count)
			{
				// Nothing staged (everything kept): the kept rules were this frame's work.
				m_partitions.Adopt(context.PartitionId, plan.Placed);
				return 0;
			}
			// T-0392 cost budget: the first slice of an import estimates from the node's running average,
			// every later one from the slice before it. Budget 0 (or no estimate yet) = the count cap,
			// exactly the plan ImportActivateSlices.Plan(builders, perFrame) makes.
			string estimateFrom = plan.SliceMsPerEntity > 0 ? "slice" : s_activateMsPerEntity > 0 ? "node" : "none";
			double estimate = plan.SliceMsPerEntity > 0 ? plan.SliceMsPerEntity : s_activateMsPerEntity;
			int take = ImportActivateSlices.Take(plan.GroupSizes, plan.NextGroup, perFrame, budgetMs, estimate, out bool overBudget);
			StagedEntity[] slice = ImportActivateSlices.Slice(plan.Groups, plan.NextGroup, take).Select(index => plan.Staged[index]).ToArray();
			plan.NextGroup += take;
			if (overBudget) plan.OverBudgetSlices++;
			if (slice.Any(slot => slot.Entity == null && slot.Error == null))
			{
				timer?.Step("import_create");
				foreach (StagedEntity slot in slice)
					if (slot.Entity == null && slot.Error == null) StageOne(slot);
			}
			timer?.Step("import_activate");
			List<MyEntity> activated = new List<MyEntity>(slice.Length);
			ImportAddSlice addSlice = ActivateEntities(context, slice, liveControlled, activated);
			// ActivateEntities closed its activate_add step: this frame's MyEntities.Add time (67-85 % of
			// the activation on r117) per entity Added is the next slice's estimate and feeds the node's.
			double addMsPerEntity = 0;
			if (timer != null && activated.Count > 0)
			{
				addMsPerEntity = timer.StepMs("activate_add") / activated.Count;
				plan.SliceMsPerEntity = addMsPerEntity;
				s_activateMsPerEntity = s_activateMsPerEntity > 0
					? s_activateMsPerEntity + ActivateEstimateWeight * (addMsPerEntity - s_activateMsPerEntity)
					: addMsPerEntity;
			}
			if (timer != null)
				timer.Fields = string.Format(System.Globalization.CultureInfo.InvariantCulture,
					" sliceEntities={0} sliceGroups={1} budgetMs={2:0.#} estimateMsPerEntity={3:F2} estimateFrom={4} plannedAddMs={5:F1} overBudgetGroup={6} addMsPerEntity={7:F2}",
					slice.Length, take, budgetMs, estimate, estimateFrom, slice.Length * estimate, overBudget ? 1 : 0, addMsPerEntity) + PaceFields(pace)
					+ addSlice?.Fields() // T-0401: last, so the t0392 parsers' patterns still match
					+ (addSlice != null ? NodeAgeCounter.Imports.Fields(plan.ImportIndex) : null);
			timer?.Step("finalize_network_ids");
			ReplicationServerPatch.FinalizeNetworkIds(EntityIdsWithChildren(activated));
			timer?.Step("adopt");
			plan.Placed.AddRange(activated.Select(x => x.EntityId));
			m_partitions.Adopt(context.PartitionId, plan.Placed);
			return activated.Count;
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = allocationSuspended;
			ReleaseSerialUpdate(plan);
		}
	}

	/// <summary>
	/// Game thread, import.activatePerFrame=N&gt;0: the frame after the last slice. What happens once
	/// per import, exactly as the one-frame activate ends: the partition adopts every member, the full
	/// network-id Finalize resolves (or drops, as today) whatever the slices left, and the round-trip
	/// builders are collected over all entities at one instant.
	/// </summary>
	private static ImportComparison ActivateSlicedFinal(ImportContext context, StagePlan plan, ImportActivatePacer.Decision pace)
	{
		if (HandoverPhaseTimer.Current is HandoverPhaseTimer timer) timer.Fields = PaceFields(pace);
		bool allocationSuspended = VRage.MyEntityIdentifier.AllocationSuspended;
		VRage.MyEntityIdentifier.AllocationSuspended = true;
		try
		{
			ThrowIfAbandoned(context);
			if (plan.Workers <= 0) plan.StageWallMs = plan.Staged.Sum(x => x.InitMs);
			HandoverPhaseTimer.Current?.Step("adopt");
			return ActivateFinish(context, plan, null);
		}
		finally
		{
			VRage.MyEntityIdentifier.AllocationSuspended = allocationSuspended;
			ReleaseSerialUpdate(plan);
		}
	}

	/// <summary>
	/// Between two slice frames the import can be failed (a game-thread timeout ran StageAbandon) or
	/// its transfer discarded (/discard closed CreatedMembers so far): a later slice must not Add more
	/// entities that nothing would close. The throw runs ImportFail, which closes what is in Created.
	/// </summary>
	private static void ThrowIfAbandoned(ImportContext context)
	{
		if (context.Failed) throw new InvalidOperationException("Staged import was abandoned between activation slices");
		if (context.Transfer?.Discarded == true) throw new InvalidOperationException("Transfer was discarded between activation slices");
	}

	/// <summary>Game thread: a dormant copy that will never be activated.</summary>
	private static void DiscardDormant(StagedEntity slot)
	{
		MyEntity entity = slot.Entity;
		slot.Entity = null;
		if (entity == null) return;
		try { ClusterCloseScope.Run(entity.Delete); }
		catch (Exception e) { Log("discarding dormant entity " + slot.Builder.EntityId + " failed: " + e.Message); }
	}

	/// <summary>Game thread, failure path: serial mode off, dormant copies deleted, the import undone.</summary>
	private static void StageAbandon(ImportContext context, StagePlan plan)
	{
		ReleaseSerialUpdate(plan);
		if (plan.Workers > 0 && plan.Pending > 0)
		{
			// Workers still running own their slots; deleting under them would race. They are
			// bounded by one entity's Init each; the dormant entities they produce are leaked to
			// the GC with their ids never registered, which is harmless but worth a line.
			Log("staged import abandoned with " + plan.Pending + " workers still running partition=" + context.PartitionId + "; their dormant copies are dropped unregistered");
		}
		else
		{
			foreach (StagedEntity slot in plan.Staged) DiscardDormant(slot);
		}
		ImportFail(context);
	}
}
