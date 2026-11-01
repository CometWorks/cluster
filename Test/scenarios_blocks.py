#!/usr/bin/env python3
"""SE1-0163: every vanilla functional block type on a multi-node cluster.

The block list is generated from the dedicated server's ``Content/Data/CubeBlocks/*.sbc``: one key
per TypeId (FunctionalBlock per subtype, since its subtypes do different jobs), with a non-DLC large
subtype as its fixture where there is one. A TypeId that is neither in ``FAMILY`` nor explicitly out
of scope fails the scenario, so a game update that adds a block type shows up as uncovered.

The test plugin's ``BlockCoverageHarness`` spawns one small dynamic grid per key (the block, a
battery unless the block makes power, a light for the blocks that act on others), gives each the
state a player would (names, custom data, inventory, toolbar actions, a program, a store offer, a
projection, mechanical heads, connector/gear/laser partners) and reports each block's runtime facts
and its grid's object builder. Every leg compares the fixtures before and after (the OB diff of
TestPlan §1.3, class E: read after the cluster is quiet), with the handover gate's own normalizer and
comparison, then drives each block on the node that holds it now.

* ``P3-BLK-01`` - handover node-1 -> node-2 and back;
* ``P3-BLK-02`` - offline on its node, woken on the other one;
* ``P3-BLK-03`` - a partition save, then the holding node is killed and restarted;
* ``P3-BLK-04`` - Services terminal ship storage: store on one node, list and retrieve after a handover,
  an offline round trip, a restart and a replacement of the World Authority;
* ``P3-BLK-04-RACE`` - #1269: a ship take whose answer was lost, or whose node died before the blueprint fetch, is
  released, so the stored ship is listed again; a fetched take is kept and reported (the node self-test section).
* ``P3-BLK-04-RETRY`` - #1317: a ship storage Store, Order or Delete whose answer was lost leaves the node and the World
  Authority agreeing: a Store is undone at both (Unstore), an Order or Delete is retried under its operation id (the node
  self-test section).
* ``P3-BLK-04-ADMIN`` - #1316: a fetched take whose node never settles it is listed by the Gateway admin API, stranded
  after 10 min; an admin release lists the ship again (and it can be retrieved), an admin consume drops it, and a
  settle of a take that is not stranded yet or not the one held is refused;
* ``P3-BLK-04-DURABLE`` - #1270 (F28): with the World Authority's global saves held before registration, a store and
  a retrieval at the terminal are answered only after the save is registered; a SIGKILL of the WA right after the
  answer and a replacement neither lose nor duplicate the ship;
* ``P3-BLK-05`` - safe zone block: the MySafeZone entity travels with its block, once, with its settings,
  and the block keeps its zone chips;
* ``P3-BLK-06`` - contract block: a contract posted at the block is still offered after the same three legs.
* ``P3-BLK-07`` - admin safe zone (no block) from the world file: every regular node holds it once, with its
  settings, after the boot and the same three legs of the partition inside it.
* ``P3-BLK-08`` - a safe zone block with its zone in a vanilla world converted by ``to-split``: the zone is on
  the block's node from the first boot on, and after the same three legs.
* ``P3-BLK-09`` - admin safe zones created, edited and deleted in game on different nodes: every node has the
  changed set, also after a node restart and a World Authority replacement, and ``to-vanilla`` writes it.
* ``P3-BLK-09-RACE`` - #1273: a node's admin safe zone change whose answer was lost is retried under the same
  operation id, so it cannot undo a newer change from another node (the node self-test section).
* ``P3-BLK-10`` - Services terminal retrieval order, auto-deploy switch and combat cooldown across the same
  legs and a World Authority replacement.
* ``P3-BLK-11`` - ``MagnetarWorld to-vanilla`` exports the ships stored at a Services terminal, and a stock
  server keeps them.

Each block scenario writes ``blocks.md``, the per-key table (pass, fail with the reason, n/a).
"""

from __future__ import annotations

import json
import re
import shutil
import time
from pathlib import Path
from urllib.parse import urlencode

from bench import HOST
from cluster import ClusterError
from scenarios import _all_partitions, _force_handover
from scenarios_core import _lines, _nodes
from scenarios_ext import _gate_residuals, _other_regular, _wait_arrival
from scenarios_offline import _put_config
from scenarios_planet import _graceful_shutdown, _sector_objects, _wait_exited, _world_tool, start_standalone
from scenarios_restart import SERVER_TIMEOUT
from scenarios_wa_ops import _poll, _require

ORIGIN = (900000, 0, 0)
#: Fixtures this far apart (well over the membership distance and the offline quiet radius) each get a
#: partition of their own and go offline alone, so a block that breaks a leg fails only its own key.
SPACING = 30000

# Out of scope by the ticket: armor and decorative blocks without a function.
OUT_OF_SCOPE = {
    "CubeBlock": "armor and decorative blocks, no function",
    "DebugSphere1": "debug-only block", "DebugSphere2": "debug-only block", "DebugSphere3": "debug-only block",
    "Passage": "decorative, no terminal and no state", "Ladder2": "a ladder, no terminal and no state",
    "Kitchen": "decorative, no terminal and no state", "Planter": "decorative, no terminal and no state",
    "FunctionalBlock/LargeBlockAlgaeFarmReskin": "reskin of LargeBlockAlgaeFarm",
}
# Heads are built by their base (RecreateTop) and checked as its topGrid.
HEADS = {"MotorRotor": "MotorStator", "MotorAdvancedRotor": "MotorAdvancedStator", "PistonTop": "ExtendedPistonBase",
         "Wheel": "MotorSuspension"}
FAMILY = {
    **dict.fromkeys(("BatteryBlock", "Reactor", "SolarPanel", "WindTurbine", "HydrogenEngine"), "power"),
    **dict.fromkeys(("OxygenTank", "OxygenGenerator", "OxygenFarm", "AirVent"), "gas"),
    **dict.fromkeys(("Assembler", "Refinery", "UpgradeModule"), "production"),
    **dict.fromkeys(("MedicalRoom", "SurvivalKit"), "respawn"),
    **dict.fromkeys(("MotorStator", "MotorAdvancedStator", "MotorSuspension", "ExtendedPistonBase", "PistonBase"),
                    "mechanical"),
    **dict.fromkeys(("ShipConnector", "LandingGear", "LaserAntenna", "MergeBlock", "MyObjectBuilder_Projector",
                     "Projector", "JumpDrive"), "linked"),
    **dict.fromkeys(("TimerBlock", "ButtonPanel", "SensorBlock", "EventControllerBlock", "MyProgrammableBlock",
                     "BroadcastController", "RemoteControl", "TurretControlBlock", "BasicMissionBlock",
                     "FlightMovementBlock", "DefensiveCombatBlock", "OffensiveCombatBlock", "PathRecorderBlock",
                     "EmotionControllerBlock"), "actor"),
    **dict.fromkeys(("StoreBlock", "VendingMachine", "ContractBlock", "SafeZoneBlock",
                     "FunctionalBlock/ServicesTerminal"), "economy"),
    **dict.fromkeys(("LargeGatlingTurret", "LargeMissileTurret", "InteriorTurret", "SmallGatlingGun",
                     "SmallMissileLauncher", "SmallMissileLauncherReload", "Warhead", "Decoy", "TargetDummyBlock"),
                    "weapon"),
    **dict.fromkeys(("Beacon", "CryoChamber", "RadioAntenna", "TransponderBlock", "CameraBlock", "Cockpit", "Door",
                     "AirtightHangarDoor", "AirtightSlideDoor", "Drill", "ShipGrinder", "ShipWelder", "OreDetector",
                     "GravityGenerator", "GravityGeneratorSphere", "VirtualMass", "SpaceBall", "Gyro", "Thrust",
                     "InteriorLight", "ReflectorLight", "Searchlight", "EmissiveBlock", "TextPanel", "LCDPanelsBlock",
                     "SoundBlock", "Jukebox", "HeatVentBlock", "ExhaustBlock", "Parachute", "TerminalBlock",
                     "CargoContainer", "Collector", "ConveyorSorter", "Conveyor", "ConveyorConnector",
                     "FunctionalBlock/LargeBlockAlgaeFarm", "FunctionalBlock/LargeBlockFarmPlot",
                     "FunctionalBlock/LargeBlockConduitDamaged"), "plain"),
}
# Fields that change on their own while a block sits idle (power drawn, fuel burnt, game-time clocks,
# production timers, the update timer a block arms and disarms itself, an idle turret's or
# searchlight's sweep): the handover gate compares one frozen instant, these comparisons span seconds
# to minutes. Grid motion is the gate's business, not a block's. A waypoint added by coordinates has
# no orientation (NaN) and reads back as identity after any load.
VOLATILE = re.compile(r"/(CurrentStoredPower|StoredPower|FramesFromLastTrigger|ItemProductionRemainingFrames|Progress"
                      r"|TimeRemainingUntilNextBatch|ItemsPerMinute|TimeOfConnection|IsApproaching|UpkeepTime"
                      r"|ActivationTime|TimerEnabled|Rotation|Elevation|LinearVelocity|AngularVelocity"
                      r"|PositionAndOrientation)\b|/Waypoints/.*/(Matrix|RelatedMatrixCurrent|RelatedEntityMatrix)/")
# Per key: the programmable block's Storage element lags the script (the storage fact is compared
# instead); a reactor burns fuel for its light.
VOLATILE_BY_KEY = {"MyProgrammableBlock": re.compile(r"/Storage\b"), "Reactor": re.compile(r"/(Amount|Capacity)\b"),
                   "HydrogenEngine": re.compile(r"/Capacity\b"),
                   # A locked gear re-derives its pivots when the lock re-forms after a thaw (fractions of
                   # a millimetre); the lock itself is compared through the lockMode fact.
                   "LandingGear": re.compile(r"/(GearPivotPosition|OtherPivot|MasterToSlave)\b"),
                   # Where the partner antenna was last seen, re-read after a load (sub-millimetre).
                   "LaserAntenna": re.compile(r"/LastTargetPosition\b"),
                   # An algae farm harvests into its inventory every few minutes while it is online.
                   "FunctionalBlock/LargeBlockAlgaeFarm": re.compile(r"/(Items|MyObjectBuilder_InventoryItem|nextItemId)\b")}
VOLATILE_FACTS = {"storedRatio", "filledRatio", "active", "laserState"}
VOLATILE_FACTS_BY_KEY = {"FunctionalBlock/LargeBlockAlgaeFarm": {"inventories"}}


# -- the block list ----------------------------------------------------------------------------


