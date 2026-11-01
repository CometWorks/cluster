# Concurrency review: finding races by reading the code

T-0296 (Viktor 2026-09-29). This is the method, the checklist, and the state of the first full pass
(rounds 1-4, 2026-09-29..10-01, findings F1-F26) and of round 5 (2026-10-21, cluster main 97026cd = 0.1.20,
findings F27-F41, section 10).
Every change that touches claims, recovery, handover, offline, balance or registry code gets the
**checklist** (section 9) in its review. CLUSTER-DEVELOPER.md section 5.2 points here.

The races that reached the live cluster so far were rarely in the registry itself. The registry
decides under ONE lock (`GatewayRegistry._lock`), fences every ownership change on
`(node, epoch, generation)`, and persists before it answers. They were in the space AROUND it:

* a node acting on its own **stale belief** about what the registry decided (it still thinks it
  owns, it thinks a claim is not yet live, it thinks nothing newer was saved);
* a node deciding on **another node's report** (a peer's `/cluster/partitions`, a heartbeat-fed
  weight) without asking which claim that report belongs to;
* **timing assumptions** (a lease TTL, a deadline) crossed by work that nobody bounded (a 5 s peer
  timeout per peer, a save fetch from a slow holder);
* two sides that each **defer to the other** ("someone else has it") and so leave it to nobody.

T-0295 (2026-09-29, partition 227734 lost) had two of these at once: node-1's granted claim was not
renewed while it took a census and fetched a save for 36 s against a 15 s lease (timing), and
node-5's census then screened out node-1's stale copy of the SAME partition as "live elsewhere"
(peer report without its claim). Both recoveries ended with nobody holding the content (liveness).

## 1. Shared-state inventory

One row per piece of state that more than one process or thread writes, or that a decision reads
from another process. The **fencing token** is what a writer must present and the reader must
check. An item with more than one writer and no token is a finding.

