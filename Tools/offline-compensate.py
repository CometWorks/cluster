#!/usr/bin/env python3
"""Dry-run an offline Final against a running node's definitions, without claiming/loading it."""
import argparse
import json
import os
from pathlib import Path
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("node", help="node control root, including /cluster")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--save", type=Path, help="exact offline Final .mps file")
    source.add_argument("--partition", type=int, help="exact offline row on a save holder's host")
    parser.add_argument("--hours", type=float, required=True)
    args = parser.parse_args()
    if not 0 <= args.hours <= 168:
        parser.error("hours must be between 0 and 168")
    query = {"hours": args.hours}
    if args.partition is not None:
        query["partition"] = args.partition
    data = args.save.read_bytes() if args.save else b""
    request = urllib.request.Request(args.node.rstrip("/") + "/trigger/compensate-dry-run?" + urllib.parse.urlencode(query), data=data, method="POST")
    token = os.environ.get("CLUSTER_ADMIN_TOKEN")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    request.add_header("Content-Type", "application/octet-stream")
    with urllib.request.urlopen(request, timeout=120) as response:
        print(json.dumps(json.load(response), indent=2))


if __name__ == "__main__":
    main()
