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
    /// Verified against live HAR capture 2026-04-17 showing
    /// <c>User-Agent: Spotify/128700414 Win32_x86_64/0 (PC desktop)</c>.
    /// </summary>
    public const string DesktopSemver = "1.2.87.414";

    /// <summary>
    /// Git short-sha suffix appended to the semver for Connect <c>DeviceInfo</c>.
    /// Visible to other Connect clients as the device's software version string.
    /// </summary>
    public const string DesktopBuildSha = "g4e7a1155";

    /// <summary>
    /// Full <c>DeviceInfo.DeviceSoftwareVersion</c> string sent on PUT-state.
    /// Matches the value live Spotify Desktop reports in its own DeviceInfo.
    /// </summary>
    public const string DeviceSoftwareVersion = DesktopSemver + "." + DesktopBuildSha;

    /// <summary>
    /// Numeric build version Spotify encodes from <see cref="DesktopSemver"/>.
    /// Encoding (verified from HAR User-Agent <c>Spotify/128700414</c> for
    /// semver 1.2.87.414): digits pack as
    /// <c>{major}{minor}{patch:2}{reserved:2}{build:3}</c> with zero-padding.
    /// Sent as <c>ClientHello.BuildInfo.Version</c> during AP handshake —
    /// keeping this in sync with current desktop reduces the chance of being
    /// routed through legacy / deprecated server paths.
    /// </summary>
    public const ulong HandshakeBuildVersion = 128_700_414UL;

    /// <summary>
    /// Value for the outgoing <c>spotify-app-version</c> HTTP header on
    /// spclient / pathfinder calls. Spotify Desktop sends the same numeric
    /// build (as a string) that goes into the handshake.
    /// </summary>
    public const string AppVersionHeader = "128700414";

    /// <summary>
    /// <c>User-Agent</c> header for HTTP calls, matching the format live desktop
    /// sends: <c>Spotify/{buildNumber} {platform}/0 (PC desktop)</c>.
    /// </summary>
    public const string UserAgent = "Spotify/" + AppVersionHeader + " " + AppPlatform + "/0 (PC desktop)";

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
}
