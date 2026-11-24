"""Run the same Workshop and observer fixtures on standalone and two cluster nodes."""

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path

from bench import Bench, HOST
from cluster import ClusterError
from mod_definition_differential import build_observer, stage, world_with_mods
from scenarios_planet import start_standalone

NODES = ("wa-1", "node-1", "node-2")


def run_staged(
    corpus, results, scenario, title, mod, probe, fixture, measure, configure=None
):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench(scenario, title, "2", results)
    started = time.monotonic()
    report = {
        "mod": mod,
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
    }
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        try:
            mods = work / "Mods"
            stage(corpus / "244850" / mod, mods)
            sources = (mods / f"{mod}.sbm" / "Data/Scripts").rglob("*.cs")
            report["script_sha256"] = {
                p.relative_to(mods)
                .as_posix(): hashlib.sha256(p.read_bytes())
                .hexdigest()
                for p in sorted(sources)
            }
            shutil.copytree(Path(__file__).parent / fixture, mods / f"{probe}.sbm")
            world = work / "ModWorld"
            world_with_mods(
                HOST.package / "examples/Cluster-Simple", world, [mod, probe]
            )
            if configure is not None:
                configure(world)
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
            )
            try:
                reference = report["standalone"] = measure(bench, ["stock"], "stock")
            finally:
                stock.stop()
            if reference["result"] != "PASS":
                return report
            for role in NODES:
                shutil.copytree(
                    mods, config.root / f"{role}-data/Mods", dirs_exist_ok=True
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            bench.cluster.post("/automatic/pause")
            actual = report["cluster"] = measure(bench, NODES, "node-1")
            report["result"] = actual["result"]
        except (
            OSError,
            ValueError,
            TimeoutError,
            ClusterError,
            subprocess.CalledProcessError,
        ) as exc:
            report["error"] = str(exc)
        finally:
            report["seconds"] = round(time.monotonic() - started, 1)
            bench.record("observations", report)
            bench.close()
    return report
