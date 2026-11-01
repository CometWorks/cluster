"""Suite self-checks: the machinery the scenarios stand on, asserted like any other scenario.

SUITE-TPL-01 covers the test utility plugin (``ClusterTestPlugin/``, CleanupPlan step 2):

* with the plugin, every node role answers its test routes, and a moved route runs the plugin's
  handler (a positive control that changes nothing, and a negative control that must fail inside
  the handler, not in route lookup);
* without it, the same deployment still answers every production route of
  ``Docs/Gateway/NodeAPI.md`` and none of the routes that have moved out of the product.
"""

from __future__ import annotations

from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import random
import time
import uuid
import re

import bench as bench_module
from bench import HOST
from cluster import ClusterError, wait_until
from scenarios_core import _lines

#: Routes the test plugin serves that still exist in the product as well (none should): the self-check
#: skips them in the "moved routes are gone" leg.
STILL_IN_PRODUCT: set[str] = set()
NODE_API = HOST.package / "docs/NodeAPI.md"
UNKNOWN_ROUTE = "unknown cluster control route"


def production_get_routes() -> list[str]:
    """GET routes of NodeAPI.md that need no query parameter (so a 404 can only mean 'no route').
    A table cell lists routes as `GET /cluster/a`, `GET|POST /cluster/b`, `/c` (same verb as the
    previous one) or `GET /cluster/d?x=` (skipped: it needs a parameter)."""
    routes = set()
    for line in NODE_API.read_text().splitlines():
        if not line.startswith("| `"):
            continue
        method = None
        for code in re.findall(r"`([^`]*)`", line.split(" | ")[0]):
            match = re.fullmatch(r"(?:([A-Z|]+) )?(/[A-Za-z0-9/_-]+)(\?.*)?", code.strip())
            if not match:
                continue
            verbs, path, query = match.groups()
            method = verbs or method
            if method and "GET" in method.split("|") and not query:
                routes.add(path.removeprefix("/cluster"))
    return sorted(routes)


def route_state(bench, node_id: str, method: str, path: str) -> tuple[str, str]:
    """('ok' | 'missing' | 'error', detail) for one route on one node."""
    try:
        return "ok", json.dumps(bench.cluster.node(node_id, path, method=method, timeout=30))[:200]
    except ClusterError as error:
        text = str(error)
        if f"-> 404: {UNKNOWN_ROUTE}" in text:
            return "missing", text[:200]
        return "error", text[:200]


def some_entity(bench, timeout: float = 90) -> int:
    """Any entity node-1 hosts. Its partitions load a moment after "Game ready" (the registry
    assigns them), so an empty report right after startup means "not yet"."""
    deadline = time.monotonic() + timeout
    while True:
        partitions = bench.cluster.node("node-1", "/partitions")
        for partition in partitions if isinstance(partitions, list) else partitions.get("partitions", []):
            for member in partition.get("members") or partition.get("entities") or []:
                return int(member["id"] if isinstance(member, dict) else member)
        if time.monotonic() >= deadline:
            raise ClusterError("node-1 hosts no entity: " + json.dumps(partitions)[:300])
        time.sleep(2)


def suite_tpl_01(bench) -> None:
    bench.check("the suite built and installed the test plugin", bench_module.TEST_PLUGIN is not None,
                json.dumps({role: str(path) for role, path in (bench_module.TEST_PLUGIN or {}).items()}))
    bench.start_cluster(nodes=["node-1", "wa-1"])
    moved: dict[str, list[str]] = {}
    for node_id, assembly in (("node-1", "ClusterNode"), ("wa-1", "WorldAuthority")):
        try:
            answer = bench.cluster.node(node_id, "/test-plugin")
        except ClusterError as error:
            answer = {"error": str(error)[:200]}
        bench.check(f"{node_id} serves the test plugin built for {assembly}",
                    answer.get("clusterAssembly") == assembly, json.dumps(answer)[:200])
        moved[node_id] = [route for route in answer.get("routes") or [] if route not in STILL_IN_PRODUCT]
    bench.record("test-plugin-routes", moved)
    bench.check("the plugin serves the moved route set (more than the trash group)",
                len(moved.get("node-1") or []) > 50, str(len(moved.get("node-1") or [])))

    entity = some_entity(bench)
    state, detail = route_state(bench, "node-1", "POST", f"/test-position?entity={entity}&relativeTo={entity}")
    bench.check("a moved route runs: /test-position with a zero offset answers 'positioned'",
                state == "ok" and "positioned" in detail, detail)
    state, detail = route_state(bench, "node-1", "POST", "/test-position?entity=1&relativeTo=2")
    bench.check("the moved route's own handler rejects an unknown entity (negative control)",
                state == "error" and "Unknown entity" in detail, detail)

    # The same deployment without the plugin: production routes stay, moved routes are gone.
    bench.cluster.kill()
    bench.cluster.config.extra_plugins = {"node": [], "wa": []}
    bench.start_cluster(nodes=["node-1", "wa-1"])
    routes = production_get_routes()
    bench.check("NodeAPI.md lists parameterless GET routes to probe", len(routes) >= 10, ", ".join(routes))
    for node_id in ("node-1", "wa-1"):
        missing = {}
        for path in routes:
            state, detail = route_state(bench, node_id, "GET", path)
            if state == "missing":
                missing[path] = detail
        bench.check(f"{node_id} without the plugin serves every production GET route of NodeAPI.md",
                    not missing, json.dumps(missing)[:400])
        # No query parameters: a route that exists answers 400/500 on them, a missing one 404s first.
        present = {}
        for route in moved.get(node_id) or []:
            method, path = route.split(" ", 1)
            method = "GET" if method == "*" else method
            state, detail = route_state(bench, node_id, method, path)
            if state != "missing":
                present[route] = detail
        bench.check(f"{node_id} without the plugin serves none of the {len(moved.get(node_id) or [])} moved routes",
                    bool(moved.get(node_id)) and not present, json.dumps(present)[:400])
    bench.exercised(productionRoutes=len(routes), movedRoutes=len(moved.get("node-1") or []))


def _plugin_services_ready(bench, processes: list[str], timeout: float = 90) -> None:
    """Wait until every process's plugin roster (NodesAsync) lists all of them. start_cluster returns once each node
    logs Serving, but the registry counts a node ready only after its next heartbeat says so; plugin calls in that
    window answer Unavailable/Fenced (T-0256 SDK-02/07 first bench run, 2026-09-26 18:44)."""
    want = sorted(processes)
    seen: dict = {}

    def ready():
        for node_id in processes:
            seen[node_id] = sorted(r["node"] for r in (bench.cluster.node(node_id, "/plugin-nodes").get("nodes") or []))
        return all(v == want for v in seen.values()) or None

    try:
        wait_until(ready, timeout, "every process's plugin roster to list all processes")
    finally:
        bench.record("plugin-roster-ready", seen)


