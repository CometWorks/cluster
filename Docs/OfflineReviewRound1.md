# Review round 1 response — implementation and earned proof

Original review remains immutable: `review-round1.md`, exact head `f4e4128814654b9aa11d0e9aaae304c71c65d222`.
Product repairs: `265626630f4f80d42c5f0c36ef5fecc2ce6afcef` on `feature/se-346-347-offline-wake`.
Follow-ups: `b30e855`, `d1749ec`, `42fd3a0`, `21d3717`, `fda681a`, `e76821b`. PR: https://github.com/CometWorks/cluster/pull/180.

Native manifests are pinned separately: **2656266** (initial reviewed repairs), **42fd3a0**
(profile summary and observed means), **e76821b5fdd93ea0420f04db08ea398e7c119630**
(explicit generation-bearing empty presence). Suite/fixture versions and individual results are
separate evidence. Pending statements below are not passes; a later package inherits no unrun proof.

## R1 — repaired; detached regression passes, native Auto differential pending

The model now bounds the full/discharging transition to Update100 and re-evaluates the nonlinear
near-full demand within 100 frames. Auto charging demand is calculated after ordinary dispatch,
so it includes the battery's actual output rather than a zeroed output buffer.

`ReviewChecks.R1` failed before repair (2 MWh after an hour). After repair it reports
2.999999994073998 MWh and 2160 events with the same 15 MW reactor/1 MW load case.
The original 28 assertions remain passing. This is detached event-model proof, not a 6-hour
native battery/reactor differential. Tick-bounded transitions and their event cost are explicit.

## R2 — repaired; detached conservation/activation regression passes

Missing-input thresholds now create events when upstream flow makes a recipe feasible. Infeasible
recipes whose output room improves are reconsidered within their captured production timer tick;
atomic multi-material/capacity interactions therefore cannot remain idle for a whole offline span.
`TickFrames` is captured from the actual production block timer (default 60 for a detached record).

`ReviewChecks.R2` failed before repair (100 iron, zero components). It now yields **9 components
and 91 iron**, with all 100 ore spent and exact stock conservation. A real refinery is included in
the Survival radio/crash runners. The full three-ore/two-refinery/upgraded/conveyor/output-full
native differential is not established by this small regression.

## R3 — repaired; detached discharge/charge/positive-control checks pass

Captured tanks carry their electricity sink on both source and storage records. Allocation disables
both gas discharge and storage charging when that sink is not served; gates are recomputed at each
event and coupled allocation pass. Capture also respects `EnableOxygen` for oxygen tank transfer.

The before-repair charge regression fails. After repair an unpowered tank neither discharges nor
charges, while the powered positive control charges. The capture path is compiled in both roles.
Actual native power-loss/cold-bootstrap/oxygen-setting differentials remain pending.

## R4 — repaired; cold game-math regression passes

Primary and alternate modern pivots now use the engine's explicit
`Translation + Right*X + Up*Y + Forward*Z`. `offline-solar-pivots` checks identity and rotated
transforms with sign-sensitive controls against the incorrect Backward-Z transform.

Command: `dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never -- --solar-pivots ClusterNodePlugin/bin/Release/net10.0/ClusterNode.dll <DedicatedServer64>`.
This loads the actual game math assembly but runs no Havok rays. Close-obstruction/planet curve
and 200-panel exclusive timing proofs remain pending.

## R5 — repaired conservatively; cold eligibility regression passes

Repeat assemblers are excluded from queue-difference throughput measurement at scheduling and
begin-measurement time. They retain the uncached formula fallback; a repeated unit cannot become
a false low per-block rate. The shared `CanMeasureAssembler` predicate also excludes disassembly
and in-progress queues. A cold guard regression covers Repeat and its non-Repeat positive control.

The existing Repeat discrete-event inventory conservation assertion passes. A real Repeat
assembler measurement/cache differential is still required before claiming exact native throughput.

## R6 — repaired; registry generation/no-report regressions pass

Liveness heartbeats skip presence updates. A no-report `null` snapshot preserves presence;
an explicit empty per-partition record clears only its matching reported ownership version. Each game-thread
presence sample now carries its actual ownership generation. The receiver requires both heartbeat
ownership and presence generations to match current fenced ownership; it never infers a new tag.

