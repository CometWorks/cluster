"""SE-1487: the admin API removes an offline player from the cluster world (DELETE /admin/v1/players/{userId}).

* ``P2-PLAYER-FORGET`` - node-1, node-2 and WA, no client. Two identities the WA maps to Steam IDs stand for old
  sebot traders. FORGET gets a bank account, founds a faction, gets a Scripter promotion through the admin API, and
  all of that reaches every role. BUILDER built an 8-block grid on node-1.

  1. BUILDER: the forget is refused with ``player_has_blocks`` (the PCU ledger reports node-1's grid to the WA), and
     the identity stays everywhere.
  2. FORGET: the forget succeeds; the identity, its bank account, its faction (it was the only member) and its
     promotion entry are gone on node-1, node-2 and the WA, and the moderation list no longer names it.
  3. The same Idempotency-Key replays the same operation; a new key on the forgotten player succeeds with nothing
     left to remove.
"""

from __future__ import annotations

import json
import random
import time

from cluster import ClusterHttpError, wait_until
from scenarios_wa_ops import _poll, _require

ROLES = ("node-1", "node-2", "wa-1")
STEAM = {"forget": 76561190000014871, "builder": 76561190000014872}


def _admin(bench, method: str, path: str, key: str | None = None, body: dict | None = None) -> dict:
    """The admin envelope's data, also for a refused call (its error body)."""
    control = bench.cluster.config.gateway_control
    try:
        return bench.cluster._request(method, f"/admin/v1{path}", control, 120, body=body,
                                      headers={"Idempotency-Key": key} if key else None)["data"]
    except ClusterHttpError as error:
        try:
            return json.loads(error.body).get("data") or json.loads(error.body)
        except (ValueError, AttributeError):
            return {"httpError": str(error)[:300]}


def _forget(bench, steam: int, key: str) -> dict:
    op = _admin(bench, "DELETE", f"/players/{steam}", key=key, body={})
    if op.get("operationId") and op.get("state") == "Running":
        op = wait_until(lambda: (lambda d: d if d.get("state") != "Running" else None)(
            _admin(bench, "GET", f"/operations/{op['operationId']}")), 60, f"forget {steam} done")
    return op


def _view(bench, role: str, identity: int, tag: str, steam: int) -> dict:
    return {
        "identity": bench.cluster.node(role, f"/identity-test-status?identityId={identity}").get("exists"),
        "bank": bench.cluster.node(role, f"/banking-test-status?ownerId={identity}").get("exists"),
        "faction": bench.cluster.node(role, f"/faction-test-status?tag={tag}").get("members"),
        "promoted": bench.cluster.node(role, f"/promote-test?userId={steam}").get("promoted"),
    }


def p2_player_forget(bench) -> None:
    bench.start_cluster(nodes=list(ROLES))
    tag = f"F{random.randrange(100, 999)}"
    ids = {}
    for name, steam in STEAM.items():
        made = bench.cluster.node("wa-1", f"/bot-ship-test-identity?name=Trader-{name}&steamId={steam}", "POST")
        _require(bench, f"setup: the WA maps {name} to its Steam ID", made.get("steamId") == steam and made.get("npc") is False,
                 json.dumps(made))
        ids[name] = int(made["identityId"])
    forget = ids["forget"]
    bench.record("identities", ids)
    bench.cluster.node("wa-1", f"/banking-test-create?ownerId={forget}&balance=4242", "POST")
    bench.record("faction", bench.cluster.node("node-1", f"/faction-test-create?founderId={forget}&tag={tag}&name=Forget-{tag}", "POST"))
    promoted = _admin(bench, "PUT", f"/players/{STEAM['forget']}/promotion", key=f"se1487-promote-{tag}",
                      body={"level": "Scripter", "expectedRevision": 0})
    bench.record("promotion", promoted)
    before = {}
    _require(bench, "setup: identity, bank account, faction membership and promotion reached every role", _poll(
        lambda: before.update({role: _view(bench, role, forget, tag, STEAM["forget"]) for role in ROLES}) or all(
            v["identity"] and v["bank"] and forget in (v["faction"] or []) and v["promoted"] == 1 for v in before.values()), 90),
        json.dumps(before))
    bench.record("before", before)

    # 1. A player with built blocks on a loaded grid stays.
    grid = bench.cluster.node("node-1", f"/trash-test-spawn?x=700000&y=0&z=0&blocks=8&builtBy={ids['builder']}", "POST")
    bench.record("builder-grid", grid)
    time.sleep(5)  # node-1 reports its block usage to the WA once a second
    refused = _forget(bench, STEAM["builder"], f"se1487-builder-{tag}")
    bench.record("builder-forget", refused)
    code = (refused.get("error") or {}).get("code")
    bench.check("a player with built blocks is refused with player_has_blocks", refused.get("state") == "Failed"
                and code == "player_has_blocks", json.dumps(refused)[:400])
    bench.check("the refused player's identity stays on every role", all(
        bench.cluster.node(role, f"/identity-test-status?identityId={ids['builder']}").get("exists") for role in ROLES))

    # 2. Forget.
    key = f"se1487-forget-{tag}"
    done = _forget(bench, STEAM["forget"], key)
    bench.record("forget", done)
    result = done.get("result") or {}
    bench.check("the forget succeeds and names what it removed", done.get("state") == "Succeeded"
                and result.get("identityId") == str(forget) and result.get("identityRemoved") == "True"
                and result.get("promotionRemoved") == "True", json.dumps(done)[:500])
    after = {}
    gone = _poll(lambda: after.update({role: _view(bench, role, forget, tag, STEAM["forget"]) for role in ROLES}) or all(
        not v["identity"] and not v["bank"] and forget not in (v["faction"] or []) and v["promoted"] == 0 for v in after.values()), 60)
    bench.record("after", after)
    bench.check("identity, bank account, faction membership and promotion are gone on every role", bool(gone), json.dumps(after))
    moderation = _admin(bench, "GET", "/players/moderation")
    bench.check("the moderation list no longer names the player",
                all(entry.get("userId") != STEAM["forget"] for entry in moderation.get("promotions") or []),
                json.dumps(moderation)[:400])

    # 3. Replay and repeat.
    replay = _forget(bench, STEAM["forget"], key)
    bench.check("the same Idempotency-Key replays the same operation", replay.get("operationId") == done.get("operationId")
                and replay.get("state") == "Succeeded", json.dumps(replay)[:300])
    again = _forget(bench, STEAM["forget"], key + "-again")
    bench.record("forget-again", again)
    bench.check("a new forget of the removed player succeeds with nothing left to remove", again.get("state") == "Succeeded"
                and (again.get("result") or {}).get("identityRemoved") == "False", json.dumps(again)[:400])
    bench.exercised(forget=forget, builder=ids["builder"], operation=done.get("operationId"))


SCENARIOS_PLAYER_FORGET = {
    "P2-PLAYER-FORGET": (p2_player_forget, "SE-1487: the admin API forgets an offline player: identity, bank, faction, promotion gone everywhere; built blocks refuse", "3"),
}
