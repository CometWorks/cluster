"""Persistence and recovery: the former run-cluster.sh lanes `drain`, `rotation` and
`rotation-force`, as native scenarios.

All three close node-1 while a client is attached to it and node-2 is ready:

* ``P1-DRAIN-01`` - the registry closes node-1 with no grace: it writes Final saves, releases its
  partitions unowned, and node-2 recovers every Final entity exactly once. A save from the
  drained node's old epoch is then refused (P1-SEC-05).
* ``P1-REC-05a`` - graceful rotation: the occupied partition is handed to node-2 live, without a
  reconnect, and node-1 reaches Empty and exits by itself.
* ``P1-REC-05b`` - forced rotation: graceful transfer is suppressed until a 2 s deadline, node-2
  recovers the client's partition from the Final save, and the client reconnects to it.

And the former `recovery`, `save-rollback` and `save-fidelity` lanes, which SIGKILL node-1 after a
catalogued save, expire its leases and let node-2 recover the client's partition:

* ``P1-REC-01``  - bounded rollback exposure, exactly-once lease expiry, stale-epoch save refused
  (P1-REC-12), the dead node's PCU report expires, and a store payment held open across the kill
  is reconciled exactly once when node-1 restarts on its retained data root;
* ``P1-SAV-05``  - a terminal name catalogued before the save survives, a later uncommitted rename
  is rolled back (P1-LIM-02);
* ``P1-SAV-01``  - the full fidelity fixture: every node-1 partition recovers semantically equal to
  its catalogued save.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import threading
import random
import shutil
import signal
import time
import urllib.error
import urllib.request

from bench import HOST
from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _grid_ops, _join, _lines, _log_files, \
    _nodes, _owners, _partitions, _wait_line

RECOVERY_TIMEOUT = 90
SAVE_LINE = r"partition save registered: partition ([0-9]+) generation ([0-9]+) sequence ([0-9]+)"


def _world_tool(*args: str) -> dict:
    result = subprocess.run(["dotnet", str(HOST.package / "tools/MagnetarWorld/MagnetarWorld.dll"), *args],
                            capture_output=True, text=True, timeout=300)
    if result.returncode != 0:
        raise ClusterError(f"MagnetarWorld {args[0]} failed: {(result.stdout + result.stderr)[-400:]}")
    return json.loads(result.stdout)


def _registry_nodes(bench) -> list[dict]:
    return bench.cluster.get("/registry/nodes")


def _epoch(bench, node_id: str) -> int:
    return next(row["Epoch"] for row in _registry_nodes(bench) if row["Node"] == node_id)


def _wait_exit(bench, node_id: str, timeout: float = RECOVERY_TIMEOUT) -> float:
    """The node process exits by itself; returns when (time.time())."""
    process = bench.cluster.processes.get(node_id)
    if process is None:
        raise ClusterError(f"{node_id} was not started by this bench")
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        raise ClusterError(f"{node_id} did not terminate itself within {timeout}s") from None
    return time.time()


def _status(method: str, url: str) -> tuple[int, str]:
    request = urllib.request.Request(url, method=method, data=b"" if method == "POST" else None)
    try:
        with urllib.request.urlopen(request, timeout=40) as response:
            return response.status, response.read().decode(errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")


def _setup_rotation(bench) -> tuple[int, int]:
    _join(bench)
    partition = _client_partition(_partitions(bench, "node-1"))
    if partition is None:
        raise ClusterError("node-1 did not report the connected client's partition")
    generation = next(row["Generation"] for row in _owners(bench)
                      if row["Partition"] == partition and row["Node"] == "node-1")
    bench.check("node-2 is active before the rotation",
                any(row["Node"] == "node-2" and row.get("State") in (0, "Active") for row in _registry_nodes(bench)),
                json.dumps(_registry_nodes(bench))[:300])
    return partition, generation


def _reconnect_recovered_client(bench, partition: int) -> None:
    node2 = _nodes(bench)["node-2"]
    _wait_line(bench, "gateway", rf"Client {CLIENT_ID} disconnected", RECOVERY_TIMEOUT)
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
    character = bench.wait_spawned(CLIENT_ID)
    bench.check("the reconnected client has a living character", bool(character))


def p1_drain_01(bench) -> None:
    _join(bench)
    before = len(_lines(bench, "node-1", SAVE_LINE))
    wait_until(lambda: len(_lines(bench, "node-1", SAVE_LINE)) > before, RECOVERY_TIMEOUT,
               "a durable node-1 partition save after the join")
    partition, generation, sequence = map(int, re.search(SAVE_LINE, _lines(bench, "node-1", SAVE_LINE)[-1]).groups())
    owner = next((row for row in _owners(bench) if row["Partition"] == partition and row["Node"] == "node-1"), None)
    if owner is None:
        raise ClusterError(f"the durably saved partition {partition} is not owned by node-1")
    bench.record("drain-owner-before", owner)
    epoch = _epoch(bench, "node-1")
    bench.record("drain-close", bench.cluster.post(f"/registry/nodes/close?node=node-1&epoch={epoch}&forceAfterSeconds=0"))
    for label, pattern in (
            ("node-1 wrote a Final save", rf"partition save registered: partition {partition} generation {generation} sequence [0-9]+ kind Final"),
            ("node-1 unloaded the partition after the Final", rf"drain final registered and partition unloaded: {partition}\b"),
            ("node-1 released the partition unowned at the next generation",
             rf"drain released partition {partition} generation {generation + 1} unowned"),
            ("node-1 reached Empty", r"drain finals registered; node Empty")):
        _wait_line(bench, "node-1", pattern, RECOVERY_TIMEOUT)
        bench.check(label, True)
    new_generation = generation + 1
    _wait_line(bench, "node-2", rf"recovery loaded partition {partition} generation {new_generation} with ", RECOVERY_TIMEOUT)
    # The promise is containment: every entity of the drain Final is live exactly once on node-2
    # (a recovery cover filters per claimed partition, and topology keeps moving members).
    # The route ignores a query and answers with the whole catalogue (T-0204), so select on the
    # full SaveId: generation and sequence are small integers that every partition also has.
    saves = bench.cluster.get("/registry/saves")
    final = next((row for row in saves if row["SaveId"]["Partition"] == partition
                  and row["SaveId"]["Generation"] == generation
                  and row["SaveId"]["Sequence"] == sequence + 1 and row.get("Kind") in (2, "Final")), None)
    if final is None:
        raise ClusterError(f"drain Final {partition}/{generation}/{sequence + 1} is not catalogued: {json.dumps(saves)[:300]}")
    final_ids = _world_tool("verify-mps", final["Locations"][0])["entityIds"]
    _wait_line(bench, "node-2", rf"partition save registered: partition {partition} generation {new_generation} sequence 1 ",
               RECOVERY_TIMEOUT)
    owners_after = _owners(bench)
    bench.record("drain-partitions-after", owners_after)
    bench.check("node-2 owns the partition at the single advanced generation",
                any(row["Partition"] == partition and row["Node"] == "node-2" and row["Generation"] == new_generation
                    for row in owners_after))
    census = []

    def contained():
        nonlocal census
        census = [member["id"] for p in _partitions(bench, "node-2") for member in p.get("members") or []]
        return all(census.count(entity) == 1 for entity in final_ids)

    try:
        wait_until(contained, HANDOVER_TIMEOUT, "every drain Final entity live exactly once on node-2")
        live = True
    except ClusterError:
        live = False
    bench.check("every drain Final entity is live exactly once on node-2", live,
                f"expected {final_ids}, census {census}"[:400])
    _wait_exit(bench, "node-1")
    bench.check("node-1 terminated itself", True)
    gateway = bench.cluster.config.gateway_control
    status, body = _status("POST", f"http://{gateway}/registry/saves?partition={partition}&node=node-1&epoch={epoch}"
                                   f"&generation={generation}&sequence={sequence + 2}&kind=Periodic&size=0"
                                   f"&checksum=stale&clusterTime=0&location=stale")
    bench.record("drain-stale-save.txt", f"{status}\n{body}")
    bench.check("a save from the drained epoch is refused with 409 (P1-SEC-05)", status == 409, f"HTTP {status}")
    bench.exercised(partition=partition, final=f"g{generation}/s{sequence + 1}", recovered=len(final_ids),
                    generation=[generation, new_generation])


def p1_rec_05a(bench) -> None:
    partition, generation = _setup_rotation(bench)
    started = time.time()
    bench.record("rotation-close", bench.cluster.post("/close-node?forceAfterSeconds=60"))
    for label, name, pattern in (
            ("the Gateway drains the occupied partition to node-2", "gateway",
             rf"Draining occupied partition {partition} from node-1 to node-2"),
            ("the rotation handover committed", "gateway", rf"partition={partition} state=committed"),
            ("the rotation handover released", "gateway", rf"partition={partition} state=released"),
            ("node-2 adopted the client view", "node-2", rf"client-view adopt client={CLIENT_ID}")):
        _wait_line(bench, name, pattern)
        bench.check(label, True)
    _wait_line(bench, "node-1", r"drain finals registered; node Empty", RECOVERY_TIMEOUT)
    exited = _wait_exit(bench, "node-1")
    bench.check("the occupied partition moved live, not through recovery",
                not _lines(bench, "node-2", rf"recovery loaded partition {partition}\b"))
    bench.check("the client never disconnected", not _lines(bench, "gateway", rf"Client {CLIENT_ID} disconnected"))
    bench.check("the client's character survived", bench.living_character(CLIENT_ID) is not None)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(partition=partition, generation=generation, node1ExitSeconds=round(exited - started, 1))


def p1_rec_05b(bench) -> None:
    partition, generation = _setup_rotation(bench)
    started = time.time()
    bench.record("rotation-force-close", bench.cluster.post("/close-node?forceAfterSeconds=2&forceOnly=true"))
    _wait_line(bench, "node-1", r"force-drain deadline reached", RECOVERY_TIMEOUT)
    bench.check("the force deadline did not fire before 2 s", time.time() - started >= 2)
    for label, pattern in (
            ("node-1 wrote a forced Final save",
             rf"partition save registered: partition {partition} generation {generation} sequence [0-9]+ kind Final"),
            ("node-1 released the partition unowned",
             rf"drain released partition {partition} generation {generation + 1} unowned"),
            ("node-1 reached Empty", r"drain finals registered; node Empty")):
        _wait_line(bench, "node-1", pattern, RECOVERY_TIMEOUT)
        bench.check(label, True)
    exited = _wait_exit(bench, "node-1")
    new_generation = generation + 1
    # The honest content reference is the Final save the recovery actually read, not a census.
    loaded_line = _wait_line(bench, "node-2", rf"recovery loaded partition {partition} generation {new_generation} with ([0-9]+) members",
                             RECOVERY_TIMEOUT)
    loaded = int(re.search(r"with ([0-9]+) members", loaded_line).group(1))
    reads = _lines(bench, "node-2", r"recovery read save " + str(partition) + r"/([0-9]+)/([0-9]+)")
    if not reads:
        raise ClusterError("node-2 recovered with no recovery-read-save record")
    read_generation, read_sequence = map(int, re.search(rf"recovery read save {partition}/([0-9]+)/([0-9]+)", reads[-1]).groups())
    # Select by the full SaveId: a partition's lineage may carry another partition's save.
    saves = bench.cluster.get("/registry/saves")
    final = next((row for row in saves if row["SaveId"]["Partition"] == partition
                  and row["SaveId"]["Generation"] == read_generation and row["SaveId"]["Sequence"] == read_sequence), None)
    if final is None:
        raise ClusterError(f"the recovery read save {partition}/{read_generation}/{read_sequence} is not catalogued")
    expected = _world_tool("verify-mps", final["Locations"][0])["entities"]
    bench.check("the recovery loaded exactly the Final it read", loaded == expected, f"{loaded} of {expected}")
    _wait_line(bench, "node-2", rf"partition save registered: partition {partition} generation {new_generation} sequence 1 ",
               RECOVERY_TIMEOUT)
    _reconnect_recovered_client(bench, partition)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(partition=partition, generation=[generation, new_generation], loaded=loaded,
                    node1ExitSeconds=round(exited - started, 1))



def _kit(bench):
    api = bench.apis[CLIENT_ID]
    return wait_until(lambda: next(iter(api.find_nearby_blocks(block_type="SurvivalKit", max_distance=100, limit=1)), None),
                      HANDOVER_TIMEOUT, "the survival kit near the client")


def _rename(bench, kit: dict, name: str) -> None:
    api = bench.apis[CLIENT_ID]
    _grid_ops(api, {"sets": [{"type": "custom_name", "gridId": kit["gridId"], "min": kit["min"], "value": name}]})
    wait_until(lambda: _custom_name(bench, kit) == name, HANDOVER_TIMEOUT, f"the kit to read back as {name}")


def _custom_name(bench, kit: dict) -> str | None:
    result = _grid_ops(bench.apis[CLIENT_ID], {"gets": [{"type": "block", "gridId": kit["gridId"], "min": kit["min"]}]})
    return (result["gets"][0].get("data") or {}).get("customName")


def _wait_new_save(bench, partition: int) -> float:
    pattern = rf"partition save registered: partition {partition} generation [0-9]+ sequence [0-9]+"
    before = len(_lines(bench, "node-1", pattern))
    started = time.time()
    wait_until(lambda: len(_lines(bench, "node-1", pattern)) > before, RECOVERY_TIMEOUT,
               f"a new node-1 save of partition {partition}")
    return time.time() - started


def _fidelity_world(bench):
    """The fidelity fixture (PB, mechanical, connector, inventory, character, floating object, station)."""
    world = bench.run_root.parent / (bench.run_root.name + "-fidelity-world")
    shutil.rmtree(world, ignore_errors=True)
    template = bench.cluster.config.world_template
    subprocess.run(["dotnet", str(HOST.package / "tools/MagnetarWorld/MagnetarWorld.dll"), "to-fidelity",
                    str(template), str(world)], check=True, capture_output=True, text=True, timeout=300)
    bench.record("save-fidelity-fixture", _world_tool("verify-fidelity", str(world / "SANDBOX_0_0_0_.sbs")))
    return world


def _crash_and_recover(bench, variant: str) -> None:
    """The shared body of P1-REC-01, P1-SAV-05 and P1-SAV-01.

    Not ported yet: the shell lane's fidelity variant also loaded the fixture on a stock server as a
    baseline and diffed it, without asserting on the result. It returns with the stock-server helper."""
    if variant == "fidelity":
        bench.cluster.config.world_template = _fidelity_world(bench)
    _join(bench)
    gateway, wa = bench.cluster.config.gateway_control, _nodes(bench)["wa-1"].control
    partition = _client_partition(_partitions(bench, "node-1"))
    if partition is None:
        raise ClusterError("node-1 did not report the connected client's partition")
    kit = None
    if variant == "rollback":
        kit = _kit(bench)
        _rename(bench, kit, "SAV05-catalogued")
    # Two consecutive saves of the player partition, each within 30 s (plus scheduling tolerance).
    for label in ("first", "second"):
        took = _wait_new_save(bench, partition)
        bench.check(f"the {label} player-partition save came within 35 s", took <= 35, f"{took:.1f} s")
        if label == "first":
            previous_time = bench.cluster.get(f"/registry/saves/newest?partition={partition}")["ClusterTime"]
    newest = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    save_time = newest["ClusterTime"]
    bench.check("the catalogued save gap is at most 30 s", save_time - previous_time <= 30000,
                f"{save_time - previous_time} ms")
    line = _lines(bench, "node-1", rf"partition save registered: partition {partition} generation [0-9]+ sequence [0-9]+")[-1]
    generation, sequence = map(int, re.search(r"generation ([0-9]+) sequence ([0-9]+)", line).groups())
    source_count = next(p["memberCount"] for p in _partitions(bench, "node-1") if p["id"] == partition)
    fidelity = []
    if variant == "fidelity":
        for row in _owners(bench):
            if row["Node"] != "node-1":
                continue
            location = bench.cluster.get(f"/registry/saves/newest?partition={row['Partition']}")["Locations"][0]
            members = _world_tool("verify-mps", location)["entityIds"]
            before = bench.dir / f"save-fidelity-before-{row['Partition']}.xml"
            subprocess.run(["dotnet", str(HOST.package / "tools/MagnetarWorld/MagnetarWorld.dll"), "mps-to-sector",
                            location, str(before)], check=True, capture_output=True, timeout=300)
            fidelity.append((row["Partition"], row["Generation"], members, before))
        bench.record("save-fidelity-partitions", [[p, g, m] for p, g, m, _ in fidelity])
    if variant == "rollback":
        _rename(bench, kit, "SAV05-uncommitted")
    epoch_file = bench.cluster.config.node_data(_nodes(bench)["node-1"]) / "cluster/node-epoch"
    wait_until(lambda: epoch_file.is_file() and epoch_file.read_text().strip(), RECOVERY_TIMEOUT, "node-1's persisted epoch")
    epoch = int(epoch_file.read_text().strip())

    payer = hold = None
    if variant == "recovery":
        # WA-2: a store payment reservation held open on node-1's game thread when the SIGKILL lands.
        # The WA debit is real; only node-1's durable payment journal can refund it, on restart.
        payer = 944000000000000000 + random.randrange(10 ** 6)
        bench.cluster.node("node-2", f"/banking-test-create?ownerId={payer}&balance=500", method="POST")
        wait_until(lambda: (lambda st: st.get("exists") and st.get("balance") == 500)(
            bench.cluster.node("wa-1", f"/banking-test-status?ownerId={payer}")), HANDOVER_TIMEOUT,
            "the payer account at the WA")
        # Re-catalogue right before the hold: node-1 cannot save again until the kill.
        before_sequence = newest["SaveId"]["Sequence"]
        bench.cluster.node("node-1", f"/save-capture?partition={partition}", method="POST")
        newest = wait_until(lambda: (lambda n: n if n["SaveId"]["Sequence"] > before_sequence else None)(
            bench.cluster.get(f"/registry/saves/newest?partition={partition}")), HANDOVER_TIMEOUT, "the pre-hold save")
        save_time = newest["ClusterTime"]
        line = _lines(bench, "node-1", rf"partition save registered: partition {partition} generation [0-9]+ sequence [0-9]+")[-1]
        generation, sequence = map(int, re.search(r"generation ([0-9]+) sequence ([0-9]+)", line).groups())

    state = bench.cluster.node("node-1", "/partitions")
    bench.check("the client is attached to its partition right before the kill",
                any(p["id"] == partition and CLIENT_ID in (p.get("clients") or []) for p in state["partitions"]))
    crash_time = state["evaluatedAt"]
    metrics = next(row for row in bench.cluster.node("node-1", "/save-status")["partitions"] if row["partition"] == partition)
    if variant == "recovery":
        # Fire the held payment only after every node-1 probe: the hold pins node-1's game thread.
        url = (f"http://{_nodes(bench)['node-1'].control}/cluster/banking-test-store-payment?payerId={payer}"
               f"&payeeId={payer + 1}&amount=100&holdMs=30000")
        hold = threading.Thread(target=_status, args=("POST", url), daemon=True)
        hold.start()
        wait_until(lambda: bench.cluster.node("wa-1", f"/banking-test-status?ownerId={payer}").get("balance") == 400,
                   20, "the held reservation to debit the payer at the WA")
    bench.cluster.kill(slot="node-1")
    exposure = crash_time - save_time
    rollback = {"partition": partition, "generation": generation, "sequence": sequence, "saveClusterTime": save_time,
                "crashClusterTime": crash_time, "rollbackExposureMs": exposure,
                "effectiveSaveIntervalMs": metrics["effectiveIntervalMs"], "reportedSaveLagMs": metrics["saveLagMs"]}
    bench.record("rollback", rollback)
    bench.check("the rollback exposure is within the effective save interval",
                0 <= exposure <= metrics["effectiveIntervalMs"], json.dumps(rollback))
    bench.check("the reported save lag matches the observed exposure within 2 s",
                abs(exposure - metrics["saveLagMs"]) <= 2000, f"{exposure} vs {metrics['saveLagMs']} ms")
    if variant == "recovery":
        # A pull demand against the dead owner is refused cleanly (no hang, no partial handover).
        try:
            demand = bench.cluster.post(f"/registry/pull-demands?partition={partition}&source=node-1&target=node-2"
                                        f"&grid=0&client={CLIENT_ID}", timeout=30)
        except ClusterError as error:
            demand = {"error": str(error)[:300]}
        bench.record("recovery-dead-owner-demand", demand)
        bench.check("a pull demand against the dead owner is not accepted",
                    not (isinstance(demand, dict) and demand.get("accepted") is True), json.dumps(demand)[:200])

    expired = 0

    def moved():
        nonlocal expired
        expired += bench.cluster.post("/registry/leases/expire")["expired"]
        row = next((r for r in _owners(bench) if r["Partition"] == partition), None)
        return row if row and row.get("Node") != "node-1" and row["Generation"] > generation else None

    owner = wait_until(moved, RECOVERY_TIMEOUT, "node-1's partition lease to expire")
    second = bench.cluster.post("/registry/leases/expire")["expired"]
    bench.check("ownership left node-1 at a later generation; a second expiry finds nothing",
                second == 0, f"owner {owner}, expired {expired}, second {second}")
    new_generation = generation + 1
    _wait_line(bench, "node-2", rf"recovery loaded partition {partition} generation {new_generation} with {source_count} members",
               RECOVERY_TIMEOUT)
    bench.check("node-2 recovered the partition with every member", True, f"{source_count} members")
    _wait_line(bench, "node-2", rf"partition save registered: partition {partition} generation {new_generation} sequence 1 ",
               RECOVERY_TIMEOUT)
    if variant == "fidelity":
        for other, other_generation, _, _ in fidelity:
            _wait_line(bench, "node-2", rf"recovery loaded partition {other} generation {other_generation + 1} ", RECOVERY_TIMEOUT)
        after = bench.dir / "save-fidelity-after.xml"
        after.write_text(_raw(bench, "node-2", "/export"))
        bench.record("save-fidelity-after", _world_tool("verify-fidelity", str(after)))
        for other, _, members, before in fidelity:
            result = subprocess.run(["dotnet", str(HOST.package / "tools/MagnetarWorld/MagnetarWorld.dll"), "semantic-diff",
                                     str(before), str(after), ",".join(str(m) for m in members)],
                                    capture_output=True, text=True, timeout=300)
            bench.record(f"save-fidelity-semantic-{other}.txt", result.stdout + result.stderr)
            bench.check(f"recovered partition {other} is semantically equal to its catalogued save",
                        result.returncode == 0, (result.stdout + result.stderr)[-200:])
    status, body = _status("POST", f"http://{gateway}/registry/saves?partition={partition}&node=node-1&epoch={epoch}"
                                   f"&generation={generation}&sequence={sequence + 1}&kind=Periodic&size=0"
                                   f"&checksum=stale&clusterTime=0&location=stale")
    bench.record("stale-save.txt", f"{status}\n{body}")
    bench.check("a save from the dead epoch is refused with 409 (P1-REC-12)", status == 409, f"HTTP {status}")
    recovered = next((p["memberCount"] for p in _partitions(bench, "node-2") if p["id"] == partition), None)
    bench.check("node-2 holds the recovered partition with every member", recovered == source_count,
                f"{recovered} of {source_count}")

    if variant == "recovery":
        # WA-2: the dead node's PCU report expires from the merged ledger instead of double-counting.
        try:
            wait_until(lambda: "node-1" not in [n.get("origin") for n in
                                                (bench.cluster.node("node-2", "/pcu-test-status").get("ledger") or {}).get("nodes") or []],
                       90, "the dead node's PCU report to expire")
            expired_pcu = True
        except ClusterError:
            expired_pcu = False
        bench.check("the dead node's PCU report expired from the ledger", expired_pcu)
        bench.check("the WA logged the PCU report expiry", bool(_lines(bench, "wa-1", r"pcu report expired origin=node-1")))

    _reconnect_recovered_client(bench, partition)
    if variant == "rollback":
        kit = _kit(bench)
        name = _custom_name(bench, kit)
        bench.check("recovery restored the catalogued name, not the uncommitted one", name == "SAV05-catalogued", str(name))
    if variant == "recovery":
        hold.join(timeout=45)
        balance = bench.cluster.node("wa-1", f"/banking-test-status?ownerId={payer}").get("balance")
        bench.check("the leaked reservation is visible before node-1 restarts", balance == 400, str(balance))
        bench.cluster.restart_node("node-1")
        pattern = rf"store payment reconciled reserve=.* payer={payer} amount=100 reserved=True refunded=True"
        try:
            reconciled = _wait_line(bench, "node-1", pattern, RECOVERY_TIMEOUT)
        except ClusterError as error:
            reconciled = None
            bench.record("reconcile-timeout", str(error))
        bench.check("the restarted node-1 reconciled the leaked reservation", reconciled is not None)
        try:
            wait_until(lambda: bench.cluster.node("wa-1", f"/banking-test-status?ownerId={payer}").get("balance") == 500,
                       HANDOVER_TIMEOUT, "the refund at the WA")
            restored = True
        except ClusterError:
            restored = False
        bench.check("the refund restored the payer's balance", restored)
        time.sleep(3)
        final = bench.cluster.node("wa-1", f"/banking-test-status?ownerId={payer}")
        bench.record("recovery-store-reconcile", {"final": final, "reconcileLine": reconciled})
        bench.check("the reconciled balance holds (no second replay)", final.get("balance") == 500, json.dumps(final))
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(partition=partition, generation=[generation, new_generation], members=source_count,
                    rollbackExposureMs=exposure, variant=variant)



#: P1-REC-14: how long node-2 holds a granted recovery claim before its census and materialization,
#: against the bench's 15 s partition lease (node-1 took 36 s over them live, T-0295).
CLAIM_DELAY_MS = 25000


def _distinct(lines: list[str]) -> set[tuple[str, str]]:
    """One entry per logged EVENT: _lines reads both <node>.log and the node's SpaceEngineersDedicated
    log, and every [ClusterPlugin] line is in both (the second carrying a thread tag)."""
    return {(line[:23], line[line.find("[ClusterPlugin]"):]) for line in lines}


def _held_claim_setup(bench, node2_env: dict) -> tuple[int, int, int, int]:
    """P1-REC-14/15: node-2 runs with ``node2_env``; a client joins node-1, node-2 holds the newest save of the
    client partition, node-1 is killed, and node-2 is granted the recovery claim. Returns
    (partition, generation before, granted generation, members of the newest save)."""
    env = bench.cluster.config.node_env_by_id
    env["node-2"] = {**env.get("node-2", {}), **node2_env}
    _join(bench)
    try:
        result = bench.cluster.node("node-1", "/self-test?section=census-same-lineage")
        bench.record("node-self-test-census-same-lineage", result)
        passed = bool(result.get("ok")) and "census-same-lineage" in (result.get("ran") or [])
        bench.check("node self-test section `census-same-lineage` passes", passed, json.dumps(result)[:300])
    except ClusterError as error:
        bench.check("node self-test section `census-same-lineage` passes", False, str(error)[:300])
    partition = _client_partition(_partitions(bench, "node-1"))
    if partition is None:
        raise ClusterError("node-1 did not report the connected client's partition")
    before = _wait_new_save(bench, partition)
    bench.record("pre-kill-save-wait-s", before)
    generation = next(row["Generation"] for row in _owners(bench) if row["Partition"] == partition)
    # The recovery reads the newest save node-2 HOLDS: one still only on the killed node falls back to an
    # older one by design (a bounded rollback, which is P1-REC-01's subject, not this one's). So kill only
    # once node-2 has the newest save, and compare the members against what that save carried.
    newest = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    save_id = newest["SaveId"]
    _wait_line(bench, "node-2", rf"replicated save {partition}/{save_id['Generation']}/{save_id['Sequence']} from node-1",
               RECOVERY_TIMEOUT)
    members = len(_world_tool("verify-mps", newest["Locations"][0])["entityIds"])
    bench.record("pre-kill-save", {"saveId": save_id, "members": members})
    bench.cluster.kill(slot="node-1")

    def claimed():
        bench.cluster.post("/registry/leases/expire")
        row = next((r for r in _owners(bench) if r["Partition"] == partition), None)
        return row if row and row.get("Node") == "node-2" and row["Generation"] > generation else None

    owner = wait_until(claimed, RECOVERY_TIMEOUT, "node-2's recovery claim of node-1's partition")
    delay = _wait_line(bench, "node-2", r"recovery claim test delay", 30)
    bench.check("the seam held node-2's granted claim (the interleaving was forced)", bool(delay), delay[-120:])
    return partition, generation, owner["Generation"], members


def _sweep_leases(bench, seconds: float) -> None:
    """Sweep leases as the Gateway's own timer does, for ``seconds``."""
    end = time.time() + seconds
    while time.time() < end:
        bench.cluster.post("/registry/leases/expire")
        time.sleep(2)


