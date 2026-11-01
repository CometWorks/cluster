"""Phase 1 core: the former run-cluster.sh lanes `control`, `manual`, `clientless`, `handover` and
`automatic`, as native scenarios.

Every one of them boots node-1, node-2 and the World Authority on a split world, joins one headless
client on node-1 and ends with the same client-side proof: the client's character is alive, the
survival kit near it is replicated, and a power change the client makes to it replicates back. What
differs is the move in between:

* ``P1-CTL``     - nothing moves (the control run);
* ``P1-MANUAL``  - the Gateway hands the client's own partition to node-2 on request;
* ``P1-HND-03``  - a clientless partition outside the client's view moves to node-2; the client stays;
* ``P1-HND-01``  - the client flies a cockpit, and the Gateway hands the moving ship over mid-flight;
* ``P1-AUTO``    - the client flies a cockpit, and the automatic §7.1 co-location moves a partition.
"""

from __future__ import annotations

import json
import math
import re
import time
from pathlib import Path

from cluster import ClusterError, wait_until

CLIENT_ID = 1234567890
HANDOVER_TIMEOUT = 75
TRAVEL_SECONDS = 60
CLIENT_VIEW_DISTANCE = 5000
HANDOVER_STATES = ("begun", "captured", "prepared", "imported", "adopt-ready", "adopted", "active",
                   "committed", "target-committed", "released")
CLIENTLESS_STATES = ("begun", "captured", "prepared", "imported", "active", "committed", "target-committed",
                     "released")


# -- shared plumbing ------------------------------------------------------------------------


def _nodes(bench) -> dict:
    return {node.node_id: node for node in bench.cluster.config.nodes}


def _log(bench, name: str) -> Path:
    return bench.run_root / f"{name}.log"


def _log_files(bench, name: str) -> list[Path]:
    """Where a process's lines land: its stdout log, and for a node also the game log in its data
    dir - plugin loggers such as DirectTransport's write only there. Rotated stdout logs of an
    earlier start of the same node are included (oldest first), and so are the game logs of a
    warm restart onto a fresh data root (``<name>-data-restart``)."""
    files = sorted(bench.run_root.glob(f"{name}.log.*")) + [_log(bench, name)]
    for data in (f"{name}-data", f"{name}-data-restart"):
        files += sorted((bench.run_root / data).glob("SpaceEngineersDedicated_*.log"))
    return [path for path in files if path.is_file()]


def _lines(bench, name: str, pattern: str) -> list[str]:
    expression = re.compile(pattern)
    return [line for path in _log_files(bench, name)
            for line in path.read_text(errors="replace").splitlines() if expression.search(line)]


def _wait_line(bench, name: str, pattern: str, timeout: float = HANDOVER_TIMEOUT) -> str:
    return wait_until(lambda: next(iter(_lines(bench, name, pattern)), None), timeout,
                      f"{name}.log line /{pattern}/")


def _handover_url(bench, partition: int, clientless: bool = False, source: str = "node-1",
                  target: str = "node-2", target_control: str | None = None,
                  client: int | None = None) -> str:
    """`client` names the session whose partition moves. The gateway only infers it when EXACTLY
    one client is connected (DirectGateway.ResolveSession), so a scenario that starts a second one
    must name it or the call comes back 400 Missing query parameter 'client'."""
    nodes = _nodes(bench)
    return (f"/handover?source={nodes[source].control}&target={target_control or nodes[target].control}"
            f"&backend={nodes[target].backend}&partition={partition}"
            + (f"&client={client}" if client is not None else "")
            + ("&clientless=true" if clientless else ""))


def _partitions(bench, node_id: str) -> list[dict]:
    return bench.cluster.node(node_id, "/partitions")["partitions"]


def _owners(bench) -> list[dict]:
    return bench.cluster.get("/registry/partitions")


def _client_partition(partitions: list[dict]) -> int | None:
    return next((p["id"] for p in partitions if CLIENT_ID in (p.get("clients") or [])), None)


def _character(api) -> dict | None:
    try:
        character = api.get_character()
    except Exception:  # noqa: BLE001 - no local character is an answer, not an error
        return None
    return character if character and character.get("state") != "dead" else None


