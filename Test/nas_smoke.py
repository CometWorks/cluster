#!/usr/bin/env python3
"""Connect+spawn smoke test against a deployed cluster over Direct Transport.

Starts one headless client on this host, points it at a remote Gateway's Direct Transport
frontend, drives the vanilla respawn flow to a living character, and cross-checks the session on
the Gateway's transport status (read over ssh on the Gateway host). No local cluster is started.

    uv run python nas_smoke.py --gateway HOST:27116 --ssh USER@HOST [--control-port 29416]

Exit code 0 = a player can connect and spawn; 1 = they cannot.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from bench import HOST, Bench  # noqa: E402

CLIENT_ID = 1234567890


def gateway_transport_status(ssh: str, control_port: int) -> dict:
    result = subprocess.run(
        ["ssh", ssh, f"curl -s http://127.0.0.1:{control_port}/transport/status"],
        capture_output=True, text=True, timeout=30, check=True)
    return json.loads(result.stdout)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--gateway", required=True, help="the remote Gateway's Direct Transport HOST:PORT")
    parser.add_argument("--ssh", required=True, help="ssh destination of the Gateway host")
    parser.add_argument("--control-port", type=int, default=29416, help="the Gateway's control port there")
    args = parser.parse_args(argv)
    bench = Bench("remote-connect", "connect+spawn against a deployed cluster", "smoke",
                  results_root=HOST.results / "smoke", keep_root=True)
    # Reuse the bench's client machinery only - no local cluster; the client
    # connects to the deployed Gateway instead.
    bench.cluster.config.gateway_listen = args.gateway
    try:
        bench.start_client(CLIENT_ID)
        character = bench.wait_spawned(CLIENT_ID)
        bench.check("client spawned a living character",
                    bool(character), json.dumps(character, default=str)[:300])
        status = gateway_transport_status(args.ssh, args.control_port)
        session = next((s for s in status.get("sessions", [])
                        if s.get("userId") == CLIENT_ID), None)
        bench.check("gateway session is Ready",
                    bool(session) and session.get("admissionState") == "Ready",
                    json.dumps(session, default=str)[:300])
        bench.screenshot(CLIENT_ID, "01-spawned")
    except Exception as error:
        bench.check("smoke test completed", False, f"{type(error).__name__}: {error}")
    finally:
        bench.kill_client(CLIENT_ID)
        # Give the Gateway a moment to notice the disconnect before reporting.
        time.sleep(2)
    bench.record("result", {
        "passed": bench.result.passed,
        "checks": [{"label": c.label, "passed": c.passed, "detail": c.detail}
                   for c in bench.result.checks],
    })
    print("RESULT:", "PASS" if bench.result.passed else "FAIL")
    return 0 if bench.result.passed else 1


if __name__ == "__main__":
    sys.exit(main())
