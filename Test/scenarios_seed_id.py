"""SE-1443: a split must never mint a partition id that is another node's seed partition.

MagnetarWorld writes the seed partitions with ids 1..N over both catalog slots, and a fresh registry hands out ids from
1 too: it learns a seed id only when its node's startup claim lands. Cluster 0.3.11 skipped only the seed ids of the
node's own slot, so a reserve filled before the other node's startup claims held that node's seed ids. In the SE-1425
nav rig node-1 split a 300 m/s ship off into id 8 a minute after node-2 had claimed (and merged away) its seed
partition 8; the registry refused the split and node-1 fenced, so all its partitions rolled back to their last saves.

``P3-SEED-ID`` makes that order happen on purpose: wa-1 and node-1 boot alone, a stray grid far from everything makes
node-1 fill its id reserve while node-2's seed ids are free, node-2 boots and claims its seed partitions, and a ship
then leaves the stray's partition at 300 m/s, so node-1 splits it off with the next id of its reserve. With the fix no
new layout of node-1 carries a seed id, node-2 claims every seed partition of its slot, and the split commits. On
0.3.11 the stray's layout takes a slot-2 seed id, and node-2 then skips that seed partition ("catalog seed skipped:
the registry row is at generation 1"): its content never loads. With more slot-2 seed ids than the stray used, the
split takes the next one, which the registry refuses: node-1 fences.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError
from scenarios_core import _lines, _owners, _partitions
from scenarios_fast import _poll, _spawn, _where

BASE = 900_000.0      # m, far from every seed partition: the stray makes a layout of its own
SPEED = 300.0         # m/s, the SE-1425 cruise ship
FLIGHT_S = 120        # 36 km: past the split gate (reach at 300 m/s is about 11 km)


def _seed_ids(bench) -> dict[int, list[int]]:
    seed = json.loads((bench.cluster.config.split_world / "catalog-seed.json").read_text())
    slots: dict[int, list[int]] = {}
    for entry in seed["partitions"]:
        slots.setdefault(int(entry.get("slot") or 0), []).append(int(entry["id"]))
    return slots


def _registry_owners(bench) -> dict[int, str | None]:
    return {int(row["Partition"]): row.get("Node") for row in _owners(bench)}


def _alive(bench, node: str) -> bool:
    try:
        _partitions(bench, node)
        return True
    except ClusterError:
        return False


def p3_seed_id(bench) -> None:
    bench.start_cluster(nodes=["wa-1", "node-1"])
    bench.cluster.post("/automatic/pause")   # no balancing: the stray's partition stays on node-1
    slots = _seed_ids(bench)
    bench.record("seed-ids", slots)
    other = set(slots.get(2) or [])
    bench.check("the seed has partitions in slot 2 (node-2's)", bool(other), json.dumps(slots))

    # node-1 fills its id reserve while node-2's seed ids are still free in the registry.
    stray = _spawn(bench, "node-1", BASE, static=False)

    def stray_claimed():
        _, partition, _ = _where(bench, stray)
        return partition if partition is not None and _registry_owners(bench).get(partition) == "node-1" else None
    stray_partition = _poll(stray_claimed, 60)
    bench.record("stray", {"grid": stray, "partition": stray_partition})
    bench.check("the stray's new layout is claimed by node-1", stray_partition is not None)
    bench.check("the stray's new layout does not take a seed id of node-2", stray_partition not in other,
                f"partition {stray_partition}, node-2 seed ids {sorted(other)}")

    # node-2 boots and claims its seed partitions.
    bench.cluster.start_node("node-2")
    bench.cluster.wait_ready(nodes=["node-2"])
    claimed = _poll(lambda: all(_registry_owners(bench).get(p) == "node-2" for p in other) or None, 90)
    owners = _registry_owners(bench)
    bench.record("seed-owners", {p: owners.get(p) for p in sorted(other)})
    bench.check("node-2 holds every seed partition of its slot", bool(claimed), json.dumps({p: owners.get(p) for p in sorted(other)}))
    skipped = _lines(bench, "node-2", r"catalog seed skipped")
    bench.check("node-2 loads every seed partition of its slot (none skipped as already in the registry)", not skipped, "\n".join(skipped[:3]))

    # A ship joins the stray, then leaves it at 300 m/s: node-1 splits it off with the next id of its reserve.
    ship = _spawn(bench, "node-1", BASE + 100, static=False)
    joined = _poll(lambda: _where(bench, ship)[1] == stray_partition or None, 60)
    bench.check("the ship joins the stray's partition", bool(joined), str(_where(bench, ship)))
    time.sleep(12)   # past the 10 s merge maturity
    splits0 = len(_lines(bench, "node-1", r"topology Split committed"))
    owner = _where(bench, ship)[0] or "node-1"
    bench.check("the stray's partition is still node-1's", owner == "node-1", owner)
    bench.cluster.node(owner, f"/fast-test-drive?entity={ship}&vx={SPEED}&vy=0&vz=0&seconds={FLIGHT_S}&mode=teleport", "POST")

    def left():
        if not _alive(bench, "node-1"):
            return "node-1 gone"
        _, partition, _ = _where(bench, ship)
        return partition if partition is not None and partition != stray_partition else None
    outcome = _poll(left, FLIGHT_S + 30)
    time.sleep(5)
    node, piece, _ = _where(bench, ship)
    refused = _lines(bench, "node-1", r"not allocated or is already in use|registry rejection is terminal|refused for its new partition id")
    bench.record("split", {"ship": ship, "outcome": outcome, "node": node, "partition": piece,
                           "minted": _lines(bench, "node-1", r"topology minted_piece")[-5:], "refused": refused[-5:]})
    bench.check("node-1 is still serving after the split", _alive(bench, "node-1"), "\n".join(refused[-3:]))
    bench.check("the ship left the stray's partition by a committed split",
                len(_lines(bench, "node-1", r"topology Split committed")) > splits0 and piece is not None and piece != stray_partition,
                f"ship in {node}/{piece}, outcome {outcome}")
    bench.check("the split's piece does not carry a seed id of node-2", piece not in other, f"piece {piece}")
    bench.check("no topology apply was refused for its partition id", not refused, "\n".join(refused[-3:]))


SCENARIOS_SEED_ID = {
    "P3-SEED-ID": (p3_seed_id, "SE-1443: an id reserve filled before the other node's startup claims never mints its seed ids; the split commits", "2"),
}
