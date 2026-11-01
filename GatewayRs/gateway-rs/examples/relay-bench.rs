//! Relay bench (T-0229 milestone 2 and §8.3): the same load through ClusterRegistry's in-process
//! relay (`--relay internal`) and through gateway-rs (`--relay external`), measured the same way.
//!
//! One fake node (an lnl server that acknowledges attaches and echoes every relayed client packet
//! back to its client, padded to `--reply-size`) and `--clients` Direct Transport clients, each on
//! its own UDP socket, sending `--rate` packets a second. Every packet carries its send time; the
//! echo's arrival gives one round trip client -> gateway -> node -> gateway -> client. The node and
//! the clients are Rust (no GC) and identical in both modes, so a difference is the relay's.
//!
//! Reported: round-trip percentiles, trips over 5/10/50 ms, the longest time no echo arrived at
//! all (a relay stall shows up here even when few packets were in flight), losses, and the CPU the
//! gateway processes used (per thread for gateway-rs).
//!
//! cargo run --release --example relay-bench -- --gateway-dll <ClusterRegistry.dll> --mode external \
//!     --gateway-rs target/release/gateway-rs --clients 74 --rate 60 --seconds 60
//!
//! Soak (Viktor, 2026-09-24: gateway-rs must not grow its RSS steadily over long runs): `--churn S`
//! replaces every client about every S seconds by a NEW client id on a new socket, alternating a
//! graceful disconnect with an abrupt drop (the gateway's timeout path), so per-client state is
//! created and must be freed all the time; `--rss-log FILE` writes one TSV row every `--rss-every`
//! seconds with both gateway processes' VmRSS. With `--rss-log` no round trips are kept in memory,
//! so the bench's own footprint stays flat however long it runs.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use gateway_rs::nodelink::{self, Envelope};
use lnl::udp::UdpEndpoint;
use lnl::{DeliveryMethod, Event, PeerId};

struct Args {
    gateway_dll: PathBuf,
    gateway_rs: Option<PathBuf>,
    mode: String,
    clients: usize,
    rate: f64,
    size: usize,
    reply_size: usize,
    warmup: u64,
    seconds: u64,
    workers: u16,
    client_threads: usize,
    dir: PathBuf,
    admission: Option<PathBuf>,
    churn: f64,
    rss_log: Option<PathBuf>,
    rss_every: u64,
}

fn parse() -> Args {
    let mut args = Args {
        gateway_dll: PathBuf::new(),
        gateway_rs: None,
        mode: "internal".into(),
        clients: 74,
        rate: 60.0,
        size: 200,
        reply_size: 1000,
        warmup: 10,
        seconds: 60,
        workers: 4,
        client_threads: 4,
        dir: std::env::temp_dir().join(format!("relay-bench-{}", std::process::id())),
        admission: None,
        churn: 0.0,
        rss_log: None,
        rss_every: 60,
    };
    let mut it = std::env::args().skip(1);
    while let Some(flag) = it.next() {
        let value = it.next().unwrap_or_else(|| panic!("{flag} needs a value"));
        match flag.as_str() {
            "--gateway-dll" => args.gateway_dll = value.into(),
            "--gateway-rs" => args.gateway_rs = Some(value.into()),
            "--mode" => args.mode = value,
            "--clients" => args.clients = value.parse().unwrap(),
            "--rate" => args.rate = value.parse().unwrap(),
            "--size" => args.size = value.parse().unwrap(),
            "--reply-size" => args.reply_size = value.parse().unwrap(),
            "--warmup" => args.warmup = value.parse().unwrap(),
            "--seconds" => args.seconds = value.parse().unwrap(),
            "--workers" => args.workers = value.parse().unwrap(),
            "--client-threads" => args.client_threads = value.parse().unwrap(),
            "--dir" => args.dir = value.into(),
            "--admission" => args.admission = Some(value.into()),
            "--churn" => args.churn = value.parse().unwrap(),
            "--rss-log" => args.rss_log = Some(value.into()),
            "--rss-every" => args.rss_every = value.parse().unwrap(),
            other => panic!("unknown flag {other}"),
        }
    }
    assert!(args.mode == "internal" || args.mode == "external", "--mode internal|external");
    assert!(args.mode == "internal" || args.gateway_rs.is_some(), "--mode external needs --gateway-rs");
    args
}

