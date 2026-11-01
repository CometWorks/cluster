"""cluster-hostd - the per-host launcher of a multi-host cluster (T-0321, PLAN.md §1).

One agent per host (the Gateway host included), started by `cluster start` over ssh. It holds the host's
fenced executor lease on the registry (``POST /executor/v1/heartbeat``, ClusterRegistry/ExecutorApi.cs:
the contract Quasar.Host's NodeActualizer speaks too) with the host-scoped Executor token this start minted,
and carries out the host's slots of the node plan with the T-0320 launcher logic (Cluster.actualize):
spawn Wanted slots, stop Kill slots (self-exit grace, then SIGKILL), never restart a registered id, refuse a
launch below the host's RAM/disk floor, seed each new node's cfg from the deployment template and its hub
snapshot from the host (CLUSTER_PREWARM_SOURCES), and set CLUSTER_NODE_HOST.

Verbs (``cluster --root R hostd <verb>``):
    run    --host H [--fresh]   the agent, in the foreground (``start`` runs it detached)
    start  --host H [--fresh]   detach ``run`` (setsid, log <root>/hostd.log, pid <root>/hostd.pid)
    stop                        SIGTERM the agent of this run root (its nodes keep running)
    status                      JSON: pid, alive, last poll, lease, plan size
    kill-nodes                  SIGKILL every node process of this run root on this host
    local-stop [--budget S]     ask every node of this run root on this host for a local stop (T-0347)
    sweep [--apply] ...         T-0340 step 7: move stopped nodes' legacy saves into the host store, delete stopped
                                dirs; a dry run (the default) reports counts and bytes and touches nothing
    disk                        JSON: this host's store bytes, legacy bytes left in node dirs, free disk

Files under the host's run root: ``hostd/hosts.json`` and ``hostd/launch.json`` (written by the start on the
Gateway host: the hosts file and the node launch settings), ``secrets/`` (0600 tokens), ``launcher.lock``
(one launcher per host, shared with T-0320's launcher), ``hostd.pid``, ``hostd-status.json``.

Failure handling:
- Lease lost (409 "expired or belongs to another Gateway/session"): acquire a new lease; nothing is acted on
  until the registry answers again.
- Another session owns the host (409 on acquisition, e.g. a second agent, or the previous agent's lease still
  running after a restart): retried until that lease expires (60 s), then taken.
- A refused observation (409) fails the whole batch; the batch is resent one report at a time and the refused
  ones are logged and dropped (they are re-observed on the next step).
- Credential refused (403): this agent belongs to an earlier start (tokens are minted anew at every start);
  it exits 3 without touching its nodes.
- No successful poll for PLAN_FRESH_SECONDS: no launch and no kill happens on a stale plan.
"""
from __future__ import annotations

import dataclasses
import fcntl
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime
from pathlib import Path

import cluster as cl

HEARTBEAT_ROUTE = "/executor/v1/heartbeat"
LEASE_SECONDS = 60                    # Admin.ExecutorProtocol.LeaseSeconds
MAX_REPORTS = 256                     # Admin.ExecutorProtocol.MaxReports
INTERVAL = float(os.environ.get("CLUSTER_HOSTD_INTERVAL", "2"))
#: Act only on a plan this fresh: half the lease, so a partitioned agent stops acting well before the
#: registry treats its lease as gone.
PLAN_FRESH_SECONDS = LEASE_SECONDS / 2
#: The first node on a host starts alone (it fills the host's shared plugin caches, as the WA does on the
#: Gateway host, T-0031); the rest follow once it is Ready or after this long.
WARMUP_SECONDS = float(os.environ.get("CLUSTER_HOSTD_WARMUP_SECONDS", str(cl.SERIAL_WARMUP_TIMEOUT)))
#: os.environ keys the launch spec never carries from the Gateway host: secrets, and paths each host's
#: serverEnv / cli/cluster wrapper sets for itself.
LAUNCH_ENV_PREFIXES = ("CLUSTER_", "MAGNETAR_", "DOTNET_", "SE_", "DIRECT_TRANSPORT_")
LAUNCH_ENV_DENY = frozenset({
    "CLUSTER_ADMIN_TOKEN", "CLUSTER_ADMIN_TOKENS", "CLUSTER_JOIN_TOKEN", "CLUSTER_JOIN_TOKEN_CURRENT",
    "CLUSTER_JOIN_TOKEN_PREVIOUS", "CLUSTER_ROOT", "CLUSTER_ARCHIVE", "CLUSTER_HUB_PLUGINS", "CLUSTER_PREWARM_SOURCES",
    "CLUSTER_PLUGIN_BINARIES", "CLUSTER_PLUGIN_SOURCE", "CLUSTER_WORLD_TEMPLATE", "CLUSTER_HOSTS_FILE",
    "CLUSTER_NODE_HOST", "CLUSTER_NODE_DEDICATED_TEMPLATE", "CLUSTER_NODE_DEDICATED_REQUIRE", "CLUSTER_COMMON_PLUGINS",
    "CLUSTER_EXTRA_PLUGINS", "MAGNETAR_HOME", "MAGNETAR_GITHUB_CACHE", "MAGNETAR_GATEWAY_DLL", "MAGNETAR_WORLD_TOOL",
    "MAGNETAR_GATEWAY_RS", "DOTNET_ROOT", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "DIRECT_TRANSPORT_SOURCE",
    "DIRECT_TRANSPORT_BINARIES", "CLUSTER_WORLD", "CLUSTER_DATA_ROOT", "CLUSTER_SHARED_ROOT",
})


class StaleCredential(cl.ClusterError):
    """The registry refused this agent's executor token: a newer start minted new ones."""


class ExecutorClient:
    """The fenced executor heartbeat of one host: session, lease, sequence and replay (ExecutorApi.cs)."""

    def __init__(self, gateway: str, host: str, token: str, executor: str | None = None, log=print,
                 request=None) -> None:
        self.gateway = gateway.rstrip("/")
        self.host = host
        self.token = token
        self.executor = executor or f"cluster-hostd@{host}"
        self.session = str(uuid.uuid4())
        self.lease: str | None = None
        self.expires: str | None = None
        self.sequence = 0
        self.plan: list[dict] = []
        self.polled_at = 0.0           # monotonic time of the last successful poll
        self.log = log
        self._request = request or self._http

    def _http(self, body: dict) -> tuple[int, dict]:
        request = urllib.request.Request(self.gateway + HEARTBEAT_ROUTE, method="POST", data=json.dumps(body).encode())
        request.add_header("Content-Type", "application/json")
        request.add_header("Authorization", "Bearer " + self.token)
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                return response.status, json.loads(response.read().decode() or "{}")
        except urllib.error.HTTPError as error:
            text = error.read().decode(errors="replace")
            try:
                return error.code, json.loads(text)
            except ValueError:
                return error.code, {"error": {"code": "http", "message": text[:300]}}

    def _send(self, reports: list[dict]) -> tuple[int, dict]:
        """One poll with a new sequence number; a transport failure resends the SAME body (same sequence:
        the registry accepts an identical replay and answers it again)."""
        self.sequence += 1
        body = {"host": self.host, "executor": self.executor, "session": self.session, "lease": self.lease,
                "sequence": self.sequence, "reports": reports, "deploymentRevision": None}
        last: Exception | None = None
        for attempt in range(3):
            try:
                return self._request(body)
            except (OSError, urllib.error.URLError, ValueError) as error:
                last = error
                time.sleep(0.5 * (attempt + 1))
        raise cl.ClusterError(f"executor heartbeat to {self.gateway}: {last}")

    def _accept(self, answer: dict) -> None:
        data = answer.get("data", answer) if isinstance(answer, dict) else {}
        self.lease = data.get("lease") or self.lease
        self.expires = data.get("expiresAt")
        self.plan = list(data.get("plan") or [])
        self.polled_at = time.monotonic()

    @staticmethod
    def _message(answer: dict) -> str:
        error = (answer or {}).get("error") or {}
        return f"{error.get('code', '?')}: {error.get('message', '')}"

    def poll(self, reports: list[dict] | None = None) -> list[dict]:
        """Acquire the lease if needed, deliver ``reports``, and return this host's plan slots. Refused
        reports are dropped after a one-at-a-time retry; returns the reports the registry refused."""
        reports = list(reports or [])[:MAX_REPORTS]
        if self.lease is None:
            status, answer = self._send([])
            if status == 403:
                raise StaleCredential(f"executor credential refused: {self._message(answer)}")
            if status != 200:
                raise cl.ClusterError(f"executor lease not acquired ({status} {self._message(answer)})")
            self._accept(answer)
            self.log(f"[hostd] executor lease acquired (session {self.session[:8]})")
        if not reports:
            status, answer = self._send([])
            return self._settle(status, answer, [])
        status, answer = self._send(reports)
        if status == 200:
            self._accept(answer)
            return []
        refused = self._settle(status, answer, reports)
        return refused

    def _settle(self, status: int, answer: dict, reports: list[dict]) -> list[dict]:
        if status == 200:
            self._accept(answer)
            return []
        message = self._message(answer)
        if status == 403:
            raise StaleCredential(f"executor credential refused: {message}")
        if status == 409 and re.search(r"lease expired|another gateway|another executor session", message, re.I):
            self.log(f"[hostd] executor lease lost ({message}); acquiring a new one")
            self.lease = None
            return reports
        if status == 409 and reports:
            refused = []
            for report in reports:
                single, reply = self._send([report])
                if single == 200:
                    self._accept(reply)
                    continue
                if single == 409 and re.search(r"lease expired|another", self._message(reply), re.I):
                    self.lease = None
                    return refused + [report]
                refused.append(report)
                self.log(f"[hostd] observation {report['state']} of {report['slotKey']} refused: {self._message(reply)}")
            return refused
        raise cl.ClusterError(f"executor heartbeat answered {status} {message}")


