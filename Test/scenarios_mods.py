"""T-0290: every cluster process loads the world's Workshop mods from its own pre-staged
<data>/Mods/<id>.sbm folder and never downloads them (LocalWorldModsPatch).

The fixture is a probe mod with a made-up Workshop id: a session component that logs its ModPath. It
is staged into every process's Mods folder between prepare and start, and a copy of the packaged world
lists it. That id does not exist on the Workshop, so a vanilla DS could only load it by downloading
it, which fails. A load that shows the probe line proves the local folder is used.
Red: the same world with node-2's folder not staged. The load must FAIL on node-2 and name the folder,
and must not download.
"""
from __future__ import annotations

import base64
import gzip
import json
import re
import shutil
import socket
import time
from collections import Counter
from pathlib import Path

import bench as benchmod
from cluster import WORLD_ID, wait_until

PROBE_ID = 9990001
PROBE_NAME = f"{PROBE_ID}.sbm"
PROBE_LINE = "cluster_local_mod_probe loaded from "
PROBE_CS = """using VRage.Game.Components;
using VRage.Utils;

namespace ClusterLocalModProbe
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        public override void LoadData()
        {
            MyLog.Default.WriteLineAndConsole("cluster_local_mod_probe loaded from " + ModContext.ModPath);
        }
    }
}
"""
MOD_ITEM = (f'<Mods>\n    <ModItem FriendlyName="Cluster local mod probe">\n      <Name>{PROBE_NAME}</Name>\n'
            f'      <PublishedFileId>{PROBE_ID}</PublishedFileId>\n'
            f'      <PublishedServiceName>Steam</PublishedServiceName>\n    </ModItem>\n  </Mods>')


def _template(bench) -> Path:
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-Simple-probe-mod"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        text = path.read_text()
        new, n = re.subn(r"<Mods\s*/>", MOD_ITEM, text, count=1)
        if n != 1:
            raise AssertionError(f"{name}: no empty <Mods /> to replace")
        path.write_text(new)
    return target


def _stage(bench, processes) -> list[str]:
    staged = []
    for process in processes:
        folder = bench.run_root / f"{process}-data" / "Mods" / PROBE_NAME / "Data" / "Scripts" / "ClusterLocalModProbe"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "Probe.cs").write_text(PROBE_CS)
        staged.append(str(folder))
    return staged


def _log(bench, process) -> str:
    logs = sorted((bench.run_root / f"{process}-data").glob("SpaceEngineersDedicated_*.log"))
    return logs[-1].read_text(errors="replace") if logs else ""


def _run(bench, skip: str | None):
    bench.cluster.config.world_template = _template(bench)
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    processes = ["wa-1", "node-1", "node-2"]
    bench.record("staged", _stage(bench, [p for p in processes if p != skip]))
    error = None
    try:
        bench.cluster.start()
        bench.cluster.wait_ready()
    except Exception as e:  # noqa: BLE001 - the red expects a failed start
        error = f"{type(e).__name__}: {e}"
    time.sleep(2)
    logs = {p: _log(bench, p) for p in processes}
    for p, text in logs.items():
        lines = [line for line in text.splitlines() if "cluster_local_world_mods" in line or "cluster_local_mod_probe" in line
                 or "Downloading world mods" in line]
        bench.record(f"log-{p}.txt", "\n".join(lines))
    return logs, error


def p1_mod_local(bench) -> None:
    """Each process loads the world's mods from its own staged folder; nothing is downloaded."""
    logs, error = _run(bench, None)
    bench.check("the cluster started with the probe mod listed", error is None, str(error))
    for p, text in logs.items():
        bench.check(f"{p}: the patch took the local path", "cluster_local_world_mods ok: 1 mod(s)" in text)
        m = re.search(re.escape(PROBE_LINE) + r"(\S+)", text)
        bench.check(f"{p}: the probe mod ran from its own <data>/Mods folder",
                    bool(m) and f"{p}-data" in m.group(1) and m.group(1).rstrip("/\\").endswith(PROBE_NAME),
                    m.group(1) if m else "no probe line")
        bench.check(f"{p}: no Workshop download started", "Downloading world mods - START" not in text)
    bench.exercised(mod=PROBE_ID, processes=list(logs))