def block_catalog(content: Path | None = None) -> dict[str, dict]:
    """key -> {typeId, subtype, size, dlc, subtypes} for every block definition the game ships."""
    content = content or HOST.server / "Content/Data/CubeBlocks"
    found: dict[str, list[dict]] = {}
    for path in sorted(content.glob("*.sbc")):
        text = path.read_text(encoding="utf-8-sig")
        for body in re.findall(r"<Definition\b[^>]*>(.*?)</Definition>", text, re.S):
            id_block = re.search(r"<Id>(.*?)</Id>", body, re.S)
            type_match = id_block and re.search(r"<TypeId>([^<]+)</TypeId>", id_block.group(1))
            if not type_match:
                continue
            type_id = type_match.group(1).strip()
            subtype = (re.search(r"<SubtypeId>([^<]*)</SubtypeId>", id_block.group(1)) or [None, ""])[1].strip()
            size = (re.search(r"<CubeSize>(\w+)</CubeSize>", body) or [None, "Large"])[1]
            key = f"{type_id}/{subtype}" if type_id == "FunctionalBlock" else type_id
            found.setdefault(key, []).append({"typeId": type_id, "subtype": subtype, "size": size,
                                              "dlc": "<DLC>" in body, "file": path.name})
    catalog = {}
    for key, rows in found.items():
        best = min(rows, key=lambda row: (row["dlc"], "Prototech" in row["subtype"], row["size"] != "Large"))
        catalog[key] = {**best, "subtypes": len(rows)}
    return catalog


def in_scope(catalog: dict[str, dict]) -> tuple[dict[str, dict], dict[str, str]]:
    """(keys to spawn, key -> reason it is not spawned). Unknown keys are spawned too: they fail as
    uncovered in the table, but a fixture still shows whether they survive."""
    spawn, skipped = {}, {}
    for key, row in sorted(catalog.items()):
        if key in OUT_OF_SCOPE:
            skipped[key] = "n/a: " + OUT_OF_SCOPE[key]
        elif key in HEADS:
            skipped[key] = f"via {HEADS[key]}: built as its head"
        else:
            spawn[key] = row
    return spawn, skipped


# -- cluster plumbing ---------------------------------------------------------------------------


def _node_post(bench, node: str, path: str, body: dict | None = None, timeout: float = 120):
    control = next(spec.control for spec in bench.cluster.config.nodes if spec.node_id == node)
    return bench.cluster._request("POST", f"/cluster{path}", control, timeout, body=body)


def _regular_nodes(bench) -> list[str]:
    return [spec.node_id for spec in bench.cluster.config.nodes if spec.role != "WA"]


def _identity(bench, name: str) -> int:
    identity = bench.cluster.node("node-1", f"/identity-test-create?name={name}", "POST").get("identityId") or 0
    _require(bench, "the fixture owner identity was created", identity > 0)
    _require(bench, "the owner identity reached every node", _poll(lambda: all(
        bench.cluster.node(node, f"/identity-test-status?identityId={identity}").get("exists")
        for node in [*_regular_nodes(bench), "wa-1"]) or None, 120, 2) is not None)
    # Stores and contract blocks need their owner's bank account (store offers, contract rewards).
    bench.cluster.node("node-1", f"/banking-test-change?ownerId={identity}&amount=100000", "POST")
    _require(bench, "the owner's bank account reached every regular node", _poll(lambda: all(
        (bench.cluster.node(node, f"/banking-test-status?ownerId={identity}").get("balance") or 0) >= 100000
        for node in _regular_nodes(bench)) or None, 120, 2) is not None)
    return identity


def spawn(bench, items: list[dict], owner: int, static: bool = False, spacing: float = SPACING) -> dict:
    """Spawn the fixtures on node-1, from ORIGIN, ``spacing`` metres apart."""
    answer = _node_post(bench, "node-1", "/block-test-spawn", {
        "owner": owner, "x": ORIGIN[0], "y": ORIGIN[1], "z": ORIGIN[2], "static": static, "spacing": spacing,
        "items": [{"key": item["key"], "typeId": item["typeId"], "subtype": item["subtype"]} for item in items]})
    bench.record(f"spawn-{int(time.time())}", answer)
    return answer


def states(bench, grids: list[int], ob: bool = True) -> dict[str, dict[int, dict]]:
    """node -> grid -> its state, for every regular node (a grid on two nodes is a duplicate)."""
    query = ",".join(str(grid) for grid in grids)
    result = {}
    for node in _regular_nodes(bench):
        try:
            rows = bench.cluster.node(node, f"/block-test-state?grids={query}&ob={'true' if ob else 'false'}",
                                      timeout=120)["grids"]
        except ClusterError:
            rows = []
        result[node] = {row["grid"]: row for row in rows}
    return result


def where(bench, grids: list[int]) -> dict[int, list[str]]:
    seen = states(bench, grids, ob=False)
    return {grid: [node for node, rows in seen.items() if grid in rows] for grid in grids}


def partitions_of(bench, grids: list[int]) -> dict[int, tuple[str, int]]:
    """grid -> (node, partition), from one diag entity table; grids with no single owner are left out."""
    snapshot = bench.diag("entities")
    owners = {}
    for grid in grids:
        found = bench.entity_owner(snapshot, grid)
        if len(found) == 1:
            owners[grid] = found[0]
    return owners


def wait_settled(bench, grids: list[int], timeout: float = 240) -> dict[int, list[str]]:
    """Every grid on exactly one node and in a partition; returns where they are."""
    def settled():
        places = where(bench, grids)
        return places if all(len(nodes) == 1 for nodes in places.values()) else None
    places = _poll(settled, timeout, 3)
    return places or where(bench, grids)


# -- comparison -----------------------------------------------------------------------------------


def grid_diffs(bench, pairs: list[tuple[str, str]], key_names: list[str]) -> list[list[str]]:
    """The handover gate's own comparison (its normalizer and epsilons, in the test plugin) of each
    (before, after) grid document, minus the idle drift above."""
    answers = _node_post(bench, "node-1", "/block-test-diff", [{"left": left, "right": right} for left, right in pairs],
                         timeout=300)
    result = []
    for key, entries in zip(key_names, answers):
        extra = VOLATILE_BY_KEY.get(key)
        kept = []
        for entry in entries:
            path = entry.split(": ", 1)[0]
            if VOLATILE.search(path) or (extra and extra.search(path)):
                continue
            kept.append(entry.replace("MyObjectBuilder_Sector/SectorObjects/", ""))
        result.append(kept)
    return result


def block_diff(before: dict, after: dict, key: str) -> list[str]:
    volatile = VOLATILE_FACTS | VOLATILE_FACTS_BY_KEY.get(key, set())
    problems = []
    for field in ("name", "functional", "working", "owner"):
        if before.get(field) != after.get(field):
            problems.append(f"{field}: {before.get(field)!r} -> {after.get(field)!r}")
    for fact, value in before.get("facts", {}).items():
        if fact not in volatile and after.get("facts", {}).get(fact) != value:
            problems.append(f"fact {fact}: {value!r} -> {after.get('facts', {}).get(fact)!r}")
    return problems


# -- the fixture set ------------------------------------------------------------------------------


class Fixtures:
    """The spawned set: key -> {grid, block, partners}, the grids to look up, the latest state."""

    def __init__(self, bench, built: list[dict]):
        self.bench = bench
        self.rows = {row["key"]: row for row in built}
        self.results: dict[str, dict[str, str]] = {key: {} for key in self.rows}
        self.extra: list[int] = []   # head grids
        self.partition: dict[str, int] = {}
        self.owner = 0

    def group(self, key: str) -> list[int]:
        """The key's grid, its partners and its head: one fixture, one partition."""
        row = self.rows[key]
        return [row["grid"], *row["partners"], *self.heads_of.get(key, [])]

    def layout(self) -> dict[str, list]:
        owners = partitions_of(self.bench, self.grids)
        return {key: [owners.get(grid) for grid in self.group(key)] for key in self.rows}

    def assign_partitions(self, timeout: float = 180) -> list[str]:
        """key -> partition for every fixture that sits whole in one partition of its own; returns the
        keys that never did (a fixture that shares or splits its partition cannot be moved alone)."""
        def assignment(final: bool = False):
            result, shared = {}, {}
            for key, places in self.layout().items():
                if None not in places and len({place[1] for place in places}) == 1:
                    result[key] = places[0][1]
                    shared[places[0][1]] = shared.get(places[0][1], 0) + 1
            result = {key: part for key, part in result.items() if shared[part] == 1}
            return result if final or len(result) == len(self.rows) else None
        self.partition = _poll(assignment, timeout, 5) or assignment(final=True)
        return sorted(set(self.rows) - set(self.partition))

    @property
    def grids(self) -> list[int]:
        return [row["grid"] for row in self.rows.values()] + [g for row in self.rows.values() for g in row["partners"]] \
            + self.extra

    def snapshot(self, ob: bool = True) -> dict[str, dict]:
        """key -> {node, block (its Describe row), grid row}; a key on no node or two is reported."""
        seen = states(self.bench, self.grids, ob)
        result = {}
        for key, row in self.rows.items():
            nodes = [node for node, grids in seen.items() if row["grid"] in grids]
            if len(nodes) != 1:
                result[key] = {"nodes": nodes}
                continue
            grid = seen[nodes[0]][row["grid"]]
            block = next((fat for fat in grid["fat"] if fat["entityId"] == row["block"]), None)
            result[key] = {"nodes": nodes, "node": nodes[0], "grid": grid, "block": block,
                           "fat": {fat["entityId"]: fat for fat in grid["fat"]},
                           "partners": {g: [n for n, gs in seen.items() if g in gs] for g in row["partners"]}}
        return result

    heads_of: dict[str, list[int]] = {}

    def heads(self, snapshot: dict) -> None:
        self.heads_of = {}
        for key, entry in snapshot.items():
            top = ((entry.get("block") or {}).get("facts") or {}).get("topGrid")
            if top:
                self.heads_of[key] = [top]
                if top not in self.extra:
                    self.extra.append(top)

    def note(self, key: str, leg: str, problems: list[str]) -> None:
        current = self.results[key].get(leg)
        if problems:
            self.results[key][leg] = "fail: " + "; ".join(problems)[:300] if not current or current == "pass" \
                else current + "; " + "; ".join(problems)[:200]
        elif current is None:
            self.results[key][leg] = "pass"

    def compare(self, leg: str, before: dict, after: dict, keys: list[str] | None = None) -> None:
        keys = list(keys if keys is not None else self.rows)
        pairs, paired = [], []
        for key in keys:
            b, a = before.get(key, {}), after.get(key, {})
            if len(a.get("nodes", [])) != 1:
                self.note(key, leg, [f"grid on {a.get('nodes')} after the leg"])
                continue
            if not b.get("block") or not a.get("block"):
                self.note(key, leg, ["block missing " + ("before" if not b.get("block") else "after")])
                continue
            problems = block_diff(b["block"], a["block"], key)
            if b["grid"]["blocks"] != a["grid"]["blocks"]:
                problems.append(f"grid blocks {b['grid']['blocks']} -> {a['grid']['blocks']}")
            for partner, nodes in a["partners"].items():
                if nodes != [a["node"]]:
                    problems.append(f"partner grid {partner} on {nodes}, block on {a['node']}")
            top = b["block"]["facts"].get("topGrid")
            if top and a["block"]["facts"].get("topGrid") != top:
                problems.append(f"head grid {top} -> {a['block']['facts'].get('topGrid')}")
            self.note(key, leg, problems)
            pairs.append((b["grid"]["sector"], a["grid"]["sector"]))
            paired.append(key)
        diffs = dict(zip(paired, grid_diffs(self.bench, pairs, paired)))
        self.bench.record(f"diffs-{leg}", diffs)
        for key, entries in diffs.items():
            self.note(key, leg, [f"OB {entry}" for entry in entries[:6]])


