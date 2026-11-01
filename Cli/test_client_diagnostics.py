import json
import os
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace

import cluster


class RotationPublicationTests(unittest.TestCase):
    def test_manager_assignments_use_serving_activation_and_current_epoch(self):
        captured = []
        nodes = [dict(Node="node-1", Epoch=42, Role=0, State=0, Serving=True, Spare=False,
                      Registered="2026-11-01T10:00:00+00:00", Activated="2026-11-01T11:00:00+00:00"),
                 dict(Node="spare", Role=0, State=0, Serving=True, Spare=True),
                 dict(Node="wa", Role=1, State=0, Serving=True)]
        fake = SimpleNamespace(
            get=lambda route: nodes if route == "/registry/nodes" else {"data": {"revision": 7}},
            _admin_data=lambda response: response["data"],
            _admin_mutation=lambda method, route, body: captured.append((method, route, body)) or body)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "lifetimes.json"
            publication = dict(rotatorPid=os.getpid(), configuredLifetime=1200, nodes={"node-1": {"lifetime": 900}})
            path.write_text(json.dumps(publication))
            result = cluster.Cluster.publish_rotation(fake, str(path))
            self.assertEqual(("PUT", "/admin/v1/rotation"), captured[0][:2])
            self.assertEqual(7, result["expectedRevision"])
            self.assertEqual([dict(node="node-1", epoch=42, dueAt="2026-11-01T11:15:00+00:00")], result["nodes"])
            self.assertGreater(datetime.fromisoformat(result["validUntil"]), datetime.now(timezone.utc))
            publication["nodes"] = {}
            path.write_text(json.dumps(publication))
            self.assertEqual([], cluster.Cluster.publish_rotation(fake, str(path))["nodes"])
            publication["configuredLifetime"] = 0
            path.write_text(json.dumps(publication))
            self.assertFalse(cluster.Cluster.publish_rotation(fake, str(path))["enabled"])
            publication["rotatorPid"] = 2147483647
            path.write_text(json.dumps(publication))
            with self.assertRaises(cluster.ClusterError):
                cluster.Cluster.publish_rotation(fake, str(path))


if __name__ == "__main__":
    unittest.main()
