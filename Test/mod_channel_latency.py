"""Measure a native entity event and explicit mod-channel notifications and reads."""

import argparse
import json
import math
import os
import re
import shutil
import statistics
import tempfile
import time
from pathlib import Path

from bench import Bench, HOST
from cluster import wait_until
from mod_definition_differential import world_with_mods

PROBE = "9991845011"
SAMPLES = 16
READS = 16384
OWNER = "node-1"
SUBSCRIBER = "node-2"


def summary(values):
    return {
        "count": len(values),
        "p50_us": statistics.median(values),
        "p95_us": sorted(values)[math.ceil(0.95 * len(values)) - 1],
        "max_us": max(values),
    }


def summarize(raw):
    metrics = {}
    for phase in ("local", "snapshot", "owner", "cached", "batch", "event"):
        rows = raw[phase]
        if [int(r[1]) for r in rows] != list(range(SAMPLES)):
            raise ValueError(f"Missing, duplicate or out-of-order sample in {phase}")
        if phase in ("local", "snapshot"):
            if any(int(r[4]) != 36 * (READS // 8) for r in rows):
                raise ValueError(f"Unexpected sampled property values in {phase}")
            metrics[phase] = summary(
                [(int(r[3]) - int(r[2])) / (10 * READS) for r in rows]
            )
            continue
        if phase == "event":
            if any(
                r[2] != ("True" if i % 2 else "False")
                or r[8] != OWNER
                or r[6] != "True"
                for i, r in enumerate(rows)
            ):
                raise ValueError("Unexpected event value or sending node")
            names = (
                "setter_to_callback",
                "callback_to_send",
                "send_to_receiver",
                "setter_to_receiver",
            )
            stamps = [list(map(int, (r[3], r[4], r[5], r[7]))) for r in rows]
        else:
            for i, r in enumerate(rows):
                values = [
                    "x" * ((i + j) % 8 + 1) for j in range(8 if phase == "batch" else 1)
                ]
                if r[6] != OWNER or r[7].split(",") != values:
                    raise ValueError(
                        f"{phase} returned the wrong owner or grid properties"
                    )
            names = (
                "request_to_owner",
                "owner_service",
                "reply_to_requester",
                "round_trip",
            )
            stamps = [list(map(int, r[2:6])) for r in rows]
        if any(t != sorted(t) for t in stamps):
            raise ValueError(
                f"{phase} timestamps went backwards; cannot compare host clocks"
            )
        metrics[phase] = {
            n: summary([(t[b] - t[a]) / 10 for t in stamps])
            for n, (a, b) in zip(names, ((0, 1), (1, 2), (2, 3), (0, 3)))
        }
    return metrics


def run(results):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("MOD-CHANNEL-LATENCY", "Mod entity reads and events", "2", results)
    cluster = bench.cluster
    root = cluster.config.root
    report = {
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
        "clock": "UTC ticks on one host",
        "raw": {},
    }
    commands = {OWNER: 0, SUBSCRIBER: 0}

    def stage(node):
        return root / f"{node}-data/Mods/{PROBE}.sbm"

    def command(node, value):
        file = stage(node) / f"step{commands[node]}.txt"
        temp = file.with_suffix(".new")
        temp.write_text(value)
        temp.replace(file)
        commands[node] += 1

    def samples(node, phase):
        cluster.wait_node_log(node, rf"channel_latency {phase}\|15\|", 45)
        log = max(
            (root / f"{node}-data").glob("SpaceEngineersDedicated_*.log"),
            key=lambda p: p.stat().st_mtime,
        )
        return [
            m.group(1).split("|")
            for line in log.read_text(errors="replace").splitlines()
            if (m := re.search(rf"channel_latency ({phase}\|.*)$", line))
        ]

    try:
        with tempfile.TemporaryDirectory(dir=results) as tmp:
            world = Path(tmp) / "world"
            world_with_mods(HOST.package / "examples/Cluster-Simple", world, [PROBE])
            cluster.config.world_template = world
            cluster.prepare(wipe=True)
            for node in ("wa-1", OWNER, SUBSCRIBER):
                shutil.copytree(
                    Path(__file__).parent / "ModChannelLatencyFixture", stage(node)
                )
            cluster.start()
            cluster.wait_ready(timeout=230, serving=True)
            cluster.post("/automatic/pause")
            command(OWNER, "spawn")
            match = r"channel_latency spawn\|(\d+)"
            grid = int(re.search(match, cluster.wait_node_log(OWNER, match, 35))[1])

            def owned():
                places = bench.entity_owner(bench.diag("entities"), grid)
                return places if len(places) == 1 and places[0][0] == OWNER else None

            report["grid"] = grid
            report["ownership"] = wait_until(owned, 45, "fixture owned by node-1")
            command(OWNER, "local")
            for phase in ("local", "snapshot"):
                report["raw"][phase] = samples(OWNER, phase)
            command(SUBSCRIBER, f"resolve|{grid}")
            match = r"channel_latency resolved\|node-1\|(\d+)\|(\d+)"
            resolved = re.search(match, cluster.wait_node_log(SUBSCRIBER, match, 30))
            report["owner_lookup_us"] = (int(resolved[2]) - int(resolved[1])) / 10
            for phase in ("owner", "cached", "batch", "event"):
                if phase == "event":
                    command(OWNER, "events")
                else:
                    command(SUBSCRIBER, f"{phase}|{grid}")
                report["raw"][phase] = samples(SUBSCRIBER, phase)
                if phase != "event":
                    # Let the real 30-sends/10s channel quota clear between workloads.
                    time.sleep(10.2)
            report["metrics"] = summarize(report["raw"])
            report["result"] = "PASS"
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        bench.record("observations", report)
        bench.close()
        print(json.dumps({k: v for k, v in report.items() if k != "raw"}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    run(parser.parse_args().results)
