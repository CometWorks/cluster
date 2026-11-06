# Relay IPC: ClusterRegistry (C#) and gateway-rs (Rust)

Status: **specification, v2 (Fragment), primitive model** (T-0229, 2026-09-24). Design context:
clustering-plan `Plan/RustGateway.md`. This file is the contract both sides implement; a change here
bumps `version` in `Hello`.

## Transport

- A Unix domain socket, `SOCK_SEQPACKET`, so each `send` is one message and boundaries are kept.
  - .NET has no named constant for this socket type; use `(SocketType)5`.
- The path is `CLUSTER_RELAY_SOCKET`, by default `<run root>/gateway-relay.sock`, mode 0600.
  - ClusterRegistry binds and listens.
  - gateway-rs connects and retries every 250 ms.
- One connection at a time. A second connection is refused while the first is alive.
- A frame above 64 KiB travels as `Fragment` messages (type 199, below) and is reassembled on the
  other side, up to 64 MiB per frame. A live world's full global-state snapshot is 0.5-4 MB.
  - A SEQPACKET message must fit the sender's socket buffer (`net.core.wmem_default`, 208 KiB on
    polaris), or `send` fails with EMSGSIZE; 64 KiB stays well inside any default.
  - Receive buffers are 256 KiB.
  - Version 1 had no fragments. It dropped every frame above 256 KiB, and frames between 208 and
    256 KiB failed with EMSGSIZE. The first two gateway-rs deploys (2026-09-24) lost wa-1's
    snapshots this way. The nodes re-requested them every second, and wa-1 queued a new
    multi-MB snapshot for each request until its guard stopped the cluster at 4.5 GB.

## Framing

Every message is `[u16 type LE][u32 correlation LE][body]`.

- `correlation` is 0 for one-way messages. Otherwise it echoes the request that is being answered.
- Types 1-99 carry a UTF-8 JSON body (camelCase field names).
- Types 100 and above carry a binary body, laid out as given for each message.
- Unknown types are logged once and ignored, so either side can add messages without a lockstep
  upgrade.

### Fragment (199)

Body: `[u32 id LE][u32 total LE][u32 offset LE][chunk]`. The chunk is bytes `offset..offset+len` of one
complete frame (header included) of `total` bytes.

- Each sender numbers its fragmented frames with `id`, and sends one frame's fragments in order,
  starting at offset 0.
- Fragments of different frames may interleave: gateway-rs sends from several threads without a
  lock. ClusterRegistry sends one frame's fragments back to back under its send lock.
- The receiver keeps one partial frame per `id`. When the last byte arrives, it handles the frame
  as if it had arrived whole.
- A fragment that does not continue its frame, a `total` above 64 MiB, or more than 64 frames in
  reassembly drops the affected frames with a WARNING. Nothing is dropped silently.

## Division of labour

**Only the packet path moves to gateway-rs; all session decisions stay in C#.**

- gateway-rs is the data plane. It terminates Direct (LiteNetLib) and Steam, holds the node links,
  and per client holds the routing state and the backlogs. It applies the per-packet rules of
  today's `SendToBackend`, `SendToClient`, `ShouldRelayServerPacket` and credit.
- ClusterRegistry keeps its session objects and every decision about them: admission, the
  attach/detach state machine, handovers, respawn switches, reconnect rules. It drives the data
  plane with a few primitives, and reacts to the events the data plane reports.
- With `--relay internal`, those same primitives run in process. So the control logic is one
  implementation for both relays, and C# stays the reference.

## Session identity

- A client is identified by its `clientId` (u64): the Steam id, or the Direct Transport id.
- Every client message also carries `session`, a u64 that gateway-rs assigns when the client
  connects. A command whose `session` is not the live one is ignored. A reconnect is a new session.
- Node links are identified by `endpoint` (`host:port`), as the registry lists them.

## Per-client state held by gateway-rs

C# sets it with `SessionUpdate`. The per-packet rules read it:

