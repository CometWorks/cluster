//! The node-link thread: one LiteNetLib client connection per node endpoint (DirectGateway's
//! `_back` manager), the node-link envelope both ways, and the routing of node frames to the
//! worker that owns the client, to ClusterRegistry (control and tunnel frames), or nowhere.

use std::collections::HashMap;
use std::io;
use std::net::{SocketAddr, ToSocketAddrs};
use std::sync::Arc;
use std::time::{Duration, Instant};

use lnl::{ConnectionState, DeliveryMethod, Endpoint, Event, Output, PeerId};
use mio::net::UdpSocket;
use mio::{Events, Interest, Poll, Token, Waker};
use relay::{Delivery, LinkId, Packet};
use serde_json::json;

use crate::ipc::{LinkState, NodeControl, tunnel_body, types};
use crate::nodelink::{self, Envelope};
use crate::shared::{Coalesce, Inbox, Mailbox, Shared};
use crate::worker::WorkerCmd;

const V4: Token = Token(0);
const V6: Token = Token(1);
const WAKE: Token = Token(2);
const TICK: Duration = Duration::from_millis(5);

pub enum NodeCmd {
    /// `EnsureLink`: connect unless connected or connecting.
    Ensure(String),
    /// `DropLink`.
    Drop(String),
    Relay { link: LinkId, client_id: u64, packet: Packet },
    Credit { link: LinkId, client_id: u64, credit: i32 },
    Attach { endpoint: String, client_id: u64, body: Vec<u8> },
    Detach { endpoint: String, client_id: u64 },
    Tunnel { endpoint: String, plugin: bool, delivery: u8, payload: Vec<u8> },
    /// A node sent a frame the gateway refuses: drop the link, as the C# relay does.
    Close { link: LinkId, reason: String },
    Config { join_token: String, channels: u8 },
    Status(crossbeam_channel::Sender<serde_json::Value>),
}

struct Link {
    endpoint: String,
    addr: SocketAddr,
    peer: Option<PeerId>,
    connected: bool,
    connects: u64,
    last_reason: Option<String>,
    /// T-0321 P6: "local" when the node is on this host (loopback or one of this host's addresses), else "lan".
    path: &'static str,
}

/// T-0321 P6: datagrams and bytes exchanged with one node endpoint (LiteNetLib framing included).
#[derive(Default, Clone, Copy)]
struct Traffic {
    tx_packets: u64,
    tx_bytes: u64,
    rx_packets: u64,
    rx_bytes: u64,
}

struct Sockets {
    v4: UdpSocket,
    v6: Option<UdpSocket>,
    send_errors: u64,
    traffic: HashMap<SocketAddr, Traffic>,
}

/// Whether `addr` is this host: a loopback address, or one the kernel would also pick as the source for it (a UDP
/// `connect` sends nothing; it only resolves the route). Decided once per link, when it is first ensured.
fn is_local(addr: SocketAddr) -> bool {
    if addr.ip().is_loopback() {
        return true;
    }
    let bind: SocketAddr = if addr.is_ipv4() { "0.0.0.0:0".parse().unwrap() } else { "[::]:0".parse().unwrap() };
    std::net::UdpSocket::bind(bind)
        .and_then(|socket| socket.connect(addr).and_then(|_| socket.local_addr()))
        .is_ok_and(|local| local.ip() == addr.ip())
}

struct Out<'a> {
    sockets: &'a mut Sockets,
    events: &'a mut Vec<Event>,
}

impl Output for Out<'_> {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        let result = if to.is_ipv4() {
            self.sockets.v4.send_to(datagram, to)
        } else if let Some(v6) = &self.sockets.v6 {
            v6.send_to(datagram, to)
        } else {
            Err(io::ErrorKind::Unsupported.into())
        };
        if result.is_err() {
            self.sockets.send_errors += 1;
        } else {
            let traffic = self.sockets.traffic.entry(to).or_default();
            traffic.tx_packets += 1;
            traffic.tx_bytes += datagram.len() as u64;
        }
    }

    fn event(&mut self, event: Event) {
        self.events.push(event);
    }
}

pub struct NodeThread {
    shared: Arc<Shared>,
    inbox: Inbox<NodeCmd>,
    poll: Poll,
    sockets: Sockets,
    endpoint: Endpoint,
    links: HashMap<LinkId, Link>,
    by_peer: HashMap<PeerId, LinkId>,
    join_token: String,
    channels: u8,
    events: Vec<Event>,
    buffer: Vec<u8>,
    scratch: Vec<u8>,
    invalid_frames: u64,
    unowned_frames: u64,
    coalesce: Coalesce,
}

