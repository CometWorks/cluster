"""SE-1596: the Alt-F10 admin menu's Entity List and PCU tab for the whole cluster.

The vanilla handlers read the node the admin is attached to: a grid on another node or in an offline partition was not
listed, and a row action on it did nothing. The cluster's patched handlers ask the other nodes over the plugin bus and the
Gateway for the offline grids. No client: the test plugin's ``/admin-menu-*`` routes run on the admin's node what the
patched event handlers run (``AdminMenuService``), and say what the client would get.

WA + node-1 + node-2, offline partitions off until the offline leg.

* ``P2-ADMIN-MENU`` - the node self-test section ``admin-menu`` (paging, merge, PCU list, chat lines); an NPC probe grid
  on node-2 is in node-1's Grids list tagged ``[node-2]``, with its position for the camera; the PCU tab on node-1 lists
  it for its owner and its PCU counts in the totals; Stop and Remove from node-1 run on node-2 and the grid is gone there,
  while a node-1 grid in the same Remove stays with vanilla; an offline grid is listed ``[offline]`` and its Remove is
  skipped with a note; then the cost with many grids: ``ADMIN_MENU_COUNTS`` (default 1000,4000) probes on node-2 listed
  from node-1, recording time, pages, bus bytes and the size of the client event.
"""

from __future__ import annotations

import json
import math
import os
import time

from cluster import ClusterError, wait_until
from scenarios_ext import node_self_test
from scenarios_offline import _put_config
from scenarios_suite import _plugin_services_ready

NODES = ["node-1", "node-2"]
PROCESSES = ["wa-1", *NODES]
FAR = 600_000.0
#: Over the world's BlockCountThreshold (20), so the trash sweep keeps the probes.
BLOCKS = 21
COUNTS = [
    int(n) for n in os.environ.get("ADMIN_MENU_COUNTS", "1000,4000").split(",") if n
]
BATCH = 200


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), str(detail)[:600]):
        raise ClusterError(f"{label}: {str(detail)[:600]}")


def _residence(bench, grid: int) -> tuple[str | None, str | None]:
    """(node, residence) of the node that holds the grid as its own."""
    for node in NODES:
        placement = (
            bench.cluster.node(node, f"/plugin-views?entity={grid}").get("placement")
            or {}
        )
        if placement.get("residence") == "Local":
            return node, "Local"
    return None, None


def _spawn(bench, node: str, x: float, npc: bool = True) -> dict:
    reply = bench.cluster.node(
        node,
        f"/trash-test-spawn?x={x}&y=0&z=0&blocks={BLOCKS}&static=true&npc={str(npc).lower()}",
        "POST",
    )
    grid = int(reply["grid"])
    wait_until(
        lambda: _residence(bench, grid)[0], 120, f"{reply['name']} owned by a node"
    )
    return reply


def _list(bench, node: str, match: str, kind: str = "Grids") -> dict:
    return bench.cluster.node(node, f"/admin-menu-list?type={kind}&match={match}")


def _gone(bench, node: str, grid: int) -> bool:
    placement = (
        bench.cluster.node(node, f"/plugin-views?entity={grid}").get("placement") or {}
    )
    return placement.get("residence") in (None, "Absent")


