"""Cluster-wide player presence: the member mirror (T-0178) and the announcements (T-0179).

``P2-PRS-01`` is the regression test for a pair of claims that a cluster makes about who is here,
and that nothing else in the suite checks:

* **T-0178** - every node shows every cluster player in its clients' member list, not just the
  players attached to that node;
* **T-0179** - a login or a logout is announced exactly ONCE to the whole cluster, and a node
  traversal is announced not at all.

The traversal half is the one that needs care, because it is a test that can pass for the wrong
reason in two different ways, and both have happened:

1. **Reading a zero from a probe with no coverage.** "No presence line appeared" is also what a
   broken cursor, a renamed line or a cluster with no chat path produces. So the scenario proves
   the detector works on the SAME cursor first - the login it just performed must be found - before
   it is allowed to conclude anything from an absence.
2. **Asking before the answer could have arrived.** ``ClusterPresenceAnnouncer`` holds a departure
   for ``CLUSTER_PRESENCE_GRACE`` seconds (default 5) so that a handover gap can cancel it, which
   means an assertion made immediately after the traversal would find silence no matter what the
   code did. ``GRACE_MARGIN_SECONDS`` is what the scenario waits out, and it must stay above that
   grace; the final logout check then proves the hold releases rather than swallows.

Why the traversal needs suppressing at all: the deltas are keyed by (SteamId, SerialId) and not by
node, so a traversal usually raises nothing. USUALLY - on w32, 2026-09-19, 6 of 32 traversals had
the source node stop reporting the player before the target started, and the whole cluster was told
that somebody who never went anywhere left and came back, 67 ms to 1.03 s apart. That is what the
grace period exists for and it is what the middle of this scenario measures.

SE-1081: only players admitted through Steam are announced; Direct Transport clients are bots. Every
bench client joins through Direct Transport, so ``P2-PRS-01`` turns their announcements back on with
``CLUSTER_PRESENCE_DIRECT=1`` on the World Authority, and ``P2-PRS-03`` checks the default: a Direct
Transport login and logout say nothing.
"""

from __future__ import annotations

import json
import time

from cluster import ClusterError, wait_until
from scenarios_core import CLIENT_ID, _client_partition, _handover_url, _lines, _nodes, _partitions
from scenarios_wa import CLIENT2_ID, CLIENT3_ID, CLIENT3_PORT, start_second_client

#: Waited out before concluding that a traversal announced nothing. Must exceed the announcer's
#: CLUSTER_PRESENCE_GRACE (default 5 s), or the silence proves only that we asked too early.
GRACE_MARGIN_SECONDS = 12

#: A departure may not be announced before the grace has run; anything faster is the hold missing.
MIN_DEPARTURE_DELAY_SECONDS = 3

#: OnlinePlayersService.SeedingWindowMilliseconds (the 30 s report expiry), plus a margin.
SEEDING_WINDOW_SECONDS = 35


def _chat(bench, node_id: str, cursor: int = 0) -> tuple[list[dict], int]:
    """The global chat tail after ``cursor``, and the cursor to use next time."""
    page = bench.cluster.node(node_id, f"/chat-history?cursor={cursor}&limit=500")
    return list(page.get("messages") or []), int(page.get("nextCursor") or cursor)


def _presence(messages: list[dict], name: str) -> list[str]:
    """Just the presence lines about ``name``, in order, as "joined"/"left"."""
    out = []
    for message in messages:
        for kind in ("joined", "left"):
            if message.get("text") == f"{name} {kind} the cluster":
                out.append(kind)
    return out


def _cluster_players(bench, node_id: str) -> list[dict]:
    return list(bench.cluster.node(node_id, "/online-players").get("cluster") or [])


def _targets(bench, node_id: str) -> list[int]:
    """The node's own clients the mirror emits to. Empty means the mirror has nowhere to put a
    remote player, so a mirror assertion on this node is measuring nothing."""
    return list(bench.cluster.node(node_id, "/player-mirror").get("clients") or [])


def _mirrored_keys(bench, node_id: str) -> set[tuple[int, int]]:
    """Every (steamId, serialId) this node has installed on its clients.

    ONE SET PER NODE since T-0204 finding 1: the install events are [Broadcast] sites, so every
    attached client receives every emit and a per-client set would be the same set repeated. The
    rule about local players is a node-level question - is this key in the set at all - which is
    the form the service enforces it in."""
    status = bench.cluster.node(node_id, "/player-mirror")
    return {(entry["steamId"], entry["serialId"]) for entry in status.get("mirrored") or []}


