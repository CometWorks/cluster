# Offline wake implementation draft (SE-346 / SE-347)

This branch is **not acceptance complete**. It implements wake-time compensation and antenna
call-ups in the production `Shared/Cluster` / `ClusterRegistry` architecture. No deployment,
release or game/GUI/performance acceptance is established by its compile/model checks.

## Lifecycle and ownership

Quiet logical groups settle and profile during the normal offline grace. A moving sibling
blocks offlining the partition, while quiet groups retain their profiling state. Velocity,
acceleration and self-propulsion checks replace the large-grid sleep flag by default. Online
characters/controllers, joining seats, remote pins, imports, saves, fences, drain, proximity
and fast-partition guards remain. Encounter content is excluded. The old sleep gate is
available through `offline.velocityReadiness=false`.

Immediately before physics is disabled, capture resource ports, storage, inventory stacks,
conveyor paths, recipes/queues, upgrades, timed upkeep and the final authority-frame stamp.
Profiles travel **inside the checksummed Final**, in an MPS version 2 entity/profile section
pair. Version 1 saves remain readable and ordinary saves remain version 1. No sidecar or
registry-only inventory truth is introduced. The offline row carries `OfflineGameFrame` and
`ProfileSummary`; its Final reference remains the durable source.

Every directed wake (approach, join, respawn, radio, laser) uses the existing recovery claim
and `LoadRecovery`. After cover merge and before instantiation, apply only profiles whose
entire group came from that cover entry. Wait for the authority clock/offset and asynchronous
mod-content fingerprint before evaluating a profile. Definitions, mod bytes, game version,
upgrades captured in rates, session multipliers and relevant environment inputs fence the
model. A mismatch skips compensation with an explicit diagnostic. Source files are immutable.

The model runs on detached profile state. Builder writes are prepared and validated as a
transaction per group, including inventory stock identity, mass/volume bounds and retained item
metadata/IDs. An apply failure leaves that group's builders untouched. A crash before the
new-generation save reloads the original Final and recomputes; a catalogued ordinary Final
contains the compensated state and no profile to credit again. The existing deferred-Final
recovery cover behavior is retained. `CompensatedTo` is reported in the node result; offline
rows already end at the first member heartbeat in the current registry, so no durable claim is
made that a vanished row retains that diagnostic.

No component advances offline partitions continuously. The interval is
`floor((WA elapsed milliseconds - sun offset) / 16)` frames; a full authority hour is 225,000
frames. Captured profiles convert authority units to native engine frames before applying rates:
225,000 authority units/hour become216,000 engine frames/hour. Storage/gas convert at60 frames/second,
refinery formulas use16 ms per native frame, and assembler formulas retain the engine's rounded unit duration. Native reference testing found and corrected the earlier4% duration error. Sun changes add no production time;
the captured sun phase advances with real authority frames and admin sun steps take effect at
interval end. The default 168 **authority-hour** cap forfeits excess time rather than banking
it for another wake. Shutdown wall time earns nothing.

## Modeled behavior and conservative policies

The detached model allocates grouped sinks by priority (whole-group blackout or proportional
adaptible shortage), charges storage after ordinary sinks, honors source priorities, and does
not recharge auto storage from extra discharge of its peers. Events include storage/fuel
boundaries, inventory capacity, production units/stacks, sun breakpoints and upkeep deadlines.
Shared inventory indices prevent multiple producers from independently spending the same stock.

Reactor fuels, ice/generator/gas/tank/engine chains, battery charge loss, jump charging, damaged
tank leakage, refinery ordering, assembler whole units/repeat/disassembly, survival-kit efficiency
and safe-zone chips are represented. A zone exhausting upkeep loses its bubble, its zone ID is
cleared, and its carried zone entry is suppressed; the functional block stays enabled and retains
its settings/rights for later recovery. Creative fills storage without measuring; simplified
simulation serves all demands while modeled fuel still burns.

Throughput observes ordinary engine timer ticks. Refineries rotate a selected input stack;
assemblers rotate only between units with enough input/output headroom. Original inventory/queue
order is restored. Incomplete/stock-limited measurement leaves an uncached formula and
`Approximate` confidence. A measured formula disagreement remains per-block rather than becoming
a shared exact rate. The WA publishes and persists the cache. Clearing a key increments its
epoch so stale contributions cannot resurrect it.

