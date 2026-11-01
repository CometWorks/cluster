"""P3-PLR-01: a player's settings survive the node under them going away, and a relog.

A single dedicated server keeps a player's toolbar, build colour palette, armor skin and Alt-F10
admin flags for as long as the world exists - in ``MySession.Static.Toolbars``, on ``MyPlayer``, and
in ``MySession.Static.RemoteAdminSettings`` / ``CreativeTools`` - and writes all of it into
``checkpoint.AllPlayersData``, which is also the checkpoint a joining client initialises itself
from. On the cluster each node has its own session, so all of it used to be node-local: a player
who rejoined on a node that had never hosted them got the scenario DEFAULT toolbar, and a player
handed to another node lost every server-side effect of their admin checkboxes while the client
kept showing them ticked.

The scenario runs at most two regular nodes plus the World Authority at a time and drives one
headless client through the two losses:

    join node-1, mark the toolbar, the palette and the admin flags
      -> node-1 is killed under the connected client, which lands on node-2
      -> the client relogs onto node-3, which has never hosted it

Every leg asserts both halves: what the CLIENT sees, and what the hosting node's own session would
answer a server-side admin check (``/player-profiles``).

Ported from the standalone ``player_settings_lane.py``.
"""

from __future__ import annotations

import json
import re
import os
import time
import urllib.parse
from pathlib import Path

from cluster import ClusterError
from scenarios_core import _lines

CLIENT_ID = 76561199500009001
REMOTE_PORT = 24158 + 43   # the bench maps it onto the host's Remote API port range
RECOVERY_SECONDS = float(os.environ.get("CLUSTER_TEST_PLR_RECOVERY_SECONDS", "60"))

TOOLBAR_MARKS = {
    3: "MyObjectBuilder_PhysicalGunObject/AngleGrinderItem",
    4: "MyObjectBuilder_PhysicalGunObject/WelderItem",
    12: "MyObjectBuilder_CubeBlock/LargeHeavyBlockArmorBlock",
}
COLOR_SLOT = 7
COLOR_MARK = [0.137, 0.75, 0.42]
# The Alt-F10 checkboxes to tick. IgnorePcu is left alone on purpose: MySession.LoadWorld strips it
# (and KeepOriginalOwnershipOnPaste) from a loaded checkpoint unless the platform allows it, so it
# would read as a loss that is vanilla behaviour rather than a cluster defect.
ADMIN_MARKS = ["invulnerable", "useTerminals", "untargetable", "ignoreSafeZones", "creativeTools"]
# ...which is Invulnerable | UseTerminals | Untargetable | IgnoreSafeZones.
ADMIN_MARK_BITS = 0x01 | 0x04 | 0x08 | 0x20


def settings_when_back(api, timeout: float = 300) -> dict:
    """The settings, once the client answers again. Killing the node under a connected client takes
    its session with it for a while (503, no local player): that is a wait, not a result."""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            settings = api.get_player_settings()
            if settings.get("identityId"):
                return settings
            last = "no identity yet"
        except Exception as error:  # noqa: BLE001
            last = str(error)[:160]
        time.sleep(10)
    raise ClusterError(f"the client never came back after the node died: {last}")


def toolbar_slots(settings: dict) -> dict:
    return {int(item["slot"]): item.get("definition") or item.get("type")
            for item in (settings.get("toolbar") or {}).get("items") or []}


def marks_present(settings: dict) -> dict:
    """Every mark this scenario applied, as the CLIENT sees it now."""
    slots = toolbar_slots(settings)
    colors = settings.get("buildColorSlots") or []
    color_ok = len(colors) > COLOR_SLOT and all(
        abs(float(a) - float(b)) < 1e-3 for a, b in zip(colors[COLOR_SLOT], COLOR_MARK))
    admin = set((settings.get("adminSettings") or {}).get("flags") or [])
    return {**{f"toolbar[{slot}]": slots.get(slot) == definition for slot, definition in TOOLBAR_MARKS.items()},
            f"buildColor[{COLOR_SLOT}]": color_ok,
            **{f"admin[{flag}]": (settings.get("creativeToolsEnabled") is True if flag == "creativeTools"
                                  else flag in admin)
               for flag in ADMIN_MARKS}}


