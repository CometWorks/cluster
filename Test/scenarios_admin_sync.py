"""SE-1596: admin changes made on one node reach every node within a few seconds and outlive restarts.

* P2-ADMIN-SYNC - the Alt-F10 Trash Removal upload on node-1 (the server side of
  MyGuiScreenAdminMenu.UploadSettingsToServer) reaches node-2 and the World Authority; node-2 restarted on its
  world file and a replacement World Authority on wa-1's global save both keep it. A weather set on node-1 through
  the weather API (as a plugin or mod makes it) reaches the WA and node-2 and stays on node-1; an admin client's
  `/weather clear` on node-2 (its weather list sent up through UpdateWeathersOnClients) clears it everywhere.

Red on a package from before the fix: the upload and the weather set stay on node-1 (the weather only for the
moment until the WA's next state), and the clear stays on node-2.
"""

from __future__ import annotations

import json
import time
import urllib.parse

from cluster import ClusterError
from scenarios_blocks import _replace_authority
from scenarios_planet import _node, planet_template
from scenarios_wa_ops import _poll, _require

# "Within a few seconds": the WA publishes weather every 2 s and a trash set at once.
SYNC_SECONDS = 10


def _trash(bench, node: str) -> dict:
    return _node(bench, node, "/admin-sync-trash")


def _weathers(bench, node: str) -> list[tuple[str, int]]:
    status = _node(bench, node, "/weather")
    return [(w.get("weather"), w.get("maxLife")) for p in status.get("planets") or [] for w in p.get("weathers") or []]


def p2_admin_sync(bench) -> None:
    planet_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    roles = ["node-1", "node-2", "wa-1"]

    # -- Trash Removal settings ------------------------------------------------------------------------------
    before = {node: _trash(bench, node) for node in roles}
    bench.record("trash-before", before)
    wanted = {
        "TrashRemovalEnabled": not before["node-1"]["TrashRemovalEnabled"],
        "BlockCountThreshold": before["node-1"]["BlockCountThreshold"] + 7,
        "VoxelAgeThreshold": before["node-1"]["VoxelAgeThreshold"] + 3,
    }
    query = urllib.parse.urlencode(
        {
            "blockCount": wanted["BlockCountThreshold"],
            "voxelAge": wanted["VoxelAgeThreshold"],
            "enable": str(wanted["TrashRemovalEnabled"]).lower(),
        }
    )
    began = time.monotonic()
    bench.record("trash-upload", _node(bench, "node-1", f"/admin-sync-trash-upload?{query}", "POST"))
    seen: dict[str, dict] = {}

    def everywhere(nodes: list[str]) -> bool:
        try:
            seen.update({node: _trash(bench, node) for node in nodes})
        except (ClusterError, OSError):  # a node still starting refuses or resets the connection
            return False
        return all(all(seen[node][key] == value for key, value in wanted.items()) for node in nodes)

    synced = _poll(lambda: everywhere(roles) or None, SYNC_SECONDS, 0.5)
    took = time.monotonic() - began
    bench.record("trash-after-upload", {"rows": seen, "seconds": took})
    bench.check(
        "an Alt-F10 trash change on node-1 reaches node-2 and the WA within a few seconds",
        bool(synced),
        f"{took:.1f} s, wanted {wanted}: {json.dumps(seen)}",
    )

    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", 600)
    restarted = _poll(lambda: everywhere(["node-2"]) or None, 60, 1)
    bench.record("trash-after-node-restart", seen.get("node-2"))
    bench.check(
        "node-2 restarted on its world file keeps the cluster's trash settings",
        bool(restarted),
        json.dumps(seen.get("node-2")),
    )

    # -- weather set on a node through the weather API ---------------------------------------------------------
    definitions = [d for d in _node(bench, "wa-1", "/weather").get("definitions") or [] if d != "Clear"]
    _require(bench, "a public non-Clear weather definition is available", definitions)
    weather, length = definitions[0], 600
    mark = (weather, length * 60)  # MaxLife in frames
    began = time.monotonic()
    bench.record(
        "weather-set",
        _node(
            bench,
            "node-1",
            f"/admin-sync-weather-set?weather=" f"{urllib.parse.quote(weather, safe='')}&length={length}",
            "POST",
        ),
    )
    views: dict[str, list] = {}

    def has(nodes: list[str], present: bool) -> bool:
        try:
            views.update({node: _weathers(bench, node) for node in nodes})
        except (ClusterError, OSError):
            return False
        return all((mark in views[node]) == present for node in nodes)

    reached = _poll(lambda: has(["wa-1", "node-2"], True) or None, SYNC_SECONDS, 0.5)
    took = time.monotonic() - began
    bench.record("weather-after-set", {"views": views, "seconds": took})
    bench.check(
        "a weather set on node-1 reaches the WA and node-2 within a few seconds",
        bool(reached),
        f"{took:.1f} s, {mark}: {json.dumps(views)[:600]}",
    )
    time.sleep(6)
    bench.check(
        "the weather set on node-1 stays there (the WA's next states keep it)",
        has(["node-1", "node-2", "wa-1"], True),
        json.dumps(views)[:600],
    )

    # -- an admin client's `/weather clear` on node-2 ----------------------------------------------------------
    began = time.monotonic()
    bench.record("weather-client-clear", _node(bench, "node-2", "/admin-sync-weather-client-clear", "POST"))
    cleared = _poll(lambda: has(["wa-1", "node-1", "node-2"], False) or None, SYNC_SECONDS, 0.5)
    took = time.monotonic() - began
    bench.record("weather-after-clear", {"views": views, "seconds": took})
    bench.check(
        "an admin client's /weather clear on node-2 clears the weather on every role within a few seconds",
        bool(cleared),
        f"{took:.1f} s, {mark}: {json.dumps(views)[:600]}",
    )

    # -- a replacement World Authority restores the trash settings from wa-1's global save ---------------------
    _replace_authority(bench)
    restored = _poll(lambda: everywhere(["wa-2", "node-1", "node-2"]) or None, 120, 2)
    bench.record("trash-after-wa-replacement", seen)
    bench.check(
        "a replacement World Authority keeps the trash settings, and so do the nodes", bool(restored), json.dumps(seen)
    )
    bench.exercised(trash=wanted, weather=weather)


SCENARIOS_ADMIN_SYNC = {
    "P2-ADMIN-SYNC": (
        p2_admin_sync,
        "Alt-F10 trash settings and admin weather changes reach every node (SE-1596)",
        "3",
    ),
}
