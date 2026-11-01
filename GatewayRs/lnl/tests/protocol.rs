//! Protocol tests on the in-memory network: delivery semantics under loss, reordering and
//! duplication, fragmentation, merging, the connection state machine and wire layouts.

mod common;

use common::*;
use lnl::wire::Writer;
use lnl::{
    Collect, Config, ConnectionState, DeliveryMethod, DisconnectReason, Endpoint, Event,
    PacketProperty,
};

fn cfg16() -> Config {
    Config {
        channels_count: 16,
        ..Config::default()
    }
}

fn handshake() -> Vec<u8> {
    let mut w = Writer::new();
    w.put_string("SE-DirectTransport-v1")
        .put_u64(42)
        .put_string("tester");
    w.buf
}

/// Sends the plan both ways and runs until both verifiers are complete.
fn exchange(model: LinkModel, seed: u64, channels: u8, per: u32, big: bool) -> (u64, u64) {
    let mut sim = Sim::new(model, seed);
    let cfg = Config {
        channels_count: channels,
        disconnect_timeout_ms: 30_000,
        ..Config::default()
    };
    let s = sim.add(addr(1000), cfg.clone());
    let c = sim.add(addr(2000), cfg);
    let (on_c, on_s) = connected_pair(&mut sim, s, c, &handshake());
    let p = plan(channels, per, big);
    send_plan(&mut sim, c, on_c, &p);
    send_plan(&mut sim, s, on_s, &p);
    let mut vs = Verifier::new(&p);
    let mut vc = Verifier::new(&p);
    sim.run_until(600_000, |sim| {
        let other = drain_receives(sim, s, &mut vs);
        assert!(
            other
                .iter()
                .all(|e| matches!(e, Event::LatencyUpdate { .. })),
            "server events {other:?}"
        );
        for e in drain_receives(sim, c, &mut vc) {
            if !matches!(e, Event::LatencyUpdate { .. }) {
                panic!("client event {e:?}");
            }
        }
        vs.complete() && vc.complete()
    });
    // Nothing more may arrive for reliable channels (no duplicates after completion).
    sim.run_ms(2000);
    drain_receives(&mut sim, s, &mut vs);
    drain_receives(&mut sim, c, &mut vc);
    (vs.unreliable, vc.unreliable)
}

#[test]
fn all_delivery_methods_perfect_link() {
    let (us, uc) = exchange(LinkModel::default(), 1, 16, 12, true);
    // Without loss every unreliable datagram arrives.
    assert_eq!((us, uc), (40, 40));
}

#[test]
fn all_delivery_methods_lossy_links() {
    for seed in 1..=12 {
        let model = LinkModel {
            loss: 0.10,
            dup: 0.05,
            delay_ms: 5,
            jitter_ms: 25,
        };
        exchange(model, seed, 4, 30, seed % 3 == 0);
    }
}

#[test]
fn heavy_loss_still_completes() {
    let model = LinkModel {
        loss: 0.35,
        dup: 0.10,
        delay_ms: 2,
        jitter_ms: 40,
    };
    exchange(model, 99, 2, 20, true);
}

#[test]
fn reliable_ordered_sequence_wraps() {
    // 300-byte messages: three per datagram, so jitter reorders packets inside the window.
    let mut sim = Sim::new(
        LinkModel {
            loss: 0.02,
            dup: 0.02,
            delay_ms: 1,
            jitter_ms: 6,
        },
        7,
    );
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    let total = 40_000u32; // more than MaxSequence (32768)
    for i in 0..total {
        let mut m = [0u8; 300];
        m[..4].copy_from_slice(&i.to_le_bytes());
        sim.send(c, on_c, &m, 0, DeliveryMethod::ReliableOrdered);
        sim.send(c, on_c, &m, 0, DeliveryMethod::ReliableUnordered);
    }
    let mut next = 0u32;
    let mut unordered = vec![false; total as usize];
    let mut unordered_count = 0;
    sim.run_until(2_000_000, |sim| {
        while let Some(e) = sim.nodes[s].events.pop_front() {
            if let Event::Receive { method, packet, .. } = e {
                let v = u32::from_le_bytes(packet.data()[..4].try_into().unwrap());
                if method == DeliveryMethod::ReliableOrdered {
                    assert_eq!(v, next);
                    next += 1;
                } else {
                    assert!(!unordered[v as usize], "duplicate {v}");
                    unordered[v as usize] = true;
                    unordered_count += 1;
                }
                sim.nodes[s].ep.recycle(packet);
            }
        }
        next == total && unordered_count == total
    });
}

