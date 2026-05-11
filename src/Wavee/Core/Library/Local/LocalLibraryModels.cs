namespace Wavee.Core.Library.Local;

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
    bool IsVideo = false);

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
