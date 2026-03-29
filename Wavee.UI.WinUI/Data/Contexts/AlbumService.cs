using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, List<AlbumTrackDto>> _hot = new();

    public AlbumService(IPathfinderClient pathfinder, IMetadataDatabase db, ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _db = db;
        _logger = logger;
    }

    public async Task<List<AlbumTrackDto>> GetTracksAsync(string albumUri, CancellationToken ct = default)
    {
        // 1. Hot cache
        if (_hot.TryGetValue(albumUri, out var cached))
            return cached;

        // 2. SQLite (stores lean AlbumTrackResult, map to DTO at boundary)
        try
        {
            var json = await _db.GetAlbumTracksCacheAsync(albumUri, ct);
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
        var rawResult = await FetchFromApiAsync(albumUri, ct);
        var result = rawResult.Select(r => ToDto(r, albumUri)).ToList();
        _hot.TryAdd(albumUri, result);

        // Persist lean results to SQLite (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                var jsonData = JsonSerializer.Serialize(rawResult, AlbumTrackResultJsonContext.Default.ListAlbumTrackResult);
                await _db.SetAlbumTracksCacheAsync(albumUri, jsonData);
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
        var response = await _pathfinder.GetAlbumAsync(albumUri, ct);
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
            TotalTracks = album.TracksV2?.TotalCount ?? 0,
            DiscCount = album.Discs?.TotalCount ?? 1,
            ShareUrl = album.SharingInfo?.ShareUrl,
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
                }).ToList() ?? []
        };
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
            ArtistId = "",
            AlbumName = "",
            AlbumId = albumUri,
            Duration = r.Duration,
            IsExplicit = r.IsExplicit,
            TrackNumber = r.TrackNumber,
            DiscNumber = r.DiscNumber,
            IsPlayable = r.IsPlayable,
            OriginalIndex = r.TrackNumber,
            PlayCount = r.PlayCount
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
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true,
                IsSaved = track.Saved,
                HasVideo = (track.AssociationsV3?.VideoAssociations?.TotalCount ?? 0) > 0,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber
            };
        }).ToList();
    }

    private async Task<List<AlbumTrackResult>> FetchFromApiAsync(string albumUri, CancellationToken ct)
    {
        var response = await _pathfinder.GetAlbumTracksAsync(albumUri, ct: ct);
        var items = response.Data?.AlbumUnion?.TracksV2?.Items;

        if (items == null || items.Count == 0)
            return [];

        var results = new List<AlbumTrackResult>(items.Count);
        foreach (var item in items)
        {
            var track = item.Track;
            if (track == null) continue;

            var id = track.Uri?.Split(':').LastOrDefault() ?? item.Uid ?? $"track-{results.Count + 1}";

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
                IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                IsPlayable = track.Playability?.Playable ?? true,
                IsSaved = track.Saved,
                HasVideo = (track.AssociationsV3?.VideoAssociations?.TotalCount ?? 0) > 0,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber
            });
        }

        return results;
    }
}
