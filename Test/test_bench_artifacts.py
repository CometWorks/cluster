"""The screenshot endpoint returns before its asynchronous PNG write finishes; starts no game."""
import tempfile
import unittest
from pathlib import Path

from bench import HOST, complete_png


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
