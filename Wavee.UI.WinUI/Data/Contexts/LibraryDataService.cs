using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Real library data service backed by MetadataDatabase.
/// Reads from synced SQLite — no direct Spotify API dependency at construction.
/// Reacts to IMessenger messages for refresh signaling.
/// </summary>
public sealed class LibraryDataService : ILibraryDataService
{
    private readonly IMetadataDatabase _database;
    private readonly ITrackLikeService _likeService;
    private readonly ILogger? _logger;

    public event EventHandler? PlaylistsChanged;
    public event EventHandler? DataChanged;

    public LibraryDataService(
        IMetadataDatabase database,
        IMessenger messenger,
        ITrackLikeService likeService,
        ILogger<LibraryDataService>? logger = null)
    {
        _database = database;
        _likeService = likeService;
        _logger = logger;

        // Subscribe to messenger — no dependency on ISpotifyLibraryService
        messenger.Register<LibraryDataChangedMessage>(this, (_, _) =>
        {
            _logger?.LogDebug("LibraryDataService: received LibraryDataChangedMessage");
            DataChanged?.Invoke(this, EventArgs.Empty);
        });

        messenger.Register<PlaylistsChangedMessage>(this, (_, _) =>
        {
            _logger?.LogDebug("LibraryDataService: received PlaylistsChangedMessage");
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
            DataChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        // Use in-memory cache counts — includes optimistic local likes not yet in SQLite
        var trackCount = _likeService.GetCount(SavedItemType.Track);
        var albumCount = _likeService.GetCount(SavedItemType.Album);
        var artistCount = _likeService.GetCount(SavedItemType.Artist);

        _logger?.LogDebug("Library stats: {Tracks} tracks, {Albums} albums, {Artists} artists",
            trackCount, albumCount, artistCount);

        return Task.FromResult(new LibraryStatsDto
        {
            LikedSongsCount = trackCount,
            AlbumCount = albumCount,
            ArtistCount = artistCount,
        });
    }

    public async Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default)
    {
        var tracks = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Track, 500, 0, ct);
        return tracks.Select(e => new LibraryItemDto
        {
            Id = e.Uri,
            Title = e.Title ?? "Unknown",
            Artist = e.ArtistName,
            Album = e.AlbumName,
            ImageUrl = e.ImageUrl,
            Duration = TimeSpan.FromMilliseconds(e.DurationMs ?? 0),
            AddedAt = e.AddedAt ?? DateTimeOffset.MinValue
        }).ToList();
    }

    public Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryItemDto>>(Array.Empty<LibraryItemDto>());

    public async Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        // Read playlists from local DB (synced by SpotifyLibraryService)
        var db = _database;
        // GetPlaylistsAsync is on ISpotifyLibraryService, but playlists are in spotify_playlists table.
        // For now, return empty — playlist reading from DB needs a dedicated query.
        // TODO: Add GetPlaylistsFromDbAsync to IMetadataDatabase
        _logger?.LogDebug("GetUserPlaylistsAsync: playlist DB query not yet implemented");
        return Array.Empty<PlaylistSummaryDto>();
    }

    public async Task<IReadOnlyList<LibraryAlbumDto>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Album, 500, 0, ct);
        return entities.Select(e => new LibraryAlbumDto
        {
            Id = e.Uri,
            Name = e.Title ?? "Unknown",
            ArtistName = e.ArtistName ?? "Unknown Artist",
            ImageUrl = e.ImageUrl,
            Year = e.ReleaseYear ?? 0,
            TrackCount = e.TrackCount ?? 0,
            AddedAt = e.AddedAt ?? DateTimeOffset.MinValue
        }).ToList();
    }

    public async Task<IReadOnlyList<LibraryArtistDto>> GetArtistsAsync(CancellationToken ct = default)
    {
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Artist, 500, 0, ct);
        return entities.Select(e => new LibraryArtistDto
        {
            Id = e.Uri,
            Name = e.Title ?? e.ArtistName ?? "Unknown",
            ImageUrl = e.ImageUrl,
            FollowerCount = e.FollowerCount ?? 0,
            AddedAt = e.AddedAt ?? DateTimeOffset.MinValue
        }).ToList();
    }

    public Task<IReadOnlyList<LibraryArtistTopTrackDto>> GetArtistTopTracksAsync(string artistId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryArtistTopTrackDto>>(Array.Empty<LibraryArtistTopTrackDto>());

    public Task<IReadOnlyList<LibraryArtistAlbumDto>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryArtistAlbumDto>>(Array.Empty<LibraryArtistAlbumDto>());

    public async Task<IReadOnlyList<LikedSongDto>> GetLikedSongsAsync(CancellationToken ct = default)
    {
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Track, 500, 0, ct);
        return entities.Select((e, idx) => new LikedSongDto
        {
            Id = ExtractBareId(e.Uri, "spotify:track:"),
            Uri = e.Uri,
            Title = e.Title ?? "Unknown",
            ArtistName = e.ArtistName ?? "",
            ArtistId = "",
            AlbumName = e.AlbumName ?? "",
            AlbumId = e.AlbumUri ?? "",
            ImageUrl = e.ImageUrl,
            Duration = TimeSpan.FromMilliseconds(e.DurationMs ?? 0),
            AddedAt = e.AddedAt.HasValue ? e.AddedAt.Value.LocalDateTime : DateTime.Now,
            IsExplicit = false,
            OriginalIndex = idx + 1,
            IsLiked = true
        }).ToList();
    }

    public Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default)
    {
        _logger?.LogWarning("CreatePlaylistAsync not yet implemented");
        return Task.FromResult(new PlaylistSummaryDto
        {
            Id = $"spotify:playlist:new-{Guid.NewGuid():N}",
            Name = name,
            TrackCount = trackIds?.Count ?? 0,
            IsOwner = true
        });
    }

    public Task<PlaylistDetailDto> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
        => Task.FromResult(new PlaylistDetailDto { Id = playlistId, Name = "Playlist", OwnerName = "" });

    public Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlaylistTrackDto>>(Array.Empty<PlaylistTrackDto>());

    public Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
        => Task.CompletedTask;

    private static string ExtractBareId(string uri, string prefix) =>
        uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
}
