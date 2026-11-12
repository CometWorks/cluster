"""World Authority: the former run-cluster.sh lane `world-authority` (lines 782-3154), as native
scenarios.

The shell lane was one ~2,370-line story on one cluster (node-1, node-2, wa-1, Cluster-Simple
`to-split`) with the Gateway's automatic co-location paused (the lane engineers co-located
cross-node partitions - a remote medbay, IGC hosts - that §7.1 would immediately undo). Here it is
split into one scenario per TestPlan id. Every scenario boots its own cluster, asserts the WA's role,
pauses the automatic loop and, where a leg needs a player, joins the founder client
(``CLIENT_ID``) through the WA player binding. Where a leg depends on state an earlier leg built,
both stay in one scenario.

| shell lines | leg | scenario |
|---|---|---|
| 783-797 | WA designated, owns and evaluates no partition; resume; automatic pause | every scenario (`_wa_cluster`) |
| 798-876 | founder join: WA binding before attach, identity on all roles, vanilla respawn | every client scenario (`_join_founder`) |
| 877-896 | faction created through the vanilla terminal UI, converged | P2-WA-01, P2-WA-00 (restore needs it) |
| 897-929 | partition-local respawn points converge; idempotent WA respawn arbitration | P2-WA-01 |
| 931-1097 | 10 km split; client 2 on node-1; UI faction join; GPS; medbay handover; kill; remote-medbay respawn without reconnect; replicated player state; GPS after handover | P2-WA-01 |
| 1098-1135 | faction chat to the cross-node member; UI faction leave | P2-WA-01 |
| 1136-1219 | rival faction; neutral seed; UI war and peace | P2-WA-01 |
| 1220-1288 | corrective faction snapshot reaches the client-visible table | P2-WA-01 |
| 1289-1305 | respawn execution idempotent by operation id | P2-WA-01 |
| 1307-1319 | session clock converges to the WA (< 250 ms) | P2-WA-00 |
| 1321-1344 | ordered global-state fan-out and gap resync | P2-WA-00 |
| 1346-1361 | node-forwarded chat validated and published by the WA | P2-WA-01 |
| 1363-1386 | WA identity allocation replicated to every player collection | P2-WA-00 |
| 1388-1433 | concurrent cross-node debits (one winner), operation dedup | P2-WA-00 (restore needs it) |
| 1434-1483 | store payments: concurrent commit, compensation, deferred settle/refund | P2-WA-ECON |
| 1484-1540 | player trade transfers: settle, overdraw refused, concurrent race | P2-WA-ECON |
| 1541-1626 | contract deposit activation; terminal Finish/Fail arbitration | P2-WA-ECON |
| 1627-1666 | contract reward split across owners on different nodes | P2-WA-ECON |
| 1667-1783 | global PCU ledger verdict; simultaneous PCU builds sum exactly | P2-WA-ECON |
| 1785-1878 | spawn director: routed, idempotent, a real prefab on exactly one node | P2-WA-03 |
| 1880-1924 | drop container dispatched to the owning node only | P2-WA-03 |
| 1926-2075 | IGC hosts; cross-node unicast (no leak) and broadcast; exactly-once journal | P2-MOD-01 |
| 2034-2075 | 27-message burst keeps vanilla's 25-message oldest-drop cap | P2-MOD-03 |
| 2077-2185 | IGC unicast + scoped broadcast held across a paused handover, forwarded in order | P2-MOD-02 |
| 2187-2239 | IGC relay chain: reachable through the relay, refused when it goes dark | P2-MOD-01 |
| 2241-2315 | cross-node laser link: connect, IGC over it, sever | P2-MOD-01 |
| 2317-2342 | mod channel quota faults; per-channel order on the far node | P2-MOD-05 |
| 2343-2369 | mod channel request to the WA and response back to the origin | P2-MOD-04 |
| 2523-2554 | mod channel request from a node restarted under the same name | P2-MOD-04-RESTART |
| 2371-2538 | global encounter once cluster-wide; its partition catalogued; placement retry; planetary | P2-WA-03 |
| 2540-2600 | cargo ship and meteor wave dispatch | P2-WA-03 |
| 2602-2637 | the WA's own encounter sweep fires | P2-WA-03 |
| 2639-2691 | cross-node trash-removal safety | P2-WA-03 |
| 2693-2745 | cross-node grid ownership transfer through client 2's dialog | P2-WA-ECON |
| 2746-2789 | combat reputation applied exactly once per event | P2-WA-01 |
| 2790-2815 | promote level replicated and demoted cross-node | P2-WA-01 |
| 2816-2838 | cluster online-player list identical on every role | P2-WA-01 |
| 2839-2864 | WA-side identity removal replicates | P2-WA-03 |
| 2865-2904 | AcceptHumans and faction reputation routed through the WA | P2-WA-00 |
| 2905-3153 | global save + second holder; newer state; WA SIGKILL; fence; replacement WA fetches the save; clock resync and monotonic; binding refresh; restored identity/binding/bank/faction; replicas reconverge; new generation; distinct identity allocation; stale-generation frame rejected | P2-WA-00 |

The economy legs are WA-2 content. TestPlan's P2-WA-02 id belongs to the `chaos` lane
(when this lane was split), so they run under the non-plan key ``P2-WA-ECON``.

Known product failure (2026-09-17, bench runs 20260917-052206Z and 20260917-054429Z): the founder's
vanilla faction creation is silently dropped at the WA (T-0165), so P2-WA-00 and P2-WA-01 stop at
"the vanilla faction creation converged on both replicas and the WA".
"""

from __future__ import annotations

import json
import os
import random
import re
import signal
import subprocess
import threading
import time
import traceback
import urllib.parse
import uuid
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace

from cluster import ClusterError
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _handover_url, _lines, _nodes, _owners, _partitions, \
    _wait_line
from scenarios_recovery import RECOVERY_TIMEOUT, _status

CLIENT2_ID = 1234567891
CLIENT2_PORT = 24159
# A THIRD client, for the one rule two clients cannot test: "a node does not mirror the players
# local to it" needs two clients on the SAME node, because the per-target self guard already keeps
# a player out of their own client's list (T-0204). Its port is neither 24159 nor 24160, both of
# which other scenarios use for a second or a duplicate client.
CLIENT3_ID = 1234567892
CLIENT3_PORT = 24161
CLIENT_TIMEOUT = 75
SERVER_TIMEOUT = 180


# -- plumbing -------------------------------------------------------------------------------


class LegFailed(ClusterError):
    """A dependency of the rest of the leg did not hold (the shell lane's `fail`)."""


def _require(bench, label: str, ok, detail: str = "") -> None:
    bench.check(label, bool(ok), detail)
    if not ok:
        raise LegFailed(label + (f": {detail}" if detail else ""))


def _leg(bench, name: str, function, *args):
    """Run a leg that nothing later in the scenario depends on: a failure is recorded, the
    scenario continues with its next leg."""
    try:
        return function(*args)
    except Exception as error:  # noqa: BLE001 - a failed leg is data
        bench.check(f"{name}: leg ran to completion", False, f"{type(error).__name__}: {error}"[:400])
        bench.record(f"leg-{name}-traceback.txt", traceback.format_exc())
        return None


def _poll(function, timeout: float, interval: float = 1.0):
    """The shell lane's `for second ...; do ... && break; sleep 1; done`: the first truthy value,
    or None. An exception inside the probe is a "not yet" (the shell's `2>/dev/null || true`)."""
    deadline = time.time() + timeout
    while True:
        try:
            value = function()
        except Exception:  # noqa: BLE001
            value = None
        if value:
            return value
        if time.time() >= deadline:
            return None
        time.sleep(interval)


def _node(bench, node_id: str, path: str, method: str = "GET", timeout: float = 60):
    return bench.cluster.node(node_id, path, method=method, timeout=timeout)


def _post(bench, node_id: str, path: str, timeout: float = 60):
    return bench.cluster.node(node_id, path, method="POST", timeout=timeout)


def _try(bench, node_id: str, path: str, method: str = "GET", timeout: float = 60):
    try:
        return bench.cluster.node(node_id, path, method=method, timeout=timeout)
    except Exception:  # noqa: BLE001
        return None


def _post_json(bench, node_id: str, path: str, body: dict):
    return bench.cluster._request("POST", f"/cluster{path}", _nodes(bench)[node_id].control, 60, body)


def _q(**params) -> str:
    return urllib.parse.urlencode(params, quote_via=urllib.parse.quote)


def _parallel(*calls):
    """Fire HTTP calls at once (the shell's `& ... wait`); each result or its exception."""
    def run(call):
        try:
            return call()
        except Exception as error:  # noqa: BLE001
            return error
    with ThreadPoolExecutor(max_workers=len(calls)) as pool:
        return list(pool.map(run, calls))


def _dumps(value) -> str:
    return json.dumps(value, sort_keys=True, default=str)


def _walk(value):
    if isinstance(value, dict):
        yield value
        for item in value.values():
            yield from _walk(item)
    elif isinstance(value, list):
        for item in value:
            yield from _walk(item)


def _session(bench, client: int) -> dict | None:
    sessions = bench.cluster.get("/transport/status").get("sessions") or []
    return next((session for session in sessions if session.get("client") == client), None)


def _first_line_number(bench, name: str, pattern: str) -> int | None:
    path = bench.run_root / f"{name}.log"
    expression = re.compile(pattern)
    for number, line in enumerate(path.read_text(errors="replace").splitlines(), 1):
        if expression.search(line):
            return number
    return None


def _subsystem(bench, node_id: str, *names: str) -> list[dict]:
    state = _node(bench, node_id, "/global-state")
    return [row for row in state.get("subsystems") or [] if row.get("subsystem") in names]


def _faction(bench, node_id: str, **params) -> dict | None:
    return _try(bench, node_id, "/faction-test-status?" + _q(**params))


def _balance(bench, node_id: str, owner: int) -> dict | None:
    return _try(bench, node_id, f"/banking-test-status?ownerId={owner}")


def _client_node(bench, client: int = CLIENT_ID) -> str | None:
    """The regular node hosting a living, attached client (a cross-node respawn moves it)."""
    for node_id in ("node-1", "node-2"):
        player = _try(bench, node_id, f"/respawn-test-player?userId={client}")
        if isinstance(player, dict) and player.get("attached") is True and player.get("dead") is False:
            return node_id
    return None


def _other(node_id: str) -> str:
    return "node-2" if node_id == "node-1" else "node-1"


# -- remote API (both clients) --------------------------------------------------------------


def _screens(api, kind: str) -> list[int]:
    try:
        return [screen["index"] for screen in api.list_screens() if screen.get("type") == kind]
    except Exception:  # noqa: BLE001
        return []


def _wait_screen(api, kind: str, timeout: float = 20, last: bool = False) -> int | None:
    found = _poll(lambda: _screens(api, kind), timeout)
    return (found[-1] if last else found[0]) if found else None


def _click(api, screen: int, text: str | None = None, name: str | None = None) -> bool:
    try:
        api.control_click(name=name, text=text, screen=screen)
        return True
    except Exception:  # noqa: BLE001
        return False


def _faction_page_visible(controls) -> bool:
    return any(item.get("name") == "PageFactions" and item.get("visible") is True for item in _walk(controls))


def _open_faction_page(bench, api) -> int | None:
    """The vanilla terminal on its Factions page; its screen index, or None."""
    # Never force-close MyGuiScreenLoading: the client is still building its session behind it, and
    # deleting it leaves the client with no local player at all, so the Factions page can never open
    # afterwards. Wait for the load to finish on its own, then dismiss only the respawn screen.
    _poll(lambda: not _screens(api, "MyGuiScreenLoading") and api.list_screens() is not None, 90)
    for _ in range(10):
        stale = _screens(api, "MyGuiScreenMedicals")
        if not stale:
            break
        try:
            api.close_screen(stale[-1])
        except Exception:  # noqa: BLE001
            pass
        time.sleep(0.2)
    # The endpoint answers 503 until the client's local identity has replicated; a no-render client
    # admitted without a character can take considerably longer than the terminal itself takes to build.
    screen = None
    for _ in range(60):
        terminals = _screens(api, "MyGuiScreenTerminal")
        if terminals:
            screen = terminals[0]
            break
        try:
            api.open_factions_terminal()
        except Exception:  # noqa: BLE001
            pass
        time.sleep(1)
    if screen is None:
        # The client's own session state: the 503 only says the local identity is missing, not
        # whether the client ever got a player at all.
        try:
            state = api.get_respawn_state()
        except Exception as error:  # noqa: BLE001
            state = {"error": str(error)}
        with open(bench.dir / "faction-page-state.jsonl", "a") as log:
            log.write(json.dumps({"url": api.base_url, "state": state}, default=str) + "\n")
        return None
    try:
        controls = api.get_controls(screen, depth=10)
        if not _faction_page_visible(controls):
            api.control_set("TerminalTabs", 4, screen=screen)
            controls = api.get_controls(screen, depth=10)
    except Exception:  # noqa: BLE001
        return None
    return screen if _faction_page_visible(controls) else None


def _select_row(api, screen: int, text: str) -> bool:
    for _ in range(20):
        if _click(api, screen, text=text):
            time.sleep(0.5)
            return True
        time.sleep(1)
    return False


def _confirm(api, label: str) -> None:
    """The vanilla message box a faction action opens; answer Yes."""
    box = _wait_screen(api, "MyGuiScreenMessageBox", 20, last=True)
    if box is None:
        raise LegFailed(f"the vanilla {label} confirmation did not open")
    if not _click(api, box, text="Yes"):
        raise LegFailed(f"could not confirm the {label}")


def _create_faction_ui(bench, api, tag: str, name: str) -> dict | None:
    terminal = _open_faction_page(bench, api)
    if terminal is None or not _click(api, terminal, name="buttonCreate"):
        return None
    create = _wait_screen(api, "MyGuiScreenCreateOrEditFactionSpace", 20)
    if create is None:
        return None
    try:
        controls = api.get_controls(create, depth=10)
        boxes = sorted((c for c in controls if c.get("type") == "MyGuiControlTextbox"),
                       key=lambda c: (c.get("position") or {}).get("y", 0))
        if len(boxes) < 2:
            return None
        api.control_set(boxes[0]["name"], tag, screen=create)
        api.control_set(boxes[1]["name"], name, screen=create)
        controls = api.get_controls(create, depth=10)
        texts = sorted((c.get("properties") or {}).get("text") or "" for c in controls
                       if c.get("type") == "MyGuiControlTextbox")
        if texts != sorted([tag, name]):
            return None
        api.control_click(text="OK", screen=create)
    except Exception:  # noqa: BLE001
        return None
    return {"requested": True, "via": "vanilla-faction-ui", "tag": tag, "name": name,
            "terminalScreen": terminal, "createScreen": create, "controls": controls}


def _faction_button(bench, api, who: str, select: list[str], button: str) -> None:
    terminal = _open_faction_page(bench, api)
    if terminal is None:
        raise LegFailed(f"{who} did not open the vanilla Factions page")
    for row in select:
        if not _select_row(api, terminal, row):
            raise LegFailed(f"{who} did not select the faction table row {row}")
    if not _click(api, terminal, name=button):
        raise LegFailed(f"{who} could not click {button}")


def start_second_client(bench, route_node: str = "node-2", partition: int | None = None, spawn: bool = False,
                        client_id: int = CLIENT2_ID, remote_port: int = CLIENT2_PORT, name: str | None = None):
    """Client 2 (no-render), routed to ``route_node`` through /registry/client-routes before it
    connects. Without ``spawn`` it only has to become chat-ready: admitted, bound to an identity and
    serving its chat log (the WA lane never gives it a character).

    ``client_id``/``remote_port`` default to client 2 and exist so a scenario can bring up a THIRD
    client on a node that already has one; everything below is written in terms of them."""
    nodes = _nodes(bench)
    backend = nodes[route_node].backend
    if partition is None:
        partition = next((row["Partition"] for row in _owners(bench) if row["Node"] == route_node), None)
    if partition is None:
        if route_node != "node-2":
            raise LegFailed(f"{route_node} has no partition for the second client route")
        seed = next((p["id"] for p in _partitions(bench, "node-1") if not p.get("clients")), None)
        if seed is None:
            raise LegFailed("node-1 has no clientless partition to seed node-2")
        bench.record(("client2" if client_id == CLIENT2_ID else f"client{client_id}") + "-seed-handover.txt", bench.cluster.post(_handover_url(bench, seed, clientless=True), timeout=300))
        partition = next((row["Partition"] for row in _owners(bench)
                          if row["Partition"] == seed and row["Node"] == "node-2"), None)
    if partition is None:
        raise LegFailed(f"{route_node} has no partition for the second client route")
    # The default keeps client 2's artifact and check names byte for byte, so a third client
    # cannot quietly rename what every earlier run recorded.
    tag = "client2" if client_id == CLIENT2_ID else f"client{client_id}"
    bench.record(f"{tag}-route", bench.cluster.post(f"/registry/client-routes?client={client_id}&partition={partition}"))
    api2 = bench.start_client(client_id, remote_port, render=False, name=name)
    _wait_line(bench, "gateway", rf"Resolved client {client_id} partition {partition} to {route_node} .* at {re.escape(backend)}",
               CLIENT_TIMEOUT)
    _wait_line(bench, route_node, rf"Accepted client {client_id} through cluster node link", CLIENT_TIMEOUT)
    if not spawn:
        def ready():
            session = _session(bench, client_id) or {}
            api2.get_chat_messages(0)
            return (session.get("backend") == backend and session.get("admissionState") == "Ready"
                    and (session.get("identityId") or 0) > 0)
        who = "the second client" if client_id == CLIENT2_ID else f"client {client_id}"
        _require(bench, f"{who} became chat-ready on {route_node}", _poll(ready, CLIENT_TIMEOUT))
        return api2
    bench.wait_spawned(client_id, timeout=CLIENT_TIMEOUT + 120)
    blocking = ("MyGuiScreenMedicals", "MyGuiScreenLoading")
    for _ in range(15):
        stuck = [s["index"] for s in api2.list_screens() if s.get("type") in blocking]
        if not stuck:
            break
        api2.close_screen(stuck[-1])
        time.sleep(1)
    _require(bench, "the second client is not held behind a loading or respawn screen",
             not any(s.get("type") in blocking for s in api2.list_screens()))
    return api2


# -- shared setup ---------------------------------------------------------------------------


