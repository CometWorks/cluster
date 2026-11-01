"""T-0368 bench: a modified asteroid the client holds is carried across a partition handover.

Two regular nodes + WA, Cluster-Simple with the live world's procedural asteroids, ONE headless client.
  1. the client spawns; the node that holds its partition is the SOURCE, the other one the TARGET.
  2. the nearest pristine PROCEDURAL asteroid is picked (same entity id on every node, as live; the
     /voxel-test-spawn-asteroid fixture gets a different id per node and can never be carried). The character is
     put ~400 m off its surface when it is further than 800 m. Both nodes keep the asteroid's cells generated
     (/voxel-test-track-point), so the target holds it before the client arrives.
  3. cut 1 (/voxel-test-cutout-live -> RequestVoxelCutoutSphere, an explosion's path) makes the asteroid
     replicated; once the source's replication server reports the client's replica delivered, cut 2 reaches the
     client as the vanilla event. Then both nodes' voxel journals must be settled on the same sequence and hold
     the same content in the cut range, different from the pristine range.
  4. handover source -> target, then target -> source (the Gateway's /handover route).
  5. checks per handover, from the nodes' MyLog files (<node>-data/SpaceEngineersDedicated_*.log):
       (a) no 'client_view_prune_extra client=<id> MyVoxelReplicable/<entity>' on the handover's source
       (b) 'client_view_carry_voxel client=<id> entity=<entity> ... remapped=True' on the target of the first
           handover, 'remapped=False' on the way back
       (c) no 'client_view_carry_voxel_fallback'
       (d) the client still holds the voxel: the target's replication server lists the client's replica as
           delivered under the SOURCE's network id, the client's own replication state lists that id, and the
           client's voxel list had the entity in every sample taken across the handover.
           The HOLE cannot be read on the client (the Remote API has no voxel content read); the bench asserts
           the hole on both nodes (range hash) and says so with an [INFO] line.
       (e) the Gateway logged the transfer committed and the registry names the target as owner.
T0368_EXPECT=red inverts (a) and (b) for a package WITHOUT the fix (df): prune_extra present, no carry line;
(d) is then recorded, not asserted. The fallback phase (a second viewer on the target) is not built.

Invocation: cd <tree>/Test && CLUSTER_PACKAGE=<pkg> PYTHONPATH=$PWD uv run python bench_t0368.py <label>
"""
import json
import math
import os
import re
import sys
import threading
import time
import urllib.parse
from pathlib import Path

from bench import run_scenario
from cluster import ClusterError, wait_until
from scenarios import _all_partitions
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _lines, _partitions
from scenarios_ext import _procedural_template

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
RED = os.environ.get("T0368_EXPECT", "green").lower() == "red"
OUT = Path(os.environ.get("T0368_OUT", "/home/factory/cluster-runs/regression/tools/t0368")) / f"bench-{LABEL}"
OUT.mkdir(parents=True, exist_ok=True)
SEARCH_RADIUS = float(os.environ.get("T0368_SEARCH", "6000"))    # where a pristine asteroid is looked for
MAX_SURFACE = float(os.environ.get("T0368_MAX_DISTANCE", "800"))  # further than this off the surface: teleport
STANDOFF = float(os.environ.get("T0368_STANDOFF", "400"))
CUT_RADIUS = float(os.environ.get("T0368_CUT_RADIUS", "3"))
NODES = ("node-1", "node-2")


def node(bench, name: str, path: str, method: str = "GET", timeout: float = 40):
    return bench.cluster.node(name, path, method=method, timeout=timeout)


