//! The Steam frontend against a fake Steam and a stand-in ClusterRegistry on a real relay socket.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::Value;
use socket2::{Domain, SockAddr, Socket, Type};

use super::*;
use crate::ipc::{self, SteamPlayer};

#[derive(Default)]
struct State {
    callbacks: Vec<SteamCallback>,
    incoming: VecDeque<(u64, u8, Vec<u8>)>,
    sent: Vec<(u64, u8, bool, Vec<u8>)>,
    accepted: Vec<u64>,
    /// Users whose P2P session is accepted and not closed: Steam delivers only their packets.
    open: HashSet<u64>,
    closed: Vec<u64>,
    auth_begun: Vec<(u64, Vec<u8>)>,
    auth_ended: Vec<u64>,
    groups: Vec<(u64, u64)>,
    nameable: HashSet<u64>,
    named: Vec<(u64, String)>,
    bots: Vec<i32>,
    queued: usize,
}

#[derive(Clone, Default)]
struct Fake(Arc<Mutex<State>>);

impl Fake {
    fn with<R>(&self, f: impl FnOnce(&mut State) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }
}

impl SteamApi for Fake {
    fn callbacks(&mut self, out: &mut Vec<SteamCallback>) {
        out.append(&mut self.0.lock().unwrap().callbacks);
    }
    fn read(&mut self, channel: u8, buffer: &mut Vec<u8>) -> Option<(u64, usize)> {
        let mut state = self.0.lock().unwrap();
        // Steam keeps what a user sent until the session is accepted (AcceptP2PSessionWithUser).
        let index = state.incoming.iter().position(|(user, c, _)| *c == channel && state.open.contains(user))?;
        let (user, _, data) = state.incoming.remove(index).unwrap();
        if buffer.len() < data.len() {
            buffer.resize(data.len(), 0);
        }
        buffer[..data.len()].copy_from_slice(&data);
        Some((user, data.len()))
    }
    fn send(&mut self, user: u64, channel: u8, reliable: bool, data: &[u8]) -> bool {
        self.with(|s| s.sent.push((user, channel, reliable, data.to_vec())));
        true
    }
    fn accept(&mut self, user: u64) {
        self.with(|s| {
            s.accepted.push(user);
            s.open.insert(user);
        });
    }
    fn close(&mut self, user: u64) {
        self.with(|s| {
            s.closed.push(user);
            s.open.remove(&user);
        });
    }
    fn queued_packets(&self, _user: u64) -> usize {
        self.0.lock().unwrap().queued
    }
    fn begin_auth(&mut self, user: u64, ticket: &[u8]) -> i32 {
        self.with(|s| s.auth_begun.push((user, ticket.to_vec())));
        0
    }
    fn end_auth(&mut self, user: u64) {
        self.with(|s| s.auth_ended.push(user));
    }
    fn request_group_status(&mut self, user: u64, group: u64) {
        self.with(|s| s.groups.push((user, group)));
    }
    fn update_user_data(&mut self, user: u64, name: &str) -> bool {
        self.with(|s| {
            let ok = s.nameable.contains(&user);
            if ok {
                s.named.push((user, name.to_owned()));
            }
            ok
        })
    }
    fn apply_settings(&mut self, _publish: &SteamPublish, bots: i32) {
        self.with(|s| s.bots.push(bots));
    }
    fn identity(&self) -> Identity {
        Identity { server_id: 90_000_000_000_000_001, logged_on: true, secure: true, public_ip: None }
    }
}

/// ClusterRegistry's end of the relay socket.
struct Registry {
    peer: Socket,
    _listener: Socket,
    /// Kept alive: the IPC thread ends its connection when nobody takes its messages.
    _inbound: crossbeam_channel::Receiver<crate::ipc::Inbound>,
    dir: std::path::PathBuf,
}

impl Drop for Registry {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

impl Registry {
    fn recv(&self) -> Option<(u16, u32, Value)> {
        let mut buffer = vec![std::mem::MaybeUninit::<u8>::uninit(); 256 * 1024];
        let read = self.peer.recv(&mut buffer).ok()?;
        let frame: Vec<u8> = buffer[..read].iter().map(|b| unsafe { b.assume_init() }).collect();
        let (ty, correlation, body) = ipc::decode(&frame)?;
        Some((ty, correlation, serde_json::from_slice(body).unwrap_or(Value::Null)))
    }

