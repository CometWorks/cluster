"""SE1-0247: the accepted contract's life on the World Authority (WorldAuthority.md §6.3).

* ``P3-CON-LEDGER`` - clientless. Mod contracts posted at a contract block on node-1 and accepted there
  (the contract system's own activation, as the block's request handler calls it). Each is listed on
  every node and at the World Authority, and settles exactly once wherever the transition runs: a
  timeout at the World Authority while the block sits on node-2, a finish on node-2, a replayed finish
  on node-1, an abandon after the accepting node was killed and restarted, and an abandon after a World
  Authority failover. The mod API callbacks fire once each, on node-1 where the mod posted the
  contracts. The money is checked to the credit at every step.
* ``P3-CON-ROLLBACK`` - clientless, #1272 (F30). A contract offered at a block on node-1 is in the partition's
  save, then accepted, and node-1 is killed before the next save: the recovery loads the block's entry with
  the offer in it. The contract is finished, the partition is handed over, and the new holder must not offer
  it again or let it be accepted a second time. Then the World Authority takes a global save and is replaced,
  the stale offer is put back at the block, and the replacement must refuse to accept it. The money is checked
  at every step.
* ``P3-CON-LOST`` - #1271, clientless. Both answers to a settlement the World Authority admitted are lost (the
  test plugin drops the node's next two ``contracts`` Responses): a finish and an abandon on node-1, which holds
  the block. The node keeps the contract and asks again under the same operation, so the reward, and the refund
  of the abandoned one, are paid exactly once. Red (before #1271): the contract is gone everywhere and nobody is
  paid.
* ``P3-CON-CLIENTS`` - two headless clients attached to different nodes. B owns two contract blocks on A's
  node and posts a courier contract between them there (vanilla needs both blocks to post it). A accepts it
  at the first block through the block's own request handler and gets the package. Then the blocks and A
  move to B's node, where the contract screen lists the contract and A delivers the package at the second
  block. The reward and the collateral reach A once, and the posting node's mod callbacks fire.
* ``P3-CON-STATION`` - SE-1511, clientless. Mod contracts offered at a faction station (no start block) on the
  World Authority, the way Octahedron posts its station contracts, show at that station on both regular nodes.
  Accepted on node-1, the offer leaves both nodes and the contract is active on the World Authority; a second offer
  the World Authority withdraws leaves both nodes too. Red (before SE-1511): no node lists the station offers.

The request handlers run on the server for the attached player (the headless client has no contract
screen to drive), with the player's endpoint as the event sender.
"""

from __future__ import annotations

import json
import random
import time

from cluster import wait_until
from scenarios import _all_partitions, _force_handover
from scenarios_blocks import _identity
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _handover_url, _nodes, _partitions
from scenarios_ext import _wait_arrival, _wait_partition
from scenarios_recovery import RECOVERY_TIMEOUT
from scenarios_redeliver import CLIENT2_ID, CLIENT2_PORT
from scenarios_wa_ops import _poll, _require, _try, _wa_failover

REGULAR = ("node-1", "node-2")
#: Rewards and collaterals of the contracts, small enough that no balance runs out.
REWARD, COLLATERAL = 1000, 200
FUNDS = 100000


def _other(node: str) -> str:
    return "node-2" if node == "node-1" else "node-1"


class _Ledger:
    """The cluster as the scenarios see it: one World Authority (it changes on a failover), two nodes."""

    def __init__(self, bench):
        self.bench = bench
        self.wa = "wa-1"

    def node(self, node: str, path: str, method: str = "GET"):
        return self.bench.cluster.node(node, path, method)

    def balance(self, identity: int) -> int | None:
        return _try(lambda: self.node(self.wa, f"/banking-test-status?ownerId={identity}")).get("balance")

    def wait_balance(self, label: str, identity: int, expected: int) -> bool:
        seen = _poll(lambda: self.balance(identity) == expected or None, HANDOVER_TIMEOUT)
        return self.bench.check(label, seen is not None, f"balance {self.balance(identity)}, expected {expected}")

    def active(self, node: str, contract: int, identity: int) -> dict:
        return _try(lambda: self.node(node, f"/contract-test-active?contract={contract}&identity={identity}"))

    def everywhere(self, contract: int, identity: int) -> dict[str, dict]:
        return {node: self.active(node, contract, identity) for node in (*REGULAR, self.wa)}

    def wait_listed(self, label: str, contract: int, identity: int, listed: bool) -> bool:
        """Every node and the World Authority hold the contract as Active for the player (or none does).
        A regular node also has to show it on the player's contract screen."""
        def converged():
            states = self.everywhere(contract, identity)
            ok = all(bool(state.get("active")) == listed and bool(state.get("listed")) == listed for state in states.values())
            ok = ok and all((contract in (states[node].get("screen") or [])) == listed for node in REGULAR)
            return states if ok else None
        states = _poll(converged, HANDOVER_TIMEOUT)
        return self.bench.check(label, states is not None, json.dumps(states or self.everywhere(contract, identity))[:600])

    def callbacks(self, node: str, contract: int) -> dict:
        return _try(lambda: self.node(node, f"/contract-test-callbacks?contract={contract}"))

    def wait_callbacks(self, label: str, node: str, contract: int, acquired: list[int], succeeded: int, failed: int) -> bool:
        expected = {"acquired": acquired, "succeeded": succeeded, "failed": failed}
        seen = _poll(lambda: (lambda c: c if {key: c.get(key) for key in expected} == expected else None)(
            self.callbacks(node, contract)), HANDOVER_TIMEOUT)
        # Exactly once: nothing more arrives a little later.
        if seen is not None:
            time.sleep(5)
            seen = self.callbacks(node, contract)
            seen = seen if {key: seen.get(key) for key in expected} == expected else None
        return self.bench.check(label, seen is not None, json.dumps({"seen": self.callbacks(node, contract), "expected": expected}))

    def post(self, node: str, block: int, minutes: int = 60) -> int:
        posted = self.node(node, f"/contract-test-post-custom?block={block}&reward={REWARD}&collateral={COLLATERAL}"
                                 f"&minutes={minutes}", "POST")
        self.bench.record(f"posted-{posted.get('contractId')}", posted)
        _require(self.bench, f"a mod contract was posted at the block on {node}", bool(posted.get("success")), json.dumps(posted))
        return int(posted["contractId"])

    def accept(self, node: str, contract: int, identity: int, block: int) -> None:
        answer = self.node(node, f"/contract-test-accept?contract={contract}&identity={identity}&block={block}", "POST")
        self.bench.record(f"accepted-{contract}", answer)
        _require(self.bench, f"the contract was accepted on {node}", bool(answer.get("accepted")), json.dumps(answer))


