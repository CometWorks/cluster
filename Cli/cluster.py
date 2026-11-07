#!/usr/bin/env python3
"""``cluster`` - the one front door for humans, agents and the test bench.

Orchestration.md §11: ``fresh``/``start``/``stop``/``status``/``kill``/``diag``/``trigger``, mapped
1:1 onto admin-API verbs plus the executor actions, with no privileged side channel. TestPlan §1.7
makes this mandatory from Phase 1: the Phase 3 flight was driven by ad-hoc process wrangling and its
runs were not reproducible.

Also the module the scenario suite imports - the bench conveniences of TestPlan §1.7 (crash-report
environment, port configuration, log collection) live here rather than in each scenario.

Magnetar edition: every node is the stock DedicatedServer64 started through the Magnetar launcher
with the packaged cluster plugins (``cluster-node`` / ``cluster-wa``) loaded locally, or dev
folders in a checkout. Direct Transport supports a compiled bundle, a dev folder or the hub.
Per node the CLI keeps a Magnetar config dir (``<root>/<node>-magnetar``: plugin sources,
profile, compile cache, Magnetar log) beside the dedicated server data dir (``<root>/<node>-data``:
SpaceEngineers-Dedicated.cfg, saves, game log).

Usage (``--root`` may be omitted when ``CLUSTER_ROOT`` is set)::

    export CLUSTER_ROOT=~/cluster-run
    python3 cluster.py fresh [--wipe]        # new world from the template; --wipe ERASES an existing cluster
    python3 cluster.py status
    python3 cluster.py diag --depth entities
    python3 cluster.py trigger split-sweep --wait
    python3 cluster.py stop
"""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import uuid
import json
import math
import os
import re
import shlex
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

import magnetar_config

HOME = Path.home()

#: T-0031: start one Magnetar node alone first so it populates the SHARED plugin
#: caches before the rest launch concurrently. 0 disables (restores the old
#: all-at-once start).
SERIAL_WARMUP = os.environ.get("CLUSTER_SERIAL_WARMUP", "1") not in ("0", "", "false")
#: T-0229: "external" (the default since Viktor, 2026-09-24) runs the Gateway with --relay external and
#: gateway-rs beside it (Docs/Gateway/RelayIpc.md). "internal" keeps the whole packet path in the C#
#: Gateway: the reference implementation and fallback, to be removed later.
RELAY = os.environ.get("CLUSTER_RELAY", "external").strip().lower()
if RELAY not in ("internal", "external"):
    raise SystemExit(f"CLUSTER_RELAY must be internal or external, not {RELAY!r}")
SERIAL_WARMUP_TIMEOUT = float(os.environ.get("CLUSTER_SERIAL_WARMUP_TIMEOUT", "300"))

#: T-0102: restart the serial warm-up node once when a plugin its profile declares failed
#: to load. 0 reports the miss and starts the cluster anyway.
WARMUP_PLUGIN_RETRY = os.environ.get("CLUSTER_WARMUP_PLUGIN_RETRY", "1") not in ("0", "", "false")

#: One shared GitHub download cache per launcher, instead of one per instance.
#: Viktor, 2026-09-04: the public IP was being rate-limited by GitHub because every
#: instance fetched its own copy.
MAGNETAR_GITHUB_CACHE = Path(os.environ.get(
    "MAGNETAR_GITHUB_CACHE", HOME / ".cache/Magnetar/Legacy/GitHub")).expanduser()


def share_github_cache(instance_dir: Path, cache: Path) -> None:
    """Point one instance's GitHub folder at a shared cache, seeding it if empty.

    GitHub/ holds DOWNLOADED release artefacts - each entry is `<plugin>-<hash>`
    containing Bin/ and manifest.xml - so its contents are content-addressed and
    identical across instances (verified 2026-09-04: every node and client held the
    same DotNetCompat-6d00a29d and LinuxCompat-68cd6e19). It is therefore safe to
    share, unlike the COMPILE cache next to it, which two launchers would race on -
    that stays per instance and is why this function names one folder and not the
    parent.

    64 instances were each holding their own copy: 4.8 GB on disk, and one GitHub
    fetch per instance against a single public IP.

    Self-seeding on purpose: the first instance that already has content MOVES it
    into the cache, so an existing deployment converts itself without a download and
    a fresh host populates the cache once instead of once per instance.
    """
    github = instance_dir / "GitHub"
    cache.mkdir(parents=True, exist_ok=True)
    if github.is_symlink():
        if github.resolve() == cache.resolve():
            return
        github.unlink()
    elif github.is_dir():
        for entry in sorted(github.iterdir()):
            target = cache / entry.name
            if not target.exists():
                shutil.move(str(entry), str(target))
        shutil.rmtree(github, ignore_errors=True)
    github.symlink_to(cache, target_is_directory=True)



#: Magnetar reports a plugin it could not load as one ERROR line in the instance's own log,
#: and then runs on without it. T-0102 caught the warm-up node this way on 2026-09-08: its
#: GitHub archive fetch for the Performance plugin was cancelled 13 s into startup
#: (TaskCanceledException out of GitHub.GetRepoArchive) and the WA served the whole run with
#: no Performance plugin, while the cluster CLI printed nothing at all.
PLUGIN_LOAD_FAILURE = re.compile(r"Failed to load (?P<plugin>[^ ]+) because of an error: (?P<error>.*)")


def plugin_load_failures(config: Path, since: float) -> list[tuple[str, str]]:
    """The plugins an instance DECLARED but failed to load, newest instance log only.

    Read the FAILURE lines, not the declaration. Magnetar's "Enabled Plugins:" line is written
    before any plugin is fetched and lists what the PROFILE asked for - wa-1 named the
    Performance plugin there at 18:30:52 on 2026-09-08 and then failed to load it at 18:31:05.
    Grepping the declaration (or the plugin's own log prefix, which a plugin that never loaded
    also never writes) answers the wrong question; only "Failed to load" is discriminating.

    ``since`` is a start timestamp: Magnetar names each log info_<date>_<time>.log and the
    config dir keeps every previous start, so without it a months-old failure would be
    reported against a healthy launch.
    """
    logs = [path for path in config.glob("info_*.log") if path.stat().st_mtime >= since - 60]
    if not logs:
        return []
    newest = max(logs, key=lambda path: path.stat().st_mtime)
    failures = []
    for match in PLUGIN_LOAD_FAILURE.finditer(newest.read_text(errors="replace")):
        failures.append((match.group("plugin"), match.group("error").strip()))
    return failures


def _archive_root() -> Path | None:
    """The deployment archive this CLI runs from, when it does: the layout of Build/release.sh
    (gateway/, tools/, cli/) or of Build/package.sh (the same plus magnetar/ and plugins/).
    Development checkouts return None and the defaults below fall back to the sibling
    repositories."""
    root = Path(__file__).resolve().parent.parent
    return root if (root / "registry/ClusterRegistry.dll").exists() else None


ARCHIVE = _archive_root()


def _packaged(relative: str, fallback: Path) -> Path:
    """``relative`` inside the archive when the archive carries it, else ``fallback``. The release
    package ships neither Magnetar nor the plugin sources (Build/release.sh), the full archive
    does (Build/package.sh); the ``cli/cluster`` wrapper sets the variables explicitly either way."""
    return ARCHIVE / relative if ARCHIVE and (ARCHIVE / relative).exists() else fallback


def _transport_source() -> Path | None:
    """The Direct Transport dev folder to register, or None for the hub-pinned plugin (the default).

    T-0157 requirement 1: the nodes take Direct Transport from the Magnetar hub, the same way the
    clients do, so the transport build does not depend on what happens to be checked out on the
    host. The hub pin caught up on 2026-09-18 at 144a6e3, which is what made the hub the default;
    before that it pinned 2212094, older than the m_engineEvents field the cluster plugins bind.
    DIRECT_TRANSPORT_SOURCE names a checkout to compile instead - the dev override for testing a
    transport change before it is published - and "hub" spells the default explicitly."""
    value = os.environ.get("DIRECT_TRANSPORT_SOURCE", "").strip()
    if os.environ.get("DIRECT_TRANSPORT_BINARIES", "").strip():
        if value:
            raise ValueError("DIRECT_TRANSPORT_BINARIES and DIRECT_TRANSPORT_SOURCE are mutually exclusive")
        return None
    if not value or value.lower() == "hub":
        return None
    folder = Path(value).expanduser()
    if not (folder / "DirectTransportServer.xml").is_file():
        raise FileNotFoundError(f"Direct Transport dev folder {folder} has no DirectTransportServer.xml; "
                                "point DIRECT_TRANSPORT_SOURCE at a checkout of CometWorks/direct-transport, "
                                "or unset it (or set it to hub) to take the hub-pinned plugin")
    return folder


def _plugin_binaries() -> Path | None:
    """The folder of compiled cluster plugins to install as local plugins, or None for dev-folder
    mode: CLUSTER_PLUGIN_BINARIES, else the archive's plugins/ (Build/release.sh) unless
    CLUSTER_PLUGIN_SOURCE asks for sources explicitly."""
    explicit = os.environ.get("CLUSTER_PLUGIN_BINARIES")
    if explicit:
        return Path(explicit).expanduser()
    if os.environ.get("CLUSTER_PLUGIN_SOURCE"):
        return None
    packaged = _packaged("plugins/ClusterNode/ClusterNode.dll", Path())
    return packaged.parent.parent if packaged != Path() else None

# TestPlan §1.7: the environment every bench-launched process gets. A native crash must come back as
# a C# frame, and the dump must land in the run's artifact folder - never inside a run root a later
# `fresh` wipes, and never on /tmp, which is tmpfs on this host.
CRASH_ENV = {
    "DOTNET_DbgEnableMiniDump": "1",
    "DOTNET_DbgMiniDumpType": "2",
    "DOTNET_EnableCrashReport": "1",
}

JOIN_TOKEN = "Y2x1c3Rlci10ZXN0LWpvaW4tdG9rZW4tMDEyMzQ1Njc4OQ=="
CLUSTER_ID = "single-host-test-cluster"
WORLD_ID = "cluster-simple-test-world"
CONFIG_HASH = "single-host-test-config-v1"
BINARY_VERSION = "0.1.1.0"


def _extra_plugins() -> dict[str, list[Path]]:
    """CLUSTER_EXTRA_PLUGINS: comma-separated local plugin folders, each optionally prefixed with
    the role it is for (``node:/path/A,wa:/path/B``; no prefix = both roles)."""
    plugins: dict[str, list[Path]] = {"node": [], "wa": []}
    for entry in os.environ.get("CLUSTER_EXTRA_PLUGINS", "").split(","):
        entry = entry.strip()
        if not entry:
            continue
        role, sep, path = entry.partition(":")
        roles = [role] if sep and role in plugins else list(plugins)
        folder = Path(path if sep and role in plugins else entry).expanduser()
        for r in roles:
            plugins[r].append(folder)
    return plugins


def port_open(host: str, port: int, timeout: float = 0.3) -> bool:
    with socket.socket() as probe:
        probe.settimeout(timeout)
        return probe.connect_ex((host, port)) == 0


def split_endpoint(endpoint: str) -> tuple[str, int]:
    host, _, port = endpoint.rpartition(":")
    return host, int(port)


# -- T-0320 node control: hosts file, operator states, the `nodes list` line ----------------------
# Docs/Gateway/NodeControl.md. The registry owns the per-host serving/spare targets, the global node
# ids and every policy decision; this module applies the hosts file at start, carries out the node
# plan on its own host (Cluster.actualize_step) and renders the operator view.

#: How long a node the registry marked Kill gets to exit by itself before it is SIGKILLed. A drained
#: node ends itself ('node Empty', then 'process exit 0'); killing it the moment it is marked lost
#: its exit line on 2 of 4 rotations (T-0162, 2026-09-17).
SELF_EXIT_SECONDS = float(os.environ.get("CLUSTER_SELF_EXIT_SECONDS", "30"))
#: The operator-visible node states (NodeControlDesign.md); the registry's Active/Closed/Empty/Dead
#: are internal and never shown.
OPERATOR_STATES = ("Starting", "Serving", "Spare", "Draining", "Stopping", "Failed")
#: A node in one of these is an operation in flight: `--wait` and the rotator wait for none to be left.
TRANSITIONAL_STATES = frozenset({"Starting", "Draining", "Stopping"})
STATE_LEGEND = ("Starting = launched, not serving yet; Serving = may host partitions (0 is still Serving); "
                "Spare = warm, holds nothing; Draining = handing its partitions over; "
                "Stopping = forced or empty, process ending; Failed = crashed, until replaced")
HOSTS_FILE_KEYS = frozenset({"version", "gateway", "worldAuthority", "hosts"})
HOST_KEYS = frozenset({"serving", "spares", "ramFloorGb", "diskFloorGb"})
#: T-0321 hosts file v2 (additive): where each host is and how the launcher reaches it.
HOSTS_FILE_KEYS_V2 = HOSTS_FILE_KEYS | {"network"}
HOST_KEYS_V2 = HOST_KEYS | {"address", "ssh", "runRoot", "serverEnv", "portSlots", "portBase", "executor"}
NETWORK_KEYS = frozenset({"internalNetworks", "portBase", "networkWindows"})
MAX_NODES_PER_HOST = 16
#: Port slots per host: node i on a host has backend 28416+B+i and control 29416+B+i, i = 1..portSlots.
DEFAULT_PORT_SLOTS = 16
MAX_PORT_SLOTS = 64
#: The server side of a non-gateway host (T-0315 bin/server-sync writes it).
DEFAULT_SERVER_ENV = "~/cluster-server/env.sh"
#: node-9 | 12345@polaris | polaris#2 (resolved by the registry; checked here for a clear error).
SELECTOR = re.compile(r"^(node-\d+|\d+@[A-Za-z0-9][A-Za-z0-9.-]*|[A-Za-z0-9][A-Za-z0-9.-]*#\d+)$")
HOST_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.-]*$")
SSH_TARGET = re.compile(r"^(local|([A-Za-z0-9._-]+@)?[A-Za-z0-9][A-Za-z0-9.-]*)$")
WINDOW = re.compile(r"^([01]\d|2[0-3]):([0-5]\d)-([01]\d|2[0-3]):([0-5]\d)$")


def local_host() -> str:
    """This host's name as the node-control model uses it: CLUSTER_NODE_HOST, else the short hostname."""
    return os.environ.get("CLUSTER_NODE_HOST", "").strip() or socket.gethostname().split(".")[0]


def validate_hosts_file(data: object, here: str, root: Path | None = None) -> dict:
    """The hosts file, normalized, or ClusterError naming every problem at once.

    Version 1 (T-0320): per host serving/spares/floors; the Gateway and the World Authority run on this
    host. Version 2 (T-0321) adds where each host is (see validate_hosts_file_v2). A file without
    "version" is version 1, so every T-0320 file keeps its meaning."""
    if not isinstance(data, dict):
        raise ClusterError("hosts file: the top level must be a JSON object")
    if data.get("version", 1) == 2:
        return validate_hosts_file_v2(data, here, root)
    problems: list[str] = []
    problems += [f"unknown key {key!r}" for key in sorted(set(data) - HOSTS_FILE_KEYS)]
    if data.get("version", 1) != 1:
        problems.append(f"version must be 1 or 2, not {data.get('version')!r}")
    for key in ("gateway", "worldAuthority"):
        if data.get(key, here) != here:
            problems.append(f"{key} must be this host ({here}) in a version 1 hosts file, not {data.get(key)!r}"
                            " (multi-host needs version 2)")
    normalized = _validate_host_counts(data, HOST_KEYS, problems)
    if problems:
        raise ClusterError("hosts file: " + "; ".join(problems))
    return {"version": 1, "gateway": data.get("gateway", here), "worldAuthority": data.get("worldAuthority", here),
            "hosts": normalized}


def _validate_host_counts(data: dict, keys: frozenset, problems: list[str]) -> dict[str, dict]:
    """The v1 part of every host entry: serving/spares 0..16, floors >= 0; at least one serving node."""
    hosts = data.get("hosts")
    normalized: dict[str, dict] = {}
    if not isinstance(hosts, dict) or not hosts:
        problems.append("hosts must be a non-empty object {host: {serving, spares, ...}}")
        hosts = {}
    for name, entry in hosts.items():
        if not isinstance(name, str) or not HOST_NAME.match(name):
            problems.append(f"host name {name!r} is not a plain host name")
            continue
        if not isinstance(entry, dict):
            problems.append(f"{name}: must be an object")
            continue
        problems += [f"{name}: unknown key {key!r}" for key in sorted(set(entry) - keys)]
        counts = {}
        for key, default in (("serving", None), ("spares", 0)):
            value = entry.get(key, default)
            if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= MAX_NODES_PER_HOST:
                problems.append(f"{name}: {key} must be an integer 0..{MAX_NODES_PER_HOST}, not {value!r}")
                value = 0
            counts[key] = value
        floors = {}
        for key in ("ramFloorGb", "diskFloorGb"):
            value = entry.get(key)
            if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0):
                problems.append(f"{name}: {key} must be a non-negative number, not {value!r}")
                value = None
            floors[key] = value
        normalized[name] = {**counts, **floors}
    if hosts and normalized and sum(entry["serving"] for entry in normalized.values()) < 1:
        problems.append("at least one serving node is needed in total")
    return normalized


def _non_negative_int(value, name: str, problems: list[str], default: int, maximum: int = 60000) -> int:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= maximum:
        problems.append(f"{name} must be an integer 0..{maximum}, not {value!r}")
        return default
    return value


def validate_hosts_file_v2(data: dict, here: str, root: Path | None = None) -> dict:
    """Hosts file version 2 (T-0321): v1 plus, per host, where it is and how the launcher reaches it.

    Top level: ``gateway`` and ``worldAuthority`` (required once there is more than one host; the World
    Authority runs on the Gateway host in this increment) and ``network``: ``internalNetworks`` (CIDRs; every
    address must lie inside one), ``portBase`` (default 0) and ``networkWindows`` ("HH:MM-HH:MM", local time).
    Per host, besides serving/spares/floors:

    - ``address``: the host's LAN address, an IP literal (never a name: polaris' /etc/hosts maps
      ``polaris`` to 127.0.1.1) inside internalNetworks. Required on every host of a multi-host file.
    - ``ssh``: ``local`` or ``[user@]host``, how the Gateway host reaches it (bootstrap, secrets, logs).
      Default: ``local`` for the Gateway host, the host key otherwise.
    - ``runRoot``: absolute; the same path on every host (the server-sync rule: saved Locations in the
      registry are absolute paths). Default: the run root.
    - ``serverEnv``: the file a remote host sources before running the server side (T-0315); default
      ~/cluster-server/env.sh on every non-Gateway host, none on the Gateway host.
    - ``portSlots`` (1..64, default 16) and ``portBase`` (default network.portBase): node i on the host uses
      backend 28416+B+i and control 29416+B+i. Two host entries on one address need disjoint ranges.
    - ``executor.tokenFile``: where that host's per-start executor token is written (0600), relative to its
      runRoot unless absolute. Default ``secrets/executor-<host>.token``. Tokens are minted anew at every
      start (Viktor 2026-10-01) and never appear on a command line.
    """
    import ipaddress
    problems: list[str] = []
    problems += [f"unknown key {key!r}" for key in sorted(set(data) - HOSTS_FILE_KEYS_V2)]
    network = data.get("network") or {}
    if not isinstance(network, dict):
        problems.append("network must be an object")
        network = {}
    problems += [f"network: unknown key {key!r}" for key in sorted(set(network) - NETWORK_KEYS)]
    networks = []
    raw_networks = network.get("internalNetworks", [])
    if isinstance(raw_networks, str):
        raw_networks = [part.strip() for part in raw_networks.split(",") if part.strip()]
    if not isinstance(raw_networks, list):
        problems.append("network.internalNetworks must be a list of CIDRs")
        raw_networks = []
    for text in raw_networks:
        try:
            networks.append(ipaddress.ip_network(str(text), strict=False))
        except ValueError:
            problems.append(f"network.internalNetworks: {text!r} is not a CIDR")
    base = _non_negative_int(network.get("portBase"), "network.portBase", problems, 0)
    windows = network.get("networkWindows", [])
    if not isinstance(windows, list) or any(not isinstance(w, str) or not WINDOW.match(w) for w in windows):
        problems.append(f"network.networkWindows must be a list of \"HH:MM-HH:MM\", not {windows!r}")
        windows = []
    counts = _validate_host_counts(data, HOST_KEYS_V2, problems)
    hosts = data.get("hosts") if isinstance(data.get("hosts"), dict) else {}
    multi = len(hosts) > 1
    gateway = data.get("gateway")
    authority = data.get("worldAuthority", gateway)
    if gateway is None:
        if multi:
            problems.append("gateway is required in a multi-host file (every host reads the same file)")
        gateway = authority = here if authority is None else authority
    if gateway not in hosts:
        problems.append(f"gateway {gateway!r} is not one of the hosts")
    if authority != gateway:
        problems.append(f"worldAuthority must be the gateway host ({gateway}) in this increment, not {authority!r}")
    run_root = str(Path(root).expanduser()) if root is not None else None
    normalized: dict[str, dict] = {}
    ranges: dict[str, list[tuple[str, int, int]]] = {}
    for name, entry in hosts.items():
        if name not in counts:
            continue
        gw = name == gateway
        out = dict(counts[name])
        address = entry.get("address")
        if address is None:
            if multi:
                problems.append(f"{name}: address is required in a multi-host file")
        else:
            try:
                parsed = ipaddress.ip_address(str(address))
                if multi and parsed.is_loopback:
                    problems.append(f"{name}: address {address} is loopback; peers on other hosts cannot reach it")
                elif networks and not any(parsed in net for net in networks):
                    problems.append(f"{name}: address {address} is outside network.internalNetworks")
                elif multi and not networks:
                    problems.append("network.internalNetworks is required in a multi-host file")
            except ValueError:
                problems.append(f"{name}: address must be an IP literal, not {address!r}")
        out["address"] = address
        ssh = entry.get("ssh", "local" if gw else name)
        if not isinstance(ssh, str) or not SSH_TARGET.match(ssh):
            problems.append(f"{name}: ssh must be local or [user@]host, not {ssh!r}")
        elif gw and ssh != "local":
            problems.append(f"{name}: the gateway host runs the launcher itself; its ssh must be local")
        elif not gw and ssh == "local":
            problems.append(f"{name}: ssh local is only for the gateway host")
        out["ssh"] = ssh
        host_root = entry.get("runRoot", run_root)
        if host_root is not None and (not isinstance(host_root, str) or not host_root.startswith("/")):
            problems.append(f"{name}: runRoot must be an absolute path, not {host_root!r}")
        elif host_root is not None and run_root is not None and host_root.rstrip("/") != run_root.rstrip("/"):
            problems.append(f"{name}: runRoot {host_root} differs from the run root {run_root}; every host uses the"
                            " same path (saved Locations in the registry are absolute)")
        out["runRoot"] = host_root.rstrip("/") if isinstance(host_root, str) else host_root
        server_env = entry.get("serverEnv", None if gw else DEFAULT_SERVER_ENV)
        if server_env is not None and (not isinstance(server_env, str) or not server_env.strip()):
            problems.append(f"{name}: serverEnv must be a path, not {server_env!r}")
        out["serverEnv"] = server_env
        slots = entry.get("portSlots", DEFAULT_PORT_SLOTS)
        if isinstance(slots, bool) or not isinstance(slots, int) or not 1 <= slots <= MAX_PORT_SLOTS:
            problems.append(f"{name}: portSlots must be an integer 1..{MAX_PORT_SLOTS}, not {slots!r}")
            slots = DEFAULT_PORT_SLOTS
        needed = out["serving"] + out["spares"] + (1 if name == authority else 0)
        if slots < needed:
            problems.append(f"{name}: portSlots {slots} < {needed} processes it runs at once")
        out["portSlots"] = slots
        out["portBase"] = _non_negative_int(entry.get("portBase"), f"{name}: portBase", problems, base)
        executor = entry.get("executor", {})
        token_file = executor.get("tokenFile") if isinstance(executor, dict) else None
        if not isinstance(executor, dict) or set(executor) - {"tokenFile"}:
            problems.append(f"{name}: executor must be {{\"tokenFile\": path}}, not {executor!r}")
        elif token_file is not None and (not isinstance(token_file, str) or not token_file.strip()
                                         or ".." in Path(token_file).parts or any(c.isspace() for c in token_file)):
            problems.append(f"{name}: executor.tokenFile must be a plain path without .. or blanks, not {token_file!r}")
            token_file = None
        out["executorTokenFile"] = token_file or f"secrets/executor-{name}.token"
        normalized[name] = out
        if address is not None:
            b = out["portBase"]
            ranges.setdefault(str(address), []).append((name, b + 1, b + slots))
    for address, entries in ranges.items():
        entries.sort(key=lambda item: item[1])
        for (first, _, end), (second, begin, _) in zip(entries, entries[1:]):
            if begin <= end:
                problems.append(f"{first} and {second} share {address} with overlapping port slots")
    if problems:
        raise ClusterError("hosts file: " + "; ".join(problems))
    return {"version": 2, "gateway": gateway, "worldAuthority": authority,
            "network": {"internalNetworks": [str(net) for net in networks], "portBase": base,
                        "networkWindows": list(windows)},
            "hosts": normalized}


