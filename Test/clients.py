"""Game clients for the bench, run without a desktop session: upstream Pulsar with the Remote and Direct Transport plugins.

The launch recipe is the one the long-running regression harness proved on this fleet
(``cluster-regression/regression/clients.py``), reduced to what a single-host bench needs:

* Pulsar's own flags first - ``-bin64`` is required (without it Pulsar reads a Steam library file
  that may not exist and dies), ``-multiInstance`` lifts Pulsar's single-instance guard,
  ``-useHome`` puts Pulsar's data under the per-client ``HOME``, ``-noUpdate`` keeps the client off
  GitHub (the caches are pre-warmed);
* then the plugins' options: ``--headless --resolution 1280x720 --max-fps N --no-audio
  --client-id --client-name --connect``, plus ``--no-render`` (and ``--bare-bones``, which is gated
  on both flags) for a client that does not have to draw;
* a per-client ``HOME`` / ``XDG_CONFIG_HOME`` / ``XDG_CACHE_HOME`` / ``TMPDIR``, a copy of the
  host's Pulsar profile with its warm caches, the shared plugin download cache, the game config
  with ``ExperimentalMode=True`` (a mismatch with the server is admitted and then dropped), and a
  ``Remote.cfg`` on the client's own port.

``PULSAR_NO_RENDER`` goes into the process ENVIRONMENT: the Remote plugin exports it from inside
the process, which races the plugin that acts on it and was silently a no-op for weeks.

Never ``--no-gpu``: software rendering eats CPU and RAM and distorts every measurement. A client
exits with 143 on a normal shutdown.
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path

from hostenv import HOST, Host


# T-0403 (Viktor 2026-10-04, CH 2026-10-08): polaris has two identical AMD R9700 GPUs and Vulkan1 (PCI 0000:10:00.0)
# belongs to another user's AI models, so every SE client started ON POLARIS - --no-render included - is pinned to
# Vulkan0 (PCI 0000:0d:00.0); these two variables leave only 0d:00.0 enumerated (vulkaninfo-verified). Other hosts:
# nothing. The same values as cluster-regression regression/env.py POLARIS_GPU_PIN and bin/pulsar-prewarm.
POLARIS_GPU_PIN = {"DRI_PRIME": "pci-0000_0d_00_0", "MESA_VK_DEVICE_SELECT_FORCE_DEFAULT_DEVICE": "1"}


def local_gpu_pin() -> dict[str, str]:
    import socket
    return dict(POLARIS_GPU_PIN) if socket.gethostname().split(".")[0] == "polaris" else {}

REMOTE_USER = "admin"
REMOTE_PASSWORD = "SpaceEngineers"
#: The shared plugin download cache every client symlinks to (content-addressed, safe to share).
# The BENCH's own copy (2026-09-29): sharing the live cluster's ~/.cache/Pulsar/Legacy/GitHub let a bench
# client recompile Bugfixes/Performance into it mid-run, and the live start's version-consistency check then
# found polaris' compiled variant different from the client hosts' (INCONSISTENT, the live start refused;
# the CM re-aligned it twice). Seeded from the live cache on first use; the live one is never written.
LIVE_PULSAR_GITHUB_CACHE = Path.home() / ".cache/Pulsar/Legacy/GitHub"
PULSAR_GITHUB_CACHE = Path(os.environ.get("PULSAR_GITHUB_CACHE", Path.home() / ".cache/Pulsar-bench/Legacy/GitHub"))
NUGET_PACKAGES = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
MAX_SOURCE_AGE_HOURS = "8760"

REMOTE_CFG = """<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <DetectCodeChanges>true</DetectCodeChanges>
  <ListenIP>127.0.0.1</ListenIP>
  <ListenPort>{port}</ListenPort>
  <AdminPassword>{password}</AdminPassword>
  <EnableSwaggerUI>false</EnableSwaggerUI>
  <EnablePerformanceStats>true</EnablePerformanceStats>
  <GridGetRateLimit>500</GridGetRateLimit>
  <GridGetBurstSize>1000</GridGetBurstSize>
  <GridSetRateLimit>200</GridSetRateLimit>
  <GridSetBurstSize>400</GridSetBurstSize>
  <GridMaxGetOpsPerRequest>100</GridMaxGetOpsPerRequest>
  <GridMaxSetOpsPerRequest>500</GridMaxSetOpsPerRequest>
  <GridMaxBlocksPerPage>1000</GridMaxBlocksPerPage>
