# Cluster CLI

`cluster.py` (repo: `Cli/cluster.py`; package: `cli/cluster.py` behind the `cli/cluster`
wrapper) launches, drives and tears down one cluster: Gateway + 1..8 regular nodes + one
World Authority. It uses only the Python standard library.

In a package, use the wrapper — it validates the game-install variables, points the CLI at
the packaged binaries (Gateway, world tool, compiled node and World Authority plugins) and
checks that Magnetar is reachable (the release package does not carry it; see its `README.md`):

```bash
export SPACE_ENGINEERS_SERVER=/path/to/SpaceEngineersDedicatedServer
export MAGNETAR_HOME=/path/to/Magnetar
export CLUSTER_ROOT=~/cluster-run
cli/cluster fresh            # new cluster; needs --wipe (which ERASES it) if the run root holds one
cli/cluster start            # later restarts of the same cluster: warm restart, no flag
```

## Global options

| Option | Env default | Meaning |
| --- | --- | --- |
| `--root` | `CLUSTER_ROOT`; required when unset | run root: node data, logs, registry, dumps |
| `--world` | `CLUSTER_WORLD_TEMPLATE` | world template to split (vanilla save or premade world) |
| `--steam` | `CLUSTER_STEAM_LISTEN`, `0.0.0.0:27016` | Steam transport bind; empty string disables |
| `--listen` | `CLUSTER_LISTEN`, `127.0.0.1:27116` | Direct Transport frontend clients connect to |
| `--nodes` | `CLUSTER_NODES`, `2` | regular nodes (1..8) beside the World Authority; the serving count of the hosts file built when there is none |
| `--spares` | `CLUSTER_SPARES`, `0` | hot spares (0..4); the spare count of that built hosts file |
| `--hosts` | `CLUSTER_HOSTS_FILE`, else `<root>/hosts.json` | hosts file applied by `start`/`fresh` (node control, see `cluster nodes` below) |
| `--admin` | — | Steam id admitted as admin; repeatable |
| `--name` | `CLUSTER_NAME` | server name published in the Steam browser row |
| `--port-base` | `CLUSTER_PORT_BASE`, `0` | offset added to every port below, so a second cluster can share the host |
| `--split-step` | `CLUSTER_SPLIT_STEP`, `to-split` | MagnetarWorld step that splits the template (`to-split`, or a fixture variant such as `to-handover`) |
| `--node-env` | — | `[NODE:]KEY=VALUE` extra environment for every node, or for one node; repeatable |
| `--gateway-arg` | `CLUSTER_GATEWAY_ARGS` (shell-split) | extra Gateway argument; repeatable |

Port plan (loopback, plus the port base): node-i backend `28416+i`, control `29416+i`; the WA
takes the slot after the last regular or spare node; Gateway control `29416`, Direct Transport
`127.0.0.1:27116` unless `--listen` says otherwise.

`CLUSTER_EXTRA_PLUGINS` installs further precompiled local plugins next to the cluster plugins:
comma-separated folders laid out like the packaged ones (`<Name>/<Name>.dll` + `<Name>.xml`),
each optionally prefixed with the role it is for (`node:/p/A,wa:/p/B`). The scenario suite uses
it for its test utility plugin. `CLUSTER_LEASE_TTL_SECONDS` sets the registry lease TTL
(default 15).

`DIRECT_TRANSPORT_BINARIES=/path/to/DirectTransport` selects a compiled transport bundle
instead of a checkout or the hub. Leave `DIRECT_TRANSPORT_SOURCE` unset. The bundle
must contain `DirectTransport.dll`, `DirectTransport.xml` and `LiteNetLib.dll`; its
manifest declares ID `direct-transport`, an exact 40-character lowercase commit,
`Runtimes=CoreCLR` and `Platforms=Linux`. This is the layout prepared by Quasar's
dependency provisioner. The caller verifies the approved artifact hashes before use;
the CLI validates layout/metadata and copies the bundle into each node's local plugins.
An invalid bundle never falls back to the hub. Switching back to a checkout or the hub
removes the previously managed local transport copy.

Extra plugins cannot reuse infrastructure IDs or duplicate another local/hub selection.
These input checks do not replace deployment admission verification or cleanup of
arbitrary pre-existing plugin sources. Compatibility/common plugins and native assets
still require pinning before a complete deployment can start offline.

