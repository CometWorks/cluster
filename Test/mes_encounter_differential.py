"""Request a named MES encounter once and find the resulting grid on standalone and clustered servers."""

import argparse
import base64
import functools
import hashlib
import re
import time
from pathlib import Path

# mod_staging first: its bench import puts Cli/ on sys.path for cluster
from mod_staging import command, run_staged
from cluster import ClusterError, wait_until
from scenarios import force_handover
from scenarios_ext import gate_residuals

MES = "1521905890"
PROBE = "9991845001"


def spawn(bench, nodes, issuer):
    cluster = bench.cluster

    def snapshot(phase):
        for node in nodes:
            command(bench, node, PROBE, phase)
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
    command(bench, issuer, PROBE, "spawn")
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
    command(bench, node, PROBE, phase)
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


def round_trip(bench, stock, grid, encounter, stages):
    """Move the encounter grid node-1 -> node-2 -> node-1, appending each NPC state read to stages."""
    initial = npc_state(bench, "node-1", "initial")
    stages.append(initial)
    # A real encounter lands where MES's placement puts it, a different point each run: its origin is
    # compared against node-1's record of this run, the identity fields against the standalone run's.
    origin = initial if encounter else stock

    def owner(node):
        places = bench.entity_owner(bench.diag("entities"), int(grid))
        return places[0][1] if len(places) == 1 and places[0][0] == node else None

    def matches(state):
        return (
            state["grid"] == grid
            and state["terminal_marker"]
            and state["npc_bytes"] > 0
            and state["spawn_origin"]
            == state["stored_origin"]
            == origin["spawn_origin"]
            and state["spawn_origin"] != [0.0, 0.0, 0.0]
            and all(
                state[field] == stock[field]
                for field in ("spawn_group", "prefab", "spawned_by_mes")
            )
        )

    control = npc_state(bench, "node-1", "control")
    stages.append(control)
    if not matches(initial) or not matches(control):
        raise ValueError(
            "NPC state already differs on the issuing node before any transfer"
        )
    valid = True
    for source, target, phase in (
        ("node-1", "node-2", "away"),
        ("node-2", "node-1", "returned"),
    ):
        partition = wait_until(
            lambda: owner(source), 35, f"unique owner {source} for {grid}"
        )
        force_handover(bench, source, target, partition)
        wait_until(lambda: owner(target), 45, f"unique owner {target} for {grid}")
        state = npc_state(bench, target, phase)
        state["partition"] = partition
        state["gate_residuals"] = gate_residuals(bench, target, partition)
        stages.append(state)
        valid = valid and matches(state) and state["gate_residuals"] == 0
    return "PASS" if valid else "DIFF"


def baseline(bench, spawned, encounter):
    """The standalone NPC state every cluster leg is compared with. Raises if MES did not set it up."""
    state = npc_state(bench, "stock", "initial")
    group, prefab = (
        (encounter.split(":", 1)[1], state["prefab"])
        if encounter
        else ("ClusterProbe-Encounter", "ClusterProbe-Ship")
    )
    if (
        (
            state["spawn_origin"] == [0.0, 0.0, 0.0]
            if encounter
            else state["spawn_origin"] != [120.0, 70.0, 110.0]
        )
        or state["spawn_group"] != group
        or state["prefab"] in ("-", "")
        or state["prefab"] != prefab
        or state["stored_origin"] != state["spawn_origin"]
        or not state["spawned_by_mes"]
        or state["npc_bytes"] == 0
        or not state["terminal_marker"]
        or state["grid"] != spawned["after"]["stock"]["grids"][0]
    ):
        raise ValueError(
            "Standalone MES did not establish the NPC state and terminal marker"
        )
    return state


def measure(bench, roles, issuer, reference, handover=False, encounter=None):
    report = spawn(bench, roles, issuer)
    if reference is None:
        report["result"] = "PASS" if report["spawned_once"] else "INCONCLUSIVE"
        if handover and report["spawned_once"]:
            report["initial"] = baseline(bench, report, encounter)
    elif not report["spawned_once"]:
        report["result"] = "DIFF"
    elif handover:
        grid = report["after"][issuer]["grids"][0]
        stages = report["handover"] = []
        report["result"] = round_trip(
            bench, reference["initial"], grid, encounter, stages
        )
    else:
        report["result"] = "PASS"
    return report


def request_encounter(world, staged, group):
    """Have the fixture request a real encounter mod's spawn group instead of its own."""
    # Requested as a random encounter owned by SPRT, like the fixture's own
    (staged / f"{PROBE}.sbm/encounter.txt").write_text(
        f"{group} SPRT RandomEncounter\n"
    )


def run(corpus, results, handover=False, encounter=None):
    mods, configure = [MES], None
    if encounter:
        mod, group = encounter.split(":", 1)
        mods.append(mod)
        configure = functools.partial(request_encounter, group=group)
    return run_staged(
        corpus,
        results,
        scenario="MOD-MES-SPAWN",
        title="MES custom encounter spawn",
        mods=mods,
        probe=PROBE,
        fixture="MesEncounterFixture",
        measure=functools.partial(measure, handover=handover, encounter=encounter),
        configure=configure,
    )


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
    verdict = run(args.corpus, args.results, args.handover, args.encounter)["result"]
    print(verdict)
    raise SystemExit(0 if verdict == "PASS" else 1)
