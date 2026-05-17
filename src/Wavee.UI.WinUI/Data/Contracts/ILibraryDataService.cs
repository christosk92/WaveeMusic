using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

public sealed class PodcastEpisodeProgressChangedEventArgs(PodcastEpisodeProgressDto progress, string? aliasUri = null)
    : EventArgs
{
    public PodcastEpisodeProgressDto Progress { get; } = progress;
    public string EpisodeUri => Progress.Uri;
    public string? AliasUri { get; } = aliasUri;

    public bool Matches(string? episodeUri)
        => !string.IsNullOrWhiteSpace(episodeUri) &&
           (string.Equals(episodeUri, EpisodeUri, StringComparison.Ordinal) ||
            string.Equals(episodeUri, AliasUri, StringComparison.Ordinal));
}

/// <summary>
/// Service for retrieving user library data.
/// </summary>
public interface ILibraryDataService
{
    /// <summary>
    /// Gets library statistics for sidebar badges.
    /// </summary>
    Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all library items. Sorting/filtering is done client-side.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets recently played items.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Gets user's playlists.
    /// </summary>
    Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Cache-only variant of <see cref="GetUserPlaylistsAsync"/>. Returns <c>null</c>
    /// when the playlist cache has nothing for this user (cold launch / signed-out
    /// / never synced). Returns an empty list when the cache is present but the
    /// user genuinely has zero playlists. Used by the sidebar to render playlists
    /// instantly on launch while the real fetch + diff runs in the background.
    /// </summary>
    Task<IReadOnlyList<PlaylistSummaryDto>?> TryGetUserPlaylistsFromCacheAsync(CancellationToken ct = default);

    // Pin / Unpin / GetPinnedItems / IsPinned moved to IPinService (Phase 2 carve-out).

    /// <summary>
    /// Gets all albums in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryAlbumDto>> GetAlbumsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all artists in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistDto>> GetArtistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets top tracks for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistTopTrackDto>> GetArtistTopTracksAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets albums for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistAlbumDto>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets all liked/saved songs in the user's library.
    /// </summary>
    Task<IReadOnlyList<LikedSongDto>> GetLikedSongsAsync(CancellationToken ct = default);

    // All podcast / episode methods moved to IPodcastEpisodeService (Phase 2).

    /// <summary>
    /// Gets Spotify-provided content filters for the liked songs page.
    /// </summary>
    Task<IReadOnlyList<LikedSongsFilterDto>> GetLikedSongFiltersAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new playlist and returns the created playlist summary.
    /// </summary>
    // CreatePlaylistAsync moved to IPlaylistMutationService (Phase 2).
    // CreateFolderAsync moved to IRootlistService (Phase 2).

    /// <summary>
    /// Gets detailed information about a specific playlist.
    /// </summary>
    Task<PlaylistDetailDto> GetPlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Gets tracks for a specific playlist.
    /// </summary>
    Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the playlist's follower count via the popcount endpoint.
    /// Held out of <see cref="GetPlaylistAsync"/> so the detail load isn't blocked
    /// on a stat-only round trip — the VM fires this in parallel and shimmers
    /// the count chip until it arrives. Returns 0 if the count is hidden / unavailable.
    /// </summary>
    Task<long> GetPlaylistFollowerCountAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the playlist's pre-extracted color palette (Spotify-side image
    /// processing) via the <c>fetchPlaylist</c> Pathfinder query. Reuses the
    /// album-page <see cref="AlbumPalette"/> type — the GraphQL shape is
    /// identical for both surfaces. Returns null when the palette isn't
    /// available (e.g. mosaic-cover playlists with no upstream extraction).
    /// </summary>
    Task<AlbumPalette?> GetPlaylistPaletteAsync(string playlistId, CancellationToken ct = default);

    // All playlist mutation methods (SetPlaylistFollowed / Add / Remove / Reorder
    // tracks / Rename / Update / RemoveCover / Delete / overlay ops /
    // GetPlaylistRecommendations) moved to IPlaylistMutationService (Phase 2).
    // Playlist permission / collaborator methods moved to IPlaylistPermissionService.
    // Rootlist Move* + CreateFolder moved to IRootlistService.

    // PodcastEpisodeProgressChanged event moved to IPodcastEpisodeService (Phase 2).

    /// <summary>
    /// Requests a full library sync when local data appears to be missing.
    /// No-ops if a sync is already in progress. Subscribe to
    /// <c>IChangeBus.Changes</c> filtered on <c>ChangeScope.Library</c> for
    /// completion notification.
    /// </summary>
    void RequestSyncIfEmpty();
}

/// <summary>
/// Single row in the "Recommended Songs" footer section of PlaylistPage —
/// projection of one <c>playlistextender</c> response entry. Implements
/// <see cref="ITrackItem"/> so the canonical <c>Controls.Track.TrackItem</c>
/// row control can render it directly and the global now-playing indicator
/// (driven by <c>TrackStateBehavior</c>) lights up the matching row when the
/// user plays one of these tracks. Wire fields (<see cref="Name"/>,
/// <see cref="ArtistNames"/>) and ITrackItem fields (<see cref="ITrackItem.Title"/>,
/// <see cref="ITrackItem.ArtistName"/>) co-exist — the interface members are
/// explicit projections of the wire-side fields.
/// </summary>
public sealed class RecommendedTrackResult : ITrackItem
{
    // Not `required` — XAML's compile-time TypeInfo generator emits a
    // parameterless `new RecommendedTrackResult()` for x:DataType templates
    // and CS9035s on any required member.
    public string Uri { get; init; } = string.Empty;
    /// <summary>Bare track id (last segment of <see cref="Uri"/>). Carried
    /// as a separate field so <c>TrackStateBehavior.CurrentTrackId</c>
    /// comparisons don't have to re-parse the URI on every dispatch.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Wire-side track name (Spotify ships either <c>name</c> or
    /// <c>trackName</c>; we pick whichever lands). Projected as
    /// <see cref="ITrackItem.Title"/>.</summary>
    public string? Name { get; init; }
    /// <summary>Joined ", "-separated artist names ready for display. Projected
    /// as <see cref="ITrackItem.ArtistName"/>.</summary>
    public string? ArtistNames { get; init; }
    public string? AlbumName { get; init; }
    public string? ImageUrl { get; init; }
    public System.TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }
    public int OriginalIndex { get; init; }

    // ── ITrackItem surface ───────────────────────────────────────────────
    // Most interface members have sensible defaults at the interface level
    // (HasVideo/IsLocal/Uid/etc.) so only the required members need explicit
    // projections here.

    string ITrackItem.Title => Name ?? string.Empty;
    string ITrackItem.ArtistName => ArtistNames ?? string.Empty;
    string ITrackItem.ArtistId => string.Empty;          // no artist nav target on recs
    string ITrackItem.AlbumName => AlbumName ?? string.Empty;
    string ITrackItem.AlbumId => string.Empty;           // no album nav target on recs
    bool ITrackItem.IsLoaded => true;
    string ITrackItem.DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
        : Duration.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Mutable per ITrackItem; future enhancement could backfill
    /// from the library save service. Default false — the user can still
    /// hover the heart button to like the track to their library
    /// independently of adding it to this playlist.</summary>
    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsLiked)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
