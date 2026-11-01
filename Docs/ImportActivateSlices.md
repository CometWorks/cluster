# Staged import: activation slices (`import.activatePerFrame`, T-0392)

The staged import (`import.mode=staged`) builds the arriving entities dormant on worker threads and
then activates them on the game thread. Before T-0392 every entity was activated in one frame
(`import_activate`). On r91 the activate frames of 250 ms or more were partitions of 83-122 entities,
at about 4-5 ms per entity: register ids, Add, freeze, reseat, network ids. `import.activatePerFrame`
spreads that work over several frames.

## Frames

| `import.activatePerFrame` | game-thread frames after staging |
| --- | --- |
| `0` | `import_activate` (unchanged), then `import_finish` |
| `N > 0` (default 20, T-0392) | `import_activate_slice` x k, `import_activate_final`, then `import_finish` |

With `0` the code path is the one-frame `ImportActivate`. It was split into helpers that run in the
same order in the same frame. The delta import (`HandoverDelta`) always uses the one-frame path.

## Slice rules (N > 0)

First slice frame only:
- the kept-entity rules (`ImportKeepExisting`) for the builders that were already on the node at
  `import_begin`, before any staged entity is Added, as the one-frame activate does. A kept entity
  that has to be rebuilt after all (gone, or a fenced duplicate was purged) is not built here. It is
  queued as a staged entity and built on the game thread in its own slice, so that no entity whose ids
  were registered in the world table sits dormant across a frame;
- a worker build error fails the import before anything is Added;
- the staged entities are grouped (`ImportActivateSlices.Groups`); each slice frame then takes the
  next whole groups (`ImportActivateSlices.Take`), which is `ImportActivateSlices.Plan` one slice at
  a time.

Each slice frame:
- builds on the game thread the slice's entities that have no dormant copy
  (`import.stageWorkers=0`, or the late kept ones above);
- for each entity does what the one-frame activate does: the appeared-meanwhile check, register
  the worker's ids, Add, Created, trash protection, freeze, reseat, jetpack resync;
- `FinalizeNetworkIds(ids)` for the slice's entities and their hierarchy children. This is the
  partial Finalize: it applies the identities it finds and drops none. Without it, a client on this
  node could be streamed an entity under a temporary network id between two frames, and Reassign
  does not notify the client;
- `Adopt` for everything placed so far (kept plus activated), as the legacy chunked import does, so
  that the topology sweep cannot mint a partition around a slice;
- recomputes the live-controlled set, because a client can take control of an Added entity between
  two slices.

Final frame (`import_activate_final`), the once-per-import tail of the one-frame activate:
`Adopt` of all members, the full `FinalizeNetworkIds()` (it resolves or drops whatever is left, as
before), and `CollectImportedState` over all entities in one frame.

### Grouping

A slice never holds part of a physically joined structure. The links are read from the builders:
the mechanical base `TopBlockId`, the attachable top `ParentEntityId`, the connector
`ConnectedEntityId` and the landing gear `AttachedEntityId`. A block id resolves to the grid that
carries the block. Merged grids are already one grid, and a seated pilot is inside its cockpit's
builder. Groups are transitive, are taken in the order of their first member, and fill slices
greedily up to N. A group larger than N gets an oversized slice of its own. Inside a slice the
original order is kept. The self-test section `activate-slices` pins these rules.

### Cost budget (`import.activateBudgetMs`)

The count cap bounds entities per frame, but the cost per entity varies widely (r117: MyEntities.Add
is 67-85 % of the activation, p50 3.5-5.7 ms per entity, some entities far more), so slices of 20
still hitched 260-400 ms. With `import.activateBudgetMs=B > 0` the groups are taken one slice at a
time (`ImportActivateSlices.Take`): a slice takes whole groups while it stays within N entities AND
its entities x the estimated Add ms per entity stay within B. The estimate is the previous slice's
measured `activate_add` ms divided by the entities it Added; an import's first slice uses the node's
running average (EWMA, weight 0.2 per slice, updated by every sliced import whatever B is). A node
that has not measured a slice yet has no estimate, and its first slice is the count cap alone.