def suite_sdk_01(bench) -> None:
    """T-0256: PluginSdk's PluginStorage.GetSharedDirectory is ONE directory for the whole cluster.

        The launcher sets CLUSTER_SHARED_ROOT=<root>/shared on every node and the WA. The test plugin is
        one plugin id ("cluster-test") on every role, as a real plugin is. node-1 writes a file through the
        SDK; node-2 and wa-1 must resolve the same directory and read the same bytes, and the file is on the
        host at that path. CONTROL: before the write, node-2 reads the file as absent - the read reflects the
        file, not a constant - and a name nobody wrote stays absent everywhere afterwards. Another plugin's id
        is refused (loader identity), and an id that is not a valid name is refused as an argument.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])
    name, token = "marker-" + uuid.uuid4().hex[:8], uuid.uuid4().hex
    plugins = bench.cluster.config.root / "shared" / "plugins"
    before = bench.cluster.node("node-2", f"/plugin-storage?read={name}")
    bench.record("sdk-before", before)
    bench.check("CONTROL: node-2 reads the marker as absent before node-1 writes it", before.get("read") is None
                and before.get("error") is None, json.dumps(before)[:300])
    wrote = bench.cluster.node("node-1", f"/plugin-storage?write={name}&text={token}&read={name}")
    bench.record("sdk-node-1", wrote)
    plugin, expected = wrote.get("ownId"), wrote.get("directory")
    bench.check("node-1 is a cluster process and its shared directory is <root>/shared/plugins/<folder>",
                plugin == "cluster-test" and wrote.get("isClusterShared") is True and expected is not None
                and Path(expected).parent == plugins, json.dumps(wrote)[:300])
    bench.check("node-1 reads back what it wrote", wrote.get("read") == token, json.dumps(wrote)[:300])
    on_host = Path(expected or "/nonexistent") / name
    bench.check("the marker is on the host filesystem at that path", on_host.is_file() and on_host.read_text() == token, str(on_host))
    for node_id in ("node-2", "wa-1"):
        seen = bench.cluster.node(node_id, f"/plugin-storage?read={name}")
        bench.record(f"sdk-{node_id}", seen)
        bench.check(f"{node_id} is bound to the same plugin id and resolves the same directory",
                    seen.get("ownId") == plugin and seen.get("directory") == expected, json.dumps(seen)[:300])
        bench.check(f"{node_id} reads node-1's marker", seen.get("read") == token, json.dumps(seen)[:300])
        unwritten = bench.cluster.node(node_id, "/plugin-storage?read=never-written")
        bench.check(f"CONTROL: {node_id} reads a file nobody wrote as absent", unwritten.get("read") is None
                    and unwritten.get("directory") == expected, json.dumps(unwritten)[:300])
    foreign = bench.cluster.node("node-1", "/plugin-storage?plugin=cluster-node")
    bench.check("another plugin's id is refused (loader identity)", foreign.get("error") == "InvalidOperationException",
                json.dumps(foreign)[:300])
    bad = bench.cluster.node("node-1", "/plugin-storage?plugin=bad%5Cid")
    bench.check("an id that is not a valid name is refused", bad.get("error") == "ArgumentException", json.dumps(bad)[:300])
    bench.exercised(directory=expected, processes=["node-1", "node-2", "wa-1"])


def suite_sdk_02(bench) -> None:
    """T-0256: PluginSdk BroadcastAsync reaches the plugin's topic handler on every node and the WA, once.

        The test plugin ("cluster-test" on every role) subscribes a process on its first /plugin-broadcast call.
        CONTROL: node-2 is left unsubscribed for the first broadcast and must show as Unsupported with the
        others Success - the per-node result reflects the handler, not a constant - and it must not receive
        that message later. Then broadcasts from node-2 and from the WA reach all three; wa=0 leaves the WA out.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])
    everyone = {"node-1", "node-2", "wa-1"}

    def call(node_id: str, query: str = "") -> dict:
        reply = bench.cluster.node(node_id, "/plugin-broadcast" + query)
        bench.record(f"broadcast-{node_id}{query[:24]}", reply)
        return reply

    for node_id in ("node-1", "wa-1"):
        sub = call(node_id)
        bench.check(f"{node_id} subscribes as cluster-test", sub.get("subscribed") is True and sub.get("pluginId") == "cluster-test",
                    json.dumps(sub)[:300])

    def codes(reply: dict) -> dict:
        return {node: entry.get("code") for node, entry in ((reply.get("broadcast") or {}).get("nodes") or {}).items()}

    first = call("node-1", "?send=first")
    bench.check("CONTROL: first broadcast - node-1 and wa-1 Success, unsubscribed node-2 Unsupported",
                codes(first) == {"node-1": "Success", "wa-1": "Success", "node-2": "Unsupported"}, json.dumps(first)[:400])
    bench.check("replies carry each handler's own node id",
                (((first.get("broadcast") or {}).get("nodes") or {}).get("wa-1") or {}).get("reply") == "wa-1:first", json.dumps(first)[:400])
    sub = call("node-2")
    bench.check("node-2 subscribes", sub.get("subscribed") is True, json.dumps(sub)[:300])
    second = call("node-2", "?send=second")
    bench.check("a broadcast from node-2 reaches all three handlers", codes(second) == {n: "Success" for n in everyone}
                and (second.get("broadcast") or {}).get("code") == "Success", json.dumps(second)[:400])
    third = call("wa-1", "?send=third")
    bench.check("a broadcast from the WA reaches all three handlers", codes(third) == {n: "Success" for n in everyone},
                json.dumps(third)[:400])
    fourth = call("node-1", "?send=fourth&wa=0")
    bench.check("wa=0 leaves the WA out", codes(fourth) == {"node-1": "Success", "node-2": "Success"}, json.dumps(fourth)[:400])
    ops = {text: (reply.get("broadcast") or {}).get("operation") for text, reply in
           (("first", first), ("second", second), ("third", third), ("fourth", fourth))}
    expect = {"node-1": ["first", "second", "third", "fourth"], "node-2": ["second", "third", "fourth"],
              "wa-1": ["first", "second", "third"]}
    for node_id, texts in expect.items():
        got = call(node_id).get("received") or []
        seen = [entry.get("text") for entry in got if entry.get("operation") in ops.values()]
        bench.check(f"{node_id} received exactly {texts}, once each", sorted(seen) == sorted(texts), json.dumps(got)[:400])
        bench.check(f"{node_id}: every copy carries its broadcast's operation id and ran on the game thread",
                    all(entry.get("operation") == ops.get(entry.get("text")) and entry.get("gameThread") is True for entry in got),
                    json.dumps(got)[:400])
    bench.exercised(processes=sorted(everyone), operations=ops)


