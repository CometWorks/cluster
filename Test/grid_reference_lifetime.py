"""Compare retained and weak-only references to ordinary grids after a real handover."""

import argparse
import json
import time
from pathlib import Path

from bench import Bench, HOST
from cluster import ClusterError, wait_until
from scenarios import _force_handover
from scenarios_ext import _gate_residuals


def collect(bench, grids):
    for _ in range(3):
        bench.cluster.node("node-1", "/weakref-flush", "POST")
        time.sleep(1)
    state = bench.cluster.node("node-1", "/weakref-state")
    by_id = {row["entity"]: row for row in state["entities"]}
    return {kind: by_id[grid] for kind, grid in grids.items()}


def lookup(bench, grids):
    return {
        role: {
            kind: bench.cluster.node(role, f"/weakref-lookup?entity={grid}")
            for kind, grid in grids.items()
        }
        for role in ("node-1", "node-2")
    }


def run(results):
    bench = Bench(
        "GRID-REF-LIFETIME", "Grid reference retention across handover", "2", results
    )
    report = {
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
    }
    started = time.monotonic()
    held = None
    try:
        bench.start_cluster()
        bench.cluster.post("/automatic/pause")
        grids = report["grids"] = {
            kind: int(
                bench.cluster.node(
                    "node-1",
                    f"/trash-test-spawn?x={x}&y=100&z=100&static=false&blocks=27",
                    "POST",
                )["grid"]
            )
            for kind, x in (("strong", 12000), ("weak_only", 12100))
        }

        def shared_owner(node):
            snapshot = bench.diag("entities")
            places = [bench.entity_owner(snapshot, grid) for grid in grids.values()]
            if (
                all(len(p) == 1 and p[0][0] == node for p in places)
                and places[0] == places[1]
            ):
                return places[0][0][1]
            return None

        partition = wait_until(
            lambda: shared_owner("node-1"),
            90,
            "both ordinary grids in a single source partition",
        )
        for kind, grid in grids.items():
            hold = "true" if kind == "strong" else "false"
            bench.cluster.node(
                "node-1",
                f"/weakref-track?entity={grid}&strong={hold}&parts=true",
                "POST",
            )
            if kind == "strong":
                held = grid
        before = report["before"] = lookup(bench, grids)
        for kind, grid in grids.items():
            source, dest = before["node-1"][kind], before["node-2"][kind]
            if (
                not source["sameLocalInstance"]
                or source["localId"] != grid
                or source["localClosed"]
                or source["held"] != (kind == "strong")
                or dest["found"]
                or source["process"] == dest["process"]
            ):
                raise ValueError("Invalid source or destination lookup before transfer")
        _force_handover(bench, "node-1", "node-2", partition)
        wait_until(
            lambda: shared_owner("node-2"),
            90,
            "both grid IDs authoritative on destination",
        )
        report["gate_residuals"] = _gate_residuals(bench, "node-2", partition)
        if report["gate_residuals"] != 0:
            raise ValueError("Import gate reported residuals")
        report["while_held"] = collect(bench, grids)
        after = report["after_transfer"] = lookup(bench, grids)
        for kind, grid in grids.items():
            src, dst = after["node-1"][kind], after["node-2"][kind]
            if (
                src["found"]
                or not dst["found"]
                or dst["localId"] != grid
                or dst["localClosed"]
            ):
                raise ValueError("Live lookup does not match ownership transfer")
        retired = after["node-1"]["strong"]
        if (
            not retired["held"]
            or retired["cachedId"] != held
            or not retired["cachedClosed"]
        ):
            raise ValueError(
                "Strong control did not retain its retired source instance"
            )
        release = bench.cluster.node(
            "node-1", f"/weakref-release?entity={held}", "POST"
        )
        if not release["removed"]:
            raise ValueError("Strong root was missing before release")
        held = None
        samples = report["after_release"] = []
        for _ in range(3):
            samples.append(collect(bench, grids))
            if not any(row["alive"] for row in samples[-1].values()):
                break
        post = report["final_lookups"] = lookup(bench, grids)
        if report["while_held"]["weak_only"]["alive"] or any(
            row["alive"] for row in samples[-1].values()
        ):
            report["detail"] = (
                "A weak witness survived the observation window. Additional engine or test roots are unresolved."
            )
        elif all(
            post["node-2"][k]["found"] and not post["node-2"][k]["localClosed"]
            for k in grids
        ):
            report["result"] = "PASS"
    except (ClusterError, ValueError, OSError, TimeoutError, RuntimeError) as error:
        report["error"] = str(error)
    finally:
        if held is not None:
            try:
                bench.cluster.node("node-1", f"/weakref-release?entity={held}", "POST")
            except (ClusterError, OSError):
                pass
        report["seconds"] = round(time.monotonic() - started, 1)
        bench.record("observations", report)
        bench.close()
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=False)
    report = run(args.results)
    print(report["result"], report.get("error", report.get("detail", "")))
    raise SystemExit(0 if report["result"] == "PASS" else 1)
