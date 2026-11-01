"""#1354: the cluster-wide NPC census, offline partitions included, and the unvisited/gap-band NPC sweep.

The cluster side of the custom MES (#1321): every node reports its NPC grids (a top-level grid whose big owners are
all NPC identities) per partition, the registry keeps those of an offline partition with its row, every node polls
the merged census and publishes Count(x, y, z, radius) in-process for the mod-mes plugin (AppDomain slot
``CometWorks.Cluster.NpcCensus.v1``); a periodic sweep removes NPC grids nobody has been near for
``npc.unvisitedMinutes``, sooner inside a gap band of mod-mes's spawn pattern (slot ``CometWorks.ModMes.InGap.v1``).

One regular node + WA, no client. Probes far out, each alone in its partition (the test plugin's
``/trash-test-spawn``, ``npc=true`` adds a battery owned by an NPC identity):

* ``P2-NPC-CENSUS`` -
  1. A (NPC) goes offline: the registry census lists it as offline, and Count at its position through node-1's
     slot is 1 (no double spawn when a player returns). A is loaded back: its unvisited count carries on from the
     value it went offline with instead of restarting at 0 (the visited state survives an offline/online cycle).
  2. B (NPC, inside the stand-in gap band), C (NPC, outside it) and P (an unowned grid, in the census as kind
     "unowned" but not counted, its rule off: cleanup.unownedMinutes=0) with npc.unvisitedMinutes=2 and
     gapRemovalFactor=4: B is removed after ~30 s unvisited while C stays; C after ~120 s; P never; the census drops
     each removed grid; the partitions B and C emptied are reaped by the registry.

#1379, one cleanup policy on the same sweep, by the big owners of each top-level grid:

* ``P2-GRID-CLEANUP`` - one grid of each class, each alone in its partition, sweep every 5 s, npc.unvisitedMinutes=2,
  cleanup.unownedMinutes=1, gapRemovalFactor=4, npc.sweepKeepFactions="OCT,RLWE" (RLWE stands in for the Octahedron's
  OCT: the test world has no OCT faction). Never removed: KEEP (static, owned by RLWE's founder), PLAYER (owned by a
  player identity), LANE (static, ownerless: the trade lane computers). After 120 s unvisited: MES (SPRT's founder, MES's
  storage key), NOKEY (SPRT's founder, no key: a piece split off an MES grid) and FOREIGN (GCMU's founder). After 60 s:
  DROP (ownerless, dynamic). After 15 s: DEBRIS (ownerless, built by the player identity, inside the gap band). Each
  removal's logged unvisited seconds lie in [limit, limit + 20 s); the census carries each grid with its kind. The red
  arm is a package with the owner check removed (NpcCensusService.KindOf without its Player and Keep branches): KEEP
  and PLAYER then go with the NPC grids, and this scenario fails.

#1390, small wreckage of cheap blocks (WreckageCleaner), whatever the owner:

* ``P2-WRECK-CLEANUP`` - cleanup.wreckSeconds=30, probes of 8 armor blocks unless stated. Normal regime (the node's sim
  ratio pinned at 1.0 through the save-test seam): ownerless armor, a player grid whose battery has no charge and an NPC
  grid with a charged one go after 30 s with nobody within the sync distance; wreckage 1500 m from a viewed camera (a
  player position) stays. Aggressive (sim ratio 0.5): that wreckage goes too, "near a player"; never removed in either:
  a player grid with a charged battery (functional and powered, far and near), 60 blocks, 3 batteries (not cheap), a
  static grid, a kept faction's grid, armor 20 m from the camera (cleanup.wreckTouchMeters). Before offline
  (wreckSeconds 0, wreckBeforeOffline): a wreck beside a powered player grid is removed by the offline sweep's hook and
  the partition goes offline holding only the player grid; a wreck alone is removed and its emptied partition released;
  no offline row holds a wreck probe. Before the drain: a graceful cluster stop removes a wreck from the partition it
  drains into an offline Final; after a warm restart no offline save holds a wreck probe and every kept grid is in one.

#1381, a meteor's vanilla lifetime (~111 s at rest) across loads (MeteorLifetime). Handovers are the path that
matters: a meteor's body never sleeps, so its partition never goes offline while the meteor lives.

* ``P2-METEOR-LIFETIME`` - two meteors at rest on node-1, each alone in its partition. STAY never moves: it closes
  111 s after its spawn (the control: the bench's clock against vanilla's). HOP is handed over node-1 -> node-2 at
  20 s, back at 55 s and to node-2 again at 95 s, each through the round-trip gate with no residual and with the
  birth it was spawned with; it is there after the last load (the 30 s load grace) and closes at max(spawn + 111 s,
  last load + 30 s), long before a fresh lifetime after the last load would end. The red arm is a package whose
  MeteorLifetime.Initialized returns at once (vanilla: every load starts the life over): HOP outlives its limit
  and this scenario fails.

#1365, the NPC factions a mod defines (``IsDefault``) on a world that does not hold them, as MES and its packs on the
Octahedron template. A probe mod (a made-up Workshop id, staged into every process's Mods folder) defines two:

* ``P2-NPC-FACTIONS`` -
  1. WA first (the bench's warm-up): the WA founds both. node-1 takes them from the WA's newest global save at load,
     before vanilla would found its own: the same faction ids and founders as the WA, the founders known to node-1 as
     NPC identities, no faction created locally, and the WA's faction snapshot then removes nothing and replaces no
     faction of node-1's. A grid owned by a founder (as MES owns what it spawns) is an NPC grid in the census.
  2. The unknown-owner guard: a grid owned by an identity node-1 has never seen is reported (the known positive);
     the founder-owned grid is not.
  3. A fresh cluster with node-1 up first: its load waits for the WA's first global save, then takes the same set.
"""
from __future__ import annotations

import json
import re
import shutil
import time
from pathlib import Path

import bench as benchmod

from cluster import ClusterError, wait_until
from scenarios import _all_partitions
from scenarios_core import _lines, _partitions

NODE = "node-1"
STARTED = [NODE, "wa-1"]
FAR = 600_000.0
GAP_MIN_X = FAR + 30_000


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _members(bench) -> dict[int, int]:
    """grid id -> the node-1 partition holding it."""
    return {m["id"]: int(row["id"]) for row in _partitions(bench, NODE) for m in row.get("members") or []}


