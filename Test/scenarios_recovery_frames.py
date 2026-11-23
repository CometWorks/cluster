"""SE-1892: an offline wake loads a station partition without a long game-thread frame, and does not reload it over and over.

octahedron-test, 2026-11-23: recovery loads created every entity of a partition on the game thread in one frame
(LoadRecovery's "create" step): 300-2200 ms for Moon Station and its neighbors, and every client on the node saw
the server sim speed drop to 0.2-0.5. Bots and chained merge wakes brought 317 of 1,418 offline partitions back
within 5 min of going offline, so the same stations paid that frame again and again.

One regular node + WA, no client. A 10,203-block static station (the test plugin's large station) and six 27-block
probes within 300 m of it, so they share its partition.

* ``P2-REC-STAGED`` - the station's partition goes offline and is woken through /registry/partitions/offline/load,
  once with ``recovery.mode=inline`` and once with ``staged``. The node's "recovery loaded partition" line gives the
  load's longest game-thread frame (inline: the whole load; staged: the prepare frame or the longest Add slice) and
  the hitch lines of the window are recorded too. Staged must keep every member and cut the longest frame.
  Then the churn: ``offline.graceSeconds`` 2, and the partition is woken every 6 s for 36 s. With
  ``offline.rewakeHoldSeconds`` 0 it goes offline after each wake and is reloaded each time; with the hold it stays
  online after the first wake, so later wakes load nothing.

Against a build before SE-1892 the config keys are refused; the lane then records one inline measurement and fails.
"""
from __future__ import annotations

import json
import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import _lines, _partitions

NODE = "node-1"
STARTED = [NODE, "wa-1"]
STATION = (20000.0, 20000.0, 0.0)  # the test plugin's LargeStation position
PROBES = 6
LOAD_TIMEOUT = 180
OFFLINE_TIMEOUT = 120
CHURN_SECONDS = 36
CHURN_EVERY = 6

LOADED = r"recovery loaded partition {p} generation (\d+) with (\d+) members \[[^\]]*\] \((.*)\)$"


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _members(bench) -> dict[int, int]:
    return {m["id"]: int(row["id"]) for row in _partitions(bench, NODE) for m in row.get("members") or []}


def _offline(bench, partition: int) -> bool:
    try:
        bench.cluster.get(f"/registry/partitions/offline?partition={partition}")
        return True
    except ClusterError as error:
        if getattr(error, "status", None) == 404:
            return False
        raise


_CONFIGS = [0]


def _config(bench, values: dict) -> bool:
    _CONFIGS[0] += 1
    try:
        bench.record(f"config-{_CONFIGS[0]:02d}", bench.cluster.handover_config(values))
        return True
    except ClusterError as error:
        bench.record("config-refused", {"values": values, "error": str(error)})
        return False


def _loads(bench, partition: int) -> list[str]:
    return _lines(bench, NODE, LOADED.format(p=partition))


def _longest_frame(times: str) -> int:
    """Longest game-thread frame of a load from its "recovery loaded" timing text."""
    staged = re.search(r"longest frame (\d+) ms", times)
    if staged:
        return int(staged.group(1))
    return int(re.match(r"game thread (\d+) ms", times).group(1))


def _hitches_since(bench, count_before: int) -> list[float]:
    lines = _lines(bench, NODE, r"\] hitch ms=")
    return [float(re.search(r"hitch ms=([\d.]+)", line).group(1)) for line in lines[count_before:]]


def _take_offline(bench, partition: int) -> None:
    _config(bench, {"offline.enabled": "true"})
    wait_until(lambda: _offline(bench, partition), OFFLINE_TIMEOUT, f"partition {partition} offline")


def _wake(bench, partition: int, reason: str) -> None:
    try:
        bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node={NODE}&reason={reason}")
    except ClusterError as error:  # online already: nothing to load
        bench.record(f"wake-{reason}-{time.time():.0f}", str(error))


