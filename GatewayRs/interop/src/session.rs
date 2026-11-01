//! The Rust side of an interop run: an `lnl` endpoint over `lnl::udp`, as client or server,
//! running the shared script. Mirrors `Session` in interop/dotnet/Program.cs.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use lnl::udp::UdpEndpoint;
use lnl::wire::{Reader, Writer};
use lnl::{Config, ConnectionState, DeliveryMethod, DisconnectReason, Event, PeerId, RequestId};

use crate::script::{self, Verifier};

pub struct SessionOptions {
    pub server: bool,
    pub endpoint: SocketAddr,
    pub end: String,
    pub timeout_ms: u32,
    pub mtu_discovery: bool,
    pub expect_mtu: usize,
    pub min_rtt: i32,
    pub max_rtt: i32,
    pub hold_ms: u64,
    pub reject: bool,
    pub expect_reject: bool,
    pub per: u32,
    pub big: bool,
    pub deadline_ms: u64,
    pub expect_unreliable: i64,
    pub allow_address_change: bool,
    pub expect_address_change: bool,
    /// How long "close" waits after its own completion before disconnecting: the peer's completion also
    /// needs a ping round trip, which heavy loss can take several ping intervals to get through.
    pub grace_ms: u64,
}

struct Run {
    o: SessionOptions,
    start: Instant,
}

impl Run {
    fn role(&self) -> &'static str {
        if self.o.server { "server" } else { "client" }
    }

    fn other(&self) -> &'static str {
        if self.o.server { "client" } else { "server" }
    }

    fn ms(&self) -> u64 {
        self.start.elapsed().as_millis() as u64
    }

    fn log(&self, s: &str) {
        println!("[rs {} {:6}ms] {s}", self.role(), self.ms());
    }

    fn pass(&self, s: &str) -> i32 {
        println!("PASS rs {}: {s}", self.role());
        0
    }

    fn fail(&self, s: &str) -> i32 {
        println!("FAIL rs {}: {s}", self.role());
        1
    }
}

fn check_handshake(data: &[u8]) -> Result<(), String> {
    let mut r = Reader::new(data);
    let key = r.get_string().ok_or("no key")?;
    let id = r.get_u64().ok_or("no id")?;
    let name = r.get_string().ok_or("no name")?;
    if key != script::KEY || id != script::CLIENT_ID || name != "interop" {
        return Err(format!("bad handshake data {key}/{id}/{name}"));
    }
    Ok(())
}

pub fn run(o: SessionOptions) -> i32 {
    let run = Run {
        o,
        start: Instant::now(),
    };
    match session(&run) {
        Ok(code) => code,
        Err(e) => run.fail(&e),
    }
}

