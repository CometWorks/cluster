"""T-0361 bench (part 1): a grid in flight at a cluster stop is saved with its velocity and comes back moving.

One regular node + WA, the packaged Cluster-Simple world (deep space, no planet, no gravity), NO game client.
  1. an unpiloted, unpowered 27-block grid is spawned far from everything (/trash-test-spawn) and is alone in
     its partition; ONE write gives it a linear and an angular velocity (/fast-test-velocity, added for this
     bench): no thruster, no dampener, no drive re-applies anything, it coasts.
  2. it is read twice on the node (GET /fast-test-velocity): speed and the distance covered between the reads.
  3. the graceful cluster stop the live cluster uses (cluster.py `stop` = Cluster.stop: POST
     /registry/shutdown/graceful, then every process exits). The node drains: it freezes the partition's
     members (physics disabled) and captures the drain "Final". With offline.enabled (the default) a cluster
     stop then takes EVERY partition OFFLINE (T-0237, PartitionPersistence.Offline.cs DrainOfflineOffer +
     OfflinePartitions.cs HandleOfflineRoutes): the registry keeps an offline row that names the Final.
  4. the cluster starts again on the same run root (registry and node data intact: a warm restart). Nothing
     loads an offline partition at boot, so the bench asks for it the way demand does: POST
     /registry/partitions/offline/load?partition=&node=. Should the stop have RELEASED the partition instead
     (offline refused or disabled), the boot recovery loads it and the bench only waits. Either way the node
     loads the Final through its recovery path ('recovery loaded partition ...').
  5. the grid is polled every 0.25 s from the restart on; the first read that finds it with physics enabled
     is "right after the recovery", and the trace continues for T0361_TRACE seconds.
Checks after the restart:
  (a) the grid exists with the same entity id and name, and the node's 'recovery loaded partition' line lists it
  (b) its speed at the first read is within 10% of the speed read before the stop
  (c) its direction matches (cosine >= 0.99)
  (d) the node's own 'import settle partition=<id> ageS=5 ... fastest=' line, when emitted for this load,
      reports the same speed - a second number from a different reader ([INFO] when the line is absent)
  (e) the position advanced along the saved direction between the first and the last read of the trace
The angular velocity is reported as [INFO] only: vanilla grids carry an angular damping of 0.1, so the spin
decays between the last read and the freeze and no tolerance on it would be honest.
T0361_EXPECT=red inverts (b), (c), (d) and (e) for a package WITHOUT the fix: the Final was captured after the
freeze, vanilla reports zero velocity for a disabled body, and the grid comes back standing still.

Invocation: cd <tree>/Test && CLUSTER_PACKAGE=<pkg> PYTHONPATH=$PWD uv run python bench_t0361.py <label>
"""
import json
import math
import os
import re
import sys
import time
from collections import Counter
from pathlib import Path

from bench import run_scenario
from cluster import ClusterError, wait_until
from scenarios import _all_partitions
from scenarios_core import _lines, _partitions

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
RED = os.environ.get("T0361_EXPECT", "green").lower() == "red"
OUT = Path(os.environ.get("T0361_OUT", "/home/factory/cluster-runs/regression/tools/t0361")) / f"bench-{LABEL}"
OUT.mkdir(parents=True, exist_ok=True)
SPEED = float(os.environ.get("T0361_SPEED", "50"))         # m/s along +X; the large-grid limit is 100
SPIN = float(os.environ.get("T0361_SPIN", "0.3"))          # rad/s about Y, reported only
SPAWN_X = float(os.environ.get("T0361_X", "500000"))       # far from the world's own content at the origin
GRACE = float(os.environ.get("T0361_GRACE", "240"))        # the stop's budget for every process to exit
TRACE_S = float(os.environ.get("T0361_TRACE", "12"))       # how long the grid is sampled after it came back
LOAD_TIMEOUT = float(os.environ.get("T0361_LOAD_TIMEOUT", "240"))
TOLERANCE = 0.10
NODE = "node-1"
STARTED = [NODE, "wa-1"]


def node(bench, path: str, method: str = "GET", timeout: float = 40):
    return bench.cluster.node(NODE, path, method=method, timeout=timeout)


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


def info(text: str) -> None:
    print(f"  [INFO] {text}")


def motion(bench, grid: int) -> dict | None:
    """The grid as the node's physics reports it, stamped with the bench's clock; None when it is not there."""
    row = attempt(lambda: node(bench, f"/fast-test-velocity?entity={grid}", timeout=15))
    if not row or not row.get("found"):
        return None
    row["t"] = time.time()
    return row


