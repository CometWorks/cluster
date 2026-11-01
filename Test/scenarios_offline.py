#!/usr/bin/env python3
"""Offline-partition scenarios (T-0193 economy).

* ``P1-OFF-SEATED-JOIN`` - T-0233: a partition whose seat holds the body of a client that is attached
  to its node but has no MyPlayer yet must not be taken offline.

The live case (w32 2026-09-24 06:57): endurance carry-overs rejoin INTO a seated body. Their ships sat
alone in 1-member partitions loaded at boot, and boot + offline.minOnlineSeconds + graceSeconds landed
inside three joins: node-2 froze each ship 1-8 s after the Gateway routed the client to it, because
every "someone is here" test (the client route, the player anchors) reads the vanilla Controller,
which a mid-join player does not have yet. The client got no body, respawned a second one, the woken
seat's copy was reaped as a duplicate, and the client was left controlling a body it no longer had
as its local character.

The join window is a few seconds, so this scenario holds it open instead of racing it: the offline
economy is off while the fixture is built, the rejoining client is SIGSTOPped the moment its node
accepts the attach (before it can ask for its player), and only then do the owner's knobs drop to
minOnline 0 / grace 0. A sweep runs every 5 s, so the second sweep after the switch takes an eligible
partition. The pause stays under the transport's 30 s disconnect timeout, and a detach during it
voids the trial instead of reading as either answer.
"""

from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import time
import urllib.request
from pathlib import Path

from bench import REMOTE_PASSWORD, REMOTE_USER
from cluster import ClusterError
from se_remote import RemoteAPI
from scenarios_core import CLIENT_ID, _enter_nearest_cockpit, _join, _lines, _nodes
from scenarios_wa_ops import _client_identity, _poll, _require, _session

#: Two sweeps (5 s apart) after the switch take an eligible partition; the rest is margin, and the
#: whole pause stays well under DirectTransport's 30 s DisconnectTimeout.
PAUSE_SECONDS = 18
REJOIN = f"{CLIENT_ID}-rejoin"


#: The LIVE gateway's control port. Nothing in this scenario may ever reach it.
LIVE_GATEWAY_PORT = 29416
#: The LIVE nodes' control ports (CM 2026-09-24 08:05:28). Nothing in this scenario may reach them either.
LIVE_NODE_PORTS = range(29417, 29423)


def _environ_value(pid: int, key: str) -> str | None:
    """One key of a process's environment, never the whole block."""
    try:
        for entry in Path(f"/proc/{pid}/environ").read_bytes().split(b"\0"):
            if entry.startswith(key.encode() + b"="):
                return entry.split(b"=", 1)[1].decode(errors="replace")
    except OSError:
        return None
    return None


def _under(path: str | None, root: Path) -> bool:
    return bool(path) and (Path(path).resolve() == root or root in Path(path).resolve().parents)


def _cwd(pid: int) -> str | None:
    try:
        return os.readlink(f"/proc/{pid}/cwd")
    except OSError:
        return None


def _listener_pid(port: int) -> int | None:
    out = subprocess.run(["ss", "-ltnpH", f"sport = :{port}"], capture_output=True, text=True).stdout
    match = re.search(r"pid=(\d+)", out)
    return int(match.group(1)) if match else None


def _assert_bench_endpoint(bench, endpoint: str) -> None:
    """CM condition (1): a config PUT reaches a BENCH process only - not the live gateway's port, and the
    process listening on it runs with CLUSTER_DATA_ROOT under the bench root."""
    port = int(endpoint.rsplit(":", 1)[1])
    pid = _listener_pid(port)
    root = _environ_value(pid, "CLUSTER_DATA_ROOT") if pid else None
    ok = (port != LIVE_GATEWAY_PORT and port not in LIVE_NODE_PORTS and pid is not None
          and _under(root, Path(bench.run_root).resolve()))
    if not ok:
        raise ClusterError(f"refusing a call to {endpoint}: port={port} pid={pid} CLUSTER_DATA_ROOT={root} "
                           f"bench root={bench.run_root}")


