"""SE-1946: a new node replays a long voxel journal in short game-thread frames and ends identical to the WA.

Live Octahedron, 2026-11-25 (SE-1941): every new node logged 15-20 s of 260-540 ms frames about twice a second
while it caught up on the voxel journal. Update applied up to 64 entries every 250 ms, a count with no time
budget, and one range write costs a few ms.

WA + one regular node, Cluster-Simple, no client. The WA dispatches an asteroid (radius=200, a 256^3 storage),
node-1 writes 125 32^3 cubes inside it ``SE1946_PASSES`` times with different content, and node-1 is then replaced by a fresh node on an empty
data root, which replays the whole journal from sequence 0. Overwrites stay in the journal, as in one that is
never compacted. node-1's dirty-range capture unions nearby cubes, so 5,000 writes make about 400 entries of up
to 64^3 voxels, each a heavier write than a live hand-drill entry. Two legs:

* ``dormant``: the defaults. A fresh node with no content runs its main loop at ``dormant.hz`` (4 Hz).
* ``awake``: ``dormant.enabled=false``, the main loop at 60 Hz, as on a node that holds partitions or clients.

Each leg records the node's frames of 20 ms or more (``hitch.thresholdMs`` and ``hitch.logThresholdMs`` are 20 for
the run) from the frame that materializes the asteroid to the first poll that sees the WA's tail applied, and
whether the fresh node's hash of every cube matches the WA's. The materializing frame and the ones before it are
start-up (SE-1952) and recorded apart. With the fix no catch-up frame reaches 100 ms; run it on a package without
the fix for the before numbers (the convergence checks pass on both).

Knobs (environment): ``SE1946_RADIUS`` (the spawn's radius, which is the vanilla asteroid size: the storage is the
next power of two, default 200), ``SE1946_PASSES`` (default 40).
"""
from __future__ import annotations

import json
import os
import re
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime

from cluster import ClusterError, wait_until

NODE = "node-1"
STARTED = ["wa-1", NODE]
ORIGIN = 2_000_000
RADIUS = int(os.environ.get("SE1946_RADIUS", "200"))
PASSES = int(os.environ.get("SE1946_PASSES", "40"))
CUBE = 32
EDGE = int(os.environ.get("SE1946_EDGE", "0"))  # first cube's offset from the storage corner
STEP = 48  # 16 voxels between cubes: 5 per axis in a 256^3 storage
WRITERS = 4
CATCH_UP_TIMEOUT = 1200
SLOW_MS = 100

HITCH = re.compile(r"\] hitch ms=([\d.]+) ageMs=(\d+)(.*)$")
DONE = re.compile(r"voxel_replica catch_up_done=true .*")


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:1500]):
        raise ClusterError(f"{label}: {detail[:1500]}")


def _node(bench, name: str, path: str, method: str = "GET", timeout: float = 40):
    return bench.cluster.node(name, path, method=method, timeout=timeout)


