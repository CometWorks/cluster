"""SE-810: !ess grids list on a cluster, the plugin's own in place of Essentials', and its alias !grids list: grids on two nodes and in offline partitions.

    python Test/grid_list_scenario.py ESSENTIALS_REPO     (with Test/'s virtualenv python, its host.env done)

ESSENTIALS_REPO is a CometWorks/essentials checkout, built here against MAGNETAR_HOME. WA + 2 regular nodes + one client
without rights, on the package's example world, MarkerShowPosition on. The grids are spawned on the node they should
live on. The last part takes far grids offline, one row as an older node would leave it (read back from its Final), one
with the grids in its offer, and wakes one. The end hangs a node for one list.
"""

from __future__ import annotations

import json
import math
import os
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

REPO = Path(sys.argv[1]).resolve()
TEST_DIR = Path(__file__).resolve().parent
sys.path[:0] = [str(TEST_DIR), str(TEST_DIR.parent / "Cli")]

import bench as bench_module  # noqa: E402  (loads host.env, sets MAGNETAR_HOME)
from cluster import ClusterError, wait_until  # noqa: E402
from scenarios_offline import _put_config  # noqa: E402
from scenarios_core import (  # noqa: E402
    CLIENT_ID,
    _client_partition,
    _handover_url,
    _partitions,
)

# Beside the test root (a scenario clears its run directory), so a second checkout with a host.env of its own runs alongside
_TEST_ROOT = bench_module.HOST.test_root
ROOT = _TEST_ROOT.with_name(_TEST_ROOT.name + "-essentials-grids")
PLUGINS = ROOT / "plugins"
BLUEPRINT = ROOT / "blueprint.sbc"
PLUGIN_ID = "DF09925B-6D53-41F2-899C-CA6214B5DD6F"
NODES = ["node-1", "node-2"]
PROCESSES = ["wa-1", *NODES]
PREFIX = "Ess810"
NEAR = PREFIX + "Near"
FAR = PREFIX + "Far"
OLD = PREFIX + "Old"
NEW = PREFIX + "New"
LAST = PREFIX + "Last"


