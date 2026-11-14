"""SE-1193: cluster chat always names its sender on the receiving client.

* ``P2-CHAT-NAMES`` - client 1 spawns on node-1 and the medbay moves to node-2. Client 2 joins later on node-2, and
  each of the two sends a global line the other must show with the sender's name. Then client 3 joins on node-1
  and its respawn screen's preview moves it to the medbay on node-2: client 1 must still name client 3's lines.
  SE-1476 adds three handovers of client 1 between node-1 and node-3 while another player's member is taken off
  it between its export and its release (see ``_transit_leg``).

The client shows ``MyMultiplayer.GetMemberPlatformName(author)`` as the sender of a player's line, so a player the
client has no member entry for is shown as ": text". Live (octahedron-test, 2026-11-06 19:25Z) a player died on
the node Viktor was on and picked a medbay on another node. That move is a Gateway respawn switch (SE1-0122), not a
handover, so the source node broadcast vanilla's disconnect and its clients dropped the member. The remote player
mirror still counted the member as held from when the player was attached there and never sent it again. A fresh
client that joins on a node without a medbay takes the same switch at world-ready, which is client 3's leg.

The bench's headless clients show no multiplayer chat on their HUD (MyHudChat needs an active game service), so the
name is read from the Remote plugin's chat receipts: ``Sender`` is what the HUD would show for the line. Remote has
it since CometWorks/remote 23fefc1 (PR #54, after the 1.3.0 release). An older Remote fails setup with that version
named instead of passing the scenario.
"""

from __future__ import annotations

import json
import random
import re

from cluster import wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _handover_url, _join, _lines, _nodes
from scenarios_core import _partitions, _wait_line
from scenarios_presence import _cluster_players, _mirror_passes, _mirrored_keys
from scenarios_wa import CLIENT2_ID, CLIENT3_ID, CLIENT3_PORT, start_second_client
from scenarios_wa_ops import _point, _points, _poll, _require, _session


def _name(bench, client_id: int) -> str:
    row = wait_until(
        lambda: next((r for r in _cluster_players(bench, "wa-1") if r["steamId"] == client_id), None),
        60,
        f"client {client_id} in the cluster player list",
    )
    _require(
        bench,
        f"SETUP: client {client_id} has a name in the cluster player list",
        bool(row.get("name")),
        json.dumps(row),
    )
    return row["name"]


def _mirrored(bench, node_id: str, client_id: int) -> None:
    """Wait until the node's mirror installed the client's player, then two more passes (the one in flight may
    predate the member message)."""
    wait_until(
        lambda: client_id in {steam for steam, _ in _mirrored_keys(bench, node_id)} or None,
        60,
        f"{node_id} to mirror client {client_id} onto its clients",
    )
    first = _mirror_passes(bench, node_id)
    wait_until(lambda: _mirror_passes(bench, node_id) >= first + 2 or None, 60, f"two mirror passes on {node_id}")


def _say(bench, sender_api, receiver_api, label: str) -> dict:
    """Send one global line and return the receiver's receipt of it."""
    text = f"SE-1193 {label} {random.randrange(10**6)}"
    receiver_api.get_chat_messages(0)  # Remote logs received chat from its first chat call on
    sender_api.send_chat(text)
    receipt = _poll(
        lambda: next(
            (m for m in receiver_api.get_chat_messages(0).get("messages") or [] if m.get("Text") == text), None
        ),
        60,
    )
    _require(bench, f"{label}: the line reached the other client", receipt is not None, text)
    _require(
        bench,
        f"{label}: the client's Remote reports the shown sender (needs Remote 23fefc1 or later, CometWorks/remote PR #54)",
        "Sender" in receipt,
        json.dumps(receipt),
    )
    bench.record(f"receipt-{label}", receipt)
    return receipt


def _check_named(bench, label: str, receipt: dict, name: str) -> None:
    bench.check(label, receipt["Sender"].endswith(name), json.dumps({"receipt": receipt, "expected": name}))


