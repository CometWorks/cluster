#!/usr/bin/env python3
"""The Phase 1-3 scenarios added to TestPlan.md on 2026-07-27, as runnable Python.

Each function takes a :class:`bench.Bench` and asserts through it. No AI inference anywhere: every
verdict is a comparison against structured state from ``diag``, the registry, the event log, or a
server-side movement measurement.

Registered in :data:`SCENARIOS`; run them with ``run_suite.py``.
"""

from __future__ import annotations

import datetime
import json
import math
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from cluster import ClusterError, wait_until  # noqa: E402
from scenarios_core import _lines  # noqa: E402
from scenarios_offline import _put_config  # noqa: E402

CLIENT_ID = 1234567890
CLIENT2_ID = 1234567891


def _partition_of(section: dict, entity_id: int) -> int | None:
    for partition in section["partitions"]:
        for entity in partition["entities"] or []:
            if entity["entityId"] == entity_id:
                return partition["partitionId"]
    return None


def _all_partitions(bench) -> dict[int, str]:
    """partition id -> owning node, from the registry (transactional, class E)."""
    return {int(row["Partition"]): row["Node"] for row in bench.cluster.get("/registry/partitions")
            if row.get("Node")}


# ---------------------------------------------------------------------------
# P1-INF-01 - the assertion surface must be trustworthy before anything asserts through it
# ---------------------------------------------------------------------------

def p1_inf_01(bench) -> None:
    bench.start_cluster()
    fixture = bench.quiesce()
    bench.record("fixture-partitions", fixture)

    # Depth contract: `partitions` must not carry entity arrays, `entities` must.
    shallow = bench.diag("partitions")
    deep = bench.diag("entities")
    bench.record("diag-partitions", shallow)
    bench.record("diag-entities", deep)
    shallow_sections = bench.sections(shallow)
    bench.check("depth=partitions returns no entity arrays (cost contract)",
                all(partition["entities"] is None
                    for section in shallow_sections.values() for partition in section["partitions"]))
    deep_sections = bench.sections(deep)
    bench.check("depth=entities returns entity rows",
                any(partition["entities"] for section in deep_sections.values() for partition in section["partitions"]))

    # The fixture: node-1 holds the start beacon, the transport and a character; node-2 the end beacon.
    named = {entity["displayName"]: node
             for node, section in deep_sections.items()
             for partition in section["partitions"] for entity in partition["entities"] or []}
    bench.check("fixture layout: Start Beacon on node-1", named.get("Start Beacon") == "node-1", str(named))
    bench.check("fixture layout: End Beacon on node-2", named.get("End Beacon") == "node-2", str(named))

    # Registry-sourced fields agree with `registry dump` (class E always).
    registry_owners = _all_partitions(bench)
    node_owners = {partition["partitionId"]: node
                   for node, section in deep_sections.items() for partition in section["partitions"]}
    agree = {pid: node for pid, node in node_owners.items() if registry_owners.get(pid) == node}
    bench.check("registry-sourced ownership matches the node sections",
                agree.keys() == node_owners.keys(),
                f"registry={registry_owners} nodes={node_owners}")

    # Consistency stamps: simFrameCounter advances between snapshots, and every section carries one.
    first = {node: section["simFrameCounter"] for node, section in deep_sections.items()}
    time.sleep(2)
    later_sections = bench.sections(bench.diag("partitions"))
    advanced = {node: (first[node], later_sections[node]["simFrameCounter"])
                for node in first if node in later_sections}
    bench.check("per-node simFrameCounter advances between snapshots",
                all(after > before for before, after in advanced.values()), str(advanced))
    bench.check("every section carries capturedAt",
                all(section.get("capturedAt") is not None for section in deep_sections.values()))

    # Pagination: the union of pages equals the unfiltered set, with no duplicates.
    def entity_ids(snapshot):
        return [entity["entityId"] for section in bench.sections(snapshot).values()
                for partition in section["partitions"] for entity in partition["entities"] or []]

    unpaged = sorted(entity_ids(deep))
    paged, cursor, pages = [], 0, 0
    while pages < 50:
        page = bench.diag("entities", limit=1, cursor=cursor)
        got = entity_ids(page)
        paged += got
        pages += 1
        cursors = [section.get("nextCursor") for section in bench.sections(page).values()
                   if section.get("nextCursor") is not None]
        if not cursors:
            break
        cursor = max(cursors)
    bench.record("diag-pagination", {"unpaged": unpaged, "paged": sorted(paged), "pages": pages})
    bench.check("pagination is complete and duplicate-free",
                sorted(paged) == unpaged and len(paged) == len(set(paged)),
                f"{len(paged)} paged vs {len(unpaged)} unpaged over {pages} pages")

    # Filters.
    a_partition = next(iter(node_owners))
    filtered = bench.diag("entities", partition=a_partition)
    listed = {partition["partitionId"] for section in bench.sections(filtered).values()
              for partition in section["partitions"]}
    bench.check("partition filter narrows the snapshot", listed == {a_partition}, str(listed))

    # A dead node is reported stale with its heartbeat age, never fabricated or omitted.
    bench.cluster.kill("node-2")
    stale = wait_until(lambda: next((node for node in bench.diag("partitions")["nodes"]
                                     if node["nodeId"] == "node-2" and node["stale"]), None),
                       90, "node-2 to be reported stale")
    bench.record("diag-node-killed", stale)
    bench.check("a killed node is reported stale, not omitted", stale["stale"] is True,
                f"lastHeartbeatAgeMs={stale['lastHeartbeatAgeMs']:.0f} error={stale['sectionError']}")
    bench.check("the killed node's section is absent rather than invented", stale["section"] is None)

    bench.exercised(depths=["partitions", "entities"], pages=pages, killedNode="node-2")


# ---------------------------------------------------------------------------
# P1-INF-02 - the speed-up machinery must be behavior-equivalent to the scheduler
# ---------------------------------------------------------------------------

def p1_inf_02(bench) -> None:
    bench.start_cluster()
    bench.quiesce()

    outcomes = {}
    for task in ("split-sweep", "membership-eval", "save", "conceal-eval", "balance",
                 "catalog-gc", "rotation-check"):
        outcomes[task] = bench.cluster.trigger(task, wait=True)
    bench.record("trigger-outcomes", outcomes)
    # Name the failing task and its per-node errors: the joined-keys detail hid WHICH trigger
    # errored (the 2026-07-28 sweep failure read as a list of all seven names).
    trigger_failures = {task: [row for row in outcome.get("nodes", []) if "error" in row]
                        for task, outcome in outcomes.items()}
    trigger_failures = {task: rows for task, rows in trigger_failures.items() if rows}
    bench.check("every documented trigger runs and answers",
                not trigger_failures, str(trigger_failures) if trigger_failures else "all answered")

    # Same code path: a triggered membership pass and the scheduler's own 1 Hz pass converge on the
    # same partitioning from the same state. The trigger cannot invent a partitioning the scheduler
    # would not reach, which is the property the equivalence assertion is protecting.
    triggered = bench.cluster.trigger("membership-eval", wait=True)
    triggered_sets = {row["node"]: sorted(row["result"]["partitions"]) for row in triggered["nodes"]
                      if "result" in row}
    time.sleep(3)   # let the scheduler run several of its own passes
    scheduled_sets = {node: sorted(partition["partitionId"] for partition in section["partitions"])
                      for node, section in bench.sections(bench.diag("partitions")).items()}
    bench.record("trigger-vs-scheduler", {"triggered": triggered_sets, "scheduled": scheduled_sets})
    bench.check("a triggered pass and the scheduler agree on the partitioning",
                all(triggered_sets.get(node) == partitions or not partitions
                    for node, partitions in scheduled_sets.items()),
                f"triggered={triggered_sets} scheduled={scheduled_sets}")

    # Idempotence: re-triggering while one is running joins it rather than stacking a second run. The
    # node runs triggers on the game thread, so a second POST cannot interleave - assert it returns
    # cleanly and leaves the partitioning unchanged rather than doubling it.
    before = scheduled_sets
    for _ in range(3):
        bench.cluster.trigger("split-sweep")
    after = {node: sorted(partition["partitionId"] for partition in section["partitions"])
             for node, section in bench.sections(bench.diag("partitions")).items()}
    bench.check("re-triggering does not stack runs", before == after, f"{before} -> {after}")

    # Policy still applies: --unthrottled is refused outside the debug frontend, and accepted inside it.
    unthrottled = bench.cluster.trigger("balance", unthrottled="true")
    bench.check("--unthrottled is honored on the debug frontend", unthrottled.get("unthrottled") is True,
                str(unthrottled))

    bench.exercised(triggers=sorted(outcomes))


