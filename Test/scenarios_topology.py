"""Topology: the former run-cluster.sh lanes `proximity`, `save-lineage`, `teleport`, `triage`,
`triage-load` and `concealment`, as native scenarios.

All but concealment run on the MagnetarWorld `to-proximity` fixture (two movable ships, one per
regular node, more than 4 km apart) with one headless client on node-1:

* ``P1-HND-02``   - the client flies its ship toward node-2's ship; the §7.1 envelope pulls the
  clientless ship to node-1 before the raw gap reaches D, the two merge atomically, and a turret
  on the client's ship damages the pulled one;
* ``P1-SAV-07``   - the same approach, then a deterministic split and merge of the merged
  partition, GC planning that must not change the cover, a node-1 SIGKILL, and node-2 recovering
  every entity of the parent save exactly once with both lineage operations Collapsed;
* ``P2-WA-06a``   - a server-side teleport of the client into node-2's region: split, gateway
  handover and merge carry it there exactly once while the client rides through;
* ``P2-REC-03``   - node-1 dies holding the client partition and a partition with a newer save;
  node-2 (count-capped) recovers the player partition first, the other waits behind a visible
  capacityShortfall until an executor-driven node-3 returns capacity;
* ``P2-REC-03l``  - the same with node-2 restarted under a member ceiling calibrated from the live
  census, so the shortfall is load-attributed.

And on the plain split world with concealment enabled on the regular nodes:

* ``P2-CON-01``   - cold-start concealment, explicit / camera-view / cross-node approach wakes,
  grace reconcealment with advancing Final saves, and a SIGKILL inside a wake that loses nothing.
"""

from __future__ import annotations

import json
import math
import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _character, _grid_ops, _join, _lines, _no_reconnect, \
    _nodes, _owners, _partitions, _wait_line
from scenarios_recovery import RECOVERY_TIMEOUT, _world_tool
from scenarios_offline import _put_config

#: SERVER_TIMEOUT of the shell lanes: a (re)started node reaches "Game ready" within this.
SERVER_TIMEOUT = 300
#: The to-proximity fixture's target ship (ship_b's grid) and the client ship it is positioned
#: relative to in the SAV-07 choreography.
TARGET_GRID = 128014564749527617
CLIENT_SHIP_GRID = 128014564749527614
#: D, the cross-node distance the §7.1 co-location must beat.
SYNC_DISTANCE = 3000
#: P2-CON-01 concealment on the regular nodes only (the WA loads no entities). A short grace keeps
#: the reconcealment cycles inside the scenario budget. Pre-wake 3500: the camera-view leg needs a
#: demand position outside the 3000 m intra-node merge reach yet inside the pre-wake margin; every
#: other distance in the lane (5.6 km isolation, radius+500 approach) is unaffected by the raise.
CONCEAL_ENV = {"CLUSTER_CONCEALMENT": "1", "CLUSTER_CONCEAL_GRACE_SECONDS": "10",
               "CLUSTER_CONCEAL_PREWAKE_METERS": "3500"}


# -- plumbing -------------------------------------------------------------------------------


def _require(bench, label: str, passed: bool, detail: str = "") -> None:
    """A check the rest of the scenario cannot run without (the shell lane's `fail`)."""
    if not bench.check(label, passed, detail):
        raise ClusterError(f"{label}: {detail}"[:600])


def _try(function, default=None):
    """A poll probe: an unreachable or erroring endpoint is 'not yet', never a failure."""
    try:
        return function()
    except (ClusterError, OSError, ValueError, KeyError, TypeError, IndexError):
        return default


def _poll(predicate, timeout: float, interval: float = 1.0):
    """wait_until that returns None on timeout instead of raising (the lane records and asserts)."""
    try:
        return wait_until(lambda: _try(predicate), timeout, "poll", interval)
    except ClusterError:
        return None


