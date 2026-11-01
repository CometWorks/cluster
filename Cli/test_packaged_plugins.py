"""Offline launcher checks. Never starts Magnetar, a Gateway or a game process."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import cluster
import magnetar_config as config


class PackagedPluginsTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / "config"
        self.plugins = self.root / "plugins"
        for role, name in config.BINARIES.items():
            self.bundle(self.plugins / name, config.PROFILES[role])
        self.transport = self.root / "DirectTransport"
        self.bundle(self.transport, config.TRANSPORT_ID)
        (self.transport / "LiteNetLib.dll").write_bytes(b"dependency")
        self.hub = patch.object(config, "HUB_PLUGINS", ["common-plugin"])
        self.hub.start()
        self.addCleanup(self.hub.stop)

    @staticmethod
    def bundle(folder, plugin_id):
        folder.mkdir(parents=True)
        (folder / (folder.name + ".dll")).write_bytes(plugin_id.encode())
        (folder / (folder.name + ".xml")).write_text(
            f"<PluginData><Id>{plugin_id}</Id><Commit>{'a' * 40}</Commit>"
            "<Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms></PluginData>")

    def write(self, role="node", **kwargs):
        return config.write_config(self.output, binaries=self.plugins, role=role,
                                   transport_binaries=self.transport, **kwargs)

    def profile(self, name):
        return ET.parse(self.output / "Profiles" / (name + ".xml")).getroot()

    def test_role_profiles_copy_complete_bundles_and_replace_old_role(self):
        for role, name in config.BINARIES.items():
            ids = self.write(role)
            profile = self.profile(config.PROFILES[role])
            self.assertEqual(ids, ["direct-transport", config.PROFILES[role]])
            self.assertEqual({entry.text for entry in profile.findall("Local/string")}, set(ids))
            self.assertEqual([entry.text for entry in profile.findall("GitHub/GitHubPluginConfig/Id")],
                             ["common-plugin"])
            self.assertEqual(list(profile.find("DevFolder")), [])
            for source in (self.plugins / name, self.transport):
                for file in source.iterdir():
                    self.assertEqual(file.read_bytes(),
                                     (self.output / "Local" / source.name / file.name).read_bytes())
        self.assertFalse((self.output / "Local/ClusterNode").exists())
        # A replacement starts from the same bundles, independent of the old config.
        first = (self.output / "Local/DirectTransport/DirectTransport.dll").read_bytes()
        self.output = self.root / "replacement"
        self.write()
        self.assertEqual(first, (self.output / "Local/DirectTransport/DirectTransport.dll").read_bytes())

    def test_transport_modes_remove_previous_managed_registration(self):
        source = self.root / "transport-source"
        source.mkdir()
        (source / "DirectTransportServer.xml").write_text("<PluginData/>")
        config.write_config(self.output, binaries=self.plugins, role="node", transport=source)
        self.write()
        sources = ET.parse(self.output / "Sources/sources.xml").getroot()
        self.assertEqual(list(sources.find("LocalPluginSources")), [])
        config.write_config(self.output, binaries=self.plugins, role="node")
        self.assertFalse((self.output / "Local/DirectTransport").exists())
        self.assertIn("direct-transport", [entry.text for entry in
                      self.profile("cluster-node").findall("GitHub/GitHubPluginConfig/Id")])

    def test_invalid_transport_never_falls_back_to_hub(self):
        manifest = self.transport / "DirectTransport.xml"
        original = manifest.read_text()
        for old, new in [("direct-transport", "wrong-id"), ("a" * 40, "TODO"),
                         ("CoreCLR", "NETCoreApp"), ("Linux", "Windows")]:
            with self.subTest(new=new):
                manifest.write_text(original.replace(old, new))
                with self.assertRaises(ValueError):
                    self.write()
                self.assertFalse(self.output.exists())
        manifest.write_text(original)
        (self.transport / "LiteNetLib.dll").unlink()
        with self.assertRaises(FileNotFoundError):
            self.write()
        self.assertFalse(self.output.exists())

    def test_infrastructure_overrides_and_invalid_roles_are_rejected(self):
        for plugin_id in ("cluster-node", "cluster-wa", "direct-transport"):
            with self.subTest(plugin_id=plugin_id):
                extra = self.root / plugin_id
                self.bundle(extra, plugin_id)
                with self.assertRaises(ValueError):
                    self.write(extra_local=[extra])
                with patch.object(config, "HUB_PLUGINS", [plugin_id]):
                    with self.assertRaises(ValueError):
                        self.write()
        with self.assertRaises(ValueError):
            self.write("typo")
        with self.assertRaises(ValueError):
            self.write(transport=self.root)
        with self.assertRaises(ValueError):
            cluster.Cluster._role_key(cluster.NodeSpec("node-1", 1, "backend", "control", role="typo"))

    def test_symlinked_payload_is_rejected(self):
        (self.transport / "linked.dll").symlink_to(self.transport / "DirectTransport.dll")
        with self.assertRaises(ValueError):
            self.write()

    def test_duplicate_ids_and_input_destination_overlap_are_rejected(self):
        extras = [self.root / "Extra1", self.root / "Extra2"]
        for folder in extras:
            self.bundle(folder, "duplicate-id")
        with self.assertRaises(ValueError):
            self.write(extra_local=extras)
        self.write()
        with self.assertRaises(ValueError):
            config.write_config(self.output, binaries=self.output / "Local", role="node",
                                transport_binaries=self.transport)
        self.assertTrue((self.output / "Local/ClusterNode/ClusterNode.dll").is_file())

    def test_environment_and_cluster_writer_use_compiled_transport(self):
        with patch.dict(os.environ, {"DIRECT_TRANSPORT_BINARIES": str(self.transport)}, clear=True):
            self.assertIsNone(cluster._transport_source())
            settings = cluster.Config(root=self.root / "run", plugin_binaries=self.plugins)
            instance = cluster.Cluster(settings)
            for node in settings.nodes:
                instance._write_magnetar_config(node)
                profile = ET.parse(settings.magnetar_config(node) / "Profiles/Current.xml").getroot()
                self.assertIn("direct-transport", [x.text for x in profile.findall("Local/string")])
            with patch.dict(os.environ, {"DIRECT_TRANSPORT_SOURCE": "hub"}):
                with self.assertRaises(ValueError):
                    cluster._transport_source()

    def test_unset_transport_source_takes_the_hub_pin(self):
        """T-0157 requirement 1: with nothing set the nodes load the hub's direct-transport, so the
        transport they run cannot drift from the clients' copy or depend on a checkout being on the
        host. A path is the dev override, and it still has to be a real Direct Transport checkout."""
        home = self.root / "home"
        (home / "direct-transport").mkdir(parents=True)
        # A checkout present in the usual place must NOT change the default: that was the old
        # arrangement, and its whole defect was that a folder on disk decided what the nodes ran.
        (home / "direct-transport/DirectTransportServer.xml").write_text("<Plugin />")
        with patch.dict(os.environ, {}, clear=True), patch.object(cluster, "HOME", home):
            self.assertIsNone(cluster._transport_source())
            settings = cluster.Config(root=self.root / "run", plugin_binaries=self.plugins)
            instance = cluster.Cluster(settings)
            node = settings.nodes[0]
            instance._write_magnetar_config(node)
            profile = ET.parse(settings.magnetar_config(node) / "Profiles/Current.xml").getroot()
            self.assertIn("direct-transport", [x.text for x in profile.findall("GitHub/GitHubPluginConfig/Id")])
            self.assertNotIn("direct-transport", [x.text for x in profile.findall("Local/string")])
            self.assertNotIn("direct-transport", [x.text for x in profile.findall("DevFolder/LocalFolderConfig/Id")])
        with patch.dict(os.environ, {"DIRECT_TRANSPORT_SOURCE": "hub"}, clear=True):
            self.assertIsNone(cluster._transport_source())
        with patch.dict(os.environ, {"DIRECT_TRANSPORT_SOURCE": str(home / "direct-transport")}, clear=True):
            self.assertEqual(home / "direct-transport", cluster._transport_source())
        with patch.dict(os.environ, {"DIRECT_TRANSPORT_SOURCE": str(self.root / "nowhere")}, clear=True):
            with self.assertRaises(FileNotFoundError):
                cluster._transport_source()

    def test_package_wrapper_validates_transport_without_starting_processes(self):
        cli = self.root / "cli"
        cli.mkdir()
        shutil.copyfile(Path(__file__).with_name("cluster"), cli / "cluster")
        # The wrapper executes only this inert stub, never the real launcher.
        (cli / "cluster.py").write_text("print('wrapper reached CLI')")
        magnetar = self.root / "Magnetar"
        magnetar.mkdir()
        (magnetar / "MagnetarInterim.bin").write_text("must not execute")
        (magnetar / "MagnetarInterim.bin").chmod(0o700)
        ds = self.root / "DedicatedServer64"
        ds.mkdir()
        (ds / "SpaceEngineersDedicated.exe").write_text("must not execute")
        env = {"PATH": os.environ["PATH"], "HOME": str(self.root),
               "MAGNETAR_HOME": str(magnetar), "SPACE_ENGINEERS_BIN64": str(ds),
               "DIRECT_TRANSPORT_BINARIES": str(self.transport)}
        def run():
            return subprocess.run(["bash", str(cli / "cluster")], env=env,
                                  capture_output=True, text=True, timeout=10)
        result = run()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("wrapper reached CLI", result.stdout)
        env["DIRECT_TRANSPORT_SOURCE"] = "hub"
        result = run()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("mutually exclusive", result.stderr)
        del env["DIRECT_TRANSPORT_SOURCE"]
        (self.transport / "LiteNetLib.dll").unlink()
        self.assertNotEqual(run().returncode, 0)

    def test_registration_command_accepts_compiled_transport(self):
        command = [sys.executable, str(Path(__file__).resolve().parents[1] / "Deploy/magnetar-register.py"),
                   "--config", str(self.output), "--binaries", str(self.plugins), "--role", "node",
                   "--direct-transport-binaries", str(self.transport)]
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("DIRECT_TRANSPORT_", "CLUSTER_"))}
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("direct-transport", [x.text for x in self.profile("cluster-node").findall("Local/string")])
        env["DIRECT_TRANSPORT_SOURCE"] = "hub"
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("mutually exclusive", result.stderr)

    def test_frozen_common_plugins_disable_sources_and_preserve_implicit_compat(self):
        common = self.root / "CommonPlugins"
        for plugin_id in ("dotnet-compat", "linux-compat", "ordinary-plugin"):
            self.bundle(common / plugin_id, plugin_id)
        with patch.dict(os.environ, {"CLUSTER_HUB_PLUGINS": ""}):
            self.write(common_plugins=common)
            sources = ET.parse(self.output / "Sources/sources.xml").getroot()
            for name in ("RemoteHubSources", "RemotePluginSources", "LocalHubSources", "LocalPluginSources", "ModSources"):
                self.assertEqual(list(sources.find(name)), [])
            profile = self.profile("cluster-node")
            self.assertEqual(list(profile.find("GitHub")), [])
            self.assertEqual({e.text for e in profile.findall("Local/string")},
                             {"cluster-node", "direct-transport", "ordinary-plugin"})
            self.assertTrue((self.output / "Local/linux-compat/linux-compat.dll").is_file())
            (self.output / "Local/unmanaged.dll").write_bytes(b"unexpected")
            with self.assertRaises(ValueError):
                self.write(common_plugins=common)

    def test_gateway_bearer_is_scoped_and_can_be_overridden(self):
        instance = cluster.Cluster(cluster.Config(root=self.root / "run"))
        with patch.dict(os.environ, {"CLUSTER_ADMIN_TOKEN": "secret"}), patch.object(cluster.urllib.request, "urlopen") as send:
            send.return_value.__enter__.return_value.read.return_value = b"{}"
            for base, expected in ((None, "Bearer secret"), (instance.config.gateway_control, "Bearer secret"),
                                   ("127.0.0.1:1", None)):
                instance.get("/registry/status", base)
                self.assertEqual(send.call_args.args[0].get_header("Authorization"), expected)
            instance._request("GET", "/registry/status", headers={"Authorization": "Bearer explicit"})
            self.assertEqual(send.call_args.args[0].get_header("Authorization"), "Bearer explicit")

    def test_sdk_check_precedes_launch_and_caches_only_success(self):
        instance = cluster.Cluster(cluster.Config(root=self.root / "run", magnetar=self.root))
        with self.assertRaisesRegex(cluster.ClusterError, "PluginSdk.dll not found"):
            instance.start_node(instance.config.nodes[0].node_id)
        sdk = self.root / "Libraries/MagnetarInterim/PluginSdk.dll"
        sdk.parent.mkdir(parents=True)
        sdk.write_bytes(b"fixture")
        with patch.object(cluster.subprocess, "run") as check:
            check.return_value.returncode = 2
            check.return_value.stderr = "upgrade required"
            with self.assertRaisesRegex(cluster.ClusterError, "upgrade required"):
                instance.require_plugin_sdk()
            check.return_value.returncode = 0
            instance.require_plugin_sdk()
            self.assertIn("--check-plugin-sdk", check.call_args.args[0])
            instance.require_plugin_sdk()
            self.assertEqual(check.call_count, 2)

    def test_frozen_start_uses_no_update_even_for_warmup(self):
        settings = cluster.Config(root=self.root / "run", plugin_binaries=self.plugins,
                                  transport_source=None, transport_binaries=self.transport,
                                  common_plugins=self.root / "CommonPlugins")
        instance = cluster.Cluster(settings)
        with patch.object(instance, "require_plugin_sdk"), patch.object(instance, "seed_dedicated_config"), patch.object(instance, "refresh_premade_world"), \
             patch.object(instance, "prepare_magnetar_config", return_value=self.output), \
             patch.object(instance, "_node_env", return_value={}), patch.object(instance, "_spawn") as spawn:
            instance.start_node(settings.nodes[0].node_id, allow_update=True)
            self.assertIn("-noupdate", spawn.call_args.args[1])

    def test_frozen_config_does_not_share_a_mutable_github_cache(self):
        common = self.root / "CommonPlugins"
        for plugin_id in ("dotnet-compat", "linux-compat"):
            self.bundle(common / plugin_id, plugin_id)
        with patch.dict(os.environ, {"CLUSTER_HUB_PLUGINS": ""}):
            settings = cluster.Config(root=self.root / "run", plugin_binaries=self.plugins,
                                      transport_source=None, transport_binaries=self.transport, common_plugins=common)
            instance = cluster.Cluster(settings)
            with patch.object(cluster, "share_github_cache") as share:
                instance.prepare_magnetar_config(settings.nodes[0])
                share.assert_not_called()


if __name__ == "__main__":
    unittest.main()
