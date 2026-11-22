"""SE-1865: a whisper reaches the other player within 2 s, also while respawn points churn.

* ``P2-CHAT-LATENCY`` - clients 1 and 2 on node-1 whisper each other (the Private channel), three lines each way, in
  three phases: quiet; while node-1 and node-2 report thousands of moving respawn points with
  ``respawns.motionPublishSeconds`` 0 (every change published, as before SE-1865; recorded, not checked); and the same
  load with the default. The quiet and default phases must deliver every whisper within ``LIMIT_SECONDS``.

Live (octahedron-test 2026-11-22) whispers reached players and NPC bots 20-60 s late. The World Authority republished
all 999 respawn points (~360 KB) about four times a second, because every report from a node with a medbay on a moving
ship changed the snapshot. The Gateway's ordered global queue toward a node then held up to 87,000 packets, and chat
broadcasts travel on that same queue.

Each whisper records the time from the send to the receipt in the other client's Remote chat log (polled every
0.1 s), the node's ``chat relayed ... sinceAcceptMs`` line (WA accept to node relay), and the Gateway's ordered
global queue toward node-1.
"""

from __future__ import annotations

import json
import random
import re
import time

from scenarios_core import _join, _lines, _nodes
from scenarios_wa import CLIENT2_ID, _q, start_second_client
from scenarios_wa_ops import _client_identity, _poll, _require

LIMIT_SECONDS = 2.0
LOAD_COPIES = 1500  # per node; two nodes stay under the registry's 4096-point cap
LOAD_NAME_PADDING = 900  # ~1.2 KB a point: ~3.6 MB a snapshot, under the codec's 4 MB cap
LOAD_SETTLE_SECONDS = 60
LINES_PER_WAY = 3
SPAM_GAP_SECONDS = 3


def _queue(bench) -> dict:
    backend = _nodes(bench)["node-1"].backend
    nodes = bench.cluster.get("/transport/status").get("nodes") or []
    node = next((n for n in nodes if n.get("endpoint") == backend), None)
    # Null with CLUSTER_RELAY=external: gateway-rs holds the node links. The live Gateway runs the internal relay.
    return (node or {}).get("reliableQueue") or {}


def _relayed(bench) -> list[str]:
    return _lines(bench, "node-1", r"chat relayed channel=Private ")


def _whisper(bench, sender_api, receiver_api, receiver_identity: int, label: str) -> dict:
    text = f"SE-1865 {label} {random.randrange(10**6)}"
    receiver_api.get_chat_messages(0)  # Remote logs received chat from its first chat call on
    relayed_before = len(_relayed(bench))
    queue = _queue(bench)
    started = time.time()
    sender_api.send_chat(text, channel="Private", target_id=receiver_identity)
    receipt = None
    while receipt is None and time.time() - started < 120:
        receipt = next(
            (m for m in receiver_api.get_chat_messages(0).get("messages") or [] if m.get("Text") == text), None
        )
        if receipt is None:
            time.sleep(0.1)
    seconds = round(time.time() - started, 2)
    accept = [int(m) for line in _relayed(bench)[relayed_before:] for m in re.findall(r"sinceAcceptMs=(-?\d+)", line)]
    row = {
        "label": label,
        "seconds": seconds if receipt else None,
        "sinceAcceptMs": accept[-1] if accept else None,
        "gatewayQueue": queue,
    }
    bench.record(f"whisper-{label}", row)
    # Vanilla chat anti-spam drops a sender's next line within SpamMessagesTime and mutes it for a while.
    time.sleep(SPAM_GAP_SECONDS)
    return row


def _phase(bench, api1, api2, id1: int, id2: int, phase: str) -> list[dict]:
    rows = []
    for i in range(LINES_PER_WAY):
        rows.append(_whisper(bench, api1, api2, id2, f"{phase}-1to2-{i}"))
        rows.append(_whisper(bench, api2, api1, id1, f"{phase}-2to1-{i}"))
    bench.record(f"phase-{phase}", rows)
    return rows


def _load(bench, seconds: float) -> None:
    """node-1 and node-2 report LOAD_COPIES moving medbays each for ``seconds`` (0 stops them)."""
    for node, base in (("node-1", 9_100_000_000_000_000), ("node-2", 9_200_000_000_000_000)):
        point = {
            "Origin": node,
            "RespawnEntityId": base,
            "GridEntityId": base - 1,
            "PartitionId": 1,
            "Generation": 1,
            "Name": "SE-1865 moving medbay " + "x" * LOAD_NAME_PADDING,
            "OwnerIdentityId": 0,
            "ShareMode": 2,
            "Working": True,
            "SpawnWithoutOxygen": True,
            "SetFactionToSpawnee": False,
            "OxygenLevel": 0.5,
            "X": 1000.0,
            "Y": 2000.0,
            "Z": 3000.0,
        }
        points = json.dumps([point] if seconds > 0 else [])
        query = _q(points=points, seconds=seconds, copies=LOAD_COPIES, moving="true")
        bench.record(f"load-{node}-{seconds}", bench.cluster.node(node, "/respawn-test-raw-points?" + query, "POST"))


def _worst(rows: list[dict]) -> float:
    return max(r["seconds"] if r["seconds"] is not None else 999.0 for r in rows)


def _check(bench, phase: str, rows: list[dict]) -> None:
    worst = _worst(rows)
    bench.check(
        f"{phase}: every whisper reached the other client within {LIMIT_SECONDS} s (worst {worst} s)",
        worst <= LIMIT_SECONDS,
        json.dumps(rows),
    )


def p2_chat_latency(bench) -> None:
    """Whispers between two clients on one node arrive within 2 s, with and without respawn point churn."""
    api1 = _join(bench)
    api2 = start_second_client(bench, route_node="node-1")
    id1, id2 = _client_identity(bench), _client_identity(bench, CLIENT2_ID)

    _check(bench, "quiet", _phase(bench, api1, api2, id1, id2, "quiet"))

    bench.record("config-every-change", bench.cluster.handover_config({"respawns.motionPublishSeconds": "0"}))
    _load(bench, 3600)
    time.sleep(LOAD_SETTLE_SECONDS)
    bench.record("every-change-worst", _worst(_phase(bench, api1, api2, id1, id2, "every-change")))

    bench.record("config-default", bench.cluster.handover_config({"respawns.motionPublishSeconds": "10"}))
    drained = _poll(lambda: (lambda q: q if q.get("globalOrdered", 0) < 64 else None)(_queue(bench)), 600)
    _require(
        bench,
        "SETUP: the Gateway's ordered queue toward node-1 drained",
        drained is not None,
        json.dumps(_queue(bench)),
    )
    time.sleep(LOAD_SETTLE_SECONDS)
    _check(bench, "throttled", _phase(bench, api1, api2, id1, id2, "throttled"))
    _load(bench, 0)
    bench.exercised(route="clients 1 and 2 on node-1, whispers both ways: quiet, every-change churn, throttled churn")


SCENARIOS_CHAT_LATENCY = {
    "P2-CHAT-LATENCY": (
        p2_chat_latency,
        "SE-1865: whispers arrive within 2 s, also while respawn points on moving ships churn",
        "3",
    ),
}
