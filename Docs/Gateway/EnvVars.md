# Environment variable reference

Every variable read by the node plugins, the Gateway, and the cluster CLI.

## Game install (required at runtime)

| Variable | Meaning |
| --- | --- |
| `SPACE_ENGINEERS_BIN64` | game `DedicatedServer64` directory: source of the Windows native DLLs the Linux wrappers load (`Havok.dll`, `RecastDetour.dll`, `VRage.Native.dll`) and of game-owned managed assemblies the archive does not ship (`HavokWrapper`, `EOSSDK`, `Microsoft.CodeAnalysis`, `SharpDX.*`, `ProtoBuf.Net`, ...). Without it the server falls back to `<four levels above the server dll>/Bin64` (the repo layout). |
| `SPACE_ENGINEERS_CONTENT` | game `Content` directory. Without it the server walks upward looking for the content anchor. |
| `SPACE_ENGINEERS_CLIENT_CONTENT` | client `Content` for worlds that need client-only assets; defaults to a path derived from `SPACE_ENGINEERS_CONTENT`. |
| `SPACE_ENGINEERS_PREMADE_WORLD` | premade checkpoint a node loads (sets `PremadeCheckpointPath`). |
| `SPACE_ENGINEERS_ROOT` | game install root; used by the MagnetarWorld tool. |
| `SPACE_ENGINEERS_SERVER` | wrapper-only shortcut: derives `SPACE_ENGINEERS_BIN64` and `SPACE_ENGINEERS_CONTENT`. |

## Node identity & cluster membership (set by the CLI per node)

