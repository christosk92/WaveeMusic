namespace Wavee.Core.Session;

/// <summary>
/// Spotify protocol packet command types.
/// </summary>
/// <remarks>
/// These values are defined by the Spotify protocol specification.
/// See: https://github.com/librespot-org/librespot/blob/master/core/src/packet.rs
/// </remarks>
public enum PacketType : byte
{
    // Authentication and streaming
    SecretBlock = 0x02,
    Ping = 0x04,
    StreamChunk = 0x08,
    StreamChunkRes = 0x09,
    ChannelError = 0x0a,
    ChannelAbort = 0x0b,
    RequestKey = 0x0c,
    AesKey = 0x0d,
    AesKeyError = 0x0e,
    Unknown0x0f = 0x0f,
    Unknown0x10 = 0x10,

    // Image and session data
    Image = 0x19,
    CountryCode = 0x1b,
    UnknownDataAllZeros = 0x1f,

    // Session management
    Pong = 0x49,
    PongAck = 0x4a,
    Pause = 0x4b,
    Unknown0x4f = 0x4f,
    ProductInfo = 0x50,
    LegacyWelcome = 0x69,
    PreferredLocale = 0x74,
    LicenseVersion = 0x76,
    TrackEndedTime = 0x82,

    // Authentication packets
    Login = 0xab,
    APWelcome = 0xac,
    AuthFailure = 0xad,

    // Mercury request-response protocol
    MercuryReq = 0xb2,
    MercurySub = 0xb3,
    MercuryUnsub = 0xb4,
    MercuryEvent = 0xb5,
    Unknown0xb6 = 0xb6,

    // Sentinel for unknown packet types
    Unknown = 0xFF
}