def _census(bench) -> dict:
    """The registry's census; empty before its first complete report pass (503)."""
    try:
        return bench.cluster.get("/registry/npc-census")
    except ClusterError as error:
        if "503" not in str(error) and getattr(error, "status", None) != 503:
            raise
        return {"online": 0, "offline": 0, "grids": []}


def _grid_rows(bench) -> dict[int, dict]:
    return {int(row["id"]): row for row in _census(bench)["grids"]}


def _count(bench, x: float, radius: float = 500) -> dict:
    return bench.cluster.node(NODE, f"/npc-census-count?x={x}&y=0&z=0&radius={radius}")


def _spawn(bench, x: float, npc: bool) -> dict:
    return bench.cluster.node(NODE, f"/trash-test-spawn?x={x}&y=0&z=0&static=false&blocks=8&npc={str(npc).lower()}", "POST")


def _home(bench, grid: int) -> int:
    return wait_until(lambda: (lambda m: m.get(grid) if m.get(grid) and _all_partitions(bench).get(m[grid]) == NODE else None)(
        _members(bench)), 120, f"grid {grid} in a partition node-1 owns")


def p2_npc_census(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    selftest = bench.cluster.node(NODE, "/self-test?section=npc-census")
    bench.check("node self-test npc-census (census parse, Count and its 60 s limit, removal limits, knobs, slots)",
                selftest.get("ok") is True and selftest.get("ran") == ["npc-census"], json.dumps(selftest)[:300])
    # No removals and no offline transitions while the fixture is built.
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false"}))

    # 1. Offline: A is counted while offline and keeps its unvisited count across the offline/online cycle.
    a = _spawn(bench, FAR, npc=True)
    bench.record("probe-a", a)
    _require(bench, "setup: A's big owner is an NPC identity", a.get("bigOwners") == [a.get("owner")] and a.get("owner"), json.dumps(a))
    grid_a = int(a["grid"])
    home_a = _home(bench, grid_a)
    online = wait_until(lambda: (lambda r: r if r.get(grid_a, {}).get("u", 0) >= 10 else None)(_grid_rows(bench)), 90,
                        "A in the registry census with >= 10 s unvisited")
    bench.check("A is in the census online, the non-NPC world is not", _census(bench)["online"] == 1, json.dumps(online)[:300])
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    offline = wait_until(lambda: (lambda c: c if c["offline"] == 1 and c["online"] == 0 else None)(_census(bench)), 120,
                         "A counted as offline")
    bench.record("census-offline", offline)
    bench.record("config-offline-off", bench.cluster.handover_config({"offline.enabled": "false"}))
    row_a = next((row for row in offline["grids"] if row["id"] == grid_a), {})
    u_offline = row_a.get("u", 0)
    bench.check("the census keeps the offline partition's NPC grid with its position, unvisited count and offline flag",
                abs(row_a.get("x", 0) - FAR) < 50 and u_offline >= 10 and row_a.get("o") is True and row_a.get("k") == "npc", json.dumps(offline))
    counted = wait_until(lambda: (lambda c: c if c.get("count") == 1 else None)(_count(bench, FAR)), 30,
                         "node-1's slot counting the offline A")
    bench.check("Count through node-1's AppDomain slot counts the offline grid (no double spawn on return)",
                counted.get("published") and counted["count"] == 1 and 0 <= counted["ageSeconds"] <= 15, json.dumps(counted))
    empty = _count(bench, FAR + 200_000)
    bench.check("Count is 0 where there is no NPC grid", empty.get("count") == 0, json.dumps(empty))
    asked = bench.cluster.post(f"/registry/partitions/offline/load?partition={home_a}&node={NODE}&reason=tm-1354")
    bench.record("load-a", asked)
    back = wait_until(lambda: (lambda c: c if c["online"] == 1 and c["offline"] == 0 else None)(_census(bench)), 180,
                      "A back online in the census")
    u_back = next(row for row in back["grids"] if row["id"] == grid_a)["u"]
    bench.check("the unvisited count carries on after the offline/online cycle instead of restarting at 0",
                u_back >= u_offline, f"offline {u_offline} s, back online {u_back} s")

    # 2. The sweep: B in the gap band, C outside it, P not an NPC grid.
    probes = {"B": _spawn(bench, FAR + 40_000, npc=True), "C": _spawn(bench, FAR - 40_000, npc=True),
              "P": _spawn(bench, FAR - 80_000, npc=False)}
    bench.record("probes", probes)
    grids = {key: int(probe["grid"]) for key, probe in probes.items()}
    homes = {key: _home(bench, grid) for key, grid in grids.items()}
    rows = wait_until(lambda: (lambda r: r if set(grids.values()) <= r.keys() else None)(_grid_rows(bench)), 60, "B, C and P in the census")
    bench.check("the unowned grid P is in the census as kind unowned, B and C as npc",
                rows[grids["P"]].get("k") == "unowned" and rows[grids["B"]].get("k") == rows[grids["C"]].get("k") == "npc",
                json.dumps({k: rows[g] for k, g in grids.items()}))
    bench.record("gap", bench.cluster.node(NODE, f"/npc-test-gap?minX={GAP_MIN_X}", "POST"))
    bench.record("config-sweep", bench.cluster.handover_config({
        "npc.sweepSeconds": "5", "npc.unvisitedMinutes": "2", "npc.gapRemovalFactor": "4", "cleanup.unownedMinutes": "0"}))
    start = time.time()
    try:
        wait_until(lambda: grids["B"] not in _members(bench), 90, "B removed")
        b_gone = time.time() - start
    except ClusterError:
        b_gone = None
    members = _members(bench)
    bench.check("B (gap band, 30 s limit) is removed while C (120 s limit) stays", b_gone is not None and grids["C"] in members,
                json.dumps({"bGoneAfter": b_gone, "cPresent": grids["C"] in members}))
    try:
        wait_until(lambda: grids["C"] not in _members(bench), 180, "C removed")
        c_gone = True
    except ClusterError:
        c_gone = False
    bench.check("C is removed once unvisited for 2 min", c_gone)
    bench.check("the unowned grid P stays (cleanup.unownedMinutes=0 switches its rule off)", grids["P"] in _members(bench))
    removed = {k: _lines(bench, NODE, rf"npc sweep removed grid {grids[k]} .*gap {'true' if k == 'B' else 'false'}") for k in "BC"}
    bench.check("node-1 logged both removals with their gap flag", all(removed.values()), json.dumps({k: bool(v) for k, v in removed.items()}))
    try:
        wait_until(lambda: not ({grids["B"], grids["C"]} & _grid_rows(bench).keys()), 30, "B and C out of the census")
        dropped = True
    except ClusterError:
        dropped = False
    bench.check("the census drops the removed grids", dropped)
    try:
        wait_until(lambda: not ({homes["B"], homes["C"]} & {int(r["Partition"]) for r in bench.cluster.get("/registry/partitions")}),
                   120, "B's and C's emptied partitions reaped")
        reaped = True
    except ClusterError:
        reaped = False
    bench.check("the partitions the sweep emptied are reaped (no recovery brings the grids back)", reaped)
    census = _census(bench)
    report = _partitions(bench, NODE)
    bench.record("overhead", {"censusBytes": len(json.dumps(census)), "reportBytes": len(json.dumps(report)),
                              "census": census})
    bench.exercised(grids=grids, partitions=homes, a=grid_a, bGoneAfter=b_gone)


CLEANUP_X = 1_200_000.0
CLEANUP_SPACING = 40_000.0
# name: (class, removal limit in s or None = never, spawn query)
CLEANUP_PROBES = {
    "KEEP": ("keep faction (OCT stand-in), static", None, "static=true&npc=true&owner={RLWE}"),
    "PLAYER": ("player-owned", None, "npc=true&owner={player}"),
    "LANE": ("ownerless, static (trade lane computer)", None, "static=true&builtBy=0"),
    "MES": ("MES-spawned (storage key)", 120, "npc=true&owner={SPRT}&mesKey=true"),
    "NOKEY": ("MES faction, no key (split-off piece)", 120, "npc=true&owner={SPRT}"),
    "FOREIGN": ("foreign NPC faction", 120, "npc=true&owner={GCMU}"),
    "DROP": ("ownerless, dynamic (container drop)", 60, "builtBy=0"),
    "DEBRIS": ("ownerless, built by the player, in a gap band", 15, "builtBy={player}"),
}
CLEANUP_KINDS = {"KEEP": "keep", "LANE": "unowned", "MES": "npc", "NOKEY": "npc", "FOREIGN": "npc", "DROP": "unowned", "DEBRIS": "unowned"}
LATE_SECONDS = 20  # one 5 s sweep + one 10 s unvisited step + slack


def p2_grid_cleanup(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    selftest = bench.cluster.node(NODE, "/self-test?section=npc-census")
    bench.check("node self-test npc-census (kinds, removal rules, cleanup.* knobs)", selftest.get("ok") is True, json.dumps(selftest)[:300])
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false"}))
    owners = {tag: bench.cluster.node(NODE, f"/faction-test-status?tag={tag}").get("founderId") for tag in ("SPRT", "GCMU", "RLWE")}
    owners["player"] = bench.cluster.node(NODE, "/identity-test-create?name=cluster-1379-player", "POST").get("identityId")
    bench.record("owners", owners)
    _require(bench, "setup: the factions' founders and the player identity exist", all(owners.values()), json.dumps(owners))
    player = bench.cluster.node(NODE, f"/identity-test-status?identityId={owners['player']}")
    _require(bench, "setup: the player identity is not an NPC identity", player.get("npc") is False, json.dumps(player))
    order = list(CLEANUP_PROBES)
    xs = {name: CLEANUP_X + i * CLEANUP_SPACING for i, name in enumerate(order)}
    gap_min_x = xs["DEBRIS"] - CLEANUP_SPACING / 2
    bench.record("gap", bench.cluster.node(NODE, f"/npc-test-gap?minX={gap_min_x}", "POST"))
    # The rules are live before the first spawn, so each grid's unvisited count starts at its spawn.
    bench.record("config-sweep", bench.cluster.handover_config({
        "npc.sweepSeconds": "5", "npc.unvisitedMinutes": "2", "npc.gapRemovalFactor": "4", "npc.sweepKeepFactions": "OCT,RLWE",
        "cleanup.unownedMinutes": "1"}))
    probes = {}
    for name in order:
        query = CLEANUP_PROBES[name][2].format(**owners)
        probes[name] = bench.cluster.node(NODE, f"/trash-test-spawn?x={xs[name]}&y=0&z=0&blocks=8&{query}", "POST")
    bench.record("probes", probes)
    grids = {name: int(probe["grid"]) for name, probe in probes.items()}
    expected_owner = {"KEEP": owners["RLWE"], "PLAYER": owners["player"], "MES": owners["SPRT"], "NOKEY": owners["SPRT"], "FOREIGN": owners["GCMU"]}
    shape = {name: probes[name].get("bigOwners") == ([expected_owner[name]] if name in expected_owner else []) for name in order}
    shape["MES key"] = probes["MES"].get("mesKey") is True and probes["NOKEY"].get("mesKey") is False
    shape["static"] = probes["KEEP"].get("isStatic") is True and probes["LANE"].get("isStatic") is True and probes["DROP"].get("isStatic") is False
    _require(bench, "setup: each probe has the big owners, MES key and static flag of its class", all(shape.values()), json.dumps(shape))

    def present() -> set[str]:
        members = _members(bench)
        return {name for name, grid in grids.items() if grid in members}

    seen = wait_until(lambda: (lambda r: r if {grids[n] for n in CLEANUP_KINDS if n != "DEBRIS"} <= r.keys() else None)(_grid_rows(bench)),
                      60, "the probes in the census")
    kinds = {name: seen.get(grids[name], {}).get("k") for name in CLEANUP_KINDS}
    bench.check("the census carries each grid with its kind, the player's grid not at all",
                all(kinds[n] == k for n, k in CLEANUP_KINDS.items() if n != "DEBRIS") and grids["PLAYER"] not in seen, json.dumps(kinds))
    due = {name for name, probe in CLEANUP_PROBES.items() if probe[1]}
    try:
        wait_until(lambda: not (present() & due), 120 + 60, "every due grid removed")
    except ClusterError:
        pass
    time.sleep(30)   # every kept grid is now 30 s past the longest limit
    left = present()
    bench.record("present", sorted(left))
    removed = {}
    for name in order:
        lines = _lines(bench, NODE, rf"npc sweep removed grid {grids[name]} ")
        match = re.search(r"unvisited (\d+)s, gap (\w+), kind (\w+)", lines[0]) if lines else None
        removed[name] = {"seconds": int(match.group(1)), "gap": match.group(2), "kind": match.group(3)} if match else None
    bench.record("removed", removed)
    for name in order:
        label, limit, _ = CLEANUP_PROBES[name]
        if limit is None:
            bench.check(f"{name} ({label}) is never removed", name in left and removed[name] is None,
                        json.dumps({"present": name in left, "removed": removed[name]}))
        else:
            r = removed[name]
            bench.check(f"{name} ({label}) is removed after {limit} s unvisited, not before and not {LATE_SECONDS} s late",
                        name not in left and r is not None and limit <= r["seconds"] < limit + LATE_SECONDS,
                        json.dumps({"present": name in left, "removed": r}))
    bench.check("only DEBRIS was removed under the gap boost", {n for n, r in removed.items() if r and r["gap"] == "true"} == {"DEBRIS"},
                json.dumps(removed))
    # Past the last moment any rule would have taken them; the census row lags a report pass and a 10 s count step.
    overdue = max(probe[1] for probe in CLEANUP_PROBES.values() if probe[1]) + LATE_SECONDS

    def kept_counts() -> dict:
        rows = _grid_rows(bench)
        return {name: rows.get(grids[name], {}).get("u") or 0 for name in ("KEEP", "LANE")}
    try:
        kept = wait_until(lambda: (lambda k: k if min(k.values()) >= overdue else None)(kept_counts()), 60, "the kept grids overdue in the census")
    except ClusterError:
        kept = kept_counts()
    bench.check(f"the kept grids are overdue for every rule (unvisited >= {overdue} s) and still in the census",
                min(kept.values()) >= overdue, json.dumps(kept))
    bench.exercised(grids=grids, removed=removed, kinds=kinds, owners=owners)


WRECK_X = 700_000.0
WRECK_SPACING = 20_000.0
WRECK_SECONDS = 30
WRECK_LATE = 25  # one 10 s pass + one 10 s clear step + slack
# name: (what it is, the phase that removes it or None = never, spawn query); A = normal regime, B = aggressive.
WRECK_FAR = {
    "W_FREE": ("ownerless armor, dynamic", "A", "builtBy=0"),
    "W_UNPOWERED": ("player-owned, functional battery with no charge, switched off", "A", "npc=true&owner={player}&charge=0&batteriesOff=true"),
    "W_NPC_POWERED": ("NPC-owned, charged battery (the protection is for players)", "A", "npc=true&owner={SPRT}&charge=1"),
    "K_POWERED": ("player-owned, charged battery (functional and powered)", None, "npc=true&owner={player}&charge=1"),
    "K_BIG": ("ownerless armor, 60 blocks", None, "builtBy=0&blocks=60"),
    "K_COSTLY": ("ownerless armor + 3 uncharged batteries (3 blocks not cheap)", None, "builtBy=0&batteries=3&charge=0"),
    "K_STATIC": ("ownerless armor, static", None, "builtBy=0&static=true"),
    "K_KEEP": ("kept faction (OCT stand-in), dynamic", None, "npc=true&owner={RLWE}&charge=0"),
}
# Around a viewed camera (a player position with ClientId 0): (what, phase, query, metres from the camera along x, y).
WRECK_NEAR = {
    "W_NEAR": ("ownerless armor 1500 m from the camera (inside the sync distance)", "B", "builtBy=0", 1500, 0),
    "W_NEAR_UNPOWERED": ("player-owned, no charge, 1600 m from the camera", "B", "npc=true&owner={player}&charge=0&batteriesOff=true", 0, 1600),
    "K_TOUCH": ("ownerless armor 20 m from the camera (inside wreckTouchMeters)", None, "builtBy=0", 20, 0),
    "K_NEAR_POWERED": ("player-owned, charged, 1500 m from the camera", None, "npc=true&owner={player}&charge=1", -1500, 0),
}


def _wreck_removals(bench) -> dict[int, dict]:
    """grid id -> its first removal line: cause, regime, clear seconds."""
    removed = {}
    for line in _lines(bench, NODE, r"wreck cleanup removed grid "):
        m = re.search(r"removed grid (-?\d+) .* in partition (\d+): (.+?), regime (\w+), clear (\d+)s", line)
        if m and int(m.group(1)) not in removed:
            removed[int(m.group(1))] = {"partition": int(m.group(2)), "cause": m.group(3), "regime": m.group(4), "clear": int(m.group(5))}
    return removed


def _offline_members(bench) -> dict[int, list[int]]:
    return {int(row["Partition"]): [int(x) for x in row.get("Members") or []]
            for row in bench.cluster.get("/registry/partitions/offline").get("Partitions", [])}


def p2_wreck_cleanup(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    # Nothing is spawned before node-1 has loaded and claimed the world's saves: a camera spawned inside that window
    # was gone a moment later (20261021-230710Z).
    loaded = _poll(lambda: (lambda n: n if n and len(_lines(bench, NODE, r"startup partition claimed: ")) >= n else None)(
        len(_lines(bench, NODE, r"assigned save loaded: "))), 120)
    _require(bench, "setup: node-1 claimed every save it loaded at startup", loaded)
    selftest = bench.cluster.node(NODE, "/self-test?section=wreck-cleanup")
    bench.check("node self-test wreck-cleanup (regime hysteresis, wreck shape, due rule, cleanup.wreck* knobs)",
                selftest.get("ok") is True, json.dumps(selftest)[:300])
    # The #1379 sweep and offlining off while the fixture is built; the node's sim ratio pinned at 1.0 (normal regime).
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false"}))
    bench.cluster.node(NODE, "/save-test?simRatio=1.0", "POST")
    owners = {tag: bench.cluster.node(NODE, f"/faction-test-status?tag={tag}").get("founderId") for tag in ("SPRT", "RLWE")}
    owners["player"] = bench.cluster.node(NODE, "/identity-test-create?name=cluster-1390-player", "POST").get("identityId")
    bench.record("owners", owners)
    _require(bench, "setup: the factions' founders and the player identity exist", all(owners.values()), json.dumps(owners))
    camera = bench.cluster.node(NODE, f"/camera-test-spawn?x={WRECK_X}&y=200000&z=0", "POST")
    view = bench.cluster.node(NODE, f"/camera-test-view?entityId={camera.get('camera')}&active=true", "POST")
    bench.record("camera", {"probe": camera, "view": view})
    _require(bench, "setup: the camera probe is viewed", view.get("viewed") is True, json.dumps(view))
    # Its demand entry has to be in node-1's merged position registry before any probe near it starts counting.
    seen = _poll(lambda: any(row.get("ClientId") == 0 and row.get("EntityId") == camera.get("camera")
                             for row in bench.cluster.node(NODE, "/position-registry")), 60)
    _require(bench, "setup: node-1's position registry holds the viewed camera", seen)
    bench.record("config-wreck", bench.cluster.handover_config({
        "cleanup.wreckSeconds": str(WRECK_SECONDS), "npc.sweepKeepFactions": "OCT,RLWE"}))
    probes = {}
    for i, (name, (_, _, query)) in enumerate(WRECK_FAR.items()):
        q = query.format(**owners)
        q = q if "blocks=" in q else "blocks=8&" + q
        probes[name] = bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X + i * WRECK_SPACING}&y=0&z=0&{q}", "POST")
    for name, (_, _, query, dx, dy) in WRECK_NEAR.items():
        q = query.format(**owners)
        probes[name] = bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X + dx}&y={200000 + dy}&z=0&blocks=8&{q}", "POST")
    spawned_at = time.time()
    bench.record("probes", probes)
    grids = {name: int(probe["grid"]) for name, probe in probes.items()}
    working = {name: probe.get("working") for name, probe in probes.items()}
    expect_working = {"K_POWERED": True, "K_NEAR_POWERED": True, "W_NPC_POWERED": True, "W_UNPOWERED": False, "W_NEAR_UNPOWERED": False,
                      "K_COSTLY": False, "W_FREE": False}
    _require(bench, "setup: the charged grids are working (functional and powered), the uncharged and armor ones not",
             all(working[n] is w for n, w in expect_working.items()) and probes["K_BIG"].get("blocks") == 60, json.dumps(working))
    phase = {**{n: p[1] for n, p in WRECK_FAR.items()}, **{n: p[1] for n, p in WRECK_NEAR.items()}}

    def present() -> set[str]:
        members = _members(bench)
        return {name for name, grid in grids.items() if grid in members}

    # A: normal regime. Only the far wreckage goes, after WRECK_SECONDS with nobody within the sync distance.
    due_a = {n for n, p in phase.items() if p == "A"}
    try:
        wait_until(lambda: not (present() & due_a), WRECK_SECONDS + 60, "the phase A wreckage removed")
    except ClusterError:
        pass
    time.sleep(20)   # every grid kept so far is past the limit in both counts
    left_a = present()
    removed_a = _wreck_removals(bench)
    bench.record("phase-a", {"present": sorted(left_a), "removed": {n: removed_a.get(g) for n, g in grids.items()}})
    for name in sorted(due_a):
        r = removed_a.get(grids[name])
        label = (WRECK_FAR.get(name) or WRECK_NEAR.get(name))[0]
        bench.check(f"normal regime: {name} ({label}) is removed away from players after {WRECK_SECONDS} s, not {WRECK_LATE} s late",
                    name not in left_a and r is not None and r["cause"] == "away from players" and r["regime"] == "normal"
                    and WRECK_SECONDS <= r["clear"] < WRECK_SECONDS + WRECK_LATE, json.dumps(r))
    kept_a = set(grids) - due_a
    bench.check("normal regime: nothing else is removed - wreckage within the sync distance of the camera stays",
                kept_a <= left_a and not any(grids[n] in removed_a for n in kept_a),
                json.dumps({"missing": sorted(kept_a - left_a), "removed": [n for n in kept_a if grids[n] in removed_a]}))

    # B: the node's sim ratio pinned at 0.5: aggressive. The wreckage inside the sync distance goes; the touch radius,
    # the protected grids and every kept shape stay.
    bench.cluster.node(NODE, "/save-test?simRatio=0.5", "POST")
    entered = _poll_line(bench, r"wreck cleanup regime aggressive", 60)
    bench.check("the regime turns aggressive within 60 s of sim speed 0.5", entered is not None, entered or "")
    due_b = {n for n, p in phase.items() if p == "B"}
    try:
        wait_until(lambda: not (present() & due_b), 60, "the phase B wreckage removed")
    except ClusterError:
        pass
    time.sleep(20)
    left_b = present()
    removed_b = _wreck_removals(bench)
    bench.record("phase-b", {"present": sorted(left_b), "removed": {n: removed_b.get(g) for n, g in grids.items()}})
    for name in sorted(due_b):
        r = removed_b.get(grids[name])
        bench.check(f"aggressive regime: {name} ({WRECK_NEAR[name][0]}) is removed near a player",
                    name not in left_b and r is not None and r["cause"] == "near a player" and r["regime"] == "aggressive", json.dumps(r))
    never = {n for n, p in phase.items() if p is None}
    bench.check("aggressive regime: no protected or kept grid is removed (" + ", ".join(sorted(never)) + ")",
                never <= left_b and not any(grids[n] in removed_b for n in never),
                json.dumps({"missing": sorted(never - left_b), "removed": [n for n in never if grids[n] in removed_b]}))
    bench.cluster.node(NODE, "/save-test?simRatio=1.0", "POST")
    left = _poll_line(bench, r"wreck cleanup regime normal", 90)
    bench.check("the regime returns to normal within 90 s of sim speed 1.0", left is not None, left or "")

    # C: before offline. No periodic removal any more (wreckSeconds 0), so only the hook can take these: D (a wreck
    # beside a protected grid) and E (a wreck alone: its partition is emptied and released, never offlined).
    bench.record("config-offline-hook", bench.cluster.handover_config({"cleanup.wreckSeconds": "0", "cleanup.wreckBeforeOffline": "true"}))
    offline_probes = {
        "D_WRECK": bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X}&y=-200000&z=0&blocks=8&builtBy=0", "POST"),
        "D_POWERED": bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X + 60}&y=-200000&z=0&blocks=8&npc=true&owner={owners['player']}&charge=1", "POST"),
        "E_WRECK": bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X}&y=-400000&z=0&blocks=8&builtBy=0", "POST"),
    }
    bench.record("offline-probes", offline_probes)
    grids.update({name: int(probe["grid"]) for name, probe in offline_probes.items()})
    home_d = _home(bench, grids["D_WRECK"])
    _require(bench, "setup: D_WRECK and D_POWERED share a partition", _members(bench).get(grids["D_POWERED"]) == home_d,
             json.dumps({"D_WRECK": home_d, "D_POWERED": _members(bench).get(grids["D_POWERED"])}))
    home_e = _home(bench, grids["E_WRECK"])
    bench.record("config-offline", bench.cluster.handover_config({
        "offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    table = wait_until(lambda: (lambda t: t if home_d in t else None)(_offline_members(bench)), 180, "D's partition offline")
    released = _poll_line(bench, rf"partition {home_e} generation \d+ is empty after", 60)
    time.sleep(10)
    table = _offline_members(bench)
    removed_c = _wreck_removals(bench)
    bench.record("phase-c", {"offline": {str(k): v for k, v in table.items()}, "removed": {n: removed_c.get(g) for n, g in grids.items()}})
    rd, re_ = removed_c.get(grids["D_WRECK"]), removed_c.get(grids["E_WRECK"])
    bench.check("before offline: D_WRECK is removed by the hook (no periodic rule is on)",
                rd is not None and rd["cause"] == "before offline" and rd["partition"] == home_d, json.dumps(rd))
    bench.check("before offline: D's partition goes offline holding D_POWERED and not D_WRECK",
                grids["D_POWERED"] in table.get(home_d, []) and grids["D_WRECK"] not in table.get(home_d, []), json.dumps(table.get(home_d)))
    bench.check("before offline: E_WRECK, alone, is removed and its emptied partition released instead of offlined",
                re_ is not None and re_["cause"] == "before offline" and released is not None and home_e not in table,
                json.dumps({"removed": re_, "released": released, "offline": home_e in table}))
    wreck_ids = {grids[n] for n in grids if n.startswith("W_") or n.endswith("_WRECK")}
    offline_ids = {member for members in table.values() for member in members}
    bench.check("no offline partition carries a wreck probe in its save", not (wreck_ids & offline_ids),
                json.dumps(sorted(wreck_ids & offline_ids)))
    kept_ids = {grids[n] for n in never} | {grids["D_POWERED"]}
    online_ids = set(_members(bench))
    bench.check("every protected and kept grid still exists, online or in an offline save",
                kept_ids <= offline_ids | online_ids, json.dumps(sorted(kept_ids - offline_ids - online_ids)))

    # D: before the drain. The quiet sweep can no longer take F (minOnline 1 h); a graceful cluster stop drains every
    # partition into an offline Final, and F's must not hold F_WRECK. The warm restart's offline table shows the Finals.
    bench.record("config-drain", bench.cluster.handover_config({"offline.minOnlineSeconds": "3600"}))
    drain_probes = {
        "F_WRECK": bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X}&y=0&z=300000&blocks=8&builtBy=0", "POST"),
        "F_POWERED": bench.cluster.node(NODE, f"/trash-test-spawn?x={WRECK_X + 60}&y=0&z=300000&blocks=8&npc=true&owner={owners['player']}&charge=1", "POST"),
    }
    bench.record("drain-probes", drain_probes)
    grids.update({name: int(probe["grid"]) for name, probe in drain_probes.items()})
    home_f = _home(bench, grids["F_WRECK"])
    graceful = bench.cluster.stop(grace=240)
    removed_d = _wreck_removals(bench).get(grids["F_WRECK"])
    drained = _lines(bench, NODE, rf"drain took partition {home_f} OFFLINE")
    bench.cluster.start(STARTED)
    bench.cluster.wait_ready(nodes=STARTED)
    table = _offline_members(bench)
    bench.record("phase-d", {"graceful": graceful, "removed": removed_d, "drained": drained, "offline": table.get(home_f)})
    bench.check("before the drain: F_WRECK is removed by the drain's hook at the cluster stop",
                removed_d is not None and removed_d["cause"] == "before drain" and removed_d["partition"] == home_f, json.dumps(removed_d))
    bench.check("the drained partition went offline with F_POWERED and without F_WRECK (offline table after the restart)",
                graceful and bool(drained) and grids["F_POWERED"] in table.get(home_f, []) and grids["F_WRECK"] not in table.get(home_f, []),
                json.dumps({"graceful": graceful, "drained": drained[:1], "members": table.get(home_f)}))
    offline_ids = {member for members in table.values() for member in members}
    wrecks_saved = {grids[n] for n in grids if n.startswith("W_") or n.endswith("_WRECK")} & offline_ids
    kept_missing = ({grids[n] for n in never} | {grids["D_POWERED"], grids["F_POWERED"]}) - offline_ids
    bench.check("after the stop no offline save holds a wreck probe, and every kept grid is in one",
                not wrecks_saved and not kept_missing, json.dumps({"wrecks": sorted(wrecks_saved), "keptMissing": sorted(kept_missing)}))
    bench.exercised(grids=grids, removed={n: _wreck_removals(bench).get(g) for n, g in grids.items()}, spawnedAt=spawned_at)


def _poll(predicate, timeout: float):
    try:
        return wait_until(lambda: predicate() or None, timeout, "condition")
    except ClusterError:
        return None


def _poll_line(bench, pattern: str, timeout: float) -> str | None:
    return _poll(lambda: next(iter(_lines(bench, NODE, pattern)), None), timeout)


FACTION_MOD_ID = 9991365
FACTION_MOD = f"{FACTION_MOD_ID}.sbm"
FACTION_TAGS = ("TFA", "TFB")
FACTIONS_SBC = """<?xml version="1.0"?>
<Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Factions>
""" + "".join(f"""    <Faction Tag="{tag}" Founder="{tag} Founder">
      <Id><TypeId>MyObjectBuilder_FactionDefinition</TypeId><SubtypeId>Cluster1365-{tag}</SubtypeId></Id>
      <DisplayName>Cluster 1365 {tag}</DisplayName>
      <IsDefault>true</IsDefault>
      <DefaultRelation>Enemies</DefaultRelation>
      <AcceptHumans>false</AcceptHumans>
      <AutoAcceptMember>false</AutoAcceptMember>
      <StartingBalance>1000</StartingBalance>
      <Type>None</Type>
      <DiscoveredByDefault>true</DiscoveredByDefault>
    </Faction>
""" for tag in FACTION_TAGS) + """  </Factions>
</Definitions>
"""
UNKNOWN_OWNER = 144115188075859365  # an identity id (type IDENTITY) no process of the world has


def _faction_template(bench) -> Path:
    """The packaged world with the probe mod listed and none of its factions."""
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-Simple-npc-factions"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    item = (f'<Mods>\n    <ModItem FriendlyName="Cluster 1365 NPC factions">\n      <Name>{FACTION_MOD}</Name>\n'
            f'      <PublishedFileId>{FACTION_MOD_ID}</PublishedFileId>\n'
            f'      <PublishedServiceName>Steam</PublishedServiceName>\n    </ModItem>\n  </Mods>')
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        text = path.read_text()
        if any(f"<Tag>{tag}</Tag>" in text for tag in FACTION_TAGS):
            raise AssertionError(f"{name} already holds a probe faction")
        new, n = re.subn(r"<Mods\s*/>", item, text, count=1)
        if n != 1:
            raise AssertionError(f"{name}: no empty <Mods /> to replace")
        path.write_text(new)
    return target


def _boot_factions(bench, processes: list[str], template: Path, node_first: bool) -> None:
    bench.cluster.config.world_template = template
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    for process in processes:
        folder = bench.run_root / f"{process}-data" / "Mods" / FACTION_MOD / "Data"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "Factions.sbc").write_text(FACTIONS_SBC)
    if not node_first:
        bench.cluster.start(processes)
        bench.cluster.wait_ready(nodes=processes)
        return
    bench.cluster.start([NODE])
    wait_until(lambda: _lines(bench, NODE, r"factions waiting_for_authority_save=true"), 300, "node-1 waiting for the WA's save")
    bench.cluster.start_node("wa-1")
    bench.cluster.wait_ready(nodes=processes)


