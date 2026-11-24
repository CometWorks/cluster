"""Compare a real Advanced Welding pad merge on standalone and cluster."""

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

MOD = "510790477"
PROBE = "9991845003"
NODES = ("wa-1", "node-1", "node-2")


def command(bench, role, phase):
    path = bench.cluster.config.root / f"{role}-data/Mods/{PROBE}.sbm/{phase}.txt"
    path.touch(exist_ok=False)


def sample(bench, roles, phase):
    samples = {}
    for role in roles:
        command(bench, role, phase)
        pattern = rf"weld_pad_probe {phase} markers=([\w:,-]+) pads=([\w:,-]+)"
        found = re.search(pattern, bench.cluster.wait_node_log(role, pattern, 30))
        markers, pads = found.groups()
        samples[role] = {
            "markers": (
                []
                if markers == "-"
                else [item.split(":") for item in markers.split(",")]
            ),
            "pads": (
                [] if pads == "-" else [item.split(":") for item in pads.split(",")]
            ),
        }
    return samples


def apart(snapshot, issuer):
    owner = snapshot[issuer]
    markers, pads = owner["markers"], owner["pads"]
    grids = {grid for _, _, grid in markers}
    return (
        len(grids) == 2
        and [name for name, _, _ in markers] == ["A", "B"]
        and len({block for _, block, _ in markers}) == 2
        and len(pads) == 2
        and {grid for _, grid, _ in pads} == grids
        and all(working == "True" for _, _, working in pads)
        and all(
            not x["markers"] and not x["pads"]
            for role, x in snapshot.items()
            if role != issuer
        )
    )


def merged(reference, observed, issuer):
    before = reference[issuer]["markers"]
    markers = observed[issuer]["markers"]
    owners = {grid for _, _, grid in markers}
    return (
        [(name, block) for name, block, _ in before]
        == [(name, block) for name, block, _ in markers]
        and len(owners) == 1
        and owners <= {grid for _, _, grid in before}
        and all(not row["pads"] for row in observed.values())
        and all(not row["markers"] for role, row in observed.items() if role != issuer)
    )


def measure(bench, roles, issuer):
    # Read peers after the owner has completed its simulation-tick observation window.
    roles = (issuer, *(role for role in roles if role != issuer))
    command(bench, issuer, "spawn")
    initial = sample(bench, roles, "initial")
    control = sample(bench, roles, "apart")
    report = {
        "issuer": issuer,
        "initial": initial,
        "apart": control,
        "result": "INCONCLUSIVE",
    }
    if initial != control or not apart(control, issuer):
        return report
    command(bench, issuer, "align")
    observed = report["aligned"] = sample(bench, roles, "aligned")
    report["result"] = "PASS" if merged(control, observed, issuer) else "DIFF"
    return report


def run(corpus: Path, results: Path):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("MOD-WELD-PADS", "Advanced Welding pad merge", "2", results)
    started = time.monotonic()
    report = {
        "mod": MOD,
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
    }
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        try:
            mods = work / "Mods"
            stage(corpus / "244850" / MOD, mods)
            sources = (mods / f"{MOD}.sbm" / "Data/Scripts").rglob("*.cs")
            report["script_sha256"] = {
                p.relative_to(mods)
                .as_posix(): hashlib.sha256(p.read_bytes())
                .hexdigest()
                for p in sorted(sources)
            }
            shutil.copytree(
                Path(__file__).parent / "WeldPadFixture", mods / f"{PROBE}.sbm"
            )
            world = work / "WeldWorld"
            world_with_mods(
                HOST.package / "examples/Cluster-Simple", world, [MOD, PROBE]
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


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=False)
    result = run(args.corpus, args.results)
    print(result["result"])
    raise SystemExit(0 if result["result"] == "PASS" else 1)
