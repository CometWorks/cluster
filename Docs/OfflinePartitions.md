# Offline wake implementation draft (SE-346 / SE-347)

This branch is **not acceptance complete**. It implements wake-time compensation and antenna
call-ups in the production `Shared/Cluster` / `ClusterRegistry` architecture. Native proofs below
establish their stated scope only. No deployment, release or complete GUI/performance acceptance is
claimed.

## Lifecycle and ownership

Quiet logical groups settle and profile during the normal offline grace. A moving sibling blocks
offlining the partition, while quiet groups retain their profiling state. Velocity, acceleration and
self-propulsion checks replace the large-grid sleep flag by default. Online characters/controllers,
joining seats, remote pins, imports, saves, fences, drain, proximity and fast-partition guards
remain. Encounter content is excluded. The old sleep gate is available through
`offline.velocityReadiness=false`.

Immediately before physics is disabled, capture resource ports, storage, inventory stacks, conveyor
paths, recipes/queues, upgrades, timed upkeep and the final authority-frame stamp. Profiles travel
**inside the checksummed Final**, in an MPS version 2 entity/profile section pair. Version 1 saves
remain readable and ordinary saves remain version 1. No sidecar or registry-only inventory truth is
introduced. The offline row carries `OfflineGameFrame` and `ProfileSummary`; its Final reference
remains the durable source.

Every directed wake (approach, join, respawn, radio, laser) uses the existing recovery claim and
`LoadRecovery`. After cover merge and before instantiation, apply only profiles whose entire group
came from that cover entry. Wait for the authority clock/offset and asynchronous mod-content
fingerprint before evaluating a profile. Definitions, mod bytes, game version, upgrades captured in
rates, session multipliers and relevant environment inputs fence the model. A mismatch skips
compensation with an explicit diagnostic. Source files are immutable.

The model runs on detached profile state. Builder writes are prepared and validated as a transaction
per group, including inventory stock identity, mass/volume bounds and retained item metadata/IDs. An
apply failure leaves that group's builders untouched. A crash before the new-generation save reloads
the original Final and recomputes; a catalogued ordinary Final contains the compensated state and no
profile to credit again. The existing deferred-Final recovery cover behavior is retained.
`CompensatedTo` is reported in the node result; offline rows already end at the first member
heartbeat in the current registry, so no durable claim is made that a vanished row retains that
diagnostic.

No component advances offline partitions continuously. The interval is `floor((WA elapsed
milliseconds - sun offset) / 16)` frames; a full authority hour is 225,000 frames. Captured profiles
convert authority units to native engine frames before applying rates: 225,000 authority units/hour
become216,000 engine frames/hour. Storage/gas convert at60 frames/second, refinery formulas use16 ms
per native frame, and assembler formulas retain the engine's rounded unit duration. Native reference
testing found and corrected the earlier4% duration error. Sun changes add no production time; the
captured sun phase advances with real authority frames and admin sun steps take effect at interval
end. The default 168 **authority-hour** cap forfeits excess time rather than banking it for another
wake. Shutdown wall time earns nothing.

## Modeled behavior and conservative policies

The detached model allocates grouped sinks by priority (whole-group blackout or proportional
adaptible shortage), charges storage after ordinary sinks, dispatches paired reservoir sources
before ordinary producer priority groups, and does not recharge auto storage from extra discharge of
its peers. Events include storage/fuel boundaries, inventory capacity, production units/stacks, sun
breakpoints and upkeep deadlines. Shared inventory indices prevent multiple producers from
independently spending the same stock.

Reactor fuels, ice/generator/gas/tank/engine chains, battery charge loss, jump charging, damaged
tank leakage, refinery ordering, assembler whole units/repeat/disassembly, survival-kit efficiency
and safe-zone chips are represented. A zone exhausting upkeep loses its bubble, its zone ID is
cleared, and its carried zone entry is suppressed; the functional block stays enabled and retains
its settings/rights for later recovery. Creative fills storage without measuring; simplified
simulation serves all demands while modeled fuel still burns.

Throughput observes ordinary engine timer ticks. Refineries rotate a selected input stack;
assemblers rotate only between units with enough input/output headroom. Original inventory/queue
order is restored. Incomplete/stock-limited measurement leaves an uncached formula and `Approximate`
confidence. A measured formula disagreement remains per-block rather than becoming a shared exact
rate. The WA publishes and persists the cache. Clearing a key increments its epoch so stale
contributions cannot resurrect it.

