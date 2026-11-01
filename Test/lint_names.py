"""Undefined-name gate for the scenario modules.

A NameError in a scenario is not found until the scenario RUNS, and a scenario runs behind a
cluster start: on 2026-09-20 a helper renamed in one place and left behind in three cost two bench
arms, twelve minutes of a contended host, and a red that said nothing about the product. Nothing in
the suite reads the modules statically before spending that, so this does.

It is deliberately dumber than a real linter and has no dependencies: it collects every name any
scope binds (imports, assignments, parameters, defs, comprehension targets, except-as, globals)
plus builtins, and reports Load-context names that nothing binds anywhere in the file. That
over-approximates what is in scope, so it CANNOT flag a name that is merely used before assignment
in another branch - the false-positive rate is what makes it usable as a gate, and the failure it
does catch is exactly the rename-and-forget one.

    uv run python lint_names.py            # every scenarios*.py, exit 1 if any name is unbound
"""

from __future__ import annotations

import ast
import builtins
import pathlib
import sys

#: Module-level names Python provides that no AST node binds.
MODULE_GLOBALS = {"__file__", "__name__", "__doc__", "__package__", "__spec__", "__loader__", "__builtins__"}


def unbound_names(path: pathlib.Path) -> list[str]:
    tree = ast.parse(path.read_text(), str(path))
    bound = set(dir(builtins)) | MODULE_GLOBALS
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            bound.add(node.name)
        elif isinstance(node, ast.Name) and isinstance(node.ctx, ast.Store):
            bound.add(node.id)
        elif isinstance(node, ast.arg):
            bound.add(node.arg)
        elif isinstance(node, ast.alias):
            bound.add((node.asname or node.name).split(".")[0])
        elif isinstance(node, (ast.Global, ast.Nonlocal)):
            bound.update(node.names)
        elif isinstance(node, ast.ExceptHandler) and node.name:
            bound.add(node.name)
    used = {node.id for node in ast.walk(tree) if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Load)}
    return sorted(used - bound)


def main(argv: list[str]) -> int:
    paths = [pathlib.Path(arg) for arg in argv[1:]] or sorted(pathlib.Path(__file__).parent.glob("scenarios*.py"))
    problems = 0
    for path in paths:
        missing = unbound_names(path)
        if missing:
            problems += 1
            print(f"{path}: undefined name(s): {', '.join(missing)}")
    print(f"{len(paths)} file(s) checked, {problems} with undefined names")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
