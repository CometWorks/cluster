//! NetManager.cs without sockets and threads.

use std::collections::{HashMap, VecDeque};
use std::fmt;
use std::net::SocketAddr;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use crate::consts::*;
use crate::packet::*;
use crate::peer::*;
use crate::{Event, Output, Packet, PeerId, RequestId};

/// `NetManager` settings. Defaults are LiteNetLib's; the gateway front uses
/// `channels_count = 16, disconnect_timeout_ms = 120_000, max_connect_attempts = 20`, node links
/// `disconnect_timeout_ms = 30_000` (DirectGateway.cs `CreateManager`).
#[derive(Clone, Debug)]
pub struct Config {
    /// `ChannelsCount` (1..=64) per delivery method.
    pub channels_count: u8,
    /// `DisconnectTimeout`, ms.
    pub disconnect_timeout_ms: u32,
    /// `PingInterval`, ms.
    pub ping_interval_ms: u32,
    /// `ReconnectDelay`, ms: ConnectRequest resend interval.
    pub reconnect_delay_ms: u32,
    /// `MaxConnectAttempts`.
    pub max_connect_attempts: u32,
    /// `MtuDiscovery`.
    pub mtu_discovery: bool,
    /// `MtuOverride` (0 = off).
    pub mtu_override: usize,
    /// `AllowPeerAddressChange`.
    pub allow_peer_address_change: bool,
    /// `PacketPoolSize`.
    pub packet_pool_size: usize,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            channels_count: 1,
            disconnect_timeout_ms: 5000,
            ping_interval_ms: 1000,
            reconnect_delay_ms: 500,
            max_connect_attempts: 10,
            mtu_discovery: false,
            mtu_override: 0,
            allow_peer_address_change: false,
            packet_pool_size: 1000,
        }
    }
}

/// Maps the monotonic clock to .NET `DateTime.UtcNow.Ticks` (100 ns since 0001-01-01), which
/// LiteNetLib uses as the connection time and in Pong.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Clock {
    anchor: Instant,
    anchor_ticks: i64,
}

impl Clock {
    pub(crate) fn utc_ticks(&self, now: Instant) -> i64 {
        match now.checked_duration_since(self.anchor) {
            Some(d) => self.anchor_ticks + (d.as_nanos() / 100) as i64,
            None => self.anchor_ticks - (self.anchor.duration_since(now).as_nanos() / 100) as i64,
        }
    }
}

/// Why [`Endpoint::send`] refused a payload.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SendError {
    /// Unknown peer handle.
    UnknownPeer,
    /// The peer is not in the Connected state (LiteNetLib drops silently).
    NotConnected,
    /// Channel number not below `channels_count`.
    InvalidChannel,
    /// Unreliable, Sequenced or ReliableSequenced payload above the MTU (`TooBigPacketException`).
    TooBig { max: usize },
    /// More than 65535 fragments.
    TooManyFragments,
}

impl fmt::Display for SendError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

impl std::error::Error for SendError {}

/// Why [`Endpoint::connect`] did not create a peer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConnectError {
    /// A connection request from that address is pending (LiteNetLib returns null).
    RequestPending,
}

impl fmt::Display for ConnectError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

impl std::error::Error for ConnectError {}

/// A snapshot of a peer's public state (`NetPeer` properties).
#[derive(Clone, Copy, Debug)]
pub struct PeerInfo {
    pub addr: SocketAddr,
    pub state: ConnectionState,
    /// `NetPeer.Id`.
    pub id: u32,
    /// `NetPeer.RemoteId`: the remote's id for us.
    pub remote_id: i32,
    /// `RoundTripTime`, ms.
    pub rtt_ms: i32,
    /// `Ping` (RTT / 2), ms.
    pub ping_ms: i32,
    pub mtu: usize,
    /// `TimeSinceLastPacket`, ms.
    pub time_since_last_packet_ms: f64,
    /// `ConnectTime` (.NET ticks).
    pub connect_time: i64,
    /// `ConnectionNum`.
    pub connection_number: u8,
    /// `RemoteTimeDelta`, .NET ticks.
    pub remote_time_delta: i64,
    /// Current reliable resend delay, ms.
    pub resend_delay_ms: f64,
    pub packets_sent: u64,
    pub bytes_sent: u64,
    pub packets_received: u64,
    pub bytes_received: u64,
}