- B can only make a slice smaller than the count cap, never larger.
- A slice always takes at least one whole group. A single group whose estimate alone exceeds B is
  still one frame: the knob cannot help it. Such a slice logs `overBudgetGroup=1`.
- B budgets the Add time only; register ids, freeze, reseat and the slice's network ids come on top.
- `B=0` (default) gives exactly the count-cap slices (self-test `activate-slices` checks this).
- Read once per import, like `import.activatePerFrame`; ignored when that is 0.

Fields on each `handover_phase phase=import_activate_slice` line (also with B=0, so both arms of an
A/B read alike): `sliceEntities` (planned), `sliceGroups`, `budgetMs`, `estimateMsPerEntity`,
`estimateFrom` (`slice`, `node` or `none`), `plannedAddMs` (sliceEntities x estimate),
`overBudgetGroup` (0/1), `addMsPerEntity` (measured). `handover_import` adds `activateBudgetMs` and
`activateOverBudgetSlices` when the activation was sliced.

### Per-second pacing (`import.activateMsPerSecond`)

The per-frame budget cut frames >= 100 ms by 35 % in the live A/B, but not the per-second server sim:
a 40+ entity import still spent ~300 ms of activation inside one second. With
`import.activateMsPerSecond=N > 0` the node spends at most N ms of activation per rolling second,
across all imports in flight on it (`ImportActivatePacer`, one per node):

- The ledger holds (time, ms) of every activation frame (slices AND the final frame) over the last
  1000 ms, ms = the frame's measured game-thread ms (the `handover_phase` gameThreadMs), never an
  estimate. Every sliced import records, whatever its N.
- Before each frame: remaining = N - spentLast1s. remaining > 0: the frame runs, a slice sized by
  `Take` to min(B if B > 0, remaining) through the same Add-ms estimate (no estimate yet: the count
  cap alone). Otherwise the import does nothing this frame and stays staged/frozen exactly as
  between two slices today; a held-back frame logs nothing, it is only counted.
- A slice takes at least one group: one larger than the allowance is a slice of its own,
  `overBudgetGroup=1`. The estimate is Add only, so a window can exceed N by the rest of a slice's
  cost (init, freeze, reseat, network ids; Add is 67-85 % of it on r117); the measured ledger then holds the next frame
  back longer.
- Oldest first: an import yields while an import registered before it is waiting. Imports on one
  node are in practice serial (the control server answers on one listener thread), so the shared
  window mostly paces back-to-back imports.
- No starvation, no timeout: when this node's pacing waits within the last 10 s (the import's own plus
  those of imports that finished here) exceed W = 2000 ms, frames go regardless of the ledger
  (`forced=1`, sized by B alone). The binding deadline is the gateway's 10 s HttpClient timeout on
  `POST /cluster/import`, which covers the whole request, and every other control request queues
  behind a running import on the same 10 s clocks; today's largest imports take 1.6-2.7 s, two back to
  back 5.4 s, so pacing adds at most 2 s inside any 10 s (7.4 s worst case). The census of the other
  deadlines (none counts against the waits) is in the comment on
  `ImportActivatePacer.DefaultForceAfterWaitMs`.
- An import that was failed or discarded meanwhile goes at once, so its frame throws and cleans up.
- `N=0` (default): every frame goes at once with B handed through: exactly the slices without the
  knob (self-test `activate-pacing` checks this, the ledger, the sizing, FIFO, forcing and a 40-entity
  import at 5 ms/entity under N = 70 spread over three one-second windows).
- Read once per import, like B; ignored when `import.activatePerFrame` is 0.