def _block_grid(bench, ledger: _Ledger, owner: int, x: float, node: str = "node-1", anyone: bool = False) -> tuple[int, int, int]:
    spawned = ledger.node(node, f"/contract-test-block?owner={owner}&x={x}&y=0&z=0&anyone={int(anyone)}", "POST")
    bench.record(f"contract-block-{x:.0f}", spawned)
    grid, block = int(spawned["grid"]), int(spawned["block"])
    return grid, block, _wait_partition(bench, node, grid)


def _hand_over(bench, partition: int, target: str, client: int | None = None) -> None:
    source = _all_partitions(bench).get(partition)
    if source == target:
        return
    if client is None:
        _force_handover(bench, source, target, partition)
    else:
        bench.cluster.post(_handover_url(bench, partition, source=source, target=target, client=client), timeout=300)
    _wait_arrival(bench, partition, target)


def _restart(bench, node: str, partition: int) -> None:
    """SIGKILL and restart a regular node, and wait until the block's partition is served again."""
    generation = next((int(row.get("Generation") or 0) for row in bench.cluster.get("/registry/partitions")
                       if int(row["Partition"]) == partition), 0)
    bench.cluster.restart_node(node)
    bench.cluster.wait_node_log(node, "Game ready", RECOVERY_TIMEOUT)

    def served():
        _try(lambda: bench.cluster.post("/registry/leases/expire"))
        rows = {int(row["Partition"]): row for row in bench.cluster.get("/registry/partitions")}
        row = rows.get(partition) or {}
        return row.get("Node") if row.get("Node") and int(row.get("Generation") or 0) > generation else None
    wait_until(served, RECOVERY_TIMEOUT, f"partition {partition} served again after the restart of {node}")


