namespace Wavee.Local;

public interface ILocalLibraryService
{
    // Folder management
    Task<IReadOnlyList<LocalLibraryFolder>> GetWatchedFoldersAsync(CancellationToken ct = default);
    Task<LocalLibraryFolder> AddWatchedFolderAsync(string path, bool includeSubfolders = true, CancellationToken ct = default);
    Task RemoveWatchedFolderAsync(int folderId, CancellationToken ct = default);
    Task SetWatchedFolderEnabledAsync(int folderId, bool enabled, CancellationToken ct = default);

    // Scan control
    Task TriggerRescanAsync(int? folderId = null, CancellationToken ct = default);
    IObservable<LocalSyncProgress> SyncProgress { get; }
    bool IsScanning { get; }

    // Reads (consumed by ILibraryDataService merging layer + UI)
    Task<IReadOnlyList<LocalTrackRow>> GetAllTracksAsync(CancellationToken ct = default);
    Task<LocalAlbumDetail?> GetAlbumAsync(string albumUri, CancellationToken ct = default);
    Task<LocalArtistDetail?> GetArtistAsync(string artistUri, CancellationToken ct = default);
    Task<LocalTrackRow?> GetTrackAsync(string trackUri, CancellationToken ct = default);
    Task<string?> GetFilePathForTrackAsync(string trackUri, CancellationToken ct = default);
    /// <summary>
    /// Searches the cached metadata DB. The <paramref name="scope"/> selects between:
    ///  - <see cref="LocalSearchScope.LocalFilesOnly"/> (default): only local filesystem
    ///    entities (tracks/albums/artists). Preserves pre-existing call sites.
    ///  - <see cref="LocalSearchScope.AllCached"/>: everything cached — local files PLUS
    ///    any cached Spotify entities (tracks, albums, artists, playlists), regardless
    ///    of whether the user has saved them. Used by the omnibar quicksearch.
    /// </summary>
    Task<IReadOnlyList<LocalSearchResult>> SearchAsync(
        string query,
        int limit = 20,
        LocalSearchScope scope = LocalSearchScope.LocalFilesOnly,
        CancellationToken ct = default);

    // Watcher event hookup — the hosted service wires FileSystemWatcher events into here.
    Task NotifyFileChangedAsync(string path, CancellationToken ct = default);
    Task NotifyFileDeletedAsync(string path, CancellationToken ct = default);

    // v17 write API used by UI (settings dropdown + flyout edit).
    Task SetWatchedFolderExpectedKindAsync(int folderId, Wavee.Local.Classification.LocalContentKind? expected, CancellationToken ct = default);

    // v17 read used by the enrichment service + detail flyout.
    Task<Wavee.Local.Enrichment.EnrichmentRow?> GetEnrichmentRowAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Display-time metadata for the now-playing surface. Joins
    /// <c>local_files</c> + <c>entities</c> + <c>local_series</c> so the
    /// orchestrator can format a TMDB-enriched display title (S01E01 · "Pilot")
    /// and the PlayerBar can route title-click to the show / movie detail page.
    /// Returns null when the URI isn't in the local index. The
    /// <c>metadata_overrides</c> overlay is already applied to the returned
    /// fields — callers don't need to re-apply it.
    /// </summary>
    Task<LocalPlaybackMetadata?> GetPlaybackMetadataAsync(string trackUri, CancellationToken ct = default);

    /// <summary>Lists all locally indexed music videos.</summary>
    Task<IReadOnlyList<Models.LocalMusicVideo>> GetMusicVideosAsync(CancellationToken ct = default);

    /// <summary>Reads one locally indexed music video by its wavee local track URI.</summary>
    Task<Models.LocalMusicVideo?> GetMusicVideoAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Resolves the local music video linked to a Spotify audio track, if one
    /// has been matched by enrichment or manually linked by the user.
    /// </summary>
    Task<Models.LocalMusicVideo?> GetLinkedMusicVideoForSpotifyTrackAsync(string spotifyTrackUri, CancellationToken ct = default);

    /// <summary>
    /// Bulk form used by video-availability surfaces. Returns
    /// spotify:track:* URI -> wavee:local:track:* music-video URI.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLinkedMusicVideoUrisForSpotifyTracksAsync(
        IEnumerable<string> spotifyTrackUris,
        CancellationToken ct = default);

    /// <summary>Manually associates one local music video with a Spotify track.</summary>
    Task LinkMusicVideoToSpotifyTrackAsync(string localMusicVideoTrackUri, string spotifyTrackUri, CancellationToken ct = default);

    /// <summary>Clears a local music video's Spotify track association.</summary>
    Task UnlinkMusicVideoFromSpotifyTrackAsync(string localMusicVideoTrackUri, CancellationToken ct = default);

    /// <summary>
    /// External subtitles + embedded subtitle tracks for one video, merged in
    /// a single list. External rows carry their <c>Path</c> on disk; embedded
    /// rows have empty path + <see cref="Models.LocalSubtitle.Embedded"/> true.
    /// </summary>
    Task<IReadOnlyList<Models.LocalSubtitle>> GetSubtitlesForAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Persist a user-dropped external subtitle so future plays auto-load it.
    /// Idempotent — duplicate (video, subtitle) pairs are de-duped server-side.
    /// Returns the row id (existing or newly inserted) for callers that want
    /// to reference it. Does not validate that the subtitle file actually
    /// exists; the player resolves at attach time.
    /// </summary>
    Task<long> AddExternalSubtitleAsync(
        string videoFilePath,
        string subtitlePath,
        string? language,
        bool forced,
        bool sdh,
        CancellationToken ct = default);
}
