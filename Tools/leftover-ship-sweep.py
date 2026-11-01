#!/usr/bin/env python3
"""Remove the leftover ships of a pilot fleet from a cluster (#1334).

A fleet names each pilot's ship after the pilot ('Ion Pilot-N'). Ships of recycled or killed clients stay
where they stopped and block the corridors the next pilots fly. This sweep reads every node's
GET /cluster/partitions, picks the grids whose name matches --pattern and whose number is not an active
pilot, and asks the node that holds each one to remove it (POST /cluster/grid-delete?entityId=&name=).
The node refuses a grid that has a seated pilot, a player controlling it, mechanical links, a handover in
progress or an import grace running; refusals are counted, not retried. Grids in offline partitions are
on no node and are not seen.

    CLUSTER_ADMIN_TOKEN=... python3 Tools/leftover-ship-sweep.py \\
        --controls 192.168.1.80:28216-28222 --active 1-86 [--delete]

Without --delete it only lists what it would remove. The admin token comes from the environment and is
never printed. Prints one JSON summary line; exit code 0 unless a node could not be read.
"""

import argparse
import collections
import json
import os
import re
import sys
import urllib.parse
import urllib.request


def numbers(spec: str) -> set:
    """'1-86,90' -> {1..86, 90}."""
    out = set()
    for part in filter(None, spec.split(",")):
        low, _, high = part.partition("-")
        out.update(range(int(low), int(high or low) + 1))
    return out


def controls(spec: str) -> list:
    """'host:28216-28222,host2:28300' -> ['host:28216', ..., 'host2:28300']."""
    out = []
    for part in filter(None, spec.split(",")):
        host, _, ports = part.rpartition(":")
        out += [f"{host}:{port}" for port in sorted(numbers(ports))]
    return out


def call(control: str, path: str, token: str, method: str = "GET") -> dict:
    request = urllib.request.Request(f"http://{control}/cluster/{path}", method=method)
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument(
        "--controls", required=True, help="node control endpoints, host:port or host:low-high, comma separated"
    )
    parser.add_argument("--active", default="", help="pilot numbers in use, e.g. 1-86,90; their ships are kept")
    parser.add_argument("--pattern", default=r"Ion Pilot-(\d+)", help="full-match regex; group 1 is the pilot number")
    parser.add_argument("--delete", action="store_true", help="remove them (default: list only)")
    args = parser.parse_args()
    token = os.environ.get("CLUSTER_ADMIN_TOKEN", "")
    pattern = re.compile(args.pattern)
    active = numbers(args.active)

    summary = collections.Counter()
    refused = collections.Counter()
    unreadable = []
    deleted = []
    for control in controls(args.controls):
        try:
            report = call(control, "partitions", token)
        except OSError as error:
            unreadable.append(f"{control}: {error}")
            continue
        for partition in report.get("partitions", []):
            for member in partition.get("members") or []:
                match = pattern.fullmatch(member.get("name") or "")
                if member.get("type") != "MyCubeGrid" or not match:
                    continue
                summary["matched"] += 1
                if int(match.group(1)) in active:
                    summary["active"] += 1
                    continue
                summary["leftover"] += 1
                if not args.delete:
                    deleted.append(member["name"])
                    continue
                query = urllib.parse.urlencode({"entityId": member["id"], "name": member["name"]})
                try:
                    result = call(control, f"grid-delete?{query}", token, "POST")
                except OSError as error:
                    refused[f"request failed: {error}"] += 1
                    continue
                if result.get("deleted"):
                    deleted.append(member["name"])
                else:
                    refused[result.get("reason", "?")] += 1
    summary["deleted" if args.delete else "would_delete"] = len(deleted)
    print(json.dumps({**summary, "refused": dict(refused), "names": sorted(deleted), "unreadable": unreadable}))
    return 1 if unreadable else 0


if __name__ == "__main__":
    sys.exit(main())
