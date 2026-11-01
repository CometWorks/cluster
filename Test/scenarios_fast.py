"""T-0292 - fast grid groups: a group faster than fast.enterMps (1000 m/s, measured from positions over
time) gets a partition of its own that merges with nothing, is never migrated or pulled and wakes
nothing, until it slows down; then normal membership resumes.

The flights are driven by the test plugin's /fast-test-drive (FastTestHarness): mode=teleport moves the
grid ahead every frame with zero physics velocity, the shape of the STC 4.0 warp drive; mode=velocity
sets a real physics velocity after /fast-test-speed-limit raised the environment's ship limit, which is
what speed mods do through the same definition.
"""
from __future__ import annotations

import re
import time
from datetime import datetime

from cluster import ClusterError, wait_until
from scenarios_core import _handover_url, _lines, _nodes, _owners, _partitions
from scenarios_ext import assert_node_sections
from scenarios_offline import _put_config

SPEED = 3000.0           # m/s, the warp case (x3 the threshold)
FLIGHT_S = 20.0          # 60 km
OBSTACLE_STEP = 10_000.0
LATERAL = 200.0          # every obstacle 200 m off the flight line: well inside the 3 km merge distance


def _spawn(bench, node: str, x: float, y: float = 0.0, z: float = 0.0, static: bool = True) -> int:
    grid = bench.cluster.node(node, f"/trash-test-spawn?x={x}&y={y}&z={z}&static={'true' if static else 'false'}&blocks=27",
                              "POST")["grid"]
    if not grid:
        raise ClusterError(f"spawn on {node} at {x} returned no grid")
    return grid


def _where(bench, entity: int) -> tuple[str | None, int | None, dict | None]:
    """(node, partition id, partition report row) of the partition holding ENTITY, from the node reports."""
    for node in ("node-1", "node-2"):
        try:
            rows = _partitions(bench, node)
        except ClusterError:
            continue
        for row in rows:
            if any(member.get("id") == entity for member in row.get("members") or []):
                return node, row["id"], row
    return None, None, None


def _poll(predicate, timeout: float):
    """wait_until without the raise: a missing transition is a failed CHECK, and the scenario goes on
    so the rest of the flight (merges, partition ids) is still measured - the red on a build without
    the fast state must show WHAT happens to the flyer, not stop at the first missing line."""
    try:
        return wait_until(predicate, timeout, "fast-test condition")
    except ClusterError:
        return None


def _enter_lines(bench, entity: int, kind: str) -> list[str]:
    return [line for node in ("node-1", "node-2") for line in _lines(bench, node, rf"fast_member {kind} entity={entity} ")]


def p1_fast_01(bench) -> None:
    _fast_flight(bench, fast_on=True)


def p1_fast_01_red(bench) -> None:
    """The earned red of P1-FAST-01: the same build and fixture with the fast state switched off
    (fast.enterMps far above the flight). PASSes only when the flight then DOES what the green
    forbids - the flyer's partition merges or changes id while it passes the obstacles - which
    proves the fixture creates the condition the fast state removes."""
    _fast_flight(bench, fast_on=False)


def _green_flight_checks(bench, samples: list, fast_samples: list) -> None:
    bench.check("the flyer's partition is reported fast during the flight", len(fast_samples) >= len(samples) - 4,
                f"{len(fast_samples)} of {len(samples)} samples fast")
    bench.check("while fast the flyer is alone in its partition", all(s["members"] == 1 for s in fast_samples),
                str([s for s in fast_samples if s["members"] != 1][:5]))
    bench.check("the fast partition keeps one id and one owner for the whole flight",
                len({(s["node"], s["partition"]) for s in fast_samples}) == 1, str({(s["node"], s["partition"]) for s in fast_samples}))
    speeds = [s["fastSpeed"] for s in fast_samples if s["fastSpeed"]]
    bench.check("the measured speed is the driven speed (position over time, +-15%)",
                bool(speeds) and all(abs(v - SPEED) / SPEED < 0.15 for v in speeds), str(speeds[:10]))


