"""#1376: a client-view adopt serializes a frozen grid's property sync group before its BEFORE_NEXT_FRAME init ran.

``P3-HND-06`` - one client, standing next to a fresh emotion controller grid in its own partition, handed to the
other node. The import freezes the grid right after MyEntities.Add (NeedsUpdate = NONE), so the emotion controller
component's next-frame init has not run: its ``m_selectedLCDsIds`` (Sync<List<long>>) and ``m_selectedEmotionSubtype``
(Sync<string>) are still null. The client-view adopt runs before the commit thaws the grid and creates the client's
data for its replicables, all properties dirty, so the next SendUpdate serializes the nulls:
MySerializeException(NullNotAllowed) out of Sync`2.Serialize, and the server stops ("game loop exception").
That is how octahedron-1191c local-node-3 stopped at 2026-10-21 18:43:23Z (partition 925, 11 client adopts).

Two legs. THE HANDOVER: the handover commits, the target logs no game loop exception and still answers, the client
is on the target with the grid in reach. On the bench the adopt-to-thaw window is only two frames, too short for the
group's update layer to come round, so the handover alone does not hit the throw: what it withholds is recorded only.
THE INJECTION (test plugin ``/property-sync-test-null``): the target puts the component into the frozen-import state
(both Syncs null, every property dirty for the client). A build without the fix stops there ("game loop exception"
with MySerializeException out of Sync`2.Serialize, the 1191c stack); a fixed one logs ``property_sync_withheld`` for
both Syncs (the positive control), and once the values are back nothing more is withheld and no group was dropped.

Knob: P1376_DISTANCE (15 m: the grid's offset from the client).
"""

from __future__ import annotations

import json
import os
import re
import time

from scenarios_blocks import _identity, partitions_of, spawn
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _lines, _partitions
from scenarios_redeliver import _poll, _require, _try, _xyz

NODES = ("node-1", "node-2")
DISTANCE = float(os.environ.get("P1376_DISTANCE", "15"))
STOPPED = r"game loop exception, the server is stopping"


def _client_node(bench) -> tuple[str, int] | None:
    for node in NODES:
        partition = _client_partition(_try(lambda: _partitions(bench, node), []) or [])
        if partition is not None:
            return node, partition
    return None