</PluginConfig>
"""

# A minimal game config for a host without a live ~/.config/SpaceEngineers/SpaceEngineers.cfg.
#
# THIS FALLBACK HAD NEVER RUN. Every host that has ever run the suite also had a live config,
# because a human had started the game there once, so the copy above it always won. Setting up msi
# and sirius on 2026-09-19 (T-0173) exercised it for the first time and it did not work:
#
#   * FirstTimeRun was unset, so the client played the intro cinematics instead of reaching the
#     respawn screen - `[LinuxCompat] VideoPlayer.Play w=1920 h=1080 dur=45.38s` twice - and every
#     scenario died on "client ... did not complete the vanilla respawn flow (last={})" while
#     /v1/character answered 503 throughout. The client connects first, so the symptom appears AFTER
#     a good `UDP liveness peer=... state=Connected` line and looks like a network fault.
#   * it was the WRONG SCHEMA. The game writes MyObjectBuilder_ConfigData > Data > dictionary >
#     item with a nested <Value><Value>; this emitted MyObjectBuilder_Configuration >
#     DictionaryEntries > DictionaryEntry. So ExperimentalMode was probably not being read either -
#     it had simply never mattered.
#
# Both keys, in the schema the game actually uses, verified red-then-green on two fresh hosts:
# P1-CTL failed identically on msi and sirius without this and passed 1/1 on both with it.
GAME_CFG = """<?xml version="1.0" encoding="utf-8"?>
<MyObjectBuilder_ConfigData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Data>
    <dictionary>
      <item>
        <Key>FirstTimeRun</Key>
        <Value>
          <Value xsi:type="xsd:string">False</Value>
        </Value>
      </item>
      <item>
        <Key>ExperimentalMode</Key>
        <Value>
          <Value xsi:type="xsd:string">True</Value>
        </Value>
      </item>
    </dictionary>
  </Data>