def _fast_flight(bench, fast_on: bool) -> None:
    bench.start_cluster()
    bench.quiesce()
    if not fast_on:
        for node in ("node-1", "node-2"):
            bench.record(f"config-{node}", _put_config(bench, node, {"fast.enterMps": "1000000"}))
    base = (700_000.0, 0.0, 0.0)
    flyer = _spawn(bench, "node-1", base[0], static=False)
    # Obstacles along the line, alternating nodes, so a merge (same node) and a co-location handover
    # (other node) are both on offer to the flyer as it passes.
    obstacles = {}
    for i in range(1, int(SPEED * FLIGHT_S / OBSTACLE_STEP) + 1):
        node = "node-1" if i % 2 else "node-2"
        obstacles[_spawn(bench, node, base[0] + i * OBSTACLE_STEP, LATERAL)] = node
    # The landing obstacle, 500 m past the end of the flight: the flyer must merge with it only after
    # it slows down.
    landing = _spawn(bench, "node-1", base[0] + SPEED * FLIGHT_S + 500, LATERAL)
    bench.record("fixture", {"flyer": flyer, "obstacles": obstacles, "landing": landing})
    time.sleep(15)   # every layout past the 10 s merge maturity before the flight

    # CONTROL 1 - one teleport hop is a single speed spike and must never enter the fast state.
    bench.cluster.node("node-1", f"/test-position?entity={flyer}&relativeTo={flyer}&z=-50000", "POST")
    time.sleep(8)
    bench.cluster.node("node-1", f"/test-position?entity={flyer}&relativeTo={flyer}&z=50000", "POST")
    time.sleep(8)
    bench.check("a single 50 km teleport hop does not enter the fast state (dwell 2 samples)",
                not _enter_lines(bench, flyer, "enter"), str(_enter_lines(bench, flyer, "enter")))

    # CONTROL 2 - below the threshold: 900 m/s for 10 s never enters.
    bench.cluster.node("node-1", f"/fast-test-drive?entity={flyer}&vx=0&vy=0&vz=900&seconds=10&mode=teleport", "POST")
    time.sleep(13)
    bench.cluster.node("node-1", f"/test-position?entity={flyer}&relativeTo={flyer}&z=-9000", "POST")
    bench.check("900 m/s (below fast.enterMps 1000) does not enter the fast state",
                not _enter_lines(bench, flyer, "enter"), str(_enter_lines(bench, flyer, "enter")))
    time.sleep(8)

    # THE WARP FLIGHT - 3 km/s by per-frame teleport along the obstacle line.
    node0, partition0, _ = _where(bench, flyer)
    merges0 = len(_lines(bench, "node-1", r"topology Merge committed")) + len(_lines(bench, "node-2", r"topology Merge committed"))
    bench.cluster.node("node-1", f"/fast-test-drive?entity={flyer}&vx={SPEED}&vy=0&vz=0&seconds={FLIGHT_S}&mode=teleport", "POST")
    entered = _poll(lambda: _enter_lines(bench, flyer, "enter") or None, 10 if fast_on else 3)
    if fast_on:
        bench.check("the flyer enters the fast state within 10 s of reaching 3 km/s", bool(entered), str(entered))
    samples = []
    t0 = time.time()
    while time.time() - t0 < FLIGHT_S - 2:
        node, partition, row = _where(bench, flyer)
        samples.append({"t": round(time.time() - t0, 1), "node": node, "partition": partition,
                        "members": row and row.get("memberCount"), "fast": row and row.get("fast"),
                        "fastSpeed": row and row.get("fastSpeed")})
        time.sleep(1)
    bench.record("flight-samples", samples)
    fast_samples = [s for s in samples if s["fast"]]
    if fast_on:
        _green_flight_checks(bench, samples, fast_samples)
    diag = bench.cluster.get("/diag?registry=summary")
    bench.record("diag-fast", {"fastPartitions": diag["registry"].get("fastPartitions"),
                               "fastSuppressed": diag["registry"].get("fastSuppressed")})
    time.sleep(4)
    merges1 = len(_lines(bench, "node-1", r"topology Merge committed")) + len(_lines(bench, "node-2", r"topology Merge committed"))
    fast_partition = fast_samples[0]["partition"] if fast_samples else None
    # Every partition that held the flyer during the flight, fast or not: on a build without the fast
    # state the check must still see the flyer's merges (the red), not pass on an empty id list.
    flight_partitions = sorted({s["partition"] for s in samples if s["partition"] is not None})
    flyer_merges = [
        line for partition in flight_partitions for node in ("node-1", "node-2")
        for line in _lines(bench, node, rf"topology Merge committed inputs=(?:\d+,)*{partition}(?:,|\s)")]
    if fast_on:
        # SE1-0084: the flyer leaves the fast state within ticks of the drive's end, so the landing merge can
        # land before this check runs; the flight is over at the exit line.
        exits = [t for node in ("node-1", "node-2") for t, _ in _stamped(bench, node, rf"fast_member exit entity={flyer} ")]
        if exits:
            landed_at = min(exits)
            flyer_merges = [
                line for line in flyer_merges
                if not ((m := STAMP.match(line)) and datetime.strptime(m.group(1)[:23], "%Y-%m-%d %H:%M:%S.%f") >= landed_at)]
        bench.check("the flyer was located in every flight sample", len(flight_partitions) > 0 and all(s["partition"] for s in samples),
                    str([s for s in samples if not s["partition"]][:5]))
        # From 3 s on: the fast split gives the flyer a new id on the evaluate after the enter line.
        settled = [s for s in samples if s["t"] >= 3]
        bench.check("the flyer kept one partition (one id, one owner) from 3 s into the flight",
                    len({(s["node"], s["partition"]) for s in settled}) == 1, str({(s["node"], s["partition"]) for s in settled}))
        bench.check("no merge involved the flyer's partition during the flight", not flyer_merges, str(flyer_merges[:5]))
    bench.record("merge-counts", {"before": merges0, "after": merges1, "flyerMerges": flyer_merges})
    if not fast_on:
        partitions = {(s["node"], s["partition"]) for s in samples}
        bench.check("fast state off: no fast_member enter line for the flyer", not _enter_lines(bench, flyer, "enter"),
                    str(_enter_lines(bench, flyer, "enter")))
        bench.check("fast state off: the flyer's partition merged or changed id during the flight (the condition exists)",
                    bool(flyer_merges) or len(partitions) > 1, f"merges={flyer_merges[:5]} partitions={sorted(map(str, partitions))}")
        bench.exercised(flyer=flyer, warp=f"{SPEED:.0f} m/s x {FLIGHT_S:.0f} s teleport", obstacles=len(obstacles),
                        fastOff=True, flyerMerges=len(flyer_merges), partitions=len(partitions))
        return

    # SLOW-DOWN - the drive has ended next to the landing obstacle: exit, then normal merge.
    exited = _poll(lambda: _enter_lines(bench, flyer, "exit") or None, 20)
    bench.check("the flyer leaves the fast state after it stops (exit dwell)", bool(exited), str(exited))
    landed = _poll(lambda: (lambda a, b: a if a and a == b else None)(_where(bench, flyer)[1], _where(bench, landing)[1]), 60)
    bench.check("after slowing down the flyer merges normally with the landing obstacle", landed is not None, str(landed))
    bench.check("the flyer survived the flight with the same EntityId", _where(bench, flyer)[0] is not None,
                str(_where(bench, flyer)))

    # PHYSICS VELOCITY - a real 2 km/s physics velocity (speed limit raised as a speed mod does).
    # The landing merge may have moved the flyer to the other node: drive it where it lives now.
    owner = _where(bench, flyer)[0] or "node-1"
    limits = {node: bench.cluster.node(node, "/fast-test-speed-limit?mps=3000", "POST") for node in ("node-1", "node-2")}
    bench.record("speed-limit", {"owner": owner, **limits})
    enters_before = len(_enter_lines(bench, flyer, "enter"))
    bench.cluster.node(owner, f"/fast-test-drive?entity={flyer}&vx=0&vy=2000&vz=0&seconds=10&mode=velocity", "POST")
    physics = _poll(lambda: len(_enter_lines(bench, flyer, "enter")) > enters_before or None, 10)
    bench.check("a 2 km/s physics velocity enters the fast state too", bool(physics),
                str(_enter_lines(bench, flyer, "enter")))
    time.sleep(12)
    for node in ("node-1", "node-2"):
        bench.cluster.node(node, "/fast-test-speed-limit?mps=100", "POST")
    bench.exercised(flyer=flyer, warp=f"{SPEED:.0f} m/s x {FLIGHT_S:.0f} s teleport", obstacles=len(obstacles),
                    physics="2000 m/s x 10 s", fastPartition=fast_partition)