| Field | Meaning (C# field today) | Packet rule |
|---|---|---|
| `link` | node endpoint the client is routed to (`session.Link`) | node-to-client relay frames are forwarded only from this link |
| `attached` | the node acknowledged this link (`AttachedLink == Link`) | client-to-node forwarding needs attached, routable, not switching, not paused, link connected; otherwise the packet goes to the client backlog |
| `routable` | `AttachedBindingRoutable(session)`, recomputed by C# when its inputs change | as above |
| `switching` | `SwitchingBackend` | client-to-node packets go to the backlog; the backlog's age bound is waived |
| `attachPending` | the session is admitted and ClusterRegistry is still retrying its Attach (`RetryPlayerAttach`, bounded there by a 90 s deadline) | the client backlog's age bound is waived, as under the pause and a switch; the waiver holds for as long as the hold stands, not only when a packet arrives in it (T-0357) |
| `bufferServer` | `BufferServerPackets` | node-to-client packets go to the server backlog; overflow reports `backlogOverflow` |
| `suppressJoin` | `SuppressJoinMessages` | after the type table, game messages 2/12/13/20 from the node are dropped |
| `validationLink` | `ValidationLink` | a message-7 packet from it reports `targetReady`, once per arming: every `SessionUpdate` that carries a non-null `validationLink` re-arms it (C# pushes the full state, so also its 1 Hz refresh) |

- gateway-rs keeps `typeTableSent`, the duplicate SERVER_DATA count, credit and the backlogs itself.
- Client-to-node order is kept unconditionally: a client packet is forwarded directly only while the
  client backlog is empty, and the backlog drains by itself whenever forwarding becomes allowed (a
  `SessionUpdate`, the pause lifting, the link connecting). `flushClient` is therefore implied. The
  in-process relay could let a fresh packet overtake a backlog no `FlushClientBacklog` had drained yet.
- Standing conditions (`backlogOverflow`, `backlogExpired`, `ackBacklog`) are re-reported at most once
  a second while they hold, because C# may legitimately ignore one: a join still waiting for its
  ticket verdict is exempt from the age bound (`Credential == null`).
- A held Direct join ends when its client stops resending the ConnectRequest for 5 s (it gave up;
  the plugin then makes a new attempt, which is a new join). LiteNetLib never expires a request, so
  the in-process relay could accept one minutes later and make a session for nobody. C# logs
  "Holding join" once per relay session, although gateway-rs re-asks every second.
- A Steam session is reported `silent` after 180 s without client traffic; C# no longer runs that
  watchdog for external sessions, since it does not see their traffic.
- Known difference: a CLIENT_CONNECTED whose ticket validation C# refuses is not relayed in process;
  gateway-rs has already relayed or backlogged it by the time C# decides. Before admission the
  session has no attached link, so the packet sits in the backlog and leaves with the session.
- Ordering: C# sends a session's `SessionUpdate`s in the order it read them (a per-session lock
  around snapshot and send), so the last one gateway-rs applies is the newest state. gateway-rs
  routes messages for a join C# has answered but its worker has not processed yet (the first
  `SessionUpdate`, the node's AttachAck) by the join's pending owner; the worker's FIFO puts them
  after the answer.
- Admission is serialised in C#: gateway-rs's `JoinRequest`s and `SteamEvent`s are decided one at
  a time, as the in-process relay's poll thread does, so the member limit holds under a burst.
- It captures the client's CLIENT_CONNECTED packet for `replayConnect`.
- It sends node credit exactly as `SendToClient` and `ReplenishClientCredit` do: payload + 10 per
  forwarded packet, withheld while the client's reliable queue is at 4096 or more, released below
  3072.

## Messages

Direction is C#→Rust unless marked. "corr" means the message is a request that is answered on its
correlation.

### Link

| Type | Name | Body |
|---|---|---|
| 1 | `Hello` (both, first message) | `{version:1, role:"relay"/"registry", pid, build}` |
| 2 | `Heartbeat` (both, every 1 s) | `{seq, sessions?, load?}`. Three missed means the peer is down |
| 3 | `Config` | `{direct:{enabled,bind,port,dualStack}, steam:{enabled,bind,port,version}, backlog:{clientBytes,clientAgeMs,serverBytes}, credit:{joinWindow,withholdAbove,releaseBelow}, timeouts:{clientDisconnectMs,nodeDisconnectMs}, joinHoldSeconds, joinToken, acceptAll, nodeLinkChannels}`. The browser-row settings come in `SteamPublish` |
| 5 | `Paused` | `{paused}`. Global relay pause: `/pause`, and `PersistenceFailed` |

### Joins and clients

| Type | Name | Body |
|---|---|---|
| 10 | `JoinRequest` (Rust→C#, corr) | `{clientId, session, service:"DirectTransport"/"Steam", name, remote}`. A Direct ConnectRequest, or a Steam P2P session: its session request, or the first packet from a user with no session (a rejoin inside the 30 s grace). See **Steam** |
| 11 | `JoinAnswer` (answer to 10) | `{decision:"accept"/"hold"/"reject", reason?}`. On accept, C# then sets `link` with a `SessionUpdate`. On hold, gateway-rs re-asks every second while the request is held |
| 12 | `ClientMessage` (Rust→C#) | `{clientId, session, messageId, packet:base64}` for the messages C# acts on: 14 CLIENT_CONNECTED and 8 CLIENT_READY. The packet is still relayed per the rules |
| 15 | `ClientEvent` (Rust→C#) | `{clientId, session, event, detail?}`. Events: `disconnected{reason}`, `backlogOverflow{which}`, `backlogExpired`, `targetReady{endpoint}`, `silent`, `ackBacklog`, `duplicateServerData` |
| 16 | `Disconnect` | `{clientId, session, reason, joinResult?}`. With `joinResult`, a synthesized JOIN_RESULT goes first and the disconnect follows 250 ms later. C# does not wait for `disconnected` forever: a session it has sent `Disconnect` for is removed by its next force-reconnect, or after 45 s (#1221) |
| 17 | `ForceReconnect` | `{clientId, session, reason}`. The channel-0 control message, then a disconnect after 25 ms |
| 18 | `SessionUpdate` (corr optional) | `{clientId, session, link?, attached?, routable?, switching?, attachPending?, bufferServer?, suppressJoin?, validationLink?, flushClient?, flushServer?, discardServer?, resetTypeTable?}`. Fields that are present are applied in one step on the owning worker. The flushes run after the fields are applied and forward only if the new state allows it, which keeps today's `FlushClientBacklog` and `FinishSwitching` semantics. Answered `{ok, clientBacklog, serverBacklog}` when a correlation is given |
| 19 | `ReplayConnect` (corr) | `{clientId, session}`. Sends the captured CLIENT_CONNECTED to `link` (`AttachBackend`) |

### Steam

| Type | Name | Body |
|---|---|---|
| 20 | `SteamAuth` | `{op:"begin", clientId, ticket:base64}` (BeginAuthSession with the ticket from CLIENT_CONNECTED), `{op:"end", clientId}`, `{op:"group", clientId, groupId}` (RequestUserGroupStatus) |
| 21 | `SteamEvent` (Rust→C#) | `{kind:"ticket", clientId, response, owner, missingDlcs?}`: ValidateAuthTicketResponse, `response` the raw EAuthSessionResponse (C# maps it with `SteamFrontend.Verdict`); also a later revocation. On an OK verdict `missingDlcs` lists the DLC app ids the player has no licence for (SE-1158). `{kind:"ticketRejected", clientId, response}`: BeginAuthSession failed (EBeginAuthSessionResult), C# treats it as TicketInvalid. `{kind:"group", clientId, member}`: GSClientGroupStatus (member or officer) |
| 22 | `SteamPublish` | `{serverName, mapName, maxPlayers, passwordProtected, tags, players:[{userId,name}]}`: the browser row. C# sends it on connect, every 5 s and after an admission; gateway-rs re-applies the last one every 5 s (BUpdateUserData per player, the players Steam will not name counted as bots) |

gateway-rs is then the whole Steam frontend of `SteamFrontend.cs`; C# keeps admission:

- One GameServer identity (`GameServer.Init` on the Steam port, VAC-secure, anonymous logon, shared
  query port). Master-server queries on that port are answered by the SDK from a query thread.
- P2P on the vanilla channels {0, 2}, every packet taken as ReliableOrdered; sends are Reliable
  unless the delivery is Unreliable. The P2P session is accepted only after C# accepts the join;
  packets that arrive meanwhile wait, in order, up to 1 MiB.
- The session enters TicketPending with no node: C# sends `SteamAuth begin` when CLIENT_CONNECTED
  arrives, then admits or rejects on `SteamEvent`, exactly as with the in-process frontend.
- `Disconnect` ends the auth session and the relay session (`disconnected` follows) but leaves the
  P2P session open for 30 s, so a player who comes straight back reaches us; the grace then closes it.
  A P2P connect failure ends it at once (`disconnected`, `steam-session-failed: <error>`).
- `silent` after 180 s without traffic, as before.
- The Steam frontend is one more worker, so every per-client message routes to it by the owner map;
  `Status` carries its `steam` section (`/transport/status` shows it).
- T-0244: a forced reconnect's Disconnected control message must name `steam.serverId` (the
  GameServer SteamID), the only id a vanilla client treats as "the host left"; C# learns it from
  `Status` (a `{purpose:"steam-identity"}` request every 5 s until known). A Steam session that shows
  no CLIENT_CONNECTED within 5 s is a client still playing an ended session: C# force-reconnects it
  again until 15 s after the first forced reconnect, then refuses it with a synthesized
  `JOIN_RESULT{UserNotConnected}` (fatal to a vanilla client: back to the main menu).
  `/transport/status` lists such clients under `reconnectStreaks` until they are admitted again.

### Node links

| Type | Name | Body |
|---|---|---|
| 40 | `EnsureLink` | `{endpoint}`. Connect if not connected or connecting (`ConnectBackend`). Idempotent |
| 41 | `DropLink` | `{endpoint}`. Close the link |
| 42 | `LinkState` (Rust→C#) | `{endpoint, connected, reason?}`. Mirrors `OnPeerConnected` and `OnPeerDisconnected`. On a disconnect, C# runs its existing per-client handling |
| 43 | `NodeControl` (Rust→C#) | `{endpoint, kind:"attachAck"/"detachAck"/"detach", clientId, session}`. Control frames from a node about a client; C# runs today's handler bodies |
| 44 | `NodeFrame` | `{endpoint, kind:"attach"/"detach"/"credit", clientId, body?:base64, credit?}`. A control frame toward a node |

### Tunnel

| Type | Name | Body |
|---|---|---|
| 100 | `NodeGlobal` (both) | `[u8 delivery][u16 endpointLen][endpoint utf8][JSON bytes]`. Global node-link frames (kind 7). `delivery` is the LiteNetLib `DeliveryMethod` to send with (Global Commands and Responses are ReliableUnordered, other Global frames ReliableOrdered); toward C# it is the method the frame arrived with |
| 101 | `NodePlugin` (both) | same layout, for Plugin frames (kind 8) |
| 102 | `ClientSend` | `[u64 clientId][u64 session][u8 channel][u8 delivery][bytes]`. A packet C# sends to a client |

### Queries

| Type | Name | Body |
|---|---|---|
| 30 | `Status` (corr) | `{}`, answered with sessions (id, session, service, name, remote, link, attached, switching, backlog sizes, rtt, mtu, duplicateServerData, reliableBacklog, connectedAt, last activity), nodes (endpoint, connected, rtt) and counters |

## Loss of the peer

- **gateway-rs loses ClusterRegistry:**
  - ClusterRegistry drops its session mirrors on the same loss (below), so a session kept alive
    here would be an orphan no one can switch or admit. gateway-rs therefore sends every client
    the reconnect control message and disconnects it, and refuses pending and new joins until
    ClusterRegistry is back; the clients rejoin through the normal path.
  - Node links stay up; they carry no session state of their own.
  - It reconnects every 250 ms and sends `Hello`; C# re-sends `Config`, `Paused` and an
    `EnsureLink` per node. A second `Config` does not move the listeners (a restart does).
- **ClusterRegistry loses gateway-rs:**
  - It marks the relay down in `/diag` and `/transport/status`, and begins no handover.
  - It drops its session mirrors, as if every client had disconnected.
  - A transfer in flight fails and rolls back.
  - It waits for the reconnect. gateway-rs sessions do not survive a gateway-rs restart; their
    clients reconnect.
