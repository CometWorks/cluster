//! The Steam frontend (ClusterRegistry/SteamFrontend.cs) in gateway-rs: the single Steam GameServer
//! identity in VAC-secure mode, GameServer P2P on the vanilla channels {0, 2}, auth tickets, the
//! browser row and the master-server queries on the game port.
//!
//! One thread makes every Steamworks call except the query socket's (the same split as vanilla and
//! C#). That thread is also the worker of its clients: it owns their relay sessions exactly as a
//! front worker owns Direct clients, so control.rs routes per-client commands to it unchanged.
//! Admission stays in C#: a Steam session is asked for with `JoinRequest{service:"Steam"}`, the
//! ticket from CLIENT_CONNECTED is validated on C#'s `SteamAuth` command, and the verdict goes back
//! as a `SteamEvent`.

use std::collections::{HashMap, HashSet};
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, UdpSocket};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use mio::{Events, Poll, Token, Waker};
use relay::{Delivery, LinkId, Packet, Session};
use serde_json::json;

use crate::ipc::{ClientEvent, JoinAnswer, JoinRequest, SteamAuth, SteamConfig, SteamEvent, SteamPublish, types};
use crate::node::NodeCmd;
use crate::shared::{Inbox, JoinAsks, Mailbox, Owner, Shared};
use crate::worker::{WorkerCmd, report};

pub const APP_ID: u32 = 244850;
const WAKE: Token = Token(0);
/// The C# pump sleeps 1 ms between passes; P2P has no readiness to wait on.
const TICK: Duration = Duration::from_millis(1);
const MAINTENANCE: Duration = Duration::from_millis(100);
const CHANNELS: [u8; 2] = [0, 2];
const MAXIMUM_PACKET: usize = 1024 * 1024 + 1024;
/// How long a detached Steam session stays open for the player to come straight back (C#).
const REJOIN_GRACE: Duration = Duration::from_secs(30);
const PUBLISH_INTERVAL: Duration = Duration::from_secs(5);
/// StartSessionMaintenance: Steam P2P has no disconnect event of its own.
const SILENCE_LIMIT: Duration = Duration::from_secs(180);
/// Packets that arrive while C# decides on the join are kept (in order) up to this many bytes.
const PENDING_BYTES: usize = 1024 * 1024;
const UNANSWERED_JOIN: Duration = Duration::from_secs(15);
const FORCE_RECONNECT_DELAY: Duration = Duration::from_millis(25);
const SERVER_ID: u64 = 0xFF00_0000_0000_0001;

/// What a pass of the Steam callbacks produced, reduced to what the frontend acts on.
#[derive(Debug, Clone, PartialEq)]
pub enum SteamCallback {
    SessionRequest(u64),
    ConnectFail { user: u64, error: u8 },
    /// `response` is the raw EAuthSessionResponse (0 = OK).
    Ticket { user: u64, response: i32, owner: u64 },
    Group { user: u64, member: bool },
    Log(String),
}

/// T-0300: the P2P session's path: a Steam relay, or a direct address (which can also be a relay's).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct P2pRoute {
    pub relay: bool,
    pub remote: Option<std::net::SocketAddrV4>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Identity {
    pub server_id: u64,
    pub logged_on: bool,
    pub secure: bool,
    pub public_ip: Option<Ipv4Addr>,
}

/// The Steamworks calls the frontend makes; the real one is [`RealSteam`], tests use a fake.
pub trait SteamApi {
    fn callbacks(&mut self, out: &mut Vec<SteamCallback>);
    /// Reads one waiting P2P packet on `channel` into `buffer`: (sender, length).
    fn read(&mut self, channel: u8, buffer: &mut Vec<u8>) -> Option<(u64, usize)>;
    fn send(&mut self, user: u64, channel: u8, reliable: bool, data: &[u8]) -> bool;
    fn accept(&mut self, user: u64);
    fn close(&mut self, user: u64);
    /// `m_nPacketsQueuedForSend` of the P2P session, 0 without one.
    fn queued_packets(&self, user: u64) -> usize;
    /// T-0300: how the P2P session reaches the client (`m_bUsingRelay`, `m_nRemoteIP:Port`).
    fn p2p_route(&self, _user: u64) -> Option<P2pRoute> {
        None
    }
    /// EBeginAuthSessionResult; 0 is OK.
    fn begin_auth(&mut self, user: u64, ticket: &[u8]) -> i32;
    fn end_auth(&mut self, user: u64);
    fn request_group_status(&mut self, user: u64, group: u64);
    fn update_user_data(&mut self, user: u64, name: &str) -> bool;
    fn apply_settings(&mut self, publish: &SteamPublish, bots: i32);
    fn identity(&self) -> Identity;
}

// ------------------------------------------------------------------ the real Steam

/// Steamworks through the `steamworks` crate, plus raw calls it does not wrap.
pub struct RealSteam {
    server: steamworks::Server,
    raw: *mut steamworks_sys::ISteamGameServer,
    networking: steamworks::Networking,
}

// SAFETY: the raw interface pointer is process-global and Steamworks' game-server calls are
// thread-safe; in practice only the Steam thread and the query thread use it.
unsafe impl Send for RealSteam {}

