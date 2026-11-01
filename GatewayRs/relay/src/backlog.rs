//! `BoundedPacketQueue` from DirectGateway.cs: a FIFO bounded in bytes (payload + 2 per packet)
//! and in the age of its oldest packet. Enqueue refuses when either bound would be exceeded;
//! `refresh_age` restarts the clock (a deliberate hold is exempt from the age bound).

use std::collections::VecDeque;
use std::time::{Duration, Instant};

use crate::Packet;

#[derive(Debug)]
pub struct Backlog {
    packets: VecDeque<Packet>,
    maximum_bytes: usize,
    maximum_age: Option<Duration>,
    bytes: usize,
    started: Option<Instant>,
}

impl Backlog {
    pub fn new(maximum_bytes: usize, maximum_age: Option<Duration>) -> Self {
        Self { packets: VecDeque::new(), maximum_bytes, maximum_age, bytes: 0, started: None }
    }

    fn size(packet: &Packet) -> usize {
        packet.payload.len() + 2
    }

    pub fn refresh_age(&mut self, now: Instant) {
        self.started = Some(now);
    }

    fn too_old(&self, now: Instant) -> bool {
        match (self.maximum_age, self.started) {
            (Some(age), Some(started)) => !self.packets.is_empty() && now.saturating_duration_since(started) > age,
            _ => false,
        }
    }

    /// Returns the packet back when it does not fit (the caller reports the overflow).
    pub fn try_enqueue(&mut self, packet: Packet, now: Instant) -> Result<(), Packet> {
        let size = Self::size(&packet);
        if self.bytes + size > self.maximum_bytes || self.too_old(now) {
            return Err(packet);
        }
        if self.packets.is_empty() {
            self.started = Some(now);
        }
        self.bytes += size;
        self.packets.push_back(packet);
        Ok(())
    }

    pub fn pop(&mut self) -> Option<Packet> {
        let packet = self.packets.pop_front()?;
        self.bytes -= Self::size(&packet);
        Some(packet)
    }

    pub fn clear(&mut self) {
        self.packets.clear();
        self.bytes = 0;
    }

    pub fn is_expired(&self, now: Instant) -> bool {
        self.too_old(now)
    }

    pub fn is_empty(&self) -> bool {
        self.packets.is_empty()
    }

    pub fn len(&self) -> usize {
        self.packets.len()
    }

    pub fn bytes(&self) -> usize {
        self.bytes
    }

    pub fn age(&self, now: Instant) -> Duration {
        match self.started {
            Some(started) if !self.packets.is_empty() => now.saturating_duration_since(started),
            _ => Duration::ZERO,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn packet(len: usize) -> Packet {
        Packet { channel: 2, delivery: crate::Delivery::ReliableOrdered, payload: vec![0; len] }
    }

    #[test]
    fn bounds_bytes_and_age_like_the_csharp_queue() {
        let start = Instant::now();
        let mut queue = Backlog::new(10, Some(Duration::from_secs(1)));
        assert!(queue.try_enqueue(packet(3), start).is_ok()); // 5 bytes
        assert!(queue.try_enqueue(packet(3), start).is_ok()); // 10 bytes
        assert!(queue.try_enqueue(packet(0), start).is_err()); // 12 > 10
        assert_eq!(queue.bytes(), 10);
        assert!(!queue.is_expired(start + Duration::from_millis(900)));
        assert!(queue.is_expired(start + Duration::from_millis(1100)));
        queue.refresh_age(start + Duration::from_millis(1100));
        assert!(!queue.is_expired(start + Duration::from_millis(1200)));
        assert_eq!(queue.pop().map(|p| p.payload.len()), Some(3));
        assert_eq!(queue.bytes(), 5);
        queue.clear();
        assert!(queue.is_empty() && queue.bytes() == 0);
        // An empty queue never ages out, and its first packet starts the clock.
        assert!(queue.try_enqueue(packet(1), start + Duration::from_secs(60)).is_ok());
    }

    #[test]
    fn unbounded_age() {
        let start = Instant::now();
        let mut queue = Backlog::new(100, None);
        assert!(queue.try_enqueue(packet(1), start).is_ok());
        assert!(!queue.is_expired(start + Duration::from_secs(3600)));
    }
}
