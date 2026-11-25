"""Request a named MES encounter once and find the resulting grid on standalone and clustered servers."""

import argparse
import base64
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
from cluster import ClusterError, wait_until
from mod_definition_differential import build_observer, stage, world_with_mods
from scenarios import _force_handover
from scenarios_ext import _gate_residuals
from scenarios_planet import start_standalone

MES = "1521905890"
PROBE = "9991845001"
NODES = ("wa-1", "node-1", "node-2")


def command(bench, node, phase):
    path = bench.cluster.config.root / f"{node}-data/Mods/{PROBE}.sbm/{phase}.txt"
    path.touch(exist_ok=False)


def measure(bench, nodes, issuer):
    cluster = bench.cluster

    def snapshot(phase):
        for node in nodes:
            command(bench, node, phase)
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
    command(bench, issuer, "spawn")
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


def npc_state(bench, node, phase):
    started = time.monotonic()
    command(bench, node, phase)
    pattern = rf"mes_encounter_probe state={phase} grid=(\d+) origin=([^ ]+) terminal=(True|False) group=(\S+) prefab=(\S+) stored=(\S+) spawned=(True|False) npc=(\S+)"
    line = bench.cluster.wait_node_log(node, pattern, 80)
    grid, origin, terminal, group, prefab, stored, spawned, payload = re.search(
        pattern, line
    ).groups()
    data = base64.b64decode(payload, validate=True) if payload != "-" else b""
    return {
        "node": node,
        "phase": phase,
        "sample_seconds": round(time.monotonic() - started, 1),
        "grid": grid,
        "spawn_origin": [float(n) for n in origin.split(",")],
        "terminal_marker": terminal == "True",
        "spawn_group": group,
        "prefab": prefab,
        "stored_origin": [float(n) for n in stored.split(",")],
        "spawned_by_mes": spawned == "True",
        "npc_bytes": len(data),
        "npc_sha256": hashlib.sha256(data).hexdigest() if data else None,
    }


def round_trip(bench, report):
    stock = report["standalone"]["initial"]
    grid = report["cluster"]["after"]["node-1"]["grids"][0]
    stages = report["cluster"]["handover"] = []
    initial = npc_state(bench, "node-1", "initial")
    stages.append(initial)

    def owner(node):
        places = bench.entity_owner(bench.diag("entities"), int(grid))
        return places[0][1] if len(places) == 1 and places[0][0] == node else None

    def matches(state):
        return (
            state["grid"] == grid
            and state["terminal_marker"]
            and state["npc_bytes"] > 0
            and all(
                state[field] == stock[field]
                for field in (
                    "spawn_origin",
                    "stored_origin",
                    "spawn_group",
                    "prefab",
                    "spawned_by_mes",
                )
            )
        )

    control = npc_state(bench, "node-1", "control")
    stages.append(control)
    if not matches(initial) or not matches(control):
        report["error"] = (
            "NPC state already differs on the issuing node before any transfer"
        )
        return "INCONCLUSIVE"
    valid = True
    for source, target, phase in (
        ("node-1", "node-2", "away"),
        ("node-2", "node-1", "returned"),
    ):
        partition = wait_until(
            lambda: owner(source), 35, f"unique owner {source} for {grid}"
        )
        _force_handover(bench, source, target, partition)
        wait_until(lambda: owner(target), 45, f"unique owner {target} for {grid}")
        state = npc_state(bench, target, phase)
        state["partition"] = partition
        state["gate_residuals"] = _gate_residuals(bench, target, partition)
        stages.append(state)
        valid = valid and matches(state) and state["gate_residuals"] == 0
    return "PASS" if valid else "DIFF"


def run(corpus, results, handover=False, encounter=None):
    os.environ["CLUSTER_TEST_PLUGIN"] = "0"
    bench = Bench("MOD-MES-SPAWN", "MES custom encounter spawn", "2", results)
    started = time.monotonic()
    report = {
        "mod": MES,
        "encounter": encounter,
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
            ids = [MES, PROBE]
            if encounter:
                # A real encounter mod's spawn group, requested as a random encounter owned by SPRT like the fixture
                mod, group = encounter.split(":", 1)
                stage(corpus / "244850" / mod, mods)
                (mods / f"{PROBE}.sbm/encounter.txt").write_text(
                    f"{group} SPRT RandomEncounter\n"
                )
                ids.insert(1, mod)
                revision = hashlib.sha256()
                for path in sorted((mods / f"{mod}.sbm").rglob("*")):
                    if path.is_file():
                        revision.update(
                            path.relative_to(mods).as_posix().encode() + b"\0"
                        )
                        revision.update(hashlib.sha256(path.read_bytes()).digest())
                report["encounter_sha256"] = revision.hexdigest()
            world = work / "EncounterWorld"
            world_with_mods(HOST.package / "examples/Cluster-Simple", world, ids)
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
                if handover and report["standalone"]["spawned_once"]:
                    baseline = npc_state(bench, "stock", "initial")
                    report["standalone"]["initial"] = baseline
                    group, prefab = (
                        (encounter.split(":", 1)[1], baseline["prefab"])
                        if encounter
                        else ("ClusterProbe-Encounter", "ClusterProbe-Ship")
                    )
                    if (
                        baseline["spawn_origin"] != [120.0, 70.0, 110.0]
                        or baseline["spawn_group"] != group
                        or baseline["prefab"] in ("-", "")
                        or baseline["prefab"] != prefab
                        or baseline["stored_origin"] != baseline["spawn_origin"]
                        or not baseline["spawned_by_mes"]
                        or baseline["npc_bytes"] == 0
                        or not baseline["terminal_marker"]
                        or baseline["grid"]
                        != report["standalone"]["after"]["stock"]["grids"][0]
                    ):
                        raise ValueError(
                            "Standalone MES did not establish the NPC state and terminal marker"
                        )
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
            if handover:
                bench.cluster.post("/automatic/pause")
            report["cluster"] = measure(bench, NODES, "node-1")
            report["result"] = "PASS" if report["cluster"]["spawned_once"] else "DIFF"
            if handover and report["cluster"]["spawned_once"]:
                report["result"] = "INCONCLUSIVE"
                report["result"] = round_trip(bench, report)
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
    parser.add_argument(
        "--handover",
        action="store_true",
        help="move the encounter and its MES state across both nodes and back",
    )
    parser.add_argument(
        "--encounter",
        metavar="MOD:GROUP",
        help="request this spawn group of a Workshop encounter mod instead of the fixture's",
    )
    args = parser.parse_args()
    args.results.mkdir(parents=True)
    verdict = run(args.corpus, args.results, args.handover, args.encounter)
    print(verdict)
    raise SystemExit(0 if verdict == "PASS" else 1)
