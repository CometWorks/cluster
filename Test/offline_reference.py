#!/usr/bin/env python3
"""Standalone engine reference. Native engine advances stocks; dormant product library only.
Max-speed pacing is allowed only after a real60Hz comparison; never skips updates or jumps time.
"""
from __future__ import annotations
import argparse
import hashlib
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import subprocess
import time
from bench import Bench, HOST
from run_suite import resources
from scenarios_offline import _survival_template
from scenarios_planet import start_standalone, planet_template


def build(scenario="energy"):
    source = Path(__file__).parent / "OfflineReference"
    bundle = HOST.test_root / ("offline-reference-plugin-" + scenario) / Path("OfflineReference")
    bundle.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(["dotnet", "build", str(source / "OfflineReference.csproj"), "-c", "Release", "--nologo", "-v", "q",
        f"-p:ClusterPlugins={HOST.package / 'plugins'}", f"-p:Magnetar={HOST.magnetar}"], capture_output=True, text=True)
    (bundle.parent / "build.log").write_text(result.stdout + result.stderr)
    result.check_returncode()
    shutil.copy2(source / "bin/Release/net10.0/OfflineReference.dll", bundle)
    for path in (HOST.package / "plugins/ClusterNode").glob("*.dll"):
        shutil.copy2(path, bundle / path.name)
    (bundle / "OfflineReference.xml").write_text('''<PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">
<Id>offline-reference-observer</Id><RepoId>CometWorks/cluster</RepoId><FriendlyName>Standalone reference observer</FriendlyName>
<Runtimes>CoreCLR</Runtimes><Commit>test-only</Commit></PluginData>''')
    return bundle


def run_one(bundle, out, label, frames, fast, port_slot=None, scenario="energy"):
    state = resources()
    if state["availableGb"] < 10 or state["testRootFreeGb"] < 100: raise RuntimeError(f"resource floor: {state}")
    bench = Bench("OFF-REFERENCE-" + label, label, "3", out, keep_root=True)
    if scenario == "solar-planet":
        template = planet_template(bench)
        import xml.etree.ElementTree as ET
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = template / name
            tree = ET.parse(path); settings = tree.getroot().find("Settings")
            for key, value in {"GameMode": "Survival", "EnableSunRotation": "true", "SunRotationIntervalMinutes": "120", "AutoSaveInMinutes": "0", "WeatherSystem": "false"}.items():
                field = settings.find(key)
                if field is None: field = ET.SubElement(settings, key)
                field.text = value
            tree.write(path, encoding="utf-8", xml_declaration=True)
    else: _survival_template(bench)
    bench.cluster.config.dumps.mkdir(parents=True, exist_ok=True)
    evidence = bench.dir / "native"
    server = None
    try:
        server = start_standalone(bench, bench.cluster.config.world_template, name="reference-" + label,
            ready=("Session loaded", "Game ready"), local_plugins=[HOST.package / "plugins/Concealment", bundle], port_slot=port_slot, extra_env={
                "OFFLINE_REFERENCE_ROOT": str(evidence), "OFFLINE_REFERENCE_FRAMES": str(frames),
                "OFFLINE_REFERENCE_FAST": "1" if fast else "0", "OFFLINE_REFERENCE_SCENARIO": scenario})
        limit = time.monotonic() + max(600, frames / (60 if not fast else 100) + 300)
        last = 0
        while time.monotonic() < limit:
            if (evidence / "error.json").exists(): raise RuntimeError((evidence / "error.json").read_text())
            if (evidence / "result.json").exists():
                result = json.loads((evidence / "result.json").read_text())
                assert result["nativeFrames"] == frames and result["dormantClusterLibrary"]
                assert abs(result["gameplayMilliseconds"] - frames * 1000 / 60) < 2
                assert result["elapsedTicks"] == frames * 160000
                return result
            if not server.alive(): raise RuntimeError("reference server exited")
            if time.monotonic() - last >= 30:
                state = resources()
                if state["availableGb"] < 10 or state["testRootFreeGb"] < 100: raise RuntimeError(f"resource floor: {state}")
                progress = json.loads((evidence / "progress.json").read_text()) if (evidence / "progress.json").exists() else {}
                print(json.dumps({"lane": label, "resources": state, "nativeFrames": progress.get("nativeFrames"), "target": frames}), flush=True)
                last = time.monotonic()
            time.sleep(2)
        raise TimeoutError("native frame horizon not reached")
    finally:
        if server: server.stop()
        bench.close()