def node_admin_state(bench, node_id: str) -> dict:
    """What that node's own session would answer a server-side admin check for our client. The
    client's copy survives anything short of a world reload, so the client alone proves nothing."""
    try:
        status = bench.cluster.node(node_id, "/player-profiles")
    except Exception as error:  # noqa: BLE001
        return {"error": str(error)[:200]}
    row = next((entry for entry in status.get("session") or [] if int(entry.get("client", 0)) == CLIENT_ID), None)
    return {"node": node_id,
            "hosts": any(key.startswith(f"{CLIENT_ID}:") for key in status.get("reconciled") or []),
            "row": row, "profiles": status.get("profiles")}


def admin_state_ok(state: dict) -> bool:
    row = state.get("row")
    return bool(row) and int(row.get("adminSettings", "0x0"), 16) == ADMIN_MARK_BITS \
        and row.get("creativeTools") is True


def _try(function, default=None):
    """A poll probe: an unreachable or erroring endpoint is 'not yet', never a failure."""
    try:
        return function()
    except (ClusterError, OSError, ValueError, KeyError, TypeError, IndexError):
        return default


def wait_for_offsite_copy(bench, doomed: str, timeout: float = 180) -> dict:
    """Wait until every partition ``doomed`` owns has a catalogued save held by some OTHER node.

    Killing a node whose partitions exist in no second copy does not test whether player settings
    survive the loss: the content is simply gone. The registry agrees - its recovery readiness
    reports a single-copy partition as the risk ``partitionSingleCopy``, not as a promise - and the
    recovery path then drops the partition in a way nothing recovers from (T-0198).

    Saves reach a fresh node on the 1 Hz replication sweep, so a node started moments before the
    kill may hold nothing at all yet. sirius, 2026-09-20, same package, four runs: in .4 node-2
    pulled save 3/2/2 at 00:24:16.115, 15 s before node-1's lease expired, and recovery claimed
    partition 3 one second after it - PASS. In .2 and .3 node-2 pulled nothing before node-1 was
    gone, partition 3 was stranded unowned for the rest of the run, and the client could never be
    routed again - FAIL. The scenario was deciding a coin toss, not the code under test.
    """
    deadline = time.time() + timeout
    seen: dict = {}
    while True:
        owned = [row["Partition"] for row in bench.cluster.get("/registry/partitions")
                 if row.get("Node") == doomed and not row.get("Retired")]
        seen = {}
        # GET /registry/saves TAKES NO QUERY. DirectGateway hands it to GatewayRegistry.Saves(),
        # which returns the whole catalogue, so the ?partition= this used to pass read as a filter
        # and was not one: seen[partition] held the holders of EVERY save and the gate below passed
        # as soon as any save anywhere had an off-node holder. The partition-3 coin toss this
        # function exists to remove was still being tossed (T-0204). Filter here, like every other
        # caller of this route does.
        catalogue = bench.cluster.get("/registry/saves")
        for partition in owned:
            seen[partition] = sorted({holder for row in catalogue
                                      if row["SaveId"]["Partition"] == partition
                                      and not row.get("PendingDelete")
                                      for holder in (row.get("LocationHolders") or {}).values()})
        # `owned` empty means the node has already let go of everything, which is equally safe.
        if all(set(holders) - {doomed} for holders in seen.values()):
            return seen
        if time.time() >= deadline:
            raise ClusterError(f"no copy of {doomed}'s partitions on another node within "
                               f"{timeout:.0f}s: {json.dumps(seen)}")
        time.sleep(1)


