"""T-0405 bench: a direct-transport client that quits the game closes its Gateway session at once.

Pulsar replaces every game exit with Process.Kill(), so before the direct-transport fix (df 5232059, ExitGoodbyePatch)
a quitting DT client sent nothing and the Gateway held its session - and the player's body - until the transport
timeout ('Client <id> disconnected: Timeout'). Live r121h: 79 of 80 harness DT disconnects were Timeout.

Two regular nodes + WA, ONE headless client:
  1. the client spawns; its Gateway session and its server-side character are recorded.
  2. the client quits the way a player does: the Remote plugin's exit_game -> MySandboxGame.ExitThreadSafe, which
     Pulsar turns into Process.Kill(). The bench does NOT kill the process itself until the game is gone on its own.
  3. checks, timed from the exit_game call:
       (a) the game process ended by itself (Pulsar's kill) within 30 s - else the run is VOID, not red or green
       (b) the Gateway's client list drops the client within GONE_S (default 5 s)
       (c) the server releases the character: no partition lists the client within GONE_S + 5 s
       (d) the Gateway's 'Client <id> disconnected: <reason>' line names a reason other than Timeout
       (e) the client's DirectTransport log line 'Goodbye (game exit|process exit): sent Disconnect to 1 peer(s)'
T0405_QUIT=sigterm sends a TERM instead: a KNOWN NEGATIVE. AppDomain.ProcessExit is not raised for a TERM on this
runtime (bench 2026-10-08 17:27), so a TERM-ed client sends nothing and leaves the ghost; the fix covers quits that
reach MySandboxGame.ExitThreadSafe.
T0405_EXPECT=red (a package whose client takes direct-transport WITHOUT the fix, e.g. DIRECT_TRANSPORT_SOURCE=hub
before the pin moves) inverts (b) and (d): the session must still be there after 20 s and the reason must be Timeout;
(e) must be absent. That is the known positive for the measurement: it shows the bench can see the ghost at all.

Invocation: cd <tree>/Test && CLUSTER_PACKAGE=<pkg> DIRECT_TRANSPORT_SOURCE=<checkout|hub> PYTHONPATH=$PWD \\
            uv run python bench_t0405.py <label>
"""
import json
import os
import re
import signal
import sys
import time
from pathlib import Path

from bench import run_scenario
from cluster import ClusterError, wait_until
from scenarios import CLIENT2_ID, _all_partitions
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _lines, _partitions

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
RED = os.environ.get("T0405_EXPECT", "green").lower() == "red"
OUT = Path(os.environ.get("T0405_OUT", "/home/factory/cluster-runs/regression/tools/t0405")) / f"bench-{LABEL}"
OUT.mkdir(parents=True, exist_ok=True)
GONE_S = float(os.environ.get("T0405_GONE_S", "5"))
#: How the client quits: exit_game (the Remote plugin -> ExitThreadSafe, the path of the menu's Exit and of a window
#: close / Alt-F4: Window.OnManualWindowCloseRequest -> CloseManually -> Window.OnExit -> MySandboxGame.OnExit ->
#: ExitThreadSafe) or sigterm (a TERM to the game process: NOT covered, see the module docstring).
QUIT = os.environ.get("T0405_QUIT", "exit_game")
RED_HOLD_S = 20.0
#: The CLIENT half of direct-transport comes from the PluginHub pin in every bench client (DIRECT_TRANSPORT_SOURCE
#: reaches the nodes only). T0405_DT_CLIENT=<checkout> makes THIS scenario's client compile that checkout instead:
#: after Client.prepare() (which refreshes the profile on every start) the client's own Pulsar copy gets a
#: LocalPlugin source for the checkout and its profile swaps the GitHub direct-transport entry for a DevFolder one.
#: The host's Pulsar profile (the template) is never touched.
DT_CLIENT = os.environ.get("T0405_DT_CLIENT")
#: T0405_SMOKE=1 runs a short smoke before the quit (CH 2026-10-08 17:35, DT release 1.1.1): a second client joins
#: through the Gateway, the first client's partition is handed over SMOKE_HANDOVERS times between node-1 and node-2,
#: and the Gateway/node logs must show >= 5 commits, no abort and zero residuals. Both client logs must show the
#: lobby mode (DT 1.1.0, #10) inactive. The second client stays connected through the quit and must survive it.
SMOKE = os.environ.get("T0405_SMOKE") == "1"
SMOKE_HANDOVERS = 6