impl RealSteam {
    /// `GameServer.Init` and the fixed settings (SteamFrontend.Start); logs on anonymously.
    pub fn start(config: &SteamConfig) -> Result<(Self, steamworks::Server), String> {
        install_steam_client();
        let address: Ipv4Addr = config.bind.as_deref().and_then(|b| b.parse().ok()).unwrap_or(Ipv4Addr::UNSPECIFIED);
        let version = config.version_string();
        let (server, _client) = steamworks::Server::init(
            address,
            config.port,
            steamworks::QUERY_PORT_SHARED,
            steamworks::ServerMode::AuthenticationAndSecure,
            &version,
        )
        .map_err(|error| format!("Steam GameServer.Init failed on {address}:{}: {error}", config.port))?;
        server.set_product("Space Engineers");
        server.set_game_description("Space Engineers");
        server.set_mod_dir("Space Engineers");
        server.set_dedicated_server(true);
        server.set_bot_player_count(0);
        server.set_game_data(&version);
        server.set_key_value("CONSOLE_COMPATIBLE", "0");
        server.log_on_anonymous();
        server.set_advertise_server_active(true);
        // SAFETY: Init succeeded, so the accessor returns the live interface.
        let raw = unsafe { steamworks_sys::SteamAPI_SteamGameServer_v015() };
        let networking = server.networking();
        Ok((Self { server: server.clone(), raw, networking }, server))
    }
}

fn auth_response(response: &Result<(), steamworks::AuthSessionValidateError>) -> i32 {
    use steamworks::AuthSessionValidateError::*;
    match response {
        Ok(()) => 0,
        Err(UserNotConnectedToSteam) => 1,
        Err(NoLicenseOrExpired) => 2,
        Err(VACBanned) => 3,
        Err(LoggedInElseWhere) => 4,
        Err(VACCheckTimedOut) => 5,
        Err(AuthTicketCancelled) => 6,
        Err(AuthTicketInvalidAlreadyUsed) => 7,
        Err(AuthTicketInvalid) => 8,
        Err(PublisherIssuedBan) => 9,
        Err(AuthTicketNetworkIdentityFailure) => 10,
    }
}

impl SteamApi for RealSteam {
    fn callbacks(&mut self, out: &mut Vec<SteamCallback>) {
        use steamworks::CallbackResult as C;
        self.server.process_callbacks(|callback| {
            out.push(match callback {
                C::P2PSessionRequest(request) => SteamCallback::SessionRequest(request.remote.raw()),
                C::P2PSessionConnectFail(fail) => SteamCallback::ConnectFail { user: fail.remote.raw(), error: fail.error },
                C::ValidateAuthTicketResponse(verdict) => SteamCallback::Ticket {
                    user: verdict.steam_id.raw(),
                    response: auth_response(&verdict.response),
                    owner: verdict.owner_steam_id.raw(),
                },
                C::GSClientGroupStatus(status) => SteamCallback::Group { user: status.user.raw(), member: status.member || status.officer },
                C::SteamServersConnected(_) => SteamCallback::Log("Steam GameServer connected".into()),
                C::SteamServersDisconnected(lost) => SteamCallback::Log(format!("WARNING: Steam GameServer disconnected: {:?}", lost.reason)),
                C::SteamServerConnectFailure(failure) => SteamCallback::Log(format!(
                    "WARNING: Steam GameServer logon failed: {:?} retrying={}",
                    failure.reason, failure.still_retrying
                )),
                _ => return,
            });
        });
    }

    fn read(&mut self, channel: u8, buffer: &mut Vec<u8>) -> Option<(u64, usize)> {
        let size = self.networking.is_p2p_packet_available_on_channel(channel as i32)?;
        if buffer.len() < size {
            buffer.resize(size, 0);
        }
        let (remote, read) = self.networking.read_p2p_packet_from_channel(buffer, channel as i32)?;
        Some((remote.raw(), read))
    }

    fn send(&mut self, user: u64, channel: u8, reliable: bool, data: &[u8]) -> bool {
        let send = if reliable { steamworks::SendType::Reliable } else { steamworks::SendType::Unreliable };
        self.networking.send_p2p_packet_on_channel(steamworks::SteamId::from_raw(user), send, data, channel as i32)
    }

    fn accept(&mut self, user: u64) {
        self.networking.accept_p2p_session(steamworks::SteamId::from_raw(user));
    }

    fn close(&mut self, user: u64) {
        self.networking.close_p2p_session(steamworks::SteamId::from_raw(user));
    }

    fn queued_packets(&self, user: u64) -> usize {
        self.networking
            .get_p2p_session_state(steamworks::SteamId::from_raw(user))
            .map_or(0, |state| state.packets_queued_for_send.max(0) as usize)
    }

    fn p2p_route(&self, user: u64) -> Option<P2pRoute> {
        let state = self.networking.get_p2p_session_state(steamworks::SteamId::from_raw(user))?;
        let remote = state.remote_ip.zip(state.remote_port).map(|(ip, port)| std::net::SocketAddrV4::new(ip, port));
        Some(P2pRoute { relay: state.using_relay, remote })
    }

    fn begin_auth(&mut self, user: u64, ticket: &[u8]) -> i32 {
        // Raw: the crate's wrapper panics on a result it does not know.
        // SAFETY: a live interface and a borrowed buffer of the stated length.
        let result = unsafe { steamworks_sys::SteamAPI_ISteamGameServer_BeginAuthSession(self.raw, ticket.as_ptr().cast(), ticket.len() as i32, user) };
        result as i32
    }

    fn end_auth(&mut self, user: u64) {
        self.server.end_authentication_session(steamworks::SteamId::from_raw(user));
    }

    fn request_group_status(&mut self, user: u64, group: u64) {
        // SAFETY: a live interface; plain ids.
        unsafe { steamworks_sys::SteamAPI_ISteamGameServer_RequestUserGroupStatus(self.raw, user, group) };
    }

    fn update_user_data(&mut self, user: u64, name: &str) -> bool {
        let Ok(name) = std::ffi::CString::new(name.replace('\0', "")) else { return false };
        // SAFETY: a live interface and a NUL-terminated name that outlives the call.
        unsafe { steamworks_sys::SteamAPI_ISteamGameServer_BUpdateUserData(self.raw, user, name.as_ptr(), 0) }
    }