def brief(row: dict | None) -> str:
    if not row:
        return "not on the node"
    return (f"speed {row['speed']:.2f} m/s linear {[round(v, 2) for v in row['linear']]} spin {row['spin']:.3f} rad/s "
            f"position {[round(v, 1) for v in row['position']]} physicsEnabled={row['physicsEnabled']}")


def cosine(a, b) -> float:
    la, lb = math.sqrt(sum(v * v for v in a)), math.sqrt(sum(v * v for v in b))
    return sum(x * y for x, y in zip(a, b)) / (la * lb) if la > 1e-6 and lb > 1e-6 else 0.0


def along(start: dict, end: dict, direction) -> float:
    """Metres covered from START to END along DIRECTION (the saved velocity)."""
    length = math.sqrt(sum(v * v for v in direction)) or 1.0
    return sum((e - s) * d / length for s, e, d in zip(start["position"], end["position"], direction))


def where(bench, grid: int) -> dict | None:
    """The node's partition report row that holds the grid."""
    for row in attempt(lambda: _partitions(bench, NODE), []) or []:
        if any(member.get("id") == grid for member in row.get("members") or []):
            return row
    return None


def field(row: dict, name: str):
    """A registry row's field: its JSON is PascalCase, an anonymous answer is written as typed."""
    return row.get(name, row.get(name[:1].lower() + name[1:])) if isinstance(row, dict) else None


def offline_row(bench, grid: int, partition: int) -> dict | None:
    """The registry's offline row for the grid: by the partition it was drained from, else by membership."""
    row = attempt(lambda: bench.cluster.get(f"/registry/partitions/offline?partition={partition}"))
    if isinstance(row, dict) and field(row, "Partition") is not None:
        return row
    table = attempt(lambda: bench.cluster.get("/registry/partitions/offline?since=0"), {}) or {}
    return next((row for row in field(table, "Partitions") or [] if grid in (field(row, "Members") or [])), None)