def is_multi_host(spec: dict) -> bool:
    """A start that spans hosts: version 2 with more than one host. Everything else is the T-0320 path."""
    return spec.get("version") == 2 and len(spec.get("hosts") or {}) > 1


def synthesize_hosts_file(serving: int, spares: int, here: str) -> dict:
    """The hosts file an installation without one gets: --nodes/--spares on this host (so
    cluster-regression's env.make_config keeps working unchanged)."""
    return {"version": 1, "gateway": here, "worldAuthority": here,
            "hosts": {here: {"serving": serving, "spares": spares, "ramFloorGb": None, "diskFloorGb": None}}}


def hosts_file_request(spec: dict, sha256: str) -> dict:
    """The body of POST /admin/v1/node-control/hosts-file (HostsFileApply). Floors go over the wire in
    MB. No resume: every start allocates fresh ids (Viktor 2026-10-01)."""
    def mb(gb):
        return None if gb is None else int(round(gb * 1024))
    return {"hosts": {name: {"serving": entry["serving"], "spares": entry["spares"],
                             "ramFloorMb": mb(entry.get("ramFloorGb")), "diskFloorMb": mb(entry.get("diskFloorGb"))}
                      for name, entry in spec["hosts"].items()},
            "sha256": sha256}


def as_list(value) -> list:
    """A list field of an admin result, which carries string values: a JSON list, lines, or a real list."""
    if value in (None, ""):
        return []
    if isinstance(value, list):
        return value
    try:
        parsed = json.loads(value)
        return parsed if isinstance(parsed, list) else [parsed]
    except (TypeError, ValueError):
        return [line for line in str(value).splitlines() if line.strip()]


def quote_path(value: str) -> str:
    """A host or selector as one URL path/query segment: `polaris#2` must not become a fragment."""
    return urllib.parse.quote(value, safe="@")


def fmt_age(seconds) -> str:
    """3h12m, 4m, 9s (the `nodes list` age column)."""
    if seconds is None:
        return "?"
    seconds = int(max(0, seconds))
    if seconds >= 3600:
        return f"{seconds // 3600}h{seconds % 3600 // 60:02d}m"
    return f"{seconds // 60}m" if seconds >= 60 else f"{seconds}s"


def fmt_countdown(seconds: float) -> str:
    """1h05m, 3m40s, 12s (time left to a deadline)."""
    seconds = int(max(0, seconds))
    if seconds >= 3600:
        return f"{seconds // 3600}h{seconds % 3600 // 60:02d}m"
    return f"{seconds // 60}m{seconds % 60:02d}s" if seconds >= 60 else f"{seconds}s"


def _parse_stamp(text) -> float | None:
    """An ISO timestamp from the registry (7 fractional digits, Z) as epoch seconds; None if absent."""
    if not text:
        return None
    try:
        value = re.sub(r"(\.\d{6})\d+", r"\1", str(text)).replace("Z", "+00:00")
        stamp = datetime.fromisoformat(value)
        return (stamp if stamp.tzinfo else stamp.replace(tzinfo=timezone.utc)).timestamp()
    except ValueError:
        return None


def format_node_line(node: dict, now: float | None = None) -> str:
    """One node of the operator view, exactly as `cluster nodes list` and cluster-status print it:
    ``  #1 node-3  @polaris pid 41233 age 3h12m Serving  12 partitions 3 clients``."""
    now = time.time() if now is None else now
    position = f"#{node['position']}" if node.get("position") else ""
    pid = node.get("pid")
    state = node.get("state") or "?"
    line = (f"  {position:<2} {node.get('node', '?'):<7} @{node.get('host', '?')} pid {pid if pid else '?'}"
            f" age {fmt_age(node.get('ageSeconds')):<5} {state:<8} {node.get('partitions') or 0:>2} partitions")
    if state not in ("Spare", "Starting") and node.get("clients") is not None:
        line += f" {node['clients']} clients"
    stop = node.get("stop")
    if stop:
        who = stop.get("requestedBy") or stop.get("actor") or "?"
        deadline = _parse_stamp(stop.get("deadline"))
        force = ("forced" if stop.get("forced") else f"force in {fmt_countdown(deadline - now)}"
                 if deadline is not None and deadline > now else "force due")
        line += f"  ({stop.get('kind', '?')} by {who}: \"{stop.get('why') or ''}\", {force})"
    return line


def format_host_line(host: dict) -> str:
    """``polaris  serving 5/5 (file 4)  spares 1/1 (file 1)  * runtime differs from hosts file``."""
    parts = [host.get("host", "?")]
    for key in ("serving", "spares"):
        counts = host.get(key) or {}
        file = counts.get("file")
        parts.append(f"{key} {counts.get('actual', 0)}/{counts.get('target', 0)} (file {'-' if file is None else file})")
    differs = any((host.get(key) or {}).get("target") != (host.get(key) or {}).get("file") for key in ("serving", "spares"))
    return "  ".join(parts) + ("  * runtime differs from hosts file" if differs else "")


def _not_counted_item(entry: dict) -> str:
    text = f"{entry.get('node', '?')} @{entry.get('host', '?')} pid {entry.get('pid') or '?'}"
    if entry.get("ageSeconds") is not None:
        text += f" age {fmt_age(entry['ageSeconds'])}"
    role = str(entry.get("role") or "").replace(" ", "").upper()
    if role in ("WA", "WORLDAUTHORITY"):     # the registry names it "WorldAuthority"
        text += " World Authority"
    return text


def node_sort_key(node: dict) -> tuple:
    return (node.get("position") is None, node.get("position") or 0,
            _parse_stamp(node.get("startedAt")) or 0.0, node.get("node") or "")


def format_node_control(view: dict, now: float | None = None) -> list[str]:
    """The whole `cluster nodes list` text: per host its header, its nodes, and what is not counted."""
    lines = []
    extra = list(view.get("notCounted") or [])
    hosts = view.get("hosts") or []
    for host in hosts:
        name = host.get("host")
        lines.append(format_host_line(host))
        for warning in host.get("warnings") or []:
            lines.append(f"  ! {warning}")
        for node in sorted((n for n in view.get("nodes") or [] if n.get("host") == name), key=node_sort_key):
            lines.append(format_node_line(node, now))
        here = [entry for entry in extra if entry.get("host") == name]
        if here:
            lines.append("  not counted: " + "; ".join(_not_counted_item(entry) for entry in here))
    known = {host.get("host") for host in hosts}
    stray = [node for node in view.get("nodes") or [] if node.get("host") not in known]
    for node in sorted(stray, key=node_sort_key):
        lines.append(format_node_line(node, now))
    rest = [entry for entry in extra if entry.get("host") not in known]
    if rest:
        lines.append("  not counted: " + "; ".join(_not_counted_item(entry) for entry in rest))
    return lines


def _field(mapping, name: str):
    """Case-insensitive field of a registry JSON object (the legacy routes serialize PascalCase)."""
    if not isinstance(mapping, dict):
        return None
    if name in mapping:
        return mapping[name]
    lowered = name.lower()
    for key, value in mapping.items():
        if key.lower() == lowered:
            return value
    return None


def _enum(value, names: tuple[str, ...]) -> str | None:
    """A registry enum as its name: the legacy routes serialize enums as numbers, the admin API as names."""
    if isinstance(value, int) and not isinstance(value, bool):
        return names[value] if 0 <= value < len(names) else str(value)
    return None if value is None else str(value)


DESIRED = ("Wanted", "Draining", "Kill")
#: What the legacy (unmanaged) executor heartbeat carries per observation: the T-0320 body, unchanged.
LEGACY_OBSERVATION_KEYS = ("slotKey", "attemptKey", "state", "node", "failure", "pid", "host")
INCUMBENT = ("None", "Draining", "Kill")
ROLES = ("Regular", "WA")


def regular_node_processes(root: Path) -> list[tuple[int, str, str]]:
    """(pid, control port, node id) of every REGULAR node process of this run root on this host. No registry and
    no ledger: the process list names the nodes, and a node's environment names its control port, id and role
    (only those three keys are read - the environment also holds this start's tokens). The World Authority is
    left out: it owns no partitions, and it keeps serving the nodes until they are gone."""
    found = subprocess.run(["pgrep", "-f", f"MagnetarInterim.*{re.escape(str(root))}/"], capture_output=True, text=True)
    out = []
    for pid in sorted({int(pid) for pid in found.stdout.split()}):
        try:
            environ = dict(entry.split(b"=", 1) for entry in Path(f"/proc/{pid}/environ").read_bytes().split(b"\0")
                           if entry.startswith((b"CLUSTER_CONTROL_PORT=", b"CLUSTER_NODE_ID=", b"CLUSTER_NODE_ROLE=")))
        except OSError:
            continue
        port = environ.get(b"CLUSTER_CONTROL_PORT", b"").decode()
        if port.isdigit() and environ.get(b"CLUSTER_NODE_ROLE", b"").decode().upper() != "WA":
            out.append((pid, port, environ.get(b"CLUSTER_NODE_ID", b"?").decode()))
    return out


def local_stop_nodes(root: Path, budget: float) -> list[dict]:
    """T-0347: ask every regular node of this run root ON THIS HOST for a local stop, each on the loopback side
    of its own control port (open without a token, T-0321). One entry per node: its answer, or the error."""
    asked: dict[str, dict] = {}
    for pid, port, node_id in regular_node_processes(root):
        if port in asked:   # the launch wrapper and its dotnet child share the environment
            continue
        entry = {"node": node_id, "pid": pid}
        try:
            request = urllib.request.Request(f"http://127.0.0.1:{port}/cluster/local-stop?budget={budget:.0f}",
                                             data=b"", method="POST")
            with urllib.request.urlopen(request, timeout=10) as response:
                answer = json.loads(response.read().decode() or "{}")
            entry.update(owned=answer.get("owned"), registryHeld=answer.get("registryHeld"))
        except Exception as error:  # noqa: BLE001 - a node that cannot be asked is killed at the grace as before
            entry["error"] = str(error)
        asked[port] = entry
    return list(asked.values())


@dataclass
class NodeSpec:
    """One dedicated-server process in the plan."""

    node_id: str
    epoch: int
    backend: str
    control: str
    role: str = "regular"  # regular | WA


@dataclass
class Config:
    root: Path
    gateway_repo: Path = HOME / "ws/se/se1/cluster"
    # The Magnetar install (MagnetarInterim.bin + Libraries), the stock DedicatedServer64 and the
    # cluster plugins. MAGNETAR_HOME / SPACE_ENGINEERS_BIN64 override (the archive's `cluster`
    # wrapper sets them to the packaged copies).
    magnetar: Path = field(default_factory=lambda: Path(os.environ.get(
        "MAGNETAR_HOME", _packaged("magnetar", HOME / ".config/Magnetar"))).expanduser())
    ds64: Path = field(default_factory=lambda: Path(os.environ.get(
        "SPACE_ENGINEERS_BIN64", HOME / ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64")).expanduser())
    # Where the node / WA plugins come from (T-0157). Release: the archive's compiled
    # plugins/ClusterNode and plugins/WorldAuthority, installed as Pulsar LOCAL plugins into each
    # node's config dir - the same build as the packaged Gateway, tools and CLI, never the hub.
    # CLUSTER_PLUGIN_BINARIES names such a folder explicitly. Development: CLUSTER_PLUGIN_SOURCE
    # (or ~/ws/se/se1/cluster when the archive ships no binaries) is registered as a dev folder Magnetar
    # compiles; an explicit CLUSTER_PLUGIN_SOURCE wins over packaged binaries.
    plugin_binaries: Path | None = field(default_factory=lambda: _plugin_binaries())
    plugin_source: Path = field(default_factory=lambda: Path(os.environ.get(
        "CLUSTER_PLUGIN_SOURCE", _packaged("plugins/cluster", HOME / "ws/se/se1/cluster"))).expanduser())
    # Direct Transport comes from the Magnetar hub (T-0157 requirement 1), the same pin the clients
    # load, so nodes and clients cannot drift apart and the transport build does not depend on a
    # checkout being present. DIRECT_TRANSPORT_SOURCE names a checkout to compile instead, the dev
    # override for a transport change that is not published yet; DIRECT_TRANSPORT_BINARIES installs
    # an already-compiled copy and is mutually exclusive with it.
    transport_source: Path | None = field(default_factory=lambda: _transport_source())
    transport_binaries: Path | None = field(default_factory=lambda:
        Path(os.environ["DIRECT_TRANSPORT_BINARIES"]).expanduser()
        if os.environ.get("DIRECT_TRANSPORT_BINARIES", "").strip() else None)
    common_plugins: Path | None = field(default_factory=lambda:
        Path(os.environ["CLUSTER_COMMON_PLUGINS"]).expanduser()
        if os.environ.get("CLUSTER_COMMON_PLUGINS", "").strip() else None)
    debug_plugins: bool = False  # DebugBuild for the dev folders (CLUSTER_DEBUG_PLUGINS=1)
    # The game client launcher. The CLI never starts clients; harnesses that do (cluster-regression)
    # read it from here so one Config describes the whole deployment.
    client_launcher: Path = HOME / ".config/Pulsar/Interim.bin"
    world_template: Path | None = None
    # MagnetarWorld step that turns the template into the split world: `to-split` for a plain world,
    # or one of the tool's fixture variants (`to-handover`, `to-proximity`, `to-clientless`,
    # `to-counters`, `to-fidelity`), which take the same two arguments (CLUSTER_SPLIT_STEP).
    split_step: str = field(default_factory=lambda: os.environ.get("CLUSTER_SPLIT_STEP", "to-split"))
    # Host port plan: Steam frontend 27016, Direct Transport frontend 27116 + port_base, Gateway
    # control 29416 + port_base, node-i backend 28416 + port_base + i and control 29416 + port_base + i.
    # port_base (CLUSTER_PORT_BASE) lets a second cluster share the host. DT stays on loopback -
    # GatewayOptions refuses --direct on a publicly reachable interface next to the Steam frontend.
    port_base: int = field(default_factory=lambda: int(os.environ.get("CLUSTER_PORT_BASE", "0") or 0))
    gateway_listen: str | None = None
    gateway_control: str | None = None
    steam_listen: str | None = None
    # Registry lease TTL (CLUSTER_LEASE_TTL_SECONDS). 3 s was the default until 2026-09-17 and is
    # hair-trigger under Magnetar: a node's first frames after the world load hitch for ~5.5 s, the
    # leases of the partitions it just claimed expire, and they stay unowned (bench baseline,
    # 2026-09-17 02:54). The regression cluster has run 15 s since 2026-08-16 for the same reason.
    lease_ttl_seconds: int = field(default_factory=lambda: int(os.environ.get("CLUSTER_LEASE_TTL_SECONDS", "15")))
    sync_distance: int = 3000
    server_name: str = "Magnetar Cluster Test"
    member_limit: int = 16  # advertised and admission-enforced player cap
    administrators: list[int] = field(default_factory=list)  # Steam ids admitted as admins (§3.4)
    # The Gateway's admin kick cooldown (admission kickTimeoutSeconds); None keeps its 300 s default. Tests set seconds.
    kick_timeout_seconds: int | None = None
    regular_nodes: int = 2  # node-i at ports 28416+i/29416+i, wa-1 right after them
    spare_nodes: int = 0  # HotSpareNodes.md: extra regular slots initially designated hot spares
    node_env: dict[str, str] = field(default_factory=dict)  # extra env for every node process
    # Extra env for one node process, by node id; applied after node_env.
    node_env_by_id: dict[str, dict[str, str]] = field(default_factory=dict)
    # Extra Gateway command line arguments, appended after the CLI's own (CLUSTER_GATEWAY_ARGS,
    # shell-split), and extra Gateway environment.
    gateway_args: list[str] = field(default_factory=lambda: shlex.split(os.environ.get("CLUSTER_GATEWAY_ARGS", "")))
    gateway_env: dict[str, str] = field(default_factory=dict)
    # Extra precompiled local plugins per role ("node", "wa"): folders laid out like the packaged
    # plugins (<Name>/<Name>.dll + <Name>.xml), installed next to the cluster plugin and enabled in
    # the role's profile (CLUSTER_EXTRA_PLUGINS, see _extra_plugins).
    extra_plugins: dict[str, list[Path]] = field(default_factory=lambda: _extra_plugins())
    nodes: list[NodeSpec] = field(default_factory=list)
    # T-0320: port slots of nodes that are gone, free for the next new node id. A new node reuses the
    # slot (ports) but never the id; its data dir stays on disk until `cluster nodes prune`.
    retired_nodes: list[NodeSpec] = field(default_factory=list)
    # T-0320: the hosts file (startup only): --hosts, else CLUSTER_HOSTS_FILE, else <root>/hosts.json,
    # else one built from regular_nodes/spare_nodes for this host.
    hosts_file: Path | None = field(default_factory=lambda: Path(os.environ["CLUSTER_HOSTS_FILE"]).expanduser()
                                    if os.environ.get("CLUSTER_HOSTS_FILE", "").strip() else None)
    host: str = field(default_factory=local_host)
    # T-0320: False keeps the positional start for every plain start() (the scenario bench: its scenarios
    # name node-1.., bring crashed nodes back under the same id, and run no launcher after start, which
    # node control would all refuse or flag). CLUSTER_NODE_CONTROL=0 is the environment form.
    node_control: bool = field(default_factory=lambda: os.environ.get("CLUSTER_NODE_CONTROL", "1") not in ("0", "false", ""))
    # T-0320: node ids are fresh at every start and every runtime spawn (spare refill, recycle
    # replacement), so a deployment's own SpaceEngineers-Dedicated.cfg must reach EVERY new data dir at
    # spawn. dedicated_template (CLUSTER_NODE_DEDICATED_TEMPLATE) is that file, used verbatim except for
    # the node's DedicatedId; dedicated_require (CLUSTER_NODE_DEDICATED_REQUIRE="Element=value,...") are
    # elements every node's cfg must carry before it may launch. Unset: the minimal built-in config.
    dedicated_template: Path | None = field(default_factory=lambda:
        Path(os.environ["CLUSTER_NODE_DEDICATED_TEMPLATE"]).expanduser()
        if os.environ.get("CLUSTER_NODE_DEDICATED_TEMPLATE", "").strip() else None)
    dedicated_require: dict[str, str] = field(default_factory=lambda: dict(
        pair.split("=", 1) for pair in os.environ.get("CLUSTER_NODE_DEDICATED_REQUIRE", "").split(",") if "=" in pair))

    def __post_init__(self) -> None:
        if self.transport_binaries is not None and self.transport_source is not None:
            raise ValueError("choose compiled Direct Transport or a source checkout, not both")
        if self.world_template is None:
            self.world_template = _packaged("examples/Cluster-Simple", self.gateway_repo / "Test/Saves/Cluster-Simple")
        if self.gateway_listen is None:
            self.gateway_listen = f"127.0.0.1:{27116 + self.port_base}"
        if self.gateway_control is None:
            self.gateway_control = f"127.0.0.1:{29416 + self.port_base}"
        self.from_node_set = not self.nodes and self.load_node_set()
        if not self.nodes:
            self.nodes = self.positional_nodes()

    def positional_nodes(self) -> list[NodeSpec]:
        """The initial layout from regular_nodes/spare_nodes (node-1..N+S, then wa-1)."""
        if not 1 <= self.regular_nodes <= 8:
            raise ValueError(f"regular_nodes must be 1..8, got {self.regular_nodes}")
        if not 0 <= self.spare_nodes <= 4:
            raise ValueError(f"spare_nodes must be 0..4, got {self.spare_nodes}")
        # wa-1 takes the port right after the last regular node instead of a
        # fixed 28420/29420, which used to cap the cluster at three regular
        # nodes by collision alone. With three nodes the layout is unchanged
        # (wa-1 on 28420/29420); with four it moves up to 28421/29421.
        # Spare slots are ordinary regular slots after the base ones; which slot is
        # currently the spare migrates at runtime via /registry/nodes/spare.
        total = self.regular_nodes + self.spare_nodes
        return [self.node_spec(f"node-{i}", i) for i in range(1, total + 1)] \
            + [self.node_spec("wa-1", total + 1, role="WA")]

    def node_spec(self, node_id: str, slot: int, role: str = "regular") -> NodeSpec:
        """The spec of a node in port slot ``slot`` (epoch = slot); ports follow the port plan. A multi-host
        start (T-0321) advertises the host's LAN address and its own port base; everything else loopback."""
        base, address = self.node_port_base, self.node_address
        return NodeSpec(node_id, slot, f"{address}:{28416 + base + slot}", f"{address}:{29416 + base + slot}", role=role)

    # -- T-0321 multi-host: the hosts file v2 decides where this host is -----------------------------

    def topology(self) -> dict | None:
        """The validated hosts file when it spans hosts (version 2, more than one host), else None: then
        every address stays loopback and every path is T-0320's (plan principle 3). Cached. A hosts file
        that does not validate reads as None here; the start reports its problems through hosts_spec."""
        if getattr(self, "_topology", False) is False:
            try:
                spec = self.hosts_spec()[0]
                self._topology = spec if is_multi_host(spec) else None
            except ClusterError:
                self._topology = None
        return self._topology

    @property
    def multi_host(self) -> bool:
        return self.topology() is not None

    def host_entry(self, name: str | None = None) -> dict:
        """One host of the multi-host file (this host by default); ClusterError when it is not in it."""
        topology = self.topology()
        name = name or self.host
        if topology is None or name not in topology["hosts"]:
            raise ClusterError(f"host {name!r} is not in the multi-host hosts file")
        return topology["hosts"][name]

    @property
    def node_address(self) -> str:
        """The address this host's nodes bind and advertise: its LAN address in a multi-host start."""
        topology = self.topology()
        if topology is not None and self.host in topology["hosts"]:
            return topology["hosts"][self.host]["address"]
        return "127.0.0.1"

    @property
    def node_port_base(self) -> int:
        topology = self.topology()
        if topology is not None and self.host in topology["hosts"]:
            return topology["hosts"][self.host]["portBase"]
        return self.port_base

    @property
    def gateway_bind(self) -> str:
        """Where the Gateway control listens and where this CLI sends its requests: the Gateway host's LAN
        address in a multi-host start (nodes on other hosts must reach it), else gateway_control."""
        topology = self.topology()
        if topology is None:
            return self.gateway_control
        gateway = topology["hosts"][topology["gateway"]]
        return f"{gateway['address']}:{29416 + gateway['portBase']}"

    @property
    def secrets(self) -> Path:
        """<root>/secrets (0700): the per-start join/admin/executor tokens of a multi-host start (0600 each)."""
        return self.root / "secrets"

    def read_secret(self, name: str) -> str | None:
        try:
            return (self.secrets / name).read_text().strip() or None
        except OSError:
            return None

    def admin_token(self) -> str | None:
        """CLUSTER_ADMIN_TOKEN, else (a multi-host start) the per-start admin token under <root>/secrets."""
        return os.environ.get("CLUSTER_ADMIN_TOKEN") or (self.read_secret("admin.token") if self.multi_host else None)

    def executor_token_path(self, name: str | None = None) -> Path:
        entry = self.host_entry(name)
        path = Path(entry["executorTokenFile"]).expanduser()
        return path if path.is_absolute() else Path(entry["runRoot"] or self.root) / path

    # -- the launcher's port-slot ledger (T-0312, T-0320) ---------------------
    # The positional layout above (node-1..N+S, wa-1 right after) is only the INITIAL set.
    # <root>/node-set.json records which node id holds which port slot (live entries) and which slots
    # are free again (retired entries). Since T-0320 it is NOT the source of the node set - the
    # registry's node plan is - only the launcher's local record of ports, so a later `stop`, `status`
    # or a harness restart finds the processes it started on the ports it gave them.

    def hosts_spec(self) -> tuple[dict, str, str]:
        """(validated hosts file, where it came from, sha256). Missing file: one built from
        regular_nodes/spare_nodes for this host; an explicitly named file that is missing is an error."""
        path = self.hosts_file
        if path is None and (self.root / "hosts.json").is_file():
            path = self.root / "hosts.json"
        if path is None and (self.root / "hostd" / "hosts.json").is_file():
            # T-0321: a node host has no <root>/hosts.json; the start left its copy for cluster-hostd. Only a
            # host the copy names as a non-gateway host reads it (the gateway host's own file is <root>/hosts.json).
            try:
                copy = json.loads((self.root / "hostd" / "hosts.json").read_text())
                if isinstance(copy, dict) and self.host in (copy.get("hosts") or {}) and copy.get("gateway") != self.host:
                    path = self.root / "hostd" / "hosts.json"
            except (OSError, ValueError):
                pass
        if path is None:
            spec = synthesize_hosts_file(self.regular_nodes, self.spare_nodes, self.host)
            raw = json.dumps(spec, sort_keys=True).encode()
            return validate_hosts_file(spec, self.host, self.root), f"--nodes {self.regular_nodes} --spares {self.spare_nodes}", \
                hashlib.sha256(raw).hexdigest()
        try:
            raw = Path(path).read_bytes()
        except OSError as error:
            raise ClusterError(f"hosts file {path}: {error.strerror or error}") from None
        try:
            data = json.loads(raw)
        except json.JSONDecodeError as error:
            raise ClusterError(f"hosts file {path}: not JSON ({error})") from None
        return validate_hosts_file(data, self.host, self.root), str(path), hashlib.sha256(raw).hexdigest()

    @property
    def node_set_path(self) -> Path:
        return self.root / "node-set.json"

    def slot_of(self, node: NodeSpec) -> int:
        return int(node.backend.rpartition(":")[2]) - 28416 - self.node_port_base

    def load_node_set(self) -> bool:
        """Replace nodes/retired_nodes from node-set.json; False when there is none."""
        try:
            data = json.loads(self.node_set_path.read_text())
        except FileNotFoundError:
            return False
        def spec(entry: dict) -> NodeSpec:
            node = self.node_spec(entry["id"], int(entry["slot"]), role=entry.get("role", "regular"))
            node.epoch = int(entry.get("epoch", node.epoch))
            return node
        self.nodes = [spec(entry) for entry in data["nodes"] if not entry.get("retired")]
        self.retired_nodes = [spec(entry) for entry in data["nodes"] if entry.get("retired")]
        return True

    def save_node_set(self) -> None:
        entries = [{"id": n.node_id, "slot": self.slot_of(n), "epoch": n.epoch, "role": n.role} for n in self.nodes] \
            + [{"id": n.node_id, "slot": self.slot_of(n), "epoch": n.epoch, "role": n.role, "retired": True}
               for n in self.retired_nodes]
        tmp = self.node_set_path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"version": 1, "nodes": entries}, indent=1) + "\n")
        tmp.replace(self.node_set_path)

    def reload_node_set(self) -> None:
        """Pick up another process's scale step (the rotator reloads before every pass)."""
        if self.node_set_path.exists():
            self.load_node_set()

    def add_node(self, node_id: str, role: str = "regular") -> NodeSpec:
        """Append a node in the next free port slot (a replacement WA, an extra regular node) and
        return it; start it with Cluster.start_node."""
        if any(node.node_id == node_id for node in self.nodes):
            raise ValueError(f"node {node_id} already exists")
        spec = self.node_spec(node_id, max(node.epoch for node in self.nodes) + 1, role=role)
        self.nodes.append(spec)
        return spec

    @property
    def launcher(self) -> Path:
        return self.magnetar / "MagnetarInterim.bin"

    @property
    def world_tool(self) -> Path:
        override = os.environ.get("MAGNETAR_WORLD_TOOL")
        if override:
            return Path(override).expanduser()
        if ARCHIVE:
            return ARCHIVE / "tools/MagnetarWorld/MagnetarWorld.dll"
        return self.plugin_source / "Tools/MagnetarWorld/bin/Release/net10.0/MagnetarWorld.dll"

    # The game assemblies that define the replicated type table, in the order the node plugin
    # hashes them (ClusterPluginHost.ReplicationAssemblies); the Gateway admits a node only when
    # the digests agree, so both sides must read the same DedicatedServer64.
    REPLICATION_ASSEMBLIES = ("Sandbox.Common.dll", "Sandbox.Game.dll", "SpaceEngineers.Game.dll",
                              "SpaceEngineers.ObjectBuilders.dll", "VRage.dll", "VRage.Game.dll", "VRage.Library.dll")

    def replication_table_hash(self) -> str:
        digest = hashlib.sha256()
        for name in self.REPLICATION_ASSEMBLIES:
            digest.update((self.ds64 / name).read_bytes())
        return digest.hexdigest().upper()

    def magnetar_config(self, node: "NodeSpec") -> Path:
        return self.root / f"{node.node_id}-magnetar"

    def node_data(self, node: "NodeSpec") -> Path:
        return self.root / f"{node.node_id}-data"

    @property
    def split_world(self) -> Path:
        return self.root / "cluster-world"

    def premade_world(self, node: "NodeSpec") -> Path:
        """The node's own copy of the split world, loaded as its premade checkpoint. The game
        REWRITES SANDBOX_0_0_0_.sbsB5 whenever the binary sector deserializes without objects -
        and the split world's sector has none - so nodes loading one shared directory read each
        other's half-written file ("Attempted to read past the end of the stream", P2-CHR-02,
        2026-09-17). CLUSTER_WORLD (the saves and the catalog seed) stays the shared split world."""
        return self.node_data(node) / "premade-world"

    @property
    def dumps(self) -> Path:
        return self.root / "dumps"


