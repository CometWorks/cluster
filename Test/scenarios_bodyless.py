"""SE-1876: a handover of the partition that holds a dead player's home, while the player waits on the respawn screen.

The player has no character, so the target node had no player to adopt the client into and refused it ("Target player
is not ready"); the source could not take the client back either, so the Gateway ended its session with
``handover-abort-source-readopt-failed``. On octahedron 2026-11-22 07:08Z a scale-in drain did that to a player who had
died, relogged and was still choosing a medbay, and the forced reconnect put him back on the node being retired.

* ``P2-HND-BODYLESS`` - node-1, node-2 and the WA. The client spawns on node-1 and is killed. Its partition is handed
  to node-2 while the dead client stays connected, then the client relogs (the Gateway routes it to its home
  partition, now on node-2) and the partition is handed back to node-1. Each move commits, the client keeps its
  session and its respawn screen, the adopting node has its (dead) player, and the client finally respawns.

* ``P2-RETIRE-BODYLESS`` - node control on, two serving nodes and a hot spare. The client spawns, is killed and
  relogs, then its node is recycled. The drain moves the client's home partition off the node; the client is never
  force-reconnected, ends on another node, respawns there, and the recycle ends.

No timers to shorten: the moves are requested directly, and the recycled node drains in seconds.
"""

from __future__ import annotations

import json
import time

from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _handover_url, _join, _lines, _nodes, \
    _partitions
from scenarios_retire_loading import _node_by_backend
from scenarios_wa_ops import _owned_by, _poll, _require, _session, _try

#: The relogged client's process key: a fresh data directory, as the rejoin lane does.
RELOG = "relog"


def _player(bench, node_id: str) -> dict:
    """The node's player for the client: attached, dead, character (the test plugin's respawn route)."""
    return _try(bench.cluster.node, node_id, f"/respawn-test-player?userId={CLIENT_ID}") or {}


def _forced(bench) -> list[str]:
    return _lines(bench, "gateway", rf"Force reconnect client {CLIENT_ID}:|Client {CLIENT_ID} could not be re-adopted")


def _medicals(bench) -> bool:
    screens = _try(bench.apis[CLIENT_ID].list_screens)
    return isinstance(screens, list) and any(s.get("type") == "MyGuiScreenMedicals" for s in screens)


def _kill(bench, node_id: str) -> None:
    killed = _try(bench.cluster.node, node_id, f"/respawn-test-kill?userId={CLIENT_ID}", method="POST")
    bench.record("kill", {"node": node_id, "answer": killed})
    _require(bench, "SETUP: the node killed the client's character", killed.get("killed") is True, json.dumps(killed))
    _require(
        bench,
        "SETUP: the client is dead on the respawn screen",
        _poll(lambda: _player(bench, node_id).get("dead") is True and _medicals(bench), HANDOVER_TIMEOUT) is not None,
        json.dumps(_player(bench, node_id)),
    )


def _relog(bench) -> dict:
    """Close the client and join again, as the player did; it comes back dead, to the respawn screen."""
    bench.kill_client(CLIENT_ID)
    _require(bench, "SETUP: the closed client's Gateway session cleared",
             _poll(lambda: True if _session(bench) is None else None, 360, 5) is not None)
    bench.launch_client(CLIENT_ID, label=RELOG)
    bench.attach_api(CLIENT_ID, label=RELOG)
    session = _poll(lambda: (lambda s: s if s and s.get("worldReady") else None)(_session(bench)), 420)
    _require(bench, "SETUP: the relogged client is world-ready", session is not None)
    _require(bench, "SETUP: the relogged client is on the respawn screen", _poll(lambda: _medicals(bench), 120) is not None)
    bench.record("relog", session)
    return session


def _move(bench, partition: int, source: str, target: str, leg: str) -> None:
    before = _session(bench) or {}
    forced_before = len(_forced(bench))
    answer = _try(
        bench.cluster.post,
        _handover_url(bench, partition, source=source, target=target, client=CLIENT_ID),
        timeout=300,
    )
    backend = _nodes(bench)[target].backend
    after = _poll(
        lambda: (lambda s: s if s and s.get("worldReady") and s.get("backend") == backend else None)(_session(bench)),
        HANDOVER_TIMEOUT,
    )
    player = _player(bench, target)
    forced = _forced(bench)[forced_before:]
    bench.record(f"{leg}-handover", {"answer": answer, "before": before, "after": after, "player": player})
    bench.check(f"{leg}: the handover of {partition} {source} -> {target} committed", _owned_by(bench, partition, target),
                json.dumps(answer)[:400])
    bench.check(f"{leg}: the client moved to {target} with its session", after is not None
                and after.get("connectedAt") == before.get("connectedAt"), json.dumps(_session(bench))[:300])
    bench.check(f"{leg}: {target} has the client's player, still dead", player.get("attached") is True
                and player.get("dead") is True, json.dumps(player))
    bench.check(f"{leg}: the client is still on the respawn screen", _medicals(bench))
    bench.check(f"{leg}: the Gateway did not end the client's session", not forced, json.dumps(forced)[:600])