def p3_con_ledger(bench) -> None:
    """SE1-0247 - an accepted contract outlives the node it was accepted on and settles exactly once."""
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    ledger = _Ledger(bench)
    suffix = random.randrange(10 ** 6)
    poster = _identity(bench, f"se1-0247-poster-{suffix}")
    acceptor = _identity(bench, f"se1-0247-acceptor-{suffix}")
    paid = {poster: ledger.balance(poster), acceptor: ledger.balance(acceptor)}
    bench.record("start-balances", paid)

    def expect(label: str, poster_delta: int, acceptor_delta: int) -> None:
        paid[poster] += poster_delta
        paid[acceptor] += acceptor_delta
        ledger.wait_balance(f"{label}: the poster's balance is {paid[poster]}", poster, paid[poster])
        ledger.wait_balance(f"{label}: the acceptor's balance is {paid[acceptor]}", acceptor, paid[acceptor])

    try:
        grid, block, partition = _block_grid(bench, ledger, poster, 890000)
        finished = ledger.post("node-1", block)
        timed = ledger.post("node-1", block, minutes=1)
        expect("posted", -2 * REWARD, 0)

        # -- accepted on node-1: every node and the World Authority list it --
        ledger.accept("node-1", finished, acceptor, block)
        ledger.accept("node-1", timed, acceptor, block)
        expect("accepted", 0, -2 * COLLATERAL)
        ledger.wait_listed("accept: both nodes and the World Authority list the contract for the acceptor", finished, acceptor, True)
        ledger.wait_listed("accept: the time-limited contract is listed everywhere too", timed, acceptor, True)
        ledger.wait_callbacks("accept: node-1, where the mod posted, got OnContractAcquired once", "node-1", finished, [acceptor], 0, 0)

        # -- the block moves to node-2; the World Authority times the second contract out away from the block --
        _hand_over(bench, partition, "node-2")
        timed_out = _poll(lambda: not ledger.active(ledger.wa, timed, acceptor).get("active") or None, 150, interval=3)
        bench.check("timeout: the World Authority failed the time-limited contract", timed_out is not None,
                    json.dumps(ledger.everywhere(timed, acceptor))[:400])
        ledger.wait_listed("timeout: no node lists the failed contract any more", timed, acceptor, False)
        # Fail punishes the acceptor: the collateral and the reward go to the start block's owner.
        expect("timeout", REWARD + COLLATERAL, 0)
        ledger.wait_callbacks("timeout: node-1 got OnContractFailed once", "node-1", timed, [acceptor], 0, 1)

        # -- finished by the mod on node-2, which did not accept it; a replay on node-1 settles nothing --
        answer = ledger.node("node-2", f"/contract-test-finish-custom?contract={finished}", "POST")
        bench.record("finish-node-2", answer)
        bench.check("finish: node-2 finished the contract node-1 accepted", bool(answer.get("finished")), json.dumps(answer))
        expect("finish", 0, REWARD + COLLATERAL)
        ledger.wait_listed("finish: no node lists the finished contract any more", finished, acceptor, False)
        ledger.wait_callbacks("finish: node-1 got OnContractSucceeded once, though node-2 finished it", "node-1", finished,
                              [acceptor], 1, 0)
        replay = ledger.node("node-1", f"/contract-test-finish-custom?contract={finished}", "POST")
        bench.record("finish-replay-node-1", replay)
        bench.check("finish: a replayed finish on node-1 settles nothing", replay.get("finished") is False, json.dumps(replay))
        expect("replay", 0, 0)

        # -- accepted on node-2 (the block's node), node-2 killed and restarted; abandoned on node-1 --
        restarted = ledger.post("node-2", block)
        expect("posted on node-2", -REWARD, 0)
        ledger.accept("node-2", restarted, acceptor, block)
        expect("accepted on node-2", 0, -COLLATERAL)
        ledger.wait_listed("restart: the contract accepted on node-2 is listed everywhere", restarted, acceptor, True)
        bench.cluster.node("node-2", f"/save-capture?partition={partition}", "POST")
        time.sleep(5)
        _restart(bench, "node-2", partition)
        ledger.wait_listed("restart: after node-2's restart every node still lists the contract", restarted, acceptor, True)
        answer = ledger.node("node-1", f"/contract-test-abandon?contract={restarted}&identity={acceptor}", "POST")
        bench.record("abandon-node-1", answer)
        bench.check("restart: node-1 abandoned the contract", answer.get("result") == "Success", json.dumps(answer))
        expect("abandon after the restart", REWARD + COLLATERAL, 0)
        ledger.wait_listed("restart: no node lists the abandoned contract any more", restarted, acceptor, False)
        replay = ledger.node("node-2", f"/contract-test-abandon?contract={restarted}&identity={acceptor}", "POST")
        bench.check("restart: a replayed abandon on node-2 settles nothing", replay.get("result") != "Success", json.dumps(replay))
        expect("abandon replay", 0, 0)

        # -- accepted, the global save taken, the World Authority killed and replaced; abandoned on a node --
        holder = _all_partitions(bench).get(partition) or "node-1"
        failover = ledger.post(holder, block)
        expect("posted before the failover", -REWARD, 0)
        ledger.accept(holder, failover, acceptor, block)
        expect("accepted before the failover", 0, -COLLATERAL)
        ledger.wait_listed("failover: the contract is listed everywhere", failover, acceptor, True)
        generation = int(bench.cluster.get("/registry/world-authority").get("Generation") or 0)

        def newest() -> int:
            return ((_try(bench.cluster.get, "/registry/global-saves/newest") or {}).get("SaveId") or {}).get("Sequence", 0)
        before = newest()
        _require(bench, "the World Authority took a global save after the acceptance",
                 _poll(lambda: "error" not in _try(ledger.node, "wa-1", "/global-save-capture", "POST") or None, HANDOVER_TIMEOUT, 3)
                 is not None and _poll(lambda: newest() > before or None, HANDOVER_TIMEOUT, 3) is not None)
        _wa_failover(bench, generation, fresh=False)
        ledger.wa = "wa-2"
        ledger.wait_listed("failover: after the World Authority failover every node and wa-2 list the contract", failover, acceptor, True)
        away = _other(_all_partitions(bench).get(partition) or "node-1")
        # A competing accept that lost both replies may try to undo the winner after failover.
        rollback = ledger.node(
            away, f"/contract-test-withdraw-other-attempt?contract={failover}", "POST"
        )
        _require(
            bench,
            "a different acceptance attempt cannot withdraw the winner after failover",
            rollback.get("Success") is False,
            json.dumps(rollback),
        )
        answer = ledger.node(away, f"/contract-test-abandon?contract={failover}&identity={acceptor}", "POST")
        bench.record("abandon-after-failover", answer)
        bench.check(f"failover: {away}, away from the block, abandoned the contract", answer.get("result") == "Success", json.dumps(answer))
        # The block is elsewhere: the restored World Authority refunds its owner from the record of the acceptance.
        expect("abandon after the failover", REWARD + COLLATERAL, 0)
        ledger.wait_listed("failover: no node lists the abandoned contract any more", failover, acceptor, False)

        # -- both answers to an acceptance lost: that attempt withdraws its own acceptance, and a retry wins --
        holder = _all_partitions(bench).get(partition) or "node-1"
        lost = ledger.post(holder, block)
        expect("posted for the lost acceptance", -REWARD, 0)
        ledger.node(holder, "/test-drop-command-responses?subsystem=contracts&count=2", "POST")
        answer = ledger.node(holder, f"/contract-test-accept?contract={lost}&identity={acceptor}&block={block}", "POST")
        bench.record("accept-lost-replies", answer)
        dropped = ledger.node(holder, "/test-drop-command-responses")
        _require(bench, "lost accept: both answers to the acceptance were dropped", dropped.get("dropped") == 2,
                 json.dumps(dropped))
        bench.check("lost accept: the node refused the unanswered acceptance", not answer.get("accepted"),
                    json.dumps(answer))
        expect("lost accept: the collateral came back", 0, 0)
        ledger.wait_listed("lost accept: the winning attempt withdrew its own acceptance everywhere", lost, acceptor, False)
        ledger.accept(holder, lost, acceptor, block)
        expect("accepted after the lost answers", 0, -COLLATERAL)
        ledger.wait_listed("lost accept: the retried acceptance is listed everywhere", lost, acceptor, True)
        answer = ledger.node(holder, f"/contract-test-abandon?contract={lost}&identity={acceptor}", "POST")
        bench.check("lost accept: the retried contract can be abandoned", answer.get("result") == "Success", json.dumps(answer))
        expect("abandon after the lost answers", REWARD + COLLATERAL, 0)

        # -- a reservation whose origin forgot it blocks the offer only until it expires --
        stranded = ledger.post(holder, block)
        expect("posted for the stranded reservation", -REWARD, 0)
        reply = ledger.node(holder, f"/contract-test-strand-reservation?contract={stranded}&identity={acceptor}", "POST")
        _require(bench, "stranded: the World Authority reserved the offer", reply.get("Success") is True, json.dumps(reply))
        answer = ledger.node(holder, f"/contract-test-accept?contract={stranded}&identity={acceptor}&block={block}", "POST")
        bench.check("stranded: a live reservation refuses another acceptance", not answer.get("accepted"), json.dumps(answer))
        expect("stranded: the refused acceptance cost nothing", 0, 0)

        def reservation_seconds(value: str) -> None:
            # The World Authority decides expiry; set its config directly, the way p1_wa_heap_limit does.
            values = dict(ledger.node(ledger.wa, "/config")["values"], **{"contracts.reservationSeconds": value})
            bench.cluster._request("PUT", "/cluster/config", _nodes(bench)[ledger.wa].control, 30, values)
            wait_until(
                lambda: ledger.node(ledger.wa, "/config")["values"]["contracts.reservationSeconds"] == value,
                30,
                f"the World Authority to apply a {value} s reservation lifetime",
            )

        reservation_seconds("5")
        try:
            taken = _poll(
                lambda: ledger.node(
                    holder, f"/contract-test-accept?contract={stranded}&identity={acceptor}&block={block}", "POST"
                ).get("accepted") or None,
                30,
                interval=2,
            )
        finally:
            reservation_seconds("900")
        bench.check("stranded: the expired reservation gave way to a new acceptance", taken is not None)
        bench.check(
            "stranded: the World Authority logged the expiry",
            _node_line(bench, ledger.wa, f"contract reservation expired id={stranded} ", 10) is not None,
        )
        expect("accepted after the reservation expired", 0, -COLLATERAL)
        ledger.wait_listed("stranded: the new acceptance is listed everywhere", stranded, acceptor, True)
        answer = ledger.node(holder, f"/contract-test-abandon?contract={stranded}&identity={acceptor}", "POST")
        bench.check("stranded: the contract can be abandoned", answer.get("result") == "Success", json.dumps(answer))
        expect("abandon after the expiry", REWARD + COLLATERAL, 0)
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(block=block, contracts=[finished, timed, restarted, failover, lost, stranded],
                    legs="accept, timeout at the WA, finish on the other node, replay, node restart, WA failover, "
                         "lost acceptance answers, expired reservation")