`CLUSTER_COMMON_PLUGINS=/path/to/CommonPlugins` selects frozen common bundles, including
`dotnet-compat` and `linux-compat` with all resolved native assets. It requires packaged
role binaries and `DIRECT_TRANSPORT_BINARIES`, with no extra-plugin or hub overrides.
The caller must verify the complete snapshot first. The CLI copies common plugins to
each config, empties hub/dev/mod source lists and refuses unexpected local entries.
Compatibility plugins remain implicit; ordinary common plugins go into the local profile.
The warm-up node also receives `-noupdate`, and configs do not share the mutable GitHub
cache. This pins loader inputs; it is not a network sandbox for plugin/game code.
Writable runtime work directories, config synchronization and serving admission belong
to the Host lifecycle integration.

Packages containing this support include `cli/deployment-capabilities.json` with
`schemaVersion: 1` and `frozenPluginBundles: true`. Quasar Host preparation requires
this verified marker so older launchers cannot silently ignore the frozen inputs.

## Subcommands

- `fresh [--wipe]` — stop everything, reset the world from the template (via MagnetarWorld
  `to-split`), start Gateway + nodes, wait until Serving, print the registry. Over an
  **existing cluster** (a `gateway-registry` or any node data dir in the run root) it refuses
  unless `--wipe` is given, and it checks that before stopping anything: `--wipe` ERASES the
  existing cluster. Without an existing cluster it needs no flag. The flag is a scripting
  convenience that saves removing the files by hand; an operator never issues it without
  approval.
- `start` — start against the existing run root. A run root that still holds a previous
  session's registry and its node data is a warm restart of the recorded instance: the world
  and the saves carry over, while the node set comes from the hosts file with fresh node ids
  (the runtime `nodes set` targets of the previous run do not). A
  registry with no node data beside it is refused (fresh nodes would be fenced by it); the
  way out is `fresh --wipe`.
- `stop [--grace SECONDS]` — graceful shutdown through the admin API: every live partition
  and the WA write Final saves, then the processes exit themselves.
  When the registry does not answer (T-0347), every node is asked on its own control port
  (`POST /cluster/local-stop`, on other hosts through `hostd local-stop`) to write a Final
  save of each partition it owns to its own save store and exit; Finals the registry could
  not be told about are journaled (`<node>-data/cluster/unregistered-finals.jsonl`) and
  reported by the next start before any recovery claim (`POST /registry/saves/late`; the
  registry holds recovery of the stopped nodes' partitions until their host has reported,
  `CLUSTER_LATE_SAVE_HOLD_SECONDS`, default 300). Exit code 1 either way: the WA's global
  save is not covered and the next start is a Recovery start.
- `status` — registry + transport + node summary as JSON; **exit code reflects health**
  (0 healthy, 1 problems — each problem is a line in `problems`). Unhealthy when: no
  cluster processes run, the phase is short of Serving, the executor plan carries a Failed
  slot, a configured node slot has no process, a node process reports a non-Serving
  lifecycle, or the WA misses its authority generation / lets its registry lease lapse.
- `kill [slot]` — SIGKILL one slot (`node-1`, `wa-1`, `gateway`) or everything.
- `diag --depth cluster|nodes|partitions|entities [--node N] [--partition P] [--player STEAMID]
  [--entity ID] [--limit N] [--cursor C]` — structured snapshot from the Gateway diag
  endpoint; depth controls cost (entity arrays only at `entities`).
- `trigger <task> [--wait] [--node N] [--partition P] [--unthrottled]` — run a maintenance
  task through the Gateway: `balance`, `save`, `catalog-gc`, `rotation-check`.
- `config get` / `config set key=value ...` — the runtime handover knobs (`/admin/v1/handover-config`),
  e.g. `config set tee.async=true import.entitiesPerFrame=8`; `key=` (empty) restores the node default.

## Multi-host (T-0321)

A hosts file version 2 with more than one host (NodeControl.md "Hosts file version 2") makes `start` a
multi-host start from the gateway host: tokens minted per start and written 0600 to every host, the split world
staged, one cluster-hostd per host. Extra verbs:

```sh
cli/cluster hostd run|start --host ai [--fresh]   # the per-host agent (start = detached, <root>/hostd.log)
cli/cluster hostd stop|status|kill-nodes|local-stop           # this host's agent / its node processes
cli/cluster hostd sweep [--dry-run|--apply] [--host ai] [--registry-db COPY]   # T-0340 step 7, NodeControl.md
cli/cluster hostd sweep --apply --delete-unnamed [--unnamed-days 7] --backup-verified ~/cluster-runs/backups/<set>
cli/cluster hostd disk [--host ai]                 # store bytes, legacy save bytes left in node dirs, free disk (JSON)
cli/cluster hosts                                  # every host: agent status and node pids (JSON)
cli/cluster logs node-9 [-n 200] [-f] [--game]     # a node's log read in place on its host (ssh tail)
cli/cluster node-url 12345@ai                      # control URL from the registry (+ the header to send)
```

