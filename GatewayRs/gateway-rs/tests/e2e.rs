//! End to end through the real binary: a scripted ClusterRegistry on the relay socket, a node and a
//! Direct client speaking LiteNetLib (the lnl crate). One client joins, is attached to the node,
//! and packets flow both ways with credit; then it disconnects.

use std::mem::MaybeUninit;
use std::net::{SocketAddr, UdpSocket as StdUdp};
use std::process::{Child, Command};
use std::time::{Duration, Instant};

use gateway_rs::ipc::{decode, encode, types};
use gateway_rs::nodelink::{self, Envelope};
use lnl::udp::UdpEndpoint;
use lnl::{DeliveryMethod, Event, PeerId};
use serde_json::{Value, json};
use socket2::{Domain, SockAddr, Socket, Type};

/// The gateway-rs process and its socket directory, both gone at the end of the test.
struct Kill(Child, std::path::PathBuf);
impl Drop for Kill {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
        let _ = std::fs::remove_dir_all(&self.1);
    }
}

fn lnl_config() -> lnl::Config {
    lnl::Config { channels_count: 16, disconnect_timeout_ms: 5000, ..lnl::Config::default() }
}

fn free_port() -> u16 {
    StdUdp::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

struct Registry {
    peer: Socket,
}

impl Registry {
    fn send(&self, ty: u16, correlation: u32, body: &Value) {
        self.peer.send(&encode(ty, correlation, &serde_json::to_vec(body).unwrap())).unwrap();
    }

    /// The next message of type `ty` (others are skipped), with its correlation. The LiteNetLib
    /// endpoints are pumped meanwhile: their sends only leave on a poll.
    fn expect(&self, ty: u16, pump: &mut [&mut UdpEndpoint]) -> (u32, Vec<u8>) {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut buffer = vec![MaybeUninit::<u8>::uninit(); 300 * 1024];
        let mut reassembler = gateway_rs::ipc::Reassembler::default();
        while Instant::now() < deadline {
            for endpoint in pump.iter_mut() {
                endpoint.pump().unwrap();
            }
            let Ok(read) = self.peer.recv(&mut buffer) else { continue };
            assert!(read > 0, "gateway-rs closed the relay socket");
            let mut frame: Vec<u8> = buffer[..read].iter().map(|b| unsafe { b.assume_init() }).collect();
            if decode(&frame).unwrap().0 == types::FRAGMENT {
                let Some(complete) = reassembler.push(decode(&frame).unwrap().2) else { continue };
                frame = complete;
            }
            let (got, correlation, body) = decode(&frame).unwrap();
            if got == ty {
                return (correlation, body.to_vec());
            }
        }
        panic!("no IPC message of type {ty}");
    }

    fn expect_json(&self, ty: u16, pump: &mut [&mut UdpEndpoint]) -> (u32, Value) {
        let (correlation, body) = self.expect(ty, pump);
        (correlation, serde_json::from_slice(&body).unwrap())
    }
}

/// Waits for an event on `endpoint` while keeping `others` pumped (sends leave only on a poll).
fn wait_event(endpoint: &mut UdpEndpoint, others: &mut [&mut UdpEndpoint], what: &str, mut pick: impl FnMut(&Event) -> bool) -> Event {
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        for other in others.iter_mut() {
            other.pump().unwrap();
        }
        if let Some(event) = endpoint.wait_event(Duration::from_millis(5)).unwrap()
            && pick(&event)
        {
            return event;
        }
    }
    panic!("timed out waiting for {what}");
}

fn game_frame(message_id: u8, tail: &[u8]) -> Vec<u8> {
    let mut frame = vec![0xCE, 1, 0, 0, 0, 0, 0, 1, message_id, 0];
    frame.extend_from_slice(tail);
    let crc = relay::frame::crc32(&frame[6..]);
    frame[2..6].copy_from_slice(&crc.to_le_bytes());
    frame
}

