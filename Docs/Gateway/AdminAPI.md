# Versioned admin API (`/admin/v1`)

Client diagnostics adds `GET/PUT /admin/v1/rotation` and
`GET /admin/v1/client-diagnostics?node=ID`. See [Client diagnostics](ClientDiagnostics.md)
for publication leases, incarnation fencing and the game mod-network contract.


The stable operator contract for Quasar, the thin admin CLI, and other automation.
It lives on the same control listener as the legacy routes (`--control`, default
`127.0.0.1:28016`) under the `/admin/v1` prefix. Everything outside this prefix —
`/registry/*`, `/handover`, node `/cluster/*` routes — is an internal implementation
surface and carries **no compatibility guarantee**.

Source of truth: `ClusterRegistry.AdminContract/AdminContract.cs` (DTOs, enums,
envelopes, error codes), `ClusterRegistry/AdminApi.cs` (routing),
`ClusterRegistry/GatewayRegistry.cs` (mapping from registry state).

## Contract rules

- Every response carries `X-Cluster-Gateway-Protocol: 1` and a
  `{ protocolVersion, capturedAt, data }` envelope (camelCase JSON, string enums).
- Errors use `{ protocolVersion, capturedAt, error: { code, message, validation? } }`
  with stable machine-readable `code` values (`route_not_found`, `method_not_allowed`,
  `registry_unavailable`, `bearer_token_required`, ...). Clients never parse message text.
- A non-loopback control bind requires a bearer token; loopback is unauthenticated
  (development/bench) and carries full manage scope. See "Auth scopes" below.
- The contract DTOs are published as the implementation-free NuGet package
  `CometWorks.ClusterGateway.AdminContract`; a contract change bumps the package version
  before merge, and a breaking change bumps the protocol version.

## Operations

