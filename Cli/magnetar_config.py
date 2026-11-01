"""Magnetar config directories for the cluster: plugin sources, local plugins and profiles.

A Magnetar config dir (``-config``) holds ``Sources/sources.xml`` (where plugins come from),
``Local/`` (precompiled "local" plugins: ``<name>/<name>.dll`` with ``<name>.xml`` beside it) and
``Profiles/<name>.xml`` (which plugins a launch enables).

Two ways to supply the cluster plugins (T-0157, Viktor 2026-09-15):

* **Release (default):** the archive ships the COMPILED plugins under ``plugins/ClusterNode`` and
  ``plugins/WorldAuthority``. ``write_config(..., binaries=<archive>/plugins)`` copies the one the
  role needs into ``<config>/Local/`` and enables it in the profile's ``<Local>`` list. Nothing is
  compiled on the node, and the plugin is the same build as the Gateway, tools and CLI of the
  archive. The plugins are never published to the Magnetar hub.
* **Development:** ``write_config(..., cluster=<checkout>)`` registers the checkout as a dev folder
  (``ClusterNode.xml`` / ``WorldAuthority.xml``) that Magnetar compiles at first start and caches
  per config dir.

The Direct Transport server plugin (id ``direct-transport``, repository ``CometWorks/direct-transport``)
is registered as a dev folder when ``transport=<checkout>`` is given - the dev override for a
transport change that is not published yet - and otherwise, which is the default, enabled as a
``<GitHub>`` entry from the Magnetar hub, the same pin the clients load (T-0157 requirement 1).
``transport_binaries=<DirectTransport folder>`` instead installs a precompiled, pinned
transport bundle locally, without resolving or compiling transport on each node.
Compatibility and other hub plugins still follow their usual loading paths.

Magnetar keys dev folders by folder path, so the two manifests of one repository collide when
both are registered in one config (the second replaces the first). The World Authority entry
therefore registers the folder with a trailing slash, which Magnetar treats as a distinct path
with its own compile cache. A proper fix (keying dev folders by manifest) is proposed in
Docs/Magnetar.md of the cluster repository; drop the workaround once it lands. The cluster CLI
writes one config dir per node with a single cluster plugin, so it never needs the workaround.
"""

from __future__ import annotations

import os
import re
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

HUB = {"Name": "MagnetarHub", "Repo": "CometWorks/magnetar-hub", "Branch": "main",
       "Enabled": "true", "Trusted": "true"}

#: How long Magnetar treats a fetched source as fresh, in HOURS. Once the cluster is up,
#: nothing may go back to GitHub or NuGet: the caches are warmed once at start and every
#: later instance must use them unchanged (Viktor, 2026-09-05, T-0052). Two reasons:
#:
#:   1. Rate limiting. The public IP gets limited by BOTH GitHub and NuGet, and the cluster
#:      is many processes - six server-side Magnetar instances plus a rotating fleet. w29
#:      logged 157 rotation events, so fresh Magnetar processes start all run, not just at
#:      boot. T-0033 measured the GitHub allowance fully consumed: limit 60, remaining 0.
#:   2. Consistency. A node started at hour 20 must run the SAME plugin build as one started
#:      at hour 0. Re-resolving mid-run is a silent build-divergence risk, and divergence
#:      hides behind a green checksum.
#:
#: The old value was 2 hours, so a 24 h run crossed the staleness window twelve times.
#: One year covers any run; the warm-up before cluster start is what refreshes the cache.
MAX_SOURCE_AGE_HOURS = os.environ.get("CLUSTER_MAX_SOURCE_AGE_HOURS", "8760")
#: Hub plugins every node and WA instance loads, as ``<GitHub>`` entries in the written
#: profile. The ids are the ones the hub publishes, NOT the repository names: the Performance
#: plugin's server half is 454B0F55-... in CometWorks/magnetar-hub (its client half in
#: StarCpt/PluginHub carries the string id viktor-ferenczi/se-performance-improvements, which
#: is what the Pulsar client profile lists). Enabled 2026-09-08 to test the O(N log N)
#: replacement for the O(N^2) physics-clustering scan against the >900-entity node slowdown.
#: The Bugfixes plugin's server half, id "bugfixes" (CometWorks/bugfixes; the UUID A77D81C6-... until 2026-10-07), listed in
#: CometWorks/magnetar-hub since 2026-09-23 01:12, "Bugfixes 1.1.0"), prevents some crashes and runs on
#: the servers AND the clients, each loaded from its own hub (Viktor, 2026-09-23). The clients enable it
#: by the same id from the PluginHub (Pulsar profile template, since 2026-09-15).
#: Set CLUSTER_HUB_PLUGINS to a comma-separated id list to override, or to an empty string to
#: run with no hub plugins at all.
BUGFIXES_ID = "bugfixes"
DEFAULT_HUB_PLUGINS = "454B0F55-C727-4DAB-B1A9-5AE6050A0932," + BUGFIXES_ID
HUB_PLUGINS = [entry.strip() for entry
               in os.environ.get("CLUSTER_HUB_PLUGINS", DEFAULT_HUB_PLUGINS).split(",")
               if entry.strip()]
