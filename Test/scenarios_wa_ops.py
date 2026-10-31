"""World Authority operations: the former run-cluster.sh lanes `rc-control`, `pod-spawn`,
`respawn-chaos` and `chaos`, as native scenarios.

Every one of them boots node-1, node-2 and wa-1 on a split world and joins one headless client on
node-1 (``_join``):

* ``P2-WA-04``  - remote-control arbitration: a cross-partition take through the antenna chain, a take
  into a frozen (paused) transfer denied by the game-layer gate, the on-demand partition pull after
  the station moves to node-2, the far-broadcaster HUD signal and the remote terminal across nodes;
* ``P2-WA-06b`` - pod respawn with least-loaded placement on a Survival world: a fresh partition on
  node-2, the registry and client route follow it, it is capturable, the respawn cooldown
  replicates, the WA's own drop schedule fires, and an inventory-full purchase refunds;
* ``P2-DROP-REMOVAL`` - a drop container handed over to another node despawns there at its vanilla deadline,
  carried with the grid (#1391); ``P2-DROP-REMOVAL-RED`` is the same with the carry off and must outlive it;
* ``P2-DROP-CAP`` - at most ``drops.maxActive`` container drops are active cluster-wide (#1443): skipped, not queued,
  at the cap; replaced when one despawns; the knob live; an offline drop counted until its vanilla deadline; 0 = no cap;
* ``P2-WA-06c`` - the WA is SIGKILLed holding a cross-node respawn arbitration: the respawn fails
  cleanly into a pending retry and completes on node-2 once a replacement WA is designated;
* ``P2-REC-01`` - the WA is SIGKILLed inside a held banking and faction mutation under continuous
  traffic: conservation, exactly-once retries by operation id, exactly-once encounter dispatch
  across the replacement, and a node-to-WA network partition (P2-WA-02 is asserted here too).

The two chaos scenarios share ``_wa_failover``: WA kill -> lease fence -> replacement WA.
"""

from __future__ import annotations

import json
import random
import re
import shutil
import threading
import time
import urllib.error
import urllib.request
import uuid

from bench import survival_template
from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _grid_ops, _handover_url, _join, \
    _lines, _log_files, _no_reconnect, _nodes, _owners, _partitions
from scenarios_recovery import RECOVERY_TIMEOUT

SERVER_TIMEOUT = 180  # the replacement WA's world load


# -- shared plumbing ------------------------------------------------------------------------


def _require(bench, label: str, passed: bool, detail: str = "") -> None:
    """A gate the shell lane aborted on: recorded as a check, and nothing after it runs."""
    if not bench.check(label, passed, detail):
        raise ClusterError(f"{label}: {detail}"[:600])


def _try(function, *args, **kwargs):
    """The call's answer, or ``{"error": ...}`` - a refused or timed-out call is an observation."""
    try:
        return function(*args, **kwargs)
    except Exception as error:  # noqa: BLE001
        return {"error": str(error)[:300]}


