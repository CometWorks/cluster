use std::collections::HashSet;
use std::time::{Duration, Instant};

use super::*;
use crate::frame::crc32;

#[derive(Debug, Clone, PartialEq, Eq)]
enum Out {
    Node(LinkId, u8),
    Client(u8),
    Credit(LinkId, i32),
    Event(Event),
}

#[derive(Default)]
struct Mock {
    paused: bool,
    connected: HashSet<LinkId>,
    client_connected: bool,
    queue: usize,
    out: Vec<Out>,
}

impl Mock {
    fn new() -> Self {
        Self { connected: HashSet::from([1, 2]), client_connected: true, ..Self::default() }
    }

    fn take(&mut self) -> Vec<Out> {
        std::mem::take(&mut self.out)
    }
}

/// The message id stands in for the packet in `Out`: every test frame carries a distinct first
/// payload byte after the header (see `game`), recorded as the tag.
fn tag(packet: &Packet) -> u8 {
    if packet.channel == 2 { packet.payload[10] } else { packet.payload[0] }
}

impl Io for Mock {
    fn paused(&self) -> bool {
        self.paused
    }
    fn link_connected(&self, link: LinkId) -> bool {
        self.connected.contains(&link)
    }
    fn send_to_node(&mut self, link: LinkId, _client_id: u64, packet: Packet) {
        self.out.push(Out::Node(link, tag(&packet)));
    }
    fn send_to_client(&mut self, _client_id: u64, packet: Packet) -> Option<usize> {
        if !self.client_connected {
            return None;
        }
        self.out.push(Out::Client(tag(&packet)));
        Some(self.queue)
    }
    fn client_reliable_queue(&self, _client_id: u64) -> Option<usize> {
        self.client_connected.then_some(self.queue)
    }
    fn send_credit(&mut self, link: LinkId, _client_id: u64, bytes: i32) {
        self.out.push(Out::Credit(link, bytes));
    }
    fn event(&mut self, client_id: u64, session: u64, event: Event) {
        assert_eq!((client_id, session), (7, 70));
        self.out.push(Out::Event(event));
    }
}

/// A CRC-flagged single-part game frame, 12 bytes: message id at [8], tag at [10].
fn game(message_id: u8, tag: u8) -> Packet {
    let mut payload = vec![0xCE, 1, 0, 0, 0, 0, 0, 1, message_id, 0, tag, 0];
    let crc = crc32(&payload[6..]);
    payload[2..6].copy_from_slice(&crc.to_le_bytes());
    Packet { channel: 2, delivery: Delivery::ReliableOrdered, payload }
}

/// A non-game packet (channel 0) of `len` bytes tagged by its first byte.
fn raw(tag: u8, len: usize) -> Packet {
    let mut payload = vec![0; len.max(1)];
    payload[0] = tag;
    Packet { channel: 0, delivery: Delivery::ReliableOrdered, payload }
}

fn routed(link: LinkId) -> SessionUpdate {
    SessionUpdate {
        link: Some(Some(link)),
        attached: Some(true),
        routable: Some(true),
        switching: Some(false),
        buffer_server: Some(false),
        suppress_join: Some(false),
        validation_link: Some(None),
        ..SessionUpdate::default()
    }
}

fn session(now: Instant) -> Session {
    Session::new(7, 70, Limits::default(), now)
}

