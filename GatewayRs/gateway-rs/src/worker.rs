//! A front worker: one UDP socket per address family bound to the Direct port with SO_REUSEPORT
//! (the kernel keeps each client address on one worker), one LiteNetLib endpoint, and the relay
//! sessions of its clients. It is the only thread that touches those sessions.

use std::collections::HashMap;
use std::io;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

use lnl::{DeliveryMethod, Endpoint, Event, Output, PeerId, RequestId};
use mio::net::UdpSocket;
use mio::{Events, Interest, Poll, Token, Waker};
use relay::{Delivery, LinkId, Packet, Session};
use serde_json::json;

use crate::ipc::{ClientEvent, ClientMessage, JoinAnswer, JoinRequest, types};
use crate::node::NodeCmd;
use crate::nodelink;
use crate::shared::{Coalesce, Inbox, JoinAsks, Mailbox, Owner, Shared};

const V4: Token = Token(0);
const V6: Token = Token(1);
const WAKE: Token = Token(2);
const TICK: Duration = Duration::from_millis(5);
const MAINTENANCE: Duration = Duration::from_millis(100);
/// A join ClusterRegistry never answered (IPC lost mid-request) is refused after this long.
const UNANSWERED_JOIN: Duration = Duration::from_secs(15);
/// A connecting LiteNetLib client resends its ConnectRequest every ReconnectDelay (500 ms by
/// default) until it gives up; a held request not resent for this long belongs to a client that
/// has gone, and accepting it later only makes a session for nobody (P3-PLR-02, 2026-09-24).
const ABANDONED_REQUEST: Duration = Duration::from_secs(5);
/// `MyControlMessageEnum.Disconnected` from the server id, then the disconnect 25 ms later.
const FORCE_RECONNECT_DELAY: Duration = Duration::from_millis(25);
const SERVER_ID: u64 = 0xFF00_0000_0000_0001;

pub enum WorkerCmd {
    JoinAnswer { correlation: u32, answer: JoinAnswer },
    Disconnect { client_id: u64, session: u64, reason: String },
    ForceReconnect { client_id: u64, session: u64, reason: String },
    Update { client_id: u64, session: u64, correlation: u32, update: relay::SessionUpdate },
    ReplayConnect { client_id: u64, session: u64 },
    ClientSend { client_id: u64, session: u64, channel: u8, delivery: u8, payload: Vec<u8> },
    FromNode { client_id: u64, link: LinkId, channel: u8, delivery: Delivery, payload: Vec<u8> },
    /// The pause lifted or a link connected: drain what can go now.
    Flush,
    /// ClusterRegistry is gone and has forgotten every session: send every client to reconnect.
    IpcLost,
    Status(crossbeam_channel::Sender<Vec<serde_json::Value>>),
    /// Steam worker only: the auth-session primitives, the browser row and its status.
    SteamAuth(crate::ipc::SteamAuth),
    SteamPublish(Box<crate::ipc::SteamPublish>),
    SteamStatus(crossbeam_channel::Sender<serde_json::Value>),
}

pub struct WorkerConfig {
    pub index: u16,
    pub v4: Option<SocketAddr>,
    pub v6: Option<SocketAddr>,
    pub lnl: lnl::Config,
    pub limits: relay::Limits,
    pub join_hold: Duration,
    pub accept_all: bool,
}

struct Client {
    peer: PeerId,
    session: Session,
    name: Option<String>,
    addr: SocketAddr,
    connected_at: Instant,
    latency_ms: i32,
}

struct Pending {
    request: RequestId,
    client_id: u64,
    session: u64,
    name: Option<String>,
    addr: SocketAddr,
    first_asked: Instant,
    last_asked: Instant,
    held: bool,
}

struct Sockets {
    v4: Option<UdpSocket>,
    v6: Option<UdpSocket>,
    send_errors: u64,
}

struct Out<'a> {
    sockets: &'a mut Sockets,
    events: &'a mut Vec<Event>,
}

