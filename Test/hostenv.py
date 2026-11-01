"""The host contract: everything host-specific the suite reads, in one place.

Values come from the environment, then from ``Test/host.env`` (gitignored; template
``host.env.example``), then from the defaults below, which match a host laid out like the
reference bench (Steam library under ``~/.steam``, Magnetar under ``~/.config/Magnetar``, Pulsar
under ``~/.config/Pulsar``). Importing this module:

* loads ``host.env`` into ``os.environ`` without overriding what is already set;
* exports what the package's ``cli/cluster`` wrapper would export, so ``import cluster`` from the
  package behaves exactly like the wrapper;
* puts the package's ``cli/`` and the se-remote client on ``sys.path``.

Nothing else in ``Test/`` may name a path outside the repository, a host, a user or a Steam id.
"""

from __future__ import annotations

import os
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

TEST_DIR = Path(__file__).resolve().parent
REPO = TEST_DIR.parent
HOME = Path.home()
HOST_ENV = Path(os.environ.get("CLUSTER_TEST_HOST_ENV", TEST_DIR / "host.env"))


def _load_host_env(path: Path) -> dict[str, str]:
    """KEY=VALUE lines (an optional leading ``export``, ``#`` comments, optional quotes).
    ``~`` and ``$VAR`` / ``${VAR}`` are expanded against the environment as it is so far."""
    loaded: dict[str, str] = {}
    if not path.is_file():
        return loaded
    for number, raw in enumerate(path.read_text().splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        line = re.sub(r"^export\s+", "", line)
        key, eq, value = line.partition("=")
        key = key.strip()
        if not eq or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            raise ValueError(f"{path}:{number}: expected KEY=VALUE, got {raw!r}")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
            value = value[1:-1]
        value = os.path.expandvars(os.path.expanduser(value)) if value else ""
        loaded[key] = value
        if key not in os.environ:
            os.environ[key] = value
    return loaded


HOST_ENV_VALUES = _load_host_env(HOST_ENV)


def _env(name: str, default: str = "") -> str:
    return os.environ.get(name, "") or default


def _path(name: str, default: Path | str) -> Path:
    return Path(_env(name, str(default))).expanduser()


def _newest_package() -> Path | None:
    """The newest unpacked release package under the repository's build folders."""
    candidates = []
    for pattern in ("Build/dist/cluster-*", "Build/out/release-*/cluster-*", "Build/out/release-*"):
        for path in REPO.glob(pattern):
            if (path / "registry/ClusterRegistry.dll").is_file() and (path / "cli/cluster.py").is_file():
                candidates.append(path)
    return max(candidates, key=lambda path: path.stat().st_mtime) if candidates else None


def _first_existing(*paths: Path) -> Path:
    for path in paths:
        if path.exists():
            return path
    return paths[0]


@dataclass
class Host:
    """The resolved host contract. See host.env.example for what each field means."""

    package: Path | None
    server: Path
    client: Path
    magnetar: Path
    pulsar: Path
    se_remote: Path
    test_root: Path
    results: Path
    port_base: int
    remote_port_base: int
    network_window: str
    render: bool
    min_free_gb: float
    planet_world_archive: Path
    test_plan: Path
    steam_client_host: str
    steam_client_user: str
    steam_client_id: str
    direct_transport_source: Path | None
    extra: dict[str, str] = field(default_factory=dict)

    @property
    def ds64(self) -> Path:
        return self.server / "DedicatedServer64"

    @property
    def client_bin64(self) -> Path:
        return self.client / "Bin64"

    @property
    def pulsar_launcher(self) -> Path:
        return self.pulsar / "Interim.bin"

    @property
    def pulsar_profile(self) -> Path:
        return self.pulsar / "Interim"

    def as_dict(self) -> dict:
        return {key: (str(value) if isinstance(value, Path) else value) for key, value in vars(self).items()}


def resolve(package: Path | str | None = None) -> Host:
    package_path = Path(package).expanduser().resolve() if package else None
    if package_path is None and _env("CLUSTER_PACKAGE"):
        package_path = Path(_env("CLUSTER_PACKAGE")).expanduser().resolve()
    if package_path is None:
        package_path = _newest_package()
    steam = HOME / ".steam/steam/steamapps/common"
    # T-0157 requirement 1 (2026-09-19): nodes and clients take Direct Transport from the hub pin,
    # which caught up on 2026-09-18 at 144a6e3 in BOTH hubs (CometWorks/magnetar-hub serves the
    # ServerPlugin half, StarCpt/PluginHub the ClientPlugin half). DIRECT_TRANSPORT_SOURCE=<checkout>
    # is the dev override for testing a transport change before it is published; 'hub' spells the
    # default. The 2026-09-17 arrangement that defaulted to ~/ws/se/se1/direct-transport whenever the folder
    # merely existed is gone: it made the transport build depend on what happened to be on disk.
    transport = _env("DIRECT_TRANSPORT_SOURCE") or "hub"
    if transport.strip().lower() == "hub":
        transport = None
    return Host(
        package=package_path,
        server=_path("SPACE_ENGINEERS_SERVER", steam / "SpaceEngineersDedicatedServer"),
        client=_path("SPACE_ENGINEERS_ROOT", steam / "SpaceEngineers"),
        magnetar=_path("MAGNETAR_HOME", HOME / ".config/Magnetar"
                       if not (package_path and (package_path / "magnetar/MagnetarInterim.bin").exists())
                       else package_path / "magnetar"),
        pulsar=_path("PULSAR_HOME", HOME / ".config/Pulsar"),
        se_remote=_path("SE_REMOTE_SKILL", _first_existing(HOME / ".claude/skills/se-remote",
                                                           HOME / "ws/se/se1/plugins/remote/skills/se-remote")),
        test_root=_path("CLUSTER_TEST_ROOT", HOME / "cluster-runs/bench"),
        results=_path("CLUSTER_TEST_RESULTS", HOME / "cluster-test-results/bench"),
        port_base=int(_env("CLUSTER_TEST_PORT_BASE", "100")),
        remote_port_base=int(_env("CLUSTER_TEST_REMOTE_PORT_BASE", "24200")),
        network_window=_env("CLUSTER_TEST_NETWORK_WINDOW"),
        render=_env("CLUSTER_TEST_RENDER", "1") not in ("0", "false", "no"),
        min_free_gb=float(_env("CLUSTER_TEST_MIN_FREE_GB", "10")),
        planet_world_archive=_path("PLANET_WORLD_ARCHIVE", HOME / "ws/se/se1/plugins/remote/Worlds/RemoteAPITestEarthPlanet.zip"),
        test_plan=_path("TEST_PLAN", HOME / "ws/se/se1/clustering-plan/Plan/TestPlan.md"),
        steam_client_host=_env("STEAM_CLIENT_HOST"),
        steam_client_user=_env("STEAM_CLIENT_USER"),
        steam_client_id=_env("STEAM_CLIENT_ID"),
        direct_transport_source=Path(transport).expanduser() if transport else None,
        extra=dict(HOST_ENV_VALUES),
    )


def activate(host: Host) -> None:
    """Export what ``cli/cluster`` exports and make ``import cluster`` / ``import se_remote`` work.

    Must run before the first ``import cluster``: the CLI reads its environment at import time
    (its package root, the plugin binaries, the port base)."""
    if host.package is None:
        raise SystemExit("no release package: set CLUSTER_PACKAGE (host.env) or pass --package; "
                         "Build/release.sh builds one into Build/dist")
    package = host.package
    os.environ["SPACE_ENGINEERS_SERVER"] = str(host.server)
    os.environ.setdefault("SPACE_ENGINEERS_BIN64", str(host.ds64))
    os.environ["MAGNETAR_HOME"] = str(host.magnetar)
    _isolate_magnetar_cache(host)
    os.environ.setdefault("CLUSTER_PLUGIN_BINARIES", str(package / "plugins"))
    os.environ.setdefault("MAGNETAR_GATEWAY_DLL", str(package / "registry/ClusterRegistry.dll"))
    os.environ.setdefault("MAGNETAR_WORLD_TOOL", str(package / "tools/MagnetarWorld/MagnetarWorld.dll"))
    os.environ.setdefault("CLUSTER_WORLD_TEMPLATE", str(package / "examples/Cluster-Simple"))
    os.environ["CLUSTER_PORT_BASE"] = str(host.port_base)
    # The Steam frontend stays off on the bench; a scenario that needs it asks for it.
    os.environ.setdefault("CLUSTER_STEAM_LISTEN", "")
    os.environ["DIRECT_TRANSPORT_SOURCE"] = (str(host.direct_transport_source)
                                             if host.direct_transport_source is not None else "hub")
    # Inserted in reverse so the package's cli/ comes first: its cluster.py is the one under test.
    for path in (TEST_DIR, host.se_remote, package / "cli"):
        if str(path) in sys.path:
            sys.path.remove(str(path))
        sys.path.insert(0, str(path))


def _isolate_magnetar_cache(host: Host) -> None:
    """Give the bench its own Magnetar GitHub cache, seeded from the host's.

    The cache is SHARED by design - every instance's ``<node>-magnetar/GitHub`` is a symlink to one
    directory, so 64 instances need one download instead of 64 (Viktor, 2026-09-04, after GitHub
    rate-limited the public IP). What is content-addressed is the DOWNLOAD; the ``Bin`` beside it is
    a COMPILE output, and two instances whose hub snapshots pin different commits compile different
    code into the same file. Measured 2026-09-19: one bench scenario rewrote the shared Direct
    Transport build the LIVE cluster's nodes load, m_engineEvents 5 references -> 0 against a
    control field 11 -> 9, leaving a build the cluster plugin refuses at startup. Nothing notices
    until a live node next starts - a rotation, a recycle, a drain relaunch (T-0192).

    So the bench keeps the sharing and stops the sharing at its own boundary: one cache for all its
    instances, none of it reachable from a live cluster. Seeded by COPY rather than by download, so
    isolation costs no GitHub fetch and no network on a first run.
    """
    if os.environ.get("MAGNETAR_GITHUB_CACHE"):
        return
    source = HOME / ".cache/Magnetar/Legacy/GitHub"
    # BESIDE the test root, not inside it: a scenario clears its own run directory, and a cache
    # that is re-seeded on every run is a 103 MB copy and a full recompile per run.
    cache = host.test_root.parent / "bench-plugin-cache" / "magnetar-github"
    cache.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        for entry in sorted(source.iterdir()):
            target = cache / entry.name
            if not target.exists():
                shutil.copytree(entry, target, symlinks=True)
    os.environ["MAGNETAR_GITHUB_CACHE"] = str(cache)


HOST = resolve()
