using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee.SpotifyLive;

/// <summary>
/// A resolved video source for the pop-out / inline video surface: EITHER a clear/Canvas URL (played on the clear MF
/// backend) OR a PlayReady DRM descriptor + license relay (played via the native in-process CDM). The Spotify
/// video-resolution layer (Canvas from the feed; PlayReady from <see cref="SpotifyVideoManifest"/> once the probe
/// confirms it) produces this and publishes it on <c>PlaybackBridge.PopOutVideoSource</c>. <see cref="Key"/> is a stable
/// identity (manifest id / URL) so the player remounts cleanly when the source changes.
/// </summary>
public sealed record PopOutVideoSource
{
    /// <summary>Clear/Canvas URL (a plain .mp4 / unencrypted stream). Null for a DRM source.</summary>
    public string? ClearUrl { get; init; }

    /// <summary>A LOCAL file path (the user's attached .mp4). Non-null only for an override source; it takes precedence
    /// over <see cref="ClearUrl"/>/<see cref="DrmDescriptor"/> in the host, which opens it with <c>MediaSource.FromFile</c>
    /// on the clear MF backend — so the file's OWN audio track plays, which is the whole point of an override.</summary>
    public string? FilePath { get; init; }

    /// <summary>Parsed PlayReady descriptor (init/segment addressing + PSSH). Null for a clear source.</summary>
    public DashSourceDescriptor? DrmDescriptor { get; init; }
    /// <summary>The <c>WithDrm</c> license relay (POSTs the CDM challenge to Spotify). Required with a DRM descriptor.</summary>
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay { get; init; }
    /// <summary>Advisory license-server URI carried on the <see cref="DrmConfig"/> (the relay owns the actual POST).</summary>
    public string? LicenseServerUri { get; init; }

    /// <summary>Stable identity for player remount (manifest id or clear URL).</summary>
    public string Key { get; init; } = "";

    /// <summary>The video's natural pixel width from the manifest's best video profile (0 = unknown). Lets the
    /// docked/PiP surfaces size the card correctly AT MOUNT instead of re-laying-out when the decoder reports
    /// <c>NaturalSize</c> seconds later.</summary>
    public int NaturalWidth { get; init; }
    /// <summary>The video's natural pixel height from the manifest (0 = unknown). See <see cref="NaturalWidth"/>.</summary>
    public int NaturalHeight { get; init; }

    /// <summary>Is this source a LIVE broadcast — a YouTube/Twitch channel, not a finite video?
    /// <para>Carried on the SOURCE rather than inferred by the host, because by the time Media Foundation is open it is
    /// too late to tell: MF reports a sliding DVR window as a perfectly ordinary finite <c>GetDuration</c>, which is
    /// exactly how a six-hour broadcast came to render as <c>0:03 / -3:22</c>. The module that resolved the locator is
    /// the one party that KNOWS, so its answer rides along and the host opens the source with
    /// <c>SourceLiveness.Live</c> — after which the engine never publishes a finite duration for it at all.</para>
    /// <para>False for every clear/DRM Spotify video and every local attachment, which is correct: those are finite
    /// edits with a real end.</para></summary>
    public bool IsLive { get; init; }

    public bool IsDrm => DrmDescriptor is not null && LicenseRelay is not null;

    public static PopOutVideoSource Clear(string url) => new() { ClearUrl = url, Key = url };

    /// <summary>A user-attached local video. <see cref="Key"/> is the <c>local:video:&lt;id&gt;</c> namespace — the stable
    /// per-file remount identity every surface/host already keys on — so re-entering the video path for the same
    /// attachment is the host's existing same-Key no-op, while swapping the attached file is a clean player swap.</summary>
    public static PopOutVideoSource LocalFile(Wavee.Backend.VideoOverride o)
        => new() { FilePath = o.Path, Key = o.SourceKey };
    public static PopOutVideoSource PlayReady(string manifestId, DashSourceDescriptor descriptor,
        Func<LicenseRequest, ValueTask<LicenseResponse>> relay, string? licenseServerUri)
        => new() { DrmDescriptor = descriptor, LicenseRelay = relay, LicenseServerUri = licenseServerUri, Key = manifestId };
}

/// <summary>
/// ONE video load request: the resolved <see cref="PopOutVideoSource"/> plus the position the session must START at.
///
/// <para>The start position travels WITH the request rather than being latched on the host, and that is load-bearing.
/// A position carried across an audio→video switch is issued at the moment of the swap, when the video player does not
/// exist yet — <c>LoadVideo</c> hands off to the serialized <see cref="VideoLoadPump{TSource}"/>, and on the Rebuild
/// path the host still tears the predecessor down to completion before building the successor. A bare <c>Seek</c> at
/// that instant lands on a null player and is dropped (that is why every audio→video switch used to restart at 0), and
/// a host-side latch cannot fix it safely: a latch cleared on teardown dies before use, while one that survives
/// teardown leaks onto the next track's load — and the in-place Switch path never tears down at all, so there is no
/// teardown edge to hang a latch on. Carrying it on the request makes it scoped to exactly one load by construction,
/// and the pump's latest-wins coalescing scopes it for free.</para>
/// </summary>
/// <param name="Source">The resolved video source to open.</param>
/// <param name="StartAtMs">Where the session must begin, in ms. <c>&lt;= 0</c> means "from the start" — the ordinary
/// case for a fresh track. Clamped against the video's real duration once the media engine reports it, because a
/// carried audio position can exceed a shorter video edit.</param>
public sealed record VideoLoadRequest(PopOutVideoSource Source, long StartAtMs);
