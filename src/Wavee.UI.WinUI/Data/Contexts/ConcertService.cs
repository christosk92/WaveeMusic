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

public sealed class ConcertService : IConcertService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly ILogger? _logger;

    public ConcertService(IPathfinderClient pathfinder, ILogger<ConcertService>? logger = null)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    private static ConcertPaletteTier? MapTier(ArtistExtractedColorPalette? palette)
    {
        if (palette?.BackgroundBase == null || palette.TextBrightAccent == null) return null;
        var bg = palette.BackgroundBase;
        var bgTint = palette.BackgroundTintedBase ?? palette.BackgroundBase;
        var accent = palette.TextBrightAccent;
        return new ConcertPaletteTier
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

    private static ConcertArtistPalette? MapPalette(ArtistVisualIdentity? vi)
    {
        var set = vi?.WideFullBleedImage?.ExtractedColorSet;
        if (set == null) return null;
        var high = MapTier(set.HighContrast);
        var higher = MapTier(set.HigherContrast);
        var min = MapTier(set.MinContrast);
        if (high == null && higher == null && min == null) return null;
        return new ConcertArtistPalette
        {
            HighContrast = high,
            HigherContrast = higher,
            MinContrast = min,
        };
    }

    public async Task<ConcertDetailResult> GetDetailAsync(string concertUri, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetConcertAsync(concertUri, ct);
        var c = response.Data?.Concert
            ?? throw new InvalidOperationException("Concert not found");

        DateTimeOffset date = default;
        if (!string.IsNullOrEmpty(c.StartDateIsoString))
            DateTimeOffset.TryParse(c.StartDateIsoString, out date);

        return new ConcertDetailResult
        {
            Title = c.Title,
            Uri = c.Uri,
            Date = date,
            Venue = c.Location?.Name,
            City = c.Location?.City,
            Country = c.Location?.Country,
            Latitude = c.Location?.Coordinates?.Latitude,
            Longitude = c.Location?.Coordinates?.Longitude,
            IsFestival = c.Festival,
            IsSaved = c.Saved ?? false,
            AgeRestriction = c.AgeRestriction,
            ShowTime = date != default ? date.ToString("h:mm tt") : null,
            FullLocation = string.Join(", ",
                new[] { c.Location?.Name, c.Location?.City, c.Location?.Country }
                    .Where(s => !string.IsNullOrEmpty(s))),

            Artists = c.Artists?.Items?.Select(a => new ConcertArtistResult
            {
                Uri = a.Data?.Uri ?? a.Uri,
                Name = a.Data?.Profile?.Name,
                AvatarUrl = a.Data?.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url,
                HeaderImageUrl = a.Data?.HeaderImage?.Data?.Sources
                    ?.OrderByDescending(s => s.MaxWidth ?? s.Width ?? 0)
                    .FirstOrDefault()?.Url,
                UpcomingConcertCount = a.Data?.Goods?.Concerts?.TotalCount ?? 0,
                PopularAlbums = a.Data?.Discography?.PopularReleasesAlbums?.Items?
                    .Select(album => new ConcertPopularAlbumResult
                    {
                        Name = album.Name,
                        Uri = album.Uri,
                        CoverArtUrl = album.CoverArt?.Sources?.FirstOrDefault()?.Url,
                        ArtistName = album.Artists?.Items?.FirstOrDefault()?.Profile?.Name
                    }).ToList() ?? [],
                Palette = MapPalette(a.Data?.VisualIdentity),
            }).ToList() ?? [],

            Offers = c.Offers?.Items?.Select(o => new ConcertOfferResult
            {
                ProviderName = o.ProviderName,
                ProviderImageUrl = o.ProviderImageUrl,
                Url = o.Url,
                SaleType = o.SaleType
            }).ToList() ?? [],

            RelatedConcerts = c.RelatedConcerts?.Items?.Select(r =>
            {
                DateTimeOffset rDate = default;
                if (!string.IsNullOrEmpty(r.Data?.StartDateIsoString))
                    DateTimeOffset.TryParse(r.Data.StartDateIsoString, out rDate);

                var firstArtist = r.Data?.Artists?.Items?.FirstOrDefault();
                return new ConcertRelatedResult
                {
                    Title = r.Data?.Title,
                    Uri = r.Data?.Uri ?? r.Uri,
                    City = r.Data?.Location?.City,
                    Venue = r.Data?.Location?.Name,
                    Date = rDate,
                    ArtistName = firstArtist?.Data?.Profile?.Name,
                    ArtistAvatarUrl = firstArtist?.Data?.Visuals?.AvatarImage?.Sources?.FirstOrDefault()?.Url
                };
            }).ToList() ?? [],

            Genres = c.Concepts?.Items?
                .OrderByDescending(g => g.Weight)
                .Select(g => g.Data?.Name ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList() ?? [],

            // Pull featured playlists from the headliner only. Secondary-artist featureds
            // would crowd the section and are best discovered via ArtistPage navigation.
            FeaturedPlaylists = c.Artists?.Items?.FirstOrDefault()?.Data?.RelatedContent?.FeaturingV2?.Items?
                .Select(item => item.Data)
                .Where(d => d != null && !string.IsNullOrEmpty(d.Uri))
                .Select(d => new ConcertFeaturedPlaylistResult
                {
                    Uri = d!.Uri,
                    Name = d.Name,
                    Description = d.Description,
                    ImageUrl = d.Images?.Items?.FirstOrDefault()?.Sources?.FirstOrDefault()?.Url,
                    OwnerName = d.OwnerV2?.Data?.Name,
                }).ToList() ?? [],
        };
    }
}
