"""SE-855: programmable block scripts on worker threads on a cluster, with the Performance plugin's parallel PB phase.

Runs the items SE-762 lists as cluster risks with real scripts, at the plugin's defaults inside the feature: IGC across
nodes, freeze and partition handover with PBs running, world saves, grid split, merge and close during runs, ownership
changes and the compile caches. `PB_WORKER=off` runs the same scenario with the options off, for comparing the logs.

Performance comes from CLUSTER_EXTRA_PLUGINS as a local plugin (CLUSTER_HUB_PLUGINS=bugfixes takes the hub's copy out).
    PB_WORKER=on uv run python scenarios_pb_worker.py
Prints a pass/fail line per check and a table of what each node logged, in evidence as log-summary-final.json.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import bench as bench_module
from cluster import ClusterError
from scenarios_ai import _post_json
from scenarios_core import _handover_url, _partitions
from scenarios_fast import _poll
from scenarios_wa import _founder

MODE = os.environ.get("PB_WORKER", "on")
NODES = ("node-1", "node-2")
ON = MODE == "on"
CONFIG = f"""<?xml version="1.0" encoding="utf-8"?>
<PerformanceConfig>
  <FixScriptCounter>{str(ON).lower()}</FixScriptCounter>
  <FixScriptScheduler>{str(ON).lower()}</FixScriptScheduler>
  <FixScriptProxies>{str(ON).lower()}</FixScriptProxies>
  <ScriptLogThreadDecisions>true</ScriptLogThreadDecisions>
</PerformanceConfig>
"""

# Terminal reads and queued writes, its state in Storage. Holds on to the lights it found first, so when the grid splits
# or closes it has blocks of another grid or closed ones to write to.
WORK = """
int n; int v = VERSION; bool on;
List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
List<IMyLightingBlock> lights = new List<IMyLightingBlock>();
IMyTextPanel lcd;
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update1; int.TryParse(Storage, out n);
  GridTerminalSystem.GetBlocksOfType(lights); lcd = GridTerminalSystem.GetBlockWithName("pbw-lcd") as IMyTextPanel; }
public void Save() { Storage = n.ToString(); }
public void Main(string a, UpdateType t) {
  n++;
  if (n % 10 != 0) return;
  on = !on;
  GridTerminalSystem.GetBlocks(all);
  foreach (var l in lights) { l.Enabled = on; l.Radius = on ? 5f : 6f; var x = l.CustomName; var i = l.DetailedInfo; }
  if (lcd != null) { lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE; lcd.WriteText("n=" + n); }
  Me.CustomData = "n=" + n + ";v=" + v + ";blocks=" + all.Count + ";lights=" + lights.Count;
  Echo("n=" + n);
}
"""

# Broadcasts on two tags, answers every broadcast it hears from another PB with a unicast, and counts what comes back
TALK = """
int n, sent, got, gaps, uok, ufail, ugot, reach, strings;
IMyBroadcastListener l, s; Dictionary<long, long> last = new Dictionary<long, long>();
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; int.TryParse(Storage, out n);
  l = IGC.RegisterBroadcastListener("pbw"); s = IGC.RegisterBroadcastListener("pbw-s"); l.SetMessageCallback("b");
  IGC.UnicastListener.SetMessageCallback("u"); }
