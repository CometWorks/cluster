"""#1334: the leftover-ship sweep removes unpiloted grids on their owning node, durably.

octahedron-1191, 2026-10-21: 191 'Ion Pilot-N' ships of recycled and killed fleet clients against 43-86 active
pilots, and 4 of 13 lane legs aborted corridor_blocked on one of them. The Remote plugin's close_grid is
client-local, so the fleet needed a server-side removal: POST /cluster/grid-delete on the node that owns the
grid, driven by Tools/leftover-ship-sweep.py.

One regular node + WA, no client. Five 27-block probes ('cluster-trash-probe-N') far out: A alone, D 100 m from
A (same partition), B and C alone, E alone. The sweep keeps A's number and deletes B, C and D; E is closed the
way trash or combat closes a grid (the test plugin's /test-close-entity) as the control.

* ``P2-GRID-DELETE`` - refusals (wrong name, not here) change nothing; the dry run lists B, C, D; the sweep
  deletes exactly those; A stays. B's and C's partitions, emptied by the delete, get an empty Final and are
  reaped by the registry, so no recovery can load the grids back. E's emptied partition keeps its row and its
  last save, which still holds E: the state the delete has to avoid (the earned red, in the same run).
* ``P2-GRID-DELETE-SLOW-FRAME`` - SE-897: the game thread stalls right after the delete of a partition's only grid
  (/test-empty-release-stall), so heartbeats run on the member counts published before it. The partition is still
  released and reaped, not "left alone: it has members again" until the drain.
* ``P2-GRID-DELETE-WAKE`` - SE-1317, found removing abandoned trader ships on octahedron-test (2026-11-09). (a) Three
  grids of one partition deleted in a row: before the fix each answer said "not emptied" (the member set still held
  the grids closed a moment before), no empty Final was queued and the partition's older save brought all three back.
  (b) A lone grid and a pair, each in an offline partition: a wake is a recovery load, whose 300 s trash grace outlived
  the woken partition, and grid-delete refused them as "arrived recently (import grace)" on every wake. Now refused
  only for ``gridDelete.linkSettleSeconds`` (3 s here); both partitions are emptied and reaped, and a second wake
  brings nothing back.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
import time
import urllib.parse
from pathlib import Path

from cluster import ClusterError, wait_until
from scenarios import _all_partitions
from scenarios_core import _lines, _partitions

NODE = "node-1"
STARTED = [NODE, "wa-1"]
FAR = 600_000.0
SWEEP = Path(__file__).resolve().parent.parent / "Tools" / "leftover-ship-sweep.py"
REAP_TIMEOUT = 120
STALL_MS = 3000


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _members(bench) -> dict[int, int]:
    """grid id -> the node-1 partition holding it."""
    return {m["id"]: int(row["id"]) for row in _partitions(bench, NODE) for m in row.get("members") or []}


def _rows(bench) -> dict[int, dict]:
    """Every registry partition row, owned or not."""
    return {int(row["Partition"]): row for row in bench.cluster.get("/registry/partitions")}


def _newest_entities(bench, partition: int):
    saves = bench.cluster.get(f"/registry/saves?partition={partition}") or []
    newest = max(saves, key=lambda row: (row["SaveId"]["Generation"], row["SaveId"]["Sequence"]), default=None)
    return None if newest is None else newest.get("Entities")


def _sweep(bench, *args: str) -> dict:
    control = next(node.control for node in bench.cluster.config.nodes if node.node_id == NODE)
    control = re.sub(r"^https?://", "", control).rstrip("/")
    run = subprocess.run([sys.executable, str(SWEEP), "--controls", control, *args],
                         capture_output=True, text=True, timeout=120)
    bench.record(f"sweep{'-delete' if '--delete' in args else ''}.txt", run.stdout + run.stderr)
    _require(bench, f"the sweep ran ({' '.join(args)})", run.returncode == 0, run.stderr[-400:])
    return json.loads(run.stdout.strip().splitlines()[-1])


def _capture(bench, partition: int) -> None:
    """Ask for a catalogued save. /save-capture answers 409 while a periodic save is in flight."""

    def queued():
        try:
            bench.cluster.node(NODE, f"/save-capture?partition={partition}", "POST")
            return True
        except ClusterError:
            return None

    wait_until(queued, 60, f"a save of partition {partition} queued")


def p2_grid_delete(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    spawned = {}
    for key, x in (("A", FAR), ("D", FAR + 100), ("B", FAR + 40_000), ("C", FAR + 80_000), ("E", FAR + 120_000)):
        spawned[key] = bench.cluster.node(NODE, f"/trash-test-spawn?x={x}&y=0&z=0&static=false&blocks=27", "POST")
    grids = {key: int(probe["grid"]) for key, probe in spawned.items()}
    names = {key: probe["name"] for key, probe in spawned.items()}
    bench.record("probes", spawned)
    try:
        homes = wait_until(lambda: (lambda m: {k: m[g] for k, g in grids.items()} if all(g in m for g in grids.values())
                                    and all(_all_partitions(bench).get(m[g]) == NODE for g in grids.values()) else None)(
            _members(bench)), 120, "every probe in a partition node-1 owns")
    except ClusterError:
        homes = None
    _require(bench, "setup: every probe is in a partition node-1 owns", homes, json.dumps(_members(bench))[:300])
    _require(bench, "setup: A and D share a partition; B, C and E are alone",
             homes["A"] == homes["D"] and len({homes[k] for k in "ABCE"}) == 4, json.dumps(homes))
    # A catalogued save that holds each probe: the newest save an emptied partition is left with.
    for key in "ABCE":
        _capture(bench, homes[key])
    try:
        wait_until(lambda: all((_newest_entities(bench, homes[k]) or 0) >= 1 for k in "ABCE"), 90, "a save of each probe")
        saved = True
    except ClusterError:
        saved = False
    _require(bench, "setup: each probe's partition has a catalogued save holding it", saved,
             json.dumps({k: _newest_entities(bench, homes[k]) for k in "ABCE"}))

    # Refusals change nothing.
    wrong = bench.cluster.node(NODE, f"/grid-delete?{urllib.parse.urlencode({'entityId': grids['A'], 'name': 'Ion Pilot-999'})}", "POST")
    absent = bench.cluster.node(NODE, f"/grid-delete?{urllib.parse.urlencode({'entityId': 4242, 'name': 'x'})}", "POST")
    bench.record("refusals", {"wrong": wrong, "absent": absent})
    bench.check("a wrong name is refused", wrong.get("deleted") is False and wrong.get("reason") == "name does not match",
                json.dumps(wrong))
    bench.check("an entity not on the node is refused", absent.get("deleted") is False
                and absent.get("reason") == "not a grid on this node", json.dumps(absent))
    bench.check("after the refusals A is still there", grids["A"] in _members(bench))

    # The control: E closed as trash or combat closes a grid.
    bench.record("control-close", bench.cluster.node(NODE, f"/test-close-entity?entity={grids['E']}", "POST"))

    number = {key: int(re.search(r"(\d+)$", names[key]).group(1)) for key in names}
    pattern = ["--pattern", r"cluster-trash-probe-(\d+)", "--active", str(number["A"])]
    dry = _sweep(bench, *pattern)
    bench.check("the dry run lists B, C and D and keeps A", sorted(dry.get("names") or []) == sorted(names[k] for k in "BCD")
                and dry.get("active") == 1, json.dumps(dry))
    bench.check("the dry run deleted nothing", all(grids[k] in _members(bench) for k in "ABCD"))
    swept = _sweep(bench, "--delete", *pattern)
    bench.check("the sweep deleted B, C and D, nothing refused", swept.get("deleted") == 3 and not swept.get("refused"),
                json.dumps(swept))
    try:
        wait_until(lambda: not any(grids[k] in _members(bench) for k in "BCD"), 30, "B, C and D out of the census")
        gone = True
    except ClusterError:
        gone = False
    members = _members(bench)
    bench.check("B, C and D are gone from node-1, A stays", gone and grids["A"] in members, json.dumps(members)[:300])
    removed = {k: bool(_lines(bench, NODE, rf"grid-delete removed grid '{re.escape(names[k])}' with entity id '{grids[k]}'"))
               for k in "BCD"}
    bench.check("node-1 logged each removal", all(removed.values()), json.dumps(removed))
    queued = {k: bool(_lines(bench, NODE, rf"partition {homes[k]} generation \d+ is empty after an admin grid delete"))
              for k in "BC"}
    bench.check("the partitions B and C emptied queued their empty release", all(queued.values()), json.dumps(queued))

    # Durable: the registry reaps the emptied rows, so nothing can recover the old saves.
    try:
        wait_until(lambda: not ({homes["B"], homes["C"]} & _rows(bench).keys()), REAP_TIMEOUT, "B's and C's rows reaped")
        reaped = True
    except ClusterError:
        reaped = False
    rows = _rows(bench)
    bench.record("rows-after", {str(k): rows.get(homes[k]) for k in "ABCE"})
    bench.check("the registry reaped the partitions B and C emptied", reaped,
                json.dumps({k: rows.get(homes[k]) for k in "BC"})[:400])
    bench.check("A's partition is still node-1's", _all_partitions(bench).get(homes["A"]) == NODE)
    # The earned red: E's partition, emptied by a plain close, keeps its row and its last save still holds E.
    e_entities = _newest_entities(bench, homes["E"])
    bench.check("control: the partition a plain close emptied keeps its row and a save holding the grid",
                homes["E"] in rows and (e_entities or 0) >= 1, json.dumps({"row": rows.get(homes["E"]), "entities": e_entities}))
    bench.exercised(grids=grids, partitions=homes, swept=swept)


def p2_grid_delete_slow_frame(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    probe = bench.cluster.node(NODE, f"/trash-test-spawn?x={FAR}&y=0&z=0&static=false&blocks=27", "POST")
    grid, name = int(probe["grid"]), probe["name"]
    try:
        partition = wait_until(lambda: (lambda home: home if home and _all_partitions(bench).get(home) == NODE else None)(
            _members(bench).get(grid)), 120, "the probe in a partition node-1 owns")
    except ClusterError:
        partition = None
    _require(bench, "setup: the probe is in a partition node-1 owns", partition, json.dumps(_members(bench))[:300])
    _capture(bench, partition)
    try:
        wait_until(lambda: (_newest_entities(bench, partition) or 0) >= 1, 90, "a save of the probe")
        saved = True
    except ClusterError:
        saved = False
    _require(bench, "setup: the partition has a catalogued save holding the probe", saved)

    # Long enough for the empty Final to be catalogued and a heartbeat (1 s apart) to run inside the stall.
    bench.record("armed", bench.cluster.node(NODE, f"/test-empty-release-stall?ms={STALL_MS}", "POST"))
    answer = bench.cluster.node(NODE, f"/grid-delete?{urllib.parse.urlencode({'entityId': grid, 'name': name})}", "POST")
    bench.record("delete", answer)
    _require(bench, "the delete queued the empty release", answer.get("deleted") and answer.get("emptyRelease") == "queued",
             json.dumps(answer))
    bench.check("the game thread stalled after the delete", bool(_lines(bench, NODE, rf"empty-release-stall partition {partition}\b")))
    try:
        wait_until(lambda: partition not in _rows(bench), REAP_TIMEOUT, "the emptied partition's row reaped")
        reaped = True
    except ClusterError:
        reaped = False
    bench.check("the release was not given up", not _lines(bench, NODE, rf"empty recovery {partition} generation \d+ left alone"))
    bench.check("the registry reaped the emptied partition", reaped, json.dumps(_rows(bench).get(partition))[:400])
    bench.exercised(grid=grid, partition=partition)


SETTLE_SECONDS = 3
DELETE_WINDOW = 30  # seconds of retries; the 300 s trash grace of a recovery load is far longer


def _delete(bench, grid: int, name: str) -> dict:
    return bench.cluster.node(NODE, f"/grid-delete?{urllib.parse.urlencode({'entityId': grid, 'name': name})}", "POST")


def _delete_retrying(bench, grid: int, name: str) -> dict:
    """Delete, retrying while the answer is the import grace refusal, for DELETE_WINDOW seconds."""
    deadline = time.time() + DELETE_WINDOW
    while True:
        answer = _delete(bench, grid, name)
        if answer.get("deleted") or answer.get("reason") != "arrived recently (import grace)" or time.time() > deadline:
            return answer
        time.sleep(1)


def _offline_members(bench) -> dict[int, list[int]]:
    return {int(row["Partition"]): [int(x) for x in row.get("Members") or []]
            for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])}


def _spawn_set(bench, xs: dict[str, float]) -> tuple[dict[str, int], dict[str, str]]:
    spawned = {key: bench.cluster.node(NODE, f"/trash-test-spawn?x={x}&y=0&z=0&static=false&blocks=27", "POST")
               for key, x in xs.items()}
    bench.record(f"probes-{'-'.join(xs)}", spawned)
    return {k: int(p["grid"]) for k, p in spawned.items()}, {k: p["name"] for k, p in spawned.items()}


def _homes(bench, grids: dict[str, int]) -> dict[str, int] | None:
    try:
        return wait_until(lambda: (lambda m: {k: m[g] for k, g in grids.items()} if all(g in m for g in grids.values())
                                   and all(_all_partitions(bench).get(m[g]) == NODE for g in grids.values()) else None)(
            _members(bench)), 120, "every probe in a partition node-1 owns")
    except ClusterError:
        return None


def _gone_for_good(bench, label: str, grids: dict[str, int], partitions: set[int]) -> None:
    """The emptied partitions' rows are reaped and no offline save lists the deleted grids."""
    ids = set(grids.values())
    try:
        wait_until(lambda: not (partitions & _rows(bench).keys()), REAP_TIMEOUT, f"{label}: rows reaped")
        reaped = True
    except ClusterError:
        reaped = False
    rows = _rows(bench)
    offline = _offline_members(bench)
    saved = sorted(ids & {m for members in offline.values() for m in members})
    bench.record(f"{label}-after", {"rows": {str(p): rows.get(p) for p in partitions},
                                     "offline": {str(p): offline.get(p) for p in partitions}, "savedOffline": saved})
    bench.check(f"{label}: the registry reaped the emptied partitions {sorted(partitions)}", reaped,
                json.dumps({str(p): rows.get(p) for p in partitions})[:400])
    bench.check(f"{label}: no offline save holds a deleted grid", not saved, json.dumps(saved))
    # Wake every row that is left: a delete that did not reach the save comes back here.
    for partition in partitions & rows.keys():
        try:
            bench.record(f"{label}-rewake-{partition}", bench.cluster.post(
                f"/registry/partitions/offline/load?partition={partition}&node={NODE}&reason=se1317"))
        except ClusterError as error:
            bench.record(f"{label}-rewake-{partition}", str(error))
    try:
        back = wait_until(lambda: (lambda m: sorted(ids & m.keys()) or None)(_members(bench)), 20, f"{label}: deleted grids back")
    except ClusterError:
        back = []
    bench.check(f"{label}: no deleted grid is back online after a wake attempt", not back, json.dumps(back))