#: Writes {path: {content, mode}} (stdin, JSON) under the run root given as argv[1], atomically, parent
#: directories 0700. Runs on the target host through `python3 -c`, so the secrets never touch a command line.
UNPACK_BUNDLE = r"""
import json, os, sys
root = sys.argv[1]
bundle = json.load(sys.stdin)
for rel, item in bundle.items():
    path = rel if rel.startswith("/") else os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, item["mode"])
    with os.fdopen(fd, "w") as handle:
        handle.write(item["content"])
    os.chmod(tmp, item["mode"])
    os.replace(tmp, path)
print(len(bundle))
"""
#: sha256 over a directory tree (relative paths and contents, sorted); the same code on both ends.
TREE_HASH = r"""
import hashlib, os, sys
root = sys.argv[1]
digest = hashlib.sha256()
for folder, dirs, files in os.walk(root):
    dirs.sort()
    for name in sorted(files):
        path = os.path.join(folder, name)
        digest.update(os.path.relpath(path, root).encode() + b"\0")
        with open(path, "rb") as handle:
            for chunk in iter(lambda: handle.read(1 << 20), b""):
                digest.update(chunk)
        digest.update(b"\0")
print(digest.hexdigest())
"""


def ssh_argv_base() -> list[str]:
    """ssh without a prompt: BatchMode, a bounded connect, keep-alives (the nightly LAN window)."""
    return ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=4"]


def ssh_argv(target: str) -> list[str]:
    return [*ssh_argv_base(), target]


def remote_path(path: str) -> str:
    """A path for a remote shell: ~/x expands there (never here), everything else quoted."""
    if path.startswith("~/"):
        return '"$HOME"/' + shlex.quote(path[2:])
    return shlex.quote(path)


def tree_hash(root: Path) -> str:
    done = subprocess.run([sys.executable, "-c", TREE_HASH, str(root)], capture_output=True, text=True, check=True)
    return done.stdout.strip()


def write_files(root: Path, files: dict[str, tuple[str, int]]) -> None:
    """The local half of Cluster.write_bundle: same layout, same modes."""
    for rel, (content, mode) in files.items():
        path = Path(rel) if rel.startswith("/") else root / rel
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        tmp = path.with_name(path.name + ".tmp")
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
        with os.fdopen(fd, "w") as handle:
            handle.write(content)
        os.chmod(tmp, mode)
        tmp.replace(path)


def archive_identity(archive: Path | None) -> str | None:
    """The commit the deployment archive was built from (manifest.json), None in a checkout."""
    if archive is None:
        return None
    try:
        return json.loads((archive / "manifest.json").read_text()).get("commit") or None
    except (OSError, ValueError):
        return None


class ClusterError(RuntimeError):
    pass


class ClusterHttpError(ClusterError):
    """An HTTP error answer: ``status`` and the body's admin error ``code`` when it carries one."""

    def __init__(self, message: str, status: int, body: str = "") -> None:
        super().__init__(message)
        self.status = status
        self.body = body
        try:
            self.code = (json.loads(body).get("error") or {}).get("code")
        except (ValueError, AttributeError):
            self.code = None


