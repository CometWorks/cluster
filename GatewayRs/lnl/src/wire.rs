//! The subset of `NetDataWriter`/`NetDataReader` (LiteNetLib.Utils) that connect data and
//! handshakes use: little-endian integers and `ushort (byte count + 1)`-prefixed UTF-8 strings.

/// `NetDataWriter`.
#[derive(Clone, Debug, Default)]
pub struct Writer {
    pub buf: Vec<u8>,
}

impl Writer {
    pub fn new() -> Self {
        Writer { buf: Vec::new() }
    }

    pub fn put_u8(&mut self, v: u8) -> &mut Self {
        self.buf.push(v);
        self
    }

    pub fn put_u16(&mut self, v: u16) -> &mut Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }

    pub fn put_i32(&mut self, v: i32) -> &mut Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }

    pub fn put_u32(&mut self, v: u32) -> &mut Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }

    pub fn put_u64(&mut self, v: u64) -> &mut Self {
        self.buf.extend_from_slice(&v.to_le_bytes());
        self
    }

    pub fn put_bytes(&mut self, v: &[u8]) -> &mut Self {
        self.buf.extend_from_slice(v);
        self
    }

    /// `Put(string)`: empty strings are written as a zero length.
    pub fn put_string(&mut self, s: &str) -> &mut Self {
        if s.is_empty() {
            return self.put_u16(0);
        }
        self.put_u16(u16::try_from(s.len() + 1).expect("string too long for NetDataWriter"));
        self.buf.extend_from_slice(s.as_bytes());
        self
    }
}

/// `NetDataReader`. Every getter returns `None` when the data is too short.
#[derive(Clone, Debug)]
pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Reader { data, pos: 0 }
    }

    pub fn available(&self) -> usize {
        self.data.len() - self.pos
    }

    pub fn remaining(&self) -> &'a [u8] {
        &self.data[self.pos..]
    }

    pub fn get_bytes(&mut self, n: usize) -> Option<&'a [u8]> {
        if self.available() < n {
            return None;
        }
        let s = &self.data[self.pos..self.pos + n];
        self.pos += n;
        Some(s)
    }

    pub fn get_u8(&mut self) -> Option<u8> {
        self.get_bytes(1).map(|b| b[0])
    }

    pub fn get_u16(&mut self) -> Option<u16> {
        self.get_bytes(2).map(|b| u16::from_le_bytes([b[0], b[1]]))
    }

    pub fn get_i32(&mut self) -> Option<i32> {
        self.get_bytes(4)
            .map(|b| i32::from_le_bytes(b.try_into().unwrap()))
    }

    pub fn get_u32(&mut self) -> Option<u32> {
        self.get_bytes(4)
            .map(|b| u32::from_le_bytes(b.try_into().unwrap()))
    }

    pub fn get_u64(&mut self) -> Option<u64> {
        self.get_bytes(8)
            .map(|b| u64::from_le_bytes(b.try_into().unwrap()))
    }

    /// `GetString()`: invalid UTF-8 is replaced, as .NET's decoder does.
    pub fn get_string(&mut self) -> Option<String> {
        let n = self.get_u16()? as usize;
        if n == 0 {
            return Some(String::new());
        }
        self.get_bytes(n - 1)
            .map(|b| String::from_utf8_lossy(b).into_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn handshake_round_trip() {
        let mut w = Writer::new();
        w.put_string("SE-DirectTransport-v1")
            .put_u64(u64::MAX)
            .put_string("");
        assert_eq!(&w.buf[..2], &22u16.to_le_bytes());
        let mut r = Reader::new(&w.buf);
        assert_eq!(r.get_string().unwrap(), "SE-DirectTransport-v1");
        assert_eq!(r.get_u64().unwrap(), u64::MAX);
        assert_eq!(r.get_string().unwrap(), "");
        assert_eq!(r.available(), 0);
        assert!(r.get_u8().is_none());
    }
}
