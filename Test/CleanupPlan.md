# Test harness cleanup plan

`run-cluster.sh` (8,225 lines, 40 `TEST_PATH` lanes) cannot run any more: every lane builds and
launches the retired recompiled server (`~/archive/dotnet-server-local`), which is gone. The native
scenarios (`scenarios.py`, `scenarios_ext.py`) are dead for the same reason: `bench.py` imports
`Test/cluster.py`, the old-server copy of the CLI. The plan below ports the suite to the stock
dedicated server + Magnetar plugins deployed from a release package, removes code that only
served past runs, makes the suite installable on any host from a short host contract, and ends
with `run-cluster.sh` deleted. Nothing here has been executed yet. Every step needs live
verification, which takes hours (a headless client, a WA and two or three nodes per lane).

Revised 2026-09-17 after a survey of this host (`polaris`); the "Host contract", "This host" and
"Coexistence" sections and the corrected paths under "Environment drift" are new.

**Status 2026-09-18 (branch `test-cleanup`).** Steps 0-8 are implemented; step 9 (a second host) is
not. Every lane is a native scenario, `run-cluster.sh`, `sweep.py`, `lane_watch.py`,
`steam_lane.py` and `LegacyLanes.md` are deleted, and every scenario has run at least once on
polaris against a package built from the branch. The last full pass ran 79/94, and every failure
left is a filed product finding (below), which keeps its scenario red on purpose.

Where the findings stand (ticket state, not prose): **T-0162 done**; **T-0164 cancelled**, not a
product bug; **T-0158, T-0159, T-0160, T-0161, T-0163, T-0165, T-0166, T-0167, T-0168, T-0169 and
T-0170 are in testing** - each has its fix committed on this branch and is waiting on its
verification. **T-0157** (hub direct-transport + precompiled local plugins) is in progress and
blocked on the hub bump, Docs/Proposals.md section 6.

Open work on this branch:

- **T-0171 - the four non-route test hooks.** Done: they are behind `Shared/Cluster/Seams/ClusterSeams.cs`
  (see the "Found while implementing" entry below). `CLUSTER_ALLOW_TEST_ACCEPT_ALL` deliberately stays.
- **T-0172 - the three-pass rule.** Three clean full passes on a package built from this branch,
  after the findings above are verified. About 3.5 h per pass on this host.
- **T-0173 - step 9, the second host.** Set the suite up on another host from `Test/README.md`
  alone. `nas` is not to be used without an explicit request.

## Requirements

1. **No reference to `~/archive/dotnet-server-local` may remain** anywhere in the repository: not in
   scripts, Python modules, defaults, comments or docs. It does not matter whether the reference
   is functional (a build or launch path) or descriptive ("based on …"). The final `git grep`
   check under "Done when" enforces this.
2. **Every test case that remains in the suite must run against a Magnetar-based cluster deployed
   from a release build.** A release build is the `ClusterForLinux-<version>.tar.gz` that
   `Build/release.sh` produces, unpacked and configured as its `README.md` describes
   (`SPACE_ENGINEERS_SERVER`, `MAGNETAR_HOME`, `CLUSTER_ROOT`), using the pre-compiled node and
   WA plugins from its `plugins/` and Direct Transport from the hub. The developer overrides
   `CLUSTER_PLUGIN_SOURCE` / `DIRECT_TRANSPORT_SOURCE` stay unset. In practice:
   - The suite takes the unpacked package directory as input. It starts, stops and inspects the
     cluster through the package's `cli/cluster` (or its `cli/cluster.py` with the same
     environment), never through repo-relative build outputs.
   - The Gateway is the packaged `registry/ClusterRegistry.dll`, not
     `dotnet run --project ClusterRegistry`. The world tool is the packaged
     `tools/MagnetarWorld/MagnetarWorld.dll`. The default world is the packaged
     `examples/Cluster-Simple`.
   - **The cluster plugins stay lean.** Test support does not go into `ClusterNodePlugin`,
     `WorldAuthorityPlugin` or `Shared`. Instead, the suite brings its own **test utility plugin**
     (proposed: `Test/ClusterTestPlugin`, its own manifest and dev folder). The suite loads it
     into the node and WA profiles next to the cluster plugins. It reads, logs, or otherwise
     makes internal state accessible for test execution and verification, including test-only
     actions such as placing a player, forcing a save or spawning a fixture grid.
   - The package and the CLI get only the small, generic hooks the suite needs to deploy that
     way:
     - extra local plugins to install per role (for the test utility plugin), the same
       mechanism the packaged plugins use (`<node>-magnetar/Local/<Name>/` + the profile's
       `<Local>` list), so the test plugin is built once and every node runs the same DLL;
     - per-node extra environment;
     - extra Gateway arguments;
     - a port base, so two clusters can share a host (see "Coexistence");
     - access to the world tool's variant steps (`to-handover`, `to-proximity`, ...).

     Nothing test-specific is added to the package. Test-only settings such as
     `CLUSTER_ALLOW_TEST_ACCEPT_ALL` stay test-side and reach the processes through those hooks.
   - The test-only node control endpoints in `Shared/Cluster/ClusterControlServer.cs` move out.
     These are the ~80 `*-test-*` routes plus the diagnostic routes only the lanes call. They go
     into the test utility plugin, and the cluster plugins keep only the endpoints production
     operation uses. Where a test endpoint needs internal state that the cluster plugins do not
     expose, it is read from the test plugin through the publicized game and plugin assemblies,
     or by a Harmony patch in the test plugin. The cluster plugins do not grow accessors for
     tests.
   - A test case that cannot be made to work this way is deleted, together with its entry in
     `coverage.py` and its claim in `TestPlan.md`'s coverage. It is not kept on another code path.
     Record every deletion and its reason in the run notes.
3. **The suite is installable on another host from one file.** Everything host-specific comes
   from one gitignored `Test/host.env` (template `Test/host.env.example`) or the same variables
   in the environment; nothing under `Test/` hard-codes a path outside the repository, a host
   name, a user or a Steam id. `python3 Test/doctor.py` checks the contract and prints what is
   missing, and every run records the host and the versions it ran against.