#[test]
fn client_packets_forward_only_when_every_condition_holds() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.on_client_packet(raw(1, 4), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![], "no link: backlogged");
    s.apply(&routed(1), now, &mut io);
    assert_eq!(io.take(), vec![Out::Node(1, 1)], "routing drains the backlog");

    for (name, update) in [
        ("switching", SessionUpdate { switching: Some(true), ..Default::default() }),
        ("not attached", SessionUpdate { attached: Some(false), ..Default::default() }),
        ("not routable", SessionUpdate { routable: Some(false), ..Default::default() }),
        ("no link", SessionUpdate { link: Some(None), ..Default::default() }),
        ("link down", SessionUpdate { link: Some(Some(3)), ..Default::default() }),
    ] {
        let mut s = session(now);
        s.apply(&routed(1), now, &mut io);
        s.apply(&update, now, &mut io);
        s.on_client_packet(raw(2, 4), now, &mut io).unwrap();
        assert_eq!(io.take(), vec![], "{name}: must hold");
        assert_eq!(s.status(now).client_backlog_packets, 1, "{name}");
    }

    io.paused = true;
    s.on_client_packet(raw(3, 4), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![], "paused: held");
    io.paused = false;
    s.on_client_packet(raw(4, 4), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Node(1, 3), Out::Node(1, 4)], "backlog first, order kept");
}

#[test]
fn backlog_drains_when_a_link_reconnects_or_the_pause_lifts() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    io.connected.remove(&1);
    s.on_client_packet(raw(1, 4), now, &mut io).unwrap();
    s.poll_flush(&mut io);
    assert_eq!(io.take(), vec![]);
    io.connected.insert(1);
    s.poll_flush(&mut io);
    assert_eq!(io.take(), vec![Out::Node(1, 1)]);
}

#[test]
fn client_backlog_overflow_and_expiry_are_reported_and_rate_limited() {
    let now = Instant::now();
    let mut io = Mock::new();
    let limits = Limits { client_backlog_bytes: 20, ..Limits::default() };
    let mut s = Session::new(7, 70, limits, now);
    s.on_client_packet(raw(1, 10), now, &mut io).unwrap(); // 12 bytes
    s.on_client_packet(raw(2, 10), now, &mut io).unwrap(); // 24 > 20
    s.on_client_packet(raw(3, 10), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Event(Event::BacklogOverflow(Which::Client))]);
    s.on_client_packet(raw(4, 10), now + Duration::from_millis(1001), &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Event(Event::BacklogOverflow(Which::Client))]);

    // Unroutable: the age bound runs; 10 s later the backlog is expired.
    let mut s = session(now);
    s.on_client_packet(raw(1, 4), now, &mut io).unwrap();
    s.maintain(now + Duration::from_secs(9), None, &mut io);
    assert_eq!(io.take(), vec![]);
    s.maintain(now + Duration::from_millis(10_100), None, &mut io);
    s.maintain(now + Duration::from_millis(10_200), None, &mut io);
    assert_eq!(io.take(), vec![Out::Event(Event::BacklogExpired)], "once per report interval");
    s.maintain(now + Duration::from_millis(11_200), None, &mut io);
    assert_eq!(io.take(), vec![Out::Event(Event::BacklogExpired)], "re-reported while it stands");
}

#[test]
fn a_deliberate_hold_does_not_age_the_backlog() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    s.apply(&SessionUpdate { switching: Some(true), ..Default::default() }, now, &mut io);
    for second in 0..30 {
        s.on_client_packet(raw(1, 4), now + Duration::from_secs(second), &mut io).unwrap();
    }
    s.maintain(now + Duration::from_millis(29_500), None, &mut io);
    assert_eq!(io.take(), vec![], "a packet every second keeps a switching hold fresh");
    s.apply(&SessionUpdate { switching: Some(false), flush_client: true, ..Default::default() }, now, &mut io);
    assert_eq!(io.take().len(), 30);
}

