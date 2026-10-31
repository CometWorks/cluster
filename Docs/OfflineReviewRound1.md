# Review round 1 response — implementation and earned proof

Original review remains immutable: `review-round1.md`, exact head `f4e4128814654b9aa11d0e9aaae304c71c65d222`.
Product repairs: `265626630f4f80d42c5f0c36ef5fecc2ce6afcef` on `feature/se-346-347-offline-wake`.
Follow-ups: `b30e855`, `d1749ec`, `42fd3a0`, `21d3717`, `fda681a`, `e76821b`. PR: https://github.com/CometWorks/cluster/pull/180.

Native manifests are pinned separately: **2656266** (initial reviewed repairs), **42fd3a0**
(profile summary and observed means), **e76821b5fdd93ea0420f04db08ea398e7c119630**
(explicit generation-bearing empty presence). Suite/fixture versions and individual results are
separate evidence. Pending statements below are not passes; a later package inherits no unrun proof.

## R1 — repaired; detached and short native Auto differential pass

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


## 2026-10-31 follow-up: required shared safe-zone and production costs

New required scope is implemented in `0f40b8f`: active player-built safe zones use the same
captured electricity sinks, prioritized producer dispatch, fuel inventories, production state,
renewables and battery mode/loss accounting as every other modeled consumer. Diagnostics expose
per-producer delivered energy/fuel, per-consumer energy, storage input, production units, chip
renewals and final zone state. No separate debit mechanism or background offline executor exists.
Power loss removes the modeled bubble and pauses paid credit; powered recovery uses vanilla's
activation delay. Chip exhaustion removes the bubble without changing the functional Enabled
switch. Transactional Commit retains settings/rights and suppresses the carried enabled bubble
before instantiation when the modeled costs cannot sustain it. Admin zones remain separate.
Initially already-paused player zones with no live zone id are not full modeled coverage yet.

The native reference exposed a clock defect: 16ms authority units were being spent as 60Hz
resource frames, creating approximately4% excess duration. `0f40b8f` converts captured clock units
to native engine frames (225000 authority units/hour =216000 engine frames/hour). Sun phase and
external authority stamps remain unchanged; standalone factories state their engine-frame basis.
Earlier credit amounts prove lifecycle execution only; they are not current accuracy evidence.

At exact product `ef2e01608cf45f883770b91ab768978ee8a3cc30`, package archive
`bf26a0d0f7397974a695468f7a3ffd99b569c3ec9f1c14274c4dd2fc5541868d`:

- `native-host/results/20261031-191916Z/OFF-LIVE-DIFFERENTIAL`:7/7 pass, actual full Auto
  battery and live refinery native reference within the tick envelope after clock correction.
  This supplements R1/R2; the six-hour/three-baseline reference remains missing.
- `.../T-OC-09`:14/14 pass, extended shared Apply with a running player zone, reactor,
  refinery and assembler. First held provisional uranium debit0.00900955275; SIGKILL before
  Final; retry from the same original Final debits0.08629490417 for its later interval,
  with no stacked first debit. Actual inventory assertions include fuel and produced items.
  After replicated compensated ordinary Final, a second kill/recovery has no profile/results.
- `.../OFF-OBSERVED-POLICY`:10/10 pass. Real external components yield multi-sample rates;
  means survive known-class capture; opaque storage and scripted inventories freeze explicitly;
  actual radio/shared wake preserves their stocks. Controlled fixture fraction1/1 is not a
  real-world census or a Measure decision. Full sinusoidal/fuel-script/rotation semantics remain.
- `native-host/results/20261031-193622Z/T-OC-07`:10/10 pass, actual Block/Freeze admission,
  PB/timer/ejector unknowns, real radio/UI wake, preserved PB Storage, Frozen confidence and
  modeled battery debit beside unknown behavior. The previous terminal-open attempt failed
  and stays recorded as a failure.
- `.../T-OC-11`:94/95 individual checks passed, whole scenario FAIL. Actual reactor-only
  references earned radius10(5MW), radius20(7.510204MW), Box shape, input/output stocks,
  energy conservation, paid credit, empty/full-output standby and finite fuel outage/removal
  with switch/settings retention. Native uranium0.050750/0.071668 versus integrated modeled
  0.051400/0.072586 in the two steady radius references. Reactors settle at actual
  900..3600-frame fuel timers, which are recorded. Rapid Auto-battery sampled native fuel
  versus integrated delivered-output debit remains a longer-differential limitation.
  A too-strict zero-output full-inventory assertion ignored MyFixedPoint residual headroom;
  revised check allows at most one native production tick before standby, with actual stocks.

Solar backup then exposed a native SIGSEGV on a generic .NET Task.Run worker inside
`MyPhysics.CastRayInternal`, directly called by `OfflineCompensation.SampleSun`. Full2.9GB dump
and crash report are preserved at `native-host/solar-worker-crash/`. Root fix`6ccf587` uses
`MyPhysics.CastChainedRaycastsParallel`, whose engine workers hold Havok read state and native
thread context; one direction/panel/physics pass bounds work. Node/test-plugin compile passed;
focused OFF-SOLAR-NATIVE and corrected T-OC-11 await a rebuilt-package native pass.

Original laser fixture failed again at ef2e016. Live probes in
`native-host/results/20261031-193622Z/P2-OFF-CALL-LASER` captured accepted explicit ConnectTo,
then old online target origin while the row was durable, followed by `offline:21` with source
still stuck in contact_Rec and no load. Fix`1063947` retains explicit intent across that report
race and spends the per-source minute allowance only on an actual offline admission. Completed
online links leave the pending set and cannot become background wake demand. Node compile
passes; native id/GPS/established-no-wake regression is pending on the rebuilt package.

