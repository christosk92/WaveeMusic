using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.DescriptorExtension;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Stores;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Real library data service backed by MetadataDatabase and PlaylistCacheService.
/// Reads liked/albums/artists from SQLite and playlists from the dedicated playlist cache.
/// Reacts to IMessenger messages for refresh signaling.
/// </summary>
public sealed class LibraryDataService : ILibraryDataService
{
    private readonly IMetadataDatabase _database;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IExtendedMetadataClient _metadataClient;
    private readonly ExtendedMetadataStore? _extendedMetadataStore;
    private readonly ITrackLikeService _likeService;
    private readonly ISession _session;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private IReadOnlyList<LikedSongsFilterDto> _cachedLikedSongFilters = Array.Empty<LikedSongsFilterDto>();
    private string? _likedSongFiltersEtag;

    // Sync complete fans out across messenger + playlist-cache subjects +
    // like-service save events in tight succession — each producing a fresh
    // DataChanged emission. Without coalescing, every consumer reloads 2-4
    // times per logical change. Batch into a single trailing event per burst.
    private static readonly TimeSpan ChangeCoalesceWindow = TimeSpan.FromMilliseconds(150);
    private readonly object _coalesceGate = new();
    private bool _dataChangedPending;
    private bool _playlistsChangedPending;

    public event EventHandler? PlaylistsChanged;
    public event EventHandler? DataChanged;

    public LibraryDataService(
        IMetadataDatabase database,
        IPlaylistCacheService playlistCache,
        IExtendedMetadataClient metadataClient,
        IMessenger messenger,
        ITrackLikeService likeService,
        ISession session,
        ExtendedMetadataStore? extendedMetadataStore = null,
        ILogger<LibraryDataService>? logger = null)
    {
        _database = database;
        _playlistCache = playlistCache;
        _metadataClient = metadataClient;
        _extendedMetadataStore = extendedMetadataStore;
        _likeService = likeService;
        _session = session;
        _messenger = messenger;
        _logger = logger;

        messenger.Register<LibraryDataChangedMessage>(this, (_, _) =>
        {
            _logger?.LogDebug("LibraryDataService: received LibraryDataChangedMessage");
            ScheduleChangeEmit(dataChanged: true, playlistsChanged: false);
        });

        messenger.Register<PlaylistsChangedMessage>(this, (_, _) =>
        {
            _logger?.LogDebug("LibraryDataService: received PlaylistsChangedMessage");
            ScheduleChangeEmit(dataChanged: true, playlistsChanged: true);
        });

        _likeService.SaveStateChanged += () =>
        {
            _logger?.LogDebug("LibraryDataService: SaveStateChanged forwarding as DataChanged");
            ScheduleChangeEmit(dataChanged: true, playlistsChanged: false);
        };

        _playlistCache.Changes.Subscribe(_ =>
        {
            _logger?.LogDebug("LibraryDataService: playlist cache change received");
            ScheduleChangeEmit(dataChanged: true, playlistsChanged: true);
        });
    }

    private void ScheduleChangeEmit(bool dataChanged, bool playlistsChanged)
    {
        bool startFlush = false;
        lock (_coalesceGate)
        {
            startFlush = !_dataChangedPending && !_playlistsChangedPending;
            _dataChangedPending |= dataChanged;
            _playlistsChangedPending |= playlistsChanged;
        }
        if (startFlush)
            _ = FlushAfterDelayAsync();
    }