    fn apply_settings(&mut self, publish: &SteamPublish, bots: i32) {
        self.server.set_server_name(&publish.server_name);
        self.server.set_map_name(&publish.map_name);
        self.server.set_max_players(publish.max_players);
        self.server.set_password_protected(publish.password_protected);
        self.server.set_game_tags(&publish.tags);
        self.server.set_key_value("mods", &publish.mod_count.to_string());
        self.server.set_key_value("modsTotalSize", &publish.mods_total_size.to_string());
        for rule in &publish.rules {
            self.server.set_key_value(&rule.key, &rule.value);
        }
        self.server.set_bot_player_count(bots);
    }

    fn identity(&self) -> Identity {
        // SAFETY: a live interface; these are plain getters.
        unsafe {
            let logged_on = steamworks_sys::SteamAPI_ISteamGameServer_BLoggedOn(self.raw);
            let secure = steamworks_sys::SteamAPI_ISteamGameServer_BSecure(self.raw);
            let address = steamworks_sys::SteamAPI_ISteamGameServer_GetPublicIP(self.raw);
            // Packed: copy the fields out rather than take references.
            let kind = std::ptr::addr_of!(address.m_eType).read_unaligned();
            let bits = std::ptr::addr_of!(address.__bindgen_anon_1.m_unIPv4).read_unaligned();
            let public_ip = (kind == steamworks_sys::ESteamIPType::k_ESteamIPTypeIPv4)
                .then(|| Ipv4Addr::from_bits(bits))
                .filter(|ip| !ip.is_unspecified());
            Identity {
                server_id: if logged_on { self.server.steam_id().raw() } else { 0 },
                logged_on,
                secure,
                public_ip,
            }
        }
    }
}

/// The Steam loader looks for steamclient.so under ~/.steam/sdk64 when no Steam is installed.
/// Stage-and-rename, never copy over the target: a running Steam server has it mmap'd, and an
/// in-place overwrite truncates the inode under that mapping (SIGBUS). Same as SteamFrontend.cs.
fn install_steam_client() {
    let Some(home) = std::env::var_os("HOME").map(PathBuf::from) else { return };
    let Ok(exe) = std::env::current_exe() else { return };
    let Some(dir) = exe.parent() else { return };
    let Some(source) = [dir.join("steamclient.so"), dir.join("../gateway/steamclient.so")].into_iter().find(|p| p.is_file()) else {
        return;
    };
    let directory = home.join(".steam/sdk64");
    let target = directory.join("steamclient.so");
    let same = std::fs::metadata(&target).ok().zip(std::fs::metadata(&source).ok()).is_some_and(|(t, s)| t.len() == s.len());
    if same {
        return;
    }
    let staging = directory.join(format!("steamclient.so.{}.tmp", std::process::id()));
    let result = std::fs::create_dir_all(&directory)
        .and_then(|()| std::fs::copy(&source, &staging))
        .and_then(|_| std::fs::rename(&staging, &target));
    if let Err(error) = result {
        // A concurrent instance may already have staged it; Init reports it if not.
        eprintln!("WARNING: could not install {} as {}: {error}", source.display(), target.display());
    }
    let _ = std::fs::remove_file(&staging);
}

/// Master-server queries arrive as 0xFFFFFFFF-prefixed datagrams on the game port and are
/// answered by the SDK (MySteamGameServer.SteamServerEntryPoint). No game traffic flows here.
pub fn start_query_thread(server: steamworks::Server, bind: SocketAddrV4, queries: Arc<AtomicU64>) -> std::io::Result<()> {
    let socket = UdpSocket::bind(bind)?;
    std::thread::Builder::new().name("steam-query".into()).spawn(move || {
        let mut buffer = vec![0u8; 16 * 1024];
        let mut outgoing = vec![0u8; 16 * 1024];
        loop {
            let (size, from) = match socket.recv_from(&mut buffer) {
                Ok(received) => received,
                Err(_) => continue,
            };
            let SocketAddr::V4(from) = from else { continue };
            if size < 4 || buffer[..4] != [0xFF; 4] {
                continue;
            }
            queries.fetch_add(1, Ordering::Relaxed);
            server.handle_incoming_packet(&buffer[..size], from);
            server.get_next_outgoing_packet(&mut outgoing, |to, data| {
                let _ = socket.send_to(data, to);
            });
        }
    })?;
    Ok(())
}

// ------------------------------------------------------------------ the frontend

struct Client {
    session: Session,
    connected_at: Instant,
    /// The P2P send queue, refreshed each maintenance pass and counted up by our own sends.
    queued: usize,
    /// T-0300: the last P2P route seen; a change is logged.
    route: Option<P2pRoute>,
}

struct Pending {
    client_id: u64,
    session: u64,
    first_asked: Instant,
    last_asked: Instant,
    held: bool,
    /// T-0365: the P2P session is accepted (at the first hold, or it never closed: a rejoin).
    p2p_accepted: bool,
    /// The P2P session failed while C# was deciding: an accept is answered with `disconnected`.
    gone: bool,
    packets: Vec<Packet>,
    bytes: usize,
}

#[derive(Default)]
struct Counters {
    sent: u64,
    received: u64,
    auth_requests: u64,
    auth_rejections: u64,
}

/// The relay core's view of one Steam client.
struct SteamIo<'a, A: SteamApi> {
    api: &'a mut A,
    shared: &'a Shared,
    queued: &'a mut usize,
    counters: &'a mut Counters,
}