| Endpoint | Purpose |
| --- | --- |
| `GET /admin/v1/health` | API process liveness and Registry availability — never cluster health |
| `GET /admin/v1/capabilities` | protocol version, the operation list this build serves, feature flags, and the caller's own scope |
| `GET /admin/v1/status` | one Registry-consistent snapshot: lifecycle phase, startup kind, shutdown markers, nodes, executors, World Authority, catalog counts — plus `acceptingPlayers`, server-computed `health` (`Healthy`/`Warning`/`Unhealthy`), stable `reasonCodes[]` (`bootstrapping`, `draining`, `clusterDown`, `paused`, `nodeDead`, `saveFailure`, `globalSaveStale`, `executorSilence`, `globalSpawnFailure`, `spawnFailureAlert`, `capacityShortfall`, `networkIdSpaceLow`), and `observedAt`, so a control plane never decodes internals to judge severity |
| `GET /admin/v1/plan` | Registry-published NodePlan: slot goals, spawn observations, incumbent actions, failure/retry state |
| `GET /admin/v1/nodes` | fleet view: every registered node incarnation with slot key, host, epoch, role, state, endpoints, simulation frame, and lease freshness |
| `GET /admin/v1/world-authority` | WA incarnation: node, epoch, authority generation, lease |
| `GET /admin/v1/partitions` | read-only partition map (owner node/epoch/generation, lease, recovering, quarantined, retired) for the UI; partition mutations stay internal |
| `GET /admin/v1/snapshots` | snapshot manifests, newest first: kind, cluster time, partition/global/voxel coverage, dirty set |
| `GET /admin/v1/recovery-readiness` | newest reconstructible snapshot, partition/global/voxel coverage, rollback exposure, replica-host floor, missing artifacts, Registry store durability (registry.db checkpoint time, commits, WAL bytes) |
| `GET /admin/v1/events?cursor=N&limit=M` | ordered event/audit tail: phase changes, node lifecycle transitions, alarms, admin audit records. Resume with the returned `nextCursor`; a gap between the cursor and the first event means bounded retention truncated the tail |
| `GET /admin/v1/operations?limit=M` | durable admin operations, newest first |
| `GET`/`PUT /admin/v1/handover-config` | HandoverImprovements.md §2: the durable runtime handover-config document `{revision, values}`. PUT (manage scope, `Idempotency-Key`) merges `{key: value}` (`null` removes the key), bumps the revision on change, fans the document out to every Active node's `/cluster/config` and reports per-node `applied`/`refused`/`unreachable` in the operation detail; nodes that missed the fan-out pull it on the next heartbeat, and a registering node receives it in its registration response. Keys are validated by the nodes |
| `GET /admin/v1/operations/{id}` | one durable operation: kind, state, actor, idempotency key, result or error |
| `GET /admin/v1/config` | effective configuration: immutable deployment identity (cluster/world IDs, config/binary/type-table/world-settings hashes, sync distance, lease TTL) plus the revisioned mutable policy (node slots) |
| `PUT /admin/v1/config` | compare-and-set policy update (`AdminConfigUpdate`): upserts node slots, bumps `policyRevision`; a stale `expectedRevision` fails with `revision_mismatch` |
| `POST /admin/v1/shutdown` | graceful (`graceSeconds`) or `emergency` drain to `Down` as a durable operation. The API process stays up in `Down` (model C); final OS teardown belongs to the packaged CLI / host executor. A node that dies in its drain is not waited for: the shutdown completes `Down` with `node:<id>` and its missing saves in the marker's dirty list. If the drain's deadline (grace + 30 s) fails the operation while a node is still Closed, the Gateway completes the shutdown the same way as soon as no node is Active or Closed (#1301) |
| `POST /admin/v1/gateway/restart` | client-only event (nodes keep simulating): completes the operation, checkpoints the registry, exits the process — the supervisor (systemd, CLI, SSH) starts it again |
| `POST /admin/v1/save-all` | triggers saves on all nodes (`sweep` optional) and registers an online snapshot; the result carries the snapshot id and an honest consistency label (`CutConsistent` only with no dirty entries, otherwise `CrashConsistent`) |
| `POST /admin/v1/nodes/{slot}/close` | graceful drain of the node a slot observes, with client handover and `forceAfterSeconds` deadline |
| `POST /admin/v1/nodes/{slot}/kill` | provenance-bound force removal: the body must name the exact `node`+`epoch` the slot observes; the still-wanted slot respawns only after the executor reports the incumbent gone |
| `POST /admin/v1/world-authority/move` | drains the current WA incarnation; the successor spawns via the NodePlan and takes a bumped authority generation |
| `GET /admin/v1/clients` | stable player/session view: identity, admission state, admin flag, and the authoritative registry route (node/partition) when known |
| `GET /admin/v1/players/moderation` | authoritative promotion entries `{userId, level, revision}` and active admission kick deadlines `{userId, expiresAt}`, including offline players |
| `PUT /admin/v1/players/{userId}/promotion` | set `{level, expectedRevision}` through the current fenced World Authority; manage scope and `Idempotency-Key` required |
| `DELETE /admin/v1/admission/kicks/{userId}` | clear this player's Gateway admission kick deadline; manage scope and `Idempotency-Key` required; bans are unchanged |
| `POST /admin/v1/clients/{id}/kick` | punitive kick (arms the anti-flap cooldown) or, with `{"reconnect":true}`, a rescue reconnect that does not lock the player out |
| `GET /admin/v1/admission/bans` | the effective admin ban set |
| `POST /admin/v1/admission/bans` | ban `{userId}`; persisted to `admission-bans.json` under the registry dir (atomic rewrite, applied over the admission config at start), and the connected session is disconnected |
| `DELETE /admin/v1/admission/bans/{userId}` | unban, persisted the same way |
| `POST /admin/v1/chat` | cluster-global chat send `{message, sender}` routed through the World Authority (the single accept point); `sender` must be a real identity |
| `GET /admin/v1/chat/history?cursor=N&limit=M` | accepted global chat captured at the WA, with `worldAuthorityEpoch` so a resuming reader knows when its cursor became stale (the ring restarts with the WA incarnation) |
| `GET /admin/v1/ship-storage/takes` | the ship storage takes the World Authority holds (#1316): ship `id`, `owner`, `name`, `position` (terminal plus the ship's offset, near where vanilla pastes it), `takenBy` node, `take` id, `taken` (UTC; restarts with a WA restore), `fetched` and `stranded` (blueprint fetched and held 10 min: the WA log says `ship storage take stranded id=...`), with `worldAuthorityEpoch` |
| `POST /admin/v1/ship-storage/takes/{id}/settle` | settle a stranded take `{take, action}` (#1316). Look for the grid first (named `name`, owned by `owner`, near `position`): `release` when it is not in the world, so the ship is listed again; `consume` when it is, so the ship is dropped. The WA refuses (`take_not_settled`) a take that is not the one held or not stranded yet; a Consume or Release its node sends later changes nothing |
| `POST /admin/v1/world-exports` | full-world export as a durable operation: shells out to `MagnetarWorld to-vanilla`, checksums the output, atomically promotes it under the export root, and publishes an artifact descriptor. Requires `--world`, `--export-root`, `--export-data-roots`, and `MAGNETAR_WORLD_TOOL` (503 `exports_unconfigured` otherwise). Consistency is honest: `Quiescent` only when the cluster is Down behind a clean-shutdown snapshot with nothing dirty, otherwise `CrashConsistent` with an `onlineExportNotSnapshotPinned` warning |
| `POST /admin/v1/registry-backups` | consistent copy of the registry database for an online cluster backup, as a durable operation: pending registry mutations are written, then SQLite `VACUUM INTO` copies one read transaction from its own read-only connection while the registry keeps writing. The copy (`registry.db`) is published as a `ClusterRegistrySqlite` artifact under the export root, with the newest snapshot id. Requires only `--export-root` (503 `exports_unconfigured` otherwise); `ttlHours` defaults to 6. A backup that copies node data after this operation completes gets the state of a crash at the copy time: saves are write-once and catalogued only once complete, so every save the copy names is a whole file, and newer files are uncatalogued and ignored by recovery. Retention keeps every save the copy names on disk until the artifact is released (`DELETE /admin/v1/artifacts/{id}`) or expires, so the data copy finds them however many newer saves arrive meanwhile (#1275); the hold is in memory, a gateway restart ends it |
| `GET /admin/v1/artifacts` / `GET /admin/v1/artifacts/{id}` | artifact descriptors: format, snapshot, consistency, created/expires, size, per-file SHA-256 + manifest hash, and `retrieval` (`SharedPath` with a scope-validated absolute path in v1; the discriminator already carries `HttpDownload`/`StorageUri` for later) |
| `DELETE /admin/v1/artifacts/{id}` | explicit release (deletes the body, only ever inside the export root, and ends a registry backup's retention hold); TTL expiry cleans up as the fallback |
| `GET /admin/v1/node-control` | node control (T-0320, NodeControl.md): per-host serving/spare targets (runtime, hosts file, actual), every live node in its operator state, what is not counted; see "Node control" below |
| `GET /admin/v1/node-control/history?limit&cursor&host` | stopped nodes, newest first: who, when, why, age, host, forced, replacement |
| `PUT /admin/v1/node-control/hosts/{host}` | runtime serving/spare targets of one host (until the next start) |
| `POST /admin/v1/node-control/remove` | drain and stop the selected node; its host's target drops by one |
| `POST /admin/v1/node-control/recycle` | drain and stop the selected node; a spare (or a new node) replaces it, the count is unchanged |
| `POST /admin/v1/node-control/hosts-file` | the launcher's `start` applies the hosts file: every live node to the history, fresh ids for the whole plan |
| `GET /admin/v1/diag?depth=...` | the Orchestration §11.1 structured snapshot behind the versioned envelope and scope check; filters (`node`, `partition`, `player`, `entity`, `limit`, `cursor`) unchanged |
| `POST /admin/v1/triggers/{task}` | run a maintenance task through the same code path as the scheduler; stable vocabulary `balance`, `save`, `split-sweep`, `membership-eval`, `conceal-eval`, `compact`, `catalog-gc`, `rotation-check`; scoping via `node`/`partition`/`unthrottled` query params joins the idempotency fingerprint |

Every mutating route requires an `Idempotency-Key` header and answers with a durable
operation record: `202` while the effect is still running, `200` with the recorded
outcome once terminal. Retrying with the same key — including after a lost response or a
Gateway restart — replays the record instead of repeating the effect; reusing a key for a
different request is refused with `409 idempotency_key_reuse`.

## Player moderation

Contract package `0.11.0` adds the three routes above and feature `playerModeration: v1`
without changing HTTP wire v1. Check operation capabilities before enabling an action.
Roles are exactly `None`, `Scripter`, `Moderator`, `SpaceMaster` and `Admin`; `Owner` is
not assignable. User IDs must be nonzero. An absent user in a non-null promotion map
has role `None`, revision 0. A null `promotions` means the WA is unavailable or too old;
`promotionError` explains why, while Gateway cooldowns remain available.

The role request includes the revision observed for that player. A stale request completes
as a failed operation with `revision_mismatch`; refresh before a new decision. The Gateway
sends the operation GUID through its existing WA command lane, fenced by WA generation
and correlated by subsystem, sequence and operation GUID. Only Gateway-originated admin
commands are accepted. No node selected from the player's current placement owns the role.
Success acknowledges the WA's game-thread update and queued cluster replication; a
disconnected replica catches up through the existing snapshot resync.

The WA promotion map, revisions and explicit `None` demotions are captured with the command
ledger in an appended optional global-save section. Earlier saves still load and seed from
their checkpoint. Startup seeds cannot overwrite known roles. Nodes reapply the current
map when player loading restores stale checkpoint data during reconnect or movement.
Global saves and role changes wait for initial promotion seeding; an early role command
fails with `promotion_not_ready` without bypassing the checkpoint-loading grace period.
Refresh before submitting a new action. Journaled changes after a recoverable
save replay on its replacement WA, under the existing journal policy (including its
bounded fail-open behavior) and [state durability](StateDurability.md) rollback limits.
Use matching Gateway and node builds; older nodes do not implement the new WA commands.

A lost or invalid WA acknowledgement completes the Gateway operation with
`promotion_uncertain`, never success. Retry the **same key and identical request** to
reconcile that WA ledger GUID. The revision check prevents an undelivered old request
from overwriting a newer decision. Quasar retains this outcome as in progress and
reconciles the original request after reconnect or restart.

Punitive kicks still impose five minutes in `AdmissionBook`. Clearing a cooldown removes
only that user's deadline; it does not unban, change reserved slots or affect other users.
A replay of a completed clear does not remove a subsequent kick. These deadlines remain
in memory and disappear on Gateway restart, matching existing behavior. An interrupted
clear is failed as `clear_interrupted`; refresh before sending a new clear. Roles persist
through clean cluster restart, WA replacement, player reconnect and node movement.

CLI: `player-moderation`, `player-promotion USERID LEVEL EXPECTED_REVISION`, and
`clear-kick-cooldown USERID`. Mutations accept `--key` as usual.

## Node control

Feature flag: `GET /admin/v1/capabilities` → `data.features.nodeControl == "v1"`. The operator model is
[NodeControl.md](NodeControl.md); DTOs are in `AdminContract.cs` (contract package 0.5.0, protocol version
stays 1; `NodeStatus`, `NodePlan` and `ClusterStatus` are unchanged).

```text
NodeControlView { revision, differsFromFile, hostsFile: {sha256?, appliedAt?}, hosts: HostView[],
                  nodes: OperatorNode[], notCounted: NotCountedNode[], active, managed }
HostView        { host, serving: TargetCount, spares: TargetCount, ramFloorMb?, diskFloorMb?, warnings[] }
TargetCount     { target, file? (null = host not in the file), actual (nodes Serving / Spare now) }
OperatorNode    { node, host, position, pid?, startedAt?, servingSince?, ageSeconds?,
                  state: Starting|Serving|Spare|Draining|Stopping|Failed, partitions, clients,
                  stop?: {kind: remove|recycle|decrease|failed, actor, why, deadline, forced, requestedBy?,
                          requestedAt?, forcedAt?, now, detail?}, replaces?, replacedBy? }
NotCountedNode  { node, role: WorldAuthority|Gateway, host?, pid?, startedAt?, ageSeconds? }

HostTargetsUpdate { serving?, spares?, reason?, requestedBy? }          // null keeps the current value
NodeStopRequest   { selector, now = false, timeoutSeconds?, reason?, requestedBy? }
HostsFileApply    { hosts: {"<host>": {serving, spares, ramFloorMb?, diskFloorMb?}}, sha256?, resume }  // resume is ignored

NodeHistoryPage   { records: NodeHistoryEntry[], nextCursor? }
NodeHistoryEntry  { seq, node, host, pid?, startedAt, endedAt, ageSeconds, outcome, actor, requestedBy?, why,
                    forced, replacedBy?, partitionsAtStop }
```

- `selector`: `node-9`, `12345@polaris` (PID@host) or `polaris#2` (host#position: k-th live node on the host by
  process start, oldest = 1, Draining/Stopping/Failed included). Resolved on the server; the operation's
  `target` is always the resolved id.
- `timeoutSeconds` omitted = the registry default (`CLUSTER_DRAIN_TIMEOUT_SECONDS`, 300). `now: true` forces at
  once; on a node already draining it escalates to force.
- `stop.deadline` is the effective force time (it moves out while partitions recover); `stop.detail` explains a
  waiting stop (`"drained; waiting for the launcher to stop the process"`, `"forced; still owns 3 partition(s)..."`).
- `servingSince` is set for Serving and Draining nodes (registration, or spare activation).
- History `outcome`: `remove`, `recycle`, `decrease`, `failed`, `restart` (the start applied the hosts file;
  actor `start`), `closed` (the registry closed the node itself). `actor` is the admin actor (token name or
  `loopback`), `registry` for failures; `requestedBy` the caller's free text (the CLI sends `$USER@host`, the
  rotator `rotator`). At most 2000 records; page with `?cursor=<nextCursor>` (a `seq`), limit 1..2000, default 50.
- Mutations need an `Idempotency-Key` and finish at once (HTTP 200, `state: Succeeded`). Kinds:
  `node-control-set`, `node-control-remove`, `node-control-recycle`, `node-control-hosts-file`; `target` is
  the host, the resolved node id, or the sha256. `result` (string map): `revision`, `node` (remove/recycle),
  `replacement` (when one was chosen), and `actions` / `warnings` as JSON-encoded string arrays, e.g.
  `{"revision":"4","node":"node-2","replacement":"node-4","actions":"[\"draining node-2 @polaris (recycle)\",\"activated spare node-4 @polaris\",\"planned spare node-5 @polaris\"]","warnings":"[]"}`.
  Convergence is watched in `GET /admin/v1/node-control`.
- Refusals are answered before anything is recorded: 400 `invalid_arguments` (bad body, a count outside 0..16,
  a set with neither count, an empty selector, `host#0`, a hosts file with no serving node), 404
  `selector_not_found`, 409 `selector_ambiguous`, 404 `host_not_found` (a PUT for a host not in the hosts file),
  409 `last_serving_node` (0 serving targets cluster-wide), 409 `already_stopping` (a node already stopping or
  failed; `now: true` escalates instead), 409 `node_control_inactive` (remove/recycle before the hosts file or
  any target), 409 `managed_inventory_fixed` (any mutation on a managed deployment; list and history work
  there, read-only). A retry with a key that already names an operation replays it.
- Events: `node.planned`, `node.activated`, `node.spare`, `node.stop-requested`, `node.closing`, `node.forced`,
  `node.gone`, `node.failed`, `node-control.targets`, `node-control.hosts-file`.
- The node plan (`GET /admin/v1/plan`) carries the decisions to the launcher: one slot per planned node
  (`slotKey` = node id, `host`), goal `Wanted` → spawn, a stop sets goal `Draining` with
  `incumbentAction: Draining` (leave it running), then `Kill` once the node is empty (stop it, report `Gone`);
  a gone node's slot is dropped. Populating it in unmanaged mode turns on `executorSilence` and the spawn-failure
  alerts: a launcher must keep heartbeating.
- Legacy: `POST /registry/scale?nodes=N` maps to a set of the sole host's serving target (409 `multiple_hosts`
  with more than one host; `force`/`retire` ignored); `GET /registry/scale` keeps its fields and adds `warning`
  and `hosts`. Both are deprecated.

Out of this contract by design: down-state lifecycle (start, import/restore, registry
rebuild) stays on the packaged cluster CLI — the REST contract promises every operation
**while the Gateway API is reachable**. Promote/demote waits for the WA global-state path
(v1.1); partition mutations are internal; per-node plugin inventory/config/stats/logs are
deliberately carried by optional cluster-mode Quasar Agents, not this API.

## Auth scopes

Beyond loopback, two credential kinds open `/admin/v1`:

- `CLUSTER_ADMIN_TOKEN` — the legacy single token, mapped to the `manage` scope with
  audit actor `admin-token`.
- `CLUSTER_ADMIN_TOKENS` — path to a JSON file of named, scoped tokens:

  ```json
  {
    "tokens": [
      { "name": "quasar", "scope": "manage", "sha256": "<hex of SHA-256 of the token>",
        "previousSha256": "<optional rotation slot>" },
      { "name": "monitor", "scope": "query", "sha256": "<hex>" }
    ]
  }
  ```

  Only hashes are at rest. The file is re-read whenever its size or mtime changes, so
  editing it rotates or revokes on the next request without a Gateway restart; a
  malformed edit keeps the last good set. `query` tokens may call every GET; a mutation
  attempt is refused with the stable `403 authorization_denied` envelope and an
  `admin.denied` audit event. `manage` tokens may call everything. The token name is the
  audit actor on every mutation event.

Legacy routes outside `/admin/v1` keep accepting only `CLUSTER_ADMIN_TOKEN`.

## Events

The Gateway appends an ordered event record for every phase change, node lifecycle
transition (`node.registered`, `node.closing`, `node.empty`, `node.dead`), alarm
(`alarm.handover-blocked`), and admin audit entry. Recent events are held in memory and,
when `--registry` is configured, persisted to `events.jsonl` under the registry directory
(size-bounded, rolled once). Appends update the in-memory ring and a bounded background
queue without doing disk I/O under orchestration locks. The writer batches up to 128
records, with a 100 ms collection window for small batches; recent-tail reads use only
the ring. Slow storage or queue overload can truncate the persisted audit history.
Graceful shutdown drains admitted records for up to five seconds and, when storage is
healthy, writes an `events.gap` high-water marker for dropped final records so sequence
numbers are not reused after restart. An unclean stop can lose unflushed events.
This is an observability tail, not a durable mutation acknowledgement. `GET
/admin/v1/events` serves the tail by cursor; `earliestRetained` tells a resuming reader
whether it missed truncated history.

## Thin admin CLI

`ClusterRegistry.Cli` is a thin HTTP client over exactly these routes — one CLI
spelling per HTTP operation, no hidden local work:

```bash
CLUSTER_GATEWAY_URL=http://127.0.0.1:29416 \
  dotnet run --project ClusterRegistry.Cli -- status
```

Reads: `health`, `capabilities`, `status`, `plan`, `recovery-readiness`, `nodes`,
`world-authority`, `partitions`, `snapshots`, `config`, `clients`, `bans`,
`chat-history [--cursor N] [--limit M]`, `ship-storage-takes`, `events [--cursor N] [--limit M]`,
`operations [--limit M]`, `operation ID`,
`diag [--depth D] [--node N] [--partition P] [--player S] [--entity E] [--limit M] [--cursor C]`.
Mutations (each takes `--key IDEMPOTENCY_KEY`, generated when omitted):
`shutdown [--grace N] [--force-after N] [--emergency]`, `gateway-restart`,
`save-all [--sweep]`, `config-set FILE|-`, `node-close SLOT [--force-after N]`,
`node-kill SLOT NODE EPOCH`, `wa-move [--force-after N]`,
`trigger TASK [--node N] [--partition P] [--unthrottled]`,
`kick CLIENTID [--reconnect]`, `ban USERID`, `unban USERID`,
`chat MESSAGE --sender STEAMID`, `ship-storage-settle SHIPID TAKEID release|consume`,
`world-export [--ttl-hours H]`, `registry-backup [--ttl-hours H]`, `artifact-release ID`,
`node-control-set HOST [--serving N] [--spares S] [--reason R] [--requested-by W]`,
`node-control-remove SELECTOR [--now] [--drain-timeout S] [--reason R] [--requested-by W]`,
`node-control-recycle SELECTOR [same options]`, `node-control-hosts-file FILE|-`
(reads also include `artifacts`, `artifact ID`, `node-control` and
`node-control-history [--limit M] [--cursor N] [--host H]`).
Gateway JSON is printed
unchanged on stdout; diagnostics go to stderr. `--url` overrides the env var,
`--timeout SECONDS` sets the deadline, `CLUSTER_ADMIN_TOKEN` is sent as the bearer
when set. Exit codes are deterministic: `0` success, `2` usage, `3` authorization,
`4` timeout/unavailable, `5` Gateway rejection, `6` invalid or incompatible protocol.

## Tests

- `ClusterRegistry --self-test` sections: `admin-contract` (DTO mapping and wire shape),
  `admin-auth` (scoped token file, rotation, revocation), `admin-events` (event log
  cursor, persistence, roll), `admin-operations` (idempotent durable operations),
  `admin-fleet` (status severity and fleet reads), `admin-lifecycle` (config CAS,
  provenance-bound kill), `admin-admission` (durable bans), `admin-artifacts` (checksum
  manifest, artifact store, TTL, scope-guarded delete), `lifecycle-request` (node-link
  protocol), and the node-control sections `node-control-ids`, `-states`, `-policy`,
  `-remove-recycle`, `-force`, `-history`, `-hosts-file`, `-selectors` (with the
  node-control routes and the managed read-only case in `admin-contract` and `admin-operations`).
- `ClusterRegistry.Cli --self-test` asserts the parser, the exit-code table, and the
  **static parity sweep**: the CLI command registry maps 1:1 onto
  `AdminProtocol.Operations` and every spelling parses to its operation's method+route,
  so a new route cannot land without a CLI spelling.
- `Test/scenarios_admin.py` (P3-ADM-01..03) is the **dynamic parity sweep** (P4-QSR-01/09 shape) against a
  real Gateway process: all reads with envelope/header, 401/403/200 across no-token /
  query-token / manage-token on a non-loopback bind, missing-key 400, idempotent replay
  and `idempotency_key_reuse` 409, config CAS, SIGKILL + warm restart replaying the same
  operation and resuming the event cursor without duplicates, and CLI-vs-HTTP data
  equality.

Managed Host execution uses the separate authenticated [executor contract](ManagedDeployment.md). An unmanaged
multi-host start (T-0321) uses the same contract: cluster-hostd on each host polls `POST /executor/v1/heartbeat`
with an Executor-scope token named after its host, minted per start into `CLUSTER_ADMIN_TOKENS`
(NodeControl.md "cluster-hostd").
