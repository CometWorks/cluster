"""Cluster snapshot, shutdown and restart: the former run-cluster.sh lanes `online-snapshot`,
`clean-shutdown`, `warm-restart`, `poison-warm`, `poison` and `rebuild`, as native scenarios.

The first four share one body, each lane going one step further:

* ``P2-SAV-01a``  - an online lineage-cover snapshot: a fresh WA global save and a fresh save of every
  owned partition, cut while the client stays connected; every referenced MPS verifies;
* ``P2-SAV-01b``  - then a graceful cluster shutdown: Final saves, a clean GlobalRef persisted in the
  registry checkpoint, and every process (Gateway included) exits;
* ``P2-SAV-01c``  - then a warm restart from the clean marker on fresh data roots: the registry
  classifies it Warm, the WA restores the global checkpoint clock, every Final loads exactly once,
  and the client rejoins its one body through the durable character home;
* ``P2-REC-02w``  - the warm restart with one of node-1's staged Finals poisoned: it is quarantined
  instead of killing the node, and repaired in place from an uploaded pristine copy.

And two lanes of their own:

* ``P2-REC-02``   - node-2's seed save of partition 2 is poisoned before the boot; node-2 quarantines
  it, the cluster keeps serving, and an exported + edited + re-imported save repairs it in place
  (also asserts P1-REC-08);
* ``P2-REC-04``   - the registry is destroyed after a clean shutdown and rebuilt from the save headers
  scanned off the node data roots; the cluster recovers warm and both lineages continue.

Product failures found by these lanes (2026-09-17) and fixed the same day: drained nodes exited
137 because the LinuxCompat plugin turns ExitThreadSafe into Process.Kill (T-0162), and a
warm-restarted node never claimed its recovery while a retention delete directive pointed into
its previous data root (T-0163). Bench run 20260917-105618Z passed all three with both fixes.

The restarts run every process on a fresh data root (``<node>-data-restart``) beside the retained
one that holds the Final saves the restarted nodes are pointed at (CLUSTER_MPS_PATHS).
"""

from __future__ import annotations

import json
import random
import re
import shutil
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _join, _lines, _nodes, _owners, \
    _partitions, _wait_line
from scenarios_recovery import RECOVERY_TIMEOUT, _raw

#: The bash lanes' SERVER_TIMEOUT: the budget for a (re)started process to reach Game ready. It also
#: bounds how far the warm WA clock may run past the restored checkpoint. The bash lane used 180 s
#: against the recompiled server; Magnetar nodes load slower, so this is the CLI's wait_ready budget.
SERVER_TIMEOUT = 300
POISON_BYTES = b"CORRUPTED!"
POISON_OFFSET = 120
#: The poison lane's end-beacon grid (the battery the repair edits) in the Cluster-Simple world.
POISON_BEACON_ENTITY = "74187275373669233"
REGULAR = ("node-1", "node-2")
PROCESSES = ("node-1", "node-2", "wa-1", "gateway")


# -- shared plumbing ------------------------------------------------------------------------


def _tool(bench, *args: str) -> subprocess.CompletedProcess:
    """MagnetarWorld with the game assemblies it resolves (the CLI's tool environment)."""
    return subprocess.run(["dotnet", str(bench.cluster.config.world_tool), *args], capture_output=True, text=True,
                          timeout=300, env=bench.cluster._tool_env())


def _tool_json(bench, *args: str) -> dict:
    result = _tool(bench, *args)
    if result.returncode != 0:
        raise ClusterError(f"MagnetarWorld {args[0]} failed: {(result.stdout + result.stderr)[-400:]}")
    return json.loads(result.stdout)


def _verifies(bench, path) -> bool:
    return _tool(bench, "verify-mps", str(path)).returncode == 0


def _entity_ids(bench, path) -> list[int]:
    return _tool_json(bench, "verify-mps", str(path))["entityIds"]


def _wait_file(path: str, label: str, timeout: float = 90) -> None:
    # A location from the registry means CATALOGUED, not yet written: the file lands a moment later.
    # Bounded on purpose - a catalogued save that never appears is a real defect, not a race.
    wait_until(lambda: Path(path).is_file(), timeout, f"{label}: {path}")


def _values(collection) -> list:
    """jq's `.X[]` iterates an array or an object's values alike."""
    return list(collection.values()) if isinstance(collection, dict) else list(collection or [])


def _try(call, default=None):
    try:
        return call()
    except (ClusterError, OSError, ValueError):
        return default


def _retry_post(bench, node_id: str, path: str, timeout: float = HANDOVER_TIMEOUT) -> bool:
    """The lanes' `for ...; do curl -X POST ... && break; sleep 1; done`: a busy game thread may
    refuse a capture request for a moment."""
    def accepted():
        try:
            bench.cluster.node(node_id, path, method="POST")
            return True
        except ClusterError:
            return False

    try:
        wait_until(accepted, timeout, f"{node_id} to accept POST {path}")
        return True
    except ClusterError:
        return False


def _sequence(save) -> int:
    if not isinstance(save, dict):
        return 0
    return (save.get("SaveId") or {}).get("Sequence") or 0


def _newest(bench, path: str):
    return _try(lambda: bench.cluster.get(path))


def _post_bytes(url: str, data: bytes) -> dict:
    request = urllib.request.Request(url, method="POST", data=data)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.loads(response.read().decode())
    except urllib.error.HTTPError as error:
        raise ClusterError(f"POST {url} -> {error.code}: {error.read().decode(errors='replace')[:400]}") from None


def _corrupt(path: Path) -> None:
    """Body corruption behind an intact header: the checksum fails, the partition id stays readable."""
    with open(path, "r+b") as handle:
        handle.seek(POISON_OFFSET)
        handle.write(POISON_BYTES)


REGISTRY_MAGIC = 0x32524743  # "CGR2"


def _registry_records(data: bytes) -> list[dict]:
    """Decode a Gateway registry file: records of a 12-byte header (uint32 magic "CGR2", int32
    payload length, uint32 CRC32) followed by the raw-deflated JSON state. Plain JSON (an older
    Gateway) is accepted as one record."""
    if data[:1] in (b"{", b"["):
        return [json.loads(data)]
    records, offset = [], 0
    while offset + 12 <= len(data):
        magic, length, crc = struct.unpack_from("<IiI", data, offset)
        payload = data[offset + 12:offset + 12 + length]
        if magic != REGISTRY_MAGIC or length < 0 or len(payload) != length or zlib.crc32(payload) != crc:
            break
        records.append(json.loads(zlib.decompress(payload, -15)))
        offset += 12 + length
    return records


def _read_registry_state(directory: Path) -> dict:
    """The registry state on disk: the snapshot, superseded by the last WAL record (every WAL
    record is a full state)."""
    records = _registry_records((directory / "registry.json").read_bytes())
    wal = directory / "registry.wal"
    if wal.is_file():
        records += _registry_records(wal.read_bytes())
    if not records:
        raise ClusterError(f"no decodable registry state in {directory}")
    return records[-1]


def _alive(bench, name: str) -> bool:
    process = bench.cluster.processes.get(name)
    return process is not None and process.poll() is None