#[test]
fn fragmented_payload_sizes() {
    let mut sim = Sim::new(
        LinkModel {
            loss: 0.05,
            dup: 0.05,
            delay_ms: 3,
            jitter_ms: 10,
        },
        3,
    );
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    // Around the single-packet limit (1024 - 4 = 1020) and the fragment payload (1024 - 10 = 1014).
    let sizes = [1019usize, 1020, 1021, 1014 * 2, 1014 * 2 + 1, 200_000];
    for (i, &n) in sizes.iter().enumerate() {
        let data: Vec<u8> = (0..n).map(|k| (k * 7 + i) as u8).collect();
        sim.send(c, on_c, &data, 0, DeliveryMethod::ReliableOrdered);
    }
    // Too big for the unreliable kinds.
    let big = vec![0u8; 1024];
    for (m, n) in [
        (DeliveryMethod::Unreliable, 1024),
        (DeliveryMethod::Sequenced, 1021),
        (DeliveryMethod::ReliableSequenced, 1021),
    ] {
        assert!(
            matches!(
                sim.nodes[c].ep.send(on_c, &big[..n], 0, m),
                Err(lnl::SendError::TooBig { .. })
            ),
            "{m:?}"
        );
    }
    assert!(
        sim.nodes[c]
            .ep
            .send(on_c, &big[..1020], 0, DeliveryMethod::Sequenced)
            .is_ok()
    );
    assert!(
        sim.nodes[c]
            .ep
            .send(on_c, &big[..1023], 0, DeliveryMethod::Unreliable)
            .is_ok()
    );
    let mut got = 0;
    sim.run_until(120_000, |sim| {
        while let Some(e) = sim.nodes[s].events.pop_front() {
            if let Event::Receive {
                method: DeliveryMethod::ReliableOrdered,
                packet,
                channel,
                ..
            } = e
            {
                assert_eq!(channel, 0);
                let n = sizes[got];
                assert_eq!(packet.len(), n);
                assert!(
                    packet
                        .data()
                        .iter()
                        .enumerate()
                        .all(|(k, b)| *b == (k * 7 + got) as u8)
                );
                got += 1;
                sim.nodes[s].ep.recycle(packet);
            }
        }
        got == sizes.len()
    });
}

#[test]
fn small_packets_are_merged() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    sim.run_ms(50);
    sim.net.log = Some(Vec::new());
    for i in 0..30u8 {
        sim.send(c, on_c, &[i; 20], 0, DeliveryMethod::Unreliable);
        sim.send(c, on_c, &[i; 30], 0, DeliveryMethod::ReliableOrdered);
    }
    let mut ctx = Collect::default();
    let now = sim.now();
    sim.nodes[c].ep.poll(now, &mut ctx);
    // 30 x (1+30+4+2) reliable... all packets fit in a handful of Merged datagrams <= MTU.
    assert!(ctx.transmits.len() < 6, "{} datagrams", ctx.transmits.len());
    for (_, d) in &ctx.transmits {
        assert_eq!(d[0] & 0x1F, PacketProperty::Merged as u8);
        assert!(d.len() <= 1024);
    }
    // Deliver them by hand and check every message arrives.
    for (to, d) in ctx.transmits.drain(..) {
        assert_eq!(to, addr(1000));
        sim.with(s, |ep, out, now| ep.handle(now, addr(2000), &d, out));
    }
    let evs = sim.take_events(s);
    let unrel = evs
        .iter()
        .filter(|e| {
            matches!(
                e,
                Event::Receive {
                    method: DeliveryMethod::Unreliable,
                    ..
                }
            )
        })
        .count();
    let rel = evs
        .iter()
        .filter(|e| {
            matches!(
                e,
                Event::Receive {
                    method: DeliveryMethod::ReliableOrdered,
                    ..
                }
            )
        })
        .count();
    assert_eq!((unrel, rel), (30, 30));

    // A single queued packet goes out unwrapped.
    sim.send(c, on_c, &[9; 5], 0, DeliveryMethod::Unreliable);
    let now = sim.now();
    sim.nodes[c].ep.poll(now, &mut ctx);
    let unrel: Vec<_> = ctx
        .transmits
        .iter()
        .filter(|(_, d)| d[0] & 0x1F == 0)
        .collect();
    assert_eq!(unrel.len(), 1);
    assert_eq!(unrel[0].1, vec![0, 9, 9, 9, 9, 9]);
}