def p1_fast_00(bench) -> None:
    """Tier 1: the node's T-0292 and SE1-0084 self-test sections plus every membership section the fast pass touches."""
    assert_node_sections(bench, "T-0292 / SE1-0084 rules hold (tracker dwell/hysteresis/margin, tick detector, flap monitor, fast split/join, no adopt)",
                         "fast-motion-tracker", "fast-tick-detector", "fast-flap-monitor", "membership-fast", "membership-determinism", "membership-hysteresis",
                         "membership-atomicity", "membership-conga", "membership-pinning", "membership-revert",
                         "membership-held")


def _sync_template(bench, sync: int):
    """Cluster-Simple with SyncDistance SYNC (both settings files)."""
    import re
    import shutil
    from pathlib import Path

    import bench as benchmod
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / f"Cluster-Simple-sync{sync}"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        new, n = re.subn(r"<SyncDistance>\d+</SyncDistance>", f"<SyncDistance>{sync}</SyncDistance>", path.read_text(), count=1)
        if n != 1:
            raise ClusterError(f"{name}: no <SyncDistance> to replace")
        path.write_text(new)
    return target


SLOW_MPS, FAST_MPS = 1500.0, 3000.0   # between the two thresholds (1000 / 2000), and above both


def _threshold_flights(bench, sync: int) -> dict:
    """Boot on a world with SyncDistance SYNC; fly the same grid at SLOW_MPS, then FAST_MPS (teleport
    drive, 10 s each, nothing nearby). Returns what the nodes logged."""
    bench.cluster.config.world_template = _sync_template(bench, sync)
    # The registry admits only nodes whose world SyncDistance equals the cluster's (CLUSTER_SYNC_DISTANCE),
    # and the projectile cap follows it: the whole cluster runs at SYNC, as a real non-default world would.
    bench.cluster.config.sync_distance = sync
    bench.start_cluster()
    bench.quiesce()
    flyer = _spawn(bench, "node-1", 900_000.0, static=False)
    time.sleep(5)
    threshold = [line for node in ("node-1", "node-2") for line in _lines(bench, node, r"fast_threshold ")]
    bench.cluster.node("node-1", f"/fast-test-drive?entity={flyer}&vx={SLOW_MPS}&vy=0&vz=0&seconds=10&mode=teleport", "POST")
    time.sleep(14)
    slow_enter = _enter_lines(bench, flyer, "enter")
    _poll(lambda: _enter_lines(bench, flyer, "exit") or None, 15 if slow_enter else 1)
    bench.cluster.node("node-1", f"/fast-test-drive?entity={flyer}&vx=0&vy={FAST_MPS}&vz=0&seconds=10&mode=teleport", "POST")
    fast_enter = _poll(lambda: _enter_lines(bench, flyer, "enter")[len(slow_enter):] or None, 12) or []
    out = {"sync": sync, "flyer": flyer, "threshold": threshold, "slowEnter": slow_enter, "fastEnter": fast_enter}
    bench.record(f"threshold-flights-sync{sync}", out)
    return out