#: The Direct Transport server plugin's id in CometWorks/magnetar-hub (RepoId
#: CometWorks/direct-transport). Both cluster manifests declare it as a dependency under this id.
TRANSPORT_ID = "direct-transport"

PROFILES = {"node": "cluster-node", "wa": "cluster-wa"}
MANIFESTS = {"node": "ClusterNode.xml", "wa": "WorldAuthority.xml"}
#: Local-plugin directory per role: <binaries>/<name>/<name>.dll + <name>.xml (Build/release.sh).
BINARIES = {"node": "ClusterNode", "wa": "WorldAuthority"}


def _set_max_source_age(root: ET.Element) -> None:
    """Force MaxSourceAge, on new configs AND on ones already on disk.

    _load_sources returns an existing file unchanged, so setting this only on the
    create path would leave every profile that already exists at its old value - which
    is exactly the state found on 2026-09-05: all five nodes, the WA, and both host
    profiles sat at 2 hours. write_config rewrites sources.xml at every node start, so
    enforcing it here repairs them without touching the files by hand (a hand edit would
    be undone by the next start anyway).
    """
    element = root.find("MaxSourceAge")
    if element is None:
        element = ET.SubElement(root, "MaxSourceAge")
    element.text = MAX_SOURCE_AGE_HOURS


def _load_sources(path: Path) -> ET.Element:
    if path.is_file():
        root = ET.parse(path).getroot()
        _set_max_source_age(root)
        return root
    root = ET.Element("SourcesConfig")
    ET.SubElement(root, "ShowWarning").text = "false"
    ET.SubElement(root, "MaxSourceAge").text = MAX_SOURCE_AGE_HOURS
    ET.SubElement(root, "LocalHubSources")
    hubs = ET.SubElement(root, "RemoteHubSources")
    hub = ET.SubElement(hubs, "RemoteHub")
    for key, value in HUB.items():
        ET.SubElement(hub, key).text = value
    ET.SubElement(root, "RemotePluginSources")
    ET.SubElement(root, "LocalPluginSources")
    ET.SubElement(root, "ModSources")
    return root


def _unregister(root: ET.Element, name: str) -> None:
    """Drop the dev-folder entry ``name`` (a role this config no longer compiles from source, or
    the Direct Transport checkout once the hub supplies it)."""
    locals_ = root.find("LocalPluginSources")
    if locals_ is None:
        return
    for entry in list(locals_):
        if entry.findtext("Name") == name:
            locals_.remove(entry)
    if len(locals_) == 0:
        locals_.text = None  # ET.indent leaves the parsed whitespace of a now-empty element alone


def _register(root: ET.Element, name: str, folder: str, file: str) -> None:
    locals_ = root.find("LocalPluginSources")
    if locals_ is None:
        locals_ = ET.SubElement(root, "LocalPluginSources")
    for entry in list(locals_):
        if (entry.findtext("Name") == name
                or (entry.findtext("Folder") == folder and entry.findtext("File") == file)):
            locals_.remove(entry)
    entry = ET.SubElement(locals_, "LocalPlugin")
    ET.SubElement(entry, "Name").text = name
    ET.SubElement(entry, "Folder").text = folder
    ET.SubElement(entry, "File").text = file
    ET.SubElement(entry, "Enabled").text = "true"