def _preserve_logs(bench, suffix: str) -> dict[str, Path]:
    """The next spawn rotates each process log aside, and bench.close copies only the live ones:
    keep the first incarnation's logs as evidence."""
    kept = {}
    for name in PROCESSES:
        source = bench.run_root / f"{name}.log"
        if source.is_file():
            target = bench.dir / f"{name}-{suffix}.log"
            target.write_bytes(source.read_bytes()[-2_000_000:])
            bench.result.artifacts.append(target.name)
            kept[name] = target
    return kept


def _graceful_shutdown(bench, record: str) -> dict:
    """Graceful drain: every partition writes a Final save, the WA registers a final global
    snapshot, and every process (Gateway included) exits."""
    shutdown = bench.cluster.post(f"/registry/shutdown/graceful?timeoutSeconds={RECOVERY_TIMEOUT}&forceAfterSeconds=0",
                                  timeout=RECOVERY_TIMEOUT + 30)
    bench.record(record, shutdown)
    last = (shutdown or {}).get("LastShutdown") or {}
    if not (shutdown.get("Phase") == 4 and last.get("Dirty") == [] and last.get("GlobalRef") is not None):
        raise ClusterError(f"graceful shutdown did not produce a clean global snapshot: {json.dumps(shutdown)[:400]}")
    return shutdown


def _wait_all_exited(bench, label: str) -> None:
    try:
        wait_until(lambda: not any(_alive(bench, name) for name in PROCESSES), RECOVERY_TIMEOUT,
                   "every cluster process to exit")
    except ClusterError:
        pass
    alive = [name for name in PROCESSES if _alive(bench, name)]
    for name in PROCESSES:
        bench.check(f"{name} exited {label}", name not in alive)
    if alive:
        # Nothing after this can run: the restart needs the ports and the data roots.
        raise ClusterError(f"still running {label}: {', '.join(alive)}")


def _use_restart_data_roots(bench) -> None:
    """Every restarted process gets a fresh data root (the lanes' <node>-restart-data), and the
    retained roots keep the Final saves the restart is pointed at. `<id>-data-restart` still matches
    Cluster.kill(slot)'s `<root>/<id>-data` pattern; prepare() does not know it, so wipe it here."""
    config = bench.cluster.config
    root = config.root
    for node in config.nodes:
        shutil.rmtree(root / f"{node.node_id}-data-restart", ignore_errors=True)
    config.node_data = lambda node: root / f"{node.node_id}-data-restart"


def _start_gateway(bench) -> None:
    """The Gateway alone, on the retained (warm) or wiped (rebuild) registry directory."""
    bench.cluster.start(nodes=[])
    bench.cluster.wait_node_log("gateway", "Gateway listening", timeout=SERVER_TIMEOUT)


def _start_restarted(bench, order: list[str], env: dict[str, dict[str, str]], wa_world: Path | None = None) -> None:
    config = bench.cluster.config
    for node_id, extra in env.items():
        config.node_env_by_id[node_id] = {**config.node_env_by_id.get(node_id, {}), **extra}
    if wa_world is not None:
        # The WA loads the warm world (its checkpoint rewritten from the Final global save) as its
        # premade world; CLUSTER_WORLD stays the split world, as in the shell lane.
        wa = next(node for node in config.nodes if node.node_id == "wa-1")
        config.node_data(wa).mkdir(parents=True, exist_ok=True)
        bench.cluster.seed_dedicated_config(wa)
        cfg = config.node_data(wa) / "SpaceEngineers-Dedicated.cfg"
        text = cfg.read_text()
        seeded = f"<PremadeCheckpointPath>{config.premade_world(wa)}</PremadeCheckpointPath>"
        if seeded not in text:
            raise ClusterError(f"the seeded WA config names no per-node premade path: {cfg}")
        cfg.write_text(text.replace(seeded, f"<PremadeCheckpointPath>{wa_world}</PremadeCheckpointPath>"))
    for node_id in order:
        bench.cluster.start_node(node_id)


def _join_poisoned(bench, poison) -> object:
    """_join with a step between the world split and the process start."""
    nodes = ["node-1", "node-2", "wa-1"]
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    poison()
    bench.cluster.start(nodes)
    bench.cluster.wait_ready(nodes=nodes)
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    return api


def _reconnect_client(bench, partition: int, initial_gateway_log: Path | None) -> None:
    """The warm-restart rejoin. The first Gateway's log is final (that process exited), so the
    disconnect is read from its preserved copy; everything after it from the live logs."""
    node2 = _nodes(bench)["node-2"]
    text = initial_gateway_log.read_text(errors="replace") if initial_gateway_log and initial_gateway_log.is_file() else ""
    bench.check("the initial Gateway logged the client's disconnect",
                re.search(rf"Client {CLIENT_ID} disconnected", text) is not None)
    bench.kill_client(CLIENT_ID)
    bench.start_client(CLIENT_ID, label=f"{CLIENT_ID}-reconnect")
    for label, name, pattern in (
            ("the Gateway resolved the recovered route to node-2", "gateway",
             rf"Resolved client {CLIENT_ID} partition {partition} to node-2 .* at {re.escape(node2.backend)}"),
            ("the Gateway connected the recovered backend", "gateway",
             rf"Connected backend {re.escape(node2.backend)} as client {CLIENT_ID}"),
            ("node-2 accepted the recovered client", "node-2", rf"Accepted client {CLIENT_ID} through cluster node link")):
        try:
            _wait_line(bench, name, pattern, 180)
            bench.check(label, True)
        except ClusterError as error:
            bench.check(label, False, str(error))
    bench.check("the reconnected client recovered a living character", bool(bench.wait_spawned(CLIENT_ID)))


def _any_log(bench, pattern: str, names=("node-1", "node-2", "wa-1")) -> list[str]:
    return [line for name in names for line in _lines(bench, name, pattern)]


# -- the shared snapshot / shutdown / restart body -------------------------------------------