class Cluster:
    """Launches, drives and tears down one test cluster."""

    # Rotated per-process logs kept per name (see _spawn); at one node relaunch
    # every ~20 rotator minutes this covers the last ~7 hours of a run.
    ROTATED_LOGS = 20

    def __init__(self, config: Config) -> None:
        self.config = config
        self.processes: dict[str, subprocess.Popen] = {}
        #: Slot -> wall clock of its most recent spawn. plugin_load_failures needs it to tell
        #: this start's Magnetar log from the previous starts the config dir keeps.
        self.spawned_at: dict[str, float] = {}
        #: T-0320 launcher state: the held <root>/launcher.lock, when a Kill slot was first seen with its
        #: process still up (the self-exit grace), the current spawn attempt per node id, and the
        #: one-time notes already printed.
        self._launcher_lock = None
        self._kill_seen: dict[str, float] = {}
        self._attempts: dict[str, dict] = {}
        self._noted: set[str] = set()
        config.root.mkdir(parents=True, exist_ok=True)
        config.dumps.mkdir(parents=True, exist_ok=True)

    # -- process discovery -------------------------------------------------
    # Findings 2026-07-27: `nohup python3 … &` returns the WRAPPER's pid, and killing that leaves the
    # supervisor and its nodes running - which then had a second `prepare()` rewrite the world under
    # them. Find processes by command line, never by a remembered pid.

    def _pgrep(self, pattern: str) -> list[int]:
        found = subprocess.run(["pgrep", "-f", pattern], capture_output=True, text=True)
        return [int(pid) for pid in found.stdout.split()]

    def running_pids(self) -> list[int]:
        # A trailing slash, so ~/runs/a does not also match ~/runs/a-2. ClusterGateway is the
        # process's name before the ClusterRegistry rename: a run started from an older package.
        marker = re.escape(f"{self.config.root}/")
        return sorted(set(self._pgrep(f"MagnetarInterim.*{marker}") + self._pgrep(f"Cluster(Registry|Gateway).*{marker}")))

    # -- lifecycle ---------------------------------------------------------

    def require_plugin_sdk(self) -> None:
        """Check the actual SDK metadata before launching or resetting anything (no game startup)."""
        sdk = self.config.launcher.parent / "Libraries/MagnetarInterim/PluginSdk.dll"
        if not sdk.is_file():
            raise ClusterError(f"required PluginSdk.dll not found: {sdk}; install Magnetar 2.4.2.1 or newer")
        stamp = (str(sdk), sdk.stat().st_mtime_ns, sdk.stat().st_size)
        if getattr(self, "_verified_sdk", None) == stamp:
            return
        gateway_dll = os.environ.get("MAGNETAR_GATEWAY_DLL")
        command = (["dotnet", str(Path(gateway_dll).expanduser())] if gateway_dll else
                   ["dotnet", "run", "--project", str(self.config.gateway_repo / "ClusterRegistry"), "--"])
        checked = subprocess.run(command + ["--check-plugin-sdk", str(sdk)], capture_output=True, text=True, timeout=60)
        if checked.returncode:
            raise ClusterError("Magnetar SDK is incompatible: " + (checked.stderr or checked.stdout).strip())
        self._verified_sdk = stamp

    def node_data_dirs(self) -> list[Path]:
        """Every node data dir under the run root, live or not: with never-reused node ids (T-0320) the
        ledger no longer names every dir an earlier node left behind."""
        if not self.config.root.is_dir():
            return []
        return sorted(path for path in self.config.root.iterdir()
                      if path.is_dir() and re.fullmatch(r"(node|wa)-\d+-data", path.name))

    def existing_cluster(self) -> list[Path]:
        """The durable state of a cluster in this run root: its registry and its node data dirs."""
        return [path for path in [self.config.root / "gateway-registry",
                                  *(self.config.node_data(node) for node in self.config.nodes), *self.node_data_dirs()]
                if path.exists()]

    def require_wipe_for_existing(self, wipe: bool) -> None:
        """`fresh` erases a cluster only when told to: over an existing one it needs `--wipe`. Without
        an existing cluster there is nothing to erase and `fresh` needs no flag."""
        existing = self.existing_cluster()
        if existing and not wipe:
            raise ClusterError(
                f"run root {self.config.root} holds an existing cluster ({', '.join(path.name for path in existing)})."
                " `fresh` would ERASE it; pass `fresh --wipe` to do that deliberately.")

    def prepare(self, wipe: bool = False) -> None:
        """Reset the working world to the template. Refuses to run over a live cluster, and over an
        existing (stopped) one unless wipe=True."""
        self.require_wipe_for_existing(wipe)
        self.require_plugin_sdk()
        # A scenario that restarts its cluster calls kill() immediately before prepare(); give the
        # SIGKILLed listeners a moment to actually release their ports before deciding it is live.
        deadline = time.time() + 15
        while True:
            live = [endpoint for endpoint in [self.config.gateway_bind, self.config.gateway_listen]
                    + [node.control for node in self.config.nodes]
                    if port_open(*split_endpoint(endpoint))]
            if not live:
                break
            if time.time() >= deadline:
                raise ClusterError(f"a cluster is already up on {', '.join(live)}; stop it before `fresh`")
            time.sleep(1)
        if not self.config.launcher.exists():
            raise ClusterError(f"Magnetar launcher not found: {self.config.launcher} (MAGNETAR_HOME)")
        if not (self.config.ds64 / "SpaceEngineersDedicated.exe").exists():
            raise ClusterError(f"DedicatedServer64 not found: {self.config.ds64} (SPACE_ENGINEERS_BIN64)")
        # Clean slate means the registry too. Leaving the WAL behind while wiping the node data dirs
        # points a fresh World Authority at a catalogued global save whose file no longer exists, and
        # it correctly refuses to start rather than load a world it cannot verify.
        for stale in [self.config.split_world, self.config.root / "gateway-registry",
                      *(self.config.root / f"{node.node_id}-data" for node in [*self.config.nodes, *self.config.retired_nodes]),
                      *self.node_data_dirs()]:
            if stale.exists():
                shutil.rmtree(stale)
        # T-0312: the node set belonged to the erased cluster; a fresh one starts from regular/spare_nodes.
        self.config.node_set_path.unlink(missing_ok=True)
        if self.config.from_node_set:
            self.config.nodes, self.config.retired_nodes, self.config.from_node_set = self.config.positional_nodes(), [], False
        if not self.config.world_tool.exists():
            project = self.config.plugin_source / "Tools/MagnetarWorld/MagnetarWorld.csproj"
            if not project.exists():
                raise ClusterError(f"world tool not found: {self.config.world_tool} (MAGNETAR_WORLD_TOOL)")
            subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo"], check=True)
        subprocess.run(["dotnet", str(self.config.world_tool), self.config.split_step,
                        str(self.config.world_template), str(self.config.split_world)],
                       check=True, env=self._tool_env())

    def _tool_env(self) -> dict[str, str]:
        """The world tool resolves the game assemblies from the DedicatedServer64 and reads the
        subtype names from the game content beside it."""
        return {**os.environ, "SPACE_ENGINEERS_BIN64": str(self.config.ds64),
                "SPACE_ENGINEERS_ROOT": str(self.config.ds64.parent),
                "MAGNETAR_WORLD_LOG": str(self.config.root / "world-tool.log")}

    def _node_env(self, node: NodeSpec, standalone: bool = False) -> dict[str, str]:
        env = {
            **os.environ,
            **CRASH_ENV,
            "DOTNET_DbgMiniDumpName": str(self.config.dumps / f"{node.node_id}.%p.dmp"),
            "CLUSTER_JOIN_TOKEN": JOIN_TOKEN,
            "CLUSTER_ID": CLUSTER_ID,
            "CLUSTER_WORLD_ID": WORLD_ID,
            "CLUSTER_CONFIG_HASH": CONFIG_HASH,
            "CLUSTER_PROJECTILE_DISTANCE_CAP": str(self.config.sync_distance),
            "SE_DIRECT_TRANSPORT": "1",
            "CLUSTER_NODE_EPOCH": str(node.epoch),
            "CLUSTER_NODE_ID": node.node_id,
            # T-0320: the host the node reports at registration. The registry matches nodes to the hosts
            # file's host keys by it; without it the node sends its machine name, which may differ.
            "CLUSTER_NODE_HOST": self.config.host,
            "CLUSTER_NODE_ENDPOINT": node.backend,
            "CLUSTER_GATEWAY_REGISTRY": f"http://{self.config.gateway_control}",
            "CLUSTER_CONTROL_PORT": node.control.rpartition(":")[2],
            "CLUSTER_WORLD": str(self.config.split_world),
            "CLUSTER_DATA_ROOT": str(self.config.node_data(node)),
            # PluginSdk's PluginStorage (T-0256): one directory every node and the WA share. The nodes of
            # this launcher all run on one host, so a directory under the root is shared by construction.
            "CLUSTER_SHARED_ROOT": str(self.config.root / "shared"),
        }
        if node.role == "WA":
            env["CLUSTER_NODE_ROLE"] = "WA"
        # A standalone prewarm node never joins (no Gateway is up) and runs before `cluster start` has minted
        # this start's tokens: it gets the single-host env (r64 prewarm failed on the missing join.token).
        if self.config.multi_host and not standalone:
            env.update(self._multi_host_node_env(node))
            env.pop("CLUSTER_SHARED_ROOT", None)
        env.update(self.config.node_env)
        env.update(self.config.node_env_by_id.get(node.node_id, {}))
        return env

    def _multi_host_node_env(self, node: NodeSpec) -> dict[str, str]:
        """T-0321 (PLAN §2): a node of a multi-host start binds and advertises its host's LAN address, reaches
        the registry on the Gateway host's, and carries this start's tokens. No CLUSTER_SHARED_ROOT: a
        directory under one host's run root is not shared across hosts (F3)."""
        address = self.config.node_address
        control_port = node.control.rpartition(":")[2]
        env = {
            "CLUSTER_GATEWAY_REGISTRY": f"http://{self.config.gateway_bind}",
            "CLUSTER_CONTROL_BIND": address,
            "CLUSTER_NODE_CONTROL_ENDPOINT": f"http://{address}:{control_port}",
            "CLUSTER_INTERNAL_NETWORKS": ",".join(self.config.topology()["network"]["internalNetworks"]),
        }
        for name, key in (("CLUSTER_JOIN_TOKEN", "join.token"), ("CLUSTER_ADMIN_TOKEN", "admin.token")):
            value = self.config.read_secret(key)
            if not value:
                raise ClusterError(f"multi-host start: {self.config.secrets / key} is missing; `cluster start` on the"
                                   " gateway host mints and distributes it")
            env[name] = value
        return env

    def _gateway_env(self) -> dict[str, str]:
        return {
            **os.environ,
            **CRASH_ENV,
            "DOTNET_DbgMiniDumpName": str(self.config.dumps / "gateway.%p.dmp"),
            "CLUSTER_ALLOW_TEST_ACCEPT_ALL": "true",
            "CLUSTER_JOIN_TOKEN_CURRENT": JOIN_TOKEN,
            "CLUSTER_ID": CLUSTER_ID,
            "CLUSTER_WORLD_ID": WORLD_ID,
            "CLUSTER_CONFIG_HASH": CONFIG_HASH,
            "CLUSTER_BINARY_VERSION": BINARY_VERSION,
            "CLUSTER_REPLICATION_TABLE_HASH": self.config.replication_table_hash(),
            "CLUSTER_WORLD": str(self.config.split_world),
            "CLUSTER_SYNC_DISTANCE": str(self.config.sync_distance),
            **self._multi_host_gateway_env(),
            **self.config.gateway_env,
        }

    def _multi_host_gateway_env(self) -> dict[str, str]:
        """T-0321: this start's join token, the admin token its LAN control bind requires, the executor
        token file (one Executor-scope token per host) and the internal networks. Empty on one host."""
        if not self.config.multi_host:
            return {}
        topology = self.config.topology()
        env = {"CLUSTER_INTERNAL_NETWORKS": ",".join(topology["network"]["internalNetworks"]),
               "CLUSTER_ADMIN_TOKENS": str(self.config.secrets / "admin-tokens.json"),
               # INTERFACES.md [core]: remote = registered host != this; the start id keeps this start's tokens
               # valid across a gateway-only restart; the nightly window holds remote leases.
               "CLUSTER_GATEWAY_HOST": topology["gateway"]}
        start_id = self.config.read_secret("start-id")
        if start_id:
            env["CLUSTER_START_ID"] = start_id
        if topology["network"]["networkWindows"]:
            env["CLUSTER_NETWORK_WINDOWS"] = ",".join(topology["network"]["networkWindows"])
        for name, key in (("CLUSTER_JOIN_TOKEN_CURRENT", "join.token"), ("CLUSTER_ADMIN_TOKEN", "admin.token")):
            value = self.config.read_secret(key)
            if not value:
                raise ClusterError(f"multi-host start: {self.config.secrets / key} is missing")
            env[name] = value
        return env

    def _spawn(self, name: str, argv: list[str], env: dict[str, str], cwd: Path | None = None) -> subprocess.Popen:
        # Truncate, never append: wait_ready searches these files for startup markers, and a previous
        # run's "Game ready" makes it return before this cluster is up - which then fails as a
        # connection refused somewhere unrelated, minutes later.
        #
        # But rotate the old log aside first. A node relaunch (rotation does one every ~20
        # minutes) used to DESTROY the previous epoch's log, which is exactly the evidence a
        # post-mortem needs - the world-12 import-abort storm (2026-08-18) lost every
        # per-residual handover log line that way. Bounded: the newest ROTATED_LOGS are kept.
        log_path = self.config.root / f"{name}.log"
        if log_path.exists() and log_path.stat().st_size > 0:
            log_path.rename(self.config.root / f"{name}.log.{time.strftime('%Y%m%d-%H%M%S')}")
            for stale in sorted(self.config.root.glob(f"{name}.log.*"))[:-self.ROTATED_LOGS]:
                stale.unlink()
        log = open(log_path, "wb", buffering=0)
        # Record the exit code in the process's own log. A gateway or node that
        # goes away without an unhandled exception (and so without a minidump)
        # otherwise leaves nothing at all to tell a crash from a clean exit or a
        # signal - which is exactly what a silent gateway exit looked like
        # during node-rotation testing on 2026-08-14.
        # CLUSTER_CORE_DUMPS=1 additionally lifts the core limit, for the native
        # faults (SIGBUS/SIGSEGV) that produce no managed minidump at all.
        wrapped = ["bash", "-c",
                   '[ -n "$CLUSTER_CORE_DUMPS" ] && ulimit -c unlimited; '
                   '"$@"; code=$?; printf "[cluster] %s exited with %s\\n" "$0" "$code"; exit "$code"',
                   name, *argv]
        try:
            process = subprocess.Popen(wrapped, env=env, stdout=log, stderr=subprocess.STDOUT,
                                       start_new_session=True, cwd=cwd)
        finally:
            log.close()
        self.processes[name] = process
        self.spawned_at[name] = time.time()
        return process

    def require_clean_run_root(self) -> None:
        """TestPlan §1.7 / Orchestration §5.1: a relaunch of fresh nodes against a previous session's
        registry gets fenced exactly as the epoch protocol intends - correct behavior, wrong operation,
        observed as a wedged cluster that binds its ports and admits nobody. A run root that still has
        its node data is a warm restart and is reused by default; only a registry with NO node data
        beside it (the fresh-nodes case) is refused."""
        registry = self.config.root / "gateway-registry"
        stale = [candidate for candidate in (registry / "registry.db", registry / "registry.wal", registry / "registry.json")
                 if candidate.exists()]
        if not stale:
            return
        if any(self.config.node_data(node).exists() for node in self.config.nodes) or self.node_data_dirs():
            print(f"[cluster] reusing run root {self.config.root} (warm restart)")
            return
        raise ClusterError(
            f"run root {self.config.root} still holds a previous session's registry ({', '.join(p.name for p in stale)}"
            f" under {registry}) but no node data. Starting fresh nodes against it would fence them into a"
            " half-dead cluster. `cluster fresh --wipe` erases it and starts a new world.")

    def start(self, nodes: list[str] | None = None) -> None:
        self.require_plugin_sdk()
        self.require_clean_run_root()
        multi = self.config.multi_host
        if multi:
            if nodes is not None or not self.config.node_control:
                raise ClusterError("a multi-host hosts file needs node control: no explicit node selection and no"
                                   " CLUSTER_NODE_CONTROL=0")
            self.prepare_multi_host()
        # MAGNETAR_GATEWAY_DLL runs a packaged gateway build; the default builds and runs from source.
        gateway_dll = os.environ.get("MAGNETAR_GATEWAY_DLL")
        gateway = (["dotnet", str(Path(gateway_dll).expanduser())] if gateway_dll else
                   ["dotnet", "run", "--project", str(self.config.gateway_repo / "ClusterRegistry"), "--"])
        # The legacy two-node arguments; a multi-host ledger may hold fewer than two local nodes.
        first, second = (self.config.nodes[:2] if len(self.config.nodes) >= 2 or not multi
                         else [self.config.node_spec("node-1", 1), self.config.node_spec("node-2", 2)])
        gateway += [
            "--direct", "--listen", self.config.gateway_listen,
            "--backend", first.backend,
            "--control", self.config.gateway_bind,
            "--registry", str(self.config.root / "gateway-registry"),
            "--lease-ttl-seconds", str(self.config.lease_ttl_seconds),
            "--paused",
            "--source-control", first.control,
            "--target-control", second.control,
            "--target-backend", second.backend,
        ]
        # Admission policy is not a Steam concern: the member limit and the
        # administrator list apply to Direct Transport clients just as much, and
        # without --admission the Gateway falls back to AdmissionConfig's default
        # 16-member cap and rejects the 17th client with ServerFull (seen with 24
        # pilots the moment the Steam frontend was turned off, 2026-08-14).
        admission = self.config.root / "admission.json"
        admission.write_text(json.dumps({
            "serverName": self.config.server_name, "worldName": self.config.world_template.name,
            "gameMode": "C", "syncDistance": self.config.sync_distance,
            "memberLimit": self.config.member_limit, "familySharing": True,
            "administrators": self.config.administrators,
            **({"kickTimeoutSeconds": self.config.kick_timeout_seconds}
               if self.config.kick_timeout_seconds is not None else {})}))
        gateway += ["--admission", str(admission)]
        if self.config.steam_listen:
            gateway += ["--steam", self.config.steam_listen]
        # T-0229: CLUSTER_RELAY=external moves the packet path to gateway-rs, which connects to the
        # Gateway's relay socket; the C# relay stays in the Gateway as the reference and fallback.
        relay_socket = self.config.root / "gateway-relay.sock"
        external = RELAY == "external"
        if external:
            binary = self.gateway_rs_binary()
            gateway += ["--relay", "external", "--relay-socket", str(relay_socket)]
        gateway += self.config.gateway_args
        self._spawn("gateway", gateway, self._gateway_env())
        if external:
            self._spawn_gateway_rs(binary, relay_socket)
        # T-0320: a plain start applies the hosts file and runs the registry's node plan with fresh node
        # ids. An explicit node selection (the test bench's staged starts), Config.node_control=False
        # (the scenario bench) and a registry without node control keep the positional start below.
        if nodes is None and self.config.node_control and self.apply_hosts_file():
            if multi:
                self._start_multi_host()
            else:
                self._start_planned()
            return
        if multi:
            raise ClusterError("this registry has no node control; a multi-host start needs it")
        self._mark_spares()
        selected = [node for node in self.config.nodes
                    if nodes is None or node.node_id in nodes]
        # T-0031: bring ONE Magnetar node fully up before the rest start together.
        #
        # The plugin caches are now SHARED per host - Pulsar/NuGet and NuGet's
        # http-cache both point at one directory per host instead of one per instance -
        # which is what stops every instance re-fetching the same packages and getting
        # the public IP rate-limited. Unlike the GitHub cache, those two are WRITTEN at
        # runtime, so a cold start with six nodes launching at once would have six
        # processes restoring into one directory simultaneously. NuGet takes per-package
        # locks, but the failure this avoids is a SECOND process reading a partially
        # extracted package, which locking does not address.
        #
        # One serial start closes the window: the first node populates the cache alone,
        # and everything after it reads a cache that is already complete. It costs one
        # node's startup on a COLD boot and nothing at all once the cache is warm.
        #
        # The WA is the natural choice - it is a Magnetar node like any other, it is not
        # a regular that could be designated a spare, and nothing waits on it.
        #
        # wait_log rather than wait_ready on purpose: wait_ready ends by POSTing /resume,
        # which would un-pause the gateway while five nodes are still loading and let
        # clients attach to a node that has no world yet.
        warm = next((node for node in selected if node.role == "WA"), None)
        if warm is None and selected:
            warm = selected[0]
        # A single selected node needs no serial warm-up; it starts in the loop below like any other.
        if len(selected) <= 1 or not SERIAL_WARMUP:
            warm = None
        if warm is not None:
            self._warm_up(warm)
            self.propagate_sources(warm, selected)
        for node in selected:
            if warm is not None and node.node_id == warm.node_id:
                continue
            self.start_node(node.node_id)

    def _warm_up(self, warm: NodeSpec) -> None:
        """Start ``warm`` alone with updates allowed and wait for Game ready (T-0031), restarting it once
        when a plugin its profile declares failed to load (T-0102)."""
        print(f"[cluster] warming the shared plugin caches with {warm.node_id} before the rest "
              "(the only node allowed to update)")
        self.start_node(warm.node_id, allow_update=True)
        self.wait_node_log(warm.node_id, "Game ready", timeout=SERIAL_WARMUP_TIMEOUT)
        # T-0102. The warm-up node is the ONE instance whose plugin fetch can time out:
        # it is the only one allowed to update, so it is the only one that goes to the
        # network, and everything after it reads the cache it just filled. When its own
        # fetch loses that race it runs the whole world without the plugin, silently.
        #
        # Retrying just this node is the repair, and it is cheap here: nothing waits on
        # the WA yet, no client is attached (the gateway is still --paused), and the
        # cache the first attempt was populating is now warm - the observed 2026-09-08
        # failure still wrote Bin/plugin.dll, it only wrote it 13 s too late for its own
        # load. allow_update stays on for the retry so a cache that is genuinely absent
        # can still be fetched rather than failing again for a new reason.
        if self._report_plugin_failures(warm) and WARMUP_PLUGIN_RETRY:
            print(f"[cluster] restarting {warm.node_id} once so it loads from the now-warm cache")
            self.kill(warm.node_id)
            self.start_node(warm.node_id, allow_update=True)
            self.wait_node_log(warm.node_id, "Game ready", timeout=SERIAL_WARMUP_TIMEOUT)
            if self._report_plugin_failures(warm):
                raise ClusterError(
                    f"{warm.node_id} failed to load a declared plugin twice; the second attempt"
                    " read a warm cache, so this is not a transient fetch timeout. Starting the"
                    " rest of the cluster would spread a build that is missing a plugin its"
                    " profile declares. Set CLUSTER_WARMUP_PLUGIN_RETRY=0 to start anyway.")

    def _wait_gateway_control(self, purpose: str, timeout: float = 120) -> None:
        host, port = split_endpoint(self.config.gateway_bind)
        deadline = time.time() + timeout
        while not port_open(host, port):
            if time.time() > deadline:
                raise ClusterError(f"gateway control did not come up to accept {purpose}")
            time.sleep(0.5)

    def apply_hosts_file(self, timeout: float = 120) -> bool:
        """POST the hosts file to the registry (start/fresh only - a Gateway-only restart keeps the
        runtime targets). True when applied; False when the registry has no node control (404), in
        which case the caller falls back to the positional start."""
        spec, source, sha256 = self.config.hosts_spec()
        self._wait_gateway_control("the hosts file")
        body = hosts_file_request(spec, sha256)
        deadline = time.time() + timeout
        while True:
            try:
                result = self._admin_mutation("POST", "/admin/v1/node-control/hosts-file", body)
                break
            except ClusterHttpError as error:
                if error.status == 404:
                    print("[cluster] this registry has no node control (404 on /admin/v1/node-control/hosts-file);"
                          " positional start")
                    return False
                if error.status != 503 or time.time() > deadline:
                    raise
            except ClusterError:
                if time.time() > deadline:
                    raise
            time.sleep(1)
        counts = ", ".join(f"{name} {entry['serving']}+{entry['spares']}" for name, entry in spec["hosts"].items())
        print(f"[cluster] hosts file applied from {source} ({counts}; sha256 {sha256[:12]})"
              + "".join(f"; warning: {warning}" for warning in as_list(result.get("warnings"))))
        return True

    def _start_planned(self) -> None:
        """The node-control start: every regular node of the previous run leaves the ledger (fresh ids,
        its slot free again), the WA warms the shared caches alone, then actualize_step starts every
        node the plan wants on this host."""
        self.acquire_launcher_lock()
        for spec in [node for node in self.config.nodes if node.role == "regular"]:
            self.config.nodes.remove(spec)
            self.config.retired_nodes.append(spec)
        self.config.save_node_set()
        wa = next((node for node in self.config.nodes if node.role == "WA"), None)
        if wa is not None:
            if SERIAL_WARMUP:
                self._warm_up(wa)
            else:
                self.start_node(wa.node_id)
        summary = self.actualize_step()
        print(f"[cluster] node plan: started {', '.join(summary['spawned']) or 'nothing'}"
              + (f"; refused {', '.join(summary['refused'])}" if summary["refused"] else ""))
        print("[cluster] node control is active: a launcher on this host must keep heartbeating (the harness"
              " rotator, or `cluster nodes ... --wait`), or within 60 s the registry reports executorSilence"
              " and no spare is refilled")

    # -- T-0321 multi-host start (PLAN.md §1, §2, §4) --------------------------------------------------
    # The Gateway host runs `cluster start`: it checks every other host, mints this start's tokens, writes
    # them (0600, never on a command line) with the launch spec to every host's run root over ssh, stages the
    # split world there, starts the Gateway and the WA itself, and then one cluster-hostd per host, which
    # carries out that host's part of the node plan through the fenced executor lease.

    def run_on(self, host: str, script: str, input: bytes | None = None, timeout: float = 120,
               check: bool = True) -> subprocess.CompletedProcess:
        """A shell script on a host of the hosts file: locally for ssh=local, else over ssh (BatchMode, no
        password prompt). Secrets only ever travel on stdin. The single seam the tests replace."""
        target = self.config.host_entry(host)["ssh"]
        argv = ["bash", "-c", script] if target == "local" else [*ssh_argv(target), script]
        done = subprocess.run(argv, input=input, capture_output=True, timeout=timeout)
        if check and done.returncode:
            raise ClusterError(f"{host}: {script[:160]} -> exit {done.returncode}: "
                               f"{done.stderr.decode(errors='replace').strip()[-400:]}")
        return done

    def host_cli(self, host: str, args: list[str], timeout: float = 120, check: bool = True) -> subprocess.CompletedProcess:
        """This CLI on a host: the Gateway host runs this very file; every other host sources its serverEnv
        (T-0315: CLUSTER_ARCHIVE, SPACE_ENGINEERS_SERVER, DOTNET_ROOT, ...) and runs its synced archive's
        cli/cluster (the same archive: the version gate checks it)."""
        entry = self.config.host_entry(host)
        root = entry["runRoot"] or str(self.config.root)
        rest = " ".join(shlex.quote(arg) for arg in args)
        if entry["ssh"] == "local":
            script = (f"CLUSTER_NODE_HOST={shlex.quote(host)} {shlex.quote(sys.executable)}"
                      f" {shlex.quote(str(Path(__file__).resolve()))} --root {shlex.quote(root)} {rest}")
        else:
            script = (f". {remote_path(entry['serverEnv'] or DEFAULT_SERVER_ENV)} && CLUSTER_NODE_HOST={shlex.quote(host)}"
                      f' exec "$CLUSTER_ARCHIVE/cli/cluster" --root {shlex.quote(root)} {rest}')
        return self.run_on(host, script, timeout=timeout, check=check)

    def remote_hosts(self) -> list[str]:
        topology = self.config.topology() or {"hosts": {}}
        return [name for name, entry in topology["hosts"].items() if entry["ssh"] != "local"]

    def write_bundle(self, host: str, files: dict[str, tuple[str, int]]) -> None:
        """{path relative to the host's run root (or absolute): (content, mode)} written atomically on a host,
        directories 0700; contents on stdin."""
        entry = self.config.host_entry(host)
        root = entry["runRoot"] or str(self.config.root)
        payload = json.dumps({path: {"content": content, "mode": mode} for path, (content, mode) in files.items()})
        self.run_on(host, f"python3 -c {shlex.quote(UNPACK_BUNDLE)} {shlex.quote(root)}", input=payload.encode())

    def launch_spec(self) -> dict:
        """What cluster-hostd needs to launch nodes exactly as this host would: the node environment (the
        harness' tuning variables among them), the cfg template and its requirements, the admission settings.
        Never a secret and never a path another host sets for itself (cluster_hostd.LAUNCH_ENV_DENY)."""
        import cluster_hostd
        env = {key: value for key, value in os.environ.items()
               if key.startswith(cluster_hostd.LAUNCH_ENV_PREFIXES) and key not in cluster_hostd.LAUNCH_ENV_DENY}
        template = self.config.dedicated_template
        return {"version": 1, "createdAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "gatewayHost": self.config.host, "gateway": f"http://{self.config.gateway_bind}",
                "nodeEnv": self.config.node_env, "nodeEnvById": self.config.node_env_by_id, "env": env,
                "dedicatedTemplate": template.read_text() if template else None,
                "dedicatedRequire": self.config.dedicated_require, "syncDistance": self.config.sync_distance,
                "serverName": self.config.server_name, "memberLimit": self.config.member_limit,
                "administrators": self.config.administrators, "worldName": self.config.world_template.name,
                "leaseTtlSeconds": self.config.lease_ttl_seconds, "archive": archive_identity(ARCHIVE)}

    def prepare_multi_host(self) -> None:
        """Everything a multi-host start does before the Gateway starts: the gates, this start's tokens on
        every host, the launch spec and the staged world."""
        spec, source, _ = self.config.hosts_spec()
        if self.config.host != spec["gateway"]:
            raise ClusterError(f"the hosts file names {spec['gateway']} as the gateway host; run `cluster start` there,"
                               f" not on {self.config.host}")
        if not (self.config.split_world / "Sandbox.sbc").is_file():
            raise ClusterError(f"no split world at {self.config.split_world}; `cluster fresh` makes one")
        remote = self.remote_hosts()
        print(f"[cluster] multi-host start from {source}: gateway {spec['gateway']}, "
              + ", ".join(f"{name} {entry['address']} {entry['serving']}+{entry['spares']}"
                          for name, entry in spec["hosts"].items()))
        local_archive = archive_identity(ARCHIVE)
        for host in remote:
            self.check_host_idle(host)
            theirs = self.remote_archive(host)
            if local_archive and theirs != local_archive:
                raise ClusterError(f"{host} runs archive {theirs or 'NONE'}, this host {local_archive}: re-sync it"
                                   " (bin/host-sync) and pass version-consistency --server first")
        tokens = self.mint_start_tokens(spec)
        hosts_text = Path(source).read_text() if Path(source).is_file() else json.dumps(spec)
        launch = json.dumps(self.launch_spec(), indent=1) + "\n"
        for host in [spec["gateway"], *remote]:
            files = {"secrets/join.token": (tokens["join"] + "\n", 0o600),
                     "secrets/admin.token": (tokens["admin"] + "\n", 0o600),
                     spec["hosts"][host]["executorTokenFile"]: (tokens["executors"][host] + "\n", 0o600),
                     "hostd/hosts.json": (hosts_text, 0o644), "hostd/launch.json": (launch, 0o644)}
            if host == spec["gateway"]:
                write_files(self.config.root, files)
            else:
                self.write_bundle(host, files)
        print(f"[cluster] tokens of this start written to {len(remote) + 1} host(s) (0600 under <runRoot>/secrets)")
        for host in remote:
            self.stage_world(host)

    def mint_start_tokens(self, spec: dict) -> dict:
        """New join, admin and per-host executor tokens (32 random bytes, base64: the Gateway's minimum), and
        the scoped token file the Gateway reads through CLUSTER_ADMIN_TOKENS: one Executor-scope entry per host,
        named after it, stored as SHA-256 (AdminTokens.cs). A token leaked once is dead at the next start."""
        import base64
        import secrets as _secrets
        mint = lambda: base64.b64encode(_secrets.token_bytes(32)).decode()  # noqa: E731
        tokens = {"join": mint(), "admin": mint(), "executors": {host: mint() for host in spec["hosts"]}}
        # Not a secret (INTERFACES.md: CLUSTER_START_ID); kept beside the tokens it names, same lifetime.
        write_files(self.config.root, {"secrets/start-id": (f"{uuid.uuid4()}\n", 0o600)})
        entries = [{"name": host, "scope": "Executor", "sha256": hashlib.sha256(token.encode()).hexdigest().upper()}
                   for host, token in tokens["executors"].items()]
        write_files(self.config.root, {"secrets/admin-tokens.json": (json.dumps({"tokens": entries}, indent=1) + "\n", 0o600)})
        return tokens

    def check_host_idle(self, host: str) -> None:
        """Refuse a start while a host still runs an agent or a node process of this run root."""
        root = self.config.host_entry(host)["runRoot"] or str(self.config.root)
        # "[M]agnetarInterim": run_on wraps this in `bash -c` over ssh, whose own argv carries the pattern; the bracket
        # still matches a real node but never the text "[M]agnetarInterim" (r66 start 04:16: an idle ai read nodes=2).
        pattern = shlex.quote(f"[M]agnetarInterim.*{re.escape(root)}/")
        pid_file = shlex.quote(f"{root}/hostd.pid")
        script = (f"n=$(pgrep -fc {pattern} || true); p=$(cat {pid_file} 2>/dev/null || true);"
                  f' a=0; [ -n "$p" ] && tr "\\0" " " < /proc/$p/cmdline 2>/dev/null | grep -q " hostd run" && a=$p;'
                  ' echo "nodes=$n hostd=$a"')
        out = self.run_on(host, script, timeout=30).stdout.decode()
        found = dict(pair.split("=", 1) for pair in out.split() if "=" in pair)
        if found.get("nodes", "0") not in ("", "0") or found.get("hostd", "0") not in ("", "0"):
            raise ClusterError(f"{host} still runs this cluster ({out.strip()}); `cluster stop` first")

    def remote_archive(self, host: str) -> str | None:
        entry = self.config.host_entry(host)
        script = (f". {remote_path(entry['serverEnv'] or DEFAULT_SERVER_ENV)} >/dev/null 2>&1;"
                  ' python3 -c "import json,sys; m=json.load(open(sys.argv[1])); print(m.get(\'commit\',\'?\'))"'
                  ' "$CLUSTER_ARCHIVE/manifest.json" 2>/dev/null || true')
        return self.run_on(host, script, timeout=30).stdout.decode().strip() or None

    def stage_world(self, host: str) -> None:
        """The split world (and its Storage seeds) into the host's run root: rsync from this host, then the
        tree hash compared on both ends (F2)."""
        entry = self.config.host_entry(host)
        root = entry["runRoot"] or str(self.config.root)
        target = f"{entry['ssh']}:{root}/cluster-world/"
        self.run_on(host, f"mkdir -p {shlex.quote(root)}/cluster-world", timeout=30)
        done = subprocess.run(["rsync", "-a", "--delete", "--checksum", "-e", " ".join(ssh_argv_base()),
                               f"{self.config.split_world}/", target], capture_output=True, text=True, timeout=600)
        if done.returncode:
            raise ClusterError(f"staging the world on {host} failed: {done.stderr.strip()[-400:]}")
        mine = tree_hash(self.config.split_world)
        theirs = self.run_on(host, f"python3 -c {shlex.quote(TREE_HASH)} {shlex.quote(root + '/cluster-world')}",
                             timeout=120).stdout.decode().strip()
        if theirs != mine:
            raise ClusterError(f"the world staged on {host} hashes {theirs[:12]}, this host's {mine[:12]}")
        print(f"[cluster] world staged on {host} ({mine[:12]})")

    def _start_multi_host(self) -> None:
        """After the Gateway: the WA warms this host's caches alone, then one cluster-hostd per host."""
        import cluster_hostd
        for spec in [node for node in self.config.nodes if node.role == "regular"]:
            self.config.nodes.remove(spec)
            self.config.retired_nodes.append(spec)
        self.config.save_node_set()
        wa = next((node for node in self.config.nodes if node.role == "WA"), None)
        if wa is not None:
            if SERIAL_WARMUP:
                self._warm_up(wa)
            else:
                self.start_node(wa.node_id)
        if cluster_hostd.start(self.config.root, self.config.host, fresh=True):
            raise ClusterError(f"cluster-hostd did not start on {self.config.host}; see {self.config.root}/hostd.log")
        for host in self.remote_hosts():
            done = self.host_cli(host, ["hostd", "start", "--host", host, "--fresh"], timeout=60, check=False)
            if done.returncode:
                raise ClusterError(f"cluster-hostd did not start on {host}: "
                                   f"{(done.stderr or done.stdout).decode(errors='replace').strip()[-400:]}")
            print(f"[cluster] cluster-hostd on {host}: {done.stdout.decode(errors='replace').strip()}")
        print("[cluster] node control is active on every host through cluster-hostd (fenced executor leases)")

    def hosts_status(self) -> dict:
        """Per host: the agent (`hostd status`) and the node processes of this run root."""
        out = {}
        for host in (self.config.topology() or {"hosts": {}})["hosts"]:
            try:
                done = self.host_cli(host, ["hostd", "status"], timeout=30)
                out[host] = json.loads(done.stdout.decode() or "{}")
            except (ClusterError, ValueError, subprocess.TimeoutExpired) as error:
                out[host] = {"error": str(error)}
        return out

    def stop_agents(self) -> None:
        """SIGTERM every host's cluster-hostd (their nodes keep running until the graceful shutdown)."""
        for host in (self.config.topology() or {"hosts": {}})["hosts"]:
            try:
                self.host_cli(host, ["hostd", "stop"], timeout=60)
            except (ClusterError, subprocess.TimeoutExpired) as error:
                print(f"[cluster] stopping cluster-hostd on {host}: {error}")

    def remote_node_pids(self) -> dict[str, list[int]]:
        out = {}
        for host in self.remote_hosts():
            root = self.config.host_entry(host)["runRoot"] or str(self.config.root)
            try:
                text = self.run_on(host, f"pgrep -f {shlex.quote(f'[M]agnetarInterim.*{re.escape(root)}/')} || true",
                                   timeout=30).stdout.decode()
                out[host] = [int(pid) for pid in text.split()]
            except (ClusterError, subprocess.TimeoutExpired):
                out[host] = []
        return out

    def kill_remote_nodes(self) -> dict[str, list[int]]:
        killed = {}
        for host in self.remote_hosts():
            try:
                killed[host] = json.loads(self.host_cli(host, ["hostd", "kill-nodes"], timeout=60).stdout.decode())["killed"]
            except (ClusterError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
                print(f"[cluster] killing the nodes on {host}: {error}")
        return killed

    def wait_ready_multi_host(self, timeout: float) -> None:
        """Ready = the Gateway listens, and on every host the serving and spare counts reached their targets
        with no node still Starting (the registry's operator view, not local logs: most nodes run elsewhere)."""
        wait_log(self.config.root / "gateway.log", "Gateway listening", timeout=min(timeout, 120))
        deadline = time.time() + timeout
        last = None
        while True:
            view = self.node_control()
            hosts = view.get("hosts") or []
            line = "; ".join(format_host_line(host) for host in hosts)
            if line != last:
                print(f"[cluster] {line}", flush=True)
                last = line
            settled = hosts and all((host.get(key) or {}).get("actual") == (host.get(key) or {}).get("target")
                                    for host in hosts for key in ("serving", "spares"))
            if settled and not any(node.get("state") == "Starting" for node in view.get("nodes") or []):
                break
            if time.time() >= deadline:
                raise ClusterError(f"multi-host start not settled within {timeout:.0f}s: {line}")
            time.sleep(5)
        self.post("/resume")

    def gateway_rs_binary(self) -> Path:
        """gateway-rs: MAGNETAR_GATEWAY_RS, else the package's gateway-rs/ beside the packaged Gateway
        (MAGNETAR_GATEWAY_DLL), else the checkout's release build (Build/build-gateway-rs.sh)."""
        explicit = os.environ.get("MAGNETAR_GATEWAY_RS")
        gateway_dll = os.environ.get("MAGNETAR_GATEWAY_DLL")
        if explicit:
            binary = Path(explicit).expanduser()
        elif gateway_dll:
            binary = Path(gateway_dll).expanduser().resolve().parent.parent / "gateway-rs/gateway-rs"
        else:
            binary = self.config.gateway_repo / "GatewayRs/target/release/gateway-rs"
        if not os.access(binary, os.X_OK):
            raise ClusterError(f"CLUSTER_RELAY=external needs gateway-rs, not found at {binary} "
                               "(set MAGNETAR_GATEWAY_RS, or run Build/build-gateway-rs.sh in the checkout)")
        return binary

    def _spawn_gateway_rs(self, binary: Path, socket_path: Path) -> subprocess.Popen:
        """gateway-rs under a restart loop: it holds no durable state, and the Gateway treats its
        loss as every client leaving (Docs/Gateway/RelayIpc.md), so the right supervision is simply
        to start it again. It is not one of running_pids(): it never exits by itself (it waits for
        the Gateway to come back), so a graceful stop would wait for it forever. kill() ends it."""
        argv = [sys.executable, str(Path(__file__).with_name("gateway_supervisor.py")),
                str(binary), "--socket", str(socket_path)]
        workers = os.environ.get("CLUSTER_RELAY_WORKERS")
        if workers:
            argv += ["--workers", workers]
        env = dict(os.environ)
        env.setdefault("ERROR_REPORTING_DIRECTORY", str(self.config.root / "diagnostics/runs/gateway-wrapper"))
        return self._spawn("gateway-rs", argv, env)

    def gateway_rs_pids(self) -> list[int]:
        """The gateway-rs restart loop and the binary of this run root (the socket path names it)."""
        marker = re.escape(f"{self.config.root}/gateway-relay.sock")
        return sorted(set(self._pgrep(f"gateway-rs.*{marker}")))

    def propagate_sources(self, warm: NodeSpec, nodes: list[NodeSpec] | None = None) -> list[str]:
        """Copy the warm-up node's hub state (Sources/sources.xml + Sources/Hubs/) into every other
        node's Magnetar config dir, so the whole node side runs ONE hub snapshot.

        T-0152 (Viktor, 2026-09-15): each <node>-magnetar/Sources is its own file set with its own
        LastCheck and hub pins; only the warm-up node is ever allowed to update, so without this
        copy a refreshed snapshot reached one node out of six and the others kept pinning whatever
        they had. sources.xml is copied through magnetar_config, which re-registers this node's
        own plugin folders on top (the warm-up node's role may differ)."""
        source_dir = self.config.magnetar_config(warm) / "Sources"
        if not (source_dir / "sources.xml").is_file():
            return []
        copied = []
        for node in nodes or self.config.nodes:
            if node.node_id == warm.node_id:
                continue
            self._copy_sources(source_dir, node)
            copied.append(node.node_id)
        if copied:
            print(f"[cluster] hub snapshot of {warm.node_id} propagated to {', '.join(copied)}")
        return copied

    def _copy_sources(self, source_dir: Path, node: NodeSpec) -> None:
        """sources.xml + Hubs/ of one hub snapshot into a node's Magnetar config dir, then this node's own
        plugins/profile re-registered on the copied sources file."""
        target = self.config.magnetar_config(node) / "Sources"
        target.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_dir / "sources.xml", target / "sources.xml")
        if (source_dir / "Hubs").is_dir():
            shutil.copytree(source_dir / "Hubs", target / "Hubs", dirs_exist_ok=True)
        self._write_magnetar_config(node)

    def prewarm(self, refresh: bool = False) -> None:
        """Pre-warm the node side WITHOUT starting the cluster: the warm-up node alone, updates
        allowed, until Game ready; its snapshot propagated to every other node; then stopped.
        ``refresh`` (CLUSTER_PREWARM_REFRESH=1) resets its LastCheck first so it re-reads the hub
        instead of trusting the snapshot on disk (MaxSourceAge is pinned to a year). Runs before
        `start`; refuses while any cluster process is up."""
        if self.running_pids():
            raise ClusterError("prewarm needs a stopped cluster: cluster processes are running")
        warm = next((node for node in self.config.nodes if node.role == "WA"), self.config.nodes[0])
        print(f"[cluster] prewarm: node set {', '.join(node.node_id for node in self.config.nodes)}; "
              f"warm-up node {warm.node_id}")
        config = self.prepare_magnetar_config(warm)
        sources = config / "Sources/sources.xml"
        if refresh and sources.is_file():
            text = sources.read_text()
            text = re.sub(r"<LastCheck>[^<]*</LastCheck>", "<LastCheck>2000-01-01T00:00:00.0000000Z</LastCheck>", text)
            sources.write_text(text)
            print(f"[cluster] prewarm: LastCheck of {warm.node_id} reset, the hub will be re-read")
        print(f"[cluster] prewarm: starting {warm.node_id} alone with updates allowed")
        started = time.time()
        self.start_node(warm.node_id, allow_update=True, standalone=True)
        # Readiness is the HUB CHECK and the plugin load, never the game: with no Gateway up the
        # node sits at "[ClusterPlugin] node_state=Registering" and never prints "Game ready", so
        # waiting for that (first live pre-warm, 2026-09-15 01:18) timed out and killed wa-1
        # WITHOUT propagating a snapshot that had in fact been refreshed at 01:18:10.
        #   * refresh: Sources/sources.xml LastCheck newer than the start (the hub was re-read;
        #     without --refresh MaxSourceAge keeps the snapshot on disk and LastCheck does not move)
        #   * the plugins instantiated and compiled: Magnetar's info.log "Wrote pid file" (after
        #     the compile) or the node's stdout reaching the cluster plugin (node_state= /
        #     Game ready); the GitHub artefacts are extracted before either line
        stdout_log = self.config.root / f"{warm.node_id}.log"
        info_log = config / "info.log"

        def state() -> dict:
            checked = last_check_after(sources, started)
            info = info_log.read_text(errors="replace") if info_log.is_file() and info_log.stat().st_mtime >= started - 1 else ""
            out = stdout_log.read_text(errors="replace") if stdout_log.is_file() else ""
            return {"hub_checked": checked, "plugins_loaded": "Wrote pid file" in info
                    or bool(re.search(r"node_state=|Game ready", out)),
                    "alive": bool(self._pgrep(f"MagnetarInterim.*{warm.node_id}-magnetar"))}

        def ready():
            current = state()
            if current["plugins_loaded"] and (current["hub_checked"] or not refresh):
                return current
            return None if current["alive"] else {**current, "died": True}

        try:
            outcome = wait_until(ready, SERIAL_WARMUP_TIMEOUT, f"{warm.node_id}: hub check + plugin load", interval=2.0)
            elapsed = time.time() - started
            if outcome.get("died"):
                raise ClusterError(f"prewarm: {warm.node_id} exited after {elapsed:.0f} s before it was ready ({outcome})")
            if self._report_plugin_failures(warm):
                raise ClusterError(f"prewarm: {warm.node_id} could not load a declared plugin; see its info.log")
            print(f"[cluster] prewarm: {warm.node_id} ready after {elapsed:.0f} s "
                  f"(hub {'re-read' if outcome['hub_checked'] else 'snapshot kept'}, plugins loaded); stopping it")
        except ClusterError as failure:
            final = state()
            self.kill(warm.node_id)
            if not final["hub_checked"] and refresh:
                raise ClusterError(f"prewarm FAILED: {warm.node_id} did not re-read the hub within "
                                   f"{SERIAL_WARMUP_TIMEOUT:.0f} s (LastCheck did not advance); nothing propagated. {failure}")
            raise ClusterError(f"prewarm FAILED: {failure}; nothing propagated ({final})")
        self.kill(warm.node_id)
        self.propagate_sources(warm)
        print(json.dumps(self.versions(), indent=2))

    def versions(self) -> dict:
        """The node-side version set as one JSON document: Magnetar's informational version,
        and per node config dir the hub hash/LastCheck, the commit pinned per repository in
        Sources/Hubs/*.bin (Pulsar's protobuf-net snapshot), and the fingerprint of every cached
        GitHub artefact. cluster-regression's bin/version-consistency reads this and compares it
        with the client hosts (T-0152)."""
        out = {"magnetar": informational_version(self.config.magnetar / "MagnetarInterim.dll"), "nodes": {}}
        for node in self.config.nodes:
            config = self.config.magnetar_config(node)
            entry = {"hub": {}, "pins": {}, "cache": {}}
            sources = config / "Sources/sources.xml"
            if sources.is_file():
                text = sources.read_text()
                for tag in ("Hash", "LastCheck"):
                    match = re.search(rf"<{tag}>([^<]*)</{tag}>", text)
                    entry["hub"][tag.lower()] = match.group(1) if match else None
            hubs = config / "Sources/Hubs"
            if hubs.is_dir():
                for snapshot in sorted(hubs.glob("*.bin")):
                    try:
                        entry["pins"].update(hub_pins(snapshot))
                    except (ValueError, IndexError) as failure:
                        entry["pins"][snapshot.name] = f"UNPARSEABLE ({failure})"
            cache = config / "GitHub"
            if cache.is_dir():
                for manifest in sorted(cache.glob("*/manifest.xml")):
                    text = manifest.read_text()
                    match = re.search(r"<Fingerprint>([^<]*)</Fingerprint>", text)
                    entry["cache"][manifest.parent.name] = match.group(1) if match else None
            out["nodes"][node.node_id] = entry
        return out

    def _mark_spares(self) -> None:
        """Declare the full spare set BEFORE any node process starts (HotSpareNodes.md): the last
        spare_nodes regular slots become spares, every other regular slot is cleared so a stale
        designation left in the registry WAL by a previous run cannot leave an extra idle node.
        Positional starts only: since T-0320 a plain start applies the hosts file instead."""
        if not self.config.spare_nodes:
            return
        self._wait_gateway_control("the spare-node set")
        regulars = [node for node in self.config.nodes if node.role == "regular"]
        for index, node in enumerate(regulars):
            spare = index >= len(regulars) - self.config.spare_nodes
            self.post(f"/registry/nodes/spare?node={node.node_id}&spare={str(spare).lower()}")

    def start_node(self, node_id: str, allow_update: bool = False, standalone: bool = False) -> subprocess.Popen:
        """Spawn one node against the already-running Gateway - P3-REC-01 arms the reject-node
        hook between the Gateway's start and the target slot's spawn."""
        self.require_plugin_sdk()
        node = next(spec for spec in self.config.nodes if spec.node_id == node_id)
        data = self.config.node_data(node)
        data.mkdir(parents=True, exist_ok=True)
        self.seed_dedicated_config(node)
        self.refresh_premade_world(node)
        config = self.prepare_magnetar_config(node)
        # -multiInstance: several launchers on one host; -noconsent: no telemetry prompt;
        # -ignorelastsession + the seeded config load the split world as a premade checkpoint. No
        # -eos: clients reach the nodes through the Rust gateway, which does not carry EOS yet.
        # Magnetar drops its NuGet cache into the working directory, hence cwd.
        # -noupdate: once the cluster is up NOTHING may go back to GitHub or NuGet. The caches
        # are warmed ONCE by the serial warm-up node below, and every instance after it uses them
        # unchanged (Viktor, 2026-09-05, T-0052). The client launcher has passed this for a long
        # time; the six server-side Magnetar processes never did, and node rotation starts fresh
        # ones all run - w29 logged 157 rotation events. Its partner is MaxSourceAge in the
        # sources config, which magnetar_config pins high: the flag stops the update CHECK, the
        # age stops a source being considered stale at all, and only both together close it.
        #
        # allow_update is for the warm-up node ALONE. Passing -noupdate to it as well would mean
        # nothing ever refreshes the cache, which is not "update once at start" but "never
        # update" - a different policy, and not the one asked for.
        update_args = [] if allow_update and self.config.common_plugins is None else ["-noupdate"]
        # -noimplicitmod: never add the MagnetarMod client companion (a Steam Workshop mod) to the world.
        # The DS runs without Steam UGC; Magnetar 2.4.2.4 found a UGC service anyway, added the mod, and
        # the vanilla world-mod download then threw "Steamworks is not initialized", which the DS reports
        # as "Save is corrupted or not valid" (1.1.8 start, 2026-09-28 17:43). A node never needs it.
        return self._spawn(node.node_id, [*self._ksm_shim(node),
            str(self.config.launcher), "-config", str(config), "-ds64", str(self.config.ds64),
            "-profile", magnetar_config.PROFILES[self._role_key(node)], *update_args,
            "-multiInstance", "-noconsent", "-noimplicitmod",
            "-noconsole", "-ignorelastsession",
            "-port", node.backend.rpartition(":")[2], "-path", str(data)], self._node_env(node, standalone), cwd=config)

    @staticmethod
    def _ksm_shim(node: "NodeSpec") -> list[str]:
        """T-0247/T-0271: opt node processes into KSM page merging. CLUSTER_NODE_KSM selects them:
        ``regular`` = every node whose role is not WA (node-N, spares, and every node a rotation or restart
        starts later, since every node process starts here); ``all`` = every node including the WA; or a
        comma-separated list of node ids. The shim sets PR_SET_MEMORY_MERGE (inherited across exec on 6.6+)
        and then execs the launcher, so the pid is kept; CLUSTER_NODE_KSM_THP_OFF=1 also sets
        PR_SET_THP_DISABLE (KSM merges only 4 KiB pages; an A/B decides). The gateway, ClusterRegistry and
        gateway-rs are not started here and are never opted in. Does nothing unless the host runs ksmd
        (/sys/kernel/mm/ksm/run = 1). The prctl results go to the node log."""
        selection = os.environ.get("CLUSTER_NODE_KSM", "").strip()
        if selection == "regular":
            selected = node.role != "WA"
        elif selection == "all":
            selected = True
        else:
            selected = bool(selection) and node.node_id in {n.strip() for n in selection.split(",")}
        if not selected:
            return []
        thp = "1" if os.environ.get("CLUSTER_NODE_KSM_THP_OFF") == "1" else "0"
        code = ("import ctypes,os,sys;c=ctypes.CDLL(None,use_errno=True);"
                f"r=[c.prctl(67,1,0,0,0),c.prctl(41,1,0,0,0) if {thp} else 0];"
                "print('T-0247 ksm-shim: MEMORY_MERGE=%d THP_DISABLE=%d' % tuple(r),flush=True);"
                "os.execvp(sys.argv[1],sys.argv[1:])")
        return [sys.executable or "python3", "-c", code]

    # -- T-0320 node control: the launcher (Docs/Gateway/NodeControl.md) ---------------------------
    # The registry decides which node ids run where (NodePlan: one slot per node id, goal Wanted /
    # Draining / Kill); this host's launcher only carries it out: start Wanted slots that have no
    # process, stop Kill slots, and report what it observes. Exactly one launcher per host
    # (<root>/launcher.lock): the harness rotator, a `cluster start`, or `cluster nodes ... --wait`.

    @property
    def launcher_lock_path(self) -> Path:
        return self.config.root / "launcher.lock"

    def acquire_launcher_lock(self) -> None:
        """Take this host's launcher lock (held until the process exits), or ClusterError naming the holder."""
        if self._launcher_lock is not None:
            return
        handle = self.launcher_lock_path.open("a+")
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            handle.seek(0)
            holder = handle.read().strip() or "unknown"
            handle.close()
            raise ClusterError(f"another launcher holds {self.launcher_lock_path} (pid {holder})") from None
        handle.seek(0)
        handle.truncate()
        handle.write(f"{os.getpid()}\n")
        handle.flush()
        self._launcher_lock = handle

    def try_launcher_lock(self) -> bool:
        try:
            self.acquire_launcher_lock()
            return True
        except ClusterError:
            return False

    def release_launcher_lock(self) -> None:
        if self._launcher_lock is not None:
            self._launcher_lock.close()
            self._launcher_lock = None

    def _note_once(self, key: str, text: str) -> None:
        if key not in self._noted:
            self._noted.add(key)
            print(f"[cluster] {text}", flush=True)

    def _node_running(self, node: NodeSpec) -> bool:
        return bool(self._node_pids(node.node_id))

    def _node_pids(self, node_id: str) -> list[int]:
        return self._pgrep(f"MagnetarInterim.*{re.escape(str(self.config.root))}/{node_id}-data")

    def _node_pid(self, node_id: str) -> int | None:
        """The node's own process (not its bash launch wrapper), for the observation's PID."""
        pids = self._node_pids(node_id)
        for pid in pids:
            try:
                if Path(f"/proc/{pid}/comm").read_text().strip() != "bash":
                    return pid
            except OSError:
                continue
        return pids[0] if pids else None

    def _memory_available_mb(self) -> float:
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) / 1024
        raise ClusterError("no MemAvailable in /proc/meminfo")

    def _disk_free_mb(self) -> float:
        stat = os.statvfs(self.config.root)
        return stat.f_bavail * stat.f_frsize / (1024 * 1024)

    def _host_floors(self) -> tuple[float | None, float | None]:
        """(RAM floor MB, disk floor MB) of this host: the registry's (runtime) values, else the hosts file's."""
        try:
            view = self.node_control()
            for host in view.get("hosts") or []:
                if host.get("host") == self.config.host:
                    return host.get("ramFloorMb"), host.get("diskFloorMb")
        except ClusterError:
            pass
        entry = self.config.hosts_spec()[0]["hosts"].get(self.config.host) or {}
        mb = lambda gb: None if gb is None else gb * 1024  # noqa: E731
        return mb(entry.get("ramFloorGb")), mb(entry.get("diskFloorGb"))

    def floor_problem(self, floors: tuple[float | None, float | None]) -> str | None:
        """Why a launch now would take this host below its RAM or disk floor, or None."""
        ram, disk = floors
        if ram is not None:
            available = self._memory_available_mb()
            if available < ram:
                return f"host_floor: MemAvailable {available:.0f} MB < RAM floor {ram:.0f} MB on {self.config.host}"
        if disk is not None:
            free = self._disk_free_mb()
            if free < disk:
                return f"host_floor: {free:.0f} MB free under {self.config.root} < disk floor {disk:.0f} MB on {self.config.host}"
        return None

    def _retire_spec(self, spec: NodeSpec) -> None:
        """The node is gone: its port slot is free for the next node id (the id itself never returns)."""
        if spec in self.config.nodes:
            self.config.nodes.remove(spec)
            self.config.retired_nodes.append(spec)

    def actualize_step(self, spawn: bool = True) -> dict:
        """Carry out this host's part of the registry's node plan, once; idempotent, never waits for a
        node's startup. Starts Wanted slots that have no process (unless ``spawn`` is False - the
        rotator's dev-folder guard), stops Kill slots (SELF_EXIT_SECONDS for the node to exit by itself,
        then SIGKILL and wait for its port), never restarts a node id that already registered, refuses a
        launch below the host's RAM/disk floor, and reports every observation (with the PID) through the
        executor heartbeat. Returns {spawned, stopped, refused, observations}.

        Multi-host (T-0321): every host's own cluster-hostd is its launcher (the fenced executor lease),
        so this is a no-op returning ``delegated`` - the rotator and `nodes --wait` keep working unchanged."""
        if self.config.multi_host:
            self._note_once("delegated", "multi-host start: cluster-hostd is the launcher on every host;"
                            " this process only observes (`cluster hostd status`)")
            return {"spawned": [], "stopped": [], "refused": [], "observations": [], "delegated": True}
        self.acquire_launcher_lock()
        self.config.reload_node_set()
        plan = self.get("/registry/node-plan")
        rows = _field(self.get("/registry/status"), "nodes") or []
        summary = self.actualize(plan, rows, spawn=spawn)
        self.report_observations(summary["observations"])
        return summary

    def actualize(self, plan, rows: list, spawn: bool = True, spawn_budget: int | None = None,
                  fenced: bool = False) -> dict:
        """The launcher step over a given plan (the legacy /registry/node-plan rows, or the fenced executor
        heartbeat's slots of this host) and the registry's node rows. Observations carry the registry epoch
        of the incarnation they name (the fenced contract's incarnation check). ``spawn_budget`` caps the
        new launches of this step (cluster-hostd's per-host serial warm-up); None = no cap. ``fenced``
        (cluster-hostd) reports a running node Ready only once the registry holds a live incarnation of it
        (the fenced contract refuses Ready otherwise); the legacy path reports every running node Ready."""
        here = self.config.host
        registered = {str(_field(row, "node")): row for row in rows}
        summary: dict = {"spawned": [], "stopped": [], "refused": [], "observations": []}
        observations = summary["observations"]
        floors = None
        planned = set()
        changed = False

        def epoch_of(node_id: str | None) -> int:
            row = registered.get(str(node_id)) if node_id else None
            try:
                return int(_field(row, "epoch") or 0) if row else 0
            except (TypeError, ValueError):
                return 0

        def observe(node_id: str, state: str, attempt: str | None = None, failure: str | None = None,
                    node: str | None = None, pid: int | None = None, epoch: int | None = None) -> None:
            observations.append({"slotKey": node_id, "attemptKey": attempt or f"{node_id}-observed", "state": state,
                                 "node": node, "failure": failure, "pid": pid, "host": here,
                                 "epoch": epoch_of(node) if epoch is None else epoch})

        now = time.time()
        for slot in plan if isinstance(plan, list) else []:
            if _field(slot, "host") != here or _enum(_field(slot, "role"), ROLES) != "Regular":
                continue
            node_id = str(_field(slot, "slotKey"))
            planned.add(node_id)
            spec = next((node for node in self.config.nodes if node.node_id == node_id), None)
            running = spec is not None and self._node_running(spec)
            goal = _field(slot, "desired")
            desired = _enum(goal if goal is not None else _field(slot, "goal"), DESIRED)
            incumbent = _enum(_field(slot, "incumbentAction"), INCUMBENT)
            if desired == "Kill" or incumbent == "Kill":
                if running:
                    first = self._kill_seen.setdefault(node_id, now)
                    if now - first < SELF_EXIT_SECONDS:
                        continue   # the node usually ends itself once empty (T-0162)
                    print(f"[cluster] {node_id} did not exit within {SELF_EXIT_SECONDS:.0f}s of its stop; killing it")
                    self._kill_pids(node_id)
                    self.await_port_free(spec)
                    if self._node_running(spec):
                        continue
                self._kill_seen.pop(node_id, None)
                if incumbent == "Kill":
                    # Gone must acknowledge the exact incumbent the registry commanded (fenced contract).
                    killed = _field(slot, "incumbentNode")
                    observe(node_id, "Gone", (self._attempts.pop(node_id, None) or {}).get("key"),
                            node=killed if killed is not None else node_id,
                            epoch=int(_field(slot, "incumbentEpoch") or 0) if killed is not None else None)
                else:
                    observe(node_id, "Gone", (self._attempts.pop(node_id, None) or {}).get("key"), node=node_id)
                summary["stopped"].append(node_id)
                if spec is not None:
                    self._retire_spec(spec)
                    changed = True
                continue
            if running:
                row = registered.get(node_id)
                lease = _parse_stamp(_field(row, "leaseExpires")) if row else None
                active = row is not None and _enum(_field(row, "state"), ("Active", "Closed", "Dead", "Empty")) == "Active"
                # A registered Active incarnation whose lease looks expired is reported Spawning: the fenced
                # contract refuses Ready for it, and one refused report would fail the whole batch.
                state = "Spawning" if fenced and (row is None or active and lease is not None and lease < now) \
                    else "Ready"
                observe(node_id, state, self._attempts.get(node_id, {}).get("key"), node=node_id,
                        pid=self._node_pid(node_id))
                continue
            if desired == "Draining" or incumbent == "Draining":
                continue   # a draining slot is never (re)started
            if node_id in registered:
                # Node ids are never reused: a node that registered once and is gone stays gone, and the
                # registry replaces it under a new id.
                self._note_once(f"dead:{node_id}", f"{node_id} registered once and has no process; "
                                "not restarting it (ids are never reused), reporting it Gone")
                observe(node_id, "Gone", node=node_id)
                if spec is not None:
                    self._retire_spec(spec)
                    changed = True
                continue
            attempt = self._attempts.get(node_id)
            if attempt is not None and not attempt["failed"]:
                # Died before it ever registered: the id is not used up, the registry backs off and
                # allows a retry under the same id.
                attempt["failed"] = True
                observe(node_id, "Failed", attempt["key"], failure="process exited before it registered", epoch=0)
                continue
            if not spawn or not _field(slot, "spawnAllowed"):
                continue
            if spawn_budget is not None and len(summary["spawned"]) >= spawn_budget:
                continue
            if floors is None:
                floors = self._host_floors()
            problem = self.floor_problem(floors)
            if problem:
                self._attempts[node_id] = {"key": f"{node_id}-{uuid.uuid4().hex[:12]}", "failed": True}
                observe(node_id, "Failed", self._attempts[node_id]["key"], failure=problem, epoch=0)
                summary["refused"].append(node_id)
                print(f"[cluster] not starting {node_id}: {problem}")
                continue
            if spec is None:
                try:
                    spec = self._new_regular_node(node_id)
                except ClusterError as error:
                    self._attempts[node_id] = {"key": f"{node_id}-{uuid.uuid4().hex[:12]}", "failed": True}
                    observe(node_id, "Failed", self._attempts[node_id]["key"], failure=str(error), epoch=0)
                    summary["refused"].append(node_id)
                    print(f"[cluster] not starting {node_id}: {error}")
                    continue
                changed = True
            self.config.save_node_set()
            self.seed_sources(spec)
            print(f"[cluster] starting {node_id} (slot {self.config.slot_of(spec)})")
            try:
                self.start_node(node_id)
            except ClusterError as error:
                self._attempts[node_id] = {"key": f"{node_id}-{uuid.uuid4().hex[:12]}", "failed": True}
                observe(node_id, "Failed", self._attempts[node_id]["key"], failure=f"launch refused: {error}", epoch=0)
                summary["refused"].append(node_id)
                print(f"[cluster] not starting {node_id}: {error}")
                continue
            self._attempts[node_id] = {"key": f"{node_id}-{uuid.uuid4().hex[:12]}", "failed": False}
            observe(node_id, "Spawning", self._attempts[node_id]["key"], node=node_id, pid=self._node_pid(node_id), epoch=0)
            summary["spawned"].append(node_id)
        # A regular in the ledger the plan no longer names: its slot is free once its process is gone.
        for spec in [node for node in self.config.nodes if node.role == "regular" and node.node_id not in planned]:
            if self._node_running(spec):
                self._note_once(f"unplanned:{spec.node_id}", f"{spec.node_id} runs but is not in the node plan; left alone")
            else:
                self._retire_spec(spec)
                changed = True
        if changed:
            self.config.save_node_set()
        return summary

    def seed_sources(self, spec: NodeSpec) -> None:
        """Give a new node the host's hub snapshot before its launch: a node on this host that has one (the
        WA first), else CLUSTER_PREWARM_SOURCES - the snapshot bin/server-sync stages on a node host that has
        no warm node yet (T-0321). Neither: the node starts with the sources it has."""
        warm = next((node for node in [*[n for n in self.config.nodes if n.role == "WA"], *self.config.nodes]
                     if node is not spec and (self.config.magnetar_config(node) / "Sources/sources.xml").is_file()), None)
        if warm is not None:
            self.propagate_sources(warm, [spec])
            return
        staged = os.environ.get("CLUSTER_PREWARM_SOURCES", "").strip()
        if staged and (Path(staged).expanduser() / "sources.xml").is_file():
            self._copy_sources(Path(staged).expanduser(), spec)
            print(f"[cluster] {spec.node_id}: hub snapshot seeded from {staged}")
    def report_observations(self, observations: list[dict]) -> None:
        """The legacy executor heartbeat (unmanaged mode): one batch, or one slot at a time when the batch
        is refused (the registry rejects the whole batch for one slot it has just dropped)."""
        executor = f"launcher@{self.config.host}"
        observations = [{key: value for key, value in observation.items() if key in LEGACY_OBSERVATION_KEYS}
                        for observation in observations]
        try:
            self.post("/registry/executors/heartbeat", body={"executor": executor, "observations": observations})
            return
        except ClusterError as error:
            if len(observations) <= 1:
                print(f"[cluster] executor heartbeat refused: {error}")
                return
        for observation in observations:
            try:
                self.post("/registry/executors/heartbeat", body={"executor": executor, "observations": [observation]})
            except ClusterError as error:
                print(f"[cluster] observation {observation['state']} of {observation['slotKey']} refused: {error}")

    def await_port_free(self, spec: NodeSpec, deadline: float = 30.0) -> bool:
        """Wait until a killed node's control port is released. Moved from the rotator (w26 2026-09-02:
        a relaunch 5 s after a kill could not bind its control port and the node exited, twice). Never
        raises; False when the port is still busy at the deadline."""
        host, port = split_endpoint(spec.control)
        started = time.time()
        while time.time() - started < deadline:
            if not port_open(host, port):
                return True
            time.sleep(0.25)
        print(f"[cluster] {spec.node_id} control port {port} still busy after {deadline:.0f}s")
        return False

    def _new_regular_node(self, node_id: str) -> NodeSpec:
        """A NodeSpec for a new node id from the plan, appended to config.nodes: the slot of a retired node
        whose ports are free (that retired entry is dropped), else the next slot after the highest."""
        live_slots = {self.config.slot_of(node) for node in self.config.nodes}
        slot = None
        for retired in sorted(self.config.retired_nodes, key=self.config.slot_of):
            candidate = self.config.slot_of(retired)
            if candidate in live_slots or self._node_running(retired):
                continue
            probe = self.config.node_spec(node_id, candidate)
            if any(port_open(*split_endpoint(endpoint)) for endpoint in (probe.backend, probe.control)):
                continue
            self.config.retired_nodes.remove(retired)
            slot = candidate
            break
        if slot is None:
            slot = max((self.config.slot_of(node) for node in [*self.config.nodes, *self.config.retired_nodes]),
                       default=0) + 1
            if self.config.multi_host and slot > self.config.host_entry()["portSlots"]:
                raise ClusterError(f"no free port slot on {self.config.host}: all {self.config.host_entry()['portSlots']}"
                                   " slots are taken (portSlots in the hosts file)")
        spec = self.config.node_spec(node_id, slot)
        self.config.nodes.append(spec)
        return spec

    # -- T-0320 node control: the operator verbs (`cluster nodes ...`) -------------------------------

    @staticmethod
    def _admin_data(response):
        """The payload of an /admin/v1 envelope ({protocolVersion, capturedAt, data})."""
        return response.get("data", response) if isinstance(response, dict) else response

    def _admin_mutation(self, method: str, path: str, body: dict) -> dict:
        """One /admin/v1 mutation with a fresh Idempotency-Key; the operation's result (or the operation)."""
        operation = self._admin_data(self._request(method, path, body=body,
                                                   headers={"Idempotency-Key": str(uuid.uuid4())}))
        if isinstance(operation, dict) and operation.get("state") == "Failed":
            error = operation.get("error") or {}
            raise ClusterError(f"{method} {path} failed: {error.get('code', '?')}: {error.get('message', '')}")
        if isinstance(operation, dict) and isinstance(operation.get("result"), dict):
            return {**operation["result"], "operation": operation.get("operationId"), "target": operation.get("target")}
        return operation if isinstance(operation, dict) else {"result": operation}

    @staticmethod
    def requested_by() -> str:
        return f"{os.environ.get('USER') or os.environ.get('LOGNAME') or 'unknown'}@{local_host()}"

    def publish_rotation(self, lifetime_file: str, valid_for: int = 120) -> dict:
        """Publish the Manager's assignments, bound to the Registry's current node epochs."""
        if not 1 <= valid_for <= 300:
            raise ClusterError("rotation publication validity must be 1..300 seconds")
        try:
            path = Path(lifetime_file)
            if path.stat().st_size > 1024 * 1024:
                raise ValueError("publication exceeds 1 MiB")
            published = json.loads(path.read_text())
            pid = published["rotatorPid"]
            if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0:
                raise ValueError("invalid rotatorPid")
            os.kill(pid, 0)
            configured = float(published["configuredLifetime"])
            if not math.isfinite(configured) or configured < 0:
                raise ValueError("invalid configuredLifetime")
            nodes = published.get("nodes") or {}
            assigned = []
            for node in self.get("/registry/nodes") if configured else []:
                if node.get("Role") != 0 or node.get("State") != 0 or node.get("Spare") or not node.get("Serving"):
                    continue
                budget = (nodes.get(node["Node"]) or {}).get("lifetime")
                if budget is None:
                    continue
                budget = float(budget)
                if not math.isfinite(budget) or budget <= 0:
                    raise ValueError("invalid assigned lifetime")
                start = datetime.fromisoformat(node.get("Activated") or node["Registered"])
                assigned.append({"node": node["Node"], "epoch": node["Epoch"],
                                 "dueAt": (start + timedelta(seconds=budget)).isoformat()})
        except (OSError, ValueError, TypeError, KeyError, AttributeError, OverflowError) as error:
            raise ClusterError(f"invalid or inactive rotation publication: {error}") from error
        current = self._admin_data(self.get("/admin/v1/rotation"))
        return self._admin_mutation("PUT", "/admin/v1/rotation", {
            "expectedRevision": current["revision"], "source": "cluster-manager",
            "validUntil": (datetime.now(timezone.utc) + timedelta(seconds=valid_for)).isoformat(),
            "enabled": configured > 0, "nodes": assigned})

    def node_control(self) -> dict:
        """GET /admin/v1/node-control: per-host targets and every live node in operator states."""
        return self._admin_data(self.get("/admin/v1/node-control"))

    def nodes_set(self, host: str, serving: int | None = None, spares: int | None = None,
                  reason: str | None = None, requested_by: str | None = None) -> dict:
        body = {key: value for key, value in (("serving", serving), ("spares", spares), ("reason", reason),
                                              ("requestedBy", requested_by or self.requested_by())) if value is not None}
        return self._admin_mutation("PUT", f"/admin/v1/node-control/hosts/{quote_path(host)}", body)

    def nodes_stop(self, verb: str, selector: str, now: bool = False, timeout: float | None = None,
                   reason: str | None = None, requested_by: str | None = None) -> dict:
        """`remove` (the host's target drops by one) or `recycle` (replaced, count unchanged)."""
        if verb not in ("remove", "recycle"):
            raise ValueError(verb)
        if not SELECTOR.match(selector):
            raise ClusterError(f"selector {selector!r}: use a node id (node-9), PID@host (12345@polaris)"
                               " or host#position (polaris#2)")
        body = {"selector": selector, "now": now, "reason": reason, "requestedBy": requested_by or self.requested_by()}
        if timeout is not None:
            body["timeoutSeconds"] = timeout
        return self._admin_mutation("POST", f"/admin/v1/node-control/{verb}", body)

    def nodes_history(self, limit: int = 50, host: str | None = None, cursor=None) -> dict:
        query = f"limit={int(limit)}" + (f"&host={quote_path(host)}" if host else "") \
            + (f"&cursor={quote_path(str(cursor))}" if cursor not in (None, "") else "")
        return self._admin_data(self.get(f"/admin/v1/node-control/history?{query}"))

    def nodes_history_all(self, host: str | None = None) -> list[dict]:
        records, cursor = [], None
        while True:
            page = self.nodes_history(limit=500, host=host, cursor=cursor)
            records += page.get("records") or []
            cursor = page.get("nextCursor")
            if cursor in (None, "", 0) or not page.get("records"):
                return records

    def nodes_wait(self, done, timeout: float = 900, interval: float = 5, out=sys.stdout) -> dict:
        """Poll the operator view until ``done(view)`` and no node is Starting/Draining/Stopping. Runs the
        launcher step itself when it can take this host's launcher lock (else another launcher - the
        rotator - is doing it). Prints each change of the view."""
        launcher = self.try_launcher_lock()
        deadline = time.time() + timeout
        last = None
        while True:
            if launcher:
                self.actualize_step()
            view = self.node_control()
            text = "\n".join(format_node_control(view))
            shown = re.sub(r"age \S+|force in \S+", "", text)
            if shown != last:
                out.write(text + "\n\n")
                out.flush()
                last = shown
            if done(view) and not any(node.get("state") in TRANSITIONAL_STATES for node in view.get("nodes") or []):
                return view
            if time.time() >= deadline:
                raise ClusterError(f"node control did not settle within {timeout:.0f}s")
            time.sleep(interval)

    def nodes_prune(self, apply: bool = False) -> dict:
        """Data dirs of nodes that are gone for good: in history, not live, released by the registry (T-0323: it
        removes a stopped node's row only once no save - in memory or ARCHIVED, which /registry/saves does not
        list - names it) and referenced by no catalogued save Location (partition saves, global saves, voxel
        bases). Dry run unless ``apply``. Anything the registry cannot answer keeps every dir (a prune never
        guesses).

        T-0340 step 6: in host-store mode the registry releases a stopped row WITHOUT asking the archive, and an
        archived save can still name a file in that node's dir (one that was not moved into the store; it is
        served from there by a live node of the host). The released row therefore proves nothing about the dir:
        while the registry is, or ever was, in host-store mode (status hostStore.enabled, or a non-empty alias
        map), a dir that still holds a save file is kept."""
        view = self.node_control()
        store = _field(self.get("/registry/status"), "hostStore") or {}
        keep_save_files = bool(_field(store, "enabled")) or int(_field(store, "aliases") or 0) > 0
        live = {node.get("node") for node in view.get("nodes") or []} \
            | {entry.get("node") for entry in view.get("notCounted") or []}
        history = {record.get("node") for record in self.nodes_history_all()}
        rows = {str(_field(row, "node")) for row in self.get("/registry/nodes")}
        catalogue = [*self.get("/registry/saves"), *self.get("/registry/global-saves"), *self.get("/registry/voxel-bases")]
        referenced_paths, holders = [], set()
        for entry in catalogue:
            locations = list(_field(entry, "locations") or []) + [_field(entry, "location")]
            referenced_paths += [str(location) for location in locations if location]
            holders |= {str(node) for node in (_field(entry, "locationHolders") or {}).values()}
        result = {"deleted" if apply else "wouldDelete": [], "kept": {}}
        for data in self.node_data_dirs():
            node_id = data.name[:-len("-data")]
            prefix = str(data.resolve()) + os.sep
            why = ("live" if node_id in live
                   else "not in history" if node_id not in history
                   else "registry row (save holder)" if node_id in rows
                   else "catalogued save location" if any(str(Path(path).resolve()).startswith(prefix)
                                                          or path.startswith(str(data) + os.sep) for path in referenced_paths)
                   else "save holder" if node_id in holders
                   else "save files (host-store mode: an archived save may name them)"
                   if keep_save_files and next((data / "cluster" / "saves").rglob("*.mps"), None) is not None
                   else "running" if self._node_pids(node_id) else None)
            if why:
                result["kept"][data.name] = why
                continue
            if apply:
                if data.resolve().parent != self.config.root.resolve():
                    result["kept"][data.name] = "not directly under the run root"
                    continue
                shutil.rmtree(data)
                shutil.rmtree(self.config.root / f"{node_id}-magnetar", ignore_errors=True)
            result["deleted" if apply else "wouldDelete"].append(data.name)
        return result

    # -- T-0312 online node scaling: deprecated shim (Docs/Gateway/NodeScaling.md) -----------------

    def scale_status(self) -> dict:
        """The registry's scale status: target, phase, serving / starting / retiring / retired nodes."""
        return self.get("/registry/scale")

    def set_scale(self, nodes: int, force: bool = False, retire: list[str] | None = None) -> dict:
        query = f"nodes={nodes}&force={str(force).lower()}" + (f"&retire={','.join(retire)}" if retire else "")
        return self.post(f"/registry/scale?{query}")

    def scale(self, nodes: int | None, force: bool = False, launch: bool = True, timeout: float = 3600,
              interval: float = 10, retire: list[str] | None = None) -> dict:
        """DEPRECATED `cluster scale` (T-0320: `cluster nodes set HOST --serving N`): set the target (the
        registry maps it onto the sole host's serving target), then, unless launch=False, run the
        launcher step until the registry reports the step settled or timeout passes."""
        print("[cluster] `cluster scale` is deprecated: use `cluster nodes set <host> --serving N`", file=sys.stderr)
        status = self.set_scale(nodes, force, retire) if nodes is not None else self.scale_status()
        if not launch or nodes is None:
            return status
        deadline = time.time() + timeout
        last = None
        while True:
            self.actualize_step()
            status = self.scale_status()
            line = (f"phase={status.get('phase')} target={status.get('target')} serving={len(status.get('serving') or [])}"
                    f" starting={len(status.get('starting') or [])} retiring={','.join(status.get('retiring') or []) or '-'}"
                    + (f" blocked: {status['blocked']}" if status.get("blocked") else ""))
            if line != last:
                print(f"[cluster] scale: {line}", flush=True)
                last = line
            if status.get("phase") == "settled":
                return status
            if time.time() >= deadline:
                raise ClusterError(f"scale to {nodes} did not settle within {timeout:.0f}s ({line})")
            time.sleep(interval)

    def restart_node(self, node_id: str, fresh: bool = False, epoch: int | None = None) -> subprocess.Popen:
        """SIGKILL ``node_id`` if it runs, then start it again under a new registry epoch (the next
        free one unless ``epoch`` is given). ``fresh`` deletes its data root first (a replacement
        host); otherwise it restarts on its retained data root (a crashed host coming back, which
        replays its durable journals). The port slot and the Magnetar config dir are kept."""
        node = next(spec for spec in self.config.nodes if spec.node_id == node_id)
        self.kill(slot=node_id)
        process = self.processes.pop(node_id, None)
        if process is not None:
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                pass
        if fresh:
            data = self.config.node_data(node)
            if data.resolve().parent != self.config.root.resolve():
                raise ClusterError(f"refusing to delete {data}: not directly under the run root")
            shutil.rmtree(data, ignore_errors=True)
        node.epoch = epoch if epoch is not None else max(spec.epoch for spec in self.config.nodes) + 1
        return self.start_node(node_id)

    def wait_node_log(self, name: str, pattern: str, timeout: float = 300) -> str:
        """wait_log on a spawned process's log that gives up as soon as the process has exited: a
        node that fails at plugin start (a Direct Transport it cannot bind to, a missing plugin)
        exits within seconds, and waiting out the full timeout only hides the reason."""
        path = self.config.root / f"{name}.log"
        expression = re.compile(pattern)
        deadline = time.time() + timeout
        while True:
            # Plugin loggers (DirectTransport, the WA's global_save, ...) write only to the game log
            # in the node's data dir, not to the stdout log.
            # Only the logs of this start: an earlier start's game log would match stale lines.
            since = self.spawned_at.get(name, 0.0) - 5
            game_logs = sorted(log for log in (self.config.root / f"{name}-data").glob("SpaceEngineersDedicated_*.log")
                               if log.stat().st_mtime >= since)
            for candidate in [path, *game_logs]:
                if candidate.is_file():
                    for line in candidate.read_text(errors="replace").splitlines():
                        if expression.search(line):
                            return line
            process = self.processes.get(name)
            if process is not None and process.poll() is not None:
                tail = path.read_text(errors="replace").splitlines()[-5:] if path.exists() else []
                raise ClusterError(f"{name} exited with {process.returncode} before /{pattern}/: "
                                   + " | ".join(line.strip() for line in tail))
            if time.time() >= deadline:
                raise ClusterError(f"{path.name}: no line matching /{pattern}/ within {timeout}s")
            time.sleep(1)

    def _report_plugin_failures(self, node: NodeSpec) -> bool:
        """Print every plugin this instance declared and could not load; True if there were any."""
        failures = plugin_load_failures(self.config.magnetar_config(node),
                                        self.spawned_at.get(node.node_id, 0.0))
        for plugin, error in failures:
            print(f"[cluster] PLUGIN NOT LOADED on {node.node_id}: {plugin} - {error}")
        return bool(failures)

    @staticmethod
    def _role_key(node: NodeSpec) -> str:
        if node.role not in ("regular", "WA"):
            raise ValueError(f"invalid cluster node role: {node.role}")
        return "wa" if node.role == "WA" else "node"

    def _write_magnetar_config(self, node: NodeSpec) -> list[str]:
        """Sources, local plugins and profile of one node: the packaged compiled plugins as local
        plugins (release), else the source checkout as dev folders (development)."""
        extra = self.config.extra_plugins.get(self._role_key(node), [])
        if self.config.plugin_binaries is not None:
            return magnetar_config.write_config(self.config.magnetar_config(node), role=self._role_key(node),
                                                binaries=self.config.plugin_binaries,
                                                transport=self.config.transport_source,
                                                transport_binaries=self.config.transport_binaries,
                                                common_plugins=self.config.common_plugins,
                                                debug=self.config.debug_plugins, extra_local=extra)
        return magnetar_config.write_config(self.config.magnetar_config(node), role=self._role_key(node),
                                            cluster=self.config.plugin_source,
                                            transport=self.config.transport_source,
                                            transport_binaries=self.config.transport_binaries,
                                            common_plugins=self.config.common_plugins,
                                            debug=self.config.debug_plugins, extra_local=extra)

    def prepare_magnetar_config(self, node: NodeSpec) -> Path:
        """One Magnetar config dir per node: its own local plugin copy (or plugin compile cache in
        dev-folder mode - two launchers compiling into one cache would race), its own Magnetar log,
        and a profile with exactly the plugins of its role. Rewritten at every start so a new
        archive or a moved source tree takes effect; the compile cache inside survives, so a
        relaunch does not recompile unchanged sources.

        The GitHub folder is the exception and is SHARED - see share_github_cache."""
        config = self.config.magnetar_config(node)
        self._write_magnetar_config(node)
        if self.config.common_plugins is None:
            share_github_cache(config, MAGNETAR_GITHUB_CACHE)
        return config

    @staticmethod
    def _dedicated_id(text: str, node: NodeSpec) -> str:
        return re.sub(r"<DedicatedId>[^<]*</DedicatedId>", f"<DedicatedId>330200254399590{3000 + node.epoch}</DedicatedId>", text)

    def render_dedicated_config(self, node: NodeSpec) -> str:
        """The deployment's node template (Config.dedicated_template) with this node's DedicatedId."""
        template = self.config.dedicated_template
        try:
            return self._dedicated_id(template.read_text(), node)
        except OSError as error:
            raise ClusterError(f"node cfg template {template}: {error.strerror or error}") from None

    def verify_dedicated_config(self, node: NodeSpec) -> None:
        """Refuse a launch whose SpaceEngineers-Dedicated.cfg lacks an element the deployment requires
        (Config.dedicated_require, e.g. ExperimentalMode=true), or does not parse."""
        import xml.etree.ElementTree as ET
        cfg = self.config.node_data(node) / "SpaceEngineers-Dedicated.cfg"
        try:
            root = ET.parse(cfg).getroot()
        except (OSError, ET.ParseError) as error:
            raise ClusterError(f"{node.node_id}: {cfg} is not a readable config ({error})") from None
        wrong = []
        for name, value in self.config.dedicated_require.items():
            found = root.find(f".//{name}")
            if found is None or (found.text or "").strip() != value:
                wrong.append(f"{name}={None if found is None else (found.text or '').strip()!r} (need {value!r})")
        if wrong:
            raise ClusterError(f"{node.node_id}: {cfg} fails the deployment's requirements: {', '.join(wrong)}")

    def seed_dedicated_config(self, node: NodeSpec) -> None:
        """The stock server takes the premade world from SpaceEngineers-Dedicated.cfg.

        With a deployment template (Config.dedicated_template): the template, verbatim but for the node's
        DedicatedId, copied in when the data dir has none - and rewritten when the cfg there differs, so
        every node of the deployment runs the same settings however its dir was made. Without one: a
        minimal config when the data dir has none; a bench that writes its own (admins, session
        settings) keeps it. The world's own session settings win either way: the node plugin keeps the
        premade checkpoint's. The result is checked against Config.dedicated_require before a launch."""
        cfg = self.config.node_data(node) / "SpaceEngineers-Dedicated.cfg"
        if self.config.dedicated_template is not None:
            expected = self.render_dedicated_config(node)
            if not cfg.exists():
                cfg.write_text(expected)
            elif self._dedicated_id(cfg.read_text(errors="replace"), node) != expected:
                print(f"[cluster] {node.node_id}: {cfg.name} differs from the node template "
                      f"{self.config.dedicated_template}; rewriting it")
                cfg.write_text(expected)
            self.verify_dedicated_config(node)
            return
        if cfg.exists():
            self.verify_dedicated_config(node)
            return
        # MyConfigDedicatedData.Administrators is [XmlArrayItem("unsignedLong")]: <string> items are skipped (#1212).
        admins = "".join(f"\n    <unsignedLong>{admin}</unsignedLong>" for admin in self.config.administrators)
        cfg.write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <LoadWorld />
  <IP>0.0.0.0</IP>
  <SteamPort>8766</SteamPort>
  <ServerPort>{node.backend.rpartition(":")[2]}</ServerPort>
  <Administrators>{admins}
  </Administrators>
  <ServerName>{self.config.server_name}</ServerName>
  <WorldName>{self.config.world_template.name}</WorldName>
  <PauseGameWhenEmpty>false</PauseGameWhenEmpty>
  <AutoRestartEnabled>false</AutoRestartEnabled>
  <AutoUpdateEnabled>false</AutoUpdateEnabled>
  <IgnoreLastSession>true</IgnoreLastSession>
  <PremadeCheckpointPath>{self.config.premade_world(node)}</PremadeCheckpointPath>
  <RemoteApiEnabled>false</RemoteApiEnabled>
  <AutodetectDependencies>true</AutodetectDependencies>
  <NetworkType>steam</NetworkType>
  <DedicatedId>330200254399590{3000 + node.epoch}</DedicatedId>
