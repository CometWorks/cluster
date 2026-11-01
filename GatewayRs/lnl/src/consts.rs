//! Constants and enums of LiteNetLib 1.3.1 (NetConstants.cs, PacketProperty.cs, DeliveryMethod.cs,
//! DisconnectReason.cs, ConnectionState.cs). Numeric values are wire- or API-visible and must not change.

/// `NetConstants.DefaultWindowSize`: reliable window, in packets.
pub const DEFAULT_WINDOW_SIZE: usize = 64;
/// `NetConstants.HeaderSize`.
pub const HEADER_SIZE: usize = 1;
/// `NetConstants.ChanneledHeaderSize`: property, sequence (u16), channel id.
pub const CHANNELED_HEADER_SIZE: usize = 4;
/// `NetConstants.FragmentHeaderSize`: fragment id, part, total (3 x u16).
pub const FRAGMENT_HEADER_SIZE: usize = 6;
/// `NetConstants.FragmentedHeaderTotalSize`.
pub const FRAGMENTED_HEADER_TOTAL_SIZE: usize = 10;
/// `NetConstants.MaxSequence`.
pub const MAX_SEQUENCE: i32 = 32768;
/// `NetConstants.HalfMaxSequence`.
pub const HALF_MAX_SEQUENCE: i32 = 16384;
/// `NetConstants.ProtocolId`.
pub const PROTOCOL_ID: i32 = 13;
/// `NetConstants.ChannelTypeCount`: channel index = channel number * 4 + delivery method.
pub const CHANNEL_TYPE_COUNT: usize = 4;
/// `NetConstants.PossibleMtu`.
pub const POSSIBLE_MTU: [usize; 6] = [1024, 1164, 1392, 1404, 1424, 1432];
/// `NetConstants.InitialMtu`.
pub const INITIAL_MTU: usize = POSSIBLE_MTU[0];
/// `NetConstants.MaxPacketSize`: the largest datagram LiteNetLib sends or receives.
pub const MAX_PACKET_SIZE: usize = POSSIBLE_MTU[5];
/// `NetConstants.MaxUnreliableDataSize`.
pub const MAX_UNRELIABLE_DATA_SIZE: usize = MAX_PACKET_SIZE - 1;
/// `NetConstants.MaxConnectionNumber`.
pub const MAX_CONNECTION_NUMBER: u8 = 4;

/// `NetPeer.ShutdownDelay`: Disconnect resend interval, ms.
pub(crate) const SHUTDOWN_DELAY_MS: f64 = 300.0;
/// `NetPeer.MtuCheckDelay`, ms.
pub(crate) const MTU_CHECK_DELAY_MS: f64 = 1000.0;
/// `NetPeer.MaxMtuCheckAttempts`.
pub(crate) const MAX_MTU_CHECK_ATTEMPTS: i32 = 4;
/// .NET `DateTime` ticks at the Unix epoch.
pub(crate) const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

/// `PacketProperty`: the low 5 bits of the first header byte.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum PacketProperty {
    Unreliable = 0,
    Channeled = 1,
    Ack = 2,
    Ping = 3,
    Pong = 4,
    ConnectRequest = 5,
    ConnectAccept = 6,
    Disconnect = 7,
    UnconnectedMessage = 8,
    MtuCheck = 9,
    MtuOk = 10,
    Broadcast = 11,
    Merged = 12,
    ShutdownOk = 13,
    PeerNotFound = 14,
    InvalidProtocol = 15,
    NatMessage = 16,
    Empty = 17,
}

/// Number of `PacketProperty` values (`NetPacket.PropertiesCount`).
pub(crate) const PROPERTIES_COUNT: u8 = 18;

