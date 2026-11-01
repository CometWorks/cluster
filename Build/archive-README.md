# Magnetar Cluster — deployment archive

A self-contained build of the Magnetar clustered Space Engineers dedicated server for Linux:
the Magnetar launcher (`magnetar/`), the cluster plugins and the Direct Transport plugin as
sources Magnetar compiles on first start (`plugins/`), the cluster Registry (`registry/`), the
MagnetarWorld world tool (`tools/MagnetarWorld/`), the cluster CLI (`cli/`) and the
documentation (`docs/`). Game binaries and game content are **not** included — you supply a
Space Engineers Dedicated Server installation and point the tools at it with environment
variables.

Every node is the stock `DedicatedServer64` started through `MagnetarInterim.bin`; the cluster
behaviour comes from two plugins, `cluster-node` (a regular node) and `cluster-wa` (the World
Authority), plus `direct-transport` for the Gateway's UDP transport. Magnetar's compatibility
plugins (dotnet-compat, linux-compat) are fetched from the MagnetarHub on first start, so the
host needs network access to GitHub once per node config directory.

## Requirements

Beyond a default Debian 13 installation:

- **.NET 10 SDK** (`dotnet`) — the runtime runs the launcher, Gateway and tool; the SDK is
  needed only if you build the tool yourself. Install per
  <https://learn.microsoft.com/dotnet/core/install/linux-debian>.
- **Python 3** (packaged `python3` is enough; the CLI uses only the standard library).
- **procps** (`ps`, `pgrep`) — present in the standard install; required by the CLI.
- A **Space Engineers Dedicated Server** installation (game binaries + content), e.g. from
  SteamCMD app `298740` (Windows depot; there is no native Linux DS), reachable on the local
  filesystem.
- **git**: Magnetar lists a dev folder's files with `git ls-files`; the packaged plugin folders
  are one-commit repositories so the listing is complete wherever the archive is unpacked.

Native libraries: `libsteam_api.so` and `Steamworks.NET.dll` ship inside the Magnetar tree
(from the CometWorks `linux-dependencies` release); linux-compat downloads the Havok,
RecastDetour, VRageNative and EOS Linux builds on first start. No X11, GPU, or sound stack is
required.

Network: the Gateway publishes the Steam transport on `0.0.0.0:27016/udp` and the Direct
Transport frontend on `27116/udp` by default; the control/registry plane and the node
backends stay on loopback.

Writable paths: the cluster run root (`--root`, default `CLUSTER_ROOT`; one of them is
required), which holds per node a Magnetar config dir (`<node>-magnetar`: plugin sources
registration, compile cache, Magnetar log) and a DS data dir (`<node>-data`:
`SpaceEngineers-Dedicated.cfg`, saves, game log);
`~/.config/SpaceEngineersDedicated/NativeWrapperCache` for linux-compat's wrappers.

## Quick start

```bash
tar -I zstd -xf magnetar-cluster-<version>.tar.zst
cd magnetar-cluster-<version>

# Point at your game install (contains DedicatedServer64/ and Content/):
export SPACE_ENGINEERS_SERVER=/path/to/SpaceEngineersDedicatedServer

# Run root for node data, logs and saves (or pass --root to every command):
export CLUSTER_ROOT=~/cluster-run

# Start a cluster (2 regular nodes + World Authority + Gateway) on the bundled example world:
cli/cluster fresh

cli/cluster status          # health; exit code 0 = healthy
cli/cluster diag --depth nodes
cli/cluster stop            # graceful shutdown with Final saves
cli/cluster start           # restart the SAME cluster (warm restart, the default)
```

`fresh` creates a new cluster in an empty run root. Over an existing one (its `gateway-registry`
or node data present) it refuses unless given `--wipe`, which ERASES that cluster - a scripting
convenience instead of deleting the files by hand, never a routine step.

The first start compiles the plugins (about a minute per node, in parallel) and downloads the
compatibility plugins; later starts reuse the per-node cache.

To run a different world, pass `--world /path/to/world` (a vanilla save or premade world; it is
split into partitions on first start) or set `CLUSTER_WORLD_TEMPLATE`. Game scenarios from your
install's `Content/CustomWorlds` work as world templates.

## Environment variables

The wrapper `cli/cluster` derives everything from `SPACE_ENGINEERS_SERVER` and the archive
layout. The underlying knobs (see `docs/EnvVars.md` for the Gateway and node variables):

| Variable | Meaning |
| --- | --- |
| `SPACE_ENGINEERS_BIN64` | game `DedicatedServer64` directory (passed to Magnetar as `-ds64`, hashed for node admission) |
| `MAGNETAR_HOME` | Magnetar install tree (default: the archive's `magnetar/`) |
| `CLUSTER_PLUGIN_SOURCE` | cluster plugin sources (default: the archive's `plugins/cluster`) |
| `DIRECT_TRANSPORT_SOURCE` | Direct Transport plugin sources (default: `plugins/direct-transport`) |
| `MAGNETAR_WORLD_TOOL` | the world tool dll (default: `tools/MagnetarWorld/MagnetarWorld.dll`) |
| `MAGNETAR_GATEWAY_DLL` | the Gateway dll (default: `registry/ClusterRegistry.dll`) |
| `CLUSTER_WORLD_TEMPLATE` | world template the CLI splits and serves |
| `CLUSTER_NODES` | regular node count (1..8, default 2) |
| `CLUSTER_DEBUG_PLUGINS` | `1` compiles the plugins with `DebugBuild` (symbols, no optimisation) |
| `CLUSTER_ADMIN_TOKEN` | bearer token for non-loopback control planes |

## Layout

```
magnetar/  Magnetar launcher (MagnetarInterim.bin, MagnetarConfig.bin) and its Libraries
plugins/   cluster (ClusterNode.xml, WorldAuthority.xml) and direct-transport sources
registry/  cluster Registry (registry, control API, Steam + Direct Transport frontends)
tools/     MagnetarWorld: split, verify and convert cluster worlds (server/ is an alias)
cli/       cluster CLI (cluster.py, magnetar_config.py) and the `cluster` wrapper
docs/      CLI and HTTP API documentation, environment variables, Magnetar operation
examples/  Cluster-Simple: a small two-partition test world
```

## Documentation

- `docs/ClusterCLI.md` — the `cluster` CLI: subcommands, options, health semantics.
- `docs/Magnetar.md` — how the plugins are loaded, per-node config dirs, launch line, logs.
- `docs/ClusterManager.md` — the Cluster Manager role: keeping a cluster up, stop/deploy/start.
- `docs/GatewayAPI.md` — Gateway control and registry HTTP API.
- `docs/NodeAPI.md` — per-node control HTTP API (operational subset).
- `docs/EnvVars.md` — every environment variable read by the server, Gateway and CLI.

## Running one node standalone (no cluster)

Without the `CLUSTER_*` variables the plugins stay inert and Magnetar runs a plain dedicated
server on the stock binaries:

```bash
mkdir -p /path/to/appdata /path/to/magnetar-config
python3 - <<'PY'
import sys; sys.path.insert(0, "cli")
from magnetar_config import write_config
write_config("/path/to/magnetar-config", role="node", cluster="plugins/cluster", transport="plugins/direct-transport")
PY
magnetar/MagnetarInterim.bin -config /path/to/magnetar-config -ds64 "$SPACE_ENGINEERS_SERVER/DedicatedServer64" \
    -profile cluster-node -multiInstance -noconsent -noconsole -ignorelastsession \
    -port 27016 -path /path/to/appdata
```