def _join(bench, api_name: str = "01-spawned"):
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    bench.screenshot(CLIENT_ID, api_name)
    return api


def _grid_ops(api, body: dict) -> dict:
    return api._post("/v1/grids/ops", body)


def _survival_kit_round_trip(bench, api) -> None:
    """The common ending: alive, the survival kit replicated, and a power change replicates back."""
    bench.check("the client's character is alive", _character(api) is not None)
    kit = wait_until(lambda: next(iter(api.find_nearby_blocks(block_type="SurvivalKit", max_distance=100, limit=1)), None),
                     HANDOVER_TIMEOUT, "a replicated survival kit near the client")
    bench.record("survival-kit", kit)
    result = _grid_ops(api, {"sets": [{"type": "enabled", "gridId": kit["gridId"], "min": kit["min"], "value": False}]})
    bench.record("disable-kit", result)
    bench.check("the survival kit power action succeeds", bool(result["sets"][0].get("success")), json.dumps(result)[:300])
    # 45 s: the change must replicate, and under load the replication stream lags past 15 s.
    read = {"gets": [{"type": "block", "gridId": kit["gridId"], "min": kit["min"]}]}
    last = {}

    def disabled():
        nonlocal last
        last = _grid_ops(api, read)["gets"][0]
        return last.get("success") and last.get("data", {}).get("enabled") is False

    try:
        wait_until(disabled, 45, "the survival kit to read back disabled")
        replicated = True
    except ClusterError:
        replicated = False
    bench.record("read-kit", last)
    bench.check("the survival kit power change replicates back to the client", replicated, json.dumps(last)[:300])
    bench.screenshot(CLIENT_ID, "99-after")


def _client_moved_to_node2(bench) -> None:
    node2 = _nodes(bench)["node-2"]
    for label, name, pattern in (
            ("the Gateway connected the client's backend on node-2", "gateway",
             rf"Connected backend {re.escape(node2.backend)} as client {CLIENT_ID}"),
            ("node-2 accepted the client", "node-2", rf"Accepted client {CLIENT_ID}"),
            ("the Gateway committed the transfer", "gateway", r"state=committed"),
            ("node-2 adopted the client view", "node-2", rf"client-view adopt client={CLIENT_ID}")):
        try:
            line = _wait_line(bench, name, pattern)
            bench.check(label, True, line[-200:])
        except ClusterError as error:
            bench.check(label, False, str(error))


def _no_reconnect(bench, label: str) -> None:
    bench.check(f"{label}: the Gateway accepted the client exactly once",
                len(_lines(bench, "gateway", rf"Accepted client {CLIENT_ID}\b")) == 1)
    bench.check(f"{label}: the client never disconnected",
                not _lines(bench, "gateway", rf"Client {CLIENT_ID} disconnected"))


def _markers_check(bench, markers: dict, budget_ms: int, names=("Start", "End")) -> None:
    """HUD signal markers (beacon/antenna GPS icons). Budget 0: never vanished. A non-zero budget
    allows the two handoffs the cluster cannot make instantaneous, each at most once."""
    rows = markers.get("markers") or []
    for name in names:
        mine = [row for row in rows if re.search(name, row.get("name", ""))]
        bench.check(f"the {name} marker was visible on the client", bool(mine), json.dumps([r.get("name") for r in rows]))
        allowed = 0 if budget_ms == 0 else 2
        flicker = [row for row in mine if not (row.get("visible") and row.get("maxGapMilliseconds", 0) <= budget_ms
                                               and row.get("disappearances", 0) <= allowed)]
        events = [event for event in markers.get("events") or [] if re.search(name, event.get("name", ""))]
        bench.check(f"the {name} marker stayed lit (budget {budget_ms} ms)", mine and not flicker, json.dumps(events)[:300])


