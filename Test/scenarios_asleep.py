"""SE-1272: a handover keeps the grids Havok had asleep on the source asleep on the target.

* ``P2-HANDOVER-ASLEEP`` - a row of touching cubes rests on node-1 until Havok puts every one to sleep, then its
  partition is forced to node-2. The target's game thread is sampled from before the handover until 10 s after it:
  once a cube's body is enabled there it must never be awake, and the thaw logs ``keptAsleep`` for all of them.
  Before the fix the thaw enabled every body awake, so the target simulated the whole pile again until it settled
  (live octahedron-test 2026-11-08: Moon Station's 207 leftover trader shuttles, 0.08 sim speed for 7.8 s on every
  rotation that moved them).
* ``P2-HANDOVER-ASLEEP-RED`` - the same with ``import.keepAsleep`` off on the target: the cubes must wake there (the
  known positive, so a green run is not a sampler that cannot see a wake).

No timers: the cubes fall asleep within seconds, and the sampler stops 10 s after the handover returns.
"""

from __future__ import annotations

import json
import re
import threading
import time

from scenarios import _force_handover
from scenarios_core import _lines, _partitions
from scenarios_offline import _put_config
from scenarios_wa_ops import _poll, _require

CUBES = 12
EDGE_M = 5.0  # blocks=8 is a 2x2x2 large-armor cube: 5 m, so cubes EDGE_M apart touch face to face
ORIGIN = (700_000.0, 0.0, 0.0)


def _state(bench, node: str, grids: list[int]) -> dict:
    return bench.cluster.node(node, "/asleep-test-state?ids=" + ",".join(map(str, grids)))


class _Sampler(threading.Thread):
    """The target's sleep state of the cubes, as often as its game thread answers."""

    def __init__(self, bench, node: str, grids: list[int]):
        super().__init__(daemon=True)
        self.bench, self.node, self.grids = bench, node, grids
        self.rows: list[tuple[float, int, int, int]] = []
        self.stop = threading.Event()

    def run(self) -> None:
        started = time.time()
        while not self.stop.is_set():
            try:
                state = _state(self.bench, self.node, self.grids)
                self.rows.append((round(time.time() - started, 2), state["present"], state["enabled"], state["active"]))
            except Exception:  # noqa: BLE001 - a busy game thread answers late; the next sample counts
                pass
            self.stop.wait(0.1)


def _asleep(bench, red: bool) -> None:
    bench.start_cluster()
    bench.cluster.post("/automatic/pause")
    grids = []
    for index in range(CUBES):
        x = ORIGIN[0] + index * EDGE_M
        grids.append(bench.cluster.node("node-1", f"/trash-test-spawn?x={x}&y={ORIGIN[1]}&z={ORIGIN[2]}&blocks=8", "POST")["grid"])
    asleep = _poll(lambda: (lambda s: s if s["present"] == CUBES and s["active"] == 0 else None)(_state(bench, "node-1", grids)), 120, 1)
    _require(bench, f"SETUP: the {CUBES} touching cubes are asleep on node-1", asleep is not None,
             json.dumps(_state(bench, "node-1", grids))[:300])

    def one_partition():
        rows = [p for p in _partitions(bench, "node-1") if (p.get("memberCount") or 0) >= CUBES]
        return rows[0]["id"] if rows else None
    partition = _poll(one_partition, 120, 2)
    _require(bench, "SETUP: the cubes are one partition on node-1", partition is not None,
             json.dumps(_partitions(bench, "node-1"))[:300])

    if red:  # here, not at start: the Gateway's first config push resets a node's overrides
        _put_config(bench, "node-2", {"import.keepAsleep": "false"})
    sampler = _Sampler(bench, "node-2", grids)
    sampler.start()
    time.sleep(1)
    _force_handover(bench, "node-1", "node-2", partition)
    time.sleep(10)
    sampler.stop.set()
    sampler.join()
    bench.record("asleep-samples", sampler.rows)
    arrived = [row for row in sampler.rows if row[2] == CUBES]
    awake = max((row[3] for row in arrived), default=0)
    thaw = _lines(bench, "node-2", rf"handover_thaw .*partition={partition} ")
    kept = int(m.group(1)) if thaw and (m := re.search(r"keptAsleep=(\d+)", thaw[-1])) else None
    check = _lines(bench, "node-2", r"keep_asleep_check ")
    bench.exercised(samples=len(sampler.rows), arrivedSamples=len(arrived), maxAwake=awake, keptAsleep=kept,
                    keepAsleepCheck=check[-1][-160:] if check else None)
    _require(bench, "the target was sampled with every cube enabled", len(arrived) >= 5, f"{len(arrived)} samples")
    if red:
        bench.check("RED: with import.keepAsleep off the cubes wake on the target", awake > 0,
                    f"max awake {awake} of {CUBES}, keptAsleep={kept}")
    else:
        bench.check("the thaw kept every cube asleep", kept == CUBES, thaw[-1][:300] if thaw else "no handover_thaw line")
        bench.check("no cube woke on the target after the handover", awake == 0, f"max awake {awake} of {CUBES}")


def p2_handover_asleep(bench) -> None:
    """Grids asleep on the source stay asleep through the handover's thaw on the target."""
    _asleep(bench, red=False)


def p2_handover_asleep_red(bench) -> None:
    """Known positive: with import.keepAsleep off the same cubes wake on the target."""
    _asleep(bench, red=True)


SCENARIOS_ASLEEP = {
    "P2-HANDOVER-ASLEEP": (p2_handover_asleep, "SE-1272 a handover keeps sleeping grids asleep on the target", "2"),
    "P2-HANDOVER-ASLEEP-RED": (p2_handover_asleep_red, "SE-1272 known positive: without import.keepAsleep they wake", "2"),
}
