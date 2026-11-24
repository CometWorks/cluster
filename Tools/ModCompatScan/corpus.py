"""Build the cluster's static Workshop reports from the subscription-ranked catalog."""

import argparse
import csv
import hashlib
import json
import subprocess
from collections import Counter, defaultdict
from pathlib import Path


def read_tsv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8", newline="") as source:
        return list(csv.DictReader(source, delimiter="\t"))


def write_tsv(path: Path, columns: list[str], rows) -> None:
    with path.open("w", encoding="utf-8", newline="") as target:
        writer = csv.writer(target, delimiter="\t", lineterminator="\n")
        writer.writerow(columns)
        writer.writerows(rows)


def scan(tool: Path, root: Path, ids: list[str]):
    # Limit how many noncollectible Workshop assemblies share a process.
    for start in range(0, len(ids), 35):
        batch = ids[start : start + 35]
        run = subprocess.run(
            ["dotnet", tool, "--evidence", *(root / item for item in batch)],
            check=True,
            text=True,
            stdout=subprocess.PIPE,
        )
        results = [json.loads(line) for line in run.stdout.splitlines()]
        if [row["mod"] for row in results] != batch:
            raise ValueError("Scanner output does not match input batch")
        yield from results


def world_action(key: str) -> bool:
    # Only a direct call from a session simulation callback counts. A read, raycast
    # or role check in UpdateAfterSimulation is common and has no side effect.
    name = key.partition("(")[0].rsplit(".", 1)[-1].split("`")[0]
    return name.startswith(
        (
            "Spawn",
            "CreateFromPrefab",
            "AddEntity",
            "RemoveEntity",
            "SendMessageTo",
            "ShowNotification",
            "SendChatMessage",
            "WriteFileInWorldStorage",
            "DeleteFileInWorldStorage",
        )
    ) or name in {
        "SetVariable",
        "RemoveVariable",
        "RequestVoxelCutoutSphere",
        "DoDamage",
    }


def classify(row: dict, policy: dict[str, list[str]], scripted: bool):
    detail = row["evidence"]
    breaks, shims = Counter(), set()
    needs_adaptation = False
    wa_only = True
    unmapped = 0
    for key, sites in detail["Members"].items():
        rule = policy.get(key)
        if rule is None or rule[2] == "?" or "UNCERTAIN:" in rule[6]:
            unmapped += sites
            continue
        if rule[2] == "-":
            continue
        # Definitions and settings have a B7 policy for mutations. A read of the
        # stock scalar cannot diverge. Keep object or by-ref accesses unresolved
        # when the scanner cannot follow what the caller does with the result.
        if (
            "changing shared definition data at runtime affects this process" in rule[6]
            or "mod writes to that object are not broadcast" in rule[6]
            or key in detail["VslDelegateFields"]
        ):
            sites = detail["Writes"].get(key, 0)
            if not sites or key not in detail["VslDelegateFields"]:
                unmapped += detail["IndirectAccess"].get(key, 0)
            if not sites:
                continue
        breaks.update({b: sites for b in rule[2].split(",")})
        if rule[3] == "yes":
            shims.add(
                {
                    (
                        "B5",
                        "player-nodes + to-node / broadcast",
                    ): "client-message-relay",
                    ("B5", "player-nodes + to-node"): "client-message-relay",
                    ("B7", "request (WA)"): "wa-storage",
                    ("B7", "broadcast"): "replicate-state",
                    ("B8", "player-nodes"): "player-routing",
                    ("B1", "player-nodes"): "player-roster",
                }[(rule[2], rule[4])]
            )
        else:
            needs_adaptation = True
        # Single-authority placement can cover session state and its own storage.
        wa_only &= rule[1] == "per-node" and rule[2] in ("B6", "B7")
    for b, n in (
        ("B2", detail["EntityCollections"]),
        ("B4", detail["MutableLogicFields"]),
        ("B6", sum(n for k, n in detail["UpdateMembers"].items() if world_action(k))),
    ):
        if n:
            breaks[b] += n
    if detail["MutableLogicFields"]:
        shims.add("handover-state-review")
    gates = sum(
        sites
        for key, sites in detail["Members"].items()
        if key
        in {
            "VRage.Game.ModAPI.IMyMultiplayer.IsServer",
            "VRage.Game.ModAPI.IMySession.IsServer",
            "VRage.Game.ModAPI.IMyUtilities.IsDedicated",
        }
    )
    incomplete = (
        len(row["compileErrors"])
        or row["unresolvedCalls"]
        or detail["UnresolvedTypes"]
        or (scripted and row["class"] == "A")
        or (not scripted and row["class"] != "A")
    )
    if incomplete:
        verdict = "U"
    elif not scripted:
        verdict = "A"
    elif (
        row["placement"] == "adapted"
        or breaks["B2"]
        or (breaks and not wa_only and needs_adaptation)
    ):
        verdict = "D"
    elif unmapped:
        verdict = "U"
    elif not breaks:
        verdict = "B"
    elif set(breaks) == {"B4"}:
        verdict = "B-state"
    elif (
        wa_only
        and not breaks["B4"]
        and "session-components" in row["groups"]
        and "game-logic-components" not in row["groups"]
    ):
        verdict = "C"
    elif needs_adaptation or breaks["B6"] or breaks["B4"]:
        verdict = "D"
    else:
        verdict = "S"
    return verdict, breaks, shims, gates, unmapped


