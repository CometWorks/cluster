"""Save pipeline and paused handovers: the former run-cluster.sh lanes `save-pipeline`, `save-fetch`,
`save-retention`, `save-tee`, `counters` and `nodeglobal`, as native scenarios.

Every one of them boots node-1, node-2 and the World Authority with a 30 s registry lease TTL (the
shell lanes refused to run with anything else: a periodic save, a paused handover or a replica
fetch can outlast a short lease, and an expiry mid-scenario turns the assertion into a recovery),
joins one headless client on node-1, and then:

* ``P1-SAV-03`` - the adaptive save ladder steps x2 -> x4 under a forced sim ratio, honours the
  per-partition cap and the player x2 cap, recovers x4 -> x2 -> x1, and delayed-write captures
  pass through a save queue bounded at two with capture/serialize/write metrics (and P1-SAV-04);
* ``P1-SAV-09`` - a direct save transfer to node-2 is cut at byte 128 and resumed, node-1's copy
  is corrupted, the partition is released, and a late node-3 quarantines the corrupt holder,
  fetches node-2's replica and recovers the partition from it;
* ``P1-SAV-08`` - keep-N retention keeps the newest save and a snapshot pin, the node deletes the
  directed files and acknowledges the tombstones, every remaining location verifies;
* ``P1-SAV-06`` - a clientless handover paused after capture has a durable handover tee; node-1
  is SIGKILLed, the transfer is released, and node-2 activates entities semantically equal to it;
  (2026-09-17: fails on the target-commit check - the Gateway asks the dead source for the tee
  status and discards the transfer; ticket T-0161);
* ``P1-HND-04`` - on the `to-counters` fixture, 19 one-unit inventory transfers and a terminal
  rename are queued while the handover is held at captured/Begun, and arrive exactly on node-2
  (and P1-GWY-06);
* ``P1-HND-05`` - the source prunes the client's non-transfer replicables during a paused
  handover (the accepted marker blip), and the client view returns to a steady state with no
  pruned NetworkId rebound to a different replicable.
"""

from __future__ import annotations

import filecmp
import json
import re
import subprocess
import threading
import time
from pathlib import Path

from bench import HOST
from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _grid_ops, _handover_url, _join, _lines, _log, \
    _owners, _partitions, _wait_line
from scenarios_recovery import RECOVERY_TIMEOUT, _raw, _registry_nodes, _world_tool

LEASE_TTL_SECONDS = 30
# Stable Cluster-Simple fixture entities: the Cluster Transport ship and the Start Beacon.
TRANSPORT_SHIP = 128014564749527614
START_BEACON = 114315348218176327
# The Counters fixture (MagnetarWorld to-counters): 64 SteelPlate in "Counters Source".
COUNTERS_TOTAL_RAW = 64_000_000
COUNTERS_TRANSFERS = 19
COUNTERS_UNIT_RAW = 1_000_000
COUNTERS_TERMINAL = "Counters Terminal Applied"
COUNTERS_TERMINAL_MIN = [0, 0, 2]
COUNTERS_STATES = ("begun", "captured", "paused-after-capture", "pause-released", "prepared", "imported",
                   "adopt-ready", "adopted", "active", "committed", "target-committed", "released")
FAR_BROADCASTER = "MyFarBroadcasterReplicable"


# -- shared plumbing ------------------------------------------------------------------------


def _join_long_lease(bench):
    bench.cluster.config.lease_ttl_seconds = LEASE_TTL_SECONDS
    return _join(bench)


def _world_run(*args: str) -> subprocess.CompletedProcess:
    """MagnetarWorld without JSON parsing (mps-to-sector, semantic-diff): the caller judges the exit code."""
    return subprocess.run(["dotnet", str(HOST.package / "tools/MagnetarWorld/MagnetarWorld.dll"), *args],
                          capture_output=True, text=True, timeout=300)


def _save_status(bench, node_id: str = "node-1") -> dict:
    return bench.cluster.node(node_id, "/save-status")


def _save_test(bench, query: str, node_id: str = "node-1") -> None:
    bench.cluster.node(node_id, f"/save-test?{query}", method="POST")


def _poll(predicate, timeout: float):
    """wait_until that answers None instead of raising: the caller turns the outcome into a check."""
    try:
        return wait_until(predicate, timeout, "condition")
    except ClusterError:
        return None


def _clientless_node1_partition(bench) -> tuple[int, list[dict], list[dict]]:
    """A clientless partition node-1 owns, with the partition report and owner table it came from.

    The World Authority coalesces the near-origin Start Beacon and the Cluster Transport ship into
    one node-1 partition holding the client (gap < sync distance), which leaves no clientless
    partition. Relocating the ship 15 km away splits one back out
    (T-0375: beyond the default split distance, 4 x the 3 km sync distance since T-0319; 6 km no longer splits)."""
    def find():
        report = _partitions(bench, "node-1")
        owners = _owners(bench)
        owned = {row["Partition"] for row in owners if row["Node"] == "node-1"}
        partition = next((p["id"] for p in report if not p.get("clients") and p["id"] in owned), None)
        return (partition, report, owners) if partition is not None else None

    found = find()
    if found is None:
        _relocate_ship(bench)
        found = _poll(find, HANDOVER_TIMEOUT)
    if found is None:
        raise ClusterError("the scenario requires a clientless node-1 partition")
    return found


