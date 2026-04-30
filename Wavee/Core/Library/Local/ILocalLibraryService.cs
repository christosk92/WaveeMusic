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
    Task<IReadOnlyList<LocalSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default);

    // Watcher event hookup — the hosted service wires FileSystemWatcher events into here.
    Task NotifyFileChangedAsync(string path, CancellationToken ct = default);
    Task NotifyFileDeletedAsync(string path, CancellationToken ct = default);
}
