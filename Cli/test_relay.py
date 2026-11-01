"""T-0229: gateway-rs beside the Gateway (CLUSTER_RELAY=external). Starts only a stand-in shell script."""
import os
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

import cluster
import gateway_supervisor


class GatewayRsSupervisionTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.environment = patch.dict(os.environ, {
            "ERROR_REPORTING_DIRECTORY": str(self.base / "diagnostics/runs/wrapper"),
            "ERROR_REPORTING_CONTEXT": json.dumps({"installationId": "a" * 32, "hostId": "host-1", "runId": "wrapper"}),
            "ERROR_REPORTING_POLICY_FILE": str(self.base / "missing-policy.json"),
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.cluster = cluster.Cluster(cluster.Config(root=self.base / "run"))
        self.addCleanup(self.cluster.kill)

    def stand_in(self, path: Path) -> Path:
        """A gateway-rs that records each start, runs until signalled and then exits 3 (a crash)."""
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("#!/bin/sh\nprintf '%s\\n' \"$ERROR_REPORTING_CONTEXT\" >> \"$0.contexts\"\n"
                        "echo started >> \"$0.starts\"\n"
                        "echo native-output; echo core=$(ulimit -c)\n"
                        "trap 'exit 3' TERM\nwhile :; do sleep 0.1; done\n")
        path.chmod(0o755)
        return path

    def starts(self, binary: Path) -> int:
        marker = Path(f"{binary}.starts")
        return len(marker.read_text().splitlines()) if marker.exists() else 0

    def wait(self, condition, timeout=10.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if condition():
                return True
            time.sleep(0.05)
        return condition()

    def test_binary_resolution(self):
        package = self.base / "cluster-1.1.5"
        packaged = self.stand_in(package / "gateway-rs/gateway-rs")
        (package / "gateway").mkdir()
        with patch.dict(os.environ, {"MAGNETAR_GATEWAY_DLL": str(package / "registry/ClusterRegistry.dll")}, clear=False):
            os.environ.pop("MAGNETAR_GATEWAY_RS", None)
            self.assertEqual(self.cluster.gateway_rs_binary(), packaged.resolve())
            explicit = self.stand_in(self.base / "elsewhere/gateway-rs")
            with patch.dict(os.environ, {"MAGNETAR_GATEWAY_RS": str(explicit)}):
                self.assertEqual(self.cluster.gateway_rs_binary(), explicit)
            with patch.dict(os.environ, {"MAGNETAR_GATEWAY_RS": str(self.base / "missing")}):
                with self.assertRaises(cluster.ClusterError):
                    self.cluster.gateway_rs_binary()

    def test_restarted_on_exit_and_ended_by_kill(self):
        binary = self.stand_in(self.base / "bin/gateway-rs")
        socket = self.cluster.config.root / "gateway-relay.sock"
        self.cluster._spawn_gateway_rs(binary, socket)
        self.assertTrue(self.wait(lambda: self.starts(binary) == 1))
        self.assertTrue(self.cluster.gateway_rs_pids())
        # Not part of the cluster's own processes: a graceful stop must not wait for it.
        self.assertEqual(self.cluster.running_pids(), [])

        # It exits (a crash): the loop starts it again.
        def binary_pids():
            return [pid for pid in self.cluster.gateway_rs_pids()
                    if Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")[1:2] == [str(binary).encode()]]
        self.assertEqual(len(binary_pids()), 1)
        first_pid = binary_pids()[0]
        runs = self.base / "diagnostics/runs"
        first_directory = next(runs.iterdir())
        first = json.loads((first_directory / "run.json").read_text())
        self.assertEqual(first["processId"], first_pid)
        self.assertEqual(first["executable"], str(Path("/bin/sh").resolve()))
        stat = Path(f"/proc/{first_pid}/stat").read_text()
        ticks = stat[stat.rfind(")") + 1:].split()[19]
        self.assertEqual(first["kernelIdentity"], Path("/proc/sys/kernel/random/boot_id").read_text().strip() + "/" + ticks)
        self.assertEqual(first["context"]["hostId"], "host-1")
        self.assertEqual(first["context"]["dumpsGrantedAtStart"], "false")
        self.assertTrue(first["native"])
        self.assertTrue(first["processStartedAtUtc"])
        self.assertNotEqual(first["runId"], "wrapper")
        os.kill(first_pid, 15)
        self.assertTrue(self.wait(lambda: self.starts(binary) == 2), self.starts(binary))

        crashed = json.loads((first_directory / "run.json").read_text())
        self.assertFalse(crashed["expectedExit"])
        self.assertTrue(crashed["endedAtUtc"])
        incident = json.loads((first_directory / "exit.json").read_text())
        self.assertEqual(incident["kind"], "unexpected_exit")
        self.assertEqual(incident["context"]["exitCode"], "3")
        self.assertIn("core=0", (first_directory / "process.log").read_text())
        self.assertIn("native-output", (first_directory / "process.log").read_text())
        killed = self.cluster.kill()
        self.assertTrue(killed)
        self.assertTrue(self.wait(lambda: not self.cluster.gateway_rs_pids()))
        before = self.starts(binary)
        time.sleep(1.5)
        self.assertEqual(self.starts(binary), before, "the restart loop survived kill()")
        log = (self.cluster.config.root / "gateway-rs.log").read_text()
        self.assertIn("gateway-rs exited with", log)
        remaining = [p for p in runs.iterdir() if p != first_directory]
        self.assertEqual(len(remaining), 1)
        stopped = json.loads((remaining[0] / "run.json").read_text())
        self.assertTrue(stopped["expectedExit"])
        self.assertTrue(stopped["endedAtUtc"])
        self.assertFalse((remaining[0] / "exit.json").exists())

    def test_dump_policy_requires_current_matching_explicit_lease(self):
        policy_file = self.base / "policy.json"
        env = {"ERROR_REPORTING_POLICY_FILE": str(policy_file),
               "ERROR_REPORTING_CONTEXT": json.dumps({"installationId": "a" * 32})}
        self.assertEqual(gateway_supervisor.dump_policy(env), (False, -1, None))
        policy = dict(installationId="a" * 32, diagnosticsGranted=True, dumpsGranted=True, generation=7,
                      expiresAtUtc=(datetime.now(timezone.utc) + timedelta(minutes=2)).isoformat(),
                      keyExpiresAtUtc=(datetime.now(timezone.utc) + timedelta(days=1)).isoformat())
        policy_file.write_text(json.dumps(policy))
        self.assertEqual(gateway_supervisor.dump_policy(env), (True, 7, "a" * 32))
        for identity in (None, "", "b" * 32):
            mismatched = {**env, "ERROR_REPORTING_CONTEXT": json.dumps({"installationId": identity})}
            self.assertEqual(gateway_supervisor.dump_policy(mismatched), (False, -1, None))
        for change in ({"dumpsGranted": False}, {"expiresAtUtc": 17}, {"diagnosticsGranted": False}, {"installationId": "b" * 32},
                       {"expiresAtUtc": (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()},
                       {"expiresAtUtc": (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat()}):
            policy_file.write_text(json.dumps({**policy, **change}))
            self.assertEqual(gateway_supervisor.dump_policy(env), (False, -1, None))

    def test_wrapper_started_before_first_lease_uses_current_policy_on_restart(self):
        binary = self.stand_in(self.base / "bin/gateway-rs")
        policy_file = self.base / "policy.json"
        with patch.dict(os.environ, {"ERROR_REPORTING_CONTEXT": json.dumps({"hostId": "host-1"}),
                                   "ERROR_REPORTING_POLICY_FILE": str(policy_file)}):
            self.cluster._spawn_gateway_rs(binary, self.cluster.config.root / "gateway-relay.sock")
        self.assertTrue(self.wait(lambda: self.starts(binary) == 1))
        runs = self.base / "diagnostics/runs"

        def child_context(number):
            context = json.loads(Path(f"{binary}.contexts").read_text().splitlines()[number])
            run = json.loads((runs / context["runId"] / "run.json").read_text())
            self.assertEqual(context, run["context"])
            return context, run["processId"]

        first, first_pid = child_context(0)
        self.assertNotIn("installationId", first)
        self.assertEqual(first["dumpsGrantedAtStart"], "false")
        self.assertEqual(first["dumpConsentGeneration"], "-1")
        policy = dict(installationId="a" * 32, diagnosticsGranted=True, dumpsGranted=True, generation=7,
                      expiresAtUtc=(datetime.now(timezone.utc) + timedelta(minutes=2)).isoformat(),
                      keyExpiresAtUtc=(datetime.now(timezone.utc) + timedelta(days=1)).isoformat())
        gateway_supervisor.write_json(policy_file, policy)
        os.kill(first_pid, 15)
        self.assertTrue(self.wait(lambda: self.starts(binary) == 2))
        second, second_pid = child_context(1)
        self.assertEqual(second["installationId"], "a" * 32)
        self.assertEqual(second["hostId"], "host-1")
        self.assertEqual(second["dumpsGrantedAtStart"], "true")
        self.assertEqual(second["dumpConsentGeneration"], "7")

        gateway_supervisor.write_json(policy_file, {**policy, "dumpsGranted": False, "generation": 8})
        os.kill(second_pid, 15)
        self.assertTrue(self.wait(lambda: self.starts(binary) == 3))
        third, _ = child_context(2)
        self.assertEqual(third["dumpsGrantedAtStart"], "false")
        self.assertEqual(third["dumpConsentGeneration"], "-1")
        self.cluster.kill()
        self.assertTrue(self.wait(lambda: not self.cluster.gateway_rs_pids()))

    def test_gateway_runs_when_diagnostic_storage_is_unavailable(self):
        binary = self.stand_in(self.base / "bin/gateway-rs")
        blocked = self.base / "not-a-directory"
        blocked.write_text("block storage")
        with patch.dict(os.environ, {"ERROR_REPORTING_DIRECTORY": str(blocked / "wrapper")}):
            self.cluster._spawn_gateway_rs(binary, self.cluster.config.root / "gateway-relay.sock")
            self.assertTrue(self.wait(lambda: self.starts(binary) == 1))
            self.cluster.kill()
            self.assertTrue(self.wait(lambda: not self.cluster.gateway_rs_pids()))
        self.assertIn("diagnostics unavailable", (self.cluster.config.root / "gateway-rs.log").read_text())

    def test_process_log_is_bounded(self):
        with patch("gateway_supervisor.os.write"):
            for _ in range(300):
                gateway_supervisor.output(self.base, b"x" * 65536)
        logs = list(self.base.glob("process.log*"))
        self.assertEqual(len(logs), 2)
        self.assertTrue(all(path.stat().st_size <= gateway_supervisor.LOG_BYTES for path in logs))


if __name__ == "__main__":
    unittest.main()
