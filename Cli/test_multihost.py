"""T-0321 multi-host, launcher side: hosts file v2, addresses and node environment of a multi-host start,
cluster-hostd's fenced executor client and agent step, the start's token/launch/world distribution, and the
T-0320 single-host path left unchanged. Inert: a fake registry on a loopback port, a fake ssh (Cluster.run_on),
fake processes, temporary run roots; no Gateway, node or ssh starts."""
import base64
import hashlib
import json
import os
import stat
import tempfile
import time
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import cluster
import cluster_hostd
from test_cluster_nodes import FakeRegistry, envelope, host_view

ROOT_PATH = "/srv/run"


def v2(**overrides):
    data = {"version": 2, "gateway": "polaris", "worldAuthority": "polaris",
            "network": {"internalNetworks": ["192.168.1.0/24"], "networkWindows": ["03:50-04:08"]},
            "hosts": {"polaris": {"serving": 4, "spares": 1, "address": "192.168.1.20"},
                      "ai": {"serving": 2, "spares": 1, "address": "192.168.1.80", "ssh": "factory@ai"}}}
    data.update(overrides)
    return data


class HostsFileV2Test(unittest.TestCase):
    def test_the_next_run_sample_normalizes_with_defaults(self):
        spec = cluster.validate_hosts_file(v2(), "polaris", Path(ROOT_PATH))
        self.assertTrue(cluster.is_multi_host(spec))
        self.assertEqual(spec["network"], {"internalNetworks": ["192.168.1.0/24"], "portBase": 0,
                                           "networkWindows": ["03:50-04:08"]})
        polaris, ai = spec["hosts"]["polaris"], spec["hosts"]["ai"]
        self.assertEqual((polaris["ssh"], polaris["serverEnv"], polaris["runRoot"]), ("local", None, ROOT_PATH))
        self.assertEqual((ai["ssh"], ai["serverEnv"], ai["executorTokenFile"]),
                         ("factory@ai", "~/cluster-server/env.sh", "secrets/executor-ai.token"))
        self.assertEqual((ai["portSlots"], ai["portBase"]), (16, 0))
        # The registry still receives only counts and floors (the v1 body), whatever the file adds.
        self.assertEqual(set(cluster.hosts_file_request(spec, "x")["hosts"]["ai"]),
                         {"serving", "spares", "ramFloorMb", "diskFloorMb"})

    def test_the_committed_sample_validates(self):
        sample = Path.home() / "cluster-runs/regression/tools/t0321/hosts.sample.json"
        if not sample.is_file():
            self.skipTest("no tools/t0321/hosts.sample.json on this host")
        spec = cluster.validate_hosts_file(json.loads(sample.read_text()), "polaris",
                                           Path.home() / "cluster-runs/regression")
        self.assertEqual({name: (h["serving"], h["spares"]) for name, h in spec["hosts"].items()},
                         {"polaris": (4, 1), "ai": (2, 1)})

    def test_validation(self):
        def with_host(name, **entry):
            data = v2()
            data["hosts"][name] = {**data["hosts"][name], **entry}
            return data
        cases = {
            "ai: address must be an IP literal": with_host("ai", address="ai.lan"),
            "ai: address 10.0.0.5 is outside network.internalNetworks": with_host("ai", address="10.0.0.5"),
            "ai: address 127.0.0.1 is loopback": with_host("ai", address="127.0.0.1"),
            "ai: address is required in a multi-host file": {**v2(), "hosts": {**v2()["hosts"], "ai": {"serving": 2}}},
            "gateway is required in a multi-host file": {k: v for k, v in v2().items() if k not in ("gateway", "worldAuthority")},
            "worldAuthority must be the gateway host": v2(worldAuthority="ai"),
            "gateway 'msi' is not one of the hosts": v2(gateway="msi", worldAuthority="msi"),
            "runRoot /other differs from the run root": with_host("ai", runRoot="/other"),
            "runRoot must be an absolute path": with_host("ai", runRoot="relative/run"),
            "ssh local is only for the gateway host": with_host("ai", ssh="local"),
            "its ssh must be local": with_host("polaris", ssh="factory@polaris"),
            "portSlots 2 < 3 processes": with_host("ai", portSlots=2),
            "portSlots must be an integer 1..64": with_host("ai", portSlots=100),
            "network.networkWindows must be a list": v2(network={"internalNetworks": ["192.168.1.0/24"], "networkWindows": ["4:00"]}),
            "network.internalNetworks: 'lan' is not a CIDR": v2(network={"internalNetworks": ["lan"]}),
            "network: unknown key 'mtu'": v2(network={"internalNetworks": ["192.168.1.0/24"], "mtu": 9000}),
            "ai: unknown key 'cpus'": with_host("ai", cpus=32),
            "executor.tokenFile must be a plain path": with_host("ai", executor={"tokenFile": "../keys/x"}),
            "version must be 1 or 2": {"version": 3, "hosts": {"polaris": {"serving": 1}}},
        }
        for message, data in cases.items():
            with self.subTest(message), self.assertRaisesRegex(cluster.ClusterError, message):
                cluster.validate_hosts_file(data, "polaris", Path(ROOT_PATH))

    def test_two_entries_on_one_address_need_disjoint_port_slots(self):
        data = v2()
        data["hosts"]["bench"] = {"serving": 1, "address": "192.168.1.20", "ssh": "polaris", "portSlots": 4}
        with self.assertRaisesRegex(cluster.ClusterError, "share 192.168.1.20 with overlapping port slots"):
            cluster.validate_hosts_file(data, "polaris", Path(ROOT_PATH))
        data["hosts"]["bench"]["portBase"] = 1000
        spec = cluster.validate_hosts_file(data, "polaris", Path(ROOT_PATH))
        self.assertEqual(spec["hosts"]["bench"]["portBase"], 1000)

    def test_version_1_and_a_one_host_version_2_are_not_multi_host(self):
        one = cluster.validate_hosts_file({"hosts": {"polaris": {"serving": 4, "spares": 1}}}, "polaris")
        self.assertEqual(one["version"], 1)
        self.assertFalse(cluster.is_multi_host(one))
        single = cluster.validate_hosts_file({"version": 2, "hosts": {"polaris": {"serving": 4}}}, "polaris")
        self.assertFalse(cluster.is_multi_host(single))


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "run"
        self.root.mkdir()
        self.registry = FakeRegistry()
        self.addCleanup(self.registry.close)
        self.env = patch.dict(os.environ, {"CLUSTER_HOSTS_FILE": "", "CLUSTER_NODE_HOST": "", "CLUSTER_ADMIN_TOKEN": ""})
        self.env.start()
        self.addCleanup(self.env.stop)

    def write_hosts(self, data=None, root=None):
        data = data or v2()
        for entry in data["hosts"].values():
            entry.setdefault("runRoot", str(root or self.root))
        (self.root / "hosts.json").write_text(json.dumps(data))
        return data

    def make(self, host="polaris", **kwargs) -> cluster.Cluster:
        return cluster.Cluster(cluster.Config(root=self.root, host=host, regular_nodes=2, **kwargs))

    def secrets(self, join="J" * 44, admin="A" * 44):
        cluster.write_files(self.root, {"secrets/join.token": (join + "\n", 0o600),
                                        "secrets/admin.token": (admin + "\n", 0o600)})