def p1_fast_02(bench) -> None:
    """The fast threshold follows the sync distance in use: at SyncDistance 6000 it is 2000 m/s, logged
    with its source; 1500 m/s does not enter, 3000 m/s does."""
    r = _threshold_flights(bench, 6000)
    bench.check("every node logged fast_threshold enterMps=2000 derived from the sync distance 6000",
                len(r["threshold"]) >= 2 and all("enterMps=2000 " in line and "6000 m / 3" in line for line in r["threshold"]),
                str(r["threshold"][:4]))
    bench.check("the exit threshold scales with it (0.8 x 2000)", all("exitMps=1600 " in line for line in r["threshold"]) and r["threshold"],
                str(r["threshold"][:2]))
    bench.check(f"{SLOW_MPS:.0f} m/s (under 2000) does not enter the fast state", not r["slowEnter"], str(r["slowEnter"]))
    bench.check(f"{FAST_MPS:.0f} m/s enters it, against the 2000 m/s threshold (the line names T, the margin is +1%)", bool(r["fastEnter"])
                and all("enter > 2000 " in line for line in r["fastEnter"]), str(r["fastEnter"]))
    bench.exercised(sync=6000, flights=[SLOW_MPS, FAST_MPS])


def p1_fast_02_red(bench) -> None:
    """The earned red of P1-FAST-02: the same flights at the default SyncDistance 3000 (1000 m/s). The
    1500 m/s flight DOES enter here, so its not entering at 6000 is the sync distance's doing."""
    r = _threshold_flights(bench, 3000)
    bench.check("every node logged fast_threshold enterMps=1000 derived from the sync distance 3000",
                len(r["threshold"]) >= 2 and all("enterMps=1000 " in line and "3000 m / 3" in line for line in r["threshold"]),
                str(r["threshold"][:4]))
    bench.check(f"{SLOW_MPS:.0f} m/s enters the fast state at the 1000 m/s threshold (the condition exists)",
                bool(r["slowEnter"]), str(r["slowEnter"]))
    bench.exercised(sync=3000, flights=[SLOW_MPS, FAST_MPS])


# ---- T-0292 (Viktor via CH 2026-09-29 10:38): a fast partition is never lost. It still moves on node
# ROTATION and on CRASH RECOVERY; only balancer and proximity moves and merges are suppressed. ----

MOVE_MPS = 2000.0        # physics velocity: it survives the handover and the save (a speed mod's flight)
MOVE_S = 60.0
NEIGHBOUR_STEP = 4000.0  # node-2 grids along the whole path: the flyer is always within 2 km of one
NEIGHBOUR_START = 20_000.0


def _copies(bench, entity: int) -> list[tuple[str, int]]:
    """Every (node, partition) whose report lists ENTITY: exactly one means no loss and no duplicate."""
    out = []
    for node in ("node-1", "node-2"):
        try:
            rows = _partitions(bench, node)
        except ClusterError:
            continue
        out += [(node, row["id"]) for row in rows if any(m.get("id") == entity for m in row.get("members") or [])]
    return out


def _moving_fixture(bench) -> dict:
    """A 27-block flyer on node-1 at MOVE_MPS by physics velocity, fast, with node-2 neighbours along its path."""
    bench.start_cluster()
    bench.quiesce()
    limits = {node: bench.cluster.node(node, "/fast-test-speed-limit?mps=3000", "POST") for node in ("node-1", "node-2")}
    base = (1_100_000.0, 0.0, 0.0)
    flyer = _spawn(bench, "node-1", base[0], static=False)
    # The neighbours start 20 km down the path: nearer, co-location hands the still-slow flyer to node-2 before
    # the drive (bench 20260929-085156Z). By 20 km (10 s at 2 km/s) it is fast, and it is always within
    # 2 km of one of them from there on - where the handover and the recovery happen.
    neighbours = [_spawn(bench, "node-2", base[0] + 200.0, NEIGHBOUR_START + i * NEIGHBOUR_STEP)
                  for i in range(int(MOVE_MPS * MOVE_S / NEIGHBOUR_STEP) + 1)]
    time.sleep(12)
    owner, _, row0 = _where(bench, flyer)
    if owner != "node-1":
        raise ClusterError(f"fixture: the flyer is on {owner}, not node-1, before the drive")
    pcu = row0 and row0.get("pcu")
    bench.cluster.node("node-1", f"/fast-test-drive?entity={flyer}&vx=0&vy={MOVE_MPS}&vz=0&seconds={MOVE_S}&mode=velocity", "POST")
    entered = _poll(lambda: _enter_lines(bench, flyer, "enter") or None, 12)
    # The move happens among the neighbours, not before them.
    time.sleep(max(0.0, NEIGHBOUR_START / MOVE_MPS - 6))
    bench.check("the flyer enters the fast state on node-1 at 2 km/s (physics velocity)", bool(entered), str(entered))
    node, partition, row = _poll(lambda: (lambda w: w if w[2] and w[2].get("fast") and w[2].get("memberCount") == 1 else None)(
        _where(bench, flyer)), 10) or (None, None, None)
    bench.check("before the move the flyer is alone in a fast node-1 partition", node == "node-1" and row is not None,
                f"{node} {partition} {row and {k: row.get(k) for k in ('fast', 'memberCount', 'fastSpeed')}}")
    fixture = {"flyer": flyer, "pcu": pcu, "partition": partition, "neighbours": len(neighbours), "limits": limits}
    bench.record("moving-fixture", fixture)
    return fixture