4. **The suite never disturbs anything it did not start.** Teardown is scoped to the suite's own
   run root and process groups; a host-wide kill by binary name is not allowed (it takes down
   the standing regression cluster and other sessions' clients on this host).

## Decision: port to Python, on top of `Cli/cluster.py`

Python is the simpler target here, not just the tidier one:

- The rest of the suite is already Python: `bench.py` (checks, evidence, `run_scenario`),
  `scenarios.py` / `scenarios_ext.py` (native scenarios), `run_suite.py`, `coverage.py`, and the
  `se_remote` client from the se-remote skill.
- `Cli/cluster.py` already starts a Magnetar cluster (per-node config dirs, admission hash, seeded
  dedicated config, `prewarm`) and is documented as the module the suite imports.
- Most of the script is `curl | jq` plumbing, which becomes dict access. It also embeds 5 Python
  heredocs and a 2,800-line startup polling loop (lines 771-3562) with lane bodies inside it.
- Splitting the bash into a helper library would keep the old-server launch model and the
  `jq --argjson` size problems, and it would still be untestable piece by piece.

Packaging: `Test/` becomes a `uv` project (`Test/pyproject.toml`, Python >= 3.11, standard
library plus `httpx` for `se_remote`). `se_remote.py` is found by path: `SE_REMOTE_SKILL`, else
`~/.claude/skills/se-remote`, else `~/ws/se/se1/plugins/remote/skills/se-remote` (`hostenv.py`). It is not a git
dependency, because the client must match the Remote plugin the Pulsar profile loads, and that is
a local checkout on every bench host. The old `sys.path` hack into the skill's `.venv` is gone.
Run everything as `uv run python run_suite.py ...` (or `python3` with `httpx` installed).

## Host contract (what the suite needs from a host)

One file, `Test/host.env`, sourced by `run_suite.py`, `doctor.py` and `bench.py`; every entry
has an autodetected default so that on a host laid out like this one the file can be empty.

| Variable | Default | Meaning |
|---|---|---|
| `CLUSTER_PACKAGE` | newest `Build/dist/cluster-*` or `Build/out/release-*` under the repo | the unpacked release package under test (`registry/`, `plugins/`, `tools/`, `cli/`, `examples/`) |
| `SPACE_ENGINEERS_SERVER` | `~/.steam/steam/steamapps/common/SpaceEngineersDedicatedServer` | dedicated server install (`DedicatedServer64/`, `Content/`) |
| `SPACE_ENGINEERS_ROOT` | `~/.steam/steam/steamapps/common/SpaceEngineers` | game client install the headless clients run from; its build must match the server's |
| `MAGNETAR_HOME` | `~/.config/Magnetar` | Magnetar install (`MagnetarInterim.bin`) |
| `PULSAR_HOME` | `~/.config/Pulsar` | Pulsar install (`Interim.bin`) and its `Interim/` profile with `remote` and `direct-transport` enabled |
| `SE_REMOTE_SKILL` | `~/.claude/skills/se-remote`, then `~/ws/se/se1/plugins/remote/skills/se-remote` | local checkout of `skills/se-remote` (the Python client must match the Remote plugin the profile loads) |
| `CLUSTER_TEST_ROOT` | `~/cluster-runs/bench` | run root for clusters and client data dirs (never `/tmp`: tmpfs, and crash dumps must survive) |
| `CLUSTER_TEST_RESULTS` | `~/cluster-test-results/bench` | evidence directory, one run per subdirectory |
| `CLUSTER_TEST_PORT_BASE` | `100` | offset added to every CLI port (see "Coexistence"); `0` = the CLI defaults |
| `CLUSTER_TEST_REMOTE_PORT_BASE` | `24200` | Remote API ports of the suite's clients (`24158` stays the interactive singleton's, `24300+` the regression fleet's) |
| `CLUSTER_TEST_NETWORK_WINDOW` | empty | `HH:MM-HH:MM` local; no scenario may start if its budget would cross it |
| `CLUSTER_TEST_RENDER` | `1` | `0` on a GPU-less host: every client runs `--no-render`, and the scenarios that assert a screenshot are deferred with that reason |
| `CLUSTER_TEST_MIN_FREE_GB` | `10` | RAM floor (`available` column of `free`) below which a scenario is not started |
| `PLANET_WORLD_ARCHIVE` | `~/ws/se/se1/plugins/remote/Worlds/RemoteAPITestEarthPlanet.zip` | Earth fixture for the planet lanes |
| `TEST_PLAN` | `~/ws/se/se1/clustering-plan/Plan/TestPlan.md` | the scenario list `coverage.py` cross-checks |
| `STEAM_CLIENT_HOST` / `STEAM_CLIENT_USER` / `STEAM_CLIENT_ID` | empty | the second host with a retail client for the `steam` lane; empty defers the lane |

Toolchain: .NET 10 runtime for the package, .NET 10 **SDK** on a test host (the test utility
plugin and, when the package is built here, `Build/release.sh`), Python >= 3.11, `uv`, `procps`,
`git`, `tar`. No `curl`, `jq` or `rg` (they go with the bash harness).

`Test/doctor.py` checks, in order, and exits non-zero at the first hard failure:

1. the package layout (`registry/ClusterRegistry.dll`, `plugins/ClusterNode/ClusterNode.dll`,
   `plugins/WorldAuthority/WorldAuthority.dll`, `tools/MagnetarWorld/MagnetarWorld.dll`,
   `cli/cluster`, `examples/Cluster-Simple`) and `manifest.json`'s commit;
2. the game installs, and that client and server are the same game build (a mismatched pair is
   admitted by the Gateway and then dropped by the node with "version mismatch" in the client log
   and nothing in the Gateway's);
3. Magnetar and Pulsar (`Interim.bin`, `Libraries/Compiler/Compiler.bin`, the profile's
   `remote` and `direct-transport` entries, `ExperimentalMode=True` in the seed
   `SpaceEngineers.cfg`);
4. the pre-warmed plugin caches (`~/.cache/Magnetar/Legacy/GitHub` for nodes,
   `~/.cache/Pulsar/Legacy/GitHub` for clients) hold `DotNetCompat-*`, `LinuxCompat-*` and
   `direct-transport`, and the node-side and client-side copies of the shared plugins are the
   same artefacts - the one-version-set rule; a warm-up fetch needs `PULSAR_GITHUB_TOKEN`, which
   the doctor reports as present/absent without printing it;
5. the ports of the chosen port base are free, and the run root holds no live processes;
6. RAM (`available`), disk under the run root and the results root (warn under 100 GB), GPU
   when `CLUSTER_TEST_RENDER=1`;
7. the fixtures (`PLANET_WORLD_ARCHIVE`, `TEST_PLAN`), and which capabilities the host lacks
   (render, Steam host, root for netem) so `coverage.py` can defer per host instead of per
   hard-coded reason.

The doctor's output is also the first file of every run directory (`host.json`), together with
`cli/cluster versions` (package commit, Magnetar, plugin hashes, game build).

## This host (polaris), as surveyed 2026-09-17

What the defaults above resolve to here, so the first runs need no `host.env`:

| Item | Where |
|---|---|
| dedicated server | `~/.steam/steam/steamapps/common/SpaceEngineersDedicatedServer` |
| game client | `~/.steam/steam/steamapps/common/SpaceEngineers` |
| Magnetar | `~/.config/Magnetar` (host install, 2.4.1.1) |
| Pulsar | `~/.config/Pulsar/Interim.bin`; profile `~/.config/Pulsar/Interim/` with dev folders `~/ws/se/se1/plugins/remote` (`Remote.xml`, branch `main`) and `~/ws/se/se1/direct-transport` (`DirectTransportClient.xml`), hub plugins AutoDock, Performance, Bugfixes |
| plugin caches | `~/.cache/Magnetar/Legacy/GitHub` (nodes), `~/.cache/Pulsar/Legacy/GitHub` (clients); both hold `DotNetCompat-6d00a29d`, `LinuxCompat-68cd6e19` |
| GitHub token | `~/.profile` exports `PULSAR_GITHUB_TOKEN` from `~/keys/gh.key`; defined nowhere else |
| se-remote skill | `~/.claude/skills/se-remote -> ~/ws/se/se1/plugins/remote/skills/se-remote` (uv project, `httpx`) |
| release package | `bash ~/ws/se/se1/cluster-regression/bin/release-package` -> `~/ws/se/se1/cluster/Build/out/release-<describe>` (refuses a dirty tree); or `Build/release.sh` -> `Build/dist/`; `~/.cache/cluster/linux-dependencies` is fetched |
| TestPlan | `~/ws/se/se1/clustering-plan/Plan/TestPlan.md` (the `coverage.py` default is right; the `~/ws/se/...` path an earlier revision of this plan gave does not exist) |
| Earth fixture | `~/ws/se/se1/plugins/remote/Worlds/RemoteAPITestEarthPlanet.zip` (not under `~/ws/se/...`) |
| shader cache seed | `~/.config/SpaceEngineers/ShaderCache2` |
| toolchain | .NET SDK 10.0.302, Python 3.13.5, `uv` at `~/.local/bin/uv` |
| resources | 32 cores, 62 GB RAM, 1.2 TB free on the NVMe; `/tmp` is tmpfs |

The standing regression cluster (`~/ws/se/se1/cluster-regression`, run root `~/cluster-runs/regression`)
is **stopped** since 2026-09-15 19:27 (development paused by Viktor) and restarts only on his
word. Its ports when it runs: Direct Transport `192.168.1.20:27116`, Steam `0.0.0.0:27016`,
Gateway control `127.0.0.1:29416`, node backends `28416+i`, controls `29416+i`, Remote API
`24300+`. Crontab jobs run every 3-15 minutes against that run root (`entity-latency-sample`,
`churn-capture`, `node-placement-sample`, `client-rss-sample`); they read the regression run
root only and do not touch a bench cluster under another root.

## Coexistence on a shared host

These rules come from this host's standing instructions and apply on every host that runs
anything else; the suite enforces what it can and the README states the rest.

- **Port base.** The bench runs at `CLUSTER_TEST_PORT_BASE=100`: Direct Transport
  `127.0.0.1:27216`, Gateway control `29516`, backends `28516+i`, controls `29516+i`, Steam
  frontend **off** (`--steam ""`) unless a lane needs it. The generic CLI hook is one number
  (`CLUSTER_PORT_BASE`, default 0) applied wherever `cluster.py` derives a port today.
- **Scoped teardown.** `run_suite.kill_cluster_processes` currently SIGKILLs every
  `ClusterRegistry`, dedicated server and `Interim` process on the host. It becomes: the pids
  whose `/proc/<pid>/cmdline` or environment names the suite's run root, plus the process
  groups the suite spawned - the same rule `Cluster.running_pids` already follows. Clients are
  killed by process group (`kill_client`), never by `pkill -f Interim`.
- **Clients on the cluster host.** The bench's one or two short-lived clients run on the host
  beside its own cluster (that is how every lane always ran, and they are not pilot clients in
  the sense of the regression fleet rule that keeps pilots off polaris). They still count: a
  scenario does not start under `CLUSTER_TEST_MIN_FREE_GB` of available RAM, and "three passes
  on a quiet host" means the regression cluster is stopped or idle. **Assumption to confirm
  with Viktor:** running the bench while the regression cluster is live is allowed on polaris as
  long as the RAM floor holds; if not, the doctor refuses when `~/cluster-runs/regression` has
  live processes.
- **Rendering.** Clients launch with `--headless --resolution 1280x720`, never `--no-gpu`;
  screenshots are taken at the framebuffer size. A client that only has to exist (the second
  client of the two-client lanes, the duplicate-identity client) runs `--no-render` with
  `PULSAR_NO_RENDER=true` in its **environment** (the in-process export races the plugin load).
- **Nightly network window.** On polaris the ISP router restarts the connection 03:58-04:02 and
  the regression cron (`03:47`) kills every `Pulsar/Interim.bin` on this host and the client
  hosts, bench clients included. `CLUSTER_TEST_NETWORK_WINDOW=03:45-04:07` here; the runner
  computes each scenario's budget (the sum of its timeouts) and refuses to start one that would
  overlap the window, and never schedules a full-suite run across it. Sundays add the-rig's
  02:59-03:05, irrelevant to a single-host bench.
- **Nothing fetches from GitHub on its own.** Nodes start with `-noupdate` and the caches the
  package CLI pre-warms (`cli/cluster prewarm`, one warm-up node, updates allowed, snapshot
  propagated); clients start from a copy of the host's Pulsar profile with its warm caches,
  `-noUpdate`, `MaxSourceAge` pinned to a year, and the shared `GitHub/` cache symlinked. The
  doctor's cache check is the gate; `curl -s https://api.github.com/rate_limit` used-counts
  flat across a run is the proof when in doubt.
- **Resource watch.** Before every scenario the runner records `free`, load and disk free into
  the run directory (`resources.jsonl`); on polaris `bash ~/ws/se/se1/cluster-regression/bin/host-stats`
  remains the cross-host view but is not a suite dependency.

## What depends on the retired server (must change)

| Where | What | Replacement |
|---|---|---|
| `run-cluster.sh:8-9` | `SERVER_ROOT`, `SERVER` = recompiled `SpaceEngineersDedicated.dll` | `Cli/cluster.py` Magnetar launch |
| `run-cluster.sh:583-585` | builds the recompiled server and its `MagnetarWorld` | the package's `tools/MagnetarWorld` |
| `run-cluster.sh:696` | `SolutionDir="$SERVER_ROOT"` | drop |
| `run-cluster.sh:724` | `REPLICATION_TABLE_HASH` from `ReplicatedTypes.json` | the assembly digest `Cli/cluster.py` computes |
| `run-cluster.sh:750-757` and 22 more `dotnet "$SERVER"` sites (node, WA and replacement-WA restarts, e.g. 3005, 4420-4424, 7178-7182) | node/WA launches with `SPACE_ENGINEERS_PREMADE_WORLD` | `Cluster.start` / per-node restart; premade world via the seeded dedicated config |
| `save-fidelity` (596), `planet-diff` (639), `conversion` (7646) | a stock "vanilla control" server started from the recompiled binary | a non-clustered stock DS under Magnetar (no cluster profile), or the stock `SpaceEngineersDedicated` binary directly |
| `Test/cluster.py` | old-server copy of `Cli/cluster.py` (`server_repo`) | delete; `bench.py` imports the package's `cli/cluster.py` (`CLUSTER_PACKAGE/cli` on `sys.path`) |
| `Test/dispatch.py:38-43` | `REPOS` / `BUILD_OUTPUTS` name `dotnet-server-local` | delete `dispatch.py` and the CLI's `--host` (a remote host runs the suite itself, see "Setting up another host") |
| `Test/scenarios_ext.py:624` | normalizer pinned at `dotnet-server-local/ClusterPlugin/ImportRoundTrip.cs` | `Shared/Cluster/ImportRoundTrip.cs`; the pinned SHA (`749b00ca…`) no longer matches (`8afe7b53…`), so review the diff before re-pinning |
| `Test/coverage.py:110` | P2-MOD-06 points at `dotnet-server-local/Mods/ClusterJanitor` | the mod is in no working copy here (`~/ws/se/se1/plugins/remote`, `~/ws/se/se1/direct-transport`, `~/ws/se/se1/cluster` checked); keep the deferral with that reason |
| `Test/README.md` | build instructions and `SERVER_REPO` for the old server, `/tmp/opencode` examples | rewrite for the Python runner (see "Documentation") |

## Environment drift (fix while porting)

- **Client launcher.** `~/.local/share/Headless/Interim` and `~/.config/Headless` are gone from
  every host (2026-08-28); `Cli/cluster.py`'s `prepare_client_profile` still copytrees
  `~/.config/Headless` and raises before doing anything. The launcher is upstream Pulsar,
  `~/.config/Pulsar/Interim.bin`, a native binary (no `bash` in front). The proven single-host
  recipe is the regression harness's (`~/ws/se/se1/cluster-regression/regression/clients.py`, `_argv` and
  `_process_env`):
  - Pulsar flags first: `-noSplash -noPrompt -noUpdate -lazySteam -multiInstance -useHome
    -bin64 <client>/Bin64` (`-bin64` is required or Pulsar dies reading a missing
    `libraryfolders.vdf`; `-multiInstance` lifts Pulsar's own guard; `-useHome` puts the
    profile under the per-client `HOME`);
  - then the plugins' options: `--headless --resolution 1280x720 --max-fps 15 --no-audio
    --client-id <id> --client-name <name> --connect <dt endpoint>`, plus `--no-render`
    (and `--bare-bones`, which is gated on both) for a client that must not render;
  - per-client `HOME`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`, `TMPDIR`, `SPACE_ENGINEERS_ROOT`,
    shared `NUGET_PACKAGES` / `NUGET_HTTP_CACHE_PATH`, `PULSAR_NO_RENDER` when rendering is
    off, the crash-dump env;
  - the data dir staged from the host profile: `~/.config/Pulsar/Interim` copied with its
    warm caches, `Sources/sources.xml` `MaxSourceAge` pinned, `GitHub/` symlinked to the
    shared cache, `SpaceEngineers.cfg` with `ExperimentalMode=True` (both sides need it or
    the join is dropped 300 ms after admission), `Remote.cfg` with the client's port, the
    shader cache seeded file by file.
  - Not the se-remote skill's `StartGame.sh`: it drives one interactive singleton from a
    per-host `GameConfig.env` and has no isolation. Open point: `--quality minimal` was a
    launcher patch of the old fork; the regression harness says it is gone, the skill's
    `GameConfig.env.example` still passes it. Check the Remote `options`/`main` branch for it
    before relying on it.
- The client-side launch code (`prepare_client_profile`, `start_client`, `kill_client`,
  `wait_client`, `enable_experimental`) leaves `Cli/cluster.py` for a new `Test/clients.py`:
  it is test tooling, not cluster operation, and the package must not carry it.
- The `Initializing 4 plugins` client marker (line 3553) depends on the plugin count; wait for
  the Remote API instead (`Started HTTP Listener|Remote API server started`, then `/v1/status`).
- Headless exit code 143 is its normal exit; never treat it as a failure.
- `PLANET_WORLD_ARCHIVE` defaults to `~/ws/se/se1/plugins/remote/Worlds/…` in the script and the archive is there
  (`RemoteAPITestEarthPlanet.zip`); the earlier plan revision's `~/ws/se/se1/...` path was
  wrong. `coverage.py`'s `~/ws/se/se1/clustering-plan/Plan/TestPlan.md` is right for the same reason.
  Both become `host.env` entries with those defaults.
- The Steam lane hard-codes `STEAM_CLIENT_HOST=sirius`, the user and a Steam id (lines 708-710).
  sirius is decommissioned (2026-09-03). They become the three required `host.env` settings,
  empty by default, and the lane is deferred while they are empty.
- `Cli/cluster.py --host` imports `dispatch`, which only exists in `Test/`. Drop `--host` and
  `dispatch.py`.
- `run_suite.py` puts lane run roots under `~/tmp/lane-<lane>`; everything moves under
  `CLUSTER_TEST_ROOT`.
- `Cli/cluster.py`'s `Config.world_template` default is `gateway_repo/Test/Saves/Cluster-Simple`
  (a repo path); from a package the default must be `examples/Cluster-Simple`.

## Code that only served past runs (remove)

- `START_WA=true` is constant; drop the `! $START_WA` branches (748, 754, 781).
- The "Based on TestLinux.sh, keep in sync" comments (4, 3567). The source script is gone.
- The stale milestone labels "Loading Cluster-Simple" / "Loading End-only world" (774, 778): they
  are wrong for every non-default world.
- 19 comments that retell individual sweeps (`2026-07-28`, `2026-07-29`, `w2x`). Keep the rule
  each one states and drop the anecdote.
- `Test/README.md`'s "Validation status" list: 25 dated PASS lines with `/tmp/opencode/...`
  roots that no longer exist. Results live in the run directories; the README describes how
  to run, not what once passed.

## Duplication to fold into helpers (from the 2026-07-24 review)

- WA kill → lease fence → replacement WA: 4 copies (`world-authority` ~3005, `planet-journal`
  ~3283/3502, `respawn-chaos` ~5852, `chaos` ~7423). They differ in fresh vs retained
  `CLUSTER_DATA_ROOT` and in the fence predicate; make those the parameters.
- Respawn row selection: 2 copies (`start_second_client`, the world-authority founder spawn).
- Stock server load + Python grid diff: 2 copies (`planet-diff`, `conversion`).
- The 5 Python heredocs become plain functions.

## Lane inventory

The line ranges are approximate. `coverage.py` has the scenario ids for each lane.

| Lane(s) | Lines | World (MagnetarWorld step) | Notes |
|---|---:|---|---|
| `voxel-broadcast` | 782-806 | Earth planet, `to-split` | client-free, inside the startup loop |
| `world-authority` | 807-3179 | Cluster-Simple, `to-split` | ~2,370 lines inside the startup loop; covers P2-WA-00/01/03 and P2-MOD-01..05. Split into one scenario per id |
| `planet-journal`, `voxel-drilling` | 3180-3523, 3626-4496 | Earth planet, `to-split` (Creative override) | two clients; the heaviest lanes |
| `steam` | 3603-3625 + `steam_lane.py` | Cluster-Simple | needs a second host with the retail client |
| `online-snapshot`, `clean-shutdown`, `warm-restart`, `poison-warm` | 4497-4819 | Cluster-Simple | shared snapshot/restart body |
| `proximity`, `save-lineage` | 4820-5021 | `to-proximity` | |
| `rotation`, `rotation-force` | 5022-5085 | `to-split` | |
| `drain` | 5086-5150 | `to-split` | |
| `teleport` | 5151-5228 | `to-proximity` | |
| `rc-control` | 5229-5493 | `to-split` | radio toggle before the take |
| `pod-spawn` | 5494-5720 | `to-split` + Survival/cooldown/drop edits | |
| `respawn-chaos` | 5721-5907 | `to-split` | WA kill copy |
| `triage`, `triage-load` | 5908-6104 | `to-proximity` + `CLUSTER_MAX_PARTITIONS` | |
| `recovery`, `save-rollback`, `save-fidelity` | 6105-6362 | `to-split` / `to-fidelity` | shared reconnect path |
| `counters` | 6363-6506 | `to-counters` | |
| `nodeglobal` | 6507-6611 | `to-split` | |
| `save-pipeline` | 6612-6689 | `to-split` | |
| `concealment` | 6690-7023 + boot at 3524 | `to-split` + concealment env | |
| `poison` | 7024-7082 | `to-split` + corrupted save | |
| `rebuild` | 7083-7283 | `to-split` | |
| `chaos` | 7284-7561 | `to-split` | WA kill copy |
| `conversion` | 7562-7672 | `to-vanilla` + stock load | stock server copy |
| `save-fetch`, `save-retention`, `save-tee` | 7673-7869 | `to-split` | |
| `manual`, `automatic`, `handover`, `clientless`, `control` | 7870-8225 | `to-split` / `to-handover` / `to-clientless` | the Phase 1 core; port these first |
| `planet-diff` | 617-657 | Earth planet, `to-split` + `to-vanilla` | no cluster, no client; exits during setup |
| shared setup and helpers | 1-770, 3563-3602 | | becomes the lane framework |

## Found while implementing (2026-09-17)

- **The Magnetar hub pins Direct Transport too old for the cluster plugins.** The hub serves
  direct-transport 2212094 (2026-07-08). The plugins need c49afeb (2026-08-28) or newer: wa-1
  fails at startup with `Shared.Transport.UdpPeer2Peer has no field m_engineEvents`. The same pin
  will break any deployment that takes Direct Transport from the hub.
  Settled by T-0157 (2026-09-17, Viktor): the host's `~/ws/se/se1/direct-transport` dev folder is the DEFAULT
  for nodes and clients, and `bin/release-package` records the commit it staged. No `host.env` pin
  and no doctor warning are needed for this any more; `DIRECT_TRANSPORT_SOURCE=hub` is the switch
  back. Reading the plugin from the hub again waits on the hub bump, Docs/Proposals.md section 6.
- **Partition leases expired right after startup.** The registry heartbeat only started after
  the voxel catch-up wait, which never finished early on a world without planets. Fixed in
  `PartitionPersistence`/`VoxelJournalService` (heartbeat on its own thread; a storage-less
  node counts as caught up after its first sweep). The CLI's lease TTL default is now 15 s.
- **The CLI waited out its whole timeout for a node that had already exited.** Node warm-up now
  fails as soon as the process is gone (`wait_node_log`).
- **`cli/cluster stop --grace 60` left wa-1 running** and needed SIGKILL. FIXED by T-0162 (the
  LinuxCompat plugin replaced the server's `ExitThreadSafe` with `Process.Kill`), verified 2026-09-18
  on package v1.0.2-64-g1f7dec6: two runs of `fresh` + `stop --grace 60`, every process left on its
  own with exit code 0, the whole stop in 5.5 s of the 60 s budget, nothing SIGKILLed and the CLI
  exit 0. wa-1 is last out, at 5.2 s and 5.4 s - it is the slowest, but well inside the grace.
  T-0174.
- **The Gateway coalesces registry writes** (`CLUSTER_PERSIST_INTERVAL_SECONDS`, 60 s). A
  scenario that SIGKILLs the Gateway and expects its last operations back sets it to 1 s.
- **`player_settings_lane.py` is now P3-PLR-01** (`scenarios_player.py`), and `nas_smoke.py`
  takes the Gateway address as arguments.
- **Deviation: all test routes moved at once.** Step 2 said to move each lane's test routes as
  the lane is ported. `move_routes.py` moved every movable test-named arm (81 routes) and the
  `*ForTest` seams in two commits instead, because the plugin build and SUITE-TPL-01 check the
  whole surface anyway. Two test-named routes stay in the product on purpose (`/self-test`,
  `/igc-test-antenna`, the latter for the regression cluster's antenna A/B tool).
- **Product test hooks that were not routes** (so the mover could not take them). Done, T-0171:
  each now reaches the product through a static field of `Shared/Cluster/Seams/ClusterSeams.cs`,
  set only by the test plugin and inert while null - the convention of the game seams next to it.
  - `m_corruptImportField` (`ClusterControlServer`, consumed in `ImportRoundTrip`) ->
    `ClusterSeams.CorruptImportedDocument`, an `Action<XDocument, List<string>>` the gate applies
    to the normalised target document. While it is set the gate skips its two shortcuts (raw byte
    equality and the chunked compare against a prepared source), which is what the old
    `corruptField == null` tests did; that parameter is gone.
  - the `ControlReassertService` break state -> `ClusterSeams.SuppressControlRebind`, a
    `Func<ulong, bool>` the 1 Hz repair sweep asks per online player. `/liveness-status` no longer
    reports `breakClient`/`breakActive` (nothing read them).
  - `IgcTestHarness` and `ModChannelTestHarness` moved to `Test/ClusterTestPlugin/Source/`.
    `ClusterRuntime` raises the `ClusterSeams` services-installed seam at the end of its service
    installs - the first point where `MyAPIUtilities.Static` exists - and the plugin registers the
    mod-side handler with `ClusterSeams.OnServicesInstalled`. That registration is latched (it runs
    the handler at once if the raise already happened), because the late-start fallback in
    `ClusterPluginHost` starts the runtime inside the CLUSTER plugin's `Init`, which runs before the
    test plugin's; a plain event would have left the harness silently uninstalled there. `GET /igc-endpoints` became a moved route (only `scenarios_wa.py` calls it).
    `SetAntennaEnabled` moved the other way, into the product `AntennaGraphService`, because
    `POST /igc-test-antenna` stays (deviation above; `bin/antenna-ab` drives it on live nodes).
  - **`CLUSTER_ALLOW_TEST_ACCEPT_ALL` STAYS in the Gateway**, same deviation as
    `/igc-test-antenna`. It is not a test hook but the fail-closed guardrail of Security.md 3.6
    ("the Gateway refuses to start if any frontend is test-accept-all while allowTestAcceptAll is
    not explicitly true"), a product security property with its own test (P3-AUT-05) and its own
    `SelfTest.cs` coverage; removing it would delete that property, not relocate it. It is also
    mechanically unmovable: `ClusterTestPlugin` is a node/WA game plugin and the Gateway is a
    separate process that loads no game plugins. The suite keeps passing it in through
    `gateway_env`, which is the "nothing test-specific is added to the package" rule working as
    intended - the flag is a product option, the value is test-side.
- **The load balancer postdates several lanes.** A balance migration moved a partition the
  choreography of P1-SAV-07 and P2-VOX-01 relies on; both now hold the Gateway's automatic moves
  (`/automatic/pause`) at that point. P1-AUTO keeps them (automatic handover is its subject), and
  one of its runs saw a 5 s Start-marker blink during a balance migration.
- **Gateway registry files are CGR2 records** (12-byte header, raw deflate), not JSON; the restart
  scenarios decode them.
- **The stock-server check saves the session** through the vanilla Remote API and compares
  per-grid block counts: the root-grid listing folds subgrids into their physical-hierarchy root
  and made a complete load look 1987 blocks short.
- **Some node plugin loggers write only to the game log** (`<node>-data/SpaceEngineersDedicated_*.log`),
  for example Direct Transport's; `[ClusterPlugin]` console lines stay in the stdout log. The log
  helpers and `wait_node_log` read both.
- **Nodes raced on the shared split world.** The split world's sector has no objects, so every load
  rewrites `SANDBOX_0_0_0_.sbsB5`; two nodes loading the same directory read each other's
  half-written file (P2-CHR-02, "Attempted to read past the end of the stream"). The CLI now gives
  each node its own premade copy (`Config.premade_world`). `~/ws/se/se1/cluster-regression` has the same
  race (Docs/Proposals.md §4).
- **Product findings filed while porting** (the scenarios keep the old bar and stay red):
  - T-0160: the End HUD marker (far-broadcaster proxy) vanishes after the backend switch (P1-HND-01).
  - T-0161: a clientless tee handover is discarded when the source dies after capture (P1-SAV-06).
  - T-0162: drained nodes exit 137 instead of 0; the WA waits its full 90 s peer-drain grace
    because it asks the stopped Gateway (P2-SAV-01b/c, P2-REC-02w). Cause: the LinuxCompat
    plugin replaces ExitThreadSafe with Process.Kill (caught with gdb on the bench). Fixed on the
    cluster side the same day; the upstream change is Docs/Proposals.md §5.
  - T-0163: a warm-restarted node never claims recovery while a retention directive points into
    its old data root (P2-SAV-01c, P2-REC-02w). Fixed the same day; the two lanes then also
    needed the log helpers to read the `<node>-data-restart` game logs.
  - T-0164: the converted checkpoint has no client identity binding (P2-CNV-01). Not a product
    bug: SE writes `<ClientId>0</ClientId>` plus a `<HashedId>` in saves, and the World Authority
    already writes a row for every binding. The check now looks for the granted identity's row.
  - T-0165: a client's vanilla faction creation is silently dropped at the WA (P2-WA-00, P2-WA-01).
  - T-0166: the Start HUD marker blacks out for 4-5 s when a balance migration moves the
    broadcaster's partition (P1-AUTO).
  - T-0167: a starting WA sweeps for a global encounter at once instead of after
    `GlobalEncounterTimer`, so every WA start or failover spawns one. P2-REC-01 now counts
    spawning executions by operation id instead of assuming its dispatch is the only one.
    Fixed the same day (the first sweep waits one interval).
- **Full pass 1 (run `20260917-055254Z`, 2026-09-17 07:52-11:29 local): 79/94 passed.** Red for the
  findings above: P1-HND-01, P1-AUTO, P1-SAV-06, P2-SAV-01b/c, P2-REC-02w, P2-WA-00/01, P2-CNV-01.
  Harness defects fixed after the pass: P2-CHR-02 (the premade race), P2-WA-04 (handover refused
  while the partition was still client-routed), P2-WA-06c (a balance migration during the
  separation), P2-CON-01 (a balancer move raised the generation), P2-REC-01 (T-0167).
  The rerun on a package with those fixes (run `20260917-093035Z`) passed all five; P2-SAV-01c and
  P2-REC-02w stayed red on T-0163 with their restarted nodes loading the per-node premade copy.
  - T-0168: P1-BAL-01 stopped at 8/3 partitions in both runs. The plan's bar was even partition
    counts; the balancer plans by measured sim load and had nothing left to move. SETTLED by
    Viktor 2026-09-17: the bar is the balancer's own load verdict - `/balance/status` shows worst
    deviation within tolerance, no moves planned, at least one automatic move and no oscillation -
    and the fixture carries real sim load so the moves mean something. Per-node partition counts
    are recorded, not asserted; the balancer itself is unchanged. P1-BAL-01 was rewritten to that
    bar (8811208, 52ce7d5) and passes: bench run `20260917-153617Z`, all 13 checks, one component
    of 12151 sim-us/s moved node-1 -> node-2, then `worstDeviation` 0 and `movesPlanned` 0.
- **Passes 2 and 3 are deferred**, tracked as T-0172. Ten scenarios were red on the product findings
  above, so three clean passes cannot happen before those tickets are fixed; each pass takes about
  3.5 h on this host.
- **Intermittent:** P3-SUN-01 once saw a 1.26 s client clock step before the non-admin set (one
  failure, the rerun passed). P1-MANUAL once lost the client's survival-kit power toggle right
  after the handover (the set returned success, the kit read back enabled for 45 s, no server log
  line; run `readme-check/20260917-101030Z`, while the regression cluster was starting); the next
  two runs passed.
- **README check on polaris:** a second clone set up from `Test/README.md` alone (own `host.env`,
  port base 200, its own `Build/release.sh` package) passes `uv run python doctor.py` and ran
  P1-MANUAL (above). Note that the doctor needs `uv run`: plain `python3` lacks `httpx` and says so.
- **`P2-WA-ECON`** is a suite-only key for the WA-2 economy legs of the old world-authority lane.
- **The Steam lane has no native port.** Its ids need a Steam client host (`STEAM_CLIENT_HOST`);
  `coverage.py` defers them by host capability and reports them unported on a host that has one.
- `Cli/cluster.py` still carries `client_launcher` as configuration for `~/ws/se/se1/cluster-regression`;
  the CLI itself starts no clients.

## Execution order

Each step ends with its lanes passing **three times** on a quiet host. The findings discipline
still applies: one red run is not a regression, and one green run is not a fix.

0. **Baseline and host contract.** Add `Test/pyproject.toml`, `Test/host.env.example`
   (gitignore `host.env`) and `Test/doctor.py`. Build a release package (`Build/release.sh`, or
   `~/ws/se/se1/cluster-regression/bin/release-package` here), unpack it, run the doctor, then the
   package's `cli/cluster prewarm` / `fresh` / `status` / `stop` on its `examples/Cluster-Simple`
   at the bench port base, and start one headless client against it through the new client
   recipe up to a living character and a screenshot. Record `host.json` and the versions in the
   run directory. This is the first point at which the plan is proven on this host.
1. **Framework.** Extend `Cli/cluster.py` (generic, ships in the package) with:
   - `CLUSTER_PORT_BASE`;
   - a world-preparation hook (the MagnetarWorld `to-*` steps and XML edits);
   - per-node extra environment (concealment, partition caps);
   - extra Gateway arguments (`--steam`, `--paused` with `/resume`, the handover pair);
   - a third regular node;
   - killing and restarting one node with a fresh or retained data root, and a replacement WA;
   - extra local plugin folders per role, for the test utility plugin;
   - a stock non-clustered server.

   And test-side (`Test/`):
   - `clients.py` with the Pulsar launch recipe, one or two clients, render / no-render;
   - `bench.py` importing the package's `cli/cluster.py` and `clients.py`; delete
     `Test/cluster.py` and `dispatch.py`;
   - scoped teardown in `run_suite.py`, the resource and network-window guards, the run
     directory layout (`host.json`, `versions.json`, `resources.jsonl`, per-scenario dirs).

   Prove it by getting the existing native scenarios (`scenarios.py`, `scenarios_ext.py`)
   green against the package before porting any lane; they exercise the same framework.
2. **Test utility plugin.** Create `Test/ClusterTestPlugin` (manifest, `dotnet build` output
   installed as a local plugin next to the packaged ones). For each lane as it is ported (not
   all at once up front), move the test-only endpoints that lane uses out of
   `ClusterControlServer`, and delete them from the cluster plugins in the same commit. Check
   that a release deployment without the test plugin still serves every production endpoint
   (`Docs/Gateway/NodeAPI.md`) and none of the moved ones.
3. **Phase 1 core.** Port `control`, `manual`, `automatic`, `handover` and `clientless` as native
   scenarios. Delete their code from `run-cluster.sh` in the same commit, so the script shrinks
   as the port advances and no lane exists twice.
4. **Persistence and recovery.** `recovery`, `save-*`, `drain`, `rotation*`, `poison*`,
   `online-snapshot`, `clean-shutdown`, `warm-restart`, `rebuild`, `conversion`, `triage*`.
5. **World Authority.** Split `world-authority` into one scenario per scenario id. Then port
   `rc-control`, `teleport`, `pod-spawn`, `respawn-chaos` and `chaos` on the shared WA-replacement
   helper.
6. **Planet.** `voxel-broadcast`, `planet-diff`, `planet-journal`, `voxel-drilling`.
7. **Leftovers.** `concealment`, `counters`, `nodeglobal`, `proximity`, `save-lineage`; `steam`
   last, because it needs the second host.
8. **Remove the shell harness.**
   - Delete `run-cluster.sh` and `sweep.py`.
   - Remove the lane delegation from `run_suite.py`, and turn `coverage.py`'s `LANES` into native
     entries; `DEFERRED` splits into plan gaps (fixed text) and host capabilities the doctor
     reports (render, Steam host, root for netem, two-host clock skew).
   - Rewrite `Test/README.md`, and update `AGENTS.md` and `Docs/` where they mention lanes.
   *Done 2026-09-17.* `coverage.py` has no `LANES`; `DEFERRED` became `PLAN_GAPS` and
   `HOST_CAPABILITIES` (doctor.py reports `render`, `steamHost`, `root`, `planetWorld`, `twoHosts`).
9. **Second host.** Set the suite up on one other host from the README alone (nas is the
   candidate here: same Steam paths, Pulsar synced, 123 GB RAM, AMD iGPU) and run the Phase 1
   core there. What the README missed is the last fix of this plan.

## Setting up another host (what the README will say)

1. Install the dedicated server (SteamCMD app 298740) and the game client (Steam) at the same
   build; Magnetar (`MagnetarForLinux-*.7z`) and Pulsar with the `remote` and `direct-transport`
   plugins in its profile, both at the versions the reference host runs (on this fleet:
   `pulsar-sync <host> --sync` from polaris, then `pulsar-verify`).
2. .NET 10 SDK, Python 3.11+, `uv`. `git clone` this repository; `cd Test && uv sync`.
3. Download or build the release package and unpack it; `cp host.env.example host.env` and fill
   in what differs from the defaults (usually `CLUSTER_PACKAGE`, on a GPU-less host
   `CLUSTER_TEST_RENDER=0`, on a shared host the port base and the network window).
4. Warm the caches once with network access: `cli/cluster prewarm` in the package (needs
   `PULSAR_GITHUB_TOKEN` for a private hub repo; the public ones need none) and one client
   start.
5. `uv run python doctor.py` until it is clean, then `uv run python run_suite.py P1-MANUAL`.

## Documentation

- `Test/README.md`: purpose, host contract table, the setup recipe above, `run_suite.py` usage
  (`--list`, ids, `--native`), where results land, the coexistence rules, how to add a scenario.
- `Docs/Gateway/ClusterCLI.md` and `EnvVars.md`: the new generic hooks (`CLUSTER_PORT_BASE`,
  extra local plugins, per-node env, extra Gateway arguments, world steps).
- `Docs/Proposals.md`: one item for `~/ws/se/se1/cluster-regression` - its `GameClient` local-launch path
  and `Test/clients.py` implement the same recipe; once the bench one is proven, the harness
  can import it from the package's `Test/` or the two stay in sync by review. Not applied
  without Viktor's confirmation.
- `README.md` (repo root): the "Run a cluster" section keeps pointing the regression suite at
  the CLI; add one line for `Test/` and the doctor.

## Done when

- `uv run python run_suite.py` (with `CLUSTER_PACKAGE` set, or `--package <dir>`) runs every
  remaining scenario against a cluster deployed from that package, and all of them pass three
  times in a row. "Remaining" means every scenario `coverage.py` does not defer for a plan gap
  or a capability this host lacks.
- The suite passes against a freshly built `Build/release.sh` package on a host with no checkout
  of the old server, set up from `Test/README.md` alone (step 9).
- `python3 Test/doctor.py` is the only preflight, and a run directory always starts with
  `host.json` and `versions.json`.
- `run-cluster.sh`, `Test/cluster.py`, `dispatch.py` and `sweep.py` are gone; `Cli/cluster.py`
  has no client-launch code and no `--host`.
- The cluster plugins contain no test-only endpoints or test hooks. Everything the suite needs
  internally comes from `Test/ClusterTestPlugin`, and the release package does not ship it.
- No suite code kills a process it did not start, by name or by pattern.
- `git grep -i "dotnet-server-local"` finds nothing, `git grep "TEST_PATH"` finds nothing
  outside the finding notes, and `git grep -n 'Headless\|sirius\|/tmp/opencode' Test/` finds
  nothing.
