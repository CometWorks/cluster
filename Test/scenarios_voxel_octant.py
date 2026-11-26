"""SE-1950: node-1's edits to an asteroid reach the World Authority with the content node-1 holds.

Found in SE-1946 (P2-VOX-CATCHUP setup): node-1 wrote 32^3 cubes of uniform content into a WA-dispatched asteroid,
and every cube in (0..127)^3 ended on the WA with other content than node-1 held. That box is the whole storage
(the spawn's radius=100 is a 128^3 storage); the cubes past it were outside and matched as empty on both.

The cause is in the game's octree storage: a write that leaves a 16^3 content leaf uniform at a value other than 0
or 255 drops the leaf and keeps the node's filtered value, which halves the signed distance (114 reads back as 121).
node-1 publishes the 121 it reads, the WA's write of it collapses again to 124. MicroOctreeUniformLeafPatch keeps
such a leaf, so the value is stored exactly.

WA + node-1, Cluster-Simple, no client. The WA dispatches an asteroid, node-1 writes ``SE1950_PASSES`` passes of
32^3 cubes inside the storage (on a 40-voxel lattice and flush with the far edge), and once node-1 has published
everything the rig compares, voxel by voxel, what node-1 wrote, what it holds and what the WA holds, and records
every journal entry against both. Before the fix node-1 held 121 for its 114 and the WA 124 in every 16^3 leaf a
cube covered completely: all 64 cubes differed.

Knobs (environment): ``SE1950_RADIUS`` (default 100), ``SE1950_PASSES`` (default 3), ``SE1950_WRITERS`` (default 1).
"""
from __future__ import annotations

import base64
import collections
import hashlib
import json
import os
import time
from concurrent.futures import ThreadPoolExecutor

from cluster import ClusterError, wait_until

NODE = "node-1"
ORIGIN = 2_000_000
RADIUS = int(os.environ.get("SE1950_RADIUS", "100"))
PASSES = int(os.environ.get("SE1950_PASSES", "3"))
WRITERS = int(os.environ.get("SE1950_WRITERS", "1"))
CUBE = 32
STEP = 40


def _node(bench, name: str, path: str, method: str = "GET", timeout: float = 60):
    return bench.cluster.node(name, path, method=method, timeout=timeout)