def executor_report(observation: dict) -> dict:
    """A launcher observation (Cluster.actualize) as an ExecutorNodeReport (AdminContract/ExecutorContract.cs)."""
    state = observation["state"]
    epoch = int(observation.get("epoch") or 0)
    if state == "Ready" and epoch <= 0:
        state = "Spawning"           # Ready needs a live registry incarnation (epoch > 0)
    failure = observation.get("failure")
    return {"slotKey": observation["slotKey"], "attemptKey": str(observation["attemptKey"])[:128], "state": state,
            "node": observation.get("node"), "epoch": epoch, "failure": failure[:2048] if failure else None}


def dedupe(reports: list[dict]) -> list[dict]:
    """One report per slot (the registry refuses a batch naming a slot twice); the last one wins."""
    by_slot: dict[str, dict] = {}
    for report in reports:
        by_slot[report["slotKey"]] = report
    return list(by_slot.values())


class HostAgent:
    """The agent loop on one host."""

    def __init__(self, cluster: cl.Cluster, client: ExecutorClient, log=print, interval: float = INTERVAL) -> None:
        self.cluster = cluster
        self.client = client
        self.log = log
        self.interval = interval
        self.stop = threading.Event()
        self.warm = False
        self.warm_since: float | None = None
        self.last_error: str | None = None
        self.steps = 0
        #: T-0340 step 7: the registry's host-store mode, from the status each step reads anyway (StoreKeeper acts on it).
        self.store_mode = False
        self.keeper: StoreKeeper | None = None

    def budget(self, summary_before: dict | None = None) -> int | None:
        """Per-host serial warm-up: one launch until the first node of this agent is Ready."""
        if self.warm:
            return None
        if self.warm_since is not None and time.monotonic() - self.warm_since > WARMUP_SECONDS:
            self.log(f"[hostd] warm-up node not Ready after {WARMUP_SECONDS:.0f}s; launching the rest anyway")
            self.warm = True
            return None
        return 0 if self.warm_since is not None else 1

    def step(self) -> dict:
        """One poll + one launcher step. The plan acted on is the one the previous poll returned (at most
        one interval old); nothing is acted on when that plan is older than PLAN_FRESH_SECONDS."""
        # Twice at most: a refresh answered 409 lease-lost clears the lease WITHOUT raising (T-0296 r2) - the second
        # poll acquires a new lease and its plan; acting on the old plan there launched from a stale plan.
        for _ in range(2):
            if self.client.lease is not None and time.monotonic() - self.client.polled_at <= PLAN_FRESH_SECONDS:
                break
            self.client.poll([])
        else:
            if self.client.lease is None or time.monotonic() - self.client.polled_at > PLAN_FRESH_SECONDS:
                raise cl.ClusterError("no fresh plan from the registry; nothing acted on")
        status = self.cluster.get("/registry/status", timeout=15)
        rows = cl._field(status, "nodes") or []
        self.store_mode = bool(cl._field(cl._field(status, "hostStore"), "enabled"))
        summary = self.cluster.actualize(self.client.plan, rows, spawn=True, spawn_budget=self.budget(), fenced=True)
        if summary["spawned"] and not self.warm and self.warm_since is None:
            self.warm_since = time.monotonic()
        if any(o["state"] == "Ready" and int(o.get("epoch") or 0) > 0 for o in summary["observations"]):
            if not self.warm:
                self.log("[hostd] first node Ready: the host is warm")
            self.warm = True
        refused = self.client.poll(dedupe([executor_report(o) for o in summary["observations"]]))
        summary["refusedReports"] = refused
        self.steps += 1
        return summary

    def write_status(self, extra: dict | None = None) -> None:
        status = {"pid": os.getpid(), "host": self.client.host, "session": self.client.session,
                  "lease": bool(self.client.lease), "leaseExpires": self.client.expires,
                  "lastPollAgoSeconds": round(time.monotonic() - self.client.polled_at, 1) if self.client.polled_at else None,
                  "planSlots": len(self.client.plan), "warm": self.warm, "steps": self.steps,
                  "lastError": self.last_error, "updated": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                  **({"store": self.keeper.status} if self.keeper is not None and self.keeper.status else {}), **(extra or {})}
        path = self.cluster.config.root / "hostd-status.json"
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(status, indent=1) + "\n")
        tmp.replace(path)

    def run(self) -> int:
        errors_in_row = 0
        while not self.stop.is_set():
            try:
                summary = self.step()
                if summary["spawned"] or summary["stopped"] or summary["refused"]:
                    self.log(f"[hostd] step: started {summary['spawned'] or '-'} stopped {summary['stopped'] or '-'}"
                             f" refused {summary['refused'] or '-'}")
                if errors_in_row:
                    self.log(f"[hostd] registry reachable again after {errors_in_row} failed step(s)")
                errors_in_row, self.last_error = 0, None
            except StaleCredential as error:
                self.log(f"[hostd] {error}: this agent belongs to an earlier start; exiting (nodes left alone)")
                self.last_error = str(error)
                self.write_status({"exited": "stale credential"})
                return 3
            except (cl.ClusterError, OSError, ValueError) as error:
                errors_in_row += 1
                self.last_error = str(error)
                if errors_in_row in (1, 10) or errors_in_row % 60 == 0:
                    self.log(f"[hostd] step failed ({errors_in_row} in a row): {error}")
            try:
                self.write_status()
            except OSError:
                pass
            self.stop.wait(self.interval)
        return 0


# ------------------------------------------------------------------------------------------------ setup

def load_launch(root: Path) -> dict:
    path = root / "hostd" / "launch.json"
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise cl.ClusterError(f"{path}: {error} (written by `cluster start` on the gateway host)") from None


def build_cluster(root: Path, host: str, launch: dict) -> cl.Cluster:
    """This host's Cluster from the launch spec: the node environment and cfg template of the start."""
    for key, value in (launch.get("env") or {}).items():
        if key.startswith(LAUNCH_ENV_PREFIXES) and key not in LAUNCH_ENV_DENY:
            os.environ[key] = str(value)
    os.environ["CLUSTER_NODE_HOST"] = host
    template = None
    if launch.get("dedicatedTemplate"):
        template = root / "hostd" / "node-template.cfg"
        template.parent.mkdir(parents=True, exist_ok=True)
        if not template.is_file() or template.read_text() != launch["dedicatedTemplate"]:
            template.write_text(launch["dedicatedTemplate"])
    config = cl.Config(
        root=root, host=host, hosts_file=root / "hostd" / "hosts.json",
        node_env=dict(launch.get("nodeEnv") or {}), node_env_by_id=dict(launch.get("nodeEnvById") or {}),
        dedicated_template=template, dedicated_require=dict(launch.get("dedicatedRequire") or {}),
        sync_distance=int(launch.get("syncDistance") or 3000), server_name=launch.get("serverName") or "Magnetar Cluster Test",
        member_limit=int(launch.get("memberLimit") or 16), administrators=list(launch.get("administrators") or []),
        world_template=Path(launch.get("worldName") or "Cluster-Simple"),
        lease_ttl_seconds=int(launch.get("leaseTtlSeconds") or 15), regular_nodes=1, spare_nodes=0)
    if not config.multi_host:
        raise cl.ClusterError(f"{config.hosts_file} is not a multi-host (version 2) hosts file naming {host}")
    config.host_entry(host)
    if not config.from_node_set:
        # No ledger yet on this host: no positional node-1/wa-1 placeholders, the plan names every node.
        config.nodes, config.retired_nodes = [], []
    return cl.Cluster(config)