def _wa_cluster(bench) -> SimpleNamespace:
    """node-1, node-2 and wa-1; the WA's role; the automatic co-location loop paused."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    wa = _nodes(bench)["wa-1"]
    authority = bench.cluster.get("/registry/world-authority")
    bench.record("world-authority", authority)
    _require(bench, "the registry designates wa-1 at its control endpoint",
             authority.get("Node") == "wa-1" and (authority.get("Generation") or 0) > 0
             and authority.get("ControlEndpoint") == f"http://{wa.control}", json.dumps(authority))
    bench.check("the World Authority owns no partition", all(row["Node"] != "wa-1" for row in _owners(bench)))
    bench.check("the World Authority evaluates no local partitions", len(_partitions(bench, "wa-1")) == 0)
    # This lane engineers deliberately co-located cross-node partitions (remote medbay, IGC hosts)
    # that the 7.1 automatic co-location would immediately undo; nothing here depends on the loop, so
    # suspend just the initiation. The 7.1 behavior itself is covered by the automatic/proximity/
    # handover scenarios.
    bench.cluster.post("/automatic/pause")
    return SimpleNamespace(generation=authority["Generation"])


def _join_founder(bench, ctx: SimpleNamespace) -> None:
    """Client 1 joins node-1: the Gateway obtains the WA player binding BEFORE it attaches the
    client, the identity reaches every role, then the vanilla respawn flow."""
    node1 = _nodes(bench)["node-1"]
    ctx.api = bench.start_client(CLIENT_ID)
    _wait_line(bench, "gateway", rf"Accepted client {CLIENT_ID}\b", CLIENT_TIMEOUT)

    def bound():
        s = _session(bench, CLIENT_ID) or {}
        return s if (s.get("admissionState") == "Ready" and (s.get("identityId") or 0) > 0
                     and s.get("attachedIdentityId") == s.get("identityId")
                     and s.get("attachedAuthorityGeneration") == s.get("authorityGeneration")
                     and s.get("attachedBindingRevision") == s.get("bindingRevision")) else None
    binding = _poll(bound, CLIENT_TIMEOUT)
    _require(bench, "the Gateway obtained a World Authority player binding before attach", binding,
             json.dumps(_session(bench, CLIENT_ID)))
    identity = binding["identityId"]
    _wait_line(bench, "gateway", rf"Player binding granted client {CLIENT_ID} identity {identity} generation {ctx.generation}",
               CLIENT_TIMEOUT)
    _wait_line(bench, "gateway", rf"Connected backend {re.escape(node1.backend)} as client {CLIENT_ID}", CLIENT_TIMEOUT)
    granted = _first_line_number(bench, "gateway", rf"Player binding granted client {CLIENT_ID}")
    attached = _first_line_number(bench, "gateway", rf"Connected backend {re.escape(node1.backend)} as client {CLIENT_ID}")
    _require(bench, "the Gateway attached the client only after the WA binding grant",
             granted is not None and attached is not None and granted < attached, f"grant line {granted}, attach line {attached}")
    _wait_line(bench, "node-1", rf"Accepted client {CLIENT_ID} through cluster node link", CLIENT_TIMEOUT)
    replicas = {}

    def everywhere():
        for node_id in ("node-1", "node-2", "wa-1"):
            replicas[node_id] = _try(bench, node_id, f"/identity-test-status?identityId={identity}")
        return all(isinstance(row, dict) and row.get("exists") for row in replicas.values())
    _require(bench, "the WA player identity converged on every role before the vanilla join",
             _poll(everywhere, HANDOVER_TIMEOUT), json.dumps(replicas)[:300])
    bench.record("world-authority-player-binding", {"gateway": binding, **replicas, "attachedAfterReady": True})
    ctx.binding_identity = identity
    ctx.client_binding = binding
    try:
        bench.wait_spawned(CLIENT_ID)
    except ClusterError as error:
        _require(bench, "the founder completed the vanilla respawn flow", False, str(error))
    bench.check("the founder completed the vanilla respawn flow", True)
    bench.screenshot(CLIENT_ID, "01-spawned")


def _found_faction(bench, ctx: SimpleNamespace) -> None:
    ctx.faction_tag = f"F{random.randrange(100):02d}"
    ctx.faction_name = f"phase2-wa-faction-{random.randrange(32768)}"
    # Who the client will name as founder (its LocalPlayerId), and what each role knows about that
    # identity: the vanilla create is silently dropped when the founder is unknown at the WA or
    # already in a faction there.
    try:
        local = (ctx.api.get_respawn_state() or {}).get("localPlayerId")
    except Exception:  # noqa: BLE001 - evidence only
        local = None
    bench.record("world-authority-founder-before-create", {
        "localPlayerId": local, "bindingIdentity": ctx.binding_identity,
        "identity": {node: _try(bench, node, f"/identity-test-status?identityId={local}") for node in ("node-1", "wa-1")}
                    if local else None})
    evidence = _create_faction_ui(bench, ctx.api, ctx.faction_tag, ctx.faction_name)
    bench.record("world-authority-faction-ui-create", evidence or {})
    _require(bench, "the founder submitted faction creation through the vanilla terminal UI", evidence)
    ctx.faction_create = {key: evidence[key] for key in ("requested", "via", "tag", "name", "terminalScreen", "createScreen")}
    ctx.faction_identity = ctx.binding_identity
    founder = ctx.faction_identity
    seen = {}

    def converged():
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = _faction(bench, node_id, tag=ctx.faction_tag, identityId=founder) or {}
        fid = seen["wa-1"].get("factionId") or 0
        return fid if fid > 0 and all(seen[n].get("founderId") == founder and founder in (seen[n].get("members") or [])
                                      for n in ("node-1", "node-2")) else None
    ctx.faction_id = _poll(converged, HANDOVER_TIMEOUT)
    _require(bench, "the vanilla faction creation converged on both replicas and the WA", ctx.faction_id,
             json.dumps(seen)[:300])


def _clock_spread(bench, generation: int, wa: str) -> tuple[float, dict]:
    clocks = {node_id: _try(bench, node_id, f"/authority-state?generation={generation}")
              for node_id in ("node-1", "node-2", wa)}
    if not all(isinstance(c, dict) and c.get("accepts") is True for c in clocks.values()):
        return 1e9, clocks
    values = [c["clockMilliseconds"] for c in clocks.values()]
    return max(values) - min(values), clocks


def _wait_clock(bench, generation: int, wa: str) -> tuple[bool, float, dict]:
    last = {"spread": 1e9, "clocks": {}}

    def converged():
        last["spread"], last["clocks"] = _clock_spread(bench, generation, wa)
        return last["spread"] < 250
    ok = bool(_poll(converged, HANDOVER_TIMEOUT))
    return ok, last["spread"], last["clocks"]


# -- P2-WA-01 legs ---------------------------------------------------------------------------


def _respawn_points(bench, ctx) -> None:
    """Partition-local respawn points converge through the WA; its arbitration is exact and idempotent."""
    seen = {}

    def converged():
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = _try(bench, node_id, "/respawn-points")
        wa = seen["wa-1"]
        if not (isinstance(wa, dict) and wa.get("count", 0) > 0
                and all(p.get("Working") and p.get("Origin") != "wa-1" and p.get("PartitionId", 0) > 0
                        and p.get("Generation", 0) > 0 for p in wa.get("points") or [])):
            return False
        points = [_dumps((seen[n] or {}).get("points")) for n in ("node-1", "node-2", "wa-1")]
        return len(set(points)) == 1
    _require(bench, "partition-local respawn points converged through the World Authority",
             _poll(converged, HANDOVER_TIMEOUT), json.dumps(seen)[:300])
    bench.record("world-authority-respawns", seen)
    points = seen["wa-1"]["points"]
    local = sorted((p for p in points if p.get("Origin") == "node-1"), key=lambda p: p["RespawnEntityId"])
    _require(bench, "a node-1 respawn point exists", local, json.dumps(points)[:300])
    point = local[0]
    ctx.client_identity = ctx.binding_identity
    ctx.respawn_entity, ctx.respawn_grid, ctx.respawn_name = point["RespawnEntityId"], point["GridEntityId"], point["Name"]
    ctx.respawn_partition, ctx.respawn_generation = point["PartitionId"], point["Generation"]
    _require(bench, "the respawn arbitration prerequisites are observable",
             ctx.client_identity > 0 and ctx.respawn_entity > 0 and ctx.respawn_partition > 0 and ctx.respawn_generation > 0)
    operation = f"00000000-0000-4000-8000-{random.randrange(32768):012d}"
    path = ("/respawn-test-arbitrate?" + _q(userId=CLIENT_ID, serialId=0, identityId=ctx.client_identity,
                                             respawnEntityId=ctx.respawn_entity, operationId=operation))
    first, second = _post(bench, "node-2", path), _post(bench, "node-2", path)
    bench.record("world-authority-respawn-arbitration", {"registry": seen["wa-1"], "operationId": operation, "decision": first})
    _require(bench, "the WA returned an exact, idempotent remote respawn decision",
             first.get("RespawnEntityId") == ctx.respawn_entity and first.get("GridEntityId") == ctx.respawn_grid
             and first.get("Origin") == "node-1" and first.get("PartitionId") == ctx.respawn_partition
             and first.get("Generation") == ctx.respawn_generation and first == second, json.dumps([first, second])[:300])


def _cross_node_respawn(bench, ctx) -> None:
    """The founder dies on node-1 and respawns at a medbay that moved to node-2, without reconnecting;
    client 2 joins the faction on the way; a GPS added on node-1 survives the move."""
    api = ctx.api
    nodes = _nodes(bench)
    player = _node(bench, "node-1", f"/respawn-test-player?userId={CLIENT_ID}")
    _require(bench, "the local vanilla medbay created the initial character", (player.get("character") or 0) > 0, json.dumps(player))
    _post(bench, "node-1", f"/test-position?entity={player['character']}&relativeTo={ctx.respawn_grid}&x=15000")
    split = {}

    def distinct():
        partitions = _partitions(bench, "node-1")
        split["client"] = next((p["id"] for p in partitions if CLIENT_ID in (p.get("clients") or [])), 0)
        points = (_try(bench, "wa-1", "/respawn-points") or {}).get("points") or []
        row = next((p for p in points if p["RespawnEntityId"] == ctx.respawn_entity and p["Origin"] == "node-1"), {})
        split["medbay"], split["generation"] = row.get("PartitionId", 0), row.get("Generation", 0)
        return (split["client"] > 0 and split["medbay"] > 0 and split["client"] != split["medbay"]
                and any(p["id"] == split["medbay"] and not p.get("clients") for p in partitions))
    _require(bench, "cross-node respawn has distinct client and medbay partitions", _poll(distinct, HANDOVER_TIMEOUT), json.dumps(split))
    ctx.client_partition = split["client"]
    ctx.respawn_partition, ctx.respawn_generation = split["medbay"], split["generation"]

    ctx.api2 = api2 = start_second_client(bench, "node-1", ctx.client_partition)
    session2 = _session(bench, CLIENT2_ID) or {}
    ctx.client2_identity = session2.get("identityId") or 0
    _require(bench, "the second faction client is attached to node-1",
             session2.get("backend") == nodes["node-1"].backend and ctx.client2_identity > 0, json.dumps(session2))
    client2_name = (_node(bench, "wa-1", f"/identity-test-status?identityId={ctx.client2_identity}") or {}).get("name")
    _require(bench, "the second faction client has an authoritative identity name", client2_name)

    _faction_button(bench, api2, "the second client", [ctx.faction_tag], "buttonJoin")
    _faction_button(bench, api, "the founder", [ctx.faction_tag, client2_name], "buttonAcceptJoin")
    _confirm(api, "faction acceptance")
    member = ctx.client2_identity
    joined = {}

    def join_converged():
        for node_id in ("node-1", "node-2", "wa-1"):
            joined[node_id] = _faction(bench, node_id, tag=ctx.faction_tag, identityId=member) or {}
        return (ctx.faction_identity in (joined["node-1"].get("members") or [])
                and all(member in (joined[n].get("members") or []) for n in joined))
    _require(bench, "the vanilla faction join converged on both replicas and the WA", _poll(join_converged, HANDOVER_TIMEOUT),
             json.dumps(joined)[:300])

    gps_name = f"phase2-handover-gps-{random.randrange(32768)}"
    gps_sent = api.add_gps(gps_name, 41.25, 52.5, 63.75, "WA handover GPS")
    bench.check("the client added a GPS before the cross-node respawn", True)
    session_before = _session(bench, CLIENT_ID)
    _require(bench, "the client session exists before the cross-node respawn", session_before)
    bench.record("respawn-medbay-handover.txt",
                 bench.cluster.post(_handover_url(bench, ctx.respawn_partition, clientless=True), timeout=300))

    def medbay_moved():
        points = (_try(bench, "wa-1", "/respawn-points") or {}).get("points") or []
        return next((p for p in points if p["RespawnEntityId"] == ctx.respawn_entity and p["Origin"] == "node-2"
                     and p["Generation"] > ctx.respawn_generation), None)
    moved = _poll(medbay_moved, HANDOVER_TIMEOUT)
    _require(bench, "the respawn registry followed the medbay partition to node-2", moved)
    generation_after = moved["Generation"]
    decision = _post(bench, "node-1", "/respawn-test-arbitrate?" + _q(userId=CLIENT_ID, serialId=0, identityId=ctx.client_identity,
                                                                       respawnEntityId=ctx.respawn_entity))
    _require(bench, "the WA arbitrates the moved medbay to node-2",
             decision.get("Origin") == "node-2" and decision.get("Generation") == generation_after, json.dumps(decision))
    source = _node(bench, "node-1", f"/respawn-test-player?userId={CLIENT_ID}")
    _require(bench, "the source player is still attached before remote respawn routing",
             source.get("attached") and source.get("identityId") == ctx.client_identity, json.dumps(source))

    identity = ctx.client_identity
    state = {}

    def player_states(*node_ids):
        for node_id in node_ids:
            state[node_id] = _try(bench, node_id, f"/player-state?identityId={identity}") or {}
        return state

    # WA-1 player state: with the client alive on node-1, roles that do not host it still know its
    # controlled entity from the once-a-second node reports.
    live = _poll(lambda: all((player_states("node-2", "wa-1")[n].get("cluster") or {}).get("controlledEntity", 0) > 0
                             for n in ("node-2", "wa-1")), HANDOVER_TIMEOUT)
    _require(bench, "the pre-death player state replicated a controlled entity", live, json.dumps(state)[:300])
    controlled_before = state["wa-1"]["cluster"]["controlledEntity"]
    killed = _post(bench, "node-1", f"/respawn-test-kill?userId={CLIENT_ID}")
    _require(bench, "node-1 applied lethal damage", killed.get("killed") and killed.get("identityId") == identity, json.dumps(killed))
    _require(bench, "node-1 observed the client character death",
             _poll(lambda: (lambda p: p.get("attached") and p.get("dead") is True)(
                 _node(bench, "node-1", f"/respawn-test-player?userId={CLIENT_ID}")), HANDOVER_TIMEOUT))

    # WA-1 player state: the node-1 death converges to roles that never saw it - the node-2 identity
    # replica and the WA both flip dead and learn the last-death position.
    def dead(row):
        cluster = row.get("cluster") or {}
        return (row.get("exists") and row.get("dead") is True and cluster.get("dead") is True
                and len(cluster.get("deathPosition") or []) == 3)
    _require(bench, "the client death replicated to node-2 and the WA",
             _poll(lambda: all(dead(player_states("node-2", "wa-1")[n]) for n in ("node-2", "wa-1")), HANDOVER_TIMEOUT),
             json.dumps(state)[:300])
    bench.record("world-authority-player-state", {"phase": "dead", "node2": state["node-2"], "wa": state["wa-1"]})

    def remote_row():
        medicals = _screens(api, "MyGuiScreenMedicals")
        return medicals[0] if medicals and _click(api, medicals[0], text=ctx.respawn_name) else None
    respawn_screen = _poll(remote_row, HANDOVER_TIMEOUT)
    _require(bench, "the vanilla medical screen exposed the remote medbay row", respawn_screen is not None)
    bench.screenshot(CLIENT_ID, "world-authority-remote-respawn")
    api.control_click(text="Respawn", screen=respawn_screen)
    routed = {"routed": True, "via": "medical-screen", "row": ctx.respawn_name}
    after = {}

    def respawned():
        after["player"] = _try(bench, "node-2", f"/respawn-test-player?userId={CLIENT_ID}") or {}
        after["session"] = _session(bench, CLIENT_ID) or {}
        after["route"] = bench.cluster.get(f"/registry/client-routes?client={CLIENT_ID}")
        p, s, r = after["player"], after["session"], after["route"] or {}
        return (p.get("attached") and p.get("dead") is False and (p.get("character") or 0) > 0 and (p.get("controlled") or 0) > 0
                and s.get("backend") == nodes["node-2"].backend and s.get("admissionState") == "Ready"
                and s.get("connectedAt") == session_before.get("connectedAt")
                and r.get("Partition") == ctx.respawn_partition and r.get("Node") == "node-2" and r.get("Generation") == generation_after)
    _require(bench, "the client respawned at the remote medbay without reconnecting", _poll(respawned, HANDOVER_TIMEOUT),
             json.dumps(after, default=str)[:400])
    _require(bench, "the headless client survived the cross-node respawn", bench.clients[CLIENT_ID].alive())

    # WA-1 player state: the node-2 respawn clears the dead flag cluster-wide and the controlled entity
    # moves to the new character.
    def alive(row, need_exists):
        cluster = row.get("cluster") or {}
        return ((row.get("exists") or not need_exists) and row.get("dead") is False and cluster.get("dead") is False
                and (cluster.get("controlledEntity") or 0) > 0 and cluster.get("controlledEntity") != controlled_before)
    _require(bench, "the respawn cleared the replicated death state",
             _poll(lambda: (lambda s: alive(s["node-1"], True) and alive(s["wa-1"], False))(player_states("node-1", "wa-1")),
                   HANDOVER_TIMEOUT), json.dumps(state)[:300])
    bench.record("world-authority-player-state-respawned",
                 {"phase": "respawned", "controlledBefore": controlled_before, "node1": state["node-1"], "wa": state["wa-1"]})
    gps_after = _poll(lambda: (lambda g: g if any(e.get("name") == gps_name for e in g.get("entries") or []) else None)(api.list_gps()),
                      HANDOVER_TIMEOUT)
    _require(bench, "the GPS added on node-1 is client-visible after the handover to node-2", gps_after)
    ctx.cross = {"sourcePartition": ctx.client_partition, "medbayPartition": ctx.respawn_partition,
                 "medbayGeneration": generation_after, "gps": {"name": gps_name, "sent": gps_sent, "afterHandover": gps_after},
                 "killed": killed, "request": routed, "targetPlayer": after["player"], "route": after["route"],
                 "sessionBefore": session_before, "sessionAfter": after["session"]}
    ctx.respawn_generation_after = generation_after


def _faction_chat_and_leave(bench, ctx) -> None:
    api, api2 = ctx.api, ctx.api2
    text = f"phase2-faction-chat-{random.randrange(32768)}"
    sent = api.send_chat(text, "Faction", ctx.faction_id)

    def received():
        messages = api2.get_chat_messages(0)
        return messages if any(m.get("Text") == text and m.get("Channel") == "Faction" and m.get("TargetId") == ctx.faction_id
                               for m in messages.get("messages") or []) else None
    got = _poll(received, HANDOVER_TIMEOUT)
    _require(bench, "cross-node faction chat reached the second faction member", got)
    bench.record("world-authority-faction-ui-join", {"tag": ctx.faction_tag, "factionId": ctx.faction_id,
                                                     "founder": ctx.faction_identity, "member": ctx.client2_identity,
                                                     "chat": {"text": text, "sent": sent, "received": got}})
    _faction_button(bench, api2, "the second client", [ctx.faction_tag], "buttonLeave")
    _confirm(api2, "faction leave")
    def left():
        status = _faction(bench, "wa-1", tag=ctx.faction_tag, identityId=ctx.client2_identity)
        return isinstance(status, dict) and isinstance(status.get("members"), list) \
            and ctx.client2_identity not in status["members"]
    _require(bench, "the vanilla faction leave reached the World Authority", _poll(left, HANDOVER_TIMEOUT))


def _relation_everywhere(bench, ctx, predicate, seen: dict) -> bool:
    for node_id in ("node-1", "node-2", "wa-1"):
        seen[node_id] = _faction(bench, node_id, tag=ctx.faction_tag, otherTag=ctx.other_tag) or {}
    return all(predicate(seen[n]) for n in seen)


def _diplomacy(bench, ctx) -> None:
    api, api2 = ctx.api, ctx.api2
    ctx.other_tag = f"G{random.randrange(100):02d}"
    other_name = f"phase2-wa-rival-{random.randrange(32768)}"
    create = _post(bench, "node-1", "/faction-test-create?" + _q(founderId=ctx.client2_identity, tag=ctx.other_tag, name=other_name))
    _require(bench, "a regular node submitted the rival faction to the World Authority",
             create.get("requested") and create.get("accepted") and create.get("tag") == ctx.other_tag
             and create.get("founderId") == ctx.client2_identity, json.dumps(create))
    seen = {}

    def rival_ready():
        other = _faction(bench, "wa-1", tag=ctx.other_tag, identityId=ctx.client2_identity) or {}
        ctx.other_id = other.get("factionId") or 0
        return (ctx.other_id > 0 and other.get("founderId") == ctx.client2_identity
                and ctx.client2_identity in (other.get("members") or [])
                and _relation_everywhere(bench, ctx, lambda s: s.get("otherTag") is not None, seen))
    _require(bench, "the rival faction converged before diplomacy", _poll(rival_ready, HANDOVER_TIMEOUT), json.dumps(seen)[:300])
    seed = _post(bench, "node-1", f"/faction-test-relation?fromFactionId={ctx.faction_id}&toFactionId={ctx.other_id}&reputation=0")
    _require(bench, "a regular node seeded the neutral relation at the WA", seed.get("requested") and seed.get("reputation") == 0,
             json.dumps(seed))
    _require(bench, "the neutral relation seed converged",
             _poll(lambda: _relation_everywhere(bench, ctx, lambda s: s.get("relation") == "Neutral", seen), HANDOVER_TIMEOUT))
    _faction_button(bench, api, "the founder", [ctx.other_tag], "buttonEnemy")
    _confirm(api, "war declaration")
    _require(bench, "the vanilla war declaration converged on both replicas and the WA",
             _poll(lambda: _relation_everywhere(bench, ctx, lambda s: s.get("relation") == "Enemies", seen), HANDOVER_TIMEOUT),
             json.dumps(seen)[:300])
    war = dict(seen)
    _faction_button(bench, api2, "the rival founder", [ctx.faction_tag], "buttonSendPeace")
    request = _poll(lambda: (lambda s: s if s.get("relation") == "Enemies" and s.get("request") == "Pending" else None)(
        _faction(bench, "wa-1", tag=ctx.faction_tag, otherTag=ctx.other_tag) or {}), HANDOVER_TIMEOUT)
    _require(bench, "the vanilla peace request reached the World Authority", request)
    _faction_button(bench, api, "the founder", [ctx.other_tag], "buttonAcceptPeace")
    _require(bench, "the vanilla peace acceptance converged on both replicas and the WA",
             _poll(lambda: _relation_everywhere(bench, ctx, lambda s: s.get("relation") == "Neutral" and s.get("request") == "None", seen),
                   HANDOVER_TIMEOUT), json.dumps(seen)[:300])
    bench.record("world-authority-faction-diplomacy", {
        "first": {"tag": ctx.faction_tag, "id": ctx.faction_id},
        "second": {"tag": ctx.other_tag, "id": ctx.other_id, "create": create}, "neutralSeed": seed,
        "war": war, "peaceRequest": request, "peace": seen})


def _snapshot_reconciliation(bench, ctx) -> None:
    """WA-1 corrective-snapshot client UI reconciliation: drop the node-to-WA link on the node hosting
    client 1 and remove the rival faction at the WA during the outage. The per-operation success
    cannot reach that node, so its faction state heals through a FULL corrective snapshot - and its
    game clients only hear about snapshot-applied faction add/removes through the node's diff
    re-broadcast, which the client-visible faction table must reflect."""
    api = ctx.api
    snap = _client_node(bench)
    _require(bench, "a regular node hosts client 1 for the snapshot-reconciliation leg", snap)
    other = _other(snap)
    # Positive control: the client-visible table still lists the rival faction.
    terminal = _open_faction_page(bench, api)
    _require(bench, "client 1 opened the factions page before the snapshot leg", terminal is not None)
    _require(bench, "client 1 sees the rival faction before the snapshot removal", _select_row(api, terminal, ctx.other_tag))
    _post(bench, snap, "/test-wa-partition?ms=15000")
    removed = _post(bench, other, f"/faction-test-remove?factionId={ctx.other_id}&sender={CLIENT2_ID}")
    _require(bench, "the World Authority accepted the rival faction removal",
             removed.get("requested") is True and removed.get("accepted") is True, json.dumps(removed))
    # Isolate authority-side application before any propagation concern.
    _require(bench, "the World Authority itself removed the rival faction",
             _poll(lambda: (_faction(bench, "wa-1", tag=ctx.other_tag) or {}).get("exists") is False, 15),
             json.dumps(_faction(bench, "wa-1", tag=ctx.other_tag)))
    bench.record("world-authority-faction-snapshot-remove", removed)
    # The lossy window silently ate the removal broadcast, and the factions channel only notices the
    # gap on its NEXT message (a real link failure bumps the link epoch and resyncs every subsystem -
    # the chaos knob drops frames without a disconnect). Nudge the subsystem after the 15 s window
    # closes so the healed node detects the gap and pulls the corrective snapshot.
    time.sleep(16)
    # Accept-humans toggles route through the faction authority and publish accepted successes; the
    # first post-outage publish makes the healed node see a sequence gap and pull the corrective
    # snapshot (an AddReputation nudge proved unreliable - the clamped delta returns false and
    # publishes nothing).
    nudges = []
    for accept in ("false", "true"):
        try:
            nudges.append(_post(bench, other, f"/faction-test-accept-humans?factionId={ctx.faction_id}&accept={accept}"))
        except ClusterError as error:
            nudges.append(f"nudge-failed: {error}")
        if accept == "false":
            time.sleep(2)
    # The outage node must converge - and via a corrective snapshot, not the success path.
    gone = _poll(lambda: (_faction(bench, snap, tag=ctx.other_tag) or {}).get("exists") is False, 90)
    if not gone:
        bench.record("world-authority-faction-snapshot-diag", {"nudge": nudges, "node": _try(bench, snap, "/global-state"),
                                                               "wa": _try(bench, "wa-1", "/global-state")})
    _require(bench, "the rival faction disappeared from the outage node", gone, json.dumps(_faction(bench, snap, tag=ctx.other_tag)))
    bench.check("the outage node logged a corrective-snapshot client removal",
                bool(_lines(bench, snap, r"snapshot_reconciled=true.*clients_removed=[1-9]")))
    # The client-visible faction table must lose the row while keeping the surviving faction.
    terminal = _open_faction_page(bench, api)
    _require(bench, "client 1 reopened the factions page after the snapshot removal", terminal is not None)
    bench.check("client 1 still sees its own faction after the corrective snapshot", _select_row(api, terminal, ctx.faction_tag))
    bench.check("client 1 no longer sees the snapshot-removed rival faction", not _select_row(api, terminal, ctx.other_tag))
    try:
        bench.record("world-authority-faction-snapshot-ui", api.get_controls(terminal, depth=10))
    except Exception:  # noqa: BLE001
        pass


def _respawn_replay(bench, ctx) -> None:
    body = {"OperationId": f"00000000-0000-4000-8001-{random.randrange(32768):012d}", "UserId": CLIENT_ID, "SerialId": 0,
            "IdentityId": ctx.client_identity, "RespawnEntityId": ctx.respawn_entity, "GridEntityId": ctx.respawn_grid,
            "Origin": "node-2", "PartitionId": ctx.respawn_partition, "Generation": ctx.respawn_generation_after,
            "JoinGame": False, "ModelName": None, "PackedColor": 0}
    first = _post_json(bench, "node-2", "/respawn-execute", body)
    second = _post_json(bench, "node-2", "/respawn-execute", body)
    bench.check("the target respawn execution is idempotent by operation id",
                first == second and first.get("Ready") and first.get("Success") and (first.get("ControlledEntityId") or 0) > 0,
                json.dumps([first, second])[:300])
    bench.record("world-authority-cross-node-respawn", {**ctx.cross, "replay": first})


def _forwarded_chat(bench, ctx) -> None:
    request = _post(bench, "node-1", f"/chat-test-request?sender={CLIENT_ID}&text=phase2-wa-chat")
    _require(bench, "a regular node forwarded chat to the World Authority", request.get("forwarded") is True, json.dumps(request))
    seen = {}

    def applied():
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = _subsystem(bench, node_id, "chat")
        return all(any("phase2-wa-chat" in (row.get("payload") or "") for row in seen[n]) for n in seen)
    bench.check("the WA validated and published the forwarded chat on every role", _poll(applied, HANDOVER_TIMEOUT))
    bench.record("world-authority-chat", {"request": request, **seen})


def _combat_reputation(bench, ctx) -> None:
    """WA-1 combat-driven reputation: the vanilla combat entry point (DamageFactionPlayerReputation -
    the same path weapon damage and kills call) fires on a REGULAR node, the faction-authority prefix
    forwards it to the WA exactly once, and the changed reputation replicates to every role. Two
    identical events must move the value by the same delta twice - neither deduped nor double-applied.
    The target must be an NPC faction member: vanilla's GenerateChanges silently drops reputation
    deltas against Player-Made factions."""
    tag = "FCMC"
    target = (_node(bench, "wa-1", f"/faction-test-status?tag={tag}") or {}).get("founderId") or 0
    _require(bench, f"NPC faction {tag} has a founder identity", target > 0)
    # The node hosting client 2 (the one not hosting client 1).
    other = _other(_client_node(bench) or "node-2")
    last = {}

    def values():
        for node_id in ("node-1", "node-2", "wa-1"):
            last[node_id] = (_faction(bench, node_id, tag=tag, identityId=ctx.client2_identity) or {}).get("reputation")
        return last

    def agreed(exclude=None):
        v = values()
        same = v["node-1"] is not None and v["node-1"] == v["node-2"] == v["wa-1"]
        return v["node-1"] if same and v["node-1"] != exclude else None
    baseline = _poll(lambda: (lambda b: None if b is None else [b])(agreed()), 30)
    _require(bench, "the combat reputation baseline converged", baseline, json.dumps(last))
    baseline = baseline[0]
    damage = f"/faction-test-damage-reputation?identityId={ctx.client2_identity}&attackedIdentityId={target}&type=Stealing&amount=0"
    _post(bench, other, damage)
    after1 = _poll(lambda: (lambda b: None if b is None else [b])(agreed(exclude=baseline)), 30)
    _require(bench, "the combat reputation damage converged", after1, f"{json.dumps(last)} baseline={baseline}")
    after1 = after1[0]
    delta = baseline - after1
    _require(bench, "combat reputation moved down", delta > 0, f"baseline={baseline} after={after1}")
    _post(bench, other, damage)
    expected = after1 - delta
    bench.check("the second combat reputation event applied exactly once",
                _poll(lambda: all(value == expected for value in values().values()), 30), f"expected={expected} {json.dumps(last)}")
    bench.record("world-authority-reputation", {"baseline": baseline, "afterFirst": after1, "afterSecond": last.get("node-1"),
                                                "delta": delta})


def _promotion(bench, ctx) -> None:
    """WA-1 promote-level replication: vanilla's OnPromoteLevelSet updates only the raising node and
    its attached clients, so server-side admin checks on every other node kept answering the old
    level. Promote on node-1 (SpaceMaster=3), expect ALL roles to converge, then demote from the
    OTHER node (removal path)."""
    last = {}

    def levels():
        for node_id in ("node-1", "node-2", "wa-1"):
            last[node_id] = (_try(bench, node_id, f"/promote-test?userId={CLIENT_ID}") or {}).get("promoted")
        return last
    levels()
    _require(bench, "the baseline promote level is 0 on both regular nodes",
             last["node-1"] == 0 and last["node-2"] == 0, json.dumps(last))
    _post(bench, "node-1", f"/promote-test?userId={CLIENT_ID}&level=3")
    bench.check("the promote level converged on every role",
                _poll(lambda: all(v == 3 for v in levels().values()), 30), json.dumps(last))
    _post(bench, "node-2", f"/promote-test?userId={CLIENT_ID}&level=0")
    bench.check("the cross-node demotion converged on every role",
                _poll(lambda: all(v == 0 for v in levels().values()), 30), json.dumps(last))
    bench.record("world-authority-promotion", {"promoted": 3, "demoted": 0})


def _online_list(bench, ctx) -> None:
    """WA-1 online list: vanilla GetOnlinePlayers only knows locally attached players. The replicated
    cluster list must show BOTH clients (with a hosting node each) identically on every role."""
    seen = {}

    def both(listing):
        rows = listing.get("cluster") or []
        return all(sum(1 for row in rows if row.get("steamId") == client and row.get("node") not in (None, "")) == 1
                   for client in (CLIENT_ID, CLIENT2_ID))

    def converged():
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = _try(bench, node_id, "/online-players") or {}
        return all(both(seen[n]) for n in seen) and len({_dumps(seen[n].get("cluster")) for n in seen}) == 1
    bench.check("the cluster online-player list converged on every role", _poll(converged, 30),
                json.dumps({n: seen[n].get("cluster") for n in seen})[:400])
    bench.record("world-authority-online-players", seen.get("node-1"))


# -- P2-WA-00 legs ---------------------------------------------------------------------------


def _global_state_order(bench, ctx) -> None:
    published = _poll(lambda: _post(bench, "wa-1", "/global-state/publish?subsystem=probe&payload=ordered") is not None,
                      HANDOVER_TIMEOUT)
    _require(bench, "the WA global-state node link became ready", published)
    rows = {}

    def probe(predicate):
        for node_id in ("node-1", "node-2"):
            rows[node_id] = _subsystem(bench, node_id, "probe")
        return all(any(predicate(row) for row in rows[n]) for n in rows)
    _require(bench, "the ordered global-state broadcast reached both regular nodes",
             _poll(lambda: probe(lambda r: r.get("sequence") == 1 and r.get("payload") == "ordered"), HANDOVER_TIMEOUT),
             json.dumps(rows))
    _post(bench, "wa-1", "/global-state/publish?subsystem=probe&payload=resynced&skipSequence=true")
    bench.check("a global-state sequence gap resynced both regular nodes",
                _poll(lambda: probe(lambda r: r.get("sequence") == 3 and r.get("payload") == "resynced"
                                    and (r.get("gaps") or 0) > 0 and (r.get("snapshots") or 0) > 0), HANDOVER_TIMEOUT),
                json.dumps(rows))
    bench.record("world-authority-global-state", rows)


def _identity_allocation(bench, ctx) -> None:
    ctx.identity_name = f"phase2-wa-identity-{random.randrange(32768)}"
    request = _post(bench, "node-1", f"/identity-test-create?name={ctx.identity_name}")
    ctx.identity_id = request.get("identityId") or 0
    _require(bench, "a regular node received a WA identity allocation", ctx.identity_id > 0, json.dumps(request))
    rows = {}

    def applied():
        for node_id in ("node-1", "node-2", "wa-1"):
            rows[node_id] = _subsystem(bench, node_id, "identities")
        return all(any(ctx.identity_name in (row.get("payload") or "") for row in rows[n]) for n in rows)
    _require(bench, "the WA identity allocation converged on both replicas", _poll(applied, HANDOVER_TIMEOUT))
    local = {n: _node(bench, n, f"/identity-test-status?identityId={ctx.identity_id}") for n in ("node-1", "node-2", "wa-1")}
    _require(bench, "the WA identity was applied to every local player collection",
             all(row.get("exists") and row.get("name") == ctx.identity_name for row in local.values()), json.dumps(local))
    bench.record("world-authority-identity", {"request": request, "global": rows, "local": local})


def _balances(bench, owner: int, expected: int, node_ids=("node-1", "node-2", "wa-1")) -> bool:
    return all((lambda b: isinstance(b, dict) and b.get("exists") and b.get("balance") == expected)(_balance(bench, n, owner))
               for n in node_ids)


def _bank_race(bench, ctx) -> None:
    ctx.bank_owner = 900000000000000000 + random.randrange(32768)
    create = _post(bench, "node-1", f"/banking-test-create?ownerId={ctx.bank_owner}&balance=100")
    _require(bench, "a regular node submitted account creation to the WA",
             create.get("requested") and create.get("ownerId") == ctx.bank_owner, json.dumps(create))
    _require(bench, "the WA account snapshot converged", _poll(lambda: _balances(bench, ctx.bank_owner, 100), HANDOVER_TIMEOUT))
    path = f"/banking-test-change?ownerId={ctx.bank_owner}&amount=-60"
    results = _parallel(lambda: _post(bench, "node-1", path), lambda: _post(bench, "node-2", path))
    bench.record("bank-race-results", [str(r) if isinstance(r, Exception) else r for r in results])
    winners = sum(1 for r in results if isinstance(r, dict) and r.get("success"))
    _require(bench, "concurrent cross-node debits produced exactly one winner", winners == 1, str(results)[:300])
    _require(bench, "the WA balance converged after the concurrent debits",
             _poll(lambda: _balances(bench, ctx.bank_owner, 40), HANDOVER_TIMEOUT))
    ctx.bank_operation = f"00000000-0000-4000-8000-{random.randrange(32768):012d}"
    dedup = f"/banking-test-change?ownerId={ctx.bank_owner}&amount=5&operationId={ctx.bank_operation}"
    ctx.bank_dedup = [_post(bench, "node-1", dedup), _post(bench, "node-1", dedup)]
    _require(bench, "a duplicate banking operation returned its original success",
             all(r.get("success") and r.get("operationId") == ctx.bank_operation for r in ctx.bank_dedup), json.dumps(ctx.bank_dedup))
    _require(bench, "the duplicate banking operation applied exactly once",
             _poll(lambda: _balances(bench, ctx.bank_owner, 45, ("wa-1",)), HANDOVER_TIMEOUT))
    bench.record("world-authority-banking", {"create": create, "race": results, "dedup": ctx.bank_dedup})


def _faction_levers(bench, ctx) -> None:
    founder, fid = ctx.faction_identity, ctx.faction_id
    seen = {}

    def everywhere(predicate):
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = _faction(bench, node_id, tag=ctx.faction_tag, identityId=founder) or {}
        return all(predicate(seen[n]) for n in seen)
    _require(bench, "the WA faction creation converged on both replicas",
             _poll(lambda: everywhere(lambda s: s.get("exists") and s.get("factionId") == fid and s.get("founderId") == founder
                                      and founder in (s.get("members") or [])), HANDOVER_TIMEOUT), json.dumps(seen)[:300])
    accept = _post(bench, "node-1", f"/faction-test-accept-humans?factionId={fid}&accept=false")
    _require(bench, "a regular node routed AcceptHumans to the WA", accept.get("requested") and accept.get("accept") is False,
             json.dumps(accept))
    _require(bench, "the WA AcceptHumans change converged",
             _poll(lambda: everywhere(lambda s: s.get("acceptHumans") is False), HANDOVER_TIMEOUT))
    reputation = _post(bench, "node-2", f"/faction-test-reputation?identityId={founder}&factionId={fid}&reputation=321")
    _require(bench, "a regular node routed faction reputation to the WA",
             reputation.get("requested") and reputation.get("reputation") == 321, json.dumps(reputation))
    _require(bench, "the WA faction reputation converged", _poll(lambda: everywhere(lambda s: s.get("reputation") == 321), HANDOVER_TIMEOUT))
    bench.record("world-authority-factions", {"create": ctx.faction_create, "acceptRequest": accept,
                                              "reputationRequest": reputation, **seen})


def _newest_global(bench) -> dict | None:
    try:
        return bench.cluster.get("/registry/global-saves/newest")
    except ClusterError:
        return None


def _failover(bench, ctx) -> None:
    generation, founder, fid = ctx.generation, ctx.faction_identity, ctx.faction_id
    captured = _poll(lambda: _post(bench, "wa-1", "/global-save-capture", timeout=5) is not None, HANDOVER_TIMEOUT)
    _require(bench, "the WA accepted the pre-failover global save capture", captured)
    before = ((_newest_global(bench) or {}).get("SaveId") or {}).get("Sequence", 0)
    saved = _poll(lambda: (lambda s: s if s and s["SaveId"]["Generation"] == generation and s["SaveId"]["Sequence"] > before else None)(
        _newest_global(bench)), HANDOVER_TIMEOUT)
    _require(bench, "the WA registered the pre-failover global save", saved)
    bench.record("world-authority-global-save", saved)
    # The newest global save must reach a second holder before the failover: the replacement WA boots
    # on a fresh root and fetches it from that holder.
    global_save = _poll(lambda: (lambda s: s if any(v != "wa-1" for v in (s.get("LocationHolders") or {}).values()) else None)(
        _newest_global(bench) or {}), HANDOVER_TIMEOUT)
    _require(bench, "the pre-failover global save was replicated to a second holder", global_save,
             json.dumps((_newest_global(bench) or {}).get("LocationHolders")))
    bench.record("world-authority-global-replicas", global_save)
    _post(bench, "node-2", f"/faction-test-reputation?identityId={founder}&factionId={fid}&reputation=999")
    _post(bench, "node-1", f"/faction-test-accept-humans?factionId={fid}&accept=true")
    newer = {}

    def newer_everywhere():
        for node_id in ("node-1", "node-2", "wa-1"):
            newer[node_id] = _faction(bench, node_id, tag=ctx.faction_tag, identityId=founder) or {}
        return all(s.get("reputation") == 999 and s.get("acceptHumans") is True for s in newer.values())
    _require(bench, "the post-save faction state converged before the failover", _poll(newer_everywhere, HANDOVER_TIMEOUT))
    clocks_before = {n: _node(bench, n, f"/authority-state?generation={generation}") for n in ("node-1", "node-2", "wa-1")}
    clock_before_max = max(c["clockMilliseconds"] for c in clocks_before.values())

    # The newest catalogued save at kill time, for the record: a periodic save can catalog the post-save
    # 999/true mutation before the kill lands (saves come as close as ~1s apart).
    save_at_kill = _newest_global(bench)
    bench.cluster.kill(slot="wa-1")
    process = bench.cluster.processes.get("wa-1")
    if process is not None:
        try:
            process.wait(timeout=30)
        except Exception:  # noqa: BLE001
            pass
    fenced = _poll(lambda: (lambda a: a if a.get("Node") is None and (a.get("Generation") or 0) > generation else None)(
        bench.cluster.get("/registry/world-authority")), RECOVERY_TIMEOUT)
    _require(bench, "the Gateway fenced the failed World Authority lease", fenced)
    fenced_generation = fenced["Generation"]
    stale = {}

    def fenced_seen():
        for node_id in ("node-1", "node-2"):
            stale[node_id] = _try(bench, node_id, f"/authority-state?generation={generation}") or {}
        return all(s.get("generation") == fenced_generation and s.get("accepts") is False for s in stale.values())
    _require(bench, "the regular nodes observed the fenced WA generation", _poll(fenced_seen, HANDOVER_TIMEOUT), json.dumps(stale))

    replacement_spec = bench.cluster.config.add_node("wa-2", role="WA")
    # wa-2 starts -noupdate on a fresh Magnetar config dir: give it wa-1's hub snapshot (wa-1 was the
    # start's warm-up node), so it loads the same plugin set as the rest of the cluster.
    bench.cluster.propagate_sources(_nodes(bench)["wa-1"], [replacement_spec])
    bench.cluster.start_node("wa-2")
    # The replacement WA boots on a fresh root: the catalogued global save is held by the dead wa-1 and
    # by whichever regular node replicated it, so the restore must FETCH it over a holder's control
    # endpoint instead of reading wa-1's absolute path.
    bench.cluster.wait_node_log("wa-2", "global_save fetched=true", SERVER_TIMEOUT)
    bench.cluster.wait_node_log("wa-2", "Game ready", SERVER_TIMEOUT)
    bench.check("the replacement WA fetched the save rather than falling back to a foreign path",
                not _lines(bench, "wa-2", r"global_save foreign_path_fallback=true"))
    replacement = bench.cluster.get("/registry/world-authority")
    replacement_generation = replacement.get("Generation") or 0
    _require(bench, "the replacement WA is designated with a newer generation",
             replacement.get("Node") == "wa-2" and replacement_generation > generation
             and replacement.get("ControlEndpoint") == f"http://{replacement_spec.control}", json.dumps(replacement))
    ok, spread, clocks = _wait_clock(bench, replacement_generation, "wa-2")
    _require(bench, "the nodes resynced to the replacement WA clock (< 250 ms)", ok, f"spread={spread} ms")
    binding_state = {"state": "disconnected"}

    def refreshed():
        s = _session(bench, CLIENT_ID)
        if s is None:
            return True
        if (s.get("admissionState") == "Ready" and s.get("authorityGeneration") == replacement_generation
                and s.get("identityId") == ctx.binding_identity and s.get("attachedAuthorityGeneration") == replacement_generation
                and s.get("attachedIdentityId") == ctx.binding_identity and s.get("attachedBindingRevision") == s.get("bindingRevision")):
            binding_state["state"] = "attached"
            return True
        return False
    bench.check("the Gateway refreshed the live attached client binding after the replacement", _poll(refreshed, HANDOVER_TIMEOUT))
    clock_after_min = min(c["clockMilliseconds"] for c in clocks.values())
    bench.check("the replacement WA clock did not move backward", clock_after_min >= clock_before_max,
                f"before={clock_before_max} ms after={clock_after_min} ms")

    restored_identity = _node(bench, "wa-2", f"/identity-test-status?identityId={ctx.identity_id}")
    restored_binding = _node(bench, "wa-2", f"/identity-test-status?identityId={ctx.binding_identity}")
    dedup_after = _post(bench, "node-2", f"/banking-test-change?ownerId={ctx.bank_owner}&amount=5&operationId={ctx.bank_operation}")
    bench.check("the replacement WA returned the persisted operation result",
                dedup_after.get("success") and dedup_after.get("operationId") == ctx.bank_operation, json.dumps(dedup_after))
    restored_bank = _node(bench, "wa-2", f"/banking-test-status?ownerId={ctx.bank_owner}")
    bench.record("world-authority-command-replay", {"operationId": ctx.bank_operation, "first": ctx.bank_dedup[0],
                                                    "duplicate": ctx.bank_dedup[1], "afterReplacement": dedup_after,
                                                    "restoredBank": restored_bank})
    restored_faction = _node(bench, "wa-2", "/faction-test-status?" + _q(tag=ctx.faction_tag, identityId=founder))
    bench.record("world-authority-restored-state", {"identity": restored_identity, "binding": restored_binding,
                                                    "bank": restored_bank, "faction": restored_faction,
                                                    "expectedFactionId": fid, "factionIdentity": founder,
                                                    "observedSave": global_save, "saveAtKill": save_at_kill})
    bench.check("the replacement WA restored the catalogued identity",
                restored_identity.get("exists") and restored_identity.get("name") == ctx.identity_name, json.dumps(restored_identity))
    bench.check("the replacement WA restored the Gateway player binding identity", bool(restored_binding.get("exists")))
    bench.check("the replacement WA restored the catalogued bank balance",
                restored_bank.get("exists") and restored_bank.get("balance") == 45, json.dumps(restored_bank))
    rep, accept = restored_faction.get("reputation"), restored_faction.get("acceptHumans")
    same_faction = restored_faction.get("exists") and restored_faction.get("factionId") == fid
    # #1318: the post-save 999/true mutations were answered, so the WA journaled them before it answered, and the
    # replacement replays them after whichever save it restored (they used to be rolled back to 321/false unless a
    # periodic save raced them).
    bench.check("the replacement WA restored the answered faction state (999, AcceptHumans true)",
                same_faction and rep == 999 and accept is True,
                f"observed={json.dumps((global_save or {}).get('SaveId'))} atKill={json.dumps((save_at_kill or {}).get('SaveId'))} "
                f"restored={rep}/{accept}")
    # The replicas must reconverge to the state the replacement restored.
    replicas = {}

    def reconverged():
        for node_id in ("node-1", "node-2"):
            replicas[node_id] = {
                "identity": _try(bench, node_id, f"/identity-test-status?identityId={ctx.identity_id}") or {},
                "bank": _balance(bench, node_id, ctx.bank_owner) or {},
                "faction": _faction(bench, node_id, tag=ctx.faction_tag, identityId=founder) or {}}
        return all(r["identity"].get("exists") and r["identity"].get("name") == ctx.identity_name
                   and r["bank"].get("exists") and r["bank"].get("balance") == 45
                   and r["faction"].get("exists") and r["faction"].get("factionId") == fid
                   and r["faction"].get("reputation") == rep and r["faction"].get("acceptHumans") == accept
                   for r in replicas.values())
    bench.check("the replacement WA state reconverged on both replicas", _poll(reconverged, HANDOVER_TIMEOUT),
                json.dumps(replicas)[:400])
    bench.record("world-authority-faction-recovery", {"saved": {"reputation": 321, "acceptHumans": False}, "newer": newer,
                                                      "restored": {"node1": replicas.get("node-1", {}).get("faction"),
                                                                   "node2": replicas.get("node-2", {}).get("faction"),
                                                                   "wa": restored_faction}})
    _require(bench, "the replacement WA global-state node link became ready",
             _poll(lambda: _post(bench, "wa-2", "/global-state/publish?subsystem=probe&payload=replacement") is not None,
                   HANDOVER_TIMEOUT))
    rows = {}

    def replacement_applied():
        for node_id in ("node-1", "node-2"):
            rows[node_id] = _subsystem(bench, node_id, "probe")
        return all(any(r.get("generation") == replacement_generation and r.get("sequence") == 1 and r.get("payload") == "replacement"
                       for r in rows[n]) for n in rows)
    bench.check("the replacement WA broadcast reset the replica generation", _poll(replacement_applied, HANDOVER_TIMEOUT),
                json.dumps(rows))
    after_name = f"phase2-wa-identity-after-{random.randrange(32768)}"
    allocated = {}

    def distinct_identity():
        allocated["request"] = _post(bench, "node-2", f"/identity-test-create?name={after_name}")
        identity = allocated["request"].get("identityId")
        if not (isinstance(identity, int) and not isinstance(identity, bool) and identity > 0) or identity == ctx.identity_id:
            return False
        allocated["local"] = _try(bench, "node-2", f"/identity-test-status?identityId={identity}") or {}
        return allocated["local"].get("exists") and allocated["local"].get("name") == after_name
    bench.check("the replacement WA allocated a distinct identity", _poll(distinct_identity, HANDOVER_TIMEOUT), json.dumps(allocated))
    bench.record("world-authority-state-restored", {"globalSave": global_save, "bindingRefresh": binding_state["state"],
                                                    "replacementIdentity": restored_identity,
                                                    "replacementBinding": restored_binding, "replacementBank": restored_bank,
                                                    "replacementFaction": restored_faction, "allocatedAfter": allocated,
                                                    "replicas": replicas})
    rejected_before = bench.cluster.get("/transport/status")["counters"]["rejectedGlobalMessages"]
    _post(bench, "wa-2", f"/global-state/publish?subsystem=probe&payload=stale&generation={generation}")
    transport = {}

    def rejected():
        transport.update(bench.cluster.get("/transport/status"))
        return transport["counters"]["rejectedGlobalMessages"] > rejected_before
    bench.check("the Gateway rejected the stale-generation global-state frame", _poll(rejected, HANDOVER_TIMEOUT))
    finals = {n: _subsystem(bench, n, "identities", "probe") for n in ("node-1", "node-2")}

    def bound(rows_of_node):
        for row in rows_of_node:
            if row.get("subsystem") != "identities" or row.get("generation") != replacement_generation:
                continue
            try:
                bindings = json.loads(row.get("payload") or "{}").get("Bindings") or []
            except ValueError:
                continue
            if any(b.get("UserId") == CLIENT_ID and b.get("IdentityId") == ctx.binding_identity for b in bindings):
                return True
        return False
    bench.check("the replacement WA player binding reconverged on both replicas", all(bound(r) for r in finals.values()))
    bench.check("the stale-generation frame changed no regular-node replica",
                all(any(r.get("subsystem") == "probe" and r.get("payload") == "replacement" for r in rows_) for rows_ in finals.values()))
    bench.record("world-authority-global-state-failover", {**finals, "transport": transport})
    bench.record("world-authority-failover", {"oldGeneration": generation, "fencedGeneration": fenced_generation,
                                              "replacement": replacement, "stale": stale, "clocksBeforeFailover": clocks_before,
                                              "clocks": clocks, "spreadMilliseconds": spread})
    ctx.failover = {"old": generation, "fenced": fenced_generation, "replacement": replacement_generation}


# -- P2-WA-ECON legs -------------------------------------------------------------------------


def _wa_balance(bench, owner: int):
    return (_balance(bench, "wa-1", owner) or {}).get("balance")


def _store_payments(bench, ctx) -> None:
    payer, payee1, payee2 = (base + random.randrange(32768) for base in
                             (910000000000000000, 920000000000000000, 930000000000000000))
    for owner, balance in ((payer, 100), (payee1, 0), (payee2, 0)):
        _post(bench, "node-1", f"/banking-test-create?ownerId={owner}&balance={balance}")
    _require(bench, "the store payment accounts converged",
             _poll(lambda: _wa_balance(bench, payer) == 100 and _wa_balance(bench, payee1) == 0 and _wa_balance(bench, payee2) == 0,
                   HANDOVER_TIMEOUT))
    results = _parallel(
        lambda: _post(bench, "node-1", f"/banking-test-store-payment?payerId={payer}&payeeId={payee1}&amount=60&commit=true"),
        lambda: _post(bench, "node-2", f"/banking-test-store-payment?payerId={payer}&payeeId={payee2}&amount=60&commit=true"))
    bench.check("concurrent store payments produced exactly one committed purchase",
                sum(1 for r in results if isinstance(r, dict) and r.get("approved") and r.get("debit") and r.get("credit")) == 1,
                str(results)[:300])
    abort = _post(bench, "node-2", f"/banking-test-store-payment?payerId={payer}&payeeId={payee2}&amount=30&commit=false")
    _require(bench, "a failed store payment entered the compensation path",
             abort.get("approved") and abort.get("commit") is False and abort.get("debit") is False and abort.get("credit") is False,
             json.dumps(abort))

    def settled(payer_balance, payees):
        b1, b2 = _wa_balance(bench, payee1), _wa_balance(bench, payee2)
        return _wa_balance(bench, payer) == payer_balance and b1 is not None and b2 is not None and b1 + b2 == payees
    _require(bench, "the store payment commit/refund converged without overdraw", _poll(lambda: settled(40, 60), HANDOVER_TIMEOUT))
    # Asynchronous prefab purchase shape: the reservation is taken before the spawn and settled in its callback.
    deferred = _post(bench, "node-1", f"/banking-test-deferred-payment?payerId={payer}&payeeId={payee1}&amount=20&spawned=true")
    bench.check("a deferred store payment settled in the spawn callback",
                deferred.get("approved") and deferred.get("spawned") and deferred.get("settled"), json.dumps(deferred))
    failed = _post(bench, "node-2", f"/banking-test-deferred-payment?payerId={payer}&payeeId={payee2}&amount=20&spawned=false")
    bench.check("a failed prefab spawn entered the deferred refund path",
                failed.get("approved") and failed.get("spawned") is False and failed.get("settled") is False, json.dumps(failed))
    bench.check("the deferred store payment settle/refund converged", _poll(lambda: settled(20, 80), HANDOVER_TIMEOUT))
    bench.record("world-authority-store", {"race": [str(r) if isinstance(r, Exception) else r for r in results], "abort": abort,
                                           "deferred": deferred, "deferredFailed": failed,
                                           "payer": _balance(bench, "wa-1", payer), "payee1": _balance(bench, "wa-1", payee1),
                                           "payee2": _balance(bench, "wa-1", payee2)})


def _trade(bench, ctx) -> None:
    """WA-2: player trade / faction deposit-withdraw settlement. Vanilla's Transfer_Internal mutates both
    accounts locally after node-local validation; on a cluster node the debit-credit pair must settle
    atomically at the World Authority or the next replicated snapshot silently undoes the trade.
    banking-test-transfer drives vanilla's own Transfer_Server - the exact path a player trade and a
    faction deposit take - not the cluster seam directly."""
    source, target = 950000000000000000 + random.randrange(32768), 960000000000000000 + random.randrange(32768)
    _post(bench, "node-1", f"/banking-test-create?ownerId={source}&balance=100")
    _post(bench, "node-1", f"/banking-test-create?ownerId={target}&balance=10")
    _require(bench, "the trade settlement accounts converged",
             _poll(lambda: _balances(bench, source, 100, ("wa-1",)) and _balances(bench, target, 10, ("node-2",)), HANDOVER_TIMEOUT))
    # Success settles at the WA and is applied to the local replica inside the call, so the immediate
    # response already carries the settled balances.
    send = _post(bench, "node-1", f"/banking-test-transfer?from={source}&to={target}&amount=30")
    _require(bench, "a cross-node trade transfer settled", send.get("fromBalance") == 70 and send.get("toBalance") == 40, json.dumps(send))
    bench.check("the trade transfer balances converged across nodes",
                _poll(lambda: (_balance(bench, "node-2", source) or {}).get("balance") == 70 and _wa_balance(bench, target) == 40,
                      HANDOVER_TIMEOUT))
    overdraw = _post(bench, "node-2", f"/banking-test-transfer?from={source}&to={target}&amount=500")
    bench.check("an overdrawn trade transfer mutated nothing",
                overdraw.get("fromBalance") == 70 and overdraw.get("toBalance") == 40, json.dumps(overdraw))
    # Concurrency: 70 in the account, two simultaneous 60-credit trades from different nodes - the World
    # Authority must admit exactly one, leaving 10 and 100.
    path = f"/banking-test-transfer?from={source}&to={target}&amount=60"
    race = _parallel(lambda: _post(bench, "node-1", path), lambda: _post(bench, "node-2", path))
    bench.check("concurrent trade transfers settled to exactly one commit",
                _poll(lambda: _wa_balance(bench, source) == 10 and _wa_balance(bench, target) == 100, HANDOVER_TIMEOUT),
                f"from={_balance(bench, 'wa-1', source)} to={_balance(bench, 'wa-1', target)}")
    bench.record("world-authority-trade", {"send": send, "overdraw": overdraw,
                                           "race": [str(r) if isinstance(r, Exception) else r for r in race],
                                           "final": {"from": _balance(bench, "wa-1", source), "to": _balance(bench, "wa-1", target)}})


def _client_log_has(bench, client: int, text: str) -> bool:
    # The client's home survives between runs, so only the newest log is this run's.
    log = max(bench.run_root.glob(f"client-{client}/**/SpaceEngineers_*.log"), key=lambda path: path.stat().st_mtime)
    return text in log.read_text(errors="replace")


def _client_balance_events(bench, ctx) -> None:
    """Vanilla's balance events throw on a client that has no such account or too little to debit, and the client
    disconnects. A client on a cluster node gets there when it misses events while it switches nodes: an account
    it never heard of, or a stale balance. Client 1 must survive those, and a transfer of most of a balance, which
    the origin node's clients must get exactly once."""
    node = _client_node(bench)
    _require(bench, "a regular node hosts a living client 1", node)

    def account(balance: int) -> int:
        owner = 930000000000000000 + random.randrange(2 ** 40)
        _post(bench, node, f"/banking-test-create?ownerId={owner}&balance={balance}")
        _require(bench, "the client event account converged", _poll(lambda: _balances(bench, owner, balance), HANDOVER_TIMEOUT))
        return owner

    def survived(label: str) -> None:
        time.sleep(3)
        session = _session(bench, CLIENT_ID) or {}
        ctx.api.list_screens()
        bench.check(f"client 1 survived {label}", session.get("admissionState") == "Ready", json.dumps(session))

    source, target = account(100), account(0)
    send = _post(bench, node, f"/banking-test-transfer?from={source}&to={target}&amount=80")
    bench.check("a transfer of most of a balance settled", send.get("fromBalance") == 20 and send.get("toBalance") == 80, json.dumps(send))
    survived("a transfer of most of a balance")
    bench.check("client 1 applied the transfer once", not _client_log_has(bench, CLIENT_ID, "data do not match"))
    for kind, label in (("forget", "a debit of an account it never heard of"), ("stale", "a debit larger than its stale balance")):
        owner = account(100)
        _post(bench, node, f"/banking-test-client-desync?ownerId={owner}&kind={kind}")
        _post(bench, node, f"/banking-test-change?ownerId={owner}&amount=-60")
        _require(bench, f"the debit converged ({kind})", _poll(lambda: _balances(bench, owner, 40), HANDOVER_TIMEOUT))
        survived(label)
    source, target = account(100), account(0)
    _post(bench, node, f"/banking-test-client-desync?ownerId={source}&kind=forget")
    _post(bench, node, f"/banking-test-client-desync?ownerId={target}&kind=forget")
    _post(bench, node, f"/banking-test-transfer?from={source}&to={target}&amount=60")
    survived("a transfer between accounts it never heard of")


