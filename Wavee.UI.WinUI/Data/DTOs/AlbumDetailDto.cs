namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents detailed album metadata.
/// </summary>
public sealed record AlbumDetailDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public required string ArtistId { get; init; }
    public required string ArtistName { get; init; }
    public int Year { get; init; }
    public string? AlbumType { get; init; }
    public int TrackCount { get; init; }
    public bool IsSaved { get; init; }
}
