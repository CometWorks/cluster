# Managed deployments and executors

Quasar consumes the entire verified release archive. Required cluster plugins stay
packaged Local plugins; compatibility plugins remain implicit Magnetar core plugins.
Cluster 1.1.0 requires Magnetar 2.4.2.1 or newer with managed configuration and
plugin services, including unmanaged launches. Release builds, CLI node startup and
managed preparation check the actual PluginSdk contract version (the assembly version
for a Magnetar that declares no contract) and required types using
`ClusterRegistry --check-plugin-sdk PATH`. An incompatible SDK is refused before launch;
the displayed launcher version alone is insufficient. Magnetar PR #57 must merge and
a compatible release must be available before this cluster release ships. The Gateway now ships an apphost executable.

`cli/managed_deployment.py` performs nonlaunching production preparation. `convert`
runs the shipped MagnetarWorld `to-split` command into a new staging directory, verifies
the source is unchanged and promotes the result. `prepare` consumes a hash-approved
specification, a verified installation and pinned world seed. It emits a per-host
execution bundle, config files, canonical plugin envelopes and initial data directories.
Preparation accepts Workshop mods only when the caller supplies one locally staged
`<id>.sbm` directory per listed mod and a SHA-256 inventory in `modFiles`. The
`--mods` input is copied into every node seed; nodes and World Authority load these
bytes locally and never fetch Workshop content at start. A `profileSettingsXml` patch
updates session settings and the mod list in a copy of the world seed. The bundle
records `profileContent` so Quasar.Host can apply the same selection to stopped,
preserved worlds. Cluster 1.1.11 advertises this with `managedProfileMigration: 1`.
The Host rewrites those checkpoints, so node admission compares what the checkpoint says, not its
bytes: the Gateway hashes the session settings and mod list of its `CLUSTER_WORLD` at start, and every
node hashes those of its own at registration (`Shared/Cluster/WorldSettingsHash.cs`, SE-796).
The Quasar Host command wraps preparation; it never invokes the test CLI's `fresh`
(with or without `--wipe`), test identity constants or accept-all frontend.

Preparation does not write Python bytecode into the verified package. Hosts should
also invoke the script with `python3 -B` when consuming older packages, including
1.1.0, whose initial helper import otherwise creates an unpinned `__pycache__` file.

Partition-save replication authenticates its Registry grant request with the node
join credential, then uses the returned transfer grant for the peer download.
The 1.1.0 runtime omitted the grant-request credential: a token-protected cluster
could reach Serving while replicas repeatedly failed with HTTP 401. Deploy a release
containing this fix before accepting replica durability or clean shutdown testing.

