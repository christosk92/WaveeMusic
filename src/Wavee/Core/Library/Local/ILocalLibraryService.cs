namespace Wavee.Core.Library.Local;

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
}