`logs` takes any node selector, `wa-1`, or `gateway`/`gateway-rs`/`hostd` (this host's own). A node id that is no
longer live (crashed, killed, removed) or a Gateway that is down still works: the CLI asks each host of the hosts
file for `<runRoot>/<node>.log` and reads it where it is (node ids are never reused, so at most one host has it).

In a multi-host start the CLI talks to the Gateway on its LAN address with the per-start admin token
(`<root>/secrets/admin.token`, or `CLUSTER_ADMIN_TOKEN`), and `Cluster.node()` calls a node on any host through
its registered control endpoint with that token.

## cluster nodes (node control, T-0320)

The registry owns the node set: per-host `serving` and `spares` targets, node ids that are never reused
(`node-9` after `node-8`, across hosts), and every stop decision. The hosts file sets the targets at
start; runtime changes last until the next start. Operator states: Starting, Serving, Spare, Draining,
Stopping, Failed (the registry's Active/Closed/Empty/Dead are not shown). The operator model (policy,
failures, floors, rotation) is in [NodeControl.md](NodeControl.md); the drain mechanism in
[NodeScaling.md](NodeScaling.md).

```sh
cli/cluster nodes list [--json]                       # per host: counts vs targets vs file; every live node
cli/cluster nodes set polaris --serving 5 --spares 1  # runtime targets (a spare activates at once, a new spare refills)
cli/cluster nodes remove 12345@polaris [--now]        # drain and stop; the host's target drops by one
cli/cluster nodes recycle polaris#2 [--drain-timeout 300] --reason "..." --wait
cli/cluster nodes history [--limit 50] [--host polaris] [--json]
cli/cluster nodes prune [--apply]                     # data dirs of stopped nodes no catalogued save references
                                                      # (host-store mode: a dir that still has .mps files is kept, NodeControl.md)
```

- `list` prints one header per host (actual/target counts, the hosts-file value, a mark when they differ), one
  line per live node, and what is not counted; the same lines head each node in `cluster-status`:

  ```
  polaris  serving 3/3 (file 2)  spares 1/1 (file 1)  * runtime differs from hosts file
    #1 node-1  @polaris pid 1485318 age 3m    Draining  1 partitions 2 clients  (remove by factory@polaris: "bench step 2", force in 4m58s)
    #2 node-2  @polaris pid 1485328 age 3m    Serving   1 partitions 0 clients
    #3 node-3  @polaris pid 1485337 age 3m    Serving   0 partitions 0 clients
    #4 node-4  @polaris pid 1487368 age 12s   Spare     0 partitions
    not counted: wa-1 @polaris pid 1485167 age 3m World Authority; gateway @polaris pid 1485132
  states: Starting = launched, not serving yet; Serving = may host partitions (0 is still Serving); ...
  ```

  `--json` prints the `NodeControlView` of `GET /admin/v1/node-control` (AdminAPI.md).
- `set`, `remove` and `recycle` print the operation result: `node` (the resolved id), `replacement`,
  `actions` (what the registry did: activated a spare, planned an id, started a drain) and `warnings`
  (e.g. the projected load of a decrease). They return at once; `--wait` then prints the view at every change
  until nothing is Starting, Draining or Stopping (and, for `set`, the counts match the targets), running the
  launcher step itself when it can take the launcher lock. `--reason` is recorded as the stop's why (for a
  decrease: `"<reason> (serving on polaris lowered to 1)"`); the requester is `$USER@host`.
- Refusals are the admin API's 4xx codes, nothing is changed: `selector_not_found` (the message says so when
  the id is in the history), `selector_ambiguous`, `last_serving_node`, `already_stopping`, `host_not_found`
  (a host outside the hosts file), `node_control_inactive`, `managed_inventory_fixed`.
- `history` lists the nodes that are gone, newest first: end time, id, host, pid, age, outcome (`remove`,
  `recycle`, `decrease`, `failed`, `restart`, `closed`), who, `(forced)`, why, and the replacement.
- Selectors: `node-9`, `PID@host`, `host#position` (position = k-th live node on the host, oldest first).
- A drain is forced after `--drain-timeout` (default 300 s): remaining partitions are saved and taken
  offline, then the process stops. `--now` forces at once (visible to players).