def _contract_activation(bench, ctx) -> None:
    """Contract activation takes a starting deposit that vanilla debits in Activate_Internal without
    re-checking the balance it validated in CanActivate_Internal, so two nodes could both pass and both
    debit. Drive a real contract through Activate on each node and require the WA to admit exactly one."""
    payer = 940000000000000000 + random.randrange(32768)
    _post(bench, "node-1", f"/banking-test-create?ownerId={payer}&balance=100")
    _require(bench, "the contract deposit account converged", _poll(lambda: _balances(bench, payer, 100, ("wa-1",)), HANDOVER_TIMEOUT))
    # The reservation is applied at the WA and replicates back asynchronously, so the balance the
    # activating node reads straight afterwards is not yet the settled one. Assert activation here and
    # the single debit through the convergence poll below.
    activate = _try(bench, "node-1", f"/contract-test-activate?payerId={payer}&deposit=40", "POST")
    overdraw = _try(bench, "node-2", f"/contract-test-activate?payerId={payer}&deposit=500", "POST")
    bench.check("contract activation reserved its starting deposit at the WA",
                isinstance(activate, dict) and activate.get("activated") and activate.get("state") == "Active", json.dumps(activate))
    bench.check("an over-limit contract activation was refused at the WA",
                isinstance(overdraw, dict) and overdraw.get("activated") is False and overdraw.get("state") == "Inactive",
                json.dumps(overdraw))
    bench.check("the contract deposit converged to a single debit", _poll(lambda: _wa_balance(bench, payer) == 60, HANDOVER_TIMEOUT))
    bench.record("world-authority-contract", {"activate": activate, "overdraw": overdraw, "final": _balance(bench, "wa-1", payer)})