def pid_alive(pid: int, root: Path) -> bool:
    try:
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
    except OSError:
        return False
    return " hostd run" in cmdline and str(root) in cmdline


def read_pid(root: Path) -> int | None:
    try:
        return int((root / "hostd.pid").read_text().split()[0])
    except (OSError, ValueError, IndexError):
        return None


def run(root: Path, host: str, fresh: bool = False, log=print) -> int:
    launch = load_launch(root)
    cluster = build_cluster(root, host, launch)
    try:
        cluster.acquire_launcher_lock()
    except cl.ClusterError as error:
        log(f"[hostd] {error}: one launcher per host")
        return 4
    (root / "hostd.pid").write_text(f"{os.getpid()}\n")
    token_path = cluster.config.executor_token_path(host)
    try:
        token = token_path.read_text().strip()
    except OSError as error:
        log(f"[hostd] executor token {token_path}: {error.strerror}")
        return 2
    if fresh:
        # A start: every node id of the previous run is gone for good (fresh ids at every start); the
        # start checked that none of their processes still runs on this host.
        for spec in [node for node in cluster.config.nodes if node.role == "regular"]:
            cluster._retire_spec(spec)
        cluster.config.save_node_set()
    gateway = launch.get("gateway") or f"http://{cluster.config.gateway_bind}"
    client = ExecutorClient(gateway, host, token, log=log)
    agent = HostAgent(cluster, client, log=log)
    agent.keeper = StoreKeeper(agent, root, host)
    agent.keeper.start()
    signal.signal(signal.SIGTERM, lambda *_: agent.stop.set())
    signal.signal(signal.SIGINT, lambda *_: agent.stop.set())
    log(f"[hostd] {host}: pid {os.getpid()}, run root {root}, registry {gateway}, address {cluster.config.node_address},"
        f" port base {cluster.config.node_port_base}")
    code = agent.run()
    log(f"[hostd] {host}: stopping (code {code}); node processes are left running")
    try:
        if read_pid(root) == os.getpid():
            (root / "hostd.pid").unlink()
    except OSError:
        pass
    cluster.release_launcher_lock()
    return code


def start(root: Path, host: str, fresh: bool = False) -> int:
    """Detach `hostd run`: refuses while another agent of this run root runs on this host."""
    pid = read_pid(root)
    if pid and pid_alive(pid, root):
        print(f"cluster-hostd already runs on {host} (pid {pid})", file=sys.stderr)
        return 4
    log_path = root / "hostd.log"
    root.mkdir(parents=True, exist_ok=True)
    if log_path.exists() and log_path.stat().st_size > 0:
        log_path.rename(root / f"hostd.log.{time.strftime('%Y%m%d-%H%M%S')}")
        for stale in sorted(root.glob("hostd.log.*"))[:-10]:
            stale.unlink()
    argv = [sys.executable, str(Path(cl.__file__).resolve()), "--root", str(root), "hostd", "run", "--host", host] \
        + (["--fresh"] if fresh else [])
    with open(log_path, "ab", buffering=0) as log:
        process = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                   start_new_session=True, env={**os.environ, "CLUSTER_NODE_HOST": host})
    deadline = time.time() + 10
    while time.time() < deadline:
        if process.poll() is not None:
            tail = log_path.read_text(errors="replace").splitlines()[-3:]
            print(f"cluster-hostd on {host} exited with {process.returncode}: {' | '.join(tail)}", file=sys.stderr)
            return process.returncode or 1
        if read_pid(root) == process.pid:
            print(json.dumps({"host": host, "pid": process.pid, "log": str(log_path)}))
            return 0
        time.sleep(0.2)
    print(f"cluster-hostd on {host} (pid {process.pid}) wrote no pid file within 10 s", file=sys.stderr)
    return 1


def stop(root: Path, timeout: float = 15) -> int:
    pid = read_pid(root)
    if not pid or not pid_alive(pid, root):
        print(json.dumps({"stopped": None}))
        return 0
    os.kill(pid, signal.SIGTERM)
    deadline = time.time() + timeout
    while time.time() < deadline and pid_alive(pid, root):
        time.sleep(0.2)
    if pid_alive(pid, root):
        os.kill(pid, signal.SIGKILL)
    print(json.dumps({"stopped": pid}))
    return 0


def status(root: Path) -> dict:
    pid = read_pid(root)
    out = {"pid": pid, "alive": bool(pid and pid_alive(pid, root))}
    try:
        out.update(status=json.loads((root / "hostd-status.json").read_text()))
    except (OSError, ValueError):
        pass
    return out


def kill_nodes(root: Path) -> list[int]:
    """Every node process of this run root on this host (discovery by command line, kill by pid)."""
    found = subprocess.run(["pgrep", "-f", f"MagnetarInterim.*{re.escape(str(root))}/"], capture_output=True, text=True)
    pids = sorted({int(pid) for pid in found.stdout.split()})
    for pid in pids:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    return pids


# ------------------------------------------------------------------------------------------------ host save store
# T-0340 step 7: legacy save files of stopped nodes move into the host's store, stopped dirs go, a disk report.
#
# A world from before store mode keeps its saves in node data dirs (<root>/<node>-data/cluster/saves/...), node-held.
# On r113 those pinned every stopped node's registry row (265k archived saves, ~10 GB, many only in stopped dirs). The
# sweep below moves ONLY the files the catalogue names at that node (class rekey), per file and crash-safe:
#   1. ask the registry what each legacy file of a stopped dir is (POST /registry/host-store/adopt, commit false);
#   2. link(2) a rekey file into <root>/saves/<host>/ under the same relative name - same file system, so no byte is
#      copied, and never over an existing file (an existing one must be the same inode, or byte-equal to the source and
#      carry the catalogued size and checksum); fsync the store directories touched;
#   3. commit: the registry re-keys that node's location to host:<host> (in memory, or the archived row in place) and
#      writes it durably before it answers;
#   4. unlink the source ONLY for an answer of rekeyed, or hostHeld with the store file being this very inode (a move
#      an earlier sweep committed: class resume).
# Files no catalogue row names at this node (decision 2026-10-04 08:40) are NOT moved: class unnamed (the save is
# catalogued, but not this copy: superseded or dropped), uncatalogued (no row in memory or in the archive: a save
# retention deleted after this node stopped, or one never registered) and hostHeldCopy (the host's store holds the
# save as another file). They stay in their dir and go only WITH the dir, under --delete-unnamed (see _delete_dirs).
# A file a pending unregistered-finals journal names (T-0347: a Final of a local stop that the next start still has
# to report) is class lateFinal: never moved, never deleted, and its dir stays. Anything else (live node, size or
# checksum mismatch, wrong host, corrupt, pending delete, busy (#1276: a recovery claim or a grant still names it; a
# later sweep moves it), a conflicting store file, an unreadable file, a .mps outside
# the save layout) stays where it is, counted under kept.<reason>, and keeps its dir.
# An interrupted sweep resumes: an existing link is the same inode, a committed file answers hostHeld.
# Dry run unless apply: it reports every class with files, bytes and dirs, and writes, links and deletes nothing.

