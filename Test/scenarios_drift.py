"""SE-1152..1155: benign drift that the handover round-trip gate rejected on octahedron-test (2026-11-05/06).

``P3-HND-DRIFT`` - three fixture grids on node-1, each in a state that failed live handovers, handed to node-2
(clientless):

- a small warhead whose countdown ran out without an explosion (CountdownMs -1, still counting down), which the
  import restarts from Init's 1000 ms clamp (SE-1152, partition 4494);
- a cockpit grid with an empty block group, which the load drops (SE-1153, partition 4925);
- a server-made character seated in that cockpit, its suit one breath under full (0.9979), which the target may
  refill before the gate serializes it (SE-1154, partitions 5253/5852/5888/5921);
- a large reactor holding 0.0032 kg more uranium than MyInventory.InitItems restores, as a station refuel left
  one (SE-1155, partition 4519);
- a small cargo container full of stone and ice, the ice 0.00076 kg over what the load restores beside the stone,
  as a harvester left one (SE-1556, partition 51971);
- the seated pilot's inventory holding three oxygen bottles more than the load restores beside its hydrogen bottles, as
  a sebot trader's pilot carried them (SE-1557, partition 57263).

Without the fixes the target reports residuals on CountdownMs, BlockGroups, the uranium, ice and bottle Amounts (the suit only
when the target refills it first) and the partitions stay on node-1. With them every handover commits with no
residual. The node self-test section ``handover-drift`` (the same rules against the live dumps' values) runs on
node-2 as well.
"""

from __future__ import annotations

import json

from cluster import ClusterError
from scenarios import _all_partitions, _force_handover
from scenarios_blocks import _identity, partitions_of, spawn
from scenarios_core import _lines
from scenarios_ext import _gate_residuals, node_self_test
from scenarios_wa_ops import _poll, _require

FIXTURES = [
    {"key": "warhead", "typeId": "Warhead", "subtype": "SmallWarhead"},
    {"key": "seat", "typeId": "Cockpit", "subtype": "LargeBlockCockpitSeat"},
    {"key": "reactor", "typeId": "Reactor", "subtype": "LargeBlockLargeGenerator"},
    {"key": "cargo", "typeId": "CargoContainer", "subtype": "LargeBlockSmallContainer"},
]
ONE_BREATH_UNDER = 0.9979


def _act(bench, action: str, query: str) -> dict:
    return bench.cluster.node("node-1", f"/drift-test?action={action}&{query}", "POST")


def _state(bench, node: str, blocks: dict[str, int]) -> dict[str, dict]:
    rows = bench.cluster.node(node, "/drift-test-state?blocks=" + ",".join(str(block) for block in blocks.values()))
    return dict(zip(blocks, rows))