def _relocate_ship(bench) -> None:
    bench.cluster.node("node-1", f"/test-position?entity={TRANSPORT_SHIP}&relativeTo={START_BEACON}&z=15000",
                       method="POST")


def _holders(row: dict | None) -> list[str]:
    return list(((row or {}).get("LocationHolders") or {}).values())


def _holder_path(row: dict, node_id: str) -> str | None:
    return next((path for path, holder in (row.get("LocationHolders") or {}).items() if holder == node_id), None)


def _newest(bench, partition: int) -> dict | None:
    try:
        return bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    except ClusterError:
        return None


class _PausedHandover:
    """A handover requested with pauseAfterCapture=true, running in the background.

    The Gateway answers the request only when the transfer finishes, so the request runs on a
    thread while the scenario acts inside the pause and then releases it."""

    def __init__(self, bench, partition: int, clientless: bool = False):
        self.bench = bench
        self.partition = partition
        self.url = _handover_url(bench, partition, clientless=clientless) + "&pauseAfterCapture=true"
        self.result = None
        self.error: Exception | None = None
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        try:
            self.result = self.bench.cluster.post(self.url, timeout=300)
        except Exception as error:  # noqa: BLE001 - reported by the caller
            self.error = error

    def paused(self, transfer: str | None = None) -> dict | None:
        try:
            rows = self.bench.cluster.get("/handover/paused")
        except ClusterError:
            return None
        return next((row for row in rows or [] if row.get("paused")
                     and (row.get("transferId") == transfer if transfer else row.get("partition") == self.partition)),
                    None)

    def wait_pause(self, also=lambda: True, label: str = "handover") -> dict:
        deadline = time.time() + HANDOVER_TIMEOUT
        while time.time() < deadline:
            pause = self.paused()
            if pause and also():
                return pause
            if not self.thread.is_alive():
                raise ClusterError(f"the {label} request ended before the capture pause: "
                                   f"{self.error or self.result}")
            time.sleep(1)
        raise ClusterError(f"the {label} did not pause after capture")

    def release(self, transfer: str) -> dict:
        return self.bench.cluster.post(f"/handover/release?transferId={transfer}")

    def finish(self, timeout: float = HANDOVER_TIMEOUT) -> bool:
        self.thread.join(timeout)
        return not self.thread.is_alive()

    def release_all(self) -> None:
        """Teardown: never leave a transfer held (the shell harness's cleanup did the same)."""
        try:
            for row in self.bench.cluster.get("/handover/paused") or []:
                if row.get("transferId"):
                    self.bench.cluster.post(f"/handover/release?transferId={row['transferId']}", timeout=5)
        except ClusterError:
            pass


def _states_in_order(bench, transfer: str, partition: int, states) -> list:
    """The last Gateway log line of each transaction state, which must be strictly increasing."""
    text = _log(bench, "gateway").read_text(errors="replace").splitlines()
    order = []
    for state in states:
        pattern = re.compile(rf"handover transferId={re.escape(transfer)} partition={partition} state={state}\b")
        numbers = [number for number, line in enumerate(text, 1) if pattern.search(line)]
        order.append((state, numbers[-1] if numbers else None))
    return order


def _in_order(order: list) -> bool:
    lines = [line for _, line in order]
    return all(line is not None for line in lines) and all(a < b for a, b in zip(lines, lines[1:]))


def _client_survived(bench, label: str) -> None:
    bench.check(f"the client never disconnected during the {label} handover",
                not _lines(bench, "gateway", rf"Client {CLIENT_ID} disconnected"))
    bench.check(f"the client process is alive after the {label} handover", bench.clients[CLIENT_ID].alive())
    bench.check(f"the client's character is alive after the {label} handover",
                bench.living_character(CLIENT_ID) is not None)
    bench.screenshot(CLIENT_ID, "99-after")


# -- P1-SAV-03 / P1-SAV-04 ------------------------------------------------------------------


