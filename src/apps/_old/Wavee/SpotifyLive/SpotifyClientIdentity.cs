using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive;

// ── Desktop-parity / anti-fraud identity (ported VERBATIM — decision #11) ─────────────────────────────────────────────
// The strings Wavee presents to Spotify (DeviceInfo software version, spirc version, private_device_info.platform, the
// app-version/user-agent headers). Version pins delegate to SpotifyRuntimeIdentity; Connect-only fields stay here.
public static class SpotifyClientIdentity
{
    public static string DesktopSemver => SpotifyRuntimeIdentity.Default.DesktopSemver;
    public static string DesktopBuildSha => SpotifyRuntimeIdentity.DefaultClientVersion[(SpotifyRuntimeIdentity.DefaultClientVersion.LastIndexOf('g') + 1)..];
    public static string DeviceSoftwareVersion => SpotifyRuntimeIdentity.DefaultClientVersion;
    public static string AppVersionHeader => SpotifyRuntimeIdentity.DefaultAppVersion;
    public static string AppPlatform => SpotifyRuntimeIdentity.AppPlatform;
    public const string SpircVersion = "3.2.6";                                          // DeviceInfo.spirc_version
    public const string XpuiSnapshotVersion = "xpui-snapshot_2026-07-01_1782890476915_7b5cc0c";  // play_origin.feature_version

    /// <summary>private_device_info.platform — the OS descriptor desktop sends.</summary>
    public static string GetPrivateDevicePlatform() => GetOsDescriptor();

    /// <summary>User-Agent for spclient/pathfinder calls: Spotify/{build} {platform}/{os}.</summary>
    public static string GetUserAgent() => SpotifyRuntimeIdentity.Default.UserAgent;

    // Windows: "Windows {major} ({full-version}; {arch})". Spotify always claims x64 (x64[native:ARM] under ARM emulation).
    static string GetOsDescriptor()
    {
        var os = Environment.OSVersion;
        if (!OperatingSystem.IsWindows()) return os.VersionString;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var suffix = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "x64[native:ARM]" : "x64";
        return $"Windows {os.Version.Major} ({os.Version}; {suffix})";
    }
}
