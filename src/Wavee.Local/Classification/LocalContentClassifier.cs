using Microsoft.Extensions.Logging;

namespace Wavee.Local.Classification;

/// <summary>
/// Classifies a scanned local file into a <see cref="LocalContentKind"/> using
/// extension, ATL-extracted tag signals, filename patterns, and (soft) per-folder
/// kind hints. Pure-function module: every method is static and side-effect-free.
///
/// <para>Signal weighting (highest first):</para>
/// <list type="number">
///   <item>SxxExx episode marker in filename → <see cref="LocalContentKind.TvEpisode"/></item>
///   <item>Year-in-parens + release-group markers + video extension + duration ≥ 40 min
///         → <see cref="LocalContentKind.Movie"/></item>
///   <item>Music-video signal words (MV, Official Video, slowed+reverb) + video extension
///         → <see cref="LocalContentKind.MusicVideo"/></item>
///   <item>Audio extension + ATL Artist/Album tags → <see cref="LocalContentKind.Music"/></item>
///   <item>Audio extension (any) → <see cref="LocalContentKind.Music"/></item>
///   <item>Video extension that didn't match anything specific →
///         <see cref="LocalContentKind.MusicVideo"/> if short, otherwise
///         <see cref="LocalContentKind.Movie"/></item>
///   <item>Everything else → <see cref="LocalContentKind.Other"/></item>
/// </list>
///
/// <para>Per-folder hint <paramref name="expectedKind"/> is a soft signal — it
/// only acts as a tiebreaker when other signals are mixed. A movie file misfiled
/// in a "MusicVideos" folder still gets classified as Movie because year-in-parens
/// + HEVC markers + &gt;40 min runtime dominate the hint.</para>
/// </summary>
public static class LocalContentClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".wmv", ".mpg", ".mpeg", ".flv",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif",
    };

    private const long MoviesMinDurationMs = 40L * 60 * 1000;        // 40 minutes
    private const long MusicVideoMaxDurationMs = 12L * 60 * 1000;    // 12 minutes

    /// <summary>
    /// Classifies the given file. Pure function of its inputs — no IO.
    /// </summary>
    /// <param name="filePath">Absolute path (extension is inspected).</param>
    /// <param name="hasArtistTag">True if ATL returned a non-blank Artist or AlbumArtist.</param>
    /// <param name="hasAlbumTag">True if ATL returned a non-blank Album.</param>
    /// <param name="durationMs">Track duration in milliseconds, or 0 if unknown.</param>
    /// <param name="expectedKind">Optional per-folder soft hint (Auto = null).</param>
    public static LocalContentKind Classify(
        string filePath,
        bool hasArtistTag,
        bool hasAlbumTag,
        long durationMs,
        LocalContentKind? expectedKind = null,
        ILogger? logger = null)
    {
        var filename = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath);
        var isVideo = VideoExtensions.Contains(ext);
        var isAudio = AudioExtensions.Contains(ext);
        var hasEpisode = LocalFilenameParser.TryParseEpisode(filename, out _, out _, out _, out _);
        var hasMarkers = LocalFilenameParser.HasReleaseGroupMarkers(filename);
        var hasMvSignals = LocalFilenameParser.HasMusicVideoSignals(filename);
        var hasYear = LocalFilenameParser.TryParseMovie(filename, out _, out var movieYear) && movieYear is not null;

        LocalContentKind result;
        string reason;

        // 1) Episode marker is the strongest signal.
        if (hasEpisode) { result = LocalContentKind.TvEpisode; reason = "episode-marker"; }
        // 2) Video classification cascade.
        else if (isVideo)
        {
            if (hasMvSignals && durationMs > 0 && durationMs <= MusicVideoMaxDurationMs)
                { result = LocalContentKind.MusicVideo; reason = "mv-signals+short-duration"; }
            else if (hasYear && (hasMarkers || durationMs >= MoviesMinDurationMs))
                { result = LocalContentKind.Movie; reason = "year+markers-or-long-duration"; }
            else if (hasMarkers && durationMs >= MoviesMinDurationMs)
                { result = LocalContentKind.Movie; reason = "release-group+long-duration"; }
            else if (hasMvSignals)
                { result = LocalContentKind.MusicVideo; reason = "mv-signals"; }
            else if (durationMs >= MoviesMinDurationMs)
                { result = LocalContentKind.Movie; reason = "long-duration-fallback"; }
            else if (durationMs > 0 && durationMs <= MusicVideoMaxDurationMs)
                { result = LocalContentKind.MusicVideo; reason = "short-duration-fallback"; }
            else
            {
                result = expectedKind switch
                {
                    LocalContentKind.Movie      => LocalContentKind.Movie,
                    LocalContentKind.TvEpisode  => LocalContentKind.TvEpisode,
                    LocalContentKind.MusicVideo => LocalContentKind.MusicVideo,
                    _                           => LocalContentKind.MusicVideo,
                };
                reason = expectedKind is null ? "video-no-signals→MusicVideo-default" : $"video-no-signals→folder-hint:{expectedKind}";
            }
        }
        // 3) Audio extension is reliably music.
        else if (isAudio) { result = LocalContentKind.Music; reason = "audio-extension"; }
        // 4) Unknown extension — fall back to per-folder hint, else Other.
        else { result = expectedKind ?? LocalContentKind.Other; reason = expectedKind is null ? "unknown-ext→Other" : $"unknown-ext→folder-hint:{expectedKind}"; }

        logger?.LogDebug(
            "[classify] {File} → {Kind} (reason={Reason}, ext={Ext}, video={IsVideo}, audio={IsAudio}, "
          + "ep={HasEpisode}, markers={HasMarkers}, mvSignals={HasMvSignals}, year={HasYear}, "
          + "dur={DurationMs}ms, artistTag={HasArtist}, albumTag={HasAlbum}, hint={Hint})",
            filename, result, reason, ext, isVideo, isAudio,
            hasEpisode, hasMarkers, hasMvSignals, hasYear,
            durationMs, hasArtistTag, hasAlbumTag, expectedKind?.ToString() ?? "Auto");

        return result;
    }
}
