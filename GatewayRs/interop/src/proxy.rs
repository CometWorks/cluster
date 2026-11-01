//! An impairing UDP proxy: loss, duplication, delay and jitter (which reorders), per direction.

use std::collections::HashMap;
use std::collections::hash_map::Entry;
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

pub struct ProxyOptions {
    pub listen: SocketAddr,
    pub target: SocketAddr,
    pub loss: f64,
    pub dup: f64,
    pub delay_ms: u64,
    pub jitter_ms: u64,
    pub seed: u64,
    pub duration_ms: u64,
    /// Once, this long after the first client datagram, move every client to a new upstream socket
    /// (a NAT rebinding as the server sees it).
    pub rebind_after_ms: u64,
}

struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.0 = x;
        x.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn chance(&mut self, p: f64) -> bool {
        p > 0.0 && ((self.next() >> 11) as f64 / (1u64 << 53) as f64) < p
    }
}

struct Pending {
    at: Instant,
    /// None: from the front socket to `to`; Some(client): from that client's upstream socket.
    via: Option<SocketAddr>,
    to: SocketAddr,
    data: Vec<u8>,
}

pub fn run(o: ProxyOptions) -> io::Result<()> {
    let front = UdpSocket::bind(o.listen)?;
    front.set_nonblocking(true)?;
    let mut upstream: HashMap<SocketAddr, UdpSocket> = HashMap::new();
    let mut queue: Vec<Pending> = Vec::new();
    let mut rng = Rng(o.seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1);
    let mut buf = vec![0u8; 65536];
    let (mut forwarded, mut dropped, mut duplicated) = (0u64, 0u64, 0u64);
    let end = Instant::now() + Duration::from_millis(o.duration_ms);
    let mut first_packet: Option<Instant> = None;
    let mut rebound = o.rebind_after_ms == 0;
    let mut schedule = |queue: &mut Vec<Pending>, rng: &mut Rng, via, to, data: &[u8]| {
        if rng.chance(o.loss) {
            dropped += 1;
            return;
        }
        let copies = if rng.chance(o.dup) { 2 } else { 1 };
        duplicated += copies - 1;
        for _ in 0..copies {
            let jitter = if o.jitter_ms > 0 {
                rng.next() % (o.jitter_ms + 1)
            } else {
                0
            };
            let at = Instant::now() + Duration::from_millis(o.delay_ms + jitter);
            queue.push(Pending {
                at,
                via,
                to,
                data: data.to_vec(),
            });
        }
    };
    while Instant::now() < end {
        let mut busy = false;
        loop {
            match front.recv_from(&mut buf) {
                Ok((n, client)) => {
                    busy = true;
                    first_packet.get_or_insert_with(Instant::now);
                    if let Entry::Vacant(e) = upstream.entry(client) {
                        let s = UdpSocket::bind(SocketAddr::new(o.listen.ip(), 0))?;
                        s.set_nonblocking(true)?;
                        e.insert(s);
                    }
                    schedule(&mut queue, &mut rng, Some(client), o.target, &buf[..n]);
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(_) => break,
            }
        }
        for (client, s) in upstream.iter() {
            while let Ok((n, _)) = s.recv_from(&mut buf) {
                busy = true;
                schedule(&mut queue, &mut rng, None, *client, &buf[..n]);
            }
        }
        if !rebound
            && first_packet.is_some_and(|t| t.elapsed() >= Duration::from_millis(o.rebind_after_ms))
        {
            rebound = true;
            for (client, s) in upstream.iter_mut() {
                let fresh = UdpSocket::bind(SocketAddr::new(o.listen.ip(), 0))?;
                fresh.set_nonblocking(true)?;
                println!(
                    "proxy: client {client} moved from {} to {}",
                    s.local_addr()?,
                    fresh.local_addr()?
                );
                *s = fresh;
            }
        }
        let now = Instant::now();
        let mut i = 0;
        while i < queue.len() {
            if queue[i].at <= now {
                // `remove` keeps equal-deadline datagrams in arrival order: without jitter the proxy does not reorder.
                let p = queue.remove(i);
                forwarded += 1;
                let _ = match p.via {
                    None => front.send_to(&p.data, p.to),
                    Some(client) => upstream[&client].send_to(&p.data, p.to),
                };
            } else {
                i += 1;
            }
        }
        if !busy {
            std::thread::sleep(Duration::from_micros(200));
        }
    }
    println!("proxy: forwarded {forwarded}, dropped {dropped}, duplicated {duplicated}");
    Ok(())
}
