using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contexts;

[JsonSerializable(typeof(List<AlbumTrackResult>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AlbumTrackResultJsonContext : JsonSerializerContext { }

/// <summary>
/// Album service with 3-tier caching: hot (in-memory) → SQLite → API.
/// Reusable from artist page, album detail page, search results, etc.
/// </summary>
public sealed class AlbumService : IAlbumService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly IMetadataDatabase _db;
    private readonly ILogger? _logger;

    // Bounded LRU of resolved album track lists. Unbounded growth used to leak memory as
    // users browsed through many albums (each entry is a List<AlbumTrackDto> ~5-10 KB).
    // Capacity is supplied by the caching profile at DI construction time; the default
    // matches the Medium profile to preserve legacy behaviour when no profile is set.
    private readonly AlbumTracksLruCache _hot;

    public AlbumService(
        IPathfinderClient pathfinder,
        IMetadataDatabase db,
        ILogger? logger = null,
        int hotCacheCapacity = 50)
    {
        _pathfinder = pathfinder;
        _db = db;
        _logger = logger;
        _hot = new AlbumTracksLruCache(hotCacheCapacity);
    }

    public async Task<List<AlbumTrackDto>> GetTracksAsync(string albumUri, CancellationToken ct = default)
    {
        // 1. Hot cache
        if (_hot.TryGetValue(albumUri, out var cached))
            return cached;

        // 2. SQLite (stores lean AlbumTrackResult, map to DTO at boundary)
        try
        {
            var json = await _db.GetAlbumTracksCacheAsync(albumUri, ct).ConfigureAwait(false);
            if (json != null)
            {
                var raw = JsonSerializer.Deserialize(json, AlbumTrackResultJsonContext.Default.ListAlbumTrackResult);
                if (raw != null)
                {
                    var dtos = raw.Select(r => ToDto(r, albumUri)).ToList();
                    _hot.TryAdd(albumUri, dtos);
                    return dtos;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SQLite album tracks cache read failed for {Uri}", albumUri);
        }

        // 3. API
        var rawResult = await FetchFromApiAsync(albumUri, ct).ConfigureAwait(false);
        var result = rawResult.Select(r => ToDto(r, albumUri)).ToList();
        _hot.TryAdd(albumUri, result);

        // Persist lean results to SQLite (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                var jsonData = JsonSerializer.Serialize(rawResult, AlbumTrackResultJsonContext.Default.ListAlbumTrackResult);
                await _db.SetAlbumTracksCacheAsync(albumUri, jsonData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to persist album tracks to SQLite for {Uri}", albumUri);
            }
        });

        return result;
    }

    public async Task<AlbumDetailResult> GetDetailAsync(string albumUri, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetAlbumAsync(albumUri, ct).ConfigureAwait(false);
        var album = response.Data?.AlbumUnion
            ?? throw new InvalidOperationException("Album not found");

        return new AlbumDetailResult
        {
            Name = album.Name,
            Uri = album.Uri,
            Type = album.Type,
            Label = album.Label,
            CoverArtUrl = album.CoverArt?.Sources?.LastOrDefault()?.Url,
            ColorDarkHex = album.CoverArt?.ExtractedColors?.ColorDark?.Hex,
            ColorLightHex = album.CoverArt?.ExtractedColors?.ColorLight?.Hex,
            ColorRawHex = album.CoverArt?.ExtractedColors?.ColorRaw?.Hex,
            ReleaseDate = ParseAlbumDate(album.Date),
            IsSaved = album.Saved,
            IsPreRelease = album.IsPreRelease,
            PreReleaseEndDateTime = ParseIso(album.PreReleaseEndDateTime),
            TotalTracks = album.TracksV2?.TotalCount ?? 0,
            DiscCount = album.Discs?.TotalCount ?? 1,
            ShareUrl = album.SharingInfo?.ShareUrl,
            Palette = MapPalette(album.VisualIdentity),
            Copyrights = album.Copyright?.Items?.Select(c => new AlbumCopyrightResult
            {
                Text = c.Text,
                Type = c.Type
            }).ToList() ?? [],
            Artists = album.Artists?.Items?.Select(a => new AlbumArtistResult
            {
                Id = a.Id,
                Uri = a.Uri,
                Name = a.Profile?.Name,
                ImageUrl = a.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url
            }).ToList() ?? [],
            Tracks = MapTracksRaw(album.TracksV2?.Items).Select(r => ToDto(r, albumUri)).ToList(),
            MoreByArtist = album.MoreAlbumsByArtist?.Items?
                .SelectMany(i => i.Discography?.PopularReleasesAlbums?.Items ?? [])
                .Where(r => r.Uri != album.Uri) // exclude current album
                .Select(r => new AlbumRelatedResult
                {
                    Id = r.Id,
                    Uri = r.Uri,
                    Name = r.Name,
                    Type = r.Type,
                    ImageUrl = r.CoverArt?.Sources?.LastOrDefault()?.Url,
                    Year = r.Date?.Year ?? 0
                }).ToList() ?? [],
            AlternateReleases = album.Releases?.Items?
                .Where(r => !string.IsNullOrEmpty(r.Uri))
                .Select(r => new AlbumAlternateReleaseResult
                {
                    Id = r.Id,
                    Uri = r.Uri,
                    Name = r.Name,
                    Type = r.Type,
                    Year = r.Date?.Year ?? 0,
                    CoverArtUrl = r.CoverArt?.Sources?.LastOrDefault()?.Url,
                }).ToList() ?? []
        };
    }

    private static DateTimeOffset? ParseIso(string? iso)
        => string.IsNullOrEmpty(iso) ? null
            : (DateTimeOffset.TryParse(iso, out var dt) ? dt : null);

    private static AlbumPaletteTier? MapTier(ArtistExtractedColorPalette? palette)
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

    private static AlbumPalette? MapPalette(AlbumVisualIdentity? vi)
    {
        // Path is squareCoverImage on albums (vs wideFullBleedImage on artists/concerts).
        var set = vi?.SquareCoverImage?.ExtractedColorSet;
        if (set == null) return null;
        var high = MapTier(set.HighContrast);
        var higher = MapTier(set.HigherContrast);
        var min = MapTier(set.MinContrast);
        if (high == null && higher == null && min == null) return null;
        return new AlbumPalette
        {
            HighContrast = high,
            HigherContrast = higher,
            MinContrast = min,
        };
    }

    public async Task<List<AlbumMerchItemResult>> GetMerchAsync(string albumUri, CancellationToken ct = default)
    {
        try
        {
            var response = await _pathfinder.GetAlbumMerchAsync(albumUri, ct).ConfigureAwait(false);
            var items = response.Data?.AlbumUnion?.Merch?.Items;
            if (items == null || items.Count == 0)
                return [];

            return items.Select(i => new AlbumMerchItemResult
            {
                Name = i.NameV2,
                Description = i.Description,
                ImageUrl = i.Image?.Sources?.FirstOrDefault()?.Url,
                Price = i.Price,
                ShopUrl = i.Url
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch merch for {Uri}", albumUri);
            return [];
        }
    }

    private static DateTimeOffset ParseAlbumDate(Wavee.Core.Http.Pathfinder.AlbumDate? date)
    {
        if (date?.IsoString == null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(date.IsoString, out var dt) ? dt : DateTimeOffset.MinValue;
    }

    private static AlbumTrackDto ToDto(AlbumTrackResult r, string albumUri = "")
    {
        return new AlbumTrackDto
        {
            Id = r.Id,
            Uri = r.Uri ?? $"spotify:track:{r.Id}",
            Title = r.Title ?? "",
            ArtistName = r.ArtistNames ?? "",
            // Primary artist URI (when available) so the row's first link still
            // navigates correctly for legacy single-artist code paths.
            ArtistId = r.Artists.FirstOrDefault()?.Uri ?? "",
            AlbumName = "",
            AlbumId = albumUri,
            Duration = r.Duration,
            IsExplicit = r.IsExplicit,
            HasCanvas = r.HasVideo,
            TrackNumber = r.TrackNumber,
            DiscNumber = r.DiscNumber,
            IsPlayable = r.IsPlayable,
            OriginalIndex = r.TrackNumber,
            PlayCount = r.PlayCount,
            Artists = r.Artists
        };
    }

    private static List<AlbumTrackResult> MapTracksRaw(List<AlbumTrackItem>? items)
    {
        if (items == null || items.Count == 0) return [];

        return items.Where(i => i.Track != null).Select(item =>
        {
            var track = item.Track!;
            var id = track.Uri?.Split(':').LastOrDefault() ?? item.Uid ?? $"track-unknown";
            return new AlbumTrackResult
            {
                Id = id,
                Uid = item.Uid,
                Title = track.Name,
                Uri = track.Uri,
                Duration = TimeSpan.FromMilliseconds(track.Duration?.TotalMilliseconds ?? 0),
                PlayCount = long.TryParse(track.Playcount, out var pc) ? pc : 0,
                ArtistNames = string.Join(", ",
                    track.Artists?.Items?.Select(a => a.Profile?.Name ?? "") ?? []),
                Artists = MapTrackArtists(track.Artists?.Items),
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true,
                IsSaved = track.Saved,
                HasVideo = (track.AssociationsV3?.VideoAssociations?.TotalCount ?? 0) > 0,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber
            };
        }).ToList();
    }

    /// <summary>
    /// Map the GraphQL per-track artists list to <see cref="TrackArtistRef"/>,
    /// dropping entries with no URI (which would be unnavigable). Preserves order.
    /// </summary>
    private static List<TrackArtistRef> MapTrackArtists(
        List<Wavee.Core.Http.Pathfinder.ArtistTrackArtistItem>? items)
    {
        if (items == null || items.Count == 0) return [];
        var refs = new List<TrackArtistRef>(items.Count);
        foreach (var a in items)
        {
            var uri = a.Uri ?? "";
            if (string.IsNullOrEmpty(uri)) continue;
            refs.Add(new TrackArtistRef
            {
                Id = uri.Split(':').LastOrDefault() ?? "",
                Uri = uri,
                Name = a.Profile?.Name ?? ""
            });
        }
        return refs;
    }

    private async Task<List<AlbumTrackResult>> FetchFromApiAsync(string albumUri, CancellationToken ct)
    {
        var response = await _pathfinder.GetAlbumTracksAsync(albumUri, ct: ct).ConfigureAwait(false);
        var items = response.Data?.AlbumUnion?.TracksV2?.Items;

        if (items == null || items.Count == 0)
            return [];

        // Populate the music-video catalog cache as we map each track. Album
        // pages expose videoAssociations.totalCount per track — pre-warming
        // the cache here means the "Watch Video" button can appear instantly
        // when the user later plays one of these tracks.
        var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();

        var results = new List<AlbumTrackResult>(items.Count);
        foreach (var item in items)
        {
            var track = item.Track;
            if (track == null) continue;

            var id = track.Uri?.Split(':').LastOrDefault() ?? item.Uid ?? $"track-{results.Count + 1}";
            var hasVideo = (track.AssociationsV3?.VideoAssociations?.TotalCount ?? 0) > 0;

            if (videoMetadata is not null && !string.IsNullOrEmpty(track.Uri))
                videoMetadata.NoteHasVideo(track.Uri, hasVideo);

            results.Add(new AlbumTrackResult
            {
                Id = id,
                Uid = item.Uid,
                Title = track.Name,
                Uri = track.Uri,
                Duration = TimeSpan.FromMilliseconds(track.Duration?.TotalMilliseconds ?? 0),
                PlayCount = long.TryParse(track.Playcount, out var pc) ? pc : 0,
                ArtistNames = string.Join(", ",
                    track.Artists?.Items?.Select(a => a.Profile?.Name ?? "") ?? []),
                Artists = MapTrackArtists(track.Artists?.Items),
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true,
                IsSaved = track.Saved,
                HasVideo = hasVideo,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber
            });
        }

        return results;
    }

    /// <summary>
    /// Bounded LRU of album track lists. Exposes the subset of the
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// API that <see cref="AlbumService"/> uses (TryGetValue + TryAdd), so call sites
    /// read the same as the previous unbounded dictionary. Thread-safe via a single lock.
    /// </summary>
    private sealed class AlbumTracksLruCache
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly LinkedList<KeyValuePair<string, List<AlbumTrackDto>>> _lru = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, List<AlbumTrackDto>>>> _map = new();

        public AlbumTracksLruCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public bool TryGetValue(string key, out List<AlbumTrackDto> value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // LRU bump: move the hit to the head so eviction targets cold entries.
                    if (!ReferenceEquals(_lru.First, node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default!;
            return false;
        }

        public bool TryAdd(string key, List<AlbumTrackDto> value)
        {
            lock (_lock)
            {
                if (_map.ContainsKey(key)) return false;

                var node = _lru.AddFirst(new KeyValuePair<string, List<AlbumTrackDto>>(key, value));
                _map[key] = node;

                // Evict tail entries until within capacity.
                while (_map.Count > _capacity && _lru.Last is { } tail)
                {
                    _map.Remove(tail.Value.Key);
                    _lru.RemoveLast();
                }

                return true;
            }
        }
    }
}