def _contract_settlement(bench, ctx) -> None:
    """WA-2: terminal-state arbitration. Every node holds its own copy of a contract, and each copy can
    reach a terminal state independently - a player finishing on one node while another node (or the WA
    timeout sweep) fails the same contract. Both paths settle money, so the WA must admit exactly one
    Finish-or-Fail per contract id. Race Finish against Fail on the same id from different nodes."""
    payer = 970000000000000000 + random.randrange(32768)
    contract = -777000000000 - random.randrange(32768)
    _post(bench, "node-1", f"/banking-test-create?ownerId={payer}&balance=50")
    _require(bench, "the contract settlement account converged", _poll(lambda: _balances(bench, payer, 50, ("node-2",)), HANDOVER_TIMEOUT))
    base = f"/contract-test-settle?contractId={contract}&payerId={payer}&deposit=40&kind="
    finish, failed = _parallel(lambda: _post(bench, "node-1", base + "Finish"), lambda: _post(bench, "node-2", base + "Fail"))
    both = [finish, failed]
    _require(bench, "the contract terminal arbitration admitted exactly one settlement",
             all(isinstance(r, dict) for r in both) and sum(1 for r in both if r.get("settled")) == 1, str(both)[:300])
    # The loser must have moved nothing and stayed Active; a Finish loser reports vanilla's refusal code.
    bench.check("the losing settlement moved no state and no money",
                all(r.get("state") == "Active" and r.get("credited") == 0 for r in both if not r.get("settled")), json.dumps(both))
    # A replayed settlement of the same contract must be refused no matter which node retries it.
    retry = _post(bench, "node-2", base + "Finish")
    bench.check("a replayed contract settlement was refused", retry.get("settled") is False and retry.get("state") == "Active",
                json.dumps(retry))
    # If Finish won, the deposit came back to the payer at the WA; if Fail won (punishOwner false, NPC
    # contract), nothing moved. Either way the balance must converge and then hold.
    expected = 90 if finish.get("settled") else 50
    bench.check(f"the contract settlement balance converged to {expected}",
                _poll(lambda: _wa_balance(bench, payer) == expected, HANDOVER_TIMEOUT), json.dumps(_balance(bench, "wa-1", payer)))
    time.sleep(3)  # a late losing settlement would land inside this window
    final = _node(bench, "wa-1", f"/banking-test-status?ownerId={payer}")
    bench.check("no late losing settlement leaked money after convergence", final.get("balance") == expected, json.dumps(final))
    bench.record("world-authority-contract-settle", {"finish": finish, "fail": failed, "retry": retry, "final": final})


def _contract_split(bench, ctx) -> None:
    """WA-2: the contract reward SPLIT across owners hosted on different nodes. A real Finish with two
    owners runs the vanilla per-owner payout loop under the cluster's exactly-once arbitration; each
    owner must receive exactly reward/2 through the WA-routed ChangeBalance. The replay runs only after
    cluster-wide convergence so replica lag cannot masquerade as a double payment."""
    a, b = ctx.binding_identity, ctx.client2_identity
    contract = -888000000000 - random.randrange(32768)
    before_a = _wa_balance(bench, a) or 0
    before_b = _wa_balance(bench, b) or 0
    path = f"/contract-test-settle-split?contractId={contract}&ownerA={a}&ownerB={b}&reward=300"
    result = _post(bench, "node-1", path)
    # creditedA/B are informational: ChangeBalance is WA-routed and the settling node's replica lags,
    # so the exact +150s are asserted through the convergence poll below.
    _require(bench, "the cross-node contract reward split was admitted",
             result.get("settled") is True and result.get("outcome") == "Success", json.dumps(result))
    bench.check("the split reward converged cluster-wide",
                _poll(lambda: _wa_balance(bench, a) == before_a + 150
                      and ((_balance(bench, "node-2", b) or {}).get("balance") or 0) == before_b + 150, HANDOVER_TIMEOUT),
                f"a={_wa_balance(bench, a)} want {before_a + 150}, b={(_balance(bench, 'node-2', b) or {}).get('balance')} want {before_b + 150}")
    replay = _post(bench, "node-2", path)
    bench.check("a replayed split settlement was refused", replay.get("settled") is False, json.dumps(replay))
    time.sleep(3)  # a late replay would land inside this window
    hold_a, hold_b = _wa_balance(bench, a), _wa_balance(bench, b)
    bench.check("no late split replay leaked money", hold_a == before_a + 150 and hold_b == before_b + 150, f"a={hold_a} b={hold_b}")
    bench.record("world-authority-contract-split", {"result": result, "replay": replay, "balances": {
        "beforeA": before_a, "beforeB": before_b, "afterA": hold_a, "afterB": hold_b}})


def _pcu_ledger(bench, ctx) -> None:
    """WA-2: global PCU ledger. Each node's MyBlockLimits only counts its own partitions, so the world's
    GLOBALLY pool (TotalPCU) could be exceeded once per node. Nodes report usage every second, the WA
    merges and replicates, and CheckLimits joins the other nodes' usage into every verdict. Inject a
    large accounting commit on node-2 - exactly what a finished build records - and node-1's verdict for
    a build its local ledger alone would allow must flip to a PCU refusal, then recover after the teardown."""
    baseline = _node(bench, "node-1", "/pcu-test-status?probe=600000")
    _require(bench, "the PCU baseline verdict is Passed", baseline.get("verdict") == "Passed", json.dumps(baseline))
    remote_base = baseline["ledger"]["remotePcuBuilt"]
    build = _post(bench, "node-2", "/pcu-test-build?pcu=900000")
    _require(bench, "the PCU probe build was committed", build.get("pcu") == 900000, json.dumps(build))
    last = {}

    def verdict(predicate):
        last["status"] = _node(bench, "node-1", "/pcu-test-status?probe=600000")
        return last["status"] if predicate(last["status"]) else None
    try:
        refused = _poll(lambda: verdict(lambda s: s.get("verdict") == "PCU" and s["ledger"]["remotePcuBuilt"] >= remote_base + 900000
                                        and s.get("localRemaining", 0) >= 600000), 30)
        bench.check("remote PCU usage reached node-1's verdict", refused, json.dumps(last.get("status")))
    finally:
        _post(bench, "node-2", "/pcu-test-build?pcu=-900000")
    recovered = _poll(lambda: verdict(lambda s: s.get("verdict") == "Passed" and s["ledger"]["remotePcuBuilt"] <= remote_base), 30)
    bench.check("the PCU verdict recovered after the probe teardown", recovered, json.dumps(last.get("status")))
    bench.record("world-authority-pcu", {"baseline": baseline, "build": build, "refused": refused, "recovered": recovered})


def _pcu_simultaneous(bench, ctx) -> None:
    """WA-2: SIMULTANEOUS PCU changes for one owner on both nodes. Five concurrent probe builds fire on
    each node at once (ten in flight); each node serializes its own commits on the game thread, reports
    its own local total, and the WA merge must sum them without losing or double-counting an update.
    Then the mirror-image concurrent teardown must return every view exactly to its baseline."""
    owner = _node(bench, "node-1", "/pcu-test-status")["owner"]
    # Anchor grids first: the probe build attributes its accounting to some grid loaded locally, and
    # node-1 can transiently hold NONE at this point (earlier legs hand its partitions over - a run
    # failed exactly this way). The anchors change the owner's PCU, so baselines are captured after
    # them and only once both views hold still.
    _post(bench, "node-1", f"/igc-test-ensure-named-grid?owner={owner}&name=cluster-pcu-anchor-1&lateral=48000&radius=15000")
    _post(bench, "node-2", f"/igc-test-ensure-named-grid?owner={owner}&name=cluster-pcu-anchor-2&lateral=90000&radius=15000")
    base, previous = {}, {"sample": None}

    def stable():
        for node_id in ("node-1", "node-2"):
            base[node_id] = _node(bench, node_id, "/pcu-test-status")
        sample = [(base[n]["localPcuBuilt"], base[n]["ledger"]["remotePcuBuilt"]) for n in ("node-1", "node-2")]
        held = sample == previous["sample"]
        previous["sample"] = sample
        return held
    _require(bench, "the PCU baselines stabilized after the anchor grids", _poll(stable, 20))
    _require(bench, "the PCU probe owner is the same on both nodes", base["node-2"].get("owner") == owner,
             f"{owner} vs {base['node-2'].get('owner')}")
    now = {}

    def exact(offset):
        for node_id in ("node-1", "node-2"):
            now[node_id] = _node(bench, node_id, "/pcu-test-status")
        return all(now[n]["localPcuBuilt"] == base[n]["localPcuBuilt"] + offset
                   and now[n]["ledger"]["remotePcuBuilt"] == base[n]["ledger"]["remotePcuBuilt"] + offset for n in now)

    def burst(pcu):
        calls = [lambda n=n: _post(bench, n, f"/pcu-test-build?owner={owner}&pcu={pcu}") for _ in range(5) for n in ("node-1", "node-2")]
        return [r for r in _parallel(*calls) if isinstance(r, Exception)]
    errors = burst(120000)
    _require(bench, "every concurrent PCU probe build succeeded", not errors, str(errors)[:300])
    converged = _poll(lambda: exact(600000), 30)
    converged_view = dict(now)
    bench.check("simultaneous PCU builds converged to the exact sum", converged, json.dumps(now)[:400])
    errors = burst(-120000)
    _require(bench, "every concurrent PCU probe teardown succeeded", not errors, str(errors)[:300])
    bench.check("the simultaneous PCU teardown restored the baselines exactly", _poll(lambda: exact(0), 30), json.dumps(now)[:400])
    bench.record("world-authority-pcu-simultaneous", {"owner": owner, "converged": converged_view, "restored": now})


def _anchor(bench, node_id: str) -> tuple[float, float, float]:
    registry = _node(bench, node_id, "/position-registry")
    entry = next((row for row in registry if row.get("ClientId") == CLIENT_ID), None)
    _require(bench, "the position registry has an entry for client 1", entry)
    return entry["X"], entry["Y"], entry["Z"]


def _grid_transfer(bench, ctx) -> None:
    """WA-2 cross-node grid ownership (PCU authorship) transfer: client 1's node holds the grid and the
    transfer transaction; client 2 is attached to the OTHER node. Vanilla's directed confirm-dialog
    event would drop silently and the acceptance would land on the wrong node - the cluster forwards
    the offer to the client's real node (vanilla dialog included) and the acceptance back to the grid
    node, where vanilla's transaction check makes replays no-ops."""
    home = _client_node(bench)
    _require(bench, "a regular node hosts a living client 1", home)
    other = _other(home)
    x, y, z = _anchor(bench, home)
    old = (_node(bench, home, f"/respawn-test-player?userId={CLIENT_ID}") or {}).get("identityId") or 0
    new = (_node(bench, other, f"/respawn-test-player?userId={CLIENT2_ID}") or {}).get("identityId") or 0
    _require(bench, "the transfer identities resolved", old > 0 and new > 0, f"old={old} new={new}")
    grid = _post(bench, home, f"/trash-test-spawn?x={x + 250}&y={y}&z={z}&builtBy={old}&static=true").get("grid") or 0
    _require(bench, "the transfer probe grid spawned", grid > 0)
    offer = _poll(lambda: (lambda o: o if o.get("requested") is True else None)(
        _post(bench, home, f"/transfer-test-offer?grid={grid}&oldOwner={old}&newOwner={new}")), 30)
    _require(bench, "the grid transfer offer was requested (BlocksBuiltByGrid present)", offer)
    dialog = _wait_screen(ctx.api2, "MyGuiScreenMessageBox", HANDOVER_TIMEOUT)
    _require(bench, "the transfer confirm dialog reached client 2 on the other node", dialog is not None)
    ctx.api2.control_click(text="Yes", screen=dialog)
    state = {}

    def moved():
        state.update(_node(bench, home, f"/transfer-test-state?grid={grid}"))
        authorship = state.get("authorship") or {}
        return state.get("exists") and authorship.get(str(new), 0) >= 1 and authorship.get(str(old), 0) == 0
    _require(bench, "the accepted cross-node transfer moved authorship", _poll(moved, HANDOVER_TIMEOUT), json.dumps(state))
    # Replayed acceptance: the vanilla transfer transaction is already finished, so nothing moves twice.
    _post_json(bench, home, "/transfer-accept-execute", {"GridEntityId": grid, "OldOwner": old, "NewOwner": new,
                                                          "NewOwnerSteamId": CLIENT2_ID})
    after = _node(bench, home, f"/transfer-test-state?grid={grid}")
    bench.check("a replayed acceptance left authorship intact", (after.get("authorship") or {}).get(str(new), 0) >= 1, json.dumps(after))
    bench.record("world-authority-transfer", {"grid": grid, "oldOwner": old, "newOwner": new, "offer": offer,
                                              "afterAccept": state, "afterReplay": after})


# -- P2-WA-03 legs ---------------------------------------------------------------------------


def _resolved(bench, operation: str, timeout: float):
    def probe():
        for node_id in ("node-1", "node-2"):
            result = _try(bench, node_id, f"/spawn-result?operation={operation}")
            if isinstance(result, dict) and result.get("Pending") is False and result.get("Error") != "unknown operation":
                return result
        return None
    return _poll(probe, timeout)


def _spawn_director(bench, ctx) -> None:
    """WA-3: the World Authority hosts no clients, so a spawn decision must be routed to the node owning
    the target player and executed exactly once there. A deliberately unknown prefab keeps the first
    assertions on routing and idempotence without depending on game content."""
    operation = str(uuid.uuid4())
    dispatch = _post(bench, "wa-1", f"/spawn-test-dispatch?prefab=ClusterSpawnDirectorProbe&operationId={operation}")
    _require(bench, "the spawn dispatch resolved a cluster-wide target and reported the owning node's result",
             dispatch.get("operationId") == operation and (dispatch.get("client") or 0) > 0 and (dispatch.get("target") or 0) > 0
             and dispatch.get("Success") is False, json.dumps(dispatch))
    # Spawning is asynchronous, and an unknown prefab never reaches the completion callback at all, so the
    # owning node resolves this one through its pending timeout. Wait for the recorded outcome.
    recorded = _resolved(bench, operation, 60)
    _require(bench, "the dispatched spawn resolved on the owning node", recorded)
    bench.check("an unknown prefab did not report a successful spawn", recorded.get("Success") is False, json.dumps(recorded))
    replay = _post(bench, "wa-1", f"/spawn-test-dispatch?prefab=ClusterSpawnDirectorProbe&operationId={operation}")
    bench.check("a replayed spawn operation returned the recorded result",
                replay.get("Success") == recorded.get("Success") and replay.get("Pending") is False
                and replay.get("GridEntityIds") == recorded.get("GridEntityIds"), json.dumps(replay))
    bench.check("the WA executed no spawn locally", _node(bench, "wa-1", "/spawn-status").get("executions") == 0)
    # The target's owning node is whichever one currently routes that client, so assert the invariant that
    # matters: exactly one regular node executed the operation.
    counts = [(_node(bench, n, "/spawn-status").get("executions") or 0) for n in ("node-1", "node-2")]
    bench.check("the dispatched spawn executed on exactly one node", sum(counts) == 1, f"node1={counts[0]} node2={counts[1]}")
    # The checks above prove routing and idempotence but never that a grid appears, because the probe
    # prefab does not exist. Repeat with a prefab this world really carries.
    prefabs = _node(bench, "wa-1", "/spawn-prefabs?limit=8")
    _require(bench, "the WA reported prefab definitions", prefabs.get("count", 0) > 0)
    # Whether any given prefab spawns depends on placement, so try the candidates in order rather than
    # betting the gate on the first one. Offset well clear of the target so placement is unlikely to
    # land inside terrain. Prefab subtypes contain quotes, parentheses and spaces: percent-encode them.
    attempts, real = [], None
    for candidate in prefabs.get("prefabs") or []:
        if not candidate:
            continue
        candidate_operation = str(uuid.uuid4())
        encoded = urllib.parse.quote(candidate, safe="")
        candidate_dispatch = _try(bench, "wa-1", f"/spawn-test-dispatch?prefab={encoded}&operationId={candidate_operation}&offset=5000",
                                  "POST")
        result = _resolved(bench, candidate_operation, 40)
        attempts.append(f"{candidate}\t{json.dumps(candidate_dispatch) if candidate_dispatch else '<no dispatch>'}\t"
                        f"{json.dumps(result) if result else '<unresolved>'}")
        if isinstance(result, dict) and result.get("Success") is True and result.get("GridEntityIds"):
            real = (candidate, encoded, candidate_operation, result)
            break
    bench.record("world-authority-spawn-attempts.txt", "\n".join(attempts))
    _require(bench, "a dispatched prefab spawned a grid on the owning node", real, json.dumps(prefabs.get("prefabs")))
    prefab, encoded, real_operation, result = real
    grid = result["GridEntityIds"][0]
    views = {n: _try(bench, n, f"/spawn-grid?grid={grid}") or {} for n in ("node-1", "node-2", "wa-1")}
    bench.check("the dispatched grid materialised on exactly one regular node",
                sum(1 for n in ("node-1", "node-2") if views[n].get("exists")) == 1, json.dumps(views))
    bench.check("the WA did not materialise the dispatched grid", views["wa-1"].get("exists") is False, json.dumps(views["wa-1"]))
    # Replaying the operation must return the recorded grid immediately, not spawn a second copy.
    real_replay = _post(bench, "wa-1", f"/spawn-test-dispatch?prefab={encoded}&operationId={real_operation}&offset=5000")
    bench.check("a replayed prefab spawn returned the recorded grid",
                real_replay.get("Success") is True and real_replay.get("Pending") is False
                and real_replay.get("GridEntityIds") == result.get("GridEntityIds"), json.dumps(real_replay))
    bench.record("world-authority-spawn-director", {"dispatch": dispatch, "replay": replay, "real": {
        "prefab": prefab, "dispatch": result, "replay": real_replay, "grid": views}})


def _counts(bench, route: str, key: str) -> dict:
    return {n: _node(bench, n, route)[key] for n in ("node-1", "node-2", "wa-1")}


def _only_on(bench, label: str, node: str, before: dict, after: dict) -> None:
    other = _other(node) if node in ("node-1", "node-2") else None
    bench.check(f"{label} spawned only on its owning node {node}",
                other is not None and after[node] > before[node] and after[other] == before[other], f"before={before} after={after}")


def _drop_container(bench, ctx) -> None:
    """WA-3: the vanilla drop-container generator wired to the director. The WA hosts no clients, so its
    own UpdateContainerSpawner sees an empty player list and decides nothing; the decision has to come
    from the cluster-wide player set and the spawn has to run on the node owning that player, because
    the grid, its drop component, its GPS and its removal timer all live with the player."""
    before = _counts(bench, "/drop-container-grids", "count")
    dispatch = _post(bench, "wa-1", f"/drop-container-dispatch?identity={ctx.binding_identity}")
    _require(bench, "the WA drop container dispatch was accepted", dispatch.get("Spawned") is True, json.dumps(dispatch))
    status = _node(bench, "wa-1", "/drop-container-status")
    last = (status.get("dispatches") or [{}])[-1]
    node = last.get("node") or ""
    _require(bench, "the WA recorded the drop container dispatch", node)
    after = {}

    # The prefab manager finishes asynchronously, so poll for the grid rather than reading it straight back.
    def grew():
        after.update(_counts(bench, "/drop-container-grids", "count"))
        return after["node-1"] + after["node-2"] > before["node-1"] + before["node-2"]
    _require(bench, "the dispatched drop container materialised on a regular node", _poll(grew, 40))
    bench.check("the WA did not spawn the drop container locally", after["wa-1"] == before["wa-1"])
    _only_on(bench, "the drop container", node, before, after)
    if node in ("node-1", "node-2"):
        # Exactly-once: the owning node replays the recorded outcome for the same operation id.
        replay = _node(bench, node, f"/drop-container-result?operation={last.get('operation')}")
        bench.check("the owning node recorded the drop container exactly once",
                    replay.get("Spawned") is True and replay.get("Error") is None, json.dumps(replay))
    else:
        replay = None
    bench.record("world-authority-drop-container", {"dispatch": dispatch, "status": status, "replay": replay, "node": node,
                                                    "grids": {"before": before, "after": after}})


def _dispatch(bench, kind: str | None, accepted, attempts: int = 5, pause: float = 3.0):
    path = "/encounter-dispatch" + (f"?kind={kind}" if kind else "")
    for _ in range(attempts):
        result = _try(bench, "wa-1", path, "POST")
        if isinstance(result, dict) and accepted(result):
            return result
        time.sleep(pause)
    return None


def _last_dispatch_node(bench) -> str:
    return ((_node(bench, "wa-1", "/encounter-status").get("dispatches") or [{}])[-1]).get("node") or ""