impl<A: SteamApi> relay::Io for SteamIo<'_, A> {
    fn paused(&self) -> bool {
        self.shared.paused()
    }

    fn link_connected(&self, link: LinkId) -> bool {
        self.shared.links.connected(link)
    }

    fn send_to_node(&mut self, link: LinkId, client_id: u64, packet: Packet) {
        self.shared.to_node(NodeCmd::Relay { link, client_id, packet });
    }

    fn send_to_client(&mut self, client_id: u64, packet: Packet) -> Option<usize> {
        let reliable = packet.delivery != Delivery::Unreliable;
        if !self.api.send(client_id, packet.channel, reliable, &packet.payload) {
            return None;
        }
        self.counters.sent += 1;
        if reliable {
            *self.queued += 1;
        }
        Some(*self.queued)
    }

    fn client_reliable_queue(&self, _client_id: u64) -> Option<usize> {
        Some(*self.queued)
    }

    fn send_credit(&mut self, link: LinkId, client_id: u64, bytes: i32) {
        self.shared.to_node(NodeCmd::Credit { link, client_id, credit: bytes });
    }

    fn event(&mut self, client_id: u64, session: u64, event: relay::Event) {
        report(self.shared, client_id, session, event);
    }
}

pub struct SteamWorker<A: SteamApi> {
    index: u16,
    api: A,
    shared: Arc<Shared>,
    inbox: Inbox<WorkerCmd>,
    poll: Poll,
    limits: relay::Limits,
    join_hold: Duration,
    endpoint: String,
    clients: HashMap<u64, Client>,
    /// Open P2P sessions and when each was detached (None while a client uses it).
    p2p: HashMap<u64, Option<Instant>>,
    pending: HashMap<u32, Pending>,
    pending_by_user: HashMap<u64, u32>,
    asks: JoinAsks,
    force_disconnects: Vec<(Instant, u64, u64)>,
    publish: Option<SteamPublish>,
    publish_dirty: bool,
    next_publish: Instant,
    published: HashSet<u64>,
    unnamed_reported: HashSet<u64>,
    identity: Identity,
    counters: Counters,
    pub queries: Arc<AtomicU64>,
    callbacks: Vec<SteamCallback>,
    buffer: Vec<u8>,
    last_maintenance: Instant,
}

impl<A: SteamApi> SteamWorker<A> {
    pub fn new(index: u16, api: A, shared: Arc<Shared>, limits: relay::Limits, join_hold: Duration, endpoint: String) -> std::io::Result<(Self, Mailbox<WorkerCmd>)> {
        let poll = Poll::new()?;
        let (mailbox, inbox) = Mailbox::new(Waker::new(poll.registry(), WAKE)?);
        let now = Instant::now();
        Ok((
            Self {
                index,
                api,
                shared,
                inbox,
                poll,
                limits,
                join_hold,
                endpoint,
                clients: HashMap::new(),
                p2p: HashMap::new(),
                pending: HashMap::new(),
                pending_by_user: HashMap::new(),
                asks: JoinAsks::default(),
                force_disconnects: Vec::new(),
                publish: None,
                publish_dirty: true,
                next_publish: now,
                published: HashSet::new(),
                unnamed_reported: HashSet::new(),
                identity: Identity::default(),
                counters: Counters::default(),
                queries: Arc::new(AtomicU64::new(0)),
                callbacks: Vec::new(),
                buffer: vec![0; 64 * 1024],
                last_maintenance: now,
            },
            mailbox,
        ))
    }

    pub fn run(mut self) {
        let mut events = Events::with_capacity(4);
        let started = Instant::now();
        let mut identity_warned = false;
        loop {
            let wait = if self.inbox.park() { TICK } else { Duration::ZERO };
            let _ = self.poll.poll(&mut events, Some(wait));
            self.inbox.unpark();
            self.step(Instant::now());
            if !identity_warned && self.identity.server_id == 0 && started.elapsed() > Duration::from_secs(30) {
                identity_warned = true;
                eprintln!("WARNING: Steam GameServer has no identity after 30 s; Steam clients cannot join");
            }
        }
    }

    /// One pass: Steam callbacks, P2P receive, commands, maintenance and publishing.
    pub fn step(&mut self, now: Instant) {
        let mut callbacks = std::mem::take(&mut self.callbacks);
        self.api.callbacks(&mut callbacks);
        for callback in callbacks.drain(..) {
            self.callback(callback, now);
        }
        self.callbacks = callbacks;
        if self.identity.server_id == 0 {
            let identity = self.api.identity();
            if identity.server_id != 0 {
                eprintln!(
                    "Steam frontend listening on {} as {}, public address {}, secure {}",
                    self.endpoint,
                    identity.server_id,
                    identity.public_ip.map_or("unknown".into(), |ip| ip.to_string()),
                    identity.secure
                );
                self.identity = identity;
            }
        }
        for channel in CHANNELS {
            let mut buffer = std::mem::take(&mut self.buffer);
            while let Some((user, length)) = self.api.read(channel, &mut buffer) {
                self.counters.received += 1;
                if length <= MAXIMUM_PACKET {
                    self.client_packet(user, channel, buffer[..length].to_vec(), now);
                }
            }
            if buffer.len() > 64 * 1024 {
                buffer.truncate(64 * 1024);
                buffer.shrink_to_fit();
            }
            self.buffer = buffer;
        }
        while let Ok(command) = self.inbox.rx.try_recv() {
            self.command(command, now);
        }
        if now.duration_since(self.last_maintenance) >= MAINTENANCE {
            self.last_maintenance = now;
            self.maintain(now);
        }
        if self.publish.is_some() && (self.publish_dirty || now >= self.next_publish) {
            self.publish_dirty = false;
            self.next_publish = now + PUBLISH_INTERVAL;
            self.publish_now();
        }
    }

