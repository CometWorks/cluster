"""SE-1415: a recycled node's players spread over the nodes instead of moving together onto its replacement.

* ``P2-ROT-SPREAD`` - three regular nodes and one hot spare under node control. Three clients spawn on the same node,
  two of them are teleported 100 km away in different directions, so the node holds three one-client partitions
  that are not envelope neighbours. That node is recycled: the spare is activated in its place and the drain
  hands its partitions out. The three client partitions must end up on at least two different nodes, and no
  partition may stay with the recycled node.

Live on octahedron-test (cluster 0.3.9, 2026-11-13T04:52:50Z) the 73 players sat in 7 station partitions on 3 of the 6
serving nodes. The Gateway's rotation drains a node whole into the activated spare (T-0320), so each node's crowd
moved together every 20 minutes and the players never spread: the paced balancer holds below its 800000 sim-us/s
load floor. The fix sends a recycled node's client-carrying components to the receivers with the fewest clients.
Red before it: all three partitions land on the replacement.
"""

from __future__ import annotations

import json

from cluster import wait_until
from scenarios_core import CLIENT_ID, _owners, _partitions
from scenarios_wa import CLIENT2_ID, CLIENT2_PORT, CLIENT3_ID, CLIENT3_PORT
from scenarios_wa_ops import _require

CLIENTS = ((CLIENT_ID, None), (CLIENT2_ID, CLIENT2_PORT), (CLIENT3_ID, CLIENT3_PORT))
OFFSETS = ((0.0, 0.0, 0.0), (100_000.0, 0.0, 0.0), (0.0, 100_000.0, 0.0))


def _client_nodes(bench) -> dict[int, tuple[str, int]]:
    """Client id -> (node, partition), from every owning node's own partition report."""
    owners = {row["Partition"]: row["Node"] for row in _owners(bench) if row.get("Node")}
    found = {}
    for node in sorted(set(owners.values())):
        try:
            rows = _partitions(bench, node)
        except Exception:  # noqa: BLE001 - a node closing under us reports nothing; the next poll sees the rest
            continue
        for row in rows:
            for client in row.get("clients") or []:
                found[client] = (node, row["id"])
    return found


def p2_rot_spread(bench) -> None:
    bench.start_cluster()
    for client_id, port in CLIENTS:
        if port is None:
            bench.start_client(client_id, render=False)
        else:
            bench.start_client(client_id, remote_port=port, render=False)
        bench.wait_spawned(client_id)
    placed = wait_until(lambda: (lambda f: f if all(c in f for c, _ in CLIENTS) else None)(_client_nodes(bench)),
                        120, "all three clients in a reported partition")
    bench.record("clients-spawned", placed)
    source = placed[CLIENT_ID][0]
    _require(bench, "SETUP: the three clients spawned on one node", len({n for n, _ in placed.values()}) == 1,
             json.dumps(placed))
    centre = next(p["sphere"] for p in _partitions(bench, source) if p["id"] == placed[CLIENT_ID][1])
    for (client_id, _), (dx, dy, dz) in zip(CLIENTS[1:], OFFSETS[1:]):
        bench.record(f"teleport-{client_id}", bench.cluster.node(
            source, f"/teleport?client={client_id}&x={centre['x'] + dx}&y={centre['y'] + dy}&z={centre['z'] + dz}",
            method="POST"))

    def separated():
        found = _client_nodes(bench)
        apart = len({p for _, p in found.values()}) == 3 and all(found.get(c, ("",))[0] == source for c, _ in CLIENTS)
        return found if apart else None

    before = wait_until(separated, 180, f"three one-client partitions on {source}")
    bench.record("clients-before", before)

    bench.record("recycle", bench.cluster.nodes_stop("recycle", source, reason="SE-1415 P2-ROT-SPREAD"))

    def drained():
        if any(row.get("Node") == source for row in _owners(bench)):
            return None
        found = _client_nodes(bench)
        return found if all(c in found for c, _ in CLIENTS) else None

    bench.cluster.nodes_wait(lambda view: drained() is not None, timeout=900)
    after = wait_until(drained, 60, "the drained clients' partitions")
    bench.record("clients-after", after)
    nodes = sorted({n for n, _ in after.values()})
    bench.check(f"{source} handed out everything it owned", not any(row.get("Node") == source for row in _owners(bench)))
    bench.check("the recycled node's three crews spread over at least two nodes (SE-1415)", len(nodes) >= 2,
                json.dumps(after))
    for client_id, _ in CLIENTS:
        bench.check(f"client {client_id} still has a living character", bench.living_character(client_id) is not None)
    bench.exercised(route=f"3 clients on {source} -> recycle -> {', '.join(nodes)}")


p2_rot_spread.regular_nodes = 3
p2_rot_spread.spare_nodes = 1
p2_rot_spread.node_control = True

SCENARIOS_ROTATION_SPREAD = {
    "P2-ROT-SPREAD": (
        p2_rot_spread,
        "SE-1415: a recycled node's players spread over the nodes, not all onto its replacement",
        "4",
    ),
}
