"""SE-1372: a player who rejoins under a new name shows that name on the other players' clients.

* ``P2-REJOIN-RENAME`` - client 1 spawns on node-1 and watches. Client 2 logs in on node-2 as ``[B] SE1372Old``, logs
  out and comes back on node-2 as ``SE1372New``, then logs out again and comes back on node-1, client 1's node, as
  ``SE1372Third``. After each rejoin client 1 must show the new name in its F3 player list, as the sender of client
  2's chat line, and find client 2 with ``/w <name>``; every role must hold the new identity name.

Live (octahedron-test, 2026-11-12) the taxi bots were renamed from ``[B] Taxi <station>`` to ``Taxi1..7``. The nodes
knew the new names (SE-1100 renames the identity at the World Authority), but the other players kept seeing the old
ones: ``/w Taxi3`` found no player and chat showed the old name.

``/w`` runs on the client (MyChatCommands.CommandChannelWhisper): it looks the name up in the client's own MyPlayer
list and, when nothing matches, prints "not found" on the HUD chat. The Remote plugin runs chat commands the way the
chat box does, so a HUD line other than the echo of the command is the miss.
"""

from __future__ import annotations

import json
import random

from cluster import ClusterError, wait_until
from scenarios_core import HANDOVER_TIMEOUT, _join
from scenarios_presence import _cluster_players, _f3
from scenarios_wa import CLIENT2_ID, _try, start_second_client
from scenarios_wa_ops import _poll, _require, _session

ROLES = ("node-1", "node-2", "wa-1")


def _row(bench, client_id: int, name: str) -> dict | None:
    return next((r for r in _cluster_players(bench, "wa-1") if r["steamId"] == client_id and r.get("name") == name), None)


def _identity_names(bench, identity: int) -> dict:
    rows = {node: _try(bench, node, f"/identity-test-status?identityId={identity}") for node in ROLES}
    return {node: row.get("name") if isinstance(row, dict) else None for node, row in rows.items()}


def _named_everywhere(bench, identity: int, name: str) -> dict | None:
    return _poll(lambda: (lambda n: n if all(v == name for v in n.values()) else None)(_identity_names(bench, identity)),
                 HANDOVER_TIMEOUT)


def _sender(bench, sender_api, receiver_api, label: str) -> str:
    text = f"SE-1372 {label} {random.randrange(10**6)}"
    receiver_api.get_chat_messages(0)  # Remote logs received chat from its first chat call on
    sender_api.send_chat(text)
    receipt = _poll(
        lambda: next((m for m in receiver_api.get_chat_messages(0).get("messages") or [] if m.get("Text") == text), None),
        60,
    )
    _require(bench, f"{label}: client 2's line reached client 1", receipt is not None, text)
    bench.record(f"receipt-{label}", receipt)
    return receipt.get("Sender")


def _whisper_finds(api, name: str) -> tuple[bool, list]:
    """Run ``/w name`` on the client: True when it found the player (no HUD line but the command's echo)."""
    command = f"/w {name}"
    before = {(x["sender"], x["text"], x["timestamp"]) for x in api.get_chat_messages(0).get("hud") or []}
    api.send_chat(command)
    after = api.get_chat_messages(0).get("hud") or []
    added = [x for x in after if (x["sender"], x["text"], x["timestamp"]) not in before and x["text"] != command]
    return not added, added


def _check_client1_sees(bench, api1, api2, label: str, old: str, new: str) -> None:
    def f3_has_new():
        rows, _ = _f3(api1)
        return rows if new in rows else None

    try:
        rows = wait_until(f3_has_new, 60, f"client 1's F3 to list {new}")
    except ClusterError:
        rows = _f3(api1)[0]
    bench.check(f"{label}: client 1's F3 lists {new!r}, not {old!r}", new in rows and old not in rows, json.dumps(rows))
    sender = _sender(bench, api2, api1, label)
    # An older Remote reports no sender; the HUD names the member then, which the F3 row above already shows.
    if sender is not None:
        bench.check(f"{label}: client 1 shows client 2's line as from {new!r}", sender.endswith(new),
                    json.dumps({"sender": sender, "expected": new}))
    found, lines = _whisper_finds(api1, new)
    bench.check(f"{label}: client 1's /w {new} finds the player", found, json.dumps(lines))
    found, lines = _whisper_finds(api1, "SE1372Nobody")
    bench.check(f"{label}: control: client 1's /w of a name nobody has reports a miss", not found, json.dumps(lines))
    bench.record(f"mirror-{label}", {node: _try(bench, node, "/player-mirror") for node in ("node-1", "node-2")})


