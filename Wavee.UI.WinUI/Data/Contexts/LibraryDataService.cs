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
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly ILogger? _logger;
    private IReadOnlyList<LikedSongsFilterDto> _cachedLikedSongFilters = Array.Empty<LikedSongsFilterDto>();
    private string? _likedSongFiltersEtag;
    private static readonly TimeSpan PodcastProgressCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PodcastProgressLookback = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _podcastProgressGate = new(1, 1);
    private IReadOnlyDictionary<string, PodcastEpisodeProgressDto> _cachedPodcastEpisodeProgress =
        new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
    private DateTimeOffset _podcastProgressFetchedAt;
    private bool _podcastProgressFetchFailed;

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

    private readonly string _databasePath;

    public LibraryDataService(
        IMetadataDatabase database,
        IPlaylistCacheService playlistCache,
        IExtendedMetadataClient metadataClient,
        IMessenger messenger,
        ITrackLikeService likeService,
        ISession session,
        Wavee.Core.DependencyInjection.WaveeCacheOptions cacheOptions,
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
        _musicVideoMetadata = musicVideoMetadata;
        _logger = logger;
        _databasePath = cacheOptions.DatabasePath;

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

        _playlistCache.Changes.Subscribe(evt =>
        {
            // Only the rootlist URI represents a sidebar-shape change (playlist
            // added / removed / renamed / moved). Individual playlist URIs fire
            // for content updates (dealer pushes after a /signals POST, item
            // edits, etc.) — those don't mutate the sidebar's structure, so
            // emitting playlistsChanged on them causes a full sidebar rebuild
            // that flashes images and collapses/re-expands sections. Page-
            // level VMs (PlaylistViewModel etc.) listen via PlaylistStore /
            // DataChanged and refresh themselves.
            var isRootlistUpdate = string.Equals(evt.Uri, PlaylistCacheUris.Rootlist, StringComparison.Ordinal);
            _logger?.LogDebug(
                "LibraryDataService: playlist cache change for {Uri} (rootlistUpdate={IsRootlist})",
                evt.Uri, isRootlistUpdate);
            ScheduleChangeEmit(dataChanged: true, playlistsChanged: isRootlistUpdate);
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

    public async Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        var trackCount = _likeService.GetCount(SavedItemType.Track);
        var albumCount = _likeService.GetCount(SavedItemType.Album);
        var artistCount = _likeService.GetCount(SavedItemType.Artist);
        var yourEpisodesCount = await _database
            .GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.ListenLater, ct)
            .ConfigureAwait(false);
        var podcastCount = (await GetPodcastShowsAsync(ct).ConfigureAwait(false)).Count;

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
                e.Genre),
            HasVideo = cachedVideoAvailability.TryGetValue(e.Uri, out var hasVideo) && hasVideo
        }).ToList();
    }

    public async Task<IReadOnlyList<LibraryEpisodeDto>> GetYourEpisodesAsync(CancellationToken ct = default)
    {
        var entities = await _database
            .GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.ListenLater, int.MaxValue, 0, ct)
            .ConfigureAwait(false);

        return entities
            .Where(static e => e.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            .Select((e, idx) => new LibraryEpisodeDto
            {
                Id = ExtractBareId(e.Uri, "spotify:episode:"),
                Uri = e.Uri,
                Title = e.Title ?? "Unknown episode",
                ArtistName = e.Publisher ?? e.ArtistName ?? e.AlbumName ?? "",
                ArtistId = "",
                AlbumName = e.AlbumName ?? e.ArtistName ?? "",
                AlbumId = e.AlbumUri ?? "",
                ImageUrl = e.ImageUrl,
                Description = e.Description,
                Duration = TimeSpan.FromMilliseconds(e.DurationMs ?? 0),
                AddedAt = e.AddedAt.HasValue ? e.AddedAt.Value.LocalDateTime : DateTime.Now,
                IsExplicit = false,
                IsPlayable = true,
                MediaTypes = ["AUDIO"],
                OriginalIndex = idx + 1,
                IsLiked = true
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryEpisodeDto>> GetRecentlyPlayedPodcastEpisodesAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        var progressByUri = await GetPodcastEpisodeProgressCacheAsync(ct).ConfigureAwait(false);
        if (_podcastProgressFetchFailed || progressByUri.Count == 0)
            return [];

        var savedEpisodes = await GetYourEpisodesAsync(ct).ConfigureAwait(false);
        var savedByUri = savedEpisodes
            .GroupBy(static episode => episode.Uri, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        var orderedProgress = progressByUri.Values
            .Where(static progress => progress.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            .OrderByDescending(GetProgressSortDate)
            .Take(limit)
            .ToList();

        var results = new List<LibraryEpisodeDto>(orderedProgress.Count);
        var index = 1;
        foreach (var progress in orderedProgress)
        {
            ct.ThrowIfCancellationRequested();

            LibraryEpisodeDto? episode;
            if (savedByUri.TryGetValue(progress.Uri, out var savedEpisode))
                episode = CreateRecentSavedEpisode(savedEpisode, progress, index);
            else
                episode = await FetchRecentPodcastEpisodeAsync(progress, index, ct).ConfigureAwait(false);

            if (episode is null)
                continue;

            results.Add(episode);
            index++;
        }

        return results;
    }

    public async Task<IReadOnlyList<LibraryPodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default)
    {
        var showsTask = _database
            .GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Show, int.MaxValue, 0, ct);
        var episodesTask = GetYourEpisodesAsync(ct);

        await Task.WhenAll(showsTask, episodesTask).ConfigureAwait(false);

        var episodesByShow = (await episodesTask.ConfigureAwait(false))
            .GroupBy(BuildEpisodeShowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static g => g.Key,
                static g => g.OrderByDescending(e => e.AddedAt).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var results = new List<LibraryPodcastShowDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var show in await showsTask.ConfigureAwait(false))
        {
            var key = BuildShowKey(show.Uri, show.Title ?? show.AlbumName ?? show.Publisher);
            episodesByShow.TryGetValue(key, out var savedEpisodes);
            seen.Add(key);

            results.Add(new LibraryPodcastShowDto
            {
                Id = show.Uri,
                Name = show.Title ?? show.AlbumName ?? show.Publisher ?? "Unknown podcast",
                Publisher = show.Publisher ?? show.ArtistName,
                Description = show.Description,
                ImageUrl = show.ImageUrl,
                EpisodeCount = show.EpisodeCount ?? savedEpisodes?.Count ?? 0,
                SavedEpisodeCount = savedEpisodes?.Count ?? 0,
                AddedAt = show.AddedAt?.LocalDateTime ?? DateTime.Now,
                LastEpisodeAddedAt = savedEpisodes?.FirstOrDefault()?.AddedAt,
                IsFollowed = true
            });
        }

        foreach (var (key, savedEpisodes) in episodesByShow)
        {
            if (seen.Contains(key))
                continue;

            var first = savedEpisodes[0];
            results.Add(new LibraryPodcastShowDto
            {
                Id = GetEpisodeShowId(first),
                Name = GetEpisodeShowName(first),
                Publisher = first.ArtistName,
                ImageUrl = first.ImageUrl,
                EpisodeCount = savedEpisodes.Count,
                SavedEpisodeCount = savedEpisodes.Count,
                AddedAt = savedEpisodes.Min(static e => e.AddedAt),
                LastEpisodeAddedAt = first.AddedAt,
                IsFollowed = false
            });
        }

        return results
            .OrderByDescending(static s => s.SortDate)
            .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PodcastEpisodeCommentsPageDto?> GetPodcastEpisodeCommentsPageAsync(
        string episodeUri, string? pageToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        try
        {
            var response = await _session.Pathfinder.GetCommentsForEntityAsync(episodeUri, pageToken, ct);
            var page = response?.Data?.Comments?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastEpisodeCommentsPageDto
            {
                Items = MapCommentItems(page.Items),
                NextPageToken = page.NextPageToken,
                TotalCount = page.TotalCount ?? 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch podcast comments page for {Uri}", episodeUri);
            return null;
        }
    }

    public async Task<PodcastCommentRepliesPageDto?> GetPodcastCommentRepliesAsync(
        string commentUri, string? pageToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);

        try
        {
            var response = await _session.Pathfinder.GetCommentRepliesAsync(commentUri, pageToken, ct);
            var page = response?.Data?.CommentReplies?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastCommentRepliesPageDto
            {
                Items = MapCommentReplies(page.Items),
                NextPageToken = page.NextPageToken,
                TotalCount = page.TotalCount ?? 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch comment replies for {Uri}", commentUri);
            return null;
        }
    }

    public async Task<PodcastCommentReactionsPageDto?> GetPodcastCommentReactionsAsync(
        string uri,
        string? pageToken,
        string? reactionUnicode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            var response = await _session.Pathfinder.GetCommentReactionsAsync(uri, pageToken, reactionUnicode, ct);
            var page = response?.Data?.CommentReactions?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastCommentReactionsPageDto
            {
                Items = MapCommentReactions(page.Items),
                ReactionCounts = MapCommentReactionCounts(page.ReactionCounts),
                NextPageToken = page.NextPageToken
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch comment reactions for {Uri}", uri);
            return null;
        }
    }

    public Task<PodcastEpisodeCommentReplyDto> CreatePodcastCommentReplyAsync(
        string commentUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        // Stub for now: keep the reply composer functional locally without
        // guessing Spotify's private reply mutation.
        _logger?.LogInformation("Podcast comment reply create stub invoked for {CommentUri}", commentUri);
        return Task.FromResult(new PodcastEpisodeCommentReplyDto
        {
            Uri = $"wavee:local-reply:{Guid.NewGuid():N}",
            AuthorName = "You",
            Text = text.Trim(),
            CreatedAt = DateTimeOffset.Now
        });
    }

    public Task ReactToPodcastCommentAsync(
        string commentUri,
        string emoji,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation(
            "Podcast comment reaction stub invoked for {CommentUri} with {Emoji}",
            commentUri,
            emoji);
        return Task.CompletedTask;
    }

    public Task ReactToPodcastCommentReplyAsync(
        string replyUri,
        string emoji,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation(
            "Podcast comment reply reaction stub invoked for {ReplyUri} with {Emoji}",
            replyUri,
            emoji);
        return Task.CompletedTask;
    }

    public async Task<PodcastEpisodeDetailDto?> GetPodcastEpisodeDetailAsync(string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        try
        {
            var episodeTask = _session.Pathfinder.GetEpisodeOrChapterAsync(episodeUri, ct);
            var recommendationsTask = FetchOptionalAsync(
                () => _session.Pathfinder.GetSeoRecommendedEpisodesAsync(episodeUri, ct),
                "episode recommendations");
            var commentsTask = FetchOptionalAsync(
                () => _session.Pathfinder.GetCommentsForEntityAsync(episodeUri, null, ct),
                "episode comments");

            await Task.WhenAll(episodeTask, recommendationsTask, commentsTask).ConfigureAwait(false);

            var episode = episodeTask.Result.Data?.EpisodeUnionV2;
            if (episode is null)
                return null;

            return MapPodcastEpisodeDetail(
                episode,
                recommendationsTask.Result,
                commentsTask.Result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch podcast episode detail for {EpisodeUri}", episodeUri);
            return null;
        }
    }

    public async Task<PodcastEpisodeProgressDto?> GetPodcastEpisodeProgressAsync(string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var cached = await GetPodcastEpisodeProgressCacheAsync(ct).ConfigureAwait(false);
        if (cached.TryGetValue(episodeUri, out var progress))
            return progress;

        return _podcastProgressFetchFailed
            ? CreatePodcastProgressError(episodeUri)
            : null;
    }

    private async Task<IReadOnlyDictionary<string, PodcastEpisodeProgressDto>> GetPodcastEpisodeProgressCacheAsync(
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (IsPodcastProgressCacheFresh(now))
        {
            return _cachedPodcastEpisodeProgress;
        }

        await _podcastProgressGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (IsPodcastProgressCacheFresh(now))
            {
                return _cachedPodcastEpisodeProgress;
            }

            try
            {
                var response = await _session.SpClient
                    .ListCurrentStatesAsync(now - PodcastProgressLookback, cancellationToken: ct)
                    .ConfigureAwait(false);

                _cachedPodcastEpisodeProgress = MapCurrentStateProgress(response);
                _podcastProgressFetchFailed = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch Herodotus podcast progress; using empty progress cache");
                _cachedPodcastEpisodeProgress = new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
                _podcastProgressFetchFailed = true;
            }

            _podcastProgressFetchedAt = now;
            return _cachedPodcastEpisodeProgress;
        }
        finally
        {
            _podcastProgressGate.Release();
        }
    }

    private bool IsPodcastProgressCacheFresh(DateTimeOffset now)
        => _podcastProgressFetchedAt != default &&
           now - _podcastProgressFetchedAt < PodcastProgressCacheTtl;

    private static PodcastEpisodeProgressDto CreatePodcastProgressError(string episodeUri) => new()
    {
        Uri = episodeUri,
        PlayedState = PodcastEpisodeProgressDto.ErrorState
    };

    private async Task<LibraryEpisodeDto?> FetchRecentPodcastEpisodeAsync(
        PodcastEpisodeProgressDto progress,
        int index,
        CancellationToken ct)
    {
        try
        {
            var response = await _session.Pathfinder.GetEpisodeOrChapterAsync(progress.Uri, ct).ConfigureAwait(false);
            var episode = response.Data?.EpisodeUnionV2;
            if (episode is null)
                return null;

            return CreateRecentUnsavedEpisode(episode, progress, index);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to enrich recently played podcast episode {EpisodeUri}", progress.Uri);
            return null;
        }
    }

    private static LibraryEpisodeDto CreateRecentSavedEpisode(
        LibraryEpisodeDto episode,
        PodcastEpisodeProgressDto progress,
        int index)
    {
        var recent = episode with
        {
            AddedAt = GetProgressSortDate(progress).LocalDateTime,
            OriginalIndex = index
        };
        recent.ApplyPlaybackProgress(progress.PlayedPosition, progress.PlayedState);
        return recent;
    }

    private static LibraryEpisodeDto CreateRecentUnsavedEpisode(
        PathfinderEpisode episode,
        PodcastEpisodeProgressDto progress,
        int index)
    {
        var show = episode.PodcastV2?.Data;
        var uri = string.IsNullOrWhiteSpace(episode.Uri) ? progress.Uri : episode.Uri!;
        var showName = show?.Name ?? "";

        var result = new LibraryEpisodeDto
        {
            Id = episode.Id ?? ExtractBareId(uri, "spotify:episode:"),
            Uri = uri,
            Title = episode.Name ?? "Unknown episode",
            ArtistName = showName,
            ArtistId = "",
            AlbumName = showName,
            AlbumId = show?.Uri ?? "",
            ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
            Description = episode.Description,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
            ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
            ShareUrl = episode.SharingInfo?.ShareUrl,
            PreviewUrl = episode.PreviewPlayback?.AudioPreview?.CdnUrl,
            MediaTypes = DistinctNonEmpty(episode.MediaTypes),
            AddedAt = GetProgressSortDate(progress).LocalDateTime,
            IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase),
            IsPlayable = episode.Playability?.Playable ?? true,
            OriginalIndex = index,
            IsLiked = false
        };

        result.ApplyPlaybackProgress(progress.PlayedPosition, progress.PlayedState);
        return result;
    }

    public Task<PodcastEpisodeCommentDto> CreatePodcastEpisodeCommentAsync(
        string episodeUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        // Stub for now: keep the UI/comment consent flow real without guessing
        // Spotify's private comment mutation. Replace this when the write
        // operation is captured and verified.
        _logger?.LogInformation("Podcast comment create stub invoked for {EpisodeUri}", episodeUri);
        return Task.FromResult(new PodcastEpisodeCommentDto
        {
            Uri = $"wavee:local-comment:{Guid.NewGuid():N}",
            AuthorName = "You",
            Text = text.Trim(),
            CreatedAt = DateTimeOffset.Now
        });
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
                FormatAttributes = item.FormatAttributes.Count > 0 ? item.FormatAttributes : null,
                HasVideo = cachedVideoAvailability.TryGetValue(item.Uri, out var hasVideo) && hasVideo
            });
        }

        return tracks;
    }

    public Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Local-track playlist overlays ──

    public Task AddLocalTracksToPlaylistAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || trackUris is null || trackUris.Count == 0)
            return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();

        // Append after the current max position so overlay rows preserve add-order.
        int basePosition;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COALESCE(MAX(position), -1) FROM playlist_overlay_items WHERE playlist_uri = $u;";
            probe.Parameters.AddWithValue("$u", playlistUri);
            basePosition = Convert.ToInt32(probe.ExecuteScalar()) + 1;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < trackUris.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO playlist_overlay_items (playlist_uri, item_uri, position, added_at, added_by)
                VALUES ($p, $i, $pos, $at, 'wavee:local');
                """;
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", trackUris[i]);
            cmd.Parameters.AddWithValue("$pos", basePosition + i);
            cmd.Parameters.AddWithValue("$at", now + i); // tiny offset preserves insert order on read
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        ScheduleChangeEmit(dataChanged: true, playlistsChanged: false);
        return Task.CompletedTask;
    }

    public Task RemoveLocalOverlayTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || trackUris is null || trackUris.Count == 0)
            return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();
        foreach (var u in trackUris)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM playlist_overlay_items WHERE playlist_uri = $p AND item_uri = $i;";
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", u);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        ScheduleChangeEmit(dataChanged: true, playlistsChanged: false);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DTOs.PlaylistOverlayRow>> GetPlaylistOverlayRowsAsync(string playlistUri, CancellationToken ct = default)
    {
        var list = new List<DTOs.PlaylistOverlayRow>();
        if (string.IsNullOrEmpty(playlistUri))
            return Task.FromResult<IReadOnlyList<DTOs.PlaylistOverlayRow>>(list);

        using var conn = OpenLocalConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_uri, position, added_at, added_by
            FROM playlist_overlay_items
            WHERE playlist_uri = $p
            ORDER BY position, added_at;
            """;
        cmd.Parameters.AddWithValue("$p", playlistUri);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DTOs.PlaylistOverlayRow(
                TrackUri: r.GetString(0),
                Position: r.GetInt32(1),
                AddedAt: r.GetInt64(2),
                AddedBy: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return Task.FromResult<IReadOnlyList<DTOs.PlaylistOverlayRow>>(list);
    }

    public Task ReorderPlaylistOverlayAsync(string playlistUri, IReadOnlyList<string> orderedTrackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || orderedTrackUris is null) return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < orderedTrackUris.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE playlist_overlay_items SET position = $pos WHERE playlist_uri = $p AND item_uri = $i;";
            cmd.Parameters.AddWithValue("$pos", i);
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", orderedTrackUris[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        ScheduleChangeEmit(dataChanged: true, playlistsChanged: false);
        return Task.CompletedTask;
    }

    private Microsoft.Data.Sqlite.SqliteConnection OpenLocalConnection()
    {
        var b = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
        };
        var c = new Microsoft.Data.Sqlite.SqliteConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    // Stubs for the inline edit Phase 1 work — the UI binds to these from day one
    // so the eventual HTTP/protobuf wire-up is decoupled. Today they no-op so an
    // edit looks successful locally but doesn't survive a refresh; replace with
    // real Spotify Web API calls (PUT /v1/playlists/{id}) when that layer lands.
    public Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "RenamePlaylistAsync stub invoked: playlistId={Id}, newName='{Name}' (no backend wire-up yet)",
            playlistId, newName);
        return Task.CompletedTask;
    }

    public Task UpdatePlaylistDescriptionAsync(string playlistId, string description, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "UpdatePlaylistDescriptionAsync stub invoked: playlistId={Id}, length={Len} (no backend wire-up yet)",
            playlistId, description?.Length ?? 0);
        return Task.CompletedTask;
    }

    public Task UpdatePlaylistCoverAsync(string playlistId, byte[] jpegBytes, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "UpdatePlaylistCoverAsync stub invoked: playlistId={Id}, bytes={Len} (no backend wire-up yet)",
            playlistId, jpegBytes?.Length ?? 0);
        return Task.CompletedTask;
    }

    public Task RemovePlaylistCoverAsync(string playlistId, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "RemovePlaylistCoverAsync stub invoked: playlistId={Id} (no backend wire-up yet)", playlistId);
        return Task.CompletedTask;
    }

    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "DeletePlaylistAsync stub invoked: playlistId={Id} (no backend wire-up yet)", playlistId);
        return Task.CompletedTask;
    }

    public Task SetPlaylistCollaborativeAsync(string playlistId, bool collaborative, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "SetPlaylistCollaborativeAsync stub invoked: playlistId={Id}, collaborative={Value} (no backend wire-up yet)",
            playlistId, collaborative);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlaylistMemberResult>> GetPlaylistMembersAsync(string playlistId, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "GetPlaylistMembersAsync stub invoked: playlistId={Id} (no backend wire-up yet — returning empty)", playlistId);
        return Task.FromResult<IReadOnlyList<PlaylistMemberResult>>(System.Array.Empty<PlaylistMemberResult>());
    }

    public Task SetPlaylistMemberRoleAsync(string playlistId, string memberUserId, PlaylistMemberRole role, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "SetPlaylistMemberRoleAsync stub invoked: playlistId={Id}, member={MemberId}, role={Role} (no backend wire-up yet)",
            playlistId, memberUserId, role);
        return Task.CompletedTask;
    }

    public Task RemovePlaylistMemberAsync(string playlistId, string memberUserId, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "RemovePlaylistMemberAsync stub invoked: playlistId={Id}, member={MemberId} (no backend wire-up yet)",
            playlistId, memberUserId);
        return Task.CompletedTask;
    }

    public Task<PlaylistInviteLink> CreatePlaylistInviteLinkAsync(string playlistId, PlaylistMemberRole grantedRole, TimeSpan ttl, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "CreatePlaylistInviteLinkAsync stub invoked: playlistId={Id}, role={Role}, ttl={Ttl} (no backend wire-up yet — returning placeholder)",
            playlistId, grantedRole, ttl);

        // Compose a plausible-looking placeholder so the UI renders end-to-end.
        // Token is random; real impl returns it from the permission-grant endpoint.
        var bareId = playlistId.StartsWith("spotify:playlist:", System.StringComparison.Ordinal)
            ? playlistId["spotify:playlist:".Length..]
            : playlistId;
        var token = System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        return Task.FromResult(new PlaylistInviteLink
        {
            Token = token,
            ShareUrl = $"https://open.spotify.com/playlist/{bareId}?pt={token}",
            CreatedAt = System.DateTimeOffset.UtcNow,
            Ttl = ttl,
            GrantedRole = grantedRole
        });
    }

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

    public Task SetPlaylistFollowedAsync(string playlistId, bool followed, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "SetPlaylistFollowedAsync stub invoked: playlistId={Id}, followed={Followed} (no backend wire-up yet — succeeding silently)",
            playlistId, followed);
        return Task.CompletedTask;
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
            // Real value flows in via a separate background fetch (LoadFollowerCountAsync
            // on the VM → ILibraryDataService.GetPlaylistFollowerCountAsync). Held at 0
            // here so the playlist detail load isn't blocked on a stat-only round trip.
            FollowerCount = 0,
            IsOwner = playlist.BasePermission == CachedPlaylistBasePermission.Owner,
            IsCollaborative = playlist.IsCollaborative,
            IsPublic = playlist.IsPublic,
            BasePermission = MapBasePermission(playlist.BasePermission),
            Capabilities = MapCapabilities(
                playlist.Capabilities,
                isOwner: playlist.BasePermission == CachedPlaylistBasePermission.Owner),
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
    private async Task<T?> FetchOptionalAsync<T>(Func<Task<T>> factory, string label) where T : class
    {
        try
        {
            return await factory().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Optional podcast detail fetch failed: {Label}", label);
            return null;
        }
    }

    private static PodcastEpisodeDetailDto MapPodcastEpisodeDetail(
        PathfinderEpisode episode,
        SeoRecommendedEpisodesResponse? recommendations,
        EntityCommentsResponse? comments)
    {
        var show = episode.PodcastV2?.Data;
        var commentsPage = comments?.Data?.Comments?.FirstOrDefault();

        return new PodcastEpisodeDetailDto
        {
            Uri = episode.Uri ?? "",
            Title = episode.Name ?? "Unknown episode",
            ShowUri = show?.Uri,
            ShowName = show?.Name,
            ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
            ShowImageUrl = BestImageUrl(show?.CoverArt?.Sources) ?? BestImageUrl(episode.CoverArt?.Sources),
            Description = episode.Description,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
            ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
            AddedAt = DateTime.Now,
            IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase),
            IsPlayable = episode.Playability?.Playable ?? true,
            IsPaywalled = episode.Restrictions?.PaywallContent ?? false,
            ShareUrl = episode.SharingInfo?.ShareUrl,
            PreviewUrl = episode.PreviewPlayback?.AudioPreview?.CdnUrl,
            PlayedState = episode.PlayedState?.State,
            PlayedPosition = TimeSpan.FromMilliseconds(Math.Max(0, episode.PlayedState?.PlayPositionMilliseconds ?? 0)),
            MediaTypes = DistinctNonEmpty(episode.MediaTypes),
            TranscriptLanguages = DistinctNonEmpty(episode.Transcripts?.Items?.Select(static t => t.Language)),
            Recommendations = MapEpisodeRecommendations(recommendations),
            Comments = MapEpisodeComments(comments),
            CommentsNextPageToken = commentsPage?.NextPageToken,
            CommentsTotalCount = commentsPage?.TotalCount ?? 0
        };
    }

    private static IReadOnlyDictionary<string, PodcastEpisodeProgressDto> MapCurrentStateProgress(
        ListCurrentStatesResponse response)
    {
        var result = new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
        foreach (var state in response.States)
        {
            var uri = !string.IsNullOrWhiteSpace(state.EntityUri)
                ? state.EntityUri
                : state.Revision?.Value?.EntityUri;

            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                continue;
            }

            var positionSeconds = state.Revision?.Value?.ResumePoint?.PositionSeconds ?? 0;
            var playedPosition = TimeSpan.FromSeconds(positionSeconds);
            result[uri] = new PodcastEpisodeProgressDto
            {
                Uri = uri,
                PlayedPosition = playedPosition,
                PlayedState = playedPosition > TimeSpan.Zero ? "IN_PROGRESS" : "NOT_STARTED",
                CreatedAt = ToDateTimeOffset(state.Revision?.CreateTime),
                UpdatedAt = ToDateTimeOffset(state.Revision?.UpdateTime)
            };
        }

        return result;
    }

    private static DateTimeOffset GetProgressSortDate(PodcastEpisodeProgressDto progress)
        => progress.UpdatedAt ?? progress.CreatedAt ?? DateTimeOffset.UtcNow;

    private static DateTimeOffset? ToDateTimeOffset(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
    {
        if (timestamp is null)
            return null;

        try
        {
            return timestamp.ToDateTimeOffset();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PodcastEpisodeRecommendationDto> MapEpisodeRecommendations(
        SeoRecommendedEpisodesResponse? response)
    {
        var items = response?.Data?.SeoRecommendedEpisode?.Items;
        if (items is null || items.Count == 0)
            return [];

        return items
            .Select(static item => item.Data)
            .Where(static episode => episode is not null)
            .Select(static episode =>
            {
                var show = episode!.PodcastV2?.Data;
                return new PodcastEpisodeRecommendationDto
                {
                    Uri = episode.Uri ?? "",
                    Title = episode.Name ?? "Unknown episode",
                    ShowName = show?.Name,
                    ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
                    Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
                    ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
                    IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase)
                };
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Uri))
            .Take(8)
            .ToList();
    }

    private static IReadOnlyList<PodcastEpisodeCommentDto> MapEpisodeComments(EntityCommentsResponse? response)
    {
        var page = response?.Data?.Comments?.FirstOrDefault();
        return MapCommentItems(page?.Items);
    }

    private static IReadOnlyList<PodcastEpisodeCommentDto> MapCommentItems(IReadOnlyList<EntityCommentItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastEpisodeCommentDto>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSensitive || string.IsNullOrWhiteSpace(item.CommentString) || string.IsNullOrWhiteSpace(item.Uri))
                continue;

            var topReplyAuthors = item.TopRepliesAuthors is { Count: > 0 } authors
                ? authors
                    .Select(static a => a?.Data)
                    .Where(static a => a is not null)
                    .Select(static a => new PodcastCommentAvatarDto
                    {
                        Name = a!.Name ?? "",
                        ImageUrl = BestImageUrl(a.Avatar?.Sources)
                    })
                    .ToList()
                : (IReadOnlyList<PodcastCommentAvatarDto>)[];

            var topReactions = item.ReactionsMetadata?.TopReactionUnicode is { Count: > 0 } emoji
                ? emoji.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList()
                : (IReadOnlyList<string>)[];

            result.Add(new PodcastEpisodeCommentDto
            {
                Uri = item.Uri!,
                Text = item.CommentString!,
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionCount = item.ReactionsMetadata?.NumberOfReactions ?? 0,
                ReplyCount = item.NumberOfRepliesWithThreads,
                IsPinned = item.IsPinned,
                UserReactionEmoji = item.ReactionsMetadata?.UsersReactionUnicode,
                TopReactionEmoji = topReactions,
                TopReplyAuthors = topReplyAuthors
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastEpisodeCommentReplyDto> MapCommentReplies(IReadOnlyList<CommentReplyItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastEpisodeCommentReplyDto>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSensitive || string.IsNullOrWhiteSpace(item.ReplyString) || string.IsNullOrWhiteSpace(item.Uri))
                continue;

            var topReactions = item.ReactionsMetadata?.TopReactionUnicode is { Count: > 0 } emoji
                ? emoji.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList()
                : (IReadOnlyList<string>)[];

            result.Add(new PodcastEpisodeCommentReplyDto
            {
                Uri = item.Uri!,
                Text = item.ReplyString!,
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionCount = item.ReactionsMetadata?.NumberOfReactions ?? 0,
                UserReactionEmoji = item.ReactionsMetadata?.UsersReactionUnicode,
                TopReactionEmoji = topReactions
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastCommentReactionDto> MapCommentReactions(IReadOnlyList<CommentReactionItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastCommentReactionDto>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ReactionUnicode))
                continue;

            result.Add(new PodcastCommentReactionDto
            {
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionUnicode = item.ReactionUnicode!
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastCommentReactionCountDto> MapCommentReactionCounts(
        IReadOnlyList<CommentReactionCount>? counts)
    {
        if (counts is null || counts.Count == 0)
            return [];

        return counts
            .Where(static count => !string.IsNullOrWhiteSpace(count.ReactionUnicode) && count.NumberOfReactions > 0)
            .Select(static count => new PodcastCommentReactionCountDto
            {
                ReactionUnicode = count.ReactionUnicode!,
                Count = count.NumberOfReactions
            })
            .ToList();
    }

    private static IReadOnlyList<string> DistinctNonEmpty(IEnumerable<string?>? values)
    {
        if (values is null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? BestImageUrl(IReadOnlyList<ArtistImageSource>? sources)
        => sources?
            .Where(static source => !string.IsNullOrWhiteSpace(source.Url))
            .OrderByDescending(static source => source.Width ?? source.MaxWidth ?? 0)
            .ThenByDescending(static source => source.Height ?? source.MaxHeight ?? 0)
            .Select(static source => source.Url)
            .FirstOrDefault();

    private static string BuildEpisodeShowKey(LibraryEpisodeDto episode)
        => BuildShowKey(GetEpisodeShowId(episode), GetEpisodeShowName(episode));

    private static string BuildShowKey(string? showUri, string? showName)
    {
        if (!string.IsNullOrWhiteSpace(showUri) &&
            showUri.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase))
        {
            return showUri;
        }

        var name = string.IsNullOrWhiteSpace(showName) ? "Unknown podcast" : showName.Trim();
        return "podcast:show:" + Uri.EscapeDataString(name.ToLowerInvariant());
    }

    private static string GetEpisodeShowId(LibraryEpisodeDto episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlbumId) &&
            episode.AlbumId.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase))
        {
            return episode.AlbumId;
        }

        return BuildShowKey(null, GetEpisodeShowName(episode));
    }

    private static string GetEpisodeShowName(LibraryEpisodeDto episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlbumName))
            return episode.AlbumName;

        if (!string.IsNullOrWhiteSpace(episode.ArtistName))
            return episode.ArtistName;

        return "Unknown podcast";
    }

    private static string ExtractBareId(string uri, string prefix)
    {
        while (uri.StartsWith(prefix, StringComparison.Ordinal))
            uri = uri[prefix.Length..];
        return uri;
    }

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