class AddressTest(Fixture):
    def test_a_multi_host_file_moves_every_address_to_the_lan(self):
        self.write_hosts()
        polaris = self.make("polaris").config
        self.assertTrue(polaris.multi_host)
        self.assertEqual(polaris.gateway_bind, "192.168.1.20:29416")
        self.assertEqual(polaris.node_spec("node-3", 3).control, "192.168.1.20:29419")
        ai = self.make("ai").config
        self.assertEqual((ai.node_spec("node-9", 1).backend, ai.gateway_bind), ("192.168.1.80:28417", "192.168.1.20:29416"))

    def test_a_host_port_base_moves_its_slots(self):
        data = v2()
        data["hosts"]["ai"]["portBase"] = 1000
        self.write_hosts(data)
        spec = self.make("ai").config.node_spec("node-9", 2)
        self.assertEqual((spec.backend, spec.control), ("192.168.1.80:29418", "192.168.1.80:30418"))
        self.assertEqual(self.make("ai").config.slot_of(spec), 2)

    def test_single_host_keeps_loopback_byte_for_byte(self):
        # Plan principle 3: no hosts file, a v1 file, or a one-host v2 file = today's addresses.
        for data in (None, {"hosts": {"polaris": {"serving": 2}}},
                     {"version": 2, "hosts": {"polaris": {"serving": 2, "address": "192.168.1.20"}},
                      "network": {"internalNetworks": ["192.168.1.0/24"]}}):
            with self.subTest(data):
                (self.root / "hosts.json").unlink(missing_ok=True)
                if data:
                    (self.root / "hosts.json").write_text(json.dumps(data))
                config = self.make().config
                self.assertFalse(config.multi_host)
                self.assertEqual((config.gateway_bind, config.node_spec("node-1", 1).control),
                                 ("127.0.0.1:29416", "127.0.0.1:29417"))

    def test_a_node_host_reads_the_copy_the_start_left_but_the_gateway_host_does_not(self):
        data = self.write_hosts()
        (self.root / "hosts.json").unlink()
        (self.root / "hostd").mkdir()
        (self.root / "hostd" / "hosts.json").write_text(json.dumps(data))
        self.assertTrue(self.make("ai").config.multi_host)
        self.assertFalse(self.make("polaris").config.multi_host, "a stale copy must not turn the gateway host multi-host")


class NodeEnvironmentTest(Fixture):
    def env_of(self, instance, node_id="node-4"):
        seen = []
        with patch.object(instance, "require_plugin_sdk", lambda: None), \
                patch.object(instance, "refresh_premade_world", lambda node: None), \
                patch.object(instance, "prepare_magnetar_config", lambda node: self.root), \
                patch.object(instance, "_spawn", lambda name, argv, env, cwd=None: seen.append(env)):
            instance._new_regular_node(node_id)
            instance.start_node(node_id)
        return seen[0]

    def test_a_multi_host_node_binds_and_advertises_its_lan_address_with_this_starts_tokens(self):
        self.write_hosts()
        self.secrets()
        instance = self.make("ai")
        instance.config.nodes, instance.config.retired_nodes = [], []
        env = self.env_of(instance)
        self.assertEqual(env["CLUSTER_NODE_ENDPOINT"], "192.168.1.80:28417")
        self.assertEqual(env["CLUSTER_CONTROL_BIND"], "192.168.1.80")
        self.assertEqual(env["CLUSTER_NODE_CONTROL_ENDPOINT"], "http://192.168.1.80:29417")
        self.assertEqual(env["CLUSTER_GATEWAY_REGISTRY"], "http://192.168.1.20:29416")
        self.assertEqual(env["CLUSTER_INTERNAL_NETWORKS"], "192.168.1.0/24")
        self.assertEqual((env["CLUSTER_JOIN_TOKEN"], env["CLUSTER_ADMIN_TOKEN"]), ("J" * 44, "A" * 44))
        self.assertEqual(env["CLUSTER_NODE_HOST"], "ai")
        self.assertEqual(env["CLUSTER_WORLD"], str(self.root / "cluster-world"))
        self.assertFalse("CLUSTER_SHARED_ROOT" in env, "a multi-host node must not get CLUSTER_SHARED_ROOT")

    def test_no_secrets_no_launch(self):
        self.write_hosts()
        instance = self.make("ai")
        with self.assertRaisesRegex(cluster.ClusterError, "join.token is missing"):
            self.env_of(instance)

    def test_a_standalone_prewarm_node_needs_no_start_tokens(self):
        # r64 2026-10-01 04:08: `boot prewarm` on a multi-host run root died on the missing join.token, which only
        # `cluster start` mints; the prewarm node never joins, so it starts with the single-host environment.
        self.write_hosts()
        instance = self.make("polaris")
        seen = []
        with patch.object(instance, "require_plugin_sdk", lambda: None), \
                patch.object(instance, "refresh_premade_world", lambda node: None), \
                patch.object(instance, "prepare_magnetar_config", lambda node: self.root), \
                patch.object(instance, "_spawn", lambda name, argv, env, cwd=None: seen.append(env)):
            instance._new_regular_node("node-4")
            instance.start_node("node-4", allow_update=True, standalone=True)
        self.assertNotIn("CLUSTER_JOIN_TOKEN_CURRENT", seen[0])
        self.assertFalse(seen[0].get("CLUSTER_ADMIN_TOKEN"))
        self.assertNotIn("CLUSTER_CONTROL_BIND", seen[0])

    def test_an_idle_host_is_idle_through_a_real_shell(self):
        # r66 2026-10-01 04:16: check_host_idle's pgrep pattern matched its own `bash -c` (and the $( ) subshell):
        # 'MagnetarInterim.*' ran on to the unescaped run root in the same script -> an idle ai read nodes=2.
        self.write_hosts()
        instance = self.make("polaris")
        instance.check_host_idle("polaris")  # ssh=local: the same bash -c the remote host runs; must not raise

    def test_single_host_environment_is_unchanged(self):
        env = self.env_of(self.make())
        self.assertEqual(env["CLUSTER_JOIN_TOKEN"], cluster.JOIN_TOKEN)
        self.assertEqual(env["CLUSTER_GATEWAY_REGISTRY"], "http://127.0.0.1:29416")
        self.assertTrue("CLUSTER_SHARED_ROOT" in env)
        for key in ("CLUSTER_CONTROL_BIND", "CLUSTER_NODE_CONTROL_ENDPOINT", "CLUSTER_INTERNAL_NETWORKS", "CLUSTER_ADMIN_TOKEN"):
            self.assertFalse(env.get(key), f"{key} set in a single-host node environment")

    def test_the_gateway_of_a_multi_host_start_gets_the_token_file_and_lan_control(self):
        self.write_hosts()
        self.secrets()
        instance = self.make("polaris")
        env = instance._multi_host_gateway_env()
        self.assertEqual(env["CLUSTER_ADMIN_TOKENS"], str(self.root / "secrets/admin-tokens.json"))
        self.assertEqual(env["CLUSTER_JOIN_TOKEN_CURRENT"], "J" * 44)
        self.assertEqual(env["CLUSTER_ADMIN_TOKEN"], "A" * 44)
        self.assertEqual(env["CLUSTER_GATEWAY_HOST"], "polaris")
        self.assertEqual(env["CLUSTER_NETWORK_WINDOWS"], "03:50-04:08")
        self.assertNotIn("CLUSTER_START_ID", env)
        cluster.write_files(self.root, {"secrets/start-id": ("s-1\n", 0o600)})
        self.assertEqual(instance._multi_host_gateway_env()["CLUSTER_START_ID"], "s-1")
        # The CLI's own requests go to the LAN control with the per-start admin token.
        self.assertEqual(instance.config.admin_token(), "A" * 44)


