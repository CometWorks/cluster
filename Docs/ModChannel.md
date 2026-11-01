# The cluster mod channel

For rank-filtered server-to-player diagnostics and the Registry/Manager/Quasar contract,
see [Client diagnostics](Gateway/ClientDiagnostics.md). This secure network channel is
separate from the local cross-mod bus below; sensitive player diagnostics are never
broadcast on that shared local bus.

P2-MOD and #1280. A mod cannot reference the cluster plugin: it runs in the script sandbox, and its only way to
talk to the plugin is the cross-mod message bus. The channel is two message ids:

```csharp
const long SendId = 0x434C55534D4F4453;    // "CLUSMODS": the mod sends here
const long ReceiveId = 0x434C55534D4F4452; // "CLUSMODR": the mod listens here

MyAPIGateway.Utilities.RegisterMessageHandler(ReceiveId, OnMessage);              // LoadData
MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "role", "my-channel" }); // first update or later
```

Every message is an `object[]` whose first two items are the kind and the channel name (1 to 64 characters,
your mod's own). Every mod's handler sees every message on `ReceiveId`, so filter on your channel first. Payloads
are strings of at most 16 384 characters; serialize your own data. A channel takes at most 30 sends in any
10 s on one node (broadcast, request, respond, to-node, to-owner, owner-of); the 31st faults.

`Shared/Cluster/ClusterModChannelService.cs` implements it. The reference adaptation of a class D mod is
`Tools/ModCompatScan/TestMods/fixture-d-reference` (the world scanner `fixture-d-scan` adapted); the
`P2-MOD-V1` scenario runs it on a cluster. Docs/ModCompat.md says which mods need the channel (class D).

## Mod to plugin

| Message | What it does |
|---|---|
| `["role", channel]` | answered at once, inside the call: `["role", channel, inCluster, role, node, apiVersion]` |
| `["broadcast", channel, payload]` | to the mods of every other active node, the WA included |
| `["request", channel, payload, correlationId]` | to the World Authority's mods |
| `["respond", channel, payload, correlationId, replyTo]` | back to the node that sent a request |
| `["to-node", channel, payload, node]` | to the mods of one node (v1) |
| `["to-owner", channel, payload, entityId, correlationId]` | to the mods of the node that owns the entity's grid; `correlationId` is optional, its fault carries it (v1) |
| `["owner-of", channel, entityId, correlationId]` | which node owns the entity, answered later as `owner` (v1) |
| `["player-nodes", channel]` | answered at once: the online players and their nodes (v1) |
| `["handover-hooks", channel]` | asks for the handover messages below for this channel (v1) |
| `["handover-state", channel, gridId, state]` | only inside a `before-handover` handler: the state to carry (v1) |

`correlationId` is a non-zero `long` (an `int` or a numeric string also works). `entityId` is a `long`; any entity
of a grid works (a block, a subgrid), the owner is the owner of its top-most grid.

## Plugin to mod

| Message | When |
|---|---|
| `["role", channel, inCluster, role, node, apiVersion]` | answer to `role`: `inCluster` is a `bool`, `role` is `"WA"`, `"Regular"` or `"Standalone"`, `node` the node id, `apiVersion` the `int` 1 |
| `["broadcast", channel, origin, payload]` | another node broadcast |
| `["request", channel, origin, payload, correlationId]` | on the WA: a node's request |
| `["response", channel, origin, payload, correlationId]` | the answer to this node's request |
| `["to-node", channel, origin, payload]` | another node sent to this node (v1) |
| `["to-owner", channel, origin, payload, entityId, correlationId]` | on the owner only; `correlationId` is 0 when the sender gave none (v1) |
| `["owner", channel, entityId, node, correlationId]` | answer to `owner-of`; `node` is `""` when no node owns the entity (v1) |
| `["player-nodes", channel, players]` | answer to `player-nodes`: `players` is an `object[]` of `object[] { long identityId, ulong steamId, string node }` (v1) |
| `["before-handover", channel, gridId]` | on the source, while the grid is frozen for capture (v1) |
| `["after-import", channel, gridId, state]` | on the target, once the grid is live there; `state` is what the source gave, or null (v1) |
| `["handover-left", channel, gridId]` | on the source, after the move committed (v1) |
| `["handover-stayed", channel, gridId]` | on the source, after the move was abandoned; the grid stays (v1) |
| `["reset", channel, reason]` | ordering or delivery toward a peer was lost: resync (v1) |
| `["fault", channel, error, correlationId]` | a send of yours failed; `correlationId` is 0 when it had none |

A send that is malformed or over quota faults synchronously, inside your `SendModMessage`. A delivery that fails
later (unreachable node, refused, no owner) faults later, on the game thread. A mod cannot read the server log,
so the fault is the only error signal.

## Role

No answer to `role` means there is no cluster plugin: keep the vanilla behaviour. `inCluster` false (role
`"Standalone"`) means the plugin runs without a cluster. On a cluster, every node and the WA run every mod; a mod
that keeps world-global logic runs it where `role == "WA"` and acts elsewhere, and a mod meant to run on the WA
only can gate itself on the role (or be placed `wa-only`, Docs/ModCompat.md). A fenced WA answers `"Regular"`.

## Owner routing

The plugin finds the owner by asking each active regular node in turn, the last known owner first; the WA owns no
grids and is never asked. `to-owner` is delivered to the one node that owns the grid at that moment, and only once
(per operation, deduplicated on the receiver). While the grid is frozen in a handover, only copies of it are
seen or a node cannot be asked, the search repeats for up to 10 s, then faults (`no node owns entity N within 10 s`); a grid that no node
holds at all faults at once. A fault whose error starts with `no node owns` was not delivered
anywhere: retry it (a grid made a moment ago is owned only once its partition claim is answered, which can take
longer than 10 s on a loaded node). A request that timed out after it left (the outcome is unknown, the error starts
with `delivery outcome unknown`) faults instead of
being offered to the next node, so a move during a timeout cannot make it land twice. `to-owner` and `owner-of`
are not ordered with each other or with the other kinds.

`owner-of` answers the same search as `owner` with the node, or `""`.

## Players

`player-nodes` reads the cluster-wide online list (`OnlinePlayersService`): every online player and the node its
client is attached to, on any node and on the WA. Client messaging in vanilla (`SendMessageTo`, VSL
notifications) only reaches players attached to the node that sends it, so send the message to that node with
`to-node` and let the mod there deliver it.

## Handover hooks

A grid that moves to another node is rebuilt there from its object builder, so a game-logic component's fields
start over (B4). A mod that keeps per-grid state in memory sends `handover-hooks` once (every node, from its
first update). Then, for every grid that moves:

1. source, grid frozen: `before-handover`. Answer inside the handler with
   `["handover-state", channel, gridId, state]`. All channels' states of one grid together may be at most
   16 KiB (`PluginGridHandover.MaxStateBytes`); over that the states are dropped and each channel gets a fault.
2. target, grid live: `after-import` with the state, or null when there was none or a node restart lost it.
3. source: `handover-left` (drop the state) or `handover-stayed` (the move was abandoned; keep it).

Only grids are reported, not characters. The states ride the T-0256 plugin grid-handover pipeline
(`PluginHandoverService`) as the entry of the plugin `cluster.mod-channel`. State a mod keeps in
`MyModStorageComponent` travels with the grid anyway and needs no hook.

## Reset

Delivery is reliable only while both nodes live: nothing is persisted, so a message in flight to a node that dies
is gone. While any mod uses the channel, each node reads the registry's node list every 5 s and tells every
channel used on it (sent or received) what changed:

| Reason | Meaning |
|---|---|
| `wa-changed:<old>-><new>` | another World Authority (or none) |
| `node-gone:<node>` | the node left the active list |
| `node-restarted:<node>` | the node registered again under a new epoch |
| `node-joined:<node>` | a new active node; a broadcast before this did not reach it |
| `gap:<node>` | on this channel, messages from that node were lost and the order skipped past them |

A reset is a cue to rebuild what the mod derived from other nodes (ask again, broadcast a hello); a decision
already carried out is not undone. Deliveries are exactly-once while the nodes live; across a reset, at most
once. A mod that must not repeat an action across a WA restart keeps its own transaction ids in durable state.

## Delivery

`broadcast`, `request`, `respond` and `to-node` are queued per node and delivered in order per (origin, channel)
and exactly once per operation; a node that restarts begins a new order. Every delivery is journalled in
`GET /cluster/mod-status` (last 128) and logged as `[ClusterPlugin] mod_channel operation=...`; resets as
`[ClusterPlugin] mod_channel reset channel=... reason=...`. Node to node, deliveries go to
`POST /cluster/mod-deliver` and owner lookups to `GET /cluster/mod-locate?entity=` (both channel N9,
Docs/Gateway/NodeAPI.md).

## Not built

From Plan/Modding.md (clustering-plan) v1: the delegate vtable handshake (the object[] protocol above came first
and carries the same operations), `Request` with a plugin-managed timeout, per-assembly channel ownership and the
per-mod byte budgets, and delivery over Direct Transport instead of node HTTP.