def link_fixtures(bench, fixtures: Fixtures, timeout: float = 90) -> dict[str, str]:
    """Connect the connectors, lock the gears, point the lasers at their partners; key -> outcome."""
    want = {"ShipConnector": ("connect", "status", "Connected"), "LandingGear": ("lock", "lockMode", "Locked"),
            **{key: ("top", "topBlock", None) for key, family in FAMILY.items() if family == "mechanical"}}
    outcome = {}
    snapshot = fixtures.snapshot(ob=False)
    for key, row in fixtures.rows.items():
        entry = snapshot.get(key, {})
        if not entry.get("block"):
            continue
        type_id, node = row["typeId"], entry["node"]
        if key in want or type_id in want:
            action, fact, value = want.get(key) or want[type_id]

            def linked():
                state = fixtures.snapshot(ob=False)[key]["block"]["facts"].get(fact)
                if state == value or (value is None and state):
                    return state
                _node_post(bench, node, f"/block-test-act?block={row['block']}&action={action}")
                return None
            outcome[key] = "linked" if _poll(linked, timeout, 3) else "never linked"
        elif type_id == "LaserAntenna" and row["partners"]:
            partner = bench.cluster.node(node, f"/block-test-state?grids={row['partners'][0]}&ob=false")["grids"]
            target = next((fat["entityId"] for fat in partner[0]["fat"] if fat["typeId"] == "LaserAntenna"), 0) \
                if partner else 0
            _node_post(bench, node, f"/block-test-act?block={row['block']}&action=laser&value={target}")
            done = _poll(lambda: fixtures.snapshot(ob=False)[key]["block"]["facts"].get("laserState") == "connected" or None,
                         timeout, 3)
            outcome[key] = "linked" if done else "never linked"
    bench.record("links", outcome)
    return outcome


LINKED_STATE = {"ShipConnector": ("status", "Connected"), "LandingGear": ("lockMode", "Locked"),
                "LaserAntenna": ("laserState", "connected")}


def exercise(bench, fixtures: Fixtures, leg: str, snapshot: dict, links: dict[str, str],
             keys: list[str] | None = None) -> None:
    """Drive every block on the node that holds it now."""
    for key in keys if keys is not None else fixtures.rows:
        row = fixtures.rows[key]
        entry = snapshot.get(key, {})
        block = entry.get("block")
        if not block:
            continue
        node, facts, problems = entry["node"], block["facts"], []

        def act(action: str, value: str | None = None, target: int | None = None) -> dict:
            query = f"/block-test-act?block={target or row['block']}&action={action}" + (f"&value={value}" if value else "")
            return _node_post(bench, node, query)

        try:
            # A linked block switched off lets go of its partner; its link is its function check instead.
            if "enabled" in facts and row["typeId"] not in LINKED_STATE:
                first = act("toggle")
                second = act("toggle")
                if first.get("enabled") == facts["enabled"] or second.get("enabled") != facts["enabled"]:
                    problems.append(f"toggle read back {first.get('enabled')}, {second.get('enabled')}")
            if "toolbar0" in facts:
                light = next((fat for fat in entry["fat"].values() if fat["name"] == f"blk-{key}-light"), None)
                if light is None or not facts["toolbar0"] or not facts["toolbar0"].startswith(f"{light['entityId']}:"):
                    problems.append(f"slot 0 does not target the light (toolbar0={facts['toolbar0']!r})")
                if light is not None:
                    before = light["facts"]["lightEnabled"]
                    act("trigger")
                    flipped = _poll(lambda: _light(bench, node, row["grid"], light["entityId"]) != before or None, 15, 1)
                    act("trigger")
                    if not flipped:
                        problems.append("the toolbar action did not switch the light")
            if "compiled" in facts:
                ran = act("run")
                expected = str(int(facts.get("storage") or 0) + 1)
                if ran.get("storage") != expected:
                    problems.append(f"program run: storage {ran.get('storage')!r}, expected {expected!r}"
                                    f" (compiled={ran.get('compiled')})")
            if row["typeId"] in LINKED_STATE and links.get(key) == "linked":
                fact, value = LINKED_STATE[row["typeId"]]
                # A link re-forms a few frames after the thaw at most; give it 20 s before calling it lost.
                state = facts.get(fact) if facts.get(fact) == value else _poll(
                    lambda: (lambda now: now if now == value else None)(_fact(bench, node, row["grid"], row["block"], fact)), 20, 2)
                if state != value:
                    problems.append(f"{fact} {_fact(bench, node, row['grid'], row['block'], fact)!r}, linked before the leg")
            if facts.get("topGrid") and not facts.get("topBlock"):
                problems.append("mechanical head detached")
            if "projecting" in facts and not facts["projecting"]:
                problems.append("not projecting")
        except ClusterError as error:
            problems.append(f"act failed: {str(error)[:160]}")
        fixtures.note(key, leg, problems)
    respawn_points(bench, fixtures, leg, snapshot, keys)


def _fact(bench, node: str, grid: int, block: int, fact: str):
    rows = bench.cluster.node(node, f"/block-test-state?grids={grid}&ob=false")["grids"]
    fat = next((fat for row in rows for fat in row["fat"] if fat["entityId"] == block), None)
    return fat["facts"].get(fact) if fat else None


def _light(bench, node: str, grid: int, light: int):
    return _fact(bench, node, grid, light, "lightEnabled")


def respawn_points(bench, fixtures: Fixtures, leg: str, snapshot: dict, keys: list[str] | None = None) -> None:
    """A respawn block is offered cluster-wide from the node that holds it now (RespawnRegistryService)."""
    keys = [key for key in (keys if keys is not None else fixtures.rows)
            if FAMILY.get(key) == "respawn" and snapshot.get(key, {}).get("block")]
    if not keys:
        return

    def converged():
        points = bench.cluster.node("wa-1", "/respawn-points").get("points") or []
        rows = {key: [p for p in points if p.get("RespawnEntityId") == fixtures.rows[key]["block"]] for key in keys}
        return rows if all(len(rows[key]) == 1 and rows[key][0].get("Origin") == snapshot[key]["node"] for key in keys) else None
    rows = _poll(converged, 60, 3)
    points = bench.cluster.node("wa-1", "/respawn-points").get("points") or []
    for key in keys:
        mine = [p for p in points if p.get("RespawnEntityId") == fixtures.rows[key]["block"]]
        ok = rows is not None or (len(mine) == 1 and mine[0].get("Origin") == snapshot[key]["node"])
        fixtures.note(key, leg, [] if ok else [f"respawn registry rows {[(p.get('Origin'), p.get('Working')) for p in mine]},"
                                               f" block on {snapshot[key]['node']}"])


# -- the table ---------------------------------------------------------------------------------


def table(bench, catalog: dict[str, dict], fixtures: Fixtures | None, skipped: dict[str, str], legs: list[str],
          failed_spawns: dict[str, str]) -> None:
    lines = ["| Key | Family | Subtype | " + " | ".join(legs) + " |", "|---|---|---|" + "---|" * len(legs)]
    uncovered = []
    for key in sorted(catalog):
        family = FAMILY.get(key) or ("-" if key in skipped else "UNCOVERED")
        if family == "UNCOVERED":
            uncovered.append(key)
        if key in skipped:
            cells = [skipped[key]] * len(legs)
        elif key in failed_spawns:
            cells = ["fail: spawn: " + failed_spawns[key][:120]] * len(legs)
        else:
            results = fixtures.results.get(key, {}) if fixtures else {}
            cells = [results.get(leg, "not run") for leg in legs]
        lines.append(f"| {key} | {family} | {catalog[key]['subtype']} | " + " | ".join(c.replace("|", "/") for c in cells) + " |")
    bench.record("blocks.md", "\n".join(lines) + "\n")
    bench.check("every block type in the game's definitions has a family or a reason (none uncovered)",
                not uncovered, ", ".join(uncovered))
    if fixtures:
        for leg in legs:
            failing = sorted(key for key, results in fixtures.results.items() if results.get(leg, "").startswith("fail"))
            bench.check(f"{leg}: every block type passes", not failing,
                        "; ".join(f"{key}: {fixtures.results[key][leg][6:120]}" for key in failing)[:1500])
    bench.check("every in-scope block type spawned", not failed_spawns, json.dumps(failed_spawns)[:600])


# -- scenarios ---------------------------------------------------------------------------------


def _setup(bench):
    """Boot, spawn one fixture per key (each in a partition of its own) and link the linked ones.
    Returns (catalog, skipped, fixtures, failed spawns, links)."""
    catalog = block_catalog()
    bench.record("catalog", catalog)
    wanted, skipped = in_scope(catalog)
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0163-owner")
    answer = spawn(bench, [{"key": key, **row} for key, row in wanted.items()], owner)
    failed = {row["key"]: row["error"] for row in answer["failed"]}
    fixtures = Fixtures(bench, answer["built"])
    fixtures.owner = owner
    for row in answer["built"]:
        if row.get("prepareError"):
            fixtures.note(row["key"], "spawned", ["fixture setup: " + row["prepareError"]])
    bench.record("places-spawned", wait_settled(bench, fixtures.grids))
    links = link_fixtures(bench, fixtures)
    fixtures.heads(fixtures.snapshot(ob=False))
    unplaced = fixtures.assign_partitions()
    bench.check("every fixture is in a partition of its own", not unplaced,
                json.dumps({key: fixtures.layout()[key] for key in unplaced})[:600])
    for key in unplaced:
        fixtures.note(key, "spawned", ["the fixture never settled into a partition of its own"])
    bench.exercised(blockTypes=len(fixtures.rows), grids=len(fixtures.grids), failedSpawns=len(failed))
    return catalog, skipped, fixtures, failed, links


def _settle(bench, fixtures: Fixtures) -> dict:
    bench.quiesce(timeout=180)
    wait_settled(bench, fixtures.grids)
    time.sleep(5)   # links and power re-evaluate a few frames after the thaw
    return fixtures.snapshot()


def _leg(bench, fixtures: Fixtures, leg: str, before: dict, links: dict, keys: list[str] | None = None) -> dict:
    after = _settle(bench, fixtures)
    bench.record(f"state-{leg}", after)
    fixtures.compare(leg, before, after, keys)
    exercise(bench, fixtures, leg, after, links, keys)
    return _quiet_snapshot(bench, fixtures)


def _quiet_snapshot(bench, fixtures: Fixtures) -> dict:
    """The baseline for the next leg, once what the exercise switched has settled again."""
    time.sleep(5)
    return fixtures.snapshot()


def _residuals(bench, node: str, partition: int) -> str:
    from scenarios_core import _lines
    lines = _lines(bench, node, rf"handover residual transferId=\S+ partition={partition} ")
    return " | ".join(line.split(f"partition={partition} ", 1)[1][-160:] for line in lines[-3:])