def _snapshot_lane(bench, lane: str) -> None:
    _join(bench)
    authority = bench.cluster.get("/registry/world-authority")
    global_generation = authority.get("Generation") or 0
    if global_generation <= 0:
        raise ClusterError("online snapshot has no World Authority generation")
    global_before = _sequence(_newest(bench, "/registry/global-saves/newest"))
    _retry_post(bench, "wa-1", "/global-save-capture")
    newest_global = None

    def fresh_global():
        nonlocal newest_global
        newest_global = _newest(bench, "/registry/global-saves/newest")
        return _sequence(newest_global) > global_before

    try:
        wait_until(fresh_global, HANDOVER_TIMEOUT, "a fresh global save")
    except ClusterError:
        raise ClusterError("World Authority did not produce a fresh global save") from None
    bench.check("the global save generation matches the live World Authority",
                newest_global["SaveId"].get("Generation") == global_generation,
                f"{newest_global['SaveId']} vs generation {global_generation}")

    owners = _owners(bench)
    owned = [row for row in owners if row.get("Node") is not None]
    expected = len(owned)
    if expected == 0:
        raise ClusterError("online snapshot has no owned partitions")
    for row in owned:
        partition, node = row["Partition"], row["Node"]
        if node not in REGULAR:
            raise ClusterError(f"online snapshot has unsupported owner {node}")
        before = _sequence(_newest(bench, f"/registry/saves/newest?partition={partition}"))
        _retry_post(bench, node, f"/save-capture?partition={partition}")
        try:
            wait_until(lambda: _sequence(_newest(bench, f"/registry/saves/newest?partition={partition}")) > before,
                       HANDOVER_TIMEOUT, f"a fresh snapshot save of partition {partition}")
            fresh = True
        except ClusterError:
            fresh = False
        bench.check(f"partition {partition} produced a fresh snapshot save", fresh)

    cluster_time = int((time.time() - bench.cluster.spawned_at.get("gateway", time.time())) * 1000)
    snapshot = bench.cluster.post(f"/registry/snapshots/online?id=p2-sav-01-online&clusterTime={cluster_time}")
    bench.record("online-snapshot", snapshot)
    bench.record("global-save", newest_global)
    refs = snapshot.get("PartitionRefs") or []
    bench.check("the online snapshot is a complete, clean lineage cover",
                snapshot.get("Kind") == 1 and not snapshot.get("Dirty") and snapshot.get("GlobalRef") == newest_global["SaveId"]
                and len({ref["Partition"] for ref in refs}) == expected,
                json.dumps(snapshot)[:400])
    for save in bench.cluster.get("/registry/saves"):
        if save["SaveId"] in refs:
            for location in save.get("Locations") or []:
                _wait_file(location, "online snapshot save is missing")
                bench.check(f"online snapshot save verifies: {location}", _verifies(bench, location))
    # One line per REPLICA: `.Locations[]` yields the WA's copy and each node's, and testing the whole
    # list as if it were a single path is a check that cannot pass the moment a global save has more
    # than one holder - which is always.
    global_locations = [location for save in bench.cluster.get("/registry/global-saves")
                        if save["SaveId"] == snapshot.get("GlobalRef") for location in save.get("Locations") or []]
    bench.check("the catalogued global save reports locations", bool(global_locations))
    for location in global_locations:
        _wait_file(location, "online snapshot global save is missing")
        bench.check(f"online snapshot global save verifies: {location}", _verifies(bench, location))
    bench.screenshot(CLIENT_ID, "online-client")
    bench.exercised(snapshotPartitions=expected, globalSave=newest_global["SaveId"], lane=lane)
    if lane == "online-snapshot":
        return

    partition = None
    if lane in ("warm-restart", "poison-warm"):
        partition = _client_partition(_partitions(bench, "node-1"))
        if partition is None:
            raise ClusterError("warm shutdown could not identify the client partition")
    shutdown = _graceful_shutdown(bench, "clean-shutdown")
    try:
        # The WA holds its exit up to a 90 s peer-drain grace (T-0048), and its peer check asks the
        # Gateway, which the graceful shutdown stops first - so the grace always runs out (T-0162).
        _wait_line(bench, "wa-1", r"global final registered; World Authority Empty", RECOVERY_TIMEOUT + 60)
        bench.check("the WA registered its global final and went Empty", True)
    except ClusterError as error:
        bench.check("the WA registered its global final and went Empty", False, str(error))
    _wait_all_exited(bench, "after the clean shutdown")
    for name in PROCESSES:
        code = bench.cluster.processes[name].returncode
        bench.check(f"{name} clean shutdown returned success", code == 0, f"exit {code}")
    initial_logs = _preserve_logs(bench, "initial")

    registry_state = _read_registry_state(bench.run_root / "gateway-registry")
    ref = shutdown["LastShutdown"]["GlobalRef"]
    clean = registry_state.get("CleanShutdown") or {}
    bench.check("the registry checkpoint carries the clean global reference",
                clean.get("Dirty") == [] and clean.get("GlobalRef") == ref
                and any(s.get("Kind") == 0 and s.get("GlobalRef") == ref and s.get("Dirty") == []
                        for s in _values(registry_state.get("Snapshots"))),
                json.dumps(clean)[:300])
    final_global = next((save for save in _values(registry_state.get("GlobalSaves"))
                         if save["SaveId"] == ref and save.get("Kind") == 2), None)
    if final_global is None:
        raise ClusterError("registry checkpoint omitted the Final global save")
    final_global_location = final_global["Locations"][0]
    bench.check("the Final global save verifies", _verifies(bench, final_global_location), final_global_location)
    bench.record("final-global-save", final_global)
    bench.exercised(finalGlobal=ref, manifest=len(shutdown["LastShutdown"].get("Manifest") or []))
    if lane == "clean-shutdown":
        return

    _warm_restart(bench, lane, partition, shutdown, registry_state, final_global_location, initial_logs.get("gateway"))


