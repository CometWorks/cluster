"""SE-1893: the World Authority's scheduled voxel journal compaction, and the entries a voxel reset overwrites.

Live Octahedron, 2026-11-26: the WA held 179,397 journal entries (4.77 GB) in 246 storages and had never compacted
one, so every new node replayed all of them (SE-1941, SE-1946). The WA now compacts every night at
``voxel.compactAt`` in ``voxel.compactTimeZone`` (04:00 Europe/Stockholm); these scenarios set
``voxel.compactEverySeconds`` to run it every few seconds.

WA + one regular node, Cluster-Simple, no client. The WA dispatches an asteroid and node-1 writes 32^3 cubes into it
``SE1893_PASSES`` times (see ``scenarios_voxel_catchup``), which leaves a journal of overwrites.

P2-VOX-COMPACT:

* ``blocked``: node-1 is replaced by a fresh node, which replays the journal from sequence 0, and the compaction is
  switched on while it does. A node that has not reported a watermark in its current epoch holds the cut, so the
  first run catalogues a base and cuts nothing, names node-1 as the blocker, and the retry cuts the journal once
  node-1 has caught up. node-1 must end identical to the WA: the compaction never took an entry it still needed.
  Recorded: entries and payload bytes before and after, the run's save and wall time, and the WA's frames of 20 ms
  or more while it ran.
* ``fresh``: node-1 is replaced again. It installs the catalogued base, applies no journal entry, and matches the WA.

P2-VOX-RESET-SUPERSEDE: with compaction off, the voxel reset (``voxelReset.ageSeconds`` 5) puts the edited asteroid
back. Every entry before the reset becomes a stub, so the WA's retained payload is the reset entries alone, and a
fresh node-1 that replays the journal from sequence 0 ends identical to the WA's reset asteroid.

Knobs (environment): ``SE1893_RADIUS`` (asteroid radius, m, default 100), ``SE1893_PASSES`` (default 40).
"""
from __future__ import annotations

import json
import os
import re
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime

from cluster import wait_until
from scenarios_voxel_catchup import CUBE, STEP, _frames, _hash, _node, _query, _require, _try

NODE = "node-1"
STARTED = ["wa-1", NODE]
ORIGIN = 2_000_000
RADIUS = int(os.environ.get("SE1893_RADIUS", "100"))
PASSES = int(os.environ.get("SE1893_PASSES", "40"))
WRITERS = 4
HITCHES = {"hitch.thresholdMs": "20", "hitch.logThresholdMs": "20"}
COMPACT_OFF = {"voxel.compactAt": "", "voxel.compactEverySeconds": "0"}
DONE = re.compile(r"voxel_compact done=true (.*)$")
STAMP = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)")


def _status(bench, name: str) -> dict:
    return _try(lambda: _node(bench, name, "/voxel-journal-status", timeout=30), {}) or {}


def _log(bench, name: str) -> list[str]:
    path = bench.run_root / f"{name}.log"
    return path.read_text(errors="replace").splitlines() if path.is_file() else []


def _fields(text: str) -> dict:
    return dict(pair.split("=", 1) for pair in text.split() if "=" in pair)


def _stamp(line: str) -> float | None:
    match = STAMP.match(line)
    return datetime.strptime(match.group(1)[:23], "%Y-%m-%d %H:%M:%S.%f").timestamp() if match else None


def _config(bench, values: dict) -> dict:
    return bench.cluster.handover_config({**HITCHES, "dormant.enabled": "false", **values})