impl Output for Out<'_> {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        let socket = if to.is_ipv4() { self.sockets.v4.as_ref() } else { self.sockets.v6.as_ref() };
        // LiteNetLib ignores send failures too.
        if socket.is_none_or(|socket| socket.send_to(datagram, to).is_err()) {
            self.sockets.send_errors += 1;
        }
    }

    fn event(&mut self, event: Event) {
        self.events.push(event);
    }
}

/// The relay core's view of one client, bound to this worker for one call.
struct ClientIo<'a> {
    endpoint: &'a mut Endpoint,
    shared: &'a Shared,
    peer: PeerId,
    scratch: &'a mut Vec<u8>,
}

fn method(delivery: Delivery) -> DeliveryMethod {
    DeliveryMethod::from_u8(delivery as u8).unwrap_or(DeliveryMethod::ReliableOrdered)
}

impl relay::Io for ClientIo<'_> {
    fn paused(&self) -> bool {
        self.shared.paused()
    }

    fn link_connected(&self, link: LinkId) -> bool {
        self.shared.links.connected(link)
    }

    fn send_to_node(&mut self, link: LinkId, client_id: u64, packet: Packet) {
        self.shared.to_node(NodeCmd::Relay { link, client_id, packet });
    }

    fn send_to_client(&mut self, _client_id: u64, packet: Packet) -> Option<usize> {
        send_client(self.endpoint, self.peer, self.scratch, packet.channel, packet.delivery, &packet.payload)?;
        Some(self.endpoint.reliable_queue_len(self.peer, 0, true))
    }

    fn client_reliable_queue(&self, _client_id: u64) -> Option<usize> {
        let info = self.endpoint.peer(self.peer)?;
        (info.state == lnl::ConnectionState::Connected).then(|| self.endpoint.reliable_queue_len(self.peer, 0, true))
    }

    fn send_credit(&mut self, link: LinkId, client_id: u64, bytes: i32) {
        self.shared.to_node(NodeCmd::Credit { link, client_id, credit: bytes });
    }

    fn event(&mut self, client_id: u64, session: u64, event: relay::Event) {
        report(self.shared, client_id, session, event);
    }
}

/// `DirectClientChannel.Send`: `[vanilla channel][payload]` on LiteNetLib channel 0.
fn send_client(endpoint: &mut Endpoint, peer: PeerId, scratch: &mut Vec<u8>, channel: u8, delivery: Delivery, payload: &[u8]) -> Option<()> {
    scratch.clear();
    scratch.push(channel);
    scratch.extend_from_slice(payload);
    endpoint.send(peer, scratch, 0, method(delivery)).ok()
}

pub(crate) fn report(shared: &Shared, client_id: u64, session: u64, event: relay::Event) {
    let (name, detail) = match event {
        relay::Event::ClientMessage { message_id, packet } => {
            let message = ClientMessage::new(client_id, session, message_id, packet.channel, &packet.payload);
            shared.ipc.send_json(types::CLIENT_MESSAGE, 0, &message);
            return;
        }
        relay::Event::TargetReady { link } => ("targetReady", Some(shared.links.name(link))),
        relay::Event::BacklogOverflow(which) => ("backlogOverflow", Some(which.name().to_owned())),
        relay::Event::BacklogExpired => ("backlogExpired", None),
        relay::Event::AckBacklog => ("ackBacklog", None),
        relay::Event::DuplicateServerData => ("duplicateServerData", None),
        relay::Event::Silent => ("silent", None),
    };
    shared.ipc.send_json(types::CLIENT_EVENT, 0, &ClientEvent { client_id, session, event: name, detail });
}

pub struct Worker {
    config: WorkerConfig,
    shared: Arc<Shared>,
    inbox: Inbox<WorkerCmd>,
    poll: Poll,
    sockets: Sockets,
    endpoint: Endpoint,
    clients: HashMap<u64, Client>,
    by_peer: HashMap<PeerId, u64>,
    pending: HashMap<u32, Pending>,
    asks: JoinAsks,
    force_disconnects: Vec<(Instant, PeerId)>,
    events: Vec<Event>,
    buffer: Vec<u8>,
    scratch: Vec<u8>,
    last_maintenance: Instant,
    coalesce: Coalesce,
    invalid_client_frames: u64,
}

