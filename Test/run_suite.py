#!/usr/bin/env python3
"""Run the cluster scenario suite against an unpacked release package and write the evidence.

Every scenario deploys its own cluster from the package (``--package``, or ``CLUSTER_PACKAGE`` in
``host.env``) under ``CLUSTER_TEST_ROOT/<scenario>``, drives it through the package's
``cli/cluster.py`` and headless clients' Remote API, and asserts against structured state. Results
land in ``CLUSTER_TEST_RESULTS/<run>/``: one directory per scenario, ``host.json`` and
``versions.json`` first, ``resources.jsonl`` before every scenario, and a generated ``Summary.md``.

Usage::

    uv run python run_suite.py --package ../Build/dist/cluster-1.0.2          # every scenario
    uv run python run_suite.py P1-CHR-02 P1-BAL-01                            # a selection
    uv run python run_suite.py --list
    uv run python run_suite.py --repeat 3 P1-MANUAL                           # the three-pass rule

Run ``doctor.py`` first on a new host.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path


def _early_package(argv: list[str]) -> None:
    """--package must reach hostenv before anything imports it: the package's CLI is imported
    at bench import time and reads its environment then."""
    for index, value in enumerate(argv):
        if value == "--package" and index + 1 < len(argv):
            os.environ["CLUSTER_PACKAGE"] = str(Path(argv[index + 1]).expanduser().resolve())
        elif value.startswith("--package="):
            os.environ["CLUSTER_PACKAGE"] = str(Path(value.split("=", 1)[1]).expanduser().resolve())


_early_package(sys.argv[1:])
sys.path.insert(0, str(Path(__file__).resolve().parent))

import doctor  # noqa: E402
from bench import HOST, ScenarioResult, run_scenario  # noqa: E402
from coverage import classify  # noqa: E402
from scenarios import SCENARIOS as SCENARIOS_LIVE  # noqa: E402
from scenarios_admin import SCENARIOS_ADMIN  # noqa: E402
from scenarios_core import SCENARIOS_CORE  # noqa: E402
from scenarios_ext import SCENARIOS_EXT  # noqa: E402
from scenarios_planet import SCENARIOS_PLANET  # noqa: E402
from scenarios_player import SCENARIOS_PLAYER  # noqa: E402
from scenarios_presence import SCENARIOS_PRESENCE  # noqa: E402
from scenarios_recovery import SCENARIOS_RECOVERY  # noqa: E402
from scenarios_redeliver import SCENARIOS_REDELIVER  # noqa: E402
from scenarios_property_sync import SCENARIOS_PROPERTY_SYNC  # noqa: E402
from scenarios_suite import SCENARIOS_SUITE  # noqa: E402
from scenarios_restart import SCENARIOS_RESTART  # noqa: E402
from scenarios_saves import SCENARIOS_SAVES  # noqa: E402
from scenarios_topology import SCENARIOS_TOPOLOGY  # noqa: E402
from scenarios_wa import SCENARIOS_WA  # noqa: E402
from scenarios_wa_ops import SCENARIOS_WA_OPS  # noqa: E402
from scenarios_ai import SCENARIOS_AI  # noqa: E402
from scenarios_offline import SCENARIOS_OFFLINE  # noqa: E402
from scenarios_rejoin import SCENARIOS_REJOIN  # noqa: E402
from scenarios_stale_member import SCENARIOS_STALE_MEMBER  # noqa: E402
from scenarios_retire_loading import SCENARIOS_RETIRE_LOADING  # noqa: E402
from scenarios_member_limit import SCENARIOS_MEMBER_LIMIT  # noqa: E402
from scenarios_chat_names import SCENARIOS_CHAT_NAMES  # noqa: E402
from scenarios_admin_economy import SCENARIOS_ADMIN_ECONOMY  # noqa: E402
from scenarios_client_balance import SCENARIOS_CLIENT_BALANCE  # noqa: E402
from scenarios_rename import SCENARIOS_RENAME  # noqa: E402
from scenarios_chat_event import SCENARIOS_CHAT_EVENT  # noqa: E402
from scenarios_fast import SCENARIOS_FAST  # noqa: E402
from scenarios_orphan import SCENARIOS_ORPHAN  # noqa: E402
from scenarios_panel import SCENARIOS_PANEL  # noqa: E402
from scenarios_drift import SCENARIOS_DRIFT  # noqa: E402
from scenarios_delete_fault import SCENARIOS_DELETE_FAULT  # noqa: E402
from scenarios_grid_delete import SCENARIOS_GRID_DELETE  # noqa: E402
from scenarios_stop_after_warm_start import SCENARIOS_STOP_AFTER_WARM_START  # noqa: E402
from scenarios_npc import SCENARIOS_NPC  # noqa: E402
from scenarios_bot_ships import SCENARIOS_BOT_SHIPS  # noqa: E402
from scenarios_mods import SCENARIOS_MODS  # noqa: E402
from scenarios_spawn_preview import SCENARIOS_SPAWN_PREVIEW  # noqa: E402
from scenarios_blocks import SCENARIOS_BLOCKS  # noqa: E402
from scenarios_contracts import SCENARIOS_CONTRACTS  # noqa: E402
from scenarios_asleep import SCENARIOS_ASLEEP  # noqa: E402
from scenarios_staged_bags import SCENARIOS_STAGED_BAGS  # noqa: E402
from scenarios_seed_id import SCENARIOS_SEED_ID  # noqa: E402

SCENARIOS = {**SCENARIOS_CORE, **SCENARIOS_LIVE, **SCENARIOS_EXT, **SCENARIOS_ADMIN, **SCENARIOS_PLAYER, **SCENARIOS_RECOVERY, **SCENARIOS_RESTART,
             **SCENARIOS_SAVES, **SCENARIOS_TOPOLOGY, **SCENARIOS_WA, **SCENARIOS_WA_OPS, **SCENARIOS_AI, **SCENARIOS_OFFLINE, **SCENARIOS_REJOIN, **SCENARIOS_PLANET, **SCENARIOS_PRESENCE, **SCENARIOS_SUITE, **SCENARIOS_FAST, **SCENARIOS_ORPHAN, **SCENARIOS_DELETE_FAULT, **SCENARIOS_GRID_DELETE, **SCENARIOS_STOP_AFTER_WARM_START, **SCENARIOS_NPC, **SCENARIOS_BOT_SHIPS, **SCENARIOS_MODS,
             **SCENARIOS_REDELIVER, **SCENARIOS_PROPERTY_SYNC, **SCENARIOS_PANEL, **SCENARIOS_DRIFT, **SCENARIOS_SPAWN_PREVIEW, **SCENARIOS_BLOCKS,
             **SCENARIOS_CONTRACTS, **SCENARIOS_STALE_MEMBER, **SCENARIOS_RETIRE_LOADING, **SCENARIOS_MEMBER_LIMIT,
             **SCENARIOS_CHAT_NAMES, **SCENARIOS_ADMIN_ECONOMY, **SCENARIOS_RENAME, **SCENARIOS_CHAT_EVENT, **SCENARIOS_ASLEEP, **SCENARIOS_STAGED_BAGS,
             **SCENARIOS_CLIENT_BALANCE, **SCENARIOS_SEED_ID}
#: The longest a single scenario may take; used for the network-window guard.
SCENARIO_BUDGET = timedelta(minutes=float(os.environ.get("CLUSTER_TEST_SCENARIO_BUDGET_MINUTES", "30")))
#: Longest the suite will sit waiting for a network window to pass before giving up instead. The
#: nightly window is 22 minutes and the guard opens 30 minutes ahead of it, so an honest hold is
#: under an hour; anything longer means the window is misconfigured and stopping is the better
#: answer than an unattended suite asleep for half a day.
WINDOW_WAIT_CAP = timedelta(minutes=float(os.environ.get("CLUSTER_TEST_WINDOW_WAIT_CAP_MINUTES", "90")))


def window_conflict(window: str, now: datetime, budget: timedelta) -> str | None:
    """``HH:MM-HH:MM`` local; the reason a scenario starting now must not start, or None."""
    reason, _ = window_hold(window, now, budget)
    return reason


def window_hold(window: str, now: datetime, budget: timedelta) -> tuple[str | None, datetime | None]:
    """The reason a scenario must not start now, and the time it becomes safe again.

    A scenario must not merely avoid STARTING inside the window - it must not be IN FLIGHT when the
    window opens, because the host's nightly job kills every headless client by matching the Pulsar
    binary and cannot tell a bench client from a live one. Hence the budget: a scenario is refused
    from ``begin - budget``, not from ``begin``.
    """
    match = re.fullmatch(r"\s*(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})\s*", window or "")
    if not match:
        return None, None
    start_h, start_m, end_h, end_m = map(int, match.groups())
    for day in (-1, 0, 1):
        base = (now + timedelta(days=day)).replace(second=0, microsecond=0)
        begin = base.replace(hour=start_h, minute=start_m)
        end = base.replace(hour=end_h, minute=end_m)
        if end <= begin:
            end += timedelta(days=1)
        if now < end and now + budget > begin:
            return (f"the host's network window {window} starts at {begin:%Y-%m-%d %H:%M} and a scenario "
                    f"started now may run until {now + budget:%H:%M}"), end
    return None, None


def free_gb() -> float:
    for line in Path("/proc/meminfo").read_text().splitlines():
        if line.startswith("MemAvailable:"):
            return int(line.split()[1]) / 1024 / 1024
    return 0.0


def resources() -> dict:
    load = Path("/proc/loadavg").read_text().split()[:3]
    stat = os.statvfs(HOST.test_root if HOST.test_root.exists() else Path.home())
    return {"time": datetime.now().astimezone().isoformat(timespec="seconds"),
            "availableGb": round(free_gb(), 1), "load": [float(value) for value in load],
            "testRootFreeGb": round(stat.f_bavail * stat.f_frsize / 1024 ** 3, 1)}


def coverage_section(capabilities: dict | None = None) -> list[str]:
    table = classify(SCENARIOS, capabilities)
    kinds: dict[str, list[str]] = {}
    for scenario, (kind, _) in table.items():
        kinds.setdefault(kind, []).append(scenario)
    lines = [
        "", "## Coverage of TestPlan.md", "",
        f"TestPlan.md names {len(table)} scenarios. " + ", ".join(
            f"**{len(ids)} {kind}**" for kind, ids in sorted(kinds.items())) + ".",
        "", "| Scenario | Covered by | Detail |", "|---|---|---|",
    ]
    for scenario, (kind, detail) in sorted(table.items()):
        lines.append(f"| `{scenario}` | {kind} | {detail.replace('|', chr(92) + '|')} |")
    return lines


def summary_markdown(results: list[ScenarioResult], run_id: str, started: datetime, results_root: Path) -> str:
    passed = [result for result in results if result.passed]
    failed = [result for result in results if not result.passed]
    manifest = json.loads((results_root / "versions.json").read_text()).get("package", {})
    lines = [
        f"# Cluster test run {run_id}", "",
        f"Started {started.astimezone().isoformat(timespec='seconds')} on `{os.uname().nodename}`, package "
        f"`{manifest.get('version')}` ({manifest.get('describe') or manifest.get('commit')}). "
        f"{len(results)} scenario runs, **{len(passed)} passed, {len(failed)} failed**, "
        f"{round(sum(result.seconds for result in results) / 60, 1)} minutes of wall clock.", "",
        "Every verdict is a comparison against structured state - the `diag` snapshot, the Ownership",
        "Registry, logs, or a server-side measurement. Host and versions: [host.json](host.json),",
        "[versions.json](versions.json).", "",
        "## Results", "",
        "| Scenario | Title | Tier | Result | Checks | Time | Evidence |",
        "|---|---|---|---|---|---|---|",
    ]
    for result in results:
        checks = f"{sum(1 for check in result.checks if check.passed)}/{len(result.checks)}"
        lines.append(f"| `{result.scenario}` | {result.title} | {result.tier} | **{'PASS' if result.passed else 'FAIL'}** "
                     f"| {checks} | {result.seconds:.0f}s | [{result.directory}/]({result.directory}/) |")
    host = json.loads((results_root / "host.json").read_text()) if (results_root / "host.json").is_file() else {}
    lines += coverage_section(host.get("capabilities"))
    lines += ["", "## Detail", ""]
    for result in results:
        lines += [f"### {result.scenario} - {result.title}", ""]
        if result.exercised:
            lines += ["Exercised: " + ", ".join(f"`{key}={value}`" for key, value in result.exercised.items()), ""]
        if result.error:
            lines += ["```", result.error[:4000], "```", ""]
        if result.checks:
            lines += ["| Check | Result | Detail |", "|---|---|---|"]
            for check in result.checks:
                detail = (check.detail or "").replace("|", "\\|").replace("\n", " ")[:300]
                lines.append(f"| {check.label} | {'PASS' if check.passed else 'FAIL'} | {detail} |")
            lines.append("")
        present = [name for name in result.artifacts if (results_root / result.directory / name).exists()]
        screenshots = [name for name in present if name.endswith(".png")]
        if screenshots:
            lines += ["Screenshots:", ""] + [f"- [{name}]({result.directory}/{name})" for name in screenshots] + [""]
        data = sorted(name for name in present if not name.endswith(".png"))
        if data:
            lines += ["Data files: " + ", ".join(f"[{name}]({result.directory}/{name})" for name in data), ""]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("scenarios", nargs="*", help="scenario ids to run; default: every native scenario")
    parser.add_argument("--package", help="unpacked release package (default CLUSTER_PACKAGE, else the newest build)")
    parser.add_argument("--list", action="store_true", help="list the scenarios and exit")
    parser.add_argument("--repeat", type=int, default=1, help="run the selection this many times in a row")
    parser.add_argument("--results", help="results directory (default CLUSTER_TEST_RESULTS/<run id>)")
    parser.add_argument("--ignore-window", action="store_true", help="start scenarios inside the network window")
    args = parser.parse_args(argv)

    if args.list:
        for scenario, (_, title, tier) in SCENARIOS.items():
            print(f"{scenario:12} tier {tier:4} {title}")
        return 0
    unknown = [scenario for scenario in args.scenarios if scenario not in SCENARIOS]
    if unknown:
        print("unknown scenario(s): " + ", ".join(unknown) + " (see --list)", file=sys.stderr)
        return 2
    uncovered = [scenario for scenario, (kind, _) in classify(SCENARIOS).items() if kind == "MISSING"]
    if uncovered:
        print("TestPlan scenarios with no runner and no deferral reason: " + ", ".join(uncovered), file=sys.stderr)
        return 3

    report = doctor.check()
    if report["failures"]:
        print("doctor: the host does not meet the contract:\n  " + "\n  ".join(report["failures"]), file=sys.stderr)
        return 4
    selected = (args.scenarios or list(SCENARIOS)) * max(1, args.repeat)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ")
    results_root = Path(args.results).expanduser() if args.results else HOST.results / run_id
    results_root.mkdir(parents=True, exist_ok=True)
    (results_root / "host.json").write_text(json.dumps(report, indent=2, default=str))
    (results_root / "versions.json").write_text(json.dumps(doctor.versions(), indent=2, default=str))
    started = datetime.now(timezone.utc)
    print(f"Results: {results_root}")

    results: list[ScenarioResult] = []
    seen: dict[str, int] = {}
    for scenario in selected:
        conflict, resume_at = ((None, None) if args.ignore_window
                               else window_hold(HOST.network_window, datetime.now(), SCENARIO_BUDGET))
        if conflict:
            # WAIT IT OUT RATHER THAN ABANDON THE RUN. Stopping here was right for a suite started
            # by hand - you want to be told, not to sit for an hour - but it silently costs an
            # UNATTENDED multi-pass run its whole night: a pass that meets the window returns
            # `incomplete` (exit 1), the three-pass script treats that as a red pass and stops, and
            # the remaining passes never run. The host is idle either way; the difference is
            # whether the suite continues afterwards. --ignore-window still skips the guard.
            wait = (resume_at - datetime.now()).total_seconds()
            if wait > WINDOW_WAIT_CAP.total_seconds():
                print(f"stopping before {scenario}: {conflict}; resuming at {resume_at:%H:%M} would "
                      f"mean waiting {wait / 3600:.1f} h, past the {WINDOW_WAIT_CAP} cap", file=sys.stderr)
                break
            if wait > 0:
                print(f"holding before {scenario}: {conflict}; resuming at {resume_at:%H:%M} "
                      f"({wait / 60:.0f} min)", file=sys.stderr, flush=True)
                time.sleep(wait)
        snapshot = resources()
        with open(results_root / "resources.jsonl", "a") as log:
            log.write(json.dumps({"before": scenario, **snapshot}) + "\n")
        if snapshot["availableGb"] < HOST.min_free_gb:
            print(f"stopping before {scenario}: {snapshot['availableGb']} GB available, below the "
                  f"{HOST.min_free_gb} GB floor (CLUSTER_TEST_MIN_FREE_GB)", file=sys.stderr)
            break
        seen[scenario] = seen.get(scenario, 0) + 1
        directory = scenario if seen[scenario] == 1 else f"{scenario}.{seen[scenario]}"
        function, title, tier = SCENARIOS[scenario]
        results.append(run_scenario(function, scenario, title, tier, results_root, directory=directory))
        doctor.sweep_test_root()
        time.sleep(3)

    summary = results_root / "Summary.md"
    summary.write_text(summary_markdown(results, run_id, started, results_root))
    (results_root / "results.json").write_text(json.dumps(
        [{"scenario": result.scenario, "directory": result.directory, "passed": result.passed,
          "seconds": result.seconds, "checks": [vars(check) for check in result.checks],
          "error": result.error, "exercised": result.exercised} for result in results], indent=2, default=str))
    failed = [result.directory for result in results if not result.passed]
    print(f"\n{len(results) - len(failed)}/{len(results)} scenario runs passed. Summary: {summary}")
    if failed:
        print("Failed: " + ", ".join(failed))
    incomplete = len(results) < len(selected)
    return 1 if failed or incomplete else 0


if __name__ == "__main__":
    sys.exit(main())
