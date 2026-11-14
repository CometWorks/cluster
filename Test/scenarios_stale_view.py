"""SE-1554: what a client still holds of a grid after a handover moved its view to another node.

``P3-HND-STALE`` - one client beside a fixture grid (small cargo container, battery, seat, ore detector) that
rides its partition. Reported live on 2026-11-14 after two handovers of the reporter's partition: a cargo
container shown with mass and volume but no items, inventories with wrong readings, freshly placed blocks that
could not be used, all gone after a relog.

THE INVENTORY. An inventory's items reach a client as deltas against what the node knows the client has: amounts
relative, each delta under the next message id, applied by the client strictly in id order. Mass and volume are
plain Syncs. A delta goes out within a few frames of the change, so a handover finds one undelivered only when
the inventory is changing (the reporter was mining). The test plugin makes both undelivered states last
(``/stale-view-test-hold``, ``/stale-view-test-lose``), by ``SE1554_ARM``:

* ``lost``: nickel goes in and its delta is sent but never arrives (unacknowledged on the source).
* ``pending``: more iron and silicon go in and wait as the source's unsent diff.
* ``both`` (default): the lost delta, then the pending change.

After the handover the client's list of the container must be the target node's, and stay it when gold goes in
on the target. A build without the fix leaves the client with the list it had: after ``pending`` the amounts
are off and the new item is missing for good; after ``lost`` nothing later arrives at all.

The control before it: the client opens the container and gets the node's items (and the node's self-test
section ``inventory-held`` passes, where the build has it).

``P3-HND-STALE-ORE`` - the same fixture beside a procedural asteroid, the client seated on it: the deposits its
ore detector marks (Remote's ``hud-signals``) must survive two handovers, and a fresh scan after each must find
them again. The ore search runs on the client alone, against voxels it generated itself.

THE PLACED BLOCK. The client places a small cargo container on the fixture grid before the handover (the
control) and another after it. Each must exist on the node under the entity id the client has, with its
replicables in the client's view; a name the client gives it must reach the node, ore the node puts in must
show on the client, and the client must be able to open it. The one placed before is used again after the
handover. Neither arm failed on the build without the fix: this symptom was not reproduced.

Knobs: SE1554_ARM, SE1554_DISTANCE (12 m: the fixture's offset from the client), SE1554_HANDOVERS (1).
"""

from __future__ import annotations

import json
import os
import time

from cluster import ClusterError
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _lines, _partitions
from scenarios_redeliver import _poll, _require, _try, _xyz

NODES = ("node-1", "node-2")
DISTANCE = float(os.environ.get("SE1554_DISTANCE", "12"))
HANDOVERS = int(os.environ.get("SE1554_HANDOVERS", "1"))
ARM = os.environ.get("SE1554_ARM", "both").lower()
REACH = 3.0


def _client_node(bench) -> tuple[str, int] | None:
    for node in NODES:
        partition = _client_partition(_try(lambda: _partitions(bench, node), []) or [])
        if partition is not None:
            return node, partition
    return None


def _server(bench, block: int) -> tuple[str, dict] | None:
    """The node that has the block, and its state there with its view of the client."""
    for node in NODES:
        state = _try(lambda: bench.cluster.node(node, f"/stale-view-test-state?block={block}&client={CLIENT_ID}"))
        if state and state.get("block") == block:
            return node, state
    return None


def _add_ore(bench, block: int, subtype: str, amount: float) -> dict:
    node, _ = _server(bench, block)
    return bench.cluster.node(node, f"/stale-view-test-items?block={block}&subtype={subtype}&amount={amount}", "POST")


def _amounts(items: list[dict]) -> dict[str, float]:
    """subtype -> amount, of a node's item rows (amount) or the client's (amountRaw, 1e6 per unit)."""
    totals: dict[str, float] = {}
    for item in items or []:
        name = item.get("subtype") or item.get("subtypeId")
        amount = item["amount"] if "amount" in item else item["amountRaw"] / 1e6
        totals[name] = round(totals.get(name, 0.0) + amount, 3)
    return totals


def _client_items(api, grid: int, cell) -> dict[str, float] | None:
    answer = _try(lambda: api.get_inventory(grid, tuple(cell)))
    return None if answer is None else _amounts(answer.get("items"))