def _medbay_to_node2(bench) -> None:
    """P2-WA-06c's setup: move client 1 away from the node-1 medbay and hand the medbay's partition to node-2."""
    point = _poll(
        lambda: next(
            (p for p in _points(bench, "wa-1").get("points") or [] if p.get("Origin") == "node-1" and p.get("Working")),
            None,
        ),
        HANDOVER_TIMEOUT,
    )
    _require(bench, "SETUP: a node-1 respawn point converged at the World Authority", point is not None)
    entity, grid = point["RespawnEntityId"], point["GridEntityId"]
    character = bench.cluster.node("node-1", f"/respawn-test-player?userId={CLIENT_ID}").get("character") or 0
    _require(bench, "SETUP: client 1 has a character", character > 0)
    bench.cluster.post("/automatic/pause")
    bench.cluster.node("node-1", f"/test-position?entity={character}&relativeTo={grid}&x=15000", "POST")

    def separated():
        rows = _partitions(bench, "node-1")
        medbay = (_point(_points(bench, "wa-1"), entity, "node-1") or {}).get("PartitionId")
        client = _client_partition(rows)
        clientless = any(p["id"] == medbay and not p.get("clients") for p in rows)
        return medbay if medbay and client and medbay != client and clientless else None

    medbay = _poll(separated, HANDOVER_TIMEOUT)
    _require(bench, "SETUP: the medbay partition became clientless", medbay is not None)
    bench.record("medbay-handover", bench.cluster.post(_handover_url(bench, medbay, clientless=True), timeout=300))
    _require(
        bench,
        "SETUP: the respawn registry followed the medbay to node-2",
        bool(_poll(lambda: _point(_points(bench, "wa-1"), entity, "node-2"), HANDOVER_TIMEOUT)),
    )


def p2_chat_names(bench) -> None:
    """A relayed chat line shows its sender's name, also after a respawn switch took the sender off a node."""
    api1 = _join(bench)
    _medbay_to_node2(bench)
    nodes = _nodes(bench)

    api2 = start_second_client(bench, route_node="node-2")
    name1, name2 = _name(bench, CLIENT_ID), _name(bench, CLIENT2_ID)
    _mirrored(bench, "node-1", CLIENT2_ID)
    _mirrored(bench, "node-2", CLIENT_ID)
    _check_named(
        bench, "client 1 names client 2, who joined later on the other node", _say(bench, api2, api1, "2to1"), name2
    )
    _check_named(bench, "client 2 names client 1 on the other node", _say(bench, api1, api2, "1to2"), name1)

    # Client 3 attaches to node-1 first, so client 1 gets its member from vanilla. With no medbay on node-1 the
    # respawn screen's preview then switches it to node-2, and node-1 broadcasts the disconnect.
    partition = _client_partition(_partitions(bench, "node-1"))
    _require(bench, "SETUP: node-1 reports client 1's partition", partition is not None)
    bench.record(
        "client3-route", bench.cluster.post(f"/registry/client-routes?client={CLIENT3_ID}&partition={partition}")
    )
    api3 = bench.start_client(CLIENT3_ID, CLIENT3_PORT, render=False)
    _wait_line(bench, "node-1", rf"Accepted client {CLIENT3_ID} through cluster node link", 120)
    on_node2 = _poll(
        lambda: (
            lambda s: s if s.get("backend") == nodes["node-2"].backend and s.get("admissionState") == "Ready" else None
        )(_session(bench, CLIENT3_ID) or {}),
        120,
    )
    _require(bench, "SETUP: client 3's respawn screen moved it from node-1 to node-2", on_node2 is not None)
    name3 = _name(bench, CLIENT3_ID)
    left = _lines(bench, "node-1", rf"User left {re.escape(name3)}\b")
    suppressed = _lines(bench, "node-1", rf"traversal leave toast suppressed client={CLIENT3_ID}\b")
    bench.record("client3-left-node-1", {"left": left, "suppressed": suppressed})
    _require(bench, "SETUP: node-1 broadcast client 3's disconnect (no handover)", bool(left) and not suppressed)

    wait_until(
        lambda: any(r["steamId"] == CLIENT3_ID and r.get("node") == "node-2" for r in _cluster_players(bench, "node-1"))
        or None,
        60,
        "node-1's cluster list to carry client 3 on node-2",
    )
    _mirrored(bench, "node-1", CLIENT3_ID)
    receipt = _say(bench, api3, api1, "3to1")
    bench.record("node-1-mirror", bench.cluster.node("node-1", "/player-mirror"))
    _check_named(bench, "client 1 names client 3 after a respawn switch took it from node-1 to node-2", receipt, name3)
    _check_named(bench, "client 3 names client 1", _say(bench, api1, api3, "1to3"), name1)

    # SE-1476: client 1 rides handovers while another player's member is taken off it after its export.
    api3 = _transit_leg(bench, api1, "pending", "node-1", "node-3", CLIENT3_ID, name3)
    api3 = _transit_leg(bench, api1, "after", "node-3", "node-1", CLIENT3_ID, name3)
    _transit_leg(bench, api1, "leave", "node-1", "node-3", CLIENT2_ID, name2, api2)
    bench.exercised(
        route="client 1 node-1, client 2 node-2, client 3 node-1 -> respawn preview switch -> node-2; "
        "client 1 handed node-1 -> node-3 -> node-1 -> node-3 with a member lost in transit"
    )


