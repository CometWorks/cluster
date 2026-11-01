//! The gateway <-> node link wire format (DirectTransportCodec.cs): the LiteNetLib connect data
//! of a node link, the client handshake, and the `[kind u8][clientId u64 LE][body]` envelope.
//! Validation matches the C# decoder exactly; an envelope it rejects closes the node link.

pub const PROTOCOL_KEY: &str = "SE-DirectTransport-v1";
pub const NODE_LINK_KEY: &str = "SE-ClusterNodeLink-v2";
pub const NODE_LINK_ID: u64 = u64::MAX;
pub const ATTACH_LIMIT: usize = 4096;
pub const PLUGIN_LIMIT: usize = 128 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Kind {
    Relay = 1,
    Attach = 2,
    Detach = 3,
    AttachAck = 4,
    DetachAck = 5,
    Credit = 6,
    Global = 7,
    Plugin = 8,
}

impl Kind {
    fn from_byte(value: u8) -> Option<Self> {
        Some(match value {
            1 => Kind::Relay,
            2 => Kind::Attach,
            3 => Kind::Detach,
            4 => Kind::AttachAck,
            5 => Kind::DetachAck,
            6 => Kind::Credit,
            7 => Kind::Global,
            8 => Kind::Plugin,
            _ => return None,
        })
    }
}

/// A decoded envelope; `body` borrows the datagram.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Envelope<'a> {
    Relay { client_id: u64, channel: u8, payload: &'a [u8] },
    Attach { client_id: u64, body: &'a [u8] },
    Detach { client_id: u64 },
    AttachAck { client_id: u64 },
    DetachAck { client_id: u64 },
    Credit { client_id: u64, credit: i32 },
    Global { payload: &'a [u8] },
    Plugin { payload: &'a [u8] },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Invalid(pub &'static str);

pub fn decode(datagram: &[u8]) -> Result<Envelope<'_>, Invalid> {
    if datagram.len() < 9 {
        return Err(Invalid("Node-link packet is truncated"));
    }
    let client_id = u64::from_le_bytes(datagram[1..9].try_into().unwrap());
    let rest = &datagram[9..];
    let kind = Kind::from_byte(datagram[0]).ok_or(Invalid("Unknown or truncated node-link packet"))?;
    Ok(match kind {
        Kind::Attach if rest.len() > ATTACH_LIMIT => return Err(Invalid("Node-link attach payload is too large")),
        Kind::Attach => Envelope::Attach { client_id, body: rest },
        Kind::Detach | Kind::AttachAck | Kind::DetachAck if !rest.is_empty() => {
            return Err(Invalid("Node-link control packet has trailing data"));
        }
        Kind::Detach => Envelope::Detach { client_id },
        Kind::AttachAck => Envelope::AttachAck { client_id },
        Kind::DetachAck => Envelope::DetachAck { client_id },
        Kind::Credit => {
            let bytes: [u8; 4] = rest.try_into().map_err(|_| Invalid("Invalid node-link credit packet"))?;
            let credit = i32::from_le_bytes(bytes);
            if credit <= 0 {
                return Err(Invalid("Node-link credit must be positive"));
            }
            Envelope::Credit { client_id, credit }
        }
        Kind::Global | Kind::Plugin => {
            // No cap on Global: a full global-state sync legitimately exceeds 4 KB (the 2026-08-10
            // disconnect loop came from one).
            if client_id != 0 || rest.is_empty() || kind == Kind::Plugin && rest.len() > PLUGIN_LIMIT {
                return Err(Invalid("Invalid node-link global packet"));
            }
            if kind == Kind::Plugin { Envelope::Plugin { payload: rest } } else { Envelope::Global { payload: rest } }
        }
        Kind::Relay => {
            let (&channel, payload) = rest.split_first().ok_or(Invalid("Unknown or truncated node-link packet"))?;
            Envelope::Relay { client_id, channel, payload }
        }
    })
}

/// Encodes into `out` (cleared first) so the hot path can reuse one buffer.
pub fn encode_into(out: &mut Vec<u8>, envelope: &Envelope<'_>) -> Result<(), Invalid> {
    out.clear();
    let (kind, client_id) = match *envelope {
        Envelope::Relay { client_id, .. } => (Kind::Relay, client_id),
        Envelope::Attach { client_id, body } => {
            if body.len() > ATTACH_LIMIT {
                return Err(Invalid("Node-link attach payload is too large"));
            }
            (Kind::Attach, client_id)
        }
        Envelope::Detach { client_id } => (Kind::Detach, client_id),
        Envelope::AttachAck { client_id } => (Kind::AttachAck, client_id),
        Envelope::DetachAck { client_id } => (Kind::DetachAck, client_id),
        Envelope::Credit { client_id, .. } => (Kind::Credit, client_id),
        Envelope::Global { .. } => (Kind::Global, 0),
        Envelope::Plugin { .. } => (Kind::Plugin, 0),
    };
    out.push(kind as u8);
    out.extend_from_slice(&client_id.to_le_bytes());
    match *envelope {
        Envelope::Relay { channel, payload, .. } => {
            out.push(channel);
            out.extend_from_slice(payload);
        }
        Envelope::Attach { body: payload, .. } | Envelope::Global { payload } | Envelope::Plugin { payload } => {
            out.extend_from_slice(payload)
        }
        Envelope::Credit { credit, .. } => out.extend_from_slice(&credit.to_le_bytes()),
        _ => {}
    }
    Ok(())
}

pub fn encode(envelope: &Envelope<'_>) -> Result<Vec<u8>, Invalid> {
    let mut out = Vec::new();
    encode_into(&mut out, envelope)?;
    Ok(out)
}

/// LiteNetLib 1.3.1 `NetDataWriter.Put(string)`: u16 (UTF-8 length + 1), then the bytes; 0 = empty.
pub fn put_string(out: &mut Vec<u8>, value: &str) {
    if value.is_empty() {
        out.extend_from_slice(&0u16.to_le_bytes());
        return;
    }
    out.extend_from_slice(&((value.len() + 1) as u16).to_le_bytes());
    out.extend_from_slice(value.as_bytes());
}

/// LiteNetLib 1.3.1 `NetDataReader.GetString()`.
pub fn get_string<'a>(input: &mut &'a [u8]) -> Option<&'a str> {
    let size = u16::from_le_bytes([*input.first()?, *input.get(1)?]) as usize;
    *input = &input[2..];
    if size == 0 {
        return Some("");
    }
    let length = size - 1;
    let text = std::str::from_utf8(input.get(..length)?).ok()?;
    *input = &input[length..];
    Some(text)
}