UNNAMED = ("unnamed", "uncatalogued", "hostHeldCopy")
PENDING_JOURNAL = "unregistered-finals.jsonl"     # ClusterProtocol.UnregisteredFinals.FileName (T-0347)
NODE_DIR = re.compile(r"((?:node|wa)-\d+)-data")
PARTITION_DIR = re.compile(r"P-([0-9A-F]{16})")
SAVE_NAME = re.compile(r"g([0-9A-F]{8})-s([0-9A-F]{8})\.mps")
LEFTOVER = re.compile(r"\..*\.tmp|.*\.part")
HEADER_LENGTH, TRAILER_LENGTH = 100, 16
SWEEP_SECONDS = float(os.environ.get("CLUSTER_HOSTD_SWEEP_SECONDS", "0"))            # 0: hostd never sweeps on its own
DISK_REPORT_SECONDS = float(os.environ.get("CLUSTER_HOSTD_DISK_REPORT_SECONDS", "900"))
KEEP_DIRS = int(os.environ.get("CLUSTER_STORE_KEEP_DIRS", "2"))
KEEP_HOURS = float(os.environ.get("CLUSTER_STORE_KEEP_HOURS", "24"))
#: A dir with unnamed files goes (with --delete-unnamed only) once nothing in it was written for this many days.
UNNAMED_DAYS = float(os.environ.get("CLUSTER_STORE_UNNAMED_DAYS", "7"))
#: Open question 1 (archive retention), default OFF: delete the files of partitions archived at least this many days
#: ago (counted from when this host's sweep first saw them archived); the registry keeps the rows.
ARCHIVE_RETENTION_DAYS = float(os.environ.get("CLUSTER_STORE_ARCHIVE_RETENTION_DAYS", "0"))
#: world-backup's completion marker: a committed part has it, a part still being made has PART_FILE instead.
BACKUP_MARKER, BACKUP_PART_FILE = "BACKUP.json", ".part.json"


def read_envelope(path: Path) -> tuple[int, int | None, int, int, str | None]:
    """(size, partition, generation, sequence, body checksum as X16) of an MPS1 save file, read from its header and
    trailer only; the checksum is None when the envelope is not intact (length, magic, version). The content hash is
    not recomputed (XXH3 is not in the Python standard library): same-inode moves keep every byte, a store copy of
    another inode is compared byte for byte, and every reader checksum-validates the file anyway."""
    with open(path, "rb") as stream:
        size = os.fstat(stream.fileno()).st_size
        header = stream.read(HEADER_LENGTH)
        if size < HEADER_LENGTH + TRAILER_LENGTH or len(header) < HEADER_LENGTH or header[:4] != b"MPS1" \
                or int.from_bytes(header[4:6], "little") != 1 or int.from_bytes(header[6:8], "little") != HEADER_LENGTH:
            return size, None, -1, -1, None
        stored = int.from_bytes(header[76:84], "little", signed=True)
        if HEADER_LENGTH + stored + TRAILER_LENGTH != size:
            return size, None, -1, -1, None
        stream.seek(size - 8)
        checksum = int.from_bytes(stream.read(8), "little")
    return (size, int.from_bytes(header[8:16], "little"), int.from_bytes(header[16:20], "little"),
            int.from_bytes(header[20:24], "little"), f"{checksum:016X}")


@dataclasses.dataclass
class LegacyFile:
    node: str
    path: Path
    relative: Path                 # below cluster/saves: P-<X16>/g-s.mps or GLOBAL/g-s.mps
    partition: int
    generation: int
    sequence: int
    is_global: bool
    size: int
    checksum: str | None

    def wire(self) -> dict:
        return {"node": self.node, "partition": self.partition, "generation": self.generation, "sequence": self.sequence,
                "global": self.is_global, "sizeBytes": self.size, "checksum": self.checksum}


def store_root(root: Path, host: str) -> Path:
    if not host or host in (".", "..") or any(c in host for c in "/\\:"):
        raise cl.ClusterError(f"host name {host!r} cannot name a save store directory")
    return root / "saves" / host


def scan_legacy(data: Path, node: str) -> tuple[list[LegacyFile], list[Path]]:
    """The save files under a node dir's cluster/saves, and its leftovers (temporary files, partial downloads)."""
    saves, files, leftovers = data / "cluster" / "saves", [], []
    if not saves.is_dir():
        return files, leftovers
    for directory, _, names in os.walk(saves):
        here = Path(directory)
        for name in names:
            path = here / name
            if LEFTOVER.fullmatch(name):
                leftovers.append(path)
                continue
            match, parent = SAVE_NAME.fullmatch(name), PARTITION_DIR.fullmatch(here.name)
            if match is None or here.parent != saves or (parent is None and here.name != "GLOBAL"):
                continue           # not a save file: it goes with the dir, never into the store
            try:
                size, partition, generation, sequence, checksum = read_envelope(path)
            except OSError:
                continue
            want = (int(match.group(1), 16), int(match.group(2), 16))
            if checksum is not None and ((generation, sequence) != want or parent is not None and partition != int(parent.group(1), 16)):
                checksum = None    # the name and the header disagree: not a file to vouch for
            files.append(LegacyFile(node, path, path.relative_to(saves), int(parent.group(1), 16) if parent else 0, want[0], want[1],
                                    parent is None, size, checksum))
    return sorted(files, key=lambda f: str(f.relative)), leftovers


def node_running(root: Path, node: str) -> bool:
    found = subprocess.run(["pgrep", "-f", f"MagnetarInterim.*{re.escape(str(root))}/{re.escape(node)}-data"], capture_output=True, text=True)
    return bool(found.stdout.split())


def same_save(source: Path, target: Path, size: int, checksum: str | None) -> bool:
    """Whether the file at ``target`` may stand for ``source``: the same inode, or the same bytes with the catalogued
    size and checksum."""
    try:
        a, b = os.stat(source), os.stat(target)
        if (a.st_dev, a.st_ino) == (b.st_dev, b.st_ino):
            return True
        if b.st_size != size or read_envelope(target)[4] != checksum:
            return False
        import filecmp
        return filecmp.cmp(source, target, shallow=False)
    except OSError:
        return False


class RegistryView:
    """What the sweep asks the registry, over HTTP with the admin token (every host gets it at the start)."""

    def __init__(self, gateway: str, token: str | None) -> None:
        self.gateway, self.token = gateway.rstrip("/"), token

    def _call(self, method: str, path: str, body: dict | None = None):
        request = urllib.request.Request(self.gateway + path, method=method, data=None if body is None else json.dumps(body).encode())
        request.add_header("Content-Type", "application/json")
        if self.token:
            request.add_header("Authorization", "Bearer " + self.token)
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return json.loads(response.read().decode() or "null")
        except urllib.error.HTTPError as error:
            raise cl.ClusterError(f"{method} {path} -> {error.code}: {error.read().decode(errors='replace')[:300]}") from None
        except (urllib.error.URLError, OSError) as error:
            raise cl.ClusterError(f"{method} {path}: {error}") from None

    def store_mode(self) -> bool:
        return bool(cl._field(cl._field(self._call("GET", "/registry/status"), "hostStore"), "enabled"))

    def nodes(self) -> tuple[set[str], set[str]]:
        """(ids the registry has a row for, ids that are live: Active/Closed rows and node control's live set)."""
        rows = self._call("GET", "/registry/nodes") or []
        ids = {str(cl._field(row, "node")) for row in rows}
        live = {str(cl._field(row, "node")) for row in rows if cl._enum(cl._field(row, "state"), ("Active", "Closed", "Dead", "Empty")) in ("Active", "Closed")}
        view = self._call("GET", "/admin/v1/node-control")
        view = cl._field(view, "data") or view
        live |= {str(cl._field(node, "node")) for node in (cl._field(view, "nodes") or []) + (cl._field(view, "notCounted") or [])}
        return ids, live

    def adopt(self, host: str, commit: bool, files: list[LegacyFile]) -> list[dict]:
        answer = self._call("POST", "/registry/host-store/adopt", {"host": host, "commit": commit, "files": [f.wire() for f in files]})
        return [{"status": cl._field(entry, "status"), "archived": bool(cl._field(entry, "archived")),
                 "sizeBytes": cl._field(entry, "sizeBytes"), "checksum": cl._field(entry, "checksum")} for entry in cl._field(answer, "files") or []]

    def archived_partitions(self) -> set[int]:
        return {int(p) for p in cl._field(self._call("GET", "/registry/host-store/archived-partitions"), "partitions") or []}