def p3_plr_01(bench) -> None:
    record: dict = {}
    try:
        # node-1 only to begin with: every later leg has to land on a node that has NEVER hosted this
        # player, or the test passes on a broken build (a node hands a departed player's toolbar back).
        bench.start_cluster(nodes=["node-1", "wa-1"])
        api = bench.start_client(CLIENT_ID, REMOTE_PORT)
        bench.wait_spawned(CLIENT_ID)
        bench.ensure_gameplay_focus(CLIENT_ID)

        joined = api.get_player_settings()
        record["joined"] = {"toolbar": toolbar_slots(joined), "marks": marks_present(joined),
                            "promoteLevel": joined.get("promoteLevel"),
                            "allowed": joined.get("allowedAdminSettings")}
        bench.check("the client joined as an administrator", joined.get("promoteLevel") in ("Admin", "Owner"),
                    str(joined.get("promoteLevel")))

        for slot, definition in TOOLBAR_MARKS.items():
            api.set_toolbar_slot(slot, definition)
            time.sleep(0.5)
        palette = api.get_player_settings().get("buildColorSlots") or []
        api.set_build_color(COLOR_SLOT, *COLOR_MARK)
        if palette:
            api.set_build_color(0, *palette[0])   # leave the selection off the marked slot
        for flag in ADMIN_MARKS:
            api.set_admin_flag(flag, True)
            time.sleep(0.5)
        time.sleep(3)
        applied = api.get_player_settings()
        record["applied"] = {"marks": marks_present(applied), "node": node_admin_state(bench, "node-1")}
        # Positive control: the marks must be visible before any loss can be measured.
        bench.check("marks applied", all(marks_present(applied).values()), json.dumps(marks_present(applied)))
        bench.check("the hosting node's session carries the admin flags",
                    admin_state_ok(record["applied"]["node"]), json.dumps(record["applied"]["node"].get("row")))
        try:
            record["waProfiles"] = bench.cluster.node("wa-1", "/player-profiles")
        except Exception as error:  # noqa: BLE001
            record["waProfiles"] = {"error": str(error)[:200]}

        # -- leg 1: the node rotates out from under a connected client --
        bench.cluster.start_node("node-2")
        bench.cluster.wait_ready(nodes=["node-2"])
        record["copiesBeforeKill"] = {"node-1": wait_for_offsite_copy(bench, "node-1")}
        bench.cluster.kill(slot="node-1")
        time.sleep(RECOVERY_SECONDS)   # the gateway recovers node-1's partitions onto node-2
        settings_when_back(api)
        bench.ensure_gameplay_focus(CLIENT_ID)
        moved = api.get_player_settings()
        record["rotated"] = {"marks": marks_present(moved), "toolbar": toolbar_slots(moved),
                             "node": node_admin_state(bench, "node-2")}
        bench.check("marks survive the node being rotated out under the client",
                    all(marks_present(moved).values()), json.dumps(marks_present(moved)))
        bench.check("the new node's session carries the admin flags",
                    admin_state_ok(record["rotated"]["node"]), json.dumps(record["rotated"]["node"].get("row")))

        # -- leg 2: a relog onto a node that has never hosted this player --
        bench.cluster.start_node("node-3")
        bench.cluster.wait_ready(nodes=["node-3"])
        record["copiesBeforeKill"]["node-2"] = wait_for_offsite_copy(bench, "node-2")
        bench.kill_client(CLIENT_ID)
        bench.cluster.kill(slot="node-2")
        time.sleep(RECOVERY_SECONDS)
        api = bench.start_client(CLIENT_ID, REMOTE_PORT, label=f"{CLIENT_ID}-relog")
        bench.wait_spawned(CLIENT_ID)
        bench.ensure_gameplay_focus(CLIENT_ID)
        rejoined = api.get_player_settings()
        record["relog"] = {"toolbar": toolbar_slots(rejoined), "marks": marks_present(rejoined),
                           "node": node_admin_state(bench, "node-3")}
        bench.check("marks survive a rejoin on a node that never hosted the player",
                    all(marks_present(rejoined).values()), json.dumps(marks_present(rejoined)))
        bench.check("the rejoined node's session carries the admin flags",
                    admin_state_ok(record["relog"]["node"]), json.dumps(record["relog"]["node"].get("row")))
        bench.exercised(route="node-1 -> node-2 (killed under client) -> node-3 (relog)")
    finally:
        bench.record("player-settings", record)



