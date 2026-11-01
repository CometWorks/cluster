"""T-0320 node control, launcher side: hosts file, selectors, the `nodes list` line, the actualizer,
the start's hosts-file apply and `nodes prune`. Inert: a fake registry on a loopback port, fake
processes, temporary run roots; no Gateway, launcher or game starts."""
import hashlib
import io
import json
import os
import shutil
import tempfile
import threading
import time
import unittest
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch

import cluster
import cluster_hostd

HERE = "polaris"


class FakeRegistry:
    """A loopback HTTP server: routes[(method, path)] -> JSON (or a callable(body, query) -> JSON, or
    (status, JSON)); every request is recorded as (method, raw path, headers, body)."""

    def __init__(self):
        self.routes = {}
        self.requests = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _serve(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                body = json.loads(raw) if raw else None
                path, _, query = self.path.partition("?")
                owner.requests.append((self.command, self.path, dict(self.headers), body))
                answer = owner.routes.get((self.command, path))
                if answer is None:
                    status, answer = 404, {"error": {"code": "not_found", "message": self.path}}
                else:
                    if callable(answer):
                        answer = answer(body, query)
                    status, answer = answer if isinstance(answer, tuple) else (200, answer)
                data = json.dumps(answer).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = do_POST = do_PUT = _serve

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        self.endpoint = f"127.0.0.1:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def sent(self, method, prefix):
        return [(path, headers, body) for m, path, headers, body in self.requests if m == method and path.startswith(prefix)]


def envelope(data):
    return {"protocolVersion": 1, "capturedAt": "2026-10-01T00:00:00Z", "data": data}


def operation(result):
    return envelope({"operationId": "op-1", "kind": "node-control", "state": "Succeeded", "actor": "loopback",
                     "idempotencyKey": "k", "target": result.get("node"), "result": result, "error": None})


def slot(node, desired=0, incumbent=0, allowed=True, host=HERE, role=0):
    """A NodeSlotPlan as GET /registry/node-plan serializes it (PascalCase, enums as numbers)."""
    return {"SlotKey": node, "Host": host, "Role": role, "Desired": desired, "Observed": 0, "ObservedNode": None,
            "IncumbentAction": incumbent, "IncumbentNode": node if incumbent else None, "IncumbentEpoch": 0,
            "SpawnAllowed": allowed, "FailureCount": 0, "FailureAlert": False}


def host_view(serving=(4, 4, 4), spares=(1, 1, 1), ram=None, disk=None, host=HERE):
    return {"host": host, "serving": dict(zip(("target", "file", "actual"), serving)),
            "spares": dict(zip(("target", "file", "actual"), spares)), "ramFloorMb": ram, "diskFloorMb": disk,
            "warnings": []}


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "run"
        self.registry = FakeRegistry()
        self.addCleanup(self.registry.close)
        self.addCleanup(self.temporary.cleanup)
        self.env = patch.dict(os.environ, {"CLUSTER_HOSTS_FILE": "", "CLUSTER_NODE_HOST": ""})
        self.env.start()
        self.addCleanup(self.env.stop)

    def make(self, regular=4, spares=1, **kwargs) -> cluster.Cluster:
        kwargs.setdefault("host", HERE)
        config = cluster.Config(root=self.root, regular_nodes=regular, spare_nodes=spares,
                                gateway_control=self.registry.endpoint, **kwargs)
        return cluster.Cluster(config)


class HostsFileTest(Fixture):
    def test_parse_file_and_request_body(self):
        text = json.dumps({"version": 1, "gateway": HERE, "worldAuthority": HERE,
                           "hosts": {HERE: {"serving": 4, "spares": 1, "ramFloorGb": 16, "diskFloorGb": 50}}})
        self.root.mkdir(parents=True)
        (self.root / "hosts.json").write_text(text)
        spec, source, sha = self.make(regular=2, spares=0).config.hosts_spec()
        self.assertEqual(source, str(self.root / "hosts.json"))
        self.assertEqual(sha, hashlib.sha256(text.encode()).hexdigest())
        self.assertEqual(spec["hosts"][HERE], {"serving": 4, "spares": 1, "ramFloorGb": 16, "diskFloorGb": 50})
        self.assertEqual(cluster.hosts_file_request(spec, sha),
                         {"hosts": {HERE: {"serving": 4, "spares": 1, "ramFloorMb": 16384, "diskFloorMb": 51200}},
                          "sha256": sha})

    def test_synthesized_from_nodes_and_spares_when_absent(self):
        spec, source, sha = self.make(regular=4, spares=1).config.hosts_spec()
        self.assertEqual(spec, {"version": 1, "gateway": HERE, "worldAuthority": HERE,
                                "hosts": {HERE: {"serving": 4, "spares": 1, "ramFloorGb": None, "diskFloorGb": None}}})
        self.assertIn("--nodes 4 --spares 1", source)
        self.assertEqual(len(sha), 64)

    def test_explicit_and_environment_paths(self):
        other = Path(self.temporary.name) / "elsewhere.json"
        other.write_text(json.dumps({"hosts": {HERE: {"serving": 2}}}))
        with patch.dict(os.environ, {"CLUSTER_HOSTS_FILE": str(other)}):
            spec, source, _ = self.make().config.hosts_spec()
        self.assertEqual((source, spec["hosts"][HERE]["serving"], spec["hosts"][HERE]["spares"]), (str(other), 2, 0))
        missing = self.make(hosts_file=Path(self.temporary.name) / "missing.json")
        with self.assertRaisesRegex(cluster.ClusterError, "missing.json"):
            missing.config.hosts_spec()

    def test_validation(self):
        cases = {
            "unknown key 'nodes'": {"nodes": 3, "hosts": {HERE: {"serving": 1}}},
            "polaris: unknown key 'spare'": {"hosts": {HERE: {"serving": 1, "spare": 1}}},
            "gateway must be this host": {"gateway": "ai", "hosts": {HERE: {"serving": 1}}},
            "worldAuthority must be this host": {"worldAuthority": "ai", "hosts": {HERE: {"serving": 1}}},
            "serving must be an integer 0..16": {"hosts": {HERE: {"serving": 17}}},
            "spares must be an integer 0..16, not True": {"hosts": {HERE: {"serving": 1, "spares": True}}},
            "at least one serving node": {"hosts": {HERE: {"serving": 0, "spares": 1}}},
            "not a plain host name": {"hosts": {"polaris#1": {"serving": 1}}},
            "hosts must be a non-empty object": {"hosts": {}},
            "version must be 1 or 2": {"version": 3, "hosts": {HERE: {"serving": 1}}},
            "ramFloorGb must be a non-negative number": {"hosts": {HERE: {"serving": 1, "ramFloorGb": -1}}},
        }
        for message, data in cases.items():
            with self.subTest(message), self.assertRaisesRegex(cluster.ClusterError, message):
                cluster.validate_hosts_file(data, HERE)
        # A second host is allowed (T-0321 readiness): only this host's launcher runs it here.
        two = cluster.validate_hosts_file({"hosts": {HERE: {"serving": 4, "spares": 1}, "ai": {"serving": 2}}}, HERE)
        self.assertEqual(sorted(two["hosts"]), ["ai", HERE])


class SelectorTest(Fixture):
    def test_hash_is_escaped_in_urls_and_bodies_carry_the_selector(self):
        self.assertEqual(cluster.quote_path("polaris#2"), "polaris%232")
        self.assertEqual(cluster.quote_path("12345@polaris"), "12345@polaris")
        instance = self.make()
        result = {"node": "node-3", "replacement": "node-9"}
        self.registry.routes[("POST", "/admin/v1/node-control/recycle")] = operation(result)
        self.registry.routes[("PUT", "/admin/v1/node-control/hosts/odd%23host")] = operation({"revision": "4"})
        self.registry.routes[("GET", "/admin/v1/node-control/history")] = envelope({"records": [], "nextCursor": None})
        self.assertEqual(instance.nodes_stop("recycle", "polaris#2", reason="test")["node"], "node-3")
        instance.nodes_set("odd#host", serving=3)
        instance.nodes_history(host="odd#host")
        (path, headers, body), = self.registry.sent("POST", "/admin/v1/node-control/recycle")
        self.assertEqual(body["selector"], "polaris#2")
        self.assertEqual((body["now"], body["reason"]), (False, "test"))
        self.assertNotIn("timeoutSeconds", body)
        self.assertEqual(uuid.UUID(headers["Idempotency-Key"]).version, 4)
        self.assertTrue(self.registry.sent("PUT", "/admin/v1/node-control/hosts/odd%23host"))
        self.assertTrue(self.registry.sent("GET", "/admin/v1/node-control/history?limit=50&host=odd%23host"))

    def test_bad_selector_is_refused_before_any_request(self):
        instance = self.make()
        for selector in ("node3", "polaris#", "@polaris", "node-3; rm", "wa 1"):
            with self.subTest(selector), self.assertRaisesRegex(cluster.ClusterError, "selector"):
                instance.nodes_stop("remove", selector)
        self.assertEqual(self.registry.requests, [])


class FormatTest(unittest.TestCase):
    def test_exact_list_lines(self):
        now = datetime(2026, 10, 1, 12, 0, 0, tzinfo=timezone.utc)
        view = {
            "revision": 7, "differsFromFile": True,
            "hosts": [host_view(serving=(5, 4, 5), spares=(1, 1, 1))],
            "nodes": [
                {"node": "node-4", "host": HERE, "position": None, "pid": 39921, "ageSeconds": 5 * 3600 + 60,
                 "state": "Draining", "partitions": 3, "clients": 0, "startedAt": "2026-10-01T06:59:00Z",
                 "stop": {"kind": "remove", "actor": "loopback", "requestedBy": "viktor", "why": "..",
                          "deadline": (now + timedelta(minutes=3, seconds=40)).isoformat(), "forced": False}},
                {"node": "node-11", "host": HERE, "position": 5, "pid": 60110, "ageSeconds": 4 * 60 + 12,
                 "state": "Spare", "partitions": 0, "clients": 0},
                {"node": "node-3", "host": HERE, "position": 1, "pid": 41233, "ageSeconds": 3 * 3600 + 12 * 60 + 5,
                 "state": "Serving", "partitions": 12, "clients": 3},
            ],
            "notCounted": [{"node": "wa-1", "role": "WorldAuthority", "host": HERE, "pid": 38810, "ageSeconds": 5 * 3600 + 20 * 60},
                           {"node": "gateway", "role": "Gateway", "host": HERE, "pid": 38790}],
        }
        self.assertEqual(cluster.format_node_control(view, now.timestamp()), [
            "polaris  serving 5/5 (file 4)  spares 1/1 (file 1)  * runtime differs from hosts file",
            "  #1 node-3  @polaris pid 41233 age 3h12m Serving  12 partitions 3 clients",
            "  #5 node-11 @polaris pid 60110 age 4m    Spare     0 partitions",
            '     node-4  @polaris pid 39921 age 5h01m Draining  3 partitions 0 clients  (remove by viktor: "..", force in 3m40s)',
            "  not counted: wa-1 @polaris pid 38810 age 5h20m World Authority; gateway @polaris pid 38790",
        ])

    def test_a_host_missing_from_the_file_shows_a_dash(self):
        view = host_view()
        view["serving"]["file"] = view["spares"]["file"] = None
        self.assertEqual(cluster.format_host_line(view),
                         "polaris  serving 4/4 (file -)  spares 1/1 (file -)  * runtime differs from hosts file")

    def test_unknown_pid_seconds_and_no_difference_marker(self):
        self.assertEqual(cluster.format_host_line(host_view()), "polaris  serving 4/4 (file 4)  spares 1/1 (file 1)")
        self.assertEqual(cluster.format_node_line({"node": "node-12", "host": "ai", "position": 2, "pid": None,
                                                   "ageSeconds": 9, "state": "Starting", "partitions": 0, "clients": 0}),
                         "  #2 node-12 @ai pid ? age 9s    Starting  0 partitions")


class ActualizerTest(Fixture):
    """actualize_step against a fake plan: processes are a set, spawns and kills are recorded."""

    def setUp(self):
        super().setUp()
        self.running: set[str] = set()
        self.started: list[str] = []
        self.killed: list[str] = []
        self.plan = []
        self.registered = []
        self.heartbeats = []
        self.registry.routes[("GET", "/registry/node-plan")] = lambda body, query: self.plan
        self.registry.routes[("GET", "/registry/status")] = lambda body, query: {
            "nodes": [{"Node": node, "State": 2} for node in self.registered]}
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view()], "nodes": []})

        def heartbeat(body, query):
            self.heartbeats.append(body)
            return {"Executor": body["executor"]}
        self.registry.routes[("POST", "/registry/executors/heartbeat")] = heartbeat
        self.instance = self.make()
        self.addCleanup(self.instance.release_launcher_lock)
        self.instance.config.save_node_set()
        for name, fake in {
            "_node_pids": lambda node_id: [4000 + len(node_id)] if node_id in self.running else [],
            "_node_pid": lambda node_id: 4242 if node_id in self.running else None,
            "start_node": lambda node_id, allow_update=False: (self.started.append(node_id), self.running.add(node_id)),
            "_kill_pids": lambda slot=None: (self.killed.append(slot), self.running.discard(slot), [1])[2],
            "await_port_free": lambda spec, deadline=30.0: True,
            "propagate_sources": lambda warm, nodes=None: [],
            "_memory_available_mb": lambda: 64 * 1024,
            "_disk_free_mb": lambda: 500 * 1024,
        }.items():
            patcher = patch.object(self.instance, name, fake)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = patch.object(cluster, "port_open", lambda host, port, timeout=0.3: False)
        patcher.start()
        self.addCleanup(patcher.stop)

    def observations(self):
        return [o for beat in self.heartbeats for o in beat["observations"]]

    def ledger(self):
        data = json.loads((self.root / "node-set.json").read_text())
        return {entry["id"]: (entry["slot"], bool(entry.get("retired"))) for entry in data["nodes"]}

    def test_spawns_wanted_slots_without_a_process(self):
        self.running.add("node-1")
        self.plan = [slot("node-1"), slot("node-7"), slot("node-8", allowed=False), slot("node-9", host="ai")]
        summary = self.instance.actualize_step()
        self.assertEqual(self.started, ["node-7"])
        self.assertEqual(summary["spawned"], ["node-7"])
        states = {o["slotKey"]: o["state"] for o in self.observations()}
        self.assertEqual(states, {"node-1": "Ready", "node-7": "Spawning"})
        self.assertEqual(self.heartbeats[0]["executor"], "launcher@polaris")
        ready = next(o for o in self.observations() if o["slotKey"] == "node-1")
        self.assertEqual((ready["pid"], ready["host"], ready["node"]), (4242, HERE, "node-1"))
        self.assertIn("node-7", self.ledger())
        self.instance.actualize_step()
        self.assertEqual(self.started, ["node-7"], "a running node is not started twice")

    def test_stops_kill_slots_after_the_self_exit_grace(self):
        self.running |= {"node-1", "node-5"}
        self.plan = [slot("node-1"), slot("node-5", desired=2, incumbent=2)]
        with patch.object(cluster, "SELF_EXIT_SECONDS", 3600):
            self.instance.actualize_step()
        self.assertEqual(self.killed, [], "the node gets its self-exit grace first")
        with patch.object(cluster, "SELF_EXIT_SECONDS", 0):
            summary = self.instance.actualize_step()
        self.assertEqual((self.killed, summary["stopped"]), (["node-5"], ["node-5"]))
        gone = [o for o in self.observations() if o["state"] == "Gone"]
        self.assertEqual([o["slotKey"] for o in gone], ["node-5"])
        self.assertEqual(self.ledger()["node-5"][1], True, "the slot is free again")
        self.assertNotIn("node-5", self.started)

    def test_a_kill_slot_whose_node_exited_by_itself_is_reported_gone_without_a_kill(self):
        self.plan = [slot("node-5", desired=2, incumbent=2)]
        self.instance.actualize_step()
        self.assertEqual(self.killed, [])
        self.assertEqual([(o["slotKey"], o["state"]) for o in self.observations()], [("node-5", "Gone")])

    def test_never_restarts_a_dead_id(self):
        self.registered = ["node-3"]
        self.plan = [slot("node-3")]
        self.instance.actualize_step()
        self.instance.actualize_step()
        self.assertEqual(self.started, [])
        self.assertEqual({o["state"] for o in self.observations() if o["slotKey"] == "node-3"}, {"Gone"})

    def test_a_node_that_died_before_registering_is_reported_failed_then_retried(self):
        self.plan = [slot("node-7")]
        self.instance.actualize_step()
        self.running.discard("node-7")          # exits before it registers
        self.plan = [slot("node-7", allowed=False)]
        self.instance.actualize_step()
        self.instance.actualize_step()
        failed = [o for o in self.observations() if o["state"] == "Failed"]
        self.assertEqual(len(failed), 1, "one failure per attempt")
        self.assertEqual(self.started, ["node-7"], "no retry while the registry backs off")
        self.plan = [slot("node-7")]
        self.instance.actualize_step()
        self.assertEqual(self.started, ["node-7", "node-7"], "an id that never registered is not used up")

    def test_reuses_free_slots(self):
        config = self.instance.config
        wa = next(node for node in config.nodes if node.role == "WA")
        for spec in [node for node in config.nodes if node.role == "regular"]:
            config.nodes.remove(spec)
            config.retired_nodes.append(spec)
        config.save_node_set()
        self.plan = [slot("node-6"), slot("node-7")]
        self.instance.actualize_step()
        self.assertEqual({k: v for k, v in self.ledger().items() if not v[1]},
                         {"wa-1": (config.slot_of(wa), False), "node-6": (1, False), "node-7": (2, False)})
        # node-6 stops; the next new id takes its slot, never its id.
        self.plan = [slot("node-6", desired=2, incumbent=2), slot("node-7")]
        self.running.discard("node-6")
        self.instance.actualize_step()
        self.plan = [slot("node-7"), slot("node-8")]
        self.instance.actualize_step()
        live = {k: v[0] for k, v in self.ledger().items() if not v[1]}
        self.assertEqual((live["node-8"], live["node-7"]), (1, 2))
        self.assertNotIn("node-6", live)

    def test_refuses_a_launch_below_the_host_floor(self):
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope(
            {"hosts": [host_view(ram=16 * 1024, disk=50 * 1024)], "nodes": []})
        self.plan = [slot("node-7")]
        with patch.object(self.instance, "_memory_available_mb", lambda: 8 * 1024):
            summary = self.instance.actualize_step()
        self.assertEqual((self.started, summary["refused"]), ([], ["node-7"]))
        failed, = [o for o in self.observations() if o["slotKey"] == "node-7"]
        self.assertEqual(failed["state"], "Failed")
        self.assertTrue(failed["failure"].startswith("host_floor: MemAvailable 8192 MB < RAM floor 16384 MB"), failed)
        with patch.object(self.instance, "_disk_free_mb", lambda: 10 * 1024):
            self.instance.actualize_step()
        self.assertIn("disk floor", self.observations()[-1]["failure"])
        self.assertEqual(self.started, [])
        self.instance.actualize_step()           # back above both floors
        self.assertEqual(self.started, ["node-7"])

    def test_spawn_false_starts_nothing_but_still_stops(self):
        self.running.add("node-5")
        self.plan = [slot("node-7"), slot("node-5", desired=2, incumbent=2)]
        with patch.object(cluster, "SELF_EXIT_SECONDS", 0):
            self.instance.actualize_step(spawn=False)
        self.assertEqual((self.started, self.killed), ([], ["node-5"]))

    def test_one_launcher_per_host(self):
        self.plan = []
        self.instance.actualize_step()
        other = cluster.Cluster(cluster.Config(root=self.root, host=HERE, gateway_control=self.registry.endpoint))
        with self.assertRaisesRegex(cluster.ClusterError, "another launcher holds"):
            other.actualize_step()
        self.assertFalse(other.try_launcher_lock())
        self.instance.release_launcher_lock()
        self.assertTrue(other.try_launcher_lock())
        other.release_launcher_lock()


