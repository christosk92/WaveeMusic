using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents an album in the user's library.
/// </summary>
public sealed record LibraryAlbumDto
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