/// gateway-rs connected to a scripted registry that has said hello and sent the Config.
fn spawn_gateway(test: &str, join_hold_seconds: u32) -> (Kill, Registry, u16) {
    let dir = std::env::temp_dir().join(format!("gateway-rs-{test}-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("gateway-relay.sock");
    let _ = std::fs::remove_file(&path);
    let listener = Socket::new(Domain::UNIX, Type::SEQPACKET, None).unwrap();
    listener.bind(&SockAddr::unix(&path).unwrap()).unwrap();
    listener.listen(1).unwrap();

    let binary = env!("CARGO_BIN_EXE_gateway-rs");
    let child = Kill(Command::new(binary).args(["--socket", path.to_str().unwrap(), "--workers", "2"]).spawn().unwrap(), dir.clone());
    let (peer, _) = listener.accept().unwrap();
    peer.set_read_timeout(Some(Duration::from_millis(5))).unwrap();
    let beat = peer.try_clone().unwrap();
    std::thread::spawn(move || {
        while beat.send(&encode(types::HEARTBEAT, 0, b"{\"seq\":0}")).is_ok() {
            std::thread::sleep(Duration::from_millis(500));
        }
    });
    let registry = Registry { peer };
    let (_, hello) = registry.expect_json(types::HELLO, &mut []);
    assert_eq!(hello["role"], "relay");
    registry.send(types::HELLO, 0, &json!({"version": gateway_rs::ipc::VERSION, "role": "registry", "build": "e2e"}));

    let port = free_port();
    registry.send(
        types::CONFIG,
        0,
        &json!({
            "direct": {"enabled": true, "bind": "127.0.0.1", "port": port, "dualStack": false},
            "steam": {"enabled": false},
            "timeouts": {"clientDisconnectMs": 5000, "nodeDisconnectMs": 5000},
            "joinHoldSeconds": join_hold_seconds, "joinToken": "tok", "acceptAll": true, "nodeLinkChannels": 16
        }),
    );
    // T-0408: CONFIG starts the front workers, which bind the port one after the other (SO_REUSEPORT). A client
    // that connects before the last bind can have its first ConnectRequest queued on one worker's socket and its
    // retries hashed to another's, which takes a retry for a new join with a second session (a_held_join_is_dropped
    // read session 2 for 1 under load). The control loop runs CONFIG to the end before it reads the next message,
    // so one STATUS round trip waits for every bind.
    registry.send(types::STATUS, 1, &json!({}));
    let (_, status) = registry.expect_json(types::STATUS, &mut []);
    assert_eq!(status["workers"], 2, "the front workers did not start: {status}");
    (child, registry, port)
}

fn client_handshake(client_id: u64, name: &str) -> Vec<u8> {
    let mut handshake = Vec::new();
    nodelink::put_string(&mut handshake, nodelink::PROTOCOL_KEY);
    handshake.extend_from_slice(&client_id.to_le_bytes());
    nodelink::put_string(&mut handshake, name);
    handshake
}

#[test]
fn a_direct_client_joins_attaches_relays_and_leaves() {
    let (_child, registry, port) = spawn_gateway("e2e", 5);
    let mut node = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
    let node_addr = node.local_addr().unwrap().to_string();

    // Node link: the handshake carries the join token; the registry hears the link come up.
    registry.send(types::ENSURE_LINK, 0, &json!({"endpoint": node_addr}));
    let Event::ConnectionRequest { request, data, .. } = wait_event(&mut node, &mut [], "node-link request", |e| matches!(e, Event::ConnectionRequest { .. })) else {
        unreachable!()
    };
    assert_eq!(data, nodelink::node_link_handshake("tok"));
    let now = Instant::now();
    let gateway_peer = node.with(|endpoint, out| endpoint.accept(now, request, out)).unwrap();
    let (_, link) = registry.expect_json(types::LINK_STATE, &mut [&mut node]);
    assert_eq!((link["endpoint"].as_str(), link["connected"].as_bool()), (Some(node_addr.as_str()), Some(true)));

    // Join: the registry is asked and accepts.
    let mut client = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
    let handshake = client_handshake(1234, "pilot");
    let gateway: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    // LiteNetLib resends the request every 500 ms, which also covers workers still binding.
    let now = Instant::now();
    client.with(|endpoint, out| endpoint.connect(now, gateway, &handshake, out)).unwrap();
    let (correlation, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client, &mut node]);
    assert_eq!(join["clientId"], 1234);
    assert_eq!(join["name"], "pilot");
    let session = join["session"].as_u64().unwrap();
    registry.send(types::JOIN_ANSWER, correlation, &json!({"decision": "accept"}));
    let client_peer: PeerId = match wait_event(&mut client, &mut [&mut node], "client connected", |e| matches!(e, Event::Connected { .. })) {
        Event::Connected { peer, .. } => peer,
        _ => unreachable!(),
    };

    // Routed but not yet attached: the client's CLIENT_CONNECTED is reported and held.
    let update = |attached: bool| {
        json!({"clientId": 1234, "session": session, "link": node_addr, "attached": attached, "routable": true,
               "switching": false, "bufferServer": false, "suppressJoin": false, "validationLink": null})
    };
    registry.send(types::SESSION_UPDATE, 0, &update(false));
    registry.send(types::NODE_FRAME, 0, &json!({"endpoint": node_addr, "kind": "attach", "clientId": 1234, "body": "e30="}));
    let connect = game_frame(14, b"hello");
    let wire: Vec<u8> = [&[2u8][..], &connect].concat();
    client.with(|endpoint, _| endpoint.send(client_peer, &wire, 0, DeliveryMethod::ReliableOrdered)).unwrap();
    let (_, message) = registry.expect_json(types::CLIENT_MESSAGE, &mut [&mut client, &mut node]);
    assert_eq!(message["messageId"], 14);

    let attach = wait_event(&mut node, &mut [&mut client], "attach frame", |e| matches!(e, Event::Receive { .. }));
    let Event::Receive { channel, packet, .. } = attach else { unreachable!() };
    assert_eq!(channel, (1234 % 16) as u8);
    assert_eq!(nodelink::decode(packet.data()).unwrap(), Envelope::Attach { client_id: 1234, body: b"{}" });

    // The node acknowledges; the registry marks the link attached and the backlog drains.
    let ack = nodelink::encode(&Envelope::AttachAck { client_id: 1234 }).unwrap();
    node.with(|endpoint, _| endpoint.send(gateway_peer, &ack, 2, DeliveryMethod::ReliableOrdered)).unwrap();
    let (_, control) = registry.expect_json(types::NODE_CONTROL, &mut [&mut client, &mut node]);
    assert_eq!((control["kind"].as_str(), control["session"].as_u64()), (Some("attachAck"), Some(session)));
    registry.send(types::SESSION_UPDATE, 0, &update(true));
    let relayed = wait_event(&mut node, &mut [&mut client], "relayed CLIENT_CONNECTED", |e| matches!(e, Event::Receive { .. }));
    let Event::Receive { packet, .. } = relayed else { unreachable!() };
    assert_eq!(
        nodelink::decode(packet.data()).unwrap(),
        Envelope::Relay { client_id: 1234, channel: 2, payload: &connect }
    );

    // Node to client, and the node is paid its credit.
    let reply = game_frame(21, b"world");
    let frame = nodelink::encode(&Envelope::Relay { client_id: 1234, channel: 2, payload: &reply }).unwrap();
    node.with(|endpoint, _| endpoint.send(gateway_peer, &frame, 2, DeliveryMethod::ReliableOrdered)).unwrap();
    let received = wait_event(&mut client, &mut [&mut node], "relayed reply", |e| matches!(e, Event::Receive { .. }));
    let Event::Receive { packet, .. } = received else { unreachable!() };
    assert_eq!(packet.data(), [&[2u8][..], &reply].concat());
    let credit = wait_event(&mut node, &mut [&mut client], "credit", |e| matches!(e, Event::Receive { .. }));
    let Event::Receive { packet, .. } = credit else { unreachable!() };
    assert_eq!(nodelink::decode(packet.data()).unwrap(), Envelope::Credit { client_id: 1234, credit: reply.len() as i32 + 10 });

    // Global frames tunnel to the registry with their delivery and source.
    let global = nodelink::encode(&Envelope::Global { payload: b"{\"g\":1}" }).unwrap();
    node.with(|endpoint, _| endpoint.send(gateway_peer, &global, 0, DeliveryMethod::ReliableUnordered)).unwrap();
    let (_, tunnel) = registry.expect(types::NODE_GLOBAL, &mut [&mut client, &mut node]);
    assert_eq!(tunnel[0], DeliveryMethod::ReliableUnordered as u8);
    assert!(tunnel.ends_with(b"{\"g\":1}"));
    // A full global-state snapshot on a live world is several MB (2026-09-24: 4136274 bytes); IPC
    // version 1 dropped every frame above 256 KiB, and wa-1 re-sent it until it ran out of memory.
    let snapshot: Vec<u8> = (0..4_136_274u32).map(|i| (i % 253) as u8).collect();
    let big = nodelink::encode(&Envelope::Global { payload: &snapshot }).unwrap();
    node.with(|endpoint, _| endpoint.send(gateway_peer, &big, 0, DeliveryMethod::ReliableOrdered)).unwrap();
    let (_, tunnel) = registry.expect(types::NODE_GLOBAL, &mut [&mut client, &mut node]);
    assert_eq!(tunnel[0], DeliveryMethod::ReliableOrdered as u8);
    assert!(tunnel.ends_with(&snapshot));

    registry.send(types::STATUS, 99, &json!({}));
    let (correlation, status) = registry.expect_json(types::STATUS, &mut [&mut client, &mut node]);
    assert_eq!(correlation, 99);
    assert_eq!(status["sessions"][0]["clientId"], 1234);
    assert_eq!(status["sessions"][0]["attached"], true);

    // Leaving is reported.
    client.with(|endpoint, out| endpoint.disconnect(client_peer, &[], out));
    client.service(Duration::from_millis(50)).unwrap();
    let (_, event) = registry.expect_json(types::CLIENT_EVENT, &mut [&mut client, &mut node]);
    assert_eq!((event["event"].as_str(), event["session"].as_u64()), (Some("disconnected"), Some(session)));
}

/// A held join is re-asked every second while its client keeps resending the request, and dropped
/// once the client gives up: accepting it minutes later made a session for nobody (P3-PLR-02,
/// 2026-09-24: "Accepted client" then "disconnected: PeerNotFound" 2.5 min after the client left).
#[test]
fn a_held_join_is_dropped_when_its_client_gives_up() {
    let (_child, registry, port) = spawn_gateway("e2e-hold", 300);
    let gateway: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    // Gives up after 1 + 6 attempts 500 ms apart, ~3.5 s.
    let config = lnl::Config { max_connect_attempts: 6, ..lnl_config() };
    let mut client = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), config).unwrap();
    let now = Instant::now();
    let on_client = client.with(|endpoint, out| endpoint.connect(now, gateway, &client_handshake(77, "held"), out)).unwrap();

    let (correlation, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    let session = join["session"].as_u64().unwrap();
    registry.send(types::JOIN_ANSWER, correlation, &json!({"decision": "hold", "reason": "busy"}));
    // Re-asked (same session) while the client is still trying.
    let (again, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    assert_eq!(join["session"].as_u64(), Some(session));
    registry.send(types::JOIN_ANSWER, again, &json!({"decision": "hold", "reason": "busy"}));

    wait_event(&mut client, &mut [], "the client giving up", |e| matches!(e, Event::Disconnected { peer, .. } if *peer == on_client));
    let gave_up = Instant::now();
    // Keep answering "hold"; the re-asks must stop within ABANDONED_REQUEST (5 s) plus a re-ask.
    let mut last_ask = gave_up;
    let mut buffer = vec![MaybeUninit::<u8>::uninit(); 64 * 1024];
    while gave_up.elapsed() < Duration::from_secs(9) {
        let Ok(read) = registry.peer.recv(&mut buffer) else { continue };
        let frame: Vec<u8> = buffer[..read].iter().map(|b| unsafe { b.assume_init() }).collect();
        let (ty, correlation, _) = decode(&frame).unwrap();
        if ty == types::JOIN_REQUEST {
            last_ask = Instant::now();
            registry.send(types::JOIN_ANSWER, correlation, &json!({"decision": "hold", "reason": "busy"}));
        }
    }
    let quiet_after = last_ask.duration_since(gave_up);
    assert!(quiet_after < Duration::from_millis(6500), "still re-asked {quiet_after:?} after the client gave up");
    assert!(last_ask > gave_up, "no re-ask after the client gave up at all: the red arm needs one");
}

fn accept() -> Value {
    json!({"decision": "accept"})
}

fn hold() -> Value {
    json!({"decision": "hold", "reason": "busy"})
}

/// T-0314: a held join is re-asked every second whether or not C# answered the last ask. C# runs
/// joins one at a time (_relayAdmissionLock), so when it is backlogged it ACCEPTS an older ask
/// (creating the session) and then refuses the newer one as a second join of the same identity.
/// gateway-rs used to drop the accept (its correlation had been replaced) and reject the client on
/// the refusal: C# kept a session nobody backed and refused that player until a gateway restart.
#[test]
fn a_late_accept_of_a_superseded_ask_admits_the_client() {
    let (_child, registry, port) = spawn_gateway("e2e-late-accept", 300);
    let gateway: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    let mut client = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
    let now = Instant::now();
    client.with(|endpoint, out| endpoint.connect(now, gateway, &client_handshake(88, "late"), out)).unwrap();

    let (first, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    let session = join["session"].as_u64().unwrap();
    registry.send(types::JOIN_ANSWER, first, &hold());
    // The re-ask C# is slow to answer, and the one that supersedes it a second later.
    let (late, _) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    let (newer, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    assert_eq!(join["session"].as_u64(), Some(session), "a re-ask keeps the relay session");
    assert_ne!(late, newer, "the red arm needs the correlation replaced");
    registry.send(types::JOIN_ANSWER, late, &accept());
    registry.send(types::JOIN_ANSWER, newer, &json!({"decision": "reject"}));

    wait_event(&mut client, &mut [], "client connected on the late accept", |e| matches!(e, Event::Connected { .. }));
    registry.send(types::STATUS, 7, &json!({}));
    let (_, status) = registry.expect_json(types::STATUS, &mut [&mut client]);
    assert_eq!(status["sessions"][0]["session"].as_u64(), Some(session), "gateway-rs holds the session C# made");
}

/// T-0314: an accept that arrives after gateway-rs already refused the join (hold limit, abandoned,
/// 15 s unanswered) made a C# session for a client gateway-rs had rejected. It is released with
/// `disconnected`, as an accept for a request the client gave up is.
#[test]
fn a_late_accept_after_the_join_was_refused_releases_the_registry_session() {
    let (_child, registry, port) = spawn_gateway("e2e-refused-accept", 2);
    let gateway: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    let mut client = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
    let now = Instant::now();
    client.with(|endpoint, out| endpoint.connect(now, gateway, &client_handshake(89, "refused"), out)).unwrap();

    let (first, join) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    let session = join["session"].as_u64().unwrap();
    registry.send(types::JOIN_ANSWER, first, &hold());
    // The re-ask goes unanswered until the 2 s hold limit refuses the join.
    let (late, _) = registry.expect_json(types::JOIN_REQUEST, &mut [&mut client]);
    wait_event(&mut client, &mut [], "the join refused", |e| matches!(e, Event::Disconnected { .. }));
    registry.send(types::JOIN_ANSWER, late, &accept());

    let (_, event) = registry.expect_json(types::CLIENT_EVENT, &mut []);
    assert_eq!((event["event"].as_str(), event["clientId"].as_u64(), event["session"].as_u64()), (Some("disconnected"), Some(89), Some(session)));
}