def suite_sdk_08(bench) -> None:
    """SE-852: on a healthy cluster with no fence changing, the plugin bus never answers Fenced.

        Steady: every process broadcasts in a loop, so messages land at any point of a node's update. Burst: more
        messages at once than one handler pump takes. Stall: messages reach a node during a long game-thread frame.
        CONTROL: a reply counts as Success only with the node's own name and the text sent, so it came from the handler.
    """
    processes = ["node-1", "node-2", "wa-1"]
    bench.start_cluster(nodes=processes)
    _plugin_services_ready(bench, processes)
    for node_id in processes:
        bench.cluster.node(node_id, "/plugin-broadcast")
    tally: dict[str, Counter] = {"steady": Counter(), "burst": Counter(), "stall": Counter()}

    def broadcast(phase: str, source: str, text: str) -> None:
        nodes = (bench.cluster.node(source, "/plugin-broadcast?send=" + text).get("broadcast") or {}).get("nodes") or {}
        for node_id, entry in nodes.items():
            good = entry.get("code") != "Success" or entry.get("reply") == f"{node_id}:{text}"
            tally[phase][(entry.get("code") if good else "WrongReply")] += 1

    loops = int(os.environ.get("CLUSTER_SDK08_BROADCASTS", "100"))
    bursts = int(os.environ.get("CLUSTER_SDK08_BURSTS", "5"))
    stalls = int(os.environ.get("CLUSTER_SDK08_STALLS", "10"))

    def steady(source: str) -> None:
        for i in range(loops):
            time.sleep(random.uniform(0, 0.25))  # A reply leaves right after an update: without this the next message keeps one phase
            broadcast("steady", source, f"steady-{source}-{i}")

    with ThreadPoolExecutor(len(processes)) as pool:
        list(pool.map(steady, processes))
    for _ in range(bursts):
        time.sleep(random.uniform(0, 0.25))
        for codes in bench.cluster.node("node-1", "/plugin-broadcast-burst?count=24")["nodes"].values():
            tally["burst"].update(codes)
    for round_id in range(stalls):
        bench.cluster.node("node-2", "/game-thread-stall?ms=600")
        time.sleep(0.1)
        broadcast("stall", "wa-1", f"stall-{round_id}")

    counts = {phase: dict(codes) for phase, codes in tally.items()}
    bench.record("bus-codes", counts)
    for phase, codes in tally.items():
        bench.check(f"{phase}: every message of {sum(codes.values())} answered Success", set(codes) == {"Success"}, json.dumps(codes))
    bench.exercised(processes=processes, codes=counts)


VIEW_CLIENT_ID = 76561199500009256
VIEW_REMOTE_PORT = 24158 + 56


