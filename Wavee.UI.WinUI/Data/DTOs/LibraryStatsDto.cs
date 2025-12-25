namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Library statistics for sidebar badges.
/// </summary>
public sealed record LibraryStatsDto
{
    public int AlbumCount { get; init; }
    public int ArtistCount { get; init; }
    public int LikedSongsCount { get; init; }
    public int PlaylistCount { get; init; }
    public int TotalPlayCount { get; init; }
}
