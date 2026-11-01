# Manual deployment and development

Use this guide for development, testing or an independently operated cluster. For normal
deployment, use [Quasar automatic provisioning](../README.md#recommended-deployment-quasar).
The commands here operate a cluster directly; use Quasar's controls for a Quasar-managed
deployment. Run checkout commands from the repository root unless stated otherwise.

## Prerequisites

- [Space Engineers Dedicated Server](https://store.steampowered.com/app/244850/Space_Engineers/)
  (SteamCMD app 298740) — the stock `DedicatedServer64` the plugins run on and build against
- [Magnetar](https://github.com/CometWorks/magnetar) built and deployed into `~/.config/Magnetar`
  (`Directory.Build.props.user` can point elsewhere). For release builds, use the SDK pin in
  [Build/fetch-magnetar.sh](../Build/fetch-magnetar.sh) and the
  [release workflow](../.github/workflows/release.yml); launch and preparation paths check
  the actual PluginSdk contract. See [SDK compatibility](Versioning.md#magnetar-pluginsdk)
- [Direct Transport](https://github.com/CometWorks/direct-transport) — from the MagnetarHub pin by
  default; a checkout beside this repository is only needed for `DIRECT_TRANSPORT_SOURCE`, the dev override
- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), Python 3.12
- Linux natives for the Gateway build (Steamworks.NET, `libsteam_api.so`, the Linux EOS SDK):
  `Build/fetch-linux-dependencies.sh` downloads them from the latest
  [linux-dependencies](https://github.com/CometWorks/linux-dependencies/releases) release

For a full release package, also install GCC, tar, Git, `sha256sum` and the Rust
toolchain pinned in [GatewayRs/rust-toolchain.toml](../GatewayRs/rust-toolchain.toml).
`Build/release.sh` builds the Rust data plane unless `GATEWAY_RS_DIR` supplies a staged build.

## Build

```sh
dotnet build ClusterNodePlugin/ClusterNodePlugin.csproj -c Release -p:RunPostBuildEvent=Never
dotnet build WorldAuthorityPlugin/WorldAuthorityPlugin.csproj -c Release -p:RunPostBuildEvent=Never
dotnet build Tools/MagnetarWorld/MagnetarWorld.csproj -c Release
dotnet build ClusterRegistry/ClusterRegistry.csproj -c Release
```

The msbuild build is the compile check (Krafs publicizer, same publicized assemblies Magnetar
uses). In development Magnetar compiles the plugins from the dev folders at server start; a
release (`Build/release.sh`) ships this same msbuild output as precompiled Magnetar *local*
plugins (`plugins/ClusterNode`, `plugins/WorldAuthority`) that the CLI installs per node.
Without `-p:RunPostBuildEvent=Never` a build also deploys the DLL into Magnetar's `Local`
folder of the host config, which is not how the cluster loads them.

## Register with Magnetar

```sh
Deploy/magnetar-register.py            # ~/.config/Magnetar/Magnetar: sources + profiles
```

registers this repository twice (once per manifest) as dev folders, enables `direct-transport`
from the MagnetarHub pin (`--direct-transport /path/to/checkout` for a dev folder instead) and
writes the `cluster-node`, `cluster-wa` and `Current` profiles. `--binaries <archive>/plugins`
installs a release's compiled plugins as local plugins instead of the dev folders. The cluster
CLI writes the same registration into a per-node config dir at every start.
`--direct-transport-binaries /path/to/DirectTransport` installs a compiled, pinned
transport bundle instead of using a checkout or the hub. The cluster CLI accepts the
same bundle through `DIRECT_TRANSPORT_BINARIES`; do not also set `DIRECT_TRANSPORT_SOURCE`.

## Run a cluster

`fresh` creates a new world; use a new run root for an initial test. See the
[CLI reference](Gateway/ClusterCLI.md) before operating an existing world.

```sh
python3 Cli/cluster.py --root ~/tmp/cluster-run fresh      # Gateway + 2 nodes + World Authority
python3 Cli/cluster.py --root ~/tmp/cluster-run status
python3 Cli/cluster.py --root ~/tmp/cluster-run stop
```

From a checkout the CLI uses `~/.config/Magnetar`, this repository as dev folders, the MagnetarHub's
pinned `direct-transport` (`DIRECT_TRANSPORT_SOURCE=<checkout>` registers a dev folder instead) and
`dotnet run` of the
`ClusterRegistry` project in this repository; from a release package (`Build/release.sh`, then
`cli/cluster` inside the unpacked package) the compiled plugins, Gateway, tool and CLI all come
from the package. The regression suite drives the same CLI unchanged.

For prebuilt dependency deployments, `DIRECT_TRANSPORT_BINARIES` must contain
`DirectTransport.dll`, `DirectTransport.xml` and `LiteNetLib.dll`. Its manifest declares
`direct-transport`, an exact source commit, `CoreCLR` and `Linux`. The CLI copies this
bundle into each node's local plugins and removes the managed transport source entry.
This freezes transport only; compatibility/common plugins and native assets still need
deployment pinning. It does not establish offline readiness for the whole cluster.

`CLUSTER_COMMON_PLUGINS` supplies that frozen common set: precompiled bundles including
compatibility plugins and their resolved native assets. With packaged roles and compiled
transport, the CLI disables hub/dev source resolution, shared GitHub caches and warm-up
updates. Compatibility selection stays implicit in Magnetar. The caller verifies the
snapshot before use; Quasar Host can prepare those copies without starting a cluster.
The release includes `cli/deployment-capabilities.json` so Host can detect this support.

Offline launcher checks (no server processes):
`python3 -m unittest discover -s Cli -p 'test_packaged_plugins.py' -v`.

The scenario suite in `Test/` deploys clusters from a release package and asserts on them; start
with `cd Test && uv run python doctor.py` and see the [scenario suite instructions](../Test/README.md).

## Release

`Version.Build.props` holds the one version number (`<Version>`), shared by the plugin
assemblies, the Gateway and the release package. The GitHub workflow
(`.github/workflows/release.yml`) runs `Build/release.sh` and publishes
`ClusterForLinux-<version>.tar.gz`:

- a same-repository pull request marked ready for review builds a **draft** release (`v<version>-pr.<n>`) when
  its version differs from the base branch, so the package can be tried before the merge;
- a push to `main` publishes the public **latest** release when the version is newer than the
  latest release;
- an unchanged version skips release packaging; a manual dispatch always builds (draft by default).
  Protocol/CLI verification and Rust checks still run on ordinary pushes and pull requests.

So a release is cut by bumping `<Version>` in a pull request. `Build/release.sh` runs locally
too (`Build/dist/`); it needs a `DedicatedServer64` (auto-detected from Steam, or `DEDICATED64`),
`Build/fetch-linux-dependencies.sh` run once, the pinned Rust toolchain (or `GATEWAY_RS_DIR`),
and a Magnetar install for the plugins'
`PluginSdk.dll` reference (the host's, or `Build/fetch-magnetar.sh` + `MAGNETAR_SDK` on a host
without one, which is what the workflow does).

## Remarks

### Folder path overrides

`Directory.Build.props` declares the overridable folder paths with empty defaults (`Bin64`,
`Dedicated64`, `Magnetar`, `MagnetarData`) and detects the usual install locations. It imports
`Directory.Build.props.user` from the repository root when present, which is **not committed**,
so each contributor keeps their own local overrides.

### Accessing internal, protected and private members in game code

Game assembly publicization is already configured in the projects; there is no need to
uncomment template publicizer setup. Both compilers publicize the game assemblies listed in
[GameAssembliesToPublicize.cs](../Shared/Tools/GameAssembliesToPublicize.cs)
(the msbuild build through the `<Publicize>` items of the project files, Magnetar from the
`IgnoresAccessChecksTo` attributes).

### Game update compatibility

Use `EnsureCode` attributes to guard patches against changes to game method bodies.
Set `CLUSTER_PATCH_HASHES=1` in a test node's environment to log the actual runtime patch
target hashes before applying patches, then declare the verified hashes. A mismatch
refuses patch loading; do not guess hashes or assume the check is bypassed on Linux.
See [PatchHelpers](../Shared/Patches/PatchHelpers.cs) and
[EnsureCode](../Shared/Tools/EnsureCode.cs) for the validation behavior.

### Debugging

Use a Debug build for breakpoints and variable inspection. Attach the debugger to the
correct running process and make sure its loaded assembly and symbols match the source.
Code inside `#if DEBUG` is excluded from Release builds. Distribute Release builds.
For transpiler diagnostics, see [TranspilerHelpers](../Shared/Tools/TranspilerHelpers.cs)
for the Debug-only IL recording helpers.

## Further documentation

- [Magnetar integration](Magnetar.md): early startup, per-node configuration, admission and the world tool.
- [Managed deployments and executors](Gateway/ManagedDeployment.md): Quasar's preparation and executor contract.
- [Release packages and bench archives](Gateway/DeploymentArchive.md): contents, exclusions and packaging.
- [Packaged release README](../Build/release-README.md) and [bench archive README](../Build/archive-README.md).
- [Versioning](Versioning.md): shared version, source-build assembly literals and SDK compatibility.
- [Historical Cluster Manager role](ClusterManager.md): the older AI-assisted bench workflow
  and machine-specific paths. This is not required for Quasar provisioning or a manual installation.
