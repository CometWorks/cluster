"""SE-1870: the World Authority broadcasts only the player profiles that changed, and cameras of gone entities expire.

* ``P3-PLR-03`` - one client on node-1 or node-2 (both running from the start). The WA is given a live-sized record
  (227 synthetic players with 2.4 KB toolbars, 19 cameras each, 5 of them with 200), one of which changes a camera
  distance every 6 s, the rate the live record changed. Then:

  1. measures, at the default config, the ``player-profiles`` broadcasts on the WA's ``/global-state`` for ``MEASURE_SECONDS``: how many, and
     how big (recorded; the median must stay under 64 KB, the full record is ~1.4 MB);
  2. checks the other regular node holds the same record as the WA, player by player, which it only got by applying
     the broadcasts (it resynced before any of the synthetic players existed);
  3. marks a toolbar slot and checks the mark reaches the other node's copy;
  4. sets the camera retention to 30 s, gives the client 50 cameras of entities that exist nowhere, relogs it (the node seeds them into its session on
     reconcile), and checks they expire after ``profiles.cameraRetentionSeconds`` (set to 30 s) instead of being
     reported back forever, and that the toolbar mark survived the relog;
  5. checks no player holds more than ``profiles.maxCameras`` (32) cameras.

Live (octahedron-test 2026-11-22): 227 profiles, 1.36 MB, republished whole about every 6 s on any one player's change;
4,356 cameras, the top five players at 157-256 (the cap).
"""

from __future__ import annotations

import json
import statistics
import time

from cluster import ClusterError
from scenarios_core import CLIENT_ID, _join
from scenarios_wa_ops import _poll
from scenarios_player import toolbar_slots

SUBSYSTEM = "player-profiles"
LOAD = {"count": 227, "cameras": 19, "heavy": 5, "heavyCameras": 200, "slots": 9, "churnSeconds": 6}
MEASURE_SECONDS = 120
RETENTION_SECONDS = 30
MAX_CAMERAS = 32
FAKE_CAMERAS = 50
MARK_SLOT, MARK = 5, "MyObjectBuilder_PhysicalGunObject/WelderItem"


def _profiles(bench, node: str) -> dict:
    status = bench.cluster.node(node, "/player-profiles")
    return {p["player"]: p for p in status.get("profiles") or []}, status


def _shape(profiles: dict) -> dict:
    return {k: (p["toolbarBytes"], p["toolbarItems"], p["cameras"]) for k, p in profiles.items()}


def _host(bench) -> str:
    for node in ("node-1", "node-2"):
        _, status = _profiles(bench, node)
        if any(key.startswith(f"{CLIENT_ID}:") for key in status.get("reconciled") or []):
            return node
    raise ClusterError("no regular node hosts the client")


def _state(bench) -> tuple[int, int]:
    state = bench.cluster.node("wa-1", "/global-state")
    row = next((s for s in state.get("subsystems") or [] if s["subsystem"] == SUBSYSTEM), None)
    return (row["sequence"], len(row.get("payload") or "")) if row else (0, 0)


def _measure(bench, seconds: float) -> dict:
    """Every broadcast the WA makes in ``seconds``: count from the sequence, size from the payload it keeps."""
    sizes, missed = [], 0
    last, _ = _state(bench)
    first = last
    started = time.time()
    while time.time() - started < seconds:
        time.sleep(0.25)
        sequence, size = _state(bench)
        if sequence > last:
            sizes.append(size)
            missed += sequence - last - 1
            last = sequence
    minutes = (time.time() - started) / 60
    publishes = last - first
    return {
        "seconds": round(time.time() - started, 1),
        "publishes": publishes,
        "publishesPerMinute": round(publishes / minutes, 1),
        "sampled": len(sizes),
        "missedSamples": missed,
        "bytesMedian": int(statistics.median(sizes)) if sizes else 0,
        "bytesMax": max(sizes, default=0),
        # Missed samples are counted at the median size.
        "bytesPerMinute": int((sum(sizes) + missed * (statistics.median(sizes) if sizes else 0)) / minutes),
    }