def _transit_leg(bench, api1, mode: str, source: str, target: str, subject: int, name: str, subject_api=None):
    """SE-1476: hand client 1 from ``source`` to ``target`` while ``subject``'s member is taken off client 1 between
    its export and its release, then the subject (back) on node-2 says a line client 1 must name.

    Live (octahedron-test 2026-11-14 00:14Z) Welcemir logged out, LJS's partition was captured 4 s later, and the
    source node's mirror withdrew Welcemir from LJS by broadcast before LJS left it. The export had vouched for the
    member, so the next node never sent it when Welcemir came back, and LJS saw his lines as ": text".
    The test route on the source node (``/test-mirror-transit``) makes the window certain:

    * ``pending`` - the subject logs out and the source's mirror has its withdrawal pending at the export (the live case).
    * ``after`` - the subject logs out but the source's mirror only notices after the export.
    * ``leave`` - vanilla's disconnect for the subject runs on the source after the export (a real leave from that
      node, like SE-1193's respawn switch), while the subject stays online on node-2.
    """
    nodes = _nodes(bench)
    bench.cluster.node(source, f"/test-mirror-transit?client={CLIENT_ID}&mode={mode}&subject={subject}", "POST")
    if mode != "leave":
        bench.kill_client(subject)
        _require(
            bench,
            f"SETUP {mode}: {source} no longer lists the logged out client {subject}",
            bool(_poll(lambda: not any(r["steamId"] == subject for r in _cluster_players(bench, source)), 120)),
        )
        if mode == "pending":
            frozen = _poll(lambda: bench.cluster.node(source, "/test-mirror-transit").get("frozen") or None, 60)
            _require(bench, f"SETUP {mode}: {source}'s mirror has client {subject}'s withdrawal pending", bool(frozen))
    partition = _client_partition(_partitions(bench, source))
    _require(bench, f"SETUP {mode}: {source} reports client 1's partition", partition is not None)
    bench.record(
        f"transit-{mode}-handover",
        bench.cluster.post(
            _handover_url(bench, partition, source=source, target=target, client=CLIENT_ID), timeout=300
        ),
    )
    arrived = _poll(
        lambda: (_session(bench, CLIENT_ID) or {}).get("backend") == nodes[target].backend or None, HANDOVER_TIMEOUT
    )
    _require(bench, f"SETUP {mode}: client 1 handed over to {target}", bool(arrived))
    transit = _poll(
        lambda: (
            lambda s: s if (s.get("events") or [""])[-1].startswith(("mirror released", "vanilla leave")) else None
        )(bench.cluster.node(source, "/test-mirror-transit")),
        60,
    )
    bench.record(f"transit-{mode}-source", transit)
    _require(
        bench, f"SETUP {mode}: {source} took client {subject}'s member off client 1 after its export", bool(transit)
    )
    if subject_api is None:
        subject_api = start_second_client(bench, route_node="node-2", client_id=subject, remote_port=CLIENT3_PORT)
    wait_until(
        lambda: any(r["steamId"] == subject and r.get("node") == "node-2" for r in _cluster_players(bench, target))
        or None,
        120,
        f"{target}'s cluster list to carry client {subject} on node-2",
    )
    first = _mirror_passes(bench, target)
    wait_until(lambda: _mirror_passes(bench, target) >= first + 3 or None, 60, f"three mirror passes on {target}")
    receipt = _say(bench, subject_api, api1, f"transit-{mode}")
    bench.record(f"transit-{mode}-{target}-mirror", bench.cluster.node(target, "/player-mirror"))
    _check_named(
        bench,
        f"client 1 names client {subject} after a handover {source} -> {target} that lost the member in transit ({mode})",
        receipt,
        name,
    )
    return subject_api


p2_chat_names.regular_nodes = 3

SCENARIOS_CHAT_NAMES = {
    "P2-CHAT-NAMES": (
        p2_chat_names,
        "SE-1193: relayed chat names its sender, also after a respawn switch took the sender off a node",
        "3",
    ),
}
