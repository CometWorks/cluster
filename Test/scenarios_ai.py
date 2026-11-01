#!/usr/bin/env python3
"""AI-block flight scenarios.

* ``P1-AI-ROUTE`` - T-0232: an AI leg in flight has its route replaced by the owning node while the
  pilot stands 2.5 km off, and the ship turns to the new first waypoint.

Why the node and not the client: an endurance AI pilot hands its route to the AI Basic block from
the pad, then hops 2.5 km to the side for the whole leg, because a character near the ship suppresses
the AI. The route was planned once from the client's asteroid list, which reaches only ~20 km, so
the far part of every long AI leg flew blind, and re-planning mid-leg needs a swap from 2.5 km. The
client's own clear + add does land from there (see the probe below), but as two events that a
handover capture can split, exporting the empty list, and only while validation lets them through;
the owning node's ``POST /cluster/autopilot-route`` swaps the list in one game-thread step and says
whether it did.

The geometry keeps the whole flight on a 2.8 km circle around the standing client: every chord
between two waypoints 60 degrees apart passes the client at >= 2.4 km (clear of the suppression the
harness measured at ~2 km), and the ship stays inside the client's 3 km sync range, so the client's
replica of the waypoint list can be read too. The route change happens after the first waypoint is
passed, when the flight's index is 1, and its new first waypoint lies BEHIND the ship: a flight
that kept its old index (continueOnCurrentCourse) would fly on to the new list's [1] and never turn
back, which is the one outcome this scenario must tell apart from the fix.
"""

from __future__ import annotations

import json
import math
import os
import time
import urllib.error
import urllib.request
from pathlib import Path

from se_remote import CallOp
from scenarios_core import CLIENT_ID, _join, _nodes
from scenarios_wa_ops import _client_identity, _poll, _require, _try

#: The suite's own platform blueprint; its first AI grid is the Visitor Ion the live cluster flies.
BLUEPRINT = Path.home() / "ws/se/se1/cluster-regression" / "blueprints" / "Space Test Platform" / "bp.sbc"
RADIUS = 2800.0
SPEED_LIMIT = 40.0
ENGAGE_TIMEOUT = 90
LEG_TIMEOUT = 240
TURN_TIMEOUT = 60


