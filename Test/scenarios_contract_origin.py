"""Crash a regular node on either side of its real player's courier grant."""

import json
from uuid import UUID

from cluster import ClusterHttpError, wait_until
from scenarios_blocks import _identity
import scenarios_contracts
from scenarios_contracts import (
    CLIENT_ID,
    COLLATERAL,
    FUNDS,
    REGULAR,
    REWARD,
    _block_grid,
    _disable_contract_fixture_trash,
    _client_node,
    _client_partition,
    _Ledger,
    _node_line,
    _restart,
)
from scenarios_recovery import RECOVERY_TIMEOUT, _nodes
from scenarios_saves import _newest
from scenarios_wa_ops import _require


def _capture(bench, origin, partition):
    def sequence():
        return ((_newest(bench, partition) or {}).get("SaveId") or {}).get(
            "Sequence", 0
        )

    def queue():
        try:
            bench.cluster.node(origin, f"/save-capture?partition={partition}", "POST")
            return True
        except ClusterHttpError as error:
            if error.status == 409:
                return False
            raise

    # The next registered save could already be in flight when the observation was made.
    # The following one must contain a capture from after that observation.
    for _ in range(2):
        before = sequence()
        wait_until(queue, RECOVERY_TIMEOUT, f"queue a save of {partition}")
        wait_until(
            lambda: sequence() > before, RECOVERY_TIMEOUT, f"new save of {partition}"
        )


def _intent(folder, contract):
    for path in folder.glob("*.intent"):
        intent = json.loads(path.read_text())
        if intent["Offer"]["ContractId"] == contract:
            return path, UUID(intent["Offer"]["AcceptanceOperation"])
    return None


def _commits(journal, operation):
    if not journal.exists():
        return []
    rows = [
        json.loads(line) for line in journal.read_text().splitlines() if line.strip()
    ]
    return [
        row
        for row in rows
        if row.get("ContractOperation") and UUID(row["ContractOperation"]) == operation
    ]


