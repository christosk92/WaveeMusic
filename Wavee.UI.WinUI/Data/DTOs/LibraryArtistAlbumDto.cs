namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents an album by an artist in the library artist detail view.
/// </summary>
public sealed record LibraryArtistAlbumDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }
    public string? AlbumType { get; init; } // Album, Single, EP, Compilation
}