`--self-test remote-use` passes new full→no-report, delayed-old-generation, mixed old-presence/new-
ownership, and explicit empty-current-generation cases. The native terminal runner also observes
remote presence while ordinary liveness traffic runs. The direct registry regression is not a
two-client UI/concurrency proof. A further missing-record regression failed at 42fd3a0
(`presence-empty-before.log`): a heartbeat with current ownership plus an older dictionary that
omits that partition erased its current users. e76821b now requires a present record carrying the
sampled generation for clears too. Missing entries, stale empty records, and liveness preserve
current users; an explicit empty current-generation record clears. `presence-empty-after.log` passes.

## R7 — repaired; actual source capture refuses an active terminal pin

Clientless source capture rechecks `RemoteUse.Capture` on the game thread immediately before
`Freeze`. The deliberate co-location exemption is carried to this exact decision through the
authenticated capture route. Replay and ordinary replication add requests refuse frozen roots;
remove requests still clear pins/cancel demands. Standalone hook-null behavior remains inert.

At package 2656266, a real client woke a radio station, opened its actual terminal, and obtained
remote presence. A subsequent source `/capture?...&clientless=true` failed with `remote-use`,
without moving the grid. Evidence: `native-host/results/20261031-175308Z/P2-OFF-CALL-TERMINAL`.
The terminal PNG is complete and was visually inspected. This first run used Creative and is
not production compensation proof; its terminal close leg exposed a driver issue (below).
The exact presync-window race, two-live-client conflict/no-ping-pong interval, and co-location
positive control remain unproved natively.

## R8 — repaired through existing journal path; native pre-Final replacement passed

WA-local clear calls now queue the command through `GlobalStateChannel`'s existing authority
queue, command ledger, journal append, and held response machinery. Local responses complete
the same pending command after that path authorizes the response. Public remote `SendCommand`
still rejects self-commands; a local caller may not synchronously wait on the game thread.

`OFF-CACHE-DURABILITY` seeds the real WA cache, captures/replicates a pre-clear global save,
holds later global save registration, clears through the WA's real admin endpoint, requires
the registry journal entry before success, kills the WA, and checks replacement replay.
**Passed at product 2656266**, evidence `native-host/results/20261031-180240Z/OFF-CACHE-DURABILITY`: clear journaled before success, no newer Final before kill, WA lease fenced, replacement wa-2 restored
and replayed the acknowledged clear. A strengthened repeat with an untouched-cache positive
control and old/new epoch contributions is authored for e76821b. Existing journal fail-open
policy is inherited, not silently strengthened or presented as a new guarantee.

## Phase 5 / remaining scope

Observable rates are reapplied after known-class capture, preventing reactor/generator/production
branches from overwriting means with instantaneous/formula demand. Script inventory changes and
opaque storage still freeze conservatively with explicit reasons. That restriction is not a claim
of complete sinusoidal/fuel-script semantics; a native modded fixture/scope decision remains needed.

No Measure role was added and no observed-world fraction invented. No dirty Quasar or
clustering-plan sibling checkout was changed. Quasar UI remains a separate parent-owned follow-up.
Save v2 / WA-cache append rollback still requires restoring a compatible package and snapshot
together; no live format migration was attempted.

## Native host and driver findings

Task-owned `native-host/host.env` resolves the private package, copied Magnetar v2.4.3.7,
downloaded Pulsar v2.4.3/compiler, Remote, local planet archive and TestPlan. Ports use offset
700 and Remote base 24900; roots/caches live under this task. Doctor passes once task processes
are stopped. Nodes and client report game **01_210_014**. Other Magnetar/interactive sessions remain
untouched; no exclusive CPU/frame/traffic benchmark claim.

The initial fixture paused automatic geometry collection before any offline row existed, causing
`no fresh partition reports`; the fixture now uses the normal loop. The helper's positive-only
integer parser rejected `ms=0`; the bounded hold now explicitly accepts zero. The suite example
world was Creative, so the compensation runners now copy it to a Survival task template.

Remote's helper sent a raw replication add without adding it to the terminal's UI close ledger.
`Test/prepare_remote_fixture.py` creates a source-only task copy and registers that demand with
vanilla `MyGuiScreenTerminal.RequestReplicable` after the UI opens, so closing sends an actual
authenticated remove packet. Shared `se-remote` stays clean. The task copy records original commit
`5ddb0dca763fed7f4f5197df90d0f434406d01fd` and exact before/after file hashes in `remote-fixture/fixture.json`.

