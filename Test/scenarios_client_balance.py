"""SE-1415: below the balance load floor, the balancer still evens out the players.

* ``P2-BAL-CLIENTS`` - three regular nodes with the deployed cost model (load floor 800000 sim-us/s). Three clients
  spawn on the same node, and two are teleported 100 km away in different directions, so that node holds three
  one-client partitions with no envelope neighbours. Every node stays far below the floor. On its own cadence the
  balancer must move client partitions until at least two nodes serve players, and it must make no load move (T-0409
  still holds those below the floor). The pins' own peak loads show the floor was never reached.

Live on octahedron-test (cluster 0.3.9, 2026-11-13) the nodes ran at sim 1.00 with peaks of 50k-400k sim-us/s. The
Gateway logged "Balancing held by the load floor" on 26,252 passes, and 73 players stayed on 3 of the 6 nodes for 9 h.
Before the restart at 17:43Z on 2026-11-12, the same cluster balanced only because slow nodes lifted the peak over
the floor. Red before the fix: every pass is held and the three clients stay on one node.
"""

from __future__ import annotations

import json
import re

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, _lines, _owners, _partitions
from scenarios_wa import CLIENT2_ID, CLIENT2_PORT, CLIENT3_ID, CLIENT3_PORT
from scenarios_wa_ops import _require

CLIENTS = ((CLIENT_ID, None), (CLIENT2_ID, CLIENT2_PORT), (CLIENT3_ID, CLIENT3_PORT))
OFFSETS = ((100_000.0, 0.0, 0.0), (0.0, 100_000.0, 0.0))
PIN = r"Automatic balance migration pinned: component of \d+ partition\(s\), kind (\w+)"


def _client_nodes(bench) -> dict[int, tuple[str, int]]:
    """Client id -> (node, partition), from every owning node's own partition report."""
    found = {}
    for node in sorted({row["Node"] for row in _owners(bench) if row.get("Node")}):
        try:
            rows = _partitions(bench, node)
        except Exception:  # noqa: BLE001 - a node mid-handover may not answer; the next poll sees it
            continue
        for row in rows:
            for client in row.get("clients") or []:
                found[client] = (node, row["id"])
    return found


def p2_bal_clients(bench) -> None:
    bench.start_cluster()
    for client_id, port in CLIENTS:
        if port is None:
            bench.start_client(client_id, render=False)
        else:
            bench.start_client(client_id, remote_port=port, render=False)
        bench.wait_spawned(client_id)
    placed = wait_until(
        lambda: (lambda f: f if all(c in f for c, _ in CLIENTS) else None)(_client_nodes(bench)),
        120,
        "all three clients in a reported partition",
    )
    bench.record("clients-spawned", placed)
    source = placed[CLIENT_ID][0]
    _require(
        bench,
        "SETUP: the three clients spawned on one node",
        len({n for n, _ in placed.values()}) == 1,
        json.dumps(placed),
    )
    centre = next(p["sphere"] for p in _partitions(bench, source) if p["id"] == placed[CLIENT_ID][1])
    pins_before = len(_lines(bench, "gateway", PIN))
    for (client_id, _), (dx, dy, dz) in zip(CLIENTS[1:], OFFSETS):
        bench.record(
            f"teleport-{client_id}",
            bench.cluster.node(
                source,
                f"/teleport?client={client_id}&x={centre['x'] + dx}&y={centre['y'] + dy}&z={centre['z'] + dz}",
                method="POST",
            ),
        )
    wait_until(
        lambda: (lambda f: f if len({p for _, p in f.values()}) == 3 else None)(_client_nodes(bench)),
        180,
        "three one-client partitions",
    )

    samples = []

    def spread():
        found = _client_nodes(bench)
        status = bench.cluster.get("/balance/status")
        samples.append({"clients": found, "status": status})
        return found if len({n for n, _ in found.values()}) >= 2 and all(c in found for c, _ in CLIENTS) else None

    try:
        after = wait_until(spread, 600, "the balancer to spread the clients", interval=15)
    except ClusterError:
        after = samples[-1]["clients"] if samples else {}
    bench.record("balance-trace", samples)
    bench.record("clients-after", after)
    bench.record("held-lines", _lines(bench, "gateway", r"Balancing held by the load floor")[-5:])
    pins = _lines(bench, "gateway", PIN)[pins_before:]
    bench.record("pin-lines", pins)
    kinds = [re.search(PIN, line).group(1) for line in pins]
    peaks = [int(m.group(1)) for m in (re.search(r"peak load (\d+)->", line) for line in pins) if m]
    _require(bench, "SETUP: every pin happened below the 800000 sim-us/s load floor", all(p < 800_000 for p in peaks), str(peaks))
    nodes = sorted({n for n, _ in after.values()})
    bench.check(
        "below the load floor the balancer spreads the players over at least two nodes (SE-1415)",
        len(nodes) >= 2,
        json.dumps(after),
    )
    bench.check("no load move below the floor (T-0409)", "load" not in kinds, str(kinds))
    for client_id, _ in CLIENTS:
        bench.check(f"client {client_id} still has a living character", bench.living_character(client_id) is not None)
    bench.exercised(route=f"3 clients on {source} -> balancer -> {', '.join(nodes)}; pins {kinds}")


p2_bal_clients.regular_nodes = 3

SCENARIOS_CLIENT_BALANCE = {
    "P2-BAL-CLIENTS": (
        p2_bal_clients,
        "SE-1415: below the load floor the balancer still evens out the players",
        "4",
    ),
}
