//! Raw packet layout (NetPacket.cs) and the buffer pool.
//!
//! Header byte 0: property in bits 0-4, connection number in bits 5-6, fragmented flag in bit 7.
//! Channeled/Ack: sequence u16 LE at 1, channel id at 3. Fragmented: fragment id, part and total
//! (u16 LE each) at 4, 6 and 8.

use crate::consts::{MAX_PACKET_SIZE, PROPERTIES_COUNT, PacketProperty};

#[inline]
pub(crate) fn property_byte(raw: &[u8]) -> u8 {
    raw[0] & 0x1F
}

#[inline]
pub(crate) fn property(raw: &[u8]) -> Option<PacketProperty> {
    PacketProperty::from_u8(property_byte(raw))
}

#[inline]
pub(crate) fn set_property(raw: &mut [u8], p: PacketProperty) {
    raw[0] = (raw[0] & 0xE0) | p as u8;
}

#[inline]
pub(crate) fn connection_number(raw: &[u8]) -> u8 {
    (raw[0] & 0x60) >> 5
}

#[inline]
pub(crate) fn set_connection_number(raw: &mut [u8], n: u8) {
    raw[0] = (raw[0] & 0x9F) | (n << 5);
}

#[inline]
pub(crate) fn is_fragmented(raw: &[u8]) -> bool {
    raw[0] & 0x80 != 0
}

#[inline]
pub(crate) fn mark_fragmented(raw: &mut [u8]) {
    raw[0] |= 0x80;
}

#[inline]
pub(crate) fn u16_at(raw: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([raw[at], raw[at + 1]])
}

#[inline]
pub(crate) fn i32_at(raw: &[u8], at: usize) -> i32 {
    i32::from_le_bytes(raw[at..at + 4].try_into().unwrap())
}

#[inline]
pub(crate) fn i64_at(raw: &[u8], at: usize) -> i64 {
    i64::from_le_bytes(raw[at..at + 8].try_into().unwrap())
}

#[inline]
pub(crate) fn put_u16(raw: &mut [u8], at: usize, v: u16) {
    raw[at..at + 2].copy_from_slice(&v.to_le_bytes());
}

#[inline]
pub(crate) fn put_i32(raw: &mut [u8], at: usize, v: i32) {
    raw[at..at + 4].copy_from_slice(&v.to_le_bytes());
}

#[inline]
pub(crate) fn put_i64(raw: &mut [u8], at: usize, v: i64) {
    raw[at..at + 8].copy_from_slice(&v.to_le_bytes());
}

#[inline]
pub(crate) fn sequence(raw: &[u8]) -> u16 {
    u16_at(raw, 1)
}

#[inline]
pub(crate) fn set_sequence(raw: &mut [u8], seq: u16) {
    put_u16(raw, 1, seq);
}

#[inline]
pub(crate) fn channel_id(raw: &[u8]) -> u8 {
    raw[3]
}

#[inline]
pub(crate) fn fragment_id(raw: &[u8]) -> u16 {
    u16_at(raw, 4)
}

#[inline]
pub(crate) fn fragment_part(raw: &[u8]) -> u16 {
    u16_at(raw, 6)
}

#[inline]
pub(crate) fn fragments_total(raw: &[u8]) -> u16 {
    u16_at(raw, 8)
}

/// `NetPacket.Verify`: known property, and long enough for its header (+6 when fragmented).
pub(crate) fn verify(raw: &[u8]) -> bool {
    if raw.is_empty() {
        return false;
    }
    let b = property_byte(raw);
    if b >= PROPERTIES_COUNT {
        return false;
    }
    let header = PacketProperty::from_u8(b).unwrap().header_size();
    if raw.len() >= header {
        if is_fragmented(raw) {
            return raw.len() >= header + 6;
        }
        return true;
    }
    false
}

/// A received payload (or disconnect data) handed to the caller. It owns a pooled buffer; give it
/// back with [`crate::Endpoint::recycle`] to keep the steady state allocation-free. Dropping it is
/// safe, it only costs a later allocation.
#[derive(Debug)]
pub struct Packet {
    pub(crate) buf: Vec<u8>,
    pub(crate) offset: usize,
}