    /// The next message of type `ty` (heartbeats and others skipped), or None within 2 s.
    fn expect(&self, ty: u16) -> Option<(u32, Value)> {
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            match self.recv() {
                Some((t, correlation, body)) if t == ty => return Some((correlation, body)),
                _ => {}
            }
        }
        None
    }

    /// Everything of type `ty` that arrives within `wait`.
    fn drain(&self, ty: u16, wait: Duration) -> Vec<Value> {
        let deadline = Instant::now() + wait;
        let mut found = Vec::new();
        while Instant::now() < deadline {
            if let Some((t, _, body)) = self.recv()
                && t == ty
            {
                found.push(body);
            }
        }
        found
    }
}

fn setup(name: &str) -> (Registry, SteamWorker<Fake>, Mailbox<WorkerCmd>, Fake, Arc<Shared>) {
    setup_with_hold(name, Duration::from_secs(5))
}

fn setup_with_hold(name: &str, join_hold: Duration) -> (Registry, SteamWorker<Fake>, Mailbox<WorkerCmd>, Fake, Arc<Shared>) {
    let dir = std::env::temp_dir().join(format!("steam-test-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("gateway-relay.sock");
    let _ = std::fs::remove_file(&path);
    let listener = Socket::new(Domain::UNIX, Type::SEQPACKET, None).unwrap();
    listener.bind(&SockAddr::unix(&path).unwrap()).unwrap();
    listener.listen(1).unwrap();
    let (tx, inbound) = crossbeam_channel::unbounded();
    let sender = ipc::start(path, "test".into(), tx);
    let (peer, _) = listener.accept().unwrap();
    peer.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    let registry = Registry { peer, _listener: listener, _inbound: inbound, dir };
    registry.expect(types::HELLO).unwrap();
    registry.peer.send(&ipc::encode(types::HELLO, 0, br#"{"version":2,"role":"registry","build":"t"}"#)).unwrap();
    let deadline = Instant::now() + Duration::from_secs(2);
    while !sender.connected() && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    let shared = Arc::new(Shared::new(sender));
    let fake = Fake::default();
    let (worker, mailbox) =
        SteamWorker::new(3, fake.clone(), shared.clone(), relay::Limits::default(), join_hold, "0.0.0.0:27016".into()).unwrap();
    let _ = shared.workers.set(vec![mailbox.clone(), mailbox.clone(), mailbox.clone(), mailbox.clone()]);
    (registry, worker, mailbox, fake, shared)
}

/// A channel-2 game frame without CRC: message id, then `tail`.
fn game(message_id: u8, tail: &[u8]) -> Vec<u8> {
    let mut frame = vec![0xCE, 0, 0, 0, 0, 0, 0, 1, message_id, 0];
    frame.extend_from_slice(tail);
    frame
}

const USER: u64 = 76_561_198_000_000_001;

/// Session request, join, accept: returns the relay session number.
fn join(registry: &Registry, worker: &mut SteamWorker<Fake>, mailbox: &Mailbox<WorkerCmd>, fake: &Fake, now: Instant) -> u64 {
    fake.with(|s| s.callbacks.push(SteamCallback::SessionRequest(USER)));
    worker.step(now);
    let (correlation, request) = registry.expect(types::JOIN_REQUEST).expect("JoinRequest");
    assert_eq!(request["service"], "Steam");
    assert_eq!(request["clientId"], USER);
    assert_eq!(request["remote"], format!("steam:{USER}"));
    assert_eq!(correlation & 0xFF, 3, "the correlation names the Steam worker");
    // CLIENT_CONNECTED arrives before C# has answered: it waits, in order.
    let accepted = fake.with(|s| s.accepted.len());
    fake.with(|s| s.incoming.push_back((USER, 2, game(14, b"ticket"))));
    worker.step(now);
    assert_eq!(fake.with(|s| s.accepted.len()), accepted, "P2P is accepted only on C#'s answer");
    mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: JoinAnswer { decision: "accept".into(), reason: None } });
    worker.step(now);
    assert_eq!(fake.with(|s| s.accepted.len()), accepted + 1);
    worker.step(now);
    let (_, message) = registry.expect(types::CLIENT_MESSAGE).expect("the queued CLIENT_CONNECTED reaches C#");
    assert_eq!(message["messageId"], 14);
    request["session"].as_u64().unwrap()
}

#[test]
fn join_auth_and_verdicts() {
    let (registry, mut worker, mailbox, fake, shared) = setup("join");
    let now = Instant::now();
    let session = join(&registry, &mut worker, &mailbox, &fake, now);
    assert_eq!(shared.owner(USER), Some(Owner { worker: 3, session }));

    mailbox.send(WorkerCmd::SteamAuth(SteamAuth { op: "begin".into(), client_id: USER, ticket: Some(BASE64.encode(b"tkt")), group_id: 0 }));
    worker.step(now);
    assert_eq!(fake.with(|s| s.auth_begun.clone()), vec![(USER, b"tkt".to_vec())]);
    fake.with(|s| s.callbacks.push(SteamCallback::Ticket { user: USER, response: 0, owner: 42 }));
    worker.step(now);
    let (_, verdict) = registry.expect(types::STEAM_EVENT).unwrap();
    assert_eq!((verdict["kind"].as_str(), verdict["response"].as_i64(), verdict["owner"].as_u64()), (Some("ticket"), Some(0), Some(42)));

    // A ticket that does not decode is refused without asking Steam.
    mailbox.send(WorkerCmd::SteamAuth(SteamAuth { op: "begin".into(), client_id: USER, ticket: Some("!!".into()), group_id: 0 }));
    worker.step(now);
    let (_, rejected) = registry.expect(types::STEAM_EVENT).unwrap();
    assert_eq!(rejected["kind"], "ticketRejected");
    assert_eq!(fake.with(|s| s.auth_begun.len()), 1);

    mailbox.send(WorkerCmd::SteamAuth(SteamAuth { op: "group".into(), client_id: USER, ticket: None, group_id: 99 }));
    worker.step(now);
    assert_eq!(fake.with(|s| s.groups.clone()), vec![(USER, 99)]);
    fake.with(|s| s.callbacks.push(SteamCallback::Group { user: USER, member: true }));
    worker.step(now);
    let (_, group) = registry.expect(types::STEAM_EVENT).unwrap();
    assert_eq!((group["kind"].as_str(), group["member"].as_bool()), (Some("group"), Some(true)));

    // ClientSend reaches Steam: unreliable stays unreliable, anything else is reliable.
    mailbox.send(WorkerCmd::ClientSend { client_id: USER, session, channel: 2, delivery: 4, payload: vec![1] });
    mailbox.send(WorkerCmd::ClientSend { client_id: USER, session, channel: 0, delivery: 2, payload: vec![2] });
    mailbox.send(WorkerCmd::ClientSend { client_id: USER, session: session + 1, channel: 0, delivery: 2, payload: vec![3] });
    worker.step(now);
    let sent = fake.with(|s| s.sent.clone());
    assert_eq!(sent, vec![(USER, 2, false, vec![1]), (USER, 0, true, vec![2])], "a stale session's send is dropped");
}

#[test]
fn close_keeps_the_rejoin_grace_then_ends_the_session() {
    let (registry, mut worker, mailbox, fake, shared) = setup("grace");
    let t0 = Instant::now();
    let session = join(&registry, &mut worker, &mailbox, &fake, t0);
    mailbox.send(WorkerCmd::Disconnect { client_id: USER, session, reason: "gateway".into() });
    worker.step(t0);
    let (_, event) = registry.expect(types::CLIENT_EVENT).unwrap();
    assert_eq!((event["event"].as_str(), event["detail"].as_str()), (Some("disconnected"), Some("steam-session-closed")));
    assert_eq!(shared.owner(USER), None);
    assert_eq!(fake.with(|s| s.auth_ended.clone()), vec![USER], "the auth session ends at once");
    assert!(fake.with(|s| s.closed.is_empty()), "P2P stays open for the rejoin");

    // The player comes straight back: traffic in the open session is a new join.
    let t1 = t0 + Duration::from_secs(10);
    fake.with(|s| s.incoming.push_back((USER, 2, game(14, b"again"))));
    worker.step(t1);
    let (correlation, request) = registry.expect(types::JOIN_REQUEST).expect("rejoin asks C#");
    assert_ne!(request["session"].as_u64(), Some(session));
    mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: JoinAnswer { decision: "accept".into(), reason: None } });
    worker.step(t1);
    registry.expect(types::CLIENT_MESSAGE).expect("the rejoin's CLIENT_CONNECTED");
    let session = request["session"].as_u64().unwrap();

    // This time nobody comes back: 30 s after the detach, Steam closes the session.
    mailbox.send(WorkerCmd::Disconnect { client_id: USER, session, reason: "gateway".into() });
    let t2 = t1 + Duration::from_secs(1);
    worker.step(t2);
    worker.step(t2 + Duration::from_secs(29));
    assert!(fake.with(|s| s.closed.is_empty()));
    worker.step(t2 + Duration::from_secs(31));
    assert_eq!(fake.with(|s| s.closed.clone()), vec![USER]);
}