def _run(bench, committed):
    bench.start_cluster(nodes=[*REGULAR, "wa-1"])
    if committed:
        bench.record(
            "config-sync-deposit",
            bench.cluster.handover_config({"store.asyncPayments": "false"}),
        )
    ledger = _Ledger(bench)
    poster = _identity(bench, "ContractRestartPoster")
    bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    origin, partition = wait_until(
        lambda: _client_partition(bench, CLIENT_ID),
        RECOVERY_TIMEOUT,
        "player partition",
    )
    bench.cluster.post("/automatic/pause")
    try:
        _disable_contract_fixture_trash(ledger)
        payer = int(
            ledger.node(origin, f"/respawn-test-player?userId={CLIENT_ID}")[
                "identityId"
            ]
        )
        starting = ledger.balance(payer)
        _require(bench, "payer's opening balance is known", isinstance(starting, int))
        ledger.node(
            origin, f"/banking-test-change?ownerId={payer}&amount={FUNDS}", "POST"
        )
        balance = starting + FUNDS
        wait_until(
            lambda: ledger.balance(payer) == balance, RECOVERY_TIMEOUT, "funded payer"
        )
        _, start, sp = _block_grid(bench, ledger, poster, 900000, origin, anyone=True)
        _, end, ep = _block_grid(bench, ledger, poster, 930000, origin, anyone=True)
        posted = ledger.node(
            origin,
            f"/contract-test-post-hauling?block={start}&end={end}&reward={REWARD}"
            f"&collateral={COLLATERAL}&minutes=60",
            "POST",
        )
        _require(bench, "courier posted", posted.get("success") is True, str(posted))
        contract = int(posted["contractId"])
        query = f"&userId={CLIENT_ID}&contract={contract}"
        path = "/contract-test-rpc?op="

        def inventory(node):
            return ledger.node(
                node, path + "status" + query + f"&block={start}", "POST"
            )

        def authority(reserved, active):
            row = ledger.active(ledger.wa, contract, payer)
            if row.get("reserved") is reserved and row.get("active") is active:
                return row
            return None

        before = inventory(origin)
        character = before["character"]
        _require(
            bench,
            "the original character starts with no package for this contract",
            character != 0
            and before["identity"] == payer
            and before["packagesAfter"] == 0,
            str(before),
        )
        for part in {partition, sp, ep}:
            _capture(bench, origin, part)

        if committed:
            ledger.node(
                origin, f"/contract-test-hold-confirm?contract={contract}", "POST"
            )
        else:
            ledger.node(
                origin,
                "/test-drop-command-responses?subsystem=contracts&count=100",
                "POST",
            )

        queued = ledger.node(
            origin, path + "queue-accept" + query + f"&block={start}", "POST"
        )
        _require(
            bench,
            "queued the attached player's acceptance",
            queued.get("queued") == contract,
            str(queued),
        )
        if committed:
            wait_until(
                lambda: ledger.node(origin, "/contract-test-hold-confirm")["held"] > 0,
                RECOVERY_TIMEOUT,
                "the outbound confirmation was held before transmission",
            )
        else:
            wait_until(
                lambda: ledger.node(origin, "/test-drop-command-responses")["dropped"]
                > 0,
                RECOVERY_TIMEOUT,
                "the origin lost the reserve response",
            )
        pending = wait_until(
            lambda: authority(True, False),
            RECOVERY_TIMEOUT,
            "WA reserved the inactive contract",
        )
        data = bench.cluster.config.node_data(_nodes(bench)[origin]) / "cluster"
        intent, operation = wait_until(
            lambda: _intent(data / "contract-accepts", contract),
            RECOVERY_TIMEOUT,
            "durable origin intent",
        )
        journal = data / "store-payments.jsonl"
        if committed:
            records = _commits(journal, operation)
            _require(
                bench,
                "the synchronous payment journal durably closed the collateral reserve",
                [row["Op"] for row in records] == ["closed"]
                and records[0]["ReserveId"] != "00000000-0000-0000-0000-000000000000",
                str(records),
            )
        local = inventory(origin)
        expected_packages = int(committed)
        expected_balance = balance - (COLLATERAL if committed else 0)
        _require(
            bench,
            "the crash gate caught the expected package and payment decision",
            local["character"] == character
            and local["packagesAfter"] == expected_packages
            and bool(_commits(journal, operation)) == committed,
            str(local),
        )
        wait_until(
            lambda: ledger.balance(payer) == expected_balance,
            RECOVERY_TIMEOUT,
            "pre-crash balance",
        )
        if committed:
            _capture(bench, origin, partition)
        _require(
            bench,
            "WA is still reserved at the crash boundary",
            bool(authority(True, False)),
        )
        bench.record(
            "before-origin-crash",
            {
                "authority": pending,
                "local": local,
                "balance": expected_balance,
                "intent": json.loads(intent.read_text()),
                "payment": _commits(journal, operation),
                "playerPartition": partition,
            },
        )

        _restart(bench, origin, partition)
        settled = wait_until(
            lambda: authority(False, committed),
            RECOVERY_TIMEOUT,
            "WA recovered the origin's decision",
        )
        wait_until(
            lambda: not intent.exists() and not intent.with_suffix(".release").exists(),
            RECOVERY_TIMEOUT,
            "origin removed the resolved intent",
        )
        _require(
            bench,
            "WA lists exactly the committed owner",
            settled["owners"] == ([payer] if committed else []),
            str(settled),
        )
        bench.kill_client(CLIENT_ID)
        bench.start_client(CLIENT_ID, label="contract-origin-reconnected")
        bench.wait_spawned(CLIENT_ID)
        serving = wait_until(
            lambda: _client_node(bench, CLIENT_ID),
            RECOVERY_TIMEOUT,
            "reattached player",
        )
        after = inventory(serving)
        _require(
            bench,
            "same recovered character has the expected number of matching packages",
            after["identity"] == payer
            and after["character"] == character
            and after["packagesAfter"] == expected_packages,
            str(after),
        )
        _require(
            bench,
            "recovery preserved the payer's exact balance",
            ledger.balance(payer) == expected_balance,
        )
        _require(
            bench,
            "durable payment decision still matches the crash gate",
            bool(_commits(journal, operation)) == committed,
        )
        bench.record(
            "after-origin-restart",
            {"authority": settled, "local": after, "balance": ledger.balance(payer)},
        )
        bench.exercised(
            contract=contract,
            origin=origin,
            served=serving,
            acceptance=str(operation),
            committed=committed,
        )
    finally:
        bench.cluster.post("/automatic/resume")


def p3_con_origin_release(bench):
    _run(bench, committed=False)


def p3_con_origin_confirm(bench):
    _run(bench, committed=True)


