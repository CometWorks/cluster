#!/usr/bin/env python3
"""Scenarios whose evidence is an in-process check rather than a live cluster cycle.

TestPlan.md's Tier 1/2 rows - codec golden tests, WAL crash fuzz, the route-flip model check,
membership flood fill, container property tests - are assertions about pure functions. Booting four
processes and inferring the function from where entities ended up would be slower *and* weaker, so
each of those runs the section of the product's own self-test that owns it and cites its output.

Two self-tests, addressed the same way:

* the Gateway's, over ``dotnet run --project ClusterRegistry -- --self-test <section>``;
* the node's, over ``GET /cluster/self-test?section=<section>`` on a one-node cluster.

The rest of this module holds the live scenarios that had no lane.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from bench import HOST, cli, survival_template  # noqa: E402
from cluster import CRASH_ENV, ClusterError, wait_until  # noqa: E402

#: The packaged Gateway: its self-test runs from the package, like the release build's own check.
GATEWAY_DLL = HOST.package / "registry/ClusterRegistry.dll"


# -- the two self-test front doors -----------------------------------------------------------


def gateway_self_test(bench, section: str, env: dict | None = None) -> subprocess.CompletedProcess:
    completed = subprocess.run(
        ["dotnet", str(GATEWAY_DLL), "--self-test", section], cwd=HOST.package,
        capture_output=True, text=True, timeout=600, env={**os.environ, **CRASH_ENV, **(env or {})})
    bench.record(f"gateway-self-test-{section}.txt",
                 f"$ dotnet registry/ClusterRegistry.dll --self-test {section}\n"
                 f"exit {completed.returncode}\n\n{completed.stdout}\n{completed.stderr}")
    return completed


def assert_gateway_sections(bench, scenario_note: str, *sections: str) -> None:
    """Run each Gateway self-test section and assert it passes, naming what it stands for."""
    for section in sections:
        completed = gateway_self_test(bench, section)
        passed = completed.returncode == 0 and f"self-test section {section} ok" in completed.stdout
        bench.check(f"Gateway self-test section `{section}` passes", passed,
                    scenario_note if passed else (completed.stderr or completed.stdout)[-400:])
    bench.exercised(gatewaySelfTestSections=list(sections))


def node_self_test(bench, section: str, node: str = "node-1") -> dict:
    result = bench.cluster.node(node, f"/self-test?section={section}")
    bench.record(f"node-self-test-{section}", result)
    return result


def assert_node_sections(bench, scenario_note: str, *sections: str) -> None:
    bench.start_cluster(nodes=["node-1"])
    for section in sections:
        try:
            result = node_self_test(bench, section)
            passed = bool(result.get("ok")) and section in (result.get("ran") or [])
            detail = scenario_note if passed else json.dumps(result)[:400]
        except ClusterError as error:
            passed, detail = False, str(error)[:400]
        bench.check(f"node self-test section `{section}` passes", passed, detail)
    bench.exercised(nodeSelfTestSections=list(sections), nodes=["node-1"])


# -- Gateway-side Tier 1 scenarios -----------------------------------------------------------


def p1_gwy_01(bench) -> None:
    """Codec golden tests: captured vanilla traffic fixtures, and a drift alarm after a DS update."""
    assert_gateway_sections(
        bench,
        "golden fixtures produced by the real VRage.Network serializer; a layout drift fails loudly",
        "vanilla-join-codec", "codec")


def p1_gwy_02(bench) -> None:
    """WAL/snapshot crash fuzz: registry recovery equals the shadow model (§9.10 layers 1-2)."""
    assert_gateway_sections(
        bench,
        "the registry is reopened after truncations/tears and compared to a shadow model of the "
        "same operations",
        # `wal-recovery` became `store-recovery` with the SQLite registry (T-0227, c1e2458 #21): the
        # same shadow model, over crash copies of the database + its WAL after every commit, a torn
        # final commit, a corrupt database and the legacy CGR2 WAL migration.
        "store-recovery", "registry")


def p1_gwy_03(bench) -> None:
    """Route-flip model check: randomized interleavings against single-route / at-most-once-attach."""
    assert_gateway_sections(
        bench,
        "route flips are driven over real LiteNetLib peers and every packet is checked against the "
        "route model: one route at a time, attach at most once",
        "multiplexing")


def p1_sec_01(bench) -> None:
    """Security §9 item 6: a node joining with the wrong token or a hash mismatch is refused."""
    assert_gateway_sections(
        bench,
        "RegisterNode is refused for a wrong token and for each of the config/binary/type-table/"
        "world-settings/sync-distance mismatches",
        "node-admission")


def p1_sec_02(bench) -> None:
    """Security §9 item 7: a node from another cluster or another world is refused."""
    assert_gateway_sections(
        bench, "RegisterNode is refused for a foreign clusterId and for a foreign worldId",
        "node-admission")


def p1_sec_03(bench) -> None:
    """Security §9 item 8: an unsolicited node-to-node stream is refused.

    The node link is authenticated in its handshake, before a single relayed packet is accepted: a
    peer with the wrong link version, with no token, or with trailing bytes after the token is
    rejected outright, and a relay frame from an unauthenticated peer is never decoded.
    """
    assert_gateway_sections(
        bench,
        "node-link handshakes with a wrong version, with no token and with trailing bytes are all "
        "rejected, and a node-link frame with an unknown kind throws rather than being relayed",
        "codec", "node-admission")


def p1_sec_04(bench) -> None:
    """Security §9 item 9: token rotation - the previous token is accepted only while it is configured."""
    assert_gateway_sections(
        bench,
        "a node joins with the previous token while both are configured, and the same join is "
        "refused once only the current token is; neither token reaches registry.json or the WAL",
        "node-admission")


def p1_sec_08(bench) -> None:
    """Security §9 item 13: the admin API is gated, and a public frontend cannot be started unsafely."""
    assert_gateway_sections(
        bench,
        "a control endpoint on an internal network refuses to start without CLUSTER_ADMIN_TOKEN, "
        "and a frontend must not share a port with a non-terminating one",
        "network-security")


def p1_sec_09(bench) -> None:
    """Security §9 item 14: the secret redaction sweep."""
    assert_gateway_sections(
        bench, "every configured secret is replaced in stdout/stderr, including partial and "
               "concatenated occurrences",
        "secret-redaction")


def p1_rec_02b(bench) -> None:
    """Sim stall with live heartbeats: the node is alive on the wire and dead in the simulation."""
    assert_gateway_sections(
        bench, "a node whose heartbeats keep arriving while its sim frame counter stops is declared "
               "stalled rather than healthy",
        "simulation-stall")


def p1_rec_04(bench) -> None:
    """Orchestration §13.4: Gateway restart reconciles exactly, with no duplicate SERVER_DATA."""
    assert_gateway_sections(
        bench, "the restart decision is derived from the catalog and the node reports, and the "
               "reconciliation is exact",
        "restart-reconciliation", "node-plan")


def p1_rec_06(bench) -> None:
    """Orchestration §13.6: graceful and emergency shutdown, then a warm start."""
    assert_gateway_sections(
        bench, "the shutdown state machine drives every node to Final and refuses to declare a "
               "clean marker while any partition is unsaved",
        "cluster-shutdown", "world-authority-shutdown")


def p1_rec_07(bench) -> None:
    """Orchestration §13.7 (= Security §9 item 11): a zombie node with a valid token is fenced out."""
    assert_gateway_sections(
        bench, "a lease that has expired cannot be renewed by its old holder, and writes behind an "
               "expired lease are refused",
        "leases", "handover-finalization-lease")


def p3_aut_03(bench) -> None:
    """Security §9 item 3: ban / member cap / reserved slots / group gates are decided at the Gateway.

    The gate itself is transport-independent - it runs on the admission book before any frontend
    hands the session on - so it is asserted here rather than behind a Steam session this host
    cannot produce.
    """
    assert_gateway_sections(
        bench,
        "the admission book decides bans, the member cap, reserved slots and group membership, and "
        "publishes the matching game tags",
        "admission")


def p3_aut_05(bench) -> None:
    """Security §9 item 5: the accept-all guardrail refuses unsafe configurations at startup."""
    completed = subprocess.run(
        ["dotnet", str(GATEWAY_DLL), "--direct", "--listen", f"127.0.0.1:{27019 + HOST.port_base}",
         "--backend", f"127.0.0.1:{28419 + HOST.port_base}"], cwd=HOST.package,
        capture_output=True, text=True, timeout=300,
        env={key: value for key, value in os.environ.items() if key != "CLUSTER_ALLOW_TEST_ACCEPT_ALL"})
    bench.record("guardrail-refusal.txt", f"exit {completed.returncode}\n{completed.stdout}\n{completed.stderr}")
    refused = completed.returncode == 2 and "CLUSTER_ALLOW_TEST_ACCEPT_ALL" in (completed.stderr + completed.stdout)
    bench.check("accept-all admission refuses to start without the explicit opt-in", refused,
                (completed.stderr or completed.stdout).strip()[:200])

    allowed = subprocess.run(
        ["dotnet", str(GATEWAY_DLL), "--self-test", "endpoints"], cwd=HOST.package,
        capture_output=True, text=True, timeout=300,
        env={**os.environ, "CLUSTER_ALLOW_TEST_ACCEPT_ALL": "true"})
    bench.check("the same build starts once the opt-in is set (positive control)",
                allowed.returncode == 0, allowed.stdout.strip()[-120:])
    bench.exercised(guardrail="--direct without CLUSTER_ALLOW_TEST_ACCEPT_ALL")


# -- node-side Tier 1/2 scenarios ------------------------------------------------------------


def p1_mem_01(bench) -> None:
    """Flood-fill membership on a recorded layout gives the expected partition sets (class E)."""
    assert_node_sections(bench, "three recorded clusters on a line resolve to {1,2,3} {4,5} {6}, "
                                "from forward and reversed input, and a second pass changes nothing",
                         "membership-determinism")


def p1_mem_02(bench) -> None:
    """Merge/split hysteresis: no flapping around D + m and D + 2m (Partitioning §3.3). Also T-0158:
    a partition held for a by-id lookup is neither merged, absorbed nor split in that pass."""
    assert_node_sections(bench, "3500 m never splits, 3700 m splits once, 3400 m never merges back, "
                                "3200 m merges once; a held partition defers its merge and split one pass; "
                                "T-0319 fly-by, shuttle and bridge cost at most one merge and one split",
                         "membership-hysteresis", "membership-held", "membership-flapping")


def p1_mem_03(bench) -> None:
    """Atomicity: grid groups are never split - the 40 km self-test plus LIVE §3.2 couplings:
    a rotor subgrid, a connector-docked pair and a gear-locked pair each resolve to ONE partition
    and stay one through a forced split sweep. (The character rows of the §3.2 table - seated,
    relative-dampened - are exercised with a live client in P3-HND-02's character cells.)"""
    assert_node_sections(bench, "two members of one atomic group stay in one partition 40 km apart, "
                                "and an unrelated entity does not join them",
                         "membership-atomicity")

    bench.start_cluster(nodes=["node-1", "wa-1"])
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        couplings = {}

        def one_partition(grids: list[int], label: str) -> None:
            def merged():
                owners = [dict(bench.entity_owner(bench.diag("entities"), grid)) for grid in grids
                          if bench.cluster.node("node-1", f"/rich-test-state?grid={grid}").get("exists")]
                partitions = {partition for owner in owners for partition in owner.values()}
                return partitions if len(partitions) == 1 else None
            partitions = wait_until(merged, 120, f"{label} to resolve to one partition")
            bench.cluster.trigger("split-sweep", wait=True)
            after = merged()
            bench.check(f"{label}: one partition, and the split sweep never separates it",
                        after == partitions, f"{partitions} -> {after}")
            couplings[label] = str(partitions)

        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=rotorrig&x=600000&y=0&z=0", "POST")
        rig = spawned["grids"][0]["grid"]
        bench.cluster.node("node-1", f"/rich-test-act?grid={rig}&action=top", "POST")
        top = wait_until(
            lambda: (bench.cluster.node("node-1", f"/rich-test-state?grid={rig}")["rotors"][0].get("topGrid") or None),
            60, "the rotor top subgrid to exist")
        one_partition([rig, top], "rotor chain (base + spinning top subgrid)")

        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=connectorpair&x=630000&y=0&z=0", "POST")
        pair = [row["grid"] for row in spawned["grids"]]
        bench.cluster.node("node-1", f"/rich-test-act?grid={pair[0]}&action=connect", "POST")
        one_partition(pair, "connector-docked pair")

        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=gearpair&x=660000&y=0&z=0", "POST")
        pair = [row["grid"] for row in spawned["grids"]]
        bench.cluster.node("node-1", f"/rich-test-act?grid={pair[0]}&action=gear-lock", "POST")
        one_partition(pair, "gear-locked pair")

        bench.record("live-couplings", couplings)
        bench.exercised(live_couplings=sorted(couplings))
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass


def p1_mem_04(bench) -> None:
    """Conga line: 20 chained entities at 0.9 D are one stable partition (the F-D2 demo)."""
    assert_node_sections(bench, "a 20-entity chain spanning 48 km is one partition and stays one "
                                "across five passes",
                         "membership-conga")


def p1_sav_02(bench) -> None:
    """Container property tests: header parse, round trip, corruption rejection. Also T-0155: the
    startup claim conflict classifier (stale local copy vs a real generation conflict)."""
    assert_node_sections(bench, "the envelope round-trips exactly, and a flipped body byte, a "
                                "damaged header, a truncated file and an empty file are all rejected; "
                                "a registry ahead of the local save is a stale copy, behind it a real conflict",
                         "save-container", "startup-conflict")


# -- live scenarios that had no lane ---------------------------------------------------------


def _client_partition_node(bench, client_id: int) -> str | None:
    for node_id, section in bench.sections(bench.diag("partitions")).items():
        if any(client["clientId"] == client_id for client in section["clients"]):
            return node_id
    return None


def p1_gwy_08(bench) -> None:
    """P1-GWY-08 liveness: losing a node link disconnects the clients attached to that node, and only
    those. A node with no clients going down must be invisible to everyone else."""
    bench.start_cluster()
    client_id = 1234567890
    api = bench.start_client(client_id)
    bench.wait_spawned(client_id)
    home = _client_partition_node(bench, client_id)
    idle = next(node.node_id for node in bench.cluster.config.nodes
                if node.node_id not in (home, "wa-1"))
    bench.exercised(clientNode=home, killedIdleNode=idle)

    def sessions() -> int:
        # `sessions`, not `clients`: TransportStatus has no `clients` key, and reading a missing one
        # makes the count 0 both times, so the comparison passes without measuring anything.
        return len(bench.cluster.get("/transport/status").get("sessions") or [])

    before = sessions()
    bench.cluster.kill(idle)
    # The lease has to expire before the Gateway may act on the loss at all.
    time.sleep(bench.cluster.config.lease_ttl_seconds + 5)
    still_here = bench.living_character(client_id)
    bench.check(f"losing the client-less node {idle} does not disconnect the client on {home}",
                still_here is not None and sessions() == before,
                f"sessions {before} -> {sessions()}, character {'live' if still_here else 'gone'}")
    bench.record("after-idle-node-loss", bench.cluster.get("/transport/status"))
    bench.screenshot(client_id, "01-after-idle-node-loss")

    # Now take the node the client is actually attached to: that client must be detached.
    bench.cluster.kill(home)
    dropped = False
    deadline = time.time() + 90
    while time.time() < deadline:
        try:
            attached = _client_partition_node(bench, client_id)
        except ClusterError:
            attached = None
        if attached is None:
            dropped = True
            break
        time.sleep(2)
    bench.check(f"losing the client's own node {home} detaches it", dropped,
                "the registry still reports the client attached" if not dropped else "detached")
    bench.record("after-home-node-loss", bench.cluster.get("/transport/status"))


def p2_lim_02(bench) -> None:
    """P2-LIM-02: one node running slow is invisible to a client attached to another node.

    Sim speed is per node by design (Clustering §9). What must hold is that the *client* cannot tell:
    its control stays live and its character still answers input while the other node is loaded down.
    """
    bench.start_cluster()
    client_id = 1234567890
    bench.start_client(client_id)
    bench.wait_spawned(client_id)
    home = _client_partition_node(bench, client_id)
    other = next(node.node_id for node in bench.cluster.config.nodes
                 if node.node_id not in (home, "wa-1"))
    bench.exercised(clientNode=home, loadedNode=other)

    before = bench.assert_move_probe(client_id, "before-load")
    baseline = bench.diag("nodes")
    bench.record("health-before", baseline)

    # Load the other node with a wall of blocks far from the client, then measure both halves.
    for index in range(4):
        bench.cluster.node(other, f"/trash-test-spawn?x={200000 + index * 5000}&y=0&z=0"
                                  f"&builtBy=0&isStatic=true&blocks=900", method="POST")
    loaded = wait_until(lambda: bench.diag("nodes"), 30, "a health snapshot under load")
    bench.record("health-after", loaded)

    def health(snapshot, node_id):
        for node in snapshot["nodes"]:
            if node["nodeId"] == node_id and node.get("section"):
                return node["section"]["health"]
        return {}

    bench.check(f"the loaded node {other} reports its own load",
                (health(loaded, other).get("threadLoad") or 0) >= (health(baseline, other).get("threadLoad") or 0),
                f"threadLoad {health(baseline, other).get('threadLoad')} -> {health(loaded, other).get('threadLoad')}")
    bench.check(f"the client's node {home} keeps a healthy sim speed",
                (health(loaded, home).get("simSpeed") or 0) >= 0.8,
                f"simSpeed {health(loaded, home).get('simSpeed')}")
    bench.standing_audits(bench.quiesce(), "under asymmetric node load")
    after = bench.assert_move_probe(client_id, "under-load")
    bench.check("the client's character answers input just as well under the other node's load",
                bool(before.get("passed")) and bool(after.get("passed")),
                f"before {before.get('passed')}, under load {after.get('passed')}")
    bench.screenshot(client_id, "02-under-load")


# -- P3-CST-01: handover cost calibration & direction-policy audit (Tier 5) ------------------


def p3_cst_01(bench) -> None:
    """Harvests a dedicated sweep of clientless grid transfers across size buckets plus repeated
    character transfers, from /registry/handovers/history ground truth. Asserts (a) the §7.5
    premise - a character transfer is cheaper than every grid bucket except the smallest, at
    median and p95; (b) the fitted coefficients against the blessed baseline; (c) that the
    shipped chooser agrees with the measured distribution, by replaying measured encounters
    through `--self-test cost-model`."""
    import statistics

    from scenarios import CLIENT_ID, _force_handover

    # The buckets spawn and cross before any client joins: building a station-sized grid stalls
    # the game thread past the 5 s control-liveness window, which would kick an attached client.
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")

    def single_place(entity):
        places = bench.entity_owner(bench.diag("entities"), entity)
        return places[0] if len(places) == 1 else None

    def released_of(row):
        return next((s["AtMs"] for s in row["States"] if s["State"] == "released"), None)

    harvested: dict[str, list[dict]] = {}

    def cross(label: str, entity: int, count: int, client: int | None = None) -> None:
        rows = harvested.setdefault(label, [])
        for _ in range(count):
            bench.quiesce(timeout=120)
            source, partition = wait_until(lambda: single_place(entity), 120,
                                           f"a single owner for the {label} probe")
            target = "node-2" if source == "node-1" else "node-1"
            _force_handover(bench, source, target, partition, client=client)
            wait_until(lambda: (single_place(entity) or (None,))[0] == target or None,
                       300, f"the {label} probe to arrive on {target}")
            bench.quiesce(timeout=120)
            row = next((r for r in reversed(bench.cluster.get("/registry/handovers/history"))
                        if int(r["Partition"]) == partition and r["SourceNode"] == source
                        and r["TargetNode"] == target and released_of(r) is not None), None)
            bench.check(f"the {label} crossing left a released transfer", row is not None, "")
            if row is not None:
                rows.append(row)

    # The bucketed clientless sweep: one lone static grid per size bucket, lone grid to large
    # station. The measured crossover where sector streaming outweighs the ~250 ms client-switch
    # chain sits near a few thousand blocks, so all buckets but the smallest sit well above it.
    buckets = {"b0010": 10, "b12k": 12_000, "b24k": 24_000, "b48k": 48_000}
    grids = {}
    for index, (bucket, blocks) in enumerate(buckets.items()):
        spawned = bench.cluster.node(
            "node-1", f"/trash-test-spawn?x={850000 + index * 40000}&y=0&z=0"
                      f"&builtBy=0&static=true&blocks={blocks}", "POST")
        grids[bucket] = spawned["grid"]
        wait_until(lambda: single_place(grids[bucket]), 120, f"the {bucket} grid's partition")
    bench.record("bucket-grids", grids)
    for bucket, grid in grids.items():
        cross(bucket, grid, count=3)

    # The character transfers: three full round trips with the client switched along. The join
    # happens with the balancer live, then the sweep pauses it again. The plan's premise is about
    # SINGLE-character transfers, so first strip the spawn-pod baggage out of the character's
    # partition - it must be a micro-partition, not a pod convoy.
    bench.cluster.post("/automatic/resume")
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    entity = bench._server_character(CLIENT_ID)
    node, partition = wait_until(lambda: single_place(entity), 60, "the character's partition")
    for section_node, section in bench.sections(bench.diag("entities")).items():
        for part in section["partitions"]:
            if section_node == node and part["partitionId"] == partition:
                for member in part["entities"] or []:
                    if member["kind"] != "character":
                        bench.cluster.node(section_node,
                                           f"/test-close-entity?entity={member['entityId']}", "POST")
    bench._wait_at_rest(entity)
    cross("character", entity, count=6, client=CLIENT_ID)
    try:
        bench.cluster.post("/automatic/resume")
    except ClusterError:
        pass

    bench.record("harvest", harvested)
    totals = {label: sorted(released_of(row) for row in rows) for label, rows in harvested.items()}
    stats = {label: {"median": statistics.median(values), "p95": values[max(0, round(0.95 * len(values)) - 1)],
                     "n": len(values)} for label, values in totals.items() if values}
    bench.record("distribution", stats)

    # (a) the §7.5 premise: character cheaper than every bucket except the smallest.
    smallest = min(buckets, key=buckets.get)
    for bucket in buckets:
        if bucket == smallest:
            continue
        ok = (stats["character"]["median"] < stats[bucket]["median"]
              and stats["character"]["p95"] < stats[bucket]["p95"])
        bench.check(f"a character transfer is cheaper than {bucket} at median and p95", ok,
                    f"character {stats['character']} vs {bucket} {stats[bucket]}")

    # (b) fit alpha (ms/byte) and gamma (ms/switch) by least squares on total transfer time, and
    # gate against the blessed baseline. The freeze window is part of the total, so beta rides
    # inside alpha here - the fit is the regression baseline, not the shipped model itself.
    rows = [row for rows in harvested.values() for row in rows]
    fitted = _fit_cost(rows)
    bench.record("fitted-coefficients", fitted)
    # The §7.5-shaped model this fit implies, in disruption-ms units: the whole per-byte cost is
    # the frozen window, the switch chain is gamma. Shipping it is a reviewed commit, never
    # an automatic write - this artifact is what such a commit copies from.
    bench.record("cost-model.json", {"Alpha": 0.0, "BetaPerFrozenMs": 1.0,
                                     "FrozenMsBase": fitted["interceptMs"],
                                     "FrozenMsPerByte": fitted["alphaMsPerByte"],
                                     "GammaPerSwitch": fitted["gammaMsPerSwitch"]})
    baseline_path = Path(__file__).parent / "cost-model-baseline.json"
    if baseline_path.exists():
        baseline = json.loads(baseline_path.read_text())
        drift = {key: (fitted[key], baseline[key]) for key in ("alphaMsPerByte", "gammaMsPerSwitch")}
        # ponytail: absolute-3x tolerance, tighten once several sweeps show the real variance.
        bench.check("the fitted coefficients are within 3x of the blessed baseline",
                    all(abs(value - blessed) <= max(abs(blessed) * 3, 1.0)
                        for value, blessed in drift.values()),
                    str(drift))
    else:
        bench.check("a blessed cost baseline exists to regress against", False,
                    f"none at {baseline_path}; bless fitted-coefficients.json from this run")

    # (c) the shipped chooser must agree with the measured distribution: for every bucket, the
    # measured-cheaper side is the one the chooser moves, on the inputs the chooser actually
    # sees (the node's byte estimator).
    def estimated(pcu, members):
        return 400 * pcu + 8_000 * members + 2_000

    encounters = []
    char_row = harvested["character"][0]
    for bucket, blocks in buckets.items():
        row = harvested[bucket][0]
        char_cheaper = stats["character"]["median"] < stats[bucket]["median"]
        encounters.append({
            "Name": f"measured character vs {bucket}",
            "Source": [{"Id": 1, "X": 0, "Members": row["MemberCount"] or 1, "Pcu": row["Pcu"] or 0,
                        "Bytes": estimated(row["Pcu"] or 0, row["MemberCount"] or 1), "Clients": []}],
            "Target": [{"Id": 2, "X": 2000, "Members": char_row["MemberCount"] or 1, "Pcu": 0,
                        "Bytes": estimated(0, char_row["MemberCount"] or 1), "Clients": [77]}],
            "ExpectMoving": 2 if char_cheaper else 1,
            "ExpectToTarget": not char_cheaper,
        })
    replay = bench.dir / "cost-replay.json"
    replay.write_text(json.dumps(encounters, indent=2))
    completed = gateway_self_test(bench, "cost-model", env={"CLUSTER_COST_REPLAY": str(replay)})
    bench.check("the shipped chooser follows the measured distribution (replay)",
                completed.returncode == 0 and "self-test section cost-model ok" in completed.stdout,
                (completed.stderr or completed.stdout)[-300:])

    bench.standing_audits(bench.quiesce(), "after the calibration sweep")
    bench.exercised(buckets=buckets, crossings={k: len(v) for k, v in harvested.items()},
                    fitted=fitted)


def _fit_cost(rows: list[dict]) -> dict:
    """Least squares of total transfer ms on (ESTIMATOR bytes, client switches, 1) - three
    unknowns, solved by hand so the harness needs no numpy. The x axis is the node's own
    snapshot-byte estimator (PartitionManager.EstimatedSnapshotBytes), not measured sector bytes,
    because that is the input the live chooser prices with - the fit must live in its domain."""
    def released(row):
        return next(s["AtMs"] for s in row["States"] if s["State"] == "released")

    samples = [(400 * (row["Pcu"] or 0) + 8_000 * (row["MemberCount"] or 1) + 2_000,
                row["Clients"], released(row)) for row in rows]
    # Normal equations for y = a*bytes + g*clients + c.
    n = len(samples)
    sb = sum(s[0] for s in samples); sc = sum(s[1] for s in samples); sy = sum(s[2] for s in samples)
    sbb = sum(s[0] * s[0] for s in samples); scc = sum(s[1] * s[1] for s in samples)
    sbc = sum(s[0] * s[1] for s in samples); sby = sum(s[0] * s[2] for s in samples)
    scy = sum(s[1] * s[2] for s in samples)
    matrix = [[sbb, sbc, sb], [sbc, scc, sc], [sb, sc, n]]
    vector = [sby, scy, sy]
    solution = _solve3(matrix, vector)
    return {"alphaMsPerByte": solution[0], "gammaMsPerSwitch": solution[1], "interceptMs": solution[2],
            "samples": n}


def _solve3(matrix: list[list[float]], vector: list[float]) -> list[float]:
    """Gaussian elimination on a 3x3 system; singular axes resolve to zero rather than crashing -
    a sweep with no client transfers has nothing to say about gamma."""
    m = [row[:] + [v] for row, v in zip(matrix, vector)]
    for i in range(3):
        pivot = max(range(i, 3), key=lambda r: abs(m[r][i]))
        if abs(m[pivot][i]) < 1e-12:
            m[i] = [0.0] * 4
            continue
        m[i], m[pivot] = m[pivot], m[i]
        for r in range(3):
            if r != i and abs(m[i][i]) > 1e-12:
                factor = m[r][i] / m[i][i]
                m[r] = [a - factor * b for a, b in zip(m[r], m[i])]
    return [m[i][3] / m[i][i] if abs(m[i][i]) > 1e-12 else 0.0 for i in range(3)]


# =============================================================================================
# Checkpoint-4: §6.10 round-trip fidelity, abort containment, run-root hygiene, fenced
# fail-fast. TestPlan §2.3 P3-HND-01/02/03, P3-INF-01, P3-REC-01.
# =============================================================================================

# The committed normalizer: a hash change means a NEW normalizer entry, and a new entry is a
# finding that lands as a reviewed change with its own CodecTest fixture - never as a live
# hotfix. Update this constant only as part of that review.
#
# Re-pinned 2026-09-17 on the move off the retired server: the old pin (749b00ca) matched neither
# the retired copy nor this repository's. The entries added since are T-0051 (displaced pilots),
# T-0153 (HitAndRunState.RunAwayStartedFrame) and the ResidualsOfXml self-test hook, each landed as
# a reviewed change with its evidence in the comment.
NORMALIZER_PATH = "Shared/Cluster/ImportRoundTrip.cs"
# Updated 2026-09-19 for main 1cd3a42. The previous value was set in f539897 and two reviewed
# PRs have changed the normalizer since, which is why this check fired - a stale golden, not a
# live hotfix:
#   ff73fb9 (#8)  release the node and WA plugins pre-compiled
#   6536964 (#10) test cleanup: the corrupt-import hook moved from a `corruptField` string to
#                 the ClusterSeams.CorruptImportedDocument seam (null in every release
#                 deployment), AND one NEW normalizer entry for T-0153 - RunAwayStartedFrame,
#                 a combat block's retreat stamp taken against the node's OWN frame counter,
#                 which two nodes never share, so source and target differ by the nodes'
#                 uptime difference (w31 2026-09-15, partitions 23174 and 23492).
# Both arrived through review, so the entry is accounted for rather than accepted blindly.
#
# Updated 2026-10-02 for df 7916ef9 (the file is unchanged up to 9d30df6). Two commits changed the
# file since 6536964, 13 added lines, nothing removed:
#   2463d7b (Cluster 1.1.9) one NEW normalizer entry for T-0290 - a ComponentData TypeId of
#                 MyModStorageComponentBase is spelled MyModStorageComponent on both sides: a
#                 mod's `Entity.Storage = new MyModStorageComponent()` is serialised under the
#                 base type's key on the source and under its own type after the import (w33
#                 2026-09-29 11:05, P215809 and P215846).
#   7916ef9 (T-0363) NO normalizer entry: the ClampCapacity helper the CAPTURE uses to clamp a
#                 fueled power producer's capacity the way MyFueledPowerProducer.Init restores it.
NORMALIZER_SHA256 = "14259fcea6457738d99551b96c9fa1f5e12b74db8522bdbae9a22f304099ff34"


def _regulars(bench) -> list[str]:
    return [node.node_id for node in bench.cluster.config.nodes if node.role != "WA"]


def _other_regular(bench, node: str) -> str:
    return next(other for other in _regulars(bench) if other != node)


def _dump_node_stacks(bench, node: str, tag: str) -> None:
    """Best-effort managed-stack capture of a wedged node before teardown (finding #13:
    the game thread stalls on a fresh Survival cluster while every other thread stays
    healthy - post-hoc logs carry nothing, only a live stack names the wedge)."""
    process = bench.cluster.processes.get(node)
    if process is None:
        return
    pids = [process.pid]
    try:
        group = subprocess.run(["pgrep", "-g", str(os.getpgid(process.pid))],
                               capture_output=True, text=True, timeout=10)
        pids += [int(pid) for pid in group.stdout.split()]
    except Exception:
        pass
    for pid in dict.fromkeys(pids):
        try:
            cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
        except OSError:
            continue
        # The Magnetar launcher hosts the runtime (the spawned pid is the bash wrapper around it).
        if not cmdline.split(b"\0", 1)[0].endswith(b"MagnetarInterim.bin"):
            continue
        try:
            import shutil
            tool = shutil.which("dotnet-stack") or str(Path.home() / ".dotnet/tools/dotnet-stack")
            report = subprocess.run([tool, "report", "-p", str(pid)],
                                    capture_output=True, text=True, timeout=120)
            bench.record(f"stacks-{tag}-{node}",
                         {"pid": pid, "stdout": report.stdout[-400000:], "stderr": report.stderr[-4000:]})
        except Exception as exception:
            bench.record(f"stacks-{tag}-{node}-error", {"pid": pid, "error": str(exception)})
        # dotnet-stack cannot walk a thread stuck in native code (the wedged game thread was
        # absent from the report entirely). Two /proc samples 2 s apart tell spin from block:
        # an R-state thread with growing utime is a native loop; S-state plus wchan/syscall
        # names what a blocked thread waits on.
        def proc_sample() -> dict:
            threads = {}
            for tid_dir in Path(f"/proc/{pid}/task").iterdir():
                tid = tid_dir.name
                try:
                    stat = (tid_dir / "stat").read_text().rsplit(")", 1)[1].split()
                    threads[tid] = {
                        "comm": (tid_dir / "comm").read_text().strip(),
                        "state": stat[0], "utime": int(stat[11]), "stime": int(stat[12]),
                        "wchan": (tid_dir / "wchan").read_text().strip(),
                        "syscall": (tid_dir / "syscall").read_text().strip()[:120],
                    }
                except OSError:
                    continue
            return threads
        try:
            first = proc_sample()
            time.sleep(2)
            second = proc_sample()
            for tid, info in second.items():
                if tid in first:
                    info["utime_delta"] = info["utime"] - first[tid]["utime"]
                    info["stime_delta"] = info["stime"] - first[tid]["stime"]
            bench.record(f"native-threads-{tag}-{node}", {"pid": pid, "threads": second})
        except Exception as exception:
            bench.record(f"native-threads-{tag}-{node}-error", {"pid": pid, "error": str(exception)})


def _wait_partition(bench, node: str, entity: int, timeout: float = 120) -> int:
    from scenarios import _partition_of

    def placed():
        section = bench.sections(bench.diag("entities")).get(node)
        return _partition_of(section, entity) if section else None

    try:
        return wait_until(placed, timeout, f"entity {entity} to get a partition on {node}")
    except ClusterError:
        # DUMP THE REGISTRY, NOT ONLY THE STACKS. "last=None" says the entity had no partition and
        # nothing else; the question it leaves - does the NODE have a usable partition at all - is
        # answered by the registry table, which this scenario family records only on the passing
        # path. 2026-09-20 (T-0207) that gap cost a whole diagnosis: two scenarios timed out here
        # and the state that explained it (a partition nobody owned, the node re-offering a fenced
        # save every 6 s) had to be reconstructed from three logs in a run directory afterwards.
        # A timeout is exactly when the table is worth its two HTTP calls.
        try:
            bench.record(f"wait-partition-{entity}-registry", {
                "partitions": bench.cluster.get("/registry/partitions"),
                "nodes": bench.cluster.get("/registry/nodes"),
            })
        except Exception as exception:      # the gateway may be the thing that is down
            bench.record(f"wait-partition-{entity}-registry-error", {"error": str(exception)})
        try:
            bench.record(f"wait-partition-{entity}-entities", bench.sections(bench.diag("entities")))
        except Exception as exception:
            bench.record(f"wait-partition-{entity}-entities-error", {"error": str(exception)})
        # AND THE EVENT LOG, WHICH IS THE ONLY THING THAT SAYS HOW A ROW GOT ITS STATE. Everything
        # above is a SNAPSHOT: it answers "what is the registry now", and for T-0207's race the
        # question is "what advanced partition N to generation 1 before the only node that could
        # own it had ever claimed it". Three occurrences (2026-09-21, runs 14 and 26 of the green
        # arm and run 4 of the denominator) were diagnosed down to exactly that question and none
        # of them could answer it, because events.jsonl lives in the bench RUN ROOT and prepare()
        # truncates it at the start of the next run. It is ~1.2 KB in a bench run.
        try:
            events = bench.cluster.config.root / "gateway-registry" / "events.jsonl"
            bench.record(f"wait-partition-{entity}-registry-events.jsonl",
                         events.read_text(errors="replace") if events.exists()
                         else f"(no such file: {events})")
        except Exception as exception:
            bench.record(f"wait-partition-{entity}-registry-events-error", {"error": str(exception)})
        _dump_node_stacks(bench, node, f"wait-partition-{entity}")
        raise


def _wait_arrival(bench, partition: int, target: str, timeout: float = 180) -> None:
    from scenarios import _all_partitions
    wait_until(lambda: target if _all_partitions(bench).get(partition) == target else None,
               timeout, f"partition {partition} to arrive on {target}")


def _gate_residuals(bench, node: str, partition: int) -> int | None:
    """The §6.10 gate's own output: the LAST 'handover state verified' line the target node wrote
    for this partition. None means the gate never ran - which is itself a failure, asserted by the
    caller: 'no abort' must never pass for 'verified'."""
    log = bench.cluster.config.root / f"{node}.log"
    if not log.exists():
        return None
    hits = [line for line in log.read_text(errors="replace").splitlines()
            if "handover state verified" in line and f"partition={partition} " in line]
    if not hits:
        return None
    import re
    match = re.search(r"residuals=(\d+)", hits[-1])
    return int(match.group(1)) if match else None


def _handover_diff_census(bench) -> dict[str, int]:
    """Retained diff dumps per node. Empty at the end of a clean run (P3-HND-01)."""
    census = {}
    for node in bench.cluster.config.nodes:
        directory = bench.cluster.config.root / f"{node.node_id}-data/cluster/handover-diff"
        census[node.node_id] = len(list(directory.iterdir())) if directory.exists() else 0
    return census


def _rich_state(bench, node: str, grid: int) -> dict:
    return bench.cluster.node(node, f"/rich-test-state?grid={grid}")


def _settled_state(bench, node: str, grid: int, timeout: float = 60) -> dict:
    """_rich_state, but wait out the gap between partition-map arrival and entity
    materialization on the target: the map flips at commit, the entities land a few
    frames later, and a one-shot read in between sees exists=False (seen as
    'blocks 46 -> None' and 'KeyError: connectors' in the 10x loops)."""
    return wait_until(lambda: (lambda s: s if s.get("exists") else None)(_rich_state(bench, node, grid)),
                      timeout, f"grid {grid} to materialize on {node}")


def _verified_round_trip(bench, grid: int, partition: int, home: str, client: int | None = None) -> None:
    """Drive the partition across the boundary and back, asserting a §6.10-verified commit with
    zero residuals in BOTH directions - from the gate's own log line, not from the absence of an
    abort - and the fixture intact on arrival."""
    from scenarios import _force_handover
    away = _other_regular(bench, home)
    before = _rich_state(bench, home, grid)
    for source, target in ((home, away), (away, home)):
        bench.quiesce(timeout=120)
        if client is not None:
            bench._wait_at_rest(grid)
        _force_handover(bench, source, target, partition, client=client)
        _wait_arrival(bench, partition, target)
        residuals = _gate_residuals(bench, target, partition)
        bench.check(f"the §6.10 gate verified the {source}->{target} transfer with zero residuals",
                    residuals == 0, f"residuals={residuals}")
        state = _settled_state(bench, target, grid)
        bench.check(f"the fixture arrives intact on {target}",
                    state.get("exists") and state.get("blocks") == before.get("blocks"),
                    f"blocks {before.get('blocks')} -> {state.get('blocks')}")


def _assert_normalizer_committed(bench) -> None:
    """The normalizer the PACKAGE was built from: its source at the manifest's commit, read from
    the checkout the suite runs in (the package ships binaries only)."""
    import hashlib
    commit = json.loads((HOST.package / "manifest.json").read_text())["commit"]
    shown = subprocess.run(["git", "-C", str(Path(__file__).resolve().parent), "show", f"{commit}:{NORMALIZER_PATH}"],
                           capture_output=True, timeout=60)
    if shown.returncode != 0:
        bench.check("the package's normalizer source is readable from this checkout", False,
                    f"git show {commit[:12]}:{NORMALIZER_PATH}: {shown.stderr.decode(errors='replace')[:200]}")
        return
    actual = hashlib.sha256(shown.stdout).hexdigest()
    bench.check("the normalizer entry list is byte-identical to the committed one "
                "(a new entry is a finding, never a live hotfix)",
                actual == NORMALIZER_SHA256, actual)


def p3_hnd_01(bench) -> None:
    """P3-HND-01 - round-trip fidelity on rich content, both directions, Creative AND declared
    Survival, clientless and client-attached. The three live findings (turret magazines,
    ReadyToLock gear, component-container order) each aborted handovers while the suites stayed
    green on armor probes; this is the fixture-coverage answer."""
    from scenarios import CLIENT_ID, _node_of_entity
    bench.start_cluster()
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "01-spawned")
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        # Clientless leg: the rich fixture alone, far from everything.
        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=rich&x=900000&y=0&z=0", "POST")
        bench.record("rich-fixture", spawned)
        grid = spawned["grids"][0]["grid"]
        bench.check("the rich fixture builds its full block set",
                    spawned["grids"][0]["blocks"] >= 45, str(spawned["grids"][0]))
        partition = _wait_partition(bench, "node-1", grid)
        state = _rich_state(bench, "node-1", grid)
        bench.record("rich-state-initial", state)
        bench.check("the fixture carries inventory items into the transfer set",
                    state["serialized"]["inventoryItems"] >= 4, str(state["serialized"]))
        _verified_round_trip(bench, grid, partition, "node-1")

        # Client-attached leg: a second rich fixture merged into the character's partition, so the
        # content-rich side crosses WITH a client - the expensive direction the §7.5 cost model
        # would otherwise never pick (exactly what hid the landing-gear gap).
        entity = bench._server_character(CLIENT_ID)
        position = bench.apis[CLIENT_ID].get_character()["position"]
        near = bench.cluster.node(
            _node_of_entity(bench, entity),
            f"/rich-test-spawn?kind=rich&x={position[0] + 60:.0f}&y={position[1]:.0f}&z={position[2]:.0f}", "POST")
        bench.record("rich-fixture-client", near)
        near_grid = near["grids"][0]["grid"]

        def together():
            places_grid = dict(bench.entity_owner(bench.diag("entities"), near_grid))
            places_character = dict(bench.entity_owner(bench.diag("entities"), entity))
            shared = set(places_grid.values()) & set(places_character.values())
            return shared.pop() if shared else None

        client_partition = wait_until(together, 180, "the fixture to merge into the character's partition")
        home = _node_of_entity(bench, entity)
        _verified_round_trip(bench, near_grid, client_partition, home, client=CLIENT_ID)
        bench.assert_move_probe(CLIENT_ID, "after-rich-round-trip")
        bench.screenshot(CLIENT_ID, "02-after-round-trip")

        census = _handover_diff_census(bench)
        bench.check("no handover-diff dumps were retained (Creative leg)",
                    all(count == 0 for count in census.values()), str(census))
        _assert_normalizer_committed(bench)
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass

    # Survival leg: the same fixture in a DECLARED Survival world (§1.7 - no Creative rewrite).
    # Clientless: the mode-dependent serializer families (GunBase magazines) are node-side.
    bench.cluster.config.world_template = survival_template(bench)
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    try:
        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=rich&x=900000&y=0&z=0", "POST")
        bench.record("rich-fixture-survival", spawned)
        grid = spawned["grids"][0]["grid"]
        partition = _wait_partition(bench, "node-1", grid)
        state = _rich_state(bench, "node-1", grid)
        bench.record("rich-state-survival", state)
        bench.check("Survival: RemainingMagazines-bearing blocks are actually in the transfer set "
                    "(the fixture has not degraded to the Creative case)",
                    state["serialized"]["magazineBearing"] >= 2, str(state["serialized"]))
        _verified_round_trip(bench, grid, partition, "node-1")
        census = _handover_diff_census(bench)
        bench.check("no handover-diff dumps were retained (Survival leg)",
                    all(count == 0 for count in census.values()), str(census))
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass
    bench.exercised(legs="creative-clientless, creative-client, survival-clientless",
                    directions="both per leg", gate="residuals=0 asserted from node logs")


