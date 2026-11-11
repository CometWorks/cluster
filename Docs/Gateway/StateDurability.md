# State durability: flush on an interval, recover by sequence and hash

**Decision (Viktor, 2026-08-19). Applies to every service in the cluster that
writes state to disk: the Gateway registry, the node-side partition, voxel,
world-authority and payment journals, and anything added later.**

## The governing metric: maximum rollback time ~1 minute

Viktor, 2026-08-19: **maximum rollback time is the metric that defines everything
else.** Every other number here is derived from it, not chosen independently.

"Rollback" is how far back in world time a crash can throw the cluster. The target
is about a minute, which means: on an unclean stop, a partition may be shifted in
time and lose up to a minute of progress, and that is acceptable. Different
partitions may land on different points inside that window - they are not
required to be mutually consistent to the frame, only individually consistent and
individually within the bound.

Two consequences worth stating because they are easy to get backwards:

* **A flush interval longer than the target is a bug, not a tuning choice.** If a
  service flushes every five minutes it has a five-minute rollback, whatever the
  target says.
* **A clean stop must lose NOTHING.** The bound applies to crashes. A normal
  cluster save-and-stop that rolled anything back would be a defect.

### What a clean stop must therefore do

The naive reading - disconnect the players, wait a minute for the timers to come
round, then kill - satisfies the letter of it and is the wrong implementation.
Better, and what `cluster stop` already does: **request the saves, then stop what
has been saved.**

1. Disconnect players, so nothing new is being produced.
2. Ask every live partition and the World Authority to write a **Final** save now,
   rather than waiting for the periodic timer.
3. Wait for those saves to be catalogued and acknowledged - the completion signal,
   not a clock.
4. Then let the processes exit.

Waiting on the acknowledgement rather than on a timer is what makes the stop
lossless instead of merely probably-lossless, and it is faster in the normal case
because it does not sit through a timer that has nothing left to write.

When w18 was stopped on 2026-08-19 this path timed out and the CLI escalated to
terminate. That was not a flaw in the procedure: the gateway was saturated at
99 MB/s of fsynced writes with its global lock held, so the admin API could not
answer in time. The procedure is right; it was the thing this document exists to
fix that broke it.

## The rule

1. **State is flushed to disk at most once a minute**, not on every mutation.
   Services may stagger their flush phase so they do not all fsync at the same
   instant.
2. **Losing up to a minute of state on an unclean stop is acceptable.** Design
   for it; do not spend I/O to avoid it.
3. **Recovery must be decidable from sequence numbers and content hashes, never
   from timestamps.** Wall-clock has finite precision, is not monotonic across
   a restart, and two records written inside the same tick are
   indistinguishable by it. A monotonically increasing sequence number plus a
   checksum over the payload says exactly what a file contains and how it
   orders against its peers; a timestamp only suggests it.
4. **An interval flush is not a weaker guarantee, it is a different one.** What
   is promised is: whatever is on disk is internally consistent, self-verifying,
   and identifiable by sequence. What is *not* promised is that it is the newest
   state the process had in memory.

## Why

Three costs, all of which were being paid continuously and none of which bought
anything:

- **SSD wear.** The Gateway registry was measured at **99 MB/s of fsynced
  writes** on 2026-08-19 (w18, 3h20m in) - about **8.5 TB/day**. A 600 TBW
  drive is consumed in roughly ten weeks of that. The machine is a test rig
  today; the same code on a production host would destroy its storage.
- **I/O bandwidth**, which the nodes need for their own saves and the world.
- **CPU and latency.** The registry serialises its *entire* state per mutation
  and does it **inside the global registry lock**, so the fsync is not merely
  expensive, it blocks every handover, route lookup and partition query behind
  it. Measured: a trivial lock-taking read at 61 ms median / 226 ms p90 against
  0.2 ms for an unlocked route on the same listener. See
  `../../../cluster-regression/findings/the-registry-rewrites-itself-six-times-a-second.md`.