</MyConfigDedicated>
""")
        self.verify_dedicated_config(node)

    def refresh_premade_world(self, node: NodeSpec) -> None:
        """Copy the split world into the node's premade directory when its dedicated config loads
        from there (a config that names another world - a warm-restart WA's - is left alone). The
        cluster saves under saves/ are read through CLUSTER_WORLD and not copied."""
        cfg = self.config.node_data(node) / "SpaceEngineers-Dedicated.cfg"
        target = self.config.premade_world(node)
        if f"<PremadeCheckpointPath>{target}</PremadeCheckpointPath>" not in cfg.read_text():
            return
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(self.config.split_world, target, ignore=shutil.ignore_patterns("saves"))

    def wait_ready(self, timeout: float = 300, nodes: list[str] | None = None) -> None:
        if self.config.multi_host and nodes is None:
            self.wait_ready_multi_host(max(timeout, 600))
            return
        pending = {"gateway": "Gateway listening"}
        for node in self.config.nodes:
            if nodes is not None and node.node_id not in nodes:
                continue
            pending[node.node_id] = "Game ready"
        deadline = time.time() + timeout
        while pending and time.time() < deadline:
            for name, marker in list(pending.items()):
                log = self.config.root / f"{name}.log"
                if log.exists() and marker in log.read_text(errors="replace"):
                    print(f"[cluster] {name}: {marker}")
                    del pending[name]
            for name, process in self.processes.items():
                # Exit code 0 is no excuse: a node whose world fails to load logs "Exiting.." and
                # leaves cleanly, and waiting out the timeout for it only hides the reason.
                if name in pending and process.poll() is not None:
                    raise ClusterError(f"{name} exited with {process.returncode} before it was ready")
            if pending:
                time.sleep(1)
        if pending:
            raise ClusterError(f"not ready within {timeout}s: {', '.join(sorted(pending))}")
        # T-0102: a node that lost a plugin is still "Game ready" - the process runs, the world
        # loads, only the plugin is missing. Report and carry on: by this point clients are
        # about to attach and halting is worse than a fleet-wide warning, whereas the warm-up
        # node above is checked while stopping is still free.
        for node in self.config.nodes:
            if nodes is not None and node.node_id not in nodes:
                continue
            if node.node_id in self.spawned_at:
                self._report_plugin_failures(node)
        # The Gateway starts --paused so no client is attached to a node that is still loading its
        # world. Ready means resume: without it a client is accepted, granted an identity, and then
        # force-reconnected when its backlog expires, because nothing ever attaches it.
        self.post("/resume")

    def stop(self, grace: float = 30) -> bool:
        """Graceful shutdown through the admin API, then stop what is left.

        True when every process left on its own inside the grace budget. False when the
        graceful path was unavailable or timed out and the survivors were SIGKILLed - that
        is a stop that may have cut drains and Final saves short, and the caller's exit
        code must say so (T-0048: two consecutive deploys read as clean because this
        printed "terminating" and exited 0).

        The gateway's graceful route answers only once every node is Empty or Dead (or its
        own timeoutSeconds passes), so the POST is sent in the background and the drain is
        observed here through process exits for the whole grace budget. Waiting on the
        POST itself with a 120 s timeout made the 2026-09-06 08:13 stop SIGKILL four
        draining nodes two minutes into a five-minute drain and print "unavailable".
        """
        try:
            self.get("/registry/status", timeout=10)
        except Exception as error:  # a dead or never-started gateway is not a stop failure
            return self.local_stop(grace, error)
        force_after = max(30.0, grace - 30.0)
        multi = self.config.multi_host
        if multi:
            # No launcher may act while the cluster shuts down: stop every host's agent first.
            self.stop_agents()

        def request() -> None:
            try:
                self.post(f"/registry/shutdown/graceful?timeoutSeconds={grace:.0f}"
                          f"&forceAfterSeconds={force_after:.0f}", timeout=grace + 60.0)
            except Exception as error:  # the observation below is what decides the outcome
                print(f"[cluster] graceful shutdown request ended: {error}")

        import threading
        threading.Thread(target=request, name="graceful-shutdown", daemon=True).start()
        started = time.time()
        deadline = started + grace
        def left() -> list:
            local = self.running_pids()
            if not multi:
                return local
            return local + [f"{pid}@{host}" for host, pids in self.remote_node_pids().items() for pid in pids]

        while time.time() < deadline and left():
            time.sleep(1 if not multi else 3)
        survivors = left()
        graceful = not survivors
        if graceful:
            print(f"[cluster] graceful shutdown complete after {time.time() - started:.0f}s")
        else:
            print(f"[cluster] graceful shutdown incomplete after {grace:.0f}s; "
                  f"terminating {len(survivors)} process(es)")
            local = [pid for pid in survivors if isinstance(pid, int)]
            if local:
                self._dump_survivor(local)
        self.kill()
        return graceful

    def local_stop(self, grace: float, error: Exception) -> bool:
        """T-0347: the stop when the registry does not answer. The graceful path needs it (it closes the nodes,
        takes their releases and hears them Empty), and without it this used to SIGKILL every node at once: no
        Final saves, the world back at its last periodic saves (r113 drill D4, 2026-10-01). Instead every node
        is asked on its own control port for a local stop: it writes a Final of each partition it owns to its
        own save store, journals the ones the registry could not be told about, and exits; the next start
        reports the journals before recovery (LateSaves.cs). Nodes get the grace less 20 s; what is still
        alive at the grace is SIGKILLed as before.

        Always False: the registry was not there to record a clean shutdown, the World Authority's global save
        is not covered, and the next start is a Recovery start - the caller's exit code must say so."""
        multi = self.config.multi_host
        if multi:
            self.stop_agents()   # no launcher may relaunch a node that exits
        budget = max(15.0, grace - 20.0)
        started = time.time()
        asked = {self.config.host: local_stop_nodes(self.config.root, budget)}
        for host in self.remote_hosts() if multi else []:
            try:
                done = self.host_cli(host, ["hostd", "local-stop", "--budget", f"{budget:.0f}"], timeout=60)
                asked[host] = json.loads(done.stdout.decode())["asked"]
            except (ClusterError, ValueError, KeyError, subprocess.TimeoutExpired) as failure:
                print(f"[cluster] local stop on {host}: {failure}")
        accepted = sum(1 for nodes in asked.values() for node in nodes if "error" not in node)
        if not accepted:
            print(f"[cluster] graceful shutdown unavailable ({error}); no node took a local stop; terminating")
            self.kill()
            return False
        print(f"[cluster] graceful shutdown unavailable ({error}); {accepted} node(s) asked for a local stop "
              f"(Final saves to their own stores, {budget:.0f} s): "
              + "; ".join(f"{host}: " + ", ".join(f"{node.get('node')}"
                                                 + (f" owned={node['owned']}" if "owned" in node else f" FAILED {node.get('error')}")
                                                 for node in nodes) for host, nodes in asked.items() if nodes))

        def left() -> list:
            # Regular nodes only: the World Authority was not asked and is killed with the rest below. (On another
            # host the process list cannot tell it apart; a WA there is waited for until the grace.)
            local = [pid for pid, _, _ in regular_node_processes(self.config.root)]
            if not multi:
                return local
            return local + [f"{pid}@{host}" for host, pids in self.remote_node_pids().items() for pid in pids]

        deadline = started + grace
        while time.time() < deadline and left():
            time.sleep(1 if not multi else 3)
        survivors = left()
        if survivors:
            print(f"[cluster] local stop incomplete after {grace:.0f}s; terminating {len(survivors)} node process(es) "
                  "whose Finals may be missing")
            local = [pid for pid in survivors if isinstance(pid, int)]
            if local:
                self._dump_survivor(local)
        else:
            print(f"[cluster] local stop: every node exited after {time.time() - started:.0f}s; each node log says "
                  "'local stop complete' or INCOMPLETE; terminating the rest (not a clean shutdown: the next start "
                  "is a Recovery start that reports the journaled Finals)")
        self.kill()
        return False

    def _dump_survivor(self, pids: list[int]) -> None:
        """T-0076: a process still alive at the deadline IS the evidence, and the SIGKILL that
        follows destroys it. The logs of a lingering process cannot say whether an exit
        watchdog never ran, the exit hung in shutdown handlers, or the process was suspended;
        a minidump of one survivor (thread stacks; a regular node in preference to the gateway
        or the WA) answers that at the next occurrence. One is enough and costs seconds; a
        full fleet of dumps would cost minutes and tens of GB right where the stop is late.

        The spawned pid is the launch wrapper (a bash that sets ulimit and execs nothing), so
        the dump descends to the dotnet child; dotnet-dump reaches the runtime through a
        diagnostics socket in the TARGET's TMPDIR, so the target's own environment is passed
        (2026-09-07 23:18: dumping the wrapper pid failed 'Unable to connect to Process')."""
        tool = os.path.expanduser("~/.dotnet/tools/dotnet-dump")
        if not os.access(tool, os.X_OK):
            print(f"[cluster] no {tool}; survivors not dumped (T-0076)")
            return

        def slot_of(pid: int) -> str:
            try:
                cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
            except OSError:
                return "?"
            match = re.search(r"/((?:node|wa)-\d+)-data", cmdline)
            return match.group(1) if match else ("gateway" if re.search(r"Cluster(Registry|Gateway)", cmdline) else "?")
        def comm_of(pid: int) -> str:
            try:
                return Path(f"/proc/{pid}/comm").read_text().strip()
            except OSError:
                return ""

        def runtime_pid(pid: int) -> int:
            """The pid that hosts the .NET runtime: the process itself when it is dotnet,
            otherwise its dotnet child (the launch wrapper is a plain bash)."""
            if comm_of(pid) == "dotnet":
                return pid
            children = subprocess.run(["pgrep", "-P", str(pid)], capture_output=True, text=True)
            for candidate in (int(child) for child in children.stdout.split()):
                if comm_of(candidate) == "dotnet":
                    return candidate
            return pid

        def environ_of(pid: int) -> dict[str, str]:
            try:
                raw = Path(f"/proc/{pid}/environ").read_bytes()
            except OSError:
                return dict(os.environ)
            env = dict(os.environ)
            for entry in raw.split(b"\0"):
                if b"=" in entry:
                    key, value = entry.split(b"=", 1)
                    if key == b"TMPDIR":
                        env["TMPDIR"] = value.decode(errors="replace")
            return env

        labelled = [(slot_of(pid), pid) for pid in pids]
        labelled.sort(key=lambda entry: (not entry[0].startswith("node-"), entry[0], entry[1]))
        slot, spawned = labelled[0]
        pid = runtime_pid(spawned)
        dumps = self.config.root / "dumps"
        dumps.mkdir(parents=True, exist_ok=True)
        out = dumps / f"stop-survivor-{slot}.{pid}.{time.strftime('%Y%m%d-%H%M%S')}.dmp"
        print(f"[cluster] dumping survivor {slot} (pid {pid}, spawned {spawned}) to {out} "
              f"before the SIGKILL (T-0076)")
        try:
            result = subprocess.run([tool, "collect", "-p", str(pid), "--type", "Mini", "-o", str(out)],
                                    capture_output=True, text=True, timeout=120, env=environ_of(pid))
            tail = (result.stdout + result.stderr).strip().splitlines()[-1:] or [""]
            print(f"[cluster] dotnet-dump exit {result.returncode}: {tail[0]}")
        except subprocess.TimeoutExpired:
            print("[cluster] dotnet-dump did not finish within 120s; killing anyway")

    def kill(self, slot: str | None = None) -> list[int]:
        """SIGKILL a node or everything. Discovery is by command line, never a remembered pid. Multi-host:
        everything means every host (agents first, so none relaunches); a node on another host is killed
        there by its own host's CLI."""
        if self.config.multi_host and not slot:
            self.stop_agents()
            self.kill_remote_nodes()
        elif self.config.multi_host and slot and not self._node_pids(slot):
            host = self._node_host(slot)
            if host and host != self.config.host:
                done = self.host_cli(host, ["kill", slot], timeout=60)
                return json.loads(done.stdout.decode() or "{}").get("killed", [])
        pids = self._kill_pids(slot)
        if not slot:
            self.processes.clear()
        else:
            self._ack_kill_incumbent(slot)
        return pids

    def _node_host(self, node_id: str) -> str | None:
        try:
            return self.resolve_selector(node_id).get("host")
        except ClusterError:
            return None

    def _kill_pids(self, slot: str | None = None) -> list[int]:
        """SIGKILL one node's processes (or every cluster process) and nothing else: no registry report."""
        if slot:
            pids = self._node_pids(slot)
        else:
            # The restart loop first, or it would start gateway-rs again.
            pids = self.gateway_rs_pids() + self.running_pids()
            supervisors = []
            for pid in pids:
                try:
                    args = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
                    if any(argument.endswith(b"/gateway_supervisor.py") for argument in args):
                        supervisors.append(pid)
                        os.kill(pid, signal.SIGTERM)
                except (OSError, ProcessLookupError):
                    pass
            # Give the loop time to persist ExpectedExit before its existing SIGKILL fallback.
            deadline = time.monotonic() + 2.5
            while supervisors and time.monotonic() < deadline:
                supervisors = [pid for pid in supervisors if Path(f"/proc/{pid}").exists()]
                if supervisors:
                    time.sleep(0.05)
            # Re-discover after waiting, avoiding stale child PIDs reused during the grace period.
            survivors = self.gateway_rs_pids() + self.running_pids()
            original_pids = pids
            pids = survivors
        for pid in pids:
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        if not slot:
            supervisor = self.processes.get("gateway-rs")
            if supervisor is not None:
                try:
                    supervisor.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    pass
        return pids if slot else original_pids

    def _ack_kill_incumbent(self, node_id: str) -> None:
        """P1-REC-10: this deployment has no executor process, so the harness is the executor's
        kill hand - and it owes the Registry the executor's acknowledgement too. An admin
        node-kill parks the slot on IncumbentAction=Kill until the executor reports the
        incumbent Gone; without this report the still-wanted slot never respawns
        (SpawnAllowed stays false, and the slot plan is persisted across Gateway restarts).
        Slots with no pending Kill are left alone so crash-detection scenarios keep observing
        the Gateway discover a dead node by itself."""
        def field(mapping, name):
            if not isinstance(mapping, dict):
                return None
            for key, value in mapping.items():
                if key.lower() == name.lower():
                    return value
            return None

        if self.config.multi_host:
            return   # cluster-hostd reports Gone through its fenced lease
        try:
            plan = self.get("/registry/node-plan")
            observations = []
            for slot in plan if isinstance(plan, list) else []:
                if str(field(slot, "incumbentAction")) not in ("Kill", "2"):
                    continue
                if node_id not in (field(slot, "incumbentNode"), field(slot, "observedNode")):
                    continue
                observations.append({
                    "slotKey": field(slot, "slotKey"),
                    "attemptKey": field(slot, "lastAttemptKey") or "harness-kill",
                    "state": "Gone",
                    "node": None,
                    "failure": None,
                })
            if observations:
                self.post("/registry/executors/heartbeat",
                          body={"executor": "harness", "observations": observations})
        except ClusterError as error:  # a dead or never-started gateway leaves nothing to ack
            print(f"[cluster] kill incumbent ack skipped ({error})")

    # -- T-0321 logs and addresses of nodes on any host ---------------------------------------------

    def logs(self, selector: str, lines: int = 100, follow: bool = False, game: bool = False) -> int:
        """A process's log read where it lives: this host directly, another host over ssh (tail, never a copy).
        ``gateway``/``gateway-rs``/``hostd`` name this host's own logs."""
        if selector in ("gateway", "gateway-rs", "hostd"):
            host, name = self.config.host, selector
        else:
            try:
                entry = self.resolve_selector(selector)
                host, name = entry.get("host") or self.config.host, entry.get("node")
            except ClusterError:
                # A node that is gone (crashed, killed, removed) or a gateway that is down: the post-mortem
                # is exactly when its log is wanted. A node id's log is found on whichever host has it.
                if not (self.config.multi_host and re.fullmatch(r"(node|wa)-\d+", selector)):
                    raise
                host, name = self.log_host(selector), selector
        root = self.config.host_entry(host)["runRoot"] if self.config.multi_host else str(self.config.root)
        root = root or str(self.config.root)
        target = (f"$(ls -t {shlex.quote(root)}/{shlex.quote(name)}-data/SpaceEngineersDedicated_*.log 2>/dev/null | head -1)"
                  if game else shlex.quote(f"{root}/{name}.log"))
        script = f"tail -n {int(lines)} {'-F ' if follow else ''}{target}"
        if not self.config.multi_host or self.config.host_entry(host)["ssh"] == "local":
            return subprocess.call(["bash", "-c", script])
        return subprocess.call([*ssh_argv(self.config.host_entry(host)["ssh"]), script])

    def log_host(self, node: str) -> str:
        """The host whose run root holds ``<node>.log`` (node ids are never reused, so at most one does)."""
        for host, entry in (self.config.topology() or {"hosts": {}})["hosts"].items():
            path = f"{entry['runRoot'] or self.config.root}/{node}.log"
            try:
                if self.run_on(host, f"test -f {shlex.quote(path)}", timeout=30, check=False).returncode == 0:
                    return host
            except subprocess.TimeoutExpired:
                continue
        raise ClusterError(f"{node}: not a live node, and no host of the hosts file has {node}.log")

    def node_url(self, selector: str) -> str:
        """``http://address:port`` of a node's control endpoint (from the registry), plus how to authenticate
        when it is LAN-bound: the header value is read from a file, never printed."""
        node = self.resolve_selector(selector)["node"] if not re.fullmatch(r"(node|wa)-\d+", selector) else selector
        url = f"http://{self.node_endpoint(node)}"
        if self.config.multi_host:
            return f"{url}\t# header: Authorization: Bearer $(cat {self.config.secrets / 'admin.token'})"
        return url

    # -- admin API ---------------------------------------------------------

    def _request(self, method: str, path: str, base: str | None = None, timeout: float = 60,
                 body: dict | None = None, headers: dict | None = None, token: str | None = None) -> dict | list | str:
        url = f"http://{base or self.config.gateway_bind}{path}"
        request = urllib.request.Request(url, method=method)
        # Scope the Gateway credential to its configured origin, never arbitrary node/test endpoints; a node
        # control endpoint the registry lists gets it only through ``token`` (Cluster.node, multi-host).
        token = self.config.admin_token() if base is None or base in (self.config.gateway_control, self.config.gateway_bind) \
            else token
        if token:
            request.add_header("Authorization", "Bearer " + token)
        for name, value in (headers or {}).items():
            request.add_header(name, value)
        if body is not None:
            request.data = json.dumps(body).encode()
            request.add_header("Content-Type", "application/json")
        elif method == "POST":
            request.data = b""
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = response.read().decode()
        except urllib.error.HTTPError as error:
            # Never swallow an HTTP error: Findings 2026-07-27, a harness that did read a broken
            # injection call as "no movement" for several rounds.
            text = error.read().decode(errors="replace")
            raise ClusterHttpError(f"{method} {url} -> {error.code}: {text[:400]}", error.code, text) from None
        except urllib.error.URLError as error:
            raise ClusterError(f"{method} {url} -> {error.reason}") from None
        try:
            return json.loads(body)
        except json.JSONDecodeError:
            return body

    def get(self, path: str, base: str | None = None, timeout: float = 60):
        return self._request("GET", path, base, timeout)

    def post(self, path: str, base: str | None = None, timeout: float = 60, body: dict | None = None):
        return self._request("POST", path, base, timeout, body)

    def node(self, node_id: str, path: str, method: str = "GET", timeout: float = 60):
        """A node's own control route. One host: the port from this launcher's ledger, no token (as ever).
        Multi-host (T-0321): the control endpoint the registry lists for it (any host), with the admin token
        a LAN-bound node control requires."""
        if not self.config.multi_host:
            control = next(node.control for node in self.config.nodes if node.node_id == node_id)
            return self._request(method, f"/cluster{path}", control, timeout)
        return self._request(method, f"/cluster{path}", self.node_endpoint(node_id), timeout,
                             token=self.config.admin_token())

    def node_endpoint(self, node_id: str) -> str:
        """host:port of a node's control endpoint as the registry lists it (ControlEndpoint of its row);
        the local ledger's entry when the registry has none yet."""
        for row in _field(self.get("/registry/status"), "nodes") or []:
            if str(_field(row, "node")) == node_id and _field(row, "controlEndpoint"):
                return re.sub(r"^https?://", "", str(_field(row, "controlEndpoint"))).rstrip("/")
        local = next((node.control for node in self.config.nodes if node.node_id == node_id), None)
        if local is None:
            raise ClusterError(f"{node_id}: no control endpoint in the registry or this host's ledger")
        return local

    def resolve_selector(self, selector: str, view: dict | None = None) -> dict:
        """node-9 | 12345@polaris | polaris#2 -> the node's entry of the operator view (host, pid, ...)."""
        if not SELECTOR.match(selector) and not re.fullmatch(r"wa-\d+", selector):
            raise ClusterError(f"selector {selector!r}: use a node id (node-9), PID@host (12345@polaris)"
                               " or host#position (polaris#2)")
        view = view or self.node_control()
        entries = [*(view.get("nodes") or []), *(view.get("notCounted") or [])]
        for entry in entries:
            if selector == entry.get("node") \
                    or "@" in selector and selector == f"{entry.get('pid')}@{entry.get('host')}" \
                    or "#" in selector and selector == f"{entry.get('host')}#{entry.get('position')}":
                return entry
        raise ClusterError(f"no live node matches {selector!r}")

    def status(self) -> dict:
        return {
            "registry": self.get("/registry/status"),
            "transport": self.get("/transport/status"),
            "nodePlan": self.get("/registry/node-plan"),
            "pids": self.running_pids(),
        }

    def health_problems(self, status: dict | None = None) -> list[str]:
        """P3-REC-01: 'looks up but admits nobody' must be visible from ONE status call. Unhealthy when
        no cluster runs, when the phase is short of Serving, when the executor plan carries a Failed
        slot, when a configured node slot has no process at all (a rejected registration exits on its
        own and leaves no registry row - the missing process is the one observable it leaves behind),
        or when a node process is alive but its own lifecycle is not Serving.

        The WA is the exception: SetNodeState("Serving") lives at the end of RegisterNodeAndClaims,
        and ClusterRuntime.Update routes a WA process to m_worldAuthorityPersistence instead, so
        Registered is its healthy steady state. Its Serving-equivalent is asserted from the registry
        row: an issued authority generation, and a lease the heartbeat keeps renewing."""
        status = status or self.status()
        problems: list[str] = []
        if not status.get("pids"):
            problems.append("no cluster processes are running")
            return problems

        def field(mapping, name):
            if not isinstance(mapping, dict):
                return None
            for key, value in mapping.items():
                if key.lower() == name.lower():
                    return value
            return None

        registry = status.get("registry") or {}
        phase = field(field(registry, "lifecycle") or registry, "phase")
        if phase is not None and str(phase) not in ("Serving", "1"):   # ClusterPhase.Serving serializes as 1
            problems.append(f"cluster phase is {phase}, not Serving")
        plan = status.get("nodePlan")
        for slot in plan if isinstance(plan, list) else []:
            if str(field(slot, "observed")) in ("Failed", "3"):        # SpawnState.Failed serializes as 3
                problems.append(f"planned node slot {field(slot, 'slotKey')} spawn is Failed"
                                + (f": {field(slot, 'lastFailure')}" if field(slot, "lastFailure") else ""))
            elif field(slot, "failureAlert") is True:
                problems.append(f"planned node slot {field(slot, 'slotKey')} has a spawn-failure alert")
        for node in self.config.nodes:
            if not self._pgrep(f"MagnetarInterim.*{re.escape(str(self.config.root))}/{node.node_id}-data"):
                problems.append(f"{node.node_id} has no running process")
                continue
            try:
                state = self.node(node.node_id, "/node-state", timeout=15)
            except ClusterError as error:
                # An old binary without the route answers (404 / unknown route) and is not a health
                # verdict. NO answer is one: node-3 2026-08-11 sat fenced for 15+ minutes with its
                # single listener thread wedged behind game-thread waits, /node-state timing out -
                # and the silent `continue` here made the zombie invisible to status.
                if "404" not in str(error) and "unknown cluster control route" not in str(error):
                    problems.append(f"{node.node_id} process is alive but its control endpoint"
                                    f" does not answer /node-state: {error}")
                continue
            except Exception:
                continue
            healthy = (None, "Serving", "Registered") if node.role == "WA" else (None, "Serving")
            if isinstance(state, dict) and state.get("state") not in healthy:
                problems.append(f"{node.node_id} process is alive but reports {state.get('state')}"
                                + (f" ({state.get('detail')})" if state.get("detail") else ""))
        rows = {str(field(row, "node")): row for row in (field(registry, "nodes") or [])}
        for node in self.config.nodes:
            if node.node_id not in rows:
                continue   # an unregistered WA already reads as Registering in the loop above
            row = rows[node.node_id]
            if node.role == "WA" and not field(row, "authorityGeneration"):
                problems.append(f"{node.node_id} is registered without an authority generation")
            # A Dead registry row is the gateway's own verdict on the node - surface it no matter
            # what the process or its control endpoint claim (node-3 2026-08-11: process alive,
            # registry row Dead, status green).
            state_value = str(field(row, "state"))
            if state_value in ("Dead", "2"):
                problems.append(f"{node.node_id} registry entry is Dead (epoch {field(row, 'epoch')})")
                continue
            expires = field(row, "leaseExpires")
            # Closed/Empty rows stop heartbeating by design (drain linger); only an Active row
            # with a stale lease is a dead heartbeat.
            if expires and state_value in ("Active", "0"):
                lease = datetime.fromisoformat(str(expires))
                # Missing by more than one full TTL means the heartbeat stopped; a marginally
                # stale row is just the sampling interval.
                if datetime.now(timezone.utc) > lease + timedelta(seconds=self.config.lease_ttl_seconds):
                    problems.append(f"{node.node_id} registry lease expired at {expires}"
                                    " and is not being renewed")
        # LostEarthStarterBase.md (2026-08-10): 27 of 37 partitions sat permanently unowned -
        # never simulated, never recoverable - while status exited 0. A partition with no owner,
        # no recovery in flight and no retirement is world content nobody serves; that is an
        # outage, not a transient. (A node death shows here for the few seconds until recovery
        # flips Recovering=true - a report that clears itself on the next status call.)
        try:
            partition_rows = self.get("/registry/partitions")
        except Exception:
            partition_rows = []
        orphaned = sorted(int(field(row, "partition")) for row in partition_rows
                          if isinstance(row, dict)
                          and field(row, "node") is None
                          and field(row, "recovering") is not True
                          and field(row, "retired") is not True
                          # T-0193: unowned BY DESIGN - an offline partition loads on demand.
                          and field(row, "offline") is not True)
        if orphaned:
            listed = ", ".join(str(partition) for partition in orphaned[:10])
            if len(orphaned) > 10:
                listed += f", … ({len(orphaned) - 10} more)"
            problems.append(f"{len(orphaned)} partition(s) unowned and not recovering: {listed}")
        return problems

    def diag(self, depth: str = "partitions", **filters) -> dict:
        query = "&".join(f"{key}={value}" for key, value in filters.items() if value is not None)
        return self.get(f"/diag?depth={depth}" + ("&" + query if query else ""))

    def handover_config(self, updates: dict | None = None) -> dict:
        """HandoverImprovements.md §2: the runtime handover knobs. GET without updates; PUT merges
        {key: value} (value None removes the key), the gateway fans it out to every Active node."""
        if not updates:
            return self.get("/admin/v1/handover-config")["data"]
        operation = self._request("PUT", "/admin/v1/handover-config", body=updates,
                                  headers={"Idempotency-Key": str(uuid.uuid4())})
        return operation["data"]

    def trigger(self, task: str, wait: bool = False, **scope) -> dict:
        query = "&".join(f"{key}={value}" for key, value in scope.items() if value is not None)
        result = self.post(f"/trigger/{task}" + ("?" + query if query else ""))
        if wait:
            # The node-side triggers run synchronously on the game thread, so the POST returning IS
            # the completion. `balance` only releases the cooldown, so its outcome is observable one
            # balancer round later.
            if task == "balance":
                time.sleep(2)
        return result