#[test]
fn refused_failed_silent_and_ipc_loss() {
    let (registry, mut worker, mailbox, fake, _shared) = setup("ends");
    let now = Instant::now();
    // C# refuses: P2P is closed and nothing was accepted.
    fake.with(|s| s.callbacks.push(SteamCallback::SessionRequest(USER)));
    worker.step(now);
    let (correlation, _) = registry.expect(types::JOIN_REQUEST).unwrap();
    mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: JoinAnswer { decision: "reject".into(), reason: Some("closed".into()) } });
    worker.step(now);
    assert!(fake.with(|s| s.accepted.is_empty()));
    assert_eq!(fake.with(|s| s.closed.clone()), vec![USER]);

    // Steam reports the P2P session failed: C# hears `disconnected`.
    let session = join(&registry, &mut worker, &mailbox, &fake, now);
    fake.with(|s| s.callbacks.push(SteamCallback::ConnectFail { user: USER, error: 4 }));
    worker.step(now);
    let (_, event) = registry.expect(types::CLIENT_EVENT).unwrap();
    assert_eq!(event["session"].as_u64(), Some(session));
    assert_eq!(event["detail"], "steam-session-failed: k_EP2PSessionErrorTimeout");

    // 180 s without traffic: `silent` (C# then force-reconnects).
    let t1 = now + Duration::from_secs(1);
    let session = join(&registry, &mut worker, &mailbox, &fake, t1);
    // (backlogExpired is reported meanwhile: CLIENT_CONNECTED waits in the backlog with no link.)
    let silent = |events: Vec<Value>| events.into_iter().filter(|e| e["event"] == "silent").count();
    worker.step(t1 + Duration::from_secs(179));
    assert_eq!(silent(registry.drain(types::CLIENT_EVENT, Duration::from_millis(200))), 0);
    worker.step(t1 + Duration::from_secs(181));
    assert_eq!(silent(registry.drain(types::CLIENT_EVENT, Duration::from_millis(200))), 1);

    // ClusterRegistry lost: the reconnect control message, then the session ends.
    fake.with(|s| s.sent.clear());
    mailbox.send(WorkerCmd::IpcLost);
    let t2 = t1 + Duration::from_secs(182);
    worker.step(t2);
    let control = fake.with(|s| s.sent.clone());
    assert_eq!(control.len(), 1);
    assert_eq!((control[0].1, control[0].2, &control[0].3[..2]), (0, true, &[1u8, 0][..]));
    worker.step(t2 + Duration::from_millis(200));
    let gone: Vec<Value> = registry.drain(types::CLIENT_EVENT, Duration::from_millis(200)).into_iter().filter(|e| e["event"] == "disconnected").collect();
    assert_eq!(gone.len(), 1);
    assert_eq!(gone[0]["session"].as_u64(), Some(session));
}

