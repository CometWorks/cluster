"""Compare actual integrity restored by the Workshop Nanite Control Facility to a disabled control."""

import argparse
import re
from pathlib import Path

from staged_mod_differential import run_staged

MOD = "219757726"
PROBE = "9991845004"


def configure(world):
    # Keen scopes world files by the staged script assembly name.
    storage = world / "Storage" / (MOD + ".sbm_NaniteConstructionSystem")
    storage.mkdir(parents=True)
    (storage / "Config.xml").write_text(
        "<NaniteSettings><ConstructionMinTravelTime>1</ConstructionMinTravelTime>"
        "<ConstructionDistanceDivisor>50</ConstructionDistanceDivisor>"
        "</NaniteSettings>"
    )


def measure(bench, roles, issuer):
    (bench.cluster.config.root / f"{issuer}-data/Mods/{PROBE}.sbm/start.txt").touch(
        exist_ok=False
    )
    rows = {}
    for phase in ("pristine", "damaged", "settled"):
        pattern = rf"nanite_repair {phase} frame=(\d+) enabled=(True|False) working=(True|False) disabled=(True|False) creative=(True|False) max=([\d.E+-]+) test=([\d.E+-]+) control=([\d.E+-]+)"
        match = re.search(
            pattern,
            bench.cluster.wait_node_log(
                issuer, pattern, 135 if phase == "settled" else 35
            ),
        )
        frame, enabled, working, disabled, creative, max_hp, test_hp, control_hp = (
            match.groups()
        )
        rows[phase] = dict(
            frame=int(frame),
            enabled=enabled == "True",
            working=working == "True",
            disabled=disabled == "True",
            creative=creative == "True",
            max_hp=float(max_hp),
            test_hp=float(test_hp),
            control_hp=float(control_hp),
        )
    fresh, damaged, end = rows.values()
    usable = (
        fresh["test_hp"] == fresh["control_hp"] == fresh["max_hp"]
        and fresh["max_hp"] > 0
        and 0 < damaged["test_hp"] == damaged["control_hp"] < fresh["max_hp"]
        and not damaged["enabled"]
        and not any(row["disabled"] for row in rows.values())
        and end["enabled"]
        and end["working"]
        and end["control_hp"] == damaged["control_hp"]
    )
    return dict(
        issuer=issuer,
        measurements=rows,
        result=(
            "INCONCLUSIVE"
            if not usable
            else "PASS" if end["test_hp"] == fresh["max_hp"] else "DIFF"
        ),
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=False)
    result = run_staged(
        args.corpus,
        args.results,
        "MOD-NANITE-REPAIR",
        "Nanite Control Facility armor repair",
        MOD,
        PROBE,
        "NaniteRepairFixture",
        measure,
        configure,
    )
    print(result["result"])
    raise SystemExit(0 if result["result"] == "PASS" else 1)
