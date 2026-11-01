"""Inert fixtures: no Gateway, launcher, world tool or game starts."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import managed_deployment as managed
from test_packaged_plugins import PackagedPluginsTest


class ManagedPreparationTest(unittest.TestCase):
    def test_cli_import_does_not_modify_verified_package(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for filename in ("managed_deployment.py", "magnetar_config.py"):
                shutil.copyfile(Path(__file__).parent / filename, root / filename)
            before = managed.inventory(root)
            subprocess.run([sys.executable, "-E", str(root / "managed_deployment.py"), "--help"],
                           check=True, capture_output=True)
            self.assertEqual(before, managed.inventory(root))

    def test_profile_patch_keeps_namespaces_named_in_attribute_values(self):
        # #1371: MES stores object-typed ScriptManager variables (xsi:type="xsd:string"). XmlSerializer resolves
        # that prefix, so a patched checkpoint without the root's xmlns:xsd no longer loads.
        with tempfile.TemporaryDirectory() as temporary:
            world = Path(temporary)
            for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
                (world / filename).write_text(
                    '<?xml version="1.0" encoding="utf-8"?>\n<MyObjectBuilder_Checkpoint '
                    'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n'
                    '  <SessionName>Old</SessionName>\n  <ScriptManagerData><variables><dictionary><item>'
                    '<Key>MES-SavedDataId</Key><Value xsi:type="xsd:string">1</Value></item></dictionary></variables>'
                    '</ScriptManagerData>\n  <Settings><MaxPlayers>4</MaxPlayers></Settings>\n  <Mods />\n'
                    '</MyObjectBuilder_Checkpoint>\n', encoding="utf-8")
            managed.apply_profile_patch(world, "<MyObjectBuilder_Checkpoint><SessionName>New</SessionName><Settings>"
                                               "<MaxPlayers>200</MaxPlayers></Settings><Mods /></MyObjectBuilder_Checkpoint>")
            for filename in ("Sandbox.sbc", "Sandbox_config.sbc"):
                path = world / filename
                text = path.read_text(encoding="utf-8")
                declared = {prefix for _, (prefix, _) in ET.iterparse(path, events=("start-ns",))}
                named = {value.split(":")[0] for element in ET.parse(path).iter() for key, value in element.attrib.items()
                         if key == "{http://www.w3.org/2001/XMLSchema-instance}type" and ":" in value}
                self.assertEqual({"xsd"}, named)
                self.assertLessEqual(named | {"xsi"}, declared, filename)
                self.assertEqual(1, text.count("xmlns:xsd="))
                self.assertTrue(text.startswith("<?xml version='1.0' encoding='utf-8'?>\n<MyObjectBuilder_Checkpoint"))
                root = ET.parse(path).getroot()
                self.assertEqual("New", root.findtext("SessionName"))
                self.assertEqual("200", root.findtext("Settings/MaxPlayers"))

    def test_game_mode_tag_follows_vanilla(self):
        def tag(settings):
            return managed.game_mode_tag(ET.ElementTree(ET.fromstring(
                f"<MyObjectBuilder_Checkpoint><Settings>{settings}</Settings></MyObjectBuilder_Checkpoint>")))
        self.assertEqual("C", tag("<InventorySizeMultiplier>10</InventorySizeMultiplier>"))
        self.assertEqual("C", tag("<GameMode>Creative</GameMode>"))
        self.assertEqual("S3-1-3-3", tag("<GameMode>Survival</GameMode>"))
        self.assertEqual("S10-3-1-3", tag("<GameMode>Survival</GameMode><InventorySizeMultiplier>10.9</InventorySizeMultiplier>"
                                          "<BlocksInventorySizeMultiplier>3</BlocksInventorySizeMultiplier>"
                                          "<AssemblerSpeedMultiplier>1</AssemblerSpeedMultiplier>"))

    def test_multiple_configuration_types_and_ambiguous_inputs(self):
        first = {"configType": "Example.FirstConfig", "configuration": {"Value": 1}}
        second = {"configType": "Example.SecondConfig", "configuration": {"Value": 2}}
        self.assertEqual(first, managed.plugin_configuration(first))
        multi = {"configurations": [first, second]}
        self.assertEqual(multi, managed.plugin_configuration(multi))
        for invalid in ({"configurations": [first, first]}, {**first, **multi},
                        {"configType": "Example.Missing"}, {"configurations": "invalid"},
                        {"configType": "", "configuration": {}}, {"unexpected": True}):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                managed.plugin_configuration(invalid)

    def test_shared_host_havok_workers(self):
        def config(mode):
            return {"configType": "ServerPlugin.Config.PerformanceConfig", "configuration": {
                "values": {"havokThreadCountMode": mode, "havokThreadCount": 15}}}

        def workers(values):
            settings = values["configuration"]["values"]
            return settings["havokThreadCountMode"], settings["havokThreadCount"]

        auto = config("Auto")
        self.assertEqual(("Auto", 15), workers(managed.shared_host_havok(auto, 1, 16)))
        self.assertEqual(("Manual", 2), workers(managed.shared_host_havok(auto, 5, 16)))
        self.assertEqual(("Manual", 3), workers(managed.shared_host_havok(auto, 4, 16)))
        self.assertEqual(("Manual", 4), workers(managed.shared_host_havok(auto, 2, 64)))
        self.assertEqual(("Manual", 2), workers(managed.shared_host_havok(auto, 8, 4)))
        self.assertEqual(("Auto", 15), workers(auto))  # the spec's copy is shared by every node
        for mode in ("Manual", "Game"):
            self.assertEqual((mode, 15), workers(managed.shared_host_havok(config(mode), 5, 16)))
        multi = managed.shared_host_havok({"configurations": [config("Auto")]}, 3, 16)
        self.assertEqual(("Manual", 4), workers(multi["configurations"][0]))
        self.assertEqual({}, managed.shared_host_havok({}, 3, 16))
        self.assertGreaterEqual(managed.physical_cores(), 1)

    def test_per_host_manifests_replay_and_tamper_rejection(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root = base / "installation"
            package = root / "Package"
            payload = root / "Dependencies/payload"
            for folder, plugin in ((package / "plugins/ClusterNode", "cluster-node"),
                (package / "plugins/WorldAuthority", "cluster-wa"),
                (payload / "DirectTransport", "direct-transport"),
                (payload / "CommonPlugins/Dotnet", "dotnet-compat"),
                (payload / "CommonPlugins/Linux", "linux-compat"),
                (payload / "CommonPlugins/Performance", managed.PERFORMANCE_ID)):
                PackagedPluginsTest.bundle(folder, plugin)
            (payload / "DirectTransport/LiteNetLib.dll").write_bytes(b"fixture")
            ds = payload / "DedicatedServer/DedicatedServer64"
            ds.mkdir(parents=True)
            for file in ("Sandbox.Common.dll", "Sandbox.Game.dll", "SpaceEngineers.Game.dll",
                "SpaceEngineers.ObjectBuilders.dll", "VRage.dll", "VRage.Game.dll", "VRage.Library.dll"):
                (ds / file).write_bytes(b"fixture")
            files = {name: {"sha256": sha} for name, sha in managed.inventory(root).items()}
            managed.write_json(root / "inputs.json", {"clusterId": "test", "files": files})
            renamed = base / managed.digest(root / "inputs.json")
            root.rename(renamed)
            root = renamed
            # Quasar's Host links installation files read-only from its content store.
            for path in root.rglob("*"):
                if path.is_file():
                    path.chmod(0o444)
            world = base / "world"
            world.mkdir()
            (world / "Sandbox.sbc").write_text("<MyObjectBuilder_Checkpoint><Settings><GameMode>Survival</GameMode>"
                "<InventorySizeMultiplier>10</InventorySizeMultiplier><BlocksInventorySizeMultiplier>3</BlocksInventorySizeMultiplier>"
                "<AssemblerSpeedMultiplier>3</AssemblerSpeedMultiplier><RefinerySpeedMultiplier>3.5</RefinerySpeedMultiplier>"
                "<SyncDistance>3000</SyncDistance></Settings></MyObjectBuilder_Checkpoint>")
            spec = {"schemaVersion": 1, "clusterId": "test", "worldId": "world-1", "serverName": "Test",
                "worldName": "Test World", "messageOfTheDay": "Welcome to %worldname%",
                "binaryVersion": "fixture", "memberLimit": 16, "administrators": ["76561199500000001", 76561199500000002],
                "hosts": [{"id": host, "runRoot": str(base / (host + "-run")),
                    "executorTokenEnvironmentVariable": "EXECUTOR_TOKEN"} for host in ("host-1", "host-2")],
                "gatewayHost": "host-1", "gatewayControl": "127.0.0.1:29416", "steamListen": "0.0.0.0:27016",
                "internalNetworks": ["127.0.0.0/8"], "joinTokenEnvironmentVariable": "JOIN_TOKEN",
                "adminTokenEnvironmentVariable": "ADMIN_TOKEN", "adminTokensFileEnvironmentVariable": "ADMIN_TOKENS_FILE",
                "worldFiles": managed.inventory(world), "nodes": [
                    {"slotKey": "node-1", "nodeId": "node-1", "host": "host-1", "role": "regular",
                     "catalogSlot": 1, "backend": "127.0.0.1:28417", "control": "127.0.0.1:29417"},
                    {"slotKey": "wa-1", "nodeId": "wa-1", "host": "host-2", "role": "WA",
                     "catalogSlot": 2, "backend": "127.0.0.1:28418", "control": "127.0.0.1:29418"}],
                "pluginConfigurations": {managed.PERFORMANCE_ID: {
                    "configType": "ServerPlugin.Config.PerformanceConfig",
                    "configuration": {"values": {"havokThreadCountMode": "Auto", "havokThreadCount": 15}}}}}
            spec_path = base / "spec.json"
            managed.write_json(spec_path, spec)
            sha = managed.digest(spec_path)
            with patch.dict("os.environ", {"CLUSTER_HUB_PLUGINS": ""}), \
                 patch.object(managed.subprocess, "run") as check:
                check.return_value.returncode = 0
                results = [managed.prepare(root, spec_path, sha, host, world, base / (host + "-config"))
                           for host in ("host-1", "host-2")]
                self.assertIn("--check-plugin-sdk", check.call_args.args[0])
                self.assertEqual(results[0]["revision"], results[1]["revision"])
                for host, result in zip(("host-1", "host-2"), results):
                    manifest = Path(result["manifest"])
                    bundle = managed.read_json(manifest)
                    self.assertEqual(1, len(bundle["nodes"]))
                    self.assertEqual(host == "host-1", bundle["gateway"] is not None)
                    if bundle["gateway"] is not None:
                        # the browser row shows the world's own multipliers and sync distance (#1529)
                        admission = managed.read_json(manifest.parent / "admission.json")
                        self.assertEqual(("S10-3-3-3", 3000, 0, []), (admission["gameMode"], admission["syncDistance"],
                                                                     admission["modCount"], admission["modIds"]))
                        self.assertEqual("Test World", admission["worldName"])
                        # SE-796: the Gateway hashes this world's settings at its start for node admission
                        self.assertEqual(str(base / "host-1-run/world"), bundle["gateway"]["environment"]["CLUSTER_WORLD"])
                    self.assertFalse((base / (host + "-run")).exists())
                    self.assertIn("-noupdate", bundle["nodes"][0]["arguments"])
                    # Vanilla reads Administrators as [XmlArrayItem("unsignedLong")] and skips any other item (#1212).
                    cfg = ET.parse(manifest.parent / f"seed/{bundle['nodes'][0]['slotKey']}/SpaceEngineers-Dedicated.cfg")
                    self.assertEqual([("unsignedLong", "76561199500000001"), ("unsignedLong", "76561199500000002")],
                                     [(item.tag, item.text) for item in cfg.getroot().find("Administrators")])
                    # The players' world name and MOTD; worldId stays the identity (SE-755).
                    self.assertEqual(("Test World", "Welcome to %worldname%", None), (cfg.findtext("WorldName"),
                        cfg.findtext("MessageOfTheDay"), cfg.find("MessageOfTheDayUrl")))
                    # Quasar seeds mod configs into Saves/<worldId>: the save folder must not follow the name (SE-788).
                    self.assertEqual("world-1", bundle["nodes"][0]["environment"]["CLUSTER_SAVE_FOLDER"])
                    self.assertEqual(result, managed.prepare(root, spec_path, sha, host, world, manifest.parent))
                shared_spec = json.loads(json.dumps(spec))
                shared_spec["nodes"].append({"slotKey": "node-2", "nodeId": "node-2", "host": "host-1",
                    "role": "regular", "catalogSlot": 3, "backend": "127.0.0.1:28419",
                    "control": "127.0.0.1:29419"})
                shared_path = base / "shared-spec.json"
                managed.write_json(shared_path, shared_spec)
                shared_sha = managed.digest(shared_path)
                with patch.object(managed, "physical_cores", return_value=8):
                    for host, expected in (("host-1", ("Manual", 3)), ("host-2", ("Auto", 15))):
                        result = managed.prepare(root, shared_path, shared_sha, host, world, base / (host + "-shared-config"))
                        bundle = managed.read_json(Path(result["manifest"]))
                        for node in bundle["nodes"]:
                            canonical = managed.read_json(Path(result["manifest"]).parent / node["configurationSeed"] / "canonical.json")
                            performance = next(p for p in canonical["plugins"] if p["id"] == managed.PERFORMANCE_ID)
                            settings = performance["configuration"]["values"]
                            self.assertEqual(expected, (settings["havokThreadCountMode"], settings["havokThreadCount"]))
                modded_world = base / "modded-world"
                shutil.copytree(world, modded_world)
                (modded_world / "Sandbox.sbc").write_text(
                    "<MyObjectBuilder_Checkpoint><Settings><SyncDistance>3000</SyncDistance></Settings>"
                    "<Mods><ModItem><PublishedFileId>123456789</PublishedFileId></ModItem></Mods>"
                    "</MyObjectBuilder_Checkpoint>")
                modded_spec = json.loads(json.dumps(spec))
                modded_spec["worldFiles"] = managed.inventory(modded_world)
                modded_path = base / "modded-spec.json"
                managed.write_json(modded_path, modded_spec)
                with self.assertRaisesRegex(ValueError, "verified local payload"):
                    managed.prepare(root, modded_path, managed.digest(modded_path), "host-1",
                                    modded_world, base / "modded-config")
                self.assertFalse((base / "modded-config").exists())
                (modded_world / "Sandbox.sbc").write_bytes((world / "Sandbox.sbc").read_bytes())
                (modded_world / "Sandbox_config.sbc").write_text(
                    "<MyObjectBuilder_WorldConfiguration><Mods><ModItem /></Mods></MyObjectBuilder_WorldConfiguration>")
                modded_spec["worldFiles"] = managed.inventory(modded_world)
                modded_path.write_text(json.dumps(modded_spec))
                with self.assertRaisesRegex(ValueError, "verified local payload"):
                    managed.prepare(root, modded_path, managed.digest(modded_path), "host-1",
                                    modded_world, base / "modded-config")
                mod_item = ("<ModItem><Name>123456789.sbm</Name><PublishedFileId>123456789</PublishedFileId>"
                            "</ModItem>")
                checkpoint = ("<MyObjectBuilder_Checkpoint><Settings><SyncDistance>3000</SyncDistance>"
                              f"</Settings><Mods>{mod_item}</Mods></MyObjectBuilder_Checkpoint>")
                (modded_world / "Sandbox.sbc").write_text(checkpoint)
                (modded_world / "Sandbox_config.sbc").write_text(
                    f"<MyObjectBuilder_WorldConfiguration><Mods>{mod_item}</Mods></MyObjectBuilder_WorldConfiguration>")
                payload = modded_world / "Mods/123456789.sbm"
                payload.mkdir(parents=True)
                (payload / "Data.sbc").write_text("pinned mod")
                modded_spec["worldFiles"] = managed.inventory(modded_world)
                modded_path.write_text(json.dumps(modded_spec))
                for host in ("host-1", "host-2"):
                    prepared_mods = managed.prepare(root, modded_path, managed.digest(modded_path), host,
                                                    modded_world, base / (host + "-modded-config"))
                    bundle = managed.read_json(Path(prepared_mods["manifest"]))
                    self.assertEqual(["123456789"], bundle["profileContent"]["modIds"])
                    if bundle["gateway"] is not None:  # the browser row's mods tag and rule (#1529)
                        admission = managed.read_json(Path(prepared_mods["manifest"]).parent / "admission.json")
                        self.assertEqual((1, [123456789]), (admission["modCount"], admission["modIds"]))
                    slot = next(node["slotKey"] for node in bundle["nodes"])
                    self.assertEqual(b"pinned mod", (Path(prepared_mods["manifest"]).parent /
                                     f"seed/{slot}/Mods/123456789.sbm/Data.sbc").read_bytes())
                    # #1397: node seeds share the world seed's mod files instead of copying them.
                    config = Path(prepared_mods["manifest"]).parent
                    self.assertEqual((config / "seed/world/Mods/123456789.sbm/Data.sbc").stat().st_ino,
                                     (config / f"seed/{slot}/Mods/123456789.sbm/Data.sbc").stat().st_ino)
                    self.assertFalse((config / f"seed/{slot}/premade-world/Mods").exists())
                overlay = base / "overlay"
                shutil.copytree(modded_world / "Mods", overlay)
                for file in overlay.rglob("*"):  # Quasar passes its read-only content-store input (#1397)
                    if file.is_file():
                        file.chmod(0o444)
                patched_spec = json.loads(json.dumps(spec))
                patched_spec["profileSettingsXml"] = checkpoint
                patched_spec["modFiles"] = managed.inventory(overlay)
                patched_path = base / "patched-spec.json"
                managed.write_json(patched_path, patched_spec)
                patched = managed.prepare(root, patched_path, managed.digest(patched_path), "host-1",
                                          world, base / "patched-config", overlay)
                self.assertEqual(["123456789"], managed.read_json(Path(patched["manifest"]))["profileContent"]["modIds"])
                self.assertIn(b"123456789.sbm", (Path(patched["manifest"]).parent / "seed/world/Sandbox.sbc").read_bytes())
                with self.assertRaisesRegex(ValueError, "mod payload hash mismatch"):
                    managed.prepare(root, patched_path, managed.digest(patched_path), "host-1",
                                    world, base / "bad-patch", modded_world)
                (payload / "Data.sbc").unlink()
                modded_spec["worldFiles"] = managed.inventory(modded_world)
                modded_path.write_text(json.dumps(modded_spec))
                with self.assertRaisesRegex(ValueError, "verified local payload"):
                    managed.prepare(root, modded_path, managed.digest(modded_path), "host-1",
                                    modded_world, base / "missing-mod-config")
                # Configs held by fields/static properties and multiple types use schema 2.
                multi_spec = json.loads(json.dumps(spec))
                configs = [{"configType": "Example.FirstConfig", "configuration": {"Value": 1}},
                           {"configType": "Example.SecondConfig", "configuration": {"Value": 2}}]
                multi_spec["pluginConfigurations"] = {"direct-transport": {"configurations": configs}}
                multi_path = base / "multi-spec.json"
                managed.write_json(multi_path, multi_spec)
                multi = managed.prepare(root, multi_path, managed.digest(multi_path), "host-1", world, base / "multi-config")
                canonical = managed.read_json(Path(multi["manifest"]).parent / "config/node-1/canonical.json")
                self.assertEqual(2, canonical["schemaVersion"])
                entry = next(p for p in canonical["plugins"] if p["id"] == "direct-transport")
                self.assertEqual(configs, entry["configurations"])
                # Client frontends: Steam by default; directListen opts into the accept-all Direct
                # Transport frontend for test clusters, with or without Steam. One is required.
                gateway = managed.read_json(Path(results[0]["manifest"]))["gateway"]
                self.assertEqual(["--steam", "0.0.0.0:27016"], gateway["arguments"][:2])
                self.assertNotIn("--direct", gateway["arguments"])
                self.assertEqual("false", gateway["environment"]["CLUSTER_ALLOW_TEST_ACCEPT_ALL"])
                # Two Hosts without exportDataRoots: no world exports, but registry backups still need the export root.
                self.assertNotIn("--export-data-roots", gateway["arguments"])
                index = gateway["arguments"].index("--export-root")
                self.assertEqual(str(base / "host-1-run" / "gateway/exports"), gateway["arguments"][index + 1])
                for steam in (True, False):
                    direct_spec = json.loads(json.dumps(spec))
                    direct_spec["directListen"] = "127.0.0.1:27116"
                    if not steam:
                        del direct_spec["steamListen"]
                    direct_path = base / f"direct-{steam}.json"
                    managed.write_json(direct_path, direct_spec)
                    direct = managed.prepare(root, direct_path, managed.digest(direct_path), "host-1", world, base / f"direct-{steam}-config")
                    gateway = managed.read_json(Path(direct["manifest"]))["gateway"]
                    self.assertEqual(steam, "--steam" in gateway["arguments"])
                    index = gateway["arguments"].index("--direct")
                    self.assertEqual(["--listen", "127.0.0.1:27116"], gateway["arguments"][index + 1:index + 3])
                    self.assertIn(27116, gateway["reservedPorts"])
                    self.assertEqual(steam, 27016 in gateway["reservedPorts"])
                    self.assertEqual("true", gateway["environment"]["CLUSTER_DIRECT"])
                    self.assertEqual("true", gateway["environment"]["CLUSTER_ALLOW_TEST_ACCEPT_ALL"])
                # Plugin shared storage (T-0256): no CLUSTER_SHARED_ROOT unless the spec names the shared
                # mount; a relative path is refused.
                nodes = managed.read_json(Path(results[0]["manifest"]))["nodes"]
                self.assertTrue(all("CLUSTER_SHARED_ROOT" not in n["environment"] for n in nodes))
                shared_spec = json.loads(json.dumps(spec))
                shared_spec["sharedStorageRoot"] = str(base / "shared-mount")
                managed.write_json(base / "shared.json", shared_spec)
                shared = managed.prepare(root, base / "shared.json", managed.digest(base / "shared.json"), "host-1", world, base / "shared-config")
                nodes = managed.read_json(Path(shared["manifest"]))["nodes"]
                self.assertTrue(nodes)
                self.assertTrue(all(n["environment"]["CLUSTER_SHARED_ROOT"] == str(base / "shared-mount") for n in nodes))
                shared_spec["sharedStorageRoot"] = "relative/shared"
                managed.write_json(base / "shared-relative.json", shared_spec)
                with self.assertRaisesRegex(ValueError, "sharedStorageRoot"):
                    managed.prepare(root, base / "shared-relative.json", managed.digest(base / "shared-relative.json"), "host-1", world, base / "shared-relative-config")
                # Node GC (#1246): Server GC with DATAS, unpinned, by default; "workstation" sets it off
                # explicitly; the Gateway keeps the runtime default; anything else is refused.
                nodes = managed.read_json(Path(results[0]["manifest"]))["nodes"]
                self.assertTrue(nodes)
                for n in nodes:
                    self.assertEqual({"1"}, {n["environment"][k] for k in
                        ("DOTNET_gcServer", "DOTNET_GCDynamicAdaptationMode", "DOTNET_GCNoAffinitize")})
                gateway = managed.read_json(Path(results[0]["manifest"]))["gateway"]
                self.assertFalse([k for k in gateway["environment"] if k.startswith("DOTNET_")])
                gc_spec = json.loads(json.dumps(spec))
                gc_spec["nodeGarbageCollector"] = "workstation"
                managed.write_json(base / "workstation.json", gc_spec)
                workstation = managed.prepare(root, base / "workstation.json", managed.digest(base / "workstation.json"), "host-1", world, base / "workstation-config")
                nodes = managed.read_json(Path(workstation["manifest"]))["nodes"]
                self.assertTrue(nodes)
                self.assertTrue(all({k: v for k, v in n["environment"].items() if k.startswith("DOTNET_")}
                    == {"DOTNET_gcServer": "0"} for n in nodes))
                gc_spec["nodeGarbageCollector"] = "concurrent"
                managed.write_json(base / "gc-invalid.json", gc_spec)
                with self.assertRaisesRegex(ValueError, "nodeGarbageCollector"):
                    managed.prepare(root, base / "gc-invalid.json", managed.digest(base / "gc-invalid.json"), "host-1", world, base / "gc-invalid-config")
                no_frontend = json.loads(json.dumps(spec))
                del no_frontend["steamListen"]
                managed.write_json(base / "no-frontend.json", no_frontend)
                with self.assertRaisesRegex(ValueError, "client frontend"):
                    managed.prepare(root, base / "no-frontend.json", managed.digest(base / "no-frontend.json"), "host-1", world, base / "no-frontend-config")
                collision = json.loads(json.dumps(spec))
                collision["directListen"] = "127.0.0.1:27016"
                managed.write_json(base / "collision.json", collision)
                with self.assertRaisesRegex(ValueError, "gateway port collision"):
                    managed.prepare(root, base / "collision.json", managed.digest(base / "collision.json"), "host-1", world, base / "collision-config")
                # Hot spares (#1195): a regular node marked spare reaches managed.json; a spec without it is unchanged.
                slots = managed.read_json(Path(results[0]["manifest"]).parent / "managed.json")["slots"]
                self.assertTrue(all("spare" not in slot for slot in slots))
                spare_spec = json.loads(json.dumps(spec))
                spare_spec["nodes"].append({"slotKey": "node-2", "nodeId": "node-2", "host": "host-1", "role": "regular",
                    "catalogSlot": 3, "backend": "127.0.0.1:28419", "control": "127.0.0.1:29419", "spare": True})
                managed.write_json(base / "spare.json", spare_spec)
                spared = managed.prepare(root, base / "spare.json", managed.digest(base / "spare.json"), "host-1", world, base / "spare-config")
                slots = managed.read_json(Path(spared["manifest"]).parent / "managed.json")["slots"]
                self.assertEqual({"node-2"}, {slot["node"] for slot in slots if slot.get("spare")})
                self.assertEqual(2, len(managed.read_json(Path(spared["manifest"]))["nodes"]))
                low = json.loads(json.dumps(spare_spec))
                low["nodes"][2]["catalogSlot"], low["nodes"][0]["catalogSlot"] = 1, 3
                managed.write_json(base / "spare-low.json", low)
                with self.assertRaisesRegex(ValueError, "catalog slots after"):
                    managed.prepare(root, base / "spare-low.json", managed.digest(base / "spare-low.json"), "host-1", world, base / "spare-low-config")
                for index, change, error in ((1, {"spare": True}, "only regular nodes"), (0, {"spare": "yes"}, "only regular nodes"),
                                             (0, {"spare": True}, "not a hot spare")):
                    invalid = json.loads(json.dumps(spec))
                    invalid["nodes"][index].update(change)
                    managed.write_json(base / f"spare-invalid-{index}-{error[:3]}.json", invalid)
                    path = base / f"spare-invalid-{index}-{error[:3]}.json"
                    with self.subTest(change=change), self.assertRaisesRegex(ValueError, error):
                        managed.prepare(root, path, managed.digest(path), "host-1", world, base / (path.stem + "-config"))
                # Restore rotates credentials, not game/plugin/world inputs. New secret
                # references alone produce the new revision required by Host/Registry.
                restored_spec = json.loads(json.dumps(spec))
                for field in ("joinTokenEnvironmentVariable", "adminTokenEnvironmentVariable",
                              "adminTokensFileEnvironmentVariable"):
                    restored_spec[field] = "RESTORED_" + restored_spec[field]
                for host in restored_spec["hosts"]:
                    host["executorTokenEnvironmentVariable"] = "RESTORED_EXECUTOR_TOKEN"
                restored_path = base / "restore-spec.json"
                managed.write_json(restored_path, restored_spec)
                restored_sha = managed.digest(restored_path)
                restored_results = [managed.prepare(root, restored_path, restored_sha, host,
                    world, base / (host + "-restore-config")) for host in ("host-1", "host-2")]
                self.assertNotEqual(results[0]["revision"], restored_results[0]["revision"])
                self.assertEqual(restored_results[0]["revision"], restored_results[1]["revision"])
                self.assertEqual(restored_results[0], managed.prepare(root, restored_path,
                    restored_sha, "host-1", world, base / "host-1-restore-config"))
                check.return_value.returncode = 2
                check.return_value.stderr = "upgrade required"
                with self.assertRaisesRegex(ValueError, "SDK is incompatible"):
                    managed.prepare(root, spec_path, sha, "host-1", world, base / "rejected")
                self.assertFalse((base / "rejected").exists())
                check.return_value.returncode = 0
                manifest = Path(results[0]["manifest"])
                bundle = managed.read_json(manifest)
                bundle["nodes"][0]["arguments"].append("unapproved")
                manifest.write_text(json.dumps(bundle))
                with self.assertRaisesRegex(ValueError, "differs"):
                    managed.prepare(root, spec_path, sha, "host-1", world, manifest.parent)


if __name__ == "__main__":
    unittest.main()
