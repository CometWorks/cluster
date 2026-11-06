"""SE-1133: an open maintenance panel across a handover.

``P3-HND-PANEL`` - a LargeCrate with its right maintenance panel opened on node-1, its partition handed to node-2
(clientless). The handover gate serializes the imported crate before the panel's next-frame update has applied its
saved state. Without the fix the target re-emits ``Open`` false, the gate aborts on the one residual
``.../ComponentData[N]/Component/Open: true != false`` and the partition stays on node-1, as Alpha Station's did on
octahedron-test (2026-11-06, partition 4728). With the fix the handover commits with no residual and the panel is
still open on node-2. The node self-test section ``maintenance-panel-pending`` (the same frame, in process) runs on
node-2 as well.
"""

from __future__ import annotations

import json

from cluster import ClusterError
from scenarios import _all_partitions, _force_handover
from scenarios_blocks import _identity, partitions_of, spawn
from scenarios_core import _lines
from scenarios_ext import _gate_residuals, node_self_test
from scenarios_wa_ops import _poll, _require

PANEL = "LargeCrate01_right"
OPEN = ("Opening", "Open")


def _panels(bench, node: str, block: int, open_panel: str | None = None) -> dict:
    query = f"/panel-test?block={block}" + (f"&open={open_panel}" if open_panel else "")
    return bench.cluster.node(node, query, "POST" if open_panel else "GET")


def _right(answer: dict) -> dict:
    return next((panel for panel in answer.get("panels") or [] if panel["subtype"] == PANEL), {})


def p3_hnd_panel(bench) -> None:
    """SE-1133: hand over a partition holding a LargeCrate with an open maintenance panel."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition move is the forced handover

    owner = _identity(bench, "se1133-owner")
    answer = spawn(bench, [{"key": "crate", "typeId": "TerminalBlock", "subtype": "LargeCrate"}], owner)
    _require(bench, "the crate grid spawned", answer.get("built"), json.dumps(answer)[:400])
    grid, block = int(answer["built"][0]["grid"]), int(answer["built"][0]["block"])

    bench.record("10-open", _panels(bench, "node-1", block, PANEL))
    opened = _poll(lambda: (lambda right: right if right.get("status") in OPEN and right.get("serializedOpen") else None)(
        _right(_panels(bench, "node-1", block))), 30, 1)
    _require(bench, f"the {PANEL} panel is open on node-1", opened, json.dumps(_panels(bench, "node-1", block)))

    placed = _poll(lambda: (lambda owners: owners.get(grid) if owners.get(grid, ("", 0))[0] == "node-1" else None)(
        partitions_of(bench, [grid])), 120, 3)
    _require(bench, "the crate grid has its own partition on node-1", placed, json.dumps(partitions_of(bench, [grid])))
    _, partition = placed
    bench.record("20-setup", {"grid": grid, "block": block, "partition": partition, "panel": opened})
    print(f"  [INFO] grid={grid} block={block} partition={partition} panel={opened}")

    try:
        _force_handover(bench, "node-1", "node-2", partition)
        bench.record("30-handover", {"answered": True})
    except ClusterError as error:  # an aborted handover answers with an error
        bench.record("30-handover", {"error": str(error)[:2000]})
        print(f"  [INFO] handover: {str(error)[:300]}")
    arrived = _poll(lambda: _all_partitions(bench).get(partition) == "node-2" or None, 60, 2)
    residuals = _lines(bench, "node-2", rf"handover residual .*partition={partition} ")
    bench.record("40-residuals", residuals)
    for line in residuals:
        print(f"  [INFO] {line[-200:]}")
    bench.check("the handover gate reported no residual on node-2", not residuals, " | ".join(residuals)[:600])
    bench.check("the gate verified the import with 0 residuals", _gate_residuals(bench, "node-2", partition) == 0,
                str(_gate_residuals(bench, "node-2", partition)))
    bench.check("the partition committed to node-2", bool(arrived), json.dumps(_all_partitions(bench).get(partition)))

    after = _panels(bench, "node-2", block) if arrived else {"found": False}
    bench.record("50-target-panels", after)
    right = _right(after)
    bench.check(f"the {PANEL} panel is still open on node-2", right.get("status") in OPEN and right.get("serializedOpen"),
                json.dumps(after))
    left = next((panel for panel in after.get("panels") or [] if panel["subtype"] != PANEL), {})
    bench.check("the other panel is still closed on node-2", left.get("status") == "Closed" and not left.get("serializedOpen"),
                json.dumps(after))

    result = node_self_test(bench, "maintenance-panel-pending", node="node-2")
    bench.check("node self-test section `maintenance-panel-pending` passes",
                bool(result.get("ok")) and "maintenance-panel-pending" in (result.get("ran") or []), json.dumps(result)[:400])
    bench.exercised(grid=grid, partition=partition, residuals=len(residuals), arrived=bool(arrived), target=after)


SCENARIOS_PANEL = {
    "P3-HND-PANEL": (p3_hnd_panel, "SE-1133: an open LargeCrate maintenance panel survives the handover gate", "3"),
}