def _mirror_passes(bench, node_id: str) -> int:
    """Completed reconcile passes on this node, for ordering a read after one."""
    return int(bench.cluster.node(node_id, "/player-mirror").get("passes") or 0)


def _self_announced(bench, name: str) -> bool | None:
    """True once the client logged its own member message after its session loaded (SE-736)."""
    # The newest log only: the client folder keeps the logs of earlier runs.
    logs = sorted(bench.run_root.glob(f"client-{CLIENT_ID}/home/.config/SpaceEngineers/SpaceEngineers_*.log"),
                  key=lambda path: path.stat().st_mtime)
    for log in logs[-1:]:
        text = log.read_text(errors="replace")
        # The session is up from "Updating continues."; the log masks the id, so match the name.
        loaded = text.rfind("Updating continues.")
        if loaded >= 0 and f"Player connected: {name} (" in text[loaded:]:
            return True
    return None


def p2_prs_01(bench) -> None:
    """One announcement per login, none per traversal, and every node sees every cluster player."""
    record = {}
    try:
        # The bench clients join through Direct Transport, which the announcer skips by default (SE-1081).
        bench.cluster.config.node_env_by_id.setdefault("wa-1", {})["CLUSTER_PRESENCE_DIRECT"] = "1"
        bench.start_cluster(nodes=["node-1", "node-2", "wa-1"])
        # Taken BEFORE the client joins, so the join line has to fall inside the window we read.
        # A scenario that reads the whole history instead would pass on a line left by an earlier
        # scenario in the same run.
        _, cursor = _chat(bench, "wa-1")
        # The World Authority announces nothing for 30 s after its first report (OnlinePlayersService's
        # seeding window). With warm plugin caches the client logs in inside it and the join line never
        # comes (2026-10-28: 2 of 3 runs), so wait the window out.
        time.sleep(SEEDING_WINDOW_SECONDS)

        # -- the login is announced exactly once ------------------------------------------------
        api = bench.start_client(CLIENT_ID)
        bench.wait_spawned(CLIENT_ID)
        player = wait_until(lambda: next(iter(_cluster_players(bench, "wa-1")), None),
                            60, "the joined client to appear in the cluster player list")
        name = player["name"]
        messages, cursor = wait_until(
            lambda: (lambda page: page if _presence(page[0], name) else None)(_chat(bench, "wa-1", cursor)),
            60, f"the cluster to announce {name} joining")
        joins = _presence(messages, name)
        record["login"] = {"player": player, "lines": joins}
        # ONE line, not one per node: every node holds the merged list and would raise the same
        # delta, so a second line here means the World-Authority-only guard has gone.
        bench.check("a login is announced exactly once cluster-wide",
                    joins == ["joined"], json.dumps(joins))

        # -- SE-736: the node sends the fresh client its own member message after the session loaded.
        # The packet makes the client take the id it gets server packets from as a client
        # (MyMultiplayerClient.OnClientConnected -> Sync.ClientConnected(packet.Sender)): without it a
        # Steam client through gateway-rs dropped every secure mod message while it was alone.
        try:
            announced = bool(wait_until(lambda: _self_announced(bench, name), 30,
                                        "the client to log its own member message after loading"))
        except ClusterError:
            announced = False
        record["selfAnnounced"] = {
            "client": announced,
            "node1": bench.cluster.node("node-1", "/player-mirror").get("selfAnnouncedTotal"),
        }
        bench.check("a fresh client gets its own member message after its session loaded (SE-736)",
                    record["selfAnnounced"]["client"] and (record["selfAnnounced"]["node1"] or 0) >= 1,
                    json.dumps(record["selfAnnounced"]))

        # -- T-0178: node-2 mirrors a player who is attached to node-1 ---------------------------
        key = (player["steamId"], player["serialId"])
        bench.check("the player is attached to node-1", player["node"] == "node-1", json.dumps(player))
        # node-2 NEEDS A CLIENT OF ITS OWN before it can mirror anything onto one. The mirror
        # installs remote players on the node's connected clients
        # (RemotePlayerMirrorService.Targets = Sync.Clients with replication data), so on a node
        # with no clients Mirrored stays empty by construction and this wait could never succeed.
        # Without this the leg timed out after 60 s on every run it has ever had (2026-09-19).
        start_second_client(bench, route_node="node-2")
        # Chat-ready is NOT the same as mirror-ready: the mirror's targets are the node's clients
        # that already carry replication data, which arrives a moment after admission. Sampled
        # instantly this read comes back [] and fails a scenario whose mirror then works fine
        # (observed 2026-09-19), so wait for the target the way every other coverage gate here does.
        targets = wait_until(lambda: _targets(bench, "node-2") or None,
                             60, "node-2 to have a client the mirror can install onto")
        bench.check("node-2 has a client to mirror onto", bool(targets), json.dumps(targets))
        seen_on_2 = wait_until(lambda: key in _mirrored_keys(bench, "node-2") or None,
                               60, "node-2 to mirror the cluster player onto its own clients")
        bench.check("a node mirrors cluster players that are not its own", bool(seen_on_2))
        # The mirror must not install a player onto their own node's clients - they are already a
        # real member there, and a mirrored duplicate is what T-0178 set out to avoid.
        #
        # THIS RULE NEEDS TWO CLIENTS ON ONE NODE, which is why a third client appears here. Asked
        # of node-1 and its own player, or of node-2 and the second client, the question is decided
        # by the per-target self guard - a key is never in its own client's list - and the check
        # was green whether or not the rule existed (T-0204). Client 3 is local to node-2 and is a
        # DIFFERENT client from client 2, so only the rule can keep it out of client 2's list.
        # ON NODE-1, NOT NODE-2, and in a partition of its own. Putting the third client on node-2
        # left node-2 with two clients and cost the traversal leg below its coverage: node-2 emitted
        # no "traversal join toast suppressed" line at all, where it emits two in every passing run.
        # Arm A settled which half was responsible - the OLD package fails the same way with this
        # scenario, so it is the extra client on node-2, not any product change (2026-09-20 19:07).
        # node-1 is the better home anyway: the rule needs two clients on ONE node, the player is
        # already here, and node-2 keeps the single-client shape the rest of the scenario was
        # written against. A clientless partition keeps it out of the handover below.
        # INTO THE PLAYER'S OWN PARTITION. node-1 has no clientless partition left at this point -
        # the one it had was handed to node-2 to give the second client somewhere to land - so
        # asking for a spare fails outright (measured 19:11). Sharing the player's partition is
        # what the rule wants anyway: two clients on one node, in one partition, where the mirror
        # has to decide about a player who is local to both of them. The handover below then moves
        # both clients, which the gate reads as at least as many suppressed toasts, never fewer.
        local_partition = _client_partition(_partitions(bench, "node-1"))
        if local_partition is None:
            raise ClusterError("the client's partition was not reported before the third client")
        start_second_client(bench, route_node="node-1", partition=local_partition,
                            client_id=CLIENT3_ID, remote_port=CLIENT3_PORT)
        key3 = wait_until(lambda: next(((row["steamId"], row["serialId"])
                                        for row in _cluster_players(bench, "wa-1")
                                        if row["steamId"] == CLIENT3_ID), None),
                          60, "the third client to reach the cluster player list")
        # CONTROL FIRST: the same key has to arrive SOMEWHERE, or the absence below is the absence
        # of a working mirror rather than the rule doing its job. node-1's client is off-node for
        # client 3, so it must receive it.
        seen_off_node = wait_until(lambda: key3 in _mirrored_keys(bench, "node-2") or None,
                                   60, "node-2 to mirror the third client onto its clients")
        bench.check("the third client is mirrored onto the other node's client (control)", bool(seen_off_node))
        # ORDER THE READ AFTER THE INSTALL PASS THAT WOULD HAVE BROKEN THE RULE. An absence proves
        # nothing if it is read too early, and the first version of this check was exactly that: it
        # waited on NODE-2's mirror and then read node-1, so a node-1 install that had not happened
        # yet looked like the rule working. Measured 2026-09-20 19:20 - the falsifier build, with
        # the rule deleted, PASSED. node-1 installs its whole desired set in one pass, so waiting
        # for the second client's key (genuinely remote, and installed by that same pass) puts the
        # read after the pass that would also have installed the local one.
        # THE ORDERING THIS CHECK ACTUALLY NEEDS, and the second attempt at it. Waiting on the
        # off-node client's key was not enough: that install can happen before node-1's own merged
        # list has the third client in it at all, and an install pass that cannot see the local
        # player cannot break the rule either. Measured on the falsifier build (rule deleted):
        # 2 runs PASSED, the third FAILED, with the diagnostic showing node-1 mirroring BOTH local
        # players onto each other's clients - so the rule is real and the read was racing it.
        #
        # The precondition is therefore two facts, not one: node-1's own list knows the third
        # client AND attributes it to node-1, and node-1 has completed an install pass for the
        # third client as a target. The second is what pins the ordering - a pass that installs
        # anything onto client 3 was computed from a snapshot that already contained client 3, and
        # that same pass is the one which, without the rule, installs client 3 onto client 1.
        wait_until(lambda: any(row["steamId"] == CLIENT3_ID and row.get("node") == "node-1"
                               for row in _cluster_players(bench, "node-1")) or None,
                   60, "node-1's own cluster list to carry the third client as local")
        key2 = next(((row["steamId"], row["serialId"]) for row in _cluster_players(bench, "wa-1")
                     if row["steamId"] == CLIENT2_ID), None)
        if key2 is None:
            raise ClusterError("the second client was not in the cluster list before the local-mirror read")
        wait_until(lambda: key2 in _mirrored_keys(bench, "node-1") or None,
                   60, "node-1 to mirror the off-node client onto its clients")
        # AND THEN A WHOLE PASS THAT RAN AFTER ALL OF THAT. Waiting on facts alone still left a
        # sub-second window: the merged list can acquire the third client between the pass that
        # installs the off-node one and this read, and a pass that cannot see the local player
        # cannot break the rule either. Measured on the falsifier build with the rule deleted, the
        # old check PASSED 3 runs out of 4 for exactly that reason. The service counts its passes,
        # so the read is ordered after one instead of after a sleep nobody can justify: two, so
        # that the first may be the one already in flight.
        first_pass = _mirror_passes(bench, "node-1")
        wait_until(lambda: _mirror_passes(bench, "node-1") >= first_pass + 2 or None,
                   60, "node-1 to complete two reconcile passes with the third client in its list")
        on_client1 = _mirrored_keys(bench, "node-1")
        # THE WHOLE OF NODE-1'S STATE, PASS OR FAIL. A green nobody recorded is a green nobody can
        # falsify later, and this check has already been wrong once in the quiet direction. These
        # reads are what let a future failure be diagnosed from the run that produced it:
        # node-1's own merged list (does it even carry the third client, and under which node?),
        # its local report (what it publishes about itself), and every target's mirrored set (was
        # the install attempted onto some other client and only missing here?). Recorded in the
        # result either way, because a green that is recorded is what a later falsification reads.
        mirror_status = bench.cluster.node("node-1", "/player-mirror")
        online_status = bench.cluster.node("node-1", "/online-players")
        record["localMirrorDiagnostic"] = {
            "node1Mirror": mirror_status,
            "node1ClusterList": [{"steamId": row.get("steamId"), "serialId": row.get("serialId"),
                                  "node": row.get("node")} for row in online_status.get("cluster") or []],
            "node1LocalReport": [{"steamId": row.get("steamId"), "serialId": row.get("serialId")}
                                 for row in online_status.get("local") or []],
        }
        record["localMirror"] = {"client3": list(key3), "onNode1": sorted(f"{s}/{i}" for s, i in on_client1)}
        bench.check("a node does not mirror the players local to it",
                    key3 not in on_client1, json.dumps(record["localMirror"]))
        # THE THIRD CLIENT STAYS UNTIL THE SCENARIO ENDS. Killing it here cost the traversal leg its
        # coverage: node-2 emitted no "traversal join toast suppressed" line at all on 2026-09-20
        # 18:57 (passing runs emit exactly two), and the gate that reads it timed out. Whether the
        # kill removed the announcement or merely re-ordered it, a disconnect a few seconds before
        # the traversal is a variable this scenario never had, and the leg below is about the
        # traversal. The bench's own teardown takes the client down with the rest.

        # -- T-0179: the traversal announces nothing ---------------------------------------------
        partition = _client_partition(_partitions(bench, "node-1"))
        if partition is None:
            raise ClusterError("the client's partition was not reported before the traversal")
        suppressed_before = len(_lines(bench, "node-2", r"traversal (join|leave) toast suppressed"))
        record["handover"] = bench.cluster.post(
            _handover_url(bench, partition, client=CLIENT_ID), timeout=300)
        moved = wait_until(
            lambda: next((row for row in _cluster_players(bench, "wa-1")
                          if (row["steamId"], row["serialId"]) == key and row["node"] == "node-2"), None),
            120, "the client to be reported on node-2 after the traversal")
        record["traversal"] = {"partition": partition, "player": moved}
        # COVERAGE FIRST: a traversal that suppressed no toast is a traversal the patch never saw,
        # and the silence below would then be measuring nothing at all.
        suppressed = wait_until(
            lambda: (lambda n: n if n > suppressed_before else None)(
                len(_lines(bench, "node-2", r"traversal (join|leave) toast suppressed"))),
            60, "node-2 to suppress a traversal toast")
        bench.check("the traversal suppressed a vanilla toast",
                    suppressed > suppressed_before, f"{suppressed_before} -> {suppressed}")
        # Only now is an absence worth reading, and only after the hold could have expired.
        time.sleep(GRACE_MARGIN_SECONDS)
        messages, cursor = _chat(bench, "wa-1", cursor)
        during = _presence(messages, name)
        record["traversal"]["lines"] = during
        bench.check("a node traversal announces nothing to the cluster",
                    during == [], json.dumps(during))

        # -- the logout is still announced, once, and not before the grace -----------------------
        left_at = time.monotonic()
        bench.kill_client(CLIENT_ID)
        # 180 s, NOT 120 s, and the number is not padding. Killing the process gives the transport
        # no goodbye, so nobody learns the client is gone until the gateway's own peer timeout -
        # DirectGateway._front.DisconnectTimeout = 120000 - expires. Measured on 2026-09-19: killed
        # at 22:09:14, "Client 1234567890 disconnected: Timeout" in the gateway log at 22:11:14,
        # exactly 120 s later, 0.2 s after a 120 s wait had already given up. A bar set to the same
        # number as the mechanism it waits on can only lose the race.
        messages, cursor = wait_until(
            lambda: (lambda page: page if _presence(page[0], name) else None)(_chat(bench, "wa-1", cursor)),
            180, f"the cluster to announce {name} leaving")
        delay = time.monotonic() - left_at
        leaves = _presence(messages, name)
        record["logout"] = {"lines": leaves, "delaySeconds": round(delay, 2)}
        # The hold must DELAY a departure, never swallow it: a quiet cluster is exactly the case
        # where the last player leaving would otherwise go unannounced forever.
        bench.check("a logout is announced exactly once cluster-wide",
                    leaves == ["left"], json.dumps(leaves))
        # Reads as a floor, not as evidence about the announcer: after a killed client the delay is
        # dominated by the 120 s transport timeout above, so this can no longer tell a working grace
        # from an absent one. It stays because a departure announced instantly would still mean the
        # hold is gone. Nothing here exercises the grace on its own; that needs a clean disconnect,
        # which the bench has no way to ask a client for today.
        bench.check("the departure waited out the announcer's grace",
                    delay >= MIN_DEPARTURE_DELAY_SECONDS, f"{delay:.2f}s")
        bench.exercised(route="node-1 (login) -> node-2 (traversal) -> logout")
    finally:
        bench.record("cluster-presence", record)


