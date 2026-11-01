"""Build and install the test utility plugin (``ClusterTestPlugin/``) for the package under test.

The plugin serves the test-only control routes. It is compiled against the package's own
``plugins/`` DLLs, so each package gets its own build, cached under
``CLUSTER_TEST_ROOT/test-plugin/<key>`` where the key covers the package's cluster plugin DLLs and
the plugin source. ``install()`` points ``CLUSTER_EXTRA_PLUGINS`` at the build; the package's CLI
then copies it into each node's Magnetar ``Local/`` folder and enables it in the role's profile.

``CLUSTER_TEST_PLUGIN=0`` runs without it (a production-surface check).
"""

from __future__ import annotations

import hashlib
import os
import shutil
import subprocess
from pathlib import Path

from hostenv import TEST_DIR, Host

SOURCE = TEST_DIR / "ClusterTestPlugin"
ROLES = {"node": "ClusterTestNode", "wa": "ClusterTestWA"}


def build_key(package: Path, magnetar: Path | None = None) -> str:
    digest = hashlib.sha256()
    for dll in ("ClusterNode/ClusterNode.dll", "WorldAuthority/WorldAuthority.dll"):
        digest.update((package / "plugins" / dll).read_bytes())
    # build.sh compiles against MAGNETAR_HOME's PluginSdk (T-0256): a staged Magnetar with another SDK
    # must not reuse a build made against the host's.
    sdk = magnetar / "Libraries/MagnetarInterim/PluginSdk.dll" if magnetar else None
    if sdk and sdk.is_file():
        digest.update(sdk.read_bytes())
    for path in sorted(SOURCE.rglob("*")):
        if path.is_file() and not ({"bin", "obj"} & set(path.relative_to(SOURCE).parts)):
            digest.update(str(path.relative_to(SOURCE)).encode())
            digest.update(path.read_bytes())
    return digest.hexdigest()[:16]


def build(host: Host) -> Path:
    """The plugin folders for ``host.package``; built on first use."""
    out = host.test_root / "test-plugin" / build_key(host.package, host.magnetar)
    if all((out / name / f"{name}.dll").is_file() for name in ROLES.values()):
        return out
    staging = out.with_name(out.name + ".partial")
    shutil.rmtree(staging, ignore_errors=True)
    result = subprocess.run(["bash", str(SOURCE / "build.sh"), str(host.package), str(staging)],
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"test plugin build failed (exit {result.returncode}):\n"
                           + (result.stdout + result.stderr)[-3000:])
    shutil.rmtree(out, ignore_errors=True)
    staging.rename(out)
    return out


def install(host: Host) -> dict[str, Path] | None:
    """Build the plugin and export CLUSTER_EXTRA_PLUGINS for the package's CLI. Returns the role
    folders, or None when disabled (CLUSTER_TEST_PLUGIN=0)."""
    if os.environ.get("CLUSTER_TEST_PLUGIN", "1") in ("0", "false", "no"):
        return None
    out = build(host)
    folders = {role: out / name for role, name in ROLES.items()}
    ours = ",".join(f"{role}:{folder}" for role, folder in folders.items())
    existing = os.environ.get("CLUSTER_EXTRA_PLUGINS", "")
    # Keep extras the host configured; drop a stale build of this plugin.
    kept = [entry for entry in existing.split(",") if entry.strip()
            and not any(Path(entry.partition(":")[2] or entry).name == name for name in ROLES.values())]
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(kept + [ours])
    return folders
