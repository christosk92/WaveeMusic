namespace Wavee.Local;

public sealed record LocalLibraryFolder(
    int Id,
    string Path,
    bool Enabled,
    bool IncludeSubfolders,
    long? LastScanAt,
    int FileCount,
    string? LastScanStatus,
    string? LastScanError,
    long? LastScanDurationMs);

public sealed record LocalSyncProgress(
    int FolderId,
    int TotalFiles,
    int ProcessedFiles,
    string? CurrentPath);

/// <summary>Lightweight projection of one indexed local track for list rendering.</summary>
public sealed record LocalTrackRow(
    string TrackUri,
    string FilePath,
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? AlbumUri,
    string? ArtistUri,
    long DurationMs,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? ArtworkUri,
    bool IsVideo = false,
    // v17 — resume position; orchestrator passes this as startPositionMs
    // when launching playback so videos / long audio pick up where the user
    // left off. Default 0 keeps callers that don't care unaffected.
    long LastPositionMs = 0)
{
    /// <summary>"m:ss" for short tracks, "h:mm:ss" past the hour mark.
    /// Bound directly from XAML so rows don't have to wire a converter.</summary>
    public string DurationDisplay
    {
        get
        {
            var ts = System.TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }

    // ── v18: Spotify enrichment link ────────────────────────────────────
    // Named init properties (not positional) so existing constructors and
    // call sites that don't yet populate these fields keep compiling.
    /// <summary>Spotify URI of the matched track, or null when not synced yet.</summary>
    public string? SpotifyTrackUri  { get; init; }
    /// <summary>Spotify URI of the matched track's album. Clicking the album
    /// label on a local row opens AlbumPage with this URI — the existing
    /// URI-prefix branch in AlbumService routes it to the Spotify path.</summary>
    public string? SpotifyAlbumUri  { get; init; }
    /// <summary>Spotify URI of the matched track's first artist.</summary>
    public string? SpotifyArtistUri { get; init; }
    /// <summary>HTTPS URL to the Spotify CDN-hosted album cover (i.scdn.co)
    /// — pre-resolved at enrichment time so UI bindings don't repeatedly
    /// hit <see cref="Wavee.UI.WinUI.Helpers.SpotifyImageHelper.ToHttpsUrl"/>.</summary>
    public string? SpotifyCoverUrl  { get; init; }

    /// <summary>
    /// The cover URI the UI should display: Spotify's high-res CDN URL
    /// when present, falling back to the local artwork cache. All local-music
    /// image bindings switch to this so enrichment "just works" without
    /// per-callsite branching.
    /// </summary>
    public string? EffectiveCoverUri => SpotifyCoverUrl ?? ArtworkUri;
}

public sealed record LocalAlbumDetail(
    string AlbumUri,
    string Album,
    string? AlbumArtist,
    string? ArtistUri,
    int? Year,
    string? ArtworkUri,
    IReadOnlyList<LocalTrackRow> Tracks);

public sealed record LocalArtistDetail(
    string ArtistUri,
    string Name,
    string? ArtworkUri,
    IReadOnlyList<LocalAlbumSummary> Albums,
    IReadOnlyList<LocalTrackRow> AllTracks);

public sealed record LocalAlbumSummary(
    string AlbumUri,
    string Album,
    int? Year,
    int TrackCount,
    string? ArtworkUri);

public enum LocalSearchEntityType { Track, Album, Artist, Playlist }

/// <summary>
/// Controls which cached entities <see cref="ILocalLibraryService.SearchAsync"/> returns.
/// </summary>
public enum LocalSearchScope
{
    /// <summary>
    /// Default — local filesystem entities only (entities.source_type = Local).
    /// Used by the dedicated Search page's "On this PC" merge so cached-but-not-saved
    /// Spotify items don't duplicate the network search results.
    /// </summary>
    LocalFilesOnly,

    /// <summary>
    /// Everything in the metadata cache regardless of source — local files PLUS any
    /// cached Spotify entities (tracks/albums/artists/playlists). Used by the omnibar
    /// quicksearch so "anything I've seen" is findable without hitting the network.
    /// </summary>
    AllCached,
}

public sealed record LocalSearchResult(
    LocalSearchEntityType Type,
    string Uri,
    string Name,
    string? Subtitle,
    string? ArtworkUri);

/// <summary>
/// Display-time metadata for one local item used by the now-playing surface
/// (player bar, expanded player, theatre / fullscreen). Joins
/// <c>local_files</c> + <c>entities</c> + <c>local_series</c> so the orchestrator
/// can format a TMDB-enriched title without forking per-kind queries — and so
/// the PlayerBar can route the title-click to the show or movie detail page
/// instead of the parent-folder "album" page.
/// </summary>
public sealed record LocalPlaybackMetadata(
    string TrackUri,
    Wavee.Local.Classification.LocalContentKind Kind,
    string? RawTitle,
    string? RawArtist,
    string? RawAlbum,
    string? ArtworkUri,
    // TV episode fields — populated when Kind == TvEpisode.
    string? SeriesId,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    // Movie field — populated when Kind == Movie.
    int? MovieYear,
    // TMDB linkage — episode / movie ids respectively (null when not yet matched).
    int? TmdbId)
{
    /// <summary>
    /// Display title for the player chrome. TV episode → "S01E01 · Pilot".
    /// Movie → the movie title. Music / other → the raw track title (with
    /// filename fallback handled by the caller).
    /// </summary>
    public string FormatDisplayTitle(string? filenameFallback = null)
    {
        if (Kind == Wavee.Local.Classification.LocalContentKind.TvEpisode
            && SeasonNumber is { } s
            && EpisodeNumber is { } e)
        {
            var name = !string.IsNullOrWhiteSpace(EpisodeTitle) ? EpisodeTitle
                     : !string.IsNullOrWhiteSpace(RawTitle) ? RawTitle
                     : filenameFallback;
            return string.IsNullOrWhiteSpace(name)
                ? $"S{s:00}E{e:00}"
                : $"S{s:00}E{e:00} · {name}";
        }

        return RawTitle
               ?? filenameFallback
               ?? "Unknown";
    }

    /// <summary>
    /// Display "artist" for the player chrome. TV episode → series name. Movie →
    /// release year as string. Music → raw artist (with album-artist fallback
    /// handled by the caller).
    /// </summary>
    public string? FormatDisplayArtist(string? rawArtistFallback = null)
    {
        return Kind switch
        {
            Wavee.Local.Classification.LocalContentKind.TvEpisode =>
                SeriesName ?? rawArtistFallback ?? RawArtist,
            Wavee.Local.Classification.LocalContentKind.Movie =>
                MovieYear is { } y && y > 0
                    ? y.ToString()
                    : rawArtistFallback ?? RawArtist,
            _ => RawArtist ?? rawArtistFallback,
        };
    }
}