def _write_xml(root: ET.Element, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    root.set("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance")
    root.set("xmlns:xsd", "http://www.w3.org/2001/XMLSchema")
    ET.indent(root)
    path.write_text('<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(root, encoding="unicode") + "\n")


def _profile(name: str, hub_ids: list[str], dev_ids: list[str], local_ids: list[str], debug: bool) -> ET.Element:
    """A profile enabling ``hub_ids`` from the hub (``<GitHub>``), ``dev_ids`` as dev folders
    (``<DevFolder>``) and ``local_ids`` as precompiled local plugins (``<Local>``, Pulsar's
    ``HashSet<string>`` of plugin ids)."""
    root = ET.Element("Profile")
    ET.SubElement(root, "Name").text = name
    hub = ET.SubElement(root, "GitHub")
    for plugin_id in hub_ids:
        cfg = ET.SubElement(hub, "GitHubPluginConfig")
        ET.SubElement(cfg, "Id").text = plugin_id
    dev = ET.SubElement(root, "DevFolder")
    for plugin_id in dev_ids:
        cfg = ET.SubElement(dev, "LocalFolderConfig")
        ET.SubElement(cfg, "Id").text = plugin_id
        ET.SubElement(cfg, "DebugBuild").text = "true" if debug else "false"
    local = ET.SubElement(root, "Local")
    for plugin_id in local_ids:
        ET.SubElement(local, "string").text = plugin_id
    ET.SubElement(root, "Mods")
    return root


def _install_local(config: Path, binaries: Path, roles: list[str]) -> list[str]:
    """Copy the roles' compiled plugins from ``binaries`` into ``<config>/Local/`` and remove the
    cluster plugins of other roles; returns the plugin ids read from the manifests.

    A copy, not a symlink: the node must keep running its build even if the archive moves, and
    Pulsar's local-plugin scan walks real directories. ~1 MB per plugin."""
    local_root = config / "Local"
    for role in roles:
        folder = binaries / BINARIES[role]
        if folder.resolve().is_relative_to(local_root.resolve()):
            raise ValueError("plugin input cannot be inside the destination Local directory")
        if _local_id(folder) != PROFILES[role]:
            raise ValueError(f"{folder} must declare {PROFILES[role]}")
    local_root.mkdir(parents=True, exist_ok=True)
    wanted = {BINARIES[r] for r in roles}
    for stale in BINARIES.values():
        if stale not in wanted and (local_root / stale).exists():
            shutil.rmtree(local_root / stale)
    ids = []
    for r in roles:
        name = BINARIES[r]
        source = binaries / name
        target = local_root / name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(source, target)
        plugin_id = ET.parse(target / f"{name}.xml").getroot().findtext("Id")
        if plugin_id != PROFILES[r]:
            raise ValueError(f"{source}/{name}.xml declares id {plugin_id!r}, expected {PROFILES[r]!r}")
        ids.append(plugin_id)
    return ids


#: The extra local plugins write_config installed last time, so a later write can remove the ones
#: no longer requested.
EXTRA_RECORD = ".extra-plugins"


def _local_id(folder: Path) -> str:
    """Validate a compiled plugin before changing the destination config."""
    if folder.is_symlink() or any(path.is_symlink() for path in folder.rglob("*")):
        raise ValueError(f"local plugin bundle must not contain links: {folder}")
    assembly, metadata = folder / (folder.name + ".dll"), folder / (folder.name + ".xml")
    if not assembly.is_file() or not assembly.stat().st_size or not metadata.is_file():
        raise FileNotFoundError(f"missing compiled plugin: {assembly} + {metadata}")
    plugin_id = ET.parse(metadata).getroot().findtext("Id")
    if not plugin_id or not plugin_id.strip():
        raise ValueError(f"{metadata} declares no <Id>")
    return plugin_id


def _validate_transport(folder: Path) -> None:
    if folder.name != "DirectTransport" or _local_id(folder) != TRANSPORT_ID:
        raise ValueError("compiled transport must be a DirectTransport folder declaring direct-transport")
    root = ET.parse(folder / "DirectTransport.xml").getroot()
    if not re.fullmatch(r"[0-9a-f]{40}", root.findtext("Commit", "")):
        raise ValueError("compiled Direct Transport must declare an exact source commit")
    if root.findtext("Runtimes") != "CoreCLR" or root.findtext("Platforms") != "Linux":
        raise ValueError("compiled Direct Transport must target CoreCLR on Linux")
    dependency = folder / "LiteNetLib.dll"
    if not dependency.is_file() or not dependency.stat().st_size:
        raise FileNotFoundError(f"missing transport dependency: {dependency}")


def _install_extra(config: Path, folders: list[Path]) -> list[str]:
    """Copy extra precompiled local plugins into ``<config>/Local/``: each folder is laid out like
    the packaged ones (``<Name>/<Name>.dll`` + ``<Name>.xml`` + dependencies). Extras installed by
    an earlier write and not requested now are removed. Returns their plugin ids."""
    local_root = config / "Local"
    local_root.mkdir(parents=True, exist_ok=True)
    record = local_root / EXTRA_RECORD
    previous = record.read_text().splitlines() if record.is_file() else []
    wanted = [Path(folder).expanduser().absolute() for folder in folders]
    names = [folder.name for folder in wanted]
    ids = [_local_id(folder) for folder in wanted]
    if len(set(names)) != len(names) or len(set(ids)) != len(ids):
        raise ValueError("extra plugins must have unique folder names and IDs")
    if set(names) & set(BINARIES.values()) or set(ids) & set(PROFILES.values()):
        raise ValueError("extra plugins cannot replace cluster role plugins")
    if any(folder.resolve().is_relative_to(local_root.resolve()) for folder in wanted):
        raise ValueError("plugin input cannot be inside the destination Local directory")
    if any(not name or Path(name).name != name or name in (".", "..") for name in previous):
        raise ValueError("invalid extra-plugin record")
    for stale in previous:
        if stale not in names and stale not in BINARIES.values() and (local_root / stale).exists():
            shutil.rmtree(local_root / stale)
    for folder in wanted:
        name = folder.name
        target = local_root / name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(folder, target)
    record.write_text("\n".join(names) + ("\n" if names else ""))
    return ids


def write_config(config: Path, cluster: Path | None = None, transport: Path | None = None,
                 role: str = "both", debug: bool = False, binaries: Path | None = None,
                 extra_local: list[Path] | None = None,
                 transport_binaries: Path | None = None, common_plugins: Path | None = None) -> list[str]:
    """Write sources, local plugins and profiles into ``config``; returns the enabled plugin ids
    (Direct Transport first, then the role's cluster plugin ids).

    ``role``: ``node``, ``wa`` or ``both``. Exactly one of ``binaries`` (a folder holding
    ``ClusterNode/`` and ``WorldAuthority/`` local plugins, the archive's ``plugins/``) or
    ``cluster`` (a checkout to compile as dev folders) must be given. ``transport``: a Direct
    Transport checkout to register as a dev folder. ``transport_binaries``: a compiled,
    pinned DirectTransport folder, mutually exclusive with ``transport``. If neither
    is given, load transport from the hub.
    Existing sources are kept; entries written here replace same-named ones and the entries of
    the mode not in use are removed. Other profiles are left alone. ``extra_local``: further
    precompiled local plugin folders (a test utility plugin) enabled next to the cluster plugins of
    every role written.
    """
    if (binaries is None) == (cluster is None):
        raise ValueError("write_config needs exactly one of binaries= (compiled plugins) or cluster= (sources)")
    if role not in (*PROFILES, "both"):
        raise ValueError(f"invalid cluster role: {role}")
    if transport_binaries is not None and transport is not None:
        raise ValueError("choose compiled Direct Transport or a source checkout, not both")
    frozen = common_plugins is not None
    if frozen:
        if binaries is None or transport_binaries is None or extra_local or os.environ.get("CLUSTER_HUB_PLUGINS"):
            raise ValueError("frozen plugins require packaged role binaries and compiled transport, without extra/hub overrides")
        common_plugins = Path(common_plugins).expanduser().absolute()
        if common_plugins.is_symlink() or any(not p.is_dir() for p in common_plugins.iterdir()):
            raise ValueError("common plugins must contain only named plugin bundles")
        extra_local = sorted(common_plugins.iterdir())
        common_ids = {_local_id(folder) for folder in extra_local}
        if not {"dotnet-compat", "linux-compat"} <= common_ids:
            raise ValueError("frozen plugins require dotnet-compat and linux-compat")
    extras = [Path(folder).expanduser().absolute() for folder in (extra_local or [])]
    extra_ids = [_local_id(folder) for folder in extras]
    if set(extra_ids) & {TRANSPORT_ID, *PROFILES.values()}:
        raise ValueError("extra plugins cannot replace required infrastructure")
    if not frozen and set(HUB_PLUGINS) & {TRANSPORT_ID, *PROFILES.values(), *extra_ids}:
        raise ValueError("hub plugin selection duplicates local or required infrastructure")
    if transport_binaries is not None:
        transport_binaries = Path(transport_binaries).expanduser().absolute()
        _validate_transport(transport_binaries)
        extras.append(transport_binaries)
    config = Path(config).expanduser().resolve()
    roles = ["node", "wa"] if role == "both" else [role]
    if transport is not None:
        transport = str(Path(transport).expanduser().resolve())
        if not (Path(transport) / "DirectTransportServer.xml").is_file():
            raise FileNotFoundError(f"missing plugin manifest: {transport}/DirectTransportServer.xml")
    if cluster is not None:
        cluster = str(Path(cluster).expanduser().resolve())
        for r in roles:
            if not (Path(cluster) / MANIFESTS[r]).is_file():
                raise FileNotFoundError(f"missing plugin manifest: {cluster}/{MANIFESTS[r]}")

    sources_path = config / "Sources/sources.xml"
    root = _load_sources(sources_path)
    if frozen:
        # Persist an explicit empty source list; no first-start hub defaults or dev overrides.
        for section in ("LocalHubSources", "RemoteHubSources", "RemotePluginSources", "LocalPluginSources", "ModSources"):
            element = root.find(section)
            if element is not None:
                root.remove(element)
            ET.SubElement(root, section)
    hub_ids, dev_ids, local_ids = ([] if frozen else list(HUB_PLUGINS)), [], []
    if transport_binaries is not None:
        _unregister(root, TRANSPORT_ID)
    elif transport is None:
        _unregister(root, TRANSPORT_ID)
        hub_ids.append(TRANSPORT_ID)
    else:
        _register(root, TRANSPORT_ID, transport, "DirectTransportServer.xml")
        dev_ids.append(TRANSPORT_ID)
    if binaries is not None:
        for r in ("node", "wa"):
            _unregister(root, PROFILES[r])
        local_ids = _install_local(config, Path(binaries).expanduser().resolve(), roles)
    else:
        for r in ("node", "wa"):
            if r not in roles:
                _unregister(root, PROFILES[r])
        for r in roles:
            folder = cluster + ("/" if r == "wa" and role == "both" else "")  # trailing slash: module docstring
            _register(root, PROFILES[r], folder, MANIFESTS[r])
            dev_ids.append(PROFILES[r])
    installed_extra_ids = _install_extra(config, extras)
    if frozen:
        expected_names = {BINARIES[r] for r in roles} | {folder.name for folder in extras} | {EXTRA_RECORD}
        if any(path.name not in expected_names for path in (config / "Local").iterdir()):
            raise ValueError("unexpected local plugin entry in frozen configuration")
        # Magnetar still decides whether the game needs compatibility plugins.
        installed_extra_ids = [i for i in installed_extra_ids if i not in ("dotnet-compat", "linux-compat")]
    _write_xml(root, sources_path)

    profiles = config / "Profiles"
    for r in roles:
        role_dev = [i for i in dev_ids if i == TRANSPORT_ID or i == PROFILES[r]]
        role_local = [i for i in local_ids if i == PROFILES[r]] + installed_extra_ids
        _write_xml(_profile(PROFILES[r], hub_ids, role_dev, role_local, debug), profiles / f"{PROFILES[r]}.xml")
    _write_xml(_profile("Current", hub_ids, dev_ids, local_ids + installed_extra_ids, debug), profiles / "Current.xml")
    return [TRANSPORT_ID] + [PROFILES[r] for r in roles] + extra_ids
