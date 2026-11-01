#!/usr/bin/env python3
"""Audit both solar oracles without changing the formal failed verdict or native data."""
import argparse
import hashlib
import json
import math
from pathlib import Path


def audit(directory: Path, output: Path):
    names = ("initial.json", "result.json", "solar-samples.json", "solar-native-points.json", "solar-geometry.json")
    initial, result, live, points, geometry = [json.loads((directory / name).read_text()) for name in names]
    ports = {b["block"]: (g["name"], kind) for g in live[0]["grids"] for kind in ("panels", "farms") for b in g[kind]}
    sources = {s["Block"]: s for p in initial["profiles"] for s in p["Sources"] if s.get("Curve")}

    def predicted(block, phase, linear=True):
        curve = sources[block]["Curve"]
        at = phase % 360 / 360 * len(curve)
        return curve[int(at)] if not linear else curve[int(at)] * (1-at % 1) + curve[(int(at)+1) % len(curve)] * (at % 1)

    waveforms = []
    summaries = []
    for block, (name, kind) in ports.items():
        waveform = []
        for sample, point_sample in zip(live, points):
            port = next(b for g in sample["grids"] for k in ("panels", "farms") for b in g[k] if b["block"] == block)
            fresh = next(b for b in point_sample["points"] if b["block"] == block)
            waveform.append({"nativeFrames": sample["nativeFrames"], "livePhaseDegrees": sample["phase"],
                "freshPhaseDegrees": point_sample["phase"], "live": port["ratio"], "fresh": fresh["ratio"],
                "curveAtLivePhase": predicted(block, sample["phase"]),
                "curveAtFreshPhase": predicted(block, point_sample["phase"]),
                "modelLeftBinAtLivePhase": predicted(block, sample["phase"], False)})
        # These are angle-sampled quadrature estimates, not an unsampled native energy integral.
        integrals = {key: sum((b["nativeFrames"]-a["nativeFrames"]) * (a[key]+b[key])/2 for a,b in zip(waveform,waveform[1:])) / 216000
                     for key in ("live", "fresh", "curveAtLivePhase", "modelLeftBinAtLivePhase")}
        worst = max(waveform, key=lambda r: abs(r["live"]-r["curveAtLivePhase"]))
        summaries.append({"grid": name, "kind": kind, "block": block,
            "freshGeometryMaxAbsoluteError": max(abs(r["fresh"]-r["curveAtFreshPhase"]) for r in waveform),
            "ordinaryLiveMaxAbsoluteError": abs(worst["live"]-worst["curveAtLivePhase"]),
            "ordinaryLiveRmsError": math.sqrt(sum((r["live"]-r["curveAtLivePhase"])**2 for r in waveform)/len(waveform)),
            "worst": worst, "sampledIntegralHoursAtNameplate": integrals,
            "sampledElectricalMWh" if kind == "panels" else "sampledGasUnits": {key: value * sources[block]["Maximum"] * (1 if kind == "panels" else 3600) for key,value in integrals.items()}})
        waveforms.append({"grid": name, "kind": kind, "block": block, "samples": waveform})
    fresh_max = max(s["freshGeometryMaxAbsoluteError"] for s in summaries)
    live_max = max(s["ordinaryLiveMaxAbsoluteError"] for s in summaries)
    report = {"directory": str(directory.resolve()), "hashes": {name: hashlib.sha256((directory/name).read_bytes()).hexdigest() for name in names},
        "originalSpecification": {"section4_3": "For each offline.solarSteps angle, re-run every pivot's native ray/angle/dark-side test; curve keyed by angle, weather expectation one.",
            "T_OC_03": "curve from §4.3 equals the reference panel output sampled every 5° within 2 %; day/night boundaries match IsOnDarkSide",
            "thresholdUnchanged": .02, "ambiguityForParent": "§4.3 describes instantaneous all-pivot geometry. T-OC-03 says reference panel output, which can mean ordinary rolling-cache output. Both results remain visible; no acceptance waiver is inferred."},
        "nativeMechanism": {"modernPivots": 12, "legacyPivots": 8, "framesPerScheduledPivot": 100,
            "modernFullRefreshFrames": 1200, "modernFullRefreshSunDegreesAt120Minutes": .96,
            "cache": "ComputeSunAngle updates only m_currentPivot; OnRayCastCompleted counts cached booleans for every other pivot. IsOnDarkSide clears them all. OnSunAngleComputed multiplies the current angle by the cached count.",
            "worstEdgeFact": "At305deg, own/near panel fresh geometry is2/12 lit; ordinary output is approximately3/12 and4/12. The max discrepancy is two cached pivot quanta, rather than a 16.6% ray-position error."},
        "clockDomains": {"nativeFrames": result["nativeFrames"], "nativeElapsedSunSeconds": result["elapsedTicks"]/10000000,
            "nativeResourceSeconds": result["nativeFrames"]/60, "clusterAuthoritySecondsPerUnit": .016,
            "nativeFramesPerAuthorityUnit": .96,
            "comparison": "Ratios use each recorded actual angle. Native elapsed sun advances16ms/frame; resource integration uses1/60s/frame. A standalone time-domain model comparison must map BOTH native solar period/phase and resource duration into authority units; blindly comparing675000 native frames to703125 authority angle units introduces a 4.1667% phase drift.",
            "quadrature": "Trapezoids of the retained5degree samples only, ending at the last sample; not a per-frame energy measurement or new native stock acceptance. Left-bin curve values are also exposed; model interpolation is not silently changed."},
        "geometryControl": geometry, "freshGeometryMaxAbsoluteError": fresh_max, "ordinaryLiveMaxAbsoluteError": live_max,
        "freshGeometryWithinTwoPercent": fresh_max <= .02, "ordinaryLiveWithinTwoPercent": live_max <= .02,
        "formalFullAcceptancePassed": fresh_max <= .02 and live_max <= .02,
        "parentDecisionRequired": live_max > .02,
        "ports": summaries, "waveforms": waveforms}
    output.write_text(json.dumps(report, indent=2))
    print(json.dumps({key: report[key] for key in ("freshGeometryMaxAbsoluteError", "ordinaryLiveMaxAbsoluteError", "formalFullAcceptancePassed", "parentDecisionRequired")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    audit(args.directory, args.output)