def p1_sav_03(bench) -> None:
    """Adaptive save ladder, its caps, and a bounded save queue under delayed writes."""
    _join_long_lease(bench)
    partition_ids = [p["id"] for p in _partitions(bench, "node-1")]
    if len(partition_ids) < 2:
        # The adaptive-save queue needs two partitions to exercise backpressure; the WA coalesces
        # the beacon and the ship into one (see _clientless_node1_partition).
        _relocate_ship(bench)
        partition_ids = _poll(lambda: (lambda ids: ids if len(ids) >= 2 else None)(
            [p["id"] for p in _partitions(bench, "node-1")]), HANDOVER_TIMEOUT) or partition_ids
    if len(partition_ids) < 2:
        raise ClusterError(f"save-pipeline requires at least two node-1 partitions, found {partition_ids}")

    def settled():
        status = _save_status(bench)
        return status if all(not row["pending"] for row in status["partitions"]) else None

    if _poll(settled, HANDOVER_TIMEOUT) is None:
        raise ClusterError("the initial saves did not settle")

    _save_test(bench, "simRatio=0.89&sampleSeconds=10")
    level = _save_status(bench)["adaptiveLevel"]
    bench.check("the adaptive save ladder stepped to x2", level == 2, str(level))
    _save_test(bench, "simRatio=0.89&sampleSeconds=30")
    ladder = _save_status(bench)
    bench.check("the adaptive save ladder stepped to x4", ladder["adaptiveLevel"] == 4, str(ladder["adaptiveLevel"]))

    # POLLED: the player x2 cap depends on the node's client-partition mapping, which refreshes at
    # 1 Hz - sampled right after the fixture relocation split, both fresh partitions read as
    # player-less and no row showed 60000 while the mapping caught up (2026-07-29 sweep).
    def capped():
        nonlocal ladder
        ladder = _save_status(bench)
        intervals = [row["effectiveIntervalMs"] for row in ladder["partitions"]]
        return intervals and max(intervals) <= 300000 and 60000 in intervals

    capped_ok = _poll(capped, 30) is not None
    bench.record("save-ladder-x4", ladder)
    bench.check("the x4 ladder honours the 300 s cap and the player x2 cap (a 60 s row)", capped_ok,
                json.dumps([row["effectiveIntervalMs"] for row in ladder["partitions"]]))
    _save_test(bench, "simRatio=0.98&sampleSeconds=30")
    level = _save_status(bench)["adaptiveLevel"]
    bench.check("the adaptive save ladder recovered to x2", level == 2, str(level))
    _save_test(bench, "simRatio=0.98&sampleSeconds=30&writeDelayMs=1500")
    level = _save_status(bench)["adaptiveLevel"]
    bench.check("the adaptive save ladder recovered to x1", level == 1, str(level))

    before = _save_status(bench)
    queued, last_error = 0, ""
    for partition in partition_ids:
        # 409 busy = the scheduler's own capture is pending on this partition - it drains in seconds
        # and the knob then queues. One-shot counting lost the race right after the ladder stepped
        # back to x1 and the scheduler caught up on lagged saves (2026-07-29, isolated run 3/10).
        for _ in range(30):
            try:
                bench.cluster.node("node-1", f"/save-capture?partition={partition}", method="POST")
                queued += 1
                break
            except ClusterError as error:
                last_error = str(error)
                time.sleep(1)
    bench.check("the delayed-write test queued two captures", queued >= 2, f"queued={queued} last={last_error[:200]}")
    pressured = _save_status(bench)
    bench.check("the save queue is bounded at two and exposes backpressure",
                pressured["queueDepth"] <= 2 and any(row["pending"] for row in pressured["partitions"]),
                json.dumps({"queueDepth": pressured["queueDepth"],
                            "pending": [row["partition"] for row in pressured["partitions"] if row["pending"]]}))
    after = _poll(settled, HANDOVER_TIMEOUT) or _save_status(bench)
    bench.check("the delayed captures completed with capture/serialize/write metrics",
                all(not row["pending"] and row["captureMs"] >= 0 and row["serializeMs"] > 0 and row["writeMs"] > 0
                    for row in after["partitions"]),
                json.dumps([{k: row.get(k) for k in ("partition", "pending", "captureMs", "serializeMs", "writeMs")}
                            for row in after["partitions"]])[:400])
    _save_test(bench, "simRatio=actual&writeDelayMs=0")
    bench.record("save-pipeline", {"before": before, "underBackpressure": pressured, "after": after})
    bench.exercised(partitions=partition_ids, queuedCaptures=queued)


# -- P1-SAV-09 ------------------------------------------------------------------------------


