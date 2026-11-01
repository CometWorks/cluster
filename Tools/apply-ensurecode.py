#!/usr/bin/env python3
"""Insert [EnsureCode("<hash>")] onto patch methods, from the reporter's own output (T-0122).

The hash is a RUNTIME quantity, so the workflow is: start a node with CLUSTER_PATCH_HASHES=1,
save the lines it logs, then feed them here. That removes the step where 47 hashes get copied by
hand into 33 files, which is exactly the step where a typo stops a node from starting.

    grep 'EnsureCode-hash' node-1.log > /tmp/hashes.txt
    python3 Tools/apply-ensurecode.py /tmp/hashes.txt --tier 1,2 --dry-run
    python3 Tools/apply-ensurecode.py /tmp/hashes.txt --tier 1,2 --apply

Input lines are the reporter's format:
    EnsureCode-hash <hash> <declared|-> <Class>.<Method> -> <target description>

SAFETY, because getting this wrong stops nodes:
  * Refuses any line whose hash is not 8 hex digits - the reporter prints ???????? for a target
    it could not resolve or read, and those must be looked at by a person, never auto-applied.
  * Skips a method that already declares a hash, and never edits an existing attribute. Updating
    a hash after a game update is a deliberate act: delete the old attribute first, or pass
    --replace to do it knowingly.
  * --tier restricts to the failure-mode tiers from patch-tiers.py, so the first deploy can be
    the 47 transpilers-and-skipping-prefixes rather than all 140 at once.
  * --dry-run is the default. --apply is required to write.
  * Adds `using Shared.Tools;` when the file lacks it. Without this every edited file fails to
    compile with CS0246 - found by BUILDING after a test apply, not by reading the diff, which
    looked perfectly correct.
  * Matches the method by CLASS AND NAME. If a class declares two patch methods with the same
    name (overloads of a prefix), it refuses rather than guess - reported, not silently skipped.
"""
import argparse
import pathlib
import re
import sys

LINE = re.compile(r"EnsureCode-hash\s+(?P<hash>\S+)\s+(?P<declared>\S+)\s+"
                  r"(?P<cls>\w+)\.(?P<method>\w+)\s+->\s+(?P<target>.*)")
HEX8 = re.compile(r"^[0-9a-f]{8}$")


def load_tiers(root):
    """Method -> tier, by re-running the same classification patch-tiers.py uses."""
    sys.path.insert(0, str(root / "Tools"))
    import importlib.util
    spec = importlib.util.spec_from_file_location("pt", root / "Tools" / "patch-tiers.py")
    pt = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pt)
    tiers = {}
    for path in sorted((root / "Shared" / "Patches").glob("*.cs")):
        src = path.read_text()
        for m in pt.SIG.finditer(src):
            brace = src.find("{", m.end())
            tier = pt.tier_of(m.group(1), m.group(2).strip(),
                              pt.body_at(src, brace) if brace > 0 else "")
            if tier:
                tiers[(path.stem, m.group(3))] = tier
    return tiers


