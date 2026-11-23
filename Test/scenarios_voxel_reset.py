"""SE-1595: the World Authority's voxel reset (Shared/Cluster/VoxelResetService.cs).

P2-VOX-RESET edits three spots deep inside the EarthLike planet from node-1, 640 m apart:
  A with nothing near,
  B with a static grid 100 m away that stays online,
  C with a static grid 100 m away whose partition then goes offline.
With voxelReset.ageSeconds at 5 the WA must put A back to the generated content on every node (node-1, node-2
and itself) and leave B and C alone, also once both grids sit in offline partitions. Lowering
voxelReset.gridMeters to 50 must then reset B and C too, which shows the grids were what held them.

P2-VOX-RESET-MEMBERLESS (SE-1706) is the live Octahedron state: a partition whose only grid was closed the way trash
or MES closes one keeps its registry row and lease on its node until the node drains, but the node no longer reports
it. Before the fix the Gateway's occupancy stayed incomplete over that row and the WA reset nothing, ever. The test
edits spot A deep in EarthLike, spot B 1280 m from it, and a WA-spawned asteroid far out; grid E sits 100 m past B and
is then closed. With the fix the occupancy is complete, judges E's row by the sphere E was last reported with, and the
WA resets A and the whole asteroid on every node and keeps B until voxelReset.gridMeters drops to 50.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError, wait_until
from scenarios_core import _lines
from scenarios_grid_delete import _capture, _newest_entities
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


def _registry_rows(bench) -> dict[int, dict]:
    return {int(row["Partition"]): row for row in bench.cluster.get("/registry/partitions")}


def _node_partitions(bench, node: str) -> dict[int, list[int]]:
    """partition id -> member ids, as the node reports them."""
    return {int(row["id"]): [int(m["id"]) for m in (row.get("members") or [])]
            for row in _node(bench, node, "/partitions")["partitions"]}


def p2_vox_reset_memberless(bench) -> None:
    planet_template(bench)
    bench.start_cluster(nodes=NODES)
    bench.record("config-setup", bench.cluster.handover_config({"offline.enabled": "false", "voxelReset.enabled": "false"}))
    storage, center, size = _earthlike(bench)
    corner = [c - size / 2 for c in center]
    base = (size // 2 // CUBE) * CUBE + 30
    spots = {"A": (base, base, base), "B": (base + 2 * SPACING, base, base)}
    queries = {k: _query(storage, x, y, z, 4) for k, (x, y, z) in spots.items()}

    # A WA-dispatched asteroid far from the planet: every role materializes the same one.
    spawn = _node(bench, "wa-1", "/voxel-test-spawn-asteroid?x=2000000&y=2000000&z=2000000&radius=60&seed=7", "POST")
    bench.record("asteroid", spawn)
    asteroid = spawn.get("name")
    _require(bench, "setup: the asteroid spawn dispatch succeeded", asteroid, json.dumps(spawn))
    queries["rock"] = _query(asteroid, 40, 40, 40, 4)
    _require(bench, "setup: the asteroid materialized identically on every role",
             wait_until(lambda: (lambda h: None not in h.values() and len(set(h.values())) == 1)(_hashes(bench, queries["rock"])),
                        120, "asteroid everywhere"), json.dumps(_hashes(bench, queries["rock"])))
    pristine = {k: _hashes(bench, q) for k, q in queries.items()}
    bench.record("pristine", pristine)
    _require(bench, "setup: every node reads the same untouched voxels",
             all(len(set(h.values())) == 1 and None not in h.values() for h in pristine.values()), json.dumps(pristine))
    edited = {k: _write(bench, "node-1", q, 0, 1)["hash"] for k, q in queries.items()}
    _require(bench, "setup: the edits reached node-2 and the WA",
             wait_until(lambda: all(set(_hashes(bench, queries[k]).values()) == {edited[k]} for k in queries), 120,
                        "edits converged"))

    # Grid E, alone in its partition 100 m past the +x face of B's cube, with a catalogued save holding it.
    x, y, z = spots["B"]
    face = corner[0] + (x // CUBE + 1) * CUBE
    spawned = _node(bench, "node-1", f"/trash-test-spawn?x={face + 100}&y={corner[1] + y}&z={corner[2] + z}"
                    "&static=true&blocks=8", "POST")
    bench.record("grid-e", spawned)
    grid = int(spawned["grid"])
    home = wait_until(lambda: next((p for p, members in _node_partitions(bench, "node-1").items() if grid in members), None),
                      120, "E in a node-1 partition")
    _capture(bench, home)
    _require(bench, "setup: E's partition has a catalogued save holding it",
             wait_until(lambda: (_newest_entities(bench, home) or 0) >= 1 or None, 90, "a save of E"))

    def occupancy(predicate):
        def ready():
            state = bench.cluster.get("/registry/voxel-reset-occupancy")
            return state if predicate(state) else None
        return ready

    bench.record("occupancy-before-close",
                 wait_until(occupancy(lambda s: s.get("complete")), 120, "a complete occupancy with E reported"))

    # The live state: E closed the way trash, MES or combat closes a grid. Its row and lease stay on node-1, which
    # no longer lists the partition.
    bench.record("close-e", _node(bench, "node-1", f"/test-close-entity?entity={grid}", "POST"))
    try:
        wait_until(lambda: home not in _node_partitions(bench, "node-1")
                   and _registry_rows(bench).get(home, {}).get("Node") == "node-1", 60, "E's row memberless on node-1")
        memberless = True
    except ClusterError:
        memberless = False
    row = _registry_rows(bench).get(home)
    bench.record("memberless-row", {"partition": home, "row": row})
    _require(bench, "setup: E's partition keeps its node-1 row and lease while node-1 no longer reports it", memberless,
             json.dumps(row))

    bench.record("config-reset", bench.cluster.handover_config({
        "offline.enabled": "false", "voxelReset.enabled": "true", "voxelReset.ageSeconds": "5",
        "voxelReset.scanSeconds": "2", "voxelReset.gridMeters": "300", "voxelReset.playerMeters": "1000"}))
    try:
        bench.record("config-status", bench.cluster.handover_config({"voxelReset.statusSeconds": "5"}))
    except ClusterError as error:  # a build before SE-1706 has no such knob
        bench.record("config-status", str(error))
    started = time.time()
    try:
        wait_until(lambda: all(set(_hashes(bench, queries[k]).values()) == {pristine[k]["node-1"]} for k in ("A", "rock")),
                   120, "A and the asteroid reset")
        reset = round(time.time() - started, 1)
    except ClusterError:
        reset = None
    state = bench.cluster.get("/registry/voxel-reset-occupancy")
    after = {k: _hashes(bench, q) for k, q in queries.items()}
    bench.record("after-reset", {"seconds": reset, "hashes": after,
                                 "occupancy": {k: v for k, v in state.items() if k != "spheres"}})
    bench.check("the occupancy is complete although E's row is memberless", state.get("complete") is True,
                json.dumps({k: v for k, v in state.items() if k != "spheres"}))
    bench.check("E's memberless row is judged by the sphere E was last reported with", state.get("remembered", 0) >= 1,
                json.dumps({k: v for k, v in state.items() if k != "spheres"}))
    bench.check("A (nothing near) is back to the generated voxels on node-1, node-2 and the WA",
                set(after["A"].values()) == {pristine["A"]["node-1"]}, json.dumps(after["A"]))
    bench.check("the whole asteroid is back to the generated voxels on node-1, node-2 and the WA",
                set(after["rock"].values()) == {pristine["rock"]["node-1"]}, json.dumps(after["rock"]))
    published = {}

    def listed():
        published.update(bench.cluster.get("/registry/voxel-resets"))
        return published.get(asteroid, 0) >= int(started) - 1

    try:
        wait_until(listed, 30, "the asteroid's reset published")
    except ClusterError:
        pass
    bench.record("voxel-resets", published)
    bench.check("GET /registry/voxel-resets lists the asteroid's reset time (unix seconds)",
                published.get(asteroid, 0) >= int(started) - 1 and published.get(asteroid, 0) <= time.time() + 5,
                json.dumps(published))
    time.sleep(10)
    held = _hashes(bench, queries["B"])
    bench.check("B keeps the edit: E's save could bring it back 100 m away", set(held.values()) == {edited["B"]},
                json.dumps(held))

    bench.record("config-near", bench.cluster.handover_config({
        "offline.enabled": "false", "voxelReset.enabled": "true", "voxelReset.ageSeconds": "5",
        "voxelReset.scanSeconds": "2", "voxelReset.gridMeters": "50"}))
    try:
        wait_until(lambda: set(_hashes(bench, queries["B"]).values()) == {pristine["B"]["node-1"]}, 90, "B reset")
        released = True
    except ClusterError:
        released = False
    bench.check("with gridMeters 50, E's last sphere 100 m away no longer holds B: it resets everywhere", released,
                json.dumps(_hashes(bench, queries["B"])))
    lines = _lines(bench, "wa-1", r"voxel_reset ")
    bench.record("wa-voxel-reset-lines", lines[-30:])
    bench.check("the WA logged the resets of A, the asteroid and B",
                any(asteroid in x and "storage=" in x for x in lines) and len([x for x in lines if "storage=" in x]) >= 3,
                "\n".join(lines[-5:]))
    bench.check("the WA logged periodic voxel_reset status lines", len([x for x in lines if "voxel_reset status" in x]) >= 2,
                "\n".join(lines[-5:]))
    bench.exercised(storage=storage, asteroid=asteroid, memberless_partition=home, reset_seconds=reset)


SCENARIOS_VOXEL_RESET = {
    "P2-VOX-RESET": (p2_vox_reset, "SE-1595 voxel reset: unblocked, near online and offline grids", "3"),
    "P2-VOX-RESET-MEMBERLESS": (p2_vox_reset_memberless,
                                "SE-1706 voxel reset past a memberless partition row, planet cube and asteroid", "3"),
}