def _close_screens(api) -> None:
    for _ in range(4):
        screens = _try(api.list_screens, []) or []
        index = next((i for i, screen in enumerate(screens) if "Terminal" in json.dumps(screen)), None)
        if index is None:
            return
        _try(lambda: api.close_screen(index))
        time.sleep(0.5)


def _open(bench, api, block: int, position, label: str) -> bool:
    """Stand next to the block, aim at it and Use it until the node sees the client in an inventory screen."""
    x, y, z = position
    for offset in ((-REACH, 0, 0), (0, 0, REACH), (0, 0, -REACH), (0, REACH, 0)):
        _try(lambda: api.character_teleport(x + offset[0], y + offset[1], z + offset[2]))
        time.sleep(1.5)
        for _ in range(3):
            _try(lambda: api.character_look_at(x, y, z, tolerance=1))
            used = _try(api.character_use)
            context = _poll(lambda: (lambda s: s if s and s[1].get("clientContext") == "Inventory" else None)(
                _server(bench, block)), 6, interval=0.5)
            if context:
                bench.record(f"open-{label}", {"offset": offset, "used": used, "screens": _try(api.list_screens)})
                return True
            _close_screens(api)
    bench.record(f"open-{label}", {"failed": True, "screens": _try(api.list_screens), "state": _server(bench, block)})
    return False


def _shown(bench, api, block: int, grid: int, cell, position, label: str, timeout: float = 30) -> dict:
    """Open the block's inventory on the client and report what it lists against what the node has."""
    opened = _open(bench, api, block, position, label)
    want = _amounts(_server(bench, block)[1]["inventories"][0]["items"])
    got = _poll(lambda: (lambda items: items if items == want else None)(_client_items(api, grid, cell)),
                timeout if opened else 1, interval=1)
    result = {"opened": opened, "server": want, "client": got if got is not None else _client_items(api, grid, cell),
              "state": _server(bench, block)}
    bench.record(f"shown-{label}", result)
    _close_screens(api)
    time.sleep(1)
    return result


def _handover(bench, api, grid: int, index: int) -> str:
    place = _client_node(bench)
    _require(bench, f"handover {index}: the client is in a partition", place, json.dumps(place))
    source, partition = place
    target = next(node for node in NODES if node != source)
    before = len(_lines(bench, "gateway", rf"partition={partition} state=committed"))
    bench.record(f"handover-{index}", bench.cluster.post(
        _handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300))
    committed = _poll(lambda: len(_lines(bench, "gateway", rf"partition={partition} state=committed")) > before or None,
                      120, interval=2)
    _require(bench, f"handover {index}: {source} -> {target} committed", committed)
    moved = _poll(lambda: (lambda found: found if found and found[0] == target else None)(_client_node(bench)), 120, 2)
    _require(bench, f"handover {index}: the client is on {target}", moved, json.dumps(_client_node(bench)))
    _require(bench, f"handover {index}: {target} adopted the client's view",
             _lines(bench, target, rf"client-view adopt client={CLIENT_ID} "))
    print(f"  [INFO] handover {index}: partition {partition} {source} -> {target}")
    time.sleep(3)
    return target


def _setup(bench):
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition moves are the forced handovers
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    for flag in ("creativeTools", "useTerminals", "invulnerable"):
        bench.record(f"admin-{flag}", _try(lambda: api.set_admin_flag(flag, True)))
    position = _xyz(api.get_character().get("position"))
    _require(bench, "the client reports its position", position, str(api.get_character())[:300])
    source, _ = _client_node(bench)
    x, y, z = position
    fixture = bench.cluster.node(source, f"/stale-view-test-spawn?x={x + DISTANCE}&y={y + 2}&z={z}", "POST")
    bench.record("00-fixture", fixture)
    grid = int(fixture["grid"])
    _require(bench, "the fixture rides the client's partition", _poll(lambda: (lambda place, owners: place
             if place and owners.get(grid) == place else None)(_client_node(bench), _partitions_of(bench, grid)), 180, 3),
             json.dumps({"client": _client_node(bench), "grid": _partitions_of(bench, grid)}))
    _require(bench, "the client has the fixture replicated", _poll(lambda: _client_items(
        api, grid, fixture["blocks"]["cargo"]["min"]) is not None or None, 60, 2))
    return api, fixture, grid


