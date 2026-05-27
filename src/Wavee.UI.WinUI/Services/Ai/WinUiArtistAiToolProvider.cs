using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.AI.Artists;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Services.Ai;

public sealed partial class WinUiArtistAiToolProvider : IArtistAiToolProvider
{
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;

    public WinUiArtistAiToolProvider(
        IArtistService artistService,
        IAlbumService albumService)
    {
        _artistService = artistService;
        _albumService = albumService;
    }

    public async Task<ArtistProfileFacts> GetProfileAsync(
        string artistUri,
        CancellationToken cancellationToken = default)
    {
        var overview = await _artistService.GetOverviewAsync(artistUri, cancellationToken);
        return new ArtistProfileFacts(
            artistUri,
            overview.Name,
            overview.Biography,
            overview.MonthlyListeners,
            overview.Followers,
            overview.WorldRank,
            overview.TopCities
                .Select(c => string.IsNullOrWhiteSpace(c.Country) ? c.City : $"{c.City}, {c.Country}")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(10)
                .ToList());
    }

    public async Task<IReadOnlyList<ArtistTrackFact>> GetTopTracksAsync(
        string artistUri,
        CancellationToken cancellationToken = default)
    {
        var tracks = await _artistService.GetExtendedTopTracksAsync(artistUri, cancellationToken);
        return tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => new ArtistTrackFact(
                t.Title,
                t.Uri,
                t.AlbumName,
                t.AlbumUri,
                t.AlbumImageUrl,
                t.PlayCount,
                Year: null,
                ArtistNames: SplitArtistNames(t.ArtistNames)))
            .Take(50)
            .ToList();
    }

    public async Task<IReadOnlyList<ArtistReleaseFact>> GetDiscographyAsync(
        string artistUri,
        CancellationToken cancellationToken = default)
    {
        var releases = await _artistService.GetDiscographyAllAsync(
            artistUri,
            offset: 0,
            limit: 100,
            ct: cancellationToken);

        return releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new ArtistReleaseFact(
                r.Name,
                r.Uri,
                r.Type,
                r.ImageUrl,
                r.ReleaseDate,
                r.TrackCount,
                r.Label,
                r.Year))
            .OrderBy(r => r.ReleaseDate == default ? DateTimeOffset.MaxValue : r.ReleaseDate)
            .Take(100)
            .ToList();
    }

    public async Task<IReadOnlyList<ArtistTrackFact>> GetReleaseTracksAsync(
        string artistUri,
        IReadOnlyList<ArtistReleaseFact> releases,
        int maxReleases = 24,
        CancellationToken cancellationToken = default)
    {
        var selected = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Uri))
            .Take(Math.Max(1, maxReleases))
            .ToList();

        if (selected.Count == 0)
            return [];

        var results = new List<ArtistTrackFact>(selected.Sum(r => Math.Max(r.TrackCount, 1)));
        foreach (var release in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Wavee.UI.Models.AlbumTrackDto> tracks;
            try
            {
                tracks = await _albumService.GetTracksAsync(release.Uri!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var track in tracks)
            {
                if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Uri) || !track.IsPlayable)
                    continue;

                var albumName = string.IsNullOrWhiteSpace(track.AlbumName)
                    ? release.Name
                    : track.AlbumName;
                var albumUri = string.IsNullOrWhiteSpace(track.AlbumId)
                    ? release.Uri
                    : track.AlbumId;

                results.Add(new ArtistTrackFact(
                    track.Title,
                    track.Uri,
                    albumName,
                    albumUri,
                    track.ImageUrl ?? release.ImageUrl,
                    track.PlayCount,
                    release.Year,
                    release.ReleaseDate,
                    track.TrackNumber,
                    ArtistNames: track.Artists is { Count: > 0 }
                        ? track.Artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                        : SplitArtistNames(track.ArtistName)));
            }
        }

        return results.Take(300).ToList();
    }

    private static IReadOnlyList<string> SplitArtistNames(string? artistNames)
        => string.IsNullOrWhiteSpace(artistNames)
            ? []
            : artistNames
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
}
