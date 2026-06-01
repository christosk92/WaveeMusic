using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Models;
using static Wavee.UI.Services.LikedSongsGroupingKeys;

namespace Wavee.UI.Services;

/// <summary>
/// Groups liked songs into virtual artist entries for Library > Artists.
/// </summary>
public static class LikedSongsByArtistGrouper
{
    public static IReadOnlyList<LikedArtistDto> Group(
        IReadOnlyList<LikedSongDto> likedSongs,
        IReadOnlyList<LibraryArtistDto>? followedArtists)
    {
        if (likedSongs == null || likedSongs.Count == 0)
            return Array.Empty<LikedArtistDto>();

        var followedById = (followedArtists ?? Array.Empty<LibraryArtistDto>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var followedByName = (followedArtists ?? Array.Empty<LibraryArtistDto>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => NormalizeNameKey(a.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, List<LikedSongDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in likedSongs)
        {
            var key = GetGroupingKey(song, followedByName);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<LikedSongDto>(8);
                groups[key] = bucket;
            }
            bucket.Add(song);
        }

        var result = new List<LikedArtistDto>(groups.Count);
        foreach (var (artistKey, bucket) in groups)
        {
            followedById.TryGetValue(artistKey, out var followed);
            if (followed == null)
            {
                var nameKey = NormalizeNameKey(bucket.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ArtistName))?.ArtistName ?? "");
                followedByName.TryGetValue(nameKey, out followed);
            }

            var firstWithName = bucket.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ArtistName));
            var orderedTracks = bucket.OrderBy(s => s.OriginalIndex).ToArray();
            var mostRecent = bucket.Max(s => s.AddedAt);

            result.Add(new LikedArtistDto
            {
                Id = followed?.Id ?? artistKey,
                Name = followed?.Name ?? firstWithName?.ArtistName ?? "",
                ImageUrl = followed?.ImageUrl,
                LikedSongCount = bucket.Count,
                MostRecentLikedAt = ToOffsetRespectingKind(mostRecent),
                LikedSongs = orderedTracks,
                IsAlsoFollowed = followed != null
            });
        }

        return result;
    }

    private static string? GetGroupingKey(
        LikedSongDto song,
        IReadOnlyDictionary<string, LibraryArtistDto> followedByName)
    {
        if (!string.IsNullOrWhiteSpace(song.ArtistId))
            return song.ArtistId;

        if (string.IsNullOrWhiteSpace(song.ArtistName))
            return null;

        var nameKey = NormalizeNameKey(song.ArtistName);
        if (followedByName.TryGetValue(nameKey, out var followed))
            return followed.Id;

        return $"artist:name:{nameKey}";
    }
}
