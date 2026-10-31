"""Fixture knob changes must preserve the node's replacement config document."""
import io
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import scenarios_offline


class OfflineConfig(unittest.TestCase):
    def test_policy_change_preserves_admission_settings(self):
        previous = {"offline.graceSeconds": "0", "offline.quietMeters": "0", "offline.unknownBlocks": "Block"}
        requests = []

        def respond(request, **kwargs):
            requests.append(request)
            values = previous if isinstance(request, str) else json.loads(request.data)
            return io.BytesIO(json.dumps({"values": values}).encode())

        with patch.object(scenarios_offline, "_nodes", return_value={"node-1": SimpleNamespace(control="127.0.0.1:12345")}), \
                patch.object(scenarios_offline, "_assert_bench_endpoint"), \
                patch("urllib.request.urlopen", side_effect=respond):
            result = scenarios_offline._put_config(None, "node-1", {"offline.unknownBlocks": "Freeze"})
        self.assertEqual(result["values"], {**previous, "offline.unknownBlocks": "Freeze"})
        self.assertEqual(requests[-1].method, "PUT")
