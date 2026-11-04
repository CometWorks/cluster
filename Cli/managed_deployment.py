"""Nonlaunching production deployment preparation. Never refreshes an existing run root.

The same approved specification and seed are used on every host. Artifact acquisition,
activation and process supervision belong to Quasar.Host, not this module.
"""
from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import quoteattr

# The package is hash-pinned; importing its helpers must not add __pycache__ files.
sys.dont_write_bytecode = True
import magnetar_config

# .NET garbage collector of the node processes (#1246), spec field nodeGarbageCollector. Workstation GC runs
# a blocking gen2 collection on one thread: under host CPU contention that froze 4-9 GB nodes for 10-51 s.
# Server GC collects on several threads; DATAS (the .NET 10 default for Server GC) sizes the heap count to
# the load instead of one heap per core. Several nodes share a host, so their GC threads are not pinned to
# the same cores. Both modes are explicit so a variable inherited from Quasar.Host cannot flip them.
NODE_GC_ENVIRONMENT = {
    "server": {"DOTNET_gcServer": "1", "DOTNET_GCDynamicAdaptationMode": "1", "DOTNET_GCNoAffinitize": "1"},
    "workstation": {"DOTNET_gcServer": "0"},
}

# Havok worker threads of the Performance plugin's server half (SE-718). Its default, Auto, gives every node
# one worker per physical core minus one. The workers spin between jobs, so the nodes of one host fight over
# the same cores, and a jointed scene on Linux ran faster with 2 workers than with 7. A host with several
# nodes therefore splits its cores between them, 2 to 4 workers each. An operator's Manual or Game is kept.
PERFORMANCE_ID = "454B0F55-C727-4DAB-B1A9-5AE6050A0932"
HAVOK_WORKERS = (2, 4)


def physical_cores() -> int:
    """Hyper-threading sibling groups, the count the Performance plugin's Auto mode starts from."""
    siblings = {path.read_text().strip() for path in
                Path("/sys/devices/system/cpu").glob("cpu[0-9]*/topology/thread_siblings_list")}
    return len(siblings) or os.cpu_count() or 1


