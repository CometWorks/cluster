#!/usr/bin/env python3
"""Rejoin into a seated body while the ship streams in late (#1236).

* ``P1-SEATED-SLOW-JOIN`` - a client that rejoins into the body it left seated in a cockpit gets that body back as
  its local character even when the ship reaches it long after the server answered its respawn request.

The live case (#1191 octahedron, 2026-10-20 20:47 - 2026-10-21 01:30 local): under a CPU-starved polaris every
pilot that rejoined into a seated body and whose client finished loading 35-91 s after RequestRespawn stayed
without a local character for the harness's whole 600 s budget (184 of 232). The server had answered (vanilla
TryFindExistingCharacter -> the cockpit, MotD on the client); joins that loaded within ~27 s (48) and joins into
a standing body (13) all worked.

The window is the time between the server's answer and the ship (with its seated child) reaching the client, so
this scenario holds it open instead of hoping for a slow host: the test plugin's ``/stream-test-hold`` keeps the
ship's stream to the rejoining client back from before the launch until ``SEATED_JOIN_HOLD`` seconds after the
client sends its respawn request (RequestRespawn in its game log). Knob: ``SEATED_JOIN_HOLD`` (45; 0 is the
control arm, the same flow with no hold).
"""

from __future__ import annotations

import json
import os
import signal
import threading
import time

from bench import REMOTE_PASSWORD, REMOTE_USER
from se_remote import RemoteAPI
from scenarios_core import CLIENT_ID, _enter_nearest_cockpit, _join, _nodes
from scenarios_offline import (_assert_bench_endpoint, _character, _listener_pid, _owner_of, _put_config,
                               _verified_client_pids)
from scenarios_wa_ops import _poll, _require, _session, _try

HOLD_SECONDS = float(os.environ.get("SEATED_JOIN_HOLD", "0"))
#: Fraction of every second the rejoining client's process group is SIGSTOPped (0 = off): a starved client host.
THROTTLE = float(os.environ.get("SEATED_JOIN_THROTTLE", "0"))
#: The same for the serving node's dedicated server process: a starved server (live: sim 0.1-0.4).
NODE_THROTTLE = float(os.environ.get("SEATED_JOIN_NODE_THROTTLE", "0"))
#: How long after the respawn request the throttles keep going.
THROTTLE_AFTER = float(os.environ.get("SEATED_JOIN_THROTTLE_AFTER", "400"))
#: How long after the respawn request the client may take to show its seated local character.
POLL_SECONDS = float(os.environ.get("SEATED_JOIN_POLL", "180"))
REJOIN = f"{CLIENT_ID}-rejoin"
#: The client's respawn request; the server answers it within a second or two (TryFindExistingCharacter).
ANSWER = "->  RequestRespawn"


def _answered(client) -> bool:
    # A label's data directory survives between runs: only this launch's game logs count.
    for log in client.game_logs():
        if log.stat().st_mtime < client.launched_at:
            continue
        try:
            if ANSWER in log.read_text(errors="replace"):
                return True
        except OSError:
            pass
    return False


