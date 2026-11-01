//! NetPeer.cs.

use std::collections::{HashMap, VecDeque};
use std::net::SocketAddr;
use std::time::{Duration, Instant};

use crate::channel::{Channel, SendCtx};
use crate::consts::*;
use crate::endpoint::{Clock, Config};
use crate::packet::*;
use crate::{Event, Output, Packet, PeerId};

/// The address, MTU and merge buffer of a peer: everything `SendUserData`/`SendRaw` touch.
pub(crate) struct Link {
    pub addr: SocketAddr,
    pub mtu: usize,
    pub connect_num: u8,
    merge: Box<[u8; MAX_PACKET_SIZE + 1]>,
    merge_pos: usize,
    merge_count: usize,
    pub packets_sent: u64,
    pub bytes_sent: u64,
}

impl Link {
    fn new(addr: SocketAddr) -> Self {
        let mut merge = Box::new([0u8; MAX_PACKET_SIZE + 1]);
        merge[0] = PacketProperty::Merged as u8;
        Link {
            addr,
            mtu: INITIAL_MTU,
            connect_num: 0,
            merge,
            merge_pos: 0,
            merge_count: 0,
            packets_sent: 0,
            bytes_sent: 0,
        }
    }

    #[inline]
    pub(crate) fn send_raw(&mut self, data: &[u8], out: &mut dyn Output) {
        self.packets_sent += 1;
        self.bytes_sent += data.len() as u64;
        out.transmit(self.addr, data);
    }

    /// `NetPeer.SendUserData`: stamps the connection number and merges small packets.
    pub(crate) fn send_user_data(&mut self, packet: &mut [u8], out: &mut dyn Output) {
        set_connection_number(packet, self.connect_num);
        let num = 1 + packet.len() + 2;
        if num + 20 >= self.mtu {
            self.send_raw(packet, out);
            return;
        }
        if self.merge_pos + num > self.mtu {
            self.send_merged(out);
        }
        let at = self.merge_pos + 1;
        put_u16(&mut self.merge[..], at, packet.len() as u16);
        self.merge[at + 2..at + 2 + packet.len()].copy_from_slice(packet);
        self.merge_pos += packet.len() + 2;
        self.merge_count += 1;
    }

    /// `NetPeer.SendMerged`: a single queued packet goes out unwrapped.
    pub(crate) fn send_merged(&mut self, out: &mut dyn Output) {
        if self.merge_count == 0 {
            return;
        }
        self.packets_sent += 1;
        if self.merge_count > 1 {
            set_connection_number(&mut self.merge[..], self.connect_num);
            let len = 1 + self.merge_pos;
            self.bytes_sent += len as u64;
            out.transmit(self.addr, &self.merge[..len]);
        } else {
            let len = self.merge_pos - 2;
            self.bytes_sent += len as u64;
            out.transmit(self.addr, &self.merge[3..3 + len]);
        }
        self.merge_pos = 0;
        self.merge_count = 0;
    }
}

/// `NetPeer.IncomingFragments`.
pub(crate) struct IncomingFragments {
    fragments: Vec<Option<Vec<u8>>>,
    received_count: usize,
    total_size: usize,
    channel_id: u8,
}

/// Receive-side context handed to channels: fragment reassembly and event delivery.
pub(crate) struct Deliver<'a, 'o> {
    pub handle: PeerId,
    pub frags: &'a mut HashMap<u16, IncomingFragments>,
    pub send_queue: &'a mut VecDeque<u8>,
    pub pool: &'a mut Pool,
    pub out: &'a mut (dyn Output + 'o),
}

impl Deliver<'_, '_> {
    /// `BaseChannel.AddToPeerChannelSendQueue`.
    #[inline]
    pub(crate) fn add_to_send_queue(&mut self, id: u8, queued: &mut bool) {
        if !*queued {
            *queued = true;
            self.send_queue.push_back(id);
        }
    }

    #[inline]
    pub(crate) fn receive(
        &mut self,
        raw: Vec<u8>,
        method: DeliveryMethod,
        channel: u8,
        header: usize,
    ) {
        self.out.event(Event::Receive {
            peer: self.handle,
            channel,
            method,
            packet: Packet::new(raw, header),
        });
    }

