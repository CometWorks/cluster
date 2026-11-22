"""SE-1869: the offline partitions' respawn points go out only when they change.

* ``P2-WA-RESPAWNS-OFFLINE`` - the WA gets OFFLINE_COUNT offline respawn points (the live cluster had ~935 of its 995) and
  node-1 reports LIVE_COUNT moving medbays, LIVE_OVERRIDES of them with the ids of offline points. Over WINDOW_SECONDS
  the lane samples the WA's ``/global-state`` once a second and records, per subsystem, the publishes and the bytes per
  publish. It checks that ``respawns`` carries no offline point, that ``respawns-offline`` is not republished while the
  offline points stay the same, that every role sees the offline and live points merged with the live point winning per
  id, and that a change of the offline points (some removed) reaches every role.

Before SE-1869 the WA republished all points, offline ones included, in ``respawns`` with every change of a live point:
on the live cluster 357 KB per publish, 82 MB/min of its 118 MB/min of global-state broadcasts.
"""

from __future__ import annotations

import json
import time

from scenarios_wa import _post, _q, _try, _wa_cluster
from scenarios_wa_ops import _poll

OFFLINE_COUNT = 935
OFFLINE_REMOVED = 100
LIVE_OVERRIDES = 30
LIVE_COUNT = 2 * LIVE_OVERRIDES
OFFLINE_BASE = 9_186_900_000_000
LIVE_BASE = 9_186_800_000_000
MOTION_SECONDS = 2
WINDOW_SECONDS = 30
CONVERGE_TIMEOUT = 60
ROLES = ("node-1", "node-2", "wa-1")


def _point(origin: str, entity: int, name: str) -> dict:
    # ~440 bytes of JSON a point, a little over the live ~360.
    return {"Origin": origin, "RespawnEntityId": entity, "GridEntityId": entity - 1, "PartitionId": 1869,
            "Generation": 1, "Name": f"SE-1869 {name} " + "x" * 150, "OwnerIdentityId": 0, "ShareMode": 2,
            "Working": True, "SpawnWithoutOxygen": True, "SetFactionToSpawnee": False, "OxygenLevel": 0.5,
            "X": 1000.0, "Y": 2000.0, "Z": 3000.0}


def _offline(bench, count: int) -> None:
    points = json.dumps([_point("offline:test", OFFLINE_BASE, "offline medbay")] if count else [])
    bench.record(f"offline-{count}", _post(bench, "wa-1", "/respawn-test-offline-points?"
                                           + _q(points=points, copies=max(count, 1))))


def _live(bench, seconds: float) -> None:
    # Two points, each LIVE_OVERRIDES times: the first over the lowest offline ids, the second on ids of its own.
    points = json.dumps([_point("node-1", OFFLINE_BASE, "live over offline"),
                         _point("node-1", LIVE_BASE, "live medbay")] if seconds else [])
    bench.record(f"live-{seconds}", _post(bench, "node-1", "/respawn-test-raw-points?"
                                          + _q(points=points, seconds=seconds, copies=LIVE_OVERRIDES, moving="true")))


def _origins(bench, node_id: str) -> dict[int, str]:
    points = (_try(bench, node_id, "/respawn-points") or {}).get("points") or []
    return {p["RespawnEntityId"]: p["Origin"] for p in points
            if OFFLINE_BASE <= p["RespawnEntityId"] < OFFLINE_BASE + OFFLINE_COUNT
            or LIVE_BASE <= p["RespawnEntityId"] < LIVE_BASE + LIVE_OVERRIDES}


def _expected(offline_count: int, live: bool) -> dict[int, str]:
    want = {OFFLINE_BASE + i: "offline:test" for i in range(offline_count)}
    if live:
        want.update({OFFLINE_BASE + i: "node-1" for i in range(LIVE_OVERRIDES)})
        want.update({LIVE_BASE + i: "node-1" for i in range(LIVE_OVERRIDES)})
    return want


def _converge(bench, label: str, want: dict[int, str]) -> None:
    seen: dict[str, dict] = {}

    def done():
        for node_id in ROLES:
            seen[node_id] = _origins(bench, node_id)
        return all(seen[n] == want for n in ROLES) or None
    ok = _poll(done, CONVERGE_TIMEOUT)
    summary = {n: {"count": len(seen[n]), "live": sum(1 for o in seen[n].values() if o == "node-1")} for n in seen}
    bench.record(f"converge-{label}", summary)
    bench.check(f"{label}: every role has {len(want)} test points, the live ones winning per id", ok is not None,
                json.dumps(summary))


