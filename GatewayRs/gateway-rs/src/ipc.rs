//! gateway-rs's end of the relay IPC with ClusterRegistry (Docs/Gateway/RelayIpc.md): framing,
//! the typed messages, and a client that connects to the gateway's SOCK_SEQPACKET socket, keeps
//! reconnecting, and exchanges Hello and heartbeats.
//!
//! Frame: `[u16 type LE][u32 correlation LE][body]`; types below 100 carry camelCase JSON.
//! A frame larger than MAXIMUM_MESSAGE travels as `Fragment` messages and is reassembled on the
//! other side (a full global-state snapshot on a live world is several MB).

use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, RwLock};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use crossbeam_channel::Sender;
use serde::{Deserialize, Deserializer, Serialize};
use socket2::{Domain, SockAddr, Socket, Type};

/// 2: `Fragment` (T-0229, 2026-09-24). Version 1 dropped every frame above 256 KiB.
pub const VERSION: u32 = 2;
pub const MAXIMUM_MESSAGE: usize = 256 * 1024;
const HEADER: usize = 6;
/// `Fragment` body header: `[u32 id][u32 total][u32 offset]`.
const FRAGMENT_HEADER: usize = 12;
/// Frames above this travel as fragments of at most this size. A SEQPACKET message must fit the
/// socket's send buffer (net.core.wmem_default, 208 KiB here), so MAXIMUM_MESSAGE itself fails
/// with EMSGSIZE; 64 KiB stays well inside any default.
pub const FRAGMENT_ABOVE: usize = 64 * 1024;
const FRAGMENT_CHUNK: usize = FRAGMENT_ABOVE - HEADER - FRAGMENT_HEADER;
/// The largest frame either side reassembles; a larger `total` is refused.
pub const MAXIMUM_REASSEMBLED: usize = 64 * 1024 * 1024;
/// Frames being reassembled at once (one per concurrent sender is the norm).
const MAXIMUM_PARTIALS: usize = 64;
const HEARTBEAT: Duration = Duration::from_secs(1);
const MISSED_LIMIT: u32 = 3;
const RETRY: Duration = Duration::from_millis(250);

pub mod types {
    pub const HELLO: u16 = 1;
    pub const HEARTBEAT: u16 = 2;
    pub const CONFIG: u16 = 3;
    pub const PAUSED: u16 = 5;
    pub const JOIN_REQUEST: u16 = 10;
    pub const JOIN_ANSWER: u16 = 11;
    pub const CLIENT_MESSAGE: u16 = 12;
    pub const CLIENT_EVENT: u16 = 15;
    pub const DISCONNECT: u16 = 16;
    pub const FORCE_RECONNECT: u16 = 17;
    pub const SESSION_UPDATE: u16 = 18;
    pub const REPLAY_CONNECT: u16 = 19;
    pub const STEAM_AUTH: u16 = 20;
    pub const STEAM_EVENT: u16 = 21;
    pub const STEAM_PUBLISH: u16 = 22;
    pub const STATUS: u16 = 30;
    pub const ENSURE_LINK: u16 = 40;
    pub const DROP_LINK: u16 = 41;
    pub const LINK_STATE: u16 = 42;
    pub const NODE_CONTROL: u16 = 43;
    pub const NODE_FRAME: u16 = 44;
    pub const NODE_GLOBAL: u16 = 100;
    pub const NODE_PLUGIN: u16 = 101;
    pub const CLIENT_SEND: u16 = 102;
    /// `[u32 id][u32 total][u32 offset][chunk]`: bytes `offset..offset+chunk` of one complete frame
    /// of `total` bytes. A sender numbers its fragmented frames and sends each frame's fragments in
    /// order; fragments of different frames may interleave.
    pub const FRAGMENT: u16 = 199;
}

/// Splits a frame larger than FRAGMENT_ABOVE into `Fragment` messages.
pub fn fragments(id: u32, frame: &[u8]) -> Vec<Vec<u8>> {
    let total = frame.len() as u32;
    frame
        .chunks(FRAGMENT_CHUNK)
        .enumerate()
        .map(|(index, chunk)| {
            let mut body = Vec::with_capacity(FRAGMENT_HEADER + chunk.len());
            body.extend_from_slice(&id.to_le_bytes());
            body.extend_from_slice(&total.to_le_bytes());
            body.extend_from_slice(&((index * FRAGMENT_CHUNK) as u32).to_le_bytes());
            body.extend_from_slice(chunk);
            encode(types::FRAGMENT, 0, &body)
        })
        .collect()
}

