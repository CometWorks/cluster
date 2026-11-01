"""SE-835, SE-863: a graceful stop soon after a warm start hung with a node Closed and a partition dirty.

Two defects made that hang:

* A partition the registry reaped as empty has no row left, so the next start of the same world loaded its world seed
  and claimed it at generation 1, below the saves of the reaped generations. Retention then dropped the new saves as
  superseded, and before SE-819 the registry refused every later save of that partition.
* A drain Final the registry refused was never captured again: the drain waited for it for good, with its members
  frozen, so the node stayed Closed and its other partitions got no Final either.

Scenarios:

* ``P2-STOP-REAPED-SEED`` - a seeded grid is handed over, deleted and its partition reaped. After a stop and a warm
  start the grid stays deleted, the partition has no row, and a stop after a catalogue GC round is clean.
* ``P2-STOP-REFUSED-FINAL`` - the registry refuses a drain's first Final (a row ahead of the node's sequence, written
  right before the stop). The Final is captured again and accepted, and the stop is clean.
"""

from __future__ import annotations

import json
import re
import time
import urllib.error
import urllib.parse

from cluster import ClusterError, wait_until
from scenarios_core import _handover_url, _lines

SEED_GRID = "End Beacon"
REGULAR = ["node-1", "node-2"]
STARTED = [*REGULAR, "wa-1"]
STOP_GRACE = 180
#: DirectGateway.CatalogGcSeconds and a margin.
GC_ROUND_SECONDS = 65
#: MySessionComponentTrashPatch.RecoveryGraceSeconds: /grid-delete refuses a grid that arrived more recently.
IMPORT_GRACE_TIMEOUT = 360


def _require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail[:500]):
        raise ClusterError(f"{label}: {detail[:500]}")


def _find_grid(bench, name: str) -> tuple[str, int, int] | None:
    """(node, partition, grid id) of a grid by name in the nodes' partition reports."""
    for node in REGULAR:
        for row in bench.cluster.node(node, "/partitions")["partitions"]:
            for member in row.get("members") or []:
                if member.get("name") == name:
                    return node, int(row["id"]), int(member["id"])
    return None


def _newest(bench, partition: int) -> tuple[int, int]:
    return max(
        (
            (int(row["SaveId"]["Generation"]), int(row["SaveId"]["Sequence"]))
            for row in bench.cluster.get(f"/registry/saves?partition={partition}") or []
            if int(row["SaveId"]["Partition"]) == partition
        ),
        default=(0, 0),
    )


def _row(bench, partition: int) -> dict | None:
    return next((row for row in bench.cluster.get("/registry/partitions") if int(row["Partition"]) == partition), None)


def _capture(bench, node: str, partition: int) -> tuple[int, int]:
    """One more catalogued save of the partition. /save-capture answers 409 while a periodic save is in flight."""
    before = _newest(bench, partition)

    def captured():
        try:
            bench.cluster.node(node, f"/save-capture?partition={partition}", "POST")
        except (ClusterError, urllib.error.URLError):
            return None
        newest = _newest(bench, partition)
        return newest if newest > before else None

    return wait_until(captured, 60, f"a new save of partition {partition}")


def _stop(bench, label: str) -> bool:
    started = time.time()
    clean = bench.cluster.stop(grace=STOP_GRACE)
    return bench.check(label, clean, f"took {time.time() - started:.0f} s")


