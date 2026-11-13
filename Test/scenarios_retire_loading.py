"""SE-1448: a node recycled while a client is still loading the world on it.

* ``P2-RETIRE-LOADING`` - node control on, two serving nodes and a hot spare. A client joins; as soon as the
  Gateway has bound it to a node, and before it is world-ready, that node is recycled. A first join is in no
  partition until it spawns, so the drain read the node as owning nothing and closed it under the load; the
  launcher killed it and the client was force-reconnected with ``node-link-lost`` (octahedron-test 2026-11-13,
  cluster 0.3.11). Now the node stays open until the client is world-ready, the client is switched to the spare
  that replaces the node, and it spawns there; the recycle still ends.

No timers to shorten: the recycle starts the moment the client is bound, and the empty node drains in seconds.
"""

from __future__ import annotations

import json
import time

from cluster import _field
from scenarios_core import CLIENT_ID, _lines
from scenarios_wa_ops import _poll, _require, _session, _try


def _node_by_backend(bench, backend: str) -> str:
    """The live registry node at ``backend`` (the registry status serializes PascalCase)."""
    rows = _field(bench.cluster.get("/registry/status"), "nodes") or []
    return next((str(_field(row, "node")) for row in rows if _field(row, "endpoint") == backend), "")


def _force_reconnects(bench) -> list[str]:
    return _lines(bench, "gateway", rf"Force reconnect client {CLIENT_ID}:")


def p2_retire_loading(bench) -> None:
    """A node recycled while a client loads the world on it waits for the client, which then spawns on the spare."""
    bench.start_cluster()
    bench.launch_client(CLIENT_ID)
    bound = _poll(
        lambda: (lambda s: s if s and s.get("backend") and not s.get("worldReady") else None)(_session(bench)), 420, 0.5
    )
    _require(bench, "SETUP: the Gateway bound the client to a node before it was world-ready", bound is not None)
    node = _node_by_backend(bench, bound["backend"])
    _require(bench, "SETUP: the client's node is known", bool(node), json.dumps(bound)[:300])
    recycled = bench.cluster.nodes_stop("recycle", node, reason="SE-1448: recycle under a loading client")
    bench.record("recycle", {"bound": bound, "node": node, "answer": recycled})

    # The launcher kills the node once the registry plans its kill; the client loads meanwhile.
    ready_at = closed_at = None
    deadline = time.time() + 300
    while time.time() < deadline and ready_at is None:
        _try(bench.cluster.actualize_step)
        session = _session(bench) or {}
        if closed_at is None and _lines(bench, "gateway", rf"retiring node {node} owns nothing; closed"):
            closed_at = time.time()
        if session.get("worldReady"):
            ready_at = time.time()
        if not session and _force_reconnects(bench):
            break
        time.sleep(1)
    bench.record(
        "load",
        {
            "readyAt": ready_at,
            "closedAt": closed_at,
            "forceReconnect": _force_reconnects(bench),
            "waited": _lines(bench, "gateway", rf"retiring node {node} owns nothing but client"),
        },
    )
    bench.check("the client became world-ready", ready_at is not None, json.dumps(_force_reconnects(bench)))
    bench.check(
        "the recycled node was not closed while the client was loading on it",
        closed_at is None or (ready_at is not None and closed_at >= ready_at - 1),
        f"closed {closed_at} ready {ready_at}",
    )
    spawned = isinstance(_try(bench.wait_spawned, CLIENT_ID), dict) and bench.living_character(CLIENT_ID) is not None
    bench.check("the client spawns", spawned)
    bench.check(
        "the client was never force-reconnected", not _force_reconnects(bench), json.dumps(_force_reconnects(bench))
    )
    view = _try(bench.cluster.nodes_wait, lambda v: not any(n.get("node") == node for n in v.get("nodes") or []), 300)
    bench.check(
        "the recycle ended: the node left the live list",
        isinstance(view, dict) and "error" not in view,
        json.dumps(view)[:300],
    )
    session = _session(bench) or {}
    bench.check(
        "the client is on another node now",
        bool(session) and session.get("backend") != bound["backend"],
        json.dumps(session)[:300],
    )
    bench.exercised(
        route=f"client loading on {node} -> recycle -> {_node_by_backend(bench, session.get('backend', ''))}"
    )


p2_retire_loading.node_control = True
p2_retire_loading.spare_nodes = 1

SCENARIOS_RETIRE_LOADING = {
    "P2-RETIRE-LOADING": (
        p2_retire_loading,
        "SE-1448: a node recycled while a client loads the world on it waits for the client",
        "3",
    ),
}