def suite_sdk_03(bench) -> None:
    """T-0256: PluginSdk cluster views - online players, positions, entity placement - agree on two nodes and the WA.

        CONTROL: before the client joins, every process lists NO players (an empty list, not null) - the view
        reflects the cluster, not a constant. After it spawns, all three list the same player on the same node
        and its character at its position; LocateEntity says Local on the hosting node, with a partition whose
        ResolveOwnerAsync owner is that node, and Absent on the other node and the WA. A grid spawned there
        places the same way.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])
    processes = ["node-1", "node-2", "wa-1"]

    def views(node_id: str, query: str = "") -> dict:
        return bench.cluster.node(node_id, "/plugin-views" + query)

    before = {n: views(n) for n in processes}
    bench.record("views-before", before)
    bench.check("CONTROL: before the client joins every process lists no players (empty, not null)",
                all(v.get("players") == [] and v.get("positions") == [] for v in before.values()), json.dumps(before)[:500])
    api = bench.start_client(VIEW_CLIENT_ID, VIEW_REMOTE_PORT)
    character = bench.wait_spawned(VIEW_CLIENT_ID)
    bench.record("character", character)
    steam = str(VIEW_CLIENT_ID)
    deadline, seen = time.time() + 60, {}
    while time.time() < deadline:
        seen = {n: views(n) for n in processes}
        if all(any(p.get("steamId") == steam for p in (v.get("players") or [])) and
               any(p.get("steamId") == steam and p.get("EntityId") for p in (v.get("positions") or [])) for v in seen.values()):
            break
        time.sleep(2)
    bench.record("views-joined", seen)
    entries = {n: next((p for p in (v.get("players") or []) if p.get("steamId") == steam), None) for n, v in seen.items()}
    host = (entries.get("node-1") or {}).get("Node")
    bench.check("all three list the client, attached to the same node", host in ("node-1", "node-2")
                and all((e or {}).get("Node") == host for e in entries.values()), json.dumps(entries)[:500])
    positions = {n: next((p for p in (v.get("positions") or []) if p.get("steamId") == steam), None) for n, v in seen.items()}
    body = (positions.get(host) or {}).get("EntityId")
    bench.check("all three give the same entity for the client's position", body and all((p or {}).get("EntityId") == body
                for p in positions.values()), json.dumps(positions)[:500])
    placed = {n: views(n, f"?entity={body}").get("placement") for n in processes}
    bench.record("views-placement", placed)
    hosted = placed.get(host) or {}
    bench.check(f"LocateEntity: the client's body is Local on {host} with a partition",
                hosted.get("residence") == "Local" and hosted.get("partition") not in (None, "0") and hosted.get("Node") == host,
                json.dumps(placed)[:500])
    bench.check("LocateEntity: Absent on the other node and the WA",
                all((placed.get(n) or {}).get("residence") == "Absent" for n in processes if n != host), json.dumps(placed)[:500])
    other = next(n for n in ("node-1", "node-2") if n != host)
    owner = views(other, f"?resolve={hosted.get('partition')}").get("owner") or {}
    bench.check(f"ResolveOwnerAsync from {other}: that partition's owner is {host}",
                owner.get("code") == "Success" and owner.get("node") == host, json.dumps(owner))
    x, y, z = (positions.get(host) or {}).get("position") or [0, 0, 0]
    grid = bench.cluster.node(host, f"/trash-test-spawn?x={x + 30}&y={y}&z={z}&blocks=27", method="POST").get("grid")
    # A new grid joins a partition at the next membership sweep; Unowned until then.
    deadline = time.time() + 15
    while (views(host, f"?entity={grid}").get("placement") or {}).get("residence") != "Local" and time.time() < deadline:
        time.sleep(1)
    grid_placed = {n: views(n, f"?entity={grid}").get("placement") for n in processes}
    bench.record("views-grid", grid_placed)
    bench.check(f"LocateEntity: a grid spawned on {host} is Local there and Absent elsewhere",
                (grid_placed.get(host) or {}).get("residence") == "Local"
                and all((grid_placed.get(n) or {}).get("residence") == "Absent" for n in processes if n != host),
                json.dumps(grid_placed)[:500])
    bench.exercised(host=host, body=body, partition=hosted.get("partition"), grid=grid, api=bool(api))


def suite_sdk_04(bench) -> None:
    """T-0256: PluginSdk grid handover hooks and per-grid plugin state across a node-1 -> node-2 handover.

        Two grids share one node-1 partition; the plugin gives node-1's Leaving a state for the first only.
        An ABORTED capture (direct /capture then /thaw on node-1) must call Leaving then Stayed and leave both
        grids Local on node-1. The committed clientless handover must call Leaving + Left on node-1 and
        Arrived on node-2 with the first grid's state and NO state for the second - the CONTROL that the
        state is per grid, not a constant. Every hook runs on the game thread; the WA sees none.
    """
    import urllib.request
    from scenarios_core import _handover_url, _nodes, _wait_line, _owners

    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])
    for node_id in ("node-1", "node-2", "wa-1"):
        reg = bench.cluster.node(node_id, "/plugin-handover")
        bench.check(f"{node_id} registers the grid handover hooks", reg.get("registered") is True, json.dumps(reg)[:300])
    # Far from the world's content, and 27 blocks: over BlockCountThreshold (20), so the trash sweep keeps them.
    x, y, z = 640000.0, 0.0, 0.0
    first = bench.cluster.node("node-1", f"/trash-test-spawn?x={x}&y={y}&z={z}&blocks=27", method="POST").get("grid")
    second = bench.cluster.node("node-1", f"/trash-test-spawn?x={x + 25}&y={y}&z={z}&blocks=27", method="POST").get("grid")
    deadline, placed = time.time() + 120, {}
    while time.time() < deadline:
        placed = {g: bench.cluster.node("node-1", f"/plugin-views?entity={g}").get("placement") or {} for g in (first, second)}
        if all(p.get("residence") == "Local" for p in placed.values()) and placed[first].get("partition") == placed[second].get("partition"):
            break
        time.sleep(1)
    bench.record("handover-placed", placed)
    partition = int((placed.get(first) or {}).get("partition") or 0)
    bench.check("both grids are Local on node-1 in one partition", partition != 0
                and (placed.get(second) or {}).get("partition") == str(partition), json.dumps(placed))
    token = "state-" + uuid.uuid4().hex[:12]
    bench.cluster.node("node-1", f"/plugin-handover?grid={first}&state={token}")

    def events(node_id: str) -> list[dict]:
        return [e for e in bench.cluster.node(node_id, "/plugin-handover").get("events") or [] if e.get("grid") in (first, second)]

    generation = next(row["Generation"] for row in _owners(bench) if row["Partition"] == partition and row["Node"] == "node-1")
    control = _nodes(bench)["node-1"].control
    aborted = str(uuid.uuid4())
    query = f"partition={partition}&transferId={aborted}&generation={generation}"
    with urllib.request.urlopen(f"http://{control}/cluster/capture?clientless=true&{query}", timeout=120) as response:
        captured = len(response.read())
    bench.cluster.node("node-1", f"/thaw?{query}", method="POST", timeout=60)
    after_abort = events("node-1")
    bench.record("handover-abort-events", after_abort)
    bench.check("ABORT: node-1 saw Leaving then Stayed for both grids, never Left",
                sorted((e["hook"], e["grid"]) for e in after_abort) == sorted(
                    [("Leaving", first), ("Leaving", second), ("Stayed", first), ("Stayed", second)]),
                json.dumps(after_abort)[:500])
    bench.check("ABORT: Leaving carried the first grid's state", any(e["hook"] == "Leaving" and e["grid"] == first
                and e.get("state") == token for e in after_abort), json.dumps(after_abort)[:500])
    still = bench.cluster.node("node-1", f"/plugin-views?entity={first}").get("placement") or {}
    bench.check("ABORT: the grid is still Local on node-1", still.get("residence") == "Local", json.dumps(still))

    bench.record("clientless-handover", bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))
    _wait_line(bench, "gateway", rf"partition={partition} state=committed")
    _wait_line(bench, "gateway", rf"partition={partition} state=released")
    deadline = time.time() + 20
    while time.time() < deadline and len(events("node-1")) < len(after_abort) + 4:
        time.sleep(1)
    source = events("node-1")[len(after_abort):]
    target = events("node-2")
    bench.record("handover-source-events", source)
    bench.record("handover-target-events", target)
    bench.check("COMMIT: node-1 saw Leaving + Left for both grids, no Stayed",
                sorted((e["hook"], e["grid"]) for e in source) == sorted(
                    [("Leaving", first), ("Leaving", second), ("Left", first), ("Left", second)]), json.dumps(source)[:500])
    arrived = {e["grid"]: e.get("state") for e in target if e["hook"] == "Arrived"}
    bench.check("COMMIT: node-2 saw Arrived for both grids and nothing else",
                sorted(e["hook"] for e in target) == ["Arrived", "Arrived"] and set(arrived) == {first, second}, json.dumps(target)[:500])
    bench.check("COMMIT: the first grid arrived with its state", arrived.get(first) == token, json.dumps(arrived))
    bench.check("CONTROL: the second grid arrived with no state", second in arrived and arrived[second] is None, json.dumps(arrived))
    every = after_abort + source + target
    bench.check("every hook ran on the game thread", every and all(e.get("gameThread") is True for e in every), json.dumps(every)[:500])
    wa = events("wa-1")
    bench.check("the WA saw no hook", wa == [], json.dumps(wa)[:300])
    moved = bench.cluster.node("node-2", f"/plugin-views?entity={first}").get("placement") or {}
    bench.check("the grid is Local on node-2 now", moved.get("residence") == "Local", json.dumps(moved))
    bench.exercised(partition=partition, grids=[first, second], captureBytes=captured, token=token)


def suite_sdk_05(bench) -> None:
    """T-0256: PluginSdk cluster clock - node-1, node-2 and the WA read one clock and one game time.

        Each read pairs the SDK clock with the host's uptime Stopwatch in the same game-thread call, so
        (clock - uptime) is time-invariant and compares across processes read one after another. CONTROL:
        that offset is far from zero - the processes agree on the WA's clock, not merely on the shared host
        Stopwatch. Only the WA is authoritative; all are synchronized; the clock advances with real time.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])
    processes = ["node-1", "node-2", "wa-1"]
    deadline, first = time.time() + 60, {}
    while time.time() < deadline:
        first = {n: bench.cluster.node(n, "/plugin-clock") for n in processes}
        if all(r.get("synchronized") for r in first.values()):
            break
        time.sleep(2)
    bench.record("clock-first", first)
    bench.check("every process has the clock and is synchronized", all(r.get("available") and r.get("synchronized")
                for r in first.values()), json.dumps(first)[:500])
    bench.check("only the WA is authoritative", {n: r.get("authoritative") for n, r in first.items()}
                == {"node-1": False, "node-2": False, "wa-1": True}, json.dumps(first)[:500])
    clock = {n: r["clockMs"] - r["uptimeMs"] for n, r in first.items() if r.get("clockMs") is not None}
    game = {n: r["gameTimeMs"] - r["uptimeMs"] for n, r in first.items() if r.get("gameTimeMs") is not None}
    bench.record("clock-offsets", {"clock": clock, "game": game})
    bench.check("CONTROL: the clock is not the host uptime (|clock - uptime| > 60 s on the WA)",
                abs(clock.get("wa-1", 0)) > 60000, json.dumps(clock))
    bench.check("the three clocks agree within 250 ms", len(clock) == 3 and max(clock.values()) - min(clock.values()) < 250,
                json.dumps(clock))
    bench.check("the three game times agree within 250 ms", len(game) == 3 and max(game.values()) - min(game.values()) < 250,
                json.dumps(game))
    time.sleep(5)
    later = bench.cluster.node("node-2", "/plugin-clock")
    advance = later["clockMs"] - first["node-2"]["clockMs"]
    real = later["uptimeMs"] - first["node-2"]["uptimeMs"]
    bench.check("the clock advances with real time (within 5 %)", abs(advance - real) < 0.05 * real,
                f"clock +{advance:.0f} ms over {real:.0f} ms")
    bench.exercised(offsets={"clock": clock, "game": game})