def _partitions_of(bench, grid: int) -> dict:
    from scenarios_blocks import partitions_of
    return partitions_of(bench, [grid])


def _cell(bench, grid: int, cell) -> dict | None:
    for node in NODES:
        row = _try(lambda: bench.cluster.node(node, f"/stale-view-test-cell?grid={grid}&x={cell[0]}&y={cell[1]}&z={cell[2]}"))
        if row and row.get("grid") == grid:
            return row
    return None


def _placed(bench, api, grid: int, spot, beside, when: str, tag: str, require: bool = False) -> None:
    """The client places a small cargo container at ``spot`` (it stands next to ``beside``) and then uses it."""
    check = (lambda label, passed, detail="": _require(bench, f"CONTROL: {label}", passed, detail)) if require else bench.check
    _try(lambda: api.character_teleport(beside[0] - REACH, beside[1], beside[2]))
    time.sleep(1.5)
    built = _try(lambda: api.character_build_block(grid, spot, subtype="LargeBlockSmallContainer"))
    bench.record(f"{tag}-build", built)
    there = _poll(lambda: (lambda row: row if row and row.get("found") else None)(_cell(bench, grid, spot)), 30, 1)
    bench.record(f"{tag}-cell", there or _cell(bench, grid, spot))
    check(f"the block the client placed {when} exists on the node", bool(there), json.dumps(built)[:300])
    if not there:
        return
    _usable(bench, api, grid, spot, beside, int(there["block"]), built, when, tag, check, "Gold")


def _usable(bench, api, grid: int, spot, beside, placed: int, built, when: str, tag: str, check, ore: str) -> None:
    """The client holds the block at ``spot`` as the node has it, and can name it, see its items and open it."""
    state = _server(bench, placed)[1]
    bench.record(f"{tag}-placed", state)
    held = _poll(lambda: (lambda s: s if s.get("clientHolds") is not False and all(row.get("clientHolds") for row in s["inventories"])
                          else None)(_server(bench, placed)[1]), 20, 1)
    check(f"placed {when}: the node has the block's replicables in the client's view", bool(held),
          json.dumps({"block": state.get("clientHolds"), "inventories": [row.get("clientHolds") for row in state["inventories"]]}))
    seen = _poll(lambda: _try(lambda: api.get_block(grid, spot)), 20, 1)
    bench.record(f"{tag}-client-block", seen)
    check(f"placed {when}: the client has the block", bool(seen))
    check(f"placed {when}: the client's block has the node's entity id", (seen or {}).get("entityId") == placed,
          json.dumps({"node": placed, "client": (seen or {}).get("entityId"), "requested": (built or {}).get("blockEntityId")}))
    renamed = _try(lambda: api._post("/v1/grids/ops", {"sets": [
        {"type": "custom_name", "gridId": grid, "min": list(spot), "value": f"placed-{tag}"}]}))
    bench.record(f"{tag}-rename", renamed)
    reached = _poll(lambda: _server(bench, placed)[1].get("name") == f"placed-{tag}" or None, 20, 1)
    check(f"placed {when}: a name the client gives the block reaches the node", bool(reached), json.dumps(renamed)[:300])
    _add_ore(bench, placed, ore, 7)
    want = _amounts(_server(bench, placed)[1]["inventories"][0]["items"])
    got = _poll(lambda: (lambda items: items if items == want else None)(_client_items(api, grid, spot)), 30, 1)
    check(f"placed {when}: ore the node puts into the block shows on the client", got is not None,
          json.dumps({"node": want, "client": _client_items(api, grid, spot)}))
    where = _xyz((seen or {}).get("worldPosition")) or (beside[0], beside[1] + 2.5 * spot[1], beside[2])
    shown = _shown(bench, api, placed, grid, spot, where, f"{tag}-open")
    check(f"placed {when}: the client can open the block", shown["opened"], json.dumps(shown)[:300])