def _after_move_checks(bench, fixture: dict, how: str, window_s: float = 15.0) -> list:
    """Sample the flyer on node-2 for WINDOW_S: one copy, intact, fast, alone, not merged."""
    flyer = fixture["flyer"]
    samples = []
    t0 = time.time()
    while time.time() - t0 < window_s:
        node, partition, row = _where(bench, flyer)
        samples.append({"t": round(time.time() - t0, 1), "copies": _copies(bench, flyer), "node": node, "partition": partition,
                        "members": row and row.get("memberCount"), "fast": row and row.get("fast"),
                        "fastSpeed": row and row.get("fastSpeed"), "pcu": row and row.get("pcu")})
        time.sleep(1)
    bench.record(f"after-{how}", samples)
    partitions = sorted({s["partition"] for s in samples if s["partition"] is not None})
    merges = [line for p in partitions for line in _lines(bench, "node-2", rf"topology Merge committed inputs=(?:\d+,)*{p}(?:,|\s)")]
    bench.check(f"after the {how} the flyer exists exactly once, on node-2, in every sample",
                _once_on_node2(samples), str([s["copies"] for s in samples][:6]))
    bench.check(f"after the {how} the flyer is intact (same EntityId, PCU {fixture['pcu']})",
                all(s["pcu"] == fixture["pcu"] for s in samples if s["members"] == 1) and any(s["members"] == 1 for s in samples),
                str([(s["members"], s["pcu"]) for s in samples][:6]))
    return samples, merges


def _once_on_node2(samples) -> bool:
    return bool(samples) and all(len(s["copies"]) == 1 and s["copies"][0][0] == "node-2" for s in samples)


def _move_verdict(bench, fixture, samples, merges, how: str, red: bool, first_fast_s: float):
    fast = [s for s in samples if s["fast"]]
    alone = all(s["members"] == 1 for s in samples)
    if red:
        bench.check(f"old build: in the first seconds after the {how} the flyer is NOT fast or IS merged on node-2 (the condition exists)",
                    bool(merges) or not alone or not samples[0]["fast"], f"merges={merges[:3]} first={samples[0]}")
        return
    bench.check(f"after the {how} the flyer is classified fast on node-2 within {first_fast_s:.0f} s and stays fast",
                bool(fast) and fast[0]["t"] <= first_fast_s and len(fast) >= len(samples) - first_fast_s - 1,
                str([(s["t"], s["fast"], s["fastSpeed"]) for s in samples]))
    bench.check(f"after the {how} the flyer is alone in its partition in every sample (never merged)", alone,
                str([s for s in samples if s["members"] != 1][:4]))
    bench.check(f"no merge involved the flyer's node-2 partition after the {how}", not merges, str(merges[:4]))


def p1_fast_03(bench, red: bool = False) -> None:
    """A fast partition moves on node ROTATION: the rotator's clientless handover of the owner node's partitions
    carries it to node-2, where it is fast from its first pass (state carried, 'fast_member carried'), exactly
    one copy, intact, never merged with the node-2 neighbours along its path. The red is the same fixture on a
    build without the carried state: the flyer arrives mature and not yet fast, and merges or reads slow."""
    fixture = _moving_fixture(bench)
    flyer, partition = fixture["flyer"], fixture["partition"]
    response = bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=180)
    bench.record("rotation-handover", response)
    moved = _poll(lambda: (lambda c: c if len(c) == 1 and c[0][0] == "node-2" else None)(_copies(bench, flyer)), 60)
    bench.check("the rotation handover moved the fast partition to node-2 (not skipped, not waited on)", bool(moved),
                f"{response} copies={_copies(bench, flyer)}")
    if not red:
        carried = _lines(bench, "node-2", rf"fast_member carried entity={flyer} ")
        bench.check("node-2 took the carried fast state at commit", bool(carried), str(carried))
    samples, merges = _after_move_checks(bench, fixture, "rotation handover")
    _move_verdict(bench, fixture, samples, merges, "rotation handover", red, 1)
    bench.exercised(flyer=flyer, how="clientless rotation handover", speed=MOVE_MPS, red=red)


def p1_fast_03_lost(bench) -> None:
    """The earned red of the no-loss check (P1-FAST-03/04): after the same rotation handover the flyer is
    CLOSED on node-2, i.e. the partition is lost. The 'exactly once' check P1-FAST-03 and P1-FAST-04 use must
    then fail; this scenario PASSes only when it does."""
    fixture = _moving_fixture(bench)
    flyer = fixture["flyer"]
    bench.cluster.post(_handover_url(bench, fixture["partition"], clientless=True), timeout=180)
    moved = _poll(lambda: (lambda c: c if len(c) == 1 and c[0][0] == "node-2" else None)(_copies(bench, flyer)), 60)
    bench.check("the fixture: the flyer reached node-2 once", bool(moved), str(_copies(bench, flyer)))
    bench.record("inject-loss", bench.cluster.node("node-2", f"/test-close-entity?entity={flyer}", "POST"))
    time.sleep(3)
    samples = [{"copies": _copies(bench, flyer)} for _ in range(3) if time.sleep(1) is None]
    bench.record("after-loss", samples)
    bench.check("with the partition lost, the 'exactly once on node-2' check fails (it would catch a loss)",
                not _once_on_node2(samples), str([s["copies"] for s in samples]))
    bench.exercised(flyer=flyer, injected="close on node-2 after the handover")