class OfflineRegistry:
    """The same questions answered from a COPY of registry.db, for a dry run on a copied run root with no registry
    running: the registry's adopt rules (HostStoreMove.cs) read from the stored rows. It never commits. Live = a row
    Active/Closed or a node in the stored node-control plan (the copy's own processes are not looked at)."""

    def __init__(self, db: Path) -> None:
        import sqlite3
        import tempfile
        wal = Path(str(db) + "-wal")
        self._temporary = None
        if wal.is_file() and wal.stat().st_size > 0:
            # Opening a WAL database writes its -shm beside it; work on a private copy instead of touching the original.
            self._temporary = tempfile.TemporaryDirectory(prefix="store-sweep-db-")
            copy = Path(self._temporary.name) / db.name
            shutil.copy2(db, copy)
            shutil.copy2(wal, Path(str(copy) + "-wal"))
            self.db = sqlite3.connect(copy)
        else:
            self.db = sqlite3.connect(f"file:{db}?immutable=1", uri=True)
        state = {key: json.loads(value) for key, value in self.db.execute("SELECT k, v FROM state") if value}
        self.rows = {str(key): row for key, row in (state.get("Nodes") or {}).items()}
        self.aliases = state.get("HostStoreAliases") or {}
        self.plan = {str(key): cl._field(record, "host") for key, record in (cl._field(state.get("NodeControl"), "live") or {}).items()}
        self.history: dict[str, str] = {}
        try:
            for (value,) in self.db.execute("SELECT v FROM NodeHistory ORDER BY rowid"):
                record = json.loads(value)
                self.history[str(record.get("Node"))] = record.get("Host")
        except sqlite3.OperationalError:
            pass               # a registry from before node control has no history table
        self.saves = {key: value for key, value in self.db.execute("SELECT k, v FROM Saves")}
        self.globals = {key: value for key, value in self.db.execute("SELECT k, v FROM GlobalSaves")}
        self.store_mode_flag = bool(self.aliases) or any(str(node).startswith("host:") for node in self.rows)

    def store_mode(self) -> bool:
        return self.store_mode_flag

    def nodes(self) -> tuple[set[str], set[str]]:
        live = {node for node, row in self.rows.items()
                if cl._enum(cl._field(row, "state"), ("Active", "Closed", "Dead", "Empty")) in ("Active", "Closed")}
        return set(self.rows), live | set(self.plan)

    def archived_partitions(self) -> set[int]:
        return {int(p) for (p,) in self.db.execute("SELECT DISTINCT p FROM ArchivedSaves")}

    def adopt(self, host: str, commit: bool, files: list[LegacyFile]) -> list[dict]:
        if commit:
            raise cl.ClusterError("an offline registry copy answers questions only; nothing is committed")
        _, live = self.nodes()
        return [self._one(host, f, live) for f in files]

    def _known_host(self, node: str) -> str | None:
        row = self.rows.get(node) or {}
        return self.aliases.get(node) or self.plan.get(node) or self.history.get(node) or cl._field(row, "host")

    def _one(self, host: str, f: LegacyFile, live: set[str]) -> dict:
        if not f.node or f.node.startswith("host:"):
            return {"status": "invalid", "archived": False}
        if f.node in live:
            return {"status": "live", "archived": False}
        archived = False
        raw = (self.globals if f.is_global else self.saves).get(f"{f.generation}:{f.sequence}" if f.is_global
                                                                 else f"{f.partition}:{f.generation}:{f.sequence}")
        if raw is None and not f.is_global:
            hit = self.db.execute("SELECT v FROM ArchivedSaves WHERE k = ? AND p = ?",
                                  (f"{f.partition}:{f.generation}:{f.sequence}", f.partition)).fetchone()
            raw, archived = (hit[0], True) if hit else (None, False)
        if raw is None:
            return {"status": "uncatalogued", "archived": False}
        save = json.loads(raw)
        writer, holders = save.get("Node"), dict(save.get("LocationHolders") or {})
        mine = [location for location in save.get("Locations") or [] if holders.get(location, writer) == f.node]
        catalogue = {"archived": archived, "sizeBytes": save.get("SizeBytes"), "checksum": save.get("Checksum")}
        if not mine:
            held = any(holder == f"host:{host}" for holder in holders.values())
            return {"status": "hostHeld" if held else "unnamed", **catalogue}
        known = self._known_host(f.node)
        status = ("pendingDelete" if save.get("PendingDelete")
                  else "wrongHost" if known and known.lower() != host.lower()
                  else "mismatch" if f.size != save.get("SizeBytes") or (f.checksum or "").lower() != str(save.get("Checksum")).lower()
                  else "corrupt" if {f.node, f"host:{host}"} & set(save.get("CorruptHolders") or [])
                  else "adopt")
        return {"status": status, **catalogue}


class BackupRefused(cl.ClusterError):
    """The backup named for a deletion run does not cover what it would delete."""


def stamp(seconds: float | None) -> str | None:
    return None if seconds is None else datetime.fromtimestamp(seconds).astimezone().isoformat(timespec="seconds")


def backup_marker(backup: Path, root: Path, host: str) -> dict:
    """The completion marker of one world-backup part (cluster-regression bin/world-backup: <dest>/<name>/BACKUP.json is
    written by commit_part only, after the part's .mps count matched the run root's, and the .partial dir renamed; a
    multi-host set commits a part on every host only once every host's part is made). Raises BackupRefused unless
    ``backup`` is such a committed part of THIS run root on THIS host. Returns the marker with its creation time."""
    if not backup.is_dir():
        raise BackupRefused(f"backup {backup}: not a directory")
    if backup.name.endswith(".partial") or (backup / BACKUP_PART_FILE).exists():
        raise BackupRefused(f"backup {backup}: an uncommitted part (.partial / {BACKUP_PART_FILE})")
    try:
        marker = json.loads((backup / BACKUP_MARKER).read_text())
        created = datetime.fromisoformat(str(marker["created"])).timestamp()
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise BackupRefused(f"backup {backup}: no readable completion marker {BACKUP_MARKER} ({error})") from None
    if Path(str(marker.get("root"))).resolve() != root.resolve():
        raise BackupRefused(f"backup {backup}: of run root {marker.get('root')}, not {root}")
    if marker.get("host") not in (None, host):
        raise BackupRefused(f"backup {backup}: {marker.get('host')}'s part, not {host}'s")
    if not isinstance(marker.get("mps"), int) or marker["mps"] <= 0:
        raise BackupRefused(f"backup {backup}: the marker counts no .mps ({marker.get('mps')!r})")
    return {**marker, "createdAt": created}


def verify_backup(backup: Path, root: Path, host: str, files: list[tuple[Path, int, float]]) -> dict:
    """A deletion run's precondition: ``backup`` is a committed part of this run root (backup_marker), made AFTER the
    newest of ``files`` (path, size, mtime) was last written, and holding each of them at the same relative path with
    the same size. Raises BackupRefused naming the first gap; returns what was checked."""
    marker = backup_marker(backup, root, host)
    newest = max((mtime for _, _, mtime in files), default=None)
    if newest is not None and marker["createdAt"] <= newest:
        raise BackupRefused(f"backup {backup} was made {stamp(marker['createdAt'])}, before the newest file to delete ({stamp(newest)})")
    missing = 0
    for path, size, _ in files:
        try:
            if (backup / path.relative_to(root)).stat().st_size == size:
                continue
        except (OSError, ValueError):
            pass
        missing += 1
        if missing == 1:
            first = path
    if missing:
        raise BackupRefused(f"backup {backup} lacks {missing} of the {len(files)} files to delete (first: {first})")
    return {"backup": str(backup), "created": stamp(marker["createdAt"]), "files": len(files), "newestFile": stamp(newest)}