def p1_sav_09(bench) -> None:
    """Resumed replica transfer, corrupt holder quarantine, and a late node recovering from the replica."""
    _join_long_lease(bench)
    partition, report, _ = _clientless_node1_partition(bench)
    member_count = next(p["memberCount"] for p in report if p["id"] == partition)

    replicated = _poll(lambda: (lambda row: row if "node-2" in _holders(row) else None)(_newest(bench, partition)),
                       HANDOVER_TIMEOUT)
    if replicated is None:
        raise ClusterError("node-2 did not receive the initial replica")
    old_sequence = replicated["SaveId"]["Sequence"]

    _save_test(bench, "dropTransferAfterBytes=128")
    bench.cluster.node("node-1", f"/save-capture?partition={partition}", method="POST")
    last = {}

    def fresh_replica():
        nonlocal last
        last = _newest(bench, partition) or {}
        sequence = (last.get("SaveId") or {}).get("Sequence", 0)
        return last if sequence > old_sequence and "node-2" in _holders(last) else None

    replicated = _poll(fresh_replica, HANDOVER_TIMEOUT)
    if replicated is None:
        sequence = (last.get("SaveId") or {}).get("Sequence", 0)
        raise ClusterError("the fresh save was not catalogued" if sequence <= old_sequence
                           else "node-2 did not finish the interrupted replica")
    generation, sequence = replicated["SaveId"]["Generation"], replicated["SaveId"]["Sequence"]
    try:
        _wait_line(bench, "node-2", re.escape(f"resuming save {partition}/{generation}/{sequence} at byte 128"))
        bench.check("node-2 resumed the direct save transfer at byte 128", True)
    except ClusterError as error:
        bench.check("node-2 resumed the direct save transfer at byte 128", False, str(error))
    source_path, replica_path = _holder_path(replicated, "node-1"), _holder_path(replicated, "node-2")
    if not (source_path and replica_path and Path(source_path).is_file() and Path(replica_path).is_file()):
        raise ClusterError(f"the replicated save locations are missing: {replicated.get('LocationHolders')}")
    bench.check("the resumed replica is byte-identical to its source",
                filecmp.cmp(source_path, replica_path, shallow=False))
    bench.record("save-fetch-replica", _world_tool("verify-mps", replica_path))
    bench.record("save-fetch-catalog", replicated)

    _save_test(bench, "suppressRecovery=true", "node-1")
    _save_test(bench, "suppressRecovery=true", "node-2")
    with open(source_path, "r+b") as handle:
        handle.seek(100)
        original = handle.read(1)[0]
        handle.seek(100)
        handle.write(bytes([(original + 1) % 256]))
    if filecmp.cmp(source_path, replica_path, shallow=False):
        raise ClusterError("the source corruption injection did not alter the save")

    epoch = next(row["Epoch"] for row in _registry_nodes(bench) if row["Node"] == "node-1")
    release = bench.cluster.post(f"/registry/partitions/release?partition={partition}&node=node-1"
                                 f"&epoch={epoch}&generation={generation}")
    bench.record("save-fetch-release", release)
    released_generation = release["Generation"]

    node3 = bench.cluster.config.add_node("node-3")
    bench.cluster.start_node("node-3")
    bench.cluster.wait_node_log("node-3", "Game ready", timeout=300)
    for label, pattern in (
            # T-0376: on one host node-3 reads the other nodes' copies in place (T-0323), no fetch and no install.
            ("node-3 quarantined the corrupt node-1 holder", r"corrupt (remote|same-host) save holder node-1"),
            ("node-3 read node-2's healthy replica in place",
             rf"recovery reads {partition}/{generation}/{sequence} from this host's disk: .* \(holder node-2\)"),
            ("node-3 recovered the partition from the fetched save",
             rf"recovery loaded partition {partition} generation {released_generation} with {member_count} members")):
        try:
            bench.cluster.wait_node_log("node-3", pattern, timeout=RECOVERY_TIMEOUT)
            bench.check(label, True)
        except ClusterError as error:
            bench.check(label, False, str(error))

    installed = (bench.cluster.config.node_data(node3) / "cluster/saves" / f"P-{partition:016X}"
                 / f"g{generation:08X}-s{sequence:08X}.mps")
    # A fetched copy (another host) is installed atomically and must equal the replica; a same-host read installs nothing.
    if installed.is_file():
        bench.check("node-3's fetched save is byte-identical to node-2's replica", filecmp.cmp(replica_path, installed, shallow=False))
    catalog = bench.cluster.get("/registry/saves")
    row = next((r for r in catalog if r["SaveId"]["Partition"] == partition
                and r["SaveId"]["Generation"] == generation and r["SaveId"]["Sequence"] == sequence), None)
    bench.check("the Gateway retained node-1's corruption quarantine",
                row is not None and "node-1" in (row.get("CorruptHolders") or []), json.dumps(row)[:300])
    bench.record("save-fetch-node3", bench.cluster.node("node-3", "/partitions"))
    bench.record("save-fetch-catalog-after", catalog)
    bench.exercised(partition=partition, save=f"g{generation}/s{sequence}", releasedGeneration=released_generation,
                    members=member_count)


# -- P1-SAV-08 ------------------------------------------------------------------------------


def p1_sav_08(bench) -> None:
    """Keep-N retention with a snapshot pin, tombstone acknowledgement, verified survivors."""
    _join_long_lease(bench)
    partition = next((p["id"] for p in _partitions(bench, "node-1") if CLIENT_ID in (p.get("clients") or [])), None)
    if partition is None:
        raise ClusterError("node-1 did not report the client's partition")
    for _ in range(4):
        _poll(lambda: next((row for row in _save_status(bench)["partitions"]
                            if row["partition"] == partition and row["pending"] is False), None), HANDOVER_TIMEOUT)
        bench.cluster.node("node-1", f"/save-capture?partition={partition}", method="POST")

    def mine(rows):
        return [row for row in rows if row["SaveId"]["Partition"] == partition]

    saves = []

    def five():
        nonlocal saves
        saves = mine(bench.cluster.get("/registry/saves"))
        return len(saves) >= 5

    _poll(five, HANDOVER_TIMEOUT)
    ordered = sorted(saves, key=lambda row: row["SaveId"]["Sequence"])
    if len(ordered) < 2:
        raise ClusterError(f"retention could not select an old save pin from {len(ordered)} saves")
    pin = ordered[1]["SaveId"]
    bench.record("retention-snapshot", bench.cluster.post("/registry/snapshots", body={
        "SnapshotId": "p1-sav-08", "Kind": 1, "ClusterTime": 1, "PartitionRefs": [pin], "Dirty": []}))
    directives = bench.cluster.post("/registry/retention?keep=1&snapshots=1")
    bench.record("retention-directives", directives)
    bench.check("retention produced deletion directives", len(directives) > 0, str(len(directives)))
    # The newest is judged AT DECISION TIME, not from the catalog captured before the call: a
    # periodic save landing in between makes deleting the stale "newest" exactly what keep=1
    # promises (failed on that race 2026-07-28). Refetch and require whatever is newest now to be
    # neither directed for deletion nor pending-delete; the pin stays absolute.
    saves_after = mine(bench.cluster.get(f"/registry/saves?partition={partition}"))
    newest = max(saves_after, key=lambda row: row["SaveId"]["Sequence"])["SaveId"]
    bench.check("retention did not select the snapshot pin", all(d["SaveId"] != pin for d in directives), json.dumps(pin))
    bench.check("retention did not select the newest save", all(d["SaveId"] != newest for d in directives),
                json.dumps(newest))
    protected = [row for row in saves_after if row["SaveId"] in (pin, newest)]
    bench.check("neither the pin nor the newest save is pending delete",
                all(row.get("PendingDelete") is not True for row in protected), json.dumps(protected)[:300])

    remaining = []

    def acknowledged():
        nonlocal remaining
        remaining = bench.cluster.get("/registry/saves")
        return not any(row.get("PendingDelete") for row in remaining)

    bench.check("the node acknowledged every physical save deletion", _poll(acknowledged, HANDOVER_TIMEOUT) is not None,
                str(sum(1 for row in remaining if row.get("PendingDelete"))))
    left = [d["Location"] for d in directives if Path(d["Location"]).exists()]
    bench.check("retention left no deleted file on disk", not left, json.dumps(left)[:300])
    missing, unverified = [], []
    for row in remaining:
        for location in row["Locations"]:
            if not Path(location).is_file():
                missing.append(location)
                continue
            try:
                _world_tool("verify-mps", location)
            except (ClusterError, ValueError) as error:
                unverified.append(f"{location}: {str(error)[:100]}")
    bench.check("every catalogued save location exists", not missing, json.dumps(missing)[:300])
    bench.check("every catalogued save verifies", not unverified, json.dumps(unverified)[:300])
    bench.record("retention-after", remaining)
    bench.exercised(partition=partition, deleted=len(directives), pin=pin, newest=newest)


