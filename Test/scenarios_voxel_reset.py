"""SE-1595: the World Authority's voxel reset (Shared/Cluster/VoxelResetService.cs).

P2-VOX-RESET edits three spots deep inside the EarthLike planet from node-1, 640 m apart:
  A with nothing near,
  B with a static grid 100 m away that stays online,
  C with a static grid 100 m away whose partition then goes offline.
With voxelReset.ageSeconds at 5 the WA must put A back to the generated content on every node (node-1, node-2
and itself) and leave B and C alone, also once both grids sit in offline partitions. Lowering
voxelReset.gridMeters to 50 must then reset B and C too, which shows the grids were what held them.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError, wait_until
from scenarios_core import _lines
from scenarios_planet import _earthlike, _hash, _node, _query, _write, planet_template

NODES = ["node-1", "node-2", "wa-1"]
SPACING = 640
CUBE = 64


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _hashes(bench, query: str) -> dict[str, str | None]:
    return {node: _hash(bench, node, query) for node in NODES}


def _offline_members(bench) -> set[int]:
    return {int(x) for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])
            for x in row.get("Members") or []}


def p2_vox_reset(bench) -> None:
    planet_template(bench)
    bench.start_cluster(nodes=NODES)
    bench.record("config-setup", bench.cluster.handover_config({"offline.enabled": "false", "voxelReset.enabled": "false"}))
    storage, center, size = _earthlike(bench)
    corner = [c - size / 2 for c in center]
    # Inside one 64-voxel cube (the edit's 1-voxel padding stays in it too), deep in the planet's rock.
    base = (size // 2 // CUBE) * CUBE + 30
    spots = {"A": (base, base, base), "B": (base + SPACING, base, base), "C": (base - SPACING, base, base)}
    queries = {k: _query(storage, x, y, z, 4) for k, (x, y, z) in spots.items()}
    pristine = {k: _hashes(bench, q) for k, q in queries.items()}
    bench.record("pristine", pristine)
    _require(bench, "setup: every node reads the same untouched voxels",
             all(len(set(h.values())) == 1 and None not in h.values() for h in pristine.values()), json.dumps(pristine))
    edited = {k: _write(bench, "node-1", q, 0, 1)["hash"] for k, q in queries.items()}

    def converged():
        return all(set(_hashes(bench, queries[k]).values()) == {edited[k]} for k in spots)

    _require(bench, "setup: the three edits reached node-2 and the WA", wait_until(converged, 120, "edits converged"))

    # Static grids 100 m past the +x face of B's and C's cubes.
    grids = {}
    for key in ("B", "C"):
        x, y, z = spots[key]
        face = corner[0] + (x // CUBE + 1) * CUBE
        spawned = _node(bench, "node-1", f"/trash-test-spawn?x={face + 100}&y={corner[1] + y}&z={corner[2] + z}"
                        f"&static=true&blocks=8", "POST")
        bench.record(f"grid-{key}", spawned)
        grids[key] = int(spawned["grid"])

    def occupancy():
        state = bench.cluster.get("/registry/voxel-reset-occupancy")
        return state if state.get("complete") and len(state.get("spheres") or []) >= 2 else None

    bench.record("occupancy", wait_until(occupancy, 120, "the Gateway reports a complete occupancy with the grids"))

    bench.record("config-reset", bench.cluster.handover_config({
        "offline.enabled": "false", "voxelReset.enabled": "true", "voxelReset.ageSeconds": "5",
        "voxelReset.scanSeconds": "2", "voxelReset.gridMeters": "300", "voxelReset.playerMeters": "1000"}))
    started = time.time()
    try:
        wait_until(lambda: set(_hashes(bench, queries["A"]).values()) == {pristine["A"]["node-1"]}, 90, "A reset")
        a_reset = time.time() - started
    except ClusterError:
        a_reset = None
    after_a = {k: _hashes(bench, q) for k, q in queries.items()}
    bench.record("after-a", {"seconds": a_reset, "hashes": after_a})
    bench.check("A (nothing near) is back to the generated voxels on node-1, node-2 and the WA", a_reset is not None,
                json.dumps(after_a["A"]))
    # A few more scans: B and C must stay edited while their grids are online.
    time.sleep(10)
    held = {k: _hashes(bench, queries[k]) for k in ("B", "C")}
    bench.check("B and C keep the edit while a grid is online 100 m away",
                all(set(held[k].values()) == {edited[k]} for k in held), json.dumps(held))

    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    try:
        wait_until(lambda: set(grids.values()) <= _offline_members(bench), 180, "both grids offline")
        offline = True
    except ClusterError:
        offline = False
    _require(bench, "setup: both grids went offline", offline, json.dumps(sorted(_offline_members(bench))))
    occupied = wait_until(occupancy, 60, "complete occupancy with the offline grids")
    bench.record("occupancy-offline", occupied)
    time.sleep(10)
    held = {k: _hashes(bench, queries[k]) for k in ("B", "C")}
    bench.check("B and C keep the edit while their grids are in offline partitions",
                all(set(held[k].values()) == {edited[k]} for k in held), json.dumps(held))

    bench.record("config-near", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0",
        "voxelReset.enabled": "true", "voxelReset.ageSeconds": "5", "voxelReset.scanSeconds": "2",
        "voxelReset.gridMeters": "50"}))
    try:
        wait_until(lambda: all(set(_hashes(bench, queries[k]).values()) == {pristine[k]["node-1"]} for k in ("B", "C")),
                   90, "B and C reset")
        released = True
    except ClusterError:
        released = False
    bench.check("with gridMeters 50 the grids 100 m away no longer hold B and C: both reset everywhere", released,
                json.dumps({k: _hashes(bench, queries[k]) for k in ("B", "C")}))
    lines = _lines(bench, "wa-1", r"voxel_reset ")
    bench.record("wa-voxel-reset-lines", lines[-20:])
    bench.check("the WA logged each reset", len([x for x in lines if "storage=" in x]) >= 3, "\n".join(lines[-5:]))
    bench.exercised(storage=storage, a_reset_seconds=a_reset)


SCENARIOS_VOXEL_RESET = {
    "P2-VOX-RESET": (p2_vox_reset, "SE-1595 voxel reset: unblocked, near online and offline grids", "3"),
}
