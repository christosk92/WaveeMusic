using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Models;
using static Wavee.UI.Services.LikedSongsGroupingKeys;

namespace Wavee.UI.Services;

/// <summary>
/// Groups a flat <see cref="LikedSongDto"/> collection into a list of
/// <see cref="LikedAlbumDto"/> — one entry per distinct
/// <see cref="LikedSongDto.AlbumId"/>. Drives the "From Liked Songs" source on
/// the Library › Albums tab. Pure, no I/O; safe to call on a worker thread.
/// </summary>
public static class LikedSongsByAlbumGrouper
{
    /// <summary>
    /// Groups <paramref name="likedSongs"/> by their <see cref="LikedSongDto.AlbumId"/>.
    /// Tracks with a missing / empty <c>AlbumId</c> (rare — typically local-file
    /// entries) are skipped. For each bucket, takes the most recent <c>AddedAt</c>
    /// as <see cref="LikedAlbumDto.MostRecentLikedAt"/>, and the first non-empty
    /// image / artist / artist-id fields from the bucket. Sets
    /// <see cref="LikedAlbumDto.IsAlsoSaved"/> from <paramref name="savedAlbumIds"/>
    /// (matched case-insensitively on the full URI).
    /// </summary>
    public static IReadOnlyList<LikedAlbumDto> Group(
        IReadOnlyList<LikedSongDto> likedSongs,
        IReadOnlySet<string> savedAlbumIds)
    {
        if (likedSongs == null || likedSongs.Count == 0)
            return Array.Empty<LikedAlbumDto>();

        var savedSet = savedAlbumIds ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, List<LikedSongDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in likedSongs)
        {
            if (string.IsNullOrWhiteSpace(song.AlbumId))
                continue;

            if (!groups.TryGetValue(song.AlbumId, out var bucket))
            {
                bucket = new List<LikedSongDto>(4);
                groups[song.AlbumId] = bucket;
            }
            bucket.Add(song);
        }

        var result = new List<LikedAlbumDto>(groups.Count);
        foreach (var (albumId, bucket) in groups)
        {
            // First non-empty image / artist preserves the album cover even if
            // the metadata-thin row showed up first.
            var firstImage = bucket.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ImageUrl))?.ImageUrl;
            var firstWithArtist = bucket.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ArtistName));
            var firstWithAlbumName = bucket.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.AlbumName));

            var mostRecent = bucket.Max(s => s.AddedAt);

            // Preserve the original liked-songs order within the album bucket so
            // tracks shown in the detail pane match what the user sees on the
            // main Liked Songs list.
            var orderedTracks = bucket
                .OrderBy(s => s.OriginalIndex)
                .ToArray();

            result.Add(new LikedAlbumDto
            {
                Id = albumId,
                Name = firstWithAlbumName?.AlbumName ?? "",
                ArtistName = firstWithArtist?.ArtistName ?? "",
                ArtistId = string.IsNullOrWhiteSpace(firstWithArtist?.ArtistId) ? null : firstWithArtist!.ArtistId,
                ImageUrl = firstImage,
                Year = 0,
                TrackCount = bucket.Count,
                LikedSongCount = bucket.Count,
                MostRecentLikedAt = ToOffsetRespectingKind(mostRecent),
                LikedSongs = orderedTracks,
                IsAlsoSaved = savedSet.Contains(albumId)
            });
        }

        return result;
    }
}
