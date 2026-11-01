#!/usr/bin/env python3
"""Move test-only route blocks out of Shared/Cluster/ClusterControlServer.cs into this plugin.

A movable block is one arm of ClusterControlServer.Handle's if-chain of the exact form

    if (context.Request.HttpMethod == "M" && path.EndsWith("/suffix", StringComparison.OrdinalIgnoreCase))
    {
        ...
    }

with the comment lines directly above it (an arm without the method test is moved as method "*",
which matches any method). Its body is copied verbatim into a handler lambda
``(context, path) => { ... }`` in Source/MovedRoutes.cs, which imports the control server's static
members (``using static``; the plugin build publicizes the cluster assembly), so the body compiles
unchanged. ``return;`` inside a body returns from the lambda, as it returned from Handle.

    python3 move_routes.py --list                 # the movable test routes
    python3 move_routes.py SUFFIX...              # move these (e.g. /banking-test-create)
    python3 move_routes.py --all-test             # move every movable route with "test" in its suffix
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
SERVER = REPO / "Shared/Cluster/ClusterControlServer.cs"
TARGET = HERE / "Source/MovedRoutes.cs"
ARM = re.compile(r'^\t\t\tif \((?:context\.Request\.HttpMethod == "([A-Z]+)" && )?path\.EndsWith\("(/[A-Za-z0-9/_-]+)", '
                 r'StringComparison\.OrdinalIgnoreCase\)\)\n')
BEGIN = "// -- moved routes begin --\n"
END = "        // -- moved routes end --\n"


def arms(text: str) -> list[dict]:
    """Every movable arm: method, suffix, and the [start, end) span including its leading comments."""
    lines = text.splitlines(keepends=True)
    offsets = [0]
    for line in lines:
        offsets.append(offsets[-1] + len(line))
    found = []
    for index, line in enumerate(lines):
        match = ARM.match(line)
        if not match or index + 1 >= len(lines) or lines[index + 1] != "\t\t\t{\n":
            continue
        close = next(i for i in range(index + 2, len(lines)) if lines[i] == "\t\t\t}\n")
        first = index
        while first > 0 and lines[first - 1].startswith("\t\t\t//"):
            first -= 1
        found.append({"method": match.group(1) or "*", "suffix": match.group(2),
                      "start": offsets[first], "end": offsets[close + 1],
                      "comments": "".join(lines[first:index]), "body": "".join(lines[index + 2:close])})
    return found


def product_suffixes(text: str) -> list[str]:
    return re.findall(r'path\.(?:EndsWith|Equals)\("(/[A-Za-z0-9/_-]+)"', text)


def render(arm: dict) -> str:
    comments = "".join("        " + line.lstrip("\t") for line in arm["comments"].splitlines(keepends=True))
    body = "".join(("\t" + line) if line.strip() else line for line in arm["body"].splitlines(keepends=True))
    return (f'{comments}        TestRoutes.Add("{arm["method"]}", "{arm["suffix"]}", (context, path) =>\n'
            f"\t\t{{\n{body}\t\t}});\n")


def target_text() -> str:
    if TARGET.exists():
        return TARGET.read_text()
    usings = "".join(line + "\n" for line in SERVER.read_text().splitlines() if line.startswith("using "))
    return (usings + "using ClusterPlugin;\nusing static ClusterPlugin.ClusterControlServer;\n\n"
            "namespace ClusterTestPlugin;\n\n"
            "/// <summary>Test-only routes moved verbatim out of ClusterControlServer.Handle by move_routes.py.\n"
            "/// The bodies use the control server's own helpers (Invoke, Write, QueryLong, ...) through the\n"
            "/// static import; keep them as they were so a diff against the product history stays readable.</summary>\n"
            "internal static class MovedRoutes\n{\n    public static void Register()\n    {\n"
            f"        {BEGIN}{END}    }}\n}}\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("suffixes", nargs="*")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--all-test", action="store_true")
    args = parser.parse_args()
    text = SERVER.read_text()
    movable = arms(text)
    if args.list:
        for arm in movable:
            print(f'{arm["method"]:5} {arm["suffix"]}')
        return 0
    wanted = set(args.suffixes)
    chosen = [arm for arm in movable if arm["suffix"] in wanted or (args.all_test and "test" in arm["suffix"])]
    missing = wanted - {arm["suffix"] for arm in chosen}
    if missing:
        print("not movable (or not found): " + ", ".join(sorted(missing)), file=sys.stderr)
        return 2
    # An extension route is tried BEFORE the product routes, so a moved suffix must not also be the
    # tail of a route that stays in the product.
    remaining = text
    for arm in sorted(chosen, key=lambda a: a["start"], reverse=True):
        remaining = remaining[:arm["start"]] + remaining[arm["end"]:]
    for arm in chosen:
        shadowed = [p for p in product_suffixes(remaining) if p != arm["suffix"] and p.endswith(arm["suffix"])]
        if shadowed:
            print(f'{arm["suffix"]} would shadow product routes {shadowed}', file=sys.stderr)
            return 3
    target = target_text()
    insert = "".join(render(arm) for arm in chosen)
    target = target.replace(END, insert + END, 1)
    SERVER.write_text(remaining)
    TARGET.write_text(target)
    print(f"moved {len(chosen)} route arms: " + ", ".join(f'{a["method"]} {a["suffix"]}' for a in chosen))
    return 0


if __name__ == "__main__":
    sys.exit(main())