def p3_hnd_stale(bench) -> None:
    """SE-1554: a cargo container's items and a freshly placed block on the client after a handover."""
    api, fixture, grid = _setup(bench)
    cargo = fixture["blocks"]["cargo"]
    block, cell, position = int(cargo["block"]), cargo["min"], cargo["position"]

    # ---- the control: an open inventory screen gets the items ----
    _add_ore(bench, block, "Iron", 100)
    control = _shown(bench, api, block, grid, cell, position, "10-control")
    _require(bench, "the client could open the cargo container", control["opened"], json.dumps(control)[:400])
    _require(bench, "CONTROL: an open container lists the node's items", control["client"] == control["server"],
             json.dumps(control)[:400])

    _placed(bench, api, grid, (0, 1, 0), position, "before the handover", "12", require=True)
    held = _try(lambda: bench.cluster.node(_client_node(bench)[0], "/self-test?section=inventory-held"))
    bench.record("11-self-test", held)
    if held and "inventory-held" in (held.get("ran") or []):
        bench.check("node self-test section `inventory-held` passes", bool(held.get("ok")), json.dumps(held)[:400])
    else:
        print("  [INFO] this build has no `inventory-held` self-test section")

    # ---- the inventory: changes the source has not delivered when the partition is handed over ----
    source = _client_node(bench)[0]
    bench.record("20-hold", bench.cluster.node(source, f"/stale-view-test-hold?block={block}&client={CLIENT_ID}", "POST"))
    seen = {"Iron": 100.0}
    if ARM in ("lost", "both"):
        _add_ore(bench, block, "Nickel", 50)
        lost = bench.cluster.node(source, f"/stale-view-test-lose?block={block}&client={CLIENT_ID}", "POST")
        bench.record("21-lose", lost)
        _require(bench, "the nickel's delta was sent and lost", len(lost.get("lost") or []) == 1, json.dumps(lost))
    if ARM in ("pending", "both"):
        _add_ore(bench, block, "Iron", 10)
        _add_ore(bench, block, "Silicon", 5)
    time.sleep(3)
    waiting = _server(bench, block)[1]
    bench.record("22-before-handover", {"server": waiting, "client": _client_items(api, grid, cell)})
    data = waiting["inventories"][0].get("clientData") or {}
    _require(bench, f"the source holds the undelivered change for the client ({ARM})",
             data.get("dirty") is (ARM != "lost") and data.get("unacked") == (0 if ARM == "pending" else 1), json.dumps(data)[:300])
    _require(bench, "the client still lists what it had", _client_items(api, grid, cell) == seen,
             json.dumps(_client_items(api, grid, cell)))
    for index in range(1, HANDOVERS + 1):
        target = _handover(bench, api, grid, index)
    carried = _lines(bench, target, rf"client_view_inventory_held client={CLIENT_ID} ")
    bench.record("23-carried", carried)
    print(f"  [INFO] {target}: {carried[-1].split('client_view_inventory_held')[-1].strip() if carried else 'no client_view_inventory_held line'}")
    want = _amounts(_server(bench, block)[1]["inventories"][0]["items"])
    got = _poll(lambda: (lambda items: items if items == want else None)(_client_items(api, grid, cell)), 30, 1)
    bench.record("30-after-handover", {"server": _server(bench, block), "client": _client_items(api, grid, cell)})
    bench.check("after the handover the client lists the container as the node has it", got is not None,
                json.dumps({"node": want, "client": _client_items(api, grid, cell)}))
    _add_ore(bench, block, "Gold", 1)
    want = _amounts(_server(bench, block)[1]["inventories"][0]["items"])
    got = _poll(lambda: (lambda items: items if items == want else None)(_client_items(api, grid, cell)), 30, 1)
    bench.record("31-after-change", {"server": _server(bench, block), "client": _client_items(api, grid, cell)})
    bench.check("a change on the target reaches the client and the lists still match", got is not None,
                json.dumps({"node": want, "mass": _server(bench, block)[1]["inventories"][0]["mass"],
                            "client": _client_items(api, grid, cell)}))
    after = _shown(bench, api, block, grid, cell, position, "32-open-after")
    bench.check("the client could open the cargo container after the handover", after["opened"])
    bench.check("the open container lists every item the node has", after["client"] == after["server"],
                json.dumps({k: after[k] for k in ("server", "client")}))

    # ---- the placed block ----
    early = _cell(bench, grid, (0, 1, 0))
    if bench.check("the block placed before the handover is on the target", bool(early and early.get("found"))):
        _usable(bench, api, grid, (0, 1, 0), position, int(early["block"]), None, "before the handover, used after it", "39",
                bench.check, "Silver")
    _placed(bench, api, grid, (0, -1, 0), position, "after the handover", "40")

    bench.exercised(grid=grid, handovers=HANDOVERS, arm=ARM, node=(_client_node(bench) or ("?",))[0])