def _warm_restart(bench, lane: str, partition: int, shutdown: dict, registry_state: dict,
                  final_global_location: str, initial_gateway_log: Path | None) -> None:
    config = bench.cluster.config
    bench.kill_client(CLIENT_ID)
    warm_world = bench.run_root / "wa-warm-world"
    shutil.rmtree(warm_world, ignore_errors=True)
    shutil.copytree(config.split_world, warm_world)
    global_checkpoint = _tool_json(bench, "global-to-checkpoint", final_global_location, str(warm_world / "Sandbox.sbc"))
    bench.record("global-checkpoint", global_checkpoint)
    manifest = shutdown["LastShutdown"]["Manifest"]
    finals = [save for save in _values(registry_state.get("Saves")) if save["SaveId"] in manifest]
    if not finals:
        raise ClusterError("warm restart has no Final partition paths")
    node2_paths = [save["Locations"][0] for save in finals if save["SaveId"]["Partition"] == partition]
    node1_paths = [save["Locations"][0] for save in finals if save["SaveId"]["Partition"] != partition]
    if not node1_paths or not node2_paths:
        raise ClusterError("warm restart could not split Final saves between nodes")
    for path in node1_paths + node2_paths:
        bench.check(f"staged Final verifies: {path}", _verifies(bench, path))
    poison_partition = pristine = None
    if lane == "poison-warm":
        # P2-REC-03 warm branch: poison ONE of node-1's staged Finals (body corruption behind a valid
        # header). The warm node must quarantine it after its deterministic claim failures instead of
        # dying at boot or reclaiming forever, recover everything else, and accept the in-place
        # repair from the pristine copy.
        poison_path = node1_paths[0]
        poison_partition = next(save["SaveId"]["Partition"] for save in _values(registry_state.get("Saves"))
                                if save["Locations"][0] == poison_path)
        pristine = bench.dir / "poison-warm-pristine.mps"
        shutil.copy2(poison_path, pristine)
        _corrupt(Path(poison_path))
        if _verifies(bench, poison_path):
            raise ClusterError("the poisoned warm save still verifies")

    _use_restart_data_roots(bench)
    _start_gateway(bench)
    warm_status = bench.cluster.get("/registry/status")
    bench.record("warm-status", warm_status)
    lifecycle = warm_status.get("lifecycle") or {}
    bench.check("the registry classified the clean snapshot as Warm",
                lifecycle.get("Startup") == 0 and (lifecycle.get("LastShutdown") or {}).get("Dirty") == []
                and (lifecycle.get("LastShutdown") or {}).get("GlobalRef") == shutdown["LastShutdown"]["GlobalRef"],
                json.dumps(lifecycle)[:300])

    warm = {"CLUSTER_WARM_RECOVERY": "1"}
    _start_restarted(bench, ["node-2", "node-1", "wa-1"],
                     {"node-2": {**warm, "CLUSTER_MPS_PATHS": ":".join(node2_paths)},
                      "node-1": {**warm, "CLUSTER_MPS_PATHS": ":".join(node1_paths)}},
                     wa_world=warm_world)
    bench.cluster.wait_ready(timeout=SERVER_TIMEOUT, nodes=["node-2", "node-1", "wa-1"])
    _wait_line(bench, "node-2", rf"recovery loaded partition {partition} ", RECOVERY_TIMEOUT)

    # "Every recoverable partition recovered", asserted directly: nothing may remain unowned except
    # retired lineage husks and quarantined poison. The old form compared the owned COUNT against the
    # pre-shutdown count, which a post-recovery proximity merge legitimately shrinks - recovery
    # completed and the check still failed for the rest of its 90 s.
    state = {"owners": [], "quarantined": [], "unrecovered": None}

    def recovered():
        state["owners"] = _owners(bench)
        state["quarantined"] = [row["Partition"] for row in _try(lambda: bench.cluster.get("/registry/quarantine"), []) or []]
        state["unrecovered"] = [row["Partition"] for row in state["owners"]
                                if (row.get("Node") is None or row.get("Recovering") is True)
                                and row.get("Retired") is False and row["Partition"] not in state["quarantined"]]
        return not state["unrecovered"]

    try:
        wait_until(recovered, RECOVERY_TIMEOUT, "every recoverable partition to recover")
    except ClusterError:
        pass
    owners_after = state["owners"]
    bench.check("the warm restart recovered every recoverable partition", not state["unrecovered"],
                f"unrecovered {state['unrecovered']} of {json.dumps(owners_after)[:300]} (quarantined {state['quarantined']})")
    bench.check("node-2 recovered the warm client partition",
                any(row["Partition"] == partition and row.get("Node") == "node-2" for row in owners_after))

    repair = None
    if lane == "poison-warm":
        repair = _poison_warm_repair(bench, poison_partition, pristine)
    else:
        # Content, not topology: manifest partitions restore from their Finals and the balancer may
        # legitimately co-locate them the moment they are live, which changes the partition-id set
        # the old exact census-vs-manifest equality demanded (seen live 2026-07-28: a post-restart
        # merge failed the identity while every entity survived). Assert what the clean marker
        # actually promises - each Final loaded exactly as catalogued, ownership never regressing,
        # and every Final entity live exactly once across the warm nodes, polled because restores
        # land per-claim.
        expected_ids: list[int] = []
        for save in finals:
            restored, generation, sequence = (save["SaveId"][key] for key in ("Partition", "Generation", "Sequence"))
            ownership_generation = ((registry_state.get("Partitions") or {}).get(str(restored)) or {}).get("Generation")
            bench.check(f"warm partition {restored} loaded its exact Final save {restored}/{generation}/{sequence}",
                        bool(_any_log(bench, rf"recovery read save {restored}/{generation}/{sequence}\b", REGULAR)))
            bench.check(f"warm partition {restored} ownership did not regress below generation {ownership_generation}",
                        any(row["Partition"] == restored and (row.get("Retired") is True or
                                                               (ownership_generation is not None and row["Generation"] >= ownership_generation))
                            for row in owners_after))
            expected_ids += _entity_ids(bench, save["Locations"][0])
        live: list[int] = []

        def once():
            nonlocal live
            live = [member["id"] for node in REGULAR for p in _partitions(bench, node) for member in p.get("members") or []]
            return all(live.count(entity) == 1 for entity in expected_ids)

        try:
            wait_until(once, RECOVERY_TIMEOUT, "every manifest Final entity live exactly once")
            content = True
        except ClusterError:
            content = False
        bench.record("warm-live-union", live)
        bench.check("every manifest Final entity is live exactly once across the warm nodes", content,
                    f"expected {expected_ids}"[:300])

    # The rejoin route is NOT seeded by hand. The live client route is soft and did not survive the
    # shutdown; the durable character home reported by the node that hosted the player is what has to
    # answer the join, or the client lands on the default backend (node 1), fails to find its body
    # there, and respawns a second one.
    route = bench.cluster.get(f"/registry/character-homes?client={CLIENT_ID}")
    bench.record("warm-client-route", route)
    bench.check("the character home survived the clean shutdown",
                route.get("Partition") == partition and route.get("Entity") not in (0, None), json.dumps(route))

    authority = bench.cluster.node("wa-1", "/authority-state")
    bench.record("warm-authority", authority)
    ticks = global_checkpoint["elapsedGameTime"]
    clock = authority.get("clockMilliseconds") or 0
    bench.check("the warm WA clock continues from the restored global checkpoint",
                authority.get("role") == "WA" and authority.get("accepts") is True
                and authority.get("generation", 0) > global_checkpoint["generation"]
                and clock * 10000 >= ticks and clock <= ticks / 10000 + SERVER_TIMEOUT * 1000,
                json.dumps({"authority": authority, "checkpointTicks": ticks})[:300])

    _reconnect_client(bench, partition, initial_gateway_log)
    # One body, on the right node. A rejoin sent to the wrong node cannot see the body it already has
    # (TryFindExistingCharacter is node-local) and spawns a second one, which is what the durable
    # character home exists to prevent - so count them.
    identity = route.get("Identity")
    bodies = [{**row, "node": node} for node in REGULAR
              for row in bench.cluster.node(node, "/characters").get("characters") or []]
    bench.record("warm-characters", bodies)
    mine = [row for row in bodies if row.get("savedFor") == identity and row.get("dead") is False]
    bench.check("the rejoined player owns exactly one living body, on node-2",
                len(mine) == 1 and mine[0]["node"] == "node-2",
                json.dumps([row for row in bodies if row.get("savedFor") == identity])[:300])
    character = bench.living_character(CLIENT_ID)
    bench.record("warm-client-observation", {"character": character, "route": route})
    bench.check("the warm client observation is living", character is not None)
    bench.screenshot(CLIENT_ID, "warm-client")

    if lane == "poison-warm":
        errors = _any_log(bench, r"ERROR: global save|recovery fell back|startup partition fenced|topology WAL apply unavailable")
        bench.check("the poison-warm logs carry no unexpected persistence errors", not errors, "\n".join(errors[:5]))
    else:
        errors = _any_log(bench, r"ERROR: global save|recovery load failed|recovery fell back|startup partition fenced"
                                 r"|topology WAL apply unavailable|duplicate")
        bench.check("the warm restart logs carry no persistence or duplicate-entity errors", not errors, "\n".join(errors[:5]))
    bench.exercised(clientPartition=partition, node1Finals=len(node1_paths), node2Finals=len(node2_paths),
                    poisonPartition=poison_partition, repairGeneration=(repair or {}).get("generation"))


