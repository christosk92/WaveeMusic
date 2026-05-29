using System;
using System.Collections.Generic;

namespace Wavee.UI.Models;

/// <summary>
/// Represents an album in the user's library.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LibraryAlbumDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArtistName { get; init; }
    public string? ArtistId { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }
    public int TrackCount { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// True when this is a ghost/placeholder entry awaiting metadata.
    /// </summary>
    public bool IsLoading { get; init; }

    /// <summary>
    /// VM-populated, sort-dependent subtitle. Non-null only when the library is sorted by
    /// "Recents" and this album has a known last-played timestamp — typically something like
    /// <c>"Played 3h ago"</c>. Templates show this in place of the artist / added-date line
    /// while it has a value.
    /// </summary>
    public string? RecentsSubtitle { get; set; }
}

/// <summary>
/// Represents an album surfaced by the "From Liked Songs" view in the Library
/// Albums tab: a virtual album-shaped grouping of liked tracks that share the
/// same parent <see cref="LikedSongDto.AlbumId"/>.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LikedAlbumDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArtistName { get; init; }
    public string? ArtistId { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }

    /// <summary>
    /// Total tracks on the album when known; falls back to
    /// <see cref="LikedSongCount"/> when unknown.
    /// </summary>
    public int TrackCount { get; init; }

    /// <summary>How many tracks from this album are in the user's Liked Songs.</summary>
    public int LikedSongCount { get; init; }

    /// <summary>
    /// Most recent <see cref="LikedSongDto.AddedAt"/> across the album's liked
    /// tracks. Used by the "Recently added" sort and detail metadata line.
    /// </summary>
    public DateTimeOffset MostRecentLikedAt { get; init; }

    /// <summary>The liked tracks themselves, in their original liked-songs order.</summary>
    public IReadOnlyList<LikedSongDto> LikedSongs { get; init; } = Array.Empty<LikedSongDto>();

    /// <summary>
    /// True when this album is also in the user's saved-albums set.
    /// </summary>
    public bool IsAlsoSaved { get; init; }

    /// <summary>
    /// VM-populated, sort-dependent subtitle. Non-null only for the Recents sort
    /// when the album has a known last-played timestamp.
    /// </summary>
    public string? RecentsSubtitle { get; set; }

    /// <summary>Pluralized "N liked song(s)" string for the card subtitle.</summary>
    public string LikedSongCountLabel => LikedSongCount == 1
        ? "1 liked song"
        : $"{LikedSongCount} liked songs";

    /// <summary>Composite subtitle: "N liked songs" plus optional saved suffix.</summary>
    public string Subtitle => IsAlsoSaved
        ? $"{LikedSongCountLabel} \u00b7 saved"
        : LikedSongCountLabel;
}