class NodeCallTest(Fixture):
    """Cluster.node(): a multi-host call goes to the registry's ControlEndpoint of the node (any host) with the
    admin token its LAN prefix requires; a single-host call keeps the ledger's loopback port and no token."""

    def setUp(self):
        super().setUp()
        self.remote_node = FakeRegistry()          # a node control endpoint on another host
        self.addCleanup(self.remote_node.close)
        self.remote_node.routes[("GET", "/cluster/node-state")] = {"state": "Serving"}
        self.registry.routes[("GET", "/registry/status")] = lambda body, query: {"nodes": [
            {"Node": "node-9", "Epoch": 3, "State": 0, "ControlEndpoint": f"http://{self.remote_node.endpoint}"}]}

    def test_multi_host_sends_the_admin_token_to_the_node(self):
        self.write_hosts()
        self.secrets()
        instance = self.make("polaris")
        with patch.object(type(instance.config), "gateway_bind", property(lambda config: self.registry.endpoint)):
            self.assertEqual(instance.node("node-9", "/node-state"), {"state": "Serving"})
        path, headers, _ = self.remote_node.sent("GET", "/cluster/node-state")[-1]
        self.assertEqual(headers.get("Authorization"), "Bearer " + "A" * 44)

    def test_single_host_node_calls_carry_no_token(self):
        node = FakeRegistry()
        self.addCleanup(node.close)
        node.routes[("GET", "/cluster/node-state")] = {"state": "Serving"}
        instance = self.make("polaris", gateway_control=self.registry.endpoint)
        instance.config.nodes[0].control = node.endpoint
        with patch.dict(os.environ, {"CLUSTER_ADMIN_TOKEN": "gateway-only"}):
            instance.node(instance.config.nodes[0].node_id, "/node-state")
        path, headers, _ = node.sent("GET", "/cluster/node-state")[-1]
        self.assertNotIn("Authorization", headers)


class LocalStopTest(Fixture):
    """T-0347: a stop that finds no registry asks every node for a local stop (Final saves to its own store) on
    the loopback side of the node's control port, found from the process list and the node's own environment,
    waits the grace for the nodes to exit, and only then kills what is left. Still exit code 1."""

    def node_process(self, port: str, node_id: str, role: str = "Regular") -> int:
        import subprocess
        child = subprocess.Popen(["sleep", "60"], env={"PATH": os.environ.get("PATH", ""), "CLUSTER_CONTROL_PORT": port,
                                                       "CLUSTER_NODE_ID": node_id, "CLUSTER_NODE_ROLE": role,
                                                       "CLUSTER_ADMIN_TOKEN": "never-read"})
        self.addCleanup(child.wait)
        self.addCleanup(child.kill)
        # Popen returns before the kernel publishes the exec'd environment: /proc/<pid>/environ is still the
        # parent's for a moment, and a node read then is skipped (#1361, flaky on loaded CI runners).
        deadline = time.monotonic() + 10
        while b"CLUSTER_NODE_ID=" not in Path(f"/proc/{child.pid}/environ").read_bytes():
            self.assertLess(time.monotonic(), deadline, f"{node_id}: environment never appeared")
            time.sleep(0.001)
        return child.pid

    def pgrep(self, pids):
        import subprocess
        real = subprocess.run
        return patch.object(cluster.subprocess, "run", side_effect=lambda argv, *args, **kwargs: (
            subprocess.CompletedProcess(argv, 0, "".join(f"{pid}\n" for pid in pids), "") if argv[0] == "pgrep"
            else real(argv, *args, **kwargs)))

    def test_every_node_is_asked_once_on_its_own_control_port(self):
        node = FakeRegistry()
        self.addCleanup(node.close)
        node.routes[("POST", "/cluster/local-stop")] = {"accepted": True, "already": False, "owned": 4, "registryHeld": True}
        port = node.endpoint.rpartition(":")[2]
        # The launch wrapper and its dotnet child share one environment: one request. A node whose port is dead: an
        # error. The World Authority is not asked (it would answer on the same fake port here).
        pids = [self.node_process(port, "node-7"), self.node_process(port, "node-7"), self.node_process("1", "node-8"),
                self.node_process(port, "wa-1", role="WA")]
        with self.pgrep(pids):
            asked = cluster.local_stop_nodes(self.root, 100)
        self.assertEqual([path for path, _, _ in node.sent("POST", "/cluster/local-stop")], ["/cluster/local-stop?budget=100"])
        self.assertEqual([(entry["node"], entry.get("owned")) for entry in asked], [("node-7", 4), ("node-8", None)])
        self.assertIn("error", asked[1])
        self.assertNotIn("never-read", json.dumps(asked))

    def test_a_stop_without_a_registry_asks_the_nodes_before_it_kills(self):
        instance = self.make("polaris", gateway_control="127.0.0.1:1")
        order = []
        with patch.object(cluster, "local_stop_nodes", side_effect=lambda root, budget: order.append(("ask", budget))
                          or [{"node": "node-1", "pid": 1, "owned": 3}]), \
                patch.object(cluster, "regular_node_processes", side_effect=lambda root: order.append(("poll",)) or []), \
                patch.object(instance, "kill", side_effect=lambda: order.append(("kill",)) or []):
            self.assertFalse(instance.stop(120))
        self.assertEqual(order, [("ask", 100.0), ("poll",), ("poll",), ("kill",)])

    def test_a_stop_with_nothing_running_still_only_kills(self):
        instance = self.make("polaris", gateway_control="127.0.0.1:1")
        with patch.object(cluster, "local_stop_nodes", return_value=[]), patch.object(cluster, "regular_node_processes") as poll, \
                patch.object(instance, "kill", return_value=[]) as kill:
            self.assertFalse(instance.stop(30))
        poll.assert_not_called()
        kill.assert_called_once()


