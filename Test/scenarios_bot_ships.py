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
     SEATED, NEAR and FRESH stay; the last use of SEATED and NEAR keeps moving. HUMAN (bot A's, unused, 1500 m from the
     camera: no use, but a human player within the sync distance) is kept for that reason; once the camera is no longer
     viewed, the next backup removes it.
  2. Offline. OFF (bot A's) goes offline right after its first sight, before any backup could hold it unused, so it
     is saved with its stamp. A backup is taken once it is past the idle time: OFF is offline and stays there. Woken
     (a recovery load), it comes back with the stamp it was saved with (/cluster/bot-ships), and the offline sweep
     removes it right before its partition would go offline again, "before offline": the partition is reaped and no
     offline save holds it. Without the stamp in the builder the wake would start its clock over and OFF would go
     offline again with the ship: the red arm.

* ``P2-BOT-SHIP-RETURN`` (SE-1419) - node-1, node-2 and WA, cleanup in dry run. A bot ship spawned on node-1 is
  stamped there and handed over to node-2 with its stamp. On node-2 it is sold to a player, so node-2 forgets it,
  and it goes back to node-1 without a stamp while node-1 still remembers the old one (a grid that left is kept for
  10 minutes). The return handover commits and node-1 logs no handover residual for it. Before the fix node-1 wrote
  its old stamp into the import's builder, the round-trip gate found a mod storage entry the frozen source did not
  have, and the handover aborted: the red arm (octahedron-test partitions 34201 and 35864, 2026-11-13).
"""
from __future__ import annotations

import json
import re
import time

from cluster import ClusterError, wait_until
from scenarios_core import _handover_url, _lines, _partitions
from scenarios_grid_delete import _gone_for_good, _homes, _members, _offline_members, _require

NODE = "node-1"
STARTED = [NODE, "wa-1"]
X = 900_000.0
SPACING = 30_000.0
IDLE_SECONDS = 120
BOTS = "76561190000000001-76561190000000099"
BOT_STEAM = {"A": 76561190000000001, "B": 76561190000000002}
PLAYER_STEAM = 76561198000001316
HUMAN_METRES = 1500   # from the camera: outside cleanup.botShipUseRadius, inside the sync distance (3000 m)


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
    # Nothing is spawned before node-1 has claimed the saves it loaded: a camera spawned before that was gone a moment later.
    loaded = wait_until(lambda: (lambda n: n if n and len(_lines(bench, NODE, r"startup partition claimed: ")) >= n else None)(
        len(_lines(bench, NODE, r"assigned save loaded: "))), 120, "node-1's startup claims")
    _require(bench, "setup: node-1 claimed every save it loaded at startup", loaded)
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
    for name, owner in (("A1", ids["A"]), ("A2", ids["B"]), ("PLAYER", ids["player"]), ("NEAR", ids["B"]), ("HUMAN", ids["A"])):
        x = xs["NEAR"] + HUMAN_METRES if name == "HUMAN" else xs[name]
        probes[name] = bench.cluster.node(NODE, f"/trash-test-spawn?x={x}&y=0&z=0&blocks=8&npc=true&owner={owner}", "POST")
    seated = bench.cluster.node(NODE, f"/rich-test-spawn?kind=antennaship&x={xs['SEATED']}&y=0&z=0&builtBy={ids['A']}", "POST")
    probes["SEATED"] = {"grid": seated["grids"][0]["grid"], "name": seated["grids"][0]["name"]}
    bench.record("pilot", bench.cluster.node(NODE, f"/bot-ship-test-pilot?grid={probes['SEATED']['grid']}&owner={ids['B']}", "POST"))
    grids = {name: int(p["grid"]) for name, p in probes.items()}
    tracked = wait_until(lambda: (lambda s: s if all(s.get(grids[n], {}).get("lastUse") for n in ("A1", "A2", "SEATED", "NEAR", "HUMAN")) else None)(
        _ships(bench)), 60, "the bot ships tracked")
    bench.check("node-1 tracks the bot ships, not the player's", grids["PLAYER"] not in tracked, json.dumps(tracked)[:500])
    homes = _homes(bench, {k: grids[k] for k in ("A1", "A2")})
    _require(bench, "setup: A1 and A2 each alone in a partition node-1 owns", homes and homes["A1"] != homes["A2"], json.dumps(homes))
    # Past the idle time for the first five; FRESH is first seen now.
    time.sleep(max(0, max(tracked[grids[n]]["lastUse"] for n in ("A1", "A2", "HUMAN")) + IDLE_SECONDS + 5 - time.time()))
    probes["FRESH"] = bench.cluster.node(NODE, f"/trash-test-spawn?x={xs['FRESH']}&y=0&z=0&blocks=8&npc=true&owner={ids['A']}", "POST")
    grids["FRESH"] = int(probes["FRESH"]["grid"])
    wait_until(lambda: _ships(bench).get(grids["FRESH"], {}).get("lastUse"), 30, "FRESH tracked")
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
    bench.check("the backup after it removes the other; PLAYER, SEATED, NEAR, HUMAN and FRESH stay", gone3 == {"A1", "A2"},
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
    bench.check("HUMAN, unused but with a human player within the sync distance, is kept for that reason",
                all(line and "human player within sync distance 1" in line for line in (line2, line3)),
                json.dumps({"run2": line2, "run3": line3}))
    # The human leaves: the camera is no longer viewed, so the next backup takes HUMAN.
    bench.record("unview", bench.cluster.node(NODE, f"/camera-test-view?entityId={camera.get('camera')}&active=false", "POST"))
    wait_until(lambda: not any(row.get("EntityId") == camera.get("camera") for row in bench.cluster.node(NODE, "/position-registry")),
               60, "the camera out of the position registry")
    fourth = _backup(bench, 4)
    line4 = _run_line(bench, fourth)
    try:
        wait_until(lambda: grids["HUMAN"] not in _members(bench), 30, "HUMAN removed")
    except ClusterError:
        pass
    gone4 = {n for n, g in grids.items() if g not in _members(bench)}
    bench.check("with nobody human in sync distance any more, the next backup removes HUMAN, nothing else", gone4 == {"A1", "A2", "HUMAN"},
                json.dumps({"gone": sorted(gone4), "line": line4}))
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
    fifth = _backup(bench, 5)
    _run_line(bench, fifth)
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
    bench.exercised(grids=grids, off=grid_off, backups=[first, second, third, fourth, fifth])

def _holder(bench, grid: int, nodes) -> tuple[str, int] | None:
    """(node, partition) of the node partition holding the grid."""
    for node in nodes:
        for row in _partitions(bench, node):
            if any(member.get("id") == grid for member in row.get("members") or []):
                return node, int(row["id"])
    return None


def _move(bench, grid: int, source: str, target: str, nodes) -> tuple[dict | str, bool]:
    """A clientless handover of the grid's partition; the answer and whether the grid then sits on the target."""
    _, partition = _holder(bench, grid, nodes)
    try:
        answer = bench.cluster.post(_handover_url(bench, partition, clientless=True, source=source, target=target), timeout=300)
    except ClusterError as error:
        answer = str(error)
    try:
        wait_until(lambda: (_holder(bench, grid, nodes) or ("",))[0] == target, 60, f"grid {grid} on {target}")
        return answer, True
    except ClusterError:
        return answer, False


def p2_bot_ship_return(bench) -> None:
    nodes = ("node-1", "node-2")
    bench.start_cluster(nodes=[*nodes, "wa-1"])
    selftest = bench.cluster.node("node-1", "/self-test?section=bot-ship-cleanup")
    bench.check("node self-test bot-ship-cleanup (an arriving builder replaces the remembered stamp)", selftest.get("ok") is True,
                json.dumps(selftest)[:300])
    bench.record("config", bench.cluster.handover_config({
        "offline.enabled": "false", "npc.sweepSeconds": "0", "gridDelete.linkSettleSeconds": "3",
        "cleanup.botShips": "dryrun", "cleanup.botSteamIds": BOTS, "cleanup.botShipIdleMinutes": "60"}))
    owners = {}
    for name, steam in (("A", BOT_STEAM["A"]), ("player", PLAYER_STEAM)):
        first = bench.cluster.node("node-1", f"/bot-ship-test-identity?name=Trader-{name}&steamId={steam}", "POST")
        second = bench.cluster.node("node-2", f"/bot-ship-test-identity?name=Trader-{name}&steamId={steam}&identityId={first['identityId']}", "POST")
        _require(bench, f"setup: {name} is the same identity with the same Steam ID on both nodes",
                 first["identityId"] == second["identityId"] and first["steamId"] == second["steamId"] == steam, json.dumps([first, second]))
        owners[name] = first["identityId"]
    bench.record("owners", owners)
    ship = int(bench.cluster.node("node-1", f"/trash-test-spawn?x={X}&y=0&z=0&blocks=8&npc=true&owner={owners['A']}", "POST")["grid"])
    stamp = wait_until(lambda: (lambda s: s.get(ship, {}).get("lastUse"))(
        {int(s["id"]): s for s in bench.cluster.node("node-1", "/bot-ships")["ships"]}), 60, "the ship stamped on node-1")
    bench.record("stamp", {"ship": ship, "lastUse": stamp, "holder": _holder(bench, ship, nodes)})

    answer, there = _move(bench, ship, "node-1", "node-2", nodes)
    bench.record("handover-out", answer)
    _require(bench, "setup: the ship moved to node-2", there, str(answer)[:300])
    carried = wait_until(lambda: (lambda s: s.get(ship, {}).get("lastUse"))(
        {int(s["id"]): s for s in bench.cluster.node("node-2", "/bot-ships")["ships"]}), 60, "the ship tracked on node-2")
    bench.check("node-2 took the stamp the ship arrived with", carried == stamp, json.dumps({"node-1": stamp, "node-2": carried}))

    time.sleep(5)  # past the import grace and gridDelete.linkSettleSeconds
    bench.record("sale", bench.cluster.node("node-2", f"/bot-ship-test-owner?grid={ship}&owner={owners['player']}", "POST"))
    wait_until(lambda: ship not in {int(s["id"]) for s in bench.cluster.node("node-2", "/bot-ships")["ships"]}, 30,
               "the ship no bot ship on node-2")
    time.sleep(12)  # one tracking pass (10 s): node-2 forgets the stamp
    answer, back = _move(bench, ship, "node-2", "node-1", nodes)
    bench.record("handover-back", answer)
    residuals = _lines(bench, "node-1", rf"handover residual .*#{ship}/")
    bench.record("residuals", residuals)
    bench.check("the ship goes back to node-1 without a stamp: the handover commits, no residual on its storage",
                back and not residuals, json.dumps({"back": back, "residuals": residuals[:4], "answer": str(answer)[:300]}))
    bench.exercised(ship=ship, stamp=stamp)


SCENARIOS_BOT_SHIPS = {
    "P2-BOT-SHIP-CLEANUP": (p2_bot_ship_cleanup, "SE-1316: ships bots left go after a backup that holds them unused, dry run first and capped; in-use and player ships stay; offline partitions too", "3"),
    "P2-BOT-SHIP-RETURN": (p2_bot_ship_return, "SE-1419: a bot ship back on a node that remembers its old stamp arrives without one and its handover commits", "3"),
}