class StartTest(Fixture):
    def setUp(self):
        super().setUp()
        self.registry.routes[("POST", "/admin/v1/node-control/hosts-file")] = operation({"revision": "1"})
        # The runtime targets differ from the file (a `nodes set` earlier in the run): the start must not care.
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope(
            {"differsFromFile": True, "hosts": [host_view(serving=(6, 4, 6), spares=(2, 1, 2))], "nodes": []})

    def start(self, instance):
        calls = []
        with patch.object(cluster, "RELAY", "internal"), patch.object(instance, "require_plugin_sdk"), \
                patch.object(instance, "require_clean_run_root"), patch.object(instance, "_gateway_env", return_value={}), \
                patch.object(instance, "_spawn", lambda name, argv, env, cwd=None: calls.append(("spawn", name))), \
                patch.object(instance, "_warm_up", lambda warm: calls.append(("warm", warm.node_id))), \
                patch.object(instance, "start_node", lambda node_id, allow_update=False: calls.append(("start", node_id))), \
                patch.object(instance, "actualize_step", lambda spawn=True: calls.append(("actualize",)) or
                             {"spawned": [], "refused": [], "stopped": [], "observations": []}):
            instance.start()
        instance.release_launcher_lock()
        return calls

    def test_start_posts_the_hosts_file_not_the_runtime_counts(self):
        self.root.mkdir(parents=True)
        text = json.dumps({"hosts": {HERE: {"serving": 4, "spares": 1, "ramFloorGb": 16}}})
        (self.root / "hosts.json").write_text(text)
        instance = self.make(regular=4, spares=1)
        before = [node.node_id for node in instance.config.nodes if node.role == "regular"]
        calls = self.start(instance)
        (path, headers, body), = self.registry.sent("POST", "/admin/v1/node-control/hosts-file")
        self.assertEqual(body, {"hosts": {HERE: {"serving": 4, "spares": 1, "ramFloorMb": 16384, "diskFloorMb": None}},
                                "sha256": hashlib.sha256(text.encode()).hexdigest()})
        self.assertIn("Idempotency-Key", headers)
        self.assertEqual(self.registry.sent("PUT", "/admin/v1/node-control/hosts/"), [])
        self.assertEqual(self.registry.sent("POST", "/registry/nodes/spare"), [], "no positional spare marking")
        self.assertEqual(self.registry.sent("POST", "/registry/scale"), [])
        self.assertEqual(calls, [("spawn", "gateway"), ("warm", "wa-1"), ("actualize",)])
        # Fresh ids: every regular of the previous run left the ledger; their slots are free.
        ledger = json.loads((self.root / "node-set.json").read_text())["nodes"]
        self.assertEqual(sorted(e["id"] for e in ledger if e.get("retired")), sorted(before))
        self.assertEqual([e["id"] for e in ledger if not e.get("retired")], ["wa-1"])

    def test_registry_without_node_control_falls_back_to_the_positional_start(self):
        del self.registry.routes[("POST", "/admin/v1/node-control/hosts-file")]
        self.registry.routes[("POST", "/registry/nodes/spare")] = {}
        instance = self.make(regular=2, spares=1)
        calls = self.start(instance)
        self.assertEqual(len(self.registry.sent("POST", "/registry/nodes/spare")), 3)
        self.assertIn(("start", "node-1"), calls)
        self.assertNotIn(("actualize",), calls)

    def test_node_control_off_keeps_the_positional_start(self):
        self.registry.routes[("POST", "/registry/nodes/spare")] = {}
        instance = self.make(regular=2, spares=1, node_control=False)
        calls = self.start(instance)
        self.assertEqual(self.registry.sent("POST", "/admin/v1/node-control/hosts-file"), [])
        self.assertEqual(len(self.registry.sent("POST", "/registry/nodes/spare")), 3)
        self.assertIn(("start", "node-1"), calls)
        self.assertNotIn(("actualize",), calls)
        with patch.dict(os.environ, {"CLUSTER_NODE_CONTROL": "0"}):
            self.assertFalse(cluster.Config(root=self.root).node_control)
        self.assertTrue(cluster.Config(root=self.root).node_control)

    def test_an_explicit_node_selection_keeps_the_positional_start(self):
        instance = self.make(regular=2, spares=0)
        calls = []
        with patch.object(cluster, "RELAY", "internal"), patch.object(instance, "require_plugin_sdk"), \
                patch.object(instance, "require_clean_run_root"), patch.object(instance, "_gateway_env", return_value={}), \
                patch.object(instance, "_spawn", lambda *a, **k: None), \
                patch.object(instance, "start_node", lambda node_id, allow_update=False: calls.append(node_id)):
            instance.start(nodes=["node-1"])
        self.assertEqual(calls, ["node-1"])
        self.assertEqual(self.registry.sent("POST", "/admin/v1/node-control/hosts-file"), [])