    fn with_client<R>(&mut self, client_id: u64, f: impl FnOnce(&mut Session, &mut SteamIo<'_, A>) -> R) -> Option<R> {
        let client = self.clients.get_mut(&client_id)?;
        let mut io = SteamIo { api: &mut self.api, shared: &self.shared, queued: &mut client.queued, counters: &mut self.counters };
        Some(f(&mut client.session, &mut io))
    }

    fn session_matches(&self, client_id: u64, session: u64) -> bool {
        self.clients.get(&client_id).is_some_and(|client| client.session.session == session)
    }

    fn client_event(&self, client_id: u64, session: u64, event: &'static str, detail: Option<String>) {
        self.shared.ipc.send_json(types::CLIENT_EVENT, 0, &ClientEvent { client_id, session, event, detail });
    }

    fn steam_event(&self, event: SteamEvent) {
        self.shared.ipc.send_json(types::STEAM_EVENT, 0, &event);
    }

    // ---------------------------------------------------------------- Steam callbacks

    fn callback(&mut self, callback: SteamCallback, now: Instant) {
        match callback {
            SteamCallback::SessionRequest(user) => {
                if self.clients.contains_key(&user) {
                    // C# refuses a second session for a live client (OpenSteamSession) and closes P2P.
                    eprintln!("Refused Steam session with {user}: a session is already open");
                    self.api.close(user);
                } else if !self.pending_by_user.contains_key(&user) {
                    self.open(user, None, now);
                }
            }
            SteamCallback::ConnectFail { user, error } => {
                let reason = format!("steam-session-failed: {}", p2p_error(error));
                self.hard_close(user, &reason);
                if let Some(pending) = self.pending_by_user.get(&user).and_then(|c| self.pending.get_mut(c)) {
                    pending.gone = true;
                }
                self.drop_client(user, reason);
            }
            SteamCallback::Ticket { user, response, owner } => {
                if response != 0 {
                    self.counters.auth_rejections += 1;
                }
                eprintln!("Steam ticket verdict for {user}: {response} owner {owner}");
                self.steam_event(SteamEvent { kind: "ticket", client_id: user, response: Some(response), owner: Some(owner), member: None });
            }
            SteamCallback::Group { user, member } => {
                self.steam_event(SteamEvent { kind: "group", client_id: user, response: None, owner: None, member: Some(member) });
            }
            SteamCallback::Log(line) => eprintln!("{line}"),
        }
    }

    // ---------------------------------------------------------------- joins

    /// A session request, or traffic from a user with no client (a rejoin inside the grace):
    /// ask C#, keeping what arrives meanwhile.
    fn open(&mut self, user: u64, first: Option<Packet>, now: Instant) {
        if !self.shared.ipc.connected() {
            eprintln!("Refused Steam session with {user}: ClusterGateway is not connected");
            self.hard_close(user, "no ClusterGateway");
            return;
        }
        let mut pending = Pending {
            client_id: user,
            session: self.shared.new_session(),
            first_asked: now,
            last_asked: now,
            held: false,
            // Traffic without a session request came through a P2P session that is still open.
            p2p_accepted: first.is_some(),
            gone: false,
            packets: Vec::new(),
            bytes: 0,
        };
        if let Some(packet) = first {
            pending.bytes = packet.payload.len();
            pending.packets.push(packet);
        }
        self.ask(pending);
    }

    fn ask(&mut self, pending: Pending) {
        let correlation = self.shared.join_correlation(self.index);
        let request = JoinRequest {
            client_id: pending.client_id,
            session: pending.session,
            service: "Steam",
            name: None,
            remote: format!("steam:{}", pending.client_id),
        };
        self.shared.set_pending(pending.client_id, Owner { worker: self.index, session: pending.session });
        self.shared.ipc.send_json(types::JOIN_REQUEST, correlation, &request);
        self.asks.asked(correlation, pending.client_id, pending.session, pending.last_asked);
        self.pending_by_user.insert(pending.client_id, correlation);
        self.pending.insert(correlation, pending);
    }

    fn take_pending(&mut self, correlation: u32) -> Option<Pending> {
        let pending = self.pending.remove(&correlation)?;
        if self.pending_by_user.get(&pending.client_id) == Some(&correlation) {
            self.pending_by_user.remove(&pending.client_id);
        }
        Some(pending)
    }

    fn join_answer(&mut self, correlation: u32, answer: JoinAnswer, now: Instant) {
        let Some(asked) = self.asks.answered(correlation) else { return };
        // The join is parked under the correlation it was LAST asked with (T-0314).
        let current = self.pending_by_user.get(&asked.client_id).copied().filter(|c| self.pending.get(c).is_some_and(|p| p.session == asked.session));
        let current = match current {
            // An accept of a superseded ask made the C# session: take it (C# refuses the newer ask).
            Some(current) if current == correlation || answer.decision == "accept" => current,
            // A superseded ask's hold or refusal: the newer ask's answer decides.
            Some(_) => return,
            None => {
                if answer.decision == "accept" && !self.session_matches(asked.client_id, asked.session) {
                    eprintln!("Released late accept of Steam client {} session {}: its join was already refused", asked.client_id, asked.session);
                    self.client_event(asked.client_id, asked.session, "disconnected", Some("join already refused".into()));
                }
                return;
            }
        };
        let Some(mut pending) = self.take_pending(current) else { return };
        let (user, session) = (pending.client_id, pending.session);
        let hold = answer.decision == "hold" && !pending.gone;
        match answer.decision.as_str() {
            "accept" if pending.gone || self.clients.contains_key(&user) => {
                self.client_event(user, pending.session, "disconnected", Some("steam session gone".into()));
            }
            "accept" => {
                if !pending.p2p_accepted {
                    self.api.accept(user);
                }
                self.p2p.insert(user, None);
                self.shared.owners.write().unwrap().insert(user, Owner { worker: self.index, session: pending.session });
                let session = Session::new(user, pending.session, self.limits, now);
                self.clients.insert(user, Client { session, connected_at: now, queued: 0, route: None });
                self.publish_dirty = true;
                eprintln!("Accepted Steam P2P session with {user}");
                for packet in std::mem::take(&mut pending.packets) {
                    self.relay_client_packet(user, packet, now);
                }
            }
            "hold" if !pending.gone => {
                // T-0365: a hold is "not yet", not a refusal, so the P2P session is accepted now. Steam
                // hands over nothing a client sent before the accept, and the client's P2P attempt
                // fails by itself after about 20 s without one; vanilla sends CLIENT_CONNECTED exactly
                // once per attempt. Left unaccepted, a join held longer than that was admitted to a
                // session whose login was already lost, and C# ended it as a session without a login
                // (2026-10-01 22:18:47 held 41 s, then 77 forced ends in 32 minutes). Accepted, the
                // login is read into `packets` and reaches C# with the accept; a hold that runs out
                // still closes the session (maintain_joins).
                if !pending.p2p_accepted {
                    pending.p2p_accepted = true;
                    self.api.accept(user);
                    self.p2p.insert(user, None);
                    self.publish_dirty = true;
                }
                pending.held = true;
                pending.last_asked = now;
                self.pending_by_user.insert(user, correlation);
                self.pending.insert(correlation, pending);
            }
            _ => {
                if let Some(reason) = answer.reason {
                    eprintln!("Refused Steam session with {user}: {reason}");
                }
                self.hard_close(user, "join refused");
            }
        }
        // After the live owner (on accept) is in place, so a lookup always finds one of them.
        if !hold {
            self.shared.clear_pending(user, session);
        }
    }

    fn maintain_joins(&mut self, now: Instant) {
        self.asks.prune(now);
        let mut reask = Vec::new();
        let mut refuse = Vec::new();
        for (&correlation, pending) in &self.pending {
            let age = now.duration_since(pending.first_asked);
            if pending.held {
                if age >= self.join_hold {
                    refuse.push(correlation);
                } else if now.duration_since(pending.last_asked) >= Duration::from_secs(1) {
                    reask.push(correlation);
                }
            } else if age >= UNANSWERED_JOIN {
                refuse.push(correlation);
            }
        }
        for correlation in refuse {
            if let Some(pending) = self.take_pending(correlation) {
                self.shared.clear_pending(pending.client_id, pending.session);
                eprintln!("Refused held Steam join of {} after {:?}", pending.client_id, now.duration_since(pending.first_asked));
                self.hard_close(pending.client_id, "join not answered");
            }
        }
        for correlation in reask {
            if let Some(mut pending) = self.take_pending(correlation) {
                pending.last_asked = now;
                if self.shared.ipc.connected() {
                    self.ask(pending);
                } else {
                    self.shared.clear_pending(pending.client_id, pending.session);
                    self.hard_close(pending.client_id, "no ClusterGateway");
                }
            }
        }
    }

    // ---------------------------------------------------------------- traffic

    fn client_packet(&mut self, user: u64, channel: u8, payload: Vec<u8>, now: Instant) {
        // Traffic means the peer is there: adopt the session whether it was detached or never
        // announced by a request (SteamFrontend.PumpLoop).
        self.p2p.insert(user, None);
        // SteamFrontend delivers every P2P packet as ReliableOrdered.
        let packet = Packet { channel, delivery: Delivery::ReliableOrdered, payload };
        if self.clients.contains_key(&user) {
            self.relay_client_packet(user, packet, now);
        } else if let Some(pending) = self.pending_by_user.get(&user).and_then(|c| self.pending.get_mut(c)) {
            if pending.bytes + packet.payload.len() <= PENDING_BYTES {
                pending.bytes += packet.payload.len();
                pending.packets.push(packet);
            }
        } else {
            self.open(user, Some(packet), now);
        }
    }

    fn relay_client_packet(&mut self, user: u64, packet: Packet, now: Instant) {
        self.with_client(user, |session, io| {
            let _ = session.on_client_packet(packet, now, io);
        });
    }

    // ---------------------------------------------------------------- session ends

    /// `SteamFrontend.Close`: ends the auth session and detaches the client, but leaves the P2P
    /// session open for the rejoin grace (a player who comes straight back sends into it).
    fn detach(&mut self, user: u64, detail: &str, now: Instant) {
        self.api.end_auth(user);
        if let Some(detached) = self.p2p.get_mut(&user) {
            *detached = Some(now);
        }
        self.publish_dirty = true;
        self.drop_client(user, detail.to_owned());
    }

    /// Removes the relay session and tells C#.
    fn drop_client(&mut self, user: u64, detail: String) {
        self.force_disconnects.retain(|(_, pending, _)| *pending != user);
        let Some(client) = self.clients.remove(&user) else { return };
        let session = client.session.session;
        {
            let mut owners = self.shared.owners.write().unwrap();
            if owners.get(&user).is_some_and(|owner| owner.session == session) {
                owners.remove(&user);
            }
        }
        self.client_event(user, session, "disconnected", Some(detail));
    }

    /// Ends the session at Steam too; the browser player row goes with it.
    fn hard_close(&mut self, user: u64, reason: &str) {
        self.api.end_auth(user);
        self.api.close(user);
        if self.p2p.remove(&user).is_some() {
            self.publish_dirty = true;
            eprintln!("Closed the Steam session with {user}: {reason}");
        }
    }

    fn force_reconnect(&mut self, user: u64, now: Instant) {
        let Some(session) = self.clients.get(&user).map(|client| client.session.session) else { return };
        // T-0244: a vanilla Steam client takes the host-left path (exit to menu, auto-reconnect) only for
        // the id it joined, the GameServer's SteamID; the DirectTransport constant is ignored as "another
        // member left". The constant stays as the fallback before the GameServer has logged on.
        let server_id = if self.identity.server_id != 0 { self.identity.server_id } else { SERVER_ID };
        let mut control = [0u8; 10];
        control[0..2].copy_from_slice(&1u16.to_le_bytes());
        control[2..10].copy_from_slice(&server_id.to_le_bytes());
        if self.api.send(user, 0, true, &control) {
            self.counters.sent += 1;
        }
        if !self.force_disconnects.iter().any(|(_, pending, _)| *pending == user) {
            self.force_disconnects.push((now + FORCE_RECONNECT_DELAY, user, session));
        }
    }

    // ---------------------------------------------------------------- commands

    fn command(&mut self, command: WorkerCmd, now: Instant) {
        match command {
            WorkerCmd::JoinAnswer { correlation, answer } => self.join_answer(correlation, answer, now),
            WorkerCmd::Disconnect { client_id, session, reason } => {
                if self.session_matches(client_id, session) {
                    eprintln!("Disconnecting Steam client {client_id}: {reason}");
                    self.detach(client_id, "steam-session-closed", now);
                }
            }
            WorkerCmd::ForceReconnect { client_id, session, reason } => {
                if self.session_matches(client_id, session) {
                    eprintln!("Force-reconnecting Steam client {client_id}: {reason}");
                    self.force_reconnect(client_id, now);
                }
            }
            WorkerCmd::Update { client_id, session, correlation, update } => {
                let applied = if self.session_matches(client_id, session) {
                    self.with_client(client_id, |relay, io| {
                        relay.apply(&update, now, io);
                        let status = relay.status(now);
                        (status.client_backlog_packets, status.server_backlog_packets)
                    })
                } else {
                    None
                };
                if correlation != 0 {
                    let (ok, client, server) = applied.map_or((false, 0, 0), |(c, s)| (true, c, s));
                    let body = json!({ "ok": ok, "clientBacklog": client, "serverBacklog": server });
                    self.shared.ipc.send_json(types::SESSION_UPDATE, correlation, &body);
                }
            }
            WorkerCmd::ReplayConnect { client_id, session } => {
                if self.session_matches(client_id, session) {
                    self.with_client(client_id, |relay, io| relay.replay_connect(io));
                }
            }
            WorkerCmd::ClientSend { client_id, session, channel, delivery, payload } => {
                if self.session_matches(client_id, session) {
                    let delivery = Delivery::from_byte(delivery).unwrap_or(Delivery::ReliableOrdered);
                    self.with_client(client_id, |_, io| {
                        relay::Io::send_to_client(io, client_id, Packet { channel, delivery, payload });
                    });
                }
            }
            WorkerCmd::FromNode { client_id, link, channel, delivery, payload } => {
                let invalid = self.with_client(client_id, |relay, io| {
                    relay.on_node_packet(link, Packet { channel, delivery, payload }, now, io).is_err()
                });
                if invalid == Some(true) {
                    self.shared.to_node(NodeCmd::Close { link, reason: "invalid game frame".into() });
                }
            }
            WorkerCmd::Flush => {
                let ids: Vec<u64> = self.clients.keys().copied().collect();
                for client_id in ids {
                    self.with_client(client_id, |relay, io| relay.poll_flush(io));
                }
            }
            WorkerCmd::IpcLost => {
                let ids: Vec<u64> = self.clients.keys().copied().collect();
                for client_id in ids {
                    self.force_reconnect(client_id, now);
                }
                let pending: Vec<u32> = self.pending.keys().copied().collect();
                for correlation in pending {
                    if let Some(pending) = self.take_pending(correlation) {
                        self.shared.clear_pending(pending.client_id, pending.session);
                        self.hard_close(pending.client_id, "ClusterGateway lost");
                    }
                }
                self.asks.clear();
            }
            WorkerCmd::Status(reply) => {
                let sessions = self.clients.iter().map(|(&id, client)| self.status_of(id, client, now)).collect();
                let _ = reply.send(sessions);
            }
            WorkerCmd::SteamAuth(auth) => self.auth(auth),
            WorkerCmd::SteamPublish(publish) => {
                self.publish = Some(*publish);
                self.publish_dirty = true;
            }
            WorkerCmd::SteamStatus(reply) => {
                let _ = reply.send(self.status());
            }
        }
    }

    fn auth(&mut self, auth: SteamAuth) {
        let user = auth.client_id;
        match auth.op.as_str() {
            "begin" => {
                self.counters.auth_requests += 1;
                let ticket = auth.ticket.as_deref().map(|t| BASE64.decode(t));
                // 1 = k_EBeginAuthSessionResultInvalidTicket.
                let result = match ticket {
                    Some(Ok(ticket)) if !ticket.is_empty() => self.api.begin_auth(user, &ticket),
                    _ => 1,
                };
                if result != 0 {
                    self.counters.auth_rejections += 1;
                    eprintln!("Steam BeginAuthSession for {user} failed: {result}");
                    self.steam_event(SteamEvent { kind: "ticketRejected", client_id: user, response: Some(result), owner: None, member: None });
                }
            }
            "end" => self.api.end_auth(user),
            "group" => self.api.request_group_status(user, auth.group_id),
            other => eprintln!("WARNING: unknown SteamAuth op {other}"),
        }
    }

    /// SteamFrontend.Publish: re-push every player (vanilla does too); a player Steam will not
    /// name (a Direct Transport client has no auth session) is counted into the bot slot.
    fn publish_now(&mut self) {
        let Some(publish) = self.publish.take() else { return };
        let mut unnamed = 0;
        for player in &publish.players {
            if self.api.update_user_data(player.user_id, &player.name) {
                self.published.insert(player.user_id);
                continue;
            }
            unnamed += 1;
            let was_published = self.published.remove(&player.user_id);
            let first_time = self.unnamed_reported.insert(player.user_id);
            if was_published || first_time {
                eprintln!("Steam would not publish player {} ({}); counting it in the bot slot", player.user_id, player.name);
            }
        }
        let live: HashSet<u64> = publish.players.iter().map(|p| p.user_id).collect();
        self.unnamed_reported.retain(|id| live.contains(id));
        self.published.retain(|id| live.contains(id));
        self.api.apply_settings(&publish, unnamed);
        self.publish = Some(publish);
    }

    // ---------------------------------------------------------------- maintenance

    fn maintain(&mut self, now: Instant) {
        // Logon, VAC-secure policy and public address arrive after Init and can change.
        if self.identity.server_id != 0 {
            self.identity = self.api.identity();
        }
        let ids: Vec<u64> = self.clients.keys().copied().collect();
        for client_id in ids {
            let queued = self.api.queued_packets(client_id);
            let route = self.api.p2p_route(client_id);
            if let Some(client) = self.clients.get_mut(&client_id) {
                client.queued = queued;
                if let Some(r) = route.as_ref().filter(|r| client.route.as_ref() != Some(*r)) {
                    eprintln!("steam p2p route client={client_id} relay={} remote={}", r.relay,
                        r.remote.map_or("-".to_owned(), |a| a.to_string()));
                    client.route = route;
                }
            }
            self.with_client(client_id, |relay, io| relay.maintain(now, Some(SILENCE_LIMIT), io));
        }
        let due: Vec<(u64, u64)> = self.force_disconnects.iter().filter(|(at, ..)| *at <= now).map(|&(_, user, session)| (user, session)).collect();
        self.force_disconnects.retain(|(at, ..)| *at > now);
        for (user, session) in due {
            if self.session_matches(user, session) {
                self.detach(user, "steam-session-closed", now);
            }
        }
        let expired: Vec<u64> = self
            .p2p
            .iter()
            .filter(|(_, detached)| detached.is_some_and(|at| now.duration_since(at) >= REJOIN_GRACE))
            .map(|(&user, _)| user)
            .collect();
        for user in expired {
            self.hard_close(user, "the rejoin grace expired");
        }
        self.maintain_joins(now);
    }

    fn status_of(&self, client_id: u64, client: &Client, now: Instant) -> serde_json::Value {
        let status = client.session.status(now);
        json!({
            "clientId": client_id,
            "session": client.session.session,
            "worker": self.index,
            "service": "Steam",
            "name": null,
            "remote": format!("steam:{client_id}"),
            "link": status.link.map(|link| self.shared.links.name(link)),
            "attached": status.attached,
            "routable": status.routable,
            "switching": status.switching,
            "attachPending": status.attach_pending,
            "bufferServer": status.buffer_server,
            "clientBacklog": status.client_backlog_packets,
            "clientBacklogBytes": status.client_backlog_bytes,
            "serverBacklog": status.server_backlog_packets,
            "serverBacklogBytes": status.server_backlog_bytes,
            "withheldCredit": status.withheld_credit,
            "reliableBacklogMs": status.reliable_backlog_ms,
            "reliableBacklog": client.queued,
            "usingRelay": client.route.as_ref().map(|r| r.relay),
            "p2pRemote": client.route.as_ref().and_then(|r| r.remote).map(|a| a.to_string()),
            "duplicateServerData": status.duplicate_server_data,
            "connectedSeconds": now.duration_since(client.connected_at).as_secs(),
            "nodeIdleMs": status.node_idle_ms,
        })
    }

    /// SteamFrontend.Status.
    fn status(&self) -> serde_json::Value {
        let publish = self.publish.clone().unwrap_or_default();
        json!({
            "endpoint": self.endpoint,
            "serverId": self.identity.server_id,
            "loggedOn": self.identity.logged_on,
            "secure": self.identity.secure,
            "publicAddress": self.identity.public_ip.map(|ip| ip.to_string()),
            "sessions": self.p2p.len(),
            "clients": self.clients.len(),
            "pendingJoins": self.pending.len(),
            "sentPackets": self.counters.sent,
            "receivedPackets": self.counters.received,
            "queries": self.queries.load(Ordering::Relaxed),
            "authRequests": self.counters.auth_requests,
            "authRejections": self.counters.auth_rejections,
            "serverName": publish.server_name,
            "worldName": publish.map_name,
            "maxPlayers": publish.max_players,
            "tags": publish.tags,
        })
    }
}

/// EP2PSessionError names, as SteamFrontend logs them.
fn p2p_error(error: u8) -> &'static str {
    match error {
        0 => "k_EP2PSessionErrorNone",
        1 => "k_EP2PSessionErrorNotRunningApp",
        2 => "k_EP2PSessionErrorNoRightsToApp",
        3 => "k_EP2PSessionErrorDestinationNotLoggedIn",
        4 => "k_EP2PSessionErrorTimeout",
        5 => "k_EP2PSessionErrorMax",
        _ => "unknown",
    }
}

#[cfg(test)]
mod tests;