#[test]
fn wire_layout_of_handshake_and_control_packets() {
    let t0 = std::time::Instant::now();
    let ticks = 638_900_000_000_000_000i64;
    let mut client = Endpoint::with_clock(Config::default(), t0, ticks);
    let mut out = Collect::default();
    let server = addr(27116);
    let data = handshake();
    let id = client.connect(t0, server, &data, &mut out).unwrap();
    assert_eq!(id.id, 0);
    let (to, req) = &out.transmits[0];
    assert_eq!(*to, server);
    assert_eq!(req[0], 5);
    assert_eq!(i32::from_le_bytes(req[1..5].try_into().unwrap()), 13);
    assert_eq!(i64::from_le_bytes(req[5..13].try_into().unwrap()), ticks);
    assert_eq!(i32::from_le_bytes(req[13..17].try_into().unwrap()), 0);
    assert_eq!(req[17], 16);
    assert_eq!(&req[18..22], &[2, 0, 0x69, 0xEC]); // AF_INET, port 27116 big-endian
    assert_eq!(&req[22..26], &[10, 0, 0, 1]);
    assert_eq!(&req[34..], &data[..]);

    // Server side: request event, accept -> 15-byte ConnectAccept.
    let mut srv = Endpoint::with_clock(Config::default(), t0, ticks + 5);
    let mut sout = Collect::default();
    srv.handle(t0, addr(5000), req, &mut sout);
    let Some(Event::ConnectionRequest {
        request, data: d, ..
    }) = sout.events.pop_front()
    else {
        panic!()
    };
    assert_eq!(d, data);
    // Repeated requests do not raise a second event.
    srv.handle(t0, addr(5000), req, &mut sout);
    assert!(sout.events.is_empty());
    srv.accept(t0, request, &mut sout).unwrap();
    let acc = &sout.transmits[0].1;
    assert_eq!(acc.len(), 15);
    assert_eq!(acc[0], 6);
    assert_eq!(i64::from_le_bytes(acc[1..9].try_into().unwrap()), ticks);
    assert_eq!(acc[9], 0);
    assert_eq!(acc[10], 0);
    assert_eq!(i32::from_le_bytes(acc[11..15].try_into().unwrap()), 0);

    // Wrong protocol id -> InvalidProtocol.
    let mut bad = req.clone();
    bad[1] = 12;
    sout.transmits.clear();
    srv.handle(t0, addr(5001), &bad, &mut sout);
    assert_eq!(sout.transmits, vec![(addr(5001), vec![15])]);
    // Unknown address sending data -> PeerNotFound.
    sout.transmits.clear();
    srv.handle(t0, addr(5002), &[0, 1, 2], &mut sout);
    assert_eq!(sout.transmits, vec![(addr(5002), vec![14])]);
    // Unknown address sending Disconnect -> ShutdownOk.
    sout.transmits.clear();
    srv.handle(t0, addr(5003), &[7, 0, 0, 0, 0, 0, 0, 0, 0], &mut sout);
    assert_eq!(sout.transmits, vec![(addr(5003), vec![13])]);

    // Client receives InvalidProtocol while connecting.
    client.handle(t0, server, &[15], &mut out);
    assert!(matches!(
        out.events.pop_front(),
        Some(Event::Disconnected {
            reason: DisconnectReason::InvalidProtocol,
            ..
        })
    ));
}