/// T-0357 (2026-10-01 23:44:48): a joining client sends its login once and then waits. Admitted
/// while the gateway was still paused for the boot, its backlog was reported expired 10 s after
/// the login, because only a packet ARRIVING in the hold refreshed the age.
#[test]
fn a_quiet_client_is_not_aged_while_a_hold_stands() {
    let now = Instant::now();
    let at = |ms: u64| now + Duration::from_millis(ms);
    for hold in ["paused", "switching", "attachPending"] {
        let mut io = Mock::new();
        let mut s = session(now);
        match hold {
            "paused" => io.paused = true,
            "switching" => s.apply(&SessionUpdate { switching: Some(true), ..Default::default() }, now, &mut io),
            _ => s.apply(&SessionUpdate { attach_pending: Some(true), ..Default::default() }, now, &mut io),
        }
        s.on_client_packet(game(14, 1), now, &mut io).unwrap(); // the login, then nothing
        io.take();
        for tick in 1..=600 {
            s.maintain(at(tick * 100), None, &mut io);
        }
        assert_eq!(io.take(), vec![], "{hold}: 60 s in the hold, no expiry");
        // The hold ends without the client becoming routable: the age bound runs again, from then.
        io.paused = false;
        s.apply(&SessionUpdate { switching: Some(false), attach_pending: Some(false), ..Default::default() }, at(60_000), &mut io);
        s.maintain(at(69_000), None, &mut io);
        assert_eq!(io.take(), vec![], "{hold}: 9 s after the hold");
        s.maintain(at(70_200), None, &mut io);
        assert_eq!(io.take(), vec![Out::Event(Event::BacklogExpired)], "{hold}: a session that never attaches still expires");
    }
}

/// The hold never hides a dead client: silence is still reported.
#[test]
fn a_hold_does_not_hide_silence() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&SessionUpdate { attach_pending: Some(true), ..Default::default() }, now, &mut io);
    s.on_client_packet(game(14, 1), now, &mut io).unwrap();
    io.take();
    s.maintain(now + Duration::from_secs(181), Some(Duration::from_secs(180)), &mut io);
    assert_eq!(io.take(), vec![Out::Event(Event::Silent)]);
}

#[test]
fn client_messages_are_reported_captured_and_still_relayed() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    s.on_client_packet(game(14, 5), now, &mut io).unwrap();
    s.on_client_packet(game(8, 6), now, &mut io).unwrap();
    s.on_client_packet(game(9, 7), now, &mut io).unwrap();
    assert_eq!(
        io.take(),
        vec![
            Out::Event(Event::ClientMessage { message_id: 14, packet: game(14, 5) }),
            Out::Node(1, 5),
            Out::Event(Event::ClientMessage { message_id: 8, packet: game(8, 6) }),
            Out::Node(1, 6),
            Out::Node(1, 7),
        ]
    );
    s.apply(&SessionUpdate { link: Some(Some(2)), ..Default::default() }, now, &mut io);
    assert!(s.replay_connect(&mut io));
    assert_eq!(io.take(), vec![Out::Node(2, 5)], "the captured CLIENT_CONNECTED goes to the new link");

    let mut bad = game(14, 9);
    bad.payload[11] ^= 0xFF;
    assert_eq!(s.on_client_packet(bad, now, &mut io), Err(FrameError::Crc));
    assert_eq!(io.take(), vec![], "an invalid client frame is dropped");
}

#[test]
fn node_packets_only_from_the_current_link_and_credit_is_paid() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    s.on_node_packet(2, raw(1, 20), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![], "stale link");
    s.on_node_packet(1, raw(2, 20), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Client(2), Out::Credit(1, 30)]);

    let mut bad = game(9, 3);
    bad.payload[0] = 0;
    assert_eq!(s.on_node_packet(1, bad, now, &mut io), Err(FrameError::Header), "caller closes the link");

    io.client_connected = false;
    s.on_node_packet(1, raw(4, 20), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![], "client gone: dropped, no credit");
}