struct PendingRequest {
    serial: u64,
    info: ConnectRequestInfo,
    /// The last ConnectRequest (first or resent) for it; a client that gave up stops resending.
    last_seen: Instant,
}

/// One LiteNetLib `NetManager`: peers, pending connection requests and the packet pool. It owns
/// no socket and no thread; see the crate documentation.
pub struct Endpoint {
    config: Config,
    peers: Vec<Option<Peer>>,
    by_addr: HashMap<SocketAddr, u32>,
    free_ids: VecDeque<u32>,
    last_peer_id: u32,
    generation: u32,
    requests: HashMap<SocketAddr, PendingRequest>,
    request_serial: u64,
    pool: Pool,
    last_update: Instant,
    clock: Clock,
}

impl Endpoint {
    /// A new endpoint; `now` starts the logic clock and anchors wall-clock ticks to `SystemTime::now()`.
    pub fn new(config: Config, now: Instant) -> Self {
        let wall = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default();
        let ticks = UNIX_EPOCH_TICKS + (wall.as_nanos() / 100) as i64;
        Self::with_clock(config, now, ticks)
    }

    /// As [`Endpoint::new`] with an explicit wall clock: `utc_ticks` is the .NET tick count at `now`.
    pub fn with_clock(config: Config, now: Instant, utc_ticks: i64) -> Self {
        assert!(
            (1..=64).contains(&config.channels_count),
            "channels_count must be between 1 and 64"
        );
        let pool = Pool::new(config.packet_pool_size);
        Endpoint {
            config,
            peers: Vec::new(),
            by_addr: HashMap::new(),
            free_ids: VecDeque::new(),
            last_peer_id: 0,
            generation: 0,
            requests: HashMap::new(),
            request_serial: 0,
            pool,
            last_update: now,
            clock: Clock {
                anchor: now,
                anchor_ticks: utc_ticks,
            },
        }
    }

    pub fn config(&self) -> &Config {
        &self.config
    }

    /// .NET `DateTime.UtcNow.Ticks` at `now` as this endpoint sees it.
    pub fn utc_ticks(&self, now: Instant) -> i64 {
        self.clock.utc_ticks(now)
    }

    /// Gives a received payload's buffer back to the pool.
    pub fn recycle(&mut self, packet: Packet) {
        self.pool.put(packet.buf);
    }

    fn peer_ref(&self, peer: PeerId) -> Option<&Peer> {
        self.peers
            .get(peer.id as usize)?
            .as_ref()
            .filter(|p| p.handle == peer)
    }

    /// The peer at `addr`, in whatever state.
    pub fn peer_by_addr(&self, addr: &SocketAddr) -> Option<PeerId> {
        let id = *self.by_addr.get(addr)?;
        self.peers[id as usize].as_ref().map(|p| p.handle)
    }

    /// The peer with LiteNetLib id `id` (`GetPeerById`).
    pub fn peer_by_id(&self, id: u32) -> Option<PeerId> {
        self.peers.get(id as usize)?.as_ref().map(|p| p.handle)
    }

    pub fn peer(&self, peer: PeerId) -> Option<PeerInfo> {
        let p = self.peer_ref(peer)?;
        Some(PeerInfo {
            addr: p.link.addr,
            state: p.state,
            id: p.handle.id,
            remote_id: p.remote_id,
            rtt_ms: p.round_trip_time(),
            ping_ms: p.round_trip_time() / 2,
            mtu: p.link.mtu,
            time_since_last_packet_ms: p.time_since_last_packet,
            connect_time: p.connect_time,
            connection_number: p.link.connect_num,
            remote_time_delta: p.remote_delta,
            resend_delay_ms: p.resend_delay(),
            packets_sent: p.link.packets_sent,
            bytes_sent: p.link.bytes_sent,
            packets_received: p.packets_received,
            bytes_received: p.bytes_received,
        })
    }

