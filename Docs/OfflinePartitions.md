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

A directed request loads onto the caller's node and reasserts while pending. A second caller
cannot steal a live reserved remote/laser target. Once local, vanilla rights are checked before
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

Actual local proof on Linux THE-RIG:

```
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never
# 28 synthetic model assertions

dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never -- \
  --save-container ClusterNodePlugin/bin/Release/net10.0/ClusterNode.dll /path/to/DedicatedServer64
# 10 container structural/compatibility assertions; loads assemblies, starts no game

dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test remote-use
dotnet ClusterRegistry/bin/Release/net10.0/ClusterRegistry.dll --self-test offline-partitions
```

Node, WA and Registry compile with post-build deployment disabled. New production patch hashes
were resolved against local DS binaries (`d24b196b`, `a90270cc`, replicable request `d6d1e890`);
confirmation against an actual running-node hash dump remains pending. The always-on CI job runs
the detached model proof. No version was bumped.

| Required scenario | Current proof / remaining obligation |
|---|---|
| T-OC-01 | Battery/reactor priorities, recharge loss and shared fuel synthetic checks; 6-hour game differential missing |
| T-OC-02 | Ore order, conservation/output stall and atomic multi-product checks; upgraded two-refinery/network differential missing |
| T-OC-03 | Sun breakpoint synthetic check; planet/ridge/own-grid ray differential missing |
| T-OC-04 | Coupled ice/gas/engine synthetic exhaustion check; engine differential ordering/times missing |
| T-OC-05 | Measure-node decision and fixture pending real-world evidence |
| T-OC-06 | Short observation implementation; sinusoidal mod/long-window acceptance and Measure decision pending |
| T-OC-07 | Freeze/Mean/Block paths authored; real PB/timer/ejection fixture missing |
| T-OC-08 | Independent group census and signature cache authored; rotor/quiet-sibling fixture missing |
| T-OC-09 | Deterministic retry/zero-repeat/cap checks plus immutable-source container proofs; apply-to-Final crash-injection game run missing |
| T-OC-10 | Content/mod-byte mismatch gate authored; changed-mod wake/healthy-partition game run missing |
| T-OC-11 | Chip exhaustion synthetic check; real zone removal/upkeep differential missing |
| T-OC-12 | Shared measured cache, per-block fallback and epoch invalidation authored; 20-base rotation/cache game proof missing |
| T-OC-13 | Velocity/acceleration/self-propulsion gate authored; dynamic parked-ship pose differential missing |
| PRF-OC-01 | No 200-panel/exclusive timing proof; no performance claim |
| P2-OFF-CALL-TERMINAL | Native runner authored for terminal, caller node, pin/sweep/close/shared wake; not run |
| P2-OFF-CALL-RC | Native station take/presence runner authored; not run; dynamic drone driving fixture missing |
| P2-OFF-CALL-DENY | Admission code authored; authenticated packet/no-relay/hostile native fixture missing |
| P2-OFF-CALL-RATE | Bounded admission code authored; 20 reachable offline grids/minute native fixture missing |
| P2-OFF-CALL-CONFLICT | Registry heartbeat/ownership/balance and first-caller proofs passed; two-live-client/no-ping-pong fixture missing |
| P2-OFF-CALL-LASER | Id/GPS directed wake and real-connect replay authored; native established-link/no-wake fixture missing |
| P2-OFF-CALL-IGC | Native no-wake unicast runner authored; not run |

`Test/coverage.py` explicitly maps all these IDs, including missing fixtures as plan gaps; it
never aliases model/container assertions to a native differential or GUI pass. Native doctor
failed for absent release package, configured launchers/compiler/client profile/Remote API and
planet fixture. No game/server/client was started to bypass that host contract. Actual latency,
large-station frozen window/bytes/traffic, call frequency/conflicts and frame benchmarks remain
unmeasured. Those omissions and Quasar integration block declaring either ticket complete.

## Rollback boundary

No live/deployed cluster was touched. Before any future deployment, retain a pre-change cluster
snapshot and binary package. Old readers cannot read profile-bearing v2 Finals or the newly
appended WA cache section. `offline.compensate=false` stops new profiling/credit; it is not a
binary-format downgrade tool. Rollback must restore a compatible snapshot/package together.
