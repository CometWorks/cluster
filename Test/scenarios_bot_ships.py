"""SE-1316: the ships bots left behind go right after each cluster backup (BotShipCleaner).

One regular node + WA, no client, a Gateway with an export root so the bench can take real registry backups through
the admin API (POST /admin/v1/registry-backups, then DELETE /admin/v1/artifacts/{id} as Quasar releases the copy).
Two identities stand for sebot traders: identities the player collection maps to Steam IDs of
``cleanup.botSteamIds``, with no client. ``cleanup.botShipIdleMinutes`` = 2 and ``gridDelete.linkSettleSeconds`` = 3.

* ``P2-BOT-SHIP-CLEANUP`` -
  1. After a backup. Probes, each far from the others: A1 (bot A) and A2 (bot B), unused; PLAYER (a player
     identity with a Steam ID outside the bot list); SEATED (bot A's ship with a pilot of bot B in its cockpit); NEAR
     (bot B's ship 30 m from a viewed camera, a player position); FRESH (bot A's, spawned 2 min after the others, so
     the backups below were taken less than the idle time after its first sight). Dry run first: the backup's run
     logs "would remove" for A1 and A2 only, and removes nothing. Then on, at most 1 per run: the next backup removes
     one of them, the one after it the other; each removal is logged with the grid's id, name, owners with their
     Steam IDs, position, partition and the unused and backup times; their emptied partitions are reaped. PLAYER,
     SEATED, NEAR and FRESH stay; the last use of SEATED and NEAR keeps moving.
  2. Offline. OFF (bot A's) goes offline right after its first sight, before any backup could hold it unused, so it
     is saved with its stamp. A backup is taken once it is past the idle time: OFF is offline and stays there. Woken
     (a recovery load), it comes back with the stamp it was saved with (/cluster/bot-ships), and the offline sweep
     removes it right before its partition would go offline again, "before offline": the partition is reaped and no
     offline save holds it. Without the stamp in the builder the wake would start its clock over and OFF would go
     offline again with the ship: the red arm.
"""
from __future__ import annotations

import json
import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import _lines
from scenarios_grid_delete import _gone_for_good, _homes, _members, _offline_members, _require

NODE = "node-1"
STARTED = [NODE, "wa-1"]
X = 900_000.0
SPACING = 30_000.0
IDLE_SECONDS = 120
BOTS = "76561190000000001-76561190000000099"
BOT_STEAM = {"A": 76561190000000001, "B": 76561190000000002}
PLAYER_STEAM = 76561198000001316


def _backup(bench, n: int) -> int:
    """A registry backup through the admin API, released as Quasar releases it; returns the census's backupAt."""
    control = bench.cluster.config.gateway_control
    started = int(time.time())
    op = bench.cluster._request("POST", "/admin/v1/registry-backups", control, 60, body={"ttlHours": 1},
                                headers={"Idempotency-Key": f"se1316-backup-{n}"})["data"]
    done = wait_until(lambda: (lambda d: d if d["state"] in ("Succeeded", "Failed") else None)(
        bench.cluster._request("GET", f"/admin/v1/operations/{op['operationId']}", control)["data"]), 60, f"backup {n} done")
    _require(bench, f"backup {n}: the registry copy succeeded", done["state"] == "Succeeded", json.dumps(done)[:300])
    artifact = done["result"]["artifactId"]
    bench.cluster._request("DELETE", f"/admin/v1/artifacts/{artifact}", control, 60,
                           headers={"Idempotency-Key": f"se1316-release-{n}"})
    at = wait_until(lambda: (lambda c: c.get("backupAt") if (c.get("backupAt") or 0) >= started else None)(
        bench.cluster.get("/registry/npc-census")), 30, f"backup {n} in the census")
    bench.record(f"backup-{n}", {"operation": done, "backupAt": at})
    return int(at)


def _run_line(bench, backup_at: int) -> str | None:
    """node-1's summary line of the run after the backup of backup_at."""
    stamp = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime(backup_at))
    try:
        return wait_until(lambda: next(iter(_lines(bench, NODE, rf"bot ship cleanup \(\w+\) after the backup of {re.escape(stamp)}")), None),
                          45, "the cleanup run after the backup")
    except ClusterError:
        return None


def _ships(bench) -> dict[int, dict]:
    return {int(s["id"]): s for s in bench.cluster.node(NODE, "/bot-ships")["ships"]}


def _logged(bench, verb: str, grid: int) -> list[str]:
    return _lines(bench, NODE, rf"bot ship cleanup {verb} grid {grid} ")


