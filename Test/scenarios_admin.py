"""P3-ADM-01..03: the versioned admin API contract, against the packaged Gateway and admin CLI.

No game nodes: each scenario starts the package's ``registry/ClusterRegistry.dll`` on its own and
drives ``/admin/v1`` over HTTP and through ``cli/gateway-admin``. The control plane binds a
non-loopback address of this host, which is what makes the Gateway enforce bearer tokens; the
address is declared internal so the Gateway accepts it.

* P3-ADM-01 - parity: the admin CLI's self-test (the static CLI <-> operation bijection) and live
  CLI-vs-HTTP equality of the same read and the same idempotent mutation.
* P3-ADM-02 - scopes and audit: no token is 401, a query token reads everything and is refused
  every mutation with the stable 403 ``authorization_denied``, a manage token mutates, the
  mutation is audited to ``/events``, and no token value is ever echoed.
* P3-ADM-03 - durable operations: a mutation without ``Idempotency-Key`` is 400, a key replays the
  original operation, a key reused for a different request is 409, config writes are CAS, and
  after SIGKILL and a warm restart the replay and the ``/events`` cursor both survive.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import signal
import socket
import subprocess
import time
import urllib.error
import urllib.request

from bench import HOST
from cluster import CRASH_ENV

MANAGE_TOKEN = base64.b64encode(b"manage-token-0123456789012345678901").decode()
QUERY_TOKEN = "query-token-for-admin-api-test"
READ_ROUTES = ["/health", "/capabilities", "/status", "/plan", "/recovery-readiness", "/nodes",
               "/world-authority", "/partitions", "/snapshots", "/config", "/clients",
               "/admission/bans", "/events", "/operations", "/diag"]
# Loopback-free ports of their own, offset by the host's port base.
CONTROL_PORT = 29957 + HOST.port_base
BACKEND_PORT = 28957 + HOST.port_base
LISTEN_PORT = 27957 + HOST.port_base
PERSIST_INTERVAL = 1


def host_ip() -> str:
    """The local address that routes outward (the UDP connect sends nothing)."""
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("10.255.255.255", 1))
        return probe.getsockname()[0]
    finally:
        probe.close()


class AdminGateway:
    """One self-hosted Gateway with scoped admin tokens, under the scenario's run root."""

    def __init__(self, bench) -> None:
        self.bench = bench
        self.ip = host_ip()
        self.base = f"http://{self.ip}:{CONTROL_PORT}/admin/v1"
        self.root = bench.run_root / "admin-gateway"
        # A fresh registry every run: a previous run's idempotency records would answer this one.
        shutil.rmtree(self.root, ignore_errors=True)
        (bench.run_root / "admin-gateway.log").unlink(missing_ok=True)
        self.root.mkdir(parents=True, exist_ok=True)
        self.tokens = self.root / "admin-tokens.json"
        self.tokens.write_text(json.dumps({"tokens": [
            {"name": "quasar", "scope": "manage", "sha256": hashlib.sha256(MANAGE_TOKEN.encode()).hexdigest()},
            {"name": "monitor", "scope": "query", "sha256": hashlib.sha256(QUERY_TOKEN.encode()).hexdigest()},
        ]}))
        self.process: subprocess.Popen | None = None

    def environment(self) -> dict[str, str]:
        return {**os.environ, **CRASH_ENV,
                "CLUSTER_ALLOW_TEST_ACCEPT_ALL": "true",
                "CLUSTER_JOIN_TOKEN_CURRENT": base64.b64encode(b"\0" * 32).decode(),
                "CLUSTER_ID": "admin-api-test", "CLUSTER_WORLD_ID": "admin-api-test-world",
                "CLUSTER_CONFIG_HASH": "cfg", "CLUSTER_BINARY_VERSION": "v",
                "CLUSTER_REPLICATION_TABLE_HASH": "r", "CLUSTER_WORLD": str(self.root.parent / "admin-world"),
                "CLUSTER_SYNC_DISTANCE": "3000",
                # Registry state coalesces in memory before it is flushed (60 s in production,
                # StateDurability.md); the restart leg waits out this shorter interval.
                "CLUSTER_PERSIST_INTERVAL_SECONDS": str(PERSIST_INTERVAL),
                "CLUSTER_ADMIN_TOKEN": MANAGE_TOKEN, "CLUSTER_ADMIN_TOKENS": str(self.tokens),
                "CLUSTER_INTERNAL_NETWORKS": f"{self.ip}/32"}

    def start(self) -> None:
        world = self.root.parent / "admin-world"
        world.mkdir(parents=True, exist_ok=True)
        (world / "Sandbox.sbc").write_text("<MyObjectBuilder_Checkpoint />")
        command = ["dotnet", str(HOST.package / "registry/ClusterRegistry.dll"),
                   "--direct", "--listen", f"127.0.0.1:{LISTEN_PORT}", "--backend", f"127.0.0.1:{BACKEND_PORT}",
                   "--control", f"{self.ip}:{CONTROL_PORT}", "--registry", str(self.root / "registry")]
        log = open(self.root.parent / "admin-gateway.log", "ab")
        self.process = subprocess.Popen(command, env=self.environment(), stdout=log, stderr=log,
                                        start_new_session=True, cwd=self.root)
        stop = time.time() + 60
        while time.time() < stop:
            try:
                if self.request("/health")[0] == 200:
                    return
            except OSError:
                pass
            if self.process.poll() is not None:
                raise RuntimeError(f"the Gateway exited with {self.process.returncode}; see admin-gateway.log")
            time.sleep(0.3)
        raise RuntimeError("the Gateway did not answer /admin/v1/health within 60 s")

    def sigkill(self) -> None:
        if self.process is None:
            return
        try:
            os.killpg(os.getpgid(self.process.pid), signal.SIGKILL)
        except ProcessLookupError:
            pass
        self.process.wait()
        stop = time.time() + 15
        while time.time() < stop:
            try:
                self.request("/health")
                time.sleep(0.2)
            except OSError:
                return
        raise RuntimeError("the Gateway control port stayed open after SIGKILL")

    def request(self, path: str, method: str = "GET", token: str | None = MANAGE_TOKEN,
                key: str | None = None, body: dict | None = None) -> tuple[int, dict, dict]:
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method)
        if token:
            req.add_header("Authorization", "Bearer " + token)
        if key:
            req.add_header("Idempotency-Key", key)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=30) as response:
                return response.status, dict(response.headers), json.loads(response.read())
        except urllib.error.HTTPError as error:
            payload = error.read()
            return error.code, dict(error.headers), json.loads(payload) if payload else {}

    def cli(self, *arguments: str) -> tuple[int, str]:
        result = subprocess.run([str(HOST.package / "cli/gateway-admin"), "--url", f"http://{self.ip}:{CONTROL_PORT}",
                                 *arguments], env={**os.environ, "CLUSTER_ADMIN_TOKEN": MANAGE_TOKEN},
                                capture_output=True, text=True, timeout=120)
        return result.returncode, result.stdout