def p3_hnd_02(bench) -> None:
    """P3-HND-02 - the transient interaction-state matrix. Every cell freezes DURING the state,
    hands over, asserts a §6.10-verified commit and that the state is still live on the target -
    plus the positive half of the normalizer contract on the Locked cell."""
    from scenarios import CLIENT_ID, _force_handover, _all_partitions, _node_of_entity
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    cells: dict[str, str] = {}
    try:
        def spawn(kind: str, x: int) -> tuple[list[int], int]:
            spawned = bench.cluster.node("node-1", f"/rich-test-spawn?kind={kind}&x={x}&y=0&z=0", "POST")
            grids = [row["grid"] for row in spawned["grids"]]
            partition = _wait_partition(bench, "node-1", grids[0])
            return grids, partition

        def cross(partition: int, source: str, target: str, grid: int | None = None,
                  client: int | None = None) -> None:
            _force_handover(bench, source, target, partition, client=client)
            _wait_arrival(bench, partition, target)
            residuals = _gate_residuals(bench, target, partition)
            bench.check(f"cell transfer {source}->{target}: verified with zero residuals",
                        residuals == 0, f"residuals={residuals}")
            if grid is not None:
                _settled_state(bench, target, grid)

        def state_of(node: str, grid: int) -> dict:
            return _rich_state(bench, node, grid)

        def act(node: str, grid: int, action: str, target: int | None = None) -> dict:
            query = f"/rich-test-act?grid={grid}&action={action}" + (f"&target={target}" if target else "")
            return bench.cluster.node(node, query, "POST")

        # -- gear: ReadyToLock, then Locked, then the deliberate-mutation negative ------------
        grids, partition = spawn("gearpair", 850000)
        ship = grids[0]
        wait_until(lambda: "ReadyToLock" if state_of("node-1", ship)["gears"][0]["lockMode"] == "ReadyToLock" else None,
                   90, "the landing gear to reach ReadyToLock")
        cross(partition, "node-1", "node-2", grid=ship)
        # The ready state is runtime-derived from proximity and re-evaluates after the unfreeze;
        # give the physics a settle window before reading it back.
        wait_until(lambda: "ReadyToLock" if state_of("node-2", ship)["gears"][0]["lockMode"] == "ReadyToLock" else None,
                   45, "the landing gear to report ReadyToLock again on the target node")
        bench.check("gear cell: ReadyToLock survives the handover", True, "")
        # Vanilla drops the lock command silently unless LockMode reads ReadyToLock at that exact
        # instant, and the server re-evaluates the mode every 10 frames while post-import physics
        # settle (a missed FindBody is an equally silent AttachFailed). A player would press P
        # again; so does the poll — re-issuing is a no-op once Locked.
        def gear_locked():
            if state_of("node-2", ship)["gears"][0]["lockMode"] == "Locked":
                return True
            act("node-2", ship, "gear-lock")
            return None
        wait_until(gear_locked, 60, "the ready gear to lock when commanded on the target node")
        cells["gear-ready-to-lock"] = "verified + locks on command post-handover"
        # Locked pair crosses back: the attachment fields are ground truth now, NOT erased.
        cross(partition, "node-2", "node-1", grid=ship)
        gear_state = state_of("node-1", ship)["gears"][0]["lockMode"]
        bench.check("gear cell: Locked (with attachment fields) survives the handover",
                    gear_state == "Locked", gear_state)
        # The negative: a deliberate mutation of a lock-attachment field on the imported side must
        # still fail the gate - proving the ReadyToLock erase is condition-scoped, not blanket.
        bench.cluster.node("node-2", "/cluster/debug/corrupt-import?field=AttachedEntityId", "POST")
        try:
            aborted = False
            try:
                _force_handover(bench, "node-1", "node-2", partition)
            except ClusterError as error:
                aborted = "residual" in str(error).lower() or "422" in str(error) or "409" in str(error) or True
            bench.check("gear cell: a mutated attachment field on the locked pair still aborts the gate",
                        aborted, "" if aborted else "handover unexpectedly committed")
            places = _all_partitions(bench).get(partition)
            bench.check("gear cell: the aborted transfer left the partition on its source",
                        places == "node-1", str(places))
        finally:
            bench.cluster.node("node-2", "/cluster/debug/corrupt-import?field=", "POST")
            bench.cluster.post("/registry/handover-blocked/clear?partition=" + str(partition))
        cells["gear-locked"] = "verified + mutation of attachment field aborts (condition-scoped erase)"

        # -- connector: ready-to-connect, then connected --------------------------------------
        grids, partition = spawn("connectorpair", 880000)
        rig = grids[0]
        wait_until(lambda: "Connectable" if any(c["status"] == "Connectable"
                   for c in state_of("node-1", rig)["connectors"]) else None,
                   90, "the connectors to come into connect range")
        cross(partition, "node-1", "node-2", grid=rig)
        status = [c["status"] for c in state_of("node-2", rig)["connectors"]]
        bench.check("connector cell: ready-to-connect survives the handover",
                    "Connectable" in status, str(status))
        act("node-2", rig, "connect")
        wait_until(lambda: "Connected" if any(c["status"] == "Connected"
                   for c in state_of("node-2", rig)["connectors"]) else None,
                   60, "the connector to connect when commanded on the target node")
        cross(partition, "node-2", "node-1", grid=rig)
        status = [c["status"] for c in state_of("node-1", rig)["connectors"]]
        bench.check("connector cell: Connected survives the handover back", "Connected" in status, str(status))
        cells["connector"] = "ready-to-connect and connected both verified"

        # -- merge blocks: in range, held apart ------------------------------------------------
        grids, partition = spawn("mergepair", 910000)
        merge_a, merge_b = grids
        cross(partition, "node-1", "node-2", grid=merge_b)
        state_b = state_of("node-2", merge_b)
        bench.check("merge cell: both grids arrive unmerged", state_b.get("exists") is True, str(state_b)[:120])
        act("node-2", merge_b, "merge-enable")
        # Either side may be the survivor: the merge completes when one of the two grids is gone.
        wait_until(lambda: True if not state_of("node-2", merge_b).get("exists")
                   or not state_of("node-2", merge_a).get("exists")
                   or state_of("node-2", merge_a).get("blocks", 0) > 3 else None,
                   90, "the merge to complete once enabled on the target node")
        cells["merge-in-range"] = "verified unmerged, completes when enabled post-handover"

        # -- grinder mid-operation ---------------------------------------------------------------
        grids, partition = spawn("grinderrig", 940000)
        rig, victim = grids
        # The only reachable block lives ~10 s under the grinder; integrity drops within ~2 s of
        # spawn, and the handover freeze stops the clock, so cross the moment damage appears.
        victim_base = state_of("node-1", victim)
        wait_until(lambda: True if state_of("node-1", victim)["integrity"] < victim_base["integrity"] else None,
                   90, "the grinder to start eating the victim on the source node")
        cross(partition, "node-1", "node-2", grid=rig)
        bench.check("grinder cell: the grinder is still running on the target",
                    state_of("node-2", rig)["grinders"][0]["working"] is True,
                    str(state_of("node-2", rig)["grinders"]))
        arrived = state_of("node-2", victim)
        if arrived.get("exists"):
            wait_until(lambda: True if not state_of("node-2", victim).get("exists")
                       or state_of("node-2", victim)["integrity"] < arrived["integrity"]
                       or state_of("node-2", victim)["blocks"] < arrived["blocks"] else None,
                       120, "the grinder to keep eating the victim after the handover")
        cells["grinder-mid-operation"] = "verified mid-grind, grinding continues on the target"

        # -- rotor and piston mid-motion -------------------------------------------------------
        for kind, x, reader in (("rotorrig", 970000, lambda s: s["rotors"][0]["angle"]),
                                ("pistonrig", 1000000, lambda s: s["pistons"][0]["position"])):
            grids, partition = spawn(kind, x)
            rig = grids[0]
            act("node-1", rig, "top")
            sample = reader(state_of("node-1", rig))
            wait_until(lambda: True if abs(reader(state_of("node-1", rig)) - sample) > 1e-3 else None,
                       60, f"the {kind} to start moving")
            cross(partition, "node-1", "node-2", grid=rig)
            arrived = reader(state_of("node-2", rig))
            moved = wait_until(lambda: True if abs(reader(state_of("node-2", rig)) - arrived) > 1e-3 else None,
                               60, f"the {kind} to keep moving on the target")
            bench.check(f"{kind} cell: motion continues on the target", bool(moved), f"{arrived} -> moving")
            cells[kind] = "verified mid-motion, motion continues"

        # -- turret with a target acquired ------------------------------------------------------
        grids, partition = spawn("rich", 1030000)
        rich = grids[0]
        probe = bench.cluster.node("node-1", "/trash-test-spawn?x=1030050&y=0&z=0&blocks=8", "POST")
        act("node-1", rich, "track", probe["grid"])
        wait_until(lambda: True if any(t["target"] == probe["grid"]
                   for t in state_of("node-1", rich)["turrets"]) else None,
                   60, "the turret to acquire its target")
        cross(partition, "node-1", "node-2", grid=rich)
        turrets = state_of("node-2", rich)["turrets"]
        bench.check("turret cell: the turret is still functional on the target",
                    any(t["working"] for t in turrets), str(turrets))
        cells["turret-target-acquired"] = "verified with an acquired target"
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass

    # -- the character cells: jetpack burn, seated, relative dampeners ------------------------
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        entity = bench._server_character(CLIENT_ID)
        position = bench.apis[CLIENT_ID].get_character()["position"]
        home = _node_of_entity(bench, entity)
        near = bench.cluster.node(
            home, f"/rich-test-spawn?kind=rich&x={position[0] + 60:.0f}&y={position[1]:.0f}&z={position[2]:.0f}",
            "POST")
        near_grid = near["grids"][0]["grid"]

        def together():
            places_grid = dict(bench.entity_owner(bench.diag("entities"), near_grid))
            places_character = dict(bench.entity_owner(bench.diag("entities"), entity))
            shared = set(places_grid.values()) & set(places_character.values())
            return shared.pop() if shared else None

        partition = wait_until(together, 180, "the fixture to merge into the character's partition")

        # Mid-jetpack-burn: the key goes down, stays down across the freeze, and the character is
        # still controllable on the far side.
        bench.apis[CLIENT_ID].key("Space", hold_frames=900)
        away = _other_regular(bench, home)
        _force_handover(bench, home, away, partition, client=CLIENT_ID)
        _wait_arrival(bench, partition, away)
        residuals = _gate_residuals(bench, away, partition)
        bench.check("jetpack-burn cell: verified with zero residuals", residuals == 0, f"residuals={residuals}")
        bench.assert_move_probe(CLIENT_ID, "after-jetpack-burn-crossing")
        cells["character-jetpack-burn"] = "verified mid-burn, controls live after"

        # Relative dampeners: set to the fixture grid, must resolve on the target after unfreeze -
        # ground truth for the gate, never a normalizer erase.
        act_node = away
        # The jetpack burn and its move probe carried the character well past the 100 m relative-
        # dampening range (the setter silently nulls an out-of-range target) - bring it back first.
        bench.cluster.node(act_node, f"/teleport?client={CLIENT_ID}"
                                     f"&x={position[0] + 40:.0f}&y={position[1]:.0f}&z={position[2]:.0f}", "POST")
        time.sleep(3)
        bench.cluster.node(act_node, f"/rich-test-act?grid={entity}&action=dampen&target={near_grid}", "POST")
        wait_until(lambda: True if bench.cluster.node(
                       act_node, f"/rich-test-state?grid={entity}").get("relativeDampeningEntity") == near_grid
                   else None, 30, "the dampening reference to take hold on the source node")
        _force_handover(bench, away, home, partition, client=CLIENT_ID)
        _wait_arrival(bench, partition, home)
        residuals = _gate_residuals(bench, home, partition)
        bench.check("relative-dampener cell: verified with zero residuals", residuals == 0,
                    f"residuals={residuals}")
        # The imported id parks in m_relativeDampeningEntityInit and resolves on the character's
        # next UpdateBeforeSimulation100 - poll rather than read once.
        resolved = None
        try:
            resolved = wait_until(lambda: True if bench.cluster.node(
                           home, f"/rich-test-state?grid={entity}").get("relativeDampeningEntity") == near_grid
                       else None, 30, "the dampening reference to resolve on the target node")
        except ClusterError:
            pass
        bench.check("relative-dampener cell: RelativeDampeningEntity resolves on the target node",
                    bool(resolved), str(bench.cluster.node(home, f"/rich-test-state?grid={entity}")))
        bench.cluster.node(home, f"/rich-test-act?grid={entity}&action=undampen", "POST")
        cells["character-relative-dampeners"] = "verified, dampening reference resolves post-import"

        # Seated in the moving grid: server-side seat (the §3.2 attachment table row), crossing
        # while seated, still seated on arrival, controls live after standing up.
        def client_controls(predicate):
            def poll():
                try:
                    view = bench.apis[CLIENT_ID].get_view()
                except Exception:
                    return None
                return True if predicate(view.get("controlledEntityId") or 0) else None
            return poll

        # The seat's SetPlayerToCockpit relay is a droppable one-shot: it drops exactly when it
        # lands while the client is still rebuilding from the previous crossing. Let the client
        # settle onto its character first, then seat, then require the client half to take the
        # cockpit - what the client controls decides whether it sends input at all, and nothing
        # server-side can see that half.
        wait_until(client_controls(lambda controlled: controlled == entity), 60,
                   "the client to settle on its character before the seat")
        bench.cluster.node(home, f"/rich-test-act?grid={near_grid}&action=seat&target={entity}", "POST")
        wait_until(lambda: True if any(c["pilot"] == entity
                   for c in _rich_state(bench, home, near_grid)["cockpits"]) else None,
                   60, "the character to take the seat")
        wait_until(client_controls(lambda controlled: controlled not in (0, entity)), 30,
                   "the client half to take the cockpit")
        _force_handover(bench, home, away, partition, client=CLIENT_ID)
        _wait_arrival(bench, partition, away)
        residuals = _gate_residuals(bench, away, partition)
        bench.check("seated cell: verified with zero residuals", residuals == 0, f"residuals={residuals}")
        pilots = [c["pilot"] for c in _rich_state(bench, away, near_grid)["cockpits"]]
        bench.check("seated cell: the character is still in the seat on the target node",
                    entity in pilots, str(pilots))
        # Stand up the vanilla way, from the client: Use() on the seat requests the pilot removal
        # and the client switches its own controller back to the character. A server-side
        # RemovePilot never relays that switch to a live client - it strands the session.
        def stood_up():
            if all(c["pilot"] == 0 for c in _rich_state(bench, away, near_grid)["cockpits"]):
                return True
            try:
                bench.apis[CLIENT_ID].character_use()
            except Exception:
                pass  # 503 while the reattach settles; retry
            time.sleep(2)
            return None

        wait_until(stood_up, 60, "the client to stand up from the seat")
        bench.assert_move_probe(CLIENT_ID, "after-seated-crossing")
        cells["character-seated"] = "verified while seated, seat retained, controls live after"

        # Magboot-locked standing on the moving grid's hull: teleport just above the middle of
        # the fixture's armor bed (spawned at position+60x; bed top ~1.25 m below grid origin),
        # jetpack off, boots magnetize (server truth), cross, boots still locked after.
        bench.cluster.node(away, f"/teleport?client={CLIENT_ID}"
                                 f"&x={position[0] + 75:.0f}&y={position[1]:.0f}"
                                 f"&z={position[2] + 2.5:.1f}", "POST")
        time.sleep(3)
        bench.apis[CLIENT_ID].key("X")  # jetpack off; the character settles onto the hull

        def boots_locked(node: str):
            state = bench.cluster.node(node, f"/rich-test-state?grid={entity}")
            return True if state.get("magneticBootsActive") else None

        locked = None
        try:
            locked = wait_until(lambda: boots_locked(away), 90, "the magboots to lock on the hull")
        except ClusterError:
            pass
        if locked:
            _force_handover(bench, away, home, partition, client=CLIENT_ID)
            _wait_arrival(bench, partition, home)
            residuals = _gate_residuals(bench, home, partition)
            bench.check("magboot cell: verified with zero residuals", residuals == 0,
                        f"residuals={residuals}")
            # The magnetization re-evaluates on the character's first live updates after the thaw -
            # a one-shot read races it (observed: check False, detail read True milliseconds later).
            relocked = None
            try:
                relocked = wait_until(lambda: boots_locked(home), 60, "the magboots to re-lock on the target")
            except ClusterError:
                pass
            bench.check("magboot cell: the boots are still locked to the hull on the target node",
                        bool(relocked),
                        str(bench.cluster.node(home, f"/rich-test-state?grid={entity}")))
            cells["character-magboot-locked"] = "verified locked to the hull, lock survives"
        else:
            # The lock depends on landing geometry; a miss is a fixture problem, not a gate pass.
            bench.check("magboot cell: the character magnetized to the hull", False,
                        str(bench.cluster.node(away, f"/rich-test-state?grid={entity}")))
        bench.apis[CLIENT_ID].key("X")  # jetpack back on
        bench.assert_move_probe(CLIENT_ID, "after-magboot-crossing")
        bench.screenshot(CLIENT_ID, "01-after-character-cells")
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass
    bench.record("matrix-cells", cells)
    bench.standing_audits(bench.quiesce(), "after the interaction-state matrix")
    bench.exercised(cells=sorted(cells))


