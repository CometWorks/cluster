"""Planet and conversion: the former run-cluster.sh lanes `voxel-broadcast`, `planet-journal`,
`voxel-drilling`, `planet-diff` and `conversion`, as native scenarios.

The planet lanes run on the Earth fixture (``HOST.planet_world_archive``), unpacked per scenario and
declared Creative: the planet legs were tuned against Creative-mode drilling, which every node's
auto-generated dedicated config used to force by silently overriding the world's settings at load.
The cluster now honors the premade world's own rules, so the scenario's copy of the world declares
that intent itself - the shared archive stays untouched for the client test flow.

* ``P2-VOXB``    - an ordered voxel cutout on node-1 is relayed through the World Authority to
  node-2 exactly once (client-free);
* ``P2-VOX-02``  - the voxel journal under chaos: replica convergence, WA compaction and cataloguing,
  a WA SIGKILL inside a held journal append and a replacement WA on the retained root, a node-to-WA
  partition, a raced total order, compaction racing appends, a node-2 restart that fetches the
  base, cross-node chat and GPS through the replacement WA, an asteroid, and the voxel catalog
  rebuilt from scanned roots after a registry wipe;
* ``P2-VOX-01``  - the dual-base drilling headline: two rendered clients drill real hand-drill cuts
  on two sites more than 3 km apart (one through a node-to-WA outage), the cuts converge and are
  compacted, the clients fly to each other's site across real route switches and observe the other
  tunnel, node-1 restarts onto the compacted base, and the WA pushes weather;
* ``P2-DIF-01``  - the planetary split representation reassembles semantically identical, every voxel
  storage byte-identical, and a stock server loads it with its grids intact (no cluster, no client);
* ``P2-CNV-01``  - vanilla -> split -> cluster run -> vanilla: a client mutation and the identity
  binding survive graceful shutdown and reassembly, and a stock server loads the result.

``start_standalone`` is the stock (non-clustered) dedicated server these last two need: the
package's Magnetar launcher with a profile that loads no cluster plugin. A stock server has no
cluster control endpoint, so the grids it loaded come from the vanilla dedicated-server Remote API:
the session is saved (``PATCH /vrageremote/v1/session``) and every grid's block count in the save is
compared with the baseline, as the old ``/cluster/export`` comparison did. The root-grid listing
(``/v1/session/grids``) is recorded as evidence only - it folds subgrids into their root.

The converted checkpoint's player table does not name clients by id: SE writes ``<ClientId>0``
with a ``<HashedId>`` in saves, so the identity binding is checked as a player row carrying the
identity the Gateway granted the client (T-0164, which was this check's old raw-id search).
"""

from __future__ import annotations

import base64
import email.utils
import hashlib
import hmac
import json
import math
import os
import random
import re
import secrets
import shutil
import signal
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import magnetar_config
from bench import HOST
from clients import REMOTE_PASSWORD, REMOTE_USER, Client
from cluster import CRASH_ENV, MAGNETAR_GITHUB_CACHE, ClusterError, port_open, share_github_cache, split_endpoint, \
    wait_until
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _grid_ops, _handover_url, _join, _lines, _nodes, _owners, \
    _partitions, _wait_line
from scenarios_recovery import RECOVERY_TIMEOUT
from se_remote import RemoteAPI

CLIENT2_ID = 1234567891
# Arc between the two drill sites: beyond the 12 km split distance (4 x sync distance, T-0319), so the
# clients stay in separate partitions until one flies to the other site.
SITE_ARC_M = 15000
#: Planet lanes load a 120 km planet: 300 s server timeouts instead of 180.
SERVER_TIMEOUT = 300
CLIENT_TIMEOUT = 75
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
VOXEL_HIT = re.compile(r"Voxel|Planet")


# -- shared plumbing ------------------------------------------------------------------------


# 400 characters truncated the one detail that mattered: the chat traversal reports a line per node
# and the third node's row fell off the end. A detail is only read when something already failed.
DETAIL_LIMIT = 1800


def _require(bench, label: str, passed, detail: str = "") -> None:
    """A check the shell lane aborted on: everything after it depends on it."""
    if not bench.check(label, bool(passed), detail[:DETAIL_LIMIT]):
        raise ClusterError(f"{label}: {detail[:DETAIL_LIMIT]}")


