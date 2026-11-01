# Running the cluster under Magnetar

Every cluster process is the stock `DedicatedServer64` started through the Magnetar launcher
(`MagnetarInterim.bin`, .NET 10). The cluster behaviour comes from three plugins:

| Plugin id | Manifest | Source directories | Role | Released as |
|---|---|---|---|---|
| `cluster-node` | `ClusterNode.xml` | `ClusterNodePlugin`, `Shared` | regular node | compiled, `plugins/ClusterNode/` of the release package (Magnetar **local** plugin) |
| `cluster-wa` | `WorldAuthority.xml` | `WorldAuthorityPlugin`, `Shared` | World Authority | compiled, `plugins/WorldAuthority/` (local plugin) |
| `direct-transport` | `DirectTransportServer.xml` (CometWorks/direct-transport) | `ServerPlugin`, `Shared` | UDP transport the Gateway attaches to | the MagnetarHub pin (`<GitHub>` entry), T-0157; `DIRECT_TRANSPORT_SOURCE=<checkout>` registers a dev folder instead |

For a pinned, precompiled transport, use `DIRECT_TRANSPORT_BINARIES` in the CLI or
`--direct-transport-binaries` in `Deploy/magnetar-register.py`. The input is a
`DirectTransport/` folder with `DirectTransport.dll`, `DirectTransport.xml` and
`LiteNetLib.dll`; the manifest pins its source commit and targets `CoreCLR`/`Linux`.
This selects `direct-transport` through the same local-plugin path as the packaged
node/WA plugins, unregisters the managed dev source and omits transport from the hub
profile. It is mutually exclusive with `DIRECT_TRANSPORT_SOURCE`. The other hub and
compatibility plugins are unaffected; their artifacts/assets still need deployment pins.

The cluster CLI's `CLUSTER_COMMON_PLUGINS` mode supplies those bundles locally, with
resolved assets and source/hub loading disabled. Local entries retain their original
IDs, allowing Magnetar's existing core-plugin lookup to select compatibility plugins
without enabling them explicitly in the profile. Other common plugins are enabled
locally. This requires a verified complete snapshot; see `Gateway/ClusterCLI.md`.

Both cluster manifests declare `direct-transport` as a dependency, `CoreCLR` as the runtime and the
same NuGet references the msbuild projects use. Magnetar publicizes the game assemblies named by
`[assembly: IgnoresAccessChecksTo]` in `Shared/Tools/GameAssembliesToPublicize.cs`.