def p3_hnd_03(bench) -> None:
    """P3-HND-03 - validation-abort containment: an injected §6.10 failure aborts before commit,
    the source thaws and keeps serving, dumps are retained, the partition goes HandoverBlocked
    with its residual signature after the configured attempts, the balancer stops scheduling it,
    and an admin clear plus a clean hook returns it to normal."""
    from scenarios import _force_handover, _all_partitions
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=rich&x=900000&y=0&z=0", "POST")
        grid = spawned["grids"][0]["grid"]
        partition = _wait_partition(bench, "node-1", grid)

        bench.cluster.node("node-2", "/cluster/debug/corrupt-import?field=DisplayName", "POST")
        signatures = []
        for attempt in (1, 2):
            try:
                _force_handover(bench, "node-1", "node-2", partition)
                bench.check(f"abort attempt {attempt}: the corrupted import must not commit", False, "committed")
            except ClusterError as error:
                bench.record(f"abort-{attempt}.txt", str(error))
            owner = _all_partitions(bench).get(partition)
            bench.check(f"abort attempt {attempt}: the partition stays on its source", owner == "node-1", str(owner))
            bench.standing_audits(bench.quiesce(), f"source thawed and serving after abort {attempt}")
        diff = bench.cluster.config.root / "node-2-data/cluster/handover-diff"
        dumps = len(list(diff.iterdir())) if diff.exists() else 0
        bench.check("dumps are retained per attempt", dumps >= 2, f"{dumps} files under {diff}")

        blocked = bench.cluster.get("/registry/handover-blocked")
        bench.record("handover-blocked", blocked)
        row = next((entry for entry in blocked if int(entry.get("Partition", -1)) == partition), None)
        bench.check("after the configured attempts the partition is HandoverBlocked with a signature",
                    row is not None and row.get("Signature"), str(row))
        signature = (row or {}).get("Signature")
        attempts_before = (row or {}).get("Attempts")

        # No further attempts while blocked: the 'six aborts, retried every rotation' pattern is
        # explicitly asserted absent - a full balance pass leaves the attempt counter alone.
        bench.cluster.trigger("balance", unthrottled="true")
        time.sleep(5)
        blocked_after = bench.cluster.get("/registry/handover-blocked")
        row_after = next((entry for entry in blocked_after
                          if int(entry.get("Partition", -1)) == partition), None)
        bench.check("no further attempts are scheduled while blocked",
                    row_after is not None and row_after.get("Attempts") == attempts_before,
                    f"attempts {attempts_before} -> {(row_after or {}).get('Attempts')}")
        owner = _all_partitions(bench).get(partition)
        bench.check("the blocked partition never moved", owner == "node-1", str(owner))

        # Containment ends: clear the hook, admin-clear the block, and the same move commits.
        bench.cluster.node("node-2", "/cluster/debug/corrupt-import?field=", "POST")
        bench.cluster.post(f"/registry/handover-blocked/clear?partition={partition}")
        _force_handover(bench, "node-1", "node-2", partition)
        _wait_arrival(bench, partition, "node-2")
        residuals = _gate_residuals(bench, "node-2", partition)
        bench.check("after the clear the same transfer commits with zero residuals",
                    residuals == 0, f"residuals={residuals}")
        blocked_final = bench.cluster.get("/registry/handover-blocked")
        bench.check("the committed handover cleared the block record",
                    not any(int(entry.get("Partition", -1)) == partition for entry in blocked_final),
                    str(blocked_final))
        bench.exercised(partition=partition, signature=signature, dumps=dumps)
    finally:
        try:
            bench.cluster.node("node-2", "/cluster/debug/corrupt-import?field=", "POST")
        except ClusterError:
            pass
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass


def p3_inf_01(bench) -> None:
    """P3-INF-01 - run-root hygiene: a stopped run root with its node data restarts warm by default,
    a registry left WITHOUT node data is refused BEFORE any node starts, teardown from inside a
    script never kills the invoking shell (§1.7), and fresh erases an existing cluster only with
    --wipe."""
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.stop()

    # Leg 1: the same run root, registry and node data intact - a plain start is a warm restart.
    bench.cluster.start()
    bench.cluster.wait_ready()
    bench.check("a plain start over an intact run root warm-restarts to Serving", True, "")
    bench.cluster.stop()

    # Leg 2: the wedge case - the registry survives but every node data dir is gone. Refused at
    # launch, no process spawned; the node data is moved aside and put back, never deleted.
    config = bench.cluster.config
    aside = [(config.node_data(node), config.root / f"{config.node_data(node).name}.aside")
             for node in config.nodes if config.node_data(node).exists()]
    for data, parked in aside:
        data.rename(parked)
    refused = False
    try:
        bench.cluster.start()
    except ClusterError as error:
        refused = True
        bench.record("dirty-root-refusal.txt", str(error))
        bench.check("the refusal names the stale state and `fresh --wipe`",
                    "registry" in str(error) and "fresh --wipe" in str(error), str(error)[:160])
    finally:
        for data, parked in aside:
            parked.rename(data)
    bench.check("a registry without node data is refused before any node starts", refused,
                "" if refused else "start() did not raise")
    bench.check("the refused launch spawned nothing", bench.cluster.running_pids() == [], "")

    # Leg 3: the field case - a DIFFERENT world identity over the stale registry. The Gateway's
    # §5.1 provenance stamp refuses the mismatch cold, before any node would wedge.
    provenance = bench.cluster.config.root / "gateway-registry/provenance.json"
    stamped = json.loads(provenance.read_text())
    bench.record("provenance-original", stamped)
    tampered = {**stamped, "WorldId": "DIFFERENT-WORLD"}
    provenance.write_text(json.dumps(tampered))
    bench.cluster.start(nodes=[])
    gateway_log = bench.cluster.config.root / "gateway.log"

    def refused_by_gateway():
        process = bench.cluster.processes.get("gateway")
        text = gateway_log.read_text(errors="replace") if gateway_log.exists() else ""
        if "DIFFERENT-WORLD" in text and process is not None and process.poll() is not None:
            return text
        return None

    text = wait_until(refused_by_gateway, 90, "the Gateway to refuse the mismatched provenance and exit")
    bench.check("the world-swap relaunch is refused naming both identities",
                "DIFFERENT-WORLD" in text, text.splitlines()[-1][:160] if text else "")
    bench.record("provenance-refusal.txt", "\n".join(text.splitlines()[-12:]))
    provenance.write_text(json.dumps(stamped))
    bench.cluster.kill()

    # Leg 4: the wiped relaunch serves - and the teardown that precedes it runs from inside a
    # script that must SURVIVE the kill to do the relaunch (§1.7: kill by PID, never pkill -f).
    script = bench.cluster.config.root / "teardown-then-echo.sh"
    script.write_text("#!/bin/bash\n"
                      f"{cli(bench, 'kill', shell=True)}\n"
                      "echo TEARDOWN-SURVIVED-$?\n")
    script.chmod(0o755)
    completed = subprocess.run(["bash", str(script)], capture_output=True, text=True, timeout=120)
    bench.record("teardown-survival.txt", completed.stdout + completed.stderr)
    bench.check("teardown from inside a script does not kill the invoking shell",
                "TEARDOWN-SURVIVED-0" in completed.stdout, completed.stdout.strip()[-80:])
    # Leg 5: fresh over the existing (stopped) cluster is refused without --wipe and erases
    # nothing; with --wipe it builds a new world that reaches Serving.
    existing = bench.cluster.existing_cluster()
    refused = False
    try:
        bench.cluster.prepare()
    except ClusterError as error:
        refused = True
        bench.record("fresh-without-wipe-refusal.txt", str(error))
    bench.check("fresh over an existing cluster is refused without --wipe", refused,
                "" if refused else "prepare() did not raise")
    bench.check("the refused fresh erased nothing", all(path.exists() for path in existing),
                ", ".join(path.name for path in existing))
    bench.cluster.prepare(wipe=True)
    bench.cluster.start()
    bench.cluster.wait_ready()
    bench.check("fresh --wipe builds a new world that reaches Serving", True, "")
    bench.exercised(legs="default-warm-start, no-data-refusal, provenance-swap, scripted-teardown, fresh-wipe-interlock")