def suite_sdk_06(bench) -> None:
    """T-0256: PluginSdk cluster events - partition acquired/lost across a handover, node down/up across a restart.

        CONTROL: after subscribing and the seeding poll, a quiet cluster raises NO node events (the seed is
        silent) - so the later NodeDown/NodeUp are the restart, not the subscription.
    """
    from scenarios_core import _handover_url, _wait_line

    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])

    def events(node_id: str) -> dict:
        return bench.cluster.node(node_id, "/plugin-events", timeout=60)

    first = {n: events(n) for n in ("node-1", "node-2")}
    bench.record("events-subscribed", first)
    roster = sorted((r["node"], r["role"]) for r in first["node-1"].get("nodes") or [])
    bench.check("NodesAsync lists node-1, node-2 and the WA with roles", roster ==
                [("node-1", "Regular"), ("node-2", "Regular"), ("wa-1", "WorldAuthority")], json.dumps(roster))
    time.sleep(15)
    quiet = events("node-1")["events"]
    bench.check("CONTROL: a quiet cluster raises no node events after the silent seed",
                not [e for e in quiet if e["kind"] in ("NodeUp", "NodeDown")], json.dumps(quiet)[:400])
    grid = bench.cluster.node("node-1", "/trash-test-spawn?x=660000&y=0&z=0&blocks=27", method="POST").get("grid")
    partition = wait_until(lambda: int((bench.cluster.node("node-1", f"/plugin-views?entity={grid}").get("placement") or {})
                                       .get("partition") or 0) or None, 120, "the probe grid to get a node-1 partition")
    acquired = wait_until(lambda: next((e for e in events("node-1")["events"] if e["kind"] == "PartitionAcquired"
                                        and e["partition"] == str(partition)), None), 30, "PartitionAcquired on node-1")
    bench.check("node-1 raised PartitionAcquired for the new partition", acquired["node"] == "node-1", json.dumps(acquired))
    bench.record("clientless-handover", bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))
    _wait_line(bench, "gateway", rf"partition={partition} state=released")
    lost = wait_until(lambda: next((e for e in events("node-1")["events"] if e["kind"] == "PartitionLost"
                                    and e["partition"] == str(partition)), None), 30, "PartitionLost on node-1")
    gained = wait_until(lambda: next((e for e in events("node-2")["events"] if e["kind"] == "PartitionAcquired"
                                      and e["partition"] == str(partition)), None), 30, "PartitionAcquired on node-2")
    bench.check("handover: PartitionLost on node-1, PartitionAcquired on node-2 at a later generation",
                gained["generation"] > acquired["generation"], json.dumps([acquired, lost, gained]))
    before = next(r for r in first["node-1"]["nodes"] if r["node"] == "node-2")["incarnation"]
    bench.cluster.restart_node("node-2")
    down = wait_until(lambda: next((e for e in events("node-1")["events"] if e["kind"] == "NodeDown"
                                    and e["node"] == "node-2"), None), 240, "NodeDown node-2 on node-1")
    up = wait_until(lambda: next((e for e in events("node-1")["events"] if e["kind"] == "NodeUp"
                                  and e["node"] == "node-2"), None), 400, "NodeUp node-2 on node-1")
    bench.check("restart: NodeDown of the old incarnation, NodeUp of a new one",
                down["incarnation"] == before and up["incarnation"] != before, json.dumps([down, up]))
    every = events("node-1")["events"]
    bench.record("events-node-1", every)
    bench.check("every event was raised on the game thread", all(e["gameThread"] is True for e in every), json.dumps(every)[:400])
    bench.exercised(partition=partition, grid=grid, incarnations=[before, up["incarnation"]])