def shared_host_havok(values: dict, nodes_on_host: int, cores: int) -> dict:
    """The Performance configuration with Manual Havok workers when several nodes share the host."""
    if nodes_on_host < 2:
        return values
    workers = max(HAVOK_WORKERS[0], min(HAVOK_WORKERS[1], cores // nodes_on_host - 1))
    values = json.loads(json.dumps(values))
    for entry in values.get("configurations", [values] if values else []):
        settings = entry["configuration"].get("values", {})
        if settings.get("havokThreadCountMode") == "Auto":
            settings.update(havokThreadCountMode="Manual", havokThreadCount=workers)
    return values


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def link_or_copy(source, destination):
    """copytree's copy_function: a hard link, or a copy where the filesystem refuses one."""
    try:
        os.link(source, destination)
    except OSError:
        shutil.copy2(source, destination)


def plain_path(path: Path) -> Path:
    path = path.absolute()
    if any(p.is_symlink() for p in (path, *path.parents)):
        raise ValueError(f"symbolic link is not allowed: {path}")
    return path


def inventory(root: Path) -> dict[str, str]:
    plain_path(root)
    files = {}
    for path in sorted(root.rglob("*")):
        plain_path(path)
        if path.is_file():
            files[path.relative_to(root).as_posix()] = digest(path)
        elif not path.is_dir():
            raise ValueError(f"unsupported file: {path}")
        if len(files) > 200_000:
            raise ValueError("deployment exceeds 200000 files")
    return files


def read_json(path: Path, limit: int = 64 * 1024 * 1024):
    plain_path(path)
    if path.stat().st_size > limit:
        raise ValueError("JSON document is too large")
    return json.loads(path.read_bytes())


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x") as stream:
        json.dump(value, stream, sort_keys=True, separators=(",", ":"))
        stream.flush()
        os.fsync(stream.fileno())


def installation(root: Path) -> dict:
    pins = read_json(root / "inputs.json")
    if digest(root / "inputs.json") != root.name:
        raise ValueError("installation path does not match its deployment input hash")
    actual = {f"{area}/{name}": sha for area in ("Package", "Dependencies")
              for name, sha in inventory(root / area).items()}
    if actual != {name: pin["sha256"] for name, pin in pins["files"].items()}:
        raise ValueError("installation no longer matches verified deployment inputs")
    return pins


def name(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", value):
        raise ValueError("IDs must contain 1..128 ASCII letters, digits, underscores or hyphens")
    return value


def endpoint(value: str) -> int:
    address, separator, port = value.rpartition(":")
    if not separator or not 1 <= int(port) <= 65535:
        raise ValueError("endpoint requires an IP address and valid port")
    ipaddress.ip_address(address.strip("[]"))
    return int(port)


def variable(value: str) -> str:
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,127}", value):
        raise ValueError("credential must name a host environment variable")
    return value


def plugin_configuration(values: dict) -> dict:
    """Validate canonical SDK config input without guessing plugin storage paths."""
    if not isinstance(values, dict) or set(values) - {"configType", "configuration", "configurations"}:
        raise ValueError("invalid plugin configuration fields")
    if "configurations" in values:
        if set(values) != {"configurations"} or not isinstance(values["configurations"], list):
            raise ValueError("configurations cannot be mixed with flat configuration fields")
        entries = values["configurations"]
    else:
        entries = [values] if values else []
    seen = set()
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {"configType", "configuration"}:
            raise ValueError("each configuration requires configType and configuration")
        config_type = entry["configType"]
        if not isinstance(config_type, str) or not config_type.strip() or not isinstance(entry["configuration"], dict):
            raise ValueError("configuration requires a type name and object")
        if config_type in seen:
            raise ValueError("duplicate plugin configuration type")
        seen.add(config_type)
    return values


def staged_world_mods(world: Path) -> list[str]:
    """Require complete, exact local payloads for the checkpoint's mod list."""
    selections = None
    for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
        source = world / filename
        if not source.exists():
            continue
        found = []
        for item in ET.parse(source).iter():
            if item.tag.rsplit("}", 1)[-1] != "ModItem":
                continue
            fields = {child.tag.rsplit("}", 1)[-1]: (child.text or "").strip() for child in item}
            mod_id = fields.get("PublishedFileId", "")
            if not mod_id.isascii() or not mod_id.isdecimal() or int(mod_id) <= 0 \
                    or fields.get("Name") != f"{mod_id}.sbm":
                raise ValueError("managed world mods require verified local payload preparation")
            found.append(mod_id)
        if len(found) != len(set(found)) or selections is not None and found != selections:
            raise ValueError("managed world mod lists differ or contain duplicates")
        selections = found
    selected = selections or []
    payload = world / "Mods"
    actual = {entry.name for entry in payload.iterdir()} if payload.exists() else set()
    expected = {f"{mod_id}.sbm" for mod_id in selected}
    if actual != expected or any(not (payload / name).is_dir()
                                 or not any(path.is_file() for path in (payload / name).rglob("*")) for name in actual):
        raise ValueError("managed world mods require verified local payload preparation")
    return selected


def apply_profile_patch(world: Path, xml: str) -> None:
    desired = ET.fromstring(xml)
    if desired.tag.rsplit("}", 1)[-1] != "MyObjectBuilder_Checkpoint":
        raise ValueError("profile settings patch must be a checkpoint")
    fields = {child.tag.rsplit("}", 1)[-1]: child for child in desired}
    if set(fields) - {"SessionName", "Settings", "Mods"} or "Settings" not in fields or "Mods" not in fields:
        raise ValueError("profile settings patch has unsupported fields")
    for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
        target = world / filename
        if not target.exists():
            continue
        declared = {}
        for _, (prefix, uri) in ET.iterparse(target, events=("start-ns",)):
            declared.setdefault(prefix, uri)
        checkpoint = ET.parse(target)
        root = checkpoint.getroot()
        for name, replacement in fields.items():
            old = next((child for child in root if child.tag.rsplit("}", 1)[-1] == name), None)
            if old is not None and name == "Settings":
                for setting in replacement:
                    current = next((child for child in old if child.tag.rsplit("}", 1)[-1]
                                    == setting.tag.rsplit("}", 1)[-1]), None)
                    if current is not None:
                        old.remove(current)
                    old.append(ET.fromstring(ET.tostring(setting)))
            elif old is not None:
                index = list(root).index(old)
                root.remove(old)
                root.insert(index, ET.fromstring(ET.tostring(replacement)))
            else:
                root.append(ET.fromstring(ET.tostring(replacement)))
        write_checkpoint(root, target, declared)


def game_mode_tag(checkpoint: ET.ElementTree) -> str:
    """The gamemode Steam tag of the world, as MyDedicatedServer.SendGameTagsToSteam builds it: survival
    S<inventory>-<blocks inventory>-<assembler>-<refinery> (truncated to int), creative C. A missing field keeps
    MyObjectBuilder_SessionSettings' default (Creative; 3, 1, 3, 3) (#1529)."""
    found = checkpoint.getroot().find("Settings")
    settings = {element.tag.rsplit("}", 1)[-1]: (element.text or "").strip() for element in ([] if found is None else found)}
    if settings.get("GameMode", "Creative") != "Survival":
        return "C"
    return "S" + "-".join(str(int(float(settings.get(field) or default))) for field, default in (
        ("InventorySizeMultiplier", 3), ("BlocksInventorySizeMultiplier", 1),
        ("AssemblerSpeedMultiplier", 3), ("RefinerySpeedMultiplier", 3)))


def write_checkpoint(root: ET.Element, target: Path, declared: dict[str, str]) -> None:
    """ElementTree writes only the namespace declarations that tag and attribute names use. The game also
    names types in attribute values (xsi:type="xsd:string" on object-typed ScriptManager variables, which
    MES writes), and XmlSerializer refuses those without the original xmlns:xsd (#1371)."""
    text = ET.tostring(root, encoding="unicode")
    end = text.index(">")
    end -= text[end - 1] == "/"
    missing = "".join(f" xmlns:{prefix}={quoteattr(uri)}" for prefix, uri in declared.items()
                      if prefix and f"xmlns:{prefix}=" not in text[:end])
    target.write_text("<?xml version='1.0' encoding='utf-8'?>\n" + text[:end] + missing + text[end:],
                      encoding="utf-8")


def convert(root: Path, source: Path, destination: Path) -> dict:
    installation(root)
    source, destination = plain_path(source), plain_path(destination)
    if destination.exists() or destination.is_relative_to(source):
        raise ValueError("conversion requires a new destination outside the source world")
    before = inventory(source)
    if "Sandbox.sbc" not in before:
        raise ValueError("source is not a Space Engineers world")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".convert-", dir=destination.parent))
    try:
        output = staging / "world"
        ds = root / "Dependencies/payload/DedicatedServer/DedicatedServer64"
        tool = root / "Package/tools/MagnetarWorld/MagnetarWorld.dll"
        subprocess.run(["dotnet", str(tool), "to-split", str(source), str(output)], check=True,
                       env={**os.environ, "SPACE_ENGINEERS_BIN64": str(ds), "SPACE_ENGINEERS_ROOT": str(ds.parent)})
        if inventory(source) != before:
            raise ValueError("conversion changed the source world")
        pins = inventory(output)
        if "Sandbox.sbc" not in pins:
            raise ValueError("conversion produced no checkpoint")
        ET.parse(output / "Sandbox.sbc")
        output.rename(destination)
        return {"directory": str(destination), "files": pins}
    finally:
        shutil.rmtree(staging)


