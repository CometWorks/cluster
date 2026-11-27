"""Compare a Workshop mod's changes to game definitions on standalone and clustered servers."""

import argparse
import json
import os
import re
import shutil
import tempfile
import time
from pathlib import Path
from typing import NamedTuple
from xml.sax.saxutils import escape

from bench import Bench, HOST
from mod_staging import FAILURES, NODES, build_plugin, digest, stage, world_with_mods
from scenarios_planet import start_standalone


class Case(NamedTuple):
    workshop: str
    fields: tuple[str, ...]
    vanilla: tuple[float, ...]
    expected: tuple[float, ...]


CASES = {
    "speed": Case(
        "380165093", ("large", "small"), (100, 100), (150_000_000, 150_000_000)
    ),
    "speed-500": Case("379815146", ("large", "small"), (100, 100), (500, 500)),
    "speed-configured": Case("599536562", ("large", "small"), (100, 100), (850, 420)),
    "sound": Case(
        "1662953858", ("welder", "drill", "rain"), (0.5, 0.27, 0.2), (0.15, 0.06, 0.08)
    ),
}


def configure_ship_speeds(world, large, small):
    # The mod reads a serialized string from MyAPIGateway.Utilities.GetVariable.
    path = world / "Sandbox.sbc"
    settings = (
        "<MidspaceEnvironmentComponent>"
        f"<LargeShipMaxSpeed>{large}</LargeShipMaxSpeed>"
        f"<SmallShipMaxSpeed>{small}</SmallShipMaxSpeed>"
        "</MidspaceEnvironmentComponent>"
    )
    entry = (
        "<variables><dictionary><item><Key>MidspaceEnvironmentComponent</Key>"
        f'<Value xsi:type="xsd:string">{escape(settings)}</Value>'
        "</item></dictionary></variables>"
    )
    text, count = re.subn(
        r"<variables>\s*<dictionary\s*/>\s*</variables>", entry, path.read_text()
    )
    if count != 1:
        raise ValueError(f"Expected one empty script variable dictionary in {path}")
    path.write_text(text)


def run(corpus, results, case_name):
    case = CASES[case_name]
    os.environ["MOD_DEFINITION_CASE"] = case_name
    marker = "mod_definition_probe " + " ".join(
        field + r"=([^\s]+)" for field in case.fields
    )

    def sample(bench, node):
        line = bench.cluster.wait_node_log(node, marker, 30)
        return [float(value) for value in re.search(marker, line).groups()]

    # The normal bench observer installs cluster control routes; this lane only reads a game value.
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench(
        "MOD-" + case_name.upper(), "Modded " + case_name + " definitions", "2", results
    )
    started = time.monotonic()
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        bundle = build_plugin("ModDefinitionProbe", work)
        staging = build_plugin("ModStaging", work)
        (work / "no-mods").mkdir()
        stage(corpus / "244850" / case.workshop, work / "Mods")
        revision = digest(work / "Mods" / f"{case.workshop}.sbm")
        if not revision["scripts"]:
            raise ValueError("Workshop item has no script")
        vanilla = HOST.package / "examples/Cluster-Simple"
        world = work / "ModWorld"
        world_with_mods(vanilla, world, [case.workshop])
        control_world = vanilla
        if case_name == "speed-configured":
            control_world = work / "ControlWorld"
            shutil.copytree(vanilla, control_world)
            configure_ship_speeds(control_world, *case.expected)
            configure_ship_speeds(world, *case.expected)

        config = bench.cluster.config
        config.world_template = world
        for role in ("node", "wa"):
            config.extra_plugins[role].append(bundle)
        samples = {}
        try:
            bench.cluster.prepare(wipe=True)
            for name, stock_world, mods in (
                ("control", control_world, work / "no-mods"),
                ("stock", world, work / "Mods"),
            ):
                server = start_standalone(
                    bench,
                    stock_world,
                    name=name,
                    ready=("Game ready",),
                    local_plugins=[staging, bundle],
                    staged_mods=mods,
                    timeout=180,
                )
                try:
                    samples[name] = sample(bench, name)
                finally:
                    server.stop()
            for role in NODES:
                shutil.copytree(
                    work / "Mods",
                    config.root / f"{role}-data" / "Mods",
                    dirs_exist_ok=True,
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            for role in NODES:
                samples[role] = sample(bench, role)
            if (
                tuple(samples["stock"]) != case.expected
                or tuple(samples["control"]) != case.vanilla
            ):
                return "INCONCLUSIVE"
            return (
                "PASS"
                if all(samples[node] == samples["stock"] for node in NODES)
                else "DIFF"
            )
        finally:
            bench.record(
                "observations",
                {
                    "mod": case.workshop,
                    "fields": case.fields,
                    "samples": samples,
                    "revision": revision,
                    "package": json.loads((HOST.package / "manifest.json").read_text()),
                    "seconds": round(time.monotonic() - started, 1),
                },
            )
            bench.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("corpus", type=Path)
    ap.add_argument("results", type=Path, help="new results folder")
    ap.add_argument("--case", choices=CASES, default="speed")
    args = ap.parse_args()
    args.results.mkdir(parents=True)
    try:
        verdict = run(args.corpus, args.results, args.case)
    except FAILURES as error:
        (args.results / "error.txt").write_text(str(error) + "\n")
        verdict = "INCONCLUSIVE"
    print(verdict)
    (args.results / "verdict.txt").write_text(verdict + "\n")
    return 0 if verdict == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