#[test]
fn credit_is_withheld_while_the_client_is_congested_and_released_below_the_mark() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    io.queue = 4096;
    s.on_node_packet(1, raw(1, 90), now, &mut io).unwrap();
    io.queue = 4000; // below the withhold mark, but the episode has started: still withheld
    s.on_node_packet(1, raw(2, 40), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Client(1), Out::Client(2)]);
    assert_eq!(s.status(now).withheld_credit, 150);
    s.maintain(now + Duration::from_millis(100), None, &mut io);
    assert_eq!(io.take(), vec![], "not below 3072 yet");
    io.queue = 3071;
    s.maintain(now + Duration::from_millis(200), None, &mut io);
    assert_eq!(io.take(), vec![Out::Credit(1, 150)]);
    s.on_node_packet(1, raw(3, 0), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Client(3), Out::Credit(1, 11)]);

    io.queue = 5000;
    s.on_node_packet(1, raw(4, 0), now, &mut io).unwrap();
    io.take();
    s.maintain(now + Duration::from_secs(10), None, &mut io);
    assert_eq!(io.take(), vec![Out::Event(Event::AckBacklog)], "congested for 10 s");
}

#[test]
fn join_suppression_and_duplicate_server_data() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    s.apply(&SessionUpdate { suppress_join: Some(true), ..Default::default() }, now, &mut io);
    s.on_node_packet(1, game(12, 1), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Client(1), Out::Credit(1, 22)], "no type table yet: the join sequence passes");
    s.on_node_packet(1, game(6, 2), now, &mut io).unwrap();
    s.on_node_packet(1, game(6, 3), now, &mut io).unwrap();
    for id in [2, 12, 13, 20] {
        s.on_node_packet(1, game(id, 4), now, &mut io).unwrap();
    }
    s.on_node_packet(1, game(21, 5), now, &mut io).unwrap();
    assert_eq!(
        io.take(),
        vec![Out::Client(2), Out::Credit(1, 22), Out::Event(Event::DuplicateServerData), Out::Client(5), Out::Credit(1, 22)]
    );
    assert_eq!(s.status(now).duplicate_server_data, 1);
    s.apply(&SessionUpdate { reset_type_table: true, ..Default::default() }, now, &mut io);
    s.on_node_packet(1, game(6, 6), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Client(6), Out::Credit(1, 22)]);
}

#[test]
fn server_packets_buffer_during_a_switch_then_flush_or_discard() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    let switch = SessionUpdate {
        link: Some(Some(2)),
        attached: Some(false),
        switching: Some(true),
        buffer_server: Some(true),
        suppress_join: Some(true),
        validation_link: Some(Some(2)),
        ..Default::default()
    };
    s.apply(&switch, now, &mut io);
    s.on_node_packet(1, raw(1, 4), now, &mut io).unwrap();
    s.on_node_packet(2, game(7, 2), now, &mut io).unwrap();
    s.on_node_packet(2, game(7, 3), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Event(Event::TargetReady { link: 2 })], "one report per arming; nothing to the client");
    s.apply(&switch, now, &mut io);
    s.on_node_packet(2, game(7, 4), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Event(Event::TargetReady { link: 2 })], "re-armed by the next update");

    s.apply(
        &SessionUpdate {
            attached: Some(true),
            switching: Some(false),
            buffer_server: Some(false),
            flush_server: true,
            ..Default::default()
        },
        now,
        &mut io,
    );
    assert_eq!(
        io.take(),
        vec![Out::Client(2), Out::Credit(2, 22), Out::Client(3), Out::Credit(2, 22), Out::Client(4), Out::Credit(2, 22)]
    );

    s.apply(&SessionUpdate { buffer_server: Some(true), ..Default::default() }, now, &mut io);
    s.on_node_packet(2, raw(5, 4), now, &mut io).unwrap();
    s.apply(&SessionUpdate { buffer_server: Some(false), discard_server: true, flush_server: true, ..Default::default() }, now, &mut io);
    assert_eq!(io.take(), vec![], "discarded before the flush");

    let limits = Limits { server_backlog_bytes: 10, ..Limits::default() };
    let mut s = Session::new(7, 70, limits, now);
    s.apply(&routed(1), now, &mut io);
    s.apply(&SessionUpdate { buffer_server: Some(true), ..Default::default() }, now, &mut io);
    s.on_node_packet(1, raw(1, 20), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![Out::Event(Event::BacklogOverflow(Which::Server))]);
}

