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
frames. Storage/gas convert at 60 frames/second, refineries use 16 ms/frame, and assembler
formula times retain the engine's rounded unit duration. Sun changes add no production time;
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
and safe-zone chips are represented. A zone exhausting upkeep is disabled, its zone ID cleared,
and its carried zone entry suppressed. Creative fills storage without measuring; simplified
simulation serves all demands while modeled fuel still burns.

Throughput observes ordinary engine timer ticks. Refineries rotate a selected input stack;
assemblers rotate only between units with enough input/output headroom. Original inventory/queue
order is restored. Incomplete/stock-limited measurement leaves an uncached formula and
`Approximate` confidence. A measured formula disagreement remains per-block rather than becoming
a shared exact rate. The WA publishes and persists the cache. Clearing a key increments its
epoch so stale contributions cannot resurrect it.

Solar/farm sampling snapshots pivots, transforms and planet geometry on the game thread and
ray-tests on workers without changing the world's sun. Armor/partial-block edits, group pose,
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

## Radio / laser admission and presence

Only an authenticated client's explicit absent-replicable add request starts radio call-up.
The original endpoint, interacted entity, medical ID and pin flags survive. Remove requests
cancel pending work. Proxy relay reach and friendly ownership/remote sharing are checked before
waking; moderators bypass those checks, never rate limits. The service permits one pending
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

The existing GPS proxy/point-back fixes on main were retained. An explicit id/GPS connection to
an offline laser queues a directed wake, then runs vanilla `ConnectTo` against both real blocks.
Retarget, disable and timeout cancel it; one source can start at most once per minute.
An established proxy link does not create a wake demand. IGC does not wake offline PBs: unicast
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
  Credited **434.79072 iron over 1422 frames**. Small-station observed open latency **22.313 s**
  on the shared host (not a large-station/exclusive benchmark); complete PNG visually inspected.
- Earlier Survival terminal partial run `results/20261031-180240Z/P2-OFF-CALL-TERMINAL` reached real UI,
  caller-node wake, active pin, source capture refusal `remote-use`, and actual shared credit of
  **449.16144 iron over 1469 frames**. Its close leg failed due an old Remote driver; it is not a
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
| T-OC-01 | R1 detached fail/pass; short native live-engine battery/refinery comparison authored; full six-hour standalone differential and uranium tolerance missing |
| T-OC-02 | R2 detached fail/pass and actual Survival refinery wake/stock conservation; full upgraded three-ore/two-refinery/output-full standalone differential missing |
| T-OC-03 | Cold Forward-Z game-math regression; native planet/ridge/own-grid obstruction differential missing |
| T-OC-04 | Electrically gated tank regressions and coupled stock checks; native exhaustion order/timing and cold-bootstrap differential missing |
| T-OC-05 | Real-world observed-group fraction and Measure decision pending; no speculative role added |
| T-OC-06 | External native game-logic short-window/frozen-fallback fixture authored; full-rotation/Measure acceptance missing |
| T-OC-07 | Real Update100 PB, timer and ejector Block/Freeze reached native profiling/offline; full wake/storage/UI rerun pending |
| T-OC-08 | Real rotor top and independent quiet production sibling runner authored; execution pending |
| T-OC-09 | Native shared Apply/pre-Final kill/retry/replicated ordinary Final/second-kill no-repeat passed at 42fd3a0; long-cap differential remains separate |
| T-OC-10 | Real locally staged changed-mod-byte wake/healthy-skip runner authored; execution pending |
| T-OC-11 | Detached chip exhaustion check; real safe-zone removal/upkeep differential missing |
| T-OC-12 | Native twenty-base run exposed cold readiness and replica propagation assumptions; corrected runner compiled; acceptance rerun pending |
| T-OC-13 | Parked large dynamic grid true radio wake/pose runner authored; execution pending |
| PRF-OC-01 | 200-panel planet fixture and exclusive frame-budget measurement missing |
| P2-OFF-CALL-TERMINAL | Full actual Survival radio/UI/caller-node/pin/source refusal/production/close/offline lifecycle passed at e76821b |
| P2-OFF-CALL-RC | Actual Survival authenticated take, presence and dynamic raw-input drone drive passed at 2656266 |
| P2-OFF-CALL-DENY | Real authenticated negative packet runners authored; server-outcome/relay/spatial setup corrected; rerun pending |
| P2-OFF-CALL-RATE | Twenty real reachable offline bases/concurrent-pending runner authored; full successive per-minute completion leg still missing |
| P2-OFF-CALL-CONFLICT | Two-live-client/different-node/two-grid/three-minute no-ping-pong runner authored; execution pending |
| P2-OFF-CALL-LASER | Id/GPS/established-link no-wake native runner authored; execution and radio chain behind offline laser pending |
| P2-OFF-CALL-IGC | Actual unicast false/no load passed at 2656266 |

`Test/coverage.py` maps all 21 required IDs. Thirteen have native runners (some still partial or
unrun); eight retain explicit fixture/decision/performance gaps. Extra native regressions
`OFF-CACHE-DURABILITY`, `OFF-OBSERVED-POLICY` and `OFF-LIVE-DIFFERENTIAL` state their narrower scope.
A short clustered-engine comparison is not the full standalone/three-baseline differential lane.

Large-station frozen window, bytes, handover traffic, call frequency/conflicts and latency remain
unmeasured at the required scale. Quasar UI and real-world Measure evidence remain parent-owned
follow-ups. These obligations block declaring either ticket accepted.

## Rollback boundary

No live/deployed cluster was touched. Before any future deployment, retain a pre-change cluster
snapshot and binary package. Old readers cannot read profile-bearing v2 Finals or the newly
appended WA cache section. `offline.compensate=false` stops new profiling/credit; it is not a
binary-format downgrade tool. Rollback must restore a compatible snapshot/package together.
