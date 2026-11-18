"""SE-1596: the Alt-F10 "Show players" checkbox shows players on other nodes.

* ``P2-ADM-SHOWPLAYERS`` - client 1 (an admin) is on node-1 and client 2 on node-2. Vanilla draws a Show players
  marker only for a player whose character the admin's client holds, and client 2's character lives on node-2, so
  without the fix client 1 sees nothing. With the fix node-1 sends client 1 a temporary GPS marker for client 2,
  named after the player, and keeps it on client 2's position.

The legs: no marker while the flag is off; ticking it adds one marker at client 2's position, on client 1 only and
in no node's GPS collection (so no save can hold it); moving client 2 300 m moves the marker; unticking removes it;
handing client 1 over to node-2 (where client 2 is) takes it back at once; handing client 1 back to node-1 brings it
again; client 2 logging out removes it.

The markers are read from client 1's GPS list through Remote's ``/v1/gps``; ours carry the description
"Show players: <node>".
"""

from __future__ import annotations

import json
import math
import time
from urllib.parse import quote

from scenarios_chat_names import _medbay_to_node2, _name
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _handover_url, _join, _nodes, _partitions
from scenarios_wa import CLIENT2_ID, start_second_client
from scenarios_wa_ops import _poll, _require, _session

PREFIX = "Show players: "
MARKER_TIMEOUT = 30
#: The Gateway, the online list and the position registry each take a second or more to see a logout.
LOGOUT_TIMEOUT = 90
#: A marker is on the player's position as the registry had it, about a second old; the player stands still.
NEAR_METERS = 20


def _markers(api, name: str | None = None) -> list[dict]:
    return [
        e
        for e in api.list_gps().get("entries") or []
        if (e.get("description") or "").startswith(PREFIX) and (name is None or e.get("name") == name)
    ]


def _position(api) -> list[float]:
    return list(api.get_character()["position"])


def _distance(entry: dict, position: list[float]) -> float:
    return math.dist((entry["x"], entry["y"], entry["z"]), position)


def _one_near(api, name: str, position: list[float]):
    """The single marker named ``name`` once it is within NEAR_METERS of ``position``, else None."""
    found = _markers(api, name)
    return found[0] if len(found) == 1 and _distance(found[0], position) < NEAR_METERS else None


def _on(bench, client_id: int, node_id: str) -> bool:
    session = _session(bench, client_id) or {}
    return session.get("backend") == _nodes(bench)[node_id].backend and session.get("admissionState") == "Ready"


def _hand(bench, source: str, target: str) -> dict:
    partition = _client_partition(_partitions(bench, source))
    _require(bench, f"SETUP: {source} reports client 1's partition", partition is not None)
    result = bench.cluster.post(
        _handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300
    )
    _require(
        bench,
        f"SETUP: client 1 was handed over {source} -> {target}",
        bool(_poll(lambda: _on(bench, CLIENT_ID, target) or None, HANDOVER_TIMEOUT)),
        json.dumps(result)[:300],
    )
    return result


