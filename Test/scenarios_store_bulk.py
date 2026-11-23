"""SE-1892: a bulk store purchase adds its items in one call, not one call per unit.

octahedron-test, 2026-11-23, after SE-1525's async payments: 54 of the 87 station purchases of 20,000 units or more
(trader bots buying Nickel, Iron and Platinum ingots) were in 300-840 ms frames, and none of the 934 smaller ones.
Vanilla's MyStoreBlock.BuyPhysicalItemInternal adds a purchase with ``amount`` calls of inventory.AddItems(1, item),
each recomputing the inventory's volume and mass and raising its change events.

One regular node + WA, no client. ``/test-store-bulk-add`` adds N iron ingots to an empty large cargo container of a
dynamic ship on node-1's game thread, with the loop's AddItems call as vanilla makes it and as the cluster's
transpiler rewrites it (MyStoreBlockPatch.AddPurchased). Both must leave the same amount; the patched loop must take a
small fraction of vanilla's time. The node starting at all proves the transpiler found its anchors.

* ``P2-STORE-BULK-ADD`` - 78,063 ingots (the largest live purchase), three rounds of each mode.
"""
from __future__ import annotations

import statistics

from cluster import ClusterError

NODE = "node-1"
STARTED = [NODE, "wa-1"]
AMOUNT = 78063
ROUNDS = 3


def _add(bench, mode: str) -> dict:
    return bench.cluster.node(NODE, f"/test-store-bulk-add?amount={AMOUNT}&mode={mode}", "POST", timeout=120)


def p2_store_bulk_add(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    runs = {mode: [_add(bench, mode) for _ in range(ROUNDS)] for mode in ("vanilla", "patched")}
    bench.record("bulk-add", runs)
    loop = statistics.median(r["ms"] for r in runs["vanilla"])
    batched = statistics.median(r["ms"] for r in runs["patched"])
    amounts = {r["inInventory"] for mode in runs.values() for r in mode}
    if not bench.check(f"both modes leave {AMOUNT} ingots", amounts == {float(AMOUNT)}, str(amounts)):
        raise ClusterError(f"amounts {amounts}")
    bench.check(f"patched loop {batched:.1f} ms is under a tenth of vanilla's {loop:.1f} ms",
                batched * 10 < loop)
    bench.exercised(amount=AMOUNT, vanillaMedianMs=loop, patchedMedianMs=batched)


SCENARIOS_STORE_BULK = {
    "P2-STORE-BULK-ADD": (p2_store_bulk_add, "SE-1892: a bulk store purchase's items are added in one call", "3"),
}