def _node_line(bench, node: str, pattern: str, timeout: float = 5) -> str | None:
    line = _try(bench.cluster.wait_node_log, node, pattern, timeout)
    return line if isinstance(line, str) else None


def p3_con_rollback(bench) -> None:
    """#1272 (F30) - a settled contract is offered nowhere again after its partition rolled back to an older save."""
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    ledger = _Ledger(bench)
    suffix = random.randrange(10 ** 6)
    poster = _identity(bench, f"f30-poster-{suffix}")
    acceptor = _identity(bench, f"f30-acceptor-{suffix}")
    paid = {poster: ledger.balance(poster), acceptor: ledger.balance(acceptor)}
    bench.record("start-balances", paid)

    def expect(label: str, poster_delta: int, acceptor_delta: int) -> None:
        paid[poster] += poster_delta
        paid[acceptor] += acceptor_delta
        ledger.wait_balance(f"{label}: the poster's balance is {paid[poster]}", poster, paid[poster])
        ledger.wait_balance(f"{label}: the acceptor's balance is {paid[acceptor]}", acceptor, paid[acceptor])

    def offers(node: str) -> dict:
        return _try(lambda: ledger.node(node, f"/contract-test-offers?block={block}"))

    block = contract = 0
    try:
        grid, block, partition = _block_grid(bench, ledger, poster, 890000)
        contract = ledger.post("node-1", block)
        expect("posted", -REWARD, 0)
        copy = ledger.node("node-1", f"/contract-test-offer-copy?contract={contract}")["offer"]

        # -- S0 holds the offer in the block's entry; the acceptance comes after it, and node-1 dies before the next save --
        ledger.node("node-1", f"/save-capture?partition={partition}", "POST")
        time.sleep(5)
        ledger.accept("node-1", contract, acceptor, block)
        expect("accepted", 0, -COLLATERAL)
        ledger.wait_listed("accept: every node and the World Authority list the contract", contract, acceptor, True)
        _restart(bench, "node-1", partition)
        holder = _all_partitions(bench).get(partition) or "node-1"
        adopted = (f"contract offer not restored, already active here: block={block} contract={contract}"
                   f"|contract offers adopted block={block} .*restored=[1-9]")
        line = next(filter(None, (_node_line(bench, node, adopted) for node in REGULAR)), None)
        _require(bench, f"rollback: the recovered block entry still carried the accepted offer (recovered on {holder})",
                 line is not None, line or "no adoption line names the contract")
        bench.record("rollback-adoption", {"holder": holder, "line": line})
        ledger.wait_listed("rollback: the contract is still listed everywhere", contract, acceptor, True)
        bench.check("rollback: the accepted contract is not offered at the block", contract not in (offers(holder).get("contracts") or []),
                    json.dumps(offers(holder)))

        # -- finished; the partition moves, and the new holder adopts the entry the recovery wrote --
        answer = ledger.node(holder, f"/contract-test-finish-custom?contract={contract}", "POST")
        bench.record("finish", answer)
        _require(bench, f"finish: {holder} finished the contract", bool(answer.get("finished")), json.dumps(answer))
        expect("finish", 0, REWARD + COLLATERAL)
        ledger.wait_listed("finish: no node lists the finished contract any more", contract, acceptor, False)
        target = _other(holder)
        _hand_over(bench, partition, target)
        seen = offers(target)
        bench.check(f"handover: {target} hosts the block and does not offer the settled contract again",
                    bool(seen.get("here")) and contract not in (seen.get("contracts") or []), json.dumps(seen))
        stale = _node_line(bench, target, f"contract offer not restored, already settled here: block={block} contract={contract}")
        bench.check("handover: the block entry the recovery wrote no longer carries the contract", stale is None, stale or "")
        second = ledger.node(target, f"/contract-test-accept?contract={contract}&identity={acceptor}&block={block}", "POST")
        bench.record("second-accept", second)
        bench.check("handover: a second acceptance of the settled contract is refused", second.get("accepted") is False, json.dumps(second))
        expect("second acceptance", 0, 0)

        # -- the World Authority's global save keeps the settled id: its replacement refuses the stale offer --
        generation = int(bench.cluster.get("/registry/world-authority").get("Generation") or 0)

        def newest() -> int:
            return ((_try(bench.cluster.get, "/registry/global-saves/newest") or {}).get("SaveId") or {}).get("Sequence", 0)
        before = newest()
        _require(bench, "the World Authority took a global save after the settlement",
                 _poll(lambda: "error" not in _try(ledger.node, "wa-1", "/global-save-capture", "POST") or None, HANDOVER_TIMEOUT, 3)
                 is not None and _poll(lambda: newest() > before or None, HANDOVER_TIMEOUT, 3) is not None)
        _wa_failover(bench, generation, fresh=False)
        ledger.wa = "wa-2"
        holder = _all_partitions(bench).get(partition) or target
        back = ledger.node(holder, f"/contract-test-reoffer?offer={copy}", "POST")
        bench.record("reoffer", back)
        _require(bench, f"failover: the stale offer is back at the block on {holder}", bool(back.get("offered")), json.dumps(back))
        third = ledger.node(holder, f"/contract-test-accept?contract={contract}&identity={acceptor}&block={block}", "POST")
        bench.record("failover-accept", third)
        bench.check("failover: accepting the stale offer is refused", third.get("accepted") is False, json.dumps(third))
        refused = _node_line(bench, "wa-2", f"contract_ledger kind=Accept id={contract} .*reason=already settled", 30)
        bench.check("failover: wa-2 refused it as already settled (from the restored record)", refused is not None, refused or "")
        bench.check(f"failover: {holder} withdrew the stale offer", contract not in (offers(holder).get("contracts") or []),
                    json.dumps(offers(holder)))
        expect("after the failover", 0, 0)
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(block=block, contract=contract, legs="accept after a partition save, node kill, finish, handover, WA failover")


