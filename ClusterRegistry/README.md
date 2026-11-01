# Cluster Gateway

Direct Transport gateway for multiple Space Engineers clients and clustered dedicated-server nodes.

```bash
dotnet run --project ClusterRegistry -- --steam 0.0.0.0:27016 --backend 127.0.0.1:27016
```

The gateway terminates the client transport at the frontend and speaks Direct Transport to the nodes. Clients retain independent routes and bounded handover queues, while one multiplexed Direct Transport link per node carries attach, detach, and relayed packet envelopes. Reliable node-link traffic is spread over 16 LiteNetLib channels by client ID. The backend leg is Direct Transport regardless of which client frontend is enabled.

Client frontends, at least one of which must be enabled:

- `--steam [host:port]` terminates the Steam network transport. Default `0.0.0.0:27016`.
- `--direct` terminates Direct Transport on `--listen`. Default `0.0.0.0:27116`. Development only: it admits every client, so it requires `CLUSTER_ALLOW_TEST_ACCEPT_ALL=true` and refuses a publicly reachable bind next to the Steam frontend (Security.md §3.6).
  `CLUSTER_ALLOW_TEST_ACCEPT_ALL` is a product guardrail, not a test hook: it is the deliberate, audited opt-in §3.6 requires before any accept-all frontend starts, and the Gateway refuses to start without it. It stays in the Gateway (T-0171) - whoever runs `--direct`, suite or human, sets it.

The two default to different ports so they can run side by side; `--eos [host:port]` logs in but terminates no clients, so it is not a frontend on its own and needs a port of its own.

Other defaults:

- `--backend 127.0.0.1:27016` - Direct Transport link to the node
- `--control 127.0.0.1:28016` - admin/handover control plane, off unless passed

Every option is also settable in the environment as `CLUSTER_<OPTION>` - `--lease-ttl-seconds` as `CLUSTER_LEASE_TTL_SECONDS`, `--direct` as `CLUSTER_DIRECT=true`, and so on. The command line wins over the environment, which wins over the default above; an empty value counts as unset, and a flag takes `true`/`1`/`yes` or `false`/`0`/`no` - anything else is refused rather than read as off. A value is validated identically whichever layer it came from, so `--direct` still demands `CLUSTER_ALLOW_TEST_ACCEPT_ALL=true` and still refuses a publicly reachable bind. `--help` lists the variables. The secrets and the cluster identity (`CLUSTER_ADMIN_TOKEN`, `CLUSTER_JOIN_TOKEN*`, `CLUSTER_INTERNAL_NETWORKS`, and the `--registry` admission set) have no flag and stay environment-only.

On Linux the gateway prints a warning at startup for each system limit below what a busy host needs: open files per process (65536), and per user processes and threads (16384), inotify instances (1024) and inotify watches (524288). The warning names the `limits.conf`, systemd or `sysctl` line that raises the limit; the gateway starts either way. `LinuxLimits.cs` is a copy of Pulsar's `Shared/LinuxLimits.cs`, which Magnetar runs on the nodes, and the `linux-limits` self-test section covers it.

Run the small parser check with:

```bash
dotnet run --project ClusterRegistry -- --self-test
```

Optional handover control:

```bash
dotnet run --project ClusterRegistry -- --control 127.0.0.1:28016 --backend 127.0.0.1:27016
curl 'http://127.0.0.1:28016/handover?source=127.0.0.1:28117&target=127.0.0.1:28118&backend=127.0.0.1:27018&partition=1'
curl -X POST 'http://127.0.0.1:28016/handover?source=127.0.0.1:28117&target=127.0.0.1:28118&backend=127.0.0.1:27018&partition=1&clientless=true'
```

With multiple connected clients, add `client=ID` to `/handover` and `/switch`; it remains optional for a single client.
With the registry enabled, `clientless=true` transfers a reported zero-client partition through the same fenced transaction without resolving or switching a client session.
Registry-fenced requests must use the configured source control, target control, and target backend. Automatic handover dispatches zero-client partitions through the clientless transaction, one-client partitions through that client's source session, and defers partitions with multiple clients.

Each server node must run with `CLUSTER_CONTROL_PORT` set. The MVP handover transfers the current saved entity set as one partition.

