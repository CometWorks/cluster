# Cluster scenario suite

Steam skin ownership can be checked without a game process or Steam login:
`dotnet run -c Release --project Test/PlayerSkins/PlayerSkins.csproj` patches a stand-in game
with the production Harmony patch and runs the production ownership service against a scripted HTTP
handler. It checks forwarding, owned/unowned skins, handover bindings, stale replies and retry suppression.
`dotnet run -c Release --project ClusterRegistry -- --self-test steam-skins` checks authenticated node
HTTP through real relay IPC, including the external Steam frontend and reconnect-stale replies.
From `GatewayRs/`, `cargo test -p gateway-rs skins` checks signed-result status and owner validation,
item filtering, delayed definitions, timeout, disconnect and IPC-loss cleanup against a fake Steam API.
A live Steam check still requires a player with owned skins: place and repaint armor, hand over to
another node, then place and repaint again; inspect `/test-entitlements?steamId=...` with the test plugin.
In a real game process, `P1-SKINS` checks that the node forwards a client's inventory request to the
Gateway and keeps an owned skin on a painted block while it strips an unowned one.

Global-state deltas and player profiles can be checked without game processes (SE-1870):
`dotnet run --project Test/GlobalStateDelta/GlobalStateDelta.csproj` checks the generic
`GlobalStateDelta` helper (only changed entries broadcast, removals, a replica that applies deltas
matches the authority), and `dotnet run --project Test/PlayerProfiles/PlayerProfiles.csproj` checks
the player profile codec on a live-sized record: one-player deltas, the camera cap and expiry, and
that a reader from before the delta format rejects a delta. In a real cluster, `P3-PLR-03` measures
the `player-profiles` broadcasts on the WA under a live-sized record and checks camera expiry.

Player moderation can be checked without game processes or Steam:
`dotnet run --project Test/PlayerModeration/PlayerModeration.csproj` exercises the
production promotion owner, command ledger and global-save section codec using
inert game/scheduler/envelope adapters. The Registry's `--self-test player-moderation`
uses real loopback HTTP auth and UDP WA command routing, plus admission state and
durable operation recovery. Build plugins with `-p:RunPostBuildEvent=Never` for
verification so the normal post-build target does not deploy to a local Magnetar.

The projector remap of a staged handover import (SE-1231) is checked the same way:
`dotnet run -c Release -p:RunPostBuildEvent=Never --project Test/ProjectorRemap/ProjectorRemapChecks.csproj`
calls `EntityIdProjectorPatch.RemapPrefix` from 16 threads at once and prints PASS when every
remapped id ends up in `MyBlueprintIdTracker` for its projector, written on the game thread only.
It needs the dedicated server's assemblies, no game process, and takes about a second.

The entity-event registrations of the staged import's builder (SE-1313) have a check of their own:
`dotnet run -c Release -p:RunPostBuildEvent=Never --project Test/EntityEventRegistry/EntityEventRegistryChecks.csproj`
first registers listeners from 16 threads the unguarded way (it reports the race it hits), then
records them on 16 threads through `EntityEventRegistryPatch.Recording` while the game thread adds
and closes listeners of its own, replays them on the game thread, and prints PASS when nothing threw,
no worker wrote the registry and every listener is registered. About a second, no game process.

The suite deploys a cluster from an unpacked **release package** (the `ClusterForLinux-<version>`
that `Build/release.sh` produces), joins headless clients to it, and asserts against structured
state: the Gateway's `diag` snapshot, the Ownership Registry, node control routes, process logs and
the clients' Remote API. Each scenario id comes from `TestPlan.md` (clustering-plan repository).

Nothing here names a host, a user or a path outside the repository: everything host-specific is in
`host.env` (see `host.env.example`), and `doctor.py` checks it.

## Layout