Solar/farm sampling snapshots pivots, transforms and planet geometry on the game thread and
ray-tests through the engine physics queue without changing the world's sun. Generic .NET workers
must not invoke synchronous Havok rays: a native crash exposed that invalid context. The sampler now
uses `CastChainedRaycastsParallel`, one direction/panel/pass, under the engine's physics read state
and native thread initialization. Armor/partial-block edits, group pose, mod content and sun basis
invalidate the curve. Wind uses settled placement and atmospheric speed with weather multiplier one.
Planet/farm point fidelity and the exclusive sampling budget are separate checks; current outcomes
are below.

Script-driven resource rates get short-window means/variance; `InPlace` can wait a full sun
rotation, while the default `Skip` uses the short window. PB/timer/ejection execution is never
replayed. `Freeze` leaves opaque behavior unchanged while modeled siblings compensate; `Block` keeps
the partition online. `Mean` handles observable resource rates, but inventory changes that cannot be
separated from conveyor transfers freeze that block with a diagnostic rather than inventing an
inventory source/sink. Ordinary reactor fuel settlements are now observed at the guarded native
ConsumeFuel boundary and normalized out of its inventory fingerprint. Conveyor pulls, arbitrary
additions/removals, extra inventories and bottle-state edits keep the conservative fingerprint. This
observer never adds a second fuel debit; the model uses the captured native timer and stock
semantics. The full InPlace learning and independent native reference now pass for a varying source
and genuinely varying ModAPI reactor fuel, with an opaque-inventory negative control; the
initialized cluster scheduler and representative-world census remain unproved. No Measure node was
added: real Octahedron observed-world fractions are unavailable, so phase 6 remains an
evidence-gated decision.

The model refuses a partial result after its fixed event budget. It reports the skip; it does not
silently apply an incomplete interval. Arbitrary script inventory semantics, assembler cooperation,
refinery per-tick ore-switch under-credit and changing external occluders are not claimed to
reproduce engine replay.

## Required running safe-zone and common power budget

Running player-built safe zones are required SE-347 scope. Their actual engine sink supplies
shape/radius-dependent operational draw; refinery/assembler idle versus working demand, upgrades,
actual block and grid-system consumers and storage charging share that same group allocation.
Capture enumerates actual distributor ports once per grid, deduplicates block-exposed ports and
includes gyro/conveyor/emissive and other registered electrical sinks. Native gyro runtime
multipliers invalidate the profile. Dispatch honors source/consumer priorities, renewable
contributions, battery modes/recharge losses, fuel and inventory bounds. Fuel is consumed according
to each producer's delivered output and captured definition rate, rather than its nameplate maximum.
Results include per-producer energy/fuel, per-consumer energy, storage input, production units, chip
renewal and zone state. There is no separate accounting service, second debit layer or background
offline simulation. Gas generators now select standby versus operational draw from delivered gas in
the same allocator; oxygen/hydrogen ports share one electrical sink. Power/production multipliers
and the oxygen setting remain respected. A nonadaptible blocked working draw cannot bootstrap gas
production. Positive partial input powers adaptible groups according to the native predicate; output
is not assumed to scale linearly. Engine gas intake and electrical dispatch settle together
independently of resource enumeration order. Older profiles without these optional rates retain
their captured constant demand. The expanded 39-check shared-budget proof is detached; the fresh
native gas-standby comparison is separate.

Vanilla only advances upkeep while the block works. Power loss removes the modeled bubble and pauses
paid credit. Recovery recreates it through the native activation delay, then resumes credit/chip
renewal; chip exhaustion removes the bubble while retaining the functional switch, settings and
rights. `BlockSafeZones` already carries zone XML in the grid builder: source close removes the old
bubble, and compensation changes the carried state before target instantiation. Cold incoming active
bubbles begin inactive while the carried desired entry/paid credit waits for thaw and native
electrical-group capacity. Native activation and an actual allocation check restore protection only
with real supply; saved builders and pending offline captures preserve the desired state and costs.
Native functional On/Off cancels pending restoration. Unsupported bubbles cannot be restored
enabled. Active bubbles do not independently block quiet offlining; the existing
approach/interaction wake path remains responsible for loading content before interaction. Admin
zones follow their separate global-state rules and incur no player-block chip or producer-fuel
debit.

