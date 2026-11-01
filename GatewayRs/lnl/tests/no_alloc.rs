//! The steady state (connected peers exchanging small reliable, sequenced and unreliable
//! packets, with acks, merging, pings and pongs) must not allocate once the pool is warm.

use std::alloc::{GlobalAlloc, Layout, System};
use std::cell::Cell;
use std::collections::VecDeque;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use lnl::{Config, ConnectionState, DeliveryMethod, Endpoint, Event, Output};

struct Counting;

static ALLOCATIONS: AtomicU64 = AtomicU64::new(0);

thread_local! {
    static COUNTING: Cell<bool> = const { Cell::new(false) };
}

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        if COUNTING.with(|c| c.get()) {
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        if COUNTING.with(|c| c.get()) {
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { System.realloc(ptr, layout, new_size) }
    }
}

#[global_allocator]
static GLOBAL: Counting = Counting;

/// Preallocated datagram ring and event queue.
struct Wire {
    datagrams: Vec<(SocketAddr, usize, [u8; lnl::MAX_PACKET_SIZE])>,
    len: usize,
    events: VecDeque<Event>,
}

impl Wire {
    fn new() -> Self {
        Wire {
            datagrams: vec![
                (
                    SocketAddr::from(([0, 0, 0, 0], 0)),
                    0,
                    [0; lnl::MAX_PACKET_SIZE]
                );
                1024
            ],
            len: 0,
            events: VecDeque::with_capacity(4096),
        }
    }
}

impl Output for Wire {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        let slot = &mut self.datagrams[self.len];
        slot.0 = to;
        slot.1 = datagram.len();
        slot.2[..datagram.len()].copy_from_slice(datagram);
        self.len += 1;
    }

    fn event(&mut self, event: Event) {
        self.events.push_back(event);
    }
}

fn deliver(from: SocketAddr, wire: &mut Wire, to: &mut Endpoint, to_wire: &mut Wire, now: Instant) {
    for i in 0..wire.len {
        let (_, n, ref data) = wire.datagrams[i];
        to.handle(now, from, &data[..n], to_wire);
    }
    wire.len = 0;
}

fn drain(ep: &mut Endpoint, wire: &mut Wire) -> usize {
    let mut n = 0;
    while let Some(e) = wire.events.pop_front() {
        match e {
            Event::Receive { packet, .. } => {
                n += 1;
                ep.recycle(packet);
            }
            Event::ConnectionRequest { request, .. } => {
                ep.accept(Instant::now(), request, wire).unwrap();
            }
            _ => {}
        }
    }
    n
}

#[test]
fn steady_state_does_not_allocate() {
    let sa = SocketAddr::from(([10, 0, 0, 1], 1000));
    let ca = SocketAddr::from(([10, 0, 0, 2], 2000));
    let mut now = Instant::now();
    let cfg = Config {
        channels_count: 16,
        ..Config::default()
    };
    let mut server = Endpoint::new(cfg.clone(), now);
    let mut client = Endpoint::new(cfg, now);
    let (mut sw, mut cw) = (Wire::new(), Wire::new());
    let peer = client.connect(now, sa, b"hello", &mut cw).unwrap();
    let mut received = 0;
    let payload = [7u8; 200];
    let mut run = |iterations: usize, count: bool, received: &mut usize| {
        for i in 0..iterations {
            now += Duration::from_millis(1);
            if i % 2 == 0 && client.peer(peer).unwrap().state == ConnectionState::Connected {
                client
                    .send(
                        peer,
                        &payload,
                        (i % 16) as u8,
                        DeliveryMethod::ReliableOrdered,
                    )
                    .unwrap();
                client
                    .send(peer, &payload, 3, DeliveryMethod::ReliableUnordered)
                    .unwrap();
                client
                    .send(peer, &payload[..50], 5, DeliveryMethod::Sequenced)
                    .unwrap();
                client
                    .send(peer, &payload[..50], 6, DeliveryMethod::ReliableSequenced)
                    .unwrap();
                client
                    .send(peer, &payload[..80], 0, DeliveryMethod::Unreliable)
                    .unwrap();
            }
            COUNTING.with(|c| c.set(count));
            client.poll(now, &mut cw);
            deliver(ca, &mut cw, &mut server, &mut sw, now);
            server.poll(now, &mut sw);
            deliver(sa, &mut sw, &mut client, &mut cw, now);
            COUNTING.with(|c| c.set(false));
            *received += drain(&mut server, &mut sw);
            drain(&mut client, &mut cw);
        }
    };
    run(3000, false, &mut received);
    assert!(received > 5000, "warm-up received {received}");
    let before = received;
    run(10_000, true, &mut received);
    assert!(received - before > 20_000, "received {}", received - before);
    assert_eq!(
        ALLOCATIONS.load(Ordering::Relaxed),
        0,
        "allocations in steady state"
    );
}