# -- P1-SAV-06 ------------------------------------------------------------------------------


def p1_sav_06(bench) -> None:
    """A durable handover tee carries a paused clientless transfer through a source SIGKILL."""
    _join_long_lease(bench)
    partition, report, owners = _clientless_node1_partition(bench)
    generation = next(row["Generation"] for row in owners if row["Partition"] == partition)
    member_count = next(p["memberCount"] for p in report if p["id"] == partition)
    handover = _PausedHandover(bench, partition, clientless=True)
    try:
        transfer = handover.wait_pause(label="save-tee handover")["transferId"]
        tee = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
        bench.record("save-tee-catalog", tee)
        if tee.get("Kind") not in (1, "HandoverTee"):
            raise ClusterError(f"the capture did not durably register a handover tee: {json.dumps(tee)[:300]}")
        bench.check("the capture durably registered a handover tee", True)
        tee_path = tee["Locations"][0]
        verified = _world_tool("verify-mps", tee_path)
        bench.record("save-tee-verify", verified)
        members = ",".join(str(entity) for entity in verified["entityIds"])
        sector = bench.dir / "save-tee-sector.xml"
        converted = _world_run("mps-to-sector", tee_path, str(sector))
        if converted.returncode != 0:
            raise ClusterError(f"mps-to-sector failed: {(converted.stdout + converted.stderr)[-300:]}")

        bench.cluster.kill(slot="node-1")
        process = bench.cluster.processes.get("node-1")
        if process is not None:
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                pass
        bench.record("save-tee-release", handover.release(transfer))
        try:
            _wait_line(bench, "gateway", rf"transferId={re.escape(transfer)} partition={partition} state=target-committed")
            bench.check("the Gateway committed the tee transfer on the target", True)
        except ClusterError as error:
            bench.check("the Gateway committed the tee transfer on the target", False, str(error))
        handover.finish()  # the request's own outcome is irrelevant: its source was killed
    finally:
        handover.release_all()

    target_count = next((p["memberCount"] for p in _partitions(bench, "node-2") if p["id"] == partition), None)
    bench.check("the tee target holds every member", target_count == member_count, f"{target_count} of {member_count}")
    target = bench.dir / "save-tee-target.xml"
    target.write_text(_raw(bench, "node-2", "/export"))
    diff = _world_run("semantic-diff", str(sector), str(target), members)
    bench.record("save-tee-semantic.txt", diff.stdout + diff.stderr)
    bench.check("node-2's state is semantically equal to the durable handover tee", diff.returncode == 0,
                (diff.stdout + diff.stderr)[-200:])
    bench.check("the tee handover advanced ownership exactly once to node-2",
                any(row["Partition"] == partition and row["Node"] == "node-2" and row["Generation"] == generation + 1
                    for row in _owners(bench)))
    bench.exercised(transfer=transfer, partition=partition, members=member_count,
                    generation=[generation, generation + 1])


# -- P1-HND-04 / P1-GWY-06 ------------------------------------------------------------------