class FencedRegistry:
    """The fenced executor heartbeat's rules (ExecutorApi.cs PollExecutor), on a FakeRegistry: host-scoped
    tokens, one session per host while its lease runs, sequence/fingerprint replay, per-host plan, Gone must
    acknowledge the commanded incumbent, Ready needs a live incarnation."""

    def __init__(self, registry: FakeRegistry, tokens: dict[str, str]):
        self.tokens = tokens
        self.leases: dict[str, dict] = {}
        self.plan: list[dict] = []
        self.nodes: dict[str, dict] = {}          # node -> {epoch, state, leaseExpires}
        self.accepted: list[dict] = []
        self.polls: list[dict] = []
        self.clock = time.time
        registry.routes[("POST", "/executor/v1/heartbeat")] = self.handle
        registry.routes[("GET", "/registry/status")] = lambda body, query: {"nodes": [
            {"Node": node, "Epoch": row["epoch"], "State": row["state"], "LeaseExpires": row["leaseExpires"]}
            for node, row in self.nodes.items()]}
        self.registry = registry

    def handle(self, body, query):
        headers = self.registry.requests[-1][2]
        token = (headers.get("Authorization") or "")[7:]
        host = next((name for name, value in self.tokens.items() if value == token), None)
        if host is None:
            return 403, {"error": {"code": "executor_credential_required", "message": "A host-scoped executor credential is required."}}
        if body["host"] != host:
            return 403, {"error": {"code": "executor_host_mismatch", "message": "Credential does not own the requested host."}}
        self.polls.append(body)
        now = self.clock()
        current = self.leases.get(host)
        active = current is not None and current["expires"] > now
        conflict = lambda text: (409, {"error": {"code": "executor_fenced", "message": text}})  # noqa: E731
        if body["lease"] is None:
            if body["reports"]:
                return 400, {"error": {"code": "invalid_executor_request", "message": "Lease acquisition cannot report observations."}}
            if active and current["session"] != body["session"]:
                return conflict("Another executor session owns this host.")
            current = current if active else {"session": body["session"], "lease": str(uuid.uuid4()), "sequence": 0, "hash": ""}
        elif not active or current["lease"] != body["lease"] or current["session"] != body["session"]:
            return conflict("Executor lease expired or belongs to another Gateway/session.")
        digest = hashlib.sha256(json.dumps(body, sort_keys=True).encode()).hexdigest()
        if body["sequence"] < current["sequence"] or body["sequence"] == current["sequence"] and digest != current["hash"]:
            return conflict("Executor sequence was replayed with different or stale content.")
        slots = {slot["slotKey"]: slot for slot in self.plan}
        if body["sequence"] > current["sequence"]:
            for report in body["reports"]:
                slot = slots.get(report["slotKey"])
                if slot is None or slot["host"] != host:
                    return conflict("Observation does not belong to this host's plan.")
                node = self.nodes.get(report["node"] or "")
                if report["epoch"] > 0 and (node is None or node["epoch"] != report["epoch"]):
                    return conflict("Observation names a stale node incarnation.")
                if report["state"] == "Ready" and report["epoch"] <= 0:
                    return conflict("Ready requires a live matching Registry incarnation.")
                if report["state"] == "Gone" and slot["incumbentAction"] == "Kill" and (
                        report["node"] != slot["incumbentNode"] or report["epoch"] != slot["incumbentEpoch"]):
                    return conflict("Gone does not acknowledge the commanded incumbent.")
            self.accepted += body["reports"]
            for report in body["reports"]:
                slot = slots[report["slotKey"]]
                if report["state"] == "Gone" and slot["incumbentAction"] == "Kill":
                    slot.update(incumbentAction="None", incumbentNode=None, incumbentEpoch=0)
        current.update(expires=now + 60, sequence=body["sequence"], hash=digest)
        self.leases[host] = current
        return envelope({"lease": current["lease"], "expiresAt": "2026-10-01T00:01:00Z",
                         "plan": [slot for slot in self.plan if slot["host"] == host]})


def fslot(node, host="ai", goal="Wanted", incumbent="None", incumbent_node=None, epoch=0, allowed=True):
    """An admin-contract NodePlan (camelCase, enums as names), as the fenced heartbeat returns it."""
    return {"slotKey": node, "host": host, "role": "Regular", "goal": goal, "observed": "Missing",
            "observedNode": None, "incumbentAction": incumbent, "incumbentNode": incumbent_node,
            "incumbentEpoch": epoch, "spawnAllowed": allowed, "failureCount": 0, "failureAlert": False}


class ExecutorClientTest(Fixture):
    def setUp(self):
        super().setUp()
        self.fenced = FencedRegistry(self.registry, {"ai": "tok-ai", "polaris": "tok-polaris"})
        self.fenced.plan = [fslot("node-7"), fslot("node-8", host="polaris")]
        self.logs = []

    def client(self, host="ai", token="tok-ai"):
        return cluster_hostd.ExecutorClient(f"http://{self.registry.endpoint}", host, token, log=self.logs.append)

    def test_acquires_the_lease_and_gets_only_its_hosts_slots(self):
        client = self.client()
        client.poll([])
        self.assertIsNotNone(client.lease)
        self.assertEqual([slot["slotKey"] for slot in client.plan], ["node-7"])
        body = self.fenced.polls[0]
        self.assertEqual((body["host"], body["executor"], body["lease"], body["reports"]), ("ai", "cluster-hostd@ai", None, []))

    def test_a_second_agent_is_refused_while_the_first_holds_the_lease(self):
        self.client().poll([])
        with self.assertRaisesRegex(cluster.ClusterError, "Another executor session owns this host"):
            self.client().poll([])

    def test_a_lost_lease_is_acquired_again(self):
        client = self.client()
        client.poll([])
        self.fenced.leases["ai"]["expires"] = 0           # the registry restarted / the lease ran out
        refused = client.poll([cluster_hostd.executor_report(
            {"slotKey": "node-7", "attemptKey": "a", "state": "Spawning", "node": "node-7", "epoch": 0})])
        self.assertIsNone(client.lease)
        self.assertEqual(len(refused), 1)
        client.poll([])
        self.assertIsNotNone(client.lease)

    def test_one_refused_report_does_not_lose_the_others(self):
        self.fenced.plan = [fslot("node-7"), fslot("node-9", incumbent="Kill", incumbent_node="node-9", epoch=4)]
        client = self.client()
        client.poll([])
        good = {"slotKey": "node-7", "attemptKey": "a", "state": "Spawning", "node": "node-7", "epoch": 0, "failure": None}
        bad = {"slotKey": "node-9", "attemptKey": "b", "state": "Gone", "node": "node-9", "epoch": 3, "failure": None}
        self.fenced.nodes["node-9"] = {"epoch": 3, "state": 0, "leaseExpires": None}
        refused = client.poll([good, bad])
        self.assertEqual(refused, [bad])
        self.assertIn(good, self.fenced.accepted)

    def test_a_refused_credential_is_a_stale_agent(self):
        with self.assertRaises(cluster_hostd.StaleCredential):
            self.client(token="tok-of-an-earlier-start").poll([])
        with self.assertRaises(cluster_hostd.StaleCredential):
            self.client(host="ai", token="tok-polaris").poll([])

    def test_ready_without_an_incarnation_is_sent_as_spawning(self):
        report = cluster_hostd.executor_report({"slotKey": "node-7", "attemptKey": "a", "state": "Ready", "node": "node-7", "epoch": 0})
        self.assertEqual(report["state"], "Spawning")


