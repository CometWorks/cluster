"""SE-1804: with serving=True, wait_ready returns only once every node logged node_state=Serving."""
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Cli"))
from cluster import Cluster, ClusterError  # noqa: E402


def _cluster(root: Path) -> SimpleNamespace:
    nodes = [SimpleNamespace(node_id=n) for n in ("node-1", "wa-1")]
    posts = []
    return SimpleNamespace(config=SimpleNamespace(root=root, nodes=nodes, multi_host=False), processes={},
                           spawned_at={}, post=posts.append, posts=posts)


def _write(root: Path, name: str, text: str) -> None:
    with open(root / f"{name}.log", "a") as log:
        log.write(text + "\n")


class WaitReady(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        _write(self.root, "gateway", "Gateway listening")
        for node in ("node-1", "wa-1"):
            _write(self.root, node, "[ClusterPlugin] node_state=Registering\nGame ready")

    def test_game_ready_is_enough_without_serving(self):
        cluster = _cluster(self.root)
        Cluster.wait_ready(cluster, timeout=5)
        self.assertEqual(cluster.posts, ["/resume"])

    def test_serving_waits_for_every_node(self):
        cluster = _cluster(self.root)
        _write(self.root, "wa-1", "[ClusterPlugin] node_state=Serving")

        def serve():
            time.sleep(1.5)
            _write(self.root, "node-1", "[ClusterPlugin] node_state=Serving")
        threading.Thread(target=serve).start()
        started = time.time()
        Cluster.wait_ready(cluster, timeout=10, serving=True)
        self.assertGreaterEqual(time.time() - started, 1.4)
        self.assertEqual(cluster.posts, ["/resume"])

    def test_serving_times_out_naming_the_node(self):
        _write(self.root, "wa-1", "[ClusterPlugin] node_state=Serving")
        with self.assertRaisesRegex(ClusterError, r"node-1 \(no 'node_state=Serving'\)"):
            Cluster.wait_ready(_cluster(self.root), timeout=2, serving=True)

    def test_failed_node_fails_at_once(self):
        _write(self.root, "node-1", "[ClusterPlugin] node_state=Failed detail=registry epoch fenced")
        started = time.time()
        with self.assertRaisesRegex(ClusterError, "node-1 failed before it was Serving.*epoch fenced"):
            Cluster.wait_ready(_cluster(self.root), timeout=30, serving=True)
        self.assertLess(time.time() - started, 5)


if __name__ == "__main__":
    unittest.main()
