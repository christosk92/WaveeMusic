using Wavee.Local.Classification;

namespace Wavee.Local.Models;

/// <summary>One TV show grouping a set of episodes from disk.</summary>
public sealed record LocalShow(
    string Id,
    string Name,
    int? TmdbId,
    string? Overview,
    string? PosterArtworkUri,
    string? BackdropArtworkUri,
    int SeasonCount,
    int EpisodeCount,
    int UnwatchedCount,
    long? LastWatchedAt)
{
    /// <summary>
    /// Total seasons TMDB reports for this show (v19). Null until the
    /// enrichment pipeline calls <c>GetTvDetailsAsync</c>. When set,
    /// <c>SeasonCount</c> reads as "S of <see cref="TotalSeasonsExpected"/>".
    /// </summary>
    public int? TotalSeasonsExpected { get; init; }

    /// <summary>
    /// Total episodes TMDB reports for this show across every season.
    /// Powers the "X of Y episodes" hero meta.
    /// </summary>
    public int? TotalEpisodesExpected { get; init; }

    // ── v21: show-level rich details ─────────────────────────────────────
    /// <summary>TMDB tagline; italic subtitle under the title on detail page.</summary>
    public string? Tagline { get; init; }
    /// <summary>"Returning Series" / "Ended" / "In Production" / etc.</summary>
    public string? Status { get; init; }
    /// <summary>ISO date string (e.g. "2011-09-22"). Display in hero meta.</summary>
    public string? FirstAirDate { get; init; }
    /// <summary>ISO date string of the most-recent aired episode TMDB knows.</summary>
    public string? LastAirDate { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    /// <summary>TMDB vote_average (0..10). Renders as "★ {n:F1}".</summary>
    public double? VoteAverage { get; init; }
    public IReadOnlyList<string>? Networks { get; init; }
    /// <summary>Top-N principal cast — loaded by the VM from <c>GetShowCastAsync</c>.</summary>
    public IReadOnlyList<LocalCastMember>? Cast { get; init; }
}

/// <summary>One season of a local show. Backed by query, not a row.</summary>
public sealed record LocalSeason(
    string ShowId,
    int SeasonNumber,
    int EpisodeCount,
    IReadOnlyList<LocalEpisode> Episodes);

/// <summary>
/// One episode of a local show. After Continuation 8 the row's
/// <c>TrackUri</c> / <c>FilePath</c> are nullable — the Show Detail page
/// renders the full TMDB roster (cached in <c>local_series_episodes</c>),
/// and rows whose corresponding file isn't on disk surface with those
/// fields set to null + <see cref="IsOnDisk"/> false.
/// </summary>
public sealed record LocalEpisode(
    string? TrackUri,
    string? FilePath,
    string ShowId,
    int Season,
    int Episode,
    string? Title,
    long DurationMs,
    long LastPositionMs,
    long? WatchedAt,
    string? StillImageUri,
    int SubtitleCount,
    int AudioTrackCount,
    int? TmdbId)
{
    /// <summary>
    /// Episode synopsis from TMDB. Now read from the cached roster
    /// (<c>local_series_episodes.overview</c>), so it's populated for
    /// every episode the show has been enriched for — including
    /// missing-from-disk episodes.
    /// </summary>
    public string? Overview { get; init; }

    /// <summary>
    /// True when the matching file is in <c>local_files</c>. False for
    /// roster entries whose corresponding file isn't currently in any
    /// watched folder — the UI renders these at reduced opacity with a
    /// "Not in library" badge and disables the play affordance.
    /// </summary>
    public bool IsOnDisk { get; init; }
}

/// <summary>A movie indexed locally.</summary>
public sealed record LocalMovie(
    string TrackUri,
    string FilePath,
    string Title,
    int? Year,
    long DurationMs,
    long LastPositionMs,
    long? WatchedAt,
    int WatchCount,
    string? PosterUri,
    string? BackdropUri,
    string? Overview,
    int? TmdbId,
    int SubtitleCount,
    int AudioTrackCount)
{
    /// <summary>TMDB tagline — short pitch / poster line. Italic subtitle on the detail page.</summary>
    public string? Tagline { get; init; }

    /// <summary>Runtime in minutes from TMDB. Used in the hero meta strip alongside year + genres.</summary>
    public int? RuntimeMinutes { get; init; }

    /// <summary>Genre names from TMDB (already deduped + ordered as TMDB returns them).</summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>TMDB vote_average (0..10). Rendered as "★ {n:F1}".</summary>
    public double? VoteAverage { get; init; }

    /// <summary>Top-N principal cast loaded separately by the VM via <c>GetMovieCastAsync</c> — not part of the main row read.</summary>
    public IReadOnlyList<LocalCastMember>? Cast { get; init; }
}

/// <summary>One row from a movie's principal-cast list.</summary>
public sealed record LocalCastMember(
    int? PersonId,
    string Name,
    string? Character,
    string? ProfileImageUri,
    int Order);

/// <summary>
/// TMDB person biography + profile image, materialised on demand when the
/// user opens a cast member's detail page. Image URL is a direct TMDB CDN
/// HTTPS URL (not <c>wavee-artwork://</c>) — we don't cache person profiles
/// in <c>LocalArtworkCache</c>, since person pages are a navigation
/// destination, not a row in a recurring shelf.
/// </summary>
public sealed record LocalPersonInfo(
    int Id,
    string Name,
    string? Biography,
    string? ProfileImageUrl,
    string? KnownForDepartment,
    string? Birthday,
    string? Deathday,
    string? PlaceOfBirth);

/// <summary>A local music video.</summary>
public sealed record LocalMusicVideo(
    string TrackUri,
    string FilePath,
    string Title,
    string? Artist,
    int? Year,
    long DurationMs,
    string? ThumbnailUri)
{
    /// <summary>
    /// Spotify audio track this local music video is associated with. Populated
    /// by Spotify enrichment or by a manual user link.
    /// </summary>
    public string? LinkedSpotifyTrackUri { get; init; }
}

/// <summary>
/// "Other" / unclassified item — files that didn't fit any known kind.
/// </summary>
public sealed record LocalOtherItem(
    string TrackUri,
    string FilePath,
    string DisplayName,
    long DurationMs,
    long FileSize,
    string Extension,
    LocalContentKind AutoKind,
    LocalContentKind? KindOverride);

/// <summary>A user-defined collection (or auto-generated show/album group).</summary>
public sealed record LocalCollection(
    string Id,
    string Name,
    string Kind,
    string? PosterArtworkUri,
    long CreatedAt,
    bool UserCreated,
    int ItemCount);

/// <summary>An "in progress" media item with resume info.</summary>
public sealed record LocalContinueItem(
    string TrackUri,
    string FilePath,
    string DisplayName,
    long DurationMs,
    long LastPositionMs,
    long PlayedAt,
    string? ArtworkUri,
    LocalContentKind Kind);

/// <summary>A subtitle entry for a video.</summary>
public sealed record LocalSubtitle(
    long Id,
    string Path,
    string? Language,
    bool Forced,
    bool Sdh,
    bool Embedded);

/// <summary>An audio / video / subtitle stream embedded in a container.</summary>
public sealed record LocalEmbeddedTrack(
    long Id,
    string Kind,
    int StreamIndex,
    string? Language,
    string? Label,
    string? Codec,
    bool IsDefault);

/// <summary>Patch applied to a file's metadata overrides JSON.</summary>
public sealed record MetadataPatch(
    string? Title = null,
    string? Artist = null,
    string? AlbumArtist = null,
    string? Album = null,
    int? Year = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    string? Genre = null,
    string? ShowName = null,
    int? Season = null,
    int? Episode = null,
    string? EpisodeTitle = null,
    string? Director = null,
    string? ArtworkHash = null);

/// <summary>Progress reported by the enrichment background pipeline.</summary>
public sealed record EnrichmentProgress(
    int Pending,
    int Matched,
    int NoMatch,
    int Failed,
    string? CurrentlyProcessing);

/// <summary>Lyrics record (cached LrcLib result or sibling-file .lrc parse).</summary>
public sealed record LocalLyrics(
    string FilePath,
    string Source,        // 'sibling-file' | 'lrclib' | 'manual'
    string Format,        // 'plain' | 'lrc' | 'enhanced-lrc'
    string Body,
    string? Language,
    long FetchedAt);

/// <summary>A row in local_plays — one playback event.</summary>
public sealed record LocalPlay(
    long Id,
    string TrackUri,
    long PlayedAt,
    long PositionMs,
    long DurationMs);