class StoreSweep:
    """One sweep of this host's stopped node dirs (see the section comment). ``registry``: a RegistryView or an
    OfflineRegistry (dry run only). ``delete_unnamed`` with ``backup``: dirs whose only save files are unnamed go too."""

    def __init__(self, root: Path, host: str, registry, apply: bool = False, keep_dirs: int = KEEP_DIRS,
                 keep_hours: float = KEEP_HOURS, retention_days: float = ARCHIVE_RETENTION_DAYS, batch: int = 500,
                 pause: float = 0.0, running=node_running, now=time.time, log=print, delete_unnamed: bool = False,
                 unnamed_days: float = UNNAMED_DAYS, backup: Path | None = None) -> None:
        self.root, self.host, self.registry, self.apply = root, host, registry, apply
        self.keep_dirs, self.keep_hours, self.retention_days = keep_dirs, keep_hours, retention_days
        self.batch, self.pause, self.running, self.now, self.log = max(1, min(batch, 2000)), pause, running, now, log
        self.delete_unnamed, self.unnamed_days, self.backup = delete_unnamed, unnamed_days, backup
        self.store = store_root(root, host)
        self.counts: dict[str, dict[str, int]] = {}
        self.classes: dict[str, dict[str, int]] = {}
        self.unnamed = {"files": 0, "bytes": 0, "oldest": None, "newest": None}
        self.unnamed_files: dict[str, list[tuple[Path, int, float]]] = {}
        self._mtimes: dict[str, float] = {}           # the current dir's .mps mtimes, from its one walk
        self.result: dict = {"host": host, "root": str(root), "dryRun": not apply, "deleteUnnamed": delete_unnamed,
                             "unnamedDays": unnamed_days, "dirs": {}}

    def count(self, what: str, size: int = 0, n: int = 1) -> None:
        entry = self.counts.setdefault(what, {"files": 0, "bytes": 0})
        entry["files"] += n
        entry["bytes"] += size

    def classify(self, record: dict, f: "LegacyFile", what: str) -> None:
        """One save file's class, per host and per dir; unnamed ones also by mtime."""
        for table in (self.classes, record["classes"]):
            entry = table.setdefault(what, {"files": 0, "bytes": 0})
            entry["files"] += 1
            entry["bytes"] += f.size
        if what in UNNAMED:
            mtime = self._mtimes.get(str(f.path)) or self._mtime(f.path) or 0.0
            if self.delete_unnamed:          # the backup check's list: only a deletion run needs it (1.6 M files on ai)
                self.unnamed_files.setdefault(record["name"], []).append((f.path, f.size, mtime))
            for table in (self.unnamed, record["unnamed"]):
                table["files"] += 1
                table["bytes"] += f.size
                table["oldest"] = mtime if table["oldest"] is None else min(table["oldest"], mtime)
                table["newest"] = mtime if table["newest"] is None else max(table["newest"], mtime)

    def run(self) -> dict:
        store_mode = self.registry.store_mode()
        self.result["storeMode"] = store_mode
        if self.apply and not store_mode:
            raise cl.ClusterError("the registry is not in host-store mode: nothing is moved (dry run works)")
        if self.apply and (self.delete_unnamed or self.retention_days > 0):
            # A deletion run names its backup before anything is touched; the per-file check runs once the plan is known.
            if self.backup is None:
                raise BackupRefused("a run that deletes save files (--delete-unnamed, archive retention) needs --backup-verified PATH")
            backup_marker(self.backup, self.root, self.host)
        rows, live = self.registry.nodes()
        expired = self._expired_partitions() if self.retention_days > 0 else set()
        stopped: list[tuple[Path, str]] = []
        for data in sorted(self.root.iterdir()) if self.root.is_dir() else []:
            match = NODE_DIR.fullmatch(data.name)
            if match is None or not data.is_dir():
                continue
            node = match.group(1)
            if node in live or self.running(self.root, node):
                self.result["dirs"][data.name] = {"state": "live"}
                continue
            self.result["dirs"][data.name] = self._move_dir(data, node, expired)
            stopped.append((data, node))
        if expired:
            self._expire_store(expired)
        self._sweep_store_leftovers()
        self._delete_dirs(stopped, rows)
        self.result["counts"] = self.counts
        self.result["classes"] = self.classes
        self.result["unnamed"] = {**self.unnamed, "oldest": stamp(self.unnamed["oldest"]), "newest": stamp(self.unnamed["newest"])}
        for record in self.result["dirs"].values():
            if "unnamed" in record:
                record["unnamed"] = {**record["unnamed"], "oldest": stamp(record["unnamed"]["oldest"]),
                                     "newest": stamp(record["unnamed"]["newest"])}
                record.pop("name", None)
        return self.result

    # -- the move

    def _move_dir(self, data: Path, node: str, expired: set[int]) -> dict:
        record = {"name": data.name, "state": "stopped", "classes": {}, "unnamed": {"files": 0, "bytes": 0, "oldest": None, "newest": None}}
        files, leftovers = scan_legacy(data, node)
        scanned = {f.path for f in files}
        # One walk before anything moves: when the node last wrote anything (its log too), and .mps the layout does not
        # explain (they are never touched and keep the dir).
        last, strays, self._mtimes = self._mtime(self.root / f"{node}.log") or 0.0, 0, {}
        for directory, _, names in os.walk(data):
            for name in names:
                path = os.path.join(directory, name)
                mtime = self._mtime(Path(path)) or 0.0
                last = max(last, mtime)
                if name.endswith(".mps"):
                    self._mtimes[path] = mtime
                    strays += Path(path) not in scanned and not LEFTOVER.fullmatch(name)
        record["lastWrite"], record["lastWriteAt"], record["otherSaveFiles"] = stamp(last), last, strays
        record["pendingJournal"] = (data / "cluster" / PENDING_JOURNAL).is_file()
        for path in leftovers:
            size = self._size(path)
            self.count("leftoversDeleted" if self.apply else "leftoversWouldDelete", size)
            if self.apply:
                self._unlink(path)
        expiring = [f for f in files if not f.is_global and f.partition in expired]
        for f in expiring:
            self.count("retentionDeleted" if self.apply else "retentionWouldDelete", f.size)
            if self.apply:
                self._unlink(f.path)
        files = [f for f in files if f.is_global or f.partition not in expired]
        journaled = self._journaled(data) if record["pendingJournal"] else set()
        for f in [f for f in files if (f.partition, f.generation, f.sequence) in journaled and not f.is_global]:
            self.classify(record, f, "lateFinal")
        files = [f for f in files if f.is_global or (f.partition, f.generation, f.sequence) not in journaled]
        for start in range(0, len(files), self.batch):
            self._move_batch(record, files[start:start + self.batch])
            if self.pause and self.apply:
                time.sleep(self.pause)
        if self.apply:
            self._prune_empty(data / "cluster" / "saves")
        return record

    @staticmethod
    def _journaled(data: Path) -> set[tuple[int, int, int]]:
        """The save ids a pending unregistered-finals journal names (lines that do not parse name nothing; the dir stays
        whatever they said)."""
        found = set()
        try:
            for line in (data / "cluster" / PENDING_JOURNAL).read_text(errors="replace").splitlines():
                try:
                    entry = json.loads(line)
                    found.add((int(entry["Partition"]), int(entry["Generation"]), int(entry["Sequence"])))
                except (ValueError, KeyError, TypeError):
                    continue
        except OSError:
            pass
        return found

    def _move_batch(self, record: dict, files: list["LegacyFile"]) -> None:
        for f in [f for f in files if f.checksum is None]:
            self.classify(record, f, "kept.unreadable")
        files = [f for f in files if f.checksum is not None]
        if not files:
            return
        answers = self.registry.adopt(self.host, False, files)
        if len(answers) != len(files):
            raise cl.ClusterError(f"the registry answered {len(answers)} of {len(files)} files")
        commit, done = [], []
        for f, answer in zip(files, answers):
            status = answer["status"]
            if status in ("unnamed", "uncatalogued"):
                self.classify(record, f, status)
            elif status == "hostHeld":
                # The store's file is this very inode: a move an earlier sweep committed. Else another copy of a save the
                # store already holds: this one is unnamed.
                if self._same_inode(f.path, self._store_path(f)):
                    self.classify(record, f, "resume")
                    done.append((f, "resumed"))
                else:
                    self.classify(record, f, "hostHeldCopy")
            elif status != "adopt":
                self.classify(record, f, "kept." + str(status))
            elif answer.get("checksum") is not None and (answer.get("sizeBytes"), str(answer["checksum"]).upper()) != (f.size, f.checksum):
                self.classify(record, f, "kept.mismatch")
            elif not self._in_store(f):
                self.classify(record, f, "kept.storeConflict")
            else:
                self.classify(record, f, "rekey")
                commit.append(f)
        if not self.apply:
            return
        self._sync_store_dirs(commit)
        if commit:
            for f, answer in zip(commit, self.registry.adopt(self.host, True, commit)):
                if answer["status"] == "rekeyed" or answer["status"] == "hostHeld" and self._same_inode(f.path, self._store_path(f)):
                    done.append((f, "rekeyed"))
                else:
                    self.count("kept.commit." + str(answer["status"]), f.size)
                    record["commitRefused"] = record.get("commitRefused", 0) + 1
        for f, how in done:
            self._unlink(f.path)
            self.count("moved." + how, f.size)

    @staticmethod
    def _same_inode(a: Path, b: Path) -> bool:
        try:
            x, y = os.stat(a), os.stat(b)
            return (x.st_dev, x.st_ino) == (y.st_dev, y.st_ino)
        except OSError:
            return False

    def _store_path(self, f: "LegacyFile") -> Path:
        return self.store / f.relative

    def _in_store(self, f: "LegacyFile") -> bool:
        """The file is in the store under its own name - linked now (apply) or already there and standing for it. A dry
        run links nothing: a missing store file counts as one it would link."""
        target = self._store_path(f)
        if target.exists() or target.is_symlink():
            return same_save(f.path, target, f.size, f.checksum)
        if not self.apply:
            return True
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.link(f.path, target)        # no-replace: EEXIST if a node installed it meanwhile
            return True
        except FileExistsError:
            return same_save(f.path, target, f.size, f.checksum)
        except OSError as error:
            self.log(f"[hostd] store: {f.path} not linked into {target}: {error}")   # never a copy (EXDEV: another file system)
            return False

    def _sync_store_dirs(self, files: list["LegacyFile"]) -> None:
        """The new names are on disk before the registry is told (one fsync per directory per batch, none per file)."""
        for directory in sorted({self._store_path(f).parent for f in files}):
            descriptor = os.open(directory, os.O_RDONLY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)

    # -- archive retention (open question 1; off unless retention_days > 0)

    def _expired_partitions(self) -> set[int]:
        """Archived partitions this host first saw archived at least retention_days ago. The first-seen record is the
        sweep's own (<store>/.archive-seen.json); a partition that left the archive (read back) leaves it too, so its
        clock starts again. A dry run reads the record but never writes it."""
        archived = self.registry.archived_partitions()
        record_path = self.store / ".archive-seen.json"
        try:
            seen = {int(key): float(value) for key, value in json.loads(record_path.read_text()).items()}
        except (OSError, ValueError):
            seen = {}
        now = self.now()
        seen = {p: seen.get(p, now) for p in archived}
        if self.apply:
            self.store.mkdir(parents=True, exist_ok=True)
            temporary = record_path.with_suffix(".tmp")
            temporary.write_text(json.dumps({str(p): t for p, t in sorted(seen.items())}))
            temporary.replace(record_path)
        return {p for p, t in seen.items() if now - t >= self.retention_days * 86400}

    def _expire_store(self, expired: set[int]) -> None:
        for p in sorted(expired):
            directory = self.store / f"P-{p:016X}"
            if not directory.is_dir():
                continue
            for path in directory.iterdir():
                if path.is_file() and SAVE_NAME.fullmatch(path.name):
                    self.count("retentionDeleted" if self.apply else "retentionWouldDelete", self._size(path))
                    if self.apply:
                        self._unlink(path)
            if self.apply:
                try:
                    directory.rmdir()
                except OSError:
                    pass

    def _sweep_store_leftovers(self) -> None:
        """Temporary files and partial downloads in the store older than a day: a crashed writer's or an abandoned fetch's
        (a live node's own are minutes old; a fetch resumes its .part within the hour)."""
        cutoff = self.now() - 24 * 3600
        for directory, _, names in os.walk(self.store):
            for name in names:
                path = Path(directory) / name
                if not LEFTOVER.fullmatch(name) or name.startswith(".archive-seen") or (self._mtime(path) or cutoff) >= cutoff:
                    continue
                self.count("storeLeftoversDeleted" if self.apply else "storeLeftoversWouldDelete", self._size(path))
                if self.apply:
                    self._unlink(path)

    # -- stopped dirs

    def _delete_dirs(self, stopped: list[tuple[Path, str]], rows: set[str]) -> None:
        """A stopped dir goes (with its -magnetar dir) when ALL of these hold: the registry no longer has its row; no
        pending unregistered-finals journal is in it; every save file left in it is unnamed (rekey and resume files leave it,
        unless the registry refused the commit; no kept, lateFinal or out-of-layout .mps); it is not among the newest
        keep_dirs stopped dirs and not younger than keep_hours. A dir that still holds unnamed files needs, on top,
        --delete-unnamed, nothing written in it for unnamed_days, and a verified backup that holds every unnamed file
        (verify_backup; checked once for all such dirs: any gap deletes none of them). ``underFlag`` lists the dirs that
        would go with --delete-unnamed, whatever was asked."""
        by_last = sorted(stopped, key=lambda entry: self.result["dirs"][entry[0].name]["lastWriteAt"])
        newest = {data for data, _ in by_last[-self.keep_dirs:]} if self.keep_dirs > 0 else set()
        now = self.now()
        plain, unnamed, kept = [], [], {}
        for data, node in by_last:
            record = self.result["dirs"][data.name]
            blocking = sum(entry["files"] for what, entry in record["classes"].items()
                           if what not in UNNAMED and what not in ("rekey", "resume"))
            blocking += record.get("commitRefused", 0) + record["otherSaveFiles"]
            age = now - record["lastWriteAt"]
            why = ("registry row" if node in rows
                   else "pending unregistered-finals journal (T-0347)" if record["pendingJournal"]
                   else f"{blocking} save file(s) that are not unnamed" if blocking
                   else "newest stopped dirs" if data in newest
                   else f"younger than {self.keep_hours:g} h" if self.keep_hours > 0 and age < self.keep_hours * 3600
                   else "not directly under the run root" if data.resolve().parent != self.root.resolve()
                   else None)
            if why is None and record["unnamed"]["files"]:
                why = (f"unnamed files, last write {age / 86400:.1f} d ago < {self.unnamed_days:g} d"
                       if age < self.unnamed_days * 86400 else None)
                if why is None:
                    unnamed.append((data, node))
                    continue
            if why:
                kept[data.name] = why
            else:
                plain.append((data, node))
        flagged = {data.name: self._tree_size(data) + self._tree_size(self.root / f"{node}-magnetar") for data, node in unnamed}
        self.result["underFlag"] = {"dirs": flagged, "bytes": sum(flagged.values()),
                                    "unnamedFiles": sum(self.result["dirs"][name]["unnamed"]["files"] for name in flagged),
                                    "unnamedBytes": sum(self.result["dirs"][name]["unnamed"]["bytes"] for name in flagged)}
        go = list(plain)
        if unnamed and self.delete_unnamed:
            files = [entry for data, _ in unnamed for entry in self.unnamed_files.get(data.name, [])]
            try:
                if self.backup is None:
                    raise BackupRefused("--delete-unnamed needs --backup-verified PATH")
                self.result["backupCheck"] = verify_backup(self.backup, self.root, self.host, files)
                go += unnamed
            except BackupRefused as error:
                self.result["backupCheck"] = {"refused": str(error)}
                for data, _ in unnamed:
                    kept[data.name] = f"unnamed files: {error}"
        else:
            for data, _ in unnamed:
                kept[data.name] = "unnamed files only (deleted with --delete-unnamed)"
        deleted = []
        for data, node in sorted(go):
            size = flagged.get(data.name)
            size = self._tree_size(data) + self._tree_size(self.root / f"{node}-magnetar") if size is None else size
            self.count("dirsDeleted" if self.apply else "dirsWouldDelete", size)
            if self.apply:
                shutil.rmtree(data)
                shutil.rmtree(self.root / f"{node}-magnetar", ignore_errors=True)
            deleted.append(data.name)
        self.result["deleted" if self.apply else "wouldDelete"] = deleted
        self.result["keptDirs"] = kept

    # -- small helpers

    @staticmethod
    def _size(path: Path) -> int:
        try:
            return path.stat().st_size
        except OSError:
            return 0

    @staticmethod
    def _mtime(path: Path) -> float | None:
        try:
            return path.stat().st_mtime
        except OSError:
            return None

    @staticmethod
    def _tree_size(path: Path) -> int:
        total = 0
        for directory, _, names in os.walk(path):
            for name in names:
                try:
                    total += os.lstat(os.path.join(directory, name)).st_size
                except OSError:
                    pass
        return total

    @staticmethod
    def _unlink(path: Path) -> None:
        try:
            path.unlink()
        except FileNotFoundError:
            pass

    @staticmethod
    def _prune_empty(saves: Path) -> None:
        for directory, _, _ in sorted(os.walk(saves), key=lambda entry: -len(entry[0])):
            if Path(directory) != saves:
                try:
                    os.rmdir(directory)
                except OSError:
                    pass