def use_dev_transport(pulsar_data: Path, checkout: str) -> None:
    sources = pulsar_data / "Sources" / "sources.xml"
    text = sources.read_text()
    # The client's home persists between runs: an entry left by an earlier run must be repointed, or Pulsar keeps
    # loading that run's cached build (bench 2026-10-08 17:45, red-8e5 ran the 084aee8 client).
    text = re.sub(r"(<Name>direct-transport</Name>\s*<Folder>)[^<]*(</Folder>)", lambda m: m.group(1) + checkout + m.group(2),
                  text)
    if "<Name>direct-transport</Name>" not in text:
        entry = ("    <LocalPlugin>\n      <Name>direct-transport</Name>\n      <Folder>" + checkout + "</Folder>\n"
                 "      <File>DirectTransportClient.xml</File>\n      <Enabled>true</Enabled>\n    </LocalPlugin>\n")
        text = (text.replace("<LocalPluginSources>\n", "<LocalPluginSources>\n" + entry, 1)
                if "<LocalPluginSources>\n" in text
                else text.replace("<LocalPluginSources />", "<LocalPluginSources>\n" + entry + "  </LocalPluginSources>", 1))
    sources.write_text(text)
    for profile in (pulsar_data / "Profiles").glob("*.xml"):
        xml = profile.read_text()
        xml = re.sub(r"\s*<GitHubPluginConfig>\s*<Id>direct-transport</Id>\s*</GitHubPluginConfig>", "", xml)
        if "<Id>direct-transport</Id>" not in xml:
            dev = ("    <LocalFolderConfig>\n      <Id>direct-transport</Id>\n      <DebugBuild>false</DebugBuild>\n"
                   "    </LocalFolderConfig>\n")
            xml = (xml.replace("<DevFolder>\n", "<DevFolder>\n" + dev, 1) if "<DevFolder>\n" in xml
                   else xml.replace("<DevFolder />", "<DevFolder>\n" + dev + "  </DevFolder>", 1))
        profile.write_text(xml)


if DT_CLIENT:
    import clients as _clients
    _prepare = _clients.Client.prepare

    def _prepare_with_dev_transport(self) -> None:
        _prepare(self)
        use_dev_transport(self.pulsar_data, DT_CLIENT)

    _clients.Client.prepare = _prepare_with_dev_transport


def poll(predicate, timeout: float, interval: float = 0.25):
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def in_gateway(bench) -> bool:
    return CLIENT_ID in [session["clientId"] for session in bench.cluster.get("/diag?depth=cluster")["clients"]]


def disconnect_reasons(bench) -> list[str]:
    pattern = rf"Client {CLIENT_ID} disconnected: (.+)$"
    return [re.search(pattern, line).group(1).strip() for name in ("gateway", "registry", "cluster-registry")
            for line in _lines(bench, name, pattern)]


def goodbye_lines(client) -> list[str]:
    return [line for path in client.game_logs() for line in path.read_text(errors="replace").splitlines()
            if "Goodbye (" in line]


def lobby_lines(client) -> list[str]:
    return [line for path in client.game_logs() for line in path.read_text(errors="replace").splitlines()
            if "DirectTransport:" in line and "obby" in line]


def smoke(bench) -> None:
    bench.start_client(CLIENT2_ID, remote_port=24160)
    bench.wait_spawned(CLIENT2_ID)
    sessions = lambda: {s["clientId"] for s in bench.cluster.get("/diag?depth=cluster")["clients"]}  # noqa: E731
    require(bench, "smoke: both clients hold a Gateway session",
            poll(lambda: {CLIENT_ID, CLIENT2_ID} <= sessions(), 60), str(sessions()))
    committed_pattern = r"handover transferId=\S+ partition=\d+ state=committed"
    aborted_pattern = r"handover transferId=\S+ partition=\d+ state=aborted"
    check_failed = "Automatic handover check failed"
    before = {pattern: len(_lines(bench, "gateway", pattern)) for pattern in (committed_pattern, aborted_pattern, check_failed)}
    verified_before = {node: len(_lines(bench, node, r"handover state verified")) for node in ("node-1", "node-2")}
    moves = []
    for index in range(SMOKE_HANDOVERS):
        owners = _all_partitions(bench)
        source = next((node for node in ("node-1", "node-2")
                       if _client_partition(_partitions(bench, node)) is not None), None)
        if source is None:
            moves.append({"index": index, "error": "no node lists the client's partition"})
            break
        partition = _client_partition(_partitions(bench, source))
        target = "node-2" if source == "node-1" else "node-1"
        count_before = len(_lines(bench, "gateway", committed_pattern))
        started = time.time()
        try:
            bench.cluster.post(_handover_url(bench, partition, source=source, target=target, client=CLIENT_ID),
                               timeout=300)
            error = None
        except ClusterError as failure:
            error = str(failure)[:200]
        arrived = poll(lambda: _all_partitions(bench).get(partition) == target
                       and len(_lines(bench, "gateway", committed_pattern)) > count_before, 90, 0.5)
        moves.append({"index": index, "partition": partition, "from": source, "to": target, "owner": owners.get(partition),
                      "committed": arrived is not None, "seconds": round(time.time() - started, 2), "error": error})
        print(f"  [INFO] smoke handover {index + 1}: {moves[-1]}")
        time.sleep(5)
    bench.record("smoke-handovers", moves)
    committed = len(_lines(bench, "gateway", committed_pattern)) - before[committed_pattern]
    aborted = len(_lines(bench, "gateway", aborted_pattern)) - before[aborted_pattern]
    failed = len(_lines(bench, "gateway", check_failed)) - before[check_failed]
    verified = [line for node in ("node-1", "node-2")
                for line in _lines(bench, node, r"handover state verified")[verified_before[node]:]]
    residuals = [int(m.group(1)) for line in verified if (m := re.search(r"residuals=(\d+)", line))]
    bench.check(f"smoke (1): >= 5 handovers committed through the Gateway", committed >= 5,
                f"{committed} committed of {len(moves)} requested")
    bench.check("smoke (2): no aborted transfer and no failed automatic check", aborted == 0 and failed == 0,
                f"aborted={aborted} checkFailed={failed}")
    bench.check("smoke (3): every verified handover has zero residuals", bool(residuals) and not any(residuals),
                f"{len(residuals)} verified, residuals={residuals}")
    lobby = {client_id: lobby_lines(bench.clients[client_id]) for client_id in (CLIENT_ID, CLIENT2_ID)}
    bench.check("smoke (4): the lobby mode stays inactive on both joining clients (no lobby line)",
                not any(lobby.values()), str(lobby)[:300])
    bench.record("smoke-summary", {"committed": committed, "aborted": aborted, "checkFailed": failed,
                                   "residuals": residuals, "lobbyLines": lobby})


