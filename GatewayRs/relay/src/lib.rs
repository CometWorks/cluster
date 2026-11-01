//! gateway-rs relay core (T-0229, Docs/Gateway/RelayIpc.md): one client session's routing state and
//! the per-packet rules of the C# relay - `SendToBackend`, `SendToClient`, `ShouldRelayServerPacket`,
//! `ReplenishClientCredit`, `FlushClientBacklog`/`FlushServerBacklog` and the session maintenance
//! checks - without any I/O. The transport layer owns sockets and threads and implements [`Io`];
//! ClusterRegistry (C#) makes every session decision and drives this state with [`SessionUpdate`].
//!
//! One deliberate difference from the in-process relay: a client packet is forwarded directly only
//! while the client backlog is empty, and the backlog drains by itself the moment forwarding becomes
//! allowed. The C# relay could let a fresh packet overtake a backlog that no `FlushClientBacklog` had
//! drained yet; here client-to-node order is kept unconditionally.

pub mod backlog;
pub mod frame;

use std::time::{Duration, Instant};

use backlog::Backlog;
use frame::{FrameError, game_message_id, message};

/// The transport's handle for a node link (one per node endpoint).
pub type LinkId = u32;

/// LiteNetLib `DeliveryMethod`, with its wire values (`ClientSend` carries it as a byte).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Delivery {
    ReliableUnordered = 0,
    Sequenced = 1,
    ReliableOrdered = 2,
    ReliableSequenced = 3,
    Unreliable = 4,
}

impl Delivery {
    pub fn from_byte(value: u8) -> Option<Self> {
        Some(match value {
            0 => Self::ReliableUnordered,
            1 => Self::Sequenced,
            2 => Self::ReliableOrdered,
            3 => Self::ReliableSequenced,
            4 => Self::Unreliable,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Packet {
    pub channel: u8,
    pub delivery: Delivery,
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Which {
    Client,
    Server,
}

impl Which {
    pub fn name(self) -> &'static str {
        match self {
            Which::Client => "client",
            Which::Server => "server",
        }
    }
}

/// What the session reports to ClusterRegistry (`ClientMessage` / `ClientEvent`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Event {
    /// CLIENT_CONNECTED (14) or CLIENT_READY (8); the packet is relayed by the rules regardless.
    ClientMessage { message_id: u8, packet: Packet },
    TargetReady { link: LinkId },
    BacklogOverflow(Which),
    BacklogExpired,
    AckBacklog,
    DuplicateServerData,
    Silent,
}

/// The data plane under one session: the transport implements it per worker.
pub trait Io {
    /// The global relay pause (`Paused`).
    fn paused(&self) -> bool;
    fn link_connected(&self, link: LinkId) -> bool;
    /// A Relay frame toward a node (the node-link envelope is the transport's).
    fn send_to_node(&mut self, link: LinkId, client_id: u64, packet: Packet);
    /// Sends to the client; `None` when the client is not connected (the packet is dropped),
    /// otherwise the client's reliable queue length AFTER the send.
    fn send_to_client(&mut self, client_id: u64, packet: Packet) -> Option<usize>;
    /// The client's reliable queue length, `None` when not connected.
    fn client_reliable_queue(&self, client_id: u64) -> Option<usize>;
    fn send_credit(&mut self, link: LinkId, client_id: u64, bytes: i32);
    fn event(&mut self, client_id: u64, session: u64, event: Event);
}

/// Flow-control and backlog limits (`Config.backlog`, `Config.credit`); defaults are the C# constants.
#[derive(Debug, Clone, Copy)]
pub struct Limits {
    pub client_backlog_bytes: usize,
    pub client_backlog_age: Duration,
    pub server_backlog_bytes: usize,
    pub withhold_above: usize,
    pub release_below: usize,
    pub ack_backlog_limit: Duration,
    /// A condition C# has not acted on yet is reported again after this long.
    pub report_interval: Duration,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            client_backlog_bytes: 4 * 1024 * 1024,
            client_backlog_age: Duration::from_secs(10),
            server_backlog_bytes: 1024 * 1024,
            withhold_above: 4096,
            release_below: 3072,
            ack_backlog_limit: Duration::from_secs(10),
            report_interval: Duration::from_secs(1),
        }
    }
}

/// Credit per relayed packet on top of its payload (`SendToClient`).
const CREDIT_OVERHEAD: i64 = 10;

/// `SessionUpdate`: present fields are applied in one step, then the flushes run.
/// `link`/`validation_link` are `Some(None)` for an explicit null.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SessionUpdate {
    pub link: Option<Option<LinkId>>,
    pub attached: Option<bool>,
    pub routable: Option<bool>,
    pub switching: Option<bool>,
    /// T-0357: admitted, the Attach is being retried by ClusterRegistry (bounded there).
    pub attach_pending: Option<bool>,
    pub buffer_server: Option<bool>,
    pub suppress_join: Option<bool>,
    pub validation_link: Option<Option<LinkId>>,
    pub flush_client: bool,
    pub flush_server: bool,
    pub discard_server: bool,
    pub reset_type_table: bool,
}

