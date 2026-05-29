namespace Wavee.UI.Models;

/// <summary>
/// Represents an album by an artist in the library artist detail view.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LibraryArtistAlbumDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }
    public string? AlbumType { get; init; } // Album, Single, EP, Compilation
    public bool IsSaved { get; init; }

    /// <summary>
    /// True when the user has liked at least one song from this album.
    /// Combined with <see cref="IsSaved"/> by the artist-detail "Saved only"
    /// filter — an album counts as "in library" if it's directly saved OR
    /// the user has liked tracks from it.
    /// </summary>
    public bool ContainsLikedSongs { get; init; }

    /// <summary>True when either path marks the album as part of the user's library.</summary>
    public bool IsInLibrary => IsSaved || ContainsLikedSongs;
}