The screenshot endpoint returns before the render thread completes its PNG write. Bench now
waits for PNG signature and final IEND chunk; `test_bench_artifacts` passes. Earlier empty images
are not visual proof. Actual RC protocol/state/drive proof **passed** at package 2656266:
`results/20261031-175308Z/P2-OFF-CALL-RC`, raw W drive **113.88727569580149 m**. Its final drone PNG
was empty and is not counted. Actual IGC no-wake proof also passed in that run. The entire batch
was interrupted before known-invalid Creative compensation arms; no whole-batch success claimed.

## Current evidence paths

- `native-host/review-R1-before.log`, `review-R2-before.log`, `review-R3-before.log`: failing checks before repairs.
- `native-host/review-model-after.log`: original 28 plus new model cases passing.
- `native-host/review-remote-use-after.log`: registry fencing/presence checks passing.
- `native-host/review-pivots-repeat-after.log`, `save-container-reviewed.log`: cold game-math/guard and container proofs.
- `native-host/build-package-reviewed.log`, package `manifest.json`: full private release build/checks, exact product 2656266.
- `native-host/run-survival.log`: partial/interrupted batch; individual earned results only.
- `native-host/build-package-final.log`: full verified package 42fd3a0, archive SHA256 bdbbaa1f7899990f927693667ed670af6435aa982ba25575c8ec9e23e2bb8ed5.
- `native-host/results/20261031-182113Z/T-OC-09`: **native crash/idempotence pass at 42fd3a0**, 13 checks, two SIGKILLs. First apply credited 119.55216 iron from immutable Final 4:1:2. Retry read the same Final and recomputed 1022.46144 iron for the later interval (no stacked first credit); compensated Final 4:3:1 had node-1 and node-2 holders before owner kill. Second recovery had no profile/results for this group.
- `native-host/build-package-presence.log`: full verified e76821b package, archive SHA256 485d8b923bac8edc5f6e96eb77e686f5cb0b630d9a762d6479f9b3a87b215ef8.
- `native-host/results/20261031-180240Z/P2-OFF-CALL-RC`: **native Survival drive pass**, 113.88727569580149 m raw W; complete final PNG visually inspected.
- `native-host/run-final.log`: newer individual runs; not a whole-batch acceptance claim.

Tickets remain OPEN/Doing/assigned owen under parent coordination. No merge, publication, release,
deployment, watch, assignment removal or closure performed.

## Fixture failures retained honestly

The initial native config helper replaced the whole override map when changing one knob,
restoring default grace/quiet distances. It now reads and merges the existing document;
`test_offline_config` passes. Remote can report grid presence before the client antenna graph
is ready; successful-UI runners now retry the real helper until `screenOpen`, while denial/rate
probes retain their original negative/packet observations. The short observed fixture also
needed non-overlapping block placement and component detachment before composing vanilla solar
logic. Twenty-base profiling now waits for actual electrical working state; clear/reprofile
waits for the authority snapshot to reach the measuring node. Previous failed runs remain failed.

Interrupted scenarios previously wrote default-success result.json files, sometimes with zero
checks. Bench now records interruption as failure and rethrows after teardown; a focused regression
passes. Old zero-check interrupted files are excluded from all native proof. No synthetic test,
short engine comparison, authored fixture or interrupted batch is relabeled as full native
differential, concurrency, UI or performance acceptance.

## Later earned terminal proof at e76821b

`native-host/results/20261031-183835Z/P2-OFF-CALL-TERMINAL/result.json` is a full pass.
A real authenticated radio request woke the actual refinery station onto the caller node; the
real terminal UI opened, server presence appeared, source clientless capture refused `remote-use`,
and the open UI prevented offlining. Shared wake credited 434.79072 iron over 1422 frames.
Closing the UI sent the actual remove, cleared presence, and the station offlined again.
The complete terminal PNG was visually inspected. Observed small-station open latency was
22.313036152976565 s on this shared host; no exclusive or large-station performance claim.
The exact presync-window schedule and two-client interval remain separate proofs.