def _encounters(bench, ctx) -> None:
    """WA-3: the vanilla global-encounter generator wired to the cluster. Vanilla's spawn tick returns at
    GetOnlineHumanPlayerCount() == 0, which is always true on the WA, so a cluster never spawned one.
    The WA decides which encounter is due and near whom; the owning node runs vanilla's own position
    pickers and spawn helpers, because that is where the planets, the player and a partition are."""
    before = _counts(bench, "/encounter-count", "count")
    partitions_before = {row["Partition"] for row in _owners(bench)}
    # Vanilla's position pickers legitimately refuse now and then, and a dispatch can 500 transiently
    # ("No cluster player to spawn an encounter near") while the position registry catches up with a
    # control change; the real generator just tries again next timer tick. Mirror that.
    dispatch = _dispatch(bench, None, lambda r: r.get("Spawned") is True, pause=5.0)
    _require(bench, "the WA global encounter dispatch spawned within five attempts", dispatch,
             json.dumps(_try(bench, "wa-1", "/encounter-status")))
    status = _node(bench, "wa-1", "/encounter-status")
    node = ((status.get("dispatches") or [{}])[-1]).get("node") or ""
    _require(bench, "the WA recorded the global encounter dispatch", node in ("node-1", "node-2"), node)
    after = {}

    def grew():
        after.update(_counts(bench, "/encounter-count", "count"))
        return after["node-1"] + after["node-2"] > before["node-1"] + before["node-2"]
    _require(bench, "the dispatched global encounter materialised on a regular node", _poll(grew, 40))
    bench.check("the WA did not spawn the global encounter locally", after["wa-1"] == before["wa-1"])
    # Exactly once cluster-wide: the other regular node must not have spawned its own copy.
    _only_on(bench, "the global encounter", node, before, after)
    bench.record("world-authority-encounter", {"dispatch": dispatch, "status": status, "node": node,
                                               "encounters": {"before": before, "after": after}})

    # WA-3: a global encounter lands on a random sphere around the WORLD CENTER, far from any owned
    # partition. The landed grids must join or create a partition on the executing node (fresh
    # partitions self-register with the gateway) and be catalogued - otherwise the encounter is
    # invisible to handover and lost on the first node crash.
    grid = _poll(lambda: ((_node(bench, node, "/encounter-grids").get("grids") or [{}])[0]).get("entityId"), HANDOVER_TIMEOUT)
    _require(bench, f"the spawned global encounter exposed grids on {node}", grid)
    partition = _poll(lambda: _node(bench, node, f"/partition-of?entity={grid}").get("partition") or None, HANDOVER_TIMEOUT)
    _require(bench, f"the global encounter grid {grid} joined a partition on {node}", partition)
    branch = "joined-existing" if partition in partitions_before else "fresh-partition"
    _require(bench, f"the registry shows encounter partition {partition} owned by {node}",
             _poll(lambda: any(r["Partition"] == partition and r["Node"] == node for r in _owners(bench)), HANDOVER_TIMEOUT))

    def sequence():
        # saves/newest answers 404 until the fresh partition's FIRST save is catalogued.
        try:
            return (bench.cluster.get(f"/registry/saves/newest?partition={partition}").get("SaveId") or {}).get("Sequence", 0)
        except ClusterError:
            return 0
    sequence_before = sequence()
    # 409 busy = a capture for this partition is already in flight, which is exactly what this POST
    # exists to force - the sequence poll below proves it lands either way. Retry transient refusals
    # (game-thread timeouts under sweep load) instead of failing on one.
    url = f"http://{_nodes(bench)[node].control}/cluster/save-capture?partition={partition}"
    codes = []

    def capture():
        try:
            code = _status("POST", url)[0]
        except OSError as error:
            code = str(error)
        codes.append(code)
        return code in (200, 409)
    _require(bench, f"a save capture for encounter partition {partition} was accepted (200/409)", _poll(capture, HANDOVER_TIMEOUT),
             str(codes[-3:]))
    bench.check(f"encounter partition {partition} was catalogued", _poll(lambda: sequence() > sequence_before, HANDOVER_TIMEOUT))

    # Placement retry: vanilla position selection legitimately refuses (TestPlaceInSpace can fail all
    # its probes). The executor re-rolls up to 3 attempts inside ONE operation. Force 2 refusals on both
    # candidate nodes and prove a single dispatch still spawns exactly one encounter, with the refusals
    # visible in the executing node's log.
    for node_id in ("node-1", "node-2"):
        _post(bench, node_id, "/encounter-test-place-fail?count=2")
    try:
        retry_before = _counts(bench, "/encounter-count", "count")
        retry = _dispatch(bench, None, lambda r: r.get("Spawned") is True)
    finally:
        for node_id in ("node-1", "node-2"):
            _post(bench, node_id, "/encounter-test-place-fail?count=0")
    _require(bench, "the placement-retry dispatch spawned", retry, json.dumps(_try(bench, "wa-1", "/encounter-status")))
    retry_node = _last_dispatch_node(bench)
    _require(bench, "the WA routed the placement-retry dispatch to a regular node", retry_node in ("node-1", "node-2"), retry_node)
    forced = len(_lines(bench, retry_node, r"encounter placement retry .* forced=true"))
    bench.check(f"{retry_node} logged the 2 forced placement refusals", forced >= 2, f"{forced} found")
    expected = retry_before[retry_node] + 1
    count = lambda: _node(bench, retry_node, "/encounter-count")["count"]  # noqa: E731
    _require(bench, f"the placement-retry encounter materialised on {retry_node}", _poll(lambda: count() >= expected, HANDOVER_TIMEOUT))
    bench.check("the placement-retry dispatch spawned exactly one encounter", count() == expected, f"{count()} vs {expected}")
    bench.record("world-authority-encounter-partition", {"branch": branch, "partition": partition, "grid": grid, "node": node,
                                                         "catalogued": True, "retry": {"dispatch": retry, "node": retry_node,
                                                                                       "forcedRefusals": forced}})


def _planetary(bench, ctx) -> None:
    """Planetary installations need no new vanilla seam: SpawnAroundPosition is already public, so the WA
    supplies only the target and vanilla picks the planet, spawn group and site from the player's
    position - all of which need the planet loaded, which is true on the owning node and never on the
    WA. The target's ControlledEntity is legitimately null in transient windows, so retry."""
    dispatch = _dispatch(bench, "planetary", lambda r: r.get("Error") is None)
    bench.check("the WA planetary encounter dispatch succeeded within five attempts", dispatch,
                json.dumps(_try(bench, "wa-1", "/encounter-status")))
    bench.record("world-authority-planetary-encounter", {"dispatch": dispatch, "status": _try(bench, "wa-1", "/encounter-status")})


def _cargo(bench, ctx) -> None:
    """WA-3 cargo ships: the SpawnCargoShip global event ticks only on the WA, where vanilla's
    random-online-player pick is always empty. The WA dispatches the decided spawn group to the node
    owning a cluster player, which runs vanilla placement with the target pinned. The prefab spawns in
    an asynchronous callback, so the tracked-ship count (cargoShips) is the convergence signal."""
    before = _counts(bench, "/encounter-count", "cargoShips")
    dispatch = _dispatch(bench, "cargo", lambda r: r.get("Spawned") is True)
    _require(bench, "the WA cargo dispatch spawned within five attempts", dispatch, json.dumps(_try(bench, "wa-1", "/encounter-status")))
    node = _last_dispatch_node(bench)
    _require(bench, "the WA recorded the cargo dispatch", node)
    after = {}

    def grew():
        after.update(_counts(bench, "/encounter-count", "cargoShips"))
        return after["node-1"] + after["node-2"] > before["node-1"] + before["node-2"]
    _require(bench, "the dispatched cargo ship materialised on a regular node", _poll(grew, 60))
    bench.check("the WA did not spawn the cargo ship locally", after["wa-1"] == before["wa-1"])
    _only_on(bench, "the cargo ship", node, before, after)
    bench.record("world-authority-cargo", {"dispatch": dispatch, "node": node, "cargoShips": {"before": before, "after": after}})


def _meteor(bench, ctx) -> None:
    """WA-3 meteor waves: per-player MeteorWave events only activate on player join - never true on the
    WA - so the WA keeps its own registry-driven schedule and dispatches each due wave to the owning
    node, which runs vanilla's restriction check, radii and amount at the player's real position. The
    wave spawns synchronously inside the dispatch; EncountersAfter carries the meteor count."""
    dispatch = _dispatch(bench, "meteor", lambda r: r.get("Spawned") is True and (r.get("EncountersAfter") or 0) > 0)
    _require(bench, "the WA meteor dispatch spawned within five attempts", dispatch,
             json.dumps(_try(bench, "wa-1", "/encounter-status")))
    node = _last_dispatch_node(bench)
    bench.check("the WA routed the meteor wave to a regular node", node in ("node-1", "node-2"), node)
    bench.record("world-authority-meteor", {"dispatch": dispatch, "node": node})


def _encounter_cadence(bench, ctx) -> None:
    """WA-3 production cadence: the WA's OWN encounter sweep fires with no dispatch knob involved. The
    test knob only shrinks the GlobalEncounterTimer-derived interval (self-expiring); the decision,
    target selection, spawn-group pick, owner routing and node execution are the production loop's.
    Sweep dispatches legitimately refuse (vanilla position pickers), so the poll window spans several
    12 s sweeps and requires one spawned dispatch."""
    dispatches_before = len(_node(bench, "wa-1", "/encounter-status").get("dispatches") or [])
    total = lambda: sum(_node(bench, n, "/encounter-count")["count"] for n in ("node-1", "node-2"))  # noqa: E731
    count_before = total()
    armed = _post(bench, "wa-1", "/encounter-test-interval?ms=12000")
    _require(bench, "the encounter-interval test knob armed", armed.get("intervalMs") == 12000, json.dumps(armed))
    status = {}
    try:
        def fired():
            status.update(_node(bench, "wa-1", "/encounter-status"))
            fresh = (status.get("dispatches") or [])[dispatches_before:]
            return any(row.get("spawned") is True for row in fresh)
        _require(bench, "the WA's own encounter sweep fired with a 12 s interval", _poll(fired, 120, interval=2),
                 json.dumps(status)[:400])
    finally:
        _post(bench, "wa-1", "/encounter-test-interval?ms=0")
    counts = {}
    bench.check("the scheduled encounter materialised",
                _poll(lambda: counts.update(after=total()) or counts["after"] > count_before, 40),
                f"before={count_before} now={counts.get('after')}")
    bench.record("wa3-encounter-cadence", {"automaticDispatches": (status.get("dispatches") or [])[dispatches_before:],
                                           "encountersBefore": count_before, "encountersAfter": counts.get("after")})


def _trash_safety(bench, ctx) -> None:
    """WA-3 cross-node trash-removal safety. Grid/voxel trash sweeps are gated to regular nodes (the WA
    hosts every grid only as a replication proxy; spawner/encounter protections exist solely on the
    owning node, so a WA-local sweep could close proxies the owner keeps) and identity cleanup to the
    WA. Two probe grids are genuine trash under the world's Default flags except for player distance:
    the near one (150 m from client 1) must survive on its owning node AND read as player-protected on
    the WA - which has ZERO local players, so only the cluster position registry can protect it - while
    the far one (700 m out, beyond the 500 m threshold) must be really removed by the owner's sweep."""
    # Probes must spawn on the node owning the space around the player, or the partition machinery culls them.
    home = _client_node(bench)
    _require(bench, "a regular node hosts a living client 1 for the trash leg", home)
    x, y, z = _anchor(bench, home)
    near = _post(bench, home, f"/trash-test-spawn?x={x + 150}&y={y}&z={z}").get("grid") or 0
    far = _post(bench, home, f"/trash-test-spawn?x={x + 700}&y={y}&z={z}").get("grid") or 0
    _require(bench, "the trash probe grids spawned", near > 0 and far > 0)
    far_state = {}
    bench.check("the far trash probe was removed on its owning node",
                _poll(lambda: far_state.update(_node(bench, home, f"/trash-test-state?entityId={far}")) or far_state.get("exists") is False, 60),
                json.dumps(far_state))
    near_state = _node(bench, home, f"/trash-test-state?entityId={near}")
    bench.check("the near trash probe survived on its owning node",
                near_state.get("exists") is True and near_state.get("markedAsTrash") is False, json.dumps(near_state))
    # Cross-node protection proof: the OTHER regular node (which hosts no player) and the WA (which never
    # hosts any) must both report the near position as player-protected - only the replicated position
    # registry can tell them that - and the far position as unprotected. The verdict consults ONLY the
    # replicated registry; localPlayers is informational (client 2 counts as an online player wherever
    # it is attached even though it never spawns a character).
    near_other = _node(bench, _other(home), f"/trash-test-close?x={x + 150}&y={y}&z={z}")
    bench.check("the other regular node registry-protects the near position",
                near_other.get("close") is True and (near_other.get("registryPlayers") or 0) > 0, json.dumps(near_other))
    near_wa = _node(bench, "wa-1", f"/trash-test-close?x={x + 150}&y={y}&z={z}")
    bench.check("the World Authority registry-protects the near position",
                near_wa.get("close") is True and (near_wa.get("registryPlayers") or 0) > 0 and near_wa.get("localPlayers") == 0,
                json.dumps(near_wa))
    far_wa = _node(bench, "wa-1", f"/trash-test-close?x={x + 700}&y={y}&z={z}")
    bench.check("the far position is not player-protected on the World Authority", far_wa.get("close") is False, json.dumps(far_wa))
    # #1307: the other regular node hosts no player, and vanilla alone keeps every grid there ("nobody online").
    # With client 1 flying on the home node, one server would trash a far probe, so this node must too. The
    # automatic loop is paused for the whole lane (_founder), so the probe stays where it spawned.
    lone = _post(bench, _other(home), f"/trash-test-spawn?x={x + 60000}&y={y}&z={z}").get("grid") or 0
    _require(bench, "the lone trash probe spawned on the node without players", lone > 0)
    lone_state = {}
    bench.check("the node without players removed the far trash probe",
                _poll(lambda: lone_state.update(_node(bench, _other(home), f"/trash-test-state?entityId={lone}"))
                      or lone_state.get("exists") is False, 60),
                json.dumps(lone_state))
    selftest = _node(bench, _other(home), "/self-test?section=trash-remote-players")
    bench.check("node self-test section `trash-remote-players` passes",
                bool(selftest.get("ok")) and "trash-remote-players" in (selftest.get("ran") or []), json.dumps(selftest)[:400])
    bench.record("world-authority-trash", {"nearProbe": near, "farProbe": far, "nearOwner": near_state, "nearOtherNode": near_other,
                                           "nearWa": near_wa, "farWa": far_wa, "loneProbe": lone, "loneOtherNode": lone_state})


def _identity_trash(bench, ctx) -> None:
    """WA-3: WA-side identity removal replicates. Create a throwaway identity, converge it everywhere,
    remove it through the vanilla abandoned-identity cleanup on the WA (the production path minus only
    the logout timer), and every role must drop it via the identity snapshot."""
    name = f"phase2-wa3-trash-identity-{random.randrange(32768)}"
    identity = _post(bench, "node-1", f"/identity-test-create?name={name}").get("identityId") or 0
    _require(bench, "the identity-trash leg created its identity", identity > 0)
    seen = {}

    def exists_everywhere(value):
        for node_id in ("node-1", "node-2", "wa-1"):
            seen[node_id] = (_try(bench, node_id, f"/identity-test-status?identityId={identity}") or {}).get("exists")
        return all(v is value for v in seen.values())
    _require(bench, "the trash-leg identity converged before removal", _poll(lambda: exists_everywhere(True), 30), json.dumps(seen))
    removed = _post(bench, "wa-1", f"/identity-trash-test?identityId={identity}")
    _require(bench, "the WA removed the abandoned identity", removed.get("removed") is True, json.dumps(removed))
    bench.check("the removed identity vanished on every role", _poll(lambda: exists_everywhere(False), 30), json.dumps(seen))
    bench.record("world-authority-identity-trash", {"identityId": identity, "removed": True})


# -- P2-MOD legs -----------------------------------------------------------------------------


def _received(bench, node_id: str, address: int) -> dict:
    return _node(bench, node_id, f"/igc-test-received?address={address}")


def _listener_carried(bench, node_id: str, origin: str, tag: str) -> bool:
    status = _node(bench, node_id, "/igc-status")
    return any(tag in (row.get("Tags") or []) for row in status.get("listeners") or [] if row.get("Origin") == origin)


def _igc_hosts(bench, owner: int) -> SimpleNamespace:
    """One antenna-carrying cluster-igc-host station per regular node, owned by ``owner``, each with a
    programmable block; returns once the endpoint directory and antenna reachability have converged.

    WA-4: vanilla resolves an addressee only among blocks loaded on the calling node and delivers a
    broadcast only to local listeners, so both stop at the partition boundary. The world carries no
    programmable blocks, so one is placed on each station and driven through the same
    IMyIntergridCommunicationSystem API a script would. Each node provisions its OWN station owned by
    the player identity - identity 0 reaches no remote broadcaster, and an incidental world grid can
    carry a hostile owner (picking "any grid with a working antenna" once selected a hostile world grid
    and the unicast was refused as Enemies)."""
    ensured = {n: _post(bench, n, f"/igc-test-ensure-grid?owner={owner}") for n in ("node-1", "node-2")}
    bench.record("world-authority-igc-ensure", ensured)
    grid1, grid2 = ensured["node-1"].get("grid"), ensured["node-2"].get("grid")
    _require(bench, "IGC host provisioning returned a grid on each node", grid1 and grid2, json.dumps(ensured))
    # An antenna only counts once it is working, which takes a moment after load while power settles.
    ready = _poll(lambda: grid1 in [g["id"] for g in _node(bench, "node-1", "/igc-endpoints").get("grids") or []]
                  and grid2 in [g["id"] for g in _node(bench, "node-2", "/igc-endpoints").get("grids") or []], HANDOVER_TIMEOUT)
    if not ready:
        # The antennas diagnostic tells a missing grid from a present-but-dead antenna (unpowered,
        # disabled, converted to a ship, damaged).
        bench.record("world-authority-igc-endpoints", {n: _try(bench, n, "/igc-endpoints") for n in ("node-1", "node-2")})
    _require(bench, "the provisioned IGC host grids reached a working antenna", ready, f"node1={grid1} node2={grid2}")
    _require(bench, "the two IGC endpoints are on different grids", grid1 != grid2)
    pb1 = _post(bench, "node-1", f"/igc-test-add-pb?grid={grid1}").get("address")
    pb2 = _post(bench, "node-2", f"/igc-test-add-pb?grid={grid2}").get("address")
    _require(bench, "programmable blocks were placed for the IGC test", pb1 and pb2, f"node1={pb1} node2={pb2}")

    # Each node reports its endpoints once a second and the WA merges them; a send cannot resolve a
    # remote addressee before that directory has converged.
    def directory():
        return all({pb1, pb2} <= {row.get("EndpointId") for row in _node(bench, n, "/igc-endpoints").get("cluster") or []}
                   for n in ("node-1", "node-2"))
    _require(bench, "the cluster IGC endpoint directory converged on both nodes", _poll(directory, HANDOVER_TIMEOUT))
    # The directory is not enough: the antenna graph is a separate cycle - the target node reports its
    # antenna, the source node installs a proxy, and the source's reachable-broadcaster set picks it up.
    reach = {}

    def reachable():
        reach.update(_node(bench, "node-1", f"/igc-test-reachability?source={pb1}&addressee={pb2}"))
        return (reach.get("reach") or {}).get("reachable") is True
    ok = _poll(reachable, HANDOVER_TIMEOUT)
    bench.record("world-authority-igc-reachability", reach)
    _require(bench, "the IGC host grids became antenna-reachable", ok, json.dumps(reach)[:400])
    return SimpleNamespace(grid1=grid1, grid2=grid2, pb1=pb1, pb2=pb2, reach=reach)


def _collect(bench, node_id: str, address: int, kind: str, tag: str, done, timeout: float) -> list:
    """igc-test-received drains cumulatively: accumulate the payloads of one tag across polls."""
    drained = []

    def more():
        batch = _received(bench, node_id, address)
        drained.extend(row.get("data") for row in batch.get(kind) or [] if row.get("tag") == tag)
        return done(drained)
    _poll(more, timeout)
    return drained


def _igc_unicast_broadcast(bench, ctx, hosts) -> None:
    pb1, pb2 = hosts.pb1, hosts.pb2
    # Unicast: node-1 addresses a programmable block that only exists on node-2.
    unicast = _post(bench, "node-1", f"/igc-test-unicast?source={pb1}&addressee={pb2}&tag=cluster-probe&data=unicast-payload")
    _require(bench, "a cross-node IGC unicast was accepted", unicast.get("sent") is True,
             json.dumps({"send": unicast, "reach": hosts.reach})[:400])
    arrived = _poll(lambda: (lambda r: r if any(u.get("tag") == "cluster-probe" and u.get("data") == "unicast-payload"
                                                and u.get("source") == pb1 for u in r.get("unicast") or []) else None)(
        _received(bench, "node-2", pb2)), 30)
    bench.check("the cross-node IGC unicast arrived at the addressee node", arrived)
    # ...and must not have been delivered anywhere else.
    leak = _received(bench, "node-1", pb1)
    bench.check("the cross-node IGC unicast was not delivered on the sending node", not leak.get("unicast"), json.dumps(leak))
    # Broadcast: only fanned out to nodes reporting a listener for the tag, so register one first and
    # wait for the registry to carry it.
    _post(bench, "node-2", f"/igc-test-listen?address={pb2}&tag=cluster-beacon")
    _require(bench, "the cluster IGC listener registry carried the remote broadcast tag",
             _poll(lambda: _listener_carried(bench, "node-1", "node-2", "cluster-beacon"), HANDOVER_TIMEOUT))
    _post(bench, "node-1", f"/igc-test-broadcast?source={pb1}&tag=cluster-beacon&data=broadcast-payload")
    broadcast = _poll(lambda: (lambda r: r if any(b.get("tag") == "cluster-beacon" and b.get("data") == "broadcast-payload"
                                                  and b.get("source") == pb1 for b in r.get("broadcast") or []) else None)(
        _received(bench, "node-2", pb2)), 30)
    bench.check("the cross-node IGC broadcast reached the remote listener", broadcast)
    status = _node(bench, "node-1", "/igc-status")
    # Exactly-once and ordering: every send the sender journalled must have been delivered, and the
    # receiving node must have buffered nothing, since the sends were serialised.
    bench.check("every cross-node IGC send was journalled as delivered",
                all(row.get("delivered") is True for row in status.get("journal") or []), json.dumps(status.get("journal"))[:300])
    bench.check("the receiving node holds no out-of-order IGC messages", _node(bench, "node-2", "/igc-status").get("buffered") == 0)
    bench.record("world-authority-igc", {"endpoints": {"node1": pb1, "node2": pb2},
                                         "unicast": {"send": unicast, "received": arrived},
                                         "broadcast": {"received": broadcast}, "status": status})


def _igc_relay_chain(bench, ctx, owner: int) -> None:
    """WA-4 relay-chain reachability: endpoints whose antenna radii cannot span the distance directly,
    bridged only by an intermediate relay grid - and unreachable again the moment the relay goes dark.
    Geometry on the shared lateral line (all radii 15 km): A at 60 km (node-1), B at 72 km (node-2), C at
    84 km (node-2). A-B and B-C are 12 km links; A-C is 24 km, out of direct range; the hosts sit at
    2-9 km lateral, more than 50 km from every chain grid, so they cannot bridge it."""
    grids = {}
    for key, node_id, lateral in (("a", "node-1", 60000), ("b", "node-2", 72000), ("c", "node-2", 84000)):
        grids[key] = _post(bench, node_id, f"/igc-test-ensure-named-grid?owner={owner}&name=cluster-igc-relay-{key}"
                                           f"&lateral={lateral}&radius=15000").get("grid")
    _require(bench, "the relay-chain grids were provisioned", all(grids.values()), json.dumps(grids))
    pba = _post(bench, "node-1", f"/igc-test-add-pb?grid={grids['a']}").get("address")
    pbc = _post(bench, "node-2", f"/igc-test-add-pb?grid={grids['c']}").get("address")
    _require(bench, "the relay-chain programmable blocks were placed", pba and pbc)
    reach = f"/igc-test-reachability?source={pba}&addressee={pbc}"
    up, dark = {}, {}
    _require(bench, "the relay-chain endpoints became reachable through the middle relay",
             _poll(lambda: up.update(_node(bench, "node-1", reach)) or (up.get("reach") or {}).get("reachable") is True, HANDOVER_TIMEOUT),
             json.dumps(up)[:300])
    send = _post(bench, "node-1", f"/igc-test-unicast?source={pba}&addressee={pbc}&tag=cluster-relay&data=relay-1")
    _require(bench, "the relay-chain unicast was accepted while the relay was up", send.get("sent") is True, json.dumps(send))
    got = _collect(bench, "node-2", pbc, "unicast", "cluster-relay", lambda d: len(d) >= 1, 30)
    bench.check("the relay-chain unicast arrived at the far endpoint exactly once", got == ["relay-1"], json.dumps(got))
    _post(bench, "node-2", f"/igc-test-antenna?grid={grids['b']}&enabled=false")
    try:
        bench.check("the relay-chain endpoints became unreachable with the middle relay dark",
                    _poll(lambda: dark.update(_node(bench, "node-1", reach)) or (dark.get("reach") or {}).get("reachable") is False,
                          HANDOVER_TIMEOUT), json.dumps(dark)[:300])
        send_dark = _post(bench, "node-1", f"/igc-test-unicast?source={pba}&addressee={pbc}&tag=cluster-relay&data=relay-2")
        bench.check("the relay-chain unicast was refused with the relay dark", send_dark.get("sent") is False, json.dumps(send_dark))
    finally:
        _post(bench, "node-2", f"/igc-test-antenna?grid={grids['b']}&enabled=true")
    bench.record("world-authority-igc-relay-chain", {"grids": grids, "relayUp": {"reach": up, "send": send},
                                                     "relayDark": {"reach": dark, "send": send_dark}})