def check(bench) -> None:
    bench.start_cluster()
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    client = bench.clients[CLIENT_ID]
    character = poll(lambda: bench._server_character(CLIENT_ID), 60)
    require(bench, "setup: the server holds the client's character", character, str(character))
    require(bench, "setup: the Gateway lists the client's session", poll(lambda: in_gateway(bench), 30))
    if SMOKE:
        smoke(bench)
    reasons_before = disconnect_reasons(bench)
    goodbye_before = len(goodbye_lines(client))

    started = time.time()
    if QUIT == "sigterm":
        os.kill(client.process.pid, signal.SIGTERM)
    else:
        try:
            api.exit_game()
        except Exception:  # noqa: BLE001 - the process may die before the HTTP reply; that is the point
            pass
    exited = poll(lambda: not client.alive(), 30)
    exit_s = round(time.time() - started, 2)
    bench.apis.pop(CLIENT_ID, None)
    require(bench, f"(a) the game process ended by itself after {QUIT} (VOID otherwise) - {exit_s} s", exited,
            "still alive after 30 s: the quit never reached the game's exit")

    if RED:
        held = poll(lambda: not in_gateway(bench), RED_HOLD_S)
        gone_s = None if held is None else round(time.time() - started, 2)
        bench.check(f"(b-red) the Gateway still lists the session {RED_HOLD_S:.0f} s after the quit (the ghost)",
                    held is None, f"dropped after {gone_s} s")
    else:
        dropped = poll(lambda: not in_gateway(bench), GONE_S)
        gone_s = None if dropped is None else round(time.time() - started, 2)
        bench.check(f"(b) the Gateway drops the session within {GONE_S:.0f} s of the quit", dropped is not None,
                    f"dropped after {gone_s} s")
    released = poll(lambda: bench._server_character(CLIENT_ID) is None, GONE_S + 5 if not RED else 5)
    released_s = None if released is None else round(time.time() - started, 2)
    if not RED:
        bench.check(f"(c) the server releases the character within {GONE_S + 5:.0f} s", released is not None,
                    f"released after {released_s} s")

    # The reason line: in the red arm it only appears at the timeout, so wait for it there (bounded).
    reasons = poll(lambda: disconnect_reasons(bench)[len(reasons_before):] or None, 15 if not RED else 200, 1.0) or []
    reason = reasons[0] if reasons else None
    if RED:
        bench.check("(d-red) the Gateway's disconnect reason is Timeout", reason is not None and "Timeout" in reason,
                    str(reasons))
    else:
        bench.check("(d) the Gateway's disconnect reason is not Timeout", reason is not None and "Timeout" not in reason,
                    str(reasons))
    goodbye = goodbye_lines(client)[goodbye_before:]
    if SMOKE:
        bench.check("smoke (5): the second client keeps its Gateway session through the first one's quit",
                    CLIENT2_ID in [s["clientId"] for s in bench.cluster.get("/diag?depth=cluster")["clients"]])
    if RED:
        bench.check("(e-red) no client Goodbye line (the client has no fix)", not goodbye, str(goodbye[:2]))
    else:
        bench.check("(e) the client logged its Goodbye", any("to 1 peer(s)" in line for line in goodbye), str(goodbye[:2]))
    bench.kill_client(CLIENT_ID)
    if SMOKE:
        bench.kill_client(CLIENT2_ID)
    bench.exercised(expect="red" if RED else "green", quit=QUIT, smoke=SMOKE, dtClient=DT_CLIENT or "hub", exitS=exit_s, sessionGoneS=gone_s, characterReleasedS=released_s,
                    reason=reason, goodbye=goodbye[:1], character=character)


check.regular_nodes = 2
result = run_scenario(check, "T0405-dt-goodbye", f"a direct-transport client that quits ({QUIT}) closes its session at once"
                      + (" (RED baseline: no fix)" if RED else ""), "3", OUT)
(OUT / "verdict.json").write_text(json.dumps(vars(result), default=lambda x: vars(x), indent=2))
raise SystemExit(0 if result.passed else 1)
