//! The vanilla game frame on Direct Transport channel 2, as `DirectTransportCodec.ParseGameFrame`
//! reads it: `[0xCE][crcFlag][crc32 LE][partIndex][partCount][messageId][receiverIndex]...`.

/// Game message ids the relay acts on (VanillaJoinCodec / ShouldRelayServerPacket).
pub mod message {
    pub const SERVER_DATA: u8 = 6;
    pub const TARGET_READY: u8 = 7;
    pub const CLIENT_READY: u8 = 8;
    pub const CLIENT_CONNECTED: u8 = 14;
    /// Join-sequence messages a node re-sends after a switch; dropped under join suppression.
    pub const JOIN_SEQUENCE: [u8; 4] = [2, 12, 13, 20];
}

/// The channel that carries vanilla game frames.
pub const GAME_CHANNEL: u8 = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GameFrame {
    pub has_crc: bool,
    pub part_index: u8,
    pub part_count: u8,
    pub message_id: u8,
    pub receiver_index: u8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameError {
    Header,
    CrcFlag,
    Multipart,
    Crc,
}

/// Parses and (when flagged) CRC-checks a game frame. Same acceptance as the C# codec.
pub fn parse_game_frame(payload: &[u8]) -> Result<GameFrame, FrameError> {
    if payload.len() < 8 || payload[0] != 0xCE {
        return Err(FrameError::Header);
    }
    let crc_flag = payload[1];
    if crc_flag > 1 {
        return Err(FrameError::CrcFlag);
    }
    let (part_index, part_count) = (payload[6], payload[7]);
    if part_count == 0 || part_index >= part_count || part_index == 0 && payload.len() < 10 {
        return Err(FrameError::Multipart);
    }
    if crc_flag == 1 {
        let expected = u32::from_le_bytes([payload[2], payload[3], payload[4], payload[5]]);
        if expected != crc32(&payload[6..]) {
            return Err(FrameError::Crc);
        }
    }
    Ok(GameFrame {
        has_crc: crc_flag != 0,
        part_index,
        part_count,
        message_id: if part_index == 0 { payload[8] } else { 0 },
        receiver_index: if part_index == 0 { payload[9] } else { 0 },
    })
}

/// `TryGetGameMessageId`: the message id of the first part of a channel-2 frame.
/// `Ok(None)` for other channels and for continuation parts.
pub fn game_message_id(channel: u8, payload: &[u8]) -> Result<Option<u8>, FrameError> {
    if channel != GAME_CHANNEL {
        return Ok(None);
    }
    let frame = parse_game_frame(payload)?;
    Ok((frame.part_index == 0).then_some(frame.message_id))
}

/// IEEE CRC-32 (reflected, poly 0xEDB88320), table-driven; equal to the C# bitwise loop.
pub fn crc32(data: &[u8]) -> u32 {
    static TABLE: std::sync::OnceLock<[u32; 256]> = std::sync::OnceLock::new();
    let table = TABLE.get_or_init(|| {
        let mut table = [0u32; 256];
        for (index, entry) in table.iter_mut().enumerate() {
            let mut crc = index as u32;
            for _ in 0..8 {
                crc = (crc >> 1) ^ (0xEDB8_8320 & (crc & 1).wrapping_neg());
            }
            *entry = crc;
        }
        table
    });
    let mut crc = u32::MAX;
    for &byte in data {
        crc = (crc >> 8) ^ table[((crc ^ byte as u32) & 0xFF) as usize];
    }
    !crc
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(message: u8, crc: bool, part: u8, parts: u8) -> Vec<u8> {
        let mut bytes = vec![0xCE, crc as u8, 0, 0, 0, 0, part, parts, message, 0, 1, 2, 3];
        if crc {
            let value = crc32(&bytes[6..]);
            bytes[2..6].copy_from_slice(&value.to_le_bytes());
        }
        bytes
    }

    #[test]
    fn crc32_matches_the_reference_bitwise_loop() {
        // "123456789" is the standard check value for CRC-32/IEEE.
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
        let bitwise = |data: &[u8]| {
            let mut crc = u32::MAX;
            for &value in data {
                crc ^= value as u32;
                for _ in 0..8 {
                    crc = (crc >> 1) ^ (0xEDB8_8320 & (crc & 1).wrapping_neg());
                }
            }
            !crc
        };
        let data: Vec<u8> = (0..1000u32).map(|x| (x * 37 % 251) as u8).collect();
        assert_eq!(crc32(&data), bitwise(&data));
    }

    #[test]
    fn parses_like_the_csharp_codec() {
        assert_eq!(game_message_id(2, &frame(14, true, 0, 1)), Ok(Some(14)));
        assert_eq!(game_message_id(2, &frame(7, false, 0, 1)), Ok(Some(7)));
        assert_eq!(game_message_id(2, &frame(7, false, 1, 2)), Ok(None));
        assert_eq!(game_message_id(0, &[1, 2, 3]), Ok(None));
        let mut bad = frame(14, true, 0, 1);
        bad[12] ^= 1;
        assert_eq!(parse_game_frame(&bad), Err(FrameError::Crc));
        assert_eq!(parse_game_frame(&[0xCD; 12]), Err(FrameError::Header));
        assert_eq!(parse_game_frame(&frame(1, false, 2, 2)), Err(FrameError::Multipart));
        assert_eq!(parse_game_frame(&[0xCE, 0, 0, 0, 0, 0, 0, 1]), Err(FrameError::Multipart));
    }
}
