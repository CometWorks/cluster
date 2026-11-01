# Space Engineers cluster — release package

The Linux build of the pieces that turn Magnetar-launched Space Engineers dedicated servers
into one clustered world: the cluster Registry (`registry/`), the compiled node and World
Authority plugins (`plugins/`), the MagnetarWorld world tool (`tools/MagnetarWorld/`), the
cluster CLI with the Gateway admin CLI (`cli/`) and the documentation (`docs/`). Released from
<https://github.com/CometWorks/cluster>.

Two things are **not** in the package and are supplied on the host:

- a **Space Engineers Dedicated Server** installation (game binaries and content), for
  example SteamCMD app `298740` (the Windows depot; there is no native Linux server);
- the **Magnetar** launcher, a release of <https://github.com/CometWorks/magnetar>
  (`MagnetarForLinux-*.7z`, unpacked anywhere).

Every node is the stock `DedicatedServer64` started through `MagnetarInterim.bin`; the cluster
behaviour comes from two plugins, `cluster-node` (a regular node) and `cluster-wa` (the World
Authority), plus `direct-transport` for the Gateway's UDP transport. The two cluster plugins
ship **precompiled** in `plugins/` and are installed by the CLI as Magnetar *local* plugins
into every node's config directory, so they are always the same build as the Gateway, tools
and CLI of this package (they are not published to the MagnetarHub). Direct Transport is
taken from the MagnetarHub pin (T-0157; the hub caught up on 2026-09-18, and the game clients get
the matching `ClientPlugin` half from StarCpt/PluginHub at the same commit,
`DIRECT_TRANSPORT_SOURCE=hub` selects it once it is updated). Magnetar's compatibility plugins
(dotnet-compat, linux-compat) are fetched from the MagnetarHub on first start, so the host needs
network access to GitHub once per node config directory.

Alternatively, set `DIRECT_TRANSPORT_BINARIES` to a prebuilt `DirectTransport/` bundle
and leave `DIRECT_TRANSPORT_SOURCE` unset. It must contain `DirectTransport.dll`,
`DirectTransport.xml` and `LiteNetLib.dll`; metadata must declare `direct-transport`, an
exact source commit and runtime/platform `CoreCLR`/`Linux`. The launcher installs it
locally on each node without transport source compilation or hub selection. A bad
bundle fails configuration; there is no fallback. Compatibility and other hub plugins
still need their own provisioning, so this option alone does not make startup offline.

For a complete pre-provisioned plugin set, `CLUSTER_COMMON_PLUGINS` points to verified
local bundles for both compatibility plugins and all common plugins, including resolved
native assets. With compiled transport and packaged role binaries, this disables
hub/source plugin resolution and shared GitHub caches, including during warm-up.
Leave extra-plugin and hub overrides unset. See `docs/ClusterCLI.md` for the contract.
`cli/deployment-capabilities.json` declares support for this mode to Quasar Host.

Cluster 1.1.0 requires **Magnetar 2.4.2.1 or newer** with managed configuration and
plugin services for every node, including unmanaged clusters. Release packaging and
launch/preparation paths verify PluginSdk metadata. `cli/deployment-capabilities.json`
records the PluginSdk the plugins were built against: `pluginSdkContract` (when that SDK
declares a contract version) and `pluginSdkSha256`. Quasar accepts any Magnetar whose
PluginSdk contract has the same major and an equal or higher minor. This requires Magnetar PR #57 and
a compatible release; older launchers cannot run these plugins. Magnetar must be fixed,
merged and released before the public cluster release. Pull-request review packages may
compile against the exact public Magnetar source commit in
`Build/pr-magnetar-sdk-revision.txt`; that is not a substitute for installing its matching
launcher. CI checks cross-repository protocol equality and all `Cli/test_*.py` tests even
when no release is built. Public and manually dispatched packaging still fetch the
published `v2.4.3.9` SDK and fail if it is unavailable or incompatible.

## Requirements

Beyond a default Debian 13 installation:

- **.NET 10 runtime** (`dotnet`): runs the Gateway, the world tool, the admin CLI and the
  Magnetar launcher. Install per <https://learn.microsoft.com/dotnet/core/install/linux-debian>.
- **Python 3** (the packaged `python3` is enough; the cluster CLI uses only the standard library).
- **procps** (`ps`, `pgrep`), present in the standard install; the cluster CLI needs it.