def p3_con_lost(bench) -> None:
    """#1271 - an admitted settlement whose answers were both lost is paid once, on the node that sent it."""
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    ledger = _Ledger(bench)
    suffix = random.randrange(10 ** 6)
    poster = _identity(bench, f"t1271-poster-{suffix}")
    acceptor = _identity(bench, f"t1271-acceptor-{suffix}")
    paid = {poster: ledger.balance(poster), acceptor: ledger.balance(acceptor)}
    bench.record("start-balances", paid)

    def expect(label: str, poster_delta: int, acceptor_delta: int) -> None:
        paid[poster] += poster_delta
        paid[acceptor] += acceptor_delta
        ledger.wait_balance(f"{label}: the poster's balance is {paid[poster]}", poster, paid[poster])
        ledger.wait_balance(f"{label}: the acceptor's balance is {paid[acceptor]}", acceptor, paid[acceptor])

    def lost(label: str, contract: int, route: str) -> dict:
        """Runs the settlement on node-1 with its next two contract answers dropped; the fault must have fired."""
        ledger.node("node-1", "/test-drop-command-responses?subsystem=contracts&count=2", "POST")
        answer = ledger.node("node-1", route, "POST")
        bench.record(f"{label}-{contract}", answer)
        dropped = ledger.node("node-1", "/test-drop-command-responses")
        _require(bench, f"{label}: both answers to node-1's settlement were dropped", dropped.get("dropped") == 2, json.dumps(dropped))
        return answer

    def once(label: str) -> None:
        """Nothing more is paid over two retry periods of the node (15 s each)."""
        time.sleep(35)
        for who, name in ((poster, "poster"), (acceptor, "acceptor")):
            bench.check(f"{label}: the {name} was paid once", ledger.balance(who) == paid[who],
                        f"balance {ledger.balance(who)}, expected {paid[who]}")

    try:
        grid, block, partition = _block_grid(bench, ledger, poster, 891000)
        finished = ledger.post("node-1", block)
        abandoned = ledger.post("node-1", block)
        expect("posted", -2 * REWARD, 0)
        ledger.accept("node-1", finished, acceptor, block)
        ledger.accept("node-1", abandoned, acceptor, block)
        expect("accepted", 0, -2 * COLLATERAL)
        ledger.wait_listed("accept: every node and the World Authority list the contract", finished, acceptor, True)
        ledger.wait_listed("accept: the second contract is listed everywhere too", abandoned, acceptor, True)

        # -- finish: admitted at the World Authority, both answers lost --
        answer = lost("finish", finished, f"/contract-test-finish-custom?contract={finished}")
        bench.check("finish: node-1 got no answer, so its finish did not complete there", answer.get("finished") is False,
                    json.dumps(answer))
        expect("finish with both answers lost", 0, REWARD + COLLATERAL)
        ledger.wait_listed("finish: no node lists the finished contract any more", finished, acceptor, False)
        ledger.wait_callbacks("finish: node-1, where the mod posted, got OnContractSucceeded once", "node-1", finished,
                              [acceptor], 1, 0)
        once("finish")

        # -- abandon: the refund of the block owner (on node-1, with the block) --
        answer = lost("abandon", abandoned, f"/contract-test-abandon?contract={abandoned}&identity={acceptor}")
        bench.record("abandon-answer", answer)
        expect("abandon with both answers lost", REWARD + COLLATERAL, 0)
        ledger.wait_listed("abandon: no node lists the abandoned contract any more", abandoned, acceptor, False)
        ledger.wait_callbacks("abandon: node-1 got OnContractFailed once", "node-1", abandoned, [acceptor], 0, 1)
        once("abandon")
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(block=block, contracts=[finished, abandoned], legs="finish and abandon, both answers lost")


def _client_node(bench, user: int) -> str | None:
    for node in REGULAR:
        player = _try(lambda node=node: bench.cluster.node(node, f"/respawn-test-player?userId={user}"))
        if player.get("attached") and player.get("character"):
            return node
    return None


def _client_partition(bench, user: int) -> tuple[str, int] | None:
    for node in REGULAR:
        for partition in _try(lambda node=node: _partitions(bench, node)) or []:
            if isinstance(partition, dict) and user in (partition.get("clients") or []):
                return node, int(partition["id"])
    return None