def _enter_nearest_cockpit(bench, api, speed: str = "walk") -> dict:
    cockpit = next(iter(api.find_nearby_blocks(block_type="Cockpit", max_distance=10, limit=1)), None)
    if cockpit is None:
        raise ClusterError("no cockpit within 10 m of the client")
    x, y, z = cockpit["worldPosition"]
    move = api.character_move_to(x, y, z, speed=speed, timeout=30)
    bench.record("cockpit-move", move)
    # Arriving is not aiming: `use` raycasts from the camera. Look, use, and retry - a character still
    # bobbing on its jetpack at the 2 m reach boundary legitimately misses a single attempt.
    for _ in range(5):
        api.character_look_at(x, y, z)
        api.character_use()
        try:
            wait_until(lambda: (_character(api) or {}).get("state") == "sitting", 8, "the character to sit")
            break
        except ClusterError:
            continue
    state = (_character(api) or {}).get("state")
    if state != "sitting":
        raise ClusterError(f"the character did not enter the cockpit (state={state}, cockpit={cockpit}, move={move})")
    bench.check("the client entered the cockpit", True, json.dumps({k: cockpit[k] for k in ("entityId", "gridId")}))
    return cockpit


# -- scenarios ------------------------------------------------------------------------------


def p1_ctl(bench) -> None:
    """Control run: the client joins node-1 and stays there."""
    api = _join(bench)
    _survival_kit_round_trip(bench, api)
    bench.check("the client stayed on node-1", _client_partition(_partitions(bench, "node-1")) is not None)
    _no_reconnect(bench, "control")
    bench.exercised(route="node-1 only")


def p1_manual(bench) -> None:
    """The Gateway hands the client's own partition to node-2 on request."""
    api = _join(bench)
    partitions = _partitions(bench, "node-1")
    partition = _client_partition(partitions) or next((p["id"] for p in partitions if not p.get("anchored")), None)
    if partition is None:
        raise ClusterError("no client or movable partition on node-1: " + json.dumps(partitions)[:300])
    bench.record("handover", bench.cluster.post(_handover_url(bench, partition), timeout=300))
    _client_moved_to_node2(bench)
    _survival_kit_round_trip(bench, api)
    bench.exercised(route="node-1 -> node-2 (manual)", partition=partition)


