using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Services.Data;

/// <summary>
/// Mock implementation of ILibraryDataService for demo/offline mode.
/// </summary>
public sealed class MockLibraryDataService : ILibraryDataService
{
    // Artist image URLs (from Last.fm CDN - verified working)
    private static readonly string[] ArtistImageUrls =
    [
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/d4a55227798912f1fd5451eaf9b719f5", // The Beatles
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/77e6137fc4456e88ab8e3974f03aa9f2", // Pink Floyd
        "https://lastfm.freetls.fastly.net/i/u/300x300/c79fc02300b24cd3cc33009ae9194b74.jpg", // Led Zeppelin
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/07358a878fa699f15f11408e0f52ae31", // Queen
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/ba7d12f78ef8b665ac46ee6169224747", // David Bowie
        "https://lastfm.freetls.fastly.net/i/u/300x300/d72dcfe345d94468c46804aed4c55f79.jpg", // Fleetwood Mac
        "https://lastfm.freetls.fastly.net/i/u/300x300/62d26c6cb4ac4bdccb8f3a2a0fd55421.jpg", // Radiohead
        "https://lastfm.freetls.fastly.net/i/u/300x300/f493a0dc48f947a999fa3a0dbe9e3c83.jpg", // Nirvana
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/74cc679ea720e70f54c03f4d1a56d3db", // The Smiths
        "https://lastfm.freetls.fastly.net/i/u/300x300/c2d70ec002c3487abd2e59e27fe6e85d.jpg", // Arcade Fire
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/112f3c3170afa8dd8138ec2cd572841d", // Talking Heads
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/9b5f1c790196498aa3bcf96eb507347a", // Joy Division
        "https://lastfm.freetls.fastly.net/i/u/300x300/7ee82efcfa740f23cc25e58acdc04d26.jpg", // The Velvet Underground
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/7da1a8e21da022e42c25414864bc434a.jpg", // Pixies
        "https://lastfm.freetls.fastly.net/i/u/300x300/e0f7efdddd044ce1a1706ca4c20f09f3.jpg", // Neutral Milk Hotel
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/3c58a04bb2fb44d9b462cbbc1e5fb38e", // My Bloody Valentine
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/9489fcb8ab7887f7d02ed463a4375062", // Sonic Youth
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/e1eac43c0cf45bf3086ee3b6858101a1", // Pavement
        "https://lastfm.freetls.fastly.net/i/u/300x300/a7f76fcb56c94a51ca3eefed472e88b4.jpg", // Television
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/ae411e2f89d748368a55c0ef36683c58", // Daft Punk
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/27c394e05d6222cc23bd4cd1806e79a3", // Kendrick Lamar
        "https://lastfm.freetls.fastly.net/i/u/300x300/d99ae43bd8df11ad42569de16452a5ee.jpg", // Frank Ocean
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/90abe434c4eae5f5b10a2873f12e58c4", // Tyler, the Creator
        "https://lastfm.freetls.fastly.net/i/u/avatar170s/a425fa059b47c1d3dd020070d4fd8037" // Tame Impala
    ];

    // Album cover image URLs (from Last.fm CDN - using 300x300 format)
    private static readonly string[] AlbumImageUrls =
    [
        "https://lastfm.freetls.fastly.net/i/u/300x300/f304ba0296794c6fc9d0e1cccd194ed0.jpg", // Abbey Road
        "https://lastfm.freetls.fastly.net/i/u/300x300/d4bdd038cacbec705e269edb0fd38419.jpg", // The Dark Side of the Moon
        "https://lastfm.freetls.fastly.net/i/u/300x300/1e6f99756d0342f891d3233ac1283d21.jpg", // Led Zeppelin IV
        "https://lastfm.freetls.fastly.net/i/u/300x300/a15e773a42182a7acfe62701d247e297.jpg", // A Night at the Opera
        "https://lastfm.freetls.fastly.net/i/u/300x300/16c9b96bf0f1edf31c210deca6d57430.jpg", // Hunky Dory
        "https://lastfm.freetls.fastly.net/i/u/300x300/349d64820e124b77cb5275ab03042693.jpg", // Rumours
        "https://lastfm.freetls.fastly.net/i/u/300x300/62d26c6cb4ac4bdccb8f3a2a0fd55421.jpg", // OK Computer
        "https://lastfm.freetls.fastly.net/i/u/300x300/e8693de0a153e609b3eaebb42d62e8be.jpg", // Nevermind
        "https://lastfm.freetls.fastly.net/i/u/300x300/827fbd1bac1d3ed232ec6c95a2526139.jpg", // The Queen Is Dead
        "https://lastfm.freetls.fastly.net/i/u/300x300/3278464e10e38a8119da1d9455681654.jpg", // Funeral
        "https://lastfm.freetls.fastly.net/i/u/300x300/909484b931449e8fc2e4fecca90b7eb5.jpg", // Remain in Light
        "https://lastfm.freetls.fastly.net/i/u/300x300/0c6c868b77a4417f937cf09506099081.jpg", // Unknown Pleasures
        "https://lastfm.freetls.fastly.net/i/u/300x300/99088f450ca5eecffdd08995d53bcf8b.jpg", // The Velvet Underground & Nico
        "https://lastfm.freetls.fastly.net/i/u/300x300/6cf55efba65b9f89db4e2754694c0b0e.jpg", // Doolittle
        "https://lastfm.freetls.fastly.net/i/u/300x300/d95051e07a714889c8f7fbbccf61bf8b.jpg", // In the Aeroplane Over the Sea
        "https://lastfm.freetls.fastly.net/i/u/300x300/510546e3b6df7504392274c528c77780.jpg", // Loveless
        "https://lastfm.freetls.fastly.net/i/u/300x300/da3687c17718278341e5d5f28a7aac74.jpg", // Daydream Nation
        "https://lastfm.freetls.fastly.net/i/u/300x300/515b7450118c4ff0b8d0a9ad2b4375ec.jpg", // Crooked Rain, Crooked Rain
        "https://lastfm.freetls.fastly.net/i/u/300x300/a7f76fcb56c94a51ca3eefed472e88b4.jpg", // Marquee Moon
        "https://lastfm.freetls.fastly.net/i/u/300x300/1340e9e1082cf0dc748583b7eefce6d5.jpg", // Discovery
        "https://lastfm.freetls.fastly.net/i/u/300x300/86b35c4eb3c479da49c915d8771bbd1a.jpg", // To Pimp a Butterfly
        "https://lastfm.freetls.fastly.net/i/u/300x300/82c92f044b27db86328ed6be3f8a735a.jpg", // Blonde
        "https://lastfm.freetls.fastly.net/i/u/300x300/e150fa362c89b8f1d92d883ae828b7ef.jpg", // IGOR
        "https://lastfm.freetls.fastly.net/i/u/300x300/dd45b0438a315aed98b5830aa2fc43c5.jpg" // Currents
    ];

