# Offline wake implementation draft (SE-346 / SE-347)

Round12 follow-up: the pinned standalone loader requires a schema-2 unpaid root's
mechanical history before reconstruction. Missing/null root history keeps the
whole loaded closure held, retains the malformed root verbatim and refuses a new
claim. Child rows may omit the root-owned map; explicit schema-1 no-history roots
retain observation compatibility. Targeted recovery checks cover both normal and
early-stop recovery plus repeated save/reload. This small validation repair has
no new native-host proof; earlier native scopes retain their exact source pins.

This branch is **not acceptance complete**. It implements wake-time compensation and antenna
call-ups in the production `Shared/Cluster` / `ClusterRegistry` architecture. Native proofs below
establish their stated scope only. No deployment, release or complete GUI/performance acceptance is
claimed.

## Concealment accounting extraction (2026-11-01)

Cluster now invokes accounting contract 2 from the single Concealment server plugin;
the copied model, profile, capture, solar and observation implementation is removed.
`Dependencies/Concealment.lock.json` pins its source commit and content hash.
`Build/prepare-concealment.py --source <concealment-checkout>` builds that immutable
commit, independently of checkout edits; omitting `--source` downloads the same pin.
Both packaging paths stage the DLL, manifest, provenance and dependency notices.
No public NuGet package or machine-specific source path is required. Source-compiled
and precompiled plugin identities use the same versioned process-local boundary.
The SDK-only CI lane uses `--source-only` and `AccountingSourceChecks=true` to
compile the verified pin's pure model directly into the test executable; it needs
no game installation. Default local/native checks reference the built production
DLL. No model source is copied into this repository or its production assemblies.

A nonblank `CLUSTER_GATEWAY_REGISTRY` disables autonomous Concealment before manager
or hook construction, including when transport is absent, disconnected or offline
offlining is disabled. Cluster owns admission, generation, time, Finals and application.
Required profiles remain in the checksummed v2 Final with native stocks; optional
plugin handover hints remain dispensable. Dormant standalone claims or positive
legacy credit are preserved and block conflicting compensation.

The elapsed cap now defaults to **72 world hours** in both Magnetar hosts. Both
use 225,000 saved-world clock units per hour; standalone resources still retain
native 60 Hz phases. Excess is forfeited through the actual wake, including retry.
Explicit overrides and the admin prediction request range of 0..168 hours survive.
The historical proof sections below retain their original products and scope;
they do not independently review or accept this extraction.

### Round6 clock/recovery repairs

Both real host getters address `floor(world milliseconds / 16)`. Native world time
advances 160,000 TimeSpan ticks per engine update; subtracting the admin time-of-day
offset is additive and does not change that rate. Contract 2/schema 2 therefore map
one clock unit to one native resource/production/timer frame. A world hour is
225,000 native frames (1.0416667 resource hours), while the cap remains 72 elapsed
world hours by default. Sun geometry still follows elapsed world time; excess is
forfeited through actual wake. Explicit cap overrides remain supported.

Never-applied schema-1 Finals with `.016` or `1/60` clock discriminators migrate on
the detached model copy and emit `ClockMigration`. Original Final bytes, stocks,
native timer phases and frame addresses remain the retry basis. Schema-1 profiles
already advanced beyond their freeze address and unknown schemas/clocks are refused
as ambiguous before entity instantiation; their immutable Final remains the retry
basis instead of being replaced by an ordinary Final. Ordinary already-paid Finals have no old profile and earn no second
claim or retroactive adjustment. Keep the corrected host and immutable core pin
together; contract-1 binaries cannot consume new schema-2 claims. Downgrade only
with the matching pre-upgrade world/Finals and package, never by reinterpreting paid
stocks. The v2 container and AdminContract 0.10/wire 1 remain unchanged.

Standalone failed recovery separately preserves surviving rows and durable opaque
evidence across the loaded physical/logical closure. Admission, capture and reveal
refuse that group without inventing a missing root profile or stock baseline.