impl Packet {
    pub(crate) fn new(buf: Vec<u8>, offset: usize) -> Self {
        Packet { buf, offset }
    }

    /// The user data (the LiteNetLib `NetPacketReader` contents).
    #[inline]
    pub fn data(&self) -> &[u8] {
        &self.buf[self.offset..]
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.buf.len() - self.offset
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Copies the user data out into an owned vector (allocates).
    pub fn to_vec(&self) -> Vec<u8> {
        self.data().to_vec()
    }
}

/// Free list of `MAX_PACKET_SIZE`-capacity buffers (`NetManager.PoolGetPacket`/`PoolRecycle`).
/// Buffers larger than a datagram (reassembled messages) are not kept.
#[derive(Debug)]
pub(crate) struct Pool {
    free: Vec<Vec<u8>>,
    limit: usize,
}

impl Pool {
    pub(crate) fn new(limit: usize) -> Self {
        Pool {
            free: Vec::with_capacity(limit.min(4096)),
            limit,
        }
    }

    /// A zeroed buffer of `size` bytes.
    pub(crate) fn get(&mut self, size: usize) -> Vec<u8> {
        if size > MAX_PACKET_SIZE {
            return vec![0; size];
        }
        match self.free.pop() {
            Some(mut v) => {
                v.clear();
                v.resize(size, 0);
                v
            }
            None => {
                let mut v = Vec::with_capacity(MAX_PACKET_SIZE);
                v.resize(size, 0);
                v
            }
        }
    }

    /// A buffer holding a copy of `data`.
    pub(crate) fn copy_of(&mut self, data: &[u8]) -> Vec<u8> {
        if data.len() > MAX_PACKET_SIZE {
            return data.to_vec();
        }
        let mut v = self
            .free
            .pop()
            .unwrap_or_else(|| Vec::with_capacity(MAX_PACKET_SIZE));
        v.clear();
        v.extend_from_slice(data);
        v
    }

    pub(crate) fn put(&mut self, v: Vec<u8>) {
        let cap = v.capacity();
        if (MAX_PACKET_SIZE..=2 * MAX_PACKET_SIZE).contains(&cap) && self.free.len() < self.limit {
            self.free.push(v);
        }
    }

    #[cfg(test)]
    pub(crate) fn available(&self) -> usize {
        self.free.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn header_bits() {
        let mut b = [0u8; 10];
        set_property(&mut b, PacketProperty::Channeled);
        set_connection_number(&mut b, 3);
        mark_fragmented(&mut b);
        assert_eq!(b[0], 0x80 | 0x60 | 1);
        assert_eq!(property(&b), Some(PacketProperty::Channeled));
        assert_eq!(connection_number(&b), 3);
        assert!(is_fragmented(&b));
        set_property(&mut b, PacketProperty::Ack);
        assert_eq!(b[0], 0x80 | 0x60 | 2);
        set_connection_number(&mut b, 1);
        assert_eq!(b[0], 0x80 | 0x20 | 2);
    }

    #[test]
    fn verify_sizes() {
        assert!(verify(&[0]));
        assert!(!verify(&[1, 0, 0]));
        assert!(verify(&[1, 0, 0, 0]));
        assert!(!verify(&[0x81, 0, 0, 0, 0, 0, 0, 0, 0]));
        assert!(verify(&[0x81, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        assert!(!verify(&[18]));
        assert!(!verify(&[]));
    }

    #[test]
    fn pool_reuses() {
        let mut p = Pool::new(4);
        let a = p.get(10);
        assert_eq!(a, vec![0; 10]);
        p.put(a);
        assert_eq!(p.available(), 1);
        let b = p.copy_of(&[1, 2, 3]);
        assert_eq!(b, vec![1, 2, 3]);
        assert_eq!(p.available(), 0);
        p.put(vec![0; 70000]);
        assert_eq!(p.available(), 0);
    }
}