def _with_gateway(bench, body) -> None:
    gateway = AdminGateway(bench)
    try:
        gateway.start()
        body(bench, gateway)
    finally:
        gateway.sigkill()


def p3_adm_01(bench) -> None:
    def body(bench, gateway: AdminGateway) -> None:
        selftest = subprocess.run(["dotnet", str(HOST.package / "cli/ClusterRegistry.Cli/ClusterRegistry.Cli.dll"),
                                   "--self-test"], capture_output=True, text=True, timeout=300, cwd=HOST.package)
        bench.record("cli-self-test.txt", f"exit {selftest.returncode}\n{selftest.stdout}\n{selftest.stderr}")
        bench.check("the admin CLI self-test passes (static CLI <-> operation bijection)",
                    selftest.returncode == 0, (selftest.stdout or selftest.stderr).strip()[-200:])
        # Reads whose data is stable between two calls (status carries live timings).
        for command, route in (("config", "/config"), ("nodes", "/nodes"), ("bans", "/admission/bans")):
            code, stdout = gateway.cli(command)
            _, _, http = gateway.request(route)
            cli_data = json.loads(stdout).get("data") if code == 0 and stdout.strip() else None
            bench.check(f"`gateway-admin {command}` returns the same data as GET {route}",
                        code == 0 and cli_data == http.get("data"), f"exit {code}")
        status, _, first = gateway.request("/save-all", "POST", key="parity-1", body={})
        code, stdout = gateway.cli("save-all", "--key", "parity-1")
        replay = json.loads(stdout)["data"]["operationId"] if code == 0 else None
        bench.check("the CLI replays the HTTP operation under the same idempotency key",
                    status in (200, 202) and replay == first["data"]["operationId"],
                    f"http {status} {first.get('data', {}).get('operationId')} cli {replay}")
        bench.exercised(gateway=str(HOST.package / "registry/ClusterRegistry.dll"), cli="cli/gateway-admin")
    _with_gateway(bench, body)