def check(bench):
    # ---- 1. the fixture: an unpowered grid alone in its partition, coasting ----
    bench.start_cluster(nodes=STARTED)
    routes = (attempt(lambda: node(bench, "/test-plugin"), {}) or {}).get("routes") or []
    require(bench, "setup: the test plugin serves /fast-test-velocity", "POST /fast-test-velocity" in routes
            and "GET /fast-test-velocity" in routes, f"{len(routes)} routes")
    spawned = node(bench, f"/trash-test-spawn?x={SPAWN_X}&y=0&z=0&static=false&blocks=27", "POST")
    bench.record("00-spawn", spawned)
    grid = int(spawned.get("grid") or 0)
    require(bench, f"setup: an unpowered 27-block grid spawned on {NODE}", grid, json.dumps(spawned))
    home = poll(lambda: (lambda row: row if row and _all_partitions(bench).get(int(row["id"])) == NODE else None)(where(bench, grid)), 90)
    bench.record("01-partition", home or where(bench, grid))
    require(bench, f"setup: the grid's partition is owned by {NODE} in the registry", home, json.dumps(where(bench, grid))[:300])
    members = [member.get("id") for member in home.get("members") or []]
    require(bench, "setup: the grid is alone in its partition and no client is in it",
            members == [grid] and not home.get("clients"), f"members {members} clients {home.get('clients')}")

    bench.record("02-set-velocity", node(bench, f"/fast-test-velocity?entity={grid}&vx={SPEED}&vy=0&vz=0&wx=0&wy={SPIN}&wz=0", "POST"))
    time.sleep(2)
    first = motion(bench, grid)
    time.sleep(3)
    second = motion(bench, grid)
    bench.record("03-moving-before-stop", {"first": first, "second": second})
    require(bench, f"setup: the grid coasts at {SPEED:.0f} m/s on both reads (nothing re-applies the velocity)",
            first and second and all(abs(row["speed"] - SPEED) <= 0.05 * SPEED and not row["isStatic"] for row in (first, second)),
            f"{brief(first)} | {brief(second)}")
    covered = along(first, second, first["linear"])
    elapsed = second["t"] - first["t"]
    require(bench, "setup: the position advanced along the velocity between the two reads",
            covered >= 0.5 * SPEED * elapsed, f"{covered:.1f} m in {elapsed:.1f} s at {SPEED:.0f} m/s")
    # The partition id the drain will name: a moving partition keeps its id, but it is read, not assumed.
    row = where(bench, grid)
    require(bench, "setup: the grid is still alone in a partition right before the stop",
            row and [member.get("id") for member in row.get("members") or []] == [grid], json.dumps(row)[:300])
    partition = int(row["id"])
    saved = motion(bench, grid)
    require(bench, "setup: the last read before the stop still shows the grid moving", saved and saved["speed"] > 0.9 * SPEED, brief(saved))
    info(f"before the stop: grid {grid} '{saved.get('name')}' partition {partition}: {brief(saved)}")

    # ---- 2. the graceful cluster stop: drain Finals, every process exits ----
    stop_started = time.time()
    graceful = bench.cluster.stop(grace=GRACE)
    stop_seconds = time.time() - stop_started
    bench.check(f"stop: every process exited on its own inside {GRACE:.0f} s", graceful, f"{stop_seconds:.0f} s")
    unloaded = _lines(bench, NODE, rf"drain final registered and partition unloaded: {partition}\b")
    require(bench, f"stop: {NODE} registered the drain Final of partition {partition} and unloaded it", unloaded,
            "; ".join(_lines(bench, NODE, r"drain blocked|force-drain deadline|drain capture of partition")[-3:]) or "no drain line")
    went_offline = _lines(bench, "gateway", rf"partition {partition} went OFFLINE from {NODE}")
    info(f"stop: {'the registry took the partition OFFLINE: ' + went_offline[-1][-160:] if went_offline else 'no OFFLINE line: the partition was released for the boot recovery'}")
    bench.record("10-stop", {"graceful": graceful, "seconds": round(stop_seconds, 1), "unloaded": unloaded, "offline": went_offline})
    before_lines = Counter(_lines(bench, NODE, r"recovery loaded partition |import settle partition="))
    fresh = lambda pattern: list((Counter(_lines(bench, NODE, pattern)) - before_lines).elements())  # noqa: E731

    # ---- 3. the restart on the same run root, then the load ----
    bench.cluster.start(STARTED)
    bench.cluster.wait_ready(nodes=STARTED)
    ready = time.time()
    back = motion(bench, grid)
    offline = offline_row(bench, grid, partition)
    bench.record("20-after-restart", {"offlineRow": offline, "gridOnNode": back,
                                      "owners": attempt(lambda: bench.cluster.get("/registry/partitions"))})
    if offline is not None:
        partition = int(field(offline, "Partition"))
        info(f"restart: partition {partition} is OFFLINE (save {json.dumps(field(offline, 'Save'))}), the grid is "
             f"{'ALREADY loaded' if back else 'not loaded'}; asking for the load as demand does")
    else:
        info(f"restart: no offline row for partition {partition}; the boot recovery must load it"
             + (" (already loaded)" if back else ""))

    trace: list[dict] = []
    asked: list = []
    last_ask = 0.0
    deadline = time.time() + LOAD_TIMEOUT
    arrived = None
    while time.time() < deadline:
        row = motion(bench, grid)
        if row is not None:
            trace.append(row)
            if arrived is None and row["physicsEnabled"]:
                arrived = row
            if arrived is not None and row["t"] - arrived["t"] >= TRACE_S:
                break
        elif offline is not None and time.time() - last_ask >= 10:
            # Demand is re-asserted by whoever still has it (RequestOfflineLoad): every 10 s until the grid is there.
            last_ask = time.time()
            asked.append(attempt(lambda: bench.cluster.post(
                f"/registry/partitions/offline/load?partition={partition}&node={NODE}&reason=t0361-bench"), "request failed"))
            if len(asked) == 1:
                info(f"restart: offline load request answered {json.dumps(asked[0])}")
        time.sleep(0.25)
    bench.record("21-load-requests", asked)
    bench.record("22-trace", [{"t": round(row["t"] - ready, 2), **{key: row[key] for key in
                              ("speed", "spin", "linear", "angular", "position", "physicsEnabled")}} for row in trace])
    loaded = fresh(rf"recovery loaded partition \d+ generation \d+ with \d+ members \[[^\]]*\b{grid}\b")
    bench.record("23-recovery-lines.txt", "\n".join(fresh(r"recovery loaded partition ")))
    require(bench, "(a) the grid exists after the restart with the same entity id and name",
            arrived is not None and arrived.get("name") == saved.get("name"),
            f"{brief(arrived)}; {len(asked)} load request(s), last {json.dumps(asked[-1:])}; "
            f"owners {json.dumps(attempt(lambda: bench.cluster.get('/registry/partitions')))[:200]}")
    bench.check(f"(a) {NODE} logged the recovery load of a partition that lists the grid", bool(loaded), (loaded or ["no line"])[-1][-200:])
    last = trace[-1]
    info(f"after the restart: first read {arrived['t'] - ready:.1f} s after ready ({len(trace)} samples over "
         f"{last['t'] - arrived['t']:.1f} s): {brief(arrived)}")
    info(f"position: {along(saved, arrived, saved['linear']):.1f} m along the saved direction since the last read before the stop "
         f"(the grid kept flying until the drain froze it, and since the load), {math.dist(saved['position'], arrived['position']):.1f} m in all")
    info(f"angular velocity (reported, not asserted - vanilla angular damping 0.1): {saved['spin']:.3f} rad/s before the stop, "
         f"{arrived['spin']:.3f} rad/s at the first read")

    # ---- 4. the verdict ----
    wanted = saved["speed"]
    speeds = [row["speed"] for row in trace if row["physicsEnabled"]]
    span = f"first {arrived['speed']:.2f} m/s, trace min {min(speeds):.2f} max {max(speeds):.2f}, saved {wanted:.2f} m/s"
    cos = cosine(arrived["linear"], saved["linear"])
    covered = along(arrived, last, saved["linear"])
    elapsed = last["t"] - arrived["t"]
    moved = math.dist(arrived["position"], last["position"])
    still = max(1.0, TOLERANCE * wanted)
    if RED:
        bench.check("(b, RED) the grid came back at rest: the Final stored velocity zero", arrived["speed"] < still and max(speeds) < still, span)
        bench.check("(c, RED) no direction to compare: the velocity vector is ~zero", arrived["speed"] < still, f"linear {arrived['linear']}")
        bench.check("(e, RED) the position did not advance", moved < 1.0 + 0.02 * wanted * elapsed, f"{moved:.2f} m in {elapsed:.1f} s")
    else:
        bench.check(f"(b) the speed right after the recovery is within {TOLERANCE:.0%} of the saved speed",
                    abs(arrived["speed"] - wanted) <= TOLERANCE * wanted, span)
        bench.check("(c) the direction matches the saved velocity", cos >= 0.99, f"cosine {cos:.4f}: {arrived['linear']} vs {saved['linear']}")
        bench.check("(e) the position advanced along the saved direction", elapsed >= 3 and covered >= 0.5 * wanted * elapsed,
                    f"{covered:.1f} m in {elapsed:.1f} s ({wanted * elapsed:.0f} m at sim speed 1)")

    # (d) the node's own sample 5 s after the load: a second reader of the same fact.
    loaded_id = int(re.search(r"recovery loaded partition (\d+)", loaded[-1]).group(1)) if loaded else partition
    settle_pattern = rf"import settle partition={loaded_id} ageS=5 .*fastest=([0-9.]+) m/s"
    settle = poll(lambda: fresh(settle_pattern), 45, interval=2) or []
    bench.record("24-settle-lines.txt", "\n".join(fresh(rf"import settle partition={loaded_id} ")))
    fastest = float(re.search(settle_pattern, settle[-1]).group(1)) if settle else None
    if fastest is None:
        info(f"(d) no 'import settle partition={loaded_id} ageS=5' line for this load")
    elif RED:
        bench.check("(d, RED) the node's import settle line reports the partition at rest", fastest < still, settle[-1][-200:])
    else:
        bench.check(f"(d) the node's import settle line reports the saved speed (within {TOLERANCE:.0%})",
                    abs(fastest - wanted) <= TOLERANCE * wanted, settle[-1][-200:])
    bench.exercised(expect="red" if RED else "green", grid=grid, partition=partition, loadedPartition=loaded_id,
                    wentOffline=bool(went_offline), offlineRowAfterRestart=offline is not None, loadRequests=len(asked),
                    gracefulStop=graceful, stopSeconds=round(stop_seconds, 1), savedSpeed=round(wanted, 2),
                    firstSpeed=round(arrived["speed"], 2), firstReadAfterReadyS=round(arrived["t"] - ready, 1),
                    cosine=round(cos, 4), advancedMeters=round(covered, 1), traceSeconds=round(elapsed, 1),
                    settleFastest=fastest, spinBefore=round(saved["spin"], 3), spinAfter=round(arrived["spin"], 3))


check.regular_nodes = 2   # the Gateway's arguments name two regular slots; only node-1 and the WA are started
result = run_scenario(check, "T0361-moving-final", "a grid in flight at a cluster stop comes back moving"
                      + (" (RED baseline: no fix)" if RED else ""), "3", OUT)
(OUT / "verdict.json").write_text(json.dumps(vars(result), default=lambda x: vars(x), indent=2))
raise SystemExit(0 if result.passed else 1)