def attempt(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def poll(predicate, timeout: float, interval: float = 1.0):
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def require(bench, label: str, passed, detail: str = "") -> None:
    """A precondition: everything after it depends on it, so a failure ends the run."""
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def mylog(bench, name: str, pattern: str) -> list[str]:
    """Lines of a node's MyLog files only: MyLog.Default lines never reach <node>.log."""
    regex = re.compile(pattern)
    return [line for path in sorted((bench.run_root / f"{name}-data").glob("SpaceEngineersDedicated_*.log"))
            for line in path.read_text(errors="replace").splitlines() if regex.search(line)]


def point(values) -> str:
    return "&".join(f"{axis}={value:.3f}" for axis, value in zip("xyz", values))


def asteroids(bench, name: str, where, radius: float) -> list[dict]:
    return (attempt(lambda: node(bench, name, f"/voxel-test-asteroids?{point(where)}&radius={radius}"), {}) or {}).get("asteroids") or []


def asteroid(bench, name: str, where, entity: int) -> dict | None:
    return next((row for row in asteroids(bench, name, where, 2000) if int(row["entityId"]) == entity), None)


def holds(row: dict | None) -> dict | None:
    """The client's entry in an asteroid row when its replica is delivered (neither pending nor streaming)."""
    for entry in (row or {}).get("clients") or []:
        if int(entry["client"]) == CLIENT_ID and not entry["pending"] and not entry["streaming"]:
            return entry
    return None


def journal(bench, name: str) -> dict:
    return attempt(lambda: node(bench, name, "/voxel-journal-status"), {}) or {}


def range_hash(bench, name: str, query: str) -> str | None:
    return (attempt(lambda: node(bench, name, f"/voxel-range-hash?{query}"), {}) or {}).get("hash")


def client_node(bench) -> str | None:
    return next((name for name in NODES if _client_partition(attempt(lambda: _partitions(bench, name), []) or []) is not None), None)


def client_position(api) -> list[float] | None:
    return (attempt(api.get_character) or {}).get("position")


class ClientWatch(threading.Thread):
    """Samples the client's own view across a handover: is the voxel entity in its voxel list, is the
    replicable's network id in its replication state. The flash T-0368 is about is a gap in either."""

    def __init__(self, api, entity: int, network_id: int, where):
        super().__init__(daemon=True)
        self.api, self.entity, self.network_id, self.where = api, entity, network_id, where
        self.stop = threading.Event()
        self.samples: list[tuple[float, bool | None, bool | None]] = []

    def sample(self) -> tuple[bool | None, bool | None]:
        maps = attempt(lambda: self.api.get_voxel_maps(*self.where, 2000))
        replication = attempt(self.api.get_replication)
        return (None if maps is None else any(int(row.get("entityId", 0)) == self.entity for row in maps),
                None if not replication or "networkIds" not in replication else self.network_id in replication["networkIds"])

    def run(self) -> None:
        started = time.time()
        while not self.stop.wait(0.2):
            self.samples.append((round(time.time() - started, 2), *self.sample()))

    def finish(self) -> dict:
        self.stop.set()
        self.join(30)
        entity = [seen for _, seen, _ in self.samples if seen is not None]
        network = [seen for _, _, seen in self.samples if seen is not None]
        return {"samples": len(self.samples), "entityObserved": len(entity), "entityMissing": entity.count(False),
                "networkIdObserved": len(network), "networkIdMissing": network.count(False), "trace": self.samples[:400]}


def settled(bench, storage: str, query: str, pristine: str, state: dict) -> bool:
    """Both nodes applied the same journal sequence, hold no own edit outside it, and agree on the cut range."""
    state.update({name: journal(bench, name) for name in NODES})
    state["hash"] = {name: range_hash(bench, name, query) for name in NODES}
    applied = [(state[name].get("applied") or {}).get(storage, 0) for name in NODES]
    quiet = all(state[name].get("liveSelfPending") == 0 and state[name].get("dirtyRanges") == 0
                and state[name].get("replaying") == 0 and state[name].get("outboundQueued") == 0 for name in NODES)
    hashes = set(state["hash"].values())
    return applied[0] > 0 and applied[0] == applied[1] and quiet and len(hashes) == 1 and None not in hashes and pristine not in hashes


def handover(bench, api, label: str, source: str, target: str, rock: dict, network_id: int, query: str, cut_hash: str,
             expect_remapped: bool) -> dict:
    entity, where = int(rock["entityId"]), rock["center"]
    partition = _client_partition(_partitions(bench, source))
    require(bench, f"{label}: the client's partition is on {source}", partition is not None)
    prune_pattern = rf"client_view_prune_extra client={CLIENT_ID} MyVoxelReplicable/{entity}\b"
    carry_pattern = rf"client_view_carry_voxel client={CLIENT_ID} entity={entity} "
    fallback_pattern = rf"client_view_carry_voxel_fallback client={CLIENT_ID} "
    skip_pattern = rf"client_view_carry_voxel_skip client={CLIENT_ID} "
    committed_pattern = rf"handover transferId=\S+ partition={partition} state=committed"
    before = {(name, pattern): len(mylog(bench, name, pattern)) for name in NODES
              for pattern in (prune_pattern, carry_pattern, fallback_pattern, skip_pattern)}
    committed_before = len(_lines(bench, "gateway", committed_pattern))
    new = lambda name, pattern: mylog(bench, name, pattern)[before[(name, pattern)]:]  # noqa: E731

    watch = ClientWatch(api, entity, network_id, where)
    watch.start()
    started = time.time()
    try:
        answer = bench.cluster.post(_handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300)
    except ClusterError as error:  # the verdict is check (e) below; the lines are still read
        answer = f"handover request failed: {error}"
        print(f"  [INFO] {label}: {answer[:300]}")
    bench.record(f"{label}-handover", answer)
    arrived = poll(lambda: _all_partitions(bench).get(partition) == target
                   and _client_partition(attempt(lambda: _partitions(bench, target), []) or []) is not None, 90)
    # The lines this handover is expected to write; in the other mode they must stay absent, so the wait is the proof.
    expected = (source, prune_pattern) if RED else (target, carry_pattern)
    poll(lambda: new(*expected), 30)
    time.sleep(10)
    view = watch.finish()
    bench.record(f"{label}-client-view", view)

    committed = _lines(bench, "gateway", committed_pattern)[committed_before:]
    bench.check(f"{label} (e): the Gateway committed the transfer and {target} owns the partition",
                bool(committed) and bool(arrived), f"{len(committed)} committed line(s), arrived={bool(arrived)}, "
                f"{time.time() - started:.0f} s")
    pruned, carried = new(source, prune_pattern), new(target, carry_pattern)
    fallbacks = [line for name in NODES for line in new(name, fallback_pattern)]
    skips = [line for name in NODES for line in new(name, skip_pattern)]
    strays = new(target, prune_pattern) + new(source, carry_pattern)
    bench.record(f"{label}-lines.txt", "\n".join(pruned + carried + fallbacks + skips + strays))
    if RED:
        bench.check(f"{label} (a, RED): {source} pruned the asteroid from the client's view", len(pruned) >= 1, "; ".join(pruned)[-300:])
        bench.check(f"{label} (b, RED): no carry line on either node", not carried and not strays, "; ".join(carried + strays)[-300:])
    else:
        bench.check(f"{label} (a): {source} did not prune the asteroid from the client's view", not pruned, "; ".join(pruned)[-300:])
        wanted = f"remapped={expect_remapped}"
        bench.check(f"{label} (b): {target} carried the asteroid, {wanted}",
                    len(carried) == 1 and wanted in carried[0], "; ".join(carried)[-300:] or f"no line; skips: {skips[-2:]}")
    bench.check(f"{label} (c): no carry fallback", not fallbacks, "; ".join(fallbacks)[-300:])

    # (d) the client still holds the voxel. In RED the target re-adds and streams it again: recorded, not asserted.
    row = poll(lambda: (lambda found: found if holds(found) else None)(asteroid(bench, target, where, entity)), 60)
    replication = attempt(api.get_replication) or {}
    listed = attempt(lambda: api.get_voxel_maps(*where, 2000))
    on_client = None if listed is None else any(int(found.get("entityId", 0)) == entity for found in listed)
    facts = {"targetRow": row or asteroid(bench, target, where, entity), "sourceNetworkId": network_id,
             "clientHasNetworkId": network_id in (replication.get("networkIds") or []), "clientListsEntity": on_client,
             "targetHash": range_hash(bench, target, query), "cutHash": cut_hash}
    bench.record(f"{label}-client-holds", facts)
    if RED:
        print(f"  [INFO] {label} (d, RED, not asserted): {json.dumps({k: v for k, v in facts.items() if k != 'targetRow'})} "
              f"entityMissing={view['entityMissing']}/{view['entityObserved']} networkIdMissing={view['networkIdMissing']}/{view['networkIdObserved']}")
    else:
        bench.check(f"{label} (d): {target} serves the client's replica under the source's network id {network_id}",
                    row is not None and int(row["networkId"]) == network_id, json.dumps(facts["targetRow"])[:300])
        bench.check(f"{label} (d): the client's replication state still lists network id {network_id}",
                    facts["clientHasNetworkId"] and view["networkIdMissing"] == 0,
                    f"after={facts['clientHasNetworkId']} missing {view['networkIdMissing']} of {view['networkIdObserved']} samples")
        bench.check(f"{label} (d): the client's voxel list had the asteroid in every sample across the handover",
                    on_client is True and view["entityObserved"] > 0 and view["entityMissing"] == 0,
                    f"after={on_client} missing {view['entityMissing']} of {view['entityObserved']} samples")
    bench.check(f"{label}: the hole is in {target}'s storage (range hash of the cut)", facts["targetHash"] == cut_hash,
                f"{facts['targetHash']} vs {cut_hash}")
    return {"partition": partition, "pruned": len(pruned), "carried": carried[-1:] or None, "fallbacks": len(fallbacks),
            "skips": skips, "entityMissing": view["entityMissing"], "networkIdMissing": view["networkIdMissing"]}


def check(bench):
    bench.cluster.config.world_template = _procedural_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # no balancer or co-location move besides the two forced handovers
    api = bench.start_client(CLIENT_ID, timeout=420, render=False)
    bench.wait_spawned(CLIENT_ID, timeout=300)
    source = poll(lambda: client_node(bench), 60)
    require(bench, "setup: the client's partition is on a regular node", source)
    target = next(name for name in NODES if name != source)
    position = poll(lambda: client_position(api), 30)
    require(bench, "setup: the client reports its character's position", position)

    # ---- the asteroid: procedural, pristine, the same entity on both nodes ----
    found = poll(lambda: [row for row in asteroids(bench, source, position, SEARCH_RADIUS)
                          if not row["save"] and not row["contentChanged"] and not row["clients"]], 90, interval=3)
    bench.record("00-asteroids-source", asteroids(bench, source, position, SEARCH_RADIUS))
    require(bench, f"setup: a pristine procedural asteroid within {SEARCH_RADIUS:.0f} m of the client on {source}", found,
            json.dumps(attempt(lambda: node(bench, source, "/test-voxel-census"), {})))
    rock = found[0]
    entity, storage, center = int(rock["entityId"]), rock["storage"], rock["center"]
    surface = rock["distance"] - rock["halfExtent"]
    if surface > MAX_SURFACE or surface < 50:
        direction = [p - c for p, c in zip(position, center)]
        length = math.sqrt(sum(d * d for d in direction)) or 1.0
        spot = [c + d / length * (rock["halfExtent"] + STANDOFF) for c, d in zip(center, direction)]
        bench.record("01-teleport", node(bench, source, f"/conceal-test-teleport?client={CLIENT_ID}&{point(spot)}", "POST"))
        moved = poll(lambda: (lambda at: at if at and math.dist(at, spot) < 150 else None)(client_position(api)), 60)
        require(bench, f"setup: the client's character stands {STANDOFF:.0f} m off the asteroid", moved,
                f"client reports {client_position(api)}, wanted {spot}")
        position = moved
        time.sleep(10)
        require(bench, f"setup: the client's partition is still on {source} after the teleport", client_node(bench) == source,
                str(client_node(bench)))
    for name in NODES:
        bench.record(f"02-track-{name}", node(bench, name, f"/voxel-test-track-point?{point(center)}&radius=1000", "POST"))
    twin = poll(lambda: asteroid(bench, target, center, entity), 90, interval=2)
    bench.record("03-asteroid", {"source": asteroid(bench, source, center, entity), "target": twin})
    require(bench, f"setup: {target} holds the same asteroid entity {entity}", twin and twin["storage"] == storage,
            json.dumps(asteroids(bench, target, center, 2000))[:400])

    # ---- the hole: cut 1 makes the asteroid replicated, cut 2 reaches the client that holds it ----
    cut_path = f"/voxel-test-cutout-live?entity={entity}&{point(position)}&radius={CUT_RADIUS}"
    probe = node(bench, source, cut_path + "&dry=1", "POST")
    voxel = [max(0, v - 2) for v in probe["voxel"]]
    query = f"storage={urllib.parse.quote(storage, safe='')}&x={voxel[0]}&y={voxel[1]}&z={voxel[2]}&size=4"
    pristine = {name: range_hash(bench, name, query) for name in NODES}
    require(bench, "setup: the pristine cut range is identical on both nodes",
            pristine[source] and pristine[source] == pristine[target], json.dumps(pristine))
    skips_before = {name: journal(bench, name).get("unknownStorageSkips") for name in NODES}
    bench.record("10-cut-1", node(bench, source, cut_path, "POST"))
    delivered = poll(lambda: (lambda row: row if holds(row) else None)(asteroid(bench, source, center, entity)), 120)
    require(bench, f"setup: {source}'s replication server reports the asteroid delivered to the client (cut 1)", delivered,
            json.dumps(asteroid(bench, source, center, entity))[:400])
    network_id = int(delivered["networkId"])
    require(bench, "setup: the client's own replication state lists the asteroid's network id",
            poll(lambda: network_id in ((attempt(api.get_replication) or {}).get("networkIds") or []), 30), str(network_id))
    bench.record("11-cut-2", node(bench, source, cut_path, "POST"))
    state: dict = {}
    quiet = poll(lambda: settled(bench, storage, query, pristine[source], state), 120, interval=2)
    time.sleep(5)
    quiet = quiet and settled(bench, storage, query, pristine[source], state)
    bench.record("12-journal-settled", state)
    require(bench, "setup: both journals settled on one sequence and both nodes hold the hole", quiet, json.dumps(state)[:500])
    cut_hash = state["hash"][source]
    skips = {name: journal(bench, name).get("unknownStorageSkips") for name in NODES}
    bench.check("setup: no journal entry was skipped for an unknown storage", skips == skips_before, f"{skips_before} -> {skips}")
    rows = {name: asteroid(bench, name, center, entity) for name in NODES}
    bench.record("13-before-handover", rows)
    require(bench, "setup: the client still holds the replica, and only on the source",
            holds(rows[source]) and not (rows[target] or {}).get("clients"), json.dumps(rows)[:500])
    print(f"  [INFO] asteroid {storage} entity={entity}: network id {rows[source]['networkId']} on {source}, "
          f"{rows[target]['networkId']} on {target} (equal ids make the first carry remapped=False)")
    print("  [INFO] the hole is asserted on the nodes only: the Remote API cannot read voxel content on the client")

    # ---- the two handovers ----
    there = handover(bench, api, "handover-1", source, target, rock, network_id, query, cut_hash, expect_remapped=True)
    state = {}
    bench.check("between: the journals are settled before the way back",
                bool(poll(lambda: settled(bench, storage, query, pristine[source], state), 60, interval=2)), json.dumps(state)[:400])
    back = handover(bench, api, "handover-2", target, source, rock, network_id, query, cut_hash, expect_remapped=False)
    bench.check("the client's character is alive at the end", bench.living_character(CLIENT_ID) is not None)
    bench.exercised(expect="red" if RED else "green", source=source, target=target, asteroid=storage, entity=entity,
                    networkId=network_id, surfaceDistance=round(surface, 1), there=there, back=back)


check.regular_nodes = 2
result = run_scenario(check, "T0368-voxel-carry", "a modified asteroid the client holds is carried across a handover"
                      + (" (RED baseline: no fix)" if RED else ""), "3", OUT)
(OUT / "verdict.json").write_text(json.dumps(vars(result), default=lambda x: vars(x), indent=2))
raise SystemExit(0 if result.passed else 1)
