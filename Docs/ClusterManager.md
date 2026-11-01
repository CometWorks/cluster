# The Cluster Manager (CM) role — Magnetar edition

Saying **"You are the Cluster Manager"** should be enough. This file is what that means for a
cluster whose nodes are Magnetar-launched stock dedicated servers with the cluster plugins.
Read it, then `~/ws/se/notes/ClusterTesting.md` for the current run's state, and
`~/ws/se/se1/cluster-regression/CLUSTER-MANAGER.md` for the bench procedures (monitoring, crash dumps,
damage triage, scheduled windows, recording) that did not change with the launcher.

## Goal

After assigning lifetimes, publish them with `cli/cluster --root <run-root> nodes
publish-rotation --lifetimes-file <manager-publication> --valid-for 120` from the existing
Manager loop, renewing every 30 seconds. This binds the actual stagger/jitter assignments
to Registry epochs; it does not execute rotation. Do not copy lifetime files to nodes.
See [Client diagnostics](Gateway/ClientDiagnostics.md).

**Keep the cluster alive, and serve deployment requests from other agents.** No own
initiatives: the CM does not start experiments, change world settings or knobs, or redeploy
without a request (`memory/cluster-manager-role.md`).

On a deployment request: **stop the cluster, package HEAD, deploy it, start the cluster.** A
new (clean) world only on request. The quality bar is `memory/cluster-goal-set-2026-08-21.md`;
a cluster is expected to run 24 h clean, restart at most daily.

## What runs

| Process | Binary | Code |
|---|---|---|
| Gateway | `<archive>/registry/ClusterRegistry.dll` | `~/ws/se/se1/cluster/ClusterRegistry` |
| node-N, wa-1 | `MagnetarInterim.bin` (host install, `MAGNETAR_HOME`) + stock `DedicatedServer64` | `<archive>/plugins/ClusterNode`, `<archive>/plugins/WorldAuthority` (compiled, installed as local plugins per node) + `direct-transport` from the MagnetarHub |
| world tool | `<archive>/tools/MagnetarWorld/MagnetarWorld.dll` | `~/ws/se/se1/cluster/Tools/MagnetarWorld` |
| CLI | `<archive>/cli/cluster.py` | `~/ws/se/se1/cluster/Cli` |

Since 2026-09-15 the regression cluster runs the **release package** (`Build/release.sh`, the
same thing the GitHub workflow publishes; `Docs/Gateway/DeploymentArchive.md`), staged by
`~/ws/se/se1/cluster-regression/bin/release-package` into `~/ws/se/se1/cluster/Build/out/release-<git describe>`.
The package carries the node and World Authority plugins **compiled** (`plugins/ClusterNode`,
`plugins/WorldAuthority`; T-0157): the CLI copies the role's folder into `<node>-magnetar/Local/`
and enables it as a local plugin, so nodes run exactly the build of the packaged Gateway, tool
and CLI. Direct Transport comes from the MagnetarHub pin, enabled as a
dev folder - the same checkout the clients load (T-0157, 2026-09-17: the MagnetarHub pin is older
than the transport the cluster plugins need; `DIRECT_TRANSPORT_SOURCE=hub` switches back once it is
bumped). Keep that checkout clean: the package manifest records its commit. Magnetar is the host install (`~/.config/Magnetar`), not part of the
package - `MAGNETAR_HOME` names another.
The bench archive of `~/ws/se/se1/cluster/Build/package.sh` (`Build/out/magnetar-cluster-<describe>`,
Magnetar included) is the older lineage and still works. The regression harness picks the
deployment named by `CLUSTER_ARCHIVE` (full path) in `~/cluster-runs/regression/current-run.env`;
without it, its `REGRESSION_ARCHIVE_PIN` under `~/ws/se/se1/cluster/Build/out` applies, else the newest
archive there. **Set `CLUSTER_ARCHIVE` for a Magnetar run.**

Per node the run root (`~/cluster-runs/regression`) holds `<node>-magnetar/` (Magnetar config
dir: `Local/` plugin copy, hub cache, `info.log`) and `<node>-data/` (dedicated config,
saves, game log); `<node>.log` is the launch's stdout with the plugin's early lines. See
`Docs/Magnetar.md`.

## 1. Deployment

1. **Verify the repo state at package time.** `git status --porcelain` must be empty for
   `~/ws/se/se1/cluster`: the plugins, gateway and tool are all built from the working tree, and the
   manifest's commit must describe what was built.