#[test]
fn publish_names_players_and_counts_the_rest_as_bots() {
    let (_registry, mut worker, mailbox, fake, _shared) = setup("publish");
    fake.with(|s| s.nameable.insert(1));
    let publish = SteamPublish {
        server_name: "Cluster".into(),
        map_name: "Earth".into(),
        max_players: 16,
        password_protected: false,
        tags: "t".into(),
        mod_count: 2,
        mods_total_size: 1714928497,
        players: vec![SteamPlayer { user_id: 1, name: "steam".into() }, SteamPlayer { user_id: 2, name: "direct".into() }],
    };
    mailbox.send(WorkerCmd::SteamPublish(Box::new(publish)));
    let now = Instant::now();
    worker.step(now);
    assert_eq!(fake.with(|s| s.named.clone()), vec![(1, "steam".into())]);
    assert_eq!(fake.with(|s| s.bots.clone()), vec![1]);
    // Re-pushed every 5 s, not every pass.
    worker.step(now + Duration::from_secs(1));
    assert_eq!(fake.with(|s| s.bots.len()), 1);
    worker.step(now + Duration::from_secs(6));
    assert_eq!(fake.with(|s| s.bots.len()), 2);
    assert_eq!(fake.with(|s| s.named.len()), 2);
}

/// T-0314: C# accepts a held join's older re-ask late and refuses the newer one as a second join;
/// the accept must not be dropped (C# would keep a session nobody backs and refuse the player).
#[test]
fn a_late_accept_of_a_superseded_ask_admits_the_client() {
    let (registry, mut worker, mailbox, fake, shared) = setup("late-accept");
    let t0 = Instant::now();
    fake.with(|s| s.callbacks.push(SteamCallback::SessionRequest(USER)));
    worker.step(t0);
    let (first, request) = registry.expect(types::JOIN_REQUEST).unwrap();
    let session = request["session"].as_u64().unwrap();
    mailbox.send(WorkerCmd::JoinAnswer { correlation: first, answer: JoinAnswer { decision: "hold".into(), reason: None } });
    worker.step(t0);
    worker.step(t0 + Duration::from_secs(1));
    let (late, _) = registry.expect(types::JOIN_REQUEST).expect("re-ask");
    worker.step(t0 + Duration::from_secs(2));
    let (newer, request) = registry.expect(types::JOIN_REQUEST).expect("re-ask of the unanswered re-ask");
    assert_eq!(request["session"].as_u64(), Some(session));
    assert_ne!(late, newer);
    mailbox.send(WorkerCmd::JoinAnswer { correlation: late, answer: JoinAnswer { decision: "accept".into(), reason: None } });
    mailbox.send(WorkerCmd::JoinAnswer { correlation: newer, answer: JoinAnswer { decision: "reject".into(), reason: None } });
    worker.step(t0 + Duration::from_secs(2));
    assert_eq!(fake.with(|s| s.accepted.clone()), vec![USER]);
    assert!(fake.with(|s| s.closed.is_empty()), "the stale refusal closed the admitted session");
    assert_eq!(shared.owner(USER), Some(Owner { worker: 3, session }));
}