| state | lives in | writers | decision readers | token |
|---|---|---|---|---|
| partition ownership row (node, epoch, generation, lease, Recovering) | registry `_state.Partitions` (SQLite) | ClaimPartition, ClaimRecoveries, ReleasePartition, ExpireLeases, handover Commit/Abort, topology apply | every node (heartbeat reply, claim replies), gateway routing | generation (+ node epoch); all writes under `_lock` |
| lease expiry | same row | heartbeat (renews only on matching node/epoch/generation), claims | ExpireLeases, ClaimPartition (`LeaseExpires > now` = live owner) | same |
| node-local ownership `m_owned` | node PartitionPersistence | registry loop (claims), game thread (loads), lost-ownership handler | save registration, heartbeat report, drain | generation, re-validated by the registry on every save and heartbeat |
| node-local pending claims `m_claimedPending` | node PartitionPersistence | replication loop (grant), registry loop (failed recovery), game thread (loaded) | heartbeat (lease renewal) | generation. **T-0295: added at grant, not after materialization (6ee7f37)** |
| peer partition census | node-to-node `/cluster/partitions` | each node, about itself | recovery census (`SnapshotLiveEntitiesElsewhere`) | NONE on the wire: **T-0295: census entries now carry the holder partition, and a recovery of P ignores peers' copies of P (4e07125)**; startup-fenced copies are skipped (`fenced` flag) |
| offline partitions | registry `_state.OfflinePartitions` | offline registration, DropOfflineRow (heartbeat with members), directed load claims | ClaimPartition (refuses), ClaimRecoveries (directed only) | row presence + generation of the loading claim |
| save catalogue (generation, sequence, holders) | registry saves index | RegisterSave (owner only), replication holders, deletions | recovery cover, retention | (partition, generation, sequence) and owner check at registration |
| handover records | registry `_state.Handovers` | Begin/Activate/Commit/Abort/FinalizeReconciliation | ExpireLeases (exempts transferring rows), claims | source (node, epoch, generation) via RequireOwner at every step |
| pull demands | registry | RemotePullService (from a peer's `/cluster/partition-of`) | handover scheduler | registry re-checks ownership; Begin requires the source generation. Stale peer data at most produces a rejected demand |
| spare reservations | registry `_spareRecoveries` | PromoteSparesForDeadNodes, PruneSpareRecoveries | ClaimRecoveries | (node, epoch, until) |
| network-id blocks / entity-id ranges | Registry durable counters | AllocateNetworkIdBlock, AllocateEntityIdRange | node replication/entity creation | node epoch and Active state under `_lock`; T-0306: issued ranges must flush before answer; old coalesced counter reuse reproduced by process-exit control |
| character home | Registry CharacterHomes | hosted-player report, topology repoint | rejoin routing, recovery home stamps | **T-0304 deb263b:** node epoch, partition generation, both leases; changed-home version CAS under `_lock` |
| offline duplicate close permission | same home, PendingReap + WriteRevision | authorizing owner; exact executor acknowledgment; fenced takeover | node game-thread close | **T-0305 32ee10d:** durable permit/executor + issuance revision; no expiry; report revision barrier, consumed queue watermark |
| trash recovery grace | node per-grid deadlines | recovery arrival, per-grid leaving/arrival hooks | trash pass | **T-0303 5f6a447:** absolute UTC expiry carried by registered grid handover hook, preserving remaining duration |
| WA global save catalogue | Registry GlobalSaves | current WA register, replica holders | WA recovery | node epoch + WA authority generation; ordered sequence under `_lock` |
| node-control plan (records, per-host targets, stop requests, history, never-reused ids) | registry `_state.NodeControl` | NodeControlStep (1 Hz + every executor report), StopNode/SetHostTargets/ApplyHostsFile (admin), ExpireLeases (NodeControlOnDeath), PromoteSparesForDeadNodes (spare record -> Serving) | launcher/cluster-hostd (spawn, kill), ScaleInPass (drain receiver), registration gate | all under `_lock`; ids persisted+flushed before use (AllocateNodeId); a history id can never register again (node_id_retired) |
| node slot plan (Desired, IncumbentAction, Observed) | registry `_state.NodePlan` | node control, ExpireLeases (Missing, Draining->Kill), executor observations | launcher kill/spawn | slot key = node id (never reused); Gone acknowledges the commanded incumbent; a late report of a dropped history slot is ignored |
| executor lease per host (T-0321) | registry `_executorLeases` (boot-local) | PollExecutor under `_lock` | cluster-hostd (spawn/kill) | host-scoped Executor credential + (session, lease, sequence, fingerprint); a lost lease means re-acquire AND re-read the plan before acting (T-0328) |
| network-window lease hold (T-0321 P4) | predicate `LeaseHeld` over node rows | configuration + clock | EVERY reader of "lease lapsed = gone" | must be applied in every such reader: ExpireLeases had it, the handover reconciler and ClaimPartition did not (T-0327, `NodeLeaseLive`/`PartitionLeaseLive`); HolderUsable still reads a held holder as unusable, so recovery now waits while a newer save is held (T-0329) |
| per-start credentials (T-0321) | gateway boot (StartCredentials) | written once per start (temp file + atomic move), keyed by CLUSTER_START_ID | node/peer auth | start id |
| runtime voxel base fetched cross-host | node `m_bases` | VoxelJournalService fetch | voxel apply | compare-and-set (`TryUpdate(storage, fetched, basis)`) |
| reported holders (partitions a node simulates with members) | registry memory `_reportedWithMembers` (T-0296 audit) | Heartbeat | invariant audit only | incarnation (node, epoch) must still be live |

Added in round 5 (state introduced between 25199bb and 97026cd; **bold** = the row's finding):

| state | lives in | writers | decision readers | token |
|---|---|---|---|---|
| late-save hold `_lateSaveHold` (T-0347) | registry memory | ArmLateSaveHold, RegisterLateSaves, Heartbeat, deadline clear | ClaimRecoveries (all branches), InvariantAudit | owner row + host key under `_lock`; bounded by LateSaveHold (300 s) |
| unregistered-finals journal (T-0347) | `<node>-data/cluster/unregistered-finals.jsonl` | local-stop save worker (append + fsync); first node of the host at the next start (report, rename) | RegisterLateSave | row still the writer's at G, or unowned, not recovering, at exactly G+1; no newer save. **Nothing checks the member set: F39** |
| node registry apply queue (RegistryApplyQueue) | node `m_apply` | game thread (enqueue, abandon, finish), ClusterRegistryApply worker | CompleteRegistryApplies, heartbeat Expected, every capture path (`IsPending`) | op-id replay (topology); input generations via RequireOwner. **The local-stop capture does not check IsPending: F39** |
| expected ownership (`Operation.Expect`) | node | game thread at enqueue | heartbeat renews the row at Members=0 | exact (node, epoch, generation) |
| empty recoveries (T-0388) | node `m_emptyRecoveries` | game thread adds, registry loop releases | ReleaseEmptyRecoveries | generation + catalogued generation + lease renewed in this heartbeat |
| host save store `<runRoot>/saves/<host>` (T-0340) | host disk | every node of the host (link(2), never overwrites), cluster-hostd adopt + unlink | recovery fetch, serving, scrub | unique (partition, generation, sequence) names. Adopt + unlink of a legacy path is fenced against recovery claims and unexpired grants (F33: the adopt answers `busy`) |
| archive stamps + sweeps (T-0400) | registry SQLite `ArchiveRetention` + memory | ArchiveRetired, Rehydrate, SweepArchivedFiles, acks, HostStoreAdopt | PendingDeletes, Rehydrate | HandedOut flag + still-archived check under `_lock` |
| registry backup copy | `VACUUM INTO` off-lock (RegistryBackups) | CopyStore | the external node-data copier | SQLite snapshot, plus a retention hold on every save the copy catalogues until the artifact is released or expires (F32, FIXED #1275, self-test registry-backup-retention) |
| live-process lease hold (#1218, open PR) | registry predicate `LiveProcessHeld` | executor observations + clock | ExpireLeases, ClaimPartition, reconciler | bounded at 60 s after the last renewal. **Holder-usability readers ignore it: F37** |
| sliced import plan, activation guard, pacer tickets | node StagePlan / ImportActivationGuard / ImportActivatePacer | handler thread + one game-thread Invoke per slice | next slice, guarded patches | released on every exit (checked, section 10 round 5) |
| managed hot-spare set (#84) | registry `SpareNodes`, `NodeRegistration.Spare` | SeedManagedSpares, PromoteSparesForDeadNodes | Begin (refuses a spare target), ClaimRecoveries via `_spareRecoveries` | `_lock`; reservations keyed (node, epoch) |
| live cost-model override | gateway `_costModel` (volatile) | POST /balance/cost-model (thread pool) | automatic pass (read several times) | **none: F35** |
| `ClientSession.AttachRetryDetachedBy` | gateway session | RetryPlayerAttach | switch-hold exemption | cleared when its retry ends (F36, #1279) |
| stored ships (`TakenBy`, `Take`), tombstones, retrieval orders | WA ShipStorageService, global save | WA command handler under `Sync`; WA game thread | Take admission, blueprint, every node's Sweep | WA generation on the envelope; take guid + origin node. take time + fetch mark: an unfetched take expires (F27, #1269); **acked before durable: F28** |
| accepted contracts, settlements | WA contract system + global-save section | Accept/Withdraw/Finish/Fail commands (WA game thread), timeout sweep | Settle admission, AcceptHere | ledger op id (one id, two attempts); settled ids kept in the global-save record and refused at Accept (F30 fix, #1272). **F28, F29** |
| contract offers per block | block builder entry | ContractOffers.Adopt/Saving (game thread) | vanilla Accept -> WA AcceptHere | partition generation via the builder; Adopt skips offers known active or settled and rewrites the entry without them (F30 fix, #1272) |
| admin safe-zone set | WA `s_zones`/`s_version`, global save | Handle Set/Remove (fresh op id per retry) | Apply on every node | readers (generation, version); **writers unordered: F31** |
| fast gateway-pass flags | ClusterRuntime `m_gatewayPassWanted`/`Posting` | game thread, thread pool | the pass poster | **none: lost wake-up F40** |

Still open after round 2 (section 10): remaining plugin handover state; client attachment beyond the
route/SwitchLock checks listed in section 10. Round 5 covered the staged import and the gateway session changes.

## 2. Partition lifecycle and the pair table

States as the REGISTRY sees them (the only linearizable view):
`unowned` -> (claim | recovery claim | handover commit) -> `owned(node, gen)` -> (release | lease expiry
| handover commit to another node) -> `unowned(gen+1)`; `offline` is an unowned row that only a directed
load may claim; `retired` rows are inputs of a split/merge. Generation increments on every loss of
ownership, so it is the fencing token for everything written under an ownership.

States as a NODE sees them add the gaps: `granted-not-yet-renewing` (closed by 6ee7f37),
`claimed-pending-load`, `loaded-not-yet-confirmed`, `owned`, `lost-but-not-yet-discarded`,
`draining`, `startup-fenced`. Every node-only state is a window in which the node's belief and the
registry disagree. For each pair of transitions on the SAME partition, ask: what does each side
believe during the other's window, and what does it DO on that belief?

| pair (same partition) | prevented by | residual window | status |
|---|---|---|---|
| recover x recover | registry grants one claim at a time (row owned while leased) | the first claimant's lease lapsing before it renews (T-0295 first half); a peer's copy under the lapsed claim read as live by the second (T-0295 second half) | FIXED 6ee7f37 + 4e07125; bench P1-REC-14 forces it |
| offline x migrate | (T-0291) | stale queued Final after a node pause | known positive, see section 11 |
| handover x lease expiry | ExpireLeases exempts partitions with a Begun/Active handover whose source is alive | handover whose source died: expires normally | by design |
| claim x release by previous owner | ReleasePartition requires owner generation; a release of gen G after a claim at G+1 returns the new row | none found yet | ok |
| node stop (remove/recycle/decrease) x spare promotion (round 2) | PromoteSparesForDeadNodes skips spares with a stop request and dead nodes stopped on request; a promoted spare's record becomes Serving in the same lock | none | ok |
| node stop of a Starting node x its launch (round 2) | a stopped id goes to the history; its late registration is refused node_id_retired and the node fails itself | one wasted launch | ok |
| forced stop x drain (round 2) | ForceStop only Closes (the node's own drain writes Finals and offers offline); the slot is killed only after Empty/Dead | none | ok |
| network-window hold x handover reconciliation (round 2) | (was: none - the reconciler read the held target as dead and chose Recover) | committed handover rolled back to the last save | FIXED T-0327 c05dde3, self-test multihost-window-hold-readers |
| network-window hold x live-generation claim (round 2) | (was: none) | held partition taken | FIXED T-0327 |
| network-window hold x recovery holder choice (round 2) | (was: HolderUsable reads a held holder as unusable, Newest() skipped the newer save) | rollback to an older save with a usable holder | FIXED T-0329 f6f9ec8 (the partition waits the window out); covers through lineage parents not checked |
| executor lease lost x cluster-hostd step (round 2) | (was: one poll, 409 cleared the lease, then actualize on the old plan) | launch/kill from a stale plan | FIXED T-0328 b848eb7 |
| handover (Begun) x source death + lease expiry (round 2) | (was: Complete attempted on a moved row, threw every pass) | loop wedged | FIXED T-0332 |
| handover x node re-registration (round 2) | (was: record skipped forever) | partition stuck moving | FIXED T-0333 |
| Committed-unfinalized handover x target transfer-record sweep (round 2) | (was: 404 -> Recover released the live target) | rollback | FIXED T-0334 |
| handover (Begun) x network-window hold of the target (round 2) | T-0327 (second interleaving: Abort without the discard reaching the held target) | none after T-0327 | test handover-reconcile-held-lease |
| duplicate copy x anything (round 2) | runtime audit: two live incarnations reporting P with members > 30 s alarm | detection only, not prevention | audit |

| offline x recover (round 5) | TakeOffline under `_lock` (owner + generation, no Begun/Active handover, newest reachable save, bumps the generation); generic and warm recovery skip offline rows, ClaimPartition refuses them, only DirectedOfflineLoads claims one; no reaper, archive sweep or late save removes an offline row | none | ok |
| save-register x lease expiry (round 5) | RegisterSave checks (node, epoch, generation) under the lock that expires leases; RegisterLateSave accepts only the writer at G or an unowned, non-recovering row at exactly G+1 with no newer save; every loss of ownership bumps the generation exactly once | none for the registry; the member set of a late Final is not checked (F39) | ok |
| split/merge (lineage commit) x handover Begun (round 3, re-checked round 5) | the capture refuses on a pending apply or a generation mismatch before anything freezes; frozen layouts are locked | ApplyLineage checks RequireOwner only: Abort "fenced", record Begun until the next reconcile pass | transient, accepted |
| drain x recover (round 5) | the drainer's heartbeat keeps P leased; UpdateDrain skips frozen, pending-save and pending-apply layouts; a Final after a lapse is refused and the lost-ownership discard follows | rollback to the last registered save, never two copies | ok |
| local stop x pending registry apply (round 5) | (was: none - the local-stop selection is the one capture path without `IsPending`) | split-off members lost at the next start | F39 (#1284) |
| drain catch-up x network-window hold of the target (round 5) | (was: none - LightestDrainTarget filters retiring nodes only) | crew committed into a held node, rollback | F34 (#1277) |

All pairs the first pass listed are now filled in. (fast x handover and WA failover x global save were checked
in round 2, section 10.)

## 3. Check-then-act audit

A decision read at t0 and acted on at t1 without the registry re-checking the same token at t1.
Search patterns (run over `Shared/Cluster` and `ClusterRegistry`):

* peer reads: `GetStringAsync(` / `Get(client, "/registry/` followed by a decision and a later
  `Post(`; the decision must be re-validated by the POST's own token check;
* in-memory ownership checks outside the registry: `m_owned`, `IsOwned`, `ContainsKey(` on ownership
  maps, then a save, a release or a discard;
* timing: every HTTP call with a timeout inside a window bounded by a lease or deadline
  (`Timeout = `, `.GetAwaiter().GetResult()` in the replication/registry loops): sum the worst case
  and compare it with the lease;
* "skip because elsewhere": `continue` / `return` guarded by `live`, `elsewhere`, `owned by`,
  `already`, `stale` in recovery, drain and offline code (feeds section 4).
* **group decisions executed piecemeal** (added for T-0249): a plan about SEVERAL partitions or
  entities (a pinned group, a split's outputs, a merge's inputs) whose execution can stop after some
  members. Every such plan is all-or-nothing, or states why a partial result is safe. Look for loops
  over a planned set with `continue` on a per-member refusal.
* **asynchronous responses surviving local completion** (T-0305): removing an active item does
  not revoke replies already captured by another thread. A delayed grant/list reply must not
  recreate consumed work. Authorization cannot be acknowledged until the destructive action or
  refusal actually finishes, even if work was already dequeued. Keep a monotonic consumed watermark
  until all responses from the producer have drained; prune only at a proven quiescent point.
* **transition-scoped state not cleared on every exit** (added for T-0204): a flag, set, freeze or
  reservation taken at the start of a transition (handover, offline, drain, fast) must be released on
  EVERY exit: commit, abort, timeout, exception, node close. Find each `Mark*`/`Add`/`= true` at a
  transition's start and list its releases; a missing exit path is a finding. A time-based expiry
  longer than the transition is not a release.
* **a new path into an old transition** (round 5, F34, F39): list every caller of the transition's
  entry (`QueueCapture`, `Handover`, `Begin`) and diff its guard set against the existing callers.
  The local stop captured without `IsPending` while five other capture paths had it; the drain
  catch-up chose a target without the window-hold filter that the pair loop and CloseNode apply.
  Prefer moving the guard into the entry over adding it to the new caller.
* **acknowledged before durable** (round 5, F28): any answer that lets the caller act irreversibly
  (close the grids, pay, delete) must follow the durable write that a replacement process would
  restore. Grep each command handler for "mutate, then respond" with the save only requested
  (`SaveSoon`, `RequestGlobalSave`, a dirty flag). The registry persists before it answers; the
  World Authority's global commands did not.
* **a remote reservation whose answer is lost** (round 5, F27, F29): a command that reserves or
  consumes something on the other side (a take, a settlement) and then times out must either be
  retried under the SAME op id until it is answered (the ledger replays it), or be released by a
  bounded rule on the owning side. "Refuse locally and forget" leaks the reservation forever.
* **retries under a fresh op id** (round 5, F31): a retry that gets a new operation id is a new write,
  and it lands after any newer write from another node. Retry under the original id, or compare on the
  version the writer read.
* **unfenced read-modify-write and flag-pair wake-ups** (round 5, F35, F40): `x = f(x)` on a shared
  field from thread-pool handlers, and a "wanted"/"posting" flag pair whose worker exits between its
  last check and its reset. Grep `Interlocked.Exchange(ref .*Wanted` and `_field = With...(_field`.
* **two-step copies racing deletion** (round 5, F32, F33): a reader that copies or fetches in two steps
  (a catalogue first, the files later; a grant first, the fetch later) needs a hold on deleting or moving
  what step one named, until step two is done.

## 4. Liveness: "someone else has it"

Invariant: an offline or recovering partition is always in exactly one of (a) owned by a live node,
(b) durable on disk with a claimable save, (c) in a handover whose two ends are known.
Never "abandoned by all". Every skip, defer or refuse on the grounds that another node has it must
name the bounded re-check that guarantees someone ends up owning it. T-0295 violated it: node-5
skipped the members because node-1 had them, and node-1 discarded them because node-5 had the claim.

## 5. Linearization points

Every ownership change is one call under `GatewayRegistry._lock` that checks the token and persists
before answering: ClaimPartition, ClaimRecoveries, ReleasePartition, ExpireLeases, handover
Begin/Activate/Commit/Abort/FinalizeReconciliation, topology apply, offline registration. A node that
changes what it SIMULATES (loads, discards, closes entities) must do it only after the matching
registry answer, and must treat its own belief as stale the moment a heartbeat says otherwise.

## 6. Stale-copy discipline

A node's copy of a partition that the registry no longer assigns to it must never be treated as
live by another node. Consumers of peer-reported state, and how each is fenced:

* recovery census (`SnapshotLiveEntitiesElsewhere`): startup-fenced copies skipped (fenced flag);
  copies of the partition being recovered ignored (4e07125). A peer's copy of ANOTHER partition Q
  under a lapsed claim still screens an entity E out of this recovery. Round 5 verdict: safe when E is
  in Q's catalogued save (Q's next claimant loads it; moves recorded in the lineage are cut out of the
  cover by ResolveCover). Not safe when E was adopted into Q after Q's last save (adoption is not in
  the lineage) and Q's holder is about to be discarded: F41 (#1286, plausible). Fixed (#1286): a copy
  screens only while the registry assigns it to its node. The census keeps every copy's node and reads
  the partition rows of the copies that hold the recovered content; a copy is stale when its node is
  Dead or Q's row names another live node, uncertain when the row names no owner or a Dead or unknown
  one. A recovery whose content only such copies hold goes back to the replication thread for a fresh
  census at least 2 s apart, at most 8 times (`CLUSTER_RECOVERY_STALE_COPY_RECHECKS`; the claim stays pending
  and leased), then loads it. The boot-time assigned-save path has no deferral: a stale copy does not
  screen, an uncertain one (a booting peer's copy before its startup claim) still does. Red:
  `CLUSTER_RECOVERY_STALE_COPY_CHECK=0`; self-test section `census-stale-copy`.
* remote pull (`/cluster/partition-of`): registry re-validates; safe.

## 7. Forced-interleaving tests

Each finding gets a bench seam that FORCES the predicted interleaving (a delay or pause at the
suspected point), a red with the fix off, and the same run green with it on.
Example: P1-REC-14, seam `CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS`, red `CLUSTER_RECOVERY_LEASE_AT_GRANT=0`.

## 8. Runtime invariant audit

Built (round 2, 7d31964, `ClusterRegistry/InvariantAudit.cs`): once a second inside ExpireLeases, under the
registry lock.

* **duplicate**: two live incarnations report the same partition WITH members in their latest heartbeats for
  more than 30 s (`CLUSTER_AUDIT_DUPLICATE_SECONDS`). Generation-blind on purpose: a stale copy under an old
  generation is the case to catch (F1/T-0297, T-0307 class). A handover overlaps for about a second.
* **abandoned**: an unowned row, not recovering, not retired, not offline, with a reachable save, unclaimed for
  10 min (`CLUSTER_AUDIT_ABANDONED_SECONDS`) while a serving regular is up: the section 4 invariant ("abandoned
  by all", T-0295). Stranded rows (no reachable holder) stay with the stranded sweep (ReapUnrecoverable).

Each episode is reported once (event `invariant.violation`, console `INVARIANT VIOLATION <kind>: partition P`),
listed in `/diag` `registry.invariants` while it holds, and `invariant restored` when it clears; cluster-status
prints an INVARIANT VIOLATIONS section. Self-test `invariant-audit` (red earned with the audit call removed).
Not covered: one entity in two DIFFERENT partitions (reports carry member counts, not entity ids), and content
lost inside an owned partition (an empty Final). The duplicate grace rests on the heartbeat building ownerships
from `m_owned` plus recovery claims (PartitionPersistence.RegistryLoop): a handover target reports only after
adoption, the source until its release, so a healthy overlap is commit -> release (seconds).

Earned reds:

* **Bench drill, 2026-10-01 09:50 (r103, `tools/t0296/`).** Bench node-1 was stopped (SIGSTOP) and a
  substituted heartbeat named node-2's partition 2. Before: Total 0. Then `09:50:11 INVARIANT VIOLATION
  duplicate: partition 2 for 30s: reported with members by node-1, node-2; registry owner node-2`, listed in
  `/diag` Active. After SIGCONT: `09:50:17 invariant restored`, Active empty, Total 1.
* **Blind spot found by the drill, fixed in 3b642dc.** The 1 Hz registration heartbeat posts
  `Ownerships=[]` to the same route, and it wiped the node's report. It is now marked `Liveness` and
  not recorded. The self-test is red without the guard (`InvariantAudit.LivenessIsNoReport`).
* **Self-test re-run on main 97026cd, 2026-10-21 (`~/work/ticket-314/selftest/run.sh` on polaris).**
  `--self-test invariant-audit` is green. With the `AuditInvariants(now)` call in ExpireLeases removed,
  it is red: `invariant-audit: past the grace the duplicate alarms once`.

Live: cluster 0.1.20 carries the audit. The #1191 registry on ai (Quasar-managed, package b4ae591f…) contains
`AuditInvariants` and `INVARIANT VIOLATION`, and its run log since the 05:37 start had no violation lines
on 2026-10-21.

## 9. Review checklist (every change to claims, recovery, handover, offline, balance, registry)

1. Which rows of section 1 does the change read or write? Is every write fenced by the token?
2. Which node-only states (section 2) does it add or lengthen? What does each side believe meanwhile?
3. Any decision on a read acted on later: where is it re-validated?
4. Any "someone else has it": what bounded re-check guarantees an owner?
5. Any new work inside a lease or deadline window: worst case vs the window?
6. Any use of another node's report: which claim does that report belong to, and does it cover nested
   identities (seated characters, hierarchy children) or only top-level ids (T-0224)?
7. Any plan about several partitions or entities: all-or-nothing, or why a partial result is safe?
8. Any state taken at a transition's start: released on every exit (commit, abort, timeout,
   exception, close)? Does required state travel to the next owner with the transition?
9. Can an old async reply recreate completed work or acknowledge it before execution finishes?
   Where are consume, effect, acknowledgment and quiescent pruning ordered?
10. A forced-interleaving bench test for the window it closes.
11. Any hold, exemption or grace that changes what a liveness predicate means (a lease held in a network window,
    an Empty holder that stays usable): applied in EVERY reader of that predicate, not only the sweep that
    introduced it? Grep `LeaseExpires >` / `<=` and `State is ... Active` (T-0327).
12. Any loop-head pass over many items (reconciliation, sweeps): does one item's failure end the pass, and so
    starve later items and later stages? Every "skip because the node/epoch is unknown" needs a bounded fallback,
    and the absence of a peer's local record (swept, never had it) must not be read as a negative (T-0332..T-0334).
13. A new caller of an existing transition (a capture, a handover, a claim): does it carry every guard the
    other callers have? Better: is the guard inside the entry (F34, F39)?
14. Any answer that lets the caller act irreversibly: is the state it reports already durable where a
    replacement process would read it (F28)?
15. Any command that reserves something remotely: what releases it when the answer is lost or the caller
    dies, and does a retry reuse the op id (F27, F29, F31)?
16. Any two-step read (catalogue, then files; grant, then fetch): what holds step one's targets against
    deletion or moves until step two is done (F32, F33)?

## 10. First pass: status and findings

First pass COMPLETE (rounds 1-4, closed 2026-10-21 with round 5 below). Findings are ticketed one per issue;
content loss first, then stuck states, then transient errors. F15-F26 were found and fixed inside the review
rounds themselves (commit and self-test named in each row, all on main since Cluster 1.1.12, #27), so they
have no ticket of their own: a ticket for a fixed, shipped change would only be closed again. Round 5
findings (F27-F41) are open and each has its Vikunja ticket. Every fix still requires CM live
deployment/acceptance; a deterministic authorization fixture is not a live body-destruction trial.

The paragraph below is the round-1 status, kept as written:

Reviewed so far: registry ownership operations (ClaimPartition, ClaimRecoveries, ReleasePartition,
ExpireLeases, Heartbeat, handover Begin/Activate/Commit, TakeOffline); node recovery claim, load and
lost-ownership paths; drain release; offline transition; remote pull; network/entity-id allocation;
WA global-save writer fencing; character-home report and queued offline-body cleanup. This is not
a completed full pass; runtime invariant audit and the remaining rows below are still pending.

| # | finding | class | severity | ticket |
|---|---|---|---|---|
| F1 | a recovery claim the registry took back while it waited in the queue is loaded anyway (the heartbeat judged lost ownership only over `m_owned`), a duplicate of the new claimant's load until the next heartbeat discards it | check-then-act / node-only state `claimed-pending-load` | transient duplicate | T-0297 (fixed 349fb22, bench P1-REC-15) |
| F2 | a delayed character-home POST gets a new home version after its writer loses ownership, overwriting a newer home | unfenced shared writer | wrong rejoin/content loss | T-0304 (deb263b; forced former-owner red/fixed green; CAS also orders reports between still-owned partitions) |
| F3 | worker reads home Y, queues X, then a fresh report selects X before the next game-frame deletion; offline checks alone still close current X | check-then-act / delayed response replay | body/inventory loss | T-0305 (32ee10d; old-policy forced control fails, durable authorization + queue tests pass) |
| F4 | the membership pass splits/merges/adopts into a layout the node does not own (startup-fenced stale copy); split outputs are allocated and so CLAIMED, and the deferred topology commit never undoes the mutation | check-then-act across two layers (membership mutates before persistence checks ownership) | duplicate content beside the newer copy | T-0307 (ddc38b9: unowned layouts locked per pass; live positive node-2 'deferred, inputs not owned: 4,5' 2026-09-24/25) |
| F5 | a topology commit whose reply was lost is retried with the same op id after CloseNode; the Closed check ran before the op-id lookup, the node read the 409 as "never committed" and reverted layouts the registry had committed | lost reply + lifecycle transition between attempt and retry | members in retired inputs, outputs owned but unknown to the node | T-0308 (d9a0320: exact replay by the same incarnation answered while Closed) |
| F6 | balance-migration pins outlived their partition and target: progress renewed every pin (expired ones revived), and no pass removed pins of partitions no node reports or of targets that stopped serving | transition-scoped state not cleared on every exit + stale data deciding | half-moved group kept split; target starved of balancing while any train progressed | T-0311 (0666299: renew live pins only, per-pass prune; self-test balance-pin-lifecycle) |
| F7 | the network-window lease hold (T-0321 P4) was honoured by ExpireLeases only: ReconcileHandovers (HasLiveNode/HasLiveOwnership) and ClaimPartition read a held remote node as dead, chose Recover after a committed handover and released the held owner's partition to recovery | a hold that changes a liveness predicate applied in one reader only (checklist 11) | content loss (rollback to the last save) while a window is configured | T-0327 (c05dde3; self-test multihost-window-hold-readers red/green) |
| F8 | cluster-hostd after a lost executor lease: one poll got 409 lease-lost, the lease was cleared without raising, and actualize ran on the plan from before the outage | check-then-act on a stale plan | wrong launch/kill (transient) | T-0328 (b848eb7; test_no_action_on_a_stale_plan_after_the_lease_was_lost red/green) |
| F9 | recovery inside a network window: the newest save's live holder is a held remote node, Newest() skipped it and the auto-pick restored an older save | same predicate gap as F7, on the holder side | content loss (rollback past the last handover) while a window is configured | T-0329 (f6f9ec8; self-test multihost-window-recovery-holder red/green) |
| F10 | the gone-row release cached archive-named gone ids, and every rehydrate cleared the whole cache: the next pass rescanned every one (~0.3 s each) inside the `_rotation` hold; a pass that raced a rehydrate cached stale finds | over-broad invalidation of a cache read off-lock; work inside a lock-like hold | latency of pulls, handovers, CloseNode (no content loss) | T-0330 (bf78922; self-test gone-holder-archive-cache, red arm embedded) |
| F11 | balance pins toward a node that node control starts draining (Retiring) kept carrying members into it; ScaleInPass pinned each arrival out again | transition-scoped state not cleared on a NEW exit path (T-0235's guard covers operator drains only; the rotator now recycles through node control) | bounced handovers (transient) | T-0331 (35c0664; balance-pin-lifecycle red arm) |
| F12 | ReconcileHandovers: a Begun record whose source died after the target imported; lease expiry moved the row past the source generation; Complete -> Activate threw "fenced" every pass, with no per-record catch, at the head of the automatic loop | check-then-act on probes vs the row; an unguarded loop head | stuck state, cluster-wide (no balancing, drains, catalogue GC) | T-0332 (59169b1; self-test handover-reconcile-stale-source red/green) |
| F13 | ReconcileHandovers skipped forever a record whose node re-registered under a new epoch | "someone else has it" with no bounded fallback (a vanished epoch never returns) | stuck state (partition "moving", source frozen) | T-0333 (ff5bdb2; handover-reconcile-reregistered); ships only with F14 |
| F14 | a Committed record reconciled after the target swept its transfer record (1 h): the 404 read as "not ready", Recover released the live target's committed row | absence of local state read as a negative | content loss (rollback) | T-0334 (09c5f74; handover-reconcile-committed-pruned; red re-earned) |
| F15 | the client-bearing drains (CloseNode, DrainClosedNodes) and the operator /handover took the crew from the node report and never asked about a client still joining into the partition; its body left with the partition, its session stayed (T-0237 shape) | a guard applied on two of five paths into the same transition | duplicate body, one despawned | 033584a (self-test joining-client-placement, drain arms); no ticket yet |
| F16 | DrainClosedNodes picked the lightest serving node, which a retiring node becomes BECAUSE it drains; pull demands could target one too; ScaleInPass pinned the arrival straight out | a target predicate that ignores a transition the target is in (F11 family) | two client blackouts per drain (transient) | b1d5cc5 (drain-sources); no ticket yet |
| F17 | gateway-rs: ClusterRegistry ends a switch with two SessionUpdates (flush while still buffering, then unbuffer without a flush); a node packet between them was enqueued and never released | a two-step state change over an ordered channel that other traffic interleaves | one lost node-to-client packet per hit, replayed stale at the next switch | f7bf78f (relay test a_node_packet_between_the_flush_and_the_unbuffer_is_not_stranded); no ticket yet |
| F18 | FinishSwitching threw out of the player-binding re-attach (World Authority replaced or away while a switch completes) before clearing SwitchingBackend; CompleteSwitch's loop ended, RespawnSwitch skipped SwitchLock.Release | transition-scoped state not cleared on every exit (checklist 8) | clients buffered until the byte cap; a leaked SwitchLock wedges close-node under `_rotation` | e152d74 (finish-switching); no ticket yet |
| F19 | a JoinRequest answered on the thread pool after the relay IPC was lost and re-established: the accept went to the new connection, which never asked; ClusterRegistry kept a session gateway-rs does not have | a reply matched by a correlation that is only valid inside one connection (checklist 9) | the player is refused as a second join until the next relay loss (T-0314 class) | 9814d2c (relay-ipc-stale-answer); no ticket yet |
| F20 | a handover import keeps a live client-controlled entity and ImportFinish adopts it into the arriving layout; Discard left it there, unowned: fenced saves, three strikes, the fenced-orphan purge closes it | required state not handed back on the abort exit (checklist 8) | the body a connected client controls is closed ~15 s after an aborted handover | 3ebfb2d (node self-test membership-discard, run OFFLINE only; wiring not executed in a node); no ticket yet |
| F21 | a pull demand moved a pinned partition and left the pin; the next pass carried it off the demanding node as "balance-migration" | transition-scoped state ignored by a second mover (T-0311 list) | pull undone after 1 s, two handovers (transient) | f16205d (balance-pin-lifecycle); no ticket yet |
| F22 | "no co-location move chosen" read as "co-located" while the mover was only in handover backoff; the per-node planner pinned the other half of the split component to a third node | a null read as a positive (T-0311 list) | a client apart from its neighbours for a detour (transient) | 0cb76e0 (balance-waits-for-colocation); no ticket yet |
| F23 | T-0249 ignored the pin of a group whose member still to move is HandoverBlocked, in a local variable only; the pin outlived the block (60 s against 180 s, renewed by any train) and carried the reunited group apart again | transition-scoped state not cleared on an exit (checklist 8) | three handovers for one refused move; both nodes kept out of the paced planner (transient) | dafdf5b (balance-pin-lifecycle); no ticket yet |
| F24 | the T-0249 stall's only bounded exit is the pin expiring, and any other train's arrival renewed every live pin, the stalled train's too | a bounded fallback defeated by an unrelated renewal (section 4) | a group split across two nodes for as long as its mover fails and anything else migrates (stuck, self-healing on success) | 183f989 (balance-pin-lifecycle); no ticket yet |
| F25 | fast (T-0292) is checked when the planner pins; a pinned member that turned fast is its own component and was returned by ChooseMove as "balance-migration" | a guard applied at plan time only, on a state that changes during the transition | a handover at over 1000 m/s (transient) | 92dc8bd (balance-pin-lifecycle); no ticket yet |
| F26 | the automatic pass collected /partitions from every serving node in turn; one refused connection or 10 s game-thread timeout threw out of the whole pass | an unguarded loop head (F12 family); liveness | no wake, co-location or drain for any node while one node is silent (live 2026-09-30 20:27:45-20:31:01, 44 passes) | d89d27f (automatic-pass-partial); no ticket yet |

Checked and safe (handover lost replies, 2026-09-30): the Registry's handover Begin/Activate/Commit are
called in-process by the gateway orchestrator (DirectGateway 5053/5232/5430), and Activate/Commit
answer a replay from their recorded state BEFORE any lifecycle check, so the T-0308 order bug does not
apply; the node target `commit` returns on a matching CommittedGeneration (adoption once, T-0150) and
the source `release` returns the recorded count when already released, so PostCommittedWithRetry's
retries after a lost reply are idempotent.

Checked and safe: RemotePullService (registry re-validates the demand); TakeOffline (owner generation,
newest save, no handover in flight, one lock); ReleasePartition of a generation already moved on
(returns the new row, idempotent).

Balancer pinned groups (2026-09-30): F6 above. Checked and safe: ownership moving between plan and
execution (FencedClientlessHandover re-reads the source report and requires the owner's node and epoch;
Begin is generation-fenced); concurrent operator, pull and close handovers (all take `_rotation`); a
refused capture (refused before Freeze, later failures thaw + Abort); offline of a pinned partition
(refused). Plausible, not yet verified (listed in T-0311): a validation block disabling the pin only
inside ChooseMove (group bounce), `decision == null` read as "co-located" while a mover backs off, the
T-0249 stall keeping an already-split group split, pinned members not re-checked for fast/client state,
pull demands ignoring pins.

Round 2 (2026-10-01, df fc5d693, branch t0296-r2): T-0320 node control, T-0321 multi-host, T-0323 gone holders,
T-0319 core-merge, plus the areas listed below as still to review.

Checked and safe (T-0320 node control): every transition runs under `_lock` (NodeControlStep, StopNode,
SetHostTargets, ApplyHostsFile, executor observations, NodeControlOnDeath); a stopped Starting node that the launcher
spawns from a stale plan is refused at registration (node_id_retired) and fails itself; a stop requested before
registration keeps Desired=Kill and the launcher kills within 30 s, long before the node could serve; ForceStop only
Closes (Finals + offline through the node's own drain), the slot is killed only after Empty/Dead; spare promotion
skips spares with a stop and dead nodes stopped on request, and turns the promoted record Serving in the same lock;
AllocateNodeId persists and flushes before use. Not a race, noted: CheckStop resolves a positional selector
(`host#N`) and the execution re-resolves it, so the operation record can name a different node than the effect
when the host's order changed in between (milliseconds; positional selectors only).

Checked and safe (T-0321 multi-host): two agents per host (launcher.lock flock + PollExecutor refuses a second
session while the lease is active); gateway restart vs executor leases (lost on purpose, a delayed old request is
refused on lease id/sequence); a lost poll reply (identical body = replay); Ready read then lease lapse (batch
refused, retried per report); a late report of a dropped slot (refused, noise only); agent restart inside a window
(refused until window + grace); voxel base fetch (compare-and-set); start credentials (written once, atomic move);
JudgePeerRejection (under `_lock`, log-only verdict). The World Authority runs on the gateway host, never held.

Checked and safe (T-0323 gone holders): the off-lock archive scan (archive inserts come only from memory, queued
inserts are under `_lock`, a rehydrate counter defers the pass, step 3 re-checks memory and gone-ness); a late write
from a gone node (RegisterSave/RegisterHolder require a live node; ids never reused); row release x executor Gone
(the agent re-reads rows; a Kill incumbent's record stays live, so its row cannot be released yet); prune x proxy
(the data dir is kept while the row exists); SQLite TRUNCATE checkpoint x the read-only scan (busy=1, no throw,
measured). Residual, not ticketed: an evacuation drop or a proxy exit between a recovery claim and its fetch makes
the node fall back to an older save; this needs two new copies plus a GC pass, or a proxy exit, inside seconds, and
is the pre-existing dying-holder fallback. Gaps that are not races: voxel bases and global saves of gone holders get
no proxy; `Reachable` still offers saves of a gone holder whose host has no live node.

T-0319 core-merge: the membership pass is single-threaded, knobs are read per pass, refusals are re-judged every pass,
locked layouts are skipped (T-0307). Within-D refused pairs now rely on registry co-location (piecemeal group moves:
the T-0249/T-0311 rule area).

Checked and safe (handover internals, rotator targets, spare promotion, fast state; round 2): in-process
reconciliation never races a live handover (all hold `_rotation`); `/registry/handovers/begin` over HTTP has no
production caller; client-bearing Begin is generation-fenced and Commit runs under every crew RelayLock after re-checking
route health and bindings, a failure before commit restores sessions, discards, thaws and aborts; concurrent handovers
sharing a client take SwitchLocks in client-id order, RespawnSwitch takes the same lock; a client dropped mid-handover
follows the commit (routes are keyed by partition); spare promotion vs node-control activation (same lock, reservations
keyed by (node, epoch), pruned when not Active; a promoted spare later retired costs at most SpareRecoveryWindow);
fast state travels once with the handover (LocallyAdopted) and pull/balance exclude fast partitions; the close-node
drain target excludes spares and retiring nodes; gateway-rs targetReady checks the validation link and the dead-node
force-reconnect skips switching sessions. Plausible, no forced test yet: the client-bearing drains (CloseNode,
DrainClosedNodes, operator /handover with a crew) lack the joining-client / post-ready-settle guards of T-0237/T-0238/
T-0313; DrainClosedNodes and pull demands can pick a Retiring target (Begin does not refuse one; churn, F11 family);
a RespawnSwitch concurrent with a clientless handover of the medbay partition (routed-client guard checked before the
presync, not re-checked); one slow served node (GetPartitions 10 s timeout) fails the whole automatic pass - any change
there must keep SettleRetiring from reading a missing report as "owns nothing".

Checked and safe (WA failover, node consumers): a node accepts a World Authority message only at its current
generation (AcceptAuthorityMessage); a generation change clears the replica queue, fails pending commands and
resyncs every subsystem from a snapshot (GlobalStateChannel.AuthorityChanged); ApplyReplica re-checks the
generation under the lock after the handler and resyncs if it moved.

Round 3 (2026-10-01, df dcea7f2, branch t0296-r3): the candidates round 2 left open, one verdict each. CONFIRMED
means the interleaving was traced step by step in the code and a test shows it red; REFUTED names what prevents
it. Verdicts marked (sub-review) come from a read-only sub-review of that area and were not re-traced line by
line; treat them as leads until somebody does. Nothing in round 3 was run on a bench or the live cluster.

| candidate | verdict | evidence |
|---|---|---|
| client-bearing drains without the join guards | CONFIRMED, fixed (F15) | CloseNode and DrainClosedNodes called Handover with the reported crew; only the automatic path and FencedClientlessHandover asked JoiningClientPlacedIn. Now `ClientBearingMoveBlocker` (DirectGateway.cs:4879) in both drains and in Handover. The operator path refuses only for a joining client OUTSIDE the crew; a just-ready crew member is still moved as asked (T-0238 not applied there). |
| Retiring targets for DrainClosedNodes / pull demands | CONFIRMED, fixed (F16) | `LightestDrainTarget` (DirectGateway.cs:872); the pull handler refuses a retiring target. Begin itself still accepts a retiring target (operator /handover can name one). |
| RespawnSwitch x clientless handover of the medbay partition | REFUTED as a wrong state | After Begin: RouteClient throws busy while the partition is moving (GatewayRegistry.cs:4756), the respawn rolls back. Before Begin, route recorded inside the presync await (DirectGateway.cs:5632 guard, 5666 presync): if respawn-execute reaches the node first, the clientless capture refuses a partition with a connected client (ClusterControlServer.cs:1517) and the handover aborts; if the capture is first, the frozen layout is locked, the fresh character gets its own layout and Release closes only the captured Members and Descendants, so the character stays with its client on the source and co-location follows. Cost: one extra co-location. Read, not run. |
| one slow served node fails the whole automatic pass | CONFIRMED as liveness only, NOT fixed | `GetPartitions` (10 s timeout) per served node at DirectGateway.cs:499, before the offline wake, co-location, scale-in and drain catch-up; `ServedNodes` (441) has no lease filter, and a window-held remote node keeps serving. No wrong state. Skipping the node needs a decision first: PruneMigrationPins would drop its pins as "no node reports it" and SettleRetiring must not read the missing report as "owns nothing". Live: 0 "Automatic handover check failed" lines in the current run (04:57-06:28, 1.5 h only). NEEDS-LIVE-EVIDENCE over a full day before a change. |
| topology split/merge x handover | REFUTED, one transient (sub-review) | A layout with a frozen member is locked for adoption, split, merge and fast passes (PartitionManager.LockedLayouts); imported-not-committed content is frozen and unowned, so locked; Begin is generation-fenced. Transient, not fixed: ApplyLineage (GatewayRegistry.cs:3214) checks RequireOwner only, so a topology commit between Begin and the capture moves the generation; the capture is refused, Abort throws "fenced", the record stays Begun until the next reconciliation pass abandons it. One failed handover, joins held for a second. |
| the plugin handover state | CONFIRMED, fixed (F20); rest REFUTED (sub-review) | F20 above. Refuted: late/duplicate capture, commit, discard, thaw, release (each checks Released/Thawed/Discarded/CommittedGeneration); heartbeat lost-ownership vs an imported-not-committed partition (judged over the heartbeat's own m_owned snapshot; skips a live inbound transfer); saves in the window (frozen layouts are excluded). NEEDS-LIVE-EVIDENCE: no node-side timeout on a frozen source or on the node-wide NetworkId import guard when the gateway is gone (decider: "Another NetworkId import is in progress" for over a minute); an entity closed on the source while frozen is resurrected by the commit (decider: a trash removal between `captured` and `released`). |
| gateway-rs session state | CONFIRMED x3, fixed (F17, F18, F19) | Refuted (sub-review): an old-backend packet after a switch (link check), the client backlog after a rollback (drains only when forwardable), stale session commands (session id on every command), a late accept on the same connection (matched by the ask's session). NEEDS-LIVE-EVIDENCE: a late Disconnected of the old peer removing a NEW session of the same client on the same worker (decider: "Accepted client X" within 250 ms of "Rejected client X at the Gateway", then "disconnected"); a second concurrent join on another worker overwriting the pending owner (decider: "client-backlog-expired" about 10 s after "Accepted client X" with two remote ports). |
| character homes, two fresh admissions | REFUTED as a report race (sub-review) | No admission fence exists; the version CAS is learn-then-overwrite, so two nodes that both hold the player flap the home, and the node with the live session re-asserts within 10 s (only a node whose online players contain the client reports; the gateway keeps one session per client). The loser's body is reaped only offline, under a durable permit. The WRONG body winning needs a fork that already happened at admission (join answered where the body is not): that is the T-0237 class, not a home race. NEEDS-LIVE-EVIDENCE for fork frequency: "did not resolve ... the join falls back to the default backend" against "authorized stale body reap". |
| T-0311 list: a validation block disabling the pin only inside ChooseMove | CONFIRMED (sub-review), NOT fixed | The block clears a local variable (DirectGateway.cs:1224); the pin stays, so after the block lapses the group can be carried again and bounce. Transient churn. Fix sketch: drop the group's pins in the per-pass prune when a to-move member is validation-blocked. |
| T-0311 list: `decision == null` read as co-located | CONFIRMED, fixed (F22) | `RebalanceMayStart` (DirectGateway.cs:103). |
| T-0311 list: the T-0249 stall keeps a split group split | CONFIRMED as coded (sub-review), NOT fixed | DirectGateway.cs:1263 stalls the remaining members and never reunites the moved ones; exits are success, a validation block, or pin expiry without renewal. NEEDS-LIVE-EVIDENCE for frequency: "handover has failed K time(s)" for a pinned member after a sibling's `reason balance-migration`. |
| T-0311 list: pinned members not re-checked for fast / client state | CONFIRMED (sub-review), low, NOT fixed | Fast and client-bound are checked when planning only; a pinned member that turned fast is still carried. Its fast state travels with the handover, so churn only. |
| T-0311 list: pull demands ignoring pins | CONFIRMED, fixed (F21) | `PullPinRefusal` (DirectGateway.cs:1906). The operator /handover ignores pins the same way and is left as asked. |

Rules the round added to the reading: a guard on a transition belongs at the transition's single entry, or it is
missing on the next path that reaches it (F15, F21); a state change sent as two messages over a channel that
carries other traffic is one message late for whatever arrives between them (F17); a correlation number is a
token only inside the connection that issued it (F19).

Still open after round 3 (the four NOT-fixed rows are closed by round 4 below): the lineage commit during a
Begun handover, and every NEEDS-LIVE-EVIDENCE decider.
F20 needs its node self-test section and a forced discard run on a bench before it ships. Sub-review verdicts
need a second read. The runtime audit still owes a bench red (forced duplicate on the bench, needs Viktor's go).
For character homes, do not claim the two-admission case is solved by T-0304: it is bounded by the single
gateway session, not fenced.

Round 4 (2026-10-01, df 0f7bb17, branch t0296-r4): the four rows round 3 confirmed and did not fix, each re-traced
line by line (line numbers of the traces are df 0f7bb17, those of the new functions are the branch), with a test that is red on the df rule. All four are CONFIRMED and
fixed; the round 3 "(sub-review)" marks on them are replaced by these traces. Registry self-test 126/126 sections;
nothing was run on a bench or the live cluster.

| candidate | verdict | trace and fix |
|---|---|---|
| one slow served node fails the whole automatic pass | CONFIRMED (liveness), fixed (F26) | `served.Select(node => GetPartitions(node.Control))` (DirectGateway.cs:498-499) under one try (:483, catch :820); the node answers on its game thread (ClusterControlServer.cs:937); HttpClient timeout 10 s (:264); `ServedNodes` (:441) has no lease filter. A dead-but-Active or stalled node M throws there, before the wake (:555), the pair loop (:584), ScaleInPass (:574) and DrainClosedNodes (:806). Live: 69 failed passes in the last 12 gateway logs, 44 consecutive on 2026-09-30 20:27:45-20:31:01 ("Connection refused"), so the round 3 "0 lines" window was too short. Fix: `CollectReports` (:910) keeps the reports it got; a partial pass wakes, co-locates, hands over and drains among the reporting nodes and skips what reads an absence: PruneMigrationPins (pins of and toward M), ScaleInPass (SettleRetiring, NodeScaling.cs:153, closes an unreported retiring node), the planner (`MissingReportPause`), the route-contradiction clock (`ClientBearingAsReported`, read by RoutePinStale) and the published snapshot (offline admission then refuses "no fresh partition reports" after 15 s, as on df). Not changed: the 10 s wait is still inside the pass under `_rotation`; a malformed report still fails the pass. |
| a validation block disabling the pin only inside ChooseMove | CONFIRMED, fixed (F23) | :1222-1224 sets the local `pinned` to None when a member on the from-side is `hardBlocked`; the dictionary entry stays. {A, B} X -> Y: A arrives (:802 OnMigrationProgress releases A's pin, renews B's to +180 s); B is validation-blocked for 60 s (GatewayRegistry.cs:2962); the pass ignores the pin, B being the only mover and blocked flips the home (:1243-1252), A returns as "co-location"; at the lapse B's pin is still live, :1210-1215 reads the group as a migration and :1293 returns B as "balance-migration". Fix: ChooseMove reports the component (`pinVoided`), `DropVoidedPins` (:2093) removes its pins after the pair loop. Not applied while a scale-in drain runs (ScaleInPass re-pins every pass). |
| the T-0249 stall keeps a split group split | CONFIRMED, fixed (F24) | :1263 `continue`s the whole migrating component while a member to move is `blocked` (handover backoff, 2 s doubling to 300 s, :48); the arrived members stay on the target. The comment's exit, "the pin expires" (:1241), is defeated by :1940: any pinned partition's arrival renews EVERY live pin. So with one other train moving, the stall lasts as long as the mover fails. Fix: ChooseMove reports each migrating component's members still to move (`carried`); `StalledMigrationPins` (:2041) selects the trains with a mover that failed and has not succeeded since, and `RenewMigrationPins` skips them. The pins then expire 180 s after their last renewal and cost-based co-location reunites the group. Frequency on the live cluster is still unmeasured (decider unchanged: "handover has failed K time(s)" for a pinned member after a sibling's `reason balance-migration`). |
| pinned members not re-checked for fast / client state | fast: CONFIRMED, fixed (F25); client: REFUTED | Fast: the planner's `movable` (:1639) is the only check; `Components` (:1168) isolates a fast partition, the pin loop (:1210-1215) still finds its pin, the gap floors at the envelope (:1285-1289), and neither FencedClientlessHandover nor the node capture (ClusterControlServer.cs:1563 only carries the fast state) refuses. Fix: the per-pass prune drops the pin of a partition a non-retiring node reports fast. Client: a pinned member that gained a client is moved last (:1265) and behind the settle and joining-client deferrals (:763, :776); that is the designed path, not a missing re-check. |

Still open after round 4: the lineage commit during a Begun handover; every NEEDS-LIVE-EVIDENCE decider of round 3;
F20's node self-test and forced discard run; the runtime audit's bench red. New, from F26: the automatic pass still
waits 10 s per stalled node per pass while holding `_rotation` (pulls, operator handovers and CloseNode wait behind
it); F23-F26 have no bench or live run yet, and F24's frequency is unmeasured.

Closed since round 4, on the r103 bench on 2026-10-01 (tools/t0296/): F20 passed its node self-test
(`/self-test?section=membership-discard`, 30 sections) and a forced discard (09:53:07, "discard handed 1 kept
live entity back", the character stayed controlled). The audit's bench red was earned at 09:50 (section 8).

Round 5 (2026-10-21, cluster main 97026cd = Cluster 0.1.20, diffed against round 4's 25199bb: about 23k changed
lines in ClusterRegistry and Shared/Cluster). It applies the checklist (section 9) to the changes since round 4,
in five read-only slices: registry, node persistence, handover/import/gateway, World Authority state, and the
open stacked PRs (#1243 52d6d4c, #1221 2b6fd6f, #1218 7249e16, #392 f15f35c). CONFIRMED means the
interleaving was traced with line numbers and its key guard was re-read by the coordinating reviewer.
PLAUSIBLE names the decider. Nothing was run on a bench or the live cluster, and no fix is part of this round.

| # | finding | class | severity | ticket |
|---|---|---|---|---|
| F27 | a ship Take whose answer is lost (or whose node dies before Consume/Release) is never released; Hello is once per process and node ids are never reused | remote reservation, answer lost (checklist 15) | content loss: the stored ship is unretrievable forever | #1269 (a node releases a Take whose answer it lost; the WA releases a take whose blueprint was not fetched within 2 min and reports a fetched one held 10 min; self-test ship-storage-takes); #1316: an admin lists and settles a stranded take (`/admin/v1/ship-storage/takes`, P3-BLK-04-ADMIN) |
| F28 | the World Authority answers ship-storage and contract commands before the global save holding them is registered; a replacement WA restores the older save | acknowledged before durable (checklist 14) | content loss or duplication across a WA replacement | #1270; #1317 (ship storage commands whose answer is lost, which the held answer makes likelier: a Store is undone at the WA by an Unstore, Delete/Share/Order are retried under their op id; self-test ship-storage-retry) |
| F29 | a contract settlement is admitted at the WA, then both answers are lost: the WA forgot the contract and the node never pays | remote reservation, answer lost | content loss (contract and reward) | #1271 |
| F30 | after a partition rollback, a contract block's entry still carries an offer that is active at the WA; once settled it is offered again, and AcceptHere checks active contracts only | stale copy vs WA ledger | duplication (paid twice) | #1272, fixed: bench P3-CON-ROLLBACK |
| F31 | admin safe-zone retries use a fresh op id, so a retried Set lands after another node's Remove | retry under a fresh op id | wrong state, low | #1273 |
| F32 | online registry backup: retention deletes catalogued saves between CopyStore and the node-data copy | two-step copy racing deletion (checklist 16) | content loss in a RESTORED world | #1275, FIXED: retention hold until release/TTL (RegistryBackups.cs), self-test registry-backup-retention |
| F33 | a cluster-hostd host-store adopt + legacy unlink between a recovery claim and its fetch: the node falls back to an older save | two-step read racing a move | content loss (rollback) | #1276, FIXED: the adopt answers busy while a Recovering claim (its partition or a partition its cover reaches) or an unexpired grant names the copy (HostStoreMove.cs), self-test host-store-adopt-during-claim |
| F34 | the drain catch-up (DrainClosedNodes / LightestDrainTarget) hands a crew into a node whose lease is lapsed and held by a network window | hold applied in one reader only (checklist 11) + new path missing a guard (13) | content loss (rollback) + blackout, while a window is configured | #1277 |
| F35 | live cost-model override: unfenced read-modify-write from thread-pool handlers; one pass reads the model several times | unfenced RMW | transient, admin only | #1278 |
| F36 | `ClientSession.AttachRetryDetachedBy` is never cleared; later switches toward that node attach a granted binding mid-switch | transition-scoped state not cleared (checklist 8) | transient | #1279 (cleared in the retry's finally; self-test attach-retry-switch-hold) |
| F37 | (#1218 PR) the live-process lease hold is not known to HolderUsable / StoreState / NewerSaveHeldByWindow: recovery restores an older save while the newer one's holder is held | checklist 11 | content loss (rollback); not a regression vs the parent | #1282 (follows #1218), FIXED: NewerSaveHeld (was NewerSaveHeldByWindow) and StoreState consult the live-process hold, so the recovery waits up to its grace; HolderUsable still reads a held holder as unusable (it is no source). Self-tests live-process-hold-recovery (red before: C claimed S1) and live-process-hold-host-store |
| F38 | (#392 PR) a failed Closed-node placeholder drain leaves its claimed row unrenewed and unreleased; RecoveryPending pauses balancing until the 5 min reap | checklist 5 + 8 | stuck up to 5 min | #1283, FIXED: claimed just before Begin (after the report and presync), released (reaped as stillborn) on every exit before Commit (DirectGateway.FencedHandover), self-test closed-node-placeholder-lease-lapse |
| F39 | the local stop (T-0347) captures a layout whose topology apply is unanswered; the next start catalogues the partial Final as the newest save and the split-off layout's members are lost | new path into an old transition missing a guard (checklist 13) | content loss | #1284 |
| F40 | RequestGatewayPass loses a wake-up (wanted/posting flag pair) | flag-pair wake-up | transient | #1285 |
| F41 | PLAUSIBLE: the recovery census screens an entity against a doomed peer's copy of ANOTHER partition into which it was adopted after that partition's last save | stale-copy discipline (section 6) | content loss, narrow | #1286 (fixed: copies checked against the registry rows, bounded re-check) |

Checked and safe (round 5): offline x recover, save-register x lease expiry and drain x recover (section 2);
the lineage commit during a Begun handover is still the round-3 transient, and its committed variant is a
transient duplicate at worst. Also checked safe:
- the T-0347 late-save hold against the gone-row release;
- archive deletion against Rehydrate, late acks, and archiving with deletes outstanding (T-0400);
- the cold-start network-id reclaim (T-0344) and the restarted managed incarnation (#1195);
- the SaveFileWorker grant check;
- every exit of the staged import (pacer ticket, activation guard, serial mode, dormant copies, created
  entities, kept-live bodies);
- a discard, second handover or capture between import slices (the control server is single-listener and the
  gateway holds `_rotation`);
- the capture's 409 against Freeze;
- hot spares in managed deployments (#84) against node control and recovery claims;
- gateway-rs attach_pending (pushed under RoutingPushLock);
- the node apply queue (no queued apply survives a lost ownership; the queue stays ordered);
- the empty-recovery release;
- scrub against needed content;
- stored ships taken twice, stored-and-still-in-world, contracts settled twice, and block safe zones across
  handover (each guarded, with line references in the round's slice reports).
For the open PRs: #1243 and #1221 passed the checklist with no finding; #1218 creates no duplicate ownership
and its hold is bounded (60 s after the last renewal, 80 s after a Commit).

Plausible, not ticketed (decider in brackets):
- a claim answer lost across the node's Close makes the node exit Failed, because ClaimPartition checks
  Closed before the replay, unlike the topology T-0308 order ["partition mint refused ... (node phasing out)",
  then "registry epoch fenced" within 15 s];
- a topology apply at the head of the node's queue that keeps getting 5xx blocks every later claim and save,
  with no bound and no alarm ["topology WAL apply unavailable (attempt N" with N growing and no "registry
  unavailable"];
- a relay IPC send that fails mid-frame does not drop the peer, so the next frame starts mid-frame
  (RelayIpc.cs:271);
- a lost adopt reply on the target skips the abort's re-adopt guard;
- the offline-wake target and pull demands can pick a window-held node (the F34 class);
- under #1243, a scale-in node holding a HandoverBlocked member becomes the co-location home and pulls
  members into it;
- retrieval orders run by two holders of the terminal during a handover overlap;
- a voxel replay from the base tail onto a regenerated procedural asteroid;
- an archived adopt during an incomplete sweep leaks a store file.

Not a race, noted: Store, Take, the blueprint fetch, Delete and Order each block the node's game thread for
up to 10 s. A retrieval can therefore stall it for up to 20 s (checklist 5).

What round 5 says about the method: 13 of its 15 findings fall under rules that already existed. The 11 is
the most productive rule (F34 and F37 are T-0327 again, on predicates added after T-0327). The other two
needed new rules: F28 (acknowledged before durable) and F32 (two-step copies racing deletion). Both are now
section 3 patterns and checklist items 14 and 16.

## 11. Known positives

A known positive the method does not re-find means the method needs another rule.

Score of the five the ticket names: T-0295, T-0291 and T-0197 were re-found by the first cut. T-0249 and
T-0204 #3 were missed, and a rule was added for each. T-0295, the one that had to be found, is found by three
sections.

| ticket | race | re-found by | notes |
|---|---|---|---|
| T-0295 | recover x recover: a lapsed claim's copy screened out the new claim's members; the lease lapsed before renewal began | section 3 (timing inside a lease window), 4 (both gave up), 6 (peer report without its claim) | yes |
| T-0291 | offline x balance-migration capture after an 8.7 s node pause (stale queued Final) | section 2 pair offline x migrate, node-side window | yes |
| T-0197 | a client refused permanently while its partition was recovering | section 4 (refuse because recovery has it, no bounded re-check) | yes |
| T-0249 | a pinned group [517, 3641] moved piecemeal: 517's capture was refused, 3641 moved alone | missed by the first cut | rule added: group decisions executed piecemeal (section 3, checklist 7) |
| T-0204 #3 | IsLeavingByHandover stayed true for an hour after the handover ended | missed by the first cut | rule added: transition-scoped state not cleared on every exit (section 3, checklist 8) |

Done-list sweep (round 2, 2026-10-01; titles with race/stale/twice/duplicate/lost, then the ticket body):

| ticket | race | re-found by | notes |
|---|---|---|---|
| T-0080 | the heartbeat's lost-ownership check iterated the CURRENT m_owned against an older registry answer and discarded a split output committed 65 ms earlier | section 3 (in-memory ownership check acted on later; fixed by iterating the heartbeat's own snapshot and re-checking the local generation) | yes |
| T-0158 | node topology merge consumed partition 8372 between the gateway's handover trigger and the capture | section 2 pair split/merge x handover | yes (pair listed) |
| T-0200 | chat in global state missing on replicas after the World Authority was replaced | section 2 pair WA failover x global save | yes (pair listed; consumer side now checked, see section 10 round 2) |
| T-0224 | recovery's live-elsewhere census compared top-level entity ids only; a cockpit pilot live elsewhere was restored twice | partly section 6 | rule added: a census or duplicate check must cover nested identities (seated characters, hierarchy children), not only top-level ids |
| T-0150 | drains of a pile-up node: close-node 500, topology WAL apply 409 fenced, self-terminations and 30 grids lost | section 2 (drain x topology) + section 3 lost replies (the T-0308 class) | yes, as a pair; the content-loss path was not re-derived here |
| T-0099 | recovery claim retries without backoff burned a generation per failed claim on two nodes alternating | section 4 (bounded re-check) only partly | not an ownership race (no lost content); a retry-policy rule, not added |

### Added known positives (2026-09-30)

* T-0303: recovery trash grace existed on the source but was absent after handover. A checklist must
  follow required state across ownership changes, as well as checking each exit clears temporary flags.
* T-0304: a shared Registry lock serialized home reports without fencing their writer. Locking alone
  never proves a delayed writer is still authorized. Lease validity includes both node and partition.
* T-0305: a queued close can outlive its home observation. A second asynchronous lookup only moves
  that window. Durable authorization must block candidate home selection until consumed execution is
  resolved; revisions invalidate earlier reports after resolution. Delayed authorization/list responses
  can recreate locally completed work, and a retry ACK can precede execution unless completion is a
  separate state. Queue tests cover both and the enqueue-after-drain quiescence boundary. A seated
  candidate's durable location follows the existing topology seat hint independently of its grant token.
