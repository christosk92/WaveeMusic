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
using Wavee.Local;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Services;

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
    private readonly ILocalLibraryService? _localLibrary;
    private readonly ILogger? _logger;

    public ArtistService(
        IPathfinderClient pathfinder,
        IColorService colorService,
        ILocationService locationService,
        IMessenger messenger,
        ILocalLibraryService? localLibrary = null,
        ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _colorService = colorService;
        _locationService = locationService;
        _messenger = messenger;
        _localLibrary = localLibrary;
        _logger = logger;
    }

    public async Task<ArtistOverviewResult> GetOverviewAsync(string artistUri, CancellationToken ct = default)
    {
        // Local-artist branch: synthesize the same shape Pathfinder would.
        if (Wavee.Core.PlayableUri.IsLocalArtist(artistUri))
        {
            if (_localLibrary is null)
                throw new InvalidOperationException("Local library service not registered for local artist URI: " + artistUri);
            var local = await _localLibrary.GetArtistAsync(artistUri, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Local artist not found: " + artistUri);
            return SynthesizeArtistOverviewResult(local);
        }

        var response = await _pathfinder.GetArtistOverviewAsync(artistUri, ct).ConfigureAwait(false);
        var artist = response.Data?.ArtistUnion
            ?? throw new InvalidOperationException("Artist not found");

        var latest = artist.Discography?.Latest;
        var headerImageUrl = artist.HeaderImage?.Data?.Sources
            ?.OrderByDescending(s => s.MaxWidth ?? s.Width ?? 0)
            .FirstOrDefault()?.Url;

        // First gallery shot — used as a soft fallback for surfaces that want a hero
        // backdrop even when the artist has no editorial header (e.g. search top-result
        // card). Kept separate from HeaderImageUrl so ArtistPage's wide banner still
        // only picks up the real editorial image and doesn't get a square avatar
        // stretched into a landscape.
        var galleryHeroUrl = artist.Visuals?.Gallery?.Items?.FirstOrDefault()?.Sources
            ?.OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        // Prefer Spotify's pre-extracted palette (matches web player: higherContrast.backgroundBase),
        // then the avatar's raw color, and finally fall back to a live IColorService extraction.
        var heroColorHex = artist.VisualIdentity?.WideFullBleedImage?.ExtractedColorSet
            ?.HigherContrast?.BackgroundBase?.ToHex()
            ?? artist.Visuals?.AvatarImage?.ExtractedColors?.ColorRaw?.Hex;

        if (string.IsNullOrEmpty(heroColorHex))
        {
            var colorSource = artist.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url ?? headerImageUrl;
            if (!string.IsNullOrEmpty(colorSource))
            {
                try
                {
                    var extracted = await _colorService.GetColorAsync(colorSource, ct).ConfigureAwait(false);
                    heroColorHex = extracted?.DarkHex ?? extracted?.RawHex ?? extracted?.LightHex;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to resolve artist hero color for {ArtistUri}", artistUri);
                }
            }
        }

        var musicVideoMappings = MapMusicVideoMappings(artist);
        _logger?.LogInformation(
            "[VideoCache] Artist overview video pages for {Artist}: related={RelatedCount} unmapped={UnmappedCount} mappings={MappingCount}",
            artistUri,
            artist.RelatedMusicVideos?.Items?.Count ?? -1,
            artist.UnmappedMusicVideos?.Items?.Count ?? -1,
            musicVideoMappings.Count);
        PrewarmMusicVideoCatalog(musicVideoMappings);

        return new ArtistOverviewResult
        {
            Name = artist.Profile?.Name,
            ImageUrl = artist.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url,
            HeaderImageUrl = headerImageUrl,
            GalleryHeroUrl = galleryHeroUrl,
            HeroColorHex = heroColorHex,
            Palette = MapPalette(artist.VisualIdentity),
            MonthlyListeners = artist.Stats?.MonthlyListeners ?? 0,
            Followers = artist.Stats?.Followers ?? 0,
            WorldRank = artist.Stats?.WorldRank is > 0 ? artist.Stats.WorldRank : null,
            Biography = artist.Profile?.Biography?.Text,
            // Prefer the newer onPlatformReputationTrait — it carries both
            // verified + registered flags. Fall back to legacy profile.verified
            // for older response shapes.
            IsVerified = artist.OnPlatformReputationTrait?.Verification?.IsVerified
                         ?? artist.Profile?.Verified
                         ?? false,
            IsRegistered = artist.OnPlatformReputationTrait?.Verification?.IsRegistered ?? false,

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

            PopularReleases = MapPopularReleases(artist.Discography?.PopularReleasesAlbums),
            AppearsOn = MapReleaseGroup(artist.RelatedContent?.AppearsOn, "APPEARS_ON"),
            Playlists = MapPlaylists(artist.Profile?.PlaylistsV2, artist.RelatedContent?.FeaturingV2, artist.RelatedContent?.DiscoveredOnV2),
            MusicVideos = MapMusicVideos(artist.RelatedMusicVideos),
            Merch = MapMerch(artist.Goods?.Merch),

            PinnedItem = artist.Profile?.PinnedItem != null ? MapPinnedItem(artist.Profile.PinnedItem) : null,

            WatchFeed = artist.WatchFeedEntrypoint?.Video?.FileId != null ? new ArtistWatchFeedResult
            {
                VideoUrl = artist.WatchFeedEntrypoint.Video.FileId,
                ThumbnailUrl = artist.WatchFeedEntrypoint.ThumbnailImage?.Data?.Sources?.FirstOrDefault()?.Url
            } : null,

            Concerts = await MapConcertsAsync(artist.Goods?.Concerts, ct).ConfigureAwait(false),

            ExternalLinks = (artist.Profile?.ExternalLinks?.Items ?? new())
                .Where(l => !string.IsNullOrWhiteSpace(l.Name) && !string.IsNullOrWhiteSpace(l.Url))
                .Select(l => new ArtistSocialLinkResult { Name = l.Name!, Url = l.Url! })
                .ToList(),

            TopCities = (artist.Stats?.TopCities?.Items ?? new())
                .Where(c => !string.IsNullOrWhiteSpace(c.City))
                .Select(c => new ArtistTopCityResult
                {
                    City = c.City!,
                    Country = c.Country,
                    NumberOfListeners = c.NumberOfListeners
                })
                .ToList(),

            GalleryPhotos = (artist.Visuals?.Gallery?.Items ?? new())
                .Select(g => g.Sources?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .ToList(),

            MusicVideoMappings = musicVideoMappings
        };
    }

    private void PrewarmMusicVideoCatalog(IReadOnlyList<ArtistMusicVideoMappingResult> mappings)
    {
        if (mappings.Count == 0) return;

        var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<IMusicVideoMetadataService>();
        if (videoMetadata is null) return;

        foreach (var mapping in mappings)
            videoMetadata.NoteVideoUri(mapping.AudioTrackUri, mapping.VideoTrackUri);
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
        // Local-artist discography is already included in GetArtistAsync — the
        // overview synthesizer pre-populates Albums. The "view all" page calling
        // back into this method gets the same list directly.
        if (Wavee.Core.PlayableUri.IsLocalArtist(artistUri))
        {
            if (_localLibrary is null) return new List<ArtistReleaseResult>();
            var local = await _localLibrary.GetArtistAsync(artistUri, ct).ConfigureAwait(false);
            if (local is null) return new List<ArtistReleaseResult>();
            return local.Albums
                .Skip(offset).Take(limit)
                .Select(MapLocalAlbumSummaryToRelease)
                .ToList();
        }

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

    private static ArtistPaletteTier? MapTier(ArtistExtractedColorPalette? palette)
    {
        if (palette?.BackgroundBase == null || palette.TextBrightAccent == null) return null;
        var bg = palette.BackgroundBase;
        var bgTint = palette.BackgroundTintedBase ?? palette.BackgroundBase;
        var accent = palette.TextBrightAccent;
        return new ArtistPaletteTier
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

    private static ArtistPalette? MapPalette(ArtistVisualIdentity? vi)
    {
        var set = vi?.WideFullBleedImage?.ExtractedColorSet;
        if (set == null) return null;
        var high = MapTier(set.HighContrast);
        var higher = MapTier(set.HigherContrast);
        var min = MapTier(set.MinContrast);
        if (high == null && higher == null && min == null) return null;
        return new ArtistPalette
        {
            HighContrast = high,
            HigherContrast = higher,
            MinContrast = min,
        };
    }

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
                // Use LastOrDefault — the Sources list is ordered ascending by resolution,
                // and the first source can be a tiny/missing variant for some tracks.
                // Matches the pattern used elsewhere for release and related-artist images.
                AlbumImageUrl = track.AlbumOfTrack?.CoverArt?.Sources?.LastOrDefault()?.Url,
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

    private static List<ArtistMusicVideoMappingResult> MapMusicVideoMappings(ArtistUnion artist)
    {
        var results = new List<ArtistMusicVideoMappingResult>();
        var topTrackUriByName = (artist.Discography?.TopTracks?.Items ?? new())
            .Select(item => item.Track)
            .Where(track => track is not null
                            && !string.IsNullOrWhiteSpace(track.Name)
                            && !string.IsNullOrWhiteSpace(track.Uri))
            .GroupBy(track => NormalizeTrackTitle(track!.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()!.Uri!, StringComparer.OrdinalIgnoreCase);

        AddMappings(artist.RelatedMusicVideos, results, topTrackUriByName);
        AddMappings(artist.UnmappedMusicVideos, results, topTrackUriByName);
        return results
            .GroupBy(m => m.AudioTrackUri, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static void AddMappings(
        ArtistMusicVideosPage? page,
        List<ArtistMusicVideoMappingResult> results,
        IReadOnlyDictionary<string, string> topTrackUriByName)
    {
        if (page?.Items is not { Count: > 0 }) return;

        foreach (var item in page.Items)
        {
            var videoUri = item.Uri ?? item.Data?.Uri;
            if (string.IsNullOrWhiteSpace(videoUri))
                continue;

            var audioUris = item.Data?.AssociationsV3?.AudioAssociations?.Items?
                .Select(association => association.TrackAudio?.Uri)
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .Select(uri => uri!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (audioUris is null or { Count: 0 })
            {
                var title = NormalizeTrackTitle(item.Data?.Name);
                if (!string.IsNullOrWhiteSpace(title)
                    && topTrackUriByName.TryGetValue(title, out var matchedAudioUri))
                {
                    audioUris = new List<string> { matchedAudioUri };
                }
            }

            if (audioUris is null or { Count: 0 })
                continue;

            foreach (var audioUri in audioUris)
            {
                results.Add(new ArtistMusicVideoMappingResult
                {
                    AudioTrackUri = audioUri,
                    VideoTrackUri = videoUri
                });
            }
        }
    }

    private static string NormalizeTrackTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();

    public async Task<List<ArtistTopTrackResult>> GetExtendedTopTracksAsync(
        string artistUri, CancellationToken ct = default)
    {
        // Local artist: derive "top tracks" from the locally-indexed track list.
        // The 10-track preview from GetArtistAsync is the v1 stand-in for
        // Spotify's play-count-ordered top-tracks list.
        if (Wavee.Core.PlayableUri.IsLocalArtist(artistUri))
        {
            if (_localLibrary is null) return new List<ArtistTopTrackResult>();
            var local = await _localLibrary.GetArtistAsync(artistUri, ct).ConfigureAwait(false);
            if (local is null) return new List<ArtistTopTrackResult>();
            return local.AllTracks.Select(MapLocalTrackToArtistTopTrack).ToList();
        }

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

    public async Task<IReadOnlyDictionary<string, string?>> GetTrackImagesAsync(
        IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (trackUris.Count == 0)
            return new Dictionary<string, string?>();

        try
        {
            var request = _messenger.Send(new TrackImagesEnrichmentRequest
            {
                TrackUris = trackUris,
                CancellationToken = ct
            });

            if (!request.HasReceivedResponse)
                return new Dictionary<string, string?>();
            return await request.Response.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve track images for {Count} URIs", trackUris.Count);
            return new Dictionary<string, string?>();
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

    private static List<ArtistReleaseResult> MapPopularReleases(ArtistPopularReleases? popular)
    {
        if (popular?.Items == null) return [];

        var results = new List<ArtistReleaseResult>();
        foreach (var release in popular.Items)
        {
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

    private static List<ArtistMusicVideoResult> MapMusicVideos(ArtistMusicVideosPage? videos)
    {
        if (videos?.Items == null) return [];

        var results = new List<ArtistMusicVideoResult>();
        foreach (var item in videos.Items)
        {
            var data = item.Data;
            // Video items are surfaced as track entities (the audio-track URI is
            // the linked target; the cover-art URI is `_uri` on the wrapper).
            if (data?.Uri == null) continue;

            results.Add(new ArtistMusicVideoResult
            {
                TrackUri = data.Uri,
                Title = data.Name,
                ThumbnailUrl = data.AlbumOfTrack?.CoverArt?.Sources?.LastOrDefault()?.Url,
                AlbumUri = data.AlbumOfTrack?.Uri,
                Duration = data.Duration?.TotalMilliseconds is > 0
                    ? TimeSpan.FromMilliseconds(data.Duration.TotalMilliseconds)
                    : TimeSpan.Zero,
                IsExplicit = string.Equals(data.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase)
            });
        }
        return results;
    }

    /// <summary>Merges playlistsV2 (official artist playlists), featuringV2
    /// (Spotify-curated playlists featuring the artist), and discoveredOnV2
    /// (user / algorithmic playlists where the artist was discovered) into a
    /// single source-tagged list. <c>GenericError</c> rows from the payload
    /// are silently dropped (per V4A spec).</summary>
    private static List<ArtistPlaylistResult> MapPlaylists(
        ArtistPlaylistCollection? official,
        ArtistPlaylistCollection? featuring,
        ArtistPlaylistCollection? discoveredOn)
    {
        var results = new List<ArtistPlaylistResult>();
        AppendPlaylists(official, "Spotify · official", results);
        AppendPlaylists(featuring, "Spotify · featured", results);
        AppendPlaylists(discoveredOn, null, results, isDiscoveredOn: true);
        return results;
    }

    private static void AppendPlaylists(
        ArtistPlaylistCollection? collection,
        string? fixedSubtitle,
        List<ArtistPlaylistResult> sink,
        bool isDiscoveredOn = false)
    {
        if (collection?.Items == null) return;
        foreach (var item in collection.Items)
        {
            if (string.Equals(item.Typename, "GenericError", StringComparison.Ordinal))
                continue;
            var data = item.Data;
            if (data == null || string.IsNullOrEmpty(data.Uri)) continue;
            if (string.Equals(data.Typename, "GenericError", StringComparison.Ordinal))
                continue;

            var imageUrl = data.Images?.Items?.FirstOrDefault()?.Sources
                ?.OrderByDescending(s => s.Width ?? 0).FirstOrDefault()?.Url;

            string? subtitle;
            if (isDiscoveredOn)
            {
                var owner = data.OwnerV2?.Data?.Name;
                subtitle = !string.IsNullOrEmpty(owner)
                    ? $"{owner} · discovered on"
                    : "Discovered on";
            }
            else
            {
                subtitle = fixedSubtitle;
            }

            sink.Add(new ArtistPlaylistResult
            {
                Uri = data.Uri!,
                Name = data.Name,
                ImageUrl = imageUrl,
                Subtitle = subtitle
            });
        }
    }

    private static List<ArtistMerchResult> MapMerch(ArtistMerch? merch)
    {
        if (merch?.Items == null) return [];

        var results = new List<ArtistMerchResult>();
        foreach (var item in merch.Items)
        {
            if (string.IsNullOrWhiteSpace(item.NameV2) && string.IsNullOrWhiteSpace(item.Url)) continue;

            results.Add(new ArtistMerchResult
            {
                Name = item.NameV2,
                Price = item.Price,
                Description = item.Description,
                ImageUrl = item.Image?.Sources?.LastOrDefault()?.Url,
                Uri = item.Uri,
                ShopUrl = item.Url
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

    // ─────────────────────────────────────────────────────────────────────────
    // Local artist → ArtistOverviewResult synthesis
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="ArtistOverviewResult"/> from a <see cref="LocalArtistDetail"/>.
    /// Same XAML / VM / Store pipeline as Spotify artists; Spotify-only sections
    /// (concerts, related artists, gallery photos, biography, social links)
    /// come back empty so their x:Load bindings collapse.
    /// </summary>
    private static ArtistOverviewResult SynthesizeArtistOverviewResult(LocalArtistDetail local)
    {
        var topTracks = local.AllTracks.Take(10).Select(MapLocalTrackToArtistTopTrack).ToList();
        var albums = local.Albums.Select(MapLocalAlbumSummaryToRelease).ToList();

        return new ArtistOverviewResult
        {
            Name = local.Name,
            ImageUrl = local.ArtworkUri,
            HeaderImageUrl = null,
            GalleryHeroUrl = null,
            HeroColorHex = null,
            Palette = null,
            MonthlyListeners = 0,
            Followers = 0,
            WorldRank = null,
            Biography = null,
            IsVerified = false,
            IsRegistered = false,
            LatestRelease = null,
            TopTracks = topTracks,
            Albums = albums,
            Singles = new List<ArtistReleaseResult>(),
            Compilations = new List<ArtistReleaseResult>(),
            RelatedArtists = new List<RelatedArtistResult>(),
            AlbumsTotalCount = albums.Count,
            SinglesTotalCount = 0,
            CompilationsTotalCount = 0,
            PinnedItem = null,
            WatchFeed = null,
            Concerts = new List<ArtistConcertResult>(),
            ExternalLinks = new List<ArtistSocialLinkResult>(),
            TopCities = new List<ArtistTopCityResult>(),
            GalleryPhotos = new List<string>(),
            MusicVideoMappings = new List<ArtistMusicVideoMappingResult>(),
        };
    }

    private static ArtistTopTrackResult MapLocalTrackToArtistTopTrack(LocalTrackRow t)
    {
        var id = Wavee.Core.PlayableUri.ExtractId(t.TrackUri).ToString();
        if (string.IsNullOrEmpty(id)) id = t.TrackUri;
        return new ArtistTopTrackResult
        {
            Id = id,
            Title = t.Title ?? System.IO.Path.GetFileNameWithoutExtension(t.FilePath),
            Uri = t.TrackUri,
            AlbumImageUrl = t.ArtworkUri,
            AlbumUri = t.AlbumUri,
            AlbumName = t.Album,
            Duration = TimeSpan.FromMilliseconds(t.DurationMs),
            PlayCount = 0,
            ArtistNames = t.Artist ?? t.AlbumArtist,
            IsExplicit = false,
            IsPlayable = true,
            HasVideo = t.IsVideo,
        };
    }

    private static ArtistReleaseResult MapLocalAlbumSummaryToRelease(LocalAlbumSummary a)
    {
        var id = Wavee.Core.PlayableUri.ExtractId(a.AlbumUri).ToString();
        if (string.IsNullOrEmpty(id)) id = a.AlbumUri;
        return new ArtistReleaseResult
        {
            Id = id,
            Uri = a.AlbumUri,
            Name = a.Album,
            Type = "ALBUM",
            ImageUrl = a.ArtworkUri,
            ReleaseDate = a.Year is { } y && y > 0
                ? new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero)
                : DateTimeOffset.MinValue,
            TrackCount = a.TrackCount,
            Label = null,
            Year = a.Year ?? 0,
        };
    }

}
