# Review round 1 response — repairs and earned proof

PR https://github.com/CometWorks/cluster/pull/180, feature/se-346-347-offline-wake.
Original review remains immutable at `review-round1.md`, reviewed head
`f4e4128814654b9aa11d0e9aaae304c71c65d222`. All R1–R8 repairs are implemented;
independent re-review and full acceptance remain pending. Earlier chronological responses are
preserved outside the source repository in `review-round1-response-history.md`.

Initial repair commit is now `a1cd1dd` after the clean rebase onto
`dcc444ece0d473eefb516d660b8f2172fd353708`. Historical tested commits retain their own
manifests and `refs/tm/offline-wake/pre-main`; no newer package inherits an unrun native pass.
Current production package is `4109242cf4ecb17b3c04f74d9fd45ed5a336ae51`, archive SHA256
`c81472f8a694222afa40db4cb5ebbb6befd36af746ddb14ffb7663132ccec262`.
Source `93c2fa3` changes only the optional test hold scheduler. All paths below are under
`/home/owendb/tm/offline-partitions/` unless repository-relative.

## R1 — full Auto battery transition

Bound full/discharging and nonlinear near-full demand transitions to Update100, then recompute
the existing allocation. Charging uses actual preceding battery output rather than a cleared
output buffer. Detached exact-source regression failed before repair (2 MWh after an hour)
and passes after (2.999999994073998 MWh,2161 events). Original28 checks still pass.

Native `results/20261031-191916Z/OFF-LIVE-DIFFERENTIAL` at ef2e016 passes7/7 with an actual
full Auto battery and refinery after the authority/native-clock correction. It is a short
clustered-engine reference. The full six-hour/three-baseline uranium tolerance and rapid
Auto-battery/native fuel-settlement differential remain unproved.

## R2 — downstream input/output enablement

Upstream input thresholds create events. Improving output capacity is rechecked within the
captured production timer tick, including atomic multi-material requirements. Exact-source
regression failed before (100 iron/zero components) and now yields9 components/91 iron from
100 ore, with exact conservation. Shared wake uses actual Survival refinery/assembler builders.

Native combined zone/production T-OC-11 passes125/125 at1063947 and9f128a7. The original
upgraded three-ore/two-refinery/conveyor/full-output standalone differential is still required;
these fixtures do not claim that production-chain acceptance.

## R3 — electrical gates on tank source and storage

Both tank source and reservoir records carry the electrical sink. Coupled allocation disables
discharge and storage input when it is not supplied and recomputes at events. Oxygen tank
capture respects EnableOxygen. Detached discharge/charge negative and powered positive
controls fail before/pass after. No native full coupled-gas/cold-bootstrap/oxygen-toggle proof
is claimed. Later gas-generator standby repair is separately evidenced below.

## R4 — solar Forward-Z pivots

Primary/alternate pivots use the engine's explicit Translation+Right*X+Up*Y+Forward*Z.
Cold actual-game-math identity/rotated sign-sensitive checks pass. They perform no Havok rays.

Actual sampling later exposed a separate generic Task.Run/Havok native-thread crash. Full dump
is preserved in `native-host/solar-worker-crash`. Rebased `1cf729c` uses the engine's
CastChainedRaycastsParallel physics workers/read context. Native OFF-SOLAR-NATIVE4/4 and
T-OC-11 125/125 pass at1063947. These establish physics-queue safety and an unobstructed panel,
not the required planet/ridge/own-grid close-obstruction or200-panel timing differential.

## R5 — Repeat measurement

CanMeasureAssembler excludes Repeat, disassembly and in-progress queues at scheduling and
measurement entry. Repeat retains uncached formula fallback; completed re-enqueued units
cannot become a false local cached rate. Cold eligibility and existing detached Repeat stock
conservation controls pass. Native T-OC-12 47/47 at9f128a7 proves twenty refinery bases share
one measured formula and one remeasurement after a journaled clear/epoch increase; it does
not prove Repeat assembler throughput.

## R6 — no-report versus empty, sampled ownership generation

