#!/usr/bin/env python3
"""Which TestPlan scenario is covered by what, and what is not covered at all.

TestPlan.md names 98 scenarios for Phases 1-3. This module is the single place that says, for each
one, whether the suite runs it (a native scenario, directly or through an alias), whether it was
retired and why, whether the plan itself has a gap there (no fixture, a Phase 5 measurement), or
which host capability it needs that this host lacks (``doctor.py`` reports the capabilities).
``run_suite.py`` cross-checks this map against TestPlan.md, so a scenario added to the plan and
forgotten here fails the run rather than quietly vanishing from the claim.

Run ``python3 coverage.py`` for the table.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from hostenv import HOST  # noqa: E402

TEST_PLAN = HOST.test_plan

# Scenario ids retired from the suite, and why (CleanupPlan.md: a test case that cannot run against
# a release-package deployment is deleted, not kept on another code path).
RETIRED: dict[str, str] = {
    "P1-INF-03": "remote dispatch parity asserted Test/dispatch.py, which synced repository checkouts "
                 "to an offload host. The suite now runs against a release package on every host, and "
                 "the package manifest names the commit (versions.json per run), so there is no "
                 "checkout to keep in parity. Retired 2026-09-17.",
    "P1-FAST-03-RED": "the red control was P1-FAST-03 run on a package older than 2463d7b (no carried fast "
                      "state). The carry is unconditional, so no current package can go red; the suite "
                      "runs one release package. Its one earned red is run 20260929-090354Z. Retired 2026-10-02.",
}

# Plan gaps: the plan asks for something no working copy provides (a fixture, a mod, a Phase 5
# baseline), or the deployment has no such failure mode. Fixed text; no host changes them.
PLAN_GAPS: dict[str, str] = {
    "P1-GWY-05": "50 simultaneous joins needs 50 headless clients; this host can run about four "
                 "before the GPU and RAM distort the measurement. The credit pacing that bounds "
                 "memory under the flood is asserted in the `packet-queue` self-test section.",
    "P1-GWY-07": "buffer-overflow injection needs a client that stalls its own receive path; the "
                 "headless client offers no hook for that, and killing it instead is P1-CHR-04.",
    "P1-REC-03": "the 7 states x 3 processes kill matrix needs a hold hook at every handover "
                 "protocol state; only the Begun and mid-transfer holds exist (save-tee, chaos).",
    "P1-GWY-04": "Tier 5 relay-latency harness: a PRF-02 baseline, which Phase 5 owns "
                 "(TestPlan §3.2); it measures rather than asserts, so it has no pass/fail here.",
    "P1-LIM-01": "needs a modded weapon with a range beyond the sync distance; no mod fixture "
                 "exists in these working copies.",
    "P1-MOD-01": "the reference PB scripts of Modding §10 are in no working copy here: the lanes "
                 "place programmable blocks for the IGC legs but never load a script into one, so "
                 "there is no counter/Update10/CustomData state to migrate.",
    "P1-MOD-02": "same missing PB fixture set as P1-MOD-01.",
    "P1-MOD-03": "same missing PB fixture set as P1-MOD-01.",
    "P1-SEC-07": "WireGuard fail-closed is a host-network property; this box runs every node on "
                 "loopback, so there is no isolation boundary to fail closed.",
    "P1-REC-09": "disk-full and I/O-error injection needs a loopback filesystem the harness may "
                 "fill; refused here because the run root shares the box's only NVMe.",
    "P1-REC-10": "there is no separate executor process in this deployment - the node is started "
                 "directly - so the 'executor down' failure mode does not exist to inject.",
    "P2-LIM-01": "Tier 5 voxel replica memory overhead: a Phase 5 baseline measurement.",
    "P2-MOD-06": "the ClusterJanitor reference mod is in no working copy of this project, and no world "
                 "fixture loads a mod: the split-world converter has never been run "
                 "over a modded save, so wiring it in is a change to the fixture pipeline, not a "
                 "test to write.",
}

# Host capabilities (doctor.py `capabilities`): scenario id -> (capability, why it needs it). On a
# host that has the capability the id counts as unported until a native scenario exists: the
# Phase 3 Steam lane (steam_lane.py, removed 2026-09-17, in git history) has no native port yet.
HOST_CAPABILITIES: dict[str, tuple[str, str]] = {
    "P1-REC-11": ("root", "packet loss/latency/reorder per leg needs tc/netem, which needs root on this box."),
    "P1-REC-13": ("twoHosts", "clock skew between hosts needs two hosts with independently settable clocks."),
    "P3-AUT-01": ("steamHost", "Steam authentication: this host has no Steam client and no retail game, so no "
                 "real EAuthSessionResponse can be produced. Deferred at the user's direction."),
    "P3-AUT-02": ("steamHost", "Steam ticket revocation - same missing Steam session as P3-AUT-01."),
    "P3-AUT-04": ("steamHost", "identity binding from a validated Steam/EOS userId - same missing session."),
    "P3-PUB-01": ("steamHost", "GameServer registration and A2S need a live Steam master-server connection."),
    "P3-PUB-02": ("steamHost", "retail-client end-to-end needs the retail game on a second machine."),
    "P3-PUB-03": ("steamHost", "EOS lobby publish and cross-play need EOS credentials and a retail client."),
    "P3-PUB-04": ("steamHost", "transport-swap regression sweep needs both transports live; the Steam half is "
                 "unavailable here."),
    "P3-PUB-05": ("steamHost", "password world and salt stability are asserted through a retail client join."),
}

# Scenarios that TestPlan.md lists as an alias or an umbrella for another id.
ALIASES = {
    "P1-SEC-06": "P1-REC-07",
    "P1-REC-05": "P1-REC-05a",
    # the drain scenario asserts the stale-epoch save refusal
    "P1-SEC-05": "P1-DRAIN-01",
    # the rollback scenario measures the per-partition anomaly window; recovery fences the stale save
    "P1-LIM-02": "P1-SAV-05",
    "P1-REC-12": "P1-REC-01",
    # the save-pipeline scenario steps the ladder and checks its alarms; counters covers the stream under load
    "P1-SAV-04": "P1-SAV-03",
    "P1-GWY-06": "P1-HND-04",
    # the WA-chaos scenario runs the concurrent cross-node debits and checks conservation
    "P2-WA-02": "P2-REC-01",
    # the poison scenario quarantines the crash-on-load partition and repairs it through
    # export, edit and re-import
    "P1-REC-08": "P2-REC-02",
    "P2-CNV-02": "P2-REC-02",
    "P2-WA-05": "P2-VOX-01",
    # the conversion scenario runs the deep-space split round trip before the live conversion
    "P1-DIF-01": "P2-CNV-01",
    "P2-SAV-01": "P2-SAV-01a",
    "P1-REC-02": "P1-REC-02b",
    "P2-WA-06": "P2-WA-06a",
    # regex artifact: TestPlan's Phase 3 risks paragraph cites assertion (c) as "P3-CST-01c"
    "P3-CST-01c": "P3-CST-01",
}


def plan_scenarios() -> list[str]:
    """Every scenario id TestPlan.md names, in sorted order."""
    text = TEST_PLAN.read_text()
    return sorted({match.group(0) for match in re.finditer(r"\bP[123]-[A-Z]+-\d+[a-c]?\b", text)})


def classify(native: dict, capabilities: dict | None = None) -> dict[str, tuple[str, str]]:
    """scenario id -> (kind, detail) for every id in the plan. ``capabilities`` is doctor.py's map;
    without it every capability counts as missing."""
    capabilities = capabilities or {}
    table: dict[str, tuple[str, str]] = {}
    for scenario in plan_scenarios():
        target = ALIASES.get(scenario, scenario)
        if target in native:
            table[scenario] = ("native", native[target][1])
        elif target in RETIRED:
            table[scenario] = ("retired", RETIRED[target])
        elif target in PLAN_GAPS:
            table[scenario] = ("plan gap", PLAN_GAPS[target])
        elif target in HOST_CAPABILITIES:
            capability, reason = HOST_CAPABILITIES[target]
            if capabilities.get(capability):
                table[scenario] = ("unported", f"this host has `{capability}`, but no native scenario exists: {reason}")
            else:
                table[scenario] = ("deferred", f"needs host capability `{capability}`: {reason}")
        else:
            table[scenario] = ("MISSING", "no runner, no deferral reason")
    return table


def main() -> int:
    from run_suite import SCENARIOS  # local import: coverage.py must stay importable standalone

    table = classify(SCENARIOS)
    counts: dict[str, int] = {}
    for scenario, (kind, detail) in table.items():
        counts[kind] = counts.get(kind, 0) + 1
        print(f"{scenario:12} {kind:9} {detail[:100]}")
    print("\n" + ", ".join(f"{kind}: {count}" for kind, count in sorted(counts.items())))
    return 1 if counts.get("MISSING") else 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).parent))
    sys.exit(main())