def informational_version(dll: Path) -> str:
    """An assembly's informational version, read as the UTF-16 string it carries
    (``2.4.1.1+<commit>`` for Magnetar); no .NET needed."""
    try:
        data = dll.read_bytes()
    except OSError:
        return "ABSENT"
    match = re.search(rb"(?:\d\x00)+\.\x00(?:\d\x00)+\.\x00(?:\d\x00)+(?:\.\x00(?:\d\x00)+)?\+\x00(?:[0-9a-f]\x00){7,40}", data)
    if match is None:
        match = re.search(rb"(?:\d\x00)+\.\x00(?:\d\x00)+\.\x00(?:\d\x00)+\.\x00(?:\d\x00)+", data)
    return match.group(0).decode("utf-16-le") if match else "NO-VERSION-STRING"


def _proto_varint(buf: bytes, i: int) -> tuple[int, int]:
    result, shift = 0, 0
    while True:
        byte = buf[i]
        i += 1
        result |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return result, i


def _proto_fields(buf: bytes) -> list[tuple[int, int, object]]:
    """Flat decode of one protobuf message: (field, wire type, value)."""
    i, out = 0, []
    while i < len(buf):
        key, i = _proto_varint(buf, i)
        field, wire = key >> 3, key & 7
        if wire == 0:
            value, i = _proto_varint(buf, i)
        elif wire == 1:
            value, i = buf[i:i + 8], i + 8
        elif wire == 2:
            length, i = _proto_varint(buf, i)
            value, i = buf[i:i + length], i + length
        elif wire == 5:
            value, i = buf[i:i + 4], i + 4
        else:
            raise ValueError(f"unsupported wire type {wire}")
        out.append((field, wire, value))
    return out


