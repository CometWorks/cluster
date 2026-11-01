//! In-memory network with a virtual clock: loss, duplication, delay and jitter (which reorders).
#![allow(dead_code)]

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::time::{Duration, Instant};

use lnl::{Config, DeliveryMethod, Endpoint, Event, Output, PeerId};

/// xorshift64*: deterministic randomness without dependencies.
#[derive(Clone)]
pub struct Rng(pub u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Rng(seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1)
    }

    pub fn next_u64(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.0 = x;
        x.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    pub fn chance(&mut self, p: f64) -> bool {
        p > 0.0 && ((self.next_u64() >> 11) as f64 / (1u64 << 53) as f64) < p
    }

    pub fn below(&mut self, n: u64) -> u64 {
        if n == 0 { 0 } else { self.next_u64() % n }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct LinkModel {
    pub loss: f64,
    pub dup: f64,
    pub delay_ms: u64,
    pub jitter_ms: u64,
}

pub struct Datagram {
    pub at: Instant,
    pub from: SocketAddr,
    pub to: SocketAddr,
    pub data: Vec<u8>,
}

pub struct Net {
    pub model: LinkModel,
    pub rng: Rng,
    pub queue: Vec<Datagram>,
    pub sent: u64,
    pub dropped: u64,
    pub now: Instant,
    /// Every datagram offered to the network (before loss), for wire assertions.
    pub log: Option<Vec<(SocketAddr, SocketAddr, Vec<u8>)>>,
    /// Addresses whose outgoing and incoming traffic is dropped.
    pub cut: Vec<SocketAddr>,
}

impl Net {
    fn push(&mut self, from: SocketAddr, to: SocketAddr, data: &[u8]) {
        self.sent += 1;
        if let Some(log) = self.log.as_mut() {
            log.push((from, to, data.to_vec()));
        }
        if self.cut.contains(&from) || self.cut.contains(&to) || self.rng.chance(self.model.loss) {
            self.dropped += 1;
            return;
        }
        let copies = if self.rng.chance(self.model.dup) {
            2
        } else {
            1
        };
        for _ in 0..copies {
            let delay = self.model.delay_ms + self.rng.below(self.model.jitter_ms + 1);
            self.queue.push(Datagram {
                at: self.now + Duration::from_millis(delay),
                from,
                to,
                data: data.to_vec(),
            });
        }
    }
}

pub struct Node {
    pub addr: SocketAddr,
    pub ep: Endpoint,
    pub events: VecDeque<Event>,
}

struct Out<'a> {
    from: SocketAddr,
    net: &'a mut Net,
    events: &'a mut VecDeque<Event>,
}

impl Output for Out<'_> {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        self.net.push(self.from, to, datagram);
    }

    fn event(&mut self, event: Event) {
        self.events.push_back(event);
    }
}

pub struct Sim {
    pub t0: Instant,
    pub net: Net,
    pub nodes: Vec<Node>,
    pub poll_every_ms: u64,
    ticks: u64,
}

pub fn addr(port: u16) -> SocketAddr {
    SocketAddr::from(([10, 0, 0, 1], port))
}

pub const TICKS_2026: i64 = 639_000_000_000_000_000;

impl Sim {
    pub fn new(model: LinkModel, seed: u64) -> Self {
        let t0 = Instant::now();
        Sim {
            t0,
            net: Net {
                model,
                rng: Rng::new(seed),
                queue: Vec::new(),
                sent: 0,
                dropped: 0,
                now: t0,
                log: None,
                cut: Vec::new(),
            },
            nodes: Vec::new(),
            poll_every_ms: 5,
            ticks: 0,
        }
    }

    pub fn now(&self) -> Instant {
        self.net.now
    }

    pub fn elapsed_ms(&self) -> u64 {
        (self.net.now - self.t0).as_millis() as u64
    }

    /// Adds an endpoint at `addr`; returns its node index.
    pub fn add(&mut self, addr: SocketAddr, config: Config) -> usize {
        // Distinct wall clocks per node, like separate machines; later nodes connect "later".
        let ticks = TICKS_2026 + self.elapsed_ms() as i64 * 10_000;
        self.nodes.push(Node {
            addr,
            ep: Endpoint::with_clock(config, self.net.now, ticks),
            events: VecDeque::new(),
        });
        self.nodes.len() - 1
    }

    /// Runs `f` against node `i` with an output wired into the network.
    pub fn with<R>(
        &mut self,
        i: usize,
        f: impl FnOnce(&mut Endpoint, &mut dyn Output, Instant) -> R,
    ) -> R {
        let now = self.net.now;
        let node = &mut self.nodes[i];
        let mut out = Out {
            from: node.addr,
            net: &mut self.net,
            events: &mut node.events,
        };
        f(&mut node.ep, &mut out, now)
    }

    pub fn connect(&mut self, i: usize, to: SocketAddr, data: &[u8]) -> PeerId {
        self.with(i, |ep, out, now| ep.connect(now, to, data, out))
            .unwrap()
    }