RECIPES = {
    "A": "content-load",
    "B": "script-load",
    "B-state": "handover",
    "C": "wa-only-side-effects",
    "S": "shim-off-on",
    "D": "differential",
    "U": "inspect-scan",
}


def report(catalog: Path, inventory: Path, tool: Path, scans, out: Path) -> None:
    inputs = read_tsv(catalog)
    mods = {r["id"]: r for r in inputs if r["item_type"] == "mod"}
    if len(mods) != sum(r["item_type"] == "mod" for r in inputs):
        raise ValueError("Duplicate Workshop ID in catalog")
    inventory_rows = read_tsv(inventory)
    scripts = {r["id"]: int(r["has_scripts"]) for r in inventory_rows}
    if len(scripts) != len(inventory_rows):
        raise ValueError("Duplicate Workshop ID in script inventory")
    if scripts.keys() != mods.keys():
        raise ValueError("Script inventory and mod catalog differ")
    raw_policy = subprocess.check_output(["dotnet", tool, "--policy-tsv"], text=True)
    policy = {r[0]: r for r in csv.reader(raw_policy.splitlines(), delimiter="\t")}
    frequency = defaultdict(Counter)
    summary = defaultdict(Counter)
    rows = {}
    for scan_row in scans:
        item = scan_row["mod"]
        if item not in mods:
            continue
        if item in rows:
            raise ValueError(f"Duplicate scan for {item}")
        info = mods[item]
        subs = int(info["lifetime_subscriptions"])
        verdict, breaks, shims, gates, unmapped = classify(
            scan_row, policy, bool(scripts[item])
        )
        for key, count in scan_row["evidence"]["Members"].items():
            frequency[key].update(mods=1, sites=count, lifetime_subscriptions=subs)
        for label in [f"verdict/{verdict}", *breaks, *("shim/" + b for b in shims)]:
            summary[label].update(mods=1, lifetime_subscriptions=subs)
        rows[item] = [
            info["rank"],
            item,
            info["title"],
            subs,
            info["time_updated"],
            verdict,
            scan_row["class"] if not scan_row["compileErrors"] else "",
            scan_row["ilSha256"],
            len(scan_row["compileErrors"]),
            scan_row["unresolvedCalls"],
            scan_row["evidence"]["UnresolvedTypes"],
            unmapped,
            gates,
            *(breaks[f"B{i}"] for i in range(1, 9)),
            ",".join(sorted(shims)),
            RECIPES[verdict],
        ]
    if rows.keys() != mods.keys():
        raise ValueError(f"Missing scans for {sorted(mods.keys() - rows.keys())}")
    out.mkdir(parents=True, exist_ok=True)
    write_tsv(
        out / "verdicts.tsv",
        [
            "rank",
            "id",
            "title",
            "lifetime_subscriptions",
            "time_updated",
            "verdict",
            "runtime_class",
            "il_sha256",
            "compile_diagnostics",
            "unresolved_il",
            "unresolved_types",
            "unverified_member_sites",
            "role_check_sites",
            *(f"B{i}" for i in range(1, 9)),
            "candidate_shims",
            "test_recipe",
        ],
        (rows[key] for key in mods),
    )
    write_tsv(
        out / "members.tsv",
        ["member", "policy", "mods", "sites", "lifetime_subscriptions"],
        (
            [
                key,
                policy[key][1] if key in policy else "unmapped",
                counts["mods"],
                counts["sites"],
                counts["lifetime_subscriptions"],
            ]
            for key, counts in sorted(
                frequency.items(), key=lambda p: (-p[1]["lifetime_subscriptions"], p[0])
            )
        ),
    )
    write_tsv(
        out / "summary.tsv",
        ["category", "mods", "lifetime_subscriptions"],
        (
            [key, counts["mods"], counts["lifetime_subscriptions"]]
            for key, counts in sorted(summary.items())
        ),
    )
    print(
        f"{len(rows)} loadable entries; catalog SHA256 {hashlib.sha256(catalog.read_bytes()).hexdigest()}"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tool", type=Path, help="ModCompatScan.dll")
    parser.add_argument(
        "corpus",
        type=Path,
        help="folder containing catalogue.tsv, scripts.tsv and 244850/",
    )
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    catalog = args.corpus / "catalogue.tsv"
    ids = [r["id"] for r in read_tsv(catalog) if r["item_type"] == "mod"]
    report(
        catalog,
        args.corpus / "scripts.tsv",
        args.tool,
        scan(args.tool, args.corpus / "244850", ids),
        args.output,
    )


if __name__ == "__main__":
    main()