def _disable_contract_fixture_trash(ledger):
    # Four-block unpowered grids need to survive a World Authority replacement.
    settings = ledger.node("node-1", "/admin-sync-trash")
    ledger.node(
        "node-1",
        f"/admin-sync-trash-upload?enable=false&blockCount={settings['BlockCountThreshold']}"
        f"&voxelAge={settings['VoxelAgeThreshold']}",
        "POST",
    )
    wait_until(
        lambda: all(
            ledger.node(node, "/admin-sync-trash")["TrashRemovalEnabled"] is False
            for node in (*REGULAR, ledger.wa)
        ),
        20,
        "cluster-wide trash removal disabled for the contract grids",
    )


def _accept_while_save_held(
    bench, ledger, node: str, block: int, contract: int, identity: int, competitor: int
) -> dict:
    query = f"&userId={CLIENT_ID}&block={block}&contract={contract}"
    hold = "/global-save-register-hold"
    before = ledger.node(ledger.wa, hold)

    def pending() -> dict | None:
        status = ledger.node(ledger.wa, hold)
        if (
            status["heldSaves"] <= before["heldSaves"]
            or not status["channel"]["heldResponses"]
        ):
            return None
        active = ledger.active(ledger.wa, contract, identity)
        return status if active.get("reserved") and not active["active"] else None

    def player_request(op: str) -> dict:
        return ledger.node(node, f"/contract-test-rpc?op={op}" + query, "POST")

    player_request("watch-accept-replies")
    drop = "/test-drop-command-responses"
    copy = ledger.node(node, f"/contract-test-offer-copy?contract={contract}")["offer"]
    other = _other(node)
    ledger.node(other, f"/contract-test-reoffer?offer={copy}", "POST")
    ledger.node(node, drop + "?subsystem=contracts&count=100", "POST")
    ledger.node(ledger.wa, hold + "?ms=20000", "POST")
    try:
        queued = ledger.node(node, "/contract-test-rpc?op=queue-accept" + query, "POST")
        _require(
            bench,
            "the node queued the player's acceptance",
            queued.get("queued") == contract,
        )
        held = _poll(pending, 3, interval=0.1)
        _require(
            bench,
            "the WA reserves the offer without activating it while the save is held",
            held is not None,
        )
        duplicate = player_request("accept")
        bench.check(
            "a repeat click while reserving leaves the player's result callback pending",
            duplicate["acceptReplies"] == 0,
            json.dumps(duplicate),
        )
        # This route dispatches to the node's game thread. A blocked activation cannot answer it.
        started = time.monotonic()
        clock = _try(lambda: bench.cluster.node(node, "/plugin-clock", timeout=1))
        elapsed_ms = round((time.monotonic() - started) * 1000, 2)
        bench.record(
            "accept-during-held-save",
            {"wa": held, "clock": clock, "roundTripMs": elapsed_ms},
        )
        bench.check(
            "the accepting node serves a game-thread read while the WA holds its reply",
            clock.get("node") == node and pending() is not None,
            f"{elapsed_ms} ms, {json.dumps(clock)}",
        )
        if clock.get("node") == node:
            state = ledger.node(node, "/contract-test-rpc?op=status" + query, "POST")
            bench.check(
                "the courier receives no package before the durable reply",
                state.get("packagesAfter") == 0 and pending() is not None,
                json.dumps(state),
            )
    finally:
        ledger.node(ledger.wa, hold + "?ms=0", "POST")

    _require(
        bench,
        "the origin lost the response after the reservation became durable",
        _poll(lambda: ledger.node(node, drop)["dropped"] > 0 or None, 30, interval=0.2)
        is not None,
    )
    race = ledger.node(
        other,
        f"/contract-test-accept?contract={contract}&block={block}&identity={competitor}",
        "POST",
    )
    _require(
        bench,
        "the other player cannot win the reserved offer",
        race.get("accepted") is False,
        json.dumps(race),
    )
    _require(
        bench,
        "the authority refused the competing operation because the offer was reserved",
        _node_line(
            bench,
            ledger.wa,
            f"kind=Accept id={contract} origin={other} success=False reason=offer reserved",
        )
        is not None,
    )
    generation = int(bench.cluster.get("/registry/world-authority")["Generation"])
    _wa_failover(bench, generation, fresh=True)
    ledger.wa = "wa-2"
    restored = ledger.active(ledger.wa, contract, identity)
    _require(
        bench,
        "the replacement restored the reservation without activating it",
        restored.get("reserved") and not restored["active"],
        json.dumps(restored),
    )
    confirm = "/contract-test-hold-confirm"
    ledger.node(node, f"{confirm}?contract={contract}", "POST")
    try:
        ledger.node(node, drop + "?subsystem=contracts&count=0", "POST")
        _require(
            bench,
            "the courier has activated locally and is waiting for confirmation",
            _poll(
                lambda: ledger.node(node, confirm)["held"] > 0 or None, HANDOVER_TIMEOUT
            )
            is not None,
        )
        duplicate = player_request("accept")
        bench.check(
            "a repeat click after the grant leaves the player's result callback pending",
            duplicate["packagesAfter"] == 1 and duplicate["acceptReplies"] == 0,
            json.dumps(duplicate),
        )
    finally:
        ledger.node(node, confirm + "?contract=0", "POST")

    def delivered() -> dict | None:
        state = ledger.node(node, "/contract-test-rpc?op=status" + query, "POST")
        return (
            state
            if state["contract"]["active"] and state["packagesAfter"] == 1
            else None
        )

    delivered_state = _poll(delivered, HANDOVER_TIMEOUT) or {}
    bench.check(
        "the server sends one acceptance reply to the player for the three clicks",
        _poll(
            lambda: player_request("status")["acceptReplies"] or None, HANDOVER_TIMEOUT
        )
        == 1,
    )
    return delivered_state