/// T-0314: an accept after the hold limit refused the join releases the C# session it made.
#[test]
fn a_late_accept_after_the_join_was_refused_releases_the_registry_session() {
    let (registry, mut worker, mailbox, fake, _shared) = setup("refused-accept");
    let t0 = Instant::now();
    fake.with(|s| s.callbacks.push(SteamCallback::SessionRequest(USER)));
    worker.step(t0);
    let (first, request) = registry.expect(types::JOIN_REQUEST).unwrap();
    let session = request["session"].as_u64().unwrap();
    mailbox.send(WorkerCmd::JoinAnswer { correlation: first, answer: JoinAnswer { decision: "hold".into(), reason: None } });
    worker.step(t0);
    worker.step(t0 + Duration::from_secs(1));
    let (late, _) = registry.expect(types::JOIN_REQUEST).expect("re-ask");
    worker.step(t0 + Duration::from_secs(6));
    assert_eq!(fake.with(|s| s.closed.clone()), vec![USER], "the 5 s hold limit refused the join");
    mailbox.send(WorkerCmd::JoinAnswer { correlation: late, answer: JoinAnswer { decision: "accept".into(), reason: None } });
    worker.step(t0 + Duration::from_secs(6));
    let released: Vec<Value> = registry.drain(types::CLIENT_EVENT, Duration::from_millis(300)).into_iter().filter(|e| e["event"] == "disconnected").collect();
    assert_eq!(released.len(), 1, "the late accept's C# session is released");
    assert_eq!(released[0]["session"].as_u64(), Some(session));
    assert!(fake.with(|s| s.open.is_empty()), "no P2P session is left open");
}