class PruneTest(Fixture):
    def test_prune_keeps_every_referenced_dir(self):
        instance = self.make()
        for name in ("node-1", "node-2", "node-3", "node-4", "node-5", "node-6", "node-7"):
            (self.root / f"{name}-data").mkdir(parents=True)
            (self.root / f"{name}-magnetar").mkdir()
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope(
            {"hosts": [host_view()], "nodes": [{"node": "node-1", "host": HERE, "state": "Serving"}],
             "notCounted": [{"node": "wa-1", "host": HERE}]})
        pages = {"": {"records": [{"node": "node-2"}, {"node": "node-3"}], "nextCursor": "2"},
                 "2": {"records": [{"node": "node-4"}, {"node": "node-6"}, {"node": "node-7"}], "nextCursor": None}}
        self.registry.routes[("GET", "/admin/v1/node-control/history")] = lambda body, query: envelope(
            pages[dict(p.split("=") for p in query.split("&")).get("cursor", "")])
        self.registry.routes[("GET", "/registry/saves")] = [
            {"SaveId": {"Partition": 7}, "Locations": [str(self.root / "node-2-data/cluster/saves/P-7/7.sbs")],
             "LocationHolders": {}}]
        self.registry.routes[("GET", "/registry/global-saves")] = [
            {"Locations": ["/elsewhere/GLOBAL/1.sbs"], "LocationHolders": {"/elsewhere/GLOBAL/1.sbs": "node-3"}}]
        self.registry.routes[("GET", "/registry/voxel-bases")] = [
            {"StorageName": "a", "Location": str(self.root / "node-6-data/voxels/a.vx")}]
        # T-0323: node-7 is named by nothing /registry/saves lists (an ARCHIVED save names it), so its row stays.
        self.registry.routes[("GET", "/registry/nodes")] = [{"Node": "node-1"}, {"Node": "node-7"}, {"Node": "wa-1"}]
        # Not in host-store mode and never was: a released row's leftover save files go with its dir, as before.
        self.registry.routes[("GET", "/registry/status")] = {"hostStore": {"enabled": False, "aliases": 0}}
        (self.root / "node-4-data/cluster/saves/P-9").mkdir(parents=True)
        (self.root / "node-4-data/cluster/saves/P-9/g00000001-s00000001.mps").write_bytes(b"leftover")
        with patch.object(instance, "_node_pids", lambda node_id: []):
            dry = instance.nodes_prune()
            self.assertEqual(dry["wouldDelete"], ["node-4-data"])
            self.assertEqual(dry["kept"], {"node-1-data": "live", "node-2-data": "catalogued save location",
                                           "node-3-data": "save holder", "node-5-data": "not in history",
                                           "node-6-data": "catalogued save location",
                                           "node-7-data": "registry row (save holder)"})
            self.assertTrue((self.root / "node-4-data").is_dir(), "a dry run deletes nothing")
            applied = instance.nodes_prune(apply=True)
        self.assertEqual(applied["deleted"], ["node-4-data"])
        self.assertFalse((self.root / "node-4-data").exists())
        self.assertFalse((self.root / "node-4-magnetar").exists())
        for kept in ("node-1", "node-2", "node-3", "node-5", "node-6", "node-7"):
            self.assertTrue((self.root / f"{kept}-data").is_dir(), kept)

    def test_host_store_mode_keeps_a_released_dir_that_still_has_save_files(self):
        # T-0340 step 6: the row is released without the archive, so an archived save may still name a file in the dir.
        instance = self.make()
        for name in ("node-4", "node-5"):
            (self.root / f"{name}-data/cluster/saves/P-9").mkdir(parents=True)
        named = self.root / "node-4-data/cluster/saves/P-9/g00000001-s00000001.mps"
        named.write_bytes(b"an archived save names this file")
        (self.root / "node-5-data/cluster/saves/P-9/.g00000001-s00000001.mps.node-5.tmp").write_bytes(b"not a save file")
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view()], "nodes": []})
        self.registry.routes[("GET", "/admin/v1/node-control/history")] = envelope(
            {"records": [{"node": "node-4"}, {"node": "node-5"}], "nextCursor": None})
        for route in ("/registry/saves", "/registry/global-saves", "/registry/voxel-bases", "/registry/nodes"):
            self.registry.routes[("GET", route)] = []
        reason = "save files (host-store mode: an archived save may name them)"
        for status in ({"enabled": True, "aliases": 3}, {"enabled": False, "aliases": 3}):   # on, or switched off again
            self.registry.routes[("GET", "/registry/status")] = {"hostStore": status}
            with patch.object(instance, "_node_pids", lambda node_id: []):
                applied = instance.nodes_prune(apply=True)
            self.assertEqual(applied["kept"], {"node-4-data": reason}, status)
            self.assertTrue(named.is_file(), status)
            (self.root / "node-5-data/cluster/saves/P-9").mkdir(parents=True, exist_ok=True)
        self.assertFalse((self.root / "node-5-data/cluster/saves/P-9/.g00000001-s00000001.mps.node-5.tmp").exists(),
                         "a dir without save files goes, as before")
        # Falsifier: a registry that never was in host-store mode releases a row only when the archive does not name
        # it, so the same dir is pruned (the rule above is what kept it).
        self.registry.routes[("GET", "/registry/status")] = {"hostStore": {"enabled": False, "aliases": 0}}
        with patch.object(instance, "_node_pids", lambda node_id: []):
            self.assertEqual(instance.nodes_prune(apply=True)["deleted"], ["node-4-data", "node-5-data"])
        self.assertFalse(named.exists())

    def test_an_unanswered_catalogue_prunes_nothing(self):
        instance = self.make()
        (self.root / "node-4-data").mkdir(parents=True)
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [], "nodes": []})
        self.registry.routes[("GET", "/admin/v1/node-control/history")] = envelope(
            {"records": [{"node": "node-4"}], "nextCursor": None})
        with self.assertRaises(cluster.ClusterError):
            instance.nodes_prune(apply=True)
        self.assertTrue((self.root / "node-4-data").is_dir())