def p1_mod_local_red(bench) -> None:
    """The earned red: node-2's folder is not staged. node-2's load must fail and name the folder;
    no process downloads, and the staged processes still load the probe."""
    logs, error = _run(bench, "node-2")
    bench.check("the start did not complete (node-2 cannot load the world)", error is not None, str(error))
    bench.check("node-2 failed the load on the missing folder", "cluster_local_world_mods FAILED: 1 of 1" in logs["node-2"]
                and PROBE_NAME in logs["node-2"])
    bench.check("node-2 did not download", "Downloading world mods - START" not in logs["node-2"])
    bench.check("wa-1 (staged) still loaded the probe", PROBE_LINE in logs["wa-1"])
    bench.exercised(mod=PROBE_ID, missing="node-2")


def _a2s_rules(address: str) -> dict[str, str]:
    """The A2S_RULES answer of the Steam frontend, as the client's server browser reads it."""
    host, _, port = address.rpartition(":")
    target = ("127.0.0.1" if host == "0.0.0.0" else host, int(port))
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.settimeout(2)
        s.sendto(b"\xff\xff\xff\xffV\xff\xff\xff\xff", target)
        data = s.recv(65535)
        if data[4:5] == b"A":  # challenge
            s.sendto(b"\xff\xff\xff\xffV" + data[5:9], target)
            data = s.recv(65535)
        if data[:4] == b"\xfe\xff\xff\xff":  # split answer: id, total, number, size, then the payload
            parts = {data[9]: data[12:]}
            while len(parts) < data[8]:
                data = s.recv(65535)
                parts[data[9]] = data[12:]
            data = b"".join(parts[i] for i in sorted(parts))
    if data[4:5] != b"E":
        raise AssertionError(f"not an A2S_RULES answer: {data[:8]!r}")
    items = data[7:].split(b"\0")
    count = int.from_bytes(data[5:7], "little")
    return {items[2 * i].decode(errors="replace"): items[2 * i + 1].decode(errors="replace") for i in range(count)}


def _protobuf(raw: bytes) -> dict[int, list]:
    """Field number -> values (varints as int, length-delimited as bytes) of one protobuf message."""
    fields: dict[int, list] = {}
    i = 0

    def varint() -> int:
        nonlocal i
        value = shift = 0
        while True:
            byte = raw[i]
            i += 1
            value |= (byte & 0x7F) << shift
            shift += 7
            if byte < 0x80:
                return value

    while i < len(raw):
        key = varint()
        kind = key & 7
        if kind == 0:
            value = varint()
        elif kind == 2:
            size = varint()
            value, i = raw[i:i + size], i + size
        elif kind in (1, 5):
            size = 8 if kind == 1 else 4
            value, i = raw[i:i + size], i + size
        else:
            raise AssertionError(f"wire type {kind}")
        fields.setdefault(key >> 3, []).append(value)
    return fields


def _server_details(rules: dict[str, str]) -> dict[int, list]:
    """MyCachedServerItem.DeserializeSettings: "sc" bytes in 93-byte base64 chunks of MyCompression (length
    prefix + gzip) around the protobuf MyServerData (1 Settings, 7 Mods, 10 Description, 14 UsedServices)."""
    size = int(rules["sc"])
    blob = b"".join(base64.b64decode(rules[f"sc{i}"]) for i in range((size + 92) // 93))
    if len(blob) != size:
        raise AssertionError(f"sc says {size} bytes, the chunks hold {len(blob)}")
    return _protobuf(gzip.decompress(blob[4:]))


def p1_server_details(bench) -> None:
    """SE-1076: the Steam frontend publishes the "sc" rules a vanilla DS sets, so the client's server
    details list the world's mods and settings. The blob comes from a node's session via the heartbeat."""
    config = bench.cluster.config
    config.steam_listen = f"127.0.0.1:{27016 + config.port_base}"
    _run(bench, None)
    rules: dict[str, str] = {}

    def published() -> bool:
        nonlocal rules
        try:
            rules = _a2s_rules(config.steam_listen)
        except OSError:
            return False
        return "sc" in rules

    try:
        wait_until(published, 90, "the sc rules on the Steam frontend", interval=2)
    except Exception:  # noqa: BLE001 - the check below reports it
        pass
    bench.record("a2s-rules", rules)
    bench.check("the Steam frontend publishes the sc rules", "sc" in rules, ", ".join(sorted(rules)))
    if "sc" not in rules:
        return
    details = _server_details(rules)
    mods = [_protobuf(item) for item in details.get(7, [])]
    listed = [(mod.get(1, [0])[0], mod.get(4, [b""])[0].decode()) for mod in mods]
    bench.record("server-details", {"mods": listed, "settingsBytes": len(details.get(1, [b""])[0]),
                                    "usedServices": [s.decode() for s in details.get(14, [])]})
    bench.check("the server details list the world's mod", listed == [(PROBE_ID, "Steam")], str(listed))
    bench.check("the server details carry the world settings", len(details.get(1, [b""])[0]) > 100)
    gateway = (bench.run_root / "gateway.log").read_text(errors="replace")
    bench.check("the Gateway took the blob from a node", "published as the server details' sc rules" in gateway)
    bench.exercised(mod=PROBE_ID, steam=config.steam_listen, rules=len(rules))


def p1_mod_storage_00(bench) -> None:
    """Tier 1: the handover gate's normalisation self-test, incl. the mod storage key (w33 2026-09-29):
    a mod storage component keyed by its base type on the source and by its own type after the import is
    no residual; changed storage data still is."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "round-trip normalisation incl. the mod storage key", "round-trip-normalize")


# T-0242 (#260): mod classification, placement and the per-mod load record. Three probe mods with made-up
# Workshop ids, staged like the T-0290 probe, and an admin placement file in every process's Mods folder.
COMPAT_PROBES = {
    # session-only world state (SetVariable): automatic C, WA-only, so its session component runs on wa-1 only
    9990011: ("CompatC", """using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;

namespace CompatC
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        public override void LoadData()
        {
            MyAPIGateway.Utilities.SetVariable("cluster-compat-probe", 1);
            MyLog.Default.WriteLineAndConsole("cluster_mod_compat_probe 9990011 session component loaded");
        }
    }
}
"""),
    # writes local storage at load: automatic B; the admin file says D, so the admin decides (adapted)
    9990012: ("CompatAdmin", """using Sandbox.ModAPI;
