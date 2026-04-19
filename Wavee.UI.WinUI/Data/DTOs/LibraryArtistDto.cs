using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents an artist in the user's library.
/// </summary>
public sealed record LibraryArtistDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public int FollowerCount { get; init; }
    public int AlbumCount { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// True when this is a ghost/placeholder entry awaiting metadata.
    /// </summary>
    public bool IsLoading { get; init; }

    /// <summary>
    /// Formatted follower count (e.g., "1.2M followers")
    /// </summary>
    public string FollowerCountFormatted => FollowerCount switch
    {
        >= 1_000_000 => $"{FollowerCount / 1_000_000.0:0.#}M followers",
        >= 1_000 => $"{FollowerCount / 1_000.0:0.#}K followers",
        _ => $"{FollowerCount:N0} followers"
    };

    /// <summary>
    /// Formatted date added to library (e.g., "Added Mar 15, 2025")
    /// </summary>
    public string AddedAtFormatted => AddedAt == DateTimeOffset.MinValue
        ? ""
        : $"Added {AddedAt.LocalDateTime:MMM d, yyyy}";

    /// <summary>
    /// VM-populated, sort-dependent subtitle. Non-null only when the library is sorted by
    /// "Recents" and this artist has a known last-played timestamp.
    /// </summary>
    public string? RecentsSubtitle { get; set; }
}