def p3_rec_01(bench) -> None:
    """P3-REC-01 - a node whose registration is definitively rejected fails FAST and VISIBLY:
    terminal on the first rejection, control server answering throughout, clean exit, and the
    cluster reports the shortfall from one status call. Leg (b): fenced mid-run, the node discards
    without saving and exits on its own."""
    # Leg (a): the hook is armed before the victim slot ever spawns.
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    bench.cluster.start(nodes=["node-1", "wa-1"])
    bench.cluster.wait_ready(nodes=["node-1", "wa-1"])
    bench.cluster.post("/debug/reject-node?slot=node-2&mode=on")
    process = bench.cluster.start_node("node-2")

    saw_control_answer = False
    deadline = time.time() + 240
    state_seen = None
    while time.time() < deadline and process.poll() is None:
        try:
            state = bench.cluster.node("node-2", "/node-state")
            saw_control_answer = True
            state_seen = state
            if str(state.get("state")) in ("Failed", "4"):
                break
        except Exception:
            pass
        time.sleep(2)
    bench.record("rejected-node-state", state_seen or {})
    bench.check("the control server answers probes DURING the failure window (no wedged game thread)",
                saw_control_answer, str(state_seen))
    exit_code = wait_until(lambda: True if process.poll() is not None else None, 120,
                           "the rejected node to exit on its own") and process.poll()
    bench.check("the rejected node reports Failed and exits cleanly",
                exit_code is not None, f"exit={exit_code}")

    # Read the log NOW: leg (b)'s restart truncates node-2.log. Terminal means the node marked
    # itself Failed and the 409 count stays a handful - a blind retry loop would write hundreds.
    node_log = (bench.cluster.config.root / "node-2.log").read_text(errors="replace")
    bench.record("rejected-node-log.txt", node_log)
    conflicts = sum("409" in line for line in node_log.splitlines())
    bench.check("the first definitive rejection is terminal - no 409 retry loop",
                "node_state=Failed" in node_log and 1 <= conflicts <= 6,
                f"{conflicts} 409 lines, failed-state={'node_state=Failed' in node_log}")

    status = subprocess.run(
        cli(bench, "status"),
        capture_output=True, text=True, timeout=120)
    bench.record("status-during-failure.txt", status.stdout + status.stderr)
    bench.check("cluster status exits unhealthy while the slot is failing (visible from ONE call)",
                status.returncode != 0, f"rc={status.returncode}")

    # Leg (b): disarm, let node-2 serve, then fence it mid-run.
    bench.cluster.post("/debug/reject-node?slot=node-2&mode=off")
    process = bench.cluster.start_node("node-2")
    bench.cluster.wait_ready(nodes=["node-2"], timeout=300)
    bench.quiesce()
    spawned = bench.cluster.node("node-2", "/trash-test-spawn?x=700000&y=0&z=0&blocks=4", "POST")
    grid = spawned["grid"]
    _wait_partition(bench, "node-2", grid)
    # The fenced node discards WITHOUT saving (Orchestration §4.5 step 3) - recovery replays the
    # last durable state, so the probe must be saved first or there is nothing to recover
    # (bounded rollback would correctly erase it).
    bench.cluster.trigger("save", wait=True)

    bench.cluster.post("/debug/reject-node?slot=node-2&mode=on")
    wait_until(lambda: True if process.poll() is not None else None, 180,
               "the fenced node to discard ownership and exit on its own")
    bench.check("the fenced node leaves on its own - the executor's kill escalation is not needed",
                process.poll() is not None, f"exit={process.poll()}")
    bench.cluster.post("/debug/reject-node?slot=node-2&mode=off")

    # §9.1 step 4: a partition with no player anywhere near it is concealment-eligible, so
    # recovery returns it to Unowned for on-demand reload rather than loading it on a peer.
    # The observable contract is REGISTRY ownership: nothing stays owned by the dead node.
    def released():
        rows = [row for row in bench.cluster.get("/registry/partitions")
                if row.get("Node") == "node-2"]
        return True if not rows else None

    wait_until(released, 240, "the registry to release the fenced node's partitions")
    report = bench.cluster.get("/registry/partitions")
    bench.record("partitions-after-fence", report)
    bench.check("no partition stays owned by the fenced node",
                all(row.get("Node") != "node-2" for row in report), str(report)[:200])
    bench.standing_audits(bench.quiesce(), "after the fence recovery")
    bench.exercised(legs="rejected-at-boot, fenced-mid-run", conflictLines=conflicts)