using VRage.Game.Components;

namespace CompatAdmin
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        public override void LoadData()
        {
            using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage("probe.txt", typeof(Probe)))
                writer.Write("written");
        }
    }
}
"""),
    # world scan without the channel: automatic D everywhere; the author's constant declares it adapted
    9990013: ("CompatAuthor", """using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;

namespace CompatAuthor
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        public const string ClusterModCompat = "D adapted";

        public override void BeforeStart()
        {
            var found = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(found);
        }
    }
}
"""),
}
COMPAT_PLACEMENT = "# admin placement (P1-MOD-COMPAT)\n9990012 D\n9990099\n"


def _compat_template(bench) -> Path:
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-Simple-compat-mods"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    items = "".join(f'    <ModItem FriendlyName="{name}">\n      <Name>{mod}.sbm</Name>\n'
                    f'      <PublishedFileId>{mod}</PublishedFileId>\n'
                    f'      <PublishedServiceName>Steam</PublishedServiceName>\n    </ModItem>\n'
                    for mod, (name, _) in COMPAT_PROBES.items())
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        new, n = re.subn(r"<Mods\s*/>", f"<Mods>\n{items}  </Mods>", path.read_text(), count=1)
        if n != 1:
            raise AssertionError(f"{name}: no empty <Mods /> to replace")
        path.write_text(new)
    return target


def _compat_records(text: str) -> dict:
    records = {}
    for line in text.splitlines():
        m = re.search(r"cluster_mod_compat_json (\{.*\})\s*$", line)
        if m:
            record = json.loads(m.group(1))
            records[record["mod"]["id"]] = record
    return records


def p1_mod_compat(bench) -> None:
    """T-0242 (#260): every process writes one record per mod with its role, final class and placement, the
    deciding source and the automatic evidence; the WA-only probe's session component is registered on wa-1
    only; the admin and author overrides win in that order and each disagreement is a WARN line; a mod file
    write on a regular node is counted, logged and served at /mod-compat."""
    bench.cluster.config.world_template = _compat_template(bench)
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    processes = ["wa-1", "node-1", "node-2"]
    for process in processes:
        mods = bench.run_root / f"{process}-data" / "Mods"
        for mod, (name, code) in COMPAT_PROBES.items():
            folder = mods / f"{mod}.sbm" / "Data" / "Scripts" / name
            folder.mkdir(parents=True, exist_ok=True)
            (folder / "Probe.cs").write_text(code)
        (mods / "cluster-mod-placement.txt").write_text(COMPAT_PLACEMENT)
    bench.cluster.start()
    bench.cluster.wait_ready()
    time.sleep(2)
    for process in processes:
        text = _log(bench, process)
        bench.record(f"log-{process}.txt", "\n".join(line for line in text.splitlines() if "cluster_mod_" in line))
        records = _compat_records(text)
        role = "WA" if process == "wa-1" else "Regular"
        bench.check(f"{process}: one record per probe mod", sorted(records) == sorted(str(m) for m in COMPAT_PROBES),
                    str(sorted(records)))
        c, admin, author = (records.get(str(m), {}) for m in COMPAT_PROBES)
        bench.check(f"{process}: records carry the role {role}", all(r.get("role") == role for r in (c, admin, author)))
        here = "wa-only:this-node-is-wa" if role == "WA" else "wa-only:skipped-here"
        bench.check(f"{process}: SetVariable probe is automatic C, wa-only, {here}",
                    (c.get("class"), c.get("placement"), c.get("here"), c.get("decidedBy"), c.get("automatic", {}).get("class"))
                    == ("C", "wa-only", here, "automatic", "C"), json.dumps(c)[:400])
        bench.check(f"{process}: evidence names the SetVariable call site",
                    c.get("automatic", {}).get("groups", {}).get("set-variable", {}).get("firstSite") == "CompatC.Probe.LoadData")
        loaded = "cluster_mod_compat_probe 9990011 session component loaded" in text
        bench.check(f"{process}: the WA-only session component {'ran' if role == 'WA' else 'was not registered'}",
                    loaded == (role == "WA"))
        bench.check(f"{process}: admin override decides 9990012 (automatic B -> admin D adapted)",
                    (admin.get("decidedBy"), admin.get("class"), admin.get("placement"), admin.get("automatic", {}).get("class"))
                    == ("admin", "D", "adapted", "B") and "cluster-mod-placement.txt:2" in str(admin.get("admin")),
                    json.dumps(admin.get("admin")))
        bench.check(f"{process}: author constant decides 9990013 (automatic D everywhere -> author D adapted)",
                    (author.get("decidedBy"), author.get("placement"), author.get("automatic", {}).get("placement"), author.get("admin"))
                    == ("author", "adapted", "everywhere", "NONE"), json.dumps(author.get("author")))
        bench.check(f"{process}: a WARN line per disagreement and for the bad placement line",
                    "WARN cluster_mod_compat mod=9990012 disagreement automatic=B/everywhere admin=D/adapted" in text
                    and "WARN cluster_mod_compat mod=9990013 disagreement automatic=D/everywhere author=D/adapted" in text
                    and "ignored (no verdict): 9990099" in text)
        bench.check(f"{process}: storage tier with this node's absolute path",
                    str(admin.get("storage", {}).get("local", {}).get("path", "")).startswith(str(bench.run_root / f"{process}-data")),
                    json.dumps(admin.get("storage")))
        status = bench.cluster.node(process, "/mod-compat")
        writes = {(w["mod"], w["tier"]): w["count"] for w in status.get("storageWrites", [])}
        bench.check(f"{process}: /mod-compat serves the records and counts the local-storage write",
                    status.get("role") == role and len(status.get("mods", [])) == 3 and writes.get(("9990012", "local"), 0) >= 1,
                    json.dumps(status.get("storageWrites")))
        if role == "Regular":
            bench.check(f"{process}: the node-side write is a WARN line", "WARN cluster_mod_storage_write mod=9990012 tier=local" in text)
    bench.exercised(mods=list(COMPAT_PROBES), processes=processes)


# SE-788: the world's save folder doesn't follow its name, or the mods' configs seeded for it are never read.
WORLD_NAME = "World Name Rig"
STORAGE_PROBE_ID = 9990031
STORAGE_PROBE = """using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;

namespace StorageProbe
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class Probe : MySessionComponentBase
    {
        public override void LoadData()
        {
            string limit = "140";
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage("limit.cfg", typeof(Probe)))
                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage("limit.cfg", typeof(Probe)))
                    limit = reader.ReadToEnd();
            else
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage("limit.cfg", typeof(Probe)))
                    writer.Write(limit);
            MyLog.Default.WriteLineAndConsole("cluster_world_storage_probe limit=" + limit);
        }
    }
}
"""
# The real mod when this host has it: its limit is a game value a node really runs with.
RTS_ID = 1359618037
RTS_SOURCE = Path.home() / ".steam/steam/steamapps/workshop/content/244850" / str(RTS_ID)
RTS_CONFIG = f"{RTS_ID}.sbm_RelativeTopSpeed/RelativeTopSpeed.cfg"
STORAGE_PROBE_CONFIG = f"{STORAGE_PROBE_ID}.sbm_StorageProbe/limit.cfg"


def _world_name_template(bench) -> Path:
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / WORLD_NAME
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    mods = [STORAGE_PROBE_ID] + ([RTS_ID] if RTS_SOURCE.is_dir() else [])
    items = "".join(f'    <ModItem FriendlyName="{mod}">\n      <Name>{mod}.sbm</Name>\n'
                    f'      <PublishedFileId>{mod}</PublishedFileId>\n'
                    f'      <PublishedServiceName>Steam</PublishedServiceName>\n    </ModItem>\n' for mod in mods)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        new, n = re.subn(r"<Mods\s*/>", f"<Mods>\n{items}  </Mods>", path.read_text(), count=1)
        if n != 1:
            raise AssertionError(f"{name}: no empty <Mods /> to replace")
        path.write_text(new)
    return target


def _world_name_start(bench, processes: list[str], seed: dict[str, str] | None = None) -> None:
    """A fresh cluster with the mods staged and, as Quasar's ModStorageSeed does, the seed in Saves/<world id>."""
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    for process in processes:
        data = bench.run_root / f"{process}-data"
        folder = data / "Mods" / f"{STORAGE_PROBE_ID}.sbm" / "Data" / "Scripts" / "StorageProbe"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "Probe.cs").write_text(STORAGE_PROBE)
        if RTS_SOURCE.is_dir():
            shutil.copytree(RTS_SOURCE, data / "Mods" / f"{RTS_ID}.sbm", dirs_exist_ok=True)
        for relative, content in (seed or {}).items():
            path = data / "Saves" / WORLD_ID / "Storage" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
    bench.cluster.start()
    bench.cluster.wait_ready()
    time.sleep(5)