class HostAgentTest(Fixture):
    """cluster-hostd's step on host ai against the fenced registry, with fake node processes."""

    def setUp(self):
        super().setUp()
        self.write_hosts()
        self.secrets()
        self.fenced = FencedRegistry(self.registry, {"ai": "tok-ai"})
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view(host="ai")], "nodes": []})
        self.instance = self.make("ai", gateway_control=self.registry.endpoint)
        # The fake registry stands in for the Gateway at 192.168.1.20:29416.
        patcher = patch.object(type(self.instance.config), "gateway_bind", property(lambda config: self.registry.endpoint))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.instance.config.nodes, self.instance.config.retired_nodes = [], []
        self.addCleanup(self.instance.release_launcher_lock)
        self.running, self.started, self.killed = set(), [], []
        for name, fake in {
            "_node_pids": lambda node_id: [5000] if node_id in self.running else [],
            "_node_pid": lambda node_id: 5000 if node_id in self.running else None,
            "start_node": lambda node_id, allow_update=False: (self.started.append(node_id), self.running.add(node_id)),
            "_kill_pids": lambda slot=None: (self.killed.append(slot), self.running.discard(slot), [1])[2],
            "await_port_free": lambda spec, deadline=30.0: True,
            "seed_sources": lambda spec: None,
            "_memory_available_mb": lambda: 64 * 1024,
            "_disk_free_mb": lambda: 500 * 1024,
        }.items():
            patcher = patch.object(self.instance, name, fake)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = patch.object(cluster, "port_open", lambda host, port, timeout=0.3: False)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.client = cluster_hostd.ExecutorClient(f"http://{self.registry.endpoint}", "ai", "tok-ai", log=lambda text: None)
        self.agent = cluster_hostd.HostAgent(self.instance, self.client, log=lambda text: None)
        self.agent.warm = True

    def live(self, node, epoch):
        self.fenced.nodes[node] = {"epoch": epoch, "state": 0,
                                   "leaseExpires": (datetime.now(timezone.utc) + timedelta(seconds=15)).isoformat()}

    def test_spawns_wanted_slots_and_reports_ready_only_for_a_live_incarnation(self):
        self.fenced.plan = [fslot("node-7"), fslot("node-8"), fslot("node-3", host="polaris")]
        summary = self.agent.step()     # acquires the lease, then acts on the plan it returned
        self.assertEqual(sorted(summary["spawned"]), ["node-7", "node-8"])
        self.assertEqual(sorted(self.started), ["node-7", "node-8"])
        self.assertEqual({r["slotKey"]: r["state"] for r in self.fenced.accepted}, {"node-7": "Spawning", "node-8": "Spawning"})
        self.agent.step()               # running, not registered yet: still Spawning, epoch 0
        self.assertEqual(self.fenced.accepted[-1]["state"], "Spawning")
        self.live("node-7", 11)
        self.agent.step()
        ready = [r for r in self.fenced.accepted if r["state"] == "Ready"]
        self.assertEqual([(r["slotKey"], r["epoch"]) for r in ready], [("node-7", 11)])
        self.assertEqual(self.started.count("node-7"), 1, "a running node is never started twice")

    def test_gone_acknowledges_the_commanded_incumbent(self):
        self.running.add("node-7")
        self.instance._new_regular_node("node-7")
        self.live("node-7", 11)
        self.fenced.plan = [fslot("node-7", goal="Kill", incumbent="Kill", incumbent_node="node-7", epoch=11)]
        with patch.object(cluster, "SELF_EXIT_SECONDS", 0):
            self.agent.step()
            self.agent.step()
        gone = [r for r in self.fenced.accepted if r["state"] == "Gone"]
        self.assertEqual((gone[0]["node"], gone[0]["epoch"]), ("node-7", 11))
        self.assertEqual(self.killed, ["node-7"])

    def test_never_restarts_a_registered_id(self):
        self.live("node-7", 11)
        self.fenced.nodes["node-7"]["state"] = 2      # Dead
        self.fenced.plan = [fslot("node-7")]
        self.agent.step()
        self.agent.step()
        self.assertEqual(self.started, [])
        self.assertEqual(self.fenced.accepted[-1]["state"], "Gone")

    def test_a_launch_below_the_floor_is_refused_and_reported_failed(self):
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"hosts": [host_view(host="ai", ram=128 * 1024)], "nodes": []})
        self.fenced.plan = [fslot("node-7")]
        self.agent.step()
        summary = self.agent.step()
        self.assertEqual((summary["refused"], self.started), (["node-7"], []))
        failed = self.fenced.accepted[-1]
        self.assertEqual((failed["state"], failed["epoch"]), ("Failed", 0))
        self.assertIn("host_floor", failed["failure"])

    def test_the_first_node_on_a_host_starts_alone(self):
        self.agent.warm = False
        self.fenced.plan = [fslot("node-7"), fslot("node-8"), fslot("node-9")]
        self.agent.step()
        self.agent.step()
        self.assertEqual(len(self.started), 1)
        self.agent.step()
        self.assertEqual(len(self.started), 1, "no second launch while the first node warms the host")
        self.live(self.started[0], 5)
        self.agent.step()               # Ready observed: the host is warm
        self.agent.step()
        self.assertEqual(len(self.started), 3)

    def test_no_action_on_a_stale_plan(self):
        self.agent.step()               # lease acquired, empty plan
        self.client.plan = [fslot("node-7")]  # a plan the agent holds but can no longer refresh
        self.client.polled_at -= cluster_hostd.PLAN_FRESH_SECONDS + 1
        self.registry.routes.pop(("POST", "/executor/v1/heartbeat"))       # the registry is unreachable now
        with self.assertRaises(cluster.ClusterError):
            self.agent.step()
        self.assertEqual(self.started, [])

    def test_no_action_on_a_stale_plan_after_the_lease_was_lost(self):
        """T-0296 r2: the registry IS reachable again but this agent's lease is gone (a gateway-only restart, or a LAN cut
        longer than the 60 s lease). The refresh poll answers 409 lease-lost, which clears the lease without raising; the
        step must not then act on the plan it held from before - the registry's current plan no longer wants node-7."""
        self.agent.step()               # lease acquired, empty plan
        self.client.plan = [fslot("node-7")]  # the plan from before the outage
        self.client.polled_at -= cluster_hostd.PLAN_FRESH_SECONDS + 1
        self.fenced.leases["ai"]["expires"] = 0         # the lease ran out / the Gateway restarted
        self.fenced.plan = []                           # meanwhile the slot was removed
        try:
            self.agent.step()
        except cluster.ClusterError:
            pass                        # skipping the step is as good as acting on the fresh plan
        self.assertEqual(self.started, [], "a launch from a plan older than PLAN_FRESH_SECONDS")

    def test_the_step_reads_the_registrys_store_mode(self):
        self.agent.step()
        self.assertFalse(self.agent.store_mode)
        status = self.registry.routes[("GET", "/registry/status")]
        self.registry.routes[("GET", "/registry/status")] = lambda body, query: {**status(body, query), "hostStore": {"enabled": True}}
        self.agent.step()
        self.assertTrue(self.agent.store_mode, "T-0340 step 7: the store keeper acts only in store mode")

    def test_the_rotators_launcher_step_is_a_no_op_in_a_multi_host_start(self):
        other = self.make("polaris", gateway_control=self.registry.endpoint)
        self.assertTrue(other.actualize_step()["delegated"])
        self.assertIsNone(other._launcher_lock, "the rotator must not take the lock cluster-hostd holds")