class CliTest(Fixture):
    def test_list_prints_lines_and_legend(self):
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view()], "nodes": []})
        out = io.StringIO()
        args = type("Args", (), {"verb": "list", "json": False})()
        self.assertEqual(cluster.nodes_command(self.make(), args, out), 0)
        text = out.getvalue()
        self.assertTrue(text.startswith("polaris  serving 4/4 (file 4)  spares 1/1 (file 1)\n"))
        for state in cluster.OPERATOR_STATES:
            self.assertIn(state, text)
        for internal in ("Active", "Closed", "Empty"):
            self.assertNotIn(internal, text)


TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <SessionSettings>
    <ExperimentalMode>{experimental}</ExperimentalMode>
    <EnableCopyPaste>true</EnableCopyPaste>
  </SessionSettings>
  <ServerName>Regression</ServerName>
  <DedicatedId>3302002543995903037</DedicatedId>
</MyConfigDedicated>
"""


class NodeConfigSeedTest(Fixture):
    """Every spawn - at start or at runtime, under a fresh id - gets the deployment's node cfg template,
    and a cfg that fails the deployment's requirements never launches."""

    def setUp(self):
        super().setUp()
        self.template = Path(self.temporary.name) / "dedicated-node.cfg"
        self.template.write_text(TEMPLATE.format(experimental="true"))
        self.instance = self.make(dedicated_template=self.template, dedicated_require={"ExperimentalMode": "true"})
        self.addCleanup(self.instance.release_launcher_lock)
        self.spawned = []
        for name, fake in {"require_plugin_sdk": lambda: None, "refresh_premade_world": lambda node: None,
                           "prepare_magnetar_config": lambda node: self.root,
                           "_node_env": lambda node, standalone=False: {},
                           "_spawn": lambda name, argv, env, cwd=None: self.spawned.append(name)}.items():
            patcher = patch.object(self.instance, name, fake)
            patcher.start()
            self.addCleanup(patcher.stop)

    def cfg(self, node_id):
        return (self.root / f"{node_id}-data/SpaceEngineers-Dedicated.cfg").read_text()

    def test_the_spawn_environment_names_the_hosts_file_host(self):
        # The registry matches a registering node to its hosts-file host by CLUSTER_NODE_HOST; the machine
        # name it falls back to may not be the key (the launcher's host is the key it actualizes).
        seen = []
        instance = self.make(host="polaris-lan")
        with patch.object(instance, "require_plugin_sdk", lambda: None), \
                patch.object(instance, "refresh_premade_world", lambda node: None), \
                patch.object(instance, "prepare_magnetar_config", lambda node: self.root), \
                patch.object(instance, "_spawn", lambda name, argv, env, cwd=None: seen.append(env)), \
                patch.dict(os.environ, {"CLUSTER_NODE_HOST": ""}):
            instance._new_regular_node("node-30")
            instance.start_node("node-30")
        self.assertEqual(seen[0]["CLUSTER_NODE_HOST"], "polaris-lan")
        self.assertEqual(seen[0]["CLUSTER_NODE_ID"], "node-30")

    def test_the_host_store_switch_is_the_gateways_and_a_node_needs_only_its_host(self):
        # T-0340: the registry alone switches the per-host save store on - CLUSTER_HOST_STORE=1 in the GATEWAY's
        # environment, inherited from the launcher's - and tells each node in its registration answer. What a node
        # needs for the store path is its host (CLUSTER_NODE_HOST, the hosts-file key the registry computes the same
        # path with) and a data dir whose parent is the run root; both are there with or without the switch.
        instance = self.make(host="polaris-lan")
        with patch.object(type(instance.config), "replication_table_hash", lambda config: "T"), \
                patch.dict(os.environ, {"CLUSTER_HOST_STORE": "1"}):
            self.assertEqual(instance._gateway_env().get("CLUSTER_HOST_STORE"), "1")
        seen = []
        with patch.object(instance, "require_plugin_sdk", lambda: None), \
                patch.object(instance, "refresh_premade_world", lambda node: None), \
                patch.object(instance, "prepare_magnetar_config", lambda node: self.root), \
                patch.object(instance, "_spawn", lambda name, argv, env, cwd=None: seen.append(env)), \
                patch.dict(os.environ, {"CLUSTER_NODE_HOST": ""}):
            os.environ.pop("CLUSTER_HOST_STORE", None)
            instance._new_regular_node("node-31")
            instance.start_node("node-31")
        self.assertNotIn("CLUSTER_HOST_STORE", seen[0])
        self.assertEqual(seen[0]["CLUSTER_NODE_HOST"], "polaris-lan")
        self.assertEqual(Path(seen[0]["CLUSTER_DATA_ROOT"]).parent, self.root)

    def test_a_new_node_gets_the_template_with_its_own_dedicated_id(self):
        spec = self.instance._new_regular_node("node-17")
        self.instance.start_node("node-17")
        self.assertEqual(self.spawned, ["node-17"])
        expected = TEMPLATE.format(experimental="true").replace(
            "3302002543995903037", f"330200254399590{3000 + spec.epoch}")
        self.assertEqual(self.cfg("node-17"), expected)

    def test_a_differing_cfg_is_rewritten_from_the_template(self):
        config = self.instance.config
        config.dedicated_template, config.dedicated_require = None, {}
        self.instance.start_node("node-1")        # the built-in minimal cfg, no ExperimentalMode
        self.assertNotIn("ExperimentalMode", self.cfg("node-1"))
        config.dedicated_require = {"ExperimentalMode": "true"}
        with self.assertRaisesRegex(cluster.ClusterError, "ExperimentalMode=None"):
            self.instance.start_node("node-1")    # the minimal cfg is refused under the requirement
        config.dedicated_template = self.template
        self.instance.start_node("node-1")
        self.assertIn("<ExperimentalMode>true</ExperimentalMode>", self.cfg("node-1"))

    def test_the_built_in_cfg_lists_admins_as_unsigned_long_items(self):
        # Vanilla reads Administrators as [XmlArrayItem("unsignedLong")] and skips any other item (#1212).
        config = self.instance.config
        config.dedicated_template, config.dedicated_require = None, {}
        config.administrators = [76561199500000001, 76561199500000002]
        self.instance.start_node("node-1")
        admins = ET.fromstring(self.cfg("node-1")).find("Administrators")
        self.assertEqual([("unsignedLong", "76561199500000001"), ("unsignedLong", "76561199500000002")],
                         [(item.tag, item.text) for item in admins])

    def test_a_cfg_failing_the_requirements_never_launches(self):
        self.template.write_text(TEMPLATE.format(experimental="false"))
        with self.assertRaisesRegex(cluster.ClusterError, "ExperimentalMode='false' \\(need 'true'\\)"):
            self.instance.start_node("node-1")
        self.template.unlink()
        with self.assertRaisesRegex(cluster.ClusterError, "node cfg template"):
            self.instance.start_node("node-2")
        self.assertEqual(self.spawned, [])

    def test_runtime_spawn_is_seeded_and_a_refused_one_is_reported(self):
        self.registry.routes[("GET", "/registry/node-plan")] = [slot("node-21")]
        self.registry.routes[("GET", "/registry/status")] = {"nodes": []}
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view()], "nodes": []})
        beats = []
        self.registry.routes[("POST", "/registry/executors/heartbeat")] = lambda body, query: beats.append(body) or {}
        with patch.object(self.instance, "_node_pids", lambda node_id: []), \
                patch.object(self.instance, "propagate_sources", lambda warm, nodes=None: []), \
                patch.object(cluster, "port_open", lambda host, port, timeout=0.3: False):
            self.instance.actualize_step()
            self.assertEqual(self.spawned, ["node-21"])
            self.assertIn("<ExperimentalMode>true</ExperimentalMode>", self.cfg("node-21"))
            self.template.write_text(TEMPLATE.format(experimental="false"))
            self.registry.routes[("GET", "/registry/node-plan")] = [slot("node-22")]
            summary = self.instance.actualize_step()
        self.assertEqual((self.spawned, summary["refused"]), (["node-21"], ["node-22"]))
        failed, = [o for beat in beats for o in beat["observations"] if o["slotKey"] == "node-22"]
        self.assertEqual(failed["state"], "Failed")
        self.assertIn("launch refused", failed["failure"])