public void Save() { Storage = n.ToString(); }
public void Main(string a, UpdateType t) {
  if ((t & UpdateType.Update10) != 0) {
    n++; IGC.SendBroadcastMessage("pbw", (long)n); IGC.SendBroadcastMessage("pbw-s", "s" + n); sent += 2; }
  while (l.HasPendingMessage) { var m = l.AcceptMessage(); if (m.Source == IGC.Me) continue; got++;
    var w = m.As<long>(); long p; if (last.TryGetValue(m.Source, out p) && w != p + 1) gaps++; last[m.Source] = w;
    if (IGC.IsEndpointReachable(m.Source)) reach++;
    if (IGC.SendUnicastMessage(m.Source, "pbw-u", (double)w)) uok++; else ufail++; }
  while (s.HasPendingMessage) { var m = s.AcceptMessage(); if (m.As<string>() != null) strings++; }
  while (IGC.UnicastListener.HasPendingMessage) { IGC.UnicastListener.AcceptMessage(); ugot++; }
  Me.CustomData = "n=" + n + ";sent=" + sent + ";got=" + got + ";gaps=" + gaps + ";uok=" + uok + ";ufail=" + ufail + ";ugot=" + ugot + ";reach=" + reach + ";strings=" + strings;
}
"""

# Spins for a millisecond or two: enough of them keep the frame budget busy, so the once a minute warning shows
HEAVY = """
int n;
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update1; }
public void Main(string a, UpdateType t) {
  n++; double x = 0; for (int i = 0; i < 5000; i++) x += System.Math.Sqrt(i + n);
  Me.CustomData = "n=" + n + ";x=" + (int)x;
}
"""


def values(text: str) -> dict[str, str]:
    return dict(part.split("=", 1) for part in (text or "").split(";") if "=" in part)


def number(pb: dict, key: str) -> int:
    return int(values(pb.get("customData")).get(key, 0))


def state(bench, node: str) -> dict:
    return bench.cluster.node(node, "/pb-test-state", timeout=120)


def all_pbs(bench, real: bool = False) -> dict[str, dict]:
    """The rig's own scripts, or the published ones, which may stop by themselves on a station lacking their blocks"""
    found = {}
    for node in NODES:
        for pb in state(bench, node)["pbs"]:
            if pb["name"].startswith("pbw-real-") == real:
                found[pb["name"]] = pb | {"node": node}
    return found


def corpus_scripts(folder: Path) -> list[tuple[str, str, bool]]:
    """Published scripts: every one using IGC and every 8th of the others, largest last. (id, source, uses IGC)"""
    scripts = sorted(
        (path.parent.name, path.read_text(errors="replace"))
        for path in folder.glob("*/Script.cs")
    )
    igc = [(name, source, True) for name, source in scripts if "IGC." in source]
    others = [
        (name, source, False) for name, source in scripts if "IGC." not in source
    ][::8]
    return igc + others


def real_summary(pbs: dict) -> dict:
    return {
        name: {
            "instance": pb["instance"],
            "termination": pb["termination"],
            "info": (pb["info"] or "")[:160],
        }
        for name, pb in sorted(pbs.items())
    }


def stations(bench, node: str, owner: int, *specs) -> dict:
    grids = {}
    for name, lateral, pbs in specs:

        def spawn():
            return _post_json(
                bench,
                node,
                "/pb-test-spawn",
                {
                    "name": name,
                    "owner": owner,
                    "lateral": lateral,
                    "length": 16,
                    "pbs": [{"name": n, "program": p} for n, p in pbs],
                },
                timeout=120,
            )

        # The node's list of player positions can be empty for a moment, and the station is placed from it
        status, answer = spawn()
        deadline = time.time() + 30
        while "No cluster player position" in str(answer) and time.time() < deadline:
            time.sleep(1)
            status, answer = spawn()
        if status != 200:
            raise ClusterError(f"spawn {name} on {node}: {status} {answer}")
        grids[name] = answer["grid"]
    return grids


def partition_of(bench, grid: int) -> tuple[str, int] | None:
    for node in NODES:
        for row in _partitions(bench, node):
            if any(member.get("id") == grid for member in row.get("members") or []):
                return node, row["id"]
    return None