def _poison_warm_repair(bench, poison_partition: int, pristine: Path) -> dict:
    try:
        quarantine = wait_until(lambda: (lambda q: q if any(row.get("partition") == poison_partition
                                                            for row in (q or {}).get("quarantined") or []) else None)(
            _try(lambda: bench.cluster.node("node-1", "/quarantine"))), RECOVERY_TIMEOUT, "node-1 to quarantine the poison")
    except ClusterError:
        quarantine = None
    bench.record("poison-warm-quarantine", quarantine or {})
    if quarantine is None:
        raise ClusterError("node 1 did not quarantine the poisoned warm partition")
    bench.check("node-1's log carries the warm quarantine record",
                bool(_lines(bench, "node-1", rf"QUARANTINED poison partition {poison_partition}\b")))
    bench.check("the poisoned warm partition is not loaded despite quarantine",
                all(p["id"] != poison_partition for p in _partitions(bench, "node-1")))
    # Registry visibility: the node's heartbeat carries its quarantine list, so an operator watching
    # only the GATEWAY finds the poisoned partition.
    seen = []

    def visible():
        nonlocal seen
        seen = _try(lambda: bench.cluster.get("/registry/quarantine"), []) or []
        return any(row.get("Node") == "node-1" and row.get("Partition") == poison_partition for row in seen)

    try:
        wait_until(visible, 30, "the quarantine at the registry")
        registry_seen = True
    except ClusterError:
        registry_seen = False
    bench.record("poison-warm-registry-quarantine", seen)
    bench.check("the quarantined partition became visible at the registry", registry_seen)

    # Cross-host repair: the replacement save is UPLOADED (request body), not a node-local path - the
    # form an operator on another host must use.
    control = _nodes(bench)["node-1"].control
    repair = _post_bytes(f"http://{control}/cluster/quarantine-repair?partition={poison_partition}", pristine.read_bytes())
    bench.record("poison-warm-repair", repair)
    if not (repair.get("partition") == poison_partition and (repair.get("members") or 0) >= 1):
        raise ClusterError(f"warm quarantine repair did not adopt the pristine save: {repair}")
    bench.check("the repaired warm partition left quarantine",
                not (bench.cluster.node("node-1", "/quarantine").get("quarantined") or []))
    try:
        wait_until(lambda: (_try(lambda: bench.cluster.get("/registry/quarantine"), [{}]) or []) == [], 30,
                   "the registry quarantine list to clear")
        cleared = True
    except ClusterError:
        cleared = False
    bench.check("the registry quarantine list cleared after the repair", cleared)

    # Post-repair: nothing recoverable left unowned, and every entity of the repaired save live on SOME
    # node. "Owned by node-1" was a topology pin - seen live, the repaired partition recovered on node-1
    # and was then legitimately co-located into node-2's partition.
    pristine_ids = _entity_ids(bench, pristine)

    def repaired_live():
        owners = _owners(bench)
        if any((row.get("Node") is None or row.get("Recovering") is True) and row.get("Retired") is False for row in owners):
            return False
        return all(any((_try(lambda: bench.cluster.node(node, f"/partition-of?entity={member}"), {}) or {}).get("partition")
                       for node in REGULAR) for member in pristine_ids)

    try:
        wait_until(repaired_live, RECOVERY_TIMEOUT, "the repaired content to come live")
        live = True
    except ClusterError:
        live = False
    bench.check("the repaired warm partition content came live", live, f"pristine {pristine_ids}"[:300])
    # ANY catalogued Final at the repair generation, not the NEWEST save: the moment the repaired
    # partition is live again it is ordinary balancer material, and a co-location handover can tee a
    # newer save 400 ms after the repair Final lands (exactly that failed this check on 2026-07-28) -
    # being superseded is not being absent from the lineage.
    saves = []

    def in_lineage():
        nonlocal saves
        saves = _try(lambda: bench.cluster.get(f"/registry/saves?partition={poison_partition}"), []) or []
        return any(row["SaveId"]["Generation"] == repair.get("generation") and row["SaveId"]["Sequence"] >= 1
                   and row.get("Kind") == 2 for row in saves)

    try:
        wait_until(in_lineage, HANDOVER_TIMEOUT, "the repair Final in the catalogue")
        saved = True
    except ClusterError:
        saved = False
    bench.record("poison-warm-saves", saves)
    bench.check("the repaired warm partition rejoined the catalogued save lineage", saved,
                f"repair {repair}, saves {[row['SaveId'] for row in saves]}"[:300])
    return repair


def p2_sav_01a(bench) -> None:
    _snapshot_lane(bench, "online-snapshot")


def p2_sav_01b(bench) -> None:
    _snapshot_lane(bench, "clean-shutdown")


def p2_sav_01c(bench) -> None:
    _snapshot_lane(bench, "warm-restart")


def p2_rec_02w(bench) -> None:
    _snapshot_lane(bench, "poison-warm")


# -- poison ----------------------------------------------------------------------------------


def p2_rec_02(bench) -> None:
    """P2-REC-03 (plan id P2-REC-02): node-2 quarantines the poison partition at boot instead of dying
    - the whole cluster stays up and the client spawns. Repair in place from an exported, edited,
    re-imported save and prove the partition rejoins the save lineage."""
    pristine = bench.dir / "poison-pristine.mps"

    def poison():
        # Poison node-2's end-beacon partition save (body corruption behind an intact header, so the
        # checksum fails but the partition id stays readable). The end beacon is 5.6 km from
        # everything else, so the repaired partition cannot merge into a neighbor and dissolve its
        # id - node-1's start beacon sits 5 m from the spawn ship and does exactly that, which is the
        # recorded wake-merge absorption gap, not a repair defect. A pristine copy is kept for the
        # export/edit/import repair loop.
        save = bench.cluster.config.split_world / "saves/P-0000000000000002/g00000001-s00000001.mps"
        shutil.copy2(save, pristine)
        _corrupt(save)
        if _verifies(bench, save):
            raise ClusterError("the poisoned save still verifies")

    _join_poisoned(bench, poison)
    quarantine = bench.cluster.node("node-2", "/quarantine")
    bench.record("poison-quarantine", quarantine)
    rows = quarantine.get("quarantined") or []
    bench.check("node-2 quarantined exactly the poisoned partition",
                len(rows) == 1 and rows[0].get("partition") == 2 and bool(rows[0].get("error")), json.dumps(rows)[:300])
    bench.check("the poisoned partition is not loaded despite quarantine",
                all(p["id"] != 2 for p in _partitions(bench, "node-2")))
    bench.check("node-2's log carries the quarantine record", bool(_lines(bench, "node-2", r"QUARANTINED poison partition 2\b")))

    # Export the pristine replica, make a real edit, re-import it as the repair save.
    export = bench.dir / "poison-export.xml"
    repaired = bench.dir / "poison-repaired.mps"
    result = _tool(bench, "mps-to-sector", str(pristine), str(export))
    if result.returncode != 0:
        raise ClusterError(f"mps-to-sector failed: {(result.stdout + result.stderr)[-300:]}")
    text = export.read_text()
    edited = text.replace("<CurrentStoredPower>3</CurrentStoredPower>", "<CurrentStoredPower>2.5</CurrentStoredPower>")
    if "<CurrentStoredPower>2.5</CurrentStoredPower>" not in edited:
        raise ClusterError("the repair edit did not apply to the export")
    export.write_text(edited)
    result = _tool(bench, "sector-to-mps", str(export), str(repaired), "2", "1", "2")
    bench.record("poison-import.txt", result.stdout + result.stderr)
    if result.returncode != 0:
        raise ClusterError(f"sector-to-mps failed: {(result.stdout + result.stderr)[-300:]}")
    repair = bench.cluster.node("node-2", f"/quarantine-repair?partition=2&path={urllib.parse.quote(str(repaired))}",
                                method="POST", timeout=120)
    bench.record("poison-repair", repair)
    bench.check("the quarantine repair adopted the replacement save",
                repair.get("partition") == 2 and repair.get("members") == 1 and repair.get("generation") == 1, json.dumps(repair))
    bench.check("the repaired partition left quarantine", not (bench.cluster.node("node-2", "/quarantine").get("quarantined") or []))
    bench.check("the repaired partition came into service",
                any(p["id"] == 2 and p.get("memberCount") == 1 for p in _partitions(bench, "node-2")))

    # The edit is live: the beacon battery reports the edited stored power.
    live = bench.dir / "poison-live-sector.xml"
    live.write_text(_raw(bench, "node-2", "/export"))
    powers = _battery_powers(live, POISON_BEACON_ENTITY)
    # The battery drifts from the moment the grid comes alive; anything well below the pristine 3.0
    # and near the edited 2.5 proves the edit is live.
    bench.check("the edited battery state is live in the repaired partition",
                powers is not None and len(powers) == 1 and 2.0 < powers[0] <= 2.6, str(powers))

    # Lineage rejoin: the repair queues an immediate Final capture, and registry catalog sequences
    # restart at 1 per ownership claim, so a catalogued save at the claimed generation proves the
    # repaired state is durable.
    newest = None

    def durable():
        nonlocal newest
        newest = _newest(bench, "/registry/saves/newest?partition=2")
        return isinstance(newest, dict) and newest["SaveId"].get("Generation") == 1 \
            and newest["SaveId"].get("Sequence", 0) >= 1 and newest.get("Kind") == 2

    try:
        wait_until(durable, HANDOVER_TIMEOUT, "the repair Final in the catalogue")
        rejoined = True
    except ClusterError:
        rejoined = False
    bench.record("poison-newest-save", newest or {})
    bench.check("the repaired partition rejoined the catalogued save lineage", rejoined, json.dumps(newest)[:300])
    bench.check("the client character survived the quarantine and repair", bench.living_character(CLIENT_ID) is not None)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(poisoned=2, repairSequence=_sequence(newest), battery=powers)