/// Status of one session (`Status`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionStatus {
    pub link: Option<LinkId>,
    pub attached: bool,
    pub routable: bool,
    pub switching: bool,
    pub attach_pending: bool,
    pub buffer_server: bool,
    pub suppress_join: bool,
    pub type_table_sent: bool,
    pub client_backlog_packets: usize,
    pub client_backlog_bytes: usize,
    pub server_backlog_packets: usize,
    pub server_backlog_bytes: usize,
    pub withheld_credit: i64,
    pub reliable_backlog_ms: Option<u64>,
    pub duplicate_server_data: u64,
    /// Since the attached node last sent this client anything (C#'s `LastNodeActivity`).
    pub node_idle_ms: u64,
}

#[derive(Debug)]
pub struct Session {
    pub client_id: u64,
    pub session: u64,
    link: Option<LinkId>,
    attached: bool,
    routable: bool,
    switching: bool,
    attach_pending: bool,
    buffer_server: bool,
    suppress_join: bool,
    validation_link: Option<LinkId>,
    /// Re-armed by every update that carries a validation link; one `TargetReady` per arming.
    target_armed: bool,
    type_table_sent: bool,
    client_backlog: Backlog,
    server_backlog: Backlog,
    withheld_credit: i64,
    reliable_backlog_started: Option<Instant>,
    connect_packet: Option<Packet>,
    duplicate_server_data: u64,
    last_client_activity: Instant,
    last_node_activity: Instant,
    limits: Limits,
    reported: [Option<Instant>; 4],
}

#[derive(Clone, Copy)]
enum Report {
    ClientOverflow = 0,
    ServerOverflow = 1,
    Expired = 2,
    AckBacklog = 3,
}

impl Session {
    pub fn new(client_id: u64, session: u64, limits: Limits, now: Instant) -> Self {
        Self {
            client_id,
            session,
            link: None,
            attached: false,
            routable: false,
            switching: false,
            attach_pending: false,
            buffer_server: false,
            suppress_join: false,
            validation_link: None,
            target_armed: false,
            type_table_sent: false,
            client_backlog: Backlog::new(limits.client_backlog_bytes, Some(limits.client_backlog_age)),
            server_backlog: Backlog::new(limits.server_backlog_bytes, None),
            withheld_credit: 0,
            reliable_backlog_started: None,
            connect_packet: None,
            duplicate_server_data: 0,
            last_client_activity: now,
            last_node_activity: now,
            limits,
            reported: [None; 4],
        }
    }

    pub fn link(&self) -> Option<LinkId> {
        self.link
    }

    pub fn connect_packet(&self) -> Option<&Packet> {
        self.connect_packet.as_ref()
    }

    pub fn last_client_activity(&self) -> Instant {
        self.last_client_activity
    }

    fn event(&self, io: &mut impl Io, event: Event) {
        io.event(self.client_id, self.session, event);
    }

    /// Reports a standing condition at most once per `report_interval`: C# may legitimately ignore
    /// one (a pending join is exempt from the backlog age), and a lost report must not wedge a client.
    fn report(&mut self, io: &mut impl Io, kind: Report, event: Event, now: Instant) {
        let slot = &mut self.reported[kind as usize];
        if slot.is_some_and(|at| now.saturating_duration_since(at) < self.limits.report_interval) {
            return;
        }
        *slot = Some(now);
        self.event(io, event);
    }

    /// The `SendToBackend` condition: the client may reach its node now.
    fn forwardable(&self, io: &impl Io) -> Option<LinkId> {
        let link = self.link?;
        (!io.paused() && !self.switching && self.routable && self.attached && io.link_connected(link)).then_some(link)
    }

    /// A deliberate hold: the relay pause, a backend switch, or (T-0357) an admitted client whose
    /// Attach ClusterRegistry is still retrying. Each is bounded by its owner (the operator's
    /// resume, the switch's timeouts, the 90 s attach deadline), so the backlog's age is no
    /// evidence of a stuck client while it stands; the byte cap and the silence check still apply.
    fn held(&self, io: &impl Io) -> bool {
        io.paused() || self.switching || self.attach_pending
    }

    // ---------------------------------------------------------------- client -> node

