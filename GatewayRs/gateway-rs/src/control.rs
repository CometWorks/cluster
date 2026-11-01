//! The control thread: applies what ClusterRegistry sends. It starts the data plane on the first
//! `Config`, and routes every per-client command to the worker that owns the client and every
//! link command to the node-link thread. It holds no packet-path lock.

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use crossbeam_channel::Receiver;
use serde_json::json;

use crate::ipc::{Config, Inbound, SessionUpdate, types};
use crate::node::{NodeCmd, NodeThread};
use crate::shared::Shared;
use crate::steam::{RealSteam, SteamWorker, start_query_thread};
use crate::worker::{Worker, WorkerCmd, WorkerConfig};

pub struct Options {
    pub workers: u16,
}

pub fn default_workers() -> u16 {
    let cores = std::thread::available_parallelism().map_or(1, |n| n.get());
    cores.saturating_sub(2).clamp(1, 8) as u16
}

pub fn run(shared: Arc<Shared>, inbound: Receiver<Inbound>, options: Options) {
    let mut started: Option<Config> = None;
    for message in inbound {
        match message {
            Inbound::Ready { build } => eprintln!("relay IPC: ClusterGateway connected (build {build})"),
            Inbound::Lost { .. } => shared.to_all_workers(|| WorkerCmd::IpcLost),
            Inbound::Config(config) => match &started {
                None => match start(&shared, &config, &options) {
                    Ok(()) => started = Some(*config),
                    Err(error) => {
                        eprintln!("ERROR: gateway-rs could not start its data plane: {error}");
                        std::process::exit(2);
                    }
                },
                Some(running) => {
                    if running.direct != config.direct || running.steam != config.steam {
                        eprintln!("WARNING: Config changes the listeners; gateway-rs keeps its sockets until it is restarted");
                    }
                    shared.to_node(NodeCmd::Config { join_token: config.join_token.clone(), channels: config.node_link_channels });
                }
            },
            Inbound::Paused(paused) => {
                let was = shared.paused.swap(paused, Ordering::AcqRel);
                if was && !paused {
                    shared.to_all_workers(|| WorkerCmd::Flush);
                }
            }
            Inbound::JoinAnswer { correlation, answer } => {
                if let Some(worker) = shared.worker((correlation & 0xFF) as u16) {
                    worker.send(WorkerCmd::JoinAnswer { correlation, answer });
                }
            }
            Inbound::Disconnect(client) => {
                let reason = client.reason.unwrap_or_default();
                route(&shared, client.client_id, WorkerCmd::Disconnect { client_id: client.client_id, session: client.session, reason });
            }
            Inbound::ForceReconnect(client) => {
                let reason = client.reason.unwrap_or_default();
                route(&shared, client.client_id, WorkerCmd::ForceReconnect { client_id: client.client_id, session: client.session, reason });
            }
            Inbound::SessionUpdate { correlation, update } => {
                let (client_id, session) = (update.client_id, update.session);
                let command = WorkerCmd::Update { client_id, session, correlation, update: convert(&shared, &update) };
                if !route(&shared, client_id, command) && correlation != 0 {
                    shared.ipc.send_json(types::SESSION_UPDATE, correlation, &json!({ "ok": false }));
                }
            }
            Inbound::ReplayConnect { client, .. } => {
                route(&shared, client.client_id, WorkerCmd::ReplayConnect { client_id: client.client_id, session: client.session });
            }
            Inbound::ClientSend { client_id, session, channel, delivery, payload } => {
                route(&shared, client_id, WorkerCmd::ClientSend { client_id, session, channel, delivery, payload });
            }
            Inbound::EnsureLink(endpoint) => shared.to_node(NodeCmd::Ensure(endpoint)),
            Inbound::DropLink(endpoint) => shared.to_node(NodeCmd::Drop(endpoint)),
            Inbound::NodeFrame(frame) => {
                let body = match frame.body_bytes() {
                    Ok(body) => body,
                    Err(error) => {
                        eprintln!("WARNING: NodeFrame for client {} has a bad body: {error}", frame.client_id);
                        continue;
                    }
                };
                let command = match frame.kind.as_str() {
                    "attach" => NodeCmd::Attach { endpoint: frame.endpoint, client_id: frame.client_id, body },
                    "detach" => NodeCmd::Detach { endpoint: frame.endpoint, client_id: frame.client_id },
                    "credit" => match (shared.links.id(&frame.endpoint), frame.credit) {
                        (Some(link), Some(credit)) if credit > 0 => NodeCmd::Credit { link, client_id: frame.client_id, credit },
                        _ => continue,
                    },
                    other => {
                        eprintln!("WARNING: unknown NodeFrame kind {other}");
                        continue;
                    }
                };
                shared.to_node(command);
            }
            Inbound::Tunnel { plugin, delivery, endpoint, payload } => {
                shared.to_node(NodeCmd::Tunnel { endpoint, plugin, delivery, payload });
            }
            Inbound::SteamAuth(auth) => match shared.steam.get() {
                Some(steam) => steam.send(WorkerCmd::SteamAuth(auth)),
                None => eprintln!("WARNING: SteamAuth for {} without a Steam frontend", auth.client_id),
            },
            Inbound::SteamPublish(publish) => {
                if let Some(steam) = shared.steam.get() {
                    steam.send(WorkerCmd::SteamPublish(publish));
                }
            }
            Inbound::Status { correlation } => {
                let status = status(&shared);
                shared.ipc.send_json(types::STATUS, correlation, &status);
            }
        }
    }
}

