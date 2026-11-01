"""T-0396: a client-view adopt that re-delivers a grid the source was still streaming to the client.

``P3-HND-04`` - two clients in one partition: A next to (then in) the ship's cockpit, B a short way off, watching.
The source node holds the ship's stream to B (the test plugin's ``/stream-test-hold``), so the ship is pending and
streaming in B's view on BOTH sides - the live state of 2026-10-03 16:17:53, when node-335 exported client-52's
view with 'Visitor Ion' at ``streaming=True`` and node-334's adopt re-delivered it (``client_view_redeliver``:
RemoveForClient + AddForClient = a destroy and a new create on the client).

Why a seated character is the subject: vanilla sends a streamed grid's children only after the grid's stream
(MyReplicationServer.SendStreamingEntry), and never destroys a grid the client has not acknowledged
(ScheduleStateGroupSync: 'not yet replicated on client'). So on a vanilla server a client never sees a destroy
of a grid it holds pending together with a READY child. The cluster's redeliver sends exactly that. The vanilla
client then takes the pending branch (MyReplicationClient.ProcessReplicationDestroy -> PendingReplicableDestroy),
which clears pending entries only: a ready character replicable under that grid stays registered under its
network ids. The server believes it removed the character with the grid, sends it again with the grid, and the
client logs 'Replicated object already exists' for the character and its sub-objects - every time the grid
re-enters its view - and 'Raising event on object which is not recognized by replication' for the new copy.

How B comes to hold A's character READY while the grid is pending (``T0396_ARM``):

* ``board`` (default, expected RED on the T-0396 hypothesis): A stands at the cockpit on foot; B comes back into
  view under the hold, so A's character reaches B as a root replicable and is acknowledged while the grid's
  stream is held; then A boards, which makes the character a child of the grid on the server. Export, adopt,
  redeliver.
* ``seated`` (the falsifier, expected GREEN): A boards first. B comes back under the hold and never receives the
  seated character (a streamed grid's children follow its stream), so the redeliver destroys a pending grid with
  no ready child. Same handover, same redeliver.

Then B leaves A's view and re-enters it ``T0396_CYCLES`` times on the target. Checks: B's client log, this launch,
after the handover: no 'Replicated object already exists' (and none for A's character subtree in particular), no
'not recognized by replication'; and the target has A's character delivered to B with B's replication holding
its network id. A control cycle (B away and back, no hold, no handover) runs first: it must log none of these
lines. A 25 m teleport probe of A seen from B is recorded only (it never earned a positive control).

PRECONDITION checks (named so) end the run when the window was not reproduced: then the run says nothing about
the defect, either way.

Knobs: T0396_ARM=board|seated, T0396_CYCLES (3), T0396_AWAY (7000 m: B's distance from the ship when out of view),
T0396_BACK (60 m).
"""

from __future__ import annotations

import json
import os
import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, _character, _enter_nearest_cockpit, _handover_url, _lines, _partitions

CLIENT2_ID = 1234567891
CLIENT2_PORT = 24160
NODES = ("node-1", "node-2")
ARM = os.environ.get("T0396_ARM", "board").lower()
CYCLES = int(os.environ.get("T0396_CYCLES", "3"))
AWAY = float(os.environ.get("T0396_AWAY", "7000"))
BACK = float(os.environ.get("T0396_BACK", "60"))
PROBE_JUMP = 25.0
DUPLICATE = r"Replicated object already exists"
UNRECOGNIZED = r"not recognized by replication"


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(f"PRECONDITION: {label}", bool(passed), detail[:500]):
        raise ClusterError(f"precondition failed, the window was not reproduced: {label}: {detail[:500]}")