/// T-0296 r3: ClusterRegistry ends a switch with TWO updates (`FlushServerBacklog`, then the
/// `BufferServerPackets = false` setter): the first flushes while `bufferServer` is still true, the
/// second clears the flag without a flush. A node packet between the two was buffered and then
/// stranded: lost to the client, and replayed stale at the next switch's flush.
#[test]
fn a_node_packet_between_the_flush_and_the_unbuffer_is_not_stranded() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.apply(&routed(1), now, &mut io);
    s.apply(&SessionUpdate { buffer_server: Some(true), ..Default::default() }, now, &mut io);
    s.on_node_packet(1, raw(1, 4), now, &mut io).unwrap();
    s.apply(&SessionUpdate { buffer_server: Some(true), flush_server: true, ..Default::default() }, now, &mut io);
    assert_eq!(io.take(), vec![Out::Client(1), Out::Credit(1, 14)]);
    s.on_node_packet(1, raw(2, 4), now, &mut io).unwrap();
    assert_eq!(io.take(), vec![], "still buffering");
    s.apply(&SessionUpdate { buffer_server: Some(false), ..Default::default() }, now, &mut io);
    s.on_node_packet(1, raw(3, 4), now, &mut io).unwrap();
    assert_eq!(
        io.take(),
        vec![Out::Client(2), Out::Credit(1, 14), Out::Client(3), Out::Credit(1, 14)],
        "the packet between the two updates reaches the client, in order"
    );
    assert_eq!(s.status(now).server_backlog_packets, 0);

    // A discard still wins: what was held for a switch that did not commit is dropped, not released.
    s.apply(&SessionUpdate { buffer_server: Some(true), ..Default::default() }, now, &mut io);
    s.on_node_packet(1, raw(4, 4), now, &mut io).unwrap();
    s.apply(&SessionUpdate { buffer_server: Some(true), discard_server: true, ..Default::default() }, now, &mut io);
    s.apply(&SessionUpdate { buffer_server: Some(false), ..Default::default() }, now, &mut io);
    assert_eq!(io.take(), vec![]);
}

/// A pending join (C# ignores `backlogExpired` until the ticket is validated) still goes silent:
/// the expired backlog must not hide the silence check (StartSessionMaintenance falls through).
#[test]
fn an_expired_backlog_does_not_hide_silence() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.on_client_packet(game(14, 1), now, &mut io).unwrap();
    io.take();
    let limit = Some(Duration::from_secs(180));
    s.maintain(now + Duration::from_secs(181), limit, &mut io);
    let events = io.take();
    assert!(events.contains(&Out::Event(Event::BacklogExpired)), "{events:?}");
    assert!(events.contains(&Out::Event(Event::Silent)), "{events:?}");
}

#[test]
fn silence_is_reported_only_where_asked() {
    let now = Instant::now();
    let mut io = Mock::new();
    let mut s = session(now);
    s.maintain(now + Duration::from_secs(600), None, &mut io);
    assert_eq!(io.take(), vec![]);
    let limit = Some(Duration::from_secs(180));
    s.maintain(now + Duration::from_secs(179), limit, &mut io);
    assert_eq!(io.take(), vec![]);
    s.maintain(now + Duration::from_secs(180), limit, &mut io);
    s.maintain(now + Duration::from_secs(181), limit, &mut io);
    assert_eq!(io.take(), vec![Out::Event(Event::Silent)]);
}

#[test]
fn oversized_credit_is_paid_in_int_slices() {
    let mut io = Mock::new();
    send_credit(&mut io, 1, 7, i32::MAX as i64 + 5);
    assert_eq!(io.take(), vec![Out::Credit(1, i32::MAX), Out::Credit(1, 5)]);
}
