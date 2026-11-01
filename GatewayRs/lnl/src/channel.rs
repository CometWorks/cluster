//! ReliableChannel.cs and SequencedChannel.cs.

use std::collections::VecDeque;
use std::time::Instant;

use crate::Output;
use crate::consts::{DEFAULT_WINDOW_SIZE, DeliveryMethod, MAX_SEQUENCE, relative_sequence_number};
use crate::packet::{
    Pool, channel_id, is_fragmented, property, sequence, set_property, set_sequence,
};
use crate::peer::{Deliver, Link};

const WINDOW: i32 = DEFAULT_WINDOW_SIZE as i32;
/// `(DefaultWindowSize - 1) / 8 + 2` bytes of ack bitfield after the 4-byte channeled header.
const ACK_PACKET_SIZE: usize = 4 + (DEFAULT_WINDOW_SIZE - 1) / 8 + 2;

/// Everything a channel needs to send.
pub(crate) struct SendCtx<'a, 'o> {
    pub link: &'a mut Link,
    pub out: &'a mut (dyn Output + 'o),
    pub pool: &'a mut Pool,
    pub now: Instant,
    /// `NetPeer.ResendDelay`, ms.
    pub resend_delay_ms: f64,
}

pub(crate) enum Channel {
    Reliable(Box<ReliableChannel>),
    Sequenced(SequencedChannel),
}

impl Channel {
    /// `NetPeer.CreateChannel`: the channel type is `idx % 4`.
    pub(crate) fn new(idx: u8) -> Self {
        match idx % 4 {
            0 => Channel::Reliable(Box::new(ReliableChannel::new(false, idx))),
            1 => Channel::Sequenced(SequencedChannel::new(false, idx)),
            2 => Channel::Reliable(Box::new(ReliableChannel::new(true, idx))),
            _ => Channel::Sequenced(SequencedChannel::new(true, idx)),
        }
    }

    pub(crate) fn queued(&mut self) -> &mut bool {
        match self {
            Channel::Reliable(c) => &mut c.queued,
            Channel::Sequenced(c) => &mut c.queued,
        }
    }

    /// `BaseChannel.AddToQueue` minus the send-queue registration (done by the caller).
    pub(crate) fn push_outgoing(&mut self, raw: Vec<u8>) {
        match self {
            Channel::Reliable(c) => c.outgoing.push_back(raw),
            Channel::Sequenced(c) => c.outgoing.push_back(raw),
        }
    }

    /// `BaseChannel.PacketsInQueue`.
    pub(crate) fn packets_in_queue(&self) -> usize {
        match self {
            Channel::Reliable(c) => c.outgoing.len(),
            Channel::Sequenced(c) => c.outgoing.len(),
        }
    }

    /// Packets sent but not acknowledged yet (reliable channels) or the retained last packet.
    pub(crate) fn packets_in_flight(&self) -> usize {
        match self {
            Channel::Reliable(c) => {
                relative_sequence_number(c.local_seq, c.local_window_start) as usize
            }
            Channel::Sequenced(c) => c.last_packet.is_some() as usize,
        }
    }

    pub(crate) fn send_next_packets(&mut self, ctx: &mut SendCtx) -> bool {
        match self {
            Channel::Reliable(c) => c.send_next_packets(ctx),
            Channel::Sequenced(c) => c.send_next_packets(ctx),
        }
    }

    /// `ProcessPacket` for a Channeled or Ack packet. Consumes `raw` (stored, delivered or recycled).
    pub(crate) fn process_packet(&mut self, raw: Vec<u8>, d: &mut Deliver) {
        match self {
            Channel::Reliable(c) => c.process_packet(raw, d),
            Channel::Sequenced(c) => c.process_packet(raw, d),
        }
    }

    pub(crate) fn recycle_all(self, pool: &mut Pool) {
        match self {
            Channel::Reliable(c) => {
                let c = *c;
                c.outgoing.into_iter().for_each(|p| pool.put(p));
                c.pending
                    .into_iter()
                    .filter_map(|p| p.packet)
                    .for_each(|p| pool.put(p));
                c.received.into_iter().flatten().for_each(|p| pool.put(p));
            }
            Channel::Sequenced(c) => {
                c.outgoing.into_iter().for_each(|p| pool.put(p));
                if let Some(p) = c.last_packet {
                    pool.put(p);
                }
            }
        }
    }
}

#[derive(Default)]
struct PendingPacket {
    packet: Option<Vec<u8>>,
    time_stamp: Option<Instant>,
    is_sent: bool,
}

impl PendingPacket {
    fn init(&mut self, packet: Vec<u8>) {
        self.packet = Some(packet);
        self.is_sent = false;
    }

    /// Returns true while a packet occupies the slot (sent now or waiting for its resend delay).
    fn try_send(&mut self, ctx: &mut SendCtx) -> bool {
        let Some(packet) = self.packet.as_mut() else {
            return false;
        };
        if self.is_sent
            && let Some(ts) = self.time_stamp
        {
            let elapsed_ms = ctx.now.saturating_duration_since(ts).as_secs_f64() * 1000.0;
            if elapsed_ms < ctx.resend_delay_ms {
                return true;
            }
        }
        self.time_stamp = Some(ctx.now);
        self.is_sent = true;
        ctx.link.send_user_data(packet, ctx.out);
        true
    }

