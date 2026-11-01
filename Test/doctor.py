#!/usr/bin/env python3
"""Check that this host can run the suite, and say what is missing.

    uv run python doctor.py [--package DIR] [--json]

Exit 0 when every hard requirement holds (warnings are printed but do not fail), 1 otherwise.
``run_suite.py`` runs the same checks before the first scenario and records the report as
``host.json``; ``versions()`` is its ``versions.json``.

Checks, in order: the release package; the game installs and that client and server run the same
game build; Magnetar and Pulsar (with the ``remote`` and ``direct-transport`` client plugins);
the pre-warmed plugin caches; free ports and a quiet run root; RAM, disk and GPU; the fixtures;
and the capabilities the host lacks, which ``coverage.py`` turns into per-host deferrals.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

if __name__ == "__main__":
    for _index, _value in enumerate(sys.argv[1:]):
        if _value == "--package" and _index + 2 < len(sys.argv):
            os.environ["CLUSTER_PACKAGE"] = str(Path(sys.argv[_index + 2]).expanduser().resolve())

import hostenv  # noqa: E402

HOST = hostenv.HOST
PACKAGE_FILES = ("registry/ClusterRegistry.dll", "plugins/ClusterNode/ClusterNode.dll",
                 "plugins/ClusterNode/ClusterNode.xml", "plugins/WorldAuthority/WorldAuthority.dll",
                 "plugins/WorldAuthority/WorldAuthority.xml", "tools/MagnetarWorld/MagnetarWorld.dll",
                 "cli/cluster", "cli/cluster.py", "cli/magnetar_config.py",
                 "examples/Cluster-Simple/Sandbox.sbc", "manifest.json")
#: Plugin artefacts both the node side and the client side load from the shared download caches.
SHARED_PLUGINS = ("DotNetCompat-", "LinuxCompat-")
APP_VERSION = re.compile(r"App Version: (\d+_\d+_\d+)")


def _last_app_version(logs: list[Path]) -> str | None:
    for log in sorted((path for path in logs if path.is_file()), key=lambda path: path.stat().st_mtime, reverse=True):
        match = APP_VERSION.search(log.read_text(errors="replace")[:200_000])
        if match:
            return match.group(1)
    return None


def game_versions() -> dict[str, str | None]:
    """The game build each install reported the last time it ran here (the game prints
    ``App Version`` at startup). Client and server assemblies differ byte-wise between the two
    Steam apps, so a log line is the only offline evidence of the build."""
    root = HOST.test_root
    server_logs = list(root.glob("*/node-*.log")) + list(root.glob("*/wa-*.log"))
    client_logs = list(root.glob("*/client-*/home/.config/SpaceEngineers/SpaceEngineers_*.log")) \
        + list((Path.home() / ".config/SpaceEngineers").glob("SpaceEngineers_*.log"))
    return {"server": _last_app_version(server_logs), "client": _last_app_version(client_logs)}


def port_free(port: int) -> bool:
    with socket.socket() as probe:
        probe.settimeout(0.3)
        return probe.connect_ex(("127.0.0.1", port)) != 0


def bench_ports() -> list[int]:
    base = HOST.port_base
    return [27116 + base, 29416 + base] + [28416 + base + i for i in range(1, 6)] \
        + [29416 + base + i for i in range(1, 6)] + [HOST.remote_port_base + i for i in range(4)]


def test_root_processes() -> list[int]:
    """Processes whose command line or environment names the test root: what the suite started."""
    marker = str(HOST.test_root).encode() + b"/"
    found = []
    for proc in Path("/proc").iterdir():
        if not proc.name.isdigit() or int(proc.name) == os.getpid():
            continue
        try:
            cmdline = (proc / "cmdline").read_bytes()
            environ = (proc / "environ").read_bytes() if marker not in cmdline else b""
        except OSError:
            continue
        if marker in cmdline or b"\0HOME=" + marker in b"\0" + environ:
            found.append(int(proc.name))
    return found


def sweep_test_root() -> list[int]:
    """Kill what a scenario left running under the test root - and nothing else on the host."""
    pids = test_root_processes()
    for pid in pids:
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            pass
    return pids


def gpu_present() -> bool:
    return any(Path("/dev/dri").glob("renderD*")) if Path("/dev/dri").is_dir() else False


def vulkan_devices() -> list[str] | None:
    """The driverName of every Vulkan device the loader will offer, or None when vulkaninfo is not
    installed and the question cannot be answered.

    A RENDER NODE IS NOT A VULKAN DEVICE, and the difference is not academic: on sirius on
    2026-09-20 /dev/dri held three renderD* nodes while the loader offered only llvmpipe, because
    the NVIDIA ICD was not enumerating. dxvk then selected the software rasteriser, and the client
    died inside libvulkan_lvp.so on a call through a null function pointer (T-0199). gpu_present()
    was true throughout - a check that passes while the thing it stands for is false."""
    try:
        result = subprocess.run(["vulkaninfo", "--summary"], capture_output=True, text=True, timeout=30)
    except (FileNotFoundError, subprocess.SubprocessError):
        return None
    if result.returncode != 0:
        return None
    return [line.split("=", 1)[1].strip() for line in result.stdout.splitlines()
            if line.strip().startswith("driverName")]


def software_only_vulkan(drivers: list[str] | None) -> bool:
    """True only when the loader definitely offers nothing but software rasterisers. Unknown
    (vulkaninfo missing) is not "software": it must not fail a host it cannot measure."""
    if not drivers:
        return False
    software = ("llvmpipe", "lavapipe", "swrast", "softpipe")
    return all(any(name in driver.lower() for name in software) for driver in drivers)


def check() -> dict:
    failures: list[str] = []
    warnings: list[str] = []
    info: dict = {"host": os.uname().nodename, "contract": HOST.as_dict()}

    def need(condition: bool, message: str) -> bool:
        if not condition:
            failures.append(message)
        return condition

    # 1. package
    package = HOST.package
    if need(package is not None, "no release package: set CLUSTER_PACKAGE or pass --package "
                                 "(Build/release.sh builds one into Build/dist)"):
        missing = [name for name in PACKAGE_FILES if not (package / name).exists()]
        need(not missing, f"package {package} lacks {', '.join(missing)}")
        if (package / "manifest.json").is_file():
            info["package"] = json.loads((package / "manifest.json").read_text())
    # 2. game installs
    need((HOST.ds64 / "SpaceEngineersDedicated.exe").is_file(),
         f"no dedicated server at {HOST.server} (SPACE_ENGINEERS_SERVER)")
    need((HOST.client_bin64 / "SpaceEngineers.exe").is_file(),
         f"no game client at {HOST.client} (SPACE_ENGINEERS_ROOT)")
    versions = game_versions()
    info["gameVersions"] = versions
    if versions["server"] and versions["client"] and versions["server"] != versions["client"]:
        failures.append(f"client build {versions['client']} != server build {versions['server']}: the Gateway "
                        "admits such a client and the node drops it with 'version mismatch'")
    elif not all(versions.values()):
        warnings.append(f"game build not yet known for {', '.join(k for k, v in versions.items() if not v)} "
                        "(read from its first log); the first scenario checks it")
    # 3. launchers
    need((HOST.magnetar / "MagnetarInterim.bin").is_file(), f"no Magnetar launcher at {HOST.magnetar} (MAGNETAR_HOME)")
    need(HOST.pulsar_launcher.is_file(), f"no Pulsar launcher at {HOST.pulsar_launcher} (PULSAR_HOME)")
    need((HOST.pulsar / "Libraries/Compiler/Compiler.bin").is_file(),
         f"Pulsar at {HOST.pulsar} has no Libraries/Compiler/Compiler.bin; every plugin would fail to load")
    profile = HOST.pulsar_profile / "Profiles/Current.xml"
    if need(profile.is_file(), f"no Pulsar profile {profile}"):
        text = profile.read_text()
        for plugin in ("remote", "direct-transport"):
            need(f"<Id>{plugin}</Id>" in text or f"<string>{plugin}</string>" in text,
                 f"the Pulsar profile {profile} does not enable the {plugin} client plugin")
    need((HOST.se_remote / "se_remote.py").is_file(), f"no se_remote.py under {HOST.se_remote} (SE_REMOTE_SKILL)")
    try:
        import httpx  # noqa: F401
    except ImportError:
        failures.append("python package httpx is missing (uv sync in Test/, or pip install httpx)")
    for tool in ("dotnet", "git", "tar", "ps"):
        need(shutil.which(tool) is not None, f"required tool not found: {tool}")
    # T-0157 requirement 1: the hub pin is the release arrangement, so it is NOT what to warn about -
    # a dev folder is. The warning used to point the other way, when the hub still pinned 2212094 and
    # the dev folder was the only build that worked; it caught up on 2026-09-18 at 144a6e3.
    if HOST.direct_transport_source is not None:
        warnings.append(f"DIRECT_TRANSPORT_SOURCE={HOST.direct_transport_source}: this is the dev override, not the "
                        "release arrangement - nodes compile that checkout instead of the hub pin, so the transport "
                        "build depends on what is on disk here and can drift from the clients' copy. Unset it to "
                        "take the hub pin (T-0157).")
    # 4. caches
    caches = {"node": Path(os.environ.get("MAGNETAR_GITHUB_CACHE", Path.home() / ".cache/Magnetar/Legacy/GitHub")),
              "client": Path(os.environ.get("PULSAR_GITHUB_CACHE", Path.home() / ".cache/Pulsar-bench/Legacy/GitHub"))}
    cached = {side: sorted(entry.name for entry in path.iterdir()) if path.is_dir() else [] for side, path in caches.items()}
    info["pluginCaches"] = cached
    for prefix in SHARED_PLUGINS:
        node = {name for name in cached["node"] if name.startswith(prefix)}
        client = {name for name in cached["client"] if name.startswith(prefix)}
        if not node or not client:
            warnings.append(f"{prefix.rstrip('-')} is not pre-warmed for the "
                            f"{'node' if not node else 'client'} side; the first start fetches it from GitHub "
                            "(run `cli/cluster prewarm` and start one client with network access)")
        elif not node & client:
            failures.append(f"{prefix.rstrip('-')} differs between nodes {sorted(node)} and clients {sorted(client)}: "
                            "one version set cluster-wide")
    # THE HUB PIN, which the cache folder name cannot show. A GitHub entry is named
    # "<plugin>-<hash of the source>", not of the commit, so two hub snapshots seven commits apart
    # occupy the SAME folder and look identical in the listing above. MaxSourceAge is pinned to a
    # year, so a snapshot only moves on an explicit `prewarm --refresh` - which means a bench
    # directory can sit on a commit the package does not name for weeks without a symptom.
    # It cost a full red suite on 2026-09-19 (T-0172): 235 scenario directories pinned 2212094,
    # seven commits behind the 144a6e3 the package's manifest names, and 2212094 has no
    # Shared.Transport.UdpPeer2Peer.m_engineEvents, which NodeLinkTransportBinding.cs:175 requires
    # of every role at startup. Every scenario that loaded the transport from the hub died at boot.
    expected = (json.loads((HOST.package / "manifest.json").read_text()).get("direct-transport")
                if HOST.package and (HOST.package / "manifest.json").is_file() else None)
    if expected and re.fullmatch(r"[0-9a-f]{40}", expected):
        import cluster  # the package's own cli/cluster.py, already on sys.path via hostenv
        stale = {}
        for snapshot in sorted(HOST.test_root.glob("*/*-magnetar/Sources/Hubs/*.bin")):
            pin = cluster.hub_pins(snapshot).get("CometWorks/direct-transport")
            if pin and pin != expected:
                stale[f"{snapshot.parents[3].name}/{snapshot.parents[2].name}"] = pin[:7]
        info["hubPinMismatches"] = len(stale)
        need(not stale, f"{len(stale)} bench directories pin a Direct Transport commit the package does "
                        f"not name (expected {expected[:7]}, found e.g. "
                        f"{dict(sorted(stale.items())[:3])}); run `cli/cluster prewarm --refresh` on one "
                        "instance and propagate its Sources/Hubs snapshot to the rest")
    info["githubToken"] = "present" if os.environ.get("PULSAR_GITHUB_TOKEN") else "absent"
    # 5. ports and run root
    busy = [port for port in bench_ports() if not port_free(port)]
    need(not busy, f"bench ports in use: {busy} (CLUSTER_TEST_PORT_BASE={HOST.port_base}, "
                   f"CLUSTER_TEST_REMOTE_PORT_BASE={HOST.remote_port_base})")
    leftovers = test_root_processes()
    need(not leftovers, f"processes still run under {HOST.test_root}: {leftovers}")
    need(not str(HOST.test_root).startswith("/tmp"), "CLUSTER_TEST_ROOT must not be under /tmp (tmpfs, crash dumps)")
    # 6. resources
    mem = {line.split(":")[0]: int(line.split()[1]) for line in Path("/proc/meminfo").read_text().splitlines()}
    available = mem.get("MemAvailable", 0) / 1024 / 1024
    info["availableGb"] = round(available, 1)
    need(available >= HOST.min_free_gb + 12, f"{available:.1f} GB RAM available; a two-node scenario with one "
                                             f"client needs ~12 GB above the {HOST.min_free_gb} GB floor")
    for label, path in (("test root", HOST.test_root), ("results", HOST.results)):
        path.mkdir(parents=True, exist_ok=True)
        stat = os.statvfs(path)
        free = stat.f_bavail * stat.f_frsize / 1024 ** 3
        info[f"{label.replace(' ', '')}FreeGb"] = round(free, 1)
        if free < 100:
            warnings.append(f"{label} {path}: only {free:.0f} GB free")
    capabilities = {"render": HOST.render and gpu_present(), "steamHost": bool(HOST.steam_client_host),
                    "root": os.geteuid() == 0, "planetWorld": HOST.planet_world_archive.is_file(),
                    # the suite drives one host; nothing here sets a second host's clock
                    "twoHosts": False}
    if HOST.render and not gpu_present():
        warnings.append("no /dev/dri render node: set CLUSTER_TEST_RENDER=0; screenshot scenarios are deferred")
    drivers = vulkan_devices()
    info["vulkanDrivers"] = drivers
    if software_only_vulkan(drivers):
        # A FAILURE, not a warning: a client that renders through lavapipe is not a slow client,
        # it is a client that crashes (T-0199, the null call inside libvulkan_lvp.so), and every
        # run it takes part in is a run whose failures have a second explanation.
        failures.append("the Vulkan loader offers only software drivers "
                        + ", ".join(drivers) + ": clients would render through lavapipe, which "
                        "crashes (T-0199). Fix the GPU driver or ICD on this host - a /dev/dri "
                        "render node is not enough, the loader has to ENUMERATE the device.")
    info["capabilities"] = capabilities
    # 7. fixtures
    need(HOST.test_plan.is_file(), f"no TestPlan.md at {HOST.test_plan} (TEST_PLAN)")
    if not HOST.planet_world_archive.is_file():
        warnings.append(f"no planet world at {HOST.planet_world_archive} (PLANET_WORLD_ARCHIVE): planet scenarios are deferred")
    if HOST.network_window:
        info["networkWindow"] = HOST.network_window
    return {**info, "failures": failures, "warnings": warnings}


def versions() -> dict:
    """What a run ran against: the package manifest, Magnetar, Pulsar and the plugin caches."""
    out: dict = {}
    if HOST.package and (HOST.package / "manifest.json").is_file():
        out["package"] = json.loads((HOST.package / "manifest.json").read_text())
    try:
        import cluster
        out["magnetar"] = cluster.informational_version(HOST.magnetar / "MagnetarInterim.dll")
        out["pulsar"] = cluster.informational_version(HOST.pulsar / "Libraries/Interim/Interim.dll") \
            if (HOST.pulsar / "Libraries/Interim/Interim.dll").is_file() else None
    except Exception as error:  # noqa: BLE001 - a version probe is evidence, never a gate
        out["versionProbeError"] = str(error)
    out["gameVersions"] = game_versions()
    for repo, label in ((HOST.direct_transport_source, "directTransportSource"), (HOST.se_remote, "seRemote")):
        if repo is not None and repo.exists():
            describe = subprocess.run(["git", "-C", str(repo), "describe", "--always", "--dirty"],
                                      capture_output=True, text=True)
            out[label] = {"path": str(repo), "describe": describe.stdout.strip() or None}
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--package", help="unpacked release package (default CLUSTER_PACKAGE)")
    parser.add_argument("--json", action="store_true", help="print the full report as JSON")
    args = parser.parse_args()
    hostenv.activate(HOST) if HOST.package else None
    report = check()
    if args.json:
        print(json.dumps({**report, "versions": versions() if HOST.package else None}, indent=2, default=str))
    else:
        print(f"host {report['host']}, package {HOST.package}")
        for key in ("gameVersions", "availableGb", "capabilities", "githubToken"):
            print(f"  {key}: {report.get(key)}")
        for warning in report["warnings"]:
            print(f"WARN  {warning}")
        for failure in report["failures"]:
            print(f"FAIL  {failure}")
        print("OK" if not report["failures"] else f"{len(report['failures'])} problem(s)")
    return 0 if not report["failures"] else 1


if __name__ == "__main__":
    sys.exit(main())