class MultiHostStartTest(Fixture):
    """prepare_multi_host on the gateway host with a fake ssh: the gates, the tokens, the bundles, the world."""

    def setUp(self):
        super().setUp()
        self.write_hosts()
        (self.root / "cluster-world" / "Storage").mkdir(parents=True)
        (self.root / "cluster-world" / "Sandbox.sbc").write_text("<world/>")
        (self.root / "cluster-world" / "Storage" / "seed.cfg").write_text("seed")
        self.instance = self.make("polaris")
        self.calls = []                  # (host, script, stdin)
        self.remote = {"nodes": "0", "hostd": "0", "archive": "", "tree": None}
        patcher = patch.object(self.instance, "run_on", self.fake_run_on)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.rsync = []
        real_run = cluster.subprocess.run

        def fake_run(argv, *args, **kwargs):
            if argv and argv[0] == "rsync":
                self.rsync.append(argv)
                return cluster.subprocess.CompletedProcess(argv, 0, "", "")
            return real_run(argv, *args, **kwargs)
        patcher = patch.object(cluster.subprocess, "run", fake_run)
        patcher.start()
        self.addCleanup(patcher.stop)

    def fake_run_on(self, host, script, input=None, timeout=120, check=True):
        self.calls.append((host, script, input))
        if "pgrep -fc" in script:
            out = f"nodes={self.remote['nodes']} hostd={self.remote['hostd']}"
        elif "manifest.json" in script:
            out = self.remote["archive"]
        elif "hashlib" in script:
            out = self.remote["tree"] or cluster.tree_hash(self.root / "cluster-world")
        elif "json.load(sys.stdin)" in script:
            self.bundle = json.loads(input)
            out = str(len(self.bundle))
        else:
            out = ""
        return cluster.subprocess.CompletedProcess([host], 0, out.encode(), b"")

    def test_tokens_bundles_and_world(self):
        self.instance.prepare_multi_host()
        secrets = self.root / "secrets"
        for name in ("join.token", "admin.token", "admin-tokens.json", "executor-polaris.token", "start-id"):
            mode = stat.S_IMODE((secrets / name).stat().st_mode)
            self.assertEqual(mode, 0o600, name)
        join, admin = (secrets / "join.token").read_text().strip(), (secrets / "admin.token").read_text().strip()
        for token in (join, admin):
            self.assertGreaterEqual(len(base64.b64decode(token)), 32)
        entries = json.loads((secrets / "admin-tokens.json").read_text())["tokens"]
        self.assertEqual({(e["name"], e["scope"]) for e in entries}, {("polaris", "Executor"), ("ai", "Executor")})
        ai_token = self.bundle["secrets/executor-ai.token"]["content"].strip()
        ai_entry = next(e for e in entries if e["name"] == "ai")
        self.assertEqual(ai_entry["sha256"], hashlib.sha256(ai_token.encode()).hexdigest().upper())
        self.assertNotIn(ai_token, (secrets / "admin-tokens.json").read_text(), "only hashes in the token file")
        # ai receives its own executor token and the shared ones, never polaris' executor token.
        polaris_token = (secrets / "executor-polaris.token").read_text().strip()
        self.assertNotIn(polaris_token, json.dumps(self.bundle))
        self.assertEqual({path: item["mode"] for path, item in self.bundle.items()},
                         {"secrets/join.token": 0o600, "secrets/admin.token": 0o600, "secrets/executor-ai.token": 0o600,
                          "hostd/hosts.json": 0o644, "hostd/launch.json": 0o644})
        # No token ever travels on a command line (ssh argv / the remote script).
        for token in (join, admin, ai_token, polaris_token):
            for host, script, _ in self.calls:
                self.assertNotIn(token, script)
            for argv in self.rsync:
                self.assertNotIn(token, " ".join(argv))
        launch = json.loads(self.bundle["hostd/launch.json"]["content"])
        self.assertNotIn(admin, json.dumps(launch))
        self.assertEqual(launch["gateway"], "http://192.168.1.20:29416")
        # The world went to ai's run root and was hash-checked there.
        self.assertEqual(len(self.rsync), 1)
        self.assertTrue(self.rsync[0][-1].endswith(f"factory@ai:{self.root}/cluster-world/"))

    def test_launch_spec_carries_tuning_but_no_secret_or_host_path(self):
        with patch.dict(os.environ, {"CLUSTER_NODE_KSM": "regular", "CLUSTER_ADMIN_TOKEN": "secret-admin",
                                     "CLUSTER_ARCHIVE": "/polaris/archive", "MAGNETAR_HOME": "/polaris/magnetar"}):
            env = self.instance.launch_spec()["env"]
        self.assertEqual(env.get("CLUSTER_NODE_KSM"), "regular")
        for key in ("CLUSTER_ADMIN_TOKEN", "CLUSTER_ARCHIVE", "MAGNETAR_HOME"):
            self.assertNotIn(key, env)

    def test_a_busy_host_refuses_the_start(self):
        self.remote["nodes"] = "3"
        with self.assertRaisesRegex(cluster.ClusterError, "ai still runs this cluster"):
            self.instance.prepare_multi_host()
        self.assertFalse((self.root / "secrets" / "join.token").exists(), "nothing minted before the gates pass")

    def test_a_different_archive_refuses_the_start(self):
        self.remote["archive"] = "0ld0ld0"
        with patch.object(cluster, "archive_identity", lambda archive: "n3wn3w0"):
            with self.assertRaisesRegex(cluster.ClusterError, "ai runs archive 0ld0ld0, this host n3wn3w0"):
                self.instance.prepare_multi_host()

    def test_a_world_that_differs_after_staging_refuses_the_start(self):
        self.remote["tree"] = "f" * 64
        with self.assertRaisesRegex(cluster.ClusterError, "the world staged on ai hashes ffffffffffff"):
            self.instance.prepare_multi_host()

    def test_only_the_gateway_host_starts(self):
        with self.assertRaisesRegex(cluster.ClusterError, "names polaris as the gateway host"):
            self.make("ai").prepare_multi_host()

    def test_a_remote_cli_call_sources_the_server_env_and_carries_no_secret(self):
        self.instance.host_cli("ai", ["hostd", "start", "--host", "ai", "--fresh"])
        host, script, stdin = self.calls[-1]
        self.assertEqual(host, "ai")
        self.assertTrue(script.startswith('. "$HOME"/cluster-server/env.sh && CLUSTER_NODE_HOST=ai exec "$CLUSTER_ARCHIVE/cli/cluster"'))
        self.assertIn(f"--root {self.root} hostd start --host ai --fresh", script)
        self.assertIsNone(stdin)


