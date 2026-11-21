"""SE-1759: handover of a partition holding big grids. The target answers POST /cluster/import only after staging,
activating and verifying every entity, and the Gateway gave that call its node client's fixed 10 s timeout: a
60,000-block grid took 10.6 s, the call was cancelled and node-2 discarded the import, so the partition never moved.
Most of it was the round-trip gate's walk, quadratic in a list of blocks without EntityIds.

P3-HND-BIG spawns one slowly spinning 60,000-block light armor grid on node-1 (the test plugin's /trash-test-spawn,
no client), hands its partition over to node-2 clientless and checks that it arrived there once and passed the
gate, then runs the node self-test section `big-grid-compare` (the walk stays linear). P3-HND-BIG-2 hands over two
such grids in one partition. Both record the numbers: the sector's stored and body bytes, the Gateway's import
timeout, the target's compare and total import wall time (its handover_import line) and the route's own time.
"""
from __future__ import annotations

import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import _handover_url, _lines, _partitions
from scenarios_ext import _gate_residuals, node_self_test

BLOCKS = 60_000
FAR = 1_400_000.0  # far from the world's own grids, so the partition holds only what the scenario spawns


def _where(bench, entity: int) -> list[tuple[str, int]]:
    """Every (node, partition) whose report lists ENTITY: exactly one means no loss and no duplicate."""
    out = []
    for node in ("node-1", "node-2"):
        try:
            rows = _partitions(bench, node)
        except ClusterError:
            continue
        out += [(node, row["id"]) for row in rows if any(m.get("id") == entity for m in row.get("members") or [])]
    return out


def _number(line: str, key: str) -> float | None:
    match = re.search(rf"\b{key}=([0-9.]+)", line)
    return float(match.group(1)) if match else None


def _all_on(bench, grids: list[int], node: str):
    homes = [_where(bench, grid) for grid in grids]
    return homes if all(len(home) == 1 and home[0][0] == node for home in homes) else None


def _big_grid_handover(bench, count: int) -> None:
    bench.start_cluster()
    bench.quiesce()
    grids = []
    for index in range(count):
        # 40 blocks a side (100 m); 150 m apart keeps two of them in one partition. A slow spin keeps the pose
        # moving inside the freeze window, so the gate runs its full document compare as for any ship in use; a
        # still grid imports byte for byte equal and skips it (140 ms instead of seconds).
        answer = bench.cluster.node("node-1", f"/trash-test-spawn?x={FAR + index * 150}&y=0&z=0&static=false"
                                              f"&spin=0.02&blocks={BLOCKS}", "POST", timeout=300)
        if not answer.get("grid"):
            raise ClusterError(f"spawn {index} returned no grid: {answer}")
        grids.append(answer["grid"])
    bench.record("spawned", grids)
    homes = wait_until(lambda: _all_on(bench, grids, "node-1"), 120, "the big grids in node-1 partitions")
    partitions = {home[0][1] for home in homes}
    bench.record("homes", homes)
    if not bench.check(f"the {count} grid(s) of {BLOCKS} blocks share one node-1 partition", len(partitions) == 1,
                       str(homes)):
        return
    partition = partitions.pop()

    started = time.monotonic()
    try:
        answer = bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=600)
        error = None
    except ClusterError as failure:
        answer, error = None, str(failure)[:600]
    route_s = time.monotonic() - started
    bench.record("handover", {"answer": answer, "error": error, "routeSeconds": round(route_s, 1)})
    bench.check("the handover route answered success", error is None, error or "")
    try:
        moved = wait_until(lambda: _all_on(bench, grids, "node-2"), 120, "the big grids once on node-2")
    except ClusterError:
        moved = None
    bench.check("every big grid is on node-2, once", moved is not None, str([_where(bench, g) for g in grids]))
    residuals = _gate_residuals(bench, "node-2", partition)
    bench.check("node-2's round-trip gate verified the import with no residuals", residuals == 0, repr(residuals))

    imports = _lines(bench, "node-2", rf"handover_import .*partition={partition} ")
    gateway = [line for line in _lines(bench, "gateway", rf"partition={partition}\b")
               if re.search(r"captured|import|[Tt]imeout|canceled|failed", line)]
    numbers = {
        "routeSeconds": round(route_s, 1),
        "sectorBytes": next((_number(x, "sectorBytes") for x in gateway if "sectorBytes=" in x), None),
        "bodyBytes": next((_number(x, "bodyBytes") for x in gateway if "bodyBytes=" in x), None),
        "importTimeoutS": next((_number(x, "importTimeoutS") for x in gateway if "importTimeoutS=" in x), None),
        "compareMs": _number(imports[-1], "compareMs") if imports else None,
        "totalWallMs": _number(imports[-1], "totalWallMs") if imports else None,
    }
    bench.record("numbers", numbers)
    bench.record("node2-import-lines", imports)
    bench.record("gateway-lines", gateway[-40:])
    bench.exercised(grids=count, blocks=BLOCKS, partition=partition, **numbers)


def p3_hnd_big(bench) -> None:
    _big_grid_handover(bench, 1)
    result = node_self_test(bench, "big-grid-compare", node="node-2")
    bench.check("node self-test section `big-grid-compare` passes (the gate's walk is linear in the blocks)",
                bool(result.get("ok")) and "big-grid-compare" in (result.get("ran") or []), str(result)[:400])


def p3_hnd_big_2(bench) -> None:
    _big_grid_handover(bench, 2)


SCENARIOS_BIG_GRID = {
    "P3-HND-BIG": (p3_hnd_big, "SE-1759: clientless handover of a partition with a 60,000-block grid", "3"),
    "P3-HND-BIG-2": (p3_hnd_big_2, "SE-1759: clientless handover of a partition with two 60,000-block grids", "3"),
}