def _subsystems(bench) -> dict[str, dict]:
    state = bench.cluster.node("wa-1", "/global-state")
    return {s["subsystem"]: s for s in state.get("subsystems") or []}


def _measure(bench, label: str) -> dict:
    """Publishes and bytes per publish of respawns and respawns-offline at the WA over WINDOW_SECONDS."""
    stats = {name: {"publishes": 0, "bytes": 0, "maxBytes": 0} for name in ("respawns", "respawns-offline")}
    last = _subsystems(bench)
    started = time.time()
    payloads: dict[str, str] = {}
    while time.time() - started < WINDOW_SECONDS:
        time.sleep(1)
        now = _subsystems(bench)
        for name, row in stats.items():
            if name not in now:
                continue
            published = now[name]["sequence"] - (last.get(name) or {}).get("sequence", 0)
            if published > 0:
                size = len(now[name]["payload"].encode())
                row["publishes"] += published
                row["bytes"] += size * published
                row["maxBytes"] = max(row["maxBytes"], size)
                payloads[name] = now[name]["payload"]
        last = now
    elapsed = time.time() - started
    for row in stats.values():
        row["perMinute"] = round(row["publishes"] * 60 / elapsed, 1)
        row["bytesPerPublish"] = round(row["bytes"] / row["publishes"]) if row["publishes"] else 0
        row["kbPerMinute"] = round(row["bytes"] * 60 / elapsed / 1024, 1)
        row["seconds"] = round(elapsed, 1)
    live = payloads.get("respawns") or (last.get("respawns") or {}).get("payload") or "{}"
    stats["respawns"]["offlinePointsInPayload"] = sum(
        1 for p in json.loads(live).get("Points") or [] if str(p.get("Origin", "")).startswith("offline:"))
    bench.record(f"publish-{label}", stats)
    return stats


def p2_wa_respawns_offline(bench) -> None:
    """The offline respawn points travel on their own subsystem, published only when they change."""
    _wa_cluster(bench)
    bench.record("config", bench.cluster.handover_config({"respawns.motionPublishSeconds": str(MOTION_SECONDS)}))
    _offline(bench, OFFLINE_COUNT)
    _live(bench, 600)
    _converge(bench, "merged", _expected(OFFLINE_COUNT, True))

    stats = _measure(bench, "steady")
    live, offline = stats["respawns"], stats["respawns-offline"]
    bench.check("respawns went out while node-1's medbays moved", live["publishes"] > 0, json.dumps(live))
    bench.check("respawns carries no offline point", live["offlinePointsInPayload"] == 0, json.dumps(live))
    bench.check("respawns-offline was not republished while the offline points stayed the same",
                offline["publishes"] == 0, json.dumps(offline))
    snapshot = (_subsystems(bench).get("respawns-offline") or {}).get("payload") or "{}"
    count = len(json.loads(snapshot).get("Points") or [])
    bench.check(f"respawns-offline holds the {OFFLINE_COUNT} offline points", count == OFFLINE_COUNT, str(count))

    before = (_subsystems(bench).get("respawns-offline") or {}).get("sequence", 0)
    _offline(bench, OFFLINE_COUNT - OFFLINE_REMOVED)
    _converge(bench, "offline-changed", _expected(OFFLINE_COUNT - OFFLINE_REMOVED, True))
    after = (_subsystems(bench).get("respawns-offline") or {}).get("sequence", 0)
    bench.record("offline-change-publishes", after - before)
    bench.check("a change of the offline points went out once on respawns-offline", after - before == 1,
                f"{before} -> {after}")

    _live(bench, 0)
    _converge(bench, "live-gone", _expected(OFFLINE_COUNT - OFFLINE_REMOVED, False))
    _offline(bench, 0)
    _converge(bench, "cleared", {})
    bench.exercised(route=f"WA {OFFLINE_COUNT} offline points + node-1 {LIVE_COUNT} moving medbays, "
                          f"respawns.motionPublishSeconds {MOTION_SECONDS}")


SCENARIOS_RESPAWNS_OFFLINE = {
    "P2-WA-RESPAWNS-OFFLINE": (
        p2_wa_respawns_offline,
        "SE-1869: offline respawn points travel on respawns-offline, published only when they change",
        "3",
    ),
}