def p3_adm_02(bench) -> None:
    def body(bench, gateway: AdminGateway) -> None:
        bad = []
        for route in READ_ROUTES:
            status, headers, payload = gateway.request(route)
            if status != 200 or headers.get("X-Cluster-Gateway-Protocol") != "1" or payload.get("protocolVersion") != 1:
                bad.append(f"{route}={status}")
        bench.check("every read route answers 200 with the protocol header and envelope", not bad, ", ".join(bad))
        status, _, _ = gateway.request("/status", token=None)
        bench.check("a read without a token is 401 on a non-loopback control plane", status == 401, str(status))
        denied = []
        for route in READ_ROUTES:
            status, _, _ = gateway.request(route, token=QUERY_TOKEN)
            if status != 200:
                denied.append(f"{route}={status}")
        bench.check("a query-scope token reads every route", not denied, ", ".join(denied))
        refused = []
        mutations = (("/save-all", "POST", {}),
                     ("/config", "PUT", {"expectedRevision": 0, "slots": [
                         {"slotKey": "slot-1", "host": "host-1", "role": "Regular", "goal": "Wanted"}]}),
                     ("/gateway/restart", "POST", {}),
                     ("/admission/bans", "POST", {"userId": 76561190000000001}),
                     ("/triggers/balance", "POST", {}))
        for index, (route, method, request_body) in enumerate(mutations):
            status, _, payload = gateway.request(route, method, token=QUERY_TOKEN, key=f"scope-{index}", body=request_body)
            if status != 403 or payload.get("error", {}).get("code") != "authorization_denied":
                refused.append(f"{method} {route}={status} {payload.get('error')}")
        bench.check("a query-scope token is refused every mutation with 403 authorization_denied",
                    not refused, "; ".join(refused))
        status, _, done = gateway.request("/save-all", "POST", key="audit-1", body={})
        bench.check("a manage-scope token mutates", status in (200, 202), str(status))
        operation = (done.get("data") or {}).get("operationId")
        _, _, events = gateway.request("/events?limit=1000")
        text = json.dumps(events)
        bench.record("events", events)
        bench.check("the mutation is audited to /events", bool(operation) and str(operation) in text,
                    f"operation {operation}")
        leaked = [name for name, secret in (("manage", MANAGE_TOKEN), ("query", QUERY_TOKEN))
                  if secret in text or secret in (gateway.root.parent / "admin-gateway.log").read_text(errors="replace")]
        for route in READ_ROUTES:
            _, _, payload = gateway.request(route)
            body_text = json.dumps(payload)
            leaked += [f"{route}:{name}" for name, secret in (("manage", MANAGE_TOKEN), ("query", QUERY_TOKEN))
                       if secret in body_text]
        bench.check("no token value is echoed in any read, event or the Gateway log", not leaked, str(leaked))
    _with_gateway(bench, body)