def _try(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def _query(storage: str, cube: tuple[int, int, int]) -> str:
    x, y, z = cube
    return f"storage={storage}&x={x}&y={y}&z={z}&size={CUBE}"


def _hash(bench, name: str, storage: str, cube) -> str | None:
    return _try(lambda: _node(bench, name, f"/voxel-range-hash?{_query(storage, cube)}").get("hash"))


def _log(bench) -> list[str]:
    """This start's stdout log: a restart rotates the previous one aside."""
    path = bench.run_root / f"{NODE}.log"
    return path.read_text(errors="replace").splitlines() if path.is_file() else []


def _applied(bench, storage: str) -> int:
    status = _try(lambda: _node(bench, NODE, "/voxel-journal-status", timeout=10), {}) or {}
    return int((status.get("applied") or {}).get(storage, 0))


def _frames(lines: list[str]) -> list[dict]:
    frames = []
    for line in lines:
        match = HITCH.search(line)
        if match:
            journal = re.search(r"voxel_journal=([\d.]+)/(\d+)", match.group(3))
            # The node's log stamps local time; the bench runs on the same host, so timestamp() reads it the same way.
            try:
                stamp = datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f").timestamp()
            except ValueError:  # the unstamped stdout copy of a line the game log stamps
                continue
            frames.append({"ms": float(match.group(1)), "ageMs": int(match.group(2)), "epoch": round(stamp, 3),
                           "journalMs": float(journal.group(1)) if journal else None})
    return frames


def _replay(bench, leg: str, storage: str, tail: int, cubes: list, want: dict) -> dict:
    """Replaces node-1 by a fresh node and measures its catch-up until it has applied the WA's tail.

    The fresh node can only apply the journal once the asteroid has materialized on it, and that frame (a load
    with a World Authority round trip, 3 s here) is not part of the replay: the catch-up frames are the ones after
    it, until the first poll that saw the tail applied. The frames before are recorded apart."""
    restarted = time.time()
    bench.cluster.restart_node(NODE, fresh=True)
    samples = []

    def caught_up():
        samples.append((time.time(), _applied(bench, storage)))
        return samples[-1][1] >= tail

    wait_until(caught_up, CATCH_UP_TIMEOUT, f"{leg}: {NODE} applied {tail}", interval=1)
    end = samples[-1][0]
    lines = _log(bench)
    done = next((match for match in map(DONE.search, lines) if match), None)
    materialized = next((index for index, line in enumerate(lines) if f"asteroid materialized name={storage}" in line), 0)
    before, after = _frames(lines[:materialized]), _frames(lines[materialized:])
    # The first hitch after the line is the materializing frame itself: it is logged when that frame ends.
    materialize = after[0] if after else None
    frames = [frame for frame in after[1:] if frame["epoch"] <= end + 1]
    begin = materialize["epoch"] if materialize else restarted
    startup = before + after[:1]
    bench.cluster.wait_ready(nodes=[NODE], serving=True)
    seen = {}

    def converged():
        seen.update({cube: _hash(bench, NODE, storage, cube) for cube in cubes if seen.get(cube) != want[cube]})
        return all(seen[cube] == want[cube] for cube in cubes)

    same = _try(lambda: wait_until(converged, 120, f"{leg}: {NODE} converged", interval=2), False)
    slow = [frame["ms"] for frame in frames if frame["ms"] >= SLOW_MS]
    result = {
        "leg": leg, "restartToAppliedS": round(end - restarted, 1), "catchUpS": round(end - begin, 1),
        "catchUpDone": done.group(0) if done else None,
        "materializeFrameMs": materialize["ms"] if materialize else None, "startupFrames20ms": len(startup), "startupMaxFrameMs": max((frame["ms"] for frame in startup), default=0.0),
        "converged": bool(same),
        "diverged": [list(cube) for cube in cubes if seen.get(cube) != want[cube]][:10],
        "frames20ms": len(frames), "frames100ms": len(slow), "slowMsTotal": round(sum(slow)),
        "maxFrameMs": max((frame["ms"] for frame in frames), default=0.0),
        "maxJournalMs": max((frame["journalMs"] or 0.0 for frame in frames), default=0.0),
        "summaries": [line for line in _log(bench) if "voxel_journal catch_up " in line][-3:],
    }
    bench.record(f"replay-{leg}", {**result, "begin": begin, "end": end, "samples": samples, "frames": before + after})
    return result


def p2_vox_catchup(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    bench.cluster.post("/automatic/pause")
    hitches = {"hitch.thresholdMs": "20", "hitch.logThresholdMs": "20"}
    # Awake while it writes: a dormant node-1 runs one frame per 250 ms, and each write waits for one.
    bench.record("config", bench.cluster.handover_config({**hitches, "dormant.enabled": "false"}))
    storage = _node(bench, "wa-1", f"/voxel-test-spawn-asteroid?x={ORIGIN}&y={ORIGIN}&z={ORIGIN}"
                                   f"&radius={RADIUS}&seed=19", "POST")["name"]
    held = _try(lambda: wait_until(lambda: _try(lambda: _node(bench, NODE, f"/voxel-test-dump?storage={storage}"
                                                                    "&x=0&y=0&z=0&mx=0&my=0&mz=0")),
                                   180, "asteroid on node-1"))
    _require(bench, f"setup: {NODE} holds the dispatched asteroid {storage}", held)
    # SE-1950: every cube inside the storage. A write past its edge is refused; before that it was dropped, and
    # the cubes there matched as empty voxels.
    per_axis = max(1, (held["size"] - CUBE - EDGE) // STEP + 1)
    cubes = [(EDGE + x * STEP, EDGE + y * STEP, EDGE + z * STEP)
             for x in range(per_axis) for y in range(per_axis) for z in range(per_axis)]

    want = {}
    with ThreadPoolExecutor(WRITERS) as pool:
        for index in range(PASSES):
            content, material = 40 + (index * 37) % 200, 1 + index % 5

            def write(cube):
                return _node(bench, NODE, f"/voxel-test-write?{_query(storage, cube)}"
                                          f"&content={content}&material={material}", "POST")["hash"]

            want.update(zip(cubes, pool.map(write, cubes)))
            time.sleep(1.5)  # the pass's dirty ranges settle and publish before the same cubes are written again

    # The fresh node must end where the WA is, and the WA where node-1's last write left each cube (SE-1950).
    def settled():
        status = _try(lambda: _node(bench, NODE, "/voxel-journal-status"), {}) or {}
        if status.get("outboundQueued") != 0 or status.get("dirtyRanges") != 0:
            return None
        first = {cube: _hash(bench, "wa-1", storage, cube) for cube in cubes}
        time.sleep(5)
        second = {cube: _hash(bench, "wa-1", storage, cube) for cube in cubes}
        return second if first == second and None not in second.values() else None

    wa = _try(lambda: wait_until(settled, 300, "the WA's copy settles", interval=5))
    _require(bench, "setup: node-1's journal reached the WA and the WA's copy is stable", wa)
    journal = _node(bench, "wa-1", f"/voxel-journal-entries?storage={storage}", timeout=120)
    entries, tail = len(journal), max((row["sequence"] for row in journal), default=0)
    differ = [list(cube) for cube in cubes if wa[cube] != want[cube]]
    bench.record("journal", {"entries": entries, "tail": tail, "cubes": len(cubes), "writes": len(cubes) * PASSES,
                             "originDiffers": differ})
    bench.check(f"setup: {entries} journal entries from {len(cubes) * PASSES} writes; "
                f"{len(differ)} of {len(cubes)} cubes on the WA differ from node-1's last write", not differ, json.dumps(differ)[:1500])
    want = wa

    bench.cluster.handover_config({**hitches, "dormant.enabled": "true"})
    dormant = _replay(bench, "dormant", storage, tail, cubes, want)
    bench.cluster.handover_config({**hitches, "dormant.enabled": "false"})
    awake = _replay(bench, "awake", storage, tail, cubes, want)

    for leg in (dormant, awake):
        name = leg["leg"]
        bench.check(f"[INFO] {name}: the fresh node applied the WA's tail {tail} in {leg['catchUpS']:.1f} s, "
                    f"{leg['restartToAppliedS']:.1f} s after its restart",
                    True, json.dumps(leg)[:1500])
        bench.check(f"{name}: every cube on the fresh node matches the WA", leg["converged"], json.dumps(leg["diverged"]))
        bench.check(f"{name}: no catch-up frame reached {SLOW_MS} ms (max {leg['maxFrameMs']:.1f} ms, "
                    f"{leg['frames100ms']} slow frames)", leg["frames100ms"] == 0, json.dumps(leg)[:1500])
    bench.exercised(entries=entries, cubes=len(cubes), passes=PASSES, radius=RADIUS,
                    **{f"{leg['leg']}{key[0].upper()}{key[1:]}": leg[key] for leg in (dormant, awake)
                       for key in ("catchUpS", "restartToAppliedS", "frames20ms", "frames100ms", "slowMsTotal", "maxFrameMs", "maxJournalMs")})


SCENARIOS_VOXEL_CATCHUP = {
    "P2-VOX-CATCHUP": (p2_vox_catchup, "SE-1946: a fresh node replays a long voxel journal without 100 ms frames and converges", "2"),
}
