"""SE1-0122: the respawn screen previews a spawn point that lives on another node.

* ``P2-WA-06d`` - the vanilla respawn screen streams the selected spawn point's grid, and enables its Respawn button
  only once the grid is on the client. A client dead on one node selects the transport's survival kit while the
  transport sits on the other node, first with a second client aboard (the Gateway refuses to pull a partition
  holding a client, so moving the grid cannot work), then while the transport's partition is offline. Each time the
  client must get the grid and an enabled Respawn button without reconnecting, and respawn at the kit. Last, a
  preview switch queued for a client whose view a cross-node respawn already detached must be refused, not crash
  the node (#1287).
* ``P2-WA-06e`` - the kit's node refuses a preview switch outright (SE-1124): the client must come back to its own
  node with its view, stay connected and unpaused on the medical screen, and preview and respawn at the kit afterwards.
* ``P2-WA-06f`` - the kit's partition is split on its node between the Gateway's ownership check and the target's (a
  trader shuttle leaving Alpha on octahedron-test, SE-1124): the preview must still arrive.
* ``P2-WA-06g`` - SE-1189, two legs on the client's own node. First the switch fails before the Gateway moves the
  client (the bench refuses it there): the node still has the player, its body still its character, and must take the
  client back unpaused. Then a fast flight splits the kit's partition after the click and before the Gateway's switch,
  which retires the partition the request names (Viktor at Alpha on octahedron-test, 2026-11-06 19:07Z): the preview
  must follow the kit to its new partition.

The Respawn button is checked through ``respawnButtonEnabled`` before it is clicked: the Remote API's click raises
the button's event even when the button is disabled, which is how the older respawn scenarios passed while players
could not respawn at a remote spawn point.
"""

from __future__ import annotations

import json
import re
import time

from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _handover_url, _join, _lines, _nodes, _owners, _partitions
from scenarios_offline import _put_config
from scenarios_wa import CLIENT2_ID, start_second_client
from scenarios_wa import _session as _session_of
from scenarios_wa_ops import _medical_click, _point, _points, _poll, _require, _try

#: Far enough from the transport to leave its partition (sync distance 3 km) and its offline quiet radius (8 km).
AWAY = 15000
PREVIEW_TIMEOUT = 120


def _player(bench, node_id: str, client_id: int) -> dict:
    player = _try(bench.cluster.node, node_id, f"/respawn-test-player?userId={client_id}")
    return player if isinstance(player, dict) else {}


def _node_of(bench, client_id: int) -> str | None:
    backend = (_session_of(bench, client_id) or {}).get("backend")
    return next((node_id for node_id, node in _nodes(bench).items() if node.backend == backend), None)


def _move_away(bench, client_id: int, anchor: int, x: float) -> None:
    node_id = _node_of(bench, client_id)
    character = _player(bench, node_id, client_id).get("character") or 0
    _require(bench, f"client {client_id} has a character on {node_id} to move", character > 0)
    bench.cluster.node(node_id, f"/test-position?entity={character}&relativeTo={anchor}&x={x}", "POST")


def _kill(bench, client_id: int) -> str:
    node_id = _node_of(bench, client_id)
    killed = bench.cluster.node(node_id, f"/respawn-test-kill?userId={client_id}", "POST")
    _require(bench, f"{node_id} killed client {client_id}", killed.get("killed") is True, json.dumps(killed))
    dead = _poll(lambda: _player(bench, node_id, client_id).get("dead") is True, HANDOVER_TIMEOUT)
    _require(bench, f"{node_id} observed the death of client {client_id}", bool(dead))
    return node_id