def p3_hnd_drift(bench) -> None:
    """SE-1152..1155, SE-1556, SE-1557: hand over a warhead that ran out, an empty block group, a seated pilot one breath
    under a full suit and with too many bottles, an overfull reactor and a full ore container."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    bench.cluster.post("/automatic/pause")  # the only partition moves are the forced handovers

    owner = _identity(bench, "se1152-owner")
    answer = spawn(bench, FIXTURES, owner, spacing=30)
    _require(bench, "the fixture grids spawned", len(answer.get("built") or []) == len(FIXTURES), json.dumps(answer)[:600])
    built = {row["key"]: row for row in answer["built"]}
    blocks = {key: int(row["block"]) for key, row in built.items()}
    grids = [int(row["grid"]) for row in built.values()]

    setup = {
        "warhead": _act(bench, "warhead-runout", f"block={blocks['warhead']}"),
        "group": _act(bench, "empty-group", f"grid={built['seat']['grid']}"),
        "reactor": _act(bench, "overfill", f"block={blocks['reactor']}"),
        "cargo": _act(bench, "overfill-ore", f"block={blocks['cargo']}"),
        "pilot": _act(bench, "pilot", f"block={blocks['seat']}&owner={owner}"),
    }
    bench.record("10-setup", setup)
    _require(bench, "the warhead ran out still counting down",
             setup["warhead"].get("countdownMs") == -1 and setup["warhead"].get("isCountingDown"), json.dumps(setup["warhead"]))
    _require(bench, "the cockpit grid holds one empty block group",
             [group["blocks"] for group in setup["group"].get("groups") or []] == [0], json.dumps(setup["group"]))
    source_uranium = float(setup["reactor"].get("uranium") or 0)
    _require(bench, "the reactor holds a uranium load", source_uranium > 0, json.dumps(setup["reactor"]))
    source_stone, source_ice = float(setup["cargo"].get("stone") or 0), float(setup["cargo"].get("ice") or 0)
    _require(bench, "the cargo container holds stone and ice", source_stone > 0 and source_ice > 0, json.dumps(setup["cargo"]))
    pilot = setup["pilot"].get("pilot") or 0
    _require(bench, "a character sits in the cockpit", pilot > 0, json.dumps(setup["pilot"]))

    placed = _poll(lambda: (lambda owners: owners if len(owners) == len(grids) and all(
        node == "node-1" for node, _ in owners.values()) else None)(partitions_of(bench, grids)), 120, 3)
    _require(bench, "every fixture grid is in a partition on node-1", placed, json.dumps(partitions_of(bench, grids)))
    partitions = sorted({partition for _, partition in placed.values()})
    bench.record("20-placed", {"grids": placed, "partitions": partitions, "blocks": blocks, "pilot": pilot})
    print(f"  [INFO] blocks={blocks} pilot={pilot} partitions={partitions}")

    bench.record("25-suit", _act(bench, "suit", f"block={blocks['seat']}&level={ONE_BREATH_UNDER}"))
    bottles = _act(bench, "overfill-pilot", f"block={blocks['seat']}")
    bench.record("26-bottles", bottles)
    fits = int(bottles.get("fits") or 0)
    _require(bench, "the pilot carries oxygen bottles beyond what the load restores",
             fits > 0 and float(bottles.get("oxygen") or 0) == fits + 3, json.dumps(bottles))
    for partition in partitions:
        try:
            _force_handover(bench, "node-1", "node-2", partition)
        except ClusterError as error:  # an aborted handover answers with an error
            print(f"  [INFO] handover of {partition}: {str(error)[:300]}")
    arrived = _poll(lambda: all(_all_partitions(bench).get(partition) == "node-2" for partition in partitions) or None, 60, 2)
    residuals = [line for partition in partitions for line in _lines(bench, "node-2", rf"handover residual .*partition={partition} ")]
    bench.record("30-residuals", residuals)
    for line in residuals:
        print(f"  [INFO] {line[-200:]}")
    bench.check("the handover gate reported no residual on node-2", not residuals, " | ".join(residuals)[:800])
    for partition in partitions:
        bench.check(f"the gate verified partition {partition} with 0 residuals", _gate_residuals(bench, "node-2", partition) == 0,
                    str(_gate_residuals(bench, "node-2", partition)))
    bench.check("every fixture partition committed to node-2", bool(arrived),
                json.dumps({partition: _all_partitions(bench).get(partition) for partition in partitions}))

    after = _state(bench, "node-2", blocks) if arrived else {}
    bench.record("40-target", after)
    seat, reactor = after.get("seat") or {}, after.get("reactor") or {}
    bench.check("the pilot is still seated on node-2", seat.get("pilot") == pilot, json.dumps(seat))
    bench.check("the pilot's suit kept its oxygen (within a breath)", (seat.get("suit") or 0) >= ONE_BREATH_UNDER - 0.001, json.dumps(seat))
    bench.check("the pilot arrived with the oxygen bottles the load restores (the three extra clamped)",
                float(seat.get("oxygenBottles") or 0) == fits, f"source {fits + 3} fits {fits} target {seat.get('oxygenBottles')}")
    target_uranium = float(reactor.get("uranium") or 0)
    bench.check("the reactor arrived with the uranium the load restores (a little less, the overshoot clamped)",
                0 < source_uranium - target_uranium < 0.01, f"source {source_uranium} target {target_uranium}")

    cargo = after.get("cargo") or {}
    target_stone, target_ice = float(cargo.get("stone") or 0), float(cargo.get("ice") or 0)
    bench.check("the cargo container arrived with its stone and the ice the load restores (the overshoot clamped)",
                target_stone == source_stone and 0 < source_ice - target_ice < 0.01,
                f"source {source_stone} + {source_ice} target {target_stone} + {target_ice}")

    result = node_self_test(bench, "handover-drift", node="node-2")
    bench.check("node self-test section `handover-drift` passes",
                bool(result.get("ok")) and "handover-drift" in (result.get("ran") or []), json.dumps(result)[:400])
    bench.exercised(blocks=blocks, partitions=partitions, residuals=len(residuals), arrived=bool(arrived), target=after)


SCENARIOS_DRIFT = {
    "P3-HND-DRIFT": (p3_hnd_drift, "SE-1152..1155, SE-1556, SE-1557: a run-out warhead, an empty block group, a pilot's suit and bottles, an overfull reactor and a full ore container pass the handover gate", "3"),
}
