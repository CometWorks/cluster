"""SE-1461: a seed partition whose id another node took keeps its content and moves under a fresh id.

On the cluster 0.3.11 nav bench node-2 booted first and minted node-1's seed ids for its clients' layouts (SE-1443,
fixed since by never minting a seed id). node-1's startup claims of those seeds then got "Partition N is generation
1, not 0", node-1 fenced them as stale copies, and a minute later its fenced-orphan purge closed their content:
Gamma Station (partition 5) left the world, and every dock at it failed. Meanwhile the Gateway saw partition 5 on
node-1 and the registry gave it to node-2, so every co-location handover of it failed too.

``P3-SEED-COLLIDE`` makes that collision on purpose with two bench seams. node-2 runs with
CLUSTER_TEST_MINT_SEED_IDS=1 (0.3.11's id reserve) and boots alone with wa-1. node-1 boots with
CLUSTER_TEST_STARTUP_CLAIM_DELAY_MS, so it loads its seed saves and then holds its startup claims. In that window a
stray grid far from everything gets a new layout on node-2, under one of node-1's seed ids, and node-1's claim of
that seed is refused. Green: node-1 keeps the seed's members, they get a new partition with a fresh id that the
registry gives to node-1, and nothing of the seed is purged. Red (the 0.3.11 rule: every fenced partition owned
elsewhere is purged): the seed's members are closed.
"""

from __future__ import annotations

import json
import time

from scenarios_core import _lines, _owners, _partitions
from scenarios_fast import _poll, _spawn, _where
from scenarios_seed_id import _registry_owners, _seed_ids

BASE = 900_000.0          # m, far from every seed partition: each stray makes a layout of its own
CLAIM_DELAY_MS = 90_000   # node-1 holds its startup claims this long after its seed saves loaded
REHOME_S = 300            # three registry probes a minute apart, then the next membership pass


def p3_seed_collide(bench) -> None:
    env = bench.cluster.config.node_env_by_id
    env["node-2"] = {**env.get("node-2", {}), "CLUSTER_TEST_MINT_SEED_IDS": "1"}
    env["node-1"] = {**env.get("node-1", {}), "CLUSTER_TEST_STARTUP_CLAIM_DELAY_MS": str(CLAIM_DELAY_MS)}
    bench.start_cluster(nodes=["wa-1", "node-2"])
    bench.cluster.post("/automatic/pause")   # no balancing: every partition stays where it is made
    slots = _seed_ids(bench)
    bench.record("seed-ids", slots)
    mine = set(slots.get(1) or [])
    bench.check("the seed has partitions in slot 1 (node-1's)", bool(mine), json.dumps(slots))

    bench.cluster.start_node("node-1")
    held = _poll(lambda: _lines(bench, "node-1", r"startup claims held") or None, 600)
    bench.check("node-1 loaded its seed saves and holds its startup claims", bool(held))

    # Strays on node-2 until one of them takes a seed id of node-1.
    collided = None
    for index in range(4):
        stray = _spawn(bench, "node-2", BASE + index * 100_000.0, static=False)

        def claimed():
            _, partition, _ = _where(bench, stray)
            return partition if partition is not None and _registry_owners(bench).get(partition) == "node-2" else None
        partition = _poll(claimed, 30)
        bench.record(f"stray-{index}", {"grid": stray, "partition": partition})
        if partition in mine:
            collided = partition
            break
    bench.check("a stray's layout on node-2 took a seed id of node-1", collided is not None, f"node-1 seed ids {sorted(mine)}")
    if collided is None:
        return

    bench.cluster.wait_ready(nodes=["node-1"], timeout=CLAIM_DELAY_MS / 1000 + 300)
    # "Game ready" comes before the held claims: wait out the hold. The members are read while it lasts, because
    # the re-home follows the refusal at once and the fenced row is then gone.
    members = []

    def refused():
        if not members:
            row = next((p for p in _partitions(bench, "node-1") if p["id"] == collided), None) or {}
            members.extend(m["id"] for m in row.get("members") or [])
        return _lines(bench, "node-1", rf"startup partition fenced, waiting for recovery: {collided} ") or None
    fenced = _poll(refused, CLAIM_DELAY_MS / 1000 + 120)
    bench.check(f"node-1's startup claim of seed {collided} is refused", bool(fenced))
    bench.record("fenced-seed", {"partition": collided, "members": members})
    bench.check("node-1 held the seed's members before the refusal", bool(members))

    def rehomed():
        places = {_where(bench, member)[:2] for member in members}
        if len(places) != 1:
            return None
        node, partition = next(iter(places))
        return (node, partition) if node == "node-1" and partition not in (None, collided) \
            and _registry_owners(bench).get(partition) == "node-1" else None
    place = _poll(rehomed, REHOME_S) if members else None
    time.sleep(5)
    purged = [line for line in _lines(bench, "node-1", rf"purged stale fenced partition: {collided} ")
              if "(0 leftover" not in line]
    bench.record("outcome", {"place": place, "where": [_where(bench, m)[:2] for m in members], "purged": purged[-3:],
                             "rehomed": _lines(bench, "node-1", r"re-homed startup partition")[-3:]})
    bench.check("no member of the seed was purged", not purged, "\n".join(purged[-2:]))
    bench.check("the seed's members live on node-1 in one partition with a fresh id the registry gives to node-1",
                place is not None, json.dumps([_where(bench, m)[:2] for m in members]))
    owners = {int(r["Partition"]): r.get("Node") for r in _owners(bench)}
    bench.check(f"the registry still gives id {collided} to node-2's stray", owners.get(collided) == "node-2",
                str(owners.get(collided)))


SCENARIOS_SEED_COLLIDE = {
    "P3-SEED-COLLIDE": (p3_seed_collide, "SE-1461: a seed whose id another node took keeps its content under a fresh id instead of being purged", "2"),
}
