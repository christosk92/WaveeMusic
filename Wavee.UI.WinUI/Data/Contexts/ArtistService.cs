using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Service layer for artist data. Wraps IPathfinderClient with domain mapping.
/// Future: add SQLite caching for overview data.
/// </summary>
public sealed class ArtistService : IArtistService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly IColorService _colorService;
    private readonly ILocationService _locationService;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;

    public ArtistService(
        IPathfinderClient pathfinder,
        IColorService colorService,
        ILocationService locationService,
        IMessenger messenger,
        ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _colorService = colorService;
        _locationService = locationService;
        _messenger = messenger;
        _logger = logger;
    }

    public async Task<ArtistOverviewResult> GetOverviewAsync(string artistUri, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetArtistOverviewAsync(artistUri, ct).ConfigureAwait(false);
        var artist = response.Data?.ArtistUnion
            ?? throw new InvalidOperationException("Artist not found");

        var latest = artist.Discography?.Latest;
        var headerImageUrl = artist.HeaderImage?.Data?.Sources
            ?.OrderByDescending(s => s.MaxWidth ?? s.Width ?? 0)
            .FirstOrDefault()?.Url;
        string? heroColorHex = null;

        if (!string.IsNullOrEmpty(headerImageUrl))
        {
            try
            {
                var extracted = await _colorService.GetColorAsync(headerImageUrl, ct).ConfigureAwait(false);
                heroColorHex = extracted?.RawHex ?? extracted?.DarkHex ?? extracted?.LightHex;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to resolve artist hero color for {ArtistUri}", artistUri);
            }
        }

        return new ArtistOverviewResult
        {
            Name = artist.Profile?.Name,
            ImageUrl = artist.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url,
            HeaderImageUrl = headerImageUrl,
            HeroColorHex = heroColorHex,
            MonthlyListeners = artist.Stats?.MonthlyListeners ?? 0,
            Followers = artist.Stats?.Followers ?? 0,
            Biography = artist.Profile?.Biography?.Text,
            IsVerified = artist.Profile?.Verified ?? false,

            LatestRelease = latest == null ? null : new ArtistLatestReleaseResult
            {
                Name = latest.Name,
                ImageUrl = latest.CoverArt?.Sources?.LastOrDefault()?.Url,
                Uri = latest.Uri,
                Type = latest.Type,
                TrackCount = latest.Tracks?.TotalCount ?? 0,
                FormattedDate = FormatReleaseDate(latest.Date)
            },

            TopTracks = MapTopTracks(artist.Discography?.TopTracks),
            Albums = MapReleaseGroup(artist.Discography?.Albums, "ALBUM"),
            Singles = MapReleaseGroup(artist.Discography?.Singles, "SINGLE"),
            Compilations = MapReleaseGroup(artist.Discography?.Compilations, "COMPILATION"),
            RelatedArtists = MapRelatedArtists(artist.RelatedContent?.RelatedArtists),

            AlbumsTotalCount = artist.Discography?.Albums?.TotalCount ?? 0,
            SinglesTotalCount = artist.Discography?.Singles?.TotalCount ?? 0,
            CompilationsTotalCount = artist.Discography?.Compilations?.TotalCount ?? 0,

            PinnedItem = artist.Profile?.PinnedItem != null ? MapPinnedItem(artist.Profile.PinnedItem) : null,

            WatchFeed = artist.WatchFeedEntrypoint?.Video?.FileId != null ? new ArtistWatchFeedResult
            {
                VideoUrl = artist.WatchFeedEntrypoint.Video.FileId,
                ThumbnailUrl = artist.WatchFeedEntrypoint.ThumbnailImage?.Data?.Sources?.FirstOrDefault()?.Url
            } : null,

            Concerts = await MapConcertsAsync(artist.Goods?.Concerts, ct).ConfigureAwait(false)
        };
    }

    private async Task<List<ArtistConcertResult>> MapConcertsAsync(ArtistConcerts? concerts, CancellationToken ct)
    {
        if (concerts?.Items == null || concerts.Items.Count == 0) return [];

        // Fetch user city via ILocationService (cached internally)
        await _locationService.GetUserCityAsync(ct).ConfigureAwait(false);

        var results =   new List<ArtistConcertResult>();
        foreach (var item in concerts.Items)
        {
            var data = item.Data;
            if (data == null) continue;

            DateTimeOffset date = default;
            if (!string.IsNullOrEmpty(data.StartDateIsoString))
                DateTimeOffset.TryParse(data.StartDateIsoString, out date);

            results.Add(new ArtistConcertResult
            {
                Title = data.Title,
                Venue = data.Location?.Name,
                City = data.Location?.City,
                Date = date,
                IsFestival = data.Festival,
                Uri = data.Uri,
                IsNearUser = _locationService.IsNearUser(data.Location?.City)
            });
        }

        return results
            .OrderByDescending(c => c.IsNearUser)
            .ThenBy(c => c.Date)
            .ToList();
    }

    public async Task<List<ArtistReleaseResult>> GetDiscographyAllAsync(
        string artistUri, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetArtistDiscographyAllAsync(artistUri, offset, limit, ct).ConfigureAwait(false);
        var allGroup = response.Data?.ArtistUnion?.Discography?.All;
        if (allGroup?.Items == null) return [];

        var results = new List<ArtistReleaseResult>();
        foreach (var item in allGroup.Items)
        {
            var release = item.Releases?.Items?.FirstOrDefault();
            if (release?.Id == null) continue;

            results.Add(new ArtistReleaseResult
            {
                Id = release.Id,
                Uri = release.Uri,
                Name = release.Name,
                Type = release.Type ?? "ALBUM",
                ImageUrl = release.CoverArt?.Sources?.LastOrDefault()?.Url,
                ReleaseDate = ParseReleaseDate(release.Date),
                TrackCount = release.Tracks?.TotalCount ?? 0,
                Label = release.Label,
                Year = release.Date?.Year ?? 0
            });
        }
        return results;
    }

    public async Task<List<ArtistReleaseResult>> GetDiscographyPageAsync(
        string artistUri, string type, int offset, int limit = 20, CancellationToken ct = default)
    {
        var response = type switch
        {
            "ALBUM" => await _pathfinder.GetArtistDiscographyAlbumsAsync(artistUri, offset, limit, ct).ConfigureAwait(false),
            "SINGLE" => await _pathfinder.GetArtistDiscographySinglesAsync(artistUri, offset, limit, ct).ConfigureAwait(false),
            "COMPILATION" => await _pathfinder.GetArtistDiscographyCompilationsAsync(artistUri, offset, limit, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown discography type: {type}")
        };

        var group = type switch
        {
            "ALBUM" => response.Data?.ArtistUnion?.Discography?.Albums,
            "SINGLE" => response.Data?.ArtistUnion?.Discography?.Singles,
            "COMPILATION" => response.Data?.ArtistUnion?.Discography?.Compilations,
            _ => null
        };

        return MapReleaseGroup(group, type);
    }

    // ── Mapping helpers ──

    private static List<ArtistTopTrackResult> MapTopTracks(ArtistTopTracks? topTracks)
    {
        if (topTracks?.Items == null) return [];

        var results = new List<ArtistTopTrackResult>();
        foreach (var item in topTracks.Items)
        {
            var track = item.Track;
            if (track == null) continue;

            results.Add(new ArtistTopTrackResult
            {
                Id = track.Id ?? item.Uid ?? $"track-{results.Count + 1}",
                Title = track.Name,
                Uri = track.Uri,
                AlbumImageUrl = track.AlbumOfTrack?.CoverArt?.Sources?.FirstOrDefault()?.Url,
                AlbumUri = track.AlbumOfTrack?.Uri,
                AlbumName = track.AlbumOfTrack?.Name,
                Duration = TimeSpan.FromMilliseconds(track.Duration?.TotalMilliseconds ?? 0),
                PlayCount = long.TryParse(track.Playcount, out var pc) ? pc : 0,
                ArtistNames = string.Join(", ",
                    track.Artists?.Items?.Select(a => a.Profile?.Name ?? "") ?? []),
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true,
                HasVideo = track.HasVideo
            });
        }
        return results;
    }

    public async Task<List<ArtistTopTrackResult>> GetExtendedTopTracksAsync(
        string artistUri, CancellationToken ct = default)
    {
        try
        {
            var request = _messenger.Send(new ExtendedTopTracksRequest
            {
                ArtistUri = artistUri,
                CancellationToken = ct
            });

            if (!request.HasReceivedResponse) return [];
            return await request.Response.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get extended top tracks for {Artist}", artistUri);
            return [];
        }
    }

    private static List<ArtistReleaseResult> MapReleaseGroup(ArtistReleaseGroup? group, string type)
    {
        if (group?.Items == null) return [];

        var results = new List<ArtistReleaseResult>();
        foreach (var item in group.Items)
        {
            var release = item.Releases?.Items?.FirstOrDefault();
            if (release?.Id == null) continue;

            results.Add(new ArtistReleaseResult
            {
                Id = release.Id,
                Uri = release.Uri,
                Name = release.Name,
                Type = type,
                ImageUrl = release.CoverArt?.Sources?.LastOrDefault()?.Url,
                ReleaseDate = ParseReleaseDate(release.Date),
                TrackCount = release.Tracks?.TotalCount ?? 0,
                Label = release.Label,
                Year = release.Date?.Year ?? 0
            });
        }
        return results;
    }

    private static List<RelatedArtistResult> MapRelatedArtists(ArtistRelatedArtists? related)
    {
        if (related?.Items == null) return [];

        return related.Items.Select(ra => new RelatedArtistResult
        {
            Id = ra.Id,
            Uri = ra.Uri,
            Name = ra.Profile?.Name,
            ImageUrl = ra.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url
        }).ToList();
    }

    private static DateTimeOffset ParseReleaseDate(ArtistReleaseDate? date)
    {
        if (date == null) return DateTimeOffset.MinValue;
        try { return new DateTimeOffset(date.Year, date.Month ?? 1, date.Day ?? 1, 0, 0, 0, TimeSpan.Zero); }
        catch (Exception ex) { Debug.WriteLine($"Failed to parse release date: {ex.Message}"); return DateTimeOffset.MinValue; }
    }

    private static string FormatReleaseDate(ArtistReleaseDate? date)
    {
        if (date == null) return "";
        var dt = new DateTime(date.Year, date.Month ?? 1, date.Day ?? 1);
        return dt.ToString("MMM d, yyyy").ToUpperInvariant();
    }

    private static ArtistPinnedItemResult MapPinnedItem(ArtistPinnedItem pin)
    {
        // Image resolution priority:
        // 1. ItemV2.Data.CoverArt (albums)
        // 2. ThumbnailImage (tracks, episodes, etc.)
        // 3. BackgroundImageV2 (final fallback)
        var imageUrl = pin.ItemV2?.Data?.CoverArt?.Sources?.LastOrDefault()?.Url
                    ?? pin.ThumbnailImage?.Data?.Sources?.FirstOrDefault()?.Url
                    ?? pin.BackgroundImageV2?.Data?.Sources?.FirstOrDefault()?.Url;

        return new ArtistPinnedItemResult
        {
            Title = pin.Title,
            Subtitle = pin.Subtitle,
            Comment = pin.Comment,
            Type = pin.Type,
            Uri = pin.Uri,
            ImageUrl = imageUrl,
            BackgroundImageUrl = pin.BackgroundImageV2?.Data?.Sources?.FirstOrDefault()?.Url
        };
    }

}
