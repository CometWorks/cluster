"""SE-1998: asteroids turned to the identity orientation online, one at a time (Shared/Cluster/AsteroidTurnService.cs,
ClusterRegistry/AsteroidTurns.cs).

P2-AST-TURN puts four rotated 128^3 asteroids into the premade world, 5 km apart:
  A with nothing near,
  B with a dynamic grid 10 m off its box and an astronaut backpack 5 m off it,
  C with a static grid 20 m off its box,
  D with a dynamic grid 500 m off its box.
With asteroidReset.enabled false nothing turns. Enabled (intervalSeconds 2) while every grid is online, A turns on every
role and B, C and D wait ("online"). Once the grids are in offline partitions, B turns and pins the partitions of its
grid and bag, D turns with no pin (its grid is past memberMeters), and C stays as it is for good ("static"). Waking
B's partitions turns the grid and the bag with the rock: their pose relative to it is the same as before (1 mm, 1e-4).
The grid near D keeps its world pose. A restarted node-2 loads the old poses from the premade world and turns A, B
and D again before it serves.
"""

from __future__ import annotations

import json
import math
import shutil
import time
import xml.etree.ElementTree as ET
from pathlib import Path

from cluster import ClusterError, wait_until
from hostenv import HOST
from scenarios_core import _lines
from scenarios_voxel_reset import _offline_members, _require

NODES = ["node-1", "node-2", "wa-1"]
XSI = "{http://www.w3.org/2001/XMLSchema-instance}"
HALF = 64.0  # Asteroid128-*.vx2: a 128^3 storage
# name: (corner x, vx2, forward, up); the frames are orthonormal and none is the identity.
ROCKS = {
    "TurnRock-A": (20000, "Asteroid128-001.vx2", (0.6, 0.0, -0.8), (0.0, 1.0, 0.0)),
    "TurnRock-B": (25000, "Asteroid128-002.vx2", (0.0, -1.0, 0.0), (0.6, 0.0, 0.8)),
    "TurnRock-C": (30000, "Asteroid128-003.vx2", (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)),
    "TurnRock-D": (35000, "Asteroid128-004.vx2", (-0.8, 0.0, -0.6), (0.0, 1.0, 0.0)),
}
TOLERANCE_M = 0.001
TOLERANCE_AXIS = 1e-4