def p1_hnd_04(bench) -> None:
    """Inventory and terminal operations queued during a held handover arrive exactly once."""
    api = _join_long_lease(bench)
    report = _partitions(bench, "node-1")
    partition = next((p["id"] for p in report if CLIENT_ID in (p.get("clients") or [])), None)
    if partition is None:
        raise ClusterError("node-1 did not report the counters client's partition")
    source_members = next(p for p in report if p["id"] == partition)["members"]
    source_members = [member["id"] for member in source_members]
    old_generation = next((row["Generation"] for row in _owners(bench)
                           if row["Partition"] == partition and row["Node"] == "node-1"), None)
    if old_generation is None:
        raise ClusterError("the counters partition's generation is unavailable")

    grids = _grid_ops(api, {"gets": [{"type": "grid_list"}]})["gets"][0]["data"]["grids"]
    grid = next((g["entityId"] for g in grids if g.get("name") == "Cluster Transport"), None)
    if grid is None:
        raise ClusterError("the Counters fixture grid is not visible to Remote")
    blocks = _grid_ops(api, {"gets": [{"type": "block_list", "entityId": grid, "limit": 100}]})["gets"][0]["data"]["blocks"]
    source_min = next((b["min"] for b in blocks if b.get("customName") == "Counters Source"), None)
    destination_min = next((b["min"] for b in blocks if b.get("customName") == "Counters Destination"), None)
    if source_min is None or destination_min is None:
        raise ClusterError("the Counters source/destination inventories were not found by stable name")
    inventory_get = {"gets": [{"type": "inventory", "gridId": grid, "min": source_min, "inventoryIndex": 0},
                              {"type": "inventory", "gridId": grid, "min": destination_min, "inventoryIndex": 0}]}

    def amounts():
        gets = _grid_ops(api, inventory_get)["gets"]
        return gets, [sum(item["amountRaw"] for item in (get.get("data") or {}).get("items") or []) for get in gets]

    gets, (source_before, destination_before) = amounts()
    item = next((i["itemId"] for i in gets[0]["data"]["items"]
                 if i["typeId"] == "MyObjectBuilder_Component" and i["subtypeId"] == "SteelPlate"), None)
    if item is None or source_before != COUNTERS_TOTAL_RAW or destination_before != 0:
        raise ClusterError(f"the Counters fixture was not exactly 64 source units and an empty destination "
                           f"({source_before}/{destination_before}, item {item})")
    terminal_get = {"gets": [{"type": "block", "gridId": grid, "min": COUNTERS_TERMINAL_MIN}]}
    terminal_before = _grid_ops(api, terminal_get)["gets"][0]
    if not terminal_before.get("success") or (terminal_before.get("data") or {}).get("customName") == COUNTERS_TERMINAL:
        raise ClusterError(f"the terminal's initial state is unavailable: {json.dumps(terminal_before)[:200]}")

    handover = _PausedHandover(bench, partition)
    try:
        pause = handover.wait_pause(label="counters handover")
        transfer, queued_before = pause["transferId"], pause["queuedPacketCount"]
        state = bench.cluster.get(f"/registry/handovers?id={transfer}")["State"]
        bench.check("the paused registry transaction is Begun", state in (0, "Begun"), str(state))
        source = bench.cluster.node("node-1", f"/status?transferId={transfer}&partition={partition}"
                                              f"&generation={old_generation}")
        bench.check("the paused source is captured and retained",
                    source.get("captured") is True and source.get("released") is False, json.dumps(source)[:200])

        transfers = _grid_ops(api, {"sets": [
            {"type": "inventory_transfer", "gridId": grid, "min": source_min, "inventoryIndex": 0, "itemId": item,
             "destinationGridId": grid, "destinationMin": destination_min, "destinationInventoryIndex": 0,
             "amountRaw": COUNTERS_UNIT_RAW} for _ in range(COUNTERS_TRANSFERS)]})
        bench.record("counters-transfers", transfers)
        accepted = [s for s in transfers["sets"]
                    if s.get("success") and (s.get("data") or {}).get("queued") and (s.get("data") or {}).get("accepted") is None]
        bench.check("all 19 inventory transfers were queued with asynchronous acceptance",
                    len(accepted) == COUNTERS_TRANSFERS, f"{len(accepted)} of {COUNTERS_TRANSFERS}")
        terminal = _grid_ops(api, {"sets": [{"type": "custom_name", "gridId": grid, "min": COUNTERS_TERMINAL_MIN,
                                             "value": COUNTERS_TERMINAL}]})
        bench.record("counters-terminal", terminal)
        bench.check("the terminal custom-name edit was queued", bool(terminal["sets"][0].get("success")))

        queued_after = queued_before

        def grown():
            nonlocal queued_after
            row = handover.paused(transfer)
            if row is None:
                raise ClusterError(f"transfer {transfer} is no longer paused")
            queued_after = row["queuedPacketCount"]
            return queued_after >= queued_before + COUNTERS_TRANSFERS + 1

        bench.check("the Gateway queued at least 20 packets during the pause", _poll(grown, HANDOVER_TIMEOUT) is not None,
                    f"grew by {queued_after - queued_before}")
        state = bench.cluster.get(f"/registry/handovers?id={transfer}")["State"]
        bench.check("the registry did not advance while the capture pause was held", state in (0, "Begun"), str(state))
        source = bench.cluster.node("node-1", f"/status?transferId={transfer}&partition={partition}"
                                              f"&generation={old_generation}")
        bench.check("the source capture was not released while paused",
                    source.get("captured") is True and source.get("released") is False, json.dumps(source)[:200])
        bench.record("counters-pause", {"transferId": transfer, "partition": partition, "registryState": "Begun",
                                        "sourceCaptured": True, "queuedBefore": queued_before,
                                        "queuedAfter": queued_after})

        released = handover.release(transfer)
        bench.record("counters-release", released)
        bench.check("the pause release endpoint released the transfer",
                    released.get("released") is True and released.get("transferId") == transfer, json.dumps(released))
        if not handover.finish():
            raise ClusterError("the released counters handover did not finish")
        bench.record("counters-handover", handover.result if handover.error is None else str(handover.error))
        bench.check("the released counters handover request succeeded", handover.error is None, str(handover.error)[:300])
    finally:
        handover.release_all()

    final = (None, None)

    def converged():
        nonlocal final
        try:
            final = tuple(amounts()[1])
        except Exception:  # noqa: BLE001 - the client may briefly have no view of the grid
            return None
        return final == (COUNTERS_TOTAL_RAW - COUNTERS_TRANSFERS * COUNTERS_UNIT_RAW, COUNTERS_TRANSFERS * COUNTERS_UNIT_RAW)

    bench.check("the final inventory amounts are exactly 45000000/19000000", _poll(converged, HANDOVER_TIMEOUT) is not None,
                f"{final[0]}/{final[1]}")
    bench.check("the final inventory total is conserved",
                None not in final and sum(final) == COUNTERS_TOTAL_RAW, str(final))

    # Double window: the terminal block's name replicates on its own cadence after the adopt
    # re-establishes the grid replicable, and it converged slower than the cargo reads once under
    # sweep load (2026-07-29). On timeout, ask node-2 for its authoritative name so the failure
    # separates a client replication gap from a lost edit.
    seen = None

    def renamed():
        nonlocal seen
        seen = (_grid_ops(api, terminal_get)["gets"][0].get("data") or {}).get("customName")
        return seen == COUNTERS_TERMINAL

    if _poll(renamed, HANDOVER_TIMEOUT * 2) is not None:
        bench.check("the terminal custom-name edit arrived after the handover", True)
    else:
        try:
            names = [row.get("name") for row in bench.cluster.node("node-2", f"/rich-test-state?grid={grid}").get("blockNames") or []]
        except ClusterError as error:
            names = str(error)[:200]
        census = [p["memberCount"] for p in _partitions(bench, "node-2") if p["id"] == partition]
        bench.check("the terminal custom-name edit arrived after the handover", False,
                    f"client saw {seen!r}, node-2 authoritative names {names}, census {census}")

    new_generation = old_generation + 1
    # Generation >= g+1, not == g+1, and node-2 OR retired: the handover advances exactly once, but a
    # post-handover merge or lease turnover legitimately bumps it again before this reads (the exact
    # pin failed the counters lane in the 2026-07-28 sweep) - same relax as HND-05.
    owners_after = []

    def advanced():
        nonlocal owners_after
        owners_after = _owners(bench)
        return any(row["Partition"] == partition and row["Generation"] >= new_generation
                   and (row["Node"] == "node-2" or row.get("Retired") is True) for row in owners_after)

    bench.check(f"ownership advanced to node-2 at generation >= {new_generation}",
                _poll(advanced, HANDOVER_TIMEOUT) is not None,
                json.dumps([row for row in owners_after if row["Partition"] == partition])[:300])
    # Member CONTAINMENT, not count equality: a post-handover merge legitimately adds members to the
    # transferred partition (count 3 -> 4 failed this on 2026-07-28). The transfer's promise is that
    # every source member arrives exactly once; extras are the partitioner's ordinary business, and
    # the inventory/terminal checks above already pin the content.
    census = []

    def contained():
        nonlocal census
        census = [member["id"] for p in _partitions(bench, "node-2") for member in p.get("members") or []]
        return all(census.count(member) == 1 for member in source_members)

    bench.check("every transferred member is live exactly once on node-2", _poll(contained, HANDOVER_TIMEOUT) is not None,
                f"expected {source_members}, census {census}"[:400])
    order = _states_in_order(bench, transfer, partition, COUNTERS_STATES)
    bench.check("every transaction state was logged, in order", _in_order(order), json.dumps(order))
    _client_survived(bench, "counters")
    bench.exercised(transfer=transfer, partition=partition, queuedPackets=queued_after - queued_before,
                    generation=[old_generation, new_generation], members=source_members)