class ClusterStatusTest(unittest.TestCase):
    """cluster-status heads each node with the `nodes list` line when the snapshot carries node control,
    and keeps its old header without it (an older registry answers 404)."""

    def render(self, diag):
        import subprocess
        import sys
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "diag.json"
            path.write_text(json.dumps(diag))
            return subprocess.run([sys.executable, str(Path(__file__).parent / "cluster-status"), "--from", str(path)],
                                  capture_output=True, text=True, check=True, timeout=60,
                                  env={**os.environ, "CLUSTER_NODE_LIFETIME": "3600"}).stdout

    def test_node_control_header_and_legend(self):
        captured = "2026-10-01T12:00:00+00:00"
        node = {"node": "node-3", "host": HERE, "position": 1, "pid": 41233, "ageSeconds": 11525, "state": "Serving",
                "partitions": 12, "clients": 3}
        diag = {"capturedAt": captured,
                "nodes": [{"nodeId": "node-3", "role": "Regular", "state": "Active", "section": {}}],
                "registry": {"status": {"nodes": [{"Node": "node-3", "Role": 0, "State": 0,
                                                   "Registered": "2026-10-01T08:48:00+00:00"}]}, "partitions": []}}
        old = self.render(diag)
        self.assertIn("=== node-3  [Regular/Active]  sim", old)
        self.assertNotIn("states:", old)
        diag["nodeControl"] = {"hosts": [host_view()], "nodes": [node], "notCounted": []}
        text = self.render(diag)
        line = cluster.format_node_line(node, datetime.fromisoformat(captured).timestamp())
        self.assertIn(f"\n=== {line.strip()}  |  sim", text)
        self.assertIn("\n" + line + "\n", text)
        self.assertIn("  states: " + cluster.STATE_LEGEND, text)
        self.assertNotIn("[Regular/Active]", text)

    def test_replica_alarm(self):
        """The registry's replica capacity alarm (T-0323) is a warning block with its numbers; inactive, nothing."""
        diag = {"capturedAt": "2026-10-01T12:00:00+00:00", "nodes": [],
                "registry": {"status": {"nodes": [], "replicaAlarm": {
                    "active": False, "depth": 3, "copiesPerMinute": 170.0, "etaMinutes": 0.02, "intervalMinutes": 27.0,
                    "reason": None, "since": None}}, "partitions": []}}
        self.assertNotIn("REPLICA CAPACITY", self.render(diag))
        diag["registry"]["status"]["replicaAlarm"].update(
            active=True, depth=33000, copiesPerMinute=250.4, etaMinutes=131.8,
            reason="the queue outlasts a recycle interval", since="2026-10-01T11:20:00+00:00")
        text = self.render(diag)
        self.assertIn("--- REPLICA CAPACITY BELOW INVALIDATION RATE (for 40m", text)
        self.assertIn("!! the queue outlasts a recycle interval: 33000 save(s) queued, 250 copies/min, 132 min to clear, "
                      "recycle interval 27 min\n", text)
        diag["registry"]["status"]["replicaAlarm"].update(copiesPerMinute=0.0, etaMinutes=None)
        self.assertIn("0 copies/min, no end in sight, recycle interval", self.render(diag))

    def test_stopped_rows_get_no_node_section(self):
        """A stopped node keeps its registry row (Empty, a save holder) and diag lists it; under node control it is in
        the history, not a node, so it gets no section (bench run 3 printed `node-1 [Regular] (not in node control)`
        with the partitions of the node that took over its port slot). A Failed node is still live: it keeps its own."""
        captured = "2026-10-01T12:00:00+00:00"
        live = {"node": "node-5", "host": HERE, "position": 1, "pid": 41233, "ageSeconds": 60, "state": "Serving",
                "partitions": 1, "clients": 0}
        failed = {"node": "node-4", "host": HERE, "position": 2, "pid": 41200, "ageSeconds": 90, "state": "Failed",
                  "partitions": 0, "clients": 0}
        diag = {"capturedAt": captured,
                "nodes": [{"nodeId": "node-5", "role": "Regular", "state": "Active", "section": {"partitions": [{"partitionId": 7}]}},
                          {"nodeId": "node-1", "role": "Regular", "state": "Empty", "stale": True, "section": None,
                           "sectionError": "node is Empty; not queried"},
                          {"nodeId": "node-4", "role": "Regular", "state": "Dead", "stale": True, "section": None}],
                "registry": {"status": {"nodes": []}, "partitions": []},
                "nodeControl": {"hosts": [host_view()], "nodes": [live, failed], "notCounted": []}}
        text = self.render(diag)
        self.assertNotIn("=== node-1", text)
        self.assertIn("1 stopped node row(s) not shown (cluster nodes history): node-1", text)
        self.assertIn("=== " + cluster.format_node_line(failed, datetime.fromisoformat(captured).timestamp()).strip(), text)
        del diag["nodeControl"]
        self.assertIn("=== node-1  [Regular/Empty]", self.render(diag))   # an older registry: every row as before

    def test_registry_invariant_violations_are_listed(self):
        """T-0296: the registry's invariant audit verdicts get their own section; none -> no section."""
        captured = "2026-10-01T12:00:00+00:00"
        base = {"capturedAt": captured, "nodes": [], "registry": {"status": {"nodes": []}, "partitions": []}}
        self.assertNotIn("INVARIANT VIOLATIONS", self.render(base))
        base["registry"]["invariants"] = {"total": 3, "active": [
            {"kind": "duplicate", "partition": 7, "since": "2026-10-01T11:59:00+00:00",
             "detail": "reported with members by node-1, node-2; registry owner node-1 epoch=4 generation=9"}]}
        text = self.render(base)
        self.assertIn("--- INVARIANT VIOLATIONS (1 active, 3 since the gateway start) ---", text)
        self.assertIn("!! duplicate P7", text)
        self.assertIn("registry owner node-1 epoch=4 generation=9", text)

    def test_every_host_gets_a_load_line_and_a_paths_line(self):
        """T-0321 P6: one block per host from the node-control placement: node count, effective sim-load, sim speed,
        and the calls its nodes made per channel from the diag `transport` section (served requests left out)."""
        def transport(local, lan, p99=None, served=0):
            return {"channels": {"R2": {"local": {"n": local, "bytes": 1, "p50Ms": 0.1, "p99Ms": 0.2},
                                        "lan": {"n": lan, "bytes": 1, "p50Ms": 1.0, "p99Ms": p99}, "fallback": 0,
                                        "served": {"local": {"n": served}, "lan": {"n": served}}},
                                 "N3": {"local": {"n": 0}, "lan": {"n": 0}, "fallback": 0}}}
        def row(node, sim, load, section_host, channels):
            return {"nodeId": node, "role": "Regular", "state": "Active", "balanceLoadUs": load,
                    "section": {"host": section_host, "health": {"simSpeed": sim}, "transport": channels}}
        captured = "2026-10-01T12:00:00+00:00"
        control = [{"node": node, "host": host, "position": i, "pid": 40000 + i, "ageSeconds": 60, "state": "Serving",
                    "partitions": 1, "clients": 0}
                   for i, (node, host) in enumerate((("node-2", HERE), ("node-3", "ai"), ("node-4", "ai")), 1)]
        diag = {"capturedAt": captured,
                # node-4's own diag names no host yet: the node-control placement decides.
                "nodes": [row("node-2", 1.0, 3000, HERE, transport(5, 0, served=99)),
                          row("node-3", 0.8, 5000, "ai", transport(1, 7, p99=2.0)),
                          row("node-4", 0.6, 1000, None, transport(0, 3, p99=4.0))],
                "registry": {"status": {"nodes": []}, "partitions": []},
                "nodeControl": {"hosts": [host_view(), host_view(host="ai")], "nodes": control, "notCounted": []}}
        text = self.render(diag)
        self.assertIn("--- hosts (2) ---", text)
        self.assertIn(f"\n  {HERE}: 1 node(s), 1 balanceable  |  effective sim-load 3.0ms/s (mean 3.0ms/s)"
                      "  |  sim min 1.00x mean 1.00x\n    paths: R2 local 5 / lan 0\n", text)
        self.assertIn("\n  ai: 2 node(s), 2 balanceable  |  effective sim-load 6.0ms/s (mean 3.0ms/s)"
                      "  |  sim min 0.60x mean 0.70x\n    paths: R2 local 1 / lan 10 (lan p99 <=4ms)\n", text)
        for node in diag["nodes"]:
            del node["section"]["transport"]
        self.assertNotIn("paths:", self.render(diag))    # a build before t0321-core: no paths line, no invented zeros


# ------------------------------------------------------------------------------------------------ T-0340 step 7

def write_mps(path: Path, partition: int, generation: int, sequence: int, body: bytes = b"x" * 64, checksum: int = 0x1234) -> str:
    """A save file with an intact MPS1 envelope (header ids, stored length, trailer); returns its checksum as X16."""
    header = bytearray(100)
    header[0:4] = b"MPS1"
    header[4:6] = (1).to_bytes(2, "little")
    header[6:8] = (100).to_bytes(2, "little")
    header[8:16] = partition.to_bytes(8, "little")
    header[16:20] = generation.to_bytes(4, "little")
    header[20:24] = sequence.to_bytes(4, "little")
    header[76:84] = len(body).to_bytes(8, "little")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(header) + body + (7).to_bytes(8, "little") + checksum.to_bytes(8, "little"))
    return f"{checksum:016X}"


def legacy_path(root: Path, node: str, partition: int, generation: int = 1, sequence: int = 1) -> Path:
    return root / f"{node}-data" / "cluster" / "saves" / f"P-{partition:016X}" / f"g{generation:08X}-s{sequence:08X}.mps"


