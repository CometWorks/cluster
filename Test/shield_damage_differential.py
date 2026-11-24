"""Compare actual armor damage with and without an Energy Shields generator."""

import argparse
import hashlib
import json
import os
import re
import shutil
import tempfile
import time
from pathlib import Path

from bench import Bench, HOST
from cluster import ClusterError
from mod_definition_differential import build_observer, stage, world_with_mods
from scenarios_planet import start_standalone

MOD = "484504816"
PROBE = "9991845002"
PATTERN = r"shield_damage_probe control=([\d.E+-]+) shielded=([\d.E+-]+) controlLoss=([\d.E+-]+) shieldLoss=([\d.E+-]+)"


def measure(bench, role):
    mods = bench.cluster.config.root / f"{role}-data/Mods"
    (mods / f"{PROBE}.sbm/start.txt").touch(exist_ok=False)
    match = re.search(PATTERN, bench.cluster.wait_node_log(role, PATTERN, 60))
    return dict(
        zip(
            ("unshielded", "shielded", "unshielded_loss", "shielded_loss"),
            map(float, match.groups()),
            strict=True,
        )
    )


def run(corpus: Path, results: Path):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("SHIELD-DAMAGE", "Energy Shields damage absorption", "2", results)
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
                Path(__file__).parent / "ShieldDamageFixture", mods / f"{PROBE}.sbm"
            )
            world = work / "ShieldWorld"
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
                reference = report["standalone"] = measure(bench, "stock")
            finally:
                stock.stop()
            if (
                reference["unshielded"] <= 0
                or reference["shielded"] != 0
                or reference["unshielded_loss"] <= 0
                or reference["shielded_loss"] != 0
            ):
                return report
            for role in ("wa-1", "node-1", "node-2"):
                shutil.copytree(
                    mods, config.root / f"{role}-data/Mods", dirs_exist_ok=True
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            actual = report["node-1"] = measure(bench, "node-1")
            report["result"] = (
                "INCONCLUSIVE"
                if actual["unshielded"] <= 0
                or actual["unshielded_loss"] <= 0
                or actual["shielded"] < 0
                else "PASS" if actual == reference else "DIFF"
            )
        except (OSError, ValueError, TimeoutError, ClusterError) as exc:
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