Capture also records an enabled zone already paused by power loss, with id0 and retained paid
credit/settings. It uses the paused manager credit, not its stale serialized wall deadline. A
powered manually removed zone stays off. A restored modeled bubble uses the existing carried-zone
path, retaining its former entity id and rights; native
id0/credit/settings/rights/activation/manual-off controls pass. A standalone native interval now
demonstrates natural solar restoration during the modeled offline interval, including native
activation, paid-credit quantization and the shared energy budget. Actual cluster environmental-restoration shared wake now passes17/17 with cold-producer admission, pending save/capture and native functional-off controls. Unpowered production keeps its formula fallback rather than waiting
forever for a stopped timer. Reactor profiles retain the native900..3600-frame fuel settlement phase
and tier transition; the model samples delivered output at that boundary and carries the unpaid
phase into the restored builder. Three six-hour standalone baselines now establish
Auto/Recharge/Discharge/renewable priority stock fidelity within1% of actual stock. They do not
certify every possible rapid load waveform.

## Radio / laser admission and presence

Only an authenticated client's explicit absent-replicable add request starts radio call-up. The
original endpoint, interacted entity, medical ID and pin flags survive. Remove requests cancel
pending work. Proxy relay reach and friendly ownership/remote sharing are checked before waking;
moderators bypass those checks, never rate limits. Cluster moderator bypasses read the replicated
promotion map; vanilla OFFLINE backend Owner shortcuts do not grant a bypass. The service permits
one pending request per client, a 10-second entity cooldown and six offline admissions per minute.

A directed request loads onto the caller's node and reasserts while pending; an unavailable caller
is refused without redirecting. A same-node approach refresh preserves the reservation. A second
caller cannot steal a live reserved remote/laser target. Once local, vanilla rights are checked
before pinning and forcing replication (also for online pulls). A brief receiver-settle retry covers
the Update100 gap between entity registration and antenna graph readiness. HUD notices start after
two seconds, repeat every ten, and report failure at denial/timeout. Default timeout: 120 seconds.

Permanent replicables, remote controllers and admitted pending wakes count as presence. Reports and
ownership-fenced heartbeats carry `remoteUsers`. Sweep/offline admission, balancing and clientless
pulls respect it; conflicting pulls return `remote-use`. Co-location is allowed to end a remote
session when the spatial invariant requires moving that content. No Plan B client reattachment was
introduced.

The existing GPS proxy/point-back fixes on main were retained. An explicit id/GPS connection queues
bounded intent across the durable-offline-row/antenna-report race, then directs an offline wake and
runs vanilla `ConnectTo` against both real blocks. A completed online link leaves that pending set;
the source minute allowance is spent on an actual offline admission. Retarget, disable and timeout
cancel it; one source can start at most once per minute. The arriving real peer is paired at laser
registration before vanilla Update10 can idle its caller. A durable offline row wins over retained
source reports until its admitted target reports live; a bridge ends once the authority publishes
the new origin. An established proxy link does not create a wake demand. IGC does not wake offline
PBs: unicast returns false and no broadcast listener exists there.

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

`python3 Tools/offline-compensate.py http://NODE:PORT/cluster --partition N --hours 6` or `--save
exact-final.mps` uses that dry-run API. It reads `CLUSTER_ADMIN_TOKEN` from the environment and
never prints it.

New defaults: `offline.compensate=true`, `velocityReadiness=true`, `settleFrames=300`,
`observeSeconds=180`, `rotateProduction=true`, `solarSteps=72`, `cacheTolerance=0.02`,
`unknownBlocks=Freeze`, `longObservation=Skip`, `compensateMaxHours=168`, `callUp=true`,
`callUpSeconds=120`, `callUpPerMinute=6`, `callUpNoticeSeconds=2` (all under `offline.`). Existing
grace/minOnline defaults stay 180/60 seconds.

The authenticated Gateway AdminContract0.9.0 / wire1 seam advertises
`features.offlinePartitions="v1"`. Older nodes explicitly refuse unsupported operations. Quasar
PR317 at84f6bfd vendors the exact bb65a32 DTOs; later boundary repairs keep them unchanged. Gateway
routes are:

- GET `/admin/v1/offline/partitions`, `/offline/details?node=N&partition=P`, `/offline/cache`.
- GET `/admin/v1/offline/prediction?node=HOLDER&partition=P&hours=H`, finite0..168 hours.
- POST `/admin/v1/triggers/profile?node=OWNER&partition=P`.
- POST `/admin/v1/offline/cache/clear` with `{key,expectedEpoch}`.