Historical normalized model greens (including cap66) do not prove host duration.
The corrected checkers use raw saved elapsed ticks and native phase, without
inflating 450,000 native frames into 468,750 units or replacing a captured clock
discriminator. Older Factory traces without raw elapsed ticks cannot establish
the corrected host-boundary proof; fresh traces record them explicitly.

Repair evidence is separately scoped to runtime product `0ad6458` and pinned core
`ce8df32` (content `90d217e33b8ba5dca7ebd026cdae50b69a68b938f9d2b038ad29a993caceb64b`).
The private package and an independent preparation reproduce DLL
`43f20fd6e6641c41408bc6966fc3d7d04e81a1b7f9d6f1dae6856a39ae201a42`.
New native incomplete-root rotor-group runs pass scans/refusal/unaffected-group
settlement/save/SIGKILL/reload in both loader paths. Actual Cluster integration
passes both loader orders, identical ModelForHost input/output, ambiguous-clock
Apply refusal and a cross-node wake from an explicitly migrated schema-1 Final.
Standalone and isolated dormant-Cluster-facade native references each pass 56
raw-time stock prefixes. A normal Factory capture and all four requested native
phases in each adapter pass the 4/100/600/1200-frame checks using captured clock
fields and raw ticks (40 differential checkpoints in total).

The isolated reference uses the production Cluster facade with only the existing
local-mod loader installed; Cluster lifecycle and clock synchronization stay dormant.
It is a real native resource reference, separate from the initialized cross-node
integration. Heavy per-frame probes in an initialized cluster perturb its independently
corrected clock domain; those timed-out/failed attempts remain failed and are not
used as native duration or performance proof. Original Cluster10/lifecycle12 failures
and lifecycle13's separate scope remain unchanged. New independent review and the
external acceptance gates below remain open; no fresh client-hosted/Windows proof,
remote CI result, merge, release, publication or deployment is claimed.

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
end. The default 72 **authority-hour** cap forfeits excess time rather than banking it for another
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
and native thread initialization. Completed curves are reused. Discarded sessions/censuses, changed
group/occluder signatures and lost ownership cancel their queue work; completion only fills that
task's private result, never a replacement census. Armor/partial-block edits, group pose, mod content
and sun basis invalidate the curve. Wind uses settled placement and atmospheric speed with weather
multiplier one.

The user selected fresh all-pivot geometric catch-up, accepting vanilla rolling-pivot history as an
approximation. Raw normalized `Curve` remains independently verifiable under the unchanged2% geometry
criterion. Only electricity-producing solar panels receive a separate optional `ShadowAllowance`.
For a sampled angle, let `n` be pivot count, `k` lit pivots, and `r = abs(cosine) * k/n` (clamped0..1).
Mixed visibility (`0 < k < n`) indicates a shadow boundary whose area estimate is uncertain at finite
pivot resolution. Its normalized addition is `min(0.05*r, r*(n-k)/k)`; otherwise it is zero. This is
at most5% of the computed contribution and also stays inside the unshadowed cosine envelope and
nameplate. Ordinary cosine variation alone does not qualify. Clear, fully blocked and night samples
stay unchanged; oxygen-farm gas curves receive no allowance. Scripted environmental rates retain
their observation policy. No extra rays, configuration knob or generic worker service is added.

The existing allocator receives `Maximum * (Curve + ShadowAllowance)`, so actual dispatch, fuel,
storage, production and zone costs remain one transaction. Internal node results separately report
`SolarShadowAllowanceMWh` as the delivered source's proportional allowance share, not a second credit
or counterfactual fuel-saving calculation. Unused supply earns nothing. Old profiles default to
zero allowance. Public AdminContract0.9/wire1/offlinePartitions=v1 DTOs/routes are unchanged.
Planet/farm point fidelity and the exclusive sampling budget are separate checks; current outcomes
are below.