    /// A packet from the client. `Err` is an invalid game frame: the C# relay drops it (and keeps
    /// the client), so the caller only counts it.
    pub fn on_client_packet(&mut self, packet: Packet, now: Instant, io: &mut impl Io) -> Result<(), FrameError> {
        self.last_client_activity = now;
        if let Some(id) = game_message_id(packet.channel, &packet.payload)? {
            if id == message::CLIENT_CONNECTED {
                self.connect_packet = Some(packet.clone());
            }
            if id == message::CLIENT_CONNECTED || id == message::CLIENT_READY {
                self.event(io, Event::ClientMessage { message_id: id, packet: packet.clone() });
            }
        }
        self.send_to_backend(packet, now, io);
        Ok(())
    }

    fn send_to_backend(&mut self, packet: Packet, now: Instant, io: &mut impl Io) {
        if let Some(link) = self.forwardable(io) {
            self.drain_client_backlog(link, io);
            io.send_to_node(link, self.client_id, packet);
            return;
        }
        // A deliberate hold is exempt from the age bound; the byte cap still catches a wedged
        // switch. Genuine unroutability ages.
        if self.held(io) {
            self.client_backlog.refresh_age(now);
        }
        if self.client_backlog.try_enqueue(packet, now).is_err() {
            self.report(io, Report::ClientOverflow, Event::BacklogOverflow(Which::Client), now);
        }
    }

    fn drain_client_backlog(&mut self, link: LinkId, io: &mut impl Io) {
        while let Some(held) = self.client_backlog.pop() {
            io.send_to_node(link, self.client_id, held);
        }
    }

    /// Drains the client backlog if forwarding is allowed now. The transport calls it after
    /// anything outside the session changes the condition: the pause, a link (re)connecting.
    pub fn poll_flush(&mut self, io: &mut impl Io) {
        if !self.client_backlog.is_empty()
            && let Some(link) = self.forwardable(io)
        {
            self.drain_client_backlog(link, io);
        }
    }

    /// `ReplayConnect` (`AttachBackend`): the captured CLIENT_CONNECTED to the current link.
    pub fn replay_connect(&self, io: &mut impl Io) -> bool {
        match (&self.connect_packet, self.link) {
            (Some(packet), Some(link)) => {
                io.send_to_node(link, self.client_id, packet.clone());
                true
            }
            _ => false,
        }
    }

    // ---------------------------------------------------------------- node -> client

    /// A Relay frame for this client from node link `from`. `Err` is an invalid game frame: the C#
    /// relay disconnects the node link for it, so the caller closes `from`.
    pub fn on_node_packet(&mut self, from: LinkId, packet: Packet, now: Instant, io: &mut impl Io) -> Result<(), FrameError> {
        if self.link != Some(from) {
            return Ok(());
        }
        self.last_node_activity = now;
        let message_id = game_message_id(packet.channel, &packet.payload)?;
        if let Some(id) = message_id {
            if self.suppress_join && self.type_table_sent && message::JOIN_SEQUENCE.contains(&id) {
                return Ok(());
            }
            if id == message::SERVER_DATA {
                if self.type_table_sent {
                    self.duplicate_server_data += 1;
                    self.event(io, Event::DuplicateServerData);
                    return Ok(());
                }
                self.type_table_sent = true;
            }
            if id == message::TARGET_READY && self.target_armed && self.validation_link == Some(from) {
                self.target_armed = false;
                self.event(io, Event::TargetReady { link: from });
            }
        }
        if self.buffer_server {
            if self.server_backlog.try_enqueue(packet, now).is_err() {
                self.report(io, Report::ServerOverflow, Event::BacklogOverflow(Which::Server), now);
            }
        } else {
            self.send_to_client(packet, now, io);
        }
        Ok(())
    }

    /// `SendToClient`: relay and pay the node credit, withheld while the client's reliable queue is
    /// congested.
    fn send_to_client(&mut self, packet: Packet, now: Instant, io: &mut impl Io) {
        let cost = packet.payload.len() as i64 + CREDIT_OVERHEAD;
        let Some(queue) = io.send_to_client(self.client_id, packet) else { return };
        if queue >= self.limits.withhold_above {
            self.reliable_backlog_started.get_or_insert(now);
            self.withheld_credit += cost;
        } else if self.reliable_backlog_started.is_none() {
            let credit = std::mem::take(&mut self.withheld_credit) + cost;
            if let Some(link) = self.link {
                send_credit(io, link, self.client_id, credit);
            }
        } else {
            self.withheld_credit += cost;
        }
    }

    // ---------------------------------------------------------------- control

