"""T-0274 second holder: vanilla MyPhysicsBody.Deactivate nulls a child body's RigidBody without
disposing it when the top-most parent's HavokWorld is already gone. The Havok wrapper's static handle
map then roots the body -> MyPhysicsBody -> entity -> the whole CLOSED grid, forever (a ship
connector's detector phantom in the 2026-09-29 dumps). HavokOrphanBodyFix disposes such a body
through CloseRigidBody.

Oracle: a weak reference to the grid, then a forced full GC after the close (test plugin
/weakref-track, /weakref-state). The CONTROL is a connector-less grid, closed the same way: it must be
collected in both arms, which proves the oracle can see a collection at all.
"""
from __future__ import annotations

import os
import time

FAR = 900_000.0


def _orphan_run(bench, fix_on: bool) -> dict:
    if not fix_on:
        bench.cluster.config.node_env["CLUSTER_HAVOK_ORPHAN_FIX"] = "0"
    bench.start_cluster()
    bench.quiesce()
    rich = bench.cluster.node("node-1", f"/rich-test-spawn?kind=rich&x={FAR}&y=0&z=0", "POST")
    rich_grids = [g["grid"] for g in rich["grids"]]
    control = bench.cluster.node("node-1", f"/trash-test-spawn?x={FAR + 5000}&y=0&z=0&static=false&blocks=27", "POST")["grid"]
    bench.record("fixture", {"rich": rich, "control": control})
    time.sleep(15)   # physics settles, the detector phantoms are in the world
    for grid in rich_grids + [control]:
        bench.record(f"track-{grid}", bench.cluster.node("node-1", f"/weakref-track?entity={grid}", "POST"))
    before = bench.cluster.node("node-1", "/weakref-state")
    bench.record("state-before-close", before)
    bench.check("every tracked grid is alive before the close", all(r["alive"] for r in before["entities"]), str(before))
    for grid in rich_grids + [control]:
        # The live order: the grid leaves the physics world first, then closes.
        bench.record(f"close-{grid}", bench.cluster.node("node-1", f"/test-close-physics-first?entity={grid}", "POST"))
    time.sleep(10)   # the close and the delayed physics removal run on later frames
    for _ in range(3):   # move the orchestrator's frozen InvokeLater segment on (see WeakRefRoutes)
        bench.cluster.node("node-1", "/weakref-flush", "POST")
        time.sleep(1)
    after = bench.cluster.node("node-1", "/weakref-state")
    bench.record("state-after-close", after)
    alive = {r["entity"]: r["alive"] for r in after["entities"]}
    hold = int(os.environ.get("ORPHAN_HOLD_S", "0"))   # keep the bench cluster up to dump node-1
    if hold:
        print(f"[P1-HVK-ORPHAN] holding {hold} s for a heap dump of node-1", flush=True)
        time.sleep(hold)
    bench.check("CONTROL: the connector-less grid is collected after its close (the oracle sees a release)",
                alive.get(control) is False, str(after))
    return {"rich": rich_grids, "alive": alive, "handles": (before["registeredHandles"], after["registeredHandles"])}


def p1_hvk_orphan(bench) -> None:
    r = _orphan_run(bench, fix_on=True)
    bench.check("the closed rich grid (with a ship connector) is collected",
                all(r["alive"].get(g) is False for g in r["rich"]), str(r))
    bench.exercised(fix="on", **r)


def p1_hvk_orphan_red(bench) -> None:
    """The earned red: the same fixture with the fix off must leave the closed rich grid alive."""
    r = _orphan_run(bench, fix_on=False)
    bench.check("fix OFF: the closed rich grid stays reachable (the vanilla leak reproduces)",
                any(r["alive"].get(g) is True for g in r["rich"]), str(r))
    bench.exercised(fix="off", **r)


def p1_hvk_disposed_00(bench) -> None:
    """T-0263: the node self-test on a real HkWorld - once disposed it keeps no released batch buffer, and
    batch calls on it write nothing (the vanilla path wrote into the freed buffer: two node SIGSEGVs)."""
    from scenarios_ext import assert_node_sections
    assert_node_sections(bench, "disposed HkWorld: batch buffers forgotten, batch calls skipped", "havok-disposed-world")


def p1_hvk_disposed_00_red(bench) -> None:
    """The red: the same section with the guard off (CLUSTER_HAVOK_DISPOSED_WORLD_GUARD=0) must FAIL on the
    kept batch pointers, i.e. vanilla keeps the released buffers the stale write goes through."""
    from scenarios_ext import node_self_test
    bench.cluster.config.node_env["CLUSTER_HAVOK_DISPOSED_WORLD_GUARD"] = "0"
    bench.start_cluster(nodes=["node-1"])
    from cluster import ClusterError
    try:
        result = node_self_test(bench, "havok-disposed-world")
    except ClusterError as error:
        result = {"ok": False, "error": str(error)}
    bench.record("self-test-guard-off", result)
    bench.check("guard off: the section fails on the released batch buffers the disposed world kept",
                not result.get("ok") and "kept its released batch buffers" in str(result), str(result)[:400])
    bench.exercised(guard="off")


SCENARIOS_ORPHAN = {
    "P1-HVK-DISPOSED-00": (p1_hvk_disposed_00, "T-0263: a disposed HkWorld forgets its batch buffers; batch calls skipped", "1"),
    "P1-HVK-DISPOSED-00-RED": (p1_hvk_disposed_00_red, "T-0263: guard off - the disposed world keeps its released buffers", "1"),
    "P1-HVK-ORPHAN": (p1_hvk_orphan, "T-0274: a closed grid with a connector is released (orphaned Havok body disposed)", "3"),
    "P1-HVK-ORPHAN-RED": (p1_hvk_orphan_red, "T-0274: the same with the fix off keeps the closed grid alive", "3"),
}