def _faction(bench, process: str, tag: str) -> dict:
    return bench.cluster.node(process, f"/faction-test-status?tag={tag}")


def _same_factions(bench, label: str) -> dict:
    """Each probe faction on wa-1 and node-1: same id and founder, the founder an NPC identity node-1 knows."""
    seen = {}
    for tag in FACTION_TAGS:
        wa, node = _faction(bench, "wa-1", tag), _faction(bench, NODE, tag)
        founder = bench.cluster.node(NODE, f"/identity-test-status?identityId={node.get('founderId') or 1}")
        seen[tag] = {"wa": [wa.get("factionId"), wa.get("founderId")], NODE: [node.get("factionId"), node.get("founderId")],
                     "founder": founder}
        bench.check(f"{label}: {tag} exists on wa-1 (the WA founded the mod's faction)", wa.get("exists") is True, json.dumps(wa))
        bench.check(f"{label}: {tag} has the WA's faction id and founder on node-1",
                    node.get("exists") is True and seen[tag]["wa"] == seen[tag][NODE], json.dumps(seen[tag]))
        bench.check(f"{label}: node-1 knows {tag}'s founder as an NPC identity",
                    founder.get("exists") is True and founder.get("npc") is True, json.dumps(founder))
    return seen


def _adoption_checks(bench, label: str) -> None:
    adopted = _lines(bench, NODE, r"factions adopted_from_authority=true")
    bench.record(f"adopted-{label}", adopted)
    bench.check(f"{label}: node-1 took both factions from the WA's global save",
                adopted and all(f"default_factions_adopted={len(FACTION_TAGS)}/{len(FACTION_TAGS)}" in line for line in adopted), str(adopted))
    local = _lines(bench, NODE, r"factions created_locally=true|authority_adopt_failed=true")
    bench.check(f"{label}: node-1 created no NPC faction itself", not local, str(local[:3]))


