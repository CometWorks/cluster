# Gateway control & registry HTTP API

The Gateway binds its control plane at `--control` (default `127.0.0.1:28016`; the CLI uses
`127.0.0.1:29416`). All endpoints speak JSON. When the control plane is bound beyond
loopback, requests need `Authorization: Bearer $CLUSTER_ADMIN_TOKEN`.

Source of truth: `ClusterRegistry/DirectGateway.cs` (routing) and
`ClusterRegistry/GatewayRegistry.cs` (registry semantics).

These routes are the Gateway's internal operational surface. The stable, versioned
operator contract for Quasar and automation is `/admin/v1` — see `Docs/AdminAPI.md`.

## Operations

| Endpoint | Purpose |
| --- | --- |
| `GET /diag?depth=cluster|nodes|partitions|entities` | structured cluster snapshot; filters `node`, `partition`, `player`, `entity`, paging `limit`/`cursor`; `registry=summary` trims the registry section for pollers: `partitions` keeps only rows that are live, or retired but still Recovering or blocked, with `partitionCounts {total, retired, owned, offline, unowned}` beside it, and `entityIdRanges` is replaced by `entityIdRangeSummary {issued, firstIndex, lastIndex, byNode, current}` (current = ranges of the registered node incarnations). w32: 34.7 MB -> see T-0227. Every registry row is listed; a stopped node's row (Empty or Dead) is `stale` with `section: null` and is never queried, since its port slot may serve another node by now (T-0320) |
| `POST /handover?partition=P&target=NODE` | move a partition to a node |
| `GET /handover/paused` / `POST /handover/release` | inspect / release a paused handover (test hook) |
| `POST /switch` | switch a client's route to its partition's current owner |
| `POST /close-node?node=N` | drain and close a node through the registry |
| `POST /pause` / `POST /resume` | pause/resume relaying |
| `POST /automatic/pause` / `POST /automatic/resume` | suspend/resume automatic handover policy |
| `GET /transport/status` | frontends, connected clients, backlog counters |
| `POST /trigger/<task>?wait=1` | run `balance`, `save`, `catalog-gc`, `rotation-check` (saves fan out through `/trigger/save`) |
| `POST /admission/ban` `/admission/kick` `/admission/test-ticket-verdict` | admission book operations |
| `POST /debug/reject-node` | test hook: definitively reject a node's registration |
| `POST /registry/shutdown/graceful` / `POST /registry/shutdown/emergency` | registry-driven cluster shutdown |

## Registry plane (`/registry/...`)

State: `GET /registry/status` (lifecycle phase, nodes, node plan, executors).

- **Nodes**: `GET|POST /registry/nodes`, `POST /registry/nodes/heartbeat`,
  `POST /registry/nodes/close`, `POST /registry/nodes/empty`, `GET|POST /registry/node-plan`,
  `POST /registry/executors/heartbeat`
- **Online node scaling** (T-0312, NodeScaling.md; DEPRECATED by node control, T-0320 NodeControl.md and the
  `/admin/v1/node-control` routes): `GET /registry/scale` (target = sum of the serving targets, phase
  `unset|scaling-out|scaling-in|settling|settled`, serving / starting / retiring / retired / spare nodes,
  blocked = recovery only, balance excess, capacity `warning`, per-host `hosts`), `POST /registry/scale?nodes=N`
  (sets the sole host's serving target; 409 `multiple_hosts` with more than one host; `force` and `retire` are
  ignored; operator route, admin token when one is set)
- **Partitions & leases**: `GET|POST /registry/partitions`, `POST /registry/partitions/release`,
  `POST /registry/partition-ids`, `POST /registry/network-id-blocks`, `GET|POST /registry/entity-id-ranges` (see EntityIdRanges.md), `POST /registry/leases/expire`
