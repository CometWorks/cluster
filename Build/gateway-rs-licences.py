"""List the crates gateway-rs links, with their licence expressions (Build/build-gateway-rs.sh).

Input: `cargo metadata --format-version 1 --filter-platform <host>` JSON. Only normal
dependencies of gateway-rs, transitively - dev and build dependencies are not in the binary.
"""
import json
import sys

meta = json.load(open(sys.argv[1]))
resolve = {node["id"]: node for node in meta["resolve"]["nodes"]}
packages = {package["id"]: package for package in meta["packages"]}
root = next(package["id"] for package in meta["packages"] if package["name"] == "gateway-rs")
seen, stack = set(), [root]
while stack:
    current = stack.pop()
    if current in seen:
        continue
    seen.add(current)
    for dependency in resolve[current]["deps"]:
        if any(kind["kind"] is None for kind in dependency["dep_kinds"]):
            stack.append(dependency["pkg"])
print("gateway-rs links these crates (name version: licence):\n")
for package in sorted((packages[i] for i in seen), key=lambda p: p["name"]):
    licence = package.get("license") or "see " + (package.get("repository") or "crates.io")
    print(f"{package['name']} {package['version']}: {licence}")
