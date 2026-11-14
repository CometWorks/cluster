"""SE-1488: the Alt-F10 Economy page changes balances and reputation on the cluster.

* ``P2-ADM-ECONOMY`` - client 1 (an admin) is on node-1 and client 2 on node-2. Through the admin menu's own server
  events (Remote's ``/v1/economy/admin``), client 1 changes its own balance and client 2's, then both players'
  reputation with an NPC faction. Every change must land at the World Authority, on both nodes and on the client
  that owns the account, and the admin must be answered with the new value, which the menu shows as the current
  balance or reputation. Then client 1 is handed over to node-2 and every value must still be there.

Live (octahedron-test, 2026-11-14 01:31Z) LJS asked to take 77,087,664 SC off a balance of 77,087,660 SC and nothing
happened. Vanilla refuses a change that would leave a balance below zero and sends no answer; the menu shows the
final balance clamped to 0, so the refusal looks like a dead button. The first leg checks that the cluster keeps that
refusal. The rest are the changes vanilla accepts.

The bench world has the economy off, so the scenario runs on a copy with it on.
"""

from __future__ import annotations

import json
import re
import shutil
from pathlib import Path

from cluster import ClusterError
from scenarios_chat_names import _medbay_to_node2
from scenarios_core import CLIENT_ID, HANDOVER_TIMEOUT, _client_partition, _handover_url, _join, _nodes, _partitions
from scenarios_wa import CLIENT2_ID, start_second_client
from scenarios_wa_ops import _poll, _require, _session

FACTION_TAG = "GCSL"  # an NPC trader faction of Cluster-Simple
ANSWER_TIMEOUT = 30
CONVERGE_TIMEOUT = 30


def _economy_template(bench) -> Path:
    """Cluster-Simple with the economy on (both settings files)."""
    import bench as benchmod

    source = Path(bench.cluster.config.world_template or benchmod.HOST.package / "examples/Cluster-Simple")
    target = bench.run_root.parent / f"{bench.run_root.name}-templates" / "Cluster-Simple-economy"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = target / name
        new, n = re.subn(
            r"<EnableEconomy>false</EnableEconomy>", "<EnableEconomy>true</EnableEconomy>", path.read_text()
        )
        if n != 1:
            raise ClusterError(f"{name}: no <EnableEconomy>false</EnableEconomy> to replace")
        path.write_text(new)
    return target


def _identity(bench, node_id: str, client_id: int) -> int:
    player = _poll(
        lambda: (lambda p: p if p.get("identityId") else None)(
            bench.cluster.node(node_id, f"/respawn-test-player?userId={client_id}")
        ),
        60,
    )
    _require(bench, f"SETUP: client {client_id} has an identity on {node_id}", player is not None)
    return int(player["identityId"])


def _balance(bench, node_id: str, owner: int):
    status = bench.cluster.node(node_id, f"/banking-test-status?ownerId={owner}")
    return status["balance"] if status.get("exists") else None


def _reputation(bench, node_id: str, owner: int):
    return bench.cluster.node(node_id, f"/faction-test-status?tag={FACTION_TAG}&identityId={owner}").get("reputation")


def _answer(api, since: int, kind: str, owner: int, timeout: float = ANSWER_TIMEOUT):
    return _poll(
        lambda: next(
            (a for a in api.admin_economy_answers(since)["answers"] if a["kind"] == kind and a["identityId"] == owner),
            None,
        ),
        timeout,
    )


def _answered(api, since: int, kind: str, owner: int, key: str, expected: int) -> list[dict]:
    """The admin's answers about ``owner`` once one shows ``expected`` (or the timeout ran out). A node may first
    send vanilla's answer from its replica and then the corrected one; the menu shows the last."""
    answers: list[dict] = []

    def shown():
        answers[:] = [
            a for a in api.admin_economy_answers(since)["answers"] if a["kind"] == kind and a["identityId"] == owner
        ]
        return bool(answers) and answers[-1][key] == expected or None

    _poll(shown, ANSWER_TIMEOUT)
    return answers