- **Offline partitions** (T-0193, clustering-plan Partitioning.md §3.6): `POST /registry/partitions/offline`
  (the owner's offer, JSON `OfflineRequest`: partition, node, epoch, generation, the Final's sequence,
  sphere, member ids and spheres, antennas and respawn points captured at the freeze; 409 unless owned,
  the Final is the newest catalogued save with a usable holder, no handover or migration pin, no
  connected client routed to it, and no other partition close enough to be demand for it; idempotent;
  flushed to disk before it answers), `GET /registry/partitions/offline[?since=<revision>|?partition=<id>]`
  (the proxy table; `Unchanged` when nothing moved since the revision; `FastClients` on every answer, the
  clients whose partition the latest reports call fast, which the World Authority's wake check skips), `POST
  /registry/partitions/offline/load?partition=&node=&reason=` (demand: aims a directed load at `node`,
  or at the least-loaded serving node). `/registry/partitions` rows carry `Offline`; `/registry/status`
  carries `offlinePartitions`.
- **Offline grids** (SE-810): `GET /registry/partitions/offline/grids[?owner=<identityId>]` answers
  `{unknown, grids: [{partition, offlineAt, grid: {id, n, l, s, b, x, y, z, o, m, g, f, p, t}}]}`: every grid of the offline
  partitions, or those with that big owner, as it was at the freeze (name, large, static, blocks, position, big owners,
  mass of its blocks in kg without cargo or tank fill, physical group = its smallest entity id, a functional terminal block, a working power source, a registered
  station), without loading anything. A row read back from a Final has no m, g, f, p or t: unknown, not false. `OfflineRequest.Grids` carries them in; they stay out of the proxy table. `unknown` counts the rows
  whose grids aren't known yet: they went offline from a node that predates the field. The nodes fill those in from
  the Final, one row per node every `offline.gridBackfillSeconds`, with `GET .../grids/missing?after=&limit=` and `POST .../grids`.
  A row a load has claimed is listed until it drops, so prefer what the nodes report for the same id.
- **NPC census** (#1354): `GET /registry/npc-census` answers `{ageSeconds, online, offline, grids: [{id, x, y, z, u}]}`:
  the NPC grids (a mechanical-hierarchy root grid whose big owners are all NPC identities) of the last complete
  report pass (each partition row's `npc`) plus those an offline partition's row kept from its offer (`OfflineRequest.Npc`),
  one row per entity id (online wins; `u` = seconds online with no player within `npc.visitRadius`, the smaller of
  two reports). `ageSeconds` = since that pass. 503 before the first complete pass. Every node polls it every 5 s
  and publishes `Count(x, y, z, radius)` in-process for the mod-mes plugin (`Shared/Cluster/NpcCensusService.cs`).
  #1379: a row also has `k`, the node's cleanup kind (`keep` = a faction of `npc.sweepKeepFactions` or a trade station,
  `npc`, `unowned` = no big owner: drops, debris; absent from an older node), and `o: true` when it comes from an
  offline partition. `grids` holds the `unowned` rows too, so the cleanup rule's count survives a handover and an
  offline cycle; `online`, `offline` and `Count` count only the NPC rows.
  SE-1316: `backupAt`, the unix second the newest registry backup finished (its artifact released by
  `DELETE /admin/v1/artifacts/{id}?backup=stored`, as Quasar does once it has stored and verified the backup), absent
  until the first one since the Gateway started. A release without `backup=stored`, from a failed or interrupted
  backup, ends the retention hold but leaves `backupAt` alone (SE-1351). The nodes' bot ship cleanup (`cleanup.botShips`) runs once for each new value.
- **Partition pass now** (SE1-0084): `POST /registry/report-now`, sent by a node when a member entered or left
  the fast state, at most once per 250 ms. The automatic loop's next pass (it pulls every node's
  `/cluster/partitions`) starts at once instead of after its one second wait, at most every 200 ms. Counted as
  `fastSuppressed.passRequests` in `/diag?registry=summary`.
- **Handovers**: `GET /registry/handovers`, `POST /registry/handovers/begin`,
  `GET /registry/handovers/history` (gateway phase stamps plus node-reported game-thread
  hitches per transfer, see `cluster-regression/bin/handover-hitch-report`), `GET /registry/handover-blocked`,
  `POST /registry/handover-blocked/clear`
- **Handover config**: `GET /registry/handover-config` — the node-pull side of
  `/admin/v1/handover-config`; nodes fetch it when the heartbeat response's
  `HandoverConfigRevision` moves
- **Grants & fencing**: `GET|POST /registry/grants`, `POST /registry/grants/validate`
- **Saves & lineage**: `GET|POST /registry/saves`, `/registry/saves/newest`,
  `/registry/saves/holders`, `/registry/saves/cover`, `/registry/saves/resolve`,
  `/registry/saves/corruption`, `GET|POST /registry/global-saves`(+`/newest`, `/holders`
  (`?withdraw=1`, T-0399: a holder takes back a copy its scrub found unreadable), `/recovery`), `GET|POST /registry/global-journal`
  (#1318: the World Authority appends each banking, faction, identity, player-binding and time-of-day command before it
  answers it, fenced like a global save; a replacement reads `?after=<restored ledger order>` and replays them; memory
  only, 16384 entries), `GET|POST /registry/snapshots`(+`/online`), `GET /registry/lineage`,
  `GET|POST /registry/retention`, `GET /registry/deletions`, `POST /registry/deletions/ack`,
  `POST /registry/checkpoint`, `POST /registry/rebuild`, `GET|POST /registry/quarantine`,
  `GET|POST /registry/replicas`, `POST /registry/recoveries/claim`
- **Clients & world state**: `GET /registry/topology`, `GET /registry/client-routes`,
  `GET|POST /registry/character-homes`, `GET|POST /registry/pull-demands`,
  `POST /registry/pod-placement`, `POST /registry/respawns/switch`,
  `GET|POST /registry/teleport-intents`, `GET|POST /registry/voxel-bases`,
  `GET|POST /registry/voxel-journals`, `GET|POST /registry/voxel-replicas`,
  `GET /registry/world-authority`
- **Steam ownership** (SE-1158): `POST /registry/steam/skins` takes `{userId, data}`, the inventory blob a client sends its
  node (base64), and answers `{skins}`, the armor and block skins Steam says are in it. Only for a player the Steam
  frontend admitted; 503 without a Steam frontend, 422 when Steam refuses the blob.

## Self test

`ClusterRegistry --self-test [section]` runs the built-in self-test (sections include
`endpoints`, `admission`, `leases`, `node-plan`, `cluster-shutdown`, ...). Exit code 0 on
pass; a scenario can cite exactly the section it owns.

## Frontends

- `--direct HOST:PORT` — Direct Transport frontend (UDP). Refused on a publicly reachable
  bind next to the Steam frontend unless the network is listed in
  `CLUSTER_INTERNAL_NETWORKS`.
- `--steam HOST:PORT` — Steam transport (default deployment: `0.0.0.0:27016`), publishes
  the server row using `--admission` config (server name, member limit, admins, tags).
  It is also the only process with the player's Steam auth session, so it answers what a vanilla DS asks its
  own game server (SE-1158): which DLCs the player has a license for, checked at the ticket verdict, and which
  skins their inventory blob holds (`/registry/steam/skins`). Both go to the nodes in the attach binding
  (`PlayerReady.MissingDlcs`, `PlayerReady.Skins`); the DLCs also travel in the online list. A node's Direct
  Transport game server would say yes to every DLC and no to every skin. With the external relay, gateway-rs checks
  the DLCs and sends them with the `ticket` SteamEvent (`missingDlcs`); it doesn't check skins yet (503).
- `--eos` — EOS P2P frontend.

See `Docs/EnvVars.md` for every `--flag` ⇔ `CLUSTER_*` equivalence.

### Character home reports

`POST /registry/character-homes` requires `client`, `identity`, `entity`, `partition`, `node`,
`epoch`, `generation`, `expectedVersion` and `expectedWriteRevision`. The Registry checks the node incarnation, exact
partition ownership and an unexpired lease before accepting even an unchanged report. A changed
home also compares `expectedVersion` with the stored home version under the same lock; a stale
writer gets HTTP 409. No unfenced writer is supported.

A conflict response includes the current `version` and `writeRevision` (zero when no home exists). A node bootstraps
with zero and caches successful/conflict responses before capturing a fresh report on a later
game update. It never retries the old captured report or adds a blocking lookup to the game thread.
Ordinary GET and persisted offline homes continue to support rejoin and legacy lineage lookup.

### Offline character reap authorization

`POST /registry/character-reaps` takes `client`, `identity`, candidate `entity`, executor `node`,
`epoch`, `partition`, `generation`, observed `homeVersion` and `writeRevision`. Under the Registry
lock it verifies the live executor claim and that the candidate is still a different body of the
same identity. It returns one durable permit per client. The permit includes an operation ID,
executor claim and issuance revision; while unresolved, no home report can select the candidate.
Grant and resolution advance **writeRevision**, without changing **home Version** or recovery stamps.

The node consumes a permit once, then rechecks holder, generation, epoch, identity, client and
control/offline/freeze state on the game thread. Only after execution/refusal finishes does it
asynchronously `POST /registry/character-reaps/resolve?client=...` with the exact permit JSON.
Resolution is flushed before its reply and rejects old executor acknowledgments after takeover.
Pending permissions never expire by time. `GET /registry/character-reaps?node=...&epoch=...` lets
an original executor or the candidate's current owner resume them after a restart or topology
change; a new executor must acquire a fenced takeover before acting.

The local queue retains a consumed-revision watermark until every response from its single lookup
worker has been drained. This rejects delayed grant/list replies even after acknowledgment clears
the active item, without retaining permanent per-player operation history.