The cost also *grows*: per-mutation cost is proportional to state size, and
state size grows over a run, so the write rate climbed 64 -> 99 MB/s as the
registry went 10.3 -> 14.2 MB. Flushing on an interval removes the growth term
from the write path entirely - the cost of a flush still scales with state, but
the *number* of flushes stops scaling with activity.

## Player moderation

Player promotion state, including revisions and explicit None demotions, is part
of the WA global-save bundle and command journal. A clean stop saves it with the
ledger; replacement WAs replay changes after that save under the journal's existing
policy. Admission kick deadlines remain Gateway memory only and reset on Gateway
restart. See [player moderation](AdminAPI.md#player-moderation).

## SIGTERM commits and exits

A SIGTERM stops the Gateway process, not the cluster. The Registry commits what it holds and the process exits
with 143, without disconnecting or draining anything, so the nodes stay up for the respawn. The restarted
Registry recovers as after a crash, but no row it acknowledged is gone. The commit keeps the registry lock until
the exit, so nothing is acknowledged after it, and waits at most 5 s for that lock and the disk: past that the
process exits without the commit and the stop costs what a SIGKILL does (SE-842).

## A stop after a start where no World Authority ran

A global save normally counts for a clean stop only if it is a Final written after the stop began. A Gateway whose
World Authority never registered has no such save: an update whose nodes all died at startup, then abandoned. That
stop keeps the Final of the clean stop the Gateway started from, because nothing global can have changed since (SE-892).

All of this has to hold, otherwise `global` stays in the dirty list:

- This process consumed that clean marker in a warm start. A Recovery start doesn't qualify, and neither does a
  respawned Gateway on the same start generation, because its flush interval can have lost the row of an authority that
  ran.
- The World Authority generation still is that save's. An authority gets a new generation when it registers and another
  when it dies, so one that ran without saving is caught here.
- That save still has a location, isn't pending delete, and is the newest global save in the catalogue.

Partitions and voxels are judged as before. Regular nodes running without an authority can't lose global state, since it
lives only on the authority.

## A writer can be ahead of the catalogue

Nodes and the World Authority keep their save sequence in memory, so after an unclean Gateway stop the
restarted Registry can have rows only up to sequence 1 of a generation while the writer is at 5. The Registry
takes that writer's next save anyway: the partition owner row or the authority's node, epoch and generation
have already fenced stale writers, and the skipped slots are rows it lost, with their files orphaned on disk.
Only a sequence at or behind the newest catalogued one is refused. The exact-next-slot check this replaces
refused every later save of that writer, Final included, so such a stop never finished (SE-819).

A refused drain Final doesn't end the drain. The node thaws the partition and captures it again 5 s later, with
the generation and sequence the refusal taught it, and reports Empty only once every Final is accepted (SE-835).

A partition reaped as empty has save rows and no partition row. That pair keeps its world seed out of the next
start: the node skips the seed, and the Registry refuses a claim that would start the partition again at generation
1, below those saves (SE-835).

A refusal still in place is not silent. The World Authority retries the save it holds once a second and captures
no other, so its log carries the Registry's answer on each attempt (`Global save registration was fenced: ...`). The
Registry reports the age of the newest global save as `lifecycle.GlobalSaveAgeSeconds` in `/registry/status`, also
printed by `cluster-status`, and after 5 minutes without a new one on a Serving cluster adds the degraded reason
`globalSaveStale`, severe in the admin status (`health` is `Unhealthy`), until the next save registers (SE-843).

## What this does not relax

The diagnostic `events.jsonl` audit tail is separate from world state and commit
acknowledgements. It uses a bounded background queue and buffered batch flushes without
fsync; producers never wait for that I/O. Overload or an unclean stop can lose audit
records. Healthy graceful shutdown drains admitted events for up to five seconds and
persists a sequence high-water marker when the queue dropped final records. This
observability policy does not change any world-state or plugin-CAS durability guarantee.

- **Write ordering stays.** A snapshot is still written to a temporary file,
  fsynced, and atomically renamed *before* any log it supersedes is truncated.
  A crash must never leave a truncated log with no snapshot to pair it with.
- **Self-verification stays.** Every record keeps its magic, length and CRC.
  A torn tail is detected and discarded, not replayed.
- **Explicit flush points stay, and are immediate:** clean shutdown, an
  operator checkpoint request, and any transition a service defines as a
  commit point. The interval is a ceiling on *background* flushing, not a gag
  on deliberate ones.
- **Nothing here licenses losing data a caller was told was durable.** If an
  API answers "committed", either it flushed or it must not claim it.

## Implementation status (2026-08-19)

| service | interval | compression | staggering | sequence + hash |
| --- | --- | --- | --- | --- |
| Gateway registry | 60 s coalesced flush (`CLUSTER_PERSIST_INTERVAL_SECONDS`), immediate at boot/checkpoint/dispose and at commit points | none yet (row JSON; see "Registry store" below) | single writer | `StateSequence` on the state; SQLite WAL frames carry their own checksums (CGR1/CGR2/CGR3 files are read once, to migrate) |
| Node partition saves | adaptive 30-60 s with players, capped at `CLUSTER_MAX_ROLLBACK_SECONDS` (60) without - the backoff used to reach 300 s, which breached the bound | LZ4 / Zstd-3 (`PartitionSaveFile`, versioned header + trailer) | by construction: the periodic loop queues ONE partition per tick, oldest-catalogued first, frame-time aware, bounded queue | `SaveId(partition, generation, sequence)` + `BodyChecksum`, verified on every fetch/stage |
| World Authority | 60 s captures | same `PartitionSaveFile` format | single writer | gen/seq + xxh3 in `manifest.jsonl` |

Cross-service phase synchronisation was considered and deliberately NOT added: each
writer now emits ~1 MB once a minute, so even a direct collision is trivial, and the
node loop already spreads its own saves. Machinery without a measured problem.

Guarding the rule: `cluster-regression/bin/disk-write-guard` (monitor section 7d)
watches per-process `write_bytes` and stops the cluster on a sustained runaway - the
only monitor check permitted to end a run.

## Implementing it

A service adopting this rule needs four things:

- a `StateSequence` on the persisted state, incremented on every mutation and
  written with it, so a recovered file identifies itself;
- a dirty flag and a due-time, set by mutations, cleared by the flusher;
- a flusher that runs on the service's existing periodic loop - a dedicated
  thread is not required and adds a lock to reason about;
- an immediate-flush path for the explicit commit points above.

Recovery then reads the newest self-consistent snapshot, checks its CRC, and
takes its `StateSequence` as the authoritative "this is what survived". Peers
reconcile against that number, not against file mtimes.

## World Authority commands: answered only once a replacement would know them

The minute of rollback is uniform within one role. A World Authority command is not: the asking node acts on the
answer at once (closes a stored ship's grids, hands out a purchase, pays a reward), and a replacement WA restores
the newest recoverable global save. An answer sent before a replacement could know the command leaves the two
disagreeing for good. So the WA answers a command only when it is recoverable without itself:

* **Durable** (ship storage, contracts, admin safe zones; #1270): the answer waits for a global save that holds the
  command and that a regular node has replicated, 0.6-8 s. These commands are rare and none blocks a game thread.
* **Plugin commands** (`ExecuteGlobalAsync`): not held. They are answered at once and may not outlive a WA crash. A
  plugin that must know runs the reserved command `cluster.global-save` afterwards: see [PluginServices.md](PluginServices.md).
* **Journaled** (banking, factions, identities, player bindings, time of day; #1318): frequent, and banking and
  identity commands block the asking node's game thread, so the WA appends the command to the registry's
  `/registry/global-journal` and answers once the append is acknowledged, one round trip. A replacement replays the
  entries after its restored save's ledger order before it serves anything. The journal lives in the Gateway's
  memory: it outlives a WA, not the Gateway. An append the registry does not acknowledge within 1.5 s is answered
  anyway (counted as `failedOpen` in the WA's `/cluster/global-state` journal status), because the node gives up after
  5 s and an effect the node never heard of is the worse outcome; a fenced WA's append is refused and never answered.
  `CLUSTER_GLOBAL_JOURNAL=0` on the WA turns it off. A faction founded after the last save is not replayed (#1335):
  vanilla allocates the faction id at random.

## NPC factions a regular node loads: the World Authority's (#1365)

Vanilla founds every `IsDefault` faction of the definitions (mods such as MES and its packs) that the world lacks, at
each load, with a random faction id and a new founder identity. Regular nodes always load the split world, so each
would found its own set, which the WA's faction and identity snapshots replace later, after mods such as MES have
cached the founders. Instead a regular node, before vanilla founds anything, reads the WA's newest catalogued global
save (`/registry/global-saves/recovery`, then a verified local copy or a holder's `/cluster/global-save-file`) and
takes its factions and NPC identities. On a fresh cluster the load waits up to 180 s for the WA's first global save
(log `factions waiting_for_authority_save=true`); a default faction still missing then is founded locally and
reported (`WARNING: factions created_locally=true`). A node also warns when a faction snapshot replaces one of its
factions under another id (`factions snapshot replaced_local=true`), and once a minute about grids whose big owner is
an identity it does not know (`WARNING: grids_unknown_owner`).

## Executor acknowledgements and persistence faults

Fenced executor reports flush immediately only when they changed durable slot, Kill
acknowledgement or backoff state. Lease renewal and unchanged observations do not flush
other pending world mutations. The normal persistence interval still batches those.

After a persistence failure, the Gateway deliberately stays alive but fails closed:
control requests return 503, client relay pauses and handover advancement stops.
Supervision must check control health, not only PID liveness. Correct the storage fault
before restarting with the recorded generation. A new generation after dirty shutdown
requires the explicit stopped-fleet procedure in [ManagedDeployment.md](ManagedDeployment.md).

## Registry store: SQLite, row-level changes (T-0227, format v3)

Measured on w32 (2026-09-23): the registry state had grown to 374 MB of JSON (37 MB compressed).
A full-state flush - serialise, deflate, fsync - took 2.8-3.8 s INSIDE the global registry lock,
once every ~64 s and again at every offline-partition commit. Every node's synchronous gateway
call waited behind it: 52 of 52 node game-thread hitches >= 2 s fell within 5 s of a gateway
write, against 6 of 52 for the same stalls shifted by 30 s.

The registry now persists to `registry.db`, a SQLite database in WAL mode with `synchronous=FULL`:

- **In memory nothing changed.** `RegistryState` keeps its dictionaries and lists, tuned for lookup
  and change. The big collections (Partitions, Saves, GlobalSaves, Handovers, PartitionWeights,
  Lineage, AllocatedPartitionIds) are `Tracked*` subclasses. They record every key added, replaced
  or removed. A row changed in place (for example `owner.Node = null` or `save.Locations.Add`) is
  reported by the registry calling `Touch`.
- **On disk, a flush is one transaction of the rows that changed.** That is typically a few hundred
  rows. Each small property of the state (everything else) is one row in the `state` table,
  written only when its serialised bytes differ from the last write. Measured on a copy of the
  w32 registry (830k rows): **1.8 ms without the audit, about 12 ms with it**, against 2.8-3.8 s
  before. Statements are prepared once per table and reused with parameters.
- **SQLite's write-ahead log is the append-only log.** A commit appends frames and fsyncs them.
  Checkpoints fold the log into the file. The registry checkpoints (and truncates the log) at every
  start - the replayed state becomes the file - and at operator checkpoints. A torn final commit is
  dropped whole by SQLite. A corrupt database refuses to open (`InvalidDataException`), never
  starts empty.
- **Commit points are unchanged.** A plugin CAS, an offline-partition commit, a clean shutdown and
  an operator checkpoint are durable before they answer. A plugin write now also carries any
  coalesced mutation pending at that moment, which is strictly more durable than the old CGR3
  delta.
- **Completeness of change tracking.**
  - The self-test reloads the store after EVERY write in every section and compares it,
    property by property, with the live state. It was shown to fail by removing one `Touch`.
  - In production, every write also audits the live partition rows and a rotating slice of every
    table: stored bytes against the live serialisation (`CLUSTER_STORE_AUDIT_BUDGET`, default 50
    rows per table).
  - A row changed without being recorded is logged (`WARNING: registry store audit: ...`) and
    repaired by the next write.
  - `/registry/status` reports `storeAudited` and `storeAuditRepairs`.
- **Migration** happens once, at the first start of this build over a legacy registry: the old
  snapshot and WAL are replayed as before, written to `registry.db` in one transaction, and the file
  is compacted. The old files are renamed `*.migrated-<utc>`, kept and never read again. The w32
  copy took 13.6 s (legacy load plus 5.3 s of migration), gave a 420 MB database, and every
  persisted property compared equal after a reload. Later starts load in about 4 s.
- **Downgrade.** An older gateway binary finds no `registry.json` and would start an EMPTY
  registry. The release therefore declares `registry=cluster-registry-v3`. Rolling back means
  restoring the stopped pre-upgrade backup (or renaming the `*.migrated-*` files back and accepting
  the loss of everything since the migration), never pointing an old binary at a migrated run root.
- The database schema is documented in the clustering plan (`Plan/RegistryStore.md`).

### Archived saves of retired partitions (2026-09-24)

Retention keeps the newest save of every RETIRED partition as a margin (`keepRetired`). On w32 that
was 194,152 of 194,814 catalogued saves and 509 MB of an 816 MB registry heap: loaded at every start,
walked by every retention pass (about 850 ms under the registry lock, once a minute) and needed by
nothing live.

- **What is archived.** A retired partition whose saves are all unneeded: none is in a live
  partition's recovery cover or any snapshot's references, none has a delete pending, none is
  reserved for a replica, and no node owns or is recovering the partition. Always the WHOLE
  partition: its saves are either all in memory or all archived.
- **Where.** The `ArchivedSaves` table in `registry.db`, with the same row bytes as `Saves` plus the
  partition, the generation and whether the save had a location. The rows move in the SAME
  transaction that deletes them from `Saves`. Memory keeps one entry per archived partition (its
  newest located generation, used by lineage collapse).
- **Never replicated; files deleted only by archive retention.** Archived saves keep their files,
  which is the margin, until archive retention (below) deletes the files of a partition archived
  longer than `retention.archiveFileDays`. The rows are never deleted.
- **Brought back on demand.** A lookup that needs an archived save brings the whole partition
  back into memory: a live cover falling back on a retired ancestor, a save or holder report for it,
  a snapshot reference. The next retention pass archives it again once nothing needs it.
  `registry: partition P: N archived save(s) brought back into memory` logs each rehydration.
- **Batches.** Each retention pass archives at most `CLUSTER_ARCHIVE_BATCH` partitions (default
  5000), so the first start over a large registry spreads the move over passes.
- **Measured on the w32 copy.** 39 passes, 193,587 partitions archived and 0 rehydrations. The
  retention pass fell from about 850 ms to 6 ms. The registry heap after reopening fell from 816 MB
  to 288 MB. Each migration pass took 40-460 ms, falling as the backlog shrank; the archive step
  itself took 12-76 ms per batch.
- **`/registry/status`** reports `archivedPartitions`, `archivedSaves`, `archiveRehydrations`,
  `archiveMilliseconds` and `archiveRetention` (below).
- **Rollback.** An older build ignores the table: archived saves disappear from its catalogue (their
  files stay on the nodes and are never deleted), and live state is unaffected.

### Archive retention: files of long-archived partitions (T-0400, 2026-10-06)

Viktor 2026-10-05: delete the FILES of the saves of partitions archived more than 7 days ago; keep the
registry ROWS (lineage and history stay queryable).

- **Age = time since the partition was archived.** The `ArchiveRetention` table in `registry.db`
  holds one row per archived partition: `p`, `a` (archived, unix ms), `s` (1 = swept: no store file
  left to delete), `n` (locations held by a node id, see below). ArchiveRetired writes it in the same
  transaction as the archive move; a rehydrate removes it with the archived rows; a re-archive stamps
  afresh. Partitions archived before this build are stamped the first time it loads them (first
  sight), so their files go a full period after the deploy, never earlier. That load logs ONE line:
  `registry: archive retention: N archived partition(s) had no archive stamp and were stamped at first
  sight <time> (...); read-only proxy max(newest save Created, retiring lineage op time): M would
  already be past D day(s), U without a proxy; not used to delete`.
- **Knob `retention.archiveFileDays`** in the gateway's handover-config document (`PUT
  /admin/v1/handover-config`; live): days, default 7, `0` = off. A non-negative integer or refused
  (400) before anything changes; a stored value the registry cannot read counts as off.
- **Eligibility**, decided in the retention pass (once a minute, under the rotation gate and the
  registry lock) AFTER its `needed` walk, which brings back any archived partition a live cover
  reaches: archived at least the knob's days ago, not swept, and still archived after the walk; retired
  with its retiring lineage record collapsed; not owned, not recovering, not offline, not named dirty
  by a snapshot; none of its saves needed by a live cover or a snapshot.
- **What is deleted, and how.** Two kinds of location, each handed out by `GET /registry/deletions`
  exactly like a live save's pending delete:
  - a store file: held by `host:H` at exactly H's store path (`<runRoot>/saves/H/P-.../g...-s....mps`),
    while a node of H is serving, to the node that has H's delete turn;
  - a node's own copy (T-0412, 2026-10-21): held by a node id, to that node while it is Active with a
    live lease (the state in which a node polls its deletions). With or without the host store: a
    Quasar-managed cluster has none, so all its copies are node-held, in node dirs that live as long
    as the node. A holder that is registered but not Active right now (lease lapsed, Closed, Dead,
    Empty) leaves the partition unswept, read again on a later round: Quasar restarts the same node
    id into the same dir, and the copy goes once it is Active again.

  The node deletes the file and acks (`POST /registry/deletions/ack`), and the ack rewrites the
  ARCHIVED row in place: the location leaves `Locations`/`LocationHolders` and is recorded in
  `DeletedLocations` with the time. The row stays archived. When a partition's last file is acked it
  is marked swept. A node-held directive not acked within 10 minutes is recorded as deleted the same
  way, with one `WARNING: registry: archive retention: N node-held archived file(s) handed out more
  than 10 min ago and not acknowledged by their holder; recorded as deleted` line: only that node
  could act on it (it deleted the file and lost the ack, or refused it as outside its save root,
  T-0163), and it must not hold the batch for ever.
- **Not deleted: released nodes' copies.** A location held by a node whose registry row is released
  (a stopped node's data dir; in store mode a stopped row is released at once, T-0340 step 6) is never handed out (no node acts on it); it is counted in `n`
  and in the status (`legacyHolderLocations`) for T-0340's host sweep (`cluster hostd sweep`, host-store
  mode). The host-store adopt answer (`POST /registry/host-store/adopt`) carries `ArchiveFilesDue` for
  an archived save whose partition is past the age, so that sweep can delete such a file instead of
  adopting it; an adopted copy of an already swept partition re-opens its sweep.
- **Paced.** A pass reads at most `CLUSTER_ARCHIVE_FILE_BATCH` (default 250) archived rows and due
  stamps, straight from the store (the partition is NOT brought back; each row is read with a slim
  JSON reader that takes only SaveId, Node, Locations and LocationHolders), and keeps at most about
  that many files outstanding. Measured offline on a copy of the live w33 registry (2026-10-06 04:30:
  304,024 archived partitions, ~1 KB rows, store mode, clock +8 days, on ai at nice 19): 60 warm
  passes of 250 rows held the lock p50 2.7 ms, p90 5.6 ms, max 22 ms; the 5 passes that ran into a
  GC were the slow ones (max without a GC 7.2 ms); ~106 store files queued per pass (52-149). The
  same bench with a full deserialization per row: p50 3.2, p90 7.0, max 24 ms, 8 GC passes; at 500
  rows typical passes were ~5 ms with ~50 ms GC passes. The due count (off-lock, read-only
  connection) took ~6 ms. Each pass that reads anything logs `registry: archive retention: R
  archived row(s) read, Q file(s) queued ... X ms under the registry lock`.
- **Load cost.** The first load of an archive without stamps stamps every partition: 1.3-1.4 s on
  the w33 copy (load 5.4-5.6 s vs 4.4 s for r118); later loads ~50 ms. The table and its index add
  ~10 MB to `registry.db` (757 -> 767 MB for 304k partitions).
- **Restart.** The outstanding directives are memory only and reconstructible: the archived rows are
  the truth, a partition is swept only at its last ack, and the pass's cursor over unswept due
  partitions starts afresh, re-queueing whatever is still catalogued (an ack the restart lost is
  refused once; the node gets the directive again, deletes nothing and acks).
- **Recovery fails loudly.** A rehydrate that brings back a row with deleted files logs
  `registry: archived save <key> files deleted at <time> (archive retention) - not recoverable` (or
  `... from N location(s) - recoverable only from its M other location(s)`); a file handed out but
  not acked when its partition comes back is recorded as deleted (with a WARNING), never offered. A
  rehydrated row without locations is not marked pending delete, so it can be archived again.
- **Status** `archiveRetention`: `days`, `eligiblePartitions` (due and unswept, counted on a
  read-only connection before the lock), `outstandingDirectives`, `sweepingPartitions`,
  `deletedLocations` (ever, persisted), `legacyHolderLocations`, `unackedRecordedDeleted` (node-held
  directives recorded as deleted after the grace, since the start), `skippedPartitions` (last pass),
  `lastPassRows`, `lastPassMs`, `firstSightStamped`.
- **Why 112,911 store copies were left on w33 (T-0412).** The 2-day shortcut was live only
  2026-10-09 04:26-13:27; at 250 rows a pass it read ~130k of ~304k due rows before the knob went
  back to 7. 112,874 of the copies left were in unswept partitions beyond the pass's cursor, 37 in
  skipped ones: pace, not a guard.
- **Rollback.** An older build ignores `ArchiveRetention` and `DeletedLocations`; a partition it
  rehydrates and re-archives keeps a stale stamp row, which the next load of this build drops only if
  the partition is no longer archived (a re-archived one keeps its older stamp).

### Global save retention (SE-1346, 2026-11-11)

The World Authority writes a Periodic global save every minute, and until SE-1346 nothing deleted
one: GLOBAL grew by about 1,440 files a day on the World Authority and on every replica holder
(1,744 files, 613 MB on the octahedron-test World Authority node after a few days). The retention
pass (once a minute, `GlobalRetention.cs`) now releases global saves too:

- **Kept:** the newest `CLUSTER_RETENTION_GLOBAL_KEEP` (default 10; `0` turns retention off), every
  snapshot's `GlobalRef`, the last clean shutdown's, the one a warm start came from, the current
  shutdown Final, and every global save an outstanding registry backup catalogues (its retention hold,
  #1275).
- **The floor:** nothing at or above the newest global save that has its full copy target of usable
  holders is deleted (two nodes, one while only one node can hold anything; the hosts' copy target
  with the host store). Without such a save the pass releases no global save at all.
- **How:** the rest is marked `PendingDelete` and handed out by `GET /registry/deletions` like a
  partition save's copies, with `Global: true` and partition 0 in its `SaveId` (partition 0 is never a
  partition save's id). The node deletes the file and acks with partition 0, which the registry routes
  to the global row; the last ack removes the row. A regular node built before this change does the
  same without knowing the flag. The World Authority runs no persistence loop, so it applies the deletes
  for its own copies after each of its global saves. A global row whose last copy was on a gone holder
  is removed by the release pass.

### Copies above the copy target (SE-1347, 2026-11-11)

The replica queue only adds copies, and retention deleted a save's copies all at once, so a
retained save kept a copy on every node that ever held it: old owners after handovers, replicas,
and stopped nodes whose copies were copied again elsewhere. On a test cluster's backup every one of
9,231 partitions sat on 2 to 8 nodes. The retention pass (`CopyTrim.cs`) now trims each retained
partition save down to the copy target:

- **Kept:** two holders (on two hosts while more than one host serves; with the host store, the
  hosts' copy target), each live, usable and not marked corrupt: the partition's owner first, then
  Active nodes before Closed ones, then by name. A save with fewer such holders is not trimmed.
- **Not touched:** a save pending deletion, a save with a replica copy in flight, a partition that is
  recovering, being loaded offline or in an incomplete handover, and with the host store a save that
  still has a node-held location. Only a live holder's copy is trimmed; a stopped, gone or released
  holder's copy is left to the gone-holder release, or trimmed once its holder serves again.
- **How:** a trimmed copy leaves `Locations` at once, so nothing counts or offers it, and waits in the
  save's `TrimmedLocations` until its holder deletes it through `GET /registry/deletions` and acks. The
  replica queue hands that holder no copy of that save meanwhile, and a holder that registers the
  location again cancels the trim. A partition with a trim waiting is not archived. A trim on a gone
  holder is dropped with the holder's other locations.
- **Paced:** at most `CLUSTER_RETENTION_TRIM_BATCH` copies a pass (default 1000, `0` = off).
  `/registry/status` counts the waiting ones as `pendingTrims` and all trimmed since the Gateway
  started as `trimmedCopies`.

`manifest.jsonl` in a node's data dir (a line per save file written or deleted) had no bound and
nothing reads it. Past 4 MB it now moves to `manifest.jsonl.1`, replacing the previous one.

## Durable plugin writes and Registry format v2 (superseded by the store above)

Successful plugin CAS is an explicit durable commit. The first write after Gateway
startup commits a full baseline; subsequent writes append a CRC-protected `CGR3`
record containing one mutation and its replay result, sharing the payload once on
disk. Replay eviction keys are recorded with that mutation. Each record is fsynced
before success is returned. Background world changes retain interval batching;
checkpoint rollover occasionally serializes the complete Registry.

Ordering and fsync still run under the Registry lock. This bounds serialization work
per plugin write but does not eliminate storage latency. New CAS attempts are limited
to 10 per plugin and 20 cluster-wide in each one-second window; excess requests return
`Capacity`. Retrying a retained successful operation does not consume this budget.
Snapshot/old-WAL recovery uses `StateSequence` so a crash between snapshot publication
and WAL truncation cannot overwrite newer state. A torn tail is discarded; a complete
record with an invalid CRC fails recovery.

The release declares `registry=cluster-registry-v2`. Its reader accepts existing
CGR1/CGR2 state, but old binaries cannot read CGR3. Quasar intentionally refuses
in-place activation/restore between different declared format maps. Do not change
capability metadata to bypass that fence. Preserve a verified stopped backup before
migration; reverting to v1 requires restoring that pre-upgrade backup with compatible
binaries into a separate stopped deployment, accepting loss of later changes. No
in-place v2-to-v1 downgrade or managed v1-to-v2 migrator is supplied by this release.
