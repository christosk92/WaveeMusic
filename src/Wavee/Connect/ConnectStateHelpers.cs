using System.Net.NetworkInformation;
using System.Net.Sockets;
using Wavee.Core;
using Wavee.Core.Session;
using Wavee.Protocol.Media;
using Wavee.Protocol.Player;
using SpotifyPlaybackCapabilities = Wavee.Core.Audio.SpotifyPlaybackCapabilities;

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
    /// Full <c>supported_types</c> list emitted by the desktop client. Mirrors
    /// the 14 entries observed in 2026-04-28 SAZ capture so the server treats
    /// us as a full playback device rather than a thin remote-controller.
    /// </summary>
    private static readonly string[] DesktopSupportedTypes =
    {
        "audio/ad",
        "audio/episode",
        "audio/episode+track",
        "audio/interruption",
        "audio/local",
        "audio/media",
        "audio/podcast-chapter",
        "audio/track",
        "audio/user-highlight",
        "video/ad",
        "video/episode",
        "video/podcast-chapter",
        "video/track",
        "video/user-highlight",
    };

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
        var localSpotifyPlaybackEnabled = config.LocalSpotifyPlaybackEnabled;
        var info = new DeviceInfo
        {
            CanPlay = localSpotifyPlaybackEnabled,
            Volume = (uint)Math.Clamp(volume, 0, MaxVolume),
            Name = config.DeviceName,
            DeviceId = config.DeviceId,
            DeviceType = MapDeviceType(config.DeviceType),
            DeviceSoftwareVersion = SpotifyClientIdentity.DeviceSoftwareVersion,
            ClientId = config.ClientId ?? KeymasterClientId,
            SpircVersion = SpotifyClientIdentity.SpircVersion,
            Capabilities = CreateDefaultCapabilities(volumeSteps, localSpotifyPlaybackEnabled),
            // Brand/model: parity with desktop's own DeviceInfo. These show up in
            // remote "Now Playing" device lists and feed device-fingerprint logic
            // server-side. Pre-2026-04 captures show desktop sends "spotify"/"PC laptop".
            Brand = "spotify",
            Model = "PC laptop",
            // license=premium is what tells the play-history pipeline that this
            // device is eligible to register plays. Without it, plays from the
            // device may be excluded from Recently Played.
            License = "premium",
        };

        if (!localSpotifyPlaybackEnabled)
        {
            info.DisallowPlaybackReasons.Add(SpotifyPlaybackCapabilities.DisabledReason);
            info.DisallowTransferReasons.Add(SpotifyPlaybackCapabilities.DisabledReason);
        }

        // metadata_map — desktop sends a small set of debug/network keys here.
        // Include the ones we can derive locally; desktop additionally sets
        // public_ip on a sibling field, but that requires an external probe.
        info.MetadataMap["debug_level"] = "1";
        info.MetadataMap["tier1_port"] = "0";
        var ipMask = TryGetLocalIpMask();
        if (!string.IsNullOrEmpty(ipMask))
            info.MetadataMap["device_address_mask"] = ipMask;

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
    /// Creates default capabilities for a Spotify Connect device. Mirrors the
    /// full capability set the desktop client advertises in its PutStateRequest
    /// so Spotify's Connect / play-history backends classify Wavee as a real
    /// playback device, not a thin controller.
    /// </summary>
    /// <param name="volumeSteps">Number of volume steps (default 64).</param>
    /// <returns>Configured Capabilities instance.</returns>
    public static Capabilities CreateDefaultCapabilities(
        int volumeSteps = DefaultVolumeSteps,
        bool localSpotifyPlaybackEnabled = true)
    {
        var capabilities = new Capabilities
        {
            CanBePlayer = localSpotifyPlaybackEnabled,
            GaiaEqConnectId = true,
            SupportsLogout = true,
            IsObservable = true,
            CommandAcks = true,
            SupportsRename = false,
            SupportsPlaylistV2 = true,
            IsControllable = localSpotifyPlaybackEnabled,
            SupportsExternalEpisodes = true,
            SupportsSetBackendMetadata = true,
            SupportsTransferCommand = localSpotifyPlaybackEnabled,
            SupportsCommandRequest = true,
            VolumeSteps = volumeSteps,
            SupportsGzipPushes = true,
            // Wavee deliberately keeps NeedsFullPlayerState=true so the server
            // pushes complete cluster snapshots rather than incremental deltas.
            // Do NOT flip — Wavee's state machine reconciles from full snapshots.
            NeedsFullPlayerState = true,
            SupportsSetOptionsCommand = true,
            SupportsHifi = new CapabilitySupportDetails
            {
                FullySupported = true,
                UserEligible = true,
                DeviceSupported = true,
            },
            SupportsDj = true,
            // Wavee streams 320 kbps OGG Vorbis. Advertise VeryHigh (matches
            // BitrateLevel.Veryhigh in PlaybackQuality). Captures show desktop
            // sends 5 (HIFI), but claiming HIFI would lie about FLAC support.
            SupportedAudioQuality = AudioQuality.VeryHigh,
        };

        // Full supported_types list — desktop advertises 14 entries.
        foreach (var t in DesktopSupportedTypes)
            capabilities.SupportedTypes.Add(t);

        return capabilities;
    }

    /// <summary>
    /// Creates the <c>private_device_info</c> submessage Spotify desktop attaches
    /// to every connect-state PUT. Carries the host OS descriptor, used by
    /// server-side device classification.
    /// </summary>
    public static PrivateDeviceInfo CreatePrivateDeviceInfo()
    {
        return new PrivateDeviceInfo
        {
            Platform = SpotifyClientIdentity.GetPrivateDevicePlatform(),
        };
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
    /// Best-effort local-IP-with-mask string ("192.168.x.y/24") used for the
    /// <c>device_address_mask</c> metadata key. Returns null if no suitable
    /// active IPv4 interface is found.
    /// </summary>
    private static string? TryGetLocalIpMask()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var prefix = addr.PrefixLength > 0 ? addr.PrefixLength : 24;
                    return $"{addr.Address}/{prefix}";
                }
            }
        }
        catch
        {
            // NIC enumeration can throw on locked-down systems; the metadata key
            // is non-essential, so swallow and skip.
        }
        return null;
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