def _move_all(bench, fixtures: Fixtures, leg: str, keys: list[str], target: str) -> list[str]:
    """Hand each key's partition to ``target``; a refused handover fails that key. Returns the keys moved."""
    moved = []
    for key in keys:
        partition = fixtures.partition[key]
        origin = _all_partitions(bench).get(partition)
        if origin == target:
            moved.append(key)
            continue
        try:
            _force_handover(bench, origin, target, partition)
            _wait_arrival(bench, partition, target)
        except ClusterError as error:
            fixtures.note(key, leg, [f"handover {origin}->{target} refused: {str(error)[:120]} {_residuals(bench, target, partition)}"])
            try:
                bench.cluster.post(f"/registry/handover-blocked/clear?partition={partition}")
            except ClusterError:
                pass
            continue
        residuals = _gate_residuals(bench, target, partition)
        if residuals:
            fixtures.note(key, leg, [f"gate residuals={residuals}"])
        moved.append(key)
    return moved


def p3_blk_01(bench) -> None:
    """Every functional block type across a handover node-1 -> node-2 and back."""
    catalog, skipped, fixtures, failed, links = _setup(bench)
    before = fixtures.snapshot()
    bench.record("state-initial", before)
    exercise(bench, fixtures, "spawned", before, links)
    before = _quiet_snapshot(bench, fixtures)
    keys = sorted(fixtures.partition)
    for leg, target in (("handover", "node-2"), ("back", "node-1")):
        keys = _move_all(bench, fixtures, leg, keys, target)
        bench.exercised(**{f"{leg}Moved": len(keys)})
        before = _leg(bench, fixtures, leg, before, links, keys)
    table(bench, catalog, fixtures, skipped, ["spawned", "handover", "back"], failed)


def p3_blk_02(bench) -> None:
    """Every functional block type through an offline round trip, woken on the other node."""
    catalog, skipped, fixtures, failed, links = _setup(bench)
    before = fixtures.snapshot()
    exercise(bench, fixtures, "spawned", before, links)
    before = _quiet_snapshot(bench, fixtures)
    keys = _offline_and_wake(bench, fixtures, "offline", sorted(fixtures.partition), "node-1", "node-2")
    _leg(bench, fixtures, "offline", before, links, keys)
    table(bench, catalog, fixtures, skipped, ["spawned", "offline"], failed)


def _offline_and_wake(bench, fixtures: Fixtures, leg: str, keys: list[str], source: str, target: str) -> list[str]:
    """Every key's partition offline, then each loaded on the node other than the one it went offline
    from (``target`` for those still on ``source``). Returns the keys woken.

    The registry admits an offline offer only against fresh partition reports of the automatic loop,
    so the loop runs while the partitions go offline; it may move one first, hence the last holder is
    tracked per partition."""
    partitions = {fixtures.partition[key]: key for key in keys}
    for node in _regular_nodes(bench):
        _put_config(bench, node, {"offline.enabled": "false"})
    _capture_saves(bench, fixtures, leg, keys, source)   # a partition with no save yet is "saving", never offline
    holders = {partition: source for partition in partitions}

    def all_offline():
        online = _all_partitions(bench)
        holders.update({partition: online[partition] for partition in partitions if partition in online})
        return not (set(partitions) & set(online)) or None

    for node in _regular_nodes(bench):
        _put_config(bench, node, {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"})
    bench.cluster.post("/automatic/resume")
    try:
        _poll(all_offline, 300, 5)
    finally:
        bench.cluster.post("/automatic/pause")
        for node in _regular_nodes(bench):
            bench.record(f"offline-status-{leg}-{node}", bench.cluster.node(node, "/offline"))
            _put_config(bench, node, {"offline.enabled": "false"})
    bench.record(f"offline-table-{leg}", bench.cluster.get("/registry/partitions/offline"))
    online = _all_partitions(bench)
    woken = []
    for partition, key in partitions.items():
        if partition in online:
            fixtures.note(key, leg, [f"partition never went offline (still on {online[partition]})"])
            continue
        # The node is a preference: the registry may place the load elsewhere ("requested:<node>").
        wake_on = _other_regular(bench, holders[partition])
        asked = bench.cluster.post(f"/registry/partitions/offline/load?partition={partition}&node={wake_on}&reason=se1-0163")
        if _poll(lambda: _all_partitions(bench).get(partition), 120, 2):
            woken.append(key)
        else:
            fixtures.note(key, leg, [f"offline load never arrived: {json.dumps(asked)[:100]}"])
    bench.exercised(**{f"{leg}Woken": {fixtures.partition[key]: _all_partitions(bench).get(fixtures.partition[key])
                                       for key in woken}})
    return woken


def p3_blk_03(bench) -> None:
    """Every functional block type through a partition save and a kill and restart of its node."""
    catalog, skipped, fixtures, failed, links = _setup(bench)
    exercise(bench, fixtures, "spawned", fixtures.snapshot(), links)
    keys = sorted(fixtures.partition)
    before = _save_and_restart(bench, fixtures, "restart", keys, "node-1")
    _leg(bench, fixtures, "restart", before, links, keys)
    table(bench, catalog, fixtures, skipped, ["spawned", "restart"], failed)


def _save_and_restart(bench, fixtures: Fixtures, leg: str, keys: list[str], node: str) -> dict:
    """A save of every key's partition (that state is what the restart must bring back), then SIGKILL and a
    restart of ``node`` on its retained data root."""
    saved = _quiet_snapshot(bench, fixtures)
    _capture_saves(bench, fixtures, leg, keys, node)
    bench.cluster.restart_node(node)
    bench.cluster.wait_node_log(node, "Game ready", timeout=SERVER_TIMEOUT)
    partitions = {fixtures.partition[key] for key in keys}
    back = _poll(lambda: partitions <= set(_all_partitions(bench)) or None, 300, 5)
    if not back:
        missing = partitions - set(_all_partitions(bench))
        for key in keys:
            if fixtures.partition[key] in missing:
                fixtures.note(key, leg, ["partition not served again after the restart"])
    return saved


def _capture_saves(bench, fixtures: Fixtures, leg: str, keys: list[str], node: str) -> None:
    """A fresh catalogued save of every key's partition on ``node``."""
    sequences = {key: _save_sequence(bench, node, fixtures.partition[key]) for key in keys}
    for key in keys:
        bench.cluster.node(node, f"/save-capture?partition={fixtures.partition[key]}", "POST")
    _poll(lambda: all(_save_sequence(bench, node, fixtures.partition[key]) > sequences[key] for key in keys) or None, 180, 3)
    for key in keys:
        if _save_sequence(bench, node, fixtures.partition[key]) <= sequences[key]:
            fixtures.note(key, leg, ["no save catalogued before the leg"])


def _save_sequence(bench, node: str, partition: int) -> int:
    rows = bench.cluster.node(node, "/save-status").get("partitions") or []
    row = next((r for r in rows if r.get("partition") == partition), {})
    return 0 if row.get("pending") else int(row.get("sequence") or 0)


def _journey(bench, fixtures: Fixtures, key: str, after_leg) -> None:
    """One fixture through a handover, an offline round trip (woken on the other node) and a save plus
    restart of its node; ``after_leg(leg, node)`` checks it wherever it is after each leg."""
    partition = fixtures.partition[key]

    def holder() -> str:
        return _all_partitions(bench).get(partition)

    after_leg("spawned", holder())
    source = holder()
    if _move_all(bench, fixtures, "handover", [key], _other_regular(bench, source)):
        _settle(bench, fixtures)
        after_leg("handover", holder())
    source = holder()
    if _offline_and_wake(bench, fixtures, "offline", [key], source, _other_regular(bench, source)):
        _settle(bench, fixtures)
        after_leg("offline", holder())
    _save_and_restart(bench, fixtures, "restart", [key], holder())
    _settle(bench, fixtures)
    after_leg("restart", holder())
    for leg, outcome in fixtures.results[key].items():
        if outcome.startswith("fail"):
            bench.check(f"{leg}: the fixture went through the leg", False, outcome[6:400])


def _request_result(bench, node: str, request: str, timeout: float = 60) -> str | None:
    answer = _poll(lambda: (lambda row: row if row.get("done") else None)(
        bench.cluster.node(node, f"/block-test-request?id={request}")), timeout, 1)
    return answer["result"] if answer else None


TERMINAL = "FunctionalBlock/ServicesTerminal"


def _replace_authority(bench) -> None:
    """A catalogued global save, then SIGKILL of wa-1 and a replacement World Authority, wa-2, on its data root."""
    def newest() -> int:
        try:
            return ((bench.cluster.get("/registry/global-saves/newest") or {}).get("SaveId") or {}).get("Sequence", 0)
        except ClusterError:
            return 0

    before = newest()
    bench.cluster.node("wa-1", "/global-save-capture", "POST")
    _require(bench, "the World Authority catalogued a global save", _poll(lambda: newest() > before or None, 120, 2))
    _kill_authority(bench, "wa-1", "wa-2")


def _kill_authority(bench, old: str, new: str) -> None:
    """SIGKILL of World Authority ``old`` as it is, and a replacement ``new`` on wa-1's data root."""
    config = bench.cluster.config
    nodes = {spec.node_id: spec for spec in config.nodes}
    bench.cluster.kill(slot=old)
    _require(bench, f"the Gateway fenced the killed World Authority {old}",
             _poll(lambda: bench.cluster.get("/registry/world-authority").get("Node", "?") is None or None, 180, 2))
    replacement = config.add_node(new, role="WA")
    config.node_env_by_id[new] = {"CLUSTER_DATA_ROOT": str(config.node_data(nodes["wa-1"]))}
    bench.cluster.propagate_sources(nodes["wa-1"], [replacement])
    bench.cluster.start_node(new)
    bench.cluster.wait_node_log(new, "Game ready", SERVER_TIMEOUT)


def _storage_fixtures(bench, ships: list[str], owner: int) -> Fixtures:
    """The Services terminal and a cargo container grid per name in ``ships``, all in the terminal's partition."""
    catalog = block_catalog()
    items = {TERMINAL: catalog[TERMINAL], **{ship: catalog["CargoContainer"] for ship in ships}}
    answer = spawn(bench, [{"key": key, **row} for key, row in items.items()], owner, spacing=60)
    _require(bench, "the terminal and the ships spawned", not answer["failed"], json.dumps(answer["failed"]))
    fixtures = Fixtures(bench, answer["built"])
    wait_settled(bench, fixtures.grids)
    group = _poll(lambda: (lambda owners: owners if len(owners) == len(fixtures.grids)
                           and len(set(owners.values())) == 1 else None)(partitions_of(bench, fixtures.grids)), 180, 5) \
        or partitions_of(bench, fixtures.grids)
    _require(bench, "the terminal and its ships share one partition",
             len({place for place in group.values()}) == 1 and len(group) == len(fixtures.grids), json.dumps(
                 {str(grid): place for grid, place in group.items()}))
    fixtures.partition = {TERMINAL: next(iter(group.values()))[1]}
    return fixtures


def p3_blk_04(bench) -> None:
    """Services terminal ship storage on a cluster: a ship stored at the terminal must still be listed
    and retrievable there, on every node and once, after the terminal's partition moved, went offline, its
    node restarted or the World Authority was replaced."""
    ships = [f"ship-{n}" for n in range(1, 5)]
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0163-storage")
    fixtures = _storage_fixtures(bench, ships, owner)
    terminal = fixtures.rows[TERMINAL]["block"]
    stored: list[str] = []      # ship names stored before the current leg
    queue = list(ships)
    report = {}

    def after_leg(leg: str, node: str) -> None:
        listing = bench.cluster.node(node, "/block-test-stored-grids")
        report[leg] = {"node": node, "listing": listing, "others": {other: bench.cluster.node(other, "/block-test-stored-grids")
                                                                     for other in _regular_nodes(bench) if other != node}}
        names = {row["name"]: row["id"] for row in listing["grids"]}
        for name in stored:
            listed = bench.check(f"{leg}: {name}, stored before the leg, is listed at the terminal on {node}",
                                 f"blk-{name}" in names, json.dumps(listing)[:300])
            elsewhere = {other: f"blk-{name}" in {row["name"] for row in rows["grids"]}
                         for other, rows in report[leg]["others"].items()}
            bench.check(f"{leg}: {name} is listed on every other regular node too", all(elsewhere.values()),
                        json.dumps(elsewhere))
            if listed:
                request = bench.cluster.node(node, f"/block-test-retrieve-grid?terminal={terminal}&id={names['blk-' + name]}"
                                                   f"&owner={owner}", "POST")["request"]
                result = _request_result(bench, node, request)
                if bench.check(f"{leg}: {name} is retrieved at the terminal on {node}", result == "Success", str(result)):
                    # Retrieved once: no node offers it any more (SE1-0233).
                    gone = _poll(lambda: all(f"blk-{name}" not in {row["name"] for row in
                                                                   bench.cluster.node(n, "/block-test-stored-grids")["grids"]}
                                             for n in _regular_nodes(bench)) or None, 30, 2)
                    bench.check(f"{leg}: after its retrieval no node lists {name}", bool(gone))
        stored.clear()
        if queue:
            name = queue.pop(0)
            request = bench.cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows[name]['grid']}"
                                               f"&owner={owner}", "POST")["request"]
            result = _request_result(bench, node, request)
            if bench.check(f"{leg}: {name} is stored at the terminal on {node}", result == "Success", str(result)):
                stored.append(name)
                fixtures.rows.pop(name)   # its grid is gone; the retrieved one is a new entity

    _journey(bench, fixtures, TERMINAL, after_leg)
    if stored:
        # The World Authority keeps the stored ships (SE1-0233): the last one survives its replacement.
        _replace_authority(bench)
        after_leg("authority", _all_partitions(bench).get(fixtures.partition[TERMINAL]))
    bench.record("storage", report)
    bench.exercised(legs=list(report), nodes={leg: row["node"] for leg, row in report.items()})