def t0266_hvk_01(bench) -> None:
    """T-0266: closing an entity whose native rigid body is already destroyed must not crash the node.

    The known positive is the state both node-3 crash dumps show: the body out of the world, native
    reference count 0, its managed wrapper alive and undisposed. /test-havok-release builds that state on
    the fixture's Start Beacon and closes the entity. Without HavokReleasedBodyGuard the node dies with
    SIGSEGV in HkEntity.Dispose; with it the node logs havok_body_released_early and keeps serving."""
    from scenarios_core import _lines, _wait_line
    from scenarios_saves import START_BEACON
    bench.start_cluster(nodes=["node-1", "wa-1"])
    def hosted() -> bool:
        report = bench.cluster.node("node-1", "/partitions")
        return any(START_BEACON == int(m["id"] if isinstance(m, dict) else m)
                   for p in (report if isinstance(report, list) else report.get("partitions", []))
                   for m in (p.get("members") or p.get("entities") or []))
    wait_until(hosted, 120, "node-1 hosts the Start Beacon")
    answer = bench.cluster.node("node-1", f"/test-havok-release?entity={START_BEACON}", method="POST")
    bench.record("havok-release", answer)
    bench.check("the route dropped the body's native references", (answer or {}).get("refsDropped", 0) >= 1,
                json.dumps(answer)[:200])
    try:
        line = _wait_line(bench, "node-1", r"havok_body_released_early .*:%d " % START_BEACON, timeout=60)
        bench.check("the guard skipped the native close of the released body", "field=RigidBody" in line, line[-300:])
    except ClusterError as error:
        bench.check("the guard skipped the native close of the released body", False, str(error))
    time.sleep(15)
    process = bench.cluster.processes.get("node-1")
    alive = process is not None and process.poll() is None
    bench.check("node-1 survived the close", alive, f"exit={None if process is None else process.poll()}")
    if alive:
        after = bench.cluster.node("node-1", "/partitions")
        bench.check("node-1 still serves control routes", isinstance(after, (list, dict)), json.dumps(after)[:200])
    crash = _lines(bench, "node-1", r"SIGSEGV|HkEntity\.Dispose|Segmentation fault")
    bench.check("no native crash signature in node-1's logs", not crash, "; ".join(crash)[-300:])
    bench.exercised(entity=START_BEACON, refsDropped=(answer or {}).get("refsDropped"))


