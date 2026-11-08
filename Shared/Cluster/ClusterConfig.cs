using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Runtime-switchable handover knobs (HandoverImprovements.md §2). Flat string map, every knob read
/// per use so a PUT on a live node takes effect on the next phase; the gateway fans its document out
/// on PUT and hands it to a node at registration, so a relaunched node starts with the current set.
/// Defaults are "today's behaviour"; the experiment programme flips them through the API.
/// </summary>
public static class ClusterConfig
{
	private static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
	{
		["tee.async"] = "false",
		["import.entitiesPerFrame"] = "0",
		// §5 staged import: legacy = one game-thread frame as before; staged = deserialise and the
		// round-trip compare on the handler thread, entity Init on workers, one activate frame.
		["import.mode"] = "staged",
		// staged only: 0 = build the entities on the game thread inside the activate frame (deserialise
		// and compare still off-thread); N>0 = build them on worker threads, one worker per entity.
		["import.stageWorkers"] = "8",
		// T-0392, staged only: 0 = activate every staged entity in ONE game-thread frame (as before);
		// N>0 = at most N per frame (whole physical groups: rotor/piston/connector/landing gear never
		// split, a larger group is one frame), then a final frame for partition adopt, the remaining
		// network ids and the round-trip builders. Read once per import. The delta import ignores it.
		// Default 20 (T-0392 live A/B on r116, 2026-10-05: 0 vs 43 game-thread hitches >= 100 ms on 40+ entity
		// imports, worst activation frame 206 vs 625 ms, client sim not worse, 0 residuals).
		["import.activatePerFrame"] = "20",
		// T-0392 cost budget, sliced activation only (import.activatePerFrame > 0; ignored at 0): a slice also
		// stops before the group that would take its estimated MyEntities.Add time past this many ms. The
		// estimate is the previous slice's measured Add ms per entity, for an import's first slice the node's
		// running average over earlier slices (none yet = the count cap alone). Only ever SMALLER slices than
		// the count cap; at least one whole group per slice, so one group that alone exceeds the budget is
		// still one frame (handover_phase overBudgetGroup=1). Budget = Add time only: init, freeze, reseat and
		// the slice's network ids come on top. Read once per import. 0 (default) = the count cap alone, the
		// same slices as without this knob.
		["import.activateBudgetMs"] = "0",
		// T-0392 per-second pacing, sliced activation only (import.activatePerFrame > 0; ignored at 0): the node
		// spends at most this many ms of activation (measured game-thread ms of the slice and final frames) per
		// rolling second across all imports in flight on it; a frame that does not fit waits for a later one, a
		// slice is sized to the remaining allowance through the activateBudgetMs estimate, oldest import first.
		// An import held back for more than ImportActivatePacer.DefaultForceAfterWaitMs in total goes regardless
		// (no handover deadline can be reached through pacing). Read once per import. 0 (default) = no frame waits.
		["import.activateMsPerSecond"] = "0",
		// one canned staged import at node start so the first real one is not the JIT-cold one
		// (390-860 ms wall vs 65-100 ms steady, w22); read once at "Serving", not live-switchable.
		["import.warmup"] = "true",
		// T-0397: fsync every global-save replica before registering the holder (today). false = kernel writeback; a
		// torn copy fails its checksum where it is read and the reader takes another holder.
		["replica.globalFsync"] = "true",
		// T-0399: the replication worker, when it has no copy to make, re-reads one global-save copy this node holds (its
		// host's, on the host's scrubber in store mode) every N seconds and withdraws one that no longer verifies, so a copy
		// torn or rotted after its install stops counting as a holder. 0 = off. Read per check.
		["replica.scrubSeconds"] = "6",
		// HandoverDelta.md: the gateway presyncs the partition onto the target before the freeze and
		// the capture carries a field-level patch. presync.enabled is read by the GATEWAY (same
		// document); the nodes read ttlSeconds (baseline / dormant-copy lifetime) and the applier
		// tiers that may patch a dormant entity instead of rebuilding it.
		["presync.enabled"] = "false",
		["presync.ttlSeconds"] = "120",
		["delta.appliers"] = "pose,component,scalar",
		["release.closePerFrame"] = "0",
		["hitch.thresholdMs"] = "100",
		["hitch.logThresholdMs"] = "250",
		// SE-1228: a game thread stuck this long in one frame logs every thread's stack once, while it is still stuck, so a
		// long hitch names what it waits on. At most once per 30 min per process (the capture stops it for a moment);
		// Linux only. 0 = off. Read every 0.5 s.
		["hitch.stackMs"] = "3000",
		// HotSpareNodes.md dormant spares: a registry-enabled regular node that has never owned
		// a partition and has no clients throttles the whole main loop to dormant.hz.
		["dormant.enabled"] = "true",
		["dormant.hz"] = "4",
		// T-0120: charge the whole main thread to the partitions, not just entity-update ticks
		// plus the physics share. Default ON because the old behaviour is the bug - the summed
		// load was ANTI-correlated with content and the balancer fed the fullest node. Left
		// switchable because it changes the balancer's only input on a live cluster: if the
		// measured spread turns out too narrow to rank nodes, or the balancer starts thrashing,
		// a PUT of "false" restores the previous metric in seconds without a redeploy.
		// T-0120. Which measurement supplies the main cost the old metric was missing.
		//   "thread" (default) = Sync.ServerThreadLoadSmooth, worker-pool time.
		//   "cpu"              = Game.CPUTimeSmooth * 60, MAIN-THREAD cost of a simulated second.
		//   "off"              = the old attributed-only metric (entity ticks + physics share).
		//
		// I PREFERRED "cpu" ON SEMANTICS AND THE DATA SAYS OTHERWISE, so the default follows the
		// data. Sim speed is bounded by the MAIN thread, and worker time is not main-thread time -
		// on the argument alone "cpu" is the right quantity. But "thread" is the one with a
		// measured pedigree: the CM verified it over 3701 samples this run, correlation +0.926
		// with entity count, medians 12.7 / 35.8 / 77.7 / 117.0 ms/s across the <100 / 100-500 /
		// 500-1000 / 1000+ entity bands. Converted to per-simulated-second and applied to the four
		// serving nodes at 23:23 it ranks them correctly over a 21x range - 1726 / 332 / 141 / 83
		// against entity counts 1558 / 327 / 54 / 47 - where the metric in use today reported
		// 75 / 143 / 158 / 137, i.e. backwards.
		//
		// Whereas "cpu" I cannot even evaluate yet: CPUTimeSmooth is not exposed by any build that
		// has run, so its value on a HEALTHY node is unknown to me. It is 1333 ms/s on the blob by
		// arithmetic from sim 0.75, but whether an idle node reads 60 or 900 decides whether the
		// metric has any dynamic range, and I am not defaulting the balancer to a number I have
		// never seen. One PUT switches to it once health.frameTimeMsEma has been observed.
		//
		// Scale does not have to be a wall-clock identity: the gateway's balance test is an EXCESS
		// RATIO against BalanceRiskTolerance, so it is invariant to the units, and the metric it
		// replaces was not an identity either (it was one node's physics step).
		["balance.unattributedLoad"] = "thread",
		// T-0124. Per-entity update timing in MyEntitySimCostPatch. On by default: without it the
		// balancer's per-entity cost term is structurally zero, which is the defect T-0124 fixes.
		// Kept switchable because it costs two Stopwatch reads per entity-update on the game
		// thread, and the whole point of a cost metric is not to become one.
		["balance.entityCostSampling"] = "true",
		// --- T-0126: Margin as a controlled variable -----------------------------------------
		//
		// partition.margin IS the old `private const double Margin` in PartitionManager, moved
		// into the config plane so it can move at all. Nodes read it per membership pass, so a PUT
		// takes effect without a redeploy.
		//
		// OPERATOR-SET ONLY. Viktor 2026-09-12: no dynamic margin. A controller that walked this
		// knob was built and run against the w30 blob; it worked exactly as designed - it stepped
		// 500 -> 400 -> ... -> 0 as the node's achieved sim fell - and it accomplished NOTHING,
		// because a gap rule needs a gap. The single-linkage MST over that 1181-member partition
		// has its LARGEST edge at 2991 m, nine metres short of D. Zero of 1180 edges reach the
		// split threshold at ANY Margin >= 0, so no setting of this knob can divide that blob.
		// See findings/the-balancer-optimum-equals-the-status-quo-on-a-blob.md and T-0134: the
		// lever that reaches a contiguous blob is a SIZE CAP, not a distance.
		//
		// WHAT THE NUMBER MEANS, because the ticket had it wrong for ten hours. The rule lives in
		// PartitionMembership and it is ASYMMETRIC:
		//     merge:  nearest member gap <  D              (= SyncDistance, 3000 m; Margin ABSENT)
		//     split:  every cross-group gap >= D + 2*Margin
		// So Margin sets ONLY the upper edge of the deadband [D, D+2*Margin]. A step of 100 on
		// Margin is a step of 200 on the split threshold. Against the w29 blob (566 members):
		//     Margin 1000 -> 5000 m ->  1 component        Margin  250 -> 3500 m ->  5 components
		//     Margin  500 -> 4000 m ->  1 component        Margin    0 -> 3000 m -> 10 components
		// Everything above roughly 240 splits NOTHING, so the useful band is the bottom of the
		// range, not the middle of it.
		//
		// THE FLOOR IS NOT ARBITRARY. At Margin = 0 the split threshold reaches the merge
		// distance and the deadband closes. Below -(D/2) = -1500 the split threshold falls under
		// the merge distance and the two rules invert: groups split at one distance and re-merge
		// at a larger one, which is a guaranteed oscillator rather than aggressive splitting.
		// Normalize() enforces the floor for free - every knob here is a non-negative integer, so
		// a negative margin cannot be PUT at all. Lifting that is a deliberate second change with
		// its own evidence, not a config edit. Note that a negative value here would NOT give the
		// "lower the merge threshold" behaviour it sounds like: merge never consults Margin, so a
		// negative Margin lowers only the split threshold and produces the inverted-band
		// oscillator described above. Measured on the w30 blob, lowering the MERGE side to 2000 m
		// leaves a 1005-member partition (85% of the original) while creating 110 fragments, 77 of
		// them singletons - so that edit is not worth making either.
		//
		// THE CONTROL IS NOT SYMMETRIC IN ITS EFFECT, and an observer who misses this will misread
		// the run. Lowering Margin lowers the SPLIT threshold, so the next membership pass splits
		// what now qualifies - the effect is immediate. Raising it again does NOT put the pieces
		// back: merge is fixed at D and never consults Margin, so a raise only stops FURTHER
		// splitting. The knob therefore ratchets the topology finer under load and leaves it there
		// until the members themselves come within D of each other.
		//
		// DEFAULT 1000 (Viktor 2026-09-12). Above ~240 nothing splits on the observed geometry, so
		// this is deliberately at the quiet end of the range: the knob exists to be lowered by an
		// operator who has a reason, not to do work on its own.
		["partition.margin"] = "1000",
		// T-0319 (Viktor 2026-09-30: hysteresis in DISTANCE, not time). Since T-0319 the node's split no
		// longer reads partition.margin above - that knob is the registry's co-location envelope
		// (D + margin) and nothing else. The split distance is its own knob:
		//   "auto" (default) = 4 x the sync distance in use (12000 m at 3000 m); a number = metres,
		//   floored at the sync distance.
		// r34 churned 582 topology commits per 10 min cluster-wide at 5000 m: pilots shuttle between
		// parked groups 6-10 km apart, merging on arrival and splitting on departure, and the [D, split]
		// band protects only the pair that merged, not the traveller's distance to the group it left.
		// The replay of the logged trajectories (tools/t0319) predicts -19% commits at 6000 m, -38% at
		// 8000 m, -51% at 10000 m, -62% at 12000 m; the largest partition grows from 72 to 105 members.
		// Same asymmetry as margin had: LOWERING it splits on the next pass, RAISING it re-merges nothing
		// until members come within D again.
		["partition.splitDistance"] = "auto",
		// #1300 (octahedron-1191, 2026-10-21): a partition with more members than this splits at a shorter distance than
		// partition.splitDistance - the split distance less 1000 m at a time, down to D + partition.margin (4000 m at
		// the defaults, the registry's co-location envelope; #1305: D + 2 x margin = 5000 m left a 266-member partition
		// with a 4.9 km widest gap whole), until every piece holds at most this many members. local-node-1 ran at sim 0.40 beside four nodes
		// at 1.00 on ONE partition of 578 members (parked grids along the lanes, 26 pilots, 77 % of the node's sim load):
		// its widest member gap was 9.7 km, under the 12 km split distance, so it never split, and the balancer moves a
		// partition only whole (an estimated 335 s of lag, never worth it). At 5 km it falls into 51 pieces, the largest
		// 215. A compact base stays whole at any cut. Read every membership pass; a non-negative integer; "0" = off.
		["partition.oversizeMembers"] = "150",
		// T-0319: judge a non-atomic merge on the cores (PartitionMembership.CoresHold): two layouts whose
		// only contact is a piece band-attached to its own core (a traveller between groups) are not
		// merged, so the split rule never has to undo it seconds later. Touching and attached grids merge
		// regardless. "false" restores the plain nearest-pair merge.
		["partition.coreMerge"] = "true",
		// T-0339 (design C): a piece that leaves one partition already within merge distance of another moves there in
		// one topology transfer instead of a split (new id) and a merge (id retired) in the same pass. Takes effect only
		// on a registry that applies transfers (heartbeat TopologyTransfer). "false" restores the split and the merge.
		["partition.transfer"] = "true",
		// T-0339 (design C2): widen that transfer to a band. A piece may also go to a partition whose nearest member is
		// D <= gap < min(split distance, D + piece speed x transferBandSeconds, its gap to the rest of its source - D/3)
		// (speed = the largest member speed of the piece, physics, no direction). Needs partition.transfer (and a registry
		// that applies transfers). Default "true" since the r116 live A/B (2026-10-05, ABAB 1 h at nas 32: short-lived
		// new ids -35 %, no client/server regression, handovers +12 %); "false" = design C exactly.
		["partition.transferBand"] = "true",
		["partition.transferBandSeconds"] = "60",
		// T-0339 (merge hold-off): for this many seconds after a split, the pieces of that split are not merged back with
		// EACH OTHER unless their nearest members come under D/2 (1500 m at 3000 m); they stay two partitions and are judged
		// again every pass. Merges with any other partition, atomic links (attached grids), touching grids, splits and
		// transfers are unaffected. r117: 36 % of the topology-born ids dead within 60 s were a piece re-merged with the
		// partition it had just split from (median life 25-33 s). Read every membership pass (live-changeable); a
		// non-negative integer like the other integer knobs (a negative value is refused). "0" (default) = off, the pass
		// is exactly what it was. Counters: heldMerges (last pass) and heldMergePairs (pairs held at least once since the
		// node started) in the node's partition report; one "topology merge_holdoff" log line per held pair.
		["partition.mergeHoldoffSeconds"] = "0",
		// T-0339 (split hysteresis): when a split would give a piece a NEW id while that piece is closing on a standing
		// partition (nearest gap >= D, falling faster than 1 m/s since the last pass, reaching D at that rate before the hold
		// runs out), the piece stays in its partition for up to this many seconds and is transferred to that partition once
		// it is within reach; it splits as before once it stops closing or the seconds run out (from the FIRST held pass).
		// w33 2026-10-06: young split-born ids died merging into a third partition 8-46 s after the split. Needs
		// partition.transfer. Read every membership pass; a non-negative integer. "0" (default) = off, the pass is exactly
		// what it was. Counters hysteresisHeld/-HeldTotal/-Transferred/-Expired/-Released in the node's partition report;
		// one "topology split_hysteresis" log line per hold start and per outcome.
		["partition.splitHysteresisSeconds"] = "0",
		// T-0292 - FAST MEMBERS (Viktor 2026-09-29). A grid group or unseated character faster than
		// fast.enterMps gets a partition of its own that is never merged, adopted into, migrated,
		// pulled or used as a wake trigger until it slows below fast.exitFraction x the threshold (FastMotionTracker).
		// Speed is displacement over time between ~1 s position samples, never physics velocity: a
		// warp drive teleports the grid ahead every tick. enterSamples/exitSamples are the dwell:
		// consecutive samples needed to enter/leave, so one teleport spike never enters.
		// The threshold is "auto" = the sync distance in use / 3 (1000 m/s at the default 3000 m; Viktor
		// 2026-09-29): a fast member crosses its sync distance in under 3 s. A number overrides it. The
		// hysteresis scales with it: exit below exitFraction x T, enter above (1 + enterMarginFraction) x T,
		// the margin being the measurement tolerance (a 1000 m/s speed cap never enters).
		["fast.enterMps"] = "auto",
		["fast.enterMarginFraction"] = "0.01",
		["fast.exitFraction"] = "0.8",
		["fast.enterSamples"] = "2",
		["fast.exitSamples"] = "5",
		// SE1-0084: the samples above come at most every 0.9 s, so a ship that jumps to warp speed is an
		// ordinary partition for ~2 s and stays fast ~4.5 s after it stopped. The tick detector also enters
		// when each of the last tickSteps game ticks moved faster than the threshold, and leaves when each of
		// the last tickSteps moved slower than the exit speed (FastMotionTracker, THE TICK DETECTOR). Keep it
		// small: the ship covers tickSteps x v / 60 before it counts as fast. 3 rejects a single teleport and
		// a teleport followed by a correction. tickDetector=false leaves the samples alone.
		["fast.tickSteps"] = "3",
		["fast.tickDetector"] = "true",
		// T-0138 - PROXY ANTENNA SCAN THROTTLE, banded by distance from this node's own content.
		//
		// WHY. bin/game-thread-profile put UpdateBroadcastersInRange at 56.31% of node wall time on
		// w31, WITH 81% of the world's antennas already disabled. The amplification is the cluster's
		// own: every remote antenna becomes a local MyProxyAntenna and node-1 held 1494 of them, each
		// rescanning 6x/s - ~9,000 sphere queries a second for antennas the node does not own. A hot
		// spare owning ZERO entities still carried the full 1893-receiver graph.
		//
		// THE BANDS (Viktor 2026-09-13). Distance is proxy -> nearest SURFACE of this node's partition
		// spheres, in multiples of the world SyncDistance D (3000 m). Vanilla scans every 10 frames;
		// a multiplier of N means every N-th of those, so N=1 is stock behaviour.
		//     within  10 x D  ->   6x slower  ->  every  60 frames (~1 s)
		//     within 100 x D  ->  30x slower  ->  every 300 frames (~5 s)
		//     beyond 100 x D  ->  60x slower  ->  every 600 frames (~10 s)
		// Set all three multipliers to 1 to revert to stock with three PUTs and no redeploy.
		//
		// NEVER THROTTLED: antennas on grids this node OWNS keep vanilla timing exactly, and a proxy
		// always scans on first sight so it relays immediately instead of waiting out a band.
		// THE COST: a proxy's relay set can be one band interval stale, so a MOVING ship far from this
		// node updates its relay neighbours up to ten seconds late. Lower distantMultiplier rather
		// than widening the bands if that ever matters - the far band is where the population is.
		["antenna.proxyScanNearMultiplier"] = "6",
		["antenna.proxyScanFarMultiplier"] = "30",
		["antenna.proxyScanDistantMultiplier"] = "60",
		["antenna.proxyScanNearBandSyncDistances"] = "10",
		["antenna.proxyScanFarBandSyncDistances"] = "100",
		// T-0193 OFFLINE PARTITIONS (Plan/Partitioning.md §3.6). A partition this node owns goes offline -
		// Final save, registry row unowned, entities closed, no node hosting it -
		// once, for graceSeconds without a break, it has had no member that is awake or moving, no
		// character, no client routed to it, no online player's body or controlled grid in it, and no
		// player or viewed camera (from the cluster position registry) within quietMeters of its sphere. Never within minOnlineSeconds
		// of the node first holding it, and a refused attempt waits retrySeconds. The gateway then
		// re-checks the geometry against every node's partitions before it commits.
		// offline.enabled=false stops NEW offline transitions at once; partitions already offline
		// still come back on demand (that side needs no switch).
		["offline.enabled"] = "true",
		["offline.compensate"] = "true",
		["offline.velocityReadiness"] = "true",
		["offline.settleFrames"] = "300",
		["offline.observeSeconds"] = "180",
		["offline.rotateProduction"] = "true",
		["offline.solarSteps"] = "72",
		["offline.cacheTolerance"] = "0.02",
		["offline.unknownBlocks"] = "Freeze",
		["offline.longObservation"] = "Skip",
		["offline.compensateMaxHours"] = "72",
		// SE-1096: a group's model stops at this many events; the rest of its interval is forfeited like past compensateMaxHours.
		// About 13 s of worker-thread time for the heaviest recorded base; 2,000,000 events still fail the group.
		["offline.compensateMaxEvents"] = "500000",
		["offline.callUp"] = "true",
		["offline.callUpSeconds"] = "120",
		["offline.callUpPerMinute"] = "6",
		["offline.callUpNoticeSeconds"] = "2",
		["offline.graceSeconds"] = "180",   // T-0237 (Viktor 2026-09-24): offline after 3 min of inactivity
		["offline.minOnlineSeconds"] = "60",   // T-0237: covers the route-to-attach window of a join (10-13 s)
		["offline.quietMeters"] = "8000",
		["offline.retrySeconds"] = "300",
		["offline.offerGrids"] = "true",   // SE-810: an offline offer carries the partition's grids; false leaves them to the backfill
		["offline.gridBackfillSeconds"] = "10",   // SE-810: per node, one offline row without grids read from its Final
		// Read by the GATEWAY and the World Authority from this same document: a client-bearing
		// partition or a player closer than wakeMeters (sphere gap) loads an offline partition, any
		// other partition at the co-location envelope D + m; both with a closing-speed lookahead of
		// leadSeconds plus the expected load time.
		["offline.wakeMeters"] = "5000",
		["offline.leadSeconds"] = "15",
		// GATEWAY only: the highest member speed, in m/s, the velocity lookahead of co-location and of the offline wake
		// counts; a pair's closing speed is capped at twice this. Set it to the world's top speed when a mod raises it
		// (Relative Top Speed at 360 m/s: 360), or a fast pair gets less warning than the relocation lead. Listed here so
		// the fan-out of the gateway's document is not refused by the nodes; they never read it.
		["lookahead.maxSpeed"] = "100",
		// GATEWAY only, managed (Quasar) deployments (SE-1168): every lifetimeSeconds / n (n serving regular nodes) the
		// oldest serving node is recycled onto the hot spare, so each node serves about lifetimeSeconds; 0 = never.
		// A recycle still draining after drainTimeoutSeconds closes the node: its own drain hands out or offlines the rest.
		["rotation.lifetimeSeconds"] = "7200",
		["rotation.drainTimeoutSeconds"] = "600",
		// GATEWAY only (T-0269): a wake whose demander's node is above this multiple of the mean node load
		// loads on the least-loaded node instead; 0 = always the demander's node.
		["offline.wakeOverloadRatio"] = "1.5",
		// #1354 (UnvisitedNpcSweeper): an NPC grid (a top-level grid whose big owners are all NPC identities) that has
		// been online for unvisitedMinutes with no player or viewed camera within visitRadius ("auto" = the sync
		// distance) is removed; inside a gap band of the mod-mes spawn pattern after unvisitedMinutes / gapRemovalFactor.
		// The sweep runs every sweepSeconds; 0 = no removals (the census and its unvisited counts keep running).
		// unvisitedMinutes 0 = no NPC grid is removed (#1379; it used to mean "at once").
		["npc.sweepSeconds"] = "60",
		["npc.unvisitedMinutes"] = "30",
		["npc.visitRadius"] = "auto",
		["npc.gapRemovalFactor"] = "4",
		// #1379, the cleanup policy of the same sweep. Never removed: a grid with a big owner that is not an NPC identity
		// (players, pilots), a grid with a big owner in a faction whose tag is in npc.sweepKeepFactions (comma list; the
		// Octahedron stations' OCT), and with keepTradeStations a faction trade station. Every other NPC-owned grid, MES
		// spawned or not (a piece split off an MES grid loses MES's storage key), goes by the npc.* rule above. A grid with
		// no big owner (container drops, debris, cut-off pieces) goes after unownedMinutes with no player or viewed camera
		// within unownedVisitRadius metres - a fly-by at the sync distance is not a visit - divided by gapRemovalFactor in a
		// gap band; 0 = never. A static one only with unownedStatic (the world's trade lane computers are static and ownerless).
		["npc.sweepKeepFactions"] = "OCT",
		// At most sweepMaxRemovals top-level grids go per pass and node, the most overdue first; 0 = no limit.
		["npc.sweepMaxRemovals"] = "50",
		["cleanup.keepTradeStations"] = "true",
		["cleanup.unownedMinutes"] = "5",
		["cleanup.unownedVisitRadius"] = "200",
		["cleanup.unownedStatic"] = "false",
		// #1390 (WreckageCleaner), whatever the owner: a dynamic physical group of at most wreckMaxBlocks blocks, of which at
		// most wreckMaxOtherBlocks are not cheap (a cheap block has only components of wreckCheapComponents, a comma list of
		// component subtype ids), is wreckage. Never removed: a group with a grid that has a player among its big owners and
		// a working terminal block while it has power, a kept owner's grid (npc.sweepKeepFactions, trade stations), a seated
		// or controlled grid, a grid mid-handover or in its import grace. It goes after wreckSeconds with no player or viewed
		// camera within the sync distance; while the node's sim speed (30 s mean) is below wreckAggressiveSimSpeed (out again
		// at +0.02; 0 = never), after wreckSeconds with none within wreckTouchMeters. wreckSeconds 0 = no periodic removal.
		// wreckBeforeOffline: the offline sweep and the drain remove a partition's wreckage right before its Final, no wait.
		// Off by default: the regression scenarios' probe grids are cheap armor cubes, wreckage by this rule.
		["cleanup.wreckSeconds"] = "0",
		["cleanup.wreckBeforeOffline"] = "false",
		["cleanup.wreckMaxBlocks"] = "50",
		["cleanup.wreckMaxOtherBlocks"] = "2",
		["cleanup.wreckCheapComponents"] = "SteelPlate,InteriorPlate,Construction,Girder,MetalGrid,SmallTube,LargeTube,BulletproofGlass",
		["cleanup.wreckAggressiveSimSpeed"] = "0.95",
		["cleanup.wreckTouchMeters"] = "50",
		// #1443 (DropContainerDirectorService), WORLD AUTHORITY: at most maxActive container drops ('Unknown Signal', personal
		// and strong alike) are active cluster-wide; 0 = no cap. Active = an unopened drop container still in the world: online
		// on any node or in an offline partition (the NPC census rows), until its vanilla removal deadline (an offline drop
		// keeps the one it had at the freeze and leaves the world at it when its partition loads), plus the drops the WA has
		// dispatched that the census does not show yet. A player whose drop falls due while the cluster is at the cap gets
		// none: the drop is skipped, not queued, and the player's next one is due an interval later. While the census is
		// unknown (none yet, or older than 60 s) every due drop is skipped. Read per sweep (5 s). SE-1146: default 5 (was 3), and
		// the WA deletes the oldest drop GPS markers over it (ContainerGpsSweep).
		["drops.maxActive"] = "5",
		// SE-1146: seconds a NEW container drop stays in the world before vanilla removes it (and the World Authority's drop GPS
		// markers last this long plus up to a minute); 0 = vanilla's (the drop system definition's 15 min). Read by the node at
		// each spawn and by the WA's marker sweep (5 s). A bench sets a few seconds so drops come and go within a lane.
		["drops.lifetimeSeconds"] = "0",
		// T-0265: carry each client's inventory delta counters across a handover. "false" restores the
		// old fresh-counter adopt, only so a live A/B can earn the red on the same build.
		["handover.inventoryCounters"] = "true",
		// T-0396: before a client-view adopt re-delivers a replicable the client had not finished receiving, destroy
		// what the client holds below it (RedeliverPolicy). "false" restores the bare destroy-and-create, which leaves
		// a ready child (a seated character) registered on the client - only so a bench can earn the red on one build.
		["handover.redeliverSubtree"] = "true",
		// T-0368: how long a target waits for a carried asteroid it has not generated (or not caught up) yet
		// before it destroys the client's copy and streams its own. "0" restores the immediate fallback.
		["handover.voxelCarryWaitSeconds"] = "10",
		// T-0388: a recovery that loads none of its saves' entities (all live elsewhere or stale) releases the empty
		// partition unowned once its empty Final is catalogued, and the registry reaps the row. "false" restores
		// holding it, unreported, until the node's drain releases it.
		["recovery.releaseEmpty"] = "true",
		// SE-1161: a recovery load that keeps failing. quarantineAfter (NODE): after this many failed loads in a row of one
		// partition, cold or warm recovery alike, the node quarantines it (/cluster/quarantine, repairable in place) and
		// the registry hands it to nobody while the node reports it; 0 = never. failedBackoffSeconds and
		// failedBackoffMaxSeconds (GATEWAY): after each failed load the registry holds the partition back from every claim
		// for failedBackoffSeconds, doubled per failure in a row up to failedBackoffMaxSeconds; 0 = no backoff.
		["recovery.quarantineAfter"] = "3",
		["recovery.failedBackoffSeconds"] = "5",
		["recovery.failedBackoffMaxSeconds"] = "300",
		// T-0400, GATEWAY only (the registry's retention pass): the files of the saves of a partition ARCHIVED more than
		// this many days ago are deleted from the per-host stores (the registry rows stay). 0 = off. Listed here so the
		// fan-out of the gateway's document is not refused by the nodes; they never read it.
		["retention.archiveFileDays"] = "7",
		// T-0401: time the parts of each handover import's MyEntities.Add (OnEntityAdd event, physics activation, the
		// rest, GC, thread CPU, lock contention) and append them to the activation's handover_phase line (ImportAddDiag).
		// Also the SOURCE side: the release's close sequence, rel* fields on the release / release_close line. Log-only,
		// a few clock reads per imported entity / per release frame. "false" = no timing, no extra fields.
		["diag.importAddTimers"] = "true",
		// T-0401: GridAddTrace logs a stack for a staged import's activation adds too. "false" (default) skips them (the
		// import path already logs them); "true" restores tracing them, for the A/B. Other adds are traced either way.
		["diag.gridAddTraceImports"] = "false",
		// T-0401 fix: a handover import Adds each entity and freezes it at once (physics off until the commit's thaw), so
		// the cluster-tree work its physics activation did inside the Add - a new HkWorld, or a ReorderClusters that
		// re-partitions the NEIGHBOURS' clusters (r121e live: 44.3 % of Add ms, 236 objects moved per reorder p50) - was
		// thrown away by the freeze. "true": a grid without a seated pilot is Added with its physics (and its hierarchy
		// children's bodies, e.g. a connector's detector) already disabled, and the thaw (RestoreFrozen) enables them as
		// recorded = the one real cluster placement. "false" = as before. Default "true" since 2026-10-08: live A/B r121i
		// PASS (WORK/grid 0.670x), injected-fault known positive for the post-thaw state compare, >= 3 h clean live.
		["import.deferPhysics"] = "true",
		// T-0401 follow-up: log one import_add_cluster_op line per Havok cluster create / reorder that happens INSIDE a
		// timed import Add (diag.importAddTimers), naming the physics body whose activation caused it (its entity type
		// and id, subpart flag, top-most parent and whether that parent's body is enabled) - to name the bodies a
		// deferred grid still activates. Log-only, inert outside a timed import Add. "false" (default) = off.
		["diag.importAddClusterTrace"] = "false",
		// T-0404: the commit's thaw (RestoreFrozen) enables each deferred body on its own, and every body whose box is
		// not inside ONE Havok cluster runs a ReorderClusters that re-partitions the neighbourhood (r121i ON window: the
		// 34 commits > 100 ms were 98 % reorder, p50 8 reorders moving 2682 objects, worst 1279 ms). "true": one
		// MyPhysics.EnsurePhysicsSpace over the union box of the bodies the thaw will enable, first, so each enable finds
		// its box inside a cluster - vanilla's own call for "make this space one cluster". "false" = as before.
		// Default true since the r140 OFF/ON/OFF read (2026-10-09, prereg t0404b, with thawEnsureGroups): thaw reorders
		// per class commit 3.1 -> 2.1, commit frames > 100 ms 0.44x, 0 aborts/residuals, no client harm (CH 01:24).
		["import.thawEnsureSpace"] = "true",
		// SE-1272: the commit's thaw enables every imported body awake (Havok adds a body active), so a resting pile woke
		// on each handover and the target solved all of it until it settled again: live 2026-11-08, Moon Station's 207
		// leftover trader shuttles (122 touching) took every rotation's target to 0.08-0.15 sim speed for 4.5-7.8 s.
		// "true": the source names the grids Havok had asleep (PluginHandoverService.AsleepKey); the thaw puts them back to
		// sleep, and again two frames later the ones the first step woke and that are still at rest. "false" = as before
		// (keep_asleep_check still logs how many were awake). Read per commit.
		["import.keepAsleep"] = "true",
		// t0404b, with import.thawEnsureSpace: one EnsurePhysicsSpace per spatial GROUP of the bodies (each group's box
		// below MyClusterTree.MaximumForSplit), each group's bodies enabled right after its ensure. The single union box
		// cannot be applied once it reaches MaximumForSplit (r139 ON: 54 of 133 eligible commits, 4.89 reorders each).
		// "true" (default since the r140 read, see import.thawEnsureSpace) = grouped; "false" = the single union box.
		["import.thawEnsureGroups"] = "true",
		// T-0404: prepare_network_ids looked for transferred identities already present on the target by building the
		// identity string of EVERY replicable on the node (~27 ms per transfer, flat in transfer size). An identity
		// starts with its root's EntityId, so only roots named by the transfer can match. "scoped" walks those roots
		// only; "full" is the old walk; "check" runs both, uses the full result and logs prepare_scope_check (the
		// equivalence guard, with a dropped-root known positive). Default "check" (CH 10:49): scoped goes live by config only
		// after >= 200 live transfers with 0 mismatches, a KP that went red and no VOID majority.
		["import.prepareScope"] = "check",
	};
	private static readonly object m_lock = new object();
	private static Dictionary<string, string> m_values = new Dictionary<string, string>(Defaults);
	private static long m_revision;