Script-driven resource rates get short-window means/variance; `InPlace` can wait a full sun
rotation, while the default `Skip` uses the short window. Observation durations use16ms authority
units: `ceil(observeSeconds*1000/16)`. Thus default180seconds waits11250 units; configured5seconds
waits313 units (5.008seconds). The former `seconds*60` incorrectly waited172.8seconds for180seconds.
`settleFrames=300` remains300 authority units (4.8seconds); native resource/timer rates remain60Hz.
PB/timer/ejection execution is never
replayed. `Freeze` leaves opaque behavior unchanged while modeled siblings compensate; `Block` keeps
the partition online. `Mean` handles observable resource rates, but inventory changes that cannot be
separated from conveyor transfers freeze that block with a diagnostic rather than inventing an
inventory source/sink. Ordinary reactor fuel settlements are now observed at the guarded native
ConsumeFuel boundary and normalized out of its inventory fingerprint. Conveyor pulls, arbitrary
additions/removals, extra inventories and bottle-state edits keep the conservative fingerprint. This
observer never adds a second fuel debit; the model uses the captured native timer and stock
semantics. The full InPlace learning and independent native reference now pass for a varying source
and genuinely varying ModAPI reactor fuel, with an opaque-inventory negative control; the
initialized serving-cluster scheduler now has a separate bounded natural-rotation fixture; a
representative-world census remains unavailable. No Measure node was
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
and the oxygen setting remain respected. Zero standby power cannot bootstrap gas production. With enough standby power but insufficient
nonadaptible working power, the native generator can alternate its delayed working state and
gas-dependent electrical demand. Capture retains both states; the existing resource allocator
evaluates the resulting finite feedback cycle. The event model integrates whole closed periods and
replays individual phases at fuel, storage, timer and interval boundaries. Shared production follows
those powered phases. Tank standby/transfer demand retains the native latch refreshed by nonzero flow or deferred working callbacks; zero flow alone does not clear it.
Reactor fuel and fast-feedback battery stock settlements use the actual last delivered phase,
preserving their native sampled-cost rules. Battery sampling is confined to actual fast feedback
intervals; steady flow retains its existing integral. A timer interval entered by a cycle retains
its sampled settlement even after the cycle ends, reconciling its already-integrated steady prefix
once. Delivered producer/consumer energy is conserved; sampled
native stock cost is reported separately from the energy integral. The captured battery Update100
phase and float stock arithmetic prevent a cycle average from inventing a different native debit.
Capture observes existing distributor state and does not force an extra allocation between engine
frames. Battery age uses the last completed frame (`GameplayFrameCounter - 1`), matching both
pre-simulation Invoke capture and post-simulation plugin capture. No linear supply-ratio scaling or nameplate surcharge is introduced. Positive
partial input still powers adaptible groups according to the native predicate. Engine gas intake and electrical dispatch settle together
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
`unknownBlocks=Freeze`, `longObservation=Skip`, `compensateMaxHours=72`, `callUp=true`,
`callUpSeconds=120`, `callUpPerMinute=6`, `callUpNoticeSeconds=2` (all under `offline.`). Existing
grace/minOnline defaults stay 180/60 seconds.

The authenticated Gateway AdminContract0.9.0 / wire1 seam advertises
`features.offlinePartitions="v1"`. Older nodes explicitly refuse unsupported operations. Quasar
PR317 ataa6f578 vendors the exact bb65a32 DTOs; later boundary repairs keep them unchanged. Gateway
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
controls, including the real source-release race. Authorized isolated Quasar/Blazor smoke at
aa6f578 with nativec307bba passes actual trusted-network Administrator/Viewer circuits, Profile,
cache epoch clear/replay, exact-Final0/1/168h predictions, no-wake invariants, private-LAN bearer
scopes, mobile/keyboard, older backend and outage. Steam-account cookie login was not proved after
the task-secret request timed out; no retry is scheduled. Uncertain-clear browser fault injection
and extended in-flight races retain only focused-test coverage. Parent independently reviewed the
separate explicit-role binding repair and reran3/3 regressions; the UI worker reports230 tests.
All task-owned smoke processes/listeners were torn down. This scoped partial smoke does not certify
backend fidelity or replace static/cold test provenance.

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