fn lnl_config() -> lnl::Config {
    lnl::Config { channels_count: 16, disconnect_timeout_ms: 30_000, ..lnl::Config::default() }
}

fn free_port() -> u16 {
    std::net::UdpSocket::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn free_tcp_port() -> u16 {
    std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

/// Game frame without CRC: message id, then the body.
fn frame(message_id: u8, body: &[u8]) -> Vec<u8> {
    let mut frame = Vec::with_capacity(10 + body.len());
    frame.extend_from_slice(&[0xCE, 0, 0, 0, 0, 0, 0, 1, message_id, 0]);
    frame.extend_from_slice(body);
    frame
}

/// Tail of every bench packet: client id, sequence, send time (ns since the bench epoch).
const STAMP: usize = 24;
const REQUEST: u8 = 9;
const REPLY: u8 = 21;

struct Kill(Child);
impl Drop for Kill {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

// ------------------------------------------------------------------ the node

fn node(mut endpoint: UdpEndpoint, reply_size: usize, stop: Arc<AtomicBool>, attached: Arc<AtomicU64>, datagrams: Arc<AtomicU64>) {
    let mut gateway: Option<PeerId> = None;
    let mut out = Vec::with_capacity(reply_size + 64);
    while !stop.load(Ordering::Relaxed) {
        let _ = endpoint.pump();
        datagrams.store(endpoint.received, Ordering::Relaxed);
        let mut idle = true;
        while let Some(event) = endpoint.next_event() {
            idle = false;
            match event {
                Event::ConnectionRequest { request, .. } => {
                    let now = Instant::now();
                    gateway = endpoint.with(|e, o| e.accept(now, request, o));
                }
                Event::Receive { packet, .. } => {
                    let Some(peer) = gateway else {
                        endpoint.recycle(packet);
                        continue;
                    };
                    match nodelink::decode(packet.data()) {
                        Ok(Envelope::Attach { client_id, .. }) => {
                            attached.fetch_add(1, Ordering::Relaxed);
                            let ack = nodelink::encode(&Envelope::AttachAck { client_id }).unwrap();
                            let _ = endpoint.with(|e, _| e.send(peer, &ack, (client_id % 16) as u8, DeliveryMethod::ReliableOrdered));
                        }
                        Ok(Envelope::Relay { client_id, channel: 2, payload }) if payload.len() >= 10 + STAMP && payload[8] == REQUEST => {
                            let stamp = &payload[10..10 + STAMP];
                            let mut body = stamp.to_vec();
                            body.resize(reply_size.max(STAMP), 0);
                            let reply = frame(REPLY, &body);
                            out.clear();
                            nodelink::encode_into(&mut out, &Envelope::Relay { client_id, channel: 2, payload: &reply }).unwrap();
                            let _ = endpoint.with(|e, _| e.send(peer, &out, (client_id % 16) as u8, DeliveryMethod::ReliableOrdered));
                        }
                        _ => {}
                    }
                    endpoint.recycle(packet);
                }
                _ => {}
            }
        }
        if idle {
            std::thread::sleep(Duration::from_micros(100));
        }
    }
}

// ------------------------------------------------------------------ the clients

struct Client {
    id: u64,
    index: u64,
    generation: u64,
    endpoint: UdpEndpoint,
    peer: Option<PeerId>,
    seq: u64,
    next_send: Instant,
    /// --churn: when this client is replaced by a new one.
    replace_at: Option<Instant>,
}

/// Client ids: generation 0 is 1_000_000 + index, as before; every replacement gets a fresh id.
fn client_id(index: u64, generation: u64) -> u64 {
    1_000_000 + generation * 100_000 + index
}

fn connect(client: &mut Client, gateway: SocketAddr) {
    let mut handshake = Vec::new();
    nodelink::put_string(&mut handshake, nodelink::PROTOCOL_KEY);
    handshake.extend_from_slice(&client.id.to_le_bytes());
    nodelink::put_string(&mut handshake, &format!("bench-{}", client.id));
    let now = Instant::now();
    client.endpoint.with(|e, o| e.connect(now, gateway, &handshake, o)).unwrap();
}

#[derive(Default)]
struct Samples {
    rtt_us: Vec<u32>,
    arrivals_ms: Vec<u32>,
    sent: u64,
    received: u64,
}

#[allow(clippy::too_many_arguments)]
fn clients(
    mut clients: Vec<Client>,
    gateway: SocketAddr,
    epoch: Instant,
    rate: f64,
    size: usize,
    measure: (Instant, Instant),
    stop: Arc<AtomicBool>,
    connected: Arc<AtomicU64>,
    samples: Arc<Mutex<Samples>>,
    churn: (f64, Arc<AtomicU64>),
    record: bool,
) {
    let interval = Duration::from_secs_f64(1.0 / rate);
    let mut local = Samples::default();
    for client in clients.iter_mut() {
        connect(client, gateway);
    }
    let mut payload = vec![0u8; 1 + 10 + size.max(STAMP)];
    while !stop.load(Ordering::Relaxed) {
        let mut idle = true;
        for client in clients.iter_mut() {
            let _ = client.endpoint.pump();
            while let Some(event) = client.endpoint.next_event() {
                idle = false;
                match event {
                    Event::Connected { peer, .. } => {
                        client.peer = Some(peer);
                        connected.fetch_add(1, Ordering::Relaxed);
                    }
                    Event::Receive { packet, .. } => {
                        let now = Instant::now();
                        let data = packet.data();
                        // [vanilla channel 2][game frame 21][stamp]
                        if data.len() >= 1 + 10 + STAMP && data[0] == 2 && data[9] == REPLY {
                            let stamp = &data[11..11 + STAMP];
                            let sent_ns = u64::from_le_bytes(stamp[16..24].try_into().unwrap());
                            let sent_at = epoch + Duration::from_nanos(sent_ns);
                            if sent_at >= measure.0 && sent_at < measure.1 {
                                local.received += 1;
                                if record {
                                    local.rtt_us.push(now.duration_since(sent_at).as_micros().min(u32::MAX as u128) as u32);
                                }
                            }
                            if record && now >= measure.0 && now < measure.1 {
                                local.arrivals_ms.push(now.duration_since(measure.0).as_millis() as u32);
                            }
                        }
                        client.endpoint.recycle(packet);
                    }
                    Event::Disconnected { reason, .. } => {
                        eprintln!("client {} disconnected: {reason:?}", client.id);
                        client.peer = None;
                    }
                    _ => {}
                }
            }
            let now = Instant::now();
            if let Some(at) = client.replace_at.filter(|&at| now >= at) {
                // Even generations leave gracefully, odd ones vanish (the gateway times them out).
                if let Some(peer) = client.peer.take()
                    && client.generation % 2 == 0
                {
                    client.endpoint.with(|e, o| e.disconnect(peer, &[], o));
                }
                client.generation += 1;
                client.id = client_id(client.index, client.generation);
                client.endpoint = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
                client.replace_at = Some(at + Duration::from_secs_f64(churn.0));
                churn.1.fetch_add(1, Ordering::Relaxed);
                connect(client, gateway);
                continue;
            }
            let Some(peer) = client.peer else { continue };
            if now >= client.next_send {
                idle = false;
                client.next_send += interval;
                if client.next_send < now {
                    client.next_send = now + interval;
                }
                client.seq += 1;
                let stamp_ns = now.duration_since(epoch).as_nanos() as u64;
                payload[0] = 2;
                payload[1..11].copy_from_slice(&[0xCE, 0, 0, 0, 0, 0, 0, 1, REQUEST, 0]);
                payload[11..19].copy_from_slice(&client.id.to_le_bytes());
                payload[19..27].copy_from_slice(&client.seq.to_le_bytes());
                payload[27..35].copy_from_slice(&stamp_ns.to_le_bytes());
                if client.endpoint.with(|e, _| e.send(peer, &payload, 0, DeliveryMethod::ReliableOrdered)).is_ok() && now >= measure.0 && now < measure.1 {
                    local.sent += 1;
                }
                client.endpoint.with(|e, o| e.poll(Instant::now(), o));
            }
        }
        if idle {
            std::thread::sleep(Duration::from_micros(100));
        }
    }
    let mut total = samples.lock().unwrap();
    total.rtt_us.append(&mut local.rtt_us);
    total.arrivals_ms.append(&mut local.arrivals_ms);
    total.sent += local.sent;
    total.received += local.received;
}

// ------------------------------------------------------------------ CPU

const CLOCK_TICKS: f64 = 100.0;

/// (utime + stime) in ticks of one /proc stat line.
fn stat_ticks(text: &str) -> Option<(String, u64)> {
    let open = text.find('(')?;
    let close = text.rfind(')')?;
    let name = text[open + 1..close].to_owned();
    let fields: Vec<&str> = text[close + 2..].split(' ').collect();
    Some((name, fields.get(11)?.parse::<u64>().ok()? + fields.get(12)?.parse::<u64>().ok()?))
}

/// Per-thread CPU ticks of a process, keyed by thread name and id.
fn threads(pid: u32) -> HashMap<String, u64> {
    let mut map = HashMap::new();
    let Ok(dir) = std::fs::read_dir(format!("/proc/{pid}/task")) else { return map };
    for task in dir.flatten() {
        if let Ok(text) = std::fs::read_to_string(task.path().join("stat"))
            && let Some((name, ticks)) = stat_ticks(&text)
        {
            map.insert(format!("{name}#{}", task.file_name().to_string_lossy()), ticks);
        }
    }
    map
}

fn cpu_seconds(before: &HashMap<String, u64>, after: &HashMap<String, u64>) -> (f64, Vec<(String, f64)>) {
    let mut per: Vec<(String, f64)> = after
        .iter()
        .map(|(name, &ticks)| (name.clone(), (ticks - before.get(name).copied().unwrap_or(0)) as f64 / CLOCK_TICKS))
        .filter(|(_, seconds)| *seconds >= 0.05)
        .collect();
    per.sort_by(|a, b| b.1.total_cmp(&a.1));
    (per.iter().map(|(_, s)| s).sum(), per)
}

// ------------------------------------------------------------------ main

fn percentile(sorted: &[u32], p: f64) -> u32 {
    if sorted.is_empty() {
        return 0;
    }
    sorted[((sorted.len() - 1) as f64 * p).round() as usize]
}

/// VmRSS of a process in kB (0 once it is gone).
fn rss_kb(pid: u32) -> u64 {
    std::fs::read_to_string(format!("/proc/{pid}/status"))
        .ok()
        .and_then(|s| s.lines().find(|l| l.starts_with("VmRSS:")).and_then(|l| l.split_whitespace().nth(1)?.parse().ok()))
        .unwrap_or(0)
}

fn main() {
    let args = parse();
    std::fs::create_dir_all(&args.dir).unwrap();
    let epoch = Instant::now();
    let stop = Arc::new(AtomicBool::new(false));

    let node_endpoint = UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap();
    let node_addr = node_endpoint.local_addr().unwrap();
    let attached = Arc::new(AtomicU64::new(0));
    let node_datagrams = Arc::new(AtomicU64::new(0));
    let node_thread = {
        let (stop, attached, reply, datagrams) = (stop.clone(), attached.clone(), args.reply_size, node_datagrams.clone());
        std::thread::spawn(move || node(node_endpoint, reply, stop, attached, datagrams))
    };

    let port = free_port();
    let control = free_tcp_port();
    let socket = args.dir.join("gateway-relay.sock");
    let _ = std::fs::remove_file(&socket);
    let mut command = Command::new("dotnet");
    command
        .arg(&args.gateway_dll)
        .args(["--direct", "--listen", &format!("127.0.0.1:{port}"), "--backend", &node_addr.to_string()])
        .args(["--control", &format!("127.0.0.1:{control}"), "--relay", &args.mode])
        .env("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true")
        .stdout(Stdio::from(std::fs::File::create(args.dir.join("gateway.log")).unwrap()))
        .stderr(Stdio::from(std::fs::File::create(args.dir.join("gateway.err")).unwrap()));
    if args.mode == "external" {
        command.args(["--relay-socket", socket.to_str().unwrap()]);
    }
    // The default member limit is 16; a bench admits every client. Under --churn a client that
    // left holds its slot until its reconnect lease ends, so leave room for those too.
    let admission = args.admission.clone().unwrap_or_else(|| {
        let path = args.dir.join("admission.json");
        let limit = if args.churn > 0.0 { args.clients.max(16) * 50 + 10 } else { args.clients.max(16) + 10 };
        std::fs::write(&path, format!("{{\"memberLimit\": {limit}}}")).unwrap();
        path
    });
    command.args(["--admission", admission.to_str().unwrap()]);
    let gateway = Kill(command.spawn().expect("start ClusterGateway"));
    let mut relay = None;
    if let Some(binary) = args.gateway_rs.as_ref().filter(|_| args.mode == "external") {
        let deadline = Instant::now() + Duration::from_secs(20);
        while !socket.exists() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        relay = Some(Kill(
            Command::new(binary)
                .args(["--socket", socket.to_str().unwrap(), "--workers", &args.workers.to_string()])
                .stdout(Stdio::null())
                .stderr(Stdio::from(std::fs::File::create(args.dir.join("gateway-rs.log")).unwrap()))
                .spawn()
                .expect("start gateway-rs"),
        ));
    }
    let target: SocketAddr = format!("127.0.0.1:{port}").parse().unwrap();
    // Gateway start-up: wait until its port answers (it is bound once Run starts).
    std::thread::sleep(Duration::from_secs(3));

    let connected = Arc::new(AtomicU64::new(0));
    let churns = Arc::new(AtomicU64::new(0));
    let record = args.rss_log.is_none();
    let samples = Arc::new(Mutex::new(Samples::default()));
    let start = Instant::now() + Duration::from_secs(2);
    let measure = (start + Duration::from_secs(args.warmup), start + Duration::from_secs(args.warmup + args.seconds));
    let per_thread = args.clients.div_ceil(args.client_threads.max(1));
    let mut client_threads = Vec::new();
    for chunk in (0..args.clients).collect::<Vec<_>>().chunks(per_thread) {
        let group: Vec<Client> = chunk
            .iter()
            .map(|&i| Client {
                id: client_id(i as u64, 0),
                index: i as u64,
                generation: 0,
                endpoint: UdpEndpoint::bind("127.0.0.1:0".parse().unwrap(), lnl_config()).unwrap(),
                peer: None,
                seq: 0,
                // Spread the clients over one send interval.
                next_send: start + Duration::from_secs_f64(i as f64 / args.clients as f64 / args.rate),
                // ... and their replacements over one churn period.
                replace_at: (args.churn > 0.0)
                    .then(|| start + Duration::from_secs_f64(args.churn * (1.0 + i as f64 / args.clients as f64))),
            })
            .collect();
        let (stop, connected, samples) = (stop.clone(), connected.clone(), samples.clone());
        let (rate, size, churn) = (args.rate, args.size, (args.churn, churns.clone()));
        client_threads.push(std::thread::spawn(move || {
            clients(group, target, epoch, rate, size, measure, stop, connected, samples, churn, record)
        }));
    }

    while Instant::now() < measure.0 {
        std::thread::sleep(Duration::from_millis(100));
    }
    let gateway_pid = gateway.0.id();
    let relay_pid = relay.as_ref().map(|r| r.0.id());
    let rss_thread = args.rss_log.clone().map(|path| {
        let (stop, churns, connected, every) = (stop.clone(), churns.clone(), connected.clone(), args.rss_every.max(1));
        let begun = Instant::now();
        std::thread::spawn(move || {
            use std::io::Write;
            let mut log = std::fs::File::create(&path).unwrap();
            writeln!(log, "epoch\telapsedS\tgatewayRsRssKb\tgatewayRssKb\tconnectedTotal\treplaced").unwrap();
            let mut next = begun;
            while !stop.load(Ordering::Relaxed) {
                if Instant::now() >= next {
                    let epoch = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
                    writeln!(
                        log,
                        "{epoch}\t{}\t{}\t{}\t{}\t{}",
                        begun.elapsed().as_secs(),
                        relay_pid.map(rss_kb).unwrap_or(0),
                        rss_kb(gateway_pid),
                        connected.load(Ordering::Relaxed),
                        churns.load(Ordering::Relaxed)
                    )
                    .unwrap();
                    next += Duration::from_secs(every);
                }
                std::thread::sleep(Duration::from_millis(200));
            }
        })
    });
    let node_datagrams_before = node_datagrams.load(Ordering::Relaxed);
    let before_gateway = threads(gateway_pid);
    let before_relay = relay_pid.map(threads).unwrap_or_default();
    eprintln!(
        "measuring: {} of {} clients connected, {} attached",
        connected.load(Ordering::Relaxed),
        args.clients,
        attached.load(Ordering::Relaxed)
    );
    while Instant::now() < measure.1 {
        std::thread::sleep(Duration::from_millis(100));
    }
    let node_datagrams_measured = node_datagrams.load(Ordering::Relaxed) - node_datagrams_before;
    let after_gateway = threads(gateway_pid);
    let after_relay = relay_pid.map(threads).unwrap_or_default();
    // Let the echoes of the last measured sends arrive.
    std::thread::sleep(Duration::from_secs(2));
    stop.store(true, Ordering::Relaxed);
    for thread in client_threads {
        let _ = thread.join();
    }
    let _ = node_thread.join();
    if let Some(thread) = rss_thread {
        let _ = thread.join();
    }
    drop(relay);
    drop(gateway);

    let mut samples = std::mem::take(&mut *samples.lock().unwrap());
    samples.rtt_us.sort_unstable();
    samples.arrivals_ms.sort_unstable();
    let longest_gap_ms = samples.arrivals_ms.windows(2).map(|w| w[1] - w[0]).max().unwrap_or(0);
    let gaps_over = |ms: u32| samples.arrivals_ms.windows(2).filter(|w| w[1] - w[0] >= ms).count();
    let over = |us: u32| samples.rtt_us.iter().rev().take_while(|&&rtt| rtt > us).count();
    let (gateway_cpu, _) = cpu_seconds(&before_gateway, &after_gateway);
    let (relay_cpu, relay_threads) = cpu_seconds(&before_relay, &after_relay);
    let result = serde_json::json!({
        "mode": args.mode,
        "clients": args.clients,
        "connected": connected.load(Ordering::Relaxed),
        "ratePerClient": args.rate,
        "requestBytes": args.size,
        "replyBytes": args.reply_size,
        "seconds": args.seconds,
        "sent": samples.sent,
        "echoed": samples.received,
        "lost": samples.sent.saturating_sub(samples.received),
        "replaced": churns.load(Ordering::Relaxed),
        "rttUs": {
            "p50": percentile(&samples.rtt_us, 0.50),
            "p90": percentile(&samples.rtt_us, 0.90),
            "p99": percentile(&samples.rtt_us, 0.99),
            "p999": percentile(&samples.rtt_us, 0.999),
            "p9999": percentile(&samples.rtt_us, 0.9999),
            "max": samples.rtt_us.last().copied().unwrap_or(0),
        },
        "tripsOver5ms": over(5_000),
        "tripsOver10ms": over(10_000),
        "tripsOver50ms": over(50_000),
        "longestEchoGapMs": longest_gap_ms,
        "echoGapsOver5ms": gaps_over(5),
        "echoGapsOver10ms": gaps_over(10),
        "gatewayToNodeDatagramsPerSecond": node_datagrams_measured / args.seconds.max(1),
        "gatewayCpuSeconds": gateway_cpu,
        "gatewayRsCpuSeconds": relay_cpu,
        "gatewayRsThreads": relay_threads.iter().take(12).map(|(n, s)| format!("{n}={s:.2}")).collect::<Vec<_>>(),
    });
    println!("{}", serde_json::to_string_pretty(&result).unwrap());
}