def p2_hnd_bodyless(bench) -> None:
    """A dead client on the respawn screen moves with its partition, twice, and then respawns."""
    _join(bench)
    _require(bench, "SETUP: the client's partition is on node-1",
             _client_partition(_partitions(bench, "node-1")) is not None)
    _kill(bench, "node-1")
    # The body is the client's controlled entity until it respawns; its partition is the one that holds the client.
    partition = _poll(lambda: _client_partition(_partitions(bench, "node-1")), HANDOVER_TIMEOUT)
    _require(bench, "SETUP: node-1 reports the dead client in a partition", partition is not None)
    _move(bench, partition, "node-1", "node-2", "dead")
    _relog(bench)
    landed = _node_by_backend(bench, (_session(bench) or {}).get("backend", ""))
    _require(bench, "SETUP: the relogged client was routed to its home partition on node-2", landed == "node-2", landed)
    _move(bench, partition, "node-2", "node-1", "relogged")
    respawned = _try(bench.wait_spawned, CLIENT_ID)
    bench.check("the client respawns", isinstance(respawned, dict) and bench.living_character(CLIENT_ID) is not None,
                json.dumps(respawned)[:300])
    bench.check("the client was never force-reconnected", not _forced(bench), json.dumps(_forced(bench))[:600])
    bench.exercised(route="dead on node-1 -> node-2 -> relog -> node-1 -> respawn", partition=partition)


def p2_retire_bodyless(bench) -> None:
    """A node recycled under a dead, relogged client on the respawn screen: the client is never force-reconnected."""
    bench.start_cluster()
    bench.launch_client(CLIENT_ID)
    bench.attach_api(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    spawned = _poll(lambda: (lambda s: s if s and s.get("worldReady") and s.get("backend") else None)(_session(bench)),
                    120)
    _require(bench, "SETUP: the client is world-ready on a node", spawned is not None)
    node = _node_by_backend(bench, spawned["backend"])
    _require(bench, "SETUP: the client's node is known", bool(node), json.dumps(spawned)[:300])
    _kill(bench, node)
    relogged = _relog(bench)
    _require(bench, "SETUP: the relogged client is back on its node", relogged.get("backend") == spawned["backend"],
             json.dumps(relogged)[:300])
    recycled = bench.cluster.nodes_stop("recycle", node, reason="SE-1876: recycle under a dead client")
    bench.record("recycle", {"node": node, "answer": recycled})

    def moved():
        _try(bench.cluster.actualize_step)
        session = _session(bench) or {}
        return session if session.get("worldReady") and session.get("backend") not in (None, spawned["backend"]) else None

    after = _poll(moved, 420, 1)
    bench.record("moved", {"session": after, "forced": _forced(bench),
                           "carried": _lines(bench, "gateway", r"Handover of partition \d+ carries|state=adopted")})
    bench.check("the client is on another node now", after is not None, json.dumps(_session(bench))[:300])
    bench.check("the client kept its session", after is not None and after.get("connectedAt") == relogged.get("connectedAt"),
                json.dumps(after)[:300])
    bench.check("the client was never force-reconnected", not _forced(bench), json.dumps(_forced(bench))[:600])
    respawned = _try(bench.wait_spawned, CLIENT_ID)
    bench.check("the client respawns", isinstance(respawned, dict) and bench.living_character(CLIENT_ID) is not None,
                json.dumps(respawned)[:300])
    view = _try(bench.cluster.nodes_wait, lambda v: not any(n.get("node") == node for n in v.get("nodes") or []), 300)
    bench.check("the recycle ended: the node left the live list", isinstance(view, dict) and "error" not in view,
                json.dumps(view)[:300])
    bench.exercised(route=f"dead client relogged on {node} -> recycle -> "
                          f"{_node_by_backend(bench, (after or {}).get('backend', ''))} -> respawn")


p2_retire_bodyless.node_control = True
p2_retire_bodyless.spare_nodes = 1

SCENARIOS_BODYLESS = {
    "P2-HND-BODYLESS": (
        p2_hnd_bodyless,
        "SE-1876: a dead client on the respawn screen moves with its partition and respawns",
        "3",
    ),
    "P2-RETIRE-BODYLESS": (
        p2_retire_bodyless,
        "SE-1876: a node recycled under a dead, relogged client never force-reconnects it",
        "3",
    ),
}
