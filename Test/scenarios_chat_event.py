"""SE-1261: a player's chat line reaches the server plugins and mods of the node the player is on, once.

* ``P2-CHAT-EVENT`` - client 1 joins, the test plugin's ``/chat-event-probe`` records ``ChatMessageReceived``
  (server plugins) and ``MessageRecieved`` (mods) on node-1, node-2 and the World Authority, and the client sends a
  global line, a Magnetar ``!`` command and a line the probe's chat prefix refuses at the WA.

A regular node forwards a player's line to the World Authority instead of running vanilla's accept path, and the WA
relays it to every node's clients. Before SE-1261 only the WA raised the chat events, where the player has no
character, so a server plugin reading chat (Image Generation's ``/ig``) never saw the line on the player's node.
Now the WA accepts and relays the line once without raising them, and the player's node raises them when the accepted
line comes back. A line the WA refuses (a prefix like Hangar's command blocker, anti-spam) raises nothing, and a ``!``
command, answered by Magnetar on the player's node before any of this, stays out of both.
"""

from __future__ import annotations

import json
import random

from scenarios_core import CLIENT_ID, _join, _nodes
from scenarios_presence import _chat
from scenarios_wa_ops import _poll, _require, _session

ROLES = ("node-1", "node-2", "wa-1")
#: A relay from the WA takes well under a second on the bench.
RELAY_TIMEOUT = 15


def _events(bench, role: str, text: str) -> dict[str, list[dict]]:
    rows = [e for e in bench.cluster.node(role, "/chat-event-probe").get("events") or [] if e.get("text") == text]
    return {kind: [e for e in rows if e["kind"] == kind] for kind in ("plugin", "mod")}


def _accepted(bench, text: str) -> list[dict]:
    return [m for m in _chat(bench, "wa-1")[0] if m.get("text") == text]


def p2_chat_event(bench) -> None:
    """Server plugins and mods on the player's node see the player's chat line once; the WA accepts it once."""
    api = _join(bench)
    backend = (_session(bench, CLIENT_ID) or {}).get("backend")
    home = next((node_id for node_id, node in _nodes(bench).items() if node.backend == backend), None)
    _require(bench, "SETUP: the Gateway names the client's node", home in ROLES[:2], str(backend))
    for role in ROLES:
        bench.cluster.node(role, "/chat-event-probe", method="POST")

    text = f"SE-1261 line {random.randrange(10**6)}"
    api.send_chat(text)
    accepted = _poll(lambda: _accepted(bench, text), 60)
    _require(bench, "SETUP: the World Authority accepted the line", bool(accepted), text)
    # The player's node raises the events when the WA's relay comes back; without the fix they never come.
    _poll(lambda: _events(bench, home, text)["plugin"], RELAY_TIMEOUT)
    seen = {role: _events(bench, role, text) for role in ROLES}
    bench.record("line-events", {"home": home, "text": text, "events": seen, "accepted": accepted})
    for kind, label in (("plugin", "ChatMessageReceived"), ("mod", "the mods' MessageRecieved")):
        rows = seen[home][kind]
        bench.check(
            f"{label} raised once on the player's node {home}",
            len(rows) == 1 and rows[0]["sender"] == str(CLIENT_ID),
            json.dumps(rows),
        )
        others = {role: seen[role][kind] for role in ROLES if role != home}
        bench.check(f"{label} raised on no other node or the WA", not any(others.values()), json.dumps(others))
    bench.check("the World Authority accepted the line once", len(accepted) == 1, json.dumps(accepted))

    # Lines the WA never accepts raise nothing anywhere: Magnetar answers a ! command on the player's node, and the
    # probe's last-priority prefix refuses a veto line at the WA the way Hangar refuses vanilla hangar commands.
    suffix = random.randrange(10**6)
    refused = {"a Magnetar ! command": f"!help se1261-{suffix}", "a line a WA prefix refuses": f"SE-1261 veto {suffix}"}
    for line in refused.values():
        api.send_chat(line)
    marker = f"SE-1261 after the refused lines {suffix}"
    api.send_chat(marker)
    _require(
        bench, "SETUP: the line after the refused ones was accepted", bool(_poll(lambda: _accepted(bench, marker), 60))
    )
    _poll(lambda: _events(bench, home, marker)["plugin"], RELAY_TIMEOUT)
    for label, line in refused.items():
        leaked = {role: _events(bench, role, line) for role in ROLES}
        bench.record(f"refused-{line.split()[0]}", leaked)
        bench.check(
            f"{label} raised no chat event anywhere",
            not any(rows for kinds in leaked.values() for rows in kinds.values()),
            json.dumps(leaked),
        )
        bench.check(f"the World Authority did not accept {label}", not _accepted(bench, line), line)
    bench.exercised(
        route=f"client 1 on {home}: a global line, a ! command and a refused line; chat events on node-1, node-2, wa-1"
    )


p2_chat_event.regular_nodes = 2

SCENARIOS_CHAT_EVENT = {
    "P2-CHAT-EVENT": (
        p2_chat_event,
        "SE-1261: a player's chat reaches server plugins and mods on the player's node once, never on the WA",
        "3",
    ),
}