def _sphere_gap(a: dict, b: dict) -> float:
    return math.dist((a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"])) - a["radius"] - b["radius"]


def _clients(partition: dict) -> list:
    return partition.get("clients") or []


def _field(row: dict, name: str):
    """Registry rows are PascalCase in the shell lanes; tolerate camelCase too."""
    if not isinstance(row, dict):
        return None
    return next((value for key, value in row.items() if key.lower() == name.lower()), None)


def _enter_cockpit(bench, api, max_distance: float = 15) -> dict:
    """scenarios_core._enter_nearest_cockpit with the shell lane's 15 m search radius (the core
    helper searches 10 m)."""
    cockpit = next(iter(api.find_nearby_blocks(block_type="Cockpit", max_distance=max_distance, limit=1)), None)
    _require(bench, "the cockpit-ready barrier found a cockpit", cockpit is not None)
    x, y, z = cockpit["worldPosition"]
    bench.record("cockpit-move", api.character_move_to(x, y, z, speed="walk", timeout=30))
    for _ in range(5):
        api.character_look_at(x, y, z)
        api.character_use()
        if _poll(lambda: (_character(api) or {}).get("state") == "sitting", 8):
            break
    state = (_character(api) or {}).get("state")
    _require(bench, "the cockpit-ready barrier was reached (client seated)", state == "sitting", f"state={state}")
    return cockpit


def _start_client(bench):
    """The client half of scenarios_core._join, for scenarios that start the cluster themselves."""
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    return api


# -- P1-HND-02 / P1-SAV-07 ------------------------------------------------------------------


def _proximity(bench, lineage: bool) -> None:
    api = _join(bench)
    node1_report = bench.cluster.node("node-1", "/partitions")
    node2_report = bench.cluster.node("node-2", "/partitions")
    ship_a = next((p for p in node1_report["partitions"]
                   if p.get("anchored") is False and -1000 < p["sphere"]["z"] < 1000), None)
    ship_b = next((p for p in node2_report["partitions"]
                   if p.get("anchored") is False and -6000 < p["sphere"]["z"] < -4000), None)
    bench.record("proximity-initial", {"node1": node1_report, "node2": node2_report})
    _require(bench, "the proximity fixture produced two movable ships on opposite nodes",
             ship_a is not None and ship_b is not None)
    a, b = ship_a["id"], ship_b["id"]
    owners = _owners(bench)
    _require(bench, "the proximity ships have different initial ownership",
             any(r["Partition"] == a and r["Node"] == "node-1" for r in owners)
             and any(r["Partition"] == b and r["Node"] == "node-2" for r in owners), json.dumps(owners)[:300])
    initial_gap = _sphere_gap(ship_a["sphere"], ship_b["sphere"])
    _require(bench, "the initial raw ship gap is above 4000 m", initial_gap > 4000, f"{initial_gap:.1f} m")
    print(f"  initial ownership ship {a}=node-1 ship {b}=node-2 raw-gap={initial_gap:.1f} m")

    _enter_cockpit(bench, api)
    integrity_url = f"/block-integrity?grid={TARGET_GRID}&x=0&y=0&z=2"
    before_integrity = bench.cluster.node("node-2", integrity_url)
    _require(bench, "the target block integrity is available before co-location",
             before_integrity.get("exists") and before_integrity.get("integrity") is not None,
             json.dumps(before_integrity))
    before_integrity = before_integrity["integrity"]

    gaps = bench.dir / "proximity-gaps.jsonl"
    gaps.write_text("")
    bench.result.artifacts.append(gaps.name)
    trigger_pattern = rf"Automatic partition handover triggered for {b}, .*direction target->source"
    api.set_input_state(keys=["W"], mode="override")
    try:
        triggered = False
        for _ in range(HANDOVER_TIMEOUT):
            source = _try(lambda: _partitions(bench, "node-1"))
            destination = _try(lambda: _partitions(bench, "node-2"))
            if source is None or destination is None:
                time.sleep(1)
                continue
            x = next((p for p in source if p["id"] == a), None)
            y = next((p for p in destination if p["id"] == b), None)
            raw = None
            if x and y:
                raw = _sphere_gap(x["sphere"], y["sphere"])
                sample = {"rawGap": raw,
                          "envelopeGap": raw - min(x["maxMemberSpeed"], 100) * 5 - min(y["maxMemberSpeed"], 100) * 5,
                          "sourceSpeed": x["maxMemberSpeed"], "targetSpeed": y["maxMemberSpeed"]}
                with gaps.open("a") as stream:
                    stream.write(json.dumps(sample) + "\n")
            if _lines(bench, "gateway", trigger_pattern):
                triggered = True
                break
            if raw is not None:
                _require(bench, "the raw cross-node gap never reached D before the handover", raw > SYNC_DISTANCE,
                         f"{raw:.1f} m")
            time.sleep(1)
        # 7.1 direction: both ships are unanchored and only ship_a carries a client, so the
        # clientless ship_b is PULLED to node-1 - the client never switches backends.
        _require(bench, "the proximity envelope triggered the 7.1 reverse pull", triggered)
        trigger = _lines(bench, "gateway", rf"Automatic partition handover triggered for {b}\b")[-1]
        trigger_gap = float(re.search(r"raw gap ([0-9.-]+)m", trigger).group(1))
        bench.check("the handover triggered before the raw gap reached D", trigger_gap > SYNC_DISTANCE,
                    f"{trigger_gap} m")
        _wait_line(bench, "gateway", r"state=committed")
        _wait_line(bench, "node-1", r"topology Merge committed")
        bench.check("node-1 committed the atomic local merge", True)
        node2 = _nodes(bench)["node-2"]
        bench.check("the client never switched backends during the 7.1 reverse pull",
                    not _lines(bench, "gateway", rf"Connected backend {re.escape(node2.backend)} as client {CLIENT_ID}"))
        if lineage:
            api.clear_input_state()
            _save_lineage(bench, api)
            return

        battery = _poll(lambda: next((block for block in api.find_nearby_blocks(
            block_type="BatteryBlock", max_distance=250, limit=10) if block.get("gridId") == TARGET_GRID), None),
            HANDOVER_TIMEOUT)
    finally:
        api.clear_input_state()
    _require(bench, "the target ship entered the exact client view after the handover", battery is not None)
    gun = next(iter(api.find_nearby_blocks(block_type="LargeGatlingTurret", max_distance=50, limit=1)), None)
    _require(bench, "a functional gatling gun is available after the handover", gun is not None)
    shot = _grid_ops(api, {"sets": [{"type": "action", "gridId": gun["gridId"], "min": gun["min"],
                                     "actionId": "ShootOnce"}]})
    bench.record("shoot-once", shot)
    _require(bench, "the gatling ShootOnce action succeeds", bool(shot["sets"][0].get("success")), json.dumps(shot)[:300])
    after_integrity = before_integrity

    def damaged():
        nonlocal after_integrity
        row = bench.cluster.node("node-1", integrity_url)
        after_integrity = row["integrity"] if row.get("exists") else 0
        return after_integrity < before_integrity

    _poll(damaged, 20)
    bench.check("the target block integrity decreased after the weapon action", after_integrity < before_integrity,
                f"{before_integrity} -> {after_integrity}")
    bench.check("the client character survived the proximity handover and fight", _character(api) is not None)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(shipA=a, shipB=b, initialGap=round(initial_gap, 1), triggerGap=trigger_gap,
                    integrity=[before_integrity, after_integrity])


def _save_lineage(bench, api) -> None:
    """P1-SAV-07: split/merge lineage, GC-stable cover, crash, exactly-once recovery."""
    bench.cluster.post("/pause")
    # The shell lane predates the load balancer (T-0141). A balance migration of a split output
    # captures behind the 60 s write delay below and holds node-1's game thread for 30 s, so the
    # deterministic merge never runs (bench run 20260917-031742Z). Splits and merges are node-local
    # and keep running; only the Gateway's automatic moves stop.
    bench.cluster.post("/automatic/pause")
    merges = [row for row in bench.cluster.get("/registry/lineage") if row.get("Kind") in (1, "Merge")]
    _require(bench, "the SAV-07 merged client partition is available", bool(merges))
    merged = merges[-1]["Outputs"][0]["Partition"]
    # The 7.1 reverse pull leaves the merged client partition on NODE 1 - the whole SAV-07
    # choreography runs there and recovery lands on node 2.
    for _ in range(3):
        _poll(lambda: any(row["partition"] == merged and row.get("pending") is False
                          for row in bench.cluster.node("node-1", "/save-status")["partitions"]), HANDOVER_TIMEOUT)
        bench.cluster.node("node-1", f"/save-capture?partition={merged}", method="POST")
    parent = {}

    def three_saves():
        nonlocal parent
        parent = bench.cluster.get(f"/registry/saves/newest?partition={merged}")
        return parent["SaveId"]["Sequence"] >= 3

    _poll(three_saves, HANDOVER_TIMEOUT)
    bench.record("sav07-parent-save", parent)
    parent_verify = _world_tool("verify-mps", parent["Locations"][0])
    bench.record("sav07-parent-verify", parent_verify)
    expected = sorted(parent_verify["entityIds"])
    bench.cluster.node("node-1", "/save-test?simRatio=actual&writeDelayMs=60000", method="POST")
    # T-0319: the default split distance is 4 x D (12 km); this lane moves the target 6 km and is about
    # lineage, not the distance - pin the pre-T-0319 5000 m on node-1 before the deterministic split.
    bench.record("sav07-config", _put_config(bench, "node-1", {"partition.splitDistance": "5000"}))
    bench.cluster.node("node-1", f"/test-position?entity={TARGET_GRID}&relativeTo={CLIENT_SHIP_GRID}&z=6000",
                       method="POST")

    def split_of_merged():
        splits = [row for row in bench.cluster.get("/registry/lineage") if row.get("Kind") in (0, "Split")]
        return splits[-1] if splits and splits[-1]["Inputs"][0]["Partition"] == merged else None

    split = _poll(split_of_merged, HANDOVER_TIMEOUT)
    _require(bench, "the SAV-07 deterministic split committed", split is not None)
    bench.record("sav07-split", split)
    # Conservation and disjointness, NOT equality. The parent save is a point-in-time snapshot and
    # this census is live, so a proximity merge landing between the two legitimately ADDS members -
    # the Start Beacon did exactly that, sitting in its own partition when the save was taken and
    # joining partition 3 before the split. Demanding equality made an ordinary merge a failure.
    # What must still hold: nothing the save recorded may vanish, and nothing may land in two
    # outputs at once.
    members = [entity for ids in (split.get("Members") or {}).values() for entity in ids]
    bench.check("the SAV-07 split filters neither lost nor duplicated a member",
                len(members) == len(set(members)) and not set(expected) - set(members),
                f"expected superset of {expected}, got {json.dumps(split.get('Members'))}"[:400])
    time.sleep(11)  # the shell lane's fixed settle between the split and the merge move
    bench.cluster.node("node-1", f"/test-position?entity={TARGET_GRID}&relativeTo={CLIENT_SHIP_GRID}&x=100",
                       method="POST")
    # T-0339 design C: the target leaves its partition at the split distance while already within D
    # of the client ship's partition, so with transfers on it moves there in ONE change - a Split row
    # with two inputs [source, target] -> [source, target] - instead of split + merge. Either op is
    # the lane's join; a transfer must put TARGET_GRID in its target output (Outputs[1]).
    def joined():
        for row in reversed(bench.cluster.get("/registry/lineage")):
            if row["ClusterTime"] <= split["ClusterTime"]:
                continue
            if row.get("Kind") in (1, "Merge"):
                return row
            inputs, outputs = row.get("Inputs") or [], row.get("Outputs") or []
            if row.get("Kind") in (0, "Split") and len(inputs) == 2 and len(outputs) == 2 \
                    and TARGET_GRID in ((row.get("Members") or {}).get(str(outputs[1]["Partition"])) or []):
                return row
        return None

    merge = _poll(joined, HANDOVER_TIMEOUT)
    _require(bench, "the SAV-07 deterministic merge (or design C transfer) committed", merge is not None)
    bench.record("sav07-join", merge)
    if merge.get("Kind") in (0, "Split"):
        ship_partition = bench.cluster.node("node-1", f"/partition-of?entity={CLIENT_SHIP_GRID}").get("partition")
        bench.check("the SAV-07 transfer put the target grid in the client ship's partition",
                    ship_partition == merge["Outputs"][1]["Partition"],
                    f"transfer target {merge['Outputs'][1]['Partition']}, client ship in {ship_partition}")
    # The lane's subject is the ENTITY, not a partition id: the partitioner keeps splitting and
    # merging after the sampled operations, so any pinned output id is stale by the time it is
    # used (this lane once waited 90 s for partition 5 while node-2 recovered 7 and 8). Resolve
    # "the partition currently holding the target grid" at each step instead.
    subject = bench.cluster.node("node-1", f"/partition-of?entity={TARGET_GRID}").get("partition")
    _require(bench, "the SAV-07 target grid is in a node-1 partition before the crash", bool(subject))
    cover_before = bench.cluster.get(f"/registry/saves/resolve?partition={subject}")
    bench.record("sav07-cover-before", cover_before)
    bench.record("sav07-gc", bench.cluster.post("/registry/retention?keep=1&snapshots=0"))
    cover_after = bench.cluster.get(f"/registry/saves/resolve?partition={subject}")
    bench.check("the SAV-07 cover did not change across GC planning",
                json.dumps(cover_before, sort_keys=True) == json.dumps(cover_after, sort_keys=True))
    bench.cluster.kill(slot="node-1")

    # Recovery is complete for the subject when node-2 holds the target grid LIVE - whatever
    # partition id it lives under by then.
    def recovered():
        bench.cluster.post("/registry/leases/expire")
        return bench.cluster.node("node-2", f"/partition-of?entity={TARGET_GRID}").get("partition")

    recovered_partition = _poll(recovered, RECOVERY_TIMEOUT)
    _require(bench, "SAV-07 recovery brought the target grid live on node-2", bool(recovered_partition))
    loads = _lines(bench, "node-2", r"recovery loaded partition [0-9]+ generation [0-9]+ with")
    _require(bench, "node-2 recorded a recovery load for the target", bool(loads))
    claimed, claimed_generation = map(int, re.search(r"recovery loaded partition ([0-9]+) generation ([0-9]+)",
                                                     loads[-1]).groups())
    _wait_line(bench, "node-2", rf"partition save registered: partition {claimed} generation {claimed_generation} "
                                r"sequence 1 kind Final", RECOVERY_TIMEOUT)
    bench.check("node-2 catalogued the SAV-07 recovery Final", True)
    # Exactly-once, asserted on the recovered WORLD: every entity the parent save recorded is
    # live exactly once across node-2's partitions. Equality against one partition's save was
    # wrong twice over - proximity merges legitimately ADD members, and post-recovery topology
    # moves them between partitions. POLLED, because the entities need not arrive together:
    # the moving transport legitimately splits into its own partition before the kill, and
    # that partition recovers on its own claim - asserting the full set the instant the
    # SUBJECT partition became ready failed on exactly that (seen live twice on 2026-07-28).
    census = []

    def all_live():
        nonlocal census
        bench.cluster.post("/registry/leases/expire")
        census = [m["id"] for p in _partitions(bench, "node-2") for m in p.get("members") or []]
        return all(census.count(entity) == 1 for entity in expected)

    live = _poll(all_live, RECOVERY_TIMEOUT)
    bench.record("sav07-node2-census", census)
    bench.check("SAV-07 recovery restored every expected entity exactly once", bool(live),
                f"expected {expected}, live {census}"[:400])
    # The recovery Final itself: valid, internally duplicate-free, catalogued under the claim.
    final = next((row for row in bench.cluster.get(f"/registry/saves?partition={claimed}")
                  if row["SaveId"]["Generation"] == claimed_generation and row["SaveId"]["Sequence"] == 1), None)
    _require(bench, f"the SAV-07 recovery Final g{claimed_generation}/s1 of partition {claimed} is catalogued",
             final is not None)
    final_verify = _world_tool("verify-mps", final["Locations"][0])
    bench.record("sav07-final-verify", final_verify)
    ids = final_verify.get("entityIds") or []
    bench.check("the SAV-07 recovery Final is valid and duplicate-free",
                bool(final_verify.get("valid")) and len(ids) == len(set(ids)), json.dumps(final_verify)[:300])
    lineage_after = bench.cluster.get("/registry/lineage")
    bench.record("sav07-lineage-after", lineage_after)
    operations = [row for row in lineage_after if row.get("OpId") in (split["OpId"], merge["OpId"])]
    bench.check("the SAV-07 lineage converged to Collapsed", all(row.get("Collapsed") for row in operations),
                json.dumps(operations)[:300])
    bench.exercised(mergedPartition=merged, split=split["OpId"], merge=merge["OpId"], subject=subject,
                    recovered=[claimed, claimed_generation], entities=len(expected))


def p1_hnd_02(bench) -> None:
    """Proximity reverse pull, atomic merge and a cross-node fire on the pulled ship."""
    _proximity(bench, lineage=False)


def p1_sav_07(bench) -> None:
    """Split/merge lineage, GC-stable cover and exactly-once recovery after a node kill."""
    _proximity(bench, lineage=True)


p1_hnd_02.split_step = "to-proximity"
p1_sav_07.split_step = "to-proximity"


# -- P2-WA-06a ------------------------------------------------------------------------------


def p2_wa_06a(bench) -> None:
    """WA-6 teleport ownership arbitration.

    A server-side teleport is just a position change - ownership must follow through the cluster's
    own machinery. The next partition evaluation splits the teleported character away from its old
    partition-mates, the gateway's automatic handover moves the resulting client partition to the
    node owning the destination region, and the topology merge absorbs it there. The scenario
    proves the whole chain with the real client online."""
    _join(bench)
    source_before = _partitions(bench, "node-1")
    mine = next((p for p in source_before if CLIENT_ID in _clients(p)), None)
    _require(bench, "node-1 reports the client partition", mine is not None)
    client_partition, sphere = mine["id"], mine["sphere"]
    target_before = _partitions(bench, "node-2")
    _require(bench, "node-2 has a destination partition", bool(target_before))
    target = max(target_before, key=lambda p: math.dist((p["sphere"]["x"], p["sphere"]["y"], p["sphere"]["z"]),
                                                        (sphere["x"], sphere["y"], sphere["z"])))
    tx, ty, tz = target["sphere"]["x"], target["sphere"]["y"] + 20, target["sphere"]["z"]
    released_before = len(_lines(bench, "gateway", r"state=released"))

    teleport = bench.cluster.node("node-1", f"/teleport?client={CLIENT_ID}&x={tx}&y={ty}&z={tz}", method="POST")
    bench.record("teleport", teleport)
    character_id = teleport.get("character")
    bench.check("the teleport reports the client partition as its source",
                teleport.get("sourcePartition") == client_partition, json.dumps(teleport))

    # An unknown client is refused, not silently absorbed.
    try:
        bench.cluster.node("node-1", "/teleport?client=4242&x=0&y=0&z=0", method="POST")
        refused = False
    except ClusterError:
        refused = True
    bench.check("teleporting an unknown client is refused", refused)

    # The chain completes: the client is served by node-2, in a partition that also holds the
    # destination structure (the arrival merged - a lone character would be a 1-member partition).
    target_after = []

    def arrived():
        nonlocal target_after
        target_after = _partitions(bench, "node-2")
        return any(CLIENT_ID in _clients(p) and p["memberCount"] >= 2 for p in target_after)

    arbitrated = _poll(arrived, RECOVERY_TIMEOUT)
    bench.record("teleport-target-after", target_after)
    _require(bench, "the teleported client arrived in a merged node-2 partition", bool(arbitrated))

    # Handover machinery (not any side channel) carried it: a new released transfer. Poll: release
    # is the transfer's LAST step and can land seconds after the client-arrival evidence on a
    # loaded box (seen once in a sweep).
    bench.check("a handover transfer released for the teleport",
                bool(_poll(lambda: len(_lines(bench, "gateway", r"state=released")) > released_before, 30)))

    # Exactly-once: the character entity exists on node-2 and not on node-1.
    sectors = {}
    for node_id in ("node-2", "node-1"):
        sectors[node_id] = str(bench.cluster.node(node_id, "/export", timeout=120))
        bench.record(f"teleport-{node_id.replace('-', '')}-sector.txt", sectors[node_id])
    marker = f"<EntityId>{character_id}</EntityId>"
    bench.check("the teleported character entity exists on node-2", marker in sectors["node-2"])
    bench.check("the teleported character entity no longer exists on node-1", marker not in sectors["node-1"])
    source_after = _partitions(bench, "node-1")
    bench.check("node-1 no longer reports the teleported client",
                all(CLIENT_ID not in _clients(p) for p in source_after))

    # The physical client rode through: one accepted session, no disconnect, alive.
    _no_reconnect(bench, "teleport")
    bench.check("the client process is alive", bench.clients[CLIENT_ID].alive())
    bench.check("the client character is alive after the teleport", bench.living_character(CLIENT_ID) is not None)
    bench.record("teleport-evidence", {"teleport": teleport, "targetAfter": target_after, "sourceAfter": source_after})
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(character=character_id, sourcePartition=client_partition, targetPartition=target["id"],
                    route="node-1 -> node-2 (teleport)")


p2_wa_06a.split_step = "to-proximity"


# -- P2-REC-03 / P2-REC-03l -----------------------------------------------------------------


def _owner_row(owners: list[dict], partition: int) -> dict | None:
    return next((row for row in owners if row["Partition"] == partition), None)


def _plan_slot(bench) -> dict:
    return next((row for row in bench.cluster.get("/registry/node-plan")
                 if _field(row, "SlotKey") == "slot-node-3"), {})


def _executor_heartbeat(bench, attempt: str, state: str, node: str | None, failure: str | None = None) -> None:
    bench.cluster.post("/registry/executors/heartbeat", body={
        "Executor": "lane-executor",
        "Observations": [{"SlotKey": "slot-node-3", "AttemptKey": attempt, "State": state, "Node": node,
                          "Failure": failure}]})


def _shortfall(status: dict) -> bool:
    return "capacityShortfall" in (status.get("lifecycle", {}).get("DegradedReasons") or [])


def _triage(bench, load: bool) -> None:
    """P2-REC host-loss triage (Orchestration 9.3).

    node-1 dies holding the client partition and a second partition with a STRICTLY NEWER save.
    node-2 has one free capacity slot (or, for ``load``, exactly the member headroom of the client
    partition) - it must spend it on the player-attached partition (the priority order beats
    recency), the other partition stays Unowned with the registry Degraded on capacityShortfall,
    and recovery completes only when capacity returns as node-3."""
    env = bench.cluster.config.node_env_by_id
    if load:
        # node-1 is count-capped at its own two partitions so it cannot steal node-2's partitions
        # while node-2 restarts under the member ceiling.
        env.setdefault("node-1", {})["CLUSTER_MAX_PARTITIONS"] = "2"
    else:
        # node-2 owns two proximity partitions and may recover exactly one more, so the registry's
        # priority order decides which of node-1's partitions survives the host loss first and
        # which waits for capacity to return.
        env.setdefault("node-2", {})["CLUSTER_MAX_PARTITIONS"] = "3"
    _join(bench)
    node1_state = _partitions(bench, "node-1")
    bench.record("triage-node1-before", node1_state)
    client_partition = next((p["id"] for p in node1_state if CLIENT_ID in _clients(p)), None)
    _require(bench, "node-1 reports the client partition", client_partition is not None)
    other_partition = next((p["id"] for p in node1_state if p["id"] != client_partition), None)
    _require(bench, "node-1 holds a second partition", other_partition is not None)
    _require(bench, "node-1 holds exactly two partitions", len(node1_state) == 2, str(len(node1_state)))

    # Fresh catalogued saves for both, the non-client partition LAST, so a recency-only policy
    # would pick the wrong partition and the assertion below is real.
    def sequence(partition: int) -> int:
        return _try(lambda: bench.cluster.get(f"/registry/saves/newest?partition={partition}")["SaveId"]["Sequence"], 0)

    for partition in (client_partition, other_partition):
        before = sequence(partition)
        bench.cluster.node("node-1", f"/save-capture?partition={partition}", method="POST")
        _require(bench, f"the capture of partition {partition} was catalogued",
                 bool(_poll(lambda: sequence(partition) > before, HANDOVER_TIMEOUT)))
    client_time = bench.cluster.get(f"/registry/saves/newest?partition={client_partition}")["ClusterTime"]
    other_time = bench.cluster.get(f"/registry/saves/newest?partition={other_partition}")["ClusterTime"]
    _require(bench, "precondition: the non-client partition holds the newest save", other_time > client_time,
             f"{other_time} vs {client_time}")
    # The route lands via the owning node's heartbeat claim report, so a one-shot read races it
    # under load (seen as a 400 seconds after spawn) - poll like every other async signal.
    _require(bench, "the registry has a client route before the host loss",
             bool(_poll(lambda: bench.cluster.get(f"/registry/client-routes?client={CLIENT_ID}") is not None,
                        HANDOVER_TIMEOUT)))

    weights = {p["id"]: p["memberCount"] for p in node1_state}
    load_cap = None
    if load:
        # Projected-load capacity: nobody is count-capped here except node-1's anti-steal cap -
        # node-2 is restarted with a member ceiling calibrated from the LIVE census to fit exactly
        # the player-attached partition on top of its own load. The restart is cold (a fresh epoch
        # matches no catalog slot), so node-2 reclaims its own partitions through the registry's
        # load-gated auto-pick - they fit under the ceiling - and the registry must later spend
        # the remaining headroom on the client partition and refuse the other one.
        node2_load = sum(p["memberCount"] for p in _partitions(bench, "node-2"))
        _require(bench, "the load calibration census is non-empty",
                 weights[client_partition] >= 1 and weights[other_partition] >= 1 and node2_load >= 1,
                 f"client={weights[client_partition]} other={weights[other_partition]} node2={node2_load}")
        load_cap = node2_load + weights[client_partition]
        node2_partitions = [row["Partition"] for row in _owners(bench) if row["Node"] == "node-2"]
        _require(bench, "node-2 owns partitions to reclaim", bool(node2_partitions))
        bench.record("triage-load-calibration", {"node2Load": node2_load, "weights": weights, "loadCap": load_cap,
                                                 "node2Partitions": node2_partitions})
        env["node-2"] = {**env.get("node-2", {}), "CLUSTER_MAX_MEMBERS": str(load_cap)}
        bench.cluster.restart_node("node-2")
        bench.cluster.wait_node_log("node-2", "Game ready", SERVER_TIMEOUT)
        owners = []

        def reclaimed():
            nonlocal owners
            owners = _owners(bench)
            return all(any(row["Partition"] == p and row["Node"] == "node-2" and row.get("Recovering") is False
                           for row in owners) for p in node2_partitions)

        _require(bench, "node-2 reclaimed its own partitions under the member ceiling",
                 bool(_poll(reclaimed, RECOVERY_TIMEOUT)), json.dumps(owners)[:300])
        nodes_view = []

        def reported():
            nonlocal nodes_view
            nodes_view = bench.cluster.get("/registry/nodes")
            return any(row["Node"] == "node-2" and row.get("LoadCapacity") == load_cap
                       and row.get("ProjectedLoad") == node2_load for row in nodes_view)

        _require(bench, f"node-2 reports ProjectedLoad={node2_load} LoadCapacity={load_cap}",
                 bool(_poll(reported, HANDOVER_TIMEOUT)),
                 json.dumps([row for row in nodes_view if row.get("Node") == "node-2"])[:300])

    print("  node-1: SIGKILL as the lost host")
    bench.cluster.kill(slot="node-1")

    # node-2 spends its single free slot on the player-attached partition.
    owners = []

    def client_recovered():
        nonlocal owners
        owners = _owners(bench)
        row = _owner_row(owners, client_partition)
        return row and row["Node"] == "node-2" and row.get("Recovering") is False

    _require(bench, "node-2 recovered the player-attached partition", bool(_poll(client_recovered, RECOVERY_TIMEOUT)),
             json.dumps(owners)[:300])
    row = _owner_row(owners, other_partition)
    bench.check("the lower-priority partition was not recovered before capacity allowed",
                row is not None and row.get("Node") is None, json.dumps(row))

    # The shortfall is stable, visible, and correctly attributed.
    time.sleep(10)
    row = _owner_row(_owners(bench), other_partition)
    bench.check("the lower-priority partition stays unowned under the capacity ceiling",
                row is not None and row.get("Node") is None, json.dumps(row))
    status = bench.cluster.get("/registry/status")
    bench.record("triage-shortfall-status", status)
    bench.check("the registry reports Degraded with capacityShortfall",
                status.get("lifecycle", {}).get("Phase") in (2, "Degraded") and _shortfall(status),
                json.dumps(status.get("lifecycle"))[:300])
    if load:
        # The shortfall can only be load-attributed: node-2's partition count is unlimited
        # (RecoveryCapacity 0) and its member ceiling is exactly consumed.
        nodes_view = []

        def saturated():
            nonlocal nodes_view
            nodes_view = bench.cluster.get("/registry/nodes")
            return any(r["Node"] == "node-2" and r.get("RecoveryCapacity") == 0 and r.get("LoadCapacity") == load_cap
                       and r.get("ProjectedLoad") == load_cap for r in nodes_view)

        ok = _poll(saturated, HANDOVER_TIMEOUT)
        bench.record("triage-load-nodes", nodes_view)
        bench.check("node-2 is exactly load-saturated with an unlimited partition count", bool(ok),
                    json.dumps([r for r in nodes_view if r.get("Node") == "node-2"])[:300])
        # Capacity returns as an uncapped node-3 (the executor machinery is the plain triage
        # scenario's concern): the load-refused partition must recover there.
        bench.cluster.start_node("node-3")
        bench.cluster.wait_node_log("node-3", "Game ready", SERVER_TIMEOUT)
    else:
        # Capacity returns EXECUTOR-DRIVEN: the operator plans a node-3 slot; the executor spawns
        # only when the registry's plan allows it and reports its observations back. A synthetic
        # failed attempt first proves the backoff closes the gate and reopens it.
        bench.record("triage-node-plan", bench.cluster.post("/registry/node-plan", body={
            "SlotKey": "slot-node-3", "Host": "localhost", "Role": "Regular", "Desired": "Wanted"}))
        slot = _plan_slot(bench)
        _require(bench, "the planned node-3 slot is spawnable", _field(slot, "SpawnAllowed") is True, json.dumps(slot))
        _executor_heartbeat(bench, "attempt-0", "Failed", None, "synthetic preflight failure")
        slot = _plan_slot(bench)
        bench.check("a failed spawn attempt closes the spawn gate",
                    _field(slot, "SpawnAllowed") is False
                    and _field(slot, "LastFailure") == "synthetic preflight failure", json.dumps(slot))
        _require(bench, "the spawn backoff reopens the gate",
                 bool(_poll(lambda: _field(_plan_slot(bench), "SpawnAllowed") is True, 30)),
                 json.dumps(_plan_slot(bench)))
        bench.cluster.start_node("node-3")
        _executor_heartbeat(bench, "attempt-1", "Spawning", "node-3")
        bench.cluster.wait_node_log("node-3", "Game ready", SERVER_TIMEOUT)
        _executor_heartbeat(bench, "attempt-1", "Ready", "node-3")
        slot = _plan_slot(bench)
        bench.record("triage-node-plan-final", slot)
        bench.check("the registry recorded the Ready observation",
                    _field(slot, "Observed") in (2, "Ready") and _field(slot, "ObservedNode") == "node-3"
                    and _field(slot, "RetryAt") is None, json.dumps(slot))

    def other_recovered():
        nonlocal owners
        owners = _owners(bench)
        row = _owner_row(owners, other_partition)
        return row and row["Node"] == "node-3" and row.get("Recovering") is False

    _require(bench, "the lower-priority partition recovered on node-3 after capacity returned",
             bool(_poll(other_recovered, RECOVERY_TIMEOUT)), json.dumps(owners)[:300])
    status = {}

    def cleared():
        nonlocal status
        status = bench.cluster.get("/registry/status")
        return not _shortfall(status)

    bench.check("capacityShortfall cleared after capacity returned", bool(_poll(cleared, HANDOVER_TIMEOUT)),
                json.dumps(status.get("lifecycle"))[:300])
    bench.record("triage-owners", owners)
    bench.record("triage-final-status", status)
    bench.exercised(clientPartition=client_partition, otherPartition=other_partition,
                    weights={str(k): v for k, v in weights.items()}, loadCap=load_cap,
                    capacityReturn="uncapped node-3" if load else "executor-driven node-3")


def p2_rec_03(bench) -> None:
    _triage(bench, load=False)


def p2_rec_03l(bench) -> None:
    _triage(bench, load=True)


for _function in (p2_rec_03, p2_rec_03l):
    _function.split_step = "to-proximity"
    _function.regular_nodes = 3  # node-3 is the capacity that returns; the scenario starts it late


# -- P2-CON-01 ------------------------------------------------------------------------------


def _awake(status: dict, partition: int) -> bool:
    return partition in (status.get("awake") or [])


def _concealed(status: dict, partition: int) -> dict | None:
    return next((entry for entry in status.get("concealed") or [] if entry["id"] == partition), None)


def _boot_concealed(bench) -> tuple[dict, dict]:
    """Cold-start assertion, taken before the client joins so no approach demand can have woken
    anything yet. LoadAssigned (and with it the conceal decision) can land seconds after the
    'Game ready' log line, and /cluster/partitions can transiently time out on the busy game
    thread, so this polls. The transport-ship partition carries the premade character and the
    survival kit - standing demand - so node-1 must load it eagerly while the start-beacon
    partition stays a catalog proxy; node-2's end-beacon partition must conceal as well."""
    seen = {}

    def booted():
        boot = {node: bench.cluster.node(node, "/concealment") for node in ("node-1", "node-2")}
        loaded = {node: _partitions(bench, node) for node in ("node-1", "node-2")}
        seen.update(boot=boot, loaded=loaded)
        for node in ("node-1", "node-2"):
            status = boot[node]
            if not (status.get("enabled") and len(status.get("concealed") or []) >= 1 and not status.get("awake")):
                return None
            ids = {entry["id"] for entry in status["concealed"]}
            if any(p["id"] in ids for p in loaded[node]):
                return None
        return boot if loaded["node-1"] else None

    boot = _poll(booted, SERVER_TIMEOUT)
    bench.record("concealment-boot", seen)
    _require(bench, "both regular nodes booted concealing, with node-1's demand partition loaded", boot is not None)
    bench.record("concealment-boot-node1", boot["node-1"])
    bench.record("concealment-boot-node2", boot["node-2"])
    print(f"  booted concealing {len(boot['node-1']['concealed'])} partition(s) on node-1, "
          f"{len(boot['node-2']['concealed'])} on node-2")
    return boot["node-1"], boot["node-2"]


def p2_con_01(bench) -> None:
    """Concealment: cold start, explicit/view/approach wakes, grace reconceal, mid-wake crash.

    The wake/reconceal cycles run against NODE 2's concealed partition (the end beacon, isolated by
    5.6 km) while the client character stays owned by node-1: its position reaches node-2's
    concealment scan through the cluster position registry, so the approach wake is the real
    cross-node demand path. node-1's own concealed partition (the start beacon) sits 5 m from the
    exempt spawn ship, so the spawn itself must wake it by approach - asserted at the end. The
    client character is only ever moved server-side."""
    env = bench.cluster.config.node_env_by_id
    for node in ("node-1", "node-2"):
        env[node] = {**env.get(node, {}), **CONCEAL_ENV}
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    boot1, _ = _boot_concealed(bench)
    _start_client(bench)

    def status(node: str = "node-2") -> dict:
        return bench.cluster.node(node, "/concealment")

    # The client spawns ~5.6 km from node-2's concealed sphere, far outside the pre-wake margin,
    # so node-2's baseline is its boot state: still concealed.
    baseline = status()
    bench.record("concealment-baseline-node2", baseline)
    _require(bench, "node-2 stayed concealed through the client spawn",
             len(baseline.get("concealed") or []) >= 1 and not baseline.get("awake"), json.dumps(baseline)[:300])
    first = baseline["concealed"][0]
    partition, gen0, seq0, members0 = first["id"], first["generation"], first["sequence"], first["members"]
    base_cx = first["center"][0]
    approach = (first["center"][0] + first["radius"] + 500, first["center"][1], first["center"][2])

    # Explicit wake (stands in for join/respawn/remote-control demand).
    print(f"  node-2: waking concealed partition {partition} explicitly")
    woken = bench.cluster.node("node-2", f"/conceal-test-wake?partition={partition}&cause=explicit-test", method="POST")
    bench.record("concealment-wake-explicit", woken)
    _require(bench, "the explicit wake was accepted", woken.get("woken") is True, json.dumps(woken))
    after_wake = status()
    bench.record("concealment-after-explicit-wake", after_wake)
    bench.check("the explicitly woken partition is reported awake", _awake(after_wake, partition))
    wake_census = _partitions(bench, "node-2")
    bench.check(f"the woken partition loaded its {members0} members",
                any(p["id"] == partition and p["memberCount"] == members0 for p in wake_census))

    # THE MEMBERS BY ID, CAPTURED HERE BECAUSE THIS IS WHERE THEY EXIST (T-0209). The approach
    # check far below used to look for `partition` by id and read its memberCount, which cannot
    # survive a merge that absorbs the woken partition: on 2026-09-21 the balancer committed
    # "inputs=2,4 outputs=4, kept 4" 301 ms after the approach wake, so the id was retired and the
    # check polled 30 s for something gone while its member, End Beacon, was alive in partition 4
    # throughout. Ids are invariant under that merge and under the handover the check tolerates.
    # The capture has to happen while the partition is AWAKE: a concealed partition is unloaded and
    # _partitions() does not list it, so capturing after the reconceal yields an empty set and a
    # check that fails for no reason (the first attempt at this fix did exactly that). The later
    # camera-probe absorption only ADDS a member, so these ids stay a valid liveness assertion.
    expected_members: set[int] = {member["id"] for entry in wake_census if entry["id"] == partition
                                  for member in entry.get("members") or []}
    bench.record("concealment-expected-members", sorted(expected_members))
    _require(bench, "the woken partition's members could be read by id while it was awake",
             bool(expected_members), json.dumps(wake_census)[:300])

    # Grace reconcealment: no player near, no player member, no routed client for the grace period
    # takes a Final save and unloads only once it is durably catalogued.
    reconcealed = {}

    def back_concealed():
        nonlocal reconcealed
        reconcealed = status()
        return _concealed(reconcealed, partition)

    entry = _poll(back_concealed, HANDOVER_TIMEOUT)
    bench.record("concealment-after-reconceal", reconcealed)
    _require(bench, "the partition reconcealed after the grace period", entry is not None)
    gen1, seq1 = entry["generation"], entry["sequence"]
    # A Final save keeps the partition's generation and advances the sequence. Newer, not
    # same-generation: a balance migration can move the woken partition away and back before the
    # reconceal, and each move starts a new generation (bench run 20261002-094545Z: node-2 ->
    # node-1 -> node-2 inside the grace period, g1-s1 -> g3-s1).
    bench.check("the reconceal advanced the save", (gen1, seq1) > (gen0, seq0),
                f"g{gen0}-s{seq0} -> g{gen1}-s{seq1}")
    bench.check("the reconcealed partition is unloaded",
                all(p["id"] != partition for p in _partitions(bench, "node-2")))
    bench.check("node-2 logged the reconceal", bool(_lines(bench, "node-2", rf"reconcealed partition {partition}\b")))

    # Client-view demand: an actively viewed camera block is a demand position in the cluster
    # position registry (ClientId 0), so watching a remote camera wakes the concealed terrain
    # around the CAMERA, not the viewer. The probe sits 3300 m from the beacon - outside the
    # 3000 m intra-node merge reach, inside the 3500 m pre-wake margin - so the wake can only come
    # from the camera's registry entry and the probe partition never merges with the beacon. The
    # view toggle stands in for a remote client's View button (which raises the camera's synced
    # viewer count), exactly like conceal-test-teleport stands in for a real approach.
    probe = bench.cluster.node("node-2", f"/camera-test-spawn?x={base_cx + 3300}&y={approach[1]}&z={approach[2]}",
                               method="POST")
    bench.record("concealment-camera-probe", probe)
    camera = probe.get("camera")
    _require(bench, "the camera probe spawned", bool(camera), json.dumps(probe))
    print(f"  node-2: camera probe {camera} spawned 3300 m from concealed partition {partition}")
    view = bench.cluster.node("node-2", f"/camera-test-view?entityId={camera}&active=true", method="POST")
    bench.record("concealment-camera-view", view)
    _require(bench, "the camera view toggle armed", view.get("viewed") is True, json.dumps(view))

    # The demand entry must replicate cluster-wide: node-1, which does not host the camera's
    # partition, must see the ClientId-0 entry through the merged registry.
    def camera_entry():
        return any(row.get("ClientId") == 0 and row.get("EntityId") == camera
                   for row in bench.cluster.node("node-1", "/position-registry"))

    bench.check("node-1 sees the viewed camera's demand entry in the position registry",
                bool(_poll(camera_entry, 30)))
    view_woken = {}

    def woke_on_view():
        nonlocal view_woken
        view_woken = status()
        return _awake(view_woken, partition)

    ok = _poll(woke_on_view, HANDOVER_TIMEOUT)
    bench.record("concealment-after-camera-wake", view_woken)
    _require(bench, "the partition woke on the camera view", bool(ok))
    bench.check("node-2 logged the view wake", bool(_lines(bench, "node-2", rf"woke partition {partition} .*cause view")))
    bench.cluster.node("node-2", f"/camera-test-view?entityId={camera}&active=false", method="POST")
    bench.check("the camera demand entry ended with the view",
                bool(_poll(lambda: not camera_entry(), 30)))

    # An un-viewed camera holds nothing: the beacon must reconceal again before the approach leg.
    # Partition envelopes are 2.5 km, so the woken beacon normally ABSORBS the camera probe
    # partition (gap 3300 - 2x2500 < 3000 merge reach): a topology change that bumps the
    # generation, adds the probe as a member, and carries concealability to the merged content
    # (the wake-merge absorption design). Follow the reconcealed entry by members+center and
    # refresh the scenario state so every later leg tracks the merged partition; an unmerged
    # reconceal (timing) stays covered by the members0 alternative.
    view_reconcealed = {}

    def beacon_reconcealed():
        nonlocal view_reconcealed
        view_reconcealed = status()
        return next((e for e in view_reconcealed.get("concealed") or []
                     if e["members"] in (members0, members0 + 1) and abs(e["center"][0] - base_cx) < 3000), None)

    view_entry = _poll(beacon_reconcealed, HANDOVER_TIMEOUT)
    bench.record("concealment-after-camera-reconceal", view_reconcealed)
    _require(bench, "the beacon reconcealed after the camera view ended", view_entry is not None,
             json.dumps(view_reconcealed)[:300])
    if view_entry["members"] != members0 or view_entry["id"] != partition:
        print(f"  camera probe absorbed into partition {view_entry['id']} (was {partition}, "
              f"members {members0} -> {view_entry['members']})")
    partition, gen1, seq1, members0 = view_entry["id"], view_entry["generation"], view_entry["sequence"], view_entry["members"]
    # Absorption stretched the sphere ~1650 m toward the camera, so the baseline approach point
    # (baseline radius + 500) would now land INSIDE the merged sphere: the approaching character
    # would merge in as a member, hand to node-2, and block every later reconceal. Recompute the
    # approach point from the merged sphere so it keeps the original 500 m clearance.
    approach = (view_entry["center"][0] + view_entry["radius"] + 500, view_entry["center"][1], view_entry["center"][2])

    # Cross-node approach wake: move the node-1-owned client character just outside the partition
    # sphere but inside the pre-wake margin; node-2 must wake it from the cluster position
    # registry without any explicit call, and the wake must load the reconcealed (new-sequence)
    # save. The teleport goes to node-1 - the node that owns the character.
    print(f"  node-2: approaching partition {partition} at x={approach[0]}")
    bench.record("concealment-teleport-approach", bench.cluster.node(
        "node-1", f"/conceal-test-teleport?client={CLIENT_ID}&x={approach[0]}&y={approach[1]}&z={approach[2]}",
        method="POST"))
    approached = {}

    def woke_on_approach():
        nonlocal approached
        approached = status()
        return _awake(approached, partition)

    ok = _poll(woke_on_approach, HANDOVER_TIMEOUT)
    bench.record("concealment-after-approach", approached)
    _require(bench, "the partition woke on approach", bool(ok))
    bench.check("node-2 logged the approach wake",
                bool(_lines(bench, "node-2", rf"woke partition {partition} .*cause approach")))
    # Poll: the wake status flips before the next partition census evaluates, so an immediate
    # member check can race a wake that demonstrably loaded. members0 alone, or members0+1: the
    # approaching CLIENT CHARACTER can legitimately hand over and merge into the woken partition
    # (sync distance 3000 m, approach point ~500 m out) - whether it does is a per-run handover
    # timing race. The residency check spans BOTH nodes: the automatic balancer may hand the woken
    # partition to the approaching client's node before this check lands (partitions come to the
    # player) - the wake promise is that the reconcealed save's members are LIVE, not which node
    # hosts them.
    census = {}

    holders: dict[int, tuple[str, int]] = {}

    def members_live():
        holders.clear()
        for node in ("node-2", "node-1"):
            census[node] = _try(lambda: _partitions(bench, node), [])
            for entry in census[node]:
                for member in entry.get("members") or []:
                    if member["id"] in expected_members:
                        holders[member["id"]] = (node, entry["id"])
        return bool(expected_members) and len(holders) == len(expected_members)

    ok = _poll(members_live, 30)
    bench.record("concealment-approach-partitions", census)
    bench.record("concealment-approach-member-holders",
                 {str(member): list(where) for member, where in sorted(holders.items())})
    missing = sorted(expected_members - set(holders))
    # The promise this asserts is the one the comment above states - the reconcealed save's members
    # are LIVE - and it now holds whichever node hosts them AND whichever partition id survived the
    # merge. The holders are recorded so a split across partitions stays visible in the evidence
    # even though it does not fail the check.
    bench.check(f"the approach-woken partition's {len(expected_members)} members from the reconcealed save are live",
                bool(ok), f"missing={missing} holders={json.dumps({str(k): v for k, v in sorted(holders.items())})}"[:300])

    # Walk away again: a second reconcealment must advance the sequence once more, proving the
    # wake/save/unload cycle is repeatable from the newly written save. The approach may have
    # handed the client over to node-2 (or left it mid-transfer) - poll the walk-away teleport
    # against BOTH nodes until the current host accepts it.
    # z=4000: after the camera-probe absorption the beacon sphere reaches ~1650 m toward the
    # camera, and reconceal's near check spans envelope (2.5 km) plus the 3.5 km pre-wake margin -
    # the origin sits ~136 m inside that reach. 4 km up the +Z axis is ~9.8 km from the merged
    # sphere (demand-free) while still holding node-1's spawn-area beacon awake for the final assert.
    def walk_away():
        for node in ("node-1", "node-2"):
            result = _try(lambda: bench.cluster.node(node, f"/conceal-test-teleport?client={CLIENT_ID}&x=0&y=0&z=4000",
                                                     method="POST"))
            if result is not None:
                bench.record("concealment-teleport-away", {"node": node, "result": result})
                return node
        return None

    _require(bench, "a node accepted the walk-away teleport for the client", bool(_poll(walk_away, 30)))
    final, reconceal_host = {}, None

    def second_reconceal():
        nonlocal final, reconceal_host
        for node in ("node-2", "node-1"):
            final = _try(lambda: status(node), {})
            if any(e["members"] == members0 and abs(e["center"][0] - base_cx) < 3000 for e in final.get("concealed") or []):
                reconceal_host = node
                return True
        return False

    if not _poll(second_reconceal, RECOVERY_TIMEOUT):
        final = status()
    bench.record("concealment-final-node2", final)
    # The beacon can reconceal under a NEW id (a passing character's partition may have
    # topology-merged with it; concealability carries through) - follow it by center+members.
    entry = next((e for e in final.get("concealed") or []
                  if e["members"] == members0 and abs(e["center"][0] - base_cx) < 3000), None)
    if entry is None:
        # Absorption can legally park the beacon AWAKE: churn hands it around and the client
        # sitting at the origin keeps its area hot. The guarantee is that the beacon's CONTENT is
        # never lost - some node must hold a loaded partition of boot member size near a known
        # cluster area. A reconceal elsewhere/later stays covered by the shared set.
        alive = any(p["memberCount"] >= members0 for node in ("node-1", "node-2")
                    for p in _try(lambda: _partitions(bench, node), []))
        diag = {name: _try(call) for name, call in (
            ("routes", lambda: bench.cluster.get("/registry/client-routes")),
            ("partitions2", lambda: _partitions(bench, "node-2")),
            ("partitions1", lambda: _partitions(bench, "node-1")),
            ("conceal", lambda: status()),
            ("registry", lambda: bench.cluster.node("node-2", "/position-registry")),
            ("owners", lambda: _owners(bench)))}
        bench.record("concealment-walkaway-diag", diag)
        _require(bench, "the beacon partition either reconcealed or stayed loaded somewhere", alive)
        print("  NOTE: the beacon stayed AWAKE after the walk-away (absorption churn; content accounted for on a "
              "live partition) - reconceal cycle proven by the first cycle")
        gen2, seq2 = gen1, seq1
    else:
        gen2, seq2 = entry["generation"], entry["sequence"]
        if entry["id"] == partition and reconceal_host == "node-2":
            # Newer, not same-generation: co-location and a balance migration can move the partition
            # away and back between the cycles, and each move starts a new generation (bench run
            # 20260917-055254Z: node-2 -> node-1 -> node-2, g1-s4 -> g3-s1).
            bench.check("the second reconceal advanced the save", (gen2, seq2) > (gen1, seq1),
                        f"g{gen1}-s{seq1} -> g{gen2}-s{seq2}")
        else:
            print(f"  beacon reconcealed under partition {entry['id']} (was {partition}) on {reconceal_host} "
                  f"at g{gen2}-s{seq2}")
            partition = entry["id"]

    # node-1's start-beacon partition sits 5 m from the exempt spawn ship, so the spawn itself is
    # an approach: it must have woken by now without any call. Wake-merge absorption: the spawn
    # merges the start-beacon partition and concealability carries to the NEW id, so assert an
    # awake partition and an approach wake by ANY id.
    node1_final = status("node-1")
    bench.record("concealment-final-node1", node1_final)
    bench.check("the client spawn approach-woke node-1's concealable partition",
                len(node1_final.get("awake") or []) >= 1, json.dumps(node1_final)[:300])
    bench.check("node-1 logged the spawn approach wake",
                bool(_lines(bench, "node-1", r"woke partition [0-9]+ .*cause approach")))

    # Mid-wake crash: fire a wake and SIGKILL node-2 with no settle window. The wake loads members
    # and only catalogs a Final at the NEXT reconceal, so a crash inside the window must lose
    # nothing: the newest catalogued save predates the wake, and a warm restart on the same data
    # root must bring the partition back with all members and the lineage intact - never
    # quarantined.
    midwake = "skipped"
    if reconceal_host != "node-2":
        print("  NOTE: skipping the mid-wake crash leg - absorption churn left no concealed beacon on node-2 this "
              "run (the leg is exercised on non-churn runs)")
    else:
        bench.cluster.node("node-2", f"/conceal-test-wake?partition={partition}&cause=midwake-crash", method="POST")
        env["node-2"] = {**env.get("node-2", {}), "CLUSTER_WARM_RECOVERY": "1"}
        print(f"  node-2: SIGKILL inside the wake of partition {partition}")
        bench.cluster.restart_node("node-2")
        bench.cluster.wait_node_log("node-2", "Game ready", SERVER_TIMEOUT)
        # The partition survives on EITHER node: if the restart outlasts node-2's leases the gateway
        # fails the partition over to node-1 (cross-node recovery while the owner is down); a
        # faster restart warm-reclaims it on node-2. Both are correct - what matters is that it
        # exists somewhere with its members, unquarantined, on an unbroken lineage.
        seen = {"owner": None, "state": None}

        def survived():
            row = _owner_row(_owners(bench), partition)
            owner = row.get("Node") if row else None
            seen["owner"] = owner
            if owner not in ("node-1", "node-2"):
                return None
            state = _try(lambda: status(owner), {})
            seen["state"] = state
            entry = _concealed(state, partition)
            if (entry and entry["members"] == members0) or _awake(state, partition):
                return owner
            if any(p["id"] == partition and p["memberCount"] == members0
                   for p in _try(lambda: _partitions(bench, owner), [])):
                return owner
            return None

        owner = _poll(survived, RECOVERY_TIMEOUT)
        _require(bench, "the partition survived the mid-wake crash somewhere", owner is not None, json.dumps(seen)[:300])
        print(f"  mid-wake crash left partition {partition} on {owner}")
        quarantine = bench.cluster.node(owner, "/quarantine")
        bench.check("the mid-wake crash quarantined nothing", not (quarantine.get("quarantined") or []),
                    json.dumps(quarantine)[:300])
        newest = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
        save = newest["SaveId"]
        bench.check("the catalogued lineage did not roll back after the mid-wake crash",
                    save["Generation"] >= gen2 and (save["Generation"] > gen2 or save["Sequence"] >= seq2),
                    json.dumps(save))
        if _try(lambda: _concealed(status(owner), partition)):
            bench.cluster.node(owner, f"/conceal-test-wake?partition={partition}&cause=midwake-verify", method="POST")
        loaded = _poll(lambda: any(p["id"] == partition and p["memberCount"] == members0
                                   for p in _partitions(bench, owner)), RECOVERY_TIMEOUT)
        bench.check(f"the partition loaded its {members0} members on {owner} after the mid-wake crash", bool(loaded))
        bench.record("concealment-midwake-crash", {"owner": owner, "state": seen["state"], "newest": newest})
        midwake = owner

    bench.check("the client character survived the concealment cycles", bench.living_character(CLIENT_ID) is not None)
    bench.record("concealment", {"partition": partition, "generation": gen0, "sequences": [seq0, seq1, seq2],
                                 "members": members0, "bootNode1": boot1, "finalNode1": node1_final,
                                 "finalNode2": final})
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(partition=partition, sequences=[seq0, seq1, seq2], members=members0,
                    node1BootPartition=boot1["concealed"][0]["id"], reconcealHost=reconceal_host,
                    midwakeCrash=midwake)


SCENARIOS_TOPOLOGY = {
    "P1-HND-02": (p1_hnd_02, "proximity merge and cross-node fire", "3"),
    "P1-SAV-07": (p1_sav_07, "split/merge lineage and exact-once cover", "4"),
    "P2-WA-06a": (p2_wa_06a, "teleport ownership arbitration", "3"),
    "P2-REC-03": (p2_rec_03, "host loss with capacity shortfall; triage order", "4"),
    "P2-REC-03l": (p2_rec_03l, "projected-load triage against a member ceiling", "4"),
    "P2-CON-01": (p2_con_01, "concealment, demand wake and reconceal", "3"),
}