def p3_con_clients(bench) -> None:
    """SE1-0247 - accepted at a block on one node by an attached player, delivered at a block on another."""
    bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
    ledger = _Ledger(bench)
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    api_b = bench.start_client(CLIENT2_ID, remote_port=CLIENT2_PORT, render=False)
    bench.wait_spawned(CLIENT2_ID)
    # B far from A, so the two ride partitions of their own once the automatic loop has split them; then
    # everything stays where the scenario puts it.
    position = (api_b.get_character() or {}).get("position") or {}
    x, y, z = (position.get(axis, 0.0) for axis in ("x", "y", "z")) if isinstance(position, dict) else position
    api_b.character_teleport(x + 20000, y, z)
    where = _poll(lambda: (lambda a, b: {"a": a, "b": b} if a and b and a[1] != b[1] else None)(
        _client_partition(bench, CLIENT_ID), _client_partition(bench, CLIENT2_ID)), HANDOVER_TIMEOUT, interval=3)
    _require(bench, "A and B ride partitions of their own", where is not None,
             json.dumps({"a": _client_partition(bench, CLIENT_ID), "b": _client_partition(bench, CLIENT2_ID)}))
    bench.cluster.post("/automatic/pause")
    contract = 0
    try:
        _disable_contract_fixture_trash(ledger)
        node_a = where["a"][0]
        node_b = _other(node_a)
        _hand_over(bench, where["b"][1], node_b, client=CLIENT2_ID)
        _require(bench, "A and B are attached to different nodes", _poll(lambda: _client_node(bench, CLIENT_ID) == node_a
                 and _client_node(bench, CLIENT2_ID) == node_b or None, HANDOVER_TIMEOUT) is not None,
                 json.dumps({"a": _client_node(bench, CLIENT_ID), "b": _client_node(bench, CLIENT2_ID)}))
        a = int(bench.cluster.node(node_a, f"/respawn-test-player?userId={CLIENT_ID}")["identityId"])
        b = int(bench.cluster.node(node_b, f"/respawn-test-player?userId={CLIENT2_ID}")["identityId"])
        bench.record("clients", {"a": {"node": node_a, "identity": a}, "b": {"node": node_b, "identity": b}, "where": where})
        for identity in (a, b):
            ledger.node(node_a, f"/banking-test-change?ownerId={identity}&amount={FUNDS}", "POST")
        _poll(lambda: all((ledger.balance(i) or 0) >= FUNDS for i in (a, b)) or None, HANDOVER_TIMEOUT)
        paid = {a: ledger.balance(a), b: ledger.balance(b)}

        # B's two contract blocks on A's node: vanilla posts a courier contract only when it finds both blocks.
        _, start, start_partition = _block_grid(bench, ledger, b, 900000, node_a, anyone=True)
        _, end, end_partition = _block_grid(bench, ledger, b, 930000, node_a, anyone=True)
        posted = ledger.node(node_a, f"/contract-test-post-hauling?block={start}&end={end}&reward={REWARD}"
                                     f"&collateral={COLLATERAL}&minutes=60", "POST")
        bench.record("posted", posted)
        _require(bench, f"B's courier contract was posted on {node_a}", bool(posted.get("success")), json.dumps(posted))
        contract = int(posted["contractId"])
        ledger.wait_balance("posted: B paid the reward in", b, paid[b] - REWARD)

        accepted = _accept_while_save_held(bench, ledger, node_a, start, contract, a, b)
        bench.record("accept", accepted)
        _require(bench, f"accept: A accepted at the start block on {node_a} and got the package",
                    (accepted.get("contract") or {}).get("active") is True and accepted.get("packagesAfter") == 1,
                    json.dumps(accepted)[:500])
        ledger.wait_balance("accept: A paid the collateral", a, paid[a] - COLLATERAL)
        ledger.wait_listed(f"accept: {node_b}'s contract screen lists A's contract before A gets there", contract, a, True)
        ledger.wait_callbacks(f"accept: {node_a}, where the contract was posted, got OnContractAcquired once", node_a, contract, [a], 0, 0)

        # The delivery block goes to B's node, and A follows with its partition, the package in its inventory.
        for partition in {start_partition, end_partition}:
            _hand_over(bench, partition, node_b)
        _hand_over(bench, where["a"][1], node_b, client=CLIENT_ID)
        _require(bench, f"A is attached to {node_b}", _poll(lambda: _client_node(bench, CLIENT_ID) == node_b or None, HANDOVER_TIMEOUT) is not None)

        def delivered():
            answer = ledger.node(node_b, f"/contract-test-rpc?op=finish&userId={CLIENT_ID}&block={end}&contract={contract}", "POST")
            bench.record(f"finish-{int(time.time())}", answer)
            return answer if not (answer.get("contract") or {}).get("active") and answer.get("packagesAfter") == 0 else None
        answer = _poll(delivered, 60, interval=5)
        bench.check(f"finish: A delivered the package at the end block on {node_b}", answer is not None, json.dumps(answer)[:500])
        ledger.wait_balance("finish: A got the reward and the collateral back once", a, paid[a] + REWARD)
        ledger.wait_balance("finish: B's reward stays paid out", b, paid[b] - REWARD)
        ledger.wait_listed("finish: no node lists the contract any more", contract, a, False)
        ledger.wait_callbacks(f"finish: {node_a} got OnContractSucceeded once, though A finished on {node_b}", node_a, contract, [a], 1, 0)
        replay = ledger.node(node_b, f"/contract-test-rpc?op=finish&userId={CLIENT_ID}&block={end}&contract={contract}", "POST")
        bench.record("finish-replay", replay)
        time.sleep(5)
        bench.check(
            "finish: a second delivery request pays nothing more",
            ledger.balance(a) == paid[a] + REWARD,
            f"balance {ledger.balance(a)}, expected {paid[a] + REWARD}",
        )

        # The Octahedron report involved item acquisition contracts. This fixture also
        # exercises a successful payment decision for an offer with zero collateral.
        acquisition = ledger.node(
            node_b, f"/contract-test-post?block={start}&reward={REWARD}", "POST"
        )
        _require(bench, "the acquisition contract was posted", acquisition["success"])
        acquisition_id = acquisition["contractId"]
        opening = ledger.balance(a)
        ledger.node(
            node_b,
            f"/contract-test-rpc?op=queue-accept&userId={CLIENT_ID}&block={start}&contract={acquisition_id}",
            "POST",
        )
        ledger.wait_listed(
            "all nodes list the accepted acquisition contract", acquisition_id, a, True
        )
        active = ledger.active(ledger.wa, acquisition_id, a)
        bench.check(
            "the production contract type confirms without debiting zero collateral",
            active.get("type") == "MyContractObtainAndDeliver"
            and active["owners"] == [a]
            and ledger.balance(a) == opening,
            json.dumps(active),
        )

        # A abandons while the origin's confirmation is still on its way. The World Authority installs the grant
        # for the settlement, and the late confirmation then finds the contract settled.
        early = ledger.node(node_b, f"/contract-test-post?block={start}&reward={REWARD}", "POST")
        _require(bench, "the second acquisition contract was posted", early["success"])
        early_id = early["contractId"]
        confirm = "/contract-test-hold-confirm"
        ledger.node(node_b, f"{confirm}?contract={early_id}", "POST")
        try:
            ledger.node(
                node_b,
                f"/contract-test-rpc?op=queue-accept&userId={CLIENT_ID}&block={start}&contract={early_id}",
                "POST",
            )
            _require(
                bench,
                "the grant is waiting for its confirmation",
                _poll(lambda: ledger.node(node_b, confirm)["held"] > 0 or None, HANDOVER_TIMEOUT) is not None,
            )
            answer = ledger.node(node_b, f"/contract-test-abandon?contract={early_id}&identity={a}", "POST")
            bench.record("abandon-before-confirm", answer)
            bench.check(
                "A can abandon the contract before its confirmation lands",
                answer.get("result") == "Success",
                json.dumps(answer),
            )
        finally:
            ledger.node(node_b, confirm + "?contract=0", "POST")
        ledger.wait_listed("the contract abandoned before its confirmation is listed nowhere", early_id, a, False)
        bench.check(
            "the late confirmation of the settled contract succeeds",
            _node_line(bench, ledger.wa, f"kind=Confirm id={early_id} origin={node_b} success=True", 60) is not None,
        )
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(contract=contract, clients=[CLIENT_ID, CLIENT2_ID], where=where)


