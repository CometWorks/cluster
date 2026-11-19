"""SE-1694: a watching client's thruster flames after the pilot of the ship it watches let go, was handed over
mid-burn, or dropped out mid-burn.

A second client (the observer) never controls the ship: its flames come from the throttle its own copy of the
ship's control system builds from the pilot's controls, which ride in each physics snapshot of the grid. Live,
ships' flames stayed lit on other players' clients while the node held no thrust and the ship burnt no hydrogen.

    CONTROL: the pilot burns forward and lets go on node-1: the observer's thrusters go back to zero.
    TRIGGER: the pilot burns, the partition goes node-1 -> node-2 during the burn, the pilot lets go on node-2:
             the observer's thrusters go back to zero too, like the node's.
    DROP:    the pilot's game closes during the burn. The node drops the controller and holds no thrust. The
             vanilla client keeps the last throttle for good (only the server and the pilot's own client clear it
             on release); Bugfixes 1.23.0 on the observer turns the flames off. Without it this leg fails.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, _client_partition, _enter_nearest_cockpit, _handover_url, _join, _partitions
from scenarios_wa import CLIENT2_ID, start_second_client

SETTLE_SECONDS = 8
BURN_SECONDS = 3


def _node_state(bench, grid: int) -> tuple[str, dict] | None:
    for node in ("node-1", "node-2"):
        try:
            state = bench.cluster.node(node, f"/thrust-test-state?grid={grid}")
        except Exception:  # noqa: BLE001 - a node without the grid is an answer
            continue
        if state.get("found"):
            return node, state
    return None


def _client_thrust(api, grid: int, thrusters: list[dict]) -> dict[str, float]:
    """Each thruster's current thrust as this client shows it (IMyThrust.CurrentThrust, the flame's strength)."""
    shown = {}
    for thruster in thrusters:
        block = api.get_block(grid, tuple(thruster["min"])) or {}
        shown[",".join(map(str, thruster["min"]))] = float((block.get("data") or block).get("currentThrust") or 0.0)
    return shown


def _lit(shown: dict[str, float]) -> int:
    return sum(1 for value in shown.values() if value > 1.0)


def _burn(api, seconds: float) -> None:
    api.set_input_state(keys=["W"], mode="override")
    time.sleep(seconds)


def _settled(bench, observer, grid: int, thrusters: list[dict], label: str) -> dict:
    """Let go, wait, and compare the observer's flames with the node's thrust."""
    time.sleep(SETTLE_SECONDS)
    node = _node_state(bench, grid)
    shown = _client_thrust(observer, grid, thrusters)
    result = {"node": node[0] if node else None, "nodeState": node[1] if node else None, "observer": shown,
              "observerLit": _lit(shown)}
    bench.record(f"{label}-settled", result)
    return result


def p2_thrust_flames(bench) -> None:
    api = _join(bench)
    cockpit = _enter_nearest_cockpit(bench, api)
    grid = cockpit["gridId"]
    blocks = api.list_blocks(grid, limit=500).get("blocks") or []
    thrusters = [block for block in blocks if block.get("blockType") == "MyThrust"]
    bench.check("SETUP: the ship has thrusters", bool(thrusters), str(len(blocks)))
    if not thrusters:
        return
    bench.record("dampeners-off", bench.cluster.node("node-1", f"/thrust-test-dampeners?grid={grid}&on=false", "POST"))

    partition = _client_partition(_partitions(bench, "node-1"))
    observer = start_second_client(bench, route_node="node-1", partition=partition, spawn=True)
    x, y, z = cockpit["worldPosition"]
    observer.character_teleport(x + 25, y + 5, z)
    wait_until(lambda: any(g.get("entityId") == grid for g in observer.list_grids() or []), 60,
               "the observer to see the ship")

    # CONTROL: burn and let go on node-1.
    _burn(api, BURN_SECONDS)
    burning = _client_thrust(observer, grid, thrusters)
    bench.record("control-burning", {"observer": burning, "node": _node_state(bench, grid)})
    bench.check("CONTROL: the observer shows the burn", _lit(burning) > 0, json.dumps(burning))
    api.clear_input_state()
    control = _settled(bench, observer, grid, thrusters, "control")
    bench.check("CONTROL: after the pilot let go the observer shows no flame", control["observerLit"] == 0,
                json.dumps(control)[:400])

    # TRIGGER: burn, hand the partition over during the burn, let go on node-2.
    observer.character_teleport(*_beside(bench, grid))
    time.sleep(3)
    _burn(api, BURN_SECONDS)
    partition = _client_partition(_partitions(bench, "node-1"))
    if partition is None:
        raise ClusterError("the pilot's partition is not on node-1 before the handover")
    try:
        bench.record("trigger-handover", bench.cluster.post(_handover_url(bench, partition, client=CLIENT_ID), timeout=300))
        bench.record("trigger-burning", {"observer": _client_thrust(observer, grid, thrusters),
                                         "node": _node_state(bench, grid)})
    finally:
        api.clear_input_state()
    trigger = _settled(bench, observer, grid, thrusters, "trigger")
    bench.check("TRIGGER: the ship is on node-2 after the handover", trigger["node"] == "node-2", str(trigger["node"]))
    node_lit = sum(1 for t in (trigger["nodeState"] or {}).get("thrusters", []) if t["strength"] > 0.01)
    bench.check("TRIGGER: the node holds no thrust after the pilot let go", node_lit == 0,
                json.dumps(trigger["nodeState"])[:400])
    bench.check("TRIGGER: after the pilot let go on node-2 the observer shows no flame", trigger["observerLit"] == 0,
                json.dumps(trigger["observer"]))

    # DROP: the pilot burns and its client drops out mid-burn; the ship drifts on with nobody at the controls.
    observer.character_teleport(*_beside(bench, grid))
    time.sleep(3)
    _burn(api, BURN_SECONDS)
    bench.record("drop-burning", {"observer": _client_thrust(observer, grid, thrusters), "node": _node_state(bench, grid)})
    try:
        api.exit_game(force=True)
    except Exception:  # noqa: BLE001 - the client going away mid-call is the point
        pass
    drop = _settled(bench, observer, grid, thrusters, "drop")
    node_lit = sum(1 for t in (drop["nodeState"] or {}).get("thrusters", []) if t["strength"] > 0.01)
    bench.check("DROP: the node holds no thrust once the pilot's client is gone", node_lit == 0,
                json.dumps(drop["nodeState"])[:400])
    bench.check("DROP: the observer shows no flame once the pilot's client is gone", drop["observerLit"] == 0,
                json.dumps(drop["observer"]))
    bench.exercised(ship=grid, thrusters=len(thrusters), observer=CLIENT2_ID, control=control["observerLit"],
                    trigger=trigger["observerLit"], drop=drop["observerLit"])


def _beside(bench, grid: int) -> tuple[float, float, float]:
    """25 m beside the ship where it is now (it drifted after the control burn)."""
    node = _node_state(bench, grid)
    if node is None or not node[1].get("position"):
        raise ClusterError("no node holds the ship")
    x, y, z = node[1]["position"]
    return x + 25, y + 5, z


SCENARIOS_THRUST_FLAMES = {
    "P2-THRUST-FLAMES": (p2_thrust_flames, "a watching client's flames go out after a handover mid-burn (SE-1694)", "3"),
}