def p3_plr_03(bench) -> None:
    record: dict = {}
    try:
        api = _join(bench)
        host = _host(bench)
        other = "node-2" if host == "node-1" else "node-1"
        record["host"] = host

        query = "&".join(f"{k}={v}" for k, v in {**LOAD, "seconds": 3600}.items())
        record["load"] = bench.cluster.node("wa-1", "/player-profile-test-load?" + query, "POST")
        time.sleep(15)
        record["recordPlayers"] = len(_profiles(bench, "wa-1")[0])

        # 1. the broadcasts
        record["broadcasts"] = measured = _measure(bench, MEASURE_SECONDS)
        _, snapshot_bytes = _state(bench)
        record["lastPayloadBytes"] = snapshot_bytes
        bench.check(
            f"{SUBSYSTEM} broadcasts are small: median {measured['bytesMedian']} B, "
            f"{measured['publishesPerMinute']}/min, {measured['bytesPerMinute']} B/min",
            0 < measured["bytesMedian"] < 64 * 1024,
            json.dumps(measured),
        )

        # 2. the other node holds the same record, built from broadcasts
        def same() -> dict | None:
            a, _ = _profiles(bench, "wa-1")
            b, _ = _profiles(bench, other)
            diff = {k: (v, _shape(b).get(k)) for k, v in _shape(a).items() if _shape(b).get(k) != v}
            return {"players": len(a), "nodePlayers": len(b)} if not diff and len(a) == len(b) else None

        agreed = _poll(same, 60, interval=2)
        record["replicaAgrees"] = agreed
        bench.check(f"{other} holds the WA's record, player by player", agreed is not None)

        # 3. a toolbar edit reaches the other node
        api.set_toolbar_slot(MARK_SLOT, MARK)
        player = f"{CLIENT_ID}:0"

        def toolbar_reached() -> dict | None:
            a, _ = _profiles(bench, "wa-1")
            b, _ = _profiles(bench, other)
            if player not in a or player not in b or a[player]["toolbarItems"] == 0:
                return None
            return a[player] if a[player]["toolbarBytes"] == b[player]["toolbarBytes"] else None

        before = _profiles(bench, "wa-1")[0].get(player, {}).get("toolbarBytes")
        reached = _poll(lambda: (r := toolbar_reached()) and r["toolbarBytes"] != before and r, 30)
        record["toolbarEdit"] = {"before": before, "reached": reached}
        bench.check(f"a toolbar edit reaches {other}'s copy of the record", bool(reached))

        # 4. cameras of gone entities expire, also after a relog seeds them into the node's session
        # Only now: a 30 s retention refreshes the stamps every 7 s, which the broadcasts above would have counted.
        try:
            record["config"] = bench.cluster.handover_config(
                {"profiles.cameraRetentionSeconds": str(RETENTION_SECONDS), "profiles.maxCameras": str(MAX_CAMERAS)}
            )
        except Exception as error:  # noqa: BLE001 - a build from before SE-1870 has no such keys
            record["config"] = {"error": str(error)[:300]}
        record["fakeCameras"] = bench.cluster.node(
            "wa-1", f"/player-profile-test-cameras?steamId={CLIENT_ID}&serialId=0&count={FAKE_CAMERAS}", "POST"
        )
        try:
            api.exit_game(force=True)
        except Exception as error:  # noqa: BLE001 - the client may close before it answers
            record["exit"] = str(error)[:200]
        time.sleep(10)
        bench.kill_client(CLIENT_ID)
        api = bench.start_client(CLIENT_ID, label=f"{CLIENT_ID}-relog")
        bench.wait_spawned(CLIENT_ID)
        relogged = api.get_player_settings()
        record["relog"] = {"toolbar": toolbar_slots(relogged), "host": _host(bench)}
        bench.check(
            "the toolbar mark survives the relog",
            toolbar_slots(relogged).get(MARK_SLOT) == MARK,
            json.dumps(record["relog"]),
        )
        started = time.time()
        expired = _poll(
            lambda: (c := _profiles(bench, "wa-1")[0].get(player, {}).get("cameras", 999)) < 10 and {"cameras": c},
            RETENTION_SECONDS + 150,
            interval=5,
        )
        record["camerasAfterRelog"] = {
            "expired": expired,
            "seconds": round(time.time() - started),
            "cameras": _profiles(bench, "wa-1")[0].get(player, {}).get("cameras"),
        }
        bench.check(
            f"{FAKE_CAMERAS} cameras of entities that exist nowhere expire after the relog",
            expired is not None,
            json.dumps(record["camerasAfterRelog"]),
        )

        # 5. the cap
        most = max((p["cameras"] for p in _profiles(bench, "wa-1")[0].values()), default=0)
        record["mostCameras"] = most
        bench.check(f"no player keeps more than {MAX_CAMERAS} cameras (most: {most})", most <= MAX_CAMERAS)
        bench.exercised(route="live-sized profile record on the WA, client relog, fake cameras")
    finally:
        try:
            bench.cluster.node("wa-1", "/player-profile-test-load?count=0&cameras=0&heavy=0&heavyCameras=0"
                               "&slots=0&churnSeconds=0&seconds=0", "POST")
        except Exception:  # noqa: BLE001
            pass
        bench.record("player-profiles", record)


SCENARIOS_PLAYER_PROFILES = {
    "P3-PLR-03": (
        p3_plr_03,
        "SE-1870: player profile broadcasts carry only changes; cameras of gone entities expire",
        "P3",
    ),
}