def _poll(predicate, timeout: float, interval: float = 1.0):
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def _try(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def _client_lines(bench, client_id: int, pattern: str) -> list[str]:
    """Every line matching ``pattern`` in the game logs THIS launch of the client wrote (see
    scenarios_wa_ops._client_game_lines for why the launch bounds it), unlimited: phases are counted by offset."""
    client = bench.clients.get(client_id)
    if client is None:
        return []
    expression = re.compile(pattern)
    return [line for path in client.game_logs() if path.stat().st_mtime >= client.launched_at - 5
            for line in path.read_text(errors="replace").splitlines() if expression.search(line)]


def _counts(bench) -> dict:
    return {"duplicate": len(_client_lines(bench, CLIENT2_ID, DUPLICATE)),
            "unrecognized": len(_client_lines(bench, CLIENT2_ID, UNRECOGNIZED))}


def _view(bench, node: str, grid: int, character: int) -> dict:
    return _try(lambda: bench.cluster.node(node, f"/stream-test-hold?client={CLIENT2_ID}&entity={grid}&character={character}"), {}) or {}


def _shared_partition(bench) -> tuple[str, int] | None:
    """(node, partition) when both clients ride the same partition."""
    for node in NODES:
        for partition in _try(lambda: _partitions(bench, node), []) or []:
            clients = partition.get("clients") or []
            if CLIENT_ID in clients and CLIENT2_ID in clients:
                return node, partition["id"]
    return None


def _teleport_b(api_b, ship: list[float], offset: float) -> None:
    api_b.character_teleport(ship[0] + offset, ship[1], ship[2])


def _away(bench, api_b, ship, node: str, grid: int, character: int, label: str) -> None:
    _teleport_b(api_b, ship, AWAY)
    gone = _poll(lambda: (lambda view: view.get("found") and not (view.get("grid") or {}).get("inView"))(
        _view(bench, node, grid, character)), 90, interval=2)
    _require(bench, f"{label}: the ship left B's view on {node} ({AWAY:.0f} m)", gone, json.dumps(_view(bench, node, grid, character)))


def _back(bench, api_b, ship, node: str, grid: int, character: int, label: str, delivered: bool = True) -> dict:
    _teleport_b(api_b, ship, BACK)

    def arrived():
        view = _view(bench, node, grid, character)
        entry = view.get("grid") or {}
        return view if entry.get("inView") and (not delivered or not entry.get("pending")) else None

    view = _poll(arrived, 120, interval=1)
    _require(bench, f"{label}: the ship is back in B's view on {node}" + (" and delivered" if delivered else ""), view,
             json.dumps(_view(bench, node, grid, character)))
    return view


def _xyz(value) -> tuple[float, float, float] | None:
    """A Remote API position as floats: a list, an {x,y,z} object, or a 'x, y, z' string (T-0383 met all three)."""
    try:
        if isinstance(value, dict):
            value = [value[key] for key in ("x", "y", "z")]
        elif isinstance(value, str):
            value = re.findall(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", value)
        x, y, z = (float(part) for part in value)
        return x, y, z
    except (TypeError, ValueError, KeyError):
        return None


def _probe(bench, api_a, api_b, character: int, label: str) -> dict:
    """Does B's replica of A's character follow A? B samples it every frame while A jumps 25 m (vanilla admin
    teleport); a replica whose replicable is not the one the server talks to stays put."""
    result: dict = {"label": label}
    try:
        api_b.start_handover_observation(character)
    except Exception as error:  # noqa: BLE001 - "B has no such entity" is an answer
        result["error"] = f"B cannot observe A's character {character}: {error}"
        return result
    position = _xyz((_character(api_a) or {}).get("position"))
    if position is None:
        result["error"] = f"A reports no usable character position: {(_character(api_a) or {}).get('position')!r}"
        return result
    x, y, z = position
    api_a.character_teleport(x + PROBE_JUMP, y, z)
    time.sleep(4)
    observation = _try(api_b.get_handover_observation, {}) or {}
    result.update(observation=observation, jumped=float(observation.get("maxPositionError") or 0) >= PROBE_JUMP * 0.4
                  and int(observation.get("samples") or 0) > 0)
    return result


def p3_hnd_04(bench) -> None:
    """T-0396: redeliver of a grid with a seated character while the grid is still streaming to a viewer."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition move is the forced handover
    api_a = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    api_b = bench.start_client(CLIENT2_ID, remote_port=CLIENT2_PORT, render=False)
    bench.wait_spawned(CLIENT2_ID)
    bench.ensure_gameplay_focus(CLIENT2_ID)

    cockpit = next(iter(api_a.find_nearby_blocks(block_type="Cockpit", max_distance=30, limit=1)), None)
    _require(bench, "a cockpit within 30 m of A", cockpit, "none")
    grid, ship = int(cockpit["gridId"]), list(_xyz(cockpit["worldPosition"]))
    character = bench._server_character(CLIENT_ID)
    _require(bench, "the server reports A's character", character)
    shared = _poll(lambda: _shared_partition(bench), 120, interval=3)
    _require(bench, "A and B ride one partition", shared, json.dumps({n: _try(lambda: _partitions(bench, n)) for n in NODES})[:500])
    source = shared[0]
    target = next(node for node in NODES if node != source)
    bench.record("00-setup", {"arm": ARM, "grid": grid, "ship": ship, "character": character, "source": source,
                              "target": target, "partition": shared[1], "cockpit": cockpit})
    print(f"  [INFO] arm={ARM} ship={grid} A's character={character} source={source} target={target}")

    # ---- CONTROL: B away and back, no hold, no handover; and the probe's positive control ----
    counts = {"start": _counts(bench)}
    _away(bench, api_b, ship, source, grid, character, "control")
    time.sleep(5)
    _back(bench, api_b, ship, source, grid, character, "control")
    time.sleep(10)
    control_probe = _probe(bench, api_a, api_b, character, "control")
    bench.record("10-control-probe", control_probe)
    counts["control"] = _counts(bench)
    control = {key: counts["control"][key] - counts["start"][key] for key in counts["start"]}
    bench.check("CONTROL: a plain leave/re-enter of B's view logs no duplicate and no unrecognized line on B",
                control["duplicate"] == 0 and control["unrecognized"] == 0, json.dumps(control))
    # Recorded, not asserted: the observation reports the largest per-frame step, and B's client smooths a remote
    # character's 25 m jump into steps of a few metres (r91/r94 benches: 6.8 m and 2.7 m), so the probe has never
    # earned its positive control. bench.py's rule: an unproven probe is not evidence either way.
    print(f"  [INFO] control jump probe (not asserted): {json.dumps(control_probe)[:300]}")

    # A back to the cockpit (the probe moved it 25 m away), on foot.
    x, y, z = ship
    api_a.character_teleport(x, y + 3, z)
    time.sleep(5)
    if ARM == "seated":
        _enter_nearest_cockpit(bench, api_a, speed="fly")

    # ---- THE WINDOW: hold the ship's stream to B on the source, bring B back ----
    _away(bench, api_b, ship, source, grid, character, "hold")
    bench.record("20-arm", bench.cluster.node(source, f"/stream-test-hold?client={CLIENT2_ID}&entity={grid}", method="POST"))
    _teleport_b(api_b, ship, BACK)

    def held():
        view = _view(bench, source, grid, character)
        entry, body = view.get("grid") or {}, view.get("character") or {}
        ready = entry.get("inView") and entry.get("pending") and entry.get("streaming") and view.get("heldSends", 0) > 0
        if ARM == "board":
            ready = ready and body.get("inView") and not body.get("pending")
        return view if ready else None

    view = _poll(held, 120, interval=1)
    bench.record("21-held", view or _view(bench, source, grid, character))
    _require(bench, f"the ship is pending+streaming (held) in B's view on {source}"
             + (" and A's character is delivered to B" if ARM == "board" else ""), view,
             json.dumps(_view(bench, source, grid, character)))
    if ARM == "board":
        _enter_nearest_cockpit(bench, api_a, speed="fly")
        view = _poll(lambda: (lambda v: v if v.get("characterParentIsGrid") and (v.get("character") or {}).get("inView")
                              and not (v.get("character") or {}).get("pending") and (v.get("grid") or {}).get("pending") else None)(
            _view(bench, source, grid, character)), 30)
        _require(bench, "A sits in the ship: its character is a child of the held grid and still delivered to B", view,
                 json.dumps(_view(bench, source, grid, character)))
    else:
        view = _view(bench, source, grid, character)
        bench.check("seated arm: B does not hold A's seated character while the ship is held",
                    not (view.get("character") or {}).get("inView"), json.dumps(view)[:300])
    bench.record("22-before-handover", view)
    subtree = [int(value) for value in view.get("characterSubtree") or []]
    shared = _shared_partition(bench)
    _require(bench, f"A and B still ride one partition on {source}", shared and shared[0] == source, str(shared))
    partition = shared[1]

    # ---- the handover: the Gateway moves the whole crew (A and B) source -> target ----
    counts["handover"] = _counts(bench)
    export_pattern = (rf"client-view pending client={CLIENT2_ID} networkId=\d+ streaming=True "
                      rf"entity=MyCubeGridReplicable entity={grid}\b")
    redeliver_pattern = rf"client_view_redeliver client={CLIENT2_ID} .*MyCubeGridReplicable/{grid} root"
    exports_before = len(_lines(bench, source, export_pattern))
    redelivers_before = len(_lines(bench, target, redeliver_pattern))
    try:
        answer = bench.cluster.post(_handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300)
    finally:
        bench.record("31-release", _try(lambda: bench.cluster.node(source, "/stream-test-release", method="POST"), "release failed"))
    bench.record("30-handover", answer)
    exported = _poll(lambda: _lines(bench, source, export_pattern)[exports_before:], 30)
    _require(bench, f"{source} exported B's view with the ship pending and streaming", exported,
             "; ".join(exported or [])[-300:] or "no export line")
    redelivered = _poll(lambda: _lines(bench, target, redeliver_pattern)[redelivers_before:], 60)
    _require(bench, f"{target}'s adopt re-delivered the ship to B (client_view_redeliver)", redelivered,
             "; ".join(redelivered or [])[-300:] or "no redeliver line")
    subtree_lines = _lines(bench, target, rf"client_view_redeliver_subtree client={CLIENT2_ID} ")
    print(f"  [INFO] {target} subtree destroys before the redeliver (T-0396 fix): {subtree_lines[-1:] or 'none'}")
    bench.record("32-lines.txt", "\n".join(exported + redelivered + subtree_lines))
    _require(bench, f"B is on {target} with the ship in view",
             _poll(lambda: ((_view(bench, target, grid, character).get("grid") or {}).get("inView")), 60),
             json.dumps(_view(bench, target, grid, character)))
    time.sleep(20)  # the re-delivered ship streams from the target
    counts["redeliver"] = _counts(bench)

    # ---- the ship leaves and re-enters B's view on the target ----
    for cycle in range(1, CYCLES + 1):
        _away(bench, api_b, ship, target, grid, character, f"cycle {cycle}")
        time.sleep(5)
        _back(bench, api_b, ship, target, grid, character, f"cycle {cycle}")
        time.sleep(10)
        counts[f"cycle-{cycle}"] = _counts(bench)

    # ---- B holds A's character as the target sends it: delivered on the node, its network id on the client ----
    final = _view(bench, target, grid, character)
    held_id = int((final.get("character") or {}).get("networkId") or 0)
    replication = _try(api_b.get_replication, {}) or {}
    bench.record("39-final-view", {"node": final, "clientHasCharacterId": held_id in (replication.get("networkIds") or [])})
    bench.check(f"{target} has A's character delivered to B and B's replication holds its network id {held_id}",
                held_id != 0 and (final.get("character") or {}).get("inView") and not (final.get("character") or {}).get("pending")
                and held_id in (replication.get("networkIds") or []), json.dumps(final.get("character")))

    # ---- does B's replica of A's character follow A? (A leaves the seat first; recorded only, see the control) ----
    api_a.character_use()
    _poll(lambda: (_character(api_a) or {}).get("state") not in (None, "sitting"), 15)
    time.sleep(3)
    after_probe = _probe(bench, api_a, api_b, character, "after")
    bench.record("40-after-probe", after_probe)
    counts["end"] = _counts(bench)
    bench.record("41-counts", counts)

    duplicates = _client_lines(bench, CLIENT2_ID, DUPLICATE)[counts["handover"]["duplicate"]:]
    unrecognized = _client_lines(bench, CLIENT2_ID, UNRECOGNIZED)[counts["handover"]["unrecognized"]:]
    ids = {int(match.group(1)) for line in duplicates for match in [re.search(r"id : (\d+)\s*$", line)] if match}
    mine = sorted(ids & set(subtree))
    bench.record("42-b-lines.txt", "\n".join(duplicates + ["", "-- not recognized --"] + unrecognized))
    per_phase = {phase: counts[phase] for phase in counts}
    bench.check("B logged no 'Replicated object already exists' after the handover", not duplicates,
                f"{len(duplicates)} lines, per phase {json.dumps(per_phase)}; first: {duplicates[:1]}")
    bench.check("... none of them for A's character subtree", not mine,
                f"{len(mine)} of A's {len(subtree)} subtree ids: {mine[:10]}")
    bench.check("B logged no 'not recognized by replication' after the handover", not unrecognized,
                f"{len(unrecognized)} lines; first: {unrecognized[:2]}")
    print(f"  [INFO] after jump probe (not asserted): {json.dumps(after_probe)[:300]}")
    bench.exercised(arm=ARM, source=source, target=target, ship=grid, character=character, partition=partition,
                    heldSends=(view or {}).get("heldSends"), cycles=CYCLES, duplicates=len(duplicates),
                    subtreeDuplicates=len(mine), unrecognized=len(unrecognized))


p3_hnd_04.split_step = "to-handover"
p3_hnd_04.administrators = [CLIENT_ID, CLIENT2_ID]  # the vanilla admin teleport moves both characters

SCENARIOS_REDELIVER = {
    "P3-HND-04": (p3_hnd_04, "redeliver of a grid with a seated character while it is still streaming (T-0396)", "3"),
}