def _battery_powers(sector: Path, entity_id: str) -> list[float] | None:
    root = ET.parse(sector).getroot()
    for objects in root.iter("SectorObjects"):
        for entity in objects:
            if entity.findtext("EntityId") == entity_id:
                return [float(block.findtext("CurrentStoredPower")) for block in entity.iter()
                        if block.get("{http://www.w3.org/2001/XMLSchema-instance}type") == "MyObjectBuilder_BatteryBlock"]
    return None


# -- rebuild ---------------------------------------------------------------------------------


def p2_rec_04(bench) -> None:
    """Lose the registry (snapshot + WAL) entirely and rebuild the whole catalog from the save headers
    scanned off the surviving node data roots. The rebuilt registry must resume the lineage exactly
    like an ordinary crash recovery: nodes recover their partitions warm from their own Final saves,
    the World Authority restores the newest global save from the rebuilt catalog, and fresh
    partition/global captures register without fencing."""
    _join(bench)
    config = bench.cluster.config
    data_roots = {node.node_id: config.node_data(node) for node in config.nodes}

    # Global state that must survive the disaster: a banking account carried by a catalogued global save.
    owner = 942000000000000000 + random.randrange(32768)
    bench.cluster.node("node-1", f"/banking-test-create?ownerId={owner}&balance=750", method="POST")

    def balance_750():
        status = _try(lambda: bench.cluster.node("wa-1", f"/banking-test-status?ownerId={owner}"), {}) or {}
        return status.get("exists") and status.get("balance") == 750

    try:
        wait_until(balance_750, HANDOVER_TIMEOUT, "the rebuild account at the WA")
    except ClusterError:
        raise ClusterError("rebuild account did not converge at the World Authority") from None

    _try(lambda: bench.cluster.node("wa-1", "/global-save-capture", method="POST"))
    shutdown = _graceful_shutdown(bench, "rebuild-shutdown")
    _wait_all_exited(bench, "before the wipe")
    bench.kill_client(CLIENT_ID)
    _preserve_logs(bench, "initial")

    # Keep the pre-wipe registry only as cross-check evidence, then destroy it.
    registry = bench.run_root / "gateway-registry"
    before = _read_registry_state(registry)
    bench.record("registry-before-wipe", before)
    shutil.rmtree(registry)

    # Scan the surviving data roots into a rebuild payload from save headers alone.
    payload_path = bench.dir / "rebuild-payload.json"
    scan = _tool(bench, "catalog-scan", str(payload_path), *(f"{node}={data_roots[node]}" for node in ("node-1", "node-2", "wa-1")))
    bench.record("rebuild-scan.txt", scan.stdout + scan.stderr)
    if scan.returncode != 0:
        raise ClusterError("catalog scan failed")
    payload = json.loads(payload_path.read_text())
    manifest = shutdown["LastShutdown"]["Manifest"]
    bench.check("the catalog scan found every Final save of the shutdown manifest",
                all(any(save["Partition"] == ref["Partition"] and save["Generation"] == ref["Generation"]
                        and save["Sequence"] == ref["Sequence"] and save.get("Kind") == 2 for save in payload["Saves"])
                    for ref in manifest))

    # Fresh gateway over an empty registry directory, then rebuild the catalog.
    _start_gateway(bench)
    if bench.cluster.get("/registry/partitions"):
        raise ClusterError("the wiped registry is not empty")
    # Rebuild tail: an operator-supplied NextEntityIdRange floor. Without it the range counter restarts
    # and re-registering nodes would reuse entity-id ranges the pre-wipe nodes already burned - a
    # collision risk the operator closes by passing a floor above the highest range ever issued.
    floored = {**payload, "NextEntityIdRange": 7}
    bench.record("rebuild-payload-floored", floored)
    try:
        rebuilt = bench.cluster.post("/registry/rebuild", body=floored)
    except ClusterError as error:
        raise ClusterError(f"the catalog rebuild was refused: {error}") from None
    bench.record("rebuild-summary", rebuilt)
    bench.check("the rebuild summary is plausible",
                rebuilt.get("Partitions", 0) >= 2 and rebuilt.get("Saves", 0) >= 2 and rebuilt.get("GlobalSaves", 0) >= 1
                and rebuilt.get("WorldAuthorityGeneration", 0) >= 1, json.dumps(rebuilt))
    bench.check("the rebuild honored the operator NextEntityIdRange floor", rebuilt.get("NextEntityIdRange") == 7,
                str(rebuilt.get("NextEntityIdRange")))
    try:
        bench.cluster.post("/registry/rebuild", body=payload)
        second = "accepted"
    except ClusterError as error:
        second = str(error)
    bench.check("a second rebuild over a populated registry is refused", second != "accepted", second[:200])

    # The rebuilt partition table must equal the shutdown manifest exactly (a dissolved partition leaking
    # back in would duplicate its entities), unowned, at the same generation the pre-wipe registry held
    # after the drain releases.
    partitions_after = bench.cluster.get("/registry/partitions")
    bench.record("rebuild-partitions", partitions_after)
    before_partitions = before.get("Partitions") or {}
    manifest_ids = sorted({ref["Partition"] for ref in manifest})
    bench.check("the rebuilt partition table equals the pre-wipe registry",
                sorted(row["Partition"] for row in partitions_after) == manifest_ids
                and all(row.get("Node") is None
                        and row.get("Generation") == (before_partitions.get(str(row["Partition"])) or {}).get("Generation")
                        for row in partitions_after), json.dumps(partitions_after)[:300])

    # Split the newest Final saves between the nodes by scanned holder.
    newest_finals: dict[int, dict] = {}
    for save in payload["Saves"]:
        if save.get("Kind") != 2:
            continue
        key = (save["Generation"], save["Sequence"], save.get("Node") or "")
        current = newest_finals.get(save["Partition"])
        if current is None or key > (current["Generation"], current["Sequence"], current.get("Node") or ""):
            newest_finals[save["Partition"]] = save
    finals = list(newest_finals.values())
    node1_paths = [save["Location"] for save in finals if save.get("Node") == "node-1"]
    node2_paths = [save["Location"] for save in finals if save.get("Node") != "node-1"]
    if not node1_paths or not node2_paths:
        raise ClusterError("rebuild could not split Final saves between nodes")

    _use_restart_data_roots(bench)
    warm = {"CLUSTER_WARM_RECOVERY": "1"}
    _start_restarted(bench, ["node-1", "node-2", "wa-1"],
                     {"node-1": {**warm, "CLUSTER_MPS_PATHS": ":".join(node1_paths)},
                      "node-2": {**warm, "CLUSTER_MPS_PATHS": ":".join(node2_paths)}})
    bench.cluster.wait_node_log("wa-1", "global save restored generation=", timeout=SERVER_TIMEOUT)
    bench.cluster.wait_ready(timeout=SERVER_TIMEOUT, nodes=["node-1", "node-2", "wa-1"])

    expected = rebuilt.get("Partitions")
    owners_after = []

    def all_owned():
        nonlocal owners_after
        owners_after = _owners(bench)
        return len([row for row in owners_after if row.get("Node") is not None and row.get("Recovering") is False]) == expected

    try:
        wait_until(all_owned, RECOVERY_TIMEOUT, "every rebuilt partition to recover")
    except ClusterError:
        raise ClusterError(f"the rebuilt cluster did not recover every partition: {json.dumps(owners_after)[:300]}") from None
    bench.record("rebuild-owners-after", owners_after)

    # Every partition recovered from its exact newest Final save with all members.
    reports = {node: _partitions(bench, node) for node in REGULAR}
    bench.check("the rebuilt local partition census equals the shutdown manifest",
                sorted(p["id"] for node in REGULAR for p in reports[node]) == manifest_ids)
    for save in finals:
        restored = save["Partition"]
        owning = next((row.get("Node") for row in owners_after if row["Partition"] == restored), None)
        node = "node-2" if owning == "node-2" else "node-1"
        expected_members = _tool_json(bench, "verify-mps", save["Location"])["entities"]
        actual = next((p.get("memberCount") for p in reports[node] if p["id"] == restored), None)
        bench.check(f"rebuilt partition {restored} holds its Final save's members", actual == expected_members,
                    f"{actual} of {expected_members} on {node}")
        bench.check(f"rebuilt partition {restored} loaded its exact Final save",
                    bool(_lines(bench, node, rf"recovery read save {restored}/{save['Generation']}/{save['Sequence']}\b")))

    # The banking balance crossed the wipe inside the rebuilt global save.
    try:
        wait_until(balance_750, HANDOVER_TIMEOUT, "the banking balance after the rebuild")
        survived = True
    except ClusterError:
        survived = False
    bench.check("the banking balance survived the registry wipe", survived)

    # Lineage continues without fencing: a fresh partition capture registers at sequence 1 of the
    # claimed generation, and a fresh global save registers at the new authority generation.
    authority = bench.cluster.get("/registry/world-authority")
    bench.check("the World Authority generation continued from the rebuilt catalog",
                authority.get("Node") == "wa-1" and authority.get("Generation") == rebuilt.get("WorldAuthorityGeneration", 0) + 1,
                json.dumps(authority)[:200])
    probe = min(owners_after, key=lambda row: row["Partition"])
    probe_node = "node-2" if probe.get("Node") == "node-2" else "node-1"
    try:
        bench.cluster.node(probe_node, f"/save-capture?partition={probe['Partition']}", method="POST")
    except ClusterError as error:
        raise ClusterError(f"the post-rebuild capture could not be queued: {error}") from None
    newest = None

    def captured():
        nonlocal newest
        newest = _newest(bench, f"/registry/saves/newest?partition={probe['Partition']}")
        return isinstance(newest, dict) and newest["SaveId"].get("Generation") == probe["Generation"] \
            and newest["SaveId"].get("Sequence", 0) >= 1

    try:
        wait_until(captured, HANDOVER_TIMEOUT, "the post-rebuild capture in the catalogue")
        ok = True
    except ClusterError:
        ok = False
    bench.check("the post-rebuild capture registered in the rebuilt catalog", ok, json.dumps(newest)[:200])
    try:
        bench.cluster.node("wa-1", "/global-save-capture", method="POST")
    except ClusterError as error:
        raise ClusterError(f"the post-rebuild global capture could not be queued: {error}") from None
    newest_global = None

    def global_captured():
        nonlocal newest_global
        newest_global = _newest(bench, "/registry/global-saves/newest")
        return isinstance(newest_global, dict) and newest_global["SaveId"].get("Generation") == authority.get("Generation") \
            and newest_global["SaveId"].get("Sequence", 0) >= 1

    try:
        wait_until(global_captured, HANDOVER_TIMEOUT, "the post-rebuild global capture in the catalogue")
        ok = True
    except ClusterError:
        ok = False
    bench.check("the post-rebuild global capture registered in the rebuilt catalog", ok, json.dumps(newest_global)[:200])

    # Every node that re-registered against the rebuilt registry drew its entity-id range at or above
    # the operator floor.
    nodes = bench.cluster.get("/registry/nodes")
    bench.record("rebuild-nodes", nodes)
    ranges = bench.cluster.get("/registry/entity-id-ranges")
    bench.record("rebuild-entity-id-ranges", ranges)
    bench.check("at least three nodes re-registered", len(nodes) >= 3, json.dumps([row.get("Node") for row in nodes]))
    bench.check("re-registered nodes drew entity-id ranges at or above the floor",
                len(ranges) >= 3 and all(row.get("Index", -1) >= 7 for row in ranges),
                json.dumps([{"Node": row.get("Node"), "Index": row.get("Index")} for row in ranges]))

    # Rebuild tail: the scanned catalog knows holders by name only (no control endpoints on disk), so
    # cross-host replication has no usable source until each holder re-registers and the registry
    # completes its endpoints. All three holders re-registered above.
    catalog = {"saves": [], "globals": []}

    def holders():
        catalog["saves"] = _try(lambda: bench.cluster.get("/registry/saves"), []) or []
        catalog["globals"] = _try(lambda: bench.cluster.get("/registry/global-saves"), []) or []
        return all(rows and all(len(row.get("HolderEndpoints") or []) >= 1 for row in rows) for rows in catalog.values())

    try:
        wait_until(holders, HANDOVER_TIMEOUT, "holder endpoints on every rebuilt save")
        ok = True
    except ClusterError:
        ok = False
    bench.record("rebuild-saves", catalog["saves"])
    bench.check("rebuilt saves carry holder endpoints after re-registration", ok,
                json.dumps([{"SaveId": row.get("SaveId"), "HolderEndpoints": row.get("HolderEndpoints")}
                            for rows in catalog.values() for row in rows])[:300])
    bench.record("rebuild", {"shutdown": shutdown, "rebuilt": rebuilt, "owners": owners_after,
                             "probeSave": newest, "globalSave": newest_global})
    bench.exercised(partitions=manifest_ids, node1Finals=len(node1_paths), node2Finals=len(node2_paths),
                    waGeneration=authority.get("Generation"), probePartition=probe["Partition"])