def p2_adm_showplayers(bench) -> None:
    api1 = _join(bench)
    _medbay_to_node2(bench)
    api2 = start_second_client(bench, route_node="node-2", spawn=True)
    _require(bench, "SETUP: client 1 is on node-1", _on(bench, CLIENT_ID, "node-1"))
    _require(bench, "SETUP: client 2 is on node-2", _on(bench, CLIENT2_ID, "node-2"))
    name2 = _name(bench, CLIENT2_ID)
    bench.record("setup", {"name2": name2, "client2": api2.get_character()})

    # -- the flag off: vanilla, nothing --
    time.sleep(5)
    bench.check("no Show players marker while the flag is off", not _markers(api1), json.dumps(_markers(api1)))

    # -- the flag on: one marker for client 2, at its position --
    api1.set_admin_flag("showPlayers", True)
    started = time.time()
    marker = _poll(lambda: _one_near(api1, name2, _position(api2)), MARKER_TIMEOUT)
    bench.record("marker-added", {"marker": marker, "seconds": round(time.time() - started, 1), "all": _markers(api1)})
    _require(
        bench,
        "with the flag on, client 1 gets one marker named after client 2 at client 2's position",
        marker is not None,
        json.dumps({"markers": _markers(api1), "client2": _position(api2)}),
    )
    bench.check("the marker names client 2's node", marker["description"] == PREFIX + "node-2", marker["description"])
    bench.check(
        "client 2 gets no Show players marker (only the admin's client does)",
        not _markers(api2),
        json.dumps(_markers(api2)),
    )
    tables = {
        node: bench.cluster.node(node, f"/gps-test-table?prefix={quote(name2)}")
        for node in ("node-1", "node-2", "wa-1")
    }
    bench.record("gps-tables", tables)
    bench.check(
        "no node and not the World Authority hold the marker in their GPS collection (no save can contain it)",
        all(not t.get("entries") for t in tables.values()),
        json.dumps({n: t.get("entries") for n, t in tables.items()}),
    )

    # -- client 2 moves: the marker follows --
    character = api2.get_character()["entityId"]
    bench.cluster.node("node-2", f"/test-position?entity={character}&relativeTo={character}&x=300", "POST")
    # The client takes a moment to show the teleport; until it does, the old marker is "near" client 2.
    _require(
        bench,
        "SETUP: client 2 is 300 m from where its marker was",
        bool(_poll(lambda: _distance(marker, _position(api2)) > 250 or None, MARKER_TIMEOUT)),
        json.dumps(_position(api2)),
    )
    started = time.time()
    moved = _poll(lambda: _one_near(api1, name2, _position(api2)), MARKER_TIMEOUT)
    bench.record(
        "marker-moved",
        {
            "marker": moved,
            "seconds": round(time.time() - started, 1),
            "client2": _position(api2),
            "all": _markers(api1),
        },
    )
    bench.check(
        "after client 2 moved 300 m, its one marker is a new one at the new position",
        moved is not None and moved["hash"] != marker["hash"],
        json.dumps({"markers": _markers(api1), "client2": _position(api2)}),
    )

    # -- the flag off: the marker goes --
    api1.set_admin_flag("showPlayers", False)
    gone = _poll(lambda: not _markers(api1) or None, MARKER_TIMEOUT)
    bench.check("unticking the flag removes the marker", bool(gone), json.dumps(_markers(api1)))

    # -- handover to client 2's node: the marker is taken back, and node-2 adds none (vanilla draws client 2) --
    api1.set_admin_flag("showPlayers", True)
    _require(
        bench,
        "SETUP: the marker is back with the flag on again",
        bool(_poll(lambda: _one_near(api1, name2, _position(api2)), MARKER_TIMEOUT)),
    )
    bench.record("handover-to-node-2", _hand(bench, "node-1", "node-2"))
    started = time.time()
    gone = _poll(lambda: not _markers(api1) or None, 15)
    bench.record("after-handover", {"seconds": round(time.time() - started, 1), "markers": _markers(api1)})
    bench.check(
        "after client 1's handover to node-2 the marker is gone within 15 s (taken back, not left to expire)",
        bool(gone),
        json.dumps(_markers(api1)),
    )
    time.sleep(6)
    bench.check(
        "node-2 sends no marker for client 2, who is on node-2 with client 1",
        not _markers(api1),
        json.dumps(_markers(api1)),
    )

    # -- back to node-1: the marker returns --
    bench.record("handover-to-node-1", _hand(bench, "node-2", "node-1"))
    back = _poll(lambda: _one_near(api1, name2, _position(api2)), MARKER_TIMEOUT + 15)
    bench.check("back on node-1, client 1 has client 2's marker again", back is not None, json.dumps(_markers(api1)))

    # -- client 2 logs out: the marker goes --
    try:
        api2.exit_game(force=True)
    except Exception as error:  # noqa: BLE001 - the client may close before it answers
        bench.record("client2-exit", {"error": str(error)[:200]})
    started = time.time()
    gone = _poll(lambda: not _markers(api1) or None, LOGOUT_TIMEOUT)
    bench.record("after-logout", {"seconds": round(time.time() - started, 1), "markers": _markers(api1)})
    bench.check("client 2 logging out removes its marker", bool(gone), json.dumps(_markers(api1)))
    bench.exercised(
        route="client 1 (admin) node-1, client 2 node-2; flag on/off; client 2 moved 300 m; "
        "client 1 handed node-1 -> node-2 -> node-1; client 2 logged out"
    )


SCENARIOS_ADMIN_SHOW_PLAYERS = {
    "P2-ADM-SHOWPLAYERS": (
        p2_adm_showplayers,
        "SE-1596: Alt-F10 Show players marks players on other nodes with temporary GPS markers",
        "2",
    ),
}