Optional durable registry/catalog:

```bash
dotnet run --project ClusterRegistry -- --control 127.0.0.1:28016 --registry ./gateway-state
curl -X POST 'http://127.0.0.1:28016/registry/nodes?node=node-1&endpoint=127.0.0.1:27016'
curl 'http://127.0.0.1:28016/registry/status'
curl 'http://127.0.0.1:28016/registry/nodes'
curl 'http://127.0.0.1:28016/registry/partitions'
curl -X POST 'http://127.0.0.1:28016/registry/checkpoint'
```

The mutation routes include `POST /registry/nodes`, `POST /registry/partitions`, `POST /registry/saves`, `POST /registry/voxel-bases`, and `POST /registry/voxel-journals`. Persistence saves use `partition`, `generation`, `sequence`, `kind`, `size`, `checksum`, `clusterTime`, and `location`; sequences are gap-free and ownership is generation-fenced. `GET /registry/nodes`, `GET /registry/partitions`, `GET /registry/voxel-bases`, and `GET /registry/voxel-journals` expose read-only lifecycle and catalog status. Query direct saves with `GET /registry/saves/newest`, or resolve split/merge ancestry with `GET /registry/saves/resolve?partition=ID`.

Lineage and snapshot manifests are JSON bodies posted to `POST /registry/lineage` and `POST /registry/snapshots`. `POST /registry/retention` marks saves outside keep-N, recent snapshot pins, and recursive live covers; files remain catalogued until their locations acknowledge through `POST /registry/deletions/ack`. Registry state remains fsynced to the checksummed framed WAL and periodically compacted by `POST /registry/checkpoint`.

Voxel entries currently catalogue WA-local native bases and the latest per-map journal watermark. Online and clean-shutdown snapshots record exact voxel cuts and become dirty when a current cut is unavailable. Voxel files are not yet replicated holders, portable recovery inputs, or retention candidates; base files and full journals are retained indefinitely until those mechanisms are implemented.

## Versioned admin contract

`ClusterRegistry.AdminContract` packages the implementation-free v1 DTOs
used by Gateway, Quasar, `Quasar.Host`, and the CLI. Initial read-only routes:

- `GET /admin/v1/health` — process and Registry availability.
- `GET /admin/v1/status` — one Registry-consistent lifecycle, node, executor,
  World Authority, and catalog-count snapshot.
- `GET /admin/v1/plan` — Registry-published node goals and executor observations.
- `GET /admin/v1/recovery-readiness` — newest reconstructible snapshot,
  current partition/global/voxel coverage, rollback exposure, replica-host floor,
  missing artifacts, and Registry snapshot/WAL durability.

Responses use camel-case JSON, string enums, `protocolVersion`/`capturedAt`
envelopes, and the `X-Cluster-Gateway-Protocol` header. Error envelopes have
stable `code` and `message` fields. A non-loopback control endpoint requires the
existing admin bearer token; query-only and executor-scoped credentials remain
later Phase 4 contract work.

Recovery readiness is `NotReconstructible` when any current artifact class is
missing, `AtRisk` when reconstruction is possible but no complete snapshot or
cross-host second copy exists, and `Ready` only when a complete cut and at least
two distinct-host copies of each required save class are present. The Gateway
uses catalogued, checksummed locations for this calculation; it does not require
the holder process to be live, so the report remains meaningful after a total
process crash. Voxel storage currently has one durable holder and therefore keeps
the result `AtRisk` until voxel artifact replication is implemented.

The thin CLI uses those same routes and emits Gateway JSON unchanged on stdout:

```bash
CLUSTER_GATEWAY_URL=http://127.0.0.1:28016 \
  dotnet run --project ClusterRegistry.Cli -- status
```

Use `recovery-readiness` as the command to retrieve the same recovery report.

Set `CLUSTER_ADMIN_TOKEN` for authenticated control endpoints. `--timeout`
sets the request deadline in seconds. Exit codes are deterministic: `0` success,
`2` usage, `3` authorization, `4` timeout/unavailable, `5` Gateway rejection,
and `6` invalid or incompatible protocol output. Diagnostics go to stderr.