Every row names its **actual tested product**. The reviewed fidelity productf769415 is based on freshly
fetched mainbce4399. `rebase-bce4399-range-diff.txt` preserves68 ticket patches:67 equal and one
required adaptation retaining upstream DrainSave frozen-state retry data alongside compensation
profiles. Upstream plugin blockers, graceful-stop/retry and global-save protections remain. Main
itself supplies version0.1.26 and the Magnetar2.4.3.9 requirement; this task does not bump it or
publish a release. Required Magnetar was fetched into a separate private task directory; original
host/shared installs remain intact. The private `package-fidelity-10` archive is
`15afadc84192a3c364bb09dcf1b5e104a21d76abb9f7faf42d9357c39e34ec42`; all release gates pass.
Earlier rows retain their original products. Independent round3 reviewed onlyafc0080d/c307bba,
accepted Q1–Q3/U1–U3 for their reported defects and retained R1–R8. Round4 at3583ece/f769415 found no
new confirmed source defects in the later bounded Factory/cost/scheduler repairs; it did not claim
full acceptance. The newer solar/clock changes have no new independent review. Canonical evidence and `fidelity-repair-response.md` retain exact commands,
new results and failed intermediate runs, including the introduced client-admission error that
was caught by rotor/cold-zone regressions and corrected inca0f104 (rebased50bf053).

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

The new bounded stabilization is product779baaf (`package-solar-grace-3`, archive
`648f01233a08594e20a05f57c8a3348dbda5f86ea091014272ff506839e6645a`). All private release gates
pass. Its short-window `OFF-OBSERVED-POLICY` native result under
`/home/owendb/tm/offline-partitions/solar-grace/clock-results/20261101-133854Z` passes10/10.
Five compiled-plugin duration assertions cover0/1/5/180/600seconds. Fresh solar native9/9 at
cbe5542 (`solar-grace/solar-results/20261101-132823Z`) includes14 allowance-bearing bins in72
samples, unchanged-task reuse, real pending cancellation/replacement and a disabled nameplate.
The only production delta cbe5542→779baaf is the short observation-clock conversion; solar/model
bytes and the active InPlace3750-unit window are identical. Retained native geometry replay covers
576 bins:16 mixed,180 dark/blocked,92 clear and288 unchanged farm bins, with raw/allowed budgets
kept separately. The new2969-check bounded-allowance suite and existing model/feedback checks pass.
`solar-grace/observation-clock-scope.json` and product/native identities retain these distinctions;
none of the historical live-output verdicts or prior native products is relabeled.

Fresh initialized `OFF-OBSERVED-SCHEDULER` at cbe5542 passes28/28 under
`solar-grace/scheduler-results/20261101-133124Z`: minOnlineSeconds=600 and graceSeconds=600 remain
unchanged through eligibility, natural InPlace profiling and ordinary automatic offlining. These
are sequential UTC deadlines: online13:31:57.6182091Z, quiet13:42:01.370963Z, grace deadline
13:52:01.370963Z, offline observed13:52:09.6041842Z. Effective config, native/authority timestamps,
213 continuous expiry snapshots and no premature row are retained. Supported fuel versus opaque
edits, propulsion invalidation, native fuel reference, original-Final stock apply and new-generation
thaw all pass; wake debits0.006634 uranium. The final fixture takes its post-offline wait from the
frozen Final's timer, accommodating the native Tier1→Tier2 change during full grace. The pinned
run's shorter35s wait still crossed its actual20s remaining timer boundary and earned the positive
debit. No second full-duration run is claimed for that conservative fixture-only wait correction.
The shell wrapper returned143 after all structured results/Summary completed; no zero-wrapper-exit
claim is made. Task-owned native processes/listeners are stopped; no performance exclusivity claim.