    /// `NetPeer.AddReliablePacket`: reassembles fragments, then raises the receive event.
    pub(crate) fn add_reliable_packet(&mut self, method: DeliveryMethod, raw: Vec<u8>) {
        if !is_fragmented(&raw) {
            let channel = channel_id(&raw) / 4;
            self.receive(raw, method, channel, CHANNELED_HEADER_SIZE);
            return;
        }
        let fragment_id = fragment_id(&raw);
        let ch = channel_id(&raw);
        let entry = self
            .frags
            .entry(fragment_id)
            .or_insert_with(|| IncomingFragments {
                fragments: (0..fragments_total(&raw)).map(|_| None).collect(),
                received_count: 0,
                total_size: 0,
                channel_id: ch,
            });
        let part = fragment_part(&raw) as usize;
        if part >= entry.fragments.len()
            || entry.fragments[part].is_some()
            || ch != entry.channel_id
        {
            self.pool.put(raw);
            return;
        }
        entry.total_size += raw.len() - FRAGMENTED_HEADER_TOTAL_SIZE;
        entry.fragments[part] = Some(raw);
        entry.received_count += 1;
        if entry.received_count != entry.fragments.len() {
            return;
        }
        let entry = self.frags.remove(&fragment_id).unwrap();
        let mut result = self.pool.get(entry.total_size);
        let mut pos = 0;
        for frag in entry.fragments.into_iter().flatten() {
            let n = frag.len() - FRAGMENTED_HEADER_TOTAL_SIZE;
            result[pos..pos + n].copy_from_slice(&frag[FRAGMENTED_HEADER_TOTAL_SIZE..]);
            pos += n;
            self.pool.put(frag);
        }
        self.receive(result, method, entry.channel_id / 4, 0);
    }
}

