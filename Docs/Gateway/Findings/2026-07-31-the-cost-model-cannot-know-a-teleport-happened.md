# The cost model cannot know a teleport happened

Date: 2026-07-31. Found by full-sweep P2-CHR-02 / P2-WA-06a / P1-CHR-02 after the §7.5
cost-priced direction chooser (workstream C) replaced the old PCU-majority rule.

## What broke

The teleport is deliberately just a position change on the node - ownership was expected to
follow "through the cluster's own topology machinery": split, then the automatic co-location
moves the resulting client micro-partition to the node owning the destination region, then the
merge absorbs it. That expectation was carried by an accident of the OLD direction rule:
PCU-majority made the weightless character partition (PCU 0) lose to ANY content, so the
character always travelled. The §7.5 cost rule prices a small destination partition (a beacon
crumb, ~400 sector bytes) far below a client switch, so co-location started pulling the crumb
to the player's node instead - and the teleported character never changed node
(`component home source`, `route node-2->node-1` in the gateway log).

Both behaviors are in the plan, for different triggers (Partitioning.md §7.4):

- **Respawn, teleport, admin move, spawn-elsewhere, join:** the character moves to the node
  owning the destination. Destination partitions are never pulled toward the player.
- **Ordinary §7.1 proximity encounters:** the smaller estimated §7.5 cost moves - including a
  crumb travelling to a flying player rather than the player switching nodes.

The chooser sees only partition reports; the trigger is not in them. Respawn/pod/join flows
already carry their own directed routing (pod-placement, respawn arbitration, admission) -
only the teleport relied on the emergent direction.

## The fix: teleport intents

- Node `/cluster/teleport` handler registers the client at the gateway
  (`POST /registry/teleport-intents?client=`) right after the position change - best effort,
  a failure degrades to the priced direction rather than failing the teleport.
- `DirectGateway.HomeIsTarget` consults the pending-intent set FIRST: the side bearing an
  intent client moves, regardless of price. Everything else stays §7.5-priced.
- The intent is consumed when the client-bearing handover lands (left in place if the
  transfer fails, so the retry keeps the direction), and expires after 120 s if no encounter
  ever materializes.
- Self-test pins both directions with the same fixture: intent → character moves to the
  crumb's node; no intent → the crumb travels to the player.

## Rule

A trigger-directed move (§7.4) must carry its direction to the decision point explicitly.
Any policy that "happens to" produce the right direction from unrelated inputs (PCU majority)
is a regression waiting for the policy to get smarter.