def p3_con_old_authority(bench):
    """SE-1909: a World Authority that does not know Reserve (a mixed-version deploy) answers a player's acceptance.

    The origin must give up on the reservation at once, tell the player, leave no intent behind and keep the offer
    open, so the same player can take it once the authority understands the request.
    """
    # Read at call time: a rig may set the slot's client id after import.
    client = scenarios_contracts.CLIENT_ID
    bench.start_cluster(nodes=[*REGULAR, "wa-1"])
    ledger = _Ledger(bench)
    poster = _identity(bench, "ContractOldAuthorityPoster")
    bench.start_client(client)
    bench.wait_spawned(client)
    origin, _ = wait_until(
        lambda: _client_partition(bench, client),
        RECOVERY_TIMEOUT,
        "player partition",
    )
    bench.cluster.post("/automatic/pause")
    refuse = "/contract-test-refuse-kind"
    try:
        _disable_contract_fixture_trash(ledger)
        payer = int(
            ledger.node(origin, f"/respawn-test-player?userId={client}")["identityId"]
        )
        starting = ledger.balance(payer)
        _require(bench, "payer's opening balance is known", isinstance(starting, int))
        ledger.node(
            origin, f"/banking-test-change?ownerId={payer}&amount={FUNDS}", "POST"
        )
        balance = starting + FUNDS
        wait_until(
            lambda: ledger.balance(payer) == balance, RECOVERY_TIMEOUT, "funded payer"
        )
        _, start, _ = _block_grid(bench, ledger, poster, 900000, origin, anyone=True)
        _, end, _ = _block_grid(bench, ledger, poster, 930000, origin, anyone=True)
        posted = ledger.node(
            origin,
            f"/contract-test-post-hauling?block={start}&end={end}&reward={REWARD}"
            f"&collateral={COLLATERAL}&minutes=60",
            "POST",
        )
        _require(bench, "courier posted", posted.get("success") is True, str(posted))
        contract = int(posted["contractId"])
        query = f"&userId={client}&contract={contract}&block={start}"

        def player(op):
            return ledger.node(origin, "/contract-test-rpc?op=" + op + query, "POST")

        def replies(count):
            state = player("status")
            return state if state["acceptReplies"] >= count else None

        def granted(state):
            return state if state and state["contract"].get("active") else None

        player("watch-accept-replies")
        intents = (
            bench.cluster.config.node_data(_nodes(bench)[origin])
            / "cluster"
            / "contract-accepts"
        )
        ledger.node(ledger.wa, refuse + "?kind=Reserve", "POST")
        try:
            player("queue-accept")
            refused = wait_until(
                lambda: replies(1),
                60,
                "the player hears back from the refused reservation",
            )
            _require(
                bench,
                "the player gets one refusal and no package",
                refused["acceptReplies"] == 1
                and refused["packagesAfter"] == 0
                and not refused["contract"]["active"],
                str(refused),
            )
            _require(
                bench,
                "the origin named the unsupported request",
                _node_line(
                    bench,
                    origin,
                    f"contract_ledger_unsupported kind=Reserve id={contract}",
                )
                is not None,
            )
            wait_until(
                lambda: not any(intents.glob("*.intent"))
                and not any(intents.glob("*.release")),
                RECOVERY_TIMEOUT,
                "the origin removed the refused intent",
            )
            row = ledger.active(ledger.wa, contract, payer)
            _require(
                bench,
                "the authority neither reserved nor activated the offer",
                not row.get("reserved") and not row.get("active"),
                str(row),
            )
        finally:
            ledger.node(ledger.wa, refuse + "?kind=", "POST")

        player("queue-accept")
        accepted = wait_until(
            lambda: granted(replies(2)),
            RECOVERY_TIMEOUT,
            "the same player takes the offer once the authority knows Reserve",
        )
        _require(
            bench,
            "the second click wins the offer with one package",
            accepted["acceptReplies"] == 2 and accepted["packagesAfter"] == 1,
            str(accepted),
        )
        wait_until(
            lambda: ledger.active(ledger.wa, contract, payer).get("active") is True,
            RECOVERY_TIMEOUT,
            "the authority lists the accepted contract",
        )
        wait_until(
            lambda: ledger.balance(payer) == balance - COLLATERAL,
            RECOVERY_TIMEOUT,
            "the collateral was charged once",
        )
        bench.exercised(contract=contract, origin=origin)
    finally:
        bench.cluster.post("/automatic/resume")


SCENARIOS_CONTRACT_ORIGIN = {
    "P3-CON-ORIGIN-RELEASE": (
        p3_con_origin_release,
        "Release an ungranted courier after an origin crash",
        "3",
    ),
    "P3-CON-ORIGIN-CONFIRM": (
        p3_con_origin_confirm,
        "Confirm a committed courier after an origin crash",
        "3",
    ),
    "P3-CON-OLD-AUTHORITY": (
        p3_con_old_authority,
        "SE-1909: a player acceptance refused by an authority without Reserve",
        "3",
    ),
}