Fields: each `import_activate_slice` and `import_activate_final` line adds `paceMsPerSecond` (N),
`spentLast1sMs` (the window before this frame), `pacedFrames` (frames this import was held back since
its previous frame) and `forced` (0/1); a slice line's `budgetMs` is the budget `Take` used. The
`handover_import` line of a sliced import adds `activateMsPerSecond`, `pacedFramesTotal`,
`forcedFrames` and `activateSpanMs` (start of the first activation frame to the end of the last).

### MyEntities.Add breakdown (T-0401, `diag.importAddTimers`)

`activate_add` is 67-85 % of the activation live. With `diag.importAddTimers=true` (default; log-only)
`ImportAddDiag` times each Add of the activation and appends, LAST on the `import_activate_slice` line
(and on the one-frame activate's phase line), after the pacing fields: `addN` (Adds timed), `addTotalMs`
(Stopwatch around the Adds), `addEventMs` (inside `MyEntities.RaiseEntityAdd`, the OnEntityAdd handlers),
`addPhysicsMs` (inside `MyPhysicsBody.Activate`, which `MyEntity.OnAddedToScene` calls for the entity and
its children; physics inside an event handler stays in the event), `addSceneOtherMs` (total - event -
physics: OnAddedToScene minus physics, update/draw registration, pruning structure, components, id table),
`addEventCalls`/`addPhysicsCalls` (outermost hook hits: `addEventCalls` below `addN` means the event hook
missed Adds and the split is a lower bound), `addClusterCreates`/`addClusterReorders` (calls of
`MyClusterTree.CreateCluster` - a new HkWorld - and `ReorderClusters` inside the Adds), `addGrids`,
`addOthers`, `addBlocksSum`, `addBlocksMax`, `addFatBlocksSum`, `addGc0`/`addGc1`/`addGc2` (collections
during the Adds) and `addGcPauseMs` (`GC.GetTotalPauseDuration` delta during the Adds). `false`: no timing,
no fields.

Then, for the lock hypothesis (is the per-entity cost lock waits?), after `addGcPauseMs`:

- `addCpuMs`: the game thread's CPU time inside the Adds, `clock_gettime(CLOCK_THREAD_CPUTIME_ID)` (libc
  P/Invoke) read just inside the wall window of each Add, summed over the slice.
- `addBlockedMs` = `addTotalMs` - `addCpuMs`: wall time inside the Adds the thread was NOT on a CPU: lock
  waits (a contended Monitor spins on CPU first, then blocks), I/O, page faults, preemption by other
  threads/processes.
- `addLockContention`: `Monitor.LockContentionCount` delta across each Add, summed. The counter is
  PROCESS-wide, so lock contention on other threads during the Add counts too: an upper bound for the Adds'
  own contention (0 means no Add of this slice waited on a contended `lock`; it does not see
  `SpinLock`/`ReaderWriterLockSlim`/native locks).
- `addEventCpuMs`, `addPhysicsCpuMs`: the same CPU clock around the `RaiseEntityAdd` and
  `MyPhysicsBody.Activate` hooks, so each part reads wall (`addEventMs`, `addPhysicsMs`) against CPU.
- The CPU fields print `-1` when the thread CPU clock is unavailable (not Linux, or the P/Invoke failed once,
  which disables only these fields for the process; nothing throws).

How to read: `addBlockedMs` ~ 0 and `addLockContention` ~ 0 => lock waits are not the cost (refutes the
hypothesis); `addCpuMs` ~ `addTotalMs` => the cost is compute (under or outside locks: a lock HELD is CPU
time, so look at the CPU sampling trace under `MyEntities.Add` for where). A large `addBlockedMs` with
contention > 0 supports lock waits; a large `addBlockedMs` with contention 0 points at I/O, page faults or
preemption (compare with the host's load and `addGcPauseMs`). Read per part the same way (event, physics;
other = the rest).

### Release side (source node, T-0401, same knob)