def suite_sdk_10(bench) -> None:
    """SE-903: a plugin's offline blocker keeps a partition online with no player near, and letting go lets it go.

        Static grids in partitions of their own, far from everything, go offline at once (grace 0). Each has a job:
        KEPT is blocked through a handover and a node restart, RELEASED is blocked and then answered null, THROWS
        has a blocker that throws, CONTROL is never blocked on a node with a blocker, and NEVER sits on a node whose
        plugin never registered one: those two say the sweep works and that a plugin that doesn't call this changes
        nothing. The cluster keeps nothing: after a restart LOST, blocked before it, goes offline because the plugin
        didn't say so again, and KEPT stays because it did. A move asks the blocker of the node holding the grid.
    """
    from scenarios_core import _handover_url, _log, _wait_line
    from scenarios_saves import _newest

    regular = ["node-1", "node-2"]
    bench.start_cluster(nodes=regular + ["wa-1"])
    _plugin_services_ready(bench, regular + ["wa-1"])
    off = {"offline.enabled": "false"}
    on = {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}
    bench.record("config-off", bench.cluster.handover_config(off))

    def spawn(node: str, offset: int) -> int:
        return bench.cluster.node(node, f"/trash-test-spawn?x={640000 + offset}&y=0&z=0&blocks=27&static=true", method="POST").get("grid")

    def placement(node: str, grid: int) -> dict:
        return bench.cluster.node(node, f"/plugin-views?entity={grid}").get("placement") or {}

    def where(grid: int) -> str | None:
        return next((node for node in regular if placement(node, grid).get("residence") == "Local"), None)

    def offline(grid: int) -> bool:
        table = bench.cluster.get("/registry/partitions/offline")
        return any(grid in (row.get("Members") or []) for row in table.get("Partitions", []))

    def blocker(node: str, **query) -> dict:
        return bench.cluster.node(node, "/plugin-offline?" + "&".join(f"{k}={v}" for k, v in query.items()))

    def reasons_on(node: str) -> dict:
        return (bench.cluster.node(node, "/offline").get("owner") or {}).get("blockers") or {}

    def went(grid: int, timeout: float = 120) -> bool:
        try:
            return bool(wait_until(lambda: offline(grid) or None, timeout, f"grid {grid} offline"))
        except ClusterError:
            return False

    def holds(grid: int, seconds: float = 15) -> bool:
        """Three sweeps, 5 s apart, with the grid never offline."""
        deadline = time.time() + seconds
        while time.time() < deadline:
            if offline(grid):
                return False
            time.sleep(1)
        return True

    grids = {"KEPT": spawn("node-1", 0), "RELEASED": spawn("node-1", 40000), "THROWS": spawn("node-1", 80000),
             "CONTROL": spawn("node-1", 120000), "NEVER": spawn("node-2", 160000)}
    bench.record("grids", grids)
    wait_until(lambda: all(where(grid) for grid in grids.values()) or None, 120, "the grids placed")
    first = blocker("node-1", register=1)
    bench.record("register-node-1", first)
    bench.check("node-1 registers a blocker", first.get("registered") is True, json.dumps(first))
    bench.check("a second blocker of the plugin is refused while the first is live", first.get("second") == "refused", json.dumps(first))
    wa = blocker("wa-1", register=1)
    bench.check("the WA registers one too", wa.get("registered") is True, json.dumps(wa))
    for key in ("KEPT", "RELEASED"):
        blocker("node-1", block=grids[key])
    blocker("node-1", **{"throw": grids["THROWS"]})
    bench.record("config-on", bench.cluster.handover_config(on))

    bench.check("CONTROL: a grid the blocker answers null about goes offline", went(grids["CONTROL"]))
    bench.check("NEVER: a grid on a node whose plugin never registered a blocker goes offline", went(grids["NEVER"]))
    for key in ("KEPT", "RELEASED", "THROWS"):
        bench.check(f"{key} stays online with no player near", holds(grids[key], 15 if key == "KEPT" else 5), str(grids[key]))
    blockers = reasons_on("node-1")
    bench.record("offline-blockers", blockers)
    reasons = {key.split("/", 1)[1] for key in blockers if "/" in key}
    bench.check("/offline names the plugin's reason and the throw", {"rig", "error"} <= reasons, json.dumps(blockers))
    bench.check("the throw is logged once", _log(bench, "node-1").read_text(errors="replace").count("threw, keeping partitions online") == 1)
    seen = blocker("node-1")
    bench.check("the blocker ran on the game thread", seen.get("asked", 0) > 0 and seen.get("offThread") is False, json.dumps(seen))

    # Letting go: null from the blocker, then the throw gone.
    blocker("node-1", unblock=grids["RELEASED"])
    bench.check("RELEASED: answered null, it goes offline", went(grids["RELEASED"]))
    blocker("node-1", nothrow=grids["THROWS"])
    bench.check("THROWS: no longer throwing, it goes offline", went(grids["THROWS"]))

    # A move: the new node's blocker is the one asked, and it must know the job.
    second = blocker("node-2", register=1, block=grids["KEPT"])
    bench.check("node-2 registers a blocker", second.get("registered") is True, json.dumps(second))
    partition = int(placement("node-1", grids["KEPT"]).get("partition") or 0)
    if not partition:
        bench.check("KEPT is still online to move", False, str(where(grids["KEPT"])))
        return
    bench.record("handover", bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))
    _wait_line(bench, "gateway", rf"partition={partition} state=released")
    bench.check("MOVE: KEPT is on node-2", where(grids["KEPT"]) == "node-2", str(where(grids["KEPT"])))
    bench.check("MOVE: node-2's blocker keeps it online", holds(grids["KEPT"]))
    moved = reasons_on("node-2")
    bench.check("MOVE: node-2 says why", any(key.endswith("/rig") for key in moved), json.dumps(moved))
    asked = blocker("wa-1")
    bench.check("WA: its blocker is never asked", asked.get("asked") == 0, json.dumps(asked))

    # A restart: the registration and the plugin's memory are gone. LOST is not said again, KEPT is.
    bench.record("config-off-restart", bench.cluster.handover_config(off))
    grids["LOST"] = spawn("node-2", 200000)
    wait_until(lambda: where(grids["LOST"]) or None, 120, "LOST placed")
    blocker("node-2", block=grids["LOST"])
    for key in ("KEPT", "LOST"):
        id_ = int(placement("node-2", grids[key]).get("partition") or 0)
        before = (_newest(bench, id_) or {}).get("SaveId")
        bench.cluster.node("node-2", f"/save-capture?partition={id_}", "POST")
        wait_until(lambda: (_newest(bench, id_) or {}).get("SaveId") != before or None, 180, f"a fresh save of {key}")
    bench.cluster.restart_node("node-2")

    def reloaded():
        try:
            bench.cluster.post("/registry/leases/expire")
            return blocker("node-2") and all(where(grids[key]) for key in ("KEPT", "LOST")) or None
        except (ClusterError, OSError):  # not up yet
            return None

    wait_until(reloaded, 420, "node-2 back, and KEPT and LOST loaded again")
    holders = {key: where(grids[key]) for key in ("KEPT", "LOST")}
    bench.record("after-restart", holders)
    restarted = blocker("node-2")
    bench.check("RESTART: node-2's plugin has no blocker until it registers again", restarted.get("registered") is False, json.dumps(restarted))
    blocker(holders["KEPT"], register=1, block=grids["KEPT"])
    bench.record("config-on-restart", bench.cluster.handover_config(on))
    bench.check("RESTART: LOST, not said again, goes offline", went(grids["LOST"], 180))
    bench.check("RESTART: KEPT, said again, stays online", holds(grids["KEPT"]))

    # Dispose lets it go too.
    blocker(holders["KEPT"], unregister=1)
    bench.check("DISPOSE: KEPT goes offline", went(grids["KEPT"], 180))
    bench.exercised(grids=grids, partition=partition, holders=holders)