The f769415 read-only capture proof adds native `gas-results/20261101-123018Z` Factory31/31
and `factory-fidelity-10-comparison.json`: normal Invoke plus all four real working/source capture
phases,20 finite4/100/600/1200-frame comparisons. All pass; native/model battery debit is identical
at every recorded float checkpoint. Maximum gas/settled+pending-ice/all-electrical errors are
0.00038147 units/0.000018 units/4.646705e-9MWh. Original bounds remain8 native frames for flows and
electrical delivery, two float ULP plus one Update100 settlement for sampled battery cost. The model
conserves delivered producer/consumer electricity; native sampled stock cost remains explicit.
Current `results/20261101-123018Z/OFF-GRID-POWER`12/12 preserves the actual distributor/gyro census
without forcing capture-side allocation. Current `bridge-results/20261101-123018Z` initialized
scheduler25/25 covers natural1-minute InPlace rotation during configured600s grace, supported varying fuel
versus opaque edits, propulsion invalidation, ordinary offline, exact original-Final stock apply and
same-worker new-generation native thaw. That historical fixture then lowered grace/minOnline to0;
it does not prove full600-second expiry. Rebased f8ceba6 `results/20261101-115102Z` additionally
preserves moving-rotor/quiet-sibling admission6/6, upstream plugin-blocker stop/restart10/10 and
refused drain-Final retry6/6. Final f769415 `results/20261101-123328Z-standalone` adds36000 actual
native coupled frames: stock5/5 within original1% and trajectory5/5 within unchanged160-frame
bounds. Final `bridge-results/20261101-123426Z/OFF-ZONE-ENVIRONMENT`17/17 preserves pending-save/
capture common costs, native supply before active protection and functional-off cancellation.
Canonical manifest and fidelity response retain exact hashes and commands.

The standalone engine advances stocks independently while ClusterNode is a dormant capture/model
library. Bounded native max-speed pacing was compared with3600 ordinary60Hz updates: gameplay and
elapsed-sun counters and battery/fuel endpoints agree. No update is skipped and no sun/time counter
is assigned.1296000 engine updates are six resource hours; the native elapsed-sun clock
advances5.76h, and the model boundary explicitly maps them to1350000 authority units. The energy
cases disable sun rotation; planetary and full-rotation observed cases are separate.

Native traces exposed and repaired paired-source priority, queued assembler working draw, sampled
reactor fuel phase/tier and fractional sun-bin livelock. Each root has a
failing-before/passing-after regression. R1–R8 were independently resolved at immutableb1e393f for
their specific defects; Q1–Q3 and U1–U3 were also accepted by round3 atafc0080d/c307bba.
Round4 independently checked the later bounded finite-feedback and profiling repairs at3583ece/f769415, including865 extra native-prefix checkpoints; newer solar/clock changes remain unreviewed. A failed first
reactor-observer run also exposed representation-sensitive decimal stock hashes; canonical G29
formatting repairs that fingerprint. The full 450000-frame learning plus 450000-frame native
reference subsequently passes: actual/model fuel debit0.833338/0.833375 within1%, battery
error−0.0000297683MWh, nonzero rate variance and opaque inventory freezing. Original
constant-clamped and first fingerprint failures remain failed.

| Required scenario | Earned proof / remaining obligation |
|---|---|
|T-OC-01|Three six-hour standalone baselines and12/12 strict stock cases above; broad arbitrary rapidly varying load waveforms remain outside that envelope|
|T-OC-02|Full upgraded3-ore/two-refinery/conveyor/output-full/queued downstream assembler reference above; native ore-switch tolerance is one captured tick per switch|
|T-OC-03|User-selected fresh all-pivot geometry: loaded-voxel ridge/own/near obstruction/panel/farm reference passes872 comparisons at max0.001161; model bins max0.009102. Both remain under0.02. Historical ordinary rolling-pivot max0.165844 remains failed, now an explicitly accepted approximation. Bounded shadow allowance is tested separately; exclusive sampling budget remains external|
|T-OC-04|Five-case native coupled stock/trajectory/oxygen-toggle/exhaustion/cold/circuit proofs above; finite native Factory cycling repair and fresh coupled regression described above|
|T-OC-05|Only controlled test worlds available; no safe representative observed-group census or Octahedron access; no speculative Measure role|
|T-OC-06|Actual Prepare/InPlace450000+450000-frame varying source/fuel/opaque-inventory reference passes at79a67df. Initialized serving-cluster natural-rotation proof is separate above; no representative-world census|
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