def p1_rec_14(bench) -> None:
    """T-0295 forced interleaving: a granted recovery claim keeps its lease while the node is still
    taking the peer census and fetching the save.

    Live 2026-09-29 16:52-16:53, node-1 spent 36 s between the registry's grant of 227734 and queueing
    the load (a 5 s census timeout per silent peer plus a save fetched from the slow holder) with the
    claim not yet on its heartbeat; the 15 s lease expired, the registry re-offered the partition to
    node-5, and the two recoveries of one partition ended with neither keeping its two members.
    The seam CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS holds node-2 there for 25 s after the grant. Green:
    no lease expiry for node-2's claim, ONE claim, loaded with every member and kept. The red is the
    same run with CLUSTER_RECOVERY_LEASE_AT_GRANT=0 (REGRESSION env P1_REC_14_RED=1).
    Also runs the node self-test section census-same-lineage (the census half of the T-0295 fix)."""
    red = os.environ.get("P1_REC_14_RED") == "1"
    extra = {"CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS": str(CLAIM_DELAY_MS)}
    if red:
        extra["CLUSTER_RECOVERY_LEASE_AT_GRANT"] = "0"
    partition, generation, granted, members = _held_claim_setup(bench, extra)
    # Through the delay and two more lease periods.
    _sweep_leases(bench, CLAIM_DELAY_MS / 1000 + 30)
    expired = _lines(bench, "gateway", rf"lease expired partition={partition} node=node-2 ")
    bench.record("lease-expired-lines", expired)
    bench.check("node-2's claim kept its lease through the held materialization", not expired,
                "; ".join(line[:160] for line in expired[:2]))
    claims = _distinct(_lines(bench, "node-2", rf"recovery claimed partition {partition} generation "))
    bench.record("claim-lines", sorted(claims))
    bench.check("node-2 claimed the partition exactly once", len(claims) == 1, f"{len(claims)} claim(s)")
    loaded = _distinct(_lines(bench, "node-2", rf"recovery loaded partition {partition} generation {granted} with {members} members"))
    bench.check("node-2 loaded the granted generation with every member", len(loaded) == 1, f"{len(loaded)} line(s)")
    discarded = _distinct(_lines(bench, "node-2", rf"discarded stale local ownership: {partition}$"))
    bench.check("node-2 did not discard its recovered copy", not discarded, str(len(discarded)))
    row = next((r for r in _owners(bench) if r["Partition"] == partition), {})
    bench.check("the registry still has node-2 at the granted generation", row.get("Node") == "node-2"
                and row.get("Generation") == granted, json.dumps(row)[:200])
    held = next((p["memberCount"] for p in _partitions(bench, "node-2") if p["id"] == partition), None)
    bench.check("node-2 holds the partition with every member", held == members, f"{held} of {members}")
    bench.exercised(partition=partition, generation=[generation, granted], members=members,
                    claimDelayMs=CLAIM_DELAY_MS, red=red)