The specification names `schemaVersion: 1`, `clusterId`, `worldId`, `serverName`,
`binaryVersion` (the node's game BuildVersion), `memberLimit`, `administrators`, `hosts`,
`gatewayHost`, `gatewayControl`, `steamListen`, `internalNetworks`, `worldFiles`, `nodes`,
and host environment-variable names `joinTokenEnvironmentVariable`,
`adminTokenEnvironmentVariable`, `adminTokensFileEnvironmentVariable`.
The optional `worldName` is the world name players see: each node's cfg `WorldName` (which becomes the loaded
session's name) and the Gateway's Steam map name in `admission.json`. Without it they get `worldId`, which
stays the cluster's identity either way. The node's save folder is `Saves/<worldId>` whatever the name
(`CLUSTER_SAVE_FOLDER`), because that is where Quasar seeds the mods' world storage. The optional `messageOfTheDay` and `messageOfTheDayUrl` go into each node's
cfg as `MessageOfTheDay` and `MessageOfTheDayUrl`; the node a player spawns on shows it.
`steamListen` is the production client frontend. The optional `directListen` additionally starts
the Direct Transport frontend (`--direct --listen`), which admits every client and therefore sets
`CLUSTER_ALLOW_TEST_ACCEPT_ALL=true`; use it for test clusters only. The Gateway refuses to bind it
to a publicly reachable interface: use loopback or an address inside `internalNetworks`. A
specification may omit `steamListen` when it names `directListen` (a test cluster on a host
without `steamclient.so`, like the bench); it must name at least one of them.
Each host supplies `id`, absolute `runRoot`, and `executorTokenEnvironmentVariable`.
Each node supplies `slotKey`, `nodeId`, `host`, `role` (`regular` or `WA`), `catalogSlot`,
`backend`, and `control`. Use routable private addresses for different hosts. A regular node may also set
`"spare": true` to start as a hot spare (below); at least one regular node must not be a spare, and every spare's
`catalogSlot` must come after the serving regular nodes' slots (the world converter seeds slots 1 and 2, and a node
loads the seeded partitions of its slot at start).
The optional `nodeGarbageCollector` picks the .NET garbage collector of every node process (#1246):
`server` (the default) sets `DOTNET_gcServer=1`, `DOTNET_GCDynamicAdaptationMode=1` (DATAS: the heap count
follows the load, not one heap per core) and `DOTNET_GCNoAffinitize=1` (the nodes on a host do not pin their GC
threads to the same cores); `workstation` sets `DOTNET_gcServer=0`, the runtime default. Workstation GC runs a
blocking gen2 collection on one thread, which froze 4-9 GB nodes for 10-51 s under host CPU contention. The
Gateway process is not affected.

### Hung nodes

A node that stops making progress is declared Dead: its lease lapsed past the live-process hold
(`CLUSTER_LIVE_PROCESS_GRACE_SECONDS`), or it went 60 s without a simulation frame (15 min while recovery loads are
pending). A node with a running heartbeat thread learns this from the fence and exits by itself. A process frozen as a
whole can't, so if its host last named that incarnation as running, the registry plans a fenced Kill of it: Host stops
exactly that process, with no grace, and reports `Gone`. The event is `node.kill-requested` with `reason=dead`.

The kill counts as a spawn failure (`lastFailure` is `node_dead:<node>`), so the slot respawns after the usual backoff
(5 s, doubling to 600 s) and a slot that hangs on every start shows up in `spawnFailureAlert`. A process that has not
published its ready receipt has no epoch the host can name, so Host's ready timeout covers that one instead.

A hung game thread on a node that owns no partition never gets that far. At 15 s without a frame the registry closes
the node, its heartbeat thread drains to Empty, and with no game thread to act on that the exit watchdog ends the
process about 60 s later (exit 3). Before it does, the watchdog logs each thread's stack, the game thread first, to the
node's log if the game thread made no update for 15 s, so the line `exit watchdog: no game thread update for` is the
start of the evidence. Host's `hang-evidence.txt` only covers the registry's kill. The stacks come from the runtime's
createdump and take under a second; the capture has a limit of 10 s.

### World Authority memory

The World Authority checks its managed heap every 2 s (SE-1382). When the heap first passes `wa.heapDumpMb` (8 GB) it
writes one heap dump, `heap-<pid>-<MB>MB.dmp`, next to the crash dumps or in its data root, and logs
`heap watch: ... heap dump <path> written in <s> s`. The process stands still while the dump is written. Read it with
`dotnet-dump analyze` (`dumpheap -stat`, `gcroot`). Past `wa.heapLimitMb` (24 GB) it writes the dump if it has none yet and exits with code 6 and Host
restarts it, before it can take the host's memory and every node on the host with it. Its heap is 1.2-1.7 GB in a
100-trader soak.

### Hot spares

The approved `nodes` list is the whole inventory, so node ids cannot be allocated at runtime. Hot spares are
therefore regular slots of that list: `prepare` copies `spare` into `managed.json`, and every new generation
starts with exactly those slots designated spares, before any node registers. A spare registers, proves its
configuration, holds nothing and runs dormant (4 Hz) until it is needed.

- **A node dies holding partitions:** the registry promotes a live spare, on the dead node's host first, else on
  any host, and reserves the dead node's partitions for it (the T-0246 recovery path), so they reload on the spare
  instead of on the loaded survivors.
- **A fast restart counts as a death:** Host relaunches a crashed slot at once, and its new process can register
  before the old incarnation's lease ran out. That registration ends the old incarnation the same way: the spare is
  promoted and the old partitions' leases end at once.
- **Refill:** the dead node's slot becomes the spare. Host restarts it as usual; the new incarnation registers as a
  spare. The number of spares stays the same and no slot is added. When the promoted spare came from another host,
  the spare has moved to the dead node's host.
- A spare that dies comes back as a spare. A node that dies holding nothing is not replaced; it restarts serving.
- Replaying a generation (a Gateway restart) keeps the moved designation. A new generation (start after a clean
  Down, recovery, restore) starts from the approved designation again.
- A new generation with fewer slots (a lower spare count): the rows of the dropped slots are gone holders (NodeControl.md
  "Stopped nodes"). A live node of the same host serves their saves (it may read any `.mps` under the host's
  `runtime/nodes`), and the release pass after each retention pass removes a row once nothing in memory names it, so
  `/admin/v1/nodes` stops listing it. As in host-store mode the archive is not asked: an archived save that still names
  a released slot is served through a live node of that slot's host when it comes back. Rows of slots still in the
  inventory stay (#1210).

### Rotation (SE-1168)

The Gateway recycles the regular nodes of a managed deployment itself: a node process grows the longer it runs, and no
other component rotates a Quasar cluster (the regression harness's rotator works through node control, which a
managed deployment refuses). Every `rotation.lifetimeSeconds` / n, where n is the number of serving regular nodes,
the oldest serving node is recycled if it has served that long, so in steady state each node serves about one
lifetime. Both knobs are handover-config keys, live-tunable with `PUT /admin/v1/handover-config`:

| Key | Default | Meaning |
|---|---|---|
| `rotation.lifetimeSeconds` | 7200 | how long a node serves; 0 = never recycle |
| `rotation.drainTimeoutSeconds` | 600 | a recycle still draining after this closes the node |

One recycle runs at a time:

1. A live, proven spare is activated (on the node's host first), and the node starts retiring. The scale-in drain
   pins its components to the spare, clients last, as for a node-control recycle.
2. Once the node owns nothing it is closed and reports Empty, which turns its slot's incumbent action from Draining
   into Kill. Past the drain timeout it is closed anyway, and its own close drain hands out or offlines the rest.
3. Empty (or Dead) designates the slot a spare. Host kills that incarnation and reports Gone; the slot is still
   wanted, so Host restarts it, and the new incarnation registers as the spare holding nothing.

It waits while the cluster is not Serving, a recovery is pending, any node is retiring or closed, any slot has an
incumbent action, or no live, proven spare exists, and logs the reason once (`Rotation: ... waiting: ...`). The
counts never change. A Gateway restart keeps a recycle in flight (it is in the registry state), and the first
recycle after a start comes lifetime / n after it. A deployment without a spare is never rotated.

Release capability `managedHotSpares: 1` declares this. An older package ignores `spare`, so its spares would
serve; Quasar checks the capability before it writes a spare into a specification.

Node control (T-0320, NodeControl.md) is **read-only** on a managed deployment. `GET /admin/v1/node-control` and
its history work (records built from the slots; a host's serving target is its regular slots that are not
designated spares now, its spares target the ones that are); `PUT .../hosts/{host}`, `remove`, `recycle` and
`hosts-file` answer 409 `managed_inventory_fixed`. The admin node list (`/admin/v1/nodes`) carries `spare` per node.
`pluginConfigurations` maps plugin IDs to `configType` and a complete SDK `configuration`
envelope. Plugins with several configuration types use `configurations: [{configType,
configuration}, ...]`, producing canonical schema 2; single-type flat entries retain
schema 1. Mixed forms and duplicate configuration types are rejected.
One value is changed per host (SE-718): on a host with more than one node (regular, spare and World
Authority slots all count), a Performance plugin configuration with `havokThreadCountMode` `Auto` becomes
`Manual` with `havokThreadCount` set to the host's physical cores / its nodes - 1, kept between 2 and 4. Auto
would give every node a worker per physical core, and those spinning workers compete for the same cores.
`Manual` and `Game` from the operator are kept, and so is a single node on its host. Secrets are resolved only at launch and never embedded in generated files.

The generated `managed.json` binds the fleet and revision. Gateway reads it through
`CLUSTER_MANAGED_DEPLOYMENT`; Host supplies `CLUSTER_START_GENERATION`. First start seeds
NodePlan atomically. Replaying a generation preserves goals, backoff, pending kills and
shutdown progress. Another ordinary generation requires an empty Registry or proven clean Down.
For an unchanged managed revision/inventory, explicit `CLUSTER_START_RECOVERY=true`
with a new generation also permits recovery after dirty Down or a full fleet power loss.
The operator/Host must first prove every node stopped, fence and stop the Gateway,
then recheck every Host for in-flight launches. Recovery preserves saves and plugin
records/store identity, fences old node/ownership incarnations, aborts active handovers
and clears obsolete admin operations/executor observations. It never fabricates a clean
shutdown or wipes the Registry. Replaying that generation preserves a subsequent drain.
`CLUSTER_START_RECOVERY=update` is the same recovery start, but the new generation may also bring a
new revision and a changed slot inventory (#1296): a cluster whose every stop ends dirty, for example
a node that crashes in its drain, can then still take the build that fixes the crash. It needs a
previous managed deployment and the same stopped-fleet proof; plain `true` still refuses a changed
inventory, and an older Gateway refuses the value as an invalid flag. Release capability
`managedRecoveryUpdate: 1` declares it.
An ordinary Gateway process restart reuses its generation and never implies recovery.
Initial Serving waits for current-incarnation readiness from the required fleet.

`POST /executor/v1/heartbeat` accepts `ExecutorPollRequest` and returns an admin envelope
containing `ExecutorPollResult`. Authenticate with an Executor-scoped token whose name
is exactly the Host ID. Query/manage tokens do not grant executor authority. Tokens
configured on loopback are enforced too. Leases last 60 seconds, are boot-local and
fence competing sessions; sequence and request fingerprint protect lost-response replay.
Acquisition carries no observations. Reports are restricted to that host's plan;
positive incarnations must match Registry, and Gone must acknowledge the exact Kill.
A report without an epoch cannot change what a slot observes of a live incarnation; one
that repeats the slot's Spawning observation is accepted, since a node can register
between the Host reading its receipt and sending the report.
Registry flushes before acknowledging durable slot, kill or backoff changes. Unchanged
heartbeats only refresh volatile executor state and do not flush unrelated coalesced
world mutations. Managed mode refuses both legacy mutation routes
`POST /registry/executors/heartbeat` and `POST /registry/node-plan` with 409, even with
an admin token; only the fenced executor path may report observations.
Persistence failure closes admission, pauses relay and refuses control operations with
503 until restart/recovery. The Gateway remains alive: process liveness is not health.
A supervisor must detect the failed control health, correct the storage fault and restart
the Gateway with its recorded generation; explicit stopped-fleet recovery is required
if a fresh generation is needed after an unclean shutdown.

The runtime atomically publishes `CLUSTER_PROCESS_IDENTITY_PATH` after registration and
readiness. Its receipt binds cluster, slot, launch attempt, PID, node, Registry epoch,
deployment revision, readiness and failure. Right before a plugin-decided exit (finished
drain, empty World Authority, fence) the runtime writes the receipt once more with the exit
code, so the Host can report a clean exit (code 0) as `Gone` instead of `Failed`. `CLUSTER_NODE_EPOCH` remains a catalog slot,
never a Registry incarnation. Canonical SDK configuration is checked before readiness
and periodically on the game thread. Drift removes eligibility and asks Registry to
drain; Host executes the resulting fenced stop rather than inventing a restart policy.

Preparation and activation are separate. Mutable worlds/data are initialized once from
verified seeds, kept outside immutable artifacts/configs, and retained across retries
and binary changes. Per-host world copies use the existing runtime save transfer; a
shared filesystem is not required. Automatic cross-host distribution and complete
operator workflows are still integration work. These contracts are not evidence of
live multi-host acceptance.

Fixture checks: `python3 -m unittest discover -s Cli -p 'test_*.py'`, Gateway
`--self-test executor-contract`, and `--self-test managed-deployment`. No game processes
are needed for these checks.

### Runtime readiness, private data and restore

Managed runtime heartbeats carry `DeploymentRevision`, `ConfigurationVerified` and
`ConfigurationFailure`. Executor observations do not grant admission. Readiness proofs
are volatile: a Gateway restart waits for surviving runtimes to prove their configuration
again, without needing Host or Agent connectivity and without resetting drains or goals.
Missing initial proof prevents serving; drift closes and drains the exact incarnation.
Partition loading remains possible before final Serving, avoiding a bootstrap cycle.

The generated execution bundle declares the complete `requiredHosts` inventory and each
node's `configurationSeed`. Host copies pinned Magnetar configuration into the stopped
slot's writable `config` directory, preserving plugin-owned additional files. It replaces
or removes files previously supplied by the package. Plugin private data consequently
remains in the runtime tree across deployments and binary rollback.

An explicit Host restore writes `gateway/registry/managed-restore.json`, containing a new
`restoreId` and the prepared deployment `revision`. This is an offline restore authorization,
not a normal startup flag. Host must verify all restored file hashes and require fresh
runtime credentials before publishing it. The Registry consumes each restore ID once,
fences restored node/ownership incarnations, clears pre-restore operation/executor state,
and boots recovery against retained saves. Replaying the same marker cannot undo a later
shutdown. A different revision without clean Down still requires explicit restore authority.

## Shared data, backup and update compatibility

Release capability `pluginServices: 1` declares the provider documented in
[PluginServices.md](PluginServices.md). Release packaging records the PluginSdk the
plugins were compiled against: `pluginSdkContract`, its contract version, when it declares
one, and `pluginSdkSha256`, the hash of the file. Dependency preparation accepts a
Magnetar whose PluginSdk contract has the same major and an equal or higher minor. When
either side declares no contract, the frozen PluginSdk must match those bytes.

Bundles declare `storageFormats` for world, Registry and plugin records. Host activation
and restore require equal nonempty format maps. Format changes require an explicit
migration before activation; rollback never silently rolls data back.

Every managed Gateway gets the fixed `runtime/gateway/exports` directory as its export root,
which registry backups (`POST /admin/v1/registry-backups`) need on any number of Hosts.
Gateway world exports also use the shipped MagnetarWorld tool. A single-Host generator derives node data roots.
Multi-Host deployments must supply `exportDataRoots` that are actually readable by the
Gateway Host (for example shared storage). Native stopped snapshots instead capture
all Hosts independently and do not need shared filesystems.

Quasar's durable update checkpoints are Stopping, Activating, Starting and Complete.
Online preview validates a candidate without stopping processes. Activation still
requires clean shutdown and stopped Hosts. Completion requires exact deployment
revision, runtime readiness and matching Host attachment hashes. Interrupted phases
replay; previous installations remain available for compatible binary/config rollback.

### Admission and existing sessions

Loss of managed readiness closes admission to new clients. Existing sessions continue
relaying while their current World Authority generation and attachment binding remain
valid. An authority generation change still fences those bindings until re-admission;
an explicit Gateway pause or persistence failure still pauses relay for all sessions.

### Host launch contract

The preparation bundle is an execution template, not a directly launchable process
specification. For each attempt, Host injects `CLUSTER_DEPLOYMENT_REVISION` from the
verified prepared revision, `CLUSTER_NODE_ID`, `CLUSTER_NODE_ROLE`,
`CLUSTER_GATEWAY_REGISTRY`, `CLUSTER_SLOT_ID` and `CLUSTER_LAUNCH_ATTEMPT` from the
selected slot and attempt. Quasar implements this in `Quasar.Host/NodeActualizer.cs`.
These values must not be supplied independently by plugin configuration or copied from
another attempt. Missing/wrong revisions fail managed readiness.

A restarted Host with a new executor session may wait up to the previous 60-second
lease expiry. This fences the prior executor during partitions; restart does not
implicitly revoke its authority.

### Configuration failure and existing Registry migration

Equivalent numeric config values (for example `1` and `1.0`) compare equally. SDK-loaded
private/static configuration instances and multiple configuration types participate in
readiness. Actual divergent values still close the incarnation. Its slot remains
`Draining`, with the failure reason retained, instead of spawning identical replacements
indefinitely. Correct canonical configuration and deploy it through the stopped-fleet
workflow before explicitly resuming affected slots.

A nonempty unmanaged Registry cannot be adopted in place. For migration, cleanly stop
and back up the old fleet, export/reassemble a standalone world from verified copies
with the shipped world tools, then convert/import that world into a new managed
deployment with an empty Registry and fresh credentials. Retain the old Registry and
plugin stores for rollback and explicit plugin-specific migration; world conversion
does not translate their private/shared data. Quasar's direct reverse-conversion wizard
requires a managed source; use the release tools for an unmanaged source.

Restore requires freshly prepared credential environment references, whose names are
part of the hashed specification. Create new environment-variable names and fresh
secret values, prepare that specification on every Host, then restore. Preserve old
references through validation so Host can compare old and new values. This naturally
produces a new revision; arbitrary world/config changes are unnecessary. Retry using
the same restore operation and prepared revision.
