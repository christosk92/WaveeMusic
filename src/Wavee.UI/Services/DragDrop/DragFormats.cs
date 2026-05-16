using System.Collections.Generic;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Single source of truth for every drag-drop format string in Wavee.
/// Internal custom formats use the <c>application/x-wavee-*</c> namespace so they
/// never collide with anything Windows ships out of the box.
/// </summary>
public static class DragFormats
{
    public const string Tracks      = "application/x-wavee-tracks";
    public const string Album       = "application/x-wavee-album";
    public const string Playlist    = "application/x-wavee-playlist";
    public const string Artist      = "application/x-wavee-artist";
    public const string SidebarItem = "application/x-wavee-sidebar-item";
    public const string LikedSongs  = "application/x-wavee-likedsongs";
    public const string Show        = "application/x-wavee-show";

    // Legacy pipe-joined ids written by the original drag-drop code. Kept for
    // one release so anything that latched onto the old format keeps reading.
    public const string LegacyTrackIds = "WaveeTrackIds";

    public static IReadOnlyList<string> All { get; } =
    [
        Tracks,
        Album,
        Playlist,
        Artist,
        SidebarItem,
        LikedSongs,
        Show,
    ];
}