def p1_fast_04(bench) -> None:
    """A fast partition survives a CRASH of its owner: its saves stay current while fast (a save of the fast
    partition within the save interval before the SIGKILL), node-2 recovers it from that save, exactly one
    copy, intact, re-derived fast from position over time within the membership maturity (10 s), and never
    merged with the node-2 neighbours along its path.

    The kill waits for that save to reach node-2 as well (SE1-0101). Replication is asynchronous: node-2 copies a
    new save within about a second, and a save whose only holder is the killed node is not used by recovery, so a
    kill inside that second recovers the save before it, taken before the drive started. Losing a save that had
    no second copy yet is within the crash rollback bound (Docs/Gateway/StateDurability.md); this scenario is about
    the fast state surviving recovery, not about the replication lag."""
    fixture = _moving_fixture(bench)
    flyer = fixture["flyer"]
    partition = _where(bench, flyer)[1]
    pattern = rf"partition save registered: partition {partition} generation ([0-9]+) sequence [0-9]+"
    before = len(_lines(bench, "node-1", pattern))
    _poll(lambda: len(_lines(bench, "node-1", pattern)) > before or None, 70)
    saves = _lines(bench, "node-1", pattern)
    bench.check("the fast partition was saved while fast (within 70 s)", len(saves) > before, f"{len(saves)} vs {before}")
    generation = int(re.search(pattern, saves[-1]).group(1)) if saves else 0
    replicated = _poll(lambda: (lambda row: row if "node-2" in ((row or {}).get("LocationHolders") or {}).values() else None)(
        bench.cluster.get(f"/registry/saves/newest?partition={partition}")), 30)
    newest = replicated or bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    bench.check("the newest save of the fast partition has a second holder (node-2) before the kill",
                bool(replicated), str(newest.get("LocationHolders")))
    state = bench.cluster.node("node-1", "/partitions")
    exposure = state["evaluatedAt"] - newest["ClusterTime"]
    bench.record("pre-crash", {"partition": partition, "generation": generation, "newest": newest, "exposureMs": exposure})
    bench.check("the newest save of the fast partition is at most one save interval (60 s) old at the kill",
                0 <= exposure <= 60_000, f"{exposure} ms")
    bench.cluster.kill(slot="node-1")

    def moved():
        bench.cluster.post("/registry/leases/expire")
        row = next((r for r in _owners(bench) if r["Partition"] == partition), None)
        return row if row and row.get("Node") != "node-1" and row["Generation"] > generation else None

    owner = _poll(moved, 180)
    bench.check("ownership of the fast partition moved off the dead node", bool(owner), str(owner))
    loaded = _poll(lambda: _lines(bench, "node-2", rf"recovery loaded partition {partition} generation ") or None, 180)
    bench.check("node-2 recovered the fast partition from its save", bool(loaded), str(loaded))
    samples, merges = _after_move_checks(bench, fixture, "crash recovery", window_s=20.0)
    _move_verdict(bench, fixture, samples, merges, "crash recovery", False, 8)
    bench.check("the recovered flyer still moves at the saved velocity (re-derived, +-15%)",
                any(s["fastSpeed"] and abs(s["fastSpeed"] - MOVE_MPS) / MOVE_MPS < 0.15 for s in samples),
                str([s["fastSpeed"] for s in samples]))
    bench.exercised(flyer=flyer, how="SIGKILL of the owner + recovery load", speed=MOVE_MPS, exposureMs=exposure)


# ---- SE1-0084: the tick detector. A ship that jumps to warp speed is fast within a few ticks, not after
# two 0.9 s samples, and the Gateway hears of it at once (POST /registry/report-now). ----

STAMP = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)")
TICK_MPS = 3000.0
TICK_FLIGHT_S = 4.0
# Placed so the flyer comes within the 3 km merge distance (sync distance) of each one in the first second:
# the merge obstacle from 0.25 s, the offline one (the Gateway's wake distance for a clientless partition
# is the sync distance too) from 0.9 s. The samples put the flyer in the fast state 1.8 to 2.7 s in.
MERGE_AT = (3000.0, -2000.0)
WAKE_AT = (5000.0, 2000.0)


def _stamped(bench, node: str, pattern: str) -> list[tuple[datetime, str]]:
    """(time, line) of a node's stdout lines matching PATTERN; the node runs on this host, on this clock."""
    out = []
    for line in _lines(bench, node, pattern):
        match = STAMP.match(line)
        if match:
            out.append((datetime.strptime(match.group(1)[:23], "%Y-%m-%d %H:%M:%S.%f"), line))
    return out


def _offline_members(bench) -> set[int]:
    table = bench.cluster.get("/registry/partitions/offline")
    return {member for row in table.get("Partitions", []) for member in row.get("Members") or []}