def suite_sdk_11(bench) -> None:
    """SE-904: a cluster stop keeps a partition a plugin's blocker holds out of the offline table, so the start loads it.

        Static grids in partitions of their own, far from everything and with nobody connected. KEPT has a reason at
        the stop, CONTROL on the same node and NEVER on a node with no blocker have none. After the stop and the start
        KEPT is online, in the same lineage and with no demand on it, the other two are offline and stay there, and
        the stop took no longer than one without a reason. Letting go after the start still lets KEPT go offline.
    """
    from scenarios_saves import _newest

    regular = ["node-1", "node-2"]
    processes = regular + ["wa-1"]
    bench.start_cluster(nodes=processes)
    _plugin_services_ready(bench, processes)
    bench.record("config-off", bench.cluster.handover_config({"offline.enabled": "false"}))

    def spawn(node: str, offset: int) -> int:
        return bench.cluster.node(node, f"/trash-test-spawn?x={640000 + offset}&y=0&z=0&blocks=27&static=true", method="POST").get("grid")

    def placement(node: str, grid: int) -> dict:
        return bench.cluster.node(node, f"/plugin-views?entity={grid}").get("placement") or {}

    def where(grid: int) -> str | None:
        for node in regular:
            try:
                if placement(node, grid).get("residence") == "Local":
                    return node
            except ClusterError:
                pass  # not up yet
        return None

    def offline(grid: int) -> bool:
        table = bench.cluster.get("/registry/partitions/offline")
        return any(grid in (row.get("Members") or []) for row in table.get("Partitions", []))

    def blocker(node: str, **query) -> dict:
        return bench.cluster.node(node, "/plugin-offline?" + "&".join(f"{k}={v}" for k, v in query.items()))

    def saved(grid: int, node: str) -> int:
        partition = int(placement(node, grid).get("partition") or 0)
        before = (_newest(bench, partition) or {}).get("SaveId")
        bench.cluster.node(node, f"/save-capture?partition={partition}", "POST")
        wait_until(lambda: (_newest(bench, partition) or {}).get("SaveId") != before or None, 180, f"a fresh save of {grid}")
        return partition

    grids = {"KEPT": spawn("node-1", 0), "CONTROL": spawn("node-1", 40000), "NEVER": spawn("node-2", 80000)}
    bench.record("grids", grids)
    wait_until(lambda: all(where(grid) for grid in grids.values()) or None, 120, "the grids placed")
    bench.check("the grids are where the test put them",
                where(grids["KEPT"]) == where(grids["CONTROL"]) == "node-1" and where(grids["NEVER"]) == "node-2",
                json.dumps({key: where(grid) for key, grid in grids.items()}))
    partition = saved(grids["KEPT"], "node-1")
    for key, node in (("CONTROL", "node-1"), ("NEVER", "node-2")):
        saved(grids[key], node)
    first = blocker("node-1", register=1, block=grids["KEPT"])
    bench.check("node-1 has a blocker with a reason for KEPT", first.get("registered") is True, json.dumps(first))
    bench.record("config-on", bench.cluster.handover_config({"offline.enabled": "true"}))

    started = time.time()
    graceful = bench.cluster.stop(grace=240)
    seconds = time.time() - started
    bench.record("stop", {"graceful": graceful, "seconds": round(seconds, 1)})
    bench.check("the stop is graceful, every process left on its own", graceful, f"{seconds:.0f} s")
    bench.check("the stop is as fast as one without a reason: under a minute", seconds < 60, f"{seconds:.0f} s")

    bench.cluster.start(processes)
    bench.cluster.wait_ready(nodes=processes)
    _plugin_services_ready(bench, processes)
    try:
        back = wait_until(lambda: where(grids["KEPT"]), 180, "KEPT loaded")
    except ClusterError:
        back = None
    bench.check("KEPT: the start loads it, with nobody connected", back, f"offline: {offline(grids['KEPT'])}")
    bench.check("KEPT: not in the offline table", not offline(grids["KEPT"]))
    for key in ("CONTROL", "NEVER"):
        bench.check(f"{key}: no reason, it stays offline after the start", offline(grids[key]) and where(grids[key]) is None,
                    f"online on {where(grids[key])}")
    if not back:
        return
    bench.check("KEPT: the same partition", int(placement(back, grids["KEPT"]).get("partition") or 0) == partition)

    # The start's ordinary rules from here on: the plugin says it again, or lets it go.
    bench.record("config-on-after", bench.cluster.handover_config(
        {"offline.enabled": "true", "offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}))
    try:
        wait_until(lambda: offline(grids["KEPT"]) or None, 180, "KEPT offline")
        gone = True
    except ClusterError:
        gone = False
    bench.check("KEPT: no reason after the start, it goes offline", gone)
    bench.exercised(grids=grids, partition=partition, stop_seconds=round(seconds, 1))


def suite_sdk_07(bench) -> None:
    """T-0256: PluginSdk idempotent global command - one operation id from two nodes runs ONCE, on the WA.

        The command is registered on wa-1 and ALSO on node-1, whose handler must never run (commands execute on
        the World Authority). CONTROL: a NEW operation id runs again (the counter moves), so the replayed
        answer is the ledger, not a constant.
    """
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    _plugin_services_ready(bench, ["node-1", "node-2", "wa-1"])

    def glob(node_id: str, query: str) -> dict:
        reply = bench.cluster.node(node_id, "/plugin-global" + query, timeout=60)
        bench.record(f"global-{node_id}-{len(query)}-{time.time():.0f}", reply)
        return reply

    for node_id in ("wa-1", "node-1"):
        reg = glob(node_id, "?register=1")
        bench.check(f"{node_id} registers the global command", reg.get("registered") is True, json.dumps(reg))
    a, b = str(uuid.uuid4()), str(uuid.uuid4())
    first = glob("node-1", f"?run=alpha&op={a}")["result"] or {}
    bench.check("node-1: the command ran on the WA", first.get("code") == "Success" and first.get("payload") == "wa-1:1:alpha",
                json.dumps(first))
    replay = glob("node-2", f"?run=alpha&op={a}")["result"] or {}
    bench.check("node-2 with the same operation id gets the first outcome (not run again)",
                replay.get("code") == "Success" and replay.get("payload") == "wa-1:1:alpha", json.dumps(replay))
    reused = glob("node-2", f"?run=beta&op={a}")["result"] or {}
    bench.check("the same operation id with another payload is a Conflict", reused.get("code") == "Conflict", json.dumps(reused))
    fresh = glob("node-1", f"?run=gamma&op={b}")["result"] or {}
    bench.check("CONTROL: a new operation id runs again", fresh.get("code") == "Success" and fresh.get("payload") == "wa-1:2:gamma",
                json.dumps(fresh))
    wa, n1 = glob("wa-1", ""), glob("node-1", "")
    bench.check("the WA handler ran exactly twice; node-1's never", wa.get("runs") == 2 and n1.get("runs") == 0,
                json.dumps({"wa-1": wa.get("runs"), "node-1": n1.get("runs")}))
    bench.exercised(operations=[a, b])


def suite_sdk_09(bench) -> None:
    """SE-865: the reserved global command cluster.global-save answers only once a recoverable global save holds what
    the World Authority did before it. The ledger of the test's own command shows what a save holds: a replacement WA
    answers an operation id from its restored ledger without running the handler, or runs it again.

    Saves held before registration: no answer, from a node or from the WA itself, while plain commands answer at once.
    The WA killed then: the command is gone. After an answer: killed again, the command is still there. A retry with
    the same operation id ends the wait when the save lands. Saves the Registry refuses: no answer, ever."""
    bench.start_cluster(nodes=["node-1", "wa-1"])
    cluster = bench.cluster
    report: dict = {}

    def ready() -> None:
        cluster.wait_ready(nodes=["wa-1"])
        _plugin_services_ready(bench, ["node-1", "wa-1"])
        cluster.node("wa-1", "/plugin-global?register=1", timeout=60)

    def run(op: str, text: str = "alpha") -> dict:
        return wait_until(
            lambda: (lambda reply: reply if (reply["result"] or {}).get("code") == "Success" else None)(
                cluster.node("node-1", f"/plugin-global?run={text}&op={op}", timeout=60)
            ),
            60,
            f"global command {op}",
        )

    def save(node: str, op: str, ms: int) -> str:
        """The answer to one request of up to ms."""
        started = time.monotonic()
        cluster.node(node, f"/plugin-global-save?op={op}&ms={ms}")
        code = wait_until(lambda: cluster.node(node, f"/plugin-global-save?op={op}")["code"], ms / 1000 + 30, f"the answer to {op}")
        report.setdefault("answers", []).append({"node": node, "op": op, "code": code, "seconds": round(time.monotonic() - started, 2)})
        return code

    def saved(node: str, op: str) -> None:
        """Until the answer, whatever the plugin bus says while the WA starts."""
        wait_until(lambda: save(node, op, 25000) == "Success" or None, 180, "a save")

    def runs() -> int:
        return cluster.node("wa-1", "/plugin-global")["runs"]

    def newest() -> tuple:
        row = cluster.get("/registry/global-saves/recovery?without=wa-1")["SaveId"]
        return row["Generation"], row["Sequence"]

    def hold() -> None:
        cluster.node("wa-1", "/global-save-register-hold?ms=600000", "POST")

    ready()
    wait_until(lambda: cluster.get("/registry/global-saves") or None, 180, "the first global save")
    for node in ("node-1", "wa-1"):
        saved(node, str(uuid.uuid4()))
        bench.check(f"{node}: answered with saves running", report["answers"][-1]["seconds"] < 25, json.dumps(report["answers"][-1]))

    # A held save: no answer, and nothing else waits.
    hold()
    command = str(uuid.uuid4())
    started = time.monotonic()
    first = run(command)
    report["commandSeconds"] = time.monotonic() - started
    bench.check("a plain command answers at once while saves are held", report["commandSeconds"] < 5, str(report["commandSeconds"]))
    bench.check("the command ran", runs() == 1, str(runs()))
    before = None
    for node in ("node-1", "wa-1"):
        answer = save(node, str(uuid.uuid4()), 8000)
        bench.check(f"{node}: no answer while the save is held", answer == "Timeout", answer)
        # The save past the seam when the hold began has been copied by the end of the first wait.
        before = before or newest()
    held = cluster.node("wa-1", "/global-save-register-hold")
    report["held"] = held
    bench.check("the WA holds the answers", held["channel"]["heldResponses"] >= 2, json.dumps(held)[:400])
    bench.check("no newer save is recoverable", newest() == before, str(newest()))

    # Killed then: the command is not in a save, as no answer said it was.
    waiting = str(uuid.uuid4())
    answer = save("node-1", waiting, 2000)
    bench.check("no answer right before the kill", answer == "Timeout", answer)
    cluster.restart_node("wa-1")
    ready()
    again = run(command)
    report["afterKill"] = again
    bench.check("after a kill without an answer the command ran again", runs() == 1, str(again))

    # An answer, then a kill: it stays.
    saved("node-1", waiting)
    held = cluster.node("wa-1", "/global-save-register-hold")
    bench.check("the answer came from the replacement's save", held["channel"]["heldResponses"] == 0, json.dumps(held)[:400])
    cluster.restart_node("wa-1")
    ready()
    replayed = run(command)
    report["afterSecondKill"] = replayed
    bench.check(
        "after a kill past the answer the save holds the command",
        runs() == 0 and replayed["result"]["payload"] == again["result"]["payload"],
        json.dumps(replayed),
    )

    # Slower than the caller's timeout: the same operation id keeps its place.
    wait_until(lambda: newest()[0] > before[0] or None, 120, "a save of the replacement")
    before = newest()
    hold()
    slow = str(uuid.uuid4())
    answer = save("node-1", slow, 2000)
    bench.check("a retry: no answer yet", answer == "Timeout", answer)
    cluster.node("wa-1", "/global-save-register-hold?ms=1", "POST")
    saved("node-1", slow)
    bench.check("the retry is answered once the save lands", newest() > before, f"{newest()} after {before}")

    # Refused saves: a catalogue row ahead of the writer, as in P2-WAR-03. It names no file, so the scenario ends here.
    authority = next(row for row in cluster.get("/registry/nodes") if row["Node"] == "wa-1")
    cluster.post(
        f"/registry/global-saves?node=wa-1&epoch={authority['Epoch']}&generation={newest()[0]}&sequence=1000000"
        "&kind=Periodic&size=1&checksum=forced&clusterTime=0&location=forced-ahead"
    )
    refused = "Global save registration was fenced"
    wait_until(lambda: _lines(bench, "wa-1", refused) or None, 90, "the refusal in the World Authority's log")
    denied = str(uuid.uuid4())
    for attempt in range(3):
        answer = save("wa-1", denied, 10000)
        bench.check(f"no answer while saves are refused ({attempt})", answer == "Timeout", answer)
    started = time.monotonic()
    run(str(uuid.uuid4()), "beta")
    bench.check("a plain command still answers", time.monotonic() - started < 5, str(time.monotonic() - started))
    bench.record("global-save-command", report)
    bench.exercised(route="held -> no answer, killed -> gone; answered, killed -> kept; retry; refused -> no answer")


SCENARIOS_SUITE = {
    "SUITE-TPL-01": (suite_tpl_01, "test utility plugin: test routes installed, product surface intact without it", "1"),
    "SUITE-SDK-01": (suite_sdk_01, "PluginSdk shared directory: one folder seen by two nodes and the WA (T-0256)", "1"),
    "SUITE-SDK-03": (suite_sdk_03, "PluginSdk cluster views: players, positions, entity placement on two nodes and the WA (T-0256)", "1"),
    "SUITE-SDK-04": (suite_sdk_04, "PluginSdk grid handover hooks + per-grid state: abort and commit node-1 -> node-2 (T-0256)", "1"),
    "SUITE-SDK-05": (suite_sdk_05, "PluginSdk cluster clock: one clock and game time on two nodes and the WA (T-0256)", "1"),
    "SUITE-SDK-06": (suite_sdk_06, "PluginSdk cluster events: partition acquired/lost, node down/up (T-0256)", "1"),
    "SUITE-SDK-07": (suite_sdk_07, "PluginSdk idempotent global command: once on the WA from two nodes (T-0256)", "1"),
    "SUITE-SDK-02": (suite_sdk_02, "PluginSdk broadcast: one message to the topic handler on two nodes and the WA (T-0256)", "1"),
    "SUITE-SDK-10": (suite_sdk_10, "PluginSdk offline blocker: a plugin keeps a partition online with no player near, across a move and a restart (SE-903)", "1"),
    "SUITE-SDK-11": (suite_sdk_11, "PluginSdk offline blocker: a cluster stop keeps its partition out of the offline table, so the start loads it (SE-904)", "1"),
    "SUITE-SDK-09": (suite_sdk_09, "plugin global command cluster.global-save: answered only when a recoverable global save holds what came before (SE-865)", "1"),
    "SUITE-SDK-08": (suite_sdk_08, "plugin bus: no Fenced answer from a healthy node, at any point of its update, in a burst, in a long frame (SE-852)", "1"),
}