def _try(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def _dump(bench, name: str, storage: str, lo, hi) -> tuple[bytes, bytes]:
    reply = _node(bench, name, f"/voxel-test-dump?storage={storage}&x={lo[0]}&y={lo[1]}&z={lo[2]}"
                               f"&mx={hi[0]}&my={hi[1]}&mz={hi[2]}")
    return base64.b64decode(reply["content"]), base64.b64decode(reply["material"])


def _cube_box(cube):
    return cube, tuple(value + CUBE - 1 for value in cube)


def _hash(dump: tuple[bytes, bytes]) -> str:
    return hashlib.sha256(dump[0] + dump[1]).hexdigest().upper()


def _compare(holder, other, pristine) -> dict:
    """Where ``other`` differs from ``holder``: how many voxels, and whether ``other`` holds the pristine value there."""
    differ = [i for i in range(len(holder[0])) if holder[0][i] != other[0][i] or holder[1][i] != other[1][i]]
    pristine_there = sum(1 for i in differ if other[0][i] == pristine[0][i] and other[1][i] == pristine[1][i])
    values = collections.Counter((other[0][i], other[1][i]) for i in differ).most_common(4)
    held = collections.Counter((holder[0][i], holder[1][i]) for i in differ).most_common(4)
    return {"voxels": len(differ), "pristine": pristine_there, "values": [list(value) for value in values],
            "held": [list(value) for value in held], "first": differ[0] if differ else None}


def p2_vox_octant(bench) -> None:
    bench.start_cluster(nodes=["wa-1", NODE])
    bench.cluster.post("/automatic/pause")
    bench.record("config", bench.cluster.handover_config({"dormant.enabled": "false"}))
    storage = _node(bench, "wa-1", f"/voxel-test-spawn-asteroid?x={ORIGIN}&y={ORIGIN}&z={ORIGIN}"
                                   f"&radius={RADIUS}&seed=19", "POST")["name"]
    held = _try(lambda: wait_until(lambda: _try(lambda: _node(bench, NODE, f"/voxel-test-dump?storage={storage}"
                                                                    "&x=0&y=0&z=0&mx=0&my=0&mz=0")),
                                   180, "asteroid on node-1"))
    if not bench.check(f"setup: {NODE} holds the dispatched asteroid {storage}", bool(held)):
        raise ClusterError("node-1 never held the asteroid")
    # The spawn's radius is the vanilla asteroid size: radius=100 is a 128^3 storage (FindBestOctreeSize). Cubes on
    # the lattice and one flush with the far edge, every one inside it.
    size = held["size"]
    axis = sorted(set(range(0, size - CUBE + 1, STEP)) | {size - CUBE})
    cubes = [(x, y, z) for x in axis for y in axis for z in axis]
    outside = _try(lambda: _node(bench, NODE, f"/voxel-test-write?storage={storage}&x={size - CUBE + 8}&y=0&z=0"
                                              f"&size={CUBE}&content=1&material=1", "POST"), "refused")
    bench.check(f"a test write past the storage's edge ({size}) is refused", outside == "refused", str(outside)[:300])
    pristine = {cube: _dump(bench, NODE, storage, *_cube_box(cube)) for cube in cubes}
    wa_pristine = {cube: _dump(bench, "wa-1", storage, *_cube_box(cube)) for cube in cubes}
    bench.check("[INFO] setup: node-1 and the WA start on the same asteroid", True,
                json.dumps([list(c) for c in cubes if pristine[c] != wa_pristine[c]][:30]))

    written = {}
    with ThreadPoolExecutor(WRITERS) as pool:
        for index in range(PASSES):
            content, material = 40 + (index * 37) % 200, 1 + index % 5

            def write(cube):
                _node(bench, NODE, f"/voxel-test-write?storage={storage}&x={cube[0]}&y={cube[1]}&z={cube[2]}"
                                   f"&size={CUBE}&content={content}&material={material}", "POST")
                return cube

            for cube in pool.map(write, cubes):
                written[cube] = (content, material)
            time.sleep(1.5)

    def settled():
        status = _try(lambda: _node(bench, NODE, "/voxel-journal-status"), {}) or {}
        if status.get("outboundQueued") != 0 or status.get("dirtyRanges") != 0:
            return None
        journal = _node(bench, "wa-1", f"/voxel-journal-entries?storage={storage}", timeout=120)
        time.sleep(5)
        again = _node(bench, "wa-1", f"/voxel-journal-entries?storage={storage}", timeout=120)
        return again if len(again) == len(journal) and again else None

    journal = _try(lambda: wait_until(settled, 300, "node-1 published everything", interval=5))
    if not bench.check("setup: node-1's outbox and dirty ranges are empty and the WA's journal is stable", bool(journal)):
        raise ClusterError("node-1 never settled")

    origin = {cube: _dump(bench, NODE, storage, *_cube_box(cube)) for cube in cubes}
    wa = {cube: _dump(bench, "wa-1", storage, *_cube_box(cube)) for cube in cubes}
    not_written = {json.dumps(list(cube)): _compare((bytes([written[cube][0]]) * CUBE ** 3,
                                                     bytes([written[cube][1]]) * CUBE ** 3), origin[cube], pristine[cube])
                   for cube in cubes
                   if origin[cube] != (bytes([written[cube][0]]) * CUBE ** 3, bytes([written[cube][1]]) * CUBE ** 3)}
    differ = {cube: _compare(origin[cube], wa[cube], pristine[cube]) for cube in cubes if origin[cube] != wa[cube]}

    # Every range entry against what node-1 and the WA hold in its box now: the last entry of a box is the one the
    # replicas converge to, so it must carry node-1's content.
    rows = []
    for row in journal:
        if row.get("kind") != "Range":
            continue
        lo, hi = row["min"], row["max"]
        now_origin, now_wa = _hash(_dump(bench, NODE, storage, lo, hi)), _hash(_dump(bench, "wa-1", storage, lo, hi))
        rows.append({**row, "originNow": now_origin == row["hash"], "waNow": now_wa == row["hash"]})
    last_per_cube = {}
    for row in rows:
        for cube in cubes:
            if all(row["min"][axis] <= cube[axis] and cube[axis] + CUBE - 1 <= row["max"][axis] for axis in range(3)):
                last_per_cube[cube] = row
    bench.record("octant", {
        "storage": storage, "size": size, "cubes": len(cubes), "entries": len(journal),
        "pristineDiffers": [list(c) for c in cubes if pristine[c] != wa_pristine[c]],
        "originNotWritten": not_written,
        "waDiffers": {json.dumps(list(cube)): info for cube, info in differ.items()},
        "rangeEntries": rows,
        "lastEntryOfDifferingCube": {json.dumps(list(cube)): last_per_cube.get(cube) for cube in differ},
    })
    bench.check(f"node-1 holds its last write in every cube ({len(not_written)} of {len(cubes)} do not)",
                not not_written, json.dumps(not_written)[:1500])
    bench.check(f"[INFO] journal entries that carry what node-1 holds in their box now: "
                f"{sum(1 for row in rows if row['originNow'])} of {len(rows)}", True,
                json.dumps([row for row in rows if not row["originNow"]][:5]))
    bench.check(f"every cube on the WA matches node-1 ({len(differ)} of {len(cubes)} differ)",
                not differ, json.dumps({json.dumps(list(c)): i for c, i in list(differ.items())[:8]}))
    bench.exercised(cubes=len(cubes), passes=PASSES, writers=WRITERS, entries=len(journal), waDiffers=len(differ),
                    originNotWritten=len(not_written))


SCENARIOS_VOXEL_OCTANT = {
    "P2-VOX-OCTANT": (p2_vox_octant, "SE-1950: node-1's asteroid edits reach the WA with node-1's content in every octant", "2"),
}
