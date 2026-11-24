"""Compare a real Advanced Welding pad merge on standalone and cluster."""

import argparse
import re
from pathlib import Path

from staged_mod_differential import run_staged

MOD = "510790477"
PROBE = "9991845003"


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
    pattern = (
        r"weld_pad_probe positioned before=([\d.-]+) after=([\d.-]+) upDot=([\d.-]+)"
    )
    positions = re.search(pattern, bench.cluster.wait_node_log(issuer, pattern, 30))
    before, after, up = map(float, positions.groups())
    report["pose"] = {"apart_m": before, "aligned_m": after, "up_dot": up}
    if before < 10 or abs(after - 2.7) > 0.01 or abs(up + 1) > 0.01:
        return report
    observed = report["aligned"] = sample(bench, roles, "aligned")
    report["result"] = "PASS" if merged(control, observed, issuer) else "DIFF"
    return report


def run(corpus: Path, results: Path):
    return run_staged(
        corpus,
        results,
        scenario="MOD-WELD-PADS",
        title="Advanced Welding pad merge",
        mod=MOD,
        probe=PROBE,
        fixture="WeldPadFixture",
        measure=measure,
        world_name="WeldWorld",
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=False)
    result = run(args.corpus, args.results)
    print(result["result"])
    raise SystemExit(0 if result["result"] == "PASS" else 1)