def p1_rec_15(bench) -> None:
    """T-0297 forced interleaving: a recovery claim the registry takes back while it is still queued is
    dropped, not loaded as a duplicate of the new claimant's load and then discarded (node-1's
    16:53:15 load-then-discard of 227734 in T-0295).

    node-2 holds every granted claim 25 s (CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS) and leaves pending claims
    out of its heartbeat (CLUSTER_TEST_HEARTBEAT_OMIT_PENDING), so each claim's 15 s lease lapses while it
    waits, with the node heartbeating normally. Nothing may be loaded under a lost claim. The seam makes
    every re-claim lapse too, so the partition is never held here - that is the seam, not the product.
    Red: CLUSTER_RECOVERY_DROP_LOST_PENDING=0 (REGRESSION env P1_REC_15_RED=1)."""
    red = os.environ.get("P1_REC_15_RED") == "1"
    extra = {"CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS": str(CLAIM_DELAY_MS), "CLUSTER_TEST_HEARTBEAT_OMIT_PENDING": "1"}
    if red:
        extra["CLUSTER_RECOVERY_DROP_LOST_PENDING"] = "0"
    partition, generation, granted, members = _held_claim_setup(bench, extra)
    _sweep_leases(bench, CLAIM_DELAY_MS / 1000 + 20)
    expired = _lines(bench, "gateway", rf"lease expired partition={partition} node=node-2 epoch=[0-9]+ generation={granted} ")
    bench.check("the granted claim lapsed while queued (the interleaving was forced)", bool(expired), str(len(expired)))
    loaded = _distinct(_lines(bench, "node-2", rf"recovery loaded partition {partition} generation {granted} "))
    bench.check("node-2 did not load the claim it lost while queued", not loaded, f"{len(loaded)} load(s)")
    discarded = _distinct(_lines(bench, "node-2", rf"discarded stale local ownership: {partition}$"))
    bench.check("node-2 discarded nothing (nothing was loaded under a lost claim)", not discarded, str(len(discarded)))
    skipped = _distinct(_lines(bench, "node-2", rf"recovery skipped: claim {partition} generation {granted} was lost before its load"))
    bench.check("node-2 logged the dropped load", len(skipped) == 1, f"{len(skipped)} line(s)")
    bench.exercised(partition=partition, generation=[generation, granted], members=members,
                    claimDelayMs=CLAIM_DELAY_MS, red=red)

