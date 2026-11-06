"""SE-1149 soak: does a regular node's live managed heap grow with clientless handovers?

WA + two regular nodes, Cluster-Simple, no client. Spawns SOAK_GRIDS grids on node-1 far apart (one partition
each), then hands every clientless partition back and forth between node-1 and node-2 for SOAK_MINUTES.
Every SOAK_DUMP_EVERY seconds it takes a dotnet-gcdump of both nodes (a gcdump forces a full GC, so its total
is the live heap) and writes the top types of each to the evidence dir.

Invocation: cd <tree>/Test && CLUSTER_PACKAGE=<pkg> PYTHONPATH=$PWD uv run python soak_1149.py <label>
"""

import json
import os
import subprocess
import sys
import time
from pathlib import Path

from bench import run_scenario
from cluster import ClusterError
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _join, _partitions

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
OUT = Path(os.environ.get("SOAK_OUT", str(Path.home() / "ws/se/tmp/se1149-soak"))) / LABEL
OUT.mkdir(parents=True, exist_ok=True)
GRIDS = int(os.environ.get("SOAK_GRIDS", "6"))
MINUTES = float(os.environ.get("SOAK_MINUTES", "30"))
DUMP_EVERY = float(os.environ.get("SOAK_DUMP_EVERY", "300"))
GCDUMP = os.environ.get("GCDUMP", str(Path.home() / ".dotnet/tools/dotnet-gcdump"))
NODES = ("node-1", "node-2")