def p1_hnd_03(bench) -> None:
    """A clientless partition beyond the client's view moves to node-2; the client stays on node-1."""
    api = _join(bench)
    source_before = _partitions(bench, "node-1")
    owners_before = _owners(bench)
    owned = {row["Partition"] for row in owners_before if row["Node"] == "node-1"}
    partition = next((p["id"] for p in source_before if not p.get("clients") and p["id"] in owned), None)
    client_partition = _client_partition(source_before)
    bench.record("clientless-source-before", source_before)
    bench.record("clientless-target-before", _partitions(bench, "node-2"))
    bench.record("clientless-owners-before", owners_before)
    if partition is None:
        raise ClusterError("node-1 reports no clientless partition it owns")
    bench.check("the client sits on a distinct node-1 partition",
                client_partition is not None and client_partition != partition, f"{client_partition} vs {partition}")
    spheres = {p["id"]: p["sphere"] for p in source_before}
    a, b = spheres[partition], spheres[client_partition]
    gap = math.dist((a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"])) - a["radius"] - b["radius"]
    bench.record("clientless-distance", {"partition": partition, "clientPartition": client_partition,
                                         "sphereGap": gap, "viewDistance": CLIENT_VIEW_DISTANCE})
    bench.check("the cargo partition is outside the client's view distance", gap > CLIENT_VIEW_DISTANCE,
                f"{gap:.1f} m > {CLIENT_VIEW_DISTANCE} m")
    count = next(p["memberCount"] for p in source_before if p["id"] == partition)
    generation = next(row["Generation"] for row in owners_before if row["Partition"] == partition and row["Node"] == "node-1")

    # A fenced handover whose target control does not match its target backend is refused.
    try:
        bench.cluster.post(_handover_url(bench, partition, clientless=True,
                                         target_control=_nodes(bench)["node-1"].control), timeout=40)
        mismatch = "accepted"
    except ClusterError as error:
        mismatch = str(error)
    bench.check("a mismatched fenced target control is refused with 400", "-> 400:" in mismatch, mismatch[:200])

    bench.record("clientless-handover", bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))
    _wait_line(bench, "gateway", rf"partition={partition} state=committed")
    _wait_line(bench, "gateway", rf"partition={partition} state=released")
    committed = _lines(bench, "gateway", rf"handover transferId=\S+ partition={partition} state=committed")
    transfer = re.search(r"transferId=(\S+)", committed[-1]).group(1)
    missing = [state for state in CLIENTLESS_STATES
               if not _lines(bench, "gateway", rf"handover transferId={re.escape(transfer)} partition={partition} state={state}\b")]
    bench.check("the clientless transaction reached every state", not missing, f"missing: {missing}")
    try:
        bench.cluster.node("node-2", f"/discard?transferId={transfer}&partition={partition}&generation={generation}",
                           method="POST", timeout=40)
        discard = "accepted"
    except ClusterError as error:
        discard = str(error)
    bench.check("a post-commit discard on the target is refused (500, commit fencing)",
                "-> 500:" in discard and "Committed transfer cannot be discarded" in discard, discard[:200])

    owners_after = _owners(bench)
    source_after = _partitions(bench, "node-1")
    target_after = _partitions(bench, "node-2")
    bench.record("clientless-owners-after", owners_after)
    bench.record("clientless-source-after", source_after)
    bench.record("clientless-target-after", target_after)
    bench.check("ownership advanced exactly once to node-2",
                any(row["Partition"] == partition and row["Node"] == "node-2" and row["Generation"] == generation + 1
                    for row in owners_after))
    bench.check("the client's partition is still owned by node-1",
                any(row["Partition"] == client_partition and row["Node"] == "node-1" for row in owners_after))
    bench.check("node-1 released the partition", all(p["id"] != partition for p in source_after))
    target_count = next((p["memberCount"] for p in target_after if p["id"] == partition and not p.get("clients")), None)
    bench.check("node-2 holds every member", target_count == count, f"{target_count} of {count}")
    bench.check("the client is still attached to its node-1 partition",
                any(p["id"] == client_partition and CLIENT_ID in (p.get("clients") or []) for p in source_after))
    node2 = _nodes(bench)["node-2"]
    bench.check("the client's backend never switched",
                not _lines(bench, "gateway", rf"Connected backend {re.escape(node2.backend)} as client {CLIENT_ID}"))
    bench.check("node-2 adopted no client view", not _lines(bench, "node-2", rf"client-view adopt client={CLIENT_ID}"))
    _no_reconnect(bench, "clientless")
    _survival_kit_round_trip(bench, api)
    bench.check("the client's backend still never switched after the kit round trip",
                not _lines(bench, "gateway", rf"Connected backend {re.escape(node2.backend)} as client {CLIENT_ID}"))
    bench.exercised(route="cargo node-1 -> node-2, client stays", partition=partition, members=count,
                    generation=[generation, generation + 1], transfer=transfer)


p1_hnd_03.split_step = "to-clientless"


def p1_hnd_01(bench) -> None:
    """Fly-through handover: exact frozen builders, no reconnect or recreation, one short pause."""
    api = _join(bench)
    cockpit = _enter_nearest_cockpit(bench, api)
    api.reset_gps_markers()
    grid, cockpit_id = cockpit["gridId"], cockpit["entityId"]
    x, y, z = cockpit["worldPosition"]
    # WA-3 transient: a trash-ELIGIBLE probe protected only by the nearby client must ride the frozen
    # transfer and never be swept while the player is transiently absent. Spawned relative to the ship.
    probe = bench.cluster.node("node-1", f"/trash-test-spawn?x={x + 30}&y={y}&z={z}", method="POST")["grid"]
    bench.check("the trash probe spawned and exists",
                probe > 0 and bench.cluster.node("node-1", f"/trash-test-state?entityId={probe}").get("exists") is True)
    owners_before = _owners(bench)
    lineage = bench.cluster.get("/registry/lineage")
    watermark = max((row.get("ClusterTime", 0) for row in lineage), default=0)
    boot_merges = len(_lines(bench, "node-1", r"topology Merge committed"))
    partition = _client_partition(_partitions(bench, "node-1"))
    if partition is None:
        raise ClusterError("the client's ship partition was not reported before the handover")
    generation = next((row["Generation"] for row in owners_before if row["Partition"] == partition and row["Node"] == "node-1"), None)
    if generation is None:
        raise ClusterError("the client's ship was not owned by node-1 before the handover")
    probe_partition = wait_until(lambda: bench.cluster.node("node-1", f"/partition-of?entity={probe}").get("partition"),
                                 20, "the trash probe to settle into a partition")
    count = next(p["memberCount"] for p in _partitions(bench, "node-1") if p["id"] == partition)

    dampeners = api.set_dampeners(False)
    bench.check("the ship's dampeners are off", dampeners.get("enabled") is False, json.dumps(dampeners))
    api.set_input_state(keys=["W"], mode="override")
    try:
        speed = wait_until(lambda: (lambda s: s if s >= 5 else None)(
            _grid_ops(api, {"gets": [{"type": "grid", "entityId": grid}]})["gets"][0].get("data", {}).get("speed", 0)),
            5, "the ship to reach 5 m/s", interval=0.05)
    finally:
        api.clear_input_state()
    bench.check("the ship reached 5 m/s", speed >= 5, f"{speed} m/s")
    time.sleep(1)
    burn = api.get_gps_markers()
    bench.record("handover-gps-markers-burn", burn)
    _markers_check(bench, burn, 0)
    api.start_handover_observation(grid)
    bench.record("handover", bench.cluster.post(_handover_url(bench, partition), timeout=300))
    released = _lines(bench, "gateway", rf"handover transferId=\S+ partition={partition} state=released")
    if not released:
        raise ClusterError("the fly-through handover did not release")
    transfer = re.search(r"transferId=(\S+)", released[-1]).group(1)
    time.sleep(1)
    observation = api.get_handover_observation()
    bench.record("handover-observation", observation)
    bench.check("the client saw no replication or entity recreation",
                observation["samples"] > 10 and observation["stateSyncPackets"] > 10 and len(observation["networkIds"]) > 0
                and observation["createEvents"] == 0 and observation["destroyEvents"] == 0
                and observation["replacements"] == 0 and observation["entityRecreations"] == 0
                and observation["missingFrames"] == 0, json.dumps(observation)[:400])
    bench.check("position continuity within 10 m (P1-HND-01)", float(observation["maxPositionError"]) <= 10,
                str(observation["maxPositionError"]))
    bench.check("exactly one sub-second delivery pause",
                observation["deliveryPauses"] == 1 and observation["maxStateSyncGapMs"] < 1000,
                f"{observation['deliveryPauses']} pauses, {observation['maxStateSyncGapMs']} ms")
    order, previous = [], 0
    text = _log(bench, "gateway").read_text(errors="replace").splitlines()
    for state in HANDOVER_STATES:
        pattern = re.compile(rf"handover transferId={re.escape(transfer)} partition={partition} state={state}\b")
        numbers = [number for number, line in enumerate(text, 1) if pattern.search(line)]
        order.append((state, numbers[-1] if numbers else None))
    bench.check("every handover state was logged, in order",
                all(line is not None for _, line in order)
                and [line for _, line in order] == sorted(line for _, line in order), json.dumps(order))
    try:
        _wait_line(bench, "node-2", rf"handover state verified transferId={re.escape(transfer)} partition={partition} .* pbs=1")
        bench.check("node-2 verified the frozen state and PB equality", True)
    except ClusterError as error:
        bench.check("node-2 verified the frozen state and PB equality", False, str(error))

    # The 7.1 chase can merge the transferred partition on node-2 within ~200 ms of release; settle on
    # whichever stable state appears first. Only merges newer than the watermark count.
    def settled():
        owners = _owners(bench)
        target = _partitions(bench, "node-2")
        if any(row["Partition"] == partition and row["Node"] == "node-2" and row["Generation"] == generation + 1
               for row in owners) and next((p["memberCount"] for p in target if p["id"] == partition), None) == count:
            return "advanced"
        merges = [row for row in bench.cluster.get("/registry/lineage")
                  if row.get("Kind") in (1, "Merge") and row.get("ClusterTime", 0) > watermark
                  and any(item["Partition"] == partition for item in row.get("Inputs") or [])]
        if merges:
            output = merges[-1]["Outputs"][0]["Partition"]
            merged = next((p["memberCount"] for p in target if p["id"] == output), 0)
            if any(row["Partition"] == output and row["Node"] == "node-2" for row in owners) and merged >= count:
                return f"merged into {output}"
        return None

    try:
        outcome = wait_until(settled, 15, "ownership to settle on node-2")
    except ClusterError as error:
        outcome = None
        bench.record("settle-timeout", str(error))
    bench.check("ownership advanced exactly once to node-2, or merged there intact", outcome is not None, str(outcome))
    bench.check("node-1 released the partition", all(p["id"] != partition for p in _partitions(bench, "node-1")))
    if boot_merges == 0:
        for label, pattern in (("the 7.1 invariant alarmed after the switch", r"ALARM: cross-node sphere gap"),
                               ("the 7.1 co-location chase ran", r"Automatic partition handover triggered for [0-9]+, .*direction source->target")):
            try:
                _wait_line(bench, "gateway", pattern, 30 if "ALARM" in pattern else HANDOVER_TIMEOUT)
                bench.check(label, True)
            except ClusterError as error:
                bench.check(label, False, str(error))
    else:
        bench.check("no 7.1 alarm when the coalesced start partition moved whole",
                    not _lines(bench, "gateway", r"ALARM: cross-node sphere gap"))
    try:
        wait_until(lambda: bench.cluster.node("node-2", f"/trash-test-state?entityId={probe}").get("exists") is True,
                   60, "the trash probe on node-2")
        arrived = True
    except ClusterError:
        arrived = False
    bench.check("the trash-eligible probe arrived on node-2", arrived)
    time.sleep(6)
    later = bench.cluster.node("node-2", f"/trash-test-state?entityId={probe}") if arrived else {}
    bench.record("handover-trash-transient", {"node": "node-2", "probePartition": probe_partition, "probeAfterHandover": later})
    bench.check("the trash probe was not swept after the chase settled",
                later.get("exists") is True and later.get("markedAsTrash") is False, json.dumps(later))
    markers = api.get_gps_markers()
    bench.record("handover-gps-markers", markers)
    _markers_check(bench, markers, 3000)
    _no_reconnect(bench, "fly-through")
    bench.check("the client process is alive", bench.clients[CLIENT_ID].alive())
    _client_moved_to_node2(bench)
    _survival_kit_round_trip(bench, api)
    character = _character(api) or {}
    bench.check("the client still controls the same cockpit",
                (character.get("controlledEntity") or {}).get("entityId") == cockpit_id,
                json.dumps(character.get("controlledEntity")))
    bench.exercised(route="node-1 -> node-2 (fly-through)", partition=partition, transfer=transfer,
                    generation=[generation, generation + 1], settle=outcome, bootMerges=boot_merges)


p1_hnd_01.split_step = "to-handover"


def _ship_visible(api, grid: int) -> bool:
    try:
        return any(g.get("entityId") == grid for g in api.list_grids() or [])
    except Exception:  # noqa: BLE001
        return False


def _seat_exit_standoff(bench, api, cockpit: dict, offset: float) -> list[float]:
    """Leave the cockpit (Use while seated) and teleport `offset` m sideways, the pilot's AI-leg standoff."""
    api.character_use()
    wait_until(lambda: (_character(api) or {}).get("state") not in (None, "sitting"), 15, "the character to leave the seat")
    x, y, z = cockpit["worldPosition"]
    api.character_teleport(x + offset, y, z)
    return [x + offset, y, z]


def _handover_and_watch(bench, api, grid: int, source: str, target: str, label: str) -> dict:
    """Hand the client's partition source -> target, then watch the client's view of its ship for 30 s."""
    partition = _client_partition(_partitions(bench, source))
    if partition is None:
        raise ClusterError(f"{label}: the client's partition is not on {source}")
    ship_partition = bench.cluster.node(source, f"/partition-of?entity={grid}").get("partition")
    started = time.time()
    bench.record(f"{label}-handover", bench.cluster.post(_handover_url(bench, partition, source=source, target=target), timeout=300))
    samples = []
    while time.time() - started < 30:
        samples.append((round(time.time() - started, 1), _ship_visible(api, grid)))
        time.sleep(1)
    result = {"partition": partition, "shipPartition": ship_partition, "samples": samples,
              "visibleAtEnd": samples[-1][1] if samples else False, "missing": sum(1 for _, seen in samples if not seen)}
    bench.record(f"{label}-view", result)
    return result


def p1_hnd_seat(bench) -> None:
    """T-0253: a handover of the client's partition seconds after the pilot LEFT its cockpit for a 2.5 km
    standoff. Live w33: 8 of 35 balance moves inside that window lost the ship from the client's view for good
    ('Grid N not found'), against 0 of 1,277 handovers of a pilot that had held its standoff for a while.

        CONTROL: board, leave, stand off 2.5 km, wait 60 s, hand node-1 -> node-2: the ship stays in view.
        TRIGGER: re-board, leave, stand off, and hand node-2 -> node-1 within ~1 s: the ship must stay in view.
    """
    api = _join(bench)
    cockpit = _enter_nearest_cockpit(bench, api)
    grid = cockpit["gridId"]
    bench.check("SETUP: the client sees its ship while seated", _ship_visible(api, grid))

    _seat_exit_standoff(bench, api, cockpit, 2500)
    time.sleep(60)
    bench.check("SETUP: the ship is in view from the 2.5 km standoff after 60 s", _ship_visible(api, grid))
    control = _handover_and_watch(bench, api, grid, "node-1", "node-2", "control")
    bench.check("CONTROL: after a settled-standoff handover the ship is in view at the end",
                control["visibleAtEnd"], json.dumps(control)[:300])

    x, y, z = cockpit["worldPosition"]
    api.character_teleport(x, y + 3, z)
    time.sleep(5)
    # After the standoff teleport the character floats on its jetpack; a walk cannot close the last metres.
    cockpit = _enter_nearest_cockpit(bench, api, speed="fly")
    time.sleep(5)
    _seat_exit_standoff(bench, api, cockpit, 2500)
    time.sleep(1)
    trigger = _handover_and_watch(bench, api, grid, "node-2", "node-1", "trigger")
    bench.check("TRIGGER: after a handover ~1 s into the standoff the ship is back in view within 30 s",
                trigger["visibleAtEnd"], json.dumps(trigger)[:300])
    bench.exercised(ship=grid, control=control, trigger=trigger)


p1_hnd_seat.split_step = "to-handover"


def p1_auto(bench) -> None:
    """The client flies a cockpit; the automatic §7.1 co-location fires before the raw gap reaches D."""
    api = _join(bench)
    _enter_nearest_cockpit(bench, api)
    api.reset_gps_markers()
    burn = api.get_gps_markers()
    api.set_input_state(keys=["W"], mode="override")
    try:
        for _ in range(TRAVEL_SECONDS):
            if not _lines(bench, "gateway", r"Automatic partition handover triggered for [0-9]+"):
                burn = api.get_gps_markers()
            time.sleep(1)
    finally:
        api.clear_input_state()
    bench.record("automatic-gps-markers-burn", burn)
    trigger = _wait_line(bench, "gateway", r"Automatic partition handover triggered for [0-9]+")
    partition = int(re.search(r"triggered for ([0-9]+),", trigger).group(1))
    raw_gap = float(re.search(r"raw gap ([0-9.-]+)m", trigger).group(1))
    bench.check("co-location triggered before the raw gap reached D", raw_gap > 3000, f"{raw_gap} m")
    try:
        _wait_line(bench, "gateway", rf"handover transferId=\S+ partition={partition} state=released")
        bench.check("the co-location transfer released", True)
    except ClusterError as error:
        bench.check("the co-location transfer released", False, str(error))
    _no_reconnect(bench, "fly-through")
    direction = "reverse pull" if "target->source" in trigger else "client switch"
    _markers_check(bench, burn, 0)
    markers = api.get_gps_markers()
    bench.record("automatic-gps-markers", markers)
    _markers_check(bench, markers, 3000)
    _survival_kit_round_trip(bench, api)
    bench.exercised(route=f"7.1 co-location ({direction})", partition=partition, rawGap=raw_gap)


SCENARIOS_CORE = {
    "P1-CTL": (p1_ctl, "client control run on node-1", "3"),
    "P1-MANUAL": (p1_manual, "end-to-end handover with a real client and screenshots", "3"),
    "P1-HND-03": (p1_hnd_03, "clientless partition transfer", "3"),
    "P1-HND-01": (p1_hnd_01, "fly-through handover with exact frozen object builders", "3"),
    "P1-AUTO": (p1_auto, "automatic handover while flying a cockpit", "3"),
    "P1-HND-SEAT": (p1_hnd_seat, "a handover seconds after the pilot left its cockpit keeps the ship in view (T-0253)", "3"),
}