Solar/farm sampling snapshots pivots, transforms and planet geometry on the game thread and
ray-tests through the engine physics queue without changing the world's sun. Generic .NET
workers must not invoke synchronous Havok rays: a native crash exposed that invalid context.
The sampler now uses `CastChainedRaycastsParallel`, one direction/panel/pass, under the engine's
physics read state and native thread initialization. Armor/partial-block edits, group pose,
mod content and sun basis invalidate the curve. Wind uses settled placement and atmospheric speed
with weather multiplier one. Curve/performance fidelity still needs the planet differential lane.

Script-driven resource rates get short-window means/variance; `InPlace` can wait a full sun
rotation, while the default `Skip` uses the short window. PB/timer/ejection execution is never
replayed. `Freeze` leaves opaque behavior unchanged while modeled siblings compensate; `Block`
keeps the partition online. `Mean` handles observable resource rates, but inventory changes that
cannot be separated from conveyor transfers freeze that block with a diagnostic rather than
inventing an inventory source/sink. This conservative fallback is a material limitation to validate
with the intended modded-world fixtures. No Measure node was added: real Octahedron observed-world
fractions are unavailable, so phase 6 remains an evidence-gated decision.

The model refuses a partial result after its fixed event budget. It reports the skip; it does
not silently apply an incomplete interval. Arbitrary script inventory semantics, assembler
cooperation, refinery per-tick ore-switch under-credit and changing external occluders are not
claimed to reproduce engine replay.

## Required running safe-zone and common power budget

Running player-built safe zones are required SE-347 scope. Their actual engine sink supplies
shape/radius-dependent operational draw; refinery/assembler idle versus working demand, upgrades,
all other captured consumers and storage charging share that same group allocation. Dispatch
honors source/consumer priorities, renewable contributions, battery modes/recharge losses, fuel
and inventory bounds. Fuel is consumed according to each producer's delivered output and
captured definition rate, rather than its nameplate maximum. Results include per-producer
energy/fuel, per-consumer energy, storage input, production units, chip renewal and zone state.
There is no separate accounting service, second debit layer or background offline simulation.

Vanilla only advances upkeep while the block works. Power loss removes the modeled bubble and
pauses paid credit. Recovery recreates it through the native activation delay, then resumes
credit/chip renewal; chip exhaustion removes the bubble while retaining the functional switch,
settings and rights. `BlockSafeZones` already carries zone XML in the grid builder: source close
removes the old bubble, and compensation changes the carried state before target instantiation.
Unsupported bubbles cannot be restored enabled. Active bubbles do not independently block
quiet offlining; the existing approach/interaction wake path remains responsible for loading
content before interaction. Admin zones follow their separate global-state rules and incur no
player-block chip or producer-fuel debit.

Capture covers active or activating zones with a live zone id. Initially already-paused zones
with id0 are not full modeled coverage. Native reactors settle fuel at900..3600-frame timers;
the model integrates delivered energy. Short steady-load comparisons record raw native and
modeled quantities plus the actual native timer envelope. A rapidly changing Auto-battery load
needs the longer differential; these short checks do not establish its native sampled-fuel fidelity.

## Radio / laser admission and presence

Only an authenticated client's explicit absent-replicable add request starts radio call-up.
The original endpoint, interacted entity, medical ID and pin flags survive. Remove requests
cancel pending work. Proxy relay reach and friendly ownership/remote sharing are checked before
waking; moderators bypass those checks, never rate limits. Cluster moderator bypasses read the replicated promotion map; vanilla OFFLINE backend
Owner shortcuts do not grant a bypass. The service permits one pending
request per client, a 10-second entity cooldown and six offline admissions per minute.

A directed request loads onto the caller's node and reasserts while pending; an unavailable
caller is refused without redirecting. A same-node approach refresh preserves the reservation.
A second caller cannot steal a live reserved remote/laser target. Once local, vanilla rights are checked before
pinning and forcing replication (also for online pulls). A brief receiver-settle retry covers the
Update100 gap between entity registration and antenna graph readiness. HUD notices start after
two seconds, repeat every ten, and report failure at denial/timeout. Default timeout: 120 seconds.