- **Hosts file** (`--hosts`, `CLUSTER_HOSTS_FILE`, else `<root>/hosts.json`, else built from
  `--nodes/--spares` for this host), applied by `start`/`fresh` only, with fresh node ids every start:
  `{"version":1,"gateway":"polaris","worldAuthority":"polaris","hosts":{"polaris":{"serving":4,"spares":1,"ramFloorGb":16,"diskFloorGb":50}}}`.
  A launch that would take the host below a floor is refused and shown in the host's warnings.
- **Launcher.** `cluster start` runs the plan once; afterwards exactly one launcher per host
  (`<root>/launcher.lock`) must keep running it (`Cluster.actualize_step`: start Wanted nodes, stop the
  ones the registry marks Kill, report through the executor heartbeat) - in the regression harness that is
  the rotator; otherwise `cluster nodes ... --wait` takes the lock while it waits. **Once the hosts file is
  applied the registry expects a launcher heartbeat within 60 s**, else `cluster status` reports
  `executorSilence` and nothing refills spares or replaces failed nodes.
- The launcher sets `CLUSTER_NODE_HOST` (the hosts-file host) for every node; the registry matches
  nodes to hosts by it. A node's `SpaceEngineers-Dedicated.cfg` comes from `CLUSTER_NODE_DEDICATED_TEMPLATE`
  when set (checked against `CLUSTER_NODE_DEDICATED_REQUIRE`, e.g. `ExperimentalMode=true`), else a minimal one.
- A start that names nodes (`start(nodes=[...])`), `CLUSTER_NODE_CONTROL=0` / `Config(node_control=False)`
  (the scenario bench) and a registry without node control keep the positional start: no hosts file, no
  node control, nodes outside a plan accepted. While node control is active the registry refuses a node id
  that is not planned (`node_not_planned`) or that already stopped or died (`node_id_retired`).

## cluster scale (deprecated)

Deprecated with T-0320, kept for one release.

Use `cluster nodes set HOST --serving N`: the registry maps `scale --to N` onto the sole host's serving
target (409 with more than one host), and the command's launch loop runs the node-control launcher step.

```sh
cli/cluster scale                         # status: target (sum of serving targets), phase, serving/starting/retiring/retired nodes, hosts
cli/cluster scale --to 5                  # = cluster nodes set <sole host> --serving 5, then run the launcher until settled
cli/cluster scale --to 5 --no-launch      # only set the target; another launcher (the harness rotator) acts on it
```

`--retire` and `--force` are accepted and ignored: the node that drains is the oldest (NodeControl.md) and a
decrease is never refused (a capacity warning is returned instead).

## cluster-status

`cli/cluster-status` (shipped beside `cli/cluster`) formats `cli/cluster diag --depth entities
--registry summary` into a per-node overview. With node control (T-0320) it opens with the
`cluster nodes list` text and the state legend, and heads each node's section with that node's
`nodes list` line (`#k node-N @host pid PID age AGE STATE partitions clients`); the registry's
Active/Closed/Empty states are not shown. A stopped node (its row stays in the registry as a save
holder) gets no section; the nodes such rows belong to are named on one line
(`2 stopped node row(s) not shown (cluster nodes history): node-1, node-2`). An older registry (404 on
`/admin/v1/node-control`) keeps the old per-node header and shows every row. Legend:

| State | Meaning |
| --- | --- |
| Starting | launched, not serving yet |
| Serving | may host partitions (0 is still Serving) |
| Spare | warm, holds nothing |
| Draining | handing its partitions over (who, why, time to force on the line) |
| Stopping | forced or empty, process ending |
| Failed | crashed, until replaced |

Per node it then shows: node age and rotation state, EntityId range use,
partitions with their grids and player characters (as pasteable GPS strings), PCU and effective
load per node, and an "in flight" section for handovers, blocked handovers, recovering or unowned
partitions and switching clients.