    pub fn send(
        &mut self,
        i: usize,
        peer: PeerId,
        data: &[u8],
        channel: u8,
        method: DeliveryMethod,
    ) {
        self.nodes[i].ep.send(peer, data, channel, method).unwrap();
    }

    /// Advances the clock by 1 ms: delivers due datagrams, polls every `poll_every_ms`.
    pub fn step(&mut self) {
        self.net.now += Duration::from_millis(1);
        self.ticks += 1;
        let now = self.net.now;
        let mut due = Vec::new();
        let mut i = 0;
        while i < self.net.queue.len() {
            if self.net.queue[i].at <= now {
                due.push(self.net.queue.remove(i));
            } else {
                i += 1;
            }
        }
        for d in due {
            if let Some(node) = self.nodes.iter_mut().find(|n| n.addr == d.to) {
                let mut out = Out {
                    from: node.addr,
                    net: &mut self.net,
                    events: &mut node.events,
                };
                node.ep.handle(now, d.from, &d.data, &mut out);
            }
        }
        if self.ticks.is_multiple_of(self.poll_every_ms) {
            for node in self.nodes.iter_mut() {
                let mut out = Out {
                    from: node.addr,
                    net: &mut self.net,
                    events: &mut node.events,
                };
                node.ep.poll(now, &mut out);
            }
        }
    }

    pub fn run_ms(&mut self, ms: u64) {
        for _ in 0..ms {
            self.step();
        }
    }

    /// Steps until `cond` holds; panics after `max_ms`.
    pub fn run_until(&mut self, max_ms: u64, mut cond: impl FnMut(&mut Sim) -> bool) {
        for _ in 0..max_ms {
            if cond(self) {
                return;
            }
            self.step();
        }
        assert!(cond(self), "condition not reached within {max_ms} ms");
    }

    pub fn take_events(&mut self, i: usize) -> Vec<Event> {
        self.nodes[i].events.drain(..).collect()
    }
}

/// Server + client with the default request accepted; returns (server peer on client, client peer on server).
pub fn connected_pair(
    sim: &mut Sim,
    server: usize,
    client: usize,
    data: &[u8],
) -> (PeerId, PeerId) {
    let server_addr = sim.nodes[server].addr;
    let on_client = sim.connect(client, server_addr, data);
    let mut on_server = None;
    sim.run_until(10_000, |s| {
        while let Some(e) = s.nodes[server].events.pop_front() {
            match e {
                Event::ConnectionRequest {
                    request, data: d, ..
                } => {
                    assert_eq!(d, data);
                    let p = s.with(server, |ep, out, now| ep.accept(now, request, out));
                    on_server = p;
                }
                Event::Connected { .. } => {}
                other => panic!("unexpected server event {other:?}"),
            }
        }
        on_server.is_some()
            && s.nodes[client].ep.peer(on_client).unwrap().state == lnl::ConnectionState::Connected
    });
    let evs = sim.take_events(client);
    assert!(
        evs.iter()
            .any(|e| matches!(e, Event::Connected { peer, .. } if *peer == on_client)),
        "{evs:?}"
    );
    (on_client, on_server.unwrap())
}

/// Scripted payloads: `[method u8][channel u8][seq u32][len u32]` then a pattern derived from them.
pub fn payload(method: DeliveryMethod, channel: u8, seq: u32, len: usize) -> Vec<u8> {
    assert!(len >= 10);
    let mut v = Vec::with_capacity(len);
    v.push(method as u8);
    v.push(channel);
    v.extend_from_slice(&seq.to_le_bytes());
    v.extend_from_slice(&(len as u32).to_le_bytes());
    for i in 10..len {
        v.push(pattern(method as u8, channel, seq, i));
    }
    v
}

pub fn pattern(method: u8, channel: u8, seq: u32, i: usize) -> u8 {
    (seq.wrapping_mul(31) as usize + i * 7 + method as usize * 13 + channel as usize) as u8
}

/// Checks a scripted payload; returns (method, channel, seq).
pub fn check_payload(data: &[u8]) -> Result<(u8, u8, u32), String> {
    if data.len() < 10 {
        return Err(format!("short payload {}", data.len()));
    }
    let (m, c) = (data[0], data[1]);
    let seq = u32::from_le_bytes(data[2..6].try_into().unwrap());
    let len = u32::from_le_bytes(data[6..10].try_into().unwrap()) as usize;
    if len != data.len() {
        return Err(format!("length {} != header {len}", data.len()));
    }
    for (i, b) in data.iter().enumerate().skip(10) {
        if *b != pattern(m, c, seq, i) {
            return Err(format!("corrupt byte {i} in m{m} c{c} seq{seq}"));
        }
    }
    Ok((m, c, seq))
}