/// Reassembles `Fragment` bodies into the frames they carry. Any inconsistency drops that frame
/// and says so: a silent loss is what version 1 did.
#[derive(Default)]
pub struct Reassembler {
    partials: std::collections::HashMap<u32, Vec<u8>>,
    total_of: std::collections::HashMap<u32, usize>,
    pub completed: u64,
    pub failed: u64,
}

impl Reassembler {
    /// Returns the complete frame when `body` was its last fragment.
    pub fn push(&mut self, body: &[u8]) -> Option<Vec<u8>> {
        if body.len() < FRAGMENT_HEADER {
            return self.fail(None, format!("fragment of {} bytes has no header", body.len()));
        }
        let id = u32::from_le_bytes(body[0..4].try_into().unwrap());
        let total = u32::from_le_bytes(body[4..8].try_into().unwrap()) as usize;
        let offset = u32::from_le_bytes(body[8..12].try_into().unwrap()) as usize;
        let chunk = &body[FRAGMENT_HEADER..];
        if offset == 0 {
            if self.partials.contains_key(&id) {
                self.fail(Some(id), "restarted before it completed".into());
            }
            if !(HEADER..=MAXIMUM_REASSEMBLED).contains(&total) {
                return self.fail(None, format!("frame {id} of {total} bytes is outside {HEADER}..={MAXIMUM_REASSEMBLED}"));
            }
            if self.partials.len() >= MAXIMUM_PARTIALS {
                let stale: Vec<u32> = self.partials.keys().copied().collect();
                for other in stale {
                    self.fail(Some(other), format!("more than {MAXIMUM_PARTIALS} frames in reassembly"));
                }
            }
            self.partials.insert(id, Vec::with_capacity(total));
            self.total_of.insert(id, total);
        }
        let Some(buffer) = self.partials.get_mut(&id) else {
            return self.fail(None, format!("fragment at {offset} of frame {id} without its start"));
        };
        if self.total_of[&id] != total || buffer.len() != offset || offset + chunk.len() > total {
            let have = buffer.len();
            return self.fail(Some(id), format!("fragment {offset}+{} of {total} does not follow {have}", chunk.len()));
        }
        buffer.extend_from_slice(chunk);
        if buffer.len() < total {
            return None;
        }
        self.total_of.remove(&id);
        self.completed += 1;
        self.partials.remove(&id)
    }

    fn fail(&mut self, id: Option<u32>, why: String) -> Option<Vec<u8>> {
        if let Some(id) = id {
            self.partials.remove(&id);
            self.total_of.remove(&id);
        }
        self.failed += 1;
        eprintln!("WARNING: relay IPC: fragmented frame dropped: {why}");
        None
    }
}

pub fn encode(ty: u16, correlation: u32, body: &[u8]) -> Vec<u8> {
    let mut frame = Vec::with_capacity(HEADER + body.len());
    frame.extend_from_slice(&ty.to_le_bytes());
    frame.extend_from_slice(&correlation.to_le_bytes());
    frame.extend_from_slice(body);
    frame
}

pub fn decode(frame: &[u8]) -> Option<(u16, u32, &[u8])> {
    if frame.len() < HEADER {
        return None;
    }
    let ty = u16::from_le_bytes([frame[0], frame[1]]);
    let correlation = u32::from_le_bytes([frame[2], frame[3], frame[4], frame[5]]);
    Some((ty, correlation, &frame[HEADER..]))
}

