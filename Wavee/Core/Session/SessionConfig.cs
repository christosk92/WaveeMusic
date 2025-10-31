using System.Net;

namespace Wavee.Core.Session;

/// <summary>
/// Configuration for creating a Spotify session.
/// </summary>
/// <remarks>
/// This configuration is immutable and should be created once per application.
/// All fields have sensible defaults except DeviceId which must be unique per device.
/// </remarks>
public sealed record SessionConfig
{
    /// <summary>
    /// Unique device identifier. Must be stable across sessions.
    /// </summary>
    /// <remarks>
    /// Recommended format: UUID v4 or platform-specific stable ID.
    /// This ID is used for credential encryption and device management.
    /// </remarks>
    public required string DeviceId { get; init; }

    /// <summary>
    /// Device name shown in Spotify Connect UI.
    /// </summary>
    public string DeviceName { get; init; } = "Wavee";

    /// <summary>
    /// Device type for Spotify Connect.
    /// </summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Computer;

    /// <summary>
    /// Spotify client ID (OAuth). If null, uses platform default.
    /// </summary>
    /// <remarks>
    /// Platform defaults:
    /// - Desktop: 65b708073fc0480ea92a077233ca87bd
    /// - Android: 9a8d2f0ce77a4e248bb71fefcb557637
    /// - iOS: 58bd3c95768941ea9eb4350aaa033eb3
    /// </remarks>
    public string? ClientId { get; init; }

    /// <summary>
    /// Access Point port override. If null, uses default (4070).
    /// </summary>
    public int? ApPort { get; init; }

    /// <summary>
    /// HTTP/SOCKS proxy for all connections. If null, no proxy.
    /// </summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>
    /// Enable Spotify Connect subsystem (dealer WebSocket and device state management).
    /// </summary>
    /// <remarks>
    /// When enabled, the session will automatically connect to Spotify's dealer service
    /// and announce the device for remote control. Disable if you only need API access.
    /// </remarks>
    public bool EnableConnect { get; init; } = true;

    /// <summary>
    /// Initial volume level for Spotify Connect (0-65535 range).
    /// </summary>
    /// <remarks>
    /// Spotify uses a 16-bit unsigned integer for volume (0-65535).
    /// Use ConnectStateHelpers.VolumeFromPercentage() to convert from percentage.
    /// Default is 32767 (approximately 50%).
    /// </remarks>
    public int InitialVolume { get; init; } = 32767;

    /// <summary>
    /// Gets the effective client ID (user-provided or platform default).
    /// </summary>
    public string GetClientId()
    {
        if (ClientId is not null)
            return ClientId;

        // Use platform-specific default
        if (OperatingSystem.IsAndroid())
            return "9a8d2f0ce77a4e248bb71fefcb557637";
        if (OperatingSystem.IsIOS())
            return "58bd3c95768941ea9eb4350aaa033eb3";

        // Desktop default
        return "65b708073fc0480ea92a077233ca87bd";
    }

    /// <summary>
    /// Gets the effective AP port (user-provided or default 4070).
    /// </summary>
    public int GetApPort() => ApPort ?? 4070;
}

/// <summary>
/// Spotify Connect device types.
/// </summary>
public enum DeviceType
{
    Unknown = 0,
    Computer = 1,
    Tablet = 2,
    Smartphone = 3,
    Speaker = 4,
    TV = 5,
    AVR = 6,
    STB = 7,
    AudioDongle = 8,
    GameConsole = 9,
    CastVideo = 10,
    CastAudio = 11,
    Automobile = 12,
    Smartwatch = 13,
    Chromebook = 14,
    UnknownSpotify = 100,
    CarThing = 101,
    Observer = 102,
    HomeThing = 103
}