Permanent replicables, remote controllers and admitted pending wakes count as presence. Reports
and ownership-fenced heartbeats carry `remoteUsers`. Sweep/offline admission, balancing and
clientless pulls respect it; conflicting pulls return `remote-use`. Co-location is allowed to
end a remote session when the spatial invariant requires moving that content. No Plan B client
reattachment was introduced.

The existing GPS proxy/point-back fixes on main were retained. An explicit id/GPS connection queues bounded intent across the durable-offline-row/antenna-report
race, then directs an offline wake and runs vanilla `ConnectTo` against both real blocks. A
completed online link leaves that pending set; the source minute allowance is spent on an actual
offline admission.
Retarget, disable and timeout cancel it; one source can start at most once per minute.
The arriving real peer is paired at laser registration before vanilla Update10 can idle its
caller. A durable offline row wins over retained source reports until its admitted target
reports live; a bridge ends once the authority publishes the new origin. An established proxy
link does not create a wake demand. IGC does not wake offline PBs: unicast
returns false and no broadcast listener exists there.

## Operator surface

All routes retain the existing node admin-token gate.

- `GET /cluster/offline`: profiling state, confidence, skips/results, cache and call-up counters.
- `GET /cluster/profiles?partition=N`: captured online records, or the exact offline Final's
  records on a holder host. It does not claim a recovery or instantiate entities.
- `POST /cluster/trigger/profile?partition=N`: the normal profiling pass, with its policy gates.
- `POST /cluster/trigger/compensate-dry-run?partition=N&hours=H`: read the exact offline Final
  on that holder host and return modeled results without loading/applying/saving it.
- `POST /cluster/compensate-dry-run?hours=H`: same model for an uploaded MPS file, capped at
  512 MiB input and 0..168 authority hours. No caller-supplied file path is opened.
- `POST /cluster/profiles/cache/clear?key=...`: journaled WA cache invalidation for that key.

`python3 Tools/offline-compensate.py http://NODE:PORT/cluster --partition N --hours 6`
or `--save exact-final.mps` uses that dry-run API. It reads `CLUSTER_ADMIN_TOKEN` from the
environment and never prints it.

New defaults: `offline.compensate=true`, `velocityReadiness=true`, `settleFrames=300`,
`observeSeconds=180`, `rotateProduction=true`, `solarSteps=72`, `cacheTolerance=0.02`,
`unknownBlocks=Freeze`, `longObservation=Skip`, `compensateMaxHours=168`, `callUp=true`,
`callUpSeconds=120`, `callUpPerMinute=6`, `callUpNoticeSeconds=2` (all under `offline.`).
Existing grace/minOnline defaults stay 180/60 seconds.

The Quasar partition/cache UI is a separate repository task. The exact follow-up contract and
clustering-plan patches are prepared outside this checkout for an isolated documentation pass.

## Verification / acceptance map

This remains an implementation draft. All results below come from Linux THE-RIG; a native
fixture, a detached assertion and a full standalone differential are different proof.

The isolated host now works. `CLUSTER_TEST_HOST_ENV` points at a task-owned env file under
`/home/owendb/tm/offline-partitions/native-host`, with copied Magnetar 2.4.3.7, Pulsar 2.4.3,
matching client/server **01_210_014**, compiler, prepared Remote driver, planet archive and actual
TestPlan. Ports use offset 700 and Remote base 24900. Roots/caches/packages are task-owned;
shared sessions and Steam installations were preserved. Doctor passes between runs. Correctness
runs retain the RAM/disk floors; this shared host supplies no exclusive timing benchmark.

Private `Build/release.sh` packages passed their build/check/manifest/binary-hash gates:

| Product commit | Archive SHA256 | Earned native result |
|---|---|---|
| `265626630f4f80d42c5f0c36ef5fecc2ce6afcef` | `73081deb6a13317ecbab7dbc781447d8b04798e29a7879e448afe59cefc7904b` | Survival RC drive and pre-Final WA-local clear/replacement; IGC no-wake; partial terminal production |
| `42fd3a0a2756d00c1869cabf7c732c347bb088e2` | `bdbbaa1f7899990f927693667ed670af6435aa982ba25575c8ec9e23e2bb8ed5` | Actual shared-apply crash/retry and post-Final recovery, T-OC-09 |
| `e76821b5fdd93ea0420f04db08ea398e7c119630` | `485d8b923bac8edc5f6e96eb77e686f5cb0b630d9a762d6479f9b3a87b215ef8` | Full Survival terminal/radio/production/pin-close-offline lifecycle passed; other follow-ups running |