/// The connect data a gateway sends when opening a node link (`EncodeNodeLinkHandshake`).
pub fn node_link_handshake(join_token: &str) -> Vec<u8> {
    let mut out = Vec::with_capacity(64 + join_token.len());
    put_string(&mut out, PROTOCOL_KEY);
    out.extend_from_slice(&NODE_LINK_ID.to_le_bytes());
    put_string(&mut out, NODE_LINK_KEY);
    put_string(&mut out, join_token);
    out
}

/// A Direct client's connect data (`TryDecodeHandshake`): the client id and an optional name.
/// `None` when it is not a client handshake (a node link's id is refused too).
pub fn client_handshake(mut data: &[u8]) -> Option<(u64, Option<String>)> {
    if get_string(&mut data)? != PROTOCOL_KEY || data.len() < 8 {
        return None;
    }
    let client_id = u64::from_le_bytes(data[..8].try_into().unwrap());
    data = &data[8..];
    if client_id == NODE_LINK_ID {
        return None;
    }
    let name = if data.is_empty() { None } else { get_string(&mut data).and_then(sanitize_name) };
    Some((client_id, name))
}

/// `SanitizeName`: trimmed, control characters removed, at most 32 UTF-16 units, never empty.
fn sanitize_name(asserted: &str) -> Option<String> {
    let mut clean = String::new();
    let mut units = 0;
    for character in asserted.trim().chars().filter(|c| !c.is_control()) {
        units += character.len_utf16();
        if units > 32 {
            break;
        }
        clean.push(character);
    }
    (!clean.is_empty()).then_some(clean)
}