def p2_stop_reaped_seed(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    source, partition, grid = wait_until(lambda: _find_grid(bench, SEED_GRID), 120, f"the seeded grid '{SEED_GRID}'")

    # Past the seed's generation, as a partition is after any handover or restart.
    target = REGULAR[1 - REGULAR.index(source)]
    bench.cluster.post(_handover_url(bench, partition, clientless=True, source=source, target=target))
    wait_until(
        lambda: (lambda found: found if found and found[0] == target else None)(_find_grid(bench, SEED_GRID)),
        120,
        "the handover of the seeded grid",
    )
    # Retention keeps the three newest saves of a partition: these are newer than anything at generation 1.
    for _ in range(3):
        _capture(bench, target, partition)
    newest = _newest(bench, partition)
    _require(
        bench, "setup: the partition's newest saves are above the seed's generation", newest[0] > 1, json.dumps(newest)
    )

    query = urllib.parse.urlencode({"entityId": grid, "name": SEED_GRID})
    answers = []

    def deleted():
        # On the node holding it now: the balancer may have moved it on.
        found = _find_grid(bench, SEED_GRID)
        answers.append(bench.cluster.node(found[0], f"/grid-delete?{query}", "POST") if found else {"found": None})
        return answers[-1].get("deleted")

    try:
        wait_until(deleted, IMPORT_GRACE_TIMEOUT, "the delete, refused during the import grace", interval=5)
    finally:
        bench.record("grid-delete-answers", answers)
    wait_until(lambda: _row(bench, partition) is None, 180, "the emptied partition's row reaped")
    bench.check("setup: the emptied partition is reaped", True)
    bench.record("reaped", {"partition": partition, "grid": grid, "newest": _newest(bench, partition)})

    _stop(bench, "the stop before the warm start is clean")
    bench.cluster.start(STARTED)
    bench.cluster.wait_ready(nodes=STARTED)

    found = _find_grid(bench, SEED_GRID)
    row = _row(bench, partition)
    bench.record("after-warm-start", {"found": found, "row": row, "newest": _newest(bench, partition)})
    bench.check("the deleted grid stays deleted across the warm start", found is None, json.dumps(found))
    bench.check("the reaped partition gets no row from its world seed", row is None, json.dumps(row))

    # What the hang needed: a catalogue GC round after the start, and a periodic capture of what it collected.
    # The round only logs when it marks something, and with the fix there is nothing to mark, so this one waits it out.
    time.sleep(GC_ROUND_SECONDS)
    if found:
        # Only a resurrected partition has a next save to refuse. It sorts below the reaped saves, so _newest cannot see it.
        def answered():
            try:
                bench.cluster.node(found[0], f"/save-capture?partition={partition}", "POST")
            except (ClusterError, urllib.error.URLError):
                pass
            return _lines(bench, "gateway", r"sequence is fenced|skips the sequence")

        try:
            wait_until(answered, 90, "the resurrected partition's next save", interval=5)
        except ClusterError:
            pass
    refused = _lines(bench, "gateway", r"sequence is fenced")
    bench.check("no save of the warm start is refused", not refused, "; ".join(refused)[:400])
    skipped = _lines(bench, "gateway", r"skips the sequence")
    bench.check("no save skips a sequence: this registry lost nothing", not skipped, "; ".join(skipped)[:400])

    _stop(bench, "the stop after the warm start is clean")
    closed = _lines(bench, "gateway", r"nodes not Empty")
    bench.check("the stop never ended waiting on a Closed node", not closed, "; ".join(closed)[:400])
    bench.exercised(
        partition=partition, grid=grid, route="seed -> handover -> delete -> reap -> warm start -> GC -> stop"
    )


p2_stop_reaped_seed.regular_nodes = 2


def p2_stop_refused_final(bench) -> None:
    bench.start_cluster(nodes=STARTED)
    source, partition, grid = wait_until(lambda: _find_grid(bench, SEED_GRID), 120, f"'{SEED_GRID}' in a partition")

    # Right after a save, so no periodic capture learns of the row before the Final runs into it.
    generation, sequence = _capture(bench, source, partition)
    epoch = next(
        int(row["Epoch"])
        for row in bench.cluster.get("/registry/nodes")
        if row["Node"] == source and row["State"] in (0, "Active")
    )
    ahead = sequence + 50
    bench.cluster.post(
        "/registry/saves?"
        + urllib.parse.urlencode(
            {
                "partition": partition,
                "node": source,
                "epoch": epoch,
                "generation": generation,
                "sequence": ahead,
                "kind": "Periodic",
                "size": 1,
                "checksum": "AHEAD",
                "clusterTime": 0,
                "location": "/nonexistent/row-ahead",
            }
        )
    )

    _stop(bench, "the stop after the refused Final is clean")

    refusals = _lines(bench, source, rf"save registration fenced: {partition}\b")
    bench.check("the registry refused the drain's Final", bool(refusals), "the Final never ran into the row")
    finals = _lines(
        bench,
        source,
        rf"partition save registered: partition {partition} generation {generation} sequence \d+ kind Final",
    )
    accepted = [int(re.search(r"sequence (\d+)", line).group(1)) for line in finals]
    bench.check(
        "the partition's Final was accepted past the row ahead",
        any(number > ahead for number in accepted),
        f"ahead={ahead} finals={accepted}",
    )
    unloaded = _lines(bench, source, rf"drain final registered and partition unloaded: {partition}\b")
    bench.check("its members were unloaded after the accepted Final", bool(unloaded))
    bench.check("the node went Empty", bool(_lines(bench, source, r"drain finals registered; node Empty")))
    closed = _lines(bench, "gateway", r"nodes not Empty")
    bench.check("the stop never ended waiting on a Closed node", not closed, "; ".join(closed)[:400])
    bench.exercised(partition=partition, source=source, grid=grid, ahead=ahead, finals=accepted, refusals=len(refusals))


p2_stop_refused_final.regular_nodes = 2


SCENARIOS_STOP_AFTER_WARM_START = {
    "P2-STOP-REAPED-SEED": (
        p2_stop_reaped_seed,
        "SE-835: a reaped partition's world seed stays out of a warm start, and the stop after it is clean",
        "3",
    ),
    "P2-STOP-REFUSED-FINAL": (
        p2_stop_refused_final,
        "SE-863: a drain Final the registry refuses is captured again, and the stop is clean",
        "3",
    ),
}