p3_hnd_stale.administrators = [CLIENT_ID]


# -- the ore detector -----------------------------------------------------------------------------

ORE_FACTORS = tuple(float(f) for f in os.environ.get("SE1554_ORE_FACTORS", "0.7,0.6,0.5,0.85").split(","))


def _signals(api) -> dict:
    return _try(api.get_hud_signals, {}) or {}


def _deposits(api, detector: int) -> list[dict]:
    return [row for row in _signals(api).get("oreDeposits") or [] if row.get("detectorId") == detector]


def _sit(bench, api, position, label: str) -> bool:
    x, y, z = position
    for offset in ((0, 0, REACH), (0, 0, -REACH), (0, REACH, 0), (0, -REACH, 0)):
        _try(lambda: api.character_teleport(x + offset[0], y + offset[1], z + offset[2]))
        time.sleep(1.5)
        for _ in range(3):
            _try(lambda: api.character_look_at(x, y, z, tolerance=1))
            used = _try(api.character_use)
            if _poll(lambda: (_try(api.get_character) or {}).get("state") == "sitting" or None, 6, 0.5):
                bench.record(f"sit-{label}", {"offset": offset, "used": used})
                return True
    bench.record(f"sit-{label}", {"failed": True, "character": _try(api.get_character)})
    return False