#: P1-REC-16: how long node-1 holds the discard of its lost partitions and its fenced exit once it resumes, so its
#: doomed copies are still in its census when node-2's recovery census asks (CLAIM_DELAY_MS after the grant). Live,
#: that window is one heartbeat: a fenced node exits within milliseconds of learning it.
DISCARD_DELAY_MS = 40000


def _require(bench, label: str, passed: bool, detail: str = "") -> None:
    if not bench.check(label, passed, detail):
        raise ClusterError(f"{label}: {detail}"[:400])


def p1_rec_16(bench) -> None:
    """#1286 (F41) forced interleaving: a recovery census does not screen content against a doomed peer copy of
    ANOTHER partition. node-1 holds two stations in partition R; node-2 holds R's newest save, which carries both.
    The test seam lifts station E out of R and drops it 30 km away, a new partition Q with no save and no lineage
    record (the shape of an adoption after Q's last save), and node-1 is SIGSTOPped at once. node-2 is granted R's
    recovery and holds the claim CLAIM_DELAY_MS (CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS); node-1 resumes, is fenced,
    and holds its discard and exit DISCARD_DELAY_MS (CLUSTER_TEST_DISCARD_DELAY_MS), so node-2's census finds E in node-1's
    Q - a Dead node's copy. Green: node-2 defers the load (bounded re-check) until node-1's copy is gone, then loads
    E; E survives node-1's exit on node-2 only. Red: CLUSTER_RECOVERY_STALE_COPY_CHECK=0 (REGRESSION env
    P1_REC_16_RED=1): node-2 skips E as "already live on another node", node-1 discards Q, and E is gone.
    Also runs the node self-test section census-stale-copy."""
    red = os.environ.get("P1_REC_16_RED") == "1"
    env = bench.cluster.config.node_env_by_id
    env["node-1"] = {**env.get("node-1", {}), "CLUSTER_TEST_DISCARD_DELAY_MS": str(DISCARD_DELAY_MS)}
    node2 = {"CLUSTER_TEST_RECOVERY_CLAIM_DELAY_MS": str(CLAIM_DELAY_MS)}
    if red:
        node2["CLUSTER_RECOVERY_STALE_COPY_CHECK"] = "0"
    env["node-2"] = {**env.get("node-2", {}), **node2}
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")
    try:
        result = bench.cluster.node("node-2", "/self-test?section=census-stale-copy")
        bench.record("node-self-test-census-stale-copy", result)
        bench.check("node self-test section `census-stale-copy` passes",
                    bool(result.get("ok")) and "census-stale-copy" in (result.get("ran") or []), json.dumps(result)[:300])
    except ClusterError as error:
        bench.check("node self-test section `census-stale-copy` passes", False, str(error)[:300])

    stations = {name: bench.cluster.node("node-1", f"/igc-test-ensure-laser-at?name={name}&x=870000&y=0&z={z}", "POST")
                for name, z in (("f41-anchor", 0), ("f41-e", 60))}
    bench.record("stations", stations)
    anchor, entity = stations["f41-anchor"].get("grid"), stations["f41-e"].get("grid")
    if not anchor or not entity:
        raise ClusterError(f"the stations were not created: {json.dumps(stations)[:300]}")

    def holder(node_id: str, entity_id: int) -> int:
        try:
            return int(bench.cluster.node(node_id, f"/partition-of?entity={entity_id}").get("partition") or 0)
        except (ClusterError, OSError, ValueError):
            return 0

    partition = wait_until(lambda: holder("node-1", anchor) if holder("node-1", anchor) and holder("node-1", anchor) == holder("node-1", entity)
                           else None, 60, "both stations in one partition on node-1")
    wait_until(lambda: any(r["Partition"] == partition and r.get("Node") == "node-1" for r in _owners(bench)), 60,
               f"the registry assigning partition {partition} to node-1")

    def newest_save():
        try:
            return bench.cluster.get(f"/registry/saves/newest?partition={partition}")
        except ClusterError:
            return None

    def fresh_replicated():
        # A save registered just now, on node-2 already, carrying E: the lift below lands well before R's next save.
        newest = newest_save()
        if not newest:
            return None
        save_id = newest["SaveId"]
        if not _lines(bench, "node-2", rf"replicated save {partition}/{save_id['Generation']}/{save_id['Sequence']} from node-1"):
            return None
        return newest if entity in _world_tool("verify-mps", newest["Locations"][0])["entityIds"] else None

    _wait_new_save(bench, partition)
    newest = wait_until(fresh_replicated, 20, f"node-2 holding partition {partition}'s newest save, with E")
    bench.record("cover-save", newest["SaveId"])
    generation = next(row["Generation"] for row in _owners(bench) if row["Partition"] == partition)
    bench.record("lift", bench.cluster.node("node-1", f"/duplicate-test?entity={entity}&op=lift", "POST"))
    # Only once R no longer lists E: dropped while R still did, the sweep mints E as a split piece of R - a lineage
    # record, which cuts E out of R's cover (the safe case, not F41).
    wait_until(lambda: not any(entity in [m["id"] for m in p.get("members") or []] for p in _partitions(bench, "node-1")),
               30, "R no longer listing the lifted station")

    def drop():
        try:
            return bench.cluster.node("node-1", f"/duplicate-test?entity={entity}&op=drop&dx=30000", "POST")
        except ClusterError:
            return None

    pid = bench.cluster._node_pid("node-1")
    dropped = wait_until(drop, 30, "the lifted station dropped 30 km away on node-1")
    stray = dropped.get("partition") or wait_until(lambda: holder("node-1", entity) or None, 10, "E in a partition of its own")
    os.kill(pid, signal.SIGSTOP)
    stopped = time.time()
    bench.record("dropped", {**dropped, "partition": stray, "pid": pid})
    try:
        _require(bench, "E landed in a new partition Q, not back in R", stray != partition, f"Q={stray} R={partition}")
        minted = _lines(bench, "node-1", rf"topology minted_piece partition={stray} source={partition} ")
        _require(bench, "Q is a new layout, not a split piece of R (no lineage record of E's move)", not minted,
                 "; ".join(minted)[:300])
        after = newest_save()
        bench.record("cover-save-at-stop", after and after["SaveId"])
        _require(bench, "R's newest save (the recovery's cover) still carries E",
                 bool(after) and entity in _world_tool("verify-mps", after["Locations"][0])["entityIds"])

        def claimed():
            bench.cluster.post("/registry/leases/expire")
            row = next((r for r in _owners(bench) if r["Partition"] == partition), None)
            return row if row and row.get("Node") == "node-2" and row["Generation"] > generation else None

        owner = wait_until(claimed, RECOVERY_TIMEOUT, "node-2's recovery claim of partition R")
        _wait_line(bench, "node-2", r"recovery claim test delay", 30)
    finally:
        os.kill(pid, signal.SIGCONT)
    bench.record("stopped-s", round(time.time() - stopped, 1))
    held = _wait_line(bench, "node-1", r"lost ownership discard test delay", 30)
    bench.check("node-1 resumed and holds its doomed copies (the interleaving was forced)", bool(held), held[-120:])
    loaded = _wait_line(bench, "node-2", rf"recovery loaded partition {partition} generation {owner['Generation']} with",
                        CLAIM_DELAY_MS / 1000 + 90)
    bench.record("recovery-loaded", loaded)
    skipped = _distinct(_lines(bench, "node-2", rf"recovery skipped entity {entity} for partition {partition}"))
    bench.check("node-2 did not skip E against node-1's doomed copy", not skipped, "; ".join(line for _, line in sorted(skipped))[:300])
    deferred = _distinct(_lines(bench, "node-2", rf"recovery of partition {partition} generation [0-9]+ deferred: "))
    bench.record("deferred-lines", sorted(deferred))
    bench.check("node-2 deferred the load while only the doomed copy held E", bool(deferred), str(len(deferred)))
    wait_until(lambda: not bench.cluster._node_pids("node-1"), 120, "node-1's exit after its fence")
    final = holder("node-2", entity)
    bench.record("final", {"node2Partition": final, "q": stray, "qRow": next((r for r in _owners(bench) if r["Partition"] == stray), None)})
    bench.check("E survives on node-2 after node-1 is gone", final != 0, f"partition {final}")
    bench.check("node-2 holds E in the recovered partition", final == partition, f"partition {final}, R {partition}")
    bench.exercised(partition=partition, stray=stray, entity=entity, generation=[generation, owner["Generation"]],
                    claimDelayMs=CLAIM_DELAY_MS, discardDelayMs=DISCARD_DELAY_MS, red=red)