2. Build + test: `release.sh` builds the two plugins, the gateway and the tool itself
   (`dotnet build ... -c Release -p:RunPostBuildEvent=Never`); for gateway changes run
   `dotnet run -c Release --project ClusterRegistry -- --self-test` first. A plugin that fails
   to load shows as `PLUGIN NOT LOADED` on the CLI and in `<node>-magnetar/info.log`, and the
   node exits before "Game ready". Check the first node's `info.log` after every deploy.
3. **Quiesce** — harness down, clients down on all hosts, `/registry/handovers` at 0. Never
   hard-kill mid-churn.
4. `uv run python -m regression.boot stop --grace 120`; confirm the final save wave
   (`find node-*-data/cluster/saves -name '*.mps' -mmin -5 | wc -l`); back up `node-*-data`,
   `gateway-registry` and `current-run.env`.
5. `bash ~/ws/se/se1/cluster-regression/bin/release-package` (or `bash ~/ws/se/se1/cluster/Build/package.sh` for a
   bench archive). Check the name printed; `-dirty` in it means step 1 was skipped (the
   release script refuses a dirty tree outright). Both refuse to overwrite a staging directory
   a live process runs from.
6. Set `CLUSTER_ARCHIVE` in `current-run.env` to the new staging directory (the unpacked
   `Build/out/<name>`), **with a dated comment saying what changed and why**.
   **6b. Pre-warm + consistency gate (T-0152, Viktor 2026-09-15).** No node and no client
   ever fetches from GitHub on its own; every process starts from a pre-warmed folder, and
   the whole cluster - server nodes and every test client on every host - runs ONE
   Magnetar/Pulsar/plugin version set. Node side: `cli/cluster --root <run root> prewarm`
   (the warm-up node alone, updates allowed, its `Sources/` snapshot propagated to every
   node dir; `--refresh` / `CLUSTER_PREWARM_REFRESH=1` re-reads the hub); `cli/cluster
   versions` prints the node-side set. Both read the node set from the `<node>-magnetar`
   dirs that exist under the root (never from `--nodes/--spares`) and print it. On the
   regression bench use `uv run python -m regression.boot prewarm [--refresh]` (the same
   method on the regression Config). Client side: `~/ws/se/se1/cluster-regression/bin/pulsar-prewarm` (`--refresh`
   to re-read the client hub) distributes the template and shared caches to every client
   host. `bin/version-consistency` is the proof and `boot start` refuses on a disagreement.
7. `uv run python -m regression.boot start` (a warm restart of the existing world is the
   default; `--reuse` is obsolete and ignored, `start --wipe` is removed) → `python3 bin/base-check`
   (must reach the expected count) → `regression.setup` → `bash bin/harness-up`. Never hand-roll
   the `regression.*` launches. A NEW world is `regression.boot fresh` (or `prepare` to build it
   without starting): over an existing cluster both refuse unless given `--wipe`, which ERASES
   it, and they check that before stopping anything. **Never issue or request `fresh --wipe`
   without Viktor's approval.**
8. Verify the fleet is flying before calling it done.

Harness traps met on w25 (2026-09-02) in `~/ws/se/se1/cluster-regression`; the first two were fixed in
cluster-regression f24dbbc the same day (confirmed on w26):

* `regression.setup` builds only the kinds in `REGRESSION_PLATFORM_KINDS` and keeps every
  platform already in `endurance-platforms.json` whose kind is not requested. To add a ground
  platform under a running space fleet: `REGRESSION_PLATFORM_KINDS=ground regression.setup`.
* `regression.endurance` takes the platform kinds from `--platforms`, else from
  `REGRESSION_PLATFORM_KINDS` when `current-run.env` sets it, else every platform in the state
  file; it logs `platforms: ...` at the top of each session. A staged subset (6 space pilots
  before 12) is therefore expressed in `current-run.env` and survives a nightly `harness-up`.
* `regression.setup` waits up to 180 s for the cluster to read healthy (a rotation in progress
  logs `not healthy yet ...`) and always ends with a `RESULT: PASS|FAIL` line.
* Process-matching tools (`trace-node-birth`, `sag-catch.py`, `antenna-cost-probe`,
  `dip-window-profile`, `lift-pcu-limit`) match `MagnetarInterim` (comm is kernel-truncated
  to 15 characters) plus `-config .../node-N-magnetar`; `dotnet SpaceEngineersDedicated.dll`
  no longer exists.
* Killing harness processes: match on the executable (`ps -o pid= -C python3` + `/proc/<pid>/
  cmdline`, `ps -o pid= -C Interim.bin`). Never filter `bash` rows by their arguments: the shell
  running your own command matches its own text and dies (exit 144).
* Samplers (`soak-sample`, `sim-speed-trace`) and the persistent monitor source
  `current-run.env` once at launch: restart them after every archive pin change.