def _preview_and_respawn(bench, api, leg: str, name: str, grid: int, session_before: dict | None = None) -> dict:
    """Select the spawn point on the respawn screen (unless SESSION_BEFORE says it was selected already); the preview
    must arrive (the grid on the client, the button enabled) with the player still dead and the session unbroken; then
    respawn there."""
    if session_before is None:
        session_before = _session_of(bench, CLIENT_ID) or {}
        screen = _medical_click(bench, api, name)
    else:
        screen = _medical_screen(api)
    state = {"streak": 0}

    def previewed():
        # Selecting a row enables the button for a frame; the screen disables it every frame until the spawn
        # block exists on the client, so only a button that stays enabled counts.
        state["respawn"] = _try(api.get_respawn_state)
        grids = _try(api.list_grids)
        state["grid"] = isinstance(grids, list) and any(g.get("entityId") == grid for g in grids)
        state["node"] = _node_of(bench, CLIENT_ID)
        enabled = state["respawn"].get("respawnButtonEnabled") is True and state["grid"]
        state["streak"] = state["streak"] + 1 if enabled else 0
        return state["streak"] >= 3

    ok = _poll(previewed, PREVIEW_TIMEOUT)
    bench.record(f"{leg}-preview", {**state, "nodeLog": _lines(bench, state.get("node") or "node-1", r"spawn preview")[-5:]})
    bench.screenshot(CLIENT_ID, f"{leg}-preview")
    _require(bench, f"{leg}: the spawn point's grid reached the client and the Respawn button is enabled", bool(ok),
             json.dumps(state)[:400])
    node_id = state["node"]
    bench.check(f"{leg}: the client is still dead while it previews",
                _player(bench, node_id, CLIENT_ID).get("dead") is not False)

    api.control_click(text="Respawn", screen=screen)
    after = {}

    def respawned():
        after["node"] = _node_of(bench, CLIENT_ID)
        after["player"] = _player(bench, after["node"], CLIENT_ID) if after["node"] else {}
        after["session"] = _session_of(bench, CLIENT_ID) or {}
        return after["player"].get("dead") is False and (after["player"].get("character") or 0) > 0 \
            and bench.living_character(CLIENT_ID) is not None
    ok = _poll(respawned, HANDOVER_TIMEOUT)
    bench.record(f"{leg}-respawned", after)
    _require(bench, f"{leg}: the client respawned", bool(ok), json.dumps(after, default=str)[:400])
    bench.check(f"{leg}: the client never reconnected",
                (after["session"].get("connectedAt") or 0) == session_before.get("connectedAt"),
                json.dumps([session_before.get("connectedAt"), after["session"].get("connectedAt")]))
    character = bench.living_character(CLIENT_ID) or {}
    bench.record(f"{leg}-character", character)
    return after