After the balance lines a `--- hosts (N) ---` block (T-0321) gives one line per host (node placement from
node control, else the host the node's diag names): node and balanceable counts, effective sim-load sum and
mean, sim speed min/mean, and a `paths:` line summing the calls that host's nodes MADE per channel
(CHANNELS.md id) from the node diag `transport` section, `local n / lan n` plus the worst lan p99 bucket and any
`fallback` (served requests are the callee's side and are left out). A build without the `transport` section
prints no paths line.

```
--- hosts (2) ---
  polaris: 4 node(s), 4 balanceable  |  effective sim-load 41.0ms/s (mean 10.3ms/s)  |  sim min 0.98x mean 1.00x
    paths: N3 local 1,204 / lan 88 (lan p99 <=4ms) | R2 local 902 / lan 0
  ai: 2 node(s), 2 balanceable  |  effective sim-load 18.2ms/s (mean 9.1ms/s)  |  sim min 1.00x mean 1.00x
    paths: N3 local 12 / lan 90 (lan p99 <=4ms) | R2 local 0 / lan 450 (lan p99 <=2ms)
```

```sh
cli/cluster-status --root ~/cluster-run       # one snapshot (or set CLUSTER_ROOT)
cli/cluster-status -w 5                       # refresh every 5 s
cli/cluster-status --json                     # the raw diag JSON
cli/cluster-status --from diag.json           # format a captured snapshot (- = stdin)
```

It runs the `cli/cluster.py` of the archive it lives in directly (symlinks followed;
`CLUSTER_ARCHIVE` overrides), bypassing the `cli/cluster` wrapper: a diag read needs only the run
root, not a game install, Magnetar or plugin binaries. Node rotation is external to the cluster, so the
rotation column needs `CLUSTER_NODE_LIFETIME` (seconds) or `CLUSTER_NODE_LIFETIMES_FILE` (a JSON
the rotator publishes: `configuredLifetime`, `rotatorPid`, `nodes.<id>.lifetime`); without either
the column is omitted.

## Tests

The scenario suite in `Test/` drives a release package through this CLI (see `Test/README.md`).
Its admin API scenarios (P3-ADM-01..03) run a self-hosted Gateway without game nodes.

## Relation to the admin API

`cluster.py` owns the down-state lifecycle: it starts OS processes, prepares run roots,
and escalates teardown — things a stopped Gateway cannot serve over HTTP. While the
cluster is up, management goes through the versioned `/admin/v1` contract
(`Docs/Gateway/AdminAPI.md`) and its thin C# CLI (`ClusterRegistry.Cli`, shipped as
`cli/gateway-admin`); Quasar invokes `cluster.py` (locally or over SSH) only for
`fresh`/`start`/`kill`-level state control.

## Package-mode environment

Set by the `cli/cluster` wrapper; usable directly for custom setups:

| Variable | Meaning |
| --- | --- |
| `MAGNETAR_HOME` | Magnetar install tree holding `MagnetarInterim.bin` |
| `CLUSTER_PLUGIN_BINARIES` | compiled node / WA plugins installed as local plugins into every node's config dir (the package's `plugins/`) |
| `CLUSTER_PLUGIN_SOURCE` | developer override: a cluster checkout Magnetar compiles as dev folders instead of the binaries |
| `DIRECT_TRANSPORT_SOURCE` | unset (the default) loads the MagnetarHub's pinned `direct-transport`; a path registers that checkout as a dev folder — the dev override |
| `DIRECT_TRANSPORT_BINARIES` | compiled, pinned `DirectTransport/` folder; mutually exclusive with `DIRECT_TRANSPORT_SOURCE` |
| `MAGNETAR_GATEWAY_DLL` | packaged Gateway dll (otherwise `dotnet run` from source) |
| `MAGNETAR_WORLD_TOOL` | `MagnetarWorld.dll` used to split/verify worlds |

## MagnetarWorld

`dotnet tools/MagnetarWorld/MagnetarWorld.dll` — world conversion and verification (used by `fresh`,
also usable standalone): `to-split VANILLA SPLIT` (vanilla → cluster form),
`to-vanilla SPLIT VANILLA [DATA_ROOT...]` (cluster → vanilla), `verify-mps FILE`,
`semantic-diff BEFORE AFTER ENTITY_IDS`, `catalog-scan`, and fixture builders used by the
test bench. Needs `SPACE_ENGINEERS_BIN64` (game assemblies) and `SPACE_ENGINEERS_ROOT`
pointing at the game install when content lookups are involved.

## SDK and authenticated loopback requirements

Cluster 1.1.0 requires Magnetar 2.4.2.1 or newer with managed configuration and plugin
services, even for unmanaged clusters. Before resetting data or launching a node,
`cluster.py` checks the installed `Libraries/MagnetarInterim/PluginSdk.dll` through
the Gateway's metadata-only `--check-plugin-sdk` command: its PluginSdk contract must
have the major of `PluginSdkContract` in `Shared/Cluster/PluginSdkContract.cs` and an
equal or higher minor. No game starts for this check.

When Gateway tokens are configured, loopback also requires authentication. Export
`CLUSTER_ADMIN_TOKEN` for `cluster.py`; its Gateway requests now send that bearer.
Requests to node endpoints do not receive it. For token-file installations, export a
matching manage credential; the CLI does not extract credentials from the token file.