def _rts_limit(bench, process: str) -> float:
    return bench.cluster.node(process, "/fast-test-speed-limit?mps=50", "POST")["before"]["large"]


def p1_world_name_save_folder(bench) -> None:
    """SE-788: a world name other than the world id. Unset CLUSTER_SAVE_FOLDER shows the defect: the save follows
    the name and the mods write their defaults there. Set, as managed deployments do, the save stays in
    Saves/<world id>, the mods run with the configs seeded there and players still see the world's name."""
    bench.cluster.config.world_template = _world_name_template(bench)
    processes = ["wa-1", "node-1", "node-2"]
    _world_name_start(bench, processes)
    # The scanner places the storage-writing probe on the WA only; Relative Top Speed runs everywhere.
    probe = bench.run_root / "wa-1-data" / "Saves" / WORLD_NAME / "Storage" / STORAGE_PROBE_CONFIG
    bench.check("no save folder set: the mods write their defaults under the world's name",
                probe.exists() and probe.read_text() == "140", str(probe))
    storage = bench.run_root / "node-1-data" / "Saves" / WORLD_NAME / "Storage"
    seed = {STORAGE_PROBE_CONFIG: "360"}
    if RTS_SOURCE.is_dir():
        default = (storage / RTS_CONFIG).read_text()
        bench.check("no save folder set: Relative Top Speed runs at its default", _rts_limit(bench, "node-1") == 140)
        seed[RTS_CONFIG] = default.replace("<SpeedLimit>140<", "<SpeedLimit>360<").replace(
            "<RemoteControlSpeedLimit>100<", "<RemoteControlSpeedLimit>360<")
        bench.check("the seed differs from the default", "<SpeedLimit>360<" in seed[RTS_CONFIG])

    bench.cluster.config.node_env["CLUSTER_SAVE_FOLDER"] = WORLD_ID
    _world_name_start(bench, processes, seed)
    for process in processes:
        saves = bench.run_root / f"{process}-data" / "Saves"
        text = _log(bench, process)
        bench.record(f"log-{process}.txt", "\n".join(line for line in text.splitlines() if "cluster_world_storage_probe" in line))
        bench.check(f"{process}: the save is in Saves/<world id>, none under the name",
                    (saves / WORLD_ID / "Sandbox.sbc").exists() and not (saves / WORLD_NAME).exists(),
                    str(sorted(p.name for p in saves.iterdir())))
        if process == "wa-1":
            bench.check(f"{process}: the probe mod read the seeded config", "cluster_world_storage_probe limit=360" in text)
        names = bench.cluster.node(process, "/world-name-test")
        bench.check(f"{process}: session and multiplayer keep the world's name",
                    (names["session"], names["multiplayer"]) == (WORLD_NAME, WORLD_NAME), json.dumps(names))
        if RTS_SOURCE.is_dir():
            bench.check(f"{process}: Relative Top Speed runs with the seeded limit", _rts_limit(bench, process) == 360)
            bench.check(f"{process}: the mod left the seeded config in place", "<SpeedLimit>360<" in (
                saves / WORLD_ID / "Storage" / RTS_CONFIG).read_text())
    bench.exercised(mods=[STORAGE_PROBE_ID], processes=processes, relativeTopSpeed=RTS_SOURCE.is_dir())