def _igc_laser(bench, ctx, owner: int) -> None:
    """WA-4 laser endpoint: a cross-node point-to-point laser link. Each side pairs against the other's
    proxy (the real block never exists locally); the cluster syncs the handshake facts at 1 Hz and the
    state machines converge to connected independently. IGC then flows over the laser-only path, and
    turning one antenna off severs it - reachability false, unicast refused vanilla-shaped."""
    ensured = {n: _post(bench, n, f"/igc-test-ensure-laser?owner={owner}&name=cluster-igc-laser-{i}&lateral={lateral}")
               for i, n, lateral in ((1, "node-1", 100000), (2, "node-2", 102000))}
    bench.record("world-authority-igc-laser-grids", ensured)
    grid1, grid2 = ensured["node-1"].get("grid"), ensured["node-2"].get("grid")
    laser1, laser2 = ensured["node-1"].get("laser"), ensured["node-2"].get("laser")
    _require(bench, "the laser grids and antennas exist", grid1 and grid2 and laser1 and laser2, json.dumps(ensured))
    # Pairing needs the far side's proxy and peer record, which propagate at 1 Hz - retry until accepted.
    paired = {"node-1": False, "node-2": False}

    def pair():
        for node_id, grid, target in (("node-1", grid1, laser2), ("node-2", grid2, laser1)):
            if not paired[node_id]:
                result = _try(bench, node_id, f"/igc-test-laser-connect?grid={grid}&target={target}", "POST")
                paired[node_id] = isinstance(result, dict) and result.get("requested") is True
        return all(paired.values())
    _require(bench, "the laser pairing was accepted on both sides", _poll(pair, HANDOVER_TIMEOUT), json.dumps(paired))
    states = {}

    def connected():
        states["node-1"] = _node(bench, "node-1", f"/igc-test-laser-status?grid={grid1}")
        states["node-2"] = _node(bench, "node-2", f"/igc-test-laser-status?grid={grid2}")
        return all(s.get("state") == "connected" for s in states.values())
    _require(bench, "the cross-node laser link connected", _poll(connected, HANDOVER_TIMEOUT), json.dumps(states))
    pb1 = _post(bench, "node-1", f"/igc-test-add-pb?grid={grid1}").get("address")
    pb2 = _post(bench, "node-2", f"/igc-test-add-pb?grid={grid2}").get("address")
    _require(bench, "the laser-grid programmable blocks were placed", pb1 and pb2)
    reach_path = f"/igc-test-reachability?source={pb1}&addressee={pb2}"
    reach, dark = {}, {}
    _require(bench, "the laser-only path became IGC-reachable",
             _poll(lambda: reach.update(_node(bench, "node-1", reach_path)) or (reach.get("reach") or {}).get("reachable") is True,
                   HANDOVER_TIMEOUT), json.dumps(reach)[:300])
    send = _post(bench, "node-1", f"/igc-test-unicast?source={pb1}&addressee={pb2}&tag=cluster-laser&data=laser-1")
    _require(bench, "the laser-path unicast was accepted while connected", send.get("sent") is True, json.dumps(send))
    got = _collect(bench, "node-2", pb2, "unicast", "cluster-laser", lambda d: len(d) >= 1, 30)
    bench.check("the laser-path unicast arrived exactly once", got == ["laser-1"], json.dumps(got))
    _post(bench, "node-2", f"/igc-test-antenna?grid={grid2}&enabled=false")
    send_dark = None
    try:
        def severed():
            dark["state"] = _node(bench, "node-1", f"/igc-test-laser-status?grid={grid1}")
            dark["reach"] = _node(bench, "node-1", reach_path)
            return dark["state"].get("state") != "connected" and (dark["reach"].get("reach") or {}).get("reachable") is False
        bench.check("the laser link severed after the far antenna went dark", _poll(severed, HANDOVER_TIMEOUT), json.dumps(dark)[:300])
        send_dark = _post(bench, "node-1", f"/igc-test-unicast?source={pb1}&addressee={pb2}&tag=cluster-laser&data=laser-2")
        bench.check("the laser-path unicast was refused with the link severed", send_dark.get("sent") is False, json.dumps(send_dark))
    finally:
        _post(bench, "node-2", f"/igc-test-antenna?grid={grid2}&enabled=true")
    bench.record("world-authority-igc-laser", {"grids": {"node1": grid1, "node2": grid2}, "connected": states,
                                               "linkUp": {"reach": reach, "send": send},
                                               "linkDark": {**dark, "send": send_dark}})


def _mod_drain(bench) -> None:
    for node_id in ("node-1", "node-2", "wa-1"):
        _node(bench, node_id, "/mod-test-received")


# -- scenarios -------------------------------------------------------------------------------


def _founder(bench) -> SimpleNamespace:
    ctx = _wa_cluster(bench)
    _join_founder(bench, ctx)
    return ctx


def p2_wa_00(bench) -> None:
    """WA role, session clock, ordered global state, identity, faction and bank state, then a WA SIGKILL
    and a replacement that restores the catalogued state, replays the commands answered after it (#1318) and fences
    the old generation."""
    ctx = _founder(bench)
    _found_faction(bench, ctx)
    ok, spread, clocks = _wait_clock(bench, ctx.generation, "wa-1")
    bench.record("world-authority-clock-before", {**clocks, "spreadMilliseconds": spread})
    bench.check("the regular nodes converged to the WA clock (< 250 ms)", ok, f"spread={spread} ms")
    _leg(bench, "global-state", _global_state_order, bench, ctx)
    _identity_allocation(bench, ctx)
    _bank_race(bench, ctx)
    _faction_levers(bench, ctx)
    _failover(bench, ctx)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(route="wa-1 killed -> wa-2", generations=ctx.failover, identity=ctx.identity_id,
                    faction=ctx.faction_tag, bankOwner=ctx.bank_owner)


def _bench_pids(bench, slot: str) -> list[int]:
    """The processes of ONE bench node, found by /proc and signalled by pid: the bench node names
    match the live cluster's, so nothing here may select by name (CM condition for P2-WA-07).

    A process qualifies only if ALL of these hold, and one that names the data dir but fails any
    other raises instead, so the caller never signals: (1) its argv names exactly this run's
    ``<root>/<slot>-data``; (2) its CLUSTER_DATA_ROOT (the only environ key read) is that same dir;
    (3) its own cwd or its parent's cwd is under this run's root. The game process chdirs into the
    dedicated-server install, so for it (3) holds through its launcher; the launcher's own cwd is
    its Magnetar config dir under the root."""
    root = os.path.realpath(str(bench.cluster.config.root)) + os.sep
    data = f"{root}{slot}-data"

    def cwd_of(pid) -> str:
        return os.path.realpath(os.readlink(f"/proc/{pid}/cwd")) + os.sep

    found = []
    for entry in os.listdir("/proc"):
        if not entry.isdigit() or int(entry) == os.getpid():
            continue
        try:
            argv = open(f"/proc/{entry}/cmdline", "rb").read().split(b"\0")
            if not any(arg.decode(errors="replace").rstrip("/") == data for arg in argv):
                continue
            environ = open(f"/proc/{entry}/environ", "rb").read().split(b"\0")
            data_root = next((item.split(b"=", 1)[1].decode(errors="replace").rstrip("/") for item in environ
                              if item.startswith(b"CLUSTER_DATA_ROOT=")), None)
            parent = open(f"/proc/{entry}/stat").read().rsplit(")", 1)[1].split()[1]
            cwds = [cwd_of(entry), cwd_of(parent)]
        except OSError:
            continue
        if data_root != data:
            raise ClusterError(f"pid {entry} names {data} but its CLUSTER_DATA_ROOT is {data_root!r}: refusing to signal")
        if not any(cwd.startswith(root) for cwd in cwds):
            raise ClusterError(f"pid {entry} names {data} but neither its cwd nor its parent's ({cwds}) is under {root}: refusing to signal")
        found.append(int(entry))
    return found


def p2_wa_07(bench) -> None:
    """T-0230: a WA killed within a second of a global save that nobody replicated yet. The
    replacement must restore the newest save a LIVE node holds, fetched over its control endpoint,
    and boot even where the dead WA's files are not readable (another host).

    THE RACE IS MADE, NOT HOPED FOR. Replicas are pulled by the regular nodes ~0.3 s after a
    save registers, and the bench run that found this (P2-WA-00, 2026-09-24 05:02) lost it by
    chance. Here node-1 and node-2 are SIGSTOPped (by pid, cwd asserted under the bench root) for
    the ~2 s from the capture to the kill, so the captured save CANNOT have a second holder, and
    the scenario asserts that it has none before it lets them continue.

    ANOTHER HOST IS SIMULATED by renaming wa-1's GLOBAL directory before wa-2 starts: the
    foreign-path fallback that rescued the bench run is then gone, as it is across hosts.

    The red arm is the pre-fix gateway (any package before T-0230): the registry hands wa-2 the
    unreplicated save, wa-2 retries dead wa-1 for 45 s, finds no readable path and does not boot."""
    ctx = _wa_cluster(bench)
    generation = ctx.generation
    root = bench.cluster.config.root

    def replicated():
        s = _newest_global(bench) or {}
        return s if any(h != "wa-1" for h in (s.get("LocationHolders") or {}).values()) else None
    _require(bench, "wa-1 catalogued a global save", _poll(lambda: _newest_global(bench), SERVER_TIMEOUT))
    _post(bench, "wa-1", "/global-save-capture", timeout=5)
    older = _poll(replicated, HANDOVER_TIMEOUT)
    _require(bench, "a global save was replicated to a regular node before the race", older,
             json.dumps((_newest_global(bench) or {}).get("LocationHolders")))
    bench.record("t0230-replicated-save", older)

    stopped: list[int] = []
    for slot in ("node-1", "node-2"):
        pids = _bench_pids(bench, slot)
        _require(bench, f"the bench {slot} processes were found under the bench root", pids, str(root))
        stopped.extend(pids)
    raced = None
    try:
        for pid in stopped:
            os.kill(pid, signal.SIGSTOP)
        before = older["SaveId"]["Sequence"]
        _post(bench, "wa-1", "/global-save-capture", timeout=5)
        raced = _poll(lambda: (lambda s: s if s and s["SaveId"]["Generation"] == generation
                               and s["SaveId"]["Sequence"] > before else None)(_newest_global(bench)), 4, interval=0.05)
        wa_pids = _bench_pids(bench, "wa-1")
        for pid in wa_pids:
            os.kill(pid, signal.SIGKILL)
        killed_at = time.time()
        # Earned: the save exists and, with both replica pullers stopped, wa-1 is still its only holder.
        at_kill = _newest_global(bench) or {}
    finally:
        for pid in stopped:
            try:
                os.kill(pid, signal.SIGCONT)
            except ProcessLookupError:
                pass
    bench.cluster._ack_kill_incumbent("wa-1")
    _require(bench, "wa-1 registered the raced global save before it was killed", raced)
    holders = sorted(set((at_kill.get("LocationHolders") or {}).values()))
    _require(bench, "the race was earned: at the kill the newest global save was held by wa-1 alone",
             at_kill.get("SaveId") == raced["SaveId"] and holders == ["wa-1"], json.dumps({"atKill": at_kill.get("SaveId"), "holders": holders}))
    bench.record("t0230-raced-save", {"raced": raced, "atKill": at_kill, "stopped": stopped, "killed": wa_pids,
                                      "killedAt": killed_at})

    _require(bench, "the Gateway fenced the dead WA", _poll(
        lambda: (lambda a: a if not a.get("Node") and (a.get("Generation") or 0) > generation else None)(
            bench.cluster.get("/registry/world-authority")), HANDOVER_TIMEOUT))
    recovery = bench.cluster.get("/registry/global-saves/recovery")
    bench.record("t0230-recovery", recovery)
    bench.check("the registry offers the replicated save, not the one only the dead WA held",
                recovery.get("SaveId") == older["SaveId"], json.dumps(recovery.get("SaveId")))

    global_dir = root / "wa-1-data" / "cluster" / "saves" / "GLOBAL"
    hidden = global_dir.with_name("GLOBAL.hidden-t0230")
    global_dir.rename(hidden)
    try:
        replacement_spec = bench.cluster.config.add_node("wa-2", role="WA")
        bench.cluster.propagate_sources(_nodes(bench)["wa-1"], [replacement_spec])
        bench.cluster.start_node("wa-2")
        want = rf"global_save fetched=true generation={generation} sequence={older['SaveId']['Sequence']} bytes=\d+ from=node-"
        bench.cluster.wait_node_log("wa-2", want, SERVER_TIMEOUT)
        bench.cluster.wait_node_log("wa-2", "Game ready", SERVER_TIMEOUT)
    finally:
        hidden.rename(global_dir)
    bench.check("the replacement WA never needed the dead WA's files",
                not _lines(bench, "wa-2", r"global_save foreign_path_fallback=true"))
    replacement = bench.cluster.get("/registry/world-authority")
    _require(bench, "the replacement WA is designated with a newer generation",
             replacement.get("Node") == "wa-2" and (replacement.get("Generation") or 0) > generation, json.dumps(replacement))
    bench.exercised(route="wa-1 killed before replication -> wa-2 from a regular node's replica",
                    raced=raced["SaveId"], restored=older["SaveId"])


JOURNAL_HOLD_MS = 180000   # longer than the whole leg: no global save after the commands is catalogued before the kill


def p2_wa_09(bench) -> None:
    """#1318: a World Authority killed after it answered banking and identity commands from the nodes, but before any
    global save holding them was catalogued, comes back with them. A global save holding the account is catalogued and
    replicated first; then every later save of wa-1 is held unregistered (test seam), node-1 debits 300 under an
    operation id, node-2 credits 50 and node-1 allocates an identity. wa-1 is SIGKILLed; wa-2 restores the save and
    replays the journal. Green: 750, the identity under its id, a retry of the debit's operation id answered from the
    replayed ledger (not applied twice), every journaled answer sent within a round trip, no failed or diverged replay.
    Red (P2_WA_09_JOURNAL=0 turns the journal off on both WAs): 1000, no identity, and the retry debits again.
    Faction commands replay through the same path; P2-WA-00 asserts its post-save faction state is restored."""
    journal = os.environ.get("P2_WA_09_JOURNAL", "1") != "0"
    if not journal:
        for wa in ("wa-1", "wa-2"):
            bench.cluster.config.node_env_by_id.setdefault(wa, {})["CLUSTER_GLOBAL_JOURNAL"] = "0"
    ctx = _wa_cluster(bench)
    generation = ctx.generation
    selftest = _node(bench, "node-1", "/self-test?section=ledger-replay")
    bench.check("node self-test ledger-replay: a replayed result answers its retry, the next command follows it",
                selftest.get("ok") is True, json.dumps(selftest)[:300])
    owner = 930000000000000000 + random.randrange(32768)
    _post(bench, "node-1", f"/banking-test-create?ownerId={owner}&balance=1000")
    _require(bench, "the account holds 1000 on every role", _poll(lambda: _balances(bench, owner, 1000), HANDOVER_TIMEOUT))

    def replicated():
        save = _newest_global(bench) or {}
        return save if save.get("SaveId", {}).get("Sequence", 0) > before \
            and any(holder != "wa-1" for holder in (save.get("LocationHolders") or {}).values()) else None
    before = ((_newest_global(bench) or {}).get("SaveId") or {}).get("Sequence", 0)
    _post(bench, "wa-1", "/global-save-capture", timeout=5)
    saved = _poll(replicated, HANDOVER_TIMEOUT)
    _require(bench, "a global save holding the account is catalogued and replicated", saved)
    held = _post(bench, "wa-1", f"/global-save-register-hold?ms={JOURNAL_HOLD_MS}")
    _require(bench, "wa-1 holds its later global saves unregistered", held.get("until", 0) > 0, json.dumps(held)[:300])

    operation = str(uuid.uuid4())
    started = time.monotonic()
    debit = _post(bench, "node-1", f"/banking-test-change?ownerId={owner}&amount=-300&operationId={operation}")
    debit_seconds = time.monotonic() - started
    _require(bench, "node-1's debit of 300 succeeded", debit.get("success") is True, json.dumps(debit))
    credit = _post(bench, "node-2", f"/banking-test-change?ownerId={owner}&amount=50")
    _require(bench, "node-2's credit of 50 succeeded", credit.get("success") is True, json.dumps(credit))
    name = f"p2-wa-09-identity-{random.randrange(32768)}"
    identity = _post(bench, "node-1", f"/identity-test-create?name={name}").get("identityId") or 0
    _require(bench, "node-1 got a new identity from wa-1", identity > 0)
    _require(bench, "wa-1 holds 750", _poll(lambda: _balances(bench, owner, 750, ("wa-1",)), HANDOVER_TIMEOUT))
    before_kill = _node(bench, "wa-1", "/global-state").get("journal") or {}
    at_kill = _newest_global(bench) or {}
    bench.record("p2-wa-09-before-kill", {"journal": before_kill, "newestSave": at_kill, "saved": saved,
                                          "debitSeconds": round(debit_seconds, 3)})
    _require(bench, "no global save after the commands was catalogued (the window is open at the kill)",
             at_kill.get("SaveId") == saved["SaveId"], json.dumps(at_kill.get("SaveId")))

    bench.cluster.kill(slot="wa-1")
    _require(bench, "the Gateway fenced the killed wa-1", _poll(
        lambda: (lambda a: a if not a.get("Node") and (a.get("Generation") or 0) > generation else None)(
            bench.cluster.get("/registry/world-authority")), RECOVERY_TIMEOUT))
    replacement = bench.cluster.config.add_node("wa-2", role="WA")
    bench.cluster.propagate_sources(_nodes(bench)["wa-1"], [replacement])
    bench.cluster.start_node("wa-2")
    bench.cluster.wait_node_log("wa-2", "Game ready", SERVER_TIMEOUT)
    _require(bench, "wa-2 serves banking", _poll(lambda: (_balance(bench, "wa-2", owner) or {}).get("exists"), HANDOVER_TIMEOUT))
    # The replay runs on wa-2's second ready frame, before it serves a command; the red arm has nothing to replay.
    _require(bench, "wa-2 has no journal replay pending", _poll(
        lambda: (_node(bench, "wa-2", "/global-state").get("journal") or {}).get("replayPending") is False, HANDOVER_TIMEOUT))
    bench.check("wa-2 reported Serving only after its replay",
                (_first_line_number(bench, "wa-2", r"node_state=Serving") or 0)
                > (_first_line_number(bench, "wa-2", r"global_journal replayed=") or 0))
    restored = {"balance": (_balance(bench, "wa-2", owner) or {}).get("balance"),
                "identity": _try(bench, "wa-2", f"/identity-test-status?identityId={identity}") or {},
                "journal": (_node(bench, "wa-2", "/global-state").get("journal") or {})}
    retry = _post(bench, "node-1", f"/banking-test-change?ownerId={owner}&amount=-300&operationId={operation}")
    after_retry = (_balance(bench, "wa-2", owner) or {}).get("balance")
    bench.record("p2-wa-09-restored", {**restored, "retry": retry, "afterRetry": after_retry, "journalArm": journal})

    bench.check("every journaled answer went out after its append, none failed open (debit answered within a round trip)",
                debit_seconds < 3 and before_kill.get("failedOpen") == 0 and (before_kill.get("appended") or 0) >= 3,
                f"debit {debit_seconds:.2f} s, journal {json.dumps(before_kill)}")
    bench.check("wa-2 restored the debit and the credit made after the save (750)", restored["balance"] == 750,
                f"balance {restored['balance']}")
    bench.check("wa-2 knows the identity allocated after the save, under the same id",
                restored["identity"].get("exists") is True and restored["identity"].get("name") == name, json.dumps(restored["identity"]))
    bench.check("wa-2 replayed the journal without a failed or diverged entry",
                (restored["journal"].get("replayed") or 0) >= 3 and restored["journal"].get("replayFailed") == 0
                and restored["journal"].get("replayDiverged") == 0, json.dumps(restored["journal"]))
    bench.check("a retry of the debit's operation id is answered from the replayed ledger, not debited again",
                retry.get("success") is True and after_retry == restored["balance"], f"{json.dumps(retry)} -> {after_retry}")
    bench.check("both regular nodes converge on wa-2's balance",
                _poll(lambda: _balances(bench, owner, after_retry, ("node-1", "node-2")), HANDOVER_TIMEOUT), f"balance {after_retry}")
    bench.exercised(route="wa-1 killed with a journal of 3 commands after its last catalogued save -> wa-2 replays them",
                    journal=journal, saved=saved["SaveId"])


def p2_wa_01(bench) -> None:
    """Faction, chat and GPS across nodes, through the vanilla UI of two real clients, with the founder
    respawning at a medbay that moved to the other node."""
    ctx = _founder(bench)
    _found_faction(bench, ctx)
    _respawn_points(bench, ctx)
    _cross_node_respawn(bench, ctx)
    _leg(bench, "faction-chat-and-leave", _faction_chat_and_leave, bench, ctx)
    _leg(bench, "diplomacy", _diplomacy, bench, ctx)
    if getattr(ctx, "other_id", 0) > 0:  # the snapshot leg removes the rival faction the diplomacy leg founded
        _leg(bench, "snapshot-reconciliation", _snapshot_reconciliation, bench, ctx)
    else:
        bench.check("snapshot-reconciliation: the rival faction exists to remove", False, "the diplomacy leg founded none")
    _leg(bench, "respawn-replay", _respawn_replay, bench, ctx)
    _leg(bench, "forwarded-chat", _forwarded_chat, bench, ctx)
    _leg(bench, "combat-reputation", _combat_reputation, bench, ctx)
    _leg(bench, "promotion", _promotion, bench, ctx)
    _leg(bench, "online-list", _online_list, bench, ctx)
    bench.screenshot(CLIENT_ID, "99-after")
    bench.exercised(route="founder node-1 -> node-2 (remote medbay), client 2 on node-1", faction=ctx.faction_tag,
                    medbayPartition=ctx.respawn_partition, founder=ctx.faction_identity, member=ctx.client2_identity)


def _broadcast_chat(bench, ctx, node_id: str, identity: int, author: str) -> str:
    text = f"se943-broadcast-{node_id}-{random.randrange(32768)}"
    _post(bench, node_id, "/broadcast-chat-test?" + _q(identityId=identity, author=author, text=text))
    return text


STORE_WA_DELAY_MS = 7000  # past the node's 5 s World Authority command timeout


def p2_wa_store_timeout(bench) -> None:
    """SE-1226: a store payment leg the World Authority answers after the node's 5 s command timeout. On
    octahedron-test the WA's game thread stalled for 10 s while trader ships sold at station stores; the credit
    timed out on the node, the payment scope refunded the payer, and the WA applied the late credit as well, so the
    amount existed twice (about 453,000 SC on 2026-11-07). Here the WA holds the payee's credit for 7 s on its game
    thread, then the payer's reserve. Each time the money of both parties must add up to what they had, the trade
    either happening once (credit) or not at all (reserve), and the node's game thread must not wait for a credit."""
    _wa_cluster(bench)
    selftest = _try(bench, "node-1", "/self-test?section=store-payment-settle") or {}
    bench.check("node self-test store-payment-settle (late credit, reserve and refund answers, refused legs)",
                selftest.get("ok") is True and selftest.get("ran") == ["store-payment-settle"], json.dumps(selftest)[:300])

    def accounts(tag: int) -> tuple[int, int]:
        payer, payee = 912260000000000000 + tag * 1000 + random.randrange(1000), 922260000000000000 + tag * 1000 + random.randrange(1000)
        for owner, balance in ((payer, 1000), (payee, 0)):
            _post(bench, "node-1", f"/banking-test-create?ownerId={owner}&balance={balance}")
        _require(bench, f"the leg {tag} accounts exist at the WA",
                 _poll(lambda: _wa_balance(bench, payer) == 1000 and _wa_balance(bench, payee) == 0, HANDOVER_TIMEOUT))
        return payer, payee

    def pay(payer: int, payee: int) -> tuple[dict | None, float]:
        started = time.monotonic()
        answer = _try(bench, "node-1", f"/banking-test-store-payment?payerId={payer}&payeeId={payee}&amount=300&commit=true",
                      method="POST")
        return answer, time.monotonic() - started

    def settle(payer: int, payee: int, expected: tuple[int, int]) -> dict:
        # Past the WA's hold and one more answer, whatever the node does; then the balances must stay put.
        reached = _poll(lambda: (_wa_balance(bench, payer), _wa_balance(bench, payee)) == expected, STORE_WA_DELAY_MS / 1000 + 15)
        first = (_wa_balance(bench, payer), _wa_balance(bench, payee))
        time.sleep(3)
        return {"reached": bool(reached), "balances": first, "later": (_wa_balance(bench, payer), _wa_balance(bench, payee))}

    # Credit leg: the reserve applies at once, the credit only after the node gave up waiting for it.
    payer, payee = accounts(1)
    _post(bench, "wa-1", f"/banking-test-delay?ownerId={payee}&ms={STORE_WA_DELAY_MS}")
    answer, seconds = pay(payer, payee)
    credit = {"answer": answer, "seconds": round(seconds, 2), **settle(payer, payee, (700, 300))}
    bench.record("store-timeout-credit", credit)
    bench.check("a late credit does not hold the node's game thread (store payment answered within 2 s)",
                answer is not None and answer.get("approved") and seconds < 2, json.dumps(credit))
    bench.check("a late credit is paid once and not refunded: payer 700, payee 300, 1000 SC in all",
                credit["balances"] == (700, 300) and credit["later"] == (700, 300), json.dumps(credit))
    bench.check("the node settled the credit through its journal (credited=True, refunded=False)",
                bool(_lines(bench, "node-1", rf"store payment settled reserve=.* payer={payer} amount=300 "
                                             rf"reserved=True refunded=False credited=True")))

    # Reserve leg: the node gives up on the reserve, which the WA applies later; it must come back to the payer.
    payer, payee = accounts(2)
    _post(bench, "wa-1", f"/banking-test-delay?ownerId={payer}&ms={STORE_WA_DELAY_MS}")
    answer, seconds = pay(payer, payee)
    reserve = {"answer": answer, "seconds": round(seconds, 2), **settle(payer, payee, (1000, 0))}
    bench.record("store-timeout-reserve", reserve)
    bench.check("an unanswered reserve refuses the purchase without an error",
                answer is not None and answer.get("approved") is False, json.dumps(reserve))
    bench.check("a late reserve is refunded and nothing is paid: payer 1000, payee 0",
                reserve["balances"] == (1000, 0) and reserve["later"] == (1000, 0), json.dumps(reserve))
    bench.check("the node settled the reserve through its journal (reserved=True, refunded=True)",
                bool(_lines(bench, "node-1", rf"store payment settled reserve=.* payer={payer} amount=300 "
                                             rf"reserved=True refunded=True credited=False")))
    bench.exercised(credit=credit, reserve=reserve, delayMs=STORE_WA_DELAY_MS)


