//! The message script shared with the C# peer (interop/dotnet/Program.cs); keep both in step.
//!
//! Every payload is `[method u8][channel u8][seq u32 LE][len u32 LE]` followed by a pattern derived
//! from those fields, so the receiver checks integrity, method, channel and ordering on its own.

use std::collections::HashMap;

use lnl::DeliveryMethod;

pub const KEY: &str = "SE-DirectTransport-v1";
pub const CLIENT_ID: u64 = 4242;
pub const VERIFIED: u8 = 0xFF;
pub const VERIFIED_ACK: u8 = 0xFE;
pub const CHANNELS: u8 = 16;

const CHANNELED: [DeliveryMethod; 4] = [
    DeliveryMethod::ReliableUnordered,
    DeliveryMethod::Sequenced,
    DeliveryMethod::ReliableOrdered,
    DeliveryMethod::ReliableSequenced,
];

pub type Plan = Vec<(DeliveryMethod, u8, usize)>;

pub fn plan(per: u32, big: bool) -> Plan {
    let mut v = Vec::new();
    for c in 0..CHANNELS {
        for m in CHANNELED {
            for s in 0..per as usize {
                v.push((m, c, 10 + (s * 97 + c as usize * 13 + m as usize * 7) % 990));
            }
        }
    }
    for s in 0..40usize {
        v.push((DeliveryMethod::Unreliable, 0, 10 + s * 53 % 1000));
    }
    if big {
        v.push((DeliveryMethod::ReliableOrdered, 1, 65536));
        v.push((DeliveryMethod::ReliableOrdered, 1, 3000));
        v.push((DeliveryMethod::ReliableOrdered, 1, 65536));
        v.push((DeliveryMethod::ReliableUnordered, 2, 65536));
        v.push((DeliveryMethod::ReliableUnordered, 2, 5000));
    }
    v
}

pub fn pattern(m: u8, c: u8, seq: u32, i: usize) -> u8 {
    (seq.wrapping_mul(31)
        .wrapping_add((i * 7 + m as usize * 13 + c as usize) as u32)) as u8
}

pub fn payload(m: DeliveryMethod, c: u8, seq: u32, len: usize) -> Vec<u8> {
    let mut v = Vec::with_capacity(len);
    v.push(m as u8);
    v.push(c);
    v.extend_from_slice(&seq.to_le_bytes());
    v.extend_from_slice(&(len as u32).to_le_bytes());
    v.extend((10..len).map(|i| pattern(m as u8, c, seq, i)));
    v
}

/// The plan as (payload, channel, method) in send order.
pub fn messages(plan: &Plan) -> Vec<(Vec<u8>, u8, DeliveryMethod)> {
    let mut seqs: HashMap<(u8, u8), u32> = HashMap::new();
    plan.iter()
        .map(|&(m, c, len)| {
            let s = seqs.entry((m as u8, c)).or_default();
            let p = payload(m, c, *s, len);
            *s += 1;
            (p, c, m)
        })
        .collect()
}

#[derive(Default)]
pub struct Verifier {
    expected: HashMap<(u8, u8), u32>,
    ordered_next: HashMap<(u8, u8), u32>,
    seen: HashMap<(u8, u8), Vec<bool>>,
    last: HashMap<(u8, u8), u32>,
    pub received: u64,
    pub unreliable: u64,
    /// Sequenced and ReliableSequenced messages delivered.
    pub sequenced: [u64; 2],
}

impl Verifier {
    pub fn new(plan: &Plan) -> Self {
        let mut v = Verifier::default();
        for &(m, c, _) in plan {
            *v.expected.entry((m as u8, c)).or_default() += 1;
        }
        v
    }

