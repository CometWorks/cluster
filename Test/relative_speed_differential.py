"""Compare Relative Top Speed's physical governor on standalone and across a cluster handover."""

import re

# mod_staging first: its bench import puts Cli/ on sys.path for cluster
from mod_staging import command, main, run_staged
from cluster import wait_until
from scenarios import force_handover

MOD = "1359618037"
PROBE = "9991845012"


def configure(world, staged):
    storage = world / "Storage" / f"{MOD}.sbm_RelativeTopSpeed"
    storage.mkdir(parents=True)
    curve = '<CruiseCurve><Point Mass="0" Speed="20"/><Point Mass="1000000" Speed="20"/></CruiseCurve>'
    (storage / "RelativeTopSpeed.cfg").write_text(
        "<Settings><Version>2</Version><EnableBoosting>false</EnableBoosting>"
        "<EnableGridGroups>false</EnableGridGroups><IgnoreGridsWithoutThrust>false</IgnoreGridsWithoutThrust>"
        "<IgnoreGridsWithoutCockpit>true</IgnoreGridsWithoutCockpit><SpeedLimit>100</SpeedLimit>"
        f"<LargeGrid>{curve}</LargeGrid><SmallGrid>{curve}</SmallGrid></Settings>"
    )


def observe(bench, node, phase, step):
    pattern = rf"rts_probe {phase} {step} frame=(\d+) grids=([\d.,=Ee+-]+)"
    match = re.search(pattern, bench.cluster.wait_node_log(node, pattern, 35))
    fields = [pair.split("=") for pair in match[2].split(",")]
    return {
        "node": node,
        "frame": int(match[1]),
        "grids": [int(k) for k, _ in fields],
        "speeds": [float(v) for _, v in fields],
    }


def near(actual, expected):
    return len(actual) == len(expected) and all(
        abs(v - t) < 0.5 for v, t in zip(actual, expected)
    )


def measure(bench, roles, issuer, reference):
    command(bench, issuer, PROBE, "spawn")
    rows = {
        s: observe(bench, issuer, "initial", s)
        for s in ("stationary", "kick", "settled")
    }
    initial, kick, end = rows.values()
    valid = (
        len(set(kick["grids"])) == 3
        and initial["grids"] == kick["grids"] == end["grids"]
        and near(initial["speeds"], [0, 0, 0])
        and near(kick["speeds"], [60, 60, 0])
        and end["frame"] - kick["frame"] == 240
    )
    report = {"initial": rows, "result": "INCONCLUSIVE"}
    if not valid:
        return report
    if not near(end["speeds"], [20, 60, 0]):
        report["result"] = "DIFF"
        return report
    if len(roles) > 1:
        grid = kick["grids"][0]

        def owner(node):
            places = bench.entity_owner(bench.diag("entities"), grid)
            return places if len(places) == 1 and places[0][0] == node else None

        source = wait_until(lambda: owner(issuer), 40, "governed grid on initial node")
        force_handover(bench, issuer, "node-2", source[0][1])
        target = wait_until(
            lambda: owner("node-2"), 45, "governed grid moved to other node"
        )
        command(bench, "node-2", PROBE, "moved", str(grid))
        handover = {
            s: observe(bench, "node-2", "moved", s)
            for s in ("imported", "kick", "settled")
        }
        report.update(ownership=[source, target], handover=handover)
        kick, end = handover["kick"], handover["settled"]
        if (
            any(r["grids"] != [grid] for r in handover.values())
            or not near(kick["speeds"], [60])
            or end["frame"] - kick["frame"] != 240
        ):
            return report
        if not near(end["speeds"], [20]):
            report["result"] = "DIFF"
            return report
    report["result"] = "PASS"
    return report


def run(corpus, results):
    return run_staged(
        corpus,
        results,
        scenario="MOD-RELATIVE-SPEED",
        title="Relative Top Speed physical governor",
        mods=[MOD],
        probe=PROBE,
        fixture="RelativeSpeedFixture",
        measure=measure,
        configure=configure,
    )


if __name__ == "__main__":
    main(run, __doc__)
