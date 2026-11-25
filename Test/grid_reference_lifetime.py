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


def owner(bench, grids, node):
    def shared_owner(node):
        snapshot = bench.diag("entities")
        places = [bench.entity_owner(snapshot, grid) for grid in grids.values()]
        if (
            all(len(p) == 1 and p[0][0] == node for p in places)
            and places[0] == places[1]
        ):
            return places[0][0][1]
        return None

    return wait_until(lambda: shared_owner(node), 90, f"matched grids owned by {node}")


def lifecycle(bench, case):
    report = {"result": "INCONCLUSIVE"}
    held = None
    try:
        if case == "safe-zone":
            ids = {
                kind: bench.cluster.node(
                    "node-1",
                    f"/block-test-admin-safezone?op=create&x={x}&y=2500&z=500",
                    "POST",
                )["zone"]
                for kind, x in (("strong", 45000), ("weak_only", 46000))
            }
            wait_until(
                lambda: all(
                    row["found"] for row in lookup(bench, ids)["node-2"].values()
                ),
                45,
                "both independent admin safe zones replicated to peer",
            )
        else:
            base = 12000 if case == "grid" else 22000
            grids = report["grids"] = {
                kind: bench.cluster.node(
                    "node-1",
                    f"/trash-test-spawn?x={x}&y=100&z=100&static=false&blocks=27&batteries={int(case=='battery')}",
                    "POST",
                )["grid"]
                for kind, x in (("strong", base), ("weak_only", base + 100))
            }
            partition = owner(bench, grids, "node-1")
            ids = grids
            if case == "battery":
                blocks = lookup(bench, grids)["node-1"]
                if any(len(b["fatBlocks"]) != 1 for b in blocks.values()):
                    raise ValueError(
                        "Expected exactly one real battery per fixture grid"
                    )
                ids = {kind: row["fatBlocks"][0] for kind, row in blocks.items()}
        report["entities"] = ids
        for kind, grid in ids.items():
            hold = "true" if kind == "strong" else "false"
            bench.cluster.node(
                "node-1",
                f"/weakref-track?entity={grid}&strong={hold}&parts=true",
                "POST",
            )
            if kind == "strong":
                held = grid
        before = report["before"] = lookup(bench, ids)
        for kind, grid in ids.items():
            source, dest = before["node-1"][kind], before["node-2"][kind]
            if (
                not source["sameLocalInstance"]
                or source["localId"] != grid
                or source["localClosed"]
                or source["held"] != (kind == "strong")
                or dest["found"] != (case == "safe-zone")
                or source["localType"]
                != {
                    "battery": "MyBatteryBlock",
                    "grid": "MyCubeGrid",
                    "safe-zone": "MySafeZone",
                }[case]
                or source["process"] == dest["process"]
                or source["sourceCloseEvents"] != 0
            ):
                raise ValueError("Invalid source or destination lookup before transfer")
        if case == "safe-zone":
            for entity in ids.values():
                bench.cluster.node(
                    "node-1",
                    f"/block-test-admin-safezone?op=delete&id={entity}",
                    "POST",
                )
            wait_until(
                lambda: all(
                    not x["found"]
                    for role in lookup(bench, ids).values()
                    for x in role.values()
                ),
                60,
                "admin zone removals on both nodes",
            )
        else:
            _force_handover(bench, "node-1", "node-2", partition)
            owner(bench, grids, "node-2")
            report["gate_residuals"] = _gate_residuals(bench, "node-2", partition)
            if report["gate_residuals"] != 0:
                raise ValueError("Import gate reported residuals")
        report["while_held"] = collect(bench, ids)
        after = report["after_retirement"] = lookup(bench, ids)
        for kind, grid in ids.items():
            src, dst = after["node-1"][kind], after["node-2"][kind]
            if (
                src["found"]
                or src["sourceCloseEvents"] != 1
                or dst["found"] != (case != "safe-zone")
                or (dst["found"] and (dst["localId"] != grid or dst["localClosed"]))
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
            samples.append(collect(bench, ids))
            if not any(row["alive"] for row in samples[-1].values()):
                break
        post = report["final_lookups"] = lookup(bench, ids)
        if report["while_held"]["weak_only"]["alive"] or any(
            row["alive"] for row in samples[-1].values()
        ):
            report["detail"] = (
                "A weak witness survived the observation window. Additional engine or test roots are unresolved."
            )
        elif all(
            post["node-2"][k]["found"] == (case != "safe-zone")
            and not post["node-2"][k]["localClosed"]
            for k in ids
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
    return report


def run(results, cases):
    bench = Bench(
        "GRID-REF-LIFETIME",
        "Reference retention across entity lifecycles",
        "2",
        results,
    )
    report = {
        "result": "INCONCLUSIVE",
        "package": json.loads((HOST.package / "manifest.json").read_text()),
    }
    started = time.monotonic()
    try:
        bench.start_cluster()
        bench.cluster.post("/automatic/pause")
        report["cases"] = {case: lifecycle(bench, case) for case in cases}
        if all(row["result"] == "PASS" for row in report["cases"].values()):
            report["result"] = "PASS"
    finally:
        report["seconds"] = round(time.monotonic() - started, 1)
        bench.record("observations", report)
        bench.close()
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument(
        "--case", choices=("grid", "battery", "safe-zone"), action="append"
    )
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=False)
    report = run(args.results, args.case or ["grid", "battery", "safe-zone"])
    print(report["result"], report.get("error", report.get("detail", "")))
    raise SystemExit(0 if report["result"] == "PASS" else 1)