def fuel(profile):
    return sum(s["Amount"] for inv in profile["Inventories"] for s in inv["Stacks"] if s["Item"] == "MyObjectBuilder_Ingot/Uranium")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--proof-only", action="store_true")
    parser.add_argument("--continue-long", type=Path)
    parser.add_argument("--solar-planet", action="store_true")
    parser.add_argument("--zone-environment", action="store_true")
    parser.add_argument("--observed", action="store_true")
    parser.add_argument("--observed-short", action="store_true", help="short native fuel-observer regression, never full-rotation acceptance")
    parser.add_argument("--production", action="store_true")
    parser.add_argument("--gyros", action="store_true")
    parser.add_argument("--gas", action="store_true")
    parser.add_argument("--gas-no-oxygen", action="store_true")
    parser.add_argument("--zone-long", type=Path, help="prior valid pacing proof")
    args = parser.parse_args()
    out = HOST.results / (datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ") + "-standalone")
    out.mkdir(parents=True)
    scenario = "solar-planet" if args.solar_planet else "zone-environment" if args.zone_environment else "observed-short" if args.observed_short else "observed" if args.observed else "production" if args.production else "gyros" if args.gyros else "gas-no-oxygen" if args.gas_no_oxygen else "gas" if args.gas else "zones" if args.zone_long else "energy"
    bundle = build(scenario)
    source_files = [Path(__file__), *sorted((Path(__file__).parent / "OfflineReference").glob("*.*")),
                    Path(__file__).parent / "ClusterTestPlugin/Source/OfflineObservedLogic.cs"]
    source_files = [p for p in source_files if p.is_file()]
    for path in source_files:
        destination = out / "observer-source" / path.relative_to(Path(__file__).parent)
        destination.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, destination)
    (out / "provenance.json").write_text(json.dumps({"package": str(HOST.package), "source": subprocess.check_output(
        ["git", "rev-parse", "HEAD"], text=True).strip(), "engine": "stock DS, cluster plugin not registered",
        "sourceDirty": subprocess.check_output(["git", "status", "--porcelain"], text=True).splitlines(),
        "observerSha256": {str(p.relative_to(Path(__file__).parent)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files},
        "observerBinarySha256": hashlib.sha256((bundle / "OfflineReference.dll").read_bytes()).hexdigest(),
        "product": json.loads((HOST.package / "manifest.json").read_text()),
        "pacing": "native EnableMaxSpeed plus1ms sleep; no skipped frames/time jumps", "resources": resources()}, indent=2))
    if args.solar_planet:
        run = run_one(bundle, out, "solar-planet", 675000, True, port_slot=12, scenario="solar-planet")
        (out / "solar-planet-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.zone_environment:
        run = run_one(bundle, out, "zone-environment", 108000, True, port_slot=13, scenario="zone-environment")
        (out / "zone-environment-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.observed or args.observed_short:
        run = run_one(bundle, out, "observed-short" if args.observed_short else "observed-rotation", 3600 if args.observed_short else 450000, True, port_slot=15 if args.observed_short else 14, scenario=scenario)
        (out / "observed-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.production:
        run = run_one(bundle, out, "production", 36000, True, port_slot=11, scenario="production")
        (out / "production-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.gyros:
        run = run_one(bundle, out, "gyro-fuel", 21600, True, port_slot=10, scenario="gyros")
        (out / "gyro-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.gas or args.gas_no_oxygen:
        run = run_one(bundle, out, "gas-coupled", 36000, True, port_slot=9, scenario=scenario)
        (out / "gas-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.zone_long:
        proof = json.loads((args.zone_long / "pacing-equivalence.json").read_text())
        assert proof["passed"] and proof["clockEquivalence"]
        run = run_one(bundle, out, "four-hour-zone", 864000, True, port_slot=20, scenario="zones")
        (out / "zone-reference.json").write_text(json.dumps(run, indent=2))
        return
    if args.continue_long:
        proof = json.loads((args.continue_long / "pacing-equivalence.json").read_text())
        initial = json.loads((args.continue_long / "OFF-REFERENCE-paced60/native/initial.json").read_text())
        assert proof["passed"] and proof["clockEquivalence"] and all(not p["Creative"] and not p["Simplified"] for p in initial["profiles"])
        with ThreadPoolExecutor(max_workers=2) as pool:
            runs = list(pool.map(lambda n: run_one(bundle, out, "six-hour-resume-"+str(n),1296000,True,port_slot=4+n), (1,2)))
        baseline = args.continue_long / "OFF-REFERENCE-six-hour-2/native/result.json"
        runs.append(json.loads(baseline.read_text()))
        compare(runs,out)
        return
    normal = run_one(bundle, out, "paced60", 3600, False)
    fast = run_one(bundle, out, "bounded-fast", 3600, True)
    checks = []
    for a,b in zip(normal["results"], fast["results"]):
        ea = sum(s["Amount"] for s in a["actual"]["Storage"] if s["Kind"] == "Battery")
        eb = sum(s["Amount"] for s in b["actual"]["Storage"] if s["Kind"] == "Battery")
        checks.append({"case":a["native"]["name"], "energyA":ea,"energyB":eb,"fuelA":fuel(a["actual"]),"fuelB":fuel(b["actual"]),
                       "passed":abs(ea-eb)<.006 and abs(fuel(a["actual"])-fuel(b["actual"]))<.02})
    eq={"normalSeconds":normal["wallSeconds"],"fastSeconds":fast["wallSeconds"],"checks":checks,"clockEquivalence":True,"passed":all(c["passed"] for c in checks)}
    (out / "pacing-equivalence.json").write_text(json.dumps(eq,indent=2))
    if not eq["passed"]: raise RuntimeError("pacing equivalence failed; long fast lane refused")
    if args.proof_only: return
    with ThreadPoolExecutor(max_workers=3) as pool:
        runs=list(pool.map(lambda n:run_one(bundle,out,"six-hour-"+str(n),1296000,True,port_slot=4+n),range(1,4)))
    compare(runs,out)


def compare(runs,out):
    differences=[]
    for run in runs:
        for c in run["results"]:
            a=sum(s["Amount"] for s in c["actual"]["Storage"] if s["Kind"]=="Battery")
            m=sum(s["Amount"] for s in c["modeled"]["Storage"] if s["Kind"]=="Battery")
            differences.append({"case":c["native"]["name"],"actualMWh":a,"modeledMWh":m,"actualUranium":fuel(c["actual"]),
                "modeledUranium":fuel(c["modeled"]),"passed":abs(a-m)<=.06 and abs(fuel(c["actual"])-fuel(c["modeled"]))<=.1})
    (out / "six-hour-comparison.json").write_text(json.dumps({"differences":differences,"passed":all(d["passed"] for d in differences),
        "limitation":"resource6h/60Hz; native elapsed sun clock5.76h, explicit authority conversion; sun rotation disabled"},indent=2))
    if not all(d["passed"] for d in differences): raise RuntimeError("six-hour standalone differential failed")

if __name__ == "__main__": main()