Solar's strict ordinary-live2% gate remains failed. `solar-oracle-audit-final.json` preserves the
675000-frame reference's retained angle samples,872 fresh points,8 ports, exact waveforms, phase offsets
(max0.0136°), per-port max/RMS and angle-sampled quadrature energy. Original §4.3 re-runs every pivot
at each sampled angle; the native modern component instead refreshes one of12 cached pivots per100
frames (full refresh1200 frames/.96° at120 minutes). At the sharp305° obstruction edge, fresh2/12
lit differs from cached3/12 or4/12. Interpolated sampled geometry error is0.001161; the actual model's
left-bin values have0.009102 maximum fresh-geometry error; both are below0.02. Ordinary live error
remains0.165844, including the left-bin comparison. Neither geometry result replaces the failed
live oracle or changes that historical verdict. The user explicitly selected the design's all-pivot
geometric model on2026-11-01; rolling-cache differences are accepted as a documented approximation,
not a pending decision. The new shadow allowance has its own bounded proof, separate from raw2%
geometric accuracy. Quadrature is not a per-frame native energy/stock
proof. Native sun uses16ms/frame while resources use1/60s; both phase/period and duration must be
mapped for standalone time comparisons. Physics-queue ray context and dynamic occlusion remain.

Remaining external limits are representative-world/Measure evidence, exclusive200-panel frame
cost, production-frequency/week statistics and the scoped Quasar login/browser gaps above. Only
controlled test archives exist locally (`world-census-availability.json`); controlled fixtures are
not a representative population. Parent owns plan PR13 and Quasar PR317. No acceptance
obligation is removed by a confidence label.

Useful focused commands:

```sh
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never -- --feedback
dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test offline-admin
# From Test/, using the exact isolated host/package contract:
uv run python run_suite.py OFF-ADMIN-PROFILE-RACE OFF-CACHE-DURABILITY
uv run python offline_reference.py --proof-only
```

## Deferred Concealment extraction