`diag.importAddTimers` also times the SOURCE node's release of a handover. `CloseEntities` marks the
transfer's roots (`MyEntities.Close` only queues them), closes attached children, then
`MyEntities.DeleteRememberedEntities` does the per-entity work in the SAME game-thread frame: `Delete` ->
`Close`/`BeforeDelete` (a grid's blocks), physics `Close` (rigid body `Dispose`, the T-0266 path), then
`MyEntities.Remove` -> `OnRemovedFromScene` (physics `Deactivate`, cluster tree removal) and
`RaiseEntityRemove`. Nothing is deferred to a later frame except an entity vanilla finds `Pinned` (moved to
the next frame's delete list; not covered). `ImportAddDiag.RunRelease` wraps that sequence as it is
(prefix/postfix hooks only; nothing reordered or batched) and appends, last on the `handover_phase` line of
the frame that ran it (`phase=release` with `release.closePerFrame=0`, the default: Members and
Descendants summed; each `phase=release_close` frame when chunked):

`relN` (entities of the transfer closed, roots), `relTotalMs`, `relCpuMs`, `relBlockedMs`,
`relLockContention`, `relEventMs`/`relEventCpuMs` (`RaiseEntityRemove`), `relPhysicsMs`/`relPhysicsCpuMs`
(`MyPhysicsBody.Deactivate`, `MyPhysicsBody.Close`, `MyGridPhysics.Close`, outermost; the
HavokOrphanBodyFix postfix on `Deactivate` is inside), `relOtherMs` (total - event - physics: block close,
hierarchy, game logic, update unregistration, render, `OnEntityDelete`, id table), `relGrids`,
`relBlocksSum` (read before the close), `relGc0..2`, `relGcPauseMs`, then `relOthers`, `relBlocksMax`,
`relFatBlocksSum`, `relEventCalls`, `relPhysicsCalls` (hook coverage). `relTotalMs` also covers any entity
someone else queued for delete before this frame's `DeleteRememberedEntities`. Read them as the `add*`
fields.

### Node-age tags (T-0401, same knob)

The bench put the first import on a fresh node at 3.70 ms/entity, the second at 1.45, warm 0.86 (live 1.8 p50).
To split the live cost by node age, every timed activation line (`import_activate_slice`, or the one-frame
`import_activate` line) ends, after the `add*` fields, with `nodeAgeS` (seconds since the node PROCESS started,
the same origin as the hitch lines' age; the node starts serving some tens of seconds later) and `importIndex`
(1-based count of staged imports this process has begun activating, this one included; every slice of one
import carries the same index). The source's `release` / `release_close` line ends, after the `rel*` fields,
with `nodeAgeS` and `releaseIndex` (1-based count of releases this process has begun closing).

`diag.gridAddTraceImports=false` (default) stops `GridAddTrace` logging a stack for the Adds of this
activation (the import path logs them); `true` restores it. Other grid adds are traced as before.

## Serial update mode

`ForceSerialUpdate` protects worker Init that is still running from the parallel orchestrator's
spin-wait (SE-31669). The workers have finished before the first slice runs, so serial mode is
released at the end of the first slice frame. Keeping the whole node serial for k more frames
would cost frame time and protect nothing. `ReleaseSerialUpdate` is idempotent per plan (the
`SerialForced` flag), so the later frames and `StageAbandon` repeat the same single release.

## Periodic services between slices (ImportActivationGuard)

With N > 0 the game thread runs whole frames between the first slice and the final frame, and with
them every periodic service in `ClusterRuntime.Update` (4 Hz) and the vanilla per-frame session
components. A service that WRITES an imported entity in that window changes what the final frame
collects for the round trip. Live 2026-10-05 00:22:56 (transfer 081ae0b3, 140 entities, 8 slices):
`ShipAntennaService` (rule `assigned`) switched off the radio antennas of 7 ships Added by early
slices (unpiloted until commit, frozen so speed 0): residuals=7 `CubeBlock/Enabled: true != false`,
abort. With N = 0 activation and collection are one frame, so nothing can run between them.

`ImportActivationGuard` restores that property. `ImportActivationGuard.RunSliced` opens a guard over
the import's top-level entity ids (staged and kept) before the first slice and releases it in a
`finally` after the final frame: on success, on a slice that bails (`ThrowIfAbandoned`: failed or
discarded), on any frame that throws, and on a game-thread timeout. `ImportActivationGuard.IsOpen(id)`
or `IsOpen(entity)` (entity or its top-most parent) is the one predicate the writers check:

- `ShipAntennaService` skips guarded AND frozen grids (`ShipAntennaService.Skips`), keeping their
  bookkeeping so the grace term survives an abort.
- `CharacterRegistryService`: first-seen and settle skip a guarded body. Helmet, light and
  broadcasting are round-trip fields, and the body is "seen" on the first pass after the guard.
- `MyTimerComponentSystemPatch` and `MySessionComponentSmartUpdaterPatch`: no timer tick
  (`FramesFromLastTrigger`) and no presence-tier change (timer pause, `TimerEnabled`) for a guarded
  entity.
- `PartitionPersistence`: the fenced-orphan purge and recovery's stale-copy replacement treat a
  guarded id as re-acquired. It is untainted only at `ImportFinish`.

Each caller asks through `IsOpenFor(GuardUser, id|entity)`: the same boolean as `IsOpen`, plus a
per-caller skip counter that moves only when the guard holds (the frozen checks are not counted).
`GET /cluster/import-guard` reports the counters with the guarded imports and ids since start
(diagnostic only, no periodic log).

Self-test section `import-activation-guard` checks the guard's lifetime through `RunSliced`: open in
every slice and in the final frame, released after the final, after a bailing slice and after a
throwing final, overlapping guards counted per id, `ShipAntennaService.Skips` following it, and
`IsOpenFor` counting only open ids, only for the asking caller, with `RunSliced` counting the import.
Bench: `tools/t0392/bench_t0392_slices_v2.py` uses antenna ships, rule `assigned`, and a reconcile
on every `ClusterRuntime.Update`.

## Failure

- A slice or final frame that throws runs `ImportFail` in that frame. `ImportFail` closes
  everything in `Created`, which includes all earlier slices, and thaws what was frozen. The handler
  then runs `StageAbandon`, which releases serial mode (idempotent) and deletes the dormant copies
  that were not activated. Activated slots have `Entity = null`, so they are skipped.
- On a game-thread timeout between slices, the `StageAbandon` Invoke is queued behind the late
  slice. The late slice runs first and `StageAbandon` then closes it.
- `/discard` landing between slices: the next slice or final frame sees `Transfer.Discarded` (or
  `context.Failed`) and throws before it Adds anything. `ImportFail` then closes what was added.
- As before, a plain import (no transfer) does not close what it already Added when it fails.

## Bench plan

1. Node self-test `GET /cluster/self-test?section=activate-slices` on a one-node cluster.
2. P3-HND-01 (round-trip fidelity, both directions), P3-HND-02 (interaction-state matrix: rotors,
   connectors, landing gear) and P3-HND-03 (validation-abort containment, which now aborts after
   slices were Added) with `import.activatePerFrame=20`, then again with `0`.
3. P3-HND-04 (redeliver with a seated character while streaming): checks that the partial network-id
   finalize leaves no temporary ids visible.
4. Live arm in the T-0392 style: `import.activatePerFrame=20` against `0` (ABBA). Read the
   `handover_phase` lines (`import_activate` against `import_activate_slice` and
   `import_activate_final`): p90 and maximum game-thread ms per frame, frames of 250 ms or more, and
   the extra wall time per handover (k+1 frames). Also read `handover_import ... activateSlices=`,
   residual aborts per handover, and client-metrics lag on a connected pilot.
