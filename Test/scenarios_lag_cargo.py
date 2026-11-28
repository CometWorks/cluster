"""SE-2010: cargo containers listed empty to a client far from the server after its partition moved node.

``P3-HND-LAG-CARGO`` - one client with a round trip of ``SE2010_RTT`` ms (SE Dev Tools on the client, half on send
and half on receive, ``SE2010_SIGMA`` ms jitter) beside filled cargo containers, its partition handed over between
two nodes ``SE2010_HANDOVERS`` times. Reported live on 2026-11-28 at ~180 ms: containers that hold items showed
empty until a relog or an Essentials ``!fixship`` (which re-pastes the grid, so the client gets it anew).

Each round spawns a fresh fixture grid next to the client and fills its cargo container, then hands the partition
over ``SE2010_DELAYS`` seconds later (one per round, cycled). At this latency the client's acknowledgements of the
grid's blocks and inventories can still be on their way at the export, so the source exports them as pending and the
target re-delivers them. The latency alone hit that window in none of six rounds, so the source holds the client's
acknowledgements of the fixture's blocks and inventories until the handover (the test plugin's
``/lag-cargo-test-hold-acks``; ``SE2010_HOLD_GRID=1`` holds the grid's own too). A pending inventory is not synced,
so the client keeps what the grid's stream gave it, often fewer items or none. After every handover the client must
list each fixture's container as the node has it; a build without the fix leaves it short or empty for good.

Knobs: SE2010_RTT (180), SE2010_SIGMA (35), SE2010_HANDOVERS (6), SE2010_DELAYS ("0.3,0.6,1.0,1.5"),
SE2010_HOLD_ACKS (1), SE2010_HOLD_GRID (0), SE2010_DEVTOOLS (the se-dev-tools checkout the client compiles;
~/ws/se/se1/plugins/se-dev-tools).
"""

from __future__ import annotations

import json
import math
import os
import re
import time
from pathlib import Path

import clients as _clients
from scenarios_core import CLIENT_ID
from scenarios_redeliver import _poll, _require, _try, _xyz
from scenarios_stale_view import _add_ore, _amounts, _client_items, _client_node, _handover, _server

RTT = float(os.environ.get("SE2010_RTT", "180"))
SIGMA = float(os.environ.get("SE2010_SIGMA", "35"))
HANDOVERS = int(os.environ.get("SE2010_HANDOVERS", "6"))
DELAYS = [float(d) for d in os.environ.get("SE2010_DELAYS", "0.3,0.6,1.0,1.5").split(",")]
DEVTOOLS = os.environ.get("SE2010_DEVTOOLS", str(Path.home() / "ws/se/se1/plugins/se-dev-tools"))
DEVTOOLS_ID = "viktor-ferenczi/se-dev-tools"
HOLD_ACKS = os.environ.get("SE2010_HOLD_ACKS", "1") == "1"
CHILDREN = "false" if os.environ.get("SE2010_HOLD_GRID") == "1" else "true"
LOAD = (("Iron", 300), ("Nickel", 120), ("Silicon", 80))

DEVTOOLS_CFG = """<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Enabled>true</Enabled>
  <EmulateNetwork>{on}</EmulateNetwork>
  <SendLatencyMs>{half}</SendLatencyMs>
  <SendJitterMs>{jitter}</SendJitterMs>
  <ReceiveLatencyMs>{half}</ReceiveLatencyMs>
  <ReceiveJitterMs>{jitter}</ReceiveJitterMs>
</PluginConfig>
"""


def _use_devtools(client) -> None:
    """The client's own Pulsar copy compiles se-dev-tools from DEVTOOLS; the host's profile is never touched."""
    sources = client.pulsar_data / "Sources" / "sources.xml"
    text = sources.read_text()
    text = re.sub(
        r"(<Name>se-dev-tools</Name>\s*<Folder>)[^<]*(</Folder>)", lambda m: m.group(1) + DEVTOOLS + m.group(2), text
    )
    if "<Name>se-dev-tools</Name>" not in text:
        entry = (
            "    <LocalPlugin>\n      <Name>se-dev-tools</Name>\n      <Folder>" + DEVTOOLS + "</Folder>\n"
            "      <File>DevTools.xml</File>\n      <Enabled>true</Enabled>\n    </LocalPlugin>\n"
        )
        text = (
            text.replace("<LocalPluginSources>\n", "<LocalPluginSources>\n" + entry, 1)
            if "<LocalPluginSources>\n" in text
            else text.replace("<LocalPluginSources />", "<LocalPluginSources>\n" + entry + "  </LocalPluginSources>", 1)
        )
    sources.write_text(text)
    for profile in (client.pulsar_data / "Profiles").glob("*.xml"):
        xml = profile.read_text()
        if f"<Id>{DEVTOOLS_ID}</Id>" not in xml:
            dev = (
                f"    <LocalFolderConfig>\n      <Id>{DEVTOOLS_ID}</Id>\n      <DebugBuild>false</DebugBuild>\n"
                "    </LocalFolderConfig>\n"
            )
            xml = (
                xml.replace("<DevFolder>\n", "<DevFolder>\n" + dev, 1)
                if "<DevFolder>\n" in xml
                else xml.replace("<DevFolder />", "<DevFolder>\n" + dev + "  </DevFolder>", 1)
            )
        profile.write_text(xml)
    (client.user_data / "DevTools.cfg").write_text(
        DEVTOOLS_CFG.format(on="true" if RTT > 0 else "false", half=RTT / 2, jitter=round(SIGMA / math.sqrt(2), 1))
    )


