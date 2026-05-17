using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.DescriptorExtension;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.Protocol.Resumption;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Services;

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
    private readonly Wavee.UI.Services.Infra.IChangeBus _changeBus;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly ILogger? _logger;
    // ISpotifyLibraryService moved with the pin methods to PinService (Phase 2).
    // IOutboxProcessor + _databasePath moved with the mutation methods to PlaylistMutationService (Phase 2).
    // Pinned-state cache + lock moved to PinService (Phase 2).
    private IReadOnlyList<LikedSongsFilterDto> _cachedLikedSongFilters = Array.Empty<LikedSongsFilterDto>();
    private string? _likedSongFiltersEtag;
    private readonly IPodcastEpisodeService _podcastEpisodeService;

    // Sync complete fans out across messenger + playlist-cache subjects +
    // like-service save events in tight succession. Coalescing now lives in
    // IChangeBus — publish freely; subscribers see one emission per scope per
    // burst.

    public LibraryDataService(
        IMetadataDatabase database,
        IPlaylistCacheService playlistCache,
        IExtendedMetadataClient metadataClient,
        Wavee.UI.Services.Infra.IChangeBus changeBus,
        IMessenger messenger,
        ITrackLikeService likeService,
        ISession session,
        IPodcastEpisodeService podcastEpisodeService,
        ExtendedMetadataStore? extendedMetadataStore = null,
        IMusicVideoMetadataService? musicVideoMetadata = null,
        ILogger<LibraryDataService>? logger = null)
    {
        _database = database;
        _playlistCache = playlistCache;
        _metadataClient = metadataClient;
        _extendedMetadataStore = extendedMetadataStore;
        _likeService = likeService;
        _session = session;
        _messenger = messenger;
        _changeBus = changeBus;
        _podcastEpisodeService = podcastEpisodeService;
        _musicVideoMetadata = musicVideoMetadata;
        _logger = logger;

        _likeService.SaveStateChanged += () =>
        {
            _logger?.LogDebug("LibraryDataService: SaveStateChanged → ChangeScope.Library");
            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
        };

        _playlistCache.Changes.Subscribe(evt =>
        {
            // Only the rootlist URI represents a sidebar-shape change (playlist
            // added / removed / renamed / moved). Individual playlist URIs fire
            // for content updates (dealer pushes after a /signals POST, item
            // edits, etc.) — those don't mutate the sidebar's structure, so
            // emitting playlistsChanged on them causes a full sidebar rebuild
            // that flashes images and collapses/re-expands sections. Page-
            // level VMs (PlaylistViewModel etc.) listen via PlaylistStore.
            var isRootlistUpdate = string.Equals(evt.Uri, PlaylistCacheUris.Rootlist, StringComparison.Ordinal);
            _logger?.LogDebug(
                "LibraryDataService: playlist cache change for {Uri} (rootlistUpdate={IsRootlist})",
                evt.Uri, isRootlistUpdate);
            if (isRootlistUpdate)
                _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Playlists);
            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
        });
    }

    public async Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        var trackCount = _likeService.GetCount(SavedItemType.Track);
        var albumCount = _likeService.GetCount(SavedItemType.Album);
        var artistCount = _likeService.GetCount(SavedItemType.Artist);
        var yourEpisodesCount = await _database
            .GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.ListenLater, ct)
            .ConfigureAwait(false);
        var podcastCount = (await _podcastEpisodeService.GetPodcastShowsAsync(ct).ConfigureAwait(false)).Count;

        _logger?.LogDebug("Library stats: {Tracks} tracks, {Albums} albums, {Artists} artists, {Episodes} episodes",
            trackCount, albumCount, artistCount, yourEpisodesCount);

        return new LibraryStatsDto
        {
            LikedSongsCount = trackCount,
            AlbumCount = albumCount,
            ArtistCount = artistCount,
            YourEpisodesCount = yourEpisodesCount,
            PodcastCount = podcastCount,
        };
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

        _logger?.LogInformation("[rootlist] GetUserPlaylistsAsync read (sidebar refresh trigger)");
        var snapshot = await _playlistCache.GetRootlistAsync(ct: ct);
        var summaries = await BuildPlaylistSummariesAsync(snapshot, ct);
        _logger?.LogInformation(
            "[rootlist] GetUserPlaylistsAsync returned {Count} playlists",
            summaries.Count);
        return summaries;
    }

    public async Task<IReadOnlyList<PlaylistSummaryDto>?> TryGetUserPlaylistsFromCacheAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_session.GetUserData()?.Username))
            return null;

        var snapshot = await _playlistCache.TryGetRootlistFromCacheAsync(ct);
        if (snapshot is null)
        {
            _logger?.LogInformation("[rootlist] TryGetUserPlaylistsFromCacheAsync source=cache MISS");
            return null;
        }

        var summaries = await BuildPlaylistSummariesAsync(snapshot, ct);
        _logger?.LogInformation(
            "[rootlist] TryGetUserPlaylistsFromCacheAsync source=cache HIT returned {Count} playlists",
            summaries.Count);
        return summaries;
    }

    private async Task<IReadOnlyList<PlaylistSummaryDto>> BuildPlaylistSummariesAsync(
        RootlistSnapshot snapshot,
        CancellationToken ct)
    {
        var currentUsername = _session.GetUserData()?.Username;
        var currentUserId = Helpers.PlaylistUriHelpers.ExtractBareId(currentUsername, "spotify:user:");
        var results = new List<PlaylistSummaryDto>();

        foreach (var entry in snapshot.Items.OfType<RootlistPlaylist>())
        {
            snapshot.Decorations.TryGetValue(entry.Uri, out var decoration);
            var persisted = decoration == null
                ? await _database.GetPlaylistCacheEntryAsync(entry.Uri, touchAccess: false, ct)
                : null;

            var ownerUsername = decoration?.OwnerUsername ?? persisted?.OwnerUsername ?? persisted?.OwnerName;
            var ownerUserId = Helpers.PlaylistUriHelpers.ExtractBareId(ownerUsername, "spotify:user:");
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
                IsOwner = !string.IsNullOrWhiteSpace(currentUserId)
                    && string.Equals(ownerUserId, currentUserId, StringComparison.OrdinalIgnoreCase),
                // Decoration is the authoritative source for collab status on
                // the rootlist row; fall back to the persisted entry when the
                // decoration hasn't loaded yet. Safe default is false (drops
                // get rejected until the real flag arrives — better than
                // silently failing server-side).
                IsCollaborative = decoration?.IsCollaborative ?? persisted?.IsCollaborative ?? false,
            });
        }

        return results;
    }

    // Pin / Unpin / GetPinnedItems / IsPinned moved to PinService (Phase 2).

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
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Track, int.MaxValue, 0, ct);

        var trackUris = entities.Select(e => e.Uri).ToList();
        var descriptorBytes = await _database
            .GetExtensionsBulkAsync(trackUris, ExtensionKind.TrackDescriptor, ct)
            .ConfigureAwait(false);

        var cachedVideoAvailability = _musicVideoMetadata is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _musicVideoMetadata.GetCachedAvailabilityAsync(trackUris, ct).ConfigureAwait(false);

        return entities.Select((e, idx) => new LikedSongDto
        {
            Id = Helpers.PlaylistUriHelpers.ExtractBareId(e.Uri, "spotify:track:"),
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
                e.Genre),
            HasVideo = cachedVideoAvailability.TryGetValue(e.Uri, out var hasVideo) && hasVideo
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


    // CreateFolderAsync moved to RootlistService (Phase 2).
    // Rootlist ChangeInfo + Nonce helpers moved to Helpers/RootlistGraph.cs.

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

        var cachedVideoAvailability = _musicVideoMetadata is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _musicVideoMetadata
                .GetCachedAvailabilityAsync(trackItems.Select(static item => item.Uri), ct)
                .ConfigureAwait(false);

        var tracks = new List<PlaylistTrackDto>(trackItems.Length);
        foreach (var item in trackItems)
        {
            if (!parsedTracks.TryGetValue(item.Uri, out var track))
                continue;

            tracks.Add(new PlaylistTrackDto
            {
                Id = Helpers.PlaylistUriHelpers.ExtractBareId(item.Uri, "spotify:track:"),
                Uri = item.Uri,
                Title = track.Name ?? "Unknown",
                ArtistName = track.Artist.Count > 0
                    ? string.Join(", ", track.Artist.Select(static artist => artist.Name))
                    : "",
                ArtistId = GetSpotifyUri(track.Artist.Count > 0 ? track.Artist[0].Gid : null, SpotifyIdType.Artist) ?? "",
                AlbumName = track.Album?.Name ?? "",
                AlbumId = GetSpotifyUri(track.Album?.Gid, SpotifyIdType.Album) ?? "",
                ImageUrl = GetImageUrl(track.Album, Image.Types.Size.Default),
                // 48 px row art reads ImageSmallUrl first; the Small flavor is
                // a distinct CDN image-id (~80 px), ~10× smaller bytes than Default.
                ImageSmallUrl = GetImageUrl(track.Album, Image.Types.Size.Small),
                Duration = TimeSpan.FromMilliseconds(track.Duration),
                AddedAt = item.AddedAt?.LocalDateTime,
                AddedBy = item.AddedBy,
                IsExplicit = track.Explicit,
                OriginalIndex = tracks.Count + 1,
                // Spotify's on-wire uid: lower-case hex of the 8-byte itemId. Matches
                // the format web/mobile clients publish for skip-to-uid round-trip.
                Uid = item.ItemId is { Length: > 0 } id ? Convert.ToHexString(id).ToLowerInvariant() : null,
                FormatAttributes = item.FormatAttributes.Count > 0 ? item.FormatAttributes : null,
                HasVideo = cachedVideoAvailability.TryGetValue(item.Uri, out var hasVideo) && hasVideo
            });
        }

        return tracks;
    }









    // Move / out-of-folder rootlist methods + PostRootlist* helpers moved to RootlistService (Phase 2).

    // Playlist permission / collaborator methods moved to PlaylistPermissionService (Phase 2).

    public async Task<long> GetPlaylistFollowerCountAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            return await _session.SpClient.GetPlaylistFollowerCountAsync(playlistId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetPlaylistFollowerCountAsync failed for {Id} — defaulting to 0", playlistId);
            return 0;
        }
    }


    public async Task<AlbumPalette?> GetPlaylistPaletteAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            var response = await _session.Pathfinder
                .FetchPlaylistAsync(playlistId, ct)
                .ConfigureAwait(false);
            var set = response?.Data?.PlaylistV2?.VisualIdentity?.SquareCoverImage?.ExtractedColorSet;
            if (set is null)
            {
                _logger?.LogDebug("[palette] {Id} -> no extractedColorSet on response", playlistId);
                return null;
            }
            return MapPalette(set);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[palette] FetchPlaylistAsync failed for {Id} — palette unavailable", playlistId);
            return null;
        }
    }

    // Mirrors AlbumService.MapTier / MapPalette — duplicated here rather than
    // hoisted into a shared helper because it's small and only two callers
    // ever need it. If we add a third surface (concert?) consolidate then.
    private static AlbumPaletteTier? MapPaletteTier(ArtistExtractedColorPalette? palette)
    {
        if (palette?.BackgroundBase == null || palette.TextBrightAccent == null) return null;
        var bg = palette.BackgroundBase;
        var bgTint = palette.BackgroundTintedBase ?? palette.BackgroundBase;
        var accent = palette.TextBrightAccent;
        return new AlbumPaletteTier
        {
            BackgroundR = (byte)bg.Red,
            BackgroundG = (byte)bg.Green,
            BackgroundB = (byte)bg.Blue,
            BackgroundTintedR = (byte)bgTint.Red,
            BackgroundTintedG = (byte)bgTint.Green,
            BackgroundTintedB = (byte)bgTint.Blue,
            TextAccentR = (byte)accent.Red,
            TextAccentG = (byte)accent.Green,
            TextAccentB = (byte)accent.Blue,
        };
    }

    private static AlbumPalette? MapPalette(ArtistExtractedColorSet set)
    {
        var high = MapPaletteTier(set.HighContrast);
        var higher = MapPaletteTier(set.HigherContrast);
        var min = MapPaletteTier(set.MinContrast);
        if (high == null && higher == null && min == null) return null;
        return new AlbumPalette
        {
            HighContrast = high,
            HigherContrast = higher,
            MinContrast = min,
        };
    }

    private async Task<PlaylistDetailDto> GetPlaylistCoreAsync(string playlistId, CancellationToken ct)
    {
        var playlist = await _playlistCache.GetPlaylistAsync(playlistId, ct: ct);
        _logger?.LogInformation(
            "[session-signals] GetPlaylistCoreAsync: playlist={Id} AvailableSignals.Count={Count} first={First}",
            playlistId, playlist.AvailableSignals?.Count ?? -1,
            playlist.AvailableSignals?.Count > 0 ? playlist.AvailableSignals[0] : "<none>");
        // playlist.OwnerUsername sometimes arrives as a bare username and sometimes as
        // the full "spotify:user:{id}" URI depending on which proto path filled it.
        // Canonicalize at the DTO boundary so OwnerId is a single-prefix URI and
        // OwnerName is the bare username (the resolver can replace it with a real
        // display name once the round-trip completes).
        var bareOwner = string.IsNullOrWhiteSpace(playlist.OwnerUsername)
            ? string.Empty
            : Helpers.PlaylistUriHelpers.ExtractBareId(playlist.OwnerUsername, "spotify:user:");
        var bareCurrentUser = Helpers.PlaylistUriHelpers.ExtractBareId(_session.GetUserData()?.Username, "spotify:user:");
        var isOwner = playlist.BasePermission == CachedPlaylistBasePermission.Owner
            || (!string.IsNullOrWhiteSpace(bareOwner)
                && !string.IsNullOrWhiteSpace(bareCurrentUser)
                && string.Equals(bareOwner, bareCurrentUser, StringComparison.OrdinalIgnoreCase));

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
            // Real value flows in via a separate background fetch (LoadFollowerCountAsync
            // on the VM → ILibraryDataService.GetPlaylistFollowerCountAsync). Held at 0
            // here so the playlist detail load isn't blocked on a stat-only round trip.
            FollowerCount = 0,
            IsOwner = isOwner,
            IsCollaborative = playlist.IsCollaborative,
            IsPublic = playlist.IsPublic,
            BasePermission = isOwner ? PlaylistBasePermission.Owner : MapBasePermission(playlist.BasePermission),
            Capabilities = MapCapabilities(
                playlist.Capabilities,
                isOwner: isOwner),
            FormatAttributes = playlist.FormatAttributes.Count > 0 ? playlist.FormatAttributes : null,
            Revision = playlist.Revision.Length > 0 ? playlist.Revision : null,
            SessionControlOptions = BuildSessionControlOptions(playlist.FormatAttributes, playlist.AvailableSignals),
            SessionControlGroupId = null // no longer used — per-option identifiers come from AvailableSignals
        };
    }

    private static IReadOnlyList<SessionControlOption>? BuildSessionControlOptions(
        IReadOnlyDictionary<string, string>? formatAttributes,
        IReadOnlyList<string>? availableSignals)
    {
        var raw = SelectedListContentMapper.ExtractSessionControlOptions(formatAttributes);
        if (raw.Count == 0) return null;

        var result = new List<SessionControlOption>(raw.Count);
        foreach (var (optionKey, displayName) in raw)
        {
            // Match the chip's OptionKey to the server's advertised signal
            // identifier by suffix. Identifiers look like
            //   "session_control_display$<group_id>$<option_key>"
            // so we pick the one ending in "$<option_key>".
            string? signal = null;
            if (availableSignals is not null)
            {
                var suffix = "$" + optionKey;
                foreach (var id in availableSignals)
                {
                    if (id.EndsWith(suffix, System.StringComparison.Ordinal))
                    {
                        signal = id;
                        break;
                    }
                }
            }
            result.Add(new SessionControlOption
            {
                OptionKey = optionKey,
                DisplayName = displayName,
                SignalIdentifier = signal
            });
        }
        return result;
    }

    // Strip the prefix repeatedly — some cache entries arrived with the prefix applied
    // multiple times (`spotify:user:spotify:user:id`) from an earlier write path, and a
    // single-pass strip leaves the remainder still prefixed. Re-prefixing that in the
    // DTO then produced `spotify:user:spotify:user:id` in the UI header.


    // Rootlist index / span helpers moved to Helpers/RootlistGraph.cs (Phase 2).

    private static PlaylistBasePermission MapBasePermission(CachedPlaylistBasePermission value)
    {
        return value switch
        {
            CachedPlaylistBasePermission.Owner => PlaylistBasePermission.Owner,
            CachedPlaylistBasePermission.Contributor => PlaylistBasePermission.Contributor,
            _ => PlaylistBasePermission.Viewer
        };
    }

    private PlaylistCapabilitiesDto MapCapabilities(CachedPlaylistCapabilities value, bool isOwner)
    {
        var dto = new PlaylistCapabilitiesDto
        {
            CanView = value.CanView,
            // Owners can always edit / delete / administrate their own playlists.
            // The proto's per-field flags are observed to be unreliable for owners
            // (Spotify treats those as implied by base permission rather than
            // setting them on the wire), so derive defensively. The OR'd-in
            // explicit flag still covers the contributor case where the server
            // grants edit-items via an explicit permission level.
            CanEditItems = value.CanEditItems || isOwner,
            CanEditMetadata = value.CanEditMetadata || isOwner,
            CanDelete = isOwner,
            CanAdministratePermissions = value.CanAdministratePermissions || isOwner,
            CanCancelMembership = value.CanCancelMembership,
            CanAbuseReport = value.CanAbuseReport
        };
        _logger?.LogInformation(
            "[caps] MapCapabilities: isOwner={IsOwner} | raw=[CanView={V},EditItems={EI},EditMeta={EM},Admin={AD},Cancel={CC},Abuse={AB}] | dto=[EditItems={DEI},EditMeta={DEM},Delete={DD},Admin={DAD}]",
            isOwner,
            value.CanView, value.CanEditItems, value.CanEditMetadata, value.CanAdministratePermissions,
            value.CanCancelMembership, value.CanAbuseReport,
            dto.CanEditItems, dto.CanEditMetadata, dto.CanDelete, dto.CanAdministratePermissions);
        return dto;
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