fn session(r: &Run) -> Result<i32, String> {
    let o = &r.o;
    let config = Config {
        channels_count: script::CHANNELS,
        disconnect_timeout_ms: o.timeout_ms,
        reconnect_delay_ms: 500,
        max_connect_attempts: 20,
        mtu_discovery: o.mtu_discovery,
        allow_peer_address_change: o.allow_address_change,
        ..Config::default()
    };
    let bind = if o.server {
        o.endpoint
    } else {
        SocketAddr::new(o.endpoint.ip(), 0)
    };
    let mut udp = UdpEndpoint::bind(bind, config).map_err(|e| format!("bind {bind}: {e}"))?;
    r.log(&format!(
        "started on {}",
        udp.local_addr().map_err(|e| e.to_string())?
    ));
    let plan = script::plan(o.per, o.big);
    let messages = script::messages(&plan);
    let mut verifier = Verifier::new(&plan);

    let mut peer: Option<PeerId> = None;
    if !o.server {
        let mut w = Writer::new();
        w.put_string(script::KEY)
            .put_u64(script::CLIENT_ID)
            .put_string("interop");
        let target = o.endpoint;
        peer = Some(
            udp.with(|ep, out| ep.connect(Instant::now(), target, &w.buf, out))
                .map_err(|e| e.to_string())?,
        );
    }
    let mut connected = false;
    let mut held: Option<(RequestId, u64)> = None;
    let mut rejected_at: Option<u64> = None;
    let mut disconnect: Option<(DisconnectReason, String, u64)> = None;
    let (mut sent_verified, mut got_verified, mut got_verified_ack) = (false, false, false);
    let mut latency_updates = 0u32;
    let mut address_changes = 0u32;
    let mut done_at: Option<u64> = None;
    let mut closed = false;

    while r.ms() < o.deadline_ms {
        udp.service(Duration::from_millis(1))
            .map_err(|e| e.to_string())?;
        while let Some(ev) = udp.next_event() {
            match ev {
                Event::ConnectionRequest {
                    request,
                    addr,
                    data,
                } => {
                    check_handshake(&data)?;
                    r.log(&format!("connection request from {addr}"));
                    if o.reject {
                        let now = Instant::now();
                        udp.with(|ep, out| ep.reject(now, request, b"go-away", out));
                        rejected_at = Some(r.ms());
                    } else if o.hold_ms > 0 {
                        held = Some((request, r.ms()));
                    } else {
                        peer = udp.with(|ep, out| ep.accept(Instant::now(), request, out));
                    }
                }
                Event::Connected { peer: p, addr } => {
                    let info = udp.endpoint().peer(p).unwrap();
                    r.log(&format!(
                        "connected {addr} id={} remoteId={}",
                        info.id, info.remote_id
                    ));
                    peer = Some(p);
                    connected = true;
                    for (data, c, m) in &messages {
                        udp.with(|ep, _| ep.send(p, data, *c, *m))
                            .map_err(|e| format!("send: {e}"))?;
                    }
                }
                Event::Disconnected { reason, data, .. } => {
                    let text = data
                        .as_ref()
                        .map(|d| String::from_utf8_lossy(d.data()).into_owned())
                        .unwrap_or_default();
                    r.log(&format!("disconnected reason={reason:?} data='{text}'"));
                    disconnect = Some((reason, text, r.ms()));
                    if let Some(d) = data {
                        udp.recycle(d);
                    }
                }
                Event::LatencyUpdate { .. } => latency_updates += 1,
                Event::Receive {
                    peer: p,
                    channel,
                    method,
                    packet,
                } => {
                    let data = packet.data();
                    if data == [script::VERIFIED] {
                        got_verified = true;
                        udp.with(|ep, _| {
                            ep.send(
                                p,
                                &[script::VERIFIED_ACK],
                                0,
                                DeliveryMethod::ReliableOrdered,
                            )
                        })
                        .map_err(|e| e.to_string())?;
                    } else if data == [script::VERIFIED_ACK] {
                        got_verified_ack = true;
                    } else {
                        verifier.on_receive(channel, method, data)?;
                    }
                    udp.recycle(packet);
                }
                Event::PeerAddressChanged { previous, addr, .. } => {
                    r.log(&format!("peer address changed {previous} -> {addr}"));
                    address_changes += 1;
                }
            }
        }
        let now = r.ms();
        if let Some((req, at)) = held
            && now - at >= o.hold_ms
        {
            r.log(&format!("accepting held request after {} ms", now - at));
            peer = udp.with(|ep, out| ep.accept(Instant::now(), req, out));
            held = None;
        }
        if o.reject && o.server {
            if connected {
                return Ok(r.fail("a rejected client connected"));
            }
            if let Some(at) = rejected_at {
                let lingering = udp
                    .endpoint()
                    .peers()
                    .filter(|p| {
                        udp.endpoint().peer(*p).unwrap().state == ConnectionState::ShutdownRequested
                    })
                    .count();
                if now - at >= 500 && lingering == 0 {
                    return Ok(r.pass("rejected the client; it answered ShutdownOk"));
                }
                if now - at > 10_000 {
                    return Ok(r.fail("no ShutdownOk for the reject"));
                }
            }
            continue;
        }
        if let Some((reason, data, at)) = disconnect.clone()
            && !closed
        {
            if o.expect_reject {
                return Ok(
                    if reason == DisconnectReason::ConnectionRejected && data == "go-away" {
                        r.pass("rejected with data 'go-away'")
                    } else {
                        r.fail(&format!(
                            "expected ConnectionRejected/go-away, got {reason:?}/'{data}'"
                        ))
                    },
                );
            }
            let Some(done) = done_at else {
                return Ok(r.fail(&format!(
                    "disconnected before the script completed: {reason:?} (received {}, verified={got_verified}, ack={got_verified_ack})",
                    verifier.received
                )));
            };
            let expected_data = format!("bye-{}", r.other());
            if o.end == "await-close" {
                // Keep answering the remote's resent Disconnects with ShutdownOk for a while.
                let until = Instant::now() + Duration::from_millis(3000);
                while Instant::now() < until {
                    udp.service(Duration::from_millis(1))
                        .map_err(|e| e.to_string())?;
                    while let Some(ev) = udp.next_event() {
                        if let Event::Receive { packet, .. } = ev {
                            udp.recycle(packet);
                        }
                    }
                }
            }
            return Ok(match o.end.as_str() {
                "await-close"
                    if reason == DisconnectReason::RemoteConnectionClose
                        && data == expected_data =>
                {
                    r.pass(&format!("remote close with data '{data}'"))
                }
                "await-close" => r.fail(&format!(
                    "expected RemoteConnectionClose/{expected_data}, got {reason:?}/'{data}'"
                )),
                "await-timeout" if reason == DisconnectReason::Timeout => r.pass(&format!(
                    "timeout detected {} ms after completion",
                    at - done
                )),
                "await-timeout" => r.fail(&format!("expected Timeout, got {reason:?}")),
                _ => r.fail(&format!("unexpected disconnect {reason:?}")),
            });
        }
        if let Some(p) = peer.filter(|_| connected)
            && done_at.is_none()
        {
            if verifier.complete() && !sent_verified {
                sent_verified = true;
                r.log(&format!(
                    "script complete: {} messages: {}",
                    verifier.received,
                    verifier.summary()
                ));
                udp.with(|ep, _| {
                    ep.send(p, &[script::VERIFIED], 0, DeliveryMethod::ReliableOrdered)
                })
                .map_err(|e| e.to_string())?;
            }
            let info = udp.endpoint().peer(p).unwrap();
            let mtu_ok = o.expect_mtu == 0 || info.mtu == o.expect_mtu;
            let move_ok = !o.expect_address_change || address_changes > 0;
            if sent_verified
                && got_verified
                && got_verified_ack
                && latency_updates > 0
                && mtu_ok
                && move_ok
            {
                done_at = Some(now);
                r.log(&format!(
                    "done: rtt={} ms mtu={} latencyUpdates={latency_updates} {}",
                    info.rtt_ms,
                    info.mtu,
                    verifier.summary()
                ));
                if o.min_rtt >= 0 && info.rtt_ms < o.min_rtt {
                    return Ok(r.fail(&format!("rtt {} < {}", info.rtt_ms, o.min_rtt)));
                }
                if o.max_rtt >= 0 && info.rtt_ms > o.max_rtt {
                    return Ok(r.fail(&format!("rtt {} > {}", info.rtt_ms, o.max_rtt)));
                }
                if o.expect_unreliable >= 0 && verifier.unreliable as i64 != o.expect_unreliable {
                    return Ok(r.fail(&format!(
                        "unreliable {} != {}",
                        verifier.unreliable, o.expect_unreliable
                    )));
                }
            }
        }
        if let Some(done) = done_at
            && !closed
            && now - done >= o.grace_ms
        {
            let p = peer.unwrap();
            match o.end.as_str() {
                "close" => {
                    r.log("disconnecting");
                    let data = format!("bye-{}", r.role());
                    udp.with(|ep, out| ep.disconnect(p, data.as_bytes(), out));
                    closed = true;
                }
                "die" => {
                    println!(
                        "PASS rs {}: script complete, exiting without disconnect",
                        r.role()
                    );
                    std::process::exit(0);
                }
                _ => {}
            }
        }
        if closed {
            let done = done_at.unwrap();
            if udp.endpoint().peer(peer.unwrap()).unwrap().state == ConnectionState::Disconnected {
                let own = matches!(
                    disconnect,
                    Some((DisconnectReason::DisconnectPeerCalled, _, _))
                );
                // Disconnected before the shutdown timeout means a ShutdownOk ended it. Under loss the one
                // ShutdownOk can be dropped, and C# LiteNetLib answers a repeated Disconnect only once it has
                // forgotten the peer (ProcessDisconnect returns None for a peer it already disconnected; it is
                // removed after DisconnectTimeout), so the shutdown then ends on our own timeout instead. The
                // remote's log still has to show our close data, which is the part that is under test.
                let how = if now - done - o.grace_ms < o.timeout_ms as u64 {
                    "remote answered ShutdownOk"
                } else {
                    "shutdown timed out (the ShutdownOk was lost)"
                };
                return Ok(if own {
                    r.pass(&format!("closed; {how}"))
                } else {
                    r.fail(&format!("own disconnect event missing: {disconnect:?}"))
                });
            }
            if now - done > o.grace_ms + 5000.max(o.timeout_ms as u64 + 2000) {
                return Ok(r.fail("no ShutdownOk after disconnect"));
            }
        }
    }
    let state = peer.and_then(|p| udp.endpoint().peer(p));
    Ok(r.fail(&format!(
        "deadline: received {} complete={} ({}) verified={got_verified} ack={got_verified_ack} latency={latency_updates} peer={state:?}",
        verifier.received,
        verifier.complete(),
        verifier.summary()
    )))
}