def _converged(bench, label: str, read, expected, places: dict) -> None:
    """Every place (name -> zero-argument reader) shows ``expected`` within CONVERGE_TIMEOUT."""
    seen = {}

    def all_there():
        for name, reader in places.items():
            seen[name] = read(reader)
        return all(value == expected for value in seen.values()) or None

    _poll(all_there, CONVERGE_TIMEOUT)
    bench.check(
        f"{label}: {', '.join(places)} show {expected}", all(v == expected for v in seen.values()), json.dumps(seen)
    )


def _change_balance(bench, admin, owner: int, amount: int, expected: int, places: dict, label: str) -> None:
    sent = admin.admin_economy("change-balance", owner, amount)
    answers = _answered(admin, sent["sequence"], "balance", owner, "balance", expected)
    bench.record(f"balance-{label}", {"sent": sent, "answers": answers})
    bench.check(
        f"{label}: the admin is shown the new balance {expected}",
        bool(answers) and answers[-1]["balance"] == expected,
        json.dumps(answers),
    )
    _converged(bench, label, lambda reader: reader(), expected, places)


def _change_reputation(
    bench, admin, owner: int, faction: int, amount: int, expected: int, places: dict, label: str
) -> None:
    sent = admin.admin_economy("change-reputation", owner, amount, faction_id=faction)
    answers = _answered(admin, sent["sequence"], "reputation", owner, "reputation", expected)
    bench.record(f"reputation-{label}", {"sent": sent, "answers": answers})
    bench.check(
        f"{label}: the admin is shown the new reputation {expected}",
        bool(answers) and answers[-1]["reputation"] == expected,
        json.dumps(answers),
    )
    _converged(bench, label, lambda reader: reader(), expected, places)