def p3_hnd_06(bench) -> None:
    """#1376: hand a client's partition holding a fresh emotion controller grid to the other node."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition move is the forced handover
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)

    owner = _identity(bench, "p1376-owner")
    position = _xyz(api.get_character().get("position"))
    _require(bench, "the client reports its position", position, str(api.get_character())[:300])
    x, y, z = position
    origin = (x + DISTANCE, y + 2, z)
    answer = bench.cluster._request("POST", "/cluster/block-test-spawn",
                                    next(spec.control for spec in bench.cluster.config.nodes if spec.node_id == "node-1"),
                                    120, body={"owner": owner, "x": origin[0], "y": origin[1], "z": origin[2],
                                               "static": False, "spacing": 0,
                                               "items": [{"key": "emotion", "typeId": "EmotionControllerBlock",
                                                          "subtype": "EmotionControllerLarge"}]})
    bench.record("00-spawn", answer)
    _require(bench, "the emotion controller grid spawned", answer.get("built"), json.dumps(answer)[:400])
    grid = int(answer["built"][0]["grid"])

    def together():
        place = _client_node(bench)
        owners = partitions_of(bench, [grid])
        return (place, owners[grid]) if place and grid in owners and owners[grid] == place else None

    shared = _poll(together, 180, interval=3)
    _require(bench, "the grid rides the client's partition", shared,
             json.dumps({"client": _client_node(bench), "grid": partitions_of(bench, [grid])})[:400])
    (source, partition), _ = shared
    target = next(node for node in NODES if node != source)
    seen = _poll(lambda: next(iter(api.find_nearby_blocks(block_type="EmotionControllerBlock",
                                                          max_distance=DISTANCE + 30, limit=1)), None), 60, interval=2)
    _require(bench, "the client has the emotion controller replicated", seen, "not within reach")
    bench.record("10-setup", {"grid": grid, "source": source, "target": target, "partition": partition, "block": seen})
    print(f"  [INFO] grid={grid} partition={partition} {source} -> {target}")
    time.sleep(5)

    bench.record("20-handover", bench.cluster.post(_handover_url(bench, partition, source=source, target=target,
                                                                 client=CLIENT_ID), timeout=300))
    committed = _poll(lambda: _lines(bench, "gateway", rf"partition={partition} state=committed") or None, 120, interval=2)
    bench.check("the handover committed", bool(committed))
    _poll(lambda: _lines(bench, target, STOPPED) or None, 20, interval=2)
    stopped = _lines(bench, target, STOPPED)
    bench.check(f"{target} did not stop on a game loop exception", not stopped, " | ".join(stopped)[:600])
    answered = _try(lambda: _partitions(bench, target)) is not None
    bench.check(f"{target} still answers its control route", answered)
    adopted = _lines(bench, target, rf"client-view adopt client={CLIENT_ID} ")
    bench.check(f"{target} adopted the client's view", bool(adopted))
    place = _poll(lambda: (lambda found: found if found and found[0] == target else None)(_client_node(bench)),
                  120, interval=2)
    bench.check(f"the client is on {target}", bool(place), json.dumps(_client_node(bench)))
    reached = _poll(lambda: next(iter(api.find_nearby_blocks(block_type="EmotionControllerBlock",
                                                             max_distance=DISTANCE + 30, limit=1)), None), 60, interval=2)
    bench.check("the client still has the emotion controller after the handover", bool(reached))
    # Recorded, not asserted: on the bench the adopt-to-thaw window is two frames, too short for the group's update
    # layer to come round (octahedron-1191c's eleven sequential adopts held it open for longer).
    adopt_withheld = _lines(bench, target, r"property_sync_withheld ")
    bench.record("30-handover-withheld", adopt_withheld)
    print(f"  [INFO] withheld during the handover: {len(adopt_withheld)}")
    if stopped or not answered:
        bench.exercised(route=f"{source} -> {target}", partition=partition, grid=grid, stopped=len(stopped))
        return

    # ---- THE INJECTION: the frozen-import state (both Syncs null, every property dirty) with the client in view ----
    block = int(answer["built"][0]["block"])
    before = len(_lines(bench, target, r"property_sync_withheld "))
    bench.record("40-null", bench.cluster.node(target, f"/property-sync-test-null?block={block}", method="POST"))
    _poll(lambda: _lines(bench, target, STOPPED) or None, 15, interval=1)
    stopped = _lines(bench, target, STOPPED)
    bench.check(f"{target} did not stop when the emotion controller's null Syncs were state-synced", not stopped,
                " | ".join(stopped)[:600])
    bench.check(f"{target} still answers after the null Syncs", _try(lambda: _partitions(bench, target)) is not None)
    if stopped:
        bench.exercised(route=f"{source} -> {target}", partition=partition, grid=grid, stopped=len(stopped))
        return
    withheld = _lines(bench, target, r"property_sync_withheld ")[before:]
    bench.record("41-withheld", withheld)
    names = {match.group(1) for line in withheld for match in [re.search(r"property=(\S+)", line)] if match}
    bench.check("both null Syncs were withheld from the state sync (the positive control: they reached it)",
                {"m_selectedLCDsIds", "m_selectedEmotionSubtype"} <= names, " | ".join(withheld[:4])[:600])
    bench.record("42-restore", bench.cluster.node(target, f"/property-sync-test-null?block={block}&restore=true",
                                                  method="POST"))
    time.sleep(3)
    counters = [bench.cluster.node(target, "/property-sync-test-counters")]
    time.sleep(5)
    counters.append(bench.cluster.node(target, "/property-sync-test-counters"))
    bench.record("43-counters", counters)
    bench.check("nothing is withheld once the values are back (the properties went out)",
                counters[0].get("withheld") == counters[1].get("withheld"), json.dumps(counters))
    bench.check("no state sync group was dropped", (counters[1].get("dropped") or 0) == 0, json.dumps(counters))
    bench.exercised(route=f"{source} -> {target}", partition=partition, grid=grid, stopped=0,
                    withheld=len(withheld), counters=counters[1])


SCENARIOS_PROPERTY_SYNC = {
    "P3-HND-06": (p3_hnd_06, "client-view adopt of a frozen emotion controller grid: null Syncs in state sync (#1376)", "3"),
}
