# Deployment packages

Two packages come out of this repository. Neither carries game binaries or game content: the
host supplies a Space Engineers Dedicated Server installation and names it through
`SPACE_ENGINEERS_SERVER` (or `SPACE_ENGINEERS_BIN64`).

| | Release package | Bench archive |
| --- | --- | --- |
| Script | `Build/release.sh` | `Build/package.sh` |
| Name | `ClusterForLinux-<version>.tar.gz` (`cluster-<version>/` inside) | `magnetar-cluster-<git describe>.tar.zst` |
| Version | `<Version>` of `Version.Build.props` | `git describe` of the checkout |
| Published | GitHub release by `.github/workflows/release.yml` | local `Build/out/`, pinned by the regression harness |
| Magnetar | no: the host's own install (`MAGNETAR_HOME`) | yes, `magnetar/` |
| Cluster plugins | compiled, `plugins/ClusterNode`, `plugins/WorldAuthority` (Magnetar local plugins) | sources, `plugins/cluster` as a one-commit repository (dev folder) |
| Direct Transport | the MagnetarHub pin, recorded in `manifest.json` as the commit it resolves to (`DIRECT_TRANSPORT_SOURCE=<checkout>` for a dev folder instead) | sources, `plugins/direct-transport` (dev folder) |
| README | `Build/release-README.md` | `Build/archive-README.md` |

Both carry `registry/`, `tools/MagnetarWorld/`, `cli/`, `docs/`, `examples/Cluster-Simple` and
a `manifest.json` with the exact commit, and both are driven by the same `cli/cluster` wrapper,
which prefers a packaged Magnetar and the packaged plugins (binaries, else sources) when they
are there and the host's copies otherwise.

## Release package

Made for a host that already has Magnetar and fetches plugins on its own (Quasar downloads
the Magnetar release the same way it will download this one: the latest GitHub release,
asset by name pattern). It holds what the cluster needs beyond a plain Magnetar server:

| Path | Source | Notes |
| --- | --- | --- |
| `registry/` | `ClusterRegistry` Release build, `-p:MagnetarPackaging=true` | Steam + Direct Transport frontends, registry, admin API; ships `Steamworks.NET.dll`, `libsteam_api.so`, `libsteam_api64.so` (the shim built from `ClusterRegistry/Native/steam_api64_shim.c`) and the Linux EOS SDK under both names the binding imports |
| `plugins/ClusterNode/`, `plugins/WorldAuthority/` | `ClusterNodePlugin`, `WorldAuthorityPlugin` Release builds | the node and World Authority plugins as Magnetar **local** plugins: `<Name>.dll`, `<Name>.xml` (the repository manifest with `<Commit>` filled in) and the NuGet dependencies (LZ4, zstd, System.IO.Hashing, LiteNetLib); Harmony and Cecil come with Magnetar. LiteNetLib is shipped locally because type discovery can request it before Direct Transport initializes. Matching assembly identities share the default load context. The CLI copies the role's folder into each node's `<config>/Local/` and enables it in the profile's `<Local>` list, so every node runs exactly the build the package was made from. Never published to the hub |
| `tools/MagnetarWorld/` | `Tools/MagnetarWorld` Release build | splits, verifies and converts worlds; game assemblies resolved at runtime from `SPACE_ENGINEERS_BIN64` |
| `cli/cluster`, `cli/cluster.py`, `cli/magnetar_config.py` | `Cli/` | the cluster CLI: start, stop, status, diag, triggers |
| `cli/gateway-admin`, `cli/ClusterRegistry.Cli/` | `Cli/gateway-admin`, `ClusterRegistry.Cli` Release build | the admin CLI over `/admin/v1` |
| `docs/` | `Docs/Gateway/{ClusterCLI,AdminAPI,GatewayAPI,NodeAPI,EnvVars}.md`, `Docs/{Magnetar,ClusterManager}.md`, `ClusterRegistry/README.md` as `Registry.md`, `ClusterRegistry.AdminContract/README.md` as `AdminContract.md` | running a cluster, every REST API, the CLI tools, the variables |
| `examples/Cluster-Simple` | `Test/Saves/Cluster-Simple` | our test world, not game content |
| `README.md`, `LICENSE`, `manifest.json` | `Build/release-README.md`, repository root, generated | |

Before archiving, `release.sh` runs `Test/PackagedPluginCheck` against each staged
plugin in a separate process. It uses `Assembly.LoadFrom` and `GetTypes`, matching
Magnetar's local-plugin discovery, with only the game and Magnetar SDK available as
platform dependencies. A missing runtime dependency therefore fails packaging even
when the Gateway self-tests pass. This does not start a game server.

Excluded by design:

- **Game binaries.** The Gateway references the game's `EOSSDK.dll` and the world tool the
  VRage/Sandbox assemblies, all from `DedicatedServer64`; `MagnetarPackaging` marks the EOS
  reference `Private=false` and the tool's references never copy, so neither reaches the
  output. `release.sh` asserts none of them is staged. Both resolve the assemblies at runtime
  from `SPACE_ENGINEERS_BIN64` (`ClusterRegistry/Program.cs`, `Tools/MagnetarWorld/Program.cs`).
