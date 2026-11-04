"""The screenshot endpoint returns before its asynchronous PNG write finishes; starts no game."""
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path

from bench import HOST, complete_png, run_scenario


class ScreenshotCompletion(unittest.TestCase):
    def test_waits_for_both_png_header_and_final_chunk(self):
        HOST.test_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=HOST.test_root) as folder:
            path = Path(folder) / "shot.png"
            self.assertFalse(complete_png(path))
            path.write_bytes(b"")
            self.assertFalse(complete_png(path))
            path.write_bytes(b"\x89PNG\r\n\x1a\n")
            self.assertFalse(complete_png(path))
            path.write_bytes(b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\x00IEND\xaeB`\x82")
            self.assertTrue(complete_png(path))

    def test_interrupted_scenario_records_failure_and_still_tears_down(self):
        records = {}
        result = SimpleNamespace(scenario="interrupt", title="", tier="3", passed=True, error=None,
                                 seconds=0, exercised={}, checks=[])
        bench = SimpleNamespace(result=result, record=lambda name, value: records.update({name: value}), close=lambda: records.update(closed=True))
        def interrupt(_):
            raise KeyboardInterrupt()
        with patch("bench.Bench", return_value=bench), self.assertRaises(KeyboardInterrupt):
            run_scenario(interrupt, "interrupt", "", "3", Path("unused"))
        self.assertTrue(records["closed"])
        self.assertFalse(records["result.json"]["passed"])
        self.assertIn("interrupted", records["result.json"]["error"])
