#!/usr/bin/env python3
"""Interactive lab for the movement probe: start a cluster + client, then measure input paths.

Not part of the suite. Kept because the Phase 3 retrospective's first rule is that a probe with no
positive control is not a measurement - this is where a probe earns its positive control.

The client half is what makes a stuck character hard to read: per Remote.md, a client that missed its
control assignment keeps its camera and its ability to look around and silently stops steering, and a
character with no `localPhysics` answers `V` but ignores `W`, `J` and `C` while the server's copy
looks healthy. So every attempt below prints `get_view()` alongside the server-measured distance.
"""
from __future__ import annotations

import json
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from bench import HOST, Bench  # noqa: E402

CLIENT_ID = 1234567890
VIEW_KEYS = ("controlledEntityId", "localCharacterId", "localPhysics", "localPhysicsEnabled",
             "localCharacterProxy", "localPhysicsStatic", "localSpeed", "firstPerson", "spectator")


def main() -> int:
    bench = Bench("PROBE-LAB", "movement probe lab", "-", HOST.results / "lab")
    bench.start_cluster()
    api = bench.start_client(CLIENT_ID)
    character = bench.wait_spawned(CLIENT_ID)
    print("character:", json.dumps(character, indent=2))
    entity = bench._server_character(CLIENT_ID)
    print("server character entity:", entity)

    def view():
        data = api.get_view()
        return {key: data.get(key) for key in VIEW_KEYS}

    def attempt(name, setup=None, keys=None, mode="override", seconds=4.0):
        if setup:
            setup()
        bench._wait_at_rest(entity)
        before = bench._server_position(entity)
        if keys:
            api.set_input_state(keys=keys, mode=mode)
        time.sleep(seconds)
        after = bench._server_position(entity)
        state = view()
        api.clear_input_state()
        time.sleep(1)
        print(f"{name:26} {math.dist(before, after):8.3f} m  focus={bench.focus(CLIENT_ID):24} "
              f"jetpack={api.get_character()['jetpack']} view={json.dumps(state)}")

    print("screens:", [screen.get("type") for screen in api.list_screens()])
    print("view at spawn:", json.dumps(view(), indent=1))
    attempt("baseline (no input)")
    attempt("W override", keys=["W"])
    attempt("Space override", keys=["Space"])
    attempt("J then W", setup=lambda: (api.key("J"), time.sleep(1)), keys=["W"])
    bench.dismiss_blocking_screens(CLIENT_ID)
    print("screens after dismiss:", [screen.get("type") for screen in api.list_screens()])
    attempt("W after dismiss", keys=["W"])
    attempt("J then W after dismiss", setup=lambda: (api.key("J"), time.sleep(1)), keys=["W"])
    print(f"\nleaving the cluster up; `{HOST.package}/cli/cluster --root {bench.run_root} kill` to stop")
    return 0


if __name__ == "__main__":
    sys.exit(main())
