#!/usr/bin/env python3
"""Standalone engine reference. Native engine advances stocks; dormant product library only.
Max-speed pacing is allowed only after a real60Hz comparison; never skips updates or jumps time.
"""
from __future__ import annotations
import argparse
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
from scenarios_planet import start_standalone


def build():
    source = Path(__file__).parent / "OfflineReference"
    bundle = HOST.test_root / "offline-reference-plugin/OfflineReference"
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


def run_one(bundle, out, label, frames, fast):
    state = resources()
    if state["availableGb"] < 10 or state["testRootFreeGb"] < 100: raise RuntimeError(f"resource floor: {state}")
    bench = Bench("OFF-REFERENCE-" + label, label, "3", out, keep_root=True)
    _survival_template(bench)
    bench.cluster.config.dumps.mkdir(parents=True, exist_ok=True)
    evidence = bench.dir / "native"
    server = None
    try:
        server = start_standalone(bench, bench.cluster.config.world_template, name="reference-" + label,
            ready=("Session loaded", "Game ready"), local_plugins=[bundle], extra_env={
                "OFFLINE_REFERENCE_ROOT": str(evidence), "OFFLINE_REFERENCE_FRAMES": str(frames),
                "OFFLINE_REFERENCE_FAST": "1" if fast else "0"})
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
    args = parser.parse_args()
    out = HOST.results / (datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ") + "-standalone")
    out.mkdir(parents=True)
    bundle = build()
    (out / "provenance.json").write_text(json.dumps({"package": str(HOST.package), "source": subprocess.check_output(
        ["git", "rev-parse", "HEAD"], text=True).strip(), "engine": "stock DS, cluster plugin not registered",
        "pacing": "native EnableMaxSpeed plus2ms sleep; no skipped frames/time jumps", "resources": resources()}, indent=2))
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
        runs=list(pool.map(lambda n:run_one(bundle,out,"six-hour-"+str(n),1296000,True),range(1,4)))
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