def _template(bench) -> Path:
    """The packaged Cluster-Simple world with the four rocks added to its sector (and no binary sector cache)."""
    source = HOST.package / "examples/Cluster-Simple"
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-AsteroidTurn"
    shutil.rmtree(target, ignore_errors=True)
    shutil.copytree(source, target)
    (target / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    sector = target / "SANDBOX_0_0_0_.sbs"
    ET.register_namespace("xsi", XSI[1:-1])
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    tree = ET.parse(sector)
    objects = tree.getroot().find("SectorObjects")
    for index, (name, (x, vx2, forward, up)) in enumerate(ROCKS.items()):
        shutil.copy(HOST.server / "Content/VoxelMaps" / vx2, target / f"{name}.vx2")
        rock = ET.SubElement(objects, "MyObjectBuilder_EntityBase", {XSI + "type": "MyObjectBuilder_VoxelMap"})
        ET.SubElement(rock, "SubtypeName")
        ET.SubElement(rock, "EntityId").text = str(72_000_000_000_000_001 + index)
        ET.SubElement(rock, "PersistentFlags").text = "CastShadows InScene"
        pose = ET.SubElement(rock, "PositionAndOrientation")
        ET.SubElement(pose, "Position", {"x": str(x), "y": "0", "z": "0"})
        ET.SubElement(pose, "Forward", dict(zip("xyz", map(str, forward))))
        ET.SubElement(pose, "Up", dict(zip("xyz", map(str, up))))
        ET.SubElement(rock, "StorageName").text = name
        ET.SubElement(rock, "MutableStorage").text = "true"
        ET.SubElement(rock, "ContentChanged").text = "false"
    tree.write(sector, xml_declaration=True, encoding="utf-8")
    return target


def _pose(bench, node: str, storage: str) -> dict:
    return bench.cluster.node(node, f"/asteroid-turn-test-pose?storage={storage}")


def _rock_matrix(bench, node: str, storage: str) -> list[float] | None:
    return _pose(bench, node, storage).get("matrix")


def _aligned(matrix) -> bool:
    return matrix is not None and all(
        abs(a - b) < 1e-6 for a, b in zip(matrix[3:], (0, 0, -1, 0, 1, 0))
    )


def _entity(bench, entity: int) -> tuple[str, list[float]] | None:
    """The entity's pose on whichever role holds it."""
    for node in NODES:
        answer = bench.cluster.node(node, f"/asteroid-turn-test-entity?entity={entity}")
        if answer.get("found"):
            return node, answer["matrix"]
    return None


def _axes(m):
    forward, up = m[3:6], m[6:9]
    right = (forward[1] * up[2] - forward[2] * up[1], forward[2] * up[0] - forward[0] * up[2],
             forward[0] * up[1] - forward[1] * up[0])
    return right, up, [-x for x in forward]


def _local(entity_matrix, rock_matrix):
    """The entity's position, Forward and Up in the rock's frame."""
    right, up, backward = _axes(rock_matrix)
    pivot = rock_matrix[:3]
    rel = [a - b for a, b in zip(entity_matrix[:3], pivot)]

    def to(v):
        return [sum(a * b for a, b in zip(v, axis)) for axis in (right, up, backward)]

    return to(rel), to(entity_matrix[3:6]), to(entity_matrix[6:9])


def _same_local(before, after) -> tuple[bool, float, float]:
    dp = math.dist(before[0], after[0])
    da = max(math.dist(before[1], after[1]), math.dist(before[2], after[2]))
    return dp <= TOLERANCE_M and da <= TOLERANCE_AXIS, dp, da


def _point(rock_matrix, along: str, meters: float) -> list[float]:
    """A point `meters` past the rock's storage box along its own right axis (or up, for along="up")."""
    right, up, _ = _axes(rock_matrix)
    axis = right if along == "right" else up
    return [p + a * (HALF + meters) for p, a in zip(rock_matrix[:3], axis)]


def _turns(bench) -> list[dict]:
    return bench.cluster.get("/registry/asteroid-turns").get("Rows") or []


def p2_ast_turn(bench) -> None:
    bench.cluster.config.world_template = _template(bench)
    bench.start_cluster(nodes=NODES)
    bench.record("config-off", bench.cluster.handover_config({
        "offline.enabled": "false", "asteroidReset.enabled": "false", "asteroidReset.pollSeconds": "2"}))
    rocks = {}
    for name in ROCKS:
        poses = {node: _rock_matrix(bench, node, name) for node in NODES}
        rocks[name] = poses["wa-1"]
        _require(bench, f"setup: {name} is loaded rotated, the same on every role",
                 all(p is not None and not _aligned(p) and max(abs(a - b) for a, b in zip(p, poses["wa-1"])) < 1e-6
                     for p in poses.values()), json.dumps(poses))
    bench.record("rocks-before", rocks)

    # Members near the rocks, all on node-1.
    def spawn_grid(point, static: bool) -> int:
        x, y, z = point
        answer = bench.cluster.node("node-1", f"/trash-test-spawn?x={x}&y={y}&z={z}&static={'true' if static else 'false'}"
                                    "&blocks=8", "POST")
        return int(answer["grid"])

    grid_b = spawn_grid(_point(rocks["TurnRock-B"], "right", 10), False)
    x, y, z = _point(rocks["TurnRock-B"], "up", 5)
    bags = bench.cluster.node("node-1", f"/bag-test-spawn?x={x}&y={y}&z={z}&count=1", "POST")
    bag_b = int(bags["bags"][0])
    grid_c = spawn_grid(_point(rocks["TurnRock-C"], "right", 20), True)
    grid_d = spawn_grid(_point(rocks["TurnRock-D"], "right", 500), False)
    bench.record("members", {"grid_b": grid_b, "bag_b": bag_b, "grid_c": grid_c, "grid_d": grid_d})
    # Let the bag and the grids settle (no gravity out here: they drift at most a little), then stop them in place.
    time.sleep(5)

    # 1. Off: nothing turns.
    time.sleep(8)
    _require(bench, "asteroidReset.enabled false: nothing turns", _turns(bench) == [], json.dumps(_turns(bench)))

    # 2. On, grids online: A turns everywhere, B, C and D wait.
    bench.record("config-on", bench.cluster.handover_config({
        "offline.enabled": "false", "asteroidReset.enabled": "true", "asteroidReset.dryRun": "false",
        "asteroidReset.intervalSeconds": "2", "asteroidReset.statusSeconds": "5", "asteroidReset.pollSeconds": "2"}))
    started = time.time()
    a_turned = wait_until(lambda: all(_aligned(_rock_matrix(bench, n, "TurnRock-A")) for n in NODES) or None, 90,
                          "A turned on every role")
    a_seconds = round(time.time() - started, 1)
    bench.record("a-seconds", a_seconds)
    a_after = _rock_matrix(bench, "wa-1", "TurnRock-A")
    bench.check("A (nothing near) turns on node-1, node-2 and the WA, about its center",
                bool(a_turned) and math.dist(a_after[:3], rocks["TurnRock-A"][:3]) < 1e-6,
                json.dumps(a_after))
    time.sleep(12)
    held = {n: [not _aligned(_rock_matrix(bench, n, r)) for r in ("TurnRock-B", "TurnRock-C", "TurnRock-D")]
            for n in NODES}
    bench.check("B, C and D wait while a grid is online within gridMeters", all(all(v) for v in held.values()),
                json.dumps(held))
    refusals = _lines(bench, "wa-1", r"asteroid_turn refused")
    bench.check("the WA logged the online refusals", any("reason=online:" in x for x in refusals), "\n".join(refusals[-5:]))

    # Poses of B's members relative to B, before.
    before = {}
    for entity in (grid_b, bag_b, grid_d):
        found = _entity(bench, entity)
        _require(bench, f"setup: entity {entity} is live", found, "")
        before[entity] = found[1]
    bench.record("members-before", before)

    # 3. Offline: B turns with pins, D turns with none, C is kept.
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    members = {grid_b, bag_b, grid_c, grid_d}
    offline = wait_until(lambda: members <= _offline_members(bench) or None, 180, "every member offline")
    _require(bench, "setup: every member went offline", offline, json.dumps(sorted(_offline_members(bench))))
    rows_offline = {int(row["Partition"]): [int(m) for m in row.get("Members") or []]
                    for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])}
    home = {e: next(p for p, m in rows_offline.items() if e in m) for e in members}
    bench.record("homes", home)

    def turned(storage: str) -> bool:
        return all(_aligned(_rock_matrix(bench, n, storage)) for n in NODES)

    done = wait_until(lambda: (turned("TurnRock-B") and turned("TurnRock-D")) or None, 120, "B and D turned")
    bench.check("with the grids offline, B and D turn on every role", bool(done),
                json.dumps({r: [_rock_matrix(bench, n, r) for n in NODES] for r in ("TurnRock-B", "TurnRock-D")}))
    rows = {row["Storage"]: row for row in _turns(bench)}
    bench.record("turn-rows", rows)
    pins_b = {int(p["Partition"]) for p in rows.get("TurnRock-B", {}).get("Pins") or []}
    pins_d = {int(p["Partition"]) for p in rows.get("TurnRock-D", {}).get("Pins") or []}
    bench.check("B pins the partitions of its grid and its bag", {home[grid_b], home[bag_b]} <= pins_b,
                json.dumps(sorted(pins_b)))
    bench.check("D pins nothing: its grid is 500 m off, past memberMeters", home[grid_d] not in pins_d,
                json.dumps(sorted(pins_d)))
    time.sleep(10)
    bench.check("C (a static grid 20 m off) stays as it is", "TurnRock-C" not in {r["Storage"] for r in _turns(bench)}
                and not any(_aligned(_rock_matrix(bench, n, "TurnRock-C")) for n in NODES),
                json.dumps(_lines(bench, "wa-1", r"asteroid_turn refused storage=TurnRock-C")[-2:]))

    # 4. Wake B's and D's partitions on node-1: B's members turn with the rock, D's grid stays where it was.
    for partition in sorted({home[grid_b], home[bag_b], home[grid_d]}):
        bench.record(f"wake-{partition}", bench.cluster.post(
            f"/registry/partitions/offline/load?partition={partition}&node=node-1&reason=se1998"))
    woken = wait_until(lambda: all(_entity(bench, e) for e in (grid_b, bag_b, grid_d)) or None, 180, "members loaded")
    _require(bench, "setup: the woken members are live again", woken, "")
    rock_b_after = _rock_matrix(bench, "node-1", "TurnRock-B")
    results = {}
    for entity in (grid_b, bag_b):
        after = _entity(bench, entity)[1]
        same, dp, da = _same_local(_local(before[entity], rocks["TurnRock-B"]), _local(after, rock_b_after))
        results[entity] = {"same": same, "meters": dp, "axis": da, "before": before[entity], "after": after}
    bench.record("relative-poses", results)
    bench.check("B's grid keeps its pose relative to the rock", results[grid_b]["same"], json.dumps(results[grid_b]))
    bench.check("B's backpack keeps its pose relative to the rock", results[bag_b]["same"], json.dumps(results[bag_b]))
    d_after = _entity(bench, grid_d)[1]
    moved = math.dist(d_after[:3], before[grid_d][:3])
    bench.check("D's grid, 500 m off, keeps its world pose", moved < 0.01, f"{moved} m")
    load_lines = _lines(bench, "node-1", r"asteroid_turn load ")
    bench.check("node-1 logged the load-time turn of B's members", any("storage=TurnRock-B" in x for x in load_lines),
                "\n".join(load_lines[-4:]))

    # 5. A restarted node-2 starts from the premade poses and turns A, B and D again.
    bench.cluster.restart_node("node-2")
    back = wait_until(lambda: _try_pose(bench, "node-2"), 600, "node-2 back with the rocks turned")
    bench.check("a restarted node-2 turns A, B and D again and leaves C", bool(back), json.dumps(back))

    status = _lines(bench, "wa-1", r"asteroid_turn status")
    bench.record("wa-status", status[-5:])
    late = [x for n in NODES for x in _lines(bench, n, r"asteroid_turn (late_member|error=frozen_member)")]
    bench.check("no member was found near a rock after its turn (late or frozen)", late == [], "\n".join(late[:5]))
    bench.exercised(a_seconds=a_seconds, pins_b=sorted(pins_b), turns=len(_turns(bench)))


def _try_pose(bench, node: str):
    try:
        poses = {r: _aligned(_rock_matrix(bench, node, r)) for r in ROCKS}
    except (ClusterError, OSError):  # node-2 is down or still starting
        return None
    expected = {"TurnRock-A": True, "TurnRock-B": True, "TurnRock-C": False, "TurnRock-D": True}
    return poses if poses == expected else None


SCENARIOS_ASTEROID_TURN = {
    "P2-AST-TURN": (p2_ast_turn, "SE-1998: rotated asteroids turn online one at a time; offline members turn with "
                    "their rock as they load; static grids keep theirs", "2"),
}