fn bind(addr: SocketAddr) -> io::Result<UdpSocket> {
    use socket2::{Domain, Protocol, Socket, Type};
    let socket = Socket::new(Domain::for_address(addr), Type::DGRAM, Some(Protocol::UDP))?;
    socket.set_reuse_port(true)?;
    if addr.is_ipv6() {
        socket.set_only_v6(true)?;
    }
    // LiteNetLib's SocketBufferSize.
    let _ = socket.set_recv_buffer_size(1024 * 1024);
    let _ = socket.set_send_buffer_size(1024 * 1024);
    socket.set_nonblocking(true)?;
    socket.bind(&addr.into())?;
    Ok(UdpSocket::from_std(socket.into()))
}

impl Worker {
    /// Binds the sockets (so a bind failure surfaces before any thread starts).
    pub fn new(config: WorkerConfig, shared: Arc<Shared>) -> io::Result<(Self, Mailbox<WorkerCmd>)> {
        let poll = Poll::new()?;
        let mut v4 = config.v4.map(bind).transpose()?;
        let mut v6 = match config.v6.map(bind) {
            Some(Ok(socket)) => Some(socket),
            Some(Err(error)) => {
                eprintln!("WARNING: worker {}: IPv6 bind failed ({error}); IPv4 only", config.index);
                None
            }
            None => None,
        };
        if let Some(socket) = v4.as_mut() {
            poll.registry().register(socket, V4, Interest::READABLE)?;
        }
        if let Some(socket) = v6.as_mut() {
            poll.registry().register(socket, V6, Interest::READABLE)?;
        }
        let (mailbox, inbox) = Mailbox::new(Waker::new(poll.registry(), WAKE)?);
        let now = Instant::now();
        let endpoint = Endpoint::new(config.lnl.clone(), now);
        Ok((
            Self {
                config,
                shared,
                inbox,
                poll,
                sockets: Sockets { v4, v6, send_errors: 0 },
                endpoint,
                clients: HashMap::new(),
                by_peer: HashMap::new(),
                pending: HashMap::new(),
                asks: JoinAsks::default(),
                force_disconnects: Vec::new(),
                events: Vec::new(),
                buffer: vec![0; 64 * 1024],
                scratch: Vec::with_capacity(2048),
                last_maintenance: now,
                coalesce: Coalesce::new(TICK, now),
                invalid_client_frames: 0,
            },
            mailbox,
        ))
    }

    pub fn run(mut self) {
        let mut events = Events::with_capacity(64);
        loop {
            let wait = if self.inbox.park() { self.coalesce.wait(Instant::now()) } else { Duration::ZERO };
            if let Err(error) = self.poll.poll(&mut events, Some(wait))
                && error.kind() != io::ErrorKind::Interrupted
            {
                eprintln!("ERROR: worker {} poll failed: {error}", self.config.index);
                return;
            }
            self.inbox.unpark();
            let now = Instant::now();
            for token in [V4, V6] {
                self.receive(token, now);
            }
            while let Ok(command) = self.inbox.rx.try_recv() {
                self.coalesce.touch();
                self.command(command, Instant::now());
            }
            let now = Instant::now();
            if now.duration_since(self.last_maintenance) >= MAINTENANCE {
                self.last_maintenance = now;
                self.maintain(now);
                self.coalesce.touch();
            }
            if self.coalesce.due(now) {
                self.flush(now);
                self.coalesce.flushed(now);
            }
        }
    }

    /// Drives LiteNetLib (queued sends leave here) and handles what it raised.
    fn flush(&mut self, now: Instant) {
        let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
        self.endpoint.poll(now, &mut out);
        self.drain_events(now);
    }