def p2_wa_respawn_points(bench) -> None:
    """SE-1150: one bad respawn point no longer costs a node its whole list at the World Authority. On
    octahedron-test a medbay in an overfilled room reported an oxygen level just above 1 for 12 minutes; the WA
    refused node-1's whole report every second ("global_state rejected=true error=Invalid respawn registry
    payload") and kept its stale list. node-1 sends, besides its real points, a good point, one reading 1.0000001
    (the live case), one with generation 0 and a duplicate id. The WA keeps the real points and the good one,
    clamps the oxygen to 1, drops the other two, logs each once over several reports, and every replica gets
    the same list. Reports go every second, so a few seconds cover several."""
    _wa_cluster(bench)
    roles = ("node-1", "node-2", "wa-1")
    selftest = _try(bench, "wa-1", "/self-test?section=respawn-registry") or {}
    bench.check("WA self-test respawn-registry (clamp, truncate, drop, duplicates, envelope, log once)",
                selftest.get("ok") is True and selftest.get("ran") == ["respawn-registry"], json.dumps(selftest)[:300])
    good, over, broken = 9_150_000_001, 9_150_000_002, 9_150_000_003
    raw_ids = {good, over, broken}

    def registry(node_id: str) -> list[dict]:
        return list((_try(bench, node_id, "/respawn-points") or {}).get("points") or [])

    def real(points: list[dict]) -> list[dict]:
        return sorted((p for p in points if p.get("Origin") == "node-1" and p["RespawnEntityId"] not in raw_ids),
                      key=lambda p: p["RespawnEntityId"])
    # node-1's own points, once its reports reach the WA (the bench world's survival kit).
    before = _poll(lambda: real(registry("wa-1")) or None, HANDOVER_TIMEOUT)
    _require(bench, "node-1's own respawn points reached the WA", before, json.dumps(registry("wa-1"))[:300])
    bench.record("respawn-points-before", before)

    def raw(entity: int, **fields) -> dict:
        return {"Origin": "node-1", "RespawnEntityId": entity, "GridEntityId": entity + 1000, "PartitionId": 1,
                "Generation": 1, "Name": f"SE-1150 {entity}", "OwnerIdentityId": 0, "ShareMode": 2, "Working": True,
                "SpawnWithoutOxygen": True, "SetFactionToSpawnee": False, "OxygenLevel": 0.5, "X": 0, "Y": 0, "Z": 0,
                **fields}
    points = [raw(good), raw(over, OxygenLevel=1.0000001), raw(broken, Generation=0), raw(good, Name="SE-1150 duplicate")]
    started = time.time()
    _post(bench, "node-1", "/respawn-test-raw-points?" + _q(points=json.dumps(points), seconds=60))
    seen: dict[str, list[dict]] = {}

    def converged():
        for node_id in roles:
            seen[node_id] = registry(node_id)
        wa = {p["RespawnEntityId"]: p for p in seen["wa-1"]}
        return (good in wa and over in wa and broken not in wa and wa[good]["Name"] == f"SE-1150 {good}"
                and wa[over]["OxygenLevel"] == 1 and all(wa.get(p["RespawnEntityId"]) == p for p in before)
                and all(_dumps(seen[n]) == _dumps(seen["wa-1"]) for n in ("node-1", "node-2")))
    ok = _poll(converged, 30)
    bench.record("respawn-points-with-bad", seen)
    bench.check("the WA kept node-1's real points and the good one, clamped 1.0000001 to 1 and dropped generation 0 "
                "and the duplicate; every replica has the same list", ok, json.dumps(seen.get("wa-1"))[:400])
    # Several more reports with the same bad points: still one line each.
    _poll(lambda: time.time() - started > 8, 15)
    lines = {f"{kind} {entity}": _lines(bench, "wa-1", rf"respawn_point {kind} where=authority origin=node-1 entity={entity} ")
             for kind, entity in (("repaired", over), ("dropped", broken), ("dropped", good))}
    rejected = {node_id: _lines(bench, node_id, r"global_state rejected=true error=Invalid respawn registry payload")
                for node_id in roles}
    bench.record("respawn-point-log", {"lines": lines, "rejected": rejected})
    bench.check("the WA logged each repaired or dropped point once over 8 s of reports",
                all(len(found) == 1 for found in lines.values()) and "check='duplicate RespawnEntityId'" in lines[f"dropped {good}"][0]
                and "value=1.0000001" in lines[f"repaired {over}"][0], json.dumps(lines)[:400])
    bench.check("no role refused a whole respawn payload", not any(rejected.values()), json.dumps(rejected)[:300])

    _post(bench, "node-1", "/respawn-test-raw-points?" + _q(points="[]", seconds=0))

    def cleared():
        for node_id in roles:
            seen[node_id] = registry(node_id)
        return all(not any(p["RespawnEntityId"] in raw_ids for p in seen[n]) for n in roles) \
            and all(any(p["RespawnEntityId"] == q["RespawnEntityId"] for p in seen["wa-1"]) for q in before)
    bench.check("with the bad points gone the WA list is node-1's real points again, on every role",
                _poll(cleared, 30), json.dumps(seen.get("wa-1"))[:300])
    bench.exercised(route="node-1 reports 1.0000001 oxygen, generation 0 and a duplicate id -> the WA repairs or drops just those",
                    realPoints=len(before))


def p2_wa_bcast(bench) -> None:
    """SE-943: a broadcast controller message sent on a regular node goes through the WA's chat path. The
    node the targeted client is on relays it with the block's name in CustomData.AuthorName, which the
    client shows as the sender, from either node; no other client receives it. Without CustomData across
    the WA the client named the Author, the node's server member, Good.bot.

    The bench's headless clients show no multiplayer chat on their HUD (MyHudChat needs an active game
    service), so the name is read from what node-1 relays (the test plugin's /chat-relay-probe), and the
    client's received-message log proves the delivery."""
    ctx = _founder(bench)
    api = ctx.api
    client = str(CLIENT_ID)
    author = f"SE-943 Beacon {random.randrange(1000)}"
    _post(bench, "node-1", "/chat-relay-probe")

    def relayed(text):
        return [row for row in _node(bench, "node-1", "/chat-relay-probe")["relayed"] if row.get("text") == text]

    def received(text):
        return any(m.get("Text") == text for m in api.get_chat_messages(0).get("messages") or [])

    for node_id in ("node-1", "node-2"):
        text = _broadcast_chat(bench, ctx, node_id, ctx.binding_identity, author)
        bench.check(f"a broadcast controller message sent on {node_id} reached the targeted client",
                    _poll(lambda: received(text), CLIENT_TIMEOUT), text)
        rows = relayed(text)
        bench.record(f"broadcast-from-{node_id}", rows)
        bench.check(f"node-1 relayed the message from {node_id} to the client with the broadcast controller's name, not Good.bot",
                    any(row["recipient"] == client and row["authorName"] == author for row in rows), json.dumps(rows))
    other = _post(bench, "node-1", "/identity-test-create?" + _q(name=f"se943-other-{random.randrange(1000)}"))["identityId"]
    hidden = _broadcast_chat(bench, ctx, "node-1", other, author)
    published = _poll(lambda: all(any(hidden in (row.get("payload") or "") for row in _subsystem(bench, n, "chat"))
                                  for n in ("node-1", "node-2")), HANDOVER_TIMEOUT)
    bench.check("the WA published the message targeted at another identity to both nodes", published, hidden)
    seen = _poll(lambda: received(hidden), 10)
    rows = relayed(hidden)
    bench.record("broadcast-to-other", rows)
    bench.check("the message targeted at another identity did not reach the client",
                not seen and not any(row["recipient"] == client for row in rows), json.dumps(rows))
    bench.exercised(route="broadcast controller chat from node-1 and node-2 to the founder on node-1, and to another identity",
                    author=author, identity=ctx.binding_identity, other=other)


def p2_wa_rename(bench) -> None:
    """SE-1100: a player who logs in again under a new name keeps it on every role, also after the World
    Authority's next identity snapshot. Vanilla renames the identity on each world request, but on a cluster
    only the node serving it did, and the WA's next snapshot put the old name back: a bot fleet that started
    prefixing its names kept the old ones. Two players: a bot renamed from ``Name`` to ``[B] Name``, and a
    Steam-style name stored with its platform icon (\\ue030) logging in without one, which keeps the icon. A
    third login publishes one more snapshot, after which every role must still hold the new names."""
    _wa_cluster(bench)
    roles = ("node-1", "node-2", "wa-1")
    selftest = _try(bench, "wa-1", "/self-test?section=identity-joined-name") or {}
    bench.check("WA self-test identity-joined-name (bot prefix, Steam icon kept or replaced, id-only and empty logins)",
                selftest.get("ok") is True and selftest.get("ran") == ["identity-joined-name"], json.dumps(selftest)[:300])

    def login(client_id: int, port: int | None, name: str) -> int:
        if port is None:
            bench.start_client(client_id, render=False, name=name)
        else:
            bench.start_client(client_id, port, render=False, name=name)

        def ready():
            session = _session(bench, client_id) or {}
            return session if session.get("admissionState") == "Ready" and (session.get("identityId") or 0) > 0 else None
        session = _poll(ready, CLIENT_TIMEOUT)
        _require(bench, f"client {client_id} logged in as {name!r}", session, json.dumps(_session(bench, client_id)))
        return session["identityId"]

    def names(identity: int) -> dict:
        rows = {node: _try(bench, node, f"/identity-test-status?identityId={identity}") for node in roles}
        return {node: row.get("name") if isinstance(row, dict) else None for node, row in rows.items()}

    def everywhere(identity: int, name: str):
        return _poll(lambda: (lambda seen: seen if all(value == name for value in seen.values()) else None)(names(identity)),
                     HANDOVER_TIMEOUT)

    def relog(client_id: int, port: int | None, old: str, new: str, expected: str) -> int:
        identity = login(client_id, port, old)
        _require(bench, f"{old!r} reached every role", everywhere(identity, old), json.dumps(names(identity)))
        bench.kill_client(client_id)
        _require(bench, f"client {client_id}'s Gateway session cleared",
                 _poll(lambda: True if _session(bench, client_id) is None else None, 360, 5))
        again = login(client_id, port, new)
        _require(bench, f"client {client_id} came back to the same identity", again == identity, f"{identity} then {again}")
        # The WA's name is the cluster's. The node serving the world request also renames the identity
        # (vanilla) to the platform name it sees, which for a Steam client carries the same icon; this bench's
        # Direct Transport client has none, so that node may show the bare name until the next snapshot.
        seen = _poll(lambda: names(identity)["wa-1"] == expected or None, HANDOVER_TIMEOUT)
        bench.record(f"rename-{client_id}", {"identity": identity, "old": old, "new": new, "names": names(identity)})
        bench.check(f"{old!r} logging in as {new!r} is {expected!r} on the WA", seen, json.dumps(names(identity)))
        return identity

    bot = relog(CLIENT_ID, None, "SE-1100 Bot", "[B] SE-1100 Bot", "[B] SE-1100 Bot")
    human = relog(CLIENT2_ID, CLIENT2_PORT, "\ue030SE-1100 Steam", "SE-1100 Steam 2", "\ue030SE-1100 Steam 2")
    # Only its binding matters: the WA publishes a snapshot of every identity with it. The client itself is
    # not kept (the backend drops a third headless client on this bench).
    bench.start_client(CLIENT3_ID, CLIENT3_PORT, render=False, name="SE-1100 Third")
    _wait_line(bench, "gateway", rf"Player binding granted client {CLIENT3_ID} identity", CLIENT_TIMEOUT)
    bench.kill_client(CLIENT3_ID)
    time.sleep(5)
    for identity, name in ((bot, "[B] SE-1100 Bot"), (human, "\ue030SE-1100 Steam 2")):
        bench.check(f"{name!r} stayed on every role after the next identity snapshot", everywhere(identity, name),
                    json.dumps(names(identity)))
    bench.exercised(route="relog under a new name, then another player's binding snapshot", bot=bot, human=human)


def _gps_state(bench, node_id: str) -> dict:
    state = _node(bench, node_id, "/global-state")
    gps = next(x for x in state["subsystems"] if x["subsystem"] == "gps")
    return {"generation": gps.get("generation"), "sequence": gps.get("sequence"), "bytes": len(gps.get("payload") or ""),
            "gaps": gps.get("gaps"), "snapshots": gps.get("snapshots")}


def p2_wa_08(bench) -> None:
    """T-0229: a global-state frame larger than the relay IPC's fragment size - a live world's GPS table
    (2.6 MB on w32) - reaches every node, both as a full-state publish and as a corrective snapshot.
    IPC version 1 dropped every frame above 256 KiB: the nodes gapped, re-requested the snapshot every
    second, and wa-1 queued a new multi-MB snapshot per request until it ran out of memory. The bench
    world's own GPS table is tiny, so the WA test route grows it first."""
    _wa_cluster(bench)
    regular = ("node-1", "node-2")
    # Few, large entries: the WA publishes the WHOLE table on every change, so 800 small adds were
    # 800 publishes of up to 1.9 MB (~1.5 GB per node), which neither relay drains in a minute
    # (2026-09-24: internal and gateway-rs both stopped near sequence 460 of 801).
    # GpsAuthorityModel.Valid caps a description at 65536 characters (an 80000-character entry made
    # every node reject the table: "Invalid GPS snapshot entries").
    count, size = 32, 40000
    # P2_WA_08_GPS=COUNTxSIZE overrides it, e.g. 800x2000 for the full-table-per-change burst (T-0236)
    # that pushes ~1.5 GB through the relay: a load for gateway-rs memory, not a delivery verdict.
    if os.environ.get("P2_WA_08_GPS"):
        count, size = (int(x) for x in os.environ["P2_WA_08_GPS"].lower().split("x"))
    bulk = _post(bench, "wa-1", f"/gps-test-bulk?count={count}&bytes={size}", timeout=120)
    bench.record("gps-bulk", bulk)
    _require(bench, f"the WA added {count} GPS entries of {size} characters", bulk.get("added") == count, json.dumps(bulk))
    wa = _poll(lambda: (s := _gps_state(bench, "wa-1"))["bytes"] >= count * size and s, 30)
    _require(bench, f"SETUP: the WA publishes a GPS table of at least {count} x {size} bytes (well above the 64 KiB fragment size)",
             wa, json.dumps(_gps_state(bench, "wa-1")))

    def rejected():
        return {n: _lines(bench, n, r"apply_failed=true subsystem=gps")[:3] for n in regular}

    def converged(nodes):
        target = _gps_state(bench, "wa-1")
        states = {n: _gps_state(bench, n) for n in nodes}
        ok = all(st["sequence"] == target["sequence"] and st["bytes"] == target["bytes"] for st in states.values())
        return ok and {"wa-1": target, **states}

    if os.environ.get("P2_WA_08_GPS"):
        # Memory-load mode: let the relay drain the whole burst, then 60 s of quiet for the relay's
        # resident memory to settle (or not). No delivery verdict.
        drained = _poll(lambda: converged(regular), 600)
        bench.record("gps-load-drained", drained or {n: _gps_state(bench, n) for n in ("wa-1", *regular)})
        time.sleep(60)
        bench.check("memory-load mode: the burst drained, then 60 s quiet (no delivery verdict)", bool(drained))
        return
    published = _poll(lambda: converged(regular), 60)
    # A node that REJECTS the table (codec validation) is a broken setup, not a relay failure: an
    # 80000-character description once made every arm fail alike. Delivery is what this measures.
    bad = {n: lines for n, lines in rejected().items() if lines}
    _require(bench, "SETUP: no node rejected a GPS apply (INVALID RUN otherwise, not a relay verdict)", not bad, json.dumps(bad)[:400])
    bench.record("gps-publish-converged", published or {"wa-1": _gps_state(bench, "wa-1"), **{n: _gps_state(bench, n) for n in regular}})
    _require(bench, "every regular node applied the multi-MB GPS publish (same sequence and size as the WA)", published)

    # A corrective snapshot: node-1 loses its global frames for 10 s while the WA changes the table,
    # then one more change shows node-1 the gap, and it must pull the full table as a snapshot.
    before = _gps_state(bench, "node-1")
    _post(bench, "node-1", "/test-wa-partition?ms=10000")
    _post(bench, "wa-1", "/gps-test-bulk?count=1&bytes=100")
    time.sleep(11)
    _post(bench, "wa-1", "/gps-test-bulk?count=1&bytes=100")
    healed = _poll(lambda: converged(["node-1"]), 60)
    after = _gps_state(bench, "node-1")
    bench.record("gps-snapshot-healed", {"before": before, "after": after, "wa-1": _gps_state(bench, "wa-1")})
    bad = {n: lines for n, lines in rejected().items() if lines}
    _require(bench, "SETUP: no node rejected a GPS apply after the outage (INVALID RUN otherwise)", not bad, json.dumps(bad)[:400])
    _require(bench, "node-1 caught up with the WA's multi-MB GPS table after the outage", healed, json.dumps(after))
    bench.check("node-1 healed through a corrective snapshot (its snapshot count rose)", after["snapshots"] > before["snapshots"],
                f"{before['snapshots']} -> {after['snapshots']}")

    # No resync loop afterwards: the GPS gap and snapshot counts stay put on every node for 20 s.
    settled = {n: _gps_state(bench, n) for n in regular}
    time.sleep(20)
    later = {n: _gps_state(bench, n) for n in regular}
    bench.record("gps-settled", {"at": settled, "20s-later": later})
    bench.check("no GPS resync loop: gaps and snapshots unchanged on every node over 20 s",
                all((later[n]["gaps"], later[n]["snapshots"]) == (settled[n]["gaps"], settled[n]["snapshots"]) for n in regular),
                json.dumps({"at": settled, "later": later}))
    bench.exercised(gpsEntries=count, gpsBytes=wa["bytes"])


def p2_wa_econ(bench) -> None:
    """WA-2 economy: store, trade and contract settlement, the global PCU ledger, and a cross-node grid
    ownership transfer, with client 1 on node-1 and client 2 on node-2. The respawn screen of a client with no
    character previews the first spawn point it may use, and moves the client to that point's node (SE1-0122),
    so client 2 joins with node-1's spawn points switched off."""
    ctx = _founder(bench)
    def points(node_id: str) -> list[dict]:
        rows = (_try(bench, node_id, "/respawn-points") or {}).get("points") or []
        return [p for p in rows if p.get("Working")]

    switched = _poll(lambda: points("wa-1"), HANDOVER_TIMEOUT)
    _require(bench, "node-1's spawn points converged at the World Authority", switched)
    for point in switched:
        _post(bench, point["Origin"], f"/respawn-test-enable?entity={point['RespawnEntityId']}&enabled=false")
    _require(bench, "no spawn point is left for client 2 to preview", _poll(lambda: not points("node-2") and not points("wa-1"), HANDOVER_TIMEOUT))
    ctx.api2 = start_second_client(bench, "node-2")
    ctx.client2_identity = (_session(bench, CLIENT2_ID) or {}).get("identityId") or 0
    _require(bench, "client 2 has a WA identity", ctx.client2_identity > 0)
    for name, leg in (("store", _store_payments), ("trade", _trade), ("client-balance-events", _client_balance_events), ("contract-activation", _contract_activation),
                      ("contract-settlement", _contract_settlement), ("contract-split", _contract_split),
                      ("pcu-ledger", _pcu_ledger), ("pcu-simultaneous", _pcu_simultaneous), ("grid-transfer", _grid_transfer)):
        _leg(bench, name, leg, bench, ctx)
    bench.exercised(clients={"node-1": CLIENT_ID, "node-2": CLIENT2_ID})


def p2_wa_03(bench) -> None:
    """WA-3: spawns, drop containers and encounters decided on the WA run exactly once on the owning
    node; no cross-node trash collection; WA-side identity cleanup replicates."""
    ctx = _founder(bench)
    for name, leg in (("spawn-director", _spawn_director), ("drop-container", _drop_container), ("encounters", _encounters),
                      ("planetary", _planetary), ("cargo", _cargo), ("meteor", _meteor), ("cadence", _encounter_cadence),
                      ("trash", _trash_safety), ("identity-trash", _identity_trash)):
        _leg(bench, name, leg, bench, ctx)
    bench.exercised(target=CLIENT_ID)


def p2_mod_01(bench) -> None:
    """Cross-node IGC unicast and broadcast (no leak to the sender), a relay chain, and a laser link."""
    ctx = _founder(bench)
    hosts = _igc_hosts(bench, ctx.binding_identity)
    _leg(bench, "unicast-broadcast", _igc_unicast_broadcast, bench, ctx, hosts)
    _leg(bench, "relay-chain", _igc_relay_chain, bench, ctx, ctx.binding_identity)
    _leg(bench, "laser", _igc_laser, bench, ctx, ctx.binding_identity)
    bench.exercised(endpoints={"node-1": hosts.pb1, "node-2": hosts.pb2})


def p2_mod_02(bench) -> None:
    """WA-4: handover buffering across a frozen window. A unicast that arrives while its addressee's
    partition is frozen for transfer would be enqueued on the already-captured copy and die at release;
    the delivery service must hold it and forward it to the partition's new owner with stream identity
    intact. pauseAfterCapture makes the frozen window deterministic instead of a race."""
    ctx = _founder(bench)
    hosts = _igc_hosts(bench, ctx.binding_identity)
    pb1, pb2 = hosts.pb1, hosts.pb2
    # The sends run node-2 -> node-1, the reverse of the direction the host setup proved; make sure that
    # path has converged too before opening the frozen window, which runs against the handover timeout.
    _require(bench, "the reverse IGC path became antenna-reachable",
             _poll(lambda: (_node(bench, "node-2", f"/igc-test-reachability?source={pb2}&addressee={pb1}").get("reach") or {})
                   .get("reachable") is True, HANDOVER_TIMEOUT))
    # Broadcast-to-frozen-listener: register a broadcast listener on the grid about to freeze, and wait
    # for the cluster listener registry to carry the tag so node-2 fans the broadcast to node-1.
    _post(bench, "node-1", f"/igc-test-listen?address={pb1}&tag=cluster-ho-bcast")
    _require(bench, "the listener registry carried the frozen-window broadcast tag",
             _poll(lambda: _listener_carried(bench, "node-2", "node-1", "cluster-ho-bcast"), 30))
    partition = _node(bench, "node-1", f"/partition-of?entity={hosts.grid1}").get("partition") or 0
    _require(bench, "the node-1 IGC host grid belongs to a partition", partition, f"grid={hosts.grid1}")
    outcome = {}

    def handover():
        try:
            outcome["result"] = bench.cluster.post(_handover_url(bench, partition, clientless=True) + "&pauseAfterCapture=true",
                                                   timeout=120)
        except Exception as error:  # noqa: BLE001
            outcome["error"] = str(error)
    thread = threading.Thread(target=handover, daemon=True)
    thread.start()
    transfer = None
    try:
        def paused():
            rows = bench.cluster.get("/handover/paused") or []
            return next((row["transferId"] for row in rows if row.get("partition") == partition and row.get("paused") is True), None)
        transfer = _poll(paused, HANDOVER_TIMEOUT)
        _require(bench, "the paused IGC handover reached its capture pause", transfer)
        for index in range(1, 9):
            send = _post(bench, "node-2", f"/igc-test-unicast?source={pb2}&addressee={pb1}&tag=cluster-handover&data=ho-{index}")
            _require(bench, f"the frozen-window unicast ho-{index} was accepted", send.get("sent") is True, json.dumps(send))
        # A broadcast arriving during the frozen window must not die on the captured copy: node-1 skips the
        # frozen listener and holds a scoped copy that follows the grid to its new owner.
        send = _post(bench, "node-2", f"/igc-test-broadcast?source={pb2}&tag=cluster-ho-bcast&data=ho-bcast-1")
        _require(bench, "the frozen-window broadcast was accepted", send.get("sent") is True, json.dumps(send))
        held = {}
        _require(bench, "the frozen window held 9 IGC messages (8 unicasts + 1 scoped broadcast copy)",
                 _poll(lambda: held.update(_node(bench, "node-1", "/igc-status")) or held.get("held") == 9, 15),
                 f"held={held.get('held')} journal={json.dumps(held.get('journal'))[:300]}")
        held_count = held["held"]
        bench.cluster.post(f"/handover/release?transferId={transfer}")
        released = transfer
        transfer = None
    finally:
        if transfer is not None:
            try:
                bench.cluster.post(f"/handover/release?transferId={transfer}")
            except ClusterError:
                pass
    thread.join(timeout=125)
    _require(bench, "the paused IGC handover completed after release", not thread.is_alive() and "error" not in outcome,
             json.dumps(outcome)[:300])
    # Drain on the new owner; the forward waits for the endpoint registry to name the new node, which
    # takes a report cycle or two after import.
    drained = _collect(bench, "node-2", pb1, "unicast", "cluster-handover", lambda d: len(d) >= 8, HANDOVER_TIMEOUT)
    bench.check("the handover-buffered IGC messages arrived on the new owner, in order, once",
                drained == [f"ho-{i}" for i in range(1, 9)],
                f"drained={drained} journal={json.dumps(_try(bench, 'node-1', '/igc-status'))[:300]}")
    bench.check("the source node holds no IGC messages after the handover", _node(bench, "node-1", "/igc-status").get("held") == 0)
    # The moved block's listener registration is runtime state the transfer destroyed; a real PB script
    # re-registers it when it recompiles on the new node. Mirror that, then the forwarded scoped copy
    # must reach exactly this listener - once, and never re-fan to node-2's others.
    _post(bench, "node-2", f"/igc-test-listen?address={pb1}&tag=cluster-ho-bcast")
    broadcast = _collect(bench, "node-2", pb1, "broadcast", "cluster-ho-bcast", lambda d: len(d) >= 1, 45)
    time.sleep(2)  # a duplicate forward would land inside this window
    broadcast += [row.get("data") for row in _received(bench, "node-2", pb1).get("broadcast") or []
                  if row.get("tag") == "cluster-ho-bcast"]
    bench.check("the frozen-window broadcast reached the moved listener exactly once", broadcast == ["ho-bcast-1"], json.dumps(broadcast))
    others = [row.get("data") for row in _received(bench, "node-2", pb2).get("broadcast") or [] if row.get("tag") == "cluster-ho-bcast"]
    bench.check("the forwarded scoped broadcast copy did not re-fan to a listener that was not frozen", others == [], json.dumps(others))
    source = _node(bench, "node-1", "/igc-status")
    bench.record("world-authority-igc-handover", {
        "partition": partition, "transfer": released, "heldDuringWindow": held_count, "drained": drained,
        "broadcastDrained": broadcast, "handover": outcome,
        "source": {"held": source.get("held"), "journal": [row for row in source.get("journal") or []
                                                           if row.get("tag") in ("cluster-handover", "cluster-ho-bcast")]}})
    bench.exercised(partition=partition, transfer=released, held=held_count)