def _raw(bench, node_id: str, path: str) -> str:
    control = _nodes(bench)[node_id].control
    with urllib.request.urlopen(f"http://{control}/cluster{path}", timeout=120) as response:
        return response.read().decode()


def p1_rec_01(bench) -> None:
    _crash_and_recover(bench, "recovery")


def p1_sav_05(bench) -> None:
    _crash_and_recover(bench, "rollback")


def p1_sav_01(bench) -> None:
    _crash_and_recover(bench, "fidelity")




# P2-WAR-01 - a World Authority recovery that rewinds past a newer save on disk says so
# ---------------------------------------------------------------------------


def _global_saves(bench) -> list[tuple[int, int]]:
    """(generation, sequence) of every global save in wa-1's own directory, oldest first. The
    filenames are UPPERCASE hex, g{generation:X8}-s{sequence:X8}.mps (ClusterControlServer:724)."""
    root = bench.run_root / "wa-1-data" / "cluster" / "saves" / "GLOBAL"
    found = []
    for path in sorted(root.glob("g*-s*.mps")) if root.is_dir() else []:
        match = re.fullmatch(r"g([0-9A-Fa-f]{8})-s([0-9A-Fa-f]{8})", path.stem)
        if match:
            found.append((int(match.group(1), 16), int(match.group(2), 16)))
    return sorted(found)