/// T-0365: a held join's login must reach C# with the accept. Steam hands over nothing a client
/// sent before the P2P session is accepted, and the client's attempt fails by itself after about
/// 20 s without an accept (vanilla sends CLIENT_CONNECTED once per attempt), so the session is
/// accepted at the first hold and the login waits here. Live 2026-10-01 22:18:47: held 41 s, the
/// session C# then made never presented a login and was force-reconnected 5 s later.
#[test]
fn a_held_join_keeps_the_login_for_the_accept() {
    let (registry, mut worker, mailbox, fake, shared) = setup_with_hold("held-login", Duration::from_secs(120));
    let t0 = Instant::now();
    let hold = || JoinAnswer { decision: "hold".into(), reason: Some("character home partition is recovering".into()) };
    fake.with(|s| {
        s.callbacks.push(SteamCallback::SessionRequest(USER));
        s.incoming.push_back((USER, 2, game(14, b"ticket")));
    });
    worker.step(t0);
    let (mut correlation, request) = registry.expect(types::JOIN_REQUEST).unwrap();
    let session = request["session"].as_u64().unwrap();
    // Held for 25 s, re-asked every second.
    for second in 0..25u64 {
        let now = t0 + Duration::from_secs(second);
        mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: hold() });
        worker.step(now);
        if second == 20 {
            // The client's P2P attempt times out: what Steam still held for an unaccepted session is gone.
            fake.with(|s| {
                let open = s.open.clone();
                s.incoming.retain(|(user, ..)| open.contains(user));
            });
        }
        worker.step(now + Duration::from_secs(1));
        let (again, request) = registry.expect(types::JOIN_REQUEST).expect("re-ask");
        assert_eq!(request["session"].as_u64(), Some(session));
        correlation = again;
    }
    assert!(registry.drain(types::CLIENT_MESSAGE, Duration::from_millis(200)).is_empty(), "nothing reaches C# before it accepts");
    let accepted = t0 + Duration::from_secs(26);
    mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: JoinAnswer { decision: "accept".into(), reason: None } });
    worker.step(accepted);
    worker.step(accepted);
    let (_, message) = registry.expect(types::CLIENT_MESSAGE).expect("the held join's CLIENT_CONNECTED reaches C# with the accept");
    assert_eq!((message["messageId"].as_u64(), message["session"].as_u64()), (Some(14), Some(session)));
    assert_eq!(fake.with(|s| s.accepted.clone()), vec![USER], "accepted once, at the first hold");
    assert!(fake.with(|s| s.closed.is_empty()));
    assert_eq!(shared.owner(USER), Some(Owner { worker: 3, session }));
}

/// T-0365: a hold that runs out closes the P2P session it accepted, and what the client sent is dropped.
#[test]
fn a_held_join_that_runs_out_closes_the_accepted_session() {
    let (registry, mut worker, mailbox, fake, shared) = setup("held-out");
    let t0 = Instant::now();
    fake.with(|s| {
        s.callbacks.push(SteamCallback::SessionRequest(USER));
        s.incoming.push_back((USER, 2, game(14, b"ticket")));
    });
    worker.step(t0);
    let (correlation, _) = registry.expect(types::JOIN_REQUEST).unwrap();
    mailbox.send(WorkerCmd::JoinAnswer { correlation, answer: JoinAnswer { decision: "hold".into(), reason: None } });
    worker.step(t0);
    assert_eq!(fake.with(|s| s.accepted.clone()), vec![USER]);
    worker.step(t0 + Duration::from_secs(6));
    assert_eq!(fake.with(|s| s.closed.clone()), vec![USER], "the 5 s hold limit refused the join");
    assert!(registry.drain(types::CLIENT_MESSAGE, Duration::from_millis(200)).is_empty());
    assert_eq!(shared.owner(USER), None);
}