def p2_prs_02(bench) -> None:
    """SE-732: a fresh client gets its own member message even when the node sees it before it asks for the world."""
    record = {}
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        # The mirror's pass runs inside the join, before the client's world request. On the live
        # cluster that happened to a few logins in a hundred (one pass a second, the world request
        # 30-50 ms after the join); here it happens to every one.
        record["harness"] = bench.cluster.node("node-1", "/test-early-mirror-pass?on=1", method="POST")
        bench.start_client(CLIENT_ID)
        bench.wait_spawned(CLIENT_ID)
        name = wait_until(lambda: next(iter(_cluster_players(bench, "wa-1")), None),
                          60, "the joined client to appear in the cluster player list")["name"]
        try:
            announced = bool(wait_until(lambda: _self_announced(bench, name), 30,
                                        "the client to log its own member message after loading"))
        except ClusterError:
            announced = False
        harness = bench.cluster.node("node-1", "/test-early-mirror-pass?on=0", method="POST")
        mirror = bench.cluster.node("node-1", "/player-mirror")
        record["result"] = {"client": announced, "earlyPasses": harness.get("forced"),
                            "selfAnnouncedTotal": mirror.get("selfAnnouncedTotal")}
        bench.check("the node ran a mirror pass inside the join", (harness.get("forced") or 0) >= 1,
                    json.dumps(record["result"]))
        bench.check("the client gets its own member message after its session loaded, seen early (SE-732)",
                    announced and (mirror.get("selfAnnouncedTotal") or 0) >= 1, json.dumps(record["result"]))
        bench.exercised(route="node-1 (login, mirror pass inside the join)")
    finally:
        bench.record("early-self-announce", record)