def node_pid(bench, name: str) -> int | None:
    marker = str(bench.run_root / f"{name}-magnetar")
    for proc in Path("/proc").iterdir():
        if not proc.name.isdigit():
            continue
        try:
            cmdline = (proc / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
        except OSError:
            continue
        if cmdline.startswith("/") and "MagnetarInterim.bin" in cmdline.split(" ")[0] and marker in cmdline:
            return int(proc.name)
    return None


def gcdump(bench, name: str, tag: str) -> dict:
    pid = node_pid(bench, name)
    if pid is None:
        return {"node": name, "error": "no pid"}
    path = OUT / f"{name}-{tag}.gcdump"
    started = time.time()
    subprocess.run([GCDUMP, "collect", "-p", str(pid), "-o", str(path)], capture_output=True, text=True, timeout=600)
    pause = time.time() - started
    report = subprocess.run([GCDUMP, "report", str(path)], capture_output=True, text=True, timeout=600).stdout
    (OUT / f"{name}-{tag}.txt").write_text(report)
    rss = (
        int(
            next(
                line.split()[1]
                for line in Path(f"/proc/{pid}/status").read_text().splitlines()
                if line.startswith("VmRSS")
            )
        )
        // 1024
    )
    return {"node": name, "tag": tag, "seconds": round(pause, 1), "rssMB": rss, "head": report.splitlines()[:3]}


def soak(bench) -> None:
    bench.start_cluster()
    bench.quiesce()
    for index in range(GRIDS):
        x = 600_000.0 + index * 20_000.0
        bench.cluster.node("node-1", f"/trash-test-spawn?x={x}&y=0&z=0&static=false", "POST")
    time.sleep(20)
    dumps = [gcdump(bench, name, "t0000") for name in NODES]
    print(json.dumps(dumps), flush=True)
    started = time.time()
    next_dump = started + DUMP_EVERY
    handovers = failures = 0
    while time.time() - started < MINUTES * 60:
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            try:
                partitions = [
                    p["id"] for p in _partitions(bench, source) if not p.get("clients") and p.get("memberCount")
                ]
            except ClusterError:
                continue
            for partition in partitions:
                try:
                    bench.cluster.post(
                        _handover_url(bench, partition, clientless=True, source=source, target=target), timeout=120
                    )
                    handovers += 1
                except ClusterError as error:
                    failures += 1
                    print(f"  handover {partition} {source}->{target} failed: {str(error)[:160]}", flush=True)
        if time.time() >= next_dump:
            tag = f"t{int(time.time() - started):04d}"
            row = [gcdump(bench, name, tag) for name in NODES]
            dumps += row
            print(json.dumps({"handovers": handovers, "failures": failures, "dumps": row}), flush=True)
            next_dump += DUMP_EVERY
    dumps += [gcdump(bench, name, "end") for name in NODES]
    summary = {"handovers": handovers, "failures": failures, "dumps": dumps}
    bench.record("soak", summary)
    (OUT / "soak.json").write_text(json.dumps(summary, indent=2))
    bench.exercised(handovers=handovers, failures=failures)


def soak_client(bench) -> None:
    """One headless client; its partition goes node-1 -> node-2 -> node-1 ... (a traversal each time)."""
    _join(bench)
    time.sleep(20)
    dumps = [gcdump(bench, name, "t0000") for name in NODES]
    print(json.dumps(dumps), flush=True)
    started = time.time()
    next_dump = started + DUMP_EVERY
    handovers = failures = 0
    source, target = "node-1", "node-2"
    while time.time() - started < MINUTES * 60:
        try:
            partition = _client_partition(_partitions(bench, source))
        except ClusterError:
            partition = None
        if partition is None:
            source, target = target, source
            time.sleep(2)
            failures += 1
            continue
        try:
            bench.cluster.post(
                _handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300
            )
            handovers += 1
            source, target = target, source
        except ClusterError as error:
            failures += 1
            print(f"  handover {partition} {source}->{target} failed: {str(error)[:160]}", flush=True)
        time.sleep(float(os.environ.get("SOAK_GAP", "5")))
        if time.time() >= next_dump:
            tag = f"t{int(time.time() - started):04d}"
            row = [gcdump(bench, name, tag) for name in NODES]
            dumps += row
            print(json.dumps({"handovers": handovers, "failures": failures, "dumps": row}), flush=True)
            next_dump += DUMP_EVERY
    if os.environ.get("SOAK_FULLDUMP"):
        tool = str(Path(GCDUMP).with_name("dotnet-dump"))
        for name in NODES:
            pid = node_pid(bench, name)
            if pid:
                subprocess.run(
                    [tool, "collect", "-p", str(pid), "--type", "Full", "-o", str(OUT / f"{name}.dmp")],
                    capture_output=True,
                    timeout=900,
                )
    summary = {"handovers": handovers, "failures": failures, "dumps": dumps}
    bench.record("soak", summary)
    (OUT / "soak.json").write_text(json.dumps(summary, indent=2))
    bench.exercised(handovers=handovers, failures=failures)


def soak_antenna(bench) -> None:
    """One headless client on node-1 plus SOAK_GRIDS antenna grids in its range; the antenna grids' partitions go
    back and forth between the nodes, so every handover closes a proxy antenna on one node and installs one on
    the other (the live trader ships' pattern)."""
    _join(bench)
    sessions = bench.cluster.get("/transport/status").get("sessions") or []
    owner = next(s.get("identityId") for s in sessions if s.get("client") == CLIENT_ID)
    for index in range(GRIDS):
        bench.cluster.node(
            "node-1",
            f"/igc-test-ensure-named-grid?owner={owner}&name=se1149-antenna-{index}"
            f"&lateral={12000 + index * 7000}&radius=50000",
            "POST",
        )
    time.sleep(30)
    dumps = [gcdump(bench, name, "t0000") for name in NODES]
    print(json.dumps(dumps), flush=True)
    started = time.time()
    next_dump = started + DUMP_EVERY
    handovers = failures = 0
    while time.time() - started < MINUTES * 60:
        for source, target in (("node-1", "node-2"), ("node-2", "node-1")):
            try:
                partitions = [
                    p["id"] for p in _partitions(bench, source) if not p.get("clients") and p.get("memberCount")
                ]
            except ClusterError:
                continue
            for partition in partitions:
                try:
                    bench.cluster.post(
                        _handover_url(
                            bench, partition, clientless=True, source=source, target=target, client=CLIENT_ID
                        ),
                        timeout=120,
                    )
                    handovers += 1
                except ClusterError as error:
                    failures += 1
                    print(f"  handover {partition} {source}->{target} failed: {str(error)[:160]}", flush=True)
            time.sleep(float(os.environ.get("SOAK_GAP", "3")))
        if time.time() >= next_dump:
            tag = f"t{int(time.time() - started):04d}"
            row = [gcdump(bench, name, tag) for name in NODES]
            dumps += row
            print(json.dumps({"handovers": handovers, "failures": failures, "dumps": row}), flush=True)
            next_dump += DUMP_EVERY
    if os.environ.get("SOAK_FULLDUMP"):
        tool = str(Path(GCDUMP).with_name("dotnet-dump"))
        for name in NODES:
            pid = node_pid(bench, name)
            if pid:
                subprocess.run(
                    [tool, "collect", "-p", str(pid), "--type", "Full", "-o", str(OUT / f"{name}.dmp")],
                    capture_output=True,
                    timeout=900,
                )
    summary = {"handovers": handovers, "failures": failures, "dumps": dumps}
    bench.record("soak", summary)
    (OUT / "soak.json").write_text(json.dumps(summary, indent=2))
    bench.exercised(handovers=handovers, failures=failures)


if __name__ == "__main__" and os.environ.get("SOAK_MODE") == "antenna":
    result = run_scenario(soak_antenna, "SE1149-SOAK-ANTENNA", "node heap under antenna proxy churn", "3", OUT)
    print("PASSED" if result.passed else f"FAILED {result.error}", flush=True)
elif __name__ == "__main__" and os.environ.get("SOAK_MODE") == "client":
    result = run_scenario(soak_client, "SE1149-SOAK-CLIENT", "node heap under client traversals", "3", OUT)
    print("PASSED" if result.passed else f"FAILED {result.error}", flush=True)
elif __name__ == "__main__":
    result = run_scenario(soak, "SE1149-SOAK", "node heap under handovers", "3", OUT)
    print("PASSED" if result.passed else f"FAILED {result.error}", flush=True)
