"""SE-1156: a player a node still lists as a member after their session ended can join again.

* ``P2-STALE-MEMBER`` - two legs on the node that served the client:

  1. takeover: the client leaves, the node is given the leaked member list entry (the test plugin's
     ``/stale-member-inject``), and the client's next join opens a new session there. The node clears the entry
     (``stale_member_cleared``) and the client spawns. Before the fix vanilla answered JOIN_RESULT AlreadyJoined,
     "User already connected to server", on every join until the node restarted.
  2. race: the leave runs on the game thread right before the client's queued CLIENT_CONNECTED
     (``/stale-member-race``), which is what the client's own relayed Disconnected message did live
     (octahedron-test node-0, 2026-11-06 04:11:33Z). The node drops that CLIENT_CONNECTED
     (``client_connected_dropped``) instead of listing the client with no session, and the next join spawns.

No timers: the legs wait only for the Gateway to time out the killed client (Direct Transport, 120 s).
"""

from __future__ import annotations

import json

from scenarios_core import CLIENT_ID, _join, _lines, _nodes
from scenarios_wa_ops import _poll, _require, _session, _try


def _node_of(bench) -> str:
    backend = (_session(bench) or {}).get("backend")
    return next((name for name, node in _nodes(bench).items() if node.backend == backend), "")


def _members(bench, node: str) -> list[int]:
    return bench.cluster.node(node, "/members")["members"]


def _leave(bench, label: str) -> None:
    bench.kill_client(label)
    gone = _poll(lambda: True if _session(bench) is None else None, 360, 5)
    _require(bench, f"SETUP: the Gateway session of {label} cleared", gone is not None)


def _rejoin(bench, label: str) -> bool:
    bench.start_client(CLIENT_ID, label=label)
    return isinstance(_try(bench.wait_spawned, CLIENT_ID), dict) and bench.living_character(CLIENT_ID) is not None


def p2_stale_member(bench) -> None:
    """A member left behind with no session is cleared by the next join, and the leave-before-login race leaves none."""
    _join(bench)
    bench.cluster.post("/automatic/pause")  # the character's partition stays on its node between the legs
    node = _node_of(bench)
    _require(bench, "SETUP: the client's serving node is known", bool(node), json.dumps(_session(bench))[:300])

    # -- leg 1: takeover of the leaked entry --
    _leave(bench, CLIENT_ID)
    injected = bench.cluster.node(node, f"/stale-member-inject?client={CLIENT_ID}", method="POST")
    _require(
        bench,
        "SETUP: the node lists the client with no session",
        CLIENT_ID in injected["members"],
        json.dumps(injected),
    )
    takeover = f"{CLIENT_ID}-takeover"
    spawned = _rejoin(bench, takeover)
    rejoined_on = _node_of(bench)
    cleared = _lines(bench, node, rf"stale_member_cleared client={CLIENT_ID}\b")
    bench.record(
        "takeover",
        {
            "node": node,
            "rejoinedOn": rejoined_on,
            "cleared": cleared,
            "alreadyJoined": _lines(bench, node, r"Already joined"),
            "members": _members(bench, node),
        },
    )
    bench.check("a client its node still listed joins again and spawns", spawned)
    bench.check("the node cleared the stale member when the new session opened", bool(cleared), node)
    bench.check(
        "the node lists the client exactly once",
        _members(bench, node).count(CLIENT_ID) == 1,
        json.dumps(_members(bench, node)),
    )

    # -- leg 2: the leave runs before the queued CLIENT_CONNECTED --
    _leave(bench, takeover)
    armed = bench.cluster.node(node, f"/stale-member-race?client={CLIENT_ID}", method="POST")
    _require(bench, "SETUP: the leave-before-login race armed", armed.get("armed") == CLIENT_ID, json.dumps(armed))
    raced = f"{CLIENT_ID}-race"
    bench.launch_client(CLIENT_ID, label=raced)
    fired = _poll(
        lambda: _lines(bench, node, rf"stale-member-race leave before CLIENT_CONNECTED client={CLIENT_ID}\b") or None,
        420,
        2,
    )
    _require(bench, "SETUP: the race fired on the serving node", bool(fired))
    dropped = _poll(lambda: _lines(bench, node, rf"client_connected_dropped client={CLIENT_ID}\b") or None, 30, 1)
    # The Gateway saw the node close the session; once it has, nothing else can add the member back.
    _poll(lambda: True if _session(bench) is None else None, 60, 1)
    members = _members(bench, node)
    bench.record("race", {"node": node, "fired": fired, "dropped": dropped, "members": members})
    bench.check("the node dropped the CLIENT_CONNECTED of the session it had closed", bool(dropped))
    bench.check("the race left the client off the node's member list", CLIENT_ID not in members, json.dumps(members))
    _leave(bench, raced)
    bench.check("the client joins again after the race and spawns", _rejoin(bench, f"{CLIENT_ID}-after-race"))
    bench.exercised(route=f"{node}: injected stale member -> takeover; leave before CLIENT_CONNECTED -> rejoin")


SCENARIOS_STALE_MEMBER = {
    "P2-STALE-MEMBER": (
        p2_stale_member,
        "SE-1156: a member left with no session clears itself, and the leave-before-login race leaves none",
        "3",
    ),
}