Liveness/no-report preserves presence. Only an explicit per-partition empty record clears
its matching sampled ownership generation. Both heartbeat ownership and presence tags must
match current fenced ownership; the receiver never retags an old snapshot. Missing-record
clear was additionally repaired in `839b0bf`.

Registry remote-use tests cover full→no-report, delayed-old generation, mixed generations,
missing entries and explicit current-generation clears; the missing-record regression fails
before/pass after. Native Conflict23/23 at9f128a7 preserves the original permanent terminal
pin through three minutes of ordinary heartbeat traffic and authenticated requests from a
second live client on another node. Closing the terminal eventually clears reported presence.

## R7 — atomic source capture

The no-remote-use check runs on the source game thread immediately before Freeze, with the
existing deliberate co-location exemption carried to that decision. Ordinary replication
add/replay rejects frozen roots; removals still clear pins/cancel demands. Standalone hooks
remain inert.

`results/20261031-213001Z/OFF-REMOTE-CAPTURE-RACE` at4109242 passes10/10: gateway
reads no pin; its actual source request is held before game-thread capture; a real client
opens its terminal and obtains a permanent pin inside that gap; release produces source
remote-use refusal, no freeze/ownership move, and the original pin survives. Complete PNG
is captured. The optional test route extension holds only that request on a worker so the
serial control listener continues serving status/release; the unchanged production handler
then executes normally. Earlier unobservable-listener holds remain failed fixtures.

Terminal12/12, Conflict23/23 and actual raw-W RC9/9 also pass at9f128a7. Conflict/terminal/RC
complete PNGs were visually inspected. Deliberate spatial co-location and frozen-new-pin
positive controls remain separate native obligations.

## R8 — journal-before-success WA-local clear

Local clears reuse GlobalStateChannel's actual authority queue, command ledger, journal append
and held-response path. Remote SendCommand still rejects self-commands; local callers cannot
synchronously wait on the game thread. No separate durability/debit mechanism was introduced.

OFF-CACHE-DURABILITY passes12/12 at1063947 and9f128a7: seed/snapshot/second holder, hold
later global Final registration, WA-local clear, actual journal entry before acknowledgment,
kill before newer Final, WA replacement replays clear over old snapshot, untouched-key positive
control, stale epoch rejected/new epoch admitted. Existing journal fail-open policy is inherited.

## Required safe-zone/shared power expansion and subsequent native repairs

Running player-built zones, all modeled production and other sinks share the existing resource
allocator. Actual radius/shape sink draw, delivered producer output/definition fuel cost,
renewable priorities, battery modes/recharge loss and stock/output limits govern debits.
Result diagnostics expose per-producer fuel/energy, consumer/storage energy, production units,
chip renewal and zone state. There is no parallel accounting service or background offline engine.
Admin zones remain separate. Power loss pauses paid credit/removes the bubble, recovery uses
vanilla activation, chip exhaustion removes the bubble while retaining block/settings/rights.

- Native T-OC-11 at1063947 and9f128a7 passes125/125: actual reactor/refinery/assembler/zone,
  radius10/20/Box demand, native chip renewal, energy/fuel/stock conservation, empty/full-output
  standby, finite native uranium settlement/exhaustion, bubble removal,135s solar/battery
  recovery spanning120s activation, active-zone quiet offlining and true radio/shared wake.
  The1063947 shared wake debits0.0820199339 uranium and produces2565.08928 refinery units/
  109 plates; unsupported bubble absent, admin-zone control enabled/uncharged.
- At6423447, `bridge-results/20261031-210020Z/T-OC-09` passes18/18: real paid-credit
  expiry; first shared Apply consumes1 chip and0.0997225080014 uranium; pre-Final SIGKILL;
  immutable retry consumes1 chip and0.1259399743134 uranium for its later interval without
  stacking the abandoned debit; replicated ordinary Final, second kill, no profile to repeat,
  retained2 of original3 chips. The4109242 repeat in results/20261031-213001Z also passes
  18/18 (262s): first/retry uranium0.0799590741902/0.1563608327776, one chip each from
  original stocks, replicated ordinary Final and second kill/no-repeat retain2 of3 chips.