def p2_bot_ship_cleanup(bench) -> None:
    bench.cluster.config.gateway_args += ["--export-root", str(bench.dir / "exports")]
    bench.start_cluster(nodes=STARTED)
    selftest = bench.cluster.node(NODE, "/self-test?section=bot-ship-cleanup")
    bench.check("node self-test bot-ship-cleanup (rule, knobs, the stamp in the builder)", selftest.get("ok") is True,
                json.dumps(selftest)[:300])
    bench.record("config-setup", bench.cluster.handover_config({
        "offline.enabled": "false", "npc.sweepSeconds": "0", "gridDelete.linkSettleSeconds": "3",
        "cleanup.botShips": "dryrun", "cleanup.botSteamIds": BOTS, "cleanup.botShipIdleMinutes": str(IDLE_SECONDS // 60),
        "cleanup.botShipUseRadius": "50", "cleanup.botShipsMaxPerRun": "5"}))
    owners = {name: bench.cluster.node(NODE, f"/bot-ship-test-identity?name=Trader-{name}&steamId={steam}", "POST")
              for name, steam in BOT_STEAM.items()}
    owners["player"] = bench.cluster.node(NODE, f"/bot-ship-test-identity?name=player-1316&steamId={PLAYER_STEAM}", "POST")
    bench.record("owners", owners)
    _require(bench, "setup: the identities map to their Steam IDs and are no NPCs",
             all(o.get("steamId") and o.get("npc") is False for o in owners.values()), json.dumps(owners))
    ids = {k: o["identityId"] for k, o in owners.items()}

    # 1. After a backup.
    xs = {name: X + i * SPACING for i, name in enumerate(("A1", "A2", "PLAYER", "SEATED", "NEAR", "FRESH"))}
    camera = bench.cluster.node(NODE, f"/camera-test-spawn?x={xs['NEAR']}&y=30&z=0", "POST")
    view = bench.cluster.node(NODE, f"/camera-test-view?entityId={camera.get('camera')}&active=true", "POST")
    _require(bench, "setup: the camera beside NEAR is viewed", view.get("viewed") is True, json.dumps(view))
    _require(bench, "setup: node-1's position registry holds the camera", wait_until(lambda: any(
        row.get("ClientId") == 0 and row.get("EntityId") == camera.get("camera") for row in bench.cluster.node(NODE, "/position-registry")),
        60, "camera in the position registry"))
    probes = {}
    for name, owner in (("A1", ids["A"]), ("A2", ids["B"]), ("PLAYER", ids["player"]), ("NEAR", ids["B"])):
        probes[name] = bench.cluster.node(NODE, f"/trash-test-spawn?x={xs[name]}&y=0&z=0&blocks=8&npc=true&owner={owner}", "POST")
    seated = bench.cluster.node(NODE, f"/rich-test-spawn?kind=antennaship&x={xs['SEATED']}&y=0&z=0&builtBy={ids['A']}", "POST")
    probes["SEATED"] = {"grid": seated["grids"][0]["grid"], "name": seated["grids"][0]["name"]}
    bench.record("pilot", bench.cluster.node(NODE, f"/bot-ship-test-pilot?grid={probes['SEATED']['grid']}&owner={ids['B']}", "POST"))
    grids = {name: int(p["grid"]) for name, p in probes.items()}
    tracked = wait_until(lambda: (lambda s: s if set(grids[n] for n in ("A1", "A2", "SEATED", "NEAR")) <= s.keys() else None)(_ships(bench)),
                         60, "the bot ships tracked")
    bench.check("node-1 tracks the bot ships, not the player's", grids["PLAYER"] not in tracked, json.dumps(tracked)[:500])
    homes = _homes(bench, {k: grids[k] for k in ("A1", "A2")})
    _require(bench, "setup: A1 and A2 each alone in a partition node-1 owns", homes and homes["A1"] != homes["A2"], json.dumps(homes))
    # Past the idle time for the first five; FRESH is first seen now.
    time.sleep(max(0, max(tracked[grids[n]]["lastUse"] for n in ("A1", "A2")) + IDLE_SECONDS + 5 - time.time()))
    probes["FRESH"] = bench.cluster.node(NODE, f"/trash-test-spawn?x={xs['FRESH']}&y=0&z=0&blocks=8&npc=true&owner={ids['A']}", "POST")
    grids["FRESH"] = int(probes["FRESH"]["grid"])
    wait_until(lambda: grids["FRESH"] in _ships(bench), 30, "FRESH tracked")
    bench.record("probes", probes)

    first = _backup(bench, 1)
    line = _run_line(bench, first)
    bench.record("run-1", line)
    would = {name: bool(_logged(bench, "would remove", g)) for name, g in grids.items()}
    present = _members(bench)
    bench.check("dry run: the backup's run logs A1 and A2 as 'would remove', nothing else",
                line is not None and would == {n: n in ("A1", "A2") for n in grids}, json.dumps({"line": line, "would": would}))
    bench.check("dry run: nothing is removed", all(g in present for g in grids.values()), json.dumps(sorted(present))[:300])

    bench.record("config-on", bench.cluster.handover_config({"cleanup.botShips": "on", "cleanup.botShipsMaxPerRun": "1"}))
    second = _backup(bench, 2)
    line2 = _run_line(bench, second)
    try:
        wait_until(lambda: {grids["A1"], grids["A2"]} - _members(bench).keys(), 30, "A1 or A2 removed")
    except ClusterError:
        pass
    gone2 = {n for n, g in grids.items() if g not in _members(bench)}
    bench.check("on, cap 1: the next backup removes one of A1 and A2, nothing else", gone2 in ({"A1"}, {"A2"}),
                json.dumps({"gone": sorted(gone2), "line": line2}))
    third = _backup(bench, 3)
    line3 = _run_line(bench, third)
    try:
        wait_until(lambda: not ({grids["A1"], grids["A2"]} & _members(bench).keys()), 30, "A1 and A2 removed")
    except ClusterError:
        pass
    gone3 = {n for n, g in grids.items() if g not in _members(bench)}
    bench.check("the backup after it removes the other; PLAYER, SEATED, NEAR and FRESH stay", gone3 == {"A1", "A2"},
                json.dumps({"gone": sorted(gone3), "line": line3}))
    removed = {n: (_logged(bench, "removed", grids[n]) or [None])[0] for n in ("A1", "A2")}
    bench.record("removed", removed)
    shape = {n: bool(r and f"Trader-{'A' if n == 'A1' else 'B'} ({ids['A' if n == 'A1' else 'B']}, {BOT_STEAM['A' if n == 'A1' else 'B']})" in r
                     and f"in partition {homes[n]}" in r and "unused since" in r and "after backup" in r) for n, r in removed.items()}
    bench.check("each removal is logged with its owners and Steam IDs, partition, last use, backup and cause", all(shape.values()),
                json.dumps(removed))
    ships = _ships(bench)
    recent = {n: ships.get(grids[n], {}).get("lastUse") for n in ("SEATED", "NEAR")}
    bench.check("SEATED and NEAR count as in use: their last use is after the third backup",
                all(use and use >= third - 15 for use in recent.values()), json.dumps({"third": third, **recent}))
    bench.check("the run summaries name the capped ship", bool(line2) and "over cleanup.botShipsMaxPerRun, next backup 1" in line2, str(line2))
    _gone_for_good(bench, "after-backup", {k: grids[k] for k in ("A1", "A2")}, {homes["A1"], homes["A2"]})

    # 2. Offline: the stamp rides the offline save, and the offline sweep removes the ship before it goes offline again.
    off = bench.cluster.node(NODE, f"/trash-test-spawn?x={X - SPACING}&y=0&z=0&blocks=8&npc=true&owner={ids['A']}", "POST")
    grid_off = int(off["grid"])
    stamp = wait_until(lambda: _ships(bench).get(grid_off, {}).get("lastUse"), 30, "OFF tracked")
    home_off = _homes(bench, {"OFF": grid_off})["OFF"]
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "10", "offline.graceSeconds": "0"}))
    wait_until(lambda: grid_off in _offline_members(bench).get(home_off, []), 120, "OFF offline")
    bench.record("config-offline-hold", bench.cluster.handover_config({"offline.enabled": "false"}))
    time.sleep(max(0, stamp + IDLE_SECONDS + 2 - time.time()))
    fourth = _backup(bench, 4)
    _run_line(bench, fourth)
    bench.check("OFF stays in its offline partition through the backup", grid_off in _offline_members(bench).get(home_off, []),
                json.dumps(_offline_members(bench).get(home_off)))
    bench.record("wake", bench.cluster.post(f"/registry/partitions/offline/load?partition={home_off}&node={NODE}&reason=se1316"))
    carried = wait_until(lambda: _ships(bench).get(grid_off, {}).get("lastUse"), 120, "OFF back online and tracked")
    bench.check("OFF comes back with the stamp it was saved with", carried == stamp, json.dumps({"saved": stamp, "loaded": carried}))
    bench.record("config-offline-again", bench.cluster.handover_config({"offline.enabled": "true"}))
    try:
        line = wait_until(lambda: next(iter(_logged(bench, "removed", grid_off)), None), 120, "OFF removed before offline")
    except ClusterError:
        line = None
    bench.check("the offline sweep removes OFF right before its partition goes offline", bool(line) and "before offline" in line, str(line))
    bench.record("config-offline-off", bench.cluster.handover_config({"offline.enabled": "false"}))
    _gone_for_good(bench, "before-offline", {"OFF": grid_off}, {home_off})
    bench.exercised(grids=grids, off=grid_off, backups=[first, second, third, fourth])

SCENARIOS_BOT_SHIPS = {
    "P2-BOT-SHIP-CLEANUP": (p2_bot_ship_cleanup, "SE-1316: ships bots left go after a backup that holds them unused, dry run first and capped; in-use and player ships stay; offline partitions too", "3"),
}
