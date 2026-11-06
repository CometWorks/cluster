# Node control (T-0320)

How an operator sizes and changes the set of partition-hosting nodes. The registry holds the whole model and
makes every decision; the CLI (`cli/cluster nodes ...`, ClusterCLI.md), the admin API
(`/admin/v1/node-control*`, AdminAPI.md) and the Quasar UI are front ends to it, and a launcher on each host
carries the decisions out. The drain itself (how a stopping node hands its partitions over) is described in
NodeScaling.md.

## Counts per host

- For every host the operator sets two numbers: **serving** (nodes that may host partitions) and **spares**
  (warm nodes that host nothing and take over at once). The cluster total is the sum over hosts.
- The World Authority (`wa-1`) and the Gateway + registry are not counted. `list` shows them under
  "not counted". Each runs on the host the hosts file names, fixed for the run.
- **The hosts file applies at startup only.** Every `start` and `fresh` applies it. A runtime change (CLI, API,
  UI) lasts until the next start, and `nodes list` marks a host whose runtime targets differ from the file
  (`* runtime differs from hosts file`; `differsFromFile` in the API).
- A Gateway-only restart (`/admin/v1/gateway/restart`) keeps the runtime targets; it does not apply the file.

Hosts file (`--hosts`, `CLUSTER_HOSTS_FILE`, else `<root>/hosts.json`, else one built from `--nodes/--spares`
for this host):

```json
{"version": 1, "gateway": "polaris", "worldAuthority": "polaris",
 "hosts": {"polaris": {"serving": 4, "spares": 1, "ramFloorGb": 16, "diskFloorGb": 50}}}
```

Only these keys are accepted; counts are 0..16 per host and at least one serving node in total. In a version 1
file the gateway and the World Authority must be on the host that runs `start`.

### Hosts file version 2: multi-host (T-0321)

Version 2 adds where each host is and how the gateway host reaches it. With **more than one host** it is a
multi-host start; a version 1 file, a one-host version 2 file or no file keep every single-host path unchanged
(loopback addresses, the built-in join token, no admin token, no agents).

```json
{"version": 2, "gateway": "polaris", "worldAuthority": "polaris",
 "network": {"internalNetworks": ["192.168.1.0/24"], "portBase": 0, "networkWindows": ["03:50-04:08"]},
 "hosts": {
  "polaris": {"serving": 4, "spares": 1, "ramFloorGb": 16, "diskFloorGb": 50,
              "address": "192.168.1.20", "ssh": "local", "runRoot": "/home/factory/cluster-runs/regression", "portSlots": 16},
  "ai":      {"serving": 2, "spares": 1, "ramFloorGb": 10, "diskFloorGb": 20,
              "address": "192.168.1.80", "ssh": "factory@ai", "runRoot": "/home/factory/cluster-runs/regression",
              "serverEnv": "~/cluster-server/env.sh", "portSlots": 16,
              "executor": {"tokenFile": "secrets/executor-ai.token"}}}}
```

