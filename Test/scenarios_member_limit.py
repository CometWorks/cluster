"""SE-1203: a node lets in every client the Gateway admitted, whatever its world's MaxPlayers.

* ``P2-MEMBER-LIMIT`` - the bench world's MaxPlayers is 2 and the Gateway's memberLimit is 16. Three clients on one
  node, two ways:

  1. login: clients 2 and 3 are routed to client 1's node. Before the fix vanilla SetUserConnectionResult refused
     client 3 as ServerFull ('User left' 50 ms after its auth answer) and Direct Transport rejoined every 5 s.
  2. handover: client 1's partition goes to the other node and back while clients 2 and 3 stay. Its arrival is a
     vanilla join on the node too, the third member again.

The other node refuses spawn preview switches (SE-1124's test knob), so the unspawned clients 2 and 3 stay put.
No timers: the waits end on the Gateway's session state and the node's member list.
"""

from __future__ import annotations

import json

from scenarios_core import CLIENT_ID, _handover_url, _join, _lines, _nodes, _partitions
from scenarios_wa import CLIENT2_ID, CLIENT3_ID, CLIENT3_PORT, CLIENT_TIMEOUT, start_second_client
from scenarios_wa_ops import _poll, _require, _session, _try


def _node_of(bench, client: int) -> str:
    backend = (_session(bench, client) or {}).get("backend")
    return next((name for name, node in _nodes(bench).items() if node.backend == backend), "")


def _members(bench, node: str) -> dict:
    return bench.cluster.node(node, "/members")


def _refused(bench, node: str, client: int) -> list[str]:
    """The node's 'User left' lines for the client: vanilla logs one right after each refused join."""
    return _lines(bench, node, rf"User left \[0+\.\.\.{str(client)[-3:]}\]")


def _ready_on(bench, client: int, node: str):
    session = _session(bench, client) or {}
    return (
        session.get("backend") == _nodes(bench)[node].backend
        and session.get("admissionState") == "Ready"
        and (session.get("identityId") or 0) > 0
    ) or None


def p2_member_limit(bench) -> None:
    """Three clients on one node of a world with MaxPlayers=2: a third login and a third arrival by handover."""
    _join(bench)
    bench.cluster.post("/automatic/pause")  # no balancing: the partitions stay where the legs put them
    node = _node_of(bench, CLIENT_ID)
    _require(bench, "SETUP: client 1's node is known", bool(node), json.dumps(_session(bench, CLIENT_ID))[:300])
    other = next(name for name in _nodes(bench) if name.startswith("node-") and name != node)
    start = _members(bench, node)
    bench.record("members-start", start)
    _require(bench, "SETUP: the node's world allows 2 players", start.get("maxPlayers") == 2, json.dumps(start))
    bench.check(
        "the node's vanilla member limit is above the world's MaxPlayers",
        start.get("memberLimit", 0) > start["maxPlayers"],
        json.dumps(start),
    )
    bench.record("preview-refusal", bench.cluster.node(other, "/respawn-execute-fault?mode=refuse&count=1000", "POST"))
    home = next((p["id"] for p in _partitions(bench, node) if CLIENT_ID in (p.get("clients") or [])), None)
    spare = next((p["id"] for p in _partitions(bench, node) if not p.get("clients")), None)
    _require(
        bench,
        "SETUP: the node has client 1's partition and a clientless one",
        None not in (home, spare),
        json.dumps(_partitions(bench, node)),
    )

    # -- leg 1: the third login --
    start_second_client(bench, route_node=node, partition=spare)
    third = _try(
        start_second_client, bench, route_node=node, partition=spare, client_id=CLIENT3_ID, remote_port=CLIENT3_PORT
    )
    members = _members(bench, node)
    bench.record(
        "login",
        {
            "node": node,
            "third": third if isinstance(third, dict) else "chat-ready",
            "refused": _refused(bench, node, CLIENT3_ID),
            "members": members,
        },
    )
    bench.check("a third client logs in to the node and becomes chat-ready", not isinstance(third, dict), str(third))
    bench.check("the node never refused the third client", not _refused(bench, node, CLIENT3_ID))
    bench.check("the node lists three clients", len(members["members"]) - 1 == 3, json.dumps(members))

    # -- leg 2: the third member arrives by handover --
    away = _try(
        bench.cluster.post, _handover_url(bench, home, source=node, target=other, client=CLIENT_ID), timeout=300
    )
    bench.record("handover-away", away)
    _require(
        bench,
        f"SETUP: client 1 moved to {other}",
        _poll(lambda: _ready_on(bench, CLIENT_ID, other), 60) is not None,
        json.dumps(_session(bench, CLIENT_ID))[:300],
    )
    # Before the fix client 3 got in only now, on a retry, as the node's second client.
    held = _poll(lambda: len(_members(bench, node)["members"]) - 1 >= 2 or None, CLIENT_TIMEOUT)
    _require(
        bench,
        f"SETUP: {node} holds two clients before client 1 comes back",
        held is not None,
        json.dumps(_members(bench, node)),
    )
    refused_before = len(_refused(bench, node, CLIENT_ID))
    back = _try(
        bench.cluster.post, _handover_url(bench, home, source=other, target=node, client=CLIENT_ID), timeout=300
    )
    returned = _poll(lambda: _ready_on(bench, CLIENT_ID, node), 60)
    members = _members(bench, node)
    refused = _refused(bench, node, CLIENT_ID)[refused_before:]
    bench.record(
        "handover-back", {"answer": back, "session": _session(bench, CLIENT_ID), "refused": refused, "members": members}
    )
    bench.check(
        f"client 1 arrives on {node} by handover as its third client",
        returned is not None,
        json.dumps(_session(bench, CLIENT_ID))[:300],
    )
    bench.check("the node never refused the arriving client", not refused, json.dumps(refused))
    bench.check("the node lists three clients again", len(members["members"]) - 1 == 3, json.dumps(members))
    bench.check("client 1 still has a living character", bench.living_character(CLIENT_ID) is not None)
    bench.check("clients 2 and 3 stayed on the node", all(_node_of(bench, c) == node for c in (CLIENT2_ID, CLIENT3_ID)))
    bench.exercised(route=f"3 clients on {node} (MaxPlayers=2): login; handover {node} -> {other} -> {node}")


SCENARIOS_MEMBER_LIMIT = {
    "P2-MEMBER-LIMIT": (
        p2_member_limit,
        "SE-1203: a node of a MaxPlayers=2 world takes a third client by login and by handover",
        "3",
    ),
}