Reads require query scope; mutations require manage scope and Idempotency-Key. Routing resolves
exact registry owner/Final-holder identities without caller URLs/paths or node credentials.
Reads/prediction never load, claim, apply, save or wake. Profile revalidates its original target
after preflight and before success; native admission requires serving, unfenced, current local
generation and non-frozen members before Prepare. Cache clear uses the original operation GUID
through the existing WA ledger and epoch CAS. A failure after dispatch settles as
`offline_clear_uncertain`: retry the SAME key and body to reconcile that identity. It never asserts
no effect after a lost/malformed reply. Terminal success/CAS mismatch replay normally.

Node metadata is bounded to8MiB with deadline/cancellation on stream acquisition and every read;
this includes the earlier peer-rejection reader. Timeout/size/schema/transport failures use stable
errors. Uploaded Finals retain their separate512MiB limit. Existing journal policy can fail open
when disabled/unsupported or after1500ms; this work does not establish unconditional durability.
Actual non-loopback Gateway bearer proof passes21/21 with missing/query/manage token and named audit
controls, including the real source-release race. Interactive Quasar/Blazor service proof remains
absent. Static renders and cold HTTP tests retain their narrower credit.

## Verification and current acceptance status

**Acceptance remains incomplete.** Linux THE-RIG has a working isolated native host. Correctness
runs preserve10GiB available RAM and100GiB disk; no exclusive frame/CPU claims are made on this
shared machine. Game01_210_014, task-owned Magnetar/Pulsar copies, roots/caches/ports and exact
package hashes are recorded under `/home/owendb/tm/offline-partitions/native-host`. No live cluster,
shared interactive install, Steam update, deployment or release was touched.

Canonical evidence: `/home/owendb/tm/offline-partitions/implementation-evidence.json`;
`review-round1-response.md`, `review-round2-response.md`, `quasar-ui/review-round1-response.md` and
`quasar-admin-seam.md`. Original reviews, failed/interrupted runs and old-product results remain
immutable. They are not promoted to latest-head proof. Historical detailed logs formerly repeated
here remain in Git and `offline-partitions-doc-before-consolidation-3a033aa.md`; current results are
consolidated below.

Every row names its **actual tested product**. Current source c307bba is based on main10fd567;
`rebase-10fd567-range-diff.txt` preserves61/61 ticket commits unchanged. The earlier main-deb6b69
rebase likewise preserves53/53 patches. The latest main changes offline-grid mass reporting, not
compensation/admin semantics. The fresh c307bba private package passes all release gates; its actual environmental shared-wake17/17 result is separate from the historical rows below. Earlier rows never
imply that an old package contains subsequent repairs.