| Key | Meaning (default) |
|---|---|
| `gateway`, `worldAuthority` | required with more than one host; the World Authority runs on the gateway host in this version |
| `network.internalNetworks` | CIDRs; every host address must lie inside one (required with more than one host) |
| `network.portBase` | port offset of every host (0); a host's own `portBase` overrides it |
| `network.networkWindows` | `HH:MM-HH:MM` local-time windows in which remote nodes' lapsed leases are held (`CLUSTER_NETWORK_WINDOWS`) |
| `address` | the host's LAN address, an IP literal (never a name: `polaris` resolves to 127.0.1.1 on polaris) |
| `ssh` | `local` for the gateway host (the only allowed value there), else `[user@]host` (the host key) |
| `runRoot` | absolute, the same path on every host (the run root); saved Locations in the registry are absolute paths |
| `serverEnv` | file a non-gateway host sources before running the server side (`~/cluster-server/env.sh`, written by bin/server-sync) |
| `portSlots` | node port slots on the host, 1..64 (16); at least serving + spares (+1 on the World Authority's host) |
| `executor.tokenFile` | where this start's executor token for the host is written, relative to `runRoot` (`secrets/executor-<host>.token`) |

Ports on each host: node slot i uses backend UDP `28416+B+i` and control TCP `29416+B+i`; the gateway host also
has the Gateway control TCP `29416+B` and Direct Transport UDP `27116+B`. Two entries on one address need
disjoint slot ranges. The registry receives only the counts and floors (the version 1 body).

**What a multi-host start does** (`cluster start` on the gateway host):

1. Refuses unless every other host is idle (no agent, no node process of the run root) and runs the same archive
   commit (`$CLUSTER_ARCHIVE/manifest.json` after sourcing `serverEnv`).
2. Mints this start's tokens: join, admin, one Executor-scope token per host (32 random bytes, base64) and a start
   id. They go to `<runRoot>/secrets/` on every host, mode 0600, over ssh stdin (never a command line); the Gateway
   reads the Executor tokens as SHA-256 entries from `secrets/admin-tokens.json` (`CLUSTER_ADMIN_TOKENS`). Every
   start mints new ones; a leaked token is dead at the next start. Traffic between hosts is not encrypted
   (a trusted LAN, Viktor 2026-10-01); the tokens authenticate.
3. Writes `<runRoot>/hostd/{hosts.json,launch.json}` on every host (the hosts file and the node launch settings:
   node environment and tuning variables, cfg template, admission settings; no secret, no host path) and stages the
   split world (`cluster-world/`, including its Storage seeds) into every other host's run root with rsync, then
   compares a sha256 tree hash.
4. Starts the Gateway on its LAN address (`--control <address>:<29416+B>`; the Gateway also listens on
   127.0.0.1), gateway-rs, and the World Authority, which warms this host's caches alone.
5. Starts one **cluster-hostd** per host (the gateway host included), detached, log `<runRoot>/hostd.log`.

Every node of a multi-host start binds and advertises its host's address (`CLUSTER_CONTROL_BIND`,
`CLUSTER_NODE_CONTROL_ENDPOINT`, `CLUSTER_NODE_ENDPOINT`), reaches the registry on the gateway host's address,
carries the start's join and admin tokens and gets no `CLUSTER_SHARED_ROOT` (EnvVars.md). `cluster stop` stops
every agent first, then drains the cluster and waits for the node processes on every host; `cluster kill` covers
every host.

## Node identity

- Node ids are global and **never reused**: `node-9` follows `node-8`, whatever the host. A node that stopped or
  died never registers again under its id (409 `node_id_retired`); its replacement gets a new id.
- **Every start gives every node a fresh id.** Nodes live at the stop go to the history with outcome `restart`
  (actor `start`). Their data dirs stay on disk as save holders (see "Stopped nodes" below); `cluster nodes prune`
  deletes the dirs of history nodes whose registry row is gone and that no catalogued save references.
- Every listing shows id, host, PID, age, state, partitions and clients:

  ```
  polaris  serving 3/3 (file 2)  spares 1/1 (file 1)  * runtime differs from hosts file
    #1 node-3  @polaris pid 1505287 age 3m    Serving   0 partitions 0 clients
    #2 node-2  @polaris pid 1505278 age 3m    Serving   1 partitions 0 clients
    #3 node-1  @polaris pid 1505268 age 3m    Draining  1 partitions 2 clients  (remove by factory@polaris: "bench step 2", force in 4m58s)
    #4 node-4  @polaris pid 1509104 age 8s    Spare     0 partitions
    not counted: wa-1 @polaris pid 1503197 age 3m World Authority; gateway @polaris pid 1503161
  ```

- A node can be selected by id (`node-9`), by `PID@host` (`12345@polaris`) or by `host#position`
  (`polaris#2`). The position is the k-th live node on that host ordered by process start, oldest = 1; draining
  and stopping nodes are included, so the number `list` shows is the one to type. Positions shift when an older
  node leaves; every answer echoes the resolved id.

## States

| State | Meaning |
|---|---|
| Starting | launched (or planned), not serving yet |
| Serving | may host partitions; a serving node with 0 partitions is still Serving |
| Spare | warm, holds nothing, activated at once when needed |
| Draining | stop requested: handing its partitions and clients over; the line shows who, why and when it is forced |
| Stopping | forced, or empty and its process ending |
| Failed | crashed without a stop request, shown until its replacement serves |

The registry's internal node states (Active, Closed, Empty, Dead) are not shown. A node that is gone leaves the
live list; its record goes to the **history** (`cluster nodes history`): who stopped it, when, why, its age and
host, whether it was forced, what replaced it. History outcomes: `remove`, `recycle`, `decrease`, `failed`,
`restart`, and `closed` (the registry closed it itself: a save failure, a stall or a direct close). The history
keeps the newest 2000 records.

## Operations

