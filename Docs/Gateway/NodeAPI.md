# Node control HTTP API (operational subset)

Every dedicated-server node exposes a control server at
`CLUSTER_CONTROL_BIND:CLUSTER_CONTROL_PORT` (default bind `127.0.0.1`). Binding
beyond loopback requires `CLUSTER_ADMIN_TOKEN`; requests then need
`Authorization: Bearer <token>`. All routes are rooted at `/cluster`.

### Authentication (T-0321 multi-host)

A LAN bind listens on two prefixes, `http://127.0.0.1:<port>/cluster/` and `http://<address>:<port>/cluster/`. The
credential is decided per request from the prefix it arrived on (`ClusterNet.NodeControlAccess`):

- loopback prefix: open, as every request was on a loopback-bound node (local tools keep working);
- LAN prefix, peer routes (`global-save-file`, `voxel-base-file`, `snapshot`, `GET partitions`, `igc-deliver`,
  `mod-deliver`, `mod-locate`, `*-execute`, `partition-of`, `voxel-journal*`): the join token or the admin token;
- LAN prefix, every other route: the admin token;
- grant routes (`save-file`, `prepare-import`, `import`): the one-time registry grant, as before.

Nodes always dial a peer's advertised address, so two nodes of one host authenticate each other too. A refusal is
JSON `{"error", "code", "node", "epoch"}`: `credential_required` (no token, 401), `admin_scope_required` (the join
token on an admin route, 403) or `credential_rejected` (a token this node does not accept, 401). A node that gets
`credential_rejected` from a PEER reports it to `POST /registry/peer-rejections` and carries on; the same code (or
`stale_incarnation` / `node_id_retired`) from the REGISTRY makes it log the incident (token fingerprint only) and
exit through the epoch-fence path without saving.

Source of truth: `Shared/Cluster/ClusterControlServer.cs` (compiled into both node plugins)
plus `GET /cluster/self-test` and `POST /cluster/igc-test-antenna?grid=&enabled=` (sets
every antenna of a grid on or off server-side; used by operational A/B tooling).

Test-only routes live in `Test/ClusterTestPlugin`, a local plugin the scenario suite installs next
to the cluster plugins; a release deployment does not serve them. It registers them through
`ClusterControlServer.AddRouteExtension`, which runs after authorization and before the product
routes, under the same error handling.

## Node & cluster state

