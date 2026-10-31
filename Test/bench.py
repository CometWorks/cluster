#!/usr/bin/env python3
"""The scenario bench: fixtures, standing audits, the movement probe, and artifact recording.

TestPlan.md §1.2 shape - a scenario builds a fixture, drives it through the client-facing verbs,
asserts against structured state, and archives everything it looked at. Everything here exists
because the Phase 3 flight lost time to its absence:

* ``move_probe`` measures the character **server-side under a raw held key**. ``move-to`` measures the
  client's predicted position, which moves whether or not the server accepted a single packet, and it
  reported "moved 38.1 m of 40" for ten rounds on a character the server was ignoring.
* Every probe asserts **gameplay focus at the moment of measurement** and returns "could not run"
  rather than a zero. A focused GUI screen eats every injected key and is indistinguishable from the
  stuck-character bug; two "live reproductions" were invalid because of it.
* A probe with no **positive control** is not a measurement, so the bench refuses to report a failure
  for a probe kind that has never passed in the same session.
* Every check records what it actually exercised. Three consecutive clean runs meant nothing once it
  turned out no cycle had crossed a node.
"""

from __future__ import annotations

import json
import math
import shutil
import sys
import time
import traceback
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import hostenv  # noqa: E402

hostenv.activate(hostenv.HOST)

from clients import REMOTE_PASSWORD, REMOTE_USER, Client, ClientError  # noqa: E402
from cluster import Cluster, ClusterError, Config, wait_until  # noqa: E402  (the package's cli/cluster.py)
from se_remote import RemoteAPI  # noqa: E402
import testplugin  # noqa: E402

HOST = hostenv.HOST
#: The test utility plugin's role folders, once built (None: disabled or not yet built).
TEST_PLUGIN: dict | None = None
_TEST_PLUGIN_READY = False
#: The Remote API port the scenarios name for their first client; mapped onto the host's
#: CLUSTER_TEST_REMOTE_PORT_BASE so the interactive singleton (24158) stays free.
LEGACY_REMOTE_PORT = 24158

GAMEPLAY_SCREEN = "MyGuiScreenGamePlay"


def complete_png(path: Path) -> bool:
    """Remote's save endpoint queues a render-thread write; a returned path is not a screenshot yet."""
    try:
        with path.open("rb") as stream:
            if stream.read(8) != b"\x89PNG\r\n\x1a\n":
                return False
            stream.seek(-12, 2)
            return stream.read() == b"\x00\x00\x00\x00IEND\xaeB`\x82"
    except OSError:
        return False

# Each leg's key. TestPlan §1.2 asks for "a short walk (when standing on a surface) and a jetpack
# burst" - and a jetpack burst is Space, not W. Measured on a live client: from rest, a freshly
# spawned character in deep space travels 0.019 m under a 4 s W hold and 169.3 m under Space. Once it
# is moving, W/A/S/D/C all read the same distance, so a probe that holds W and calls the zero a stuck
# character is measuring the wrong control.
LEG_KEYS = {"walk": "W", "jetpack": "Space"}
JETPACK_KEY = "X"  # not J - J is the helmet


@dataclass
class Check:
    label: str
    passed: bool
    detail: str = ""
    evidence: str | None = None


@dataclass
class ScenarioResult:
    scenario: str
    title: str
    tier: str
    passed: bool
    checks: list[Check] = field(default_factory=list)
    artifacts: list[str] = field(default_factory=list)
    error: str | None = None
    seconds: float = 0.0
    exercised: dict = field(default_factory=dict)
    directory: str = ""  # the evidence directory under the run's results root