    /// All peers (any state), in id order.
    pub fn peers(&self) -> impl Iterator<Item = PeerId> + '_ {
        self.peers.iter().flatten().map(|p| p.handle)
    }

    /// Number of peers in the Connected state.
    pub fn connected_peers_count(&self) -> usize {
        self.peers
            .iter()
            .flatten()
            .filter(|p| p.state == ConnectionState::Connected)
            .count()
    }

    /// `GetPacketsCountInReliableQueue`: packets queued on a reliable channel and not yet in its window.
    pub fn reliable_queue_len(&self, peer: PeerId, channel: u8, ordered: bool) -> usize {
        let idx = channel as usize * CHANNEL_TYPE_COUNT + if ordered { 2 } else { 0 };
        self.peer_ref(peer).map_or(0, |p| p.channel_queue_len(idx))
    }

    /// Packets queued plus in flight (sent, unacknowledged) on one channel of any type.
    pub fn channel_backlog(&self, peer: PeerId, channel: u8, method: DeliveryMethod) -> usize {
        if method == DeliveryMethod::Unreliable {
            return 0;
        }
        let idx = channel as usize * CHANNEL_TYPE_COUNT + method as usize;
        self.peer_ref(peer).map_or(0, |p| {
            if idx >= p.channel_count() {
                0
            } else {
                p.channel_queue_len(idx) + p.channel_in_flight(idx)
            }
        })
    }

    /// `GetMaxSinglePacketSize`.
    pub fn max_single_packet_size(&self, peer: PeerId, method: DeliveryMethod) -> Option<usize> {
        self.peer_ref(peer)
            .map(|p| p.max_single_packet_size(method))
    }

    /// `NetPeer.Send`: queues a payload; it is sent on the next [`Endpoint::poll`].
    pub fn send(
        &mut self,
        peer: PeerId,
        data: &[u8],
        channel: u8,
        method: DeliveryMethod,
    ) -> Result<(), SendError> {
        let Endpoint { peers, pool, .. } = self;
        let p = peers
            .get_mut(peer.id as usize)
            .and_then(|p| p.as_mut())
            .filter(|p| p.handle == peer)
            .ok_or(SendError::UnknownPeer)?;
        p.send(data, channel, method, pool)
    }

    fn next_peer_id(&mut self) -> PeerId {
        let id = self.free_ids.pop_front().unwrap_or_else(|| {
            let id = self.last_peer_id;
            self.last_peer_id += 1;
            id
        });
        self.generation = self.generation.wrapping_add(1);
        PeerId {
            id,
            generation: self.generation,
        }
    }

    fn add_peer(&mut self, peer: Peer) {
        let id = peer.handle.id as usize;
        if id >= self.peers.len() {
            self.peers
                .resize_with((id + 1).max(self.peers.len() * 2).max(32), || None);
        }
        self.by_addr.insert(peer.link.addr, peer.handle.id);
        self.peers[id] = Some(peer);
    }

    fn remove_peer(&mut self, id: u32) {
        if let Some(p) = self.peers[id as usize].take() {
            if self.by_addr.get(&p.link.addr) == Some(&id) {
                self.by_addr.remove(&p.link.addr);
            }
            self.free_ids.push_back(id);
            p.recycle_into(&mut self.pool);
        }
    }

    fn ctx<'a, 'o>(
        pool: &'a mut Pool,
        clock: &'a Clock,
        config: &'a Config,
        now: Instant,
        out: &'a mut (dyn Output + 'o),
    ) -> Ctx<'a, 'o> {
        Ctx {
            out,
            pool,
            now,
            clock,
            config,
        }
    }

    /// `NetManager.DisconnectPeer(peer, reason, ..., force, data, eventData)`.
    fn disconnect_peer_internal(
        &mut self,
        id: u32,
        reason: DisconnectReason,
        force: bool,
        data: &[u8],
        event_data: Option<Packet>,
        out: &mut dyn Output,
    ) {
        let Some(p) = self.peers[id as usize].as_mut() else {
            return;
        };
        if p.shutdown(data, force, out) == ShutdownResult::None {
            if let Some(d) = event_data {
                self.pool.put(d.buf);
            }
            return;
        }
        out.event(Event::Disconnected {
            peer: p.handle,
            addr: p.link.addr,
            reason,
            data: event_data,
        });
    }

    /// `NetManager.Connect`: starts an outgoing connection with `connect_data` as the request data.
    /// Returns the existing peer when one at `addr` is Outgoing or Connected.
    pub fn connect(
        &mut self,
        now: Instant,
        addr: SocketAddr,
        connect_data: &[u8],
        out: &mut dyn Output,
    ) -> Result<PeerId, ConnectError> {
        if self.requests.contains_key(&addr) {
            return Err(ConnectError::RequestPending);
        }
        let mut connect_num = 0;
        if let Some(&id) = self.by_addr.get(&addr) {
            let p = self.peers[id as usize].as_ref().unwrap();
            if p.state == ConnectionState::Outgoing || p.state == ConnectionState::Connected {
                return Ok(p.handle);
            }
            connect_num = (p.link.connect_num + 1) % MAX_CONNECTION_NUMBER;
            self.remove_peer(id);
        }
        let handle = self.next_peer_id();
        let mut ctx = Self::ctx(&mut self.pool, &self.clock, &self.config, now, out);
        let peer = Peer::new_outgoing(handle, addr, connect_num, connect_data, &mut ctx);
        self.add_peer(peer);
        Ok(handle)
    }

    /// `NetManager.DisconnectPeer`: graceful disconnect with optional data (raises the Disconnected
    /// event with `DisconnectPeerCalled` at once; the Disconnect packet is resent until ShutdownOk).
    pub fn disconnect(&mut self, peer: PeerId, data: &[u8], out: &mut dyn Output) {
        if self.peer_ref(peer).is_some() {
            self.disconnect_peer_internal(
                peer.id,
                DisconnectReason::DisconnectPeerCalled,
                false,
                data,
                None,
                out,
            );
        }
    }

    /// `NetManager.DisconnectPeerForce`: no packet is sent.
    pub fn disconnect_force(&mut self, peer: PeerId, out: &mut dyn Output) {
        if self.peer_ref(peer).is_some() {
            self.disconnect_peer_internal(
                peer.id,
                DisconnectReason::DisconnectPeerCalled,
                true,
                &[],
                None,
                out,
            );
        }
    }

    /// `NetManager.DisconnectAll`.
    pub fn disconnect_all(&mut self, data: &[u8], out: &mut dyn Output) {
        for id in 0..self.peers.len() as u32 {
            if self.peers[id as usize].is_some() {
                self.disconnect_peer_internal(
                    id,
                    DisconnectReason::DisconnectPeerCalled,
                    false,
                    data,
                    None,
                    out,
                );
            }
        }
    }

    /// `NetManager.Stop`: shuts every peer down (sending one Disconnect each when
    /// `send_disconnect_messages`), raises no events and forgets all peers and requests.
    pub fn stop(&mut self, send_disconnect_messages: bool, out: &mut dyn Output) {
        for id in 0..self.peers.len() {
            if let Some(p) = self.peers[id].as_mut() {
                p.shutdown(&[], !send_disconnect_messages, out);
            }
        }
        for id in 0..self.peers.len() as u32 {
            self.remove_peer(id);
        }
        self.peers.clear();
        self.by_addr.clear();
        self.free_ids.clear();
        self.last_peer_id = 0;
        self.requests.clear();
    }

    /// The latest request data of a pending connection request (`ConnectionRequest.Data`).
    pub fn request_data(&self, request: RequestId) -> Option<&[u8]> {
        self.requests
            .get(&request.addr)
            .filter(|r| r.serial == request.serial)
            .map(|r| r.info.data.as_slice())
    }

    /// When the client last sent (or resent) a pending request's ConnectRequest. Not LiteNetLib API:
    /// a server that answers a request only after a long wait uses it to tell a waiting client
    /// from one that gave up (LiteNetLib's own ConnectionRequest never expires).
    pub fn request_last_seen(&self, request: RequestId) -> Option<Instant> {
        self.requests
            .get(&request.addr)
            .filter(|r| r.serial == request.serial)
            .map(|r| r.last_seen)
    }

    /// Pending connection requests.
    pub fn pending_requests(&self) -> impl Iterator<Item = RequestId> + '_ {
        self.requests.iter().map(|(a, r)| RequestId {
            addr: *a,
            serial: r.serial,
        })
    }

    fn take_request(&mut self, request: RequestId) -> Option<ConnectRequestInfo> {
        match self.requests.get(&request.addr) {
            Some(r) if r.serial == request.serial => {
                self.requests.remove(&request.addr).map(|r| r.info)
            }
            _ => None,
        }
    }

    /// `ConnectionRequest.Accept`. Returns the new peer (or the peer already at that address, as
    /// LiteNetLib does), or `None` if the request is no longer pending.
    pub fn accept(
        &mut self,
        now: Instant,
        request: RequestId,
        out: &mut dyn Output,
    ) -> Option<PeerId> {
        let info = self.take_request(request)?;
        if let Some(&id) = self.by_addr.get(&request.addr) {
            return self.peers[id as usize].as_ref().map(|p| p.handle);
        }
        let handle = self.next_peer_id();
        let mut ctx = Self::ctx(&mut self.pool, &self.clock, &self.config, now, out);
        let peer = Peer::new_incoming(handle, request.addr, &info, &mut ctx);
        self.add_peer(peer);
        out.event(Event::Connected {
            peer: handle,
            addr: request.addr,
        });
        Some(handle)
    }

    /// `ConnectionRequest.Reject(data)`: a transient peer resends a Disconnect carrying `data` until
    /// the remote answers ShutdownOk. Returns false if the request is no longer pending.
    pub fn reject(
        &mut self,
        now: Instant,
        request: RequestId,
        data: &[u8],
        out: &mut dyn Output,
    ) -> bool {
        let Some(info) = self.take_request(request) else {
            return false;
        };
        if !self.by_addr.contains_key(&request.addr) {
            let handle = self.next_peer_id();
            let mut ctx = Self::ctx(&mut self.pool, &self.clock, &self.config, now, out);
            let peer = Peer::new_rejecting(handle, request.addr, &info, data, &mut ctx);
            self.add_peer(peer);
        }
        true
    }

    /// `ConnectionRequest.RejectForce(data)`: one Disconnect (only when `data` is not empty), no peer.
    pub fn reject_force(&mut self, request: RequestId, data: &[u8], out: &mut dyn Output) -> bool {
        let Some(info) = self.take_request(request) else {
            return false;
        };
        if !data.is_empty() {
            let mut pkt = vec![0u8; PacketProperty::Disconnect.header_size() + data.len()];
            set_property(&mut pkt, PacketProperty::Disconnect);
            set_connection_number(&mut pkt, info.connection_number);
            put_i64(&mut pkt, 1, info.connection_time);
            if pkt.len() < POSSIBLE_MTU[0] {
                pkt[9..].copy_from_slice(data);
            }
            out.transmit(request.addr, &pkt);
        }
        true
    }

    /// Forgets a pending request without answering it (not in LiteNetLib, where an unanswered
    /// request stays forever). The remote keeps retrying until its `max_connect_attempts`.
    pub fn drop_request(&mut self, request: RequestId) -> bool {
        self.take_request(request).is_some()
    }

    /// `NetManager.OnMessageReceived`/`HandleMessageReceived`: processes one datagram from `from`.
    pub fn handle(
        &mut self,
        now: Instant,
        from: SocketAddr,
        datagram: &[u8],
        out: &mut dyn Output,
    ) {
        if datagram.is_empty() || datagram.len() > MAX_PACKET_SIZE || !verify(datagram) {
            return;
        }
        let Some(prop) = property(datagram) else {
            return;
        };
        match prop {
            PacketProperty::ConnectRequest => {
                if i32_at(datagram, 1) != PROTOCOL_ID {
                    out.transmit(from, &[PacketProperty::InvalidProtocol as u8]);
                    return;
                }
            }
            PacketProperty::Broadcast
            | PacketProperty::UnconnectedMessage
            | PacketProperty::NatMessage => return,
            _ => {}
        }
        let known = self.by_addr.get(&from).copied();
        if let Some(id) = known {
            let p = self.peers[id as usize].as_mut().unwrap();
            p.packets_received += 1;
            p.bytes_received += datagram.len() as u64;
        }
        match prop {
            PacketProperty::ConnectRequest => {
                if let Some(req) = ConnectRequestInfo::parse(datagram) {
                    self.process_connect_request(now, from, known, req, out);
                }
            }
            PacketProperty::PeerNotFound => {
                self.process_peer_not_found(now, from, known, datagram, out)
            }
            PacketProperty::InvalidProtocol => {
                if let Some(id) = known
                    && self.peers[id as usize].as_ref().unwrap().state == ConnectionState::Outgoing
                {
                    self.disconnect_peer_internal(
                        id,
                        DisconnectReason::InvalidProtocol,
                        true,
                        &[],
                        None,
                        out,
                    );
                }
            }
            PacketProperty::Disconnect => {
                if let Some(id) = known {
                    let result = self.peers[id as usize]
                        .as_ref()
                        .unwrap()
                        .process_disconnect(datagram);
                    if result != DisconnectResult::None {
                        let reason = if result == DisconnectResult::Disconnect {
                            DisconnectReason::RemoteConnectionClose
                        } else {
                            DisconnectReason::ConnectionRejected
                        };
                        let data = Packet::new(
                            self.pool.copy_of(datagram),
                            PacketProperty::Disconnect.header_size(),
                        );
                        self.disconnect_peer_internal(id, reason, true, &[], Some(data), out);
                    }
                }
                out.transmit(from, &[PacketProperty::ShutdownOk as u8]);
            }
            PacketProperty::ConnectAccept => {
                if let Some(id) = known
                    && let Some(acc) = ConnectAcceptInfo::parse(datagram)
                {
                    let p = self.peers[id as usize].as_mut().unwrap();
                    if p.process_connect_accept(&acc) {
                        out.event(Event::Connected {
                            peer: p.handle,
                            addr: from,
                        });
                    }
                }
            }
            _ => match known {
                Some(id) => {
                    let Endpoint {
                        peers,
                        pool,
                        clock,
                        config,
                        ..
                    } = self;
                    let mut ctx = Self::ctx(pool, clock, config, now, out);
                    peers[id as usize]
                        .as_mut()
                        .unwrap()
                        .process_packet(datagram, &mut ctx);
                }
                None => out.transmit(from, &[PacketProperty::PeerNotFound as u8]),
            },
        }
    }

    fn process_peer_not_found(
        &mut self,
        _now: Instant,
        from: SocketAddr,
        known: Option<u32>,
        datagram: &[u8],
        out: &mut dyn Output,
    ) {
        if let Some(id) = known {
            let p = self.peers[id as usize].as_mut().unwrap();
            if p.state == ConnectionState::Connected {
                if datagram.len() == 1 {
                    p.reset_mtu(&self.config);
                    let pkt = ConnectAcceptInfo::make_network_changed(
                        p.connect_time,
                        p.link.connect_num,
                        p.remote_id,
                    );
                    p.link.send_raw(&pkt, out);
                } else if datagram.len() == 2 && datagram[1] == 1 {
                    self.disconnect_peer_internal(
                        id,
                        DisconnectReason::PeerNotFound,
                        true,
                        &[],
                        None,
                        out,
                    );
                }
            }
            return;
        }
        if datagram.len() <= 1 {
            return;
        }
        let mut handled = false;
        if self.config.allow_peer_address_change
            && let Some(acc) = ConnectAcceptInfo::parse(datagram)
            && acc.peer_network_changed
            && (acc.peer_id as usize) < self.peers.len()
            && let Some(p) = self.peers[acc.peer_id as usize].as_mut()
            && p.connect_time == acc.connection_time
            && p.link.connect_num == acc.connection_number
        {
            if p.state == ConnectionState::Connected {
                // InitiateEndPointChange + the PeerAddressChanged event processing (FinishEndPointChange).
                p.reset_mtu(&self.config);
                let previous = p.link.addr;
                let id = p.handle.id;
                if self.by_addr.get(&previous) == Some(&id) {
                    self.by_addr.remove(&previous);
                }
                p.link.addr = from;
                self.by_addr.insert(from, id);
                out.event(Event::PeerAddressChanged {
                    peer: p.handle,
                    previous,
                    addr: from,
                });
            }
            handled = true;
        }
        if !handled {
            out.transmit(from, &[PacketProperty::PeerNotFound as u8, 1]);
        }
    }

    /// `NetManager.ProcessConnectRequest`.
    fn process_connect_request(
        &mut self,
        now: Instant,
        from: SocketAddr,
        known: Option<u32>,
        mut req: ConnectRequestInfo,
        out: &mut dyn Output,
    ) {
        if let Some(id) = known {
            let p = self.peers[id as usize].as_mut().unwrap();
            let result = p.process_connect_request(&req, out);
            let connect_num = p.link.connect_num;
            match result {
                ConnectRequestResult::None => return,
                ConnectRequestResult::Reconnection => {
                    self.disconnect_peer_internal(
                        id,
                        DisconnectReason::Reconnect,
                        true,
                        &[],
                        None,
                        out,
                    );
                    self.remove_peer(id);
                }
                ConnectRequestResult::NewConnection => self.remove_peer(id),
                ConnectRequestResult::P2PLose => {
                    self.disconnect_peer_internal(
                        id,
                        DisconnectReason::PeerToPeerConnection,
                        true,
                        &[],
                        None,
                        out,
                    );
                    self.remove_peer(id);
                }
            }
            if result != ConnectRequestResult::P2PLose {
                req.connection_number = (connect_num + 1) % MAX_CONNECTION_NUMBER;
            }
        }
        if let Some(r) = self.requests.get_mut(&from) {
            r.info.update(req);
            r.last_seen = now;
            return;
        }
        self.request_serial += 1;
        let serial = self.request_serial;
        let data = req.data.clone();
        self.requests
            .insert(from, PendingRequest { serial, info: req, last_seen: now });
        out.event(Event::ConnectionRequest {
            request: RequestId { addr: from, serial },
            addr: from,
            data,
        });
    }

    /// The logic tick (`NetManager.UpdateLogic` body): timers, pings, MTU checks, resends, acks and
    /// queued sends for every peer; removes peers that stayed Disconnected for the disconnect timeout.
    pub fn poll(&mut self, now: Instant, out: &mut dyn Output) {
        let mut dt = now
            .saturating_duration_since(self.last_update)
            .as_secs_f64()
            * 1000.0;
        if dt <= 0.0 {
            dt = 0.001;
        }
        if now > self.last_update {
            self.last_update = now;
        }
        let timeout = self.config.disconnect_timeout_ms as f64;
        for id in 0..self.peers.len() {
            let Some(p) = self.peers[id].as_mut() else {
                continue;
            };
            if p.state == ConnectionState::Disconnected && p.time_since_last_packet > timeout {
                self.remove_peer(id as u32);
                continue;
            }
            let Endpoint {
                peers,
                pool,
                clock,
                config,
                ..
            } = self;
            let mut ctx = Self::ctx(pool, clock, config, now, out);
            if let Some(reason) = peers[id].as_mut().unwrap().update(dt, &mut ctx) {
                self.disconnect_peer_internal(id as u32, reason, true, &[], None, out);
            }
        }
    }
}
