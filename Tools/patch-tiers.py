#!/usr/bin/env python3
"""Classify Shared/Patches by how badly each patch fails if the game changes under it (T-0122).

EnsureCode declares an expected IL body hash for a patch target and refuses the plugin load when
the game no longer matches. Adoption has to be staged, because a wrong or stale hash STOPS A NODE
FROM STARTING - so the question is which patches to guard first. That is not "the important ones",
it is the ones whose failure is SILENT:

  1-transpiler               rewrites IL against an anchor. If the anchor moves, the patch either
                             throws at patch time or - worse - matches something else.
  2-prefix-SKIPS             returns false somewhere, so it REPLACES the original. If the original
                             gains behaviour, that behaviour silently stops happening.
  3-prefix-bool-never-false  returns bool but never false in any branch: it observes, and the
                             original always runs.
  4-prefix-void / 5-postfix  cannot suppress the original at all. A game change degrades these to
                             a wrong trace or a missed observation, not wrong semantics.

Tiers 1 and 2 are the enforce-first set. Run from the repo root:  python3 Tools/patch-tiers.py

LIMITS, because this is a regex over source and not a compiler:
  * "returns bool and contains `return false`" is a heuristic. A prefix whose false branch is
    unreachable is over-classified, and one that suppresses via a ref/out parameter or by setting
    __result with a void return is UNDER-classified. Read the method before trusting the tier.
  * Patch methods are found by attribute + `static` signature; an unconventional declaration is
    missed entirely. The printed total is the check - compare it against a grep for HarmonyPrefix
    and friends before believing a small number.
"""
import collections
import pathlib
import re
import sys

KINDS = [("HarmonyTranspiler", "transpiler"), ("HarmonyPrefix", "prefix"),
         ("HarmonyPostfix", "postfix"), ("HarmonyFinalizer", "finalizer")]
SIG = re.compile(r"((?:\s*\[[^\]]+\]\s*)+)\s*(?:private|public|internal)\s+static\s+"
                 r"([\w<>\[\],\s\.\?]+?)\s+(\w+)\s*\(")


def body_at(src, brace):
    depth = 0
    for i in range(brace, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[brace:i + 1]
    return ""


def tier_of(attrs, ret, body):
    kind = next((k for tag, k in KINDS if tag in attrs), None)
    if kind is None:
        return None
    if kind == "transpiler":
        return "1-transpiler"
    if kind == "prefix":
        if ret != "bool":
            return "4-prefix-void"
        return "2-prefix-SKIPS" if re.search(r"return\s+false\s*;", body) else "3-prefix-bool-never-false"
    return "5-" + kind


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "Shared/Patches")
    if not root.is_dir():
        print(f"no such directory: {root} (run from the repo root)")
        return 2
    rows = []
    for path in sorted(root.glob("*.cs")):
        src = path.read_text()
        for m in SIG.finditer(src):
            brace = src.find("{", m.end())
            tier = tier_of(m.group(1), m.group(2).strip(), body_at(src, brace) if brace > 0 else "")
            if tier:
                rows.append((tier, path.name, m.group(3)))

    counts = collections.Counter(t for t, _, _ in rows)
    print(f"patch methods: {len(rows)} across {len({f for _, f, _ in rows})} files\n")
    for tier in sorted(counts):
        print(f"  {tier:28s} {counts[tier]:3d}")

    risky = [r for r in rows if r[0][0] in "12"]
    print(f"\nENFORCE-FIRST (tiers 1-2): {len(risky)} methods in "
          f"{len({f for _, f, _ in risky})} files\n")
    for tier, path, name in sorted(risky):
        print(f"  {tier:20s} {path:44s} {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