    private readonly List<LibraryItemDto> _mockItems;
    private readonly List<PlaylistSummaryDto> _mockPlaylists;
    private readonly List<LibraryAlbumDto> _mockAlbums;
    private readonly Dictionary<string, List<LibraryAlbumTrackDto>> _mockAlbumTracks;
    private readonly List<LibraryArtistDto> _mockArtists;
    private readonly Dictionary<string, List<LibraryArtistTopTrackDto>> _mockArtistTopTracks;
    private readonly Dictionary<string, List<LibraryArtistAlbumDto>> _mockArtistAlbums;
    private readonly List<LikedSongDto> _mockLikedSongs;
    private readonly List<LibraryEpisodeDto> _mockYourEpisodes;
    private readonly List<LibraryEpisodeDto> _mockRecentlyPlayedPodcastEpisodes;
    private readonly List<LibraryPodcastShowDto> _mockPodcastShows;
    private readonly List<LikedSongsFilterDto> _mockLikedSongFilters;
    private readonly Dictionary<string, List<PlaylistTrackDto>> _mockPlaylistTracks;

    public event EventHandler? PlaylistsChanged;
    public event EventHandler? DataChanged;
    public event EventHandler<PodcastEpisodeProgressChangedEventArgs>? PodcastEpisodeProgressChanged;

    public void RequestSyncIfEmpty()
    {
        throw new NotImplementedException();
    }

    public MockLibraryDataService()
    {
        _mockItems = GenerateMockItems();
        _mockPlaylists = GenerateMockPlaylists();
        (_mockAlbums, _mockAlbumTracks) = GenerateMockAlbums();
        (_mockArtists, _mockArtistTopTracks, _mockArtistAlbums) = GenerateMockArtists(_mockAlbums, _mockAlbumTracks);
        _mockLikedSongs = GenerateMockLikedSongs(_mockAlbums, _mockAlbumTracks);
        _mockYourEpisodes = GenerateMockYourEpisodes();
        _mockRecentlyPlayedPodcastEpisodes = GenerateMockRecentlyPlayedPodcastEpisodes(_mockYourEpisodes);
        _mockPodcastShows = GenerateMockPodcastShows(_mockYourEpisodes);
        _mockLikedSongFilters = GenerateMockLikedSongFilters();
        _mockPlaylistTracks = GenerateMockPlaylistTracks(_mockPlaylists, _mockAlbums, _mockAlbumTracks);
    }

