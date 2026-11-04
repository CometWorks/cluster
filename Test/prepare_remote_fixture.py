#!/usr/bin/env python3
"""Prepare an isolated Remote source fixture with vanilla terminal pin/close bookkeeping.

Usage: python prepare_remote_fixture.py REMOTE_CHECKOUT TASK_DESTINATION
Point the task's Pulsar LocalPluginSources Remote folder at TASK_DESTINATION. The shared
Remote checkout is read-only. This fixes the test driver's raw RequestReplicable lifecycle;
the cluster still receives real authenticated client add/remove packets and the real UI opens.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path


def prepare(source: Path, target: Path) -> None:
    if (source.resolve() == target.resolve() or target.resolve() in source.resolve().parents
            or source.resolve() in target.resolve().parents):
        raise ValueError("the fixture must be isolated from the source checkout")
    if target.exists():
        raise ValueError("use a new task-owned destination; refusing to overwrite an existing tree")
    original = source / "ClientPlugin/Shared/Services/CharacterService.cs"
    text = original.read_text()
    anchor = "Sandbox.Game.Gui.MyGuiScreenTerminal.Show(MyTerminalPageEnum.ControlPanel, character, antenna, isRemote: true);"
    if text.count(anchor) != 1:
        raise ValueError("Remote terminal helper changed; review its pin lifecycle before preparing a fixture")
    target.mkdir(parents=True)
    shutil.copy2(source / "Remote.xml", target / "Remote.xml")
    shutil.copytree(source / "ClientPlugin", target / "ClientPlugin", ignore=shutil.ignore_patterns("bin", "obj"))
    patched = text.replace(anchor, anchor + "\n                // Record the raw helper request in vanilla's UI close ledger.\n"
                           "                Sandbox.Game.Gui.MyGuiScreenTerminal.RequestReplicable(gridId, gridId, _ => { });")
    (target / original.relative_to(source)).write_text(patched)
    commit = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    (target / "fixture.json").write_text(json.dumps({"sourceCommit": commit,
        "sourceCharacterServiceSha256": hashlib.sha256(text.encode()).hexdigest(),
        "fixtureCharacterServiceSha256": hashlib.sha256(patched.encode()).hexdigest(),
        "change": "register remote terminal demand with vanilla UI request/close tracking"}, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    prepare(args.source, args.destination)
