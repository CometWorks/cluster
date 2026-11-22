"""SE-1867: suit gauge drift no longer republishes the ``characters`` registry several times a second.

* ``P2-CHR-GAUGES`` - node-1 and node-2 each report ``SYNTHETIC`` online characters whose oxygen and energy drift every
  second (``/character-test-entries``). With ``characters.gaugePublishSeconds`` 0 (every change published, as before
  SE-1867) the World Authority publishes ``characters`` about once per node report; with the default 10 at most every
  10 s. A helmet toggle and a new body still reach the other node within ``PROMPT_SECONDS``.

Live (octahedron-test 2026-11-22) the WA published the whole 49 KB ``characters`` snapshot about four times a second,
~12 MB/min to every node, because oxygen and energy changed in nearly every node report. That stream rides the same
ordered Gateway queue as chat.

Rate and size come from the WA's ``/global-state``: the ``characters`` sequence (one step per publish) and its
payload length, sampled every second.
"""

from __future__ import annotations

import json
import time

from scenarios_wa_ops import _poll, _require

SYNTHETIC = 60  # per node
BASES = {"node-1": 9_300_000_000_000_000, "node-2": 9_400_000_000_000_000}
LOAD_SECONDS = 3600
SETTLE_SECONDS = 12
MEASURE_SECONDS = 40
PROMPT_SECONDS = 4.0


def _characters(bench, node: str = "wa-1") -> tuple[int, int] | None:
    state = bench.cluster.node(node, "/global-state")
    row = next((s for s in state.get("subsystems") or [] if s["subsystem"] == "characters"), None)
    return (row["sequence"], len(row.get("payload") or "")) if row else None


def _measure(bench, label: str) -> dict:
    start = _characters(bench)
    sizes = []
    started = time.time()
    while time.time() - started < MEASURE_SECONDS:
        time.sleep(1)
        sizes.append(_characters(bench)[1])
    end = _characters(bench)
    elapsed = time.time() - started
    row = {
        "label": label,
        "publishes": end[0] - start[0],
        "seconds": round(elapsed, 1),
        "perSecond": round((end[0] - start[0]) / elapsed, 3),
        "payloadBytesMax": max(sizes),
        "payloadBytesLast": end[1],
        "bytesPerMinute": round((end[0] - start[0]) / elapsed * 60 * max(sizes)),
    }
    bench.record(f"measure-{label}", row)
    return row


def _load(bench, count: int, helmet: bool = True, nodes=("node-1", "node-2")) -> None:
    for node in nodes:
        query = f"count={count}&base={BASES[node]}&seconds={LOAD_SECONDS if count else 0}&helmet={str(helmet).lower()}"
        bench.record(f"load-{node}-{count}-{helmet}", bench.cluster.node(node, "/character-test-entries?" + query, "POST"))


def _seen_on(bench, node: str, predicate, label: str) -> float | None:
    started = time.time()
    seen = _poll(lambda: predicate(bench.cluster.node(node, "/character-registry")) or None, 30, interval=0.2)
    seconds = round(time.time() - started, 2) if seen else None
    bench.record(f"prompt-{label}", {"node": node, "seconds": seconds})
    return seconds


def p2_chr_gauges(bench) -> None:
    """Gauge-only character changes publish at most every gaugePublishSeconds; body and toggle changes at once."""
    bench.start_cluster()
    _load(bench, SYNTHETIC)
    loaded = _poll(
        lambda: sum(1 for r in bench.cluster.node("node-1", "/character-registry")["instances"]
                    if r["IdentityId"] >= BASES["node-1"]) >= 2 * SYNTHETIC or None, 60)
    _require(bench, "SETUP: node-1 sees both nodes' synthetic characters", loaded is not None)

    bench.record("config-every-change", bench.cluster.handover_config({"characters.gaugePublishSeconds": "0"}))
    time.sleep(SETTLE_SECONDS)
    every = _measure(bench, "every-change")
    bench.check("CONTROL: with every change published, gauge drift republishes characters at least once a second "
                f"({every['perSecond']}/s)", every["perSecond"] >= 1.0, json.dumps(every))

    bench.record("config-default", bench.cluster.handover_config({"characters.gaugePublishSeconds": "10"}))
    time.sleep(SETTLE_SECONDS)
    throttled = _measure(bench, "throttled")
    bench.check(f"with the 10 s default, gauge drift publishes characters at most every ~10 s ({throttled['perSecond']}/s)",
                throttled["perSecond"] <= 0.15, json.dumps(throttled))

    # A toggle and a new body are not gauges: they reach the other node at once, also inside a throttle window.
    time.sleep(2)
    _load(bench, SYNTHETIC, helmet=False, nodes=("node-1",))
    first = BASES["node-1"]
    helmet = _seen_on(bench, "node-2", lambda r: any(s["IdentityId"] == first and not s["Helmet"] for s in r["settings"]),
                      "helmet")
    bench.check(f"a helmet toggle reaches node-2 within {PROMPT_SECONDS} s ({helmet} s)",
                helmet is not None and helmet <= PROMPT_SECONDS)
    time.sleep(2)
    _load(bench, SYNTHETIC + 1, nodes=("node-2",))
    added = BASES["node-2"] + SYNTHETIC
    body = _seen_on(bench, "node-1", lambda r: any(i["IdentityId"] == added and i["State"] == "Live" for i in r["instances"]),
                    "new-body")
    bench.check(f"a new body reaches node-1 within {PROMPT_SECONDS} s ({body} s)",
                body is not None and body <= PROMPT_SECONDS)

    _load(bench, 0)
    bench.exercised(synthetic=2 * SYNTHETIC, everyChange=every, throttled=throttled, helmetSeconds=helmet,
                    newBodySeconds=body)


SCENARIOS_CHARACTERS_CHURN = {
    "P2-CHR-GAUGES": (
        p2_chr_gauges,
        "SE-1867: suit gauge drift publishes characters at most every 10 s; toggles and bodies at once",
        "3",
    ),
}