</MyObjectBuilder_ConfigData>
"""

CRASH_ENV = {
    "DOTNET_DbgEnableMiniDump": "1",
    "DOTNET_DbgMiniDumpType": "2",
    "DOTNET_EnableCrashReport": "1",
}


class ClientError(RuntimeError):
    pass


def seed_cache(cache: Path, source: Path) -> None:
    """Fill the bench cache with the entries of SOURCE it does not have yet (copies, never links)."""
    if cache.resolve() == source.resolve() or not source.is_dir():
        return
    cache.mkdir(parents=True, exist_ok=True)
    for entry in source.iterdir():
        if not (cache / entry.name).exists():
            if entry.is_dir():
                shutil.copytree(entry, cache / entry.name, symlinks=True)
            else:
                shutil.copy2(entry, cache / entry.name)


def share_cache(folder: Path, cache: Path) -> None:
    """Replace ``folder`` with a symlink to ``cache``, moving what it held into the cache first."""
    cache.mkdir(parents=True, exist_ok=True)
    if folder.is_symlink():
        if folder.resolve() == cache.resolve():
            return
        folder.unlink()
    elif folder.is_dir():
        for entry in folder.iterdir():
            if not (cache / entry.name).exists():
                shutil.move(str(entry), str(cache / entry.name))
        shutil.rmtree(folder, ignore_errors=True)
    folder.parent.mkdir(parents=True, exist_ok=True)
    folder.symlink_to(cache, target_is_directory=True)


def pin_source_age(profile: Path) -> None:
    """MaxSourceAge (hours) high enough that a pre-warmed source is never re-resolved mid-run."""
    path = profile / "Sources/sources.xml"
    if not path.is_file():
        return
    tree = ET.parse(path)
    element = tree.getroot().find("MaxSourceAge")
    if element is None:
        element = ET.SubElement(tree.getroot(), "MaxSourceAge")
    if element.text != MAX_SOURCE_AGE_HOURS:
        element.text = MAX_SOURCE_AGE_HOURS
        tree.write(path, encoding="utf-8", xml_declaration=True)


def set_experimental_mode(cfg: Path) -> None:
    """ExperimentalMode=True in a client SpaceEngineers.cfg, verified; the server runs with it on."""
    text = cfg.read_text()
    marker = "<Key>ExperimentalMode</Key>"
    start = text.find(marker)
    if start >= 0:
        value_start = text.find('<Value xsi:type="xsd:string">', start)
        value_end = text.find("</Value>", value_start)
        if value_start < 0 or value_end < 0:
            raise ClientError(f"malformed ExperimentalMode entry in {cfg}")
        text = f'{text[:value_start]}<Value xsi:type="xsd:string">True{text[value_end:]}'
    elif "</DictionaryEntries>" in text:
        entry = ('  <DictionaryEntry>\n    <Key>ExperimentalMode</Key>\n'
                 '    <Value xsi:type="xsd:string">True</Value>\n  </DictionaryEntry>\n')
        text = text.replace("</DictionaryEntries>", entry + "</DictionaryEntries>", 1)
    else:
        raise ClientError(f"{cfg} has no ExperimentalMode key and no <DictionaryEntries> section")
    cfg.write_text(text)
    check = cfg.read_text()
    position = check.find(marker)
    if position < 0 or "True" not in check[position:position + 200]:
        raise ClientError(f"ExperimentalMode did not stick in {cfg}")


class Client:
    """One headless client process: its data directory, its process group and its Remote API."""

    def __init__(self, root: Path, client_id: int, remote_port: int, connect: str, name: str | None = None,
                 label: str | None = None, render: bool | None = None, max_fps: int = 15,
                 resolution: str = "1280x720", host: Host = HOST) -> None:
        self.host = host
        self.client_id = client_id
        self.key = label or str(client_id)
        self.data = root / f"client-{self.key}"
        self.log = root / f"client-{self.key}.log"
        self.dumps = root / "dumps"
        self.remote_port = remote_port
        self.connect = connect
        self.name = name or f"Bench-{client_id % 10000:04d}"
        # A host without a usable GPU runs every client without a back-buffer.
        self.render = host.render if render is None else (render and host.render)
        self.max_fps = max_fps
        self.resolution = resolution
        self.process: subprocess.Popen | None = None
        self.launched_at = 0.0

    # -- paths ------------------------------------------------------------

    @property
    def home(self) -> Path:
        return self.data / "home"

    @property
    def user_data(self) -> Path:
        """The game's UserDataPath: LinuxCompat maps it onto $XDG_CONFIG_HOME/SpaceEngineers."""
        return self.home / ".config/SpaceEngineers"

    @property
    def pulsar_data(self) -> Path:
        """Pulsar's data under -useHome; it must exist before launch or Pulsar falls back to Legacy/."""
        return self.home / ".config/Pulsar/Interim"

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.remote_port}"

    # -- staging ----------------------------------------------------------

    def prepare(self) -> None:
        for folder in (self.data / "tmp", self.data / "cache", self.user_data, self.dumps):
            folder.mkdir(parents=True, exist_ok=True)
        template = self.host.pulsar_profile
        if not (template / "config.xml").is_file():
            raise ClientError(f"no Pulsar profile at {template} (PULSAR_HOME)")
        # Never share_cache() the TEMPLATE: it is the live Pulsar profile (PULSAR_HOME/Interim), whose GitHub
        # link must keep pointing at the live cache.
        seed_cache(PULSAR_GITHUB_CACHE, LIVE_PULSAR_GITHUB_CACHE)
        if not self.pulsar_data.is_dir():
            self.pulsar_data.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(template, self.pulsar_data, symlinks=True,
                            ignore=shutil.ignore_patterns("*.log", "info_*"))
        share_cache(self.pulsar_data / "GitHub", PULSAR_GITHUB_CACHE)
        # Profiles and their source registrations are one pinned version set. Refresh both on
        # reused homes, or changing a task's Remote fixture leaves the old source silently loaded.
        for folder in ("Profiles", "Sources"):
            if (template / folder).is_dir():
                shutil.copytree(template / folder, self.pulsar_data / folder, dirs_exist_ok=True)
        pin_source_age(self.pulsar_data)
        live = Path.home() / ".config/SpaceEngineers"
        cfg = self.user_data / "SpaceEngineers.cfg"
        if not cfg.exists():
            if (live / "SpaceEngineers.cfg").is_file():
                shutil.copy2(live / "SpaceEngineers.cfg", cfg)
            else:
                cfg.write_text(GAME_CFG)
        set_experimental_mode(cfg)
        (self.user_data / "Remote.cfg").write_text(REMOTE_CFG.format(port=self.remote_port, password=REMOTE_PASSWORD))
        # Seed the shader cache file by file: the runtime HLSL compiler is flaky, a cache miss can
        # kill a rendering client at load.
        warm = live / "ShaderCache2"
        if warm.is_dir():
            target = self.user_data / "ShaderCache2"
            target.mkdir(exist_ok=True)
            for entry in warm.iterdir():
                if entry.is_file() and not (target / entry.name).exists():
                    shutil.copy2(entry, target / entry.name)

    def argv(self) -> list[str]:
        argv = [str(self.host.pulsar_launcher),
                "-noSplash", "-noPrompt", "-noUpdate", "-lazySteam", "-multiInstance", "-useHome",
                "-bin64", str(self.host.client_bin64),
                "--headless", "--max-fps", str(self.max_fps), "--resolution", self.resolution,
                "--no-audio",
                "--client-id", str(self.client_id), "--client-name", self.name,
                "--connect", self.connect]
        if not self.render:
            argv += ["--no-render", "--bare-bones"]
        return argv

    def environment(self) -> dict[str, str]:
        env = {
            **os.environ,
            **CRASH_ENV,
            "DOTNET_DbgMiniDumpName": str(self.dumps / f"client-{self.key}.%p.dmp"),
            "HOME": str(self.home),
            "XDG_CONFIG_HOME": str(self.home / ".config"),
            "XDG_CACHE_HOME": str(self.data / "cache"),
            "TMPDIR": str(self.data / "tmp"),
            "NUGET_PACKAGES": str(NUGET_PACKAGES),
            "SPACE_ENGINEERS_ROOT": str(self.host.client),
            "SE_DIRECT_NAME": self.name,
        }
        if not self.render:
            env["PULSAR_NO_RENDER"] = "true"
        env.update(local_gpu_pin())
        return env

    # -- lifecycle --------------------------------------------------------

    def start(self) -> None:
        if self.alive():
            raise ClientError(f"client {self.key} is already running")
        self.kill_leftovers()
        self.prepare()
        if self.log.exists() and self.log.stat().st_size:
            self.log.rename(self.log.with_name(f"{self.log.name}.{time.strftime('%Y%m%d-%H%M%S')}"))
        self.launched_at = time.time()
        with open(self.log, "wb", buffering=0) as log:
            self.process = subprocess.Popen(self.argv(), env=self.environment(), stdout=log,
                                            stderr=subprocess.STDOUT, start_new_session=True, cwd=self.data)

    def alive(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def wait_api_listening(self, timeout: float = 420) -> None:
        """The Remote listener announcement in this launch's logs; fails fast on exit."""
        pattern = re.compile(r"Started HTTP Listener|Remote API server started")
        deadline = time.time() + timeout
        while time.time() < deadline:
            # Remote logs through the game's logger; its announcement need not reach stdout.
            # Old game logs remain in a reused client home and must not satisfy a new launch.
            logs = [self.log, *self.game_logs()]
            if any(path.exists() and path.stat().st_mtime >= self.launched_at
                   and pattern.search(path.read_text(errors="replace")) for path in logs):
                return
            if self.process is not None and self.process.poll() is not None:
                raise ClientError(f"client {self.key} exited with {self.process.returncode} before its "
                                  f"Remote API started; see {self.log}")
            time.sleep(1)
        raise ClientError(f"client {self.key}: Remote API did not start within {timeout:.0f}s ({self.log})")

    def connect_rejected(self) -> bool:
        """Direct Transport refused or timed out the join, in a game log this launch wrote."""
        logs = [path for path in self.user_data.glob("SpaceEngineers_*.log")
                if path.stat().st_mtime >= self.launched_at - 5]
        if not logs:
            return False
        tail = max(logs, key=lambda path: path.stat().st_mtime).read_text(errors="replace")
        return "ConnectionRejected" in tail or ("Direct link" in tail and "timed out" in tail)

    def game_logs(self) -> list[Path]:
        return sorted(self.user_data.glob("SpaceEngineers_*.log"), key=lambda path: path.stat().st_mtime)

    def stop(self) -> None:
        """SIGKILL the client's process group, then anything that still carries its HOME."""
        if self.process is not None:
            try:
                os.killpg(os.getpgid(self.process.pid), signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
        self.kill_leftovers()

    def kill_leftovers(self) -> list[int]:
        """Processes of THIS client that outlived their group (an orphan keeps the Remote API port
        bound and poisons the next start). Found by the per-client HOME in their environment, which
        no other process on the host carries."""
        marker = b"\0" + f"HOME={self.home}".encode() + b"\0"
        killed = []
        for proc in Path("/proc").iterdir():
            if not proc.name.isdigit() or int(proc.name) == os.getpid():
                continue
            try:
                environ = (proc / "environ").read_bytes()
            except OSError:
                continue
            if marker in b"\0" + environ:
                try:
                    os.kill(int(proc.name), signal.SIGKILL)
                    killed.append(int(proc.name))
                except OSError:
                    pass
        return killed