/// Sends a per-client command to the owning worker; false when no worker owns the client.
fn route(shared: &Shared, client_id: u64, command: WorkerCmd) -> bool {
    match shared.route_owner(client_id).and_then(|owner| shared.worker(owner.worker)) {
        Some(worker) => {
            worker.send(command);
            true
        }
        None => false,
    }
}

fn convert(shared: &Shared, update: &SessionUpdate) -> relay::SessionUpdate {
    let link = |value: &Option<Option<String>>| value.as_ref().map(|inner| inner.as_deref().and_then(|e| shared.links.id(e)));
    relay::SessionUpdate {
        link: link(&update.link),
        attached: update.attached,
        routable: update.routable,
        switching: update.switching,
        attach_pending: update.attach_pending,
        buffer_server: update.buffer_server,
        suppress_join: update.suppress_join,
        validation_link: link(&update.validation_link),
        flush_client: update.flush_client,
        flush_server: update.flush_server,
        discard_server: update.discard_server,
        reset_type_table: update.reset_type_table,
    }
}

fn status(shared: &Shared) -> serde_json::Value {
    let timeout = Duration::from_secs(2);
    let mut sessions = Vec::new();
    for worker in shared.workers.get().into_iter().flatten() {
        let (tx, rx) = crossbeam_channel::bounded(1);
        worker.send(WorkerCmd::Status(tx));
        if let Ok(mut part) = rx.recv_timeout(timeout) {
            sessions.append(&mut part);
        }
    }
    let mut nodes = json!({});
    if let Some(node) = shared.node.get() {
        let (tx, rx) = crossbeam_channel::bounded(1);
        node.send(NodeCmd::Status(tx));
        nodes = rx.recv_timeout(timeout).unwrap_or_default();
    }
    let steam = shared.steam.get().and_then(|steam| {
        let (tx, rx) = crossbeam_channel::bounded(1);
        steam.send(WorkerCmd::SteamStatus(tx));
        rx.recv_timeout(timeout).ok()
    });
    json!({
        "sessions": sessions,
        "nodes": nodes,
        "steam": steam,
        "paused": shared.paused(),
        "workers": shared.workers.get().map_or(0, |w| w.len()),
        "ipcSent": shared.ipc.sent.load(Ordering::Relaxed),
        "ipcDropped": shared.ipc.dropped.load(Ordering::Relaxed),
    })
}

