"""#1295: a graceful cluster stop ends clean although a drained grid's vanilla BeforeDelete throws.

octahedron-1191, 2026-10-21: four stops in a row lost a regular node half way through its drain. The drain
registered partition 651's Final, closed its grids, and MyCubeGrid.BeforeDelete threw a NullReferenceException
out of the game loop (PartitionPersistence.CloseWithHierarchy <- UpdateDrain). The node never reported Empty,
went Dead by lease expiry, and the CleanShutdown snapshot listed "node:<id>" as dirty.

One regular node + WA, no client. A 27-block grid is spawned far out (/trash-test-spawn) and waited for in a
partition node-1 owns; the test plugin arms a NullReferenceException in that grid's BeforeDelete
(/test-grid-delete-fault); the cluster stops the way the live one does (POST /registry/shutdown/graceful).

* ``P2-SAV-FAULT``     - GridDeleteFaultGuard on (the default): the fault is thrown and logged with its probe,
  node-1 still drains to Empty, the shutdown's Dirty list is empty, every process exits 0.
* ``P2-SAV-FAULT-RED`` - the same with CLUSTER_GRID_DELETE_GUARD=0: node-1 dies in the drain and the shutdown
  lists node:node-1 dirty, which is the live failure. It proves the fixture reaches the fault and the oracle
  can see it.

SE-1491, octahedron-test 2026-11-14: a drain died the same way one physics step after its close, with a
NullReferenceException in MyPhysics.ProcessCollisionFilterRefreshes. The same fixture arms a stale entry in the
collision filter refresh queue at the grid's delete (/test-collision-filter-fault):

* ``P2-SAV-FILTER``     - CollisionFilterRefreshGuard on (the default): the entry is dropped and logged, node-1
  drains to Empty, nothing is dirty, every process exits 0.
* ``P2-SAV-FILTER-RED`` - the same with CLUSTER_COLLISION_FILTER_GUARD=0: node-1 dies with the live stack and the
  shutdown lists node:node-1 dirty.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError, wait_until
from scenarios import _all_partitions
from scenarios_core import _lines, _partitions
from scenarios_recovery import RECOVERY_TIMEOUT

NODE = "node-1"
STARTED = [NODE, "wa-1"]
PROCESSES = (NODE, "wa-1", "gateway")
FAR = 600_000.0


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _home(bench, grid: int) -> int | None:
    """The id of the node-1 partition that holds the grid, once the registry says node-1 owns it."""
    for row in _partitions(bench, NODE):
        if any(member.get("id") == grid for member in row.get("members") or []):
            pid = int(row["id"])
            return pid if _all_partitions(bench).get(pid) == NODE else None
    return None


def _alive(bench, name: str) -> bool:
    process = bench.cluster.processes.get(name)
    return process is not None and process.poll() is None


#: fault -> (test route, guard switch, the line the armed fault leaves, the guard's line). {grid} is filled in.
FAULTS = {
    "delete": (
        "/test-grid-delete-fault",
        "CLUSTER_GRID_DELETE_GUARD",
        r"test fault in MyCubeGrid.BeforeDelete of grid {grid}\b",
        r"grid delete fault: BeforeDelete of grid {grid}\b",
    ),
    "filter": (
        "/test-collision-filter-fault",
        "CLUSTER_COLLISION_FILTER_GUARD",
        r"collision-filter-fault queued a stale refresh in the delete of grid {grid}\b",
        r"collision filter refresh dropped: null entry",
    ),
}


def _fault_run(bench, guard_on: bool, fault: str = "delete") -> dict:
    route, switch, thrown_line, guard_line = FAULTS[fault]
    if not guard_on:
        bench.cluster.config.node_env[switch] = "0"
    bench.start_cluster(nodes=STARTED)
    routes = bench.cluster.node(NODE, "/test-plugin").get("routes") or []
    _require(bench, f"setup: the test plugin serves {route}", f"POST {route}" in routes, f"{len(routes)} routes")
    grid = int(bench.cluster.node(NODE, f"/trash-test-spawn?x={FAR}&y=0&z=0&static=false&blocks=27", "POST")["grid"])
    try:
        partition = wait_until(lambda: _home(bench, grid), 90, f"grid {grid} in a partition {NODE} owns")
    except ClusterError:
        partition = None
    _require(bench, f"setup: grid {grid} is in a partition {NODE} owns", partition)
    bench.record("armed", bench.cluster.node(NODE, f"{route}?entity={grid}", "POST"))

    shutdown = bench.cluster.post(
        f"/registry/shutdown/graceful?timeoutSeconds={RECOVERY_TIMEOUT}&forceAfterSeconds=0",
        timeout=RECOVERY_TIMEOUT + 30,
    )
    bench.record("shutdown", shutdown)
    try:
        wait_until(
            lambda: not any(_alive(bench, name) for name in PROCESSES), RECOVERY_TIMEOUT, "every process to exit"
        )
    except ClusterError:
        pass
    codes = {name: bench.cluster.processes[name].returncode for name in PROCESSES}
    last = (shutdown or {}).get("LastShutdown") or {}
    result = {
        "grid": grid,
        "partition": partition,
        "phase": (shutdown or {}).get("Phase"),
        "dirty": last.get("Dirty"),
        "codes": codes,
        "thrown": bool(_lines(bench, NODE, rf"-fault armed grid={grid}\b"))
        and bool(_lines(bench, NODE, thrown_line.format(grid=grid))),
        "guarded": _lines(bench, NODE, guard_line.format(grid=grid)),
        "crashed": bool(
            _lines(bench, NODE, r"at Sandbox\.Engine\.Physics\.MyPhysics\.ProcessCollisionFilterRefreshes")
        ),
        "unloaded": bool(_lines(bench, NODE, rf"drain final registered and partition unloaded: {partition}\b")),
        "empty": bool(_lines(bench, NODE, r"drain finals registered; node Empty")),
    }
    bench.record("result", result)
    bench.check("the shutdown completed (phase Down)", result["phase"] == 4, json.dumps(shutdown)[:300])
    bench.check("the armed fault was thrown in the drain's close of the grid", result["thrown"])
    return result


def p2_sav_filter(bench) -> None:
    r = _fault_run(bench, guard_on=True, fault="filter")
    bench.check("the guard dropped the stale refresh and logged it", bool(r["guarded"]), "no guard line")
    bench.check("no NullReferenceException in ProcessCollisionFilterRefreshes", not r["crashed"])
    bench.check(
        f"{NODE} unloaded the faulted partition and drained to Empty", r["unloaded"] and r["empty"], json.dumps(r)[:300]
    )
    bench.check("the shutdown left nothing dirty", r["dirty"] == [], json.dumps(r["dirty"]))
    for name, code in r["codes"].items():
        bench.check(f"{name} exited 0", code == 0, f"exit {code}")
    bench.exercised(guard="on", fault="filter", grid=r["grid"], partition=r["partition"], dirty=r["dirty"])


def p2_sav_filter_red(bench) -> None:
    """The earned red: with the guard off the stale entry must end node-1 with the live stack and leave it dirty."""
    r = _fault_run(bench, guard_on=False, fault="filter")
    bench.check("guard OFF: no guard line", not r["guarded"])
    bench.check("guard OFF: node-1 died in ProcessCollisionFilterRefreshes (the live stack)", r["crashed"])
    bench.check(f"guard OFF: {NODE} never drained to Empty", not r["empty"])
    bench.check(
        f"guard OFF: the shutdown lists node:{NODE} dirty", f"node:{NODE}" in (r["dirty"] or []), json.dumps(r["dirty"])
    )
    bench.exercised(guard="off", fault="filter", grid=r["grid"], partition=r["partition"], dirty=r["dirty"])


def p2_sav_fault(bench) -> None:
    r = _fault_run(bench, guard_on=True)
    bench.check(
        "the guard logged the fault with its probe",
        bool(r["guarded"]) and "blocks=27" in r["guarded"][0],
        (r["guarded"] or ["no guard line"])[0][:400],
    )
    bench.check(
        f"{NODE} unloaded the faulted partition and drained to Empty", r["unloaded"] and r["empty"], json.dumps(r)[:300]
    )
    bench.check("the shutdown left nothing dirty", r["dirty"] == [], json.dumps(r["dirty"]))
    for name, code in r["codes"].items():
        bench.check(f"{name} exited 0", code == 0, f"exit {code}")
    bench.exercised(guard="on", grid=r["grid"], partition=r["partition"], dirty=r["dirty"])


def p2_sav_fault_red(bench) -> None:
    """The earned red: with the guard off the same fault must end node-1 and leave it dirty (the live failure)."""
    r = _fault_run(bench, guard_on=False)
    bench.check("guard OFF: no guard line", not r["guarded"])
    bench.check(f"guard OFF: {NODE} never drained to Empty", not r["empty"])
    bench.check(
        f"guard OFF: the shutdown lists node:{NODE} dirty", f"node:{NODE}" in (r["dirty"] or []), json.dumps(r["dirty"])
    )
    bench.check(
        f"guard OFF: {NODE} exited non-zero", r["codes"].get(NODE) not in (0, None), f"exit {r['codes'].get(NODE)}"
    )
    bench.exercised(guard="off", grid=r["grid"], partition=r["partition"], dirty=r["dirty"])


SCENARIOS_DELETE_FAULT = {
    "P2-SAV-FAULT": (
        p2_sav_fault,
        "#1295: a graceful stop ends clean although a drained grid's BeforeDelete throws",
        "3",
    ),
    "P2-SAV-FAULT-RED": (p2_sav_fault_red, "#1295: the same with the guard off leaves the node dirty", "3"),
    "P2-SAV-FILTER": (
        p2_sav_filter,
        "SE-1491: a graceful stop ends clean although a drain's close leaves a stale collision filter refresh",
        "3",
    ),
    "P2-SAV-FILTER-RED": (p2_sav_filter_red, "SE-1491: the same with the guard off leaves the node dirty", "3"),
}