DURABLE_HOLD_MS = 6000   # + replication, < the node's 10 s ship storage command timeout


def p3_blk_04_durable(bench) -> None:
    """#1270 (race F28): the World Authority answers a ship storage command only once a registered global save holds
    it. Each arm holds the WA's global saves unregistered for DURABLE_HOLD_MS (test seam), runs the command at the
    terminal and SIGKILLs the WA the moment the node has its answer; a replacement restores the newest catalogued save.
    Arm a, Store of ship-1: the ship is stored exactly once afterwards (red: neither stored nor in the world - lost).
    Arm b, Take + Consume (a retrieval) of ship-2, stored and catalogued before: the ship is in the world and stored
    nowhere (red: both - duplicated)."""
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "f28-storage")
    fixtures = _storage_fixtures(bench, ["ship-1", "ship-2"], owner)
    terminal = fixtures.rows[TERMINAL]["block"]
    node = _all_partitions(bench)[fixtures.partition[TERMINAL]]
    report: dict = {"node": node}

    def listed(name: str) -> dict:
        return {each: [row["id"] for row in bench.cluster.node(each, "/block-test-stored-grids")["grids"] if row["name"] == name]
                for each in _regular_nodes(bench)}

    def in_world(name: str) -> dict:
        found = {}
        for each in _regular_nodes(bench):
            try:
                found[each] = bench.cluster.node(each, f"/entity-id-audit?name={name}").get("root")
            except ClusterError:
                found[each] = None
        return found

    def newest() -> int:
        try:
            return ((bench.cluster.get("/registry/global-saves/newest") or {}).get("SaveId") or {}).get("Sequence", 0)
        except ClusterError:
            return 0

    def hold(wa: str) -> None:
        held = bench.cluster.node(wa, f"/global-save-register-hold?ms={DURABLE_HOLD_MS}", "POST")
        _require(bench, f"{wa} holds its global saves before registration", held.get("until", 0) > 0, json.dumps(held)[:300])

    def answered(label: str, request: str) -> tuple[str | None, float]:
        started = time.monotonic()
        answer = _poll(lambda: (lambda row: row if row.get("done") else None)(
            bench.cluster.node(node, f"/block-test-request?id={request}")), 60, 0.2)
        report[label] = {"result": answer and answer["result"], "answerSeconds": round(time.monotonic() - started, 2)}
        return (answer or {}).get("result"), time.monotonic() - started

    # ship-2 stored and in a catalogued global save: arm b's fixture.
    request = bench.cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows['ship-2']['grid']}"
                                       f"&owner={owner}", "POST")["request"]
    _require(bench, f"ship-2 is stored at the terminal on {node}", _request_result(bench, node, request) == "Success")
    before = newest()
    bench.cluster.node("wa-1", "/global-save-capture", "POST")
    _require(bench, "a global save after ship-2's store is catalogued", _poll(lambda: newest() > before or None, 120, 2))

    # -- arm a: Store, answered, WA killed before the save holding it is registered --
    hold("wa-1")
    request = bench.cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows['ship-1']['grid']}"
                                       f"&owner={owner}", "POST")["request"]
    result, seconds = answered("store", request)
    _require(bench, f"store: ship-1 is stored at the terminal on {node}", result == "Success", str(result))
    report["store"]["wa"] = bench.cluster.node("wa-1", "/global-save-register-hold")
    _kill_authority(bench, "wa-1", "wa-2")
    rows = _poll(lambda: (lambda r: r if sum(map(len, r.values())) else None)(listed("blk-ship-1")), 60, 2) \
        or listed("blk-ship-1")
    world = in_world("blk-ship-1")
    report["store"].update(listed=rows, world=world)
    bench.check("store: the answer waited for the global save registration (persist before answer)",
                seconds >= DURABLE_HOLD_MS / 1000 * 0.9, f"answered after {seconds:.2f}s, hold {DURABLE_HOLD_MS} ms")
    bench.check("store: after the WA replacement every regular node lists ship-1 exactly once",
                all(len(ids) == 1 for ids in rows.values()), json.dumps(rows))
    bench.check("store: ship-1's grid is not in the world any more", not any(world.values()), json.dumps(world))

    # -- arm b: Take + Consume (retrieval), answered, WA killed before the save holding them is registered --
    stored = _poll(lambda: (lambda r: r if all(len(ids) == 1 for ids in r.values()) else None)(listed("blk-ship-2")), 60, 2)
    _require(bench, "ship-2 is listed on every regular node under wa-2", stored is not None, json.dumps(listed("blk-ship-2")))
    ship = stored[node][0]
    hold("wa-2")
    request = bench.cluster.node(node, f"/block-test-retrieve-grid?terminal={terminal}&id={ship}&owner={owner}",
                                 "POST")["request"]
    result, seconds = answered("retrieve", request)
    _require(bench, f"retrieve: ship-2 is retrieved at the terminal on {node}", result == "Success", str(result))
    report["retrieve"]["wa"] = bench.cluster.node("wa-2", "/global-save-register-hold")
    _kill_authority(bench, "wa-2", "wa-3")
    # The node's outbox re-sends an unanswered Consume to wa-3 under its operation id; give it two flush rounds.
    time.sleep(15)
    rows = listed("blk-ship-2")
    world = in_world("blk-ship-2")
    report["retrieve"].update(listed=rows, world=world)
    bench.check("retrieve: the Take's answer waited for the global save registration",
                seconds >= DURABLE_HOLD_MS / 1000 * 0.9, f"answered after {seconds:.2f}s, hold {DURABLE_HOLD_MS} ms")
    bench.check("retrieve: after the WA replacement no regular node lists ship-2", not any(rows.values()), json.dumps(rows))
    bench.check("retrieve: ship-2's grid is in the world on exactly one node", sum(1 for v in world.values() if v) == 1,
                json.dumps(world))
    bench.record("durable", report)


def p3_blk_10(bench) -> None:
    """Services terminal retrieval orders, auto-deploy and combat cooldown on a cluster (SE1-0248). A delayed
    retrieval queued at the terminal is known on every node with the player's auto-deploy switch, and stays
    through a handover, an offline round trip, a restart of the terminal's node and a World Authority
    replacement; only the order can take its ship, and it delivers once the switch is back on. A grid's
    combat cooldown follows it through the handover."""
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0248-orders")
    fixtures = _storage_fixtures(bench, ["ship-1", "ship-2"], owner)
    terminal = fixtures.rows[TERMINAL]["block"]
    fighter = fixtures.rows["ship-2"]["grid"]
    node = _all_partitions(bench)[fixtures.partition[TERMINAL]]
    request = bench.cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows['ship-1']['grid']}"
                                       f"&owner={owner}", "POST")["request"]
    _require(bench, f"ship-1 is stored at the terminal on {node}", _request_result(bench, node, request) == "Success")
    fixtures.rows.pop("ship-1")
    ship = next(row["id"] for row in bench.cluster.node(node, "/block-test-stored-grids")["grids"] if row["name"] == "blk-ship-1")
    # A cooldown long enough to outlast the legs up to the handover; GetCombatCooldownRemainingMs reads it per node.
    for each in _regular_nodes(bench):
        bench.cluster.node(each, f"/block-test-combat?grid={fighter}&cooldown=3600&damage={str(each == node).lower()}", "POST")
    hit = bench.cluster.node(node, f"/block-test-combat?grid={fighter}", "POST")
    _require(bench, "ship-2 is in combat after a hit", hit["remainingMs"] > 0, json.dumps(hit))
    bench.cluster.node(node, f"/block-test-auto-deploy?owner={owner}&on=false", "POST")
    queued = bench.cluster.node(node, f"/block-test-order-grid?terminal={terminal}&id={ship}&owner={owner}&maxMinutes=0.05", "POST")
    _require(bench, "a retrieval order for ship-1 is queued", queued.get("queued") == 1, json.dumps(queued))
    report = {}

    def queue_everywhere() -> dict:
        def row(listing: dict) -> dict:
            user = next((u for u in listing["users"] if u["identity"] == owner), {})
            return {"autoDeploy": user.get("autoDeploy"), "orders": [o["id"] for o in user.get("orders", [])],
                    "inTransition": [g["inTransition"] for g in listing["grids"] if g["id"] == ship]}
        return {each: row(bench.cluster.node(each, "/block-test-stored-grids")) for each in _regular_nodes(bench)}

    expected = {"autoDeploy": False, "orders": [ship], "inTransition": [True]}

    def after_leg(leg: str, holder: str) -> None:
        rows = _poll(lambda: (lambda r: r if all(v == expected for v in r.values()) else None)(queue_everywhere()), 30, 2) \
            or queue_everywhere()
        report[leg] = {"node": holder, "queues": rows}
        bench.check(f"{leg}: every node has the order, the switch off and ship-1 in transition",
                    all(v == expected for v in rows.values()), json.dumps(rows))
        request = bench.cluster.node(holder, f"/block-test-retrieve-grid?terminal={terminal}&id={ship}&owner={owner}",
                                     "POST")["request"]
        result = _request_result(bench, holder, request)
        bench.check(f"{leg}: a direct retrieval of the ordered ship-1 on {holder} is refused", result == "GridNotFound", str(result))
        if leg == "handover":
            combat = bench.cluster.node(holder, f"/block-test-combat?grid={fighter}", "POST")
            report[leg]["combat"] = combat
            bench.check(f"{leg}: ship-2 is still in combat on {holder}", combat["remainingMs"] > 0, json.dumps(combat))

    _journey(bench, fixtures, TERMINAL, after_leg)
    _replace_authority(bench)
    holder = _all_partitions(bench).get(fixtures.partition[TERMINAL])
    after_leg("authority", holder)
    bench.cluster.node(holder, f"/block-test-auto-deploy?owner={owner}&on=true", "POST")
    delivered = True
    try:
        bench.cluster.wait_node_log("wa-2", rf"ship storage Consume id={ship}", 120)
    except Exception as error:   # noqa: BLE001 - the check below reports it
        delivered = str(error)
    bench.check(f"auto-deploy: the order delivered ship-1 at the terminal on {holder}", delivered is True, str(delivered))
    gone = _poll(lambda: (lambda r: r if all(not v["orders"] and not v["inTransition"] for v in r.values()) else None)(
        queue_everywhere()), 30, 2) or queue_everywhere()
    report["delivered"] = gone
    bench.check("auto-deploy: no node lists ship-1 or its order any more",
                all(not v["orders"] and not v["inTransition"] for v in gone.values()), json.dumps(gone))
    bench.record("orders", report)