- **Magnetar and Direct Transport.** The host operator (or Quasar) installs Magnetar; the
  `cli/cluster` wrapper refuses to start without it and says which variable to set. Direct
  Transport comes from the hub pin, the same commit the game clients load: the MagnetarHub
  publishes its `ServerPlugin` half for the nodes and the WA, StarCpt/PluginHub the `ClientPlugin`
  half for the clients, and `bin/release-package` refuses to build unless the two pins are the same
  commit — it records that commit in `manifest.json`, so the archive names the transport build it
  was released with (T-0157). `DIRECT_TRANSPORT_SOURCE=<checkout>` is the dev override. Note that a
  hub snapshot is kept for a year (`MaxSourceAge`), so a newly published pin only reaches the
  cluster through `cluster prewarm --refresh` (nodes) and `bin/pulsar-prewarm --refresh` (clients);
  `bin/version-consistency` fails the deploy if what a side loaded is not what the archive names.
  Only the two cluster plugins ship in the package, compiled.
- **Symbols** (`*.pdb`) and Python bytecode.

Redistributables that ship on purpose: Steamworks.NET and `libsteam_api.so` (Valve) and
`libEOSSDK-Linux-Shipping.so` (Epic), all from the latest `linux-dependencies` release at
build time, with their notices and that release's tag under `registry/LICENSES/`; LiteNetLib,
Harmony, LZ4, zstd and
System.IO.Hashing from NuGet.

## The release workflow

`Version.Build.props` is the single source of the version. `.github/workflows/release.yml`:

1. **Verify.** Every run, including draft/fork PRs and unchanged versions, checks all
   `Cli/test_*.py` tests and byte equality of the Gateway protocol against Magnetar's SDK.
   PRs use the full source commit in `Build/pr-magnetar-sdk-revision.txt`; main/manual
   runs use the required published release tag.
2. **Gate.** Reads `<Version>`. On a pull request (ready for review, not from a fork) it reads
   the base branch's version through the API and builds only when the two differ; on a push
   to `main` it builds only when the version is strictly newer than the latest release (the
   first release ever counts as newer); a manual dispatch always builds. A draft pull request
   produces no package until it is marked ready.
3. **Build.** Installs the .NET 10 SDK, downloads the dedicated server's library set with
   steamcmd (anonymous, cached under the depot's public build id, `Content` dropped), runs
   `Build/fetch-linux-dependencies.sh` and `Build/fetch-magnetar.sh`. Public/manual builds
   require the published Magnetar `v2.4.3.9` release. PR review builds explicitly compile
   `PluginSdk.dll` from the pinned source checkout with `--source`; this does not ship a
   launcher or bypass the release package's SDK compatibility check. Neither SDK enters
   the package. Then runs `Build/release.sh dist` with `DEDICATED64`, `LINUX_DEPENDENCIES`
   and `MAGNETAR_SDK` pointing at those. The script builds, stages, verifies (no game
   binary, every required file, `py_compile`, both `--self-test`s) and writes the tarball
   plus `SHA256SUMS`.
4. **Release.** A pull request build lands on a **draft** release tagged
   `v<version>-pr.<number>` (recreated on every push, never marked latest); a `main` build
   on the public release `v<version>` marked **latest**; a manual dispatch on a draft unless
   the `draft` input is unchecked.

Quasar's consumer side finds the newest package through
`repos/CometWorks/cluster/releases/latest` and the asset name `ClusterForLinux-*.tar.gz`;
the archive format is one .NET reads without extra tools, and one the extraction Quasar
already uses for Magnetar handles.

## Bench archive

`Build/package.sh` stays the archive the regression bench and the Cluster Manager deploy
(`Docs/ClusterManager.md`): everything above plus the Magnetar install tree and, instead of the
compiled plugins, the two plugin source folders as one-commit git repositories Magnetar
compiles as dev folders, so a node host needs only the game. Its Steam
pair falls back to `Build/fetch-linux-dependencies.sh`'s `steam/` folder when the Magnetar
tree lacks it.

### Crash symbols

Release packaging publishes `ClusterSymbols-<version>.tar.gz` alongside the runtime archive and
includes both in `SHA256SUMS`. PDBs and native `.debug` files are retained there, with matching
runtime binary hashes in `symbols.json`. Rust release builds keep debug information and an ELF
build ID; packaging extracts `gateway-rs.debug` and adds a GNU debug link to the runtime binary.
`gateway-rs/BUILD-ID.txt` records its ELF notes. Preserve the symbol archive from the exact
release that crashed; a later release's symbols are not interchangeable.

`Build/build-gateway-rs.sh` verifies split DWARF, build IDs and portable checksums;
its `VERSION` also records the actual Rust toolchain and whether source was uncommitted.
`Build/release.sh` validates every recorded symbol and runtime hash before archiving.
Repeat verification on extracted trees with
`python3 Build/verify-symbols.py cluster-<version> ClusterSymbols-<version>`.
For standalone gateway staging, use `python3 Build/verify-symbols.py --gateway <directory>`.