def p1_fast_05(bench, red: bool = False) -> None:
    """SE1-0084: a 3 km/s teleport flight is fast within a fraction of a second of its start (the tick
    detector, `by=tick`), so it neither merges with a static obstacle it passes 2 km off the line in the
    first second nor wakes an offline one 2 km off the line a little further, and it leaves the fast state
    within a fraction of a second of the stop. The red switches the tick detector off (fast.tickDetector=
    false, the 0.9 s samples alone, as before SE1-0084) and passes only when the flyer then merges with or
    wakes an obstacle: the fixture creates the condition."""
    bench.start_cluster()
    bench.quiesce()
    if red:
        for node in ("node-1", "node-2"):
            bench.record(
                f"config-{node}",
                _put_config(bench, node, {"fast.tickDetector": "false"}),
            )
    base = 1_300_000.0
    flyer = _spawn(bench, "node-1", base, static=False)
    time.sleep(2)
    load_before = bench.cluster.node("node-1", "/diag")["load"]
    merge_obstacle = _spawn(bench, "node-1", base + MERGE_AT[0], MERGE_AT[1])
    time.sleep(2)
    load_after = bench.cluster.node("node-1", "/diag")["load"]
    bench.record("tick-members", {"beforeStatic": load_before, "afterStatic": load_after})
    bench.check(
        "the tick detector reads the flyer but not a static grid (diag fastTickMembers)",
        load_after["entityCount"] == load_before["entityCount"] + 1
        and load_after["fastTickMembers"] == load_before["fastTickMembers"]
        and load_after["fastTickMembers"] < load_after["entityCount"],
        f"before={load_before} after={load_after}",
    )
    wake_obstacle = _spawn(bench, "node-2", base + WAKE_AT[0], WAKE_AT[1])
    time.sleep(15)  # every layout past the 10 s merge maturity
    bench.record(
        "offline-config",
        _put_config(
            bench,
            "node-2",
            {
                "offline.enabled": "true",
                "offline.minOnlineSeconds": "0",
                "offline.graceSeconds": "0",
            },
        ),
    )
    went = _poll(lambda: wake_obstacle in _offline_members(bench) or None, 90)
    _put_config(bench, "node-2", {"offline.enabled": "false"})  # keep it offline unless something wakes it
    bench.check(
        "the wake obstacle went offline before the flight",
        bool(went),
        str(_offline_members(bench)),
    )
    offline_partition = next(
        (
            row["Partition"]
            for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])
            if wake_obstacle in (row.get("Members") or [])
        ),
        None,
    )
    _, merge_partition, _ = _where(bench, merge_obstacle)
    merge_pattern = rf"topology Merge committed inputs=(?:\d+,)*{merge_partition}(?:,|\s)"
    merges_before = len(_lines(bench, "node-1", merge_pattern))
    wake_pattern = rf"Offline wake: partition {offline_partition} "
    wakes_before = len(_lines(bench, "gateway", wake_pattern))
    enters_before = len(_stamped(bench, "node-1", rf"fast_member enter entity={flyer} "))
    bench.record(
        "fixture",
        {
            "flyer": flyer,
            "mergeObstacle": merge_obstacle,
            "mergePartition": merge_partition,
            "wakeObstacle": wake_obstacle,
            "offlinePartition": offline_partition,
        },
    )

    started = datetime.now()
    bench.cluster.node(
        "node-1",
        f"/fast-test-drive?entity={flyer}&vx={TICK_MPS}&vy=0&vz=0&seconds=60&mode=teleport",
        "POST",
    )
    time.sleep(TICK_FLIGHT_S)
    bench.cluster.node("node-1", f"/fast-test-stop?entity={flyer}", "POST")
    stopped = datetime.now()
    time.sleep(6)

    enters = _stamped(bench, "node-1", rf"fast_member enter entity={flyer} ")[enters_before:]
    exits = [x for x in _stamped(bench, "node-1", rf"fast_member exit entity={flyer} ") if x[0] >= started]
    merges = _lines(bench, "node-1", merge_pattern)[merges_before:]
    wakes = _lines(bench, "gateway", wake_pattern)[wakes_before:]
    enter_s = (enters[0][0] - started).total_seconds() if enters else None
    in_flight = [line for t, line in exits if t < stopped]
    after_stop = [t for t, _ in exits if t >= stopped]
    exit_s = (after_stop[0] - stopped).total_seconds() if after_stop else None
    diag = bench.cluster.get("/diag?registry=summary")["registry"]
    bench.record(
        "flight",
        {
            "started": str(started),
            "stopped": str(stopped),
            "enter": enters,
            "exit": exits,
            "enterSeconds": enter_s,
            "exitSeconds": exit_s,
            "merges": merges,
            "wakes": wakes,
            "fastSuppressed": diag.get("fastSuppressed"),
        },
    )
    if red:
        bench.check(
            "tick detector off: the flyer merged with or woke an obstacle before it was fast (the condition exists)",
            bool(merges or wakes),
            f"merges={merges[:3]} wakes={wakes[:3]} enter={enter_s}",
        )
        bench.exercised(
            flyer=flyer,
            tickDetector=False,
            enterSeconds=enter_s,
            merges=len(merges),
            wakes=len(wakes),
        )
        return
    bench.check(
        "the flyer enters the fast state within 0.5 s of the drive request, by the tick detector",
        enter_s is not None and enter_s < 0.5 and "by=tick" in enters[0][1],
        f"{enter_s} {enters[:1]}",
    )
    bench.check(
        "the static obstacle 2 km off the line is not merged with the flyer",
        not merges,
        str(merges[:3]),
    )
    bench.check(
        "the offline obstacle 2 km off the line is not woken",
        not wakes and wake_obstacle in _offline_members(bench),
        str(wakes[:3]),
    )
    bench.check("the flyer stays fast for the whole flight", not in_flight, str(in_flight[:3]))
    bench.check(
        "the flyer leaves the fast state within 0.5 s of the stop, by the tick detector",
        exit_s is not None and exit_s < 0.5 and any("by=tick" in line for _, line in exits),
        f"{exit_s} {exits[:2]}",
    )
    flapping = [line for line in _lines(bench, "node-1", rf"fast_flapping entity={flyer} ") if " - Thread" not in line]
    bench.check("a normal flight is not reported as flapping", not flapping, str(flapping[:2]))
    bench.check(
        "the node asked the Gateway for a pass on the transitions",
        (diag.get("fastSuppressed") or {}).get("passRequests", 0) >= 2,
        str(diag.get("fastSuppressed")),
    )
    bench.exercised(
        flyer=flyer,
        warp=f"{TICK_MPS:.0f} m/s x {TICK_FLIGHT_S:.0f} s teleport",
        enterSeconds=enter_s,
        exitSeconds=exit_s,
    )