// ------------------------------------------------------------------ C# -> Rust

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct Config {
    pub direct: DirectConfig,
    pub steam: SteamConfig,
    pub backlog: BacklogConfig,
    pub credit: CreditConfig,
    pub timeouts: TimeoutConfig,
    pub join_hold_seconds: f64,
    pub join_token: String,
    pub accept_all: bool,
    pub node_link_channels: u8,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct DirectConfig {
    pub enabled: bool,
    pub bind: String,
    pub port: u16,
    pub dual_stack: bool,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamConfig {
    pub enabled: bool,
    pub bind: Option<String>,
    pub port: u16,
    /// The game version string the Steam server advertises (e.g. "1.210.014").
    pub version: Option<serde_json::Value>,
}

impl SteamConfig {
    pub fn version_string(&self) -> String {
        match &self.version {
            Some(serde_json::Value::String(text)) => text.clone(),
            Some(other) => other.to_string(),
            None => String::new(),
        }
    }
}

/// `SteamAuth`: the auth-session primitives of SteamFrontend (BeginAuth, EndAuth, RequestGroupStatus).
#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamAuth {
    /// "begin", "end" or "group".
    pub op: String,
    pub client_id: u64,
    /// The auth ticket from CLIENT_CONNECTED, base64 ("begin").
    pub ticket: Option<String>,
    pub group_id: u64,
}

/// `SteamPublish`: the browser row (SteamFrontend.Publish), re-applied every 5 s by gateway-rs.
#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamPublish {
    pub server_name: String,
    pub map_name: String,
    pub max_players: i32,
    pub password_protected: bool,
    pub tags: String,
    /// The A2S rules vanilla sets beside the tags (MyDedicatedServerBase.ModCount / ModsTotalSize, #1529).
    pub mod_count: i64,
    pub mods_total_size: u64,
    /// Further A2S rules, set as given: the server details' "sc" rules (MyCachedServerItem.SendSettingsToSteam, SE-1076).
    pub rules: Vec<SteamRule>,
    pub players: Vec<SteamPlayer>,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamRule {
    pub key: String,
    pub value: String,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamPlayer {
    pub user_id: u64,
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct BacklogConfig {
    pub client_bytes: usize,
    pub client_age_ms: u64,
    pub server_bytes: usize,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct CreditConfig {
    pub join_window: i32,
    pub withhold_above: usize,
    pub release_below: usize,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct TimeoutConfig {
    pub client_disconnect_ms: u64,
    pub node_disconnect_ms: u64,
}

impl Config {
    /// The relay core's limits; a zero (absent) field keeps the C# constant.
    pub fn limits(&self) -> relay::Limits {
        let mut limits = relay::Limits::default();
        if self.backlog.client_bytes > 0 {
            limits.client_backlog_bytes = self.backlog.client_bytes;
        }
        if self.backlog.client_age_ms > 0 {
            limits.client_backlog_age = Duration::from_millis(self.backlog.client_age_ms);
        }
        if self.backlog.server_bytes > 0 {
            limits.server_backlog_bytes = self.backlog.server_bytes;
        }
        if self.credit.withhold_above > 0 {
            limits.withhold_above = self.credit.withhold_above;
        }
        if self.credit.release_below > 0 {
            limits.release_below = self.credit.release_below;
        }
        limits
    }
}

/// Distinguishes an absent field (`None`) from an explicit null (`Some(None)`).
fn present<'de, D: Deserializer<'de>, T: Deserialize<'de>>(d: D) -> Result<Option<Option<T>>, D::Error> {
    Option::<T>::deserialize(d).map(Some)
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct SessionUpdate {
    pub client_id: u64,
    pub session: u64,
    #[serde(deserialize_with = "present")]
    pub link: Option<Option<String>>,
    pub attached: Option<bool>,
    pub routable: Option<bool>,
    pub switching: Option<bool>,
    pub attach_pending: Option<bool>,
    pub buffer_server: Option<bool>,
    pub suppress_join: Option<bool>,
    #[serde(deserialize_with = "present")]
    pub validation_link: Option<Option<String>>,
    pub flush_client: bool,
    pub flush_server: bool,
    pub discard_server: bool,
    pub reset_type_table: bool,
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct JoinAnswer {
    pub decision: String,
    #[serde(default)]
    pub reason: Option<String>,
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ClientRef {
    pub client_id: u64,
    pub session: u64,
    #[serde(default)]
    pub reason: Option<String>,
}

#[derive(Debug, Clone, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct NodeFrame {
    pub endpoint: String,
    pub kind: String,
    pub client_id: u64,
    #[serde(default)]
    pub body: Option<String>,
    #[serde(default)]
    pub credit: Option<i32>,
}

impl NodeFrame {
    pub fn body_bytes(&self) -> Result<Vec<u8>, base64::DecodeError> {
        self.body.as_deref().map_or(Ok(Vec::new()), |text| BASE64.decode(text))
    }
}

/// Everything ClusterRegistry sends, decoded.
#[derive(Debug, Clone, PartialEq)]
pub enum Inbound {
    /// The IPC (re)connected and ClusterRegistry said Hello.
    Ready { build: String },
    /// The IPC is gone; sessions keep their current state until it is back.
    Lost { reason: String },
    Config(Box<Config>),
    Paused(bool),
    JoinAnswer { correlation: u32, answer: JoinAnswer },
    Disconnect(ClientRef),
    ForceReconnect(ClientRef),
    SessionUpdate { correlation: u32, update: SessionUpdate },
    ReplayConnect { correlation: u32, client: ClientRef },
    Status { correlation: u32 },
    EnsureLink(String),
    DropLink(String),
    NodeFrame(NodeFrame),
    /// Global (kind 7) or Plugin (kind 8) frame toward `endpoint`, sent with LiteNetLib `delivery`.
    Tunnel { plugin: bool, delivery: u8, endpoint: String, payload: Vec<u8> },
    ClientSend { client_id: u64, session: u64, channel: u8, delivery: u8, payload: Vec<u8> },
    SteamAuth(SteamAuth),
    SteamPublish(Box<SteamPublish>),
}

#[derive(Debug)]
pub enum DecodeError {
    Json(serde_json::Error),
    Binary(&'static str),
    Unknown(u16),
}

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DecodeError::Json(error) => write!(f, "bad JSON: {error}"),
            DecodeError::Binary(what) => write!(f, "bad binary body: {what}"),
            DecodeError::Unknown(ty) => write!(f, "unknown message type {ty}"),
        }
    }
}

#[derive(Deserialize)]
struct Endpoint {
    endpoint: String,
}

#[derive(Deserialize)]
struct PausedBody {
    paused: bool,
}

/// Decodes one message from ClusterRegistry (not Hello/Heartbeat, which the client consumes).
pub fn parse(ty: u16, correlation: u32, body: &[u8]) -> Result<Inbound, DecodeError> {
    use types::*;
    fn json<'a, T: Deserialize<'a>>(body: &'a [u8]) -> Result<T, DecodeError> {
        serde_json::from_slice(body).map_err(DecodeError::Json)
    }
    Ok(match ty {
        CONFIG => Inbound::Config(Box::new(json(body)?)),
        PAUSED => Inbound::Paused(json::<PausedBody>(body)?.paused),
        JOIN_ANSWER => Inbound::JoinAnswer { correlation, answer: json(body)? },
        DISCONNECT => Inbound::Disconnect(json(body)?),
        FORCE_RECONNECT => Inbound::ForceReconnect(json(body)?),
        SESSION_UPDATE => Inbound::SessionUpdate { correlation, update: json(body)? },
        REPLAY_CONNECT => Inbound::ReplayConnect { correlation, client: json(body)? },
        STATUS => Inbound::Status { correlation },
        STEAM_AUTH => Inbound::SteamAuth(json(body)?),
        STEAM_PUBLISH => Inbound::SteamPublish(Box::new(json(body)?)),
        ENSURE_LINK => Inbound::EnsureLink(json::<Endpoint>(body)?.endpoint),
        DROP_LINK => Inbound::DropLink(json::<Endpoint>(body)?.endpoint),
        NODE_FRAME => Inbound::NodeFrame(json(body)?),
        NODE_GLOBAL | NODE_PLUGIN => {
            let (delivery, endpoint, payload) = split_tunnel(body).ok_or(DecodeError::Binary("tunnel frame"))?;
            Inbound::Tunnel { plugin: ty == NODE_PLUGIN, delivery, endpoint, payload: payload.to_vec() }
        }
        CLIENT_SEND => {
            if body.len() < 18 {
                return Err(DecodeError::Binary("ClientSend shorter than 18 bytes"));
            }
            Inbound::ClientSend {
                client_id: u64::from_le_bytes(body[0..8].try_into().unwrap()),
                session: u64::from_le_bytes(body[8..16].try_into().unwrap()),
                channel: body[16],
                delivery: body[17],
                payload: body[18..].to_vec(),
            }
        }
        other => return Err(DecodeError::Unknown(other)),
    })
}

/// `[u8 delivery][u16 endpointLen][endpoint utf8][payload]`.
fn split_tunnel(body: &[u8]) -> Option<(u8, String, &[u8])> {
    let delivery = *body.first()?;
    let length = u16::from_le_bytes([*body.get(1)?, *body.get(2)?]) as usize;
    let endpoint = std::str::from_utf8(body.get(3..3 + length)?).ok()?.to_owned();
    Some((delivery, endpoint, &body[3 + length..]))
}

pub fn tunnel_body(delivery: u8, endpoint: &str, payload: &[u8]) -> Vec<u8> {
    let mut body = Vec::with_capacity(3 + endpoint.len() + payload.len());
    body.push(delivery);
    body.extend_from_slice(&(endpoint.len() as u16).to_le_bytes());
    body.extend_from_slice(endpoint.as_bytes());
    body.extend_from_slice(payload);
    body
}

// ------------------------------------------------------------------ Rust -> C#

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JoinRequest<'a> {
    pub client_id: u64,
    pub session: u64,
    pub service: &'a str,
    pub name: Option<&'a str>,
    pub remote: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientMessage {
    pub client_id: u64,
    pub session: u64,
    pub message_id: u8,
    pub channel: u8,
    pub packet: String,
}

impl ClientMessage {
    pub fn new(client_id: u64, session: u64, message_id: u8, channel: u8, packet: &[u8]) -> Self {
        Self { client_id, session, message_id, channel, packet: BASE64.encode(packet) }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientEvent<'a> {
    pub client_id: u64,
    pub session: u64,
    pub event: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkState<'a> {
    pub endpoint: &'a str,
    pub connected: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NodeControl<'a> {
    pub endpoint: &'a str,
    pub kind: &'a str,
    pub client_id: u64,
    pub session: u64,
}

/// `SteamEvent` (Rust -> C#): `ticket` carries the raw EAuthSessionResponse (`response`) and the
/// licence owner; `ticketRejected` the EBeginAuthSessionResult of a failed BeginAuthSession;
/// `group` the GSClientGroupStatus answer.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamEvent {
    pub kind: &'static str,
    pub client_id: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub response: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub owner: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub member: Option<bool>,
}

// ------------------------------------------------------------------ the connection

/// A cheap, clonable handle for sending to ClusterRegistry from any thread. SOCK_SEQPACKET sends
/// are atomic per message, so senders need no lock beyond the slot that holds the live socket.
#[derive(Clone, Default)]
pub struct IpcSender {
    slot: Arc<RwLock<Option<Arc<Socket>>>>,
    next_correlation: Arc<AtomicU32>,
    next_fragmented: Arc<AtomicU32>,
    pub sent: Arc<AtomicU64>,
    pub dropped: Arc<AtomicU64>,
}

impl IpcSender {
    pub fn connected(&self) -> bool {
        self.slot.read().unwrap().is_some()
    }

    pub fn correlation(&self) -> u32 {
        loop {
            let value = self.next_correlation.fetch_add(1, Ordering::Relaxed).wrapping_add(1);
            if value != 0 {
                return value;
            }
        }
    }

    /// Sends one message. Without a connection the message is dropped (and counted): nothing sent
    /// to a lost ClusterRegistry could be acted on, and it restates its state on reconnect.
    pub fn send_raw(&self, ty: u16, correlation: u32, body: &[u8]) -> bool {
        let socket = self.slot.read().unwrap().clone();
        let Some(socket) = socket else {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            return false;
        };
        let frame = encode(ty, correlation, body);
        if frame.len() > FRAGMENT_ABOVE {
            if frame.len() > MAXIMUM_REASSEMBLED {
                eprintln!("ERROR: relay IPC message type {ty} of {} bytes exceeds {MAXIMUM_REASSEMBLED}; dropped", frame.len());
                self.dropped.fetch_add(1, Ordering::Relaxed);
                return false;
            }
            let id = self.next_fragmented.fetch_add(1, Ordering::Relaxed);
            for fragment in fragments(id, &frame) {
                if socket.send(&fragment).is_err() {
                    self.dropped.fetch_add(1, Ordering::Relaxed);
                    return false;
                }
            }
            self.sent.fetch_add(1, Ordering::Relaxed);
            return true;
        }
        match socket.send(&frame) {
            Ok(_) => {
                self.sent.fetch_add(1, Ordering::Relaxed);
                true
            }
            Err(_) => {
                self.dropped.fetch_add(1, Ordering::Relaxed);
                false
            }
        }
    }

    pub fn send_json(&self, ty: u16, correlation: u32, body: &impl Serialize) -> bool {
        match serde_json::to_vec(body) {
            Ok(bytes) => self.send_raw(ty, correlation, &bytes),
            Err(error) => {
                eprintln!("WARNING: relay IPC message type {ty} failed to serialise: {error}");
                false
            }
        }
    }

    fn set(&self, socket: Option<Arc<Socket>>) {
        *self.slot.write().unwrap() = socket;
    }
}

/// Starts the connection thread: connect (retrying every 250 ms), Hello, heartbeats, receive.
/// Decoded messages, `Ready` and `Lost` go to `inbound`.
pub fn start(path: PathBuf, build: String, inbound: Sender<Inbound>) -> IpcSender {
    let sender = IpcSender::default();
    let handle = sender.clone();
    std::thread::Builder::new()
        .name("relay-ipc".into())
        .spawn(move || run(&path, &build, &handle, &inbound))
        .expect("spawn relay-ipc thread");
    sender
}

fn connect(path: &Path) -> io::Result<Socket> {
    let socket = Socket::new(Domain::UNIX, Type::SEQPACKET, None)?;
    socket.connect(&SockAddr::unix(path)?)?;
    socket.set_read_timeout(Some(HEARTBEAT))?;
    Ok(socket)
}

fn run(path: &Path, build: &str, sender: &IpcSender, inbound: &Sender<Inbound>) {
    let mut waiting_logged = false;
    loop {
        let socket = match connect(path) {
            Ok(socket) => Arc::new(socket),
            Err(error) => {
                if !waiting_logged {
                    eprintln!("relay IPC: waiting for ClusterGateway at {} ({error})", path.display());
                    waiting_logged = true;
                }
                std::thread::sleep(RETRY);
                continue;
            }
        };
        waiting_logged = false;
        sender.set(Some(socket.clone()));
        let hello = serde_json::json!({"version": VERSION, "role": "relay", "pid": std::process::id(), "build": build});
        sender.send_json(types::HELLO, 0, &hello);
        let reason = session(&socket, sender, inbound);
        sender.set(None);
        eprintln!("WARNING: relay IPC: ClusterGateway lost ({reason})");
        if inbound.send(Inbound::Lost { reason }).is_err() {
            return;
        }
        std::thread::sleep(RETRY);
    }
}

/// One connection's life; returns why it ended.
fn session(socket: &Socket, sender: &IpcSender, inbound: &Sender<Inbound>) -> String {
    let mut buffer = vec![std::mem::MaybeUninit::<u8>::uninit(); MAXIMUM_MESSAGE];
    let mut last_received = Instant::now();
    let mut last_beat = Instant::now();
    let mut seq = 0u64;
    let mut ready = false;
    let mut reassembler = Reassembler::default();
    let mut assembled: Vec<u8>;
    loop {
        if last_beat.elapsed() >= HEARTBEAT {
            seq += 1;
            sender.send_json(types::HEARTBEAT, 0, &serde_json::json!({ "seq": seq }));
            last_beat = Instant::now();
        }
        if last_received.elapsed() > HEARTBEAT * MISSED_LIMIT {
            return format!("no message for {MISSED_LIMIT} heartbeats");
        }
        let read = match socket.recv(&mut buffer) {
            Ok(0) => return "ClusterGateway closed the socket".into(),
            Ok(read) => read,
            Err(error) if matches!(error.kind(), io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::Interrupted) => continue,
            Err(error) => return error.to_string(),
        };
        last_received = Instant::now();
        // SAFETY: recv initialised the first `read` bytes.
        let mut frame = unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>(), read) };
        let Some((ty, _, body)) = decode(frame) else {
            return format!("short message ({read} bytes)");
        };
        if ty == types::FRAGMENT {
            match reassembler.push(body) {
                Some(complete) => {
                    assembled = complete;
                    frame = &assembled;
                }
                None => continue,
            }
        }
        let Some((ty, correlation, body)) = decode(frame) else {
            return format!("short reassembled message ({} bytes)", frame.len());
        };
        let message = match ty {
            types::HELLO => {
                #[derive(Deserialize)]
                struct Hello {
                    version: u32,
                    #[serde(default)]
                    build: String,
                }
                match serde_json::from_slice::<Hello>(body) {
                    Ok(hello) if hello.version == VERSION => {
                        ready = true;
                        Inbound::Ready { build: hello.build }
                    }
                    Ok(hello) => return format!("ClusterGateway speaks IPC version {}, this relay {VERSION}", hello.version),
                    Err(error) => return format!("bad Hello: {error}"),
                }
            }
            types::HEARTBEAT => continue,
            _ if !ready => return format!("message type {ty} before Hello"),
            _ => match parse(ty, correlation, body) {
                Ok(message) => message,
                Err(DecodeError::Unknown(ty)) => {
                    eprintln!("relay IPC: ignoring unknown message type {ty}");
                    continue;
                }
                Err(error) if ty == types::CONFIG => {
                    // Without a Config there is no data plane: fail loudly so the supervisor sees it.
                    eprintln!("ERROR: relay IPC: unusable Config: {error}");
                    std::process::exit(2);
                }
                Err(error) => {
                    eprintln!("WARNING: relay IPC: message type {ty}: {error}");
                    continue;
                }
            },
        };
        if inbound.send(message).is_err() {
            return "relay shutting down".into();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_round_trip() {
        let frame = encode(types::SESSION_UPDATE, 42, b"{}");
        assert_eq!(decode(&frame), Some((types::SESSION_UPDATE, 42, &b"{}"[..])));
        assert_eq!(decode(&[1, 0, 0]), None);
    }

    #[test]
    fn fragments_reassemble_and_interleave() {
        let big_a: Vec<u8> = encode(types::NODE_GLOBAL, 0, &(0..3 * MAXIMUM_MESSAGE + 17).map(|i| (i % 251) as u8).collect::<Vec<_>>());
        let big_b: Vec<u8> = encode(types::NODE_PLUGIN, 5, &vec![7u8; MAXIMUM_MESSAGE + 1]);
        let a = fragments(1, &big_a);
        let b = fragments(2, &big_b);
        assert_eq!(a.len(), big_a.len().div_ceil(FRAGMENT_CHUNK));
        assert!(b.len() >= 2 && a.iter().chain(&b).all(|f| f.len() <= FRAGMENT_ABOVE));
        let mut r = Reassembler::default();
        let mut out = Vec::new();
        // interleaved: a0 b0 a1 b1 ... then the rest of a (b is the shorter frame)
        let mut order = Vec::new();
        for i in 0..a.len() {
            order.push(&a[i]);
            if i < b.len() {
                order.push(&b[i]);
            }
        }
        for f in order {
            let (ty, _, body) = decode(f).unwrap();
            assert_eq!(ty, types::FRAGMENT);
            out.extend(r.push(body));
        }
        assert_eq!(out, vec![big_b.clone(), big_a.clone()]);
        assert_eq!((r.completed, r.failed), (2, 0));
        // a missing middle fragment drops that frame, loudly, and the next frame still passes
        let (_, _, first) = decode(&a[0]).unwrap();
        let (_, _, third) = decode(&a[2]).unwrap();
        assert_eq!(r.push(first), None);
        assert_eq!(r.push(third), None);
        assert_eq!(r.failed, 1);
        for f in &b {
            let (_, _, body) = decode(f).unwrap();
            out.push(r.push(body).unwrap_or_default());
        }
        assert_eq!(out.last().unwrap(), &big_b);
        // an oversized total is refused
        let mut huge = Vec::new();
        huge.extend_from_slice(&9u32.to_le_bytes());
        huge.extend_from_slice(&((MAXIMUM_REASSEMBLED + 1) as u32).to_le_bytes());
        huge.extend_from_slice(&0u32.to_le_bytes());
        assert_eq!(r.push(&huge), None);
    }

    #[test]
    fn session_update_tells_null_from_absent() {
        // The shape PushRouting sends: full state, null link and validation link.
        let body = br#"{"clientId":7,"session":3,"link":null,"attached":false,"routable":false,"switching":false,"attachPending":true,
            "bufferServer":false,"suppressJoin":false,"validationLink":null,"flushClient":false,"flushServer":true,"discardServer":false}"#;
        let Inbound::SessionUpdate { correlation, update } = parse(types::SESSION_UPDATE, 0, body).unwrap() else { panic!() };
        assert_eq!(correlation, 0);
        assert_eq!(update.link, Some(None));
        assert_eq!(update.validation_link, Some(None));
        assert!(update.flush_server);
        assert_eq!(update.attach_pending, Some(true));
        let Inbound::SessionUpdate { update, .. } =
            parse(types::SESSION_UPDATE, 0, br#"{"clientId":7,"session":3,"link":"10.0.0.5:27016"}"#).unwrap()
        else {
            panic!()
        };
        assert_eq!(update.link, Some(Some("10.0.0.5:27016".into())));
        assert_eq!(update.validation_link, None);
        assert_eq!(update.attached, None);
        assert_eq!(update.attach_pending, None, "absent from an older ClusterRegistry: unchanged");
    }

    #[test]
    fn config_as_the_adapter_sends_it() {
        let body = br#"{"direct":{"enabled":true,"bind":"0.0.0.0","port":27116,"dualStack":true},
            "steam":{"enabled":false,"bind":null,"port":0,"version":"1.210.014"},
            "backlog":{"clientBytes":4194304,"clientAgeMs":10000,"serverBytes":1048576},
            "credit":{"joinWindow":4194304,"withholdAbove":4096,"releaseBelow":3072},
            "timeouts":{"clientDisconnectMs":120000,"nodeDisconnectMs":30000},
            "joinHoldSeconds":120,"joinToken":"t","acceptAll":true,"nodeLinkChannels":16}"#;
        let Inbound::Config(config) = parse(types::CONFIG, 0, body).unwrap() else { panic!() };
        assert_eq!(config.direct.port, 27116);
        assert_eq!(config.node_link_channels, 16);
        assert_eq!(config.limits().client_backlog_age, Duration::from_secs(10));
    }

    #[test]
    fn binary_bodies() {
        let tunnel = tunnel_body(0, "[::1]:27016", b"{\"a\":1}");
        let Inbound::Tunnel { plugin, delivery, endpoint, payload } = parse(types::NODE_PLUGIN, 0, &tunnel).unwrap() else { panic!() };
        assert!(plugin);
        assert_eq!(delivery, 0);
        assert_eq!(endpoint, "[::1]:27016");
        assert_eq!(payload, b"{\"a\":1}");
        let mut send = Vec::new();
        send.extend_from_slice(&7u64.to_le_bytes());
        send.extend_from_slice(&3u64.to_le_bytes());
        send.extend_from_slice(&[0, 2, 9, 9]);
        assert_eq!(
            parse(types::CLIENT_SEND, 0, &send).unwrap(),
            Inbound::ClientSend { client_id: 7, session: 3, channel: 0, delivery: 2, payload: vec![9, 9] }
        );
        assert!(parse(types::CLIENT_SEND, 0, &send[..17]).is_err());
        assert!(matches!(parse(99, 0, b"{}"), Err(DecodeError::Unknown(99))));
    }

    #[test]
    fn steam_messages() {
        let Inbound::SteamAuth(auth) = parse(types::STEAM_AUTH, 0, br#"{"op":"begin","clientId":76561198000000001,"ticket":"AQID"}"#).unwrap()
        else {
            panic!()
        };
        assert_eq!((auth.op.as_str(), auth.client_id, auth.ticket.as_deref()), ("begin", 76561198000000001, Some("AQID")));
        let body = br#"{"serverName":"s","mapName":"m","maxPlayers":16,"passwordProtected":false,"tags":"a,b",
            "rules":[{"key":"sc","value":"3"},{"key":"sc0","value":"AQID"}],"players":[{"userId":5,"name":"five"}]}"#;
        let Inbound::SteamPublish(publish) = parse(types::STEAM_PUBLISH, 0, body).unwrap() else { panic!() };
        assert_eq!(publish.players, vec![SteamPlayer { user_id: 5, name: "five".into() }]);
        assert_eq!(publish.rules[1], SteamRule { key: "sc0".into(), value: "AQID".into() });
        assert_eq!(publish.max_players, 16);
    }

    #[test]
    fn connects_says_hello_and_reports_loss() {
        let dir = std::env::temp_dir().join(format!("relay-ipc-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("gateway-relay.sock");
        let _ = std::fs::remove_file(&path);
        let listener = Socket::new(Domain::UNIX, Type::SEQPACKET, None).unwrap();
        listener.bind(&SockAddr::unix(&path).unwrap()).unwrap();
        listener.listen(1).unwrap();
        let (tx, rx) = crossbeam_channel::unbounded();
        let sender = start(path.clone(), "test".into(), tx);
        let (peer, _) = listener.accept().unwrap();
        let mut buffer = [std::mem::MaybeUninit::<u8>::uninit(); 4096];
        let read = peer.recv(&mut buffer).unwrap();
        let frame: Vec<u8> = buffer[..read].iter().map(|b| unsafe { b.assume_init() }).collect();
        let (ty, _, body) = decode(&frame).unwrap();
        assert_eq!(ty, types::HELLO);
        let hello: serde_json::Value = serde_json::from_slice(body).unwrap();
        assert_eq!(hello["role"], "relay");
        peer.send(&encode(types::HELLO, 0, br#"{"version":2,"role":"registry","build":"b"}"#)).unwrap();
        assert_eq!(rx.recv_timeout(Duration::from_secs(2)).unwrap(), Inbound::Ready { build: "b".into() });
        peer.send(&encode(types::PAUSED, 0, br#"{"paused":true}"#)).unwrap();
        assert_eq!(rx.recv_timeout(Duration::from_secs(2)).unwrap(), Inbound::Paused(true));
        assert!(sender.connected());
        drop(peer);
        assert!(matches!(rx.recv_timeout(Duration::from_secs(3)).unwrap(), Inbound::Lost { .. }));
        assert!(!sender.connected());
        let (_again, _) = listener.accept().unwrap();
        let _ = std::fs::remove_dir_all(&dir);
    }
}