After stabilization, user-directed follow-up[#1804](https://tasks.cometworks.se/tasks/1804) moves the
single profile/model/capture/background/cache implementation to Concealment for default-on standalone
accounting, replacing its legacy boost while preserving explicit Off/Pause choices. This branch does
not implement extraction. The pure profile/model allowance has no Cluster dependency.

Cluster retains authority-clock adaptation, ownership/generations, offlining, pins, immutable-Final
persistence and application authority. In configured cluster mode, every autonomous Concealment
scan/conceal/reveal/boost/dynamic update patch/command/keepalive/disposal RevealAll must be disabled
before manager/hook construction, regardless of saved settings or connection health. The current
configured marker is nonblank `CLUSTER_GATEWAY_REGISTRY`; a missing node link is not standalone mode.
Shared computation/background work runs only by explicit Cluster request, with no independent timers
or lifecycle. Parent's `Plan/ConcealmentCompensation.md` owns the later design and mixed-plugin tests.

Profiles and actual stocks must remain in the same durable host snapshot. Cluster's v2 offline Final
already provides that boundary; ordinary live handover carries actual object builders and rebuilds
destination census. Entity references, Tasks and partial windows are local, not transferable claims.
Optional plugin handover hints can be dropped and cannot carry the only durable entitlement.
Standalone needs consistent local world-save/profile/time/application markers, then a validated
game-thread commit before reveal resumes native updates. Both adapters must prevent duplicate credit
or fuel/chip debits across crashes/retries and reject stale ownership/inventory/topology. These are
future extraction obligations, not newly claimed standalone behavior.

## Rollback boundary

Before any future deployment, retain a pre-change cluster snapshot and binary package. Version1
saves remain readable; old readers cannot read profile-bearing v2 Finals or the appended WA cache
section. `offline.compensate=false` stops new profiling/credit and does not downgrade the format.
Rollback must restore a compatible snapshot/package together. No migration/deployment was performed.

### Round-seven loaded-world lifetime and required JSON discriminators

Round-eight review found an early-stop gap at core `d257347`: stopping before
its first accounting update retained only loaded rows, leaving uncovered members
live. The `8d3039a` repair reuses pending recovery during Stop before cancelling
host callbacks. The subsequent native review supports missing-root/member
containment through real first-update failure and early disposal, saves/restarts,
unchanged own-world stocks/phases and unrelated live production on both loaders.
That review separately found R9-1: genuine complete saved rotor groups were
rejected while their guarded native saved-ID attachment was still pending.
Historical failures remain scoped in the round-nine review; they are not full
acceptance passes.
The core pin retains standalone claim/legacy metadata serialization and whole-group
update guards after a manager update failure or explicit plugin disposal while the
same world remains loaded. Scheduling, commands, observation and measurement stop;
complete groups settle before stopping, and rejected/opaque groups remain suspended.
Native world unload releases the retained runtime, patches and callbacks. A runtime
cannot replace that owner while the same loaded world still retains state.

Shared persisted profiles require explicit `Schema` and `ClockFrameSeconds`, also
inside standalone records. Sparse JSON cannot acquire current defaults via a model
clone. Missing fields propagate refusal through the shared Apply reader before
Cluster recovery can instantiate entities or replace its immutable Final; standalone
retains malformed records as opaque evidence. Explicit known unpaid schema-1
migration, current schema-2 profiles, cap/retry and coupled rollback policy remain.
No AdminContract, v2 container, clock domain, resource model or tolerance changes.

The focused regression commands are Concealment `tests/runtime`,
`tests/accounting-recovery/run.py` and `tests/accounting-clock` (production DLL plus
`DEDICATED64`). Actual native fault/disposal/save/restart proof and source/precompiled
loader identities are recorded separately by the implementation handoff. New
independent review and the initialized authority-clock differential remain required;
the dormant-facade duration references keep their earlier bounded scope.

### Native mechanical reconnection recovery (R9-1)

The current immutable core pin reconstructs only reciprocal saved mechanical
base/top links before validating loaded physical/logical membership. The native
base's saved-ID structural method runs on the owning game thread with production
and update guards retained. Saved claims, world/entity/block identities, current
closure, reciprocal targets and stocks are checked before and after attachment;
missing, stale, foreign or mismatched evidence still quarantines the group.
There is no nearest-top search, replacement part, synthetic claim/profile,
production thaw, unrestricted one-frame callback or change to the accounting model.
Rotor and piston paths share the native mechanical base.

Reconstructed native constraints stay held until paid reveal. This prevents their
solver from moving a concealed checkpoint. Original pose evidence is retained;
validation permits only its exact native double-to-cluster-float-to-double
publication (including body center/offset), with no positional tolerance added.
Ordinary movement still refuses. The same owner retains unpaid state during early
failure/disposal and releases native constraints only after successful settlement.

Focused commands are Concealment `tests/accounting-mechanical/run.py`,
`tests/accounting-recovery/run.py`, portable runtime and production-DLL clock/JSON
checks. The actual saved rotor/piston, source-compiler/precompiled, stock/receipt,
save/restart/early-stop/negative-link and true-unload evidence and all failed
attempts are retained in `/home/owendb/tm/offline-partitions/mechanical-recovery-repairs/`.
The implementation response records exact source, product and fixture identities.
New independent review remains required. Initialized Cluster authority-duration
and the external acceptance gates above remain open. Contract2/schema2, known
unpaid schema1 migration, Admin0.10/wire1, costs, clocks and solar policy are unchanged.


### Mechanical ownership and settlement boundary (R10)

The pinned core now owns complete per-base constraint identities before structural
reconstruction, including disabled and absent entries. Assignments are retained even
when native callbacks throw; native world admission disables each newly owned constraint
before public attachment callbacks. Ownership remains with the loaded world through
rejection, scheduling failure, disposal and saves. Complete block/link/claim/closure and
exact stock/pose checks surround every structural call, including piston subparts.
Later callbacks invalidating earlier links stop subsequent calls.

Piston internal subpart constraints use the same owner, including their saved flags
and explicit world-membership history. LoadSubparts reconstruction remains inactive
until ordinary paid native updates; no motion callback runs during unpaid recovery.
Deferred restoration yields to any newer unpaid owner.

The private standalone root record adds required per-entry Mechanical state so native
save/reload does not lose originally disabled constraints. Paid snapshots retain the
current state per grid, bound to native saved targets and checkpoint. Ordinary native
initialization restores those flags before later capture or another snapshot. Existing claims without
that history keep the supported reconstruction path and record the actual native
requested reconstruction state, rather than assuming an enabled default; unrecorded
historical disabled flags cannot be reconstructed. Subsequent checkpoints retain the
observed state explicitly. Older runtimes do not implement this ownership; rollback
requires the matching pre-upgrade world and runtime/package together.

Settlement retains its unpaid receipt through native writes and notifications, checks
mechanical ownership throughout, and restores complete original inventories, native
caches, queues and phases on refusal. Native reentrant inventory writes are fenced
before mutation during commit/rollback. Reactor capacity is prepared from the exact
native fuel-inventory rule so normal native notification cache refresh remains valid.
This does not change the shared model, schema2/contract2, clocks, costs or Cluster wire.

New focused proof, exact inputs/products/fixtures, failures and cleanup belong under
`/home/owendb/tm/offline-partitions/mechanical-failure-repairs/`, with
`mechanical-failure-repairs-response.md`. The earlier mechanical recovery and round10
reports remain immutable and retain their original scopes. New independent review and
all previously open acceptance gates remain required.

### Requested intent and prepared settlement stages (R11)

The pin now scopes Havok admission permission to the current OnAddedToWorld call,
revokes it before removal callbacks (including nested removal), and retains removed
objects with disabled managed intent. Unpaid SafeConstraint reads suppress native
refresh/disposal/recreation during inspection and builder serialization; target checks
read connection state directly. Cleanup never re-admits a constraint merely
to use native setters. Requested m_enabled is captured independently of effective
native Enabled for main and piston-internal constraints, including paid snapshots.
Private standalone record schema 2 requires IntentVersion=1 on mechanical history.
Schema-1 unforced records can migrate the known request; forced history is ambiguous
and refuses instead of inventing intent. Genuinely old no-history claims keep the
observation-only reconstruction path. Rollback requires a matching earlier world and
runtime; Admin0.10, shared profile schema2 and accounting contract2 are unchanged.

Settlement keeps the original recipe-bearing profiling signature at admission,
then validates immutable ownership/settings plus an exact prepared stock basis
before and after each native write. Only fields belonging to that write advance
the expected basis. Completed recipes may disappear legitimately; edits to later
blocks are refused before overwrite. Every notification and the final publication
still require exact desired stocks. The model and cost kernel are unchanged.

Focused controls, failed attempts, exact source/binary products and final private
package identity are recorded in
`/home/owendb/tm/offline-partitions/intent-assembler-repairs/` and its response.
Earlier positive native scopes retain their original product identities; they are
not rerun or combined into an acceptance score. Independent review is required.
Release-setter failure atomicity after paid publication and unqualified storage/gas/
zone callbacks remain explicit boundaries unless separately proved by those controls.
Initialized authority-clock duration differential, external census/Measure,
exclusive200/performance, production-week, Steam/player/client-hosted Windows and
full external antenna acceptance remain open.