def _storage_section(world: Path) -> str:
    """The grids storage session component of a world's Sandbox.sbc, as text."""
    text = (world / "Sandbox.sbc").read_text(errors="replace")
    match = re.search(r'<MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_GridsStorageSessionComponent".*?'
                      r'</MyObjectBuilder_SessionComponent>', text, re.S)
    return match.group(0) if match else ""


def p3_blk_11(bench) -> None:
    """MagnetarWorld to-vanilla with ships stored at a Services terminal (SE1-0249). The exported world has each
    stored ship's blueprint and details files and the session component's index and retrieval queue, and a stock
    dedicated server keeps them: it would delete storage files it does not track and entries whose files are
    missing, so they are still in the save it writes."""
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0249-export")
    ships = ["ship-1", "ship-2"]
    fixtures = _storage_fixtures(bench, ships, owner)
    terminal = fixtures.rows[TERMINAL]["block"]
    node = _all_partitions(bench)[fixtures.partition[TERMINAL]]
    for name in ships:
        request = bench.cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows[name]['grid']}"
                                           f"&owner={owner}", "POST")["request"]
        _require(bench, f"{name} is stored at the terminal on {node}", _request_result(bench, node, request) == "Success")
    ids = {row["name"][len("blk-"):]: row["id"] for row in bench.cluster.node(node, "/block-test-stored-grids")["grids"]}
    _require(bench, "both ships are listed", set(ships) <= set(ids), json.dumps(ids))
    bench.cluster.node(node, f"/block-test-auto-deploy?owner={owner}&on=false", "POST")
    queued = bench.cluster.node(node, f"/block-test-order-grid?terminal={terminal}&id={ids['ship-2']}&owner={owner}"
                                      f"&maxMinutes=30", "POST")
    _require(bench, "a retrieval order for ship-2 is queued", queued.get("queued") == 1, json.dumps(queued))

    config = bench.cluster.config
    _graceful_shutdown(bench, "export")
    _wait_exited(bench, [spec.node_id for spec in config.nodes], "export")
    vanilla = config.root / "vanilla-world"
    shutil.rmtree(vanilla, ignore_errors=True)
    converted = _world_tool(bench, "to-vanilla", config.split_world, vanilla, *(config.node_data(spec) for spec in config.nodes))
    bench.record("to-vanilla.txt", converted.stdout + converted.stderr)
    _require(bench, "to-vanilla converted the cluster state", converted.returncode == 0, converted.stderr[-300:])

    def kept(world: Path, label: str) -> None:
        section = _storage_section(world)
        bench.record(f"storage-{label}.xml", section)
        for name, ship in ids.items():
            files = [f"gs-{ship}.sbc", f"gs-{ship}-detail.sbc"]
            bench.check(f"{label}: {name}'s blueprint and details files are there",
                        all((world / file).is_file() for file in files), str(sorted(p.name for p in world.glob("gs-*"))))
            bench.check(f"{label}: the session component lists {name}", ship in section)
        order = re.search(r"<OrdersData>.*?</OrdersData>", section, re.S)
        bench.check(f"{label}: the retrieval order for ship-2 is in the session component",
                    bool(order) and ids["ship-2"] in order.group(0), section[-400:])

    kept(vanilla, "exported")
    server = start_standalone(bench, vanilla, ready=("Session loaded", "Game ready"))
    try:
        saved = server.data / "Saves" / vanilla.name
        requested = time.time()
        server.remote_call("PATCH", "/v1/session")
        _require(bench, "the stock server saved the session", _poll(
            lambda: (saved / "Sandbox.sbc").is_file() and (saved / "Sandbox.sbc").stat().st_mtime >= requested or None,
            120, 2), str(saved))
        kept(saved, "stock save")
        dropped = _lines(bench, server.name, r"storage files are being deleted|is missing storage files")
        bench.check("the stock server dropped no stored ship", not dropped, "; ".join(dropped)[:400])
    finally:
        server.stop()


def _block_zone_checks(bench, fixtures: Fixtures, block: int):
    """``after_leg`` for a safe zone block fixture: its zone exists once, on the block's node, with the
    radius 25 it started with and unchanged settings, the block points at it and kept its 5 zone chips."""
    reference = {}

    def zones() -> dict[str, list[dict]]:
        return {name: [zone for zone in bench.cluster.node(name, "/block-test-safezones")["zones"] if zone["block"] == block]
                for name in _regular_nodes(bench)}

    def after_leg(leg: str, holder: str) -> None:
        found = _poll(lambda: (lambda z: z if len(z[holder]) == 1 else None)(zones()), 30, 2) or zones()
        bench.record(f"zones-{leg}", found)
        here = found[holder]
        elsewhere = {name: rows for name, rows in found.items() if name != holder and rows}
        if bench.check(f"{leg}: exactly one safe zone of the block on its node {holder}", len(here) == 1, json.dumps(found)):
            zone = {key: here[0][key] for key in ("entityId", "enabled", "radius", "allowed", "access")}
            if "zone" not in reference:
                reference["zone"] = zone
                bench.check(f"{leg}: the zone has radius 25 and is enabled", zone["radius"] == 25 and zone["enabled"],
                            json.dumps(zone))
            bench.check(f"{leg}: the zone kept its entity id and settings", zone == reference["zone"],
                        f"{zone} != {reference['zone']}")
        bench.check(f"{leg}: no safe zone of the block left on another node", not elsewhere, json.dumps(elsewhere))
        state = fixtures.snapshot()["SafeZoneBlock"]
        facts = (state.get("block") or {}).get("facts") or {}
        bench.check(f"{leg}: the block points at a zone that exists on its node",
                    bool(facts.get("safeZone")) and facts.get("safeZoneHere") is True, json.dumps(facts))
        # 5 chips and an hour of upkeep paid: none may be spent or lost.
        bench.check(f"{leg}: the block kept its 5 zone chips", facts.get("zoneChips") == 5, json.dumps(facts))

    return after_leg


def p3_blk_05(bench) -> None:
    """Safe zone block: its MySafeZone entity exists once, on the block's node, with its settings and the
    block's zone chips, after a handover, an offline round trip and a node restart, and nothing is left
    behind."""
    catalog = block_catalog()
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0163-safezone")
    answer = spawn(bench, [{"key": "SafeZoneBlock", **catalog["SafeZoneBlock"]}], owner, static=True)
    _require(bench, "the safe zone block spawned on a static grid", not answer["failed"], json.dumps(answer["failed"]))
    fixtures = Fixtures(bench, answer["built"])
    wait_settled(bench, fixtures.grids)
    unplaced = fixtures.assign_partitions()
    _require(bench, "the safe zone fixture has a partition", not unplaced)
    block = fixtures.rows["SafeZoneBlock"]["block"]
    node = _all_partitions(bench)[fixtures.partition["SafeZoneBlock"]]
    switched = bench.cluster.node(node, f"/block-test-safezone-on?block={block}", "POST")
    _require(bench, "the block created its safe zone", switched.get("zone"), json.dumps(switched))
    _journey(bench, fixtures, "SafeZoneBlock", _block_zone_checks(bench, fixtures, block))
    bench.exercised(results=fixtures.results)


def p3_blk_06(bench) -> None:
    """Contract block: a contract posted at the block is still offered there after a handover, an
    offline round trip and a node restart."""
    catalog = block_catalog()
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0163-contracts")
    answer = spawn(bench, [{"key": "ContractBlock", **catalog["ContractBlock"]}], owner)
    _require(bench, "the contract block spawned", not answer["failed"], json.dumps(answer["failed"]))
    fixtures = Fixtures(bench, answer["built"])
    wait_settled(bench, fixtures.grids)
    _require(bench, "the contract fixture has a partition", not fixtures.assign_partitions())
    block = fixtures.rows["ContractBlock"]["block"]
    node = _all_partitions(bench)[fixtures.partition["ContractBlock"]]
    # The block owner pays the reward up front; a fresh identity has no money.
    bench.cluster.node(node, f"/banking-test-change?ownerId={owner}&amount=100000", "POST")
    _poll(lambda: (bench.cluster.node(node, f"/banking-test-status?ownerId={owner}").get("balance") or 0) >= 100000 or None, 60, 2)
    posted = bench.cluster.node(node, f"/block-test-contract?block={block}&owner={owner}", "POST")
    _require(bench, "a contract was posted at the block", posted.get("success"), json.dumps(posted))
    contract = posted["contractId"]

    def after_leg(leg: str, holder: str) -> None:
        listed = {name: bench.cluster.node(name, f"/block-test-contracts?block={block}") for name in _regular_nodes(bench)}
        bench.record(f"contracts-{leg}", listed)
        bench.check(f"{leg}: the contract is offered at the block on its node {holder}",
                    contract in listed[holder]["contracts"], json.dumps(listed))

    _journey(bench, fixtures, "ContractBlock", after_leg)


ADMIN_ZONE_ID = 72057594037928187