def hub_pins(snapshot: Path) -> dict[str, str]:
    """{repo: commit} pinned by a Sources/Hubs/*.bin snapshot. protobuf-net writes PluginData[]
    as repeated field 1; each item carries Id (1) and its GitHubPlugin subtype under the
    ProtoInclude tag 103 (Commit 1, RepoId 6) - Pulsar Shared/Data/PluginData.cs, GitHubPlugin.cs.
    Mod and obsolete entries (104, 100) are not pins and are skipped."""
    pins = {}
    for field, wire, item in _proto_fields(snapshot.read_bytes()):
        if field != 1 or wire != 2:
            continue
        plugin_id, github = None, None
        for sub_field, sub_wire, value in _proto_fields(item):
            if sub_field == 1 and sub_wire == 2:
                plugin_id = value.decode("utf-8", "replace")
            elif sub_field == 103 and sub_wire == 2:
                github = value
        if github is None:
            continue
        commit, repo = None, None
        for sub_field, sub_wire, value in _proto_fields(github):
            if sub_field == 1 and sub_wire == 2:
                commit = value.decode("utf-8", "replace")
            elif sub_field == 6 and sub_wire == 2:
                repo = value.decode("utf-8", "replace")
        pins[repo or plugin_id or "?"] = commit or "NO-COMMIT"
    return pins