class Bench:
    """One scenario's run: its cluster, its artifact directory and its assertion record."""

    def __init__(self, scenario: str, title: str, tier: str, results_root: Path, keep_root: bool = False,
                 regular_nodes: int = 2, world_template: Path | None = None, split_step: str = "to-split",
                 directory: str | None = None, administrators: list[int] | None = None,
                 node_control: bool = False):
        self.scenario = scenario
        self.title = title
        self.tier = tier
        self.dir = results_root / (directory or scenario)
        self.dir.mkdir(parents=True, exist_ok=True)
        # One run root per scenario under the host's test root; a previous run's root is wiped
        # before the cluster starts (prepare), never while something runs in it.
        self.run_root = HOST.test_root / scenario.lower()
        # Process logs of an earlier run of this scenario were copied into its evidence at close();
        # left here they would be read (and counted) as this run's lines.
        if self.run_root.is_dir():
            for stale in [*self.run_root.glob("*.log"), *self.run_root.glob("*.log.*")]:
                if stale.is_file():
                    stale.unlink()
        global TEST_PLUGIN, _TEST_PLUGIN_READY
        if not _TEST_PLUGIN_READY:  # before Config reads CLUSTER_EXTRA_PLUGINS
            TEST_PLUGIN = testplugin.install(HOST)
            _TEST_PLUGIN_READY = True
        self.result = ScenarioResult(scenario, title, tier, passed=True, directory=directory or scenario)
        # T-0320: node control (hosts file, fresh node ids at every start, a launcher that must keep
        # running) stays OFF unless a scenario declares `node_control = True`: the scenarios name
        # node-1.., restart crashed nodes under the same id and warm-restart the same node set, all of
        # which the registry refuses once node control is active (node_id_retired / node_not_planned).
        # Off, the registry never activates it (no hosts file, no runtime target) and accepts nodes
        # outside a plan exactly as before.
        self.cluster = Cluster(Config(root=self.run_root, regular_nodes=regular_nodes,
                                      world_template=world_template, split_step=split_step,
                                      steam_listen=None, administrators=list(administrators or []),
                                      node_control=node_control))
        self.apis: dict[int, RemoteAPI] = {}
        self.clients: dict[object, Client] = {}
        self._positive_control: set[str] = set()
        self.keep_root = keep_root

    # -- recording ---------------------------------------------------------

    def record(self, name: str, data) -> Path:
        path = self.dir / (name if name.endswith((".json", ".txt", ".md")) else name + ".json")
        if isinstance(data, (dict, list)):
            path.write_text(json.dumps(data, indent=2, default=str))
        else:
            path.write_text(str(data))
        self.result.artifacts.append(path.name)
        return path

    def check(self, label: str, passed: bool, detail: str = "", evidence: str | None = None) -> bool:
        self.result.checks.append(Check(label, bool(passed), detail, evidence))
        if not passed:
            self.result.passed = False
        print(f"  [{'PASS' if passed else 'FAIL'}] {label}" + (f" - {detail}" if detail else ""))
        return bool(passed)

    def exercised(self, **facts) -> None:
        """What this cycle actually did - node route, handover count, probes run (§1.2 rule 5)."""
        self.result.exercised.update(facts)

    def screenshot(self, client_id: int, name: str) -> str | None:
        api = self.apis.get(client_id)
        if api is None:
            return None
        path = self.dir / f"{name}.png"
        try:
            api.screenshot_save(str(path))
            wait_until(lambda: complete_png(path), 10, f"complete render-thread screenshot {name}", interval=0.1)
        except Exception as error:  # a screenshot is evidence, never a gate
            print(f"  (screenshot {name} failed: {error})")
            return None
        self.result.artifacts.append(path.name)
        return path.name

    # -- lifecycle ---------------------------------------------------------

    def start_cluster(self, nodes: list[str] | None = None) -> None:
        """Boot the cluster. ``nodes`` starts only some of them - a check that only needs one node
        should not pay for three."""
        self.cluster.kill()
        self.cluster.prepare(wipe=True)   # a bench run root is always reset
        self.cluster.start(nodes)
        self.cluster.wait_ready(nodes=nodes)

    def remote_port(self, port: int) -> int:
        """The scenarios name ports as 24158 + n; the bench runs them at the host's base + n."""
        return HOST.remote_port_base + (port - LEGACY_REMOTE_PORT) if port >= LEGACY_REMOTE_PORT else port

    def launch_client(self, client_id: int, remote_port: int = LEGACY_REMOTE_PORT, label: str | None = None,
                      render: bool | None = None, name: str | None = None) -> Client:
        """Start a client process without waiting for anything. ``label`` keeps two processes that
        assert the SAME identity out of each other's data directory and log - the duplicate-join
        leg, and any rejoin after a kick. ``render=False`` runs it without a back-buffer."""
        key = label or client_id
        previous = self.clients.pop(key, None)
        if previous is not None:
            previous.stop()
        client = Client(self.run_root, client_id, self.remote_port(remote_port), self.cluster.config.gateway_listen,
                        name=name, label=label, render=render)
        client.start()
        self.clients[key] = client
        return client

    def kill_client(self, key) -> None:
        """Stop one client (by client id, or by the label it was started with)."""
        client = self.clients.pop(key, None)
        if client is not None:
            client.stop()

    def start_client(self, client_id: int, remote_port: int = LEGACY_REMOTE_PORT, timeout: float = 420,
                     label: str | None = None, render: bool | None = None, name: str | None = None) -> RemoteAPI:
        client = self.launch_client(client_id, remote_port, label=label, render=render, name=name)
        client.wait_api_listening(timeout)
        api = RemoteAPI(client.url, username=REMOTE_USER, password=REMOTE_PASSWORD, timeout=180)
        api.wait_for_api(max_wait=timeout)
        self.apis[client_id] = api
        return api

    def living_character(self, client_id: int) -> dict | None:
        try:
            character = self.apis[client_id].get_character()
        except Exception:
            return None
        return character if character and character.get("state") != "dead" else None

    def wait_spawned(self, client_id: int, timeout: float = 240) -> dict:
        """Take the client through the vanilla respawn flow to a living character.

        Deliberately the vanilla path, not a screen dismissal: closing ``MyGuiScreenMedicals`` leaves
        the player characterless, and ``onRespawnClick`` refuses silently until BOTH a respawn row is
        selected and the local identity has replicated. Selecting the row through the control's own
        event is what enables the button - a plain field write leaves the screen's selection state
        untouched, and both refusals look like a successful click over HTTP.
        """
        api = self.apis[client_id]
        deadline = time.time() + timeout
        last = {}
        attempt = 0
        while time.time() < deadline:
            character = self.living_character(client_id)
            if character:
                return character
            try:
                screens = api.list_screens()
            except Exception:
                time.sleep(1)
                continue
            medicals = [screen for screen in screens if screen.get("type") == "MyGuiScreenMedicals"]
            if not medicals:
                time.sleep(1)
                continue
            index = medicals[0]["index"]
            try:
                controls = api.get_controls(index, depth=10) or []
                texts = {control.get("properties", {}).get("text") for control in controls}
                if "Join" in texts:
                    api.control_click(text="Join", screen=index)
                    time.sleep(2)
                    continue
                last = api.get_respawn_state()
                if last.get("identityPresent") and last.get("rowsCount", 0) > 0:
                    table = next((control["name"] for control in controls
                                  if control.get("type") == "MyGuiControlTable"
                                  and control.get("properties", {}).get("rowsCount", 0) > 0), None)
                    if table:
                        # Rotate through the rows across retries: a respawn-point row can leave the
                        # button disabled (cooldown / someone else's med bay) while the suit row works,
                        # and which row lands first depends on replication timing.
                        api.control_set(table, attempt % max(1, int(last.get("rowsCount", 1))), screen=index)
                        attempt += 1
                    api.control_click(text="Respawn", screen=index)
            except Exception as error:
                last = {"error": str(error)}
            time.sleep(2)
        self.record(f"respawn-state-{client_id}", last)
        raise ClusterError(f"client {client_id} did not complete the vanilla respawn flow (last={last})")

    def dismiss_blocking_screens(self, client_id: int) -> list[str]:
        """Close the screens that hold input focus. A focused screen mimics the stuck-character bug."""
        api = self.apis[client_id]
        closed = []
        try:
            screens = api.list_screens()
        except Exception:
            return closed
        for screen in screens:
            kind = screen.get("type", "")
            # Never force-close MyGuiScreenLoading: the client is still building its session behind it
            # and deleting it leaves the client with no local player at all. Never MyGuiScreenMedicals
            # either - closing it leaves the player characterless; it exits through wait_spawned's
            # vanilla respawn flow.
            if kind in ("MyGuiScreenGamePlay", "MyGuiScreenLoading", "MyGuiScreenHudSpace",
                        "MyGuiScreenMedicals"):
                continue
            try:
                api.close_screen(screen["index"])
                closed.append(kind)
            except Exception:
                pass
        return closed

    def _wait_loaded(self, client_id: int, timeout: float = 120) -> bool:
        """Wait out MyGuiScreenLoading.

        Never close it: the client is still building its session behind it, and deleting it leaves the
        client with no local player at all. It also holds focus, so measuring through it reads as a
        stuck character.
        """
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                if not any(screen.get("type") == "MyGuiScreenLoading"
                           for screen in self.apis[client_id].list_screens()):
                    return True
            except Exception:
                pass
            time.sleep(2)
        return False

    def focus(self, client_id: int) -> str:
        """What holds input focus right now. Named, because 'no focus' and 'a dialog has focus' are
        different failures and both read as a dead character if the probe only returns a boolean."""
        try:
            return self.apis[client_id].get_focused_screen().get("type", "unknown")
        except Exception as error:
            return f"none ({type(error).__name__})"

    def ensure_gameplay_focus(self, client_id: int, attempts: int = 5) -> bool:
        """Close what holds focus, and keep closing: the welcome/GDPR stack re-presents itself, so a
        single dismissal often leaves the next screen focused and every injected key is eaten."""
        for _ in range(attempts):
            self.dismiss_blocking_screens(client_id)
            if self.has_gameplay_focus(client_id):
                return True
            time.sleep(2)
        return self.has_gameplay_focus(client_id)

    def has_gameplay_focus(self, client_id: int) -> bool:
        # The HUD sits on top of gameplay and takes focus without taking input; anything else does.
        return self.focus(client_id) in (GAMEPLAY_SCREEN, "MyGuiScreenHudSpace")

    def close(self) -> None:
        for api in self.apis.values():
            try:
                api.clear_input_state()
            except Exception:
                pass
            try:
                api.close()
            except Exception:
                pass
        for client in list(self.clients.values()):
            client.stop()
        self.clients.clear()
        self.cluster.kill()
        # Logs are evidence: copy them before the run root is reused. The game logs of every
        # client and the Magnetar log of every node live one level down.
        logs = sorted(self.run_root.glob("*.log")) + sorted(self.run_root.glob("client-*/home/.config/SpaceEngineers/SpaceEngineers_*.log")) \
            + sorted(self.run_root.glob("*-magnetar/info.log")) + sorted(self.run_root.glob("*.log.*")) \
            + sorted(self.run_root.glob("*-data/SpaceEngineersDedicated_*.log")) \
            + sorted(self.run_root.glob("*-data-restart/SpaceEngineersDedicated_*.log"))
        for log in logs:
            name = log.name if log.parent == self.run_root else f"{log.relative_to(self.run_root).parts[0]}-{log.name}"
            try:
                (self.dir / name).write_bytes(log.read_bytes()[-2_000_000:])
                self.result.artifacts.append(name)
            except OSError:
                pass
        dumps = sorted(self.run_root.glob("dumps/*"))
        if dumps:
            self.record("crash-dumps.txt", "\n".join(str(dump) for dump in dumps))

    # -- structured state --------------------------------------------------

    def diag(self, depth: str = "entities", **filters) -> dict:
        return self.cluster.diag(depth, **filters)

    def sections(self, snapshot: dict) -> dict[str, dict]:
        """Node id -> its node-local section, skipping nodes reported stale."""
        return {node["nodeId"]: node["section"] for node in snapshot["nodes"] if node.get("section")}

    def quiesce(self, timeout: float = 60) -> dict:
        """Barrier on no in-flight handovers, so cross-node facts are class E (TestPlan §1.3)."""
        def settled():
            snapshot = self.diag("partitions")
            incomplete = snapshot["registry"]["handovers"]
            return snapshot if not incomplete else None

        return wait_until(settled, timeout, "no in-flight handovers")

    def entity_owner(self, snapshot: dict, entity_id: int) -> list[tuple[str, int]]:
        """Every (node, partition) that claims an entity. More than one violates invariant 1."""
        found = []
        for node_id, section in self.sections(snapshot).items():
            for partition in section["partitions"]:
                for entity in partition["entities"] or []:
                    if entity["entityId"] == entity_id:
                        found.append((node_id, partition["partitionId"]))
        return found

    # -- standing audits (Partitioning.md §10) -----------------------------

    def standing_audits(self, snapshot: dict, label: str = "standing audits") -> bool:
        sections = self.sections(snapshot)
        ok = True

        # 1. Single owner: an entity belongs to exactly one partition on one node.
        seen: dict[int, list[str]] = {}
        for node_id, section in sections.items():
            for partition in section["partitions"]:
                for entity in partition["entities"] or []:
                    seen.setdefault(entity["entityId"], []).append(f"{node_id}/{partition['partitionId']}")
        duplicated = {entity: places for entity, places in seen.items() if len(places) > 1}
        ok &= self.check(f"{label}: single owner (invariant 1)", not duplicated,
                         f"{len(duplicated)} entity ids in more than one partition: {list(duplicated)[:5]}")

        # 3. ID uniqueness: no partition id claimed by two nodes.
        owners: dict[int, list[str]] = {}
        for node_id, section in sections.items():
            for partition in section["partitions"]:
                owners.setdefault(partition["partitionId"], []).append(node_id)
        contested = {pid: nodes for pid, nodes in owners.items() if len(nodes) > 1}
        ok &= self.check(f"{label}: one owner per partition (invariant 3)", not contested, str(contested))

        # 7. Character uniqueness: one body per identity, cluster-wide.
        bodies: dict[int, list[str]] = {}
        for node_id, section in sections.items():
            for character in section["characters"]["characters"]:
                identity = character["savedFor"] or character["identityOwner"]
                if identity:
                    bodies.setdefault(identity, []).append(f"{node_id}/{character['entityId']}")
        doubled = {identity: places for identity, places in bodies.items() if len(set(places)) > 1}
        ok &= self.check(f"{label}: one character per identity (invariant 7)", not doubled, str(doubled))

        # 8. Control liveness: an attached client's controlled entity has a bound controller here.
        unbound = []
        for node_id, section in sections.items():
            for client in section["clients"]:
                if client["controlledEntityId"] and not client["controllerBound"]:
                    unbound.append(f"{node_id}/{client['clientId']}")
        ok &= self.check(f"{label}: control liveness (invariant 8)", not unbound, str(unbound))
        return ok

    # -- the movement probe ------------------------------------------------

    def move_probe(self, client_id: int, legs: tuple[str, ...] | None = None,
                   hold_seconds: float = 4.0, minimum_metres: float = 1.5,
                   attempts_per_leg: int = 4) -> dict:
        """TestPlan §1.2 ``moveProbe``: does the character respond to input, measured on the server?

        Returns ``{"ran": bool, "legs": {...}}``. ``ran=False`` means the probe could not run - a
        focus loss or a missing character - which is reported as such and never as a zero distance.
        """
        api = self.apis[client_id]
        report: dict = {"ran": False, "reason": None, "legs": {}}
        try:
            character = api.get_character()
        except Exception as error:
            report["reason"] = f"no character: {error}"
            return report
        entity_id = self._server_character(client_id)
        if entity_id is None:
            report["reason"] = "the server reports no character for this client"
            return report
        if character.get("state") == "dead":
            report["reason"] = "character is dead"
            return report
        # TestPlan §1.2: "a short walk (when standing on a surface) and a jetpack burst". In deep space
        # there is no surface, so asserting a walk leg there measures nothing and fails a healthy
        # character. Derive the legs from what the character is actually doing unless told otherwise.
        if legs is None:
            legs = ("walk", "jetpack") if character.get("state") in ("standing", "crouching") else ("jetpack",)
        report["legsRequested"] = list(legs)

        if not self._wait_loaded(client_id):
            report["reason"] = f"the session was still loading: {self.focus(client_id)}"
            return report
        for leg in legs:
            attempts = []
            for attempt in range(attempts_per_leg):
                closed = self.dismiss_blocking_screens(client_id)
                if not self.has_gameplay_focus(client_id):
                    attempts.append({"attempt": attempt, "blocked": self.focus(client_id), "closed": closed})
                    time.sleep(2)
                    continue
                if leg == "jetpack" and not api.get_character().get("jetpack"):
                    api.key(JETPACK_KEY)
                    time.sleep(1.0)
                # Record what the character actually was at the moment of measurement. A jetpack that
                # would not come on makes Space a no-op in zero g, and the resulting zero is
                # indistinguishable from a stuck character unless this is written down.
                before_character = api.get_character()
                if self._wait_at_rest(entity_id) is None:
                    attempts.append({"attempt": attempt, "notAtRest": self._server_speed(entity_id)})
                    continue
                before = self._server_position(entity_id)
                # mode=override replaces the game's own input for the hold; without it the injected
                # key is merged with what the game believes is pressed and the character does not move.
                api.set_input_state(keys=[LEG_KEYS[leg]], mode="override")
                time.sleep(hold_seconds)
                focused = self.has_gameplay_focus(client_id)
                after = self._server_position(entity_id)
                api.clear_input_state()
                time.sleep(0.5)
                if before is None or after is None:
                    report["reason"] = "the server stopped reporting the character mid-probe"
                    return report
                distance = math.dist(before, after)
                attempts.append({"attempt": attempt, "metres": round(distance, 3), "focusHeld": focused,
                                 "before": before, "after": after,
                                 "endSpeed": self._server_speed(entity_id),
                                 "character": {key: before_character.get(key)
                                               for key in ("state", "jetpack", "dampeners")},
                                 # The server's opinion beside the client's. `character` above is the
                                 # CLIENT's view, and when a transferred character stops moving these
                                 # two disagree - the client still shows the jetpack on while the
                                 # server simulates a body it believes is standing. Only this half
                                 # says why, and invariant 8 cannot see it either.
                                 "serverCharacter": self._server_census(entity_id)})
                if focused and distance >= minimum_metres:
                    break
            best = max((entry for entry in attempts if "metres" in entry),
                       key=lambda entry: entry["metres"], default=None)
            if best is None:
                report["reason"] = f"the probe never got a clean window on the {leg} leg: {attempts}"
                return report
            report["legs"][leg] = {**best, "moved": best["metres"] >= minimum_metres, "attempts": attempts}
        report["ran"] = True
        report["passed"] = all(leg["moved"] for leg in report["legs"].values())
        # A zero is only actionable with both halves of the control state next to it: the server's
        # own row (frozen? physics active? controller bound?) and what the client believes it steers.
        # Per Remote.md a character with no localPhysics answers the camera and ignores W/X/C while
        # the server's copy looks healthy - the two halves disagree, and the probe must say which.
        report["serverRow"] = self._server_row(entity_id)
        report["serverCensus"] = self._server_census(entity_id)
        try:
            view = api.get_view()
            report["clientView"] = {key: view.get(key) for key in
                                    ("controlledEntityId", "localCharacterId", "localPhysics",
                                     "localPhysicsEnabled", "localCharacterProxy", "localPhysicsStatic",
                                     "localSpeed", "firstPerson", "spectator")}
        except Exception as error:  # noqa: BLE001 - a missing diagnostic is not a probe failure
            report["clientView"] = {"error": str(error)}
        for leg, outcome in report["legs"].items():
            if outcome["moved"]:
                self._positive_control.add(leg)
        report["positiveControl"] = sorted(self._positive_control)
        return report

    def assert_move_probe(self, client_id: int, label: str, **kwargs) -> dict:
        report = self.move_probe(client_id, **kwargs)
        self.record(f"moveprobe-{label}", report)
        if not report["ran"]:
            self.check(f"moveProbe ({label})", False, f"probe could not run: {report['reason']}")
            return report
        for leg, outcome in report["legs"].items():
            if not outcome["moved"] and leg not in self._positive_control:
                # No positive control for this leg in this session: report it as unproven rather than
                # as a defect. A probe that has never passed is not evidence of anything.
                self.check(f"moveProbe {leg} ({label})", False,
                           f"{outcome['metres']} m - and this leg has never passed in this session, "
                           "so the probe itself is unproven")
            else:
                self.check(f"moveProbe {leg} ({label})", outcome["moved"], f"{outcome['metres']} m server-side")
        return report

    def _server_character(self, client_id: int) -> int | None:
        for section in self.sections(self.diag("partitions")).values():
            for client in section["clients"]:
                if client["clientId"] == client_id:
                    return client["characterEntityId"] or client["controlledEntityId"] or None
        return None

    # The fields that actually discriminate a stuck character. `PlayerStateService.Characters()` was
    # written for exactly this defect and rides in every diag section as `characters`; the entity row
    # carries none of it. `needsUpdate` is the one to watch - the freeze zeroes it and the restore
    # puts back whatever it captured, so a body can be thawed, physics-active and never updated
    # again, which is precisely "inert to a millimetre while every other field reads healthy".
    def _server_census(self, entity_id: int) -> dict | None:
        """The character census row for this entity, from whichever node holds it.

        The whole row, verbatim: a fixed key list silently dropped every field the census gained
        after the list was written (moveIndicator, framesSinceUpdate, havok), which is exactly the
        evidence the stuck-character hunt needed.
        """
        for section in self.sections(self.diag("partitions")).values():
            # `PlayerStateService.Characters()` returns {node, characters}, not a bare list.
            for row in (section.get("characters") or {}).get("characters") or []:
                if row.get("entityId") == entity_id:
                    return dict(row)
        return None

    def _server_row(self, entity_id: int) -> dict | None:
        snapshot = self.diag("entities", entity=entity_id)
        for section in self.sections(snapshot).values():
            for partition in section["partitions"]:
                for entity in partition["entities"] or []:
                    if entity["entityId"] == entity_id:
                        return entity
        return None

    def _server_position(self, entity_id: int) -> tuple[float, float, float] | None:
        row = self._server_row(entity_id)
        return tuple(row["position"]) if row and row.get("position") else None

    def _server_speed(self, entity_id: int) -> float | None:
        row = self._server_row(entity_id)
        if not row or not row.get("velocity"):
            return None
        return math.dist((0.0, 0.0, 0.0), row["velocity"])

    def _wait_at_rest(self, entity_id: int, timeout: float = 30, still: float = 0.5) -> float | None:
        """Coast to a stop before measuring.

        A character that is already drifting travels the same distance whatever key is held - five
        different keys each read exactly 169.379 m in the lab - so raw displacement over a fixed window
        is not a measurement of input at all unless the character starts at rest.
        """
        deadline = time.time() + timeout
        speed = None
        while time.time() < deadline:
            speed = self._server_speed(entity_id)
            if speed is not None and speed < still:
                return speed
            time.sleep(1)
        return None if speed is None or speed >= still else speed


