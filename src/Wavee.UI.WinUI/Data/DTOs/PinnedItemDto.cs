namespace Wavee.UI.WinUI.Data.DTOs;

public enum PinnedItemKind
{
    Playlist,
    Album,
    Artist,
    Show,
    LikedSongs,
    YourEpisodes
}

/// <summary>
/// A single row in the sidebar's "Pinned" section, mapped from a Spotify
/// <c>ylpin</c> collection entry. The Spotify endpoint returns a mixed bag of
/// URI kinds — this DTO is filtered to the four surfaces Wavee currently
/// renders (playlist / album / artist / show).
/// </summary>
public sealed record PinnedItemDto
{
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public string? ImageUrl { get; init; }
    public long AddedAtUnixSeconds { get; init; }
    public required PinnedItemKind Kind { get; init; }
}