| Actual product / native result under native-host | Earned scope |
|---|---|
|8d9912e / results/20261031-221514Z | Q1 actual distributor census, nonoverride gyro enabled/disabled battery debit12/12; combined zone/fuel/production/chips/shared wake125/125 |
|8d9912e / gas-results/20261031-222137Z | Definition-only adaptible generator positive partial input, zero/bootstrap and constant Factory negative controls |
|3a033aa / gas-results/20261031-225713Z-standalone | Heavy-gyro versus disabled-gyro real reactor fuel differential2/2; native3.2003202MW versus0.20032012MW, uranium9.653303 versus modeled9.653298973 |
|b51c3b7 / gas-results/20261031-230816Z-standalone | Upgraded three-ore/two-refinery/conveyor/downstream queued assembler and output-full differential2/2 over36000 real frames |
|b51c3b7 / gas-results/20261031-231140Z and231659Z | Frozen new-pin/source-transfer denial10/10; actual spatial co-location exemption10/10, each with live client controls |
|7b92c60 / bridge-results/20261031-233440Z-standalone | Four-hour864000-frame/three-chip zone reference; native/model uranium80.000070, chips0, bubble removed, functional switch retained |
|7b92c60 / gas-results/20261031-235337Z | Full laser id/GPS/established no-wake plus two-hop radio chain behind offline laser12/12; broken-middle negative and restored-path positive |
|72992ec / results/20261031-224058Z/OFF-CACHE-DURABILITY | WA epoch CAS/replay/stale refusal/journal-before-ack/pre-Final kill/replacement15/15; inherited fail-open policy explicit |
|a19514c / bridge-results/20261101-000429Z-standalone | Three independent1296000-frame six-hour baselines,12/12 strict one-percent native stock comparisons; normal versus bounded-fast native engine-time equivalence |
|a19514c / bridge-results/20261101-002017Z and002317Z-standalone | Five coupled gas cases with oxygen on/off: final stocks, ICE→TANK→ENGINE order, exhaustion timing, real empty-engine battery bootstrap, cold negative, disconnected circuit and Factory blackout;160-frame native tick/sample bound |
|a19514c / bridge-results/20261101-002031Z-standalone | Natural solar restoration during108000-frame zone interval; native activation/settings/actions, supported bubble, real common energy; paid-credit error892.98frames within6099.9996-frame sun-bin+Update100 bound |
|a19514c / results/20261101-002016Z/OFF-ADMIN-PROFILE-RACE | Actual non-loopback bearer query/manage/missing-token/CAS/replay/audit and held-preflight/committed-handover/held-source-release refusal21/21 |
|a19514c / results/20261101-001523Z/T-OC-09 | Shared zone/fuel/production/chip pre-Final kill/retry and ordinary-Final second kill18/18; separate admin setup failure in that run stays failed |
|a19514c / bridge-results/20261101-004309Z/T-OC-11 | Fresh combined common-power/zone/production/outage/backup/shared-wake132/132 after native sun-boundary repair; source claim acknowledgement is an explicit precondition |
|a19514c / results/20261101-003112Z/OFF-LARGE-ONLINE-PULL | Three actual terminal pulls of10203 native blocks,24/24 controls; raw freeze/bytes/transport/latency measurements on shared host |
|79a67df / bridge-results/20261101-012523Z-standalone | Full450000-frame Prepare/InPlace learning plus450000-frame independent observed varying-source/ModAPI fuel/opaque-inventory reference passes;4501samples, fuel0.833338/0.833375 within1% |
|89dffe9 / results/20261101-012931Z/T-OC-09 | Cold-zone repair package shared pre-Final retry/ordinary-Final second-kill18/18 |
|c636a46 / results/20261101-014046Z/T-OC-11 | Latest combined common-budget/radius/shape/production/fuel/chips/outage/backup/shared-wake140/140, atomic game-thread reference capture |
|c636a46 / gas-results/20261101-014432Z/OFF-ZONE-PAUSED | Native paused id0/paid-credit/shared recovery/settings/120s activation/manual-off15/15 |
|c307bba / results/20261101-020139Z/OFF-ZONE-ENVIRONMENT | Actual environmental shared-wake17/17: cold native supply/no unsupported active frame, pending save/capture paid credit/costs, settings/rights and real functional-off cancellation |

The standalone engine advances stocks independently while ClusterNode is a dormant capture/model
library. Bounded native max-speed pacing was compared with3600 ordinary60Hz updates: gameplay and
elapsed-sun counters and battery/fuel endpoints agree. No update is skipped and no sun/time counter
is assigned.1296000 engine updates are six resource hours; the native elapsed-sun clock
advances5.76h, and the model boundary explicitly maps them to1350000 authority units. The energy
cases disable sun rotation; planetary and full-rotation observed cases are separate.

Native traces exposed and repaired paired-source priority, queued assembler working draw, sampled
reactor fuel phase/tier and fractional sun-bin livelock. Each root has a
failing-before/passing-after regression. R1–R8 were independently resolved at immutableb1e393f for
their specific defects; Q1–Q3 and U1–U3 repairs have exact responses and proof artifacts. No
subsequent independent review of the final combined source is claimed. A failed first
reactor-observer run also exposed representation-sensitive decimal stock hashes; canonical G29
formatting repairs that fingerprint. The full 450000-frame learning plus 450000-frame native
reference subsequently passes: actual/model fuel debit0.833338/0.833375 within1%, battery
error−0.0000297683MWh, nonzero rate variance and opaque inventory freezing. Original
constant-clamped and first fingerprint failures remain failed.