def _restored_save(bench) -> tuple[int, int] | None:
    """The save the WA actually loaded, from the LAST such line - a warm restart leaves the
    previous start's line in the same log, and reading the first one would answer about the wrong
    boot."""
    lines = _lines(bench, "wa-1", r"global save restored generation=\d+ sequence=\d+")
    if not lines:
        return None
    match = re.search(r"generation=(\d+) sequence=(\d+)", lines[-1])
    return (int(match.group(1)), int(match.group(2)))


def p2_war_01(bench) -> None:
    """T-0194: the recovery point is the CATALOGUE's newest global save, not the disk's, and the
    gateway registry flushes at most once per CLUSTER_PERSIST_INTERVAL_SECONDS. An unclean stop
    inside that window therefore loses the newest registrations while the FILES they name sit
    complete in the World Authority's own directory, and the world comes back rewound.

    The fix does not move the recovery point - a save the registry never acknowledged may belong to
    a fenced generation - it makes the rewind loud. This scenario manufactures the window instead of
    racing it: the interval is set to an hour, so the second global save is registered in the
    gateway's MEMORY and can never reach disk before the kill."""
    record = {}
    previous = os.environ.get("CLUSTER_PERSIST_INTERVAL_SECONDS")
    os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "3600"
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        wa_log = bench.run_root / "wa-1.log"

        def capture_global():
            before = wa_log.read_text(errors="replace").count("global save registered")
            bench.cluster.node("wa-1", "/global-save-capture", "POST")
            wait_until(lambda: wa_log.read_text(errors="replace").count("global save registered") > before or None,
                       90, "the WA to register a global save")

        # Save 1, then the documented operator commit point: this one the catalogue keeps.
        capture_global()
        bench.cluster.post("/registry/checkpoint")
        catalogued = _global_saves(bench)
        bench.check("the committed global save is on disk", len(catalogued) >= 1, json.dumps(catalogued))

        # Save 2, with NO checkpoint behind it. At a 3600 s interval nothing will flush it, which is
        # what an unclean gateway stop inside the default 60 s window produces - manufactured rather
        # than raced, so the scenario cannot decide a coin toss.
        capture_global()
        on_disk = _global_saves(bench)
        record["onDisk"] = [f"{g}/{s}" for g, s in on_disk]
        bench.check("a newer global save reached disk before the kill", len(on_disk) > len(catalogued),
                    json.dumps(record["onDisk"]))

        bench.cluster.kill()
        bench.cluster.start()
        bench.cluster.wait_ready(nodes=["node-1", "wa-1"])

        restored = _restored_save(bench)
        newest = on_disk[-1]
        record["restored"] = restored and f"{restored[0]}/{restored[1]}"
        # PRECONDITION, NOT THE RESULT. If the catalogue kept the newer save there is no rewind to
        # announce, and a silent pass here would be the scenario deciding it had tested something.
        bench.check("the recovery rewound past the newest save on disk (precondition)",
                    restored is not None and restored < newest,
                    f"restored {record['restored']}, newest on disk {newest[0]}/{newest[1]}")

        warnings = _lines(bench, "wa-1", r"WARNING: global save rewind")
        record["warnings"] = warnings
        bench.check("the rewind is announced exactly once", len(warnings) == 1, json.dumps(warnings))
        line = warnings[-1] if warnings else ""
        bench.check("the warning names the save it restored",
                    f"restored generation={restored[0]} sequence={restored[1]}" in line, line)
        bench.check("the warning names the newer save it did not restore",
                    f"generation={newest[0]} sequence={newest[1]}" in line.split("but this host")[-1], line)
        # The point of reading the file is to separate "the catalogue lost a finished save" from
        # "that write never finished"; this one finished, so it must not be reported as garbage.
        bench.check("the newer save is reported as well-formed", "well-formed, body checksum" in line, line)
        bench.exercised(route="wa-1 global save x2 -> unflushed catalogue -> kill -> warm restart")
    finally:
        if previous is None:
            os.environ.pop("CLUSTER_PERSIST_INTERVAL_SECONDS", None)
        else:
            os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = previous
        bench.record("wa-global-save-rewind", record)


p2_war_01.regular_nodes = 1

def _try(function, default=None):
    try:
        return function()
    except (ClusterError, OSError, ValueError):
        return default


def _save_writers(bench):
    """node-1's partition, the newest global and partition save sequences, and a capture of both."""
    cluster = bench.cluster
    partition = wait_until(
        lambda: next((row["Partition"] for row in _owners(bench) if row["Node"] == "node-1"), None),
        180,
        "node-1 to own a partition",
    )

    def newest_global() -> int:
        return max([row["SaveId"]["Sequence"] for row in cluster.get("/registry/global-saves")], default=0)

    def newest_save() -> int:
        return (
            _try(lambda: cluster.get(f"/registry/saves/newest?partition={partition}"))
            or {"SaveId": {"Sequence": 0}}
        )["SaveId"]["Sequence"]

    def capture_both() -> None:
        sequences = newest_global(), newest_save()
        # A periodic save in flight answers 409 busy, which is not what this checks.
        wait_until(
            lambda: _try(lambda: cluster.node("wa-1", "/global-save-capture", "POST") or True),
            90,
            "the WA to take a capture",
        )
        wait_until(
            lambda: _try(lambda: cluster.node("node-1", f"/save-capture?partition={partition}", "POST") or True),
            90,
            "node-1 to take a capture",
        )
        wait_until(lambda: newest_global() > sequences[0] or None, 90, "the WA to register a global save")
        wait_until(lambda: newest_save() > sequences[1] or None, 90, "node-1 to register a partition save")

    return partition, newest_global, newest_save, capture_both