#[test]
fn ping_pong_rtt_and_ack_layout() {
    let mut sim = Sim::new(
        LinkModel {
            delay_ms: 20,
            ..LinkModel::default()
        },
        1,
    );
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, on_s) = connected_pair(&mut sim, s, c, b"");
    sim.net.log = Some(Vec::new());
    sim.send(c, on_c, b"hello", 0, DeliveryMethod::ReliableOrdered);
    sim.run_ms(3500);
    let rtt_c = sim.nodes[c].ep.peer(on_c).unwrap().rtt_ms;
    let rtt_s = sim.nodes[s].ep.peer(on_s).unwrap().rtt_ms;
    assert!((40..=50).contains(&rtt_c), "client rtt {rtt_c}");
    assert!((40..=50).contains(&rtt_s), "server rtt {rtt_s}");
    let evs = sim.take_events(c);
    assert!(evs.iter().any(
        |e| matches!(e, Event::LatencyUpdate { latency_ms, .. } if (20..=25).contains(latency_ms))
    ));
    let log = sim.net.log.take().unwrap();
    let pings: Vec<_> = log.iter().filter(|(_, _, d)| d[0] & 0x1F == 3).collect();
    let pongs: Vec<_> = log.iter().filter(|(_, _, d)| d[0] & 0x1F == 4).collect();
    assert!(pings.iter().all(|(_, _, d)| d.len() == 3));
    assert!(pongs.iter().all(|(_, _, d)| d.len() == 11));
    assert!(pings.len() >= 4, "{} pings", pings.len());
    // The reliable ack is a 13-byte Ack on channel index 2 (channel 0, ReliableOrdered).
    let acks: Vec<_> = log
        .iter()
        .filter(|(f, _, d)| *f == addr(1000) && d[0] & 0x1F == 2)
        .collect();
    assert!(!acks.is_empty());
    assert!(
        acks.iter().all(|(_, _, d)| d.len() == 13 && d[3] == 2),
        "{acks:?}"
    );
}

fn expect_disconnect(evs: &[Event], want: DisconnectReason, data: Option<&[u8]>) {
    let found = evs.iter().find_map(|e| match e {
        Event::Disconnected {
            reason, data: d, ..
        } => Some((*reason, d.as_ref().map(|p| p.to_vec()))),
        _ => None,
    });
    let (reason, d) = found.unwrap_or_else(|| panic!("no Disconnected in {evs:?}"));
    assert_eq!(reason, want);
    if let Some(expected) = data {
        assert_eq!(d.as_deref(), Some(expected));
    }
}

#[test]
fn graceful_disconnect_from_either_side() {
    for client_side in [true, false] {
        let mut sim = Sim::new(
            LinkModel {
                loss: 0.2,
                delay_ms: 5,
                ..LinkModel::default()
            },
            11,
        );
        let s = sim.add(addr(1000), Config::default());
        let c = sim.add(addr(2000), Config::default());
        let (on_c, on_s) = connected_pair(&mut sim, s, c, b"");
        sim.take_events(s);
        let (a, pa, b, pb) = if client_side {
            (c, on_c, s, on_s)
        } else {
            (s, on_s, c, on_c)
        };
        sim.with(a, |ep, out, _| ep.disconnect(pa, b"bye", out));
        expect_disconnect(
            &sim.take_events(a),
            DisconnectReason::DisconnectPeerCalled,
            None,
        );
        assert_eq!(
            sim.nodes[a].ep.peer(pa).unwrap().state,
            ConnectionState::ShutdownRequested
        );
        sim.run_until(5000, |sim| {
            sim.nodes[a].ep.peer(pa).unwrap().state == ConnectionState::Disconnected
        });
        expect_disconnect(
            &sim.take_events(b),
            DisconnectReason::RemoteConnectionClose,
            Some(b"bye"),
        );
        assert_eq!(
            sim.nodes[b].ep.peer(pb).unwrap().state,
            ConnectionState::Disconnected
        );
        // Disconnected peers linger for the disconnect timeout, then are removed.
        sim.run_ms(5200);
        assert!(sim.nodes[a].ep.peer(pa).is_none());
        assert!(sim.nodes[b].ep.peer(pb).is_none());
        assert_eq!(sim.nodes[a].ep.peers().count(), 0);
    }
}

#[test]
fn timeout_when_the_link_dies() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let cfg = Config {
        disconnect_timeout_ms: 2000,
        ..Config::default()
    };
    let s = sim.add(addr(1000), cfg.clone());
    let c = sim.add(addr(2000), cfg);
    let (on_c, on_s) = connected_pair(&mut sim, s, c, b"");
    sim.take_events(s);
    sim.run_ms(1000);
    sim.net.cut.push(addr(2000));
    let cut_at = sim.elapsed_ms();
    sim.run_until(5000, |sim| {
        sim.nodes[s].ep.peer(on_s).unwrap().state == ConnectionState::Disconnected
    });
    let waited = sim.elapsed_ms() - cut_at;
    assert!((1900..=2100).contains(&waited), "timeout after {waited} ms");
    expect_disconnect(&sim.take_events(s), DisconnectReason::Timeout, None);
    expect_disconnect(&sim.take_events(c), DisconnectReason::Timeout, None);
    assert_eq!(
        sim.nodes[c].ep.peer(on_c).unwrap().state,
        ConnectionState::Disconnected
    );
}

