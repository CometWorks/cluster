"""SE-1871: a jump drive does not land a ship inside another grid, wherever that grid is hosted.

The server picks a jump's arrival in vanilla's FindSuitableJumpLocation, which moves it off every entity in a box
around the target. On a cluster node that box held only the node's own entities, so a jump onto a grid on another
node or in an offline partition ended inside it. P2-JUMP-OBSTACLES aims five jumps at the middle of a static
station, each station somewhere else:

* in the ship's own partition (a relay grid 3 km out links ship and station 6 km away),
* in another partition on the ship's node,
* in a partition on the other node,
* in an offline partition the ship's node took offline,
* in an offline partition the other node took offline.

Each jump is started on the server through the test plugin's /jump-test (the request a client sends, no client
needed) and lands after vanilla's 10 s charge. Every arrival must stay about 1 km off its station, as on a single
server (vanilla inflates each obstacle by the ship's bounds plus 1 km). Without SE-1871 the last three land on the
station. About 4 minutes.
"""
from __future__ import annotations

import math
import tempfile
import time
import urllib.parse
from pathlib import Path

from cluster import ClusterError, wait_until
from scenarios_core import _lines
from scenarios_offline import _put_config

STARTED = ["wa-1", "node-1", "node-2"]
BLOCK = (
    '<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{kind}"><SubtypeName>{subtype}</SubtypeName>'
    '<Min x="0" y="0" z="{z}" />{extra}</MyObjectBuilder_CubeBlock>'
)


def _blueprint(name: str, blocks: str) -> str:
    return (
        '<?xml version="1.0"?><Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"'
        ' xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints><ShipBlueprint'
        ' xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        f'<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="{name}" /><CubeGrids><CubeGrid>'
        "<PersistentFlags>InScene</PersistentFlags><PositionAndOrientation><Position x=\"0\" y=\"0\" z=\"0\" />"
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks><IsStatic>false</IsStatic>"
        "</CubeGrid></CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>"
    )


SHIP = _blueprint(
    "SE1871Ship",
    BLOCK.format(kind="BatteryBlock", subtype="LargeBlockBatteryBlock", z=0, extra="<CurrentStoredPower>3</CurrentStoredPower>")
    + BLOCK.format(kind="JumpDrive", subtype="LargeJumpDrive", z=1,
                   extra="<StoredPower>3</StoredPower><Recharging>false</Recharging>"),
)
# 20 armor blocks along +Z: about 2.5 x 2.5 x 97.5 m from its origin
STATION = _blueprint(
    "SE1871Station",
    "".join(BLOCK.format(kind="CubeBlock", subtype="LargeBlockArmorBlock", z=z, extra="") for z in range(0, 39, 2)),
)
MIDDLE = (0.0, 0.0, 48.75)
OFF_STATION_M = 900.0

# Each case: where the ship starts, the station's node and offset from the ship, and how it is held
CASES = {
    "same-partition": dict(start=(100_000.0, 0.0, 0.0), node="node-1", offset=(0.0, 0.0, 6_000.0), relay=True),
    "same-node": dict(start=(200_000.0, 0.0, 0.0), node="node-1", offset=(0.0, 0.0, 25_000.0)),
    "other-node": dict(start=(300_000.0, 0.0, 0.0), node="node-2", offset=(0.0, 0.0, 25_000.0)),
    "offline-node-1": dict(start=(400_000.0, 0.0, 0.0), node="node-1", offset=(0.0, 0.0, 25_000.0), offline=True),
    "offline-node-2": dict(start=(500_000.0, 0.0, 0.0), node="node-2", offset=(0.0, 0.0, 25_000.0), offline=True),
}


def _spawn(bench, node: str, path: Path, at, name: str, static: bool) -> int:
    reply = bench.cluster.node(
        node,
        f"/blueprint-test-spawn?path={urllib.parse.quote(str(path))}&x={at[0]}&y={at[1]}&z={at[2]}"
        f"&name={name}&static={'true' if static else 'false'}",
        "POST", timeout=60)
    bench.record(f"spawn-{name}", reply)
    return int(reply["grids"][0])


def _position(bench, entity: int) -> tuple[str | None, list[float] | None]:
    for node in ("node-1", "node-2"):
        try:
            reply = bench.cluster.node(node, f"/fast-test-velocity?entity={entity}", timeout=10)
        except ClusterError:
            continue
        if not isinstance(reply, dict) or not reply.get("found"):
            continue
        position = reply.get("position")
        if isinstance(position, dict):
            position = [position.get(k, position.get(k.upper())) for k in ("x", "y", "z")]
        if position:
            return node, [float(v) for v in position]
    return None, None


def _partition_of(bench, node: str, entity: int) -> int | None:
    rows = bench.cluster.node(node, "/partitions")["partitions"]
    return next((row["id"] for row in rows if any(m.get("id") == entity for m in row.get("members") or [])), None)