| Required scenario | Earned proof / remaining obligation |
|---|---|
|T-OC-01|Three six-hour standalone baselines and12/12 strict stock cases above; broad arbitrary rapidly varying load waveforms remain outside that envelope|
|T-OC-02|Full upgraded3-ore/two-refinery/conveyor/output-full/queued downstream assembler reference above; native ore-switch tolerance is one captured tick per switch|
|T-OC-03|Loaded-voxel ridge/own-grid/nearby obstruction/panel/farm675000-frame geometry point reference passes872 comparisons at max0.001161; ordinary live rolling-pivot max0.165844 exceeds the original0.02 bound, so full T-OC-03 remains incomplete|
|T-OC-04|Five-case native coupled stock/trajectory/oxygen-toggle/exhaustion/cold/circuit proofs above; variable Factory standby/working cycling still differs from the steady model|
|T-OC-05|Only controlled test worlds available; no safe representative observed-group census or Octahedron access; no speculative Measure role|
|T-OC-06|Actual Prepare/InPlace450000-frame learning plus independent450000-frame native varying source/fuel/opaque-inventory reference passes at79a67df; initialized cluster scheduler and representative-world census remain unproved|
|T-OC-07|Native Block/Freeze/PB/timer/ejector/storage policy/shared wake10/10 at ef2e016; arbitrary script stock semantics remain conservatively Frozen|
|T-OC-08|Native6/6 at9f128a7: moving rotor top, quiet sibling and stopped-rate reuse|
|T-OC-09|Fresh shared crash/retry/ordinary-Final no-repeat18/18 above; long-cap reference distinct|
|T-OC-10|Changed actual mod bytes7/7 at4109242; real asynchronous-readiness race repaired|
|T-OC-11|Four-hour/three-chip native reference, combined140/140 and paused15/15; fresh c307bba actual environmental shared-wake17/17 additionally proves cold supply, pending save/capture and native functional-off cancellation|
|T-OC-12|Twenty real refinery bases share cache formula and journaled epoch-clear controls47/47 at9f128a7|
|T-OC-13|Parked large dynamic grid, actual radio wake and unchanged pose5/5 at9f128a7|
|PRF-OC-01|Exclusive200-panel per-frame sampling budget unavailable on this shared host; no exclusive claim|
|P2-OFF-CALL-TERMINAL|Actual Survival radio/UI/caller-node/pin/refinery/close/offline12/12 at9f128a7; actual source-pin capture race10/10 at4109242|
|P2-OFF-CALL-RC|Native take/presence/raw-W dynamic drone drive112.946m9/9 at9f128a7|
|P2-OFF-CALL-DENY|Ordinary hostile/no-relay denial and authoritative Moderator positive14/14 at6423447|
|P2-OFF-CALL-RATE|Twenty concurrent requests, real minute wait, six completed wakes/seventh refusal29/29 at9f128a7|
|P2-OFF-CALL-CONFLICT|Two live clients/different nodes/two grids/three-minute no-ping-pong and retained first terminal23/23 at9f128a7|
|P2-OFF-CALL-LASER|Full offline-laser/two-radio-hop chain and break/restore controls12/12 above; main-deb6b69 affected-path rerun12/12 at2495d4b, bridge-results/20261101-005401Z|
|P2-OFF-CALL-IGC|Native unicast false/no load5/5 at9f128a7 and fresh main-deb6b69 affected-path rerun5/5 at2495d4b, bridge-results/20261101-005401Z|

The large station's three raw source-freeze→target-physics-thaw intervals are851.990,460.845 and
277.054ms. Snapshot payloads are57565 sector bytes and92236..92297 network-state bytes, with no
client-state payload. Terminal-visible latency is5.796,54.173 and52.322s; later pulls include the
ordinary gateway partition cooldown. Gateway node-byte deltas are52396,462909 and444952; external
relay aggregate bidirectional deltas are374205,2578805 and2446841. These observation windows include
ordinary node/global traffic and cooldown time, so they are **not transfer-only byte counts**. Only
three synthetic station observations were made: no population percentile, production conflict rate
or week-long usage frequency is inferred. Full transport snapshots/monotonic traces remain in
`large-online-pull-samples.json` for review.

Known unresolved implementation/proof limits are Factory power cycling, native rolling-pivot
live-output fidelity, initialized cluster observation scheduler,
representative-world Measure evidence, exclusive200-panel frame cost, production-frequency/week
statistics and live Quasar/Blazor operation. The host works; these are not generic host blockers.
Parent owns plan PR12 and Quasar PR317. No acceptance obligation is removed by a confidence label.

Useful focused commands:

```sh
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never
dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test offline-admin
# From Test/, using the exact isolated host/package contract:
uv run python run_suite.py OFF-ADMIN-PROFILE-RACE OFF-CACHE-DURABILITY
uv run python offline_reference.py --proof-only
```

## Rollback boundary

Before any future deployment, retain a pre-change cluster snapshot and binary package. Version1
saves remain readable; old readers cannot read profile-bearing v2 Finals or the appended WA cache
section. `offline.compensate=false` stops new profiling/credit and does not downgrade the format.
Rollback must restore a compatible snapshot/package together. No migration/deployment was performed.