def p2_adm_economy(bench) -> None:
    bench.cluster.config.world_template = _economy_template(bench)
    api1 = _join(bench)
    _medbay_to_node2(bench)
    api2 = start_second_client(bench, route_node="node-2")
    id1, id2 = _identity(bench, "node-1", CLIENT_ID), _identity(bench, "node-2", CLIENT2_ID)
    for owner, node in ((id1, "node-1"), (id2, "node-2")):
        if _balance(bench, "wa-1", owner) is None:
            bench.cluster.node(node, f"/banking-test-create?ownerId={owner}&balance=1000", "POST")
    start = {}
    for owner in (id1, id2):
        start[owner] = _poll(lambda o=owner: _balance(bench, "wa-1", o), 60)
        _require(bench, f"SETUP: identity {owner} has a bank account at the World Authority", start[owner] is not None)
        _require(bench, f"SETUP: identity {owner} has a positive balance", start[owner] > 0, str(start[owner]))
    faction = bench.cluster.node("wa-1", f"/faction-test-status?tag={FACTION_TAG}").get("factionId") or 0
    _require(bench, f"SETUP: the NPC faction {FACTION_TAG} exists", faction > 0)
    bench.record("setup", {"identities": [id1, id2], "balances": start, "faction": faction})

    def places(owner: int, node: str, api, read_node, read_client) -> dict:
        return {
            "wa-1": lambda: read_node("wa-1", owner),
            node: lambda: read_node(node, owner),
            f"client {api is api1 and 1 or 2}": lambda: read_client(api, owner),
        }

    def node_balance(node, owner):
        return _balance(bench, node, owner)

    def client_balance(api, owner):
        return api.get_balance(owner).get("balance")

    def node_reputation(node, owner):
        return _reputation(bench, node, owner)

    def client_reputation(api, owner):
        return api.get_reputation(faction, owner).get("reputation")

    # The reporter's case: 4 SC more than the balance. Vanilla refuses it and does not answer.
    sent = api1.admin_economy("change-balance", id1, -(start[id1] + 4))
    refused = _answer(api1, sent["sequence"], "balance", id1, timeout=10)
    bench.record("balance-below-zero", {"sent": sent, "answer": refused, "wa": _balance(bench, "wa-1", id1)})
    bench.check(
        "a change below zero is refused as in vanilla: no answer, the balance is unchanged",
        refused is None and _balance(bench, "wa-1", id1) == start[id1],
        json.dumps({"answer": refused, "wa": _balance(bench, "wa-1", id1)}),
    )

    _change_balance(
        bench,
        api1,
        id1,
        -start[id1],
        0,
        places(id1, "node-1", api1, node_balance, client_balance),
        "own balance to exactly 0",
    )
    _change_balance(
        bench,
        api1,
        id2,
        12345,
        start[id2] + 12345,
        places(id2, "node-2", api2, node_balance, client_balance),
        "client 2's balance on node-2 +12345",
    )
    _change_balance(
        bench, api1, id1, 5000, 5000, places(id1, "node-1", api1, node_balance, client_balance), "own balance +5000"
    )

    reputation = {owner: _reputation(bench, "wa-1", owner) for owner in (id1, id2)}
    bench.record("reputation-start", reputation)
    _change_reputation(
        bench,
        api1,
        id1,
        faction,
        100,
        reputation[id1] + 100,
        places(id1, "node-1", api1, node_reputation, client_reputation),
        "own reputation +100",
    )
    _change_reputation(
        bench,
        api1,
        id2,
        faction,
        -100,
        reputation[id2] - 100,
        places(id2, "node-2", api2, node_reputation, client_reputation),
        "client 2's reputation on node-2 -100",
    )

    # The admin is handed over to node-2: every value stays, and the admin reads them there.
    expected = {
        "balance": {id1: 5000, id2: start[id2] + 12345},
        "reputation": {id1: reputation[id1] + 100, id2: reputation[id2] - 100},
    }
    nodes = _nodes(bench)
    partition = _client_partition(_partitions(bench, "node-1"))
    _require(bench, "SETUP: node-1 reports client 1's partition", partition is not None)
    bench.record(
        "handover",
        bench.cluster.post(
            _handover_url(bench, partition, source="node-1", target="node-2", client=CLIENT_ID), timeout=300
        ),
    )
    arrived = _poll(
        lambda: (_session(bench, CLIENT_ID) or {}).get("backend") == nodes["node-2"].backend or None, HANDOVER_TIMEOUT
    )
    _require(bench, "SETUP: client 1 handed over to node-2", bool(arrived))
    for owner, api in ((id1, api1), (id2, api2)):
        _converged(
            bench,
            f"after the handover, identity {owner}'s balance",
            lambda reader: reader(),
            expected["balance"][owner],
            {
                "wa-1": lambda o=owner: _balance(bench, "wa-1", o),
                "node-1": lambda o=owner: _balance(bench, "node-1", o),
                "node-2": lambda o=owner: _balance(bench, "node-2", o),
                "its client": lambda o=owner, a=api: client_balance(a, o),
                "the admin client": lambda o=owner: client_balance(api1, o),
            },
        )
        _converged(
            bench,
            f"after the handover, identity {owner}'s reputation",
            lambda reader: reader(),
            expected["reputation"][owner],
            {
                "wa-1": lambda o=owner: _reputation(bench, "wa-1", o),
                "node-2": lambda o=owner: _reputation(bench, "node-2", o),
                "its client": lambda o=owner, a=api: client_reputation(a, o),
            },
        )
        sent = api1.admin_economy("balance", owner)
        answer = _answer(api1, sent["sequence"], "balance", owner)
        bench.check(
            f"after the handover, the admin on node-2 reads identity {owner}'s balance",
            answer is not None and answer["balance"] == expected["balance"][owner],
            json.dumps(answer),
        )
        sent = api1.admin_economy("reputation", owner, faction_id=faction)
        answer = _answer(api1, sent["sequence"], "reputation", owner)
        bench.check(
            f"after the handover, the admin on node-2 reads identity {owner}'s reputation",
            answer is not None and answer["reputation"] == expected["reputation"][owner],
            json.dumps(answer),
        )
    bench.exercised(
        admin="client 1 on node-1, then node-2", player="client 2 on node-2", faction=FACTION_TAG, identities=[id1, id2]
    )


p2_adm_economy.administrators = [CLIENT_ID]

SCENARIOS_ADMIN_ECONOMY = {
    "P2-ADM-ECONOMY": (
        p2_adm_economy,
        "SE-1488: the admin menu's Economy page changes balances and reputation on any node",
        "2",
    ),
}