- `ca9888f` repairs initially power-paused/id0 capture using m_timeLeft/retained geometry,
  excludes powered manual-off, and reuses carried-zone restoration. An unpowered production
  timer cannot complete a measurement, so it uses definition fallback. Before native run failed
  stuck in production profiling (`paused-before-status.json`). At1e51d8e,
  `gas-results/20261031-212653Z/OFF-ZONE-PAUSED` passes14/14: exact paid credit pause,
  real shared recovery, no unsupported bubble/debit, real120s power reactivation with edited
  Box/rights, powered manual-off stays off. Environmental restoration DURING the offline
  interval still lacks native proof. An earlier13/14 run failed only an empty-dictionary
  assertion for a correctly explicit zero fuel diagnostic; numeric assertion is fixed.
- `1e51d8e` repairs gas generators charged operational draw while idle: delivered gas selects
  the existing sink's working/standby demand once for both output ports; electrical gate,
  oxygen setting and native multipliers remain. Old b3feac1 native case fails at500 times
  native1kW draw. New `gas-results/20261031-212653Z/OFF-GAS-IDLE` passes7/7:
  native0.0000055688888889MWh versus modeled0.0000055688891534MWh; no ice consumed,
  electrical energy conserved.39 detached budget assertions pass; exact old model fails.
  Full native coupled-gas working/exhaustion/cold-bootstrap remains separate.
- `4109242` repairs a native lost wake after asynchronous content hashing: the pending claim
  stays in a bounded game-thread queue, checking readiness before rereading its Final.
  At b3feac1 T-OC-10 read once then never resumed. At4109242,
  `results/20261031-213001Z/T-OC-10` passes7/7: actual changed mod bytes, fresh pid/Serving,
  real shared recovery, explicit content mismatch skip, healthy loaded partition. Earlier
  fake-Workshop client and stale-startup-log fixture failures remain failed.

The native reference also found a separate4% clock over-credit. Captured clocks remain225000
16ms units/hour; rates integrate216000 native60Hz frames/hour. Detached factories use1/60.
Rebased `c1f4a94` fixes that conversion; historical pre-fix item amounts prove execution only.

Laser retained intent/registration pairing/durable-row precedence and the actual replicated
promotion predicate were also repaired from native failures. At6423447 laser8/8 (id/GPS,
both real ends connected, established proxy/no wake) and Deny14/14 (ordinary hostile/no-relay,
actual Moderator positive control) pass. At9f128a7 Rate29/29, rotor6/6, dynamic pose5/5,
Terminal12/12, RC9/9 (112.946m actual drive) and IGC5/5 pass. Exact per-package results and
all historical failures are in implementation-evidence.json and Docs/OfflinePartitions.md.

## Remaining acceptance and rollback

Phase5 preserves observed means after known-class capture but deliberately freezes opaque
script storage/inventory changes, including fuel movement it cannot separate. Native controlled
short-window policy10/10 is not full sinusoidal/fuel-script/rotation semantics and its1/1 fixture
fraction is not a real-world Measure census. No speculative Measure role is added.

Full six-hour/three-baseline energy, upgraded multi-ore/conveyor production, coupled gas,
planet/ridge/own-grid rays, four-hour/three-chip standalone zone, broader producer envelope,
laser radio chain, co-location/frozen-new-pin controls, Quasar UI, real-world Measure census and
exclusive200-panel/large-station measurements remain material acceptance work. A missing fixture
is work, not a human prerequisite. Host setup works; no generic defaults-unconfigured blocker.
This shared machine supports correctness runs and no exclusive benchmark claim.

Old readers cannot read profile-bearing v2 Finals or the WA cache append. Rollback requires
compatible snapshot/package together; compensate=false is not a format downgrade. No live
migration/deployment/release/merge occurred. Source and sibling Quasar/clustering-plan ownership
boundaries are preserved. Exact plan update is clustering-plan-native.patch against451980b.
Both tickets remain open/assigned owen pending real acceptance and independent review.
