namespace Wavee.UI.WinUI.ViewModels;

/// <summary>Card VM for the V4A "Playlists and discovery" shelf. <see cref="Subtitle"/>
/// is source-derived in <c>ArtistService.MapPlaylists</c> (e.g. "Spotify · official",
/// "Aria Maelstrom · discovered on").</summary>
public sealed class ArtistPlaylistVm
{
    public required string Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? Subtitle { get; init; }
}