    fn clear(&mut self, pool: &mut Pool) -> bool {
        match self.packet.take() {
            Some(p) => {
                pool.put(p);
                true
            }
            None => false,
        }
    }
}

pub(crate) struct ReliableChannel {
    id: u8,
    ordered: bool,
    queued: bool,
    outgoing: VecDeque<Vec<u8>>,
    pending: Vec<PendingPacket>,
    received: Vec<Option<Vec<u8>>>,
    early_received: Vec<bool>,
    local_seq: i32,
    remote_seq: i32,
    local_window_start: i32,
    remote_window_start: i32,
    must_send_acks: bool,
    delivery_method: DeliveryMethod,
    outgoing_acks: [u8; ACK_PACKET_SIZE],
}

impl ReliableChannel {
    fn new(ordered: bool, id: u8) -> Self {
        let mut acks = [0u8; ACK_PACKET_SIZE];
        set_property(&mut acks, crate::PacketProperty::Ack);
        acks[3] = id;
        ReliableChannel {
            id,
            ordered,
            queued: false,
            outgoing: VecDeque::with_capacity(DEFAULT_WINDOW_SIZE),
            pending: (0..DEFAULT_WINDOW_SIZE)
                .map(|_| PendingPacket::default())
                .collect(),
            received: if ordered {
                (0..DEFAULT_WINDOW_SIZE).map(|_| None).collect()
            } else {
                Vec::new()
            },
            early_received: if ordered {
                Vec::new()
            } else {
                vec![false; DEFAULT_WINDOW_SIZE]
            },
            local_seq: 0,
            remote_seq: 0,
            local_window_start: 0,
            remote_window_start: 0,
            must_send_acks: false,
            delivery_method: if ordered {
                DeliveryMethod::ReliableOrdered
            } else {
                DeliveryMethod::ReliableUnordered
            },
            outgoing_acks: acks,
        }
    }

    fn process_ack(&mut self, raw: &[u8], pool: &mut Pool) {
        if raw.len() != ACK_PACKET_SIZE {
            return;
        }
        let ack_seq = sequence(raw) as i32;
        let num = relative_sequence_number(self.local_window_start, ack_seq);
        if ack_seq >= MAX_SEQUENCE || !(0..WINDOW).contains(&num) {
            return;
        }
        let mut n = self.local_window_start;
        while n != self.local_seq && relative_sequence_number(n, ack_seq) < WINDOW {
            let idx = (n % WINDOW) as usize;
            let byte = 4 + idx / 8;
            let bit = idx % 8;
            if raw[byte] & (1 << bit) != 0 {
                if n == self.local_window_start {
                    self.local_window_start = (self.local_window_start + 1) % MAX_SEQUENCE;
                }
                self.pending[idx].clear(pool);
            }
            n = (n + 1) % MAX_SEQUENCE;
        }
    }

    fn send_next_packets(&mut self, ctx: &mut SendCtx) -> bool {
        if self.must_send_acks {
            self.must_send_acks = false;
            ctx.link.send_user_data(&mut self.outgoing_acks, ctx.out);
        }
        while !self.outgoing.is_empty()
            && relative_sequence_number(self.local_seq, self.local_window_start) < WINDOW
        {
            let mut p = self.outgoing.pop_front().unwrap();
            set_sequence(&mut p, self.local_seq as u16);
            p[3] = self.id;
            self.pending[(self.local_seq % WINDOW) as usize].init(p);
            self.local_seq = (self.local_seq + 1) % MAX_SEQUENCE;
        }
        let mut flag = false;
        let mut n = self.local_window_start;
        while n != self.local_seq {
            if self.pending[(n % WINDOW) as usize].try_send(ctx) {
                flag = true;
            }
            n = (n + 1) % MAX_SEQUENCE;
        }
        if !flag && !self.must_send_acks {
            return !self.outgoing.is_empty();
        }
        true
    }