#[test]
fn reject_with_data_and_reject_force() {
    for force in [false, true] {
        // Reject resends its Disconnect until ShutdownOk, so it survives loss; RejectForce sends once.
        let loss = if force { 0.0 } else { 0.2 };
        let mut sim = Sim::new(
            LinkModel {
                loss,
                ..LinkModel::default()
            },
            5,
        );
        let s = sim.add(addr(1000), Config::default());
        let c = sim.add(addr(2000), Config::default());
        let on_c = sim.connect(c, addr(1000), b"key");
        sim.run_until(5000, |sim| !sim.nodes[s].events.is_empty());
        let Some(Event::ConnectionRequest { request, .. }) = sim.nodes[s].events.pop_front() else {
            panic!()
        };
        let answered = sim.with(s, |ep, out, now| {
            if force {
                ep.reject_force(request, b"go-away", out)
            } else {
                ep.reject(now, request, b"go-away", out)
            }
        });
        assert!(answered);
        sim.run_until(5000, |sim| {
            sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Disconnected
        });
        expect_disconnect(
            &sim.take_events(c),
            DisconnectReason::ConnectionRejected,
            Some(b"go-away"),
        );
        assert!(sim.take_events(s).is_empty());
        // The rejecting peer stops resending after ShutdownOk and is removed after the timeout.
        sim.run_ms(6000);
        assert_eq!(sim.nodes[s].ep.peers().count(), 0);
    }
}

#[test]
fn deferred_request_stays_pending_until_answered() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(
        addr(2000),
        Config {
            max_connect_attempts: 20,
            ..Config::default()
        },
    );
    sim.net.log = Some(Vec::new());
    let on_c = sim.connect(c, addr(1000), b"hold-me");
    sim.run_ms(3000);
    let evs = sim.take_events(s);
    assert_eq!(evs.len(), 1, "{evs:?}");
    let Event::ConnectionRequest { request, .. } = evs[0] else {
        panic!()
    };
    // The client kept retrying every ReconnectDelay (500 ms) and nothing answered.
    let log = sim.net.log.take().unwrap();
    let reqs = log.iter().filter(|(_, _, d)| d[0] & 0x1F == 5).count();
    assert!((6..=7).contains(&reqs), "{reqs} requests");
    assert!(log.iter().all(|(f, _, _)| *f == addr(2000)));
    assert_eq!(sim.nodes[s].ep.request_data(request), Some(&b"hold-me"[..]));
    assert_eq!(
        sim.nodes[c].ep.peer(on_c).unwrap().state,
        ConnectionState::Outgoing
    );
    let on_s = sim
        .with(s, |ep, out, now| ep.accept(now, request, out))
        .unwrap();
    sim.run_until(1000, |sim| {
        sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Connected
    });
    let now = sim.now();
    assert!(
        sim.nodes[s]
            .ep
            .accept(now, request, &mut Collect::default())
            .is_none()
    );
    sim.send(c, on_c, b"x", 0, DeliveryMethod::ReliableOrdered);
    sim.run_until(1000, |sim| {
        sim.nodes[s]
            .events
            .iter()
            .any(|e| matches!(e, Event::Receive { peer, .. } if *peer == on_s))
    });
}

#[test]
fn connection_fails_after_max_attempts() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(
        addr(2000),
        Config {
            max_connect_attempts: 3,
            reconnect_delay_ms: 200,
            ..Config::default()
        },
    );
    let on_c = sim.connect(c, addr(1000), b"");
    sim.run_until(5000, |sim| {
        sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Disconnected
    });
    // 1 initial + 3 retries, the 4th timer expiry fails the connection (~800 ms).
    assert!(
        (790..=830).contains(&sim.elapsed_ms()),
        "{}",
        sim.elapsed_ms()
    );
    expect_disconnect(
        &sim.take_events(c),
        DisconnectReason::ConnectionFailed,
        None,
    );
    assert_eq!(sim.nodes[s].events.len(), 1);
}