def p2_wa_06d(bench) -> None:
    """The spawn point preview across nodes: aboard-partition leg and offline leg."""
    api = _join(bench)
    bench.cluster.post("/automatic/pause")

    def converged():
        points = _points(bench, "wa-1")
        mine = [p for p in points.get("points") or [] if p.get("Origin") == "node-1" and p.get("Working")]
        return min(mine, key=lambda p: p["RespawnEntityId"]) if mine else None

    point = _poll(converged, HANDOVER_TIMEOUT)
    _require(bench, "a node-1 respawn point converged at the World Authority", point is not None)
    entity, grid, name = point["RespawnEntityId"], point["GridEntityId"], point["Name"]

    # Leg 1: a second client stands at the kit, and the kit's partition goes to node-2 with it aboard.
    start_second_client(bench, "node-1", point["PartitionId"], spawn=True)
    _move_away(bench, CLIENT_ID, grid, AWAY)

    def kit_partition():
        rows = _try(_partitions, bench, "node-1")
        rows = rows if isinstance(rows, list) else []
        row = next((p for p in rows if any(m.get("id") == grid for m in p.get("members") or [])), None)
        return row if row and CLIENT2_ID in (row.get("clients") or []) and CLIENT_ID not in row["clients"] else None

    row = _poll(kit_partition, HANDOVER_TIMEOUT)
    _require(bench, "the kit's partition holds client 2 and not client 1", row is not None)
    bench.record("aboard-handover.txt", bench.cluster.post(_handover_url(bench, row["id"], client=CLIENT2_ID), timeout=300))
    moved = _poll(lambda: _point(_points(bench, "node-1"), entity, "node-2", working=True), HANDOVER_TIMEOUT)
    _require(bench, "the respawn registry followed the kit to node-2", moved is not None)
    _require(bench, "client 2 went to node-2 with the kit", _node_of(bench, CLIENT2_ID) == "node-2")

    source = _kill(bench, CLIENT_ID)
    _require(bench, "client 1 died on node-1", source == "node-1", str(source))
    first = _preview_and_respawn(bench, api, "aboard", name, grid)
    bench.check("aboard: the client respawned on node-2, where the kit is", first["node"] == "node-2", str(first["node"]))
    bench.check("aboard: node-1 moved the client for the preview",
                bool(_lines(bench, "node-1", rf"spawn preview switch operation=\S+ user={CLIENT_ID} target=node-2")))
    bench.check("aboard: the kit's partition stayed on node-2", _point(_points(bench, "node-2"), entity, "node-2") is not None)

    # Leg 2: both clients leave, the kit's partition goes offline, client 1 dies far away and picks the kit.
    _move_away(bench, CLIENT_ID, grid, AWAY)
    _move_away(bench, CLIENT2_ID, grid, -AWAY)
    # The fixture parks a stray body next to the transport, and a non-grid member keeps a partition online.
    def kit_members() -> tuple[int, list[dict]]:
        rows = _try(_partitions, bench, "node-2")
        rows = rows if isinstance(rows, list) else []
        row = next((p for p in rows if any(m.get("id") == grid for m in p.get("members") or [])), {})
        return row.get("id", 0), row.get("members") or []

    _, members = kit_members()
    for stray in [m for m in members if m.get("type") != "MyCubeGrid"]:
        bench.cluster.node("node-2", f"/test-close-entity?entity={stray['id']}", "POST")
    alone = _poll(lambda: (lambda ms: ms if ms and all(m.get("type") == "MyCubeGrid" for m in ms) else None)(kit_members()[1]),
                  HANDOVER_TIMEOUT)
    bench.record("kit-members", alone or kit_members()[1])
    _require(bench, "the kit's partition holds grids only", alone is not None)
    # The Gateway admits an offline only on fresh reports of the automatic loop, paused for leg 1.
    bench.cluster.post("/automatic/resume")
    for node_id in ("node-1", "node-2"):
        bench.record(f"offline-on-{node_id}", _put_config(bench, node_id, {
            "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))

    def offline():
        # Leaving split the partition, which renumbers it: match the point, not the old id.
        return next((p for p in _points(bench, "wa-1").get("points") or []
                     if p.get("RespawnEntityId") == entity and str(p.get("Origin")).startswith("offline:")), None)

    went = _poll(offline, 180)
    bench.record("offline-state", {"points": _points(bench, "wa-1"), "owners": _try(_owners, bench),
                                   "node2": _try(bench.cluster.node, "node-2", "/offline")})
    _require(bench, "the kit's partition went offline", went is not None)
    # Nothing may wake it but the preview: no more sweeps either way.
    for node_id in ("node-1", "node-2"):
        _put_config(bench, node_id, {"offline.enabled": "false"})
    time.sleep(5)
    _require(bench, "the kit's partition is still offline before the preview", offline() is not None)

    _kill(bench, CLIENT_ID)
    second = _preview_and_respawn(bench, api, "offline", name, grid)
    bench.check("offline: a node asked to load the kit's partition for the preview",
                any(_lines(bench, n, rf"spawn preview grid={grid} client={CLIENT_ID} loads offline partition") for n in ("node-1", "node-2")))
    bench.check("offline: the client respawned where the kit was loaded",
                _point(_points(bench, second["node"]), entity, second["node"]) is not None, str(second["node"]))
    bench.screenshot(CLIENT_ID, "99-after")

    # Leg 3 (#1287): a cross-node respawn detached the dead client's view while a preview switch for the same client
    # was queued. The preview used to throw on the game thread, which ended the node with every client on it. The kit
    # must sit on another node than the client, as on the live cluster: a local kit is refused as grid-local first.
    bench.cluster.post("/automatic/pause")
    _move_away(bench, CLIENT_ID, grid, AWAY)

    def own_partition():
        rows = _try(_partitions, bench, "node-2")
        rows = rows if isinstance(rows, list) else []
        return next((p for p in rows if CLIENT_ID in (p.get("clients") or [])
                     and not any(m.get("id") == grid for m in p.get("members") or [])), None)

    row = _poll(own_partition, HANDOVER_TIMEOUT)
    _require(bench, "client 1 has a partition of its own on node-2, away from the kit", row is not None)
    bench.record("race-handover.txt", bench.cluster.post(
        _handover_url(bench, row["id"], source="node-2", target="node-1", client=CLIENT_ID), timeout=300))
    _require(bench, "client 1 went to node-1", _poll(lambda: _node_of(bench, CLIENT_ID) == "node-1", HANDOVER_TIMEOUT) is not None)
    _require(bench, "the kit stayed on node-2", _point(_points(bench, "node-1"), entity, "node-2", working=True) is not None)
    race_node = _kill(bench, CLIENT_ID)
    bench.record("race", bench.cluster.node(race_node, f"/respawn-test-preview-race?userId={CLIENT_ID}&respawnEntityId={entity}", "POST"))
    skipped = _poll(lambda: _lines(bench, race_node, rf"spawn preview switch operation=\S+ user={CLIENT_ID} skipped=client-detached"), 60)
    bench.check("race: the queued preview refused the detached client", bool(skipped))
    time.sleep(5)
    alive = _try(bench.cluster.node, race_node, f"/respawn-test-player?userId={CLIENT_ID}")
    bench.record("race-alive", alive)
    bench.check(f"race: {race_node}'s game thread still answers", isinstance(alive, dict) and "error" not in alive, json.dumps(alive)[:300])
    bench.check(f"race: no unhandled exception on {race_node}", not _lines(bench, race_node, r"[Uu]nhandled exception"))
    bench.exercised(kit=entity, grid=grid, aboard=first["node"], offline=second["node"], race=race_node)


def _own_partition_on(bench, grid: int, target: str) -> None:
    """Client 1 far from the kit, in a partition of its own on ``target``."""
    _move_away(bench, CLIENT_ID, grid, AWAY)
    source = _node_of(bench, CLIENT_ID)
    if source == target:
        return

    def own_partition():
        rows = _try(_partitions, bench, source)
        rows = rows if isinstance(rows, list) else []
        return next((p for p in rows if CLIENT_ID in (p.get("clients") or [])
                     and not any(m.get("id") == grid for m in p.get("members") or [])), None)

    row = _poll(own_partition, HANDOVER_TIMEOUT)
    _require(bench, f"client 1 has a partition of its own on {source}, away from the kit", row is not None)
    bench.record(f"to-{target}-handover.txt", bench.cluster.post(
        _handover_url(bench, row["id"], source=source, target=target, client=CLIENT_ID), timeout=300))
    _require(bench, f"client 1 went to {target}",
             _poll(lambda: _node_of(bench, CLIENT_ID) == target, HANDOVER_TIMEOUT) is not None)


def _watch_failed_switch(bench, api, leg: str, node_id: str, seconds: float = 25) -> dict:
    """What the client goes through after its preview switch failed: reconnects, its paused state (the "Paused" a
    stranded Steam client showed), the medical screen, and where its session ends up."""
    before = _session_of(bench, CLIENT_ID) or {}
    failed = _poll(lambda: _lines(bench, node_id, rf"spawn preview switch operation=\S+ failed"), 60)
    samples = []
    deadline = time.time() + seconds
    while time.time() < deadline:
        state = _try(api.get_state) or {}
        screens = _try(api.list_screens)
        session = _session_of(bench, CLIENT_ID) or {}
        samples.append({"t": round(time.time(), 1), "active": state.get("active"), "ready": state.get("ready"),
                        "paused": state.get("paused"), "frame": state.get("frameCounter"),
                        "medical": isinstance(screens, list) and any(x.get("type") == "MyGuiScreenMedicals" for x in screens),
                        "connectedAt": session.get("connectedAt"), "node": _node_of(bench, CLIENT_ID)})
        time.sleep(1)
    summary = {
        "switchFailed": bool(failed),
        "reconnected": any(x["connectedAt"] != before.get("connectedAt") for x in samples),
        "pausedSamples": sum(1 for x in samples if x["paused"]),
        "medicalAtEnd": samples[-1]["medical"] if samples else None,
        "nodeAtEnd": samples[-1]["node"] if samples else None,
        "restored": _lines(bench, node_id, r"spawn switch restored")[-3:],
        "rollbackDisconnect": _lines(bench, node_id, rf"spawn preview switch operation=\S+ failed")[-3:],
        "samples": samples,
    }
    bench.record(f"{leg}-after-failure", summary)
    return summary


def _medical_screen(api) -> int | None:
    screens = _try(api.list_screens)
    return next((s["index"] for s in screens if s.get("type") == "MyGuiScreenMedicals"), None) \
        if isinstance(screens, list) else None


def _reselect(bench, api) -> None:
    """Taking the client in again creates its player on the node, and the client answers a new player without a
    character with a medical screen of its own: keep the newest one, whose selection is empty, so a click on the
    kit's row asks for a preview again."""
    def screens():
        listed = _try(api.list_screens)
        found = [x["index"] for x in listed if x.get("type") == "MyGuiScreenMedicals"] if isinstance(listed, list) else []
        return found or None

    found = _poll(screens, 30) or []
    bench.record("reselect-screens", _try(api.list_screens))
    for index in sorted(found)[:-1][::-1]:
        _try(api.close_screen, index)
    # A closing screen stays listed until its close animation ends, and _medical_click takes the first one listed.
    _poll(lambda: (lambda left: left is not None and len(left) <= 1)(screens()), 30)


def _kit_on_node1(bench):
    api = _join(bench)
    bench.cluster.post("/automatic/pause")

    def converged():
        points = _points(bench, "wa-1")
        mine = [p for p in points.get("points") or [] if p.get("Origin") == "node-1" and p.get("Working")]
        return min(mine, key=lambda p: p["RespawnEntityId"]) if mine else None

    point = _poll(converged, HANDOVER_TIMEOUT)
    _require(bench, "a node-1 respawn point converged at the World Authority", point is not None)
    return api, point["RespawnEntityId"], point["GridEntityId"], point["Name"]


def p2_wa_06e(bench) -> None:
    """The kit's node refuses a spawn preview switch: the client stays, unpaused, and previews again (SE-1124)."""
    api, entity, grid, name = _kit_on_node1(bench)
    _own_partition_on(bench, grid, "node-2")
    _kill(bench, CLIENT_ID)
    bench.record("refuse-armed", bench.cluster.node("node-1", "/respawn-execute-fault?mode=refuse&count=1", "POST"))
    _medical_click(bench, api, name)
    refused = _watch_failed_switch(bench, api, "refuse", "node-2")
    bench.record("refuse-fault", bench.cluster.node("node-1", "/respawn-execute-fault"))
    bench.screenshot(CLIENT_ID, "refuse-after-failure")
    _require(bench, "refuse: the preview switch failed", refused["switchFailed"])
    bench.check("refuse: the client never reconnected", not refused["reconnected"])
    bench.check("refuse: the client never paused", refused["pausedSamples"] == 0, str(refused["pausedSamples"]))
    bench.check("refuse: the client is still on the medical screen", refused["medicalAtEnd"] is True)
    # Its new medical screen asks for the kit again at once, so the next switch may already have taken it to node-1.
    bench.check("refuse: the client came back to node-2", any(x["node"] == "node-2" for x in refused["samples"]),
                str([x["node"] for x in refused["samples"]]))
    bench.check("refuse: node-2 took the client in again", bool(refused["restored"]))
    _reselect(bench, api)
    first = _preview_and_respawn(bench, api, "refuse-retry", name, grid)
    bench.check("refuse-retry: the client respawned on node-1, where the kit is", first["node"] == "node-1", str(first["node"]))
    bench.exercised(kit=entity, grid=grid, refused=refused["switchFailed"], reconnected=refused["reconnected"])


def p2_wa_06f(bench) -> None:
    """The kit's partition is split on its node between the Gateway's ownership check and the target's (SE-1124)."""
    api, entity, grid, name = _kit_on_node1(bench)
    _own_partition_on(bench, grid, "node-2")

    def movers():
        rows = _try(_partitions, bench, "node-1")
        rows = rows if isinstance(rows, list) else []
        row = next((p for p in rows if any(m.get("id") == grid for m in p.get("members") or [])), {})
        others = [m["id"] for m in row.get("members") or [] if m.get("id") != grid]
        return (row, others) if others else None

    found = _poll(movers, HANDOVER_TIMEOUT)
    bench.record("split-partition", found)
    _require(bench, "the kit's partition has other members to split off", found is not None)
    _, moved = found
    _kill(bench, CLIENT_ID)
    bench.record("split-armed", bench.cluster.node(
        "node-1", f"/respawn-execute-fault?mode=split&count=1&movers={','.join(str(x) for x in moved)}", "POST"))
    second = _preview_and_respawn(bench, api, "split", name, grid)
    fault = bench.cluster.node("node-1", "/respawn-execute-fault")
    bench.record("split-fault", fault)
    faulted = (fault.get("faulted") or [{}])[0]
    bench.check("split: the kit's partition was renumbered on node-1 during the switch",
                faulted.get("partition") not in (0, faulted.get("requested")),
                json.dumps(faulted))
    bench.check("split: the client respawned on node-1, where the kit is", second["node"] == "node-1", str(second["node"]))
    bench.check("split: node-1 executed at the kit's new partition",
                bool(_lines(bench, "node-1", rf"respawn execute operation=\S+ grid={grid} partition-moved")))
    bench.exercised(kit=entity, grid=grid, split=faulted)


def p2_wa_06g(bench) -> None:
    """A preview switch that fails before the Gateway moves the client keeps it, and one whose kit partition a fast
    split retired after the click follows the kit (SE-1189)."""
    api, entity, grid, name = _kit_on_node1(bench)
    _own_partition_on(bench, grid, "node-2")

    # Leg 1: the switch fails before the Gateway moved the backend. node-2 still has the player and its dead body; the
    # restore used to refuse those bindings, give up and disconnect the client, which left a Steam client "Paused".
    _kill(bench, CLIENT_ID)
    bench.record("refuse-armed", bench.cluster.node("node-2", "/respawn-switch-fault?mode=refuse&count=1", "POST"))
    _medical_click(bench, api, name)
    refused = _watch_failed_switch(bench, api, "early-refuse", "node-2")
    abandoned = _lines(bench, "node-2", r"spawn switch restore abandoned")
    bench.record("early-refuse-abandoned", abandoned)
    bench.screenshot(CLIENT_ID, "early-refuse-after-failure")
    _require(bench, "early refuse: the preview switch failed", refused["switchFailed"])
    bench.check("early refuse: node-2 took the client back", bool(refused["restored"]) and not abandoned,
                json.dumps(abandoned)[:400])
    bench.check("early refuse: the client never reconnected", not refused["reconnected"])
    bench.check("early refuse: the client never paused", refused["pausedSamples"] == 0, str(refused["pausedSamples"]))
    bench.check("early refuse: the client is still on the medical screen", refused["medicalAtEnd"] is True)
    _reselect(bench, api)
    first = _preview_and_respawn(bench, api, "early-refuse-retry", name, grid)
    bench.check("early refuse retry: the client respawned on node-1, where the kit is", first["node"] == "node-1",
                str(first["node"]))

    # Leg 2: a fast flight splits the kit's partition between the click and the Gateway's switch. A fast split gives
    # every part a new id, so the request names a retired partition, whose row has no owner.
    _own_partition_on(bench, grid, "node-2")

    def movers():
        rows = _try(_partitions, bench, "node-1")
        rows = rows if isinstance(rows, list) else []
        row = next((p for p in rows if any(m.get("id") == grid for m in p.get("members") or [])), {})
        others = [m["id"] for m in row.get("members") or [] if m.get("id") != grid]
        return (row["id"], others[0]) if others else None

    found = _poll(movers, HANDOVER_TIMEOUT)
    bench.record("split-partition", found)
    _require(bench, "the kit's partition has another member to fly away", found is not None)
    _, mover = found
    _kill(bench, CLIENT_ID)
    session_before = _session_of(bench, CLIENT_ID) or {}
    failed_before = len(_lines(bench, "node-2", r"spawn preview switch operation=\S+ failed"))
    splits_before = len(_lines(bench, "node-1", r"topology Split committed"))
    bench.record("hold-armed", bench.cluster.node("node-2", "/respawn-switch-fault?mode=hold&count=1&seconds=60", "POST"))
    _medical_click(bench, api, name)
    held = _poll(lambda: (lambda s: s if isinstance(s, dict) and s.get("faulted") else None)(
        _try(bench.cluster.node, "node-2", "/respawn-switch-fault")), 60)
    _require(bench, "node-2 holds the preview switch", held is not None, json.dumps(held))
    partition = held["faulted"][0]["partition"]
    bench.cluster.node("node-1", f"/fast-test-drive?entity={mover}&vx=3000&vy=0&vz=0&seconds=20&mode=teleport", "POST")
    def retired(line: str) -> bool:
        # A distance split keeps the larger group's id; only a split with the partition among its inputs and not among
        # its outputs retires it, as the trader's fast split retired Alpha's.
        m = re.search(r"inputs=(\S+) outputs=([\d,]+)", line)
        return bool(m) and str(partition) in m.group(1).split(",") and str(partition) not in m.group(2).split(",")

    split = _poll(lambda: [line for line in _lines(bench, "node-1", r"topology Split committed")[splits_before:]
                           if retired(line)], 60)
    bench.record("split", {"held": held, "split": split})
    _require(bench, f"a fast split retired the kit's partition {partition} while the switch was held", bool(split))
    bench.cluster.node("node-2", "/respawn-switch-fault-release", "POST")
    second = _preview_and_respawn(bench, api, "retired", name, grid, session_before=session_before)
    bench.check("retired: the client respawned on node-1, where the kit is", second["node"] == "node-1", str(second["node"]))
    failed = _lines(bench, "node-2", r"spawn preview switch operation=\S+ failed")[failed_before:]
    bench.check("retired: the Gateway did not fail the switch", not failed, json.dumps(failed)[:400])
    bench.exercised(kit=entity, grid=grid, refused=refused["switchFailed"], retired=partition, mover=mover)


SCENARIOS_SPAWN_PREVIEW = {
    "P2-WA-06d": (p2_wa_06d, "the respawn screen previews a spawn point on another node or offline (SE1-0122)", "3"),
    "P2-WA-06e": (p2_wa_06e, "a refused spawn preview switch keeps the client on its node, unpaused (SE-1124)", "3"),
    "P2-WA-06f": (p2_wa_06f, "a split of the kit's partition during a spawn preview switch does not fail it (SE-1124)", "3"),
    "P2-WA-06g": (p2_wa_06g, "a preview switch failing before the Gateway moves the client keeps it, and one whose kit "
                  "partition was split away follows the kit (SE-1189)", "3"),
}
