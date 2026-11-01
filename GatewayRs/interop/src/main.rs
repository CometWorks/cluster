//! `lnl-interop`: interop peer and impairing proxy for testing the `lnl` crate against C#
//! LiteNetLib 1.3.1. Driven by `interop/run.sh`.
//!
//! ```text
//! lnl-interop server --bind ADDR [session options]
//! lnl-interop client --connect ADDR [session options]
//! lnl-interop proxy --listen ADDR --target ADDR [--loss P] [--dup P] [--delay-ms N] [--jitter-ms N] [--seed N] [--duration-ms N]
//!                   [--rebind-after-ms N]
//!
//! session options: --end close|await-close|die|await-timeout  --timeout-ms N  --mtu-discovery
//!   --expect-mtu N  --min-rtt N  --max-rtt N  --hold-ms N  --reject  --expect-reject  --per N
//!   --no-big  --deadline-ms N  --expect-unreliable N  --allow-address-change  --expect-address-change  --grace-ms N
//! ```

mod proxy;
mod script;
mod session;

use std::net::SocketAddr;
use std::process::ExitCode;

fn parse<T: std::str::FromStr>(v: Option<String>, name: &str) -> T {
    v.and_then(|s| s.parse().ok()).unwrap_or_else(|| {
        eprintln!("bad or missing value for {name}");
        std::process::exit(2)
    })
}

fn main() -> ExitCode {
    let mut args = std::env::args().skip(1);
    let mode = args.next().unwrap_or_default();
    match mode.as_str() {
        "server" | "client" => {
            let mut o = session::SessionOptions {
                server: mode == "server",
                endpoint: SocketAddr::from(([127, 0, 0, 1], 0)),
                end: "close".into(),
                timeout_ms: 4000,
                mtu_discovery: false,
                expect_mtu: 0,
                min_rtt: -1,
                max_rtt: -1,
                hold_ms: 0,
                reject: false,
                expect_reject: false,
                per: 12,
                big: true,
                deadline_ms: 60_000,
                expect_unreliable: -1,
                allow_address_change: false,
                expect_address_change: false,
                grace_ms: 1500,
            };
            while let Some(a) = args.next() {
                match a.as_str() {
                    "--bind" | "--connect" => o.endpoint = parse(args.next(), &a),
                    "--end" => o.end = parse(args.next(), &a),
                    "--timeout-ms" => o.timeout_ms = parse(args.next(), &a),
                    "--mtu-discovery" => o.mtu_discovery = true,
                    "--expect-mtu" => o.expect_mtu = parse(args.next(), &a),
                    "--min-rtt" => o.min_rtt = parse(args.next(), &a),
                    "--max-rtt" => o.max_rtt = parse(args.next(), &a),
                    "--hold-ms" => o.hold_ms = parse(args.next(), &a),
                    "--reject" => o.reject = true,
                    "--expect-reject" => o.expect_reject = true,
                    "--per" => o.per = parse(args.next(), &a),
                    "--no-big" => o.big = false,
                    "--deadline-ms" => o.deadline_ms = parse(args.next(), &a),
                    "--expect-unreliable" => o.expect_unreliable = parse(args.next(), &a),
                    "--allow-address-change" => o.allow_address_change = true,
                    "--expect-address-change" => o.expect_address_change = true,
                    "--grace-ms" => o.grace_ms = parse(args.next(), &a),
                    other => {
                        eprintln!("unknown option {other}");
                        return ExitCode::from(2);
                    }
                }
            }
            ExitCode::from(session::run(o) as u8)
        }
        "proxy" => {
            let mut o = proxy::ProxyOptions {
                listen: SocketAddr::from(([127, 0, 0, 1], 0)),
                target: SocketAddr::from(([127, 0, 0, 1], 0)),
                loss: 0.0,
                dup: 0.0,
                delay_ms: 0,
                jitter_ms: 0,
                seed: 1,
                duration_ms: 120_000,
                rebind_after_ms: 0,
            };
            while let Some(a) = args.next() {
                match a.as_str() {
                    "--listen" => o.listen = parse(args.next(), &a),
                    "--target" => o.target = parse(args.next(), &a),
                    "--loss" => o.loss = parse(args.next(), &a),
                    "--dup" => o.dup = parse(args.next(), &a),
                    "--delay-ms" => o.delay_ms = parse(args.next(), &a),
                    "--jitter-ms" => o.jitter_ms = parse(args.next(), &a),
                    "--seed" => o.seed = parse(args.next(), &a),
                    "--duration-ms" => o.duration_ms = parse(args.next(), &a),
                    "--rebind-after-ms" => o.rebind_after_ms = parse(args.next(), &a),
                    other => {
                        eprintln!("unknown option {other}");
                        return ExitCode::from(2);
                    }
                }
            }
            match proxy::run(o) {
                Ok(()) => ExitCode::SUCCESS,
                Err(e) => {
                    eprintln!("proxy: {e}");
                    ExitCode::FAILURE
                }
            }
        }
        _ => {
            eprintln!("usage: lnl-interop server|client|proxy ...");
            ExitCode::from(2)
        }
    }
}