    pub fn on_receive(
        &mut self,
        channel: u8,
        method: DeliveryMethod,
        data: &[u8],
    ) -> Result<(), String> {
        if data.len() < 10 {
            return Err(format!("short payload {}", data.len()));
        }
        let (m, c) = (data[0], data[1]);
        let seq = u32::from_le_bytes(data[2..6].try_into().unwrap());
        let len = u32::from_le_bytes(data[6..10].try_into().unwrap()) as usize;
        if len != data.len() {
            return Err(format!("length {} != header {len}", data.len()));
        }
        if let Some(i) = (10..data.len()).find(|&i| data[i] != pattern(m, c, seq, i)) {
            return Err(format!("corrupt byte {i} in m{m} c{c} seq{seq}"));
        }
        if m != method as u8 {
            return Err(format!("method mismatch {m} vs {method:?}"));
        }
        let channel_ok = if method == DeliveryMethod::Unreliable {
            channel == 0
        } else {
            channel == c
        };
        if !channel_ok {
            return Err(format!("channel mismatch {c} vs {channel}"));
        }
        let key = (m, c);
        let n = *self
            .expected
            .get(&key)
            .ok_or(format!("unexpected m{m} c{c}"))?;
        if seq >= n {
            return Err(format!("seq {seq} out of plan for m{m} c{c}"));
        }
        self.received += 1;
        match method {
            DeliveryMethod::ReliableOrdered => {
                let next = self.ordered_next.entry(key).or_default();
                if seq != *next {
                    return Err(format!(
                        "ReliableOrdered out of order on channel {c}: {seq} expected {next}"
                    ));
                }
                *next += 1;
            }
            DeliveryMethod::ReliableUnordered => {
                let seen = self
                    .seen
                    .entry(key)
                    .or_insert_with(|| vec![false; n as usize]);
                if seen[seq as usize] {
                    return Err(format!("ReliableUnordered duplicate {seq} on channel {c}"));
                }
                seen[seq as usize] = true;
            }
            DeliveryMethod::Sequenced | DeliveryMethod::ReliableSequenced => {
                if let Some(&l) = self.last.get(&key)
                    && seq <= l
                {
                    return Err(format!(
                        "sequenced not increasing: {seq} after {l} (m{m} c{c})"
                    ));
                }
                self.last.insert(key, seq);
                self.sequenced[(method == DeliveryMethod::ReliableSequenced) as usize] += 1;
            }
            DeliveryMethod::Unreliable => self.unreliable += 1,
        }
        Ok(())
    }

    /// Messages received per delivery method, e.g. "RU 192/192 Seq 190/192 ...".
    pub fn summary(&self) -> String {
        let mut counts = [(0u64, 0u64); 5];
        for (&(m, c), &n) in &self.expected {
            let got = match DeliveryMethod::from_u8(m).unwrap() {
                DeliveryMethod::ReliableOrdered => {
                    *self.ordered_next.get(&(m, c)).unwrap_or(&0) as u64
                }
                DeliveryMethod::ReliableUnordered => self
                    .seen
                    .get(&(m, c))
                    .map_or(0, |s| s.iter().filter(|x| **x).count() as u64),
                _ => 0,
            };
            counts[m as usize].0 += got;
            counts[m as usize].1 += n as u64;
        }
        counts[1].0 = self.sequenced[0];
        counts[3].0 = self.sequenced[1];
        counts[4].0 = self.unreliable;
        let names = ["RU", "Seq", "RO", "RSeq", "Unrel"];
        names
            .iter()
            .zip(counts)
            .map(|(n, (g, e))| format!("{n} {g}/{e}"))
            .collect::<Vec<_>>()
            .join(" ")
    }

    pub fn complete(&self) -> bool {
        self.expected
            .iter()
            .all(|(&(m, c), &n)| match DeliveryMethod::from_u8(m).unwrap() {
                DeliveryMethod::ReliableOrdered => self.ordered_next.get(&(m, c)) == Some(&n),
                DeliveryMethod::ReliableUnordered => {
                    self.seen.get(&(m, c)).is_some_and(|s| s.iter().all(|x| *x))
                }
                DeliveryMethod::ReliableSequenced => self.last.get(&(m, c)) == Some(&(n - 1)),
                _ => true,
            })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn verifier_accepts_the_plan_and_rejects_faults() {
        let p = plan(3, true);
        let msgs = messages(&p);
        let mut v = Verifier::new(&p);
        for (data, c, m) in &msgs {
            v.on_receive(
                if *m == DeliveryMethod::Unreliable {
                    0
                } else {
                    *c
                },
                *m,
                data,
            )
            .unwrap();
        }
        assert!(v.complete());
        assert_eq!(v.unreliable, 40);

        let mut v = Verifier::new(&p);
        let ro: Vec<_> = msgs
            .iter()
            .filter(|(_, c, m)| *m == DeliveryMethod::ReliableOrdered && *c == 0)
            .collect();
        assert!(
            v.on_receive(0, DeliveryMethod::ReliableOrdered, &ro[1].0)
                .is_err()
        );
        let mut corrupt = ro[0].0.clone();
        *corrupt.last_mut().unwrap() ^= 1;
        assert!(
            v.on_receive(0, DeliveryMethod::ReliableOrdered, &corrupt)
                .is_err()
        );
        assert!(
            v.on_receive(3, DeliveryMethod::ReliableOrdered, &ro[0].0)
                .is_err()
        );
        assert!(!v.complete());
    }
}