#: SE1-0232: the contract block owner's starting funds and the reward the posted contract takes from them.
CONTRACT_FUNDS = 100000
CONTRACT_REWARD = 1000


def _generation(bench, partition: int) -> int:
    rows = _try(lambda: bench.cluster.get("/registry/partitions"), []) or []
    return next((int(row.get("Generation") or 0) for row in rows if int(row["Partition"]) == partition), 0)


def p3_hnd_contracts(bench) -> None:
    """SE1-0232 - a contract posted at a contract block is still offered there, and its reward still paid in
    (nothing refunded), after a handover node-1 -> node-2, a save plus SIGKILL and restart of the node holding
    it, and a handover to the other node. Last, deleting the grid, which is not a cluster close, drops the
    offer as vanilla does. Clientless: a dynamic grid with a contract block, the contract posted through the mod
    API (an acquisition, like P3-BLK-06's)."""
    from scenarios import _all_partitions, _force_handover
    from scenarios_ext import _gate_residuals, _wait_arrival, _wait_partition
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        owner = 945000000000000000 + random.randrange(10 ** 6)
        bench.cluster.node("node-1", f"/banking-test-create?ownerId={owner}&balance={CONTRACT_FUNDS}", "POST")

        def balance(node_id: str = "wa-1"):
            return _try(lambda: bench.cluster.node(node_id, f"/banking-test-status?ownerId={owner}"), {}).get("balance")
        wait_until(lambda: balance("node-1") == CONTRACT_FUNDS or None, HANDOVER_TIMEOUT, "the owner's account on node-1")
        spawned = bench.cluster.node("node-1", f"/contract-test-block?owner={owner}&x=880000&y=0&z=0", "POST")
        bench.record("contract-block", spawned)
        grid, block = spawned["grid"], spawned["block"]
        partition = _wait_partition(bench, "node-1", grid)
        posted = bench.cluster.node("node-1", f"/contract-test-post?block={block}&reward={CONTRACT_REWARD}", "POST")
        bench.record("contract-posted", posted)
        if not posted.get("success"):
            bench.check("a contract was posted at the block", False, json.dumps(posted))
            return
        contract = posted["contractId"]
        paid_in = CONTRACT_FUNDS - CONTRACT_REWARD
        _try(lambda: wait_until(lambda: balance() == paid_in or None, HANDOVER_TIMEOUT, "the reward debit at the WA"))

        def leg(name: str) -> str:
            holder = _all_partitions(bench).get(partition)
            offers = {node: _try(lambda node=node: bench.cluster.node(node, f"/contract-test-offers?block={block}"), {})
                      for node in REGULAR}
            bench.record(f"contract-offers-{name}", {"holder": holder, "offers": offers, "balance": balance()})
            hosted = offers.get(holder) or {}
            bench.check(f"{name}: {holder} hosts the block and offers the contract",
                        bool(hosted.get("here")) and contract in (hosted.get("contracts") or []), json.dumps(offers))
            bench.check(f"{name}: the reward stays paid in, nothing refunded", balance() == paid_in,
                        f"balance {balance()}, expected {paid_in}")
            return holder

        def hand_over(name: str, source: str, target: str) -> bool:
            try:
                _force_handover(bench, source, target, partition)
                _wait_arrival(bench, partition, target)
            except ClusterError as error:
                bench.check(f"{name}: the block's partition moved {source} -> {target}", False, str(error)[:300])
                return False
            residuals = _gate_residuals(bench, target, partition)
            bench.check(f"{name}: the gate verified the transfer with zero residuals", residuals == 0, f"residuals={residuals}")
            return True

        holder = leg("posted")
        if not hand_over("handover", holder, "node-2"):
            return
        holder = leg("handover")

        # The catalogued save is what the restarted node (or the one recovering the partition) loads.
        def save_id():
            save = _newest(bench, f"/registry/saves/newest?partition={partition}")
            return ((save or {}).get("SaveId") or {}).get("Generation") or 0, _sequence(save)
        before = save_id()
        bench.cluster.node(holder, f"/save-capture?partition={partition}", "POST")
        wait_until(lambda: save_id() > before or None, HANDOVER_TIMEOUT, "a fresh save of the block's partition")
        generation = _generation(bench, partition)
        bench.cluster.restart_node(holder)

        # A restart within seconds of the kill can match the dead process's "Game ready", and the registry
        # names the old owner until its lease expires: wait for a load at a later generation instead.
        def served():
            _try(lambda: bench.cluster.post("/registry/leases/expire"))
            node = _all_partitions(bench).get(partition)
            if not node or _generation(bench, partition) <= generation:
                return None
            hosted = _try(lambda: bench.cluster.node(node, f"/contract-test-offers?block={block}"), {})
            return node if hosted.get("here") else None
        wait_until(served, RECOVERY_TIMEOUT, "the block's partition to be loaded again at a later generation")
        holder = leg("restart")

        if not hand_over("back", holder, next(node for node in REGULAR if node != holder)):
            return
        holder = leg("back")

        # A delete is not a cluster close: vanilla's close handler runs and drops the offer. Vanilla refunds the
        # reward only when it still finds the block, which a whole-grid delete does not (seen with and without
        # the fix), so the balance is recorded, not asserted.
        bench.cluster.node(holder, f"/contract-test-delete-grid?grid={grid}", "POST")
        time.sleep(3)
        offers = bench.cluster.node(holder, f"/contract-test-offers?block={block}")
        bench.record("contract-deleted", {"offers": offers, "balance": balance()})
        bench.check("delete: the contract is no longer offered", contract not in (offers.get("contracts") or []), json.dumps(offers))
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(block=block, contract=contract, legs="posted, handover, restart, back, delete")


SCENARIOS_RESTART = {
    "P2-SAV-01a": (p2_sav_01a, "online lineage-cover snapshot", "3"),
    "P2-SAV-01b": (p2_sav_01b, "graceful cluster shutdown with Final saves", "3"),
    "P2-SAV-01c": (p2_sav_01c, "warm restart from the clean marker", "3"),
    "P2-REC-02w": (p2_rec_02w, "poisoned staged Final quarantined on a warm restart", "4"),
    "P2-REC-02": (p2_rec_02, "poison partition quarantine and in-place repair", "4"),
    "P2-REC-04": (p2_rec_04, "catalog rebuild from node disks after registry loss", "4"),
    "P3-HND-CONTRACTS": (p3_hnd_contracts, "SE1-0232: contract block offers across handover and restart", "3"),
}