/// A send plan: (method, channel, size) in send order; seq numbers count per (method, channel).
pub fn plan(channels: u8, per_channel: u32, big: bool) -> Vec<(DeliveryMethod, u8, usize)> {
    let mut v = Vec::new();
    for c in 0..channels {
        for m in [
            DeliveryMethod::ReliableUnordered,
            DeliveryMethod::Sequenced,
            DeliveryMethod::ReliableOrdered,
            DeliveryMethod::ReliableSequenced,
        ] {
            for s in 0..per_channel {
                v.push((
                    m,
                    c,
                    10 + ((s as usize * 97 + c as usize * 13 + m as usize * 7) % 990),
                ));
            }
        }
    }
    for s in 0..40usize {
        v.push((DeliveryMethod::Unreliable, 0, 10 + (s * 53) % 1000));
    }
    if big {
        let c = 1 % channels;
        let c2 = 2 % channels;
        v.push((DeliveryMethod::ReliableOrdered, c, 65536));
        v.push((DeliveryMethod::ReliableOrdered, c, 3000));
        v.push((DeliveryMethod::ReliableOrdered, c, 65536));
        v.push((DeliveryMethod::ReliableUnordered, c2, 65536));
        v.push((DeliveryMethod::ReliableUnordered, c2, 5000));
    }
    v
}

pub fn send_plan(sim: &mut Sim, node: usize, peer: PeerId, plan: &[(DeliveryMethod, u8, usize)]) {
    let mut seqs = std::collections::HashMap::new();
    for &(m, c, len) in plan {
        let s = seqs.entry((m as u8, c)).or_insert(0u32);
        let p = payload(m, c, *s, len);
        *s += 1;
        sim.send(node, peer, &p, c, m);
    }
}

#[derive(Default)]
pub struct Verifier {
    expected: std::collections::HashMap<(u8, u8), u32>,
    ordered_next: std::collections::HashMap<(u8, u8), u32>,
    seen: std::collections::HashMap<(u8, u8), Vec<bool>>,
    last: std::collections::HashMap<(u8, u8), u32>,
    pub received: u64,
    pub unreliable: u64,
}

impl Verifier {
    pub fn new(plan: &[(DeliveryMethod, u8, usize)]) -> Self {
        let mut v = Verifier::default();
        for &(m, c, _) in plan {
            *v.expected.entry((m as u8, c)).or_insert(0) += 1;
        }
        v
    }

    pub fn on_receive(&mut self, channel: u8, method: DeliveryMethod, data: &[u8]) {
        let (m, c, seq) = check_payload(data).unwrap();
        assert_eq!(m, method as u8, "method mismatch");
        if method == DeliveryMethod::Unreliable {
            assert_eq!(channel, 0);
        } else {
            assert_eq!(c, channel, "channel mismatch");
        }
        let key = (m, c);
        let n = self.expected[&key];
        assert!(seq < n, "seq {seq} out of plan");
        self.received += 1;
        match method {
            DeliveryMethod::ReliableOrdered => {
                let next = self.ordered_next.entry(key).or_insert(0);
                assert_eq!(seq, *next, "ReliableOrdered out of order on channel {c}");
                *next += 1;
            }
            DeliveryMethod::ReliableUnordered => {
                let seen = self
                    .seen
                    .entry(key)
                    .or_insert_with(|| vec![false; n as usize]);
                assert!(
                    !seen[seq as usize],
                    "ReliableUnordered duplicate {seq} on channel {c}"
                );
                seen[seq as usize] = true;
            }
            DeliveryMethod::Sequenced | DeliveryMethod::ReliableSequenced => {
                if let Some(l) = self.last.get(&key) {
                    assert!(
                        seq > *l,
                        "sequenced not increasing: {seq} after {l} (m{m} c{c})"
                    );
                }
                self.last.insert(key, seq);
            }
            DeliveryMethod::Unreliable => self.unreliable += 1,
        }
    }

    pub fn complete(&self) -> bool {
        self.expected
            .iter()
            .all(|(&(m, c), &n)| match DeliveryMethod::from_u8(m).unwrap() {
                DeliveryMethod::ReliableOrdered => self.ordered_next.get(&(m, c)) == Some(&n),
                DeliveryMethod::ReliableUnordered => {
                    self.seen.get(&(m, c)).is_some_and(|s| s.iter().all(|x| *x))
                }
                DeliveryMethod::ReliableSequenced => self.last.get(&(m, c)) == Some(&(n - 1)),
                _ => true,
            })
    }
}

/// Feeds node `i`'s Receive events to `v`, recycling buffers; returns other events.
pub fn drain_receives(sim: &mut Sim, i: usize, v: &mut Verifier) -> Vec<Event> {
    let mut other = Vec::new();
    while let Some(e) = sim.nodes[i].events.pop_front() {
        match e {
            Event::Receive {
                channel,
                method,
                packet,
                ..
            } => {
                v.on_receive(channel, method, packet.data());
                sim.nodes[i].ep.recycle(packet);
            }
            e => other.push(e),
        }
    }
    other
}