    public Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = new LibraryStatsDto
        {
            AlbumCount = _mockAlbums.Count,
            ArtistCount = _mockArtists.Count,
            LikedSongsCount = _mockLikedSongs.Count,
            YourEpisodesCount = _mockYourEpisodes.Count,
            PodcastCount = _mockPodcastShows.Count,
            PlaylistCount = _mockPlaylists.Count,
            TotalPlayCount = _mockItems.Sum(x => x.PlayCount)
        };
        return Task.FromResult(stats);
    }

    public Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryItemDto>>(_mockItems);
    }

    public Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default)
    {
        var recent = _mockItems
            .Where(x => x.LastPlayedAt.HasValue)
            .OrderByDescending(x => x.LastPlayedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryItemDto>>(recent);
    }

    public Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PlaylistSummaryDto>>(_mockPlaylists);
    }

    public Task<IReadOnlyList<PlaylistSummaryDto>?> TryGetUserPlaylistsFromCacheAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PlaylistSummaryDto>?>(_mockPlaylists);
    }

    public Task<IReadOnlyList<LibraryAlbumDto>> GetAlbumsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryAlbumDto>>(_mockAlbums);
    }

    public Task<IReadOnlyList<LibraryArtistDto>> GetArtistsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryArtistDto>>(_mockArtists);
    }

    public Task<IReadOnlyList<LibraryArtistTopTrackDto>> GetArtistTopTracksAsync(string artistId, CancellationToken ct = default)
    {
        if (_mockArtistTopTracks.TryGetValue(artistId, out var tracks))
        {
            return Task.FromResult<IReadOnlyList<LibraryArtistTopTrackDto>>(tracks);
        }
        return Task.FromResult<IReadOnlyList<LibraryArtistTopTrackDto>>([]);
    }

    public Task<IReadOnlyList<LibraryArtistAlbumDto>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default)
    {
        if (_mockArtistAlbums.TryGetValue(artistId, out var albums))
        {
            return Task.FromResult<IReadOnlyList<LibraryArtistAlbumDto>>(albums);
        }
        return Task.FromResult<IReadOnlyList<LibraryArtistAlbumDto>>([]);
    }

    public Task<IReadOnlyList<LikedSongDto>> GetLikedSongsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LikedSongDto>>(_mockLikedSongs);
    }

    public Task<IReadOnlyList<LibraryEpisodeDto>> GetYourEpisodesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryEpisodeDto>>(_mockYourEpisodes);
    }

    public Task<IReadOnlyList<LibraryEpisodeDto>> GetRecentlyPlayedPodcastEpisodesAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        var episodes = _mockRecentlyPlayedPodcastEpisodes
            .OrderByDescending(static episode => episode.AddedAt)
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryEpisodeDto>>(episodes);
    }

    public Task<IReadOnlyList<LibraryPodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryPodcastShowDto>>(_mockPodcastShows);
    }

    public Task<PodcastEpisodeDetailDto?> GetPodcastEpisodeDetailAsync(string episodeUri, CancellationToken ct = default)
    {
        var allPodcastEpisodes = _mockYourEpisodes
            .Concat(_mockRecentlyPlayedPodcastEpisodes)
            .GroupBy(static episode => episode.Uri, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();

        var episode = allPodcastEpisodes.FirstOrDefault(e => string.Equals(e.Uri, episodeUri, StringComparison.Ordinal));
        if (episode is null)
            return Task.FromResult<PodcastEpisodeDetailDto?>(null);

        var recommendations = allPodcastEpisodes
            .Where(e => !string.Equals(e.Uri, episodeUri, StringComparison.Ordinal))
            .Select(e => new PodcastEpisodeRecommendationDto
            {
                Uri = e.Uri,
                Title = e.Title,
                ShowName = e.AlbumName,
                ImageUrl = e.ImageUrl,
                Duration = e.Duration,
                ReleaseDate = e.ReleaseDate,
                IsExplicit = e.IsExplicit
            })
            .ToList();

        var detail = PodcastEpisodeDetailDto.FromEpisode(episode) with
        {
            Recommendations = recommendations,
            Comments =
            [
                new PodcastEpisodeCommentDto
                {
                    Uri = "spotify:comment:mock-wavee-listener",
                    AuthorName = "Wavee listener",
                    Text = "Saved this for the section on practical AI workflows.",
                    CreatedAt = DateTimeOffset.Now.AddDays(-1),
                    ReactionCount = 2,
                    TopReactionEmoji = ["\U0001F602", "\u2764\uFE0F"]
                }
            ],
            TranscriptLanguages = ["en-us"]
        };

        return Task.FromResult<PodcastEpisodeDetailDto?>(detail);
    }

    public Task<PodcastEpisodeCommentsPageDto?> GetPodcastEpisodeCommentsPageAsync(
        string episodeUri,
        string? pageToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            return Task.FromResult<PodcastEpisodeCommentsPageDto?>(new PodcastEpisodeCommentsPageDto
            {
                Items = [],
                TotalCount = 1
            });
        }

        return Task.FromResult<PodcastEpisodeCommentsPageDto?>(new PodcastEpisodeCommentsPageDto
        {
            Items =
            [
                new PodcastEpisodeCommentDto
                {
                    Uri = "spotify:comment:mock-wavee-listener",
                    AuthorName = "Wavee listener",
                    Text = "Saved this for the section on practical AI workflows.",
                    CreatedAt = DateTimeOffset.Now.AddDays(-1),
                    ReactionCount = 2,
                    TopReactionEmoji = ["\U0001F602", "\u2764\uFE0F"]
                }
            ],
            TotalCount = 1
        });
    }

    public Task<PodcastCommentRepliesPageDto?> GetPodcastCommentRepliesAsync(
        string commentUri,
        string? pageToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<PodcastCommentRepliesPageDto?>(new PodcastCommentRepliesPageDto
        {
            Items = [],
            TotalCount = 0
        });
    }

    public Task<PodcastCommentReactionsPageDto?> GetPodcastCommentReactionsAsync(
        string uri,
        string? pageToken,
        string? reactionUnicode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ct.ThrowIfCancellationRequested();

        var all = new List<PodcastCommentReactionDto>
        {
            new()
            {
                AuthorName = "Maya",
                CreatedAt = DateTimeOffset.Now.AddMinutes(-4),
                ReactionUnicode = "\U0001F602"
            },
            new()
            {
                AuthorName = "Jon",
                CreatedAt = DateTimeOffset.Now.AddMinutes(-12),
                ReactionUnicode = "\u2764\uFE0F"
            },
            new()
            {
                AuthorName = "Priya",
                CreatedAt = DateTimeOffset.Now.AddHours(-1),
                ReactionUnicode = "\U0001F44D"
            }
        };

        var filtered = string.IsNullOrWhiteSpace(reactionUnicode)
            ? all
            : all.Where(reaction => string.Equals(reaction.ReactionUnicode, reactionUnicode, StringComparison.Ordinal)).ToList();

        return Task.FromResult<PodcastCommentReactionsPageDto?>(new PodcastCommentReactionsPageDto
        {
            Items = string.IsNullOrWhiteSpace(pageToken) ? filtered : [],
            ReactionCounts =
            [
                new() { ReactionUnicode = "\U0001F602", Count = 1 },
                new() { ReactionUnicode = "\u2764\uFE0F", Count = 1 },
                new() { ReactionUnicode = "\U0001F44D", Count = 1 }
            ]
        });
    }

    public Task<PodcastEpisodeCommentReplyDto> CreatePodcastCommentReplyAsync(
        string commentUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new PodcastEpisodeCommentReplyDto
        {
            Uri = $"wavee:reply:{Guid.NewGuid():N}",
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
        return Task.CompletedTask;
    }

    public Task<PodcastEpisodeProgressDto?> GetPodcastEpisodeProgressAsync(
        string episodeUri,
        CancellationToken ct = default)
    {
        var episode = _mockYourEpisodes
            .Concat(_mockRecentlyPlayedPodcastEpisodes)
            .GroupBy(static e => e.Uri, StringComparer.Ordinal)
            .Select(static group => group.First())
            .FirstOrDefault(e => string.Equals(e.Uri, episodeUri, StringComparison.Ordinal));
        if (episode is null)
            return Task.FromResult<PodcastEpisodeProgressDto?>(null);

        return Task.FromResult<PodcastEpisodeProgressDto?>(new PodcastEpisodeProgressDto
        {
            Uri = episode.Uri,
            PlayedPosition = episode.PlayedPosition,
            PlayedState = episode.PlayedState,
            Duration = episode.Duration,
            UpdatedAt = DateTimeOffset.Now.AddHours(-1)
        });
    }

    public Task SavePodcastEpisodeProgressAsync(
        string episodeUri,
        TimeSpan? resumePosition,
        bool completed,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var position = completed ? TimeSpan.Zero : resumePosition ?? TimeSpan.Zero;
        var progress = new PodcastEpisodeProgressDto
        {
            Uri = episodeUri,
            PlayedPosition = position,
            PlayedState = completed
                ? "COMPLETED"
                : position > TimeSpan.Zero
                    ? "IN_PROGRESS"
                    : "NOT_STARTED",
            UpdatedAt = DateTimeOffset.Now
        };
        PodcastEpisodeProgressChanged?.Invoke(this, new PodcastEpisodeProgressChangedEventArgs(progress));
        return Task.CompletedTask;
    }

    public Task<PodcastEpisodeCommentDto> CreatePodcastEpisodeCommentAsync(
        string episodeUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new PodcastEpisodeCommentDto
        {
            Uri = $"wavee:comment:{Guid.NewGuid():N}",
            AuthorName = "You",
            Text = text.Trim(),
            CreatedAt = DateTimeOffset.Now
        });
    }

    public Task<IReadOnlyList<LikedSongsFilterDto>> GetLikedSongFiltersAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LikedSongsFilterDto>>(_mockLikedSongFilters);
    }

    public Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default)
    {
        var playlistId = $"spotify:playlist:{Guid.NewGuid():N}";

        var playlist = new PlaylistSummaryDto
        {
            Id = playlistId,
            Name = name,
            TrackCount = trackIds?.Count ?? 0,
            IsOwner = true
        };

        _mockPlaylists.Insert(0, playlist);

        // Add tracks to the playlist if provided
        if (trackIds != null && trackIds.Count > 0)
        {
            var tracks = new List<PlaylistTrackDto>();
            var now = DateTime.Now;

            foreach (var trackId in trackIds)
            {
                // Look up track from liked songs
                var likedSong = _mockLikedSongs.FirstOrDefault(s => s.Id == trackId);
                if (likedSong != null)
                {
                    tracks.Add(new PlaylistTrackDto
                    {
                        Id = likedSong.Id,
                        Uri = likedSong.Uri,
                        Title = likedSong.Title,
                        ArtistName = likedSong.ArtistName,
                        ArtistId = likedSong.ArtistId,
                        AlbumName = likedSong.AlbumName,
                        AlbumId = likedSong.AlbumId,
                        ImageUrl = likedSong.ImageUrl,
                        ImageSmallUrl = likedSong.ImageUrl,
                        Duration = likedSong.Duration,
                        AddedAt = now,
                        AddedBy = "You",
                        IsExplicit = likedSong.IsExplicit
                    });
                }
            }

            _mockPlaylistTracks[playlistId] = tracks;
        }

        PlaylistsChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(playlist);
    }

    public Task<PlaylistSummaryDto> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        var folderId = $"spotify:folder:{Guid.NewGuid():N}";

        var folder = new PlaylistSummaryDto
        {
            Id = folderId,
            Name = name,
            TrackCount = 0,
            IsOwner = true
        };

        _mockPlaylists.Insert(0, folder);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(folder);
    }

    public Task<PlaylistDetailDto> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var summary = _mockPlaylists.FirstOrDefault(p => p.Id == playlistId);
        if (summary == null)
        {
            return Task.FromResult(new PlaylistDetailDto
            {
                Id = playlistId,
                Name = "Unknown Playlist",
                OwnerName = "Unknown"
            });
        }

        var trackCount = _mockPlaylistTracks.TryGetValue(playlistId, out var tracks) ? tracks.Count : 0;

        var basePermission = summary.IsOwner
            ? PlaylistBasePermission.Owner
            : PlaylistBasePermission.Viewer;
        var capabilities = new PlaylistCapabilitiesDto
        {
            CanView = true,
            CanEditItems = summary.IsOwner,
            CanAdministratePermissions = summary.IsOwner,
            CanCancelMembership = !summary.IsOwner,
            CanAbuseReport = !summary.IsOwner
        };

        var detail = new PlaylistDetailDto
        {
            Id = summary.Id,
            Name = summary.Name,
            Description = GetPlaylistDescription(summary.Name),
            ImageUrl = summary.ImageUrl,
            OwnerName = summary.IsOwner ? "You" : "Spotify",
            OwnerId = summary.IsOwner ? "user:me" : "spotify:user:spotify",
            TrackCount = trackCount,
            FollowerCount = summary.IsOwner ? 0 : 1_250_000,
            IsOwner = summary.IsOwner,
            IsCollaborative = false,
            IsPublic = !summary.IsOwner,
            BasePermission = basePermission,
            Capabilities = capabilities
        };

        return Task.FromResult(detail);
    }

    public Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        if (_mockPlaylistTracks.TryGetValue(playlistId, out var tracks))
        {
            return Task.FromResult<IReadOnlyList<PlaylistTrackDto>>(tracks);
        }
        return Task.FromResult<IReadOnlyList<PlaylistTrackDto>>([]);
    }

    public Task<long> GetPlaylistFollowerCountAsync(string playlistId, CancellationToken ct = default)
        => Task.FromResult(0L);

    public Task SetPlaylistFollowedAsync(string playlistId, bool followed, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<AlbumPalette?> GetPlaylistPaletteAsync(string playlistId, CancellationToken ct = default)
        => Task.FromResult<AlbumPalette?>(null);

    public Task AddLocalTracksToPlaylistAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveLocalOverlayTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<Wavee.UI.WinUI.Data.DTOs.PlaylistOverlayRow>> GetPlaylistOverlayRowsAsync(string playlistUri, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Wavee.UI.WinUI.Data.DTOs.PlaylistOverlayRow>>(Array.Empty<Wavee.UI.WinUI.Data.DTOs.PlaylistOverlayRow>());

    public Task ReorderPlaylistOverlayAsync(string playlistUri, IReadOnlyList<string> orderedTrackUris, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        // Mock can't synthesize full PlaylistTrackDto rows for arbitrary track
        // IDs; bump the summary count so callers see "something happened" and
        // the playlist refreshes its track list from upstream on next nav.
        var summary = _mockPlaylists.FirstOrDefault(p => p.Id == playlistId);
        if (summary != null && trackIds is { Count: > 0 })
        {
            var index = _mockPlaylists.IndexOf(summary);
            _mockPlaylists[index] = summary with { TrackCount = summary.TrackCount + trackIds.Count };
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecommendedTrackResult>> GetPlaylistRecommendationsAsync(string playlistUri, IReadOnlyList<string>? skipUris = null, int numResults = 20,
        CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        if (_mockPlaylistTracks.TryGetValue(playlistId, out var tracks))
        {
            var trackIdSet = trackIds.ToHashSet();
            tracks.RemoveAll(t => trackIdSet.Contains(t.Id));

            // Update the summary track count
            var summary = _mockPlaylists.FirstOrDefault(p => p.Id == playlistId);
            if (summary != null)
            {
                var index = _mockPlaylists.IndexOf(summary);
                _mockPlaylists[index] = summary with { TrackCount = tracks.Count };
            }

            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdatePlaylistDescriptionAsync(string playlistId, string description, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdatePlaylistCoverAsync(string playlistId, byte[] jpegBytes, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemovePlaylistCoverAsync(string playlistId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetPlaylistCollaborativeAsync(string playlistId, bool collaborative, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<PlaylistMemberResult>> GetPlaylistMembersAsync(string playlistId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlaylistMemberResult>>(Array.Empty<PlaylistMemberResult>());

    public Task SetPlaylistMemberRoleAsync(string playlistId, string memberUserId, PlaylistMemberRole role, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemovePlaylistMemberAsync(string playlistId, string memberUserId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<PlaylistInviteLink> CreatePlaylistInviteLinkAsync(string playlistId, PlaylistMemberRole grantedRole, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult(new PlaylistInviteLink
        {
            Token = "mock",
            ShareUrl = $"https://open.spotify.com/playlist/{playlistId}?pt=mock",
            CreatedAt = DateTimeOffset.UtcNow,
            Ttl = ttl,
            GrantedRole = grantedRole
        });

    private static string? GetPlaylistDescription(string name) => name switch
    {
        "Favorites" => "My all-time favorite tracks",
        "Chill Vibes" => "Relaxing tunes for winding down",
        "Workout Mix" => "High energy tracks to keep you moving",
        "Discover Weekly" => "Your weekly mixtape of fresh music. Enjoy new discoveries and deep cuts chosen just for you.",
        _ => null
    };

    private static List<LibraryItemDto> GenerateMockItems()
    {
        var now = DateTimeOffset.UtcNow;
        var artists = new[] { "The Beatles", "Pink Floyd", "Led Zeppelin", "Queen", "David Bowie" };
        var albums = new[] { "Abbey Road", "The Dark Side of the Moon", "Led Zeppelin IV", "A Night at the Opera", "Hunky Dory" };

        var items = new List<LibraryItemDto>();
        for (int i = 1; i <= 50; i++)
        {
            items.Add(new LibraryItemDto
            {
                Id = $"spotify:track:{i}",
                Title = $"Track {i}",
                Artist = artists[(i - 1) % artists.Length],
                Album = albums[(i - 1) % albums.Length],
                ImageUrl = null,
                Duration = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(i * 7 % 120),
                PlayCount = (50 - i) * 2,
                LastPlayedAt = i <= 20 ? now.AddHours(-i * 3) : null,
                AddedAt = now.AddDays(-i)
            });
        }
        return items;
    }

    private static List<PlaylistSummaryDto> GenerateMockPlaylists()
    {
        return
        [
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:1",
                Name = "Favorites",
                TrackCount = 25,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:2",
                Name = "Chill Vibes",
                TrackCount = 42,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:3",
                Name = "Workout Mix",
                TrackCount = 18,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:4",
                Name = "Discover Weekly",
                TrackCount = 30,
                IsOwner = false
            }
        ];
    }

    private static (List<LibraryAlbumDto>, Dictionary<string, List<LibraryAlbumTrackDto>>) GenerateMockAlbums()
    {
        var now = DateTimeOffset.UtcNow;
        var albums = new List<LibraryAlbumDto>();
        var albumTracks = new Dictionary<string, List<LibraryAlbumTrackDto>>();

        var albumData = new[]
        {
            ("The Beatles", "Abbey Road", 1969, new[] { "Come Together", "Something", "Maxwell's Silver Hammer", "Oh! Darling", "Octopus's Garden", "I Want You (She's So Heavy)", "Here Comes the Sun", "Because", "You Never Give Me Your Money", "Sun King", "Mean Mr. Mustard", "Polythene Pam", "She Came In Through the Bathroom Window", "Golden Slumbers", "Carry That Weight", "The End", "Her Majesty" }),
            ("Pink Floyd", "The Dark Side of the Moon", 1973, new[] { "Speak to Me", "Breathe", "On the Run", "Time", "The Great Gig in the Sky", "Money", "Us and Them", "Any Colour You Like", "Brain Damage", "Eclipse" }),
            ("Led Zeppelin", "Led Zeppelin IV", 1971, new[] { "Black Dog", "Rock and Roll", "The Battle of Evermore", "Stairway to Heaven", "Misty Mountain Hop", "Four Sticks", "Going to California", "When the Levee Breaks" }),
            ("Queen", "A Night at the Opera", 1975, new[] { "Death on Two Legs", "Lazing on a Sunday Afternoon", "I'm in Love with My Car", "You're My Best Friend", "'39", "Sweet Lady", "Seaside Rendezvous", "The Prophet's Song", "Love of My Life", "Good Company", "Bohemian Rhapsody", "God Save the Queen" }),
            ("David Bowie", "Hunky Dory", 1971, new[] { "Changes", "Oh! You Pretty Things", "Eight Line Poem", "Life on Mars?", "Kooks", "Quicksand", "Fill Your Heart", "Andy Warhol", "Song for Bob Dylan", "Queen Bitch", "The Bewlay Brothers" }),
            ("Fleetwood Mac", "Rumours", 1977, new[] { "Second Hand News", "Dreams", "Never Going Back Again", "Don't Stop", "Go Your Own Way", "Songbird", "The Chain", "You Make Loving Fun", "I Don't Want to Know", "Oh Daddy", "Gold Dust Woman" }),
            ("Radiohead", "OK Computer", 1997, new[] { "Airbag", "Paranoid Android", "Subterranean Homesick Alien", "Exit Music (For a Film)", "Let Down", "Karma Police", "Fitter Happier", "Electioneering", "Climbing Up the Walls", "No Surprises", "Lucky", "The Tourist" }),
            ("Nirvana", "Nevermind", 1991, new[] { "Smells Like Teen Spirit", "In Bloom", "Come as You Are", "Breed", "Lithium", "Polly", "Territorial Pissings", "Drain You", "Lounge Act", "Stay Away", "On a Plain", "Something in the Way" }),
            ("The Smiths", "The Queen Is Dead", 1986, new[] { "The Queen Is Dead", "Frankly, Mr. Shankly", "I Know It's Over", "Never Had No One Ever", "Cemetry Gates", "Bigmouth Strikes Again", "The Boy with the Thorn in His Side", "Vicar in a Tutu", "There Is a Light That Never Goes Out", "Some Girls Are Bigger Than Others" }),
            ("Arcade Fire", "Funeral", 2004, new[] { "Neighborhood #1 (Tunnels)", "Neighborhood #2 (Laïka)", "Une Année Sans Lumière", "Neighborhood #3 (Power Out)", "Neighborhood #4 (7 Kettles)", "Crown of Love", "Wake Up", "Haïti", "Rebellion (Lies)", "In the Backseat" }),
            ("Talking Heads", "Remain in Light", 1980, new[] { "Born Under Punches", "Crosseyed and Painless", "The Great Curve", "Once in a Lifetime", "Houses in Motion", "Seen and Not Seen", "Listening Wind", "The Overload" }),
            ("Joy Division", "Unknown Pleasures", 1979, new[] { "Disorder", "Day of the Lords", "Candidate", "Insight", "New Dawn Fades", "She's Lost Control", "Shadowplay", "Wilderness", "Interzone", "I Remember Nothing" }),
            ("The Velvet Underground", "The Velvet Underground & Nico", 1967, new[] { "Sunday Morning", "I'm Waiting for the Man", "Femme Fatale", "Venus in Furs", "Run Run Run", "All Tomorrow's Parties", "Heroin", "There She Goes Again", "I'll Be Your Mirror", "The Black Angel's Death Song", "European Son" }),
            ("Pixies", "Doolittle", 1989, new[] { "Debaser", "Tame", "Wave of Mutilation", "I Bleed", "Here Comes Your Man", "Dead", "Monkey Gone to Heaven", "Mr. Grieves", "Crackity Jones", "La La Love You", "No. 13 Baby", "There Goes My Gun", "Hey", "Silver", "Gouge Away" }),
            ("Neutral Milk Hotel", "In the Aeroplane Over the Sea", 1998, new[] { "The King of Carrot Flowers Pt. One", "The King of Carrot Flowers Pts. Two & Three", "In the Aeroplane Over the Sea", "Two-Headed Boy", "The Fool", "Holland, 1945", "Communist Daughter", "Oh Comely", "Ghost", "Untitled", "Two-Headed Boy Pt. Two" }),
            ("My Bloody Valentine", "Loveless", 1991, new[] { "Only Shallow", "Loomer", "Touched", "To Here Knows When", "When You Sleep", "I Only Said", "Come in Alone", "Sometimes", "Blown a Wish", "What You Want", "Soon" }),
            ("Sonic Youth", "Daydream Nation", 1988, new[] { "'Teen Age Riot", "Silver Rocket", "The Sprawl", "Cross the Breeze", "Eric's Trip", "Total Trash", "Hey Joni", "Providence", "Candle", "Rain King", "Kissability", "Trilogy: The Wonder / Hyperstation / Eliminator Jr." }),
            ("Pavement", "Crooked Rain, Crooked Rain", 1994, new[] { "Silence Kit", "Elevate Me Later", "Stop Breathin", "Cut Your Hair", "Newark Wilder", "Unfair", "Gold Soundz", "5-4=Unity", "Range Life", "Heaven Is a Truck", "Hit the Plane Down", "Fillmore Jive" }),
            ("Television", "Marquee Moon", 1977, new[] { "See No Evil", "Venus", "Friction", "Marquee Moon", "Elevation", "Guiding Light", "Prove It", "Torn Curtain" }),
            ("Daft Punk", "Discovery", 2001, new[] { "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger", "Crescendolls", "Nightvision", "Superheroes", "High Life", "Something About Us", "Voyager", "Veridis Quo", "Short Circuit", "Face to Face", "Too Long" }),
            ("Kendrick Lamar", "To Pimp a Butterfly", 2015, new[] { "Wesley's Theory", "For Free? (Interlude)", "King Kunta", "Institutionalized", "These Walls", "u", "Alright", "For Sale? (Interlude)", "Momma", "Hood Politics", "How Much a Dollar Cost", "Complexion (A Zulu Love)", "The Blacker the Berry", "You Ain't Gotta Lie (Momma Said)", "i", "Mortal Man" }),
            ("Frank Ocean", "Blonde", 2016, new[] { "Nikes", "Ivy", "Pink + White", "Be Yourself", "Solo", "Skyline To", "Self Control", "Good Guy", "Nights", "Solo (Reprise)", "Pretty Sweet", "Facebook Story", "Close to You", "White Ferrari", "Seigfried", "Godspeed", "Futura Free" }),
            ("Tyler, the Creator", "IGOR", 2019, new[] { "IGOR'S THEME", "EARFQUAKE", "I THINK", "EXACTLY WHAT YOU RUN FROM YOU END UP CHASING", "RUNNING OUT OF TIME", "NEW MAGIC WAND", "A BOY IS A GUN*", "PUPPET", "WHAT'S GOOD", "GONE, GONE / THANK YOU", "I DON'T LOVE YOU ANYMORE", "ARE WE STILL FRIENDS?" }),
            ("Tame Impala", "Currents", 2015, new[] { "Let It Happen", "Nangs", "The Moment", "Yes I'm Changing", "Eventually", "Gossip", "The Less I Know the Better", "Past Life", "Disciples", "Cause I'm a Man", "'Cause I'm a Man", "Reality in Motion", "Love/Paranoia", "New Person, Same Old Mistakes" })
        };

        for (int i = 0; i < albumData.Length; i++)
        {
            var (artist, albumName, year, trackNames) = albumData[i];
            var albumId = $"spotify:album:{i + 1}";

            albums.Add(new LibraryAlbumDto
            {
                Id = albumId,
                Name = albumName,
                ArtistName = artist,
                ArtistId = $"spotify:artist:{i + 1}",
                ImageUrl = i < AlbumImageUrls.Length ? AlbumImageUrls[i] : null,
                Year = year,
                TrackCount = trackNames.Length,
                AddedAt = now.AddDays(-i * 3)
            });

            var tracks = new List<LibraryAlbumTrackDto>();
            for (int t = 0; t < trackNames.Length; t++)
            {
                tracks.Add(new LibraryAlbumTrackDto
                {
                    Id = $"spotify:track:{albumId}:{t + 1}",
                    TrackNumber = t + 1,
                    Title = trackNames[t],
                    Duration = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds((t * 17) % 180),
                    IsExplicit = t % 7 == 0
                });
            }
            albumTracks[albumId] = tracks;
        }

        return (albums, albumTracks);
    }

    private static (List<LibraryArtistDto>, Dictionary<string, List<LibraryArtistTopTrackDto>>, Dictionary<string, List<LibraryArtistAlbumDto>>) GenerateMockArtists(
        List<LibraryAlbumDto> albums,
        Dictionary<string, List<LibraryAlbumTrackDto>> albumTracks)
    {
        var now = DateTimeOffset.UtcNow;
        var random = new Random(42); // Fixed seed for consistent data

        // Extract unique artists from albums
        var artistsData = albums
            .GroupBy(a => a.ArtistName)
            .Select((g, index) => new
            {
                Name = g.Key,
                Id = g.First().ArtistId ?? $"spotify:artist:{index + 1}",
                Albums = g.ToList()
            })
            .ToList();

        var artists = new List<LibraryArtistDto>();
        var artistTopTracks = new Dictionary<string, List<LibraryArtistTopTrackDto>>();
        var artistAlbums = new Dictionary<string, List<LibraryArtistAlbumDto>>();

        // Follower counts for variety
        var followerCounts = new[] { 45_000_000, 32_000_000, 18_500_000, 12_000_000, 8_700_000, 5_200_000, 3_100_000, 2_400_000, 1_800_000, 950_000, 720_000, 480_000, 350_000, 220_000, 180_000, 145_000, 98_000, 67_000, 42_000, 25_000, 15_000, 8_500, 4_200, 1_800 };

        for (int i = 0; i < artistsData.Count; i++)
        {
            var artistData = artistsData[i];

            artists.Add(new LibraryArtistDto
            {
                Id = artistData.Id,
                Name = artistData.Name,
                ImageUrl = i < ArtistImageUrls.Length ? ArtistImageUrls[i] : null,
                FollowerCount = followerCounts[i % followerCounts.Length],
                AlbumCount = artistData.Albums.Count,
                AddedAt = now.AddDays(-i * 5)
            });

            // Generate top tracks from their albums
            var topTracks = new List<LibraryArtistTopTrackDto>();
            var trackIndex = 1;
            var playCounts = new long[] { 2_500_000_000, 1_800_000_000, 1_200_000_000, 850_000_000, 620_000_000, 480_000_000, 350_000_000, 220_000_000, 150_000_000, 95_000_000 };

            // For first artist, ensure we have at least 15 tracks to test pagination
            var minTracks = i == 0 ? 15 : 5;

            foreach (var album in artistData.Albums)
            {
                if (albumTracks.TryGetValue(album.Id, out var tracks))
                {
                    // Take first 3-5 tracks from each album as "top tracks"
                    var tracksToTake = Math.Min(tracks.Count, 3 + random.Next(3));
                    foreach (var track in tracks.Take(tracksToTake))
                    {
                        if (topTracks.Count >= minTracks) break;

                        topTracks.Add(new LibraryArtistTopTrackDto
                        {
                            Id = track.Id,
                            Index = trackIndex++,
                            Title = track.Title,
                            AlbumName = album.Name,
                            AlbumImageUrl = album.ImageUrl,
                            Duration = track.Duration,
                            PlayCount = playCounts[Math.Min(topTracks.Count, playCounts.Length - 1)],
                            IsExplicit = track.IsExplicit
                        });
                    }
                }
                if (topTracks.Count >= minTracks) break;
            }

            // If first artist doesn't have enough tracks, add synthetic ones to test pagination
            if (i == 0 && topTracks.Count < 15)
            {
                var firstAlbum = artistData.Albums.FirstOrDefault();
                var syntheticTrackNames = new[] { "Hey Jude", "Let It Be", "Yesterday", "Come Together", "Here Comes the Sun", "Something", "While My Guitar Gently Weeps", "A Day in the Life", "Strawberry Fields Forever", "Penny Lane", "Eleanor Rigby", "Help!", "Twist and Shout", "I Want to Hold Your Hand", "She Loves You" };
                while (topTracks.Count < 15)
                {
                    var idx = topTracks.Count;
                    topTracks.Add(new LibraryArtistTopTrackDto
                    {
                        Id = $"spotify:track:synthetic{idx}",
                        Index = trackIndex++,
                        Title = syntheticTrackNames[idx % syntheticTrackNames.Length],
                        AlbumName = firstAlbum?.Name ?? "Greatest Hits",
                        AlbumImageUrl = firstAlbum?.ImageUrl ?? AlbumImageUrls[0],
                        Duration = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(random.Next(60)),
                        PlayCount = playCounts[Math.Min(idx, playCounts.Length - 1)] / (idx + 1),
                        IsExplicit = false
                    });
                }
            }

            artistTopTracks[artistData.Id] = topTracks;

            // Generate album list for artist
            var albumTypes = new[] { "Album", "Album", "Album", "Single", "EP" };
            artistAlbums[artistData.Id] = artistData.Albums
                .Select((a, idx) => new LibraryArtistAlbumDto
                {
                    Id = a.Id,
                    Name = a.Name,
                    ImageUrl = a.ImageUrl,
                    Year = a.Year,
                    AlbumType = albumTypes[idx % albumTypes.Length]
                })
                .ToList();
        }

        return (artists, artistTopTracks, artistAlbums);
    }

    private static List<LikedSongDto> GenerateMockLikedSongs(
        List<LibraryAlbumDto> albums,
        Dictionary<string, List<LibraryAlbumTrackDto>> albumTracks)
    {
        var now = DateTime.Now;
        var likedSongs = new List<LikedSongDto>();
        var random = new Random(123); // Fixed seed for consistent data
        var tagPool = new[]
        {
            "pop",
            "pop rock",
            "chill",
            "electronic",
            "romantic",
            "soundtrack",
            "cozy",
            "emotional"
        };

        foreach (var album in albums)
        {
            if (!albumTracks.TryGetValue(album.Id, out var tracks)) continue;

            // Like a random subset of tracks from each album
            var tracksToLike = tracks
                .Where(_ => random.NextDouble() > 0.4) // ~60% of tracks
                .ToList();

            foreach (var track in tracksToLike)
            {
                likedSongs.Add(new LikedSongDto
                {
                    Id = track.Id,
                    Uri = $"spotify:track:{track.Id}",
                    Title = track.Title,
                    ArtistName = album.ArtistName,
                    ArtistId = album.ArtistId ?? $"spotify:artist:{album.ArtistName.GetHashCode()}",
                    AlbumName = album.Name,
                    AlbumId = album.Id,
                    ImageUrl = album.ImageUrl,
                    ImageSmallUrl = album.ImageUrl,
                    Duration = track.Duration,
                    AddedAt = now.AddDays(-random.Next(1, 365)).AddHours(-random.Next(0, 24)),
                    IsExplicit = track.IsExplicit,
                    Tags =
                    [
                        tagPool[Math.Abs(track.Title.GetHashCode()) % tagPool.Length],
                        tagPool[Math.Abs(album.Name.GetHashCode()) % tagPool.Length]
                    ]
                });
            }
        }

        // Sort by AddedAt descending (most recent first)
        return likedSongs.OrderByDescending(s => s.AddedAt).ToList();
    }

    private static List<LibraryEpisodeDto> GenerateMockYourEpisodes()
    {
        var now = DateTime.Now;
        var episodes = new List<LibraryEpisodeDto>
        {
            new()
            {
                Id = "44EvzGeNJfNFKDlqnoLxRr",
                Uri = "spotify:episode:44EvzGeNJfNFKDlqnoLxRr",
                Title = "Inside MCP: How AI Agents Are Learning to Talk to Each Other",
                ArtistName = "The Brave Technologist",
                ArtistId = "",
                AlbumName = "The Brave Technologist",
                AlbumId = "spotify:show:38FtgGDbuNlSsBgpRdeMah",
                ImageUrl = "https://i.scdn.co/image/ab67656300005f1f3894f3993d88d77b0ab2ab72",
                Description = "Andy Maskin explains why brands are shifting from SEO to AI visibility and how cleaner data changes agentic AI outcomes.",
                ReleaseDate = DateTimeOffset.Now.AddDays(-8),
                ShareUrl = "https://open.spotify.com/episode/44EvzGeNJfNFKDlqnoLxRr",
                PreviewUrl = "https://p.scdn.co/mp3-preview/28e627561d00849b8292750815b5831d9a1bd93f.mp3",
                MediaTypes = ["AUDIO"],
                Duration = TimeSpan.FromMinutes(33),
                AddedAt = now.AddDays(-2),
                IsExplicit = false,
                IsPlayable = true,
                OriginalIndex = 1,
                IsLiked = true
            },
            new()
            {
                Id = "mock-episode-2",
                Uri = "spotify:episode:mock-episode-2",
                Title = "Designing Better Developer Tools",
                ArtistName = "Wavee Labs",
                ArtistId = "",
                AlbumName = "Product Notes",
                AlbumId = "spotify:show:mock-product-notes",
                ImageUrl = "https://misc.spotifycdn.com/your-episodes/SE-300.png",
                Description = "A practical discussion about designing developer tools that stay fast under repeated daily use.",
                ReleaseDate = DateTimeOffset.Now.AddDays(-14),
                MediaTypes = ["AUDIO"],
                Duration = TimeSpan.FromMinutes(42),
                AddedAt = now.AddDays(-9),
                IsExplicit = false,
                IsPlayable = true,
                OriginalIndex = 2,
                IsLiked = true
            }
        };

        episodes[0].ApplyPlaybackProgress(TimeSpan.FromMinutes(32).Add(TimeSpan.FromSeconds(45)), "IN_PROGRESS");
        episodes[1].ApplyPlaybackProgress(TimeSpan.Zero, "NOT_STARTED");
        return episodes;
    }

    private static List<LibraryEpisodeDto> GenerateMockRecentlyPlayedPodcastEpisodes(IReadOnlyList<LibraryEpisodeDto> savedEpisodes)
    {
        var now = DateTime.Now;
        var savedRecent = savedEpisodes[0] with
        {
            AddedAt = now.AddMinutes(-12),
            OriginalIndex = 1
        };
        savedRecent.ApplyPlaybackProgress(TimeSpan.FromMinutes(32).Add(TimeSpan.FromSeconds(45)), "IN_PROGRESS");

        var unsavedProgress = new LibraryEpisodeDto
        {
            Id = "mock-recent-unsaved-1",
            Uri = "spotify:episode:mock-recent-unsaved-1",
            Title = "Why Clean Data Makes Agents Useful",
            ArtistName = "Applied AI Notes",
            ArtistId = "",
            AlbumName = "Applied AI Notes",
            AlbumId = "spotify:show:mock-applied-ai-notes",
            ImageUrl = "https://misc.spotifycdn.com/your-episodes/SE-300.png",
            Description = "A recently played episode that is not saved and belongs to a show the user does not follow.",
            ReleaseDate = DateTimeOffset.Now.AddDays(-4),
            MediaTypes = ["AUDIO"],
            Duration = TimeSpan.FromMinutes(38),
            AddedAt = now.AddHours(-3),
            IsExplicit = false,
            IsPlayable = true,
            OriginalIndex = 2,
            IsLiked = false
        };
        unsavedProgress.ApplyPlaybackProgress(TimeSpan.FromMinutes(14), "IN_PROGRESS");

        var unsavedUnplayed = new LibraryEpisodeDto
        {
            Id = "mock-recent-unsaved-2",
            Uri = "spotify:episode:mock-recent-unsaved-2",
            Title = "A New Season Trailer",
            ArtistName = "Release Radar Stories",
            ArtistId = "",
            AlbumName = "Release Radar Stories",
            AlbumId = "spotify:show:mock-empty-followed",
            ImageUrl = "https://misc.spotifycdn.com/your-episodes/SE-300.png",
            Description = "A zero-position Herodotus row, shown as unplayed.",
            ReleaseDate = DateTimeOffset.Now.AddDays(-1),
            MediaTypes = ["AUDIO"],
            Duration = TimeSpan.FromMinutes(2),
            AddedAt = now.AddHours(-8),
            IsExplicit = false,
            IsPlayable = true,
            OriginalIndex = 3,
            IsLiked = false
        };
        unsavedUnplayed.ApplyPlaybackProgress(TimeSpan.Zero, "NOT_STARTED");

        return new List<LibraryEpisodeDto>
        {
            savedRecent,
            unsavedProgress,
            unsavedUnplayed
        };
    }

    private static List<LibraryPodcastShowDto> GenerateMockPodcastShows(IReadOnlyList<LibraryEpisodeDto> episodes)
    {
        var episodeShows = episodes
            .GroupBy(e => string.IsNullOrWhiteSpace(e.AlbumId) ? e.AlbumName : e.AlbumId)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(e => e.AddedAt).ToList();
                var first = ordered[0];
                return new LibraryPodcastShowDto
                {
                    Id = string.IsNullOrWhiteSpace(first.AlbumId)
                        ? $"podcast:show:{Uri.EscapeDataString(first.AlbumName.ToLowerInvariant())}"
                        : first.AlbumId,
                    Name = string.IsNullOrWhiteSpace(first.AlbumName) ? first.ArtistName : first.AlbumName,
                    Publisher = first.ArtistName,
                    ImageUrl = first.ImageUrl,
                    EpisodeCount = ordered.Count,
                    SavedEpisodeCount = ordered.Count,
                    AddedAt = ordered.Min(e => e.AddedAt),
                    LastEpisodeAddedAt = ordered[0].AddedAt,
                    IsFollowed = true
                };
            });

        return episodeShows
            .Append(new LibraryPodcastShowDto
            {
                Id = "spotify:show:mock-empty-followed",
                Name = "Release Radar Stories",
                Publisher = "Spotify",
                Description = "A followed podcast with no saved episodes yet.",
                ImageUrl = "https://misc.spotifycdn.com/your-episodes/SE-300.png",
                EpisodeCount = 18,
                SavedEpisodeCount = 0,
                AddedAt = DateTime.Now.AddDays(-21),
                IsFollowed = true
            })
            .OrderByDescending(show => show.SortDate)
            .ThenBy(show => show.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LikedSongsFilterDto> GenerateMockLikedSongFilters()
    {
        return
        [
            new() { Title = "Pop", Query = "tags contains pop", TagValue = "pop" },
            new() { Title = "Pop Rock", Query = "tags contains pop rock", TagValue = "pop rock" },
            new() { Title = "Chill", Query = "tags contains chill", TagValue = "chill" },
            new() { Title = "Electronic", Query = "tags contains electronic", TagValue = "electronic" },
            new() { Title = "Romantic", Query = "tags contains romantic", TagValue = "romantic" },
            new() { Title = "Soundtrack", Query = "tags contains soundtrack", TagValue = "soundtrack" },
            new() { Title = "Cozy", Query = "tags contains cozy", TagValue = "cozy" },
            new() { Title = "Emotional", Query = "tags contains emotional", TagValue = "emotional" }
        ];
    }

    private static Dictionary<string, List<PlaylistTrackDto>> GenerateMockPlaylistTracks(
        List<PlaylistSummaryDto> playlists,
        List<LibraryAlbumDto> albums,
        Dictionary<string, List<LibraryAlbumTrackDto>> albumTracks)
    {
        var now = DateTime.Now;
        var random = new Random(456); // Fixed seed for consistent data
        var playlistTracks = new Dictionary<string, List<PlaylistTrackDto>>();

        // Flatten all album tracks for random selection
        var allTracks = albums
            .SelectMany(album => albumTracks.TryGetValue(album.Id, out var tracks)
                ? tracks.Select(t => (Album: album, Track: t))
                : [])
            .ToList();

        foreach (var playlist in playlists)
        {
            var tracks = new List<PlaylistTrackDto>();
            var targetCount = playlist.TrackCount > 0 ? playlist.TrackCount : random.Next(15, 40);

            // Shuffle and pick tracks
            var shuffled = allTracks.OrderBy(_ => random.Next()).Take(targetCount).ToList();

            for (int i = 0; i < shuffled.Count; i++)
            {
                var (album, track) = shuffled[i];
                tracks.Add(new PlaylistTrackDto
                {
                    Id = track.Id,
                    Uri = $"spotify:track:{track.Id}",
                    Title = track.Title,
                    ArtistName = album.ArtistName,
                    ArtistId = album.ArtistId ?? $"spotify:artist:{album.ArtistName.GetHashCode()}",
                    AlbumName = album.Name,
                    AlbumId = album.Id,
                    ImageUrl = album.ImageUrl,
                    ImageSmallUrl = album.ImageUrl,
                    Duration = track.Duration,
                    AddedAt = now.AddDays(-random.Next(1, 180)).AddHours(-random.Next(0, 24)),
                    AddedBy = playlist.IsOwner ? "You" : "Spotify",
                    IsExplicit = track.IsExplicit
                });
            }

            // Sort by AddedAt descending
            playlistTracks[playlist.Id] = tracks.OrderByDescending(t => t.AddedAt).ToList();
        }

        return playlistTracks;
    }

    public Task<IReadOnlyList<PinnedItemDto>> GetPinnedItemsAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<bool> PinAsync(string uri, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> UnpinAsync(string uri, CancellationToken ct = default)
        => Task.FromResult(false);

    public bool IsPinned(string uri) => false;
}