Native libraries: `libsteam_api.so`, `Steamworks.NET.dll` and the Linux EOS SDK ship in
`registry/` (from the CometWorks `linux-dependencies` release; their licence notices are in
`registry/LICENSES/`, with the release they came from); linux-compat downloads the
Havok, RecastDetour and VRageNative wrappers on first start. No X11, GPU or sound stack is
needed.

Network: the Gateway publishes the Steam transport on `0.0.0.0:27016/udp` and the Direct
Transport frontend on `27116/udp` by default; the control planes and the node backends stay
on loopback.

Writable paths: the cluster run root (`--root`, default `CLUSTER_ROOT`; one of them is
required), which holds per node a Magnetar config dir (`<node>-magnetar`: the local plugin
copy, hub cache, Magnetar log) and a dedicated server data dir (`<node>-data`:
`SpaceEngineers-Dedicated.cfg`, saves, game log);
`~/.config/SpaceEngineersDedicated/NativeWrapperCache` for linux-compat's wrappers;
`~/.steam/sdk64` when the Gateway installs a bundled `steamclient.so`.

## Quick start

```bash
tar -xzf ClusterForLinux-<version>.tar.gz
cd cluster-<version>

export SPACE_ENGINEERS_SERVER=/path/to/SpaceEngineersDedicatedServer   # holds DedicatedServer64/ and Content/
export MAGNETAR_HOME=/path/to/Magnetar                                 # holds MagnetarInterim.bin
export CLUSTER_ROOT=~/cluster-run                                      # run root; or pass --root each time

# Start a cluster (2 regular nodes + World Authority + Gateway) on the bundled example world:
cli/cluster fresh

cli/cluster status                               # health; exit code 0 = healthy
cli/cluster-status                               # per-node partitions, grids, players, load (docs/ClusterCLI.md)
cli/gateway-admin status                         # the same through the versioned admin API
cli/cluster stop                                 # graceful shutdown with Final saves
cli/cluster start                                # restart the SAME cluster (warm restart, the default)
```

`fresh` creates a new cluster in an empty run root. Over an existing one (its `gateway-registry`
or node data present) it refuses unless given `--wipe`, which ERASES that cluster - a scripting
convenience instead of deleting the files by hand, never a routine step.

`cli/cluster` finds Magnetar under `~/.config/Magnetar` when
`MAGNETAR_HOME` is unset, and takes the cluster plugins from the package's `plugins/`.

The first start downloads the compatibility plugins and Direct Transport from the hub;
later starts reuse the per-node cache. The cluster plugins are never compiled on the host.
Developers can point `CLUSTER_PLUGIN_SOURCE` at a cluster checkout to have Magnetar compile it
as dev folders instead.

To run a different world, pass `--world /path/to/world` (a vanilla save or premade world; it is
split into partitions on first start) or set `CLUSTER_WORLD_TEMPLATE`. Game scenarios from
your install's `Content/CustomWorlds` work as world templates.

## Environment variables

The wrapper `cli/cluster` derives everything from `SPACE_ENGINEERS_SERVER` and the package
layout. The underlying knobs (`docs/EnvVars.md` has the Gateway and node variables):