def _admin_zone(entity_id: int, position: tuple, name: str) -> str:
    """An admin safe zone (no block, like Octahedron's stations): radius 50, Drilling and LandingGearLock allowed."""
    return f"""    <MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_SafeZone">
      <SubtypeName />
      <EntityId>{entity_id}</EntityId>
      <PersistentFlags>CastShadows InScene</PersistentFlags>
      <Name>{entity_id}</Name>
      <PositionAndOrientation>
        <Position x="{position[0]}" y="{position[1]}" z="{position[2]}" />
        <Forward x="0" y="0" z="-1" />
        <Up x="0" y="1" z="0" />
      </PositionAndOrientation>
      <DisplayName>{name}</DisplayName>
      <Radius>50</Radius>
      <Enabled>true</Enabled>
      <SafeZoneBlockId>0</SafeZoneBlockId>
      <AccessTypePlayers>Blacklist</AccessTypePlayers>
      <AccessTypeFactions>Blacklist</AccessTypeFactions>
      <AccessTypeGrids>Blacklist</AccessTypeGrids>
      <AccessTypeFloatingObjects>Blacklist</AccessTypeFloatingObjects>
      <AllowedActions>Drilling LandingGearLock</AllowedActions>
      <Factions />
      <Players />
      <Entities />
      <Shape>Sphere</Shape>
      <IsVisible>true</IsVisible>
      <Texture>SafeZone_Texture_Default</Texture>
    </MyObjectBuilder_EntityBase>"""


ADMIN_ZONE = _admin_zone(ADMIN_ZONE_ID, ORIGIN, "SE1-0251 admin zone")