def _with_devtools(bench) -> None:
    prepare = _clients.Client.prepare

    def patched(self) -> None:
        prepare(self)
        _use_devtools(self)

    _clients.Client.prepare = patched


def _views(bench, api, fixtures: list[dict]) -> list[dict]:
    """Per fixture: the node's cargo items, the client's, and the node's record of the client's inventory."""
    rows = []
    for fixture in fixtures:
        cargo = fixture["blocks"]["cargo"]
        found = _server(bench, int(cargo["block"]))
        inventory = found[1]["inventories"][0] if found else {}
        rows.append(
            {
                "grid": int(fixture["grid"]),
                "node": found[0] if found else None,
                "server": _amounts(inventory.get("items")),
                "clientData": inventory.get("clientData"),
                "clientHolds": inventory.get("clientHolds"),
                "client": _client_items(api, int(fixture["grid"]), cargo["min"]),
            }
        )
    return rows


def p3_hnd_lag_cargo(bench) -> None:
    """SE-2010: filled cargo containers on a high-latency client across handovers of its partition."""
    _with_devtools(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition moves are the forced handovers
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    for flag in ("creativeTools", "invulnerable"):
        bench.record(f"admin-{flag}", _try(lambda: api.set_admin_flag(flag, True)))
    log = bench.clients[CLIENT_ID].user_data / "SpaceEngineers.log"
    loaded = _poll(lambda: "DevTools: Successfully loaded" in log.read_text(errors="replace") or None, 30, 1)
    _require(bench, "the client loaded SE Dev Tools", loaded, str(log))
    print(f"  [INFO] client round trip emulated: {RTT:.0f} ms, jitter {SIGMA:.0f} ms")

    fixtures: list[dict] = []
    failures = 0
    for index in range(1, HANDOVERS + 1):
        position = _xyz(api.get_character().get("position"))
        _require(bench, "the client reports its position", position)
        source = _client_node(bench)[0]
        x, y, z = position
        fixture = bench.cluster.node(source, f"/stale-view-test-spawn?x={x + 12}&y={y + 2 + 6 * index}&z={z}", "POST")
        block = int(fixture["blocks"]["cargo"]["block"])
        if HOLD_ACKS:
            bench.cluster.node(
                source,
                f"/lag-cargo-test-hold-acks?client={CLIENT_ID}&grid={fixture['grid']}&children={CHILDREN}",
                "POST",
            )
        for subtype, amount in LOAD:
            _add_ore(bench, block, subtype, amount)
        fixtures.append(fixture)
        delay = DELAYS[(index - 1) % len(DELAYS)]
        time.sleep(delay)
        before = _views(bench, api, fixtures)
        bench.record(f"{index:02d}-before", {"delay": delay, "views": before})
        if HOLD_ACKS:
            pending = _poll(
                lambda: (
                    lambda row: (
                        row
                        if row["clientHolds"] and row["clientData"] is not None and row["client"] is not None
                        else None
                    )
                )(_views(bench, api, [fixture])[0]),
                20,
                0.5,
            )
            bench.record(f"{index:02d}-held", pending)
            _require(
                bench,
                f"round {index}: the client has the fixture while the source holds its acknowledgements",
                pending,
                json.dumps(_views(bench, api, [fixture]))[:400],
            )
        _handover(bench, api, int(fixture["grid"]), index)
        if HOLD_ACKS:
            bench.record(
                f"{index:02d}-released",
                bench.cluster.node(
                    source, f"/lag-cargo-test-hold-acks?client={CLIENT_ID}&grid={fixture['grid']}&armed=false", "POST"
                ),
            )
        settled = _poll(
            lambda: (lambda rows: rows if all(row["client"] == row["server"] for row in rows) else None)(
                _views(bench, api, fixtures)
            ),
            30,
            1,
        )
        after = settled or _views(bench, api, fixtures)
        bench.record(f"{index:02d}-after", after)
        wrong = [row for row in after if row["client"] != row["server"]]
        failures += bool(wrong)
        bench.check(
            f"handover {index} (spawned {delay:.1f} s before): the client lists every container as the node has it",
            not wrong,
            json.dumps(wrong)[:900],
        )
    bench.exercised(handovers=HANDOVERS, fixtures=len(fixtures), rtt=RTT, failures=failures)


p3_hnd_lag_cargo.administrators = [CLIENT_ID]

SCENARIOS_LAG_CARGO = {
    "P3-HND-LAG-CARGO": (
        p3_hnd_lag_cargo,
        "filled cargo containers on a 180 ms client across handovers (SE-2010)",
        "3",
    ),
}