	public static long Revision => Interlocked.Read(ref m_revision);
	public static IEnumerable<string> Keys => Defaults.Keys;
	internal static string DefaultOf(string key) => Defaults[key];

	public static string Get(string key)
	{
		Dictionary<string, string> values = Volatile.Read(ref m_values);
		return values.TryGetValue(key, out string value) ? value
			: throw new ArgumentException("Unknown config key: " + key + "; known: " + string.Join(", ", Defaults.Keys));
	}

	public static bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);
	public static int GetInt(string key) => int.Parse(Get(key), CultureInfo.InvariantCulture);

	/// <summary>
	/// Applies the gateway's document as the desired state: the given keys win, every knob the
	/// document does not mention returns to its default (that is what `config set key=` means, and
	/// the gateway always fans out the whole document). Rejects the whole update on an unknown key
	/// or an unparsable value so a typo never half-applies. Returns the new revision, 0-change
	/// updates do not bump it.
	/// </summary>
	public static long Apply(IReadOnlyDictionary<string, string> update)
	{
		lock (m_lock)
		{
			Dictionary<string, string> next = new Dictionary<string, string>(Defaults);
			foreach ((string key, string value) in update)
			{
				if (!Defaults.ContainsKey(key))
					throw new ArgumentException("Unknown config key: " + key + "; known: " + string.Join(", ", Defaults.Keys));
				next[key] = Normalize(key, value ?? Defaults[key]);
			}
			List<string> changed = m_values
				.Where(x => next[x.Key] != x.Value)
				.Select(x => x.Key + "=" + next[x.Key] + (next[x.Key] == Defaults[x.Key] ? " (default)" : ""))
				.OrderBy(x => x)
				.ToList();
			if (changed.Count == 0) return m_revision;
			Volatile.Write(ref m_values, next);
			long revision = Interlocked.Increment(ref m_revision);
			HitchRecorder.ThresholdMs = int.Parse(next["hitch.thresholdMs"], CultureInfo.InvariantCulture);
			HitchRecorder.LogThresholdMs = int.Parse(next["hitch.logThresholdMs"], CultureInfo.InvariantCulture);
			Log("[ClusterPlugin] config revision=" + revision + " changed " + string.Join(" ", changed));
			return revision;
		}
	}

	/// <summary>
	/// Console as well as MyLog, for the same reason SetNodeState does it: the registration document
	/// is applied before MyLog exists, so the one line that says a rotated node picked the document
	/// up went nowhere — exactly the line HandoverImprovementsTesting.md §4 tells an operator to look
	/// for on a freshly started node.
	/// </summary>
	private static void Log(string line)
	{
		Console.WriteLine(line);
		MyLog.Default?.WriteLineAndConsole(line);
	}

	private static readonly Dictionary<string, string[]> Enumerations = new Dictionary<string, string[]>
	{
		["import.mode"] = new[] { "legacy", "staged" },
		// T-0120. Without this entry Normalize() has no branch that accepts a word: the key is not a
		// list, not an enumeration, its default is not true/false, so every PUT falls through to the
		// integer branch and throws "expects a non-negative integer, got off". The knob would have
		// been settable only by editing the source - i.e. the documented rollback would have failed
		// at the moment it was needed.
		["balance.unattributedLoad"] = new[] { "thread", "cpu", "off" },
		["offline.unknownBlocks"] = new[] { "Freeze", "Mean", "Block" },
		["offline.longObservation"] = new[] { "Skip", "InPlace" },
		["import.prepareScope"] = new[] { "scoped", "full", "check" },
	};

	// Comma lists: every item must be one of the allowed words; order is kept, blanks dropped.
	private static readonly Dictionary<string, string[]> Lists = new Dictionary<string, string[]>
	{
		["delta.appliers"] = new[] { "pose", "component", "scalar" },
	};

	// Knobs that also accept the word "auto" (derived at use).
	private static readonly HashSet<string> Auto = new HashSet<string> { "partition.splitDistance", "fast.enterMps", "npc.visitRadius" };

	// T-0387: knobs read with double.Parse at use. Without this set they fell through to the integer
	// branch, so their own defaults (0.01, 0.8, 1.5) were refused and a read-back-and-PUT of a node's
	// config returned 400: the knobs could only ever hold their defaults.
	private static readonly HashSet<string> Decimals = new HashSet<string>
	{
		"offline.cacheTolerance", "fast.enterMps", "fast.enterMarginFraction", "fast.exitFraction", "offline.wakeOverloadRatio",
		"npc.visitRadius", "npc.gapRemovalFactor", "cleanup.unownedVisitRadius", "cleanup.wreckAggressiveSimSpeed", "cleanup.wreckTouchMeters",
	};

	// #1379: comma lists of faction tags (letters, digits, '-' and '_'); order is kept, blanks dropped, empty allowed.
	// #1390: and of component subtype ids, which have the same shape.
	private static readonly HashSet<string> FactionTags = new HashSet<string> { "npc.sweepKeepFactions", "cleanup.wreckCheapComponents" };

	internal static string Normalize(string key, string value)
	{
		string fallback = Defaults[key];
		if (Auto.Contains(key) && string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
			return "auto";
		if (Decimals.Contains(key))
			return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0 && !double.IsInfinity(d)
				? d.ToString("R", CultureInfo.InvariantCulture)
				: throw new ArgumentException("Config key " + key + " expects a non-negative decimal" + (Auto.Contains(key) ? " or auto" : "") + ", got " + value);
		if (FactionTags.Contains(key))
		{
			List<string> tags = new List<string>();
			foreach (string raw in (value ?? string.Empty).Split(','))
			{
				string tag = raw.Trim();
				if (tag.Length == 0) continue;
				if (tag.Length > 32 || !tag.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
					throw new ArgumentException("Config key " + key + " expects a comma list of faction tags, got " + raw);
				tags.Add(tag);
			}
			return string.Join(",", tags);
		}
		if (Lists.TryGetValue(key, out string[] words))
		{
			List<string> items = new List<string>();
			foreach (string raw in (value ?? string.Empty).Split(','))
			{
				string item = raw.Trim();
				if (item.Length == 0) continue;
				items.Add(words.FirstOrDefault(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))
					?? throw new ArgumentException("Config key " + key + " accepts " + string.Join("/", words) + ", got " + item));
			}
			return string.Join(",", items);
		}
		if (Enumerations.TryGetValue(key, out string[] allowed))
			return allowed.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException("Config key " + key + " expects one of " + string.Join("/", allowed) + ", got " + value);
		if (fallback == "true" || fallback == "false")
			return bool.TryParse(value, out bool b) ? (b ? "true" : "false")
				: throw new ArgumentException("Config key " + key + " expects true/false, got " + value);
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0
			? i.ToString(CultureInfo.InvariantCulture)
			: throw new ArgumentException("Config key " + key + " expects a non-negative integer, got " + value);
	}

	/// <summary>
	/// The "values" object of a {revision, values} document, in either casing. The gateway serialises
	/// its document as a record, so the wire names are PascalCase ("Values"); this node emits
	/// lower-case; the PUT fan-out sends a bare {key: value} map with no wrapper at all. Matching
	/// only one casing silently dropped every registration document and every heartbeat pull, so a
	/// rotated node ran on the defaults while the gateway reported it converged (w22, 2026-08-21).
	/// </summary>
	private static bool TryGetValues(JsonElement document, out JsonElement values)
	{
		if (document.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty property in document.EnumerateObject())
				if (string.Equals(property.Name, "values", StringComparison.OrdinalIgnoreCase))
				{
					values = property.Value;
					return values.ValueKind == JsonValueKind.Object;
				}
		values = default;
		return false;
	}

	/// <summary>
	/// Splits a gateway document into the keys this build knows and the ones it does not. A key only
	/// a newer gateway knows is skipped and named, never fatal. Returns false with a reason when the
	/// document cannot be read at all, so a caller never latches a revision it did not apply.
	/// </summary>
	public static bool TryParseDocument(JsonElement document, out Dictionary<string, string> update, out List<string> unknown, out string error)
	{
		update = new Dictionary<string, string>();
		unknown = new List<string>();
		error = null;
		if (document.ValueKind != JsonValueKind.Object)
		{
			error = "document is not an object: " + document.GetRawText();
			return false;
		}
		JsonElement values = document;
		bool wrapped = document.EnumerateObject().Any(x => string.Equals(x.Name, "values", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(x.Name, "revision", StringComparison.OrdinalIgnoreCase));
		if (wrapped && !TryGetValues(document, out values))
		{
			error = "document has no 'values' object: " + document.GetRawText();
			return false;
		}
		foreach (JsonProperty property in values.EnumerateObject())
		{
			if (!Defaults.ContainsKey(property.Name)) { unknown.Add(property.Name); continue; }
			update[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
		}
		return true;
	}

	public static string Json()
	{
		Dictionary<string, string> values = Volatile.Read(ref m_values);
		return JsonSerializer.Serialize(new { revision = Revision, values = values.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value) });
	}

	/// <summary>Accepts either a flat {key: value} object or the {revision, values} shape this node emits.</summary>
	public static Dictionary<string, string> Parse(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Config body must be a JSON object");
		if (TryGetValues(root, out JsonElement values)) root = values;
		Dictionary<string, string> update = new Dictionary<string, string>();
		foreach (JsonProperty property in root.EnumerateObject())
			update[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
		return update;
	}
}