fn bind(addr: SocketAddr) -> io::Result<UdpSocket> {
    use socket2::{Domain, Protocol, Socket, Type};
    let socket = Socket::new(Domain::for_address(addr), Type::DGRAM, Some(Protocol::UDP))?;
    if addr.is_ipv6() {
        socket.set_only_v6(true)?;
    }
    let _ = socket.set_recv_buffer_size(4 * 1024 * 1024);
    let _ = socket.set_send_buffer_size(4 * 1024 * 1024);
    socket.set_nonblocking(true)?;
    socket.bind(&addr.into())?;
    Ok(UdpSocket::from_std(socket.into()))
}

fn delivery_of(method: DeliveryMethod) -> Delivery {
    Delivery::from_byte(method as u8).unwrap_or(Delivery::ReliableOrdered)
}

fn method_of(delivery: u8) -> DeliveryMethod {
    DeliveryMethod::from_u8(delivery).unwrap_or(DeliveryMethod::ReliableOrdered)
}

impl NodeThread {
    pub fn new(shared: Arc<Shared>, config: lnl::Config, join_token: String, channels: u8) -> io::Result<(Self, Mailbox<NodeCmd>)> {
        let poll = Poll::new()?;
        let mut v4 = bind("0.0.0.0:0".parse().unwrap())?;
        poll.registry().register(&mut v4, V4, Interest::READABLE)?;
        let v6 = match bind("[::]:0".parse().unwrap()) {
            Ok(mut v6) => {
                poll.registry().register(&mut v6, V6, Interest::READABLE)?;
                Some(v6)
            }
            Err(_) => None,
        };
        let (mailbox, inbox) = Mailbox::new(Waker::new(poll.registry(), WAKE)?);
        let endpoint = Endpoint::new(config, Instant::now());
        Ok((
            Self {
                shared,
                inbox,
                poll,
                sockets: Sockets { v4, v6, send_errors: 0, traffic: HashMap::new() },
                endpoint,
                links: HashMap::new(),
                by_peer: HashMap::new(),
                join_token,
                channels: channels.max(1),
                events: Vec::new(),
                buffer: vec![0; 64 * 1024],
                scratch: Vec::with_capacity(2048),
                invalid_frames: 0,
                unowned_frames: 0,
                coalesce: Coalesce::new(TICK, Instant::now()),
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
                eprintln!("ERROR: node-link poll failed: {error}");
                return;
            }
            self.inbox.unpark();
            let now = Instant::now();
            self.receive(true, now);
            self.receive(false, now);
            while let Ok(command) = self.inbox.rx.try_recv() {
                self.coalesce.touch();
                self.command(command, Instant::now());
            }
            let now = Instant::now();
            if self.coalesce.due(now) {
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                self.endpoint.poll(now, &mut out);
                self.drain_events();
                self.coalesce.flushed(now);
            }
        }
    }

    fn receive(&mut self, v4: bool, now: Instant) {
        loop {
            let socket = if v4 { Some(&self.sockets.v4) } else { self.sockets.v6.as_ref() };
            let Some(socket) = socket else { return };
            match socket.recv_from(&mut self.buffer) {
                Ok((length, from)) => {
                    self.coalesce.touch();
                    if let Some(traffic) = self.sockets.traffic.get_mut(&from) {
                        traffic.rx_packets += 1;
                        traffic.rx_bytes += length as u64;
                    }
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.handle(now, from, &self.buffer[..length], &mut out);
                    self.drain_events();
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => return,
                Err(_) => {}
            }
        }
    }

    fn drain_events(&mut self) {
        while !self.events.is_empty() {
            for event in std::mem::take(&mut self.events) {
                self.event(event);
            }
        }
    }

    fn event(&mut self, event: Event) {
        match event {
            Event::Connected { peer, .. } => {
                let Some(&id) = self.by_peer.get(&peer) else { return };
                let Some(link) = self.links.get_mut(&id) else { return };
                link.connected = true;
                link.last_reason = None;
                self.shared.links.set_connected(id, true);
                eprintln!("Connected node link {}", link.endpoint);
                self.shared.ipc.send_json(types::LINK_STATE, 0, &LinkState { endpoint: &link.endpoint, connected: true, reason: None });
                self.shared.to_all_workers(|| WorkerCmd::Flush);
            }
            Event::Disconnected { peer, reason, data, .. } => {
                if let Some(data) = data {
                    self.endpoint.recycle(data);
                }
                let Some(id) = self.by_peer.remove(&peer) else { return };
                let Some(link) = self.links.get_mut(&id) else { return };
                if link.peer != Some(peer) {
                    return;
                }
                link.peer = None;
                let was_connected = std::mem::replace(&mut link.connected, false);
                let reason = format!("{reason:?}");
                link.last_reason = Some(reason.clone());
                self.shared.links.set_connected(id, false);
                eprintln!("Disconnected node link {}: {reason}", link.endpoint);
                // A connect that never succeeded is reported too: C# retries it with EnsureLink.
                let _ = was_connected;
                self.shared.ipc.send_json(types::LINK_STATE, 0, &LinkState { endpoint: &link.endpoint, connected: false, reason: Some(reason) });
            }
            Event::Receive { peer, method, packet, .. } => {
                let link = self.by_peer.get(&peer).copied();
                if let Some(link) = link {
                    self.node_frame(link, peer, method, packet.data());
                }
                self.endpoint.recycle(packet);
            }
            Event::ConnectionRequest { request, .. } => {
                // Nodes never connect to the gateway's node-link socket.
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                self.endpoint.reject_force(request, &[], &mut out);
            }
            Event::LatencyUpdate { .. } | Event::PeerAddressChanged { .. } => {}
        }
    }

    fn node_frame(&mut self, link: LinkId, peer: PeerId, method: DeliveryMethod, datagram: &[u8]) {
        let envelope = match nodelink::decode(datagram) {
            Ok(envelope) => envelope,
            Err(invalid) => {
                self.invalid_frames += 1;
                let endpoint = self.links.get(&link).map(|l| l.endpoint.clone()).unwrap_or_default();
                eprintln!("Receive failed from node link {endpoint}: {}; disconnecting it", invalid.0);
                let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                self.endpoint.disconnect(peer, &[], &mut out);
                return;
            }
        };
        let endpoint = || self.links.get(&link).map(|l| l.endpoint.as_str()).unwrap_or_default();
        match envelope {
            Envelope::Relay { client_id, channel, payload } => match self.shared.route_owner(client_id).and_then(|o| self.shared.worker(o.worker)) {
                Some(worker) => worker.send(WorkerCmd::FromNode {
                    client_id,
                    link,
                    channel,
                    delivery: delivery_of(method),
                    payload: payload.to_vec(),
                }),
                None => self.unowned_frames += 1,
            },
            Envelope::AttachAck { client_id } | Envelope::DetachAck { client_id } | Envelope::Detach { client_id } => {
                let kind = match envelope {
                    Envelope::AttachAck { .. } => "attachAck",
                    Envelope::DetachAck { .. } => "detachAck",
                    _ => "detach",
                };
                if let Some(owner) = self.shared.route_owner(client_id) {
                    let control = NodeControl { endpoint: endpoint(), kind, client_id, session: owner.session };
                    self.shared.ipc.send_json(types::NODE_CONTROL, 0, &control);
                }
            }
            Envelope::Global { payload } | Envelope::Plugin { payload } => {
                let ty = if matches!(envelope, Envelope::Plugin { .. }) { types::NODE_PLUGIN } else { types::NODE_GLOBAL };
                self.shared.ipc.send_raw(ty, 0, &tunnel_body(method as u8, endpoint(), payload));
            }
            // A node never sends these to the gateway; the C# relay ignores them.
            Envelope::Attach { .. } | Envelope::Credit { .. } => {}
        }
    }

    fn command(&mut self, command: NodeCmd, now: Instant) {
        match command {
            NodeCmd::Ensure(endpoint) => self.ensure(&endpoint, now),
            NodeCmd::Drop(endpoint) => {
                if let Some(id) = self.shared.links.id(&endpoint)
                    && let Some(peer) = self.links.get(&id).and_then(|link| link.peer)
                {
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.disconnect(peer, &[], &mut out);
                }
            }
            NodeCmd::Relay { link, client_id, packet } => {
                let envelope = Envelope::Relay { client_id, channel: packet.channel, payload: &packet.payload };
                self.send(link, client_id, &envelope, method_of(packet.delivery as u8));
            }
            NodeCmd::Credit { link, client_id, credit } => {
                self.send(link, client_id, &Envelope::Credit { client_id, credit }, DeliveryMethod::ReliableOrdered);
            }
            NodeCmd::Attach { endpoint, client_id, body } => {
                if let Some(link) = self.shared.links.id(&endpoint) {
                    self.send(link, client_id, &Envelope::Attach { client_id, body: &body }, DeliveryMethod::ReliableOrdered);
                }
            }
            NodeCmd::Detach { endpoint, client_id } => {
                if let Some(link) = self.shared.links.id(&endpoint) {
                    self.send(link, client_id, &Envelope::Detach { client_id }, DeliveryMethod::ReliableOrdered);
                }
            }
            NodeCmd::Tunnel { endpoint, plugin, delivery, payload } => {
                if let Some(link) = self.shared.links.id(&endpoint) {
                    let envelope = if plugin { Envelope::Plugin { payload: &payload } } else { Envelope::Global { payload: &payload } };
                    self.send(link, 0, &envelope, method_of(delivery));
                }
            }
            NodeCmd::Close { link, reason } => {
                if let Some(peer) = self.links.get(&link).and_then(|l| l.peer) {
                    eprintln!("Closing node link {}: {reason}", self.shared.links.name(link));
                    let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
                    self.endpoint.disconnect(peer, &[], &mut out);
                }
            }
            NodeCmd::Config { join_token, channels } => {
                self.join_token = join_token;
                self.channels = channels.max(1);
            }
            NodeCmd::Status(reply) => {
                let links: Vec<_> = self
                    .links
                    .values()
                    .map(|link| {
                        let info = link.peer.and_then(|peer| self.endpoint.peer(peer));
                        let traffic = self.sockets.traffic.get(&link.addr).copied().unwrap_or_default();
                        json!({
                            "endpoint": link.endpoint,
                            "connected": link.connected,
                            "rtt": info.as_ref().map(|i| i.rtt_ms),
                            "mtu": info.as_ref().map(|i| i.mtu),
                            "connects": link.connects,
                            "lastReason": link.last_reason,
                            // T-0321 P6: which path the node link (CHANNELS.md D3) uses, and what crossed it.
                            "transport": "udp",
                            "path": link.path,
                            "txPackets": traffic.tx_packets,
                            "txBytes": traffic.tx_bytes,
                            "rxPackets": traffic.rx_packets,
                            "rxBytes": traffic.rx_bytes,
                        })
                    })
                    .collect();
                let _ = reply.send(json!({
                    "nodes": links,
                    "invalidNodeFrames": self.invalid_frames,
                    "unownedNodeFrames": self.unowned_frames,
                    "nodeSendErrors": self.sockets.send_errors,
                }));
            }
        }
    }

    /// `SendNodeLink`: on the client's channel; an Unreliable frame over the MTU goes ReliableUnordered.
    fn send(&mut self, link: LinkId, client_id: u64, envelope: &Envelope<'_>, mut method: DeliveryMethod) {
        let Some(peer) = self.links.get(&link).filter(|l| l.connected).and_then(|l| l.peer) else { return };
        if nodelink::encode_into(&mut self.scratch, envelope).is_err() {
            return;
        }
        if method == DeliveryMethod::Unreliable
            && self.endpoint.max_single_packet_size(peer, method).is_some_and(|max| self.scratch.len() > max)
        {
            method = DeliveryMethod::ReliableUnordered;
        }
        let channel = nodelink::channel_of(client_id, self.channels);
        let _ = self.endpoint.send(peer, &self.scratch, channel, method);
    }

    fn ensure(&mut self, endpoint: &str, now: Instant) {
        let Some(id) = self.shared.links.id(endpoint) else { return };
        let link = match self.links.entry(id) {
            std::collections::hash_map::Entry::Occupied(entry) => entry.into_mut(),
            std::collections::hash_map::Entry::Vacant(entry) => {
                let Some(addr) = endpoint.to_socket_addrs().ok().and_then(|mut addrs| addrs.next()) else {
                    eprintln!("WARNING: node link endpoint {endpoint} does not resolve");
                    return;
                };
                self.sockets.traffic.entry(addr).or_default();
                entry.insert(Link { endpoint: endpoint.to_owned(), addr, peer: None, connected: false, connects: 0, last_reason: None, path: if is_local(addr) { "local" } else { "lan" } })
            }
        };
        if let Some(peer) = link.peer
            && self.endpoint.peer(peer).is_some_and(|info| matches!(info.state, ConnectionState::Connected | ConnectionState::Outgoing))
        {
            return;
        }
        let data = nodelink::node_link_handshake(&self.join_token);
        let mut out = Out { sockets: &mut self.sockets, events: &mut self.events };
        match self.endpoint.connect(now, link.addr, &data, &mut out) {
            Ok(peer) => {
                link.peer = Some(peer);
                link.connects += 1;
                self.by_peer.insert(peer, id);
            }
            Err(error) => eprintln!("WARNING: node link {endpoint} connect failed: {error:?}"),
        }
        self.drain_events();
    }
}

#[cfg(test)]
mod tests {
    use super::is_local;

    #[test]
    fn loopback_and_own_address_are_local_a_foreign_address_is_not() {
        assert!(is_local("127.0.0.1:28417".parse().unwrap()));
        assert!(is_local("[::1]:28417".parse().unwrap()) || std::net::UdpSocket::bind("[::1]:0").is_err());
        // TEST-NET-1 (RFC 5737) is never this host.
        assert!(!is_local("192.0.2.1:28417".parse().unwrap()));
        // This host's own LAN address (the source the kernel picks toward a foreign address), when there is a route.
        let probe = std::net::UdpSocket::bind("0.0.0.0:0").unwrap();
        if probe.connect("192.0.2.1:9").is_ok() {
            let own = probe.local_addr().unwrap().ip();
            assert!(is_local(std::net::SocketAddr::new(own, 28417)), "own address {own} must be local");
        }
    }
}