- **`set HOST --serving N --spares S`** changes the host's runtime targets.
  - *Increase:* a warm spare on that host is activated at once (inside the call), then a new spare is launched
    to refill the spare count. Fresh serving nodes are launched only for what the spares cannot cover.
  - *Decrease:* the **oldest** serving node on the host drains first (most accumulated state freed, at the cost
    of more handover); not the least loaded. A spare decrease stops the oldest spare.
  - A decrease that would overload the remaining nodes is obeyed; the answer carries a warning with the
    projected load per remaining node (`CLUSTER_SCALE_IN_MAX_THREAD_SHARE`, default 0.7 of a game thread).
  - Nothing may leave the cluster with 0 serving targets (409 `last_serving_node`).
- **`remove SEL`** drains and stops the node; its host's serving (or spare) target drops by one.
- **`recycle SEL`** drains and stops the node and keeps the count: a spare on the same host is activated in its
  place and receives its partitions, and a new spare refills; with no spare, a fresh node is launched.
- **Draining** is a graceful handover with a seamless client switch. If it has not finished within the drain
  timeout (`--drain-timeout`, default `CLUSTER_DRAIN_TIMEOUT_SECONDS` = 300 s) it is **forced**: the node saves
  its remaining partitions (Final saves) and takes them offline, then its process stops. The deadline pauses
  while partitions recover. `--now` forces at once; a `--now` on a node that is already draining escalates it.
  A forced stop is visible to players on that node: an unpartitioned client is disconnected, a partition routed to
  a connected player is released and recovers on another node.
- A node is never killed while it still owns partitions: a forced node that still owns rows stays Stopping, with
  the reason in the stop's detail, until its own drain has released them.
- **Failures.** A node that dies without a stop request shows Failed; a spare on its host is activated in its
  place and a new spare (new id) refills. Replacement launches on a host that saw deaths in the last 10 minutes
  back off (5 s, 10 s, ... up to 600 s), so a crash loop does not burn through ids.
- **Host floors.** A launch that would take the host below its RAM floor (`MemAvailable`) or disk floor (free
  space under the run root) is refused and shown in the host's warnings; an increase that does not fit is
  applied partially.
- **Rotation** (the regression harness): per host, the oldest serving node past the lifetime limit is recycled
  (`requestedBy: rotator`). Rotation never changes the counts. The rotator waits while any node is Starting,
  Draining or Stopping. A managed deployment refuses `recycle`; the Gateway rotates it itself ("Rotation" in
  ManagedDeployment.md).

## Stopped nodes (T-0323)

A stopped node's registry row stays (Empty, or Dead after a crash) while its data dir holds catalogued saves. Its own
process can no longer serve them, so the rules below apply to both. A Dead node's saves are served like a drained
node's (T-0348): a save is registered only after its file is written and renamed, the file survives the process, and
the requester verifies every fetch against the catalogued checksum (a torn file is reported corrupt and the next
holder is tried; a file that is not on disk is answered `410` by the save-file route and reported the same way). Two limits: a Dead node that node control still lists (`Failed`, until its replacement serves) is
not treated as stopped yet, and a Dead node's saves count as usable only while a live node on its host can serve
them. On a host with no live node they wait (recovery uses the newest save some other holder can serve, as before).

- **A proxy serves them.** For each such "gone" holder the registry names a live regular node on the same host (a row
  from before node control, with no host recorded, counts as on the gateway host). Recovery claims carry it
  (`HolderProxies`), replica directives name it, and a proxied grant binds exactly one catalogued location. The proxy
  serves that file only if its realpath is a `.mps` under its run root. A recovering node on the same host reads the
  file straight from disk (checksum-checked). A host with no live node cannot serve its stopped nodes' files.
- **Saves are moved off.** A gone holder no longer counts toward a save's two copies. The replica queue copies such
  saves to live nodes at the lowest rank, within `CLUSTER_REPLICA_EVACUATE_BYTES_PER_SECOND` (default 512 KiB/s), itself
  inside the budget of all save copies (`CLUSTER_REPLICA_BYTES_PER_SECOND`, default 1 MiB/s). If the copies do not
  keep up with the recycles the registry warns: `replica capacity below invalidation rate` (EnvVars.md).
- **The row goes once nothing names it.** After each retention pass (once a minute) the registry drops a gone holder's
  location from a save being deleted (the node can never acknowledge the delete, and the file goes with the data dir)
  and from a save that live holders already cover. It then removes the row of a gone node that no in-memory save,
  global save, voxel base or journal, owned partition, open handover or archived save names (event
  `node.row-released`). The history record stays.