def ensure_using(src):
    """Add `using Shared.Tools;` if absent. Every edited file needs it for the attribute to
    resolve; without it the build fails CS0246 on all 47 at once."""
    if re.search(r"^using\s+Shared\.Tools\s*;", src, re.M):
        return src
    usings = list(re.finditer(r"^using\s+[^;]+;[ \t]*\r?\n", src, re.M))
    if not usings:
        return "using Shared.Tools;\n\n" + src
    # Repo convention is System.* first, then the rest alphabetically. Insert among the
    # non-System block so the result looks hand-written rather than sorted by a machine.
    def name_of(u):
        return re.match(r"^using\s+([^;]+);", u.group(0)).group(1).strip()

    others = [u for u in usings if not name_of(u).split(".")[0] == "System"]
    for u in others:
        if name_of(u) > "Shared.Tools":
            return src[:u.start()] + "using Shared.Tools;\n" + src[u.start():]
    last = (others or usings)[-1]
    return src[:last.end()] + "using Shared.Tools;\n" + src[last.end():]


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("hashes", help="file of EnsureCode-hash lines from a node log")
    ap.add_argument("--root", default=".", help="repo root (default: cwd)")
    ap.add_argument("--tier", default="1,2",
                    help="comma-separated leading tier digits to apply to (default 1,2)")
    ap.add_argument("--apply", action="store_true", help="write the files (default: dry run)")
    ap.add_argument("--replace", action="store_true",
                    help="also rewrite an EXISTING [EnsureCode] attribute; off by default "
                         "because updating a hash should be a deliberate act")
    args = ap.parse_args()

    root = pathlib.Path(args.root).resolve()
    patches = root / "Shared" / "Patches"
    if not patches.is_dir():
        print(f"no {patches} - run from the repo root or pass --root")
        return 2

    tiers = load_tiers(root)
    want = {t.strip() for t in args.tier.split(",") if t.strip()}

    planned, skipped = [], []
    for raw in pathlib.Path(args.hashes).read_text().splitlines():
        m = LINE.search(raw)
        if not m:
            continue
        cls, method, h = m["cls"], m["method"], m["hash"]
        if not HEX8.match(h):
            skipped.append((cls, method, f"unusable hash {h!r} - resolve by hand: {m['target']}"))
            continue
        tier = tiers.get((cls, method))
        if tier is None:
            skipped.append((cls, method, "not found in Shared/Patches by that class+name"))
            continue
        if tier[0] not in want:
            continue
        planned.append((cls, method, h, tier))

    edited, unchanged = 0, []
    for cls, method, h, tier in planned:
        path = patches / f"{cls}.cs"
        src = path.read_text()
        # Anchor on the signature, then walk back over its attribute block.
        hits = [m for m in re.finditer(
            r"((?:[ \t]*\[[^\]]+\][ \t]*\r?\n)+)([ \t]*)(?:private|public|internal)\s+static\s+"
            r"[\w<>\[\],\s\.\?]+?\s+" + re.escape(method) + r"\s*\(", src)]
        if len(hits) != 1:
            skipped.append((cls, method, f"{len(hits)} signature matches - refusing to guess"))
            continue
        src = ensure_using(src)
        hits = [m for m in re.finditer(
            r"((?:[ \t]*\[[^\]]+\][ \t]*\r?\n)+)([ \t]*)(?:private|public|internal)\s+static\s+"
            r"[\w<>\[\],\s\.\?]+?\s+" + re.escape(method) + r"\s*\(", src)]
        block, indent = hits[0].group(1), hits[0].group(2)
        # Authoritative check: what the FILE says, not what the reporter's declared column
        # said. A stale hash file re-fed here must not look like it changed something.
        present = re.search(r"\[EnsureCode\(\"([^\"]*)\"\)\]", block)
        if present and present.group(1) == h:
            unchanged.append((cls, method))
            continue
        if present and not args.replace:
            skipped.append((cls, method,
                            f"file already declares {present.group(1)!r}, log says {h!r} "
                            f"- a game update likely moved it; use --replace deliberately"))
            continue
        new_block = re.sub(r"[ \t]*\[EnsureCode\([^\]]*\)\][ \t]*\r?\n", "", block)
        new_block = new_block.rstrip("\r\n") + f'\n{indent}[EnsureCode("{h}")]\n'
        src = src[:hits[0].start(1)] + new_block + src[hits[0].end(1):]
        print(f"  {tier:26s} {cls}.{method} <- {h}")
        if args.apply:
            path.write_text(src)
        edited += 1

    print(f"\n{'APPLIED' if args.apply else 'DRY RUN'}: {edited} attribute(s) changed "
          f"on tier(s) {sorted(want)}"
          + (f", {len(unchanged)} already correct" if unchanged else ""))
    if skipped:
        print(f"\nSKIPPED {len(skipped)} - each needs a human, none were silently dropped:")
        for cls, method, why in skipped:
            print(f"  {cls}.{method}: {why}")
    if not args.apply and edited:
        print("\nNothing was written. Re-run with --apply, then BUILD before deploying, and "
              "deploy this\nchange on its own - a wrong hash stops every node from starting.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