def _offline_members(bench) -> set[int]:
    table = bench.cluster.get("/registry/partitions/offline")
    rows = table.get("Partitions") or table.get("partitions") or []
    return {int(member) for row in rows for member in (row.get("Members") or row.get("members") or [])}


def _offline(bench, on: bool) -> None:
    values = ({"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0",
               "offline.settleFrames": "0"} if on else {"offline.enabled": "false"})
    for node in ("node-1", "node-2"):
        _put_config(bench, node, values)


def p2_jump_obstacles(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    folder = Path(tempfile.mkdtemp(prefix="se1871-"))
    ship_path, station_path = folder / "ship.sbc", folder / "station.sbc"
    ship_path.write_text(SHIP, encoding="utf-8")
    station_path.write_text(STATION, encoding="utf-8")

    stations: dict[str, tuple[int, tuple[float, float, float]]] = {}

    def place(case: str) -> None:
        spec = CASES[case]
        at = tuple(s + o for s, o in zip(spec["start"], spec["offset"]))
        stations[case] = (_spawn(bench, spec["node"], station_path, at, f"Station-{case}", True), at)

    # The offline stations first: nothing else may go offline with them, and no ship exists yet
    for case in ("offline-node-1", "offline-node-2"):
        place(case)
    _offline(bench, True)
    offline_ids = {stations[c][0] for c in ("offline-node-1", "offline-node-2")}
    went = wait_until(lambda: offline_ids <= _offline_members(bench) or None, 180, "the two stations offline")
    _offline(bench, False)
    bench.check("both offline stations are in the offline table", bool(went), str(sorted(offline_ids)))

    for case in ("same-partition", "same-node", "other-node"):
        place(case)
    relay_spec = CASES["same-partition"]
    _spawn(bench, "node-1", station_path,
           tuple(s + o / 2 for s, o in zip(relay_spec["start"], relay_spec["offset"])), "Relay-same-partition", True)

    ships = {case: _spawn(bench, "node-1", ship_path, spec["start"], f"Ship-{case}", False) for case, spec in CASES.items()}
    time.sleep(8)  # membership settles: the same-partition station joins the ship's partition through the relay
    layout = {}
    for case, ship in ships.items():
        station = stations[case][0]
        layout[case] = {
            "shipNode": _position(bench, ship)[0],
            "stationNode": _position(bench, station)[0],
            "shipPartition": _partition_of(bench, "node-1", ship),
            "stationPartition": _partition_of(bench, "node-1", station) or _partition_of(bench, "node-2", station),
        }
    bench.record("layout", layout)
    same = layout["same-partition"]
    bench.check("the same-partition station shares the ship's partition",
                same["shipPartition"] is not None and same["shipPartition"] == same["stationPartition"], str(same))
    bench.check("the same-node station is another partition on node-1",
                layout["same-node"]["stationNode"] == "node-1"
                and layout["same-node"]["stationPartition"] != layout["same-node"]["shipPartition"], str(layout["same-node"]))
    bench.check("the other-node station is on node-2", layout["other-node"]["stationNode"] == "node-2",
                str(layout["other-node"]))
    bench.check("the offline stations are on no node",
                all(layout[c]["stationNode"] is None for c in ("offline-node-1", "offline-node-2")),
                str({c: layout[c] for c in ("offline-node-1", "offline-node-2")}))

    targets = {}
    for case, ship in ships.items():
        at = stations[case][1]
        target = tuple(a + m for a, m in zip(at, MIDDLE))
        targets[case] = target
        bench.record(f"jump-{case}", bench.cluster.node(
            "node-1", f"/jump-test?grid={ship}&x={target[0]}&y={target[1]}&z={target[2]}", "POST"))

    def landed():
        positions = {case: _position(bench, ship)[1] for case, ship in ships.items()}
        done = all(p and math.dist(p, CASES[c]["start"]) > 1000 for c, p in positions.items())
        return positions if done else None

    try:
        arrived = wait_until(landed, 45, "every ship jumped")
    except ClusterError:
        arrived = {case: _position(bench, ship)[1] for case, ship in ships.items()}
    time.sleep(2)
    result = {}
    for case in CASES:
        position = arrived.get(case)
        result[case] = {
            "arrival": position,
            "toTargetM": math.dist(position, targets[case]) if position else None,
            "jumpedM": math.dist(position, CASES[case]["start"]) if position else None,
        }
    bench.record("arrivals", result)
    bench.record("jump-obstacle-lines", _lines(bench, "node-1", r"jump obstacles"))
    for case, row in result.items():
        bench.check(f"{case}: the ship jumped", (row["jumpedM"] or 0) > 1000, str(row))
        bench.check(f"{case}: it arrives off the station (>= {OFF_STATION_M:.0f} m from its middle)",
                    (row["toTargetM"] or 0) >= OFF_STATION_M, str(row))


SCENARIOS_JUMP = {
    "P2-JUMP-OBSTACLES": (p2_jump_obstacles,
                          "SE-1871: a jump onto a grid lands off it, in the same partition, another partition, on the "
                          "other node and in offline partitions of either node", "3"),
}