def disk_report(root: Path, host: str) -> dict:
    """This host's save bytes: the store (unique inodes), what is left in node dirs (inodes not also in the store),
    leftovers, free disk. Hard links are counted once: during a move a file is in both places."""
    store, inodes = store_root(root, host), set()
    report = {"host": host, "at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "store": {"files": 0, "bytes": 0, "leftovers": 0},
              "legacy": {"files": 0, "bytes": 0, "alsoInStore": 0, "dirs": {}}}
    for directory, _, names in os.walk(store):
        for name in names:
            try:
                st = os.lstat(os.path.join(directory, name))
            except OSError:
                continue
            if not SAVE_NAME.fullmatch(name):
                report["store"]["leftovers"] += bool(LEFTOVER.fullmatch(name))
                continue
            if (st.st_dev, st.st_ino) in inodes:
                continue
            inodes.add((st.st_dev, st.st_ino))
            report["store"]["files"] += 1
            report["store"]["bytes"] += st.st_size
    for data in sorted(root.iterdir()) if root.is_dir() else []:
        if not NODE_DIR.fullmatch(data.name) or not data.is_dir():
            continue
        files = size = 0
        for directory, _, names in os.walk(data / "cluster" / "saves"):
            for name in names:
                if not name.endswith(".mps"):
                    continue
                try:
                    st = os.lstat(os.path.join(directory, name))
                except OSError:
                    continue
                if (st.st_dev, st.st_ino) in inodes:
                    report["legacy"]["alsoInStore"] += 1
                    continue
                files += 1
                size += st.st_size
        if files:
            report["legacy"]["dirs"][data.name] = {"files": files, "bytes": size}
            report["legacy"]["files"] += files
            report["legacy"]["bytes"] += size
    report["nodeDirs"] = sum(1 for data in (root.iterdir() if root.is_dir() else []) if NODE_DIR.fullmatch(data.name) and data.is_dir())
    stat = os.statvfs(root)
    report["freeMb"] = round(stat.f_bavail * stat.f_frsize / (1024 * 1024))
    return report