The cluster plugins are never published to the hub: a release ships them compiled next to the
Gateway, the world tool and the CLI so all four are always the same commit, and the CLI installs
them as local plugins (`<config>/Local/<Name>/<Name>.dll` + `<Name>.xml`, enabled through the
profile's `<Local>` list). During development Magnetar compiles them from this checkout instead
("dev folders", `<DevFolder>` in the profile).

## Registration

`Deploy/magnetar-register.py` writes the registration into a Magnetar config dir (default
`~/.config/Magnetar/Magnetar`, the `Magnetar` folder next to the launcher):

* `Sources/sources.xml` — the MagnetarHub remote hub Direct Transport and the compatibility
  plugins come from; in dev-folder mode also the `LocalPlugin` entries (this repository twice,
  once per manifest, and `--direct-transport` when a checkout is given).
* `Local/ClusterNode/`, `Local/WorldAuthority/` — with `--binaries <archive>/plugins`, the
  compiled plugins copied in.
* `Profiles/cluster-node.xml`, `Profiles/cluster-wa.xml` — `direct-transport` (hub, or a dev folder under the override) plus one
  cluster plugin (local or dev folder).
* `Profiles/Current.xml` — everything; the plugins decide by `CLUSTER_NODE_ROLE` which one drives
  the process (`WA` selects the World Authority, anything else the regular node) and the other
  stays idle.

`DebugBuild` is `false` (optimised) unless `--debug` (dev folders only).

The same writer (`Cli/magnetar_config.py`, `write_config(config, role=..., binaries=... | cluster=...,
transport=None)`) is what the cluster CLI uses for the per-node config dirs: the archive's
compiled `plugins/` in a release, the checkout (`CLUSTER_PLUGIN_SOURCE`) in development.

### Magnetar keys dev folders by folder

`PluginList.devFolderPlugins` is a dictionary keyed by the dev folder's path, so registering one
repository under two manifests keeps only the second entry. The shared registration works around
it by registering the World Authority manifest with a trailing slash (`/home/factory/ws/se/se1/cluster/`),
which Magnetar treats as a distinct path with its own compile cache. The per-node config dirs the
CLI writes carry one cluster plugin each and need no workaround.

Proposed Magnetar change (not applied; `~/ws/se/se1/magnetar` is outside this repository):

```diff
--- a/Pulsar/Shared/PluginList.cs
+++ b/Pulsar/Shared/PluginList.cs
@@ private void AddLocalPlugin(LocalPluginConfig source)
         string folder = Path.GetFullPath(source.Folder);
         LocalFolderPlugin local = new(folder, source.File) { Source = "DevFolder" };
         LoadPluginData(local);
-        devFolderPlugins[folder] = local;
+        // One folder may hold several plugins (one manifest each): key by manifest, not folder.
+        devFolderPlugins[Path.Combine(folder, source.File ?? string.Empty)] = local;
```

`LocalFolderPlugin.GetCacheDirectory()` already includes the plugin id in the cache name, so two
manifests of one folder do not share a compile cache.

### Dedicated server auto-detection

Without `-ds64`, Magnetar looks for `libraryfolders.vdf` under `~/.steam/steam/steamapps` and
reports `SpaceEngineersDedicated.exe not found` when the file is absent, although the documented
fallback path exists. The CLI always passes `-ds64` (from `SPACE_ENGINEERS_BIN64`).

## Startup order: why the plugins have a preloader hook

Magnetar registers Pulsar's plugin loader as a game plugin, and the dedicated server calls
`IPlugin.Init` from `MySandboxGame.Run` — after `Initialize()`, which for a dedicated server
includes loading the dedicated config, binding the network and loading the session. The cluster
runtime (`ClusterRuntime.StartFromEnvironment()`) has to start before all of that, and the cluster patches on those startup paths (premade world settings,
session load, network) have to be in place before they run.

Pulsar's preloader offers the hook: a class named `Preloader` (no namespace) in a plugin assembly
with a public static `Finish()` method is called by the launcher after every Cecil preloader patch
is applied and the game assemblies are resolvable, but before the dedicated server's `Main`.
`ClusterNodePlugin/Preloader.cs` and `WorldAuthorityPlugin/Preloader.cs` call
`ClusterPluginHost.Preload`, which:

1. claims the process role (`CLUSTER_NODE_ROLE`); the other plugin's hook returns,
2. applies every Harmony patch of the plugin assembly,
3. patches a prefix onto `VRage.Dedicated.DedicatedServer.Run` that starts the cluster runtime
   after `MyVRageWindows.Init` and `InitMultithreading`, before the dedicated config, the world
   and the network.

`IPlugin.Init` then only loads the plugin config. Under a launcher without the preloader hook the
plugin patches and starts from `Init`, logs a warning, and startup-path patches are inactive for
that run.

Log lines before the game log exists go to the console (`[ClusterNode] Info: ...`), which the CLI
captures in `<node>.log`; everything after goes to the game log as well.

## Node admission hash

The Gateway's `CLUSTER_REPLICATION_TABLE_HASH` identifies the replication type table. Stock
binaries build that table from the game assemblies, so the node hashes those: SHA-256 over the bytes of `Sandbox.Common.dll`,
`Sandbox.Game.dll`, `SpaceEngineers.Game.dll`, `SpaceEngineers.ObjectBuilders.dll`, `VRage.dll`,
`VRage.Game.dll`, `VRage.Library.dll` in that order, read from the DedicatedServer64 directory
(`ClusterPluginHost.ReplicationTableHash`). `Cli/cluster.py` computes the same digest from
`SPACE_ENGINEERS_BIN64` for the Gateway. A game update changes both.

## Launch line

The CLI starts a node as

```
MagnetarInterim.bin -config <root>/<node>-magnetar -ds64 <DedicatedServer64> -profile cluster-node|cluster-wa
    -multiInstance -noconsent -noconsole -ignorelastsession -port <backend port> -path <root>/<node>-data
```

with the `CLUSTER_*` environment (see `Docs/Gateway/EnvVars.md`),
`SE_DIRECT_TRANSPORT=1`, and the working directory set to the config dir (Magnetar keeps its
NuGet cache relative to it). The stock server takes the premade world from `SpaceEngineers-Dedicated.cfg`, which the CLI seeds into an empty data dir
(`PremadeCheckpointPath` = the split world, `LoadWorld` empty, the node's port and the
administrators). A bench that writes its own config keeps it. The node plugin keeps the premade
checkpoint's session settings (`ClusterKeepPremadeSessionSettings`), so the per-node config's
session settings do not matter in a cluster.

Per node the run root holds:

* `<node>-magnetar/` — `Sources/`, `Profiles/`, `DevFolder/<id>-<hash>/` (compile cache),
  `Preloader/` (patched game assemblies), `info.log` (Magnetar's log, rotated per launch).
* `<node>-data/` — `SpaceEngineers-Dedicated.cfg`, `Saves/`, `SpaceEngineersDedicated_*.log`.
* `<node>.log` — stdout/stderr of the launch (the "Game ready" marker, the plugin's console lines,
  the exit code line).

The config dir is rewritten at every start (sources and profile) but its caches survive, so a
relaunch of unchanged sources does not recompile. `fresh` wipes the data dirs, the split world and
the registry, not the config dirs.

## Player commands

On a node where Essentials is loaded, the node plugin replaces `!ess grids list` and adds `!grids list`, the name players know from Torch's Essentials. Both do the same, for any player: they list the player's grids on every node and in offline partitions, without loading any. A list that can't reach everything says so and leaves the markers of grids it couldn't see. The markers and positions follow `MarkerShowPosition` and `UtilityShowPosition` in `Essentials.cfg`. Without Essentials, neither command exists.

## World tool

`Tools/MagnetarWorld` builds against the stock DedicatedServer64 assemblies and resolves them at
run time from `SPACE_ENGINEERS_BIN64` (`SPACE_ENGINEERS_ROOT` names the install whose `Content`
supplies the subtype names). Two things the stock assemblies do not provide outside the game are
done in the tool itself: the protobuf type model is created explicitly (the stock platform makes
it only in its full, Windows-only `Init`), and `CustomRootWriter.Init` is Harmony-patched to write
`xsi:type` as a prefixed attribute (the .NET Core `XmlWriter` rejects the qualified local name).

## Deployment archive

`Build/package.sh` stages `magnetar/` (the install tree without its config dir), `plugins/cluster`
and `plugins/direct-transport` (git archives of HEAD, each turned into a one-commit repository:
Magnetar lists a dev folder with `git ls-files --cached --others --exclude-standard` and gives up
on an empty list, which is what an archive unpacked inside a repository's ignored `Build/out`
produces), `registry/`, `tools/MagnetarWorld/`
(`server/` is a symlink to it, for the regression harness' `MAGNETAR_WORLD_TOOL`), `cli/`, `docs/`
and `examples/Cluster-Simple`, verifies no game binary is staged and writes
`magnetar-cluster-<git describe>.tar.zst` into `Build/out`. That is the bench archive: it
carries everything a node host needs except the game.

## Release package

`Build/release.sh` builds what the GitHub release ships (`ClusterForLinux-<version>.tar.gz`):
`registry/`, `plugins/` (the compiled `ClusterNode` and `WorldAuthority` local plugins),
`tools/MagnetarWorld/`, `cli/` (the cluster CLI plus `gateway-admin`, the admin CLI over
`/admin/v1`), `docs/` and `examples/Cluster-Simple` — without Magnetar, without Direct
Transport (the hub supplies it) and without game binaries (the package's `README.md` says how
the host supplies the rest). The version is `<Version>` of `Version.Build.props`; the release
workflow (`.github/workflows/release.yml`) builds and publishes a release only when that
version changes. `Build/fetch-linux-dependencies.sh` downloads the Steam pair and the Linux
EOS SDK the Gateway build links against, from the latest `linux-dependencies` release. See
`Docs/Gateway/DeploymentArchive.md`.