def snapshot(root: Path) -> dict:
    """Every path under root: inode, size, mtime, link count and content - a dry run must leave all of it as it was."""
    out = {}
    for directory, dirs, names in os.walk(root):
        for name in dirs + names:
            path = os.path.join(directory, name)
            st = os.lstat(path)
            content = Path(path).read_bytes() if os.path.isfile(path) else None
            out[path] = (st.st_ino, st.st_size, st.st_mtime_ns, st.st_nlink, content)
    return out


class FakeCatalogue:
    """The registry's adopt rules (HostStoreMove.cs) over a dict: (global, partition, generation, sequence) -> holders,
    size, checksum. ``on_commit(file)`` runs for every committed file BEFORE the re-key (to look at the disk then)."""

    def __init__(self, host=HERE):
        self.host = host
        self.saves: dict[tuple, dict] = {}
        self.rows: set[str] = set()
        self.live: set[str] = set()
        self.known: dict[str, str] = {}
        self.archived: set[int] = set()
        self.store = True
        self.on_commit = None
        self.rekeyed: list[tuple] = []
        self.calls: list[tuple[bool, int]] = []
        self.busy: set[tuple] = set()      # saves a recovery claim or a grant names (#1276)

    def add(self, partition, holders, checksum, size, generation=1, sequence=1, is_global=False):
        self.saves[(is_global, partition, generation, sequence)] = {"holders": set(holders), "checksum": checksum, "size": size}

    def store_mode(self):
        return self.store

    def nodes(self):
        return set(self.rows), set(self.live)

    def archived_partitions(self):
        return set(self.archived)

    def adopt(self, host, commit, files):
        self.calls.append((commit, len(files)))
        out = []
        for f in files:
            if commit and self.on_commit:
                self.on_commit(f)
            key = (f.is_global, f.partition, f.generation, f.sequence)
            save = self.saves.get(key)
            if f.node in self.live:
                status = "live"
            elif save is None:
                status = "uncatalogued"
            elif f.node not in save["holders"]:
                status = "hostHeld" if f"host:{host}" in save["holders"] else "unnamed"
            elif self.known.get(f.node, host) != host:
                status = "wrongHost"
            elif (f.size, f.checksum) != (save["size"], save["checksum"]):
                status = "mismatch"
            elif key in self.busy:
                status = "busy"
            elif not commit:
                status = "adopt"
            else:
                save["holders"].discard(f.node)
                save["holders"].add(f"host:{host}")
                self.rekeyed.append(key)
                status = "rekeyed"
            out.append({"status": status, "archived": False, **({} if save is None else {"sizeBytes": save["size"], "checksum": save["checksum"]})})
        return out