def _measure(bench, partition: int, mode: str, members: set[int]) -> dict:
    staged_keys = _config(bench, {"recovery.mode": mode})
    _take_offline(bench, partition)
    _config(bench, {"offline.enabled": "false"})
    before = len(_loads(bench, partition))
    hitches_before = len(_lines(bench, NODE, r"\] hitch ms="))
    _wake(bench, partition, f"se1892-{mode}")
    wait_until(lambda: len(_loads(bench, partition)) > before, LOAD_TIMEOUT, f"{mode} recovery of {partition}")
    line = _loads(bench, partition)[-1]
    times = re.search(LOADED.format(p=partition), line).group(3)
    time.sleep(5)
    back = set(_members(bench))
    result = {"mode": mode, "keysAccepted": staged_keys, "line": line, "longestFrameMs": _longest_frame(times),
              "hitchesMs": _hitches_since(bench, hitches_before), "missing": sorted(members - back)}
    bench.record(f"load-{mode}", result)
    return result


def _churn(bench, partition: int, hold: int) -> int:
    """Wakes the partition every CHURN_EVERY s for CHURN_SECONDS with a 2 s offline grace; returns the loads."""
    _config(bench, {"offline.rewakeHoldSeconds": str(hold), "offline.rewakeWindowSeconds": "600"})
    if not _offline(bench, partition):
        _take_offline(bench, partition)
    before = len(_loads(bench, partition))
    _config(bench, {"offline.enabled": "true"})
    deadline = time.monotonic() + CHURN_SECONDS
    while time.monotonic() < deadline:
        if _offline(bench, partition):
            _wake(bench, partition, f"se1892-churn-{hold}")
        time.sleep(CHURN_EVERY)
    loads = len(_loads(bench, partition)) - before
    held = _lines(bench, NODE, rf"recovery of partition {partition} came \d+ s after its newest save; held online")
    bench.record(f"churn-hold-{hold}", {"loads": loads, "holdLines": held[-3:]})
    return loads


def p2_rec_staged(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    _config(bench, {"offline.enabled": "false", "offline.wakeMeters": "0", "offline.leadSeconds": "0",
                    "offline.quietMeters": "0", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "2",
                    "offline.settleFrames": "0", "offline.compensate": "false"})
    owner = bench.cluster.node(NODE, "/identity-test-create?name=se1892-owner", "POST")["identityId"]
    station = bench.cluster.node(NODE, f"/offline-test-large-station?owner={owner}", "POST", timeout=120)
    bench.record("station", station)
    grids = {int(station["grid"])}
    for i in range(PROBES):
        x, y, z = STATION[0] + 120 + 30 * i, STATION[1] + 60, STATION[2]
        grids.add(int(bench.cluster.node(NODE, f"/trash-test-spawn?x={x}&y={y}&z={z}&static=true&blocks=27", "POST")["grid"]))
    home = wait_until(lambda: (lambda m: m[station["grid"]] if all(g in m for g in grids)
                               and len({m[g] for g in grids}) == 1 else None)(_members(bench)), 120,
                      "station and probes in one partition")
    _require(bench, "setup: station of 10,203 blocks", station.get("blocks") == 10203, json.dumps(station))

    inline = _measure(bench, home, "inline", grids)
    staged = _measure(bench, home, "staged", grids)
    _require(bench, "staged: the build accepts recovery.mode", staged["keysAccepted"])
    bench.check("inline: every member is back", not inline["missing"], json.dumps(inline["missing"]))
    bench.check("staged: every member is back", not staged["missing"], json.dumps(staged["missing"]))
    bench.check("staged: the load line names its staged frames", "staged: build" in staged["line"], staged["line"])
    bench.check(f"staged: longest frame {staged['longestFrameMs']} ms is under half of inline's {inline['longestFrameMs']} ms",
                staged["longestFrameMs"] * 2 < inline["longestFrameMs"],
                json.dumps({"inline": inline["longestFrameMs"], "staged": staged["longestFrameMs"]}))

    unheld = _churn(bench, home, 0)
    held = _churn(bench, home, 3600)
    bench.check(f"churn without the hold reloads the partition more than once ({unheld} loads)", unheld >= 2)
    bench.check(f"churn with the hold loads it once ({held} loads)", held == 1)
    bench.exercised(inlineLongestFrameMs=inline["longestFrameMs"], stagedLongestFrameMs=staged["longestFrameMs"],
                    inlineMaxHitchMs=max(inline["hitchesMs"], default=0), stagedMaxHitchMs=max(staged["hitchesMs"], default=0),
                    reloadsWithoutHold=unheld, reloadsWithHold=held)


SCENARIOS_RECOVERY_FRAMES = {
    "P2-REC-STAGED": (p2_rec_staged, "SE-1892: a station partition's recovery is staged over short frames, and a re-woken partition is held online", "3"),
}