# ---------------------------------------------------------------------------
# P1-MEM-05 - the split sweep, including the case no member moved
# ---------------------------------------------------------------------------

def p1_mem_05(bench) -> None:
    bench.start_cluster()
    bench.quiesce()
    control = "127.0.0.1:29417"
    # T-0319: the default split distance is 4 x D (12 km); the chain's ends below are 6 km apart and
    # this scenario's subject is the no-movement sweep, not the distance - pin the pre-T-0319 5000 m.
    bench.record("config-node-1", _put_config(bench, "node-1", {"partition.splitDistance": "5000"}))

    # A chain far from everything else: A - bridge - B, each hop inside the D + m merge reach (3000 +
    # 1000) so the three aggregate into one partition, but the ends 6 km apart, which is beyond the
    # D + 2m split radius once the bridge is gone.
    base = (500_000.0, 0.0, 0.0)
    positions = {"a": base, "bridge": (base[0] + 3000, 0.0, 0.0), "b": (base[0] + 6000, 0.0, 0.0)}
    spawned = {}
    for name, (x, y, z) in positions.items():
        spawned[name] = bench.cluster.node("node-1", f"/trash-test-spawn?x={x}&y={y}&z={z}&static=true", "POST")
    bench.record("fixture-chain", spawned)

    holders_seen = {}

    def aggregated():
        section = bench.sections(bench.diag("entities"))["node-1"]
        # Record per-grid holders so a timeout says WHICH grid is unassigned or apart -
        # "last=None" distinguished nothing when this timed out under sweep load 2026-07-28.
        holders_seen.update({name: _partition_of(section, spawned[name]["grid"]) for name in positions})
        holders = set(holders_seen.values())
        return holders.pop() if len(holders) == 1 and None not in holders else None

    # 120 s, not 60: the merge sweep runs on its own cadence and the box runs lanes back to
    # back during sweeps - this scenario passed in isolation and timed out only under load.
    try:
        merged = wait_until(aggregated, 120, "the three probes to merge into one partition")
    except ClusterError:
        merged = None
    bench.check("the chain aggregates into one partition", merged is not None,
                f"partition {merged}" if merged is not None else f"holders after 120s: {holders_seen}")
    if merged is None:
        return

    # The no-movement case: destroy the bridge while both halves stay exactly where they are. The
    # incremental, dirty-entity-only pass cannot see this; only the sweep can.
    bench.cluster.node("node-1", f"/test-close-entity?entity={spawned['bridge']['grid']}", "POST")

    def split():
        section = bench.sections(bench.diag("entities"))["node-1"]
        left = _partition_of(section, spawned["a"]["grid"])
        right = _partition_of(section, spawned["b"]["grid"])
        return (left, right) if left and right and left != right else None

    outcome = wait_until(split, 90, "the aggregate to split with no member moving")
    bench.record("split-outcome", {"merged": merged, "children": outcome})
    bench.check("a partition splits when the bridge is destroyed and nothing moves",
                outcome is not None, f"{merged} -> {outcome}")

    # No flapping against the merge threshold: the children stay separate.
    time.sleep(10)
    still = split()
    bench.check("the split does not flap back into a merge", still is not None and still == outcome,
                f"{outcome} -> {still}")

    # Locked partitions are skipped and caught next cycle: a frozen partition is reported locked and
    # the sweep leaves it alone.
    locked = [partition for section in bench.sections(bench.diag("partitions")).values()
              for partition in section["partitions"] if partition["locked"]]
    bench.check("no partition is left locked in steady state", not locked, str(locked))

    bench.exercised(chain=list(positions), mergedPartition=merged, children=outcome)


# ---------------------------------------------------------------------------
# P1-CHR-01 - a login or reconnect never duplicates a character
# ---------------------------------------------------------------------------