    private async Task FlushAfterDelayAsync()
    {
        await Task.Delay(ChangeCoalesceWindow).ConfigureAwait(false);
        bool fireData, firePlaylists;
        lock (_coalesceGate)
        {
            fireData = _dataChangedPending;
            firePlaylists = _playlistsChangedPending;
            _dataChangedPending = false;
            _playlistsChangedPending = false;
        }
        if (firePlaylists) PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        if (fireData) DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
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
        if (string.IsNullOrWhiteSpace(_session.GetUserData()?.Username))
            return Array.Empty<PlaylistSummaryDto>();

        var snapshot = await _playlistCache.GetRootlistAsync(ct: ct);
        var currentUsername = _session.GetUserData()?.Username;
        var results = new List<PlaylistSummaryDto>();

        foreach (var entry in snapshot.Items.OfType<RootlistPlaylist>())
        {
            snapshot.Decorations.TryGetValue(entry.Uri, out var decoration);
            var persisted = decoration == null
                ? await _database.GetPlaylistCacheEntryAsync(entry.Uri, touchAccess: false, ct)
                : null;

            var ownerUsername = decoration?.OwnerUsername ?? persisted?.OwnerUsername ?? persisted?.OwnerName;
            // Reject empty / whitespace names — `??` alone lets "" win over a
            // valid persisted fallback, which renders as a blank sidebar row.
            var name = !string.IsNullOrWhiteSpace(decoration?.Name) ? decoration!.Name
                     : !string.IsNullOrWhiteSpace(persisted?.Name)  ? persisted!.Name
                     : "Playlist";
            results.Add(new PlaylistSummaryDto
            {
                Id = entry.Uri,
                Name = name,
                ImageUrl = decoration?.ImageUrl ?? persisted?.ImageUrl,
                TrackCount = decoration?.Length ?? persisted?.TrackCount ?? 0,
                IsOwner = !string.IsNullOrWhiteSpace(currentUsername)
                    && string.Equals(ownerUsername, currentUsername, StringComparison.OrdinalIgnoreCase)
            });
        }

        return results;
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

        var trackUris = entities.Select(e => e.Uri).ToList();
        var descriptorBytes = await _database
            .GetExtensionsBulkAsync(trackUris, ExtensionKind.TrackDescriptor, ct)
            .ConfigureAwait(false);

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
            IsLiked = true,
            Tags = ExtractDescriptorTags(
                descriptorBytes.TryGetValue(e.Uri, out var bytes) ? bytes : null,
                e.Genre)
        }).ToList();
    }

    public async Task<IReadOnlyList<LikedSongsFilterDto>> GetLikedSongFiltersAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _session.SpClient.GetLikedSongsContentFiltersAsync(_likedSongFiltersEtag, ct);

            if (result.IsNotModified)
                return _cachedLikedSongFilters;

            var filters = result.Filters
                .Select(LikedSongsFilterDto.FromContentFilter)
                .ToList();

            _cachedLikedSongFilters = filters;
            _likedSongFiltersEtag = result.ETag ?? _likedSongFiltersEtag;

            return filters;
        }
        catch (SessionException ex)
        {
            _logger?.LogDebug(ex, "Skipping liked-songs content filters before session authentication");
            return _cachedLikedSongFilters;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load liked-songs content filters");
            return _cachedLikedSongFilters;
        }
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
        => GetPlaylistCoreAsync(playlistId, ct);

    public async Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await _playlistCache.GetPlaylistAsync(playlistId, ct: ct);
        var trackItems = playlist.Items
            .Where(static item => item.Uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            .ToArray();

        if (trackItems.Length == 0)
            return Array.Empty<PlaylistTrackDto>();

        // Route through ExtendedMetadataStore when available so concurrent
        // playlist opens share a single batched POST; fall back to the
        // client's direct batch API if DI didn't inject the store.
        var parsedTracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        if (_extendedMetadataStore is not null)
        {
            var requests = trackItems
                .Select(item => (item.Uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));
            var resolved = await _extendedMetadataStore.GetManyAsync(requests, ct);
            foreach (var (key, bytes) in resolved)
            {
                try
                {
                    parsedTracks[key.Uri] = Track.Parser.ParseFrom(bytes);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to parse Track for {Uri}", key.Uri);
                }
            }
        }
        else
        {
            var requests = trackItems
                .Select(item => (item.Uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));
            var response = await _metadataClient.GetBatchedExtensionsAsync(requests, ct);
            foreach (var data in response.GetAllExtensionData(ExtensionKind.TrackV4))
            {
                var track = data.UnpackAs<Track>();
                if (track != null)
                    parsedTracks[data.EntityUri] = track;
            }
        }

        var tracks = new List<PlaylistTrackDto>(trackItems.Length);
        foreach (var item in trackItems)
        {
            if (!parsedTracks.TryGetValue(item.Uri, out var track))
                continue;

            tracks.Add(new PlaylistTrackDto
            {
                Id = ExtractBareId(item.Uri, "spotify:track:"),
                Uri = item.Uri,
                Title = track.Name ?? "Unknown",
                ArtistName = track.Artist.Count > 0
                    ? string.Join(", ", track.Artist.Select(static artist => artist.Name))
                    : "",
                ArtistId = GetSpotifyUri(track.Artist.Count > 0 ? track.Artist[0].Gid : null, SpotifyIdType.Artist) ?? "",
                AlbumName = track.Album?.Name ?? "",
                AlbumId = GetSpotifyUri(track.Album?.Gid, SpotifyIdType.Album) ?? "",
                ImageUrl = GetImageUrl(track.Album, Image.Types.Size.Default),
                Duration = TimeSpan.FromMilliseconds(track.Duration),
                AddedAt = item.AddedAt?.LocalDateTime,
                AddedBy = item.AddedBy,
                IsExplicit = track.Explicit,
                OriginalIndex = tracks.Count + 1,
                // Spotify's on-wire uid: lower-case hex of the 8-byte itemId. Matches
                // the format web/mobile clients publish for skip-to-uid round-trip.
                Uid = item.ItemId is { Length: > 0 } id ? Convert.ToHexString(id).ToLowerInvariant() : null,
                FormatAttributes = item.FormatAttributes.Count > 0 ? item.FormatAttributes : null
            });
        }

        return tracks;
    }

    public Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
        => Task.CompletedTask;

    private async Task<PlaylistDetailDto> GetPlaylistCoreAsync(string playlistId, CancellationToken ct)
    {
        var playlist = await _playlistCache.GetPlaylistAsync(playlistId, ct: ct);
        // playlist.OwnerUsername sometimes arrives as a bare username and sometimes as
        // the full "spotify:user:{id}" URI depending on which proto path filled it.
        // Canonicalize at the DTO boundary so OwnerId is a single-prefix URI and
        // OwnerName is the bare username (the resolver can replace it with a real
        // display name once the round-trip completes).
        var bareOwner = string.IsNullOrWhiteSpace(playlist.OwnerUsername)
            ? string.Empty
            : ExtractBareId(playlist.OwnerUsername, "spotify:user:");
        return new PlaylistDetailDto
        {
            Id = playlist.Uri,
            Name = playlist.Name,
            Description = playlist.Description,
            ImageUrl = playlist.ImageUrl,
            HeaderImageUrl = playlist.HeaderImageUrl,
            OwnerName = bareOwner,
            OwnerId = string.IsNullOrEmpty(bareOwner) ? null : $"spotify:user:{bareOwner}",
            TrackCount = playlist.Length,
            FollowerCount = 0,
            IsOwner = playlist.BasePermission == CachedPlaylistBasePermission.Owner,
            IsCollaborative = playlist.IsCollaborative,
            IsPublic = playlist.IsPublic,
            BasePermission = MapBasePermission(playlist.BasePermission),
            Capabilities = MapCapabilities(playlist.Capabilities),
            FormatAttributes = playlist.FormatAttributes.Count > 0 ? playlist.FormatAttributes : null
        };
    }

    private static string ExtractBareId(string uri, string prefix) =>
        uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;

    private static PlaylistBasePermission MapBasePermission(CachedPlaylistBasePermission value)
    {
        return value switch
        {
            CachedPlaylistBasePermission.Owner => PlaylistBasePermission.Owner,
            CachedPlaylistBasePermission.Contributor => PlaylistBasePermission.Contributor,
            _ => PlaylistBasePermission.Viewer
        };
    }

    private static PlaylistCapabilitiesDto MapCapabilities(CachedPlaylistCapabilities value)
    {
        return new PlaylistCapabilitiesDto
        {
            CanView = value.CanView,
            CanEditItems = value.CanEditItems,
            CanAdministratePermissions = value.CanAdministratePermissions,
            CanCancelMembership = value.CanCancelMembership,
            CanAbuseReport = value.CanAbuseReport
        };
    }

    private static string? GetSpotifyUri(Google.Protobuf.ByteString? gid, SpotifyIdType type)
    {
        if (gid is not { Length: > 0 })
            return null;

        var prefix = type == SpotifyIdType.Album ? "spotify:album:" : "spotify:artist:";
        return prefix + SpotifyId.FromRaw(gid.Span, type).ToBase62();
    }

    private static string? GetImageUrl(Album? album, Image.Types.Size preferredSize)
    {
        if (album?.CoverGroup?.Image.Count is not > 0)
            return null;

        var image = album.CoverGroup.Image.FirstOrDefault(img => img.Size == preferredSize)
            ?? album.CoverGroup.Image.FirstOrDefault();
        if (image == null)
            return null;

        return $"spotify:image:{Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant()}";
    }

    private static IReadOnlyList<string> ExtractTags(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return Array.Empty<string>();

        return genre
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractDescriptorTags(byte[]? descriptorBytes, string? fallbackGenre)
    {
        if (descriptorBytes is null || descriptorBytes.Length == 0)
            return ExtractTags(fallbackGenre);

        try
        {
            var data = ExtensionDescriptorData.Parser.ParseFrom(descriptorBytes);
            if (data.Descriptors.Count == 0)
                return ExtractTags(fallbackGenre);

            var tags = new List<string>(data.Descriptors.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in data.Descriptors)
            {
                var text = descriptor.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                var lower = string.Intern(text.ToLowerInvariant());
                if (seen.Add(lower))
                    tags.Add(lower);
            }

            return tags.Count == 0 ? ExtractTags(fallbackGenre) : tags;
        }
        catch
        {
            return ExtractTags(fallbackGenre);
        }
    }

    public void RequestSyncIfEmpty()
    {
        _logger?.LogDebug("RequestSyncIfEmpty sending RequestLibrarySyncMessage");
        _messenger.Send(new RequestLibrarySyncMessage());
    }
}
