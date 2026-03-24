using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Service layer for artist data. Wraps IPathfinderClient with domain mapping.
/// Future: add SQLite caching for overview data.
/// </summary>
public sealed class ArtistService : IArtistService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly ILogger? _logger;

    public ArtistService(IPathfinderClient pathfinder, ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    public async Task<ArtistOverviewResult> GetOverviewAsync(string artistUri, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetArtistOverviewAsync(artistUri, ct);
        var artist = response.Data?.ArtistUnion
            ?? throw new InvalidOperationException("Artist not found");

        var latest = artist.Discography?.Latest;

        return new ArtistOverviewResult
        {
            Name = artist.Profile?.Name,
            ImageUrl = artist.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url,
            HeaderImageUrl = artist.HeaderImage?.Data?.Sources
                ?.OrderByDescending(s => s.MaxWidth ?? s.Width ?? 0)
                .FirstOrDefault()?.Url,
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
            CompilationsTotalCount = artist.Discography?.Compilations?.TotalCount ?? 0
        };
    }

    public async Task<List<ArtistReleaseResult>> GetDiscographyPageAsync(
        string artistUri, string type, int offset, int limit = 20, CancellationToken ct = default)
    {
        var response = type switch
        {
            "ALBUM" => await _pathfinder.GetArtistDiscographyAlbumsAsync(artistUri, offset, limit, ct),
            "SINGLE" => await _pathfinder.GetArtistDiscographySinglesAsync(artistUri, offset, limit, ct),
            "COMPILATION" => await _pathfinder.GetArtistDiscographyCompilationsAsync(artistUri, offset, limit, ct),
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
                Duration = TimeSpan.FromMilliseconds(track.Duration?.TotalMilliseconds ?? 0),
                PlayCount = long.TryParse(track.Playcount, out var pc) ? pc : 0,
                ArtistNames = string.Join(", ",
                    track.Artists?.Items?.Select(a => a.Profile?.Name ?? "") ?? []),
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true
            });
        }
        return results;
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
        catch { return DateTimeOffset.MinValue; }
    }

    private static string FormatReleaseDate(ArtistReleaseDate? date)
    {
        if (date == null) return "";
        var dt = new DateTime(date.Year, date.Month ?? 1, date.Day ?? 1);
        return dt.ToString("MMM d, yyyy").ToUpperInvariant();
    }
}