/// Everything the endpoint lends a peer for one call.
pub(crate) struct Ctx<'a, 'o> {
    pub out: &'a mut (dyn Output + 'o),
    pub pool: &'a mut Pool,
    pub now: Instant,
    pub clock: &'a Clock,
    pub config: &'a Config,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ShutdownResult {
    None,
    Success,
    WasConnected,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ConnectRequestResult {
    None,
    P2PLose,
    Reconnection,
    NewConnection,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum DisconnectResult {
    None,
    Reject,
    Disconnect,
}

/// A parsed ConnectRequest (`NetConnectRequestPacket`).
#[derive(Clone, Debug)]
pub(crate) struct ConnectRequestInfo {
    pub connection_time: i64,
    pub connection_number: u8,
    pub target_address: Vec<u8>,
    pub peer_id: i32,
    pub data: Vec<u8>,
}

impl ConnectRequestInfo {
    pub(crate) fn parse(raw: &[u8]) -> Option<Self> {
        let connection_number = connection_number(raw);
        if connection_number >= MAX_CONNECTION_NUMBER {
            return None;
        }
        let addr_len = raw[17] as usize;
        if addr_len != 16 && addr_len != 28 {
            return None;
        }
        if raw.len() < 18 + addr_len {
            return None;
        }
        Some(ConnectRequestInfo {
            connection_time: i64_at(raw, 5),
            connection_number,
            peer_id: i32_at(raw, 13),
            target_address: raw[18..18 + addr_len].to_vec(),
            data: raw[18 + addr_len..].to_vec(),
        })
    }

    /// `NetConnectRequestPacket.Make`.
    pub(crate) fn make(
        connect_data: &[u8],
        target: &SocketAddr,
        connect_time: i64,
        local_id: i32,
    ) -> Vec<u8> {
        let (addr, addr_len) = sockaddr_bytes(target);
        let mut raw = vec![0u8; 18 + addr_len + connect_data.len()];
        set_property(&mut raw, PacketProperty::ConnectRequest);
        put_i32(&mut raw, 1, PROTOCOL_ID);
        put_i64(&mut raw, 5, connect_time);
        put_i32(&mut raw, 13, local_id);
        raw[17] = addr_len as u8;
        raw[18..18 + addr_len].copy_from_slice(&addr[..addr_len]);
        raw[18 + addr_len..].copy_from_slice(connect_data);
        raw
    }

    /// `ConnectionRequest.UpdateRequest`.
    pub(crate) fn update(&mut self, newer: ConnectRequestInfo) {
        if newer.connection_time >= self.connection_time
            && (newer.connection_time != self.connection_time
                || newer.connection_number != self.connection_number)
        {
            *self = newer;
        }
    }
}

/// A parsed ConnectAccept (`NetConnectAcceptPacket`), also the network-changed PeerNotFound.
pub(crate) struct ConnectAcceptInfo {
    pub connection_time: i64,
    pub connection_number: u8,
    pub peer_id: i32,
    pub peer_network_changed: bool,
}

impl ConnectAcceptInfo {
    pub(crate) fn parse(raw: &[u8]) -> Option<Self> {
        if raw.len() != 15 {
            return None;
        }
        let connection_number = raw[9];
        if connection_number >= MAX_CONNECTION_NUMBER {
            return None;
        }
        let changed = raw[10];
        if changed > 1 {
            return None;
        }
        let peer_id = i32_at(raw, 11);
        if peer_id < 0 {
            return None;
        }
        Some(ConnectAcceptInfo {
            connection_time: i64_at(raw, 1),
            connection_number,
            peer_id,
            peer_network_changed: changed == 1,
        })
    }

    pub(crate) fn make(connect_time: i64, connect_num: u8, local_peer_id: i32) -> [u8; 15] {
        let mut raw = [0u8; 15];
        raw[0] = PacketProperty::ConnectAccept as u8;
        put_i64(&mut raw, 1, connect_time);
        raw[9] = connect_num;
        put_i32(&mut raw, 11, local_peer_id);
        raw
    }

    /// `NetConnectAcceptPacket.MakeNetworkChanged`.
    pub(crate) fn make_network_changed(
        connect_time: i64,
        connect_num: u8,
        remote_id: i32,
    ) -> [u8; 15] {
        let mut raw = [0u8; 15];
        raw[0] = PacketProperty::PeerNotFound as u8;
        put_i64(&mut raw, 1, connect_time);
        raw[9] = connect_num;
        raw[10] = 1;
        put_i32(&mut raw, 11, remote_id);
        raw
    }
}

/// .NET `SocketAddress` bytes of an endpoint as LiteNetLib on Linux serializes it (a native
/// `sockaddr_in`/`sockaddr_in6`). Only its length is checked by the receiver; the bytes take part
/// in the peer-to-peer tie break of simultaneous connects.
pub(crate) fn sockaddr_bytes(addr: &SocketAddr) -> ([u8; 28], usize) {
    let mut b = [0u8; 28];
    match addr {
        SocketAddr::V4(a) => {
            b[0..2].copy_from_slice(&2u16.to_le_bytes());
            b[2..4].copy_from_slice(&a.port().to_be_bytes());
            b[4..8].copy_from_slice(&a.ip().octets());
            (b, 16)
        }
        SocketAddr::V6(a) => {
            b[0..2].copy_from_slice(&10u16.to_le_bytes());
            b[2..4].copy_from_slice(&a.port().to_be_bytes());
            b[4..8].copy_from_slice(&a.flowinfo().to_be_bytes());
            b[8..24].copy_from_slice(&a.ip().octets());
            b[24..28].copy_from_slice(&a.scope_id().to_le_bytes());
            (b, 28)
        }
    }
}

/// `System.Diagnostics.Stopwatch` as `NetPeer._pingTimer` uses it.
#[derive(Default)]
struct Stopwatch {
    started: Option<Instant>,
    stopped_elapsed: Duration,
}

impl Stopwatch {
    fn is_running(&self) -> bool {
        self.started.is_some()
    }

    fn elapsed_ms(&self, now: Instant) -> i32 {
        let d = match self.started {
            Some(s) => now.saturating_duration_since(s),
            None => self.stopped_elapsed,
        };
        d.as_millis().min(i32::MAX as u128) as i32
    }

    fn stop(&mut self, now: Instant) {
        if let Some(s) = self.started.take() {
            self.stopped_elapsed = now.saturating_duration_since(s);
        }
    }

    fn restart(&mut self, now: Instant) {
        self.started = Some(now);
        self.stopped_elapsed = Duration::ZERO;
    }
}

pub(crate) struct Peer {
    pub handle: PeerId,
    pub link: Link,
    pub state: ConnectionState,
    pub connect_time: i64,
    pub remote_id: i32,
    rtt: i32,
    avg_rtt: i32,
    rtt_count: i32,
    resend_delay: f64,
    ping_send_timer: f64,
    rtt_reset_timer: f64,
    ping_timer: Stopwatch,
    pub time_since_last_packet: f64,
    pub remote_delta: i64,
    ping_sequence: u16,
    pong_sequence: u16,
    unreliable: Vec<Vec<u8>>,
    channels: Vec<Option<Channel>>,
    channel_send_queue: VecDeque<u8>,
    mtu_idx: usize,
    finish_mtu: bool,
    mtu_check_timer: f64,
    mtu_check_attempts: i32,
    fragment_id: i32,
    holded_fragments: HashMap<u16, IncomingFragments>,
    connect_attempts: u32,
    connect_timer: f64,
    connect_request_packet: Vec<u8>,
    connect_accept_packet: Option<[u8; 15]>,
    shutdown_packet: Vec<u8>,
    shutdown_timer: f64,
    pub packets_received: u64,
    pub bytes_received: u64,
}

impl Peer {
    /// `NetPeer(NetManager, IPEndPoint, int)`: the common constructor (state Connected).
    fn base(handle: PeerId, addr: SocketAddr, config: &Config) -> Self {
        let mut p = Peer {
            handle,
            link: Link::new(addr),
            state: ConnectionState::Connected,
            connect_time: 0,
            remote_id: 0,
            rtt: 0,
            avg_rtt: 0,
            rtt_count: 0,
            resend_delay: 27.0,
            ping_send_timer: 0.0,
            rtt_reset_timer: 0.0,
            ping_timer: Stopwatch::default(),
            time_since_last_packet: 0.0,
            remote_delta: 0,
            ping_sequence: 1,
            pong_sequence: 0,
            unreliable: Vec::with_capacity(8),
            channels: (0..config.channels_count as usize * CHANNEL_TYPE_COUNT)
                .map(|_| None)
                .collect(),
            channel_send_queue: VecDeque::with_capacity(8),
            mtu_idx: 0,
            finish_mtu: false,
            mtu_check_timer: 0.0,
            mtu_check_attempts: 0,
            fragment_id: 0,
            holded_fragments: HashMap::new(),
            connect_attempts: 0,
            connect_timer: 0.0,
            connect_request_packet: Vec::new(),
            connect_accept_packet: None,
            shutdown_packet: Vec::new(),
            shutdown_timer: 0.0,
            packets_received: 0,
            bytes_received: 0,
        };
        p.reset_mtu(config);
        p
    }

    /// Outgoing connection: sends the ConnectRequest immediately.
    pub(crate) fn new_outgoing(
        handle: PeerId,
        addr: SocketAddr,
        connect_num: u8,
        connect_data: &[u8],
        ctx: &mut Ctx,
    ) -> Self {
        let mut p = Peer::base(handle, addr, ctx.config);
        p.connect_time = ctx.clock.utc_ticks(ctx.now);
        p.state = ConnectionState::Outgoing;
        p.link.connect_num = connect_num;
        p.connect_request_packet =
            ConnectRequestInfo::make(connect_data, &addr, p.connect_time, handle.id as i32);
        set_connection_number(&mut p.connect_request_packet, connect_num);
        let pkt = std::mem::take(&mut p.connect_request_packet);
        p.link.send_raw(&pkt, ctx.out);
        p.connect_request_packet = pkt;
        p
    }

    /// Accepted incoming connection: sends the ConnectAccept immediately.
    pub(crate) fn new_incoming(
        handle: PeerId,
        addr: SocketAddr,
        req: &ConnectRequestInfo,
        ctx: &mut Ctx,
    ) -> Self {
        let mut p = Peer::base(handle, addr, ctx.config);
        p.connect_time = req.connection_time;
        p.link.connect_num = req.connection_number;
        p.remote_id = req.peer_id;
        let accept = ConnectAcceptInfo::make(p.connect_time, p.link.connect_num, handle.id as i32);
        p.connect_accept_packet = Some(accept);
        p.state = ConnectionState::Connected;
        p.link.send_raw(&accept, ctx.out);
        p
    }

    /// Rejected incoming connection (`NetPeer.Reject`): lingers in ShutdownRequested resending the
    /// Disconnect until ShutdownOk or the disconnect timeout.
    pub(crate) fn new_rejecting(
        handle: PeerId,
        addr: SocketAddr,
        req: &ConnectRequestInfo,
        data: &[u8],
        ctx: &mut Ctx,
    ) -> Self {
        let mut p = Peer::base(handle, addr, ctx.config);
        p.connect_time = req.connection_time;
        p.link.connect_num = req.connection_number;
        p.shutdown(data, false, ctx.out);
        p
    }

    pub(crate) fn reset_mtu(&mut self, config: &Config) {
        self.finish_mtu = !config.mtu_discovery;
        if config.mtu_override > 0 {
            self.link.mtu = config.mtu_override;
            self.finish_mtu = true;
        } else {
            self.set_mtu(0);
        }
    }

    fn set_mtu(&mut self, idx: usize) {
        self.mtu_idx = idx;
        self.link.mtu = POSSIBLE_MTU[idx];
    }

    pub(crate) fn round_trip_time(&self) -> i32 {
        self.avg_rtt
    }

    pub(crate) fn resend_delay(&self) -> f64 {
        self.resend_delay
    }

    pub(crate) fn channel_queue_len(&self, idx: usize) -> usize {
        self.channels
            .get(idx)
            .and_then(|c| c.as_ref())
            .map_or(0, |c| c.packets_in_queue())
    }

    pub(crate) fn channel_in_flight(&self, idx: usize) -> usize {
        self.channels
            .get(idx)
            .and_then(|c| c.as_ref())
            .map_or(0, |c| c.packets_in_flight())
    }

    pub(crate) fn channel_count(&self) -> usize {
        self.channels.len()
    }

    /// `NetPeer.GetMaxSinglePacketSize`.
    pub(crate) fn max_single_packet_size(&self, method: DeliveryMethod) -> usize {
        let header = if method == DeliveryMethod::Unreliable {
            PacketProperty::Unreliable.header_size()
        } else {
            PacketProperty::Channeled.header_size()
        };
        self.link.mtu - header
    }

    fn channel(&mut self, idx: u8) -> &mut Channel {
        self.channels[idx as usize].get_or_insert_with(|| Channel::new(idx))
    }

    /// `NetPeer.SendInternal`: queues `data` (fragmenting reliable payloads above the MTU).
    pub(crate) fn send(
        &mut self,
        data: &[u8],
        channel_number: u8,
        method: DeliveryMethod,
        pool: &mut Pool,
    ) -> Result<(), crate::SendError> {
        use crate::SendError;
        if self.state != ConnectionState::Connected {
            return Err(SendError::NotConnected);
        }
        if channel_number as usize * CHANNEL_TYPE_COUNT >= self.channels.len() {
            return Err(SendError::InvalidChannel);
        }
        let (property, idx) = if method == DeliveryMethod::Unreliable {
            (PacketProperty::Unreliable, None)
        } else {
            (
                PacketProperty::Channeled,
                Some(channel_number * CHANNEL_TYPE_COUNT as u8 + method as u8),
            )
        };
        let header = property.header_size();
        let mtu = self.link.mtu;
        if data.len() + header > mtu {
            if method != DeliveryMethod::ReliableOrdered
                && method != DeliveryMethod::ReliableUnordered
            {
                return Err(SendError::TooBig { max: mtu - header });
            }
            let per = mtu - header - FRAGMENT_HEADER_SIZE;
            let total = data.len().div_ceil(per);
            if total > u16::MAX as usize {
                return Err(SendError::TooManyFragments);
            }
            self.fragment_id = self.fragment_id.wrapping_add(1);
            let fragment_id = self.fragment_id as u16;
            let idx = idx.unwrap();
            let ch = self.channels[idx as usize].get_or_insert_with(|| Channel::new(idx));
            for (part, chunk) in data.chunks(per).enumerate() {
                let mut p = pool.get(header + FRAGMENT_HEADER_SIZE + chunk.len());
                set_property(&mut p, property);
                put_u16(&mut p, 4, fragment_id);
                put_u16(&mut p, 6, part as u16);
                put_u16(&mut p, 8, total as u16);
                mark_fragmented(&mut p);
                p[FRAGMENTED_HEADER_TOTAL_SIZE..].copy_from_slice(chunk);
                ch.push_outgoing(p);
            }
            self.register_channel(idx);
            return Ok(());
        }
        let mut p = pool.get(header + data.len());
        set_property(&mut p, property);
        p[header..].copy_from_slice(data);
        match idx {
            None => self.unreliable.push(p),
            Some(idx) => {
                self.channel(idx).push_outgoing(p);
                self.register_channel(idx);
            }
        }
        Ok(())
    }

    /// `BaseChannel.AddToPeerChannelSendQueue`.
    fn register_channel(&mut self, idx: u8) {
        let q = self.channel(idx).queued();
        if !*q {
            *q = true;
            self.channel_send_queue.push_back(idx);
        }
    }

    /// `NetPeer.ProcessDisconnect`.
    pub(crate) fn process_disconnect(&self, raw: &[u8]) -> DisconnectResult {
        if (self.state == ConnectionState::Connected || self.state == ConnectionState::Outgoing)
            && raw.len() >= 9
            && i64_at(raw, 1) == self.connect_time
            && connection_number(raw) == self.link.connect_num
        {
            if self.state != ConnectionState::Connected {
                return DisconnectResult::Reject;
            }
            return DisconnectResult::Disconnect;
        }
        DisconnectResult::None
    }

    /// `NetPeer.Shutdown`.
    pub(crate) fn shutdown(
        &mut self,
        data: &[u8],
        force: bool,
        out: &mut dyn Output,
    ) -> ShutdownResult {
        if self.state == ConnectionState::Disconnected
            || self.state == ConnectionState::ShutdownRequested
        {
            return ShutdownResult::None;
        }
        let result = if self.state != ConnectionState::Connected {
            ShutdownResult::Success
        } else {
            ShutdownResult::WasConnected
        };
        if force {
            self.state = ConnectionState::Disconnected;
            return result;
        }
        self.time_since_last_packet = 0.0;
        let mut pkt = vec![0u8; PacketProperty::Disconnect.header_size() + data.len()];
        set_property(&mut pkt, PacketProperty::Disconnect);
        set_connection_number(&mut pkt, self.link.connect_num);
        put_i64(&mut pkt, 1, self.connect_time);
        // LiteNetLib logs an error and sends the packet without the data when it reaches the MTU.
        if pkt.len() < self.link.mtu {
            pkt[9..].copy_from_slice(data);
        }
        self.state = ConnectionState::ShutdownRequested;
        self.link.send_raw(&pkt, out);
        self.shutdown_packet = pkt;
        result
    }

    /// `NetPeer.ProcessConnectAccept`.
    pub(crate) fn process_connect_accept(&mut self, acc: &ConnectAcceptInfo) -> bool {
        if self.state != ConnectionState::Outgoing || acc.connection_time != self.connect_time {
            return false;
        }
        self.link.connect_num = acc.connection_number;
        self.remote_id = acc.peer_id;
        self.time_since_last_packet = 0.0;
        self.state = ConnectionState::Connected;
        true
    }

    /// `NetPeer.ProcessConnectRequest`.
    pub(crate) fn process_connect_request(
        &mut self,
        req: &ConnectRequestInfo,
        out: &mut dyn Output,
    ) -> ConnectRequestResult {
        match self.state {
            ConnectionState::Outgoing => {
                if req.connection_time < self.connect_time {
                    return ConnectRequestResult::P2PLose;
                }
                if req.connection_time == self.connect_time {
                    let (mine, len) = sockaddr_bytes(&self.link.addr);
                    for i in (0..len).rev() {
                        let b = mine[i];
                        let t = req.target_address.get(i).copied().unwrap_or(0);
                        if b != t && b < t {
                            return ConnectRequestResult::P2PLose;
                        }
                    }
                }
            }
            ConnectionState::Connected => {
                if req.connection_time == self.connect_time {
                    if let Some(acc) = self.connect_accept_packet {
                        self.link.send_raw(&acc, out);
                    }
                } else if req.connection_time > self.connect_time {
                    return ConnectRequestResult::Reconnection;
                }
            }
            ConnectionState::ShutdownRequested | ConnectionState::Disconnected => {
                if req.connection_time >= self.connect_time {
                    return ConnectRequestResult::NewConnection;
                }
            }
            ConnectionState::EndPointChange => {}
        }
        ConnectRequestResult::None
    }

    fn update_round_trip_time(&mut self, rtt: i32) {
        self.rtt = self.rtt.wrapping_add(rtt);
        self.rtt_count += 1;
        self.avg_rtt = self.rtt / self.rtt_count;
        self.resend_delay = 25.0 + self.avg_rtt as f64 * 2.1;
    }

    /// `NetPeer.ProcessPacket`: everything from a known address except the NetManager-level types.
    pub(crate) fn process_packet(&mut self, raw: &[u8], ctx: &mut Ctx) {
        if self.state == ConnectionState::Outgoing || self.state == ConnectionState::Disconnected {
            return;
        }
        let Some(prop) = property(raw) else { return };
        if prop == PacketProperty::ShutdownOk {
            if self.state == ConnectionState::ShutdownRequested {
                self.state = ConnectionState::Disconnected;
            }
            return;
        }
        if connection_number(raw) != self.link.connect_num {
            return;
        }
        self.time_since_last_packet = 0.0;
        match prop {
            PacketProperty::Merged => {
                let mut pos = 1;
                while pos + 2 <= raw.len() {
                    let size = u16_at(raw, pos) as usize;
                    if size == 0 {
                        break;
                    }
                    pos += 2;
                    if raw.len() - pos < size {
                        break;
                    }
                    let sub = &raw[pos..pos + size];
                    if !verify(sub) {
                        break;
                    }
                    pos += size;
                    self.process_packet(sub, ctx);
                }
            }
            PacketProperty::Ping => {
                let seq = sequence(raw);
                if relative_sequence_number(seq as i32, self.pong_sequence as i32) > 0 {
                    let mut pong = [0u8; 11];
                    pong[0] = PacketProperty::Pong as u8;
                    set_connection_number(&mut pong, self.link.connect_num);
                    put_i64(&mut pong, 3, ctx.clock.utc_ticks(ctx.now));
                    self.pong_sequence = seq;
                    set_sequence(&mut pong, seq);
                    self.link.send_raw(&pong, ctx.out);
                }
            }
            PacketProperty::Pong => {
                if sequence(raw) == self.ping_sequence {
                    self.ping_timer.stop(ctx.now);
                    let elapsed = self.ping_timer.elapsed_ms(ctx.now);
                    self.remote_delta =
                        i64_at(raw, 3) + elapsed as i64 * 10000 / 2 - ctx.clock.utc_ticks(ctx.now);
                    self.update_round_trip_time(elapsed);
                    ctx.out.event(Event::LatencyUpdate {
                        peer: self.handle,
                        latency_ms: elapsed / 2,
                    });
                }
            }
            PacketProperty::Channeled | PacketProperty::Ack => {
                let ch = channel_id(raw) as usize;
                if ch >= self.channels.len() {
                    return;
                }
                if self.channels[ch].is_none() {
                    if prop == PacketProperty::Ack {
                        return;
                    }
                    self.channels[ch] = Some(Channel::new(ch as u8));
                }
                let copy = ctx.pool.copy_of(raw);
                let Peer {
                    channels,
                    holded_fragments,
                    channel_send_queue,
                    handle,
                    ..
                } = self;
                let mut d = Deliver {
                    handle: *handle,
                    frags: holded_fragments,
                    send_queue: channel_send_queue,
                    pool: ctx.pool,
                    out: ctx.out,
                };
                channels[ch].as_mut().unwrap().process_packet(copy, &mut d);
            }
            PacketProperty::Unreliable => {
                let copy = ctx.pool.copy_of(raw);
                ctx.out.event(Event::Receive {
                    peer: self.handle,
                    channel: 0,
                    method: DeliveryMethod::Unreliable,
                    packet: Packet::new(copy, HEADER_SIZE),
                });
            }
            PacketProperty::MtuCheck | PacketProperty::MtuOk => self.process_mtu_packet(raw, ctx),
            _ => {}
        }
    }

    /// `NetPeer.ProcessMtuPacket`.
    fn process_mtu_packet(&mut self, raw: &[u8], ctx: &mut Ctx) {
        if raw.len() < POSSIBLE_MTU[0] {
            return;
        }
        let num = i32_at(raw, 1);
        let num2 = i32_at(raw, raw.len() - 4);
        if num as usize != raw.len() || num != num2 || num as usize > MAX_PACKET_SIZE {
            return;
        }
        if property(raw) == Some(PacketProperty::MtuCheck) {
            self.mtu_check_attempts = 0;
            let mut reply = ctx.pool.copy_of(raw);
            set_property(&mut reply, PacketProperty::MtuOk);
            self.link.send_raw(&reply, ctx.out);
            ctx.pool.put(reply);
        } else if num as usize > self.link.mtu
            && !self.finish_mtu
            && self.mtu_idx + 1 < POSSIBLE_MTU.len()
            && num as usize == POSSIBLE_MTU[self.mtu_idx + 1]
        {
            self.set_mtu(self.mtu_idx + 1);
            if self.mtu_idx == POSSIBLE_MTU.len() - 1 {
                self.finish_mtu = true;
            }
        }
    }

    /// `NetPeer.UpdateMtuLogic`. The MtuCheck header carries connection number 0, as in LiteNetLib
    /// (a pooled packet whose first byte was reset), so discovery only succeeds on connection number 0.
    fn update_mtu_logic(&mut self, dt: f64, ctx: &mut Ctx) {
        if self.finish_mtu {
            return;
        }
        self.mtu_check_timer += dt;
        if self.mtu_check_timer < MTU_CHECK_DELAY_MS {
            return;
        }
        self.mtu_check_timer = 0.0;
        self.mtu_check_attempts += 1;
        if self.mtu_check_attempts >= MAX_MTU_CHECK_ATTEMPTS {
            self.finish_mtu = true;
            return;
        }
        if self.mtu_idx < POSSIBLE_MTU.len() - 1 {
            let size = POSSIBLE_MTU[self.mtu_idx + 1];
            let mut p = ctx.pool.get(size);
            set_property(&mut p, PacketProperty::MtuCheck);
            put_i32(&mut p, 1, size as i32);
            put_i32(&mut p, size - 4, size as i32);
            self.link.send_raw(&p, ctx.out);
            ctx.pool.put(p);
        }
    }

    /// `NetPeer.Update`. Returns the reason when the peer has to be force-disconnected.
    pub(crate) fn update(&mut self, dt: f64, ctx: &mut Ctx) -> Option<DisconnectReason> {
        self.time_since_last_packet += dt;
        let timeout = ctx.config.disconnect_timeout_ms as f64;
        match self.state {
            ConnectionState::Connected => {
                if self.time_since_last_packet > timeout {
                    return Some(DisconnectReason::Timeout);
                }
            }
            ConnectionState::ShutdownRequested => {
                if self.time_since_last_packet > timeout {
                    self.state = ConnectionState::Disconnected;
                    return None;
                }
                self.shutdown_timer += dt;
                if self.shutdown_timer >= SHUTDOWN_DELAY_MS {
                    self.shutdown_timer = 0.0;
                    let pkt = std::mem::take(&mut self.shutdown_packet);
                    self.link.send_raw(&pkt, ctx.out);
                    self.shutdown_packet = pkt;
                }
                return None;
            }
            ConnectionState::Outgoing => {
                self.connect_timer += dt;
                if self.connect_timer > ctx.config.reconnect_delay_ms as f64 {
                    self.connect_timer = 0.0;
                    self.connect_attempts += 1;
                    if self.connect_attempts > ctx.config.max_connect_attempts {
                        return Some(DisconnectReason::ConnectionFailed);
                    }
                    let pkt = std::mem::take(&mut self.connect_request_packet);
                    self.link.send_raw(&pkt, ctx.out);
                    self.connect_request_packet = pkt;
                }
                return None;
            }
            ConnectionState::Disconnected => return None,
            ConnectionState::EndPointChange => {}
        }

        self.ping_send_timer += dt;
        if self.ping_send_timer >= ctx.config.ping_interval_ms as f64 {
            self.ping_send_timer = 0.0;
            self.ping_sequence = self.ping_sequence.wrapping_add(1);
            if self.ping_timer.is_running() {
                let e = self.ping_timer.elapsed_ms(ctx.now);
                self.update_round_trip_time(e);
            }
            self.ping_timer.restart(ctx.now);
            let mut ping = [0u8; 3];
            ping[0] = PacketProperty::Ping as u8;
            set_connection_number(&mut ping, self.link.connect_num);
            set_sequence(&mut ping, self.ping_sequence);
            self.link.send_raw(&ping, ctx.out);
        }
        self.rtt_reset_timer += dt;
        if self.rtt_reset_timer >= ctx.config.ping_interval_ms as f64 * 3.0 {
            self.rtt_reset_timer = 0.0;
            self.rtt = self.avg_rtt;
            self.rtt_count = 1;
        }
        self.update_mtu_logic(dt, ctx);

        let resend_delay_ms = self.resend_delay;
        let Peer {
            channels,
            channel_send_queue,
            link,
            unreliable,
            ..
        } = self;
        let mut sctx = SendCtx {
            link,
            out: &mut *ctx.out,
            pool: &mut *ctx.pool,
            now: ctx.now,
            resend_delay_ms,
        };
        let mut count = channel_send_queue.len();
        while count > 0 {
            count -= 1;
            let Some(idx) = channel_send_queue.pop_front() else {
                break;
            };
            let ch = channels[idx as usize].as_mut().unwrap();
            if ch.send_next_packets(&mut sctx) {
                channel_send_queue.push_back(idx);
            } else {
                *ch.queued() = false;
            }
        }
        for mut p in unreliable.drain(..) {
            sctx.link.send_user_data(&mut p, sctx.out);
            sctx.pool.put(p);
        }
        sctx.link.send_merged(sctx.out);
        None
    }

    /// Returns all pooled buffers of the peer.
    pub(crate) fn recycle_into(self, pool: &mut Pool) {
        for ch in self.channels.into_iter().flatten() {
            ch.recycle_all(pool);
        }
        for p in self.unreliable {
            pool.put(p);
        }
        for f in self.holded_fragments.into_values() {
            for p in f.fragments.into_iter().flatten() {
                pool.put(p);
            }
        }
    }
}