#[test]
fn a_pending_request_is_seen_while_resent_and_goes_quiet_when_the_client_gives_up() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(
        addr(2000),
        Config {
            max_connect_attempts: 3,
            reconnect_delay_ms: 200,
            ..Config::default()
        },
    );
    let start = sim.now();
    let on_c = sim.connect(c, addr(1000), b"");
    sim.run_ms(500);
    let Event::ConnectionRequest { request, .. } = sim.take_events(s)[0] else {
        panic!()
    };
    let seen = sim.nodes[s].ep.request_last_seen(request).unwrap();
    assert!(seen.duration_since(start).as_millis() >= 390, "resends move it on");
    sim.run_until(5000, |sim| {
        sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Disconnected
    });
    sim.run_ms(2000);
    // The request is still pending (LiteNetLib never expires one), but nobody is asking any more.
    let last = sim.nodes[s].ep.request_last_seen(request).unwrap();
    assert!(last.duration_since(start).as_millis() <= 700, "{last:?}");
    assert!(sim.now().duration_since(last).as_millis() >= 2000);
}

#[test]
fn server_restart_gives_peer_not_found() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    // The server process restarts: a fresh endpoint on the same address.
    sim.nodes[s].ep = Endpoint::with_clock(Config::default(), sim.now(), TICKS_2026);
    sim.send(c, on_c, b"x", 0, DeliveryMethod::ReliableOrdered);
    sim.run_until(3000, |sim| {
        sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Disconnected
    });
    expect_disconnect(&sim.take_events(c), DisconnectReason::PeerNotFound, None);
}

#[test]
fn peer_address_change_keeps_the_session() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(
        addr(1000),
        Config {
            allow_peer_address_change: true,
            ..Config::default()
        },
    );
    let c = sim.add(addr(2000), Config::default());
    let (on_c, on_s) = connected_pair(&mut sim, s, c, b"");
    sim.take_events(s);
    // NAT rebinding: the client's source address changes.
    sim.nodes[c].addr = addr(2001);
    sim.send(c, on_c, b"after-move", 0, DeliveryMethod::ReliableOrdered);
    sim.run_until(3000, |sim| {
        sim.nodes[s]
            .events
            .iter()
            .any(|e| matches!(e, Event::Receive { .. }))
    });
    let evs = sim.take_events(s);
    assert!(
        evs.iter().any(|e| matches!(e, Event::PeerAddressChanged { peer, previous, addr: a } if *peer == on_s && *previous == addr(2000) && *a == addr(2001))),
        "{evs:?}"
    );
    assert_eq!(sim.nodes[s].ep.peer(on_s).unwrap().addr, addr(2001));
    assert_eq!(
        sim.nodes[s].ep.peer(on_s).unwrap().state,
        ConnectionState::Connected
    );
    assert_eq!(sim.nodes[s].ep.peer_by_addr(&addr(2001)), Some(on_s));
    assert!(sim.nodes[s].ep.peer_by_addr(&addr(2000)).is_none());

    // Without the option the moved client is told PeerNotFound and disconnects.
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    sim.nodes[c].addr = addr(2001);
    sim.send(c, on_c, b"after-move", 0, DeliveryMethod::ReliableOrdered);
    sim.run_until(3000, |sim| {
        sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Disconnected
    });
    expect_disconnect(&sim.take_events(c), DisconnectReason::PeerNotFound, None);
}

#[test]
fn reconnect_from_the_same_address_bumps_the_connection_number() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), Config::default());
    let c = sim.add(addr(2000), Config::default());
    let (_, on_s) = connected_pair(&mut sim, s, c, b"first");
    sim.take_events(s);
    sim.run_ms(100);
    // The client restarts (new endpoint, same address, later wall clock) and connects again.
    sim.nodes[c].ep = Endpoint::with_clock(Config::default(), sim.now(), TICKS_2026 + 50_000_000);
    let (on_c2, on_s2) = connected_pair_with_events(&mut sim, s, c);
    let evs_s = sim.take_events(s);
    let _ = evs_s;
    assert_ne!(on_s, on_s2);
    assert_eq!(sim.nodes[c].ep.peer(on_c2).unwrap().connection_number, 1);
    assert_eq!(sim.nodes[s].ep.peer(on_s2).unwrap().connection_number, 1);
    // Traffic flows on the new connection number.
    sim.send(c, on_c2, b"again", 0, DeliveryMethod::ReliableOrdered);
    sim.run_until(1000, |sim| {
        sim.nodes[s]
            .events
            .iter()
            .any(|e| matches!(e, Event::Receive { .. }))
    });
}