class HostdProcessTest(Fixture):
    def test_one_agent_per_host(self):
        self.write_hosts()
        self.secrets()
        (self.root / "hostd").mkdir()
        (self.root / "hostd" / "launch.json").write_text(json.dumps({"gateway": "http://127.0.0.1:1"}))
        (self.root / "hostd" / "hosts.json").write_text((self.root / "hosts.json").read_text())
        cluster.write_files(self.root, {"secrets/executor-ai.token": ("tok\n", 0o600)})
        holder = self.make("ai")
        holder.acquire_launcher_lock()
        self.addCleanup(holder.release_launcher_lock)
        logs = []
        self.assertEqual(cluster_hostd.run(self.root, "ai", log=logs.append), 4)
        self.assertIn("another launcher holds", logs[-1])

    def test_logs_of_a_node_on_another_host_are_read_there(self):
        self.write_hosts()
        instance = self.make("polaris")
        calls = []
        with patch.object(instance, "resolve_selector", lambda selector: {"node": "node-9", "host": "ai"}), \
                patch.object(cluster.subprocess, "call", lambda argv: calls.append(argv) or 0):
            instance.logs("node-9", lines=20, follow=True)
        self.assertEqual(calls[0][:-1], [*cluster.ssh_argv("factory@ai")])
        self.assertEqual(calls[0][-1], f"tail -n 20 -F {self.root}/node-9.log")

    def test_logs_of_a_node_on_this_host_are_read_locally(self):
        self.write_hosts()
        instance = self.make("polaris")
        calls = []
        with patch.object(instance, "resolve_selector", lambda selector: {"node": "node-2", "host": "polaris"}), \
                patch.object(cluster.subprocess, "call", lambda argv: calls.append(argv) or 0):
            instance.logs("node-2", lines=5)
        self.assertEqual(calls, [["bash", "-c", f"tail -n 5 {self.root}/node-2.log"]])

    def test_game_log_of_a_remote_node_is_its_newest_ds_log_there(self):
        self.write_hosts()
        instance = self.make("polaris")
        calls = []
        with patch.object(instance, "resolve_selector", lambda selector: {"node": "node-9", "host": "ai"}), \
                patch.object(cluster.subprocess, "call", lambda argv: calls.append(argv) or 0):
            instance.logs("node-9", lines=7, game=True)
        self.assertEqual(calls[0][:-1], [*cluster.ssh_argv("factory@ai")])
        self.assertEqual(calls[0][-1], f"tail -n 7 $(ls -t {self.root}/node-9-data/SpaceEngineersDedicated_*.log"
                                       " 2>/dev/null | head -1)")

    def test_logs_of_a_dead_node_are_found_on_the_host_that_has_them(self):
        self.write_hosts()
        instance = self.make("polaris")
        calls, probes = [], []

        def gone(selector):
            raise cluster.ClusterError("no live node matches 'node-9'")

        def run_on(host, script, **_):
            probes.append((host, script))
            return cluster.subprocess.CompletedProcess(script, 0 if host == "ai" else 1, b"", b"")
        with patch.object(instance, "resolve_selector", gone), patch.object(instance, "run_on", run_on), \
                patch.object(cluster.subprocess, "call", lambda argv: calls.append(argv) or 0):
            instance.logs("node-9", lines=3)
            self.assertEqual([host for host, _ in probes], ["polaris", "ai"])
            self.assertEqual(calls[0][:-1], [*cluster.ssh_argv("factory@ai")])
            self.assertEqual(calls[0][-1], f"tail -n 3 {self.root}/node-9.log")
            with self.assertRaises(cluster.ClusterError):       # a selector that is not a node id stays an error
                instance.logs("1234@ai")
            probes.clear()
            with patch.object(instance, "run_on", lambda host, script, **_: probes.append(host)
                              or cluster.subprocess.CompletedProcess(script, 1, b"", b"")), self.assertRaises(cluster.ClusterError):
                instance.logs("node-77")