fn front_config(config: &Config) -> lnl::Config {
    lnl::Config {
        channels_count: config.node_link_channels.max(1),
        disconnect_timeout_ms: if config.timeouts.client_disconnect_ms > 0 { config.timeouts.client_disconnect_ms as u32 } else { 120_000 },
        reconnect_delay_ms: 500,
        max_connect_attempts: 20,
        ..lnl::Config::default()
    }
}

fn node_config(config: &Config) -> lnl::Config {
    lnl::Config {
        disconnect_timeout_ms: if config.timeouts.node_disconnect_ms > 0 { config.timeouts.node_disconnect_ms as u32 } else { 30_000 },
        ..front_config(config)
    }
}

fn start(shared: &Arc<Shared>, config: &Config, options: &Options) -> std::io::Result<()> {
    let (node, node_mailbox) = NodeThread::new(shared.clone(), node_config(config), config.join_token.clone(), config.node_link_channels)?;
    let _ = shared.node.set(node_mailbox);
    std::thread::Builder::new().name("node-links".into()).spawn(move || node.run())?;

    let mut mailboxes = Vec::new();
    let mut workers = Vec::new();
    let join_hold = Duration::from_secs_f64(config.join_hold_seconds.max(0.0));
    let mut v4_listen = None;
    if config.direct.enabled {
        let address: IpAddr = config.direct.bind.parse().unwrap_or(IpAddr::V4(Ipv4Addr::UNSPECIFIED));
        let v4 = SocketAddr::new(address, config.direct.port);
        // As DirectGateway.Run: the v6 socket follows the v4 bind, so a loopback --listen never opens
        // the accept-all frontend on every v6 interface (Security.md §3.6).
        let v6 = config.direct.dual_stack.then(|| {
            let v6 = if address.is_loopback() { Ipv6Addr::LOCALHOST } else { Ipv6Addr::UNSPECIFIED };
            SocketAddr::new(IpAddr::V6(v6), config.direct.port)
        });
        for index in 0..options.workers.max(1) {
            let worker_config = WorkerConfig {
                index,
                v4: Some(v4),
                v6,
                lnl: front_config(config),
                limits: config.limits(),
                join_hold,
                accept_all: config.accept_all,
            };
            let (worker, mailbox) = Worker::new(worker_config, shared.clone())?;
            mailboxes.push(mailbox);
            workers.push(worker);
        }
        v4_listen = Some((v4, v6));
    }
    // The Steam frontend is one more worker: its clients' commands route by the owner map as usual.
    let mut steam = None;
    if config.steam.enabled {
        let index = mailboxes.len() as u16;
        let bind: Ipv4Addr = config.steam.bind.as_deref().and_then(|b| b.parse().ok()).unwrap_or(Ipv4Addr::UNSPECIFIED);
        let endpoint = SocketAddrV4::new(bind, config.steam.port);
        let (api, query_server) = RealSteam::start(&config.steam).map_err(std::io::Error::other)?;
        let (worker, mailbox) = SteamWorker::new(index, api, shared.clone(), config.limits(), join_hold, endpoint.to_string())?;
        start_query_thread(query_server, endpoint, worker.queries.clone())?;
        let _ = shared.steam.set(mailbox.clone());
        mailboxes.push(mailbox);
        steam = Some(worker);
    }
    let _ = shared.workers.set(mailboxes);
    for (index, worker) in workers.into_iter().enumerate() {
        std::thread::Builder::new().name(format!("front-{index}")).spawn(move || worker.run())?;
    }
    if let Some(worker) = steam {
        std::thread::Builder::new().name("steam".into()).spawn(move || worker.run())?;
        eprintln!("gateway-rs: Steam frontend on {}:{}", config.steam.bind.as_deref().unwrap_or("0.0.0.0"), config.steam.port);
    }
    match v4_listen {
        Some((v4, v6)) => eprintln!(
            "gateway-rs: Direct frontend on {v4}{} with {} workers",
            v6.map(|v| format!(" and {v}")).unwrap_or_default(),
            options.workers.max(1)
        ),
        None => eprintln!("gateway-rs: Direct frontend disabled"),
    }
    Ok(())
}
