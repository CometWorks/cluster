"""SE-1999: Grid Backups on a cluster. One shared backup folder, each grid backed up only by the node that owns it, and
no backups while a node starts or drains, or while a grid's partition moves.

    python Test/grid_backups_scenario.py GRID_BACKUPS_REPO     (with Test/'s virtualenv python, its host.env done)

GRID_BACKUPS_REPO is a CometWorks/grid-backups checkout, built here against MAGNETAR_HOME and loaded on every process
as a local plugin. WA + 2 regular nodes, no clients, the package's example world. The plugin scans every second and
backs up every grid it may on each scan (nobody's and NPC grids too, no minimum delay), so a backup in a window it
must hold shows up. ClusterSettleSeconds is SETTLE. Each backup's side file names the node that took it and when.

P2-GRID-BACKUPS:
  start      no node backs up a grid before SETTLE seconds of Serving, and every node logs the hold
  owners     a grid spawned on node-1 and one on node-2 are backed up by their own node only, never by the WA
  handover   node-1's grid moves to node-2 (clientless): node-1 stops, node-2 waits SETTLE before its first backup,
             while it keeps backing up its own grid; both nodes' backups of the moved grid sit in one shared folder
  drain      node-2 is closed: it backs up no grid of a partition once the drain released it (the drain freezes it
             first), and node-1 waits SETTLE after it loads them before it backs them up
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(sys.argv[1]).resolve()
TEST_DIR = Path(__file__).resolve().parent
sys.path[:0] = [str(TEST_DIR), str(TEST_DIR.parent / "Cli")]

import bench as bench_module  # noqa: E402  (loads host.env, sets MAGNETAR_HOME)
from cluster import ClusterError, wait_until  # noqa: E402
from scenarios_core import _handover_url, _lines  # noqa: E402
from scenarios_recovery import _epoch, _wait_exit  # noqa: E402

_TEST_ROOT = bench_module.HOST.test_root
ROOT = _TEST_ROOT.with_name(_TEST_ROOT.name + "-grid-backups")
PLUGINS = ROOT / "plugins"
QUASAR_DATA = ROOT / "quasar"
BLUEPRINT = ROOT / "blueprint.sbc"
PLUGIN_ID = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C"
NODES = ["node-1", "node-2"]
PROCESSES = ["wa-1", *NODES]
SETTLE = 20
# A backup is stamped when it is written; the scan that queued it may be a second older
SLACK = 2
CONFIG = {
    "ScanIntervalSeconds": 1,
    "MinMinutesSinceLastSave": 0,
    "MaxMinutesSinceLastSave": 0,
    "BackupIfSteady": True,
    "SteadySeconds": 0,
    "MaxBlockCountChange": 0,
    "BackupNobodyGrids": True,
    "BackupNpcGrids": True,
    "MinBlocksForBackup": 0,
    "NumberOfBackupSaves": 1000,
    "NumberOfDailyBackupSaves": 0,
    "DeleteBackupsOlderThanDays": 29,
    "ClusterSettleSeconds": SETTLE,
}


def build() -> None:
    """Grid Backups as a local plugin (DLL + manifest) for CLUSTER_EXTRA_PLUGINS, and its config"""
    out = PLUGINS / ".build"
    subprocess.run(
        ["nice", "dotnet", "build", str(REPO / "ServerPlugin/ServerPlugin.csproj"), "-c", "Release",
         "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = PLUGINS / "GridBackups"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "GridBackups.dll").write_bytes((out / "GridBackups.dll").read_bytes())
    (folder / "GridBackups.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{PLUGIN_ID}</Id><FriendlyName>GridBackups</FriendlyName><RepoId>CometWorks/grid-backups</RepoId>"
        "<Commit>0000000000000000000000000000000000000000</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    # Every process reads GridBackups/GridBackups.cfg under QUASAR_DATA_DIR
    config = QUASAR_DATA / "GridBackups"
    config.mkdir(parents=True, exist_ok=True)
    values = "".join(f"<{k}>{str(v).lower() if isinstance(v, bool) else v}</{k}>" for k, v in CONFIG.items())
    (config / "GridBackups.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<GridBackupsConfig '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        f'xmlns:xsd="http://www.w3.org/2001/XMLSchema">{values}</GridBackupsConfig>\n',
        encoding="utf-8",
    )
    # One battery, nobody's
    BLUEPRINT.write_text(
        '<?xml version="1.0"?>\n<Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Gb1999" />'
        "<CubeGrids><CubeGrid><SubtypeName /><EntityId>0</EntityId>"
        "<PersistentFlags>InScene</PersistentFlags><GridSizeEnum>Large</GridSizeEnum><CubeBlocks>"
        '<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_BatteryBlock">'
        "<SubtypeName>LargeBlockBatteryBlock</SubtypeName></MyObjectBuilder_CubeBlock>"
        "</CubeBlocks><DisplayName>Gb1999</DisplayName></CubeGrid></CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>\n",
        encoding="utf-8",
    )


def backups(bench) -> list[dict]:
    """Every backup's side file under the shared storage, with its path"""
    shared = bench.cluster.config.root / "shared" / "plugins"
    found = []
    for path in shared.glob("*/GridBackups/*/*/*.json"):
        try:
            info = json.loads(path.read_text())
        except (OSError, ValueError):
            continue  # being written
        info["path"] = path
        info["taken"] = datetime.fromisoformat(info["takenUtc"].replace("Z", "+00:00")).timestamp()
        found.append(info)
    return found


