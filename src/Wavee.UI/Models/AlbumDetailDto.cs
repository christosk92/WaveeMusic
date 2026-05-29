namespace Wavee.UI.Models;

/// <summary>
/// Represents detailed album metadata.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record AlbumDetailDto
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