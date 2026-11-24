"""Compare a Workshop mod's changes to game definitions on standalone and clustered servers."""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import zipfile
from pathlib import Path
from typing import NamedTuple

from bench import Bench, HOST
from cluster import ClusterError
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
    "sound": Case(
        "1662953858", ("welder", "drill", "rain"), (0.5, 0.27, 0.2), (0.15, 0.06, 0.08)
    ),
}


def stage(source, mods):
    target = mods / f"{source.name}.sbm"
    archives = list(source.glob("*_legacy.bin"))
    if not archives:
        shutil.copytree(source, target)
        return
    if len(archives) != 1:
        raise ValueError(f"Expected one Workshop archive in {source}")
    with zipfile.ZipFile(archives[0]) as archive:
        for entry in archive.infolist():
            path = Path(entry.filename.replace("\\", "/"))
            if path.is_absolute() or ".." in path.parts:
                raise ValueError(f"Invalid archive member {entry.filename}")
            if entry.is_dir() or entry.filename.endswith("\\"):
                continue
            out = target / path
            out.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as src, out.open("wb") as dest:
                shutil.copyfileobj(src, dest)


def run(corpus, results, case_name):
    case = CASES[case_name]
    os.environ["MOD_DEFINITION_CASE"] = case_name
    marker = "mod_definition_probe " + " ".join(
        field + r"=([^\s]+)" for field in case.fields
    )

    def sample(bench, node):
        line = bench.cluster.wait_node_log(node, marker, 30)
        return [float(value) for value in re.search(marker, line).groups()]

    project = Path(__file__).parent / "ModDefinitionProbe"
    # The normal bench observer installs cluster control routes; this lane only reads a game value.
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench(
        "MOD-" + case_name.upper(), "Modded " + case_name + " definitions", "2", results
    )
    started = time.monotonic()
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        bundle = work / "ModDefinitionProbe"
        subprocess.run(
            [
                "dotnet",
                "build",
                str(project / "ModDefinitionProbe.csproj"),
                "-c",
                "Release",
                "-o",
                str(bundle),
            ],
            check=True,
        )
        (work / "no-mods").mkdir()
        stage(corpus / "244850" / case.workshop, work / "Mods")
        scripts = sorted((work / "Mods").rglob("*.cs"))
        if not scripts:
            raise ValueError("Workshop item has no script")
        source_hashes = {
            str(p.relative_to(work / "Mods")): hashlib.sha256(
                p.read_bytes()
            ).hexdigest()
            for p in scripts
        }
        vanilla = HOST.package / "examples/Cluster-Simple"
        world = work / "ModWorld"
        shutil.copytree(vanilla, world)
        for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = world / filename
            item = f'<Mods><ModItem FriendlyName="{case.workshop}"><Name>{case.workshop}.sbm</Name><PublishedFileId>{case.workshop}</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem></Mods>'
            text, count = re.subn(r"<Mods\s*/>", item, path.read_text())
            if count != 1:
                raise ValueError(f"{filename} must contain a single empty Mods element")
            path.write_text(text)

        config = bench.cluster.config
        config.world_template = world
        for role in ("node", "wa"):
            config.extra_plugins[role].append(bundle)
        samples = {}
        try:
            bench.cluster.prepare(wipe=True)
            for name, stock_world, mods in (
                ("control", vanilla, work / "no-mods"),
                ("stock", world, work / "Mods"),
            ):
                server = start_standalone(
                    bench,
                    stock_world,
                    name=name,
                    ready=("Game ready",),
                    local_plugins=[bundle],
                    staged_mods=mods,
                    extra_env={"MOD_DEFINITION_STAGE_STOCK": "1"},
                    timeout=180,
                )
                try:
                    samples[name] = sample(bench, name)
                finally:
                    server.stop()
            for role in ("wa-1", "node-1", "node-2"):
                shutil.copytree(
                    work / "Mods",
                    config.root / f"{role}-data" / "Mods",
                    dirs_exist_ok=True,
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            for role in ("wa-1", "node-1", "node-2"):
                samples[role] = sample(bench, role)
            if (
                tuple(samples["stock"]) != case.expected
                or tuple(samples["control"]) != case.vanilla
            ):
                return "INCONCLUSIVE"
            return (
                "PASS"
                if all(
                    samples[node] == samples["stock"]
                    for node in ("wa-1", "node-1", "node-2")
                )
                else "DIFF"
            )
        finally:
            bench.record(
                "observations",
                {
                    "mod": case.workshop,
                    "fields": case.fields,
                    "samples": samples,
                    "scripts_sha256": source_hashes,
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
    except (
        OSError,
        ValueError,
        ClusterError,
        TimeoutError,
        subprocess.CalledProcessError,
    ) as error:
        (args.results / "error.txt").write_text(str(error) + "\n")
        verdict = "INCONCLUSIVE"
    print(verdict)
    (args.results / "verdict.txt").write_text(verdict + "\n")
    return 0 if verdict == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