def p3_hnd_stale_ore(bench) -> None:
    """SE-1554: an ore detector's deposits on the client, seated on its grid, across handovers."""
    import math
    from scenarios_ext import _procedural_template
    bench.cluster.config.world_template = _procedural_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID, timeout=300)
    bench.ensure_gameplay_focus(CLIENT_ID)
    for flag in ("creativeTools", "useTerminals", "invulnerable"):
        bench.record(f"admin-{flag}", _try(lambda: api.set_admin_flag(flag, True)))
    _require(bench, "this Remote build reports ore deposits", "oreDeposits" in _signals(api), json.dumps(_signals(api))[:200])
    source = _poll(lambda: (_client_node(bench) or (None,))[0], 60)
    position = _xyz(api.get_character().get("position"))
    _require(bench, "the client is in a partition and reports its position", source and position)
    query = "&".join(f"{axis}={value:.3f}" for axis, value in zip("xyz", position))
    rocks = _poll(lambda: [row for row in (_try(lambda: bench.cluster.node(source, f"/voxel-test-asteroids?{query}&radius=8000"), {})
                                           or {}).get("asteroids") or [] if row["halfExtent"] > 80], 90, 3)
    _require(bench, "a procedural asteroid within 8 km of the client", rocks)
    rock = rocks[0]
    center = rock["center"]
    direction = [p - c for p, c in zip(position, center)]
    length = math.sqrt(sum(d * d for d in direction)) or 1.0
    for name in NODES:
        bench.record(f"00-track-{name}", bench.cluster.node(
            name, "/voxel-test-track-point?" + "&".join(f"{a}={v:.3f}" for a, v in zip("xyz", center)) + "&radius=1000", "POST"))
    fixture = detector = None
    for factor in ORE_FACTORS:
        spot = [c + d / length * rock["halfExtent"] * factor for c, d in zip(center, direction)]
        bench.record(f"01-teleport-{factor}", bench.cluster.node(
            source, f"/conceal-test-teleport?client={CLIENT_ID}&" + "&".join(f"{a}={v:.3f}" for a, v in zip("xyz", spot)), "POST"))
        _poll(lambda: (lambda at: at if at and math.dist(at, spot) < 50 else None)(_xyz((_try(api.get_character) or {}).get("position"))), 60)
        time.sleep(8)
        source = (_client_node(bench) or (source,))[0]
        candidate = bench.cluster.node(source, f"/stale-view-test-spawn?x={spot[0] + DISTANCE}&y={spot[1] + 2}&z={spot[2]}", "POST")
        bench.record(f"02-fixture-{factor}", candidate)
        grid = int(candidate["grid"])
        if not _poll(lambda: _client_items(api, grid, candidate["blocks"]["cargo"]["min"]) is not None or None, 60, 2):
            continue
        if not _sit(bench, api, candidate["blocks"]["seat"]["position"], f"{factor}"):
            continue
        found = _poll(lambda: _deposits(api, int(candidate["blocks"]["detector"]["block"])) or None, 40, 2)
        bench.record(f"03-deposits-{factor}", {"signals": _signals(api), "found": len(found or [])})
        if found:
            fixture, detector = candidate, int(candidate["blocks"]["detector"]["block"])
            break
        _try(api.character_use)  # leave the seat for the next spot
        time.sleep(2)
    _require(bench, "CONTROL: seated on the fixture, its ore detector marks deposits before any handover", fixture,
             json.dumps(_signals(api))[:400])
    grid = int(fixture["grid"])
    before = _deposits(api, detector)
    print(f"  [INFO] {len(before)} ore deposits marked before the handover")
    _require(bench, "the fixture rides the client's partition", _poll(lambda: (lambda place, owners: place
             if place and owners.get(grid) == place else None)(_client_node(bench), _partitions_of(bench, grid)), 180, 3),
             json.dumps({"client": _client_node(bench), "grid": _partitions_of(bench, grid)}))
    for index in range(1, max(HANDOVERS, 2) + 1):
        _handover(bench, api, grid, index)
        time.sleep(10)
        signals = _signals(api)
        marked = _deposits(api, detector)
        mine = next((row for row in signals.get("oreDetectors") or [] if row.get("entityId") == detector), None)
        bench.record(f"10-after-handover-{index}", {"deposits": len(marked), "detector": mine,
                                                    "character": _try(api.get_character)})
        bench.check(f"handover {index}: the client still has the detector working and accessible",
                    bool(mine and mine.get("working") and mine.get("access")), json.dumps(mine))
        bench.check(f"handover {index}: the client is still seated", (_try(api.get_character) or {}).get("state") == "sitting")
        bench.check(f"handover {index}: the ore detector still marks its deposits", len(marked) >= len(before),
                    f"{len(marked)} of {len(before)}")
        # A fresh scan: off clears the deposits, on must find them again.
        off = _try(lambda: api._post("/v1/grids/ops", {"sets": [
            {"type": "enabled", "gridId": grid, "min": fixture["blocks"]["detector"]["min"], "value": False}]}))
        cleared = _poll(lambda: not _deposits(api, detector) or None, 30, 1)
        _try(lambda: api._post("/v1/grids/ops", {"sets": [
            {"type": "enabled", "gridId": grid, "min": fixture["blocks"]["detector"]["min"], "value": True}]}))
        again = _poll(lambda: (lambda rows: rows if len(rows) >= len(before) else None)(_deposits(api, detector)), 60, 2)
        bench.record(f"11-rescan-{index}", {"off": off, "cleared": bool(cleared), "deposits": len(_deposits(api, detector))})
        bench.check(f"handover {index}: switched off, the detector's deposits go", bool(cleared))
        bench.check(f"handover {index}: switched on again, the detector finds its deposits anew", bool(again),
                    f"{len(_deposits(api, detector))} of {len(before)}")
    bench.exercised(grid=grid, asteroid=rock["entityId"], deposits=len(before), handovers=max(HANDOVERS, 2))


p3_hnd_stale_ore.administrators = [CLIENT_ID]

SCENARIOS_STALE_VIEW = {
    "P3-HND-STALE": (p3_hnd_stale, "a cargo container's items and a placed block on the client after a handover (SE-1554)", "3"),
    "P3-HND-STALE-ORE": (p3_hnd_stale_ore, "an ore detector's deposits on the seated client across handovers (SE-1554)", "3"),
}