impl PacketProperty {
    pub fn from_u8(v: u8) -> Option<Self> {
        use PacketProperty::*;
        Some(match v {
            0 => Unreliable,
            1 => Channeled,
            2 => Ack,
            3 => Ping,
            4 => Pong,
            5 => ConnectRequest,
            6 => ConnectAccept,
            7 => Disconnect,
            8 => UnconnectedMessage,
            9 => MtuCheck,
            10 => MtuOk,
            11 => Broadcast,
            12 => Merged,
            13 => ShutdownOk,
            14 => PeerNotFound,
            15 => InvalidProtocol,
            16 => NatMessage,
            17 => Empty,
            _ => return None,
        })
    }

    /// `NetPacket.HeaderSizes`.
    pub const fn header_size(self) -> usize {
        match self {
            PacketProperty::Channeled | PacketProperty::Ack => 4,
            PacketProperty::Ping => 3,
            PacketProperty::ConnectRequest => 18,
            PacketProperty::ConnectAccept => 15,
            PacketProperty::Disconnect => 9,
            PacketProperty::Pong => 11,
            _ => 1,
        }
    }
}

/// `DeliveryMethod`. The value of the four channeled methods is the channel type in the channel index.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum DeliveryMethod {
    ReliableUnordered = 0,
    Sequenced = 1,
    ReliableOrdered = 2,
    ReliableSequenced = 3,
    Unreliable = 4,
}

impl DeliveryMethod {
    pub fn from_u8(v: u8) -> Option<Self> {
        Some(match v {
            0 => DeliveryMethod::ReliableUnordered,
            1 => DeliveryMethod::Sequenced,
            2 => DeliveryMethod::ReliableOrdered,
            3 => DeliveryMethod::ReliableSequenced,
            4 => DeliveryMethod::Unreliable,
            _ => return None,
        })
    }

    pub const ALL: [DeliveryMethod; 5] = [
        DeliveryMethod::ReliableUnordered,
        DeliveryMethod::Sequenced,
        DeliveryMethod::ReliableOrdered,
        DeliveryMethod::ReliableSequenced,
        DeliveryMethod::Unreliable,
    ];
}

/// `DisconnectReason`, reported in [`crate::Event::Disconnected`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum DisconnectReason {
    ConnectionFailed = 0,
    Timeout = 1,
    HostUnreachable = 2,
    NetworkUnreachable = 3,
    RemoteConnectionClose = 4,
    DisconnectPeerCalled = 5,
    ConnectionRejected = 6,
    InvalidProtocol = 7,
    UnknownHost = 8,
    Reconnect = 9,
    PeerToPeerConnection = 10,
    PeerNotFound = 11,
}

/// `ConnectionState` (the C# enum is a flags enum; a peer is always in exactly one state).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum ConnectionState {
    Outgoing = 2,
    Connected = 4,
    ShutdownRequested = 8,
    Disconnected = 0x10,
    EndPointChange = 0x20,
}

/// `NetUtils.RelativeSequenceNumber`: signed distance `number - expected` on the 15-bit ring.
#[inline]
pub(crate) fn relative_sequence_number(number: i32, expected: i32) -> i32 {
    (number - expected + MAX_SEQUENCE + HALF_MAX_SEQUENCE) % MAX_SEQUENCE - HALF_MAX_SEQUENCE
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relative_sequence_wraps() {
        assert_eq!(relative_sequence_number(5, 3), 2);
        assert_eq!(relative_sequence_number(3, 5), -2);
        assert_eq!(relative_sequence_number(0, 32767), 1);
        assert_eq!(relative_sequence_number(32767, 0), -1);
        assert_eq!(relative_sequence_number(16383, 0), 16383);
        assert_eq!(relative_sequence_number(16384, 0), -16384);
    }

    #[test]
    fn header_sizes_match_litenetlib() {
        let expected = [1, 4, 4, 3, 11, 18, 15, 9, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        for (i, e) in expected.iter().enumerate() {
            assert_eq!(
                PacketProperty::from_u8(i as u8).unwrap().header_size(),
                *e,
                "property {i}"
            );
        }
        assert!(PacketProperty::from_u8(PROPERTIES_COUNT).is_none());
    }
}