def p2_war_02(bench) -> None:
    """SE-819: a Gateway that dies alone comes back from a store older than the World Authority and the nodes, which
    keep their sequences in memory. The saves they register next jump past the rows the store lost, and those saves
    must land: refused, the World Authority retried its Final once a second for as long as it lived and the stop never
    finished. The window is manufactured as in P2-WAR-01: at an hour's interval nothing but the checkpoint flushes."""
    record = {}
    previous = os.environ.get("CLUSTER_PERSIST_INTERVAL_SECONDS")
    os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "3600"
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        cluster = bench.cluster
        partition, newest_global, newest_save, capture_both = _save_writers(bench)

        capture_both()
        cluster.post("/registry/checkpoint")
        kept = newest_global(), newest_save()
        capture_both()
        capture_both()
        reached = newest_global(), newest_save()
        record.update(kept=kept, reached=reached)

        pids = cluster._pgrep(f"ClusterRegistry.*{re.escape(str(cluster.config.root))}/gateway-registry")
        bench.check("the Gateway process is found", bool(pids), str(pids))
        for pid in pids:
            os.kill(pid, signal.SIGKILL)
        cluster.start(nodes=[])
        cluster.wait_node_log("gateway", "Gateway listening", timeout=120)
        wait_until(lambda: _try(lambda: cluster.get("/registry/nodes")) or None, 60, "the restarted Registry to answer")
        rewound = newest_global(), newest_save()
        record["rewound"] = rewound
        # PRECONDITION, NOT THE RESULT: without the rewind this would prove nothing.
        bench.check(
            "the restarted Registry lost the unflushed saves (precondition)",
            rewound[0] < reached[0] and rewound[1] < reached[1],
            json.dumps(record),
        )

        capture_both()
        after = newest_global(), newest_save()
        record["after"] = after
        bench.check(
            "the World Authority's next save registers past the lost rows", after[0] > reached[0], json.dumps(record)
        )
        bench.check("the node's next save registers past the lost rows", after[1] > reached[1], json.dumps(record))

        shutdown = cluster.post("/registry/shutdown/graceful?timeoutSeconds=180&forceAfterSeconds=0", timeout=210)
        record["shutdown"] = shutdown
        last = (shutdown or {}).get("LastShutdown") or {}
        bench.check(
            "the stop after the crash ends clean",
            shutdown.get("Phase") == 4 and last.get("Dirty") == [],
            json.dumps(shutdown)[:400],
        )
        bench.check(
            "the stop's global save is a Final past the crash",
            (last.get("GlobalRef") or {}).get("Sequence", 0) > after[0],
            json.dumps(last)[:300],
        )
        refused = _lines(bench, "wa-1", r"Global save registration was fenced") + _lines(
            bench, "node-1", r"save registration fenced"
        )
        bench.check("no save was refused", not refused, "; ".join(refused)[:300])
        bench.exercised(
            route="saves past the last flush -> SIGKILL of the Gateway alone -> restart -> saves and a clean stop"
        )
    finally:
        if previous is None:
            os.environ.pop("CLUSTER_PERSIST_INTERVAL_SECONDS", None)
        else:
            os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = previous
        bench.record("gateway-crash-save-sequence", record)


p2_war_02.regular_nodes = 1

def p2_war_03(bench) -> None:
    """SE-843: a World Authority that cannot get a global save registered keeps retrying that save and captures no
    other. Nothing showed until a stop failed (SE-819). A held registration and a real refusal must both turn into the
    degraded reason and the age, and the refusal must log the Registry's answer. The refusal is manufactured with a
    catalogue row ahead of the writer, the same fence as any sequence the Registry will not take. That row names no
    file, so this scenario ends with the bench's kill rather than a stop."""
    record = {}
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        cluster = bench.cluster
        rows = wait_until(lambda: cluster.get("/registry/global-saves") or None, 180, "the first global save")

        def lifecycle() -> dict:
            return cluster.get("/registry/status")["lifecycle"]

        def stale() -> bool:
            return "globalSaveStale" in (lifecycle().get("DegradedReasons") or [])

        def check_stalled(label: str) -> None:
            state = lifecycle()
            status = cluster.get("/admin/v1/status")["data"]
            record[label] = {
                "age": state.get("GlobalSaveAgeSeconds"),
                "phase": state.get("Phase"),
                "health": status.get("health"),
                "reasonCodes": status.get("reasonCodes"),
            }
            bench.check(f"{label}: the age passed the 5 minutes", state.get("GlobalSaveAgeSeconds", 0) >= 300, str(state))
            bench.check(f"{label}: the phase is Degraded", state.get("Phase") == 2, str(state))
            bench.check(
                f"{label}: the admin status is Unhealthy with the reason code",
                status.get("health") == "Unhealthy" and "globalSaveStale" in status.get("reasonCodes", []),
                json.dumps(record[label]),
            )

        bench.check("a cluster that saves is not stale", not stale(), str(lifecycle()))

        # Saves that do not land for a reason that ends: the reason comes, and goes with the next registration.
        cluster.node("wa-1", "/global-save-register-hold?ms=420000", "POST")
        wait_until(lambda: stale() or None, 400, "globalSaveStale during the hold")
        check_stalled("held")
        cluster.node("wa-1", "/global-save-register-hold?ms=1", "POST")
        wait_until(lambda: not stale() or None, 90, "the next global save to clear globalSaveStale")
        state = lifecycle()
        record["released"] = {"age": state.get("GlobalSaveAgeSeconds"), "phase": state.get("Phase")}
        bench.check(
            "the next save clears it", state.get("Phase") == 1 and state.get("GlobalSaveAgeSeconds", 999) < 90, str(state)
        )

        # A refusal that does not end.
        authority = next(row for row in cluster.get("/registry/nodes") if row["Node"] == "wa-1")
        generation = max(row["SaveId"]["Generation"] for row in rows)
        ahead = 1000000
        cluster.post(
            f"/registry/global-saves?node=wa-1&epoch={authority['Epoch']}&generation={generation}&sequence={ahead}"
            "&kind=Periodic&size=1&checksum=forced&clusterTime=0&location=forced-ahead"
        )
        refused = rf"Global save registration was fenced: .*sequence is fenced; expected {ahead + 1} or later"
        answer = wait_until(lambda: _lines(bench, "wa-1", refused) or None, 90, "the refusal in the World Authority's log")
        record["answer"] = answer[0]
        wait_until(lambda: stale() or None, 330, "globalSaveStale during the refusals")
        check_stalled("refused")
        report = subprocess.run(
            [sys.executable, str(HOST.package / "cli/cluster-status"), "--root", str(bench.run_root)],
            capture_output=True,
            text=True,
            timeout=120,
        ).stdout
        record["clusterStatus"] = [line for line in report.splitlines() if "global save" in line or "degraded" in line]
        bench.check(
            "cluster-status prints the age and the reason",
            "Newest global save:" in report and "!! degraded: globalSaveStale" in report,
            json.dumps(record["clusterStatus"]),
        )
        retries = len(_lines(bench, "wa-1", refused))
        record["retries"] = retries
        bench.check("the World Authority kept retrying", retries >= 60, str(retries))
        bench.exercised(route="held registration -> stale -> released -> clear -> refused registration -> stale")
    finally:
        bench.record("global-save-stale", record)


p2_war_03.regular_nodes = 1


