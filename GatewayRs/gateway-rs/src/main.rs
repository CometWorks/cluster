//! gateway-rs: the Rust data plane of the cluster gateway (T-0229, Docs/Gateway/RelayIpc.md).
//! ClusterRegistry (`--relay external`) listens on the relay socket; this process connects to it,
//! receives `Config`, and serves Direct and Steam clients and node links from then on.

use std::path::PathBuf;
use std::sync::Arc;

use gateway_rs::control::{self, Options};
use gateway_rs::ipc;
use gateway_rs::shared::Shared;

fn usage() -> ! {
    eprintln!("usage: gateway-rs [--socket PATH] [--workers N]");
    eprintln!("  --socket   the relay socket ClusterGateway listens on (CLUSTER_RELAY_SOCKET)");
    eprintln!("  --workers  front worker threads (default: cores - 2, 1..8)");
    std::process::exit(64);
}

fn main() {
    // The Steam SDK reads the app id from the process environment at GameServer.Init. Set here,
    // before any thread exists (set_var is only sound single-threaded).
    let app_id = gateway_rs::steam::APP_ID.to_string();
    // SAFETY: no other thread has been started yet.
    unsafe {
        std::env::set_var("SteamAppId", &app_id);
        std::env::set_var("SteamGameId", &app_id);
    }
    let mut socket = std::env::var_os("CLUSTER_RELAY_SOCKET").map(PathBuf::from);
    let mut workers = control::default_workers();
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--socket" => socket = Some(args.next().unwrap_or_else(|| usage()).into()),
            "--workers" => workers = args.next().and_then(|n| n.parse().ok()).filter(|&n| n > 0 && n < 256).unwrap_or_else(|| usage()),
            "--version" => {
                println!("gateway-rs {}", env!("CARGO_PKG_VERSION"));
                return;
            }
            _ => usage(),
        }
    }
    let Some(socket) = socket else { usage() };
    let build = format!("gateway-rs {}", env!("CARGO_PKG_VERSION"));
    eprintln!("{build}: connecting to ClusterGateway at {}", socket.display());
    gateway_rs::trim::start();
    let (tx, rx) = crossbeam_channel::unbounded();
    let sender = ipc::start(socket, build, tx);
    let shared = Arc::new(Shared::new(sender));
    control::run(shared, rx, Options { workers });
}