def p2_mod_03(bench) -> None:
    """WA-4: the vanilla per-listener cap - 25 pending messages, oldest dropped on the 26th enqueue - holds
    for cluster-delivered messages too. ClusterDeliverMessage funnels into the same
    MyMessageListener.EnqueueMessage as local delivery, so a 27-message undrained burst must retain
    exactly cap-3..cap-27 in order."""
    ctx = _founder(bench)
    hosts = _igc_hosts(bench, ctx.binding_identity)
    _post(bench, "node-2", f"/igc-test-listen?address={hosts.pb2}&tag=cluster-cap")
    _require(bench, "the listener registry carried the cap-test tag",
             _poll(lambda: _listener_carried(bench, "node-1", "node-2", "cluster-cap"), HANDOVER_TIMEOUT))
    for index in range(1, 28):
        _post(bench, "node-1", f"/igc-test-broadcast?source={hosts.pb1}&tag=cluster-cap&data=cap-{index}")
    # Drain only after the sender journals all 27 as delivered, or the drain races the burst tail.
    journal = {}
    _require(bench, "the 27-message IGC burst was fully delivered",
             _poll(lambda: journal.update(_node(bench, "node-1", "/igc-status")) or
                   sum(1 for row in journal.get("journal") or [] if row.get("tag") == "cluster-cap" and row.get("delivered") is True) == 27,
                   30), json.dumps([row for row in journal.get("journal") or [] if row.get("tag") == "cluster-cap"])[:300])
    received = _received(bench, "node-2", hosts.pb2)
    kept = [row.get("data") for row in received.get("broadcast") or [] if row.get("tag") == "cluster-cap"]
    bench.check("cluster-delivered messages honor the vanilla 25-message oldest-drop cap",
                kept == [f"cap-{i}" for i in range(3, 28)], json.dumps(kept))
    bench.record("world-authority-igc-cap", {"received": received, "status": journal})
    bench.exercised(burst=27, kept=len(kept))


def p2_mod_04(bench) -> None:
    """The cluster mod channel through the exact IMyUtilities seat a mod occupies: a request from node-1
    routed to the World Authority, and the response routed back to its origin."""
    _wa_cluster(bench)
    _mod_drain(bench)
    _post(bench, "node-1", "/mod-test-send?kind=request&channel=cluster-mod-probe&payload=census-ping&correlation=4242")

    def request_at_wa():
        probe = _node(bench, "wa-1", "/mod-test-received")
        return probe if any(r.get("kind") == "request" and r.get("channel") == "cluster-mod-probe" and r.get("payload") == "census-ping"
                            and r.get("correlation") == 4242 and r.get("origin") == "node-1" for r in probe.get("received") or []) else None
    wa_request = _poll(request_at_wa, 30)
    _require(bench, "the mod channel request reached the World Authority", wa_request)
    _post(bench, "wa-1", "/mod-test-send?kind=respond&channel=cluster-mod-probe&payload=census-pong&correlation=4242&replyTo=node-1")

    def response_at_origin():
        probe = _node(bench, "node-1", "/mod-test-received")
        return probe if any(r.get("kind") == "response" and r.get("channel") == "cluster-mod-probe" and r.get("payload") == "census-pong"
                            and r.get("correlation") == 4242 and r.get("origin") == "wa-1" for r in probe.get("received") or []) else None
    response = _poll(response_at_origin, 30)
    bench.check("the mod channel response returned to the requesting node", response)
    status = _node(bench, "node-1", "/mod-status")
    bench.record("world-authority-mod-channel", {"waRequest": wa_request, "response": response, "node1": {
        "sent": status.get("sent"), "faults": status.get("faults"),
        "journal": [row for row in status.get("journal") or [] if row.get("channel") == "cluster-mod-probe"]}})
    bench.exercised(correlation=4242)


def p2_mod_04_restart(bench) -> None:
    """SE1-0086: a node that restarts keeps its name, and its mod channel sequence numbers start again at 1.
    Its first request after the restart must still reach the World Authority, not be refused as a duplicate
    of the requests the previous process sent."""
    _wa_cluster(bench)
    _mod_drain(bench)

    seen: list[dict] = []

    def at_wa(payload: str):
        def probe():
            seen.extend(_node(bench, "wa-1", "/mod-test-received").get("received") or [])
            return any(r.get("kind") == "request" and r.get("payload") == payload and r.get("origin") == "node-1"
                       for r in seen) or None
        return _poll(probe, 30)

    for index in range(1, 4):
        _post(bench, "node-1", f"/mod-test-send?kind=request&channel=cluster-mod-restart&payload=before-{index}"
                               f"&correlation={4300 + index}")
        _require(bench, f"request {index} before the restart reached the World Authority", at_wa(f"before-{index}"))

    bench.cluster.restart_node("node-1")
    bench.cluster.wait_node_log("node-1", "Game ready", timeout=SERVER_TIMEOUT)
    # The test plugin's routes come up after "Game ready"
    _poll(lambda: _try(bench, "node-1", "/mod-test-received"), SERVER_TIMEOUT)
    _post(bench, "node-1", "/mod-test-send?kind=request&channel=cluster-mod-restart&payload=after&correlation=4400")
    arrived = at_wa("after")
    status = _node(bench, "node-1", "/mod-status")
    journal = [row for row in status.get("journal") or [] if row.get("channel") == "cluster-mod-restart"]
    bench.record("mod-channel-after-restart", {"arrived": arrived, "journal": journal,
                                               "faults": status.get("faults")})
    bench.check("the restarted node's first request reached the World Authority", arrived, json.dumps(journal)[:400])


def p2_mod_05(bench) -> None:
    """Mod channel quota: a 33-message broadcast flood from one node faults the over-limit sends back to
    the sending mod (the channel's only error signal), and the far node receives exactly the admitted
    30, in order."""
    _wa_cluster(bench)
    _mod_drain(bench)
    for index in range(1, 34):
        _post(bench, "node-1", f"/mod-test-send?kind=broadcast&channel=cluster-mod-quota&payload=mod-q-{index}")
    faults = [row for row in _node(bench, "node-1", "/mod-test-received").get("received") or []
              if row.get("kind") == "fault" and row.get("channel") == "cluster-mod-quota"]
    bench.check("the mod channel quota faulted the over-limit sends",
                len(faults) >= 1 and all("quota" in (row.get("payload") or "") for row in faults), json.dumps(faults)[:300])
    expected = [f"mod-q-{i}" for i in range(1, 31)]
    arrived = []

    def ordered():
        batch = _node(bench, "node-2", "/mod-test-received").get("received") or []
        arrived.extend(row.get("payload") for row in batch if row.get("kind") == "broadcast" and row.get("channel") == "cluster-mod-quota")
        return arrived == expected or len(arrived) > 30
    _poll(ordered, 30)
    bench.check("node-2 received exactly mod-q-1..30, in order", arrived == expected, json.dumps(arrived))
    bench.record("world-authority-mod-quota", {"quotaFaults": faults, "node2Broadcasts": arrived})
    bench.exercised(flood=33, faults=len(faults), delivered=len(arrived))


def _laser_gps_errors(bench, node_id: str) -> list[str]:
    """Exception lines of the laser state machine on a node: process log and game log (SE1-0226)."""
    from scenarios_wa_ops import _game_log
    pattern = re.compile(r"MyLaserAntenna\.UpdateAfterSimulation100")
    return [line for line in (_lines(bench, node_id, pattern.pattern) + _game_log(bench, node_id).splitlines())
            if pattern.search(line)]


def p2_mod_laser_gps(bench) -> None:
    """SE1-0226, SE1-0235 - a laser antenna linking by GPS to a laser on another node. Clientless: two
    unowned laser stations 3 km apart, A on node-1 and B on node-2, each node holding the other's laser
    as a proxy. Leg one: A searches for B's coordinates while B is idle - vanilla's GPS search read
    RealAntenna of every laser in the dictionary, null for a proxy, and threw every 100 frames; the
    search must find B through its proxy, and node-2 must point the idle B back so the link connects.
    Leg two: B is set idle while A still targets it, and stays idle. Leg three: B searches for A's
    coordinates and the link connects again."""
    _wa_cluster(bench)
    stations = {}
    for node_id, name, z in (("node-1", "laser-gps-a", 0), ("node-2", "laser-gps-b", 3000)):
        stations[node_id] = _post(bench, node_id, f"/igc-test-ensure-laser-at?{_q(name=name, x=870000, y=0, z=z)}")
    bench.record("laser-gps-stations", stations)
    grid_a, laser_a = stations["node-1"].get("grid"), stations["node-1"].get("laser")
    grid_b, laser_b = stations["node-2"].get("grid"), stations["node-2"].get("laser")
    _require(bench, "the laser stations exist on node-1 and node-2", grid_a and grid_b and laser_a and laser_b,
             json.dumps(stations))
    status = {}

    def read():
        status["a"] = _node(bench, "node-1", f"/igc-test-laser-status?grid={grid_a}")
        status["b"] = _node(bench, "node-2", f"/igc-test-laser-status?grid={grid_b}")
        return status

    def linked():
        return (read()["a"]["state"] == "connected" and status["b"]["state"] == "connected"
                and status["a"]["targetId"] == laser_b and status["b"]["targetId"] == laser_a)

    def point_backs():
        return _lines(bench, "node-2", rf"laser_point_back laser={laser_b} caller={laser_a}\b")
    _require(bench, "each node holds the other's laser as a proxy",
             _poll(lambda: laser_b in read()["a"]["proxyLasers"] and laser_a in status["b"]["proxyLasers"],
                   HANDOVER_TIMEOUT), json.dumps(status))
    head_a, head_b = status["a"]["head"], status["b"]["head"]
    before = {n: len(_laser_gps_errors(bench, n)) for n in ("node-1", "node-2")}

    # Leg one: only A searches. Its node points A at B; node-2 sees A linking to B and points B back.
    _post(bench, "node-1", f"/igc-test-laser-gps?{_q(grid=grid_a, x=head_b[0], y=head_b[1], z=head_b[2])}")
    found = _poll(lambda: read()["a"]["targetId"] == laser_b, 60)
    errors = _laser_gps_errors(bench, "node-1")[before["node-1"]:]
    bench.record("laser-gps-node-1-errors.txt", "\n".join(errors))
    bench.check("one-sided: the GPS search on node-1 threw nothing while B's proxy was there",
                not errors, " | ".join(errors[:4]))
    bench.check("one-sided: A found B through its proxy and points at it", found,
                f"state={status['a']['state']} target={status['a']['targetId']} error={status['a']['connectionError']}")
    connected = _poll(linked, HANDOVER_TIMEOUT)
    bench.record("laser-gps-one-sided", status)
    bench.check("one-sided: node-2 pointed the idle B back at A", any("pointed=True" in line for line in point_backs()),
                " | ".join(point_backs()))
    bench.check("one-sided: the cross-node GPS link connected", connected, json.dumps(status))

    # Leg two: B goes idle while A still targets it. The request was answered once; B must stay idle.
    _post(bench, "node-2", f"/igc-test-laser-idle?{_q(grid=grid_b)}")
    answered = len(point_backs())
    relinked = _poll(lambda: read()["b"]["state"] != "idle", 30)
    bench.record("laser-gps-idled", status)
    bench.check("idled: B stayed idle while A still targets it", not relinked
                and status["a"]["targetId"] == laser_b, json.dumps(status))
    bench.check("idled: node-2 did not point B back again", len(point_backs()) == answered, " | ".join(point_backs()))

    # Leg three: B searches for A; both sides point at each other and the link connects again.
    _post(bench, "node-2", f"/igc-test-laser-gps?{_q(grid=grid_b, x=head_a[0], y=head_a[1], z=head_a[2])}")
    connected = _poll(linked, HANDOVER_TIMEOUT)
    bench.record("laser-gps-two-sided", status)
    bench.check("two-sided: the cross-node GPS link connected again", connected, json.dumps(status))
    errors = {n: _laser_gps_errors(bench, n)[before[n]:] for n in ("node-1", "node-2")}
    bench.check("no laser exception on either node", not any(errors.values()),
                " | ".join(line for lines in errors.values() for line in lines[:2]))
    bench.exercised(lasers={"node-1": laser_a, "node-2": laser_b}, legs="one-sided, idled, two-sided")


def _gps_table(bench, node_id: str, prefix: str | None = None) -> dict:
    return _node(bench, node_id, "/gps-test-table" + (f"?{_q(prefix=prefix)}" if prefix else ""))


def _gps_op(bench, node_id: str, op: str, identity: int, name: str, **extra) -> dict:
    return _post(bench, node_id, f"/gps-test-op?{_q(op=op, identity=identity, name=name, **extra)}")


def p2_wa_gps(bench) -> None:
    """#1405: GPS add, modify and delete requested from either regular node or the WA itself, on three
    identities, reach every node with the WA's exact table (a digest of every entry), also after a burst
    of modifies and after a node restart. The WA re-reads only the changed identity per change since
    #1405; before, each Modify rebuilt the whole table. P2_WA_GPS_TRACE=DIR also traces the WA during
    a steady modify load (dotnet-trace, speedscope) for the game-thread cost."""
    _wa_cluster(bench)
    nodes = ("wa-1", "node-1", "node-2")
    if os.environ.get("P2_WA_GPS_SELFTEST", "1") == "1":  # 0 for a package before #1405, which has no such section
        try:
            selftest = _node(bench, "wa-1", "/self-test?section=gps-snapshot-parts")
        except Exception as error:  # noqa: BLE001 - a failed self-test is a check result
            selftest = {"error": str(error)[:300]}
        bench.check("WA self-test gps-snapshot-parts (per-identity parts give the whole-table serializer's text)",
                    selftest.get("ok") is True, json.dumps(selftest)[:300])
    # A live-sized table first: the cost being fixed grows with the table, the semantics must not.
    bulk = _post(bench, "wa-1", "/gps-test-bulk?count=2000&bytes=300", timeout=120)
    bench.record("gps-bulk", bulk)
    _require(bench, "SETUP: the WA added 2000 GPS entries", bulk.get("added") == 2000, json.dumps(bulk)[:300])
    identities = [i for i in _gps_table(bench, "node-1")["identities"] if i > 0][:3]
    _require(bench, "SETUP: node-1 knows three identities to hold GPS entries", len(identities) == 3, json.dumps(identities))
    tag = f"p2gps-{random.randrange(1 << 20):05x}-"

    def converged(expect=None):
        tables = {n: _gps_table(bench, n, tag) for n in nodes}
        digests = {t["sha256"] for t in tables.values()}
        if len(digests) != 1:
            return None
        if expect is not None and not expect({(e["identity"], e["name"]): e for e in tables["wa-1"]["entries"]}):
            return None
        return tables

    def step(label, expect, timeout=30):
        tables = _poll(lambda: converged(expect), timeout)
        bench.record("gps-" + re.sub(r"[^a-z0-9]+", "-", label.lower())[:40],
                     tables or {n: _gps_table(bench, n, tag) for n in nodes})
        _require(bench, label, tables, "" if tables else json.dumps({n: _gps_table(bench, n, tag)["count"] for n in nodes}))
        return tables

    i1, i2, i3 = identities
    _gps_op(bench, "node-1", "add", i1, tag + "a1", x=1)
    _gps_op(bench, "node-1", "add", i1, tag + "a2", x=2)
    _gps_op(bench, "node-2", "add", i2, tag + "b1", x=3)
    _gps_op(bench, "wa-1", "add", i3, tag + "w1", x=4)
    step("adds from node-1, node-2 and the WA on three identities reach every node (same table digest)",
         lambda e: {(i1, tag + "a1"), (i1, tag + "a2"), (i2, tag + "b1"), (i3, tag + "w1")} <= set(e)
         and e[(i1, tag + "a1")]["x"] == 1 and e[(i3, tag + "w1")]["x"] == 4)
    _gps_op(bench, "node-1", "modify", i1, tag + "a1", x=11, description="moved")
    _gps_op(bench, "node-2", "modify", i2, tag + "b1", newName=tag + "b1-renamed")
    _gps_op(bench, "wa-1", "modify", i3, tag + "w1", x=44)
    step("modifies (coords + description, a rename, a WA-local modify) reach every node",
         lambda e: e.get((i1, tag + "a1"), {}).get("x") == 11 and e[(i1, tag + "a1")]["description"] == "moved"
         and (i2, tag + "b1-renamed") in e and (i2, tag + "b1") not in e and e.get((i3, tag + "w1"), {}).get("x") == 44)
    _gps_op(bench, "node-2", "delete", i1, tag + "a2")
    _gps_op(bench, "node-1", "delete", i3, tag + "w1")
    step("deletes requested from the other node reach every node",
         lambda e: (i1, tag + "a2") not in e and (i3, tag + "w1") not in e and (i1, tag + "a1") in e)
    # Descriptions: the hash covers name and coords, so a coords modify re-keys the entry and the next one sent from a
    # replica that has not caught up names a hash the WA no longer has (vanilla, before and after #1405).
    for n in range(50):
        _gps_op(bench, "node-1", "modify", i1, tag + "a1", description=f"burst-{n}")
    step("a burst of 50 modifies ends at the last value on every node",
         lambda e: e.get((i1, tag + "a1"), {}).get("description") == "burst-49")
    full = {n: _gps_table(bench, n) for n in nodes}
    bench.check("the whole table (bulk entries included) has one digest on every node",
                len({t["sha256"] for t in full.values()}) == 1, json.dumps({n: [t["count"], t["sha256"][:12]] for n, t in full.items()}))

    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", RECOVERY_TIMEOUT)
    step("after a node-2 restart its table matches the WA's", None, 120)
    _gps_op(bench, "node-2", "modify", i2, tag + "b1-renamed", x=7)
    _gps_op(bench, "node-2", "add", i3, tag + "w2", x=8)
    step("node-2's requests after its restart reach every node",
         lambda e: e.get((i2, tag + "b1-renamed"), {}).get("x") == 7 and (i3, tag + "w2") in e)
    full = {n: _gps_table(bench, n) for n in nodes}
    bench.check("after the restart the whole table has one digest on every node",
                len({t["sha256"] for t in full.values()}) == 1, json.dumps({n: [t["count"], t["sha256"][:12]] for n, t in full.items()}))

    trace_dir = os.environ.get("P2_WA_GPS_TRACE")
    if trace_dir:
        # Steady load: 20 modifies/s from node-1 for 30 s; the WA traced for the 20 s in the middle.
        # P2_WA_GPS_LOAD=small modifies an entry of an identity with one entry (a pilot's own GPS in a big table),
        # the default one of the identity that also holds the 2000 bulk entries (the worst case per identity).
        small = os.environ.get("P2_WA_GPS_LOAD") == "small"
        load_identity, load_name = (i2, tag + "b1-renamed") if small else (i1, tag + "a1")
        pid = bench.cluster._node_pid("wa-1")  # the process itself, not its bash launch wrapper
        environ = dict(item.split(b"=", 1) for item in open(f"/proc/{pid}/environ", "rb").read().split(b"\0") if b"=" in item)
        trace_env = {**os.environ, "TMPDIR": environ.get(b"TMPDIR", b"/tmp").decode()}  # its diagnostics socket
        sent, stop = [], threading.Event()

        def load():
            while not stop.is_set():
                started = time.time()
                _gps_op(bench, "node-1", "modify", load_identity, load_name, description=f"load-{len(sent)}")
                sent.append(started)
                time.sleep(max(0.0, 0.05 - (time.time() - started)))

        worker = threading.Thread(target=load, daemon=True)
        worker.start()
        time.sleep(5)
        out = os.path.join(trace_dir, "wa-gps")
        t0 = time.time()
        trace = subprocess.run([os.path.expanduser("~/.dotnet/tools/dotnet-trace"), "collect", "-p", str(pid),
                                "--duration", "00:00:00:20", "--format", "Speedscope", "-o", out + ".nettrace"],
                               capture_output=True, text=True, timeout=180, env=trace_env)
        t1 = time.time()
        time.sleep(3)
        stop.set()
        worker.join(30)
        during = sum(1 for t in sent if t0 <= t <= t1)
        bench.record("gps-trace", {"pid": pid, "load": "small" if small else "bulk", "modifiesDuringTrace": during, "traceSeconds": round(t1 - t0, 1),
                                   "exit": trace.returncode, "stderr": trace.stderr[:800], "out": out})
        bench.check("the WA trace ran during the modify load", trace.returncode == 0 and during > 100,
                    f"exit {trace.returncode}, {during} modifies in {t1 - t0:.0f} s")
        step("the modify load ends at its last value on every node",
             lambda e: e.get((load_identity, load_name), {}).get("description") == f"load-{len(sent) - 1}", 60)
    bench.exercised(identities=identities, tag=tag, ops="add x4, modify x3, delete x2, burst x50, node-2 restart, add+modify after")


SCENARIOS_WA = {
    "P2-WA-00": (p2_wa_00, "World Authority role, clock and replacement", "3"),
    "P2-WA-GPS": (p2_wa_gps, "#1405: GPS add, modify and delete from any node on three identities converge on every node, "
                             "also after a modify burst and a node restart", "3"),
    "P2-WA-08": (p2_wa_08, "a multi-MB global-state frame (a live-sized GPS table) reaches every node through the relay (T-0229)", "3"),
    "P2-WA-07": (p2_wa_07, "a WA killed before its newest global save replicated boots from a live replica (T-0230)", "3"),
    "P2-WA-09": (p2_wa_09, "#1318: banking and identity answers after the last global save survive a WA kill "
                           "(write-ahead journal)", "3"),
    "P2-WA-01": (p2_wa_01, "cross-node chat, faction and GPS", "3"),
    "P2-WA-03": (p2_wa_03, "global encounter dispatched once cluster-wide; counts converge", "3"),
    "P2-WA-RENAME": (p2_wa_rename, "SE-1100: a player logging in under a new name keeps it on every role after the WA's next "
                                   "identity snapshot, a stored platform icon too", "3"),
    "P2-WA-RESPAWN-POINTS": (p2_wa_respawn_points, "SE-1150: a bad respawn point is repaired or dropped at the WA, the rest of "
                                                   "the node's points kept, each logged once", "3"),
    "P2-WA-BCAST": (p2_wa_bcast, "SE-943: a broadcast controller message names its block, not Good.bot, after the WA's chat path", "3"),
    "P2-WA-ECON": (p2_wa_econ, "WA-2 economy: store, trade, contracts, PCU ledger, grid transfer", "3"),
    "P2-WA-STORE-TIMEOUT": (p2_wa_store_timeout, "SE-1226: a store payment leg the WA answers after the node's timeout is "
                                                 "settled once, never paid and refunded both", "3"),
    "P2-MOD-LASER-GPS": (p2_mod_laser_gps, "SE1-0226, SE1-0235: a laser links by GPS to a laser on another node, which points back", "3"),
    "P2-MOD-01": (p2_mod_01, "cross-node IGC unicast and broadcast, no leak to other nodes", "3"),
    "P2-MOD-02": (p2_mod_02, "IGC in flight across a handover: WA buffering, order preserved", "3"),
    "P2-MOD-03": (p2_mod_03, "listener queue cap of 25, oldest dropped - vanilla parity", "3"),
    "P2-MOD-04": (p2_mod_04, "mod channel request/response across nodes and the WA", "4"),
    "P2-MOD-04-RESTART": (p2_mod_04_restart, "mod channel request from a node that restarted under the same name", "4"),
    "P2-MOD-05": (p2_mod_05, "mod channel quota faults under a flood", "4"),
}
