//! State shared between gateway-rs threads, and the mailboxes that connect them. Nothing here is
//! locked on the per-packet path except the read side of `owners` (written only at connect and
//! disconnect) and the node-link connected flags, which are atomics.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::{Duration, Instant};

use crossbeam_channel::{Receiver, Sender};
use mio::Waker;
use relay::LinkId;

use crate::ipc::IpcSender;
use crate::node::NodeCmd;
use crate::worker::WorkerCmd;

/// Node links are numbered once per endpoint and never reused; a cluster has tens of endpoints.
pub const MAX_LINKS: usize = 4096;

/// A JoinRequest C# has not answered yet.
#[derive(Clone, Copy)]
pub struct Asked {
    pub client_id: u64,
    pub session: u64,
    at: Instant,
}

/// T-0314: a worker's JoinRequests that C# has not answered, by correlation, INCLUDING the ones a
/// held join's re-ask superseded. A held join is re-asked every second whether or not C# answered
/// the last ask, and C# decides joins one at a time; when it is backlogged it answers an older ask
/// after the newer one went out. That answer still names a session C# made (accept) and must reach
/// the join it belongs to: dropped, C# kept a session gateway-rs never had and refused the player
/// as a second join until gateway-rs restarted.
#[derive(Default)]
pub struct JoinAsks(HashMap<u32, Asked>);

/// C# answers every JoinRequest exactly once, except when its handler throws; an ask nobody answers
/// is forgotten after this long (far past the join hold).
const UNANSWERED_ASK: Duration = Duration::from_secs(600);

impl JoinAsks {
    pub fn asked(&mut self, correlation: u32, client_id: u64, session: u64, now: Instant) {
        self.0.insert(correlation, Asked { client_id, session, at: now });
    }

    pub fn answered(&mut self, correlation: u32) -> Option<Asked> {
        self.0.remove(&correlation)
    }

    pub fn prune(&mut self, now: Instant) {
        self.0.retain(|_, ask| now.duration_since(ask.at) < UNANSWERED_ASK);
    }

    /// ClusterRegistry is gone and has forgotten every join.
    pub fn clear(&mut self) {
        self.0.clear();
    }
}

/// A channel whose receiver sleeps in `mio::Poll`: a send wakes it only if it is parked.
pub struct Mailbox<T> {
    tx: Sender<T>,
    waker: Arc<Waker>,
    parked: Arc<AtomicBool>,
}

impl<T> Clone for Mailbox<T> {
    fn clone(&self) -> Self {
        Self { tx: self.tx.clone(), waker: self.waker.clone(), parked: self.parked.clone() }
    }
}

impl<T> Mailbox<T> {
    pub fn new(waker: Waker) -> (Self, Inbox<T>) {
        let (tx, rx) = crossbeam_channel::unbounded();
        let parked = Arc::new(AtomicBool::new(false));
        (Self { tx, waker: Arc::new(waker), parked: parked.clone() }, Inbox { rx, parked })
    }

    pub fn send(&self, message: T) {
        if self.tx.send(message).is_ok() && self.parked.swap(false, Ordering::SeqCst) {
            let _ = self.waker.wake();
        }
    }
}

pub struct Inbox<T> {
    pub rx: Receiver<T>,
    parked: Arc<AtomicBool>,
}

impl<T> Inbox<T> {
    /// Call before blocking: true when the caller may sleep (nothing arrived meanwhile).
    pub fn park(&self) -> bool {
        self.parked.store(true, Ordering::SeqCst);
        if self.rx.is_empty() {
            return true;
        }
        self.parked.store(false, Ordering::SeqCst);
        false
    }