def build() -> None:
    """Essentials as a local plugin (DLL + manifest) for CLUSTER_EXTRA_PLUGINS"""
    out = PLUGINS / ".build"
    subprocess.run(
        ["nice", "dotnet", "build", str(REPO / "ServerPlugin/ServerPlugin.csproj"), "-c", "Release",
         "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = PLUGINS / "Essentials"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "Essentials.dll").write_bytes((out / "Essentials.dll").read_bytes())
    (folder / "Essentials.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{PLUGIN_ID}</Id><FriendlyName>Essentials</FriendlyName><RepoId>CometWorks/essentials</RepoId>"
        "<Commit>0000000000000000000000000000000000000000</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    # One battery: a grid has an owner only through a block that can have one
    BLUEPRINT.write_text(
        '<?xml version="1.0"?>\n<Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Ess810" />'
        "<CubeGrids><CubeGrid><SubtypeName /><EntityId>0</EntityId>"
        "<PersistentFlags>InScene</PersistentFlags><GridSizeEnum>Large</GridSizeEnum><CubeBlocks>"
        '<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_BatteryBlock">'
        "<SubtypeName>LargeBlockBatteryBlock</SubtypeName></MyObjectBuilder_CubeBlock>"
        "</CubeBlocks><DisplayName>Ess810</DisplayName></CubeGrid></CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>\n",
        encoding="utf-8",
    )


def run(bench) -> None:
    cluster = bench.cluster
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        data = bench.run_root / f"{process}-data"
        data.mkdir(parents=True, exist_ok=True)
        (data / "Essentials.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
            'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
            "<MarkerShowPosition>true</MarkerShowPosition></PluginConfig>\n",
            encoding="utf-8",
        )
    cluster.start(PROCESSES)
    cluster.wait_ready(nodes=PROCESSES)
    # Off until the last part: the far grids would go offline at once
    for node in NODES:
        _put_config(bench, node, {"offline.enabled": "false"})
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)

    def client_node() -> str | None:
        for node in NODES:
            try:
                if _client_partition(_partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass
        return None

    home = wait_until(client_node, 120, "the client's partition")
    other = NODES[1 - NODES.index(home)]
    player = cluster.node(home, f"/respawn-test-player?userId={CLIENT_ID}")
    identity = int(player["identityId"])
    here = tuple(api.get_character()["position"])

    def spawn(
        node: str, name: str, x_offset: float, static: str = "true"
    ) -> tuple[int, tuple]:
        position = (here[0] + x_offset, here[1], here[2])
        reply = cluster.node(
            node,
            f"/blueprint-test-spawn?path={quote(str(BLUEPRINT))}&x={position[0]}&y={position[1]}"
            f"&z={position[2]}&owner={identity}&static={static}&name={name}",
            "POST",
        )
        grid = reply["grids"][0]
        wait_until(
            lambda: cluster.node(node, f"/partition-of?entity={grid}").get("partition"),
            60,
            f"{name} in a partition of {node}",
        )
        return grid, position

    def hud() -> set:
        return {
            (m.get("sender"), m.get("text"), m.get("timestamp"))
            for m in api.get_chat_messages().get("hud") or []
        }

    def grids_list(command: str = "!ess grids list") -> str:
        before = hud()
        api.send_chat(command)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            found = [
                t
                for s, t, _ in hud() - before
                if s in ("ess", "Essentials", "grids", "Grids")
            ]
            if found:
                return "\n".join(found)
            time.sleep(0.5)
        raise ClusterError(f"No reply to {command}, new on the HUD: {hud() - before}")

    def client_markers() -> dict[str, list[tuple]]:
        markers: dict[str, list[tuple]] = {}
        for entry in api.list_gps().get("entries", []):
            if (entry.get("name") or "").startswith(PREFIX):
                markers.setdefault(entry["name"], []).append(
                    (entry["x"], entry["y"], entry["z"])
                )
        return markers

    def tables_agree(expected: list[str]) -> bool:
        """The same markers of the player on the WA and on every node"""
        tables = [
            sorted(
                e["name"]
                for e in cluster.node(process, f"/gps-test-table?prefix={PREFIX}")[
                    "entries"
                ]
                if e["identity"] == identity
            )
            for process in PROCESSES
        ]
        return all(table == sorted(expected) for table in tables)

    def settle(expected: dict[str, tuple]) -> tuple[dict, bool]:
        """The markers on the client and in the tables once they show what is expected, or after 30 s"""
        deadline = time.monotonic() + 30
        while True:
            markers = client_markers()
            agree = tables_agree(list(expected))
            matches = set(markers) == set(expected) and all(
                len(markers[name]) == 1 and math.dist(markers[name][0], position) < 1
                for name, position in expected.items()
            )
            if (matches and agree) or time.monotonic() > deadline:
                return markers, matches and agree
            time.sleep(1)

    _near, near_position = spawn(home, NEAR, 200)
    far_grid, far_position = spawn(other, FAR, 50_000)

    before = hud()
    api.send_chat("!ess home list")
    wait_until(
        lambda: [t for s, t, _ in hud() - before if s in ("ess", "Essentials")],
        30,
        "a reply to !ess home list: the rest of !ess stays Essentials'",
    )
    log = (bench.run_root / f"{home}.log").read_text(errors="replace")
    bench.check("the plugin took the command over", "!ess grids list taken over" in log)
    reply = grids_list()
    bench.check(
        f"the client on {home} lists its grids on both nodes",
        NEAR in reply and FAR in reply,
        reply,
    )
    expected = {NEAR: near_position, FAR: far_position}
    markers, ok = settle(expected)
    bench.check(
        "one marker per grid at its position, on the client and every node",
        ok,
        str(markers),
    )

    alias = grids_list("!grids list")
    markers, ok = settle(expected)
    bench.check(
        "!grids list says the same for a player without rights and adds no marker",
        "!grids list added" in log and alias == reply and ok,
        alias,
    )

    partition = _client_partition(_partitions(bench, home))
    bench.record(
        "handover",
        cluster.post(
            _handover_url(bench, partition, source=home, target=other), timeout=300
        ),
    )
    wait_until(lambda: client_node() == other, 120, f"the client on {other}")
    markers, ok = settle(expected)
    bench.check(f"the markers followed the client to {other}", ok, str(markers))

    reply = grids_list()
    markers, ok = settle(expected)
    bench.check(
        "listing again from the other node adds nothing",
        ok and FAR in reply,
        str(markers),
    )

    # The far grid is gone and another of that name stands elsewhere, now on the first node
    cluster.node(other, f"/contract-test-delete-grid?grid={far_grid}", "POST")
    _moved, moved_position = spawn(home, FAR, 60_000)
    reply = grids_list()
    expected[FAR] = moved_position
    markers, ok = settle(expected)
    bench.check(
        "the marker of a moved grid moves with it",
        ok and FAR in reply,
        f"{markers}\n{reply}",
    )

    # A partition that went offline without its grids in the offer, as from an older node
    # A PUT replaces all that an earlier one set
    settings = {"offline.enabled": "false"}

    def configure(**values) -> None:
        settings.update({k.replace("_", "."): v for k, v in values.items()})
        for node in NODES:
            _put_config(bench, node, settings)

    def offline_grid(grid: int) -> dict | None:
        answer = cluster.get(f"/registry/partitions/offline/grids?owner={identity}")
        return next((r for r in answer["grids"] if r["grid"]["id"] == grid), None)

    def wait_offline(grid: int, what: str) -> dict:
        return wait_until(lambda: offline_grid(grid), 240, what)

    configure(offline_offerGrids="false")
    old_grid, old_position = spawn(other, OLD, 80_000)
    configure(
        offline_enabled="true", offline_minOnlineSeconds="0", offline_graceSeconds="0"
    )
    row = wait_offline(old_grid, f"{OLD} read from its Final")
    bench.check(
        "a row without grids is read from its Final, what it can't tell unknown",
        row["grid"]["o"] == [identity]
        and "p" not in row["grid"]
        and "f" not in row["grid"],
        str(row),
    )
    logs = "".join(
        p.read_text(errors="replace")
        for node in NODES
        for p in (bench.run_root / f"{node}-data").glob("SpaceEngineersDedicated_*.log")
    )
    bench.check(
        "the backfill read it",
        f"offline grids of partition {row['partition']} read" in logs,
    )

    configure(offline_offerGrids="true")
    new_grid, new_position = spawn(other, NEW, 90_000, static="false")
    row = wait_offline(new_grid, f"{NEW} from the offline offer")
    bench.check(
        "an offer carries the grids and their facts",
        row["grid"]["n"] == NEW
        and row["grid"]["o"] == [identity]
        and row["grid"]["g"] == new_grid
        and row["grid"]["m"] > 0
        and row["grid"]["f"] is True
        and row["grid"]["t"] is False
        and "p" in row["grid"],
        str(row),
    )

    reply = grids_list()
    bench.check(
        "the list has the grids in offline partitions, marked",
        f"{OLD} - Large - 1 block(s) - Position Unknown - Offline" in reply
        and f"{NEW} - Large - 1 block(s) - Position Unknown - Offline" in reply
        and f"{NEAR} - Large - 1 block(s) - Position Unknown\n" in reply + "\n",
        reply,
    )
    expected.update({OLD: old_position, NEW: new_position})
    markers, ok = settle(expected)
    bench.check("a marker at each offline grid", ok, str(markers))
    alias = grids_list("!grids list")
    bench.check("!grids list has the offline grids too", alias == reply, alias)

    # Woken: a node has it, and it is listed once
    configure(offline_enabled="false")
    partition = offline_grid(new_grid)["partition"]
    cluster.post(
        f"/registry/partitions/offline/load?partition={partition}&node={home}&reason=test"
    )
    # On any node: the gateway can aim the load elsewhere
    wait_until(
        lambda: any(
            cluster.node(node, f"/partition-of?entity={new_grid}").get("partition")
            for node in NODES
        ),
        120,
        f"{NEW} loaded",
    )
    reply = grids_list()
    bench.check(
        "a woken grid is listed once, not as offline",
        reply.count(NEW) == 1
        and f"{NEW} - Large - 1 block(s) - Position Unknown - Offline" not in reply,
        reply,
    )

    # A node that doesn't answer: the list says so and leaves the marker of the grid it can't see
    _last, last_position = spawn(home, LAST, 110_000)
    grids_list()
    expected[LAST] = last_position
    markers, ok = settle(expected)
    bench.check("a marker on the node about to hang", ok, str(markers))
    pid = cluster._node_pid(home)
    os.kill(pid, signal.SIGSTOP)
    try:
        reply = grids_list()
        markers = client_markers()
    finally:
        os.kill(pid, signal.SIGCONT)
    bench.check(
        "a hung node: the list says so and the marker stays",
        "did not answer" in reply and LAST not in reply and LAST in markers,
        f"{markers}\n{reply}",
    )
    # The node catches up within seconds, and the list is whole again
    def whole_list() -> str | None:
        answer = grids_list()
        return None if "did not answer" in answer else answer

    reply = wait_until(whole_list, 60, "a list with every node answering")
    bench.check("the node back: its grid is listed again", LAST in reply, reply)


run.regular_nodes = 2


def main() -> int:
    ROOT.mkdir(parents=True, exist_ok=True)
    build()
    os.environ["CLUSTER_EXTRA_PLUGINS"] = str(PLUGINS / "Essentials")
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-essentials-grids")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-ESS-GRIDS",
        "SE-810: !ess grids list and its GPS markers on a cluster",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