    /// Applies a `SessionUpdate` from ClusterRegistry, then its flushes.
    pub fn apply(&mut self, update: &SessionUpdate, now: Instant, io: &mut impl Io) {
        if let Some(link) = update.link {
            self.link = link;
        }
        if let Some(value) = update.attached {
            self.attached = value;
        }
        if let Some(value) = update.routable {
            self.routable = value;
        }
        if let Some(value) = update.switching {
            self.switching = value;
        }
        if let Some(value) = update.attach_pending {
            self.attach_pending = value;
        }
        if let Some(value) = update.buffer_server {
            self.buffer_server = value;
        }
        if let Some(value) = update.suppress_join {
            self.suppress_join = value;
        }
        if let Some(link) = update.validation_link {
            self.validation_link = link;
            self.target_armed = link.is_some();
        }
        if update.reset_type_table {
            self.type_table_sent = false;
        }
        if update.discard_server {
            self.server_backlog.clear();
        }
        // T-0296 r3: not buffering means nothing stays held. ClusterRegistry flushes while
        // `bufferServer` is still set and clears the flag in a second update, so a node packet
        // between the two was enqueued and then stranded by an update that carried no flush.
        if update.flush_server || !self.buffer_server {
            while let Some(packet) = self.server_backlog.pop() {
                self.send_to_client(packet, now, io);
            }
        }
        // flush_client is implied: the backlog drains whenever forwarding is allowed.
        self.poll_flush(io);
    }

    /// Every ~100 ms (`StartSessionMaintenance`): credit release, then the liveness checks.
    /// `silence_limit` is for transports with no disconnect detection of their own (Steam P2P).
    pub fn maintain(&mut self, now: Instant, silence_limit: Option<Duration>, io: &mut impl Io) {
        self.replenish_credit(io);
        // C# acts on an expired backlog only once the client is admitted (`Credential != null`),
        // and a pending join falls through to the checks below; so the report does not end the pass.
        // T-0357: the hold is exempt for as long as it STANDS, not only when a packet arrives in
        // it. A joining client sends its login and then waits, so nothing refreshed the age and a
        // join admitted under the boot pause was reported expired 10 s after its login
        // (2026-10-01 23:44:48), two seconds before the resume that would have attached it.
        if self.held(io) {
            self.client_backlog.refresh_age(now);
        } else if self.client_backlog.is_expired(now) {
            self.report(io, Report::Expired, Event::BacklogExpired, now);
        }
        if self
            .reliable_backlog_started
            .is_some_and(|started| now.saturating_duration_since(started) >= self.limits.ack_backlog_limit)
        {
            self.report(io, Report::AckBacklog, Event::AckBacklog, now);
        } else if silence_limit.is_some_and(|limit| now.saturating_duration_since(self.last_client_activity) >= limit) {
            // Silence ends the session in C#; report it once.
            self.last_client_activity = now;
            self.event(io, Event::Silent);
        }
    }

    /// `ReplenishClientCredit`.
    fn replenish_credit(&mut self, io: &mut impl Io) {
        if self.reliable_backlog_started.is_none() {
            return;
        }
        match io.client_reliable_queue(self.client_id) {
            Some(queue) if queue < self.limits.release_below => {}
            _ => return,
        }
        self.reliable_backlog_started = None;
        let credit = std::mem::take(&mut self.withheld_credit);
        if credit != 0
            && let Some(link) = self.link
        {
            send_credit(io, link, self.client_id, credit);
        }
    }

    pub fn status(&self, now: Instant) -> SessionStatus {
        SessionStatus {
            link: self.link,
            attached: self.attached,
            routable: self.routable,
            switching: self.switching,
            attach_pending: self.attach_pending,
            buffer_server: self.buffer_server,
            suppress_join: self.suppress_join,
            type_table_sent: self.type_table_sent,
            client_backlog_packets: self.client_backlog.len(),
            client_backlog_bytes: self.client_backlog.bytes(),
            server_backlog_packets: self.server_backlog.len(),
            server_backlog_bytes: self.server_backlog.bytes(),
            withheld_credit: self.withheld_credit,
            reliable_backlog_ms: self.reliable_backlog_started.map(|at| now.saturating_duration_since(at).as_millis() as u64),
            duplicate_server_data: self.duplicate_server_data,
            node_idle_ms: now.saturating_duration_since(self.last_node_activity).as_millis() as u64,
        }
    }
}

/// C# keeps credit in an `int`; a larger sum (never seen) is paid in `i32::MAX` slices.
fn send_credit(io: &mut impl Io, link: LinkId, client_id: u64, mut credit: i64) {
    while credit > 0 {
        let slice = credit.min(i32::MAX as i64);
        io.send_credit(link, client_id, slice as i32);
        credit -= slice;
    }
}

#[cfg(test)]
mod tests;