# #1280: the mod channel v1 primitives, through a real class D mod: fixture-d-scan adapted to the channel
# (Tools/ModCompatScan/TestMods/fixture-d-reference, the reference adaptation), staged like the probes above.
REFERENCE_ID = 9990021
REFERENCE_SOURCE = (Path(__file__).resolve().parent.parent
                    / "Tools/ModCompatScan/TestMods/fixture-d-reference/Data/Scripts/Main/Reference.cs")
REFERENCE_LINE = "cluster_mod_reference "


def _reference_template(bench) -> Path:
    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-Simple-reference-mod"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    item = (f'<Mods>\n    <ModItem FriendlyName="Cluster mod reference">\n      <Name>{REFERENCE_ID}.sbm</Name>\n'
            f'      <PublishedFileId>{REFERENCE_ID}</PublishedFileId>\n'
            f'      <PublishedServiceName>Steam</PublishedServiceName>\n    </ModItem>\n  </Mods>')
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        new, n = re.subn(r"<Mods\s*/>", item, path.read_text(), count=1)
        if n != 1:
            raise AssertionError(f"{name}: no empty <Mods /> to replace")
        path.write_text(new)
    return target


def _reference_events(bench, processes) -> list[dict]:
    """Every line the reference mod logged, from every game log of each process (a restart starts a new one):
    {process, event, raw, and the line's key=value fields}."""
    events = []
    for process in processes:
        for log in sorted((bench.run_root / f"{process}-data").glob("SpaceEngineersDedicated_*.log")):
            for line in log.read_text(errors="replace").splitlines():
                if REFERENCE_LINE not in line:
                    continue
                text = line[line.index(REFERENCE_LINE) + len(REFERENCE_LINE):]
                event, _, rest = text.partition(" ")
                events.append({"process": process, "event": event, "raw": text, **dict(re.findall(r"(\w+)=(\S*)", rest))})
    return events