| Endpoint | Purpose |
| --- | --- |
| `GET /cluster/status` | node role, phase, partitions, sim speed |
| `GET /cluster/node-state` | lifecycle (`Registering`, `Serving`, `Draining`, `Failed`, ...) with detail |
| `GET /cluster/authority-state` | WA session clock, generation, global-state sequence |
| `GET /cluster/global-state` / `POST /cluster/global-state/publish` | ordered global-state fan-out |
| `POST /cluster/chat-send?sender=&text=` / `GET /cluster/chat-history?cursor=&limit=` | cluster chat entry and the WA-captured accepted-chat tail, used by the Gateway `/admin/v1/chat*` routes |
| `GET /cluster/ship-storage/takes` / `POST /cluster/ship-storage/settle?id=&take=&action=release\|consume` | World Authority only: the held ship storage takes and the admin settle of a stranded one (409 with the reason when refused), used by the Gateway `/admin/v1/ship-storage/*` routes (#1316) |
| `GET /cluster/diag` | node-local diag section (the Gateway aggregates these) |
| `GET /cluster/self-test` | node-side self-test sections (membership, save containers, ...) |
| `GET /cluster/online-players`, `GET /cluster/characters`, `GET /cluster/character-registry` | who is where |
| `GET /cluster/player-state`, `GET /cluster/client-state`, `GET /cluster/control-liveness` | per-client control map |
| `POST /cluster/reassert-control` | re-issue control for a stuck client |
| `GET`/`PUT /cluster/config` | runtime handover knobs (`tee.async`, `import.entitiesPerFrame`, `import.mode` = `legacy`|`staged`, `import.stageWorkers`, `import.activatePerFrame` (staged: entities per activate frame, 0 = one frame; default 20; see `Docs/ImportActivateSlices.md`), `import.activateBudgetMs` (sliced activate only: a slice also stops before the group that would take its estimated MyEntities.Add ms past this budget, estimate = the previous slice's Add ms per entity, an import's first slice the node's running average; never larger than the count cap, at least one whole group; default 0 = the count cap alone; see `Docs/ImportActivateSlices.md`), `import.activateMsPerSecond` (T-0392, sliced activate only: the node spends at most N ms of measured activation game-thread time per rolling second across all its imports, a frame that does not fit waits, slices sized to the remaining allowance, oldest import first, forced once the node's pacing waits in the last 10 s exceed 2 s; default 0 = no frame waits; see `Docs/ImportActivateSlices.md`), `import.warmup`, `replica.globalFsync` (T-0397: fsync each global-save replica; `false` = kernel writeback), `replica.scrubSeconds` (T-0399: when the replication worker is idle, re-read one held global-save copy every N seconds; a copy that fails size/checksum is renamed `<name>.corrupt` and withdrawn via `POST /registry/global-saves/holders?withdraw=1`; in host-store mode only the host's lowest-id Active node scrubs; default 6, 0 = off), `presync.enabled` (read by the Gateway), `presync.ttlSeconds`, `delta.appliers`, `release.closePerFrame`, `hitch.thresholdMs`, `partition.transferBand` (T-0339 C2: a split-off piece may transfer into a partition whose nearest member is D <= gap < min(split distance, D + piece speed x `partition.transferBandSeconds`); default `true` since the r116 A/B, `false` = design C), `partition.transferBandSeconds` (default 60), `partition.mergeHoldoffSeconds` (T-0339 merge hold-off: for this many seconds after a split its pieces are not merged back with each other unless their nearest members are under D/2; merges with any other partition, atomic links and touching grids are unaffected; read every membership pass; default 0 = off; counters `heldMerges`/`heldMergePairs` in `GET /cluster/partitions`, log line `topology merge_holdoff`), `partition.oversizeMembers` (#1300: a partition with more members than this splits at a shorter distance than the split distance, 1000 m less at a time down to D + `partition.margin` (the co-location envelope, 4000 m at the defaults; #1305), until every piece holds at most this many members; the first cut that fits wins, and a compact group stays whole; the split's `topology` line says `oversize split`; read every membership pass; default 150, 0 = off), `partition.splitHysteresisSeconds` (T-0339 split hysteresis: a split that would give a piece a NEW id while the piece is closing on a standing partition - nearest gap >= D, falling faster than 1 m/s since the previous pass, reaching D at that rate before the hold runs out - keeps that piece in its partition and transfers it there once it is within reach; the split is made as before once no new piece is closing or N s after the first held pass; other new pieces split off as before; needs `partition.transfer`; read every membership pass; default 0 = off; counters `hysteresis*` in `GET /cluster/partitions`, log line `topology split_hysteresis`), `retention.archiveFileDays` (T-0400, read by the GATEWAY only: files of the saves of partitions archived more than this many days ago are deleted from the per-host stores and, T-0412, by their live holder node from its own data dir, rows kept; default 7, 0 = off; see `Docs/Gateway/StateDurability.md`), `rotation.lifetimeSeconds` and `rotation.drainTimeoutSeconds` (SE-1168, read by the GATEWAY only, managed deployments: one recycle of the oldest serving node onto the hot spare per lifetime / n serving nodes, and the drain timeout after which it is closed; defaults 7200 and 600, lifetime 0 = off; see "Rotation" in `Docs/Gateway/ManagedDeployment.md`), `lookahead.maxSpeed` (read by the GATEWAY only: the highest member speed in m/s that the co-location and offline-wake velocity lookahead counts, a pair's closing speed capped at twice it; default 100, the vanilla top speed; set it to the world's top speed when a mod raises it), `diag.importAddTimers` (T-0401: time the parts of each staged import activation's MyEntities.Add and append `add*` fields to the activation's `handover_phase` line, and on the source the release's close sequence as `rel*` fields on the `release`/`release_close` line; log-only; default `true`; see `Docs/ImportActivateSlices.md`), `diag.gridAddTraceImports` (T-0401: `false` (default) = GridAddTrace skips the staged import activation's adds; `true` = trace them as before), `import.deferPhysics` (T-0401 fix: `true` = a handover import Adds a grid without a seated pilot with its physics and its hierarchy children's bodies (a connector's detector, block subparts) already disabled, so the Add creates/reorders no Havok cluster that the freeze would throw away; the commit's thaw enables it as recorded; with `diag.importAddTimers` the thaw logs `handover_thaw` with `deferred`/`deferredChildren`/`notDeferred`/`notDeferredBy` and the `add*` timers, `deferPhysics` = the transfer's own mode and `knobAtThaw` = the knob when it thawed; default `true` since 2026-10-08, `false` = as before), `diag.importAddClusterTrace` (T-0401 follow-up: one `import_add_cluster_op` log line per Havok cluster create/reorder inside a timed import Add or thaw, naming the added entity and the body whose activation caused it; log-only; default `false`), `drops.maxActive` (#1443, read by the WORLD AUTHORITY: at most this many container drops active cluster-wide, online or in an offline partition, until opened or past their vanilla removal deadline; a drop that falls due at the cap is skipped, not queued; default 3, 0 = no cap; see `ClusterConfig.cs` and `GET /drop-container-status`)); PUT merges a flat `{key: value}` object, rejects unknown keys, returns `{revision, values}`. Normally driven by the Gateway's `/admin/v1/handover-config` (fan-out + registration + heartbeat pull), never the game thread |
| `GET /cluster/handover-stats` | last 256 handover phases with game-thread hold ms and sub-steps, plus per-phase p50/p95/max |
| `GET /cluster/hitches` | game-thread frames over `hitch.thresholdMs` with the handover phase/transfer active at the time (`none` = not a handover) |

## Partition lifecycle & handover

| Endpoint | Purpose |
| --- | --- |
| `GET /cluster/partitions`, `GET /cluster/partition-of?entity=ID` | membership; the partitions report also carries `refusedMerges` (last pass), `heldMerges` (sibling pairs the last pass held off under `partition.mergeHoldoffSeconds`) `heldMergePairs` (sibling pairs held off at least once since the node started) and `deferredSplits` (T-0339: layouts whose members the last pass found in two or more split groups but did not split or transfer - locked, pinned or no fresh id; each logged as `topology split_deferred partition=<id> reason=<hold-closed|hold-registry|unowned|frozen|registry-pending|held-id|pending|no-ids> groups=<n> sinceS=<s>` once per layout and reason, again every 60 s while it lasts; a split held by the hysteresis is NOT a deferral and is never listed), `hysteresisHeld` (layouts the last pass held under `partition.splitHysteresisSeconds`), and since the node started `hysteresisHeldTotal` (holds started), `hysteresisTransferred` (holds that ended with the piece transferred, no new id), `hysteresisExpired` (ended at N s, split made) and `hysteresisReleased` (ended because no new piece was closing any more, or the partition was no longer due); each hold logs `topology split_hysteresis partition=<id> piece=<members> target=<id or -> gapM=<m> closingMps=<m/s> outcome=<held|transferred|expired|not-closing|cleared> heldS=<s>` when it starts and when it ends |
| `POST /cluster/capture`, `/commit`, `/discard`, `/release`, `/remove`, `/thaw` | fenced handover transaction steps; `capture?presync=ID` answers with the delta against baseline ID (kind-3 sector, see `Plan/HandoverDelta.md`), `&full=true` the full capture |
| `GET /cluster/snapshot?partition=P` | the partition's builders without a freeze (kind-1 MPS1 file); keeps the per-entity bytes as a baseline for `presync.ttlSeconds`; `X-Presync-Id` header names it |
| `POST /cluster/presync?partition=P&source=host:port` | target pulls the source's snapshot and stages dormant entities for the delta import; `{presyncId, entities, staged, kept, totalWallMs}`; one stage per partition, replaced by a newer one, dropped after `presync.ttlSeconds` |
| `POST /cluster/adopt`, `POST /cluster/adopt-ready` | target-side adoption |
| `GET /cluster/export`, `POST /cluster/import`, `POST /cluster/prepare-import` | partition state transfer |
| `GET /cluster/tee-status?transferId=` | `tee.async`: whether the capture's durable tee is catalogued yet (`known=false` when the tee was awaited inside capture); the Gateway waits on it before activating |

## Saves

| Endpoint | Purpose |
| --- | --- |
| `POST /cluster/save-capture` | capture a partition save (adaptive pipeline) |
| `GET /cluster/save-file`, `GET /cluster/save-status` | fetch/inspect saves; `save-file` is served on two worker threads and streamed from `offset` (T-0340), the other routes stay on the listener thread |
| `POST /cluster/global-save-capture`, `GET /cluster/global-save-file` | WA global saves |
| `GET|POST /cluster/quarantine`, `POST /cluster/quarantine-repair` | poison-save handling |

## World state

| Endpoint | Purpose |
| --- | --- |
| `GET /cluster/respawn-points`, `/respawn-cooldown`, `POST /cluster/respawn-execute` | respawn registry |
| `POST /cluster/teleport` | server-side teleport |
| `POST /cluster/grid-delete?entityId=&name=` | #1334: remove an unpiloted grid on its owning node, as the trash collector does; `name` must equal the grid's display name. Refused (`deleted=false` with a `reason`) when the grid is not here, closing, not top-level, frozen for a handover, inside its import grace, has a seated pilot, is controlled by an online player or is mechanically attached to other grids. Fleet sweep: `Tools/leftover-ship-sweep.py` |
| `GET|POST /cluster/time-of-day`, `GET /cluster/weather` | environment |
| `GET /cluster/antenna-graph`, `GET /cluster/position-registry` | topology info |
| `GET /cluster/ship-antennas` | what the ship-antenna rule is doing: rule, thresholds, reconcile passes, writes, failures, grids tracked and how many are on; `skippedInHandover` counts guard AND frozen skips, `skippedByGuard` the guard's share |
| `GET /cluster/import-guard` | T-0392 sliced-activation guard diagnostics: `openNow` (open guards), `openIds`, `guardedImports` / `guardedIds` (sliced imports and ids guarded since start), `opened` / `released`, and `skips` per caller (`ShipAntenna`, `CharacterSeed`, `CharacterSettle`, `FencePurge`, `RecoveryStaleCopy`, `Timer`, `SmartUpdater`): how often the guard, not the frozen mark, made it skip. Counters since start; not marshalled to the game thread |
| `GET /cluster/voxel-storages`, `GET|POST /cluster/voxel-journal`, `POST /cluster/voxel-journal/append`, `GET /cluster/voxel-journal-tails`, `GET /cluster/voxel-journal-entries`, `GET /cluster/voxel-journal-status`, `GET /cluster/voxel-base-file`, `POST /cluster/voxel-compact`, `GET /cluster/voxel-range-hash` | voxel journal & bases |
| `GET /cluster/validation-failures` | handover validation aborts |
| `GET /cluster/block-integrity` | per-block integrity probe |
| `POST /cluster/igc-test-antenna?grid=&enabled=`, `POST /cluster/mod-deliver`, `GET /cluster/mod-status` | scripting/modding channels |
| `GET /cluster/mod-locate?entity=ID` | #1280: this node's view of an entity for the mod channel's owner routing, `{entity, residence}` with residence `Local`, `Frozen`, `Unowned` or `Absent`; Docs/ModChannel.md |
| `GET /cluster/mod-compat` | T-0242: this process's per-mod class, placement and load record, and its mod file-write counters (`storageWrites`); Docs/ModCompat.md |

## Offline partitions

`GET /cluster/offline` — T-0193: this node's offline sweep (`owner`: enabled, thresholds, how many it
took offline / was refused / failed, the in-flight transition, and the count of owned partitions per
reason they cannot go offline right now) and, on the World Authority, the cluster table it republishes
(`cluster`: retained antennas and respawn points, load requests). Knobs are the `offline.*` keys of the
handover-config document (ClusterConfig.cs).

SE-346/SE-347 add `owner.compensation` (profiling, modeled results, confidence/skips and
the rate cache) and `callUps` counters. Partition reports carry `remoteUsers`; these pins
block offlining, balancing and conflicting clientless pulls. Spatial co-location can still
end a remote session. The implementation is a review draft with native acceptance pending;
see [OfflinePartitions.md](../OfflinePartitions.md) for defaults, limitations and evidence.

| Endpoint | Purpose |
| --- | --- |
| `GET /cluster/profiles?partition=N` | Captured online profiles, or the exact offline Final's profiles on a holder host; no recovery claim or wake |
| `POST /cluster/trigger/profile?partition=N` | Advance the normal profiling pass with its readiness and policy gates |
| `POST /cluster/trigger/compensate-dry-run?partition=N&hours=H` | Model the exact offline Final on this holder without applying or saving it |
| `POST /cluster/compensate-dry-run?hours=H` | Model an uploaded MPS Final (512 MiB maximum), without loading entities |
| `POST /cluster/profiles/cache/clear?key=K` | Journal a per-key WA cache invalidation; epochs reject stale contributions |

Dry-run hours must be finite and within 0..168 authority hours (225,000 game frames/hour).
These routes use the existing admin-route authorization described above.

## Concealment

`GET|POST /cluster/concealment` — inspect/steer entity concealment (grace and pre-wake are
configured with `CLUSTER_CONCEAL_GRACE_SECONDS` / `CLUSTER_CONCEAL_PREWAKE_METERS`).