class Parallel:
    """The plugin's counters since the start, across its once a minute reset"""

    KEYS = (
        "frames",
        "dueRuns",
        "ranRuns",
        "parallelRuns",
        "overloadFrames",
        "failedWrites",
    )

    def __init__(self, bench):
        self.bench = bench
        self.seen = {node: {} for node in NODES}
        self.total = {node: Counter() for node in NODES}

    def sample(self):
        for node in NODES:
            now = self.bench.cluster.node(node, "/pb-test-scheduler", timeout=120)
            for key in self.KEYS:
                value, before = now.get(key) or 0, self.seen[node].get(key, 0)
                self.total[node][key] += value - before if value >= before else value
                self.seen[node][key] = value
        return self.total


def log_text(bench, process: str) -> str:
    text = ""
    for path in [
        *(bench.run_root / f"{process}-data").glob("SpaceEngineersDedicated_*.log"),
        bench.run_root / f"{process}.log",
    ]:
        if path.is_file():
            text += path.read_text(errors="replace")
    return text


LOG_PATTERNS = {
    "per thread": r"PB instruction counter per thread",
    "not per thread": r"FixScriptCounter is on, but",
    "worker decisions": r"PB thread decision: .*on a worker thread",
    "main decisions": r"PB thread decision: .*main thread",
    "over budget warnings": r"PB scheduler over budget",
    "failed write warnings": r"writes of scripts run on a worker thread failed",
    "script stopped by limit": r"ran longer than this server",
    "exceptions": r"Exception",
    "collection races": r"Collection was modified|Operations that change non-concurrent",
    "null references": r"NullReferenceException",
    "igc failures": r"igc.*(failed|error|dropped)",
    "unhandled": r"Unhandled exception",
    "script frames": r"at Shared\.Patches\.Script|ScriptProxies|ScriptRunner\.Execute",
}


def log_summary(bench) -> dict:
    summary = {}
    for process in (*NODES, "wa-1"):
        text = log_text(bench, process)
        summary[process] = {
            name: len(re.findall(pattern, text))
            for name, pattern in LOG_PATTERNS.items()
        }
    return summary


def handover(bench, grid: int) -> tuple[str, float]:
    source, partition = partition_of(bench, grid)
    target = next(node for node in NODES if node != source)
    started = time.time()
    bench.cluster.post(
        _handover_url(bench, partition, clientless=True, source=source, target=target),
        timeout=300,
    )
    if not _poll(lambda: (partition_of(bench, grid) or (None,))[0] == target, 120):
        raise ClusterError(f"grid {grid} did not reach {target}")
    return target, time.time() - started


def stopped(pbs: dict) -> dict:
    return {
        k: (v["termination"], (v["info"] or "")[:300])
        for k, v in pbs.items()
        if not v["instance"] or v["termination"] not in (None, "None")
    }


