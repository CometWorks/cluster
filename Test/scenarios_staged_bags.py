"""SE-1313: the staged handover import builds a partition of many inventory bags without corrupting game state.

* ``P2-STAGED-BAGS`` - 24 astronaut backpacks and 4 small armor cubes rest on node-1 as one partition, which is
  then forced node-1 -> node-2 and back four times (eight staged imports of 28 entities). Every import must go
  through on the first try, the bags must all arrive, and neither node may log an ``Entity init`` failure.
  Before the fix the import built its entities on parallel worker threads, and every bag's physics component
  registers an entity-event listener in the game's static, unlocked registry during Init
  (MyEntityContainerEventExtensions.RegisteredListeners): the dictionary got corrupted, bags failed Entity init
  with an IndexOutOfRangeException and the import was thrown away (live octahedron-test 2026-11-09: five bags kept
  a partition from moving for four tries).

The ``handover_import`` lines of every leg are recorded (``imports``) with their ``stageWallMs`` and
``totalWallMs``, the import times the fix's serial builder can change. No timers to shorten: a forced handover
starts at once.
"""

from __future__ import annotations

import re

from cluster import ClusterError
from scenarios import _all_partitions, _force_handover
from scenarios_core import _lines, _partitions
from scenarios_wa_ops import _poll, _require

BAGS = 24
CUBES = 4
ROUND_TRIPS = 4
ORIGIN = (750_000.0, 0.0, 0.0)
BAD = re.compile(r"Entity init|could not be built|non-concurrent collections|IndexOutOfRangeException")


def _present(bench, node: str, bags: list[int]) -> int:
    return len(bench.cluster.node(node, "/bag-test-state?ids=" + ",".join(map(str, bags)))["present"])


def p2_staged_bags(bench) -> None:
    """A partition of many inventory bags moves back and forth through the staged import, every try."""
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    x, y, z = ORIGIN
    bags = bench.cluster.node("node-1", f"/bag-test-spawn?x={x}&y={y}&z={z}&count={BAGS}&spacing=2", "POST")["bags"]
    _require(bench, f"SETUP: {BAGS} bags spawned on node-1", len(bags) == BAGS, str(bags))
    for index in range(CUBES):
        bench.cluster.node("node-1", f"/trash-test-spawn?x={x + index * 15}&y={y + 20}&z={z}&blocks=8", "POST")
    members = BAGS + CUBES

    def one_partition():
        rows = [p for p in _partitions(bench, "node-1") if (p.get("memberCount") or 0) >= members]
        return rows[0]["id"] if rows else None
    partition = _poll(one_partition, 120, 2)
    _require(bench, f"SETUP: the bags and cubes are one partition of {members} on node-1", partition is not None,
             str(_partitions(bench, "node-1"))[:300])

    legs = []
    for trip in range(ROUND_TRIPS):
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            error = None
            try:
                _force_handover(bench, source, target, partition)
            except ClusterError as e:
                error = str(e)[:300]
            owner = _poll(lambda: _all_partitions(bench).get(partition), 30, 1)
            arrived = owner == target and _present(bench, target, bags) == BAGS
            legs.append({"trip": trip, "source": source, "target": target, "arrived": arrived, "owner": owner,
                         "error": error})
            if owner != target:  # a failed leg leaves the partition where it was: carry on from there
                break

    imports = [line for node in ("node-1", "node-2") for line in _lines(bench, node, rf"handover_import .*partition={partition} ")]
    bad = {node: [line for line in _lines(bench, node, r".") if BAD.search(line)] for node in ("node-1", "node-2")}
    bench.record("legs", legs)
    bench.record("imports", imports)
    bench.record("bad-lines", {node: lines[:20] for node, lines in bad.items()})
    ok = sum(1 for leg in legs if leg["arrived"])
    bench.exercised(legs=len(legs), arrived=ok, imports=len(imports),
                    initFailures={node: len(lines) for node, lines in bad.items()})
    bench.check(f"all {2 * ROUND_TRIPS} handovers of the {members}-member partition went through on the first try",
                ok == 2 * ROUND_TRIPS and len(legs) == 2 * ROUND_TRIPS, str(legs)[:500])
    for node, lines in bad.items():
        bench.check(f"{node}: no Entity init failure in the log", not lines, " | ".join(lines[:3])[:500])


SCENARIOS_STAGED_BAGS = {
    "P2-STAGED-BAGS": (p2_staged_bags, "SE-1313 a partition of 24 inventory bags moves through the staged import every try", "2"),
}