def last_check_after(sources_xml: Path, since: float) -> bool:
    """True when the <LastCheck> in a Sources/sources.xml is newer than the epoch ``since``:
    the launcher re-read the hub after that instant. Pulsar writes it as UTC with 7 fractional
    digits (2026-09-14T23:18:10.4731603Z), which fromisoformat does not take unchanged."""
    if not sources_xml.is_file():
        return False
    match = re.search(r"<LastCheck>([^<]*)</LastCheck>", sources_xml.read_text(errors="replace"))
    if not match:
        return False
    text = re.sub(r"(\.\d{6})\d+", r"\1", match.group(1).strip()).replace("Z", "+00:00")
    try:
        stamp = datetime.fromisoformat(text)
    except ValueError:
        return False
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=timezone.utc)
    return stamp.timestamp() >= since


def wait_log(path: Path, pattern: str, timeout: float = 120) -> str:
    """Wait for a log line. Never a blind sleep - Findings: fixed waits hide startup failures."""
    expression = re.compile(pattern)
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.exists():
            for line in path.read_text(errors="replace").splitlines():
                if expression.search(line):
                    return line
        time.sleep(1)
    raise ClusterError(f"{path.name}: no line matching /{pattern}/ within {timeout}s")


def wait_until(predicate, timeout: float, description: str, interval: float = 1.0):
    """Barrier on an observable condition, returning its value. Never a bare sleep."""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = predicate()
        if last:
            return last
        time.sleep(interval)
    raise ClusterError(f"timed out after {timeout}s waiting for {description} (last={last!r})")


def deployed_node_set(root: Path) -> tuple[int, bool] | None:
    """(regular node count, wa-1 present) read from the ``<node>-magnetar`` config dirs an
    earlier start left under ``root``; None when there are none.

    ``prewarm`` and ``versions`` act on the node dirs that EXIST, not on the --nodes/--spares
    defaults: against the regression run root (node-1..5 + wa-1, built by regression.env) the
    defaults (2 regulars, no spare) would have warmed and propagated the wrong set, and wa-1
    would have been rewritten onto the ports of a three-node layout (first live pre-warm,
    2026-09-15 01:18). The regular count is the highest node-N present - the slot layout is
    positional, so that reproduces the ports every existing dir was written with."""
    if not root.is_dir():
        return None
    numbers = [int(m.group(1)) for m in
               (re.fullmatch(r"node-(\d+)-magnetar", entry.name) for entry in root.iterdir() if entry.is_dir()) if m]
    wa = (root / "wa-1-magnetar").is_dir()
    if not numbers and not wa:
        return None
    return (max(numbers) if numbers else 1), wa


def build_config(args: argparse.Namespace, from_root: bool = False) -> Config:
    root = Path(args.root).expanduser()
    # T-0320: with a port-slot ledger the node set comes from it; node ids grow without bound, so the
    # highest node-N dir says nothing about the layout any more.
    if from_root and not (root / "node-set.json").is_file():
        deployed = deployed_node_set(root)
        if deployed is not None:
            regulars, wa = deployed
            args.nodes, args.spares = regulars, 0
            names = [f"node-{i}" for i in range(1, regulars + 1)] + (["wa-1"] if wa else [])
            print(f"[cluster] node set from the config dirs under {root}: {', '.join(names)}"
                  + ("" if wa else " (no wa-1 dir: node-1 is the warm-up node)"))
        else:
            print(f"[cluster] no <node>-magnetar dirs under {root}: node set from --nodes/--spares")
    node_env: dict[str, str] = {}
    node_env_by_id: dict[str, dict[str, str]] = {}
    for entry in args.node_env:
        head, eq, value = entry.partition("=")
        if not eq:
            raise ClusterError(f"--node-env needs [NODE:]KEY=VALUE, got {entry!r}")
        node_id, colon, key = head.rpartition(":")
        (node_env_by_id.setdefault(node_id, {}) if colon else node_env)[key] = value
    gateway_args = shlex.split(os.environ.get("CLUSTER_GATEWAY_ARGS", "")) + list(args.gateway_arg)
    return Config(
        root=root,
        steam_listen=args.steam,
        world_template=Path(args.world).expanduser() if args.world else None,
        split_step=args.split_step,
        port_base=args.port_base,
        gateway_listen=args.listen,
        node_env=node_env,
        node_env_by_id=node_env_by_id,
        gateway_args=gateway_args,
        server_name=args.name,
        member_limit=args.member_limit,
        administrators=[int(steam_id) for steam_id in args.admin],
        regular_nodes=args.nodes,
        spare_nodes=args.spares,
        debug_plugins=os.environ.get("CLUSTER_DEBUG_PLUGINS", "") in ("1", "true", "yes"),
        **({"hosts_file": Path(args.hosts).expanduser()} if getattr(args, "hosts", None) else {}),
    )


def nodes_command(cluster: Cluster, args: argparse.Namespace, out=sys.stdout) -> int:
    """`cluster nodes list|set|remove|recycle|history|prune`."""
    if args.verb == "list":
        view = cluster.node_control()
        out.write(json.dumps(view, indent=2, default=str) + "\n" if args.json
                  else "\n".join(format_node_control(view)) + f"\n\nstates: {STATE_LEGEND}\n")
        return 0
    if args.verb == "history":
        page = cluster.nodes_history(limit=args.limit, host=args.host)
        if args.json:
            out.write(json.dumps(page, indent=2, default=str) + "\n")
            return 0
        for record in page.get("records") or []:
            out.write(f"{record.get('endedAt', '?')}  {record.get('node', '?'):<7} @{record.get('host', '?')}"
                      f" pid {record.get('pid') or '?'} age {fmt_age(record.get('ageSeconds'))}"
                      f" {record.get('outcome', '?')} by {record.get('requestedBy') or record.get('actor') or '?'}"
                      f"{' (forced)' if record.get('forced') else ''}: \"{record.get('why') or ''}\""
                      + (f" -> {record['replacedBy']}" if record.get("replacedBy") else "") + "\n")
        return 0
    if args.verb == "publish-rotation":
        out.write(json.dumps(cluster.publish_rotation(args.lifetimes_file, args.valid_for), indent=2) + "\n")
        return 0
    if args.verb == "prune":
        out.write(json.dumps(cluster.nodes_prune(apply=args.apply), indent=2) + "\n")
        return 0
    if args.verb == "set":
        if args.serving is None and args.spares is None:
            raise ClusterError("nodes set needs --serving and/or --spares")
        result = cluster.nodes_set(args.host, args.serving, args.spares, args.reason)
        done = lambda view: all(  # noqa: E731
            (host.get(key) or {}).get("actual") == (host.get(key) or {}).get("target")
            for host in view.get("hosts") or [] if host.get("host") == args.host for key in ("serving", "spares"))
    else:
        result = cluster.nodes_stop(args.verb, args.selector, now=args.now, timeout=args.drain_timeout,
                                    reason=args.reason)
        gone = result.get("node") or result.get("target")
        done = lambda view: gone not in {node.get("node") for node in view.get("nodes") or []}  # noqa: E731
    out.write(json.dumps(result, indent=2, default=str) + "\n")
    if args.wait:
        cluster.nodes_wait(done, timeout=args.timeout, out=out)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cluster", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    env_root = os.environ.get("CLUSTER_ROOT") or None
    parser.add_argument("--root", default=env_root, required=env_root is None,
                        help="run root: node data, logs, registry, dumps (never /tmp - it is tmpfs here);"
                             " defaults to CLUSTER_ROOT, required when that is not set")
    parser.add_argument("--world", default=os.environ.get("CLUSTER_WORLD_TEMPLATE"), help="world template to split")
    parser.add_argument("--steam", default=os.environ.get("CLUSTER_STEAM_LISTEN", "0.0.0.0:27016"),
                        help="expose the Steam transport here (default 0.0.0.0:27016 on this host);"
                             " CLUSTER_STEAM_LISTEN overrides, empty string disables (TestPlan §1.7)")
    parser.add_argument("--listen", default=os.environ.get("CLUSTER_LISTEN"),
                        help="Direct Transport frontend the Gateway binds and clients --connect to"
                             " (default 127.0.0.1:27116 + port base)."
                             " Alongside --steam the Gateway refuses a publicly reachable bind, so a"
                             " LAN address needs it listed in CLUSTER_INTERNAL_NETWORKS")
    parser.add_argument("--member-limit", type=int, default=int(os.environ.get("CLUSTER_MEMBER_LIMIT", 16)),
                        help="player cap advertised on Steam and enforced by gateway admission;"
                             " CLUSTER_MEMBER_LIMIT overrides")
    parser.add_argument("--nodes", type=int, default=int(os.environ.get("CLUSTER_NODES", 2)),
                        help="regular nodes to run, 1..3, beside the World Authority (default 2)")
    parser.add_argument("--spares", type=int, default=int(os.environ.get("CLUSTER_SPARES", 0)),
                        help="extra regular slots started as hot spares (HotSpareNodes.md);"
                             " CLUSTER_SPARES overrides (default 0)")
    parser.add_argument("--hosts", default=None,
                        help="hosts file (JSON, applied by start/fresh only): per host serving/spares and"
                             " RAM/disk floors; default CLUSTER_HOSTS_FILE, else <root>/hosts.json, else one"
                             " built from --nodes/--spares for this host")
    parser.add_argument("--admin", action="append", help="Steam id admitted as an admin; repeatable")
    parser.add_argument("--name", default=os.environ.get("CLUSTER_NAME", "Magnetar Cluster Test"),
                        help="server name published in the Steam browser row")
    parser.add_argument("--port-base", type=int, default=int(os.environ.get("CLUSTER_PORT_BASE", "0") or 0),
                        help="offset added to every port of the plan (Gateway control 29416, Direct Transport"
                             " 27116, node backends 28416+i, controls 29416+i) so two clusters can share a"
                             " host; CLUSTER_PORT_BASE overrides (default 0)")
    parser.add_argument("--split-step", default=os.environ.get("CLUSTER_SPLIT_STEP", "to-split"),
                        help="MagnetarWorld step that splits the template (to-split, or a fixture variant"
                             " such as to-handover); CLUSTER_SPLIT_STEP overrides")
    parser.add_argument("--node-env", action="append", default=[], metavar="[NODE:]KEY=VALUE",
                        help="extra environment for every node process, or for one node with a node-id"
                             " prefix (node-1:CLUSTER_CONCEALMENT=on); repeatable")
    parser.add_argument("--gateway-arg", action="append", default=[], metavar="ARG",
                        help="extra Gateway argument appended to the CLI's own; repeatable"
                             " (CLUSTER_GATEWAY_ARGS, shell-split, is the environment form)")
    commands = parser.add_subparsers(dest="command", required=True)
    freshener = commands.add_parser("fresh", help="stop everything, reset the world to the template, start")
    freshener.add_argument("--wipe", action="store_true",
                           help="required when the run root holds an existing cluster: ERASE it (never without approval)")
    starter = commands.add_parser("start", help="start the Gateway and the nodes; warm-restarts an existing run root (§1.7)")
    stop = commands.add_parser("stop", help="graceful shutdown through the admin API")
    stop.add_argument("--grace", type=float, default=30)
    prewarm = commands.add_parser("prewarm", help="pre-warm the node side on a stopped cluster: the warm-up"
                                  " node alone with updates allowed, its hub snapshot propagated to"
                                  " every node, then stopped (T-0152)")
    prewarm.add_argument("--refresh", action="store_true", default=os.environ.get("CLUSTER_PREWARM_REFRESH") == "1",
                         help="reset the warm-up node's LastCheck so the hub is re-read (CLUSTER_PREWARM_REFRESH=1)")
    commands.add_parser("versions", help="the node-side version set (Magnetar, hub pins, cached artefacts) as JSON")
    commands.add_parser("status", help="registry + transport + node summary; exit code reflects health")
    hostd = commands.add_parser("hostd", help="cluster-hostd, the per-host launcher of a multi-host start (T-0321)")
    hostd_verbs = hostd.add_subparsers(dest="hostd_verb", required=True)
    for verb, text in (("run", "the agent in the foreground"), ("start", "the agent detached (setsid, <root>/hostd.log)")):
        runner = hostd_verbs.add_parser(verb, help=text)
        runner.add_argument("--host", default=local_host(), help="this host's key in the hosts file")
        runner.add_argument("--fresh", action="store_true", help="a start: the previous run's node ids leave the ledger")
    hostd_verbs.add_parser("stop", help="SIGTERM this run root's agent (nodes keep running)")
    hostd_verbs.add_parser("status", help="the agent's pid, liveness and last poll (JSON)")
    hostd_verbs.add_parser("kill-nodes", help="SIGKILL every node process of this run root on this host")
    local_stopper = hostd_verbs.add_parser("local-stop", help="ask every node of this run root on this host for a local"
                                           " stop: Final saves to its own store, then exit (T-0347; no registry needed)")
    local_stopper.add_argument("--budget", type=float, default=60, help="seconds a node has for its Final saves")
    sweeper = hostd_verbs.add_parser("sweep", help="T-0340 step 7: move the legacy save files of stopped node dirs into this"
                                     " host's store (re-keyed by the registry first), delete stopped dirs; dry run unless --apply")
    sweeper.add_argument("--host", default=local_host(), help="this host's key in the hosts file (the store is saves/<host>)")
    mode = sweeper.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", help="report what would move and be deleted, touch nothing (the default)")
    mode.add_argument("--apply", action="store_true", help="link, re-key, unlink and delete")
    sweeper.add_argument("--registry-db", help="dry run on a COPIED run root: answer from this registry.db copy, no registry running")
    sweeper.add_argument("--keep-dirs", type=int, default=int(os.environ.get("CLUSTER_STORE_KEEP_DIRS", "2")),
                         help="keep the newest N stopped dirs (CLUSTER_STORE_KEEP_DIRS, default 2)")
    sweeper.add_argument("--keep-hours", type=float, default=float(os.environ.get("CLUSTER_STORE_KEEP_HOURS", "24")),
                         help="keep stopped dirs younger than this (CLUSTER_STORE_KEEP_HOURS, default 24)")
    sweeper.add_argument("--archive-retention-days", type=float,
                         default=float(os.environ.get("CLUSTER_STORE_ARCHIVE_RETENTION_DAYS", "0")),
                         help="delete files of partitions archived at least N days ago, rows kept (default 0 = off)")
    sweeper.add_argument("--batch", type=int, default=500, help="files per registry call (at most 2000)")
    sweeper.add_argument("--delete-unnamed", action="store_true",
                         help="also delete stopped dirs whose only save files are unnamed (no catalogue row names them),"
                              " once nothing was written there for --unnamed-days; needs --backup-verified with --apply")
    sweeper.add_argument("--unnamed-days", type=float, default=float(os.environ.get("CLUSTER_STORE_UNNAMED_DAYS", "7")),
                         help="days since a stopped dir's last write before its unnamed files may go (CLUSTER_STORE_UNNAMED_DAYS, default 7)")
    sweeper.add_argument("--backup-verified", metavar="PATH",
                         help="a committed world-backup part of this run root on this host (<dest>/<name> with BACKUP.json),"
                              " made after the newest file to delete and holding every one of them; required to delete save files")
    disker = hostd_verbs.add_parser("disk", help="this host's store bytes, legacy save bytes left in node dirs, free disk (JSON)")
    disker.add_argument("--host", default=local_host(), help="this host's key in the hosts file")
    commands.add_parser("hosts", help="multi-host: every host's agent and node processes (JSON)")
    logger = commands.add_parser("logs", help="a node's log, read in place on its host (ssh for another host)")
    logger.add_argument("selector", help="node-9, 12345@polaris, polaris#2, wa-1 or gateway")
    logger.add_argument("-n", "--lines", type=int, default=100)
    logger.add_argument("-f", "--follow", action="store_true")
    logger.add_argument("--game", action="store_true", help="the game log (<node>-data/SpaceEngineersDedicated_*.log)")
    urler = commands.add_parser("node-url", help="a node's control URL as the registry lists it, and the header it needs")
    urler.add_argument("selector", help="node-9, 12345@polaris, polaris#2 or wa-1")
    killer = commands.add_parser("kill", help="SIGKILL a node or everything")
    killer.add_argument("slot", nargs="?", help="node id, e.g. node-1; omit for everything")
    diag = commands.add_parser("diag", help="the §11.1 runtime-state snapshot")
    diag.add_argument("--depth", default="partitions", choices=["cluster", "nodes", "partitions", "entities"])
    diag.add_argument("--node")
    diag.add_argument("--partition")
    diag.add_argument("--player")
    diag.add_argument("--entity")
    diag.add_argument("--limit")
    diag.add_argument("--cursor")
    diag.add_argument("--registry", choices=["full", "summary"], default="full",
                      help="summary: live partition rows plus counts, EntityId issue log summarised (for pollers)")
    config = commands.add_parser("config", help="runtime handover config (HandoverImprovements.md §2)")
    config.add_argument("action", choices=["get", "set"])
    config.add_argument("pairs", nargs="*", help="key=value for set; key= (empty) restores the node default")
    trigger = commands.add_parser("trigger", help="run a scheduled maintenance task now (§11.2)")
    trigger.add_argument("task")
    trigger.add_argument("--wait", action="store_true")
    trigger.add_argument("--node")
    trigger.add_argument("--partition")
    trigger.add_argument("--unthrottled", action="store_true")
    nodes_parser = commands.add_parser("nodes", help="node control (T-0320): per-host serving/spare counts,"
                                       " remove/recycle a node, history, prune")
    verbs = nodes_parser.add_subparsers(dest="verb", required=True)
    lister = verbs.add_parser("list", help="every live node: ID @host pid age STATE partitions clients")
    lister.add_argument("--json", action="store_true")
    setter = verbs.add_parser("set", help="change a host's runtime serving/spare targets (until the next start)")
    setter.add_argument("host")
    setter.add_argument("--serving", type=int)
    setter.add_argument("--spares", type=int)
    for verb, text in (("remove", "drain and stop a node; its host's target drops by one"),
                       ("recycle", "drain and stop a node; a spare (or a new node) replaces it, count unchanged")):
        stopper = verbs.add_parser(verb, help=text)
        stopper.add_argument("selector", help="node-9, 12345@polaris (PID@host) or polaris#2 (host#position)")
        stopper.add_argument("--now", action="store_true", help="force at once: offline the partitions, stop the process")
        stopper.add_argument("--drain-timeout", type=float, default=None,
                             help="seconds of graceful drain before it is forced (registry default 300)")
    for verb in (setter, *[verbs.choices[name] for name in ("remove", "recycle")]):
        verb.add_argument("--reason", help="why, recorded in the history")
        verb.add_argument("--wait", action="store_true", help="wait until the change has settled")
        verb.add_argument("--timeout", type=float, default=900, help="seconds --wait waits (default 900)")
    historian = verbs.add_parser("history", help="nodes that are gone: who stopped them, when, why, age")
    historian.add_argument("--limit", type=int, default=50)
    historian.add_argument("--host")
    historian.add_argument("--json", action="store_true")
    rotation = verbs.add_parser("publish-rotation", help="publish the Manager's assigned rotation times to the Registry")
    rotation.add_argument("--lifetimes-file", required=True, help="existing Cluster Manager lifetime publication")
    rotation.add_argument("--valid-for", type=int, default=120, help="publication lease in seconds, 1..300; renew before expiry")
    pruner = verbs.add_parser("prune", help="delete data dirs of nodes in history that no catalogued save references")
    pruner.add_argument("--apply", action="store_true", help="delete (default: dry run)")
    scaler = commands.add_parser("scale", help="DEPRECATED (use `nodes set`): online node scaling (T-0312);"
                                 " no --to = status")
    scaler.add_argument("--to", type=int, help="desired regular (partition-hosting, non-spare) node count")
    scaler.add_argument("--force", action="store_true", help="scale in past the capacity check")
    scaler.add_argument("--no-launch", action="store_true",
                        help="only set the target; another launcher (the harness rotator) starts and stops nodes")
    scaler.add_argument("--timeout", type=float, default=3600, help="seconds to wait for the step to settle")
    scaler.add_argument("--retire", default="", help="comma-separated nodes to remove first on a scale-in"
                        " (default: the least loaded)")
    args = parser.parse_args(argv)
    args.admin = args.admin or [entry for entry in
                                os.environ.get("CLUSTER_ADMINS", "").replace(",", " ").split() if entry]

    if args.command == "hostd":
        import cluster_hostd
        return cluster_hostd.main(args)
    cluster = Cluster(build_config(args, from_root=args.command in ("prewarm", "versions")))
    if args.command == "fresh":
        cluster.require_wipe_for_existing(args.wipe)   # before kill(): a refused fresh touches nothing
        cluster.kill()
        cluster.prepare(wipe=args.wipe)
        cluster.start()
        cluster.wait_ready()
        print(json.dumps(cluster.status()["registry"], indent=2))
    elif args.command == "start":
        cluster.start()
        cluster.wait_ready()
    elif args.command == "stop":
        # Exit 1 on a stop that had to SIGKILL survivors, so a deploy script cannot mistake
        # it for a clean one (T-0048).
        return 0 if cluster.stop(args.grace) else 1
    elif args.command == "kill":
        print(json.dumps({"killed": cluster.kill(args.slot)}))
    elif args.command == "prewarm":
        cluster.prewarm(refresh=args.refresh)
    elif args.command == "versions":
        print(json.dumps(cluster.versions(), indent=2))
    elif args.command == "status":
        status = cluster.status()
        problems = cluster.health_problems(status)
        status["problems"] = problems
        print(json.dumps(status, indent=2, default=str))
        return 0 if not problems else 1
    elif args.command == "diag":
        print(json.dumps(cluster.diag(args.depth, node=args.node, partition=args.partition,
                                      player=args.player, entity=args.entity,
                                      limit=args.limit, cursor=args.cursor,
                                      registry=None if args.registry == "full" else args.registry),
                         indent=2, default=str))
    elif args.command == "config":
        if args.action == "set":
            if not args.pairs or any("=" not in pair for pair in args.pairs):
                raise ClusterError("config set needs key=value pairs")
            updates = {key: (value or None) for key, value in (pair.split("=", 1) for pair in args.pairs)}
            print(json.dumps(cluster.handover_config(updates), indent=2, default=str))
        else:
            print(json.dumps(cluster.handover_config(), indent=2, default=str))
    elif args.command == "scale":
        print(json.dumps(cluster.scale(args.to, force=args.force, launch=not args.no_launch, timeout=args.timeout,
                                       retire=[n for n in args.retire.split(",") if n]),
                         indent=2, default=str))
    elif args.command == "nodes":
        return nodes_command(cluster, args)
    elif args.command == "hosts":
        if not cluster.config.multi_host:
            raise ClusterError("`cluster hosts` needs a multi-host (version 2) hosts file")
        print(json.dumps(cluster.hosts_status(), indent=2, default=str))
    elif args.command == "logs":
        return cluster.logs(args.selector, lines=args.lines, follow=args.follow, game=args.game)
    elif args.command == "node-url":
        print(cluster.node_url(args.selector))
    elif args.command == "trigger":
        scope = {"node": args.node, "partition": args.partition}
        if args.unthrottled:
            scope["unthrottled"] = "true"
        print(json.dumps(cluster.trigger(args.task, args.wait, **scope), indent=2, default=str))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ClusterError as failure:
        print(f"cluster: {failure}", file=sys.stderr)
        sys.exit(2)