def p3_plr_02(bench) -> None:
    """T-0197: a client that reconnects while its partition is still recovering must be HELD, not
    refused for good.

    P3-PLR-01 cannot see this. Both of its legs sleep ``RECOVERY_SECONDS`` between killing the node
    and bringing the client back, so the client always arrives after the gateway can route it again.

    THE FIRST TWO VERSIONS OF THIS SCENARIO RACED, AND LOST BOTH WAYS. They killed node-1 under the
    client and reconnected into the recovery window, which is real but far too small to aim at:
    bench recovery measured 241 ms, while the gateway only learns the client's node link is gone
    ~14 s after the kill. Reconnect before that notice and the single-flight guard refuses the
    second join outright ("Refused second join for identity N ...: single-flight"), which is a
    duplicate-session refusal and says nothing about T-0197; wait for the notice and the partition
    has been back on node-2 for ~14 s, so there is nothing to hold. No sleep value sits inside a
    window that is negative.

    SO THIS VERSION DOES NOT RACE. It removes every node that could recover the partition, which
    makes the busy state LAST until the scenario itself ends it:

        client joins on node-1, node-2 takes an offsite copy of node-1's partition
          -> BOTH regular nodes are killed; the partition is now unowned with no node to re-home to
          -> the client reconnects: its live placement still names that partition, so
             ResolveClientRoute throws RegistryBusyException and the gateway HOLDS the join
          -> node-2 is restarted; it still holds the copy, claims the recovery, and loads it
          -> the held join is answered and the character spawns

    Three code facts make that deterministic rather than lucky, and each is asserted below:

    * The placement survives the node's death. ``_state.ClientPartitions`` is only dropped for a
      RETIRED partition (GatewayRegistry.ResolveClientRoute); a killed owner re-homes the partition
      and keeps the row, so the join takes the holdable live-placement path rather than the
      ``CharacterHomes`` fallback, where a Busy answer is deliberately swallowed into the default
      backend and no hold is possible.
    * An unowned partition throws ``RegistryBusyException`` ("unowned and being re-homed"), which is
      what ``OnConnectionRequest`` catches and turns into ``HoldJoin``.
    * The hold has FIVE MINUTES of room. ``ReapUnrecoverable`` reports a partition stranded - a
      TERMINAL ``RegistryConflictException``, not a hold - once it has been unowned for
      ``UnownedReapGrace`` (5 min) with no reachable save holder, and with both nodes dead this one
      qualifies. Restarting node-2 takes far less than that, and the scenario fails on the stranded
      line if it ever does not, rather than reporting the timeout as a broken hold.

    THE HOLD MUST OUTLIVE A NODE START, so the scenario raises ``CLUSTER_JOIN_HOLD`` from its
    20 s default. It defers to an externally set value because ``_gateway_env`` spreads
    ``config.gateway_env`` AFTER ``os.environ`` - hard-coding it here would silently override the
    red arm.

    THE RED ARM NEEDS NO BUILD. ``CLUSTER_JOIN_HOLD=0`` disables holding in the gateway itself -
    ``HoldJoin`` then logs "Rejected client N: <reason> (holding disabled)" and refuses exactly as
    the pre-fix code did - so the falsifier is one environment variable on the same package rather
    than a reverted branch and a second package. Running this scenario with it set MUST fail
    "the gateway held the reconnect rather than refusing it"; if it passes, this scenario is not
    measuring the hold and its green means nothing.

    THE HOLD ITSELF IS AN ASSERTION, not a hoped-for side effect: a run where the client simply got
    in because recovery happened to finish first proves nothing about T-0197, so the absence of a
    hold line FAILS the scenario rather than passing it quietly. The recovery is asserted too, for
    the same reason - without it the scenario could pass against a cluster where nothing was ever
    unavailable - and so is the registry's own view of the partition while the join is held, which
    is a second instrument on the same fact: the log says the gateway held, the registry says there
    was something to hold FOR.
    """
    record: dict = {}
    try:
        # BEFORE start_cluster, and only as a default: see the docstring. config.gateway_env is
        # spread last by Cluster._gateway_env, so an unconditional assignment here would beat the
        # CLUSTER_JOIN_HOLD=0 that the red arm sets in the environment and disarm the falsifier.
        hold_seconds = os.environ.get("CLUSTER_JOIN_HOLD", "300")
        bench.cluster.config.gateway_env["CLUSTER_JOIN_HOLD"] = hold_seconds
        record["joinHold"] = hold_seconds

        bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
        api = bench.start_client(CLIENT_ID, REMOTE_PORT)
        bench.wait_spawned(CLIENT_ID)
        bench.ensure_gameplay_focus(CLIENT_ID)
        record["joined"] = {"settings": bool(api.get_player_settings())}

        # node-2 has to be up and Active BEFORE the kills, and has to have taken a copy: it is the
        # node that will recover the partition at the end. Without the copy it comes back with
        # nothing to claim, the hold runs to its deadline, and the scenario measures a missing save
        # instead of a held join.
        bench.cluster.wait_ready(nodes=["node-2"])
        record["copiesBeforeKill"] = {"node-1": wait_for_offsite_copy(bench, "node-1")}

        route = _try(lambda: bench.cluster.get(f"/registry/client-routes?client={CLIENT_ID}"), None)
        partition = int((route or {}).get("Partition") or 0)
        record["partition"] = partition
        bench.check("the client's partition could be read from the registry before the kills",
                    partition != 0, json.dumps(route)[:300])

        held_before = len(_lines(bench, "gateway", rf"Holding join for client {CLIENT_ID}"))
        rejected_before = len(_lines(bench, "gateway", rf"Rejected client {CLIENT_ID}"))
        force_before = len(_lines(bench, "gateway", rf"Force reconnect client {CLIENT_ID}"))

        # BOTH regular nodes, so nothing can recover the partition until this scenario says so.
        # wa-1 stays up: it is the World Authority, not a partition host, so it cannot take the row.
        bench.kill_client(CLIENT_ID)
        bench.cluster.kill(slot="node-2")
        bench.cluster.kill(slot="node-1")

        # THE GATEWAY HAS TO HAVE NOTICED before the reconnect. While it still believes the old
        # session is live, the single-flight guard refuses the new one as a duplicate - transport
        # "ConnectionRejected" 5 ms after connecting, no "Rejected client" line at all - and that
        # refusal is not the one this scenario is about (T-0197, 2026-09-21).
        deadline = time.time() + 90
        noticed = False
        while time.time() < deadline:
            if len(_lines(bench, "gateway", rf"Force reconnect client {CLIENT_ID}")) > force_before:
                noticed = True
                break
            time.sleep(0.5)
        record["gatewayNoticedNodeLoss"] = noticed
        # DETAIL ONLY ON FAILURE. bench.check prints `detail` whether it passed or not, so a
        # failure-shaped string on a PASSING check reads as a failure - the 17:21 green arm printed
        # "[PASS] the gateway held the reconnect - no 'Holding join' line ...", which is a
        # contradiction a reader has to decode. Each check below states what it FOUND when it
        # passes and what was missing when it did not.
        bench.check("the gateway noticed the node loss before the reconnect was attempted", noticed,
                    f"force-reconnect lines: {force_before} -> "
                    f"{len(_lines(bench, 'gateway', rf'Force reconnect client {CLIENT_ID}'))}" if noticed else
                    "no 'Force reconnect client ... node-link-lost' within 90 s of killing node-1; "
                    "a reconnect sent before this point meets the duplicate-session refusal "
                    "instead of the recovery hold, and measures nothing about T-0197")

        # start_client returns when the client's Remote API listens, which the plugin starts before
        # it joins anything - so this does NOT wait for the join, and the join below is the held one.
        api = bench.start_client(CLIENT_ID, REMOTE_PORT, label=f"{CLIENT_ID}-reconnect")

        held: list[str] = []
        registry_view = None
        deadline = time.time() + 120
        while time.time() < deadline:
            held = _lines(bench, "gateway", rf"Holding join for client {CLIENT_ID}")[held_before:]
            if held:
                registry_view = _try(lambda: [row for row in bench.cluster.get("/registry/partitions")
                                              if int(row.get("Partition") or 0) == partition], None)
                break
            time.sleep(1)
        record["held"] = held[:5]
        record["registryWhileHeld"] = registry_view

        rejected_now = len(_lines(bench, "gateway", rf"Rejected client {CLIENT_ID}")) - rejected_before
        bench.check("the gateway held the reconnect rather than refusing it", bool(held),
                    (f"held {len(held)}x, first: {held[0][:160]}" if held else
                     "no 'Holding join' line for this client within 120 s of the reconnect; "
                     f"rejections={rejected_now}"))
        # The second instrument: the log says the gateway held, and the registry says the partition
        # it held for really was unowned. A hold over a partition that was fine all along would be
        # a different bug wearing this scenario's green.
        unowned = bool(registry_view) and all(row.get("Node") in (None, "") for row in registry_view)
        bench.check("the registry had no owner for that partition while the join was held", unowned,
                    json.dumps(registry_view)[:300] if registry_view is not None else
                    "the registry was not read - there was no hold to read it during")

        # Now give the partition somewhere to go. node-2 restarts on its RETAINED data root, so it
        # still holds the offsite copy taken above; fresh=True would throw that away.
        bench.cluster.restart_node("node-2")
        bench.cluster.wait_ready(nodes=["node-2"])
        bench.wait_spawned(CLIENT_ID)
        bench.ensure_gameplay_focus(CLIENT_ID)

        rejected = _lines(bench, "gateway", rf"Rejected client {CLIENT_ID}")[rejected_before:]
        recovered = _lines(bench, "node-2", r"recovery loaded partition ([0-9]+) generation ([0-9]+)")
        stranded = _lines(bench, "gateway", rf"partition {partition} is stranded")
        record["rejected"] = rejected[:5]
        record["recovered"] = recovered[-3:]
        record["stranded"] = stranded[:3]

        bench.check("node-2 actually recovered the partition the client was waiting for",
                    bool(recovered), json.dumps(recovered[-2:])[:300])
        bench.check("the held reconnect ended in a living character",
                    bool(api.get_player_settings()) and bench.wait_spawned(CLIENT_ID) is not None)
        # The 5-minute stranded grace is the scenario's budget, not a background detail: past it the
        # registry answers Conflict instead of Busy and the client is refused for good. If node-2
        # ever takes that long to come back, this says so rather than letting the timeout read as a
        # broken hold.
        bench.check("the partition was never reported stranded (the hold stayed inside the 5 min grace)",
                    not stranded, json.dumps(stranded[:2])[:300])
        # A refusal after a FULL hold is survivable - the client retries and the next request is
        # held again - so it is recorded rather than failed. What must not happen is a refusal with
        # no hold at all, which is the pre-fix behaviour and is caught by the first check.
        bench.check("no reconnect was refused without having been held first",
                    len(rejected) <= len(held), f"held={len(held)} rejected={len(rejected)}")
        bench.standing_audits(bench.diag("entities"), "after the held reconnect")
        bench.exercised(route="both regular nodes killed -> reconnect HELD -> node-2 restarted -> recovery",
                        held=len(held), rejected=len(rejected), joinHold=hold_seconds)
    finally:
        bench.record("held-join", record)