def p1_mod_channel_00(bench) -> None:
    """Tier 1 (#1280): the mod channel v1's pure rules - which roster changes reset the mods, and the codec's
    to-owner entity id - in the node self-test."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "mod channel v1: roster resets and the to-owner codec rule", "mod-channel-v1")


def p1_mod_msg_server(bench) -> None:
    """Tier 1 (DF-671): a mod message a client sends to the server reaches the node's mods whatever server id
    the client addressed it to (a Steam client through gateway-rs knows the gateway's GameServer id), while
    vanilla's own recipient check still drops it - in the node self-test."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "mod messages to the server: any addressed id delivered, vanilla check still drops it",
                         "mod-message-to-server")


def p2_mod_v1(bench) -> None:
    """#1280: a class D mod adapted to the mod channel v1 acts exactly once cluster-wide. The reference mod runs on
    wa-1, node-1 and node-2: role query; the WA's census (broadcast, answered with to-node) and one decision per
    grid, acted on by the grid's owner only (to-owner); the owner followed across a clientless handover
    (owner-of) with the mod's in-memory state carried by the handover hooks; one greeting per online player on
    the node the player is attached to (player-nodes, to-node); a reset when node-2 restarts. Its #260 record says
    D adapted on every process."""
    from scenarios import _partition_of
    from scenarios_core import CLIENT_ID, _handover_url
    from scenarios_wa import SERVER_TIMEOUT, _poll, _post, _q, _require

    processes = ["wa-1", "node-1", "node-2"]
    bench.cluster.config.world_template = _reference_template(bench)
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    # Every process, and the client (which joins only with the world's mods present, from its own Mods folder).
    for mods in [bench.run_root / f"{process}-data" / "Mods" for process in processes] + [
            bench.run_root / f"client-{CLIENT_ID}" / "home/.config/SpaceEngineers/Mods"]:
        folder = mods / f"{REFERENCE_ID}.sbm" / "Data" / "Scripts" / "ClusterModReference"
        folder.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REFERENCE_SOURCE, folder / "Reference.cs")
    bench.cluster.start()
    bench.cluster.wait_ready()
    # The stations below must stay on the node that made them until the scenario moves one.
    bench.cluster.post("/automatic/pause")

    def events(name: str | None = None) -> list[dict]:
        return [e for e in _reference_events(bench, processes) if name is None or e["event"] == name]

    # Role query, answered inside the mod's first send.
    roles = _poll(lambda: {e["process"]: e for e in events("role")} if len(events("role")) >= 3 else None, 120)
    bench.record("roles", roles or events("role"))
    _require(bench, "the reference mod ran on every process and asked its role", roles and sorted(roles) == sorted(processes),
             json.dumps(events("role"))[:400])
    for process in processes:
        role = roles[process]
        bench.check(f"{process}: role answer inCluster=True role={'WA' if process == 'wa-1' else 'Regular'} node={process} api=1",
                    (role.get("inCluster"), role.get("role"), role.get("node"), role.get("api"))
                    == ("True", "WA" if process == "wa-1" else "Regular", process, "1"), role["raw"])

    # One station per regular node: the WA's census finds them, decides once each, and the owner acts.
    # Before this, the WA's first census can meet a node whose session is not up yet: a fault, as it should be.
    early_faults = Counter((e["process"], e["raw"]) for e in events("fault"))
    stations = {node: _post(bench, node, f"/igc-test-ensure-laser-at?{_q(name=f'mod-ref-{node}', x=880000, y=0, z=z)}")
                for node, z in (("node-1", 0), ("node-2", 30000))}
    bench.record("stations", stations)
    grids = {node: str(station.get("grid")) for node, station in stations.items()}
    _require(bench, "a station exists on node-1 and on node-2", all(g != "None" for g in grids.values()), json.dumps(stations))

    def acted():
        done = {e["grid"] for e in events("act")}
        return done if set(grids.values()) <= done else None
    _require(bench, "the WA decided both stations and their owners acted", _poll(acted, 120), json.dumps(events())[:600])
    # Three more census rounds: an adaptation that acted per node, or decided twice, would show it now.
    time.sleep(16)
    decides, acts = events("decide"), events("act")
    bench.record("decisions", {"decide": decides, "act": acts, "census-reports": [e["reportedBy"] for e in decides]})
    for node, grid in grids.items():
        mine = [e for e in decides if e["grid"] == grid]
        bench.check(f"station {grid} ({node}) decided exactly once, on wa-1",
                    len(mine) == 1 and mine[0]["process"] == "wa-1", json.dumps(mine))
        ops = {e["op"] for e in mine}
        done = [e for e in acts if e["grid"] == grid]
        bench.check(f"station {grid} acted on exactly once cluster-wide, by its owner {node}",
                    len(done) == 1 and done[0]["process"] == node and {e["op"] for e in done} == ops, json.dumps(done))
    owners = _poll(lambda: {e["grid"]: e["owner"] for e in events("owner")} if len(events("owner")) >= 2 else None, 30)
    bench.check("owner-of names each station's node", owners == {grid: node for node, grid in grids.items()}, json.dumps(owners))

    # A player: the WA lists it with its node and greets it once, there.
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)

    def listed():
        rows = [e for e in events("players") if f":{CLIENT_ID}@" in e["raw"]]
        return rows[-1] if rows else None
    players = _poll(listed, 60)
    greets = _poll(lambda: [e for e in events("greet") if e.get("player") == str(CLIENT_ID)] or None, 60) or []
    bench.record("players", {"listed": players, "greets": greets})
    attached = re.search(rf":{CLIENT_ID}@(\S+?)[,\]]", players["raw"]).group(1) if players else None
    _require(bench, "player-nodes lists the client with its node", attached in ("node-1", "node-2"), json.dumps(players))
    time.sleep(11)
    greets = [e for e in events("greet") if e.get("player") == str(CLIENT_ID)]
    bench.check(f"the player was greeted exactly once, on its node {attached}",
                len(greets) == 1 and greets[0]["process"] == attached and greets[0].get("from") == "wa-1", json.dumps(greets))

    # The node-1 station moves to node-2: its in-memory state rides the handover hooks, owner-of follows it, and the
    # WA's ping lands on node-2 only, where the state is.
    grid = grids["node-1"]
    op = next(e["op"] for e in events("act") if e["grid"] == grid)
    partition = _partition_of(bench.sections(bench.diag("entities"))["node-1"], int(grid))
    _require(bench, f"station {grid} has a node-1 partition", partition, str(partition))
    bench.record("handover", bench.cluster.post(_handover_url(bench, partition, clientless=True), timeout=300))

    def moved():
        names = {(e["process"], e["event"]) for e in events() if e.get("grid") == grid}
        return names if {("node-1", "before-handover"), ("node-2", "after-import"), ("node-1", "handover-left"),
                         ("wa-1", "owner")} <= names and any(e["owner"] == "node-2" for e in events("owner") if e["grid"] == grid) else None
    _poll(moved, 120)
    trail = [e for e in events() if e.get("grid") == grid]
    bench.record("handover-trail", trail)
    state = f"op{op}"
    bench.check("node-1 handed its in-memory state to the capture (before-handover)",
                any(e["process"] == "node-1" and e["event"] == "before-handover" and e.get("state") == state for e in trail))
    bench.check("node-2 got the state back after the import (after-import)",
                any(e["process"] == "node-2" and e["event"] == "after-import" and e.get("state") == state for e in trail))
    bench.check("node-1 was told the grid left (handover-left)",
                any(e["process"] == "node-1" and e["event"] == "handover-left" for e in trail))
    bench.check("owner-of follows the station to node-2",
                any(e["process"] == "wa-1" and e["event"] == "owner" and e.get("owner") == "node-2" and e.get("previous") == "node-1"
                    for e in trail))
    pings = _poll(lambda: [e for e in events("ping") if e["grid"] == grid] or None, 60) or []
    time.sleep(6)
    pings = [e for e in events("ping") if e["grid"] == grid]
    bench.check("the WA's ping reached the new owner only, with the carried state",
                len(pings) == 1 and pings[0]["process"] == "node-2" and pings[0].get("state") == state, json.dumps(pings))
    again = [e for e in events("act") if e["grid"] == grid]
    bench.check("the move did not make anyone act again", len(again) == 1, json.dumps(again))

    # A station is owned only once its partition claim is answered; until then a to-owner finds no owner and faults
    # (delivered nowhere), and the mod retries the same operation. Any other fault is a failure.
    late_faults = Counter((e["process"], e["raw"]) for e in events("fault")) - early_faults
    unexpected = [k for k in late_faults if "error=no node owns entity" not in k[1]]
    bench.record("faults", {"beforeStations": [list(k) for k in early_faults], "after": [list(k) for k in late_faults],
                            "retries": [e["raw"] for e in events("retry")]})
    bench.check("no mod channel fault reached the mod once the stations existed, other than a not-yet-owned station",
                not unexpected, json.dumps([list(k) for k in unexpected])[:400])
    for process in processes:
        record = _compat_records(_log(bench, process)).get(str(REFERENCE_ID), {})
        bench.check(f"{process}: the reference mod's #260 record is D adapted",
                    (record.get("class"), record.get("placement"), record.get("here")) == ("D", "adapted", "adapted"),
                    json.dumps({k: record.get(k) for k in ("class", "placement", "here", "decidedBy")}))
    # node-2 restarts under a new epoch: a send to it meanwhile faults (connection refused), as it should.
    # The other instances are told to resync.
    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", timeout=SERVER_TIMEOUT)

    def told():
        resets = [e for e in events("reset") if e.get("reason") in ("node-gone:node-2", "node-restarted:node-2")]
        return resets if {e["process"] for e in resets} >= {"wa-1", "node-1"} else None
    resets = _poll(told, 60)
    bench.record("resets", events("reset"))
    bench.check("wa-1 and node-1 got a node-gone or node-restarted reset for node-2", resets, json.dumps(events("reset"))[:400])

    final_acts = events("act")
    bench.check("after the handover and the restart, still one act per decision",
                sorted(e["op"] for e in final_acts) == sorted({e["op"] for e in events("decide")}), json.dumps(final_acts))
    bench.exercised(mod=REFERENCE_ID, stations=grids, moved=grid, partition=partition, player=attached)