def _login(bench, node: str, name: str):
    api = start_second_client(bench, route_node=node, name=name)
    row = wait_until(lambda: _row(bench, CLIENT2_ID, name), 120, f"client 2 listed as {name!r}")
    # A respawn preview switch moves a client within about 5 s of its login.
    moved = _poll(lambda: (lambda r: r if r is None or r.get("node") != node else None)(_row(bench, CLIENT2_ID, name)), 15)
    _require(bench, f"SETUP: client 2 stayed on {node} as {name!r}", moved is None, json.dumps(moved))
    bench.record(f"client2-{name}", row)
    return api, row


def _logout(bench) -> None:
    bench.kill_client(CLIENT2_ID)
    _require(bench, "SETUP: client 2's Gateway session cleared",
             _poll(lambda: True if _session(bench, CLIENT2_ID) is None else None, 360, 5))


def p2_rejoin_rename(bench) -> None:
    """A rejoin under a new name reaches the other players' clients: F3, chat sender and /w."""
    # Client 2's restart takes a minute, far past the mirror's 5 s grace. Live, a bot restarted by a signal is back
    # within seconds of its Gateway session timing out, and a handover carries what a client holds without any
    # grace at all: either way node-1 never withdraws the old row. A long grace gives the bench the same shape.
    bench.cluster.config.node_env["CLUSTER_MIRROR_GRACE"] = "3600"
    api1 = _join(bench)
    bench.cluster.post("/automatic/pause")
    # Client 2 never spawns: its respawn screen previews the medbay on node-1 and switches it there within seconds,
    # which hands client 1 vanilla's own join and leave messages. Node-1 refuses those switches (SE-1124's knob, as
    # in P2-PRS-04), so client 2 stays on node-2 and client 1 sees it only through node-1's remote player mirror.
    bench.record("preview-refusal", bench.cluster.node("node-1", "/respawn-execute-fault?mode=refuse&count=1000", "POST"))
    old, new, third = "[B] SE1372Old", "SE1372New", "SE1372Third"

    _, row = _login(bench, "node-2", old)
    identity = row["identityId"]
    rows = wait_until(lambda: (lambda r: r if old in r else None)(_f3(api1)[0]), 60, f"client 1's F3 to list {old!r}")
    bench.record("f3-before", rows)

    _logout(bench)
    api2, row = _login(bench, "node-2", new)
    _require(bench, "SETUP: client 2 came back to the same identity", row["identityId"] == identity, json.dumps(row))
    bench.check(f"every role names the identity {new!r}", _named_everywhere(bench, identity, new) is not None,
                json.dumps(_identity_names(bench, identity)))
    _check_client1_sees(bench, api1, api2, "remote", old, new)
    mirror = _try(bench, "node-1", "/player-mirror") or {}
    bench.check("node-1 replaced the renamed phantom (renamedTotal)", (mirror.get("renamedTotal") or 0) >= 1,
                json.dumps({k: mirror.get(k) for k in ("renamedTotal", "replacedByLoginTotal", "mirrored")}))

    _logout(bench)
    api2, row = _login(bench, "node-1", third)
    bench.check(f"every role names the identity {third!r}", _named_everywhere(bench, identity, third) is not None,
                json.dumps(_identity_names(bench, identity)))
    _check_client1_sees(bench, api1, api2, "local", new, third)
    mirror = _try(bench, "node-1", "/player-mirror") or {}
    bench.check("node-1 replaced the phantom at the player's login there (replacedByLoginTotal)",
                (mirror.get("replacedByLoginTotal") or 0) >= 1,
                json.dumps({k: mirror.get(k) for k in ("renamedTotal", "replacedByLoginTotal", "mirrored")}))
    bench.exercised(route="client 2 node-2 old -> node-2 new -> node-1 third, client 1 watching on node-1")


p2_rejoin_rename.regular_nodes = 2

SCENARIOS_RENAME = {
    "P2-REJOIN-RENAME": (
        p2_rejoin_rename,
        "SE-1372: a player rejoining under a new name shows it on the other clients (F3, chat sender, /w)",
        "3",
    ),
}
