"""SE-1589: a player's Steam skins reach the node, and the node keeps an owned skin on a painted block.

* ``P1-SKINS`` - a Direct Transport client spawns on node-1.

  1. The client's inventory request (``RequestUpdateClientGameInventory``) runs through the game's own handler on
     node-1, as if the client had sent it. The node must forward the blob to the Gateway, which answers and the
     node logs a ``skins_check`` line. A bench player has no Steam session, so the Gateway refuses the blob (404 or
     503); the line is what proves the node asked.
  2. The client paints the survival kit with Battered_Armor. It owns no skin, so the node strips the skin and
     paints only the colour.
  3. The node gets Battered_Armor for the client, as a confirmed ``skins_check`` leaves it, and the client paints
     again: the skin stays. ValidateItem, the gate of hand placement and painting, keeps Battered_Armor and still
     strips Rusty_Armor.

Live (Octahedron, cluster 0.3.17, DIS-0088) a hand-placed block came out with the default skin and couldn't be
painted with an owned one: ``GameInventoryRequestPatch`` answered every client's request by dropping it, so the
node never learned any skin (``game_inventory_check_ignored`` in the node logs, no ``skins_check`` anywhere).
Step 1 fails there; steps 2 and 3 pass on both, since the node's skin gate itself was right.
"""

from __future__ import annotations

import json
import random

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, _lines

OWNED = "Battered_Armor"
OTHER = "Rusty_Armor"


def _kit(api) -> dict:
    return wait_until(lambda: next(iter(api.find_nearby_blocks(block_type="SurvivalKit", max_distance=100, limit=1)), None),
                      120, "a replicated survival kit near the client")


def _paint(bench, api, kit: dict, label: str) -> dict:
    """Paint the kit with a fresh colour and OWNED; the node's block once the colour has arrived."""
    hsv = (round(random.uniform(0.05, 0.95), 3), 0.3, 0.2)
    bench.record(f"paint-{label}", api.character_grid_event(kit["gridId"], tuple(kit["min"]), hsv=hsv, skin=OWNED))
    x, y, z = kit["min"]
    path = f"/test-block-skin?gridId={kit['gridId']}&x={x}&y={y}&z={z}"
    block: dict = {}

    def painted() -> bool:
        nonlocal block
        block = bench.cluster.node("node-1", path)
        return bool(block.get("hsv")) and abs(block["hsv"][0] - hsv[0]) < 0.01

    try:
        wait_until(painted, 30, f"{label}: the paint on node-1")
        arrived = True
    except ClusterError:
        arrived = False
    bench.record(f"block-{label}", block)
    bench.check(f"{label}: the paint reached node-1 (colour)", arrived, json.dumps({"hsv": hsv, "block": block}))
    return block


def p1_skins(bench) -> None:
    """The node forwards a client's skin inventory to the Gateway and keeps owned skins on painted blocks."""
    bench.start_cluster(nodes=["node-1", "wa-1"])
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)

    pattern = rf"skins_check client={CLIENT_ID} "
    before = len(_lines(bench, "node-1", pattern))
    bench.record("request", bench.cluster.node("node-1", f"/test-skin-request?steamId={CLIENT_ID}&text=SE-1589", "POST"))
    try:
        wait_until(lambda: len(_lines(bench, "node-1", pattern)) > before, 30, "node-1's skins_check line")
    except ClusterError:
        pass
    checked = _lines(bench, "node-1", pattern)[before:]
    ignored = _lines(bench, "node-1", rf"game_inventory_check_ignored client={CLIENT_ID}")
    bench.record("skins-check", {"checked": checked, "ignored": ignored})
    bench.check("node-1 forwards the client's inventory request to the Gateway", bool(checked),
                json.dumps({"checked": checked, "ignored": ignored}))

    kit = _kit(api)
    bench.record("kit", kit)
    block = _paint(bench, api, kit, "unowned")
    bench.check(f"an unowned {OWNED} is stripped from the painted block", block.get("skin") == "", json.dumps(block))

    bench.record("grant", bench.cluster.node("node-1", f"/test-skin-grant?steamId={CLIENT_ID}&skins={OWNED}", "POST"))
    block = _paint(bench, api, kit, "owned")
    bench.check(f"an owned {OWNED} stays on the painted block", block.get("skin") == OWNED, json.dumps(block))
    gate = bench.cluster.node("node-1", f"/test-entitlements?steamId={CLIENT_ID}&validate={OWNED},{OTHER}")["validated"]
    bench.record("validated", gate)
    bench.check(f"ValidateItem keeps {OWNED} and strips the unowned {OTHER}", gate == {OWNED: OWNED, OTHER: ""},
                json.dumps(gate))
    bench.exercised(route="node-1: Direct Transport client, inventory request through the game's handler, paint events")


p1_skins.regular_nodes = 1

SCENARIOS_SKINS = {
    "P1-SKINS": (p1_skins, "SE-1589: a client's skin inventory reaches the Gateway; owned skins stay on painted blocks", "1"),
}