- **`cluster nodes prune`** keeps the dir of every node that still has a row. Rows named by an archived save (retired
  partitions' saves are archived and never deleted) therefore stay, and so do their dirs.

## Per-host save store (T-0340)

Off by default; `CLUSTER_HOST_STORE=1` in the gateway's environment turns it on (EnvVars.md says when store mode is
entered and which locations are re-keyed). With it a save file belongs to the HOST it lies on, not to the node that
wrote it, so a node stop invalidates nothing.

- **Holder and path.** The catalogue's holder is `host:<name>` and the location is
  `<runRoot>/saves/<host>/P-<X16>/g<X8>-s<X8>.mps` (`GLOBAL/...` for global saves): a pure function of host and save
  id that the registry and the nodes both compute. The registry never takes it from a request. Each host holder has
  a synthetic node row (`host:<name>`, Regular, Empty), which is what the package before T-0340 reads as a stopped
  holder served by proxy.
- **Who uses it.** The registry alone decides. A node offers store mode when it registers and the answer
  (`HostStore`) says whether it uses it: only in store mode, and only when it reports the host the plan runs it on.
  A node that was refused keeps its saves in its data dir, node-held (status `hostStore.legacyNodes`).
- **Writing.** Several processes write one directory, so a file is installed and never overwritten: a temporary name
  carrying the node id, then `link(2)` into place. A file that is already there with the same content is success; a
  readable save with other content is refused for a save the node wrote and replaced for a fetched copy (verified
  against the catalogue); a damaged one is replaced. Partial downloads carry the node id too.
- **Store states.** Serving: a regular node of the host (by the plan) is Active or Closed with a live lease; a spare
  counts. Held: all of them lapsed inside a network window. Dark: no live node, host still planned. Lost: the host
  left the plan. Only a Serving store is offered as a source. Held and Dark copies still count toward the target, so
  nothing is re-replicated; a recovery that needs a save in a Held store waits. Lost copies stop counting.
- **Copy target.** `min(2, hosts planned with serving > 0)`: two hosts on a multi-host cluster, one copy on a single
  host. A node is handed a save only if its own host holds no intact copy. A node stop queues nothing.
- **Serving and grants.** Any live node of the host serves its store. A save grant for a host-held save is bound to
  (save id, host) and lists the host's servers in the order to try: spares, the Active nodes rotated per grant, then
  Closed ones. Any registered, non-Dead node the plan runs on that host may consume it, once; a refusal does not burn
  the token, so the requester tries the next server. The node's save-file route runs on two worker threads and
  streams the file, so the control listener keeps answering during a read.
- **Deletes.** A host's pending deletes are handed to one of its live nodes at a time (a 60 s turn). Global saves
  have no retention, for node holders and host holders alike.
- **A damaged store copy** found by a recovering node on that host is reported corrupt (the host holder); the fetched
  copy replaces the file and its registration clears the mark.
- **Rows of stopped nodes.** In store mode the release pass does not consult the archive: a stopped node's row goes on
  the first pass after nothing in memory names it (a location it still holds, a voxel base or journal, an owned
  partition, an open handover). Each released row leaves `node -> host` in the alias map (`HostStoreAliases`, status
  `hostStore.aliases`). A save that still names a released node - an archived save whose file was not moved into the
  store - stays served: a live node of that host is its proxy, under a grant bound to the catalogued location, and
  the replica queue copies it into a store like any save short of copies. An archived save whose file is in the
  store comes back host-held. With no live node on that host it is not offered. The package before T-0340 has no
  such rule: after a rollback to it, archived saves naming a row released here have no servable holder.
- **`cluster nodes prune`** in store mode (or after it: `hostStore.aliases` > 0) also keeps every data dir that still
  holds a `.mps` file under `cluster/saves`, because a released row no longer proves that no archived save names it.
- **Status.** `/registry/status` `hostStore`: `enabled`, per host `state` and `saves`, `unmoved` (locations still
  node-held at a legacy path), `legacyNodes`, `aliases`.
