"""Shared helpers for the Workshop mod tests: stage pinned mods, build worlds, drive fixture mods,
and run the same measurement on a standalone server and a cluster (run_staged)."""

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
from cluster import ClusterError
from scenarios_planet import start_standalone

NODES = ("wa-1", "node-1", "node-2")
FAILURES = (
    OSError,
    ValueError,
    TimeoutError,
    ClusterError,
    subprocess.CalledProcessError,
)


def stage(source, mods):
    """Copy or unpack one Workshop item from the corpus into ``mods/<id>.sbm``."""
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


def digest(folder):
    """Identify a staged mod's revision: file and script counts and one SHA-256 over every file."""
    files = sorted(p for p in folder.rglob("*") if p.is_file())
    total = hashlib.sha256()
    for path in files:
        total.update(path.relative_to(folder).as_posix().encode() + b"\0")
        total.update(hashlib.sha256(path.read_bytes()).digest())
    return {
        "files": len(files),
        "scripts": sum(p.suffix == ".cs" for p in files),
        "sha256": total.hexdigest(),
    }


def build_plugin(name, work):
    """Build a test plugin project under Test/ into ``work/<name>`` and return that folder."""
    bundle = work / name
    project = Path(__file__).parent / name / f"{name}.csproj"
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "-o", str(bundle)],
        check=True,
    )
    return bundle


def world_with_mods(vanilla, world, ids):
    """Copy ``vanilla`` to ``world`` and list the staged mods in both of its world files."""
    shutil.copytree(vanilla, world)
    items = "".join(
        f'<ModItem FriendlyName="{mod}"><Name>{mod}.sbm</Name><PublishedFileId>{mod}</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>'
        for mod in ids
    )
    for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / filename
        text, count = re.subn(r"<Mods\s*/>", f"<Mods>{items}</Mods>", path.read_text())
        if count != 1:
            raise ValueError(f"{filename} must contain a single empty Mods element")
        path.write_text(text)


def command(bench, node, probe, name, value=""):
    """Give a fixture mod a command: write ``<name>.txt`` into its staged folder on ``node``.

    Mods cannot delete files, so a fixture remembers which commands it has run, and each name is
    used once per run. The file is written whole and then renamed, so the mod never reads it half
    written."""
    path = bench.cluster.config.root / f"{node}-data/Mods/{probe}.sbm/{name}.txt"
    if path.exists():
        raise ValueError(f"Command {name} was already given to {node}")
    temp = path.with_suffix(".new")
    temp.write_text(value)
    temp.replace(path)


def run_staged(
    corpus,
    results,
    *,
    scenario,
    title,
    mods,
    probe,
    fixture,
    measure,
    configure=None,
):
    """Measure a fixture on a standalone server, then on the cluster if standalone PASSes.

    ``mods`` are the Workshop items staged from ``corpus``, ``fixture`` is the Test/ folder of the
    fixture mod staged as ``probe``. ``configure(world, staged)`` may edit the world and the staged
    mods folder before either server starts. ``measure(bench, roles, issuer, reference)`` runs once
    with ``(["stock"], "stock", None)`` and once with ``(NODES, "node-1", <standalone result>)``
    and returns a dict with a ``result`` of PASS, DIFF or INCONCLUSIVE."""
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench(scenario, title, "2", results)
    started = time.monotonic()
    report = {
        "mods": {},
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
    }
    with tempfile.TemporaryDirectory(dir=results) as temp:
        work = Path(temp)
        try:
            staged = work / "Mods"
            for mod in mods:
                stage(corpus / "244850" / mod, staged)
                report["mods"][mod] = digest(staged / f"{mod}.sbm")
            shutil.copytree(Path(__file__).parent / fixture, staged / f"{probe}.sbm")
            world = work / "World"
            world_with_mods(
                HOST.package / "examples/Cluster-Simple", world, [*mods, probe]
            )
            if configure is not None:
                configure(world, staged)
            config = bench.cluster.config
            config.world_template = world
            bench.cluster.prepare(wipe=True)
            stock = start_standalone(
                bench,
                world,
                ready=("Game ready",),
                local_plugins=[build_plugin("ModStaging", work)],
                staged_mods=staged,
                timeout=180,
            )
            try:
                reference = report["standalone"] = measure(
                    bench, ["stock"], "stock", None
                )
            finally:
                stock.stop()
            if reference["result"] != "PASS":
                return report
            for role in NODES:
                shutil.copytree(
                    staged, config.root / f"{role}-data/Mods", dirs_exist_ok=True
                )
            bench.cluster.start()
            bench.cluster.wait_ready(timeout=240, serving=True)
            bench.cluster.post("/automatic/pause")
            actual = report["cluster"] = measure(bench, NODES, "node-1", reference)
            report["result"] = actual["result"]
        except FAILURES as exc:
            report["error"] = str(exc)
        finally:
            report["seconds"] = round(time.monotonic() - started, 1)
            bench.record("observations", report)
            bench.close()
    return report


def main(run, doc):
    """Command line of a ``run_staged`` test: CORPUS RESULTS, prints and exits with the verdict."""
    parser = argparse.ArgumentParser(description=doc)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("results", type=Path, help="new results folder")
    args = parser.parse_args()
    args.results.mkdir(parents=True)
    result = run(args.corpus, args.results)["result"]
    print(result)
    raise SystemExit(0 if result == "PASS" else 1)