Evidence root: `/home/owendb/tm/offline-partitions/native-host`.

Current private package: **6423447** (full SHA in its manifest), archive SHA256
`58bf6cc64c4787a7da5729054a412224fa5bb739e4136bf894f638ce9891009b`.
This adds durable offline-row precedence over retained source antenna reports and drops the
bridge when the authority publishes its new origin. Native bridge lane uses separate caches,
roots, port offset10000 and Remote34900; no rendering/screenshot or benchmark proof is claimed
there. Primary correctness lane remains at9f128a7/offset700/Remote24900 and its per-package
results stay separate. Both retain resource floors; no shared deployment is touched.

`bridge-results/20261031-205109Z` now passes at6423447: **laser8/8** (actual id wake,
established proxy/no wake, GPS wake, both real ends connected) and **Deny14/14** (ordinary
hostile/no-relay refusals with authoritative promotion0, actual Moderator positive control).
This is native correctness evidence without rendering; the radio-chain-behind-laser leg remains.
The earlier9f128a7 laser7/8 failure drove the durable-row/bridge repair; its Deny13/14 failure
was the final ownership-heartbeat observation race. Both historical failures remain failures.

At9f128a7, `results/20261031-203241Z` also passes **T-OC-11 125/125** (476.6s),
**T-OC-09 14/14**, **T-OC-12 47/47**, **OFF-CACHE-DURABILITY12/12**, and **Rate29/29**.
The warm-output cache fixture now proves twenty real refinery bases use one measured formula,
then exactly one remeasurement after a journaled clear/epoch increment. Crash proof here covers
fuel/production, not actual chip renewal; a fresh test-only extension waits across paid-credit
expiry before killing/retrying shared Apply. Other primary cases are still active.

A focused native paused-zone runner is authored: power-removed bubble/id0, paid credit,
settings/rights through shared recovery, real120s power restoration, and a powered manual-off
control. Initial id0 capture remains a source gap pending its fail/fix/fresh-package proof. An
authored runner is not a native pass.

Prior private package: **9f128a7bc2715cc2f0bbb0522715ffb6b7d39380**, archive SHA256
`d837fc659fc7f4276f092dc2337bb89ab6f3193ae60feb47957b8977eba7f3a4`.
It includes the promotion-map and registration-boundary repairs and is rebased onto main
`dcc444ece0d473eefb516d660b8f2172fd353708`. All23 feature commits rebased without conflict;
`rebase-range-diff.txt` records the unchanged patches. Native suite at
`results/20261031-203241Z` uses this package; pending cases are not passes. Test-only follow-up
30d928f waits for actual refinery output before counting rotations and anchors a second caller's
relay at that caller's position. Source semantic changes require fresh package proof; old
package results below retain their own exact manifests.

- `results/20261031-201018Z/P2-OFF-CALL-RATE`:**29/29 passed** at1063947. Twenty real
  concurrent client requests produced19 refusals/one completion. After an actual minute wait,
  six successive absent-grid requests completed actual terminal/UI wakes in16.573 seconds;
  the seventh was rate-refused and stayed offline. The implicit OFFLINE owner bypass does
  not bypass rate limits. Ordinary-player admission is rerunning with the promotion-map repair.
- `results/20261031-201018Z/OFF-CACHE-DURABILITY`:**12/12 passed** at1063947, strengthened
  WA-local journal-before-success/pre-Final kill/replacement replay with untouched-entry and
  stale/new-epoch controls. This supplements the earlier nine-check2656266 proof.
- `results/20261031-201018Z/T-OC-12`:FAIL. The first electrically working cold refinery had
  not produced yet; its incomplete measurement used the explicit formula fallback. A second
  measured base populated the shared cache. Warm-output readiness is authored for the fresh
  run; clear propagation/epoch remeasurement checks passed separately in the failed run.


