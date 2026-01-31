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
}