- **Legacy files: the sweep (step 7).** A world from before store mode keeps its saves in node data dirs, node-held
  (status `hostStore.unmoved`), and they pin the stopped rows. `cluster hostd sweep` moves ONLY the files the catalogue
  names at that node (class `rekey`), per host, from every stopped node dir (not live in node control, no
  Active/Closed row, no process), crash-safe and idempotent:
  1. it reads each save file's envelope (size, ids, trailer checksum) and asks the registry what the file is
     (`POST /registry/host-store/adopt`, admin token, `commit: false`);
  2. it links a `rekey` file into `<runRoot>/saves/<host>/` under the same name with `link(2)` - same file system, no
     byte copied, never over an existing file (an existing one must be the same inode, or byte-equal with the
     catalogued size and checksum) - and fsyncs each store directory once per batch;
  3. it commits: the registry re-keys exactly that node's location to `host:<host>` - in memory, or the archived row
     in place without reading the partition back - and writes it durably before it answers (`hostStore.adopted`);
  4. it unlinks the source only on `rekeyed`, or on `hostHeld` when the store's file is this very inode (a move an
     earlier sweep committed: class `resume`).

  Classes of the other files (decision 2026-10-04): `unnamed` (the save is catalogued, but no location names this
  copy: superseded or dropped as a gone holder's), `uncatalogued` (no row in memory or in the archive: a save retention
  removed after the node stopped - its delete directive went to a node that was gone - or one never registered) and
  `hostHeldCopy` (the host's store holds the save as another file) are NOT moved: they stay in their dir and go only
  with it (below). `lateFinal`: a file named by a pending `cluster/unregistered-finals.jsonl` (T-0347: a Final of a
  local stop that the next start still reports, the one uncatalogued file the registry may still need) is never
  moved or deleted and keeps its dir. `kept.<reason>`: a live node, another size or checksum, the wrong host, a
  corrupt mark, a pending delete, an unreadable file, a conflicting store file or a refused commit leave the file
  where it is and keep the dir; so does a `.mps` outside the save layout.

  An interrupted sweep resumes. Temporary files and partial downloads of stopped dirs, and those in the store older
  than a day, are deleted. A stopped dir (and its `-magnetar` dir; node logs stay) is deleted when ALL hold: the
  registry has released its row; no pending unregistered-finals journal; every save file left is unnamed (rekey and
  resume files have left it); not among the newest `CLUSTER_STORE_KEEP_DIRS` (2) stopped dirs; older than
  `CLUSTER_STORE_KEEP_HOURS` (24). A dir that still holds unnamed files needs on top: `--delete-unnamed`, no write in
  the dir (or its node log) for `--unnamed-days` / `CLUSTER_STORE_UNNAMED_DAYS` (7), and `--backup-verified PATH`.
  Without the flag such a dir is never deleted.
- **The backup precondition.** A run that deletes save files (`--apply` with `--delete-unnamed`, or archive
  retention) refuses before touching anything (exit 3) unless `--backup-verified PATH` names a committed part of a
  `bin/world-backup` backup (cluster-regression) of this run root on this host: `PATH/BACKUP.json` present (written
  by its commit only, after the copy's `.mps` count matched the run root's), not a `.partial` dir and no
  `.part.json`, its `root` this run root, its `host` (multi-host parts) this host, `mps` > 0. Once the plan is known,
  the backup's `created` must be later than the newest unnamed file to delete, and the backup must hold each of
  them at the same relative path with the same size; any gap deletes none of the unnamed dirs (`backupCheck.refused`,
  exit 3). world-backup copies the node data dirs but NOT `<runRoot>/saves/`, so the store itself is in no backup.
- **The report.** Dry run by default: nothing written, linked or deleted. It gives per host `classes` (files and bytes
  per class), `unnamed` (files, bytes, oldest and newest mtime), per dir the same plus `lastWrite`, `underFlag` (the
  dirs a `--delete-unnamed` run would delete, their bytes and their unnamed files), `wouldDelete`, `keptDirs` (why),
  `counts` (leftovers, retention) and the disk report. `--registry-db` answers from a COPY of `registry.db` (the
  registry's rules, read from the stored rows) for a dry run on a copied run root with no registry running; it
  refuses `--apply`.
- **Archive retention** (open question 1) is a knob, default off: `--archive-retention-days N`
  (`CLUSTER_STORE_ARCHIVE_RETENTION_DAYS`) deletes the files of partitions that this host's sweep first saw archived
  at least N days ago (`saves/<host>/.archive-seen.json`; a partition read back from the archive leaves it), in the
  store and in stopped dirs. The rows stay; recovery of such a save falls back like any missing copy. CLI only, with
  the backup precondition.
- **The host agent** runs a store keeper once the registry is in store mode: the disk report (store bytes and files,
  legacy bytes left per node dir, hard links counted once, free MB) every `CLUSTER_HOSTD_DISK_REPORT_SECONDS` (900)
  into `hostd-status.json` `store.disk` (`hostd status`, `cluster hosts`; `hostd disk` prints it at once), and a
  sweep with `--apply` every `CLUSTER_HOSTD_SWEEP_SECONDS` - 0 by default, so hostd never sweeps on its own until
  that is set. The keeper's sweep never deletes unnamed files and never runs archive retention.
- **Not there yet.** The registry still cannot see a host's free disk (its disk-floor check passes every host; the
  free MB is in the host agent's report only). In the store the sweep deletes only temporary files and partial downloads
  older than a day; a store file no save names (a legacy file nothing catalogued, moved in) stays. Voxel bases stay with their node (their rows stay pinned); global saves have no
  retention. A legacy file of a node that is still live is not moved.

## The launcher

The registry publishes its decisions as the node plan (`GET /admin/v1/plan`): one slot per planned node, slot key
= node id, goal `Wanted` (start it), `Draining` (leave it running), then `Kill` once the node is empty (stop the
process and report it `Gone`). Exactly one launcher per host carries the plan out (`Cluster.actualize_step`,
guarded by `<root>/launcher.lock`): it starts Wanted nodes, never restarts an id that registered once, stops Kill
nodes (30 s to exit on their own, `CLUSTER_SELF_EXIT_SECONDS`, then SIGKILL), checks the host floors and reports
every observation with the PID through the executor heartbeat. `cluster start` runs it once. After that
something must keep running it: the harness rotator on the regression cluster, or `cluster nodes ... --wait`.
Without a heartbeat for 60 s the registry reports `executorSilence` and nothing refills spares or replaces failed
nodes.

The launcher sets `CLUSTER_NODE_HOST` for every node (the registry matches nodes to hosts by it) and seeds each
new node's `SpaceEngineers-Dedicated.cfg` from `CLUSTER_NODE_DEDICATED_TEMPLATE` when set.

### cluster-hostd (multi-host, T-0321)

In a multi-host start the launcher of every host is **cluster-hostd** (`cli/cluster_hostd.py`, run as
`cluster --root <runRoot> hostd run --host <host>`). It holds the host's fenced executor lease on the registry
(`POST /executor/v1/heartbeat`, the contract Quasar.Host speaks, ManagedDeployment.md) with the host's Executor
token and carries out that host's slots with the same launcher logic: Spawning after a launch, Ready once the
registry holds a live incarnation (with its epoch), Gone acknowledging the commanded incumbent and epoch, Failed
with the reason (`host_floor: ...`, `launch refused: ...`, `no free port slot ...`). Differences from the
single-host launcher:

- The first node on a host starts alone and the rest follow once it is Ready (or after
  `CLUSTER_HOSTD_WARMUP_SECONDS`, default 300); a new node's hub snapshot comes from a warm node on the host,
  else from `CLUSTER_PREWARM_SOURCES` (staged by bin/server-sync).
- Nothing is launched or killed on a plan older than 30 s (half the 60 s lease); a lost lease is acquired again;
  a second agent on the host is refused by `<runRoot>/launcher.lock` and by the registry ("Another executor
  session owns this host"); a refused credential (a newer start) makes the agent exit 3 and leave its nodes alone.
- The rotator and `nodes ... --wait` no longer launch anything in a multi-host start (`actualize_step` is a no-op);
  recycles and counts still go through the admin API.

`cluster hostd start|stop|status|kill-nodes` manage the agent of one host, `cluster hosts` shows every host's
agent and node processes, `cluster logs <node>` tails a node's log where it runs (ssh), `cluster node-url <node>`
prints its control URL from the registry.

## Managed deployments

Managed (Quasar) deployments keep their hash-approved node slots. Their hot spares are slots of that list marked
`spare`; a promoted spare's dead slot comes back as the spare instead of a new id. `list` and `history` work
read-only (records built from the slots); `set`, `remove` and `recycle` answer 409 `managed_inventory_fixed`. See
"Hot spares" in ManagedDeployment.md.

## Legacy scale surface

`cluster scale --to N` and `POST /registry/scale?nodes=N` (T-0312) are deprecated. They map onto
`set <sole host> --serving N` (409 `multiple_hosts` with more than one host); `force` and `retire` are ignored.