class StoreSweepTest(unittest.TestCase):
    """cluster-hostd's sweep of stopped node dirs into the host store (T-0340 step 7), against a fake catalogue. Rules of
    the 2026-10-04 08:40 decision: only catalogued files move; unnamed ones stay and go with their dir, only with
    --delete-unnamed, after unnamed_days, and with a verified backup."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "run"
        self.root.mkdir()
        self.backups = Path(self.temporary.name) / "backups"
        self.catalogue = FakeCatalogue()
        self.now = time.time()

    def save(self, node, partition, holders=None, sequence=1, checksum=0x1234, catalogued_checksum=None, body=b"x" * 64):
        path = legacy_path(self.root, node, partition, sequence=sequence)
        written = write_mps(path, partition, 1, sequence, body=body, checksum=checksum)
        if holders is not None:
            self.catalogue.add(partition, holders, catalogued_checksum or written, path.stat().st_size, sequence=sequence)
        return path

    def store(self, partition, sequence=1):
        return self.root / "saves" / HERE / f"P-{partition:016X}" / f"g00000001-s{sequence:08X}.mps"

    def age(self, node, days):
        """Every file of the node's dir (and its log) last written ``days`` ago."""
        old = self.now - days * 86400
        for directory, _, names in os.walk(self.root / f"{node}-data"):
            for name in names:
                os.utime(os.path.join(directory, name), (old, old))
        if (self.root / f"{node}.log").exists():
            os.utime(self.root / f"{node}.log", (old, old))

    def backup(self, name="w-pre", created=None, root=None, host=None, skip=(), partial=False, marker=True):
        """A world-backup part as bin/world-backup commits it: the node dirs copied, BACKUP.json last."""
        target = self.backups / (name + (".partial" if partial else ""))
        for data in self.root.glob("*-data"):
            shutil.copytree(data, target / data.name, ignore=lambda d, names: [n for n in names if os.path.join(d, n) in skip])
        target.mkdir(parents=True, exist_ok=True)
        if marker:
            mps = sum(1 for _ in target.rglob("*.mps"))
            body = {"name": name, "created": datetime.fromtimestamp(created or self.now).isoformat(timespec="seconds"),
                    "root": str(root or self.root), "items": sorted(p.name for p in target.iterdir()), "mps": mps}
            if host:
                body["host"] = host
            (target / "BACKUP.json").write_text(json.dumps(body))
        return target

    def sweep(self, apply, **kwargs):
        kwargs.setdefault("keep_dirs", 0)
        kwargs.setdefault("keep_hours", 0)
        return cluster_hostd.StoreSweep(self.root, HERE, self.catalogue, apply=apply, running=lambda root, node: False,
                                        now=lambda: self.now, log=lambda text: None, **kwargs).run()

    def legacy_world(self):
        """node-3 (stopped, row released): a catalogued save, one nobody catalogues, one with another checksum in the
        catalogue, a superseded copy (the save names another holder), a temporary file. node-4: one catalogued save.
        node-9: live."""
        self.save("node-3", 1, holders={"node-3"})
        self.save("node-3", 2)
        self.save("node-3", 3, holders={"node-3"}, catalogued_checksum="FFFFFFFFFFFFFFFF")
        self.save("node-3", 5, holders={"node-7"})
        (self.root / "node-3-data" / "cluster" / "saves" / "P-0000000000000001" / ".g00000001-s00000002.mps.abc.tmp").write_bytes(b"t")
        self.save("node-4", 4, holders={"node-4"})
        self.save("node-9", 9, holders={"node-9"})
        self.catalogue.live = {"node-9"}
        self.catalogue.rows = {"node-9"}
        self.age("node-3", 10)
        self.age("node-4", 10)

    def test_dry_run_touches_nothing_and_reports_every_class(self):
        self.legacy_world()
        os.utime(legacy_path(self.root, "node-3", 2), (self.now - 30 * 86400,) * 2)
        before = snapshot(self.root)
        result = self.sweep(apply=False)
        self.assertEqual(snapshot(self.root), before, "a dry run writes, links and deletes nothing")
        self.assertFalse((self.root / "saves").exists())
        self.assertTrue(all(not commit for commit, _ in self.catalogue.calls), "a dry run never commits")
        size = legacy_path(self.root, "node-3", 1).stat().st_size
        self.assertEqual(result["classes"], {"rekey": {"files": 2, "bytes": 2 * size}, "uncatalogued": {"files": 1, "bytes": size},
                                             "kept.mismatch": {"files": 1, "bytes": size}, "unnamed": {"files": 1, "bytes": size}})
        self.assertEqual(result["dirs"]["node-3-data"]["classes"]["rekey"], {"files": 1, "bytes": size})
        self.assertEqual((result["unnamed"]["files"], result["unnamed"]["bytes"]), (2, 2 * size))
        self.assertEqual(result["unnamed"]["oldest"], cluster_hostd.stamp(self.now - 30 * 86400))
        self.assertEqual(result["unnamed"]["newest"], cluster_hostd.stamp(self.now - 10 * 86400))
        self.assertEqual(result["dirs"]["node-3-data"]["unnamed"]["oldest"], cluster_hostd.stamp(self.now - 30 * 86400))
        self.assertEqual(result["counts"]["leftoversWouldDelete"]["files"], 1)
        self.assertEqual(result["dirs"]["node-9-data"], {"state": "live"})
        self.assertEqual(result["wouldDelete"], ["node-4-data"], "only a dir every save file would leave")
        self.assertIn("not unnamed", result["keptDirs"]["node-3-data"])
        self.assertTrue(result["dryRun"])

    def test_under_flag_lists_the_dirs_and_bytes_a_delete_unnamed_run_would_remove(self):
        self.save("node-3", 1, holders={"node-7"})          # unnamed only
        self.save("node-3", 2)                              # uncatalogued
        (self.root / "node-3-magnetar").mkdir()
        (self.root / "node-3-magnetar" / "cfg").write_bytes(b"c" * 10)
        self.save("node-4", 4, holders={"node-7"})          # unnamed, but written 2 days ago
        self.age("node-3", 8)
        self.age("node-4", 2)
        before = snapshot(self.root)
        result = self.sweep(apply=False)
        self.assertEqual(snapshot(self.root), before)
        size = legacy_path(self.root, "node-3", 1).stat().st_size
        self.assertEqual(result["underFlag"], {"dirs": {"node-3-data": 2 * size + 10}, "bytes": 2 * size + 10,
                                               "unnamedFiles": 2, "unnamedBytes": 2 * size})
        self.assertEqual(result["wouldDelete"], [], "without the flag nothing with unnamed files goes")
        self.assertIn("--delete-unnamed", result["keptDirs"]["node-3-data"])
        self.assertIn("< 7 d", result["keptDirs"]["node-4-data"])

    def test_only_catalogued_files_move_unnamed_ones_stay_in_their_dir(self):
        self.legacy_world()
        source = legacy_path(self.root, "node-3", 1)
        inode = source.stat().st_ino
        seen = []

        def at_commit(f):
            seen.append((f.partition, f.path.exists(), self.store(f.partition).exists()))
        self.catalogue.on_commit = at_commit
        result = self.sweep(apply=True)
        self.assertEqual(sorted(seen), [(1, True, True), (4, True, True)])
        self.assertEqual(self.store(1).stat().st_ino, inode, "a same-file-system move is a link: no byte copied")
        self.assertFalse(source.exists())
        self.assertEqual(self.catalogue.saves[(False, 1, 1, 1)]["holders"], {f"host:{HERE}"})
        for partition in (2, 5):
            self.assertTrue(legacy_path(self.root, "node-3", partition).exists() and not self.store(partition).exists(),
                            f"partition {partition}: an unnamed or uncatalogued file stays in its dir and out of the store")
        self.assertTrue(legacy_path(self.root, "node-3", 3).exists() and not self.store(3).exists(), "a mismatch stays where it is")
        self.assertTrue(legacy_path(self.root, "node-9", 9).exists(), "a live node's files are not touched")
        self.assertEqual(result["deleted"], ["node-4-data"])
        self.assertTrue((self.root / "node-3-data").exists())
        self.assertEqual(result["counts"]["moved.rekeyed"]["files"], 2)

    def test_a_source_is_deleted_only_after_the_registry_rekey(self):
        self.save("node-3", 1, holders={"node-3"})
        source = legacy_path(self.root, "node-3", 1)

        def registry_down(f):
            raise cluster.ClusterError("registry unreachable")
        self.catalogue.on_commit = registry_down
        with self.assertRaises(cluster.ClusterError):
            self.sweep(apply=True)
        self.assertTrue(source.exists(), "no re-key, no delete")
        self.assertEqual(self.store(1).stat().st_ino, source.stat().st_ino, "the store link is there (harmless: same inode)")
        self.assertEqual(self.catalogue.saves[(False, 1, 1, 1)]["holders"], {"node-3"})

        def changed_meanwhile(f):
            self.catalogue.saves[(False, 1, 1, 1)]["checksum"] = "0"
        self.catalogue.on_commit = changed_meanwhile
        result = self.sweep(apply=True)
        self.assertTrue(source.exists(), "a commit the registry refuses deletes nothing")
        self.assertEqual(result["counts"]["kept.commit.mismatch"]["files"], 1)
        self.assertEqual(result["deleted"], [], "nor the dir")

    def test_a_busy_save_stays_until_a_later_sweep(self):
        # #1276: a recovery claim or a grant names the file; the registry answers busy, at the query or at the commit.
        for partition in (1, 2):
            self.save("node-3", partition, holders={"node-3"})
        self.catalogue.busy.add((False, 1, 1, 1))

        def claimed_meanwhile(f):
            self.catalogue.busy.add((False, f.partition, f.generation, f.sequence))
        self.catalogue.on_commit = claimed_meanwhile
        result = self.sweep(apply=True)
        self.assertTrue(legacy_path(self.root, "node-3", 1).exists() and legacy_path(self.root, "node-3", 2).exists(),
                        "a busy file is not unlinked")
        self.assertEqual(result["classes"]["kept.busy"]["files"], 1)
        self.assertEqual(result["counts"]["kept.commit.busy"]["files"], 1)
        self.assertEqual(result["deleted"], [], "nor its dir")
        self.catalogue.busy.clear()
        self.catalogue.on_commit = None
        result = self.sweep(apply=True)
        self.assertEqual(result["counts"]["moved.rekeyed"]["files"], 2, "the next sweep moves both")
        self.assertFalse(any((self.root / "node-3-data" / "cluster" / "saves").rglob("*.mps")))

    def test_an_interrupted_move_resumes(self):
        for partition in (1, 2):
            self.save("node-3", partition, holders={"node-3"})
        calls = {"n": 0}

        def crash_after_first_rekey(f):
            calls["n"] += 1
            if calls["n"] == 2:
                raise KeyboardInterrupt("hostd killed between two re-keys")
        self.catalogue.on_commit = crash_after_first_rekey
        with self.assertRaises(KeyboardInterrupt):
            self.sweep(apply=True)
        self.assertEqual(self.catalogue.rekeyed, [(False, 1, 1, 1)])
        self.assertTrue(legacy_path(self.root, "node-3", 1).exists() and legacy_path(self.root, "node-3", 2).exists())
        self.catalogue.on_commit = None
        result = self.sweep(apply=True)
        self.assertEqual(self.catalogue.rekeyed, [(False, 1, 1, 1), (False, 2, 1, 1)], "each location is re-keyed exactly once")
        self.assertEqual(result["classes"]["resume"]["files"], 1, "the committed one is recognised by its store inode")
        self.assertEqual(result["counts"]["moved.resumed"]["files"], 1)
        self.assertEqual(result["counts"]["moved.rekeyed"]["files"], 1)
        self.assertFalse(any((self.root / "node-3-data" / "cluster" / "saves").rglob("*.mps")))
        self.assertTrue(self.store(1).exists() and self.store(2).exists())
        self.assertEqual(result["deleted"], ["node-3-data"])
        again = self.sweep(apply=True)
        self.assertEqual((again["counts"], again["classes"], again["deleted"]), ({}, {}, []), "a third sweep has nothing to do")

    def test_a_host_held_save_stored_as_another_file_is_an_unnamed_copy(self):
        source = self.save("node-3", 1, holders={f"host:{HERE}"})
        self.store(1).parent.mkdir(parents=True)
        self.store(1).write_bytes(source.read_bytes())          # a node fetched the same save into the store
        result = self.sweep(apply=True)
        self.assertTrue(source.exists(), "not the store's inode: it stays, an unnamed copy")
        self.assertEqual(result["classes"], {"hostHeldCopy": {"files": 1, "bytes": source.stat().st_size}})

    def test_a_dir_with_a_save_file_that_is_not_unnamed_is_never_deleted(self):
        self.save("node-3", 1, holders={"node-3"}, catalogued_checksum="FFFFFFFFFFFFFFFF")    # mismatch
        self.save("node-4", 4, holders={"node-7"})                                             # unnamed + a stray .mps
        stray = self.root / "node-4-data" / "cluster" / "old-saves" / "x.mps"
        stray.parent.mkdir(parents=True)
        stray.write_bytes(b"not in the layout")
        self.save("node-5", 5, holders={"node-5"})
        self.catalogue.known["node-5"] = "ai"                                                    # wrong host
        conflict = self.save("node-6", 6, holders={"node-6"})
        write_mps(self.store(6), 6, 1, 1, body=b"y" * 64)                                       # the store has another file
        for node in ("node-3", "node-4", "node-5", "node-6"):
            self.age(node, 30)
        backup = self.backup(created=self.now - 60)
        for apply in (False, True):
            result = self.sweep(apply=apply, delete_unnamed=True, backup=backup)
            self.assertEqual(result["deleted" if apply else "wouldDelete"], [], f"apply={apply}: {result['keptDirs']}")
            self.assertEqual(set(result["keptDirs"]), {"node-3-data", "node-4-data", "node-5-data", "node-6-data"})
            self.assertEqual(result["underFlag"]["dirs"], {})
        self.assertTrue(stray.exists() and conflict.exists() and legacy_path(self.root, "node-3", 1).exists()
                        and legacy_path(self.root, "node-5", 5).exists())
        self.assertNotEqual(self.store(6).read_bytes(), conflict.read_bytes(), "a store file is never replaced")
        self.assertEqual(result["classes"]["kept.storeConflict"]["files"], 1)
        self.assertEqual(result["classes"]["kept.wrongHost"]["files"], 1)

    def unnamed_dir(self, days=8):
        self.save("node-3", 1, holders={"node-7"})
        self.save("node-3", 2)
        (self.root / "node-3-magnetar").mkdir()
        self.age("node-3", days)

    def test_without_the_flag_a_dir_with_unnamed_files_is_never_deleted(self):
        self.unnamed_dir(days=400)
        result = self.sweep(apply=True)
        self.assertEqual(result["deleted"], [])
        self.assertTrue(legacy_path(self.root, "node-3", 1).exists() and legacy_path(self.root, "node-3", 2).exists())
        self.assertIn("node-3-data", result["underFlag"]["dirs"])

    def test_with_the_flag_unnamed_files_go_with_their_dir_after_the_retention_days(self):
        self.unnamed_dir(days=6)
        backup = self.backup(created=self.now - 60)
        result = self.sweep(apply=True, delete_unnamed=True, backup=backup)
        self.assertEqual(result["deleted"], [], "6 days < 7")
        self.assertIn("< 7 d", result["keptDirs"]["node-3-data"])
        self.age("node-3", 8)
        result = self.sweep(apply=True, delete_unnamed=True, backup=backup, unnamed_days=9)
        self.assertEqual(result["deleted"], [], "the knob: 8 days < 9")
        result = self.sweep(apply=True, delete_unnamed=True, backup=backup)
        self.assertEqual(result["deleted"], ["node-3-data"])
        self.assertFalse((self.root / "node-3-data").exists() or (self.root / "node-3-magnetar").exists())
        self.assertEqual(result["backupCheck"]["files"], 2)
        self.assertFalse((self.root / "saves").exists(), "nothing went into the store")

    def test_a_deletion_run_refuses_without_a_verified_backup_and_touches_nothing(self):
        self.unnamed_dir()
        self.save("node-4", 4, holders={"node-4"})              # a catalogued file a refused run must not move either
        before = snapshot(self.root)
        with self.assertRaisesRegex(cluster_hostd.BackupRefused, "needs --backup-verified"):
            self.sweep(apply=True, delete_unnamed=True)
        for bad, why in ((self.backup("partial", created=self.now - 60, partial=True), "uncommitted"),
                         (self.backup("nomarker", marker=False), "completion marker"),
                         (self.backup("other-root", created=self.now - 60, root=self.root.parent / "elsewhere"), "of run root"),
                         (self.backup("other-host", created=self.now - 60, host="ai"), "part, not"),
                         (self.backups / "missing", "not a directory")):
            with self.assertRaisesRegex(cluster_hostd.BackupRefused, why):
                self.sweep(apply=True, delete_unnamed=True, backup=bad)
            self.assertEqual(snapshot(self.root), before, f"{why}: nothing touched")
        with self.assertRaisesRegex(cluster_hostd.BackupRefused, "needs --backup-verified"):
            self.sweep(apply=True, retention_days=10)

    def test_a_backup_older_than_a_file_or_missing_one_deletes_no_unnamed_dir(self):
        self.unnamed_dir()
        stale = self.backup("stale", created=self.now - 9 * 86400)
        result = self.sweep(apply=True, delete_unnamed=True, backup=stale)
        self.assertIn("before the newest file to delete", result["backupCheck"]["refused"])
        self.assertEqual(result["deleted"], [])
        self.assertTrue(legacy_path(self.root, "node-3", 1).exists())
        gap = self.backup("gap", created=self.now - 60, skip={str(legacy_path(self.root, "node-3", 2))})
        result = self.sweep(apply=True, delete_unnamed=True, backup=gap)
        self.assertIn("lacks 1 of the 2 files", result["backupCheck"]["refused"])
        self.assertTrue((self.root / "node-3-data").exists())

    def test_a_pending_late_final_journal_keeps_its_file_and_its_dir(self):
        self.save("node-3", 1)                                   # uncatalogued: the Final a local stop wrote
        self.save("node-3", 2, holders={"node-3"})
        journal = self.root / "node-3-data" / "cluster" / "unregistered-finals.jsonl"
        journal.write_text(json.dumps({"Partition": 1, "Generation": 1, "Sequence": 1, "Checksum": "0000000000001234",
                                       "Location": str(legacy_path(self.root, "node-3", 1)), "Writer": "node-3"}) + "\n")
        self.age("node-3", 30)
        result = self.sweep(apply=True, delete_unnamed=True, backup=self.backup(created=self.now - 60))
        self.assertEqual(result["classes"]["lateFinal"]["files"], 1)
        self.assertTrue(legacy_path(self.root, "node-3", 1).exists() and not self.store(1).exists())
        self.assertTrue(self.store(2).exists(), "the catalogued file beside it still moves")
        self.assertEqual(result["deleted"], [])
        self.assertIn("unregistered-finals", result["keptDirs"]["node-3-data"])

    def test_store_leftovers_older_than_a_day_go(self):
        old, young = self.store(1).with_name(".g00000001-s00000001.mps.node-3.abc.tmp"), self.store(1).with_name("g00000001-s00000002.mps.node-4.part")
        old.parent.mkdir(parents=True)
        old.write_bytes(b"t")
        young.write_bytes(b"p")
        os.utime(old, (self.now - 25 * 3600, self.now - 25 * 3600))
        self.assertEqual(self.sweep(apply=False)["counts"]["storeLeftoversWouldDelete"]["files"], 1)
        self.assertTrue(old.exists())
        self.sweep(apply=True)
        self.assertFalse(old.exists())
        self.assertTrue(young.exists(), "a live writer's temporary file or a fetch in progress stays")

    def test_dir_deletion_keeps_rows_the_newest_and_the_young(self):
        for i, node in enumerate(("node-1", "node-2", "node-3", "node-4", "node-5")):
            self.save(node, 10 + i, holders={node})
            (self.root / f"{node}-magnetar").mkdir()
            self.age(node, (100 - i * 20) / 24)      # node-1 oldest (100 h) ... node-5 newest (20 h)
        self.catalogue.rows = {"node-1"}
        result = self.sweep(apply=True, keep_dirs=1, keep_hours=50)
        self.assertEqual(result["deleted"], ["node-2-data", "node-3-data"])
        self.assertEqual(result["keptDirs"], {"node-1-data": "registry row", "node-4-data": "younger than 50 h",
                                              "node-5-data": "newest stopped dirs"})
        self.assertFalse((self.root / "node-2-magnetar").exists())
        self.assertTrue((self.root / "node-4-magnetar").exists())

    def test_apply_refuses_without_store_mode(self):
        self.save("node-3", 1, holders={"node-3"})
        self.catalogue.store = False
        with self.assertRaisesRegex(cluster.ClusterError, "not in host-store mode"):
            self.sweep(apply=True)
        self.assertFalse(self.sweep(apply=False)["storeMode"], "a dry run still reports")

    def test_archive_retention_is_off_by_default_and_counts_from_first_seen(self):
        self.save("node-3", 1, holders={"node-3"})
        self.save("node-3", 2, holders={"node-3"})
        self.catalogue.archived = {1}
        self.sweep(apply=True)
        self.assertTrue(self.store(1).exists(), "off by default: nothing is deleted for retention")
        self.save("node-4", 1, sequence=2, holders={"node-4"})
        record = self.root / "saves" / HERE / ".archive-seen.json"
        dry = self.sweep(apply=False, retention_days=10)
        self.assertFalse(record.exists(), "a dry run does not start the clock")
        self.assertNotIn("retentionWouldDelete", dry["counts"])
        backup = self.backup(created=self.now + 1)
        self.sweep(apply=True, retention_days=10, backup=backup)
        self.assertEqual(json.loads(record.read_text()), {"1": self.now})
        self.assertTrue(self.store(1).exists() and self.store(1, 2).exists(), "seen archived today: kept")
        self.now += 11 * 86400
        self.save("node-5", 1, sequence=3, holders={"node-5"})
        dry = self.sweep(apply=False, retention_days=10)
        self.assertEqual(dry["counts"]["retentionWouldDelete"]["files"], 3)
        result = self.sweep(apply=True, retention_days=10, backup=backup)
        self.assertEqual(result["counts"]["retentionDeleted"]["files"], 3)
        self.assertFalse((self.root / "saves" / HERE / "P-0000000000000001").exists())
        self.assertFalse(legacy_path(self.root, "node-5", 1, sequence=3).exists())
        self.assertTrue(self.store(2).exists(), "a partition that is not archived is kept")
        self.assertIn(f"host:{HERE}", self.catalogue.saves[(False, 1, 1, 1)]["holders"], "the rows are kept")

    def test_disk_report_counts_a_linked_file_once(self):
        self.save("node-3", 1, holders={"node-3"})
        self.save("node-3", 2, holders={"node-3"})
        size = legacy_path(self.root, "node-3", 1).stat().st_size
        self.store(1).parent.mkdir(parents=True)
        os.link(legacy_path(self.root, "node-3", 1), self.store(1))
        report = cluster_hostd.disk_report(self.root, HERE)
        self.assertEqual(report["store"], {"files": 1, "bytes": size, "leftovers": 0})
        self.assertEqual((report["legacy"]["files"], report["legacy"]["bytes"], report["legacy"]["alsoInStore"]), (1, size, 1))
        self.assertEqual(report["legacy"]["dirs"], {"node-3-data": {"files": 1, "bytes": size}})
        self.assertGreater(report["freeMb"], 0)

    def test_offline_dry_run_from_a_registry_db_copy(self):
        """The CD's first run: a COPY of a real run root, no registry running - the answers come from registry.db."""
        import sqlite3
        self.save("node-3", 1)
        self.save("node-3", 2)
        self.save("node-3", 3)
        self.save("node-9", 9)
        db_path = self.root / "gateway-registry" / "registry.db"
        db_path.parent.mkdir()
        db = sqlite3.connect(db_path)
        for table in ("state", "Saves", "GlobalSaves", "NodeHistory"):
            db.execute(f'CREATE TABLE "{table}"(k TEXT NOT NULL PRIMARY KEY, v BLOB)')
        db.execute('CREATE TABLE "ArchivedSaves"(k TEXT NOT NULL PRIMARY KEY, p INTEGER NOT NULL, g INTEGER NOT NULL, l INTEGER NOT NULL, v BLOB)')
        size, checksum = legacy_path(self.root, "node-3", 1).stat().st_size, "0000000000001234"
        legacy = "/elsewhere/run/node-3-data/cluster/saves/P-0000000000000001/g00000001-s00000001.mps"
        def row(partition, holders, check=checksum, locations=None):
            return json.dumps({"SaveId": {"Partition": partition, "Generation": 1, "Sequence": 1}, "Node": "node-3", "SizeBytes": size,
                               "Checksum": check, "Locations": locations or list(holders), "LocationHolders": holders, "CorruptHolders": []})
        db.execute("INSERT INTO Saves VALUES(?, ?)", ("1:1:1", row(1, {legacy: "node-3"})))
        db.execute("INSERT INTO ArchivedSaves VALUES(?, ?, 1, 1, ?)", ("2:1:1", 2, row(2, {}, locations=[legacy])))   # no holder: the writer
        db.execute("INSERT INTO Saves VALUES(?, ?)", ("3:1:1", row(3, {legacy: "node-3"}, check="FFFFFFFFFFFFFFFF")))
        db.execute("INSERT INTO state VALUES('Nodes', ?)", (json.dumps({"node-9": {"Node": "node-9", "State": 0, "Host": HERE}}),))
        db.execute("INSERT INTO state VALUES('HostStoreAliases', ?)", (json.dumps({"node-3": HERE}),))
        db.commit()
        db.close()
        before = snapshot(self.root)
        registry = cluster_hostd.OfflineRegistry(db_path)
        result = cluster_hostd.StoreSweep(self.root, HERE, registry, apply=False, keep_dirs=0, keep_hours=0,
                                          running=lambda root, node: False, log=lambda text: None).run()
        self.assertEqual(snapshot(self.root), before)
        self.assertEqual(result["classes"]["rekey"]["files"], 2, "one in memory, one archived")
        self.assertEqual(result["classes"]["kept.mismatch"]["files"], 1)
        self.assertEqual(result["dirs"]["node-9-data"], {"state": "live"})
        with self.assertRaisesRegex(cluster.ClusterError, "answers questions only"):
            registry.adopt(HERE, True, [])


if __name__ == "__main__":
    unittest.main()