def p2_npc_factions(bench) -> None:
    processes = [NODE, "wa-1"]
    template = _faction_template(bench)
    # 1. WA first
    _boot_factions(bench, processes, template, node_first=False)
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false"}))
    _adoption_checks(bench, "wa first")
    before = _same_factions(bench, "wa first, at load")
    # The WA publishes its faction snapshot about once a second; give node-1 a few to apply.
    time.sleep(15)
    reconciled = _lines(bench, NODE, r"factions snapshot_reconciled")
    replaced = _lines(bench, NODE, r"factions snapshot replaced_local=true")
    bench.record("reconciled-wa-first", reconciled + replaced)
    bench.check("the WA's faction snapshot removed no faction of node-1's",
                not [line for line in reconciled if not re.search(r"clients_removed=0\b", line)], str(reconciled[:3]))
    bench.check("the WA's faction snapshot replaced no faction node-1 had created", not replaced, str(replaced[:3]))
    after = _same_factions(bench, "wa first, after the snapshots")
    bench.check("the faction ids and founders did not change on node-1 after the snapshots",
                {t: v[NODE] for t, v in before.items()} == {t: v[NODE] for t, v in after.items()}, json.dumps(after)[:300])
    founder = before[FACTION_TAGS[0]][NODE][1]
    owned = bench.cluster.node(NODE, f"/trash-test-spawn?x={FAR}&y=0&z=0&static=false&blocks=8&npc=true&owner={founder}", "POST")
    stranger = bench.cluster.node(NODE, f"/trash-test-spawn?x={FAR + 20_000}&y=0&z=0&static=false&blocks=8&npc=true&owner={UNKNOWN_OWNER}", "POST")
    bench.record("probes", {"founder": owned, "unknown": stranger})
    _require(bench, "setup: the probes are owned by the founder and the unknown identity",
             owned.get("bigOwners") == [founder] and stranger.get("bigOwners") == [UNKNOWN_OWNER], json.dumps([owned, stranger]))
    counted = wait_until(lambda: (lambda r: r if int(owned["grid"]) in r else None)(_grid_rows(bench)), 120,
                         "the founder-owned grid in the NPC census")
    bench.check("the founder-owned grid is an NPC grid in the census (MES's grids are counted)", int(owned["grid"]) in counted)

    # 2. The guard: two walks a minute apart see the unknown owner
    guard = wait_until(lambda: _lines(bench, NODE, r"WARNING: grids_unknown_owner count=[1-9]"), 200, "the unknown-owner guard line")
    bench.record("guard", guard)
    bench.check("the guard reports exactly the grid of the unknown identity",
                re.search(rf"count=1 owners=1 examples={stranger['grid']}:{UNKNOWN_OWNER}\b", guard[0]) is not None, guard[0][-240:])

    # 3. A fresh cluster, node-1 first
    _boot_factions(bench, processes, template, node_first=True)
    waited = _lines(bench, NODE, r"factions waiting_for_authority_save=true")
    bench.record("waited", waited)
    bench.check("node-1 waited for the WA's first global save (none was catalogued)", "saved=none" in (waited or [""])[0], str(waited[:1]))
    _adoption_checks(bench, "node first")
    _same_factions(bench, "node first")
    bench.exercised(factions=before, guard=guard[0][-200:], waited=waited[:1])


