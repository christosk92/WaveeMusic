namespace Wavee.UI.Models;

/// <summary>
/// Library statistics for sidebar badges.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LibraryStatsDto
{
    public int AlbumCount { get; init; }
    public int ArtistCount { get; init; }
    public int LikedSongsCount { get; init; }
    public int YourEpisodesCount { get; init; }
    public int PodcastCount { get; init; }
    public int PlaylistCount { get; init; }
    public int TotalPlayCount { get; init; }
}