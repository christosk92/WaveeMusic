namespace Wavee.Core;

/// <summary>
/// Single source of truth for the version / identity strings that Wavee presents
/// to Spotify's servers. Keeping these centralized matters because the server
/// reads them in at least five different places — handshake build info, connect
/// device info, authenticator version string, and two HTTP headers on pathfinder
/// and lyrics calls. When one drifts, the server flags us as deprecated
/// (<c>ProductInfo.client-deprecated = 1</c>) and starts throttling or rejecting
/// requests while the others still look fine in logs, making diagnosis painful.
///
/// Values track current Spotify Desktop. Bump all three together when updating.
/// </summary>
public static class SpotifyClientIdentity
{
    /// <summary>
    /// Semantic version of the tracked Spotify Desktop release (e.g. the dotted
    /// quad shown in Spotify's About dialog). Bump this first when refreshing,
    /// then re-derive the numeric <see cref="HandshakeBuildVersion"/>.
    /// Verified against live SAZ capture 2026-04-28 showing
    /// <c>User-Agent: Spotify/128800483 Win32_x86_64/Windows 10 (10.0.26200; x64[native:ARM])</c>.
    /// </summary>
    public const string DesktopSemver = "1.2.88.483";

    /// <summary>
    /// Git short-sha suffix appended to the semver for Connect <c>DeviceInfo</c>.
    /// Visible to other Connect clients as the device's software version string.
    /// </summary>
    public const string DesktopBuildSha = "g8aa8628e";

    /// <summary>
    /// Full <c>DeviceInfo.DeviceSoftwareVersion</c> string sent on PUT-state.
    /// Matches the value live Spotify Desktop reports in its own DeviceInfo.
    /// </summary>
    public const string DeviceSoftwareVersion = DesktopSemver + "." + DesktopBuildSha;

    /// <summary>
    /// Numeric build version Spotify encodes from <see cref="DesktopSemver"/>.
    /// Encoding (verified from SAZ capture <c>Spotify/128800483</c> for
    /// semver 1.2.88.483): digits pack as
    /// <c>{major}{minor}{patch:2}{reserved:2}{build:3}</c> with zero-padding.
    /// Sent as <c>ClientHello.BuildInfo.Version</c> during AP handshake —
    /// keeping this in sync with current desktop reduces the chance of being
    /// routed through legacy / deprecated server paths.
    /// </summary>
    public const ulong HandshakeBuildVersion = 128_800_483UL;

    /// <summary>
    /// Value for the outgoing <c>spotify-app-version</c> HTTP header on
    /// spclient / pathfinder calls. Spotify Desktop sends the same numeric
    /// build (as a string) that goes into the handshake.
    /// </summary>
    public const string AppVersionHeader = "128800483";

    /// <summary>
    /// <c>User-Agent</c> header for HTTP calls. Matches the format live
    /// desktop sends in the latest builds:
    /// <c>Spotify/{buildNumber} {platform}/{osDescriptor}</c>.
    /// The OS descriptor is built lazily via <see cref="GetUserAgent"/> so we
    /// can include the actual host OS version
    /// (e.g. <c>Windows 10 (10.0.26200; x64[native:ARM])</c>); use this string
    /// only as a static fallback.
    /// </summary>
    public const string UserAgent = "Spotify/" + AppVersionHeader + " " + AppPlatform + "/0 (PC desktop)";

    /// <summary>
    /// Builds the rich User-Agent string Spotify Desktop emits today,
    /// embedding the host OS version. Mirrors values seen in live captures:
    /// <c>Spotify/128800483 Win32_x86_64/Windows 10 (10.0.26200; x64[native:ARM])</c>.
    /// </summary>
    public static string GetUserAgent()
        => $"Spotify/{AppVersionHeader} {AppPlatform}/{GetOsDescriptor()}";

    /// <summary>
    /// <c>private_device_info.platform</c> value sent inside the connect-state
    /// PutStateRequest. Same OS-descriptor format Spotify desktop uses.
    /// </summary>
    public static string GetPrivateDevicePlatform() => GetOsDescriptor();

    /// <summary>
    /// OS descriptor Spotify desktop emits inside its User-Agent and
    /// private_device_info.platform fields. Format on Windows:
    /// <c>Windows {major} ({full-version}; {arch-suffix})</c>. Spotify always
    /// presents itself as an x64 binary, so the arch suffix is <c>x64</c> on
    /// x64 hosts and <c>x64[native:ARM]</c> on native-ARM hosts (the binary
    /// runs under emulation but still identifies as x64). Matching this
    /// exactly is load-bearing for server-side App-Platform classification.
    /// </summary>
    private static string GetOsDescriptor()
    {
        var os = Environment.OSVersion;
        if (!OperatingSystem.IsWindows())
            return os.VersionString;

        var hostArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        // Spotify always claims x64 — the arch suffix only changes to mark
        // emulation when the host is native ARM (the only mainstream case
        // where x64 emulation is in play).
        var archSuffix = hostArch == System.Runtime.InteropServices.Architecture.Arm64
            ? "x64[native:ARM]"
            : "x64";
        return $"Windows {os.Version.Major} ({os.Version}; {archSuffix})";
    }

    /// <summary>
    /// Value for <c>ClientResponseEncrypted.VersionString</c> in the auth
    /// packet. Spotify logs this for auth telemetry; align with the semver.
    /// </summary>
    public const string AuthVersionString = "Spotify " + DesktopSemver;

    /// <summary>
    /// Spirc version used in Connect <c>DeviceInfo</c>. Stable protocol constant.
    /// </summary>
    public const string SpircVersion = "3.2.6";

    /// <summary>
    /// Platform identifier sent as the <c>app-platform</c> HTTP header on
    /// spclient / pathfinder calls. Matches the platform component of the
    /// User-Agent (<c>Win32_x86_64</c> on Windows).
    /// </summary>
    public const string AppPlatform = "Win32_x86_64";

    /// <summary>
    /// xpui-snapshot version string used in <c>play_origin.feature_version</c>
    /// for connect-state remote play commands. Captured from a real desktop
    /// client; mimics the web-based player snapshot identifier the Connect
    /// server expects on transfer-play. Refresh alongside <see cref="DesktopSemver"/>.
    /// </summary>
    public const string XpuiSnapshotVersion = "xpui-snapshot_2026-05-06_1778061618835_fb3c63a";
}
