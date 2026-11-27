# Safe zones of safe zone blocks

A safe zone block's zone is a separate entity. The cluster carries it inside the block's object builder, so it
moves with the block through handover, partition saves, offline partitions, recovery and a keep-world deploy
(`BlockSafeZones`). Admin safe zones have no block and are World Authority state (`AdminSafeZones`).

## What the block's builder carries

| Mod storage key | Content | Written from |
|---|---|---|
| `5c1a0231-…` | the zone's builder, to create the zone again where it is missing | the live zone, at every save |
| `5c1a1997-…` | the settings the players set | a client's request that changed the zone |

The second entry holds the access types, the allowed actions, the player, faction and grid lists, the shape,
radius, size, color, texture and visibility. The enabled state is not in it: a zone that is off because the
block ran out of zone chips or power has not lost its settings. A block without the entry takes the live
zone's settings the first time it is checked, so damage done before this was deployed is not detected. A
pasted or blueprinted block starts a new entry.

## Faction lists

Vanilla keeps a zone's faction list as `MyFaction` objects and matches them by reference. A World Authority
faction snapshot is applied by `MyFactionCollectionPatch.ReplaceLocally`, which keeps the object of every
faction the node already had (`KeepObjects`, SE-2006) and then points every zone's list at the current objects
(`SafeZoneFactions.Rebind`), for a faction that is new to the node. A faction id the node cannot
resolve yet stays with the zone and its builder, and joins the list when the faction arrives.

## Log lines

All are written by the node that holds the block, to its game log and its console.

| Line | Meaning |
|---|---|
| `safe zone settings drift block= zone= grid= owner= at= fields=` | The live zone differs from what the players set. `fields` lists each one as `name: set -> live`. `at` is `save`, `restore`, `create`, `factions` or `edit`: where it was noticed. Logged once per node for a given difference; the entry is left as it was, so the next node that holds the block reports it too. |
| `safe zone settings reapplied block= zone= fields=` | Vanilla created the zone again and skipped its kept settings, because the zone collides with another one. The cluster applied them. |
| `safe zone waits for factions zone= block= factions=` | The zone lists faction ids this node does not know. They are kept and resolved later. |
| `safe zone restored block= zone= enabled=` | The zone was created from the block's builder after a handover, an offline load, a recovery or a restart. |
| `safe zone not restored block= zone=: …` | That creation failed. The block keeps its entry. |

A `drift` line means something other than a player changed the zone: a cluster or game defect, or a mod that
updates zones directly. `factionObjects: stale` in `fields` means the faction list holds objects the faction
collection no longer has, which is what a missed rebind looks like.

The regression test is `P3-BLK-05` in `Test/scenarios_blocks.py`.