The original review artifact remains immutable. No synthetic assertion is used as native
crash/concurrency/UI proof. Safe-zone recovery/chip renewal/shared-wake, the broader original
acceptance matrix, Quasar UI and exclusive performance obligations still block acceptance.


### Earned rebuilt-package follow-up

At exact product1063947af594975ad2fae65259dc39c2ea67ae98, archive
242b88048f67eb11c62ad10aac2ccb8b45588ac322200ace838830bbe1184f3e:
`native-host/results/20261031-194947Z/OFF-SOLAR-NATIVE` passes4/4 (native Havok worker crash
regression). Expanded T-OC-11 passes125/125 in474 seconds: actual zone+production shared draw,
radius/shape, native chip renewal, fuel/stock conservation, empty/full standby, finite-fuel
outage/removal,135s solar/battery recovery through120s native activation, ordinary active-zone
offlining and true radio/shared wake after chip credit expiry. Wake debits0.0820199339 uranium,
produces2565.08928 refinery units and109 plates, removes unsupported bubble and preserves the
functional switch/rights; global admin control stays enabled/uncharged. Four-hour/three-chip
standalone and broader production/producer envelopes remain separate. The following source
trace found another edge: current carried live-zone settings must win over the manager's stale
remembered disabled settings when removal suppresses the carried entry. One-line root repair
and focused actual-native-builder failing/passing fixture are authored, not earned yet.

Laser id wake now loads onto the source node at1063947, proving retained intent across the old
online report. However both real lasers end idle before pairing; full scenario remains FAIL.
Focused native state tracing is authored to identify the transition instead of masking it with
unconditional reconnects. GPS/established-no-wake/radio-chain proof still missing.


### Further native trace and corrected hypothesis

The source contains no new settings-precedence repair. Native probe
`results/20261031-201018Z/OFF-ZONE-SETTINGS/zone-settings-transaction.json` showed remembered and
live radius20/Box/Drilling+Welding already identical. Its stale-input precondition failed, so the
hypothesis was not reproduced; the speculative one-line change and extra fixture were removed.
Native125-check T-OC-11 settings/rights proof remains as recorded above.

Laser trace in the same run identified the next actual cause: immediately after real peer
registration, vanilla TryUpdateTargetCoords (inlined in Update10) sees the peer still idle and
idles the source before the1s call-up update. Exact stack is in node-1.log, laser trace idle at
21:14:44.702 local. Pairing is authored at the existing AddLaser postfix boundary using existing
MyLaserAntennaSeams.PointBack (idle-peer/first-caller, native access/sync), not an unconditional
reconnect loop. Before/fresh rebuilt-package pass remains pending.

A separate native trust-boundary defect is repaired in source: radio/respawn admission used
vanilla GetUserPromoteLevel, which reports every nonzero id as Owner for an OFFLINE backend.
Actual probe:1234567890 level5, promoted0; authenticated hostile request was accepted and woke.
Changing fixture ids cannot resolve the OFFLINE shortcut. Admission now reads the already
replicated promotion map through PromotionService.IsModerator, reused by online-player admin
reporting. Standalone retains vanilla role behavior. New native fixture asserts no actual
promotion for negatives and sets a real replicated Moderator for a positive control; pending
rebuilt-package proof. This supplements the R1-R8 repairs and does not alter their original review.


### Latest build and additional earned native results

All23 feature commits rebased cleanly onto main dcc444ece0d473eefb516d660b8f2172fd353708.
Patch equivalence is recorded in rebase-range-diff.txt; original tested commits remain available
under refs/tm/offline-wake/pre-main. Current production package9f128a7bc2715cc2f0bbb0522715ffb6b7d39380,
archive d837fc659fc7f4276f092dc2337bb89ab6f3193ae60feb47957b8977eba7f3a4, passes full private build,
registry/CLI and exact binary/symbol hash checks. Fresh native suite results/20261031-203241Z
is active; no pending case is a pass. Test-only follow-up30d928f awaits actual refinery output
rather than power alone, and anchors a second client's relay to that client's position.

At product1063947, results/20261031-201018Z/P2-OFF-CALL-RATE passes29/29:20 concurrent
client requests,19 refused/one complete; real minute boundary;6 successive actual terminal
wakes in16.573s;7th refused and offline. OFF-CACHE-DURABILITY passes12/12, adding untouched-entry
and stale/new epoch controls to real WA pre-Final kill/replacement replay. T-OC-12 still FAIL:
first cold zero-output tick retained formula fallback, then another base measured/cache populated;
corrected warm-output fixture is pending. No incomplete whole batch or interrupted case counted.


### Durable antenna row / bridge follow-up

At9f128a7, native P2-OFF-CALL-RATE passes29/29 again using the repaired raw promotion-map
predicate. Native laser id wake now establishes BOTH real blocks, proving the registration-boundary
pairing repair. The established-link leg fails with no local proxy at12s: its retained source
report still appears live for20s and outranks the authority's durable offline row. Source6423447
makes that durable row authoritative until its admitted load target reports live, and drops the
source bridge when the authority publishes a new origin. Native fixture fails before this repair;
fresh package archive58bf6cc64c4787a7da5729054a412224fa5bb739e4136bf894f638ce9891009b is running
in an isolated second correctness lane (offset10000/Remote34900, separate caches, no rendering).
No additional accounting service or background wake demand was added.

Deny at9f128a7 earned ordinary-player hostile and no-relay refusal checks with promoted-map0.
Its Moderator positive-control actually loaded the hostile target (artifact owner=node-1,
found=true, promoted2), but the immediate final assertion preceded the heartbeat ending the
offline row. Whole case remains FAIL; corrected fixture waits for owner AND row retirement.
No synthetic or partial proof is promoted to a full native pass.