def p2_grid_delete_wake(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    bench.record("config-setup", bench.cluster.handover_config({"offline.enabled": "false"}))
    try:
        bench.record("config-settle", bench.cluster.handover_config({"gridDelete.linkSettleSeconds": str(SETTLE_SECONDS)}))
    except ClusterError as error:   # a build before SE-1317 has no such key
        bench.record("config-settle", str(error))

    # (a) Three grids of one online partition, deleted in a row.
    grids, names = _spawn_set(bench, {"T1": FAR, "T2": FAR + 100, "T3": FAR + 200})
    homes = _homes(bench, grids)
    _require(bench, "setup: T1, T2 and T3 share a partition node-1 owns", homes and len(set(homes.values())) == 1,
             json.dumps(homes or _members(bench))[:300])
    trio = homes["T1"]
    _capture(bench, trio)
    try:
        wait_until(lambda: (_newest_entities(bench, trio) or 0) >= 3, 90, "a save of the three")
        saved = True
    except ClusterError:
        saved = False
    _require(bench, "setup: the partition has a catalogued save holding the three", saved)
    answers = {k: _delete(bench, grids[k], names[k]) for k in ("T1", "T2", "T3")}
    bench.record("trio-deletes", answers)
    bench.check("trio: all three deletes succeed", all(a.get("deleted") for a in answers.values()), json.dumps(answers)[:500])
    bench.check("trio: the last delete empties the partition and queues its empty release",
                answers["T3"].get("emptyRelease") == "queued", json.dumps(answers["T3"]))
    _gone_for_good(bench, "trio", grids, {trio})

    # (b) A lone grid and a pair, each in an offline partition, deleted through a wake.
    grids, names = _spawn_set(bench, {"L": FAR + 60_000, "P1": FAR + 120_000, "P2": FAR + 120_100})
    homes = _homes(bench, grids)
    _require(bench, "setup: L alone, P1 and P2 together, in partitions node-1 owns",
             homes and homes["P1"] == homes["P2"] and homes["L"] != homes["P1"], json.dumps(homes or _members(bench))[:300])
    woken = {homes["L"], homes["P1"]}
    for partition in woken:
        _capture(bench, partition)
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    try:
        wait_until(lambda: woken <= _offline_members(bench).keys(), 180, "both partitions offline")
        went = True
    except ClusterError:
        went = False
    offline = _offline_members(bench)
    _require(bench, "setup: both partitions went offline holding their grids", went and all(
        grids[k] in offline.get(homes[k], []) for k in grids), json.dumps({str(p): offline.get(p) for p in woken}))
    # Woken partitions stay up long enough for the deletes; production's 60 + 180 s would too.
    bench.record("config-woken", bench.cluster.handover_config({"offline.minOnlineSeconds": "120", "offline.graceSeconds": "0"}))
    for partition in woken:
        bench.record(f"wake-{partition}", bench.cluster.post(
            f"/registry/partitions/offline/load?partition={partition}&node={NODE}&reason=se1317"))
    try:
        wait_until(lambda: set(grids.values()) <= _members(bench).keys(), 120, "the woken grids online")
        loaded = time.time()
    except ClusterError:
        loaded = None
    _require(bench, "setup: the wakes loaded all three grids on node-1", loaded, json.dumps(_members(bench))[:300])
    answers = {k: _delete_retrying(bench, grids[k], names[k]) for k in ("L", "P1", "P2")}
    took = round(time.time() - loaded, 1)
    bench.record("woken-deletes", {"answers": answers, "secondsAfterLoad": took})
    bench.check("woken: every grid of a woken partition is deleted within the trash grace (not refused as import grace)",
                all(a.get("deleted") for a in answers.values()), json.dumps({"took": took, **answers})[:500])
    bench.check("woken: both woken partitions are emptied and queue their empty release",
                answers["L"].get("emptyRelease") == "queued" and answers["P2"].get("emptyRelease") == "queued",
                json.dumps({k: answers[k].get("emptyRelease") for k in answers}))
    bench.record("config-offline-off", bench.cluster.handover_config({"offline.enabled": "false"}))
    _gone_for_good(bench, "woken", grids, woken)
    bench.exercised(woken=sorted(woken), deleteSecondsAfterLoad=took)


SCENARIOS_GRID_DELETE = {
    "P2-GRID-DELETE": (p2_grid_delete, "#1334: the leftover-ship sweep deletes unpiloted grids on their node, and an emptied partition is reaped", "3"),
    "P2-GRID-DELETE-SLOW-FRAME": (p2_grid_delete_slow_frame, "SE-897: a slow frame right after the delete does not cost the emptied partition its release", "3"),
    "P2-GRID-DELETE-WAKE": (p2_grid_delete_wake, "SE-1317: grids of a woken offline partition can be deleted, and deletes that empty a partition stay deleted", "3"),
}
