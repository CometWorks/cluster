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
import math
from concurrent.futures import ThreadPoolExecutor
import os
import re
import signal
import shutil
import subprocess
import time
import urllib.request
from urllib.parse import quote
import uuid
from pathlib import Path

from bench import REMOTE_PASSWORD, REMOTE_USER
from cluster import ClusterError, ClusterHttpError
from hostenv import HOST
from se_remote import RemoteAPI
from scenarios_core import CLIENT_ID, _enter_nearest_cockpit, _join, _lines, _nodes
from scenarios_wa_ops import _authority, _client_identity, _poll, _require, _session, _wa_failover

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
    # PUT replaces the entire override document. Preserve previous fixture settings when
    # changing one knob (e.g. Block -> Freeze must retain zero grace and quiet distance).
    with urllib.request.urlopen(url, timeout=30) as response:
        merged = json.load(response)["values"]
    merged.update(values)
    print(f"[offline fixture] PUT {url} changes={json.dumps(values)}", flush=True)
    request = urllib.request.Request(url, method="PUT", data=json.dumps(merged).encode(),
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


# SE-346: these use the existing real-client RC/terminal fixture and the production sweep/load.
# They deliberately do not label the unavailable multi-client/planet differential fixtures as passing.
def _survival_template(bench):
    source = Path(bench.cluster.config.world_template or HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / (bench.run_root.name + "-templates") / "survival"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        text, count = re.subn(r"<GameMode>Creative</GameMode>", "<GameMode>Survival</GameMode>", path.read_text())
        if not count and "<GameMode>Survival</GameMode>" not in text:
            raise ClusterError(f"{path}: no explicit game mode")
        path.write_text(text)
    bench.cluster.config.world_template = target


def _call_fixture(bench, *, pb=False, dynamic=False, production=False, unknown=False, hostile=False, observed=False, rotor=False, zone=False, zone_seconds=3600, api=None, client_id=CLIENT_ID):
    if api is None:
        _survival_template(bench)
        api = _join(bench, client_id=client_id)
    # Offline admission requires fresh geometry reports from the normal automatic loop.
    # Pausing it before the first offline row starves those reports and refuses every offer.
    for node in _nodes(bench):
        if node != "wa-1":
            _put_config(bench, node, {"offline.enabled": "false"})
    identity = _client_identity(bench, client_id)
    fixture = bench.cluster.node("node-1", f"/rc-test-ensure?owner={identity}&dynamic={str(dynamic).lower()}", "POST")
    bench.record("call-up-fixture", fixture)
    grid, block, relay = fixture["grid"], fixture["block"], fixture["relay"]
    bench.exercised(relay=relay)
    if production:
        bench.record("production-fixture", bench.cluster.node("node-1", f"/offline-test-production?grid={grid}", "POST"))
    if zone:
        bench.record("zone-fixture", bench.cluster.node("node-1", f"/offline-test-zone?grid={grid}&seconds={zone_seconds}", "POST"))
    if rotor:
        sibling = bench.cluster.node("node-1", f"/offline-test-sibling?grid={grid}&owner={identity}", "POST")["grid"]
        bench.cluster.node("node-1", f"/offline-test-production?grid={sibling}", "POST")
        bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=rotor", "POST")
    if unknown or hostile or observed:
        mode = "unknown" if unknown else "hostile" if hostile else "observed"
        bench.record(mode + "-fixture", bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode={mode}", "POST"))
    if observed:
        _put_config(bench, "node-1", {"offline.observeSeconds": "8", "offline.longObservation": "Skip", "offline.unknownBlocks": "Mean"})
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 90)
    _require(bench, "remote fixture has an owned partition", bool(partition), json.dumps(fixture))
    # Suit broadcasting is an explicit player action in vanilla.
    for _ in range(8):
        characters = bench.cluster.node("node-1", "/characters").get("characters") or []
        if any(r.get("radioOn") for r in characters if identity in (r.get("controllingIdentity"), r.get("identityOwner"))):
            break
        api.key("O")
        time.sleep(2)
    addresses = None
    if pb:
        addresses = [bench.cluster.node("node-1", f"/igc-test-add-pb?grid={g}", "POST")["address"] for g in (relay, grid)]
    for node in _nodes(bench):
        _put_config(bench, node, {"offline.wakeMeters": "0", "offline.leadSeconds": "0", "offline.quietMeters": "0"})
    _put_config(bench, "node-1", {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0", "offline.settleFrames": "0"})
    if rotor:
        def quiet():
            status = bench.cluster.node("node-1", "/offline")["owner"]["compensation"]
            return next((p for p in status["profiling"] if sibling in p["members"] and p["state"] == "complete"), None)
        _require(bench, "quiet sibling profiles while the rotor group moves", bool(_poll(quiet, 90))
                 and _owner_of(bench, sibling) == ("node-1", partition) and _offline_row(bench, partition) is None)
        before = bench.cluster.node("node-1", "/offline-test-measurements")
        bench.record("rotor-quiet-profile", quiet())
        bench.record("rotor-quiet-measurements", before)
        bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=stop-rotor", "POST")
    if unknown:
        _put_config(bench, "node-1", {"offline.unknownBlocks": "Block"})
        def blocked():
            status = bench.cluster.node("node-1", "/offline").get("owner", {}).get("compensation", {})
            return next((p for p in status.get("profiling", []) if grid in p.get("members", []) and p.get("state") == "unknown-blocked"), None)
        bench.record("unknown-blocked", _poll(blocked, 30))
        _require(bench, "Block policy keeps the real unknown group online", blocked() is not None and _offline_row(bench, partition) is None)
        _put_config(bench, "node-1", {"offline.unknownBlocks": "Freeze"})
    row = _poll(lambda: _offline_row(bench, partition), 120)
    _require(bench, "station reached the durable offline table", bool(row), str(partition))
    bench.record("call-up-before", row)
    if rotor:
        after = bench.cluster.node("node-1", "/offline-test-measurements")
        bench.record("rotor-stopped-measurements", after)
        bench.check("quiet sibling reuses its production rate after rotor stop", after["attempts"] == before["attempts"])
    # Leave enough authority frames for a non-zero compensation interval, independent of load latency.
    time.sleep(4)
    return api, grid, block, partition, addresses


def _offline_row(bench, partition):
    try:
        return bench.cluster.get(f"/registry/partitions/offline?partition={partition}")
    except ClusterHttpError as error:
        if error.status == 404:
            return None
        raise


def _remote_presence(bench, partition):
    report = bench.cluster.node("node-1", "/partitions")
    return next((p.get("remoteUsers") for p in report.get("partitions", []) if p.get("id") == partition), [])


def _open_terminal(api, grid, timeout=120):
    # Remote returns as soon as the grid exists. Vanilla's client antenna graph can
    # need another Update100 before CanConnect succeeds; existence is not UI readiness.
    deadline = time.monotonic() + timeout
    last = {}
    while time.monotonic() < deadline:
        last = api.open_remote_terminal(grid, timeout=min(10, max(1, deadline - time.monotonic())))
        if last.get("screenOpen"):
            return last
        time.sleep(2)
    return last


def p2_off_call_terminal(bench):
    api, grid, block, partition, _ = _call_fixture(bench, production=True)
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    bench.record("production-offline-profiles", profiles)
    _require(bench, "real refinery profile was captured in the Final", any(p.get("Production") for p in profiles))
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    started = time.monotonic()
    opened = _open_terminal(api, grid)
    latency = time.monotonic() - started
    bench.record("call-up-latency", {"seconds": latency, "grid": grid, "kind": "small refinery station", "exclusiveBenchmark": False})
    bench.record("call-up-terminal", opened)
    _require(bench, "real client opened the woken remote terminal", opened.get("screenOpen") is True and opened.get("connected") is True, json.dumps(opened))
    bench.screenshot(CLIENT_ID, "02-woken-production-terminal")
    _require(bench, "call-up loads onto the caller's node", _owner_of(bench, grid) == ("node-1", partition))
    _require(bench, "terminal pin becomes remote presence", bool(_poll(lambda: _remote_presence(bench, partition), 15)))
    # A source-local terminal opened after an earlier gateway report must be checked at capture.
    generation = next(p["Generation"] for p in bench.cluster.get("/registry/partitions") if p["Partition"] == partition)
    refusal = ""
    try:
        bench.cluster.node("node-1", f"/capture?transferId={uuid.uuid4()}&partition={partition}&generation={generation}&clientless=true")
    except Exception as error:
        refusal = str(error)
    bench.check("source capture atomically refuses an active terminal pin", "remote-use" in refusal, refusal[:300])
    # Outlast two sweep passes; the fixture's zero grace would otherwise offline it again.
    time.sleep(12)
    bench.check("open terminal remains online", _offline_row(bench, partition) is None)
    status = bench.cluster.node("node-1", "/offline")
    bench.record("call-up-status", status)
    results = status.get("owner", {}).get("compensation", {}).get("results", [])
    result = next((r for r in results if r.get("Group") == grid), {})
    bench.check("shared wake credits actual refinery output", not result.get("Skipped") and result.get("ItemDelta", {}).get("MyObjectBuilder_Ingot/Iron", 0) > 0, json.dumps(result))
    bench.record("production-applied-builders", bench.cluster.node("node-1", "/offline-test-recovery"))
    api.close_remote_terminal()
    _require(bench, "closing removes remote presence", bool(_poll(lambda: not _remote_presence(bench, partition), 15)))
    _require(bench, "closed remote terminal station can offline again", bool(_poll(lambda: _offline_row(bench, partition), 60)))
    bench.exercised(grid=grid, block=block, partition=partition, latencySeconds=latency,
                    proof="terminal/caller-node/pin/sweep/close/shared-wake; no large-station benchmark")


def p2_off_call_rc(bench):
    api, grid, block, partition, _ = _call_fixture(bench, dynamic=True)
    taken = api.take_remote_control(grid, block, timeout=120)
    bench.record("call-up-control", taken)
    _require(bench, "real client takes the offline remote control", taken.get("controlledByMe") is True, json.dumps(taken))
    _require(bench, "remote control loaded onto caller node", _owner_of(bench, grid) == ("node-1", partition))
    time.sleep(12)
    bench.check("controlled partition stays online", _offline_row(bench, partition) is None)
    status = bench.cluster.node("node-1", f"/rc-test-status?block={block}")
    bench.check("server agrees on the remote controller", status.get("controlled") is True)
    bench.ensure_gameplay_focus(CLIENT_ID)
    _require(bench, "the remote-control fixture is a dynamic drone", status.get("isStatic") is False)
    api.set_input_state(keys=["W"])
    try:
        time.sleep(4)
        driven = bench.cluster.node("node-1", f"/rc-test-status?block={block}")
    finally:
        api.clear_input_state()
    distance = math.dist(status["position"], driven["position"])
    bench.record("drone-driving", {"before": status, "after": driven, "metres": distance})
    bench.check("raw client thrust drives the server-side drone", distance > 2 and driven.get("controlled") is True, f"{distance:.3f} m")
    bench.screenshot(CLIENT_ID, "02-driving-woken-drone")
    bench.cluster.node("node-1", f"/rc-test-release?block={block}", "POST")
    bench.exercised(grid=grid, block=block, partition=partition, drivenMetres=distance)


def p2_off_call_igc(bench):
    api, grid, block, partition, addresses = _call_fixture(bench, pb=True)
    source, target = addresses
    # Let the endpoint deletion propagate, then exercise the real IGC send API through the test plugin.
    time.sleep(5)
    sent = bench.cluster.node("node-1", f"/igc-test-unicast?source={source}&addressee={target}&tag=offline-no-wake&data=probe", "POST")
    bench.record("call-up-igc", sent)
    bench.check("unicast to offline PB returns false", sent.get("sent") is False)
    time.sleep(5)
    row = _offline_row(bench, partition)
    bench.check("IGC leaves target offline without a load demand", bool(row) and not row.get("LoadRequestedNode"), json.dumps(row))
    bench.exercised(grid=grid, block=block, partition=partition, source=source, target=target)


def p2_off_call_conflict(bench):
    from scenarios_contracts import _client_partition, _client_node, _hand_over
    from scenarios_redeliver import CLIENT2_ID, CLIENT2_PORT
    api, grid, _, partition, _ = _call_fixture(bench)
    opened = _open_terminal(api, grid)
    _require(bench, "first real client pins the woken station", opened.get("screenOpen") is True)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false"})
    owner = _client_identity(bench)
    sibling = bench.cluster.node("node-1", f"/offline-test-sibling?grid={grid}&owner={owner}", "POST")["grid"]
    for target in (grid, sibling):
        bench.cluster.node("node-1", f"/offline-test-options?grid={target}&mode=share-all", "POST")
    _require(bench, "two independent grids share the pinned partition", bool(_poll(lambda: _owner_of(bench, sibling) == ("node-1", partition), 60)))
    other = bench.start_client(CLIENT2_ID, remote_port=CLIENT2_PORT, render=False)
    bench.wait_spawned(CLIENT2_ID)
    bench.ensure_gameplay_focus(CLIENT2_ID)
    position = other.get_character()["position"]
    x, y, z = (position[a] for a in ("x", "y", "z")) if isinstance(position, dict) else position
    other.character_teleport(x + 20000, y, z)
    separate = _poll(lambda: (lambda p: p if p and p[1] != _client_partition(bench, CLIENT_ID)[1] else None)(
        _client_partition(bench, CLIENT2_ID)), 120, 2)
    _require(bench, "second live client has its own character partition", bool(separate))
    bench.cluster.post("/automatic/pause")
    _hand_over(bench, separate[1], "node-2", client=CLIENT2_ID)
    _require(bench, "both clients remain attached on different nodes", _client_node(bench, CLIENT_ID) == "node-1" and _client_node(bench, CLIENT2_ID) == "node-2")
    identity = bench.cluster.node("node-2", f"/respawn-test-player?userId={CLIENT2_ID}")["identityId"]
    bench.cluster.node("node-2", f"/rc-test-ensure?owner={identity}", "POST")
    for _ in range(8):
        characters = bench.cluster.node("node-2", "/characters").get("characters") or []
        if any(r.get("radioOn") for r in characters if identity in (r.get("controllingIdentity"), r.get("identityOwner"))):
            break
        other.key("O")
        time.sleep(2)
    attempts = []
    before_remote_refusals = bench.cluster.node("node-2", "/offline")["callUps"]["outcomes"].get("remote-use", 0)
    started = time.monotonic()
    while time.monotonic() - started < 180:
        answer = other.open_remote_terminal(sibling, timeout=4)
        demand = bench.cluster.post(f"/registry/pull-demands?partition={partition}&source=node-1&target=node-2")
        attempts.append({"elapsed": time.monotonic() - started, "client": answer, "gateway": demand,
                         "owner": _owner_of(bench, grid), "presence": _remote_presence(bench, partition)})
        _require(bench, "second caller is refused while the original pin survives", not answer.get("screenOpen")
                 and not demand.get("accepted") and demand.get("reason") == "remote-use"
                 and _owner_of(bench, sibling) == ("node-1", partition) and bool(_remote_presence(bench, partition)), json.dumps(attempts[-1]))
        time.sleep(12)
    bench.record("two-client-conflict-three-minutes", attempts)
    bench.check("second live client emitted authenticated replication demands", any(a["client"].get("replicationRequests", 0) > 0 for a in attempts))
    actual_refusals = bench.cluster.node("node-2", "/offline")["callUps"]
    bench.record("second-client-server-admission", actual_refusals)
    bench.check("actual authenticated client path reports remote-use refusal", actual_refusals["outcomes"].get("remote-use", 0) > before_remote_refusals)
    bench.check("three-minute conflict has no owner ping-pong", all(a["owner"] == ("node-1", partition) for a in attempts))
    bench.screenshot(CLIENT_ID, "first-terminal-still-pinned")
    api.close_remote_terminal()
    bench.exercised(twoLiveClients=True, differentNodes=True, twoGridsOnePartition=True, observedSeconds=time.monotonic() - started)


def offline_remote_capture_race(bench):
    """R7: a real terminal pin starts after the gateway read, before source freeze."""
    api, grid, _, partition, _ = _call_fixture(bench)
    _require(bench, "race target first wakes through actual terminal", _open_terminal(api, grid).get("screenOpen") is True)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false"})
    bench.cluster.post("/automatic/pause")
    api.close_remote_terminal()
    _require(bench, "no source pin remains before the gateway checks", bool(_poll(lambda: not _remote_presence(bench, partition), 30)))
    bench.cluster.node("node-1", f"/offline-test-capture-hold?partition={partition}", "POST")
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            early = []
            held = None
            for _ in range(20):
                request = pool.submit(bench.cluster.post, f"/registry/pull-demands?partition={partition}&source=node-1&target=node-2", timeout=60)
                held = _poll(lambda: (lambda s: s if s["entered"] else {"early": True} if request.done() else None)(
                    bench.cluster.node("node-1", f"/offline-test-capture-hold?grid={grid}")), 15, .1)
                if held and not held.get("early"):
                    break
                # Source-local close precedes its next ownership-fenced heartbeat.
                # Early remote-use refusal is correct; wait for that explicit clear.
                outcome = request.result(timeout=60)
                early.append(outcome)
                bench.record("capture-race-before-heartbeat-clear", early)
                _require(bench, "only the old presence snapshot delays arming the race", outcome.get("reason") == "remote-use", json.dumps(outcome))
                time.sleep(1)
            _require(bench, "gateway passed its reads and reached held source capture", bool(held) and not held["frozen"])
            opened = _open_terminal(api, grid, timeout=10)
            _require(bench, "real terminal acquires its permanent pin inside the capture gap", opened.get("screenOpen") is True
                     and bool(_poll(lambda: _remote_presence(bench, partition), 4, .1)), json.dumps(opened))
            bench.cluster.node("node-1", "/offline-test-capture-hold?partition=0", "POST")
            outcome = request.result(timeout=60)
        state = bench.cluster.node("node-1", f"/offline-test-capture-hold?grid={grid}")
        bench.record("capture-race", {"held": held, "terminal": opened, "outcome": outcome, "state": state,
                                     "owner": _owner_of(bench, grid), "presence": _remote_presence(bench, partition)})
        bench.check("atomic source guard refuses the stale gateway decision without freezing or moving the pin", not outcome.get("accepted")
                    and _owner_of(bench, grid) == ("node-1", partition) and not state["frozen"] and bool(_remote_presence(bench, partition)))
        bench.check("source refusal is actually remote-use", bool(_lines(bench, "node-1", "remote-use")))
        bench.screenshot(CLIENT_ID, "terminal-pin-after-capture-race")
    finally:
        bench.cluster.node("node-1", "/offline-test-capture-hold?partition=0", "POST")
        api.close_remote_terminal()
    bench.exercised(realTerminalAfterGatewayRead=True, sourceAtomicRefusal=True,
                    limitation="deliberate spatial co-location positive control remains separate")


def t_oc_07(bench):
    api, grid, block, partition, _ = _call_fixture(bench, unknown=True)
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    bench.record("unknown-frozen-profiles", profiles)
    profile = next(p for p in profiles if grid in p["Members"])
    _require(bench, "Freeze reports PB/timer/ejection as unknown", profile["Confidence"] == "Frozen" and len(profile["Unknowns"]) >= 3, json.dumps(profile["Unknowns"]))
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    opened = _open_terminal(api, grid)
    _require(bench, "unknown-group wake opens through the real terminal", opened.get("screenOpen") is True, json.dumps(opened))
    applied = bench.cluster.node("node-1", "/offline-test-recovery").get("applied")
    bench.record("unknown-applied", applied)
    _require(bench, "shared recovery observer saw the unknown builders", bool(applied))
    blocks = next(g["blocks"] for g in applied["builders"] if g["grid"] == grid)
    bench.check("PB Storage remains frozen before engine instantiation", any(b.get("storage") == "offline-storage-sentinel" for b in blocks))
    result = next(r for r in applied["compensation"]["results"] if r["Group"] == grid)
    bench.check("actual shared wake retains Frozen confidence", result["Confidence"] == "Frozen", json.dumps(result))
    bench.check("modeled battery compensates beside unknowns", not result.get("Skipped") and result["EnergyDelta"] < 0, json.dumps(result))
    api.close_remote_terminal()


def _applied(bench, node):
    return bench.cluster.node(node, "/offline-test-recovery").get("applied")


def _serving_after_restart(bench, node, previous_pid):
    try:
        state = bench.cluster.node(node, "/node-state", timeout=3)
        return state if state.get("pid") != previous_pid and state.get("state") == "Serving" else None
    except (OSError, ClusterError):
        return None


def _assert_applied_inventory(bench, profiles, applied, grid, label):
    profile = next(p for p in profiles if grid in p["Members"])
    result = next(r for r in applied["compensation"]["results"] if r["Group"] == profile["Group"])
    expected = {}
    for inventory in profile["Inventories"]:
        for item in inventory["Stacks"]:
            expected[item["Item"]] = expected.get(item["Item"], 0) + item["Amount"]
    for item, delta in result["ItemDelta"].items():
        expected[item] = expected.get(item, 0) + delta
    actual = {}
    for g in applied["builders"]:
        if g["grid"] not in profile["Members"]:
            continue
        for block in g["blocks"]:
            for inventory in block["inventories"]:
                for item in inventory["items"]:
                    actual[item["type"]] = actual.get(item["type"], 0) + item["amount"]
    bench.check(label, not result.get("Skipped") and result["FromFrame"] == profile["SimulatedThrough"]
                and all(abs(actual.get(k, 0) - v) < .00001 for k, v in expected.items()), json.dumps({"result": result, "actual": actual}))
    return result


def t_oc_09(bench):
    _, grid, _, partition, _ = _call_fixture(bench, production=True, zone=True, zone_seconds=60)
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    original = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    bench.record("crash-original-profiles", profiles)
    bench.record("crash-original-final", original)
    _put_config(bench, "node-1", {"offline.enabled": "false"})
    profile = next(p for p in profiles if grid in p["Members"])
    zone = next(z for z in profile["Upkeep"] if z["Enabled"] and z["Exists"])
    _require(bench, "immutable Final has paid zone credit and spare chips", zone["RemainingFrames"] > 0
             and sum(s["Amount"] for i in profile["Inventories"] for s in i["Stacks"]
                     if s["Item"] == "MyObjectBuilder_Component/ZoneChip") >= zone["Chips"]["Amount"])
    # Real authority time, not injected model time: cross the captured paid-credit
    # boundary before either node may apply. Repeated attempts must debit the same
    # immutable chip stock, just as they do fuel and production ingredients.
    deadline = time.monotonic() + zone["RemainingFrames"] / 60 + 5
    while time.monotonic() < deadline:
        time.sleep(min(2, deadline - time.monotonic()))
    bench.cluster.node("node-2", f"/offline-test-recovery?grid={grid}&ms=45000", "POST")
    bench.record("crash-wake-request", bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node=node-2&reason=remote"))
    first = _poll(lambda: _applied(bench, "node-2"), 90)
    _require(bench, "node-2 reached shared apply before instantiation/Final", bool(first))
    bench.record("crash-held-builders", first)
    _assert_applied_inventory(bench, profiles, first, grid, "first apply uses exactly the original stocks plus its interval delta")
    budget = next(r for r in first["compensation"]["results"] if r["Group"] == grid)
    _require(bench, "held shared wake debits actual producer fuel for zone and both production blocks", any(
        values.get("MyObjectBuilder_Ingot/Uranium", 0) > 0 for values in budget["ProducerFuelConsumed"].values())
        and len(budget["ProductionUnits"]) == 2 and bool(budget["Zones"]), json.dumps(budget))
    _require(bench, "first held apply consumes a real zone upkeep period", budget["UpkeepConsumed"].get(str(zone["Block"])) == zone["Chips"]["Amount"]
             and budget["ItemDelta"].get("MyObjectBuilder_Component/ZoneChip") == -zone["Chips"]["Amount"], json.dumps(budget["UpkeepConsumed"]))
    latest = bench.cluster.get(f"/registry/saves/newest?partition={partition}")
    _require(bench, "held wake has not advanced the durable Final", latest["SaveId"] == original["SaveId"], json.dumps(latest["SaveId"]))
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    bench.cluster.kill(slot="node-2")
    def retry():
        bench.cluster.post("/registry/leases/expire")
        # An offline row remains demand-driven after a failed claim. A fresh caller must
        # reassert demand; generic recovery must not wake unrelated offline content.
        if _offline_row(bench, partition):
            bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node=node-1&reason=remote")
        return _applied(bench, "node-1")
    second = _poll(retry, 240, 2)
    _require(bench, "another node actually recovered after SIGKILL in the apply window", bool(second))
    bench.record("crash-retry-builders", second)
    result = _assert_applied_inventory(bench, profiles, second, grid, "crash retry recomputes from original stocks without crediting the first attempt")
    _require(bench, "retry credits nonzero production", result["ItemDelta"].get("MyObjectBuilder_Ingot/Iron", 0) > 0)
    _require(bench, "crash retry debits one chip period from original stock without stacking the first debit",
             result["UpkeepConsumed"] == budget["UpkeepConsumed"]
             and result["ItemDelta"].get("MyObjectBuilder_Component/ZoneChip") == budget["ItemDelta"].get("MyObjectBuilder_Component/ZoneChip"))
    final = _poll(lambda: (lambda s: s if s["SaveId"]["Generation"] > original["SaveId"]["Generation"] else None)(bench.cluster.get(f"/registry/saves/newest?partition={partition}")), 90)
    _require(bench, "post-compensation Final is catalogued", bool(final))
    bench.record("crash-compensated-final", final)
    bench.cluster.start_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", timeout=180)
    _put_config(bench, "node-2", {"offline.enabled": "false"})
    replicated = _poll(lambda: (lambda s: s if s["SaveId"]["Generation"] >= final["SaveId"]["Generation"]
                       and len(s.get("Locations", [])) > 1 else None)(bench.cluster.get(f"/registry/saves/newest?partition={partition}")), 120, 2)
    _require(bench, "compensated Final has a recoverable second holder before its owner dies", bool(replicated))
    bench.record("crash-compensated-final-replicated", replicated)
    bench.cluster.node("node-2", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    bench.cluster.kill(slot="node-1")
    def after_final():
        bench.cluster.post("/registry/leases/expire")
        return _applied(bench, "node-2")
    third = _poll(after_final, 240, 2)
    _require(bench, "ordinary Final actually recovered after the second kill", bool(third))
    bench.record("crash-after-final-builders", third)
    bench.check("ordinary Final carries no profile to credit again", not any(r.get("Group") == grid for r in third["compensation"]["results"]))
    original_chips = sum(s["Amount"] for i in profile["Inventories"] for s in i["Stacks"] if s["Item"] == "MyObjectBuilder_Component/ZoneChip")
    recovered_chips = sum(i["amount"] for g in third["builders"] if g["grid"] in profile["Members"]
                          for b in g["blocks"] for inv in b["inventories"] for i in inv["items"]
                          if i["type"] == "MyObjectBuilder_Component/ZoneChip")
    bench.check("post-Final recovery retains the single committed chip debit", recovered_chips == original_chips - zone["Chips"]["Amount"],
                json.dumps({"original": original_chips, "recovered": recovered_chips}))
    bench.exercised(grid=grid, partition=partition, actualNodeKills=2, proof="shared apply/pre-Final crash/retry/catalogued-Final recovery; long differential still separate")


def offline_cache_durability(bench):
    """R8: a WA-local clear acknowledged from the actual journal survives pre-Final WA death.

    This is command durability, not T-OC-12's twenty-production-base cache measurement.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    key = "native-offline-cache-clear"
    bench.cluster.node("wa-1", f"/offline-test-cache?key={key}", "POST")
    control = key + "-untouched-control"
    bench.cluster.node("wa-1", f"/offline-test-cache?key={control}", "POST")
    def cache(node):
        return bench.cluster.node(node, "/offline")["owner"]["compensation"]["cache"]
    _require(bench, "WA cache seeded before the snapshot", any(e["Key"] == key for e in cache("wa-1")["entries"]))
    before = bench.cluster.get("/registry/global-saves/newest")["SaveId"]
    bench.cluster.node("wa-1", "/global-save-capture", "POST")
    saved = _poll(lambda: (lambda s: s if s["SaveId"] != before else None)(bench.cluster.get("/registry/global-saves/newest")), 90)
    _require(bench, "pre-clear cache snapshot is catalogued", bool(saved))
    _require(bench, "pre-clear snapshot has a holder besides the WA", bool(_poll(lambda: len(bench.cluster.get("/registry/global-saves/newest").get("Locations", [])) > 1, 60)))
    bench.cluster.node("wa-1", "/global-save-register-hold?ms=60000", "POST")
    cleared = bench.cluster.node("wa-1", f"/profiles/cache/clear?key={key}", "POST")
    _require(bench, "WA-local clear acknowledged", cleared.get("cleared") is True, json.dumps(cleared))
    journal = bench.cluster.get("/registry/global-journal?after=0")
    bench.record("cache-clear-journal", journal)
    _require(bench, "clear was journaled before success", any(e["Subsystem"] == "offline-rates-clear" and e["Payload"] == key for e in journal["Entries"]))
    _require(bench, "no post-clear Final was catalogued before the kill", bench.cluster.get("/registry/global-saves/newest")["SaveId"] == saved["SaveId"])
    _wa_failover(bench, _authority(bench)["Generation"])
    restored = cache("wa-2")
    bench.record("cache-after-wa-replacement", restored)
    bench.check("replacement restored the pre-clear snapshot's untouched cache entry", any(e["Key"] == control for e in restored["entries"]))
    bench.check("replacement replays the acknowledged clear over its old snapshot", not any(e["Key"] == key for e in restored["entries"]))
    bench.cluster.node("wa-2", f"/offline-test-cache?key={key}&epoch=0", "POST")
    bench.check("replacement rejects stale pre-clear measurement epoch", not any(e["Key"] == key for e in cache("wa-2")["entries"]))
    bench.cluster.node("wa-2", f"/offline-test-cache?key={key}&epoch=1", "POST")
    bench.check("replacement admits a new measurement at the cleared epoch", any(e["Key"] == key and e["Epoch"] == 1 for e in cache("wa-2")["entries"]))
    bench.exercised(waKilledBeforeFinal=True, localClearJournaled=True, proof="native authority command journal/replacement; production cache reuse remains separate")


def _denied_call(bench, api, grid, partition, label):
    before = bench.cluster.node("node-1", "/offline")["callUps"]["outcomes"].get("refused-reach", 0)
    answer = api.open_remote_terminal(grid, timeout=4)
    bench.record(label + "-answer", answer)
    _require(bench, label + ": authenticated client actually sent an absent-grid request", answer.get("replicationRequests", 0) > 0)
    # WA proxy publication and the asynchronous admission task follow the client request.
    # A timed-out UI helper is not itself a server admission outcome.
    _poll(lambda: bench.cluster.node("node-1", "/offline")["callUps"]["outcomes"].get("refused-reach", 0) > before, 60)
    outcomes = bench.cluster.node("node-1", "/offline")["callUps"]["outcomes"]
    row = _offline_row(bench, partition)
    bench.record(label + "-server-admission", {"outcomes": outcomes, "offline": row})
    bench.check(label + ": server admission refuses and leaves the row offline", outcomes.get("refused-reach", 0) > before
                and bool(row) and not row.get("LoadRequestedNode") and not answer.get("screenOpen"), json.dumps({"row": row, "outcomes": outcomes}))


def p2_off_call_deny(bench):
    # An OFFLINE backend reports every nonzero id as Owner through vanilla.
    # Cluster bypasses must follow the actual replicated promotion map instead.
    client_id = CLIENT_ID + 1
    api, grid, _, partition, _ = _call_fixture(bench, hostile=True, client_id=client_id)
    level = bench.cluster.node("node-1", f"/promote-test?userId={client_id}")
    bench.record("negative-client-privilege", level)
    _require(bench, "negative admission client has no authoritative moderator promotion", level["promoted"] == 0)
    _denied_call(bench, api, grid, partition, "hostile-owner")
    hostile_grid, hostile_partition = grid, partition
    _put_config(bench, "node-1", {"offline.enabled": "false"})
    identity = _client_identity(bench, client_id)
    # A fresh target must not overlap the hostile offline row: vanilla spatial co-location
    # would deliberately wake that row, independently of this denied radio demand.
    fresh = bench.cluster.node("node-1", f"/offline-test-rate-fixtures?owner={identity}&count=1", "POST")[0]
    grid = fresh["grid"]
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 90)
    _require(bench, "friendly no-relay fixture has ownership", bool(partition))
    relay = json.loads((bench.dir / "call-up-fixture.json").read_text())["relay"]
    bench.cluster.node("node-1", f"/offline-test-options?grid={relay}&mode=no-relay", "POST")
    _put_config(bench, "node-1", {"offline.enabled": "true"})
    _require(bench, "friendly no-relay target reached offline", bool(_poll(lambda: _offline_row(bench, partition), 90)))
    time.sleep(3)
    _denied_call(bench, api, grid, partition, "no-relay")
    # Positive control: the actual replicated promotion, not OFFLINE's implicit Owner,
    # deliberately grants admission. Later vanilla UI access remains a separate decision.
    bench.cluster.node("node-1", f"/promote-test?userId={client_id}&level=2", "POST")
    try:
        _require(bench, "moderator positive control reaches the authoritative promotion map", bool(_poll(lambda:
            bench.cluster.node("node-1", f"/promote-test?userId={client_id}")["promoted"] == 2, 30)))
        _require(bench, "hostile positive-control target is still offline before the privileged request", bool(_offline_row(bench, hostile_partition)))
        answer = api.open_remote_terminal(hostile_grid, timeout=3)
        loaded = _poll(lambda: _owner_of(bench, hostile_grid) if _offline_row(bench, hostile_partition) is None else None, 90)
        bench.record("moderator-bypass-positive-control", {"answer": answer, "owner": loaded,
            "promotion": bench.cluster.node("node-1", f"/promote-test?userId={client_id}")})
        bench.check("an actually promoted moderator can admit the otherwise hostile unreachable target", bool(loaded)
                    and answer.get("replicationRequests", 0) > 0 and _offline_row(bench, hostile_partition) is None)
    finally:
        bench.cluster.node("node-1", f"/promote-test?userId={client_id}&level=0", "POST")


def p2_off_call_rate(bench):
    api, _, _, _, _ = _call_fixture(bench)
    _put_config(bench, "node-1", {"offline.enabled": "false"})
    identity = _client_identity(bench)
    fixtures = bench.cluster.node("node-1", f"/offline-test-rate-fixtures?owner={identity}", "POST")
    bench.record("rate-twenty-fixtures", fixtures)
    target_ids = {f["grid"] for f in fixtures}
    # All owners may offline: an ordinary balancer move must not invalidate this admission fixture.
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0", "offline.settleFrames": "0"})
    def rows():
        return bench.cluster.get("/registry/partitions/offline").get("Partitions", [])
    offline = _poll(lambda: (lambda rs: rs if target_ids <= {m for r in rs for m in r.get("Members", [])} else None)(rows()), 240, 2)
    _require(bench, "twenty distinct reachable targets are durably offline", bool(offline))
    before = bench.cluster.node("node-1", "/offline")["callUps"]
    started = time.monotonic()
    with ThreadPoolExecutor(max_workers=20) as pool:
        replies = list(pool.map(lambda f: api.open_remote_terminal(f["grid"], timeout=3), fixtures))
    duration = time.monotonic() - started
    after = bench.cluster.node("node-1", "/offline")["callUps"]
    bench.record("rate-client-requests", {"seconds": duration, "replies": replies, "before": before, "after": after})
    _require(bench, "twenty real client requests ran inside one rate window", duration < 60 and all(r.get("replicationRequests", 0) > 0 for r in replies))
    refused = after["outcomes"].get("refused-rate", 0) - before["outcomes"].get("refused-rate", 0)
    bench.check("concurrent call-ups enforce one pending admission", refused >= 19 and after["pending"] <= 1, json.dumps(after))
    _require(bench, "burst admission completes or times out before the successive-window leg", bool(_poll(lambda:
        bench.cluster.node("node-1", "/offline")["callUps"]["pending"] == 0, 150)))
    api.close_remote_terminal()
    # Real wall time, with default six admissions/minute. No counter reset or shortened
    # window: all seven selected targets must still be absent when their requests begin.
    time.sleep(60)
    current = rows()
    remaining = [(f, r["Partition"]) for f in fixtures for r in current if f["grid"] in r.get("Members", [])]
    _require(bench, "seven reachable targets remain offline for the actual minute limit", len(remaining) >= 7)
    completions = []
    window_started = time.monotonic()
    for fixture, partition in remaining[:6]:
        _require(bench, "successive target is absent before its authenticated request", bool(_offline_row(bench, partition)))
        opened = _open_terminal(api, fixture["grid"], timeout=45)
        completions.append({"grid": fixture["grid"], "seconds": time.monotonic() - window_started, "answer": opened})
        bench.record("rate-successive-completions", completions)
        _require(bench, "successive offline admission completes through the real terminal", opened.get("screenOpen") is True, json.dumps(opened))
        _require(bench, "completed admission leaves no pending request", bool(_poll(lambda:
            bench.cluster.node("node-1", "/offline")["callUps"]["pending"] == 0, 10)))
        api.close_remote_terminal()
    _require(bench, "six real completions fit inside one native rate window", time.monotonic() - window_started < 55,
             json.dumps(completions))
    fixture, partition = remaining[6]
    before_rate = bench.cluster.node("node-1", "/offline")["callUps"]["outcomes"].get("refused-rate", 0)
    rejected = api.open_remote_terminal(fixture["grid"], timeout=3)
    refused = _poll(lambda: bench.cluster.node("node-1", "/offline")["callUps"]["outcomes"].get("refused-rate", 0) > before_rate, 5)
    bench.record("rate-seventh-in-window", {"seconds": time.monotonic() - window_started, "answer": rejected,
        "status": bench.cluster.node("node-1", "/offline")["callUps"], "offline": _offline_row(bench, partition)})
    bench.check("seventh authenticated demand is rate-refused without waking", bool(refused) and bool(_offline_row(bench, partition))
                and rejected.get("replicationRequests", 0) > 0 and time.monotonic() - window_started < 60)
    bench.exercised(authenticatedBurstRequests=20, concurrentPendingLimit=True, successiveCompletions=6, seventhRefused=True)


def p2_off_call_laser(bench):
    _join(bench)
    owner = _client_identity(bench)
    for node in _nodes(bench):
        _put_config(bench, node, {"offline.enabled": "false", "offline.wakeMeters": "0", "offline.leadSeconds": "0", "offline.quietMeters": "0"})
    source = bench.cluster.node("node-1", f"/igc-test-ensure-laser-at?owner={owner}&name=offline-laser-source&x=0&y=20000&z=0", "POST")
    target = bench.cluster.node("node-2", f"/igc-test-ensure-laser-at?owner={owner}&name=offline-laser-target&x=20000&y=20000&z=0", "POST")
    source_grid, target_grid = source["grid"], target["grid"]
    # The static source remains online by the normal self-propulsion gate, not an offline bypass.
    bench.cluster.node("node-1", f"/offline-test-options?grid={source_grid}&mode=stay-online", "POST")
    partition = _poll(lambda: (_owner_of(bench, target_grid) or (None, None))[1], 90)
    _require(bench, "laser target has a partition", bool(partition))
    target_state = bench.cluster.node("node-2", f"/igc-test-laser-status?grid={target_grid}")
    bench.record("laser-target-before", target_state)
    def enable(node):
        _put_config(bench, node, {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0", "offline.settleFrames": "0"})
    def connected():
        s = bench.cluster.node("node-1", f"/igc-test-laser-status?grid={source_grid}")
        t = bench.cluster.node("node-1", f"/igc-test-laser-status?grid={target_grid}") if _owner_of(bench, target_grid) == ("node-1", partition) else {}
        return {"source": s, "target": t} if s.get("state") == "connected" and t.get("state") == "connected" else None
    enable("node-2")
    _require(bench, "laser id target is durably offline", bool(_poll(lambda: _offline_row(bench, partition), 90)))
    # Deliberately do not wait for the authority's next antenna publish: the durable
    # offline row can precede it. An accepted explicit intent must survive that race.
    bench.record("laser-graph-at-request", bench.cluster.node("node-1", "/antenna-graph"))
    requested = bench.cluster.node("node-1", f"/igc-test-laser-connect?grid={source_grid}&target={target['laser']}", "POST")
    bench.record("laser-id-request", requested)
    _require(bench, "the real laser accepts the explicit id connection", requested.get("requested") is True)
    paired = _poll(connected, 150, 2)
    bench.record("laser-graph-after-id", bench.cluster.node("node-1", "/antenna-graph"))
    bench.record("laser-source-after-id", bench.cluster.node("node-1", f"/igc-test-laser-status?grid={source_grid}"))
    _require(bench, "id wake loads onto source node and establishes both real lasers", bool(paired) and _owner_of(bench, target_grid) == ("node-1", partition))
    bench.record("laser-id-connected", paired)
    enable("node-1")
    _require(bench, "established far end can offline", bool(_poll(lambda: _offline_row(bench, partition), 90)))
    time.sleep(12)
    s = bench.cluster.node("node-1", f"/igc-test-laser-status?grid={source_grid}")
    row = _offline_row(bench, partition)
    bench.check("established proxy link stays connected without waking", s.get("state") == "connected" and bool(row) and not row.get("LoadRequestedNode"), json.dumps({"source": s, "offline": row}))
    bench.cluster.node("node-1", f"/igc-test-laser-idle?grid={source_grid}", "POST")
    # The per-source minute limit is real wall time; do not bypass it for the GPS arm.
    time.sleep(60)
    x, y, z = target_state["head"]
    bench.record("laser-gps-request", bench.cluster.node("node-1", f"/igc-test-laser-gps?grid={source_grid}&x={x}&y={y}&z={z}", "POST"))
    paired = _poll(connected, 150, 2)
    _require(bench, "GPS wake establishes both real lasers", bool(paired))
    bench.record("laser-gps-connected", paired)
    bench.exercised(idWake=True, gpsWake=True, establishedNoWake=True,
                    limitation="explicit radio-chain relay-behind-offline-laser assertion remains separate")


def t_oc_10(bench):
    """A real locally staged world mod changes on the waking node, with no live world migration."""
    from scenarios_mods import PROBE_NAME, _run as run_mod_world
    _survival_template(bench)
    logs, error = run_mod_world(bench, None)
    _require(bench, "staged mod world starts natively", error is None, str(error))
    # This assertion needs real mod bytes, a captured Final and native recovery,
    # not a player. A fake Workshop id cannot be downloaded by a stock client.
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false", "offline.quietMeters": "0", "offline.wakeMeters": "0",
                                "offline.leadSeconds": "0", "offline.settleFrames": "0"})
    owner = bench.cluster.node("node-1", "/identity-test-create?name=offline-content-owner", "POST")["identityId"]
    grid = bench.cluster.node("node-1", f"/offline-test-station?owner={owner}", "POST")["grid"]
    bench.cluster.node("node-1", f"/offline-test-production?grid={grid}", "POST")
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 90)
    _require(bench, "modded production fixture has an owned partition", bool(partition))
    _put_config(bench, "node-1", {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
    _require(bench, "modded production Final is durably offline", bool(_poll(lambda: _offline_row(bench, partition), 120)))
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    bench.record("changed-mod-original-profiles", profiles)
    _put_config(bench, "node-1", {"offline.enabled": "false"})
    previous_pid = bench.cluster.node("node-2", "/node-state")["pid"]
    bench.cluster.kill(slot="node-2")
    mod = bench.run_root / "node-2-data/Mods" / PROBE_NAME / "Data/Scripts/ClusterLocalModProbe/Probe.cs"
    original = mod.read_text()
    mod.write_text(original + "\n// native offline content-hash change fixture\n")
    bench.record("changed-mod-bytes", {"file": str(mod), "beforeBytes": len(original.encode()), "afterBytes": mod.stat().st_size})
    bench.cluster.start_node("node-2")
    # A just-closed game log still has a recent mtime and an old Game-ready line.
    # Require the replacement process itself to serve before sending test commands.
    state = _poll(lambda: _serving_after_restart(bench, "node-2", previous_pid), 180)
    _require(bench, "changed-mod replacement process is actually serving", bool(state))
    bench.record("changed-mod-replacement", {"previousPid": previous_pid, "state": state})
    bench.cluster.node("node-2", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node=node-2&reason=remote")
    applied = _poll(lambda: _applied(bench, "node-2"), 120)
    _require(bench, "changed-content wake reached actual shared recovery", bool(applied))
    bench.record("changed-mod-applied", applied)
    result = next(r for r in applied["compensation"]["results"] if r["Group"] == grid)
    bench.check("changed mod bytes skip compensation explicitly", "content mismatch" in (result.get("Skipped") or ""), json.dumps(result))
    owner = _poll(lambda: _owner_of(bench, grid), 30)
    bench.check("partition remains healthy after the skip", owner == ("node-2", partition), str(owner))


def t_oc_13(bench):
    api, grid, _, partition, _ = _call_fixture(bench, dynamic=True)
    fixture = json.loads((bench.dir / "call-up-fixture.json").read_text())
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    answer = _open_terminal(api, grid)
    _require(bench, "parked large dynamic grid wakes through real radio", answer.get("screenOpen") is True, json.dumps(answer))
    applied = _applied(bench, "node-1")
    bench.record("parked-dynamic-applied", applied)
    builder = next(g for g in applied["builders"] if g["grid"] == grid)
    bench.check("large dynamic grid survives offline at the same position", builder["isStatic"] is False
                and math.dist(fixture["position"], builder["position"]) < .01, json.dumps(builder["position"]))
    api.close_remote_terminal()


def t_oc_08(bench):
    api, grid, _, partition, _ = _call_fixture(bench, rotor=True)
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    bench.record("rotor-stopped-final-profiles", profiles)
    bench.check("stopped rotor and independent quiet sibling have separate logical profiles", len(profiles) == 2
                and any(len(p["Members"]) == 2 and grid in p["Members"] for p in profiles)
                and any(p["Production"] for p in profiles))
    bench.exercised(nativeRotorSubgrid=True, quietSibling=True, sharedPartition=partition)


def offline_live_differential(bench):
    """Bounded native engine reference, not the six-hour/three-baseline standalone T-OC lane."""
    _survival_template(bench)
    _join(bench)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false", "offline.quietMeters": "0", "offline.settleFrames": "0"})
    owner = _client_identity(bench)
    grid = bench.cluster.node("node-1", f"/rc-test-ensure?owner={owner}", "POST")["grid"]
    bench.cluster.node("node-1", f"/offline-test-production?grid={grid}", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=full-auto-reactor", "POST")
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 60)
    _require(bench, "native reference fixture has an owned partition", bool(partition))
    bench.cluster.post("/automatic/pause")
    _require(bench, "native reference profile completes", bool(_poll(lambda: bench.cluster.node("node-1", f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)))
    initial = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}", "POST")
    bench.record("native-reference-initial", initial)
    time.sleep(30)
    comparison = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}")
    bench.record("native-live-comparison", comparison)
    actual, predicted, result = (comparison[k] for k in ("actual", "predicted", "result"))
    _require(bench, "native 60 Hz engine advanced through the reference interval", result["Frames"] >= 1500)
    for store in actual["Storage"]:
        expected = next(s for s in predicted["Storage"] if s["Block"] == store["Block"])
        bench.check("full Auto battery follows native Update100 charging", abs(store["Amount"] - expected["Amount"]) < .0004,
                    json.dumps({"actual": store, "predicted": expected}))
    def totals(profile):
        sums = {}
        for inventory in profile["Inventories"]:
            for stack in inventory["Stacks"]:
                sums[stack["Item"]] = sums.get(stack["Item"], 0) + stack["Amount"]
        return sums
    before, native, model = map(totals, (initial, actual, predicted))
    iron = "MyObjectBuilder_Ingot/Iron"
    delta = native.get(iron, 0) - before.get(iron, 0)
    _require(bench, "live reference actually produced iron", delta > 0)
    # One ordinary refinery tick bounds the endpoint phase difference; this does not hide a
    # six-hour drift or ore-switch/upgrade/conveyor discrepancy behind a short fixture pass.
    recipe = next(r for p in initial["Production"] for r in p["Recipes"] if r["Id"].endswith("/IronOreToIngot"))
    tick = max(p["TickFrames"] for p in initial["Production"])
    tolerance = recipe["UnitsPerFrame"] * tick * next(m["Amount"] for m in recipe["Outputs"] if m["Item"] == iron)
    bench.check("short native production differential stays within one engine tick", abs(native[iron] - model[iron]) <= tolerance,
                json.dumps({"native": native, "model": model, "oneTickIron": tolerance}))
    bench.exercised(nativeLiveEngineReference=True, frames=result["Frames"], limitation="short clustered-engine reference; six-hour standalone lane, three baselines and full stock-chain differentials remain")


def _inventory_totals(profile):
    totals = {}
    for inventory in profile["Inventories"]:
        for stack in inventory["Stacks"]:
            totals[stack["Item"]] = totals.get(stack["Item"], 0) + stack["Amount"]
    return totals


def _zone_reference(bench, grid, partition, label, seconds=30, blocked=False, depleted=False, renewable=False):
    _require(bench, label + ": native profile completes", bool(_poll(lambda: bench.cluster.node("node-1",
        f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)))
    if depleted:
        # Align one finite, steady-demand interval to an ACTUAL vanilla fuel settlement.
        # A reactor samples output every 900..3600 frames; do not pretend it bills at 100.
        prior = bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")["producers"][0]
        tick_state = _poll(lambda: (lambda s: s if s["producers"][0]["fuel"] < prior["fuel"] else None)(
            bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")), prior["timerFrames"] / 60 + 10, .2)
        _require(bench, label + ": observed a real reactor fuel timer settlement", bool(tick_state))
        producer = tick_state["producers"][0]
        profile = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}", "POST")
        source = next(s for s in profile["Sources"] if s["Block"] == producer["block"])
        rate = next(m["Amount"] for m in source["Fuel"] if m["Item"] == "MyObjectBuilder_Ingot/Uranium")
        quota = max(.000001, math.floor(producer["output"] * producer["timerFrames"] * rate * 1000000) / 1000000 - .000002)
        bench.record(label + "-native-timer", {"state": tick_state, "quota": quota})
        bench.cluster.node("node-1", f"/offline-test-limit-fuel?grid={grid}&fuel={quota:.6f}", "POST")
        seconds = producer["timerFrames"] / 60 + 5
    initial = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}", "POST")
    _require(bench, label + ": actual player zone and both production blocks are captured", len(initial["Upkeep"]) == 1
             and initial["Upkeep"][0]["PowerSink"] >= 0 and len(initial["Production"]) == 2)
    samples = []
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        samples.append(bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}"))
        time.sleep(2)
    compared = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}")
    state = bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")
    bench.record(label + "-reference", {"initial": initial, "comparison": compared, "samples": samples, "zone": state})
    actual, predicted, result = (compared[k] for k in ("actual", "predicted", "result"))
    before, observed, modeled = map(_inventory_totals, (initial, actual, predicted))
    fuel = "MyObjectBuilder_Ingot/Uranium"
    native_burn, model_burn = before.get(fuel, 0) - observed.get(fuel, 0), before.get(fuel, 0) - modeled.get(fuel, 0)
    charged = sum(p.get(fuel, 0) for p in result["ProducerFuelConsumed"].values())
    bench.check(label + ": producer diagnostic equals the single inventory fuel debit", abs(charged - model_burn) < .00001)
    chips = "MyObjectBuilder_Component/ZoneChip"
    bench.check(label + ": native chip stock agrees with the shared upkeep debit", observed.get(chips, 0) == modeled.get(chips, 0)
                and before.get(chips, 0) - modeled.get(chips, 0) == sum(result["UpkeepConsumed"].values()),
                json.dumps({"before": before.get(chips, 0), "native": observed.get(chips, 0), "modeled": modeled.get(chips, 0), "debits": result["UpkeepConsumed"]}))
    # A reactor samples current output on its native timer, so retain a single full-output
    # timer tick as the endpoint envelope, and record raw quantities for longer baselines.
    reactor = next(s for s in initial["Sources"] if s["Kind"] == "Reactor")
    timer_frames = max(s["producers"][0]["timerFrames"] for s in samples)
    allowance = reactor["Maximum"] * timer_frames * next(m["Amount"] for m in reactor["Fuel"] if m["Item"] == fuel)
    bench.check(label + ": native fuel burn matches modeled delivered output within one reactor tick", abs(native_burn - model_burn) <= allowance + .00001,
                json.dumps({"nativeBurn": native_burn, "modeledBurn": model_burn, "oneTick": allowance}))
    supplied = sum(result["ProducerEnergyMWh"].values())
    consumed = sum(result["ConsumerEnergyMWh"].values()) + sum(result["StorageInputEnergyMWh"].values())
    bench.check(label + ": one shared grid budget conserves delivered energy", abs(supplied - consumed) < 1e-8)
    bench.check(label + ": observed reactor output matches all native sink inputs", all(abs(
        sum(p["output"] for p in s["producers"]) - sum(c["input"] for c in s["consumers"])) < .0001 for s in samples)
        if all(not store["Enabled"] for store in initial["Storage"]) else True)
    for production in initial["Production"]:
        benchmark = next((r for r in production["Recipes"] if r["Id"].endswith("/IronOreToIngot") or r["Id"].endswith("/SteelPlate")), None)
        if benchmark is None:
            continue
        for material in benchmark["Inputs"] + benchmark["Outputs"]:
            item = material["Item"]
            tick = (benchmark["UnitsPerFrame"] * production["TickFrames"] + 1) * material["Amount"] + .00001
            for inventory_index in material["Inventories"]:
                captured = initial["Inventories"][inventory_index]
                def amount(profile):
                    inventory = next(i for i in profile["Inventories"] if i["Block"] == captured["Block"] and i["Index"] == captured["Index"])
                    return sum(s["Amount"] for s in inventory["Stacks"] if s["Item"] == item)
                native_amount, model_amount = amount(actual), amount(predicted)
                bench.check(label + ": native input/output inventory stays within one production tick", abs(native_amount - model_amount) <= tick,
                            json.dumps({"block": captured["Block"], "item": item, "native": native_amount, "modeled": model_amount, "oneTick": tick}))
        if blocked:
            # ComputeAmountThatFits rounds to MyFixedPoint: a nominally full output can
            # retain sub-unit headroom. Bound that last partial batch by one native tick,
            # and require standby electricity for the remainder of the interval.
            extra = result["ConsumerEnergyMWh"][str(production["Block"])] - production["IdlePower"] * result["Frames"] * .016 / 3600
            tick_energy = (production["OperationalPower"] - production["IdlePower"]) * production["TickFrames"] / 216000
            bench.check(label + ": blocked/empty production settles to standby within one engine tick",
                        result["ProductionUnits"].get(str(production["Block"]), 0) <= benchmark["UnitsPerFrame"] * production["TickFrames"] + .00001
                        and -.00000001 <= extra <= tick_energy + .00000001,
                        json.dumps({"extraMWh": extra, "oneTickMWh": tick_energy, "productionUnits": result["ProductionUnits"].get(str(production["Block"]), 0)}))
    zone = result["Zones"][0]
    for store in actual["Storage"]:
        expected = next(s for s in predicted["Storage"] if s["Block"] == store["Block"])
        outgoing = sum(s["Maximum"] for s in initial["Sources"] if s["Block"] == store["Block"])
        tick = (store["MaxInput"] + outgoing) * 100 / 216000
        bench.check(label + ": native battery energy follows its mode and recharge loss", abs(store["Amount"] - expected["Amount"]) <= tick + .00001,
                    json.dumps({"native": store["Amount"], "modeled": expected["Amount"], "oneTick": tick}))
    bench.check(label + ": actual bubble agrees with modeled active/activation state", state["active"] == (zone["Exists"] and not zone["Activating"]))
    bench.check(label + ": power transitions retain functional block switch and settings", state["blockEnabled"] is True
                and state["radius"] == samples[0]["radius"] and state["allowedActions"] == samples[0]["allowedActions"])
    bench.check(label + ": paid upkeep agrees with the real manager", abs(state["remainingMs"] - zone["RemainingFrames"] * 1000 / 60) < 3000,
                json.dumps({"nativeMs": state["remainingMs"], "modeledMs": zone["RemainingFrames"] * 1000 / 60}))
    if depleted:
        bench.check(label + ": finite fuel leaves no unsupported enabled bubble", observed.get(fuel, 0) <= .00001
                    and modeled.get(fuel, 0) <= .00001 and not state["active"] and not zone["Exists"])
    if renewable:
        solar_ids = {str(s["Block"]) for s in initial["Sources"] if s["Kind"] == "Environmental"}
        bench.check(label + ": real solar and battery backup supply costs after reactor fuel is gone", native_burn == 0 and model_burn == 0
                    and any(result["ProducerEnergyMWh"].get(i, 0) > 0 for i in solar_ids) and state["active"])
    return compared, state


def offline_solar_native(bench):
    """Focused regression for native Havok profiling, not the planet/200-panel lane."""
    _survival_template(bench)
    _join(bench)
    for node in _nodes(bench):
        _put_config(bench, node, {"offline.enabled": "false", "offline.settleFrames": "0"})
    owner = _client_identity(bench)
    grid = bench.cluster.node("node-1", f"/rc-test-ensure?owner={owner}", "POST")["grid"]
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=solar-backup", "POST")
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 60)
    _require(bench, "solar fixture has a native owner", bool(partition))
    bench.cluster.post("/automatic/pause")
    complete = _poll(lambda: bench.cluster.node("node-1", f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)
    _require(bench, "native physics-queue solar sampling completes without a worker crash", bool(complete))
    profile = bench.cluster.node("node-1", f"/profiles?partition={partition}")[0]
    sources = [s for s in profile["Sources"] if s["Kind"] == "Environmental"]
    native = _poll(lambda: (lambda state: state if state and state[0]["maximum"] > 0 else None)(
        bench.cluster.node("node-1", f"/offline-test-solar-state?grid={grid}")), 30)
    bench.record("native-solar-profile", {"profile": profile, "native": native})
    bench.check("real sun-facing panel and sampled curve both supply nonzero electricity", bool(native) and len(sources) == 1
                and max(sources[0]["Curve"]) > .5 and all(0 <= v <= 1 for v in sources[0]["Curve"]))
    bench.exercised(nativeHavokRays=True, panels=1, limitation="focused crash regression; planet/ridge/obstruction differential and exclusive 200-panel timing remain")


def t_oc_11(bench):
    """Native running-zone power/production/fuel references, outages, backup and actual shared wake.

    Short real engine intervals exercise transitions; the 4-hour/3-chip standalone lane stays distinct.
    """
    _survival_template(bench)
    api = _join(bench)
    for node in _nodes(bench):
        _put_config(bench, node, {"offline.enabled": "false", "offline.quietMeters": "0", "offline.settleFrames": "0",
                                "offline.wakeMeters": "0", "offline.leadSeconds": "0"})
    owner = _client_identity(bench)
    admin = bench.cluster.node("node-1", "/block-test-admin-safezone?op=create&x=100000&y=20000&z=0", "POST")["zone"]
    bench.cluster.node("node-1", f"/block-test-admin-safezone?op=update&id={admin}&radius=50&allowed=Drilling", "POST")
    fixture = bench.cluster.node("node-1", f"/rc-test-ensure?owner={owner}", "POST")
    grid = fixture["grid"]
    bench.cluster.node("node-1", f"/offline-test-zone?grid={grid}&seconds=45", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=battery-off", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=restore-production", "POST")
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 60)
    _require(bench, "powered zone fixture has one owner", bool(partition))
    _require(bench, "native zone is running before profiling", bool(_poll(lambda:
        bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")["active"], 30)))
    bench.cluster.post("/automatic/pause")
    first, _ = _zone_reference(bench, grid, partition, "zone-radius10")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=radius20", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=restore-production", "POST")
    larger, _ = _zone_reference(bench, grid, partition, "zone-radius20")
    bench.check("native paid-credit renewal consumes a chip with electricity available", any(
        reference["result"]["UpkeepConsumed"] for reference in (first, larger)))
    first_draw = first["predicted"]["Upkeep"][0]["OperationalPower"]
    bigger_draw = larger["predicted"]["Upkeep"][0]["OperationalPower"]
    bench.check("actual radius-dependent sink demand drives the shared budget", bigger_draw > first_draw, f"{first_draw} -> {bigger_draw} MW")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=box-zone", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=restore-production", "POST")
    boxed, boxed_state = _zone_reference(bench, grid, partition, "zone-box")
    bench.check("actual shape-dependent sink demand is captured", boxed_state["shape"] == "Box"
                and boxed["predicted"]["Upkeep"][0]["OperationalPower"] != bigger_draw)
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=sphere10", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=empty-production", "POST")
    _zone_reference(bench, grid, partition, "zone-empty-production", seconds=10, blocked=True)
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=restore-production", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=full-production", "POST")
    _zone_reference(bench, grid, partition, "zone-full-production", seconds=10, blocked=True)
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=restore-production", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=battery-off", "POST")
    _zone_reference(bench, grid, partition, "zone-fuel-exhaustion", depleted=True)
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=solar-backup", "POST")
    _zone_reference(bench, grid, partition, "zone-solar-battery-recovery", seconds=135, renewable=True)
    bench.cluster.post("/automatic/resume")
    # Actual shared recovery: the short, already-paid credit expires while offline, with
    # electricity/fuel available. No timer shortcut or synthetic result is injected at wake.
    new = bench.cluster.node("node-1", f"/offline-test-rate-fixtures?owner={owner}&count=1", "POST")[0]
    target = new["grid"]
    bench.cluster.node("node-1", f"/offline-test-zone?grid={target}&chips=0&seconds=60&radius=10", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={target}&mode=box-zone", "POST")
    zone_before = bench.cluster.node("node-1", f"/offline-test-zone-state?grid={target}")
    target_partition = _poll(lambda: (_owner_of(bench, target) or (None, None))[1], 60)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
    row = _poll(lambda: _offline_row(bench, target_partition), 90)
    _require(bench, "running player zone does not block ordinary quiet offlining", bool(row))
    profiles = bench.cluster.node("node-1", f"/profiles?partition={target_partition}")
    _require(bench, "the Final includes the running zone and its actual draw", any(p["Upkeep"] for p in profiles))
    time.sleep(65)
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={target}&ms=0", "POST")
    # A real radio request must still reach this now-unprotected, powered station.
    for _ in range(8):
        characters = bench.cluster.node("node-1", "/characters").get("characters") or []
        if any(r.get("radioOn") for r in characters if owner in (r.get("controllingIdentity"), r.get("identityOwner"))):
            break
        api.key("O")
        time.sleep(2)
    opened = _open_terminal(api, target)
    _require(bench, "real radio wake completes after paid zone credit expires", opened.get("screenOpen") is True, json.dumps(opened))
    applied = _applied(bench, "node-1")
    result = _assert_applied_inventory(bench, profiles, applied, target, "zone/production wake debits the immutable Final stocks exactly once")
    after = bench.cluster.node("node-1", f"/offline-test-zone-state?grid={target}")
    bench.record("zone-wake-exhausted", {"before": zone_before, "applied": applied, "after": after})
    bench.check("shared wake leaves no enabled bubble after chip exhaustion and retains block switch/rights", not after["active"] and after["zone"] == 0
                and after["blockEnabled"] is True and after["allowedActions"] == zone_before["allowedActions"]
                and after["radius"] == zone_before["radius"] and after["shape"] == zone_before["shape"])
    bench.check("shared wake charges reactor for both production and zone power", any(p.get("MyObjectBuilder_Ingot/Uranium", 0) > 0
                for p in result["ProducerFuelConsumed"].values()) and len(result["ProductionUnits"]) == 2 and result["DisabledZones"])
    admin_after = next((z for z in bench.cluster.node("node-1", "/block-test-safezones")["zones"] if z["entityId"] == admin), {})
    bench.record("admin-zone-uncharged-control", admin_after)
    bench.check("admin zone remains enabled under its separate no-player-chip rules", admin_after.get("enabled") is True and admin_after.get("block") == 0
                and all(z["Block"] != admin for z in result["Zones"]))
    api.close_remote_terminal()
    bench.exercised(nativePlayerZone=True, nativeRefineryAssembler=True, sharedBudget=True, finiteFuelOutage=True,
                    solarBatteryRecovery=True, actualSharedWake=True,
                    limitation="short clustered-engine references; full 4-hour/3-chip standalone differential and larger baseline envelope remain")


def offline_zone_paused(bench):
    """A real power-removed zone carries paid credit/settings through shared recovery.

    The powered manual-off control must stay off. The eventual power recovery is
    ordinary native simulation, including its 120-second activation delay.
    """
    _survival_template(bench)
    _join(bench)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false", "offline.quietMeters": "0", "offline.wakeMeters": "0",
                                "offline.leadSeconds": "0", "offline.settleFrames": "0"})
    owner = _client_identity(bench)
    grid = bench.cluster.node("node-1", f"/rc-test-ensure?owner={owner}", "POST")["grid"]
    before = bench.cluster.node("node-1", f"/offline-test-zone?grid={grid}&seconds=600&radius=20", "POST")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=box-zone", "POST")
    before = bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")
    _require(bench, "native player zone starts active with edited Box settings", before["active"] and before["shape"] == "Box")
    bench.cluster.node("node-1", f"/offline-test-options?grid={grid}&mode=zone-power-off", "POST")
    paused = _poll(lambda: (lambda s: s if s["zone"] == 0 and not s["working"] and s["remainingMs"] > 0 else None)(
        bench.cluster.node("node-1", f"/offline-test-zone-state?grid={grid}")), 30)
    _require(bench, "vanilla power loss removes the bubble and retains paid credit", bool(paused))
    bench.record("paused-zone-native", paused)
    partition = _poll(lambda: (_owner_of(bench, grid) or (None, None))[1], 60)
    _require(bench, "paused zone profile completes", bool(_poll(lambda: bench.cluster.node("node-1", f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)))
    captured = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}", "POST")
    bench.record("paused-zone-profile", captured)
    _require(bench, "unpowered zone with id zero is captured as paused", len(captured["Upkeep"]) == 1
             and not captured["Upkeep"][0]["Exists"] and captured["Upkeep"][0]["RemainingFrames"] > 0)
    time.sleep(10)
    comparison = bench.cluster.node("node-1", f"/offline-test-reference?partition={partition}")
    bench.record("paused-zone-reference", comparison)
    bench.check("paused paid credit does not run down while no electricity is supplied", comparison["predicted"]["Upkeep"][0]["RemainingFrames"] == captured["Upkeep"][0]["RemainingFrames"]
                and abs(comparison["actual"]["Upkeep"][0]["RemainingFrames"] - captured["Upkeep"][0]["RemainingFrames"]) < 1
                and not comparison["result"]["UpkeepConsumed"] and not comparison["result"]["ProducerFuelConsumed"])
    _put_config(bench, "node-1", {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
    _require(bench, "paused player zone can durably offline", bool(_poll(lambda: _offline_row(bench, partition), 90)))
    profiles = bench.cluster.node("node-1", f"/profiles?partition={partition}")
    _put_config(bench, "node-1", {"offline.enabled": "false"})
    bench.cluster.node("node-2", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node=node-2&reason=remote")
    applied = _poll(lambda: _applied(bench, "node-2"), 90)
    _require(bench, "paused zone reached actual shared recovery", bool(applied))
    bench.record("paused-zone-applied", applied)
    result = _assert_applied_inventory(bench, profiles, applied, grid, "unpowered shared wake invents no fuel or chip debit")
    after = _poll(lambda: bench.cluster.node("node-2", f"/offline-test-zone-state?grid={grid}") if _owner_of(bench, grid) == ("node-2", partition) else None, 60)
    bench.check("shared wake retains paused credit, block switch, Box dimensions and rights", after["zone"] == 0 and after["blockEnabled"]
                and after["shape"] == before["shape"] and after["radius"] == before["radius"] and after["allowedActions"] == before["allowedActions"]
                and abs(after["remainingMs"] - paused["remainingMs"]) < 2000 and not result["UpkeepConsumed"])
    bench.cluster.node("node-2", f"/offline-test-options?grid={grid}&mode=zone-power-on", "POST")
    activating = _poll(lambda: (lambda s: s if s["zone"] != 0 and s["activating"] else None)(bench.cluster.node("node-2", f"/offline-test-zone-state?grid={grid}")), 30)
    _require(bench, "real power restoration recreates the bubble with vanilla activation", bool(activating))
    restored = _poll(lambda: (lambda s: s if s["active"] and not s["activating"] else None)(bench.cluster.node("node-2", f"/offline-test-zone-state?grid={grid}")), 140, 2)
    _require(bench, "vanilla finishes activation with retained rights and geometry", bool(restored)
             and restored["shape"] == before["shape"] and restored["allowedActions"] == before["allowedActions"])
    bench.record("paused-zone-restored", restored)
    bench.cluster.node("node-2", f"/offline-test-options?grid={grid}&mode=zone-manual-off", "POST")
    time.sleep(2)
    _require(bench, "manual-off control profile completes", bool(_poll(lambda: bench.cluster.node("node-2", f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)))
    manual = bench.cluster.node("node-2", f"/offline-test-reference?partition={partition}", "POST")
    bench.record("manual-off-zone-profile", manual)
    bench.check("powered manually removed zone is not automatically modeled active", not manual["Upkeep"])
    bench.exercised(nativePausedZone=True, actualSharedRecovery=True, nativePowerRecovery=True,
                    limitation="no environmental power recovery during the offline interval in this fixture")


def offline_observed_policy(bench):
    """Native external game-logic rate sampling and conservative storage/inventory fallback.

    This exercises phase-5 short windows, not the unimplemented Measure/full-rotation leg of T-OC-06.
    """
    api, grid, _, partition, _ = _call_fixture(bench, observed=True)
    profile = next(p for p in bench.cluster.node("node-1", f"/profiles?partition={partition}") if grid in p["Members"])
    bench.record("observed-native-profile", profile)
    observations = {o["Block"]: o for o in profile["Observations"]}
    _require(bench, "real external components produce multi-sample rate observations", len(observations) >= 2
             and all(o["Samples"] >= 2 for o in observations.values()))
    for block, observation in observations.items():
        sources = [s for s in profile["Sources"] if s["Block"] == block]
        offset = len([s for s in profile["Sinks"] if s["Block"] == block])
        for index, source in enumerate(sources):
            bench.check("captured known-class source retains observed mean", abs(source["Maximum"] - observation["Mean"][offset + index]) < 1e-9,
                        json.dumps({"block": block, "maximum": source["Maximum"], "mean": observation["Mean"][offset + index]}))
    _require(bench, "opaque storage and script inventory effects are explicitly frozen", profile["Confidence"] == "Frozen" and any("script-driven storage" in u for u in profile["Unknowns"])
             and any("script inventory changes" in u for u in profile["Unknowns"]))
    bench.cluster.node("node-1", f"/offline-test-recovery?grid={grid}&ms=0", "POST")
    answer = _open_terminal(api, grid)
    _require(bench, "observed/unknown group wakes through real radio", answer.get("screenOpen") is True, json.dumps(answer))
    applied = _applied(bench, "node-1")
    bench.record("observed-applied", applied)
    _assert_applied_inventory(bench, [profile], applied, grid, "scripted inventories remain their frozen stock without invented mean output")
    # Every group in this controlled native fixture is observed; this is not an Octahedron census.
    bench.exercised(nativeObservedGroups=1, nativeProfiledGroups=1, fixtureOnlyFraction=1.0,
                    limitation="no real-world observed fraction or Measure/full-sun-rotation decision")
    api.close_remote_terminal()


def t_oc_12(bench):
    """Twenty real refinery bases share one measured formula and remeasure after a journaled clear."""
    _survival_template(bench)
    _join(bench)
    for node in ("node-1", "node-2"):
        _put_config(bench, node, {"offline.enabled": "false", "offline.settleFrames": "0", "offline.quietMeters": "0"})
    owner = _client_identity(bench)
    # Ensure the near-player relay exists; the twentieth far base still lies within 50 km.
    bench.cluster.node("node-1", f"/rc-test-ensure?owner={owner}", "POST")
    fixtures = bench.cluster.node("node-1", f"/offline-test-rate-fixtures?owner={owner}", "POST")
    for f in fixtures:
        bench.cluster.node("node-1", f"/offline-test-production?grid={f['grid']}", "POST")
    bench.record("cache-twenty-bases", fixtures)
    target_ids = {f["grid"] for f in fixtures}
    def powered():
        status = bench.cluster.node("node-1", "/offline-test-measurements")
        ready = {b["grid"] for b in status["production"] if b["working"] and b["power"] >= .999 and b["producing"] and b["output"] > 0}
        return status if target_ids <= ready else None
    _require(bench, "all twenty native refineries produced before measuring a complete timer tick", bool(_poll(powered, 60)))
    first_attempts = bench.cluster.node("node-1", "/offline-test-measurements")["attempts"]
    profiles = []
    for f in fixtures:
        location = _poll(lambda: _owner_of(bench, f["grid"]), 60)
        _require(bench, "refinery fixture has one native owner", bool(location), str(f["grid"]))
        node, partition = location
        complete = _poll(lambda: bench.cluster.node(node, f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)
        _require(bench, "native refinery profiling completes", bool(complete), str(f["grid"]))
        p = next(p for p in bench.cluster.node(node, f"/profiles?partition={partition}") if f["grid"] in p["Members"])
        profiles.append(p)
    bench.record("cache-twenty-profiles", profiles)
    measured = bench.cluster.node("node-1", "/offline-test-measurements")
    bench.record("cache-twenty-measurements", measured)
    iron = "MyObjectBuilder_BlueprintDefinition/IronOreToIngot"
    bench.check("all twenty real bases consume the cached iron formula", all(any(r["Id"] == iron and r["Cached"]
                for b in p["Production"] for r in b["Recipes"]) for p in profiles))
    bench.check("only the first base rotates for that formula", measured["attempts"] - first_attempts == 1, json.dumps(measured))
    entry = next(e for e in measured["cache"]["entries"] if iron in e["Key"])
    bench.cluster.node("node-1", "/profiles/cache/clear?key=" + quote(entry["Key"], safe=""), "POST")
    _require(bench, "authoritative clear reaches the measuring node before reprofiling", bool(_poll(lambda:
        not any(e["Key"] == entry["Key"] for e in bench.cluster.node("node-1", "/offline-test-measurements")["cache"]["entries"]), 30)))
    node, partition = _owner_of(bench, fixtures[0]["grid"])
    _require(bench, "cleared formula remeasures on the next native profile", bool(_poll(lambda: bench.cluster.node(node,
             f"/trigger/profile?partition={partition}", "POST").get("complete"), 60)))
    after = bench.cluster.node(node, "/offline-test-measurements")
    bench.record("cache-after-clear", after)
    bench.check("clear increments epoch and permits one fresh measurement", after["attempts"] == measured["attempts"] + 1
                and any(e["Key"] == entry["Key"] and e["Epoch"] == entry["Epoch"] + 1 for e in after["cache"]["entries"]))


SCENARIOS_OFFLINE.update({
    "P2-OFF-CALL-TERMINAL": (p2_off_call_terminal, "offline radio terminal, caller-node wake, pin and sweep lifecycle", "3"),
    "P2-OFF-CALL-RC": (p2_off_call_rc, "offline dynamic drone radio control, raw client driving and presence", "3"),
    "P2-OFF-CALL-IGC": (p2_off_call_igc, "offline IGC remains a no-wake delivery failure", "3"),
    "P2-OFF-CALL-CONFLICT": (p2_off_call_conflict, "two live clients on separate nodes cannot steal a pinned partition in three minutes", "3"),
    "T-OC-07": (t_oc_07, "real PB/timer/ejector Block and Freeze policies through shared wake", "3"),
    "T-OC-09": (t_oc_09, "actual shared-apply crash, immutable-Final retry and post-Final no-repeat recovery", "3"),
    "OFF-CACHE-DURABILITY": (offline_cache_durability, "WA-local cache clear journal and pre-Final replacement (R8)", "3"),
    "P2-OFF-CALL-DENY": (p2_off_call_deny, "authenticated hostile-owner and missing-relay requests do not wake", "3"),
    "P2-OFF-CALL-RATE": (p2_off_call_rate, "twenty authenticated offline-grid requests enforce one pending call-up", "3"),
    "P2-OFF-CALL-LASER": (p2_off_call_laser, "native id/GPS laser wake and established-link no-wake", "3"),
    "T-OC-10": (t_oc_10, "real changed-mod bytes skip compensation and leave the woken partition healthy", "3"),
    "T-OC-13": (t_oc_13, "parked large dynamic grid offlines and preserves its pose", "2"),
    "T-OC-08": (t_oc_08, "moving rotor subgrid leaves a quiet sibling profiled and reuses its rate when stopped", "2"),
    "OFF-OBSERVED-POLICY": (offline_observed_policy, "native short-window external rates and conservative script storage/inventory policy", "3"),
    "OFF-LIVE-DIFFERENTIAL": (offline_live_differential, "short native engine reference for full Auto battery and real refinery production", "3"),
    "T-OC-12": (t_oc_12, "twenty native refinery bases share one rotation and remeasure after clear", "2"),
    "T-OC-11": (t_oc_11, "running player zones, common production power/fuel budget, outage/backup and real shared wake", "3"),
    "OFF-SOLAR-NATIVE": (offline_solar_native, "native physics-queue solar profiling crash regression", "2"),
    "OFF-ZONE-PAUSED": (offline_zone_paused, "native power-paused zone credit, shared recovery, vanilla reactivation and manual-off control", "3"),
    "OFF-REMOTE-CAPTURE-RACE": (offline_remote_capture_race, "native terminal pin acquired after gateway read refuses source capture", "3"),
})
