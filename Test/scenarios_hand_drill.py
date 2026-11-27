"""SE-1940: a character holding a hand drill is handed over without stopping the target's game loop.

``P2-HND-HANDDRILL`` - a character standing beside a small grid on node-1 holds a hand drill, in survival mode on
both nodes, as on the live cluster. Their partition is forced node-1 -> node-2 and back several times (clientless).
Every leg must commit, neither node may log a game loop exception, and the character must arrive with the drill
in its hand and the drill's owner set.

The import Adds the character, and MyCharacter.OnAddedToScene Adds the drill its Init built as a top-level entity
of its own. The drill gets its owner only when the character's first UpdateOnceBeforeFrame equips it, and the
import's freeze holds that update until the thaw. Without the fix the freeze held the character but not the drill,
so the drill ran MyHandDrill.UpdateBeforeSimulation100 without an owner and threw a NullReferenceException that
stopped the node (node-0, partition 3890, 2026-11-25). That update comes every 100 frames, so one leg hits it
only when the frozen window spans one; the round trips make a miss of every leg unlikely.
"""

from __future__ import annotations

import json

from cluster import ClusterError
from scenarios import _all_partitions, force_handover
from scenarios_blocks import ORIGIN, _identity, partitions_of, spawn
from scenarios_core import _lines
from scenarios_wa_ops import _poll, _require

FIXTURES = [{"key": "cargo", "typeId": "CargoContainer", "subtype": "LargeBlockSmallContainer"}]
ROUND_TRIPS = 5
BAGS = 12
# The live import of partition 3890 activated its entities in slices over a second of frames, so the drill's
# 100th-frame update fell inside the target's frozen window. The bench's two-entity import is frozen ~4 frames;
# bags and one paced entity per slice stretch it over seconds, the way a big partition does.
PACING = {"import.activatePerFrame": "1", "import.activateMsPerSecond": "2"}
CRASH = r"game loop exception"


def _state(bench, node: str, character: int) -> dict:
    return bench.cluster.node(node, f"/hand-drill-test-state?id={character}")


def _configure(bench, node: str, values: dict) -> None:
    control = {item.node_id: item for item in bench.cluster.config.nodes}[node].control
    bench.cluster._request("PUT", "/cluster/config", control, 30, dict(bench.cluster.node(node, "/config")["values"], **values))
    _require(bench, f"SETUP: {node} paces its import activation",
             _poll(lambda: all(bench.cluster.node(node, "/config")["values"].get(key) == value
                               for key, value in values.items()) or None, 30, 1) is not None)


def p2_hnd_handdrill(bench) -> None:
    """SE-1940: hand over a character holding a hand drill back and forth; the target survives every import."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition moves are the forced handovers
    for node in ("node-1", "node-2"):
        survival = bench.cluster.node(node, "/hand-drill-test-survival", "POST")
        _require(bench, f"SETUP: {node} runs in survival mode", survival.get("survival"), json.dumps(survival))
        _configure(bench, node, PACING)

    owner = _identity(bench, "se1940-driller")
    answer = spawn(bench, FIXTURES, owner)
    _require(bench, "SETUP: the fixture grid spawned", len(answer.get("built") or []) == 1, json.dumps(answer)[:600])
    grid = int(answer["built"][0]["grid"])
    x, y, z = ORIGIN
    spawned = bench.cluster.node("node-1", f"/hand-drill-test-spawn?x={x}&y={y + 6}&z={z}&owner={owner}", "POST")
    bench.record("10-spawned", spawned)
    character = int(spawned.get("id") or 0)
    bags = bench.cluster.node("node-1", f"/bag-test-spawn?x={x}&y={y - 6}&z={z}&count={BAGS}&spacing=2", "POST")["bags"]
    _require(bench, "SETUP: the character holds a hand drill it owns",
             spawned.get("tool") == "MyHandDrill" and spawned.get("toolOwner") and spawned.get("toolInScene"),
             json.dumps(spawned))

    def placed():
        snapshot = bench.diag("entities")
        rows = {entity: bench.entity_owner(snapshot, entity) for entity in (grid, character)}
        places = {entity: found[0] for entity, found in rows.items() if len(found) == 1}
        if len(places) == 2 and len({place for place in places.values()}) == 1 and places[grid][0] == "node-1":
            return places[grid][1]
        return None
    partition = _poll(placed, 120, 3)
    _require(bench, "SETUP: the grid and the character are one partition on node-1", partition is not None,
             json.dumps(partitions_of(bench, [grid, character])))
    together = _poll(lambda: (lambda snapshot: all(bench.entity_owner(snapshot, bag) == [("node-1", partition)]
                                                  for bag in bags) or None)(bench.diag("entities")), 120, 3)
    _require(bench, f"SETUP: the {BAGS} bags are in the same partition", together is not None)
    print(f"  [INFO] grid={grid} character={character} partition={partition}")

    legs = []
    for trip in range(ROUND_TRIPS):
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            error = None
            try:
                force_handover(bench, source, target, partition)
            except ClusterError as e:
                error = str(e)[:300]
            _poll(lambda: _all_partitions(bench).get(partition) == target or None, 60, 1)
            owner_node = _all_partitions(bench).get(partition)
            try:
                state = _state(bench, target, character)
            except ClusterError as e:
                state = {"error": str(e)[:300]}
            legs.append({"trip": trip, "source": source, "target": target, "owner": owner_node, "error": error,
                         "state": state})
            print(f"  [INFO] leg {source}->{target}: owner={owner_node} state={json.dumps(state)} error={error}")
            if owner_node != target:  # the target is gone or the import failed: stop here
                break
        if legs[-1]["owner"] != legs[-1]["target"]:
            break

    crashes = {node: [line for line in _lines(bench, node, CRASH)][:10] for node in ("node-1", "node-2")}
    bench.record("20-legs", legs)
    bench.record("30-crashes", crashes)
    ok = [leg for leg in legs if leg["owner"] == leg["target"]]
    held = [leg for leg in ok if leg["state"].get("tool") == "MyHandDrill"
            and leg["state"].get("toolOwner") == character]
    bench.exercised(legs=len(legs), committed=len(ok), held=len(held), partition=partition,
                    crashes={node: len(lines) for node, lines in crashes.items()})
    for node, lines in crashes.items():
        bench.check(f"{node}: no game loop exception in the log", not lines,
                    " | ".join(lines)[:600])
    bench.check(f"all {2 * ROUND_TRIPS} handovers of the partition committed",
                len(ok) == 2 * ROUND_TRIPS, json.dumps(legs)[:800])
    bench.check("the character arrived holding its drill, the drill's owner set, after every leg",
                len(held) == len(legs), json.dumps([leg["state"] for leg in legs])[:800])


SCENARIOS_HAND_DRILL = {
    "P2-HND-HANDDRILL": (p2_hnd_handdrill, "SE-1940 a character holding a hand drill is handed over and the target survives", "2"),
}