| Variable | Meaning |
| --- | --- |
| `SPACE_ENGINEERS_BIN64` | game `DedicatedServer64` directory (passed to Magnetar as `-ds64`, hashed for node admission, game assemblies of the Gateway and the world tool) |
| `MAGNETAR_HOME` | Magnetar install tree holding `MagnetarInterim.bin` |
| `CLUSTER_PLUGIN_BINARIES` | compiled cluster plugins, a folder holding `ClusterNode/` and `WorldAuthority/` (default: the package's `plugins/`) |
| `CLUSTER_PLUGIN_SOURCE` | developer override: a cluster checkout (`ClusterNode.xml`, `WorldAuthority.xml`) Magnetar compiles as dev folders instead of the binaries |
| `DIRECT_TRANSPORT_SOURCE` | unset (the default) takes Direct Transport from the MagnetarHub pin; a path registers that checkout (`DirectTransportServer.xml`) as a dev folder instead — the dev override |
| `DIRECT_TRANSPORT_BINARIES` | prebuilt, pinned `DirectTransport/` bundle; mutually exclusive with `DIRECT_TRANSPORT_SOURCE`, including `hub` |
| `CLUSTER_COMMON_PLUGINS` | verified common/compatibility bundles and native assets; enables frozen profiles with no hub/dev sources |
| `MAGNETAR_GATEWAY_DLL` | the Gateway dll (default: the package's `registry/ClusterRegistry.dll`) |
| `MAGNETAR_WORLD_TOOL` | the world tool dll (default: `tools/MagnetarWorld/MagnetarWorld.dll`) |
| `CLUSTER_WORLD_TEMPLATE` | world template the CLI splits and serves |
| `CLUSTER_NODES` | regular node count (1..8, default 2) |
| `CLUSTER_DEBUG_PLUGINS` | `1` compiles dev-folder plugins with `DebugBuild` (symbols, no optimization); no effect on the packaged binaries |
| `CLUSTER_ADMIN_TOKEN` | bearer token for non-loopback control planes |
| `CLUSTER_GATEWAY_URL` | control listener `cli/gateway-admin` talks to (default `http://127.0.0.1:29416`) |

## Layout

```
registry/   cluster Registry (registry, admin API, Steam + Direct Transport frontends)
gateway-rs/ the Gateway's Rust data plane (Direct and Steam clients, node links), used only with
            `--relay external` (Docs: RelayIpc.md); its libsteam_api.so sits beside it
plugins/    ClusterNode/ and WorldAuthority/: the compiled node and World Authority plugins (dll, manifest xml, dependencies)
tools/      MagnetarWorld: split, verify and convert cluster worlds
cli/        cluster CLI (cluster, cluster.py, magnetar_config.py) and the admin CLI (gateway-admin, ClusterRegistry.Cli/)
docs/       running a cluster, the REST APIs, the CLI tools, environment variables
examples/   Cluster-Simple: a small two-partition test world
manifest.json   version and the exact commit this package was built from
```

## Documentation

- `docs/ClusterCLI.md`: the `cluster` CLI: subcommands, options, health semantics, the world tool.
- `docs/Magnetar.md`: how the plugins are loaded, per-node config dirs, launch line, logs.
- `docs/ClusterManager.md`: keeping a cluster up: stop, deploy, start, reading a node that did not come up.
- `docs/AdminAPI.md`: the versioned Gateway admin REST API (`/admin/v1`) and the `gateway-admin` CLI.
- `docs/GatewayAPI.md`: the Gateway control and registry HTTP API.
- `docs/NodeAPI.md`: the control HTTP API every node plugin serves.
- `docs/Registry.md`: the Gateway's command line options and transports.
- `docs/AdminContract.md`: the admin contract package other tools build against.
- `docs/EnvVars.md`: every environment variable read by the Gateway, the nodes and the CLI.
- `docs/NodeControl.md`: node control: per-host serving/spare counts, the hosts file, node ids, states, remove/recycle, history.
- `docs/NodeScaling.md`: how a node drains (graceful and forced) and how a new node takes load.

## Running the tools directly

The cluster CLI is the one front door, but each binary also runs on its own:

```bash
export SPACE_ENGINEERS_BIN64=$SPACE_ENGINEERS_SERVER/DedicatedServer64

# Gateway (docs/Registry.md lists every option; each is also readable as CLUSTER_<OPTION>)
dotnet registry/ClusterRegistry.dll --steam 0.0.0.0:27016 --backend 127.0.0.1:28417 \
    --control 127.0.0.1:29416 --registry ~/cluster-run/gateway-registry

# World tool (docs/ClusterCLI.md): vanilla world -> cluster form, and back
dotnet tools/MagnetarWorld/MagnetarWorld.dll to-split /path/to/vanilla-world /path/to/split-world
dotnet tools/MagnetarWorld/MagnetarWorld.dll to-vanilla /path/to/split-world /path/to/vanilla-world

# Admin CLI (docs/AdminAPI.md)
cli/gateway-admin health
cli/gateway-admin save-all --sweep
```

## F11 client diagnostics

`client/ClusterDiagnostics` is the optional .NET 10 Pulsar client plugin. Install its
`plugin.dll` and `plugin.xml` in Pulsar's `Interim/Local/ClusterDiagnostics` folder. The
cluster plugins own the server feed. See `docs/ClientDiagnostics.md` for the Manager
publication command and the shared Quasar contracts.