def p2_pb_worker(bench) -> None:
    prepare = bench.cluster.prepare

    def with_config(*args, **kwargs):
        prepare(*args, **kwargs)
        for process in (*NODES, "wa-1"):
            folder = bench.run_root / f"{process}-data"
            folder.mkdir(parents=True, exist_ok=True)
            (folder / "Performance.cfg").write_text(CONFIG, encoding="utf-8")

    bench.cluster.prepare = with_config
    ctx = _founder(bench)
    owner = ctx.binding_identity
    counters = Parallel(bench)

    # 1. Really on
    for node in NODES:
        scheduler = bench.cluster.node(node, "/pb-test-scheduler")
        bench.record(f"scheduler-{node}", scheduler)
        bench.check(
            f"{node}: the parallel phase is {MODE}",
            scheduler.get("parallel") is ON,
            json.dumps(scheduler),
        )
        bench.check(
            f"{node}: the counter per thread is {ON}",
            scheduler.get("perThread") is ON,
            json.dumps(scheduler),
        )

    work = WORK.replace("VERSION", "1")
    grids = {}
    grids |= stations(
        bench,
        "node-1",
        owner,
        *[
            (f"pbw-a{i}", 3000.0 + 700 * i, [(f"pbw-work-a{i}", work)])
            for i in range(4)
        ],
    )
    grids |= stations(
        bench,
        "node-2",
        owner,
        *[
            (f"pbw-b{i}", 9000.0 + 700 * i, [(f"pbw-work-b{i}", work)])
            for i in range(4)
        ],
    )
    grids |= stations(
        bench,
        "node-1",
        owner,
        *[
            (f"pbw-ta{i}", 15000.0 + 700 * i, [(f"pbw-talk-a{i}", TALK)])
            for i in range(3)
        ],
    )
    grids |= stations(
        bench,
        "node-2",
        owner,
        *[
            (f"pbw-tb{i}", 21000.0 + 700 * i, [(f"pbw-talk-b{i}", TALK)])
            for i in range(3)
        ],
    )
    bench.record("grids", grids)

    def running():
        pbs = all_pbs(bench)
        return (
            pbs
            if len(pbs) == 14 and all(number(pb, "n") > 0 for pb in pbs.values())
            else None
        )

    pbs = _poll(running, 240)
    seen = all_pbs(bench)
    bench.record("pbs-started", seen)
    bench.check(
        "every script compiled and runs",
        pbs,
        json.dumps(
            {k: (v["working"], (v["info"] or "")[:200]) for k, v in seen.items()}
        )[:900],
    )
    if not pbs:
        return
    if ON:
        _poll(lambda: all(counters.sample()[n]["parallelRuns"] > 0 for n in NODES), 60)
    total = counters.sample()
    bench.record("parallel-started", {n: dict(total[n]) for n in NODES})
    print("parallel after the start:", {n: dict(total[n]) for n in NODES}, flush=True)
    if ON:
        bench.check(
            "the runs go through the parallel phase on both nodes",
            all(total[n]["parallelRuns"] > 0 for n in NODES),
            str(total),
        )

    # Published scripts run on from here on, through every leg below; their names are compared with the other mode
    real = corpus_scripts(
        Path(os.environ.get("PB_CORPUS", "~/se626/corpus")).expanduser()
    )
    for index in range(0, len(real), 4):
        batch = real[index : index + 4]
        node = NODES[index // 4 % 2]
        grids |= stations(
            bench,
            node,
            owner,
            (
                f"pbw-rs{index}",
                33000.0 + 175 * index,
                [(f"pbw-real-{name}", source) for name, source, _ in batch],
            ),
        )
    # Settled: each one compiled, or stopped trying. A script may rename its block, which takes it off this list.
    _poll(
        lambda: (found := all_pbs(bench, real=True))
        and all(
            v["instance"] or v["termination"] not in (None, "None")
            for v in found.values()
        ),
        60,
    )
    started = real_summary(all_pbs(bench, real=True))
    bench.record("real-started", started)
    print(
        "published scripts after the start:",
        json.dumps({k: [v["instance"], v["termination"]] for k, v in started.items()}),
        flush=True,
    )
    bench.check(
        "published scripts spawned",
        len(started) > len(real) // 2,
        f"{len(started)} of {len(real)}",
    )

    # 2. IGC across nodes for a while
    samples = []

    def talking() -> dict:
        return {
            name: values(pb["customData"])
            for name, pb in all_pbs(bench).items()
            if name.startswith("pbw-talk")
        }

    # Stations coming up in each other's reach can miss a few at the very start: what counts is from here on
    settled = talking()

    # Until every talker has heard plenty, the send's stall showed long before that
    def heard():
        counters.sample()
        samples.append([state(bench, node)["simRatio"] for node in NODES])
        now = talking()
        return len(now) == 6 and all(
            int(v.get("got", 0)) - int(settled.get(k, {}).get("got", 0)) >= 600
            and int(v.get("ugot", 0)) > 0
            and int(v.get("strings", 0)) > 0
            for k, v in now.items()
        )

    _poll(heard, 120)
    pbs = all_pbs(bench)
    talkers = {
        name: values(pb["customData"])
        for name, pb in pbs.items()
        if name.startswith("pbw-talk")
    }
    bench.record(
        "igc",
        {
            "talkers": talkers,
            "simRatio": samples,
            "nodes": {n: bench.cluster.node(n, "/igc-status") for n in NODES},
        },
    )
    print("IGC after 2 minutes:", json.dumps(talkers), flush=True)
    print("sim ratio:", samples, flush=True)
    detail = json.dumps(talkers)[:900]
    bench.check(
        "IGC: scripts hear broadcasts across nodes",
        len(talkers) == 6 and all(int(v.get("got", 0)) > 0 for v in talkers.values()),
        detail,
    )
    bench.check(
        "IGC: unicast replies come back across nodes",
        all(int(v.get("ugot", 0)) > 0 for v in talkers.values()),
        detail,
    )
    bench.check(
        "IGC: no gaps in a sender's sequence",
        all(
            v.get("gaps") == settled.get(k, {}).get("gaps") for k, v in talkers.items()
        ),
        detail,
    )
    bench.check(
        "IGC: strings arrive too",
        all(int(v.get("strings", 0)) > 0 for v in talkers.values()),
        detail,
    )
    bad = stopped(pbs)
    bench.check(
        "IGC: no script was stopped by a limit", not bad, json.dumps(bad)[:1200]
    )

    # 3. Handovers with PBs running, IGC ones among them
    names = {
        "pbw-a0": "pbw-work-a0",
        "pbw-b1": "pbw-work-b1",
        "pbw-ta0": "pbw-talk-a0",
        "pbw-tb1": "pbw-talk-b1",
    }
    durations, regress = [], []
    for round_ in range(6):
        for station, name in names.items():
            before = all_pbs(bench).get(name)
            try:
                target, seconds = handover(bench, grids[station])
            except ClusterError as error:
                bench.check(f"handover {round_}: {name}", False, str(error)[:400])
                continue
            durations.append(round(seconds, 1))

            def resumed():
                pb = all_pbs(bench).get(name)
                return (
                    pb
                    if pb
                    and pb["node"] == target
                    and pb["instance"]
                    and number(pb, "n") > number(before or {}, "n")
                    else None
                )

            if not _poll(resumed, 60):
                regress.append(
                    (
                        name,
                        round_,
                        (before or {}).get("customData"),
                        (all_pbs(bench).get(name) or {}).get("customData"),
                    )
                )
        counters.sample()
    bench.record("handovers", {"durations": durations, "regress": regress})
    print("handover seconds:", durations, flush=True)
    bench.check(
        "handover: every moved script goes on counting from its saved state on the new node",
        durations and not regress,
        json.dumps(regress)[:1000],
    )
    bad = stopped(all_pbs(bench))
    bench.check("handover: no script was stopped", not bad, json.dumps(bad)[:1200])

    # 4. Saves with PBs running
    saves = 0
    for _ in range(10):
        for node in NODES:
            for row in _partitions(bench, node):
                try:
                    bench.cluster.node(
                        node,
                        f"/save-capture?partition={row['id']}",
                        method="POST",
                        timeout=120,
                    )
                    saves += 1
                except ClusterError as error:
                    if "409" not in str(error):
                        bench.check(
                            f"save of partition {row['id']}", False, str(error)[:300]
                        )
        time.sleep(3)
    bench.record("saves", saves)
    bench.check("saves: partitions saved with scripts running", saves > 0, str(saves))
    bench.check(
        "saves: scripts still run",
        not stopped(all_pbs(bench)),
        json.dumps(stopped(all_pbs(bench)))[:600],
    )

    # 5. Owner changes, split and close on running grids. The script keeps writing to the lights of the part split off.
    for name in ("pbw-a1", "pbw-b0"):
        home, _ = partition_of(bench, grids[name])
        for _ in range(5):
            bench.cluster.node(
                home,
                f"/pb-test-op?grid={grids[name]}&op=owner&other={owner}",
                method="POST",
            )
    split = {}
    whole = {
        name: number(all_pbs(bench)[name], "blocks")
        for name in ("pbw-work-a1", "pbw-work-b0")
    }
    for name in ("pbw-a1", "pbw-b0"):
        home, _ = partition_of(bench, grids[name])
        split[name] = bench.cluster.node(
            home, f"/pb-test-op?grid={grids[name]}&op=split", method="POST"
        )
    _poll(
        lambda: all(
            number(all_pbs(bench)[name], "blocks") < blocks
            for name, blocks in whole.items()
        ),
        30,
    )
    bench.record("split", split)
    pbs = all_pbs(bench)
    for name in ("pbw-work-a1", "pbw-work-b0"):
        bench.check(
            f"split: {name} goes on running on its smaller grid",
            pbs[name]["instance"] and number(pbs[name], "blocks") > 0,
            pbs[name]["customData"],
        )
    # The cut off ends go back on, with the scripts running
    for name, script in (("pbw-a3", "pbw-work-a3"), ("pbw-b3", "pbw-work-b3")):
        home, _ = partition_of(bench, grids[name])
        before = {grid["id"] for grid in state(bench, home)["grids"]}
        bench.cluster.node(
            home, f"/pb-test-op?grid={grids[name]}&op=split", method="POST"
        )
        cut_off = (
            _poll(
                lambda: [
                    grid
                    for grid in state(bench, home)["grids"]
                    if grid["id"] not in before
                ],
                30,
            )
            or []
        )
        if not bench.check(
            f"merge: {name} was cut in two on {home}",
            len(cut_off) == 1,
            json.dumps(cut_off),
        ):
            continue
        count = number(all_pbs(bench)[script], "n")
        answer = bench.cluster.node(
            home,
            f"/pb-test-op?grid={grids[name]}&op=merge&other={cut_off[0]['id']}",
            method="POST",
        )
        bench.record(f"merge-{name}", answer)
        # The razed block is still missing, so what the game makes of the merged grid afterwards isn't the point
        ran = _poll(lambda: number(all_pbs(bench)[script], "n") > count + 10, 60)
        bench.check(
            f"merge: {script} goes on running through the merge",
            answer.get("grid") and ran,
            json.dumps([count, answer, all_pbs(bench)[script]["customData"]])[:400],
        )
    counters.sample()
    for name in ("pbw-a2", "pbw-b2"):
        home, _ = partition_of(bench, grids[name])
        bench.cluster.node(
            home, f"/pb-test-op?grid={grids[name]}&op=close", method="POST"
        )
    _poll(lambda: not {"pbw-work-a2", "pbw-work-b2"} & set(all_pbs(bench)), 30)
    pbs = all_pbs(bench)
    bench.check(
        "close: the closed grids' scripts are gone",
        "pbw-work-a2" not in pbs and "pbw-work-b2" not in pbs,
        str(sorted(pbs)),
    )
    bench.check(
        "close: the others still run", not stopped(pbs), json.dumps(stopped(pbs))[:600]
    )
    counters.sample()

    ended = real_summary(all_pbs(bench, real=True))
    bench.record("real-after-grid-ops", ended)
    print(
        "published scripts after the grid operations:",
        json.dumps({k: [v["instance"], v["termination"]] for k, v in ended.items()}),
        flush=True,
    )

    # 6. Compile caches: the same source on a new PB (the cluster's cache), then a changed source
    grids |= stations(
        bench, "node-1", owner, ("pbw-c0", 30000.0, [("pbw-work-c0", work)])
    )
    fresh = _poll(lambda: number(all_pbs(bench).get("pbw-work-c0") or {}, "n") > 0, 120)
    bench.check("cache: the same source on a new PB runs", fresh, "")
    target = all_pbs(bench)["pbw-work-a3"]
    status, answer = _post_json(
        bench,
        target["node"],
        f"/pb-test-program?block={target['id']}",
        WORK.replace("VERSION", "2"),
        timeout=120,
    )
    bench.check("cache: a changed source is taken", status == 200, str(answer))
    changed = _poll(
        lambda: values(all_pbs(bench)["pbw-work-a3"]["customData"]).get("v") == "2", 120
    )
    bench.check(
        "cache: a changed source runs, not the cached one",
        changed,
        target["customData"],
    )

    # 7. Enough load that the frame budget shows, with the share of parallel runs
    for i in range(10):
        stations(
            bench,
            "node-1",
            owner,
            (
                f"pbw-h{i}",
                40000.0 + 700 * i,
                [(f"pbw-heavy-{i}-{j}", HEAVY) for j in range(8)],
            ),
        )
    deadline = time.time() + 150
    warning = []
    while time.time() < deadline and not warning:
        counters.sample()
        warning = re.findall(r"PB scheduler over budget.*", log_text(bench, "node-1"))
        time.sleep(10)
    print("budget warning:", warning[-1] if warning else None, flush=True)
    bench.record("budget-warning", warning)
    # The warning is printed only by an overloaded node, this load may not get there. It prints these counters.
    total = counters.sample()
    if ON:
        bench.check(
            "load: the counters behind the warning show runs in parallel",
            all(total[n]["parallelRuns"] > 0 for n in NODES)
            and not (warning and " 0% of the runs in parallel" in warning[-1]),
            str(total),
        )

    # 8. A graceful stop and warm start bring the scripts back, from the compile cache on disk
    pbs = all_pbs(bench)
    counts = {
        name: number(pb, "n") for name, pb in pbs.items() if name.startswith("pbw-work")
    }
    bench.record("before-restart", counts)
    counters.sample()
    bench.record("parallel-total", {n: dict(counters.total[n]) for n in NODES})
    print("parallel totals:", {n: dict(counters.total[n]) for n in NODES}, flush=True)
    bench.check("graceful stop", bench.cluster.stop(grace=240))
    bench.record("log-summary-before-restart", log_summary(bench))
    bench.cluster.start(["node-1", "node-2", "wa-1"])
    bench.cluster.wait_ready(nodes=["node-1", "node-2", "wa-1"])

    asked = [0.0]

    def back():
        # A drained partition stays offline until something wants it: a demand holds for 10 s, as a player's does
        if time.time() - asked[0] >= 10:
            asked[0] = time.time()
            for row in bench.cluster.get("/registry/partitions/offline").get(
                "Partitions", []
            ):
                node = NODES[int(row["Partition"]) % len(NODES)]
                bench.cluster.post(
                    f"/registry/partitions/offline/load?partition={row['Partition']}&node={node}&reason=se-855"
                )
        now = all_pbs(bench)
        return now if all(number(now.get(k) or {}, "n") > 0 for k in counts) else None

    now = _poll(back, 300)
    bench.check("restart: the scripts run again", now, str(sorted(all_pbs(bench))))
    if now:
        low = {
            k: (v, number(now[k], "n"))
            for k, v in counts.items()
            if number(now[k], "n") < v - 3600
        }
        bench.check(
            "restart: each goes on from a saved count", not low, json.dumps(low)
        )
    summary = log_summary(bench)
    bench.record("log-summary-final", summary)
    print("final log summary:", json.dumps(summary), flush=True)
    bench.check(
        "no unhandled exception on any process",
        all(s["unhandled"] == 0 for s in summary.values()),
        json.dumps(summary),
    )
    bench.check(
        "no collection race in any log",
        all(s["collection races"] == 0 for s in summary.values()),
        json.dumps(summary),
    )


def main() -> int:
    root = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        f"%Y%m%d-%H%M%SZ-pb-worker-{MODE}"
    )
    root.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        p2_pb_worker,
        f"P2-PB-WORKER-{MODE.upper()}",
        "SE-855: PB scripts on worker threads on a cluster",
        "3",
        root,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(root)}
        )
    )
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