p1_hnd_04.split_step = "to-counters"


# -- P1-HND-05 ------------------------------------------------------------------------------


def _replication(api) -> dict:
    return api._get("/v1/game/replication")


def _far(view: dict) -> int:
    return next((row["count"] for row in view.get("types") or [] if row.get("type") == FAR_BROADCASTER), 0)


def p1_hnd_05(bench) -> None:
    """The source prunes the client view during a paused handover; the view returns steady and intact."""
    api = _join_long_lease(bench)
    partition = next((p["id"] for p in _partitions(bench, "node-1") if CLIENT_ID in (p.get("clients") or [])), None)
    if partition is None:
        raise ClusterError("node-1 did not report the node-global client's partition")
    old_generation = next((row["Generation"] for row in _owners(bench)
                           if row["Partition"] == partition and row["Node"] == "node-1"), None)
    before = _replication(api)
    before_count, before_far = before["count"], _far(before)
    bench.record("nodeglobal-before", before)
    if not (before_count > 0 and before_far > 0):
        raise ClusterError(f"the source node-global client observation was empty ({before_count}/{before_far})")

    prune = rf"client-view prune client={CLIENT_ID} removed=([0-9]+)"
    handover = _PausedHandover(bench, partition)
    try:
        pause = handover.wait_pause(also=lambda: bool(_lines(bench, "node-1", prune)), label="node-global handover")
        transfer = pause["transferId"]
        removed = int(re.search(prune, _lines(bench, "node-1", prune)[-1]).group(1))
        bench.check("the source client view pruned non-transfer replicables", removed > 0, f"removed={removed}")

        # The blip is identity-based, not count-based: replicables still ramping in at the before
        # snapshot land during the pause, so the total count can legitimately GROW across the prune
        # (seen as 30 -> 46 and 48 -> 49 under load). The prune's evidence is before-ids leaving the
        # view plus the far-broadcaster drop; the pair-level assert below pins the identities.
        paused = {}
        pruned = []

        def blip():
            nonlocal paused, pruned
            paused = _replication(api)
            ids = set(paused["networkIds"])
            pruned = [network_id for network_id in before["networkIds"] if network_id not in ids]
            return pruned and _far(paused) < before_far

        blipped = _poll(blip, HANDOVER_TIMEOUT) is not None
        paused_count, paused_far = paused.get("count"), _far(paused)
        bench.record("nodeglobal-paused", paused)
        bench.check("the client view showed the accepted marker blip", blipped,
                    f"{before_count}/{before_far} -> {paused_count}/{paused_far}, pruned-from-view {len(pruned)}")
        # A fixed window on purpose: the assertion is that nothing comes back while the pause holds.
        time.sleep(3)
        stable = _replication(api)["count"]
        bench.check("the source did not recreate pruned replicables while the handover stayed paused",
                    stable == paused_count, f"{paused_count} -> {stable}")

        bench.record("nodeglobal-release", handover.release(transfer))
        if not handover.finish():
            raise ClusterError("the released node-global handover did not finish")
        bench.record("nodeglobal-handover", handover.result if handover.error is None else str(handover.error))
        bench.check("the released node-global handover request succeeded", handover.error is None,
                    str(handover.error)[:300])
    finally:
        handover.release_all()

    # Steady means STABLE, not "back under the before-cycle count": replicables still pending at the
    # before capture land during the cycle, so the after-count can legitimately end a few above it
    # (seen as 48 -> 51 under sweep load). Growth without identity is not a leak verdict either way -
    # the pair-level assert below is what catches source-only NetworkIds surviving.
    steady, last_after, after = 0, -1, {}
    for _ in range(HANDOVER_TIMEOUT):
        after = _replication(api)
        count = after["count"]
        if count > paused_count and count == last_after and _far(after) == before_far:
            steady += 1
            if steady >= 3:
                break
        else:
            steady = 0
        last_after = count
        time.sleep(1)
    after_count, after_far = after.get("count"), _far(after)
    bench.record("nodeglobal-after", after)
    bench.check("the client replication returned to a steady state", steady >= 3,
                f"{before_count}/{before_far} -> {paused_count}/{paused_far} -> {after_count}/{after_far}")
    # A pruned id returning bound to the SAME replicable (type + entity) is continuity, not a leak; a
    # pruned id rebound to a DIFFERENT replicable is client-view corruption - a client holding the old
    # mapping would bind the wrong object. The flat id-list comparison forbade both and could not say
    # which happened (finding 2026-07-27, pruned NetworkIds reappear).
    was = {entry["networkId"]: entry for entry in before.get("entries") or []}
    removed_ids = set(pruned)
    rebound = [{"networkId": now["networkId"], "was": was.get(now["networkId"]), "now": now}
               for now in after.get("entries") or [] if now["networkId"] in removed_ids
               and (was.get(now["networkId"]) is None or now.get("type") != was[now["networkId"]].get("type")
                    or now.get("entityId") != was[now["networkId"]].get("entityId"))]
    bench.check("no pruned NetworkId returned bound to a different replicable", not rebound, json.dumps(rebound)[:400])
    new_generation = (old_generation or 0) + 1
    owners_after = _owners(bench)
    # At least g+1, not exactly: a proximity merge landing after the handover legitimately bumps the
    # generation again (seen live: handover 1->2, then a merge to 3 inside the same window).
    # Per-operation advance-by-exactly-one is P1-HND-04's assertion, on the operation record.
    bench.check(f"ownership landed on node-2 at generation >= {new_generation}",
                old_generation is not None and any(row["Partition"] == partition and row["Node"] == "node-2"
                                                   and row["Generation"] >= new_generation for row in owners_after),
                json.dumps([row for row in owners_after if row["Partition"] == partition])[:300])
    _client_survived(bench, "node-global")
    bench.exercised(transfer=transfer, partition=partition, pruned=removed, prunedFromView=len(pruned),
                    blip=[before_count, before_far, paused_count, paused_far],
                    steady=[after_count, after_far], generation=new_generation)


SCENARIOS_SAVES = {
    "P1-SAV-03": (p1_sav_03, "adaptive save clock, ladder caps and a bounded save queue", "5"),
    "P1-SAV-09": (p1_sav_09, "replica fetch, resume and holder quarantine", "4"),
    "P1-SAV-08": (p1_sav_08, "keep-N retention and tombstones", "3"),
    "P1-SAV-06": (p1_sav_06, "handover tee survives a source kill", "4"),
    "P1-HND-04": (p1_hnd_04, "counter continuity across a paused handover", "3"),
    "P1-HND-05": (p1_hnd_05, "client-view prune and re-seed", "3"),
}