def _build_journal(bench) -> tuple[str, list, dict]:
    """Spawns the asteroid, writes the passes from node-1 and returns the storage, its cubes and the WA's hashes."""
    storage = _node(bench, "wa-1", f"/voxel-test-spawn-asteroid?x={ORIGIN}&y={ORIGIN}&z={ORIGIN}"
                                   f"&radius={RADIUS}&seed=23", "POST")["name"]
    per_axis = max(1, (2 * RADIUS - CUBE) // STEP + 1)
    cubes = [(x * STEP, y * STEP, z * STEP) for x in range(per_axis) for y in range(per_axis) for z in range(per_axis)]
    _require(bench, f"setup: {NODE} holds the dispatched asteroid {storage}",
             _try(lambda: wait_until(lambda: _hash(bench, NODE, storage, cubes[0]), 180, "asteroid on node-1")))
    pristine = {cube: _hash(bench, "wa-1", storage, cube) for cube in cubes}
    with ThreadPoolExecutor(WRITERS) as pool:
        for index in range(PASSES):
            content, material = 40 + (index * 37) % 200, 1 + index % 5

            def write(cube):
                return _node(bench, NODE, f"/voxel-test-write?{_query(storage, cube)}"
                                          f"&content={content}&material={material}", "POST")["hash"]

            list(pool.map(write, cubes))
            time.sleep(1.5)  # the pass's dirty ranges settle and publish before the same cubes are written again

    def settled():
        status = _status(bench, NODE)
        if status.get("outboundQueued") != 0 or status.get("dirtyRanges") != 0:
            return None
        first = {cube: _hash(bench, "wa-1", storage, cube) for cube in cubes}
        time.sleep(5)
        second = {cube: _hash(bench, "wa-1", storage, cube) for cube in cubes}
        return second if first == second and None not in second.values() else None

    wa = _try(lambda: wait_until(settled, 300, "the WA's copy settles", interval=5))
    _require(bench, "setup: node-1's journal reached the WA and the WA's copy is stable", wa)
    bench.record("pristine", {"cubes": len(cubes), "hashes": {str(k): v for k, v in pristine.items()}})
    return storage, cubes, {"wa": wa, "pristine": pristine}


def _journal(bench, storage: str) -> dict:
    rows = _try(lambda: _node(bench, "wa-1", f"/voxel-journal-entries?storage={storage}", timeout=120), []) or []
    status = _status(bench, "wa-1")
    return {"entries": len(rows), "tail": max((row["sequence"] for row in rows), default=0),
            "kinds": {kind: sum(1 for row in rows if row["kind"] == kind) for kind in {row["kind"] for row in rows}},
            "retainedEntries": status.get("retainedEntries"), "retainedBytes": status.get("retainedBytes"),
            "retainedStubs": status.get("retainedStubs"), "compaction": status.get("compaction")}


def _converged(bench, storage: str, cubes: list, want: dict, label: str, timeout: float = 180) -> tuple[bool, list]:
    seen = {}

    def same():
        seen.update({cube: _hash(bench, NODE, storage, cube) for cube in cubes if seen.get(cube) != want[cube]})
        return all(seen[cube] == want[cube] for cube in cubes)

    ok = bool(_try(lambda: wait_until(same, timeout, label, interval=2), False))
    return ok, [list(cube) for cube in cubes if seen.get(cube) != want[cube]][:10]


def p2_vox_compact(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    bench.cluster.post("/automatic/pause")
    bench.record("config", _config(bench, COMPACT_OFF))
    storage, cubes, hashes = _build_journal(bench)
    want = hashes["wa"]
    before = _journal(bench, storage)
    bench.record("journal-before", before)
    bench.check(f"[INFO] setup: {before['entries']} journal entries (tail {before['tail']}), "
                f"{(before['retainedBytes'] or 0) / 1e6:.1f} MB of payload on the WA", True, json.dumps(before))

    # blocked: a fresh node-1 replays from sequence 0 while the compaction runs every 10 s.
    bench.cluster.restart_node(NODE, fresh=True)
    wa_lines_before = len(_log(bench, "wa-1"))
    bench.record("config-blocked", _config(bench, {"voxel.compactAt": "", "voxel.compactEverySeconds": "10",
                                                   "voxel.compactRetrySeconds": "5"}))

    def cut():
        rows = _try(lambda: _node(bench, "wa-1", f"/voxel-journal-entries?storage={storage}", timeout=60))
        return rows is not None and len(rows) == 0

    started = time.time()
    emptied = bool(_try(lambda: wait_until(cut, 900, "the WA's journal of the asteroid is cut", interval=3), False))
    emptied_s = time.time() - started
    bench.record("config-off", _config(bench, COMPACT_OFF))
    lines = _log(bench, "wa-1")[wa_lines_before:]
    runs = [_fields(match.group(1)) | {"stamp": _stamp(line)} for line in lines if (match := DONE.search(line))]
    retries = [line for line in lines if "voxel_compact retry=true" in line]
    first = runs[0] if runs else {}
    bench.record("runs", {"runs": runs, "retries": retries[-10:], "emptiedS": round(emptied_s, 1)})
    _require(bench, "blocked: the WA's journal of the asteroid ends empty", emptied,
             json.dumps({"runs": runs[-3:], "retries": retries[-3:]}))
    held = "node-1" in first.get("blockers", "")
    bench.check(f"[INFO] blocked: the first run, while node-1 replayed, was held back by node-1: {held} "
                f"(blocked={first.get('blocked')} blockers={first.get('blockers')})", True, json.dumps(first))
    ok, diverged = _converged(bench, storage, cubes, want, "blocked: node-1 converged")
    bench.check("blocked: the replaying node-1 ends identical to the WA (no entry it needed was cut)", ok,
                json.dumps(diverged))
    # The run that cut the journal: the WA's frames of 20 ms or more while the first run saved and truncated.
    frames = _frames(lines)
    start_lines = [_stamp(line) for line in lines if "voxel_compact start=true" in line]
    window = (start_lines[0], first.get("stamp")) if start_lines and first.get("stamp") else None
    during = [f for f in frames if window and window[0] - 0.5 <= f["epoch"] <= window[1] + 0.5]
    after = _journal(bench, storage)
    measure = {
        "entriesBefore": before["entries"], "bytesBefore": before["retainedBytes"],
        "entriesAfter": after["entries"], "bytesAfter": after["retainedBytes"],
        "firstRun": first, "runsUntilEmpty": len(runs), "retries": len(retries), "emptiedS": round(emptied_s, 1),
        "waFrames20msDuringFirstRun": len(during), "waMaxFrameMsDuringFirstRun": max((f["ms"] for f in during), default=0.0),
    }
    bench.record("measure", measure)
    bench.check(f"[INFO] compaction: {before['entries']} -> {after['entries']} entries, "
                f"{(before['retainedBytes'] or 0) / 1e6:.1f} -> {(after['retainedBytes'] or 0) / 1e6:.1f} MB; first run "
                f"save {first.get('save_ms')} ms (max {first.get('max_save_ms')} ms), wall {first.get('wall_s')} s; "
                f"WA frames >= 20 ms during it: {len(during)} (max {measure['waMaxFrameMsDuringFirstRun']:.0f} ms)",
                True, json.dumps(measure)[:1500])

    # fresh: a new node-1 installs the base and needs no journal entry.
    bench.cluster.restart_node(NODE, fresh=True)
    bench.cluster.wait_ready(nodes=[NODE], serving=True)
    ok, diverged = _converged(bench, storage, cubes, want, "fresh: node-1 converged")
    node_lines = _log(bench, NODE)
    installed = [line for line in node_lines if "voxel_catalog asteroid_installed=true" in line and storage in line]
    catch_up = [line for line in node_lines if "voxel_journal catch_up " in line]
    bench.record("fresh", {"installed": installed, "catchUp": catch_up, "status": _status(bench, NODE)})
    bench.check("fresh: the new node installed the catalogued base of the asteroid", installed, "\n".join(node_lines[-20:]))
    bench.check("fresh: the new node matches the WA on every cube", ok, json.dumps(diverged))
    bench.check("fresh: the new node replayed no journal catch-up", not catch_up, "\n".join(catch_up))
    bench.exercised(**{k: v for k, v in measure.items() if k != "firstRun"}, radius=RADIUS, passes=PASSES,
                    firstRunSaveMs=first.get("save_ms"), firstRunWallS=first.get("wall_s"))


def p2_vox_reset_supersede(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    bench.cluster.post("/automatic/pause")
    bench.record("config", _config(bench, {**COMPACT_OFF, "voxelReset.enabled": "false"}))
    storage, cubes, hashes = _build_journal(bench)
    before = _journal(bench, storage)
    bench.record("journal-before", before)

    def occupancy():
        state = bench.cluster.get("/registry/voxel-reset-occupancy")
        return state if state.get("complete") else None

    bench.record("occupancy", wait_until(occupancy, 120, "the Gateway reports a complete occupancy"))
    bench.record("config-reset", _config(bench, {**COMPACT_OFF, "voxelReset.enabled": "true",
                                                 "voxelReset.ageSeconds": "5", "voxelReset.scanSeconds": "2",
                                                 "voxelReset.cubesPerUpdate": "8"}))
    pristine = hashes["pristine"]
    reset = bool(_try(lambda: wait_until(
        lambda: all(_hash(bench, "wa-1", storage, cube) == pristine[cube] for cube in cubes),
        300, "the WA reset the asteroid", interval=3), False))
    bench.record("config-off", _config(bench, {**COMPACT_OFF, "voxelReset.enabled": "false"}))
    _require(bench, "the WA put the whole asteroid back to its generated content", reset)
    time.sleep(3)  # the stub files are written off the game thread
    after = _journal(bench, storage)
    lines = [line for line in _log(bench, "wa-1") if "voxel_reset storage=" + storage in line]
    superseded = sum(int(_fields(line).get("superseded", 0)) for line in lines)
    resets = len(lines)
    bench.record("journal-after", {**after, "resetLines": lines[-20:], "superseded": superseded})
    ranges = after["kinds"].get("Range", 0)
    stubs = after["kinds"].get("CutoutSphere", 0)
    bench.check(f"[INFO] reset: {before['entries']} entries, {(before['retainedBytes'] or 0) / 1e6:.1f} MB before; "
                f"after {resets} cube resets {ranges} range entries and {stubs} stubs, "
                f"{(after['retainedBytes'] or 0) / 1e6:.2f} MB", True, json.dumps(after)[:1500])
    bench.check("reset: every entry before the reset became a stub; only the reset entries keep content",
                ranges == resets and stubs == before["entries"] and superseded == before["entries"],
                json.dumps({"ranges": ranges, "resets": resets, "stubs": stubs, "superseded": superseded,
                            "before": before["entries"]}))

    bench.cluster.restart_node(NODE, fresh=True)
    bench.cluster.wait_ready(nodes=[NODE], serving=True)
    ok, diverged = _converged(bench, storage, cubes, pristine, "a fresh node-1 converged on the reset asteroid")
    status = _status(bench, NODE)
    applied = int((status.get("applied") or {}).get(storage, 0))
    bench.check(f"reset: a fresh node replays the stubbed journal (applied {applied} of tail {after['tail']}) and "
                "ends on the reset asteroid", ok and applied >= after["tail"], json.dumps(diverged))
    bench.exercised(entriesBefore=before["entries"], bytesBefore=before["retainedBytes"], resets=resets,
                    stubs=stubs, bytesAfter=after["retainedBytes"], radius=RADIUS, passes=PASSES)


SCENARIOS_VOXEL_COMPACT = {
    "P2-VOX-COMPACT": (p2_vox_compact, "SE-1893: the scheduled compaction waits for a replaying node, cuts the journal and a fresh node starts from the base", "2"),
    "P2-VOX-RESET-SUPERSEDE": (p2_vox_reset_supersede, "SE-1893: a voxel reset turns the entries it overwrites into stubs; a fresh node replays to the reset content", "2"),
}