def _poll(predicate, timeout: float, interval: float = 1.0):
    """wait_until that returns None on timeout instead of raising (the caller checks)."""
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def _http(method: str, url: str, timeout: float) -> tuple[int, str]:
    """(status, body); status 0 when the request timed out or never connected (curl's 000)."""
    request = urllib.request.Request(url, method=method, data=b"" if method == "POST" else None)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode(errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")
    except Exception as error:  # noqa: BLE001 - URLError, socket timeout
        return 0, str(error)


def _node_url(bench, node_id: str, path: str) -> str:
    return f"http://{_nodes(bench)[node_id].control}/cluster{path}"


def _game_log(bench, node_id: str) -> str:
    """The SE game log under the node data dir: MyLog lines (the remote-control gate, the RPC
    allowlist) land there, NOT in the process stdout log."""
    data = bench.cluster.config.node_data(_nodes(bench)[node_id])
    return "\n".join(path.read_text(errors="replace") for path in sorted(data.glob("SpaceEngineersDedicated_*.log")))


def _game_count(bench, node_id: str, pattern: str) -> int:
    return len(re.findall(pattern, _game_log(bench, node_id)))


def _client_game_lines(bench, client_id: int, pattern: str, limit: int = 20) -> list[str]:
    """Lines matching ``pattern`` in the SE logs THIS client launch wrote.

    Bounded by the launch, not by the file name: a client data directory accumulates the logs of
    every launch that ever used it, and a scenario that reads them all attributes another run's
    line to this one - which is exactly how a diagnosis went wrong once already (T-0201, where
    every per-run directory carried the same failing run's warning). An empty list therefore means
    "this launch wrote no such line", which is a claim worth making; it does not mean the client
    never writes one."""
    client = bench.clients.get(client_id)
    if client is None:
        return []
    expression = re.compile(pattern)
    lines = [line for path in client.game_logs() if path.stat().st_mtime >= client.launched_at - 5
             for line in path.read_text(errors="replace").splitlines() if expression.search(line)]
    return lines[-limit:]


def _session(bench, client_id: int = CLIENT_ID) -> dict | None:
    return next((s for s in bench.cluster.get("/transport/status").get("sessions") or []
                 if s.get("client") == client_id), None)


def _client_identity(bench, client_id: int = CLIENT_ID) -> int:
    identity = (_session(bench, client_id) or {}).get("identityId")
    _require(bench, "the Gateway transport status names the client identity", bool(identity), str(identity))
    return identity


def _authority(bench) -> dict:
    return bench.cluster.get("/registry/world-authority")


def _owned_by(bench, partition: int, node_id: str) -> bool:
    return any(row["Partition"] == partition and row["Node"] == node_id for row in _owners(bench))


def _medical_click(bench, api, row_text: str, timeout: float = HANDOVER_TIMEOUT) -> int:
    """Select a respawn row on the vanilla medical screen; returns the screen index."""
    def selected():
        screens = _try(api.list_screens)
        screen = next((s["index"] for s in screens if s.get("type") == "MyGuiScreenMedicals"), None) \
            if isinstance(screens, list) else None
        if screen is None:
            return None
        try:
            api.control_click(text=row_text, screen=screen)
        except Exception:  # noqa: BLE001 - the row is not there yet
            return None
        return ("screen", screen)

    found = _poll(selected, timeout)
    _require(bench, f"the medical screen exposes the '{row_text}' row", found is not None)
    return found[1]


def _kill_player(bench) -> None:
    killed = bench.cluster.node("node-1", f"/respawn-test-kill?userId={CLIENT_ID}", "POST")
    _require(bench, "node-1 applied lethal damage to the client character", killed.get("killed") is True, json.dumps(killed))
    dead = _poll(lambda: (lambda p: isinstance(p, dict) and p.get("attached") and p.get("dead") is True)(
        _try(bench.cluster.node, "node-1", f"/respawn-test-player?userId={CLIENT_ID}")), HANDOVER_TIMEOUT)
    _require(bench, "node-1 observed the client character death", bool(dead))


def _wa_failover(bench, generation: int, *, killed: str = "wa-1", replacement: str = "wa-2", fresh: bool = True,
                 after_kill=None, fenced=None, after_fence=None) -> dict:
    """WA kill -> lease fence -> replacement WA. Returns the replacement's authority row.

    ``generation`` is the authority generation read before the kill. ``after_kill()`` runs between
    the SIGKILL and the fence wait (outage-window assertions); ``fenced(row)`` is the fence predicate
    (default: no authority node, generation advanced); ``after_fence(row)`` runs before the
    replacement starts. ``fresh`` gives the replacement an empty data root (the shell lanes'
    wa-replacement-data); otherwise it runs on the killed WA's retained data root.
    The replacement is a new node slot in the next port slot, role WA, epoch = next free (4 with two
    regular nodes, as the shell lanes set), with the same admission environment as every node.
    """
    bench.cluster.kill(slot=killed)
    process = bench.cluster.processes.pop(killed, None)
    if process is not None:
        _try(process.wait, timeout=30)
    bench.record(f"{killed}-killed", {"generation": generation, "at": time.time()})
    if after_kill:
        after_kill()

    fenced = fenced or (lambda row: row.get("Node") is None and (row.get("Generation") or 0) > generation)
    row = _poll(lambda: (lambda r: r if isinstance(r, dict) and fenced(r) else None)(_try(_authority, bench)),
                RECOVERY_TIMEOUT)
    _require(bench, f"the Gateway fenced the killed World Authority lease ({killed})", row is not None,
             json.dumps(_try(_authority, bench))[:300])
    bench.record(f"{killed}-fenced", row)
    if after_fence:
        after_fence(row)

    config = bench.cluster.config
    spec = next((node for node in config.nodes if node.node_id == replacement), None) \
        or config.add_node(replacement, role="WA")
    data = config.node_data(spec)
    # prepare() only wiped the data roots of the nodes it knew about; this slot is added later.
    if data.is_symlink():
        data.unlink()
    elif data.exists():
        shutil.rmtree(data)
    if not fresh:
        data.symlink_to(config.node_data(_nodes(bench)[killed]), target_is_directory=True)
    bench.cluster.start_node(replacement)
    bench.cluster.wait_node_log(replacement, "Game ready", timeout=SERVER_TIMEOUT)
    designated = _authority(bench)
    bench.record(f"{replacement}-designated", designated)
    _require(bench, f"the replacement World Authority {replacement} was designated",
             designated.get("Node") == replacement and (designated.get("Generation") or 0) > generation,
             json.dumps(designated))
    return designated


# -- P2-WA-04 rc-control --------------------------------------------------------------------


def p2_wa_04(bench) -> None:
    """WA-6 remote-control arbitration and WA-4 remote terminal across nodes.

    A vanilla cross-partition (same-node) remote control take works through on-demand replication and
    the mutual antenna chain (suit -> relay -> station); a take against a grid frozen for a partition
    transfer is refused through the vanilla failure path - the RPC layer lets exactly this event
    through (rpc_allow) so the game-layer gate can deny it clearly instead of the silent frozen-root
    drop; after the transfer the grid lives on another node, and the absent-replicable demand pulls it
    back."""
    api = _join(bench)
    identity = _client_identity(bench)
    client_partition = _client_partition(_partitions(bench, "node-1"))
    _require(bench, "node-1 reports the client partition", client_partition is not None)

    rc = bench.cluster.node("node-1", f"/rc-test-ensure?owner={identity}", "POST")
    bench.record("rc-control-ensure", rc)
    grid, block, relay = rc.get("grid"), rc.get("block"), rc.get("relay")
    _require(bench, "rc-test-ensure returned the station grid, block and relay", bool(grid and block and relay), json.dumps(rc))
    # The scenario moves the station itself; a balance migration that moves it first leaves the paused
    # handover with "Source did not report partition" (run 20260917-120159Z, 14:09:56).
    bench.cluster.post("/automatic/pause")

    # The station must claim its own clientless partition; the relay joins the client's.
    def settled():
        station = _try(bench.cluster.node, "node-1", f"/partition-of?entity={grid}")
        relayed = _try(bench.cluster.node, "node-1", f"/partition-of?entity={relay}")
        partition = station.get("partition")
        return partition if partition and relayed.get("partition") == client_partition else None

    rc_partition = _poll(settled, RECOVERY_TIMEOUT)
    _require(bench, "the rc station partition settled and the relay merged into the client partition",
             rc_partition is not None,
             f"station={_try(bench.cluster.node, 'node-1', f'/partition-of?entity={grid}')} "
             f"relay={_try(bench.cluster.node, 'node-1', f'/partition-of?entity={relay}')} client={client_partition}")
    _require(bench, "the rc station claimed its own partition, not the client's", rc_partition != client_partition)

    # MyRemoteControl.CanControl walks the antenna graph to the CHARACTER, and §5.5 settles a fresh
    # spawn's suit radio OFF - so the player action that precedes any remote take, switching
    # broadcasting on (default key O), is part of the scenario. Before the settle fix the
    # broadcaster-init race sometimes left the radio on, which is why this take used to pass ~1/3 of
    # runs and fail silently otherwise (UseFailureCallback logs nothing anywhere).
    def radio_on() -> bool:
        rows = bench.cluster.node("node-1", "/characters").get("characters") or []
        return any(r.get("radioOn") for r in rows if identity in (r.get("controllingIdentity"), r.get("identityOwner")))

    radio = False
    for _ in range(8):
        if radio_on():
            radio = True
            break
        _try(api.clear_input_state)
        api.key("O")
        time.sleep(2)
    _require(bench, "the client switched its suit radio on before the take", radio,
             json.dumps([{k: r.get(k) for k in ("entityId", "controllingIdentity", "radioOn")}
                         for r in bench.cluster.node("node-1", "/characters").get("characters") or []]))

    def status() -> dict:
        return bench.cluster.node("node-1", f"/rc-test-status?block={block}")

    def take(timeout: float, attempts: int) -> dict:
        result = {}
        for attempt in range(attempts):
            result = _try(api.take_remote_control, grid, block, timeout=timeout)
            if result.get("controlledByMe") is True:
                break
            if attempt + 1 < attempts:
                time.sleep(2)
        return result

    # Vanilla cross-partition take: on-demand replication + mutual antenna chain.
    taken = take(10, 8)
    bench.record("rc-control-take", taken)
    if taken.get("controlledByMe") is not True:
        # The client response alone cannot say whether the take ever REACHED the server ("200 does not
        # mean it did anything") - record the server's own view of the block and its denial log.
        bench.record("rc-control-take-server", _try(status))
        # BOTH NODES, AND THE CLIENT (T-0201). The old capture read node-1's denial lines only,
        # and it read them for a block that may be owned by node-2 - so its empty output was
        # compatible with a denial nobody looked for. It also missed where the answer actually
        # was: the one failure analysed to the end was CLIENT-side, the RPC dropped before it left
        # the process, with "Raising event on object which is not recognized by replication" in
        # the client's own log and nothing at all on either node. A capture that cannot produce
        # the line that explains the failure is a capture that teaches you to look server-side.
        denials = [f"{node}: {line}" for node in sorted(_nodes(bench))
                   for line in _game_log(bench, node).splitlines()
                   if re.search(r"remote control denied|rpc_allow|rpc_drop", line)][-40:]
        unrecognised = _client_game_lines(bench, CLIENT_ID, r"not recognized by replication")
        bench.record("rc-control-take-denials.txt", "\n".join(
            denials + ["", "client, this launch only:"] + (unrecognised or ["(none)"])))
        _require(bench, "the client took the cross-partition remote control", False, json.dumps(taken)[:300])
    current = status()
    bench.check("node-1 reports the client controlling the block",
                current.get("controlled") is True and current.get("controllingIdentity") == identity, json.dumps(current))

    # Server-side release (every vanilla client-side release raiser is server-guarded).
    release = bench.cluster.node("node-1", f"/rc-test-release?block={block}", "POST")
    _require(bench, "the server-side release released the block", release.get("released") is True, json.dumps(release))
    views = {}

    def released():
        views["server"] = _try(status)
        views["client"] = _try(api.get_remote_control, block)
        return views["server"].get("controlled") is False and views["client"].get("controlledByMe") is False

    _require(bench, "the release settled on both sides", bool(_poll(released, 30)), json.dumps(views)[:300])

    # Freeze the station partition deterministically: paused clientless handover.
    denied_before = _game_count(bench, "node-1", r"remote control denied")
    allow_before = _game_count(bench, "node-1", r"rpc_allow reason=frozen-control-denial")
    url = _handover_url(bench, rc_partition, clientless=True) + "&pauseAfterCapture=true"
    request: dict = {}

    def fire():
        request["result"] = _try(bench.cluster.post, url, timeout=300)

    def paused():
        rows = _try(bench.cluster.get, "/handover/paused")
        return next((row for row in rows if row.get("partition") == rc_partition and row.get("paused")), None) \
            if isinstance(rows, list) else None

    # Fire-and-retry: a single POST can die on a transient Gateway 500 (its 10 s node-call timeout
    # against a saturated game thread). Re-firing a failed fenced clientless request is safe - the
    # pause poll is the arbiter of what actually started.
    pause, handover = None, None
    for _ in range(3):
        handover = threading.Thread(target=fire, daemon=True)
        handover.start()
        deadline = time.time() + HANDOVER_TIMEOUT
        while time.time() < deadline:
            pause = paused()
            if pause or not handover.is_alive():
                break
            time.sleep(1)
        if pause or handover.is_alive():
            break
        time.sleep(2)
    bench.record("rc-control-handover", request)
    _require(bench, "the rc-control handover reached the capture pause", pause is not None, json.dumps(request)[:300])
    transfer = pause["transferId"]

    # A real client RPC into the frozen window: allowed through the RPC layer, denied clearly by the
    # game-layer gate, and never a silent frozen-root drop.
    frozen = _try(api.take_remote_control, grid, block, timeout=8)
    bench.record("rc-control-frozen-take", frozen)
    bench.check("the frozen-window take was refused", frozen.get("controlledByMe") is False, json.dumps(frozen))
    allow_frozen = _game_count(bench, "node-1", r"rpc_allow reason=frozen-control-denial")
    bench.check("the RPC layer allowlisted the frozen remote control use request", allow_frozen > allow_before,
                f"{allow_before} -> {allow_frozen}")
    denied_frozen = _game_count(bench, "node-1", r"remote control denied")
    bench.check("the game-layer gate denied the frozen client take", denied_frozen > denied_before,
                f"{denied_before} -> {denied_frozen}")
    dropped = [line for line in _game_log(bench, "node-1").splitlines()
               if f"rpc_drop reason=frozen-root root={grid}" in line and "RequestUseMessage" in line]
    bench.check("the use request was not silently dropped at the RPC layer", not dropped, "\n".join(dropped[:3]))

    # The locally-invoked branch of the same gate: a server-side use request.
    local = bench.cluster.node("node-1", f"/rc-test-use?block={block}&client={CLIENT_ID}", "POST")
    bench.check("the server-side frozen use request was issued", local.get("requested") is True, json.dumps(local))
    denied_local = _game_count(bench, "node-1", r"remote control denied")
    bench.check("the game-layer gate denied the locally-invoked frozen take", denied_local > denied_frozen,
                f"{denied_frozen} -> {denied_local}")
    bench.check("no frozen-window take gained control", status().get("controlled") is False)

    # Release the pause; the transfer completes and the station moves to node-2.
    bench.cluster.post(f"/handover/release?transferId={transfer}")
    handover.join(timeout=300)
    bench.record("rc-control-handover", request)
    _require(bench, "the released rc-control handover completed",
             not handover.is_alive() and "error" not in (request.get("result") if isinstance(request.get("result"), dict) else {}),
             json.dumps(request)[:300])
    _require(bench, "the rc station partition arrived on node-2", _owned_by(bench, rc_partition, "node-2"))

    # Cross-node take: the grid left this node, so the first take finds nothing - but the
    # absent-replicable demand triggers the on-demand partition PULL: the Gateway hands the station
    # partition back to the client's node, and a retried take succeeds through plain vanilla
    # replication. First wait for node-1's replication destroy to reach the client: while the stale
    # local entity lingers, the take's presence check short-circuits and no replication request (hence
    # no pull demand) is ever sent.
    def stale_gone():
        return _try(api.get_remote_control, block).get("found") is False

    _require(bench, "the client dropped the departed rc block after the transfer", bool(_poll(stale_gone, HANDOVER_TIMEOUT)))
    cross = _try(api.take_remote_control, grid, block, timeout=8)
    bench.record("rc-control-cross-take", cross)
    bench.check("the cross-node take sent the replication request", cross.get("replicationRequested") is True, json.dumps(cross))
    bench.check("the cross-node take did not reach the departed grid without winning it through the pull",
                cross.get("found") is False or cross.get("controlledByMe") is True, json.dumps(cross))
    bench.check("the cross-node take produced no node-1 denial",
                _game_count(bench, "node-1", r"remote control denied") == denied_local)

    pull_pattern = rf"remote pull grid={grid} .*accepted="
    pull = _poll(lambda: _lines(bench, "node-1", pull_pattern), HANDOVER_TIMEOUT)
    _require(bench, f"node-1 reported the pull demand for grid {grid}", bool(pull),
             "\n".join(_lines(bench, "node-1", r"remote pull")[-5:]))
    bench.check("the Gateway accepted the pull demand",
                any("accepted=True" in line for line in _lines(bench, "node-1", rf"remote pull grid={grid}\b")),
                "\n".join(_lines(bench, "node-1", rf"remote pull grid={grid}\b")[-3:]))
    bench.check("the Gateway logged the pull demand",
                bool(_lines(bench, "gateway", rf"Pull demand partition {rc_partition} node-2 -> node-1")))
    _require(bench, "the station partition returned to node-1 after the pull demand",
             bool(_poll(lambda: _owned_by(bench, rc_partition, "node-1"), HANDOVER_TIMEOUT)))

    pulled = take(10, 10)
    bench.record("rc-control-pulled-take", pulled)
    _require(bench, "the retried take succeeded after the pull", pulled.get("controlledByMe") is True,
             f"client={json.dumps(pulled)[:200]} server={_try(status)}")
    current = status()
    bench.check("node-1 reports the client controlling the pulled block",
                current.get("controlled") is True and current.get("controllingIdentity") == identity, json.dumps(current))
    bench.cluster.node("node-1", f"/rc-test-release?block={block}", "POST")

    # WA-4 exit criterion: the remote terminal opens across nodes. Move the station away again (plain
    # clientless handover, no pause), wait for the stale client entity to drop, then open the terminal
    # through the vanilla remote access flow - the client re-requests replication until the second
    # pull (gated by the Gateway's 60 s per-partition cooldown) brings the grid back and the REAL
    # terminal screen opens on the remote antenna.
    # The release above clears the client's control, but the Gateway keeps the client routed to the
    # station partition until its next route refresh; a clientless handover refuses it until then
    # ("routed to a connected client", bench run 20260917-055254Z). Retry only that refusal.
    def terminal_handover():
        try:
            # Wrapped: an empty answer must not read as "not yet" and trigger a second handover.
            return {"response": bench.cluster.post(_handover_url(bench, rc_partition, clientless=True), timeout=300)}
        except ClusterError as error:
            if "routed to a connected client" in str(error):
                return None
            raise
    moved = _poll(terminal_handover, 60, interval=2)
    _require(bench, "the pre-terminal transfer to node-2 was accepted", moved is not None)
    bench.record("rc-control-terminal-handover", moved)
    _require(bench, "the rc station partition moved to node-2 before the terminal leg", _owned_by(bench, rc_partition, "node-2"))
    _require(bench, "the client dropped the departed rc block before the terminal leg", bool(_poll(stale_gone, HANDOVER_TIMEOUT)))

    # WA-4 HUD test: with the station on node-2 and its grid NOT streamed to the client, the HUD
    # signal set (the relayed broadcaster set UpdateHud reads) must still name the cross-node station
    # through its replicated far-broadcaster proxy.
    signals = {}

    def hud_seen():
        signals["last"] = _try(api.get_hud_signals)
        return any(str(row.get("name") or "").startswith("cluster-rc-host") for row in signals["last"].get("relayed") or [])

    seen = _poll(hud_seen, HANDOVER_TIMEOUT)
    bench.record("rc-control-hud-signals", signals.get("last"))
    bench.check("the cross-node station is in the client HUD signal set", bool(seen), json.dumps(signals.get("last"))[:300])

    def accepted_pulls() -> int:
        return sum("accepted=True" in line for line in _lines(bench, "node-1", rf"remote pull grid={grid}\b"))

    accepted_before = accepted_pulls()
    # Short open attempts in a loop that outlasts the Gateway's 60 s pull cooldown from the take leg:
    # each attempt re-requests replication.
    terminal = {}
    for attempt in range(12):
        terminal = _try(api.open_remote_terminal, grid, timeout=10)
        if terminal.get("screenOpen") is True:
            break
        if attempt < 11:
            time.sleep(2)
    bench.record("rc-control-remote-terminal", terminal)
    _require(bench, "the remote terminal opened across nodes", terminal.get("screenOpen") is True, json.dumps(terminal)[:300])
    bench.check("the remote terminal opened on a streamed, antenna-connected grid",
                terminal.get("found") is True and terminal.get("connected") is True, json.dumps(terminal)[:300])
    bench.check("the terminal leg went through a second accepted pull", accepted_pulls() > accepted_before,
                f"{accepted_before} -> {accepted_pulls()}")
    bench.check("the station partition is back on node-1 after the terminal pull", _owned_by(bench, rc_partition, "node-1"))
    def terminal_visible():
        screens = _try(api.list_screens)
        return isinstance(screens, list) and any(s.get("type") == "MyGuiScreenTerminal" and s.get("visible") is True
                                                 for s in screens)

    visible = _poll(terminal_visible, 20)
    bench.check("the terminal screen is visible in the client UI stack", bool(visible))
    bench.screenshot(CLIENT_ID, "rc-control-remote-terminal")
    closed = _try(api.close_remote_terminal)
    bench.check("the remote terminal screen was open at close time", closed.get("wasOpen") is True, json.dumps(closed))

    bench.check("the client never disconnected", not _lines(bench, "gateway", rf"Client {CLIENT_ID} disconnected"))
    bench.check("the client process is alive", bench.clients[CLIENT_ID].alive())
    bench.check("the client's character is alive", bench.living_character(CLIENT_ID) is not None)
    bench.exercised(grid=grid, block=block, partition=rc_partition, transfer=transfer,
                    allowlisted=[allow_before, allow_frozen], denied=[denied_before, denied_frozen, denied_local])


# -- P2-WA-06b pod-spawn --------------------------------------------------------------------


def _pod_world(bench):
    """Respawn ships are survival-only (the medical screen skips AddSpaceRespawnShips in Creative), so
    this scenario's world declares Survival to get the vanilla Space Pod row."""
    template = survival_template(bench)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = template / name
        if not path.is_file():
            continue
        text = path.read_text()
        # WA-1: a nonzero SpawnShipTimeMultiplier so the pod spawn SETS a cooldown (vanilla removes
        # all entries when the multiplier is 0) - the scenario asserts the cooldown replicates to the
        # node that did not execute the spawn. Only respawn-ship rows consult cooldowns; the initial
        # join spawns via the medbay row (medical rooms are listed first), which never sets one.
        text = text.replace("<SpawnShipTimeMultiplier>0</SpawnShipTimeMultiplier>",
                            "<SpawnShipTimeMultiplier>1</SpawnShipTimeMultiplier>")
        # WA-3 production cadence: the drop-cadence leg needs EnableContainerDrops, which vanilla only
        # honors in Survival. It is inert without the test knob: the vanilla node-side spawner is
        # gated to the WA (which hosts no players) and the WA director's first per-player drop sits
        # one 15-minute interval out.
        text = text.replace("<EnableContainerDrops>false</EnableContainerDrops>",
                            "<EnableContainerDrops>true</EnableContainerDrops>")
        path.write_text(text)
    return template


def _drop_grids(bench) -> int:
    return sum(bench.cluster.node(node, "/drop-container-grids")["count"] for node in ("node-1", "node-2"))


def p2_wa_06b(bench) -> None:
    """WA-6 pod spawn with least-loaded placement.

    A respawn-ship spawn creates its own fresh partition, so the cluster places it on the least-loaded
    node - node-2 here (node-1 holds the start area and the client). The Space Pod lands there through
    the pod-flavored respawn switch (client riding through, no reconnect), forms a FRESH partition
    owned by the executing node, the client route follows it, and it is capturable into the catalog."""
    bench.cluster.config.world_template = _pod_world(bench)
    api = _join(bench)
    before = _partitions(bench, "node-1")
    bench.record("pod-spawn-partitions-before", before)
    old_partition = _client_partition(before)
    _require(bench, "node-1 reports the client partition", old_partition is not None)
    known = {row["Partition"] for row in _owners(bench)}
    # Count only arbitration DECISION lines (target=/partition=); the shared switch-completion line
    # (success=) is emitted for pod spawns too and is not arbitration.
    route_pattern = r"respawn route operation=.* partition="
    routes_before = len(_lines(bench, "node-1", route_pattern))

    # Placement's client tiebreak needs the registry to actually map the client - the live route and
    # the 5 s character-home report both lag a fresh spawn, so establish the premise before asking
    # (until then the tiebreak legitimately reads 0 clients, and a topology merge can leave the
    # partition counts tied).
    mapped = _poll(lambda: "error" not in _try(bench.cluster.get, f"/registry/client-routes?client={CLIENT_ID}"),
                   HANDOVER_TIMEOUT)
    _require(bench, "the registry mapped the client before pod placement", bool(mapped))
    placement = bench.cluster.get("/registry/pod-placement")
    bench.record("pod-spawn-placement", placement)
    _require(bench, "pod placement picked the least-loaded node-2", placement.get("node") == "node-2", json.dumps(placement))
    target = "node-2"

    _kill_player(bench)
    screen = _medical_click(bench, api, "Space Pod")
    bench.screenshot(CLIENT_ID, "pod-spawn-medical")
    api.control_click(text="Respawn", screen=screen)

    player = _poll(lambda: (lambda p: p if isinstance(p, dict) and p.get("attached") and p.get("dead") is False
                            and (p.get("character") or 0) > 0 else None)(
        _try(bench.cluster.node, target, f"/respawn-test-player?userId={CLIENT_ID}")), HANDOVER_TIMEOUT)
    _require(bench, f"the client respawned in the Space Pod on {target}", player is not None)
    character = player["character"]
    bench.check("the pod spawn was not routed through the exact-respawn arbitration",
                len(_lines(bench, "node-1", route_pattern)) == routes_before)
    bench.check(f"node-1 reported the pod placement route to {target}",
                bool(_lines(bench, "node-1", rf"pod route operation=.* target={target}\b")))

    # The pod forms a FRESH partition: a new id, unknown to the registry before, holding the client.
    # The character is SEATED in the pod, so the partition's single member is the pod grid itself
    # (seated characters travel with their grid); the grid membership is asserted via partition-of.
    after = {}

    def fresh():
        after["partitions"] = _try(_partitions, bench, target)
        rows = after["partitions"] if isinstance(after["partitions"], list) else []
        new = _client_partition(rows)
        count = next((p.get("memberCount", 0) for p in rows if p["id"] == new), 0)
        return new if new and new != old_partition and new not in known and count >= 1 else None

    new_partition = _poll(fresh, RECOVERY_TIMEOUT)
    bench.record("pod-spawn-partitions-after", after.get("partitions"))
    _require(bench, "the pod spawn formed a fresh client partition", new_partition is not None,
             f"old={old_partition} known={sorted(known)}")

    # Registry follows: the executing node owns the fresh partition and the client route points at it.
    def followed():
        route = _try(bench.cluster.get, f"/registry/client-routes?client={CLIENT_ID}")
        return _owned_by(bench, new_partition, target) and route.get("Partition") == new_partition \
            and route.get("Node") == target

    _require(bench, "the registry and the client route followed the pod spawn", bool(_poll(followed, RECOVERY_TIMEOUT)),
             json.dumps({"owners": [r for r in _owners(bench) if r["Partition"] == new_partition],
                         "route": _try(bench.cluster.get, f"/registry/client-routes?client={CLIENT_ID}")})[:400])

    # The pod grid itself is there with the character (client-visible evidence).
    grids = {}

    def pod():
        result = _try(_grid_ops, api, {"gets": [{"type": "grid_list"}]})
        grids["last"] = result
        rows = ((result.get("gets") or [{}])[0].get("data") or {}).get("grids") or [] if "gets" in result else []
        return next((g["entityId"] for g in rows if re.search(r"Pod|pod|Respawn Ship", g.get("name") or "")), None)

    pod_grid = _poll(pod, RECOVERY_TIMEOUT)
    bench.record("pod-spawn-grids", grids.get("last"))
    _require(bench, "the client sees the pod grid", pod_grid is not None)
    membership = bench.cluster.node(target, f"/partition-of?entity={pod_grid}")
    bench.check("the pod grid is a member of the fresh partition", membership.get("partition") == new_partition,
                json.dumps(membership))

    # The fresh partition is capturable into the catalog like any other.
    def sequence() -> int:
        newest = _try(bench.cluster.get, f"/registry/saves/newest?partition={new_partition}")
        return ((newest.get("SaveId") or {}).get("Sequence") or 0) if isinstance(newest, dict) else 0

    sequence_before = sequence()
    bench.cluster.node(target, f"/save-capture?partition={new_partition}", "POST")
    bench.check("the fresh pod partition capture was catalogued",
                bool(_poll(lambda: sequence() > sequence_before, HANDOVER_TIMEOUT)), f"sequence {sequence_before} -> {sequence()}")

    # WA-1: the pod cooldown must replicate. Vanilla keeps it only in the executing node's map and
    # serves the client's cooldown sync from whatever node the NEXT join lands on - so node-1, which
    # did NOT execute this spawn, must converge to the same nonzero cooldown through the WA merge.
    executing = bench.cluster.node(target, f"/respawn-cooldown?userId={CLIENT_ID}")
    pod_seconds = executing.get("seconds") or 0
    bench.check("the executing node holds a respawn cooldown after the pod spawn", pod_seconds > 0, json.dumps(executing))
    other = _poll(lambda: (lambda c: c if isinstance(c, dict) and (c.get("seconds") or 0) > 0 else None)(
        _try(bench.cluster.node, "node-1", f"/respawn-cooldown?userId={CLIENT_ID}")), HANDOVER_TIMEOUT)
    bench.check("node-1 learned the pod respawn cooldown", other is not None)
    other_seconds = (other or {}).get("seconds") or 0
    bench.check("the replicated cooldown agrees within 15 s", other is not None and abs(pod_seconds - other_seconds) <= 15,
                f"executing={pod_seconds} other={other_seconds}")
    bench.check("the executing node reported the pod respawn cooldown",
                bool(_lines(bench, target, rf"respawn cooldown report client={CLIENT_ID}\b")))

    # WA-3 production cadence: the WA's OWN drop schedule fires with no dispatch knob involved. The
    # test knob only shrinks the vanilla-derived 15-minute per-player interval (self-expiring); the
    # decision, target selection, owner routing and node execution are the production loop's.
    dispatches_before = len(bench.cluster.node("wa-1", "/drop-container-status")["dispatches"])
    grids_before = _drop_grids(bench)
    armed = bench.cluster.node("wa-1", "/drop-test-interval?ms=12000", "POST")
    _require(bench, "the drop-interval test knob armed", armed.get("intervalMs") == 12000, json.dumps(armed))
    drop = {}

    def fired():
        drop["status"] = _try(bench.cluster.node, "wa-1", "/drop-container-status")
        new = (drop["status"].get("dispatches") or [])[dispatches_before:]
        return any(row.get("spawned") is True for row in new)

    fired_ok = _poll(fired, 120, interval=2)
    bench.cluster.node("wa-1", "/drop-test-interval?ms=0", "POST")
    bench.check("the WA's own drop schedule fired with a 12 s interval", bool(fired_ok), json.dumps(drop.get("status"))[:300])
    materialised = _poll(lambda: (lambda n: n if n > grids_before else None)(_drop_grids(bench)), 40)
    bench.check("the scheduled drop container materialised", materialised is not None,
                f"before={grids_before} now={_drop_grids(bench)}")
    bench.record("pod-spawn-drop-cadence", {"automaticDispatches": (drop.get("status") or {}).get("dispatches", [])[dispatches_before:],
                                            "gridsBefore": grids_before, "gridsAfter": materialised})

    # WA-2 forced inventory-full compensation: the store body's inventory-full branch against the
    # client's REAL Survival character inventory (an impossible ore volume). The reservation refunds on
    # the early return - no debit, no credit, the cluster-visible payer balance unchanged.
    payer = 942000000000000000 + random.randrange(32768)
    bench.cluster.node(target, f"/banking-test-create?ownerId={payer}&balance=500", "POST")
    funded = _poll(lambda: (lambda s: s.get("exists") and s.get("balance") == 500)(
        _try(bench.cluster.node, "wa-1", f"/banking-test-status?ownerId={payer}")), HANDOVER_TIMEOUT)
    _require(bench, "the inventory-full payer account converged at the WA", bool(funded))
    identity = _client_identity(bench)
    result = bench.cluster.node(target, f"/banking-test-store-inventory-full?payerId={payer}&payeeId={payer + 1}"
                                        f"&amount=200&identityId={identity}", "POST")
    bench.check("the inventory-full purchase refused and refunded",
                result.get("approved") is True and result.get("inventoryFull") is True
                and result.get("debit") is False and result.get("credit") is False, json.dumps(result))
    payer_after = {}

    def conserved():
        payer_after["last"] = _try(bench.cluster.node, "wa-1", f"/banking-test-status?ownerId={payer}")
        return payer_after["last"].get("balance") == 500

    bench.check("the refunded reservation left the payer balance at 500", bool(_poll(conserved, 30)),
                json.dumps(payer_after.get("last")))
    bench.record("pod-spawn-inventory-full", {"result": result, "payerAfter": payer_after.get("last")})

    # The physical client rode through: one session, no disconnect, alive.
    _no_reconnect(bench, "pod spawn")
    bench.check("the client process is alive", bench.clients[CLIENT_ID].alive())
    bench.check("the client's character is alive", bench.living_character(CLIENT_ID) is not None)
    bench.screenshot(CLIENT_ID, "pod-spawn-after")
    bench.exercised(oldPartition=old_partition, podTarget=target, character=character, podGrid=pod_grid,
                    freshPartition=new_partition, cooldown=[pod_seconds, other_seconds])


# -- P2-WA-06c respawn-chaos ----------------------------------------------------------------


def _points(bench, node_id: str) -> dict:
    points = _try(bench.cluster.node, node_id, "/respawn-points")
    return points if isinstance(points, dict) else {}


def _point(points: dict, entity: int, origin: str, working: bool = False) -> dict | None:
    return next((p for p in points.get("points") or [] if p.get("RespawnEntityId") == entity
                 and p.get("Origin") == origin and (p.get("Working") or not working)), None)


def p2_wa_06c(bench) -> None:
    """WA-6 chaos while a respawn is in flight.

    The WA is SIGKILLed with a cross-node respawn arbitration deterministically held on its game thread
    (the respawn-test-delay knob mirrors the banking chaos knob). The in-flight respawn must fail
    CLEANLY on the source node - no local fallback spawn, no double character, no disconnect - and the
    same respawn must succeed end-to-end once a replacement WA is designated. The client rides through
    the whole outage."""
    api = _join(bench)
    _client_identity(bench)
    nodes = _nodes(bench)

    # A converged node-1 medbay, then make its partition clientless (the client spawned next to it) and
    # move it to node-2 so the respawn is cross-node.
    def converged():
        points = _points(bench, "wa-1")
        mine = [p for p in points.get("points") or [] if p.get("Origin") == "node-1" and p.get("Working")]
        return min(mine, key=lambda p: p["RespawnEntityId"]) if points.get("count", 0) > 0 and mine else None

    point = _poll(converged, HANDOVER_TIMEOUT)
    _require(bench, "a node-1 respawn point converged at the World Authority", point is not None)
    entity, grid, name = point["RespawnEntityId"], point["GridEntityId"], point["Name"]
    character = bench.cluster.node("node-1", f"/respawn-test-player?userId={CLIENT_ID}").get("character") or 0
    _require(bench, "the client has an initial character", character > 0)
    # The lane moves the medbay partition itself. The load balancer postdates it and once migrated a
    # partition to node-2 during the separation (bench run 20260917-055254Z); hold automatic moves.
    bench.cluster.post("/automatic/pause")
    bench.cluster.node("node-1", f"/test-position?entity={character}&relativeTo={grid}&x=15000", "POST")

    def separated():
        rows = _try(_partitions, bench, "node-1")
        rows = rows if isinstance(rows, list) else []
        medbay = (_point(_points(bench, "wa-1"), entity, "node-1") or {}).get("PartitionId")
        client = _client_partition(rows)
        clientless = any(p["id"] == medbay and not p.get("clients") for p in rows)
        return medbay if medbay and client and medbay != client and clientless else None

    medbay_partition = _poll(separated, HANDOVER_TIMEOUT)
    _require(bench, "the medbay partition became clientless after the separation move", medbay_partition is not None)
    bench.record("respawn-chaos-handover", bench.cluster.post(_handover_url(bench, medbay_partition, clientless=True), timeout=300))
    _require(bench, "the respawn registry followed the medbay to node-2",
             bool(_poll(lambda: _point(_points(bench, "wa-1"), entity, "node-2"), HANDOVER_TIMEOUT)))

    # Hold every arbitration for 8 s on the WA game thread (the node-side command timeout is 5 s, so
    # the in-flight arbitration can never sneak to success).
    armed = bench.cluster.node("wa-1", "/respawn-test-delay?ms=8000", "POST")
    _require(bench, "the respawn arbitration delay knob armed", armed.get("delayMs") == 8000, json.dumps(armed))
    session_before = _session(bench) or {}
    # SE1-0122: the respawn screen's preview usually moves the dead client to node-2 before the click, and the
    # held arbitration then fails there instead; the pending retry is the same on either node.
    def node_lines(pattern):
        return _lines(bench, "node-1", pattern) + _lines(bench, "node-2", pattern)

    def client_node():
        backend = (_session(bench) or {}).get("backend")
        return next((n for n, node in nodes.items() if node.backend == backend), "node-1")

    failures_before = len(node_lines(r"remote respawn failed"))
    generation = _authority(bench)["Generation"]

    _kill_player(bench)
    # Click the remote medbay respawn; the arbitration lands in the held window.
    screen = _medical_click(bench, api, name)
    api.control_click(text="Respawn", screen=screen)
    held = _poll(lambda: (_try(bench.cluster.node, "wa-1", "/respawn-test-delay").get("held") or 0) >= 1, 30)
    _require(bench, "the respawn arbitration entered the held window", bool(held))

    stop = threading.Event()
    transport_log = bench.dir / "respawn-chaos-transport.jsonl"

    def poll_transport():
        with transport_log.open("a") as out:
            while not stop.is_set():
                status = _try(bench.cluster.get, "/transport/status")
                session = next((s for s in status.get("sessions") or [] if s.get("client") == CLIENT_ID), None)
                out.write(json.dumps({"t": int(time.time()), "session": session,
                                      "disconnects": status.get("recentDisconnects")}) + "\n")
                out.flush()
                stop.wait(1)

    poller = threading.Thread(target=poll_transport, daemon=True)

    def outage():
        poller.start()
        # The in-flight respawn fails CLEANLY on the source: a logged failure, the request kept PENDING
        # by the node (the stock medical screen cannot retry a refused medbay respawn, so the node owns
        # completion), the player still dead (no local fallback spawn), and the client still connected.
        failed = _poll(lambda: len(node_lines(r"remote respawn failed")) > failures_before
                       and node_lines(r"respawn pending queued"), HANDOVER_TIMEOUT)
        _require(bench, "the in-flight respawn failed into a pending retry on the client's node", bool(failed))
        holder = client_node()
        bench.record("respawn-chaos-pending-node", {"node": holder})
        bench.check(f"{holder} reports the pending respawn", (_points(bench, holder).get("pending") or 0) >= 1)
        time.sleep(5)  # a fallback spawn would show within this window
        source = bench.cluster.node(holder, f"/respawn-test-player?userId={CLIENT_ID}")
        bench.check("the failed respawn left the source player attached and dead",
                    source.get("attached") and source.get("dead") is True, json.dumps(source))
        bench.check("the in-flight respawn failure did not disconnect the client",
                    not _lines(bench, "gateway", rf"Client {CLIENT_ID} disconnected"))
        bench.check("the client process survived the WA outage", bench.clients[CLIENT_ID].alive())

    try:
        _wa_failover(bench, generation, after_kill=outage)
        points = {}

        def recovered():
            points["wa"], points["node1"] = _points(bench, "wa-2"), _points(bench, "node-1")
            return _point(points["wa"], entity, "node-2", working=True) and _point(points["node1"], entity, "node-2", working=True)

        ok = _poll(recovered, HANDOVER_TIMEOUT)
        bench.record("respawn-chaos-recovered-points", points)
        _require(bench, "the moved medbay point recovered on the replacement WA and the node-1 replica", bool(ok))

        # The PENDING respawn completes AUTONOMOUSLY - the node retries the same operation against the
        # replacement WA and finishes the cross-node switch without any further client input.
        final = {}

        def completed():
            final["target"] = _try(bench.cluster.node, "node-2", f"/respawn-test-player?userId={CLIENT_ID}")
            final["session"] = _try(_session, bench) or {}
            target, session = final["target"], final["session"]
            return target.get("attached") and target.get("dead") is False and (target.get("character") or 0) > 0 \
                and session.get("backend") == nodes["node-2"].backend and session.get("admissionState") == "Ready" \
                and session.get("connectedAt") == session_before.get("connectedAt")

        done = _poll(completed, HANDOVER_TIMEOUT)
    finally:
        stop.set()
        if poller.is_alive():
            poller.join(timeout=5)
    _require(bench, "the pending respawn completed autonomously on node-2 with the original session", bool(done),
             json.dumps({**final, "pending": _lines(bench, "node-1", r"respawn pending")[-3:]})[:500])
    bench.check("a node logged the pending respawn completion", bool(node_lines(r"respawn pending completed")))

    # Exactly-once: the character lives on node-2 only; one accepted session, no disconnect, alive.
    source = bench.cluster.node("node-1", f"/respawn-test-player?userId={CLIENT_ID}")
    bench.check("node-1 holds no character after the cross-node respawn",
                source.get("attached") is False or (source.get("character") or 0) == 0, json.dumps(source))
    _no_reconnect(bench, "respawn chaos")
    bench.check("the client process is alive", bench.clients[CLIENT_ID].alive())
    bench.check("the client's character is alive", bench.living_character(CLIENT_ID) is not None)
    bench.screenshot(CLIENT_ID, "respawn-chaos-after")
    bench.record("respawn-chaos-evidence", {"sourcePlayer": source, "targetPlayer": final.get("target"),
                                            "sessionAfter": final.get("session")})
    bench.exercised(medbay=entity, medbayPartition=medbay_partition, authorityGeneration=generation)


# -- P2-REC-01 chaos ------------------------------------------------------------------------


def _balance(bench, node_id: str, owner: int):
    return _try(bench.cluster.node, node_id, f"/banking-test-status?ownerId={owner}").get("balance")


def _command(bench, node_id: str, query: str, timeout: float = 60) -> dict:
    result = _try(bench.cluster.node, node_id, f"/banking-test-command?{query}", "POST", timeout=timeout)
    return result if isinstance(result, dict) else {"error": str(result)}


def _encounters(bench) -> int:
    return sum(bench.cluster.node(node, "/encounter-count")["count"] for node in ("node-1", "node-2"))


def _encounter_executions(bench) -> list[str]:
    """The operation id of every spawning encounter execution on the nodes, one entry per execution
    (a replay answered from the execution record logs nothing). Stdout logs only: the plugin writes
    the line to the console, and the game log would count it twice if it ever carried it."""
    expression = re.compile(r"encounter execute operation=(\S+) .*spawned=True")
    return [match.group(1) for node in ("node-1", "node-2")
            for path in _log_files(bench, node) if path.name.startswith(f"{node}.log")
            for match in map(expression.search, path.read_text(errors="replace").splitlines()) if match]


def p2_rec_01(bench) -> None:
    """P2-REC-01/02 (and P2-WA-02): kill the WA in the middle of a money mutation while continuous
    banking traffic runs from both regular nodes.

    Money is never created or destroyed, refusals are observable during the outage, and an
    uncertain-outcome mutation retried with the SAME operation id applies exactly once across the
    replacement WA (the third send dedupes to the recorded result). The in-flight window is
    deterministic: the held command sleeps inside the authority mutation, serializing later commands
    behind it, so nothing else executes unacknowledged when the kill lands."""
    _join(bench)
    a = 940000000000000000 + random.randrange(32768)
    b = 941000000000000000 + random.randrange(32768)
    for owner in (a, b):
        bench.cluster.node("node-1", f"/banking-test-create?ownerId={owner}&balance=1000", "POST")
    funded = _poll(lambda: all((lambda s: s.get("exists") and s.get("balance") == 1000)(
        _try(bench.cluster.node, "wa-1", f"/banking-test-status?ownerId={owner}")) for owner in (a, b)), HANDOVER_TIMEOUT)
    _require(bench, "the chaos accounts converged at the World Authority", bool(funded))

    # The replacement WA restores banking state from the newest catalogued global save, so capture one
    # that contains the chaos accounts before any killing. Transfers settled after this save
    # legitimately roll back on replacement - conservation still holds because both balances roll back
    # together.
    def global_sequence() -> int:
        newest = _try(bench.cluster.get, "/registry/global-saves/newest")
        return ((newest.get("SaveId") or {}).get("Sequence") or 0) if isinstance(newest, dict) else 0

    global_before = global_sequence()
    bench.cluster.node("wa-1", "/global-save-capture", "POST")
    _require(bench, "the pre-kill global save was catalogued",
             bool(_poll(lambda: global_sequence() > global_before, HANDOVER_TIMEOUT)))

    # WA-3 exactly-once encounter dispatch across the WA kill: a caller-supplied operation id journals
    # the decision through the command ledger, so replaying the SAME id - against this WA or the
    # replacement - must never spawn a second encounter. Dispatched AFTER the global save so the
    # replacement cannot dedupe from a restored ledger: its re-run decision re-POSTs the same operation
    # id and the owning node's execution record answers.
    encounters_before = _encounters(bench)
    executions_before = len(_encounter_executions(bench))
    encounter_op, dispatch = None, None
    for attempt in range(5):
        encounter_op = str(uuid.uuid4())
        answer = _try(bench.cluster.node, "wa-1", f"/encounter-dispatch?client={CLIENT_ID}&operationId={encounter_op}", "POST")
        if answer.get("Spawned") is True:
            dispatch = answer
            break
        if attempt < 4:
            time.sleep(3)
    _require(bench, "the ledgered encounter dispatch was accepted", dispatch is not None,
             json.dumps(_try(bench.cluster.node, "wa-1", "/encounter-status"))[:300])
    spawned = _poll(lambda: _encounters(bench) == encounters_before + 1, HANDOVER_TIMEOUT)
    _require(bench, "the dispatched encounter appeared on exactly one node", bool(spawned),
             f"before={encounters_before} now={_encounters(bench)}")
    prekill = bench.cluster.node("wa-1", f"/encounter-dispatch?client={CLIENT_ID}&operationId={encounter_op}", "POST")
    bench.check("the pre-kill encounter replay returned the recorded result", prekill.get("Spawned") is True, json.dumps(prekill))

    # Continuous 1-credit transfers from both nodes; every attempt is one outcome (ok/refused) so the
    # outage window's refusals are observable and countable.
    stop = threading.Event()
    outcomes: dict[str, list[str]] = {"node-1": [], "node-2": []}

    def stream(node_id: str):
        while not stop.is_set():
            result = _command(bench, node_id, f"from={a}&to={b}&amount=1&operationId={uuid.uuid4()}&timeoutMs=3000", timeout=5)
            outcomes[node_id].append("refused" if "error" in result else "ok")
            stop.wait(0.2)

    streams = [threading.Thread(target=stream, args=(node_id,), daemon=True) for node_id in outcomes]
    for thread in streams:
        thread.start()
    time.sleep(3)  # traffic running before the held mutations

    try:
        # P2-REC mutation-time kill for FACTION mutations: the faction handler has the same chaos delay
        # knob as banking. Hold an AddReputation(+100) in flight - it executes (sleeping) on the WA game
        # thread and the banking held command queues behind it, so BOTH are unacknowledged when the
        # kill lands inside the faction sleep.
        identity = _client_identity(bench)
        faction = bench.cluster.node("wa-1", "/faction-test-status?tag=FCMC").get("factionId") or 0
        _require(bench, "the FCMC faction exists at the World Authority", faction > 0)
        reputation_before = bench.cluster.node("wa-1", f"/faction-test-status?tag=FCMC&identityId={identity}").get("reputation") or 0
        faction_op = str(uuid.uuid4())
        inflight: dict[str, tuple[int, str]] = {}

        def hold(key: str, url: str, timeout: float):
            inflight[key] = _http("POST", url, timeout)

        faction_hold = threading.Thread(target=hold, daemon=True, args=("faction", _node_url(
            bench, "node-1", f"/faction-test-command?identityId={identity}&factionId={faction}&delta=100"
                             f"&operationId={faction_op}&delayMs=7000&timeoutMs=10000"), 15))
        faction_hold.start()
        time.sleep(1)

        # Hold a 100-credit transfer in flight at the authority and kill the WA inside it.
        chaos_op = str(uuid.uuid4())
        generation = _authority(bench)["Generation"]
        banking_hold = threading.Thread(target=hold, daemon=True, args=("banking", _node_url(
            bench, "node-1", f"/banking-test-command?from={a}&to={b}&amount=100&operationId={chaos_op}"
                             f"&delayMs=5000&timeoutMs=8000"), 12))
        banking_hold.start()
        time.sleep(2)

        def outage():
            banking_hold.join(timeout=20)
            faction_hold.join(timeout=20)
            bench.record("chaos-inflight", inflight)
            bench.check("the banking mutation held in flight during the WA kill did not report success",
                        inflight.get("banking", (0, ""))[0] != 200, str(inflight.get("banking"))[:200])
            bench.check("the faction mutation held in flight during the WA kill did not report success",
                        inflight.get("faction", (0, ""))[0] != 200, str(inflight.get("faction"))[:200])

        # Lease fencing and stale-generation observation on both regular nodes.
        def stale_observed(fenced: dict):
            def observed():
                return all((lambda s: s.get("generation") == fenced["Generation"] and s.get("accepts") is False)(
                    _try(bench.cluster.node, node_id, f"/authority-state?generation={generation}"))
                    for node_id in ("node-1", "node-2"))
            bench.check("both regular nodes observe the fenced generation", bool(_poll(observed, HANDOVER_TIMEOUT)))

        _wa_failover(bench, generation, after_kill=outage, after_fence=stale_observed)

        # Let traffic recover, then stop the streams and let balances settle.
        recovered = _poll(lambda: "error" not in _command(
            bench, "node-1", f"from={a}&to={b}&amount=1&operationId={uuid.uuid4()}&timeoutMs=3000", timeout=5), HANDOVER_TIMEOUT)
        _require(bench, "banking commands recovered on the replacement WA", bool(recovered))
    finally:
        stop.set()
        for thread in streams:
            thread.join(timeout=10)
        for node_id, rows in outcomes.items():
            bench.record(f"chaos-stream-{node_id}.txt", "\n".join(rows))
    refusals = sum(rows.count("refused") for rows in outcomes.values())
    successes = sum(rows.count("ok") for rows in outcomes.values())
    bench.check("the outage window produced observable refusals", refusals > 0, f"{refusals} refused, {successes} ok")

    stable = (None, None)
    for _ in range(HANDOVER_TIMEOUT):
        read = (_balance(bench, "wa-2", a), _balance(bench, "wa-2", b))
        if read == stable:
            break
        stable = read
        time.sleep(2)
    stable_a, stable_b = stable
    _require(bench, "money was neither created nor destroyed across the kill",
             isinstance(stable_a, int) and isinstance(stable_b, int) and stable_a + stable_b == 2000, f"{stable_a} + {stable_b}")

    # Exactly-once retry of the killed in-flight mutation: same operation id, the first resend applies
    # it, the second dedupes to the recorded result.
    retry_query = f"from={a}&to={b}&amount=100&operationId={chaos_op}&timeoutMs=10000"
    resend = _command(bench, "node-1", retry_query)
    bench.check("retrying the killed mutation with its operation id succeeded", resend.get("success") is True, json.dumps(resend))
    dedup = _command(bench, "node-1", retry_query)
    bench.check("the deduplicated resend returned the recorded result", dedup.get("success") is True, json.dumps(dedup))
    final_a, final_b = _balance(bench, "wa-2", a), _balance(bench, "wa-2", b)
    bench.check("the retried mutation applied exactly once", final_b == stable_b + 100, f"{stable_b} -> {final_b}")
    bench.check("the retry neither created nor destroyed money", (final_a or 0) + (final_b or 0) == 2000, f"{final_a} + {final_b}")

    # Exactly-once retry of the killed in-flight FACTION mutation: the resend applies the +100 once, the
    # second resend dedupes, and the reputation lands at exactly baseline+100.
    faction_query = (f"/faction-test-command?identityId={identity}&factionId={faction}&delta=100"
                     f"&operationId={faction_op}&timeoutMs=10000")
    faction_resend = _try(bench.cluster.node, "node-1", faction_query, "POST")
    bench.check("retrying the killed faction mutation with its operation id succeeded",
                faction_resend.get("success") is True, json.dumps(faction_resend))
    faction_dedup = _try(bench.cluster.node, "node-1", faction_query, "POST")
    bench.check("the deduplicated faction resend returned the recorded result",
                faction_dedup.get("success") is True, json.dumps(faction_dedup))
    expected = reputation_before + 100
    reputation_after = bench.cluster.node("wa-2", f"/faction-test-status?tag=FCMC&identityId={identity}").get("reputation") or 0
    bench.check("the faction mutation applied exactly once", reputation_after == expected,
                f"baseline={reputation_before} after={reputation_after} expected={expected}")
    replica = _poll(lambda: _try(bench.cluster.node, "node-1", f"/faction-test-status?tag=FCMC&identityId={identity}")
                    .get("reputation") == expected, HANDOVER_TIMEOUT)
    bench.check("the node-1 faction replica converged to the replacement WA reputation", bool(replica))

    # Replica convergence: node-1's banking snapshot reaches the authority balances.
    bench.check("the node-1 banking replica converged to the replacement WA balances",
                bool(_poll(lambda: (_balance(bench, "node-1", a), _balance(bench, "node-1", b)) == (final_a, final_b),
                           HANDOVER_TIMEOUT)))

    # The SAME encounter operation id replayed against the REPLACEMENT WA: its ledger never saw the
    # dispatch (it postdates the restored global save), so the decision re-runs, re-POSTs the same id,
    # and the owning node's execution record replays the outcome - the encounter count must not move.
    replay = _try(bench.cluster.node, "wa-2", f"/encounter-dispatch?client={CLIENT_ID}&operationId={encounter_op}", "POST")
    bench.check("the post-kill encounter replay was exactly-once", replay.get("Spawned") is True, json.dumps(replay))
    time.sleep(5)  # a second spawn would show within this window
    encounters_final = _encounters(bench)
    # The replacement WA's own encounter director may sweep as soon as it is ready and spawn an
    # unrelated encounter under a fresh operation id (bench run 20260917-055254Z, T-0167), so the
    # count is checked against every execution since the snapshot, and the replay by its own id.
    executions = _encounter_executions(bench)[executions_before:]
    bench.check("the dispatched operation executed exactly once across the WA kill",
                executions.count(encounter_op) == 1, f"executions={executions}")
    bench.check("the encounter count moved only by the spawning executions",
                encounters_final == encounters_before + len(executions),
                f"before={encounters_before} final={encounters_final} executions={executions}")
    bench.record("chaos-encounter", {"operation": encounter_op, "dispatch": dispatch, "prekillReplay": prekill,
                                     "replacementReplay": replay, "countBefore": encounters_before, "executions": executions,
                                     "countFinal": encounters_final})

    # P2-REC node-to-WA network partition (no process dies): node-1 drops every global-state frame BOTH
    # ways for 45 s. Its commands refuse while node-2's keep working, the WA expires node-1's
    # liveness-based origins, and once the window lapses node-1 reconverges and the refused mutation
    # retried with the SAME operation id applies exactly once.
    knob = bench.cluster.node("node-1", "/test-wa-partition?ms=45000", "POST")
    _require(bench, "the node-1 WA partition knob armed", knob.get("active") is True, json.dumps(knob))
    partition_op = str(uuid.uuid4())
    partition_query = f"from={a}&to={b}&amount=50&operationId={partition_op}"
    refused = _command(bench, "node-1", partition_query + "&timeoutMs=5000", timeout=8)
    bench.check("a banking command from the partitioned node refused", "error" in refused, json.dumps(refused))
    connected = _command(bench, "node-2", f"from={a}&to={b}&amount=1&operationId={uuid.uuid4()}&timeoutMs=5000", timeout=8)
    bench.check("a banking command from the connected node worked during the partition", "error" not in connected,
                json.dumps(connected))
    bench.check("the WA expired the partitioned node-1 online origin",
                bool(_poll(lambda: _lines(bench, "wa-2", r"online report expired origin=node-1\b"), 60)))
    healed = _poll(lambda: "error" not in _command(
        bench, "node-1", f"from={a}&to={b}&amount=1&operationId={uuid.uuid4()}&timeoutMs=3000", timeout=5), 180, interval=2)
    _require(bench, "node-1 banking commands recovered after the partition window lapsed", bool(healed))
    partition_resend = _command(bench, "node-1", partition_query + "&timeoutMs=10000")
    bench.check("the refused partition mutation retry succeeded", partition_resend.get("success") is True,
                json.dumps(partition_resend))
    partition_dedup = _command(bench, "node-1", partition_query + "&timeoutMs=10000")
    bench.check("the partition mutation dedupe resend succeeded", partition_dedup.get("success") is True,
                json.dumps(partition_dedup))
    balances = {}

    def reconverged():
        balances.update(wa=(_balance(bench, "wa-2", a), _balance(bench, "wa-2", b)),
                        node1=(_balance(bench, "node-1", a), _balance(bench, "node-1", b)))
        pa, pb = balances["wa"]
        return isinstance(pa, int) and isinstance(pb, int) and pa + pb == 2000 and balances["node1"] == balances["wa"]

    bench.check("conservation held and node-1 reconverged after the partition",
                bool(_poll(reconverged, HANDOVER_TIMEOUT)), json.dumps(balances))
    bench.record("chaos-partition", {"resend": partition_resend, "dedup": partition_dedup,
                                     "finalBalances": balances.get("wa"),
                                     "droppedFrames": _try(bench.cluster.node, "node-1", "/test-wa-partition").get("droppedFrames", 0)})

    bench.record("chaos", {"streamSuccesses": successes, "streamRefusals": refusals,
                           "inflight": inflight, "resend": resend, "dedup": dedup,
                           "finalBalances": {"a": final_a, "b": final_b},
                           "faction": {"resend": faction_resend, "dedup": faction_dedup,
                                       "reputationBefore": reputation_before, "reputationAfter": reputation_after}})
    bench.exercised(refusals=refusals, successes=successes, authorityGeneration=generation,
                    balances=[final_a, final_b], encounterOperation=encounter_op)


# -- P2-CHR-04 stale duplicate character ------------------------------------------------------


def p2_chr_04(bench) -> None:
    """T-0169: a stale, uncontrolled copy of a character arriving at the node where a connected client
    controls the live body is dropped, and the handover completes, instead of every attempt aborting
    as a duplicate incarnation (w32 2026-09-17: Pilot-018's old copy, refused every ~3 min).

    The client joins on node-1; the test seam captures its character's object builder; the client's
    partition moves to node-2; the seam re-creates the captured body on node-1 (same entity id, no
    owner) in a partition of its own; that partition is handed to node-2. One incarnation must remain:
    the live one, still controlled, and node-1 must no longer hold the id."""
    _join(bench)
    bench.cluster.post("/automatic/pause")
    entity = bench._server_character(CLIENT_ID)
    _require(bench, "the server reports the client's character", entity is not None)
    client_partition = _client_partition(_partitions(bench, "node-1"))
    _require(bench, "node-1 reports the client partition", client_partition is not None)
    bench.record("duplicate-capture", bench.cluster.node("node-1", f"/duplicate-test?entity={entity}&op=capture", "POST"))

    moved = _try(bench.cluster.post, _handover_url(bench, client_partition), timeout=300)
    bench.record("client-handover", moved)
    _require(bench, "the client partition moved to node-2",
             bool(_poll(lambda: _owned_by(bench, client_partition, "node-2"), HANDOVER_TIMEOUT)), json.dumps(moved)[:300])

    planted: dict = {}

    def plant():
        planted.update(_try(bench.cluster.node, "node-1", f"/duplicate-test?entity={entity}&op=plant", "POST"))
        return planted.get("partition")

    stale = _poll(plant, 60, interval=2)
    bench.record("duplicate-plant", planted)
    _require(bench, "node-1 holds a stale copy of the character in a partition of its own",
             bool(stale) and stale != client_partition, json.dumps(planted)[:300])
    _require(bench, "the registry assigns the stale copy's partition to node-1",
             bool(_poll(lambda: _owned_by(bench, stale, "node-1"), 60)))

    duplicates_before = _game_count(bench, "node-2", r"Duplicate incarnation")
    handover = _try(bench.cluster.post, _handover_url(bench, stale, clientless=True), timeout=300)
    bench.record("stale-handover", handover)
    bench.check("the stale copy's handover completes", "error" not in handover, json.dumps(handover)[:300])
    bench.check("node-2 dropped the arriving stale copy",
                bool(_poll(lambda: _lines(bench, "node-2", rf"import dropped stale duplicate of live client-controlled character {entity}\b"), 30)))
    bench.check("node-2 raised no duplicate incarnation", _game_count(bench, "node-2", r"Duplicate incarnation") == duplicates_before)
    # The commit moves the live body into the arriving partition, and the next pass usually merges it
    # straight back into the client's partition (the two are metres apart): retired is as good as owned.
    def settled():
        row = next((r for r in _owners(bench) if r["Partition"] == stale), None)
        return row is not None and (row.get("Retired") or row.get("Node") == "node-2")

    bench.check("the stale copy's partition left node-1 (owned by node-2 or retired)",
                bool(_poll(settled, 60)),
                json.dumps(next((r for r in _owners(bench) if r["Partition"] == stale), None)))
    node1 = _poll(lambda: _try(bench.cluster.node, "node-1", f"/partition-of?entity={entity}").get("partition") == 0, 30)
    bench.check("node-1 no longer holds the character", bool(node1))
    held = _try(bench.cluster.node, "node-2", f"/partition-of?entity={entity}")
    bench.check("node-2 holds the character", bool(held.get("partition")), json.dumps(held))
    bench.check("the client still controls the same character", bench._server_character(CLIENT_ID) == entity,
                f"{entity} -> {bench._server_character(CLIENT_ID)}")
    _no_reconnect(bench, "stale duplicate")
    bench.exercised(character=entity, clientPartition=client_partition, stalePartition=stale)



# -- P2-DROP-REMOVAL drop removal across a handover (#1391) ----------------------------------------


DROP_DEADLINE = 120  # seconds the lane leaves on the drop before its handover


def _drop_removal(bench, carried: bool) -> None:
    """A drop container's vanilla removal deadline travels with the grid (#1391). Vanilla registers the
    15-minute removal on the server that spawned the drop and, at the deadline, closes the grid only if
    that server still holds it. A drop handed over to another node never despawned. The lane spawns a
    real drop through the WA director, sets its deadline to DROP_DEADLINE s on the spawning node, hands
    its partition to the other node and waits: the target's own vanilla removal must close it at the
    carried deadline, with no knob on the target. CARRIED false (CLUSTER_DROP_REMOVAL_HANDOVER=0) is
    the red run: the target holds no deadline and the drop outlives it."""
    if not carried:
        bench.cluster.config.node_env["CLUSTER_DROP_REMOVAL_HANDOVER"] = "0"
    bench.cluster.config.world_template = _pod_world(bench)
    _join(bench)
    identity = _client_identity(bench)
    mapped = _poll(lambda: "error" not in _try(bench.cluster.get, f"/registry/client-routes?client={CLIENT_ID}"),
                   HANDOVER_TIMEOUT)
    _require(bench, "the registry mapped the client before the drop", bool(mapped))

    def grids(node: str) -> set | None:
        answer = _try(bench.cluster.node, node, "/drop-container-grids")
        return None if "error" in answer else set(answer.get("grids") or [])

    def entry(node: str) -> dict | None:
        rows = _try(bench.cluster.node, node, "/drop-container-removals").get("removals") or []
        return next((row for row in rows if row.get("grid") == grid), None)

    before = {node: grids(node) or set() for node in ("node-1", "node-2")}
    dispatch = bench.cluster.node("wa-1", f"/drop-container-dispatch?identity={identity}", "POST")
    bench.record("drop-removal-dispatch", dispatch)
    _require(bench, "the WA drop container dispatch was accepted", dispatch.get("Spawned") is True, json.dumps(dispatch))
    found: dict = {}

    def spawned() -> bool:
        for node in ("node-1", "node-2"):
            new = (grids(node) or set()) - before[node]
            if new:
                found.update(node=node, grid=min(new))
                return True
        return False

    _require(bench, "the drop container materialised on a regular node", bool(_poll(spawned, 40)))
    source, grid = found["node"], found["grid"]
    target = "node-2" if source == "node-1" else "node-1"
    initial = _poll(lambda: entry(source), 30)
    _require(bench, "the spawning node holds vanilla's removal entry for the drop",
             initial is not None and 0 < initial["seconds"] <= 900, json.dumps(initial))
    bench.cluster.node(source, f"/drop-test-deadline?grid={grid}&seconds={DROP_DEADLINE}", "POST")
    armed = time.time()

    # The move itself is the production handover; the drop may share the client's partition.
    partition = bench.cluster.node(source, f"/partition-of?entity={grid}").get("partition")
    _require(bench, "the drop grid is in a partition", bool(partition))
    clientless = partition != _client_partition(_partitions(bench, source))
    bench.record("drop-removal-handover", bench.cluster.post(
        _handover_url(bench, partition, clientless=clientless, source=source, target=target), timeout=300))
    def held(node: str):
        return _try(bench.cluster.node, node, f"/partition-of?entity={grid}").get("partition")

    moved = _poll(lambda: _owned_by(bench, partition, target) and held(target), HANDOVER_TIMEOUT)
    views = {node: {"partition": held(node), "dropGrids": sorted(grids(node) or [])} for node in (source, target)}
    bench.record("drop-removal-moved", views)
    _require(bench, f"the drop grid's partition moved from {source} to {target}", bool(moved), json.dumps(views))
    bench.check(f"{target} holds the drop container component", grid in (grids(target) or set()), json.dumps(views))
    arrived = _poll(lambda: entry(target), 10) if carried else entry(target)
    left = time.time() - armed
    bench.record("drop-removal-entries", {"source": entry(source), "target": arrived, "elapsed": left, "initial": initial})
    if carried:
        bench.check("the source dropped its removal entry with the grid", entry(source) is None)
        bench.check("the target holds the deadline the drop carried, not a restarted one",
                    arrived is not None and DROP_DEADLINE - left - 15 <= arrived["seconds"] <= DROP_DEADLINE,
                    f"entry={arrived} elapsed={left:.0f}s")
    else:
        bench.check("red: the source kept its entry for a grid it no longer holds", entry(source) is not None)
        bench.check("red: the target holds no carried deadline", arrived is None or arrived["seconds"] > DROP_DEADLINE,
                    f"entry={arrived}")

    def closed() -> bool:
        rows = [_try(bench.cluster.node, node, f"/partition-of?entity={grid}") for node in (target, source)]
        listed = grids(target)
        return all("error" not in row and not row.get("partition") for row in rows) and listed is not None and grid not in listed

    wait = max(0.0, DROP_DEADLINE + 45 - (time.time() - armed))
    gone = _poll(closed, wait, interval=3)
    despawned_after = time.time() - armed
    bench.record("drop-removal-outcome", {"gone": bool(gone), "secondsAfterArm": despawned_after,
                                          "partitionOf": {n: _try(bench.cluster.node, n, f"/partition-of?entity={grid}") for n in (source, target)},
                                          "target": entry(target), "grids": {n: sorted(grids(n) or []) for n in (source, target)}})
    if carried:
        bench.check(f"the drop despawned on {target} at its carried deadline", bool(gone), f"{despawned_after:.0f}s after arming")
        bench.check("not before the deadline", despawned_after >= DROP_DEADLINE - 10, f"{despawned_after:.0f}s")
    else:
        bench.check("red: the drop outlived its deadline on the target", not gone, f"{despawned_after:.0f}s after arming")
    _no_reconnect(bench, "drop removal")
    bench.exercised(grid=grid, source=source, target=target, partition=partition, clientless=clientless)


def p2_drop_removal(bench) -> None:
    """#1391: a handed-over drop container despawns at its vanilla deadline on the node that holds it."""
    _drop_removal(bench, carried=True)


def p2_drop_removal_red(bench) -> None:
    """#1391 falsifier: the same with CLUSTER_DROP_REMOVAL_HANDOVER=0 - the drop outlives its deadline."""
    _drop_removal(bench, carried=False)


# -- P2-DROP-CAP the cluster-wide drop cap (#1443) --------------------------------------------------


DROP_INTERVAL_MS = 12000  # the WA's per-player drop interval in this lane (production: 15 min)
DROP_FAR = (600_000.0, 0.0, 0.0)  # where the lane parks a drop alone, so its partition can go offline


def _census_drops(bench) -> dict[int, dict]:
    """The registry census's drop rows by grid id; none before its first complete report pass (503)."""
    try:
        census = bench.cluster.get("/registry/npc-census")
    except ClusterError:
        return {}
    return {int(row["id"]): row for row in census.get("grids") or [] if row.get("d") is True}


def _held_drops(bench) -> dict[int, str]:
    """Drop grid id -> the node holding it, from the nodes' own entity walk (independent of the census)."""
    held = {}
    for node in ("node-1", "node-2"):
        for grid in _try(bench.cluster.node, node, "/drop-container-grids").get("grids") or []:
            held[int(grid)] = node
    return held


def _cap_status(bench) -> dict:
    return _try(bench.cluster.node, "wa-1", "/drop-container-status").get("cap") or {}


def _watch_drops(bench, label: str, seconds: float, until=None) -> dict:
    """Samples every 2 s: the drops online on the nodes plus the census's offline drops not past their deadline. The
    count is the lane's own, so it can catch the WA's count being wrong."""
    samples = []
    end = time.time() + seconds
    while time.time() < end:
        online = _held_drops(bench)
        now = time.time()
        offline = {grid for grid, row in _census_drops(bench).items()
                   if row.get("o") and not (row.get("e") and row["e"] <= now) and grid not in online}
        samples.append({"t": round(now), "online": sorted(online), "offline": sorted(offline), "wa": _cap_status(bench)})
        if until is not None and until(samples[-1]):
            break
        time.sleep(2)
    result = {"max": max(len(s["online"]) + len(s["offline"]) for s in samples),
              "waMax": max(s["wa"].get("active", -1) for s in samples),
              "ids": set().union(*(set(s["online"]) | set(s["offline"]) for s in samples)), "last": samples[-1], "samples": len(samples)}
    bench.record(f"drop-cap-{label}", {**result, "ids": sorted(result["ids"]), "first": samples[0]})
    return result


def p2_drop_cap(bench) -> None:
    """#1443: the World Authority keeps at most drops.maxActive container drops active cluster-wide. The lane shrinks the
    WA's drop interval to 12 s for its one player, so without a cap a new drop would spawn every 12 s. With the cap at 1
    the WA spawns one and skips the rest; a drop that despawns is replaced; raised to 3 live, three are kept; a drop alone
    in an offline partition still counts until its vanilla deadline, then a new one replaces it, and when its partition
    loads it leaves the world at that deadline; with the cap at 0 (off) the drops pile up past 3 (the known positive of
    the lane's own counter)."""
    bench.cluster.config.world_template = _pod_world(bench)
    _join(bench)
    for node in ("wa-1", "node-1"):
        selftest = bench.cluster.node(node, "/self-test?section=drop-cap")
        bench.check(f"{node} self-test drop-cap (census drop rows, tally, deadline, hold, pending, knob)",
                    selftest.get("ok") is True and selftest.get("ran") == ["drop-cap"], json.dumps(selftest)[:300])
    # The #1379 sweep removes ownerless grids after 5 min unvisited, and the lane's drops must stay until it removes them.
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false",
                                                                 "drops.maxActive": "1"}))

    def arm() -> None:  # the knob expires on its own after 10 min, so each phase re-arms it
        armed = bench.cluster.node("wa-1", f"/drop-test-interval?ms={DROP_INTERVAL_MS}", "POST")
        _require(bench, "the drop-interval test knob armed", armed.get("intervalMs") == DROP_INTERVAL_MS, json.dumps(armed))

    # 1. Cap 1: one drop, every later due drop skipped.
    arm()
    one = _watch_drops(bench, "cap1", 90)
    status = _cap_status(bench)
    bench.check("cap 1: a drop spawned", len(one["ids"]) >= 1, json.dumps(one["last"]))
    bench.check("cap 1: never more than 1 drop active (lane count and the WA's own)", one["max"] <= 1 and one["waMax"] <= 1, f"lane {one['max']}, WA {one['waMax']}")
    bench.check("cap 1: the WA counts 1 active and skipped the due drops (12 s interval, 90 s)",
                status.get("active") == 1 and status.get("cap") == 1 and status.get("skippedAtCap", 0) >= 3, json.dumps(status))
    bench.check("cap 1: the WA logged the skips", bool(_lines(bench, "wa-1", r"drop_container cap active=1 cap=1 .* skipped=[1-9]")))

    # 2. Replacement: the drop despawns (its vanilla deadline moved to 3 s) and a new one takes its place.
    held = _held_drops(bench)
    _require(bench, "cap 1: one drop held on a node", len(held) == 1, json.dumps(held))
    old, holder = next(iter(held.items()))
    bench.cluster.node(holder, f"/drop-test-deadline?grid={old}&seconds=3", "POST")
    arm()
    replaced = _watch_drops(bench, "replaced", 150, until=lambda s: bool(set(s["online"]) - {old}))
    bench.check("cap 1: a new drop replaced the despawned one", bool(set(replaced["last"]["online"]) - {old}),
                json.dumps(replaced["last"]))
    bench.check("cap 1: never more than 1 drop active while it was replaced (lane and WA)", replaced["max"] <= 1 and replaced["waMax"] <= 1, f"lane {replaced['max']}, WA {replaced['waMax']}")

    # 3. The knob is live: raised to 3, three drops, never a fourth.
    bench.record("config-cap3", bench.cluster.handover_config({"drops.maxActive": "3"}))
    arm()
    three = _watch_drops(bench, "cap3", 180, until=lambda s: len(s["online"]) + len(s["offline"]) == 3)
    tail = _watch_drops(bench, "cap3-hold", 60)
    bench.check("cap 3: three drops active", len(three["last"]["online"]) + len(three["last"]["offline"]) == 3,
                json.dumps(three["last"]))
    bench.check("cap 3: never more than 3 drops active (lane and WA)", max(three["max"], tail["max"]) <= 3 and max(three["waMax"], tail["waMax"]) <= 3, f"lane {max(three['max'], tail['max'])}, WA {max(three['waMax'], tail['waMax'])}")

    # 4. Offline: a drop alone far away goes offline with a 150 s deadline. It still counts; past the deadline it does not
    # and a new drop replaces it; loaded again, it leaves the world at once (the census carried its deadline).
    held = _held_drops(bench)
    parked, holder = next(iter(held.items()))
    moved = bench.cluster.node(holder, f"/grid-move?entityId={parked}&x={DROP_FAR[0]}&y={DROP_FAR[1]}&z={DROP_FAR[2]}", "POST")
    _require(bench, "a drop moved away to a partition of its own", moved.get("moved") is True, json.dumps(moved))
    bench.cluster.node(holder, f"/drop-test-deadline?grid={parked}&seconds=150", "POST")
    deadline = time.time() + 150
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    arm()
    went = _poll(lambda: (_census_drops(bench).get(parked) or {}).get("o") is True, 120, interval=2)
    _require(bench, "the parked drop's partition went offline, the census lists it as offline with its deadline",
             bool(went) and abs((_census_drops(bench).get(parked) or {}).get("e", 0) - deadline) <= 15,
             json.dumps(_census_drops(bench).get(parked)))
    counted = _watch_drops(bench, "offline-counts", max(10.0, deadline - time.time() - 10))
    status = _cap_status(bench)
    # Other drops with no player near may go offline too; they count the same way.
    bench.check("offline: the WA counted the offline drop until its deadline (offline >= 1, active 3, no new drop)",
                status.get("offline", 0) >= 1 and status.get("active") == 3 and counted["ids"] <= three["ids"] | tail["ids"],
                json.dumps(status))
    bench.check("offline: never more than 3 drops active, the offline one included (lane and WA)", counted["max"] <= 3 and counted["waMax"] <= 3, f"lane {counted['max']}, WA {counted['waMax']}")
    before = counted["ids"] | three["ids"] | tail["ids"]
    after = _watch_drops(bench, "offline-expired", 120, until=lambda s: bool(set(s["online"]) - before))
    bench.check("offline: past its deadline the offline drop no longer counts and a new drop replaced it",
                bool(set(after["last"]["online"]) - before), json.dumps(after["last"]))
    bench.check("offline: never more than 3 drops active (lane and WA)", after["max"] <= 3 and after["waMax"] <= 3, f"lane {after['max']}, WA {after['waMax']}")
    bench.record("config-offline-off", bench.cluster.handover_config({"offline.enabled": "false"}))
    partition = next((row["Partition"] for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])
                      if parked in (row.get("Members") or [])), None)
    _require(bench, "the parked drop's offline partition is in the offline table", partition is not None)
    bench.record("load-parked", bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node=node-1&reason=tm-1443"))
    gone = _poll(lambda: _lines(bench, "node-1", rf"drop_container removal registered grid={parked} .*reason=census-deadline")
                 and parked not in _held_drops(bench), 180, interval=3)
    bench.check("offline: loaded again, the expired drop got its census deadline and left the world", bool(gone),
                json.dumps(_lines(bench, "node-1", rf"drop_container removal registered grid={parked}")[-3:]))

    # 5. Cap 0 = no cap: the drops pile up past 3 (the lane's counter sees an excess when there is one).
    bench.record("config-cap0", bench.cluster.handover_config({"drops.maxActive": "0"}))
    arm()
    uncapped = _watch_drops(bench, "cap0", 120, until=lambda s: len(s["online"]) + len(s["offline"]) >= 5)
    bench.check("cap 0 (off): more than 3 drops active", uncapped["max"] > 3, f"max {uncapped['max']}")
    bench.cluster.node("wa-1", "/drop-test-interval?ms=0", "POST")
    _no_reconnect(bench, "drop cap")
    bench.exercised(replaced=old, parked=parked, partition=partition, finalStatus=_cap_status(bench))


SCENARIOS_WA_OPS = {
    "P2-WA-04": (p2_wa_04, "antenna relay chain, remote terminal, on-demand pull", "3"),
    "P2-WA-06b": (p2_wa_06b, "cross-node pod respawn with least-loaded placement", "3"),
    "P2-WA-06c": (p2_wa_06c, "WA killed holding a cross-node respawn arbitration", "4"),
    "P2-DROP-REMOVAL": (p2_drop_removal, "#1391: a handed-over drop container despawns at its carried deadline", "3"),
    "P2-DROP-REMOVAL-RED": (p2_drop_removal_red, "#1391: the same with CLUSTER_DROP_REMOVAL_HANDOVER=0 outlives it", "3"),
    "P2-DROP-CAP": (p2_drop_cap, "#1443: at most drops.maxActive container drops active cluster-wide, offline ones included", "3"),
    # P2-WA-02 (concurrent cross-node mutations never overdraw; balances converge) is asserted here.
    "P2-REC-01": (p2_rec_01, "WA killed inside a held mutation; queue replay conserves", "4"),
    "P2-CHR-04": (p2_chr_04, "stale duplicate character arriving at the live body's node (T-0169)", "3"),
}