Historical rebuilt package with the 125-check zone pass: **1063947af594975ad2fae65259dc39c2ea67ae98**, archive SHA256
`242b88048f67eb11c62ad10aac2ccb8b45588ac322200ace838830bbe1184f3e`.
This includes the common zone/production budget, authority/native-frame conversion, physics-queue
solar fix and explicit laser-intent retention. OFF-SOLAR-NATIVE and the expanded T-OC-11 passed
on that package. Pending arms have no pass implied by its successful build. The prior budget package at
`ef2e01608cf45f883770b91ab768978ee8a3cc30` has archive SHA256
`bf26a0d0f7397974a695468f7a3ffd99b569c3ec9f1c14274c4dd2fc5541868d`.

- `results/20261031-194947Z/OFF-SOLAR-NATIVE`:4/4, native physics-queue crash regression;
  actual and sampled sun-facing panel supply nonzero electricity. This is not a planet differential.
- `results/20261031-194947Z/T-OC-11`:**125/125 checks passed,474 seconds** at1063947.
  Real radius/shape demand, refinery/assembler inputs/outputs, chip renewal, all-sink energy,
  delivered reactor fuel, empty/full-output standby, finite uranium exhaustion/removal,
  solar/battery recovery spanning native120s activation, enabled switch/settings retention,
  ordinary active-zone quiet offlining and true radio/shared wake after paid credit expiry.
  Shared wake debited0.0820199339 uranium, produced2565.08928 refinery units and109 plates,
  removed the unsupported bubble and left the global admin-zone control enabled/uncharged.
  Full four-hour/three-chip standalone differential and broader priority envelope remain.

- `results/20261031-191916Z/OFF-LIVE-DIFFERENTIAL`:7/7, full Auto battery plus short actual
  refinery reference after the clock correction; production agrees within one engine tick.
- `results/20261031-191916Z/T-OC-09`:14/14, extended cost crash proof with a real running
  player zone, reactor, refinery and assembler. First provisional uranium debit0.00900955275,
  SIGKILL before Final, later retry from the same original Final debits0.08629490417 without
  stacking the first attempt; second post-Final recovery has no profile/results to debit again.
- `results/20261031-191916Z/OFF-OBSERVED-POLICY`:10/10, actual external-component means,
  preserved observed source rates and conservative scripted inventory/storage freeze through
  real radio/shared wake. Its1/1 controlled fixture fraction is not a real-world census.
- `results/20261031-193622Z/T-OC-07`:10/10, real Block/Freeze, PB Storage, timer/ejector,
  radio/UI/shared wake, Frozen confidence and modeled battery debit. Previous UI failure stays failed.
- `results/20261031-193622Z/T-OC-11`: whole scenario FAIL,94/95 individual checks. Real
  radius10/20 and Box demand/production/stock/energy/upkeep references and finite-fuel outage
  were exercised. Full-output zero-production check failed on fixed-point residual headroom;
  the corrected bound allows at most one native production tick before standby. Solar backup
  then crashed in the generic worker Havok ray; full dump/report preserved at
  `solar-worker-crash/`. That failure drove the physics-queue fix and focused native regression.
- `results/20261031-193622Z/P2-OFF-CALL-LASER`: FAIL. Live probes show an accepted explicit
  connection against the old online report, then `offline:21` while stuck in contact_Rec with
  no load. This drove retained explicit intent; its fixed native id/GPS/no-wake run is pending.


- `results/20261031-182113Z/T-OC-09`: **13 checks passed**. Hold actual shared Apply before
  instantiation; immutable Final 4:1:2 remains catalogued. First SIGKILL aborts 119.55216 iron of
  provisional credit. Retry on another node reads that original Final and recomputes 1022.46144
  iron for its later interval, without stacking the first credit. New ordinary Final 4:3:1 is
  replicated to a second holder before the owner is killed. Its recovery carries no profile to
  credit again. These are real game processes, builders, catalog/replica transitions and kills.
- `results/20261031-180240Z/P2-OFF-CALL-RC`: **9 checks passed** in Survival, actual authenticated
  take on caller node and **113.88727569580149 m** of server-side drone motion from raw client W.
  Final PNG is complete and visually inspected. Earlier empty PNGs are excluded.
- `results/20261031-180240Z/OFF-CACHE-DURABILITY`: **9 checks passed**. WA-local clear was journaled
  before success; later global Final registration was held, WA killed, replacement replayed clear
  over its pre-clear snapshot. A strengthened rerun also checks untouched entry and old/new epochs.