def of(found: list[dict], grid: int | None = None, node: str | None = None, after: float = 0) -> list[dict]:
    return sorted(
        (b for b in found
         if (grid is None or b["gridId"] == grid) and (node is None or b.get("node") == node) and b["taken"] > after),
        key=lambda b: b["taken"],
    )  # fmt: skip


def run(bench) -> None:
    cluster = bench.cluster
    cluster.kill()
    cluster.prepare(wipe=True)
    # The shared storage outlasts prepare(): no backups of an earlier run
    shutil.rmtree(cluster.config.root / "shared", ignore_errors=True)

    # When each node is first seen Serving: no node may back up before SETTLE seconds of it
    serving: dict[str, float] = {}
    done = threading.Event()

    def watch() -> None:
        while not done.is_set() and len(serving) < len(NODES):
            for node in NODES:
                if node in serving:
                    continue
                try:
                    if cluster.node(node, "/node-state", timeout=2).get("state") == "Serving":
                        serving[node] = time.time()
                except (ClusterError, OSError, ValueError):
                    pass
            time.sleep(0.25)

    watcher = threading.Thread(target=watch, daemon=True)
    watcher.start()
    try:
        cluster.start(PROCESSES)
        cluster.wait_ready(nodes=PROCESSES, serving=True)
        wait_until(lambda: len(serving) == len(NODES), 60, "both nodes seen Serving")
    finally:
        done.set()
    bench.record("serving", serving)
    # Far grids would go offline at once
    cluster.handover_config({"offline.enabled": "false"})

    def spawn(node: str, x: float) -> int:
        reply = cluster.node(
            node, f"/blueprint-test-spawn?path={BLUEPRINT}&x={x}&y=0&z=0&owner=0&static=true&name=Gb1999-{node}", "POST"
        )
        grid = reply["grids"][0]
        wait_until(lambda: cluster.node(node, f"/partition-of?entity={grid}").get("partition"), 60,
                   f"the grid in a partition of {node}")  # fmt: skip
        return grid

    a = spawn("node-1", 60_000)
    b = spawn("node-2", -60_000)
    bench.record("grids", {"a": a, "b": b})
    wait_until(lambda: of(backups(bench), a, "node-1") and of(backups(bench), b, "node-2"), SETTLE + 60,
               "backups of both grids by their nodes")  # fmt: skip

    # -- start -----------------------------------------------------------------------------------
    found = backups(bench)
    for node in NODES:
        first = of(found, node=node)
        early = [b for b in first if b["taken"] < serving[node] + SETTLE - SLACK]
        bench.check(
            f"start: {node} backed up nothing in its first {SETTLE} s of Serving",
            bool(first) and not early,
            f"first at {first[0]['taken'] - serving[node]:.1f} s of {len(first)}" if first else "no backups",
        )
        bench.check(
            f"start: {node} logged the hold while starting",
            any("node starting" in line for line in _lines(bench, node, "Backups on hold")),
        )
    world_grids = {g["gridId"] for g in found} - {a, b}
    bench.check(
        "start: the world's own grids were backed up after the hold, so the start check is not empty",
        bool(world_grids),
        f"{len(world_grids)} grids",
    )

    # -- owners ----------------------------------------------------------------------------------
    nodes_of = {}
    for g in found:
        nodes_of.setdefault(g["gridId"], set()).add(g.get("node"))
    bench.record("nodes-per-grid", {str(k): sorted(map(str, v)) for k, v in nodes_of.items()})
    bench.check("owners: before any handover each grid is backed up by one node only",
                all(len(v) == 1 for v in nodes_of.values()), str(nodes_of))  # fmt: skip
    bench.check("owners: the World Authority backed up nothing", not of(found, node="wa-1"))
    bench.check("owners: node-1's grid only by node-1, node-2's only by node-2",
                nodes_of.get(a) == {"node-1"} and nodes_of.get(b) == {"node-2"}, str(nodes_of))  # fmt: skip

    # -- handover --------------------------------------------------------------------------------
    partition = cluster.node("node-1", f"/partition-of?entity={a}")["partition"]
    t0 = time.time()
    bench.record("handover", cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))
    t1 = time.time()
    wait_until(lambda: cluster.node("node-2", f"/partition-of?entity={a}").get("partition"), 60, "A on node-2")
    wait_until(lambda: of(backups(bench), a, "node-2"), SETTLE + 60, "node-2's first backup of A")
    time.sleep(3)
    found = backups(bench)
    bench.record("handover-times", {"t0": t0, "t1": t1})
    late = of(found, a, "node-1", after=t1 + SLACK)
    bench.check("handover: node-1 backed A up no more once it left", not late, f"{len(late)} after the handover")
    first = of(found, a, "node-2")
    bench.check(
        f"handover: node-2's first backup of A came {SETTLE} s after the handover began or later",
        first[0]["taken"] >= t0 + SETTLE - SLACK,
        f"{first[0]['taken'] - t0:.1f} s after it began, the handover took {t1 - t0:.1f} s",
    )
    meanwhile = of(found, b, "node-2", after=t1 + SLACK)
    meanwhile = [x for x in meanwhile if x["taken"] < first[0]["taken"]]
    bench.check("handover: node-2 kept backing up its own grid B meanwhile", bool(meanwhile),
                f"{len(meanwhile)} backups of B")  # fmt: skip
    folders = {x["path"].parent for x in of(found, a)}
    bench.check("handover: both nodes' backups of A are in one shared folder",
                len(folders) == 1 and {x.get("node") for x in of(found, a)} == {"node-1", "node-2"},
                str(sorted(map(str, folders))))  # fmt: skip

    # -- drain -----------------------------------------------------------------------------------
    partitions = {name: cluster.node("node-2", f"/partition-of?entity={grid}")["partition"] for name, grid in (("A", a), ("B", b))}
    t2 = time.time()
    bench.record("close", cluster.post(f"/registry/nodes/close?node=node-2&epoch={_epoch(bench, 'node-2')}"
                                       "&forceAfterSeconds=0"))  # fmt: skip
    _wait_exit(bench, "node-2")
    # Log stamps are this host's local time
    released = {}
    for line in _lines(bench, "node-2", r"drain released partition [0-9]+ "):
        match = re.search(r"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+): .*drain released partition (\d+) ", line)
        if match is None:
            continue  # the same line without a stamp, from the process' console log
        stamp, partition = match.groups()
        released[int(partition)] = datetime.strptime(stamp, "%Y-%m-%d %H:%M:%S.%f").timestamp()
    wait_until(lambda: of(backups(bench), a, "node-1", after=t2) and of(backups(bench), b, "node-1", after=t2),
               SETTLE + 120, "node-1 backing up both grids again")  # fmt: skip
    found = backups(bench)
    bench.record("drain", {"t2": t2, "partitions": partitions, "released": released})
    for name, grid in (("A", a), ("B", b)):
        out = released.get(partitions[name])
        late = of(found, grid, "node-2", after=(out or t2))
        bench.check(f"drain: node-2 backed {name} up no more once the drain released its partition",
                    out is not None and not late, f"released {out and out - t2:.1f} s after the close, {len(late)} later")  # fmt: skip
        back = of(found, grid, "node-1", after=t2)
        bench.check(
            f"drain: node-1 backed {name} up {SETTLE} s after the drain released it or later",
            out is not None and back[0]["taken"] >= out + SETTLE - SLACK,
            f"{back[0]['taken'] - (out or t2):.1f} s after the release",
        )
    # A drained node may exit without unloading its session, so this is evidence, not a check
    bench.record("node-2-holds", _lines(bench, "node-2", "Backups (on hold|resumed)"))
    bench.exercised(backups=len(found), grids=len(nodes_of), handover=f"{t1 - t0:.1f}s")


run.regular_nodes = 2


def main() -> int:
    ROOT.mkdir(parents=True, exist_ok=True)
    build()
    os.environ["CLUSTER_EXTRA_PLUGINS"] = str(PLUGINS / "GridBackups")
    os.environ["QUASAR_DATA_DIR"] = str(QUASAR_DATA)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-grid-backups")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run, "P2-GRID-BACKUPS", "SE-1999: Grid Backups on a cluster: shared folder, owners, holds", "3", results
    )
    print(json.dumps({"passed": outcome.passed, "error": outcome.error, "results": str(results)}))
    print(f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed")
    for item in outcome.checks:
        if not item.passed:
            print("FAILED: " + item.label + (f" - {item.detail}" if item.detail else ""))
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