def prepare(root: Path, spec_path: Path, expected_sha: str, host_id: str, world: Path, destination: Path,
            mod_payload: Path | None = None) -> dict:
    root, destination, world = plain_path(root), plain_path(destination), plain_path(world)
    if digest(spec_path) != expected_sha:
        raise ValueError("deployment specification hash mismatch")
    spec = read_json(spec_path)
    pins = installation(root)
    checked = subprocess.run(["dotnet", str(root / "Package/registry/ClusterRegistry.dll"),
        "--check-plugin-sdk", str(root / "Dependencies/payload/Magnetar/Libraries/MagnetarInterim/PluginSdk.dll")],
        capture_output=True, text=True, timeout=60)
    if checked.returncode:
        raise ValueError("Magnetar SDK is incompatible: " + (checked.stderr or checked.stdout).strip())
    if spec["schemaVersion"] != 1 or spec["clusterId"] != pins["clusterId"]:
        raise ValueError("deployment specification does not match installation")
    name(spec["clusterId"])
    name(spec["worldId"])
    hosts = {name(host["id"]): host for host in spec["hosts"]}
    if len(hosts) != len(spec["hosts"]) or host_id not in hosts or spec["gatewayHost"] not in hosts:
        raise ValueError("invalid host inventory")
    nodes = spec["nodes"]
    if not 2 <= len(nodes) <= 256 or sum(node["role"] == "WA" for node in nodes) != 1:
        raise ValueError("deployment requires regular nodes and exactly one World Authority")
    if len({name(node["slotKey"]) for node in nodes}) != len(nodes) or len({name(node["nodeId"]) for node in nodes}) != len(nodes):
        raise ValueError("duplicate node or slot")
    if len({node["catalogSlot"] for node in nodes}) != len(nodes):
        raise ValueError("catalog slots must be unique")
    ports = {host: set() for host in hosts}
    for node in nodes:
        if node["host"] not in hosts or node["role"] not in ("regular", "WA") or not 0 < node["catalogSlot"] < 65536:
            raise ValueError("invalid node placement, role or catalog slot")
        if not isinstance(node.get("spare", False), bool) or node.get("spare") and node["role"] != "regular":
            raise ValueError("only regular nodes can be hot spares")
        for address in (node["backend"], node["control"]):
            port = endpoint(address)
            if port in ports[node["host"]]:
                raise ValueError("node port collision")
            ports[node["host"]].add(port)
    if all(node["role"] != "regular" or node.get("spare") for node in nodes):
        raise ValueError("deployment requires a regular node that is not a hot spare")
    # The world converter seeds catalog slots 1 and 2; a spare holding one would load seeded partitions at its start.
    if max((n["catalogSlot"] for n in nodes if n["role"] == "regular" and not n.get("spare")), default=0) \
            > min((n["catalogSlot"] for n in nodes if n.get("spare")), default=65536):
        raise ValueError("hot spares need catalog slots after every serving regular node")
    # Client frontends. Steam is the production frontend. directListen enables the Direct Transport
    # frontend, which admits every client (development and test only): the Gateway refuses to bind
    # it to a publicly reachable interface. Either may be left out, not both.
    steam_listen, direct_listen = spec.get("steamListen"), spec.get("directListen")
    if not steam_listen and not direct_listen:
        raise ValueError("deployment requires a client frontend: steamListen or directListen")
    for address in (spec["gatewayControl"], steam_listen, direct_listen):
        if not address:
            continue
        port = endpoint(address)
        if port in ports[spec["gatewayHost"]]:
            raise ValueError("gateway port collision")
        ports[spec["gatewayHost"]].add(port)
    node_gc = NODE_GC_ENVIRONMENT.get(spec.get("nodeGarbageCollector", "server"))
    if node_gc is None:
        raise ValueError("nodeGarbageCollector must be 'server' or 'workstation'")
    # Shown to players only; worldId stays the identity. Specs without them keep the old cfg and admission.
    world_name = spec.get("worldName") or spec["worldId"]
    motd = {tag: spec.get(key) or "" for tag, key in
            (("MessageOfTheDay", "messageOfTheDay"), ("MessageOfTheDayUrl", "messageOfTheDayUrl"))}
    if not isinstance(world_name, str) or not all(isinstance(value, str) for value in motd.values()):
        raise ValueError("worldName, messageOfTheDay and messageOfTheDayUrl must be strings")
    if inventory(world) != spec["worldFiles"] or "Sandbox.sbc" not in spec["worldFiles"]:
        raise ValueError("world seed hash mismatch")
    run = Path(hosts[host_id]["runRoot"])
    if not run.is_absolute() or run == root or run.is_relative_to(root) or destination.is_relative_to(run) or run.is_relative_to(destination):
        raise ValueError("runtime, artifacts and generated configuration must be separate absolute directories")
    plain_path(run)
    if mod_payload is not None:
        mod_payload = plain_path(mod_payload)
        if inventory(mod_payload) != spec.get("modFiles"):
            raise ValueError("mod payload hash mismatch")
    elif spec.get("modFiles") is not None:
        raise ValueError("mod payload is missing")
    join_ref = variable(spec["joinTokenEnvironmentVariable"])
    admin_ref = variable(spec["adminTokenEnvironmentVariable"])
    tokens_ref = variable(spec["adminTokensFileEnvironmentVariable"])
    variable(hosts[host_id]["executorTokenEnvironmentVariable"])
    revision = hashlib.sha256((expected_sha + digest(root / "inputs.json")).encode()).hexdigest()
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".deployment-", dir=destination.parent))
    try:
        shutil.copytree(world, staging / "seed/world")
        if inventory(staging / "seed/world") != spec["worldFiles"]:
            raise ValueError("world seed changed during copy")
        if "profileSettingsXml" in spec:
            apply_profile_patch(staging / "seed/world", spec["profileSettingsXml"])
            if mod_payload is None and (staging / "seed/world/Mods").exists():
                shutil.rmtree(staging / "seed/world/Mods")
        if mod_payload is not None:
            if (staging / "seed/world/Mods").exists():
                shutil.rmtree(staging / "seed/world/Mods")
            shutil.copytree(mod_payload, staging / "seed/world/Mods")
        mods = staged_world_mods(staging / "seed/world")
        checkpoint = ET.parse(staging / "seed/world/Sandbox.sbc")
        sync_distance = next(float(element.text) for element in checkpoint.iter()
                             if element.tag.rsplit("}", 1)[-1] == "SyncDistance")
        if not 0 < sync_distance <= 1000000:
            raise ValueError("invalid world SyncDistance")
        seeds = {"world": "seed/world"}
        ds = root / "Dependencies/payload/DedicatedServer/DedicatedServer64"
        replication_hash = hashlib.sha256(b"".join((ds / assembly).read_bytes() for assembly in
            ("Sandbox.Common.dll", "Sandbox.Game.dll", "SpaceEngineers.Game.dll", "SpaceEngineers.ObjectBuilders.dll", "VRage.dll", "VRage.Game.dll", "VRage.Library.dll"))).hexdigest().upper()
        environment = {"CLUSTER_ID": spec["clusterId"], "CLUSTER_WORLD_ID": spec["worldId"],
            "CLUSTER_SAVE_FOLDER": spec["worldId"],
            "CLUSTER_CONFIG_HASH": revision, "CLUSTER_WORLD": str(run / "world"),
            "CLUSTER_SYNC_DISTANCE": str(sync_distance), "CLUSTER_PROJECTILE_DISTANCE_CAP": str(sync_distance),
            "SPACE_ENGINEERS_BIN64": str(ds), "SPACE_ENGINEERS_ROOT": str(ds.parent),
            "CLUSTER_INTERNAL_NETWORKS": ",".join(spec["internalNetworks"])}
        # PluginSdk's PluginStorage (T-0256). Optional: the nodes span hosts, so only the operator knows a
        # path that is the same shared mount on every one. Without it, plugins asking for shared storage
        # get an error instead of a directory only their own node sees.
        if spec.get("sharedStorageRoot"):
            shared = Path(spec["sharedStorageRoot"])
            if not shared.is_absolute():
                raise ValueError("sharedStorageRoot must be an absolute path")
            plain_path(shared)
            environment["CLUSTER_SHARED_ROOT"] = str(shared)
        for network in spec["internalNetworks"]:
            ipaddress.ip_network(network)
        descriptions = []
        for node in nodes:
            if node["host"] != host_id:
                continue
            key = node["slotKey"]
            slot_dir = run / "nodes" / hashlib.sha256(key.encode()).hexdigest()[:24]
            config_relative = f"config/{key}"
            config = staging / config_relative
            role = "wa" if node["role"] == "WA" else "node"
            magnetar_config.write_config(config, role=role, binaries=root / "Package/plugins",
                transport_binaries=root / "Dependencies/payload/DirectTransport",
                common_plugins=root / "Dependencies/payload/CommonPlugins")
            entries = []
            for folder in sorted((config / "Local").iterdir()):
                if not folder.is_dir():
                    continue
                xml = ET.parse(folder / (folder.name + ".xml"))
                plugin_id = xml.getroot().findtext("Id")
                values = spec.get("pluginConfigurations", {}).get(plugin_id)
                if values is None and (folder / "canonical-configuration.json").is_file():
                    # A bundled plugin outside the profile, like Concealment, ships its defaults (SE-1078).
                    values = read_json(folder / "canonical-configuration.json")
                values = plugin_configuration(values or {})
                if plugin_id == PERFORMANCE_ID:
                    values = shared_host_havok(values, sum(n["host"] == host_id for n in nodes), physical_cores())
                entries.append({"id": plugin_id, "assemblySha256": digest(folder / (folder.name + ".dll")), **values})
            canonical = config / "canonical.json"
            write_json(canonical, {"schemaVersion": 2 if any("configurations" in entry for entry in entries) else 1,
                "revision": revision, "plugins": entries})
            seed = staging / "seed" / key
            # The local loader reads data/Mods, not the checkpoint folder's.
            shutil.copytree(staging / "seed/world", seed / "premade-world",
                            ignore=lambda folder, names: ["Mods"] if Path(folder) == staging / "seed/world" else [])
            if mods:
                # #1397: one inode per mod file across the node seeds of this private staging tree; Quasar
                # replaces each with a link to its content store when it adopts the configuration anyway.
                shutil.copytree(staging / "seed/world/Mods", seed / "Mods", copy_function=link_or_copy)
            else:
                (seed / "Mods").mkdir()
            (seed / "Mods/.quasar-profile-mods").unlink(missing_ok=True)  # never write through a shared link
            (seed / "Mods/.quasar-profile-mods").write_text(revision)
            cfg = ET.Element("MyConfigDedicated")
            for tag, value in {"LoadWorld": "", "IP": "0.0.0.0", "SteamPort": "8766",
                "ServerPort": str(endpoint(node["backend"])), "ServerName": spec["serverName"],
                "WorldName": world_name, "PauseGameWhenEmpty": "false", "AutoRestartEnabled": "false",
                "AutoUpdateEnabled": "false", "IgnoreLastSession": "true", "RemoteApiEnabled": "false",
                "AutodetectDependencies": "true", "NetworkType": "steam",
                "PremadeCheckpointPath": str(slot_dir / "data/premade-world"),
                **{tag: value for tag, value in motd.items() if value}}.items():
                ET.SubElement(cfg, tag).text = value
            admins = ET.SubElement(cfg, "Administrators")  # vanilla reads only <unsignedLong> items (#1212)
            for admin in spec["administrators"]:
                ET.SubElement(admins, "unsignedLong").text = str(int(admin))
            ET.ElementTree(cfg).write(seed / "SpaceEngineers-Dedicated.cfg", encoding="utf-8", xml_declaration=True)
            seeds[str((slot_dir / "data").relative_to(run))] = f"seed/{key}"
            descriptions.append({"slotKey": key, "nodeId": node["nodeId"],
                "role": "WorldAuthority" if role == "wa" else "Regular",
                "executable": "Dependencies/payload/Magnetar/MagnetarInterim.bin", "workingDirectory": "{runDirectory}",
                "configurationSeed": config_relative,
                "arguments": ["-config", str(slot_dir / "config"), "-ds64", str(ds),
                    "-profile", magnetar_config.PROFILES[role], "-noupdate", "-multiInstance", "-noconsent",
                    "-noconsole", "-ignorelastsession", "-port", str(endpoint(node["backend"])),
                    "-path", str(slot_dir / "data")],
                "environment": {**environment, **node_gc, "SE_DIRECT_TRANSPORT": "1", "CLUSTER_NODE_ENDPOINT": node["backend"],
                    "CLUSTER_CONTROL_BIND": node["control"].rpartition(":")[0].strip("[]"),
                    "CLUSTER_CONTROL_PORT": str(endpoint(node["control"])), "CLUSTER_NODE_EPOCH": str(node["catalogSlot"]),
                    "CLUSTER_DATA_ROOT": str(slot_dir / "data"), "CLUSTER_PLUGIN_CONFIGURATION": str(destination / config_relative / "canonical.json"),
                    "CLUSTER_PLUGIN_CONFIGURATION_SHA256": digest(canonical)},
                "secretEnvironment": {"CLUSTER_JOIN_TOKEN": join_ref, "CLUSTER_ADMIN_TOKEN": admin_ref},
                "reservedPorts": [endpoint(node["backend"]), endpoint(node["control"])], "readyTimeoutSeconds": 600})
        gateway = None
        if host_id == spec["gatewayHost"]:
            write_json(staging / "managed.json", {"revision": revision, "slots": [
                {"slotKey": n["slotKey"], "host": n["host"], "node": n["nodeId"], "endpoint": n["backend"],
                 "role": "WA" if n["role"] == "WA" else "Regular", **({"spare": True} if n.get("spare") else {})}
                for n in nodes]})
            # The browser row's world fields (#1529): without them the Gateway advertised its defaults, gamemodeS1-1-1-1
            # view3000 mods0 modsTotalSize0, whatever the world ran. The Gateway sums the mods' Workshop sizes itself.
            write_json(staging / "admission.json", {"serverName": spec["serverName"], "worldName": world_name,
                "gameMode": game_mode_tag(checkpoint), "syncDistance": sync_distance,
                "modCount": len(mods), "modIds": [int(mod) for mod in mods],
                "memberLimit": spec["memberLimit"], "administrators": spec["administrators"], "familySharing": True})
            gateway = {"executable": "Package/registry/ClusterRegistry", "workingDirectory": "{runRoot}",
                "reservedPorts": [endpoint(address) for address in (spec["gatewayControl"], steam_listen, direct_listen) if address],
                "arguments": (["--steam", steam_listen] if steam_listen else [])
                    + (["--direct", "--listen", direct_listen] if direct_listen else [])
                    + ["--control", spec["gatewayControl"],
                    "--backend", next(n["backend"] for n in nodes if n["role"] == "regular"),
                    "--registry", str(run / "gateway/registry"), "--admission", str(destination / "admission.json"),
                    "--export-root", str(run / "gateway/exports"), "--lease-ttl-seconds", "15"],
                "environment": {**environment, "CLUSTER_MANAGED_DEPLOYMENT": str(destination / "managed.json"),
                    "CLUSTER_DIRECT": "true" if direct_listen else "false", "CLUSTER_PAUSED": "false",
                    "CLUSTER_ALLOW_TEST_ACCEPT_ALL": "true" if direct_listen else "false",
                    "CLUSTER_BINARY_VERSION": spec["binaryVersion"], "CLUSTER_REPLICATION_TABLE_HASH": replication_hash},
                "secretEnvironment": {"CLUSTER_JOIN_TOKEN_CURRENT": join_ref, "CLUSTER_ADMIN_TOKEN": admin_ref,
                    "CLUSTER_ADMIN_TOKENS": tokens_ref}}
            export_roots = spec.get("exportDataRoots")
            if export_roots is None and len(hosts) == 1:
                export_roots = [str(run / "nodes" / hashlib.sha256(n["slotKey"].encode()).hexdigest()[:24] / "data") for n in nodes]
            if export_roots:
                if not isinstance(export_roots, list) or any(not Path(p).is_absolute() or "," in p for p in export_roots):
                    raise ValueError("exportDataRoots must contain absolute Gateway-visible paths")
                gateway["arguments"] += ["--world", str(run / "world"), "--export-data-roots", ",".join(export_roots)]
                gateway["environment"]["MAGNETAR_WORLD_TOOL"] = str(root / "Package/tools/MagnetarWorld/MagnetarWorld.dll")
        # Local manifests contain absolute asset paths; publish their final location before hashing.
        # The copies keep the installation's read-only mode, so write a new file instead.
        for path in (staging / "config").rglob("*.xml"):
            text = path.read_text().replace(str(staging), str(destination))
            path.unlink()
            path.write_text(text)
        bundle = {"schemaVersion": 1, "revision": revision, "artifactRoot": str(root),
            "clusterId": spec["clusterId"], "hostId": host_id,
            "runtimeRoot": str(run), "initialDirectories": seeds,
            "profileContent": {"settingsSeed": "seed/world/Sandbox.sbc", "modIds": mods,
                               "nodeModSeeds": {node["slotKey"]: f"seed/{node['slotKey']}/Mods"
                                                for node in nodes if node["host"] == host_id}},
            "storageFormats": read_json(Path(__file__).with_name("deployment-capabilities.json"))["storageFormats"],
            "files": [{"path": file, "sha256": pin["sha256"]} for file, pin in pins["files"].items()],
            "configFiles": [{"path": file, "sha256": sha} for file, sha in inventory(staging).items()],
            "nodes": descriptions, "gateway": gateway, "requiredHosts": sorted(hosts)}
        write_json(staging / "bundle.json", bundle)
        if destination.exists():
            if inventory(staging) != inventory(destination):
                raise ValueError("prepared deployment differs from approved inputs")
        else:
            staging.rename(destination)
        return {"manifest": str(destination / "bundle.json"), "sha256": digest(destination / "bundle.json"), "revision": revision}
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--installation", required=True, type=Path)
    commands = parser.add_subparsers(dest="command", required=True)
    conversion = commands.add_parser("convert")
    conversion.add_argument("--source", required=True, type=Path)
    conversion.add_argument("--destination", required=True, type=Path)
    preparing = commands.add_parser("prepare")
    preparing.add_argument("--spec", required=True, type=Path)
    preparing.add_argument("--sha256", required=True)
    preparing.add_argument("--host", required=True)
    preparing.add_argument("--world", required=True, type=Path)
    preparing.add_argument("--mods", type=Path)
    preparing.add_argument("--destination", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "convert":
        result = convert(args.installation, args.source, args.destination)
    else:
        result = prepare(args.installation, args.spec, args.sha256, args.host, args.world, args.destination, args.mods)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