def _template_with(bench, name: str, entities: str) -> Path:
    """Cluster-Simple with ``entities`` (sector XML) added. Only the XML sector is kept, the binary one
    would lack them."""
    source = Path(bench.cluster.config.world_template or HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / name
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    (target / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    sector = target / "SANDBOX_0_0_0_.sbs"
    text, n = re.subn(r"\n\s*</SectorObjects>", lambda m: "\n" + entities + m.group(0), sector.read_text(), count=1)
    if n != 1:
        raise AssertionError("no </SectorObjects> in the template sector")
    sector.write_text(text)
    return target


def p3_blk_07(bench) -> None:
    """Admin safe zone (no block) from the world file: every regular node holds it exactly once, with its
    settings, after the boot, a handover of the partition inside it, an offline round trip and a restart."""
    # An admin safe zone (no block, like Octahedron's stations) around ORIGIN, where the fixture spawns.
    bench.cluster.config.world_template = _template_with(bench, "Cluster-Simple-admin-zone", ADMIN_ZONE)
    catalog = block_catalog()
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "se1-0251-admin-zone")
    answer = spawn(bench, [{"key": "Beacon", **catalog["Beacon"]}], owner, static=True)
    _require(bench, "the fixture spawned inside the admin zone", not answer["failed"], json.dumps(answer["failed"]))
    fixtures = Fixtures(bench, answer["built"])
    wait_settled(bench, fixtures.grids)
    _require(bench, "the fixture has a partition", not fixtures.assign_partitions())
    expected = {"entityId": ADMIN_ZONE_ID, "enabled": True, "radius": 50, "allowed": "Drilling, LandingGearLock",
                "access": "Blacklist"}

    def after_leg(leg: str, holder: str) -> None:
        found = {node: [zone for zone in bench.cluster.node(node, "/block-test-safezones")["zones"] if zone["block"] == 0]
                 for node in [*_regular_nodes(bench), "wa-1"]}
        bench.record(f"zones-{leg}", {"holder": holder, **found})
        for node in _regular_nodes(bench):
            here = [{key: zone[key] for key in expected} for zone in found[node]]
            bench.check(f"{leg}: {node}{' (holds the partition)' if node == holder else ''} has the admin zone once, "
                        "with its settings", here == [expected], json.dumps(here))

    _journey(bench, fixtures, "Beacon", after_leg)
    bench.exercised(results=fixtures.results)


BLOCK_ZONE_GRID, BLOCK_ZONE_BLOCK, BLOCK_ZONE_ID = 72057594037930001, 72057594037930003, 72057594037930004
BLOCK_ZONE = f"""    <MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">
      <SubtypeName />
      <EntityId>{BLOCK_ZONE_GRID}</EntityId>
      <PersistentFlags>CastShadows InScene</PersistentFlags>
      <PositionAndOrientation>
        <Position x="{ORIGIN[0]}" y="{ORIGIN[1]}" z="{ORIGIN[2]}" />
        <Forward x="0" y="0" z="-1" />
        <Up x="0" y="1" z="0" />
      </PositionAndOrientation>
      <GridSizeEnum>Large</GridSizeEnum>
      <CubeBlocks>
        <MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_BatteryBlock">
          <SubtypeName>LargeBlockBatteryBlock</SubtypeName>
          <EntityId>72057594037930002</EntityId>
          <Enabled>true</Enabled>
          <CurrentStoredPower>3</CurrentStoredPower>
          <ProducerEnabled>true</ProducerEnabled>
          <MaxStoredPower>3</MaxStoredPower>
        </MyObjectBuilder_CubeBlock>
        <MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_SafeZoneBlock">
          <SubtypeName>SafeZoneBlock</SubtypeName>
          <EntityId>{BLOCK_ZONE_BLOCK}</EntityId>
          <Min x="0" y="1" z="0" />
          <ComponentContainer>
            <Components>
              <ComponentData>
                <TypeId>MySafeZoneComponent</TypeId>
                <Component xsi:type="MyObjectBuilder_SafeZoneComponent">
                  <UpkeepTime>3600000</UpkeepTime>
                  <Activating>false</Activating>
                  <ActivationTime>0</ActivationTime>
                </Component>
              </ComponentData>
              <ComponentData>
                <TypeId>MyInventoryBase</TypeId>
                <Component xsi:type="MyObjectBuilder_Inventory">
                  <Items>
                    <MyObjectBuilder_InventoryItem>
                      <Amount>5</Amount>
                      <PhysicalContent xsi:type="MyObjectBuilder_Component">
                        <SubtypeName>ZoneChip</SubtypeName>
                      </PhysicalContent>
                      <ItemId>0</ItemId>
                    </MyObjectBuilder_InventoryItem>
                  </Items>
                  <nextItemId>1</nextItemId>
                </Component>
              </ComponentData>
            </Components>
          </ComponentContainer>
          <Enabled>true</Enabled>
          <SafeZoneId>{BLOCK_ZONE_ID}</SafeZoneId>
        </MyObjectBuilder_CubeBlock>
      </CubeBlocks>
      <IsStatic>true</IsStatic>
      <DisplayName>SE1-0253 safe zone block</DisplayName>
    </MyObjectBuilder_EntityBase>
    <MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_SafeZone">
      <SubtypeName />
      <EntityId>{BLOCK_ZONE_ID}</EntityId>
      <PersistentFlags>CastShadows InScene</PersistentFlags>
      <PositionAndOrientation>
        <Position x="{ORIGIN[0] + 1.25}" y="{ORIGIN[1] + 3.75}" z="{ORIGIN[2] + 2.5}" />
        <Forward x="0" y="0" z="-1" />
        <Up x="0" y="1" z="0" />
      </PositionAndOrientation>
      <Radius>25</Radius>
      <Enabled>true</Enabled>
      <SafeZoneBlockId>{BLOCK_ZONE_BLOCK}</SafeZoneBlockId>
      <AccessTypePlayers>Whitelist</AccessTypePlayers>
      <AccessTypeFactions>Whitelist</AccessTypeFactions>
      <AccessTypeGrids>Whitelist</AccessTypeGrids>
      <AccessTypeFloatingObjects>Whitelist</AccessTypeFloatingObjects>
      <AllowedActions>Welding LandingGearLock</AllowedActions>
      <Factions />
      <Players />
      <Entities />
      <Shape>Sphere</Shape>
      <IsVisible>true</IsVisible>
      <Texture>SafeZone_Texture_Default</Texture>
    </MyObjectBuilder_EntityBase>"""


def p3_blk_08(bench) -> None:
    """A vanilla world's safe zone block with its zone, converted with ``to-split``: the zone exists once, on
    the block's node, with its settings from the first boot on, and after a handover, an offline round trip
    and a restart."""
    bench.cluster.config.world_template = _template_with(bench, "Cluster-Simple-block-zone", BLOCK_ZONE)
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    fixtures = Fixtures(bench, [{"key": "SafeZoneBlock", "grid": BLOCK_ZONE_GRID, "block": BLOCK_ZONE_BLOCK, "partners": []}])
    wait_settled(bench, fixtures.grids)
    _require(bench, "the safe zone block's grid has a partition", not fixtures.assign_partitions())
    bench.record("zones-boot-wa-1", bench.cluster.node("wa-1", "/block-test-safezones"))
    _journey(bench, fixtures, "SafeZoneBlock", _block_zone_checks(bench, fixtures, BLOCK_ZONE_BLOCK))
    bench.exercised(results=fixtures.results)


ADMIN_ZONE_2 = 72057594037928188


def _admin_zones(bench, node: str) -> dict[int, dict] | None:
    """The node's admin zones, None while it does not answer (a restarted node comes up after its log says ready)."""
    try:
        zones = bench.cluster.node(node, "/block-test-safezones")["zones"]
    except (ClusterError, OSError):
        return None
    return {zone["entityId"]: {key: zone[key] for key in ("radius", "enabled", "allowed")} for zone in zones if zone["block"] == 0}


def p3_blk_09(bench) -> None:
    """Admin safe zones changed in game: one created and edited on node-1, one from the world file edited on node-2
    and another deleted on node-1. Every node has the changed set, also after a restart of node-1, a replacement of
    the World Authority followed by a restart of node-2, and in the world ``to-vanilla`` writes."""
    far = (ORIGIN[0], ORIGIN[1], ORIGIN[2] + 20000)
    bench.cluster.config.world_template = _template_with(bench, "Cluster-Simple-admin-zones",
                                                         ADMIN_ZONE + "\n" + _admin_zone(ADMIN_ZONE_2, far, "SE1-0252 admin zone"))
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")

    def admin(node: str, op: str, **query) -> dict:
        return bench.cluster.node(node, "/block-test-admin-safezone?" + urlencode({"op": op, **query}), "POST")

    created = admin("node-1", "create", x=ORIGIN[0], y=ORIGIN[1], z=ORIGIN[2] + 40000)["zone"]
    admin("node-1", "update", id=created, radius=75, allowed="Drilling")
    admin("node-2", "update", id=ADMIN_ZONE_ID, radius=60, allowed="Drilling, LandingGearLock")
    admin("node-1", "delete", id=ADMIN_ZONE_2)
    expected = {ADMIN_ZONE_ID: {"radius": 60, "enabled": True, "allowed": "Drilling, LandingGearLock"},
                created: {"radius": 75, "enabled": True, "allowed": "Drilling"}}
    bench.exercised(created=created)

    def converged(leg: str) -> None:
        nodes = _regular_nodes(bench)

        def found() -> dict[str, dict]:
            return {node: _admin_zones(bench, node) for node in nodes}

        zones = _poll(lambda: (lambda z: z if all(z[n] == expected for n in nodes) else None)(found()), 60, 2) or found()
        bench.record(f"admin-zones-{leg}", zones)
        for node in nodes:
            bench.check(f"{leg}: {node} has the admin zones as changed in game", zones[node] == expected,
                        json.dumps(zones[node]))

    converged("changed")
    bench.cluster.restart_node("node-1")
    bench.cluster.wait_node_log("node-1", "Game ready", timeout=SERVER_TIMEOUT)
    converged("restart")
    _replace_authority(bench)
    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", timeout=SERVER_TIMEOUT)
    converged("authority")

    config = bench.cluster.config
    nodes = _nodes(bench)
    vanilla = bench.run_root / "vanilla-world"
    shutil.rmtree(vanilla, ignore_errors=True)
    converted = _world_tool(bench, "to-vanilla", config.split_world, vanilla,
                            *(config.node_data(nodes[node]) for node in ("node-1", "node-2", "wa-1")))
    bench.record("to-vanilla.txt", converted.stdout + converted.stderr)
    _require(bench, "to-vanilla converted the cluster state", converted.returncode == 0, converted.stderr[-300:])
    zones = {int(entity_id): float(element.findtext("Radius")) for entity_id, kind, element in
             _sector_objects(vanilla / "SANDBOX_0_0_0_.sbs")
             if kind == "MyObjectBuilder_SafeZone" and element.findtext("SafeZoneBlockId") == "0"}
    bench.check("vanilla: the converted world has the admin zones as changed in game",
                zones == {zone: row["radius"] for zone, row in expected.items()}, json.dumps(zones))


def p3_blk_04_race(bench) -> None:
    """#1269 (race F27): N's Take admitted with its answer dropped, N dies before the fetch, the fetch against the
    expiry - the ship is listed again in each case, and without N's Release (the known positive inside the section)
    it stays unlisted."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "a take whose answer was lost or whose node died is released", "ship-storage-takes")


def p3_blk_04_retry(bench) -> None:
    """#1317: Store, Order and Delete applied with their answers dropped - the node and the World Authority agree after
    the outbox (Unstore, the same operation id), and without it (the known positives inside the section) they do not."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "a lost ship storage answer leaves the node and the WA agreeing", "ship-storage-retry")


STRANDED_AFTER = 600   # ShipStorageService.FetchedTakeReport


def p3_blk_04_admin(bench) -> None:
    """#1316: an admin settles a stranded ship storage take. Two ships are stored at the terminal, and the node takes
    and fetches each at the World Authority without pasting or consuming it (test route: the node died after the fetch).
    Both are then unlisted and the Gateway's GET /admin/v1/ship-storage/takes shows them, not stranded. A settle now
    (red: an admin could release a take whose node may still be pasting) and one with another take id are refused.
    Once stranded (10 min after the take) the WA logs each once; an admin release lists ship-1 again and it is
    retrieved at the terminal, an admin consume drops ship-2, and the list is empty."""
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    owner = _identity(bench, "s1316-storage")
    fixtures = _storage_fixtures(bench, ["ship-1", "ship-2"], owner)
    terminal = fixtures.rows[TERMINAL]["block"]
    node = _all_partitions(bench)[fixtures.partition[TERMINAL]]
    cluster = bench.cluster
    report: dict = {"node": node}

    def listed(name: str) -> list[str]:
        return [row["id"] for row in cluster.node(node, "/block-test-stored-grids")["grids"] if row["name"] == name]

    def takes() -> dict:
        return {row["id"]: row for row in cluster._admin_data(cluster.get("/admin/v1/ship-storage/takes"))["takes"]}

    def settle(ship: str, take: str, action: str) -> dict:
        return cluster._admin_data(cluster._request("POST", f"/admin/v1/ship-storage/takes/{ship}/settle",
                                                    body={"take": take, "action": action},
                                                    headers={"Idempotency-Key": f"s1316-{ship}-{take}-{action}-{time.time()}"}))

    ids = {}
    for name in ("ship-1", "ship-2"):
        request = cluster.node(node, f"/block-test-store-grid?terminal={terminal}&grid={fixtures.rows[name]['grid']}"
                                     f"&owner={owner}", "POST")["request"]
        _require(bench, f"{name} is stored at the terminal on {node}", _request_result(bench, node, request) == "Success")
        fixtures.rows.pop(name)
        ids[name] = _poll(lambda name=name: (listed("blk-" + name) or [None])[0], 60, 1)
        _require(bench, f"{name} is listed on {node}", ids[name] is not None)
    _require(bench, "the admin list holds no take before the test", not takes(), json.dumps(takes()))

    strands = {name: cluster.node(node, f"/block-test-strand-take?id={ids[name]}", "POST") for name in ids}
    report["strands"] = strands
    _require(bench, "both ships are taken and their blueprints fetched at the World Authority",
             all(row.get("blueprintChars", 0) > 0 for row in strands.values()), json.dumps(strands))
    gone = _poll(lambda: not listed("blk-ship-1") and not listed("blk-ship-2") or None, 60, 1)
    bench.check("the taken ships are unlisted at the terminal", bool(gone))
    held = takes()
    report["held"] = held
    bench.check("the admin list shows both takes as held by the node, fetched and not stranded yet",
                all(held.get(ids[name], {}).get("take") == strands[name]["take"] and held[ids[name]]["takenBy"] == node
                    and held[ids[name]]["fetched"] and not held[ids[name]]["stranded"] for name in ids), json.dumps(held))
    bench.check("the admin list names each ship", all(held.get(ids[name], {}).get("name") == "blk-" + name for name in ids),
                json.dumps(held))
    early = settle(ids["ship-1"], strands["ship-1"]["take"], "release")
    report["early"] = early
    bench.check("a settle before the take is stranded is refused (take_not_settled) and ship-1 stays unlisted",
                early.get("state") == "Failed" and (early.get("error") or {}).get("code") == "take_not_settled"
                and not listed("blk-ship-1"), json.dumps(early))

    # Stranded 10 min after the take; the WA's expiry sweep runs every 5 s.
    started = time.monotonic()
    stranded = _poll(lambda: (lambda rows: rows if all(rows.get(ids[name], {}).get("stranded") for name in ids) else None)(
        takes()), STRANDED_AFTER + 120, 10)
    report["strandedAfterSeconds"] = round(time.monotonic() - started)
    _require(bench, "both takes are stranded in the admin list", stranded is not None, json.dumps(takes()))
    wa = next(spec.node_id for spec in cluster.config.nodes if spec.role == "WA")
    for name in ids:
        report[f"log-{name}"] = cluster.wait_node_log(wa, f"ship storage take stranded id={ids[name]}", 30)
    wrong = settle(ids["ship-1"], "7e570000-1316-4000-8000-0000000000ff", "release")
    bench.check("a settle with another take id than the one held is refused",
                wrong.get("state") == "Failed" and (wrong.get("error") or {}).get("code") == "take_not_settled", json.dumps(wrong))

    released = settle(ids["ship-1"], strands["ship-1"]["take"], "release")
    report["release"] = released
    bench.check("the admin release of ship-1 succeeds", released.get("state") == "Succeeded", json.dumps(released))
    back = _poll(lambda: listed("blk-ship-1") or None, 60, 1)
    bench.check("ship-1 is listed at the terminal again", back == [ids["ship-1"]], json.dumps(back))
    request = cluster.node(node, f"/block-test-retrieve-grid?terminal={terminal}&id={ids['ship-1']}&owner={owner}",
                           "POST")["request"]
    bench.check("ship-1 is retrieved at the terminal after the release", _request_result(bench, node, request) == "Success")
    again = settle(ids["ship-1"], strands["ship-1"]["take"], "consume")
    bench.check("a second settle of the released take is refused", again.get("state") == "Failed", json.dumps(again))

    consumed = settle(ids["ship-2"], strands["ship-2"]["take"], "consume")
    report["consume"] = consumed
    bench.check("the admin consume of ship-2 succeeds", consumed.get("state") == "Succeeded", json.dumps(consumed))
    time.sleep(5)
    bench.check("ship-2 stays unlisted after the consume", not listed("blk-ship-2"))
    bench.check("the admin list is empty after both settles", not takes(), json.dumps(takes()))
    report["logReleased"] = cluster.wait_node_log(wa, f"ship storage admin released stranded take id={ids['ship-1']}", 30)
    report["logConsumed"] = cluster.wait_node_log(wa, f"ship storage admin consumed stranded take id={ids['ship-2']}", 30)
    bench.record("admin", report)


def p3_blk_09_race(bench) -> None:
    """#1273 (race F31): A's Set answered late, B's Remove, A's retry - the zone stays removed, and a new
    operation id per attempt (the known positive inside the section) brings it back."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "a retried change keeps its operation id; the ledger replays it", "admin-safezone-retry")


SCENARIOS_BLOCKS = {
    "P3-BLK-01": (p3_blk_01, "every vanilla functional block type across a handover and back", "3"),
    "P3-BLK-02": (p3_blk_02, "every vanilla functional block type through an offline round trip", "3"),
    "P3-BLK-03": (p3_blk_03, "every vanilla functional block type through a save and node restart", "4"),
    "P3-BLK-04": (p3_blk_04, "Services terminal ship storage across nodes", "3"),
    "P3-BLK-04-RACE": (p3_blk_04_race, "#1269: a lost or orphaned ship take is released", "1"),
    "P3-BLK-04-RETRY": (p3_blk_04_retry, "#1317: a lost ship storage answer leaves the node and the WA agreeing", "1"),
    "P3-BLK-04-ADMIN": (p3_blk_04_admin, "#1316: an admin releases or consumes a stranded ship take", "1"),
    "P3-BLK-04-DURABLE": (p3_blk_04_durable, "#1270: a ship storage answer waits for the global save; a WA kill neither "
                                             "loses nor duplicates the ship", "3"),
    "P3-BLK-05": (p3_blk_05, "the safe zone entity travels with its block", "3"),
    "P3-BLK-06": (p3_blk_06, "a contract block's contracts across nodes", "3"),
    "P3-BLK-07": (p3_blk_07, "an admin safe zone on every node across a boot, a handover, offline and a restart", "3"),
    "P3-BLK-08": (p3_blk_08, "a safe zone block's zone from a converted vanilla world, from the first boot on", "3"),
    "P3-BLK-09": (p3_blk_09, "admin safe zones changed in game on every node, after restarts and in to-vanilla", "3"),
    "P3-BLK-09-RACE": (p3_blk_09_race, "#1273: a retried admin safe zone change cannot undo a newer one", "1"),
    "P3-BLK-10": (p3_blk_10, "Services terminal retrieval orders, auto-deploy and combat cooldown across nodes", "3"),
    "P3-BLK-11": (p3_blk_11, "MagnetarWorld to-vanilla exports the ships stored at Services terminals", "3"),
}


if __name__ == "__main__":
    wanted, skipped = in_scope(block_catalog())
    for key, row in wanted.items():
        print(f"{key:45} {FAMILY.get(key, 'UNCOVERED'):10} {row['typeId']}/{row['subtype']} ({row['size']})")
    print(len(wanted), "to spawn;", len(skipped), "skipped")
