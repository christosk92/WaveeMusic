using Wavee.Core;
using Wavee.Core.Session;
using Wavee.Protocol.Player;

namespace Wavee.Connect;

/// <summary>
/// Helper methods for building Spotify Connect State protobuf messages.
/// </summary>
public static class ConnectStateHelpers
{
    // Keymaster client ID used for Connect State. Matches what desktop sends.
    private const string KeymasterClientId = "65b708073fc0480ea92a077233ca87bd";

    // Maximum volume (Spotify uses 0-65535 range)
    public const int MaxVolume = 65535;

    // Default volume steps
    public const int DefaultVolumeSteps = 64;

    /// <summary>
    /// Creates a DeviceInfo with full capabilities for Spotify Connect.
    /// </summary>
    /// <param name="config">Session configuration containing device details.</param>
    /// <param name="volume">Initial volume (0-65535).</param>
    /// <param name="volumeSteps">Number of volume steps supported (default 64).</param>
    /// <returns>Configured DeviceInfo instance.</returns>
    public static DeviceInfo CreateDeviceInfo(
        SessionConfig config,
        int volume = MaxVolume / 2,
        int volumeSteps = DefaultVolumeSteps,
        string? audioOutputDeviceName = null,
        AudioOutputDeviceType audioOutputDeviceType = AudioOutputDeviceType.UnknownAudioOutputDeviceType)
    {
        var info = new DeviceInfo
        {
            CanPlay = true,
            Volume = (uint)Math.Clamp(volume, 0, MaxVolume),
            Name = config.DeviceName,
            DeviceId = config.DeviceId,
            DeviceType = MapDeviceType(config.DeviceType),
            DeviceSoftwareVersion = SpotifyClientIdentity.DeviceSoftwareVersion,
            ClientId = config.ClientId ?? KeymasterClientId,
            SpircVersion = SpotifyClientIdentity.SpircVersion,
            Capabilities = CreateDefaultCapabilities(volumeSteps)
        };

        // audio_output_device_info — Spotify desktop emits this on every DeviceInfo
        // so remote "Connect to" sheets can show the speaker/headphone we're routing
        // to. Populate when we know the current PortAudio-selected device.
        if (!string.IsNullOrEmpty(audioOutputDeviceName))
        {
            info.AudioOutputDeviceInfo = new AudioOutputDeviceInfo
            {
                DeviceName = audioOutputDeviceName,
                AudioOutputDeviceType = audioOutputDeviceType
            };
        }

        return info;
    }

    /// <summary>
    /// Creates default capabilities for a Spotify Connect device.
    /// </summary>
    /// <param name="volumeSteps">Number of volume steps (default 64).</param>
    /// <returns>Configured Capabilities instance.</returns>
    public static Capabilities CreateDefaultCapabilities(int volumeSteps = DefaultVolumeSteps)
    {
        var capabilities = new Capabilities
        {
            CanBePlayer = true,
            GaiaEqConnectId = true,
            SupportsLogout = true,
            IsObservable = true,
            CommandAcks = true,
            SupportsRename = false,
            SupportsPlaylistV2 = true,
            IsControllable = true,
            SupportsTransferCommand = true,
            SupportsCommandRequest = true,
            VolumeSteps = volumeSteps,
            SupportsGzipPushes = true,
            NeedsFullPlayerState = true
        };

        // Add supported content types
        capabilities.SupportedTypes.Add("audio/track");
        capabilities.SupportedTypes.Add("audio/episode");

        return capabilities;
    }

    /// <summary>
    /// Creates an empty PlayerState for initial device announcement.
    /// </summary>
    /// <returns>Empty PlayerState instance.</returns>
    public static PlayerState CreateEmptyPlayerState()
    {
        return new PlayerState
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    /// <summary>
    /// Maps Wavee's DeviceType enum to the protobuf DeviceType enum.
    /// </summary>
    /// <param name="deviceType">Wavee device type.</param>
    /// <returns>Protobuf device type.</returns>
    private static global::Wavee.Protocol.Player.DeviceType MapDeviceType(Core.Session.DeviceType deviceType)
    {
        // Enum values match, so we can cast directly
        return (global::Wavee.Protocol.Player.DeviceType)(int)deviceType;
    }

    /// <summary>
    /// Converts a volume percentage (0-100) to Spotify's volume range (0-65535).
    /// </summary>
    /// <param name="percentage">Volume percentage (0-100).</param>
    /// <returns>Spotify volume value (0-65535).</returns>
    public static int VolumeFromPercentage(int percentage)
    {
        return (int)Math.Round((percentage / 100.0) * MaxVolume);
    }

    /// <summary>
    /// Converts Spotify's volume range (0-65535) to a percentage (0-100).
    /// </summary>
    /// <param name="spotifyVolume">Spotify volume value (0-65535).</param>
    /// <returns>Volume percentage (0-100).</returns>
    public static int VolumeToPercentage(int spotifyVolume)
    {
        return (int)Math.Round((spotifyVolume / (double)MaxVolume) * 100);
    }
}