def p2_war_04(bench) -> None:
    """SE-842: a SIGTERM stops the Gateway process, not the cluster. It commits the rows registered since the last
    flush and exits, and the nodes stay up for the respawn, so a recovery rewinds nothing it acknowledged. At an
    hour's interval nothing else flushes those rows, as in P2-WAR-02, where a SIGKILL loses them."""
    record = {}
    previous = os.environ.get("CLUSTER_PERSIST_INTERVAL_SECONDS")
    os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "3600"
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        cluster = bench.cluster
        partition, newest_global, newest_save, capture_both = _save_writers(bench)

        capture_both()
        cluster.post("/registry/checkpoint")
        capture_both()
        reached = newest_global(), newest_save()
        nodes = {slot: cluster._node_pid(slot) for slot in ("node-1", "wa-1")}
        record.update(reached=reached, nodes=nodes)

        def nodes_stayed() -> bool:
            return {slot: cluster._node_pid(slot) for slot in nodes} == nodes

        # Not the launch wrapper: it reports the Gateway's exit status below.
        gateway = [
            pid
            for pid in cluster._pgrep(f"ClusterRegistry.*{re.escape(str(cluster.config.root))}/gateway-registry")
            if open(f"/proc/{pid}/comm").read().strip() != "bash"
        ]
        bench.check("the Gateway process is found", len(gateway) == 1, str(gateway))
        os.kill(gateway[0], signal.SIGTERM)
        wait_until(lambda: _lines(bench, "gateway", r"exited with 143") or None, 15, "the Gateway to exit on SIGTERM")
        bench.check("the nodes stay up while the Gateway is gone", nodes_stayed(), json.dumps(record))

        cluster.start(nodes=[])
        cluster.wait_node_log("gateway", "Gateway listening", timeout=120)
        status = wait_until(lambda: _try(lambda: cluster.get("/registry/status")) or None, 60, "the Registry to answer")
        record["phase"] = status["lifecycle"]["Phase"]
        # Draining (3) or Down (4) would mean the signal started a cluster shutdown.
        bench.check("the signal started no cluster shutdown", record["phase"] not in (3, 4), json.dumps(record))
        kept = (
            sorted(row["SaveId"]["Sequence"] for row in cluster.get("/registry/global-saves")),
            sorted(row["SaveId"]["Sequence"] for row in cluster.get(f"/registry/saves?partition={partition}")),
        )
        record["kept"] = kept
        bench.check("the newest global save row survives the SIGTERM", reached[0] in kept[0], json.dumps(record))
        bench.check("the newest partition save row survives the SIGTERM", reached[1] in kept[1], json.dumps(record))

        capture_both()
        record["after"] = newest_global(), newest_save()
        skipped = _lines(bench, "gateway", r"skips the sequence")
        bench.check("the next saves follow the rows without a jump", not skipped, "; ".join(skipped)[:300])
        bench.check("the nodes stayed up through the respawn", nodes_stayed(), json.dumps(record))

        shutdown = cluster.post("/registry/shutdown/graceful?timeoutSeconds=180&forceAfterSeconds=0", timeout=210)
        last = (shutdown or {}).get("LastShutdown") or {}
        bench.check(
            "the stop after the respawn ends clean",
            shutdown.get("Phase") == 4 and last.get("Dirty") == [],
            json.dumps(shutdown)[:400],
        )
        bench.exercised(route="saves past the last flush -> SIGTERM of the Gateway alone -> respawn -> saves and a clean stop")
    finally:
        if previous is None:
            os.environ.pop("CLUSTER_PERSIST_INTERVAL_SECONDS", None)
        else:
            os.environ["CLUSTER_PERSIST_INTERVAL_SECONDS"] = previous
        bench.record("gateway-sigterm-flush", record)


p2_war_04.regular_nodes = 1


def p2_hang_01(bench) -> None:
    """SE-883: a game thread hang on a node that owns nothing ends through the exit watchdog, with no kill from the
    Registry, so the watchdog has to leave the evidence: the stacks name the blocking method, and the exit is as late as
    before. A healthy node that drains to Empty and a planned stop of the rest leave no stacks."""
    bench.start_cluster(nodes=["node-1", "node-2", "node-3", "wa-1"])
    regular = ["node-1", "node-2", "node-3"]
    try:
        section = bench.cluster.node("node-1", "/self-test?section=game-thread-stacks")
    except ClusterError as error:
        section = {"error": str(error)}
    bench.check("node self-test section `game-thread-stacks` passes", section.get("ok") and "game-thread-stacks" in section.get("ran", []), json.dumps(section)[:300])
    # The world's 3 partitions claimed, or a node that looks free can still get one.
    owners = wait_until(lambda: (rows := _owners(bench)) and len(rows) >= 3 and all(row["Node"] for row in rows) and {row["Node"] for row in rows},
                        RECOVERY_TIMEOUT, "every partition to be claimed")
    free = [node for node in regular if node not in owners]
    if not free:
        raise ClusterError(f"no node without a partition: {sorted(owners)}")
    hung = free[0]
    idle = next(node for node in regular if node != hung)
    bench.record("hang-nodes", {"owners": sorted(owners), "hung": hung, "idle": idle})

    started = time.time()
    bench.cluster.node(hung, "/game-thread-stall?ms=600000")
    exited = _wait_exit(bench, hung, timeout=150) - started
    text = next((log for log in (path.read_text(errors="replace") for path in _log_files(bench, hung)) if "(game thread)" in log), "")
    game_thread = text[text.find("(game thread)"):].split("\nthread ")[0]
    bench.record("hang-stacks.txt", game_thread or "\n".join(_lines(bench, hung, "exit watchdog")))
    bench.check("the hung node drained to Empty and its exit watchdog ended it, no Registry kill",
                bool(_lines(bench, hung, "exit watchdog armed .*drain complete")) and bool(_lines(bench, hung, "exit watchdog fired")))
    bench.check(f"it exited {exited:.0f} s after the hang, as late as without the stacks (76 s)", 70 <= exited <= 85, f"{exited:.1f} s")
    bench.check("the stacks name the blocking method on the game thread", "PluginBroadcastRoutes" in game_thread, game_thread[:400])

    epoch = _epoch(bench, idle)
    bench.cluster.post(f"/registry/nodes/close?node={idle}&epoch={epoch}&forceAfterSeconds=0")
    _wait_exit(bench, idle)
    bench.check("a healthy node that drains to Empty leaves no stacks", not _lines(bench, idle, "exit watchdog( fired|:)"))

    planned = [node for node in ("node-1", "node-2", "node-3", "wa-1") if node not in (hung, idle)]
    bench.check("the planned stop ended clean", bench.cluster.stop())
    for node in planned:
        bench.check(f"a planned stop of {node} leaves no stacks", not _lines(bench, node, "exit watchdog( fired|:)"))
    bench.exercised(hung=hung, idle=idle, exited_seconds=round(exited, 1))


p2_hang_01.regular_nodes = 3

SCENARIOS_RECOVERY = {
    "P1-DRAIN-01": (p1_drain_01, "registry-driven drain", "3"),
    "P1-REC-05a": (p1_rec_05a, "graceful drain-and-rotate", "4"),
    "P1-REC-05b": (p1_rec_05b, "force-drain deadline and reconnect", "4"),
    "P1-REC-01": (p1_rec_01, "node kill, lease expiry and catalog recovery", "4"),
    "P1-REC-14": (p1_rec_14, "a granted recovery claim keeps its lease through a slow materialization (T-0295)", "4"),
    "P1-REC-15": (p1_rec_15, "a recovery claim lost while queued is dropped, not loaded (T-0297)", "4"),
    "P1-REC-16": (p1_rec_16, "a recovery is not screened by a doomed peer copy of another partition (#1286)", "4"),
    "P1-SAV-05": (p1_sav_05, "bounded rollback to the catalogued value", "4"),
    "P1-SAV-01": (p1_sav_01, "round-trip fidelity across kill and recovery", "3"),
    "P2-WAR-01": (p2_war_01, "WA recovery announces a rewind past a newer global save", "3"),
    "P2-WAR-02": (p2_war_02, "saves after a lone Gateway crash register past the rows it lost, and the stop ends clean", "3"),
    "P2-WAR-03": (p2_war_03, "a global save that does not land shows in the status, and a refusal logs the Registry's answer", "3"),
    "P2-WAR-04": (p2_war_04, "a SIGTERM of the Gateway keeps the newest save rows and the nodes stay up", "3"),
    "P2-HANG-01": (p2_hang_01, "a hung node that owns nothing logs its stacks before its exit watchdog ends it (SE-883)", "3"),
}
