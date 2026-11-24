"""Compare Midspace's Ship Speed Unlimited on standalone Magnetar and a two-node cluster."""

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

from bench import Bench, HOST
from scenarios_planet import start_standalone

MOD = "380165093"
EXPECTED = (150_000_000, 150_000_000)
MARKER = r"mod_speed_probe large=([^\s]+) small=([^\s]+)"


def stage(source, mods):
    target = mods / f"{MOD}.sbm"
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


def run(corpus, results):
    project = Path(__file__).parent / "ModSpeedProbe"
    # The normal bench observer installs cluster control routes; this lane only reads a game value.
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("MOD-SPEED", "Midspace speed limits", "2", results)
    started = time.monotonic()
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        bundle = work / "ModSpeedProbe"
        subprocess.run(
            [
                "dotnet",
                "build",
                str(project / "ModSpeedProbe.csproj"),
                "-c",
                "Release",
                "-o",
                str(bundle),
            ],
            check=True,
        )
        stage(corpus / "244850" / MOD, work / "Mods")
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
        world = work / "SpeedWorld"
        shutil.copytree(vanilla, world)
        for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = world / filename
            item = f'<Mods><ModItem FriendlyName="Midspace Ship Speed"><Name>{MOD}.sbm</Name><PublishedFileId>{MOD}</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem></Mods>'
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
                ("control", vanilla, None),
                ("stock", world, work / "Mods"),
            ):
                server = start_standalone(
                    bench,
                    stock_world,
                    name=name,
                    ready=("Game ready",),
                    local_plugins=[bundle],
                    staged_mods=mods,
                    extra_env={"MOD_SPEED_STAGE_STOCK": "1"},
                    timeout=180,
                )
                try:
                    match = re.search(
                        MARKER, bench.cluster.wait_node_log(name, MARKER, 30)
                    )
                    samples[name] = [float(x) for x in match.groups()]
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
                samples[role] = [
                    float(x)
                    for x in re.search(
                        MARKER, bench.cluster.wait_node_log(role, MARKER, 30)
                    ).groups()
                ]
            if tuple(samples["stock"]) != EXPECTED or any(
                c == m for c, m in zip(samples["control"], EXPECTED)
            ):
                return "INCONCLUSIVE"
            return (
                "PASS"
                if all(
                    p == samples["stock"] for n, p in samples.items() if n != "control"
                )
                else "DIFF"
            )
        finally:
            bench.record(
                "observations",
                {
                    "mod": MOD,
                    "samples": samples,
                    "scripts_sha256": source_hashes,
                    "package": json.loads((HOST.package / "manifest.json").read_text()),
                    "seconds": round(time.monotonic() - started, 1),
                },
            )
            bench.close()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("corpus", type=Path)
    ap.add_argument("results", type=Path, help="new results folder")
    args = ap.parse_args()
    args.results.mkdir(parents=True)
    verdict = run(args.corpus, args.results)
    print(verdict)
    (args.results / "verdict.txt").write_text(verdict + "\n")
    raise SystemExit(0 if verdict == "PASS" else 1)