def registry_view(root: Path) -> RegistryView:
    """The registry as this host reaches it: the gateway named by the start's launch spec (multi-host), else this
    host's loopback control port; the admin token of the start (<root>/secrets/admin.token) or CLUSTER_ADMIN_TOKEN."""
    try:
        gateway = json.loads((root / "hostd" / "launch.json").read_text()).get("gateway")
    except (OSError, ValueError):
        gateway = None
    gateway = gateway or f"http://127.0.0.1:{29416 + int(os.environ.get('CLUSTER_PORT_BASE', '0') or 0)}"
    token = os.environ.get("CLUSTER_ADMIN_TOKEN")
    if not token:
        try:
            token = (root / "secrets" / "admin.token").read_text().strip() or None
        except OSError:
            token = None
    return RegistryView(gateway, token)


class StoreKeeper(threading.Thread):
    """hostd's background half of the store: once the registry is in host-store mode, the disk report every
    DISK_REPORT_SECONDS and - only when CLUSTER_HOSTD_SWEEP_SECONDS > 0 - a sweep (apply) that often. Off the step loop:
    a sweep of w33's legacy files takes minutes."""

    def __init__(self, agent: "HostAgent", root: Path, host: str, registry=None) -> None:
        super().__init__(name="store-keeper", daemon=True)
        self.agent, self.root, self.host = agent, root, host
        self.registry = registry or registry_view(root)
        #: Replaced whole, never changed in place: the agent's loop serializes it from another thread (write_status).
        self.status: dict = {}
        self.last_report = self.last_sweep = float("-inf")

    def _set(self, **fields) -> None:
        self.status = {**self.status, **fields}

    def tick(self) -> None:
        if not self.agent.store_mode:
            return
        now = time.monotonic()
        if SWEEP_SECONDS > 0 and now - self.last_sweep >= SWEEP_SECONDS:
            self.last_sweep = now
            try:
                # Moves, leftovers and dirs with no save file left only: never unnamed files, never archive retention.
                result = StoreSweep(self.root, self.host, self.registry, apply=True, pause=0.2, log=self.agent.log,
                                    retention_days=0, delete_unnamed=False).run()
                self._set(lastSweep={"at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "counts": result["counts"],
                                     "deleted": result.get("deleted", [])})
                self.agent.log(f"[hostd] store sweep: {json.dumps(result['counts'])}; deleted {len(result.get('deleted', []))} dir(s)")
            except (cl.ClusterError, OSError, ValueError) as error:
                self._set(lastSweep={"at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "error": str(error)})
                self.agent.log(f"[hostd] store sweep failed: {error}")
            self.last_report = float("-inf")
        if now - self.last_report >= DISK_REPORT_SECONDS:
            self.last_report = now
            try:
                self._set(disk=disk_report(self.root, self.host))
            except OSError as error:
                self._set(disk={"error": str(error)})

    def run(self) -> None:
        while not self.agent.stop.is_set():
            try:
                self.tick()
            except Exception as error:     # the keeper must not die quietly; the launcher loop is not affected
                self._set(error=str(error))
                self.agent.log(f"[hostd] store keeper: {error!r}")
            self.agent.stop.wait(30)


def sweep_command(args) -> int:
    root = Path(args.root).expanduser()
    if args.registry_db and args.apply:
        raise cl.ClusterError("--registry-db is for a dry run on a copied run root; --apply needs the live registry")
    registry = OfflineRegistry(Path(args.registry_db).expanduser()) if args.registry_db else registry_view(root)
    running = (lambda _root, _node: False) if args.registry_db else node_running
    backup = Path(args.backup_verified).expanduser() if args.backup_verified else None
    try:
        result = StoreSweep(root, args.host, registry, apply=args.apply, keep_dirs=args.keep_dirs, keep_hours=args.keep_hours,
                            retention_days=args.archive_retention_days, batch=args.batch, pause=0.2 if args.apply else 0.0,
                            running=running, delete_unnamed=args.delete_unnamed, unnamed_days=args.unnamed_days,
                            backup=backup).run()
    except BackupRefused as error:
        print(f"cluster hostd sweep: REFUSED, nothing touched: {error}", file=sys.stderr)
        return 3
    result["disk"] = disk_report(root, args.host)
    print(json.dumps(result, indent=1))
    # A deletion run whose backup did not cover the unnamed files deleted none of them: say so in the exit code.
    return 3 if args.apply and "refused" in (result.get("backupCheck") or {}) else 0


def main(args) -> int:
    root = Path(args.root).expanduser()
    if args.hostd_verb == "run":
        return run(root, args.host, fresh=args.fresh, log=lambda text: print(f"{time.strftime('%H:%M:%S')} {text}", flush=True))
    if args.hostd_verb == "start":
        return start(root, args.host, fresh=args.fresh)
    if args.hostd_verb == "stop":
        return stop(root)
    if args.hostd_verb == "status":
        print(json.dumps(status(root), indent=1))
        return 0
    if args.hostd_verb == "kill-nodes":
        print(json.dumps({"killed": kill_nodes(root)}))
        return 0
    if args.hostd_verb == "sweep":
        return sweep_command(args)
    if args.hostd_verb == "disk":
        print(json.dumps(disk_report(root, args.host), indent=1))
        return 0
    if args.hostd_verb == "local-stop":
        print(json.dumps({"asked": cl.local_stop_nodes(root, args.budget)}))
        return 0
    raise cl.ClusterError(f"unknown hostd verb {args.hostd_verb}")