def _put_config(bench, node_id: str, values: dict) -> dict | str:
    endpoint = _nodes(bench)[node_id].control
    _assert_bench_endpoint(bench, endpoint)
    url = f"http://{endpoint}/cluster/config"
    print(f"[P1-OFF-SEATED-JOIN] PUT {url} {json.dumps(values)}", flush=True)
    request = urllib.request.Request(url, method="PUT", data=json.dumps(values).encode(),
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        text = response.read().decode(errors="replace")
    try:
        return json.loads(text)
    except ValueError:
        return text


def _verified_client_pids(bench, client) -> list[int]:
    """CM condition (2): the processes of THIS bench client, each proven three ways - its HOME is the
    client's own home under the bench root, the group leader's argv carries the fixture client-id (never
    a live pilot's 76561199500000001-74), and it is in the client's own process group. Never by name.
    Any process of the group that fails a test refuses the whole pause."""
    root = Path(bench.run_root).resolve()
    leader = client.process.pid
    group = os.getpgid(leader)
    argv = Path(f"/proc/{leader}/cmdline").read_bytes().split(b"\0")
    if b"--client-id" not in argv or argv[argv.index(b"--client-id") + 1] != str(CLIENT_ID).encode():
        raise ClusterError(f"refusing to signal: pid {leader} does not carry --client-id {CLIENT_ID}")
    if 76561199500000001 <= CLIENT_ID <= 76561199500000074:
        raise ClusterError(f"refusing to signal: fixture client-id {CLIENT_ID} is a live pilot's")
    pids = []
    for proc in Path("/proc").iterdir():
        if not proc.name.isdigit():
            continue
        pid = int(proc.name)
        try:
            if os.getpgid(pid) != group:
                continue
        except OSError:
            continue
        # No cwd leg: the game chdirs to its install dir (SpaceEngineers/Bin64), so a cwd-under-root test is
        # blind to exactly the processes this pauses (CM 07:42:47, 3rd pair). Group + HOME + client-id suffice.
        home = _environ_value(pid, "HOME")
        if home != str(client.home) or not _under(home, root):
            raise ClusterError(f"refusing to signal: pid {pid} in group {group} has HOME={home} cwd={_cwd(pid)}")
        pids.append(pid)
    if leader not in pids:
        raise ClusterError(f"refusing to signal: the client's own pid {leader} failed verification")
    bench.record("paused-pids", {"pids": pids, "group": group, "clientId": CLIENT_ID, "home": str(client.home)})
    return pids


def _owner_of(bench, entity_id: int) -> tuple[str, int] | None:
    owners = bench.entity_owner(bench.diag(), entity_id)
    return owners[0] if len(owners) == 1 else None


def p1_off_seated_join(bench) -> None:
    """A seated body's partition stays online while its client is attached but has no player yet."""
    control = bench.cluster.config.gateway_control
    _require(bench, "the bench gateway is not the live one", int(control.rsplit(":", 1)[1]) != LIVE_GATEWAY_PORT, control)
    api = _join(bench)
    workers = [node for node in _nodes(bench) if node != "wa-1"]
    for node in workers:
        bench.record(f"offline-off-{node}", _put_config(bench, node, {"offline.enabled": "false"}))
    identity = _client_identity(bench)
    cockpit = _enter_nearest_cockpit(bench, api)
    grid = cockpit["gridId"]
    owner = _owner_of(bench, grid)
    _require(bench, "the cockpit's grid has exactly one owning node", owner is not None, str(owner))
    node, partition = owner
    bench.record("seated", {"identity": identity, "grid": grid, "node": node, "partition": partition})

    # The fixture world parks a stray body (MyCharacter, identity 0) in the transport's partition, and a
    # non-grid member blocks offlining for good ("type") - in both arms, whatever the join does (5th pair).
    # The live case is a ship alone in its partition, so make it one: close every non-grid member.
    def members() -> list[dict]:
        rows = bench.cluster.node(node, "/partitions").get("partitions") or []
        return next((row.get("members") or [] for row in rows if row.get("id") == partition), [])
    strays = [m for m in members() if m.get("type") != "MyCubeGrid"]
    for stray in strays:
        endpoint = _nodes(bench)[node].control
        _assert_bench_endpoint(bench, endpoint)
        print(f"[P1-OFF-SEATED-JOIN] POST http://{endpoint}/cluster/test-close-entity?entity={stray['id']}", flush=True)
        bench.cluster.node(node, f"/test-close-entity?entity={stray['id']}", "POST")
    alone = _poll(lambda: (lambda ms: ms if ms and all(m.get("type") == "MyCubeGrid" for m in ms) else None)(members()), 30, 1)
    bench.record("fixture-members", {"closed": strays, "now": alone})
    _require(bench, "the seated ship's partition holds grids only (the live shape)", alone is not None,
             json.dumps(members())[:300])

    # Control: the instrument must SEE a sweep. Seated and connected, the client is routed to the ship's
    # partition, so a live sweep reports 'client' (or 'player') within two sweep intervals. A stale
    # blocker map here means sweeps do not run after a re-enable at all, and the trial below cannot tell.
    _put_config(bench, node, {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
    control = []
    try:
        deadline = time.time() + 12
        while time.time() < deadline:
            control.append(bench.cluster.node(node, "/offline").get("owner"))
            time.sleep(1)
    finally:
        _put_config(bench, node, {"offline.enabled": "false"})
    bench.record("control-sweeps", control)
    bench.check("control: a sweep ran while the seated client was connected (the instrument sees sweeps)",
                any({"client", "player"} & set(((row or {}).get("blockers") or {})) for row in control),
                json.dumps([(row or {}).get("blockers") for row in control[-3:]]))

    # Leave seated: the body stays in the cockpit, as a carry-over's does across a harness restart.
    bench.kill_client(CLIENT_ID)
    # A killed client cannot say goodbye and the Gateway logs no reliable disconnect line: wait on the
    # QUANTITY, the session leaving /transport/status (the front's activity timeout, 4-5 min live). Until
    # it does, the old MyPlayer still anchors the ship and no arm could show anything.
    gone = _poll(lambda: True if _session(bench) is None else None, 360, 5)
    _require(bench, "the killed client's Gateway session cleared", gone is not None)
    seated = _poll(lambda: _owner_of(bench, grid), 30, 2)
    _require(bench, "the seated ship stays online while offlining is off", seated is not None, str(seated))
    node, partition = seated
    accepted_before = len(_lines(bench, node, rf"Accepted client {CLIENT_ID} through cluster node link"))
    requests_before = len(_lines(bench, node, rf"player_request accepted=true client={CLIENT_ID}\b"))

    client = bench.launch_client(CLIENT_ID, label=REJOIN)
    attached = _poll(lambda: len(_lines(bench, node, rf"Accepted client {CLIENT_ID} through cluster node link"))
                     > accepted_before or None, 300, 0.2)
    _require(bench, f"{node} accepted the rejoining client's attach", attached is not None)
    pids = _verified_client_pids(bench, client)
    for pid in pids:
        os.kill(pid, signal.SIGSTOP)
    paused_at = time.time()
    try:
        early = len(_lines(bench, node, rf"player_request accepted=true client={CLIENT_ID}\b")) > requests_before
        # The precondition this scenario exists for; without it a green proves nothing.
        _require(bench, "the client is paused attached but BEFORE its player request", not early)
        bench.record("offline-on", _put_config(bench, node, {
            "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
        blockers_seen: dict[str, int] = {}
        trace = []
        offlined = []
        missing_session = 0
        while time.time() - paused_at < PAUSE_SECONDS:
            missing_session += _session(bench) is None
            status = bench.cluster.node(node, "/offline")
            trace.append({"t": round(time.time() - paused_at, 1), **(status.get("owner") or {})})
            for key, count in ((status.get("owner") or {}).get("blockers") or {}).items():
                blockers_seen[key] = max(blockers_seen.get(key, 0), count)
            offlined = _lines(bench, node, rf"offlining partition {partition} ")
            if offlined:
                break
            time.sleep(0.5)
    finally:
        for pid in pids:
            os.kill(pid, signal.SIGCONT)
        for worker in workers:
            _put_config(bench, worker, {"offline.enabled": "false"})
    bench.record("pause-trace", trace)
    bench.record("pause", {"seconds": round(time.time() - paused_at, 1), "offlined": offlined,
                           "blockersSeen": blockers_seen, "pollsWithoutSession": missing_session})
    _require(bench, "the client stayed attached through the pause (a detach voids the trial)",
             missing_session == 0, f"{missing_session} polls without a Gateway session")
    bench.check("the seated ship's partition was NOT taken offline while its client was mid-join",
                not offlined, offlined[0][:200] if offlined else "")
    bench.check("the offline sweep named the mid-join seat as its blocker ('joining')",
                blockers_seen.get("joining", 0) > 0, json.dumps(blockers_seen))

    # Launched above without waiting; attach the Remote API the way start_client does.
    client.wait_api_listening(240)
    api = RemoteAPI(client.url, username=REMOTE_USER, password=REMOTE_PASSWORD, timeout=180)
    api.wait_for_api(max_wait=240)
    state = _poll(lambda: (lambda c: c if c and c.get("state") == "sitting" else None)(_character(api)), 180, 3)
    bench.record("rejoined-character", state or _character(api))
    bench.check("the rejoined client is back in its seat with a local character", state is not None,
                json.dumps(_character(api))[:300])


def _character(api) -> dict | None:
    try:
        return api.get_character()
    except Exception:  # noqa: BLE001 - NO_CHARACTER is a 503, and exactly the answer being looked for
        return None


SCENARIOS_OFFLINE = {
    "P1-OFF-SEATED-JOIN": (p1_off_seated_join, "a mid-join client's seated partition stays online (T-0233)", "3"),
}
