//! A sans-IO, wire-compatible implementation of LiteNetLib 1.3.1.
//!
//! The source of truth is the decompiled 1.3.1 assembly (`~/src/lnl-1.3.1-decompiled`). The port
//! keeps LiteNetLib's packet formats, sequence arithmetic, windows, timers and state machine
//! (NetManager, NetPeer, ReliableChannel, SequencedChannel, NetConnectRequestPacket,
//! NetConnectAcceptPacket), including its quirks: a lone queued packet is sent unmerged, raw sends
//! (fragments of a full MTU) overtake the merge buffer, MtuCheck carries connection number 0, a
//! lost ping counts a full ping interval of RTT, unanswered requests never expire.
//!
//! # Differences from LiteNetLib 1.3.1
//!
//! - Not implemented: NAT punch, Broadcast, UnconnectedMessage, NTP, packet layers (CRC32C, XOR),
//!   delivery events (`SendWithDeliveryEvent`), `DisconnectOnUnreachable`, most statistics (per-peer
//!   packet and byte counters only). Broadcast, UnconnectedMessage and NatMessage datagrams are
//!   ignored, as in a `NetManager` with those features off.
//! - Send failures are not reported back, so MTU discovery keeps probing after a failed send
//!   (LiteNetLib stops at once when `SendRaw` returns 0).
//! - Events go straight to the [`Output`] (like `UnsyncedEvents`/manual mode), so a peer address
//!   change is applied when the network-changed packet arrives, not in `PollEvents`.
//! - Merged sub-packets are bounds-checked against the datagram (LiteNetLib checks the buffer
//!   capacity); datagrams over 1432 bytes are dropped (LiteNetLib's receive fails with MessageSize).
//! - API: [`Endpoint::send`] returns a [`SendError`] instead of dropping silently or throwing;
//!   [`Endpoint::drop_request`] forgets a request without answering (LiteNetLib has no way to).
//! - The ConnectRequest carries the target address as a Linux `sockaddr` (.NET on Windows writes
//!   its own family numbers). Receivers only check the length (16 or 28); the bytes matter only in
//!   the tie break of two peers connecting to each other at the same tick.
//!
//! # Model
//!
//! An [`Endpoint`] is one LiteNetLib `NetManager` without sockets or threads. The caller:
//! - feeds every received datagram to [`Endpoint::handle`] with the current time and source address;
//! - calls [`Endpoint::poll`] regularly (LiteNetLib's logic thread runs every 15 ms; calling it more
//!   often, e.g. after each receive batch, lowers latency and is harmless);
//! - supplies an [`Output`] that receives outgoing datagrams and [`Event`]s.
//!
//! Sending ([`Endpoint::send`]) only queues, as in LiteNetLib: the datagrams leave on the next
//! `poll`. Reliable packets, acks and unreliable packets are merged into `Merged` datagrams exactly
//! as `NetPeer.SendUserData` does.
//!
//! Received payloads arrive as [`Packet`]s backed by pooled buffers; hand them back with
//! [`Endpoint::recycle`] so the steady state does not allocate.
//!
//! [`udp::UdpEndpoint`] is a small blocking `std::net` driver for tests and tools.

mod channel;
mod consts;
mod endpoint;
mod packet;
mod peer;
pub mod udp;
pub mod wire;

pub use consts::{
    CHANNEL_TYPE_COUNT, CHANNELED_HEADER_SIZE, ConnectionState, DEFAULT_WINDOW_SIZE,
    DeliveryMethod, DisconnectReason, FRAGMENT_HEADER_SIZE, FRAGMENTED_HEADER_TOTAL_SIZE,
    HALF_MAX_SEQUENCE, HEADER_SIZE, INITIAL_MTU, MAX_CONNECTION_NUMBER, MAX_PACKET_SIZE,
    MAX_SEQUENCE, MAX_UNRELIABLE_DATA_SIZE, POSSIBLE_MTU, PROTOCOL_ID, PacketProperty,
};
pub use endpoint::{Config, ConnectError, Endpoint, PeerInfo, SendError};
pub use packet::Packet;

use std::net::SocketAddr;

/// Handle of a peer inside one [`Endpoint`]. `id` is LiteNetLib's peer id (the value sent in
/// ConnectAccept, recycled after the peer is removed); `generation` makes stale handles harmless.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct PeerId {
    pub id: u32,
    pub generation: u32,
}

/// Handle of a pending incoming connection request (LiteNetLib's `ConnectionRequest`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct RequestId {
    pub addr: SocketAddr,
    pub serial: u64,
}

/// What the endpoint reports. The mapping to LiteNetLib's `INetEventListener`:
#[derive(Debug)]
pub enum Event {
    /// `OnPeerConnected`: an outgoing connection was accepted, or an incoming request was accepted.
    Connected { peer: PeerId, addr: SocketAddr },
    /// `OnPeerDisconnected`. `data` is the remote's disconnect/reject payload when there is one.
    Disconnected {
        peer: PeerId,
        addr: SocketAddr,
        reason: DisconnectReason,
        data: Option<Packet>,
    },
    /// `OnNetworkReceive`. `channel` is the channel number (0..channels_count), always 0 for Unreliable.
    Receive {
        peer: PeerId,
        channel: u8,
        method: DeliveryMethod,
        packet: Packet,
    },
    /// `OnNetworkLatencyUpdate`: half the measured round trip, in ms.
    LatencyUpdate { peer: PeerId, latency_ms: i32 },
    /// `OnConnectionRequest`. Raised once per remote address; repeated ConnectRequests from the same
    /// address update the pending request silently. It stays pending until [`Endpoint::accept`],
    /// [`Endpoint::reject`], [`Endpoint::reject_force`] or [`Endpoint::drop_request`].
    ConnectionRequest {
        request: RequestId,
        addr: SocketAddr,
        data: Vec<u8>,
    },
    /// `IPeerAddressChangedListener.OnPeerAddressChanged` (only with `allow_peer_address_change`).
    PeerAddressChanged {
        peer: PeerId,
        previous: SocketAddr,
        addr: SocketAddr,
    },
}

/// Receiver of everything an [`Endpoint`] produces.
pub trait Output {
    /// Send one datagram. The slice is only valid during the call.
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]);
    /// Deliver an event.
    fn event(&mut self, event: Event);
}

/// A simple [`Output`] that collects datagrams and events into vectors (allocates per datagram;
/// meant for tests and tools).
#[derive(Default, Debug)]
pub struct Collect {
    pub transmits: Vec<(SocketAddr, Vec<u8>)>,
    pub events: std::collections::VecDeque<Event>,
}

impl Output for Collect {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        self.transmits.push((to, datagram.to_vec()));
    }

    fn event(&mut self, event: Event) {
        self.events.push_back(event);
    }
}