SCENARIOS_MODS = {
    "P1-MOD-STORAGE-00": (p1_mod_storage_00, "T-0290: handover gate - mod storage key base/own type is no residual, data is", "1"),
    "P1-MOD-LOCAL": (p1_mod_local, "T-0290: world mods load from each process's staged folder, no download", "3"),
    "P1-MOD-LOCAL-RED": (p1_mod_local_red, "T-0290: a process whose mod folder is missing fails its load, no download", "3"),
    "P1-MOD-COMPAT": (p1_mod_compat, "T-0242: per-mod class/placement record on every process, WA-only skip, overrides, write counters", "3"),
    "P1-SERVER-DETAILS": (p1_server_details, "SE-1076: the Steam frontend publishes the sc rules, the server details list the world's mods", "3"),
    "P1-WORLD-NAME": (p1_world_name_save_folder, "SE-788: the save folder stays Saves/<world id> whatever the world is called; mods read the configs seeded there", "3"),
    "P1-MOD-CHANNEL-00": (p1_mod_channel_00, "#1280: mod channel v1 self-test - roster resets, to-owner codec rule", "1"),
    "P1-MOD-MSG-SERVER": (p1_mod_msg_server, "DF-671: a client's mod message to the server reaches the node's mods, whatever server id it addressed", "1"),
    "P2-MOD-V1": (p2_mod_v1, "#1280: a class D mod adapted to the mod channel v1 acts exactly once; owner routing, player nodes, handover hooks, reset", "4"),
}