def cli(bench: Bench, *args: str, shell: bool = False):
    """The package's cluster CLI on this bench's run root, as argv (or one shell line): the same
    front door an operator uses, with the environment hostenv exported."""
    import shlex
    argv = [sys.executable, str(HOST.package / "cli/cluster.py"), "--root", str(bench.run_root), *args]
    return " ".join(shlex.quote(part) for part in argv) if shell else argv


def survival_template(bench: Bench) -> Path:
    """The packaged Cluster-Simple world declared Survival: a copy under the run root with GameMode
    rewritten in both session files (the game reads Sandbox_config.sbc; Sandbox.sbc carries a
    second copy that must agree)."""
    source = Path(bench.cluster.config.world_template or HOST.package / "examples/Cluster-Simple")
    if source.name.endswith("-survival"):
        return source
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / f"{source.name}-survival"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        if path.is_file():
            path.write_text(path.read_text().replace("<GameMode>Creative</GameMode>", "<GameMode>Survival</GameMode>"))
    return target


def run_scenario(function, scenario: str, title: str, tier: str, results_root: Path,
                 directory: str | None = None) -> ScenarioResult:
    # A scenario that needs more than the default two regular nodes (or a different world, e.g.
    # the declared-Survival template) declares it as a function attribute
    # (`p3_clu_01.regular_nodes = 3`, `p3_hnd_01.world_template = ...`), so the registry tuple
    # keeps its shape. `administrators` lists Steam ids admitted as admins.
    bench = Bench(scenario, title, tier, results_root,
                  regular_nodes=getattr(function, "regular_nodes", 2),
                  world_template=getattr(function, "world_template", None),
                  split_step=getattr(function, "split_step", "to-split"), directory=directory,
                  administrators=getattr(function, "administrators", None),
                  node_control=getattr(function, "node_control", False))
    started = time.time()
    print(f"\n=== {scenario} - {title} ===")
    try:
        function(bench)
    except (KeyboardInterrupt, SystemExit) as error:
        # Teardown still records the run. An interrupted scenario must never leave a
        # default-success result.json, even if it had not reached its first assertion.
        bench.result.passed = False
        bench.result.error = f"{type(error).__name__}: interrupted before completion"
        raise
    except (ClusterError, AssertionError, Exception) as error:  # noqa: BLE001 - a scenario failure is data
        bench.result.passed = False
        bench.result.error = f"{type(error).__name__}: {error}"
        bench.record("traceback.txt", traceback.format_exc())
        print(f"  !! {bench.result.error}")
    finally:
        try:
            bench.close()
        except Exception as error:  # noqa: BLE001
            print(f"  (teardown: {error})")
        bench.result.seconds = round(time.time() - started, 1)
        bench.record("result.json", {
            "scenario": bench.result.scenario, "title": bench.result.title, "tier": bench.result.tier,
            "passed": bench.result.passed, "seconds": bench.result.seconds,
            "error": bench.result.error, "exercised": bench.result.exercised,
            "checks": [vars(check) for check in bench.result.checks],
        })
    return bench.result