def p3_adm_03(bench) -> None:
    def body(bench, gateway: AdminGateway) -> None:
        status, _, _ = gateway.request("/save-all", "POST", key=None, body={})
        bench.check("a mutation without Idempotency-Key is refused with 400", status == 400, str(status))
        status, _, first = gateway.request("/save-all", "POST", key="idem-1", body={})
        operation = first["data"]["operationId"]
        time.sleep(1)
        _, _, replay = gateway.request("/save-all", "POST", key="idem-1", body={})
        bench.check("the same key replays the original, completed operation",
                    replay["data"]["operationId"] == operation and replay["data"]["state"] == "Succeeded",
                    f"{replay['data'].get('operationId')} {replay['data'].get('state')}")
        status, _, conflict = gateway.request("/save-all", "POST", key="idem-1", body={"sweep": True})
        bench.check("a key reused for a different request is 409 idempotency_key_reuse",
                    status == 409 and conflict.get("error", {}).get("code") == "idempotency_key_reuse", str(status))
        _, _, config = gateway.request("/config")
        revision = config["data"]["policyRevision"]
        slots = [{"slotKey": "slot-1", "host": "host-1", "role": "Regular", "goal": "Wanted"}]
        status, _, applied = gateway.request("/config", "PUT", key="cfg-1", body={"expectedRevision": revision, "slots": slots})
        bench.check("a config write at the current revision succeeds",
                    status == 200 and applied["data"]["state"] == "Succeeded", str(applied.get("data")))
        _, _, stale = gateway.request("/config", "PUT", key="cfg-2", body={"expectedRevision": revision, "slots": slots})
        bench.check("a config write at a stale revision fails with revision_mismatch",
                    stale["data"]["state"] == "Failed" and stale["data"]["error"]["code"] == "revision_mismatch",
                    str(stale.get("data")))
        _, _, events = gateway.request("/events?limit=1000")
        cursor = events["data"]["nextCursor"]
        sequence = [event["seq"] for event in events["data"]["events"]]
        bench.check("the event sequence is strictly ordered and non-empty",
                    cursor > 0 and sequence == sorted(sequence) and len(set(sequence)) == len(sequence),
                    f"cursor {cursor}, {len(sequence)} events")

        # SIGKILL loses at most one persist interval by design (StateDurability.md); what must
        # survive is everything flushed before the kill.
        time.sleep(PERSIST_INTERVAL * 3 + 1)
        gateway.sigkill()
        gateway.start()
        status, _, after = gateway.request("/save-all", "POST", key="idem-1", body={})
        bench.check("after SIGKILL and a warm restart the key still replays the original operation",
                    status == 200 and after["data"]["operationId"] == operation and after["data"]["state"] == "Succeeded",
                    f"{status} {after.get('data')}")
        _, _, tail = gateway.request(f"/events?cursor={cursor}&limit=1000")
        fresh = [event["seq"] for event in tail["data"]["events"]]
        bench.check("the /events cursor resumes across the restart without replaying old events",
                    all(seq > cursor for seq in fresh), f"{len(fresh)} new events after {cursor}")
        status, _, durable = gateway.request(f"/operations/{operation}")
        bench.check("the durable operation record survives the restart",
                    status == 200 and durable["data"]["idempotencyKey"] == "idem-1", str(status))
        bench.exercised(restart="SIGKILL + warm restart of the Gateway")
    _with_gateway(bench, body)


SCENARIOS_ADMIN = {
    "P3-ADM-01": (p3_adm_01, "admin API parity: CLI self-test and live CLI-vs-HTTP equality", "2"),
    "P3-ADM-02": (p3_adm_02, "admin API scoped tokens, audit and redaction", "2"),
    "P3-ADM-03": (p3_adm_03, "admin API idempotent, durable operations across a Gateway SIGKILL", "2"),
}