def p1_chr_01(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    first = bench.quiesce()
    bench.standing_audits(first, "after first join")
    bench.assert_move_probe(CLIENT_ID, "after-join")

    entity = bench._server_character(CLIENT_ID)
    bench.check("the server reports exactly one character for the client", entity is not None, str(entity))
    identity = _identity_of(bench, CLIENT_ID)
    bench.record("first-join", {"characterEntityId": entity, "identityId": identity})

    # The abuse leg: a second connect for the same identity while the first session is live must be
    # refused - never both proceeding (Gateway.md 7.1 single-flight).
    before = bench.cluster.get("/transport/status")["counters"]["singleFlightRefusals"]
    duplicate = bench.launch_client(CLIENT_ID, remote_port=24160, label="duplicate")
    refused = wait_until(
        lambda: bench.cluster.get("/transport/status")["counters"]["singleFlightRefusals"] > before,
        180, "the duplicate join to be refused")
    bench.check("a second join for a live identity is refused", bool(refused),
                f"singleFlightRefusals {before} -> "
                f"{bench.cluster.get('/transport/status')['counters']['singleFlightRefusals']}")
    bench.kill_client("duplicate")      # the duplicate CLIENT only - the cluster is still under test
    after_duplicate = bench.quiesce()
    bench.record("after-duplicate-join", after_duplicate)
    bench.standing_audits(after_duplicate, "after the refused duplicate join")
    bench.check("the original client still holds the same character",
                bench._server_character(CLIENT_ID) == entity,
                f"{entity} -> {bench._server_character(CLIENT_ID)}")

    # Relog: LEAVE (gracefully - what a player does) and come back, and require the SAME body rather
    # than a fresh one. Deliberately not a SIGKILL: Direct Transport detects a dead peer through
    # LiteNetLib's 120 s DisconnectTimeout, so a killed client's session lingers that long and
    # single-flight then refuses its own return. That window is a real characteristic, recorded in
    # Findings/2026-07-27-killed-client-holds-its-session.md, and it is not what "leave" means.
    try:
        bench.apis[CLIENT_ID].exit_game()
    except Exception:
        pass
    bench.apis.pop(CLIENT_ID).close()
    bench.kill_client(CLIENT_ID)
    wait_until(lambda: CLIENT_ID not in [session["clientId"]
                                         for session in bench.cluster.get("/diag?depth=cluster")["clients"]],
               180, "the first session to drop")
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    rejoined = bench._server_character(CLIENT_ID)
    bench.check("the rejoining client attaches to the existing character", rejoined == entity,
                f"{entity} -> {rejoined}")
    bench.assert_move_probe(CLIENT_ID, "after-relog")
    bench.screenshot(CLIENT_ID, "02-after-relog")
    bench.standing_audits(bench.quiesce(), "after the relog")
    bench.exercised(identityId=identity, characterEntityId=entity, duplicateJoinRefused=bool(refused),
                    rejoinedCharacterEntityId=rejoined)


def _identity_of(bench, client_id: int) -> int | None:
    for section in bench.sections(bench.diag("partitions")).values():
        for client in section["clients"]:
            if client["clientId"] == client_id:
                return client["identityId"]
    return None


# ---------------------------------------------------------------------------
# P1-CHR-02 - on-foot cross-node handover: no duplicate, settings intact, controls live
# ---------------------------------------------------------------------------

def p1_chr_02(bench) -> None:
    bench.start_cluster()
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    bench.assert_move_probe(CLIENT_ID, "after-spawn")

    entity = bench._server_character(CLIENT_ID)
    identity = _identity_of(bench, CLIENT_ID)
    before_settings = _settings(bench, identity)
    bench.record("settings-before", before_settings)
    start_node = _node_of_entity(bench, entity)
    start_partition = _partition_of(bench.sections(bench.diag("entities"))[start_node], entity)

    # Cross the boundary by moving the CHARACTER to the other node's region - Clustering §6.4's
    # direction rule, "the character moves, not the world", and the one mechanism that actually
    # relocates a lone character:
    #   * flying does not: a client never changes node by moving, the partitions come to it;
    #   * a proximity merge does not: §7.1 moves the partition with NO attached clients first, so a
    #     neighbour is pulled to the character, never the reverse;
    #   * the PCU balancer does not: its improvement floor rejects a mover as small as a character;
    #   * a drain does relocate the partition, but it terminates the node the client is attached to.
    #     That path, and the "without reconnect" promise attached to it, is P1-REC-05a's to assert.
    # This scenario is about the CHARACTER across that move: no duplicate, settings intact, controls
    # live. P2-CHR-02 asserts the ownership/direction half of the same mechanism.
    other = next(node.node_id for node in bench.cluster.config.nodes
                 if node.node_id not in (start_node, "wa-1"))
    destination = None
    for node, section in bench.sections(bench.diag("entities")).items():
        if node != other:
            continue
        for partition in section["partitions"]:
            for candidate in partition["entities"] or []:
                if candidate.get("position"):
                    destination = candidate["position"]
    if destination is None:
        bench.check(f"{other} holds an entity to travel to", False, "no positioned entity found")
        return
    # ~1 km diagonal clear of it: a short offset along one axis lands inside a station's own volume,
    # where the character is simulated and still cannot translate.
    target = (destination[0] + 600, destination[1] + 600, destination[2] + 600)
    bench.cluster.node(start_node,
                       f"/teleport?client={CLIENT_ID}&x={target[0]}&y={target[1]}&z={target[2]}", "POST")
    bench.exercised(travelledTo=other, target=target)

    def moved():
        # Tolerant on purpose: the drained node's control endpoint refuses connections while it shuts
        # down, and a predicate that propagates that error ends the wait on the first poll - which is
        # how this read "never crossed" while the logs showed node-2 recovering the partition 300 ms
        # after the release.
        try:
            current = _node_of_entity(bench, entity)
        except ClusterError:
            return None
        return current if current and current != start_node else None

    try:
        end_node = wait_until(moved, 300, "the character's partition to change node")
    except ClusterError:
        end_node = None
    bench.check("the on-foot character crossed a node boundary", end_node is not None,
                f"{start_node} -> {end_node}")

    after = bench.quiesce()
    bench.standing_audits(after, "after the on-foot handover")
    bench.check("the character keeps its EntityId across the handover",
                bench._server_character(CLIENT_ID) == entity,
                f"{entity} -> {bench._server_character(CLIENT_ID)}")
    after_settings = _settings(bench, identity)
    bench.record("settings-after", after_settings)
    bench.check("the suit settings fixture is identical after the handover",
                _comparable(before_settings) == _comparable(after_settings),
                f"{before_settings} -> {after_settings}")
    # "Controls live" is asserted server-side by invariant 8 above. The probe needs a live client, and
    # a graceful drain terminates the node the client was attached to: whether the session survives
    # that is P1-REC-05a's assertion ("handed the client partition to node 2 without reconnect"), not
    # this scenario's. Record which happened, and probe only when there is something to probe.
    survived = bench.living_character(CLIENT_ID) is not None
    bench.record("client-after-drain", {"remoteApiAlive": survived,
                                        "session": _session(bench, CLIENT_ID)})
    if survived:
        bench.assert_move_probe(CLIENT_ID, "after-handover")
        bench.screenshot(CLIENT_ID, "02-after-handover")
    else:
        print("  (the client did not survive the drained node's termination; "
              "the character half is asserted server-side - see P1-REC-05a for the session half)")

    # Invariant 7's hard half: the source holds none of the transferred entities after Release.
    sections = bench.sections(after)
    leftovers = [node for node, section in sections.items()
                 if node != end_node and _partition_of(section, entity) is not None]
    bench.check("the source node holds no copy of the transferred character", not leftovers, str(leftovers))
    bench.exercised(startNode=start_node, endNode=end_node, characterEntityId=entity,
                    startPartition=start_partition)


def _settings(bench, identity_id: int | None) -> dict | None:
    if identity_id is None:
        return None
    registry = bench.cluster.node("wa-1", "/character-registry")
    for row in registry["settings"]:
        if row["IdentityId"] == identity_id:
            return row
    return None


def _comparable(settings: dict | None) -> dict | None:
    """The settings fixture minus the fields physics moves on its own."""
    if settings is None:
        return None
    return {key: value for key, value in settings.items()
            if key in ("Helmet", "Flashlight", "Radio", "Model")}


def _node_of_entity(bench, entity_id: int) -> str | None:
    for node, section in bench.sections(bench.diag("entities")).items():
        if _partition_of(section, entity_id) is not None:
            return node
    return None


def _skew(bench, node: str, blocks: int, count: int, origin: tuple[float, float, float],
          spin: float = 0.0, members: int = 1) -> list[dict]:
    """Create `count` well-separated heavy grid clusters on one node, so the balancer has a real
    imbalance. `spin` (rad/s) keeps their physics bodies awake; `members` grids per cluster (30 m
    apart, one partition each cluster) give it member weight - a node's unattributed main-thread
    time is spread by member count, and on an idle bench node that is most of the measured load."""
    spawned = []
    for index in range(count):
        x = origin[0] + index * 12_000
        for member in range(members):
            spawned.append(bench.cluster.node(node, f"/trash-test-spawn?x={x}&y={origin[1] + member * 30}"
                                                    f"&z={origin[2]}&builtBy=0&isStatic=true&blocks={blocks}"
                                                    f"&spin={spin}", "POST"))
    bench.record(f"skew-{node}", spawned)
    return spawned


# ---------------------------------------------------------------------------
# P1-CHR-03 - the control-liveness fallback
# ---------------------------------------------------------------------------

def p1_chr_03(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    healthy = bench.assert_move_probe(CLIENT_ID, "positive-control")
    bench.check("positive control: the probe passes on a healthy character",
                healthy.get("passed") is True, str(healthy.get("legs")))

    node = _node_of_entity(bench, bench._server_character(CLIENT_ID))
    bench.cluster.node(node, f"/debug/break-control-rebind?client={CLIENT_ID}&seconds=20", "POST")

    alarm = wait_until(
        lambda: next((line for line in bench.cluster.node(node, "/control-liveness")["alarms"]
                      if str(CLIENT_ID) in line), None),
        90, "the control-liveness alarm")
    bench.record("liveness-alarm", alarm)
    bench.check("a control-dead client raises the liveness alarm", alarm is not None, str(alarm))
    bench.check("the alarm names a forced reconnect", "forced-reconnect" in (alarm or ""), str(alarm))

    # The forced reconnect is a kick: §6.6 rebinds control "through the vanilla join path", so the
    # session must actually drop. A headless client does not rejoin by itself any more than a retail
    # one does - it lands on the disconnect screen - so the rejoin is driven here, as a player would.
    # Wait for the GATEWAY SESSION to go, not just the node attachment: single-flight (§7.1) keys off
    # the session, and a Direct Transport peer outlives its node attachment by up to LiteNetLib's
    # 120 s DisconnectTimeout (Findings 2026-07-27). Rejoining inside that window is refused, and the
    # refusal looks exactly like a client that never reached the respawn screen.
    dropped = wait_until(lambda: True if not _session(bench, CLIENT_ID) else None, 180,
                         "the forced reconnect to drop the Gateway session")
    bench.check("the forced reconnect actually drops the stuck client's session", bool(dropped),
                json.dumps(bench.cluster.get("/transport/status"))[:200])
    # §6.6's remedy is that the player comes straight back. An admin kick arms a multi-minute
    # admission cooldown; a control-liveness reconnect must not, or the fix locks out the very player
    # it rescued - the Gateway answered "Rejected ...: KickedRecently" for the whole window, and the
    # rejoin below is what proves the rescue path no longer does that.
    bench.record("after-kick", bench.cluster.get("/transport/status"))

    bench.kill_client(CLIENT_ID)
    bench.apis.pop(CLIENT_ID, None)
    # The rescue must not have armed the admission cooldown; if it had, this rejoin is refused with
    # "KickedRecently" for minutes and the §6.6 remedy never completes.
    bench.start_client(CLIENT_ID, label="rejoin")
    bench.wait_spawned(CLIENT_ID)
    bench.check("control is live again after rejoining through the vanilla join path",
                _attached(bench, CLIENT_ID) is not None, str(_attached(bench, CLIENT_ID)))
    bench.assert_move_probe(CLIENT_ID, "after-forced-reconnect")
    bench.standing_audits(bench.quiesce(), "after the forced reconnect")
    bench.screenshot(CLIENT_ID, "01-after-forced-reconnect")
    bench.exercised(brokenOn=node, rejoin="vanilla join path after the kick")


def _leave(bench, client_id: int, timeout: float = 180) -> None:
    """Leave the way a player does, and wait until the cluster has really let go of the session.

    Order matters: exit the game, close the API, stop the process, THEN wait - and wait on the
    Gateway's own client list (`/diag?depth=cluster`), which is what single-flight consults. Waiting
    on the node-side identity instead reads as "still here" long after the client is gone.
    """
    try:
        bench.apis[client_id].exit_game()
    except Exception:  # noqa: BLE001 - a client that already left is a successful leave
        pass
    api = bench.apis.pop(client_id, None)
    if api is not None:
        try:
            api.close()
        except Exception:  # noqa: BLE001
            pass
    bench.kill_client(client_id)
    wait_until(lambda: client_id not in [session["clientId"] for session
                                         in bench.cluster.get("/diag?depth=cluster")["clients"]],
               timeout, "the session to drop")


def _session(bench, client_id: int) -> dict | None:
    """The client's Gateway session, or None once the transport has really let go of it."""
    for session in bench.cluster.get("/transport/status").get("sessions") or []:
        if session.get("client") == client_id:
            return session
    return None


def _attached(bench, client_id: int) -> str | None:
    """The node a client is attached to right now, or None when it holds no session."""
    for node_id, section in bench.sections(bench.diag("partitions")).items():
        if any(client["clientId"] == client_id for client in section["clients"]):
            return node_id
    return None


# ---------------------------------------------------------------------------
# P1-CHR-04 - the cross-node transition corner-case matrix
# ---------------------------------------------------------------------------

def p1_chr_04(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    entity = bench._server_character(CLIENT_ID)
    identity = _identity_of(bench, CLIENT_ID)
    cells = {}

    # Cell 1: logout and login while a handover of the character's partition is possible.
    _skew(bench, "node-1", blocks=900, count=4, origin=(700_000.0, 0.0, 0.0))
    bench.cluster.trigger("balance", unthrottled="true")
    bench.apis[CLIENT_ID].close()
    bench.cluster.trigger("split-sweep", wait=True)
    cells["logout-during-balance"] = bench.standing_audits(bench.quiesce(), "logout during balancing")

    # Cell 2: two rapid successive handovers of the same partition (re-adoption with tombstones).
    for _ in range(2):
        bench.cluster.trigger("balance", unthrottled="true")
        time.sleep(5)
    cells["two-rapid-handovers"] = bench.standing_audits(bench.quiesce(), "two rapid handovers")

    # Cell 3: a node kill with a character in the world - recovery must not produce a second body.
    bench.cluster.kill("node-2")
    time.sleep(10)
    cells["source-kill"] = bench.standing_audits(
        bench.diag("entities"), "after a node kill with a character present")

    bench.record("matrix", cells)
    bench.check("every corner-case cell keeps the standing audits green", all(cells.values()), str(cells))
    bench.exercised(cells=sorted(cells), characterEntityId=entity, identityId=identity)


# ---------------------------------------------------------------------------
# P1-BAL-01 - automatic balancing exists and converges
# ---------------------------------------------------------------------------

def p1_bal_01(bench) -> None:
    bench.start_cluster()
    # The client leg (TestPlan §2.1): a live player rides through the whole rebalance. The §7.5
    # model deprioritizes client-bearing movers (the γ term) without forbidding them, and a
    # zero-PCU character never helps an imbalance, so convergence must neither move the player
    # pointlessly nor be blocked by them.
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.assert_move_probe(CLIENT_ID, "before-balancing")
    bench.quiesce()
    before = _all_partitions(bench)
    bench.record("distribution-before", before)

    # Skewed by construction: well-separated clusters of client-less, SPINNING grids, all created on
    # node-1. The clusters are further apart than D + 2m, so each is its own partition and the balancer
    # has granular work.
    #
    # T-0168 (Viktor 2026-09-17): the balancer plans by MEASURED sim load, not by partition count or
    # PCU, so the bar is its own verdict and the fixture has to carry real load: a still grid's physics
    # body sleeps and costs nothing (the old 1200-block static-looking fixture moved twice and then
    # correctly planned nothing at 8/3 partitions). Spinning keeps each body active (the physics share
    # is attributed per active body) and 15 members per cluster carry the member-count share of the
    # node's remaining main-thread time; one spinning grid per partition was still dwarfed by the
    # client's partition (bench 20260917-152037Z). Partition counts are recorded, not asserted.
    pins_before = len(_gateway_lines(bench, r"Automatic balance migration pinned"))
    _skew(bench, "node-1", blocks=8, count=8, origin=(400_000.0, 0.0, 0.0), spin=0.5, members=15)
    skewed = wait_until(lambda: _all_partitions(bench) if
                        sum(1 for node in _all_partitions(bench).values() if node == "node-1") >= 8 else None,
                        120, "the skewed distribution to register")
    bench.record("distribution-skewed", skewed)
    counts_before = _counts(skewed)
    bench.check("the fixture is skewed onto node-1", counts_before.get("node-1", 0) >= 8, str(counts_before))
    fixture = set(skewed) - set(before)
    samples = []

    def settled():
        status = bench.cluster.get("/balance/status")
        owners = _all_partitions(bench)
        samples.append({"t": round(time.time(), 1), "status": status, "counts": _counts(owners),
                        "owners": {p: owners.get(p) for p in fixture}})
        moved = len(_gateway_lines(bench, r"Automatic balance migration pinned")) > pins_before
        quiet = (status.get("evaluated") and status.get("movesPlanned") == 0 and not status.get("pauseReason")
                 and status.get("worstDeviation", 1e9) <= status.get("tolerance", 0))
        in_flight = len(bench.cluster.get("/registry/handovers"))
        # Settled = the verdict holds on two consecutive samples with nothing in flight.
        ok = moved and quiet and in_flight == 0
        samples[-1]["ok"] = bool(ok)
        return samples[-1] if ok and len(samples) >= 2 and samples[-2].get("ok") else None

    # The balancer's OWN cadence - no trigger. TestPlan §1.2: a scenario whose subject is the scheduler
    # must let it fire by itself in at least one leg. Budget: one move per 60 s cooldown, up to four
    # moves for eight equal movers, plus load-EMA settling.
    try:
        final_sample = wait_until(settled, 600, "the balancer to settle (deviation within tolerance, nothing planned)",
                                  interval=15)
        passed = True
    except ClusterError:
        final_sample, passed = (samples[-1] if samples else {}), False
    final = final_sample.get("counts", _counts(_all_partitions(bench)))
    status = final_sample.get("status", {})
    bench.check("the balancer moved at least one partition on its own cadence",
                len(_gateway_lines(bench, r"Automatic balance migration pinned")) > pins_before,
                f"{pins_before} pins before the fixture")
    bench.check("the balancer settles: worst deviation within tolerance and no moves planned", passed,
                json.dumps(status))
    bench.record("distribution-after", final)
    bench.record("balance-trace", samples)

    # No oscillation: no fixture partition returns to a node it already left.
    bounced = []
    for partition in fixture:
        path = [sample["owners"].get(partition) for sample in samples if sample["owners"].get(partition)]
        hops = [node for index, node in enumerate(path) if index == 0 or node != path[index - 1]]
        if len(hops) != len(set(hops)):
            bounced.append({partition: hops})
    bench.check("no partition moves back to a node it left (no oscillation)", not bounced, str(bounced))
    moves = samples

    # The balancer honours its budget: never more than one handover in flight.
    concurrent = len(bench.cluster.get("/registry/handovers"))
    bench.check("at most one handover in flight at a time", concurrent <= 1, str(concurrent))

    # The aggregate-shape leg: split-sweep first, then balance - the prerequisite chain end to end.
    bench.cluster.trigger("split-sweep", wait=True)
    bench.check("the split sweep keeps partitions granular for the balancer",
                sum(_counts(_all_partitions(bench)).values()) >= 8, str(_counts(_all_partitions(bench))))

    # The player rode through: same body, controls live, invariants clean after the rebalance.
    bench.check("the client survives the rebalance", bench.living_character(CLIENT_ID) is not None,
                str(_session(bench, CLIENT_ID)))
    bench.assert_move_probe(CLIENT_ID, "after-balancing")
    bench.standing_audits(bench.quiesce(), "after the rebalance")
    bench.exercised(before=counts_before, after=final, steps=len(moves), clientId=CLIENT_ID,
                    worstDeviation=status.get("worstDeviation"), tolerance=status.get("tolerance"))


def _gateway_lines(bench, pattern: str) -> list[str]:
    return _lines(bench, "gateway", pattern)


def _counts(owners: dict[int, str]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for node in owners.values():
        counts[node] = counts.get(node, 0) + 1
    return counts


# ---------------------------------------------------------------------------
# P2-CHR-01 - Character Registry uniqueness under chaos
# ---------------------------------------------------------------------------

def p2_chr_01(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    identity = _identity_of(bench, CLIENT_ID)
    entity = bench._server_character(CLIENT_ID)
    registry = bench.cluster.node("wa-1", "/character-registry")
    bench.record("registry-initial", registry)
    bench.check("the registry knows exactly one instance for the identity",
                sum(1 for row in registry["instances"] if row["IdentityId"] == identity) == 1,
                str(registry["instances"]))
    bench.check("the duplicate audit is clean", not registry["duplicates"], str(registry["duplicates"]))

    # Chaos: kill a regular node while the character is live, and let recovery run.
    bench.cluster.kill("node-2")
    time.sleep(15)
    bench.standing_audits(bench.diag("entities"), "after a node kill")

    # WA restart: the live-instance map rebuilds from node reports and the settings of record survive
    # because they are in the global save.
    settings_before = _settings(bench, identity)
    bench.cluster.trigger("save", wait=True)
    time.sleep(5)
    bench.cluster.kill("wa-1")
    time.sleep(10)
    after_kill = bench.diag("entities")
    bench.record("after-wa-kill", after_kill)
    bench.standing_audits(after_kill, "after the WA is killed")
    node1_registry = bench.cluster.node("node-1", "/character-registry")
    bench.record("registry-after-wa-kill", node1_registry)
    bench.check("no identity gained a second body through the chaos",
                not node1_registry["duplicates"], str(node1_registry["duplicates"]))
    bench.record("settings-before-wa-kill", settings_before)
    bench.exercised(identityId=identity, characterEntityId=entity)


# ---------------------------------------------------------------------------
# P2-CHR-02 - teleport & spawn-elsewhere direction
# ---------------------------------------------------------------------------

def p2_chr_02(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.assert_move_probe(CLIENT_ID, "before-teleport")
    entity = bench._server_character(CLIENT_ID)
    start_node = _node_of_entity(bench, entity)

    # The destination: node-2's End Beacon, ~5.1 km from the start base - more than D (3 km) away
    # from everything on node-1, but inside the default split distance (T-0319: 'auto' = 4 x D =
    # 12 km), so the teleport does NOT split the character off its start partition.
    deep = bench.diag("entities")
    destination = None
    for node, section in bench.sections(deep).items():
        for partition in section["partitions"]:
            for candidate in partition["entities"] or []:
                if candidate["displayName"] == "End Beacon":
                    destination = (node, partition["partitionId"], candidate["position"], candidate["entityId"])
    bench.check("the destination base is on the other node", destination is not None and destination[0] != start_node,
                str(destination))
    if destination is None:
        return
    destination_node, destination_partition, position, beacon = destination
    owners_before = _all_partitions(bench)
    handovers_before = len(bench.cluster.get("/registry/handovers"))

    # Teleport the character next to the destination base. Sphere centres are not places: this uses the
    # destination ENTITY's position, offset clear of it, not a partition sphere centre. The offset is
    # diagonal and ~1 km: a 200 m offset along one axis put the character inside the station's own
    # volume, where it is simulated (physics active, controller bound, both halves healthy) and still
    # cannot translate - which reads exactly like the stuck-character bug and is not one. Still far
    # inside D of the destination, so the character's partition must come to the destination's node.
    target = (position[0] + 600, position[1] + 600, position[2] + 600)
    bench.cluster.node(start_node,
                       f"/teleport?client={CLIENT_ID}&x={target[0]}&y={target[1]}&z={target[2]}", "POST")

    arrived = wait_until(lambda: _node_of_entity(bench, entity) if
                         _node_of_entity(bench, entity) == destination_node else None,
                         240, "the character to arrive on the destination node")
    bench.check("the character moved to the destination's node", arrived == destination_node,
                f"{start_node} -> {arrived}")

    # The direction, asserted on the destination ENTITY rather than on its partition id: with the
    # 12 km split distance the character's whole start partition is co-located to the destination
    # node and merges with the destination partition, and the merged partition keeps the id of the
    # layout with more members (the start base's), so the destination's old id legitimately
    # disappears from the registry. What must hold is that the base never moved: it is still hosted
    # by the destination node, in a partition the registry gives to that node.
    def _beacon_home():
        hosts = bench.entity_owner(bench.diag("entities"), beacon)
        owners = _all_partitions(bench)
        # Mid-merge the node-local id and the registry row can disagree for a moment: read again.
        return (hosts, owners) if len(hosts) == 1 and hosts[0][1] in owners else None

    try:
        hosts, owners_after = wait_until(_beacon_home, 30, "the destination base in one registered partition")
    except ClusterError:
        hosts, owners_after = bench.entity_owner(bench.diag("entities"), beacon), _all_partitions(bench)
    bench.record("ownership", {"before": owners_before, "after": owners_after, "destinationBaseHosts": hosts})
    bench.check("the destination base never left its node (the character went to it, not the reverse)",
                [node for node, _ in hosts] == [destination_node]
                and owners_after.get(hosts[0][1]) == destination_node,
                f"End Beacon {beacon}: ({destination_node}, {destination_partition}) -> {hosts}; "
                f"owners {owners_before} -> {owners_after}")
    bench.assert_move_probe(CLIENT_ID, "after-teleport")
    bench.screenshot(CLIENT_ID, "01-after-teleport")
    bench.standing_audits(bench.quiesce(), "after the teleport")
    bench.exercised(startNode=start_node, destinationNode=destination_node,
                    destinationPartition=destination_partition, handoversBefore=handovers_before)


# ---------------------------------------------------------------------------
# P2-CHR-03 - spawn defaults
# ---------------------------------------------------------------------------

def p2_chr_03(bench) -> None:
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    identity = _identity_of(bench, CLIENT_ID)
    fresh = _settings(bench, identity)
    bench.record("fresh-spawn-settings", fresh)
    bench.check("a fresh spawn has the helmet CLOSED", fresh is not None and fresh["Helmet"] is True, str(fresh))
    bench.check("a fresh spawn has the flashlight OFF", fresh is not None and fresh["Flashlight"] is False, str(fresh))
    bench.check("a fresh spawn has the radio OFF", fresh is not None and fresh["Radio"] is False, str(fresh))
    bench.screenshot(CLIENT_ID, "01-fresh-spawn")

    # A returning player: change the fixture, relog, and require the stored record back rather than the
    # defaults. The record is what the registry folded from the owning node's report.
    api = bench.apis[CLIENT_ID]
    if not bench.ensure_gameplay_focus(CLIENT_ID):
        bench.check("a screen held focus, so the suit toggle could not be sent", False,
                    bench.focus(CLIENT_ID))
        return
    # The node reports at 1 Hz and the World Authority folds the reports, so poll for the record
    # rather than sleeping a guessed interval. Press more than once: §5.5 settling re-applies the
    # stored record for up to 10 s after spawn, so a toggle sent inside that window is reverted by
    # design and the first press can be swallowed outright.
    def _flashlight_changed():
        row = _settings(bench, identity)
        return row if row and row["Flashlight"] != fresh["Flashlight"] else None

    distinctive = None
    for _ in range(5):
        api.key("L")            # flashlight on
        deadline = time.time() + 12
        while time.time() < deadline:
            distinctive = _flashlight_changed()
            if distinctive:
                break
            time.sleep(1)
        if distinctive:
            break
    bench.record("distinctive-settings", distinctive)
    bench.check("the registry records a changed suit state", distinctive is not None,
                f"{fresh} -> {distinctive}")
    if distinctive is None:
        return

    # Relog: §5.5 says a returning player gets the stored record, NOT the fresh-spawn defaults.
    _leave(bench, CLIENT_ID)
    bench.start_client(CLIENT_ID, label="return")
    bench.wait_spawned(CLIENT_ID)
    restored = wait_until(lambda: _settings(bench, identity), 60, "the returning player's record")
    bench.record("restored-settings", restored)
    bench.check("a returning player gets the stored record, not the fresh-spawn defaults",
                _comparable(restored) == _comparable(distinctive),
                f"{distinctive} -> {restored}")
    bench.screenshot(CLIENT_ID, "02-returned")
    bench.standing_audits(bench.quiesce(), "after the return")
    bench.exercised(identityId=identity, relog="exit_game then rejoin")


# ---------------------------------------------------------------------------
# P3-CLU-01 - three-node coverage: every ordered node pair completes a handover
# ---------------------------------------------------------------------------

def _force_handover(bench, source: str, target: str, partition: int, client: int | None = None) -> None:
    """Drive the Gateway's forced-handover route the way the lanes do.

    The route resolves nodes by their registered CONTROL endpoints, never by node id - passing
    `node-1` reads as "endpoints do not match the configured source and target nodes"."""
    nodes = {node.node_id: node for node in bench.cluster.config.nodes}
    query = (f"/handover?source={nodes[source].control}&target={nodes[target].control}"
             f"&backend={nodes[target].backend}&partition={partition}"
             + (f"&client={client}" if client is not None else "&clientless=true"))
    bench.cluster.post(query, timeout=300)


def _released_pairs(history: list) -> set[tuple[str, str]]:
    return {(record["SourceNode"], record["TargetNode"]) for record in history
            if any(stamp["State"] == "released" for stamp in record["States"])}


def p3_clu_01(bench) -> None:
    """Three-node full coverage in one continuous session with two clients: a probe partition
    walks an Eulerian circuit over the three regular nodes (every ordered pair completes a fenced
    transfer), then the first client's character visits every node - so each node receives at
    least one grid arrival AND at least one player arrival (the §13 exit-line matrix)."""
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    # The wingman: a second live session, parked - the first standing scenario where node count,
    # partition count and client count are all above two.
    bench.start_client(CLIENT2_ID, remote_port=24160)
    bench.wait_spawned(CLIENT2_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    bench.assert_move_probe(CLIENT_ID, "before-circuit")
    bench.quiesce()

    regulars = [node.node_id for node in bench.cluster.config.nodes if node.role != "WA"]
    bench.check("the bench runs three regular nodes", len(regulars) == 3, str(regulars))

    # The probe: a lone static grid far from every fixture, so it is its own partition and no
    # merge/split ever re-keys it mid-circuit.
    spawned = bench.cluster.node("node-1", "/trash-test-spawn?x=900000&y=0&z=0&static=true", "POST")
    grid = spawned["grid"]
    bench.record("probe-grid", spawned)

    def placed():
        section = bench.sections(bench.diag("entities")).get("node-1")
        return _partition_of(section, grid) if section else None

    wait_until(placed, 120, "the probe grid to get a partition on node-1")

    # Hold the balancer still: the circuit is choreography, and an automatic move mid-hop would
    # make the source assertion race its own subject.
    bench.cluster.post("/automatic/pause")
    pairs = []
    try:
        # node-1 -> 2 -> 3 -> 1 -> 3 -> 2 -> 1: an Eulerian circuit over K3, every ordered pair once.
        for target in ("node-2", "node-3", "node-1", "node-3", "node-2", "node-1"):
            bench.quiesce(timeout=120)
            places = bench.entity_owner(bench.diag("entities"), grid)
            bench.check(f"before the hop to {target}: exactly one copy of the probe grid",
                        len(places) == 1, str(places))
            if len(places) != 1:
                return
            source, partition = places[0]
            _force_handover(bench, source, target, partition)
            wait_until(lambda: target if _all_partitions(bench).get(partition) == target else None,
                       180, f"partition {partition} to arrive on {target}")
            pairs.append((source, target))
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass

    expected = {(a, b) for a in regulars for b in regulars if a != b}
    bench.check("the circuit visited every ordered node pair", set(pairs) == expected,
                f"visited {sorted(set(pairs))}")

    # The player half of the exit-line matrix: the first client's character visits every node,
    # riding the same fenced machinery with its client switched along.
    entity = bench._server_character(CLIENT_ID)
    start = _node_of_entity(bench, entity)
    # Two hops to the other nodes, then home: the arrivals cover all three nodes.
    route = [node for node in regulars if node != start] + [start]
    player_visits = []
    bench.cluster.post("/automatic/pause")
    try:
        for target in route:
            bench.quiesce(timeout=120)
            places = bench.entity_owner(bench.diag("entities"), entity)
            bench.check(f"exactly one copy of the character before its crossing to {target}",
                        len(places) == 1, str(places))
            if len(places) != 1:
                return
            source, partition = places[0]
            bench._wait_at_rest(entity)
            _force_handover(bench, source, target, partition, client=CLIENT_ID)
            wait_until(lambda: target if _node_of_entity(bench, entity) == target else None,
                       300, f"the character to arrive on {target}")
            player_visits.append((source, target))
            bench.assert_move_probe(CLIENT_ID, f"on-{target}")
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass

    # The Gateway's own record proves the full matrix: for every node, at least one released
    # clientless (grid) arrival and one released client-bearing (player) arrival.
    history = bench.cluster.get("/registry/handovers/history")
    bench.record("handover-history", history)
    released = _released_pairs(history)
    bench.check("the handover history shows a released transfer for every ordered pair",
                expected <= released, f"released pairs {sorted(released)}")
    arrivals = {node: {"grid": 0, "player": 0} for node in regulars}
    for row in history:
        if row["TargetNode"] not in arrivals:
            continue
        if not any(stamp["State"] == "released" for stamp in row["States"]):
            continue
        arrivals[row["TargetNode"]]["player" if row["Clients"] > 0 else "grid"] += 1
    bench.check("every node received at least one grid arrival and one player arrival",
                all(counts["grid"] >= 1 and counts["player"] >= 1 for counts in arrivals.values()),
                str(arrivals))

    after = bench.quiesce()
    bench.standing_audits(after, "after the coverage matrix")
    places = bench.entity_owner(bench.diag("entities"), grid)
    bench.check("no residual copy anywhere after the circuit", len(places) == 1, str(places))
    bench.check("the wingman client rode through the whole session",
                bench.living_character(CLIENT2_ID) is not None, str(_session(bench, CLIENT2_ID)))
    bench.assert_move_probe(CLIENT_ID, "after-circuit")
    bench.screenshot(CLIENT_ID, "02-after-circuit")
    bench.exercised(regulars=regulars, circuit=pairs, playerVisits=player_visits, probeGrid=grid,
                    arrivals=arrivals)


p3_clu_01.regular_nodes = 3


# ---------------------------------------------------------------------------
# P3-CLU-02 - bidirectional player handover: three forced round trips A->B->A
# ---------------------------------------------------------------------------

def _semantic_diff(bench, before: Path, after: Path, entity_id: int) -> dict:
    completed = subprocess.run(
        ["dotnet", str(bench.cluster.config.world_tool), "semantic-diff",
         str(before), str(after), str(entity_id)],
        capture_output=True, text=True, timeout=300)
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError:
        return {"equal": False, "error": (completed.stderr or completed.stdout)[:400]}


def p3_clu_02(bench) -> None:
    """The same client crosses A->B and back three times: same body, identical semantic OB, suit
    settings and toolbar intact, controls live after every crossing, and no latency creep."""
    bench.start_cluster()
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    entity = bench._server_character(CLIENT_ID)
    identity = _identity_of(bench, CLIENT_ID)
    settings_start = _settings(bench, identity)
    bench.record("settings-start", settings_start)
    try:
        toolbar_start = api.get_toolbar()
    except Exception as error:  # noqa: BLE001 - an endpoint gap is a finding, not a crash
        toolbar_start = {"error": str(error)}
    bench.record("toolbar-start", toolbar_start)
    bench.assert_move_probe(CLIENT_ID, "before-round-trips")
    bench.quiesce()

    bench.cluster.post("/automatic/pause")
    crossings = []
    try:
        for trip in (1, 2, 3):
            for leg in ("out", "back"):
                places = bench.entity_owner(bench.diag("entities"), entity)
                bench.check(f"trip {trip} {leg}: exactly one copy of the character before the crossing",
                            len(places) == 1, str(places))
                if len(places) != 1:
                    return
                source, partition = places[0]
                target = next(node.node_id for node in bench.cluster.config.nodes
                              if node.role != "WA" and node.node_id != source)
                # The semantic diff tolerates 1 m of position drift, so the exports bracket the
                # crossing tightly: settle the character first, probe only after the diff.
                bench._wait_at_rest(entity)
                before_xml = bench.dir / f"trip{trip}-{leg}-before.xml"
                before_xml.write_text(str(bench.cluster.node(source, "/export", timeout=300)))
                _force_handover(bench, source, target, partition, client=CLIENT_ID)

                def arrived():
                    try:
                        current = _node_of_entity(bench, entity)
                    except ClusterError:
                        return None
                    return current if current == target else None

                wait_until(arrived, 300, f"the character to arrive on {target}")
                bench.quiesce(timeout=120)
                after_xml = bench.dir / f"trip{trip}-{leg}-after.xml"
                after_xml.write_text(str(bench.cluster.node(target, "/export", timeout=300)))

                verdict = _semantic_diff(bench, before_xml, after_xml, entity)
                bench.record(f"semantic-diff-trip{trip}-{leg}", verdict)
                bench.check(f"trip {trip} {leg}: the character OB is semantically identical",
                            verdict.get("equal") is True, json.dumps(verdict)[:300])
                bench.check(f"trip {trip} {leg}: the character keeps its EntityId",
                            bench._server_character(CLIENT_ID) == entity,
                            f"{entity} -> {bench._server_character(CLIENT_ID)}")
                places = bench.entity_owner(bench.diag("entities"), entity)
                bench.check(f"trip {trip} {leg}: no residual copy on the source",
                            len(places) == 1 and places[0][0] == target, str(places))
                bench.standing_audits(bench.diag("entities"), f"trip {trip} {leg}")
                settings_now = _settings(bench, identity)
                bench.check(f"trip {trip} {leg}: suit settings intact",
                            _comparable(settings_now) == _comparable(settings_start),
                            f"{settings_start} -> {settings_now}")
                bench.assert_move_probe(CLIENT_ID, f"trip{trip}-{leg}")

                record = next((row for row in reversed(bench.cluster.get("/registry/handovers/history"))
                               if row["SourceNode"] == source and row["TargetNode"] == target
                               and int(row["Partition"]) == partition), None)
                released_ms = None if record is None else next(
                    (stamp["AtMs"] for stamp in record["States"] if stamp["State"] == "released"), None)
                crossings.append({"trip": trip, "leg": leg, "source": source, "target": target,
                                  "partition": partition, "releasedMs": released_ms})
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass

    bench.record("crossings", crossings)
    bench.check("every crossing left a released transfer in the history",
                all(crossing["releasedMs"] is not None for crossing in crossings), str(crossings))
    latencies = [crossing["releasedMs"] for crossing in crossings if crossing["releasedMs"] is not None]
    if len(latencies) >= 4:
        first = latencies[:len(latencies) // 2]
        last = latencies[len(latencies) - len(first):]
        # Generous by design: the box runs lanes back to back, so "no upward trend" means the last
        # crossings are not a multiple of the first ones, not that every sample descends.
        bench.check("crossing latency shows no upward trend",
                    sum(last) / len(last) <= 2 * sum(first) / len(first) + 1000, str(latencies))

    try:
        toolbar_end = api.get_toolbar()
    except Exception as error:  # noqa: BLE001
        toolbar_end = {"error": str(error)}
    bench.record("toolbar-end", toolbar_end)
    bench.check("the toolbar fixture is identical after the round trips",
                toolbar_end == toolbar_start, f"{toolbar_start} -> {toolbar_end}")
    bench.screenshot(CLIENT_ID, "02-after-round-trips")
    bench.exercised(characterEntityId=entity, identityId=identity, roundTrips=3,
                    crossings=len(crossings))


# ---------------------------------------------------------------------------
# P3-SUN-01 - admin time-of-day: WA-owned offset, one forward step, rejection, restart survival
# ---------------------------------------------------------------------------

def _forward_minutes(now: float, base: float, interval: float) -> float:
    """How far `now` sits ahead of `base` on the circular sun clock.

    The applied value comes back through a float32 round trip (40.0 -> 39.999996), and a plain
    modulo maps that hair-backward wobble to a full period - treat it as zero instead."""
    drift = (now - base) % interval
    return 0.0 if drift > interval - 0.01 else drift


def _absolute_steps(observation: dict) -> list[dict]:
    """The clock jumps in `observation` that free-running cannot account for.

    SunObservationService scores a step whenever the game clock advances more than one second
    between two frames (StepThresholdTicks). That threshold cannot separate a SET from a STALL:
    a frame that took 1.26 s advances the clock by 1.26 s and is counted. On 2026-09-19 exactly
    that happened two seconds after the reset, while the client was still settling after spawn -
    deltaSeconds 1.2569654 at frame 109 - and it failed three of this scenario's checks while the
    real admin set (2400.42 s at frame 717) sat correct in the same record.

    What distinguishes a set is that WALL TIME cannot pay for it. Between two samples the clock
    can advance at most as far as the wall clock moved, however long the frame took; an absolute
    set jumps further. The wall gap here is measured between recorded events rather than between
    frames, so it OVERSTATES the time available and can only make this test stricter about calling
    something a set - which is the direction an instrument may err in when the claim is "exactly
    one set happened".
    """
    reference = _utc(observation.get("sinceUtc"))
    absolute = []
    for event in observation.get("events") or []:
        stamp = _utc(event.get("utc"))
        gap = (stamp - reference).total_seconds() if reference and stamp else 0.0
        reference = stamp or reference
        if event.get("kind") != "step":
            continue
        if float(event.get("deltaSeconds") or 0.0) > gap + 1.0:
            absolute.append(event)
    return absolute


def _utc(value):
    """The service's ISO stamps, with or without a trailing Z or fractional seconds."""
    if not value:
        return None
    try:
        return datetime.datetime.fromisoformat(str(value).rstrip("Z"))
    except ValueError:
        return None


def p3_sun_01(bench) -> None:
    """An admin's time-of-day set lands as the WA-owned SunTimeOffset: every node serves the same
    value, the client clock steps forward exactly once and never rewinds, a non-admin's set is
    dropped, and the offset survives a save and full restart without anyone re-applying it."""
    # Strict promotion makes the offline test cluster consult the raw promotion map, so the
    # non-admin leg is a real rejection instead of vanilla's everyone-is-Owner short-circuit.
    bench.cluster.config.node_env["CLUSTER_STRICT_PROMOTION"] = "1"
    # Every client RPC into a node lands in its game log (rpc_msg/rpc_in/rpc_drop): a set that
    # never arrives and a set that is dropped look identical from the outside otherwise.
    bench.cluster.config.node_env["CLUSTER_RPC_TRACE"] = "1"
    bench.start_cluster()
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    node_ids = [node.node_id for node in bench.cluster.config.nodes]
    offset_key = "sunTimeOffsetMilliseconds"

    def statuses() -> dict[str, dict]:
        return {node: bench.cluster.node(node, "/time-of-day") for node in node_ids}

    before = statuses()
    bench.record("time-of-day-before", before)
    env0 = api.get_environment()
    bench.record("environment-before", env0)
    interval = env0.get("sunRotationIntervalMinutes") or 120
    # Two DISTINCT values: the rejected set and the admin set must not share one, both so the
    # assertion proves the admin's value specifically landed, and because a byte-identical repeat
    # of the rejected raise is swallowed before it reaches any node (see the 2026-07-30 finding).
    rejected_target = (env0["timeOfDay"] + interval / 4) % interval
    target = (env0["timeOfDay"] + interval / 3) % interval
    api.reset_sun_observation()

    # Leg 1: a NON-admin set is dropped by the server's AdminOnly validation - nothing moves.
    attached = _attached(bench, CLIENT_ID) or "node-1"
    unpromoted = bench.cluster.node(attached, f"/promote-test?userId={CLIENT_ID}")
    bench.record("promotion-before", unpromoted)
    bench.check("strict promotion reports the client as unpromoted", unpromoted.get("level") == 0,
                str(unpromoted))
    api.set_time_of_day(rejected_target)
    time.sleep(10)
    rejected = statuses()
    bench.record("after-non-admin-set", rejected)
    bench.check("a non-admin set changes no node's offset",
                all(row[offset_key] == before[node][offset_key] for node, row in rejected.items()),
                str({node: row[offset_key] for node, row in rejected.items()}))
    observation = api.get_sun_observation()
    bench.check("a non-admin set never steps the client clock", _absolute_steps(observation) == [],
                str(observation))

    # Leg 2: promote (replicates cluster-wide through the WA) and set for real.
    bench.cluster.node("node-1", f"/promote-test?userId={CLIENT_ID}&level=4", "POST")
    wait_until(lambda: bench.cluster.node(attached, f"/promote-test?userId={CLIENT_ID}")
               .get("level") == 4 or None,
               90, "the promotion to replicate to the client's node")
    api.set_time_of_day(target)

    def applied():
        rows = statuses()
        offsets = {row[offset_key] for row in rows.values()}
        if len(offsets) != 1 or offsets == {before[attached][offset_key]}:
            return None
        # The sun keeps rotating while this polls, so "at the target" means a small forward drift.
        if all(_forward_minutes(row["timeOfDay"], target, interval) <= interval / 20
               for row in rows.values()):
            return rows
        return None

    after = wait_until(applied, 120, "every node to serve the new time of day with one shared offset")
    bench.record("after-admin-set", after)
    offset = after[attached][offset_key]
    bench.check("the admin set moved the shared offset forward", offset > before[attached][offset_key],
                f"{before[attached][offset_key]} -> {offset}")

    # The replication clock must not chase the set: a heartbeat that re-attaches the offset
    # compounds it once per second, and every node then slews after the runaway.
    clocks = {node: row["authorityClockMilliseconds"] for node, row in after.items()}
    bench.check("the replication clock stays agreed across nodes after the set",
                max(clocks.values()) - min(clocks.values()) <= 30_000, str(clocks))

    observation = api.get_sun_observation()
    bench.record("sun-observation", observation)
    bench.check("the client clock stepped forward exactly once", len(_absolute_steps(observation)) == 1,
                str(observation))
    bench.check("the client clock never rewound", observation.get("backwardSteps", 0) == 0,
                str(observation))
    env_after = api.get_environment()
    bench.record("environment-after", env_after)
    bench.check("the vanilla client shows the new time of day",
                _forward_minutes(env_after["timeOfDay"], target, interval) <= interval / 20,
                f"target {target}, client {env_after['timeOfDay']}")

    # No loopback: nothing re-publishes, nothing re-steps.
    time.sleep(10)
    settled = statuses()
    bench.check("no node re-publishes the offset after it settles",
                all(row[offset_key] == offset for row in settled.values()),
                str({node: row[offset_key] for node, row in settled.items()}))
    settled_observation = api.get_sun_observation()
    bench.check("the client clock holds at one step", len(_absolute_steps(settled_observation)) == 1,
                str(settled_observation))
    bench.assert_move_probe(CLIENT_ID, "after-admin-set")
    bench.standing_audits(bench.quiesce(), "after the admin set")
    bench.screenshot(CLIENT_ID, "02-after-admin-set")

    # Leg 3: the offset is durable WA state - save, cold-restart everything, and require the same
    # offset back on every node with nobody re-applying it. The `save` trigger only queues
    # partition captures; the WA's global save (which carries the offset record) runs on its own
    # 60 s cadence, so force one and wait for its registration before killing anything.
    bench.cluster.trigger("save", wait=True)
    wa_log = bench.cluster.config.root / "wa-1.log"
    saves_before = wa_log.read_text(errors="replace").count("global save registered")
    bench.cluster.node("wa-1", "/global-save-capture", "POST")
    wait_until(lambda: wa_log.read_text(errors="replace").count("global save registered") > saves_before
               or None, 90, "the WA to register a global save carrying the offset")
    # THE CATALOGUE HAS TO BE DURABLE TOO, not just the save file. The gateway marks its registry
    # dirty and flushes at most once per CLUSTER_PERSIST_INTERVAL_SECONDS (60 s by default), so a
    # kill within that window loses the registrations of the newest global saves - and the World
    # Authority asks the registry WHICH save to recover. On 2026-09-19 that restored g1-s2 while
    # g1-s3 and g1-s4, the two carrying the offset, sat intact on disk: the offset came back 0 and
    # this leg failed for a reason that has nothing to do with the sun. /registry/checkpoint is the
    # documented operator commit point and flushes immediately.
    bench.cluster.post("/registry/checkpoint")
    bench.cluster.kill()
    bench.apis.pop(CLIENT_ID, None)
    bench.cluster.start()   # warm restart of the same run root (§5.3), the default
    bench.cluster.wait_ready()

    # The WA restores the record from the global save; the nodes then need the global-state resync
    # for the offset AND a few gateway clock samples before their sun matches the WA's - wait for
    # the propagation, never sample the boot instant. Consistency is asserted on the SUN VECTOR,
    # not the `timeOfDay` display scalar: that static races vanilla's load-time init against the
    # cluster re-publish and legitimately differs per node. §8.2's player-visible fact is that
    # every node's clock puts the sun in the same place, without anyone re-applying the offset.
    def restored():
        rows = statuses()
        if not all(row[offset_key] == offset for row in rows.values()):
            return None
        suns = [row["sunDirection"] for row in rows.values()]
        dots = [sum(a[axis] * b[axis] for axis in ("x", "y", "z")) for a in suns for b in suns]
        return rows if min(dots) >= 0.999 else None

    try:
        restarted = wait_until(restored, 120, "every node to serve the persisted offset and the same sun")
        survived = True
    except ClusterError:
        restarted = statuses()
        survived = False
    bench.record("after-restart", restarted)
    bench.check("the offset and the sun survive the restart on every node", survived,
                str({node: (row[offset_key], row["sunDirection"]) for node, row in restarted.items()}))
    bench.standing_audits(bench.diag("entities"), "after the restart")
    bench.exercised(target=round(target, 2), offsetMilliseconds=offset, attached=attached,
                    intervalMinutes=interval)


SCENARIOS = {
    "P1-INF-01": (p1_inf_01, "diag endpoint self-test", "3"),
    "P1-INF-02": (p1_inf_02, "trigger endpoints self-test", "3"),
    "P1-MEM-05": (p1_mem_05, "split sweep, including the no-movement case", "2"),
    "P1-CHR-01": (p1_chr_01, "login/reconnect never duplicates a character", "3"),
    "P1-CHR-02": (p1_chr_02, "on-foot cross-node handover: no dup, settings intact, controls live", "3"),
    "P1-CHR-03": (p1_chr_03, "control-liveness fallback", "4"),
    "P1-CHR-04": (p1_chr_04, "cross-node transition corner-case matrix", "3/4"),
    "P1-BAL-01": (p1_bal_01, "automatic balancing exists and converges", "3"),
    "P2-CHR-01": (p2_chr_01, "Character Registry uniqueness under chaos", "4"),
    "P2-CHR-02": (p2_chr_02, "teleport & spawn-elsewhere direction", "3"),
    "P2-CHR-03": (p2_chr_03, "spawn defaults", "3"),
    "P3-CLU-01": (p3_clu_01, "three-node coverage: every ordered pair completes a handover", "3"),
    "P3-CLU-02": (p3_clu_02, "bidirectional player handover: three A->B->A round trips", "3"),
    "P3-SUN-01": (p3_sun_01, "admin time-of-day: WA offset, one client step, rejection, restart", "4"),
}