def p1_fast_05_red(bench) -> None:
    p1_fast_05(bench, red=True)


PULSE = (4, 4)  # frames moving, frames still: the tick detector enters and leaves every 8 frames (15 a second)
PULSE_S = 10.0


def p1_fast_06(bench) -> None:
    """SE1-0084: a member that keeps flipping in and out of the fast state (a 3 km/s teleport drive that moves
    4 frames and stands still 4) costs at most one immediate membership pass and one Gateway pass request per
    250 ms, not one per transition, and is logged as flapping (fast_flapping) once per 60 s."""
    bench.start_cluster()
    bench.quiesce()
    flyer = _spawn(bench, "node-1", 1_500_000.0, static=False)
    time.sleep(5)
    node_before = bench.cluster.node("node-1", "/diag")["load"]
    gateway_before = bench.cluster.get("/diag?registry=summary")["registry"]["fastSuppressed"]
    transitions_before = len(_stamped(bench, "node-1", rf"fast_member (enter|exit) entity={flyer} "))
    bench.cluster.node(
        "node-1",
        f"/fast-test-drive?entity={flyer}&vx={TICK_MPS}&vy=0&vz=0&seconds={PULSE_S}&mode=teleport"
        f"&pulse={PULSE[0]},{PULSE[1]}",
        "POST",
    )
    time.sleep(PULSE_S + 3)
    node_after = bench.cluster.node("node-1", "/diag")["load"]
    gateway_after = bench.cluster.get("/diag?registry=summary")["registry"]["fastSuppressed"]
    transitions = len(_stamped(bench, "node-1", rf"fast_member (enter|exit) entity={flyer} ")) - transitions_before
    passes = node_after["fastImmediatePasses"] - node_before["fastImmediatePasses"]
    requests = gateway_after["passRequests"] - gateway_before["passRequests"]
    limit = int((PULSE_S + 1) * 4) + 2  # one request per 250 ms over the drive, plus a second for the tail
    bench.record("pulse", {"transitions": transitions, "immediatePasses": passes, "passRequests": requests, "limit": limit,
                           "node": [node_before, node_after], "gateway": [gateway_before, gateway_after]})
    bench.check("the pulsed flyer keeps entering and leaving the fast state (the condition exists)",
                transitions >= 50, f"{transitions} transitions in {PULSE_S:.0f} s")
    bench.check("at most one immediate membership pass per 250 ms", 0 < passes <= limit, f"{passes} passes, limit {limit}")
    bench.check("at most one Gateway pass request per 250 ms", 0 < requests <= limit, f"{requests} requests, limit {limit}")
    flapping = _lines(bench, "node-1", rf"fast_flapping entity={flyer} ")
    flapping = [line for line in flapping if " - Thread" not in line]
    bench.check("the node logged the flyer as flapping, once in the 60 s window",
                len(flapping) == 1 and node_after["fastFlapReports"] - node_before["fastFlapReports"] == 1, str(flapping[:3]))
    bench.exercised(flyer=flyer, pulse=f"{PULSE[0]} on / {PULSE[1]} off frames at {TICK_MPS:.0f} m/s",
                    transitions=transitions, immediatePasses=passes, passRequests=requests)


SCENARIOS_FAST = {
    "P1-FAST-00": (p1_fast_00, "node self-test: fast-motion-tracker, fast-tick-detector, fast-flap-monitor, membership-fast and the membership sections", "1"),
    "P1-FAST-01": (p1_fast_01, "fast grid group: own partition, no merges while fast, merge after", "3"),
    "P1-FAST-01-RED": (p1_fast_01_red, "P1-FAST-01 with the fast state off: the flyer merges/changes partition", "3"),
    "P1-FAST-03": (p1_fast_03, "rotation handover of a fast partition: moved, carried fast, one intact copy, never merged", "3"),
    "P1-FAST-03-LOST": (p1_fast_03_lost, "earned red of the no-loss check: a flyer closed after the move fails 'exactly once'", "3"),
    "P1-FAST-04": (p1_fast_04, "SIGKILL of a fast partition's owner: saved while fast, recovered once, intact, re-derived fast, unmerged", "3"),
    "P1-FAST-02": (p1_fast_02, "fast threshold = sync distance / 3: at 6000 m, 1500 m/s stays out, 3000 enters", "3"),
    "P1-FAST-02-RED": (p1_fast_02_red, "P1-FAST-02 at the default sync 3000: the same 1500 m/s flight enters", "3"),
    "P1-FAST-05": (p1_fast_05, "SE1-0084 tick detector: fast within 0.5 s of a 3 km/s start, no merge or wake on the way, exit within 0.5 s", "3"),
    "P1-FAST-05-RED": (p1_fast_05_red, "P1-FAST-05 with fast.tickDetector=false: the flyer merges with or wakes an obstacle", "3"),
    "P1-FAST-06": (p1_fast_06, "SE1-0084 flapping member: at most one immediate pass and one Gateway request per 250 ms, logged as flapping", "3"),
}