    fn receive(&mut self, token: Token, now: Instant) {
        loop {
            let socket = match token {
                V4 => self.sockets.v4.as_ref(),
                _ => self.sockets.v6.as_ref(),
            };
            let Some(socket) = socket else { return };
            match socket.recv_from(&mut self.buffer) {
                Ok((length, from)) => {
                    self.coalesce.touch();
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.handle(now, from, &self.buffer[..length], &mut out);
                    self.drain_events(now);
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => return,
                // ICMP errors surfaced on the socket are ignored, as in LiteNetLib.
                Err(_) => {}
            }
        }
    }

    fn drain_events(&mut self, now: Instant) {
        while !self.events.is_empty() {
            let events = std::mem::take(&mut self.events);
            for event in events {
                self.event(event, now);
            }
        }
    }

    fn event(&mut self, event: Event, now: Instant) {
        match event {
            Event::ConnectionRequest { request, addr, data } => self.connection_request(request, addr, &data, now),
            Event::Receive { peer, method, packet, .. } => {
                let client_id = self.by_peer.get(&peer).copied();
                // `DecodePacket`: the first byte is the vanilla channel.
                let decoded = packet.data().split_first().map(|(&channel, payload)| (channel, payload.to_vec()));
                self.endpoint.recycle(packet);
                let (Some(client_id), Some((channel, payload))) = (client_id, decoded) else { return };
                let delivery = Delivery::from_byte(method as u8).unwrap_or(Delivery::ReliableOrdered);
                let invalid = self.with_client(client_id, |session, io| {
                    session.on_client_packet(Packet { channel, delivery, payload }, now, io).is_err()
                });
                if invalid == Some(true) {
                    self.invalid_client_frames += 1;
                }
            }
            Event::Disconnected { peer, reason, data, .. } => {
                if let Some(data) = data {
                    self.endpoint.recycle(data);
                }
                self.force_disconnects.retain(|(_, pending)| *pending != peer);
                let Some(client_id) = self.by_peer.remove(&peer) else { return };
                let Some(client) = self.clients.remove(&client_id) else { return };
                let session = client.session.session;
                {
                    let mut owners = self.shared.owners.write().unwrap();
                    if owners.get(&client_id).is_some_and(|owner| owner.session == session) {
                        owners.remove(&client_id);
                    }
                }
                let detail = Some(format!("{reason:?}"));
                self.shared.ipc.send_json(types::CLIENT_EVENT, 0, &ClientEvent { client_id, session, event: "disconnected", detail });
            }
            Event::LatencyUpdate { peer, latency_ms } => {
                if let Some(client) = self.by_peer.get(&peer).and_then(|id| self.clients.get_mut(id)) {
                    client.latency_ms = latency_ms;
                }
            }
            Event::Connected { .. } | Event::PeerAddressChanged { .. } => {}
        }
    }

    /// Runs `f` on a client's relay session with an `Io` bound to it; `None` if there is no such client.
    fn with_client<R>(&mut self, client_id: u64, f: impl FnOnce(&mut Session, &mut ClientIo<'_>) -> R) -> Option<R> {
        let client = self.clients.get_mut(&client_id)?;
        let mut io = ClientIo { endpoint: &mut self.endpoint, shared: &self.shared, peer: client.peer, scratch: &mut self.scratch };
        Some(f(&mut client.session, &mut io))
    }

    fn session_matches(&self, client_id: u64, session: u64) -> bool {
        self.clients.get(&client_id).is_some_and(|client| client.session.session == session)
    }

    // ---------------------------------------------------------------- joins

    fn connection_request(&mut self, request: RequestId, addr: SocketAddr, data: &[u8], now: Instant) {
        let Some((client_id, name)) = nodelink::client_handshake(data) else {
            eprintln!("Rejected {addr}: bad Direct Transport handshake");
            let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
            self.endpoint.reject(now, request, &[], &mut out);
            return;
        };
        if !self.config.accept_all || !self.shared.ipc.connected() {
            // No ClusterRegistry to decide: refuse; the client retries.
            let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
            self.endpoint.reject(now, request, &[], &mut out);
            return;
        }
        let session = self.shared.new_session();
        let pending = Pending { request, client_id, session, name, addr, first_asked: now, last_asked: now, held: false };
        self.ask(pending);
    }

    fn ask(&mut self, pending: Pending) {
        let correlation = self.shared.join_correlation(self.config.index);
        let request = JoinRequest {
            client_id: pending.client_id,
            session: pending.session,
            service: "DirectTransport",
            name: pending.name.as_deref(),
            remote: pending.addr.to_string(),
        };
        self.shared.set_pending(pending.client_id, Owner { worker: self.config.index, session: pending.session });
        self.shared.ipc.send_json(types::JOIN_REQUEST, correlation, &request);
        self.asks.asked(correlation, pending.client_id, pending.session, pending.last_asked);
        self.pending.insert(correlation, pending);
    }

    fn join_answer(&mut self, correlation: u32, answer: JoinAnswer, now: Instant) {
        let Some(asked) = self.asks.answered(correlation) else { return };
        // The join is parked under the correlation it was LAST asked with (T-0314).
        let current = self.pending.iter().find(|(_, p)| p.session == asked.session).map(|(&c, _)| c);
        let current = match current {
            // An accept of a superseded ask made the C# session: take it. C# refuses the newer ask
            // as a second join, and that answer then finds no join and is dropped.
            Some(current) if current == correlation || answer.decision == "accept" => current,
            // A superseded ask's hold or refusal: the newer ask's answer decides.
            Some(_) => return,
            None => {
                if answer.decision == "accept" && !self.session_matches(asked.client_id, asked.session) {
                    eprintln!("Released late accept of client {} session {}: its join was already refused", asked.client_id, asked.session);
                    let detail = Some("join already refused".into());
                    let event = ClientEvent { client_id: asked.client_id, session: asked.session, event: "disconnected", detail };
                    self.shared.ipc.send_json(types::CLIENT_EVENT, 0, &event);
                }
                return;
            }
        };
        let Some(mut pending) = self.pending.remove(&current) else { return };
        let (client_id, session) = (pending.client_id, pending.session);
        match answer.decision.as_str() {
            "accept" => {
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                let Some(peer) = self.endpoint.accept(now, pending.request, &mut out) else {
                    // The client gave up meanwhile; ClusterRegistry already made a session. Its
                    // pending owner goes too: nothing else would ever remove it (a leak per client).
                    self.shared.clear_pending(client_id, session);
                    let event = ClientEvent { client_id: pending.client_id, session: pending.session, event: "disconnected", detail: Some("request gone".into()) };
                    self.shared.ipc.send_json(types::CLIENT_EVENT, 0, &event);
                    return;
                };
                let session = Session::new(pending.client_id, pending.session, self.config.limits, now);
                self.shared
                    .owners
                    .write()
                    .unwrap()
                    .insert(pending.client_id, Owner { worker: self.config.index, session: pending.session });
                self.by_peer.insert(peer, pending.client_id);
                self.clients.insert(
                    pending.client_id,
                    Client { peer, session, name: pending.name, addr: pending.addr, connected_at: now, latency_ms: 0 },
                );
            }
            "hold" => {
                pending.held = true;
                pending.last_asked = now;
                // Re-asked from maintenance; parked under its old correlation until then.
                self.pending.insert(correlation, pending);
            }
            _ => {
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                self.endpoint.reject(now, pending.request, &[], &mut out);
            }
        }
        // After the live owner (on accept) is in place, so a lookup always finds one of them.
        if answer.decision != "hold" {
            self.shared.clear_pending(client_id, session);
        }
    }

    // ---------------------------------------------------------------- commands

    fn command(&mut self, command: WorkerCmd, now: Instant) {
        match command {
            WorkerCmd::JoinAnswer { correlation, answer } => self.join_answer(correlation, answer, now),
            WorkerCmd::Disconnect { client_id, session, reason } => {
                if let Some(client) = self.clients.get(&client_id).filter(|c| c.session.session == session) {
                    let peer = client.peer;
                    eprintln!("Disconnecting client {client_id}: {reason}");
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.disconnect(peer, &[], &mut out);
                }
            }
            WorkerCmd::ForceReconnect { client_id, session, reason } => {
                if self.session_matches(client_id, session) {
                    eprintln!("Force-reconnecting client {client_id}: {reason}");
                    self.force_reconnect(client_id, now);
                }
            }
            WorkerCmd::Update { client_id, session, correlation, update } => {
                let applied = if self.session_matches(client_id, session) {
                    self.with_client(client_id, |relay, io| {
                        relay.apply(&update, now, io);
                        let status = relay.status(now);
                        (status.client_backlog_packets, status.server_backlog_packets)
                    })
                } else {
                    None
                };
                if correlation != 0 {
                    let (ok, client, server) = applied.map_or((false, 0, 0), |(c, s)| (true, c, s));
                    let body = json!({ "ok": ok, "clientBacklog": client, "serverBacklog": server });
                    self.shared.ipc.send_json(types::SESSION_UPDATE, correlation, &body);
                }
            }
            WorkerCmd::ReplayConnect { client_id, session } => {
                if self.session_matches(client_id, session) {
                    self.with_client(client_id, |relay, io| relay.replay_connect(io));
                }
            }
            WorkerCmd::ClientSend { client_id, session, channel, delivery, payload } => {
                if let Some(client) = self.clients.get(&client_id).filter(|c| c.session.session == session) {
                    let delivery = Delivery::from_byte(delivery).unwrap_or(Delivery::ReliableOrdered);
                    send_client(&mut self.endpoint, client.peer, &mut self.scratch, channel, delivery, &payload);
                }
            }
            WorkerCmd::FromNode { client_id, link, channel, delivery, payload } => {
                let invalid = self.with_client(client_id, |relay, io| {
                    relay.on_node_packet(link, Packet { channel, delivery, payload }, now, io).is_err()
                });
                if invalid == Some(true) {
                    self.shared.to_node(NodeCmd::Close { link, reason: "invalid game frame".into() });
                }
            }
            WorkerCmd::Flush => {
                let ids: Vec<u64> = self.clients.keys().copied().collect();
                for client_id in ids {
                    self.with_client(client_id, |relay, io| relay.poll_flush(io));
                }
            }
            WorkerCmd::IpcLost => {
                let ids: Vec<u64> = self.clients.keys().copied().collect();
                for client_id in ids {
                    self.force_reconnect(client_id, now);
                }
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                for (_, pending) in self.pending.drain() {
                    self.shared.clear_pending(pending.client_id, pending.session);
                    self.endpoint.reject(now, pending.request, &[], &mut out);
                }
                self.asks.clear();
            }
            WorkerCmd::Status(reply) => {
                let sessions = self.clients.iter().map(|(&id, client)| self.status_of(id, client, now)).collect();
                let _ = reply.send(sessions);
            }
            WorkerCmd::SteamAuth(_) | WorkerCmd::SteamPublish(_) | WorkerCmd::SteamStatus(_) => {}
        }
    }

    fn force_reconnect(&mut self, client_id: u64, now: Instant) {
        let Some(peer) = self.clients.get(&client_id).map(|client| client.peer) else { return };
        let mut control = [0u8; 10];
        control[0..2].copy_from_slice(&1u16.to_le_bytes());
        control[2..10].copy_from_slice(&SERVER_ID.to_le_bytes());
        send_client(&mut self.endpoint, peer, &mut self.scratch, 0, Delivery::ReliableOrdered, &control);
        if !self.force_disconnects.iter().any(|(_, pending)| *pending == peer) {
            self.force_disconnects.push((now + FORCE_RECONNECT_DELAY, peer));
        }
    }

    fn status_of(&self, client_id: u64, client: &Client, now: Instant) -> serde_json::Value {
        let status = client.session.status(now);
        let info = self.endpoint.peer(client.peer);
        json!({
            "clientId": client_id,
            "session": client.session.session,
            "worker": self.config.index,
            "service": "DirectTransport",
            "name": client.name,
            "remote": client.addr.to_string(),
            "link": status.link.map(|link| self.shared.links.name(link)),
            "attached": status.attached,
            "routable": status.routable,
            "switching": status.switching,
            "attachPending": status.attach_pending,
            "bufferServer": status.buffer_server,
            "clientBacklog": status.client_backlog_packets,
            "clientBacklogBytes": status.client_backlog_bytes,
            "serverBacklog": status.server_backlog_packets,
            "serverBacklogBytes": status.server_backlog_bytes,
            "withheldCredit": status.withheld_credit,
            "reliableBacklogMs": status.reliable_backlog_ms,
            "reliableBacklog": self.endpoint.reliable_queue_len(client.peer, 0, true),
            "duplicateServerData": status.duplicate_server_data,
            "rtt": info.as_ref().map(|info| info.rtt_ms),
            "mtu": info.as_ref().map(|info| info.mtu),
            "latencyMs": client.latency_ms,
            "connectedSeconds": now.duration_since(client.connected_at).as_secs(),
            "nodeIdleMs": status.node_idle_ms,
        })
    }

    // ---------------------------------------------------------------- maintenance

    fn maintain(&mut self, now: Instant) {
        let ids: Vec<u64> = self.clients.keys().copied().collect();
        for client_id in ids {
            // Direct clients have LiteNetLib's timeout; no silence watchdog (StartSessionMaintenance).
            self.with_client(client_id, |relay, io| relay.maintain(now, None, io));
        }
        let due: Vec<PeerId> = self.force_disconnects.iter().filter(|(at, _)| *at <= now).map(|(_, peer)| *peer).collect();
        self.force_disconnects.retain(|(at, _)| *at > now);
        for peer in due {
            let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
            self.endpoint.disconnect(peer, &[], &mut out);
        }
        self.maintain_joins(now);
    }

    /// Held joins are re-asked every second until the hold limit; unanswered ones are refused.
    fn maintain_joins(&mut self, now: Instant) {
        self.asks.prune(now);
        let mut reask = Vec::new();
        let mut refuse = Vec::new();
        for (&correlation, pending) in &self.pending {
            let age = now.duration_since(pending.first_asked);
            if pending.held {
                let silent = self.endpoint.request_last_seen(pending.request).is_none_or(|seen| now.duration_since(seen) >= ABANDONED_REQUEST);
                if age >= self.config.join_hold {
                    refuse.push((correlation, "held"));
                } else if silent {
                    refuse.push((correlation, "abandoned"));
                } else if now.duration_since(pending.last_asked) >= Duration::from_secs(1) {
                    reask.push(correlation);
                }
            } else if age >= UNANSWERED_JOIN {
                refuse.push((correlation, "unanswered"));
            }
        }
        for (correlation, why) in refuse {
            if let Some(pending) = self.pending.remove(&correlation) {
                self.shared.clear_pending(pending.client_id, pending.session);
                let age = now.duration_since(pending.first_asked);
                match why {
                    "abandoned" => eprintln!("Dropped held join of client {} after {age:?}: the client stopped asking", pending.client_id),
                    _ => eprintln!("Refused {why} join of client {} after {age:?}", pending.client_id),
                }
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                self.endpoint.reject(now, pending.request, &[], &mut out);
            }
        }
        for correlation in reask {
            if let Some(mut pending) = self.pending.remove(&correlation) {
                pending.last_asked = now;
                if self.shared.ipc.connected() {
                    self.ask(pending);
                } else {
                    self.shared.clear_pending(pending.client_id, pending.session);
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.reject(now, pending.request, &[], &mut out);
                }
            }
        }
    }
}