def _post_json(bench, node_id: str, path: str, body: dict, timeout: float = 60) -> tuple[int, dict | str]:
    """(status, parsed body); a 404 from a build without the route is an answer, not an exception."""
    url = f"http://{_nodes(bench)[node_id].control}/cluster{path}"
    request = urllib.request.Request(url, method="POST", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            text = response.read().decode(errors="replace")
            status = response.status
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")[:500]
    try:
        return status, json.loads(text)
    except ValueError:
        return status, text[:500]


def _on_circle(center, degrees: float, radius: float = RADIUS) -> list[float]:
    """A point in the client's x/y plane: the bench world is empty space, no up to respect."""
    angle = math.radians(degrees)
    return [center[0] + radius * math.cos(angle), center[1] + radius * math.sin(angle), center[2]]


def _unit(vector) -> list[float]:
    length = math.sqrt(sum(x * x for x in vector)) or 1.0
    return [x / length for x in vector]


def _cos(a, b) -> float:
    return sum(x * y for x, y in zip(_unit(a), _unit(b)))


def p1_ai_route(bench) -> None:
    """An active AI leg's route is replaced from the owning node with the pilot 2.5+ km away."""
    api = _join(bench)
    identity = _client_identity(bench)
    # Nothing but this scenario moves the ship's partition: a balance migration mid-leg would move
    # the autopilot to node-2 and the route call to node-1 would find no grid.
    bench.cluster.post("/automatic/pause")
    center = api.get_character().get("position")
    _require(bench, "the client reports its position", bool(center), json.dumps(center))

    start = _on_circle(center, 0)
    old = {"A.1": _on_circle(center, 60), "A.2": _on_circle(center, 120), "A": _on_circle(center, 180)}
    status, ship = _post_json(bench, "node-1", "/ai-route-test-spawn", {
        "owner": identity, "blueprint": str(BLUEPRINT), "position": start,
        "forward": [old["A.1"][i] - start[i] for i in range(3)],
        "waypoints": list(old.values()), "names": list(old), "speedLimit": SPEED_LIMIT,
    })
    bench.record("ai-route-spawn", ship)
    _require(bench, "the test plugin spawned an AI Visitor Ion on the old route",
             status == 200 and isinstance(ship, dict) and ship.get("grid")
             and ship.get("waypoints") == list(old) and ship.get("flightActive") and ship.get("basicActive"),
             f"{status} {json.dumps(ship)[:400]}")
    grid = ship["grid"]

    def state() -> dict:
        return _try(bench.cluster.node, "node-1", f"/autopilot-state?entityId={grid}")

    engaged = _poll(lambda: (lambda s: s if (s.get("speed") or 0) > 5 else None)(state()), ENGAGE_TIMEOUT, 2)
    bench.record("ai-route-engaged", engaged or state())
    _require(bench, "the AI engaged and the ship flies its old route with no client near it", engaged is not None,
             json.dumps(state())[:400])

    # Past A.1: the flight's index is 1 (A.2), so continueOnCurrentCourse would keep index 1.
    on_second = _poll(lambda: (lambda s: s if s.get("currentWaypoint") == "A.2"
                               and math.dist(s.get("position") or old["A.1"], old["A.1"]) > 300 else None)(state()),
                      LEG_TIMEOUT, 2)
    bench.record("ai-route-before", on_second or state())
    _require(bench, "the ship passed A.1 and flies to A.2 (flight index 1)", on_second is not None,
             json.dumps(state())[:400])

    here = on_second["position"]
    pilot = api.get_character().get("position")
    standoff = math.dist(pilot, here) if pilot else None
    bench.record("ai-route-standoff", {"pilot": pilot, "ship": here, "standoff": standoff})
    _require(bench, "the pilot stands >= 2 km from the ship at the route change",
             standoff is not None and standoff >= 2000, str(standoff))

    # The new first waypoint lies BEHIND the ship, back on the circle before A.1; its [1] is the old A.
    new = {"D": _on_circle(center, 30), "A": old["A"]}
    to_d, to_index1 = [new["D"][i] - here[i] for i in range(3)], [new["A"][i] - here[i] for i in range(3)]
    separation = _cos(to_d, to_index1)
    bench.record("ai-route-geometry", {"cosDetourVsIndex1": separation})
    _require(bench, "the detour and the new list's [1] lie in clearly different directions", separation < 0.3,
             f"cos {separation:.2f}")

    # Probe (P1_AI_ROUTE_CONTROL=1 or =only, off by default): the client's own clear + add on the Basic
    # block from where the pilot stands. It is NOT refused - 2026-09-24 on 67342e6 the flight took the
    # client's list from 2627 m (current C, index 0) - so it is recorded, not asserted. The harness
    # still re-routes over the node: a client pair can straddle a handover capture and export the
    # empty list between its two events, and it depends on validation T-0225 may tighten; the node
    # swap is one game-thread step on the owner and says replaced true/false.
    # Its route has its own name and point (C, 20 degrees), never used by the node path.
    basic = ship["basicBlock"]
    mode = os.environ.get("P1_AI_ROUTE_CONTROL", "0")
    if mode != "0":
        edited = {"C": _on_circle(center, 20), "A": old["A"]}
        edit = {
            "clear": _try(lambda: api.batch(calls=[CallOp.fat_block_method(basic, "AiAutopilotClearWaypoints")]).call(0)),
            "add": _try(lambda: api.batch(calls=[CallOp.fat_block_method(
                basic, "AiAutopilotAddWaypoints",
                {"positions": list(edited.values()), "names": list(edited)})]).call(0)),
        }
        time.sleep(10)
        control = state()
        bench.record("ai-route-client-edit", {"edit": edit, "nodeAfter": control})
        if mode == "only":
            bench.exercised(ship=grid, standoff=round(standoff), control=control.get("currentWaypoint"))
            return

    status, replaced = _post_json(bench, "node-1", f"/autopilot-route?entityId={grid}",
                                  {"positions": list(new.values()), "names": list(new)})
    bench.record("ai-route-replace", {"status": status, "answer": replaced})
    ok = status == 200 and isinstance(replaced, dict)
    replaced = replaced if ok else {}
    bench.check("the owning node replaced the active route", replaced.get("replaced") is True,
                f"{status} {json.dumps(replaced)[:400]}")
    bench.check("the Basic block holds exactly the new list", replaced.get("names") == list(new),
                json.dumps(replaced.get("names")))
    bench.check("the flight flies the new FIRST waypoint, not the old index", replaced.get("flying") == "D",
                json.dumps(replaced))
    # No early exit when the node has no route: what the ship does next IS the red - it flies on to A.2.

    # The ship itself turns back to D: its velocity swings from A.2's direction to D's and it closes on D.
    samples: list[dict] = []

    def turned():
        first = state()
        time.sleep(3)
        second = state()
        if not (first.get("position") and second.get("position")):
            return None
        velocity = [second["position"][i] - first["position"][i] for i in range(3)]
        to_detour = [new["D"][i] - second["position"][i] for i in range(3)]
        sample = {"at": time.strftime("%H:%M:%S"), "current": second.get("currentWaypoint"),
                  "speed": second.get("speed"), "cosToD": round(_cos(velocity, to_detour), 3),
                  "toD": round(math.dist(second["position"], new["D"]))}
        samples.append(sample)
        return sample if sample["current"] == "D" and sample["cosToD"] > 0.8 and (sample["speed"] or 0) > 5 else None

    course = _poll(turned, TURN_TIMEOUT, 0)
    bench.record("ai-route-turn", samples)
    bench.check("the ship turned back and flies at the new first waypoint", course is not None,
                json.dumps(samples[-3:]))
    unchanged = state()
    bench.record("ai-route-after", {k: unchanged.get(k) for k in ("currentWaypoint", "basicWaypoints", "waypoints", "position")})
    after = state()
    bench.check("the AI stayed active through the route change",
                after.get("activated") is True and after.get("basicActivated") is True,
                json.dumps({k: after.get(k) for k in ("activated", "basicActivated", "autopilotEnabled")}))

    # Every client's replica follows (the harness's next-leg clear works on that replica).
    replica = None

    def replica_names():
        nonlocal replica
        replica = _try(lambda: api.batch(calls=[CallOp.fat_block_method(basic, "AiAutopilotState")]).call(0))
        names = [w.get("name") for w in (replica or {}).get("waypoints") or []] if isinstance(replica, dict) else []
        return names if names == list(new) else None

    names = _poll(replica_names, 30, 2)
    bench.record("ai-route-client-replica", replica)
    bench.check("the client's replica of the Basic block holds the new list", names is not None,
                json.dumps(replica)[:400])
    bench.exercised(ship=grid, standoff=round(standoff), status=status, replaced=replaced.get("previous"),
                    turn=course, client=CLIENT_ID)


SCENARIOS_AI = {
    "P1-AI-ROUTE": (p1_ai_route, "AI leg route replaced from the owning node mid-flight (T-0232)", "3"),
}