| Variable | Meaning |
| --- | --- |
| `SE_DIRECT_TRANSPORT` | `1` enables the Direct Transport server transport |
| `CLUSTER_NODE_ID` / `CLUSTER_NODE_EPOCH` / `CLUSTER_NODE_ROLE` | node name, epoch, `Regular` or `WA` |
| `CLUSTER_NODE_HOST` | the host name the node reports at registration (T-0320 node control matches nodes to hosts-file hosts by it); set by the launcher to the hosts-file host, else the machine name. In the CLI's own environment it overrides this host's name (default: the short hostname) |
| `CLUSTER_NODE_ENDPOINT` | backend endpoint the Gateway relays to |
| `CLUSTER_NODE_CONTROL_ENDPOINT` | advertised control endpoint, `http://<address>:<port>`: the endpoint the node registers and hands out as a save holder (T-0321; unset = `http://<bind or 127.0.0.1>:<port>`) |
| `CLUSTER_NODE_READY_TIMEOUT` | startup readiness deadline |
| `CLUSTER_CONTROL_PORT` / `CLUSTER_CONTROL_BIND` | control server bind (default `127.0.0.1`); a LAN bind listens on `127.0.0.1:<port>` as well (T-0321, see NodeAPI.md "Authentication") |
| `CLUSTER_NODE_HOST` | the host name the hosts file gives this node's host (default the machine name); reported at registration and used for the diag `transport` counters |
| `CLUSTER_HOSTD_INTERVAL` / `CLUSTER_HOSTD_WARMUP_SECONDS` | cluster-hostd (T-0321): executor poll interval (2 s); how long the first node on a host starts alone before the rest follow (300 s) |
| `CLUSTER_PREWARM_SOURCES` | hub snapshot (sources.xml + Hubs/) seeded into a new node on a host without a warm node (bin/server-sync's `~/cluster-server/prewarm/Sources`) |
| `CLUSTER_GATEWAY_REGISTRY` | Gateway registry base URL |
| `CLUSTER_ID` / `CLUSTER_WORLD_ID` / `CLUSTER_CONFIG_HASH` | cluster/world identity fencing |
| `CLUSTER_JOIN_TOKEN` | node join token |
| `CLUSTER_JOIN_HOLD` | seconds a client join is held (character home recovering, route busy) before it is refused; default 120 (T-0298), 0 = refuse at once; sent to gateway-rs as `Config.joinHoldSeconds` |
| `CLUSTER_WORLD` | split world directory |
| `CLUSTER_DATA_ROOT` | node data root (saves, journals) |
| `CLUSTER_SHARED_ROOT` | plugin shared storage root, the same directory on every node and the WA (PluginSdk `PluginStorage`, T-0256); `cluster.py` sets `<root>/shared`, a managed deployment sets it from `sharedStorageRoot` |
| `CLUSTER_SAVE_FOLDER` | folder under `Saves` a premade world loads into and the mods' world storage lives in; `managed_deployment.py` sets the world id (SE-788). Unset: vanilla, the cfg's `WorldName`. The session's name stays `WorldName` either way. |
| `DOTNET_gcServer` / `DOTNET_GCDynamicAdaptationMode` / `DOTNET_GCNoAffinitize` | node .NET GC (#1246); a managed deployment sets Server GC with DATAS, unpinned, unless `nodeGarbageCollector` is `workstation` (ManagedDeployment.md); `cluster.py` passes the operator's `DOTNET_*` through |

## Node behavior knobs

| Variable | Meaning |
| --- | --- |
| `CLUSTER_MAX_PARTITIONS` / `CLUSTER_MAX_MEMBERS` | partitioning limits |
| `CLUSTER_PROJECTILE_DISTANCE_CAP` | projectile cross-partition cap |
| `CLUSTER_CONCEALMENT` / `CLUSTER_CONCEAL_GRACE_SECONDS` / `CLUSTER_CONCEAL_PREWAKE_METERS` | concealment policy |
| `CLUSTER_STRICT_PROMOTION` | strict promotion checks |
| `CLUSTER_IMPORT_CHECK` | verify imports semantically |
| `CLUSTER_RPC_TRACE` | verbose RPC tracing |
| `CLUSTER_WARM_RECOVERY` | warm-restart recovery path |
| `CLUSTER_MPS_PATHS` | extra partition-save search paths |
| `CLUSTER_MAX_ROLLBACK_SECONDS` | ceiling on the per-partition periodic-save interval: the adaptive backoff may stretch saves up to this and never past it (default 60) — the maximum-rollback bound of `Docs/StateDurability.md` |
| `CLUSTER_MOD_PLACEMENT` | T-0242: the admin's per-mod placement file (`<mod id> <verdict>` per line; default `<data>/Mods/cluster-mod-placement.txt`); Docs/ModCompat.md |
| `CLUSTER_GLOBAL_JOURNAL` | #1318, World Authority: `0` = answer banking, faction, identity, player-binding and time-of-day commands at once without journaling them in the registry, and replay nothing after a restore (before #1318); default on (Docs/Gateway/StateDurability.md) |
| `CLUSTER_ADMIN_TOKEN` | bearer token; mandatory for non-loopback control binds |
| `CLUSTER_INTERNAL_NETWORKS` | CIDRs treated as cluster-internal |

## Gateway

Every CLI flag `--x` is also readable as env `CLUSTER_X` (flag wins):
`LISTEN`, `BACKEND`, `CONTROL`, `PAUSED`, `SOURCE_CONTROL`, `TARGET_CONTROL`,
`TARGET_BACKEND`, `REGISTRY`, `LEASE_TTL_SECONDS`, `DIRECT`, `STEAM`, `EOS`, `ADMISSION`,
`COST_MODEL`, `WORLD`, `EXPORT_ROOT`, `EXPORT_DATA_ROOTS` (the last three enable
`/admin/v1/world-exports`, together with `MAGNETAR_WORLD_TOOL` pointing at
`MagnetarWorld.dll` and the game env vars that tool needs; `EXPORT_ROOT` alone enables
`/admin/v1/registry-backups`).

Environment-only:

| Variable | Meaning |
| --- | --- |
| `CLUSTER_ADMIN_TOKEN` | control-plane bearer token (manage scope) |
| `CLUSTER_ADMIN_TOKENS` | path to the scoped admin token file (named query/manage tokens, SHA-256 at rest, re-read on change) — see `Docs/AdminAPI.md` |
| `CLUSTER_JOIN_TOKEN_CURRENT` / `CLUSTER_JOIN_TOKEN_PREVIOUS` | join token rotation pair |
| `CLUSTER_START_ID` | T-0321: opaque id of the current `cluster start`, kept for a gateway-only restart. With it the gateway persists the start's join/admin tokens and a restart keeps accepting/presenting them, so running nodes are not rejected (and do not exit). Unset = nothing persisted |
| `CLUSTER_START_CREDENTIALS` | where those tokens are kept (mode 0600); default `<registry>/../secrets/start-credentials.json`, outside the backed-up registry directory |
| `CLUSTER_GATEWAY_HOST` | T-0321: the gateway host's hosts-file name. Nodes whose registered host differs are remote: they may not advertise loopback endpoints (refused at registration), and the network-window lease hold applies to them |
| `CLUSTER_NETWORK_WINDOWS` | T-0321: `HH:MM-HH:MM[,...]` gateway-local time (may cross midnight). Inside a window and its grace a remote node's lapsed lease is held (not Dead, partitions kept, balancing paused) and so is its host's executor lease. Unset = off |
| `CLUSTER_LIVE_PROCESS_GRACE_SECONDS` | #1218: how long after its last renewal a node's lapsed lease is held while its host's executor (Quasar.Host, cluster-hostd) reports the node's process Ready, fresh within 20 s: not Dead, partitions kept. A frozen process (a blocking GC under CPU contention) survives; a crash still dies at its lease, because the next poll reports it Gone. Default 60 (the simulation-stall kill); `0` = off |
| `CLUSTER_CLIENT_SILENCE_SECONDS` / `CLUSTER_CLIENT_LOADING_SILENCE_SECONDS` | SE-1450: how long a Steam client may send nothing before the Gateway ends its session (Steam P2P has no disconnect detection of its own): 180 s once it is world-ready (CLIENT_READY), 600 s before that, because a vanilla client is silent while it downloads mods and loads the world |
| `CLUSTER_NETWORK_WINDOW_GRACE_SECONDS` | hold continues this long after a window ends (default 30) |
| `CLUSTER_FAULT_SILENCE_HOSTS` | T-0321 fault injection: path of a file with one host name per line (re-read each second); requests from nodes of those hosts (their `X-Cluster-Host`) and executor polls for them are aborted without an answer |
| `CLUSTER_INTERNAL_NETWORKS` | CIDRs allowed for non-loopback `--direct` beside `--steam` |
| `CLUSTER_ALLOW_TEST_ACCEPT_ALL` | the Security.md 3.6 guardrail: the explicit opt-in an accept-all frontend (`--direct`) requires, without which the Gateway refuses to start |
| `CLUSTER_BINARY_VERSION` / `CLUSTER_REPLICATION_TABLE_HASH` | join-compatibility fencing |
| `CLUSTER_WORLD` | the Gateway's copy of the split world. Its `Sandbox.sbc` settings and mod list, hashed at Gateway start, fence node admission (`Shared/Cluster/WorldSettingsHash.cs`) |
| `CLUSTER_SYNC_DISTANCE` | advertised sync distance |
| `CLUSTER_DIRECT_TRANSPORT_VERSION` | DT wire version override |
| `CLUSTER_HANDOVER_VALIDATION_ABORTS` | test hook: injected validation aborts |
| `CLUSTER_HANDOVER_BLOCK_SWEEP_SECONDS` | SE-1185: how often the Gateway drops the handover blocks of partitions with no owner and no recovery running (default 30). A merge, split or archive that retires a blocked partition clears its block at once; the sweep catches the rest, such as a partition released by its node |
| `CLUSTER_ROUTE_OVER_REPORT_GRACE_SECONDS` | how long a registry client route may outvote a node report that says the partition has no clients (default 120). Covers the switch-settling window; past it the node report wins, so a stale route can no longer pin a partition against every handover for the life of the cluster. Applies to both the automatic-handover scheduler and the clientless-handover guard. |
| `CLUSTER_PERSIST_INTERVAL_SECONDS` | how long registry mutations may coalesce in memory before the changed rows are committed to `registry.db` (default 60; `0` = write-through, used by durability self-tests) — see `Docs/Gateway/StateDurability.md` |
| `CLUSTER_STORE_AUDIT_EVERY` | audit the registry store every N writes (default 1; `0` = off): stored rows compared with the live state, drift logged and repaired |
| `CLUSTER_STORE_AUDIT_BUDGET` | rows per table the rotating store audit reads each time (default 50; live partition rows are always included) |
| `CLUSTER_REPLICA_AUDIT_SECONDS` | how often the incrementally kept replica work queue (saves with fewer than two usable holders, best first) is compared with a full ranking of the catalogue (default 300); drift is logged as `WARNING: replica queue audit` and the full ranking adopted |
| `CLUSTER_VANILLA_AUTOSAVE` | T-0345: `1` keeps the vanilla world autosave on cluster roles. Default: disabled, nothing reads `Saves/<world>` back |
| `CLUSTER_SAVE_FSYNC` | T-0345: `all` fsyncs every partition save file a node writes or fetches. Default: only Final and HandoverTee saves and the World Authority's global saves are fsynced; Periodic saves and fetched replica/evacuation/recovery copies are written tmp + rename without fsync (a host power loss can tear up to the filesystem's commit interval of them; the checksum catches it and recovery falls back) |
| `CLUSTER_HOST_STORE` | T-0340: `1` in the GATEWAY's environment turns the per-host save store on; the registry is the single authority and a node's own environment is not read for it. Gateway: store mode is entered at load, or when a start applies the hosts file (the first start of a run root), when node control is active and `<runRoot>/saves/<gateway host>` exists. A catalogued location is re-keyed from its node holder to the `host:<name>` holder at `<runRoot>/saves/<host>/...` only when the registry sees the file there with the catalogued size; the rest stays node-held at its old path, served as before, and is reported (`WARNING: host store: N location(s) are not in the store`, status `hostStore.unmoved`) until a later load finds the file. Each node offers store mode when it registers and the registration answer (`HostStore`) says whether it uses it: yes only to a node that reports the host the plan runs it on (`CLUSTER_NODE_HOST`); a refusal is a `WARNING: host store:` line in the gateway log and the node logs `host save store OFF`. Registry status carries `hostStore` = `{ enabled, hosts: { <name>: { state, saves } }, unmoved, legacyNodes }`. Default off: nothing changes. Not for a live cluster until the host agent's file move exists. |
| `CLUSTER_REPLICA_BYTES_PER_SECOND` | T-0323: byte budget for ALL partition save copies in the cluster (default 1048576 = 1 MiB/s, ~8.4 Mb/s, ~50 average 21 KB saves a second, bursts of 10 s of budget). Nodes take copies for as long as the registry hands them out; this is the only pace. With it spent no node is handed a partition save (global saves are not paced); the queue's rank decides who gets the refill. The registry warns (`replica capacity below invalidation rate`, `replicaAlarm` in `/diag` registry status, cluster-status) when the queue would outlast the mean interval between the last recycles (30 min without two) or has not shrunk in 30 min |
| `CLUSTER_REPLICA_EVACUATE_BYTES_PER_SECOND` | T-0323: byte budget for replica copies that only move a save off stopped (gone) holders, served through a proxy node on their host (default 524288 = 512 KiB/s, ~4.2 Mb/s, bursts of 10 s of budget). These copies rank after every other replica copy; see NodeControl.md, "Stopped nodes" |
| `CLUSTER_RETENTION_GLOBAL_KEEP` | SE-1346: how many of the newest global saves retention keeps (default 10; `0` keeps them all, as before). Snapshot, shutdown and backup-hold references are kept too, and nothing at or above the newest global save with its full copy target is deleted; see StateDurability.md, "Global save retention" |
| `CLUSTER_RETENTION_TRIM_BATCH` | SE-1347: how many copies of retained partition saves above the copy target one retention pass may hand out for deletion (default 1000; `0` turns trimming off); see StateDurability.md, "Copies above the copy target" |
| `CLUSTER_ARCHIVE_BATCH` | how many retired partitions one retention pass (once a minute) may move to the `ArchivedSaves` table (default 5000); see StateDurability.md, "Archived saves of retired partitions" |
| `CLUSTER_ARCHIVE_FILE_BATCH` | T-0400 archive retention: how many archived rows (and due stamps) one retention pass may read, and about how many files may be outstanding for deletion (default 250: p50 2.7 / p90 5.6 ms under the lock on the w33 registry); the age is the handover-config knob `retention.archiveFileDays`; see StateDurability.md, "Archive retention" |
| `CLUSTER_COST_REPLAY` | cost-model replay input |
| `CLUSTER_DRAIN_TIMEOUT_SECONDS` | node control (T-0320, NodeControl.md): graceful drain budget of a remove/recycle/decrease before the stop is forced (default 300); a request's `timeoutSeconds` / `--drain-timeout` overrides it per stop |
| `CLUSTER_SCALE_IN_MAX_THREAD_SHARE` | the share of one game thread of measured partition work per remaining serving node above which a decrease answers with a projected-load warning (default 0.7). Since T-0320 it only warns; a decrease is never refused |

## Managed lifecycle

| Variable | Meaning |
| --- | --- |
| `CLUSTER_MANAGED_DEPLOYMENT` | Verified managed inventory manifest; required when reopening a managed Registry. |
| `CLUSTER_START_GENERATION` | Host-authorized UUID; replay preserves lifecycle, drains and pending kills. |
| `CLUSTER_START_RECOVERY` | Explicit `true` permits a new generation for the unchanged inventory after all nodes and Gateway have been verifiably stopped. `update` is the same recovery start, but it may also change the revision and the slots (an operator's update despite a dirty shutdown, #1296; capability `managedRecoveryUpdate: 1`). Default `false`; never infer this from process restart. See [ManagedDeployment.md](ManagedDeployment.md). |

## Cluster CLI

| Variable | Meaning |
| --- | --- |
| `CLUSTER_ADMIN_TOKEN` | bearer sent to the configured Gateway control endpoint, including loopback; not sent to node endpoints |
| `CLUSTER_ROOT` / `CLUSTER_WORLD_TEMPLATE` / `CLUSTER_LISTEN` / `CLUSTER_NODES` / `CLUSTER_NAME` / `CLUSTER_ADMINS` | defaults for the matching CLI flags |
| `CLUSTER_STEAM_LISTEN` | Steam frontend bind (empty disables) |
| `MAGNETAR_HOME` / `CLUSTER_PLUGIN_BINARIES` | Magnetar install tree and the compiled node / WA plugins installed as local plugins (set by the package's `cli/cluster` wrapper) |
| `CLUSTER_PLUGIN_SOURCE` | developer override: a cluster checkout Magnetar compiles as dev folders instead of the packaged binaries |
| `DIRECT_TRANSPORT_SOURCE` | unset (the default) loads the MagnetarHub's pinned `direct-transport`; a path registers that checkout as a dev folder — the dev override |
| `MAGNETAR_GATEWAY_DLL` / `MAGNETAR_WORLD_TOOL` | packaged binary locations (otherwise built from the checkout) |
| `CLUSTER_RELAY` | `external` (default since 2026-09-24) runs the Gateway with `--relay external` and starts gateway-rs beside it under a restart loop (T-0229, RelayIpc.md); `kill`/`stop` end both. `internal` keeps the packet path in the C# Gateway (the reference and fallback, to be removed later) |
| `MAGNETAR_GATEWAY_RS` | the gateway-rs binary (default: the package's `gateway-rs/gateway-rs` beside `MAGNETAR_GATEWAY_DLL`, else the checkout's `GatewayRs/target/release/gateway-rs` from `Build/build-gateway-rs.sh`) |
| `CLUSTER_RELAY_WORKERS` | gateway-rs front workers (default: cores - 2, 1..8) |
| `GATEWAY_RS_FLUSH_US` | gateway-rs: how often a LiteNetLib endpoint with pending traffic is flushed, in µs (default 500). Packets queued within the interval share datagrams; an endpoint idle for the interval flushes at once. 0 flushes on every wake-up (lowest latency, about 8x the datagrams and 40 % more CPU in the relay bench) |
| `CLUSTER_PORT_BASE` | offset added to every cluster port (default 0) |
| `CLUSTER_SPLIT_STEP` | MagnetarWorld step that splits the template (default `to-split`) |
| `CLUSTER_GATEWAY_ARGS` | extra Gateway arguments, shell-split |
| `CLUSTER_EXTRA_PLUGINS` | further precompiled local plugin folders, `[node:\|wa:]PATH`, comma-separated (the test utility plugin) |
| `CLUSTER_LEASE_TTL_SECONDS` | registry lease TTL passed to the Gateway (default 15) |
| `CLUSTER_HOSTS_FILE` | the hosts file `start`/`fresh` apply (same as `--hosts`); default `<root>/hosts.json`, else (on a non-gateway host of a multi-host start) `<root>/hostd/hosts.json`, else one built from `--nodes/--spares` for this host (NodeControl.md; version 2 = multi-host, T-0321) |
| `CLUSTER_SPARES` | default for `--spares` |
| `CLUSTER_NODE_CONTROL` | `0` keeps the positional start (node-1..N, no hosts file, no node control); default `1`. The scenario bench sets it per scenario (`node_control = True` opts in) |
| `CLUSTER_NODE_DEDICATED_TEMPLATE` | a `SpaceEngineers-Dedicated.cfg` every newly spawned node's data dir is seeded from (verbatim except the node's `DedicatedId`); node ids are fresh at every start and every refill, so a deployment's own cfg must reach each new dir. Unset: the minimal built-in cfg |
| `CLUSTER_NODE_DEDICATED_REQUIRE` | `Element=value,...` every node cfg must carry before the node may launch (e.g. `ExperimentalMode=true`) |
| `CLUSTER_SELF_EXIT_SECONDS` | launcher: how long a node the registry marked Kill may take to exit by itself before it is SIGKILLed (default 30) |

## Build-time only

| Variable | Meaning |
| --- | --- |
| `LinuxDependencies` (MSBuild property) | Steam pair and Linux EOS SDK the Gateway links against, as laid out by `Build/fetch-linux-dependencies.sh` (default: its destination) |
| `Dedicated64` (MSBuild property) | `DedicatedServer64` folder the Gateway and the world tool build against (default: auto-detected from Steam) |
| `MagnetarPackaging` (MSBuild property) | `-p:MagnetarPackaging=true` keeps the game's `EOSSDK.dll` out of the Gateway output for the packages |
| `LINUX_DEPENDENCIES` / `DEDICATED64` | `Build/release.sh` passes them as the two properties above |

## Diagnostics

`ERROR_REPORTING_DIRECTORY` selects a private per-run directory for atomic schema-v1 incident JSON.
`ERROR_REPORTING_CONTEXT` is a JSON dictionary of correlation strings supplied by Quasar.Host:
installation, Host, run, deployment, cluster, node and role. Registry fallback capture uses
`Diagnostics` below its executable and generates a run ID. Capture is local and does not upload.

Registry handover exceptions and failed/aborted/timed-out/recovery transitions emit bounded
reports containing transfer ID, partition and phase. These preserve existing semantic events,
not world snapshots. Node plugins route all logging through PluginSdk, including startup logs;
caught update/disposal failures mark the node Failed and emit a semantic `node_failed` record.
The required ErrorReporting plugin captures Error/Critical records from that stream.

The local Registry spool is limited to 32 reports/minute and 256 files. Capture failures never
change handover behavior. Host remains responsible for crash exit evidence and native dumps.

The native `gateway-rs` restart loop is supervised by `Cli/gateway_supervisor.py`. Every child
restart receives a sibling directory under the Host's `runs/` directory, with exact PID,
Linux boot ID/start ticks, executable, UTC process start/end and `native: true` in `run.json`.
Without a Host it uses `<cluster-root>/diagnostics/runs/`. Its `process.log` rotates once at
8 MiB; the combined current/previous logs stay bounded at 16 MiB. Unexpected exits create an
`unexpected_exit` incident and retain the existing one-second restart delay. CLI stop/kill sends
SIGTERM to the supervisor first, allowing it to persist `expectedExit` before terminating the
child process group; SIGKILL remains the fallback. Direct SIGKILL of the supervisor cannot run
cleanup, so Host recovery must inspect the retained child identity.

`ERROR_REPORTING_POLICY_FILE` names the Host's persisted short-lived consent lease. Native core
limits are enabled only at child launch when diagnostic and dump flags are true, installation
identity matches and both the lease and encryption-key expiry are current. A wrapper started
before its first Quasar lease may lack an inherited installation ID; a later valid lease then
supplies that ID for the next native child. An explicitly supplied mismatching ID still rejects
the lease. Missing, malformed, expired or mismatched leases leave the child's core soft limit zero. Run context records
`dumpsGrantedAtStart` and `dumpConsentGeneration`; Host must require these and current dump
consent before importing or forwarding an OS core.

These are launch-time settings: already-running processes do not reconfigure core limits on
lease expiry. Linux piped `core_pattern` collectors may ignore RLIMIT_CORE; OS core generation,
retention and access remain separately administered. Native capture is therefore best effort.
The supervisor never runs `coredumpctl` or forwards dumps; the Host performs exact-run import
and enforces sharing consent. No game process is needed to run its focused tests:

```sh
python3 -m unittest discover -s Cli -p test_relay.py -v
```