fn connected_pair_with_events(sim: &mut Sim, s: usize, c: usize) -> (lnl::PeerId, lnl::PeerId) {
    let on_c = sim.connect(c, addr(1000), b"second");
    let mut on_s = None;
    let mut saw_reconnect = false;
    sim.run_until(5000, |sim| {
        while let Some(e) = sim.nodes[s].events.pop_front() {
            match e {
                Event::Disconnected {
                    reason: DisconnectReason::Reconnect,
                    ..
                } => saw_reconnect = true,
                Event::ConnectionRequest { request, data, .. } => {
                    assert_eq!(data, b"second");
                    on_s = sim.with(s, |ep, out, now| ep.accept(now, request, out));
                }
                _ => {}
            }
        }
        on_s.is_some() && sim.nodes[c].ep.peer(on_c).unwrap().state == ConnectionState::Connected
    });
    assert!(saw_reconnect);
    (on_c, on_s.unwrap())
}

#[test]
fn mtu_discovery_reaches_the_largest_mtu() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let cfg = Config {
        mtu_discovery: true,
        ..Config::default()
    };
    let s = sim.add(addr(1000), cfg.clone());
    let c = sim.add(addr(2000), cfg);
    let (on_c, on_s) = connected_pair(&mut sim, s, c, b"");
    assert_eq!(sim.nodes[c].ep.peer(on_c).unwrap().mtu, 1024);
    sim.run_ms(7000);
    assert_eq!(sim.nodes[c].ep.peer(on_c).unwrap().mtu, 1432);
    assert_eq!(sim.nodes[s].ep.peer(on_s).unwrap().mtu, 1432);
    assert_eq!(
        sim.nodes[c]
            .ep
            .max_single_packet_size(on_c, DeliveryMethod::Unreliable),
        Some(1431)
    );
    // Big unreliable datagrams now fit.
    sim.send(c, on_c, &[1u8; 1400], 0, DeliveryMethod::Unreliable);
    sim.run_until(100, |sim| {
        sim.nodes[s]
            .events
            .iter()
            .any(|e| matches!(e, Event::Receive { .. }))
    });

    // A path that drops datagrams above 1200 bytes stops at 1164.
    let mut sim = Sim::new(LinkModel::default(), 1);
    let cfg = Config {
        mtu_discovery: true,
        ..Config::default()
    };
    let s = sim.add(addr(1000), cfg.clone());
    let c = sim.add(addr(2000), cfg);
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    for _ in 0..8000 {
        sim.net.queue.retain(|d| d.data.len() <= 1200);
        sim.step();
    }
    assert_eq!(sim.nodes[c].ep.peer(on_c).unwrap().mtu, 1164);
}

#[test]
fn channel_index_layout() {
    let mut sim = Sim::new(LinkModel::default(), 1);
    let s = sim.add(addr(1000), cfg16());
    let c = sim.add(addr(2000), cfg16());
    let (on_c, _) = connected_pair(&mut sim, s, c, b"");
    sim.run_ms(20);
    let mut ctx = Collect::default();
    for m in [
        DeliveryMethod::ReliableUnordered,
        DeliveryMethod::Sequenced,
        DeliveryMethod::ReliableOrdered,
        DeliveryMethod::ReliableSequenced,
    ] {
        sim.nodes[c].ep.send(on_c, &[0xAB; 600], 15, m).unwrap();
        let now = sim.now();
        sim.nodes[c].ep.poll(now, &mut ctx);
        let (_, d) = ctx
            .transmits
            .iter()
            .find(|(_, d)| d[0] & 0x1F == 1)
            .unwrap();
        assert_eq!(d[3], 15 * 4 + m as u8, "{m:?}");
        ctx.transmits.clear();
    }
    assert_eq!(
        sim.nodes[c]
            .ep
            .send(on_c, b"x", 16, DeliveryMethod::ReliableOrdered),
        Err(lnl::SendError::InvalidChannel)
    );
}