    fn process_packet(&mut self, raw: Vec<u8>, d: &mut Deliver) {
        if property(&raw) == Some(crate::PacketProperty::Ack) {
            self.process_ack(&raw, d.pool);
            d.pool.put(raw);
            return;
        }
        let seq = sequence(&raw) as i32;
        if seq >= MAX_SEQUENCE {
            d.pool.put(raw);
            return;
        }
        let num = relative_sequence_number(seq, self.remote_window_start);
        if relative_sequence_number(seq, self.remote_seq) > WINDOW
            || !(0..WINDOW * 2).contains(&num)
        {
            d.pool.put(raw);
            return;
        }
        if num >= WINDOW {
            let new_start = (self.remote_window_start + num - WINDOW + 1) % MAX_SEQUENCE;
            set_sequence(&mut self.outgoing_acks, new_start as u16);
            while self.remote_window_start != new_start {
                let idx = (self.remote_window_start % WINDOW) as usize;
                self.outgoing_acks[4 + idx / 8] &= !(1u8 << (idx % 8));
                self.remote_window_start = (self.remote_window_start + 1) % MAX_SEQUENCE;
            }
        }
        self.must_send_acks = true;
        let idx = (seq % WINDOW) as usize;
        let byte = 4 + idx / 8;
        let bit = 1u8 << (idx % 8);
        if self.outgoing_acks[byte] & bit != 0 {
            d.add_to_send_queue(self.id, &mut self.queued);
            d.pool.put(raw);
            return;
        }
        self.outgoing_acks[byte] |= bit;
        d.add_to_send_queue(self.id, &mut self.queued);

        if seq == self.remote_seq {
            d.add_reliable_packet(self.delivery_method, raw);
            self.remote_seq = (self.remote_seq + 1) % MAX_SEQUENCE;
            if self.ordered {
                while let Some(p) = self.received[(self.remote_seq % WINDOW) as usize].take() {
                    d.add_reliable_packet(self.delivery_method, p);
                    self.remote_seq = (self.remote_seq + 1) % MAX_SEQUENCE;
                }
            } else {
                while self.early_received[(self.remote_seq % WINDOW) as usize] {
                    self.early_received[(self.remote_seq % WINDOW) as usize] = false;
                    self.remote_seq = (self.remote_seq + 1) % MAX_SEQUENCE;
                }
            }
            return;
        }
        if self.ordered {
            if let Some(old) = self.received[idx].replace(raw) {
                d.pool.put(old);
            }
        } else {
            self.early_received[idx] = true;
            d.add_reliable_packet(self.delivery_method, raw);
        }
    }
}

pub(crate) struct SequencedChannel {
    id: u8,
    reliable: bool,
    queued: bool,
    outgoing: VecDeque<Vec<u8>>,
    local_sequence: i32,
    remote_sequence: u16,
    last_packet: Option<Vec<u8>>,
    ack_packet: [u8; 4],
    must_send_ack: bool,
    last_packet_send_time: Option<Instant>,
}

impl SequencedChannel {
    fn new(reliable: bool, id: u8) -> Self {
        let mut ack = [0u8; 4];
        set_property(&mut ack, crate::PacketProperty::Ack);
        ack[3] = id;
        SequencedChannel {
            id,
            reliable,
            queued: false,
            outgoing: VecDeque::new(),
            local_sequence: 0,
            remote_sequence: 0,
            last_packet: None,
            ack_packet: ack,
            must_send_ack: false,
            last_packet_send_time: None,
        }
    }

    fn send_next_packets(&mut self, ctx: &mut SendCtx) -> bool {
        if self.reliable && self.outgoing.is_empty() {
            let due = match self.last_packet_send_time {
                None => true,
                Some(t) => {
                    ctx.now.saturating_duration_since(t).as_secs_f64() * 1000.0
                        >= ctx.resend_delay_ms
                }
            };
            if due && let Some(lp) = self.last_packet.as_mut() {
                self.last_packet_send_time = Some(ctx.now);
                ctx.link.send_user_data(lp, ctx.out);
            }
        } else {
            while let Some(mut p) = self.outgoing.pop_front() {
                self.local_sequence = (self.local_sequence + 1) % MAX_SEQUENCE;
                set_sequence(&mut p, self.local_sequence as u16);
                p[3] = self.id;
                ctx.link.send_user_data(&mut p, ctx.out);
                if self.reliable && self.outgoing.is_empty() {
                    self.last_packet_send_time = Some(ctx.now);
                    if let Some(old) = self.last_packet.replace(p) {
                        ctx.pool.put(old);
                    }
                } else {
                    ctx.pool.put(p);
                }
            }
        }
        if self.reliable && self.must_send_ack {
            self.must_send_ack = false;
            set_sequence(&mut self.ack_packet, self.remote_sequence);
            ctx.link.send_user_data(&mut self.ack_packet, ctx.out);
        }
        self.last_packet.is_some()
    }

    fn process_packet(&mut self, raw: Vec<u8>, d: &mut Deliver) {
        if is_fragmented(&raw) {
            d.pool.put(raw);
            return;
        }
        if property(&raw) == Some(crate::PacketProperty::Ack) {
            if self.reliable
                && self
                    .last_packet
                    .as_ref()
                    .is_some_and(|lp| sequence(lp) == sequence(&raw))
                && let Some(lp) = self.last_packet.take()
            {
                d.pool.put(lp);
            }
            d.pool.put(raw);
            return;
        }
        let seq = sequence(&raw);
        let num = relative_sequence_number(seq as i32, self.remote_sequence as i32);
        if (seq as i32) < MAX_SEQUENCE && num > 0 {
            self.remote_sequence = seq;
            let method = if self.reliable {
                DeliveryMethod::ReliableSequenced
            } else {
                DeliveryMethod::Sequenced
            };
            let channel = channel_id(&raw) / 4;
            d.receive(raw, method, channel, 4);
        } else {
            d.pool.put(raw);
        }
        if self.reliable {
            self.must_send_ack = true;
            d.add_to_send_queue(self.id, &mut self.queued);
        }
    }
}