def p2_admin_menu(bench) -> None:
    bench.start_cluster(nodes=PROCESSES)
    for node in NODES:
        _put_config(bench, node, {"offline.enabled": "false"})
    _plugin_services_ready(bench, PROCESSES)

    selftest = node_self_test(bench, "admin-menu")
    _require(
        bench,
        "node self-test section `admin-menu` passes (paging, merge, PCU list, chat lines)",
        selftest.get("ok") is True and selftest.get("ran") == ["admin-menu"],
        json.dumps(selftest)[:400],
    )

    # One probe on each node; the admin sits on the other one than the remote probe's
    remote = _spawn(bench, "node-2", FAR)
    local = _spawn(bench, "node-1", -FAR)
    grid, mine = int(remote["grid"]), int(local["grid"])
    owner_node, _ = _residence(bench, grid)
    admin = "node-1" if owner_node == "node-2" else "node-2"
    mine_node, _ = _residence(bench, mine)
    bench.record(
        "probes",
        {
            "remote": remote,
            "local": local,
            "remoteOn": owner_node,
            "localOn": mine_node,
            "admin": admin,
        },
    )
    _require(
        bench,
        "setup: the two probes are on different nodes",
        owner_node and mine_node and owner_node != mine_node,
        f"{owner_node} {mine_node}",
    )

    # Entity List
    listed = _list(bench, admin, "cluster-npc-probe")
    bench.record("list-1", listed)
    rows = {row["id"]: row for row in listed["rows"]}
    row = rows.get(grid)
    _require(
        bench,
        f"the Grids list on {admin} has the probe on {owner_node}, tagged [{owner_node}]",
        row is not None and row["name"].endswith(f" [{owner_node}]"),
        json.dumps(listed)[:600],
    )
    bench.check(
        "its row has the probe's position (the camera jump goes there)",
        row is not None and math.dist((row["x"], row["y"], row["z"]), (FAR, 0, 0)) < 50,
        json.dumps(row),
    )
    bench.check(
        f"the probe on {admin} itself is listed untagged",
        rows.get(mine) is not None and "[" not in rows[mine]["name"],
        json.dumps(rows.get(mine)),
    )
    bench.check(
        "every node answered",
        not listed["stats"]["Missing"] and listed["incomplete"] is None,
        json.dumps(listed["stats"]),
    )
    known = bench.cluster.node(admin, f"/admin-menu-known?id={grid}")
    bench.check(
        "the PCU tab's camera jump knows where the remote grid is",
        known.get("found") and known.get("node") == owner_node,
        json.dumps(known),
    )

    # PCU tab
    owner = int(remote["owner"])
    grids = bench.cluster.node(admin, f"/admin-menu-grids?identity={owner}")
    bench.record("pcu-grids", grids)
    named = dict(zip(grids["Ids"], grids["Names"]))
    _require(
        bench,
        f"the PCU tab on {admin} lists the owner's grid on {owner_node} with its PCU",
        grid in named
        and named[grid].startswith("(")
        and named[grid].endswith(f"{remote['name']} [{owner_node}]"),
        json.dumps(grids)[:600],
    )
    bench.check(
        "the owner's total PCU counts the remote grid",
        grids["TotalPcu"] >= int(named[grid][1 : named[grid].index(")")]),
        json.dumps(grids)[:300],
    )
    pcu = bench.cluster.node(admin, "/admin-menu-pcu")
    bench.record("pcu-totals", pcu)
    bench.check(
        "the PCU totals add the other node's usage",
        not pcu["missing"] and pcu["sums"][0] > pcu["local"][0],
        json.dumps(pcu),
    )

    # Row actions
    stop = bench.cluster.node(
        admin, f"/admin-menu-action?ids={grid}&action=Stop", "POST"
    )
    bench.record("action-stop", stop)
    bench.check(
        f"Stop on the remote grid is forwarded and done on {owner_node}",
        stop["remote"] == [grid] and f"done, 1 on {owner_node}" in (stop["text"] or ""),
        json.dumps(stop),
    )
    remove = bench.cluster.node(
        admin, f"/admin-menu-action?ids={grid},{mine}&action=Remove", "POST"
    )
    bench.record("action-remove", remove)
    _require(
        bench,
        f"Remove keeps the {admin} grid for vanilla and forwards the other",
        remove["local"] == [mine]
        and remove["remote"] == [grid]
        and f"done, 1 on {owner_node}" in (remove["text"] or ""),
        json.dumps(remove),
    )
    try:
        wait_until(
            lambda: _gone(bench, owner_node, grid) or None, 30, "the removed grid gone"
        )
        gone = True
    except ClusterError:
        gone = False
    bench.check(f"the grid is gone on {owner_node}", gone)
    again = bench.cluster.node(
        admin, f"/admin-menu-action?ids={grid}&action=Remove", "POST"
    )
    bench.check(
        "a second Remove finds nothing and says so",
        "1 not found on any server." in (again["text"] or ""),
        json.dumps(again),
    )
    log = (bench.run_root / f"{owner_node}.log").read_text(errors="replace")
    bench.check(
        f"{owner_node} logs the forwarded Remove",
        f"admin Remove asked by {admin} done on {grid}" in log,
    )

    # Offline grids
    sleeper = _spawn(bench, owner_node, FAR + 80_000)
    sleeper_grid = int(sleeper["grid"])
    for node in NODES:
        _put_config(
            bench,
            node,
            {
                "offline.enabled": "true",
                "offline.minOnlineSeconds": "0",
                "offline.graceSeconds": "0",
            },
        )

    def offline_row():
        answer = bench.cluster.get("/registry/partitions/offline/grids")
        return next(
            (r for r in answer["grids"] if r["grid"]["id"] == sleeper_grid), None
        )

    try:
        wait_until(offline_row, 240, "the probe in an offline partition")
        asleep = True
    except ClusterError:
        asleep = False
    _require(bench, "setup: a probe went offline", asleep)
    listed = _list(bench, admin, "cluster-npc-probe")
    bench.record("list-offline", listed)
    rows = {row["id"]: row for row in listed["rows"]}
    bench.check(
        "the offline grid is listed, tagged [offline]",
        sleeper_grid in rows and rows[sleeper_grid]["name"].endswith(" [offline]"),
        json.dumps(listed)[:600],
    )
    skipped = bench.cluster.node(
        admin, f"/admin-menu-action?ids={sleeper_grid}&action=Remove", "POST"
    )
    bench.record("action-offline", skipped)
    bench.check(
        "Remove on an offline grid is skipped with a note",
        "1 in an offline area, skipped." in (skipped["text"] or ""),
        json.dumps(skipped),
    )
    for node in NODES:
        _put_config(bench, node, {"offline.enabled": "false"})

    # Cost with many rows
    cost = []
    spawned = 0
    for count in COUNTS:
        while spawned < count:
            n = min(BATCH, count - spawned)
            # A cube lattice per batch, far from everything else; 12 m apart, so each probe stays its own grid
            bench.cluster.node(
                "node-2",
                f"/admin-menu-spawn?count={n}&x={-FAR - spawned * 2}&y={FAR}&z=0&spacing=12"
                f"&blocks={BLOCKS}",
                "POST",
                timeout=120,
            )
            spawned += n
        time.sleep(10)
        runs = []
        for _ in range(3):
            listed = _list(bench, "node-1", "~none~")
            runs.append(
                {
                    "stats": listed["stats"],
                    "clientBytes": listed["clientBytes"],
                    "localMs": listed["localMs"],
                }
            )
        cost.append({"probes": count, "runs": runs})
        bench.record("cost", cost)
        last = runs[-1]
        bench.check(
            f"{count} probes on node-2: node-1's list has them all, every node answered",
            last["stats"]["Remote"] >= count and not last["stats"]["Missing"],
            json.dumps(last),
        )
    bench.exercised(remoteOn=owner_node, admin=admin, cost=cost)


SCENARIOS_ADMIN_MENU = {
    "P2-ADMIN-MENU": (
        p2_admin_menu,
        "SE-1596: the Alt-F10 Entity List and PCU tab list and act on the whole cluster",
        "2",
    ),
}