def p1_seated_slow_join(bench) -> None:
    """A rejoin into a seated body gets that body as its local character although the ship streams in late."""
    api = _join(bench)
    workers = [node for node in _nodes(bench) if node != "wa-1"]
    # The ship must stay online and on one node while the killed client's session drains (minutes).
    for node in workers:
        bench.record(f"offline-off-{node}", _put_config(bench, node, {"offline.enabled": "false"}))
    bench.cluster.post("/automatic/pause")
    cockpit = _enter_nearest_cockpit(bench, api)
    grid = cockpit["gridId"]
    seated = _poll(lambda: (lambda c: c if c and c.get("state") == "sitting" else None)(_character(api)), 60, 2)
    _require(bench, "SETUP: the client sits in the cockpit", seated is not None, json.dumps(_character(api))[:300])

    # Leave seated: a killed client leaves its body in the seat (the live pilots were killed by the harness).
    bench.kill_client(CLIENT_ID)
    gone = _poll(lambda: True if _session(bench) is None else None, 360, 5)
    _require(bench, "SETUP: the killed client's Gateway session cleared", gone is not None)
    owner = _poll(lambda: _owner_of(bench, grid), 30, 2)
    _require(bench, "SETUP: the seated ship has exactly one owning node", owner is not None, str(owner))
    node, partition = owner
    bench.record("seated", {"grid": grid, "node": node, "partition": partition, "hold": HOLD_SECONDS})

    if HOLD_SECONDS > 0:
        bench.record("hold-arm", bench.cluster.node(node, f"/stream-test-hold?client={CLIENT_ID}&entity={grid}",
                                                    method="POST"))
    client = bench.launch_client(CLIENT_ID, label=REJOIN)
    timeline = []
    released = None
    throttle_until = [time.time() + 900]
    stop_throttle = threading.Event()

    def throttle() -> None:
        # Pauses stay far below DirectTransport's 30 s disconnect timeout: one cycle is one second.
        while not stop_throttle.is_set() and time.time() < throttle_until[0]:
            try:
                pids = _verified_client_pids(bench, client)
            except Exception:  # noqa: BLE001 - the group is still forking; try the next cycle
                time.sleep(1)
                continue
            for pid in pids:
                _try(os.kill, pid, signal.SIGSTOP)
            time.sleep(THROTTLE)
            for pid in pids:
                _try(os.kill, pid, signal.SIGCONT)
            time.sleep(1 - THROTTLE)
        pids = _try(_verified_client_pids, bench, client)
        for pid in pids if isinstance(pids, list) else []:
            _try(os.kill, pid, signal.SIGCONT)

    def node_pids() -> list[int]:
        endpoint = _nodes(bench)[node].control
        _assert_bench_endpoint(bench, endpoint)   # a bench node under this run's root, never a live one
        return [_listener_pid(int(endpoint.rsplit(":", 1)[1]))]

    def throttle_node() -> None:
        pids = node_pids()
        bench.record("node-throttle-pids", {"node": node, "pids": pids})
        try:
            while not stop_throttle.is_set() and time.time() < throttle_until[0]:
                for pid in pids:
                    os.kill(pid, signal.SIGSTOP)
                time.sleep(NODE_THROTTLE)
                for pid in pids:
                    os.kill(pid, signal.SIGCONT)
                time.sleep(1 - NODE_THROTTLE)
        finally:
            for pid in pids:
                _try(os.kill, pid, signal.SIGCONT)

    throttler = threading.Thread(target=throttle, daemon=True) if THROTTLE > 0 else None
    if throttler:
        throttler.start()
    node_throttler = threading.Thread(target=throttle_node, daemon=True) if NODE_THROTTLE > 0 else None
    if node_throttler:
        node_throttler.start()
    try:
        answered = _poll(lambda: True if _answered(client) else None, 420, 1)
        answered_at = time.time()
        _require(bench, "PRECONDITION: the rejoining client sent its respawn request",
                 answered is not None)
        throttle_until[0] = answered_at + THROTTLE_AFTER
        if HOLD_SECONDS > 0:
            while time.time() - answered_at < HOLD_SECONDS:
                time.sleep(1)
            view = _try(lambda: bench.cluster.node(node, f"/stream-test-hold?client={CLIENT_ID}&entity={grid}"))
            bench.record("held", view)
            released = bench.cluster.node(node, "/stream-test-release", method="POST")
            bench.record("hold-release", released)
            _require(bench, "PRECONDITION: the hold kept the ship's stream back (heldSends > 0)",
                     (released or {}).get("heldSends", 0) > 0, json.dumps(released))
        # Polled while the throttles still run: the live hosts stayed starved for the harness's whole budget.
        client.wait_api_listening(240)
        rejoined = RemoteAPI(client.url, username=REMOTE_USER, password=REMOTE_PASSWORD, timeout=180)
        rejoined.wait_for_api(max_wait=240)
        state = None
        deadline = answered_at + POLL_SECONDS
        while time.time() < deadline:
            view = _try(rejoined.get_view)
            character = _character(rejoined)
            timeline.append({"t": round(time.time() - answered_at, 1), "view": view,
                             "character": (character or {}).get("state")})
            if character and character.get("state") == "sitting":
                state = character
                break
            time.sleep(3)
    finally:
        stop_throttle.set()
        if HOLD_SECONDS > 0 and released is None:
            _try(lambda: bench.cluster.node(node, "/stream-test-release", method="POST"))
    loading = [line for log in client.game_logs() if log.stat().st_mtime >= client.launched_at
               for line in log.read_text(errors="replace").splitlines() if "Loading duration" in line or ANSWER in line]
    bench.record("throttle", {"fraction": THROTTLE, "nodeFraction": NODE_THROTTLE, "afterRequest": THROTTLE_AFTER,
                              "loading": loading})

    bench.record("rejoin-timeline", timeline)
    bench.record("rejoined-character", state or _character(rejoined))
    bench.check(f"the rejoined client is back in its seat with a local character (ship held {HOLD_SECONDS:g} s, "
                f"client throttled {THROTTLE:g}, node {NODE_THROTTLE:g}, until {THROTTLE_AFTER:g} s after its request)", state is not None, json.dumps(timeline[-1:])[:400])


SCENARIOS_REJOIN = {
    "P1-SEATED-SLOW-JOIN": (p1_seated_slow_join, "a rejoin into a seated body survives a late ship or a starved client (#1236)", "3"),
}