p3_con_clients.administrators = [CLIENT2_ID]  # the vanilla admin teleport takes B away from A


def p3_con_station(bench) -> None:
    """SE-1511 - a station offer posted on the World Authority shows on every regular node."""
    bench.start_cluster()
    bench.quiesce()
    bench.cluster.post("/automatic/pause")
    ledger = _Ledger(bench)
    acceptor = _identity(bench, f"se-1511-acceptor-{random.randrange(10 ** 6)}")

    def post(minutes: int = 60) -> tuple[int, int]:
        posted = ledger.node(ledger.wa, f"/contract-test-post-station?reward={REWARD}&minutes={minutes}", "POST")
        bench.record(f"posted-station-{posted.get('contractId')}", posted)
        _require(bench, "the World Authority posted a station contract", bool(posted.get("success")), json.dumps(posted))
        return int(posted["station"]), int(posted["contractId"])

    def offered(node: str, station: int) -> list[int]:
        return _try(lambda: ledger.node(node, f"/contract-test-station-offers?station={station}")).get("contracts") or []

    def wait_offered(label: str, station: int, contract: int, listed: bool) -> bool:
        seen = _poll(lambda: all((contract in offered(node, station)) == listed for node in REGULAR) or None,
                     HANDOVER_TIMEOUT, interval=2)
        return bench.check(label, seen is not None, json.dumps({node: offered(node, station) for node in REGULAR}))

    try:
        station, taken = post()
        _, withdrawn = post()
        wait_offered("post: both nodes list the station offer", station, taken, True)
        wait_offered("post: both nodes list the second station offer", station, withdrawn, True)

        answer = ledger.node("node-1", f"/contract-test-accept?contract={taken}&identity={acceptor}&block=0&station={station}", "POST")
        bench.record("accepted-station", answer)
        _require(bench, "node-1 accepted the station offer", bool(answer.get("accepted")), json.dumps(answer))
        active = _poll(lambda: ledger.active(ledger.wa, taken, acceptor).get("active") or None, HANDOVER_TIMEOUT, interval=2)
        bench.check("accept: the World Authority holds the station contract as active", active is not None,
                    json.dumps(ledger.active(ledger.wa, taken, acceptor)))
        wait_offered("accept: no node offers the accepted contract any more", station, taken, False)
        ledger.wait_listed("accept: both nodes and the World Authority list it for the acceptor", taken, acceptor, True)

        removed = ledger.node(ledger.wa, f"/contract-test-remove?contract={withdrawn}", "POST")
        bench.check("withdraw: the World Authority removed the second offer", bool(removed.get("removed")), json.dumps(removed))
        wait_offered("withdraw: no node offers the withdrawn contract any more", station, withdrawn, False)

        answer = ledger.node("node-2", f"/contract-test-abandon?contract={taken}&identity={acceptor}", "POST")
        bench.check("abandon: node-2 abandoned the station contract", answer.get("result") == "Success", json.dumps(answer))
        ledger.wait_listed("abandon: no node lists the abandoned contract any more", taken, acceptor, False)
    finally:
        _try(lambda: bench.cluster.post("/automatic/resume"))
    bench.exercised(station=station, contracts=[taken, withdrawn], legs="post on the WA, offered on both nodes, accept, withdraw")


SCENARIOS_CONTRACTS = {
    "P3-CON-LEDGER": (p3_con_ledger, "SE1-0247: accepted contracts on the WA, settled once (clientless)", "3"),
    "P3-CON-ROLLBACK": (p3_con_rollback, "#1272: a settled contract is not offered again after a partition rollback", "3"),
    "P3-CON-LOST": (p3_con_lost, "#1271: an admitted settlement whose answers were lost is paid once", "3"),
    "P3-CON-STATION": (p3_con_station, "SE-1511: station offers posted on the WA show on every node", "3"),
    "P3-CON-CLIENTS": (p3_con_clients, "SE1-0247: accept on one node, deliver on another (two clients)", "3"),
}