def _poll(predicate, timeout: float, interval: float = 1.0):
    """The shell lanes' `for second < N ... && break` loop: the predicate's value, or None."""
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def _try(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def _node(bench, node_id: str, path: str, method: str = "GET", timeout: float = 40):
    return bench.cluster.node(node_id, path, method=method, timeout=timeout)


def _query(storage: str, x: int, y: int, z: int, size: int) -> str:
    return f"storage={urllib.parse.quote(storage, safe='')}&x={x}&y={y}&z={z}&size={size}"


def _hash(bench, node_id: str, query: str, timeout: float = 40) -> str | None:
    return _try(lambda: _node(bench, node_id, f"/voxel-range-hash?{query}", timeout=timeout).get("hash") or None)


def _write(bench, node_id: str, query: str, content: int, material: int) -> dict:
    return _node(bench, node_id, f"/voxel-test-write?{query}&content={content}&material={material}", "POST")


def _journal(bench, node_id: str) -> dict:
    return _try(lambda: _node(bench, node_id, "/voxel-journal-status"), {}) or {}


def _outbox(status: dict) -> dict:
    return {"outboundQueued": status.get("outboundQueued"), "outboxDrops": status.get("outboxDrops")}


def _outbox_clear(status: dict) -> bool:
    return status.get("outboundQueued") == 0 and status.get("outboxDrops") == 0


def _earthlike(bench) -> tuple[str, list[float], int]:
    """The EarthLike storage on node-1: (name, planet center, voxel size)."""
    storages = _node(bench, "node-1", "/voxel-storages")
    bench.record("voxel-storages", storages)
    row = next((row for row in storages if str(row.get("storage", "")).startswith("EarthLike")), None)
    _require(bench, "node-1 loaded the EarthLike storage", row is not None, json.dumps(storages)[:300])
    return row["storage"], list(row["center"]), int(row["size"][0])


def planet_template(bench) -> Path:
    """The Earth fixture unpacked under the run root, declared Creative in both session files (the
    game reads Sandbox_config.sbc; Sandbox.sbc carries a second copy that must agree)."""
    archive = HOST.planet_world_archive
    if not archive.is_file():
        raise ClusterError(f"planet world archive not found: {archive} (PLANET_WORLD_ARCHIVE)")
    parent = bench.run_root.parent / f"{bench.run_root.name}-templates"
    shutil.rmtree(parent, ignore_errors=True)
    parent.mkdir(parents=True)
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(parent)
    world = parent / "RemoteAPITestEarthPlanet"
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        path.write_text(path.read_text().replace("<GameMode>Survival</GameMode>", "<GameMode>Creative</GameMode>"))
    if not (world / "Sandbox.sbc").is_file():
        raise ClusterError(f"world template not found: {world}")
    bench.cluster.config.world_template = world
    return world


def _world_tool(bench, *args: str, timeout: float = 600) -> subprocess.CompletedProcess:
    return subprocess.run(["dotnet", str(bench.cluster.config.world_tool), *map(str, args)],
                          capture_output=True, text=True, timeout=timeout, env=bench.cluster._tool_env())


def _catalog_ids(split_world: Path) -> str:
    seed = json.loads((split_world / "catalog-seed.json").read_text())
    return ",".join(str(member) for partition in seed["partitions"] for member in partition["members"])


def _sector_objects(path: Path) -> list[tuple[str, str, ET.Element]]:
    sector = ET.parse(path).getroot()
    return [(entity.findtext("EntityId"), entity.get(XSI_TYPE), entity)
            for objects in sector.iter("SectorObjects") for entity in objects]


def _grid_blocks(path: Path) -> dict[str, int]:
    return {entity_id: sum(1 for blocks in entity.iter("CubeBlocks") for _ in blocks)
            for entity_id, kind, entity in _sector_objects(path) if kind == "MyObjectBuilder_CubeGrid"}


def _graceful_shutdown(bench, label: str) -> dict:
    """A fresh global save, then the registry's graceful drain: Final saves, a final global snapshot,
    every process exits."""
    _try(lambda: _node(bench, _wa(bench), "/global-save-capture", "POST"))
    shutdown = bench.cluster.post(f"/registry/shutdown/graceful?timeoutSeconds={RECOVERY_TIMEOUT}&forceAfterSeconds=0",
                                  timeout=RECOVERY_TIMEOUT + 30)
    bench.record(f"{label}-shutdown", shutdown)
    last = (shutdown or {}).get("LastShutdown") or {}
    _require(bench, f"graceful shutdown produced a clean snapshot ({label})",
             shutdown.get("Phase") == 4 and last.get("Dirty") == [] and last.get("GlobalRef") is not None,
             json.dumps(shutdown))
    return shutdown


def _wait_exited(bench, names: list[str], label: str) -> None:
    processes = bench.cluster.processes
    _poll(lambda: all(processes[name].poll() is not None for name in names if name in processes), RECOVERY_TIMEOUT)
    for name in names:
        process = processes.get(name)
        _require(bench, f"{name} exited before {label}", process is None or process.poll() is not None)


# -- the stock dedicated server ---------------------------------------------------------------


class Standalone:
    """One stock dedicated server started by ``start_standalone``."""

    def __init__(self, bench, name: str, process: subprocess.Popen, log: Path, remote: str, key: str,
                 data: Path | None = None):
        self.bench = bench
        self.data = data
        self.name = name
        self.process = process
        self.log = log
        self.remote = remote
        self.key = key

    def alive(self) -> bool:
        return self.process.poll() is None

    def remote_get(self, path: str, timeout: float = 60):
        """A vanilla dedicated-server Remote API GET (``/vrageremote`` + path), HMAC-signed the way
        VRage.Dedicated's MyRemoteServer.IsAuthenticated checks it: SHA1 over url, nonce and date,
        one per line."""
        return self.remote_call("GET", path, timeout)

    def remote_call(self, method: str, path: str, timeout: float = 60):
        url = f"/vrageremote{path}"
        nonce = uuid.uuid4().hex
        date = email.utils.formatdate(usegmt=True)
        message = f"{url}\n{nonce}\n{date}\n".encode()
        digest = base64.b64encode(hmac.new(base64.b64decode(self.key), message, hashlib.sha1).digest()).decode()
        request = urllib.request.Request(f"http://{self.remote}{url}", method=method,
                                         headers={"Date": date, "Authorization": f"{nonce}:{digest}"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode()
            return json.loads(body) if body.strip() else None

    def grids(self) -> dict[str, int]:
        """Root grid id -> block count, subgrids included (the vanilla route folds the physical
        hierarchy into its root)."""
        def find(node):
            if isinstance(node, dict):
                if "Grids" in node:
                    return node["Grids"]
                for value in node.values():
                    found = find(value)
                    if found is not None:
                        return found
            return None

        rows = find(self.remote_get("/v1/session/grids")) or []
        return {str(row["EntityId"]): int(row["BlocksCount"]) for row in rows}

    def stop(self) -> None:
        try:
            os.killpg(os.getpgid(self.process.pid), signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            return
        try:
            self.process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(os.getpgid(self.process.pid), signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            self.process.wait(timeout=10)


def _hub_snapshot() -> Path | None:
    """The newest node-side hub snapshot a bench cluster left behind (Sources/sources.xml + Hubs/),
    so the stock server pins the same hub state as the cluster nodes without going to GitHub."""
    candidates = [path for path in HOST.test_root.glob("*/*-magnetar/Sources/sources.xml") if path.is_file()]
    return max(candidates, key=lambda path: path.stat().st_mtime).parent if candidates else None


def start_standalone(bench, world_dir: Path, name: str = "stock", hub_plugins: list[str] | None = None,
                     timeout: float = SERVER_TIMEOUT, ready: tuple[str, ...] = ("Game ready",)) -> Standalone:
    """Load ``world_dir`` on a stock dedicated server: the Magnetar launcher, the host's
    DedicatedServer64, and a profile with the hub plugins only - no cluster plugin, no Direct
    Transport, no test plugin. Runs in the next free port slot after the cluster's nodes, with the
    vanilla Remote API on that slot's control port, and returns once every ``ready`` marker is in
    its log. The process lives under the run root, so ``bench.close`` kills it too."""
    config = bench.cluster.config
    slot = max(node.epoch for node in config.nodes) + 1
    while True:
        spec = config.node_spec(name, slot)
        if not any(port_open(*split_endpoint(endpoint)) for endpoint in (spec.backend, spec.control)):
            break
        slot += 1
    game_port = spec.backend.rpartition(":")[2]
    remote_host, _, remote_port = spec.control.rpartition(":")
    magnetar = config.root / f"{name}-magnetar"
    data = config.root / f"{name}-data"
    shutil.rmtree(data, ignore_errors=True)
    data.mkdir(parents=True)

    # Sources: the cluster's hub snapshot when there is one; no local or dev-folder plugin at all.
    sources_dir = magnetar / "Sources"
    snapshot = _hub_snapshot()
    if snapshot is not None and snapshot.resolve() != sources_dir.resolve():
        sources_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(snapshot / "sources.xml", sources_dir / "sources.xml")
        if (snapshot / "Hubs").is_dir():
            shutil.copytree(snapshot / "Hubs", sources_dir / "Hubs", dirs_exist_ok=True)
    root = magnetar_config._load_sources(sources_dir / "sources.xml")
    for plugin_id in (magnetar_config.TRANSPORT_ID, *magnetar_config.PROFILES.values()):
        magnetar_config._unregister(root, plugin_id)
    magnetar_config._write_xml(root, sources_dir / "sources.xml")
    shutil.rmtree(magnetar / "Local", ignore_errors=True)
    hub_ids = list(magnetar_config.HUB_PLUGINS if hub_plugins is None else hub_plugins)
    for profile in (name, "Current"):
        magnetar_config._write_xml(magnetar_config._profile(profile, hub_ids, [], [], False),
                                   magnetar / "Profiles" / f"{profile}.xml")
    share_github_cache(magnetar, MAGNETAR_GITHUB_CACHE)
    # Only the very first stock start on a host without a bench snapshot may fetch the hub list,
    # like the cluster's own warm-up node; otherwise nothing goes back to GitHub.
    update_args = ["-noupdate"] if snapshot is not None or not hub_ids else []

    key = base64.b64encode(secrets.token_bytes(32)).decode()
    (data / "SpaceEngineers-Dedicated.cfg").write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <LoadWorld />
  <IP>0.0.0.0</IP>
  <SteamPort>8766</SteamPort>
  <ServerPort>{game_port}</ServerPort>
  <Administrators />
  <ServerName>Stock control</ServerName>
  <WorldName>{world_dir.name}</WorldName>
  <PauseGameWhenEmpty>false</PauseGameWhenEmpty>
  <AutoRestartEnabled>false</AutoRestartEnabled>
  <AutoUpdateEnabled>false</AutoUpdateEnabled>
  <IgnoreLastSession>true</IgnoreLastSession>
  <PremadeCheckpointPath>{world_dir}</PremadeCheckpointPath>
  <RemoteApiEnabled>true</RemoteApiEnabled>
  <RemoteApiIP>{remote_host}</RemoteApiIP>
  <RemoteApiPort>{remote_port}</RemoteApiPort>
  <RemoteSecurityKey>{key}</RemoteSecurityKey>
  <AutodetectDependencies>true</AutodetectDependencies>
  <NetworkType>steam</NetworkType>
</MyConfigDedicated>
""")
    env = {key_: value for key_, value in os.environ.items() if not key_.startswith("CLUSTER_")}
    env.update(CRASH_ENV)
    env["DOTNET_DbgMiniDumpName"] = str(config.dumps / f"{name}.%p.dmp")
    process = bench.cluster._spawn(name, [
        str(config.launcher), "-config", str(magnetar), "-ds64", str(config.ds64), "-profile", name,
        *update_args, "-multiInstance", "-noconsent", "-noconsole", "-ignorelastsession",
        "-port", game_port, "-path", str(data)], env, cwd=magnetar)
    server = Standalone(bench, name, process, config.root / f"{name}.log", spec.control, key, data)
    for marker in ready:
        bench.cluster.wait_node_log(name, re.escape(marker), timeout=timeout)
    return server


def _stock_load_check(bench, world: Path, baseline: Path, label: str, ready: tuple[str, ...]) -> None:
    """Load ``world`` on a stock server and compare its grids with ``baseline``'s sector.

    The shell lane compared per-grid block counts of an /cluster/export. The vanilla route reports
    ROOT grids with the blocks of their subgrids folded in, so: every baseline grid that is a root
    there carries at least its own blocks, and the baseline grids found as roots carry exactly the
    baseline's total - a subgrid that lost blocks, or a grid that lost its subgrid, shows as a
    shortfall. A grid arriving without its blocks (unresolved subtypes) is as lost as a missing one."""
    server = start_standalone(bench, world, ready=ready)
    try:
        bench.check(f"the stock server stayed up after loading the {label} world", server.alive())
        _require(bench, "the stock server's Remote API started",
                 _poll(lambda: _lines(bench, server.name, r"Remote Server Listener started"), 60),
                 f"no listener line in {server.log.name}")
        before = _grid_blocks(baseline)
        loaded, stable_since = {}, None

        def listed():
            # "Game ready" is logged before every grid has joined the session, so the first
            # non-empty listing can be short. Take the listing once it has not changed for 15 s.
            nonlocal loaded, stable_since
            current = _try(server.grids, {}) or {}
            if not current or current != loaded:
                loaded, stable_since = current, time.monotonic()
                return None
            return loaded if time.monotonic() - stable_since >= 15 else None

        _poll(listed, 120, interval=3)
        bench.record(f"{label}-stock-grids", {"baseline": before, "loaded": loaded})
        # The listing folds subgrids into their physical-hierarchy root, so it cannot say WHICH grid
        # lost blocks. Save the session through the vanilla route and compare every grid's block
        # count with the baseline, as the shell lane did with its /cluster/export.
        saved_file = server.data / "Saves" / world.name / "SANDBOX_0_0_0_.sbs"
        requested = time.time()
        server.remote_call("PATCH", "/v1/session")
        saved = {}

        def written():
            nonlocal saved
            if not saved_file.is_file() or saved_file.stat().st_mtime < requested:
                return None
            saved = _try(lambda: _grid_blocks(saved_file), {}) or {}
            return saved or None

        _require(bench, f"the stock server saved the {label} session", _poll(written, 120, interval=2),
                 str(saved_file))
        bench.record(f"{label}-stock-saved-grids", saved)
        wrong = {grid: (saved.get(grid), count) for grid, count in before.items() if saved.get(grid) != count}
        bench.check(f"the stock server loaded every {label} grid with its blocks", not wrong,
                    f"{len(before) - len(wrong)} of {len(before)} grids exact; (saved, baseline): {wrong}; "
                    f"roots listed {len(set(before) & set(loaded))}")
    finally:
        server.stop()


# -- clients --------------------------------------------------------------------------------


def _start_client(bench, client_id: int, remote_port: int, render: bool | None = None, max_fps: int = 15,
                  resolution: str = "1280x720") -> RemoteAPI:
    """bench.start_client with the frame cap and resolution the drilling lane runs at."""
    previous = bench.clients.pop(client_id, None)
    if previous is not None:
        previous.stop()
    client = Client(bench.run_root, client_id, bench.remote_port(remote_port), bench.cluster.config.gateway_listen,
                    render=render, max_fps=max_fps, resolution=resolution)
    client.start()
    bench.clients[client_id] = client
    client.wait_api_listening(420)
    api = RemoteAPI(client.url, username=REMOTE_USER, password=REMOTE_PASSWORD, timeout=180)
    api.wait_for_api(max_wait=420)
    bench.apis[client_id] = api
    return api


def _join_first(bench, **client) -> tuple[RemoteAPI, dict]:
    api = _start_client(bench, CLIENT_ID, 24158, **client)
    _wait_line(bench, "gateway", rf"Accepted client {CLIENT_ID}\b", CLIENT_TIMEOUT)
    _wait_line(bench, "gateway", rf"Connected backend {re.escape(_nodes(bench)['node-1'].backend)} as client {CLIENT_ID}",
               CLIENT_TIMEOUT)
    character = bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    bench.screenshot(CLIENT_ID, "planet-client")
    return api, character


def _start_second_client(bench, spawn: bool, route_node: str = "node-2", partition: int | None = None,
                         **client) -> RemoteAPI:
    """Client 2 on a registry route to ``route_node``. ``spawn=False`` stops at chat-ready."""
    backend = _nodes(bench)[route_node].backend
    if partition is None:
        partition = next((row["Partition"] for row in _owners(bench) if row["Node"] == route_node), None)
    if partition is None:
        if route_node != "node-2":
            raise ClusterError(f"{route_node} has no partition for the second client route")
        seed = next((p["id"] for p in _partitions(bench, "node-1") if not p.get("clients")), None)
        if seed is None:
            raise ClusterError("node 1 has no clientless partition to seed node 2")
        bench.record("client2-seed-handover", bench.cluster.post(_handover_url(bench, seed, clientless=True), timeout=300))
        partition = next((row["Partition"] for row in _owners(bench)
                          if row["Partition"] == seed and row["Node"] == "node-2"), None)
    if partition is None:
        raise ClusterError(f"{route_node} has no partition for the second client route")
    bench.record("client2-route", bench.cluster.post(f"/registry/client-routes?client={CLIENT2_ID}&partition={partition}"))
    api = _start_client(bench, CLIENT2_ID, 24159, **client)
    _wait_line(bench, "gateway", rf"Resolved client {CLIENT2_ID} partition {partition} to {route_node} .* at {re.escape(backend)}",
               CLIENT_TIMEOUT)
    _wait_line(bench, route_node, rf"Accepted client {CLIENT2_ID} through cluster node link", CLIENT_TIMEOUT)
    if not spawn:
        def chat_ready():
            session = next((s for s in bench.cluster.get("/transport/status").get("sessions") or []
                            if s.get("client") == CLIENT2_ID), {})
            return (session.get("backend") == backend and session.get("admissionState") == "Ready"
                    and (session.get("identityId") or 0) > 0 and _try(lambda: api.get_chat_messages(0)) is not None)

        _require(bench, f"the second client became chat-ready on {route_node}", _poll(lambda: _try(chat_ready), CLIENT_TIMEOUT))
        return api
    bench.wait_spawned(CLIENT2_ID)
    # A respawn screen left behind the living character is closed; a loading screen is waited out,
    # never closed (the client is still building its session behind it).
    def clear():
        screens = api.list_screens()
        stale = [s for s in screens if s.get("type") == "MyGuiScreenMedicals"]
        for screen in stale:
            _try(lambda: api.close_screen(screen["index"]))
        return not stale and not any(s.get("type") == "MyGuiScreenLoading" for s in screens)

    _require(bench, "the second client is not behind a loading or respawn screen", _poll(lambda: _try(clear), 15))
    return api


def _client_node(bench, client_id: int) -> str | None:
    """The node that owns a client's character is not fixed: a respawn point on another node moves the
    character there (the planet world has a single respawn point, so the second client always
    crosses). Resolve it instead of assuming."""
    for node_id in ("node-1", "node-2"):
        if _try(lambda: _node(bench, node_id, f"/voxel-test-player-state?client={client_id}", timeout=5)) is not None:
            return node_id
    return None


def _wa(bench) -> str:
    """The current World Authority: the replacement once one was started."""
    return "wa-2" if any(node.node_id == "wa-2" for node in bench.cluster.config.nodes) else "wa-1"


def _global_state_has(bench, subsystem: str, text: str) -> bool:
    return not _global_state_missing(bench, subsystem, text)


def _chat_sequences(bench) -> dict:
    """Each node's current chat sequence, for a before/after comparison.

    /global-state keeps the LAST payload per subsystem, not a log, so asking whether a particular
    message is still the current chat payload is a race against the next chat publish - and the
    cluster publishes into the chat subsystem by itself: ClusterPresenceAnnouncer puts its
    "<player> joined the cluster" line there too. P2-VOX-02 failed ~50% of runs on exactly that,
    with the WA's own history showing the wanted message at position 2 of 3 and the third entry
    being the second client's join announcement (2026-09-20). The sequence is the part that is
    monotone and therefore safe to assert on.
    """
    out = {}
    for node_id in ("node-1", "node-2", _wa(bench)):
        state = _try(lambda: _node(bench, node_id, "/global-state"), {}) or {}
        row = next((r for r in state.get("subsystems") or [] if r.get("subsystem") == "chat"), None)
        out[node_id] = -1 if row is None else int(row.get("sequence") or -1)
    return out


def _chat_reached_every_node(bench, before: dict, text: str) -> bool:
    """Every node either still carries `text` as its chat payload, or has moved PAST the publish
    that carried it. Both mean the publish reached that node; only the first survives a later
    message, and the second is what makes the check race-free."""
    now = _chat_sequences(bench)
    missing = _global_state_missing(bench, "chat", text)
    return all(node not in missing or now.get(node, -1) > before.get(node, -1) for node in now)


def _global_state_missing(bench, subsystem: str, text: str) -> list[str]:
    """The nodes whose /global-state does not carry `text` in `subsystem`, in order.

    Named rather than boolean because a chat traversal that fails has three places to fail in and
    the conjunction used to report none of them: P2-VOX-02 on 2026-09-19/20 failed three runs in a
    row with the message "client 1 chat traversed the WA to client 2 and both replicas: " and an
    empty detail, which says only that one of five reads was false.
    """
    missing = []
    for node_id in ("node-1", "node-2", _wa(bench)):
        state = _try(lambda: _node(bench, node_id, "/global-state"), {}) or {}
        if not any(row.get("subsystem") == subsystem and text in str(row.get("payload", ""))
                   for row in state.get("subsystems") or []):
            missing.append(node_id)
    return missing


def _global_state_row(bench, subsystem: str) -> str:
    """Each node's row for `subsystem` plus the counters that say whether it REJECTED the frame.

    "The nodes are missing it" has two very different causes - the publish never arrived, or it
    arrived and was refused on its authority generation (Receive -> AcceptAuthorityMessage, which
    increments `rejected`) - and P2-VOX-02 replaces the World Authority mid-scenario, which is
    exactly when a generation can be stale. The check that named the nodes still could not tell
    those apart, so it now reports the row itself.
    """
    out = []
    for node_id in ("node-1", "node-2", _wa(bench)):
        state = _try(lambda: _node(bench, node_id, "/global-state"), {}) or {}
        row = next((r for r in state.get("subsystems") or [] if r.get("subsystem") == subsystem), None)
        out.append(f"{node_id} gen={state.get('generation')} recv={state.get('received')}"
                   f" rej={state.get('rejected')} resync={len(state.get('pendingResync') or [])} "
                   + ("NO ROW" if row is None else
                      f"row gen={row.get('generation')} seq={row.get('sequence')} gaps={row.get('gaps')}"
                      f" snaps={row.get('snapshots')} pend={row.get('pendingPublish')}"
                      f" payload={str(row.get('payload'))[-90:]!r}"))
    # Which node each client actually sits on decides whether the message had to traverse the WA at
    # all: two clients on one node can see each other's chat through that node's own session.
    sessions = _try(lambda: bench.cluster.get("/transport/status"), {}) or {}
    where = ", ".join(f"{x.get('client')}@{x.get('node') or x.get('backend')}"
                      for x in sessions.get("sessions") or [])
    return " | ".join(out) + f" | sessions: {where}"


def _authority_recorded(bench, text: str) -> str:
    """Whether the World Authority itself RECORDED the message (ChatAuthorityPatch.Record).

    Record runs immediately before the global-state Publish in AcceptedPostfix, so this separates
    the last two candidates cleanly: recorded but not published means the publish path failed
    between the two statements; NOT recorded means the authority never accepted the message at all
    and whatever delivered it to the clients did not go through the authority.
    """
    history = _try(lambda: _node(bench, _wa(bench), "/chat-history"), {}) or {}
    entries = history.get("Messages") or history.get("messages") or []
    texts = [str(e.get("Text") or e.get("text")) for e in entries]
    return (f"WA recorded it: {text in texts}; WA history has {len(texts)} entr(y|ies), "
            f"tail {texts[-3:]}")


def _chat_has(api, text: str, channel: str, target: int | None = None) -> bool:
    messages = (_try(lambda: api.get_chat_messages(0), {}) or {}).get("messages") or []
    return any(m.get("Text") == text and m.get("Channel") == channel and (target is None or m.get("TargetId") == target)
               for m in messages)


def _chat_and_gps(bench, api1, api2) -> None:
    """Cross-node chat (global, reply, private) and GPS through the World Authority."""
    _try(lambda: api2.get_chat_messages(0))
    live = f"phase2-live-chat-{random.randrange(32768)}"
    before = _chat_sequences(bench)
    # Retried only while the send itself fails (the shell lane's send_chat); a sent message is never re-sent.
    sent = _poll(lambda: _try(lambda: api1.send_chat(live) or {"sent": True}), 15)
    _require(bench, "client 1 sent live chat", sent is not None)
    traversed = _poll(lambda: _chat_has(api1, live, "Global") and _chat_has(api2, live, "Global")
                      and _chat_reached_every_node(bench, before, live), HANDOVER_TIMEOUT)
    _require(bench, "client 1 chat traversed the WA to client 2 and both replicas", traversed,
             "" if traversed else f"sender sees it: {_chat_has(api1, live, 'Global')}, "
             f"receiver sees it: {_chat_has(api2, live, 'Global')}, "
             f"global-state missing it on: {_global_state_missing(bench, 'chat', live) or 'nowhere'}; "
             f"{_global_state_row(bench, 'chat')} | {_authority_recorded(bench, live)} "
             f"| chat sequences before: {before}, now: {_chat_sequences(bench)}")
    reply = f"phase2-live-reply-{random.randrange(32768)}"
    _require(bench, "client 2 sent live chat", _poll(lambda: _try(lambda: api2.send_chat(reply) or {"sent": True}), 15) is not None)
    replied = _poll(lambda: _chat_has(api1, reply, "Global"), HANDOVER_TIMEOUT)
    _require(bench, "client 2 chat traversed the WA to client 1", replied,
             "" if replied else f"global-state missing it on: "
             f"{_global_state_missing(bench, 'chat', reply) or 'nowhere'}")
    identity = next((s.get("identityId") or 0 for s in bench.cluster.get("/transport/status").get("sessions") or []
                     if s.get("client") == CLIENT2_ID), 0)
    _require(bench, "the second client has a World Authority identity for private chat", identity > 0, str(identity))
    private = f"phase2-private-chat-{random.randrange(32768)}"
    # Captured BEFORE the send, or the publish being tested is already counted in the baseline.
    private_before = _chat_sequences(bench)
    private_sent = _try(lambda: api1.send_chat(private, "Private", identity))
    _require(bench, "client 1 sent private chat", private_sent is not None)
    private_through = _poll(lambda: _chat_has(api2, private, "Private", identity)
                            and _chat_reached_every_node(bench, private_before, private), HANDOVER_TIMEOUT)
    _require(bench, "private chat traversed the WA to the targeted client", private_through,
             "" if private_through else f"target sees it: {_chat_has(api2, private, 'Private', identity)}, "
             f"global-state missing it on: {_global_state_missing(bench, 'chat', private) or 'nowhere'}")
    gps_name = f"phase2-gps-{random.randrange(32768)}"
    gps_sent = _try(lambda: api1.add_gps(gps_name, 11.25, 22.5, 33.75, "WA GPS"))
    _require(bench, "client 1 added GPS through the vanilla request path", gps_sent is not None)

    def gps_converged():
        entries = (_try(api1.list_gps, {}) or {}).get("entries") or []
        wa_state = _try(lambda: _node(bench, _wa(bench), "/global-state"), {}) or {}
        players = any(row.get("subsystem") == "positions" and len((_try(lambda: json.loads(row["payload"]), {}) or {})
                                                                   .get("Players") or []) >= 1
                      for row in wa_state.get("subsystems") or [])
        return any(e.get("name") == gps_name for e in entries) and _global_state_has(bench, "gps", gps_name) and players

    _require(bench, "client GPS converged through the World Authority to both replicas",
             _poll(gps_converged, HANDOVER_TIMEOUT))
    bench.record("world-authority-gps", {"name": gps_name, "sent": gps_sent, "client": _try(api1.list_gps)})
    bench.record("world-authority-live-chat", {"text": live, "reply": reply, "sent": sent,
                                               "private": {"text": private, "targetIdentity": identity, "sent": private_sent},
                                               "client1": _try(lambda: api1.get_chat_messages(0)),
                                               "client2": _try(lambda: api2.get_chat_messages(0))})


# -- P2-VOXB --------------------------------------------------------------------------------


def p2_voxb(bench) -> None:
    """An ordered voxel cutout on node-1 is relayed through the WA to node-2 exactly once."""
    planet_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    storage, _, _ = _earthlike(bench)
    bench.record("voxel-cutout-request", _node(bench, "node-1", f"/voxel-test-cutout?storage="
                                               f"{urllib.parse.quote(storage, safe='')}&x=64&y=64&z=64&radius=1.8", "POST"))
    states = {}

    def relayed():
        states.update({node: _journal(bench, node) for node in ("node-1", "node-2", "wa-1")})
        n1, n2, wa = states["node-1"], states["node-2"], states["wa-1"]
        applied = lambda state: (state.get("applied") or {}).get(storage, 0) > 0  # noqa: E731
        return (n1.get("cutoutRelays") == 0 and applied(n1)
                and (n2.get("cutoutRelays") or 0) > 0 and applied(n2)
                and wa.get("retainedCutouts") == 1 and (wa.get("cutoutRelays") or 0) > 0 and applied(wa))

    ok = _poll(relayed, 30)
    for node, state in states.items():
        bench.record(f"voxel-cutout-{node}", state)
    bench.check("the voxel cutout broadcast traversed node 1, the WA and node 2 exactly once", ok, json.dumps(states)[:400])
    bench.exercised(storage=storage)


# -- P2-VOX-02 ------------------------------------------------------------------------------


def _replace_wa(bench, label: str) -> None:
    """SIGKILL wa-1, wait for the Gateway to fence it, start wa-2 on wa-1's retained data root."""
    config = bench.cluster.config
    bench.cluster.kill(slot="wa-1")
    _require(bench, f"the Gateway fenced the failed World Authority ({label})",
             _poll(lambda: _try(lambda: bench.cluster.get("/registry/world-authority").get("Node", "?") is None),
                   RECOVERY_TIMEOUT))
    if not any(node.node_id == "wa-2" for node in config.nodes):
        config.add_node("wa-2", role="WA")
    config.node_env_by_id["wa-2"] = {"CLUSTER_DATA_ROOT": str(config.node_data(_nodes(bench)["wa-1"]))}
    bench.cluster.start_node("wa-2")
    bench.cluster.wait_node_log("wa-2", "Game ready", SERVER_TIMEOUT)


def _journal_under_chaos(bench, storage: str, size: int) -> dict:
    """The former startup-loop body of `planet-journal`, before any client joins."""
    wa_root = bench.cluster.config.node_data(_nodes(bench)["wa-1"]) / "cluster/voxel"
    coord = size // 2
    query = _query(storage, coord, coord, coord, 4)
    expected = _write(bench, "node-1", query, 0, 1)
    bench.record("voxel-write", expected)
    expected_hash = expected["hash"]
    seen = {}

    def converged():
        seen.update(wa=_hash(bench, "wa-1", query), node2=_hash(bench, "node-2", query))
        return seen["wa"] == expected_hash and seen["node2"] == expected_hash

    _require(bench, "the planet replicas converged on the first write", _poll(converged, HANDOVER_TIMEOUT),
             f"origin={expected_hash} {seen}")
    _require(bench, "the WA persisted a voxel journal entry", list(wa_root.glob("*/*.vjr")))
    # WA-5: the truncation cut is bounded by every active regular node's reported applied watermark, so wait
    # until both have reported the tail; otherwise the compaction legitimately truncates nothing.
    wa_tail = (_journal(bench, "wa-1").get("applied") or {}).get(storage, 0)
    replicas = []

    def reported():
        nonlocal replicas
        replicas = _try(lambda: bench.cluster.get("/registry/voxel-replicas"), []) or []
        return len([r for r in replicas if r.get("StorageName") == storage and r.get("Applied", 0) >= wa_tail]) >= 2

    ok = _poll(reported, HANDOVER_TIMEOUT)
    bench.record("voxel-replicas", replicas)
    _require(bench, "the regular nodes reported their applied voxel watermarks to the registry", ok)
    compact = _node(bench, "wa-1", "/voxel-compact", "POST")
    bench.record("voxel-compaction", compact)
    row = next((r for r in compact if r.get("storage") == storage and r.get("tailSequence", 0) >= 1), None)
    _require(bench, "the WA compaction wrote the EarthLike native base", row and row.get("path") and Path(row["path"]).is_file(),
             json.dumps(compact))
    base_version, base_tail, base_checksum = row["baseVersion"], row["tailSequence"], row["checksum"]
    _require(bench, "the WA compacted base checksum matches",
             hashlib.sha256(Path(row["path"]).read_bytes()).hexdigest().upper() == base_checksum)
    # WA-5: with both replicas reported at the tail, the compaction cut reaches the tail and every retained
    # entry file at or below it must be gone, while the durable operation-id log survives for dedupe.
    _require(bench, "the WA compaction truncated the catalogued journal", (row.get("truncated") or 0) >= 1,
             f"truncated={row.get('truncated')}")
    _require(bench, "the WA retained no journal entry files past the truncation cut", not list(wa_root.glob("*/*.vjr")))
    oplog = wa_root / "operations.oplog"
    _require(bench, "the WA retained the durable voxel operation log", oplog.is_file() and oplog.stat().st_size > 0)
    catalog = {}

    def catalogued():
        catalog["bases"] = _try(lambda: bench.cluster.get("/registry/voxel-bases"), []) or []
        catalog["journals"] = _try(lambda: bench.cluster.get("/registry/voxel-journals"), []) or []
        return any(b.get("StorageName") == storage and b.get("BaseVersion") == base_version
                   and b.get("TailSequence") == base_tail and b.get("Checksum") == base_checksum for b in catalog["bases"]) \
            and any(j.get("StorageName") == storage and j.get("BaseVersion") == base_version and j.get("TailSeq") == base_tail
                    for j in catalog["journals"])

    _poll(catalogued, HANDOVER_TIMEOUT)
    _require(bench, "the Gateway catalogued the compacted EarthLike base",
             any(b.get("StorageName") == storage and b.get("BaseVersion") == base_version for b in catalog["bases"]))
    _require(bench, "the Gateway catalogued the EarthLike journal watermark",
             any(j.get("StorageName") == storage and j.get("BaseVersion") == base_version and j.get("TailSeq") == base_tail
                 for j in catalog["journals"]))
    bench.record("voxel-bases", catalog["bases"])
    bench.record("voxel-journals", catalog["journals"])
    snapshot = bench.cluster.post("/registry/snapshots/online?id=voxel-cut&clusterTime=1")
    bench.record("voxel-snapshot", snapshot)
    _require(bench, "the online snapshot retained the exact EarthLike voxel cut",
             any(r.get("StorageName") == storage and r.get("BaseVersion") == base_version and r.get("TailSeq") == base_tail
                 for r in snapshot.get("VoxelRefs") or []) and f"voxel:{storage}" not in (snapshot.get("Dirty") or []))

    # P2-REC mutation-time WA kill for VOXEL mutations: hold a second edit's journal append in flight at the
    # WA (sleeping BEFORE the durable write) and land the kill inside it. The node outbox deletes an entry
    # only on acknowledgement, so the held edit must replay to the replacement WA and apply there exactly once.
    held_coord = size // 2 + 32
    held_query = _query(storage, held_coord, held_coord, held_coord, 4)
    _require(bench, "the WA voxel append delay knob armed",
             _node(bench, "wa-1", "/voxel-test-append-delay?ms=8000", "POST").get("delayMs") == 8000)
    held = _write(bench, "node-1", held_query, 64, 2)
    bench.record("voxel-write-heldkill", held)
    held_hash = held["hash"]
    _require(bench, "the voxel journal append was held in flight at the WA",
             _poll(lambda: (_try(lambda: _node(bench, "wa-1", "/voxel-test-append-delay"), {}) or {}).get("held", 0) >= 1,
                   HANDOVER_TIMEOUT))
    _replace_wa(bench, "voxel journal")
    bench.cluster.wait_node_log("wa-2", "voxel_catalog base_installed=true storage=EarthLike", SERVER_TIMEOUT)
    _require(bench, "the replacement WA replayed its retained journal",
             _poll(lambda: _hash(bench, "wa-2", query) == expected_hash, HANDOVER_TIMEOUT), expected_hash)
    # The edit whose append was held at the kill: the node-1 outbox replays it to the replacement WA (it was
    # never acknowledged), it applies exactly once, node-2 converges, and the outbox drains to zero.
    _require(bench, "the held-append voxel edit replayed to the replacement WA",
             _poll(lambda: _hash(bench, "wa-2", held_query) == held_hash, HANDOVER_TIMEOUT), held_hash)
    _require(bench, "node 2 converged on the held-append voxel edit",
             _poll(lambda: _hash(bench, "node-2", held_query) == held_hash, HANDOVER_TIMEOUT), held_hash)
    _require(bench, "the node-1 voxel outbox drained after the replacement acknowledged",
             _poll(lambda: _outbox_clear(_journal(bench, "node-1")), HANDOVER_TIMEOUT),
             json.dumps(_outbox(_journal(bench, "node-1"))))

    # WA-5: continuous appends during a node-to-WA network PARTITION (packet loss - no process dies; the chaos
    # knob severs the voxel HTTP path too). Node-1 keeps editing while the WA is unreachable: edits apply
    # locally, the outbox accumulates without dropping, nothing reaches the WA during the window, and after it
    # lapses the outbox drains in order and every replica converges.
    _require(bench, "the node-1 WA partition knob armed for the voxel outage",
             _node(bench, "node-1", "/test-wa-partition?ms=30000", "POST").get("active") is True)
    outage = []
    for i in (1, 2, 3):
        c = size // 2 + 64 + i * 8
        outage_query = _query(storage, c, c, c, 4)
        outage.append((_write(bench, "node-1", outage_query, 100 + i, 1)["hash"], outage_query))
    # The dirty-range capture rides its own cadence, so poll for the entries to reach the outbox - where they
    # must then HOLD, because the partition blocks the send path.
    _require(bench, "the outbox held the partition-window edits",
             _poll(lambda: (lambda s: s.get("outboundQueued", 0) >= 1 and s.get("outboxDrops") == 0)(_journal(bench, "node-1")), 20),
             json.dumps(_outbox(_journal(bench, "node-1"))))
    last_hash, last_query = outage[-1]
    _require(bench, "no partition-window edit reached the WA during the outage",
             _hash(bench, "wa-2", last_query) != last_hash)
    _require(bench, "the partition-window edits converged at the WA after the outage",
             _poll(lambda: all(_hash(bench, "wa-2", q) == h for h, q in outage), 45, interval=2))
    _require(bench, "the outbox drained cleanly after the outage",
             _poll(lambda: _outbox_clear(_journal(bench, "node-1")) and _hash(bench, "node-2", last_query) == last_hash, 60),
             json.dumps(_outbox(_journal(bench, "node-1"))))
    bench.record("voxel-outage", {"droppedFrames": _node(bench, "node-1", "/test-wa-partition").get("droppedFrames", 0)})

    # WA-5 total order: overlapping writes from BOTH nodes race at the same region. The WA assigns ONE
    # strictly-increasing sequence per journal entry; replicas must retain the same (sequence, operationId)
    # pairs, and the region content must converge identically - an agreed total order, with every operation
    # applied exactly once.
    encoded = urllib.parse.quote(storage, safe="")
    order_coord = size // 2 + 200
    order_query = _query(storage, order_coord, order_coord, order_coord, 6)
    order_floor = max((e["sequence"] for e in _node(bench, "wa-2", f"/voxel-journal-entries?storage={encoded}")), default=0)
    for order_round in (1, 2, 3, 4):
        failures = []

        def race(node_id, content):
            try:
                _write(bench, node_id, order_query, content, 1)
            except Exception as error:  # noqa: BLE001
                failures.append(f"{node_id}: {error}")

        threads = [threading.Thread(target=race, args=("node-1", 150 + order_round)),
                   threading.Thread(target=race, args=("node-2", 180 + order_round))]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        _require(bench, f"the racing voxel writes of round {order_round} succeeded", not failures, str(failures))
        time.sleep(1)
    _require(bench, "the racing writes drained cleanly",
             _poll(lambda: _outbox_clear(_journal(bench, "node-1")) and _outbox_clear(_journal(bench, "node-2")), 60),
             json.dumps({"node1": _outbox(_journal(bench, "node-1")), "node2": _outbox(_journal(bench, "node-2"))}))
    order_new = [e for e in _node(bench, "wa-2", f"/voxel-journal-entries?storage={encoded}") if e["sequence"] > order_floor]
    _require(bench, "the race produced at least 2 new journal entries", len(order_new) >= 2, json.dumps(order_new))
    operations = [e["operationId"] for e in order_new]
    _require(bench, "no journal operation id appears twice at the WA", len(operations) == len(set(operations)),
             json.dumps(order_new))
    sequences = [e["sequence"] for e in order_new]
    _require(bench, "the WA journal sequences are strictly increasing",
             sequences == sorted(sequences) and len(sequences) == len(set(sequences)), json.dumps(order_new))
    # Replicas do not retain applied Range entries (they fold into the base and truncate); their total-order
    # proof is the sequence-gated apply tail: Apply refuses any entry whose sequence is not exactly applied+1,
    # so reaching the WA's tail means the node consumed the SAME stream in the SAME order with nothing skipped
    # or repeated.
    order_tail = max(sequences)
    applied = {}

    def reached():
        applied.update({node: (_journal(bench, node).get("applied") or {}).get(storage, 0) for node in ("node-1", "node-2")})
        return all(value >= order_tail for value in applied.values())

    _require(bench, "the replicas reached the WA's journal tail", _poll(reached, 60), f"tail {order_tail}: {applied}")
    hashes = {}

    def one_content():
        hashes.update({node: _hash(bench, node, order_query) for node in ("node-1", "node-2", "wa-2")})
        return hashes["node-1"] and len(set(hashes.values())) == 1

    _require(bench, "the raced region converged to one content", _poll(one_content, 45), json.dumps(hashes))
    bench.record("voxel-total-order", {"entries": order_new, "finalHash": hashes["node-1"], "tail": order_tail,
                                       "applied": applied})

    generation = bench.cluster.get("/registry/world-authority")["Generation"]
    journals = []

    def re_reported():
        nonlocal journals
        journals = _try(lambda: bench.cluster.get("/registry/voxel-journals"), []) or []
        return any(j.get("StorageName") == storage and j.get("BaseVersion") == base_version and j.get("TailSeq", 0) >= 1
                   and j.get("AuthorityGeneration") == generation for j in journals)

    _require(bench, "the replacement WA re-reported the EarthLike voxel cut", _poll(re_reported, HANDOVER_TIMEOUT))
    bench.record("voxel-journals-replacement", journals)

    # WA-5: compaction racing live appends. A write burst runs on node-1 with a WA compaction fired
    # mid-burst; every edit must survive with its exact content - the truncation cut is bounded by the
    # slowest replica's applied watermark, so a racing append can be truncated only after every replica
    # applied it. No truncated-count assert: racing watermarks may legitimately allow no cut at all.
    race = []
    for race_i in range(1, 9):
        c = size // 2 + 128 + race_i * 8
        race_query = _query(storage, c, c, c, 4)
        race.append((_write(bench, "node-1", race_query, 150 + race_i, 1)["hash"], race_query))
        if race_i == 4:
            bench.record("voxel-race-compaction", _node(bench, "wa-2", "/voxel-compact", "POST"))
    _require(bench, "no compaction-racing voxel edit was lost or diverged",
             _poll(lambda: all(_hash(bench, "wa-2", q) == h and _hash(bench, "node-2", q) == h for h, q in race), 60, interval=2))
    race_state = _node(bench, "node-1", "/voxel-journal-status")
    _require(bench, "the outbox settled after the compaction race", _outbox_clear(race_state), json.dumps(_outbox(race_state)))

    second_coord = coord + 8
    second_query = _query(storage, second_coord, second_coord, second_coord, 4)
    second_hash = _write(bench, "node-1", second_query, 0, 2)["hash"]
    _require(bench, "the voxel journal continued after the WA replacement",
             _poll(lambda: _hash(bench, "wa-2", second_query) == second_hash and _hash(bench, "node-2", second_query) == second_hash,
                   HANDOVER_TIMEOUT), second_hash)
    # The pre-compaction entries are truncated, so the journal holds only what came after the cut: at least
    # the new edit, persisted under a sequence above the compacted tail.
    journal_files = sorted(wa_root.glob("*/*.vjr"))
    _require(bench, "the replacement WA persisted the next voxel journal sequence",
             journal_files and int(journal_files[-1].stem, 10) > base_tail, str(journal_files[-1:]))

    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", SERVER_TIMEOUT)
    # The catalogued base lives on the WA's root; node-2 must fetch it over the holder's control endpoint into
    # its own root instead of trusting the foreign absolute path.
    bench.cluster.wait_node_log("node-2", "voxel_base fetched=true storage=EarthLike", SERVER_TIMEOUT)
    bench.cluster.wait_node_log("node-2", "voxel_catalog base_installed=true storage=EarthLike", SERVER_TIMEOUT)
    node2_root = bench.cluster.config.node_data(_nodes(bench)["node-2"])
    _require(bench, "node 2 installed a local voxel base copy", list(node2_root.glob("cluster/voxel/base/*/base-*.vx2")))
    _require(bench, "the restarted node 2 replayed both journal entries",
             _poll(lambda: _hash(bench, "node-2", query) == expected_hash and _hash(bench, "node-2", second_query) == second_hash,
                   HANDOVER_TIMEOUT))
    # The first edit lives in the compacted base by now, so the journal list holds only the post-cut entries.
    bench.record("voxel-convergence", {"storage": storage, "firstHash": expected_hash, "secondHash": second_hash,
                                       "journals": [str(path) for path in journal_files], "waReplacement": True})
    return {"query": query, "expected_hash": expected_hash, "base_version": base_version, "base_tail": base_tail}


def _asteroid_and_rebuild(bench, storage: str, size: int, journal: dict) -> None:
    """The planet-journal tail: a WA-dispatched asteroid, then the voxel catalog rebuilt from scanned roots."""
    wa = "wa-2"
    config = bench.cluster.config
    # WA-5 asteroid: a WA-dispatched spawn materializes the IDENTICAL asteroid on every node (the storage name
    # encodes seed/radius/position), journal writes to it converge like planet writes, and the rebuild leg
    # below proves the registry-wipe restart re-materializes it from the voxel base catalog alone.
    spawn = _node(bench, wa, "/voxel-test-spawn-asteroid?x=2000000&y=2000000&z=2000000&radius=60&seed=7", "POST")
    asteroid = spawn.get("name")
    _require(bench, "the asteroid spawn dispatch succeeded", asteroid, json.dumps(spawn))
    asteroid_query = _query(asteroid, 40, 40, 40, 4)
    seen = {}

    def identical():
        seen.update({node: _hash(bench, node, asteroid_query) for node in (wa, "node-1", "node-2")})
        return seen[wa] and len(set(seen.values())) == 1

    _require(bench, "the asteroid materialized identically on every role", _poll(identical, HANDOVER_TIMEOUT), json.dumps(seen))
    asteroid_hash = _write(bench, "node-1", asteroid_query, 200, 3)["hash"]
    _require(bench, "the asteroid journal write converged",
             _poll(lambda: _hash(bench, wa, asteroid_query) == asteroid_hash and _hash(bench, "node-2", asteroid_query) == asteroid_hash,
                   HANDOVER_TIMEOUT), asteroid_hash)
    bench.record("p2-vox-asteroid", {"spawn": spawn, "hash": asteroid_hash})

    # P2-REC-04 tail: the voxel base/journal catalog is rebuilt with the rest of the registry. Compact so a
    # catalogued base exists, land two post-compaction writes so the journal tail sits above the base, drain
    # gracefully, destroy the registry, scan the surviving roots (bases are named by their storage-name.txt
    # manifest since the directory name is a one-way hash; the tail comes from the WA-retained .vjr entries),
    # rebuild, and restart every process on its own root: the planet content must come back from the
    # catalogued base plus journal replay, not the pristine world template every node would otherwise
    # silently fall back to.
    compact = _node(bench, wa, "/voxel-compact", "POST")
    bench.record("voxel-rebuild-compaction", compact)
    row = next((r for r in compact if r.get("storage") == storage), {})
    base_version, base_tail = row.get("baseVersion"), row.get("tailSequence")
    _require(bench, "the pre-rebuild compaction catalogued a base", isinstance(base_tail, int), json.dumps(compact))
    c = size // 2 + 260
    rebuild_query = _query(storage, c, c, c, 4)
    _write(bench, "node-1", rebuild_query, 90, 1)
    rebuild_hash = _write(bench, "node-1", rebuild_query, 120, 2)["hash"]
    # Same-region writes inside one dirty-flush window merge into a single journal entry, so the tail advances
    # by at least one, not necessarily two.
    tail = base_tail + 1
    journals = []

    def ready():
        nonlocal journals
        journals = _try(lambda: bench.cluster.get("/registry/voxel-journals"), []) or []
        return _hash(bench, wa, rebuild_query) == rebuild_hash and _hash(bench, "node-2", rebuild_query) == rebuild_hash \
            and any(j.get("StorageName") == storage and j.get("BaseVersion") == base_version and j.get("TailSeq", 0) >= tail
                    for j in journals)

    _require(bench, "the post-compaction writes converged before the wipe", _poll(ready, HANDOVER_TIMEOUT), json.dumps(journals))
    registered_tail = next(j["TailSeq"] for j in journals if j.get("StorageName") == storage)
    bases_before = bench.cluster.get("/registry/voxel-bases")
    bench.record("voxel-rebuild-bases-before", bases_before)

    def newest(bases):
        rows = sorted((b for b in bases if b.get("StorageName") == storage), key=lambda b: b.get("BaseVersion", 0))
        return {key: rows[-1].get(key) for key in ("BaseVersion", "TailSequence", "SizeBytes", "Checksum", "Location")} if rows else None

    newest_before = newest(bases_before)
    shutdown = _graceful_shutdown(bench, "voxel-rebuild")
    _wait_exited(bench, ["node-1", "node-2", wa, "gateway"], "the voxel rebuild wipe")
    bench.kill_client(CLIENT_ID)
    bench.kill_client(CLIENT2_ID)
    shutil.rmtree(config.root / "gateway-registry", ignore_errors=True)

    # The planet lane's history (fenced captures, retention lag) leaves stale same-id save files on the roots;
    # the operator resolves them by cluster time.
    payload_path = config.root / "voxel-rebuild-payload.json"
    nodes = _nodes(bench)
    scan = _world_tool(bench, "catalog-scan", payload_path, f"node-1={config.node_data(nodes['node-1'])}",
                       f"node-2={config.node_data(nodes['node-2'])}", f"wa-2={config.node_data(nodes['wa-1'])}",
                       "--prefer-newest")
    bench.record("voxel-rebuild-scan.txt", scan.stdout + scan.stderr)
    _require(bench, "the voxel catalog scan succeeded", scan.returncode == 0, (scan.stdout + scan.stderr)[-300:])
    payload = json.loads(payload_path.read_text())
    bench.record("voxel-rebuild-payload", payload)
    _require(bench, "the catalog scan found the voxel base and the journal tail",
             len(payload.get("VoxelBases") or []) >= 1
             and len([j for j in payload.get("VoxelJournals") or []
                      if j.get("StorageName") == storage and j.get("TailSeq") == registered_tail]) == 1,
             json.dumps({"VoxelBases": payload.get("VoxelBases"), "VoxelJournals": payload.get("VoxelJournals")}))
    payload["NextEntityIdRange"] = 9

    # A Gateway on the wiped registry, then the rebuild, then the nodes. The CLI starts the Gateway
    # --paused; the shell lane's rebuild Gateway was not, and no client joins from here on.
    bench.cluster.start(nodes=[])
    bench.cluster.wait_node_log("gateway", "Gateway listening", SERVER_TIMEOUT)
    bench.cluster.post("/resume")
    _require(bench, "the wiped registry holds no voxel bases", len(bench.cluster.get("/registry/voxel-bases")) == 0)
    try:
        summary = bench.cluster.post("/registry/rebuild", body=payload)
    except ClusterError as error:
        _require(bench, "the voxel catalog rebuild was accepted", False, str(error))
    bench.record("voxel-rebuild-summary", summary)
    _require(bench, "the rebuild summary kept the voxel catalog",
             summary.get("VoxelBases", 0) >= 1 and summary.get("VoxelJournals", 0) >= 1 and summary.get("GlobalSaves", 0) >= 1,
             json.dumps(summary))
    bases_after = bench.cluster.get("/registry/voxel-bases")
    bench.record("voxel-rebuild-bases-after", bases_after)
    _require(bench, "the rebuilt voxel base equals the pre-wipe catalog", newest(bases_after) == newest_before,
             json.dumps({"before": newest_before, "after": newest(bases_after)}))
    journals_after = bench.cluster.get("/registry/voxel-journals")
    _require(bench, "the rebuilt voxel journal tail equals the pre-wipe catalog",
             len([j for j in journals_after if j.get("StorageName") == storage and j.get("TailSeq") == registered_tail]) == 1,
             json.dumps(journals_after))

    # Every restarted process takes a fresh epoch above every one this run used.
    for node_id in (wa, "node-1", "node-2"):
        nodes[node_id].epoch = max(node.epoch for node in config.nodes) + 1
        bench.cluster.start_node(node_id)
    bench.cluster.wait_node_log(wa, "voxel_catalog base_installed=true storage=EarthLike", SERVER_TIMEOUT)
    bench.cluster.wait_node_log(wa, "Game ready", SERVER_TIMEOUT)
    # The rebuilt catalog names wa-2 as the holder: the WA reads its own root directly while both regular
    # nodes fetch the base over its control endpoint into their own roots.
    for node_id in ("node-1", "node-2"):
        bench.cluster.wait_node_log(node_id, "voxel_base fetched=true storage=EarthLike", SERVER_TIMEOUT)
        bench.cluster.wait_node_log(node_id, "voxel_catalog base_installed=true storage=EarthLike", SERVER_TIMEOUT)
        bench.cluster.wait_node_log(node_id, "Game ready", SERVER_TIMEOUT)
    _require(bench, "the World Authority did not fetch a base it already holds",
             not _lines(bench, wa, r"voxel_base fetched=true"))

    # The post-compaction region must replay from the rebuilt journal on every role, the base-covered region
    # must come out of the catalogued base, and both replicas must reach the pre-wipe tail through the
    # sequence-gated apply.
    state = {}

    def recovered():
        state.update(region={node: _hash(bench, node, rebuild_query) for node in (wa, "node-1", "node-2")},
                     base=_hash(bench, "node-2", journal["query"]),
                     applied={node: (_journal(bench, node).get("applied") or {}).get(storage, 0) for node in ("node-1", "node-2")},
                     asteroid={node: _hash(bench, node, asteroid_query) for node in (wa, "node-1", "node-2")})
        return all(h == rebuild_hash for h in state["region"].values()) and state["base"] == journal["expected_hash"] \
            and all(h == asteroid_hash for h in state["asteroid"].values()) \
            and all(value >= registered_tail for value in state["applied"].values())

    _require(bench, "the planet content survived the registry wipe through the rebuilt voxel catalog",
             _poll(recovered, RECOVERY_TIMEOUT, interval=2),
             json.dumps({**state, "expected": rebuild_hash, "expectedBase": journal["expected_hash"],
                         "expectedAsteroid": asteroid_hash, "tail": registered_tail}))
    _require(bench, "node 1 re-materialized the asteroid from the rebuilt voxel base catalog",
             _lines(bench, "node-1", r"voxel_catalog asteroid_installed=true"))
    _require(bench, "the World Authority re-materialized the asteroid from the rebuilt voxel base catalog",
             _lines(bench, wa, r"voxel_catalog asteroid_installed=true"))

    # The rebuilt partition catalog recovers on the same run: every manifest partition comes back owned,
    # proving the voxel catalog rides the ordinary rebuild flow. Owned-or-retired per manifest partition and
    # nothing recoverable left unowned - not an owned-row COUNT equal to the manifest: a post-restart
    # co-location merge retires a manifest partition (its content lives on in the output).
    manifest = sorted({row["Partition"] for row in shutdown["LastShutdown"].get("Manifest") or []})
    owners = []

    def owned():
        nonlocal owners
        owners = _try(lambda: bench.cluster.get("/registry/partitions"), []) or []
        dangling = [r for r in owners if (r.get("Node") is None or r.get("Recovering") is True) and r.get("Retired") is False]
        return not dangling and all(any(r["Partition"] == p and ((r.get("Node") is not None and r.get("Recovering") is False)
                                                                  or r.get("Retired") is True) for r in owners)
                                    for p in manifest)

    _require(bench, "the rebuilt cluster recovered every partition alongside the voxel catalog",
             _poll(owned, RECOVERY_TIMEOUT), json.dumps(owners))
    bench.record("p2-rebuild-voxel-catalog", {"compaction": compact, "tail": registered_tail, "basesBefore": bases_before,
                                              "basesAfter": bases_after, "journalsAfter": journals_after,
                                              "rebuilt": summary, "regionHash": rebuild_hash,
                                              "baseRegionHash": journal["expected_hash"]})
    bench.exercised(baseVersion=base_version, tail=registered_tail, manifest=len(manifest))


def p2_vox_02(bench) -> None:
    """Voxel journal under chaos and the catalog rebuild."""
    planet_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    storage, _, size = _earthlike(bench)
    journal = _journal_under_chaos(bench, storage, size)
    api1, character = _join_first(bench)
    # planet-journal's second client is a cheap non-rendering observer that only has to chat.
    api2 = _start_second_client(bench, spawn=False, render=False, max_fps=5, resolution="640x480")
    _chat_and_gps(bench, api1, api2)
    target = _try(api1.get_character_target)
    log = bench.clients[CLIENT_ID].log
    _require(bench, "the planet client log is not empty", log.is_file() and log.stat().st_size > 0)
    bench.record("planet-client-observation", {"character": character, "target": target,
                                               "route": bench.cluster.get("/transport/status")})
    _asteroid_and_rebuild(bench, storage, size, journal)


# -- P2-VOX-01 ------------------------------------------------------------------------------


def _sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def _add(a, b):
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def _scale(a, k):
    return [a[0] * k, a[1] * k, a[2] * k]


def _norm(a):
    return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])


def _unit(a):
    return _scale(a, 1 / _norm(a))


def _cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def _voxel_hits(state: dict | None) -> int:
    return len([t for t in (state or {}).get("drillHitTypes") or [] if t and VOXEL_HIT.search(str(t))])


def _place(bench, node_id: str, client_id: int, storage: str, direction, align: bool) -> dict:
    x, y, z = direction
    return _node(bench, node_id, f"/voxel-test-place-player?client={client_id}&storage={urllib.parse.quote(storage, safe='')}"
                                 f"&x={x}&y={y}&z={z}" + ("&align=true" if align else ""), "POST")


def _drill_region(bench, position, coord: int, storage: str) -> str:
    x, y, z = (math.floor(p + coord - 32) for p in position)
    return _query(storage, x, y, z, 64)


def _prepare_drill_region(bench, api, label: str, center, coord: int, storage: str, require_target: bool) -> tuple[dict, str, dict]:
    state = _poll(lambda: _try(api.get_character), 30) or {}
    if state.get("state") == "sitting":
        api.character_use()
        state = _poll(lambda: (lambda s: s if s and s.get("state") != "sitting" else None)(_try(api.get_character)), 15) or state
    _require(bench, f"{label} is free to use a hand drill", state.get("state") in ("standing", "flying"), json.dumps(state)[:200])
    look = None
    for _ in range(5):
        look = _try(lambda: api.character_look_at(*center))
        if look and look.get("oriented") is True:
            break
    _require(bench, f"{label} aimed at terrain", look is not None)

    def on_voxel(target):
        entity = (target or {}).get("entity")
        return bool(target and target.get("hit") is True and target.get("distance", 99) < 2.7 and target.get("block") is None
                    and (entity is None or VOXEL_HIT.search(str(entity.get("type") or ""))))

    target = None
    for _ in range(30):
        target = _try(lambda: api.get_character_target(5))
        if not require_target or on_voxel(target):
            break
        time.sleep(1)
    record = {"label": label, "character": state, "look": look, "target": target}
    if require_target:
        _require(bench, f"{label} is within hand-drill range of planetary voxel terrain", on_voxel(target), json.dumps(target))
    query = _drill_region(bench, state["position"], coord, storage)
    before = {node: _hash(bench, node, query) for node in ("node-1", "node-2", "wa-1")}
    _require(bench, f"{label} drill region was consistent before drilling", len(set(before.values())) == 1, json.dumps(before))
    record.update(query=query, before=before)
    return record, query, state


def _fly_leg(api, point, reach: float, timeout: float) -> dict:
    """A move-to whose HTTP timeout outlasts the flight (the client's default is shorter)."""
    body = {"x": point[0], "y": point[1], "z": point[2], "speed": "fly", "reach": reach, "timeout": timeout}
    try:
        response = api._client.request("POST", f"{api.base_url}/v1/character/move-to", json=body, timeout=timeout + 30)
        return api._parse(response) or {}
    except Exception as error:  # noqa: BLE001 - a failed leg is evidence
        return {"requestFailed": str(error)}


def _fly_route(api, start, end, center) -> dict:
    """Lift radially to the cruise radius, cruise level to above the destination, land - terrain never blocks it.
    Leg budgets sized for a loaded box: movement input hangs off the client's render loop, and a leg that
    lands 300 m short of a 2 km lift at 120 s is slow, not lost. The property is arrival, not pace."""
    # Climb to one cruise radius above BOTH sites, fly level, descend. A single chord 180 m over each end went
    # into terrain on the 15 km arc: the sites sit at radii 2.6 km apart and the chord also dips chord^2 / 8R
    # below the sphere (runs 20261002-130805Z / -134305Z: ~100 m/s for 28 s, then wedged until the timeout).
    chord = _norm(_sub(end, start))
    radius = max(_norm(_sub(start, center)), _norm(_sub(end, center))) + 180 + chord ** 2 / (8 * _norm(_sub(start, center)))
    lift = _add(center, _scale(_unit(_sub(start, center)), radius))
    cruise = _add(center, _scale(_unit(_sub(end, center)), radius))
    return {"lift": _fly_leg(api, lift, 25, 480), "cruise": _fly_leg(api, cruise, 25, 1200),
            "land": _fly_leg(api, end, 6, 600)}


def p2_vox_01(bench) -> None:
    """Dual-base drilling headline."""
    planet_template(bench)
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    storage, center, size = _earthlike(bench)
    coord = size // 2
    # Both clients render at 5 fps / 800x450: client 2 must FLY in the travel leg (movement input
    # processing needs the render loop) and the rendered-tunnel assert needs it rendered in the first place.
    client = {"max_fps": 5, "resolution": "800x450"}
    api1, character = _join_first(bench, **client)
    api2 = _start_second_client(bench, spawn=True, render=True, **client)

    api1.character_equip("HandDrillItem")
    api2.character_equip("HandDrillItem")
    states = {}

    def equipped():
        states.update(client1=_try(api1.get_character, {}), client2=_try(api2.get_character, {}))
        return all(((s or {}).get("weapon") or {}).get("type") == "MyHandDrill" for s in states.values())

    ok = _poll(equipped, 15)
    bench.record("p2-vox-equipped", states)
    _require(bench, "both clients equipped their inventory hand drills", ok)

    client2_node = _client_node(bench, CLIENT2_ID)
    _require(bench, "a cluster node owns the second client character", client2_node)
    # Site 2 sits at the surface projection of the base area's partition anchor; site 1 is rotated 7.5 km
    # along the surface - far enough that it clears the anchored zone's ~2.5 km envelope by more than
    # sync+margin (4 km), so the sites sit in DISTINCT partitions, yet close enough that the flights are minutes.
    owner1 = _client_node(bench, CLIENT_ID)
    _require(bench, "a cluster node owns the first client character", owner1)
    other = "node-1" if owner1 == "node-2" else "node-2"
    pre = api1.get_character()["position"]
    other_partitions = _partitions(bench, other)
    bench.record("p2-vox-travel-other-partitions", other_partitions)
    radius = _norm(_sub(pre, center))
    spheres = [{**p["sphere"], "altitude": _norm(_sub([p["sphere"]["x"], p["sphere"]["y"], p["sphere"]["z"]], center)) - radius}
               for p in other_partitions]
    anchor = min(spheres, key=lambda s: abs(s["altitude"]), default=None)
    _require(bench, f"the other node ({other}) anchors a partition near the planet", anchor is not None)
    bench.record("p2-vox-travel-anchor", anchor)
    _require(bench, "the other node's nearest partition anchor is near the surface", abs(anchor["altitude"]) < 2500,
             json.dumps(anchor))
    anchor_direction = _unit(_sub([anchor["x"], anchor["y"], anchor["z"]], center))
    site2 = anchor_direction
    current_radius = _norm(_sub(api1.get_character()["position"], center))
    anchor_surface = _add(center, _scale(anchor_direction, current_radius))

    def site1_direction(azimuth: float):
        r = _sub(anchor_surface, center)
        big_r = _norm(r)
        u = _unit(r)
        w = [1, -1, 0]
        d = u[0] * w[0] + u[1] * w[1] + u[2] * w[2]
        v1 = _unit([w[0] - d * u[0], w[1] - d * u[1], w[2] - d * u[2]])
        v2 = _unit(_cross(u, v1))
        a = math.radians(azimuth)
        v = [v1[i] * math.cos(a) + v2[i] * math.sin(a) for i in range(3)]
        t = SITE_ARC_M / big_r
        return [u[i] * math.cos(t) + v[i] * math.sin(t) for i in range(3)]

    site1 = site1_direction(0)
    # The drill choreography below relies on this world's geometry keeping both sites node-1-owned.
    # The shell lane predates the load balancer (T-0141), which migrated client 1's partition to
    # node-2 mid-lane (bench run 20260917-043725Z). Hold the Gateway's automatic moves from here on;
    # the partition merges and splits the sites exercise are node-local and keep running.
    bench.cluster.post("/automatic/pause")
    bench.record("p2-vox-client1-placement", _place(bench, owner1, CLIENT_ID, storage, site1, False))
    # Site 2 sits under the anchor inside the base area's anchored partition zone, so a client arriving there
    # deterministically MERGES into that partition and a client departing deterministically SPLITS out - real
    # movement-driven route switches at the registry (this world's geometry keeps both sites node-1-owned
    # because the only node-2 surface anchor sits inside the anchored base zone).
    bench.record("p2-vox-client2-placement", _place(bench, client2_node, CLIENT2_ID, storage, site2, False))
    time.sleep(3)
    before1, query1, drill_state1 = _prepare_drill_region(bench, api1, "client 1", center, coord, storage, False)
    before2, query2, drill_state2 = _prepare_drill_region(bench, api2, "client 2", center, coord, storage, False)
    _require(bench, "the planet drill sites are farther apart than the 12 km split distance",
             math.dist(drill_state1["position"], drill_state2["position"]) > 12000)
    tail_before = max((j["TailSeq"] for j in bench.cluster.get("/registry/voxel-journals") if j.get("StorageName") == storage),
                      default=0)
    cutouts_before = _node(bench, "wa-1", "/voxel-journal-status")["retainedCutouts"]

    bench.record("p2-vox-client1-final-placement", _place(bench, "node-1", CLIENT_ID, storage, site1, True))
    client2_node = _client_node(bench, CLIENT2_ID)
    _require(bench, "a cluster node owns the second client character", client2_node)
    bench.record("p2-vox-client2-final-placement", _place(bench, client2_node, CLIENT2_ID, storage, site2, True))
    # A character keeps settling after it is placed, so an aligned client can still be pointing at nothing by
    # the time it fires - roughly half of runs had one of the two drills cut no voxel at all, which then
    # surfaced as an unchanged-but-converged hash and read like a replication fault. Re-align whichever
    # client the server says has no voxel under its drill, and only fire once both do.
    prefire = {}
    ready = False
    for azimuth in (0, 90, 180, 270):
        if azimuth:
            # This azimuth's terrain would not take a drill stance - rotate site 1 to a fresh direction (same
            # 7.5 km arc, so all geometry asserts hold) and retry.
            site1 = site1_direction(azimuth)
            _try(lambda: _place(bench, "node-1", CLIENT_ID, storage, site1, False))
            time.sleep(3)
        for _ in range(12):
            prefire["client1"] = _try(lambda: _node(bench, "node-1", f"/voxel-test-player-state?client={CLIENT_ID}"), {})
            prefire["client2"] = _try(lambda: _node(bench, client2_node, f"/voxel-test-player-state?client={CLIENT2_ID}"), {})
            if _voxel_hits(prefire["client1"]) and _voxel_hits(prefire["client2"]):
                ready = True
                break
            if not _voxel_hits(prefire["client1"]):
                _try(lambda: _place(bench, "node-1", CLIENT_ID, storage, site1, True))
            if not _voxel_hits(prefire["client2"]):
                _try(lambda: _place(bench, client2_node, CLIENT2_ID, storage, site2, True))
            time.sleep(1)
        if ready:
            break
    bench.record("p2-vox-prefire-server-state", prefire)
    drill_state1 = {**drill_state1, "position": prefire["client1"].get("position", drill_state1["position"])}
    drill_state2 = {**drill_state2, "position": prefire["client2"].get("position", drill_state2["position"])}
    _require(bench, "both hand drills were aimed at voxel before firing", ready,
             f"client1 hits={_voxel_hits(prefire['client1'])} client2 hits={_voxel_hits(prefire['client2'])}")
    # The pre-fire placement moved the characters after prepare_drill_region measured its 64^3 region, so a
    # cut could land outside it. Re-measure the region and its baseline from where each character is now.
    drill_before = {}
    for site, (state, record) in enumerate(((drill_state1, before1), (drill_state2, before2)), 1):
        query = _drill_region(bench, state["position"], coord, storage)
        hashes = {node: _hash(bench, node, query) for node in ("node-1", "node-2", "wa-1")}
        _require(bench, f"site {site} drill region was consistent before drilling", len(set(hashes.values())) == 1,
                 json.dumps(hashes))
        record.update(query=query, before=hashes, remeasured=True)
        bench.record(f"p2-vox-client{site}-before", record)
        drill_before[site] = (query, hashes["node-1"])
    query1, query2 = drill_before[1][0], drill_before[2][0]

    def refire():
        _try(api1.clear_input_state)
        _try(lambda: _place(bench, "node-1", CLIENT_ID, storage, site1, True))
        time.sleep(2)
        _try(lambda: api1.character_look_at(*center))
        api1.set_input_state(mouse_left=True, mode="override")

    api1.set_input_state(mouse_left=True, mode="override")
    api2.character_primary_action(True)
    # WA-5: REAL client drilling through a node-to-WA outage. Client 1 keeps cutting on node-1's site while
    # node-1's WA link drops frames both ways; the edits must hold in the node's outbox with zero drops during
    # the window and converge exactly after it.
    _require(bench, "the node-1 WA partition knob armed during the client drill",
             _node(bench, "node-1", "/test-wa-partition?ms=15000", "POST").get("active") is True)
    biting = lambda: _voxel_hits(_try(lambda: _node(bench, "node-1", f"/voxel-test-player-state?client={CLIENT_ID}"), {})) > 0  # noqa: E731
    # The prefire gate can pass and the character still settle off-target before the trigger lands (steeper
    # terrain drifts consistently). Watch the drill ray WHILE firing and re-align until it bites INSIDE the
    # outage window, so live edits land in the outbox.
    for _ in range(6):
        time.sleep(1)
        if biting():
            break
        refire()
    # ACTIVE poll: the drill can exhaust its reachable material within seconds, so keep the outage window
    # armed and the drill cutting (re-align on bite loss) until an entry actually queues in the outbox.
    outage_state = {}
    held = False
    for second in range(30):
        if second % 10 == 0:
            _try(lambda: _node(bench, "node-1", "/test-wa-partition?ms=15000", "POST"))
        outage_state = _journal(bench, "node-1")
        if outage_state.get("outboundQueued", 0) >= 1 and outage_state.get("outboxDrops") == 0:
            held = True
            break
        if not biting():
            refire()
        time.sleep(1)
    _require(bench, "the outbox held the live drill edits during the outage", held, json.dumps(_outbox(outage_state)))
    # Deepen the site-1 tunnel: 15 more seconds of sustained cutting (re-aim only on bite loss) so the cut is
    # unambiguously deep for the later rendered-cut observation.
    for _ in range(15):
        if not biting():
            refire()
        time.sleep(1)
    time.sleep(3)
    bench.record("p2-vox-drilling-state", {
        "client1Screens": _try(api1.list_screens), "client2Screens": _try(api2.list_screens),
        "client1": _try(api1.get_character), "client2": _try(api2.get_character),
        "client1Server": _try(lambda: _node(bench, "node-1", f"/voxel-test-player-state?client={CLIENT_ID}")),
        "client2Server": _try(lambda: _node(bench, _client_node(bench, CLIENT2_ID) or "node-2",
                                            f"/voxel-test-player-state?client={CLIENT2_ID}")),
        "client2Target": _try(lambda: api2.get_character_target(5))})
    bench.screenshot(CLIENT_ID, "p2-vox-client1-drilling")
    time.sleep(5)
    api1.clear_input_state()
    api2.character_primary_action(False)
    frames = _node(bench, "node-1", "/test-wa-partition").get("droppedFrames", 0)
    _require(bench, "the WA partition window dropped frames while the client drilled", frames > 0, str(frames))
    bench.record("p2-vox-drill-outage", {"droppedFrames": frames, "heldDuringWindow": _outbox(outage_state)})

    before_1, before_2 = drill_before[1][1], drill_before[2][1]
    cut = {}

    def converged():
        cut.update({f"site{site}": {node: _hash(bench, node, query, timeout=2) for node in ("node-1", "node-2", "wa-1")}
                    for site, query in ((1, query1), (2, query2))})
        s1, s2 = cut["site1"], cut["site2"]
        return (s1["node-1"] and s2["node-2"] and s1["node-1"] != before_1 and len(set(s1.values())) == 1
                and s2["node-2"] != before_2 and len(set(s2.values())) == 1)

    drilled = _poll(converged, HANDOVER_TIMEOUT)
    bench.record("p2-vox-drilling-hashes", cut)
    bench.record("p2-vox-drilling-journals", bench.cluster.get("/registry/voxel-journals"))
    statuses = {node: _journal(bench, node) for node in ("node-1", "node-2", "wa-1")}
    bench.record("p2-vox-journal-status", statuses)
    # An outbox drop means the node applied a voxel edit it could never publish, so it has diverged
    # permanently. Assert it separately from convergence: a lagging replica is a timing problem, this is a
    # lost write.
    for node, status in statuses.items():
        _require(bench, f"{node} dropped no voxel journal entries after applying them locally",
                 (status.get("outboxDrops") or 0) == 0, str(status.get("outboxDrops")))
    if not drilled:
        # Decisive question when a site diverges: is the owning node's state still moving, or is it stably
        # wrong? A moving target means the drill never stopped; a stable difference means the journal
        # genuinely failed to reproduce the local result.
        probe = {"client1": _try(lambda: _node(bench, "node-1", f"/voxel-test-player-state?client={CLIENT_ID}")),
                 "client2": _try(lambda: _node(bench, _client_node(bench, CLIENT2_ID) or "node-2",
                                               f"/voxel-test-player-state?client={CLIENT2_ID}")),
                 "samples": []}
        for _ in range(3):
            probe["samples"].append({node: _hash(bench, node, query1, timeout=5) for node in ("node-1", "node-2", "wa-1")})
            time.sleep(3)
        probe.update(journalNode1=_journal(bench, "node-1"), journalWa=_journal(bench, "wa-1"))
        bench.record("p2-vox-divergence-probe", probe)
        # "did not change and converge" covers three different faults: a drill that cut nothing (aim), an
        # owning node that applied a different edit than it replicated (divergence - the serious one), and a
        # replica that simply has not caught up. Name which one happened.
        why = []
        for site, owner, before in ((1, "node-1", before_1), (2, "node-2", before_2)):
            hashes = cut.get(f"site{site}", {})
            if not all(hashes.values()):
                why.append(f"site{site}: a node did not answer")
            elif hashes[owner] == before:
                why.append(f"site{site}: unchanged on its owning node, so the drill cut nothing")
            elif len(set(hashes.values())) != 1:
                why.append(f"site{site}: DIVERGED ({ {k: v[:8] for k, v in hashes.items()} }) - the owning node applied "
                           "an edit it did not replicate")
            else:
                why.append(f"site{site}: converged")
        _require(bench, "the real hand-drill cuts changed and converged on both regular nodes and the WA", False, "; ".join(why))
    bench.check("the real hand-drill cuts changed and converged on both regular nodes and the WA", True)

    _require(bench, "the WA ordered and persisted both real hand-drill cuts",
             _poll(lambda: (_journal(bench, "wa-1").get("retainedCutouts") or 0) >= cutouts_before + 2, HANDOVER_TIMEOUT))
    compact = _node(bench, "wa-1", "/voxel-compact", "POST")
    compact_tail = next((r["tailSequence"] for r in compact if r.get("storage") == storage), 0)
    _require(bench, "the WA compaction included the real hand-drill tail", compact_tail >= tail_before + 2,
             f"{compact_tail} vs {tail_before}")
    _require(bench, "the Gateway catalogued the compacted real hand-drill tail",
             _poll(lambda: any(j.get("StorageName") == storage and j.get("TailSeq", 0) >= compact_tail
                               for j in _try(lambda: bench.cluster.get("/registry/voxel-journals"), []) or []),
                   HANDOVER_TIMEOUT))

    _chat_and_gps(bench, api1, api2)
    log = bench.clients[CLIENT_ID].log
    _require(bench, "the planet client log is not empty", log.is_file() and log.stat().st_size > 0)
    bench.record("planet-client-observation", {"character": character, "target": _try(api1.get_character_target),
                                               "route": bench.cluster.get("/transport/status")})

    # WA-5 client flow: the clients FLY to each other's drill sites (the sites are asserted >3 km apart, so the
    # flight crosses the partition boundary), the cluster re-routes each client to the other owner, and each
    # RENDERED client then observes the other site's tunnel with its own client-side raycast.
    def census(client_id):
        node_id = _client_node(bench, client_id)
        if node_id is None:
            return None
        return next((p["id"] for p in _try(lambda: _partitions(bench, node_id), []) or []
                     if client_id in (p.get("clients") or [])), None)

    route1_before, route2_before = census(CLIENT_ID), census(CLIENT2_ID)
    _require(bench, "the clients start in distinct partitions",
             route1_before is not None and route2_before is not None and route1_before != route2_before,
             f"client1 {route1_before}, client2 {route2_before}")
    # The drill stances are wherever the drillers ENDED after the re-aligned cutting, not the original
    # placement ray - read them live so the flights and the rendered-cut observation target the deep cuts.
    s1, s2 = api1.get_character()["position"], api2.get_character()["position"]
    # The drill phase leaves client 1 holding a mouse override and client 2 a primary-action injection; both
    # would fight the flight's input frames.
    for api in (api1, api2):
        _try(api.clear_input_state)
        _try(lambda: api.character_primary_action(False))
    # SEQUENCED: client 1 flies first while client 2 holds its site. The arrival inside sync distance merges
    # the two client partitions - a movement-driven handover that switches a real client route. Client 2 then
    # flies to site 1, splitting back out of the merged partition on the way.
    flight1 = _fly_route(api1, s1, s2, center)
    bench.record("p2-vox-travel-client1", flight1)
    _require(bench, "client 1 flew to the opposite drill site", flight1["land"].get("reached") is True, json.dumps(flight1))
    routes = {}

    def merged():
        routes.update(client1=census(CLIENT_ID), client2=census(CLIENT2_ID))
        return routes["client1"] is not None and routes["client1"] != route1_before and routes["client1"] == routes["client2"]

    _require(bench, "client 1's flight switched its route into the destination partition", _poll(merged, HANDOVER_TIMEOUT),
             f"route {route1_before} -> {routes}")
    bench.record("p2-vox-travel-client2-prefly", {
        "character": _try(api2.get_character), "screens": _try(api2.list_screens),
        "server": _try(lambda: _node(bench, _client_node(bench, CLIENT2_ID) or "node-1",
                                     f"/voxel-test-player-state?client={CLIENT2_ID}"))})
    flight2 = _fly_route(api2, s2, s1, center)
    bench.record("p2-vox-travel-client2", flight2)
    _require(bench, "client 2 flew to the opposite drill site", flight2["land"].get("reached") is True, json.dumps(flight2))

    def split():
        routes.update(client1=census(CLIENT_ID), client2=census(CLIENT2_ID))
        return routes["client1"] is not None and routes["client2"] is not None and routes["client1"] != routes["client2"]

    _require(bench, "client 2's departure split it back into its own partition", _poll(split, HANDOVER_TIMEOUT), json.dumps(routes))

    def place_observer(client_id, direction) -> bool:
        # Try the placement on BOTH nodes: this leg runs right after the flights switched the clients'
        # routes, and a silent single-node fallback could place nothing.
        return any(_try(lambda: _place(bench, node_id, client_id, storage, direction, True)) is not None
                   for node_id in ("node-1", "node-2"))

    # Each rendered client observes the OTHER site's tunnel: aiming at the planet from (at most 6 m off) the
    # other driller's stance, the raycast must NOT find intact surface inside the original hand-drill range.
    for index, api, client_id, hop, stance, direction in (
            (1, api1, CLIENT_ID, s2, drill_state2["position"], site2),
            (2, api2, CLIENT2_ID, s1, drill_state1["position"], site1)):
        # Precision hop onto the other driller's exact stance; the altitude/mouth reference is its PRE-drill
        # stance: the driller itself ends standing on the cut floor.
        _try(lambda: api.character_move_to(*hop, speed="fly", timeout=90, reach=2))
        if not place_observer(client_id, direction):
            print(f"  (observer pre-placement did not land for {client_id})")
        time.sleep(3)
        # The rendered proof is PHYSICAL: a point 3.5 m straight down from the stance, into the cut.
        mouth = _add(stance, _scale(_unit(_sub(center, stance)), 3.5))
        enter, target, seen = {"noAttempt": True}, None, False
        for attempt in range(6):
            place_observer(client_id, direction)
            # The server-side snap does not bind a client-authoritative character: the client's own position
            # stream overrides it. The client's own move-to always wins, so approach the stance with it; the
            # snap then only has centimeters left to correct.
            _try(lambda: _fly_leg(api, stance, 3, 40))
            place_observer(client_id, direction)
            time.sleep(5)
            # PRIMARY proof - altitude: the align hook plants the observer on the CURRENT surface along the
            # site direction. Settled at least 0.4 m radially below the driller's pre-drill stance means the
            # observer's rendered world contains the cut.
            observed = (_try(api.get_character) or {}).get("position")
            if observed and _norm(_sub(observed, center)) <= _norm(_sub(stance, center)) - 0.4:
                target, seen = {"altitudeProof": True, "observerPosition": observed}, True
                break
            # Alternate proof - the pre-drill pose ray finds no near surface.
            target = _try(lambda: api.get_character_target(6))
            if target is not None and (target.get("hit") is False or target.get("distance", 0) >= 2.9):
                seen = True
                break
            # Fallback proof: descend into the cut itself.
            enter = _fly_leg(api, mouth, 2, 80)
            if enter.get("reached") is True:
                seen = True
                break
        bench.record(f"p2-vox-travel-observe-client{index}", {"mouth": mouth, "enter": enter, "target": target})
        _require(bench, f"client {index} observed the tunnel at the other drill site", seen,
                 f"target={target} enter={enter}")
    bench.exercised(routes={"client1": [route1_before, routes.get("client1")], "client2": [route2_before, routes.get("client2")]})

    bench.cluster.restart_node("node-1")
    for marker in ("Game ready", "voxel_base fetched=true storage=EarthLike",
                   "voxel_catalog base_installed=true storage=EarthLike"):
        bench.cluster.wait_node_log("node-1", marker, SERVER_TIMEOUT)
    # 10 s answers, not 2: the range hash marshals to the game thread of a node that just restarted. The
    # reference is the CURRENT cross-role state, not the pre-kill capture: the surviving roles keep
    # simulating and journaling while node-1 is down. The restore promise is (a) node-1 converges with the
    # live roles and (b) each cut is still a cut - different from its pre-drill baseline.
    restored = {}

    def restarted():
        restored.update({f"site{site}": {node: _hash(bench, node, query, timeout=10) for node in ("node-1", "node-2", "wa-1")}
                         for site, query in ((1, query1), (2, query2))})
        return all(hashes["node-1"] and len(set(hashes.values())) == 1 and hashes["node-1"] != before
                   for hashes, before in ((restored["site1"], before_1), (restored["site2"], before_2)))

    _require(bench, "the restarted node 1 restored both compacted hand-drill cuts", _poll(restarted, HANDOVER_TIMEOUT * 2),
             json.dumps({"now": restored, "preDrill": [before_1, before_2], "preKill": cut}))
    bench.record("p2-vox-real-drilling", {"storage": storage, "clients": [drill_state1, drill_state2],
                                          "regions": [{"query": query1, "before": before_1, "after": cut["site1"], "restarted": restored["site1"]},
                                                      {"query": query2, "before": before_2, "after": cut["site2"], "restarted": restored["site2"]}],
                                          "tailBefore": tail_before, "tailAfter": compact_tail, "compaction": compact})

    # WA-5 weather authority: the WA owns the weather decision and pushes the state; the nodes apply it
    # read-only and vanilla replicates it to their own clients. Node-1 was just RESTARTED, so its copy can
    # only come from the WA push. Lightning executes on the node that holds the strike position's entities -
    # here forced on node-1 only.
    weather_before = _node(bench, "node-2", "/weather")
    weather = next((d for d in _node(bench, "wa-1", "/weather").get("definitions") or [] if d != "Clear"), None)
    _require(bench, "a public non-Clear weather definition is available", weather)
    weather_set = _node(bench, "wa-1", f"/weather-test-set?weather={urllib.parse.quote(weather, safe='')}&length=300", "POST")
    _require(bench, "the WA applied the weather decision", weather_set.get("applied") is True, json.dumps(weather_set))
    views = {}

    def has_weather():
        views.update({node: _try(lambda: _node(bench, node, "/weather"), {}) or {} for node in ("node-1", "node-2")})
        return all(weather in [w.get("weather") for p in view.get("planets") or [] for w in p.get("weathers") or []]
                   for view in views.values())

    _require(bench, "the WA weather decision converged on both nodes", _poll(has_weather, HANDOVER_TIMEOUT), json.dumps(views)[:400])
    lightning = _node(bench, "node-1", "/weather-test-lightning", "POST")
    _require(bench, "node-1 spawned the dispatched lightning", lightning.get("lightnings", 0) >= 1, json.dumps(lightning))
    after = _node(bench, "node-2", "/weather").get("lightnings", 0)
    _require(bench, "no lightning leaked onto node-2, which does not hold the strike position",
             after == weather_before.get("lightnings", 0), f"{weather_before.get('lightnings', 0)} -> {after}")
    bench.record("p2-vox-weather", {"weather": weather, "set": weather_set, "views": views, "lightning": lightning})


# -- P2-DIF-01 ------------------------------------------------------------------------------


def p2_dif_01(bench) -> None:
    """The planetary baseline versus its cluster representation. The split (cluster) form of the planet
    world must reassemble into a vanilla world that is semantically identical to the baseline for every
    partitioned entity, keep every voxel storage byte-identical, and load on a stock (non-clustered) server
    with every grid's blocks intact. No cluster, no client - it proves the representation itself."""
    world = planet_template(bench)
    config = bench.cluster.config
    bench.cluster.kill()
    shutil.rmtree(config.split_world, ignore_errors=True)
    roundtrip = config.root / "planet-roundtrip"
    shutil.rmtree(roundtrip, ignore_errors=True)
    split = _world_tool(bench, "to-split", world, config.split_world)
    bench.record("planet-diff-split.txt", split.stdout + split.stderr)
    _require(bench, "the planet world split", split.returncode == 0, split.stderr[-300:])
    back = _world_tool(bench, "to-vanilla", config.split_world, roundtrip)
    bench.record("planet-diff-roundtrip.txt", back.stdout + back.stderr)
    _require(bench, "the planet conversion took the checkpoint from the seed global save",
             back.returncode == 0 and "checkpoint from global save" in back.stdout + back.stderr, back.stdout[-300:])
    diff = _world_tool(bench, "semantic-diff", world / "SANDBOX_0_0_0_.sbs", roundtrip / "SANDBOX_0_0_0_.sbs",
                       _catalog_ids(config.split_world))
    bench.record("planet-diff-semantic.txt", diff.stdout + diff.stderr)
    bench.check("the planetary cluster round trip is semantically identical to the baseline", diff.returncode == 0,
                diff.stdout[-300:])
    changed = [voxel.name for voxel in sorted(world.glob("*.vx2"))
               if not (roundtrip / voxel.name).is_file() or (roundtrip / voxel.name).read_bytes() != voxel.read_bytes()]
    bench.check("every voxel storage is byte-identical after the round trip", not changed, str(changed))
    _stock_load_check(bench, roundtrip, world / "SANDBOX_0_0_0_.sbs", "planet", ("Game ready",))
    entities = _try(lambda: json.loads(diff.stdout.strip().splitlines()[-1]).get("entities"))
    bench.exercised(entities=entities, voxels=len(list(world.glob("*.vx2"))))


# -- P2-CNV-01 ------------------------------------------------------------------------------


def p2_cnv_01(bench) -> None:
    """vanilla -> split -> cluster run -> vanilla round trip.

    Leg 1 proves pure tool fidelity: converting the split world straight back (no cluster state) is
    semantically identical to the source for every partitioned entity. Leg 2 proves the live path: a real
    client mutation and the run's identity bindings survive graceful shutdown, newest-generation reassembly
    across the node/WA data roots, and a stock (non-clustered) server load of the converted world."""
    api = _join(bench)
    config = bench.cluster.config
    template = Path(config.world_template)
    roundtrip = config.root / "roundtrip-world"
    shutil.rmtree(roundtrip, ignore_errors=True)
    back = _world_tool(bench, "to-vanilla", config.split_world, roundtrip)
    bench.record("conversion-roundtrip.txt", back.stdout + back.stderr)
    diff = _world_tool(bench, "semantic-diff", template / "SANDBOX_0_0_0_.sbs", roundtrip / "SANDBOX_0_0_0_.sbs",
                       _catalog_ids(config.split_world))
    bench.record("conversion-roundtrip-diff.txt", diff.stdout + diff.stderr)
    bench.check("the pure split round trip is semantically identical", back.returncode == 0 and diff.returncode == 0,
                diff.stdout[-300:])

    # Replicated client mutation to carry through the conversion: disable the survival kit through the real
    # client's grid ops.
    kit = _poll(lambda: next(iter(_try(lambda: api.find_nearby_blocks(block_type="SurvivalKit", max_distance=100, limit=1), [])
                                  or []), None), HANDOVER_TIMEOUT)
    _require(bench, "a survival kit is near the client", kit)
    result = _grid_ops(api, {"sets": [{"type": "enabled", "gridId": kit["gridId"], "min": kit["min"], "value": False}]})
    bench.record("conversion-disable-kit", result)
    _require(bench, "the conversion kit mutation succeeded", result["sets"][0].get("success"), json.dumps(result))

    shutdown = _graceful_shutdown(bench, "conversion")
    _wait_exited(bench, ["node-1", "node-2", "wa-1"], "conversion")
    nodes = _nodes(bench)
    vanilla = config.root / "vanilla-world"
    shutil.rmtree(vanilla, ignore_errors=True)
    converted = _world_tool(bench, "to-vanilla", config.split_world, vanilla,
                            *(config.node_data(nodes[node]) for node in ("node-1", "node-2", "wa-1")))
    bench.record("conversion-to-vanilla.txt", converted.stdout + converted.stderr)
    _require(bench, "to-vanilla converted the live cluster state", converted.returncode == 0, converted.stderr[-300:])
    bench.check("the conversion took the checkpoint from a global save",
                "checkpoint from global save" in converted.stdout + converted.stderr)
    # Identity round trip: the run's client identity binding must be in the converted checkpoint. Every
    # original GRID must survive; the premade character may legitimately be replaced by a fresh one when the
    # client spawns through the respawn screen instead of attaching, so characters are asserted by presence.
    granted = [re.search(r" identity (\d+) ", line) for line in
               _lines(bench, "gateway", rf"Player binding granted client {CLIENT_ID} identity \d+ ")]
    identities = {match.group(1) for match in granted if match}
    players = (vanilla / "Sandbox.sbc").read_text(errors="replace")
    table = players[players.find("<AllPlayersData>"):players.find("</AllPlayersData>")]
    rows = set(re.findall(r"<IdentityId>(\d+)</IdentityId>", table))
    bench.check("the converted checkpoint kept the client identity binding",
                bool(identities) and identities <= rows, f"granted={sorted(identities)} rows={sorted(rows)}")
    grids = {entity_id for entity_id, kind, _ in _sector_objects(template / "SANDBOX_0_0_0_.sbs")
             if kind == "MyObjectBuilder_CubeGrid"}
    after = _sector_objects(vanilla / "SANDBOX_0_0_0_.sbs")
    missing = grids - {entity_id for entity_id, _, _ in after}
    characters = [entity_id for entity_id, kind, _ in after if kind == "MyObjectBuilder_Character"]
    bench.check("the converted world kept every original grid and a character", not missing and characters,
                f"missing {sorted(missing)}, {len(characters)} characters")
    # The replicated mutation survives: the survival kit grid carries Enabled=false.
    kit_grid = next((entity for entity_id, _, entity in after if entity_id == str(kit["gridId"])), None)
    kits = [block for block in kit_grid.iter() if block.get(XSI_TYPE) == "MyObjectBuilder_SurvivalKit"] if kit_grid is not None else []
    bench.check("the converted world kept the survival kit mutation",
                kits and all(block.findtext("Enabled") == "false" for block in kits), f"{len(kits)} kits")

    # Stock load: a server without any cluster plugin loads the converted world to Game ready.
    _stock_load_check(bench, vanilla, template / "SANDBOX_0_0_0_.sbs", "converted", ("Session loaded", "Game ready"))
    bench.record("conversion", {"shutdown": shutdown, "roundtripDiff": diff.stdout, "toVanilla": converted.stdout})
    bench.exercised(kitGrid=kit["gridId"], grids=len(grids), characters=len(characters))


SCENARIOS_PLANET = {
    "P2-VOXB": (p2_voxb, "voxel edit broadcast to a second node", "3"),
    "P2-VOX-02": (p2_vox_02, "voxel journal under chaos and catalog rebuild", "4"),
    "P2-VOX-01": (p2_vox_01, "dual-base drilling headline", "3"),
    "P2-DIF-01": (p2_dif_01, "planetary differential baseline", "3"),
    "P2-CNV-01": (p2_cnv_01, "vanilla conversion round-trip", "3"),
}