- `results/20261031-175308Z/P2-OFF-CALL-IGC`: real unicast returned false and target stayed offline.
  This early Creative batch is not production proof; its individual IGC assertions are valid.
- `results/20261031-183835Z/P2-OFF-CALL-TERMINAL`: **full lifecycle passed at e76821b**,
  actual radio/UI/caller node/pin/source capture refusal/Survival compensation/close/offline.
  Its historical credit amount precedes the authority/native-frame conversion fix and establishes
  lifecycle execution only; it is not current production accuracy evidence. Small-station observed open latency **22.313 s**
  on the shared host (not a large-station/exclusive benchmark); complete PNG visually inspected.
- Earlier Survival terminal partial run `results/20261031-180240Z/P2-OFF-CALL-TERMINAL` reached real UI,
  caller-node wake, active pin, source capture refusal `remote-use`, and actual shared credit of
  historical production credit (before the clock fix; execution proof only). Its close leg failed due an old Remote driver; it is not a
  full scenario pass. Driver/source refresh and client antenna readiness retries are now authored.

Native runs also confirmed all three declared production patch method hashes on running node/WA
logs ("Verified 3 patch method(s) against declared body hashes"). Resolved hashes remain
`d24b196b`, `a90270cc`, `d6d1e890` against the installed game build.

R1/R2/R3 regressions failed before their repairs and pass after; the original 28 detached model
assertions still pass. Added cases cover Auto full-state transition, downstream production input
thresholds/conservation, electrically gated tank discharge/charge, and positive controls. Cold
game-math tests cover explicit Forward-Z solar pivots and exclusion of Repeat assemblers from
queue-shrinkage measurement. Save-container compatibility/structure assertions pass. Registry
`remote-use` covers no-report/liveness, sampled generations, explicit empty records and missing
records; the final missing-record regression failed before e76821b and passes after. None of
these is a native planet ray, six-hour reference, UI conflict or performance measurement.

Useful commands (from repository root unless otherwise shown):

```sh
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never
dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test remote-use
dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test offline-partitions
# From Test/, with CLUSTER_TEST_HOST_ENV set to an isolated host contract:
uv run python doctor.py --json
uv run python run_suite.py T-OC-09 OFF-CACHE-DURABILITY P2-OFF-CALL-TERMINAL
uv run python -m unittest test_offline_config test_bench_artifacts -v
```

The suite builds the test plugin against the exact package. Native fixture fixes include Survival
world selection, preserving prior config overrides, proper terminal demand/close bookkeeping in
an isolated Remote copy, complete-PNG detection and failure records for interrupted scenarios.
Old zero-check interrupted `result.json` files are excluded. Failed native runs stay failed;
compiled/authored fixtures are not promoted to executed proof.