/// The LiteNetLib channel of a client's frames on a node link (`clientId % NodeLinkChannels`).
pub fn channel_of(client_id: u64, channels: u8) -> u8 {
    (client_id % channels.max(1) as u64) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_every_kind() {
        let cases = [
            Envelope::Relay { client_id: 42, channel: 2, payload: &[0xCE, 1, 2] },
            Envelope::Attach { client_id: 42, body: b"{\"a\":1}" },
            Envelope::Detach { client_id: 42 },
            Envelope::AttachAck { client_id: 42 },
            Envelope::DetachAck { client_id: 42 },
            Envelope::Credit { client_id: 42, credit: 4 * 1024 * 1024 },
            Envelope::Global { payload: b"{}" },
            Envelope::Plugin { payload: b"x" },
        ];
        for case in cases {
            assert_eq!(decode(&encode(&case).unwrap()).unwrap(), case);
        }
        // The exact C# layout for a Credit: kind, id LE, i32 LE.
        assert_eq!(encode(&Envelope::Credit { client_id: 1, credit: 30 }).unwrap(), [6, 1, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0]);
    }

    #[test]
    fn rejects_what_the_csharp_decoder_rejects() {
        let id = 5u64.to_le_bytes();
        let frame = |kind: u8, body: &[u8], client: &[u8; 8]| [&[kind][..], client, body].concat();
        assert!(decode(&[1, 0, 0]).is_err());
        assert!(decode(&frame(9, &[], &id)).is_err());
        assert!(decode(&frame(1, &[], &id)).is_err(), "relay without a channel");
        assert!(decode(&frame(3, &[0], &id)).is_err(), "control with trailing data");
        assert!(decode(&frame(6, &0i32.to_le_bytes(), &id)).is_err(), "non-positive credit");
        assert!(decode(&frame(6, &[1, 0, 0], &id)).is_err());
        assert!(decode(&frame(2, &vec![0; 4097], &id)).is_err());
        assert!(decode(&frame(2, &vec![0; 4096], &id)).is_ok());
        assert!(decode(&frame(7, b"{}", &id)).is_err(), "global with a client id");
        assert!(decode(&frame(7, &[], &[0; 8])).is_err(), "empty global");
        assert!(decode(&frame(7, &vec![0; 200_000], &[0; 8])).is_ok(), "global is not capped");
        assert!(decode(&frame(8, &vec![0; PLUGIN_LIMIT + 1], &[0; 8])).is_err());
        assert!(encode(&Envelope::Attach { client_id: 1, body: &[0; 4097] }).is_err());
    }

    #[test]
    fn handshakes() {
        let data = node_link_handshake("tok");
        let mut expected = vec![22, 0];
        expected.extend_from_slice(PROTOCOL_KEY.as_bytes());
        expected.extend_from_slice(&[0xFF; 8]);
        expected.extend_from_slice(&[22, 0]);
        expected.extend_from_slice(NODE_LINK_KEY.as_bytes());
        expected.extend_from_slice(&[4, 0]);
        expected.extend_from_slice(b"tok");
        assert_eq!(data, expected);
        assert_eq!(client_handshake(&data), None, "a node link is not a client");

        let mut client = Vec::new();
        put_string(&mut client, PROTOCOL_KEY);
        client.extend_from_slice(&77u64.to_le_bytes());
        assert_eq!(client_handshake(&client), Some((77, None)));
        put_string(&mut client, "  pilot\u{7}-7 ");
        assert_eq!(client_handshake(&client), Some((77, Some("pilot-7".into()))));
        let mut long = Vec::new();
        put_string(&mut long, PROTOCOL_KEY);
        long.extend_from_slice(&77u64.to_le_bytes());
        put_string(&mut long, &"x".repeat(40));
        assert_eq!(client_handshake(&long).unwrap().1.unwrap().len(), 32);
        assert_eq!(client_handshake(b"\x03\x00ab"), None);
    }

    #[test]
    fn channels() {
        assert_eq!(channel_of(33, 16), 1);
        assert_eq!(channel_of(32, 16), 0);
    }
}