def p2_prs_03(bench) -> None:
    """SE-1081: a Direct Transport client (a bot) is neither announced joining nor leaving the cluster."""
    record = {}
    try:
        bench.start_cluster(nodes=["node-1", "wa-1"])
        selftest = bench.cluster.node("wa-1", "/self-test?section=presence-transport")
        bench.check("self-test presence-transport: Steam announced, Direct Transport not, the flag round-trips",
                    selftest.get("ok") is True, json.dumps(selftest)[:300])
        _, cursor = _chat(bench, "wa-1")
        # Past the seeding window, or the silence below would be the window's and not the rule's.
        time.sleep(SEEDING_WINDOW_SECONDS)
        api = bench.start_client(CLIENT_ID)
        bench.wait_spawned(CLIENT_ID)
        player = wait_until(lambda: next(iter(_cluster_players(bench, "wa-1")), None),
                            60, "the joined client to appear in the cluster player list")
        name = player["name"]
        record["player"] = player
        bench.check("the World Authority's list marks the client as Direct Transport",
                    player.get("directTransport") is True, json.dumps(player))
        # COVERAGE: a chat line the client sends after its login must show up in the same window, so
        # the absence of a join line is read from a chat path that works.
        marker = f"se1081-marker-{int(time.time())}"
        def send() -> bool:
            # Retried only while the send itself fails; a sent line is never sent again.
            try:
                api.send_chat(marker)
                return True
            except Exception:  # noqa: BLE001 - the Remote API is still coming up
                return False
        wait_until(send, 30, "the client to send a chat line")
        messages, cursor = wait_until(
            lambda: (lambda page: page if any(m.get("text") == marker for m in page[0]) else None)(
                _chat(bench, "wa-1", cursor)),
            60, "the client's chat line to reach the World Authority's history")
        joins = _presence(messages, name)
        record["login"] = {"lines": joins, "texts": [m.get("text") for m in messages]}
        bench.check("a Direct Transport login is not announced", joins == [], json.dumps(record["login"]))

        bench.kill_client(CLIENT_ID)
        # The gateway notices a killed client after its 120 s peer timeout (see P2-PRS-01).
        wait_until(lambda: not _cluster_players(bench, "wa-1") or None,
                   180, "the client to leave the cluster player list")
        time.sleep(GRACE_MARGIN_SECONDS)
        messages, cursor = _chat(bench, "wa-1", cursor)
        leaves = _presence(messages, name)
        record["logout"] = {"lines": leaves, "texts": [m.get("text") for m in messages]}
        bench.check("a Direct Transport logout is not announced", leaves == [], json.dumps(record["logout"]))
        bench.exercised(route="node-1 (Direct Transport login, chat, logout)")
    finally:
        bench.record("presence-direct-transport", record)


p2_prs_01.regular_nodes = 2
p2_prs_02.regular_nodes = 1
p2_prs_03.regular_nodes = 1

SCENARIOS_PRESENCE = {
    "P2-PRS-01": (p2_prs_01, "cluster presence: one announcement per login, none per traversal", "3"),
    "P2-PRS-02": (p2_prs_02, "a login's own member message survives a mirror pass before the world request", "2"),
    "P2-PRS-03": (p2_prs_03, "a Direct Transport login and logout are not announced (SE-1081)", "2"),
}
