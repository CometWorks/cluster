#!/usr/bin/env python3
"""Compare immutable native solar evidence; never changes the world or its clock."""
import argparse
import hashlib
import json
from pathlib import Path


def compare(directory, output):
    initial = json.loads((directory / "initial.json").read_text())
    ordinary = json.loads((directory / "solar-samples.json").read_text())
    points = json.loads((directory / "solar-native-points.json").read_text())
    geometry = json.loads((directory / "solar-geometry.json").read_text())
    curves = {s["Block"]: s["Curve"] for p in initial["profiles"] for s in p["Sources"] if s.get("Curve")}
    names = {b["block"]: (g["name"], kind) for g in ordinary[0]["grids"] for kind in ("panels", "farms") for b in g[kind]}

    def predicted(block, phase):
        curve = curves[block]
        at = phase % 360 * len(curve) / 360
        index = int(at)
        return curve[index] * (1 - at % 1) + curve[(index + 1) % len(curve)] * (at % 1)

    errors = []
    for sample in points:
        for point in sample["points"]:
            estimate = predicted(point["block"], sample["phase"])
            errors.append({"grid": names[point["block"]][0], "kind": names[point["block"]][1],
                           "phase": sample["phase"], "native": point["ratio"], "sampled": estimate,
                           "absoluteError": abs(point["ratio"] - estimate)})
    ordinary_errors = [abs(b["ratio"] - predicted(b["block"], sample["phase"])) for sample in ordinary
                       for g in sample["grids"] for kind in ("panels", "farms") for b in g[kind]]
    by_name = {name: curves[next(b for b, n in names.items() if n == (name, "panels"))] for name in {n[0] for n in names.values()}}
    open_curve = by_name["planet-open"]
    controls = {
        "honest1_5Rotation": json.loads((directory / "result.json").read_text())["nativeFrames"] == 675000,
        "eightNativePorts": len(curves) == 8 and len(names) == 8,
        "weatherOne": all(g["weatherMultiplier"] == 1 for s in ordinary for g in s["grids"]),
        "actualNearbyRidge": geometry["ridge75m"] >= 10,
        "ridgeChangesCurve": max(abs(a - b) for a, b in zip(open_curve, by_name["planet-ridge"])) > .01,
        "ownGridOcclusion": max(a - b for a, b in zip(open_curve, by_name["planet-own-obstruction"])) > .1,
        "nearbyGridOcclusion": max(a - b for a, b in zip(open_curve, by_name["planet-nearby-obstruction"])) > .1,
        "dayAndNight": all({g["dark"] for s in ordinary for g in s["grids"] if g["name"] == name} == {False, True} for name in by_name),
    }
    outliers = [e for e in errors if e["absoluteError"] > .02]
    point_comparison_passed = all(controls.values()) and not outliers and len(points) >= 108
    ordinary_output_within_two_percent = max(ordinary_errors) <= .02
    passed = point_comparison_passed and ordinary_output_within_two_percent
    files = ("initial.json", "result.json", "solar-samples.json", "solar-native-points.json", "solar-geometry.json")
    result = {"directory": str(directory), "hashes": {n: hashlib.sha256((directory / n).read_bytes()).hexdigest() for n in files},
              "controls": controls, "nativePointCount": len(errors), "pointMaxError": max(e["absoluteError"] for e in errors),
              "ordinaryRollingPivotMaxError": max(ordinary_errors), "outliers": outliers,
              "pointComparisonPassed": point_comparison_passed, "ordinaryOutputWithinTwoPercent": ordinary_output_within_two_percent, "passed": passed,
              "method": "native component copies compute every pivot through native workers/raycast/completion; live components and clocks unchanged; ordinary rolling-pivot trace retained separately"}
    output.write_text(json.dumps(result, indent=2))
    print(json.dumps({k: result[k] for k in ("controls", "nativePointCount", "pointMaxError", "ordinaryRollingPivotMaxError", "passed")}, indent=2))
    if not passed:
        raise SystemExit("native solar differential failed; retained output identifies the exact bound/control")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    compare(args.directory, args.output)