| Required scenario | Earned proof / remaining obligation |
|---|---|
| T-OC-01 | R1 detached fail/pass and short native full-Auto/refinery reference passed at ef2e016; full six-hour standalone differential and uranium tolerance missing |
| T-OC-02 | R2 detached fail/pass and actual Survival refinery wake/stock conservation; full upgraded three-ore/two-refinery/output-full standalone differential missing |
| T-OC-03 | Cold Forward-Z game-math regression; native planet/ridge/own-grid obstruction differential missing |
| T-OC-04 | Electrically gated tank regressions and coupled stock checks; native exhaustion order/timing and cold-bootstrap differential missing |
| T-OC-05 | Real-world observed-group fraction and Measure decision pending; no speculative role added |
| T-OC-06 | Native external-logic short-window means and conservative storage/inventory fallback passed,10 checks at ef2e016; full-rotation/Measure acceptance missing |
| T-OC-07 | Native Block/Freeze, PB/timer/ejector, radio/UI/shared wake, Storage/Frozen confidence and modeled battery debit passed,10 checks at ef2e016 |
| T-OC-08 | Real rotor top and independent quiet production sibling runner authored; execution pending |
| T-OC-09 | Extended native zone+reactor+refinery+assembler Apply/fuel debit/pre-Final kill/retry/ordinary Final/second-kill no-repeat passed,14 checks at ef2e016; long-cap differential remains separate |
| T-OC-10 | Real locally staged changed-mod-byte wake/healthy-skip runner authored; execution pending |
| T-OC-11 | Expanded native125-check combined zone/production/fuel/chip-renewal/outage/backup/true-radio-shared-wake fixture passed at1063947. four-hour/three-chip standalone differential and broader producer envelope still required |
| T-OC-12 | Native twenty-base run exposed cold readiness and replica propagation assumptions; corrected runner compiled; acceptance rerun pending |
| T-OC-13 | Parked large dynamic grid true radio wake/pose runner authored; execution pending |
| PRF-OC-01 | 200-panel planet fixture and exclusive frame-budget measurement missing |
| P2-OFF-CALL-TERMINAL | Full actual Survival radio/UI/caller-node/pin/source refusal/production/close/offline lifecycle passed at e76821b |
| P2-OFF-CALL-RC | Actual Survival authenticated take, presence and dynamic raw-input drone drive passed at 2656266 |
| P2-OFF-CALL-DENY | Real authenticated negative packet runners authored; server-outcome/relay/spatial setup corrected; rerun pending |
| P2-OFF-CALL-RATE | Native29-check burst plus six actual terminal completions in16.573s/seventh-refusal passed at1063947; fresh ordinary-player/role-repair run pending |
| P2-OFF-CALL-CONFLICT | Two-live-client/different-node/two-grid/three-minute no-ping-pong runner authored; execution pending |
| P2-OFF-CALL-LASER | Id wake establishes both real lasers at9f128a7; same-origin retained-report/established-link leg failed and drove6423447. Fixed GPS/established no-wake and radio-chain proof pending |
| P2-OFF-CALL-IGC | Actual unicast false/no load passed at 2656266 |

`Test/coverage.py` maps all 21 required IDs. Fourteen have native runners (some still partial or
unrun); seven retain explicit fixture/decision/performance gaps. Extra native regressions
`OFF-CACHE-DURABILITY`, `OFF-OBSERVED-POLICY`, `OFF-LIVE-DIFFERENTIAL` and `OFF-SOLAR-NATIVE` state their narrower scope.
A short clustered-engine comparison is not the full standalone/three-baseline differential lane.

Large-station frozen window, bytes, handover traffic, call frequency/conflicts and latency remain
unmeasured at the required scale. Quasar UI and real-world Measure evidence remain parent-owned
follow-ups. These obligations block declaring either ticket accepted.

## Rollback boundary

No live/deployed cluster was touched. Before any future deployment, retain a pre-change cluster
snapshot and binary package. Old readers cannot read profile-bearing v2 Finals or the newly
appended WA cache section. `offline.compensate=false` stops new profiling/credit; it is not a
binary-format downgrade tool. Rollback must restore a compatible snapshot/package together.


## Follow-up native failures and pending repairs

At1063947, an explicit laser now directs a real load onto its source node, but pairing still
failed. Test-only native state tracing at fixture81544eb captured vanilla
`TryUpdateTargetCoords` (inlined in Update10) idling the source immediately after the idle real
peer entered the antenna dictionary, before the1s service update could pair it. Pairing is now
authored at the real `AddLaser` completion boundary, using the existing idle-peer/first-caller
`PointBack` path and its vanilla ownership/synchronization. Fresh rebuilt-package native proof
is pending; no unconditional reconnect loop or relaxed cancellation was introduced.

Hostile/no-relay requests at1063947 were accepted because the backend session is OFFLINE:
vanilla `GetUserPromoteLevel` returns Owner for every nonzero id, even with promoted-map entry0.
This is not a valid normal-player bypass policy. The new source uses the existing replicated
promotion map through `PromotionService.IsModerator` for radio and respawn admission, and
consolidates the existing online-player predicate there. Standalone calls retain vanilla role
semantics. The native negative fixture now asserts the actual promotion-map entry and includes
an explicitly promoted moderator positive control. Fresh native refusal/bypass proof is pending.

An exploratory stale-settings fixture failed its precondition: native serialization already
remembered the latest live radius/shape/rights. It did not reproduce a settings defect. The
speculative priority change and redundant fixture were removed; that failed precondition is
preserved in results/20261031-201018Z/OFF-ZONE-SETTINGS and is not an acceptance or defect proof.