Plugin-only changes still go through stop/package/start: the CLI re-copies the packaged
plugin into `<node>-magnetar/Local/` at every node start, so a node relaunch under a moved pin
picks up new code — which is why the rolling path is not used unless asked for.

## 2. First-start costs and the compat plugins

* The first launch in a new config dir downloads dotnet-compat and linux-compat from the
  MagnetarHub (GitHub), Direct Transport included. Five nodes at once are five downloads; a
  GitHub rate limit (60/h anonymous) shows as `dotnet-compat` failures in `info.log` and the
  node exits (`memory/hub-refresh-fetch-once-then-distribute.md`). `MAGNETAR_GITHUB_TOKEN` in
  `current-run.env` lifts the cap.
* Config dirs persist across `fresh`, so a restart neither downloads nor compiles anything:
  the packaged plugins are copied in, not built. (Dev-folder mode, `CLUSTER_PLUGIN_SOURCE`:
  deleting `<node>-magnetar/DevFolder` forces a recompile.)
* linux-compat writes its native wrappers to `~/.config/SpaceEngineersDedicated/NativeWrapperCache`.

## 3. Reading a node that did not come up

In this order:

1. `<node>.log` — the launcher's stdout. `[ClusterNode] Critical: ... Failed to apply Harmony
   patch class X` lists every failing patch (the plugin then marks the node `Failed` and the
   Gateway never admits it). `Preloaded: game patched, cluster runtime starts with
   DedicatedServer.Run` is the good line; `Late start: the preloader hook did not run` means
   the launcher is not Magnetar or the plugin's `Preloader` class was not found.
2. `<node>-magnetar/info.log` — Magnetar: compile errors, hub download failures, `-ds64`
   detection, preloader patch results.
3. `<node>-data/SpaceEngineersDedicated_*.log` — the game log, as before.

`cluster.py status` reports a node without a process as "has no running process"; a node
whose plugin failed reports `Failed` through `/node-state`.

## 3a. Nodes while running: node control (T-0320)

The node set is per host: `serving` and `spares` counts, set at start by the hosts file and changed at runtime
with `cli/cluster nodes` (Docs/Gateway/NodeControl.md for the model, ClusterCLI.md for the verbs). Node ids are
never reused and every start gives every node a fresh id. `cluster scale` is deprecated.

Procedure:

1. **Look first.** `cli/cluster --root <run root> nodes list` (or `cluster-status`): per host actual/target
   counts and the hosts-file value, every node with id, host, pid, age, state, partitions and clients. One
   change at a time: wait until nothing is Starting, Draining or Stopping before the next one.
2. **Change counts:** `nodes set polaris --serving 5 --spares 1 --reason "..."`. An increase activates a spare
   at once and launches a new spare to refill; a decrease drains the OLDEST serving node. Read the `warnings`
   of the answer (projected load of a decrease; it is obeyed anyway).
3. **Take one node out:** `nodes remove <sel> --reason "..."` (the host's target drops by one) or
   `nodes recycle <sel> --reason "..."` (replaced, count unchanged; the rotator does this for lifetimes).
   Selectors: `node-9`, `PID@polaris`, `polaris#1` (oldest). The drain is graceful and forced after 300 s
   (`--drain-timeout`); `--now` forces at once and is visible to the players on that node, so use it only when
   the node must go now.
4. **Watch it converge** with `nodes list` (or `--wait` on the verb when no rotator holds the launcher lock).
   A stopped node leaves the list and appears in `nodes history` with who, why, age and whether it was
   forced. A stop that stays Stopping names its reason on the line (`detail`): it still owns partitions and is
   never killed while it does.
5. **Runtime changes last until the next start.** `nodes list` marks a host whose runtime counts differ from the
   hosts file. To make a change permanent, edit the hosts file (`<root>/hosts.json` or `CLUSTER_HOSTS_FILE`);
   the next `start` applies it.
6. A launcher must keep running on each host: on the regression cluster that is the harness rotator. With it
   down for more than 60 s `cluster status` reports `executorSilence` and nothing refills spares or replaces a
   failed node. Stopped nodes keep their data dirs as save holders; `nodes prune` (dry run; `--apply`) deletes
   the dirs no catalogued save references.

## 4. Everything else

Monitoring cadence (`bin/host-stats` before every start and every ~30 min), crash-dump
census and pruning, ship-damage triage, peer-session rules, the nightly 03:58–04:02 network
window and the Sunday `the-rig` window, and the recording rules (`current-run.env` is the
experiment log; findings are committed immediately) are unchanged: see
`~/ws/se/se1/cluster-regression/CLUSTER-MANAGER.md` §9–§15 (the single, authoritative CM document for that bench).