p3_plr_02.regular_nodes = 2
p3_plr_02.administrators = [CLIENT_ID]


p3_plr_01.regular_nodes = 3
p3_plr_01.administrators = [CLIENT_ID]

PASTE_CLIENT_ID = 76561199500009003
PASTE_REMOTE_PORT = 24158 + 45
PASTE_BLUEPRINT = Path(os.environ.get("CLUSTER_TEST_PASTE_BLUEPRINT",
                                      str(Path.home() / "ws/se/se1/cluster-regression/blueprints/Space Test Platform/bp.sbc")))
UNIQUE_MASK = 0xFFFFFFFFFFFFFF


def node_ranges(bench, node: str) -> list[tuple[int, int]]:
    """Every EntityId range the node process was issued, from its own 'entity_id_range allocated' lines."""
    ranges = []
    for line in _lines(bench, node, r"entity_id_range allocated node=" + node + r"\b"):
        match = re.search(r"start=(\d+) end=(\d+)", line)
        if match:
            ranges.append((int(match.group(1)), int(match.group(2))))
    return ranges


def outside(ids, ranges) -> list[int]:
    return [i for i in ids if not any(start <= (i & UNIQUE_MASK) <= end for start, end in ranges)]


def p3_id_01(bench) -> None:
    """T-0243: a grid paste lands with EVERY entity id inside the pasting node's registry-issued range,
    as one consistent remap of the pasted logical group.

    A client pastes the Space Test Platform (a platform with 6 Visitors docked by connectors, 7 grids)
    through the game's own paste event. node-1's /entity-id-audit then lists every id in the pasted
    logical group - grids, fat blocks, their subparts - and the connectors' partners:

      * every id is inside a range node-1 logged as issued to it;
      * no id from the blueprint survives (the blueprint's own ids, fed to the same range check, are
        the known positive: the check must flag them, or it cannot see an unremapped paste);
      * the 7 grids are one logical group and every connector the blueprint had connected is connected
        again, to a connector in that group (the remap was consistent across grids).
    """
    import xml.etree.ElementTree as ET
    if not PASTE_BLUEPRINT.is_file():
        bench.check(f"SETUP: the blueprint exists ({PASTE_BLUEPRINT})", False)
        return
    xml = PASTE_BLUEPRINT.read_text()
    root = ET.fromstring(xml)
    grid_builders = root.findall(".//CubeGrids/CubeGrid")
    blueprint_ids = {int(e.text) for e in root.iter("EntityId") if e.text and e.text.strip() not in ("", "0")}
    blueprint_connected = sum(1 for block in root.iter("MyObjectBuilder_CubeBlock")
                              if (block.findtext("ConnectedEntityId") or "0").strip() not in ("", "0"))

    bench.start_cluster(nodes=["node-1", "wa-1"])
    api = bench.start_client(PASTE_CLIENT_ID, PASTE_REMOTE_PORT)
    bench.wait_spawned(PASTE_CLIENT_ID)
    bench.ensure_gameplay_focus(PASTE_CLIENT_ID)
    api.set_admin_flag("creativeTools", True)
    time.sleep(2)

    ranges = node_ranges(bench, "node-1")
    bench.record("node-1-ranges", ranges)
    if not bench.check("SETUP: node-1 logged at least one issued EntityId range", bool(ranges)):
        return
    flagged = outside(blueprint_ids, ranges)
    bench.check("KNOWN POSITIVE: the range check flags the blueprint's own ids (it can see an unremapped paste)",
                len(blueprint_ids) > 0 and len(flagged) == len(blueprint_ids), f"{len(flagged)} of {len(blueprint_ids)} flagged")

    root_name = grid_builders[0].findtext("DisplayName")
    try:
        pasted = api.paste_blueprint(xml=xml, own=True, timeout_seconds=90)
    except Exception as error:  # noqa: BLE001
        # A 7-grid paste outlives the Remote API's HTTP timeout (504) while the game completes it; the
        # regression harness polls for the grids the same way. The audit below finds the paste by name.
        pasted = {"error": str(error)[:200]}
    bench.record("pasted", pasted)
    query = f"grid={pasted[0]['entityId']}" if isinstance(pasted, list) and pasted else f"name={urllib.parse.quote(root_name)}"

    def audit_settled():
        audit = bench.cluster.node("node-1", f"/entity-id-audit?{query}")
        connected = sum(1 for c in audit["connectors"] if c["connected"])
        return audit if len(audit["grids"]) == len(grid_builders) and connected >= blueprint_connected else None

    audit = None
    deadline = time.time() + 60
    while time.time() < deadline and not audit:
        try:
            audit = audit_settled()
        except Exception:  # noqa: BLE001
            audit = None
        if not audit:
            time.sleep(3)
    audit = audit or bench.cluster.node("node-1", f"/entity-id-audit?{query}")
    bench.record("audit", audit)
    if not bench.check(f"the paste created {len(grid_builders)} grids", len(audit["grids"]) == len(grid_builders),
                       f"{len(audit['grids'])} grids in the group of {audit['root']}"):
        return
    ids = [entry["id"] for entry in audit["ids"]]
    group = set(audit["grids"])
    kinds = {kind: sum(1 for entry in audit["ids"] if entry["kind"] == kind) for kind in ("grid", "block", "subpart")}
    bench.check(f"the {len(grid_builders)} pasted grids are one logical group", len(group) == len(grid_builders)
                and (not isinstance(pasted, list) or {g["entityId"] for g in pasted} <= group), f"{len(group)} grids in the group")
    bad = outside(ids, ranges)
    bench.check("every pasted entity id (grids, blocks, subparts) is inside a range issued to node-1",
                not bad and len(ids) > len(grid_builders), f"{len(bad)} of {len(ids)} outside {ranges}: {bad[:5]}")
    survivors = sorted(set(ids) & blueprint_ids)
    bench.check("no blueprint id survives the paste", not survivors, f"{len(survivors)} survivors: {survivors[:5]}")
    bench.check("the pasted ids are unique", len(ids) == len(set(ids)), f"{len(ids) - len(set(ids))} duplicates")
    connected = [c for c in audit["connectors"] if c["connected"]]
    bench.check(f"every connector the blueprint had connected ({blueprint_connected}) is connected again, inside the group",
                len(connected) >= blueprint_connected and all(c["otherGrid"] in group and c["otherGrid"] != c["grid"] for c in connected),
                json.dumps(connected)[:400])
    bench.exercised(grids=len(group), ids=len(ids), kinds=kinds, connected=len(connected), ranges=ranges)


p3_id_01.administrators = [PASTE_CLIENT_ID]

SCENARIOS_PLAYER = {
    "P3-ID-01": (p3_id_01, "a pasted multi-grid blueprint lands wholly inside the node's EntityId range (T-0243)", "P3"),
    "P3-PLR-01": (p3_plr_01, "player settings survive a node rotation and a relog", "P3"),
    "P3-PLR-02": (p3_plr_02, "a reconnect during recovery is held, not refused (T-0197)", "P3"),
}
