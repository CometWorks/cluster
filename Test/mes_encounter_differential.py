"""Request a named MES encounter once and find the resulting grid on standalone and clustered servers."""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
from pathlib import Path

from bench import Bench, HOST
from cluster import ClusterError
from mod_definition_differential import build_observer, stage, world_with_mods
from scenarios_planet import start_standalone

MES = "1521905890"
PROBE = "9991845001"
NODES = ("wa-1", "node-1", "node-2")


def measure(bench, nodes, issuer):
    cluster = bench.cluster

    def touch(node, phase):
        (cluster.config.root / f"{node}-data/Mods/{PROBE}.sbm/{phase}.txt").touch(
            exist_ok=False
        )

    def snapshot(phase):
        for node in nodes:
            touch(node, phase)
        rows = {}
        pattern = rf"mes_encounter_probe {phase}=([-0-9,]+) api=(True|False)"
        for node in nodes:
            line = cluster.wait_node_log(node, pattern, 30)
            ids, present = re.search(pattern, line).groups()
            rows[node] = {
                "api": present == "True",
                "grids": [] if ids == "-" else ids.split(","),
            }
        return rows

    before = snapshot("before")
    if any(r["grids"] for r in before.values()):
        raise ValueError("The fixture marker was present before the request")
    touch(issuer, "spawn")
    pattern = r"mes_encounter_probe accepted=(True|False)"
    accepted = (
        re.search(pattern, cluster.wait_node_log(issuer, pattern, 30))[1] == "True"
    )
    callback = r"mes_encounter_probe callback=(\d+)"
    if accepted:
        try:
            cluster.wait_node_log(issuer, callback, 25)
        except ClusterError as error:
            # Read the actual grid census even when MES accepted the request but never called back.
            if f"no line matching /{callback}/" not in str(error):
                raise
    after = snapshot("after")
    callbacks = {}
    for node in nodes:
        logs = list(
            (cluster.config.root / f"{node}-data").glob("SpaceEngineersDedicated_*.log")
        )
        if len(logs) != 1:
            raise ValueError(f"Expected one game log for {node}, got {len(logs)}")
        callbacks[node] = re.findall(callback, logs[0].read_text(errors="replace"))
    live = [gid for r in after.values() for gid in r["grids"]]
    announced = [gid for grid_ids in callbacks.values() for gid in grid_ids]
    return {
        "issuer": issuer,
        "before": before,
        "accepted": accepted,
        "after": after,
        "callbacks": callbacks,
        "spawned_once": accepted and len(live) == 1 and announced == live,
    }


def run(corpus, results):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("MOD-MES-SPAWN", "MES custom encounter spawn", "2", results)
    started = time.monotonic()
    report = {
        "mod": MES,
        "package": json.loads((HOST.package / "manifest.json").read_text()),
        "result": "INCONCLUSIVE",
    }
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        try:
            mods = work / "Mods"
            stage(corpus / "244850" / MES, mods)
            mes_files = mods / f"{MES}.sbm"
            scripts = sorted((mes_files / "Data/Scripts").rglob("*.cs"))
            if not scripts:
                raise ValueError(f"No MES scripts found in {mes_files}")
            revision = hashlib.sha256()
            for script in scripts:
                revision.update(
                    script.relative_to(mes_files).as_posix().encode() + b"\0"
                )
                revision.update(hashlib.sha256(script.read_bytes()).digest())
            report["mes_script_count"] = len(scripts)
            report["mes_scripts_sha256"] = revision.hexdigest()
            shutil.copytree(
                Path(__file__).parent / "MesEncounterFixture", mods / f"{PROBE}.sbm"
            )
            world = work / "EncounterWorld"
            world_with_mods(
                HOST.package / "examples/Cluster-Simple", world, [MES, PROBE]
            )
            config = bench.cluster.config
            config.world_template = world
            bench.cluster.prepare(wipe=True)
            observer = build_observer(work)
            stock = start_standalone(
                bench,
                world,
                ready=("Game ready",),
                local_plugins=[observer],
                staged_mods=mods,
                extra_env={"MOD_DEFINITION_STAGE_STOCK": "1"},
                timeout=180,
            )
            try:
                report["standalone"] = measure(bench, ["stock"], "stock")
            finally:
                stock.stop()
            if not report["standalone"]["spawned_once"]:
                return report["result"]
            for node in NODES:
                shutil.copytree(
                    mods, config.root / f"{node}-data/Mods", dirs_exist_ok=True
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            report["cluster"] = measure(bench, NODES, "node-1")
            report["result"] = "PASS" if report["cluster"]["spawned_once"] else "DIFF"
        except (
            ValueError,
            OSError,
            ClusterError,
            TimeoutError,
            subprocess.CalledProcessError,
        ) as error:
            report["error"] = str(error)
        finally:
            report["seconds"] = round(time.monotonic() - started, 1)
            bench.record("observations", report)
            bench.close()
    return report["result"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    args.results.mkdir(parents=True)
    verdict = run(args.corpus, args.results)
    print(verdict)
    raise SystemExit(0 if verdict == "PASS" else 1)
