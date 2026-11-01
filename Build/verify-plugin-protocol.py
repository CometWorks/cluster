#!/usr/bin/env python3
"""Check the vendored SDK protocol hash, optionally against a Magnetar checkout."""
import hashlib
import pathlib
import sys
root = pathlib.Path(__file__).resolve().parents[1]
protocol = root / "ClusterRegistry/PluginProtocol.cs"
expected = (root / "ClusterRegistry/PluginProtocol.sha256").read_text().strip()
assert hashlib.sha256(protocol.read_bytes()).hexdigest() == expected, "Vendored SDK protocol hash changed"
if len(sys.argv) == 2:
    source = pathlib.Path(sys.argv[1]) / "PluginSdk/Clustering/PluginProtocol.cs"
    assert protocol.read_bytes() == source.read_bytes(), "SDK and Gateway protocols differ"
print("Plugin protocol verified")