    pub fn unpark(&self) {
        self.parked.store(false, Ordering::SeqCst);
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Owner {
    pub worker: u16,
    pub session: u64,
}

pub struct Links {
    connected: Box<[AtomicBool]>,
    names: Mutex<(HashMap<String, LinkId>, Vec<String>)>,
}

impl Default for Links {
    fn default() -> Self {
        Self { connected: (0..MAX_LINKS).map(|_| AtomicBool::new(false)).collect(), names: Mutex::default() }
    }
}

impl Links {
    /// The id of `endpoint`, allocated on first use; `None` once the table is full.
    pub fn id(&self, endpoint: &str) -> Option<LinkId> {
        let mut names = self.names.lock().unwrap();
        if let Some(&id) = names.0.get(endpoint) {
            return Some(id);
        }
        let id = names.1.len();
        if id >= MAX_LINKS {
            eprintln!("WARNING: node-link table full ({MAX_LINKS}); {endpoint} refused");
            return None;
        }
        names.0.insert(endpoint.to_owned(), id as LinkId);
        names.1.push(endpoint.to_owned());
        Some(id as LinkId)
    }

    pub fn name(&self, id: LinkId) -> String {
        self.names.lock().unwrap().1.get(id as usize).cloned().unwrap_or_default()
    }

    pub fn connected(&self, id: LinkId) -> bool {
        self.connected.get(id as usize).is_some_and(|flag| flag.load(Ordering::Acquire))
    }

    pub fn set_connected(&self, id: LinkId, value: bool) {
        if let Some(flag) = self.connected.get(id as usize) {
            flag.store(value, Ordering::Release);
        }
    }
}

pub struct Shared {
    pub ipc: IpcSender,
    pub paused: AtomicBool,
    pub links: Links,
    pub owners: RwLock<HashMap<u64, Owner>>,
    /// Joins asked of ClusterRegistry and not yet answered, published before the JoinRequest is
    /// sent. C# answers the join and then immediately sends the session's first SessionUpdate and
    /// node Attach; the control thread and the node-link thread must be able to route those (and
    /// the node's AttachAck) before the worker has processed the JoinAnswer. Without it, a burst
    /// of joins lost AttachAcks and SessionUpdates (relay bench, 300 clients: a client never
    /// attached, its backlog aged out and it was force-reconnected).
    pub pending_owners: RwLock<HashMap<u64, Owner>>,
    next_session: AtomicU64,
    pub workers: OnceLock<Vec<Mailbox<WorkerCmd>>>,
    pub node: OnceLock<Mailbox<NodeCmd>>,
    /// The Steam frontend's worker (also the last entry of `workers`), when Steam is enabled.
    pub steam: OnceLock<Mailbox<WorkerCmd>>,
}

impl Shared {
    pub fn new(ipc: IpcSender) -> Self {
        Self {
            ipc,
            paused: AtomicBool::new(false),
            links: Links::default(),
            owners: RwLock::default(),
            pending_owners: RwLock::default(),
            next_session: AtomicU64::new(0),
            workers: OnceLock::new(),
            node: OnceLock::new(),
            steam: OnceLock::new(),
        }
    }

    pub fn paused(&self) -> bool {
        self.paused.load(Ordering::Acquire)
    }

    pub fn new_session(&self) -> u64 {
        self.next_session.fetch_add(1, Ordering::Relaxed) + 1
    }

    pub fn owner(&self, client_id: u64) -> Option<Owner> {
        self.owners.read().unwrap().get(&client_id).copied()
    }

    /// The owner a per-client message routes to: the live session, else a join being answered.
    /// A worker's mailbox is FIFO and C# sends nothing for a session before its JoinAnswer, so a
    /// message routed by the pending owner reaches the worker after the answer.
    pub fn route_owner(&self, client_id: u64) -> Option<Owner> {
        self.owner(client_id).or_else(|| self.pending_owners.read().unwrap().get(&client_id).copied())
    }

    pub fn set_pending(&self, client_id: u64, owner: Owner) {
        self.pending_owners.write().unwrap().insert(client_id, owner);
    }

    /// Ends a pending join's entry (answered or refused); a newer join's entry is kept.
    pub fn clear_pending(&self, client_id: u64, session: u64) {
        let mut pending = self.pending_owners.write().unwrap();
        if pending.get(&client_id).is_some_and(|owner| owner.session == session) {
            pending.remove(&client_id);
        }
    }

    pub fn worker(&self, index: u16) -> Option<&Mailbox<WorkerCmd>> {
        self.workers.get()?.get(index as usize)
    }

    pub fn to_node(&self, command: NodeCmd) {
        if let Some(node) = self.node.get() {
            node.send(command);
        }
    }

    pub fn to_all_workers(&self, command: impl Fn() -> WorkerCmd) {
        for worker in self.workers.get().into_iter().flatten() {
            worker.send(command());
        }
    }

    /// A JoinRequest correlation that names the asking worker in its low byte.
    pub fn join_correlation(&self, worker: u16) -> u32 {
        loop {
            let value = (self.ipc.correlation() << 8) | worker as u32;
            if value >> 8 != 0 {
                return value;
            }
        }
    }
}

/// How often a LiteNetLib endpoint is flushed while it has work (`GATEWAY_RS_FLUSH_US`).
///
/// lnl merges the small packets queued between two polls into one datagram, like LiteNetLib does
/// per 15 ms update. Polling on every wake-up sent each relay frame, credit and ack alone (the
/// relay bench: 13 000 datagrams/s to the node where C# sent 1 100, at twice the CPU). An
/// endpoint that was idle for the interval still flushes at once, so a lone packet is not delayed.
pub fn flush_interval() -> Duration {
    static INTERVAL: OnceLock<Duration> = OnceLock::new();
    *INTERVAL.get_or_init(|| {
        let micros = std::env::var("GATEWAY_RS_FLUSH_US").ok().and_then(|v| v.parse().ok()).unwrap_or(DEFAULT_FLUSH_US);
        Duration::from_micros(micros)
    })
}

pub const DEFAULT_FLUSH_US: u64 = 500;

/// Decides when a thread's loop runs `Endpoint::poll`: at most every [`flush_interval`] while
/// something was received or queued, and at least every `tick` for the endpoint's timers.
pub struct Coalesce {
    interval: Duration,
    tick: Duration,
    last: Instant,
    pending: bool,
}

impl Coalesce {
    pub fn new(tick: Duration, now: Instant) -> Self {
        Self { interval: flush_interval(), tick, last: now, pending: false }
    }

    /// Work arrived (a datagram or a command): there may be something to send.
    pub fn touch(&mut self) {
        self.pending = true;
    }

    pub fn due(&self, now: Instant) -> bool {
        let since = now.saturating_duration_since(self.last);
        since >= self.tick || (self.pending && since >= self.interval)
    }

    pub fn flushed(&mut self, now: Instant) {
        self.last = now;
        self.pending = false;
    }

    /// How long the loop may sleep in `mio::Poll`.
    pub fn wait(&self, now: Instant) -> Duration {
        let next = self.last + if self.pending { self.interval } else { self.tick };
        next.saturating_duration_since(now).min(self.tick)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_join_in_flight_routes_until_its_answer() {
        let shared = Shared::new(IpcSender::default());
        let pending = Owner { worker: 2, session: 7 };
        shared.set_pending(42, pending);
        assert_eq!(shared.route_owner(42), Some(pending), "routable before the worker processed the answer");
        // Accepted: the live owner wins, and clearing the answered join leaves it.
        let live = Owner { worker: 2, session: 7 };
        shared.owners.write().unwrap().insert(42, live);
        shared.clear_pending(42, 7);
        assert_eq!(shared.route_owner(42), Some(live));
        // A newer join's entry survives the clearing of an older one.
        shared.set_pending(43, Owner { worker: 1, session: 9 });
        shared.clear_pending(43, 8);
        assert_eq!(shared.route_owner(43), Some(Owner { worker: 1, session: 9 }));
        shared.clear_pending(43, 9);
        assert_eq!(shared.route_owner(43), None);
    }

    #[test]
    fn coalescing_flushes_a_lone_packet_at_once_and_a_burst_per_interval() {
        let t0 = Instant::now();
        let mut coalesce = Coalesce { interval: Duration::from_micros(500), tick: Duration::from_millis(5), last: t0, pending: false };
        assert!(!coalesce.due(t0 + Duration::from_millis(1)), "nothing to send, before the tick");
        assert!(coalesce.due(t0 + Duration::from_millis(5)), "the tick");
        // Idle for longer than the interval: a new packet goes out on this pass.
        coalesce.touch();
        assert!(coalesce.due(t0 + Duration::from_millis(1)));
        coalesce.flushed(t0 + Duration::from_millis(1));
        // Within the interval: held, and the loop sleeps only until the interval ends.
        coalesce.touch();
        let now = t0 + Duration::from_micros(1200);
        assert!(!coalesce.due(now));
        assert_eq!(coalesce.wait(now), Duration::from_micros(300));
        assert!(coalesce.due(t0 + Duration::from_micros(1500)));
    }
}
