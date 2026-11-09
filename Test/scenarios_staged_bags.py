"""SE-1313: the staged handover import builds a partition of many inventory bags without corrupting game state.

* ``P2-STAGED-BAGS`` - 120 astronaut backpacks and 4 small armor cubes rest on node-1 as one partition, which is
  then forced node-1 -> node-2 and back four times (eight staged imports of 124 entities). Every import must go
  through on the first try, the bags must all arrive, and neither node may log an ``Entity init`` failure.
  Before the fix the import built its entities on parallel worker threads, and every bag's physics component
  registers an entity-event listener in the game's static, unlocked registry during Init
  (MyEntityContainerEventExtensions.RegisteredListeners): the dictionary got corrupted, bags failed Entity init
  with an IndexOutOfRangeException and the import was thrown away (live octahedron-test 2026-11-09: five bags kept
  a partition from moving for four tries).

* ``P2-STAGED-SHIPS`` - the same with 24 vanilla cargo ships (8 prefabs, 137 to 397 blocks each) instead of the
  bags and cubes: a partition shaped like the live ones whose builds take seconds, for the import times.

The ``handover_import`` lines of every leg are recorded (``imports``) with their ``stageWallMs`` and
``totalWallMs``, the import times that the way the staged import builds can change. No timers to shorten: a
forced handover starts at once.
"""

from __future__ import annotations

import re
from pathlib import Path
from urllib.parse import quote

from bench import HOST
from cluster import ClusterError
from scenarios import _all_partitions, _force_handover
from scenarios_core import _lines, _partitions
from scenarios_wa_ops import _poll, _require

BAGS = 120  # under partition.oversizeMembers (150): one partition
CUBES = 4
ROUND_TRIPS = 4
ORIGIN = (750_000.0, 0.0, 0.0)
SHIPS = ["C20_Trade_PrivateSail", "C21_Trade_ConsignmentDrone", "C51_Derelict_Shuttle", "C00_Builder_Hauler",
         "C40_Pirate_Vulture", "C23_Trade_SolarBarge", "C30_Military_Escort", "C22_Trade_Merchant"]
BAD = re.compile(r"Entity init|could not be built|non-concurrent collections|IndexOutOfRangeException")


def _present(bench, node: str, bags: list[int]) -> int:
    return len(bench.cluster.node(node, "/bag-test-state?ids=" + ",".join(map(str, bags)))["present"])


def _blueprint(bench, prefab: str) -> Path:
    """A vanilla cargo-ship prefab rewritten as a ship blueprint, for /blueprint-test-spawn."""
    text = (HOST.server / "Content/Data/Prefabs/CargoShips" / f"{prefab}.sbc").read_text(encoding="utf-8-sig")
    for old, new in (("<Prefabs>", "<ShipBlueprints>"), ("</Prefabs>", "</ShipBlueprints>"), ("<Prefab ", "<ShipBlueprint "),
                     ("</Prefab>", "</ShipBlueprint>"), ("MyObjectBuilder_PrefabDefinition", "MyObjectBuilder_ShipBlueprintDefinition")):
        text = text.replace(old, new)
    path = bench.cluster.config.root / f"se1313-{prefab}.sbc"
    path.write_text(text, encoding="utf-8")
    return path


def _staged(bench, bags: int, cubes: int, ships: int) -> None:
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    x, y, z = ORIGIN
    bag_ids = []
    if bags:
        for row in range(0, bags, 20):  # rows of 20, 2 m apart
            bag_ids += bench.cluster.node("node-1", f"/bag-test-spawn?x={x}&y={y + row // 10}&z={z}&count={min(20, bags - row)}&spacing=2",
                                          "POST")["bags"]
        _require(bench, f"SETUP: {bags} bags spawned on node-1", len(bag_ids) == bags, str(bag_ids))
    for index in range(cubes):
        bench.cluster.node("node-1", f"/trash-test-spawn?x={x + index * 15}&y={y + 20}&z={z}&blocks=8", "POST")
    grids = 0
    for index in range(ships):
        path = _blueprint(bench, SHIPS[index % len(SHIPS)])
        sx, sy = x + (index % 5) * 150, y + 100 + (index // 5) * 150
        grids += len(bench.cluster.node("node-1", f"/blueprint-test-spawn?path={quote(str(path))}&x={sx}&y={sy}&z={z}", "POST",
                                        timeout=120)["grids"])
    members = bags + cubes + grids

    def one_partition():
        rows = [p for p in _partitions(bench, "node-1") if (p.get("memberCount") or 0) >= members]
        return rows[0]["id"] if rows else None
    partition = _poll(one_partition, 120, 2)
    _require(bench, f"SETUP: the fixtures are one partition of {members} on node-1", partition is not None,
             str(_partitions(bench, "node-1"))[:300])

    legs = []
    for trip in range(ROUND_TRIPS):
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            error = None
            try:
                _force_handover(bench, source, target, partition)
            except ClusterError as e:
                error = str(e)[:300]
            _poll(lambda: _all_partitions(bench).get(partition) == target or None, 60, 1)
            owner = _all_partitions(bench).get(partition)
            arrived = owner == target and (not bag_ids or _present(bench, target, bag_ids) == bags)
            legs.append({"trip": trip, "source": source, "target": target, "arrived": arrived, "owner": owner,
                         "error": error})
            if owner != target:  # a failed leg leaves the partition where it was: carry on from there
                break

    imports = list({re.search(r"transferId=(\S+)", line).group(1): line[line.index("handover_import"):]  # a node writes
                    for node in ("node-1", "node-2")  # its plugin lines to two logs
                    for line in _lines(bench, node, rf"handover_import transferId=\S+ partition={partition} ")}.values())
    bad = {node: [line for line in _lines(bench, node, r".") if BAD.search(line)] for node in ("node-1", "node-2")}
    bench.record("legs", legs)
    bench.record("imports", imports)
    bench.record("bad-lines", {node: lines[:20] for node, lines in bad.items()})
    ok = sum(1 for leg in legs if leg["arrived"])
    times = [dict(re.findall(r"(stageWallMs|stageInitMsSum|totalWallMs|staged)=([\d.]+)", line)) for line in imports]
    bench.exercised(legs=len(legs), arrived=ok, imports=len(imports), members=members, times=times,
                    initFailures={node: len(lines) for node, lines in bad.items()})
    bench.check(f"all {2 * ROUND_TRIPS} handovers of the {members}-member partition went through on the first try",
                ok == 2 * ROUND_TRIPS and len(legs) == 2 * ROUND_TRIPS, str(legs)[:500])
    for node, lines in bad.items():
        bench.check(f"{node}: no Entity init failure in the log", not lines, " | ".join(lines[:3])[:500])


def p2_staged_bags(bench) -> None:
    """A partition of many inventory bags moves back and forth through the staged import, every try."""
    _staged(bench, bags=BAGS, cubes=CUBES, ships=0)


def p2_staged_ships(bench) -> None:
    """A partition of 24 cargo ships moves back and forth through the staged import, for its import times."""
    _staged(bench, bags=0, cubes=0, ships=24)


SCENARIOS_STAGED_BAGS = {
    "P2-STAGED-BAGS": (p2_staged_bags, "SE-1313 a partition of 120 inventory bags moves through the staged import every try", "2"),
    "P2-STAGED-SHIPS": (p2_staged_ships, "SE-1313 a partition of 24 cargo ships moves through the staged import (import times)", "2"),
}