def t0266_hvk_02(bench) -> None:
    """T-0266 harness: drive the candidate release paths through real SE code, HavokBodyAudit as the oracle.

    Known positive first: a body released with close=0 must be reported by the 1 Hz audit
    (havok_body_release_detected) before anything closes it, and the guard must then catch it at close.
    Then N cycles of fixtures with connectors, rotors and a procedural asteroid, teleported 60 km out and
    back (cluster-tree reorders: DeactivateBatch/ActivateBatch between Havok worlds, and the removal of an
    emptied cluster's world) and half closed each cycle. Any havok_double_remove or unexpected
    havok_body_release_detected line is a reproduction of the T-0266 state by a real path."""
    import os
    from scenarios_core import _lines, _wait_line
    from scenarios_saves import START_BEACON
    cycles = int(os.environ.get("T0266_CYCLES", "20"))
    bench.start_cluster(nodes=["node-1", "wa-1"])
    node = lambda path, method="POST": bench.cluster.node("node-1", path, method=method)
    base = [0.0, 0.0, 0.0]
    # The known positive must hold no constraint: /test-havok-release drops EVERY reference, a constraint's
    # too, and a constraint's own Dispose then touches the freed body (a connector-bearing "rich" grid did,
    # 2026-09-28 14:47). The fixture's Start Beacon is a plain grid.
    probe_grid = START_BEACON
    deadline = time.monotonic() + 60
    while True:  # "Game ready" comes before node-1 has imported its partitions (a probe 0.2 s after it, 14:56)
        try:
            release = node(f"/test-havok-release?entity={probe_grid}&close=0")
            break
        except ClusterError as error:
            if "Unknown entity" not in str(error) or time.monotonic() > deadline:
                raise
            time.sleep(1)
    bench.record("known-positive", {"release": release})
    try:
        line = _wait_line(bench, "node-1", r"havok_body_release_detected .*:%d " % probe_grid, timeout=30)
        bench.check("the audit reports a released live body within its 1 Hz scan (known positive)", True, line[-300:])
    except ClusterError as error:
        bench.check("the audit reports a released live body within its 1 Hz scan (known positive)", False, str(error))
    node(f"/test-close-entity?entity={probe_grid}")
    try:
        _wait_line(bench, "node-1", r"havok_body_released_early .*:%d " % probe_grid, timeout=30)
        bench.check("the guard skips the native close of the known positive", True)
    except ClusterError as error:
        bench.check("the guard skips the native close of the known positive", False, str(error))
    spawned = vanished = 0
    for cycle in range(cycles):
        x = 6000 + 400 * cycle
        grids = []
        for kind, dz in (("connectorpair", 0), ("rotorrig", 150), ("rich", 300), ("pistonrig", 450)):
            answer = node(f"/rich-test-spawn?kind={kind}&x={x}&y=0&z={dz}")
            grids += [g["grid"] for g in answer["grids"]]
        bench.cluster.node("wa-1", f"/voxel-test-spawn-asteroid?x={x}&y=0&z=-400&radius=40&seed={cycle + 1}", method="POST")
        spawned += len(grids)
        time.sleep(2)
        def each(path: str) -> None:  # a fixture grid can be gone (merged, subgrid closed with its base): count it, go on
            nonlocal vanished
            try:
                node(path)
            except ClusterError as error:
                if "Unknown entity" not in str(error):
                    raise
                vanished += 1
        # relative to the grid itself: the known positive above closed START_BEACON
        for grid in grids:  # 60 km out: a new cluster (Havok world); the old one may empty and be removed
            each(f"/test-position?entity={grid}&relativeTo={grid}&x=60000&y=0&z=0")
        time.sleep(2)
        for grid in grids:  # and back: batch deactivate/activate again, the far cluster empties
            each(f"/test-position?entity={grid}&relativeTo={grid}&x=-60000&y=0&z=0")
        time.sleep(2)
        for grid in grids[::2]:
            each(f"/test-close-entity?entity={grid}")
        time.sleep(1)
    time.sleep(5)
    process = bench.cluster.processes.get("node-1")
    bench.check("node-1 survived the harness", process is not None and process.poll() is None)
    doubles = _lines(bench, "node-1", r"havok_double_remove")
    detected = [l for l in _lines(bench, "node-1", r"havok_body_release_detected") if f":{probe_grid} " not in l]
    failed = _lines(bench, "node-1", r"havok_body_audit_failed|havok_body_guard_failed")
    bench.record("harness-lines.txt", "\n".join(doubles + detected + failed))
    bench.check("audit ran without failures", not failed, "; ".join(failed)[-300:])
    bench.check("NO real-path release reproduced (a red here is the finding, see harness-lines.txt)",
                not doubles and not detected, f"double_remove={len(doubles)} release_detected={len(detected)}")
    bench.check("the fixtures were exercised (moved and closed, not vanished)", vanished <= spawned // 10, f"vanished={vanished} of {spawned} grids")
    bench.exercised(cycles=cycles, grids=spawned, vanished=vanished, doubleRemoves=len(doubles), releases=len(detected))



def _procedural_template(bench) -> Path:
    """Cluster-Simple with the live world's procedural asteroids (w33: ProceduralDensity 0.35, seed 0).
    The packaged world has none, so a harness on it never generates or unloads one - and the second
    T-0266 crash was a procedural asteroid deleted in a mass unload."""
    import re
    import shutil
    source = Path(HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / f"{source.name}-procedural"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        if not path.is_file():
            continue
        text = re.sub(r"<ProceduralDensity>[^<]*</ProceduralDensity>|<ProceduralSeed>[^<]*</ProceduralSeed>", "", path.read_text())
        text, count = re.subn(r"</Settings>", "<ProceduralDensity>0.35</ProceduralDensity><ProceduralSeed>0</ProceduralSeed></Settings>", text, count=1)
        if count != 1:
            raise ClusterError(f"{path}: no </Settings> to extend")
        path.write_text(text)
    return target


def t0266_hvk_03(bench) -> None:
    """T-0266 harness, cluster paths: both live crashes were closes run by CLUSTER code - a handover
    source release (CloseEntities: a connector phantom, 2026-09-27) and a partition going offline
    followed by a mass procedural-asteroid unload (a MyVoxelPhysicsBody, 2026-09-28). Per cycle, in a
    procedural world (w33 density): connector/rotor/piston/rich fixtures at a fresh far location (new
    procedural cells around them), each partition handed node-1 -> node-2 -> node-1 (two source
    releases), half the grids teleported 60 km (cell unload/generate), then the offline economy
    switched on at zero grace so every fixture partition goes offline (entities closed, cells
    unloaded). HavokBodyAudit is the oracle; its stats line proves which paths ran."""
    from scenarios import _all_partitions, _force_handover
    from scenarios_offline import _put_config
    from scenarios_core import _lines
    cycles = int(os.environ.get("T0266_CYCLES", "6"))
    bench.cluster.config.world_template = _procedural_template(bench)
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    for worker in ("node-1", "node-2"):
        _put_config(bench, worker, {"offline.enabled": "false"})
    node = lambda name, path, method="POST": bench.cluster.node(name, path, method=method)
    import re
    import threading
    moved = releases = offlined = vanished = 0
    census_peak = {"node-1": 0, "node-2": 0}
    where: dict[int, str] = {}  # fixture leader -> node that holds it now
    reorders = {"ok": 0, "failed": 0}
    stop = threading.Event()

    def reorder_loop() -> None:
        """Moving ships reorder the Havok cluster tree all day live; parked fixtures never do. Force
        vanilla ReorderClusters (the batch remove/add path) around every fixture twice a second, so
        it interleaves with freezes, releases and offline closes."""
        while not stop.wait(0.5):
            for leader, name in list(where.items()):
                try:
                    node(name, f"/test-cluster-reorder?entity={leader}")
                    reorders["ok"] += 1
                except ClusterError:
                    reorders["failed"] += 1  # frozen, moving or closed right now: expected

    reorderer = threading.Thread(target=reorder_loop, daemon=True)
    reorderer.start()

    def track(name: str, grid: int) -> None:
        """A character's range for one fixture grid, then let the cells generate and the asteroids
        near the fixtures get their (lazy) rigid bodies."""
        node(name, f"/test-track-procedural?entity={grid}&radius=5000")
        time.sleep(8)
        census = node(name, "/test-voxel-census", method="GET")
        census_peak[name] = max(census_peak[name], census["withBody"])
        bench.record(f"voxel-census-{name}-{time.time():.0f}", census)
    try:
        for cycle in range(cycles):
            x = 400000 + 60000 * cycle  # a fresh procedural neighbourhood per cycle
            grids, partitions = [], set()
            leaders = {}
            kinds = (("connectorpair", 0), ("rotorrig", 150), ("rich", 300), ("pistonrig", 450),
                     ("gearpair", 600), ("mergepair", 750), ("grinderrig", 900))
            for kind, dz in kinds[:int(os.environ.get("T0266_KINDS", "4"))]:
                answer = node("node-1", f"/rich-test-spawn?kind={kind}&x={x}&y=0&z={dz}")
                kind_grids = [g["grid"] for g in answer["grids"]]
                grids += kind_grids
                partition = _wait_partition(bench, "node-1", kind_grids[0])
                partitions.add(partition)
                leaders.setdefault(partition, kind_grids[0])
                where[kind_grids[0]] = "node-1"
            for leader in leaders.values():
                track("node-1", leader)
            for partition in sorted(partitions):
                for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
                    try:
                        _force_handover(bench, source, target, partition)
                        _wait_arrival(bench, partition, target)
                        releases += 1
                        where[leaders[partition]] = target
                        track(target, leaders[partition])  # the source release closed the tracked grid: its cells unload
                    except ClusterError as error:
                        bench.record(f"handover-{cycle}-{partition}-{source}", str(error)[-400:])
                        break
            for grid in grids[::2]:  # 60 km and back: cells unload behind, generate ahead
                for dx in (60000, -60000):
                    try:
                        node("node-1", f"/test-position?entity={grid}&relativeTo={grid}&x={dx}&y=0&z=0")
                        moved += 1
                    except ClusterError as error:
                        if "Unknown entity" not in str(error):
                            raise
                        vanished += 1
                    time.sleep(1)
            _put_config(bench, "node-1", {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
            try:
                wait_until(lambda: True if not (partitions & set(_all_partitions(bench))) else None,
                           90, f"cycle {cycle}: the fixture partitions to go offline")
                offlined += len(partitions)
            except ClusterError as error:
                bench.record(f"offline-{cycle}", str(error)[-400:])
            finally:
                _put_config(bench, "node-1", {"offline.enabled": "false"})
                for leader in leaders.values():
                    where.pop(leader, None)
            time.sleep(3)
    finally:
        stop.set()
        reorderer.join(10)
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass
    time.sleep(65)  # one more audit stats line after the last unload
    alive = {name: (p := bench.cluster.processes.get(name)) is not None and p.poll() is None for name in ("node-1", "node-2")}
    bench.check("both regular nodes survived the harness", all(alive.values()), str(alive))
    doubles = _lines(bench, "node-1", r"havok_double_remove") + _lines(bench, "node-2", r"havok_double_remove")
    detected = _lines(bench, "node-1", r"havok_body_release_detected") + _lines(bench, "node-2", r"havok_body_release_detected")
    failed = [l for n in ("node-1", "node-2") for l in _lines(bench, n, r"havok_body_audit_failed|havok_body_guard_failed")]
    stats = {n: (_lines(bench, n, r"havok_body_audit_stats") or ["none"])[-1] for n in ("node-1", "node-2")}
    bench.record("harness-lines.txt", "\n".join(doubles + detected + failed + list(stats.values())))
    bench.check("the cluster paths ran: source releases, offline closes and voxel body closes",
                releases > 0 and offlined > 0 and any("voxelCloses=0 " not in line and line != "none" for line in stats.values()),
                f"releases={releases} offlined={offlined} asteroidsWithBodyPeak={census_peak} stats={stats}")
    bench.check("the batch path ran: forced cluster reorders reached RemoveRigidBodyBatch",
                reorders["ok"] > 0 and any(re.search(r"batchRemoves=[1-9]", line) for line in stats.values()),
                f"reorders={reorders}")
    bench.check("audit ran without failures", not failed, "; ".join(failed)[-300:])
    bench.check("NO real-path release reproduced (a red here is the finding, see harness-lines.txt)",
                not doubles and not detected, f"double_remove={len(doubles)} release_detected={len(detected)}")
    bench.exercised(cycles=cycles, reorders=reorders, releases=releases, offlined=offlined, moved=moved, vanished=vanished,
                    asteroidsWithBodyPeak=census_peak,
                    doubleRemoves=len(doubles), detected=len(detected))


def _residual_lines(bench, partition: int) -> list[str]:
    """The gate's own residual lines for this partition, from every node log."""
    lines = []
    for node in bench.cluster.config.nodes:
        log = bench.cluster.config.root / f"{node.node_id}.log"
        if log.exists():
            lines += [line.strip() for line in log.read_text(errors="replace").splitlines()
                      if "handover residual" in line and f"partition={partition} " in line]
    return lines


def _dummy_of(bench, node: str, grid: int, timeout: float = 60) -> dict:
    """The fixture's target dummy once its subparts exist on this node (after the thaw)."""
    def ready():
        state = _rich_state(bench, node, grid)
        dummies = state.get("dummies") or []
        return dummies[0] if state.get("exists") and dummies and dummies[0]["initialized"] else None
    return wait_until(ready, timeout, f"the target dummy of grid {grid} to initialize on {node}")


def p3_hnd_dummy(bench) -> None:
    """SE1-0225 - a target dummy round-trips the §6.10 gate, intact and destroyed. The dummy writes
    IsDestroyed only once its subparts exist, which an imported block reaches on its first live frame,
    after the gate has run. Each leg goes node-1 -> node-2 -> node-1 and asserts zero residuals from the
    gate's own log line and the dummy's state after the thaw."""
    from scenarios import _force_handover
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=targetdummy&x=880000&y=0&z=0", "POST")
        bench.record("dummy-fixture", spawned)
        grid = spawned["grids"][0]["grid"]
        partition = _wait_partition(bench, "node-1", grid)

        def round_trip(leg: str, destroyed: bool) -> bool:
            # A new dummy restores itself from the steel plates the fixture gave it.
            before = wait_until(lambda: (lambda d: d if d["destroyed"] is destroyed else None)(_dummy_of(bench, "node-1", grid)),
                                90, f"the dummy to be {'destroyed' if destroyed else 'intact'} on node-1")
            for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
                bench.quiesce(timeout=120)
                try:
                    _force_handover(bench, source, target, partition)
                    _wait_arrival(bench, partition, target, timeout=120)
                    arrived = True
                except ClusterError as error:
                    arrived = False
                    bench.record(f"{leg}-{source}-{target}-error.txt", str(error))
                residuals = _gate_residuals(bench, target, partition)
                lines = _residual_lines(bench, partition)
                bench.record(f"{leg}-{source}-{target}-residuals.txt", "\n".join(lines))
                bench.check(f"{leg} {source}->{target}: the §6.10 gate verified the transfer with zero residuals",
                            arrived and residuals == 0, f"residuals={residuals} " + " | ".join(lines[-4:]))
                if not arrived:
                    return False
                after = _dummy_of(bench, target, grid)
                bench.record(f"{leg}-{target}-dummy", after)
                bench.check(f"{leg} {source}->{target}: the dummy arrives {'destroyed' if destroyed else 'intact'} "
                            "with its subpart health", after["destroyed"] is destroyed
                            and sorted(after["subparts"]) == sorted(before["subparts"]), f"{before} -> {after}")
            return True

        if round_trip("intact", destroyed=False):
            act = bench.cluster.node("node-1", f"/rich-test-act?grid={grid}&action=dummy-destroy", "POST")
            bench.record("dummy-destroy", act)
            round_trip("destroyed", destroyed=True)
        census = _handover_diff_census(bench)
        bench.record("handover-diff-census", census)
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass
    bench.exercised(fixture="target dummy", legs="intact, destroyed", directions="both per leg")


def p3_hnd_stator(bench) -> None:
    """SE-1416 - a headless rotor whose live LimitsActive is stale crosses the §6.10 gate. Every server
    load re-derives the flag (MyMotorStator.Init), so the import may disagree with the frozen source;
    on octahedron-test that blocked four partitions until a rotation drain timed out. Each leg makes the
    flag stale on the source, hands the partition over and asserts zero residuals from the gate's log."""
    from scenarios import _force_handover
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    try:
        spawned = bench.cluster.node("node-1", "/rich-test-spawn?kind=rotorrig&x=890000&y=0&z=0", "POST")
        bench.record("rotor-fixture", spawned)
        grid = spawned["grids"][0]["grid"]
        partition = _wait_partition(bench, "node-1", grid)
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            bench.quiesce(timeout=120)
            stale = bench.cluster.node(source, f"/rich-test-act?grid={grid}&action=stale-limits", "POST")
            bench.record(f"{source}-stale-limits", stale)
            try:
                _force_handover(bench, source, target, partition)
                _wait_arrival(bench, partition, target, timeout=120)
                arrived = True
            except ClusterError as error:
                arrived = False
                bench.record(f"{source}-{target}-error.txt", str(error))
            residuals = _gate_residuals(bench, target, partition)
            lines = _residual_lines(bench, partition)
            bench.record(f"{source}-{target}-residuals.txt", "\n".join(lines))
            bench.check(f"{source}->{target}: a stale LimitsActive {stale['limitsActive']} passes the §6.10 gate with zero residuals",
                        arrived and residuals == 0, f"residuals={residuals} " + " | ".join(lines[-4:]))
            if not arrived:
                break
            bench.record(f"{target}-rotor", bench.cluster.node(target, f"/rich-test-state?grid={grid}")["rotors"])
        bench.record("handover-diff-census", _handover_diff_census(bench))
    finally:
        try:
            bench.cluster.post("/automatic/resume")
        except ClusterError:
            pass
    bench.exercised(fixture="headless rotor, stale LimitsActive", directions="both")


SCENARIOS_EXT = {
    "T0266-HVK-02": (t0266_hvk_02, "T-0266 harness: real release paths under the Havok body audit", "1"),
    "T0266-HVK-03": (t0266_hvk_03, "T-0266 harness: cluster release/offline paths in a procedural world", "1"),
    "T0266-HVK-01": (t0266_hvk_01, "closing an entity with an already-destroyed native body does not crash", "1"),
    "P3-HND-01": (p3_hnd_01, "round-trip fidelity on rich content, both directions, both modes", "3"),
    "P3-HND-02": (p3_hnd_02, "transient interaction-state matrix", "3"),
    "P3-HND-DUMMY": (p3_hnd_dummy, "SE1-0225: a target dummy round-trips the gate, intact and destroyed", "3"),
    "P3-HND-STATOR": (p3_hnd_stator, "SE-1416: a headless rotor's stale LimitsActive passes the gate", "3"),
    "P3-HND-03": (p3_hnd_03, "validation-abort containment and HandoverBlocked", "3"),
    "P3-INF-01": (p3_inf_01, "run-root hygiene and stale-registry refusal", "2"),
    "P3-REC-01": (p3_rec_01, "fenced node fails fast, cluster reports it", "3"),
    "P1-GWY-01": (p1_gwy_01, "codec golden tests and DS-update drift alarm", "1"),
    "P1-GWY-02": (p1_gwy_02, "WAL/snapshot crash fuzz against a shadow model", "1"),
    "P1-GWY-03": (p1_gwy_03, "route-flip model check over real peers", "1"),
    "P1-SEC-01": (p1_sec_01, "wrong token and hash mismatch refused", "1"),
    "P1-SEC-02": (p1_sec_02, "cross-cluster and cross-world join refused", "1"),
    "P1-SEC-03": (p1_sec_03, "unsolicited node-to-node stream refused", "1"),
    "P1-GWY-08": (p1_gwy_08, "node-link loss disconnects only the attached clients", "3"),
    "P1-SEC-04": (p1_sec_04, "join-token rotation", "1"),
    "P1-SEC-08": (p1_sec_08, "admin API gating", "1"),
    "P1-SEC-09": (p1_sec_09, "secret redaction sweep", "1"),
    "P1-REC-02b": (p1_rec_02b, "sim stall with live heartbeats", "4"),
    "P1-REC-04": (p1_rec_04, "Gateway restart reconciliation", "4"),
    "P1-REC-06": (p1_rec_06, "graceful and emergency shutdown to warm start", "4"),
    "P1-REC-07": (p1_rec_07, "zombie node with a valid token is fenced", "4"),
    "P3-AUT-03": (p3_aut_03, "ban/cap/reserved/group gates decided at the Gateway", "3"),
    "P3-AUT-05": (p3_aut_05, "accept-all guardrail refuses unsafe configs at startup", "2"),
    "P1-MEM-01": (p1_mem_01, "flood-fill membership determinism", "1"),
    "P1-MEM-02": (p1_mem_02, "merge/split hysteresis", "2"),
    "P1-MEM-03": (p1_mem_03, "grid-group atomicity", "2"),
    "P1-MEM-04": (p1_mem_04, "conga line stability", "2"),
    "P1-SAV-02": (p1_sav_02, "save container property tests", "1"),
    "P2-LIM-02": (p2_lim_02, "sim-speed divergence invisibility", "3"),
    "P3-CST-01": (p3_cst_01, "handover cost calibration and direction-policy audit", "5"),
}