METEOR_LIFE = 10000 / 90          # s, vanilla MyMeteor at rest (MAX_TRAJECTORY_LENGTH / SPEED)
METEOR_GRACE = 30                 # s, MeteorLifetime.LoadGraceMilliseconds
METEOR_SLACK = 12                 # s, UpdateBeforeSimulation100 cadence, polling and sim speed
METEOR_HOPS = ((20, "node-2"), (55, "node-1"), (95, "node-2"))   # (age in s, target) of HOP's handovers


def _meteor(bench, node: str, entity: int) -> dict:
    return bench.cluster.node(node, f"/meteor-test-state?entityId={entity}")


def _meteor_gone(bench, node: str, entity: int, deadline: float) -> float | None:
    """When the meteor was first seen gone from ``node``, None if it was still there at ``deadline``."""
    while time.time() < deadline:
        if not _meteor(bench, node, entity).get("exists"):
            return time.time()
        time.sleep(1)
    return None


def p2_meteor_lifetime(bench) -> None:
    from scenarios import _force_handover
    from scenarios_ext import _gate_residuals, _wait_arrival

    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")   # only the scenario's own handovers move a partition
    loaded = _poll(lambda: (lambda n: n if n and len(_lines(bench, NODE, r"startup partition claimed: ")) >= n else None)(
        len(_lines(bench, NODE, r"assigned save loaded: "))), 120)
    _require(bench, "setup: node-1 claimed every save it loaded at startup", loaded)
    selftest = bench.cluster.node(NODE, "/self-test?section=meteor-lifetime")
    bench.check("node self-test meteor-lifetime (vanilla lifetime by speed, carried age, load grace, builder entry)",
                selftest.get("ok") is True and selftest.get("ran") == ["meteor-lifetime"], json.dumps(selftest)[:300])
    bench.record("config-setup", bench.cluster.handover_config({"npc.sweepSeconds": "0", "offline.enabled": "false"}))

    spawned, probes = {}, {}
    for name, x in (("HOP", FAR), ("STAY", FAR + 50_000)):
        probes[name] = bench.cluster.node(NODE, f"/meteor-test-spawn?x={x}&y=0&z=0&speed=0", "POST")
        spawned[name] = time.time()
    bench.record("probes", probes)
    _require(bench, "setup: both probes are meteors", all(p.get("meteor") and p.get("entityId") for p in probes.values()), json.dumps(probes))
    ids = {name: int(p["entityId"]) for name, p in probes.items()}
    births = {name: _meteor(bench, NODE, entity).get("birth") for name, entity in ids.items()}
    bench.record("births", births)
    homes = {name: _home(bench, entity) for name, entity in ids.items()}
    _require(bench, "setup: each meteor alone in its own partition", len(set(homes.values())) == 2, json.dumps(homes))

    hop, holder, loaded_at, hops = homes["HOP"], NODE, None, []
    for age, target in METEOR_HOPS:
        wait = spawned["HOP"] + age - time.time()
        if wait > 0:
            time.sleep(wait)
        _force_handover(bench, holder, target, hop)
        _wait_arrival(bench, hop, target, 60)
        loaded_at, holder = time.time(), target
        state = _meteor(bench, holder, ids["HOP"])
        hops.append({"target": target, "ageAtLoad": round(loaded_at - spawned["HOP"], 1), "residuals": _gate_residuals(bench, target, hop),
                     "state": state})
        bench.record("hops", hops)
    bench.check("HOP: every handover passed the round-trip gate with no residual",
                all(h["residuals"] == 0 for h in hops), json.dumps(hops))
    bench.check("HOP is there after every load, with the birth it was spawned with",
                all(h["state"].get("exists") is True and h["state"].get("birth") == births["HOP"] is not None for h in hops), json.dumps(hops))

    due = {"HOP": max(spawned["HOP"] + METEOR_LIFE, loaded_at + METEOR_GRACE), "STAY": spawned["STAY"] + METEOR_LIFE}
    where = {"HOP": holder, "STAY": NODE}
    gone = {}
    for name in sorted(due, key=due.get):
        gone[name] = _meteor_gone(bench, where[name], ids[name], due[name] + METEOR_SLACK)
    bench.record("gone", {name: {"sinceSpawn": gone[name] and round(gone[name] - spawned[name], 1),
                                 "due": round(due[name] - spawned[name], 1)} for name in gone})
    for name, label in (("STAY", f"STAY (never moved, the control) closes {METEOR_LIFE:.0f} s after its spawn"),
                        ("HOP", f"HOP closes at max(spawn + {METEOR_LIFE:.0f} s, last load + {METEOR_GRACE} s), not {METEOR_LIFE:.0f} s after its last load")):
        bench.check(label + f" (+-{METEOR_SLACK} s)", gone[name] is not None and gone[name] >= due[name] - METEOR_SLACK,
                    f"gone {gone[name] - spawned[name]:.0f} s after spawn, due {due[name] - spawned[name]:.0f} s" if gone[name]
                    else f"still there {due[name] + METEOR_SLACK - spawned[name]:.0f} s after spawn")
    bench.exercised(meteors=ids, homes=homes, hops=len(hops))


p2_meteor_lifetime.regular_nodes = 2

SCENARIOS_NPC = {
    "P2-NPC-FACTIONS": (p2_npc_factions, "#1365: a node takes the mods' NPC factions from the WA at load and founds none itself; grids of unknown owners are reported", "3"),
    "P2-NPC-CENSUS": (p2_npc_census, "#1354: the NPC census counts offline partitions, and the sweep removes unvisited and gap-band NPC grids", "3"),
    "P2-GRID-CLEANUP": (p2_grid_cleanup, "#1379: the cleanup policy keeps OCT, player and static ownerless grids and removes NPC and ownerless grids on their own timers", "3"),
    "P2-METEOR-LIFETIME": (p2_meteor_lifetime, "#1381: a meteor keeps its vanilla lifetime across handovers instead of starting it over at every load", "3"),
    "P2-WRECK-CLEANUP": (p2_wreck_cleanup, "#1390: small cheap wreckage goes away from players, near them under low sim speed, and before its partition is saved offline; working player grids never", "3"),
}