| File | What it is |
| --- | --- |
| `run_suite.py` | runner: doctor first, guards, one directory of evidence per scenario, `Summary.md` |
| `doctor.py` | host contract check (installs, versions, caches, ports, RAM, GPU); `--json` |
| `hostenv.py`, `host.env.example` | the host contract; `host.env` is gitignored |
| `bench.py` | one scenario's cluster, clients, checks and evidence (imports the package's `cli/cluster.py`) |
| `clients.py` | headless Pulsar client launcher (own HOME per client, Remote API port per client) |
| `testplugin.py`, `ClusterTestPlugin/` | test utility plugin: test-only node control routes, built per package |
| `scenarios_core.py` | Phase 1 core: control run, manual, clientless and fly-through handovers, automatic co-location |
| `scenarios.py`, `scenarios_ext.py` | membership, characters, balancing, recovery, security, Gateway self-tests; admin time of day (P3-SUN-01) and a dragged time-of-day slider, from the client and as bursts of values on the node (SE-1596, P3-SUN-DRAG) |
| `scenarios_admin.py` | admin API parity, scoped tokens, durable operations (a self-hosted Gateway, no nodes) |
| `scenarios_player.py` | player settings across a node rotation and a relog |
| `scenarios_recovery.py` | drain, rotation, node-kill recovery, bounded rollback, save round-trip fidelity, a hung node's stacks before its exit watchdog (SE-883, P2-HANG-01), a MyStringHash collision that throws instead of hanging the game thread with DotNetCompat's fix (SE-1606, P1-STRHASH-01) |
| `scenarios_saves.py` | save pipeline, replica fetch, retention, handover tee, paused-handover counters and client view |
| `scenarios_redeliver.py` | T-0396: a client-view adopt that re-delivers a grid still streaming to a viewer (the test plugin's `/stream-test-hold`) |
| `scenarios_property_sync.py` | #1376: a client-view adopt of a frozen grid whose emotion controller Syncs are still null (state sync before the thaw) |
| `scenarios_stale_view.py` | SE-1554: what a client holds of a grid after a handover. A container's item list when the source had a change queued or a delta unacknowledged at the export (the test plugin's `/stale-view-test-hold` and `-lose`), a block the client places before and after the handover (P3-HND-STALE; red with `handover.inventoryHeld` off or on a build without it); an ore detector's deposits on the seated client across two handovers (P3-HND-STALE-ORE, needs Remote's `oreDeposits` in `hud-signals`) |
| `scenarios_restart.py` | online snapshot, graceful shutdown, warm restart, poison quarantine, catalog rebuild |
| `scenarios_topology.py` | proximity merge, split/merge lineage, teleport arbitration, triage, concealment |
| `scenarios_wa.py`, `scenarios_wa_ops.py` | World Authority: identity, factions, economy, IGC, mod channel, encounters; remote control, pod respawn, WA failover chaos; a handed-over drop container despawning at its carried vanilla deadline (#1391, P2-DROP-REMOVAL, and -RED with CLUSTER_DROP_REMOVAL_HANDOVER=0); at most `drops.maxActive` container drops active cluster-wide, a despawned one replaced, the knob live, an offline drop counted until its deadline, 0 = no cap (#1443, P2-DROP-CAP); a drop's GPS marker leaving the WA and every node with the drop, after a handover too, and at most `drops.maxActive` markers (SE-1146, P2-DROP-GPS); commands answered after the last global save replayed from the journal after a WA kill (#1318, P2-WA-09); a broadcast controller message names its block, not Good.bot, and reaches only its target after the WA's chat path (SE-943, P2-WA-BCAST); a player logging in again under a new name keeps it on every role after the WA's next identity snapshot, a Steam name's platform icon too (SE-1100, P2-WA-RENAME) |
| `scenarios_delete_fault.py` | #1295: a graceful stop ends clean although a drained grid's BeforeDelete throws (P2-SAV-FAULT, and -RED with the guard off); SE-1491: the same for a stale collision filter refresh the close leaves (P2-SAV-FILTER, -RED) |
| `scenarios_grid_delete.py` | #1334: the leftover-ship sweep (`Tools/leftover-ship-sweep.py`, `POST /cluster/grid-delete`) removes unpiloted grids on their node, and a partition it empties is reaped (P2-GRID-DELETE), also after a slow frame (P2-GRID-DELETE-SLOW-FRAME, SE-897); grids of a woken offline partition are deletable, and three deletes in a row that empty a partition stay deleted (P2-GRID-DELETE-WAKE, SE-1317) |
| `scenarios_jump.py` | SE-1871: a jump drive's arrival stays off a grid at its target wherever that grid is: in the ship's own partition, another partition of its node, on the other node, or in an offline partition either node took offline (P2-JUMP-OBSTACLES, jumps started through the test plugin's `/jump-test`, no client) |
| `scenarios_admin_menu.py` | SE-1596: the Alt-F10 Entity List and PCU tab list another node's grids and offline grids, and a Remove or Stop on another node's grid runs there (P2-ADMIN-MENU, through the test plugin's `/admin-menu-*` routes, no client); the same lane records the list's time and size with thousands of grids on the other node (`ADMIN_MENU_COUNTS`). SE-1687: Cycle Objects "Biggest grids" walks the other node's grids and the offline ones, and the Trash tab's Stop entities stops a grid on the other node (P2-ADMIN-CYCLE, against vanilla's node-only walk and loop in the same run) |
| `scenarios_bot_ships.py` | SE-1316: the bot ship cleanup takes the ships bots left behind (owned only by identities of `cleanup.botSteamIds`, unused for `cleanup.botShipIdleMinutes` when a backup was taken) right after real registry backups through the admin API: a dry run logs them and removes nothing, then at most `cleanup.botShipsMaxPerRun` per backup go, each logged with owners, position and times; a player's ship, a seated one, one beside a viewed camera and one too fresh stay; a ship saved offline keeps its last use and goes right before its woken partition goes offline again; another bot standing beside a ship doesn't keep it in use, its owner bot does (SE-1528) (P2-BOT-SHIP-CLEANUP) |
| `scenarios_stop_after_warm_start.py` | SE-835, SE-863: a reaped partition's world seed stays out of a warm start (P2-STOP-REAPED-SEED), and a drain Final the registry refuses is captured again, so the stop still ends clean (P2-STOP-REFUSED-FINAL) |
| `grid_list_scenario.py` | SE-810: `!ess grids list` taken over from Essentials, and its alias `!grids list`, across nodes and offline partitions; run by hand with an Essentials checkout |
| `scenarios_npc.py` | #1354: the cluster-wide NPC census counts an offline partition's NPC grids and keeps their unvisited count across the offline/online cycle; the sweep removes unvisited NPC grids, gap-band ones sooner, never a non-NPC grid, and an emptied partition is reaped (P2-NPC-CENSUS); #1365: a node takes a mod's NPC factions and founders from the WA's global save at load instead of founding its own, also when it starts before the WA, and grids of unknown owners are reported (P2-NPC-FACTIONS); #1379: the cleanup policy keeps OCT (keep factions), player and static ownerless grids and removes every other NPC grid on the npc.* timer and ownerless dynamic grids (drops, debris) on the faster cleanup.* one, sooner in gap bands (P2-GRID-CLEANUP); #1390: small wreckage of cheap blocks goes after cleanup.wreckSeconds away from players, also near them while the node's sim speed is below 0.95, and right before its partition is offlined or drained; powered player grids with a functional block, big, costly, static and kept grids never (P2-WRECK-CLEANUP) |
| `scenarios_rename.py` | SE-1372: a player back under a new name, on another node and on the watcher's node, shows that name in the watcher's F3, chat and `/w` (P2-REJOIN-RENAME) |
| `scenarios_skins.py` | SE-1589: a client's skin inventory request reaches the Gateway, and the node keeps an owned skin on a painted block while it strips an unowned one (P1-SKINS) |
| `scenarios_spawn_preview.py` | SE1-0122: the respawn screen previews a spawn point on another node or in an offline partition |
| `scenarios_planet.py` | voxel broadcast and journal, dual-base drilling, planet differential, vanilla conversion (with a stock server) |
| `scenarios_blocks.py` | SE1-0163: every vanilla functional block type (generated from the game's CubeBlocks definitions) through a handover, an offline round trip and a node restart; ship storage with its retrieval orders, auto-deploy switches and combat cooldowns, a store and a retrieval answered only once the WA's global save is recoverable (#1270, P3-BLK-04-DURABLE), a store, order or delete whose answer was lost (#1317, P3-BLK-04-RETRY, a node self-test section), the stored ships in a `to-vanilla` export, safe zone and contract blocks across nodes, an admin safe zone from the world file on every node, a safe zone block from a converted world, admin safe zones changed in game; writes a per-TypeId table |
| `scenarios_contracts.py` | SE1-0247: accepted contracts on the World Authority's ledger, settled once across nodes, node restarts and a WA failover, mod API callbacks fired once; a settled contract not offered again after a partition rollback (#1272, P3-CON-ROLLBACK); an admitted settlement whose answers were lost is paid once (#1271, P3-CON-LOST); two clients accept on one node and deliver on another |
| `scenarios_admin_sync.py` | SE-1596: an Alt-F10 Trash Removal upload on node-1 reaches node-2 and the WA within seconds and survives a node restart and a WA replacement; a weather set on node-1 and an admin client's `/weather clear` on node-2 reach every role (P2-ADMIN-SYNC) |
| `scenarios_suite.py` | suite self-checks (the test plugin, the production route surface without it) |
| `coverage.py` | which `TestPlan.md` ids are native, retired, a plan gap, or need a host capability this host lacks |
| `soak_1149.py` | SE-1149, run by hand: a regular node's live heap and live grid count under handover churn, with a gcdump of both nodes every `SOAK_DUMP_EVERY` seconds. `SOAK_MODE` picks clientless ping-pong (default), client traversals or antenna proxies. `SOAK_TOOLCORE=on\|off` adds the ToolCore mod with `RespectPlayerMaxPCU` on (red: every grid a handover brought in stays alive) or off (green: the live grids stay at the spawned ones) |
| `nas_smoke.py`, `probe_lab.py` | manual tools: connect+spawn against a deployed cluster; movement probe lab |

The shell harness these scenarios replace (`run-cluster.sh` and its lanes) was removed on
2026-09-17; each scenario module's docstring names the lanes it replaced. The Phase 3 Steam lane
(`steam_lane.py`) has no native port yet: its ids need a Steam client host and are deferred.

## Set up a host

1. Install the dedicated server (SteamCMD app 298740) and the game client (Steam) at the same
   build. Install Magnetar (`MagnetarForLinux-*.7z`) and Pulsar; the Pulsar `Interim/` profile
   must enable the `remote` and `direct-transport` client plugins. Load Remote from a dev folder on
   the `main` branch of `CometWorks/remote` and keep it current: some scenarios read fields that
   newer Remote versions add (P2-CHAT-NAMES needs the chat receipts' `Sender`, PR #54). A checkout
   left at an older commit fails those at setup.
2. Install the .NET 10 SDK, Python 3.11+ and `uv`. Clone this repository and run `uv sync` in
   `Test/`. Check out the se-remote skill (`CometWorks/remote`, `skills/se-remote`) next to the
   Remote plugin the Pulsar profile loads.
3. Build a package (`Build/release.sh`, output in `Build/dist/`) or unpack a downloaded one.
   `cp host.env.example host.env` and set what differs: usually `CLUSTER_PACKAGE`; on a GPU-less
   host `CLUSTER_TEST_RENDER=0`; on a shared host the port base and the network window.
4. Warm the caches once with network access: `cli/cluster --root <dir> prewarm` in the package
   (`PULSAR_GITHUB_TOKEN` for a private hub repository), and one client start.
5. Run `uv run python doctor.py` until it reports `OK`.

### Adding a host beside one that already works

Steps 1 and 4 above are written for the **first** host on a fresh network. Beside an existing bench
host, copying is not a shortcut - it is the correctness-preserving choice, because two bench hosts
that disagree about a build or a plugin pin produce failures that look like product bugs:

* **Dedicated server.** `doctor` requires the client and server game builds to match. If the new
  host's `Bin64/SpaceEngineers.exe` is byte-identical to the working host's, `rsync` that host's
  `SpaceEngineersDedicatedServer` (about 8 GB) instead of a SteamCMD download: it *guarantees* the
  match a download only probably gives, and keeps Steam credentials off the new machine.
* **Plugin caches.** `rsync` `~/.cache/Magnetar/Legacy/GitHub` and `~/.cache/Pulsar/Legacy/GitHub`
  rather than running `prewarm`. The hubs are pinned for a year precisely so a resolve cannot
  silently move a commit; two hosts that compile different `direct-transport` pins is the failure
  that made the whole suite red on 2026-09-19.
* **The repository.** `git clone` needs the deploy key and the ssh host alias the origin URL names,
  which exist only on the host that already works. `rsync` the working tree instead (exclude
  `.git/`, `Build/out/`, `Test/.venv/`) and note the source commit - but **not** with
  `--delete-excluded`, which deletes the excluded paths on the destination and will remove the
  release package you just put in `Build/out`.
* **`Plan/TestPlan.md`.** `doctor` checks for it; copy `clustering-plan` too, or set `TEST_PLAN`.

No game config is needed: `clients.py` writes one when the host has none. A host that has never run
the game interactively is a supported host.

## Run

```bash
cd Test
uv run python doctor.py
uv run python run_suite.py --list
uv run python run_suite.py P1-INF-01 P1-MANUAL          # a selection
uv run python run_suite.py --repeat 3 P1-HND-01         # the three-pass rule
uv run python run_suite.py                              # every native scenario
uv run python run_suite.py --package ~/Downloads/cluster-1.0.2 P1-CTL
```

Results land in `CLUSTER_TEST_RESULTS/<run id>/`: `host.json`, `versions.json`,
`resources.jsonl` (one line before each scenario), one directory per scenario run (`ID`, then
`ID.2`, `ID.3` on repeats) with its checks, logs and screenshots, and `Summary.md` with a
coverage table. The exit code is 0 only when every selected run passed.

Each scenario runs its own cluster under `CLUSTER_TEST_ROOT/<scenario>` at the port base, and the
runner kills only processes started under that root. The runner refuses to start a scenario that
could overlap `CLUSTER_TEST_NETWORK_WINDOW` (`--ignore-window` overrides) or when less than
`CLUSTER_TEST_MIN_FREE_GB` of memory is available.

## Test-only routes

Routes that only the suite needs live in `ClusterTestPlugin/`, not in the cluster plugins. The
suite builds the plugin against the package under test (`ClusterTestPlugin/build.sh`, cached per
package under `CLUSTER_TEST_ROOT/test-plugin/`) and installs it through the package CLI's
`CLUSTER_EXTRA_PLUGINS`. `ClusterTestPlugin/move_routes.py` moves a route arm verbatim out of
`ClusterControlServer.Handle` into `Source/MovedRoutes.cs`. SUITE-TPL-01 reads the plugin's route
list from `GET /cluster/test-plugin` and proves that a deployment without the plugin serves none
of them, and still serves every production route of `NodeAPI.md`.

Two test-named routes stay in the product on purpose: `POST /self-test` (the node self-test
sections) and `POST /igc-test-antenna`, which the regression cluster's antenna A/B tool calls on
live nodes (`AntennaGraphService.SetAntennaEnabled`).

Hooks that are not routes reach the product through a **seam**: a static field of
`Shared/Cluster/Seams/ClusterSeams.cs` that only this plugin sets, inert while it is null, in the
convention of the game seams next to it. There are three (T-0171): `CorruptImportedDocument`
(P3-HND-03 perturbs the import round-trip gate), `SuppressControlRebind` (P1-CHR-03 breaks one
client's controller rebinding) and `OnServicesInstalled` (a latched session-init callback, which is where
`ModChannelTestHarness` registers the handler a mod would register). Their state lives in
`Source/TestSeams.cs`; the product side is a null check on a path that already exists. Add a seam
only when the behaviour cannot be a moved route, and never one that makes the product do test-only
work.

## Adding a scenario

A scenario is a function taking a `Bench`. It starts what it needs (`bench.start_cluster`,
`bench.start_client`), records evidence (`bench.record`, `bench.screenshot`), asserts with
`bench.check`, and states what it exercised (`bench.exercised`). Function attributes select the
deployment: `regular_nodes`, `world_template`, `split_step` (a MagnetarWorld fixture such as
`to-handover`) and `administrators`. Register it in a module's `SCENARIOS_*` table under its
`TestPlan.md` id; `run_suite.py` refuses to run while any plan id has neither a runner nor a
recorded reason.

## Offline wake draft checks

`Docs/OfflinePartitions.md` records SE-346/SE-347's exact acceptance matrix and limitations.
The detached check runs without a package or game:

```sh
dotnet run --project Tools/OfflineCompensationCheck -c Release -p:RunPostBuildEvent=Never
python3 Test/OfflineAdminRead/run.py /absolute/path/to/new/admin-read-output
```

Run these checks from the repository root.
The admin-read check compiles the exact facade with inert collaborators: an offline replica
needs no live partition, while live reads still require one. It does not launch the game.

Registry policy sections are `remote-use` and
`offline-partitions`. Native `P2-OFF-CALL-TERMINAL`, `P2-OFF-CALL-RC` and
`P2-OFF-CALL-IGC` require the normal doctor/isolated-package contract; their actual per-package results are recorded in `Docs/OfflinePartitions.md`. RC includes
a dynamic drone and raw client thrust. Additional native runners cover shared recovery crashes,
cache replacement, unknown/modded policy, admission, conflicts, laser and pose. Authored or partial
runners do not establish all of their plan obligations; remaining full differentials/decisions
are explicit in `coverage.py` and the acceptance map. Do not claim differential game, GUI, crash-injection or frame/traffic benchmarks from
model/container checks.

Player authentication checks: `ClusterRegistry --self-test player-access` covers
whitelist/priority admission, ban precedence, expiry, persistence and unsupported
roles. `--self-test player-moderation` includes real HTTP scope checks and fenced
promotion-access publication. `dotnet run --project Test/PlayerModeration/PlayerModeration.csproj`
checks the actual World Authority lease overlay, manual and default baselines,
forgetting a player, replica and replacement-authority expiry, and origin fencing
without a game process.
The Cluster CLI `--self-test` checks the new policy verb against contract parity.

### Workshop definition comparisons

With the standard bench environment, a claimed slot and its RAM guard, run
`python mod_definition_differential.py /path/to/corpus /path/to/new-results --case sound`
from `Test/`. Use `--case speed` for Midspace's Ship Speed Unlimited. The
original `mod_speed_differential.py` command still defaults to the speed case.

Each case records the Workshop script hashes and live game values from two
standalone controls and a three-process cluster. The unmodded and modded stock
controls must show the expected change before cluster agreement earns a PASS.
A differing node produces DIFF. An incomplete measurement or unexpected stock
control yields INCONCLUSIVE. The output folder contains the verdict, values and
logs. See [Workshop definition probes](ModDefinitionProbe/README.md) for the
mods and fields covered.

### MES encounter spawn

Run `python mes_encounter_differential.py /path/to/corpus /path/to/new-results`
with the same bench environment and slot as the definition comparisons. The
runner stages the pinned Modular Encounters Systems Workshop item (1521905890)
and a test encounter containing a single marked battery grid.

The fixture checks for the marker before issuing the request. Standalone and
node-1 each call MES's published `CustomSpawnRequest` once at the same empty
space coordinates. The test records the return value, the grid ID in MES's
successful-spawn callback, and the grid IDs visible to each process after the
request. A passing run needs exactly one live grid whose ID matches the callback.
Automatic encounters, player-triggered spawning and NPC behavior need separate cases.

Add `--handover` to move the spawned grid from node 1 to node 2 and back. The
standalone control records the spawn coordinates from MES's public NPC API and
decodes the spawn group, original prefab, stored start coordinates and spawned-by-MES
flag in the grid's NPC record. The cluster repeats those reads after roughly
30 simulation seconds on node 1 without moving, then after the same interval
on each receiving node. Each transfer must have a sole owner, a clean handover
gate and the original grid ID, terminal custom data and NPC identity. The
report includes the storage size and SHA-256 for investigating differences.
MES also writes a timestamp into this record.

### Energy Shields damage absorption

From `Test/`, run `python shield_damage_differential.py /path/to/corpus /path/to/new-results`
with the bench environment and a claimed slot. The fixture creates two powered
armor grids and adds the Workshop shield generator to one of them. After five
simulation seconds it applies the same 20-point hit to each grid. It records the
damage reported after the mod's handler and the resulting integrity loss,
including damage still queued for application. The standalone control must
show damage to the bare grid and full absorption on the shielded grid. The
cluster repeats the test on node 1. Other damage types, upgrades, handover, and
client effects need separate cases.

### Advanced Welding pad merge

`weld_pad_differential.py CORPUS RESULTS` loads Workshop item 510790477 and a
fixture mod in standalone and clustered worlds. The issuing server spawns two
separate grids with named batteries and opposing large weld pads. It samples
both grids while apart, moves the dynamic grid into pad contact and samples
again after 120 simulation ticks. A merge must preserve both original battery
IDs on one of the original grids, consume both pads and leave no duplicate on
the other cluster roles. The raw role snapshots are saved in
`MOD-WELD-PADS/observations.json`. This scenario exercises pad welding. Detaching
a block with a player's hand grinder requires a separate case.

### Nanite Control Facility v2.2 repair

Run `python nanite_repair_differential.py /path/to/corpus /path/to/new-results`.
Workshop item 219757726 uses its construction nanites to repair damaged armor.
The fixture creates two powered, static grids 2 km apart. Their armor targets
are at least 35 m from the factory, outside the block's 4 m native welder
radius. Each grid carries a remote control so the mod can find its power
distributor. Both factories start disabled, receive 100 steel plates and have
the same damage applied to their targets. Only the treatment facility is enabled.
A pass requires full repair on that grid while the disabled grid retains its
measured damage. The test world shortens nanite travel using the mod's config.
This case covers same-grid repair in creative mode.

### Grid references after handover

From `Test/`, run `python grid_reference_lifetime.py /path/to/new-results` with the
usual bench environment, test plugin and a claimed slot. The source node spawns
two ordinary 27-block armor grids in one partition. It holds one grid strongly
and tracks both grid instances, a sample block from each, and their physics
wrappers weakly. The cluster transfers the whole partition to the other node.

The report records process IDs, live lookups and the retired source objects.
It drains the normal game callback queue and forces collections before and
after releasing the strong reference. Surviving weak witnesses have other
possible roots. The grid retention verdict stays inconclusive when the weak-only
grid survives or the held grid survives release. The sampled block and physics
observations describe those individual objects. They do not measure heap size.