class StoreKeeperTest(Fixture):
    """T-0340 step 7, hostd side: the store keeper thread (disk report, opt-in sweep), the registry it asks, the CLI."""

    def setUp(self):
        super().setUp()
        from test_cluster_nodes import FakeCatalogue, legacy_path, write_mps
        self.catalogue = FakeCatalogue(host="ai")
        path = legacy_path(self.root, "node-3", 1)
        checksum = write_mps(path, 1, 1, 1)
        self.catalogue.add(1, {"node-3"}, checksum, path.stat().st_size)
        self.source = path
        self.agent = type("Agent", (), {})()
        self.agent.store_mode, self.agent.log, self.logs = False, None, []
        self.agent.log = self.logs.append
        self.agent.stop = __import__("threading").Event()

    def keeper(self):
        return cluster_hostd.StoreKeeper(self.agent, self.root, "ai", registry=self.catalogue)

    def test_nothing_happens_outside_store_mode(self):
        keeper = self.keeper()
        keeper.tick()
        self.assertEqual(keeper.status, {})
        self.assertEqual(self.catalogue.calls, [])

    def test_store_mode_reports_disk_and_sweeps_only_when_asked(self):
        self.agent.store_mode = True
        keeper = self.keeper()
        keeper.tick()
        self.assertEqual(keeper.status["disk"]["legacy"]["files"], 1)
        self.assertNotIn("lastSweep", keeper.status, "CLUSTER_HOSTD_SWEEP_SECONDS=0 (the default): no sweep")
        self.assertTrue(self.source.exists())
        with patch.object(cluster_hostd, "SWEEP_SECONDS", 60):
            keeper = self.keeper()
            keeper.tick()
        self.assertFalse(self.source.exists())
        self.assertTrue((self.root / "saves" / "ai" / "P-0000000000000001" / "g00000001-s00000001.mps").exists())
        self.assertEqual(keeper.status["lastSweep"]["counts"]["moved.rekeyed"]["files"], 1)
        self.assertEqual((keeper.status["disk"]["store"]["files"], keeper.status["disk"]["legacy"]["files"]), (1, 0),
                         "the disk report after the sweep")

    def test_hostd_status_carries_the_store_report(self):
        self.write_hosts()
        instance = self.make("ai")
        agent = cluster_hostd.HostAgent(instance, cluster_hostd.ExecutorClient("http://127.0.0.1:1", "ai", "t"), log=lambda text: None)
        agent.store_mode = True
        agent.keeper = cluster_hostd.StoreKeeper(agent, self.root, "ai", registry=self.catalogue)
        agent.keeper.tick()
        agent.write_status()
        status = cluster_hostd.status(self.root)["status"]
        self.assertEqual(status["store"]["disk"]["legacy"]["dirs"], {"node-3-data": {"files": 1, "bytes": self.source.stat().st_size}})
        self.assertIn("freeMb", status["store"]["disk"])

    def test_the_registry_view_uses_the_adopt_route_with_the_admin_token(self):
        self.secrets(admin="A" * 44)
        (self.root / "hostd").mkdir()
        (self.root / "hostd" / "launch.json").write_text(json.dumps({"gateway": f"http://{self.registry.endpoint}"}))
        answers = {"storeMode": True, "host": "ai", "files": [{"Status": "adopt", "SizeBytes": 1, "Checksum": "C", "Archived": True}]}
        self.registry.routes[("POST", "/registry/host-store/adopt")] = lambda body, query: answers
        self.registry.routes[("GET", "/registry/nodes")] = [{"Node": "node-3", "State": 3}, {"Node": "node-4", "State": 0}]
        self.registry.routes[("GET", "/admin/v1/node-control")] = envelope({"nodes": [{"node": "node-7"}], "notCounted": [{"node": "node-8"}]})
        self.registry.routes[("GET", "/registry/status")] = {"hostStore": {"enabled": True}}
        self.registry.routes[("GET", "/registry/host-store/archived-partitions")] = {"partitions": [5, 7]}
        view = cluster_hostd.registry_view(self.root)
        files, _ = cluster_hostd.scan_legacy(self.root / "node-3-data", "node-3")
        self.assertEqual(view.adopt("ai", False, files), [{"status": "adopt", "archived": True, "sizeBytes": 1, "checksum": "C"}])
        _, headers, body = self.registry.sent("POST", "/registry/host-store/adopt")[0]
        self.assertEqual(headers["Authorization"], "Bearer " + "A" * 44)
        self.assertEqual(body, {"host": "ai", "commit": False, "files": [
            {"node": "node-3", "partition": 1, "generation": 1, "sequence": 1, "global": False,
             "sizeBytes": self.source.stat().st_size, "checksum": "0000000000001234"}]})
        self.assertEqual(view.nodes(), ({"node-3", "node-4"}, {"node-4", "node-7", "node-8"}))
        self.assertTrue(view.store_mode())
        self.assertEqual(view.archived_partitions(), {5, 7})

    def test_the_cli_dry_run_and_its_refusals(self):
        import contextlib
        import io
        with patch.object(cluster_hostd, "registry_view", lambda root: self.catalogue), \
                patch.object(cluster_hostd, "node_running", lambda root, node: False):
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(cluster.main(["--root", str(self.root), "hostd", "sweep", "--host", "ai", "--dry-run",
                                               "--keep-hours", "0", "--keep-dirs", "0"]), 0)
        result = json.loads(out.getvalue())
        self.assertTrue(result["dryRun"])
        self.assertEqual(result["classes"]["rekey"]["files"], 1)
        self.assertEqual(result["wouldDelete"], ["node-3-data"])
        self.assertEqual(result["disk"]["legacy"]["files"], 1)
        self.assertTrue(self.source.exists() and not (self.root / "saves").exists())
        with self.assertRaisesRegex(cluster.ClusterError, "dry run on a copied run root"):
            cluster.main(["--root", str(self.root), "hostd", "sweep", "--apply", "--registry-db", str(self.root / "x.db")])
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stderr(io.StringIO()):
                cluster.main(["--root", str(self.root), "hostd", "sweep", "--apply", "--dry-run"])

    def test_the_cli_refuses_a_deletion_run_without_a_backup_and_touches_nothing(self):
        import contextlib
        import io
        from test_cluster_nodes import snapshot
        before = snapshot(self.root)
        with patch.object(cluster_hostd, "registry_view", lambda root: self.catalogue), \
                patch.object(cluster_hostd, "node_running", lambda root, node: False):
            for extra in ([], ["--backup-verified", str(self.root / "no-such-backup")]):
                err = io.StringIO()
                with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
                    code = cluster.main(["--root", str(self.root), "hostd", "sweep", "--host", "ai", "--apply", "--delete-unnamed", *extra])
                self.assertEqual(code, 3, err.getvalue())
                self.assertIn("REFUSED, nothing touched", err.getvalue())
        self.assertEqual(snapshot(self.root), before)
        self.assertEqual(self.catalogue.calls, [], "not even a question was asked")

    def test_the_keeper_never_deletes_unnamed_files_or_runs_retention(self):
        from test_cluster_nodes import legacy_path, write_mps
        unnamed = legacy_path(self.root, "node-4", 2)
        write_mps(unnamed, 2, 1, 1)
        self.catalogue.add(2, {"node-7"}, "0000000000001234", unnamed.stat().st_size)
        old = time.time() - 400 * 86400
        os.utime(unnamed, (old, old))
        self.catalogue.archived = {1}
        self.agent.store_mode = True
        with patch.object(cluster_hostd, "SWEEP_SECONDS", 60), patch.object(cluster_hostd, "ARCHIVE_RETENTION_DAYS", 1), \
                patch.object(cluster_hostd, "KEEP_DIRS", 0), patch.object(cluster_hostd, "KEEP_HOURS", 0):
            keeper = self.keeper()
            keeper.tick()
        self.assertNotIn("error", keeper.status["lastSweep"], keeper.status)
        self.assertTrue(unnamed.exists() and (self.root / "node-4-data").exists())
        self.assertTrue((self.root / "saves" / "ai" / "P-0000000000000001" / "g00000001-s00000001.mps").exists(), "no retention")


if __name__ == "__main__":
    unittest.main()
